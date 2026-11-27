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
/// localized type names, missing-import discovery, arity mismatch hints, typo ranking, branch-mismatch
/// framing, and cascading-error suppression without writing a C# compiler.
/// </summary>
internal static class ElmCSharpDiagnosticRewriter
{
    internal sealed record RewrittenError(
        string DiagnosticId,
        string Title,
        Location Location,
        string Message,
        string FixHint,
        int SuppressedCascadeCount);

    public static RewrittenError Rewrite(
        CSharpCompilation compilation,
        IReadOnlyList<Diagnostic> rawErrors)
    {
        var all = RewriteAll(compilation, rawErrors, maxErrors: 1);
        return all[0];
    }

    public static IReadOnlyList<RewrittenError> RewriteAll(
        CSharpCompilation compilation,
        IReadOnlyList<Diagnostic> rawErrors,
        int maxErrors = 5)
    {
        var filtered = FilterCascadingErrors(rawErrors);
        var suppressed = Math.Max(0, rawErrors.Count - filtered.Count);
        var limit = Math.Max(1, Math.Min(maxErrors, filtered.Count));
        var results = new List<RewrittenError>(limit);

        for (var i = 0; i < limit; i++)
        {
            var primary = filtered[i];
            var cascadeCountForEntry = i == limit - 1 ? suppressed : 0;
            results.Add(RewriteSingle(compilation, primary, cascadeCountForEntry));
        }

        return results;
    }

    private static RewrittenError RewriteSingle(
        CSharpCompilation compilation,
        Diagnostic primary,
        int suppressed)
    {
        var loc = primary.Location;
        if (!loc.IsInSource || loc.SourceTree is null)
        {
            return new RewrittenError(
                primary.Id,
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

        return primary.Id switch
        {
            "CS0029" or "CS0266" or "CS0664" or "CS1503" => RewriteTypeMismatch(primary, model, node, pos, suppressed),
            "CS0173" => RewriteConditionalBranchMismatch(primary, model, node, pos, suppressed),
            "CS8506" => RewriteSwitchArmMismatch(primary, model, node, pos, suppressed),
            "CS0019" => RewriteOperatorMismatch(primary, model, node, pos, suppressed),
            "CS1501" or "CS7036" => RewriteArityMismatch(primary, model, node, pos, suppressed),
            "CS0161" => RewriteMissingReturn(primary, model, node, pos, suppressed),
            "CS1061" => RewriteMissingMember(primary, model, node, pos, suppressed),
            "CS0103" => RewriteUnknownName(primary, compilation, model, node, pos, suppressed),
            "CS0246" => RewriteUnknownType(primary, compilation, model, node, pos, suppressed),
            _ => RewriteGeneric(primary, suppressed)
        };
    }

    /// <summary>
    /// Elm-style cascading error suppression without a custom compiler:
    /// if a syntax error occurred in a file, hide downstream semantic errors in that file;
    /// otherwise keep root semantic errors and collapse duplicate per-line cascades.
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
        return new RewrittenError(diag.Id, "TYPE MISMATCH", diag.Location, AppendCascadeNote(sb.ToString().TrimEnd(), suppressed), hint, suppressed);
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
                return new RewrittenError(diag.Id, "BRANCH TYPE MISMATCH", diag.Location, AppendCascadeNote(msg, suppressed), hint, suppressed);
            }
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteSwitchArmMismatch(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var sw = node.FirstAncestorOrSelf<SwitchExpressionSyntax>();
        if (sw is not null && sw.Arms.Count >= 2)
        {
            var armTypes = new List<string>();
            for (var i = 0; i < sw.Arms.Count && i < 4; i++)
            {
                var t = model.GetTypeInfo(sw.Arms[i].Expression).Type;
                var display = t is not null && t.Kind != SymbolKind.ErrorType
                    ? t.ToMinimalDisplayString(model, pos)
                    : "<unknown>";
                armTypes.Add($"    Arm {i + 1}: `{display}`");
            }

            var msg =
                $"The arms of this `switch` expression do not produce a single common type:\n\n" +
                string.Join("\n", armTypes);
            var fix = "Make every `switch` arm return the same type (or wrap differing outcomes in a common record / `Result<T, TError>`).";
            return new RewrittenError(diag.Id, "BRANCH TYPE MISMATCH", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteOperatorMismatch(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var bin = node.FirstAncestorOrSelf<BinaryExpressionSyntax>();
        if (bin is not null)
        {
            var leftType = model.GetTypeInfo(bin.Left).Type;
            var rightType = model.GetTypeInfo(bin.Right).Type;
            if (leftType is not null && rightType is not null &&
                leftType.Kind != SymbolKind.ErrorType && rightType.Kind != SymbolKind.ErrorType)
            {
                var opText = bin.OperatorToken.Text;
                var lDisp = leftType.ToMinimalDisplayString(model, pos);
                var rDisp = rightType.ToMinimalDisplayString(model, pos);
                var msg =
                    $"The `{opText}` operator cannot be applied to `{lDisp}` and `{rDisp}`:\n\n" +
                    $"    Left  (`{bin.Left}`):  `{lDisp}`\n" +
                    $"    Right (`{bin.Right}`): `{rDisp}`";

                string fix;
                if ((leftType.SpecialType == SpecialType.System_Decimal && rightType.SpecialType == SpecialType.System_Double) ||
                    (leftType.SpecialType == SpecialType.System_Double && rightType.SpecialType == SpecialType.System_Decimal))
                {
                    fix = "C# does not mix `decimal` and `double`. Use `decimal` literals with an `m` suffix (for example `0.5m`) on both sides.";
                }
                else if (opText is "&&" or "||")
                {
                    fix = $"Both sides of `{opText}` must be `bool`. Compare values explicitly (for example `x != 0` or `s is not null`).";
                }
                else
                {
                    fix = $"Convert both operands to compatible types before applying `{opText}`.";
                }

                return new RewrittenError(diag.Id, "OPERATOR MISMATCH", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
            }
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteArityMismatch(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var inv = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (inv is not null)
        {
            var givenCount = inv.ArgumentList.Arguments.Count;
            var memberGroup = model.GetMemberGroup(inv.Expression).OfType<IMethodSymbol>().ToList();
            if (memberGroup.Count > 0)
            {
                var methodName = memberGroup[0].Name;
                var signatures = memberGroup
                    .Take(3)
                    .Select(m => FormatMethodSignature(m, model, pos))
                    .ToList();

                var expectedCounts = memberGroup.Select(m => m.Parameters.Length).Distinct().OrderBy(n => n).ToList();
                var title = expectedCounts.Count == 1 && givenCount > expectedCounts[0]
                    ? "TOO MANY ARGS"
                    : "MISSING ARGS";

                var countSummary = expectedCounts.Count == 1
                    ? $"`{methodName}` expects {FormatArgCount(expectedCounts[0])}, but it got {givenCount} instead."
                    : $"`{methodName}` has no overload taking {FormatArgCount(givenCount)}.";

                var msg =
                    $"{countSummary}\n\n" +
                    (signatures.Count == 1 ? "Expected signature:\n" : "Available overloads:\n") +
                    string.Join("\n", signatures.Select(s => $"    {s}"));

                var fix = "Pass arguments matching the expected parameter list above (check for missing commas, extra arguments, or parameter order).";
                return new RewrittenError(diag.Id, title, diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
            }
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteMissingReturn(
        Diagnostic diag,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (methodDecl is not null && model.GetDeclaredSymbol(methodDecl) is IMethodSymbol method)
        {
            var retType = method.ReturnType.ToMinimalDisplayString(model, pos);
            var msg = $"Not all execution paths in `{method.Name}` return a `{retType}` value.";
            var fix = $"Add a `return ...;` statement for every branch (or end the method with a fallback `return` producing `{retType}`).";
            return new RewrittenError(diag.Id, "MISSING RETURN", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
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
                return new RewrittenError(diag.Id, "MISSING MEMBER", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
            }
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteUnknownName(
        Diagnostic diag,
        CSharpCompilation compilation,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var simpleName = node as SimpleNameSyntax ?? node.FirstAncestorOrSelf<SimpleNameSyntax>();
        if (simpleName is not null)
        {
            var badName = simpleName.Identifier.ValueText;
            var expectedArity = simpleName is GenericNameSyntax generic
                ? generic.TypeArgumentList.Arguments.Count
                : (int?)null;

            // First check if this is a real type name from a missing `using` directive
            // (e.g., `ImmutableList.Create(...)` or `ImmutableList<int>.Empty`)
            var missingNamespaces = FindNamespacesForTypeName(compilation, badName, maxResults: 3, expectedArity);
            if (missingNamespaces.Count > 0)
            {
                var nsList = string.Join(" or ", missingNamespaces.Select(n => $"`using {n};`"));
                var displayBadName = simpleName is GenericNameSyntax ? simpleName.ToString() : badName;
                var nsMsg = $"I cannot find `{displayBadName}` in scope, but it exists in `{string.Join("`, `", missingNamespaces)}`.";
                return new RewrittenError(
                    diag.Id,
                    "MISSING IMPORT",
                    diag.Location,
                    AppendCascadeNote(nsMsg, suppressed),
                    $"Add {nsList} at the top of this file.",
                    suppressed);
            }

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
            return new RewrittenError(diag.Id, "NAMING ERROR", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
        }

        return RewriteGeneric(diag, suppressed);
    }

    private static RewrittenError RewriteUnknownType(
        Diagnostic diag,
        CSharpCompilation compilation,
        SemanticModel model,
        SyntaxNode node,
        int pos,
        int suppressed)
    {
        var badName = node switch
        {
            GenericNameSyntax g => g.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => node.ToString().Trim()
        };
        var expectedArity = node is GenericNameSyntax generic
            ? generic.TypeArgumentList.Arguments.Count
            : (int?)null;

        var missingNamespaces = FindNamespacesForTypeName(compilation, badName, maxResults: 3, expectedArity);
        if (missingNamespaces.Count > 0)
        {
            var nsList = string.Join(" or ", missingNamespaces.Select(n => $"`using {n};`"));
            var nsMsg = $"I cannot find type `{badName}` in scope, because its namespace is not imported (`{string.Join("`, `", missingNamespaces)}`).";
            return new RewrittenError(
                diag.Id,
                "MISSING IMPORT",
                diag.Location,
                AppendCascadeNote(nsMsg, suppressed),
                $"Add {nsList} at the top of this file.",
                suppressed);
        }

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
        return new RewrittenError(diag.Id, "UNKNOWN TYPE", diag.Location, AppendCascadeNote(msg, suppressed), fix, suppressed);
    }

    private static RewrittenError RewriteGeneric(Diagnostic diag, int suppressed)
    {
        var title = IsSyntaxErrorId(diag.Id) ? "SYNTAX ERROR" : $"C# COMPILER ERROR ({diag.Id})";
        var msg = AppendCascadeNote(diag.GetMessage(), suppressed);
        var fix = IsSyntaxErrorId(diag.Id)
            ? "Fix the syntax error at the highlighted location and re-run."
            : "Fix the C# compiler error in the target sources before running queries.";
        return new RewrittenError(diag.Id, title, diag.Location, msg, fix, suppressed);
    }

    /// <summary>
    /// Searches referenced assemblies and source namespaces via Roslyn's <see cref="Compilation.GlobalNamespace"/>
    /// to discover missing <c>using</c> directives without writing a compiler.
    /// </summary>
    private static List<string> FindNamespacesForTypeName(
        CSharpCompilation compilation,
        string typeName,
        int maxResults,
        int? expectedArity = null)
    {
        var exactMatches = new List<string>();
        var exactSeen = new HashSet<string>(StringComparer.Ordinal);
        var fallbackMatches = new List<string>();
        var fallbackSeen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(INamespaceSymbol Ns, int Depth)>();
        queue.Enqueue((compilation.GlobalNamespace, 0));

        var visited = 0;
        const int maxVisitedNamespaces = 1200;

        while (queue.Count > 0 && exactMatches.Count < maxResults && visited++ < maxVisitedNamespaces)
        {
            var (ns, depth) = queue.Dequeue();
            if (!ns.IsGlobalNamespace)
            {
                foreach (var t in ns.GetTypeMembers(typeName))
                {
                    if (t.DeclaredAccessibility == Accessibility.Public)
                    {
                        var nsName = ns.ToDisplayString();
                        if (expectedArity is null || t.Arity == expectedArity.Value)
                        {
                            if (exactSeen.Add(nsName))
                            {
                                exactMatches.Add(nsName);
                                if (exactMatches.Count >= maxResults) break;
                            }
                        }
                        else if (fallbackMatches.Count < maxResults && fallbackSeen.Add(nsName))
                        {
                            fallbackMatches.Add(nsName);
                        }
                    }
                }
            }

            if (depth < 5)
            {
                foreach (var child in ns.GetNamespaceMembers())
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        var found = exactMatches.Count > 0 ? exactMatches : fallbackMatches;

        // Prefer System.* / Verifiable.* namespaces first for concise recommendations
        return found
            .OrderBy(n => n.StartsWith("System.", StringComparison.Ordinal) || n == "System" ? 0
                : n.StartsWith("Verifiable.", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(n => n.Length)
            .ToList();
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
        // Task<T> / ValueTask<T> passed where T is expected -> "Did you forget await?"
        if (actual is INamedTypeSymbol aTask && aTask.IsGenericType && aTask.TypeArguments.Length == 1)
        {
            var def = aTask.OriginalDefinition.ToDisplayString();
            if ((def == "System.Threading.Tasks.Task<TResult>" || def == "System.Threading.Tasks.ValueTask<TResult>") &&
                SymbolEqualityComparer.Default.Equals(aTask.TypeArguments[0], expected))
            {
                return $"Did you forget `await`? This expression returns an asynchronous `{actualDisplay}` instead of `{expectedDisplay}` (await it in an outer shell before passing `{expectedDisplay}` into `[Verifiable]` code).";
            }
        }

        // Nullable<T> passed where T is expected -> Elm's AnythingFromMaybe equivalent
        if (actual is INamedTypeSymbol aNull &&
            aNull.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            aNull.TypeArguments.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(aNull.TypeArguments[0], expected))
        {
            return $"`{actualDisplay}` can be `null`, whereas `{expectedDisplay}` cannot. Pattern-match with `is {{ }} value`, check `.HasValue`, or supply a default with `??`.";
        }

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

    private static string FormatMethodSignature(IMethodSymbol m, SemanticModel model, int pos)
    {
        var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToMinimalDisplayString(model, pos)} {p.Name}"));
        return $"{m.Name}({ps}) -> {m.ReturnType.ToMinimalDisplayString(model, pos)}";
    }

    private static string FormatArgCount(int count) =>
        count == 1 ? "1 argument" : $"{count} arguments";

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
