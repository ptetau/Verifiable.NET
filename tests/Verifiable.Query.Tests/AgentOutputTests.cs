using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GalaxyCheck;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R12: every command speaks the same machine-readable contract — versioned JSON with a
/// stable order, one exit code per outcome, structured diagnostics, bounded output, no colour in
/// agent mode, and byte-identical output for the same input.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class AgentOutputTests
{
    private const string FailingSource = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Pricing
        {
            public static int Discount(int price, int pct)
            {
                Spec.Requires(price >= 0 && pct >= 0 && pct <= 100, "Prices and percentages are in range.");
                Spec.Ensures<int>(r => r >= 0 && r < price, "A discounted price is never negative and always cheaper.");
                return price - price * pct / 100;
            }
        }
        """;

    private const string ProvedSource = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Pricing
        {
            public static int Clamp(int value)
            {
                Spec.Requires(value >= -1000 && value <= 1000, "Inputs are small.");
                Spec.Ensures<int>(r => r >= 0, "A clamped value is never negative.");
                return value < 0 ? 0 : value;
            }
        }
        """;

    private static (int ExitCode, string Stdout, string Stderr) RunCli(string[] args, string? stdin = null)
    {
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        var exitCode = Program.Run(args, stdin is null ? null : () => stdin, outWriter, errWriter);
        return (exitCode, outWriter.ToString(), errWriter.ToString());
    }

    private static string WriteTemp(string name, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static T InDirectory<T>(string dir, Func<T> body)
    {
        var old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try { return body(); }
        finally { Directory.SetCurrentDirectory(old); }
    }

    [Fact]
    public void Check_Json_ReportsEveryDiagnosticFieldAnAgentNeeds()
    {
        var file = WriteTemp("Pricing.cs", FailingSource);
        var (code, stdout, _) = InDirectory(Path.GetDirectoryName(file)!, () =>
            RunCli(["check", "--file", file, "--format", "json"]));

        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("verifiable-query/check@1", root.GetProperty("schema").GetString());
        Assert.Equal("refuted", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());

        var post = root.GetProperty("diagnostics").EnumerateArray()
            .Single(d => d.GetProperty("contract").GetString()!.StartsWith("Spec.Ensures", StringComparison.Ordinal));
        Assert.Equal("VER102", post.GetProperty("rule").GetString());
        Assert.Equal("error", post.GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, post.GetProperty("clause").ValueKind);
        Assert.Equal("Pricing.Discount", post.GetProperty("symbol").GetString());
        Assert.Equal("Pricing.cs", post.GetProperty("file").GetString());
        Assert.Equal(10, post.GetProperty("line").GetInt32());
        Assert.Equal("A discounted price is never negative and always cheaper.", post.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Object, post.GetProperty("counterexample").ValueKind);
        Assert.True(post.GetProperty("counterexample").TryGetProperty("price", out _));
        Assert.StartsWith("Strengthen Spec.Requires", post.GetProperty("fix").GetString());
    }

    [Fact]
    public void Check_Json_IsByteIdenticalAcrossRuns()
    {
        var file = WriteTemp("Pricing.cs", FailingSource);
        var dir = Path.GetDirectoryName(file)!;
        var first = InDirectory(dir, () => RunCli(["check", "--file", file, "--format", "json"]));
        var second = InDirectory(dir, () => RunCli(["check", "--file", file, "--format", "json"]));

        Assert.Equal(first.Stdout, second.Stdout);
        Assert.DoesNotContain(dir, first.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", first.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_Json_PagesWithACursor()
    {
        var file = WriteTemp("Pricing.cs", FailingSource);
        var first = RunCli(["check", "--file", file, "--format", "json", "--limit", "1"]);
        using var firstDoc = JsonDocument.Parse(first.Stdout);
        var page = firstDoc.RootElement.GetProperty("page");
        Assert.Equal(1, firstDoc.RootElement.GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = RunCli(["check", "--file", file, "--format", "json", "--limit", "1", "--cursor", cursor!]);
        using var secondDoc = JsonDocument.Parse(second.Stdout);
        Assert.Equal(1, secondDoc.RootElement.GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, secondDoc.RootElement.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.NotEqual(
            firstDoc.RootElement.GetProperty("diagnostics")[0].GetRawText(),
            secondDoc.RootElement.GetProperty("diagnostics")[0].GetRawText());
    }

    [Theory]
    [InlineData("--cursor", "not-a-cursor")]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "many")]
    [InlineData("--format", "yaml")]
    public void BadOptionValues_AreInputErrors(string flag, string value)
    {
        var file = WriteTemp("Pricing.cs", ProvedSource);
        var (code, _, _) = RunCli(["check", "--file", file, flag, value]);
        Assert.Equal(3, code);
    }

    [Fact]
    public void Check_CodeThatDoesNotCompile_IsAnInputError()
    {
        var file = WriteTemp("Broken.cs", "public class X { int F() { return \"s\"; } }");
        var (code, stdout, _) = RunCli(["check", "--file", file, "--format", "json"]);
        Assert.Equal(3, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("input_error", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("CS0029", doc.RootElement.GetProperty("diagnostics")[0].GetProperty("rule").GetString());
    }

    [Fact]
    public void Check_MissingZ3_IsAMissingTool()
    {
        var file = WriteTemp("Pricing.cs", FailingSource);
        var oldPath = Environment.GetEnvironmentVariable("VERIFIABLE_Z3_PATH");
        var oldSysPath = Environment.GetEnvironmentVariable("PATH");
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_Z3_PATH", null);
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("HOME", Path.GetDirectoryName(file));
            var (code, stdout, _) = InDirectory(Path.GetDirectoryName(file)!, () =>
                RunCli(["check", "--file", file, "--format", "json", "--z3", Path.Combine(Path.GetDirectoryName(file)!, "no-z3-here")]));
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal("tool_missing", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(4, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_Z3_PATH", oldPath);
            Environment.SetEnvironmentVariable("PATH", oldSysPath);
            Environment.SetEnvironmentVariable("HOME", oldHome);
        }
    }

    [Fact]
    public void EveryCommand_GivesVersionedJsonWhoseStatusMatchesTheExitCode()
    {
        var file = WriteTemp("Pricing.cs", ProvedSource);
        var runs = new[]
        {
            RunCli(["check", "--file", file, "--format", "json"]),
            RunCli(["list", "--file", file, "--format", "json"]),
            RunCli(["ask", "--file", file, "--target", "Pricing.Clamp", "--assert", "result >= 0", "--format", "json"]),
            RunCli(["ask", "--file", file, "--target", "Pricing.Clamp", "--assert", "result > 0", "--format", "json"]),
            RunCli(["ask", "--file", file, "--format", "json"]),
            RunCli(["nonsense", "--format", "json"]),
        };

        var words = new[] { "proved", "refuted", "undecided", "input_error", "tool_missing" };
        foreach (var (code, stdout, _) in runs)
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            Assert.StartsWith("verifiable-query/", root.GetProperty("schema").GetString());
            Assert.EndsWith("@1", root.GetProperty("schema").GetString());
            Assert.Equal(code, root.GetProperty("exitCode").GetInt32());
            Assert.Equal(words[code], root.GetProperty("status").GetString());
            Assert.Equal("schema", root.EnumerateObject().First().Name);
        }

        Assert.Equal(new[] { 0, 0, 0, 1, 3, 3 }, runs.Select(r => r.ExitCode).ToArray());
    }

    [Fact]
    public void AgentMode_ElmOutputHasNoAnsiColour()
    {
        var file = WriteTemp("Pricing.cs", FailingSource);
        var old = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_AGENT", "1");
            var (_, stdout, stderr) = RunCli(["check", "--file", file, "--color", "always"]);
            Assert.DoesNotContain("\u001b[", stdout + stderr, StringComparison.Ordinal);
            Assert.Contains("<verifiable-check-result>", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_AGENT", old);
        }
    }

    [Fact]
    public void Classify_UsesTheSameFiveOutcomesAsEveryOtherCommand()
    {
        static PresentedDiagnostic D(string rule, string severity = "error") =>
            new(rule, severity, "a.cs", 1, 1, "s", "s");
        static CheckReport R(params PresentedDiagnostic[] ds) =>
            new("check", ds.Count(d => d.Severity == "error"), ds.Count(d => d.Severity == "warning"), 0, ds);

        Assert.Equal(AgentOutcome.Proved, ElmCheckEngine.Classify(R()));
        Assert.Equal(AgentOutcome.Proved, ElmCheckEngine.Classify(R(D("VER001", "warning"))));
        Assert.Equal(AgentOutcome.Refuted, ElmCheckEngine.Classify(R(D("VER102"))));
        Assert.Equal(AgentOutcome.Refuted, ElmCheckEngine.Classify(R(D("VER102"), D("VER103"))));
        Assert.Equal(AgentOutcome.Undecided, ElmCheckEngine.Classify(R(D("VER103"))));
        Assert.Equal(AgentOutcome.Undecided, ElmCheckEngine.Classify(R(D("VER103"), D("VER106"))));
        Assert.Equal(AgentOutcome.ToolMissing, ElmCheckEngine.Classify(R(D("VER104"), D("VER001"))));
        Assert.Equal(AgentOutcome.InputError, ElmCheckEngine.Classify(R(D("CS0029"), D("VER104"))));
    }

    [Fact]
    public void ElmSections_AreReadBackIntoFacts()
    {
        const string elm = """
            -- PROOF FAILED ------------------------------------------------ Pricing.cs

            Z3 found inputs that break a postcondition.

            Why this rule exists:
                Prices are never negative.

            Contract:
                Postcondition: r >= 0

            Counterexample:
                price = -1
                result = -1

            Fix: Strengthen Spec.Requires(...)
            preconditions.
            <verifiable-counterexample>{}</verifiable-counterexample>
            """;

        var facts = DiagnosticFacts.ParseElm(elm);
        Assert.Equal("Prices are never negative.", facts.Reason);
        Assert.Equal("Postcondition: r >= 0", facts.Contract);
        Assert.Equal("{\"price\":\"-1\",\"result\":\"-1\"}", facts.CounterexampleJson);
        Assert.Equal("Fix: Strengthen Spec.Requires(...) preconditions.", facts.Fix);
    }

    [Fact]
    public void NormalizePath_DropsMachineSpecificPrefixes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vq-root");
        Assert.Equal("src/A.cs", AgentOutput.NormalizePath(Path.Combine(root, "src", "A.cs"), root));
        Assert.Equal("B.cs", AgentOutput.NormalizePath(Path.Combine(Path.GetTempPath(), "elsewhere", "B.cs"), root));
        Assert.Equal("rel/C.cs", AgentOutput.NormalizePath("rel\\C.cs", root));
        Assert.Equal(string.Empty, AgentOutput.NormalizePath(null, root));
    }

    [Property(Iterations = 200)]
    public Property Paging_VisitsEveryItemExactlyOnceInOrder() =>
        (from total in Gen.Int32().Between(0, 300)
         from limit in Gen.Int32().Between(1, 60)
         select (total, limit)).ForAll(p =>
        {
            var seen = new List<int>();
            var cursor = (string?)null;
            var guard = 0;
            do
            {
                Assert.True(AgentOutput.TryDecodeCursor(cursor, out var offset));
                var page = AgentOutput.Slice(p.total, offset, p.limit);
                Assert.True(page.Count <= p.limit);
                for (var i = page.Offset; i < page.Offset + page.Count; i++) seen.Add(i);
                cursor = page.NextCursor;
            }
            while (cursor is not null && ++guard < 1000);

            Assert.Equal(Enumerable.Range(0, p.total), seen);
        });
}

[CollectionDefinition("CurrentDirectory", DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
}
