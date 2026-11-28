// With Multiline, ^a$ matches "x\na\ny".
// @expect: rejected
// @witness: Corpus.Strings.MultiLine.Is("x\na\ny")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class MultiLine
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims only a matches.");
        return Regex.IsMatch(s, "^a$", RegexOptions.Multiline);
    }
}
