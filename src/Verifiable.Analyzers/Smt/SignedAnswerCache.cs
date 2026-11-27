using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// or its key cannot be set up, in which case callers must not use a disk cache at all.
    /// </summary>
    internal static SignedAnswerCache? Open(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;

        try
        {
            Directory.CreateDirectory(directory);
            RestrictToOwner(directory!, isDirectory: true);

            var key = LoadOrCreateKey(directory!);
            return key is null ? null : new SignedAnswerCache(directory!, key);
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

        try
        {
            var path = EntryPath(hash);
            if (path is null) return;

            var body = new List<string>(result.Model.Count + 1) { result.Status.ToString() };
            foreach (var kv in result.Model)
            {
                body.Add($"{kv.Key}={kv.Value.Replace('\r', ' ').Replace('\n', ' ')}");
            }

            var lines = new List<string>(body) { MacPrefix + Sign(hash, body.ToArray()) };

            // Write then move, so a reader never sees a half-written entry.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(temp, lines, Utf8NoBom); // the folder is owner-only, so entries need no chmod of their own
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        catch
        {
            // Cache write failures are non-fatal.
        }
    }

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

    private static byte[]? LoadOrCreateKey(string directory)
    {
        var keyPath = Path.Combine(directory, KeyFileName);
        if (!File.Exists(keyPath))
        {
            var fresh = new byte[KeyLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(fresh);
            }

            var temp = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, fresh);
            RestrictToOwner(temp, isDirectory: false);
            try
            {
                File.Move(temp, keyPath);
            }
            catch (IOException)
            {
                // Another process created the key first; use theirs.
                File.Delete(temp);
            }
        }

        var key = File.ReadAllBytes(keyPath);
        return key.Length == KeyLength ? key : null;
    }

    private static void RestrictToOwner(string path, bool isDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // LocalApplicationData is already private to the user on Windows.
            return;
        }

        using var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            Arguments = $"{(isDirectory ? "700" : "600")} \"{path.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (chmod is null || !chmod.WaitForExit(5000) || chmod.ExitCode != 0)
        {
            throw new IOException($"Could not restrict permissions on '{path}'.");
        }
    }

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
