using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>
/// Generates zero-allocation constrained type aliases declared via assembly- or type-level <c>[TypeAlias&lt;T&gt;]</c> attributes.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TypeAliasGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var assemblyAliases = context.CompilationProvider.Select(static (comp, _) =>
            TypeRefHelper.ExtractTypeAliases(
                comp.Assembly.GetAttributes(),
                defaultNamespace: string.Empty,
                fallbackLocation: Location.None));

        var typeAliases = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.TypeAliasAttributeMetadataName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => ExtractFromType(ctx))
            .Collect();

        var combined = assemblyAliases.Combine(typeAliases);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var allSpecs = new List<TypeAliasSpec>(pair.Left);
            foreach (var list in pair.Right)
            {
                if (list is not null)
                {
                    allSpecs.AddRange(list);
                }
            }

            if (allSpecs.Count == 0)
            {
                return;
            }

            var seenFullNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in allSpecs)
            {
                if (!SyntaxFacts.IsValidIdentifier(spec.Name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        GeneratorDescriptors.InvalidTypeAliasName,
                        spec.DiagnosticLocation,
                        spec.Name));
                    continue;
                }

                if (!seenFullNames.Add(spec.FullName))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        GeneratorDescriptors.DuplicateTypeAlias,
                        spec.DiagnosticLocation,
                        spec.FullName));
                    continue;
                }

                RefinedStructEmitter.Emit(
                    spc,
                    spec.Name,
                    spec.Namespace,
                    spec.UnderlyingType,
                    spec.Predicate,
                    spec.IsInternal ? "internal " : "public ",
                    spec.DiagnosticLocation,
                    spec.UsingDirectives,
                    Array.Empty<string>(),
                    emitRefinedAttribute: true,
                    hintSuffix: ".TypeAlias.g.cs");
            }
        });
    }

    private static List<TypeAliasSpec>? ExtractFromType(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var defaultNs = TypeRefHelper.GetNamespace(symbol);
        var fallbackUsings = TypeRefHelper.CollectUsings(context.TargetNode.SyntaxTree);
        return TypeRefHelper.ExtractTypeAliases(
            context.Attributes,
            defaultNs,
            context.TargetNode.GetLocation(),
            fallbackUsings);
    }
}
