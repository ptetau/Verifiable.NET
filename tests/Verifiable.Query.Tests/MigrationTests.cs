using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R8: a mapping from an old type to a new one is proved to cover every valid old value and
/// keep every new invariant; a lossy merge must name the fields it drops.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class MigrationTests
{
    private const string Types = """
        using Verifiable.Contracts;

        public sealed record OldStay(int Nights, int Guests, string Notes)
        {
            [Invariant("Stays are 1 to 30 nights.")]
            public bool ShortStay => Nights >= 1 && Nights <= 30;

            [Invariant("1 to 4 guests.")]
            public bool FewGuests => Guests >= 1 && Guests <= 4;
        }

        public sealed record OldGuest(string Name, int Age);

        public sealed record NewStay(int Nights, int Adults)
        {
            [Invariant("Stays are 1 to 14 nights.")]
            public bool ShortStay => Nights >= 1 && Nights <= 14;

            [Invariant("1 to 4 adults.")]
            public bool FewAdults => Adults >= 1 && Adults <= 4;
        }

        """;

    private static (int Code, JsonElement[] Diags) Check(string migrations)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "M.cs");
        File.WriteAllText(file, Types + migrations);
        using var o = new StringWriter();
        var code = Program.Run(["check", "--file", file, "--format", "json"], null, o, new StringWriter());
        var root = JsonDocument.Parse(o.ToString()).RootElement.Clone();
        return (code, root.GetProperty("diagnostics").EnumerateArray().ToArray());
    }

    private const string Head = "[Verifiable]\npublic static class Migrations\n{\n";

    [Fact]
    public void AMappingThatBreaksANewInvariant_FailsWithAnOldValueAsCounterexample()
    {
        var (code, diags) = Check(Head + """
                [Migration(Drops = new[] { "Notes" })]
                public static NewStay Migrate(OldStay old) => new NewStay(old.Nights, old.Guests);
            }
            """);
        Assert.Equal(1, code);
        var failure = Assert.Single(diags);
        Assert.Equal("VER102", failure.GetProperty("rule").GetString());
        Assert.Equal("TypeInvariant", failure.GetProperty("obligation").GetString());
        Assert.Contains("1 to 14 nights", failure.GetProperty("reason").GetString());
        Assert.Contains("OldStay", failure.GetProperty("counterexample").GetProperty("old").GetString());
    }

    [Fact]
    public void AMappingThatCoversEveryValidOldValue_IsProved()
    {
        var (code, diags) = Check(Head + """
                [Migration(Drops = new[] { "Notes" })]
                public static NewStay Migrate(OldStay old) => new NewStay(old.Nights > 14 ? 14 : old.Nights, old.Guests);
            }
            """);
        Assert.True(code == 0, string.Join("\n", diags.Select(d => d.GetRawText())));
    }

    [Fact]
    public void AMergeOfTwoTypes_NamesDroppedFieldsByParameter()
    {
        var ok = Check(Head + """
                [Migration(Drops = new[] { "stay.Notes", "guest.Name" })]
                public static NewStay Merge(OldStay stay, OldGuest guest) =>
                    new NewStay(stay.Nights > 14 ? 14 : stay.Nights, guest.Age >= 18 ? stay.Guests : 1);
            }
            """);
        Assert.True(ok.Code == 0, string.Join("\n", ok.Diags.Select(d => d.GetRawText())));

        var (_, diags) = Check(Head + """
                [Migration(Drops = new[] { "stay.Notes" })]
                public static NewStay Merge(OldStay stay, OldGuest guest) =>
                    new NewStay(stay.Nights > 14 ? 14 : stay.Nights, guest.Age >= 18 ? stay.Guests : 1);
            }
            """);
        var unnamed = Assert.Single(diags, d => d.GetProperty("rule").GetString() == "VER113");
        Assert.Contains("guest.Name", unnamed.GetProperty("summary").GetString());
    }

    [Fact]
    public void ALossyMapping_MustNameWhatItDrops()
    {
        var (code, diags) = Check(Head + """
                [Migration]
                public static NewStay Migrate(OldStay old) => new NewStay(old.Nights > 14 ? 14 : old.Nights, old.Guests);
            }
            """);
        Assert.Equal(1, code);
        var hit = Assert.Single(diags);
        Assert.Equal("VER113", hit.GetProperty("rule").GetString());
        Assert.Contains("`Notes`", hit.GetProperty("summary").GetString());
        Assert.Contains("Drops", hit.GetProperty("fix").GetString());
    }

    [Theory]
    [InlineData("\"Note\"", "no such field")]
    [InlineData("\"Notes\", \"Guests\"", "but it reads it")]
    public void ADropsListThatIsWrong_IsReported(string drops, string message)
    {
        var (_, diags) = Check(Head + $$"""
                [Migration(Drops = new[] { {{drops}} })]
                public static NewStay Migrate(OldStay old) => new NewStay(old.Nights > 14 ? 14 : old.Nights, old.Guests);
            }
            """);
        Assert.Contains(diags, d => d.GetProperty("rule").GetString() == "VER114" && d.GetProperty("summary").GetString()!.Contains(message));
    }

    [Theory]
    [InlineData("public static NewStay? Migrate(OldStay old) => null;", "nullable")]
    [InlineData("public static Result<NewStay, string> Migrate(OldStay old) => Result<NewStay, string>.Fail(\"no\");", "Result")]
    public void AMappingThatMayGiveNoResult_IsInvalid(string method, string message)
    {
        var (_, diags) = Check(Head + "    [Migration(Drops = new[] { \"Notes\", \"Nights\", \"Guests\" })]\n    " + method + "\n}\n");
        Assert.Contains(diags, d => d.GetProperty("rule").GetString() == "VER112" && d.GetProperty("summary").GetString()!.Contains(message));
    }

    [Fact]
    public void AMigrationOutsideVerifiedCode_IsInvalid()
    {
        var (_, diags) = Check("""
            public static class Loose
            {
                [Migration(Drops = new[] { "Notes" })]
                public static NewStay Migrate(OldStay old) => new NewStay(old.Nights, old.Guests);
            }
            """);
        Assert.Contains(diags, d => d.GetProperty("rule").GetString() == "VER112" && d.GetProperty("summary").GetString()!.Contains("[Verifiable]"));
    }
}
