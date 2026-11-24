using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers;

internal static class VerifiableDescriptors
{
    private const string GatekeeperCategory = "Verifiable.Gatekeeper";
    private const string LinearCategory = "Verifiable.LinearTypes";

    public static readonly DiagnosticDescriptor HeapAliasingOrMutableState = new(
        id: "VER001",
        title: "Prohibited mutable state or heap aliasing in verifiable scope",
        messageFormat: "Construct '{0}' violates immutability/aliasing rules inside a verifiable cage. Fix: Use a 'readonly record struct' or immutable 'record' with 'with' expressions instead of mutable heap state or ref/out aliasing.",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MutableCollection = new(
        id: "VER002",
        title: "Prohibited mutable collection in verifiable scope",
        messageFormat: "Mutable collection '{0}' cannot be used inside a verifiable cage. Fix: Replace with 'ImmutableList<T>', 'ImmutableDictionary<K, V>', 'ImmutableHashSet<T>', or 'ImmutableArray<T>'.",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UncheckedInitialization = new(
        id: "VER003",
        title: "Unchecked initialization of constrained type",
        messageFormat: "Unchecked initialization of '{0}' bypasses its [Invariant] or [Refined<T>] contract. Fix: Construct '{0}' using '{0}.From(...)', '{0}.TryFrom(...)', or an [InboundPort] gate.",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NondeterminismOrAmbientAuthority = new(
        id: "VER004",
        title: "Prohibited nondeterminism or ambient authority in verifiable scope",
        messageFormat: "Ambient authority or nondeterministic construct '{0}' is prohibited inside a verifiable cage. Fix: Pass explicit deterministic parameters such as 'UnixTimestamp now' or 'Guid nextId'.",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DisallowedControlFlow = new(
        id: "VER005",
        title: "Prohibited control flow or exception in verifiable scope",
        messageFormat: "Control flow construct '{0}' is prohibited inside a verifiable cage. Fix: Use bounded loops or [LoopInvariant]/[Decreases], avoid async/lock/goto, and return 'Result<T, TError>' instead of throwing exceptions.",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnverifiedCallOrDynamic = new(
        id: "VER006",
        title: "Unverified call or dynamic dispatch crosses verifiable boundary",
        messageFormat: "Call or construct '{0}' is outside the closed verifiable world. Fix: Mark the target method [Verifiable] or [PureAxiom], or extract pure logic via Sandwich I/O splitting.",
        category: GatekeeperCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LinearObligationViolation = new(
        id: "VER007",
        title: "Linear obligation token contract violated",
        messageFormat: "Linear obligation token '{0}' ({1}) {2}. Fix: Pass the [MustConsume] token to a [ConsumesObligation] method, return it, or explicitly call 'Obligation.Discard(...)'.",
        category: LinearCategory,
        defaultSeverity: DiagnosticSeverity.Error,
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
        var formatted = string.Format(descriptor.MessageFormat.ToString(), args);
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
}
