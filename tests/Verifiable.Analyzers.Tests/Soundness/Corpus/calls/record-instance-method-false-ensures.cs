// An instance method on a record promises N + 1 but returns N + 2; the static wrapper relies on it.
// @expect: rejected
// @witness: Corpus.Calls.RecordCalls.NextOf(1)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public sealed record Counter(int N)
{
    public int Next()
    {
        Spec.Requires(N >= 0 && N <= 1000, "Small counters only.");
        Spec.Ensures(Spec.Result<int>() == N + 1, "Counts up by one.");
        return N + 2;
    }
}

[Verifiable]
public static class RecordCalls
{
    public static int NextOf(int n)
    {
        Spec.Requires(n >= 0 && n <= 1000, "Small counters only.");
        Spec.Ensures<int>(r => r == n + 1, "Counts up by one.");
        return new Counter(n).Next();
    }
}
