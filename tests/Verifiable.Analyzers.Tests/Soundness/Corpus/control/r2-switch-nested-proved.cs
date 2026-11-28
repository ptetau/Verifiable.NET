// Nested switch expressions that are exhaustive together.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class NestedSwitch
{
    public static int Run(int x, int y)
    {
        Spec.Ensures<int>(r => r >= 0 && r <= 2, "A small class.");
        return x switch
        {
            > 0 => y switch
            {
                > 0 => 1,
                <= 0 => 2,
            },
            _ => 0,
        };
    }
}
