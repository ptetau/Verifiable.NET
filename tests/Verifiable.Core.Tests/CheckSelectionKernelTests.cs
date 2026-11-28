using System;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Core.Tests;

/// <summary>
/// Runtime checks of <see cref="CheckSelectionKernel"/> against a plain reference, over every input that
/// matters: no obligations, one, several, with and without a lowering failure and a known obligation source.
/// </summary>
public class CheckSelectionKernelTests
{
    private static MethodCheck Reference(int obligationCount, bool loweringFailed, bool knownObligationSource) =>
        (loweringFailed, obligationCount > 0, knownObligationSource) switch
        {
            (false, false, _) => MethodCheck.NothingToProve,
            (false, true, _) => MethodCheck.Prove,
            (true, true, _) => MethodCheck.UnsupportedError,
            (true, false, true) => MethodCheck.UnsupportedError,
            (true, false, false) => MethodCheck.UnsupportedWarning
        };

    [Fact]
    public void Select_MatchesReference()
    {
        foreach (var count in new[] { 0, 1, 2, 17, int.MaxValue })
        foreach (var failed in new[] { false, true })
        foreach (var known in new[] { false, true })
        {
            Assert.Equal(Reference(count, failed, known), CheckSelectionKernel.Select(count, failed, known));
        }
    }

    [Fact]
    public void Select_PassesOverSilentlyOnlyWithNothingToProve()
    {
        foreach (var count in new[] { 0, 1, 2 })
        foreach (var failed in new[] { false, true })
        foreach (var known in new[] { false, true })
        {
            var silent = CheckSelectionKernel.Select(count, failed, known) == MethodCheck.NothingToProve;
            Assert.Equal(count == 0 && !failed, silent);
        }
    }
}
