using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        if (OperatingSystem.IsWindows()) return;
        OpenCache();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "cache.key")));
    }

    [Fact]
    public void PreExistingOpenFolder_WithPlantedKey_IsNotTrusted()
    {
        if (OperatingSystem.IsWindows()) return;

        // An attacker who could write the folder plants a key they know and an Unsat signed with it.
        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, (UnixFileMode)0b111_111_111);
        var plantedKey = Enumerable.Repeat((byte)7, 32).ToArray();
        File.WriteAllBytes(Path.Combine(_dir, "cache.key"), plantedKey);
        using (var hmac = new System.Security.Cryptography.HMACSHA256(plantedKey))
        {
            var mac = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(HashA + "\nUnsat"))).ToLowerInvariant();
            File.WriteAllLines(Path.Combine(_dir, HashA + ".cache"), ["Unsat", "mac=" + mac]);
        }

        var cache = OpenCache();

        Assert.False(cache.TryLoad(HashA, out _));
        Assert.NotEqual(plantedKey, File.ReadAllBytes(Path.Combine(_dir, "cache.key")));
        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(_dir));
    }

    [Fact]
    public void PreExistingKey_ReadableByOthers_IsNotTrusted()
    {
        if (OperatingSystem.IsWindows()) return;
        OpenCache().Save(HashA, Answer(SmtStatus.Unsat));
        var keyPath = Path.Combine(_dir, "cache.key");
        var oldKey = File.ReadAllBytes(keyPath);
        File.SetUnixFileMode(keyPath, (UnixFileMode)0b110_100_100);

        var cache = OpenCache();

        Assert.False(cache.TryLoad(HashA, out _));
        Assert.NotEqual(oldKey, File.ReadAllBytes(keyPath));
    }

    [Fact]
    public void SymlinkedFolder_IsNotTrusted()
    {
        if (OperatingSystem.IsWindows()) return;
        var elsewhere = _dir + "-elsewhere";
        try
        {
            OpenCache(elsewhere).Save(HashA, Answer(SmtStatus.Unsat));
            Directory.CreateSymbolicLink(_dir, elsewhere);

            var cache = OpenCache();

            Assert.False(cache.TryLoad(HashA, out _));
            Assert.False((File.GetAttributes(_dir) & FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            try { Directory.Delete(elsewhere, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ConcurrentSaves_LeaveNoTemporaryFiles()
    {
        var cache = OpenCache();
        Parallel.For(0, 500, _ => cache.Save(HashA, Answer(SmtStatus.Unsat)));

        Assert.True(cache.TryLoad(HashA, out var loaded));
        Assert.Equal(SmtStatus.Unsat, loaded!.Status);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Z3Identity_FollowsSymlinks_AndChangesWhenTheTargetChanges()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "z3-real");
        var link = Path.Combine(_dir, "z3");
        File.WriteAllText(target, "version 1");
        File.CreateSymbolicLink(link, target);

        var before = Z3SolverRunner.Z3Identity(link);
        File.WriteAllText(target, "version 2, a longer binary");
        var after = Z3SolverRunner.Z3Identity(link);

        Assert.Contains(target, before);
        Assert.NotEqual(before, after);
    }
}
