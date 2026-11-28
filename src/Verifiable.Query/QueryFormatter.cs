using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers;

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

    public static string FormatInventory(SymbolInventory inv, string format, bool agentMode, int limit = AgentOutput.DefaultLimit, int offset = 0)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return BuildInventoryJson(inv, indented: true, limit, offset);
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
                .Append(BuildInventoryJson(inv, indented: false, limit, offset))
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

                if (!string.IsNullOrWhiteSpace(ce.FailedClauseBlock))
                {
                    extra.Add(ce.FailedClauseBlock!);
                }
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
        if (location != Location.None && location.IsInSource)
        {
            var span = location.GetMappedLineSpan();
            var path = span.Path ?? location.SourceTree?.FilePath ?? string.Empty;
            fileLabel = string.IsNullOrEmpty(path) ? string.Empty : System.IO.Path.GetFileName(path);
        }

        var sourceExcerpt = ElmDiagnosticFormatter.RenderExcerpt(location);

        sb.AppendLine(BuildBanner(title, fileLabel));
        sb.AppendLine();
        sb.Append(ElmDiagnosticFormatter.Reflow(intro.Trim()));

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
            var prefixed = trimmed.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"Fix: {trimmed}";
            sb.Append(ElmDiagnosticFormatter.Reflow(prefixed));
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
        var outcome = AgentOutput.FromQueryStatus(report.Status);
        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("query"));
            w.WriteString("command", report.Command);
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteString("claim", report.Claim);
            AgentOutput.StringOrNull(w, "assume", NullIfBlank(report.Assume));
            AgentOutput.StringOrNull(w, "because", NullIfBlank(report.Because));
            AgentOutput.StringOrNull(w, "target", NullIfBlank(report.TargetType));
            if (report.ErrorTitle is not null || !string.IsNullOrWhiteSpace(report.ErrorMessage))
            {
                w.WriteStartObject("error");
                AgentOutput.StringOrNull(w, "title", report.ErrorTitle);
                AgentOutput.StringOrNull(w, "message", NullIfBlank(report.ErrorMessage));
                AgentOutput.StringOrNull(w, "fix", NullIfBlank(report.FixHint));
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("error");
            }

            w.WriteStartArray("results");
            foreach (var r in report.Results)
            {
                var rOutcome = AgentOutput.FromQueryStatus(r.Status);
                var (file, line) = Coords(r.MethodLocation);
                w.WriteStartObject();
                w.WriteString("method", r.Method);
                w.WriteString("returnType", r.ReturnType);
                w.WriteString("status", AgentOutput.StatusWord(rOutcome));
                w.WriteBoolean("vacuous", r.Vacuous);
                w.WriteNumber("pathsChecked", r.PathsChecked);
                w.WriteString("file", file);
                w.WriteNumber("line", line);
                AgentOutput.StringOrNull(w, "smtHash",
                    string.IsNullOrEmpty(r.SmtHash) ? null : r.SmtHash!.Length >= 12 ? r.SmtHash.Substring(0, 12) : r.SmtHash);

                w.WriteStartArray("reasons");
                foreach (var pr in r.Reasons)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", pr.Kind);
                    w.WriteString("label", pr.Label);
                    AgentOutput.StringOrNull(w, "spirit", NullIfBlank(pr.Spirit));
                    AgentOutput.StringOrNull(w, "file", string.IsNullOrWhiteSpace(pr.FilePath) ? null : AgentOutput.NormalizePath(pr.FilePath));
                    w.WriteNumber("line", pr.Line);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("counterexamples");
                foreach (var ce in r.Counterexamples)
                {
                    w.WriteStartObject();
                    w.WriteString("file", AgentOutput.NormalizePath(ce.ReturnFile));
                    w.WriteNumber("line", ce.ReturnLine);
                    w.WriteStartObject("values");
                    foreach (var kv in ce.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        w.WriteString(kv.Key, kv.Value);
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                AgentOutput.StringOrNull(w, "error", NullIfBlank(r.ErrorMessage));
                AgentOutput.StringOrNull(w, "fix", NullIfBlank(r.Hint));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static (string File, int Line) Coords(Location loc)
    {
        if (loc == Location.None || !loc.IsInSource) return (string.Empty, 0);
        var span = loc.GetMappedLineSpan();
        return (AgentOutput.NormalizePath(span.Path), span.StartLinePosition.Line + 1);
    }

    private static string BuildInventoryJson(SymbolInventory inv, bool indented, int limit, int offset)
    {
        // One page spans the three lists in order: constrained types, then invariants, then methods.
        var total = inv.ConstrainedTypes.Count + inv.Invariants.Count + inv.Methods.Count;
        var page = AgentOutput.Slice(total, offset, limit);
        bool InPage(int globalIndex) => globalIndex >= page.Offset && globalIndex < page.Offset + page.Count;

        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("list"));
            w.WriteString("command", "list");
            w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
            w.WriteNumber("exitCode", 0);

            var g = 0;
            w.WriteStartArray("constrainedTypes");
            foreach (var c in inv.ConstrainedTypes)
            {
                if (InPage(g++))
                {
                    w.WriteStartObject();
                    w.WriteString("name", c.Name);
                    w.WriteString("underlyingType", c.UnderlyingType);
                    w.WriteString("predicate", c.Predicate);
                    AgentOutput.StringOrNull(w, "description", NullIfBlank(c.Description));
                    w.WriteString("file", AgentOutput.NormalizePath(c.FilePath));
                    w.WriteNumber("line", c.Line);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteStartArray("invariants");
            foreach (var x in inv.Invariants)
            {
                if (InPage(g++))
                {
                    w.WriteStartObject();
                    w.WriteString("type", x.ContainingType);
                    w.WriteString("member", x.MemberName);
                    AgentOutput.StringOrNull(w, "description", NullIfBlank(x.Description));
                    w.WriteString("file", AgentOutput.NormalizePath(x.FilePath));
                    w.WriteNumber("line", x.Line);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteStartArray("methods");
            foreach (var m in inv.Methods)
            {
                if (InPage(g++))
                {
                    w.WriteStartObject();
                    w.WriteString("name", m.DisplayName);
                    w.WriteString("signature", m.Signature);
                    w.WriteString("returnType", m.ReturnType);
                    w.WriteBoolean("isInboundPort", m.IsInboundPort);
                    w.WriteNumber("requiresCount", m.RequiresCount);
                    w.WriteNumber("ensuresCount", m.EnsuresCount);
                    w.WriteString("file", AgentOutput.NormalizePath(m.FilePath));
                    w.WriteNumber("line", m.Line);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, limit);
            w.WriteEndObject();
        });
    }
}
