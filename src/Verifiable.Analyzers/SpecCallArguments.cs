using System;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Shared;

/// <summary>
/// Finds the arguments of a <c>Spec.*</c> call by parameter, whether they were written by position or by name
/// (<c>because: "..."</c>, <c>clause: "C1"</c>). Code that reads the syntax of a contract call uses this instead of
/// fixed argument indexes, so an added <c>clause</c> argument never moves what it reads.
/// </summary>
/// <remarks>This one file is compiled into both Verifiable.Analyzers and Verifiable.Generators.</remarks>
internal static class SpecCallArguments
{
    /// <summary>Ordinal of the <c>because</c> parameter of <paramref name="methodName"/>.</summary>
    public static int BecauseOrdinal(string methodName) => methodName is "RequiresWhen" or "EnsuresWhen" ? 2 : 1;

    /// <summary>Ordinal of the <c>clause</c> parameter of <paramref name="methodName"/>.</summary>
    public static int ClauseOrdinal(string methodName) => BecauseOrdinal(methodName) + 1;

    /// <summary>
    /// The argument expression for the parameter at <paramref name="ordinal"/> of the <c>Spec</c> method
    /// <paramref name="methodName"/>, or <see langword="null"/> when the call omits it.
    /// </summary>
    public static ExpressionSyntax? Get(InvocationExpressionSyntax call, string methodName, int ordinal)
    {
        var names = ParameterNames(methodName, ordinal);
        var positional = 0;
        foreach (var arg in call.ArgumentList.Arguments)
        {
            if (arg.NameColon is { } nc)
            {
                if (Array.IndexOf(names, nc.Name.Identifier.ValueText) >= 0)
                {
                    return arg.Expression;
                }
                continue;
            }

            if (positional == ordinal)
            {
                return arg.Expression;
            }
            positional++;
        }
        return null;
    }

    private static string[] ParameterNames(string methodName, int ordinal) => (methodName, ordinal) switch
    {
        ("Requires", 0) => new[] { "condition" },
        ("Ensures", 0) => new[] { "predicate", "condition" },
        ("LoopInvariant", 0) => new[] { "condition" },
        ("Decreases", 0) => new[] { "measure" },
        ("RequiresWhen", 0) or ("EnsuresWhen", 0) => new[] { "condition" },
        ("RequiresWhen", 1) => new[] { "requirement" },
        ("EnsuresWhen", 1) => new[] { "predicate", "postcondition" },
        (_, var o) when o == BecauseOrdinal(methodName) => new[] { "because" },
        (_, var o) when o == ClauseOrdinal(methodName) => new[] { "clause" },
        _ => Array.Empty<string>(),
    };
}
