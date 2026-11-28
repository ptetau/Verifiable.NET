using System;

namespace Verifiable.Contracts;

/// <summary>
/// Marks an assembly, type, enum, or method as a hermetically sealed Verifiable Cage enforced with compile-time errors.
/// When applied to a non-<see cref="FlagsAttribute"/> enum, constrains SMT verification to declared enum members.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Enum)]
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

    /// <summary>
    /// Gets or sets the ID of the spec clause this rule comes from (e.g. <c>"C1"</c>), used for traceability and coverage.
    /// </summary>
    public string? Clause { get; set; }
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
/// Marks a proof-only specification member. Ghost fields and properties are excluded from generated raw I/O DTOs.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Method)]
public sealed class GhostAttribute : Attribute;

/// <summary>
/// Marks a trusted BCL wrapper or generator intrinsic whose contracts are assumed without analyzing its body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PureAxiomAttribute : Attribute;

/// <summary>
/// Requires that <c>int</c> and <c>long</c> arithmetic in the annotated scope never overflows. This is the default for
/// <c>[Verifiable]</c> code; the attribute restores it inside an <see cref="UncheckedArithmeticAttribute"/> scope.
/// </summary>
/// <remarks>
/// The prover models <c>int</c> and <c>long</c> as 32- and 64-bit values and proves that every <c>+</c>, <c>-</c>,
/// <c>*</c> and unary <c>-</c> stays in range. Code inside an explicit <c>unchecked(...)</c> expression or
/// <c>unchecked { }</c> block may wrap around instead, and is modelled as wrapping.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public sealed class CheckedArithmeticAttribute : Attribute;

/// <summary>
/// Lets <c>int</c> and <c>long</c> arithmetic in the annotated scope wrap around, as C# does by default.
/// </summary>
/// <remarks>
/// The prover still models <c>int</c> and <c>long</c> as 32- and 64-bit values, so wrap-around is taken into account
/// and proofs stay sound; it just does not require that overflow never happens. Arithmetic in a C# <c>checked</c>
/// context still must not overflow, because there it throws. The nearest of this attribute and
/// <see cref="CheckedArithmeticAttribute"/> (method, then enclosing types, then assembly) wins.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
public sealed class UncheckedArithmeticAttribute : Attribute;

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

    /// <summary>
    /// Gets or sets the ID of the spec clause this rule comes from (e.g. <c>"C1"</c>), used for traceability and coverage.
    /// </summary>
    public string? Clause { get; set; }
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

    /// <summary>
    /// Gets or sets the ID of the spec clause this rule comes from (e.g. <c>"C1"</c>), used for traceability and coverage.
    /// </summary>
    public string? Clause { get; set; }
}

/// <summary>
/// Triggers source generation of a verified 1:1 field/property projection mapping to <see cref="TargetType"/>
/// (or to the annotated <c>static partial</c> method's return type when omitted).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, AllowMultiple = true)]
public sealed class MapToAttribute : Attribute
{
    /// <summary>
    /// Initializes a <see cref="MapToAttribute"/> for a <c>static partial</c> projection method whose target type is inferred from its return type.
    /// </summary>
    public MapToAttribute()
    {
    }

    /// <summary>
    /// Initializes a <see cref="MapToAttribute"/> targeting <paramref name="targetType"/>.
    /// </summary>
    /// <param name="targetType">The target record, struct, or DTO class to project into.</param>
    public MapToAttribute(Type targetType)
    {
        TargetType = targetType;
    }

    /// <summary>
    /// Gets the target type to map to, or <see langword="null"/> when inferred from an annotated <c>static partial</c> method's return type.
    /// </summary>
    public Type? TargetType { get; }

    /// <summary>
    /// Gets or sets an optional generated method name when applied to a type (defaults to <c>To&lt;TargetTypeName&gt;</c>).
    /// </summary>
    public string? MethodName { get; set; }
}

/// <summary>
/// Marks an <c>ImmutableArray&lt;T&gt;</c> parameter, property, field, or return value as guaranteed non-default
/// (<c>!value.IsDefault</c>). Inside <c>[Verifiable]</c> scopes, Z3 assumes incoming values are initialized
/// and verifies that call sites, constructors, and returns never supply <c>default(ImmutableArray&lt;T&gt;)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.ReturnValue)]
public sealed class NonDefaultAttribute : Attribute;

/// <summary>
/// Stores compiler-generated contract metadata for an externally accessible verified member so referencing
/// compilations can prove helper predicates, preconditions, postconditions, invariants, and type aliases across assembly boundaries.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class VerifiableContractMetadataAttribute(
    string memberId,
    string kind,
    string expression,
    string? description = null) : Attribute
{
    /// <summary>
    /// Gets the Roslyn documentation comment ID (for example <c>M:Sample.Bounds.IsValid(System.Decimal)</c>) of the exported member.
    /// </summary>
    public string MemberId { get; } = memberId;

    /// <summary>
    /// Gets the contract metadata kind: <c>"Body"</c>, <c>"Requires"</c>, <c>"Ensures"</c>, <c>"Invariant"</c>, or <c>"IsValid"</c>.
    /// </summary>
    public string Kind { get; } = kind;

    /// <summary>
    /// Gets the canonical self-contained C# expression using fully-qualified <c>global::</c> symbol references.
    /// </summary>
    public string Expression { get; } = expression;

    /// <summary>
    /// Gets an optional plain-English explanation (<c>because</c> or invariant/type-alias description).
    /// </summary>
    public string? Description { get; } = description;
}

/// <summary>
/// Records that a method's contract deliberately allows more than one result (requirement R5), so the
/// one-answer check does not report it as open. Say why: <c>[AnyValidResult("Any free slot will do.")]</c>.
/// </summary>
/// <param name="reason">Why more than one answer is acceptable.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AnyValidResultAttribute(string reason) : Attribute
{
    /// <summary>
    /// Gets why more than one answer is acceptable.
    /// </summary>
    public string Reason { get; } = reason;
}
