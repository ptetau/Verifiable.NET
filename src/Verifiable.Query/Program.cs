using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Verifiable.Contracts;

namespace Verifiable.Query;

/// <summary>
/// Pure .NET Core CLI entrypoint for <c>verifiable-query</c>.
/// </summary>
public static class Program
{
    private const string HelpText = """
        Verifiable.Query — Ask formal yes/no questions and render Elm-style C# / Z3 diagnostics

        Usage:
          verifiable-query ask --project <path> --target <Type.Method|Type> --assert <expr> [options]
          verifiable-query check-invariant --project <path> --type <TypeName> --expr <lambda> [options]
          verifiable-query check --project <path> | --file <path> | --stdin | --sarif <build.sarif> [options]
          verifiable-query build <project-or-sln> [options] [-- <extra dotnet build args>]
          verifiable-query list --project <path> [options]

        Commands:
          ask               Prove or refute a boolean C# claim on a method or all methods in a type/project.
          check-invariant   Verify an invariant across every [Verifiable] method producing a given domain type.
          check             Compile & run all Verifiable analyzers + Z3 (or present a SARIF 2.1 log) with full Elm formatting.
          build             Run `dotnet build` with SARIF capture and present CS*/VER* diagnostics in Elm format.
          list              List queryable [Verifiable] methods, [Invariant] members, and [TypeAlias<T>] types.

        Options:
          --project <path>  Path to a .csproj file or directory containing C# sources.
          --file <path>     Path to a standalone .cs file (may be repeated).
          --stdin           Read C# source code directly from standard input.
          --sarif <path>    Path to a SARIF 2.1 log from `dotnet build -p:ErrorLog=build.sarif,version=2.1` (`check`).
          --target <name>   Target method (`LedgerEngine.PostTransfer`) or type (`LedgerEngine`).
          --all             Query every [Verifiable] method in scope (`ask` only).
          --type <name>     Domain type name for `check-invariant` (e.g. `LedgerState`, `TodoBoard`).
          --assert <expr>   Boolean C# expression or lambda (e.g. `result => result.State.TotalDebits == result.State.TotalCredits`).
          --expr <expr>     Alias for `--assert` (typically `s => s.TotalDebits == s.TotalCredits`).
          --assume <expr>   Extra assumption C# expression (e.g. `result.IsSuccess`).
          --because <text>  Human-readable note explaining why you are asking this question.
          --format <mode>   Output format: `elm` (default) or `json`.
          --color <mode>    ANSI terminal colors: `auto` (default), `always`, or `never` (honors `NO_COLOR`).
          --no-warnings     Hide warnings in `check` / `build` output.
          --timeout <ms>    Per-path Z3 timeout in milliseconds (default: 5000).
          --z3 <path>       Explicit path to z3 / z3.exe executable.
          --verbose         Include internal SSA trace steps in proof core output.
          -h, --help        Show this help message.

        Environment Variables:
          VERIFIABLE_AGENT=1  Append machine-readable `<verifiable-*-result>{...}</verifiable-*-result>` JSON (disables ANSI colors).
          VERIFIABLE_Z3_PATH  Override Z3 executable discovery.
          NO_COLOR            Disable ANSI colors when `--color auto` is active.

        Exit Codes:
          0  Claim holds / check passed cleanly.
          1  Claim refuted / diagnostics reported.
          2  Solver returned unknown/inconclusive.
          3  Usage, source compilation, or query expression error.
          4  Z3 executable could not be found.
        """;

    private static readonly string[] KnownCommands = ["ask", "check-invariant", "check", "build", "list"];

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
        TextWriter stdout,
        TextWriter stderr)
    {
        var agentMode = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT") == "1";
        var isRedirected = !ReferenceEquals(stdout, Console.Out) || Console.IsOutputRedirected;

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
        if (!KnownCommands.Contains(command))
        {
            var rankedCmd = Verifiable.Analyzers.ElmSuggest.Rank(command, KnownCommands, maxResults: 2);
            var didYouMean = Verifiable.Analyzers.ElmSuggest.FormatDidYouMean(rankedCmd);
            var cmdFix = didYouMean is not null
                ? $"{didYouMean} Supported subcommands are `ask`, `check-invariant`, `check`, `build`, and `list`."
                : "Use `ask`, `check-invariant`, `check`, `build`, or `list` (run `verifiable-query --help`).";
            var errReport = new QueryReport(
                command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                Array.Empty<MethodQueryResult>(), "UNKNOWN COMMAND",
                $"Unknown subcommand `{args[0]}`.", Location.None,
                cmdFix);
            stderr.WriteLine(QueryFormatter.FormatReport(errReport, "elm", agentMode, false));
            return ResolveExitCode(QueryOutcomeStatus.Error);
        }

        string? project = null;
        var files = new List<string>();
        var useStdin = false;
        string? sarifPath = null;
        string? target = null;
        var all = false;
        string? typeName = null;
        string? assertExpr = null;
        string? assumeExpr = null;
        string? because = null;
        var format = "elm";
        var colorMode = ElmColorMode.Auto;
        var includeWarnings = true;
        var timeoutMs = NormalizeTimeoutMs(5000);
        string? z3Path = null;
        var verbose = false;
        var extraBuildArgs = new List<string>();
        var positionalBuildTarget = (string?)null;

        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--" && command == "build")
            {
                for (var j = i + 1; j < args.Length; j++) extraBuildArgs.Add(args[j]);
                break;
            }

            string? NextVal() => i + 1 < args.Length ? args[++i] : null;

            switch (flag)
            {
                case "--project":
                case "-p":
                    project = NextVal();
                    break;
                case "--file":
                case "-f":
                    var f = NextVal();
                    if (f is not null) files.Add(f);
                    break;
                case "--stdin":
                    useStdin = true;
                    break;
                case "--sarif":
                    sarifPath = NextVal();
                    break;
                case "--target":
                case "-t":
                    target = NextVal();
                    break;
                case "--all":
                    all = true;
                    break;
                case "--type":
                    typeName = NextVal();
                    break;
                case "--assert":
                case "--expr":
                    assertExpr = NextVal();
                    break;
                case "--assume":
                    assumeExpr = NextVal();
                    break;
                case "--because":
                    because = NextVal();
                    break;
                case "--format":
                    format = NextVal() ?? "elm";
                    break;
                case "--color":
                    colorMode = ElmAnsiRenderer.ParseColorMode(NextVal());
                    break;
                case "--no-warnings":
                    includeWarnings = false;
                    break;
                case "--timeout":
                    if (int.TryParse(NextVal(), out var parsedTimeout))
                    {
                        timeoutMs = NormalizeTimeoutMs(parsedTimeout);
                    }
                    break;
                case "--z3":
                    z3Path = NextVal();
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                default:
                    if (command == "build" && !flag.StartsWith("-", StringComparison.Ordinal) && positionalBuildTarget is null)
                    {
                        positionalBuildTarget = flag;
                        break;
                    }
                    var uErr = new QueryReport(
                        command, assertExpr ?? string.Empty, assumeExpr, because, typeName ?? target,
                        QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                        "UNKNOWN OPTION", $"Unrecognized option `{flag}`.", Location.None,
                        "Run `verifiable-query --help` to see supported options.");
                    WriteOutput(command, uErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                    return ResolveExitCode(QueryOutcomeStatus.Error);
            }
        }

        var useColor = ElmAnsiRenderer.ShouldUseColor(colorMode, format, agentMode, isRedirected);

        if (command == "build")
        {
            var buildTarget = project ?? positionalBuildTarget;
            if (string.IsNullOrWhiteSpace(buildTarget))
            {
                var bErr = new QueryReport(
                    command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "MISSING BUILD TARGET",
                    "`build` requires a `.csproj` or `.sln` path.", Location.None,
                    "Example: verifiable-query build MyProject.csproj");
                WriteOutput(command, bErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            var (buildExit, buildReport, buildError) = ElmCheckEngine.RunDotnetBuild(buildTarget!, extraBuildArgs, includeWarnings);
            if (buildError is not null && buildReport.Diagnostics.Count == 0)
            {
                var bErr = new QueryReport(
                    command, string.Empty, null, null, buildTarget, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "BUILD FAILED",
                    buildError, Location.None,
                    "Review the build output above and fix the project configuration or compilation error.");
                WriteOutput(command, bErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return buildExit;
            }

            var formattedBuild = ElmCheckEngine.FormatCheckReport(buildReport, format, agentMode);
            if (useColor) formattedBuild = ElmAnsiRenderer.Colorize(formattedBuild);
            (buildExit == 0 || format.Equals("json", StringComparison.OrdinalIgnoreCase) ? stdout : stderr).WriteLine(formattedBuild);
            return buildExit;
        }

        if (command == "check" && !string.IsNullOrWhiteSpace(sarifPath))
        {
            if (!File.Exists(sarifPath))
            {
                var sErr = new QueryReport(
                    command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "SARIF FILE NOT FOUND",
                    $"SARIF file not found: `{sarifPath}`.", Location.None,
                    "Generate one with `dotnet build MyProject.csproj -p:ErrorLog=build.sarif,version=2.1`.");
                WriteOutput(command, sErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            Microsoft.CodeAnalysis.CSharp.CSharpCompilation? optionalComp = null;
            if (!string.IsNullOrWhiteSpace(project) || files.Count > 0 || useStdin)
            {
                var stdinOpt = useStdin ? (readStdin?.Invoke() ?? Console.In.ReadToEnd()) : null;
                optionalComp = WorkspaceLoader.Load(project, files, stdinOpt).Compilation;
            }

            var sarifReport = ElmSarifPresenter.PresentSarif(File.ReadAllText(sarifPath!), optionalComp, includeWarnings);
            var formattedSarif = ElmCheckEngine.FormatCheckReport(sarifReport, format, agentMode);
            if (useColor) formattedSarif = ElmAnsiRenderer.Colorize(formattedSarif);
            var sarifExit = sarifReport.ErrorCount > 0 ? 1 : 0;
            (sarifExit == 0 || format.Equals("json", StringComparison.OrdinalIgnoreCase) ? stdout : stderr).WriteLine(formattedSarif);
            return sarifExit;
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
            WriteOutput(command, lErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
            return ResolveExitCode(QueryOutcomeStatus.Error);
        }

        if (command == "check")
        {
            var checkReport = ElmCheckEngine.CheckCompilation(loadResult.Compilation, loadResult.CompilationErrors, z3Path, includeWarnings);
            var formattedCheck = ElmCheckEngine.FormatCheckReport(checkReport, format, agentMode);
            if (useColor) formattedCheck = ElmAnsiRenderer.Colorize(formattedCheck);
            var checkExit = checkReport.ErrorCount > 0 ? 1 : 0;
            (checkExit == 0 || format.Equals("json", StringComparison.OrdinalIgnoreCase) ? stdout : stderr).WriteLine(formattedCheck);
            return checkExit;
        }

        if (loadResult.CompilationErrors.Count > 0)
        {
            var rewritten = ElmCSharpDiagnosticRewriter.Rewrite(loadResult.Compilation, loadResult.CompilationErrors);
            var cErr = new QueryReport(
                command, assertExpr ?? string.Empty, assumeExpr, because, typeName ?? target,
                QueryOutcomeStatus.Error, Array.Empty<MethodQueryResult>(),
                rewritten.Title, rewritten.Message, rewritten.Location, rewritten.FixHint);
            WriteOutput(command, cErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
            return ResolveExitCode(QueryOutcomeStatus.Error);
        }

        var compilation = loadResult.Compilation;

        if (command == "list")
        {
            var inv = QueryEngine.ListSymbols(compilation);
            var invOut = QueryFormatter.FormatInventory(inv, format, agentMode);
            if (useColor) invOut = ElmAnsiRenderer.Colorize(invOut);
            stdout.WriteLine(invOut);
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
                WriteOutput(command, mErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            var report = QueryEngine.CheckInvariant(compilation, typeName!, assertExpr!, assumeExpr, because, z3Path, timeoutMs);
            WriteOutput(command, report, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
            return ResolveExitCode(report.Status);
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
            WriteOutput(command, aErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
            return ResolveExitCode(QueryOutcomeStatus.Error);
        }

        var askReport = QueryEngine.Ask(compilation, target, all, assertExpr!, assumeExpr, because, z3Path, timeoutMs);
        WriteOutput(command, askReport, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
        return ResolveExitCode(askReport.Status);
    }

    [Verifiable]
    internal static QueryTimeoutMs NormalizeTimeoutMs(int requestedMs)
    {
        if (requestedMs < 1) return QueryTimeoutMs.From(5000);
        if (requestedMs > 600_000) return QueryTimeoutMs.From(600_000);
        return QueryTimeoutMs.From(requestedMs);
    }

    [Verifiable]
    internal static int ResolveExitCode(QueryOutcomeStatus status)
    {
        Spec.Ensures<int>(
            result => result >= 0 && result <= 4,
            "verifiable-query exit codes must be in 0..4.");

        if (status == QueryOutcomeStatus.Proved) return 0;
        if (status == QueryOutcomeStatus.Refuted) return 1;
        if (status == QueryOutcomeStatus.Inconclusive) return 2;
        if (status == QueryOutcomeStatus.Error) return 3;
        if (status == QueryOutcomeStatus.Z3NotFound) return 4;
        return 3;
    }

    private static void WriteOutput(
        string command,
        QueryReport report,
        string format,
        ElmColorMode colorMode,
        bool agentMode,
        bool verbose,
        bool isRedirected,
        TextWriter stdout,
        TextWriter stderr)
    {
        var formatted = QueryFormatter.FormatReport(report, format, agentMode, verbose);
        if (ElmAnsiRenderer.ShouldUseColor(colorMode, format, agentMode, isRedirected))
        {
            formatted = ElmAnsiRenderer.Colorize(formatted);
        }

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
