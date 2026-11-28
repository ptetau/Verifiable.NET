// Under [CheckedArithmetic] s += s throws when short overflows (20000 * 2 is 40000).
// @expect: rejected
// @witness: Corpus.Arithmetic.R2ShortCompound.Run((short)20000)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class R2ShortCompound
{
    public static short Run(short s)
    {
        Spec.Requires(s >= 0 && s <= 20000, "Small.");
        Spec.Ensures<short>(r => r == 2 * s, "Doubled.");
        s += s;
        return s;
    }
}
