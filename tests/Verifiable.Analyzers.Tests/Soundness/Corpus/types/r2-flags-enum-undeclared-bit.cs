// A [Flags] enum parameter can carry bits no member declares (here 4); the mask is not bounded by 3.
// @expect: rejected
// @witness: Corpus.Types.FlagsMask.Mask((Corpus.Types.Perm)4)
using System;
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
[Flags]
public enum Perm { None = 0, R = 1, W = 2 }

[Verifiable]
public static class FlagsMask
{
    public static int Mask(Perm p)
    {
        Spec.Ensures<int>(r => r <= 3, "Only R and W bits.");
        return (int)p;
    }
}
