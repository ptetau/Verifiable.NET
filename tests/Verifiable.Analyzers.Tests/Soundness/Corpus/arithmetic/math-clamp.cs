// A correct program: Math.Clamp keeps the value in range.
// @expect: proved
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class Clamp
{
    public static int Limit(int x)
    {
        Spec.Ensures<int>(r => r >= 0 && r <= 10, "The result is between 0 and 10.");
        return Math.Clamp(x, 0, 10);
    }
}
