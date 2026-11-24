using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>
/// Generates zero-allocation refinement types, JSON converters, and checked/unchecked constructors for <c>[Refined&lt;T&gt;]</c> structs.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RefinedTypeGenerator : IIncrementalGenerator
{
    private sealed record RefinedTarget(
        INamedTypeSymbol Symbol,
        ITypeSymbol UnderlyingType,
        string Predicate,
        Location DiagnosticLocation,
        bool IsValidDeclaration,
        IReadOnlyList<string> UsingDirectives);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.RefinedAttributeMetadataName,
            predicate: static (node, _) =>
                node is TypeDeclarationSyntax &&
                !node.SyntaxTree.FilePath.EndsWith(".TypeAlias.g.cs", StringComparison.OrdinalIgnoreCase),
            transform: static (ctx, _) => Transform(ctx));

        context.RegisterSourceOutput(targets, static (spc, target) =>
        {
            if (target is null)
            {
                return;
            }

            Emit(spc, target);
        });
    }

    private static RefinedTarget? Transform(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var attr = context.Attributes.FirstOrDefault(a =>
            a.AttributeClass is { IsGenericType: true } ac &&
            ac.ConstructedFrom.ToDisplayString() == "Verifiable.Contracts.RefinedAttribute<TUnderlying>");

        if (attr?.AttributeClass is not { TypeArguments.Length: 1 } attrClass)
        {
            return null;
        }

        var underlyingType = attrClass.TypeArguments[0];
        var predicate = attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is string s
            ? s
            : string.Empty;

        var isReadOnlyPartialRecordStruct =
            symbol.IsRecord &&
            symbol.IsValueType &&
            symbol.IsReadOnly &&
            context.TargetNode is RecordDeclarationSyntax rds &&
            rds.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) &&
            rds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
            rds.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));

        var usings = TypeRefHelper.CollectUsings(context.TargetNode.SyntaxTree);
        return new RefinedTarget(
            symbol,
            underlyingType,
            predicate,
            context.TargetNode.GetLocation(),
            isReadOnlyPartialRecordStruct,
            usings);
    }

    private static void Emit(SourceProductionContext spc, RefinedTarget target)
    {
        if (!target.IsValidDeclaration)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.RefinedMustBeReadOnlyPartialRecordStruct,
                target.DiagnosticLocation,
                target.Symbol.Name));
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
            target.Symbol.Name,
            TypeRefHelper.GetNamespace(target.Symbol),
            target.UnderlyingType,
            target.Predicate,
            accessibility,
            target.DiagnosticLocation,
            target.UsingDirectives,
            GetEnclosingPartialDeclarations(target.Symbol),
            emitRefinedAttribute: false,
            hintSuffix: ".Refined.g.cs");
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
