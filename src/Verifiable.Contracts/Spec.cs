using System;
using System.Diagnostics;

namespace Verifiable.Contracts;

/// <summary>
/// Type-safe, zero-runtime-overhead compile-time specification calls analyzed by the Verifiable.NET Z3 SMT prover
/// and stripped from compiled IL via <see cref="ConditionalAttribute"/>.
/// </summary>
public static class Spec
{
    /// <summary>
    /// Declares a method precondition that callers inside <c>[Verifiable]</c> scopes must satisfy under their dominant path condition,
    /// and that <c>[InboundPort]</c> wrappers enforce at runtime.
    /// </summary>
    /// <param name="condition">A pure C# boolean expression over method parameters and visible state.</param>
    /// <param name="because">Optional human-readable explanation of the spirit of this requirement.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Requires(bool condition, string? because = null, string? clause = null)
    {
        _ = condition;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Declares a method postcondition over the return value <typeparamref name="TResult"/> that must hold on every return path.
    /// </summary>
    /// <typeparam name="TResult">Return type of the enclosing method.</typeparam>
    /// <param name="predicate">A pure lambda predicate referencing the return value and method inputs.</param>
    /// <param name="because">Optional human-readable explanation of the spirit of this requirement.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Ensures<TResult>(Func<TResult, bool> predicate, string? because = null, string? clause = null)
    {
        _ = predicate;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Declares a method postcondition over the return value via <see cref="Result{TResult}"/> that must hold on every return path.
    /// </summary>
    /// <param name="condition">A pure C# boolean expression referencing <see cref="Result{TResult}"/> and method inputs.</param>
    /// <param name="because">Optional human-readable explanation of the spirit of this requirement.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Ensures(bool condition, string? because = null, string? clause = null)
    {
        _ = condition;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Symbolic placeholder representing the enclosing method's return value inside <see cref="Ensures(bool, string?, string?)"/>.
    /// </summary>
    /// <typeparam name="TResult">Return type of the enclosing method.</typeparam>
    [PureAxiom]
    public static TResult Result<TResult>() => default!;

    /// <summary>
    /// Declares an inductive loop invariant checked on loop entry and preserved across every loop back-edge.
    /// Place it among the first statements of the body of the loop being verified, before any other statement.
    /// </summary>
    /// <param name="condition">A pure C# boolean expression over loop variables.</param>
    /// <param name="because">Optional human-readable explanation of why this invariant holds.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void LoopInvariant(bool condition, string? because = null, string? clause = null)
    {
        _ = condition;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Declares a non-negative integer ranking expression that strictly decreases on every iteration of the enclosing loop.
    /// Every <c>while</c>, <c>do</c> and <c>for</c> loop needs one, placed among the first statements of its body.
    /// At the top of a recursive method it is the measure that goes down at every recursive call.
    /// </summary>
    /// <param name="measure">A non-negative <see cref="int"/> expression that strictly decreases per iteration.</param>
    /// <param name="because">Optional human-readable explanation of why this loop terminates.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Decreases(int measure, string? because = null, string? clause = null)
    {
        _ = measure;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Declares a conditional method precondition that must hold whenever <paramref name="condition"/> is true.
    /// Equivalent to <c>Spec.Requires(Spec.Implies(condition, requirement), because)</c>.
    /// </summary>
    /// <param name="condition">Guard condition under which <paramref name="requirement"/> applies.</param>
    /// <param name="requirement">A pure C# boolean expression that must hold when <paramref name="condition"/> is true.</param>
    /// <param name="because">Optional human-readable explanation of the spirit of this requirement.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void RequiresWhen(bool condition, bool requirement, string? because = null, string? clause = null)
    {
        _ = condition;
        _ = requirement;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Declares a conditional method postcondition over the return value <typeparamref name="TResult"/>
    /// that must hold on every return path where <paramref name="condition"/> was true at method entry.
    /// </summary>
    /// <typeparam name="TResult">Return type of the enclosing method.</typeparam>
    /// <param name="condition">Guard condition over method inputs under which <paramref name="predicate"/> must hold.</param>
    /// <param name="predicate">A pure lambda predicate referencing the return value and method inputs.</param>
    /// <param name="because">Optional human-readable explanation of the spirit of this requirement.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void EnsuresWhen<TResult>(bool condition, Func<TResult, bool> predicate, string? because = null, string? clause = null)
    {
        _ = condition;
        _ = predicate;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Declares a conditional method postcondition over <see cref="Result{TResult}"/>
    /// that must hold on every return path where <paramref name="condition"/> was true at method entry.
    /// </summary>
    /// <param name="condition">Guard condition over method inputs under which <paramref name="postcondition"/> must hold.</param>
    /// <param name="postcondition">A pure C# boolean expression referencing <see cref="Result{TResult}"/> and method inputs.</param>
    /// <param name="because">Optional human-readable explanation of the spirit of this requirement.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void EnsuresWhen(bool condition, bool postcondition, string? because = null, string? clause = null)
    {
        _ = condition;
        _ = postcondition;
        _ = because;
        _ = clause;
    }

    /// <summary>
    /// Logical implication (<c>antecedent =&gt; consequent</c>). Lowered directly to SMT-LIB <c>(=&gt; antecedent consequent)</c>
    /// and equivalent to <c>!antecedent || consequent</c> at runtime.
    /// </summary>
    /// <param name="antecedent">Condition that, when true, requires <paramref name="consequent"/> to be true.</param>
    /// <param name="consequent">Consequence that must hold whenever <paramref name="antecedent"/> is true.</param>
    [PureAxiom]
    public static bool Implies(bool antecedent, bool consequent) => !antecedent || consequent;

    /// <summary>
    /// Quantifies universally over every integer index <c>i</c> in the half-open range
    /// <c>[startInclusive, endExclusive)</c>. Lowered by Z3 to a bounded <c>forall</c> quantifier.
    /// </summary>
    /// <param name="startInclusive">Inclusive lower index bound.</param>
    /// <param name="endExclusive">Exclusive upper index bound.</param>
    /// <param name="predicate">Pure predicate evaluated for each index <c>i</c>.</param>
    [PureAxiom]
    public static bool ForAll(int startInclusive, int endExclusive, Func<int, bool> predicate)
    {
        for (var i = startInclusive; i < endExclusive; i++)
        {
            if (!predicate(i)) return false;
        }
        return true;
    }

    /// <summary>
    /// Quantifies existentially over integer indices <c>i</c> in the half-open range
    /// <c>[startInclusive, endExclusive)</c>. Lowered by Z3 to a bounded <c>exists</c> quantifier.
    /// </summary>
    /// <param name="startInclusive">Inclusive lower index bound.</param>
    /// <param name="endExclusive">Exclusive upper index bound.</param>
    /// <param name="predicate">Pure predicate evaluated for each index <c>i</c>.</param>
    [PureAxiom]
    public static bool Exists(int startInclusive, int endExclusive, Func<int, bool> predicate)
    {
        for (var i = startInclusive; i < endExclusive; i++)
        {
            if (predicate(i)) return true;
        }
        return false;
    }

    /// <summary>
    /// Declares a non-negative 64-bit integer ranking expression that strictly decreases on every iteration of the enclosing loop.
    /// </summary>
    /// <param name="measure">A non-negative <see cref="long"/> expression that strictly decreases per iteration.</param>
    /// <param name="because">Optional human-readable explanation of why this loop terminates.</param>
    /// <param name="clause">Optional ID of the spec clause this contract came from (for example <c>"C2"</c>). It must be a constant string. It never changes a proof; the build uses it to report which clauses have code.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Decreases(long measure, string? because = null, string? clause = null)
    {
        _ = measure;
        _ = because;
        _ = clause;
    }
}
