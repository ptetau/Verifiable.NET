using Microsoft.CodeAnalysis;

namespace Verifiable.Generators;

internal static class GeneratorDescriptors
{
    private const string Category = "Verifiable.Generators";

    public static readonly DiagnosticDescriptor RefinedMustBeReadOnlyPartialRecordStruct = new(
        id: "VER201",
        title: "Invalid constrained struct target declaration",
        messageFormat: "Type '{0}' decorated with [TypeAlias<T>] or [Refined<T>] must be declared as a 'readonly partial record struct'. Fix: Change the declaration to 'public readonly partial record struct {0} {{ public static bool IsValid(...) => ...; }}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingOrInvalidIsValidMethod = new(
        id: "VER202",
        title: "Missing or invalid IsValid method on constrained type",
        messageFormat: "Constrained type '{0}' must declare 'public static bool IsValid({1} value) => ...;' with a single boolean predicate expression. Fix: Add 'public static bool IsValid({1} value) => ...;' inside '{0}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
