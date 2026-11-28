// A correct program that is rejected on purpose: local functions are not allowed in [Verifiable] code (VER005),
// because their bodies are not analysed. The fix is a static method, as in proved-static-helper-shadowing.cs.
// @expect: rejected
// @no-witness: the program is correct; it is refused by design (VER005), so there is nothing to run that breaks a contract.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Shadowing
{
    public static int Run(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == 2 * x + 2, "Doubles x + 1.");
        static int Twice(int x) => x * 2;
        return Twice(x + 1);
    }
}
