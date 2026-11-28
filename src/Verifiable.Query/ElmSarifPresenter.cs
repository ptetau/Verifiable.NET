using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Analyzers;

namespace Verifiable.Query;

internal sealed record PresentedDiagnostic(
    string RuleId,
    string Severity,
    string FilePath,
    int Line,
    int Column,
    string Summary,
    string ElmBlock,
    DiagnosticFacts? Facts = null);

internal sealed record CheckReport(
    string Mode,
    int ErrorCount,
    int WarningCount,
    int SuppressedWarningCount,
    IReadOnlyList<PresentedDiagnostic> Diagnostics);

/// <summary>
/// Parses SARIF 2.1 logs produced by <c>dotnet build -p:ErrorLog=build.sarif,version=2.1</c>
/// and renders every <c>VER*</c> and <c>CS*</c> diagnostic in full multi-line Elm layout
/// (bypassing MSBuild's single-line terminal truncation) with optional Roslyn enrichment.
/// </summary>
internal static class ElmSarifPresenter
{
    public static CheckReport PresentSarif(
        string sarifJson,
        CSharpCompilation? compilation = null,
        bool includeWarnings = true)
    {
        var presented = new List<PresentedDiagnostic>();
        var rawCsEntries = new List<(string RuleId, string Severity, string FilePath, int Line, int EndLine, int Col, int EndCol, string Text)>();
        var errorCount = 0;
        var warningCount = 0;
        var suppressedCount = 0;

        using var doc = JsonDocument.Parse(sarifJson);
        if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return new CheckReport("sarif", 0, 0, 0, presented);
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var r in results.EnumerateArray())
            {
                if (r.TryGetProperty("suppressions", out var sups) && sups.ValueKind == JsonValueKind.Array && sups.GetArrayLength() > 0)
                {
                    suppressedCount++;
                    continue;
                }

                var ruleId = r.TryGetProperty("ruleId", out var rid) ? rid.GetString() ?? string.Empty : string.Empty;
                var level = r.TryGetProperty("level", out var lvl) ? lvl.GetString() ?? "warning" : "warning";
                var severity = level.Equals("error", StringComparison.OrdinalIgnoreCase) ? "error"
                    : level.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "warning"
                    : "note";

                if (severity == "note" || (!includeWarnings && severity == "warning"))
                {
                    continue;
                }

                var messageText = r.TryGetProperty("message", out var msgObj) && msgObj.TryGetProperty("text", out var txt)
                    ? txt.GetString() ?? string.Empty
                    : string.Empty;

                var (filePath, startLine, endLine, startCol, endCol) = ExtractPrimaryLocation(r);

                if (severity == "error") errorCount++;
                else warningCount++;

                // Already-formatted VER* Elm diagnostics: strip the redundant single-line MSBuild summary prefix for clean Elm display
                if (ruleId.StartsWith("VER", StringComparison.OrdinalIgnoreCase) && messageText.Contains("\n-- "))
                {
                    var normalized = messageText.Replace("\r\n", "\n");
                    var bannerIdx = normalized.IndexOf("\n-- ", StringComparison.Ordinal);
                    var summary = normalized.Substring(0, bannerIdx).Trim();
                    var elmBody = normalized.Substring(bannerIdx + 1);
                    var symbol = DiagnosticFacts.EnclosingSymbol(compilation, filePath, startLine, startCol);
                    presented.Add(new PresentedDiagnostic(ruleId, severity, filePath, startLine, startCol, summary, elmBody,
                        DiagnosticFacts.From(ReadCustomProperties(r), elmBody, symbol)));
                    continue;
                }

                if (ruleId.StartsWith("CS", StringComparison.OrdinalIgnoreCase) && severity == "error")
                {
                    rawCsEntries.Add((ruleId, severity, filePath, startLine, endLine, startCol, endCol, messageText));
                    continue;
                }

                // Generic or warning diagnostic: format as clean Elm block with source snippet from disk
                var title = ruleId.Length > 0 ? $"{severity.ToUpperInvariant()} {ruleId}" : severity.ToUpperInvariant();
                var block = FormatFromDiskSnippet(title, filePath, startLine, endLine, startCol, endCol, messageText, fixHint: null);
                presented.Add(new PresentedDiagnostic(ruleId, severity, filePath, startLine, startCol, messageText, block));
            }
        }

        // Enrich CS* errors via Roslyn + ElmCSharpDiagnosticRewriter when a compilation is available
        if (rawCsEntries.Count > 0)
        {
            if (compilation is not null)
            {
                var roslynErrors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("CS", StringComparison.Ordinal))
                    .ToList();

                if (roslynErrors.Count > 0)
                {
                    var rewritten = ElmCSharpDiagnosticRewriter.RewriteAll(compilation, roslynErrors, maxErrors: 8);
                    foreach (var rw in rewritten)
                    {
                        var (f, l, c) = GetLocationCoords(rw.Location);
                        var block = FormatRewrittenElmBlock(rw);
                        presented.Add(new PresentedDiagnostic(rw.DiagnosticId, "error", f, l, c, FirstLine(rw.Message), block));
                    }
                    return new CheckReport("sarif", errorCount, warningCount, suppressedCount, presented);
                }
            }

            foreach (var cs in rawCsEntries.Take(8))
            {
                var block = FormatFromDiskSnippet($"C# COMPILER ERROR ({cs.RuleId})", cs.FilePath, cs.Line, cs.EndLine, cs.Col, cs.EndCol, cs.Text, "Fix the C# compiler error at the highlighted location and rebuild.");
                presented.Add(new PresentedDiagnostic(cs.RuleId, cs.Severity, cs.FilePath, cs.Line, cs.Col, cs.Text, block));
            }
        }

        return new CheckReport("sarif", errorCount, warningCount, suppressedCount, presented);
    }

    internal static string FormatRewrittenElmBlock(ElmCSharpDiagnosticRewriter.RewrittenError rw)
    {
        var sb = new StringBuilder();
        var fileLabel = string.Empty;
        if (rw.Location != Location.None && rw.Location.IsInSource)
        {
            var span = rw.Location.GetMappedLineSpan();
            var path = span.Path ?? rw.Location.SourceTree?.FilePath ?? string.Empty;
            fileLabel = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
        }

        sb.AppendLine(ElmDiagnosticFormatter.BuildHeader(rw.Title, fileLabel));
        sb.AppendLine();
        sb.Append(ElmDiagnosticFormatter.Reflow(rw.Message.Trim()));

        var excerpt = ElmDiagnosticFormatter.RenderExcerpt(rw.Location);
        if (excerpt is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(excerpt);
        }

        if (!string.IsNullOrWhiteSpace(rw.FixHint))
        {
            sb.AppendLine();
            sb.AppendLine();
            var trimmed = rw.FixHint.Trim();
            var prefixed = trimmed.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Fix: {trimmed}";
            sb.Append(ElmDiagnosticFormatter.Reflow(prefixed));
        }

        return sb.ToString();
    }

    /// <summary>Reads the diagnostic properties Roslyn writes to SARIF as <c>properties.customProperties</c>.</summary>
    private static IReadOnlyDictionary<string, string?>? ReadCustomProperties(JsonElement result)
    {
        if (!result.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object ||
            !props.TryGetProperty("customProperties", out var custom) || custom.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in custom.EnumerateObject())
        {
            dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
        }
        return dict;
    }

    private static string FormatFromDiskSnippet(
        string title,
        string filePath,
        int startLine,
        int endLine,
        int startCol,
        int endCol,
        string message,
        string? fixHint)
    {
        var fileLabel = string.IsNullOrEmpty(filePath) ? string.Empty : Path.GetFileName(filePath);
        var sb = new StringBuilder();
        sb.AppendLine(ElmDiagnosticFormatter.BuildHeader(title, fileLabel));
        sb.AppendLine();
        sb.Append(ElmDiagnosticFormatter.Reflow(message.Trim()));

        var excerpt = TryRenderDiskExcerpt(filePath, startLine, endLine, startCol, endCol);
        if (excerpt is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(excerpt);
        }

        if (!string.IsNullOrWhiteSpace(fixHint))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(ElmDiagnosticFormatter.Reflow($"Fix: {fixHint}"));
        }

        return sb.ToString();
    }

    private static string? TryRenderDiskExcerpt(string filePath, int startLine, int endLine, int startCol, int endCol)
    {
        if (string.IsNullOrWhiteSpace(filePath) || startLine <= 0 || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);
            var sIdx = Math.Max(0, Math.Min(startLine - 1, lines.Length - 1));
            var eIdx = Math.Max(sIdx, Math.Min((endLine > 0 ? endLine : startLine) - 1, lines.Length - 1));
            var numWidth = (eIdx + 1).ToString().Length;
            var gutter = new string(' ', numWidth);
            var sb = new StringBuilder();
            sb.AppendLine($"{gutter} |");

            if (sIdx == eIdx)
            {
                var lineText = lines[sIdx];
                var lineNum = (sIdx + 1).ToString().PadLeft(numWidth);
                sb.AppendLine($"{lineNum} | {lineText}");
                var c1 = Math.Max(0, Math.Min(startCol - 1, lineText.Length));
                var c2 = endCol > startCol ? Math.Min(endCol - 1, lineText.Length) : Math.Min(c1 + 1, lineText.Length);
                var len = Math.Max(1, c2 - c1);
                var indent = new StringBuilder(c1);
                for (var i = 0; i < c1; i++) indent.Append(lineText[i] == '\t' ? '\t' : ' ');
                sb.Append($"{gutter} | {indent}{new string('^', len)}");
                return sb.ToString();
            }

            var capEnd = Math.Min(eIdx, sIdx + 5);
            for (var i = sIdx; i <= capEnd; i++)
            {
                var lineNum = (i + 1).ToString().PadLeft(numWidth);
                if (i == capEnd) sb.Append($"{lineNum}|> {lines[i]}");
                else sb.AppendLine($"{lineNum}|> {lines[i]}");
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static (string FilePath, int StartLine, int EndLine, int StartCol, int EndCol) ExtractPrimaryLocation(JsonElement result)
    {
        if (!result.TryGetProperty("locations", out var locs) || locs.ValueKind != JsonValueKind.Array || locs.GetArrayLength() == 0)
        {
            return (string.Empty, 0, 0, 0, 0);
        }

        var first = locs[0];
        if (!first.TryGetProperty("resultFile", out var phys) && !first.TryGetProperty("physicalLocation", out phys))
        {
            return (string.Empty, 0, 0, 0, 0);
        }

        var uri = string.Empty;
        if (phys.TryGetProperty("uri", out var uProp))
        {
            uri = uProp.GetString() ?? string.Empty;
        }
        else if (phys.TryGetProperty("artifactLocation", out var art) && art.TryGetProperty("uri", out var aUri))
        {
            uri = aUri.GetString() ?? string.Empty;
        }

        var filePath = NormalizeSarifUri(uri);
        int sl = 0, el = 0, sc = 0, ec = 0;
        if (phys.TryGetProperty("region", out var reg))
        {
            if (reg.TryGetProperty("startLine", out var p)) sl = p.GetInt32();
            if (reg.TryGetProperty("endLine", out var p2)) el = p2.GetInt32();
            if (reg.TryGetProperty("startColumn", out var p3)) sc = p3.GetInt32();
            if (reg.TryGetProperty("endColumn", out var p4)) ec = p4.GetInt32();
        }

        return (filePath, sl, el > 0 ? el : sl, sc, ec > 0 ? ec : sc + 1);
    }

    private static string NormalizeSarifUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return parsed.LocalPath;
        }
        return uri;
    }

    private static (string File, int Line, int Col) GetLocationCoords(Location loc)
    {
        if (loc == Location.None || !loc.IsInSource) return (string.Empty, 0, 0);
        var span = loc.GetMappedLineSpan();
        var path = span.Path ?? loc.SourceTree?.FilePath ?? string.Empty;
        return (path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOfAny(['\r', '\n']);
        return idx >= 0 ? text.Substring(0, idx).Trim() : text.Trim();
    }
}
