// (Color)7 is a legal C# value outside the closed enum's members, so the callee's switch throws.
// @expect: rejected
// @witness: Corpus.Types.ClosedEnumArg.Caller(7)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public enum Color { Red, Green, Blue }

[Verifiable]
public static class ClosedEnumArg
{
    public static int Code(Color c)
    {
        Spec.Ensures<int>(r => r >= 1 && r <= 3, "Codes are 1 to 3.");
        return c switch
        {
            Color.Red => 1,
            Color.Green => 2,
            Color.Blue => 3,
            _ => throw new System.ArgumentOutOfRangeException(nameof(c)),
        };
    }

    public static int Caller(int raw)
    {
        Spec.Requires(raw >= 0 && raw <= 10, "Small raw values.");
        Spec.Ensures<int>(r => r >= 1 && r <= 3, "Codes are 1 to 3.");
        return Code((Color)raw);
    }
}
