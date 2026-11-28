// r is a ref alias of a, so writing through r changes a to 5.
// @expect: rejected
// @witness: Corpus.Control.RefAlias.Run(0)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class RefAlias
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 3, "Small inputs.");
        Spec.Ensures<int>(r => r == x, "a keeps its value.");
        var a = x;
        ref var r = ref a;
        r = 5;
        return a;
    }
}
