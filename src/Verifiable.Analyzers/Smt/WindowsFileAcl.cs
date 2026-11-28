using System;
using System.IO;
using System.Reflection;

namespace Verifiable.Analyzers.Smt;

// Utility code for SignedAnswerCache: it fetches and applies Windows security descriptors. The decision
// whether a descriptor is private to the user is made by WindowsAcl, which is pure and tested on every OS.
#pragma warning disable RS1035

/// <summary>
/// Reads and sets Windows ACLs on the cache folder. The analyzer targets netstandard2.0, so the ACL types of
/// the runtime it runs on (.NET 8+) are reached through reflection. If any of them is missing,
/// <see cref="IsAvailable"/> is <c>false</c> and the cache is not used.
/// </summary>
internal static class WindowsFileAcl
{
    private const int AccessAndOwnerSections = 2 | 4; // AccessControlSections.Access | AccessControlSections.Owner

    private static readonly Type? AclExtensionsType =
        Type.GetType("System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl", throwOnError: false);
    private static readonly Type? DirectorySecurityType =
        Type.GetType("System.Security.AccessControl.DirectorySecurity, System.IO.FileSystem.AccessControl", throwOnError: false);
    private static readonly Type? SectionsType =
        Type.GetType("System.Security.AccessControl.AccessControlSections, System.Security.AccessControl", throwOnError: false);
    private static readonly Type? WindowsIdentityType =
        Type.GetType("System.Security.Principal.WindowsIdentity, System.Security.Principal.Windows", throwOnError: false);

    private static readonly MethodInfo? CreateDirectoryMethod =
        DirectorySecurityType is null ? null : AclExtensionsType?.GetMethod("Create", new[] { typeof(DirectoryInfo), DirectorySecurityType });
    private static readonly MethodInfo? GetDirectoryAclMethod =
        SectionsType is null ? null : AclExtensionsType?.GetMethod("GetAccessControl", new[] { typeof(DirectoryInfo), SectionsType });
    private static readonly MethodInfo? GetFileAclMethod =
        SectionsType is null ? null : AclExtensionsType?.GetMethod("GetAccessControl", new[] { typeof(FileInfo), SectionsType });
    private static readonly MethodInfo? SetBinaryFormMethod =
        DirectorySecurityType?.GetMethod("SetSecurityDescriptorBinaryForm", new[] { typeof(byte[]) });

    private static readonly Lazy<string?> CurrentUser = new(ReadCurrentUserSid);

    /// <summary>The current user's SID (<c>WindowsIdentity.GetCurrent().User</c>), or <c>null</c> if it cannot be read.</summary>
    internal static string? CurrentUserSid => CurrentUser.Value;

    /// <summary>Whether every ACL member this class needs exists on this runtime and the user's SID is known.</summary>
    internal static bool IsAvailable =>
        CreateDirectoryMethod is not null && GetDirectoryAclMethod is not null && GetFileAclMethod is not null
        && SetBinaryFormMethod is not null && SectionsType is not null
        && CurrentUserSid is not null;

    /// <summary>
    /// Creates <paramref name="path"/> with a protected ACL that only the user, SYSTEM and Administrators can
    /// use. The ACL is part of the create call, so the folder is never open, even for a moment. Throws if that
    /// cannot be done or the result is not owner-only (for example because the folder already existed).
    /// </summary>
    internal static void CreateOwnerOnlyDirectory(string path)
    {
        if (!IsAvailable) throw new IOException("This runtime cannot set Windows ACLs.");

        var security = Activator.CreateInstance(DirectorySecurityType!);
        SetBinaryFormMethod!.Invoke(security, new object[] { WindowsAcl.OwnerOnlyDescriptor(CurrentUserSid!) });
        CreateDirectoryMethod!.Invoke(null, new[] { new DirectoryInfo(path), security });

        if (!IsOwnerOnly(path)) throw new IOException("The new folder is not private to the user.");
    }

    /// <summary>Whether the owner and ACL of a file or folder keep everyone but the user (and SYSTEM and Administrators) out.</summary>
    internal static bool IsOwnerOnly(string path)
    {
        try
        {
            var user = CurrentUserSid;
            var descriptor = ReadDescriptor(path);
            return user is not null && descriptor is not null && WindowsAcl.IsOwnerOnly(descriptor, user);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the owner and DACL of a file or folder as a self-relative security descriptor.</summary>
    internal static byte[]? ReadDescriptor(string path)
    {
        if (SectionsType is null) return null;

        var isDirectory = Directory.Exists(path);
        var get = isDirectory ? GetDirectoryAclMethod : GetFileAclMethod;
        if (get is null) return null;

        object info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        var security = get.Invoke(null, new[] { info, Enum.ToObject(SectionsType, AccessAndOwnerSections) });
        return security?.GetType().GetMethod("GetSecurityDescriptorBinaryForm", Type.EmptyTypes)?.Invoke(security, null) as byte[];
    }

    private static string? ReadCurrentUserSid()
    {
        try
        {
            var identity = WindowsIdentityType?.GetMethod("GetCurrent", Type.EmptyTypes)?.Invoke(null, null);
            try
            {
                var sid = identity?.GetType().GetProperty("User")?.GetValue(identity);
                return sid?.GetType().GetProperty("Value")?.GetValue(sid) as string;
            }
            finally
            {
                (identity as IDisposable)?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }
}
