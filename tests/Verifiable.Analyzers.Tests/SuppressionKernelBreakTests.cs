using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Breaks the VER107 rules in <c>SuppressionKernel.cs</c> one at a time and checks each break fails its proof.
/// Kept apart from <see cref="KernelBugs"/> so packets that add kernel files do not edit the same list.
/// </summary>
public partial class KernelSelfVerificationTests
{
    public static TheoryData<string, string, string> SuppressionKernelBugs => new()
    {
        // Forgets VER106, so lowering the vacuity check would go unreported.
        { "id == \"VER104\" || id == \"VER105\" || id == \"VER106\";", "id == \"VER104\" || id == \"VER105\";", "SuppressionKernel.IsProofCheckId" },
        // Stops guarding the audit itself, so `#pragma warning disable VER107` would go unreported.
        { "return IsProofCheckId(id) || id == AuditId;", "return IsProofCheckId(id);", "SuppressionKernel.IsGuardedId" },
        // Treats only `none` as weakening; `suggestion` and `silent` would slip through.
        { "if (belowWarning) return true;", "if (configured == ConfiguredSeverity.Suppressed) return true;", "SuppressionKernel.IsWeakening" },
        // Lets VER104 become a warning without the explicit opt-out.
        { "return !(id == \"VER104\" && allowMissingSolver);", "return id != \"VER104\";", "SuppressionKernel.IsWeakening" },
        // Misses [SuppressMessage("...", "VER107")].
        { "return IsGuardedId(checkId) ||", "return IsProofCheckId(checkId) ||", "SuppressionKernel.SuppressMessageTargetsGuardedId" },
        // Misses `refactoring`, which Roslyn reads as silent.
        { "if (value == \"silent\" || value == \"refactoring\") return ConfiguredSeverity.Hidden;", "if (value == \"silent\") return ConfiguredSeverity.Hidden;", "SuppressionKernel.ParseSeverity" },
        // Reads `none` as "not configured".
        { "if (value == \"none\") return ConfiguredSeverity.Suppressed;", "if (value == \"none\") return ConfiguredSeverity.Default;", "SuppressionKernel.ParseSeverity" },
    };

    [Theory]
    [MemberData(nameof(SuppressionKernelBugs))]
    public async Task BrokenSuppressionKernel_FailsItsProof(string original, string broken, string expectedSymbol)
    {
        var sources = KernelSources().Select(s =>
        {
            if (Path.GetFileName(s.Path) != "SuppressionKernel.cs") return s;
            var text = s.Source.Replace("\r\n", "\n");
            var at = text.IndexOf(original, StringComparison.Ordinal);
            Assert.True(at >= 0, $"Not found in SuppressionKernel.cs: {original}");
            Assert.Equal(-1, text.IndexOf(original, at + 1, StringComparison.Ordinal)); // break the code, not its contract
            return (s.Path, text.Replace(original, broken));
        }).ToList();

        var diagnostics = await VerifyKernelAsync(sources);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.GetMessage().Contains(expectedSymbol, StringComparison.Ordinal));
    }
}
