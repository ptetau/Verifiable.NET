using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// On Windows the cache folder gets a protected ACL for the user (and SYSTEM and Administrators), and an
/// existing folder or key that anyone else can use is not trusted. These tests check that with the real
/// Windows ACL API, independently of the cache's own parser. They do nothing on other systems.
/// </summary>
public sealed class SignedAnswerCacheWindowsTests : IDisposable
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EveryoneSid = "S-1-1-0";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "verifiable-acl-test-" + Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(_dir, "cache.key");

    public void Dispose()
    {
        // Also removes the folders the cache moved aside.
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + "*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
        catch
        {
            // best effort
        }
    }

    private SignedAnswerCache OpenCache() =>
        SignedAnswerCache.Open(_dir) ?? throw new InvalidOperationException("cache did not open");

    private static SmtQueryResult Answer(SmtStatus status) =>
        new(status, HashA, new Dictionary<string, string> { ["x"] = "1" }, null);

    private string[] MovedAside() =>
        Directory.GetDirectories(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + ".untrusted-*");

    [Fact]
    public void AclApi_IsAvailable_AndFindsTheCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.True(WindowsFileAcl.IsAvailable);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, WindowsFileAcl.CurrentUserSid);
    }

    [Fact]
    public void FreshFolder_IsPrivateToTheUser()
    {
        if (!OperatingSystem.IsWindows()) return;

        OpenCache();

        var folder = new DirectoryInfo(_dir).GetAccessControl();
        Assert.True(folder.AreAccessRulesProtected);
        AssertPrivate(folder);
        AssertPrivate(new FileInfo(KeyPath).GetAccessControl());
        Assert.True(WindowsFileAcl.IsOwnerOnly(_dir));
        Assert.True(WindowsFileAcl.IsOwnerOnly(KeyPath));

        // The cache's own parser reads the same owner and entries as the Windows API.
        Assert.True(WindowsAcl.TryParseDescriptor(WindowsFileAcl.ReadDescriptor(_dir)!, out var owner, out var dacl));
        Assert.Equal(folder.GetOwner(typeof(SecurityIdentifier))!.Value, owner);
        Assert.Equal(
            RuleSids(folder).OrderBy(s => s, StringComparer.Ordinal),
            dacl!.Select(a => a.Sid!).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void PrivateFolder_IsKeptWhenReopened()
    {
        if (!OperatingSystem.IsWindows()) return;
        OpenCache().Save(HashA, Answer(SmtStatus.Unsat));
        var key = File.ReadAllBytes(KeyPath);

        var cache = OpenCache();

        Assert.True(cache.TryLoad(HashA, out _));
        Assert.Equal(key, File.ReadAllBytes(KeyPath));
        Assert.Empty(MovedAside());
    }

    [Fact]
    public void PreCreatedFolder_ThatGrantsEveryoneRead_IsReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Someone else made the folder and left a key in it; the folder lets Everyone read it.
        Directory.CreateDirectory(_dir);
        GrantEveryoneRead(new DirectoryInfo(_dir));
        var oldKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(KeyPath, oldKey);

        Assert.True(WindowsAcl.TryParseDescriptor(WindowsFileAcl.ReadDescriptor(_dir)!, out _, out var dacl));
        Assert.Contains(dacl!, a => a.Sid == EveryoneSid);
        Assert.False(WindowsFileAcl.IsOwnerOnly(_dir));

        OpenCache();

        Assert.NotEqual(oldKey, File.ReadAllBytes(KeyPath));
        Assert.True(new DirectoryInfo(_dir).GetAccessControl().AreAccessRulesProtected);
        AssertPrivate(new DirectoryInfo(_dir).GetAccessControl());
        Assert.Single(MovedAside());
    }

    [Fact]
    public void OwnFolder_LaterOpenedToEveryone_IsReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        OpenCache().Save(HashA, Answer(SmtStatus.Unsat));
        var oldKey = File.ReadAllBytes(KeyPath);
        GrantEveryoneRead(new DirectoryInfo(_dir));

        var cache = OpenCache();

        Assert.False(cache.TryLoad(HashA, out _));
        Assert.NotEqual(oldKey, File.ReadAllBytes(KeyPath));
        AssertPrivate(new DirectoryInfo(_dir).GetAccessControl());
    }

    [Fact]
    public void PlantedKey_InAFolderThatGrantsEveryoneRead_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        // An attacker plants a key they know and an Unsat signed with it.
        Directory.CreateDirectory(_dir);
        GrantEveryoneRead(new DirectoryInfo(_dir));
        var plantedKey = Enumerable.Repeat((byte)7, 32).ToArray();
        File.WriteAllBytes(KeyPath, plantedKey);
        var entry = Path.Combine(_dir, HashA + ".cache");
        var mac = Convert.ToHexString(HMACSHA256.HashData(plantedKey, Encoding.UTF8.GetBytes(HashA + "\nUnsat"))).ToLowerInvariant();
        File.WriteAllLines(entry, ["Unsat", "mac=" + mac]);

        var cache = OpenCache();

        Assert.False(cache.TryLoad(HashA, out _));
        Assert.NotEqual(plantedKey, File.ReadAllBytes(KeyPath));
        Assert.False(File.Exists(entry));
        AssertPrivate(new DirectoryInfo(_dir).GetAccessControl());
    }

    [Fact]
    public void KeyThatGrantsEveryoneRead_IsNotTrusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        OpenCache().Save(HashA, Answer(SmtStatus.Unsat));
        var oldKey = File.ReadAllBytes(KeyPath);
        var key = new FileInfo(KeyPath);
        var security = key.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));
        key.SetAccessControl(security);
        Assert.False(WindowsFileAcl.IsOwnerOnly(KeyPath));

        var cache = OpenCache();

        Assert.False(cache.TryLoad(HashA, out _));
        Assert.NotEqual(oldKey, File.ReadAllBytes(KeyPath));
        AssertPrivate(new FileInfo(KeyPath).GetAccessControl());
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Everyone => new(WellKnownSidType.WorldSid, null);

    /// <summary>Lets Everyone read the folder itself (not inherited by what is inside).</summary>
    [SupportedOSPlatform("windows")]
    private static void GrantEveryoneRead(DirectoryInfo dir)
    {
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    [SupportedOSPlatform("windows")]
    private static List<string> RuleSids(FileSystemSecurity security) =>
        Rules(security).ConvertAll(r => r.IdentityReference.Value);

    /// <summary>Checks, with the Windows API, that only the user, SYSTEM and Administrators own or can use the object.</summary>
    [SupportedOSPlatform("windows")]
    private static void AssertPrivate(FileSystemSecurity security)
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;
        var trusted = new[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        };

        Assert.Contains(security.GetOwner(typeof(SecurityIdentifier))!.Value, trusted);
        var rules = Rules(security);
        Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, trusted));
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == user
            && r.AccessControlType == AccessControlType.Allow
            && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }
}
