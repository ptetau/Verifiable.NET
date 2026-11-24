using Microsoft.CodeAnalysis;

namespace Verifiable.Generators;

internal static class GeneratorDescriptors
{
    private const string Category = "Verifiable.Generators";

    public static readonly DiagnosticDescriptor RefinedMustBeReadOnlyPartialRecordStruct = new(
        id: "VER201",
        title: "Invalid [Refined<T>] target declaration",
        messageFormat: "Type '{0}' decorated with [Refined<T>] must be declared as a 'readonly partial record struct'. Fix: Change the declaration to 'public readonly partial record struct {0};'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidRefinementPredicate = new(
        id: "VER202",
        title: "Invalid refinement predicate expression",
        messageFormat: "Refinement predicate '{0}' on '{1}' is invalid: {2}. Fix: Provide a valid C# boolean expression referencing 'Value', for example [Refined<int>(\"Value >= 1\")].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
