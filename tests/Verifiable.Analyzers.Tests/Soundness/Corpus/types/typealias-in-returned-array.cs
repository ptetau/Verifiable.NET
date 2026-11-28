// Make returns an array holding new TodoId(x) unchecked, and Caller trusts every element of a returned array.
// @expect: rejected
// @witness: Corpus.Types.ReturnedArray.Caller(0)
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Todo IDs start at 1.")]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class ReturnedArray
{
    public static ImmutableArray<TodoId> Make(int x)
    {
        Spec.Ensures<ImmutableArray<TodoId>>(r => !r.IsDefault && r.Length == 1, "One initialized element.");
        var arr = ImmutableArray.Create(new TodoId(x));
        return arr;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
        var ids = Make(x);
        return ids[0].Value;
    }
}
