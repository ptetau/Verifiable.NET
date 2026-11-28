// Under ECMAScript, [\w] is ASCII only, so "é" does not match; claim it does.
// @expect: rejected
// @witness: Corpus.Strings.EcmaClass.Is("é")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class EcmaClass
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "é", "e acute.");
        Spec.Ensures<bool>(r => r, "Claims it is a word character.");
        return Regex.IsMatch(s, "^[\\w]\\z", RegexOptions.ECMAScript);
    }
}
