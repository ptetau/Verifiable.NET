// Calling Equals through IEquatable<string> on a null string throws NullReferenceException.
// @expect: rejected
// @witness: Corpus.Strings.EquatableNull.Same(null, null)
using System;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class EquatableNull
{
    public static bool Same(string? a, string? b)
    {
        Spec.Ensures<bool>(r => r == (a == b), "Equals agrees with ==.");
        IEquatable<string?> e = a!;
        return e.Equals(b);
    }
}
