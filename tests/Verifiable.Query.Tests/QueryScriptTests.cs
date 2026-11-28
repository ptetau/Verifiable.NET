using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// The unsat-core script that <c>verifiable-query</c> sends to Z3 for each return path.
/// </summary>
public sealed class QueryScriptTests
{
    [Fact]
    public void NamedQueryScript_IsThePremisesThenEachLabelledAssertionInOrderThenTheGoalLast()
    {
        var assume = new NamedPremise("assume_result_7", "QueryAssumption", "assumption", null, null, 0, "(> r 0)");
        var ob = new QueryObligation(
            MethodDisplay: "Rules.F",
            ClaimText: "result > 0",
            ReturnLocation: Location.None,
            PathCondition: "(and (> x 0) (< x 9))",
            PathConjuncts: ["(> x 0)", "(< x 9)"],
            ResultAssumption: assume,
            GoalTerm: "(> r 1)",
            ResultSmtName: "r",
            ScriptPreamble: "(declare-const x Int)\n(declare-const r Int)",
            TrackedParameters: [],
            Premises: []);
        var premiseMap = new Dictionary<string, NamedPremise>(StringComparer.Ordinal);

        var script = QueryEngine.BuildNamedQueryScript(ob, premiseMap);

        Assert.Equal(
            "(declare-const x Int)\n(declare-const r Int)\n" +
            "(assert (! (> x 0) :named path_cond_1))\n" +
            "(assert (! (< x 9) :named path_cond_2))\n" +
            "(assert (! (> r 0) :named assume_result_7))\n" +
            "(assert (! (not (> r 1)) :named query_negated_goal))\n",
            script);
        Assert.EndsWith(ProofKernel.NamedNegatedGoal(ob.GoalTerm), script, StringComparison.Ordinal);
        Assert.Equal(["path_cond_1", "path_cond_2", "assume_result_7"], premiseMap.Keys);
    }

    [Theory]
    [InlineData("path_1")]
    [InlineData("path_cond_1")]
    [InlineData("assume_result_1")]
    [InlineData("query_negated_goal")]
    public void ParameterNamedLikeAQueryLabel_DoesNotChangeTheAnswer(string name)
    {
        // The branch conditions used to be labelled p_path_1, p_path_2, ..., the symbol of a parameter named
        // path_1. Z3 accepts a label that shares a constant's name, and later formulas then read the name as
        // the label (true or false, as 1 or 0) instead of the parameter. Here the second branch condition
        // became (> 1 3), so the path returning path_1 looked unreachable and `result < 2` was "proved".
        var source = """
            using Verifiable.Contracts;
            [Verifiable] public static class Rules
            {
                public static int Pick(int NAME)
                {
                    if (NAME > 0)
                    {
                        if (NAME > 3) return NAME;
                    }
                    return 1;
                }
            }
            """.Replace("NAME", name, StringComparison.Ordinal);

        var (refutedCode, refutedJson) = Ask(source, "result < 2");
        Assert.True(refutedCode == 1, refutedJson);

        var (provedCode, provedJson) = Ask(source, "result >= 1");
        Assert.True(provedCode == 0, provedJson);
        using var doc = JsonDocument.Parse(provedJson);
        var result = doc.RootElement.GetProperty("results")[0];
        Assert.False(result.GetProperty("vacuous").GetBoolean());
    }

    private static (int Code, string Output) Ask(string source, string claim)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(
            ["ask", "--stdin", "--target", "Rules.Pick", "--assert", claim, "--format", "json"],
            () => source, stdout, stderr);
        return (code, stdout.ToString() + stderr);
    }
}
