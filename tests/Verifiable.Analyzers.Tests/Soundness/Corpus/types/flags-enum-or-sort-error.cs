// A correct program: Read | Write is the flags value 3.
// @expect: proved
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
