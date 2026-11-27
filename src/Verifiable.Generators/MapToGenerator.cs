using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>Generates scalar, name-based mappings to source-defined positional records.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class MapToGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mappings = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Verifiable.Contracts.MapToAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => (Source: (INamedTypeSymbol)ctx.TargetSymbol, Attribute: ctx.Attributes[0]));
        context.RegisterSourceOutput(mappings, static (spc, mapping) => Generate(spc, mapping.Source, mapping.Attribute));
    }

    private static void Generate(SourceProductionContext context, INamedTypeSymbol source, AttributeData attribute)
    {
        var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Location.None;
        void Reject(string reason) => context.ReportDiagnostic(Diagnostic.Create(
            GeneratorDescriptors.InvalidMapping, location, ElmGeneratorFormatter.Format(
                $"INVALID DTO MAPPING: {reason}", location, $"Cannot generate the requested mapping from '{source}'.",
                "Why this rule exists: Generated mappings must copy every stored target value without invoking user code.",
                "Use a source DTO with readable scalar storage matching each positional record parameter by name and type.")));

        if (!IsSimpleType(source) || source.IsStatic ||
            source.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && !IsScalar(f.Type)))
        {
            Reject($"'{source}' must be a top-level, non-generic DTO without inherited or non-scalar storage.");
            return;
        }

        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol target ||
            !target.IsRecord || !IsSimpleType(target) || target.IsAbstract ||
            !SymbolEqualityComparer.Default.Equals(source.ContainingAssembly, target.ContainingAssembly))
        {
            Reject("The target must be a concrete, source-defined top-level positional record or record struct.");
            return;
        }

        var declarations = target.DeclaringSyntaxReferences.Select(r => r.GetSyntax(context.CancellationToken))
            .OfType<RecordDeclarationSyntax>().ToArray();
        var positional = declarations.Where(d => d.ParameterList != null).ToArray();
        if (positional.Length != 1 || declarations.SelectMany(d => d.Members).Any(m => !IsSafeTargetMember(m)) ||
            target.GetMembers().OfType<IFieldSymbol>().Any(f => f.AssociatedSymbol is not IPropertySymbol p ||
                !p.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ParameterSyntax)) ||
            target.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute"))
        {
            Reject($"'{target}' must store only positional members, without custom construction, initializers or layout.");
            return;
        }

        var method = "To" + target.Name;
        var ns = source.ContainingNamespace;
        if (HasInstanceMember(source, method) || ns.GetTypeMembers().Where(t => t.MightContainExtensionMethods)
            .SelectMany(t => t.GetMembers(method)).OfType<IMethodSymbol>()
            .Any(m => m.IsExtensionMethod && m.Parameters.Skip(1).All(p => p.IsOptional || p.IsParams) &&
                SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, source)))
        {
            Reject($"A member already conflicts with the generated extension '{method}()'.");
            return;
        }

        var args = new List<string>();
        foreach (var parameter in positional[0].ParameterList!.Parameters)
        {
            var name = parameter.Identifier.ValueText;
            // The compiler-created positional property points to its parameter, unlike an explicitly
            // implemented property (which could ignore or change the constructor argument).
            var property = target.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault(p =>
                p.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == parameter.SyntaxTree && r.Span == parameter.Span));
            if (property is null || property.IsVirtual || property.IsOverride || !IsScalar(property.Type))
            {
                Reject($"Target member '{name}' must be an unmodified scalar positional property.");
                return;
            }

            var member = source.GetMembers(name).FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol);
            var type = StoredReadType(member);
            if (type is null || !SymbolEqualityComparer.Default.Equals(type, property.Type) ||
                (type.IsReferenceType && property.NullableAnnotation == NullableAnnotation.NotAnnotated &&
                    (type.NullableAnnotation != NullableAnnotation.NotAnnotated || MayReturnNull(member!))))
            {
                Reject($"Target member '{name}' requires accessible, non-virtual source storage of compatible type '{property.Type}'.");
                return;
            }

            args.Add("source.@" + name);
        }

        var className = source.Name + "MapToExtensions";
        while (ns.GetMembers(className).Any() || className == method)
            className += "_";
        var accessibility = source.DeclaredAccessibility == Accessibility.Public && target.DeclaredAccessibility == Accessibility.Public
            ? "public" : "internal";
        var sourceType = TypeRefHelper.ToGlobalTypeName(source);
        var targetType = TypeRefHelper.ToGlobalTypeName(target);
        var namespaceLine = ns.IsGlobalNamespace ? "" : $"namespace {ns.ToDisplayString()};\n";
        var code = $$"""
            // <auto-generated/>
            #nullable enable
            {{namespaceLine}}
            {{accessibility}} static class {{className}}
            {
                [global::Verifiable.Contracts.VerifiableAttribute]
                public static {{targetType}} {{method}}(this {{sourceType}} source) => new {{targetType}}({{string.Join(", ", args)}});
            }
            """;
        // Hint names must also be distinct under case-insensitive comparison.
        var key = BitConverter.ToString(Encoding.UTF8.GetBytes(sourceType)).Replace("-", "");
        context.AddSource($"MapTo.{key}.g.cs", code);
    }

    private static bool IsSimpleType(INamedTypeSymbol type) =>
        type.TypeKind is TypeKind.Class or TypeKind.Struct && type.ContainingType is null &&
        !type.IsFileLocal && !type.IsGenericType && !type.IsRefLikeType &&
        type.BaseType?.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;

    // Methods and computed properties do not add state and are never invoked during creation.
    private static bool IsSafeTargetMember(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax => true,
        PropertyDeclarationSyntax p => p.ExpressionBody != null ||
            (p.Initializer is null && p.AccessorList?.Accessors.All(a => a.Body != null || a.ExpressionBody != null) == true),
        _ => false
    };

    private static bool HasInstanceMember(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            if (current.GetMembers(name).Any(m => !m.IsStatic)) return true;
        return false;
    }

    private static ITypeSymbol? StoredReadType(ISymbol? member)
    {
        if (member is null || member.IsStatic || !Accessible(member)) return null;
        if (member is IFieldSymbol { IsVolatile: false, RefKind: RefKind.None } field) return field.Type;
        if (member is not IPropertySymbol { IsVirtual: false, IsOverride: false, IsAbstract: false, IsIndexer: false,
                RefKind: RefKind.None, GetMethod: { } getter } property || !Accessible(getter))
            return null;
        // A partial property's definition alone looks like auto-accessors, even when its
        // implementation has a user-written getter. Do not treat that as a storage read.
        return !getter.IsExtern && property.DeclaringSyntaxReferences.Any(r => r.GetSyntax() switch
        {
            ParameterSyntax => true,
            PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } list } p =>
                !p.Modifiers.Any(SyntaxKind.PartialKeyword) && list.Accessors.All(a => a.Body is null && a.ExpressionBody is null),
            _ => false
        }) ? property.Type : null;
    }

    private static bool MayReturnNull(ISymbol member) => member.GetAttributes()
        .Concat(member is IPropertySymbol p ? p.GetMethod!.GetReturnTypeAttributes() : [])
        .Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.MaybeNullAttribute");

    private static bool Accessible(ISymbol member) => member.DeclaredAccessibility is
        Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    private static bool IsScalar(ITypeSymbol type) =>
        type.TypeKind == TypeKind.Enum || type.SpecialType is
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Char or SpecialType.System_String or
            SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single ||
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable && IsScalar(nullable.TypeArguments[0]);
}
