// Correct: in [\d-z] the hyphen after a class escape is a literal, so "-" and "z" match but "a" does not.
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ClassRange
{
    public static bool Hyphen(string s)
    {
        Spec.Requires(s == "-", "A hyphen.");
        Spec.Ensures<bool>(r => r, "The hyphen is in the class.");
        return Regex.IsMatch(s, "^[\\d-z]\\z");
    }

    public static bool Letter(string s)
    {
        Spec.Requires(s == "a", "A letter inside the d..z span.");
        Spec.Ensures<bool>(r => !r, "Not a range from \\d to z.");
        return Regex.IsMatch(s, "^[\\d-z]\\z");
    }
}
