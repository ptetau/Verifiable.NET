namespace Verifiable.Core;

/// <summary>
/// The text that goes around a solver call: the key a cached answer is filed under, and the named
/// assertions of a <c>verifiable-query</c> script. A mistake here can replay the answer of one query for
/// another, or slip an extra assumption into a query, so the exact text is fixed by contract.
/// </summary>
/// <remarks>
/// The cache key is a sequence of length-prefixed fields. Each field starts with its length written as two
/// UTF-16 code units (the high and the low 16 bits), so a reader always knows where the field ends, whatever
/// characters the field holds. <c>Proofs/CacheKeyLaws.cs</c> proves that a key determines its fields.
/// </remarks>
internal static class SolverPlumbingKernel
{
    /// <summary>
    /// One length-prefixed field: the length of <paramref name="text"/> as two UTF-16 code units, high 16 bits
    /// first, then the text itself.
    /// </summary>
    internal static string CacheKeyField(string text)
    {
        Spec.Ensures<string>(
            r => r == ((char)(text.Length / 65536)).ToString() + ((char)(text.Length % 65536)).ToString() + text,
            "A field is exactly its length, as two fixed-width code units, followed by its text, so the text cannot run into the next field.");
        Spec.Requires(text is not null, "Key fields are never null.");

        return ((char)(text.Length / 65536)).ToString() + ((char)(text.Length % 65536)).ToString() + text;
    }

    /// <summary>
    /// One entry of the list of expressions evaluated on a counterexample: its key field, then its expression field.
    /// </summary>
    internal static string CacheKeyEvalEntry(string key, string expression)
    {
        Spec.Ensures<string>(
            r => r == ((char)(key.Length / 65536)).ToString() + ((char)(key.Length % 65536)).ToString() + key +
                      ((char)(expression.Length / 65536)).ToString() + ((char)(expression.Length % 65536)).ToString() + expression,
            "An eval entry is exactly the key field followed by the expression field.");
        Spec.Requires(key is not null && expression is not null, "Eval keys and expressions are never null.");

        return CacheKeyField(key) + CacheKeyField(expression);
    }

    /// <summary>
    /// The text a cached solver answer is filed under. <paramref name="evals"/> is the concatenation of
    /// <see cref="CacheKeyEvalEntry"/> for each expression evaluated on a counterexample, in order.
    /// </summary>
    internal static string CacheKeyMaterial(string solverIdentity, string mode, string timeout, string script, string evals)
    {
        Spec.Ensures<string>(
            r => r == ((char)(solverIdentity.Length / 65536)).ToString() + ((char)(solverIdentity.Length % 65536)).ToString() + solverIdentity +
                      ((char)(mode.Length / 65536)).ToString() + ((char)(mode.Length % 65536)).ToString() + mode +
                      ((char)(timeout.Length / 65536)).ToString() + ((char)(timeout.Length % 65536)).ToString() + timeout +
                      ((char)(script.Length / 65536)).ToString() + ((char)(script.Length % 65536)).ToString() + script +
                      ((char)(evals.Length / 65536)).ToString() + ((char)(evals.Length % 65536)).ToString() + evals,
            "The key is exactly the solver, the mode, the timeout, the script and the evaluated expressions, each as one length-prefixed field, so two different queries cannot share a key.");
        Spec.Requires(
            solverIdentity is not null && mode is not null && timeout is not null && script is not null && evals is not null,
            "Key parts are never null.");

        return CacheKeyField(solverIdentity) + CacheKeyField(mode) + CacheKeyField(timeout) +
               CacheKeyField(script) + CacheKeyField(evals);
    }

    /// <summary>
    /// One labelled premise of a <c>verifiable-query</c> script, so the unsat core can name it.
    /// </summary>
    internal static string NamedAssertion(string formula, string name)
    {
        Spec.Ensures<string>(
            r => r == "(assert (! " + formula + " :named " + name + "))\n",
            "A labelled premise asserts exactly its formula and nothing else.");
        Spec.Requires(formula is not null && name is not null, "Premises and their labels are never null.");

        return "(assert (! " + formula + " :named " + name + "))\n";
    }
}
