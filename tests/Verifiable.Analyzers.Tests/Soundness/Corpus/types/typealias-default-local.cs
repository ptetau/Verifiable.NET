// default(TodoId) holds Value 0, which TodoId.IsValid forbids; reading it back cannot give a value >= 1.
// @expect: rejected
// @witness: Corpus.Types.DefaultLocal.Read()
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Todo IDs start at 1.")]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class DefaultLocal
{
    public static int Read()
    {
        Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
        TodoId id = default;
        return id.Value;
    }
}
