using System;
using System.Collections.Generic;
using System.Linq;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// The text around a Z3 call: the cache key, and reading Z3's answers back.
/// </summary>
public class SolverPlumbingTests
{
    private const string Solver = "/opt/z3|/opt/z3|123|456";

    public static TheoryData<string, string, int, string, (string, string)[], string, string, int, string, (string, string)[]> KeysTheOldFormatConfused => new()
    {
        // The script itself ends with what the eval list used to look like.
        { Solver, "", 2500, "(check-sat)\nEVALS:\nk=e", [], Solver, "", 2500, "(check-sat)", [("k", "e")] },
        // '=' inside a key or an expression moved the split point.
        { Solver, "", 2500, "s", [("a=b", "c")], Solver, "", 2500, "s", [("a", "b=c")] },
        // A newline inside an expression read as the start of another entry.
        { Solver, "", 2500, "s", [("a", "b\nc=d")], Solver, "", 2500, "s", [("a", "b"), ("c", "d")] },
        // An empty mode was left out, so a solver identity ending in a mode name stood in for it.
        { Solver + "\nCORE", "", 2500, "s", [], Solver, "CORE", 2500, "s", [] },
        // Fields could shift: here the timeout of one key lines up with the script of the other.
        { Solver + "\n2500", "", 1, "s", [], Solver, "", 2500, "1\ns", [] },
    };

    [Theory]
    [MemberData(nameof(KeysTheOldFormatConfused))]
    public void CacheKey_DiffersForDifferentQueries(
        string solverA, string modeA, int timeoutA, string scriptA, (string, string)[] evalsA,
        string solverB, string modeB, int timeoutB, string scriptB, (string, string)[] evalsB)
    {
        var a = Z3SolverRunner.BuildHashInput(solverA, modeA, timeoutA, scriptA, evalsA);
        var b = Z3SolverRunner.BuildHashInput(solverB, modeB, timeoutB, scriptB, evalsB);

        Assert.NotEqual(a, b);
        Assert.NotEqual(Z3SolverRunner.ComputeSha256(a), Z3SolverRunner.ComputeSha256(b));
    }

    [Fact]
    public void CacheKey_NoEvalsAndAnEmptyEvalListAreTheSameQuery()
    {
        Assert.Equal(
            Z3SolverRunner.BuildHashInput(Solver, "", 2500, "s", null),
            Z3SolverRunner.BuildHashInput(Solver, "", 2500, "s", Array.Empty<(string, string)>()));
    }

    [Fact]
    public void Sha256_HashesUnpairedSurrogatesExactly()
    {
        // UTF-8 encoding turns both into U+FFFD, so they used to hash alike. Length headers in the key can
        // themselves be unpaired surrogates (a field of 55296 characters starts with U+0000 U+D800).
        Assert.NotEqual(Z3SolverRunner.ComputeSha256("\ud800"), Z3SolverRunner.ComputeSha256("\udbff"));
        Assert.NotEqual(Z3SolverRunner.ComputeSha256("a\ud800b"), Z3SolverRunner.ComputeSha256("a\ufffdb"));
    }

    [Fact]
    public void ParseModel_BackslashBeforeTheClosingQuoteEndsTheString()
    {
        // SMT-LIB 2.6 escapes a quote as "" and has no backslash escape, so "C:\" is a complete literal.
        const string model = "(\n  (define-fun p_s () VerStr\n    (str-val \"C:\\\"))\n  (define-fun p_t () VerStr\n    (str-val \"a\"\"b\"))\n  (define-fun p_n () Int\n    7)\n)\n";

        var parsed = Z3SolverRunner.ParseModel(model);

        Assert.Equal("\"C:\\\"", parsed["p_s"]);
        Assert.Equal("\"a\"\"b\"", parsed["p_t"]);
        Assert.Equal("7", parsed["p_n"]);
    }

    [Fact]
    public void ModelValueSpellingASentinel_DoesNotEndTheReadEarly()
    {
        // Z3 breaks the interpretation of `f` over several lines, and one of them is exactly the quoted text
        // "__VERIFIABLE_MODEL_DONE__". That line used to end the model read: `k` was lost, and the rest of the
        // model stayed in the pipe to be read as the answer to the next command.
        var longArg = new string('a', 80);
        var script =
            $"; {Guid.NewGuid():N}\n" +
            "(declare-const k Int)\n" +
            "(declare-fun f (String) String)\n" +
            $"(assert (= (f \"{longArg}\") \"__VERIFIABLE_MODEL_DONE__\"))\n" +
            "(assert (= (f \"b\") \"__VERIFIABLE_EVAL_DONE__\"))\n" +
            "(assert (= k 42))\n";
        var evals = new List<(string Key, string SmtExpr)> { ("k_eval", "k"), ("f_b", "(f \"b\")") };

        var first = Z3SolverRunner.Execute(script, satEvalExprs: evals);

        Assert.Equal(SmtStatus.Sat, first.Status);
        Assert.Equal("42", first.Model["k"]);
        Assert.Equal("42", first.Model["k_eval"]);
        Assert.Equal("\"__VERIFIABLE_EVAL_DONE__\"", first.Model["f_b"]);

        // The same worker must still be in step: each later answer is its own.
        for (var i = 0; i < 6; i++)
        {
            var unsat = Z3SolverRunner.Execute($"; {Guid.NewGuid():N}\n(declare-const x Int)\n(assert (> x {i}))\n(assert (< x {i}))\n");
            Assert.Equal(SmtStatus.Unsat, unsat.Status);

            var sat = Z3SolverRunner.Execute($"; {Guid.NewGuid():N}\n(declare-const y Int)\n(assert (= y {i + 100}))\n");
            Assert.Equal(SmtStatus.Sat, sat.Status);
            Assert.Equal((i + 100).ToString(System.Globalization.CultureInfo.InvariantCulture), sat.Model["y"]);
        }
    }
}
