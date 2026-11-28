// A correct program: Math.Max then Math.Min clamps x into [-10, 10], also with negative values.
// @expect: proved
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class MinMax
{
    public static int Limit(int x)
    {
        Spec.Ensures<int>(r => r >= -10 && r <= 10 && (x >= -10 && x <= 10 ? r == x : true), "Clamped into [-10, 10].");
        return Math.Min(Math.Max(x, -10), 10);
    }
}
