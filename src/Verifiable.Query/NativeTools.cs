using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Verifiable.Query;

/// <summary>One downloadable build of a native tool for one platform, pinned by checksum.</summary>
internal sealed record NativeToolArtifact(string Rid, string FileName, string Sha256);

/// <summary>
/// A native tool the toolchain depends on, pinned to one version. Agents never call it directly:
/// it runs as a managed subprocess behind a .NET interface (requirement R16).
/// </summary>
internal sealed record NativeTool(
    string Name,
    string Version,
    string Purpose,
    string BaseUrl,
    string ExecutableName,
    string VersionPrefix,
    IReadOnlyList<NativeToolArtifact> Artifacts)
{
    public string ExecutableFileName => OperatingSystem.IsWindows() ? ExecutableName + ".exe" : ExecutableName;
}

/// <summary>Where a tool was found, named without machine-specific paths.</summary>
internal enum ToolLocation
{
    NotFound,
    EnvironmentVariable,
    Managed,
    RepositoryLocal,
    SystemPath,
    Explicit,
}

internal sealed record ToolStatus(
    NativeTool Tool,
    ToolLocation Location,
    string? Path,
    string? InstalledVersion)
{
    public bool Installed => Location != ToolLocation.NotFound;
    public bool VersionMatches => InstalledVersion == Tool.Version;
}

internal sealed record InstallResult(bool Ok, bool AlreadyInstalled, string? Error, string? Fix, ToolStatus? Status);

/// <summary>
/// The pinned manifest of native tools, and the one place they are found, checked and installed.
/// </summary>
internal static class NativeTools
{
    /// <summary>Overrides the download base URL (an <c>https://</c> mirror or a <c>file://</c> folder).</summary>
    public const string MirrorVariable = "VERIFIABLE_TOOLS_MIRROR";

    public static readonly NativeTool Z3 = new(
        Name: "z3",
        Version: "5.3.3",
        Purpose: "SMT solver that proves contracts at build time and in every verifiable-query command.",
        BaseUrl: "https://github.com/Z3Prover/z3/releases/download/z3-5.3.3/",
        ExecutableName: "z3",
        VersionPrefix: "Z3 version ",
        Artifacts:
        [
            new("linux-x64", "z3-5.3.3-x64-glibc-2.39.zip", "d56f60b06f432f33de5810f7f1a384a339131f361e2d54d08ea83eea14326880"),
            new("linux-arm64", "z3-5.3.3-arm64-glibc-2.38.zip", "d4586e12af2609499e07d615ac578256125b19f396f0d6ca358fe7b22988bcec"),
            new("osx-x64", "z3-5.3.3-x64-osx-13.3.zip", "ace69787d5a21762154b103abe68178188f38e02362fb0f82d346621af90f8e7"),
            new("osx-arm64", "z3-5.3.3-arm64-osx-13.3.zip", "6957e6c75266d7ca8fa77030e86e83d78624567dcdf34cc11f4192fdc6f93c6b"),
            new("win-x64", "z3-5.3.3-x64-win.zip", "4e48624a6895b49606b3cf91ca4fbcc069fb2973b9706532170a144baa3fd798"),
            new("win-arm64", "z3-5.3.3-arm64-win.zip", "af442c9a8ac995ae0be789e33e60a5670d5e3895fd4f19d333b242b1b8afff24"),
        ]);

    /// <summary>Every native tool, in a fixed order.</summary>
    public static readonly IReadOnlyList<NativeTool> All = [Z3];

    public static NativeTool? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The exact command that installs a tool. Every "tool missing" message quotes it.</summary>
    public static string InstallCommand(NativeTool tool) => $"verifiable-query tools install {tool.Name}";

    /// <summary>The folder <c>tools install</c> writes to, which Z3 discovery also searches.</summary>
    public static string DefaultInstallDirectory(NativeTool tool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(string.IsNullOrEmpty(home) ? "." : home, ".verifiable", tool.Name);
    }

    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    /// <summary>Finds a tool the same way the analyzers do, and reads its version.</summary>
    public static ToolStatus Locate(NativeTool tool, string? explicitPath = null)
    {
        string? path;
        ToolLocation location;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            path = File.Exists(explicitPath) ? System.IO.Path.GetFullPath(explicitPath) : null;
            location = path is null ? ToolLocation.NotFound : ToolLocation.Explicit;
        }
        else
        {
            path = Verifiable.Analyzers.Smt.Z3SolverRunner.DiscoverZ3Path(null, System.IO.Path.Combine(Directory.GetCurrentDirectory(), "_"));
            location = path is null ? ToolLocation.NotFound : Classify(tool, path);
        }

        return new ToolStatus(tool, location, path, path is null ? null : ReadVersion(tool, path));
    }

    private static ToolLocation Classify(NativeTool tool, string path)
    {
        var env = Environment.GetEnvironmentVariable("VERIFIABLE_Z3_PATH");
        if (!string.IsNullOrWhiteSpace(env) && SamePath(System.IO.Path.GetFullPath(env), path)) return ToolLocation.EnvironmentVariable;
        if (SamePath(System.IO.Path.Combine(DefaultInstallDirectory(tool), tool.ExecutableFileName), path)) return ToolLocation.Managed;
        var parent = System.IO.Path.GetDirectoryName(path);
        if (parent is not null && System.IO.Path.GetFileName(parent) == tool.Name &&
            System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(parent)) == ".tools")
        {
            return ToolLocation.RepositoryLocal;
        }
        return ToolLocation.SystemPath;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Runs <c>tool --version</c> and returns the version number, or null if it does not run.</summary>
    public static string? ReadVersion(NativeTool tool, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(tool.VersionPrefix, StringComparison.Ordinal));
            if (line is null) return null;
            var rest = line.Substring(tool.VersionPrefix.Length);
            var end = rest.IndexOf(' ');
            return end < 0 ? rest : rest.Substring(0, end);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the pinned build for this platform, refuses it unless its SHA-256 matches the
    /// manifest, unpacks its <c>bin</c> folder, and checks the installed version.
    /// </summary>
    public static InstallResult Install(NativeTool tool, string? targetDirectory = null, string? rid = null, bool force = false)
    {
        rid ??= CurrentRid();
        var dir = System.IO.Path.GetFullPath(targetDirectory ?? DefaultInstallDirectory(tool));
        var exe = System.IO.Path.Combine(dir, tool.ExecutableFileName);

        if (!force && File.Exists(exe) && ReadVersion(tool, exe) == tool.Version)
        {
            return new InstallResult(true, true, null, null, new ToolStatus(tool, ToolLocation.Managed, exe, tool.Version));
        }

        var artifact = tool.Artifacts.FirstOrDefault(a => a.Rid == rid);
        if (artifact is null)
        {
            return new InstallResult(false, false,
                $"There is no pinned {tool.Name} {tool.Version} build for platform `{rid}`.",
                $"Install {tool.Name} {tool.Version} yourself and set VERIFIABLE_Z3_PATH to it. Supported platforms: {string.Join(", ", tool.Artifacts.Select(a => a.Rid))}.",
                null);
        }

        var baseUrl = Environment.GetEnvironmentVariable(MirrorVariable);
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = tool.BaseUrl;
        if (!baseUrl!.EndsWith("/", StringComparison.Ordinal)) baseUrl += "/";
        var url = baseUrl + artifact.FileName;

        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"verifiable-{tool.Name}-{Guid.NewGuid():N}.zip");
        try
        {
            var fetchError = Fetch(url, temp);
            if (fetchError is not null)
            {
                return new InstallResult(false, false, $"Could not download {url}: {fetchError}",
                    $"Check the network, or set {MirrorVariable} to a folder or mirror that holds {artifact.FileName}.", null);
            }

            var actual = Sha256File(temp);
            if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new InstallResult(false, false,
                    $"Checksum mismatch for {artifact.FileName}: expected {artifact.Sha256}, got {actual}. Nothing was installed.",
                    $"Do not use this file. Retry `{InstallCommand(tool)}`; if it keeps failing, the download source has changed.", null);
            }

            var unpackError = Unpack(temp, dir, tool);
            if (unpackError is not null)
            {
                return new InstallResult(false, false, unpackError, $"Delete {tool.Name}'s install folder and retry `{InstallCommand(tool)}`.", null);
            }

            var version = ReadVersion(tool, exe);
            if (version != tool.Version)
            {
                return new InstallResult(false, false,
                    $"{tool.Name} was unpacked but reports version `{version ?? "none"}`, not {tool.Version}.",
                    "This platform may be missing a system library the binary needs. Install the tool yourself and set VERIFIABLE_Z3_PATH.",
                    new ToolStatus(tool, ToolLocation.Managed, exe, version));
            }

            return new InstallResult(true, false, null, null, new ToolStatus(tool, ToolLocation.Managed, exe, version));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* best effort */ }
        }
    }

    private static string? Fetch(string url, string destination)
    {
        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var local = new Uri(url).LocalPath;
                if (!File.Exists(local)) return "file not found";
                File.Copy(local, destination, overwrite: true);
                return null;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Verifiable.Query");
            using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode}";
            using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var output = File.Create(destination);
            input.CopyTo(output);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Threading.Tasks.TaskCanceledException or UriFormatException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Copies the archive's <c>bin</c> folder (without static libraries, debug symbols or language
    /// bindings) into <paramref name="dir"/>. Entries that would land outside it are refused.
    /// </summary>
    internal static string? Unpack(string zipPath, string dir, NativeTool tool)
    {
        string[] skipExtensions = [".a", ".lib", ".pdb", ".jar"];
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries
                .Select(e => (Entry: e, Parts: e.FullName.Replace('\\', '/').Split('/')))
                .Where(x => x.Parts.Length == 3 && x.Parts[1] == "bin" && x.Parts[2].Length > 0)
                .Where(x => !skipExtensions.Contains(System.IO.Path.GetExtension(x.Parts[2]), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (!entries.Any(x => x.Parts[2] == tool.ExecutableFileName))
            {
                return $"The archive has no bin/{tool.ExecutableFileName}.";
            }

            Directory.CreateDirectory(dir);
            var root = System.IO.Path.GetFullPath(dir) + System.IO.Path.DirectorySeparatorChar;
            foreach (var (entry, parts) in entries)
            {
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, parts[2]));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                {
                    return $"Refused archive entry `{entry.FullName}`: it would be written outside the install folder.";
                }

                entry.ExtractToFile(target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"Could not unpack the archive: {ex.Message}";
        }
    }

    public static string LocationWord(ToolLocation location) => location switch
    {
        ToolLocation.EnvironmentVariable => "VERIFIABLE_Z3_PATH",
        ToolLocation.Managed => "managed",
        ToolLocation.RepositoryLocal => "repository",
        ToolLocation.SystemPath => "PATH",
        ToolLocation.Explicit => "--z3",
        _ => "not_found",
    };

    /// <summary>The <c>tools</c> result in the shared JSON shape (schema <c>verifiable-query/tools@1</c>).</summary>
    public static string ToJson(string command, AgentOutcome outcome, IReadOnlyList<ToolStatus> statuses, string? error, string? fix, bool? alreadyInstalled, bool indented)
    {
        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("tools"));
            w.WriteString("command", command);
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteString("platform", CurrentRid());
            if (alreadyInstalled is null) w.WriteNull("alreadyInstalled");
            else w.WriteBoolean("alreadyInstalled", alreadyInstalled.Value);
            if (error is null && fix is null) w.WriteNull("error");
            else
            {
                w.WriteStartObject("error");
                AgentOutput.StringOrNull(w, "message", error);
                AgentOutput.StringOrNull(w, "fix", fix);
                w.WriteEndObject();
            }

            w.WriteStartArray("tools");
            foreach (var s in statuses)
            {
                var artifact = s.Tool.Artifacts.FirstOrDefault(a => a.Rid == CurrentRid());
                w.WriteStartObject();
                w.WriteString("name", s.Tool.Name);
                w.WriteString("purpose", s.Tool.Purpose);
                w.WriteString("pinnedVersion", s.Tool.Version);
                AgentOutput.StringOrNull(w, "sha256", artifact?.Sha256);
                w.WriteBoolean("installed", s.Installed);
                AgentOutput.StringOrNull(w, "installedVersion", s.InstalledVersion);
                w.WriteBoolean("versionMatches", s.VersionMatches);
                w.WriteString("location", LocationWord(s.Location));
                w.WriteString("installCommand", InstallCommand(s.Tool));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
