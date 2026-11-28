// (Shade)raw is checked when passed as a bare closed-enum argument, but not when wrapped in a record first.
// @expect: rejected
// @witness: Corpus.Types.ClosedEnumInRecord.Caller(7)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public enum Shade { Dark, Light }

public readonly record struct LightState(Shade Shade);

[Verifiable]
public static class ClosedEnumInRecord
{
    public static int Code(LightState s)
    {
        Spec.Ensures<int>(r => r == 0 || r == 1, "Codes are 0 or 1.");
        var v = (int)s.Shade;
        return v;
    }

    public static int Caller(int raw)
    {
        Spec.Requires(raw >= 0 && raw <= 10, "Small raw values.");
        Spec.Ensures<int>(r => r == 0 || r == 1, "Codes are 0 or 1.");
        return Code(new LightState((Shade)raw));
    }
}
