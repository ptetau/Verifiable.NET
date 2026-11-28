using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R12: JSON output follows the published, versioned schemas in <c>docs/schemas</c>,
/// with properties in the order the schema lists them.
/// </summary>
public sealed class SchemaConformanceTests
{
    private const string Source = """
        using Verifiable.Contracts;

        [TypeAlias<int>("Percentages run from 0 to 100.", Clause = "C1")]
        public readonly partial record struct Percent
        {
            public static bool IsValid(int value) => value >= 0 && value <= 100;
        }

        [Verifiable]
        public static class Pricing
        {
            public static int Discount(int price, int pct)
            {
                Spec.Requires(price >= 0 && price <= 10000 && pct >= 0 && pct <= 100, "Prices and percentages are in range.");
                Spec.Ensures<int>(r => r >= 0 && r < price, "A discounted price is never negative and always cheaper.");
                return price - price * pct / 100;
            }
        }
        """;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static JsonElement Schema(string name)
    {
        var path = Path.Combine(RepoRoot(), "docs", "schemas", $"{name}.v1.schema.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string Run(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        Program.Run(args, null, o, e);
        return o.ToString();
    }

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Pricing.cs");
        File.WriteAllText(path, Source);
        return path;
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return ["check", new[] { "check", "--file", "{file}", "--format", "json" }];
        yield return ["list", new[] { "list", "--file", "{file}", "--format", "json" }];
        yield return ["query", new[] { "ask", "--file", "{file}", "--target", "Pricing.Discount", "--assert", "result >= 0", "--format", "json" }];
        yield return ["query", new[] { "ask", "--file", "{file}", "--target", "Pricing.Discount", "--assert", "result > 0", "--format", "json" }];
        yield return ["query", new[] { "ask", "--file", "{file}", "--format", "json" }];
        yield return ["query", new[] { "check-invariant", "--file", "{file}", "--type", "Percent", "--expr", "p => true", "--format", "json" }];
        yield return ["tools", new[] { "tools", "status", "--format", "json" }];
        yield return ["clauses", new[] { "clauses", "--file", "{file}", "--format", "json" }];
        yield return ["tools", new[] { "tools", "status", "--z3", "{file}.missing", "--format", "json" }];
        yield return ["tools", new[] { "tools", "frob", "--format", "json" }];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Output_MatchesItsPublishedSchema(string schemaName, string[] args)
    {
        var file = TempFile();
        var json = Run(args.Select(a => a.Replace("{file}", file)).ToArray());
        using var doc = JsonDocument.Parse(json);
        var schema = Schema(schemaName);

        Assert.Equal($"verifiable-query/{schemaName}@1", doc.RootElement.GetProperty("schema").GetString());
        var errors = new List<string>();
        Validate(doc.RootElement, schema, "$", errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors) + "\n" + json);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("query")]
    [InlineData("list")]
    [InlineData("tools")]
    [InlineData("clauses")]
    public void Schemas_AreVersionedAndClosed(string name)
    {
        var schema = Schema(name);
        Assert.EndsWith($"{name}.v1.schema.json", schema.GetProperty("$id").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("schema", schema.GetProperty("properties").EnumerateObject().First().Name);
    }

    [Fact]
    public void Validator_RejectsWrongTypesMissingFieldsExtraFieldsAndReordering()
    {
        var schema = Schema("check");
        var good = JsonDocument.Parse(Run("check", "--file", TempFile(), "--format", "json")).RootElement;
        var props = good.EnumerateObject().ToList();

        string Rebuild(IEnumerable<JsonProperty> ps) =>
            "{" + string.Join(",", ps.Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}";

        var broken = new[]
        {
            Rebuild(props.Where(p => p.Name != "page")),                                   // missing
            Rebuild(props.Select(p => p)).Replace("\"exitCode\":1", "\"exitCode\":\"1\""), // wrong type
            Rebuild(props)[..^1] + ",\"extra\":true}",                                    // extra
            Rebuild(props.Skip(1).Prepend(props[0]).Reverse()),                           // reordered
            Rebuild(props).Replace("\"status\":\"refuted\"", "\"status\":\"failed\""),     // bad enum
        };

        foreach (var json in broken)
        {
            var errors = new List<string>();
            Validate(JsonDocument.Parse(json).RootElement, schema, "$", errors);
            Assert.NotEmpty(errors);
        }
    }

    /// <summary>A small validator for the subset of JSON Schema these files use.</summary>
    private static void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("enum", out var en))
        {
            if (!en.EnumerateArray().Any(x => x.GetRawText() == value.GetRawText()))
                errors.Add($"{path}: {value.GetRawText()} is not one of {en.GetRawText()}");
            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()!).ToList()
                : new List<string> { type.GetString()! };
            if (!allowed.Contains(KindOf(value)))
            {
                errors.Add($"{path}: expected {string.Join("|", allowed)}, got {KindOf(value)}");
                return;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var actual = value.EnumerateObject().Select(p => p.Name).ToList();
            if (schema.TryGetProperty("properties", out var props))
            {
                var declared = props.EnumerateObject().Select(p => p.Name).ToList();
                if (schema.TryGetProperty("required", out var req))
                {
                    foreach (var r in req.EnumerateArray())
                        if (!actual.Contains(r.GetString()!)) errors.Add($"{path}: missing required `{r.GetString()}`");
                }

                var expectedOrder = declared.Where(actual.Contains).ToList();
                var actualKnown = actual.Where(declared.Contains).ToList();
                if (!expectedOrder.SequenceEqual(actualKnown))
                    errors.Add($"{path}: property order {string.Join(",", actualKnown)} differs from schema {string.Join(",", expectedOrder)}");

                foreach (var p in value.EnumerateObject())
                {
                    if (props.TryGetProperty(p.Name, out var sub)) Validate(p.Value, sub, $"{path}.{p.Name}", errors);
                    else if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False)
                        errors.Add($"{path}: unexpected property `{p.Name}`");
                }
            }
            else if (schema.TryGetProperty("additionalProperties", out var apSchema) && apSchema.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in value.EnumerateObject()) Validate(p.Value, apSchema, $"{path}.{p.Name}", errors);
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) Validate(item, items, $"{path}[{i++}]", errors);
        }
    }

    private static string KindOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "integer",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };
}
