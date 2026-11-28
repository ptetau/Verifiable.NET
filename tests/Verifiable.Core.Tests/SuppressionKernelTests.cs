using System;
using System.Linq;
using GalaxyCheck;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Core.Tests;

/// <summary>
/// Checks the compiled VER107 rules against reference implementations written a different way
/// (a list lookup and a severity ranking), without Z3.
/// </summary>
public class SuppressionKernelTests
{
    private static readonly string[] ProofIds = ["VER101", "VER102", "VER103", "VER104", "VER105", "VER106"];
    private static readonly string[] GuardedIds = [.. ProofIds, "VER107"];
    private static readonly ConfiguredSeverity[] AllSeverities = Enum.GetValues<ConfiguredSeverity>();

    private static readonly string[] IdSamples =
    [
        .. Enumerable.Range(0, 400).Select(n => $"VER{n:D3}"),
        "ver102", "Ver102", "VER102 ", " VER102", "VER1020", "VER10", "VER", "", "CS0168", "VER102:Title", "*"
    ];

    // Lower rank = weaker. Default keeps the reported severity, so it never weakens anything.
    private static int Rank(ConfiguredSeverity severity) => severity switch
    {
        ConfiguredSeverity.Suppressed => 0,
        ConfiguredSeverity.Hidden => 1,
        ConfiguredSeverity.Info => 2,
        ConfiguredSeverity.Warning => 3,
        ConfiguredSeverity.Error => 4,
        _ => int.MaxValue
    };

    private static bool ReferenceIsWeakening(string id, ConfiguredSeverity severity, bool allowMissingSolver)
    {
        if (!GuardedIds.Contains(id, StringComparer.Ordinal)) return false;
        var floor = id == "VER107" || (id == "VER104" && allowMissingSolver)
            ? ConfiguredSeverity.Warning
            : ConfiguredSeverity.Error;
        return Rank(severity) < Rank(floor);
    }

    [Fact]
    public void IsProofCheckId_IsExactlyVer101ToVer106()
    {
        foreach (var id in IdSamples)
        {
            Assert.Equal(ProofIds.Contains(id, StringComparer.Ordinal), SuppressionKernel.IsProofCheckId(id));
            Assert.Equal(GuardedIds.Contains(id, StringComparer.Ordinal), SuppressionKernel.IsGuardedId(id));
        }
    }

    [Property]
    public bool IsGuardedId_MatchesReference(string id)
    {
        id ??= string.Empty;
        return SuppressionKernel.IsGuardedId(id) == GuardedIds.Contains(id, StringComparer.Ordinal)
            && SuppressionKernel.IsProofCheckId(id) == ProofIds.Contains(id, StringComparer.Ordinal);
    }

    [Fact]
    public void IsWeakening_MatchesTheRanking_ForEveryIdSeverityAndOptOut()
    {
        foreach (var id in IdSamples)
        {
            foreach (var severity in AllSeverities)
            {
                foreach (var allow in new[] { false, true })
                {
                    Assert.True(
                        ReferenceIsWeakening(id, severity, allow) == SuppressionKernel.IsWeakening(id, severity, allow),
                        $"IsWeakening({id}, {severity}, allowMissingSolver: {allow})");
                }
            }
        }
    }

    [Fact]
    public void IsWeakening_KeyCases()
    {
        Assert.True(SuppressionKernel.IsWeakening("VER102", ConfiguredSeverity.Warning, allowMissingSolver: false));
        Assert.True(SuppressionKernel.IsWeakening("VER106", ConfiguredSeverity.Suppressed, allowMissingSolver: false));
        Assert.False(SuppressionKernel.IsWeakening("VER102", ConfiguredSeverity.Default, allowMissingSolver: false));
        Assert.False(SuppressionKernel.IsWeakening("VER102", ConfiguredSeverity.Error, allowMissingSolver: false));
        Assert.True(SuppressionKernel.IsWeakening("VER104", ConfiguredSeverity.Warning, allowMissingSolver: false));
        Assert.False(SuppressionKernel.IsWeakening("VER104", ConfiguredSeverity.Warning, allowMissingSolver: true));
        Assert.True(SuppressionKernel.IsWeakening("VER104", ConfiguredSeverity.Suppressed, allowMissingSolver: true));
        Assert.False(SuppressionKernel.IsWeakening("VER107", ConfiguredSeverity.Warning, allowMissingSolver: false));
        Assert.True(SuppressionKernel.IsWeakening("VER107", ConfiguredSeverity.Hidden, allowMissingSolver: false));
        Assert.False(SuppressionKernel.IsWeakening("CS0168", ConfiguredSeverity.Suppressed, allowMissingSolver: false));
    }

    private static bool ReferenceTargets(string checkId)
    {
        var colon = checkId.IndexOf(':');
        var id = colon < 0 ? checkId : checkId.Substring(0, colon);
        return GuardedIds.Contains(id, StringComparer.Ordinal);
    }

    [Fact]
    public void SuppressMessageTargetsGuardedId_ReadsTheIdBeforeTheColon()
    {
        var samples = IdSamples
            .Concat(IdSamples.Select(id => id + ":Title"))
            .Concat(["VER102:", "VER107:x:y", "VER102 :x", "xVER102:", ":VER102"]);
        foreach (var checkId in samples)
        {
            Assert.True(
                ReferenceTargets(checkId) == SuppressionKernel.SuppressMessageTargetsGuardedId(checkId),
                $"SuppressMessageTargetsGuardedId(\"{checkId}\")");
        }
    }

    [Property]
    public bool SuppressMessageTargetsGuardedId_MatchesReference(string checkId)
    {
        checkId ??= string.Empty;
        return SuppressionKernel.SuppressMessageTargetsGuardedId(checkId) == ReferenceTargets(checkId);
    }

    private static ConfiguredSeverity ReferenceParse(string value) => value switch
    {
        "none" => ConfiguredSeverity.Suppressed,
        "silent" or "refactoring" => ConfiguredSeverity.Hidden,
        "suggestion" => ConfiguredSeverity.Info,
        "warning" => ConfiguredSeverity.Warning,
        "error" => ConfiguredSeverity.Error,
        _ => ConfiguredSeverity.Default
    };

    [Fact]
    public void ParseSeverity_ReadsEveryRoslynSeverityWord()
    {
        foreach (var value in new[] { "none", "silent", "refactoring", "suggestion", "warning", "error", "default", "None", "warn", "", "info", "hidden" })
        {
            Assert.Equal(ReferenceParse(value), SuppressionKernel.ParseSeverity(value));
        }
    }

    [Property]
    public bool ParseSeverity_MatchesReference(string value)
    {
        value ??= string.Empty;
        return SuppressionKernel.ParseSeverity(value) == ReferenceParse(value);
    }
}
