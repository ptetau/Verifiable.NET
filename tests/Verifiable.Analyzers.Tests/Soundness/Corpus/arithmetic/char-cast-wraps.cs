// (char) keeps only the low 16 bits, so (char)65601 is 'A' (65), not 65601.
// @expect: rejected
// @witness: Corpus.Arithmetic.CharWrap.Roundtrip(65601)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class CharWrap
{
    public static int Roundtrip(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r == x, "Converting to char and back changes nothing.");
        return (char)x;
    }
}
