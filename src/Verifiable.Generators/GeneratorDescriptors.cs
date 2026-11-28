using Microsoft.CodeAnalysis;

namespace Verifiable.Generators;

internal static class GeneratorDescriptors
{
    private const string Category = "Verifiable.Generators";

    public static readonly DiagnosticDescriptor RefinedMustBeReadOnlyPartialRecordStruct = new(
        id: "VER201",
        title: "Invalid constrained struct target declaration",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingOrInvalidIsValidMethod = new(
        id: "VER202",
        title: "Missing or invalid IsValid method on constrained type",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMapToDeclaration = new(
        id: "VER203",
        title: "Invalid [MapTo] projection declaration",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ValueTableGap = new(
        id: "VER204",
        title: "Dated value table leaves a gap between date ranges",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ValueTableOverlap = new(
        id: "VER205",
        title: "Dated value table has overlapping date ranges",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidValueTable = new(
        id: "VER206",
        title: "Dated value table cannot be read",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidDatedRule = new(
        id: "VER207",
        title: "Invalid dated rule versions",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
