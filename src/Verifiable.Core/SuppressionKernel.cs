namespace Verifiable.Core;

/// <summary>
/// The severity a project configured for one diagnostic ID. The values match Roslyn's
/// <c>ReportDiagnostic</c>, from which the analyzer converts.
/// </summary>
[Verifiable]
internal enum ConfiguredSeverity
{
    /// <summary>Nothing configured, or <c>default</c>: the diagnostic keeps the severity it is reported with.</summary>
    Default = 0,

    /// <summary><c>error</c>.</summary>
    Error = 1,

    /// <summary><c>warning</c>.</summary>
    Warning = 2,

    /// <summary><c>suggestion</c>.</summary>
    Info = 3,

    /// <summary><c>silent</c>: reported to the IDE only, never to the build.</summary>
    Hidden = 4,

    /// <summary><c>none</c>, <c>&lt;NoWarn&gt;</c>, a <c>#pragma</c> or a <c>[SuppressMessage]</c>: not reported at all.</summary>
    Suppressed = 5
}

/// <summary>
/// Decides when a project has weakened a proof check, which <c>VER107</c> then reports.
/// </summary>
/// <remarks>
/// A proof diagnostic stops the build because it is an error. Lowering it below an error lets unproved
/// code build as if it were proved. These rules say which IDs are proof checks and which configured
/// severities count as weakening them. Diagnostic IDs are compared exactly (ordinal), as Roslyn does
/// for <c>#pragma</c>, <c>&lt;NoWarn&gt;</c> and <c>[SuppressMessage]</c>.
/// </remarks>
internal static class SuppressionKernel
{
    /// <summary>The ID of the diagnostic that reports a weakened proof check.</summary>
    internal const string AuditId = "VER107";

    /// <summary>
    /// Whether a diagnostic ID is one of the proof checks: <c>VER101</c> (unsupported contract) to
    /// <c>VER106</c> (vacuity check undecided).
    /// </summary>
    internal static bool IsProofCheckId(string id)
    {
        Spec.Ensures<bool>(
            r => r == (id == "VER101" || id == "VER102" || id == "VER103" ||
                       id == "VER104" || id == "VER105" || id == "VER106"),
            "Exactly VER101 to VER106 report that something was not proved; each one must stop the build.");
        Spec.Requires(id is not null, "Diagnostic IDs are never null.");

        return id == "VER101" || id == "VER102" || id == "VER103" ||
               id == "VER104" || id == "VER105" || id == "VER106";
    }

    /// <summary>
    /// Whether weakening this ID is reported: every proof check, and <c>VER107</c> itself, so that
    /// switching the audit off in one file is also reported.
    /// </summary>
    internal static bool IsGuardedId(string id)
    {
        Spec.Ensures<bool>(
            r => r == (id == "VER101" || id == "VER102" || id == "VER103" ||
                       id == "VER104" || id == "VER105" || id == "VER106" || id == "VER107"),
            "The audit watches every proof check and also itself, and nothing else.");
        Spec.Requires(id is not null, "Diagnostic IDs are never null.");

        return IsProofCheckId(id) || id == AuditId;
    }

    /// <summary>
    /// Whether configuring <paramref name="configured"/> for <paramref name="id"/> weakens it. A proof
    /// check is weakened by anything below an error. The one exception is <c>VER104</c> as a warning when
    /// the project set <c>VerifiableAllowMissingZ3</c>: that is the deliberate, visible opt-out.
    /// <c>VER107</c> is a warning by default, so it is weakened by anything below a warning.
    /// </summary>
    internal static bool IsWeakening(string id, ConfiguredSeverity configured, bool allowMissingSolver)
    {
        Spec.Ensures<bool>(
            r => r ==
                 (((id == "VER101" || id == "VER102" || id == "VER103" ||
                    id == "VER104" || id == "VER105" || id == "VER106") &&
                   (configured == ConfiguredSeverity.Info ||
                    configured == ConfiguredSeverity.Hidden ||
                    configured == ConfiguredSeverity.Suppressed ||
                    (configured == ConfiguredSeverity.Warning && !(id == "VER104" && allowMissingSolver)))) ||
                  (id == "VER107" &&
                   (configured == ConfiguredSeverity.Info ||
                    configured == ConfiguredSeverity.Hidden ||
                    configured == ConfiguredSeverity.Suppressed))),
            "A proof check lowered below an error lets unproved code build, so it is reported; only the explicit missing-Z3 opt-out may make VER104 a warning.");
        Spec.Requires(id is not null, "Diagnostic IDs are never null.");

        var belowWarning =
            configured == ConfiguredSeverity.Info ||
            configured == ConfiguredSeverity.Hidden ||
            configured == ConfiguredSeverity.Suppressed;
        if (IsProofCheckId(id))
        {
            if (belowWarning) return true;
            if (configured != ConfiguredSeverity.Warning) return false;
            return !(id == "VER104" && allowMissingSolver);
        }

        return id == AuditId && belowWarning;
    }

    /// <summary>
    /// Whether the check ID of a <c>[SuppressMessage]</c> names a guarded diagnostic. Roslyn reads the ID
    /// as everything before the first <c>:</c>, so <c>"VER102:Proof"</c> suppresses <c>VER102</c>.
    /// </summary>
    internal static bool SuppressMessageTargetsGuardedId(string checkId)
    {
        Spec.Ensures<bool>(
            r => r ==
                 (checkId == "VER101" || checkId == "VER102" || checkId == "VER103" || checkId == "VER104" ||
                  checkId == "VER105" || checkId == "VER106" || checkId == "VER107" ||
                  checkId.StartsWith("VER101:", System.StringComparison.Ordinal) ||
                  checkId.StartsWith("VER102:", System.StringComparison.Ordinal) ||
                  checkId.StartsWith("VER103:", System.StringComparison.Ordinal) ||
                  checkId.StartsWith("VER104:", System.StringComparison.Ordinal) ||
                  checkId.StartsWith("VER105:", System.StringComparison.Ordinal) ||
                  checkId.StartsWith("VER106:", System.StringComparison.Ordinal) ||
                  checkId.StartsWith("VER107:", System.StringComparison.Ordinal)),
            "A suppression names a proof check whether or not it adds `:Title` after the ID, so both forms are caught.");
        Spec.Requires(checkId is not null, "A [SuppressMessage] check ID is never null here.");

        return IsGuardedId(checkId) ||
               checkId.StartsWith("VER101:", System.StringComparison.Ordinal) ||
               checkId.StartsWith("VER102:", System.StringComparison.Ordinal) ||
               checkId.StartsWith("VER103:", System.StringComparison.Ordinal) ||
               checkId.StartsWith("VER104:", System.StringComparison.Ordinal) ||
               checkId.StartsWith("VER105:", System.StringComparison.Ordinal) ||
               checkId.StartsWith("VER106:", System.StringComparison.Ordinal) ||
               checkId.StartsWith("VER107:", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads an analyzer-config severity value (already trimmed and lower-cased, as Roslyn compares them
    /// without case). An unknown value is ignored by Roslyn, so it reads as <see cref="ConfiguredSeverity.Default"/>.
    /// </summary>
    internal static ConfiguredSeverity ParseSeverity(string value)
    {
        Spec.Ensures<ConfiguredSeverity>(
            r => (r == ConfiguredSeverity.Suppressed) == (value == "none"),
            "Only `none` switches a diagnostic off.");
        Spec.Ensures<ConfiguredSeverity>(
            r => (r == ConfiguredSeverity.Hidden) == (value == "silent" || value == "refactoring"),
            "`silent` and `refactoring` hide a diagnostic from the build.");
        Spec.Ensures<ConfiguredSeverity>(
            r => (r == ConfiguredSeverity.Info) == (value == "suggestion"),
            "`suggestion` makes a diagnostic informational.");
        Spec.Ensures<ConfiguredSeverity>(
            r => (r == ConfiguredSeverity.Warning) == (value == "warning"),
            "`warning` makes a diagnostic a warning.");
        Spec.Ensures<ConfiguredSeverity>(
            r => (r == ConfiguredSeverity.Error) == (value == "error"),
            "`error` makes a diagnostic an error.");
        Spec.Requires(value is not null, "Analyzer-config values are never null.");

        if (value == "none") return ConfiguredSeverity.Suppressed;
        if (value == "silent" || value == "refactoring") return ConfiguredSeverity.Hidden;
        if (value == "suggestion") return ConfiguredSeverity.Info;
        if (value == "warning") return ConfiguredSeverity.Warning;
        if (value == "error") return ConfiguredSeverity.Error;
        return ConfiguredSeverity.Default;
    }
}
