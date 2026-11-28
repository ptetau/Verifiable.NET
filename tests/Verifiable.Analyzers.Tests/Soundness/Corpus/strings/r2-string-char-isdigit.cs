// char.IsDigit is true for Unicode decimal digits like Arabic-Indic U+0663, not just 0-9.
// @expect: rejected
// @witness: Corpus.Strings.IsDig.Ascii('٣')
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class IsDig
{
    public static bool Ascii(char c)
    {
        Spec.Ensures<bool>(r => !r || (c >= '0' && c <= '9'), "Claims digits are ASCII.");
        return char.IsDigit(c);
    }
}
