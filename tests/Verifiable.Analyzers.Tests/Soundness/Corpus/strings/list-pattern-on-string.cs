// A list pattern on a string checks its characters: "b" is not ['a', ..].
// @expect: rejected
// @witness: Corpus.Strings.ListPattern.StartsWithA("b")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ListPattern
{
    public static bool StartsWithA(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r, "Every text starts with 'a'.");
        return s is ['a', ..];
    }
}
