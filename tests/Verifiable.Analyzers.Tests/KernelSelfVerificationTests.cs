using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Verifiable.NET proves its own kernel. These tests re-run that proof on the kernel sources in
/// src/Verifiable.Core and then break the kernel on purpose, one rule at a time, to show each proof
/// actually rejects the bug it exists to catch.
/// </summary>
public class KernelSelfVerificationTests
{
    private static readonly string KernelDirectory = Path.Combine(FindRepoRoot(), "src", "Verifiable.Core");

    [Fact]
    public async Task Kernel_IsProvedByVerifiable()
    {
        var diagnostics = await VerifyKernelAsync(KernelSources());

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER104"); // a missing solver would make the proof vacuous
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
    }

    public static TheoryData<string, string, string, string> KernelBugs => new()
    {
        { "ProofKernel.cs", "if (status == SmtStatus.Sat) return ObligationVerdict.Refuted;", "if (status == SmtStatus.Sat) return ObligationVerdict.Proved;", "ProofKernel.Decide" },
        { "ProofKernel.cs", "if (verdict == ObligationVerdict.SolverMissing) return !allowMissingSolver;", "if (verdict == ObligationVerdict.SolverMissing) return false;", "ProofKernel.BlocksBuild" },
        { "ProofKernel.cs", "return status == SmtStatus.Unsat || status == SmtStatus.Sat;", "return status != SmtStatus.SolverUnavailable;", "ProofKernel.IsCacheable" },
        { "ProofKernel.cs", "return signatureValid && IsCacheable(status);", "return IsCacheable(status);", "ProofKernel.AcceptCachedAnswer" },
        { "ProofKernel.cs", "if (line == \"unsat\") return SolverLine.Unsat;", "if (line.Contains(\"unsat\")) return SolverLine.Unsat;", "ProofKernel.ClassifyStatusLine" },
        { "ProofKernel.cs", "if (kind != SolverLine.Noise)", "if (kind == SolverLine.Unsat || kind == SolverLine.Sat)", "ProofKernel.ReadCheckSatAnswer" },
        { "ProofKernel.cs", "return preamble + \"\\n(assert (not \" + goal + \"))\\n\";", "return preamble + \"\\n(assert \" + goal + \")\\n\";", "ProofKernel.BuildRefutationQuery" },
        { "ProofKernel.cs", "return preamble + \"\\n(assert (not \" + goal + \"))\\n\";", "return preamble + \"\\n(assert false)\\n(assert (not \" + goal + \"))\\n\";", "ProofKernel.BuildRefutationQuery" },
        { "ProofKernel.cs", "return preamble + \"\\n(assert \" + pathCondition + \")\\n(assert (not \" + goal + \"))\\n\";", "return preamble + \"\\n(assert \" + pathCondition + \")\\n(assert false)\\n(assert (not \" + goal + \"))\\n\";", "ProofKernel.BuildRefutationQuery" },
        { "ProofKernel.cs", "return preamble + \"\\n(assert \" + pathCondition + \")\\n(assert (not \" + goal + \"))\\n\";", "return preamble + \"\\n(assert (not \" + goal + \"))\\n\";", "ProofKernel.BuildRefutationQuery" },
        { "ProofKernel.cs", "return \"(assert (! (not \" + goal + \") :named \" + NegatedGoalName + \"))\\n\";", "return \"(assert (! \" + goal + \" :named \" + NegatedGoalName + \"))\\n\";", "ProofKernel.NamedNegatedGoal" },
        { "ProofKernel.cs", "return premiseCheck == SmtStatus.Unsat;", "return premiseCheck != SmtStatus.Sat;", "ProofKernel.PremisesContradict" },
        { "VacuityKernel.cs", "return prefix + \"\\n(assert \" + context + \")\\n(assert \" + guarantee + \")\\n\";", "return prefix + \"\\n(assert \" + context + \")\\n\";", "VacuityKernel.GuaranteeQuery" },
        { "VacuityKernel.cs", "return prefix + \"\\n(assert \" + context + \")\\n\";", "return prefix + \"\\n(assert \" + context + \")\\n(assert false)\\n\";", "VacuityKernel.ReachabilityQuery" },
        { "VacuityKernel.cs", "return withGuarantee == SmtStatus.Unsat;", "return withGuarantee != SmtStatus.Sat;", "VacuityKernel.NeedsReachabilityCheck" },
        { "VacuityKernel.cs", "if (withGuarantee != SmtStatus.Unsat) return GuaranteeVerdict.Undecided;", "if (withGuarantee != SmtStatus.Unsat) return GuaranteeVerdict.CanHold;", "VacuityKernel.ClassifyGuarantee" },
        { "VacuityKernel.cs", "if (withoutGuarantee == SmtStatus.Sat) return GuaranteeVerdict.Impossible;", "if (withoutGuarantee != SmtStatus.Unsat) return GuaranteeVerdict.Impossible;", "VacuityKernel.ClassifyGuarantee" },
        { "VacuityKernel.cs", "return check != SmtStatus.Sat && check != SmtStatus.Unsat;", "return check == SmtStatus.Unknown;", "VacuityKernel.CheckUndecided" },
        { "QueryVerdicts.cs", "if (undecided > 0) return QueryVerdict.Inconclusive;", "", "QueryKernel.Aggregate" },
        { "QueryVerdicts.cs", "if (provedReachable == 0 && provedUnreachable > 0) return QueryVerdict.ProvedVacuously;", "", "QueryKernel.Aggregate" },
        { "ObligationLattice.cs", "if (present == ObligationState.SinkParameter) return ObligationState.SinkParameter;\n        return ObligationState.MaybeLive;", "if (present == ObligationState.SinkParameter) return ObligationState.SinkParameter;\n        return ObligationState.Consumed;", "ObligationLattice.JoinWithUntracked" },
        { "ObligationLattice.cs", "        a == b\n            ? a", "        a == b && a != ObligationState.Live\n            ? a", "LatticeLaws.JoinIsIdempotent" },
        { "ObligationLattice.cs", ": ObligationState.MaybeLive);", ": ObligationState.Consumed);", "ObligationLattice.Join" },
        { "ObligationLattice.cs", "        a == b\n            ? a", "        a == b || b == ObligationState.Consumed\n            ? a", "LatticeLaws.JoinIsCommutative" },
        { "CheckSelectionKernel.cs", "if (obligationCount > 0) return MethodCheck.Prove;", "if (obligationCount > 1) return MethodCheck.Prove;", "CheckSelectionKernel.Select" },
        { "CheckSelectionKernel.cs", "return MethodCheck.UnsupportedWarning;", "return MethodCheck.NothingToProve;", "CheckSelectionKernel.Select" },
        { "CheckSelectionKernel.cs", "if (obligationCount > 0 || knownObligationSource) return MethodCheck.UnsupportedError;", "if (knownObligationSource) return MethodCheck.UnsupportedError;", "CheckSelectionKernel.Select" },
        { "SolverPlumbingKernel.cs", "((char)(text.Length / 65536)).ToString() + ((char)(text.Length % 65536)).ToString() + text", "text", "CacheKeyLaws.FieldThenRestDecodesUniquely" },
        { "SolverPlumbingKernel.cs", "return CacheKeyField(key) + CacheKeyField(expression);", "return CacheKeyField(key) + expression;", "SolverPlumbingKernel.CacheKeyEvalEntry" },
        { "SolverPlumbingKernel.cs", "               CacheKeyField(script) + CacheKeyField(evals);", "               script + CacheKeyField(evals);", "SolverPlumbingKernel.CacheKeyMaterial" },
        { "SolverPlumbingKernel.cs", "return \"(assert (! \" + formula + \" :named \" + name + \"))\\n\";", "return \"(assert false)\\n(assert (! \" + formula + \" :named \" + name + \"))\\n\";", "SolverPlumbingKernel.NamedAssertion" },
    };

    [Theory]
    [MemberData(nameof(KernelBugs))]
    public async Task BrokenKernel_FailsItsProof(string file, string original, string broken, string expectedSymbol)
    {
        var sources = KernelSources().Select(s =>
        {
            if (Path.GetFileName(s.Path) != file) return s;
            var text = s.Source.Replace("\r\n", "\n");
            Assert.Contains(original, text);
            return (s.Path, text.Replace(original, broken));
        }).ToList();

        var diagnostics = await VerifyKernelAsync(sources);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.GetMessage().Contains(expectedSymbol, StringComparison.Ordinal));
    }

    /// <summary>
    /// The analyzer used here, and the one that ships, is compiled from the live kernel. A kernel bug that
    /// made every query unsat would blind it. (Verifiable.Core itself is proved by the bootstrap analyzer;
    /// see BootstrapKernelTests.) These canaries use the analyzer as built and require it to reject
    /// contracts that are plainly false, so a blinded prover cannot pass the test suite.
    /// </summary>
    [Theory]
    [InlineData("public static int F(int x) { Spec.Ensures<int>(r => r > x, \"grows\"); return x; }")]
    [InlineData("public static int F(int x) { Spec.Ensures<int>(r => r >= 0, \"non-negative\"); if (x > 0) return x; return x - 1; }")]
    [InlineData("public static decimal F(decimal a, decimal b) { Spec.Requires(a > 0m, \"positive\"); Spec.Ensures<decimal>(r => r > a, \"grows\"); return a - b; }")]
    public async Task Canary_BuiltAnalyzer_RejectsFalseContracts(string member)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            "using Verifiable.Contracts;\n[Verifiable] public static class Canary { " + member + " }",
            new VerifiableSmtAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER104");
        Assert.Contains(diagnostics, d => d.Id == "VER102");
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> VerifyKernelAsync(
        IEnumerable<(string Path, string Source)> sources) =>
        AnalyzerTestHarness.AnalyzeSourcesAsync(
            sources.Append(("GlobalUsings.cs", "global using Verifiable.Contracts;")),
            new VerifiableGatekeeperAnalyzer(),
            new LinearObligationAnalyzer(),
            new VerifiableSmtAnalyzer(),
            new VerifiableNullabilitySuppressor());

    private static List<(string Path, string Source)> KernelSources() =>
        Directory.EnumerateFiles(KernelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (p, File.ReadAllText(p)))
            .ToList();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (Verifiable.NET.sln).");
    }
}
