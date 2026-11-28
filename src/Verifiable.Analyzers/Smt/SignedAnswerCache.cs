using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Verifiable.Core;

namespace Verifiable.Analyzers.Smt;

// The disk cache is utility code, but a cache hit replaces a Z3 call, so a forged entry would be a forged proof.
// Entries are therefore signed with a per-user secret and kept in a per-user folder; the decision to trust an
// entry is made by the kernel (ProofKernel.AcceptCachedAnswer).
#pragma warning disable RS1035

/// <summary>
/// A per-user, HMAC-signed store of definitive Z3 answers.
/// </summary>
/// <remarks>
/// Each entry is bound to its query hash, so a valid entry cannot be copied onto another query. Anything that
/// fails to verify is ignored and the query goes to Z3. If the folder or key cannot be set up safely, the disk
/// cache is simply not used.
/// </remarks>
internal sealed class SignedAnswerCache
{
    private const string KeyFileName = "cache.key";
    private const string MacPrefix = "mac=";
    private const int KeyLength = 32;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly byte[] _key;

    private SignedAnswerCache(string directory, byte[] key)
    {
        _directory = directory;
        _key = key;
    }

    /// <summary>The default cache location: <c>&lt;LocalApplicationData&gt;/Verifiable/smt-cache-v2</c>.</summary>
    internal static string? DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Verifiable", "smt-cache-v2");
    }

    /// <summary>
    /// Opens (creating if needed) the cache in <paramref name="directory"/>. Returns <c>null</c> if the folder
    /// or its key cannot be set up safely, in which case callers must not use a disk cache at all.
    /// </summary>
    /// <remarks>
    /// A folder or key that someone else could have written to is never trusted: an attacker who planted a key
    /// could sign any answer. A fresh folder is built under a temporary name, locked to the owner, given a new
    /// key and only then renamed into place. An existing folder is used only if it is a real directory (not a
    /// link) and, on Unix, neither it nor its key is accessible to group or others. Because the folder is 700
    /// and we can read it, we are its owner (or root). Anything else is moved aside and replaced once.
    /// </remarks>
    internal static SignedAnswerCache? Open(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;

        try
        {
            var full = Path.GetFullPath(directory!);
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) return null;

            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                TryRestrictToOwner(parent!, OwnerOnlyDirectory);
            }

            if (!Directory.Exists(full))
            {
                TryCreateFresh(full);
            }

            var key = TryLoadTrustedKey(full);
            if (key is null)
            {
                // Something we did not create safely is in the way: set it aside and start again, once.
                if (Directory.Exists(full) || File.Exists(full))
                {
                    Directory.Move(full, full + ".untrusted-" + Guid.NewGuid().ToString("N"));
                }

                TryCreateFresh(full);
                key = TryLoadTrustedKey(full);
            }

            if (key is null) return null;

            SweepStaleTempFiles(full);
            return new SignedAnswerCache(full, key);
        }
        catch
        {
            return null;
        }
    }

    internal bool TryLoad(string hash, out SmtQueryResult? result)
    {
        result = null;
        try
        {
            var path = EntryPath(hash);
            if (path is null || !File.Exists(path)) return false;

            var lines = File.ReadAllLines(path, Utf8NoBom);
            if (lines.Length < 2 || !lines[lines.Length - 1].StartsWith(MacPrefix, StringComparison.Ordinal)) return false;

            var body = new string[lines.Length - 1];
            Array.Copy(lines, body, body.Length);
            var expectedMac = Sign(hash, body);
            var authentic = FixedTimeEquals(expectedMac, lines[lines.Length - 1].Substring(MacPrefix.Length));

            if (!Enum.TryParse<SmtStatus>(body[0], out var status) || !Enum.IsDefined(typeof(SmtStatus), status)) return false;
            if (!ProofKernel.AcceptCachedAnswer(authentic, status)) return false;

            var model = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < body.Length; i++)
            {
                var eq = body[i].IndexOf('=');
                if (eq > 0)
                {
                    model[body[i].Substring(0, eq)] = body[i].Substring(eq + 1);
                }
            }

            result = new SmtQueryResult(status, hash, model, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void Save(string hash, SmtQueryResult result)
    {
        if (!ProofKernel.IsCacheable(result.Status)) return;

        string? temp = null;
        try
        {
            var path = EntryPath(hash);
            if (path is null) return;

            // The query hash fixes the answer, so a valid existing entry never needs rewriting.
            if (File.Exists(path) && TryLoad(hash, out _)) return;

            var body = new List<string>(result.Model.Count + 1) { result.Status.ToString() };
            foreach (var kv in result.Model)
            {
                // Keys are split at the first '='; a key containing one could not round-trip, so skip caching.
                if (kv.Key.IndexOf('=') >= 0) return;
                body.Add($"{OneLine(kv.Key)}={OneLine(kv.Value)}");
            }

            var lines = new List<string>(body) { MacPrefix + Sign(hash, body.ToArray()) };

            // Write then rename, so a reader never sees a half-written entry.
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(temp, lines, Utf8NoBom); // the folder is owner-only, so entries need no chmod of their own
            if (File.Exists(path))
            {
                // Another writer may have finished first; then there is nothing to replace.
                if (TryLoad(hash, out _)) return;
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            // Cache write failures are non-fatal; a concurrent writer may simply have won.
        }
        finally
        {
            if (temp is not null)
            {
                DeleteWithRetry(temp);
            }
        }
    }

    /// <summary>
    /// Deletes a temp file, retrying briefly: on Windows a virus scanner or indexer can hold a newly written
    /// file open for a moment, and a failed delete would otherwise leave the file behind.
    /// </summary>
    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            System.Threading.Thread.Sleep(20 * (attempt + 1));
        }
    }

    /// <summary>Removes temp files left behind by a crash or a delete that kept failing. Best effort.</summary>
    private static void SweepStaleTempFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleTempAge;
            foreach (var temp in Directory.EnumerateFiles(directory, "*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(temp) < cutoff)
                {
                    DeleteWithRetry(temp);
                }
            }
        }
        catch
        {
            // Sweeping is housekeeping only.
        }
    }

    internal static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    /// <summary>Signs an entry body. Exposed for tests that forge entries.</summary>
    internal string Sign(string hash, string[] body)
    {
        using var hmac = new HMACSHA256(_key);
        var text = hash + "\n" + string.Join("\n", body);
        return ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    internal string? EntryPath(string hash)
    {
        // Hashes are lowercase SHA-256 hex; anything else could escape the folder.
        if (hash.Length != 64) return null;
        foreach (var ch in hash)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return null;
        }

        return Path.Combine(_directory, hash + ".cache");
    }

    private const int OwnerOnlyDirectory = 448; // 0700
    private const int OwnerOnlyFile = 384;      // 0600
    private const int GroupAndOtherBits = 63;   // 0077

    private static readonly Type? UnixFileModeType = typeof(File).Assembly.GetType("System.IO.UnixFileMode");
    private static readonly System.Reflection.MethodInfo? GetUnixFileModeMethod =
        UnixFileModeType is null ? null : typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) });
    private static readonly System.Reflection.MethodInfo? SetUnixFileModeMethod =
        UnixFileModeType is null ? null : typeof(File).GetMethod("SetUnixFileMode", new[] { typeof(string), UnixFileModeType });

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Builds a complete, owner-only cache folder under a temporary name and renames it into place.</summary>
    private static void TryCreateFresh(string full)
    {
        var staging = full + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            RestrictToOwner(staging, OwnerOnlyDirectory);

            // Until the chmod above, a permissive umask may have left the folder open. Refuse it if anyone used that window.
            if (Directory.EnumerateFileSystemEntries(staging).GetEnumerator().MoveNext()) return;

            var key = new byte[KeyLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }

            var keyPath = Path.Combine(staging, KeyFileName);
            using (var fs = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(key, 0, key.Length);
            }

            RestrictToOwner(keyPath, OwnerOnlyFile);
            Directory.Move(staging, full); // fails if another process got there first; its folder is checked instead
        }
        catch
        {
            // Lost a race or cannot lock the folder down; the caller checks whatever is at `full`.
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Returns the folder's key only if neither the folder nor the key could have been written by anyone else.</summary>
    private static byte[]? TryLoadTrustedKey(string full)
    {
        if (!Directory.Exists(full) || IsLink(full) || !IsOwnerOnly(full)) return null;

        var keyPath = Path.Combine(full, KeyFileName);
        if (!File.Exists(keyPath) || IsLink(keyPath) || !IsOwnerOnly(keyPath)) return null;

        var key = File.ReadAllBytes(keyPath);
        return key.Length == KeyLength ? key : null;
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsOwnerOnly(string path)
    {
        // On Windows, LocalApplicationData is private to the user through its inherited ACL.
        if (IsWindows) return true;

        // No way to read the mode (runtime older than .NET 7): do not trust the folder.
        if (GetUnixFileModeMethod is null) return false;

        var mode = Convert.ToInt32(GetUnixFileModeMethod.Invoke(null, new object[] { path }));
        return (mode & GroupAndOtherBits) == 0;
    }

    private static void RestrictToOwner(string path, int mode)
    {
        if (IsWindows) return;
        if (SetUnixFileModeMethod is null || UnixFileModeType is null)
        {
            throw new IOException("This runtime cannot set Unix file modes.");
        }

        SetUnixFileModeMethod.Invoke(null, new object[] { path, Enum.ToObject(UnixFileModeType, mode) });
    }

    private static void TryRestrictToOwner(string path, int mode)
    {
        try { RestrictToOwner(path, mode); } catch { /* best effort for the parent folder */ }
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static bool FixedTimeEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length) return false;
        var diff = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            diff |= expected[i] ^ actual[i];
        }

        return diff == 0;
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
