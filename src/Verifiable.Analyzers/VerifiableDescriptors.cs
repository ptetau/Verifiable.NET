using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers;

internal static class VerifiableDescriptors
{
    private const string GatekeeperCategory = "Verifiable.Gatekeeper";
    private const string LinearCategory = "Verifiable.LinearTypes";
    private const string SmtCategory = "Verifiable.SMT";

    public static readonly DiagnosticDescriptor HeapAliasingOrMutableState = new(
        id: "VER001",
        title: "Prohibited mutable state or heap aliasing in verifiable scope",
        messageFormat: "{0}",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MutableCollection = new(
        id: "VER002",
        title: "Prohibited mutable collection in verifiable scope",
        messageFormat: "{0}",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UncheckedInitialization = new(
        id: "VER003",
        title: "Unchecked initialization of constrained type",
        messageFormat: "{0}",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NondeterminismOrAmbientAuthority = new(
        id: "VER004",
        title: "Prohibited nondeterminism or ambient authority in verifiable scope",
        messageFormat: "{0}",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DisallowedControlFlow = new(
        id: "VER005",
        title: "Prohibited control flow or exception in verifiable scope",
        messageFormat: "{0}",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnverifiedCallOrDynamic = new(
        id: "VER006",
        title: "Unverified call or dynamic dispatch crosses verifiable boundary",
        messageFormat: "{0}",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LinearObligationViolation = new(
        id: "VER007",
        title: "Linear obligation token contract violated",
        messageFormat: "{0}",
        category: LinearCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidContractExpression = new(
        id: "VER101",
        title: "Invalid or unsupported contract specification",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ProofObligationFailed = new(
        id: "VER102",
        title: "SMT verification obligation failed",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SmtSolverUnknown = new(
        id: "VER103",
        title: "SMT solver returned unknown or timed out",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Z3SolverUnavailable = new(
        id: "VER104",
        title: "Z3 SMT solver executable not found",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static Diagnostic CreateScoped(
        DiagnosticDescriptor descriptor,
        Location location,
        bool isCandidate,
        ImmutableDictionary<string, string?>? properties = null,
        params object?[] args)
    {
        var severity = isCandidate ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
        var warningLevel = isCandidate ? 1 : 0;
        var subject = args.Length > 0 ? args[0]?.ToString() ?? string.Empty : string.Empty;
        var formatted = FormatGatekeeperOrFallback(descriptor.Id, location, subject, args);
        return Diagnostic.Create(
            id: descriptor.Id,
            category: descriptor.Category,
            message: formatted,
            severity: severity,
            defaultSeverity: descriptor.DefaultSeverity,
            isEnabledByDefault: descriptor.IsEnabledByDefault,
            warningLevel: warningLevel,
            title: descriptor.Title,
            description: descriptor.Description,
            helpLink: descriptor.HelpLinkUri,
            location: location,
            additionalLocations: null,
            customTags: descriptor.CustomTags,
            properties: properties);
    }

    private static string FormatGatekeeperOrFallback(
        string id,
        Location location,
        string subject,
        object?[] args)
    {
        return id switch
        {
            "VER001" => ElmDiagnosticFormatter.Format(
                "MUTABLE STATE",
                location,
                $"Inside a `[Verifiable]` scope, `{subject}` allows aliasing or in-place mutation.",
                spirit: "Two references to the same mutable object make it impossible to prove locally what changed across a method call.",
                fixHint: "Use a `readonly record struct` or immutable `record` with `with` expressions instead of mutable heap state or ref/out aliasing."),
            "VER002" => ElmDiagnosticFormatter.Format(
                "MUTABLE COLLECTION",
                location,
                $"Mutable collection `{subject}` cannot cross or live inside a `[Verifiable]` cage.",
                spirit: "Mutable lists, dictionaries, and arrays can be changed out from under a proof after a check passes.",
                fixHint: "Replace with `ImmutableList<T>`, `ImmutableDictionary<K, V>`, `ImmutableHashSet<T>`, or `ImmutableArray<T>`."),
            "VER003" => ElmDiagnosticFormatter.Format(
                "UNCHECKED INITIALIZATION",
                location,
                $"Using `default` or `new` on `{subject}` skips its `[TypeAlias<T>]` / `[Invariant]` validation.",
                spirit: "Unchecked struct defaults can put an impossible value (like an empty ID or negative quantity) inside a verified cage.",
                fixHint: $"Construct `{subject}` using `{subject}.From(...)`, `{subject}.TryFrom(...)`, or an `[InboundPort]` gate."),
            "VER004" => ElmDiagnosticFormatter.Format(
                "AMBIENT AUTHORITY",
                location,
                $"Non-deterministic or ambient construct `{subject}` cannot be called inside a `[Verifiable]` scope.",
                spirit: "Verified code must always return the same output for the same inputs so Z3 proofs and GalaxyCheck replays remain 100% reproducible.",
                fixHint: "Hoist the time/ID/random value into an explicit parameter such as `UnixTimestamp now` or `Guid nextId`."),
            "VER005" => ElmDiagnosticFormatter.Format(
                "DISALLOWED CONTROL FLOW",
                location,
                $"Control flow `{subject}` is not allowed inside a `[Verifiable]` cage.",
                spirit: "Hidden exceptions, async state machines, and unannotated loops break straight-line mathematical reasoning.",
                fixHint: "Use bounded loops or `Spec.LoopInvariant`/`Spec.Decreases`, avoid async/lock/goto, and return `Result<T, TError>` instead of throwing exceptions."),
            "VER006" => ElmDiagnosticFormatter.Format(
                "UNVERIFIED CALL",
                location,
                $"Call `{subject}` crosses out of the verified closed world.",
                spirit: "Z3 cannot prove invariants hold if a verified method calls unverified code whose behavior is unknown.",
                fixHint: "Mark the callee `[Verifiable]` or `[PureAxiom]`, or move the I/O outside via Sandwich I/O splitting."),
            _ => args.Length == 1 ? subject : string.Join(" ", args)
        };
    }
}
