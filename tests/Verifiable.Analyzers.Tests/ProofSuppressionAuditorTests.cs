using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// VER107: every way a project can lower a proof diagnostic below an error is reported, once, with
/// what was weakened and how to undo it. Where a test also runs the prover, it shows the setting
/// really hides the proof failure, so the report is about something real.
/// </summary>
public sealed class ProofSuppressionAuditorTests
{
    private const string FailingProof = """
        using Verifiable.Contracts;
        [Verifiable] public static class Ledger
        {
            public static int Grow(int x) { Spec.Ensures<int>(r => r > x, "grows"); return x; }
        }
        """;

    private const string PlainCode = """
        public static class Helpers
        {
            public static int Same(int x) => x;
        }
        """;

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        string source,
        IEnumerable<(string Name, string Text)>? configs = null,
        IReadOnlyDictionary<string, ReportDiagnostic>? specific = null,
        IReadOnlyDictionary<string, string>? properties = null,
        bool prove = false) =>
        RunFilesAsync([("Ledger.cs", source)], configs, specific, properties, prove);

    private static async Task<ImmutableArray<Diagnostic>> RunFilesAsync(
        IEnumerable<(string Name, string Source)> sources,
        IEnumerable<(string Name, string Text)>? configs = null,
        IReadOnlyDictionary<string, ReportDiagnostic>? specific = null,
        IReadOnlyDictionary<string, string>? properties = null,
        bool prove = false)
    {
        DiagnosticAnalyzer[] analyzers = prove
            ? [new ProofSuppressionAuditor(), new VerifiableSmtAnalyzer()]
            : [new ProofSuppressionAuditor()];
        var diagnostics = await AnalyzerConfigTestHarness.AnalyzeAsync(sources, configs, specific, properties, analyzers);
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("CS", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);
        return diagnostics;
    }

    private static List<Diagnostic> Audits(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Where(d => d.Id == "VER107").ToList();

    private static string FirstLine(Diagnostic d) => d.GetMessage().Split('\n')[0];

    // ---- compiler options: <NoWarn>, -nowarn, rulesets ----

    [Fact]
    public async Task NoWarn_IsReportedProjectWide_AndReallyHidesTheFailure()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            specific: new Dictionary<string, ReportDiagnostic> { ["VER102"] = ReportDiagnostic.Suppress },
            prove: true);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER102");
        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(Location.None, audit.Location);
        Assert.Equal(DiagnosticSeverity.Error, audit.Severity);
        Assert.Contains("VER102", FirstLine(audit));
        Assert.Contains("Remove VER102 from <NoWarn>", FirstLine(audit));
    }

    // ---- VER107 is an error, unless the project opts into weakened proofs ----

    [Fact]
    public async Task Audit_IsAnError_ByDefault_ForEveryKindOfWeakening()
    {
        var pragma = await RunAsync("#pragma warning disable VER102 // not yet\n" + FailingProof, prove: true);
        var noWarn = await RunAsync(FailingProof, specific: new Dictionary<string, ReportDiagnostic> { ["VER102"] = ReportDiagnostic.Suppress });
        var config = await RunAsync(
            FailingProof,
            configs: [(".globalconfig", "is_global = true\ndotnet_diagnostic.VER105.severity = none\n")]);

        Assert.All(new[] { pragma, noWarn, config }, diagnostics =>
            Assert.Equal(DiagnosticSeverity.Error, Assert.Single(Audits(diagnostics)).Severity));
        Assert.Equal(DiagnosticSeverity.Error, ProofSuppressionAuditor.ProofCheckWeakened.DefaultSeverity);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    public async Task VerifiableAllowWeakenedProofs_MakesTheAuditAWarning_ThatIsStillReported(string value)
    {
        var diagnostics = await RunAsync(
            "#pragma warning disable VER102 // not yet\n" + FailingProof,
            properties: new Dictionary<string, string> { ["VerifiableAllowWeakenedProofs"] = value },
            prove: true);

        // The weakening still hides the proof failure; the audit stays visible but no longer fails the build.
        Assert.DoesNotContain(diagnostics, d => d.Id == "VER102");
        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(DiagnosticSeverity.Warning, audit.Severity);
        Assert.Contains("`#pragma warning disable VER102` hides VER102", FirstLine(audit));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("yes")]
    public async Task VerifiableAllowWeakenedProofs_OtherValues_KeepTheAuditAnError(string value)
    {
        var diagnostics = await RunAsync(
            "#pragma warning disable VER102 // not yet\n" + FailingProof,
            properties: new Dictionary<string, string> { ["VerifiableAllowWeakenedProofs"] = value });

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(Audits(diagnostics)).Severity);
    }

    [Fact]
    public async Task VerifiableAllowWeakenedProofs_AppliesToProjectWideWeakeningsToo()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            specific: new Dictionary<string, ReportDiagnostic> { ["VER102"] = ReportDiagnostic.Suppress },
            properties: new Dictionary<string, string> { ["VerifiableAllowWeakenedProofs"] = "true" });

        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(DiagnosticSeverity.Warning, audit.Severity);
        Assert.Equal(Location.None, audit.Location);
    }

    [Fact]
    public void BuildProps_ExposeTheOptOutToTheAnalyzer_AndKeepItAWarningUnderWarnAsError()
    {
        var props = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Verifiable.NET", "build", "Verifiable.NET.props"));

        Assert.Contains("<CompilerVisibleProperty Include=\"VerifiableAllowWeakenedProofs\" />", props, StringComparison.Ordinal);
        Assert.Contains("WarningsNotAsErrors>$(WarningsNotAsErrors);VER107", props, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task RulesetWarning_IsReported()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            specific: new Dictionary<string, ReportDiagnostic> { ["VER103"] = ReportDiagnostic.Warn });

        var audit = Assert.Single(Audits(diagnostics));
        Assert.Contains("VER103 is lowered to warning", FirstLine(audit));
    }

    [Theory]
    [InlineData(ReportDiagnostic.Default)] // <WarningsNotAsErrors>VER102</WarningsNotAsErrors> (-warnaserror-:VER102)
    [InlineData(ReportDiagnostic.Error)]   // <WarningsAsErrors>VER102</WarningsAsErrors>
    public async Task PromotionSettings_AreNotWeakenings(ReportDiagnostic report)
    {
        var diagnostics = await RunAsync(
            FailingProof,
            specific: new Dictionary<string, ReportDiagnostic> { ["VER102"] = report },
            prove: true);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(Audits(diagnostics));
    }

    [Fact]
    public async Task NoWarnWithDifferentCase_DoesNotSuppress_AndIsNotReported()
    {
        // Roslyn matches <NoWarn> IDs exactly, so `ver102` hides nothing.
        var diagnostics = await RunAsync(
            FailingProof,
            specific: new Dictionary<string, ReportDiagnostic> { ["ver102"] = ReportDiagnostic.Suppress },
            prove: true);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(Audits(diagnostics));
    }

    // ---- .editorconfig and .globalconfig ----

    [Fact]
    public async Task EditorConfigSeverity_IsReportedOnce_NamingTheFiles()
    {
        var diagnostics = await RunFilesAsync(
            [("Ledger.cs", FailingProof), ("Other.cs", FailingProof.Replace("Ledger", "Other"))],
            configs: [(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.VER102.severity = warning\n")],
            prove: true);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.Severity == DiagnosticSeverity.Warning);
        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(Location.None, audit.Location);
        Assert.Contains("`dotnet_diagnostic.VER102.severity = warning` in .editorconfig", FirstLine(audit));
        Assert.Contains("Ledger.cs, Other.cs", FirstLine(audit));
    }

    [Fact]
    public async Task EditorConfigSeverity_OnlyForFilesWithoutVerifiableCode_IsNotReported()
    {
        var diagnostics = await RunFilesAsync(
            [("Ledger.cs", FailingProof), ("Helpers.cs", PlainCode)],
            configs: [(".editorconfig", "root = true\n[Helpers.cs]\ndotnet_diagnostic.VER102.severity = none\n")]);

        Assert.Empty(Audits(diagnostics));
    }

    [Fact]
    public async Task GlobalConfigSeverity_IsReportedProjectWide()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            configs: [(".globalconfig", "is_global = true\ndotnet_diagnostic.VER105.severity = none\n")]);

        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(Location.None, audit.Location);
        Assert.Contains("`dotnet_diagnostic.VER105.severity = none` in a global analyzer config", FirstLine(audit));
    }

    [Fact]
    public async Task CategorySetting_IsReported_ForEveryProofId()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            configs: [(".globalconfig", "is_global = true\ndotnet_analyzer_diagnostic.category-Verifiable.SMT.severity = suggestion\n")],
            prove: true);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.Severity == DiagnosticSeverity.Info);
        var audit = Assert.Single(Audits(diagnostics));
        Assert.Contains("category-Verifiable.SMT.severity = suggestion", FirstLine(audit));
        Assert.Contains("VER101, VER102, VER103, VER104, VER105, VER106", FirstLine(audit));
    }

    [Fact]
    public async Task AllAnalyzersSetting_IsReported_ButAnIdSpecificErrorWins()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            configs: [(".editorconfig", "root = true\n[*.cs]\ndotnet_analyzer_diagnostic.severity = none\ndotnet_diagnostic.VER102.severity = error\ndotnet_diagnostic.VER107.severity = warning\n")],
            prove: true);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.Severity == DiagnosticSeverity.Error);
        var audit = Assert.Single(Audits(diagnostics));
        Assert.Contains("`dotnet_analyzer_diagnostic.severity = none` lowers VER101, VER103, VER104, VER105, VER106 in Ledger.cs", FirstLine(audit));
    }

    // ---- #pragma warning disable ----

    [Fact]
    public async Task Pragma_IsReportedAtThePragma_AndReallyHidesTheFailure()
    {
        var diagnostics = await RunAsync("#pragma warning disable VER102 // not yet\n" + FailingProof, prove: true);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER102");
        var audit = Assert.Single(Audits(diagnostics));
        Assert.True(audit.Location.IsInSource);
        Assert.Equal(0, audit.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains("`#pragma warning disable VER102` hides VER102", FirstLine(audit));
        Assert.Contains("Remove VER102 from the pragma", FirstLine(audit));
    }

    [Fact]
    public async Task BarePragma_IsReportedWithoutALocation_BecauseItWouldHideItsOwnReport()
    {
        var diagnostics = await RunAsync("#pragma warning disable\n" + FailingProof, prove: true);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER102");
        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(Location.None, audit.Location);
        Assert.Contains("with no IDs at Ledger.cs:1", FirstLine(audit));
    }

    [Fact]
    public async Task PragmaThatAlsoDisablesTheAudit_IsStillReported()
    {
        var diagnostics = await RunAsync("#pragma warning disable VER102, VER107\n" + FailingProof);

        var audit = Assert.Single(Audits(diagnostics));
        Assert.Equal(Location.None, audit.Location);
        Assert.Contains("hides VER102, VER107", FirstLine(audit));
    }

    [Fact]
    public async Task UnrelatedPragmas_AreNotReported()
    {
        var diagnostics = await RunAsync(
            "#pragma warning disable CS0168, ver102\n#pragma warning restore VER102\n" + FailingProof,
            prove: true);

        Assert.Contains(diagnostics, d => d.Id == "VER102");
        Assert.Empty(Audits(diagnostics));
    }

    [Fact]
    public async Task Pragma_InAFileWithoutVerifiableCode_IsNotReported()
    {
        var diagnostics = await RunFilesAsync(
            [("Ledger.cs", FailingProof), ("Helpers.cs", "#pragma warning disable\n" + PlainCode)]);

        Assert.Empty(Audits(diagnostics));
    }

    // ---- [SuppressMessage] ----

    [Fact]
    public async Task SuppressMessage_IsReportedAtTheAttribute_AndReallyHidesTheFailure()
    {
        var source = """
            using System.Diagnostics.CodeAnalysis;
            using Verifiable.Contracts;
            [Verifiable] public static class Ledger
            {
                [SuppressMessage("Verifiable.SMT", "VER102:ProofFailed", Justification = "later")]
                public static int Grow(int x) { Spec.Ensures<int>(r => r > x, "grows"); return x; }
            }
            """;

        var diagnostics = await RunAsync(source, prove: true);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER102");
        var audit = Assert.Single(Audits(diagnostics));
        Assert.True(audit.Location.IsInSource);
        Assert.Equal(4, audit.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains("[SuppressMessage] for \"VER102:ProofFailed\" hides VER102", FirstLine(audit));
    }

    [Fact]
    public async Task AssemblySuppressMessage_IsReportedAtTheAttribute()
    {
        var source = """
            using Verifiable.Contracts;
            [assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Verifiable.SMT", "VER102", Scope = "member", Target = "~M:Ledger.Grow(System.Int32)~System.Int32")]
            [Verifiable] public static class Ledger
            {
                public static int Grow(int x) { Spec.Ensures<int>(r => r > x, "grows"); return x; }
            }
            """;

        var diagnostics = await RunAsync(source, prove: true);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VER102");
        var audit = Assert.Single(Audits(diagnostics));
        Assert.True(audit.Location.IsInSource);
        Assert.Equal(1, audit.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains("[assembly: SuppressMessage] for \"VER102\" hides VER102", FirstLine(audit));
    }

    [Fact]
    public async Task SuppressMessage_OnCodeThatIsNotVerified_IsNotReported()
    {
        var source = FailingProof + """

            public static class Boundary
            {
                [System.Diagnostics.CodeAnalysis.SuppressMessage("Verifiable.SMT", "VER102")]
                public static int Same(int x) => x;
            }
            """;

        var diagnostics = await RunAsync(source);

        Assert.Empty(Audits(diagnostics));
    }

    // ---- where verification does not apply ----

    [Fact]
    public async Task ProjectWithoutVerifiableCode_ReportsNothing()
    {
        var source = """
            [assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Verifiable.SMT", "VER102")]
            #pragma warning disable VER102
            public static class Helpers
            {
                [System.Diagnostics.CodeAnalysis.SuppressMessage("Verifiable.SMT", "VER103")]
                public static int Same(int x) => x;
            }
            """;

        (string, string)[] configs =
        [
            (".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.VER104.severity = none\ndotnet_analyzer_diagnostic.severity = none\ndotnet_diagnostic.VER107.severity = warning\n"),
            (".globalconfig", "is_global = true\ndotnet_diagnostic.VER105.severity = none\n")
        ];
        var noWarn = new Dictionary<string, ReportDiagnostic> { ["VER102"] = ReportDiagnostic.Suppress };

        var plain = await RunFilesAsync([("Helpers.cs", source)], configs, noWarn);
        var withVerifiedFile = await RunFilesAsync([("Helpers.cs", source), ("Proved.cs", FailingProof)], configs, noWarn);

        Assert.Empty(Audits(plain));
        // Once the project has [Verifiable] code, the project-wide weakenings (<NoWarn>, .globalconfig, the
        // [assembly: SuppressMessage]) and the .editorconfig ones for Proved.cs are reported. The pragma and the
        // member attribute in the unverified file are not.
        Assert.Equal(5, Audits(withVerifiedFile).Count);
        Assert.DoesNotContain(Audits(withVerifiedFile), d => FirstLine(d).Contains("#pragma", StringComparison.Ordinal));
        Assert.DoesNotContain(Audits(withVerifiedFile), d => FirstLine(d).Contains("\"VER103\"", StringComparison.Ordinal));
        Assert.Contains(Audits(withVerifiedFile), d => FirstLine(d).Contains("VER104 in Proved.cs.", StringComparison.Ordinal));
    }

    // ---- the missing-Z3 opt-out ----

    [Fact]
    public async Task Ver104AsWarning_IsTheVisibleOptOut_WhenVerifiableAllowMissingZ3IsSet()
    {
        var config = new[] { (".globalconfig", "is_global = true\ndotnet_diagnostic.VER104.severity = warning\n") };

        var withOptOut = await RunAsync(FailingProof, configs: config,
            properties: new Dictionary<string, string> { ["VerifiableAllowMissingZ3"] = "true" });
        var withoutOptOut = await RunAsync(FailingProof, configs: config);

        Assert.Empty(Audits(withOptOut));
        Assert.Contains("VER104", FirstLine(Assert.Single(Audits(withoutOptOut))));
    }

    [Fact]
    public async Task Ver104SwitchedOff_IsReported_EvenWithTheOptOut()
    {
        var diagnostics = await RunAsync(
            FailingProof,
            specific: new Dictionary<string, ReportDiagnostic> { ["VER104"] = ReportDiagnostic.Suppress },
            properties: new Dictionary<string, string> { ["VerifiableAllowMissingZ3"] = "true" });

        Assert.Contains("VER104", FirstLine(Assert.Single(Audits(diagnostics))));
    }

    // ---- the analyzer agrees with the kernel ----

    [Fact]
    public void GuardedIds_MatchTheKernel()
    {
        var kernel = Enumerable.Range(0, 1000).Select(n => $"VER{n:D3}").Where(SuppressionKernel.IsGuardedId);

        Assert.Equal(kernel, ProofSuppressionAuditor.GuardedIds);
    }

    [Fact]
    public void ProofDescriptors_AreAllInTheAuditedCategory()
    {
        var descriptors = typeof(VerifiableDescriptors)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<DiagnosticDescriptor>()
            .Where(d => SuppressionKernel.IsProofCheckId(d.Id))
            .ToList();

        Assert.NotEmpty(descriptors);
        Assert.All(descriptors, d => Assert.Equal(ProofSuppressionAuditor.ProofCategory, d.Category));
        Assert.NotEqual(ProofSuppressionAuditor.ProofCategory, ProofSuppressionAuditor.ProofCheckWeakened.Category);
    }
}
