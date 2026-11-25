using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Verifiable.Query;

/// <summary>
/// Pure .NET Core CLI entrypoint for <c>verifiable-query</c>.
/// </summary>
public static class Program
{
    private const string HelpText = """
        Verifiable.Query — Ask formal yes/no questions about your verified C# code

        Usage:
          verifiable-query ask --project <path> --target <Type.Method|Type> --assert <expr> [options]
          verifiable-query check-invariant --project <path> --type <TypeName> --expr <lambda> [options]
          verifiable-query list --project <path> [options]

        Commands:
          ask               Prove or refute a boolean C# claim on a method or all methods in a type/project.
          check-invariant   Verify an invariant across every [Verifiable] method producing a given domain type.
          list              List queryable [Verifiable] methods, [Invariant] members, and [TypeAlias<T>] types.

        Options:
          --project <path>  Path to a .csproj file or directory containing C# sources.
          --file <path>     Path to a standalone .cs file (may be repeated).
          --stdin           Read C# source code directly from standard input.
          --target <name>   Target method (`LedgerEngine.PostTransfer`) or type (`LedgerEngine`).
          --all             Query every [Verifiable] method in scope (`ask` only).
          --type <name>     Domain type name for `check-invariant` (e.g. `LedgerState`, `TodoBoard`).
          --assert <expr>   Boolean C# expression or lambda (e.g. `result => result.State.TotalDebits == result.State.TotalCredits`).
          --expr <expr>     Alias for `--assert` (typically `s => s.TotalDebits == s.TotalCredits`).
          --assume <expr>   Extra assumption C# expression (e.g. `result.IsSuccess`).
          --because <text>  Human-readable note explaining why you are asking this question.
          --format <mode>   Output format: `elm` (default) or `json`.
          --timeout <ms>    Per-path Z3 timeout in milliseconds (default: 5000).
          --z3 <path>       Explicit path to z3 / z3.exe executable.
          --verbose         Include internal SSA trace steps in proof core output.
          -h, --help        Show this help message.

        Environment Variables:
          VERIFIABLE_AGENT=1  Append machine-readable `<verifiable-query-result>{...}</verifiable-query-result>` JSON.
          VERIFIABLE_Z3_PATH  Override Z3 executable discovery.

        Exit Codes:
          0  Claim holds across all checked paths.
          1  Claim refuted (counterexample printed).
          2  Solver returned unknown/inconclusive.
          3  Usage, source compilation, or query expression error.
          4  Z3 executable could not be found.
        """;

    /// <summary>
    /// CLI entry point.
    /// </summary>
    public static int Main(string[] args)
    {
        return Run(args, Console.IsInputRedirected ? Console.In.ReadToEnd : null, Console.Out, Console.Error);
    }

    internal static int Run(
        string[] args,
        Func<string>? readStdin,
        System.IO.TextWriter stdout,
        System.IO.TextWriter stderr)
    {
        var agentMode = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT") == "1";

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            stdout.WriteLine(HelpText);
            return 0;
        }

        if (args.Contains("--version"))
        {
            stdout.WriteLine("0.1.0");
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command is not ("ask" or "check-invariant" or "list"))
        {
            var errReport = new QueryReport(
                command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                Array.Empty<MethodQueryResult>(), "UNKNOWN COMMAND",
                $"Unknown subcommand `{args[0]}`.", Location.None,
                "Use `ask`, `check-invariant`, or `list` (run `verifiable-query --help`).");
            stderr.WriteLine(QueryFormatter.FormatReport(errReport, "elm", agentMode, false));
            return (int)QueryOutcomeStatus.Error;
        }

        string? project = null;
        var files = new List<string>();
        var useStdin = false;
        string? target = null;
        var all = false;
        string? typeName = null;
        string? assertExpr = null;
        string? assumeExpr = null;
        string? because = null;
        var format = "elm";
        var timeoutMs = 5000;
        string? z3Path = null;
        var verbose = false;

        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            string? NextVal() => i + 1 < args.Length ? args[++i] : null;

            switch (flag)
            {
                case "--project": project = NextVal(); break;
                case "--file":
                    var f = NextVal();
                    if (f is not null) files.Add(f);
                    break;
                case "--stdin": useStdin = true; break;
                case "--target": target = NextVal(); break;
                case "--all": all = true; break;
                case "--type": typeName = NextVal(); break;
                case "--assert":
                case "--expr": assertExpr = NextVal(); break;
                case "--assume": assumeExpr = NextVal(); break;
                case "--because": because = NextVal(); break;
                case "--format": format = NextVal() ?? "elm"; break;
                case "--timeout":
                    if (int.TryParse(NextVal(), out var ms) && ms > 0) timeoutMs = ms;
                    break;
                case "--z3": z3Path = NextVal(); break;
                case "--verbose": verbose = true; break;
                default:
                    var uErr = new QueryReport(
                        command, assertExpr ?? string.Empty, assumeExpr, because, typeName ?? target,
                        QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                        "UNKNOWN OPTION", $"Unrecognized option `{flag}`.", Location.None,
                        "Run `verifiable-query --help` to see supported options.");
                    WriteOutput(command, uErr, format, agentMode, verbose, stdout, stderr);
                    return (int)QueryOutcomeStatus.Error;
            }
        }

        string? stdinText = null;
        if (useStdin)
        {
            stdinText = readStdin?.Invoke() ?? Console.In.ReadToEnd();
        }

        var loadResult = WorkspaceLoader.Load(project, files, stdinText);
        if (loadResult.ErrorMessage is not null || loadResult.Compilation is null)
        {
            var lErr = new QueryReport(
                command, assertExpr ?? string.Empty, assumeExpr, because, typeName ?? target,
                QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                "FAILED TO LOAD SOURCES", loadResult.ErrorMessage ?? "Could not load C# sources.",
                Location.None, "Check `--project`, `--file`, or `--stdin` arguments.");
            WriteOutput(command, lErr, format, agentMode, verbose, stdout, stderr);
            return (int)QueryOutcomeStatus.Error;
        }

        if (loadResult.CompilationErrors.Count > 0)
        {
            var first = loadResult.CompilationErrors[0];
            var msg = string.Join(Environment.NewLine, loadResult.CompilationErrors.Take(5).Select(e => e.ToString()));
            var cErr = new QueryReport(
                command, assertExpr ?? string.Empty, assumeExpr, because, typeName ?? target,
                QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                "SOURCE COMPILATION ERROR", msg, first.Location,
                "Fix the C# compiler errors in the target sources before running queries.");
            WriteOutput(command, cErr, format, agentMode, verbose, stdout, stderr);
            return (int)QueryOutcomeStatus.Error;
        }

        var compilation = loadResult.Compilation;

        if (command == "list")
        {
            var inv = QueryEngine.ListSymbols(compilation);
            stdout.WriteLine(QueryFormatter.FormatInventory(inv, format, agentMode));
            return 0;
        }

        if (command == "check-invariant")
        {
            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(assertExpr))
            {
                var mErr = new QueryReport(
                    command, assertExpr ?? string.Empty, assumeExpr, because, typeName,
                    QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                    "MISSING REQUIRED ARGUMENTS",
                    "`check-invariant` requires `--type <TypeName>` and `--expr <lambda>`.",
                    Location.None,
                    "Example: verifiable-query check-invariant --project App.csproj --type LedgerState --expr \"s => s.TotalDebits == s.TotalCredits\"");
                WriteOutput(command, mErr, format, agentMode, verbose, stdout, stderr);
                return (int)QueryOutcomeStatus.Error;
            }

            var report = QueryEngine.CheckInvariant(compilation, typeName!, assertExpr!, assumeExpr, because, z3Path, timeoutMs);
            WriteOutput(command, report, format, agentMode, verbose, stdout, stderr);
            return (int)report.Status;
        }

        // command == "ask"
        if (string.IsNullOrWhiteSpace(assertExpr))
        {
            var aErr = new QueryReport(
                command, string.Empty, assumeExpr, because, target,
                QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                "MISSING QUERY CLAIM",
                "`ask` requires `--assert <expr>` (or `--expr <expr>`).",
                Location.None,
                "Example: verifiable-query ask --project App.csproj --target LedgerEngine.PostTransfer --assert \"result.State.TotalDebits == result.State.TotalCredits\"");
            WriteOutput(command, aErr, format, agentMode, verbose, stdout, stderr);
            return (int)QueryOutcomeStatus.Error;
        }

        var askReport = QueryEngine.Ask(compilation, target, all, assertExpr!, assumeExpr, because, z3Path, timeoutMs);
        WriteOutput(command, askReport, format, agentMode, verbose, stdout, stderr);
        return (int)askReport.Status;
    }

    private static void WriteOutput(
        string command,
        QueryReport report,
        string format,
        bool agentMode,
        bool verbose,
        System.IO.TextWriter stdout,
        System.IO.TextWriter stderr)
    {
        var formatted = QueryFormatter.FormatReport(report, format, agentMode, verbose);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(formatted);
        }
        else if (report.Status is QueryOutcomeStatus.Error or QueryOutcomeStatus.Z3NotFound)
        {
            stderr.WriteLine(formatted);
        }
        else
        {
            stdout.WriteLine(formatted);
        }
    }
}
