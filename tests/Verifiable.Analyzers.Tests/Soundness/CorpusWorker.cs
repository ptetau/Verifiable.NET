using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>A diagnostic reduced to plain data, so it can cross the process boundary.</summary>
public sealed record DiagInfo(string Id, int Severity, bool IsSuppressed, string? FilePath, int Start, int Length, int Line, int Column, string Message)
{
    public DiagnosticSeverity Level => (DiagnosticSeverity)Severity;

    public static DiagInfo From(Diagnostic d)
    {
        var inSource = d.Location.IsInSource;
        var span = d.Location.GetLineSpan().StartLinePosition;
        return new DiagInfo(
            d.Id, (int)d.Severity, d.IsSuppressed,
            inSource ? d.Location.SourceTree?.FilePath : null,
            inSource ? d.Location.SourceSpan.Start : 0,
            inSource ? d.Location.SourceSpan.Length : 0,
            span.Line, span.Character, d.GetMessage());
    }
}

/// <summary>The request line the corpus runner sends to the worker.</summary>
public sealed record WorkerRequest(string Source, string Analyzers, string[] References);

/// <summary>The reply line: the diagnostics, or the exception that stopped the analysis.</summary>
public sealed record WorkerResponse(DiagInfo[] Diagnostics, string? Error);

/// <summary>
/// The child-process side of the corpus analysis: reads one JSON request per line on stdin, analyzes, writes one JSON
/// reply per line on stdout. A stack overflow or runaway memory use kills only this process; the test host reports
/// the program as <c>Fatal</c> and starts a new worker.
/// </summary>
internal static class CorpusWorker
{
    public static int Run()
    {
        var protocol = Console.Out;
        Console.SetOut(Console.Error); // anything else that prints must not corrupt the protocol
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            var request = JsonSerializer.Deserialize<WorkerRequest>(line)!;
            WorkerResponse response;
            try
            {
                var analyzers = request.Analyzers == "default"
                    ? CorpusRunner.Analyzers()
                    : [(DiagnosticAnalyzer)new MisbehavingAnalyzer(request.Analyzers)];
                var diagnostics = AnalyzerTestHarness.AnalyzeReportingSuppressedAsync(
                    request.Source, analyzers, CancellationToken.None, request.References).GetAwaiter().GetResult();
                response = new WorkerResponse(diagnostics.Select(DiagInfo.From).ToArray(), null);
            }
            catch (Exception ex)
            {
                response = new WorkerResponse([], ex.ToString());
            }

            protocol.WriteLine(JsonSerializer.Serialize(response));
            protocol.Flush();
        }

        return 0;
    }
}
