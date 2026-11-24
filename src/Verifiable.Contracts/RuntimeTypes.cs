using System;
using System.Collections.Immutable;

namespace Verifiable.Contracts;

/// <summary>
/// Structured contract failure produced by refined constructors and generated inbound port gates.
/// </summary>
/// <param name="Field">Path or parameter name where the violation occurred.</param>
/// <param name="Predicate">Predicate text that evaluated to false.</param>
/// <param name="Message">Human- and agent-readable failure message.</param>
[Verifiable]
public readonly record struct ContractViolation(string Field, string Predicate, string Message);

/// <summary>
/// Represents the result of marshaling untrusted raw I/O data across an inbound port boundary.
/// </summary>
/// <typeparam name="T">Target verified domain type.</typeparam>
[Verifiable]
public readonly record struct MarshalResult<T>
{
    private readonly ImmutableArray<ContractViolation> _violations;

    /// <summary>Gets whether all structural and invariant checks succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the marshaled verified value when <see cref="IsValid"/> is <see langword="true"/>.</summary>
    public T Value { get; }

    /// <summary>Gets the accumulated contract violations when <see cref="IsValid"/> is <see langword="false"/>.</summary>
    public ImmutableArray<ContractViolation> Violations =>
        _violations.IsDefault ? ImmutableArray<ContractViolation>.Empty : _violations;

    private MarshalResult(bool isValid, T value, ImmutableArray<ContractViolation> violations)
    {
        IsValid = isValid;
        Value = value;
        _violations = violations;
    }

    /// <summary>Creates a valid marshaled result wrapping <paramref name="value"/>.</summary>
    [PureAxiom]
    public static MarshalResult<T> Ok(T value) =>
        new(true, value, ImmutableArray<ContractViolation>.Empty);

    /// <summary>Creates a failed marshaled result containing one or more contract violations.</summary>
    [PureAxiom]
    public static MarshalResult<T> Fail(params ContractViolation[] violations) =>
        new(false, default!, violations is { Length: > 0 }
            ? ImmutableArray.Create(violations)
            : ImmutableArray.Create(new ContractViolation(string.Empty, "false", "Validation failed.")));

    /// <summary>Creates a failed marshaled result from an immutable array of violations.</summary>
    [PureAxiom]
    public static MarshalResult<T> Fail(ImmutableArray<ContractViolation> violations) =>
        new(false, default!, violations.IsDefaultOrEmpty
            ? ImmutableArray.Create(new ContractViolation(string.Empty, "false", "Validation failed."))
            : violations);
}

/// <summary>
/// Explicit deterministic discriminated return union used inside verifiable cages instead of exceptions.
/// </summary>
/// <typeparam name="T">Success payload type.</typeparam>
/// <typeparam name="TError">Domain error payload type.</typeparam>
[Verifiable]
public readonly record struct Result<T, TError>
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the success payload when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public T Value { get; }

    /// <summary>Gets the error payload when <see cref="IsSuccess"/> is <see langword="false"/>.</summary>
    public TError Error { get; }

    private Result(bool isSuccess, T value, TError error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    /// <summary>Creates a successful result.</summary>
    [PureAxiom]
    public static Result<T, TError> Ok(T value) => new(true, value, default!);

    /// <summary>Creates a failed result.</summary>
    [PureAxiom]
    public static Result<T, TError> Fail(TError error) => new(false, default!, error);
}

/// <summary>
/// Empty value type representing a void-like success payload in <see cref="Result{T, TError}"/>.
/// </summary>
[Verifiable]
public readonly record struct Unit
{
    /// <summary>Gets the singleton <see cref="Unit"/> value.</summary>
    public static readonly Unit Value = default;
}

/// <summary>
/// Explicit sink helpers for <see cref="MustConsumeAttribute"/> linear obligation tokens.
/// </summary>
public static class Obligation
{
    /// <summary>
    /// Explicitly discharges a linear obligation token on a failure or cancellation branch.
    /// </summary>
    /// <typeparam name="T">The <see cref="MustConsumeAttribute"/> struct type.</typeparam>
    /// <param name="obligation">The obligation token to discard.</param>
    /// <param name="reason">Audit reason explaining why the obligation is intentionally discarded.</param>
    [ConsumesObligation]
    [PureAxiom]
    public static void Discard<T>(T obligation, string reason) where T : struct
    {
        _ = obligation;
        _ = reason;
    }
}
