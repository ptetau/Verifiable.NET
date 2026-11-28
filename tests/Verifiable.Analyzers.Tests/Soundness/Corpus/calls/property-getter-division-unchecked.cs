// A computed property divides by a field that can be 0. Reading it from a method must flag the division.
// @expect: rejected
// @witness: Corpus.Calls.PropertyCalls.Eval(new Corpus.Calls.Frac(1, 0))
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public sealed record Frac(int N, int D)
{
    public int Value => N / D;
}

[Verifiable]
public static class PropertyCalls
{
    public static int Eval(Frac f)
    {
        Spec.Requires(f.N >= 0 && f.N <= 100 && f.D >= 0 && f.D <= 100, "Small fractions only.");
        Spec.Ensures<int>(r => r >= 0, "Never negative.");
        return f.Value;
    }
}
