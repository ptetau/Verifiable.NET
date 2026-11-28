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

    /// <summary>Prefix of the synthetic field that holds a primary-constructor parameter captured by a member of a class or struct.</summary>
    internal const string CapturedFieldPrefix = "$";

    /// <summary>The parameter list of a C# 12 primary constructor on a class or struct (not a record), or null.</summary>
    internal static TypeDeclarationSyntax? PrimaryConstructorDeclaration(INamedTypeSymbol type)
    {
        if (type.IsRecord || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            type.BaseType is not { } bt || bt.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType))
        {
            return null;
        }
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is TypeDeclarationSyntax decl && decl.ParameterList is not null && decl is ClassDeclarationSyntax or StructDeclarationSyntax)
            {
                return decl;
            }
        }
        return null;
    }

    /// <summary>True for the constructor a class or struct declares through its primary-constructor parameter list.</summary>
    internal static bool IsPrimaryNonRecordConstructor(IMethodSymbol ctor) =>
        ctor.MethodKind == MethodKind.Constructor && !ctor.IsStatic &&
        ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ClassDeclarationSyntax or StructDeclarationSyntax;

    /// <summary>
    /// The primary-constructor parameters of a class or struct that members read (captured), and whether any parameter
    /// is ever assigned, incremented or passed by reference (then the type is not a pure projection of its parameters).
    /// </summary>
    internal static (List<IParameterSymbol> Captured, bool AnyMutated) AnalyzePrimaryConstructor(INamedTypeSymbol type)
    {
        var captured = new List<IParameterSymbol>();
        var anyMutated = false;
        if (PrimaryConstructorDeclaration(type) is not { } decl)
        {
            return (captured, false);
        }
        var ctor = type.InstanceConstructors.FirstOrDefault(c => IsPrimaryNonRecordConstructor(c));
        if (ctor is null)
        {
            return (captured, false);
        }
        foreach (var param in ctor.Parameters)
        {
            var isCaptured = false;
            foreach (var r in type.DeclaringSyntaxReferences)
            {
                if (r.GetSyntax() is not TypeDeclarationSyntax td) continue;
                foreach (var id in td.Members.SelectMany(m => m.DescendantNodes().OfType<IdentifierNameSyntax>())
                             .Where(i => i.Identifier.ValueText == param.Name))
                {
                    if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) continue;
                    if (id.Parent is MemberBindingExpressionSyntax) continue;
                    if (id.Parent is NameColonSyntax || id.Parent is NameEqualsSyntax) continue;
                    // An initializer reads the parameter at construction time, not through a stored capture.
                    var inInitializer = id.Ancestors().TakeWhile(a => a is not MemberDeclarationSyntax)
                        .Any(a => a is EqualsValueClauseSyntax);
                    if (!inInitializer) isCaptured = true;
                    if (id.Parent is AssignmentExpressionSyntax asg && asg.Left == id ||
                        id.Parent is PrefixUnaryExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression } ||
                        id.Parent is PostfixUnaryExpressionSyntax ||
                        id.Parent is ArgumentSyntax { RefKindKeyword.ValueText: "ref" or "out" or "in" } ||
                        id.Parent is RefExpressionSyntax)
                    {
                        anyMutated = true;
                    }
                }
            }
            if (isCaptured) captured.Add(param);
        }
        return (captured, anyMutated);
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
        var isPrimaryCtor = false;
        HashSet<string>? capturedNames = null;
        if (!isPositionalRecordCtor && !isPureDefaultCtor && IsPrimaryNonRecordConstructor(ctor))
        {
            var (captured, anyMutated) = AnalyzePrimaryConstructor(targetType);
            if (!anyMutated &&
                captured.All(c => composite.Fields.Any(f => f.Name == CapturedFieldPrefix + c.Name)))
            {
                isPrimaryCtor = true;
                capturedNames = new HashSet<string>(captured.Select(c => c.Name), StringComparer.Ordinal);
            }
        }

        if (!isPositionalRecordCtor && !isPureDefaultCtor && !isPrimaryCtor)
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
                if (!isPrimaryCtor)
                {
                    assignedMembers.Add(arg.Parameter.Name);
                }
                else if (capturedNames!.Contains(arg.Parameter.Name))
                {
                    assignedMembers.Add(CapturedFieldPrefix + arg.Parameter.Name);
                }
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
