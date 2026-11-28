using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers;

namespace Verifiable.Query;

/// <summary>
/// <c>verifiable-query clauses</c> (requirement R1): which spec clauses have code, and which code has
/// a clause, as JSON (schema <c>verifiable-query/clauses@1</c>).
/// </summary>
internal static class ClauseReport
{
    internal sealed record Site(string? Clause, string Kind, string Symbol, string File, int Line, string Text);

    public static List<Site> Collect(Compilation compilation) =>
        compilation.SyntaxTrees
            .Where(t => !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(ClauseIds.Sites)
            .Select(s =>
            {
                var span = s.Location.GetLineSpan();
                return new Site(s.Clause, s.Kind, s.Symbol, AgentOutput.NormalizePath(span.Path), span.StartLinePosition.Line + 1, OneLine(s.Text));
            })
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();

    /// <summary>The symbols whose contracts or rules carry any of <paramref name="clauses"/>.</summary>
    public static List<string> SymbolsFor(Compilation compilation, IReadOnlyCollection<string> clauses) =>
        Collect(compilation)
            .Where(s => s.Clause is not null && clauses.Contains(s.Clause))
            .Select(s => s.Symbol)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    /// <summary>
    /// Builds the report. With a manifest: missing clauses (VER401) and unknown clause IDs (VER403) make it
    /// refuted; constructs without a clause are listed but do not fail it. Without one: it lists what the code carries.
    /// </summary>
    public static (AgentOutcome Outcome, string Json) Build(Compilation compilation, IReadOnlyList<SpecClause>? manifest, bool indented, int limit, int offset)
    {
        var sites = Collect(compilation);
        var ids = manifest?.Select(c => c.Id).ToList()
                  ?? sites.Where(s => s.Clause is not null).Select(s => s.Clause!).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var known = new HashSet<string>(ids, StringComparer.Ordinal);
        var missing = ids.Where(id => !sites.Any(s => s.Clause == id)).ToList();
        var unknown = manifest is null ? new List<Site>() : sites.Where(s => s.Clause is not null && !known.Contains(s.Clause)).ToList();
        var unclaused = sites.Where(s => s.Clause is null).ToList();
        var outcome = missing.Count > 0 || unknown.Count > 0 ? AgentOutcome.Refuted : AgentOutcome.Proved;
        var page = AgentOutput.Slice(ids.Count, offset, limit);

        var json = AgentOutput.Write(indented, w =>
        {
            void WriteSite(Site s)
            {
                w.WriteStartObject();
                w.WriteString("kind", s.Kind);
                w.WriteString("symbol", s.Symbol);
                w.WriteString("file", s.File);
                w.WriteNumber("line", s.Line);
                w.WriteString("text", s.Text);
                w.WriteEndObject();
            }

            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("clauses"));
            w.WriteString("command", "clauses");
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteBoolean("manifest", manifest is not null);
            w.WriteStartArray("missing");
            foreach (var m in missing) w.WriteStringValue(m);
            w.WriteEndArray();
            w.WriteStartArray("clauses");
            for (var i = page.Offset; i < page.Offset + page.Count; i++)
            {
                var id = ids[i];
                w.WriteStartObject();
                w.WriteString("id", id);
                AgentOutput.StringOrNull(w, "text", manifest?.FirstOrDefault(c => c.Id == id)?.Text);
                w.WriteStartArray("constructs");
                foreach (var s in sites.Where(s => s.Clause == id)) WriteSite(s);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("unknownClauses");
            foreach (var s in unknown)
            {
                w.WriteStartObject();
                w.WriteString("clause", s.Clause);
                w.WriteString("kind", s.Kind);
                w.WriteString("symbol", s.Symbol);
                w.WriteString("file", s.File);
                w.WriteNumber("line", s.Line);
                w.WriteString("text", s.Text);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("withoutClause");
            foreach (var s in unclaused) WriteSite(s);
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, limit);
            w.WriteEndObject();
        });
        return (outcome, json);
    }

    /// <summary>Reads a manifest file; the error is null on success.</summary>
    public static (List<SpecClause>? Clauses, string? Error) ReadManifest(string path)
    {
        if (!File.Exists(path)) return (null, $"Clause manifest not found: `{path}`.");
        return ClauseCoverageAnalyzer.TryParseManifest(File.ReadAllText(path), out var clauses, out var error)
            ? (clauses, null)
            : (null, $"The clause manifest could not be read: {error}.");
    }
}
