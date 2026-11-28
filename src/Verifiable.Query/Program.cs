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
          verifiable-query clauses --project <path> [--clauses <spec.clauses.json>] [options]
          verifiable-query holes --project <path> [--clauses <spec.clauses.json>] [options]
          verifiable-query pack --project <path> (--target <symbols> | --clause <ids> | --holes) [--max-tokens <n>]
          verifiable-query one-answer --project <path> [--target <symbols>] [--lock <file>]
          verifiable-query fix --project <path> [--rule <id>] [--target <symbols>] [--apply]
          verifiable-query explain [<rule id>...]
          verifiable-query describe
          verifiable-query impact --old <export.json> (--new <export.json> | --project <path>)
          verifiable-query export --project <path> [--out <file.json>]
          verifiable-query lock --project <path> [--target <symbols>] --out <name.contracts.lock.json>
          verifiable-query tools status [z3] | tools install [z3] [--to <dir>] [--force]

        Commands:
          ask               Prove or refute a boolean C# claim on a method or all methods in a type/project.
          check-invariant   Verify an invariant across every [Verifiable] method producing a given domain type.
          check             Compile & run all Verifiable analyzers + Z3 (or present a SARIF 2.1 log) with full Elm formatting.
          build             Run `dotnet build` with SARIF capture and present CS*/VER* diagnostics in Elm format.
          list              List queryable [Verifiable] methods, [Invariant] members, and [TypeAlias<T>] types.
          clauses           Map spec clause IDs to the contracts and rules that carry them, and report gaps.
          holes             List unwritten bodies (Spec.Hole) with signature, contracts and clause text.
          pack              Everything an agent needs to fill one method: contracts, clause text, types, helpers, callers.
          one-answer        Report whether each method's contract fixes one answer: fixed, open (with an example), or inconclusive.
          fix               Run the code fixes from the command line and return JSON text edits (--apply writes them).
          explain           Explain a rule: why it exists, how to fix it, and an example.
          describe          List every command, its options and its output schema.
          impact            Compare two contract exports: widened / narrowed / changed, and the proofs each change breaks.
          export            Write every contract, type rule and invariant as JSON expression trees (schema export@1).
          lock              Write the contract lock for generated methods, so changing a contract fails the build (VER109).
          tools             Show or install the pinned native tools (Z3) with checked SHA-256 checksums.

        Options:
          --project <path>  Path to a .csproj file or directory containing C# sources.
          --file <path>     Path to a standalone .cs file (may be repeated).
          --stdin           Read C# source code directly from standard input.
          --sarif <path>    Path to a SARIF 2.1 log from `dotnet build -p:ErrorLog=build.sarif,version=2.1` (`check`).
          --target <name>   Target method (`LedgerEngine.PostTransfer`) or type (`LedgerEngine`).
                            With `check`, proves only these (comma-separated) symbols; alias `--symbol`.
          --all             Query every [Verifiable] method in scope (`ask` only).
          --type <name>     Domain type name for `check-invariant` (e.g. `LedgerState`, `TodoBoard`).
          --assert <expr>   Boolean C# expression or lambda (e.g. `result => result.State.TotalDebits == result.State.TotalCredits`).
          --expr <expr>     Alias for `--assert` (typically `s => s.TotalDebits == s.TotalCredits`).
          --assume <expr>   Extra assumption C# expression (e.g. `result.IsSuccess`).
          --because <text>  Human-readable note explaining why you are asking this question.
          --format <mode>   Output format: `elm` (default) or `json`.
          --color <mode>    ANSI terminal colors: `auto` (default), `always`, or `never` (honors `NO_COLOR`).
          --no-warnings     Hide warnings in `check` / `build` output.
          --timeout <ms>    Per-path Z3 timeout in milliseconds (default: 5000; `check` default: 2500).
          --clauses <file>  Clause manifest from the spec; `check` then fails on uncovered (VER401) or unknown (VER403) clauses.
          --lock <file>     With `check`, verify a contract lock (*.contracts.lock.json) in-process.
          --holes           With `pack`, one pack per hole.
          --old <file>      With `impact`, the export from before the change.
          --new <file>      With `impact`, the export from after (or pass the new sources).
          --rule <id>       With `fix`, only fixes for this rule.
          --apply           With `fix`, write the fixes to disk.
          --one-answer      With `check`, report each unmarked method whose contract allows two answers as VER111.
          --max-tokens <n>  With `pack`, drop the least relevant parts until each pack fits; dropped parts are listed.
          --out <file>      With `lock` or `export`, where to write the file (`export` writes every entry, unpaged).
          --clause <ids>    With `check`, prove only the symbols that carry these clauses (comma-separated).
          --stats           With `check`, add timing and proof-cache counts (not byte-stable).
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

    private static readonly string[] KnownCommands = ["ask", "check-invariant", "check", "build", "list", "tools", "clauses", "holes", "lock", "pack", "export", "one-answer", "fix", "explain", "describe", "impact"];

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

        if (command is "explain" or "describe")
        {
            var fmtAt = Array.LastIndexOf(args, "--format");
            var fmt = fmtAt >= 0 && fmtAt + 1 < args.Length ? args[fmtAt + 1] : "elm";
            var wantJson = fmt.Equals("json", StringComparison.OrdinalIgnoreCase);
            if (command == "describe")
            {
                stdout.WriteLine(wantJson ? CommandCatalog.DescribeJson(true)
                    : string.Join("\n", CommandCatalog.Commands.Select(c => $"{c.Name,-16} {c.Summary}  (schema {AgentOutput.SchemaId(c.Schema)})")));
                return 0;
            }

            var ids = args.Skip(1).Where(a => !a.StartsWith("-", StringComparison.Ordinal) && !a.Equals(fmt, StringComparison.Ordinal)).ToList();
            var rules = RuleCatalog.All();
            var picked = ids.Count == 0 ? rules.ToList() : rules.Where(r => ids.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var missing = ids.Where(id => !rules.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
            {
                var ranked = Verifiable.Analyzers.ElmSuggest.Rank(missing[0].ToUpperInvariant(), rules.Select(r => r.Id).ToArray(), maxResults: 3);
                var eErr = new QueryReport(command, string.Empty, null, null, missing[0], QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "UNKNOWN RULE", $"There is no rule `{missing[0]}`.", Location.None,
                    Verifiable.Analyzers.ElmSuggest.FormatDidYouMean(ranked) ?? "Run `verifiable-query explain` to list every rule.");
                WriteOutput(command, eErr, fmt, ElmColorMode.Never, agentMode, false, true, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }
            stdout.WriteLine(wantJson ? CommandCatalog.ExplainJson(picked, true)
                : string.Join("\n\n", picked.Select(r => $"{r.Id}  {r.Title}\n\nWhy: {r.Why}\n\nFix: {r.Fix}{(r.Bad is null ? string.Empty : $"\n\nBefore:\n{r.Bad}\n\nAfter:\n{r.Good}")}")));
            return 0;
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
        // Read --format first, so an error in an earlier option is still reported in the format asked for.
        var formatAt = Array.LastIndexOf(args, "--format");
        var format = formatAt >= 0 && formatAt + 1 < args.Length ? args[formatAt + 1] : "elm";
        var colorMode = ElmColorMode.Auto;
        var includeWarnings = true;
        var timeoutMs = NormalizeTimeoutMs(5000);
        string? z3Path = null;
        var verbose = false;
        var extraBuildArgs = new List<string>();
        var positionalBuildTarget = (string?)null;
        string? clauseManifest = null;
        string? clauseFilter = null;
        string? lockPath = null;
        int? maxTokens = null;
        var allHoles = false;
        var oneAnswerCheck = false;
        string? ruleFilter = null;
        string? oldExport = null;
        string? newExport = null;
        var applyFixes = false;
        string? outPath = null;
        var timeoutGiven = false;
        var stats = false;
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
                case "--symbol":
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
                        timeoutGiven = true;
                    }
                    break;
                case "--z3":
                    z3Path = NextVal();
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "--clauses":
                    clauseManifest = NextVal();
                    break;
                case "--clause":
                    clauseFilter = NextVal();
                    break;
                case "--lock":
                    lockPath = NextVal();
                    break;
                case "--holes":
                    allHoles = true;
                    break;
                case "--one-answer":
                    oneAnswerCheck = true;
                    break;
                case "--rule":
                    ruleFilter = NextVal();
                    break;
                case "--old":
                    oldExport = NextVal();
                    break;
                case "--new":
                    newExport = NextVal();
                    break;
                case "--apply":
                    applyFixes = true;
                    break;
                case "--max-tokens":
                    var mtText = NextVal();
                    if (!int.TryParse(mtText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mt) || mt < 1)
                    {
                        var mtErr = new QueryReport(command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                            Array.Empty<MethodQueryResult>(), "INVALID OPTION VALUE",
                            $"`--max-tokens` must be a positive whole number, not `{mtText}`.", Location.None, "Example: --max-tokens 4000");
                        WriteOutput(command, mtErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                        return ResolveExitCode(QueryOutcomeStatus.Error);
                    }
                    maxTokens = mt;
                    break;
                case "--out":
                    outPath = NextVal();
                    break;
                case "--stats":
                    stats = true;
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

        if (command == "impact")
        {
            string? ReadExport(string? path, out string? err)
            {
                err = null;
                if (path is null) return null;
                if (!File.Exists(path)) { err = $"Export file not found: `{path}`."; return null; }
                return File.ReadAllText(path);
            }

            var oldText = ReadExport(oldExport, out var oldErr);
            var newText = ReadExport(newExport, out var newErr);
            var usage = oldExport is null ? "`impact` needs `--old <export.json>`." :
                        oldErr ?? newErr ??
                        (newExport is null && project is null && files.Count == 0 && !useStdin ? "`impact` needs `--new <export.json>` or the new sources (`--project`)." : null);
            List<ExportEntry>? before = null, after = null;
            if (usage is null)
            {
                try
                {
                    before = ChangeImpact.Read(oldText!);
                    if (newText is not null) after = ChangeImpact.Read(newText);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    usage = $"Not a `verifiable-query export` document: {ex.Message}";
                }
            }

            if (usage is not null)
            {
                var iErr = new QueryReport(command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "BAD IMPACT INPUT", usage, Location.None,
                    "Example: verifiable-query impact --old before.export.json --project App.csproj --format json");
                WriteOutput(command, iErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            List<BrokenProof>? brokenProofs = null;
            if (after is null)
            {
                var newLoad = WorkspaceLoader.Load(project, files, useStdin ? (readStdin?.Invoke() ?? Console.In.ReadToEnd()) : null);
                if (newLoad.Compilation is null || newLoad.ErrorMessage is not null || newLoad.CompilationErrors.Count > 0)
                {
                    var nErr = new QueryReport(command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                        Array.Empty<MethodQueryResult>(), "FAILED TO LOAD SOURCES", newLoad.ErrorMessage ?? "The new sources do not compile.", Location.None,
                        "Run `verifiable-query check` on the new sources first.");
                    WriteOutput(command, nErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                    return ResolveExitCode(QueryOutcomeStatus.Error);
                }
                after = ChangeImpact.Read(ContractExport.Document(ContractExport.Collect(newLoad.Compilation), false, int.MaxValue, 0, null));
                var changesNow = ChangeImpact.Compare(before!, after, z3Path);
                brokenProofs = ChangeImpact.Broken(newLoad.Compilation, changesNow, z3Path);
                var json1 = ChangeImpact.ToJson(changesNow, brokenProofs, indented: true);
                stdout.WriteLine(json1);
                return brokenProofs.Count > 0 ? 1 : 0;
            }

            var changes = ChangeImpact.Compare(before!, after, z3Path);
            stdout.WriteLine(ChangeImpact.ToJson(changes, null, indented: true));
            return 0;
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

        List<Verifiable.Analyzers.SpecClause>? manifestClauses = null;
        if (clauseManifest is not null)
        {
            var (mc, mErr) = ClauseReport.ReadManifest(clauseManifest);
            if (mErr is not null)
            {
                var cmErr = new QueryReport(
                    command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "BAD CLAUSE MANIFEST", mErr, Location.None,
                    "Pass a file shaped {\"clauses\":[{\"id\":\"C1\",\"text\":\"...\"}]}.");
                WriteOutput(command, cmErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }
            manifestClauses = mc;
        }

        if (command == "fix")
        {
            IReadOnlyList<string>? fixScope = string.IsNullOrWhiteSpace(target)
                ? null
                : target!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            var fixes = applyFixes
                ? FixCommand.Apply(loadResult.Compilation, ruleFilter, fixScope)
                : FixCommand.Propose(loadResult.Compilation, ruleFilter, fixScope);
            stdout.WriteLine(format.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? FixCommand.ToJson(fixes, applyFixes, indented: true, limit, offset)
                : $"{(applyFixes ? "Applied" : "Found")} {fixes.Count} {(fixes.Count == 1 ? "fix" : "fixes")}." +
                  string.Concat(fixes.Select(f => $"\n  {f.Rule} {f.File}:{f.Line}  {f.Title}")));
            return 0;
        }

        if (command == "one-answer")
        {
            var candidates = OneAnswer.Candidates(loadResult.Compilation);
            if (!string.IsNullOrWhiteSpace(target))
            {
                var wanted = target!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
                candidates = candidates.Where(m => ElmCheckEngine.SymbolInScope(ContextPacks.Name(m), wanted)).ToList();
            }
            if (lockPath is not null)
            {
                // Generated code only: the methods a contract lock covers.
                var (lockText, lockOk) = File.Exists(lockPath) ? (File.ReadAllText(lockPath), true) : (string.Empty, false);
                if (!lockOk || !Verifiable.Analyzers.ContractLock.TryParse(lockText, out var lockEntries, out _))
                {
                    var lErr2 = new QueryReport(command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                        Array.Empty<MethodQueryResult>(), "BAD CONTRACT LOCK", $"Could not read the contract lock `{lockPath}`.", Location.None,
                        "Regenerate it with `verifiable-query lock --out <name>.contracts.lock.json`.");
                    WriteOutput(command, lErr2, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                    return ResolveExitCode(QueryOutcomeStatus.Error);
                }
                var locked = new HashSet<string>(lockEntries.Select(e => e.Symbol.Split('(')[0]), StringComparer.Ordinal);
                candidates = candidates.Where(m => locked.Contains(ContextPacks.Name(m))).ToList();
            }

            var answers = OneAnswer.Run(loadResult.Compilation, candidates, z3Path, timeoutGiven ? timeoutMs.Value : null);
            var answersJson = OneAnswer.ToJson(answers, indented: format.Equals("json", StringComparison.OrdinalIgnoreCase), limit, offset);
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase)) stdout.WriteLine(answersJson);
            else
            {
                foreach (var a in answers)
                {
                    stdout.WriteLine($"{OneAnswer.Word(a.Verdict).ToUpperInvariant(),-12} {a.Symbol}{(a.AnyValidResult ? "  [AnyValidResult]" : string.Empty)}{(a.Note is null ? string.Empty : "  -- " + a.Note)}");
                    if (a.CounterexampleJson is not null) stdout.WriteLine($"             {a.CounterexampleJson}");
                }
                if (agentMode) stdout.WriteLine($"<verifiable-one-answer-result>{OneAnswer.ToJson(answers, false, limit, offset)}</verifiable-one-answer-result>");
            }
            return (int)OneAnswer.Outcome(answers);
        }

        if (command == "export")
        {
            var exported = ContractExport.Collect(loadResult.Compilation);
            var isJsonExport = format.Equals("json", StringComparison.OrdinalIgnoreCase);
            if (outPath is not null)
            {
                File.WriteAllText(outPath, ContractExport.Document(exported, indented: true, int.MaxValue, 0, null) + "\n");
            }
            stdout.WriteLine(isJsonExport || outPath is null
                ? ContractExport.Document(exported, indented: true, limit, offset, outPath)
                : $"Exported {exported.Count} contracts to {outPath}.");
            return 0;
        }

        if (command == "pack")
        {
            List<IMethodSymbol> targets;
            if (allHoles) targets = ContextPacks.HoleMethods(loadResult.Compilation);
            else if (!string.IsNullOrWhiteSpace(clauseFilter))
            {
                var wanted = clauseFilter!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToList();
                targets = ContextPacks.ForClauses(loadResult.Compilation, wanted);
            }
            else if (!string.IsNullOrWhiteSpace(target))
            {
                targets = ContextPacks.Resolve(loadResult.Compilation,
                    target!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList());
            }
            else targets = new List<IMethodSymbol>();

            if (targets.Count == 0)
            {
                var pErr = new QueryReport(command, string.Empty, null, null, target ?? clauseFilter, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "NOTHING TO PACK",
                    allHoles ? "There are no holes in these sources." : "`pack` needs `--target <Type.Member>`, `--clause <id>` or `--holes`, naming at least one method.",
                    Location.None, "Run `verifiable-query list` or `verifiable-query holes` to see what exists.");
                WriteOutput(command, pErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            var texts = manifestClauses?.ToDictionary(c => c.Id, c => c.Text, StringComparer.Ordinal) ?? new Dictionary<string, string?>();
            var packs = targets
                .Select(t => ContextPacks.Build(loadResult.Compilation, t, texts))
                .Select(p => maxTokens is int budget ? ContextPacks.Trim(p, budget, ContextPacks.RenderOne) : p)
                .OrderBy(p => p.Symbol, StringComparer.Ordinal).ThenBy(p => p.Line)
                .ToList();
            var packPage = AgentOutput.Slice(packs.Count, offset, limit);
            var packJson = AgentOutput.Write(indented: format.Equals("json", StringComparison.OrdinalIgnoreCase), w =>
            {
                w.WriteStartObject();
                w.WriteString("schema", AgentOutput.SchemaId("pack"));
                w.WriteString("command", "pack");
                w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
                w.WriteNumber("exitCode", 0);
                if (maxTokens is int mtv) w.WriteNumber("maxTokens", mtv); else w.WriteNull("maxTokens");
                w.WriteStartArray("packs");
                for (var i = packPage.Offset; i < packPage.Offset + packPage.Count; i++) ContextPacks.Write(w, packs[i]);
                w.WriteEndArray();
                AgentOutput.WritePage(w, packPage, limit);
                w.WriteEndObject();
            });
            stdout.WriteLine(packJson);
            return 0;
        }

        if (command == "lock")
        {
            IReadOnlyList<string>? lockScope = string.IsNullOrWhiteSpace(target)
                ? null
                : target!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            if (outPath is not null && !outPath.EndsWith(Verifiable.Analyzers.ContractLockAnalyzer.LockSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var oErr = new QueryReport(command, string.Empty, null, null, null, QueryOutcomeStatus.Error,
                    Array.Empty<MethodQueryResult>(), "INVALID OPTION VALUE",
                    $"The lock file name must end in `{Verifiable.Analyzers.ContractLockAnalyzer.LockSuffix}` so builds pick it up.", Location.None,
                    $"Example: --out generated{Verifiable.Analyzers.ContractLockAnalyzer.LockSuffix}");
                WriteOutput(command, oErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                return ResolveExitCode(QueryOutcomeStatus.Error);
            }

            var locks = LockCommand.Select(loadResult.Compilation, lockScope);
            if (outPath is not null) File.WriteAllText(outPath, LockCommand.LockFileText(locks));
            var isJsonLock = format.Equals("json", StringComparison.OrdinalIgnoreCase);
            stdout.WriteLine(isJsonLock
                ? LockCommand.ResultJson(locks, outPath, indented: true)
                : $"Locked {locks.Count} {(locks.Count == 1 ? "method" : "methods")}{(outPath is null ? " (pass --out <file> to write the lock)" : $" in {outPath}")}.");
            return 0;
        }

        if (command == "holes")
        {
            var isJson = format.Equals("json", StringComparison.OrdinalIgnoreCase);
            var (holeOutcome, holeJson) = HoleReport.Build(loadResult.Compilation, manifestClauses, indented: isJson, limit, offset);
            if (isJson) stdout.WriteLine(holeJson);
            else
            {
                var holes = HoleReport.Collect(loadResult.Compilation, manifestClauses);
                stdout.WriteLine(holes.Count == 0 ? "-- NO HOLES --" : $"-- {holes.Count} UNWRITTEN {(holes.Count == 1 ? "BODY" : "BODIES")} --");
                foreach (var h in holes)
                {
                    stdout.WriteLine();
                    stdout.WriteLine($"{h.Symbol}  (clause {h.Clause ?? "none"}){(h.ClauseText is null ? string.Empty : $": {h.ClauseText}")}");
                    stdout.WriteLine($"    {h.Signature}");
                    foreach (var c in h.Contracts) stdout.WriteLine($"    {c}");
                }
                if (agentMode) stdout.WriteLine($"<verifiable-holes-result>{HoleReport.Build(loadResult.Compilation, manifestClauses, false, limit, offset).Json}</verifiable-holes-result>");
            }
            return (int)holeOutcome;
        }

        if (command == "clauses")
        {
            var (clauseOutcome, clauseJson) = ClauseReport.Build(loadResult.Compilation, manifestClauses, indented: format.Equals("json", StringComparison.OrdinalIgnoreCase), limit, offset);
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase)) stdout.WriteLine(clauseJson);
            else
            {
                stdout.WriteLine(clauseOutcome == AgentOutcome.Proved ? "-- CLAUSES COVERED --" : "-- CLAUSES NOT COVERED --");
                stdout.WriteLine();
                stdout.WriteLine("Run with `--format json` for the full map of clauses to symbols and contracts.");
                if (agentMode) stdout.WriteLine($"<verifiable-clauses-result>{ClauseReport.Build(loadResult.Compilation, manifestClauses, false, limit, offset).Json}</verifiable-clauses-result>");
            }
            return (int)clauseOutcome;
        }

        if (command == "check")
        {
            IReadOnlyList<string>? scope = null;
            if (!string.IsNullOrWhiteSpace(clauseFilter))
            {
                var wanted = clauseFilter!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToList();
                var symbols = ClauseReport.SymbolsFor(loadResult.Compilation, wanted);
                if (symbols.Count == 0)
                {
                    var clErr = new QueryReport(
                        command, string.Empty, null, null, clauseFilter, QueryOutcomeStatus.Error,
                        Array.Empty<MethodQueryResult>(), "UNKNOWN CLAUSE",
                        $"No contract, type alias or invariant carries clause `{clauseFilter}`.", Location.None,
                        "Run `verifiable-query clauses --format json` to see which clauses the code carries.");
                    WriteOutput(command, clErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                    return ResolveExitCode(QueryOutcomeStatus.Error);
                }
                target = string.IsNullOrWhiteSpace(target) ? string.Join(",", symbols) : target + "," + string.Join(",", symbols);
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                scope = target!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
                var known = ElmCheckEngine.KnownSymbolNames(loadResult.Compilation);
                var unknown = scope.FirstOrDefault(t => !known.Contains(t));
                if (unknown is not null)
                {
                    var ranked = Verifiable.Analyzers.ElmSuggest.Rank(unknown, known.Where(k => k.Count(c => c == '.') <= 1).OrderBy(k => k, StringComparer.Ordinal).ToArray(), maxResults: 3);
                    var tErr = new QueryReport(
                        command, string.Empty, null, null, unknown, QueryOutcomeStatus.Error,
                        Array.Empty<MethodQueryResult>(), "UNKNOWN SYMBOL",
                        $"`--target {unknown}` names no type or member in these sources.", Location.None,
                        Verifiable.Analyzers.ElmSuggest.FormatDidYouMean(ranked) ?? "Run `verifiable-query list` to see the names you can check.");
                    WriteOutput(command, tErr, format, colorMode, agentMode, verbose, isRedirected, stdout, stderr);
                    return ResolveExitCode(QueryOutcomeStatus.Error);
                }
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var before = Verifiable.Analyzers.Smt.Z3SolverRunner.GetStats();
            var checkReport = ElmCheckEngine.CheckCompilation(
                loadResult.Compilation, loadResult.CompilationErrors, z3Path, includeWarnings,
                scope, timeoutGiven ? timeoutMs.Value : null, clauseManifest, lockPath);
            if (oneAnswerCheck && checkReport.Diagnostics.All(d => !d.RuleId.StartsWith("CS", StringComparison.Ordinal)))
            {
                var oaCandidates = OneAnswer.Candidates(loadResult.Compilation);
                if (scope is not null) oaCandidates = oaCandidates.Where(m => ElmCheckEngine.SymbolInScope(ContextPacks.Name(m), scope)).ToList();
                if (lockPath is not null && File.Exists(lockPath) &&
                    Verifiable.Analyzers.ContractLock.TryParse(File.ReadAllText(lockPath), out var oaLocks, out _))
                {
                    var locked = new HashSet<string>(oaLocks.Select(e => e.Symbol.Split('(')[0]), StringComparer.Ordinal);
                    oaCandidates = oaCandidates.Where(m => locked.Contains(ContextPacks.Name(m))).ToList();
                }

                var open = OneAnswer.Run(loadResult.Compilation, oaCandidates, z3Path, timeoutGiven ? timeoutMs.Value : null)
                    .Where(a => a.Verdict == OneAnswerVerdict.Open && !a.AnyValidResult)
                    .Select(a => new PresentedDiagnostic(
                        "VER111", "error", a.File, a.Line, 1,
                        $"The contract of `{a.Symbol}` does not fix one answer{(a.CounterexampleJson is null ? string.Empty : ": " + a.CounterexampleJson)}{(a.Note is null ? string.Empty : ". " + a.Note)}",
                        $"-- CONTRACT ALLOWS MORE THAN ONE ANSWER --------------------- {a.File}\n\nThe contract of `{a.Symbol}` lets two different results pass for the same input, so a body can pass its proof and still be wrong.\n\nFix: Strengthen the postcondition until it fixes one result, send the gap back to Resolve as a question, or mark the method [AnyValidResult(\"why\")].",
                        new DiagnosticFacts(a.Symbol, a.Clauses.Count == 0 ? null : string.Join(",", a.Clauses), "OneAnswer", null, null, a.CounterexampleJson,
                            "Strengthen the postcondition until it fixes one result, send the gap back to Resolve as a question, or mark the method [AnyValidResult(\"why\")].")))
                    .ToList();
                if (open.Count > 0)
                {
                    checkReport = checkReport with { ErrorCount = checkReport.ErrorCount + open.Count, Diagnostics = checkReport.Diagnostics.Concat(open).ToList() };
                }
            }

            if (stats)
            {
                var after = Verifiable.Analyzers.Smt.Z3SolverRunner.GetStats();
                checkReport = checkReport with
                {
                    Stats = new CheckStats(
                        stopwatch.ElapsedMilliseconds,
                        after.Queries - before.Queries,
                        after.MemoryCacheHits - before.MemoryCacheHits,
                        after.DiskCacheHits - before.DiskCacheHits,
                        after.SolverRuns - before.SolverRuns),
                };
            }
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
