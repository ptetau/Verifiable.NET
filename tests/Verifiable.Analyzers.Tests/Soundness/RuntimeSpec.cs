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
/// Each method that has contracts opens a frame on entry. In the outermost frame (the method under test), a false
/// <c>Requires</c> means the input is out of scope and the run is discarded. In any deeper frame it means the caller
/// broke a callee's precondition, which the analyzer should have caught. <c>Ensures&lt;T&gt;</c> predicates of the
/// outermost frame are kept for the runner to apply to the return value. Loop invariants and <c>Decreases</c> are
/// checked in every frame.
/// </remarks>
public static class RuntimeSpec
{
    [ThreadStatic] private static Stack<Frame>? s_frames;
    [ThreadStatic] private static List<(string Because, Delegate Predicate)>? s_postconditions;

    /// <summary>Starts a run of the method under test.</summary>
    internal static void BeginRun()
    {
        s_frames = new Stack<Frame>();
        s_postconditions = new List<(string, Delegate)>();
    }

    /// <summary>The <c>Ensures&lt;T&gt;</c> predicates the method under test declared in this run.</summary>
    internal static IReadOnlyList<(string Because, Delegate Predicate)> Postconditions =>
        s_postconditions ?? (IReadOnlyList<(string, Delegate)>)Array.Empty<(string, Delegate)>();

    public static IDisposable Enter([CallerMemberName] string member = "")
    {
        var frame = new Frame(member, (s_frames?.Count ?? 0) + 1);
        (s_frames ??= new Stack<Frame>()).Push(frame);
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

    public static void LoopInvariant(bool condition, string? because = null, [CallerLineNumber] int line = 0)
    {
        Current?.ForgetMeasuresAfter(line);
        if (!condition)
        {
            throw new ContractBrokenException($"The loop invariant on line {line} of {Current?.Member} is false at run time: {because ?? "(no reason given)"}");
        }
    }

    public static void Decreases(long measure, string? because = null, [CallerLineNumber] int line = 0)
    {
        var frame = Current;
        if (frame is null) return;
        if (measure < 0)
        {
            throw new ContractBrokenException($"The Decreases measure on line {line} of {frame.Member} is negative ({measure}): {because ?? "(no reason given)"}");
        }

        if (frame.LastMeasure.TryGetValue(line, out var last) && measure >= last)
        {
            throw new ContractBrokenException($"The Decreases measure on line {line} of {frame.Member} went from {last} to {measure} instead of going down: {because ?? "(no reason given)"}");
        }

        frame.ForgetMeasuresAfter(line);
        frame.LastMeasure[line] = measure;
    }

    public static void Decreases(int measure, string? because = null, [CallerLineNumber] int line = 0) =>
        Decreases((long)measure, because, line);

    private sealed class Frame(string member, int depth) : IDisposable
    {
        public string Member { get; } = member;
        public int Depth { get; } = depth;
        public Dictionary<int, long> LastMeasure { get; } = new();

        // A loop nested inside this one starts afresh on each outer iteration; contracts of inner loops come later in the file.
        public void ForgetMeasuresAfter(int line)
        {
            foreach (var key in new List<int>(LastMeasure.Keys))
            {
                if (key > line) LastMeasure.Remove(key);
            }
        }

        public void Dispose()
        {
            if (s_frames is { Count: > 0 } && ReferenceEquals(s_frames.Peek(), this)) s_frames.Pop();
        }
    }
}
