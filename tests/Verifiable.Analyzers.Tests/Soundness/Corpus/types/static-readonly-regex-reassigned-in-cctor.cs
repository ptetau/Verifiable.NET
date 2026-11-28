// The static constructor replaces the Regex in the static readonly field, so the initializer's pattern is not the one used.
// @expect: rejected
// @witness: Corpus.Types.RegexField.IsDigits("abc")
// @known-bug: A static readonly Regex field is modelled from its initializer's pattern, ignoring a static constructor that reassigns it.
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Types;

public static class Patterns
{
    public static readonly Regex Digits = new Regex("^[0-9]+$");

    static Patterns()
    {
        Digits = new Regex("^[a-z]+$");
    }
}

[Verifiable]
public static class RegexField
{
    public static bool IsDigits(string s)
    {
        Spec.Requires(s != null, "Non-null input.");
        Spec.Ensures<bool>(r => !r || !s.Contains("a"), "A digit string contains no letter a.");
        return Patterns.Digits.IsMatch(s);
    }
}
