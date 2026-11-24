using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>
/// Generates zero-allocation refinement types, JSON converters, and checked/unchecked constructors for <c>[Refined&lt;T&gt;]</c> structs.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RefinedTypeGenerator : IIncrementalGenerator
{
    private sealed record RefinedTarget(
        INamedTypeSymbol Symbol,
        ITypeSymbol UnderlyingType,
        string Predicate,
        Location DiagnosticLocation,
        bool IsValidDeclaration,
        IReadOnlyList<string> UsingDirectives);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeRefHelper.RefinedAttributeMetadataName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => Transform(ctx));

        context.RegisterSourceOutput(targets, static (spc, target) =>
        {
            if (target is null)
            {
                return;
            }

            Emit(spc, target);
        });
    }

    private static RefinedTarget? Transform(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var attr = context.Attributes.FirstOrDefault(a =>
            a.AttributeClass is { IsGenericType: true } ac &&
            ac.ConstructedFrom.ToDisplayString() == "Verifiable.Contracts.RefinedAttribute<TUnderlying>");

        if (attr?.AttributeClass is not { TypeArguments.Length: 1 } attrClass)
        {
            return null;
        }

        var underlyingType = attrClass.TypeArguments[0];
        var predicate = attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is string s
            ? s
            : string.Empty;

        var isReadOnlyPartialRecordStruct =
            symbol.IsRecord &&
            symbol.IsValueType &&
            symbol.IsReadOnly &&
            context.TargetNode is RecordDeclarationSyntax rds &&
            rds.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) &&
            rds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
            rds.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));

        var usings = CollectUsings(context.TargetNode.SyntaxTree);
        return new RefinedTarget(
            symbol,
            underlyingType,
            predicate,
            context.TargetNode.GetLocation(),
            isReadOnlyPartialRecordStruct,
            usings);
    }

    private static IReadOnlyList<string> CollectUsings(SyntaxTree tree)
    {
        var list = new List<string>();
        if (tree.GetRoot() is CompilationUnitSyntax cu)
        {
            foreach (var u in cu.Usings)
            {
                if (u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
                {
                    var ns = u.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(ns))
                    {
                        list.Add(ns);
                    }
                }
            }
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void Emit(SourceProductionContext spc, RefinedTarget target)
    {
        if (!target.IsValidDeclaration)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.RefinedMustBeReadOnlyPartialRecordStruct,
                target.DiagnosticLocation,
                target.Symbol.Name));
            return;
        }

        var rewritten = PredicateHelper.RewriteRefinementPredicate(target.Predicate, target.UnderlyingType);
        if (!rewritten.IsValid)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                GeneratorDescriptors.InvalidRefinementPredicate,
                target.DiagnosticLocation,
                target.Predicate,
                target.Symbol.Name,
                rewritten.ErrorMessage ?? "Invalid expression"));
            return;
        }

        var ns = TypeRefHelper.GetNamespace(target.Symbol);
        var typeName = target.Symbol.Name;
        var underlyingGlobal = TypeRefHelper.ToGlobalTypeName(target.UnderlyingType);
        var predicateEsc = TypeRefHelper.EscapeStringLiteral(rewritten.NormalizedOriginalExpression);
        var valuePredEsc = TypeRefHelper.EscapeStringLiteral(rewritten.ValueExpression);

        var usings = new HashSet<string>(target.UsingDirectives, StringComparer.Ordinal)
        {
            "System",
            "System.Collections.Generic",
            "System.Text.Json",
            "System.Text.Json.Serialization",
            "Verifiable.Contracts"
        };

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        foreach (var u in usings.OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append("using ").Append(u).AppendLine(";");
        }
        sb.AppendLine();

        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine();
            sb.AppendLine("{");
        }

        var enclosingTypes = GetEnclosingPartialDeclarations(target.Symbol);
        var indent = string.IsNullOrEmpty(ns) ? string.Empty : "    ";

        foreach (var enc in enclosingTypes)
        {
            sb.Append(indent).Append("partial ").Append(enc).AppendLine();
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        var accessibility = target.Symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Internal => "internal ",
            Accessibility.Private => "private ",
            Accessibility.Protected => "protected ",
            Accessibility.ProtectedOrInternal => "protected internal ",
            Accessibility.ProtectedAndInternal => "private protected ",
            _ => "public "
        };

        sb.Append(indent).AppendLine("[global::Verifiable.Contracts.Verifiable]");
        sb.Append(indent).Append("[global::Verifiable.Contracts.Invariant(").Append(predicateEsc).AppendLine(")]");
        sb.Append(indent).Append("[global::System.Text.Json.Serialization.JsonConverter(typeof(").Append(typeName).AppendLine("JsonConverter))]");
        sb.Append(indent).Append(accessibility).Append("readonly partial record struct ").Append(typeName)
            .Append('(').Append(underlyingGlobal).Append(" Value) : global::System.IComparable<").Append(typeName).AppendLine(">");
        sb.Append(indent).AppendLine("{");

        var memberIndent = indent + "    ";

        // CompareTo
        sb.Append(memberIndent).Append("public int CompareTo(").Append(typeName).AppendLine(" other) =>");
        sb.Append(memberIndent).Append("    global::System.Collections.Generic.Comparer<").Append(underlyingGlobal).AppendLine(">.Default.Compare(Value, other.Value);");
        sb.AppendLine();

        // IsValid
        sb.Append(memberIndent).Append("public static bool IsValid(").Append(underlyingGlobal).Append(" value) => ")
            .Append(rewritten.ValueExpression).AppendLine(";");
        sb.AppendLine();

        // From
        sb.Append(memberIndent).AppendLine("[global::Verifiable.Contracts.Verifiable]");
        sb.Append(memberIndent).AppendLine("[global::Verifiable.Contracts.PureAxiom]");
        sb.Append(memberIndent).Append("[global::Verifiable.Contracts.Requires(").Append(valuePredEsc).AppendLine(")]");
        sb.Append(memberIndent).AppendLine("[global::Verifiable.Contracts.Ensures(\"result.Value == value\")]");
        sb.Append(memberIndent).Append("public static ").Append(typeName).Append(" From(").Append(underlyingGlobal).AppendLine(" value)");
        sb.Append(memberIndent).AppendLine("{");
        sb.Append(memberIndent).AppendLine("    if (!IsValid(value))");
        sb.Append(memberIndent).AppendLine("    {");
        sb.Append(memberIndent).Append("        throw new global::System.ArgumentOutOfRangeException(nameof(value), value, ")
            .Append(TypeRefHelper.EscapeStringLiteral($"Invalid {typeName}: value violates '{rewritten.NormalizedOriginalExpression}'. Fix: Pass a value satisfying '{rewritten.NormalizedOriginalExpression}'."))
            .AppendLine(");");
        sb.Append(memberIndent).AppendLine("    }");
        sb.Append(memberIndent).Append("    return new ").Append(typeName).AppendLine("(value);");
        sb.Append(memberIndent).AppendLine("}");
        sb.AppendLine();

        // TryFrom (out bool)
        sb.Append(memberIndent).Append("public static bool TryFrom(").Append(underlyingGlobal).Append(" value, out ").Append(typeName).AppendLine(" result)");
        sb.Append(memberIndent).AppendLine("{");
        sb.Append(memberIndent).AppendLine("    if (IsValid(value))");
        sb.Append(memberIndent).AppendLine("    {");
        sb.Append(memberIndent).Append("        result = new ").Append(typeName).AppendLine("(value);");
        sb.Append(memberIndent).AppendLine("        return true;");
        sb.Append(memberIndent).AppendLine("    }");
        sb.Append(memberIndent).AppendLine("    result = default;");
        sb.Append(memberIndent).AppendLine("    return false;");
        sb.Append(memberIndent).AppendLine("}");
        sb.AppendLine();

        // TryFrom (MarshalResult)
        sb.Append(memberIndent).Append("public static global::Verifiable.Contracts.MarshalResult<").Append(typeName).Append("> TryFrom(")
            .Append(underlyingGlobal).AppendLine(" value) =>");
        sb.Append(memberIndent).Append("    TryMarshal(value, nameof(value));");
        sb.AppendLine();
        sb.AppendLine();

        // TryMarshal
        sb.Append(memberIndent).Append("public static global::Verifiable.Contracts.MarshalResult<").Append(typeName).Append("> TryMarshal(")
            .Append(underlyingGlobal).AppendLine(" value, string fieldName = \"value\")");
        sb.Append(memberIndent).AppendLine("{");
        sb.Append(memberIndent).AppendLine("    if (IsValid(value))");
        sb.Append(memberIndent).AppendLine("    {");
        sb.Append(memberIndent).Append("        return global::Verifiable.Contracts.MarshalResult<").Append(typeName).Append(">.Ok(new ").Append(typeName).AppendLine("(value));");
        sb.Append(memberIndent).AppendLine("    }");
        sb.Append(memberIndent).Append("    return global::Verifiable.Contracts.MarshalResult<").Append(typeName).AppendLine(">.Fail(");
        sb.Append(memberIndent).Append("        new global::Verifiable.Contracts.ContractViolation(fieldName, ").Append(predicateEsc).Append(", ")
            .Append("$\"Value '{value}' violates invariant '").Append(EscapeInterpolated(rewritten.NormalizedOriginalExpression)).Append("' for ").Append(typeName).AppendLine(".\"));");
        sb.Append(memberIndent).AppendLine("}");

        // Bounded unchecked accessor
        if (rewritten.UpperBoundExclusiveForZeroBasedInt is { } upperBound)
        {
            sb.AppendLine();
            sb.Append(memberIndent).Append("public const int UpperBoundExclusive = ").Append(upperBound).AppendLine(";");
            sb.AppendLine();
            sb.Append(memberIndent).AppendLine("[global::Verifiable.Contracts.Verifiable]");
            sb.Append(memberIndent).AppendLine("[global::Verifiable.Contracts.PureAxiom]");
            sb.Append(memberIndent).Append("[global::Verifiable.Contracts.Requires(\"buffer.Length >= ").Append(upperBound).AppendLine("\")]");
            sb.Append(memberIndent).AppendLine("public T ReadUnchecked<T>(global::System.ReadOnlySpan<T> buffer) =>");
            sb.Append(memberIndent).AppendLine("    global::System.Runtime.CompilerServices.Unsafe.Add(");
            sb.Append(memberIndent).AppendLine("        ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(buffer),");
            sb.Append(memberIndent).AppendLine("        Value);");
        }

        // JsonConverter
        sb.AppendLine();
        EmitJsonConverter(sb, memberIndent, typeName, target.UnderlyingType, underlyingGlobal, rewritten.NormalizedOriginalExpression);

        sb.Append(indent).AppendLine("}");

        for (var i = enclosingTypes.Count - 1; i >= 0; i--)
        {
            indent = indent.Substring(4);
            sb.Append(indent).AppendLine("}");
        }

        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine("}");
        }

        var hintName = TypeRefHelper.SanitizeHintName(
            string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}") + ".Refined.g.cs";
        spc.AddSource(hintName, sb.ToString());
    }

    private static void EmitJsonConverter(
        StringBuilder sb,
        string indent,
        string typeName,
        ITypeSymbol underlyingType,
        string underlyingGlobal,
        string predicate)
    {
        var interpPred = EscapeInterpolated(predicate);
        sb.Append(indent).Append("public sealed class ").Append(typeName).Append("JsonConverter : global::System.Text.Json.Serialization.JsonConverter<").Append(typeName).AppendLine(">");
        sb.Append(indent).AppendLine("{");
        var m = indent + "    ";

        // Read
        sb.Append(m).Append("public override ").Append(typeName).AppendLine(" Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)");
        sb.Append(m).AppendLine("{");
        sb.Append(m).Append("    var raw = global::System.Text.Json.JsonSerializer.Deserialize<").Append(underlyingGlobal).AppendLine(">(ref reader, options);");
        sb.Append(m).Append("    if (!").Append(typeName).AppendLine(".TryFrom(raw!, out var result))");
        sb.Append(m).AppendLine("    {");
        sb.Append(m).Append("        throw new global::System.Text.Json.JsonException($\"JSON value '{raw}' for ").Append(typeName)
            .Append(" violates '").Append(interpPred).Append("'. Fix: Provide a valid ").Append(typeName).AppendLine(".\");");
        sb.Append(m).AppendLine("    }");
        sb.Append(m).AppendLine("    return result;");
        sb.Append(m).AppendLine("}");
        sb.AppendLine();

        // Write
        sb.Append(m).Append("public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ").Append(typeName).AppendLine(" value, global::System.Text.Json.JsonSerializerOptions options) =>");
        sb.Append(m).Append("    global::System.Text.Json.JsonSerializer.Serialize(writer, value.Value, options);");
        sb.AppendLine();

        // ReadAsPropertyName / WriteAsPropertyName for dictionary keys
        var propParseExpr = GetPropertyNameParseExpression(underlyingType);
        if (propParseExpr is not null)
        {
            sb.AppendLine();
            sb.Append(m).Append("public override ").Append(typeName).AppendLine(" ReadAsPropertyName(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)");
            sb.Append(m).AppendLine("{");
            sb.Append(m).AppendLine("    var keyText = reader.GetString() ?? throw new global::System.Text.Json.JsonException(\"Null property name.\");");
            sb.Append(m).Append("    if (!(").Append(propParseExpr).Append(") || !").Append(typeName).AppendLine(".TryFrom(parsed, out var result))");
            sb.Append(m).AppendLine("    {");
            sb.Append(m).Append("        throw new global::System.Text.Json.JsonException($\"JSON property key '{keyText}' for ").Append(typeName)
                .Append(" violates '").Append(interpPred).Append("'. Fix: Provide a valid ").Append(typeName).AppendLine(" key.\");");
            sb.Append(m).AppendLine("    }");
            sb.Append(m).AppendLine("    return result;");
            sb.Append(m).AppendLine("}");
            sb.AppendLine();

            var propWriteExpr = GetPropertyNameWriteExpression(underlyingType);
            sb.Append(m).Append("public override void WriteAsPropertyName(global::System.Text.Json.Utf8JsonWriter writer, ").Append(typeName).AppendLine(" value, global::System.Text.Json.JsonSerializerOptions options) =>");
            sb.Append(m).Append("    writer.WritePropertyName(").Append(propWriteExpr).AppendLine(");");
        }

        sb.Append(indent).AppendLine("}");
    }

    private static string? GetPropertyNameParseExpression(ITypeSymbol underlyingType)
    {
        return underlyingType.SpecialType switch
        {
            SpecialType.System_String => "keyText is { } parsed",
            SpecialType.System_Int32 => "int.TryParse(keyText, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            SpecialType.System_Int64 => "long.TryParse(keyText, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            SpecialType.System_UInt32 => "uint.TryParse(keyText, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            SpecialType.System_UInt64 => "ulong.TryParse(keyText, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            SpecialType.System_Int16 => "short.TryParse(keyText, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            SpecialType.System_Byte => "byte.TryParse(keyText, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            SpecialType.System_Decimal => "decimal.TryParse(keyText, global::System.Globalization.NumberStyles.Number, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)",
            _ when underlyingType.ToDisplayString() == "System.Guid" => "global::System.Guid.TryParse(keyText, out var parsed)",
            _ => null
        };
    }

    private static string GetPropertyNameWriteExpression(ITypeSymbol underlyingType)
    {
        return underlyingType.SpecialType switch
        {
            SpecialType.System_String => "value.Value ?? string.Empty",
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_Decimal
                => "value.Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture)",
            _ when underlyingType.ToDisplayString() == "System.Guid" => "value.Value.ToString(\"D\")",
            _ => "value.Value?.ToString() ?? string.Empty"
        };
    }

    private static List<string> GetEnclosingPartialDeclarations(INamedTypeSymbol symbol)
    {
        var list = new List<string>();
        for (var cur = symbol.ContainingType; cur is not null; cur = cur.ContainingType)
        {
            var kind = cur.IsRecord
                ? (cur.IsValueType ? "record struct" : "record class")
                : (cur.IsValueType ? "struct" : "class");
            list.Add($"{kind} {cur.Name}");
        }
        list.Reverse();
        return list;
    }

    private static string EscapeInterpolated(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}");
}
