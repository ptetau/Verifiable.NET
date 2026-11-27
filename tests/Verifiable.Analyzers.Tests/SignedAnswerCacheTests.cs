using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// A disk-cache hit replaces a Z3 call, so a forged entry would be a forged proof. These tests forge,
/// tamper with and transplant entries and check that none of them is accepted.
/// </summary>
public sealed class SignedAnswerCacheTests : IDisposable
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "verifiable-cache-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SignedAnswerCache OpenCache(string? dir = null) =>
        SignedAnswerCache.Open(dir ?? _dir) ?? throw new InvalidOperationException("cache did not open");

    private static SmtQueryResult Answer(SmtStatus status) =>
        new(status, HashA, new Dictionary<string, string> { ["x"] = "1" }, null);

    [Fact]
    public void SavedAnswers_RoundTrip()
    {
        var cache = OpenCache();
        cache.Save(HashA, Answer(SmtStatus.Sat));

        Assert.True(cache.TryLoad(HashA, out var loaded));
        Assert.Equal(SmtStatus.Sat, loaded!.Status);
        Assert.Equal("1", loaded.Model["x"]);
    }

    [Fact]
    public void UndecidedAnswers_AreNeverSaved()
    {
        var cache = OpenCache();
        cache.Save(HashA, Answer(SmtStatus.Unknown));
        cache.Save(HashB, Answer(SmtStatus.Error));

        Assert.False(File.Exists(cache.EntryPath(HashA)));
        Assert.False(File.Exists(cache.EntryPath(HashB)));
    }

    [Fact]
    public void PlantedUnsignedUnsat_IsRejected()
    {
        var cache = OpenCache();
        File.WriteAllLines(cache.EntryPath(HashA)!, ["Unsat"]);

        Assert.False(cache.TryLoad(HashA, out _));
    }

    [Fact]
    public void FlippingSatToUnsat_BreaksTheSignature()
    {
        var cache = OpenCache();
        cache.Save(HashA, Answer(SmtStatus.Sat));
        var path = cache.EntryPath(HashA)!;
        var lines = File.ReadAllLines(path);
        lines[0] = "Unsat";
        File.WriteAllLines(path, lines);

        Assert.False(cache.TryLoad(HashA, out _));
    }

    [Fact]
    public void ValidEntry_CopiedOntoAnotherQuery_IsRejected()
    {
        var cache = OpenCache();
        cache.Save(HashA, Answer(SmtStatus.Unsat));
        File.Copy(cache.EntryPath(HashA)!, cache.EntryPath(HashB)!);

        Assert.True(cache.TryLoad(HashA, out _));
        Assert.False(cache.TryLoad(HashB, out _));
    }

    [Fact]
    public void EntrySignedWithAnotherKey_IsRejected()
    {
        var otherDir = _dir + "-other";
        try
        {
            var other = OpenCache(otherDir);
            other.Save(HashA, Answer(SmtStatus.Unsat));

            var cache = OpenCache();
            File.Copy(other.EntryPath(HashA)!, cache.EntryPath(HashA)!);

            Assert.False(cache.TryLoad(HashA, out _));
        }
        finally
        {
            try { Directory.Delete(otherDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void CorrectlySignedUndecidedAnswer_IsStillRejectedByTheKernel()
    {
        var cache = OpenCache();
        var body = new[] { "Unknown" };
        File.WriteAllLines(cache.EntryPath(HashA)!, [body[0], "mac=" + cache.Sign(HashA, body)]);

        Assert.False(cache.TryLoad(HashA, out _));
    }

    [Fact]
    public void MalformedHashes_CannotEscapeTheFolder()
    {
        var cache = OpenCache();
        Assert.Null(cache.EntryPath("../../etc/passwd"));
        Assert.Null(cache.EntryPath(HashA.ToUpperInvariant()));
    }

    [Fact]
    public void Folder_IsPrivateToTheUser()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        OpenCache();

        Assert.Equal("700", Mode(_dir));
        Assert.Equal("600", Mode(Path.Combine(_dir, "cache.key")));
    }

    private static string Mode(string path)
    {
        using var stat = Process.Start(new ProcessStartInfo("stat", $"-c %a \"{path}\"") { RedirectStandardOutput = true })!;
        var output = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        return output;
    }
}
