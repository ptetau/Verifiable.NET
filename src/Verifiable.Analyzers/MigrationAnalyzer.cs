using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

/// <summary>
/// Requirement R8: a <c>[Migration]</c> maps old types to a new one. The prover already shows that every
/// valid old value (old invariants are assumed) gives a value that keeps every new invariant, with a
/// counterexample when not (VER102). This analyzer makes sure the mapping is one the prover can judge —
/// static, verified, and total (VER112) — and that a lossy merge names what it drops: an old field the
/// mapping never reads must be listed in <c>Drops</c> (VER113), and <c>Drops</c> may only list fields that
/// exist and are not read (VER114).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MigrationAnalyzer : DiagnosticAnalyzer
{
    private const string MigrationAttr = "Verifiable.Contracts.MigrationAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        VerifiableDescriptors.InvalidMigration,
        VerifiableDescriptors.MigrationDropsUnnamedField,
        VerifiableDescriptors.MigrationDropsListIsWrong);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(Analyze);
    }

    private static void Analyze(OperationBlockAnalysisContext ctx)
    {
        if (ctx.OwningSymbol is not IMethodSymbol method) return;
        var attr = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MigrationAttr);
        if (attr is null) return;
        var loc = method.Locations.FirstOrDefault() ?? Location.None;
        var name = $"{method.ContainingType?.Name}.{method.Name}";

        void Invalid(string why) =>
            ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.InvalidMigration, loc,
                ImmutableDictionary<string, string?>.Empty.Add("Symbol", name),
                $"Migration `{name}` {why}"));

        if (!method.IsStatic) Invalid("must be static: a mapping from old values only.");
        if (method.Parameters.Length == 0) Invalid("must take the old value (or values, for a merge) as parameters.");
        if (method.ReturnsVoid) Invalid("must return the new value.");
        if (VerifiableSymbolHelpers.GetScope(method) != VerificationScope.Verifiable) Invalid("must be in [Verifiable] code, so the prover checks it.");
        var ret = method.ReturnType;
        if (ret.NullableAnnotation == NullableAnnotation.Annotated || ret.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            Invalid("must return a value for every old value, not a nullable one.");
        if (ret is INamedTypeSymbol { Name: "Result" or "MarshalResult" } r && r.ContainingNamespace?.ToDisplayString() == "Verifiable.Contracts")
            Invalid("must return a value for every old value; a Result lets some old values have no result.");

        var drops = attr.NamedArguments.FirstOrDefault(n => n.Key == "Drops").Value is { Kind: TypedConstantKind.Array } arr
            ? arr.Values.Select(v => v.Value as string).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList()
            : new List<string>();
        var single = method.Parameters.Length == 1;

        // The data fields of each old type: record parameters, fields and auto-properties, not rules.
        var fields = new Dictionary<string, (IParameterSymbol Param, IPropertySymbol Prop)>(StringComparer.Ordinal);
        foreach (var p in method.Parameters)
        {
            if (p.Type is not INamedTypeSymbol t || t.SpecialType != SpecialType.None || !t.Locations.Any(l => l.IsInSource)) continue;
            foreach (var prop in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsIndexer || prop.DeclaredAccessibility != Accessibility.Public || !IsData(prop)) continue;
                fields[$"{p.Name}.{prop.Name}"] = (p, prop);
            }
        }

        // What the body reads: `old.Field`, or the whole parameter (which counts as reading all of it).
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in ctx.OperationBlocks)
        {
            foreach (var op in block.DescendantsAndSelf())
            {
                switch (op)
                {
                    case IPropertyReferenceOperation { Instance: IParameterReferenceOperation pr } pref:
                        read.Add($"{pr.Parameter.Name}.{pref.Property.Name}");
                        break;
                    case IParameterReferenceOperation whole when op.Parent is not IPropertyReferenceOperation:
                        foreach (var key in fields.Keys.Where(k => k.StartsWith(whole.Parameter.Name + ".", StringComparison.Ordinal))) read.Add(key);
                        break;
                }
            }
        }

        string Key(string drop) => drop.Contains('.') ? drop : single ? $"{method.Parameters[0].Name}.{drop}" : drop;
        var dropKeys = drops.Select(d => (Raw: d, Key: Key(d))).ToList();

        foreach (var (raw, key) in dropKeys)
        {
            if (!fields.ContainsKey(key))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.MigrationDropsListIsWrong, loc,
                    $"Migration `{name}` lists `{raw}` in Drops, but there is no such field{(single ? string.Empty : "; name merged fields as `parameter.Field`")}."));
            }
            else if (read.Contains(key))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.MigrationDropsListIsWrong, loc,
                    $"Migration `{name}` lists `{raw}` in Drops, but it reads it. Remove it from Drops."));
            }
        }

        var named = new HashSet<string>(dropKeys.Select(d => d.Key), StringComparer.Ordinal);
        foreach (var key in fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (read.Contains(key) || named.Contains(key)) continue;
            var shown = single ? key.Substring(key.IndexOf('.') + 1) : key;
            ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.MigrationDropsUnnamedField, loc,
                ImmutableDictionary<string, string?>.Empty.Add("Symbol", name).Add("Fix", $"Map `{shown}` to the new type, or add it to Drops: [Migration(Drops = new[] {{ \"{shown}\" }})]."),
                $"Migration `{name}` drops `{shown}` without saying so. A lossy mapping must name every field it drops."));
        }
    }

    /// <summary>Stored data, not a computed rule: a record parameter, or an auto-property.</summary>
    private static bool IsData(IPropertySymbol prop)
    {
        if (prop.GetAttributes().Any(a => a.AttributeClass?.Name == "InvariantAttribute")) return false;
        var syntax = prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return syntax switch
        {
            ParameterSyntax => true,
            PropertyDeclarationSyntax p => p.ExpressionBody is null && (p.AccessorList?.Accessors.All(a => a.Body is null && a.ExpressionBody is null) ?? false),
            _ => false,
        };
    }
}
