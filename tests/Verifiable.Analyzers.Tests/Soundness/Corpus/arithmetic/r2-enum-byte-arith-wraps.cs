// Arithmetic on a byte-based enum wraps like byte: (Color)255 + 1 is 0, not 256.
// @expect: rejected
// @known-bug: arithmetic on an enum with underlying byte is modelled as unbounded (c + 1 does not wrap at 255), so the false postcondition is proved
// @witness: Corpus.Arithmetic.R2EnumArith.Run((byte)255)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

public enum R2Color : byte { A = 0, B = 255 }

[Verifiable]
public static class R2EnumArith
{
    public static int Run(byte b)
    {
        Spec.Ensures<int>(r => r == b + 1, "The next value.");
        var c = (R2Color)b;
        return (int)(c + 1);
    }
}
