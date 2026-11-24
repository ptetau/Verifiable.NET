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
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Requires(bool condition)
    {
        _ = condition;
    }

    /// <summary>
    /// Declares a method postcondition over the return value <typeparamref name="TResult"/> that must hold on every return path.
    /// </summary>
    /// <typeparam name="TResult">Return type of the enclosing method.</typeparam>
    /// <param name="predicate">A pure lambda predicate referencing the return value and method inputs.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Ensures<TResult>(Func<TResult, bool> predicate)
    {
        _ = predicate;
    }

    /// <summary>
    /// Declares a method postcondition over the return value via <see cref="Result{TResult}"/> that must hold on every return path.
    /// </summary>
    /// <param name="condition">A pure C# boolean expression referencing <see cref="Result{TResult}"/> and method inputs.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Ensures(bool condition)
    {
        _ = condition;
    }

    /// <summary>
    /// Symbolic placeholder representing the enclosing method's return value inside <see cref="Ensures(bool)"/>.
    /// </summary>
    /// <typeparam name="TResult">Return type of the enclosing method.</typeparam>
    [PureAxiom]
    public static TResult Result<TResult>() => default!;

    /// <summary>
    /// Declares an inductive loop invariant checked on loop entry and preserved across every loop back-edge.
    /// Place directly inside the body of the loop being verified.
    /// </summary>
    /// <param name="condition">A pure C# boolean expression over loop variables.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void LoopInvariant(bool condition)
    {
        _ = condition;
    }

    /// <summary>
    /// Declares a non-negative integer ranking expression that strictly decreases on every iteration of the enclosing loop.
    /// </summary>
    /// <param name="measure">A non-negative <see cref="int"/> expression that strictly decreases per iteration.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Decreases(int measure)
    {
        _ = measure;
    }

    /// <summary>
    /// Declares a non-negative 64-bit integer ranking expression that strictly decreases on every iteration of the enclosing loop.
    /// </summary>
    /// <param name="measure">A non-negative <see cref="long"/> expression that strictly decreases per iteration.</param>
    [Conditional("VERIFIABLE_SPEC")]
    [PureAxiom]
    public static void Decreases(long measure)
    {
        _ = measure;
    }
}
