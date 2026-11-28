// st.b is changed through a member access, not a plain assignment to st; the invariant st.b == 0 is false.
// @expect: rejected
// @witness: Corpus.Control.TupleField.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class TupleField
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Untouched.");
        var st = (a: 0, b: 0);
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n && st.b == 0, "b untouched.");
            Spec.Decreases(n - i, "i moves toward n.");
            st.b = st.b + 1;
            i = i + 1;
        }
        return st.b;
    }
}
