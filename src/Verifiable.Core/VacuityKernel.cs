namespace Verifiable.Core;

/// <summary>
/// What Z3 says about one callee guarantee at a call that assumes it.
/// </summary>
[Verifiable]
internal enum GuaranteeVerdict
{
    /// <summary>Some input that reaches the call also satisfies the guarantee.</summary>
    CanHold = 0,

    /// <summary>
    /// Inputs reach the call, but none of them satisfies the guarantee. Every goal that relies on it
    /// is "proved" without meaning anything.
    /// </summary>
    Impossible = 1,

    /// <summary>No input reaches the call. This is dead code, not a contradiction.</summary>
    Unreachable = 2,

    /// <summary>Z3 could not decide one of the checks. Never counted as possible or impossible.</summary>
    Undecided = 3
}

/// <summary>
/// Rules for finding proofs that are vacuous on one path: a callee guarantee that cannot hold where it
/// is called makes every later goal on that path "proved".
/// </summary>
/// <remarks>
/// For each guarantee the analyzer asks Z3 two questions, both over the assumptions made before the
/// call: can the call be reached with the guarantee holding, and can it be reached at all. Only the
/// answers "no" and then "yes" make a guarantee impossible.
/// </remarks>
internal static class VacuityKernel
{
    /// <summary>
    /// Builds the first check: the earlier assumptions, then what is known at the call (the path to it
    /// and the callee's preconditions), then the guarantee. <c>unsat</c> means the guarantee cannot hold there.
    /// </summary>
    internal static string GuaranteeQuery(string prefix, string context, string guarantee)
    {
        Spec.Ensures<string>(
            r => r == prefix + "\n(assert " + context + ")\n(assert " + guarantee + ")\n",
            "The first check is exactly the earlier assumptions, the call's context and the guarantee, so unsat means the guarantee itself cannot hold at the call.");
        Spec.Requires(
            prefix is not null && context is not null && guarantee is not null,
            "Query parts are produced by the lowerer and are never null.");

        return prefix + "\n(assert " + context + ")\n(assert " + guarantee + ")\n";
    }

    /// <summary>
    /// Builds the second check: the first check without the guarantee. <c>sat</c> means the call is reachable.
    /// </summary>
    internal static string ReachabilityQuery(string prefix, string context)
    {
        Spec.Ensures<string>(
            r => r == prefix + "\n(assert " + context + ")\n",
            "The second check is exactly the first one without the guarantee, so sat means some input reaches the call.");
        Spec.Requires(
            prefix is not null && context is not null,
            "Query parts are produced by the lowerer and are never null.");

        return prefix + "\n(assert " + context + ")\n";
    }

    /// <summary>
    /// Whether the second check is needed. It only matters when the guarantee cannot hold at the call.
    /// </summary>
    internal static bool NeedsReachabilityCheck(SmtStatus withGuarantee)
    {
        Spec.Ensures<bool>(
            r => r == (withGuarantee == SmtStatus.Unsat),
            "Reachability is checked only when Z3 showed the guarantee cannot hold at the call.");

        return withGuarantee == SmtStatus.Unsat;
    }

    /// <summary>
    /// Combines the two checks. When the second check was not needed, pass the first answer for it.
    /// </summary>
    internal static GuaranteeVerdict ClassifyGuarantee(SmtStatus withGuarantee, SmtStatus withoutGuarantee)
    {
        Spec.Ensures<GuaranteeVerdict>(
            r => (r == GuaranteeVerdict.Impossible) ==
                 (withGuarantee == SmtStatus.Unsat && withoutGuarantee == SmtStatus.Sat),
            "A guarantee is impossible only when Z3 shows that no input reaching the call satisfies it and that some input does reach the call.");
        Spec.Ensures<GuaranteeVerdict>(
            r => (r == GuaranteeVerdict.CanHold) == (withGuarantee == SmtStatus.Sat),
            "A guarantee can hold only when Z3 found an input that reaches the call and satisfies it; an undecided check is not that.");
        Spec.Ensures<GuaranteeVerdict>(
            r => (r == GuaranteeVerdict.Unreachable) ==
                 (withGuarantee == SmtStatus.Unsat && withoutGuarantee == SmtStatus.Unsat),
            "A call is unreachable only when Z3 shows no input reaches it; that is dead code, not a contradiction.");

        if (withGuarantee == SmtStatus.Sat) return GuaranteeVerdict.CanHold;
        if (withGuarantee != SmtStatus.Unsat) return GuaranteeVerdict.Undecided;
        if (withoutGuarantee == SmtStatus.Sat) return GuaranteeVerdict.Impossible;
        if (withoutGuarantee == SmtStatus.Unsat) return GuaranteeVerdict.Unreachable;
        return GuaranteeVerdict.Undecided;
    }

    /// <summary>
    /// Whether a premise check left the question open. Then the proofs it guards may be vacuous, and the
    /// analyzer says so (<c>VER106</c>) instead of staying silent.
    /// </summary>
    internal static bool CheckUndecided(SmtStatus check)
    {
        Spec.Ensures<bool>(
            r => r == (check != SmtStatus.Sat && check != SmtStatus.Unsat),
            "Any answer other than sat or unsat leaves open whether the premises can hold, and must be reported.");

        return check != SmtStatus.Sat && check != SmtStatus.Unsat;
    }
}
