using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Verifiable.Analyzers;

namespace Verifiable.Query;

/// <summary>
/// Rewrites raw Roslyn C# compiler errors (<c>CS*</c>) into Elm-style diagnostics using Roslyn's
/// live <see cref="CSharpCompilation"/> and <see cref="SemanticModel"/>—achieving Elm's type diffing,
/// localized type names, typo ranking, branch-mismatch framing, and cascading-error suppression
/// without writing a C# compiler.
/// </summary>
internal static class ElmCSharpDiagnosticRewriter
{
    internal sealed record RewrittenError(
        string Title,
        Location Location,
        string Message,
        string FixHint,
        int SuppressedCascadeCount);

    public static RewrittenError Rewrite(
        CSharpCompilation compilation,
        IReadOnlyList<Diagnostic> rawErrors)
    {
        var filtered = FilterCascadingErrors(rawErrors);
        var primary = filtered[0];
        var suppressed = Math.Max(0, rawErrors.Count - filtered.Count);

        var loc = primary.Location;
        if (!loc.IsInSource || loc.SourceTree is null)
        {
            return new RewrittenError(
                "SOURCE COMPILATION ERROR",
                loc,
                primary.GetMessage(),
                "Fix the C# compiler error in the target sources before running queries.",
                suppressed);
        }

        var model = compilation.GetSemanticModel(loc.SourceTree);
        var root = loc.SourceTree.GetRoot();
        var node = root.FindNode(loc.SourceSpan, getInnermostNodeForTie: true);
        var pos = loc.SourceSpan.Start;

        var rewritten = primary.Id switch
        {
            "CS0029" or "CS0266" or "CS0664" or "CS1503" => RewriteTypeMismatch(primary, model, node, pos, suppressed),
            "CS0173" => RewriteConditionalBranchMismatch(primary, model, node, pos, suppressed),
            "CS1061" => RewriteMissingMember(primary, model, node, pos, suppressed),
            "CS0103" => RewriteUnknownName(primary, model, node, pos, suppressed),
            "CS0246" => RewriteUnknownType(primary, model, node, pos, suppressed),
            _ => RewriteGeneric(primary, suppressed)
        };

        return rewritten;
    }

    /// <summary>
    /// Elm-style cascading error suppression without a custom compiler:
    /// if a syntax error occurred in a file, hide downstream semantic errors in that file;
    /// otherwise keep root semantic errors and collapse duplicate/error-type cascades.
    /// </summary>
    internal static IReadOnlyList<Diagnostic> FilterCascadingErrors(IReadOnlyList<Diagnostic> rawErrors)
    {
        if (rawErrors.Count <= 1) return rawErrors;

        var syntaxTreesWithSyntaxErrors = new HashSet<SyntaxTree>();
        foreach (var d in rawErrors)
        {
            if (IsSyntaxErrorId(d.Id) && d.Location.SourceTree is { } tree)
            {
                syntaxTreesWithSyntaxErrors.Add(tree);
            }
        }

        var kept = new List<Diagnostic>(rawErrors.Count);
        var seenRootLines = new HashSet<(SyntaxTree?, int)>();

        foreach (var d in rawErrors)
        {
            var tree = d.Location.SourceTree;
            if (tree is not null && syntaxTreesWithSyntaxErrors.Contains(tree) && !IsSyntaxErrorId(d.Id))
            {
                continue;
            }

            var line = d.Location.IsInSource ? d.Location.GetLineSpan().StartLinePosition.Line : -1;
            if (!seenRootLines.Add((tree, line)))
            {
                continue;
            }

            kept.Add(d);
        }

        return kept.Count > 0 ? kept : new[] { rawErrors[0] };
    }

    private static bool IsSyntaxErrorId(string id) =>
        id is "CS1001" or "CS1002" or "CS1003" or "CS1026" or "CS1513" or "CS1514" or "CS1519" or "CS1525";

    private static RewrittenError RewriteTypeMismatch(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var expr = node.FirstAncestorOrSelf<ExpressionSyntax>();
        var typeInfo = expr is not null ? model.GetTypeInfo(expr) : default;
        var actual = typeInfo.Type;
        var expected = typeInfo.ConvertedType;

        if (actual is null || expected is null ||
            actual.Kind == SymbolKind.ErrorType || expected.Kind == SymbolKind.ErrorType ||
            SymbolEqualityComparer.Default.Equals(actual, expected))
        {
            return RewriteGeneric(diag, suppressed);
        }

        // Elm's Reporting.Render.Type.Localizer equivalent: Roslyn's ToMinimalDisplayString
        var actualDisplay = actual.ToMinimalDisplayString(model, pos);
        var expectedDisplay = expected.ToMinimalDisplayString(model, pos);

        var sb = new StringBuilder();
        sb.AppendLine($"This expression has type `{actualDisplay}`, but the surrounding context expects `{expectedDisplay}`.");

        var diffDetail = DiffTypes(actual, expected, model, pos);
        if (!string.IsNullOrWhiteSpace(diffDetail))
        {
            sb.AppendLine();
            sb.Append(diffDetail);
        }

        var hint = BuildTypeMismatchHint(actual, expected, expr, actualDisplay, expectedDisplay);
        return new RewrittenError("TYPE MISMATCH", diag.Location, AppendCascadeNote(sb.ToString().TrimEnd(), suppressed), hint, suppressed);
    }

    private static RewrittenError RewriteConditionalBranchMismatch(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var cond = node.FirstAncestorOrSelf<ConditionalExpressionSyntax>();
        if (cond is not null)
        {
            var t1 = model.GetTypeInfo(cond.WhenTrue).Type;
            var t2 = model.GetTypeInfo(cond.WhenFalse).Type;
            if (t1 is not null && t2 is not null)
            {
                var d1 = t1.ToMinimalDisplayString(model, pos);
                var d2 = t2.ToMinimalDisplayString(model, pos);
                var msg =
                    $"The two branches of this `?:` expression produce different types:\n\n" +
                    $"    The `then` branch is: `{d1}`\n" +
                    $"    The `else` branch is: `{d2}`";
                var hint = BuildTypeMismatchHint(t1, t2, cond.WhenFalse, d1, d2);
                return new RewrittenError("BRANCH TYPE MISMATCH", diag.Location, AppendCascadeNote(msg, suppressed), hint, suppressed);
            }
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteMissingMember(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var memberAccess = node.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
        if (memberAccess is not null)
        {
            var badName = memberAccess.Name.Identifier.ValueText;
            var recvType = model.GetTypeInfo(memberAccess.Expression).Type;
            if (recvType is not null && recvType.Kind != SymbolKind.ErrorType)
            {
                var typeDisplay = recvType.ToMinimalDisplayString(model, pos);
                var candidates = CollectPublicMembers(recvType);
                var ranked = ElmSuggest.Rank(badName, candidates, maxResults: 3);
                var didYouMean = ElmSuggest.FormatDidYouMean(ranked);

                var msg = $"I could not find a member named `{badName}` on `{typeDisplay}`.";
                var fix = didYouMean is not null
                    ? $"{didYouMean} Check the member name on `{typeDisplay}`."
                    : $"Check the available members on `{typeDisplay}`.";
                return new RewrittenError("MISSING MEMBER", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
            }
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteUnknownName(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        if (node is IdentifierNameSyntax ident)
        {
            var badName = ident.Identifier.ValueText;
            var inScope = model.LookupSymbols(pos)
                .Where(s => !s.IsImplicitlyDeclared && s.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.Property or SymbolKind.Field or SymbolKind.Method or SymbolKind.NamedType)
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal);
            var ranked = ElmSuggest.Rank(badName, inScope, maxResults: 3);
            var didYouMean = ElmSuggest.FormatDidYouMean(ranked);

            var msg = $"I cannot find a variable, parameter, or symbol named `{badName}` in this scope.";
            var fix = didYouMean is not null
                ? $"{didYouMean} Otherwise declare `{badName}` or add the missing `using` directive."
                : $"Declare `{badName}` in scope or import the namespace containing it.";
            return new RewrittenError("NAMING ERROR", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteUnknownType(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var badName = (node as IdentifierNameSyntax)?.Identifier.ValueText ?? node.ToString().Trim();
        var visibleTypes = model.LookupNamespacesAndTypes(pos)
            .OfType<INamedTypeSymbol>()
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal);
        var ranked = ElmSuggest.Rank(badName, visibleTypes, maxResults: 3);
        var didYouMean = ElmSuggest.FormatDidYouMean(ranked);

        var msg = $"I cannot find a type named `{badName}` in scope.";
        var fix = didYouMean is not null
            ? $"{didYouMean} If it lives in another namespace, add a `using` directive."
            : $"Check the spelling of `{badName}` or add the `using` directive for its namespace.";
        return new RewrittenError("UNKNOWN TYPE", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
    }

    private static RewrittenError RewriteGeneric(Diagnostic diag, int suppressed)
    {
        var title = IsSyntaxErrorId(diag.Id) ? "SYNTAX ERROR" : $"C# COMPILER ERROR ({diag.Id})";
        var msg = AppendCascadeNote(diag.GetMessage(), suppressed);
        var fix = IsSyntaxErrorId(diag.Id)
            ? "Fix the syntax error at the highlighted location and re-run."
            : "Fix the C# compiler error in the target sources before running queries.";
        return new RewrittenError(title, diag.Location, msg, fix, suppressed);
    }

    /// <summary>
    /// Structural type diffing (modeled on Elm's <c>Type.Error.toDiff</c>): compares generic type arguments
    /// and tuple elements so only the mismatching inner part stands out.
    /// </summary>
    private static string? DiffTypes(ITypeSymbol actual, ITypeSymbol expected, SemanticModel model, int pos)
    {
        if (actual is INamedTypeSymbol aNamed && expected is INamedTypeSymbol eNamed)
        {
            if (aNamed.IsGenericType && eNamed.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(aNamed.OriginalDefinition, eNamed.OriginalDefinition) &&
                aNamed.TypeArguments.Length == eNamed.TypeArguments.Length)
            {
                var diffs = new List<string>();
                for (var i = 0; i < aNamed.TypeArguments.Length; i++)
                {
                    var ta = aNamed.TypeArguments[i];
                    var te = eNamed.TypeArguments[i];
                    if (!SymbolEqualityComparer.Default.Equals(ta, te))
                    {
                        var ordinal = i == 0 ? "1st" : i == 1 ? "2nd" : i == 2 ? "3rd" : $"{i + 1}th";
                        diffs.Add(
                            $"Type argument mismatch in `{aNamed.Name}<...>` ({ordinal} type argument):\n" +
                            $"    Actual:   `{ta.ToMinimalDisplayString(model, pos)}`\n" +
                            $"    Expected: `{te.ToMinimalDisplayString(model, pos)}`");
                    }
                }
                if (diffs.Count > 0) return string.Join("\n", diffs);
            }

            if (aNamed.IsTupleType && eNamed.IsTupleType &&
                aNamed.TupleElements.Length == eNamed.TupleElements.Length)
            {
                var diffs = new List<string>();
                for (var i = 0; i < aNamed.TupleElements.Length; i++)
                {
                    var ta = aNamed.TupleElements[i].Type;
                    var te = eNamed.TupleElements[i].Type;
                    if (!SymbolEqualityComparer.Default.Equals(ta, te))
                    {
                        diffs.Add(
                            $"Tuple item #{i + 1} differs:\n" +
                            $"    Actual:   `{ta.ToMinimalDisplayString(model, pos)}`\n" +
                            $"    Expected: `{te.ToMinimalDisplayString(model, pos)}`");
                    }
                }
                if (diffs.Count > 0) return string.Join("\n", diffs);
            }
        }

        return null;
    }

    private static string BuildTypeMismatchHint(
        ITypeSymbol actual,
        ITypeSymbol expected,
        ExpressionSyntax? expr,
        string actualDisplay,
        string expectedDisplay)
    {
        if (actual.SpecialType == SpecialType.System_Double && expected.SpecialType == SpecialType.System_Decimal)
        {
            if (expr is LiteralExpressionSyntax lit)
            {
                return $"C# treats `{lit.Token.Text}` as `double` by default. Add an `m` suffix (`{lit.Token.Text}m`) for exact `decimal` arithmetic.";
            }
            return "C# does not implicitly convert `double` to `decimal`. Use a `decimal` literal (`0.5m`) or convert explicitly.";
        }

        if (actual.SpecialType == SpecialType.System_Decimal && expected.SpecialType == SpecialType.System_Double)
        {
            return "Use `decimal` consistently for exact arithmetic, or cast explicitly to `double` if floating-point is intended.";
        }

        if (actual.SpecialType == SpecialType.System_Int32 && expected.SpecialType == SpecialType.System_String)
        {
            return "Convert the integer explicitly (for example `.ToString(CultureInfo.InvariantCulture)` or string interpolation).";
        }

        if (actual.SpecialType == SpecialType.System_String &&
            expected.SpecialType is SpecialType.System_Int32 or SpecialType.System_Decimal)
        {
            return $"Parse the `string` into `{expectedDisplay}` at an `[InboundPort]` boundary (or with `{expectedDisplay}.TryParse(...)`).";
        }

        return $"Change this expression to produce `{expectedDisplay}`, or update the target type from `{expectedDisplay}` to `{actualDisplay}`.";
    }

    private static IEnumerable<string> CollectPublicMembers(ITypeSymbol type)
    {
        for (var cur = type; cur is not null; cur = cur.BaseType)
        {
            foreach (var m in cur.GetMembers())
            {
                if (m.IsImplicitlyDeclared || m.DeclaredAccessibility != Accessibility.Public) continue;
                if (m is IPropertySymbol { IsIndexer: false } p && p.Name != "EqualityContract") yield return p.Name;
                else if (m is IFieldSymbol f) yield return f.Name;
                else if (m is IMethodSymbol { MethodKind: MethodKind.Ordinary } meth) yield return meth.Name;
            }
        }
    }

    private static string AppendCascadeNote(string message, int suppressed) =>
        suppressed > 0
            ? $"{message}\n\nNote: {suppressed} follow-up compiler {(suppressed == 1 ? "error was" : "errors were")} hidden to avoid cascading noise—fix this one first."
            : message;
}
