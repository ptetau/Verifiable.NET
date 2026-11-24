using System;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Verifiable.Contracts;

namespace Verifiable.Testing;

/// <summary>
/// GalaxyCheck generator helpers and inbound-port rejection checks for Verifiable.NET refined types and boundaries.
/// </summary>
public static class VerifiableGen
{
    /// <summary>Generates positive integers in <c>[1, max]</c>.</summary>
    public static IGen<int> PositiveInt(int max = 10_000) =>
        Gen.Int32().Between(1, max);

    /// <summary>Generates non-empty strings within <c>[1, maxLength]</c>.</summary>
    public static IGen<string> NonEmptyString(int maxLength = 64) =>
        Gen.String().WithLengthBetween(1, maxLength);

    /// <summary>Generates ISO-4217-style 3-letter currency codes.</summary>
    public static IGen<string> CurrencyCode() =>
        Gen.Element(new[] { "USD", "EUR", "NZD", "GBP", "JPY", "AUD" });

    /// <summary>Generates strictly positive decimal amounts with 2 decimal places.</summary>
    public static IGen<decimal> PositiveDecimal(long maxCents = 100_000_00L) =>
        from cents in Gen.Int64().Between(1, maxCents)
        select cents / 100m;

    /// <summary>Generates non-empty <see cref="Guid"/> values.</summary>
    public static IGen<Guid> NonEmptyGuid() =>
        Gen.Guid().Where(static g => g != Guid.Empty);

    /// <summary>
    /// Builds a GalaxyCheck generator for a <c>[Refined&lt;TUnderlying&gt;]</c> value using its generated <c>TryFrom</c> marshaler.
    /// </summary>
    public static IGen<TRefined> Refined<TRefined, TUnderlying>(
        IGen<TUnderlying> source,
        Func<TUnderlying, MarshalResult<TRefined>> tryMarshal) =>
        source
            .Select(tryMarshal)
            .Where(static r => r.IsValid)
            .Select(static r => r.Value);

    /// <summary>
    /// Asserts that an <c>[InboundPort]</c> or <c>Gate</c> rejects all generated invalid raw inputs with structured violations.
    /// </summary>
    public static Property PortRejectsInvalid<TRaw, TResult>(
        IGen<TRaw> invalidInputs,
        Func<TRaw, MarshalResult<TResult>> portCall) =>
        invalidInputs.ForAll(raw =>
        {
            var res = portCall(raw);
            return !res.IsValid && !res.Violations.IsEmpty;
        });
}
