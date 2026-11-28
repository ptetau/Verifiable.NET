// Correct: a switch expression over string constants, with null handled first.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class StringSwitch
{
    public static int Code(string? s)
    {
        Spec.Ensures<int>(r => r == (s == null ? 0 : s == "a" ? 1 : s == "" ? 2 : 3), "Codes by value.");
        return s switch
        {
            null => 0,
            "a" => 1,
            "" => 2,
            _ => 3
        };
    }
}
