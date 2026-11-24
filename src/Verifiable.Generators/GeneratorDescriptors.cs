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
}
