// A static readonly field of a record struct is read; the analyzer must not invent its value.
// @expect: rejected
// @witness: Corpus.Calls.R2StructField.Get()
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public readonly record struct R2Pt(int X, int Y);

[Verifiable]
public static class R2StructField
{
    private static readonly R2Pt Origin = new R2Pt(0, 0);
    private static readonly R2Pt Other = new R2Pt(1, 1);

    public static int Get()
    {
        Spec.Ensures<int>(r => r == 0, "Origin x is zero, but this reads Other.");
        return Other.X;
    }
}
