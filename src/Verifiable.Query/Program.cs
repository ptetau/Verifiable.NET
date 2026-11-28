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
          verifiable-query tools status [z3] | tools install [z3] [--to <dir>] [--force]

        Commands:
          ask               Prove or refute a boolean C# claim on a method or all methods in a type/project.
          check-invariant   Verify an invariant across every [Verifiable] method producing a given domain type.
          check             Compile & run all Verifiable analyzers + Z3 (or present a SARIF 2.1 log) with full Elm formatting.
          build             Run `dotnet build` with SARIF capture and present CS*/VER* diagnostics in Elm format.
          list              List queryable [Verifiable] methods, [Invariant] members, and [TypeAlias<T>] types.
          tools             Show or install the pinned native tools (Z3) with checked SHA-256 checksums.

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
          --limit <n>       Most items per JSON page (default: 50, max: 1000).
          --cursor <c>      Continue from `page.nextCursor` of the previous JSON result.
          -h, --help        Show this help message.

        Environment Variables:
          VERIFIABLE_AGENT=1  Append machine-readable `<verifiable-*-result>{...}</verifiable-*-result>` JSON (disables ANSI colors).
          VERIFIABLE_Z3_PATH  Override Z3 executable discovery.
          VERIFIABLE_TOOLS_MIRROR  Download base for `tools install` (an https mirror or file:// folder).
          NO_COLOR            Disable ANSI colors when `--color auto` is active.

        Exit Codes (the same on every command; JSON `status` gives the same word):
          0  proved        Claim holds / check passed cleanly.
          1  refuted       Claim refuted / proof or rule errors reported.
          2  undecided     Solver returned unknown, and nothing was refuted.
          3  input_error   Usage, source compilation, or query expression error.
          4  tool_missing  Z3 executable could not be found.
        """;

    private static readonly string[] KnownCommands = ["ask", "check-invariant", "check", "build", "list", "tools"];

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
            var wantsJson = Array.FindIndex(args, a => a == "--format") is var fi && fi >= 0 && fi + 1 < args.Length &&
                            args[fi + 1].Equals("json", StringComparison.OrdinalIgnoreCase);
            if (wantsJson) stdout.WriteLine(QueryFormatter.FormatReport(errReport, "json", agentMode, false));
            else stderr.WriteLine(QueryFormatter.FormatReport(errReport, "elm", agentMode, false));
            return ResolveExitCode(QueryOutcomeStatus.Error);
        }

        if (command == "tools")
        {
            return RunTools(args, agentMode, stdout, stderr);
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
        var limit = AgentOutput.DefaultLimit;
        var offset = 0;

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
                case "--limit":
                    var limitText = NextVal();
                    if (!int.TryParse(limitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit) ||
                        limit < 1 || limit > AgentOutput.MaxLimit)
                    {
                        var limErr = new QueryReport(
                            command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                            Array.Empty<MethodQueryResult>(), "INVALID OPTION VALUE",
                            $"`--limit` must be a whole number from 1 to {AgentOutput.MaxLimit}, not `{limitText}`.", Location.None,
                            $"Example: --limit {AgentOutput.DefaultLimit}");
                        WriteOutput(command, limErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                        return ResolveExitCode(QueryOutcomeStatus.Error);
                    }
                    break;
                case "--cursor":
                    var cursorText = NextVal();
                    if (!AgentOutput.TryDecodeCursor(cursorText, out offset))
                    {
                        var curErr = new QueryReport(
                            command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                            Array.Empty<MethodQueryResult>(), "INVALID OPTION VALUE",
                            $"`--cursor` value `{cursorText}` was not made by this tool.", Location.None,
                            "Pass the `page.nextCursor` value from the previous JSON result, unchanged.");
                        WriteOutput(command, curErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                        return ResolveExitCode(QueryOutcomeStatus.Error);
                    }
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

        if (!format.Equals("json", StringComparison.OrdinalIgnoreCase) && !format.Equals("elm", StringComparison.OrdinalIgnoreCase))
        {
            var fErr = new QueryReport(
                command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                Array.Empty<MethodQueryResult>(), "INVALID OPTION VALUE",
                $"`--format` must be `json` or `elm`, not `{format}`.", Location.None,
                "Agents should pass `--format json`.");
            format = "elm";
            WriteOutput(command, fErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
            return ResolveExitCode(QueryOutcomeStatus.Error);
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
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            var formattedBuild = ElmCheckEngine.FormatCheckReport(buildReport, format, agentMode, limit, offset);
            if (useColor) formattedBuild = ElmAnsiRenderer.Colorize(formattedBuild);
            var buildOutcome = (int)ElmCheckEngine.Classify(buildReport);
            if (buildExit != 0 && buildOutcome == 0) buildOutcome = (int)AgentOutcome.InputError;
            (buildOutcome == 0 || format.Equals("json", StringComparison.OrdinalIgnoreCase) ? stdout : stderr).WriteLine(formattedBuild);
            return buildOutcome;
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
            var formattedSarif = ElmCheckEngine.FormatCheckReport(sarifReport, format, agentMode, limit, offset);
            if (useColor) formattedSarif = ElmAnsiRenderer.Colorize(formattedSarif);
            var sarifExit = (int)ElmCheckEngine.Classify(sarifReport);
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
            var formattedCheck = ElmCheckEngine.FormatCheckReport(checkReport, format, agentMode, limit, offset);
            if (useColor) formattedCheck = ElmAnsiRenderer.Colorize(formattedCheck);
            var checkExit = (int)ElmCheckEngine.Classify(checkReport);
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
            var invOut = QueryFormatter.FormatInventory(inv, format, agentMode, limit, offset);
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

    /// <summary>
    /// <c>tools status|install [name]</c>: the one front door to native tools (requirement R16).
    /// Exit codes follow the shared rules: 0 when every tool asked about is installed at its pinned
    /// version, 4 when one is missing, 3 for bad arguments or a failed install.
    /// </summary>
    private static int RunTools(string[] args, bool agentMode, TextWriter stdout, TextWriter stderr)
    {
        var format = "elm";
        string? sub = null, name = null, to = null, z3 = null;
        var force = false;
        string? usageError = null;

        for (var i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--format": format = Next() ?? "elm"; break;
                case "--to": to = Next(); break;
                case "--z3": z3 = Next(); break;
                case "--force": force = true; break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal)) usageError ??= $"Unrecognized option `{args[i]}`.";
                    else if (sub is null) sub = args[i];
                    else if (name is null) name = args[i];
                    else usageError ??= $"Unexpected argument `{args[i]}`.";
                    break;
            }
        }

        var json = format.Equals("json", StringComparison.OrdinalIgnoreCase);
        if (!json && !format.Equals("elm", StringComparison.OrdinalIgnoreCase)) usageError ??= $"`--format` must be `json` or `elm`, not `{format}`.";
        if (sub is not ("status" or "install")) usageError ??= sub is null ? "`tools` needs `status` or `install`." : $"Unknown `tools` subcommand `{sub}`.";

        var selected = new List<NativeTool>();
        if (name is null) selected.AddRange(NativeTools.All);
        else if (NativeTools.Find(name) is { } t) selected.Add(t);
        else usageError ??= $"Unknown tool `{name}`. Known tools: {string.Join(", ", NativeTools.All.Select(x => x.Name))}.";

        string Render(string cmd, AgentOutcome outcome, IReadOnlyList<ToolStatus> statuses, string? error, string? fix, bool? already)
        {
            var payload = NativeTools.ToJson(cmd, outcome, statuses, error, fix, already, indented: json);
            if (json) return payload;
            var sb = new System.Text.StringBuilder();
            if (error is not null) sb.AppendLine($"-- TOOLS {AgentOutput.StatusWord(outcome).ToUpperInvariant()} --").AppendLine().AppendLine(error);
            foreach (var st in statuses)
            {
                sb.AppendLine(st.Installed
                    ? $"{st.Tool.Name} {st.InstalledVersion ?? "(version unknown)"} ({NativeTools.LocationWord(st.Location)}){(st.VersionMatches ? string.Empty : $" — pinned version is {st.Tool.Version}")}"
                    : $"{st.Tool.Name} not installed — run `{NativeTools.InstallCommand(st.Tool)}`");
            }
            if (fix is not null) sb.AppendLine().Append("Fix: ").AppendLine(fix);
            if (agentMode) sb.Append("<verifiable-tools-result>").Append(NativeTools.ToJson(cmd, outcome, statuses, error, fix, already, indented: false)).Append("</verifiable-tools-result>");
            return sb.ToString().TrimEnd();
        }

        if (usageError is not null)
        {
            (json ? stdout : stderr).WriteLine(Render("tools", AgentOutcome.InputError, Array.Empty<ToolStatus>(), usageError,
                "Usage: verifiable-query tools status [name] | tools install [name] [--to <dir>] [--force]", null));
            return (int)AgentOutcome.InputError;
        }

        if (sub == "status")
        {
            var statuses = selected.Select(t => NativeTools.Locate(t, z3)).ToList();
            var missing = statuses.Where(st => !st.Installed).ToList();
            var outcome = missing.Count > 0 ? AgentOutcome.ToolMissing : AgentOutcome.Proved;
            var fix = missing.Count > 0 ? $"Run `{string.Join("` and `", missing.Select(m => NativeTools.InstallCommand(m.Tool)))}`." : null;
            var err = missing.Count > 0 ? $"Not installed: {string.Join(", ", missing.Select(m => m.Tool.Name))}." : null;
            (outcome == AgentOutcome.Proved || json ? stdout : stderr).WriteLine(Render("tools status", outcome, statuses, err, fix, null));
            return (int)outcome;
        }

        var results = new List<ToolStatus>();
        var allAlready = true;
        foreach (var tool in selected)
        {
            var r = NativeTools.Install(tool, to is null ? null : Path.Combine(to, selected.Count > 1 ? tool.Name : string.Empty), force: force);
            if (!r.Ok)
            {
                var failed = results.Append(r.Status ?? new ToolStatus(tool, ToolLocation.NotFound, null, null)).ToList();
                (json ? stdout : stderr).WriteLine(Render("tools install", AgentOutcome.InputError, failed, r.Error, r.Fix, false));
                return (int)AgentOutcome.InputError;
            }
            allAlready &= r.AlreadyInstalled;
            results.Add(r.Status!);
        }

        stdout.WriteLine(Render("tools install", AgentOutcome.Proved, results, null, null, allAlready));
        return (int)AgentOutcome.Proved;
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
