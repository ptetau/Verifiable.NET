using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R15: agents look up any diagnostic with `explain`, apply code fixes as data with `fix`,
/// and discover every command with `describe`.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ExplainAndFixTests
{
    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        using var o = new StringWriter();
        var code = Program.Run(args.Append("--format").Append("json").ToArray(), null, o, new StringWriter());
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    private static string Temp(string name, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-r15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    [Fact]
    public void Explain_DocumentsEveryRuleTheAnalyzersReport()
    {
        var (code, root) = Json("explain");
        Assert.Equal(0, code);
        Assert.Equal("verifiable-query/explain@1", root.GetProperty("schema").GetString());
        var rules = root.GetProperty("rules").EnumerateArray().ToList();
        var ids = rules.Select(r => r.GetProperty("id").GetString()!).ToList();

        foreach (var expected in new[] { "VER001", "VER005", "VER102", "VER103", "VER104", "VER107", "VER108", "VER109", "VER111", "VER301", "VER401", "VER404" })
            Assert.Contains(expected, ids);
        Assert.Equal(ids.OrderBy(i => i, StringComparer.Ordinal), ids);
        Assert.All(rules, r =>
        {
            Assert.DoesNotContain("not documented", r.GetProperty("why").GetString());
            Assert.DoesNotContain("not documented", r.GetProperty("fix").GetString());
        });

        var ver004 = rules.Single(r => r.GetProperty("id").GetString() == "VER004");
        Assert.Contains("HoistAmbientAuthorityCodeFixProvider", ver004.GetProperty("codeFixes").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public void Explain_OneRule_AndAnUnknownRule()
    {
        var (code, root) = Json("explain", "VER102");
        Assert.Equal(0, code);
        var rule = Assert.Single(root.GetProperty("rules").EnumerateArray());
        Assert.Equal("error", rule.GetProperty("severity").GetString());
        Assert.Contains("Spec.Ensures", rule.GetProperty("example").GetProperty("before").GetString());

        var (badCode, bad) = Json("explain", "VER1O2");
        Assert.Equal(3, badCode);
        Assert.Contains("VER102", bad.GetProperty("error").GetProperty("fix").GetString());
    }

    /// <summary>Each worked example is real: the "before" code triggers its rule and the "after" code does not.</summary>
    [Theory]
    [InlineData("VER001", false)]
    [InlineData("VER002", false)]
    [InlineData("VER004", false)]
    [InlineData("VER005", false)]
    [InlineData("VER006", false)]
    [InlineData("VER101", true)]
    [InlineData("VER102", true)]
    [InlineData("VER105", true)]
    [InlineData("VER108", true)]
    [InlineData("VER111", true)]
    public void WorkedExamples_TriggerTheirRuleBeforeAndNotAfter(string id, bool wrapInClass)
    {
        var rule = RuleCatalog.All().Single(r => r.Id == id);
        string Wrap(string code) =>
            "using System;\nusing System.IO;\nusing System.Collections.Generic;\nusing System.Collections.Immutable;\nusing Verifiable.Contracts;\n\n" +
            (wrapInClass ? "[Verifiable]\npublic static class Example\n{\n" + code + "\n}\n" : code + "\n");

        IEnumerable<string> Rules(string code)
        {
            var file = Temp("Example.cs", Wrap(code));
            var args = new List<string> { "check", "--file", file };
            if (id == "VER111") args.Add("--one-answer");
            return Json(args.ToArray()).Root.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("rule").GetString()!);
        }

        Assert.Contains(id, Rules(rule.Bad!));
        Assert.DoesNotContain(id, Rules(rule.Good!));
    }

    [Fact]
    public void Fix_ReturnsTheCodeFixAsAnExactTextEdit()
    {
        var file = Temp("Orders.cs", """
            using System;
            using Verifiable.Contracts;

            [Verifiable]
            public static class Orders
            {
                private static long Stamp() => DateTime.UtcNow.Ticks;
                public static long Next() => Stamp() + 1;
            }
            """);
        var before = File.ReadAllText(file);
        var (code, root) = Json("fix", "--file", file);

        Assert.Equal(0, code);
        Assert.Equal("verifiable-query/fix@1", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.Equal(before, File.ReadAllText(file));
        var fix = Assert.Single(root.GetProperty("fixes").EnumerateArray());
        Assert.Equal("VER004", fix.GetProperty("rule").GetString());
        Assert.Equal("Orders.Stamp", fix.GetProperty("symbol").GetString());

        // Applying the edit by hand gives the same text as --apply.
        var edit = Assert.Single(fix.GetProperty("edits").EnumerateArray());
        var lines = before.Replace("\r\n", "\n").Split('\n');
        int Offset(JsonElement pos) => lines.Take(pos.GetProperty("line").GetInt32() - 1).Sum(l => l.Length + 1) + pos.GetProperty("column").GetInt32() - 1;
        var text = before.Replace("\r\n", "\n");
        var start = Offset(edit.GetProperty("start"));
        var end = Offset(edit.GetProperty("end"));
        var byHand = text.Substring(0, start) + edit.GetProperty("newText").GetString() + text.Substring(end);
        Assert.Contains("Stamp(DateTime now)", byHand);

        var (applyCode, applied) = Json("fix", "--file", file, "--apply");
        Assert.Equal(0, applyCode);
        Assert.True(applied.GetProperty("applied").GetBoolean());
        Assert.Equal(byHand, File.ReadAllText(file).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Fix_FiltersByRuleAndTarget()
    {
        var file = Temp("Two.cs", """
            using System;
            using Verifiable.Contracts;

            [Verifiable]
            public static class A
            {
                private static long Now() => DateTime.UtcNow.Ticks;
            }

            [Verifiable]
            public class B
            {
                public int Count;
            }
            """);
        var rules = Json("fix", "--file", file).Root.GetProperty("fixes").EnumerateArray().Select(f => f.GetProperty("rule").GetString()).ToList();
        Assert.Contains("VER004", rules);
        Assert.Contains("VER001", rules);
        Assert.All(Json("fix", "--file", file, "--rule", "VER001").Root.GetProperty("fixes").EnumerateArray(),
            f => Assert.Equal("VER001", f.GetProperty("rule").GetString()));
        Assert.All(Json("fix", "--file", file, "--target", "A").Root.GetProperty("fixes").EnumerateArray(),
            f => Assert.Equal("A.Now", f.GetProperty("symbol").GetString()));
    }

    [Fact]
    public void Describe_ListsEveryCommandWithItsOptionsAndSchema()
    {
        var (code, root) = Json("describe");
        Assert.Equal(0, code);
        var commands = root.GetProperty("commands").EnumerateArray().ToList();
        var names = commands.Select(c => c.GetProperty("name").GetString()).ToList();
        foreach (var n in new[] { "ask", "check", "build", "list", "clauses", "holes", "pack", "lock", "export", "one-answer", "fix", "explain", "describe", "tools" })
            Assert.Contains(n, names);

        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Verifiable.NET.sln"))) repo = repo.Parent;
        foreach (var c in commands)
        {
            Assert.True(File.Exists(Path.Combine(repo!.FullName, "docs", c.GetProperty("schemaFile").GetString()!)), c.GetProperty("schemaFile").GetString());
            Assert.NotEmpty(c.GetProperty("options").EnumerateArray());
        }
        Assert.Equal("tool_missing", root.GetProperty("exitCodes").GetProperty("4").GetString());
    }
}
