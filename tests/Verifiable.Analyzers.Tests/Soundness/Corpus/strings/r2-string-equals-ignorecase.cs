// OrdinalIgnoreCase equality is not ==.
// @expect: rejected
// @witness: Corpus.Strings.EqIc.Same("a", "A")
using System;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class EqIc
{
    public static bool Same(string a, string b)
    {
        Spec.Requires(a != null && b != null, "Given.");
        Spec.Ensures<bool>(r => !r || a == b, "Claims ignoring case implies equality.");
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
