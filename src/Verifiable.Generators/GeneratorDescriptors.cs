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

    public static readonly DiagnosticDescriptor InvalidTypeAliasName = new(
        id: "VER203",
        title: "Invalid [TypeAlias<T>] type name",
        messageFormat: "Type alias name '{0}' in [TypeAlias<T>] is not a valid C# identifier. Fix: Provide a valid unqualified identifier such as 'TodoId'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateTypeAlias = new(
        id: "VER204",
        title: "Duplicate [TypeAlias<T>] declaration",
        messageFormat: "Type alias '{0}' is declared more than once. Fix: Remove the duplicate [TypeAlias<T>] attribute or use distinct names/namespaces.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
