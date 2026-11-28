// Correct: "😀" is two UTF-16 code units; no regex is used here.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Emoji2
{
    public static int Len()
    {
        Spec.Ensures<int>(r => r == 2, "Two units.");
        return "\U0001F600".Length;
    }
}
