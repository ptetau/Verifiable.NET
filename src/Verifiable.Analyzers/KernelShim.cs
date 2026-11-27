using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Verifiable.Analyzers.KernelShim
{
    // The analyzer targets netstandard2.0 and cannot reference Verifiable.Contracts, so the kernel sources
    // linked from src/Verifiable.Core bind their Spec.* calls and [Verifiable] markers to these stand-ins.
    // Every Spec method is [Conditional], so the calls are removed from IL exactly as in user code.
    // The contracts themselves are proved when src/Verifiable.Core builds against the real Verifiable.Contracts.

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class VerifiableAttribute : Attribute
    {
    }

    internal static class Spec
    {
        [Conditional("VERIFIABLE_SPEC")]
        public static void Requires([DoesNotReturnIf(false)] bool condition, string? because = null) { _ = condition; _ = because; }

        [Conditional("VERIFIABLE_SPEC")]
        public static void Ensures<TResult>(Func<TResult, bool> predicate, string? because = null) { _ = predicate; _ = because; }

        [Conditional("VERIFIABLE_SPEC")]
        public static void LoopInvariant(bool condition, string? because = null) { _ = condition; _ = because; }

        [Conditional("VERIFIABLE_SPEC")]
        public static void Decreases(int measure, string? because = null) { _ = measure; _ = because; }

        public static bool Implies(bool antecedent, bool consequent) => !antecedent || consequent;

        public static bool ForAll(int startInclusive, int endExclusive, Func<int, bool> predicate)
        {
            for (var i = startInclusive; i < endExclusive; i++)
            {
                if (!predicate(i)) return false;
            }
            return true;
        }

        public static bool Exists(int startInclusive, int endExclusive, Func<int, bool> predicate)
        {
            for (var i = startInclusive; i < endExclusive; i++)
            {
                if (predicate(i)) return true;
            }
            return false;
        }
    }
}

// netstandard2.0 lacks this nullable-analysis attribute; the compiler recognises it by name. It lets the
// C# null-state analysis know that code after Spec.Requires(x is not null) sees a non-null x, which
// src/Verifiable.Core proves with Z3.
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class DoesNotReturnIfAttribute : Attribute
    {
        public DoesNotReturnIfAttribute(bool parameterValue) => ParameterValue = parameterValue;

        public bool ParameterValue { get; }
    }
}
