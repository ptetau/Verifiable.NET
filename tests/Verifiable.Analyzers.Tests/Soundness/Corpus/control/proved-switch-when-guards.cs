// Case labels are tried in order, a "when" guard only narrows its own label, and shared labels run one section.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class SwitchGuards
{
    public static int Bucket(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == (x == 1 || x == 2 ? 10 : x > 100 ? 30 : x > 0 ? 20 : 0), "Buckets x.");
        var r = 0;
        switch (x)
        {
            case 1:
            case 2:
                r = 10;
                break;
            case > 0 when x > 100:
                r = 30;
                break;
            case > 0:
                r = 20;
                break;
            default:
                break;
        }
        return r;
    }
}
