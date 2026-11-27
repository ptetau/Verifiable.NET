using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

internal static class InitializerProjection
{
    // The SMT constructor describes stored values, not executable constructors,
    // property bodies, or reference aliases. Check the original (pre-CFG) tree.
    public static bool IsSupported(IObjectCreationOperation creation, bool allowBoundaryDto)
    {
        if (creation.Initializer is not { } initializer || creation.Constructor is not { } ctor ||
            ctor.ContainingType is not { } type || type.DeclaringSyntaxReferences.IsEmpty ||
            type.BaseType?.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType) ||
            !type.StaticConstructors.IsEmpty ||
            !(ctor.IsImplicitlyDeclared && ctor.Parameters.IsEmpty ||
              ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is RecordDeclarationSyntax &&
              ctor.Parameters.All(p => type.GetMembers(p.Name).OfType<IPropertySymbol>().Any(IsPositional))))
            return false;

        bool mutable = false;
        foreach (var member in type.GetMembers().Where(m => !m.IsStatic))
        {
            if (member is IFieldSymbol { AssociatedSymbol: null, IsImplicitlyDeclared: false } or IEventSymbol)
                return false;
            if (member is not IPropertySymbol p || p.IsImplicitlyDeclared && p.Name == "EqualityContract")
                continue;
            if (p.IsVirtual || p.IsOverride || p.IsIndexer || !IsAutoProperty(p) ||
                p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null } ||
                !IsScalar(p.Type) || VerifiableSymbolHelpers.HasAttribute(p, VerifiableSymbolHelpers.GhostAttr) ||
                VerifiableSymbolHelpers.HasAttribute(p, VerifiableSymbolHelpers.InvariantAttr))
                return false;
            mutable |= p.SetMethod is { IsInitOnly: false };
        }

        if (mutable && (!allowBoundaryDto || creation.Parent is not IReturnOperation))
            return false;

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        return initializer.Initializers.All(i => i is ISimpleAssignmentOperation
            {
                Target: IPropertyReferenceOperation
                {
                    Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }
                } target
            } assignment &&
            SymbolEqualityComparer.Default.Equals(target.Property.ContainingType, type) &&
            seen.Add(target.Property) &&
            !assignment.Value.DescendantsAndSelf().Any(op =>
                op is IAssignmentOperation or IIncrementOrDecrementOperation or IObjectCreationOperation ||
                op is IConversionOperation { OperatorMethod: { } c } && c.ContainingType.SpecialType != SpecialType.System_Decimal ||
                op is IBinaryOperation { OperatorMethod: { } b } && b.ContainingType.SpecialType is not (SpecialType.System_Decimal or SpecialType.System_String) ||
                op is IUnaryOperation { OperatorMethod: { } u } && u.ContainingType.SpecialType != SpecialType.System_Decimal ||
                op is IArgumentOperation { Parameter.RefKind: not RefKind.None } ||
                op is IPropertyReferenceOperation { Property: var read } &&
                (read.IsStatic || read.IsVirtual || read.IsOverride || read.IsAbstract ||
                 !IsAutoProperty(read) && read.ContainingType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)));
    }

    private static bool IsPositional(IPropertySymbol p) =>
        p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax;

    private static bool IsAutoProperty(IPropertySymbol p) =>
        IsPositional(p) ||
        p.GetMethod is { IsExtern: false } && p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is
            PropertyDeclarationSyntax { AccessorList: { } list } &&
        list.Accessors.All(a => a.Body is null && a.ExpressionBody is null && !a.SemicolonToken.IsMissing);

    private static bool IsScalar(ITypeSymbol type) =>
        type.TypeKind == TypeKind.Enum ||
        type.SpecialType is SpecialType.System_Boolean or SpecialType.System_String or SpecialType.System_Decimal
            or SpecialType.System_Char or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 ||
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
        IsScalar(nullable.TypeArguments[0]);
}
