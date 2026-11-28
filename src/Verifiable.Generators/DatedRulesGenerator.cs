using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Verifiable.Generators;

/// <summary>
/// Requirement R6: rules and values that change on a date.
/// <list type="bullet">
/// <item>A value table — an additional file <c>Name.values.csv</c> with columns <c>from</c>, optional
/// <c>to</c> (both inclusive, <c>yyyy-MM-dd</c>) and <c>value</c>, and a first line
/// <c># type: decimal</c> — becomes <c>Name.For(DateOnly date)</c>. A gap or overlap between ranges is
/// VER204 / VER205, naming the dates.</item>
/// <item>Methods marked <c>[EffectiveFrom("yyyy-MM-dd", Rule = "Fee")]</c> in a static partial class are
/// versions of one rule, each proved on its own; a dispatcher <c>Fee(DateOnly asOf, ...)</c> calls the one
/// in force. Duplicate dates or differing signatures are VER207.</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DatedRulesGenerator : IIncrementalGenerator
{
    private const string TableSuffix = ".values.csv";
    private static readonly string[] SupportedTypes = ["int", "long", "decimal", "bool", "string"];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tables = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(TableSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => (Path: f.Path, Text: f.GetText(ct)?.ToString() ?? string.Empty));
        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(static (o, _) =>
            o.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) && !string.IsNullOrWhiteSpace(ns) ? ns : null);

        context.RegisterSourceOutput(tables.Combine(rootNamespace), static (spc, pair) => EmitTable(spc, pair.Left.Path, pair.Left.Text, pair.Right));

        var versions = context.SyntaxProvider.ForAttributeWithMetadataName(
                "Verifiable.Contracts.EffectiveFromAttribute",
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => (Method: (IMethodSymbol)ctx.TargetSymbol, Syntax: (MethodDeclarationSyntax)ctx.TargetNode))
            .Collect();
        context.RegisterSourceOutput(versions, static (spc, all) => EmitDispatchers(spc, all));
    }

    internal sealed record Row(DateTime From, DateTime? To, string Value, int Line);

    /// <summary>Parses a table; returns the rows sorted by <c>from</c>, or an error.</summary>
    internal static (string? Type, List<Row> Rows, string? Error) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? type = null;
        string[]? header = null;
        var rows = new List<Row>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                var c = line.Substring(1).Trim();
                if (c.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) type = c.Substring(5).Trim();
                continue;
            }
            var cells = line.Split(',').Select(x => x.Trim()).ToArray();
            if (header is null) { header = cells.Select(h => h.ToLowerInvariant()).ToArray(); continue; }

            int Col(string name) => Array.IndexOf(header, name);
            if (Col("from") < 0 || Col("value") < 0) return (type, rows, "the header needs `from` and `value` columns");
            string Cell(string name) => Col(name) >= 0 && Col(name) < cells.Length ? cells[Col(name)] : string.Empty;
            if (!DateTime.TryParseExact(Cell("from"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                return (type, rows, $"line {i + 1}: `{Cell("from")}` is not a yyyy-MM-dd date");
            DateTime? to = null;
            if (Cell("to").Length > 0)
            {
                if (!DateTime.TryParseExact(Cell("to"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    return (type, rows, $"line {i + 1}: `{Cell("to")}` is not a yyyy-MM-dd date");
                if (t < from) return (type, rows, $"line {i + 1}: `to` {Cell("to")} is before `from` {Cell("from")}");
                to = t;
            }
            rows.Add(new Row(from, to, Cell("value"), i + 1));
        }

        if (type is null) return (null, rows, "the first line must say the value type, e.g. `# type: decimal`");
        if (!SupportedTypes.Contains(type)) return (type, rows, $"type `{type}` is not supported; use {string.Join(", ", SupportedTypes)}");
        if (rows.Count == 0) return (type, rows, "the table has no rows");
        return (type, rows.OrderBy(r => r.From).ToList(), null);
    }

    /// <summary>Gaps and overlaps between consecutive ranges, as (isGap, message).</summary>
    internal static List<(bool Gap, string Message)> Problems(IReadOnlyList<Row> rows)
    {
        var problems = new List<(bool, string)>();
        for (var i = 0; i + 1 < rows.Count; i++)
        {
            var a = rows[i];
            var b = rows[i + 1];
            if (a.From == b.From) { problems.Add((false, $"Two rows start on {D(a.From)} (lines {a.Line} and {b.Line}).")); continue; }
            if (a.To is not { } end) continue; // open-ended rows run until the next row starts
            if (end >= b.From) problems.Add((false, $"The range {D(a.From)}..{D(end)} (line {a.Line}) overlaps the range starting {D(b.From)} (line {b.Line})."));
            else if (end.AddDays(1) < b.From) problems.Add((true, $"No value applies from {D(end.AddDays(1))} to {D(b.From.AddDays(-1))}, between line {a.Line} and line {b.Line}."));
        }
        return problems;
    }

    private static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Literal(string type, string value) => type switch
    {
        "decimal" => value + "m",
        "long" => value + "L",
        "string" => SymbolDisplay.FormatLiteral(value, quote: true),
        "bool" => value.ToLowerInvariant(),
        _ => value,
    };

    private static void EmitTable(SourceProductionContext spc, string path, string text, string? ns)
    {
        var name = System.IO.Path.GetFileName(path);
        name = name.Substring(0, name.Length - TableSuffix.Length);
        var location = Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(LinePosition.Zero, LinePosition.Zero));
        if (!SyntaxFacts.IsValidIdentifier(name))
        {
            spc.ReportDiagnostic(Diagnostic.Create(GeneratorDescriptors.InvalidValueTable, location, $"`{name}` is not a valid C# name for a value table."));
            return;
        }

        var (type, rows, error) = Parse(text);
        if (error is not null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(GeneratorDescriptors.InvalidValueTable, location, $"Value table `{name}`: {error}."));
            return;
        }

        var problems = Problems(rows);
        foreach (var (gap, message) in problems)
        {
            spc.ReportDiagnostic(Diagnostic.Create(gap ? GeneratorDescriptors.ValueTableGap : GeneratorDescriptors.ValueTableOverlap, location, $"Value table `{name}`: {message}"));
        }
        // The lookup is still generated, so code that uses it compiles and the gap or overlap above is
        // what fails the build.

        var first = rows[0];
        var last = rows[rows.Count - 1];
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> from " + System.IO.Path.GetFileName(path));
        sb.AppendLine("#nullable enable");
        if (ns is not null) sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>Values of {name} by date, from {System.IO.Path.GetFileName(path)}.</summary>");
        sb.AppendLine("[global::Verifiable.Contracts.Verifiable]");
        sb.AppendLine($"public static partial class {name}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>The <see cref=\"global::System.DateOnly.DayNumber\"/> of the first day the table covers ({D(first.From)}).</summary>");
        sb.AppendLine($"    public const int FirstDayNumber = {DayNumber(first.From)};");
        if (last.To is { } lastTo)
        {
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>The <see cref=\"global::System.DateOnly.DayNumber\"/> of the last day the table covers ({D(lastTo)}).</summary>");
            sb.AppendLine($"    public const int LastDayNumber = {DayNumber(lastTo)};");
        }
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>The value of {name} in force on <paramref name=\"date\"/>.</summary>");
        sb.AppendLine($"    public static {type} For(global::System.DateOnly date)");
        sb.AppendLine("    {");
        var lastDay = last.To;
        var cover = lastDay is null
            ? "date.DayNumber >= FirstDayNumber"
            : "date.DayNumber >= FirstDayNumber && date.DayNumber <= LastDayNumber";
        var coverText = lastDay is null ? $"on or after {D(first.From)}" : $"from {D(first.From)} to {D(lastDay.Value)}";
        sb.AppendLine($"        global::Verifiable.Contracts.Spec.Requires({cover}, \"The table covers dates {coverText}.\");");
        // One postcondition per row, so callers know the value for each range (proofs are modular).
        for (var i = 0; i < rows.Count; i++)
        {
            var lo = $"date.DayNumber >= {DayNumber(rows[i].From)}";
            var hi = i + 1 < rows.Count ? $" && date.DayNumber < {DayNumber(rows[i + 1].From)}" : string.Empty;
            var range = i + 1 < rows.Count ? $"{D(rows[i].From)} to {D(rows[i + 1].From.AddDays(-1))}" : $"from {D(rows[i].From)}";
            sb.AppendLine($"        global::Verifiable.Contracts.Spec.EnsuresWhen<{type}>({lo}{hi}, r => r == {Literal(type!, rows[i].Value)}, \"{name} is {rows[i].Value.Replace("\"", "'")} {range}.\");");
        }
        for (var i = rows.Count - 1; i >= 1; i--)
        {
            sb.AppendLine($"        if (date.DayNumber >= {DayNumber(rows[i].From)}) return {Literal(type!, rows[i].Value)}; // from {D(rows[i].From)}");
        }
        sb.AppendLine($"        return {Literal(type!, first.Value)}; // from {D(first.From)}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource($"{name}.ValueTable.g.cs", sb.ToString());
    }

    private static int DayNumber(DateTime d) => (int)(d.Date - DateTime.MinValue.Date).TotalDays;

    private static void EmitDispatchers(SourceProductionContext spc, ImmutableArray<(IMethodSymbol Method, MethodDeclarationSyntax Syntax)> all)
    {
        var groups = all
            .Select(v => (v.Method, v.Syntax, Attr: v.Method.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == "Verifiable.Contracts.EffectiveFromAttribute")))
            .GroupBy(v => (Type: v.Method.ContainingType, Rule: v.Attr.NamedArguments.FirstOrDefault(n => n.Key == "Rule").Value.Value as string ?? string.Empty), new GroupComparer());

        foreach (var g in groups)
        {
            var type = g.Key.Type;
            var rule = g.Key.Rule;
            var loc = g.First().Syntax.Identifier.GetLocation();
            void Fail(string message) => spc.ReportDiagnostic(Diagnostic.Create(GeneratorDescriptors.InvalidDatedRule, loc, message));

            if (rule.Length == 0 || !SyntaxFacts.IsValidIdentifier(rule)) { Fail($"[EffectiveFrom] on `{type.Name}.{g.First().Method.Name}` needs `Rule = \"Name\"`: the rule it is a version of."); continue; }
            var typeSyntax = g.First().Syntax.Parent as TypeDeclarationSyntax;
            if (!type.IsStatic || typeSyntax is null || !typeSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                Fail($"Versions of rule `{rule}` must be in a static partial class, so the dispatcher can be generated beside them.");
                continue;
            }

            var dated = new List<(DateTime Date, IMethodSymbol Method)>();
            var ok = true;
            foreach (var v in g)
            {
                var raw = v.Attr.ConstructorArguments.FirstOrDefault().Value as string;
                if (!DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    Fail($"`{type.Name}.{v.Method.Name}`: `{raw}` is not a yyyy-MM-dd date."); ok = false; continue;
                }
                dated.Add((date, v.Method));
            }
            if (!ok) continue;
            dated = dated.OrderBy(d => d.Date).ToList();

            var dup = dated.GroupBy(d => d.Date).FirstOrDefault(x => x.Count() > 1);
            if (dup is not null) { Fail($"Two versions of rule `{rule}` take effect on {D(dup.Key)}: {string.Join(" and ", dup.Select(d => d.Method.Name))}."); continue; }

            var sig = Signature(dated[0].Method);
            var odd = dated.FirstOrDefault(d => Signature(d.Method) != sig);
            if (odd.Method is not null) { Fail($"Versions of rule `{rule}` must share one signature: `{odd.Method.Name}` differs from `{dated[0].Method.Name}`."); continue; }

            var m0 = dated[0].Method;
            var ret = m0.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var parameters = string.Join(", ", m0.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}"));
            var args = string.Join(", ", m0.Parameters.Select(p => p.Name));
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            if (!type.ContainingNamespace.IsGlobalNamespace) sb.AppendLine($"namespace {type.ContainingNamespace.ToDisplayString()};");
            sb.AppendLine();
            sb.AppendLine($"static partial class {type.Name}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>Rule {rule} as in force on <paramref name=\"asOf\"/>: {string.Join("; ", dated.Select(d => $"{d.Method.Name} from {D(d.Date)}"))}.</summary>");
            sb.AppendLine($"    public static {ret} {rule}(global::System.DateOnly asOf{(parameters.Length > 0 ? ", " + parameters : string.Empty)})");
            sb.AppendLine("    {");
            for (var i = dated.Count - 1; i >= 1; i--)
            {
                sb.AppendLine($"        if (asOf.DayNumber >= {DayNumber(dated[i].Date)}) return {dated[i].Method.Name}({args}); // from {D(dated[i].Date)}");
            }
            sb.AppendLine($"        return {dated[0].Method.Name}({args}); // from {D(dated[0].Date)}");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            spc.AddSource($"{type.Name}.{rule}.Dated.g.cs", sb.ToString());
        }
    }

    private static string Signature(IMethodSymbol m) =>
        m.ReturnType.ToDisplayString() + "(" + string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString())) + ")";

    private sealed class GroupComparer : IEqualityComparer<(INamedTypeSymbol Type, string Rule)>
    {
        public bool Equals((INamedTypeSymbol Type, string Rule) x, (INamedTypeSymbol Type, string Rule) y) =>
            SymbolEqualityComparer.Default.Equals(x.Type, y.Type) && x.Rule == y.Rule;

        public int GetHashCode((INamedTypeSymbol Type, string Rule) obj) =>
            SymbolEqualityComparer.Default.GetHashCode(obj.Type) ^ obj.Rule.GetHashCode();
    }
}
