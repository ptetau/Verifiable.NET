// A with-expression sets a closed enum field from an out-of-range cast; the record then reaches a callee.
// @expect: rejected
// @witness: Corpus.Types.EnumWith.Caller(new Corpus.Types.Pixel(Corpus.Types.Hue.Red), 5)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public enum Hue { Red, Green }

public sealed record Pixel(Hue H);

[Verifiable]
public static class EnumWith
{
    public static int Code(Pixel p)
    {
        Spec.Ensures<int>(r => r == 0 || r == 1, "Codes are 0 or 1.");
        return (int)p.H;
    }

    public static int Caller(Pixel p, int raw)
    {
        Spec.Requires(raw >= 0 && raw <= 10, "Small raw values.");
        Spec.Ensures<int>(r => r == 0 || r == 1, "Codes are 0 or 1.");
        return Code(p with { H = (Hue)raw });
    }
}
