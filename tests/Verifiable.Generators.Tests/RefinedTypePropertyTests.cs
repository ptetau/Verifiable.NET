using System;
using System.Collections.Immutable;
using System.Text.Json;
using GalaxyCheck;
using GalaxyCheck.Gens;
using GalaxyCheck.Gens.Injection.Int32;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

[Refined<int>("Value >= 1")]
public readonly partial record struct SampleTodoId;

[Refined<decimal>("Value > 0m && Value <= 1000000m")]
public readonly partial record struct SamplePositiveAmount;

[Refined<string>("Length is >= 1 and <= 20")]
public readonly partial record struct SampleCode;

[Refined<Guid>("Value != Guid.Empty")]
public readonly partial record struct SampleCommandId;

[Refined<int>("Value >= 0 && Value < 16")]
public readonly partial record struct SampleSlot;

public class RefinedTypePropertyTests
{
    [Property]
    public bool TodoId_FromAndTryFrom_MatchPredicate(int raw)
    {
        var expectedValid = raw >= 1;
        var ok = SampleTodoId.TryFrom(raw, out var parsed);
        var marshal = SampleTodoId.TryFrom(raw);

        if (expectedValid)
        {
            return ok && parsed.Value == raw && marshal.IsValid && marshal.Value == parsed && SampleTodoId.From(raw) == parsed;
        }

        if (ok || marshal.IsValid || marshal.Violations.IsEmpty)
        {
            return false;
        }

        try
        {
            _ = SampleTodoId.From(raw);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    [Property]
    public Property PositiveAmount_JsonRoundTrip_ValidAndInvalid() =>
        Gen.Decimal().Between(-500m, 500m).ForAll(raw =>
        {
            var expectedValid = raw > 0m && raw <= 1_000_000m;
            var json = JsonSerializer.Serialize(raw);

            if (expectedValid)
            {
                var roundTripped = JsonSerializer.Deserialize<SamplePositiveAmount>(json);
                var reserialized = JsonSerializer.Serialize(roundTripped);
                return roundTripped.Value == raw && JsonSerializer.Deserialize<decimal>(reserialized) == raw;
            }

            try
            {
                _ = JsonSerializer.Deserialize<SamplePositiveAmount>(json);
                return false;
            }
            catch (JsonException)
            {
                return true;
            }
        });

    [Property]
    public Property Code_StringShorthandPredicate_EnforcesNonNullAndLength() =>
        Gen.Nullable(Gen.String().WithLengthBetween(0, 30)).ForAll(raw =>
        {
            var expectedValid = raw is not null && raw.Length is >= 1 and <= 20;
            var ok = SampleCode.TryFrom(raw!, out var code);
            return ok == expectedValid && (!ok || code.Value == raw);
        });

    [Property]
    public Property DictionaryWithRefinedKey_RoundTripsThroughJson() =>
        (from k1 in Gen.Int32().Between(1, 50)
         from k2 in Gen.Int32().Between(51, 100)
         from c1 in Gen.String().WithLengthBetween(1, 10)
         from c2 in Gen.String().WithLengthBetween(1, 10)
         select (k1, k2, c1, c2)).ForAll(x =>
        {
            var dict = ImmutableDictionary<SampleTodoId, SampleCode>.Empty
                .Add(SampleTodoId.From(x.k1), SampleCode.From(x.c1))
                .Add(SampleTodoId.From(x.k2), SampleCode.From(x.c2));

            var json = JsonSerializer.Serialize(dict);
            var restored = JsonSerializer.Deserialize<ImmutableDictionary<SampleTodoId, SampleCode>>(json);

            return restored is not null
                && restored.Count == 2
                && restored[SampleTodoId.From(x.k1)].Value == x.c1
                && restored[SampleTodoId.From(x.k2)].Value == x.c2;
        });

    [Fact]
    public void DictionaryWithInvalidRefinedKeyInJson_ThrowsJsonException()
    {
        const string badJson = """{"0":"alpha"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ImmutableDictionary<SampleTodoId, SampleCode>>(badJson));
    }

    [Property]
    public bool ReadUnchecked_MatchesSpanIndexing([Between(0, 15)] int idx)
    {
        int[] buffer = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160];
        var slot = SampleSlot.From(idx);
        return slot.ReadUnchecked<int>(buffer) == buffer[idx];
    }

    [Fact]
    public void CommandId_RejectsEmptyGuid()
    {
        Assert.False(SampleCommandId.TryFrom(Guid.Empty, out _));
        Assert.True(SampleCommandId.TryFrom(Guid.NewGuid(), out var valid));
        Assert.NotEqual(Guid.Empty, valid.Value);
    }
}
