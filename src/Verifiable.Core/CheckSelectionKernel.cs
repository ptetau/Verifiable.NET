namespace Verifiable.Core;

/// <summary>
/// What the analyzer does with one <c>[Verifiable]</c> method after lowering it to SMT.
/// </summary>
[Verifiable]
internal enum MethodCheck
{
    /// <summary>Lowering finished and found nothing to prove. Nothing is reported.</summary>
    NothingToProve = 0,

    /// <summary>Lowering finished. Every obligation it found goes to Z3.</summary>
    Prove = 1,

    /// <summary>
    /// Lowering stopped at code it cannot translate, in a method known to need proofs. <c>VER101</c> is an
    /// error, and the obligations found before the stop still go to Z3.
    /// </summary>
    UnsupportedError = 2,

    /// <summary>
    /// Lowering stopped at code it cannot translate, in a method with no contracts and no obligation found.
    /// <c>VER101</c> is a warning: the method is not shown as proved, but a build without contracts still passes.
    /// </summary>
    UnsupportedWarning = 3
}

/// <summary>
/// Decides which <c>[Verifiable]</c> methods are checked, from what lowering them produced.
/// </summary>
/// <remarks>
/// Every <c>[Verifiable]</c> method is lowered. No method is skipped because of a guess about its source
/// code: a method is passed over in silence only when lowering it finished and found no obligation.
/// </remarks>
internal static class CheckSelectionKernel
{
    /// <summary>
    /// Chooses what to do with a method.
    /// </summary>
    /// <param name="obligationCount">Proof obligations lowering produced (before it stopped, if it failed).</param>
    /// <param name="loweringFailed">Whether lowering stopped at code it could not translate.</param>
    /// <param name="knownObligationSource">
    /// Whether the method calls <c>Spec</c> or contains code known to need a proof. Only consulted when
    /// lowering failed with no obligation found.
    /// </param>
    internal static MethodCheck Select(int obligationCount, bool loweringFailed, bool knownObligationSource)
    {
        Spec.Requires(obligationCount >= 0, "An obligation count is never negative.");
        Spec.Ensures<MethodCheck>(
            r => (r == MethodCheck.NothingToProve) == (!loweringFailed && obligationCount == 0),
            "A method is passed over in silence only when it was lowered completely and has nothing to prove, so a skipped method can never hide an obligation.");
        Spec.Ensures<MethodCheck>(
            r => (r == MethodCheck.Prove) == (!loweringFailed && obligationCount > 0),
            "A fully lowered method with obligations has every one of them sent to Z3.");
        Spec.Ensures<MethodCheck>(
            r => (r == MethodCheck.UnsupportedError) == (loweringFailed && (obligationCount > 0 || knownObligationSource)),
            "Code that cannot be translated is a build error whenever the method is known to need a proof, because part of it would go unchecked.");
        Spec.Ensures<MethodCheck>(
            r => (r == MethodCheck.UnsupportedWarning) == (loweringFailed && obligationCount == 0 && !knownObligationSource),
            "A method with no contracts and no obligation found that cannot be translated is still reported, as a warning, so it never looks proved.");

        if (loweringFailed)
        {
            if (obligationCount > 0 || knownObligationSource) return MethodCheck.UnsupportedError;
            return MethodCheck.UnsupportedWarning;
        }

        if (obligationCount > 0) return MethodCheck.Prove;
        return MethodCheck.NothingToProve;
    }
}
