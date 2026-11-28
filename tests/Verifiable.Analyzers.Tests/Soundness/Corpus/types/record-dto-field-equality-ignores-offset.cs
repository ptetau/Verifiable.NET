// Record equality compares DateTimeOffset fields by instant, so two stamps with different offsets can be ==.
// @expect: rejected
// @witness: Corpus.Types.StampEquality.Same(new System.DateTimeOffset(2020, 1, 1, 0, 0, 0, System.TimeSpan.Zero), new System.DateTimeOffset(2020, 1, 1, 1, 0, 0, System.TimeSpan.FromHours(1)))
// @known-bug: Record == compares a DateTimeOffset field as the (utc, offset) datatype, but DateTimeOffset equality ignores the offset.
using System;
using Verifiable.Contracts;

namespace Corpus.Types;

public readonly record struct Stamp(DateTimeOffset At);

[Verifiable]
public static class StampEquality
{
    public static bool Same(DateTimeOffset a, DateTimeOffset b)
    {
        Spec.Requires(a == b && a.Offset != b.Offset, "The same instant seen from two offsets.");
        Spec.Ensures<bool>(r => r == false, "Different offsets make different stamps.");
        return new Stamp(a) == new Stamp(b);
    }
}
