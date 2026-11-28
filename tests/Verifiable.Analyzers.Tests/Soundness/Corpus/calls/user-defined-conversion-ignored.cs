// A user-defined implicit conversion adds one; the analyzer may treat it as an identity conversion.
// @expect: rejected
// @witness: Corpus.Calls.CustomConv.Raw(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public readonly record struct Cents(int Value)
{
    public static implicit operator int(Cents c) => c.Value + 1;
}

[Verifiable]
public static class CustomConv
{
    public static int Raw(int v)
    {
        Spec.Requires(v >= 0 && v <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == v, "Unwrapping gives the value back.");
        int r = new Cents(v);
        return r;
    }
}
