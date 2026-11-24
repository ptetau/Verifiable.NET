using System.Linq;
using System.Threading.Tasks;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class LinearObligationTests
{
    private const string Preamble = """
        using System.Threading.Tasks;
        using Verifiable.Contracts;

        namespace LinearDomain;

        [MustConsume]
        public readonly record struct PendingCommit(int Id);

        public static class Workflow
        {
            public static PendingCommit Create(int id) => new(id);

            [ConsumesObligation]
            public static void Commit(PendingCommit commit) { }

            [ConsumesObligation]
            public static async Task CommitAsync(PendingCommit commit)
            {
                await Task.Yield();
                _ = commit.Id;
            }
        """;

    [Fact]
    public async Task ProperlyConsumedOnAllPaths_AndTransferredViaReturn_ProducesZeroVER007()
    {
        const string code = Preamble + """
            public static void ValidDirect(int id)
            {
                var pending = Create(id);
                var touched = pending.Id; // reading non-linear property does not consume
                Commit(pending);
            }

            public static void ValidBranching(bool ok, int id)
            {
                var pending = Create(id);
                if (ok)
                {
                    Commit(pending);
                }
                else
                {
                    Obligation.Discard(pending, "cancelled");
                }
            }

            public static PendingCommit ValidReturnTransfer(int id)
            {
                var pending = Create(id);
                return pending;
            }

            public static async Task ValidAsyncDischarge(int id)
            {
                var pending = Create(id);
                await CommitAsync(pending);
            }
        }
        """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(code, new LinearObligationAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.DoesNotContain(diags, d => d.Id == "VER007");
    }

    [Fact]
    public async Task DroppedReturnValue_ReportsVER007()
    {
        const string code = Preamble + """
            public static void BadDrop(int id)
            {
                Create(id);
            }
        }
        """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(code, new LinearObligationAnalyzer());
        var err = Assert.Single(diags.Where(d => d.Id == "VER007"));
        Assert.Contains("Fix:", err.GetMessage());
    }

    [Fact]
    public async Task MissingConsumptionOnElseBranch_ReportsVER007()
    {
        const string code = Preamble + """
            public static void BadBranchLeak(bool ok, int id)
            {
                var pending = Create(id);
                if (ok)
                {
                    Commit(pending);
                }
            }
        }
        """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(code, new LinearObligationAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER007");
    }

    [Fact]
    public async Task DoubleConsumption_ReportsVER007()
    {
        const string code = Preamble + """
            public static void BadDoubleConsume(int id)
            {
                var pending = Create(id);
                Commit(pending);
                Commit(pending);
            }
        }
        """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(code, new LinearObligationAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER007");
    }

    [Fact]
    public async Task OverwriteBeforeConsumption_ReportsVER007()
    {
        const string code = Preamble + """
            public static void BadOverwrite(int id)
            {
                var pending = Create(id);
                pending = Create(id + 1);
                Commit(pending);
            }
        }
        """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(code, new LinearObligationAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER007");
    }

    [Fact]
    public async Task PassingToNonSinkHelperAndReturning_ReportsVER007()
    {
        const string code = Preamble + """
            private static void Inspect(PendingCommit c) { _ = c.Id; }

            public static void BadNonSink(int id)
            {
                var pending = Create(id);
                Inspect(pending);
            }
        }
        """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(code, new LinearObligationAnalyzer());
        var err = Assert.Single(diags, d => d.Id == "VER007" && d.GetMessage().Contains("`pending`"));
        var msg = err.GetMessage();
        Assert.Contains("-- UNCONSUMED OBLIGATION", msg);
        Assert.Contains("^", msg);
        Assert.Contains("Fix:", msg);
    }
}
