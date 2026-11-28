namespace Verifiable.Core;

/// <summary>
/// The answer Z3 gave to a refutation query (<c>premises ∧ ¬goal</c>), or the reason it gave none.
/// </summary>
[Verifiable]
internal enum SmtStatus
{
    /// <summary>No assignment satisfies the premises and the negated goal: the goal is proved.</summary>
    Unsat = 0,

    /// <summary>Z3 found an assignment that breaks the goal: the goal is refuted.</summary>
    Sat = 1,

    /// <summary>Z3 gave up or timed out.</summary>
    Unknown = 2,

    /// <summary>No Z3 executable was found, so nothing was checked.</summary>
    SolverUnavailable = 3,

    /// <summary>Z3 rejected the query or crashed.</summary>
    Error = 4
}

/// <summary>
/// What a single line of Z3 output says about a <c>(check-sat)</c>.
/// </summary>
[Verifiable]
internal enum SolverLine
{
    /// <summary>The line carries no verdict (blank, a warning, an echo, ...).</summary>
    Noise = 0,

    /// <summary>Exactly <c>unsat</c>.</summary>
    Unsat = 1,

    /// <summary>Exactly <c>sat</c>.</summary>
    Sat = 2,

    /// <summary>Exactly <c>unknown</c>.</summary>
    Unknown = 3,

    /// <summary>An <c>(error ...)</c> form.</summary>
    Error = 4
}
