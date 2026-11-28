using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers;

namespace Verifiable.Query;

/// <summary>
/// Builds the <c>verifiable-query clauses</c> JSON report: which constructs carry which spec clause. It uses the
/// same <c>ClauseCatalog</c> as the VER401-VER404 analyzer, so the report and the build cannot disagree.
/// </summary>
internal static class ClauseReport
{
    /// <summary>The result of building a report.</summary>
    internal sealed record Result(string Json, bool HasErrors, string? ErrorMessage);

    public static Result Build(Compilation compilation, string baseDir, string? manifestPath)
    {
        ClauseManifest? manifest = null;
        string? manifestText = null;
        if (manifestPath is not null)
        {
            if (!File.Exists(manifestPath))
            {
                return new Result(string.Empty, true, $"Clause manifest not found: '{manifestPath}'.");
            }

            manifestText = File.ReadAllText(manifestPath);
            manifest = ClauseManifest.Parse(manifestText);
            if (manifest.Errors.Count > 0)
            {
                var lines = manifest.Errors.Select(e => $"{Path.GetFileName(manifestPath)}: {e.Message}");
                return new Result(string.Empty, true, string.Join(Environment.NewLine, lines));
            }
        }

        var constructs = ClauseCatalog.CollectAll(compilation);
        var withClause = constructs.Where(c => c.Clause is not null).ToList();
        var coverage = manifest is null ? null : ClauseCatalog.Compare(manifest, constructs);

        var idOrder = new List<(string Id, string? Text)>();
        if (manifest is not null)
        {
            idOrder.AddRange(manifest.Clauses.Select(c => (c.Id, c.Text)));
        }
        else
        {
            idOrder.AddRange(withClause.Select(c => c.Clause!).Distinct(StringComparer.Ordinal).Select(id => (id, (string?)null)));
        }
        idOrder.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartArray("clauses");
            foreach (var (id, text) in idOrder)
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                if (text is not null) w.WriteString("text", text); else w.WriteNull("text");
                w.WriteStartArray("constructs");
                foreach (var c in Sorted(withClause.Where(c => string.Equals(c.Clause, id, StringComparison.Ordinal))))
                {
                    WriteConstruct(w, c, baseDir);
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("unknownClauses");
            if (coverage is not null)
            {
                foreach (var group in coverage.Unknown.GroupBy(c => c.Clause!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("id", group.Key);
                    w.WriteStartArray("constructs");
                    foreach (var c in Sorted(group)) WriteConstruct(w, c, baseDir);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteStartArray("uncoveredClauses");
            if (coverage is not null)
            {
                foreach (var c in coverage.Uncovered.OrderBy(c => c.Id, StringComparer.Ordinal)) w.WriteStringValue(c.Id);
            }
            w.WriteEndArray();

            w.WriteStartArray("constructsWithoutClause");
            if (coverage is not null)
            {
                foreach (var c in Sorted(coverage.WithoutClause)) WriteConstruct(w, c, baseDir);
            }
            w.WriteEndArray();

            w.WriteStartArray("invalidClauses");
            foreach (var c in Sorted(constructs.Where(c => c.Problem is not null)))
            {
                WriteConstruct(w, c, baseDir, c.Problem);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        var hasErrors = coverage is not null && (coverage.Unknown.Count > 0 || coverage.Uncovered.Count > 0) ||
                        constructs.Any(c => c.Problem is not null);
        return new Result(json, hasErrors, null);
    }

    private static IEnumerable<ClauseConstruct> Sorted(IEnumerable<ClauseConstruct> constructs) =>
        constructs
            .Select(c => (Construct: c, Pos: c.Location.GetLineSpan()))
            .OrderBy(x => x.Construct.SymbolDisplay, StringComparer.Ordinal)
            .ThenBy(x => x.Pos.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Pos.StartLinePosition.Line)
            .ThenBy(x => x.Construct.Kind, StringComparer.Ordinal)
            .Select(x => x.Construct);

    private static void WriteConstruct(Utf8JsonWriter w, ClauseConstruct c, string baseDir, string? problem = null)
    {
        var span = c.Location.GetLineSpan();
        w.WriteStartObject();
        w.WriteString("kind", c.Kind);
        w.WriteString("symbol", c.SymbolDisplay);
        if (c.Because is not null) w.WriteString("because", c.Because); else w.WriteNull("because");
        w.WriteString("file", RelativePath(span.Path, baseDir));
        w.WriteNumber("line", span.StartLinePosition.Line + 1);
        if (problem is not null) w.WriteString("problem", problem);
        w.WriteEndObject();
    }

    private static string RelativePath(string path, string baseDir)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var rel = Path.IsPathRooted(path) ? Path.GetRelativePath(baseDir, path) : path;
        return rel.Replace('\\', '/');
    }
}
