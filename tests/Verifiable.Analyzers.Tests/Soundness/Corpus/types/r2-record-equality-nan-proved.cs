// A correct program: record equality uses double.Equals, under which NaN equals NaN.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record FD(double D);

[Verifiable]
public static class RecNan
{
    public static bool Same(double d)
    {
        Spec.Ensures<bool>(r => r == true, "A record equals itself.");
        return new FD(d) == new FD(d);
    }
}
