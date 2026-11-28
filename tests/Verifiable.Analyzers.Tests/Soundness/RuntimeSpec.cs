using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>Thrown when the method under test was called with inputs its own <c>Spec.Requires</c> rules out.</summary>
public sealed class InputRejectedException() : Exception("The input does not meet the method's preconditions.");

/// <summary>Thrown when running code breaks a contract the analyzer accepted, such as a callee's precondition.</summary>
public sealed class ContractBrokenException(string message) : Exception(message);

/// <summary>
/// A runtime stand-in for <c>Spec</c>. The corpus runner rewrites a program's <c>Spec.*</c> calls to call this class
/// instead, so contracts the analyzer reasons about are also checked when the code really runs.
/// </summary>
/// <remarks>
/// Every method opens a frame on entry. In the outermost frame (the method under test), a false <c>Requires</c>
/// means the input is out of scope and the run is discarded. In any deeper frame it means the caller broke a
/// callee's precondition, which the analyzer should have caught. <c>Ensures&lt;T&gt;</c> predicates of the
/// outermost frame are kept for the runner to apply to the return value. Loop invariants and <c>Decreases</c> are
/// checked in every frame. The runner learns which methods a run entered, so it can set aside runs that went
/// through a method the analyzer already rejected.
/// </remarks>
public static class RuntimeSpec
{
    /// <summary>Deeper than this, a run is reported as runaway recursion instead of overflowing the stack.</summary>
    public const int MaxDepth = 2000;

    [ThreadStatic] private static Stack<Frame>? s_frames;
    [ThreadStatic] private static List<(string Because, Delegate Predicate)>? s_postconditions;
    [ThreadStatic] private static HashSet<string>? s_entered;

    /// <summary>Starts a run of the method under test.</summary>
    internal static void BeginRun()
    {
        s_frames = new Stack<Frame>();
        s_postconditions = new List<(string, Delegate)>();
        s_entered = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>The <c>Ensures&lt;T&gt;</c> predicates the method under test declared in this run.</summary>
    internal static IReadOnlyList<(string Because, Delegate Predicate)> Postconditions =>
        s_postconditions ?? (IReadOnlyList<(string, Delegate)>)Array.Empty<(string, Delegate)>();

    /// <summary>The keys of every method this run entered.</summary>
    internal static IReadOnlyCollection<string> Entered =>
        s_entered ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    public static IDisposable Enter(string key, [CallerMemberName] string member = "")
    {
        var depth = (s_frames?.Count ?? 0) + 1;
        if (depth > MaxDepth)
        {
            throw new ContractBrokenException($"recursed more than {MaxDepth} calls deep in {member}, so it may never return");
        }

        var frame = new Frame(member, depth);
        (s_frames ??= new Stack<Frame>()).Push(frame);
        s_entered?.Add(key);
        return frame;
    }

    private static Frame? Current => s_frames is { Count: > 0 } f ? f.Peek() : null;
    private static bool Outermost => Current is null || Current.Depth == 1;

    public static void Requires(bool condition, string? because = null)
    {
        if (condition) return;
        if (Outermost) throw new InputRejectedException();
        throw new ContractBrokenException($"{Current!.Member} was called without meeting its precondition: {because ?? "(no reason given)"}");
    }

    public static void RequiresWhen(bool condition, bool requirement, string? because = null) =>
        Requires(!condition || requirement, because);

    public static void Ensures<TResult>(Func<TResult, bool> predicate, string? because = null)
    {
        if (Outermost) s_postconditions?.Add((because ?? "(no reason given)", predicate));
    }

    public static void EnsuresWhen<TResult>(bool condition, Func<TResult, bool> predicate, string? because = null)
    {
        if (condition) Ensures(predicate, because);
    }

    // Postconditions written with Spec.Result<T>() cannot be evaluated before the method returns, so they are not checked.
    public static void Ensures(bool condition, string? because = null) { }

    public static void EnsuresWhen(bool condition, bool postcondition, string? because = null) { }

    /// <summary>
    /// A loop invariant, checked each time it is reached. The runner passes the source span of the enclosing loop
    /// (<paramref name="loopStart"/>, <paramref name="loopEnd"/>) so that loops nested inside it can start afresh.
    /// </summary>
    public static void LoopInvariant(bool condition, string? because = null, int loopStart = -1, int loopEnd = -1, [CallerLineNumber] int line = 0)
    {
        Current?.ForgetLoopsInside(loopStart, loopEnd);
        if (!condition)
        {
            throw new ContractBrokenException($"The loop invariant on line {line} of {Current?.Member} is false at run time: {because ?? "(no reason given)"}");
        }
    }

    public static void Decreases(long measure, string? because = null, int loopStart = -1, int loopEnd = -1, [CallerLineNumber] int line = 0)
    {
        var frame = Current;
        if (frame is null) return;
        if (measure < 0)
        {
            throw new ContractBrokenException($"The Decreases measure on line {line} of {frame.Member} is negative ({measure}): {because ?? "(no reason given)"}");
        }

        frame.ForgetLoopsInside(loopStart, loopEnd);
        var key = (loopStart, line);
        if (frame.LastMeasure.TryGetValue(key, out var last) && measure >= last.Measure)
        {
            throw new ContractBrokenException($"The Decreases measure on line {line} of {frame.Member} went from {last.Measure} to {measure} instead of going down: {because ?? "(no reason given)"}");
        }

        frame.LastMeasure[key] = (measure, loopEnd);
    }

    public static void Decreases(int measure, string? because = null, int loopStart = -1, int loopEnd = -1, [CallerLineNumber] int line = 0) =>
        Decreases((long)measure, because, loopStart, loopEnd, line);

    private sealed class Frame(string member, int depth) : IDisposable
    {
        public string Member { get; } = member;
        public int Depth { get; } = depth;

        /// <summary>The last measure of each <c>Decreases</c>, keyed by its loop's start and its line.</summary>
        public Dictionary<(int LoopStart, int Line), (long Measure, int LoopEnd)> LastMeasure { get; } = new();

        /// <summary>A loop nested inside the given one starts a new run of iterations on each outer iteration.</summary>
        public void ForgetLoopsInside(int loopStart, int loopEnd)
        {
            if (loopStart < 0) return;
            foreach (var key in new List<(int, int)>(LastMeasure.Keys))
            {
                var end = LastMeasure[key].LoopEnd;
                var start = key.Item1;
                if (start >= loopStart && end <= loopEnd && (start, end) != (loopStart, loopEnd)) LastMeasure.Remove(key);
            }
        }

        public void Dispose()
        {
            if (s_frames is { Count: > 0 } && ReferenceEquals(s_frames.Peek(), this)) s_frames.Pop();
        }
    }
}
