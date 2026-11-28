// A correct program: Read | Write is the flags value 3.
// @expect: proved
// @known-bug: `|` on a [Flags] enum is lowered as Boolean `or` over Int terms, so Z3 reports a sort error (VER103).
using Verifiable.Contracts;

namespace Corpus.Types;

[System.Flags]
public enum Access { None = 0, Read = 1, Write = 2 }

[Verifiable]
public static class FlagsCombo
{
    public static Access Both()
    {
        Spec.Ensures<Access>(r => r == (Access)3, "Both bits are set.");
        return Access.Read | Access.Write;
    }
}
