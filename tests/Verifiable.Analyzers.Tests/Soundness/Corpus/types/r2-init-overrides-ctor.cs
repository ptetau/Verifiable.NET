// The constructor sets B = 1 and the object initializer then overwrites it with x.
// @expect: rejected
// @witness: Corpus.Types.InitCtor.Make(2)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Cfg
{
    public int B { get; init; }
    public Cfg() { B = 1; }
}

[Verifiable]
public static class InitCtor
{
    public static int Make(int x)
    {
        Spec.Ensures<int>(r => r == 1, "B is 1.");
        return new Cfg { B = x }.B;
    }
}
