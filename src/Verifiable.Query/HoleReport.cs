using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers;

namespace Verifiable.Query;

/// <summary>
/// <c>verifiable-query holes</c> (requirement R4): the work list of unwritten bodies, each with its
/// signature, contracts and clause text (schema <c>verifiable-query/holes@1</c>). Exit 1 while any remain.
/// </summary>
internal static class HoleReport
{
    public static List<HoleInfo> Collect(Compilation compilation, IReadOnlyList<SpecClause>? manifest)
    {
        var texts = manifest?.ToDictionary(c => c.Id, c => c.Text, StringComparer.Ordinal) ?? new Dictionary<string, string?>();
        return compilation.SyntaxTrees
            .SelectMany(Holes.Find)
            .Select(inv => Holes.Describe(inv, texts))
            .OrderBy(h => h.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(h => h.Location.SourceSpan.Start)
            .ToList();
    }

    public static (AgentOutcome Outcome, string Json) Build(Compilation compilation, IReadOnlyList<SpecClause>? manifest, bool indented, int limit, int offset)
    {
        var holes = Collect(compilation, manifest);
        var outcome = holes.Count == 0 ? AgentOutcome.Proved : AgentOutcome.Refuted;
        var page = AgentOutput.Slice(holes.Count, offset, limit);
        var json = AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("holes"));
            w.WriteString("command", "holes");
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteStartArray("holes");
            for (var i = page.Offset; i < page.Offset + page.Count; i++)
            {
                var h = holes[i];
                var span = h.Location.GetLineSpan();
                w.WriteStartObject();
                w.WriteString("rule", "VER108");
                w.WriteString("symbol", h.Symbol);
                AgentOutput.StringOrNull(w, "clause", h.Clause);
                AgentOutput.StringOrNull(w, "clauseText", h.ClauseText);
                w.WriteString("signature", h.Signature);
                w.WriteStartArray("contracts");
                foreach (var c in h.Contracts) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteString("file", AgentOutput.NormalizePath(span.Path));
                w.WriteNumber("line", span.StartLinePosition.Line + 1);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, limit);
            w.WriteEndObject();
        });
        return (outcome, json);
    }
}
