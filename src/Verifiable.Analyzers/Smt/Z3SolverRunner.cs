using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Verifiable.Analyzers.Smt;

// Out-of-process Z3 execution is an intentional architectural boundary (§5.1) to avoid native P/Invoke AssemblyLoadContext crashes.
#pragma warning disable RS1035

internal enum SmtStatus
{
    Unsat = 0,
    Sat = 1,
    Unknown = 2,
    SolverUnavailable = 3,
    Error = 4
}

internal sealed record SmtQueryResult(
    SmtStatus Status,
    string QueryHash,
    IReadOnlyDictionary<string, string> Model,
    string? ErrorMessage);

internal static class Z3SolverRunner
{
    private const string DoneSentinel = "__VERIFIABLE_DONE__";
    private const string ModelSentinel = "__VERIFIABLE_MODEL_DONE__";
    private const int DefaultTimeoutMs = 2500;

    private static readonly ConcurrentDictionary<string, SmtQueryResult> MemoryCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, WorkerPool> PoolsByPath = new(StringComparer.OrdinalIgnoreCase);

    public static string? DiscoverZ3Path(string? msbuildZ3Path = null, string? sourceFilePath = null)
    {
        var envPath = Environment.GetEnvironmentVariable("VERIFIABLE_Z3_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        if (!string.IsNullOrWhiteSpace(msbuildZ3Path) && File.Exists(msbuildZ3Path))
        {
            return Path.GetFullPath(msbuildZ3Path);
        }

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "z3.exe" : "z3";

        if (!string.IsNullOrWhiteSpace(sourceFilePath))
        {
            var dir = Path.GetDirectoryName(sourceFilePath);
            for (var depth = 0; depth < 7 && !string.IsNullOrEmpty(dir); depth++)
            {
                var candidate = Path.Combine(dir!, ".tools", "z3", exeName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            var homeCandidate = Path.Combine(userProfile, ".verifiable", "z3", exeName);
            if (File.Exists(homeCandidate))
            {
                return Path.GetFullPath(homeCandidate);
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var entry in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                try
                {
                    var candidate = Path.Combine(entry.Trim(), exeName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries
                }
            }
        }

        return null;
    }

    public static string ComputeSha256(string text)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    public static SmtQueryResult Execute(
        string smtScriptBody,
        string? msbuildZ3Path = null,
        string? sourceFilePath = null,
        int timeoutMs = DefaultTimeoutMs)
    {
        var z3Path = DiscoverZ3Path(msbuildZ3Path, sourceFilePath);
        if (z3Path is null)
        {
            return new SmtQueryResult(SmtStatus.SolverUnavailable, string.Empty, new Dictionary<string, string>(), "Z3 binary not found.");
        }

        var hash = ComputeSha256(z3Path + "\n" + timeoutMs + "\n" + smtScriptBody);
        if (MemoryCache.TryGetValue(hash, out var cached))
        {
            return cached;
        }

        if (TryLoadDiskCache(hash, out var diskCached))
        {
            MemoryCache.TryAdd(hash, diskCached!);
            return diskCached!;
        }

        var pool = PoolsByPath.GetOrAdd(z3Path, p => new WorkerPool(p));
        var result = pool.RunQuery(smtScriptBody, hash, timeoutMs);

        if (result.Status is SmtStatus.Unsat or SmtStatus.Sat)
        {
            MemoryCache.TryAdd(hash, result);
            TrySaveDiskCache(hash, result);
        }

        return result;
    }

    private static string GetDiskCacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "verifiable-smt-cache-v1");

    private static bool TryLoadDiskCache(string hash, out SmtQueryResult? result)
    {
        result = null;
        try
        {
            var path = Path.Combine(GetDiskCacheDirectory(), hash + ".cache");
            if (!File.Exists(path)) return false;

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return false;

            if (!Enum.TryParse<SmtStatus>(lines[0], out var status)) return false;
            var model = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < lines.Length; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq > 0)
                {
                    model[lines[i].Substring(0, eq)] = lines[i].Substring(eq + 1);
                }
            }

            result = new SmtQueryResult(status, hash, model, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySaveDiskCache(string hash, SmtQueryResult result)
    {
        try
        {
            var dir = GetDiskCacheDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, hash + ".cache");
            var lines = new List<string>(result.Model.Count + 1) { result.Status.ToString() };
            foreach (var kv in result.Model)
            {
                lines.Add($"{kv.Key}={kv.Value.Replace('\r', ' ').Replace('\n', ' ')}");
            }
            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
        catch
        {
            // Cache write failures are non-fatal
        }
    }

    internal static Dictionary<string, string> ParseModel(string modelOutput)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return result;
        }

        var pos = 0;
        var len = modelOutput.Length;
        while (pos < len)
        {
            var idx = modelOutput.IndexOf("(define-fun ", pos, StringComparison.Ordinal);
            if (idx < 0) break;

            var form = ReadBalancedForm(modelOutput, idx, out var nextPos);
            pos = nextPos > idx ? nextPos : idx + 12;
            if (form is null) continue;

            if (TryParseDefineFun(form, out var name, out var valueText))
            {
                result[name] = valueText;
            }
        }

        return result;
    }

    private static string? ReadBalancedForm(string text, int start, out int endPos)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    endPos = i + 1;
                    return text.Substring(start, endPos - start);
                }
            }
        }

        endPos = text.Length;
        return null;
    }

    private static bool TryParseDefineFun(string form, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var tokens = TokenizeTopLevelChildren(form);
        // Expected: (define-fun <name> (<params>) <sort> <body...>)
        if (tokens.Count < 5 || tokens[0] != "define-fun")
        {
            return false;
        }

        // Only collect 0-arity bindings (constants)
        if (tokens[2] != "()")
        {
            return false;
        }

        name = tokens[1];
        value = FormatSmtValue(tokens[4]);
        return true;
    }

    private static List<string> TokenizeTopLevelChildren(string sexp)
    {
        var items = new List<string>();
        var i = 1; // skip outer '('
        var limit = sexp.Length - 1;
        while (i < limit)
        {
            while (i < limit && char.IsWhiteSpace(sexp[i])) i++;
            if (i >= limit) break;

            if (sexp[i] == '(')
            {
                var form = ReadBalancedForm(sexp, i, out var next);
                if (form is not null)
                {
                    items.Add(form);
                    i = next;
                }
                else
                {
                    break;
                }
            }
            else if (sexp[i] == '"')
            {
                var start = i++;
                while (i < limit && !(sexp[i] == '"' && sexp[i - 1] != '\\')) i++;
                if (i < limit) i++;
                items.Add(sexp.Substring(start, i - start));
            }
            else
            {
                var start = i;
                while (i < limit && !char.IsWhiteSpace(sexp[i]) && sexp[i] != '(' && sexp[i] != ')') i++;
                items.Add(sexp.Substring(start, i - start));
            }
        }

        return items;
    }

    internal static string FormatSmtValue(string raw)
    {
        raw = raw.Trim();
        if (!raw.StartsWith("(", StringComparison.Ordinal))
        {
            if (raw.StartsWith("#x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(raw.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexVal))
            {
                return raw.Length == 10 ? ((int)(uint)hexVal).ToString(System.Globalization.CultureInfo.InvariantCulture) : hexVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return raw;
        }

        var parts = TokenizeTopLevelChildren(raw);
        if (parts.Count == 2 && parts[0] == "-")
        {
            var inner = FormatSmtValue(parts[1]);
            return inner.StartsWith("-", StringComparison.Ordinal) ? inner.Substring(1) : "-" + inner;
        }

        if (parts.Count == 3 && parts[0] == "/")
        {
            var numStr = FormatSmtValue(parts[1]);
            var denStr = FormatSmtValue(parts[2]);
            if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var num) &&
                decimal.TryParse(denStr, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var den) &&
                den != 0m)
            {
                var dec = num / den;
                return dec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
            }
            return $"{numStr}/{denStr}";
        }

        if (parts.Count == 3 && parts[0] == "_" && parts[1].StartsWith("bv", StringComparison.Ordinal))
        {
            if (long.TryParse(parts[1].Substring(2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bv))
            {
                if (parts[2] == "32")
                {
                    return unchecked((int)(uint)bv).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return bv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (parts.Count == 1 && parts[0] == "str-null")
        {
            return "null";
        }

        if (parts.Count == 2 && parts[0] == "str-val")
        {
            return parts[1];
        }

        if (parts.Count >= 2 && parts[0].StartsWith("mk-Dt_", StringComparison.Ordinal))
        {
            var typeName = parts[0].Substring("mk-Dt_".Length);
            var formattedArgs = new List<string>(parts.Count - 1);
            for (var k = 1; k < parts.Count; k++)
            {
                formattedArgs.Add(FormatSmtValue(parts[k]));
            }
            return $"{typeName}({string.Join(", ", formattedArgs)})";
        }

        return raw;
    }

    private sealed class WorkerPool
    {
        private readonly string _z3Path;
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentBag<Z3Worker> _workers = new();

        public WorkerPool(string z3Path)
        {
            _z3Path = z3Path;
            var maxWorkers = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            _semaphore = new SemaphoreSlim(maxWorkers, maxWorkers);
        }

        public SmtQueryResult RunQuery(string scriptBody, string hash, int timeoutMs)
        {
            _semaphore.Wait();
            Z3Worker? worker = null;
            var recycle = false;
            try
            {
                if (!_workers.TryTake(out worker) || !worker.IsAlive)
                {
                    worker?.Dispose();
                    worker = Z3Worker.Start(_z3Path);
                }

                var result = worker.Execute(scriptBody, hash, timeoutMs, out recycle);
                return result;
            }
            catch (Exception ex)
            {
                recycle = true;
                return new SmtQueryResult(SmtStatus.Error, hash, new Dictionary<string, string>(), ex.Message);
            }
            finally
            {
                if (worker is not null)
                {
                    if (recycle || !worker.IsAlive)
                    {
                        worker.Dispose();
                    }
                    else
                    {
                        _workers.Add(worker);
                    }
                }
                _semaphore.Release();
            }
        }
    }

    private sealed class Z3Worker : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;

        private Z3Worker(Process process)
        {
            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
        }

        public bool IsAlive => !_process.HasExited;

        public static Z3Worker Start(string z3Path)
        {
            var psi = new ProcessStartInfo
            {
                FileName = z3Path,
                Arguments = "-in",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start Z3 at '{z3Path}'.");
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            return new Z3Worker(process);
        }

        public SmtQueryResult Execute(string scriptBody, string hash, int timeoutMs, out bool shouldRecycle)
        {
            shouldRecycle = false;

            _stdin.WriteLine("(reset)");
            _stdin.WriteLine($"(set-option :timeout {timeoutMs})");
            _stdin.WriteLine("(set-option :model true)");
            _stdin.WriteLine(scriptBody);
            _stdin.WriteLine("(check-sat)");
            _stdin.WriteLine($"(echo \"{DoneSentinel}\")");
            _stdin.Flush();

            var hardWaitMs = timeoutMs + 2500;
            var checkOutput = ReadUntilSentinel(DoneSentinel, hardWaitMs, out var timedOut);
            if (timedOut)
            {
                shouldRecycle = true;
                return new SmtQueryResult(SmtStatus.Unknown, hash, new Dictionary<string, string>(), $"Z3 exceeded wall-clock timeout ({hardWaitMs}ms).");
            }

            var status = ParseStatus(checkOutput, out var errorLine);
            if (status == SmtStatus.Error)
            {
                shouldRecycle = true;
                return new SmtQueryResult(SmtStatus.Error, hash, new Dictionary<string, string>(), errorLine ?? "Z3 returned an error.");
            }

            if (status != SmtStatus.Sat)
            {
                return new SmtQueryResult(status, hash, new Dictionary<string, string>(), status == SmtStatus.Unknown ? "Z3 returned unknown." : null);
            }

            _stdin.WriteLine("(get-model)");
            _stdin.WriteLine($"(echo \"{ModelSentinel}\")");
            _stdin.Flush();

            var modelOutput = ReadUntilSentinel(ModelSentinel, hardWaitMs, out var modelTimedOut);
            if (modelTimedOut)
            {
                shouldRecycle = true;
                return new SmtQueryResult(SmtStatus.Sat, hash, new Dictionary<string, string>(), null);
            }

            var model = ParseModel(modelOutput);
            return new SmtQueryResult(SmtStatus.Sat, hash, model, null);
        }

        private string ReadUntilSentinel(string sentinel, int timeoutMs, out bool timedOut)
        {
            var sb = new StringBuilder();
            var readTask = Task.Run(() =>
            {
                while (true)
                {
                    var line = _stdout.ReadLine();
                    if (line is null)
                    {
                        break;
                    }

                    var trimmed = line.Trim().Trim('"');
                    if (trimmed == sentinel)
                    {
                        break;
                    }

                    sb.AppendLine(line);
                }
            });

            if (!readTask.Wait(timeoutMs))
            {
                timedOut = true;
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch
                {
                    // Ignore kill races
                }
                return sb.ToString();
            }

            timedOut = false;
            return sb.ToString();
        }

        private static SmtStatus ParseStatus(string output, out string? error)
        {
            error = null;
            using var reader = new StringReader(output);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed == "unsat") return SmtStatus.Unsat;
                if (trimmed == "sat") return SmtStatus.Sat;
                if (trimmed == "unknown") return SmtStatus.Unknown;
                if (trimmed.StartsWith("(error", StringComparison.Ordinal))
                {
                    error = trimmed;
                    return SmtStatus.Error;
                }
            }

            error = $"Unexpected Z3 output: {output.Trim()}";
            return SmtStatus.Error;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch
            {
                // Ignore
            }
            _process.Dispose();
        }
    }
}
