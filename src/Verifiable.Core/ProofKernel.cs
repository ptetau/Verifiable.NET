namespace Verifiable.Core;

/// <summary>
/// The outcome of checking one proof obligation.
/// </summary>
[Verifiable]
internal enum ObligationVerdict
{
    /// <summary>The obligation holds on every input.</summary>
    Proved = 0,

    /// <summary>Z3 found inputs that break the obligation.</summary>
    Refuted = 1,

    /// <summary>Z3 could not decide (timeout, unknown or error). Never treated as proved.</summary>
    Undecided = 2,

    /// <summary>No solver was available. Never treated as proved.</summary>
    SolverMissing = 3
}

/// <summary>
/// The decisions that make a Verifiable.NET proof sound: how a goal is turned into a solver query,
/// how solver output is read, and how the answer becomes a verdict.
/// </summary>
/// <remarks>
/// Every analyzer, the nullability suppressor and the query CLI route their verdicts through here,
/// so there is exactly one definition of "proved". Each rule below is stated as a contract and is
/// checked by Z3 whenever <c>Verifiable.Core</c> is built.
/// </remarks>
internal static class ProofKernel
{
    /// <summary>The <c>:named</c> label of the negated goal in an unsat-core query.</summary>
    internal const string NegatedGoalName = "query_negated_goal";

    /// <summary>
    /// Turns a solver answer into a verdict. Only <see cref="SmtStatus.Unsat"/> proves an obligation.
    /// </summary>
    internal static ObligationVerdict Decide(SmtStatus status)
    {
        Spec.Ensures<ObligationVerdict>(
            r => (r == ObligationVerdict.Proved) == (status == SmtStatus.Unsat),
            "An obligation is proved if and only if Z3 showed that no input can break it (unsat).");
        Spec.Ensures<ObligationVerdict>(
            r => (r == ObligationVerdict.Refuted) == (status == SmtStatus.Sat),
            "An obligation is refuted if and only if Z3 produced a breaking input (sat).");
        Spec.Ensures<ObligationVerdict>(
            r => (r == ObligationVerdict.SolverMissing) == (status == SmtStatus.SolverUnavailable),
            "A missing solver is reported as missing, never as a pass or a fail.");

        if (status == SmtStatus.Unsat) return ObligationVerdict.Proved;
        if (status == SmtStatus.Sat) return ObligationVerdict.Refuted;
        if (status == SmtStatus.SolverUnavailable) return ObligationVerdict.SolverMissing;
        return ObligationVerdict.Undecided;
    }

    /// <summary>
    /// Whether a solver answer may be cached and replayed. Only definitive answers are cached, so a
    /// timeout or crash is always retried instead of being remembered.
    /// </summary>
    internal static bool IsCacheable(SmtStatus status)
    {
        Spec.Ensures<bool>(
            r => r == (status == SmtStatus.Unsat || status == SmtStatus.Sat),
            "Only definitive solver answers (unsat or sat) may be cached; timeouts and errors must be retried.");

        return status == SmtStatus.Unsat || status == SmtStatus.Sat;
    }

    /// <summary>
    /// Whether an answer read back from the disk cache may stand in for a Z3 call. It must carry a valid
    /// signature for its own query, and be an answer the kernel would have cached in the first place.
    /// </summary>
    internal static bool AcceptCachedAnswer(bool signatureValid, SmtStatus status)
    {
        Spec.Ensures<bool>(
            r => r == (signatureValid && (status == SmtStatus.Unsat || status == SmtStatus.Sat)),
            "A cached answer replaces a proof only if it is signed for this exact query and is a definitive unsat or sat.");

        return signatureValid && IsCacheable(status);
    }

    /// <summary>
    /// Classifies one trimmed line of Z3 output.
    /// </summary>
    internal static SolverLine ClassifyStatusLine(string line)
    {
        Spec.Ensures<SolverLine>(
            r => (r == SolverLine.Unsat) == (line == "unsat"),
            "Only the exact token `unsat` can be read as a proof; anything else (including `unsat` inside other text) cannot.");
        Spec.Ensures<SolverLine>(
            r => (r == SolverLine.Sat) == (line == "sat"),
            "Only the exact token `sat` can be read as a refutation.");
        Spec.Ensures<SolverLine>(
            r => (r == SolverLine.Unknown) == (line == "unknown"),
            "Only the exact token `unknown` is read as an undecided answer.");
        Spec.Ensures<SolverLine>(
            r => (r == SolverLine.Noise) ==
                 (line != "unsat" && line != "sat" && line != "unknown" &&
                  !line.StartsWith("(error", System.StringComparison.Ordinal)),
            "A line is ignored only if it is neither a verdict nor an error, so an error can never be skipped over.");
        Spec.Requires(line is not null, "Z3 output lines are never null.");

        if (line == "unsat") return SolverLine.Unsat;
        if (line == "sat") return SolverLine.Sat;
        if (line == "unknown") return SolverLine.Unknown;
        if (line.StartsWith("(error", System.StringComparison.Ordinal)) return SolverLine.Error;
        return SolverLine.Noise;
    }

    /// <summary>
    /// Maps a verdict-carrying output line to the solver status it reports.
    /// </summary>
    internal static SmtStatus StatusOf(SolverLine line)
    {
        Spec.Requires(line != SolverLine.Noise, "Only lines that carry a verdict have a status.");
        Spec.Ensures<SmtStatus>(
            r => (r == SmtStatus.Unsat) == (line == SolverLine.Unsat),
            "A status of unsat comes only from an `unsat` line.");
        Spec.Ensures<SmtStatus>(
            r => (r == SmtStatus.Sat) == (line == SolverLine.Sat),
            "A status of sat comes only from a `sat` line.");

        if (line == SolverLine.Unsat) return SmtStatus.Unsat;
        if (line == SolverLine.Sat) return SmtStatus.Sat;
        if (line == SolverLine.Unknown) return SmtStatus.Unknown;
        return SmtStatus.Error;
    }

    /// <summary>
    /// Reads the answer to a <c>(check-sat)</c> from Z3's trimmed output lines: the first line that
    /// carries a verdict decides. Output with no verdict line is an error, never a proof.
    /// </summary>
    internal static SmtStatus ReadCheckSatAnswer(System.Collections.Immutable.ImmutableArray<string> lines)
    {
        Spec.Requires(
            !lines.IsDefault && Spec.ForAll(0, lines.Length, k => lines[k] is not null),
            "The output is always split into an initialised array of non-null lines.");
        Spec.Ensures<SmtStatus>(
            r => Spec.Implies(
                r == SmtStatus.Unsat,
                Spec.Exists(0, lines.Length, i =>
                    lines[i] == "unsat" &&
                    Spec.ForAll(0, i, j => lines[j] != "sat" && lines[j] != "unknown" &&
                                           !lines[j].StartsWith("(error", System.StringComparison.Ordinal)))),
            "Unsat is only reported when Z3 printed exactly `unsat` and nothing before it was a different verdict or an error.");

        var i = 0;
        while (i < lines.Length)
        {
            Spec.LoopInvariant(
                i >= 0 && i <= lines.Length &&
                Spec.ForAll(0, i, j => lines[j] != "unsat" && lines[j] != "sat" && lines[j] != "unknown" &&
                                       !lines[j].StartsWith("(error", System.StringComparison.Ordinal)),
                "Every line already passed over carried no verdict.");
            Spec.Decreases(lines.Length - i, "One line is consumed per step.");

            var kind = ClassifyStatusLine(lines[i]);
            if (kind != SolverLine.Noise)
            {
                return StatusOf(kind);
            }

            i = i + 1;
        }

        return SmtStatus.Error;
    }

    /// <summary>
    /// Builds the refutation query for a goal: the premises, then the path condition, then the
    /// negated goal. Z3 answering <c>unsat</c> to this script means the goal holds on that path.
    /// </summary>
    internal static string BuildRefutationQuery(string preamble, string pathCondition, string goal)
    {
        Spec.Ensures<string>(
            r => Spec.Implies(
                pathCondition.Length == 0 || pathCondition == "true",
                r == preamble + "\n(assert (not " + goal + "))\n"),
            "With no path condition, the query is exactly the premises followed by the negated goal: nothing else can be assumed.");
        Spec.Ensures<string>(
            r => Spec.Implies(
                pathCondition.Length > 0 && pathCondition != "true",
                r == preamble + "\n(assert " + pathCondition + ")\n(assert (not " + goal + "))\n"),
            "Otherwise it is exactly the premises, the path condition and the negated goal: nothing else can be assumed, so unsat means the goal holds on that path.");
        Spec.Requires(
            preamble is not null && pathCondition is not null && goal is not null,
            "Query parts are produced by the lowerer and are never null.");

        if (pathCondition.Length == 0 || pathCondition == "true")
        {
            return preamble + "\n(assert (not " + goal + "))\n";
        }

        return preamble + "\n(assert " + pathCondition + ")\n(assert (not " + goal + "))\n";
    }

    /// <summary>
    /// Builds the named negated-goal assertion used by unsat-core queries.
    /// </summary>
    internal static string NamedNegatedGoal(string goal)
    {
        Spec.Ensures<string>(
            r => r == "(assert (! (not " + goal + ") :named " + NegatedGoalName + "))\n",
            "The named assertion is exactly the negated goal, labelled so the unsat core can show whether the goal was needed.");
        Spec.Requires(goal is not null, "Goals are produced by the lowerer and are never null.");

        return "(assert (! (not " + goal + ") :named " + NegatedGoalName + "))\n";
    }
}
