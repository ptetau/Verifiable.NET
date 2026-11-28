using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Verifiable.Query;

/// <summary>One row of input data: column name to raw text (null for a missing or SQL NULL value).</summary>
internal sealed record DataRow(int Number, IReadOnlyDictionary<string, string?> Values);

internal sealed record RuleFailures(string Rule, string Kind, string? Clause, string? Reason, int Count, IReadOnlyList<DataRow> Samples);

internal sealed record DataReport(string Type, int Rows, int FailedRows, IReadOnlyList<RuleFailures> Rules);

/// <summary>
/// Requirement R9: run the current <c>IsValid</c> rules and <c>[Invariant]</c>s over existing data, and
/// report how many rows fail which rule (with its clause), with sample rows. The project is compiled in
/// memory and its real rule code is run, so the check is exactly what the code enforces.
/// </summary>
internal static class DataCheck
{
    private const int SamplesPerRule = 5;

    public static List<DataRow> ReadCsv(string text)
    {
        var records = ParseCsv(text);
        if (records.Count == 0) return new List<DataRow>();
        var header = records[0];
        return records.Skip(1)
            .Where(r => !(r.Count == 1 && r[0].Length == 0))
            .Select((r, i) => new DataRow(i + 1, header.Select((h, c) => (h, v: c < r.Count ? r[c] : null))
                .ToDictionary(x => x.h, x => x.v, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>RFC 4180 CSV: quoted fields, doubled quotes, commas and newlines inside quotes.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>A JSON array of objects (or of plain values, read as column <c>value</c>).</summary>
    public static List<DataRow> ReadJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var array = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("rows", out var rows) ? rows
            : throw new FormatException("expected a JSON array of rows, or an object with a `rows` array");
        string? Raw(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => v.GetString(),
            _ => v.GetRawText(),
        };
        return array.EnumerateArray().Select((r, i) => new DataRow(i + 1,
            r.ValueKind == JsonValueKind.Object
                ? r.EnumerateObject().ToDictionary(p => p.Name, p => Raw(p.Value), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["value"] = Raw(r) }))
            .ToList();
    }

    public static List<DataRow> ReadSql(string connectionString, string query)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        using var reader = cmd.ExecuteReader();
        var rows = new List<DataRow>();
        while (reader.Read())
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < reader.FieldCount; c++)
            {
                values[reader.GetName(c)] = reader.IsDBNull(c) ? null : Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture);
            }
            rows.Add(new DataRow(rows.Count + 1, values));
        }
        return rows;
    }

    /// <summary>Compiles the project in memory and loads it, so its rules can run.</summary>
    public static (Assembly? Assembly, string? Error) Load(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            var first = emit.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            return (null, $"The project could not be compiled to run its rules: {first.Id} {first.GetMessage(CultureInfo.InvariantCulture)}");
        }
        stream.Position = 0;
        var context = new AssemblyLoadContext("verifiable-data-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        return (context.LoadFromStream(stream), null);
    }

    public static DataReport Check(Assembly assembly, string typeName, IReadOnlyList<DataRow> rows)
    {
        var type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName || t.FullName == typeName)
                   ?? throw new ArgumentException($"There is no type `{typeName}` in these sources.");

        var failures = new Dictionary<string, (string Kind, string? Clause, string? Reason, List<DataRow> Rows)>(StringComparer.Ordinal);
        void Fail(string rule, string kind, string? clause, string? reason, DataRow row)
        {
            if (!failures.TryGetValue(rule, out var f)) failures[rule] = f = (kind, clause, reason, new List<DataRow>());
            f.Rows.Add(row);
        }

        var failedRows = 0;
        foreach (var row in rows)
        {
            var before = failures.Values.Sum(f => f.Rows.Count);
            CheckRow(type, row, Fail);
            if (failures.Values.Sum(f => f.Rows.Count) > before) failedRows++;
        }

        var rules = failures
            .Select(kv => new RuleFailures(kv.Key, kv.Value.Kind, kv.Value.Clause, kv.Value.Reason, kv.Value.Rows.Select(r => r.Number).Distinct().Count(),
                kv.Value.Rows.GroupBy(r => r.Number).Select(g => g.First()).Take(SamplesPerRule).ToList()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Rule, StringComparer.Ordinal)
            .ToList();
        return new DataReport(type.Name, rows.Count, failedRows, rules);
    }

    private static (string? Reason, string? Clause) RuleInfo(MemberInfo member, string attributeName)
    {
        var attr = member.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name.StartsWith(attributeName, StringComparison.Ordinal));
        if (attr is null) return (null, null);
        var reason = attr.ConstructorArguments.FirstOrDefault().Value as string;
        var clause = attr.NamedArguments.FirstOrDefault(n => n.MemberName == "Clause").TypedValue.Value as string;
        return (reason, clause);
    }

    private static MethodInfo? IsValidOf(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "IsValid" && m.ReturnType == typeof(bool) && m.GetParameters().Length == 1);

    private static void CheckRow(Type type, DataRow row, Action<string, string, string?, string?, DataRow> fail)
    {
        var isValid = IsValidOf(type);
        if (isValid is not null)
        {
            // A constrained type: one value per row (the only column, or the one named `value`).
            var raw = row.Values.Count == 1 ? row.Values.Values.First() : row.Values.TryGetValue("value", out var v) ? v : null;
            CheckValue(type, isValid, raw, type.Name, row, fail);
            return;
        }

        // A record or class: build it from the columns that match its constructor, then check its invariants.
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) { fail($"{type.Name} (constructor)", "conversion", null, "The type has no public constructor.", row); return; }
        var args = new object?[ctor.GetParameters().Length];
        var ok = true;
        foreach (var (p, i) in ctor.GetParameters().Select((p, i) => (p, i)))
        {
            row.Values.TryGetValue(p.Name!, out var raw);
            var pValid = IsValidOf(p.ParameterType);
            if (pValid is not null)
            {
                if (!CheckValue(p.ParameterType, pValid, raw, $"{type.Name}.{p.Name}", row, fail, out var built)) { ok = false; continue; }
                args[i] = built;
            }
            else if (!TryConvert(raw, p.ParameterType, out args[i]))
            {
                fail($"{type.Name}.{p.Name} (conversion)", "conversion", null, $"`{raw ?? "null"}` is not a {p.ParameterType.Name}.", row);
                ok = false;
            }
        }
        if (!ok) return;

        object instance;
        try { instance = ctor.Invoke(args); }
        catch (TargetInvocationException ex) { fail($"{type.Name} (constructor)", "conversion", null, ex.InnerException?.Message, row); return; }

        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .Where(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "InvariantAttribute"))
                     .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var (reason, clause) = RuleInfo(member, "Invariant");
            bool holds;
            try
            {
                holds = member switch
                {
                    PropertyInfo prop => (bool)prop.GetValue(instance)!,
                    MethodInfo m when m.GetParameters().Length == 0 => (bool)m.Invoke(instance, null)!,
                    _ => true,
                };
            }
            catch (TargetInvocationException) { holds = false; }
            if (!holds) fail($"{type.Name}.{member.Name}", "invariant", clause, reason, row);
        }
    }

    private static bool CheckValue(Type type, MethodInfo isValid, string? raw, string ruleName, DataRow row, Action<string, string, string?, string?, DataRow> fail) =>
        CheckValue(type, isValid, raw, ruleName, row, fail, out _);

    private static bool CheckValue(Type type, MethodInfo isValid, string? raw, string ruleName, DataRow row, Action<string, string, string?, string?, DataRow> fail, out object? built)
    {
        built = null;
        var underlying = isValid.GetParameters()[0].ParameterType;
        var (reason, clause) = RuleInfo(type, "TypeAlias") is { Reason: not null } a ? a : RuleInfo(type, "Refined");
        if (!TryConvert(raw, underlying, out var value))
        {
            fail($"{ruleName} (conversion)", "conversion", clause, $"`{raw ?? "null"}` is not a {underlying.Name}.", row);
            return false;
        }
        if (!(bool)isValid.Invoke(null, new[] { value })!)
        {
            fail($"{type.Name}.IsValid", "isValid", clause, reason, row);
            return false;
        }
        var from = type.GetMethod("From", BindingFlags.Public | BindingFlags.Static, new[] { underlying });
        built = from?.Invoke(null, new[] { value });
        return true;
    }

    private static bool TryConvert(string? raw, Type target, out object? value)
    {
        value = null;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (raw is null) return !target.IsValueType || Nullable.GetUnderlyingType(target) is not null;
        try
        {
            if (t == typeof(string)) value = raw;
            else if (t == typeof(bool)) value = raw is "1" ? true : raw is "0" ? false : bool.Parse(raw);
            else if (t.IsEnum) value = Enum.Parse(t, raw, ignoreCase: true);
            else if (t == typeof(DateOnly)) value = DateOnly.Parse(raw, CultureInfo.InvariantCulture);
            else if (t == typeof(DateTime)) value = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            else if (t == typeof(Guid)) value = Guid.Parse(raw);
            else value = Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    public static string ToJson(DataReport report, bool indented)
    {
        var outcome = report.FailedRows > 0 ? AgentOutcome.Refuted : AgentOutcome.Proved;
        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("data"));
            w.WriteString("command", "data");
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteString("type", report.Type);
            w.WriteNumber("rows", report.Rows);
            w.WriteNumber("failedRows", report.FailedRows);
            w.WriteStartArray("rules");
            foreach (var r in report.Rules)
            {
                w.WriteStartObject();
                w.WriteString("rule", r.Rule);
                w.WriteString("kind", r.Kind);
                AgentOutput.StringOrNull(w, "clause", r.Clause);
                AgentOutput.StringOrNull(w, "reason", r.Reason);
                w.WriteNumber("failures", r.Count);
                w.WriteStartArray("samples");
                foreach (var s in r.Samples)
                {
                    w.WriteStartObject();
                    w.WriteNumber("row", s.Number);
                    w.WriteStartObject("values");
                    foreach (var kv in s.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)) AgentOutput.StringOrNull(w, kv.Key, kv.Value);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
