// Adding to a List<int> while enumerating it throws InvalidOperationException.
// @expect: rejected
// @witness: Corpus.Control.ForeachList.Run(new System.Collections.Generic.List<int> { 1 })
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ForeachList
{
    public static int Run(List<int> xs)
    {
        Spec.Requires(xs.Count <= 5, "Small lists.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        foreach (var v in xs)
        {
            xs.Add(v);
        }
        return 0;
    }
}
