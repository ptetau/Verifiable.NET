namespace Verifiable.Core;

/// <summary>
/// The overall answer to a <c>verifiable-query</c> claim across every return path of a method.
/// </summary>
[Verifiable]
internal enum QueryVerdict
{
    /// <summary>The claim holds on every reachable return path.</summary>
    Proved = 0,

    /// <summary>The claim holds, but only because no return path is reachable under the premises.</summary>
    ProvedVacuously = 1,

    /// <summary>At least one return path has a counterexample.</summary>
    Refuted = 2,

    /// <summary>No counterexample was found, but at least one path could not be decided.</summary>
    Inconclusive = 3
}

/// <summary>
/// Combines per-path verdicts into one answer for a query.
/// </summary>
internal static class QueryKernel
{
    /// <summary>
    /// Combines per-path results. A counterexample anywhere wins; an undecided path blocks a proof;
    /// a proof with no reachable path is flagged as vacuous.
    /// </summary>
    /// <param name="refuted">Paths where Z3 found a counterexample.</param>
    /// <param name="undecided">Paths where Z3 returned unknown or an error.</param>
    /// <param name="provedReachable">Paths proved where the negated goal was part of the unsat core.</param>
    /// <param name="provedUnreachable">Paths proved only because their premises contradict each other.</param>
    internal static QueryVerdict Aggregate(int refuted, int undecided, int provedReachable, int provedUnreachable)
    {
        Spec.Requires(
            refuted >= 0 && undecided >= 0 && provedReachable >= 0 && provedUnreachable >= 0,
            "Path counts are never negative.");
        Spec.Ensures<QueryVerdict>(
            r => (r == QueryVerdict.Refuted) == (refuted > 0),
            "One counterexample on any path refutes the claim.");
        Spec.Ensures<QueryVerdict>(
            r => Spec.Implies(r == QueryVerdict.Proved || r == QueryVerdict.ProvedVacuously, refuted == 0 && undecided == 0),
            "A claim is only reported as holding when every path was decided and none was refuted.");
        Spec.Ensures<QueryVerdict>(
            r => (r == QueryVerdict.ProvedVacuously) ==
                 (refuted == 0 && undecided == 0 && provedReachable == 0 && provedUnreachable > 0),
            "A proof that never needed the goal on any path is called out as vacuous instead of looking like a real proof.");

        if (refuted > 0) return QueryVerdict.Refuted;
        if (undecided > 0) return QueryVerdict.Inconclusive;
        if (provedReachable == 0 && provedUnreachable > 0) return QueryVerdict.ProvedVacuously;
        return QueryVerdict.Proved;
    }
}
