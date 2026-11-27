using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

internal static class StringIntrinsics
{
    internal const int MaxJoinElements = 32;

    internal static bool Handles(IMethodSymbol method) =>
        method.ContainingType.SpecialType == SpecialType.System_String &&
        method.Name is "Join" or "Trim" or "Contains" or "StartsWith" or "EndsWith" or "IsNullOrWhiteSpace" or "IsNullOrEmpty";

    internal static bool IsSupported(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        if (method.ContainingType.SpecialType != SpecialType.System_String) return false;
        var parameters = method.Parameters;
        if (method.IsStatic)
        {
            if (method.Name == "Join") return TryGetJoinElements(call, out _);
            return method.Name is "IsNullOrWhiteSpace" or "IsNullOrEmpty" &&
                parameters.Length == 1 && parameters[0].Type.SpecialType == SpecialType.System_String;
        }

        if (method.Name == "Trim") return parameters.Length == 0;
        if (method.Name is not ("StartsWith" or "EndsWith" or "Contains") ||
            parameters.Length is not (1 or 2) ||
            parameters[0].Type.SpecialType is not (SpecialType.System_String or SpecialType.System_Char))
            return false;

        // The one-string prefix/suffix overloads read CurrentCulture; do not
        // substitute ordinal matching for linguistic matching.
        if (parameters.Length == 1)
            return method.Name == "Contains" || parameters[0].Type.SpecialType == SpecialType.System_Char;

        return parameters[1].Type.ToDisplayString() == "System.StringComparison" &&
            call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 1)?.Value.ConstantValue is { HasValue: true, Value: (int)StringComparison.Ordinal };
    }

    internal static bool TryGetJoinElements(IInvocationOperation call, out ImmutableArray<IOperation> elements)
    {
        elements = default;
        var method = call.TargetMethod;
        if (method.ContainingType.SpecialType != SpecialType.System_String || method.Name != "Join" ||
            method.Parameters.Length != 2 || !method.IsStatic || !method.Parameters[1].IsParams ||
            method.Parameters[0].Type.SpecialType is not (SpecialType.System_String or SpecialType.System_Char))
            return false;

        var type = method.Parameters[1].Type;
        if (!(type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_String } ||
            type is INamedTypeSymbol span && span.OriginalDefinition.ToDisplayString() == "System.ReadOnlySpan<T>" &&
            span.TypeArguments[0].SpecialType == SpecialType.System_String))
            return false;
        var value = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 1)?.Value;
        switch (value)
        {
            case IArrayCreationOperation { Initializer: { } init }:
                elements = init.ElementValues;
                break;
            case ICollectionExpressionOperation collection when collection.Elements.All(e => e is not ISpreadOperation):
                elements = collection.Elements;
                break;
            default:
                return false;
        }
        return elements.Length <= MaxJoinElements;
    }
}
