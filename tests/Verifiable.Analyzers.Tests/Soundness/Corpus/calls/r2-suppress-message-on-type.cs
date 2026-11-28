// [SuppressMessage] on the class hides VER102 for every method in it.
// @expect: rejected
// @witness: Corpus.Calls.R2SupType.Next(3)
using System.Diagnostics.CodeAnalysis;
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
[SuppressMessage("Verifiable", "VER102:Postcondition fails")]
public static class R2SupType
{
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
}
