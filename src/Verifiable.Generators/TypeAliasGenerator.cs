using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>
/// Generates zero-allocation constrained types, implicit widening, JSON converters, and checked/unchecked accessors
/// for <c>[TypeAlias&lt;T&gt;]</c> and <c>[Refined&lt;T&gt;]</c> structs declaring <c>public static bool IsValid(T value)</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TypeAliasGenerator : IIncrementalGenerator
{
    private sealed record ConstrainedTarget(
        INamedTypeSymbol Symbol,
        ITypeSymbol UnderlyingType,
        string? Description,
        Location DiagnosticLocation,
        Location ContextLocation,
        bool IsValidDeclaration,
        string? DeclarationIssue,
        IReadOnlyList<string> UsingDirectives,
        string HintSuffix);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var aliasTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.TypeAliasAttributeMetadataName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => Transform(ctx, ".TypeAlias.g.cs"));

        var refinedTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.RefinedAttributeMetadataName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => Transform(ctx, ".Refined.g.cs"));

        context.RegisterSourceOutput(aliasTargets, static (spc, target) =>
        {
            if (target is not null) Emit(spc, target);
        });

        context.RegisterSourceOutput(refinedTargets, static (spc, target) =>
        {
            if (target is not null) Emit(spc, target);
        });
    }

    private static ConstrainedTarget? Transform(GeneratorAttributeSyntaxContext context, string hintSuffix)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        if (!TypeRefHelper.TryGetConstrainedUnderlyingType(symbol, out var underlyingType, out var description) || underlyingType is null)
        {
            return null;
        }

        var isReadOnlyPartialRecordStruct =
            symbol.IsRecord &&
            symbol.IsValueType &&
            symbol.IsReadOnly &&
            context.TargetNode is RecordDeclarationSyntax rds &&
            rds.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) &&
            rds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
            rds.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));

        string? declIssue = null;
        if (!isReadOnlyPartialRecordStruct && context.TargetNode is TypeDeclarationSyntax tds)
        {
            var missing = new List<string>();
            if (!tds.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) missing.Add("`readonly`");
            if (!tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))) missing.Add("`partial`");
            if (tds is not RecordDeclarationSyntax rec || !rec.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
            {
                missing.Add("`record struct`");
            }
            if (missing.Count > 0)
            {
                declIssue = $"It is missing {string.Join(" and ", missing)}.";
            }
        }

        var highlightLoc = context.TargetNode is TypeDeclarationSyntax td
            ? td.Identifier.GetLocation()
            : context.TargetNode.GetLocation();
        var usings = TypeRefHelper.CollectUsings(context.TargetNode.SyntaxTree);
        return new ConstrainedTarget(
            symbol,
            underlyingType,
            description,
            highlightLoc,
            context.TargetNode.GetLocation(),
            isReadOnlyPartialRecordStruct,
            declIssue,
            usings,
            hintSuffix);
    }

    private static void Emit(SourceProductionContext spc, ConstrainedTarget target)
    {
        if (!target.IsValidDeclaration)
        {
            var underlyingName = target.UnderlyingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var intro = target.DeclarationIssue is not null
                ? $"The type `{target.Symbol.Name}` has `[TypeAlias<{underlyingName}>]` / `[Refined<{underlyingName}>]`, so it must be a `readonly partial record struct`. {target.DeclarationIssue}"
                : $"The type `{target.Symbol.Name}` has `[TypeAlias<{underlyingName}>]` / `[Refined<{underlyingName}>]`, so it must be a `readonly partial record struct`.";
            var msg = ElmGeneratorFormatter.Format(
                "INVALID CONSTRAINED TYPE",
                target.DiagnosticLocation,
                intro,
                "Why this rule exists: constrained value types are zero-allocation immutable structs whose generated `.From(...)`, `.TryFrom(...)`, and JSON converters are emitted into a partial declaration.",
                $"Change the declaration to `public readonly partial record struct {target.Symbol.Name} {{ public static bool IsValid({underlyingName} value) => ...; }}`.",
                contextLocation: target.ContextLocation);
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.RefinedMustBeReadOnlyPartialRecordStruct,
                target.DiagnosticLocation,
                msg));
            return;
        }

        if (!TypeRefHelper.TryFindIsValidMethod(
                target.Symbol,
                target.UnderlyingType,
                out _,
                out var bodyExpr,
                out var paramName,
                out var acceptsNullableParam) ||
            bodyExpr is null ||
            paramName is null)
        {
            var underlyingName = target.UnderlyingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var (diagLoc, specificIntro, specificFix) = DiagnoseMissingIsValid(target, underlyingName);
            var msg = ElmGeneratorFormatter.Format(
                "MISSING ISVALID PREDICATE",
                diagLoc,
                specificIntro,
                "Why this rule exists: the generator and Z3 prover share this predicate both to check raw inputs at runtime and to reason about valid values at compile time.",
                specificFix,
                contextLocation: target.ContextLocation);
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.MissingOrInvalidIsValidMethod,
                diagLoc,
                msg));
            return;
        }

        var accessibility = target.Symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Internal => "internal ",
            Accessibility.Private => "private ",
            Accessibility.Protected => "protected ",
            Accessibility.ProtectedOrInternal => "protected internal ",
            Accessibility.ProtectedAndInternal => "private protected ",
            _ => "public "
        };

        RefinedStructEmitter.Emit(
            spc,
            target.Symbol,
            target.UnderlyingType,
            bodyExpr,
            paramName,
            acceptsNullableParam,
            accessibility,
            target.UsingDirectives,
            GetEnclosingPartialDeclarations(target.Symbol),
            target.HintSuffix,
            target.Description);
    }

    private static List<string> GetEnclosingPartialDeclarations(INamedTypeSymbol symbol)
    {
        var list = new List<string>();
        for (var cur = symbol.ContainingType; cur is not null; cur = cur.ContainingType)
        {
            var kind = cur.IsRecord
                ? (cur.IsValueType ? "record struct" : "record class")
                : (cur.IsValueType ? "struct" : "class");
            list.Add($"{kind} {cur.Name}");
        }
        list.Reverse();
        return list;
    }

    private static (Location Loc, string Intro, string Fix) DiagnoseMissingIsValid(ConstrainedTarget target, string underlyingName)
    {
        IMethodSymbol? exactName = null;
        IMethodSymbol? nearMiss = null;
        var bestDist = int.MaxValue;

        foreach (var member in target.Symbol.GetMembers())
        {
            if (member is not IMethodSymbol m || m.MethodKind != MethodKind.Ordinary || m.IsImplicitlyDeclared)
            {
                continue;
            }

            if (m.Name == "IsValid")
            {
                exactName = m;
                break;
            }

            var dist = EditDistance(m.Name, "IsValid");
            if (dist <= 3 && dist < bestDist)
            {
                bestDist = dist;
                nearMiss = m;
            }
        }

        if (exactName is not null)
        {
            var mLoc = exactName.Locations.Length > 0 ? exactName.Locations[0] : target.DiagnosticLocation;
            var problems = new List<string>();
            if (!exactName.IsStatic) problems.Add("is not `static`");
            if (exactName.DeclaredAccessibility != Accessibility.Public) problems.Add("is not `public`");
            if (exactName.ReturnType.SpecialType != SpecialType.System_Boolean)
            {
                problems.Add($"returns `{exactName.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}` instead of `bool`");
            }
            if (exactName.Parameters.Length != 1)
            {
                problems.Add($"takes {exactName.Parameters.Length} parameters instead of 1 (`{underlyingName}`)");
            }
            else if (!SymbolEqualityComparer.Default.Equals(exactName.Parameters[0].Type.WithNullableAnnotation(NullableAnnotation.None), target.UnderlyingType.WithNullableAnnotation(NullableAnnotation.None)))
            {
                var actualParam = exactName.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                problems.Add($"takes `{actualParam}` instead of `{underlyingName}`");
            }

            var syntax = exactName.DeclaringSyntaxReferences.Length > 0
                ? exactName.DeclaringSyntaxReferences[0].GetSyntax() as MethodDeclarationSyntax
                : null;
            if (problems.Count == 0 && syntax is not null && syntax.ExpressionBody is null)
            {
                problems.Add("uses a multi-statement block body instead of a single `=>` expression or single `return`");
            }

            var detail = problems.Count > 0
                ? $"I found `IsValid` on `{target.Symbol.Name}`, but it {string.Join(", and ", problems)}."
                : $"I found `IsValid` on `{target.Symbol.Name}`, but its signature does not match `public static bool IsValid({underlyingName} value) => ...;`.";

            return (
                mLoc,
                detail,
                $"Update the signature to `public static bool IsValid({underlyingName} value) => ...;`.");
        }

        if (nearMiss is not null)
        {
            var mLoc = nearMiss.Locations.Length > 0 ? nearMiss.Locations[0] : target.DiagnosticLocation;
            return (
                mLoc,
                $"I could not find `IsValid({underlyingName} value)` on `{target.Symbol.Name}`, but I found `{nearMiss.Name}`.\n\nDid you mean `IsValid`?",
                $"Rename `{nearMiss.Name}` to `public static bool IsValid({underlyingName} value) => ...;`.");
        }

        return (
            target.DiagnosticLocation,
            $"The constrained type `{target.Symbol.Name}` needs a single-expression `IsValid({underlyingName} value)` method.",
            $"Add `public static bool IsValid({underlyingName} value) => ...;` inside `{target.Symbol.Name}`.");
    }

    private static int EditDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                var del = d[i - 1, j] + 1;
                var ins = d[i, j - 1] + 1;
                var sub = d[i - 1, j - 1] + cost;
                var best = del < ins ? (del < sub ? del : sub) : (ins < sub ? ins : sub);
                if (i > 1 && j > 1 &&
                    char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2]) &&
                    char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    var trans = d[i - 2, j - 2] + cost;
                    if (trans < best) best = trans;
                }
                d[i, j] = best;
            }
        }
        return d[n, m];
    }
}
