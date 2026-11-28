namespace Verifiable.Core.Proofs;

/// <summary>
/// Lemmas about the cache key built by <see cref="SolverPlumbingKernel"/>. Each says "equal keys come from
/// equal inputs"; Z3 checks it for all strings when the project builds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FieldThenRestDecodesUniquely"/> is the step that makes any sequence of fields readable in only
/// one way: if two sequences are equal as text, their first fields are equal and so are the rests. Applying
/// it once per entry shows that a whole list of eval entries decodes uniquely, so the list of evaluated
/// expressions inside a key is also fixed by the key.
/// </para>
/// <para>
/// Each lemma states that the strings are at most <c>int.MaxValue</c> long. That is always true in C#, but
/// Z3's strings are unbounded, and the two-code-unit length header only covers lengths below 2^32.
/// </para>
/// </remarks>
internal static class CacheKeyLaws
{
    internal static bool FieldThenRestDecodesUniquely(string a, string restA, string b, string restB)
    {
        Spec.Ensures<bool>(r => r, "If a field followed by more text equals another field followed by more text, the two fields are equal and so are the rests.");
        Spec.Requires(
            a is not null && restA is not null && b is not null && restB is not null &&
            a.Length <= int.MaxValue && b.Length <= int.MaxValue,
            "C# strings are not null here and hold at most int.MaxValue characters.");

        return Spec.Implies(
            SolverPlumbingKernel.CacheKeyField(a) + restA == SolverPlumbingKernel.CacheKeyField(b) + restB,
            a == b && restA == restB);
    }

    internal static bool EvalEntryThenRestDecodesUniquely(
        string keyA, string exprA, string restA, string keyB, string exprB, string restB)
    {
        Spec.Ensures<bool>(r => r, "If two eval lists are equal as text, their first entries are equal and so are the rests.");
        Spec.Requires(
            keyA is not null && exprA is not null && restA is not null &&
            keyB is not null && exprB is not null && restB is not null &&
            keyA.Length <= int.MaxValue && exprA.Length <= int.MaxValue &&
            keyB.Length <= int.MaxValue && exprB.Length <= int.MaxValue,
            "C# strings are not null here and hold at most int.MaxValue characters.");

        return Spec.Implies(
            SolverPlumbingKernel.CacheKeyEvalEntry(keyA, exprA) + restA == SolverPlumbingKernel.CacheKeyEvalEntry(keyB, exprB) + restB,
            keyA == keyB && exprA == exprB && restA == restB);
    }

    internal static bool CacheKeyIsInjective(
        string solverA, string modeA, string timeoutA, string scriptA, string evalsA,
        string solverB, string modeB, string timeoutB, string scriptB, string evalsB)
    {
        Spec.Ensures<bool>(r => r, "Two queries share a cache key only if they have the same solver, mode, timeout, script and evaluated expressions.");
        Spec.Requires(
            solverA is not null && modeA is not null && timeoutA is not null && scriptA is not null && evalsA is not null &&
            solverB is not null && modeB is not null && timeoutB is not null && scriptB is not null && evalsB is not null &&
            solverA.Length <= int.MaxValue && modeA.Length <= int.MaxValue && timeoutA.Length <= int.MaxValue &&
            scriptA.Length <= int.MaxValue && evalsA.Length <= int.MaxValue &&
            solverB.Length <= int.MaxValue && modeB.Length <= int.MaxValue && timeoutB.Length <= int.MaxValue &&
            scriptB.Length <= int.MaxValue && evalsB.Length <= int.MaxValue,
            "C# strings are not null here and hold at most int.MaxValue characters.");

        return Spec.Implies(
            SolverPlumbingKernel.CacheKeyMaterial(solverA, modeA, timeoutA, scriptA, evalsA) ==
            SolverPlumbingKernel.CacheKeyMaterial(solverB, modeB, timeoutB, scriptB, evalsB),
            solverA == solverB && modeA == modeB && timeoutA == timeoutB && scriptA == scriptB && evalsA == evalsB);
    }
}
