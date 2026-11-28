// "< 10 or > 30" also catches 40, which the contract says belongs to the top band (2).
// @expect: rejected
// @witness: Corpus.Control.BandWrong.Band(40)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class BandWrong
{
    public static int Band(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small inputs.");
        Spec.Ensures<int>(r => (x < 10 && r == 0) || (x >= 10 && x < 20 && r == 1) || (x >= 20 && r == 2), "Bands.");
        int r;
        switch (x)
        {
            case < 10 or > 30:
                r = 0;
                break;
            case >= 10 and < 20:
                r = 1;
                break;
            default:
                r = 2;
                break;
        }
        return r;
    }
}
