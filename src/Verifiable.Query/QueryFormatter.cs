using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Verifiable.Query;

internal static class QueryFormatter
{
    public static string FormatReport(QueryReport report, string format, bool agentMode, bool verbose)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return BuildJsonPayload(report, indented: true);
        }

        var sb = new StringBuilder();

        if (report.ErrorTitle is not null)
        {
            sb.Append(FormatElmBlock(
                report.ErrorTitle,
                report.ErrorLocation,
                report.ErrorMessage ?? "Query failed.",
                extraLines: null,
                report.FixHint));
        }
        else if (report.Results.Count == 1)
        {
            sb.Append(FormatSingleResult(report.Results[0], verbose));
        }
        else
        {
            var title = report.Status switch
            {
                QueryOutcomeStatus.Proved => $"INVARIANT HOLDS ({report.Results.Count}/{report.Results.Count} methods)",
                QueryOutcomeStatus.Refuted => $"INVARIANT REFUTED",
                QueryOutcomeStatus.Z3NotFound => "Z3 SOLVER NOT FOUND",
                QueryOutcomeStatus.Inconclusive => "INVARIANT INCONCLUSIVE",
                _ => "QUERY ERROR"
            };

            sb.AppendLine(BuildBanner(title, report.TargetType ?? string.Empty));
            sb.AppendLine();
            sb.AppendLine($"Claim: {report.Claim}");
            if (!string.IsNullOrWhiteSpace(report.Assume))
            {
                sb.AppendLine($"Assume: {report.Assume}");
            }
            if (!string.IsNullOrWhiteSpace(report.Because))
            {
                sb.AppendLine($"Spirit: {report.Because}");
            }
            sb.AppendLine();

            foreach (var r in report.Results)
            {
                var statusLabel = r.Status switch
                {
                    QueryOutcomeStatus.Proved => r.Vacuous ? "PROVED (VACUOUS)" : "HOLDS",
                    QueryOutcomeStatus.Refuted => "REFUTED",
                    QueryOutcomeStatus.Inconclusive => "INCONCLUSIVE",
                    QueryOutcomeStatus.Z3NotFound => "Z3 NOT FOUND",
                    _ => "ERROR"
                };

                sb.Append($"[{statusLabel}] {r.Method}  ({r.PathsChecked} {(r.PathsChecked == 1 ? "path" : "paths")})");
                if (r.Reasons.Count > 0)
                {
                    var topReasons = string.Join("; ", r.Reasons.Select(FormatCompactReason));
                    sb.Append($"  -- because: {topReasons}");
                }
                sb.AppendLine();

                if (r.Status == QueryOutcomeStatus.Refuted)
                {
                    sb.AppendLine();
                    sb.AppendLine(FormatSingleResult(r, verbose));
                }
            }

            if (report.Status == QueryOutcomeStatus.Proved)
            {
                var passed = report.Results.Count(r => r.Status == QueryOutcomeStatus.Proved);
                sb.AppendLine();
                sb.Append($"All {passed} checked methods satisfy the claim.");
            }
        }

        if (agentMode)
        {
            sb.AppendLine();
            sb.Append("<verifiable-query-result>")
                .Append(BuildJsonPayload(report, indented: false))
                .Append("</verifiable-query-result>");
        }

        return sb.ToString();
    }

    public static string FormatInventory(SymbolInventory inv, string format, bool agentMode)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return BuildInventoryJson(inv, indented: true);
        }

        var sb = new StringBuilder();
        sb.AppendLine(BuildBanner("VERIFIABLE SYMBOLS", string.Empty));
        sb.AppendLine();

        sb.AppendLine($"Constrained types ({inv.ConstrainedTypes.Count}):");
        foreach (var ct in inv.ConstrainedTypes)
        {
            sb.Append($"  {ct.Name} : {ct.UnderlyingType}  where {ct.Predicate}  ({ct.FilePath}:{ct.Line})");
            if (!string.IsNullOrWhiteSpace(ct.Description))
            {
                sb.Append($"  -- \"{ct.Description}\"");
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine($"Invariants ({inv.Invariants.Count}):");
        foreach (var i in inv.Invariants)
        {
            sb.Append($"  {i.ContainingType}.{i.MemberName}  ({i.FilePath}:{i.Line})");
            if (!string.IsNullOrWhiteSpace(i.Description))
            {
                sb.Append($"  -- \"{i.Description}\"");
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine($"Verifiable methods ({inv.Methods.Count}):");
        foreach (var m in inv.Methods)
        {
            var portTag = m.IsInboundPort ? " [InboundPort]" : string.Empty;
            sb.AppendLine($"  {m.Signature} -> {m.ReturnType}{portTag}  [req={m.RequiresCount}, ens={m.EnsuresCount}]  ({m.FilePath}:{m.Line})");
        }

        if (agentMode)
        {
            sb.AppendLine();
            sb.Append("<verifiable-symbols>")
                .Append(BuildInventoryJson(inv, indented: false))
                .Append("</verifiable-symbols>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSingleResult(MethodQueryResult res, bool verbose)
    {
        var extra = new List<string>();

        if (!string.IsNullOrWhiteSpace(res.Because))
        {
            extra.Add($"Why this question matters:\n    {res.Because}");
        }

        extra.Add($"Claim:\n    {res.Claim}");

        if (!string.IsNullOrWhiteSpace(res.Assume))
        {
            extra.Add($"Assumed:\n    {res.Assume}");
        }

        if (res.Status == QueryOutcomeStatus.Proved)
        {
            var title = res.Vacuous ? "QUERY HOLDS (VACUOUSLY)" : "QUERY HOLDS";
            var intro = res.Vacuous
                ? $"Claim holds vacuously in `{res.Method}` across {res.PathsChecked} {(res.PathsChecked == 1 ? "path" : "paths")} (no path satisfied the assumptions)."
                : $"Proved for all reachable execution paths ({res.PathsChecked} checked) in `{res.Method}`.";

            if (res.Reasons.Count > 0)
            {
                var rsb = new StringBuilder();
                rsb.AppendLine("Why it holds (from Z3 proof core):");
                for (var i = 0; i < res.Reasons.Count; i++)
                {
                    var r = res.Reasons[i];
                    var locSuffix = r.Line > 0 && !string.IsNullOrEmpty(r.FilePath) ? $"  ({r.FilePath}:{r.Line})" : string.Empty;
                    rsb.Append("    ").Append(i + 1).Append(". [").Append(r.Kind).Append("] ").Append(r.Label).Append(locSuffix);
                    if (!string.IsNullOrWhiteSpace(r.Spirit))
                    {
                        rsb.AppendLine();
                        rsb.Append("       Spirit: \"").Append(r.Spirit).Append('"');
                    }
                    if (i + 1 < res.Reasons.Count) rsb.AppendLine();
                }
                extra.Add(rsb.ToString());
            }

            if (verbose && res.TraceSteps.Count > 0)
            {
                extra.Add("Trace bindings used:\n    " + string.Join(", ", res.TraceSteps.Select(t => t.Label)));
            }

            return FormatElmBlock(title, res.MethodLocation, intro, extra, res.Hint);
        }

        if (res.Status == QueryOutcomeStatus.Refuted)
        {
            var firstCe = res.Counterexamples.FirstOrDefault();
            var ceLoc = firstCe?.ReturnLocation ?? res.MethodLocation;
            var intro = $"Z3 found inputs where your claim is false in `{res.Method}`.";

            foreach (var ce in res.Counterexamples)
            {
                var csb = new StringBuilder();
                csb.AppendLine($"Counterexample (reaching {ce.ReturnFile}:{ce.ReturnLine}):");
                if (ce.Values.Count == 0)
                {
                    csb.Append("    (solver found a satisfying assignment)");
                }
                else
                {
                    var items = ce.Values.Select(kv => $"    {kv.Key} = {kv.Value}");
                    csb.Append(string.Join(Environment.NewLine, items));
                }
                extra.Add(csb.ToString());
            }

            return FormatElmBlock("QUERY REFUTED", ceLoc, intro, extra, res.Hint);
        }

        if (res.Status == QueryOutcomeStatus.Z3NotFound)
        {
            return FormatElmBlock("Z3 SOLVER NOT FOUND", res.MethodLocation,
                res.ErrorMessage ?? "Z3 solver executable was not found.", extra, res.Hint);
        }

        if (res.Status == QueryOutcomeStatus.Inconclusive)
        {
            return FormatElmBlock("QUERY INCONCLUSIVE", res.MethodLocation,
                $"Z3 could not decide `{res.Claim}` in `{res.Method}`: {res.ErrorMessage ?? "unknown"}.", extra, res.Hint);
        }

        return FormatElmBlock("QUERY ERROR", res.MethodLocation,
            res.ErrorMessage ?? "Query error.", extra, res.Hint);
    }

    private static string FormatCompactReason(QueryReason r)
    {
        var baseText = !string.IsNullOrWhiteSpace(r.Spirit) ? $"{r.Label} (\"{r.Spirit}\")" : r.Label;
        return r.Line > 0 && !string.IsNullOrEmpty(r.FilePath) ? $"{baseText} [{r.FilePath}:{r.Line}]" : baseText;
    }

    private static string FormatElmBlock(
        string title,
        Location location,
        string intro,
        List<string>? extraLines,
        string? fixHint)
    {
        var sb = new StringBuilder();
        var fileLabel = string.Empty;
        string? sourceExcerpt = null;

        if (location != Location.None && location.IsInSource)
        {
            var span = location.GetMappedLineSpan();
            var path = span.Path ?? location.SourceTree?.FilePath ?? string.Empty;
            fileLabel = string.IsNullOrEmpty(path) ? string.Empty : System.IO.Path.GetFileName(path);

            var text = location.SourceTree?.GetText();
            if (text is not null)
            {
                var startLine = span.StartLinePosition.Line;
                if (startLine >= 0 && startLine < text.Lines.Count)
                {
                    var lineText = text.Lines[startLine].ToString();
                    var lineNumber = (startLine + 1).ToString();
                    var gutter = new string(' ', lineNumber.Length);

                    var startCol = Math.Max(0, Math.Min(span.StartLinePosition.Character, lineText.Length));
                    var endCol = span.EndLinePosition.Line == startLine
                        ? Math.Max(startCol + 1, Math.Min(span.EndLinePosition.Character, lineText.Length))
                        : Math.Max(startCol + 1, lineText.Length);
                    var caretLen = Math.Max(1, endCol - startCol);

                    var indent = new StringBuilder(startCol);
                    for (var i = 0; i < startCol; i++)
                    {
                        indent.Append(lineText[i] == '\t' ? '\t' : ' ');
                    }

                    sourceExcerpt = $"{gutter} |\n{lineNumber} | {lineText}\n{gutter} | {indent}{new string('^', caretLen)}";
                }
            }
        }

        sb.AppendLine(BuildBanner(title, fileLabel));
        sb.AppendLine();
        sb.Append(intro.Trim());

        if (sourceExcerpt is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(sourceExcerpt);
        }

        if (extraLines is not null)
        {
            foreach (var sec in extraLines)
            {
                if (string.IsNullOrWhiteSpace(sec)) continue;
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(sec.TrimEnd());
            }
        }

        if (!string.IsNullOrWhiteSpace(fixHint))
        {
            sb.AppendLine();
            sb.AppendLine();
            var trimmed = fixHint!.Trim();
            sb.Append(trimmed.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"Fix: {trimmed}");
        }

        return sb.ToString();
    }

    private static string BuildBanner(string title, string fileLabel)
    {
        var prefix = $"-- {title.Trim()} ";
        var suffix = string.IsNullOrEmpty(fileLabel) ? string.Empty : $" {fileLabel}";
        var dashCount = Math.Max(4, 72 - prefix.Length - suffix.Length);
        return $"{prefix}{new string('-', dashCount)}{suffix}";
    }

    private static string BuildJsonPayload(QueryReport report, bool indented)
    {
        var statusStr = report.Status switch
        {
            QueryOutcomeStatus.Proved => "proved",
            QueryOutcomeStatus.Refuted => "refuted",
            QueryOutcomeStatus.Inconclusive => "inconclusive",
            QueryOutcomeStatus.Z3NotFound => "z3_not_found",
            _ => "error"
        };

        var sep = indented ? "\n" : string.Empty;
        var i1 = indented ? "  " : string.Empty;
        var i2 = indented ? "    " : string.Empty;
        var i3 = indented ? "      " : string.Empty;

        var sb = new StringBuilder();
        sb.Append('{').Append(sep);
        sb.Append(i1).Append("\"command\":").Append(JsonString(report.Command)).Append(',').Append(sep);
        sb.Append(i1).Append("\"status\":").Append(JsonString(statusStr)).Append(',').Append(sep);
        sb.Append(i1).Append("\"exitCode\":").Append((int)report.Status).Append(',').Append(sep);
        sb.Append(i1).Append("\"claim\":").Append(JsonString(report.Claim)).Append(',').Append(sep);
        if (!string.IsNullOrWhiteSpace(report.Assume))
        {
            sb.Append(i1).Append("\"assume\":").Append(JsonString(report.Assume!)).Append(',').Append(sep);
        }
        if (!string.IsNullOrWhiteSpace(report.Because))
        {
            sb.Append(i1).Append("\"because\":").Append(JsonString(report.Because!)).Append(',').Append(sep);
        }
        if (!string.IsNullOrWhiteSpace(report.TargetType))
        {
            sb.Append(i1).Append("\"targetType\":").Append(JsonString(report.TargetType!)).Append(',').Append(sep);
        }
        if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
        {
            sb.Append(i1).Append("\"error\":").Append(JsonString(report.ErrorMessage!)).Append(',').Append(sep);
        }
        if (!string.IsNullOrWhiteSpace(report.FixHint))
        {
            sb.Append(i1).Append("\"fix\":").Append(JsonString(report.FixHint!)).Append(',').Append(sep);
        }

        sb.Append(i1).Append("\"results\":[").Append(sep);
        for (var rIdx = 0; rIdx < report.Results.Count; rIdx++)
        {
            var r = report.Results[rIdx];
            var rStatus = r.Status switch
            {
                QueryOutcomeStatus.Proved => "proved",
                QueryOutcomeStatus.Refuted => "refuted",
                QueryOutcomeStatus.Inconclusive => "inconclusive",
                QueryOutcomeStatus.Z3NotFound => "z3_not_found",
                _ => "error"
            };
            sb.Append(i2).Append('{').Append(sep);
            sb.Append(i3).Append("\"method\":").Append(JsonString(r.Method)).Append(',').Append(sep);
            sb.Append(i3).Append("\"returnType\":").Append(JsonString(r.ReturnType)).Append(',').Append(sep);
            sb.Append(i3).Append("\"status\":").Append(JsonString(rStatus)).Append(',').Append(sep);
            sb.Append(i3).Append("\"vacuous\":").Append(r.Vacuous ? "true" : "false").Append(',').Append(sep);
            sb.Append(i3).Append("\"pathsChecked\":").Append(r.PathsChecked).Append(',').Append(sep);
            if (!string.IsNullOrEmpty(r.SmtHash))
            {
                var shortHash = r.SmtHash!.Length >= 12 ? r.SmtHash.Substring(0, 12) : r.SmtHash;
                sb.Append(i3).Append("\"smtHash\":").Append(JsonString(shortHash)).Append(',').Append(sep);
            }

            sb.Append(i3).Append("\"reasons\":[");
            for (var j = 0; j < r.Reasons.Count; j++)
            {
                var pr = r.Reasons[j];
                if (j > 0) sb.Append(',');
                sb.Append("{\"kind\":").Append(JsonString(pr.Kind))
                    .Append(",\"label\":").Append(JsonString(pr.Label));
                if (!string.IsNullOrWhiteSpace(pr.Spirit))
                    sb.Append(",\"spirit\":").Append(JsonString(pr.Spirit!));
                if (!string.IsNullOrWhiteSpace(pr.FilePath))
                    sb.Append(",\"file\":").Append(JsonString(pr.FilePath!)).Append(",\"line\":").Append(pr.Line);
                sb.Append('}');
            }
            sb.Append("],").Append(sep);

            sb.Append(i3).Append("\"counterexamples\":[");
            for (var c = 0; c < r.Counterexamples.Count; c++)
            {
                var ce = r.Counterexamples[c];
                if (c > 0) sb.Append(',');
                sb.Append("{\"file\":").Append(JsonString(ce.ReturnFile))
                    .Append(",\"line\":").Append(ce.ReturnLine)
                    .Append(",\"values\":{");
                var k = 0;
                foreach (var kv in ce.Values)
                {
                    if (k++ > 0) sb.Append(',');
                    sb.Append(JsonString(kv.Key)).Append(':').Append(JsonString(kv.Value));
                }
                sb.Append("}}");
            }
            sb.Append(']');

            if (!string.IsNullOrWhiteSpace(r.ErrorMessage))
            {
                sb.Append(',').Append(sep).Append(i3).Append("\"error\":").Append(JsonString(r.ErrorMessage!));
            }
            if (!string.IsNullOrWhiteSpace(r.Hint))
            {
                sb.Append(',').Append(sep).Append(i3).Append("\"hint\":").Append(JsonString(r.Hint!));
            }

            sb.Append(sep).Append(i2).Append('}');
            if (rIdx + 1 < report.Results.Count) sb.Append(',');
            sb.Append(sep);
        }

        sb.Append(i1).Append(']').Append(sep);
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildInventoryJson(SymbolInventory inv, bool indented)
    {
        var sep = indented ? "\n" : string.Empty;
        var i1 = indented ? "  " : string.Empty;
        var sb = new StringBuilder();
        sb.Append('{').Append(sep);

        sb.Append(i1).Append("\"constrainedTypes\":[");
        for (var i = 0; i < inv.ConstrainedTypes.Count; i++)
        {
            var c = inv.ConstrainedTypes[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(JsonString(c.Name))
                .Append(",\"underlyingType\":").Append(JsonString(c.UnderlyingType))
                .Append(",\"predicate\":").Append(JsonString(c.Predicate));
            if (!string.IsNullOrWhiteSpace(c.Description))
                sb.Append(",\"description\":").Append(JsonString(c.Description!));
            sb.Append(",\"file\":").Append(JsonString(c.FilePath)).Append(",\"line\":").Append(c.Line).Append('}');
        }
        sb.Append("],").Append(sep);

        sb.Append(i1).Append("\"invariants\":[");
        for (var i = 0; i < inv.Invariants.Count; i++)
        {
            var x = inv.Invariants[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"type\":").Append(JsonString(x.ContainingType))
                .Append(",\"member\":").Append(JsonString(x.MemberName));
            if (!string.IsNullOrWhiteSpace(x.Description))
                sb.Append(",\"description\":").Append(JsonString(x.Description!));
            sb.Append(",\"file\":").Append(JsonString(x.FilePath)).Append(",\"line\":").Append(x.Line).Append('}');
        }
        sb.Append("],").Append(sep);

        sb.Append(i1).Append("\"methods\":[");
        for (var i = 0; i < inv.Methods.Count; i++)
        {
            var m = inv.Methods[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(JsonString(m.DisplayName))
                .Append(",\"signature\":").Append(JsonString(m.Signature))
                .Append(",\"returnType\":").Append(JsonString(m.ReturnType))
                .Append(",\"isInboundPort\":").Append(m.IsInboundPort ? "true" : "false")
                .Append(",\"requiresCount\":").Append(m.RequiresCount)
                .Append(",\"ensuresCount\":").Append(m.EnsuresCount)
                .Append(",\"file\":").Append(JsonString(m.FilePath))
                .Append(",\"line\":").Append(m.Line).Append('}');
        }
        sb.Append(']').Append(sep);
        sb.Append('}');
        return sb.ToString();
    }

    private static string JsonString(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 32) sb.Append($"\\u{(int)ch:x4}");
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
