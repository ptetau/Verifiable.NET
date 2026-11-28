// The generated primary constructor does not check IsValid, so new TodoId(0) reaches a callee that assumes it.
// @expect: rejected
// @witness: Corpus.Types.CtorArg.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Todo IDs start at 1.")]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class CtorArg
{
    public static int Raw(TodoId id)
    {
        Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(new TodoId(x));
    }
}
