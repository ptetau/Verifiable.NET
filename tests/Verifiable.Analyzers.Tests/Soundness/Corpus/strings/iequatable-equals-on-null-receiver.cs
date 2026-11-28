// Calling Equals through IEquatable<string> on a null string throws NullReferenceException.
// @expect: rejected
// @known-bug: Equals on a metadata method (here IEquatable<string>.Equals) is lowered as structural = with no non-null receiver obligation (CfgSsaLowerer.cs, method.Name == "Equals")
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
