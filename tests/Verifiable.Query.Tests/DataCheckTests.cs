using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R9: run the current IsValid rules and invariants over existing data, and report failure
/// counts by rule and clause, with sample rows, from CSV, JSON or a SQL query.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class DataCheckTests
{
    private const string Domain = """
        using Verifiable.Contracts;

        [TypeAlias<int>("Stays are 1 to 30 nights.", Clause = "C1")]
        public readonly partial record struct Nights
        {
            public static bool IsValid(int value) => value >= 1 && value <= 30;
        }

        public enum Status { Open, Closed }

        public sealed record Booking(int Start, int End, Nights Nights, Status Status, string Guest)
        {
            [Invariant("A booking ends after it starts.", Clause = "C2")]
            public bool EndsAfterStart => End > Start;

            [Invariant("Guests have names.")]
            public bool HasGuest => Guest.Length > 0;
        }
        """;

    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Domain.cs"), Domain);
        return dir;
    }

    private static (int Code, JsonElement Root) Run(string dir, params string[] args)
    {
        using var o = new StringWriter();
        var code = Program.Run(new[] { "data", "--file", Path.Combine(dir, "Domain.cs") }.Concat(args).Append("--format").Append("json").ToArray(), null, o, new StringWriter());
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    private static JsonElement Rule(JsonElement root, string name) =>
        root.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("rule").GetString() == name);

    [Fact]
    public void Csv_CountsFailuresByRuleAndClauseWithSampleRows()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "rows.csv"),
            "Start,End,Nights,Status,Guest\n1,5,4,Open,Ann\n5,3,2,Open,Bo\n2,4,45,Closed,Cy\n9,1,3,open,\"Dee, Jr.\"\n3,4,2,Open,\n8,9,x,Open,Ed\n");
        var (code, root) = Run(dir, "--type", "Booking", "--rows", Path.Combine(dir, "rows.csv"));

        Assert.Equal(1, code);
        Assert.Equal("verifiable-query/data@1", root.GetProperty("schema").GetString());
        Assert.Equal(6, root.GetProperty("rows").GetInt32());
        Assert.Equal(5, root.GetProperty("failedRows").GetInt32());

        var ends = Rule(root, "Booking.EndsAfterStart");
        Assert.Equal("invariant", ends.GetProperty("kind").GetString());
        Assert.Equal("C2", ends.GetProperty("clause").GetString());
        Assert.Equal(2, ends.GetProperty("failures").GetInt32());
        Assert.Equal(new[] { 2, 4 }, ends.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("row").GetInt32()).ToArray());
        Assert.Equal("Dee, Jr.", ends.GetProperty("samples")[1].GetProperty("values").GetProperty("Guest").GetString());

        var nights = Rule(root, "Nights.IsValid");
        Assert.Equal("C1", nights.GetProperty("clause").GetString());
        Assert.Equal("Stays are 1 to 30 nights.", nights.GetProperty("reason").GetString());
        Assert.Equal(1, nights.GetProperty("failures").GetInt32());

        Assert.Equal(1, Rule(root, "Booking.HasGuest").GetProperty("failures").GetInt32());
        Assert.Equal("conversion", Rule(root, "Booking.Nights (conversion)").GetProperty("kind").GetString());
    }

    [Fact]
    public void Json_AndAConstrainedTypeOnItsOwn()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "nights.json"), "[1, 30, 31, 0, 12]");
        var (code, root) = Run(dir, "--type", "Nights", "--rows", Path.Combine(dir, "nights.json"));
        Assert.Equal(1, code);
        Assert.Equal(2, Rule(root, "Nights.IsValid").GetProperty("failures").GetInt32());

        File.WriteAllText(Path.Combine(dir, "ok.json"), "{\"rows\":[{\"Start\":1,\"End\":2,\"Nights\":1,\"Status\":\"Closed\",\"Guest\":\"Al\"}]}");
        var ok = Run(dir, "--type", "Booking", "--rows", Path.Combine(dir, "ok.json"));
        Assert.Equal(0, ok.Code);
        Assert.Equal("proved", ok.Root.GetProperty("status").GetString());
        Assert.Empty(ok.Root.GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public void SqlQuery_ReadsRowsFromSqlite()
    {
        var dir = Dir();
        var db = Path.Combine(dir, "b.db");
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "create table b(s int, e int, n int, st text, g text);" +
                              "insert into b values (1,5,4,'Open','A'),(9,2,3,'Open','B'),(1,2,99,'Closed','C');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var (code, root) = Run(dir, "--type", "Booking", "--sql", "select s as Start, e as End, n as Nights, st as Status, g as Guest from b",
            "--connection", $"Data Source={db}");
        Assert.Equal(1, code);
        Assert.Equal(3, root.GetProperty("rows").GetInt32());
        Assert.Equal(1, Rule(root, "Booking.EndsAfterStart").GetProperty("failures").GetInt32());
        Assert.Equal(1, Rule(root, "Nights.IsValid").GetProperty("failures").GetInt32());
    }

    [Fact]
    public void BadInputs_AreInputErrors()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "rows.csv"), "value\n1\n");
        Assert.Equal(3, Run(dir, "--rows", Path.Combine(dir, "rows.csv")).Code);
        Assert.Equal(3, Run(dir, "--type", "Booking").Code);
        Assert.Equal(3, Run(dir, "--type", "Nope", "--rows", Path.Combine(dir, "rows.csv")).Code);
        Assert.Equal(3, Run(dir, "--type", "Booking", "--rows", Path.Combine(dir, "missing.csv")).Code);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{not json");
        Assert.Equal(3, Run(dir, "--type", "Booking", "--rows", Path.Combine(dir, "bad.json")).Code);
    }
}
