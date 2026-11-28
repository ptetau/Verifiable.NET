using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>What a corpus program says the analyzer should do with it.</summary>
public enum Expectation
{
    /// <summary>The program is wrong, so the analyzer must report at least one error.</summary>
    Rejected,

    /// <summary>The program is right, so the analyzer must accept it, and running it must not break any contract.</summary>
    Proved,
}

/// <summary>
/// One program in the soundness corpus, with the directives from its header comment.
/// </summary>
/// <remarks>
/// Directives are comment lines of the form <c>// @name: value</c> anywhere in the file:
/// <list type="bullet">
/// <item><c>@expect: rejected</c> or <c>@expect: proved</c> (required).</item>
/// <item><c>@witness: Ns.Type.Method(args)</c>: a call that shows the program really is wrong. Running it with its
/// preconditions met must break a postcondition, a callee's precondition, a loop contract, or throw. Required for
/// rejected programs unless <c>@no-witness</c> says why there is nothing to run.</item>
/// <item><c>@no-witness: reason</c>: for bugs a run cannot show, such as a leaked <c>[MustConsume]</c> token.</item>
/// <item><c>@known-bug: description</c>: the analyzer gets this program wrong today. The test passes while that is
/// still true and fails once it is fixed, so the marker is removed with the fix.</item>
/// <item><c>@runtime: skip reason</c>: do not run the random cross-check, for example because a loop is too slow.</item>
/// </list>
/// </remarks>
public sealed record CorpusProgram(
    string RelativePath,
    string Source,
    Expectation Expect,
    IReadOnlyList<string> Witnesses,
    string? NoWitnessReason,
    string? KnownBug,
    string? RuntimeSkipReason)
{
    public static string CorpusRoot => Path.Combine(AppContext.BaseDirectory, "Soundness", "Corpus");

    public static IEnumerable<string> AllRelativePaths() =>
        Directory.EnumerateFiles(CorpusRoot, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(CorpusRoot, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal);

    public static CorpusProgram Load(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var directives = new List<(string Name, string Value)>();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("// @", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) throw new FormatException($"{relativePath}: directive without a colon: {line}");
            directives.Add((line.Substring(4, colon - 4).Trim(), line.Substring(colon + 1).Trim()));
        }

        string? Single(string name)
        {
            var found = directives.Where(d => d.Name == name).Select(d => d.Value).ToList();
            if (found.Count > 1) throw new FormatException($"{relativePath}: @{name} appears more than once.");
            return found.Count == 0 ? null : found[0];
        }

        foreach (var (name, _) in directives)
        {
            if (name is not ("expect" or "witness" or "no-witness" or "known-bug" or "runtime"))
            {
                throw new FormatException($"{relativePath}: unknown directive @{name}.");
            }
        }

        var expect = Single("expect") switch
        {
            "rejected" => Expectation.Rejected,
            "proved" => Expectation.Proved,
            var other => throw new FormatException($"{relativePath}: @expect must be 'rejected' or 'proved', not '{other ?? "(missing)"}'."),
        };

        var witnesses = directives.Where(d => d.Name == "witness").Select(d => d.Value).ToList();
        var noWitness = Single("no-witness");
        var runtime = Single("runtime");
        string? runtimeSkip = null;
        if (runtime is not null)
        {
            if (!runtime.StartsWith("skip ", StringComparison.Ordinal))
            {
                throw new FormatException($"{relativePath}: @runtime must be 'skip <reason>'.");
            }

            runtimeSkip = runtime.Substring(5).Trim();
        }

        if (expect == Expectation.Rejected && witnesses.Count == 0 && noWitness is null)
        {
            throw new FormatException($"{relativePath}: a rejected program needs an @witness call that shows it is wrong, or @no-witness with the reason.");
        }

        if (expect == Expectation.Proved && (witnesses.Count > 0 || noWitness is not null))
        {
            throw new FormatException($"{relativePath}: @witness and @no-witness only apply to rejected programs.");
        }

        return new CorpusProgram(relativePath, source, expect, witnesses, noWitness, Single("known-bug"), runtimeSkip);
    }

    public override string ToString() => RelativePath;
}
