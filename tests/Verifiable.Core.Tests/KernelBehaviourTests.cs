using System;
using System.Collections.Immutable;
using System.Linq;
using GalaxyCheck;
using Verifiable.Core;
using Verifiable.Core.Proofs;
using Xunit;

namespace Verifiable.Core.Tests;

/// <summary>
/// Runtime checks of the compiled kernel. The Z3 proofs run when src/Verifiable.Core builds; these
/// tests confirm the compiled IL behaves the same way, exhaustively where the domain is small and
/// with GalaxyCheck where it is not.
/// </summary>
public class KernelBehaviourTests
{
    private static readonly SmtStatus[] AllStatuses = Enum.GetValues<SmtStatus>();
    private static readonly ObligationState[] AllStates = Enum.GetValues<ObligationState>();

    [Fact]
    public void Decide_OnlyUnsatProves_OnlySatRefutes()
    {
        foreach (var status in AllStatuses)
        {
            var verdict = ProofKernel.Decide(status);
            var expected = status switch
            {
                SmtStatus.Unsat => ObligationVerdict.Proved,
                SmtStatus.Sat => ObligationVerdict.Refuted,
                SmtStatus.SolverUnavailable => ObligationVerdict.SolverMissing,
                _ => ObligationVerdict.Undecided
            };
            Assert.Equal(expected, verdict);
        }
    }

    [Fact]
    public void IsCacheable_OnlyDefinitiveAnswers()
    {
        foreach (var status in AllStatuses)
        {
            Assert.Equal(status is SmtStatus.Unsat or SmtStatus.Sat, ProofKernel.IsCacheable(status));
        }
    }

    [Property]
    public bool ClassifyStatusLine_ReadsOnlyExactTokens(string line)
    {
        line ??= string.Empty;
        var kind = ProofKernel.ClassifyStatusLine(line);
        return (kind == SolverLine.Unsat) == (line == "unsat")
            && (kind == SolverLine.Sat) == (line == "sat")
            && (kind == SolverLine.Unknown) == (line == "unknown")
            && (kind == SolverLine.Error) == (line.StartsWith("(error", StringComparison.Ordinal));
    }

    private static readonly string[] OutputLineSamples =
    [
        "", "unsat", "sat", "unknown", "(error \"line 3 column 9: invalid sort\")", "success",
        "WARNING: relevancy must be enabled", " unsat", "unsat ", "UNSAT", "unsat sat", "(echo unsat)",
        "\"unsat\"", "satisfiable", "(error", "unknown reason"
    ];

    private static SmtStatus ReferenceReader(ImmutableArray<string> lines)
    {
        foreach (var line in lines)
        {
            switch (line)
            {
                case "unsat": return SmtStatus.Unsat;
                case "sat": return SmtStatus.Sat;
                case "unknown": return SmtStatus.Unknown;
            }

            if (line.StartsWith("(error", StringComparison.Ordinal)) return SmtStatus.Error;
        }

        return SmtStatus.Error;
    }

    [Property(Iterations = 500)]
    public Property ReadCheckSatAnswer_FirstVerdictLineDecides() =>
        Gen.Element(OutputLineSamples).ListOf().WithCountBetween(0, 8).ForAll(sample =>
        {
            var lines = sample.ToImmutableArray();
            var status = ProofKernel.ReadCheckSatAnswer(lines);

            var firstUnsat = lines.IndexOf("unsat");
            var unsatIsJustified = status != SmtStatus.Unsat ||
                (firstUnsat >= 0 && lines.Take(firstUnsat).All(l => ProofKernel.ClassifyStatusLine(l) == SolverLine.Noise));

            return status == ReferenceReader(lines) && unsatIsJustified;
        });

    [Fact]
    public void ReadCheckSatAnswer_ErrorBeforeUnsat_IsNotAProof()
    {
        var lines = ImmutableArray.Create("(error \"line 4 column 10: unknown constant y\")", "unsat");
        Assert.Equal(SmtStatus.Error, ProofKernel.ReadCheckSatAnswer(lines));
    }

    [Fact]
    public void ReadCheckSatAnswer_NoVerdict_IsAnError()
    {
        Assert.Equal(SmtStatus.Error, ProofKernel.ReadCheckSatAnswer(ImmutableArray<string>.Empty));
        Assert.Equal(SmtStatus.Error, ProofKernel.ReadCheckSatAnswer(ImmutableArray.Create("", "success")));
    }

    [Property(Iterations = 300)]
    public Property BuildRefutationQuery_KeepsPremisesAndNegatesGoalLast() =>
        (from preamble in Gen.String().WithLengthBetween(0, 40)
         from path in Gen.Element(new[] { "", "true", "(> x 0)", "(and p q)" })
         from goal in Gen.String().WithLengthBetween(1, 20)
         select (preamble, path, goal)).ForAll(x =>
        {
            // Independent of Z3: the analyzer that proves the kernel is built from the kernel, so a bug that
            // blinds the prover (for example an injected `(assert false)`) must be caught without it.
            var script = ProofKernel.BuildRefutationQuery(x.preamble, x.path, x.goal);
            var pathMatters = x.path.Length > 0 && x.path != "true";
            var expected = x.preamble + "\n" + (pathMatters ? "(assert " + x.path + ")\n" : "") + "(assert (not " + x.goal + "))\n";
            return script == expected;
        });

    [Fact]
    public void Vacuity_OnlyUnsatPremisesContradict()
    {
        foreach (var premises in AllStatuses)
        {
            Assert.Equal(premises == SmtStatus.Unsat, ProofKernel.PremisesContradict(premises));
        }
    }

    [Fact]
    public void NamedNegatedGoal_IsTheNegatedGoal()
    {
        Assert.Equal("(assert (! (not (> x 0)) :named query_negated_goal))\n", ProofKernel.NamedNegatedGoal("(> x 0)"));
    }

    [Fact]
    public void Aggregate_MatchesSpecificationOnSmallCounts()
    {
        for (var refuted = 0; refuted < 3; refuted++)
        for (var undecided = 0; undecided < 3; undecided++)
        for (var reachable = 0; reachable < 3; reachable++)
        for (var unreachable = 0; unreachable < 3; unreachable++)
        {
            var verdict = QueryKernel.Aggregate(refuted, undecided, reachable, unreachable);
            var expected =
                refuted > 0 ? QueryVerdict.Refuted :
                undecided > 0 ? QueryVerdict.Inconclusive :
                reachable == 0 && unreachable > 0 ? QueryVerdict.ProvedVacuously :
                QueryVerdict.Proved;
            Assert.Equal(expected, verdict);
        }
    }

    [Fact]
    public void ObligationLattice_LawsHoldForEveryState()
    {
        foreach (var a in AllStates)
        {
            Assert.True(LatticeLaws.JoinIsIdempotent(a));
            Assert.Equal(
                a == ObligationState.Consumed,
                ObligationLattice.JoinWithUntracked(a) == ObligationState.Consumed);

            foreach (var b in AllStates)
            {
                Assert.True(LatticeLaws.JoinIsCommutative(a, b));
                Assert.True(LatticeLaws.JoinIsAnUpperBound(a, b));
                Assert.Equal(ObligationLattice.JoinCore(a, b), ObligationLattice.Join(a, b));

                var outstandingIn = (ObligationLattice.IsOutstanding(a) || ObligationLattice.IsOutstanding(b)) &&
                                    a != ObligationState.SinkParameter && b != ObligationState.SinkParameter;
                Assert.True(!outstandingIn || ObligationLattice.IsOutstanding(ObligationLattice.Join(a, b)));

                foreach (var c in AllStates)
                {
                    Assert.True(LatticeLaws.JoinIsAssociative(a, b, c));
                }
            }
        }
    }
}
