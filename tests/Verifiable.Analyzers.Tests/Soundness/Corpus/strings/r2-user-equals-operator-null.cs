// `x == null` calls the user operator (always true here); `x is null` does not.
// @expect: rejected
// @witness: Corpus.Strings.UserEq.IsNullish(new Corpus.Strings.Weird())
using Verifiable.Contracts;

namespace Corpus.Strings;

public sealed class Weird
{
    public static bool operator ==(Weird? a, Weird? b) => true;
    public static bool operator !=(Weird? a, Weird? b) => false;
    public override bool Equals(object? o) => true;
    public override int GetHashCode() => 0;
}

[Verifiable]
public static class UserEq
{
    public static bool IsNullish(Weird x)
    {
        Spec.Requires(x is not null, "Given.");
        Spec.Ensures<bool>(r => !r, "Claims a non-null value is not null.");
        return x == null;
    }
}
