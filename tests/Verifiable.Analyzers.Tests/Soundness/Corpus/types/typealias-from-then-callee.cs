// A correct program: TodoId.From checks IsValid, so the callee's assumption is earned.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Todo IDs start at 1.")]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class FromThenCallee
{
    public static int Raw(TodoId id)
    {
        Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
        var v = id.Value;
        return v;
    }

    public static int Caller(int x)
    {
        Spec.Requires(x >= 1, "Valid raw id.");
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(TodoId.From(x));
    }
}
