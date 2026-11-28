// An assembly-level [SuppressMessage] with Scope = "member" and a Target hides VER102 for one method.
// @expect: rejected
// @runtime: skip VER107 rejects the program but the suppressed VER102 leaves Next accepted at method level, so the cross-check would report a false proof
// @witness: Corpus.Calls.R2SupAsm.Next(3)
using System.Diagnostics.CodeAnalysis;
using Verifiable.Contracts;

[assembly: SuppressMessage("Verifiable", "VER102", Scope = "member", Target = "~M:Corpus.Calls.R2SupAsm.Next(System.Int32)~System.Int32")]

namespace Corpus.Calls;

[Verifiable]
public static class R2SupAsm
{
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
}
