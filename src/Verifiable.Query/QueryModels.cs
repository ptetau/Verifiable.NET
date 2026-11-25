using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Verifiable.Contracts;

namespace Verifiable.Query;

/// <summary>
/// High-level outcome of a formal verification query.
/// </summary>
public enum QueryOutcomeStatus
{
    /// <summary>Claim proved across all reachable paths (exit code 0).</summary>
    Proved = 0,

    /// <summary>Counterexample found on at least one path (exit code 1).</summary>
    Refuted = 1,

    /// <summary>Solver timed out or returned unknown on at least one path (exit code 2).</summary>
    Inconclusive = 2,

    /// <summary>Input source, target resolution, or C# query expression binding failed (exit code 3).</summary>
    Error = 3,

    /// <summary>Z3 SMT solver binary could not be discovered (exit code 4).</summary>
    Z3NotFound = 4
}

/// <summary>
/// Validated per-path Z3 solver timeout in milliseconds.
/// </summary>
[TypeAlias<int>("Z3 query timeout in milliseconds must be strictly positive (1..600000).")]
public readonly partial record struct QueryTimeoutMs
{
    /// <summary>Validates that a solver timeout falls within [1, 600000] milliseconds.</summary>
    public static bool IsValid(int value) => value >= 1 && value <= 600_000;
}

/// <summary>
/// A premise from Z3's unsat core that explains why a claim holds.
/// </summary>
public sealed record QueryReason(
    string Kind,
    string Label,
    string? Spirit,
    string? FilePath,
    int Line);

/// <summary>
/// Concrete counterexample for a single failing return path.
/// </summary>
public sealed record PathCounterexample(
    string ReturnFile,
    int ReturnLine,
    Location ReturnLocation,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Query evaluation result for a single analyzed method.
/// </summary>
public sealed record MethodQueryResult(
    string Method,
    string ReturnType,
    string Claim,
    string? Assume,
    string? Because,
    QueryOutcomeStatus Status,
    bool Vacuous,
    int PathsChecked,
    string? SmtHash,
    Location MethodLocation,
    IReadOnlyList<QueryReason> Reasons,
    IReadOnlyList<QueryReason> TraceSteps,
    IReadOnlyList<PathCounterexample> Counterexamples,
    string? ErrorMessage,
    string? Hint);

/// <summary>
/// Aggregate report across one or more methods or invariant checks.
/// </summary>
public sealed record QueryReport(
    string Command,
    string Claim,
    string? Assume,
    string? Because,
    string? TargetType,
    QueryOutcomeStatus Status,
    IReadOnlyList<MethodQueryResult> Results,
    string? ErrorTitle,
    string? ErrorMessage,
    Location ErrorLocation,
    string? FixHint);

/// <summary>
/// Metadata about a constrained <c>[TypeAlias&lt;T&gt;]</c> or <c>[Refined&lt;T&gt;]</c> struct.
/// </summary>
public sealed record ListedConstrainedType(
    string Name,
    string FullName,
    string UnderlyingType,
    string Predicate,
    string? Description,
    string FilePath,
    int Line);

/// <summary>
/// Metadata about a member decorated with <c>[Invariant]</c>.
/// </summary>
public sealed record ListedInvariant(
    string ContainingType,
    string MemberName,
    string? Description,
    string FilePath,
    int Line);

/// <summary>
/// Metadata about a <c>[Verifiable]</c> method available for querying.
/// </summary>
public sealed record ListedMethod(
    string DisplayName,
    string Signature,
    string ReturnType,
    bool IsInboundPort,
    int RequiresCount,
    int EnsuresCount,
    string FilePath,
    int Line);

/// <summary>
/// Complete inventory of queryable verifiable symbols in a workspace or file set.
/// </summary>
public sealed record SymbolInventory(
    IReadOnlyList<ListedConstrainedType> ConstrainedTypes,
    IReadOnlyList<ListedInvariant> Invariants,
    IReadOnlyList<ListedMethod> Methods);
