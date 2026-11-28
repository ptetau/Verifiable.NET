// Returning TodoId? skips the IsValid check that a plain TodoId return gets, and the caller trusts the payload.
// @expect: rejected
// @witness: Corpus.Types.NullableReturn.Caller(0)
// @known-bug: Returning TodoId? does not check TodoId.IsValid on the payload, while callers assume it.
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Todo IDs start at 1.")]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class NullableReturn
{
    public static TodoId? Make(int x)
    {
        Spec.Ensures<TodoId?>(r => r.HasValue, "Always has a value.");
        TodoId? id = new TodoId(x);
        return id;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
        var id = Make(x);
        return id.HasValue ? id.Value.Value : 1;
    }
}
