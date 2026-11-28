// A static readonly Regex can be reassigned in the static constructor; the field's initializer is not the regex used.
// @expect: rejected
// @witness: Corpus.Strings.CtorField.Is("b")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static partial class CtorField
{
    private static readonly Regex Re = new Regex("^a\\z");

    static CtorField()
    {
        Re = new Regex("^b\\z");
    }

    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims the field matches a.");
        return Re.IsMatch(s);
    }
}
