using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Analyzers;
using Verifiable.Analyzers.Smt;

namespace Verifiable.Query;

/// <summary>
/// Full in-process Elm diagnostic checker (<c>verifiable-query check</c>) and <c>dotnet build</c>
/// SARIF wrapper (<c>verifiable-query build</c>), running Roslyn C# checks, all Verifiable analyzers,
/// and <see cref="VerifiableNullabilitySuppressor"/> without MSBuild's single-line truncation.
/// </summary>
internal static class ElmCheckEngine
{
    public static CheckReport CheckCompilation(
        CSharpCompilation compilation,
        IReadOnlyList<Diagnostic> compilationErrors,
        string? z3Path,
        bool includeWarnings = true)
    {
        if (compilationErrors.Count > 0)
        {
            var rewritten = ElmCSharpDiagnosticRewriter.RewriteAll(compilation, compilationErrors, maxErrors: 8);
            var list = new List<PresentedDiagnostic>(rewritten.Count);
            foreach (var rw in rewritten)
            {
                var (file, line, col) = GetCoords(rw.Location);
                list.Add(new PresentedDiagnostic(
                    rw.DiagnosticId,
                    "error",
                    file,
                    line,
                    col,
                    FirstLine(rw.Message),
                    ElmSarifPresenter.FormatRewrittenElmBlock(rw)));
            }
            return new CheckReport("check", compilationErrors.Count, 0, 0, list);
        }

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
            new VerifiableGatekeeperAnalyzer(),
            new LinearObligationAnalyzer(),
            new SpecQualityAnalyzer(),
            new VerifiableSmtAnalyzer(),
            new ProofSuppressionAuditor(),
            new VerifiableNullabilitySuppressor());

        var configProvider = new DictAnalyzerConfigOptionsProvider(z3Path);
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, configProvider);
        var withAnalyzersOptions = new CompilationWithAnalyzersOptions(
            analyzerOptions,
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: true);

        var cwa = compilation.WithAnalyzers(analyzers, withAnalyzersOptions);
        var allDiags = cwa.GetAllDiagnosticsAsync().GetAwaiter().GetResult();

        var presented = new List<PresentedDiagnostic>();
        var errorCount = 0;
        var warningCount = 0;
        var suppressedCount = 0;

        foreach (var d in allDiags
            .OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1)
            .ThenBy(d => d.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start))
        {
            if (d.IsSuppressed)
            {
                if (d.Id is "CS8602" or "CS8604")
                {
                    suppressedCount++;
                }
                continue;
            }

            if (d.Severity == DiagnosticSeverity.Hidden || d.Severity == DiagnosticSeverity.Info)
            {
                continue;
            }

            if (d.Severity == DiagnosticSeverity.Warning && !includeWarnings)
            {
                continue;
            }

            var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            if (d.Severity == DiagnosticSeverity.Error) errorCount++;
            else warningCount++;

            var (file, line, col) = GetCoords(d.Location);
            var rawMsg = d.GetMessage();

            if (d.Id.StartsWith("VER", StringComparison.OrdinalIgnoreCase) && rawMsg.Contains("\n-- "))
            {
                var normalized = rawMsg.Replace("\r\n", "\n");
                var bannerIdx = normalized.IndexOf("\n-- ", StringComparison.Ordinal);
                var summary = normalized.Substring(0, bannerIdx).Trim();
                var elmBody = normalized.Substring(bannerIdx + 1);
                presented.Add(new PresentedDiagnostic(d.Id, severity, file, line, col, summary, elmBody,
                    DiagnosticFacts.From(d.Properties, elmBody, DiagnosticFacts.EnclosingSymbol(compilation, d.Location))));
            }
            else if (d.Id.StartsWith("CS", StringComparison.OrdinalIgnoreCase))
            {
                var rw = ElmCSharpDiagnosticRewriter.Rewrite(compilation, [d]);
                presented.Add(new PresentedDiagnostic(d.Id, severity, file, line, col, FirstLine(rw.Message), ElmSarifPresenter.FormatRewrittenElmBlock(rw)));
            }
            else
            {
                var block = ElmDiagnosticFormatter.Format(
                    $"{severity.ToUpperInvariant()} {d.Id}",
                    d.Location,
                    rawMsg);
                var normalized = block.Replace("\r\n", "\n");
                var bannerIdx = normalized.IndexOf("\n-- ", StringComparison.Ordinal);
                var elmBody = bannerIdx >= 0 ? normalized.Substring(bannerIdx + 1) : normalized;
                presented.Add(new PresentedDiagnostic(d.Id, severity, file, line, col, FirstLine(rawMsg), elmBody,
                    DiagnosticFacts.From(d.Properties, elmBody, DiagnosticFacts.EnclosingSymbol(compilation, d.Location))));
            }
        }

        return new CheckReport("check", errorCount, warningCount, suppressedCount, presented);
    }

    public static (int ExitCode, CheckReport Report, string? BuildError) RunDotnetBuild(
        string targetPath,
        IReadOnlyList<string> extraArgs,
        bool includeWarnings = true)
    {
        var tempSarif = Path.Combine(Path.GetTempPath(), $"verifiable-elm-{Guid.NewGuid():N}.sarif");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(targetPath);
            psi.ArgumentList.Add($"-p:ErrorLog={tempSarif},version=2.1");
            psi.ArgumentList.Add("--nologo");
            foreach (var arg in extraArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (3, new CheckReport("build", 1, 0, 0, Array.Empty<PresentedDiagnostic>()), "Failed to launch `dotnet build`.");
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (File.Exists(tempSarif))
            {
                var sarifText = File.ReadAllText(tempSarif);
                CSharpCompilation? comp = null;
                if (targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
                {
                    var loaded = WorkspaceLoader.Load(targetPath, Array.Empty<string>(), null);
                    comp = loaded.Compilation;
                }

                var report = ElmSarifPresenter.PresentSarif(sarifText, comp, includeWarnings);
                var exit = proc.ExitCode == 0 ? 0 : 1;
                return (exit, report with { Mode = "build" }, null);
            }

            if (proc.ExitCode == 0)
            {
                return (0, new CheckReport("build", 0, 0, 0, Array.Empty<PresentedDiagnostic>()), null);
            }

            var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stderr}\n{stdout}";
            return (1, new CheckReport("build", 1, 0, 0, Array.Empty<PresentedDiagnostic>()), combined.Trim());
        }
        finally
        {
            try
            {
                if (File.Exists(tempSarif)) File.Delete(tempSarif);
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    /// <summary>
    /// The outcome of a check or build, in the same five words every command uses: code that does not
    /// compile is an input error, a missing Z3 is a missing tool, any other error is a refutation unless
    /// every error is an undecided proof (VER103, VER106).
    /// </summary>
    public static AgentOutcome Classify(CheckReport report)
    {
        var errors = report.Diagnostics.Where(d => d.Severity == "error").ToList();
        if (errors.Count == 0) return report.ErrorCount > 0 ? AgentOutcome.Refuted : AgentOutcome.Proved;
        if (errors.Any(d => d.RuleId.StartsWith("CS", StringComparison.Ordinal))) return AgentOutcome.InputError;
        if (errors.Any(d => d.RuleId == "VER104")) return AgentOutcome.ToolMissing;
        if (errors.All(d => d.RuleId is "VER103" or "VER106")) return AgentOutcome.Undecided;
        return AgentOutcome.Refuted;
    }

    public static string FormatCheckReport(
        CheckReport report,
        string format,
        bool agentMode,
        int limit = AgentOutput.DefaultLimit,
        int offset = 0)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return BuildJson(report, indented: true, limit, offset);
        }

        var sb = new StringBuilder();
        if (report.Diagnostics.Count == 0)
        {
            sb.AppendLine(ElmDiagnosticFormatter.BuildHeader("CHECK PASSED", string.Empty));
            sb.AppendLine();
            sb.Append("All C# compilation, Gatekeeper, Linear Obligation, and Z3 SMT proof checks passed with 0 errors.");
            if (report.SuppressedWarningCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append($"Note: {report.SuppressedWarningCount} C# nullable {(report.SuppressedWarningCount == 1 ? "warning was" : "warnings were")} automatically suppressed after Z3 proved non-null.");
            }
        }
        else
        {
            for (var i = 0; i < report.Diagnostics.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }
                sb.Append(report.Diagnostics[i].ElmBlock.TrimEnd());
            }

            sb.AppendLine();
            sb.AppendLine();
            var summaryParts = new List<string>();
            if (report.ErrorCount > 0) summaryParts.Add($"{report.ErrorCount} {(report.ErrorCount == 1 ? "error" : "errors")}");
            if (report.WarningCount > 0) summaryParts.Add($"{report.WarningCount} {(report.WarningCount == 1 ? "warning" : "warnings")}");
            if (report.SuppressedWarningCount > 0) summaryParts.Add($"{report.SuppressedWarningCount} Z3-proved nullable suppressed");
            sb.Append($"Summary: {string.Join(", ", summaryParts)}.");
        }

        if (agentMode)
        {
            sb.AppendLine();
            sb.Append("<verifiable-check-result>")
                .Append(BuildJson(report, indented: false, limit, offset))
                .Append("</verifiable-check-result>");
        }

        return sb.ToString();
    }

    internal static string BuildJson(CheckReport report, bool indented, int limit, int offset)
    {
        var outcome = Classify(report);
        var page = AgentOutput.Slice(report.Diagnostics.Count, offset, limit);
        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("check"));
            w.WriteString("command", report.Mode);
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteNumber("errors", report.ErrorCount);
            w.WriteNumber("warnings", report.WarningCount);
            w.WriteNumber("suppressedNullableWarnings", report.SuppressedWarningCount);
            w.WriteStartArray("diagnostics");
            for (var idx = page.Offset; idx < page.Offset + page.Count; idx++)
            {
                WriteDiagnostic(w, report.Diagnostics[idx]);
            }
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, limit);
            w.WriteEndObject();
        });
    }

    /// <summary>
    /// One diagnostic in the shape every command shares: rule, severity, clause, symbol, file and
    /// line, contract, reason, counterexample, fix, and a one-line summary.
    /// </summary>
    internal static void WriteDiagnostic(System.Text.Json.Utf8JsonWriter w, PresentedDiagnostic d)
    {
        var facts = d.Facts ?? DiagnosticFacts.From(null, d.ElmBlock, null);
        w.WriteStartObject();
        w.WriteString("rule", d.RuleId);
        w.WriteString("severity", d.Severity);
        AgentOutput.StringOrNull(w, "clause", facts.Clause);
        AgentOutput.StringOrNull(w, "symbol", facts.Symbol);
        w.WriteString("file", AgentOutput.NormalizePath(d.FilePath));
        w.WriteNumber("line", d.Line);
        w.WriteNumber("column", d.Column);
        AgentOutput.StringOrNull(w, "contract", facts.Contract);
        AgentOutput.StringOrNull(w, "reason", facts.Reason);
        AgentOutput.RawOrNull(w, "counterexample", facts.CounterexampleJson);
        AgentOutput.StringOrNull(w, "fix", facts.Fix);
        w.WriteString("summary", d.Summary);
        w.WriteEndObject();
    }

    private static (string File, int Line, int Col) GetCoords(Location loc)
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

    private sealed class DictAnalyzerConfigOptionsProvider(string? z3Path) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global = new DictOptions(z3Path);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;

        private sealed class DictOptions(string? z3Path) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (key == "build_property.VerifiableZ3Path" && !string.IsNullOrWhiteSpace(z3Path))
                {
                    value = z3Path!;
                    return true;
                }
                value = string.Empty;
                return false;
            }
        }
    }
}
