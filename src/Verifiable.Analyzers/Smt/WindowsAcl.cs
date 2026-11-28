using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Verifiable.Analyzers.Smt;

/// <summary>One entry of a Windows access control list, as far as the cache's trust decision needs it.</summary>
internal readonly struct WindowsAce
{
    internal WindowsAce(byte type, byte flags, uint mask, string? sid)
    {
        Type = type;
        Flags = flags;
        Mask = mask;
        Sid = sid;
    }

    /// <summary>The ACE type byte (0 = allow, 1 = deny, 9 = callback allow, ...).</summary>
    internal byte Type { get; }

    /// <summary>The ACE flags byte (inheritance and audit flags).</summary>
    internal byte Flags { get; }

    /// <summary>The access mask. Zero for types whose layout is not read.</summary>
    internal uint Mask { get; }

    /// <summary>The trustee as an <c>S-1-...</c> string, or <c>null</c> for types whose layout is not read.</summary>
    internal string? Sid { get; }
}

/// <summary>
/// The pure part of the Windows permission check for the signed answer cache: it reads and builds
/// self-relative security descriptors and decides whether one is private to the current user. It does no
/// I/O, so it is tested on every OS; <see cref="WindowsFileAcl"/> fetches and applies the real descriptors.
/// </summary>
/// <remarks>
/// SYSTEM and Administrators are trusted next to the user. They can read any file anyway (backup and
/// take-ownership privileges), the default profile ACL already grants them full control, and an elevated
/// build creates files owned by Administrators. Trusting them therefore exposes nothing new.
/// </remarks>
internal static class WindowsAcl
{
    internal const string LocalSystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";

    internal const byte AccessAllowedAceType = 0;
    internal const byte AccessDeniedAceType = 1;
    internal const byte AccessAllowedCallbackAceType = 9;

    internal const byte ObjectInheritAce = 0x01;
    internal const byte ContainerInheritAce = 0x02;
    internal const byte InheritedAce = 0x10;

    internal const uint FileAllAccess = 0x001F01FF;

    private const ushort DaclPresent = 0x0004;
    private const ushort DaclProtected = 0x1000;
    private const ushort SelfRelative = 0x8000;
    private const int DescriptorHeaderLength = 20;
    private const int AclHeaderLength = 8;
    private const int AceHeaderLength = 4;

    /// <summary>
    /// Decides whether an object with this owner and DACL can be read or written only by the current user
    /// (and SYSTEM and Administrators).
    /// </summary>
    /// <remarks>
    /// A missing DACL grants everyone full access, so it fails. Deny entries only take access away and are
    /// skipped. Every allow entry must name a trusted SID; an allow entry whose trustee is not read here
    /// (object, compound or unknown types) fails, as does any other entry type we do not expect in a DACL.
    /// Inherit-only entries count too: they would open up files created in the folder later.
    /// </remarks>
    internal static bool IsOwnerOnly(string? owner, IReadOnlyList<WindowsAce>? dacl, string currentUser)
    {
        if (owner is null || dacl is null || string.IsNullOrEmpty(currentUser)) return false;
        if (!IsTrusted(owner, currentUser)) return false;

        foreach (var ace in dacl)
        {
            if (IsDenyType(ace.Type)) continue;
            if (ace.Type != AccessAllowedAceType && ace.Type != AccessAllowedCallbackAceType) return false;
            if (ace.Mask == 0) continue;
            if (ace.Sid is null || !IsTrusted(ace.Sid, currentUser)) return false;
        }

        return true;
    }

    /// <summary>Parses the descriptor, then applies <see cref="IsOwnerOnly(string?, IReadOnlyList{WindowsAce}?, string)"/>. Malformed input fails.</summary>
    internal static bool IsOwnerOnly(byte[] descriptor, string currentUser) =>
        TryParseDescriptor(descriptor, out var owner, out var dacl) && IsOwnerOnly(owner, dacl, currentUser);

    private static bool IsTrusted(string sid, string currentUser) =>
        string.Equals(sid, currentUser, StringComparison.OrdinalIgnoreCase)
        || sid == LocalSystemSid
        || sid == AdministratorsSid;

    // Access denied, denied object, denied callback, denied callback object.
    private static bool IsDenyType(byte type) => type is 1 or 6 or 10 or 12;

    /// <summary>
    /// Reads the owner and DACL of a self-relative security descriptor (the binary form Windows returns).
    /// <paramref name="dacl"/> is <c>null</c> when the descriptor has no DACL. Returns <c>false</c> if the
    /// bytes are malformed.
    /// </summary>
    internal static bool TryParseDescriptor(byte[] sd, out string? owner, out List<WindowsAce>? dacl)
    {
        owner = null;
        dacl = null;
        if (sd is null || sd.Length < DescriptorHeaderLength || sd[0] != 1) return false;

        var control = ReadUInt16(sd, 2);
        if ((control & SelfRelative) == 0) return false;

        var ownerOffset = ReadInt32(sd, 4);
        var daclOffset = ReadInt32(sd, 16);

        if (ownerOffset != 0)
        {
            if (!TryReadSid(sd, ownerOffset, sd.Length, out owner)) return false;
        }

        if ((control & DaclPresent) == 0 || daclOffset == 0) return true;
        if (daclOffset < 0 || daclOffset > sd.Length - AclHeaderLength) return false;

        var aclSize = ReadUInt16(sd, daclOffset + 2);
        var aceCount = ReadUInt16(sd, daclOffset + 4);
        var aclEnd = daclOffset + aclSize;
        if (aclSize < AclHeaderLength || aclEnd > sd.Length) return false;

        var entries = new List<WindowsAce>(aceCount);
        var at = daclOffset + AclHeaderLength;
        for (var i = 0; i < aceCount; i++)
        {
            if (at > aclEnd - AceHeaderLength) return false;
            var type = sd[at];
            var flags = sd[at + 1];
            var size = ReadUInt16(sd, at + 2);
            if (size < AceHeaderLength || at + size > aclEnd) return false;

            uint mask = 0;
            string? sid = null;
            if (type is AccessAllowedAceType or AccessDeniedAceType or AccessAllowedCallbackAceType)
            {
                // Mask, then the SID; a callback ACE has its condition after the SID.
                if (size < AceHeaderLength + 4) return false;
                mask = ReadUInt32(sd, at + 4);
                if (!TryReadSid(sd, at + 8, at + size, out sid)) return false;
            }

            entries.Add(new WindowsAce(type, flags, mask, sid));
            at += size;
        }

        dacl = entries;
        return true;
    }

    /// <summary>
    /// Builds a self-relative descriptor with a protected DACL (nothing inherited from the parent) that gives
    /// full control to the user, SYSTEM and Administrators, inherited by everything created inside. It sets
    /// no owner, so Windows uses the creating process's default owner.
    /// </summary>
    internal static byte[] OwnerOnlyDescriptor(string userSid)
    {
        var aces = new List<byte>();
        foreach (var sid in new[] { userSid, LocalSystemSid, AdministratorsSid })
        {
            var sidBytes = SidToBytes(sid) ?? throw new ArgumentException("Not a SID: " + sid, nameof(userSid));
            var size = AceHeaderLength + 4 + sidBytes.Length;
            aces.Add(AccessAllowedAceType);
            aces.Add(ObjectInheritAce | ContainerInheritAce);
            AddUInt16(aces, size);
            AddUInt32(aces, FileAllAccess);
            aces.AddRange(sidBytes);
        }

        var sd = new List<byte>(DescriptorHeaderLength + AclHeaderLength + aces.Count)
        {
            1, 0, // revision, padding
        };
        AddUInt16(sd, DaclPresent | DaclProtected | SelfRelative);
        AddUInt32(sd, 0); // owner
        AddUInt32(sd, 0); // group
        AddUInt32(sd, 0); // SACL
        AddUInt32(sd, DescriptorHeaderLength); // DACL

        sd.Add(2); // ACL revision
        sd.Add(0);
        AddUInt16(sd, AclHeaderLength + aces.Count);
        AddUInt16(sd, 3); // ACE count
        AddUInt16(sd, 0);
        sd.AddRange(aces);
        return sd.ToArray();
    }

    /// <summary>Converts <c>S-1-5-21-...</c> to its binary form, or returns <c>null</c> if it is not a SID.</summary>
    internal static byte[]? SidToBytes(string sid)
    {
        if (sid is null) return null;
        var parts = sid.Split('-');
        if (parts.Length < 3 || parts.Length > 3 + 15 || parts[0] != "S" || parts[1] != "1") return null;
        if (!ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var authority) || authority > 0xFFFFFFFFFFFF) return null;

        var bytes = new byte[8 + 4 * (parts.Length - 3)];
        bytes[0] = 1;
        bytes[1] = (byte)(parts.Length - 3);
        for (var i = 0; i < 6; i++)
        {
            bytes[2 + i] = (byte)(authority >> (8 * (5 - i))); // big-endian
        }

        for (var i = 3; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var sub)) return null;
            WriteUInt32(bytes, 8 + 4 * (i - 3), sub);
        }

        return bytes;
    }

    private static bool TryReadSid(byte[] buffer, int offset, int end, out string? sid)
    {
        sid = null;
        if (offset < 0 || offset > end - 8 || end > buffer.Length || buffer[offset] != 1) return false;

        var count = buffer[offset + 1];
        if (count > 15 || offset > end - (8 + 4 * count)) return false;

        ulong authority = 0;
        for (var i = 0; i < 6; i++)
        {
            authority = (authority << 8) | buffer[offset + 2 + i];
        }

        var text = new StringBuilder("S-1-").Append(authority.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < count; i++)
        {
            text.Append('-').Append(ReadUInt32(buffer, offset + 8 + 4 * i).ToString(CultureInfo.InvariantCulture));
        }

        sid = text.ToString();
        return true;
    }

    private static ushort ReadUInt16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

    private static uint ReadUInt32(byte[] b, int at) =>
        (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

    private static int ReadInt32(byte[] b, int at) => unchecked((int)ReadUInt32(b, at));

    private static void WriteUInt32(byte[] b, int at, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            b[at + i] = (byte)(value >> (8 * i));
        }
    }

    private static void AddUInt16(List<byte> b, int value)
    {
        b.Add((byte)value);
        b.Add((byte)(value >> 8));
    }

    private static void AddUInt32(List<byte> b, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            b.Add((byte)(value >> (8 * i)));
        }
    }
}
