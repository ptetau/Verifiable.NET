using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>
/// Checks the runtime half of the corpus runner on its own, without Z3, so a broken harness cannot quietly pass
/// every corpus program.
/// </summary>
public class SoundnessHarnessTests
{
    private const string Header = """
        using Verifiable.Contracts;
        namespace H;
        [Verifiable]
        public static class P
        {
        """;

    [Fact]
    public void CrossCheck_FindsABrokenPostcondition()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Wrong(int x)
                {
                    Spec.Requires(x >= 0);
                    Spec.Ensures<int>(r => r > x, "grows");
                    return x;
                }
            }
            """);

        var failure = Assert.Single(failures);
        Assert.Contains("breaks the postcondition \"grows\"", failure);
    }

    [Fact]
    public void CrossCheck_FindsOverflowAtTheEdge()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Next(int x)
                {
                    Spec.Requires(x >= 0);
                    Spec.Ensures<int>(r => r > x, "grows");
                    return x + 1;
                }
            }
            """);

        Assert.Contains("Next(2147483647)", Assert.Single(failures));
    }

    [Fact]
    public void CrossCheck_PassesACorrectMethod_AndRespectsPreconditions()
    {
        var (failures, notes, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Div(int x, int d)
                {
                    Spec.Requires(d > 0 && x >= 0);
                    Spec.Ensures<int>(r => r <= x, "shrinks");
                    return x / d;
                }
            }
            """);

        Assert.Empty(failures);
        Assert.Contains(notes, n => n.Contains("runs held") && !n.StartsWith("0 "));
    }

    [Fact]
    public void CrossCheck_FindsABrokenCalleePrecondition()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Half(int x)
                {
                    Spec.Requires(x % 2 == 0, "even only");
                    return x / 2;
                }

                public static int Caller(int x)
                {
                    Spec.Requires(x > 0);
                    Spec.Ensures<int>(r => true);
                    return Half(x);
                }
            }
            """);

        Assert.Contains(failures, f => f.Contains("Half was called without meeting its precondition: even only"));
    }

    [Fact]
    public void CrossCheck_FindsAFalseLoopInvariant_AndANonDecreasingMeasure()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Sum(int n)
                {
                    Spec.Requires(n >= 0 && n <= 50);
                    Spec.Ensures<int>(r => r >= 0);
                    var i = 0;
                    var total = 0;
                    while (i < n)
                    {
                        Spec.LoopInvariant(i < 3, "never reaches three");
                        total += i;
                        i++;
                    }
                    return total;
                }

                public static int Stuck(int n)
                {
                    Spec.Requires(n >= 1 && n <= 50);
                    Spec.Ensures<int>(r => r >= 0);
                    var i = 0;
                    while (i < n)
                    {
                        Spec.Decreases(n, "n is fixed, so this never goes down");
                        i++;
                    }
                    return i;
                }
            }
            """);

        Assert.Contains(failures, f => f.Contains("loop invariant") && f.Contains("never reaches three"));
        Assert.Contains(failures, f => f.Contains("Decreases measure") && f.Contains("instead of going down"));
    }

    [Fact]
    public void CrossCheck_SkipsMethodsOutsideVerifiableScope_AndPureAxioms()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer("""
            using Verifiable.Contracts;
            namespace H;
            public static class NotVerified
            {
                public static int Wrong(int x)
                {
                    Spec.Ensures<int>(r => r > x);
                    return x;
                }
            }
            [Verifiable]
            public static class Axioms
            {
                [PureAxiom]
                public static int Trusted(int x)
                {
                    Spec.Ensures<int>(r => r > x);
                    return x;
                }
            }
            """);

        Assert.Empty(failures);
    }

    [Fact]
    public void Witness_ShowsBrokenOrHeldOrDiscarded()
    {
        var (_, _, outcomes) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static long Twice(long x)
                {
                    Spec.Requires(x >= 0);
                    Spec.Ensures<long>(r => r == 2 * x);
                    return x + x;
                }

                public static int Same(int x)
                {
                    Spec.Ensures<int>(r => r == x + 1);
                    return x;
                }
            }
            """, "H.P.Twice(5)", "H.P.Twice(-1)", "H.P.Same(3)");

        Assert.IsType<RunOutcome.Held>(outcomes[0]);
        Assert.IsType<RunOutcome.Discarded>(outcomes[1]);
        Assert.IsType<RunOutcome.Broken>(outcomes[2]);
    }

    [Fact]
    public void ResultPostconditions_AreDropped_SoTheirArgumentsNeverRun()
    {
        var (failures, _, outcomes) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static string Name(int x)
                {
                    Spec.Requires(x >= 0);
                    Spec.Ensures(Spec.Result<string>().Length > 0, "non-empty");
                    return "n" + x;
                }
            }
            """, "H.P.Name(1)");

        Assert.Empty(failures);
        Assert.IsType<RunOutcome.Held>(outcomes.Single());
    }

    [Fact]
    public void Decreases_IsCheckedEvenWhenTheInvariantComesFirst()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Stuck(int n)
                {
                    Spec.Requires(n >= 1 && n <= 50);
                    Spec.Ensures<int>(r => r >= 0);
                    var i = 0;
                    while (i < n)
                    {
                        Spec.LoopInvariant(i >= 0, "counts up");
                        Spec.Decreases(n, "n is fixed, so this never goes down");
                        i++;
                    }
                    return i;
                }
            }
            """);

        Assert.Contains(failures, f => f.Contains("instead of going down"));
    }

    [Fact]
    public void Decreases_InANestedLoop_StartsAfreshOnEachOuterIteration()
    {
        var (failures, notes, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Grid(int n)
                {
                    Spec.Requires(n >= 0 && n <= 10);
                    Spec.Ensures<int>(r => r == n * n);
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        Spec.LoopInvariant(total == i * n, "rows so far");
                        var j = 0;
                        while (j < n)
                        {
                            Spec.LoopInvariant(j <= n, "inner bound");
                            Spec.Decreases(n - j, "inner count goes down");
                            total++;
                            j++;
                        }
                    }
                    return total;
                }
            }
            """);

        Assert.Empty(failures);
        Assert.Contains(notes, n => n.Contains("runs held"));
    }

    [Fact]
    public void RunawayRecursion_IsReported_InsteadOfCrashingTheRun()
    {
        var (failures, _, outcomes) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Spin(int x)
                {
                    Spec.Ensures<int>(r => r > x);
                    return Spin(x);
                }
            }
            """, "H.P.Spin(1)");

        Assert.Contains("recursed more than", Assert.IsType<RunOutcome.Broken>(outcomes.Single()).Reason);
        Assert.Contains(failures, f => f.Contains("recursed more than"));
    }

    [Fact]
    public void Postconditions_SeeParameterValuesOnEntry()
    {
        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int CountDown(int x)
                {
                    Spec.Requires(x >= 0 && x <= 100);
                    Spec.Ensures<int>(r => r == x, "returns the input");
                    var r0 = x;
                    while (x > 0)
                    {
                        Spec.Decreases(x);
                        x--;
                    }
                    return r0;
                }
            }
            """);

        Assert.Empty(failures);
    }

    [Fact]
    public void RunsThroughARejectedCallee_AreSetAside()
    {
        const string source = Header + """
                public static int Wrong(int x)
                {
                    Spec.Ensures<int>(r => r > x, "grows");
                    return x;
                }

                public static int Caller(int x)
                {
                    Spec.Ensures<int>(r => r > x, "relies on Wrong");
                    return Wrong(x);
                }
            }
            """;

        var (failures, _, _) = CorpusRunner.RunWithoutAnalyzer(source, new HashSet<string> { "P.Wrong(int)" });
        Assert.Empty(failures);

        var (unflagged, _, _) = CorpusRunner.RunWithoutAnalyzer(source);
        Assert.Equal(2, unflagged.Count);
    }

    [Fact]
    public void Postconditions_ThatCallAContractedMethod_CheckItsPrecondition()
    {
        var (_, _, outcomes) = CorpusRunner.RunWithoutAnalyzer(Header + """
                public static int Half(int x)
                {
                    Spec.Requires(x % 2 == 0, "even only");
                    Spec.Ensures<int>(r => r * 2 == x);
                    return x / 2;
                }

                public static int Use(int a)
                {
                    Spec.Requires(a >= 0 && a <= 100);
                    Spec.Ensures<int>(r => r == Half(a), "matches Half");
                    return a / 2;
                }
            }
            """, "H.P.Use(4)", "H.P.Use(3)");

        Assert.IsType<RunOutcome.Held>(outcomes[0]);
        Assert.Contains("even only", Assert.IsType<RunOutcome.Broken>(outcomes[1]).Reason);
    }

    [Fact]
    public void EveryCorpusProgram_HasValidDirectives()
    {
        var paths = CorpusProgram.AllRelativePaths().ToList();
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            _ = CorpusProgram.Load(path);
        }
    }
}
