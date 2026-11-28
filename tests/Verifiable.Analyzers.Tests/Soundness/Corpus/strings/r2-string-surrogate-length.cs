// A character outside the BMP is two UTF-16 code units, so "😀".Length is 2, not 1.
// @expect: rejected
// @witness: Corpus.Strings.Emoji.Len()
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Emoji
{
    public static int Len()
    {
        Spec.Ensures<int>(r => r == 1, "Claims one unit.");
        return "\U0001F600".Length;
    }
}
