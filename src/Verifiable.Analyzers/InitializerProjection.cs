using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Verifiable.Analyzers.Smt;

namespace Verifiable.Analyzers;

internal static class InitializerProjection
{
    /// <summary>
    /// Returns true if <paramref name="type"/> is a reference class without value-equality semantics
    /// (i.e. not a value type, not a record, not string, and without a user-defined equality operator).
    /// Structural SMT equality on such types is unsound because C# compares object references.
    /// </summary>
    public static bool UsesReferenceEquality(ITypeSymbol type)
    {
        if (type.IsValueType || type.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.IsRecord || named.TypeKind == TypeKind.Enum)
            {
                return false;
            }

            var orig = named.OriginalDefinition.ToDisplayString();
            if (orig.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) ||
                orig == "System.Collections.Generic.IReadOnlyList<T>")
            {
                return false;
            }

            return named.TypeKind == TypeKind.Class;
        }

        return false;
    }

    /// <summary>
    /// Returns true if an object creation can be modeled purely as a field projection
    /// (positional record construction, or implicit/default parameterless construction with
    /// direct auto-property / field object initializers) without arbitrary user constructor or setter logic.
    /// </summary>
    public static bool IsSupportedProjection(
        IObjectCreationOperation obj,
        SmtTypeSystem types,
        out string? reason)
    {
        reason = null;

        if (obj.Type is not INamedTypeSymbol targetType)
        {
            reason = "Unsupported object creation target type.";
            return false;
        }

        if (!types.TryGetComposite(targetType, out var composite) || composite is null)
        {
            reason = $"Type '{targetType.Name}' is not modeled as an SMT composite.";
            return false;
        }

        var ctor = obj.Constructor;
        if (ctor is null)
        {
            reason = $"Object creation of '{targetType.Name}' has no resolved constructor.";
            return false;
        }

        var isPositionalRecordCtor = IsPurePositionalRecordConstructor(ctor, composite);
        var isPureDefaultCtor = IsPureDefaultParameterlessConstructor(ctor);

        if (!isPositionalRecordCtor && !isPureDefaultCtor)
        {
            reason = $"Constructor of '{targetType.Name}' has custom logic and cannot be lowered as a pure projection.";
            return false;
        }

        // Validate every initializer assignment targets a direct stored auto-property or field on the new instance.
        var assignedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in obj.Arguments)
        {
            if (arg.Parameter is not null)
            {
                assignedMembers.Add(arg.Parameter.Name);
            }
        }

        if (obj.Initializer is not null)
        {
            foreach (var init in obj.Initializer.Initializers)
            {
                if (init is not ISimpleAssignmentOperation sa ||
                    sa.Target is not IMemberReferenceOperation mr ||
                    mr.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver })
                {
                    reason = $"Unsupported initializer '{init.Syntax}' in object creation of '{targetType.Name}'.";
                    return false;
                }

                if (!IsPureStoredAssignableMember(mr.Member, composite))
                {
                    reason = $"Initializer target '{mr.Member.Name}' on '{targetType.Name}' is not a pure stored auto-property or field.";
                    return false;
                }

                assignedMembers.Add(mr.Member.Name);
            }
        }

        // Ensure no omitted stored field has an unmodeled non-trivial member initializer.
        foreach (var field in composite.Fields)
        {
            if (!assignedMembers.Contains(field.Name) && HasNonDefaultMemberInitializer(targetType, field.Name))
            {
                reason = $"Omitted member '{field.Name}' on '{targetType.Name}' has a custom member initializer that is not explicitly assigned.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether a constructor is a synthesized primary positional record constructor whose
    /// parameters map 1:1 to record positional properties.
    /// </summary>
    public static bool IsPurePositionalRecordConstructor(IMethodSymbol ctor, SmtCompositeInfo composite)
    {
        if (ctor.ContainingType.BaseType?.IsRecord == true)
        {
            return false;
        }

        if (ctor.DeclaringSyntaxReferences.Length == 0)
        {
            var isMetadataRecord = ctor.ContainingType.IsRecord ||
                ctor.ContainingType.GetMembers("PrintMembers").OfType<IMethodSymbol>().Any() ||
                (ctor.ContainingType.IsValueType &&
                 ctor.ContainingType.GetMembers("Deconstruct").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == ctor.Parameters.Length));

            if (!isMetadataRecord ||
                ctor.Parameters.Length == 0 ||
                (ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, ctor.ContainingType)))
            {
                return false;
            }

            return ctor.Parameters.All(p =>
                composite.Fields.Any(f =>
                    f.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
                    SymbolEqualityComparer.Default.Equals(f.Type, p.Type)));
        }

        if (ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not RecordDeclarationSyntax)
        {
            return false;
        }

        return ctor.Parameters.All(p =>
            ctor.ContainingType.GetMembers(p.Name).OfType<IPropertySymbol>().Any(prop =>
                prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax));
    }

    /// <summary>
    /// Checks whether a parameterless constructor is compiler-synthesized or an empty user constructor
    /// (with no custom base constructor logic).
    /// </summary>
    public static bool IsPureDefaultParameterlessConstructor(IMethodSymbol ctor)
    {
        if (ctor.Parameters.Length != 0)
        {
            return false;
        }

        // Reject types inheriting from a non-object / non-ValueType base with custom constructors.
        for (var bt = ctor.ContainingType.BaseType;
             bt is not null && bt.SpecialType != SpecialType.System_Object && bt.SpecialType != SpecialType.System_ValueType;
             bt = bt.BaseType)
        {
            var baseDefault = bt.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
            if (baseDefault is null || !IsPureDefaultParameterlessConstructor(baseDefault))
            {
                return false;
            }
        }

        if (ctor.IsImplicitlyDeclared)
        {
            return true;
        }

        // Allow metadata parameterless constructors only on pure DTOs with no custom syntax in current compilation
        // if all stored properties are simple public properties (Wait: for external DTO classes like OrderResponse,
        // the compiler-generated parameterless ctor in metadata has DeclaringSyntaxReferences.Length == 0).
        if (ctor.DeclaringSyntaxReferences.Length == 0)
        {
            return true;
        }

        foreach (var synRef in ctor.DeclaringSyntaxReferences)
        {
            var syn = synRef.GetSyntax();
            if (syn is ConstructorDeclarationSyntax cd)
            {
                if (cd.Initializer is not null)
                {
                    return false;
                }

                if (cd.Body is { Statements.Count: 0 })
                {
                    return true;
                }

                return false;
            }

            if (syn is TypeDeclarationSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPureStoredAssignableMember(ISymbol member, SmtCompositeInfo composite)
    {
        if (!composite.Fields.Any(f => f.Name == member.Name))
        {
            return false;
        }

        if (member is IFieldSymbol)
        {
            return true;
        }

        if (member is IPropertySymbol prop)
        {
            if (prop.DeclaringSyntaxReferences.Length == 0)
            {
                return true;
            }

            foreach (var synRef in prop.DeclaringSyntaxReferences)
            {
                var syn = synRef.GetSyntax();
                if (syn is ParameterSyntax)
                {
                    return true;
                }

                if (syn is PropertyDeclarationSyntax pd && pd.AccessorList is not null)
                {
                    var getter = pd.AccessorList.Accessors.FirstOrDefault(a => a.Keyword.Text == "get");
                    var setter = pd.AccessorList.Accessors.FirstOrDefault(a => a.Keyword.Text is "set" or "init");
                    var pureGet = getter is null || (getter.Body is null && getter.ExpressionBody is null);
                    var pureSet = setter is null || (setter.Body is null && setter.ExpressionBody is null);
                    return pureGet && pureSet;
                }
            }
        }

        return false;
    }

    internal static bool HasNonDefaultMemberInitializer(INamedTypeSymbol type, string memberName)
    {
        for (var cur = type;
             cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType;
             cur = cur.BaseType)
        {
            foreach (var member in cur.GetMembers(memberName))
            {
                foreach (var synRef in member.DeclaringSyntaxReferences)
                {
                    var syn = synRef.GetSyntax();
                    if (syn is PropertyDeclarationSyntax pd && pd.Initializer is not null)
                    {
                        return !IsTrivialDefaultInitializer(pd.Initializer.Value);
                    }

                    if (syn is VariableDeclaratorSyntax vd && vd.Initializer is not null)
                    {
                        return !IsTrivialDefaultInitializer(vd.Initializer.Value);
                    }
                }
            }
        }

        return false;
    }

    private static bool IsTrivialDefaultInitializer(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit &&
        (lit.Token.Value is null ||
         lit.Token.Value is false ||
         (lit.Token.Value is int i && i == 0) ||
         (lit.Token.Value is long l && l == 0L) ||
         (lit.Token.Value is decimal d && d == 0m));
}
