using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R10: the domain returns effects as values, and the shell must handle every one. A
/// generator turns declared effects into a union type and a shell interface; an unhandled effect fails
/// the build.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class EffectsTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using Verifiable.Contracts;

        [Effects]
        public partial interface IBookingEffects
        {
            void SendConfirmation(string email, int bookingId);
            void ChargeCard(int bookingId, decimal amount);
        }

        [Verifiable]
        public static class Bookings
        {
            public static ImmutableList<BookingEffect> Confirm(int bookingId, string email, decimal amount)
            {
                Spec.Requires(bookingId > 0 && amount >= 0m && amount <= 100000m, "A valid booking.");
                return ImmutableList.Create(
                    BookingEffects.ChargeCard(bookingId, amount),
                    BookingEffects.SendConfirmation(email, bookingId));
            }
        }

        public sealed class Shell : IBookingEffects
        {
            public List<string> Log { get; } = new();
            public void SendConfirmation(string email, int bookingId) => Log.Add($"mail {email} #{bookingId}");
            public void ChargeCard(int bookingId, decimal amount) => Log.Add($"charge #{bookingId} {amount}");
        }
        """;

    private static string Write(string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-effects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Booking.cs");
        File.WriteAllText(file, source);
        return file;
    }

    private static (int Code, JsonElement[] Diags) Check(string source)
    {
        using var o = new StringWriter();
        var code = Program.Run(["check", "--file", Write(source), "--format", "json"], null, o, new StringWriter());
        return (code, JsonDocument.Parse(o.ToString()).RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.Clone()).ToArray());
    }

    [Fact]
    public void DomainCodeReturningEffects_IsProved()
    {
        var (code, diags) = Check(Source);
        Assert.True(code == 0, string.Join("\n", diags.Select(d => d.GetRawText())));
    }

    [Fact]
    public void AShellThatDoesNotHandleAnEffect_FailsTheBuild()
    {
        var (code, diags) = Check(Source.Replace("    public void ChargeCard(int bookingId, decimal amount) => Log.Add($\"charge #{bookingId} {amount}\");\n", string.Empty));
        Assert.Equal(3, code);
        Assert.Contains(diags, d => d.GetProperty("rule").GetString() == "CS0535" && d.GetProperty("summary").GetString()!.Contains("ChargeCard"));

        // Adding an effect to the declaration breaks every shell that does not handle it yet.
        var (added, addedDiags) = Check(Source.Replace("    void ChargeCard(int bookingId, decimal amount);", "    void ChargeCard(int bookingId, decimal amount);\n    void Refund(int bookingId);"));
        Assert.Equal(3, added);
        Assert.Contains(addedDiags, d => d.GetProperty("summary").GetString()!.Contains("Refund"));
    }

    [Fact]
    public void TheShellPerformsEveryEffectInOrder()
    {
        var loaded = WorkspaceLoader.Load(null, new[] { Write(Source) }, null);
        var (assembly, error) = DataCheck.Load(loaded.Compilation!);
        Assert.Null(error);

        var effects = assembly!.GetType("Bookings")!.GetMethod("Confirm")!.Invoke(null, [7, "a@b.c", 120m])!;
        var shell = Activator.CreateInstance(assembly.GetType("Shell")!)!;
        assembly.GetType("BookingEffectDispatch")!.GetMethod("HandleAll")!.Invoke(null, [shell, effects]);

        var log = ((IEnumerable)shell.GetType().GetProperty("Log")!.GetValue(shell)!).Cast<string>().ToArray();
        Assert.Equal(new[] { "charge #7 120", "mail a@b.c #7" }, log);

        var union = assembly.GetType("BookingEffect")!;
        Assert.True(union.IsAbstract);
        Assert.Equal(new[] { "ChargeCard", "SendConfirmation" }, union.GetNestedTypes().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(union.GetNestedTypes(), t => Assert.True(t.IsSealed));
        Assert.Empty(union.GetConstructors()); // closed: no public or protected constructor
    }

    [Theory]
    [InlineData("    void ChargeCard(int bookingId, decimal amount);", "    int ChargeCard(int bookingId, decimal amount);", "must return void")]
    [InlineData("public partial interface IBookingEffects", "public interface IBookingEffects", "must be partial")]
    [InlineData("    void ChargeCard(int bookingId, decimal amount);", "    void ChargeCard(int bookingId, decimal amount);\n    void ChargeCard(int bookingId);", "declared more than once")]
    public void BadDeclarations_AreReported(string from, string to, string message)
    {
        var (_, diags) = Check(Source.Replace(from, to));
        Assert.Contains(diags, d => d.GetProperty("rule").GetString() == "VER208" && d.GetProperty("summary").GetString()!.Contains(message));
    }
}
