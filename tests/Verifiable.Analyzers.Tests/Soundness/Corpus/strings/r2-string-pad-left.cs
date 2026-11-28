// PadLeft(5) does not shorten longer text.
// @expect: rejected
// @witness: Corpus.Strings.Pad.Fixed("abcdefg")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Pad
{
    public static string Fixed(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<string>(r => r.Length == 5, "Claims width 5.");
        return s.PadLeft(5);
    }
}
