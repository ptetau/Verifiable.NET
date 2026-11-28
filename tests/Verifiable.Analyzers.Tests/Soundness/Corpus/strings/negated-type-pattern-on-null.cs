// `x is not int` is true when the int? is empty, so the result is not always false.
// @expect: rejected
// @witness: Corpus.Strings.TypePattern.IsEmpty(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class TypePattern
{
    public static bool IsEmpty(int? x)
    {
        Spec.Ensures<bool>(r => !r, "Claims an int? is always an int.");
        return x is not int;
    }
}
