using System;
using System.Collections.Generic;
using System.Linq;

namespace Verifiable.Query;

internal sealed record OptionInfo(string Flag, string? Value, string Description);

internal sealed record CommandInfo(string Name, string Summary, string Schema, IReadOnlyList<string> Options);

/// <summary>
/// What <c>describe</c> lists (requirement R15): every command, its options and its output schema.
/// </summary>
internal static class CommandCatalog
{
    public static readonly IReadOnlyList<OptionInfo> Options =
    [
        new("--project", "<path>", "A .csproj file or a directory of C# sources."),
        new("--file", "<path>", "A standalone .cs file; may be repeated."),
        new("--stdin", null, "Read C# source from standard input."),
        new("--format", "json|elm", "Output format; agents use json."),
        new("--limit", "<n>", "Most items per JSON page (default 50, max 1000)."),
        new("--cursor", "<c>", "Continue from page.nextCursor of the previous result."),
        new("--target", "<symbols>", "Type or Type.Member names, comma-separated (alias --symbol)."),
        new("--clause", "<ids>", "Spec clause IDs, comma-separated."),
        new("--clauses", "<file>", "The spec's clause manifest (*.clauses.json)."),
        new("--lock", "<file>", "A contract lock (*.contracts.lock.json)."),
        new("--out", "<file>", "Where to write the lock or export file."),
        new("--assert", "<expr>", "A boolean C# claim (alias --expr)."),
        new("--assume", "<expr>", "An extra assumption for the claim."),
        new("--because", "<text>", "Why the question is asked."),
        new("--type", "<name>", "The domain type for check-invariant."),
        new("--all", null, "Ask about every [Verifiable] method."),
        new("--timeout", "<ms>", "Per-obligation solver timeout."),
        new("--stats", null, "Add timing and proof-cache counts (not byte-stable)."),
        new("--holes", null, "One pack per hole."),
        new("--max-tokens", "<n>", "Trim each pack to about this many tokens."),
        new("--one-answer", null, "Also report open contracts as VER111."),
        new("--rule", "<id>", "Only this rule."),
        new("--apply", null, "Write the fixes to disk."),
        new("--sarif", "<file>", "Present a SARIF 2.1 log from dotnet build."),
        new("--no-warnings", null, "Hide warnings."),
        new("--z3", "<path>", "An explicit Z3 executable."),
        new("--color", "auto|always|never", "ANSI colour for people; never under VERIFIABLE_AGENT=1."),
        new("--verbose", null, "Include internal SSA trace steps."),
        new("--to", "<dir>", "tools install: where to install."),
        new("--force", null, "tools install: reinstall even if present."),
    ];

    public static readonly IReadOnlyList<CommandInfo> Commands =
    [
        new("ask", "Prove or refute a boolean claim about a method's result.", "query", ["--project", "--file", "--stdin", "--target", "--all", "--assert", "--assume", "--because", "--timeout", "--z3", "--format", "--verbose"]),
        new("check-invariant", "Check an invariant across every method that produces a type.", "query", ["--project", "--file", "--stdin", "--type", "--assert", "--assume", "--because", "--timeout", "--z3", "--format"]),
        new("check", "Compile, run every analyzer and prove with Z3, in-process.", "check", ["--project", "--file", "--stdin", "--sarif", "--target", "--clause", "--clauses", "--lock", "--one-answer", "--timeout", "--stats", "--z3", "--no-warnings", "--format", "--limit", "--cursor", "--color"]),
        new("build", "Run dotnet build and present its diagnostics.", "check", ["--project", "--no-warnings", "--format", "--limit", "--cursor", "--color"]),
        new("list", "List queryable methods, invariants and constrained types.", "list", ["--project", "--file", "--stdin", "--format", "--limit", "--cursor"]),
        new("clauses", "Map spec clauses to the contracts and rules that carry them.", "clauses", ["--project", "--file", "--stdin", "--clauses", "--format", "--limit", "--cursor"]),
        new("holes", "List unwritten bodies (Spec.Hole) as a work list.", "holes", ["--project", "--file", "--stdin", "--clauses", "--format", "--limit", "--cursor"]),
        new("pack", "Everything an agent needs to work on one method.", "pack", ["--project", "--file", "--stdin", "--target", "--clause", "--holes", "--clauses", "--max-tokens", "--format", "--limit", "--cursor"]),
        new("lock", "Write the contract lock for generated methods.", "lock", ["--project", "--file", "--stdin", "--target", "--out", "--format"]),
        new("export", "Write every contract as JSON expression trees.", "export", ["--project", "--file", "--stdin", "--out", "--format", "--limit", "--cursor"]),
        new("one-answer", "Report whether each contract fixes one answer.", "one-answer", ["--project", "--file", "--stdin", "--target", "--lock", "--timeout", "--z3", "--format", "--limit", "--cursor"]),
        new("fix", "Run the code fixes and return them as text edits.", "fix", ["--project", "--file", "--stdin", "--rule", "--target", "--apply", "--format", "--limit", "--cursor"]),
        new("explain", "Explain a rule: why it exists, how to fix it, and an example.", "explain", ["--format"]),
        new("describe", "List every command, its options and its output schema.", "describe", ["--format"]),
        new("tools", "Show or install the pinned native tools.", "tools", ["--to", "--force", "--z3", "--format"]),
    ];

    public static string DescribeJson(bool indented) => AgentOutput.Write(indented, w =>
    {
        var byFlag = Options.ToDictionary(o => o.Flag, StringComparer.Ordinal);
        w.WriteStartObject();
        w.WriteString("schema", AgentOutput.SchemaId("describe"));
        w.WriteString("command", "describe");
        w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
        w.WriteNumber("exitCode", 0);
        w.WriteStartObject("exitCodes");
        foreach (var o in Enum.GetValues<AgentOutcome>()) w.WriteString(((int)o).ToString(System.Globalization.CultureInfo.InvariantCulture), AgentOutput.StatusWord(o));
        w.WriteEndObject();
        w.WriteStartArray("commands");
        foreach (var c in Commands)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("summary", c.Summary);
            w.WriteString("outputSchema", AgentOutput.SchemaId(c.Schema));
            w.WriteString("schemaFile", $"schemas/{c.Schema}.v1.schema.json");
            w.WriteStartArray("options");
            foreach (var flag in c.Options)
            {
                var o = byFlag[flag];
                w.WriteStartObject();
                w.WriteString("flag", o.Flag);
                AgentOutput.StringOrNull(w, "value", o.Value);
                w.WriteString("description", o.Description);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string ExplainJson(IReadOnlyList<RuleInfo> rules, bool indented) => AgentOutput.Write(indented, w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", AgentOutput.SchemaId("explain"));
        w.WriteString("command", "explain");
        w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
        w.WriteNumber("exitCode", 0);
        w.WriteStartArray("rules");
        foreach (var r in rules)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("title", r.Title);
            w.WriteString("category", r.Category);
            w.WriteString("severity", r.Severity.ToLowerInvariant());
            w.WriteString("why", r.Why);
            w.WriteString("fix", r.Fix);
            if (r.Bad is null && r.Good is null) w.WriteNull("example");
            else
            {
                w.WriteStartObject("example");
                AgentOutput.StringOrNull(w, "before", r.Bad);
                AgentOutput.StringOrNull(w, "after", r.Good);
                w.WriteEndObject();
            }
            w.WriteStartArray("codeFixes");
            foreach (var f in r.CodeFixes) w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });
}
