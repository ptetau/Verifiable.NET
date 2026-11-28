using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// src/Verifiable.Core is proved at build time by Verifiable.Analyzers.Bootstrap: the analyzer sources
/// built with a frozen, earlier copy of the kernel (src/Verifiable.Analyzers.Bootstrap/Kernel). These
/// tests load that same stage-0 analyzer and check that it proves the live kernel, that it still rejects
/// a live kernel bug that would blind the live prover, and that it rejects plainly false contracts.
/// </summary>
/// <remarks>
/// The frozen copy is expected to lag the live kernel: every kernel change creates drift until someone
/// runs scripts/promote-bootstrap-kernel.sh. Drift alone is therefore not a failure. It is written to the
/// test output, and it is named in the failure message when the frozen kernel can no longer prove the live one.
/// </remarks>
public class BootstrapKernelTests
{
    internal const string AssemblyName = "Verifiable.Analyzers.Bootstrap";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string LiveKernelDirectory = Path.Combine(RepoRoot, "src", "Verifiable.Core");
    private static readonly string FrozenKernelDirectory = Path.Combine(RepoRoot, "src", "Verifiable.Analyzers.Bootstrap", "Kernel");
    private static readonly Lazy<Assembly> Bootstrap = new(LoadBootstrapAnalyzer);

    private readonly ITestOutputHelper _output;

    public BootstrapKernelTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BootstrapAnalyzer_IsNotTheShippedAnalyzer()
    {
        var bootstrap = Bootstrap.Value;

        Assert.Equal(AssemblyName, bootstrap.GetName().Name);
        Assert.NotEqual(typeof(Smt.VerifiableSmtAnalyzer).Assembly, bootstrap);
        Assert.NotNull(bootstrap.GetType("Verifiable.Core.ProofKernel")); // it carries its own (frozen) kernel
    }

    [Fact]
    public async Task FrozenKernel_ProvesLiveKernel()
    {
        var drift = DescribeDrift();
        _output.WriteLine(drift);

        var diagnostics = await VerifyWithBootstrapAsync(LiveKernelSources());

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER104"); // a missing solver would make the proof vacuous
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0,
            "The frozen kernel could not prove the live kernel. " + drift + Environment.NewLine +
            string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The bug that motivated the bootstrap: an extra <c>(assert false)</c> in the live
    /// BuildRefutationQuery makes every live query unsat, so an analyzer built from the live kernel
    /// approves it. The frozen kernel builds honest queries and must reject it.
    /// </summary>
    [Fact]
    public async Task FrozenKernel_RejectsLiveKernelThatBlindsItsProver()
    {
        var blinded = new[]
        {
            ("return preamble + \"\\n(assert (not \" + goal + \"))\\n\";",
             "return preamble + \"\\n(assert false)\\n(assert (not \" + goal + \"))\\n\";"),
            ("return preamble + \"\\n(assert \" + pathCondition + \")\\n(assert (not \" + goal + \"))\\n\";",
             "return preamble + \"\\n(assert \" + pathCondition + \")\\n(assert false)\\n(assert (not \" + goal + \"))\\n\";"),
        };

        var sources = LiveKernelSources().Select(s =>
        {
            if (Path.GetFileName(s.Path) != "ProofKernel.cs") return s;
            var text = s.Source.Replace("\r\n", "\n");
            foreach (var (original, broken) in blinded)
            {
                Assert.Contains(original, text);
                text = text.Replace(original, broken);
            }
            return (s.Path, text);
        }).ToList();

        var diagnostics = await VerifyWithBootstrapAsync(sources);

        Assert.Equal(2, diagnostics.Count(d =>
            d.Id == "VER102" && d.GetMessage().Contains("ProofKernel.BuildRefutationQuery", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("public static int F(int x) { Spec.Ensures<int>(r => r > x, \"grows\"); return x; }")]
    [InlineData("public static int F(int x) { Spec.Ensures<int>(r => r >= 0, \"non-negative\"); if (x > 0) return x; return x - 1; }")]
    public async Task Canary_BootstrapAnalyzer_RejectsFalseContracts(string member)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            "using Verifiable.Contracts;\n[Verifiable] public static class Canary { " + member + " }",
            CreateBootstrapAnalyzers());

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER104");
        Assert.Contains(diagnostics, d => d.Id == "VER102");
    }

    private static Task<ImmutableArray<Diagnostic>> VerifyWithBootstrapAsync(IEnumerable<(string Path, string Source)> sources) =>
        AnalyzerTestHarness.AnalyzeSourcesAsync(
            sources.Append(("GlobalUsings.cs", "global using Verifiable.Contracts;")),
            CreateBootstrapAnalyzers());

    /// <summary>Every analyzer and suppressor in the bootstrap DLL, as the compiler loads them for Verifiable.Core.</summary>
    private static DiagnosticAnalyzer[] CreateBootstrapAnalyzers() =>
        Bootstrap.Value.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t) &&
                        t.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), inherit: false).Length > 0)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToArray();

    private static Assembly LoadBootstrapAnalyzer()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "bootstrap", AssemblyName + ".dll");
        Assert.True(File.Exists(path), $"The bootstrap analyzer was not copied to {path}. Build Verifiable.Analyzers.Tests to copy it.");
        return Assembly.LoadFrom(path);
    }

    /// <summary>
    /// Lists the kernel files that differ between the frozen copy and the live kernel. Only the files the
    /// analyzer compiles are compared: AssemblyInfo.cs and Proofs/ are never part of the frozen copy.
    /// </summary>
    private static string DescribeDrift()
    {
        var live = ShippedKernelFiles(LiveKernelDirectory);
        var frozen = ShippedKernelFiles(FrozenKernelDirectory);

        var lines = new List<string>();
        foreach (var file in live.Keys.Union(frozen.Keys).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!frozen.ContainsKey(file)) lines.Add($"  new in live kernel: {file}");
            else if (!live.ContainsKey(file)) lines.Add($"  removed from live kernel: {file}");
            else if (live[file] != frozen[file]) lines.Add($"  changed: {file}");
        }

        return lines.Count == 0
            ? "The frozen kernel is identical to the live kernel."
            : "The frozen kernel differs from the live kernel (promote it with scripts/promote-bootstrap-kernel.sh " +
              "once the live kernel is proved):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static Dictionary<string, string> ShippedKernelFiles(string root) =>
        SourceFiles(root)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(rel => rel != "AssemblyInfo.cs" && !rel.StartsWith("Proofs/", StringComparison.Ordinal))
            .ToDictionary(rel => rel, rel => File.ReadAllText(Path.Combine(root, rel)).Replace("\r\n", "\n"), StringComparer.Ordinal);

    private static List<(string Path, string Source)> LiveKernelSources() =>
        SourceFiles(LiveKernelDirectory)
            .Select(p => (p, File.ReadAllText(p)))
            .ToList();

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (Verifiable.NET.sln).");
    }
}
