using System;
using System.Collections.Generic;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// The Windows trust decision for the signed answer cache is pure, so it is tested here on every OS. SIDs are
/// written out as binary literals, so these tests do not share the code under test's SID encoding.
/// </summary>
public sealed class WindowsAclTests
{
    private const string User = "S-1-5-21-1-2-3-1001";
    private const string OtherUser = "S-1-5-21-1-2-3-1002";

    private const string UserHex = "010500000000000515000000010000000200000003000000E9030000";
    private const string OtherUserHex = "010500000000000515000000010000000200000003000000EA030000";
    private const string SystemHex = "010100000000000512000000";
    private const string AdministratorsHex = "01020000000000052000000020020000";
    private const string EveryoneHex = "010100000000000100000000";
    private const string AuthenticatedUsersHex = "01010000000000050B000000";
    private const string UsersHex = "01020000000000052000000021020000";

    private const byte Allow = 0;
    private const byte Deny = 1;
    private const byte CallbackAllow = 9;
    private const byte OiCi = 0x03;
    private const byte Inherited = 0x13; // object inherit | container inherit | inherited
    private const byte InheritOnly = 0x0B; // object inherit | container inherit | inherit only
    private const uint FullControl = 0x001F01FF;
    private const uint Read = 0x001200A9;

    // --- The decision, on lists of entries -------------------------------------------------------------

    [Fact]
    public void UserSystemAndAdministrators_AreOwnerOnly()
    {
        var dacl = new[]
        {
            new WindowsAce(Allow, OiCi, FullControl, User),
            new WindowsAce(Allow, OiCi, FullControl, WindowsAcl.LocalSystemSid),
            new WindowsAce(Allow, OiCi, FullControl, WindowsAcl.AdministratorsSid),
        };

        Assert.True(WindowsAcl.IsOwnerOnly(User, dacl, User));
        Assert.True(WindowsAcl.IsOwnerOnly(WindowsAcl.AdministratorsSid, dacl, User));
        Assert.True(WindowsAcl.IsOwnerOnly(WindowsAcl.LocalSystemSid, dacl, User));
    }

    [Fact]
    public void AnotherOwner_IsNotTrusted()
    {
        var dacl = new[] { new WindowsAce(Allow, OiCi, FullControl, User) };

        Assert.False(WindowsAcl.IsOwnerOnly(OtherUser, dacl, User));
        Assert.False(WindowsAcl.IsOwnerOnly("S-1-1-0", dacl, User));
        Assert.False(WindowsAcl.IsOwnerOnly(null, dacl, User));
    }

    [Theory]
    [InlineData("S-1-1-0")]       // Everyone
    [InlineData("S-1-5-11")]      // Authenticated Users
    [InlineData("S-1-5-32-545")]  // Users
    [InlineData("S-1-3-0")]       // CREATOR OWNER
    [InlineData(OtherUser)]
    public void AllowingAnyoneElse_IsNotOwnerOnly(string sid)
    {
        var dacl = new[]
        {
            new WindowsAce(Allow, OiCi, FullControl, User),
            new WindowsAce(Allow, 0, Read, sid),
        };

        Assert.False(WindowsAcl.IsOwnerOnly(User, dacl, User));
    }

    [Fact]
    public void InheritOnlyAndCallbackEntries_ForOthers_AreNotOwnerOnly()
    {
        Assert.False(WindowsAcl.IsOwnerOnly(User, new[] { new WindowsAce(Allow, InheritOnly, Read, "S-1-1-0") }, User));
        Assert.False(WindowsAcl.IsOwnerOnly(User, new[] { new WindowsAce(CallbackAllow, 0, Read, "S-1-1-0") }, User));
        Assert.True(WindowsAcl.IsOwnerOnly(User, new[] { new WindowsAce(CallbackAllow, 0, Read, User) }, User));
    }

    [Fact]
    public void DenyEntries_AndEntriesThatGrantNothing_AreIgnored()
    {
        var dacl = new[]
        {
            new WindowsAce(Deny, 0, FullControl, "S-1-1-0"),
            new WindowsAce(Allow, 0, 0, "S-1-1-0"),
            new WindowsAce(Allow, OiCi, FullControl, User),
        };

        Assert.True(WindowsAcl.IsOwnerOnly(User, dacl, User));
    }

    [Theory]
    [InlineData(4)]    // allowed compound
    [InlineData(5)]    // allowed object
    [InlineData(11)]   // allowed callback object
    [InlineData(2)]    // audit, which does not belong in a DACL
    [InlineData(0x42)] // unknown
    public void EntriesWhoseTrusteeIsNotRead_AreNotOwnerOnly(byte type)
    {
        var dacl = new[] { new WindowsAce(Allow, OiCi, FullControl, User), new WindowsAce(type, 0, 0, null) };

        Assert.False(WindowsAcl.IsOwnerOnly(User, dacl, User));
    }

    [Fact]
    public void NoDacl_GrantsEveryoneEverything_AndIsNotOwnerOnly()
    {
        Assert.False(WindowsAcl.IsOwnerOnly(User, null, User));
        Assert.True(WindowsAcl.IsOwnerOnly(User, Array.Empty<WindowsAce>(), User));
    }

    [Fact]
    public void UnknownCurrentUser_TrustsNothing()
    {
        Assert.False(WindowsAcl.IsOwnerOnly(User, new[] { new WindowsAce(Allow, OiCi, FullControl, User) }, ""));
    }

    // --- Parsing binary descriptors ---------------------------------------------------------------------

    [Fact]
    public void SidToBytes_MatchesTheWindowsLayout()
    {
        Assert.Equal(UserHex, Convert.ToHexString(WindowsAcl.SidToBytes(User)!));
        Assert.Equal(SystemHex, Convert.ToHexString(WindowsAcl.SidToBytes("S-1-5-18")!));
        Assert.Equal(AdministratorsHex, Convert.ToHexString(WindowsAcl.SidToBytes("S-1-5-32-544")!));
        Assert.Equal(EveryoneHex, Convert.ToHexString(WindowsAcl.SidToBytes("S-1-1-0")!));
        Assert.Null(WindowsAcl.SidToBytes("S-1"));
        Assert.Null(WindowsAcl.SidToBytes("X-1-5-18"));
        Assert.Null(WindowsAcl.SidToBytes("S-1-5-x"));
        Assert.Null(WindowsAcl.SidToBytes("S-1-5--18"));
    }

    [Fact]
    public void DefaultProfileAcl_IsOwnerOnly()
    {
        // What a folder under LocalApplicationData inherits: user, SYSTEM and Administrators, full control.
        var sd = Descriptor(UserHex, (Allow, Inherited, FullControl, SystemHex), (Allow, Inherited, FullControl, AdministratorsHex), (Allow, Inherited, FullControl, UserHex));

        Assert.True(WindowsAcl.TryParseDescriptor(sd, out var owner, out var dacl));
        Assert.Equal(User, owner);
        Assert.Equal(new string?[] { "S-1-5-18", "S-1-5-32-544", User }, dacl!.ConvertAll(a => a.Sid));
        Assert.All(dacl, a => Assert.Equal(FullControl, a.Mask));
        Assert.True(WindowsAcl.IsOwnerOnly(sd, User));
    }

    [Theory]
    [InlineData(EveryoneHex)]
    [InlineData(AuthenticatedUsersHex)]
    [InlineData(UsersHex)]
    [InlineData(OtherUserHex)]
    public void DescriptorGrantingAnotherSidRead_IsNotOwnerOnly(string sidHex)
    {
        var sd = Descriptor(UserHex, (Allow, Inherited, FullControl, UserHex), (Allow, 0, Read, sidHex));

        Assert.False(WindowsAcl.IsOwnerOnly(sd, User));
    }

    [Fact]
    public void DescriptorOwnedByAnotherUser_IsNotOwnerOnly()
    {
        Assert.False(WindowsAcl.IsOwnerOnly(Descriptor(OtherUserHex, (Allow, OiCi, FullControl, UserHex)), User));
        Assert.False(WindowsAcl.IsOwnerOnly(Descriptor(null, (Allow, OiCi, FullControl, UserHex)), User));
    }

    [Fact]
    public void DescriptorWithADenyForEveryone_IsOwnerOnly()
    {
        var sd = Descriptor(UserHex, (Deny, 0, FullControl, EveryoneHex), (Allow, OiCi, FullControl, UserHex));

        Assert.True(WindowsAcl.IsOwnerOnly(sd, User));
    }

    [Fact]
    public void DescriptorWithoutDacl_IsNotOwnerOnly()
    {
        var sd = Descriptor(UserHex);
        sd[2] &= unchecked((byte)~0x04); // clear "DACL present"

        Assert.True(WindowsAcl.TryParseDescriptor(sd, out _, out var dacl));
        Assert.Null(dacl);
        Assert.False(WindowsAcl.IsOwnerOnly(sd, User));
    }

    [Fact]
    public void OwnerOnlyDescriptor_IsExactlyTheExpectedBytes()
    {
        // Control 0x9004: self-relative, DACL protected, DACL present. No owner, group or SACL. One ACL of three
        // "allow, object and container inherit, full control" entries: user, SYSTEM, Administrators.
        var expected =
            "01000490" + "00000000" + "00000000" + "00000000" + "14000000"
            + "02005800" + "03000000"
            + "00032400" + "FF011F00" + UserHex
            + "00031400" + "FF011F00" + SystemHex
            + "00031800" + "FF011F00" + AdministratorsHex;

        Assert.Equal(expected, Convert.ToHexString(WindowsAcl.OwnerOnlyDescriptor(User)));
    }

    [Fact]
    public void OwnerOnlyDescriptor_GrantsNoOneElse()
    {
        var sd = WindowsAcl.OwnerOnlyDescriptor(User);

        Assert.True(WindowsAcl.TryParseDescriptor(sd, out var owner, out var dacl));
        Assert.Null(owner); // Windows fills in the creator's default owner
        Assert.Equal(new string?[] { User, "S-1-5-18", "S-1-5-32-544" }, dacl!.ConvertAll(a => a.Sid));

        // A folder created with it, owned by the user, is owner-only; for any other user it is not.
        Assert.True(WindowsAcl.IsOwnerOnly(User, dacl, User));
        Assert.False(WindowsAcl.IsOwnerOnly(User, dacl, OtherUser));
    }

    [Fact]
    public void OwnerOnlyDescriptor_RejectsANonSid()
    {
        Assert.Throws<ArgumentException>(() => WindowsAcl.OwnerOnlyDescriptor("Everyone"));
    }

    [Fact]
    public void TruncatedDescriptors_AreRejected()
    {
        var sd = Descriptor(UserHex, (Allow, Inherited, FullControl, UserHex), (Allow, Inherited, FullControl, SystemHex));
        for (var length = 0; length < sd.Length; length++)
        {
            Assert.False(WindowsAcl.IsOwnerOnly(sd.AsSpan(0, length).ToArray(), User));
        }
    }

    [Fact]
    public void CorruptedDescriptors_NeverThrow()
    {
        var original = Descriptor(UserHex, (Allow, Inherited, FullControl, UserHex), (Deny, 0, Read, EveryoneHex));
        var random = new Random(12345);
        for (var i = 0; i < 20000; i++)
        {
            var sd = (byte[])original.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                sd[random.Next(sd.Length)] = (byte)random.Next(256);
            }

            WindowsAcl.IsOwnerOnly(sd, User);
        }
    }

    [Fact]
    public void NonSelfRelativeDescriptors_AreRejected()
    {
        var sd = Descriptor(UserHex, (Allow, OiCi, FullControl, UserHex));
        sd[3] &= 0x7F; // clear "self-relative"

        Assert.False(WindowsAcl.TryParseDescriptor(sd, out _, out _));
    }

    /// <summary>Builds a self-relative descriptor: header, owner SID, then a DACL with the given entries.</summary>
    private static byte[] Descriptor(string? ownerHex, params (byte Type, byte Flags, uint Mask, string SidHex)[] aces)
    {
        var owner = ownerHex is null ? Array.Empty<byte>() : Convert.FromHexString(ownerHex);
        var acl = new List<byte>();
        foreach (var ace in aces)
        {
            var sid = Convert.FromHexString(ace.SidHex);
            acl.Add(ace.Type);
            acl.Add(ace.Flags);
            acl.AddRange(BitConverter.GetBytes((ushort)(8 + sid.Length)));
            acl.AddRange(BitConverter.GetBytes(ace.Mask));
            acl.AddRange(sid);
        }

        var sd = new List<byte> { 1, 0 };
        sd.AddRange(BitConverter.GetBytes((ushort)0x8404)); // self-relative, DACL auto-inherited, DACL present
        sd.AddRange(BitConverter.GetBytes(owner.Length == 0 ? 0 : 20));
        sd.AddRange(BitConverter.GetBytes(0));
        sd.AddRange(BitConverter.GetBytes(0));
        sd.AddRange(BitConverter.GetBytes(20 + owner.Length));
        sd.AddRange(owner);
        sd.Add(2);
        sd.Add(0);
        sd.AddRange(BitConverter.GetBytes((ushort)(8 + acl.Count)));
        sd.AddRange(BitConverter.GetBytes((ushort)aces.Length));
        sd.AddRange(BitConverter.GetBytes((ushort)0));
        sd.AddRange(acl);
        return sd.ToArray();
    }
}
