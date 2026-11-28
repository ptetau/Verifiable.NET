using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>
/// Runs the corpus analysis in a long-lived child process (<c>--corpus-worker</c>), so a stack overflow, a hang or runaway
/// memory in an analyzer ends that process only. On Linux and macOS the worker is started with a 256 MB stack limit (<c>ulimit -s</c>, which sets the size of
/// thread-pool stacks, where the analyzers run), so deep but finite recursion succeeds; on Windows it gets the default stack. When it dies or is killed, the program being analyzed is reported as fatal
/// and the next program gets a fresh worker.
/// </summary>
internal static class AnalysisWorkerClient
{
    private const int StackKilobytes = 256 * 1024;

    private static readonly object Gate = new();
    private static Process? _worker;
    private static readonly StringBuilder StdErr = new();
    private static string[]? _references;

    public static DiagInfo[] Analyze(string source, string analyzers, TimeSpan timeout, long memoryLimit, List<string> fatal)
    {
        lock (Gate)
        {
            _references ??= AnalyzerTestHarness.CollectDefaultReferences(null)
                .OfType<Microsoft.CodeAnalysis.PortableExecutableReference>()
                .Select(r => r.FilePath!).ToArray();

            var worker = _worker is { HasExited: false } ? _worker : Start();
            var request = JsonSerializer.Serialize(new WorkerRequest(source, analyzers, _references));
            try
            {
                worker.StandardInput.WriteLine(request);
                worker.StandardInput.Flush();
            }
            catch (IOException)
            {
                // fall through: the read below sees the exit
            }

            var read = worker.StandardOutput.ReadLineAsync();
            var clock = Stopwatch.StartNew();
            while (!read.Wait(200))
            {
                if (worker.HasExited) break;
                if (clock.Elapsed > timeout)
                {
                    Kill(worker);
                    fatal.Add($"The analyzers did not finish within {timeout.TotalSeconds:0} s (hang or runaway recursion); the analysis process was killed.");
                    return [];
                }

                long used;
                try { worker.Refresh(); used = worker.WorkingSet64; } catch (InvalidOperationException) { break; }
                if (used > memoryLimit)
                {
                    Kill(worker);
                    fatal.Add($"The analyzers used more than {memoryLimit / (1024 * 1024)} MB of memory; the analysis process was killed.");
                    return [];
                }
            }

            string? line = null;
            if (read.IsCompleted) line = read.Result;
            else if (worker.HasExited) read.Wait(1000);
            if (line is null && read.IsCompleted) line = read.Result;

            if (line is null)
            {
                worker.WaitForExit(2000);
                var code = worker.HasExited ? worker.ExitCode.ToString() : "?";
                var tail = StdErr.ToString();
                tail = tail.Length > 600 ? tail.Substring(0, 600) : tail;
                Kill(worker);
                fatal.Add($"The analysis process died (exit code {code}; a stack overflow ends it with SIGABRT, 134 on Linux, or 0xC00000FD on Windows). {tail.Trim()}");
                return [];
            }

            var response = JsonSerializer.Deserialize<WorkerResponse>(line)!;
            if (response.Error is not null) fatal.Add("The analysis threw: " + response.Error);
            return response.Diagnostics;
        }
    }

    private static Process Start()
    {
        var host = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet"
            ? Environment.ProcessPath!
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var unix = !OperatingSystem.IsWindows();
        var info = new ProcessStartInfo(unix ? "/bin/sh" : host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (unix)
        {
            // The soft limit may be raised up to the hard limit; if that fails the worker simply keeps the default.
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"ulimit -s {StackKilobytes} 2>/dev/null; exec \"$0\" \"$@\"");
            info.ArgumentList.Add(host);
        }

        info.ArgumentList.Add(typeof(AnalysisWorkerClient).Assembly.Location);
        info.ArgumentList.Add("--corpus-worker");

        StdErr.Clear();
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the corpus analysis worker.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (StdErr) { if (StdErr.Length < 4000) StdErr.AppendLine(e.Data); }
        };
        process.BeginErrorReadLine();
        _worker = process;
        return process;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose();
        if (ReferenceEquals(_worker, process)) _worker = null;
    }
}
