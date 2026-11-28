// A method mentions itself in its own postcondition: r == F(x). That is trivially true for the model but the body returns something else.
// @expect: rejected
// @witness: Corpus.Calls.R2SelfRef.F(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2SelfRef
{
    public static int F(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r == F(x) && r == 1, "Self-referential claim.");
        return x;
    }
}
