using System;

namespace Verifiable.Contracts;

/// <summary>
/// Marks an assembly, type, or method as a hermetically sealed Verifiable Cage enforced with compile-time errors.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public sealed class VerifiableAttribute : Attribute;

/// <summary>
/// Opts a specific type or method out of an enclosing assembly- or type-level <see cref="VerifiableAttribute"/> or <see cref="VerifiableCandidateAttribute"/> scope.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public sealed class UnverifiableAttribute : Attribute;

/// <summary>
/// Marks an assembly, type, or method as an incremental migration target; gatekeeper violations emit warnings instead of errors.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public sealed class VerifiableCandidateAttribute : Attribute;

/// <summary>
/// Marks a parameterless <see langword="bool"/> instance method or property as a type invariant
/// proved by Z3 and evaluated at <see cref="InboundPortAttribute"/> gates.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class InvariantAttribute(string? description = null) : Attribute
{
    /// <summary>
    /// Gets an optional human-readable explanation of the spirit of this invariant.
    /// </summary>
    public string? Description { get; } = description;
}

/// <summary>
/// Marks a value type as a linear obligation token that must be passed to a <see cref="ConsumesObligationAttribute"/> sink on every control-flow path.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class MustConsumeAttribute : Attribute;

/// <summary>
/// Marks a method as a linear obligation sink that discharges any <see cref="MustConsumeAttribute"/> arguments passed to it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsumesObligationAttribute : Attribute;

/// <summary>
/// Triggers source generation of raw DTOs, invariant gates, and safe port wrappers for an I/O boundary method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InboundPortAttribute : Attribute;

/// <summary>
/// Marks a proof-only specification property or field excluded from generated raw I/O DTOs.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class GhostAttribute : Attribute;

/// <summary>
/// Marks a trusted BCL wrapper or generator intrinsic whose contracts are assumed without analyzing its body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PureAxiomAttribute : Attribute;

/// <summary>
/// Enables finite bit-vector overflow checking in the SMT prover for integer arithmetic inside the annotated scope.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public sealed class CheckedArithmeticAttribute : Attribute;

/// <summary>
/// Marks a <c>readonly partial record struct</c> declaring <c>public static bool IsValid(TUnderlying value)</c>
/// for constrained type alias generation by <c>Verifiable.Generators</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class TypeAliasAttribute<TUnderlying>(string? description = null) : Attribute
{
    /// <summary>
    /// Gets an optional human-readable explanation of why values of this type are constrained.
    /// </summary>
    public string? Description { get; } = description;
}

/// <summary>
/// Marks a <c>readonly partial record struct</c> declaring <c>public static bool IsValid(TUnderlying value)</c>
/// for refinement type expansion by <c>Verifiable.Generators</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class RefinedAttribute<TUnderlying>(string? description = null) : Attribute
{
    /// <summary>
    /// Gets an optional human-readable explanation of why values of this type are constrained.
    /// </summary>
    public string? Description { get; } = description;
}
