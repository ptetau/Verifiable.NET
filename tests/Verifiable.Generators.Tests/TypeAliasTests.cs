using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

[TypeAlias<int>]
public readonly partial record struct AliasOrderId { public static bool IsValid(int v) => v >= 1; }

[TypeAlias<int>]
public readonly partial record struct AliasNonNegCount { public static bool IsValid(int v) => v >= 0; }

[TypeAlias<decimal>("An order amount must be between 0.01 and 1,000,000 so fees and ledger totals stay positive and bounded.")]
public readonly partial record struct AliasPositiveMoney { public static bool IsValid(decimal v) => v > 0m && v <= 1000000m; }

[TypeAlias<string>]
public readonly partial record struct AliasLabel { public static bool IsValid(string v) => v is { Length: >= 1 and <= 40 }; }

[Verifiable]
public readonly record struct AliasLedger(
    AliasNonNegCount Count,
    ImmutableDictionary<AliasOrderId, AliasPositiveMoney> Items)
{
    [Invariant]
    public bool CountMatchesItems() => Items.Count == Count;
}

[Verifiable]
public static class AliasPortEngine
{
    [InboundPort]
    public static AliasLedger RecordOrder(
        AliasLedger ledger,
        AliasOrderId orderId,
        AliasLabel label,
        AliasPositiveMoney amount,
        AliasOrderId? relatedOrderId)
    {
        Spec.Requires(amount > 0m);
        _ = label.Length;
        _ = relatedOrderId;
        if (ledger.Items.ContainsKey(orderId))
        {
            return ledger;
        }

        return ledger with
        {
            Count = AliasNonNegCount.From(ledger.Count + 1),
            Items = ledger.Items.Add(orderId, amount)
        };
    }
}

public class TypeAliasTests
{
    [Fact]
    public void CompiledTypeAliases_SupportImplicitWideningOrderingFormattingAndJsonDictionaryKeys()
    {
        var a = AliasOrderId.From(5);
        var b = AliasOrderId.From(12);
        int widened = a;
        Assert.Equal(5, widened);
        Assert.Equal(17, a + b);
        Assert.True(a < b);
        Assert.Equal("5", a.ToString());

        var money = AliasPositiveMoney.From(42.5m);
        decimal dec = money;
        Assert.Equal(42.5m, dec);

        var label = AliasLabel.From("Invoice #1");
        string labelStr = label;
        Assert.Equal("Invoice #1", labelStr);
        Assert.Equal(10, label.Length);

        var ledger = new AliasLedger(
            AliasNonNegCount.From(1),
            ImmutableDictionary<AliasOrderId, AliasPositiveMoney>.Empty.Add(a, money));
        var json = JsonSerializer.Serialize(ledger);
        var roundTrip = JsonSerializer.Deserialize<AliasLedger>(json);
        Assert.Equal(1, (int)roundTrip.Count);
        Assert.Equal(42.5m, (decimal)roundTrip.Items[a]);
    }

    [Fact]
    public void InvariantMethodAndSpecRequires_GenerateCheckedPortAndGate()
    {
        var raw = new RawAliasLedger { Count = 2, Items = new Dictionary<int, decimal>() };
        var mismatch = AliasPortEnginePort.RecordOrder(raw, 1, "OK", 10m, null);
        Assert.False(mismatch.IsValid);
        Assert.Contains(mismatch.Violations, v => v.Predicate == "CountMatchesItems");

        var rawZero = new RawAliasLedger { Count = 0, Items = new Dictionary<int, decimal>() };
        var badAmount = AliasPortEnginePort.RecordOrder(rawZero, 1, "OK", 0m, null);
        Assert.False(badAmount.IsValid);
        Assert.Contains(badAmount.Violations, v => v.Message.Contains("fees and ledger totals stay positive and bounded"));
    }

    [Property]
    public Property CompiledAliasInboundPort_AcceptsValidAndRejectsInvalid() =>
        (from orderId in Gen.Int32().Between(1, 500)
         from amount in Gen.Decimal().Between(1m, 1000m)
         from label in Gen.String().WithLengthBetween(1, 20)
         select (orderId, amount, label)).ForAll(x =>
        {
            var raw = new RawAliasLedger { Count = 0, Items = new Dictionary<int, decimal>() };
            var ok = AliasPortEnginePort.RecordOrder(raw, x.orderId, x.label, x.amount, x.orderId);
            var badAmount = AliasPortEnginePort.RecordOrder(raw, x.orderId, x.label, 0m, null);
            var badId = AliasPortEnginePort.RecordOrder(raw, 0, x.label, x.amount, null);
            var badLabel = AliasPortEnginePort.RecordOrder(raw, x.orderId, "", x.amount, null);

            return ok.IsValid
                && ok.Value.Count == 1
                && ok.Value.Items[AliasOrderId.From(x.orderId)] == x.amount
                && !badAmount.IsValid
                && !badId.IsValid
                && !badLabel.IsValid;
        });
}
