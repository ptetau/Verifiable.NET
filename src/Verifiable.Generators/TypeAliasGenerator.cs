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
        Location DiagnosticLocation,
        bool IsValidDeclaration,
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

        if (!TypeRefHelper.TryGetConstrainedUnderlyingType(symbol, out var underlyingType) || underlyingType is null)
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

        var usings = TypeRefHelper.CollectUsings(context.TargetNode.SyntaxTree);
        return new ConstrainedTarget(
            symbol,
            underlyingType,
            context.TargetNode.GetLocation(),
            isReadOnlyPartialRecordStruct,
            usings,
            hintSuffix);
    }

    private static void Emit(SourceProductionContext spc, ConstrainedTarget target)
    {
        if (!target.IsValidDeclaration)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.RefinedMustBeReadOnlyPartialRecordStruct,
                target.DiagnosticLocation,
                target.Symbol.Name));
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
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.MissingOrInvalidIsValidMethod,
                target.DiagnosticLocation,
                target.Symbol.Name,
                target.UnderlyingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
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
            target.HintSuffix);
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
}
