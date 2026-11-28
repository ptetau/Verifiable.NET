using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers;

internal static class VerifiableDescriptors
{
    private const string GatekeeperCategory = "Verifiable.Gatekeeper";
    private const string LinearCategory = "Verifiable.LinearTypes";
    private const string SmtCategory = "Verifiable.SMT";
    private const string SpecQualityCategory = "Verifiable.SpecQuality";
    private const string ClauseCategory = "Verifiable.Clauses";

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

    public static readonly DiagnosticDescriptor ContradictoryPremises = new(
        id: "VER105",
        title: "Contract premises contradict each other, so proofs would be vacuous",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VacuityCheckUndecided = new(
        id: "VER106",
        title: "Z3 could not decide whether a method's premises can hold, so its proofs may be vacuous",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
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
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SpecTooComplex = new(
        id: "VER301",
        title: "Contract specification clause is overly complex",
        messageFormat: "{0}",
        category: SpecQualityCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingSpecExplanation = new(
        id: "VER302",
        title: "Contract specification is missing a plain-English 'because' explanation",
        messageFormat: "{0}",
        category: SpecQualityCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnwrittenBody = new(
        id: "VER108",
        title: "Method body has not been written yet (Spec.Hole)",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LockedContractChanged = new(
        id: "VER109",
        title: "Generated contract was changed",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InvalidContractLock = new(
        id: "VER110",
        title: "Contract lock file cannot be read",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InvalidMigration = new(
        id: "VER112",
        title: "Migration cannot be proved total",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MigrationDropsUnnamedField = new(
        id: "VER113",
        title: "Migration drops a field without naming it",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MigrationDropsListIsWrong = new(
        id: "VER114",
        title: "Migration Drops list names a field that is read or does not exist",
        messageFormat: "{0}",
        category: SmtCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ClauseNotImplemented = new(
        id: "VER401",
        title: "Spec clause has no code",
        messageFormat: "{0}",
        category: ClauseCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ConstructWithoutClause = new(
        id: "VER402",
        title: "Contract or rule names no spec clause",
        messageFormat: "{0}",
        category: ClauseCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor UnknownClause = new(
        id: "VER403",
        title: "Contract or rule names a clause the spec does not have",
        messageFormat: "{0}",
        category: ClauseCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InvalidClauseManifest = new(
        id: "VER404",
        title: "Clause manifest cannot be read",
        messageFormat: "{0}",
        category: ClauseCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static Diagnostic CreateScoped(
        DiagnosticDescriptor descriptor,
        Location location,
        bool isCandidate,
        ImmutableDictionary<string, string?>? properties = null,
        params object?[] args) =>
        CreateScopedWithContext(descriptor, location, isCandidate, properties, relatedLocation: null, relatedHeading: null, args);

    public static Diagnostic CreateScopedWithContext(
        DiagnosticDescriptor descriptor,
        Location location,
        bool isCandidate,
        ImmutableDictionary<string, string?>? properties,
        Location? relatedLocation,
        string? relatedHeading,
        params object?[] args)
    {
        var severity = isCandidate ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
        var warningLevel = isCandidate ? 1 : 0;
        var subject = args.Length > 0 ? args[0]?.ToString() ?? string.Empty : string.Empty;
        string? fixKind = null;
        properties?.TryGetValue("FixKind", out fixKind);

        var formatted = FormatGatekeeperOrFallback(
            descriptor.Id,
            location,
            subject,
            fixKind,
            relatedLocation,
            relatedHeading,
            args);

        var additionalLocations = relatedLocation is { } rel && rel != Location.None && rel.IsInSource
            ? new[] { rel }
            : null;

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
            additionalLocations: additionalLocations,
            customTags: descriptor.CustomTags,
            properties: properties);
    }

    private static string FormatGatekeeperOrFallback(
        string id,
        Location location,
        string subject,
        string? fixKind,
        Location? relatedLocation,
        string? relatedHeading,
        object?[] args)
    {
        return id switch
        {
            "VER001" => FormatVer001(location, subject, fixKind),
            "VER002" => FormatVer002(location, subject),
            "VER003" => FormatVer003(location, subject, fixKind),
            "VER004" => FormatVer004(location, subject, fixKind),
            "VER005" => FormatVer005(location, subject, fixKind),
            "VER006" => FormatVer006(location, subject, fixKind, relatedLocation, relatedHeading),
            _ => args.Length == 1 ? subject : string.Join(" ", args)
        };
    }

    private static string FormatVer001(Location location, string subject, string? fixKind)
    {
        var (intro, spirit, fix) = fixKind switch
        {
            "VER001_MutableType" => (
                $"Inside a `[Verifiable]` scope, `{subject}` is a mutable reference type.",
                "Two references to the same mutable class can alias each other, making it impossible to prove locally what changed across a method call.",
                "Convert it to a `readonly record struct` or immutable `sealed record` with `init`-only properties."),
            "VER001_MutableStruct" => (
                $"Inside a `[Verifiable]` scope, `{subject}` is not marked `readonly`.",
                "Non-readonly structs can mutate fields in place (or trigger defensive copies), breaking value-equality reasoning.",
                "Mark the declaration `readonly struct` or `readonly record struct` and make all fields `readonly`."),
            "VER001_SettableProperty" => (
                $"Inside a `[Verifiable]` scope, `{subject}` has a mutable `set;` accessor.",
                "Settable properties allow state to change after construction, invalidating previously proved invariants.",
                "Change `set;` to `init;` and produce updated copies with `record with { ... }` expressions."),
            "VER001_MutableField" => (
                $"Inside a `[Verifiable]` scope, `{subject}` can be reassigned after construction.",
                "Mutable fields break local reasoning because any method with a reference could silently alter them.",
                "Mark the field `readonly` (or convert it to an `init`-only property on a `readonly record struct`)."),
            "VER001_RefOutParam" => (
                $"Inside a `[Verifiable]` scope, `{subject}` passes a mutable alias by reference.",
                "`ref` and `out` parameters allow two names to point at the same storage location, breaking local value proofs.",
                "Return multiple outputs as a value tuple `(T1, T2)` or a `readonly record struct` instead of using `ref`/`out`."),
            "VER001_MemberAssignment" => (
                $"Inside a `[Verifiable]` scope, `{subject}` mutates state in place.",
                "In-place property or field mutation invalidates value snapshots held elsewhere in the method.",
                "Return a new value using a non-destructive `with` expression (for example, `state = state with { Prop = newValue };`)."),
            "VER001_RefLocal" => (
                $"Inside a `[Verifiable]` scope, `{subject}` creates a mutable alias to existing storage.",
                "`ref` locals let writes through one variable silently alter another variable in the same scope.",
                "Bind a regular value local instead of `ref`, and compute a new value via `with`."),
            _ => (
                $"Inside a `[Verifiable]` scope, `{subject}` allows aliasing or in-place mutation.",
                "Two references to the same mutable object make it impossible to prove locally what changed across a method call.",
                "Use a `readonly record struct` or immutable `record` with `with` expressions instead of mutable heap state or ref/out aliasing.")
        };

        return ElmDiagnosticFormatter.Format("MUTABLE STATE", location, intro, spirit: spirit, fixHint: fix);
    }

    private static string FormatVer002(Location location, string subject)
    {
        var replacement = SuggestImmutableCollection(subject);
        var fix = replacement is not null
            ? $"Replace `{subject}` with `{replacement}` (from `System.Collections.Immutable`)."
            : "Replace with `ImmutableList<T>`, `ImmutableDictionary<K, V>`, `ImmutableHashSet<T>`, or `ImmutableArray<T>`.";

        return ElmDiagnosticFormatter.Format(
            "MUTABLE COLLECTION",
            location,
            $"Mutable collection `{subject}` cannot cross or live inside a `[Verifiable]` cage.",
            spirit: "Mutable lists, dictionaries, and arrays can be mutated through another reference after a contract or invariant check passes.",
            fixHint: fix);
    }

    private static string FormatVer003(Location location, string subject, string? fixKind)
    {
        var (intro, fix) = fixKind switch
        {
            "VER003_Default" => (
                $"Using `default({subject})` bypasses `{subject}`'s `[TypeAlias<T>]` / `[Invariant]` validation.",
                $"Construct `{subject}` through `{subject}.From(...)`, `{subject}.TryFrom(...)`, or an `[InboundPort]` gate instead of `default`."),
            "VER003_NewConstrained" => (
                $"Using parameterless `new {subject}()` bypasses `{subject}`'s `[TypeAlias<T>]` / `[Invariant]` validation.",
                $"Construct `{subject}` through `{subject}.From(...)`, `{subject}.TryFrom(...)`, or an `[InboundPort]` gate."),
            "VER003_NewInvariant" => (
                $"Using parameterless `new {subject}()` leaves `{subject}` zero-initialized without checking its `[Invariant]` rules.",
                $"Call `{subject}`'s parameterized constructor (or factory / `[InboundPort]` gate) with valid field values."),
            _ => (
                $"Using `default` or `new` on `{subject}` skips its `[TypeAlias<T>]` / `[Invariant]` validation.",
                $"Construct `{subject}` using `{subject}.From(...)`, `{subject}.TryFrom(...)`, or an `[InboundPort]` gate.")
        };

        return ElmDiagnosticFormatter.Format(
            "UNCHECKED INITIALIZATION",
            location,
            intro,
            spirit: "Unchecked struct defaults can smuggle an impossible value (like an empty ID or negative quantity) inside a verified cage.",
            fixHint: fix);
    }

    private static string FormatVer004(Location location, string subject, string? fixKind)
    {
        var (intro, fix) = ClassifyAmbientAuthority(subject, fixKind);
        return ElmDiagnosticFormatter.Format(
            "AMBIENT AUTHORITY",
            location,
            intro,
            spirit: "Verified code must always return the same output for the same inputs so Z3 proofs and GalaxyCheck replays remain 100% reproducible.",
            fixHint: fix);
    }

    private static string FormatVer005(Location location, string subject, string? fixKind)
    {
        var (intro, spirit, fix) = fixKind switch
        {
            "VER005_Throw" => (
                $"Throwing an exception (`{subject}`) is not allowed inside a `[Verifiable]` cage.",
                "Exceptions act as invisible, unchecked exit paths that bypass normal return types and postcondition reasoning.",
                "Return `Result<T, TError>.Fail(...)` (or a discriminated union) instead of throwing, and let the imperative shell handle failures."),
            "VER005_TryCatch" => (
                $"A `try`/`catch` block is not allowed inside a `[Verifiable]` cage.",
                "Exception handlers introduce non-local control flow that hides failure paths from straight-line proof verification.",
                "Return `Result<T, TError>` from pure verified logic and keep `try`/`catch` at the outer `[Unverifiable]` I/O shell."),
            "VER005_AsyncTask" => (
                $"Asynchronous `{subject}` cannot live directly inside a `[Verifiable]` cage.",
                "Async state machines interleave I/O suspension points with computation, breaking deterministic local reasoning.",
                "Split the pure calculation into a synchronous `[Verifiable]` helper and keep `async`/`await` in an outer imperative shell (Sandwich I/O)."),
            "VER005_Await" => (
                $"An `await` expression cannot appear inside a `[Verifiable]` cage.",
                "Awaiting asynchronous I/O suspends execution and introduces external nondeterminism mid-computation.",
                "Await your inputs first in the outer shell, pass immutable snapshots into a synchronous `[Verifiable]` method, then await the output side effects."),
            "VER005_Lock" => (
                $"A `lock` statement is not allowed inside a `[Verifiable]` cage.",
                "Locks imply shared mutable state and thread-interleaving schedules that cannot be verified locally.",
                "Read an immutable snapshot at the boundary, run the `[Verifiable]` transition on that value, and commit outside the cage."),
            "VER005_LocalFunction" => (
                $"The local function `{subject}` is not allowed inside a `[Verifiable]` cage.",
                "The prover does not look inside local functions: their bodies, loops and contracts would go unchecked, and their writes to captured variables would be lost.",
                "Make it a `static` method of the class (with its own `Spec.Requires`/`Spec.Ensures`) and pass what it needs as parameters."),
            "VER005_Goto" => (
                $"A `goto` statement is not allowed inside a `[Verifiable]` cage.",
                "Unstructured jumps obscure loop structure and make path-condition / termination proofs fragile.",
                "Replace `goto` with structured `if`/`else`, `switch`, early `return`, or a small helper method."),
            "VER005_UnannotatedLoop" => (
                $"This `{subject}` has no `Spec.LoopInvariant(...)` or `Spec.Decreases(...)` annotation.",
                "Without a loop invariant and termination measure, Z3 cannot inductively prove what holds after an arbitrary number of iterations or that the loop terminates.",
                "Add `Spec.LoopInvariant(...)` and `Spec.Decreases(...)` inside the loop body, or replace with a bounded `for` loop."),
            _ => (
                $"Control flow `{subject}` is not allowed inside a `[Verifiable]` cage.",
                "Hidden exceptions, async state machines, and unannotated loops break straight-line mathematical reasoning.",
                "Use bounded loops or `Spec.LoopInvariant`/`Spec.Decreases`, avoid async/lock/goto, and return `Result<T, TError>` instead of throwing exceptions.")
        };

        return ElmDiagnosticFormatter.Format("DISALLOWED CONTROL FLOW", location, intro, spirit: spirit, fixHint: fix);
    }

    private static string FormatVer006(
        Location location,
        string subject,
        string? fixKind,
        Location? relatedLocation,
        string? relatedHeading)
    {
        var (intro, spirit, fix) = fixKind switch
        {
            "VER006_CultureSensitiveParse" => (
                $"Call `{subject}` is culture-sensitive or may throw on invalid input, so it cannot be called inside a `[Verifiable]` cage.",
                "Locale-dependent parsing behaves differently across machines and throws exceptions on malformed strings.",
                "Validate and parse untrusted strings at an `[InboundPort]` boundary (or with `TryParse(..., CultureInfo.InvariantCulture)` in an `[Unverifiable]` shell) before passing typed values into verified code."),
            "VER006_Reflection" => (
                $"Reflection call `{subject}` crosses out of the verified closed world.",
                "Reflection inspects and invokes arbitrary runtime metadata that Z3 cannot reason about statically.",
                "Replace reflection with static generics, pattern matching, or move metadata inspection into an `[Unverifiable]` boundary."),
            "VER006_Dynamic" => (
                "`dynamic` dispatch crosses out of the verified closed world.",
                "Runtime dynamic binding defers method resolution and type checks until execution, making static proof impossible.",
                "Use concrete static types or interfaces/records instead of `dynamic`."),
            "VER006_Delegate" => (
                "Invoking an opaque delegate crosses out of the verified closed world.",
                "Z3 cannot know what arbitrary side effects or return values an unanalyzed delegate might produce.",
                "Call a concrete `[Verifiable]` method directly, or dispatch over a closed union / enum with `switch`."),
            _ => (
                $"Call `{subject}` crosses out of the verified closed world.",
                "Z3 cannot prove invariants hold if a verified method calls unverified code whose behavior is unknown.",
                relatedLocation is { IsInSource: true }
                    ? $"Mark `{subject}` with `[Verifiable]` (if pure) or `[PureAxiom]` (if a trusted primitive), or move the I/O outside via Sandwich I/O splitting."
                    : "Mark the callee `[Verifiable]` or `[PureAxiom]`, or move the I/O outside via Sandwich I/O splitting.")
        };

        return ElmDiagnosticFormatter.Format(
            "UNVERIFIED CALL",
            location,
            intro,
            spirit: spirit,
            fixHint: fix,
            relatedLocation: relatedLocation,
            relatedHeading: relatedHeading);
    }

    internal static string? SuggestImmutableCollection(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var s = subject.Trim();

        if (s.EndsWith("[]", StringComparison.Ordinal) && s.Length > 2)
        {
            var elem = s.Substring(0, s.Length - 2);
            return $"ImmutableArray<{elem}>";
        }

        if (s.StartsWith("List<", StringComparison.Ordinal))
            return "ImmutableList" + s.Substring("List".Length);
        if (s.StartsWith("Dictionary<", StringComparison.Ordinal))
            return "ImmutableDictionary" + s.Substring("Dictionary".Length);
        if (s.StartsWith("HashSet<", StringComparison.Ordinal))
            return "ImmutableHashSet" + s.Substring("HashSet".Length);
        if (s.StartsWith("Queue<", StringComparison.Ordinal))
            return "ImmutableQueue" + s.Substring("Queue".Length);
        if (s.StartsWith("Stack<", StringComparison.Ordinal))
            return "ImmutableStack" + s.Substring("Stack".Length);
        if (s.StartsWith("SortedDictionary<", StringComparison.Ordinal))
            return "ImmutableSortedDictionary" + s.Substring("SortedDictionary".Length);
        if (s.StartsWith("SortedList<", StringComparison.Ordinal))
            return "ImmutableSortedDictionary" + s.Substring("SortedList".Length);
        if (s.StartsWith("SortedSet<", StringComparison.Ordinal))
            return "ImmutableSortedSet" + s.Substring("SortedSet".Length);
        if (s.StartsWith("LinkedList<", StringComparison.Ordinal))
            return "ImmutableList" + s.Substring("LinkedList".Length);

        return null;
    }

    private static (string Intro, string Fix) ClassifyAmbientAuthority(string subject, string? fixKind)
    {
        if (fixKind == "VER004_StaticField" || subject.Contains("static field"))
        {
            return (
                $"Mutable global state `{subject}` cannot live inside a `[Verifiable]` scope.",
                "Make the field `static readonly` / `const` if it is a constant, or pass the state explicitly as an immutable parameter.");
        }

        if (subject.IndexOf("DateTime", StringComparison.Ordinal) >= 0 ||
            subject.IndexOf("Stopwatch", StringComparison.Ordinal) >= 0 ||
            subject.IndexOf("Environment.TickCount", StringComparison.Ordinal) >= 0)
        {
            return (
                $"Clock read `{subject}` reads ambient wall-clock time inside a `[Verifiable]` scope.",
                "Hoist the timestamp into an explicit parameter such as `DateTimeOffset now` or `UnixTimestamp now` and pass it from the calling shell.");
        }

        if (subject.IndexOf("Guid.NewGuid", StringComparison.Ordinal) >= 0 ||
            subject.IndexOf("Guid.CreateVersion7", StringComparison.Ordinal) >= 0)
        {
            return (
                $"ID allocation `{subject}` generates a fresh nondeterministic value inside a `[Verifiable]` scope.",
                "Hoist the generated ID into an explicit parameter such as `Guid nextId` and allocate it once per item in the calling shell.");
        }

        if (subject.IndexOf("Random", StringComparison.Ordinal) >= 0)
        {
            return (
                $"Random source `{subject}` introduces nondeterminism inside a `[Verifiable]` scope.",
                "Sample the random value in the outer shell and pass it as an explicit deterministic parameter.");
        }

        if (subject.IndexOf("HttpClient", StringComparison.Ordinal) >= 0 ||
            subject.IndexOf("File", StringComparison.Ordinal) >= 0 ||
            subject.IndexOf("Console", StringComparison.Ordinal) >= 0 ||
            subject.IndexOf("Environment", StringComparison.Ordinal) >= 0)
        {
            return (
                $"External I/O or environment access `{subject}` cannot run inside a `[Verifiable]` scope.",
                "Read external I/O or environment values in an `[Unverifiable]` outer shell and pass immutable data into your `[Verifiable]` core (Sandwich I/O).");
        }

        return (
            $"Non-deterministic or ambient construct `{subject}` cannot be called inside a `[Verifiable]` scope.",
            "Hoist the time/ID/random value into an explicit parameter such as `DateTimeOffset now`, `UnixTimestamp now`, or `Guid nextId`.");
    }
}
