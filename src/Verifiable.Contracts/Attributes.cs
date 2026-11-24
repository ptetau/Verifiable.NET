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
/// Declares a type invariant predicate verified by the SMT prover and checked at inbound ports.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class InvariantAttribute(string expression) : Attribute
{
    /// <summary>Gets the C# boolean invariant expression.</summary>
    public string Expression { get; } = expression;
}

/// <summary>
/// Declares a method precondition predicate that callers must satisfy under their path condition.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresAttribute(string expression) : Attribute
{
    /// <summary>Gets the C# boolean precondition expression.</summary>
    public string Expression { get; } = expression;
}

/// <summary>
/// Declares a method postcondition predicate referencing <c>result</c> and method parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EnsuresAttribute(string expression) : Attribute
{
    /// <summary>Gets the C# boolean postcondition expression.</summary>
    public string Expression { get; } = expression;
}

/// <summary>
/// Declares a loop invariant predicate used to verify inductive loops inside verifiable methods.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class LoopInvariantAttribute(string expression) : Attribute
{
    /// <summary>Gets the C# boolean loop invariant expression.</summary>
    public string Expression { get; } = expression;
}

/// <summary>
/// Declares a non-negative integer ranking expression that strictly decreases on every loop iteration.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class DecreasesAttribute(string expression) : Attribute
{
    /// <summary>Gets the decreasing integer expression.</summary>
    public string Expression { get; } = expression;
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
/// Marks a proof-only specification property or field used in contracts and invariants.
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
/// Marks a <c>readonly partial record struct</c> for refinement type expansion by <c>Verifiable.Generators</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class RefinedAttribute<TUnderlying>(string predicate) : Attribute
{
    /// <summary>Gets the refinement predicate referencing <c>Value</c>.</summary>
    public string Predicate { get; } = predicate;
}

/// <summary>
/// Generates a zero-allocation constrained type alias backed by <typeparamref name="TUnderlying"/> with compile-time invariants,
/// implicit widening to <typeparamref name="TUnderlying"/>, JSON serialization, and checked/unchecked constructors.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class TypeAliasAttribute<TUnderlying>(string name, string predicate) : Attribute
{
    /// <summary>Gets the generated type name.</summary>
    public string Name { get; } = name;

    /// <summary>Gets the refinement predicate referencing <c>Value</c>.</summary>
    public string Predicate { get; } = predicate;

    /// <summary>Gets or sets the target namespace when applied at the assembly level (defaults to the declaring namespace when applied on a type).</summary>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets whether the generated alias type is <c>internal</c> instead of <c>public</c>.</summary>
    public bool IsInternal { get; set; }
}
