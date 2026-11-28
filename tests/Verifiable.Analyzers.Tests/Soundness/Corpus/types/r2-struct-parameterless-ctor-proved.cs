// A correct program: new Seven() runs the C# 10 parameterless constructor.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

public struct SevenOk
{
    public int X;
    public SevenOk() { X = 7; }
}

[Verifiable]
public static class StructNew
{
    public static int Elem()
    {
        Spec.Ensures<int>(r => r == 7, "X is 7.");
        var s = new SevenOk();
        return s.X;
    }
}
