// default(Seven) does not run the C# 10 parameterless constructor, so X is 0, not 7.
// @expect: rejected
// @witness: Corpus.Types.StructDefault.Elem()
using Verifiable.Contracts;

namespace Corpus.Types;

public struct Seven
{
    public int X;
    public Seven() { X = 7; }
}

[Verifiable]
public static class StructDefault
{
    public static int Elem()
    {
        Spec.Ensures<int>(r => r == 7, "X is 7.");
        Seven s = default;
        return s.X;
    }
}
