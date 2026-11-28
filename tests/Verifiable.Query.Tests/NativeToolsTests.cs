using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R16: native tools sit behind one .NET front door, pinned by version and checksum,
/// installed by one command, and reported in the shared JSON shape.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class NativeToolsTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Program.Run(args, null, o, e);
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>Builds a zip shaped like a Z3 release whose <c>bin/z3</c> is a script printing a version.</summary>
    private static string FakeArchive(string dir, string version, string? extraEntry = null)
    {
        var zipPath = Path.Combine(dir, "fake-z3.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(text.Replace("\r\n", "\n"));
            }

            Add("z3-fake/bin/z3", $"#!/bin/sh\necho \"Z3 version {version} - 64 bit\"\n");
            Add("z3-fake/bin/libz3.a", "static library, must be skipped");
            Add("z3-fake/include/z3.h", "header, must be skipped");
            if (extraEntry is not null) Add(extraEntry, "x");
        }
        return zipPath;
    }

    private static NativeTool FakeTool(string zipPath) => NativeTools.Z3 with
    {
        Artifacts = [new NativeToolArtifact(NativeTools.CurrentRid(), Path.GetFileName(zipPath), NativeTools.Sha256File(zipPath))],
    };

    private static T WithMirror<T>(string? mirror, Func<T> body)
    {
        var old = Environment.GetEnvironmentVariable(NativeTools.MirrorVariable);
        Environment.SetEnvironmentVariable(NativeTools.MirrorVariable, mirror);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable(NativeTools.MirrorVariable, old); }
    }

    [Fact]
    public void Manifest_PinsZ3ForEverySupportedPlatformWithASha256()
    {
        var z3 = NativeTools.Z3;
        Assert.Equal("5.3.3", z3.Version);
        Assert.StartsWith("https://", z3.BaseUrl);
        Assert.Equal(
            new[] { "linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64" },
            z3.Artifacts.Select(a => a.Rid).OrderBy(r => r, StringComparer.Ordinal).ToArray());
        foreach (var a in z3.Artifacts)
        {
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
            Assert.Contains(z3.Version, a.FileName);
        }
        Assert.Equal(z3.Artifacts.Count, z3.Artifacts.Select(a => a.Sha256).Distinct().Count());
    }

    [Fact]
    public void Install_VerifiesTheChecksumUnpacksBinAndChecksTheVersion()
    {
        if (OperatingSystem.IsWindows()) return; // the fake binary is a shell script
        var mirror = NewDir();
        var zip = FakeArchive(mirror, "5.3.3");
        var target = Path.Combine(NewDir(), "z3");

        var result = WithMirror(new Uri(mirror + Path.DirectorySeparatorChar).AbsoluteUri, () => NativeTools.Install(FakeTool(zip), target));

        Assert.True(result.Ok, result.Error);
        Assert.False(result.AlreadyInstalled);
        Assert.Equal("5.3.3", result.Status!.InstalledVersion);
        Assert.True(File.Exists(Path.Combine(target, "z3")));
        Assert.False(File.Exists(Path.Combine(target, "libz3.a")));
        Assert.False(File.Exists(Path.Combine(target, "z3.h")));
    }

    [Fact]
    public void Install_IsIdempotentAndDoesNotDownloadAgain()
    {
        if (OperatingSystem.IsWindows()) return;
        var mirror = NewDir();
        var zip = FakeArchive(mirror, "5.3.3");
        var tool = FakeTool(zip);
        var target = Path.Combine(NewDir(), "z3");
        var mirrorUri = new Uri(mirror + Path.DirectorySeparatorChar).AbsoluteUri;
        Assert.True(WithMirror(mirrorUri, () => NativeTools.Install(tool, target)).Ok);

        var again = WithMirror(new Uri(NewDir() + Path.DirectorySeparatorChar).AbsoluteUri, () => NativeTools.Install(tool, target));
        Assert.True(again.Ok, again.Error);
        Assert.True(again.AlreadyInstalled);
    }

    [Fact]
    public void Install_RefusesAnArchiveWhoseChecksumDoesNotMatch()
    {
        var mirror = NewDir();
        var zip = FakeArchive(mirror, "5.3.3");
        var tool = NativeTools.Z3 with
        {
            Artifacts = [new NativeToolArtifact(NativeTools.CurrentRid(), Path.GetFileName(zip), new string('0', 64))],
        };
        var target = Path.Combine(NewDir(), "z3");

        var result = WithMirror(new Uri(mirror + Path.DirectorySeparatorChar).AbsoluteUri, () => NativeTools.Install(tool, target));

        Assert.False(result.Ok);
        Assert.Contains("Checksum mismatch", result.Error);
        Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any());
    }

    [Fact]
    public void Install_RefusesAWrongVersion()
    {
        if (OperatingSystem.IsWindows()) return;
        var mirror = NewDir();
        var zip = FakeArchive(mirror, "4.8.12");
        var result = WithMirror(new Uri(mirror + Path.DirectorySeparatorChar).AbsoluteUri,
            () => NativeTools.Install(FakeTool(zip), Path.Combine(NewDir(), "z3")));

        Assert.False(result.Ok);
        Assert.Contains("4.8.12", result.Error);
    }

    [Fact]
    public void Unpack_RefusesEntriesThatEscapeTheInstallFolder()
    {
        var dir = NewDir();
        var zip = FakeArchive(dir, "5.3.3", extraEntry: "z3-fake/bin/..");
        var error = NativeTools.Unpack(zip, Path.Combine(dir, "out"), NativeTools.Z3);
        Assert.NotNull(error);
    }

    [Fact]
    public void Install_ReportsAnUnsupportedPlatform()
    {
        var result = NativeTools.Install(NativeTools.Z3, Path.Combine(NewDir(), "z3"), rid: "freebsd-x64");
        Assert.False(result.Ok);
        Assert.Contains("freebsd-x64", result.Error);
        Assert.Contains("linux-x64", result.Fix);
    }

    [Fact]
    public void ToolsStatus_MissingZ3_ExitsFourAndQuotesTheInstallCommand()
    {
        var (code, stdout, _) = Run("tools", "status", "z3", "--z3", Path.Combine(NewDir(), "no-z3"), "--format", "json");
        Assert.Equal(4, code);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("verifiable-query/tools@1", root.GetProperty("schema").GetString());
        Assert.Equal("tool_missing", root.GetProperty("status").GetString());
        Assert.Contains("verifiable-query tools install z3", root.GetProperty("error").GetProperty("fix").GetString());
        var z3 = root.GetProperty("tools")[0];
        Assert.False(z3.GetProperty("installed").GetBoolean());
        Assert.Equal("verifiable-query tools install z3", z3.GetProperty("installCommand").GetString());
    }

    [Fact]
    public void ToolsStatus_FoundZ3_ExitsZeroWithoutMachinePaths()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        var fake = Path.Combine(dir, "z3");
        File.WriteAllText(fake, "#!/bin/sh\necho \"Z3 version 5.3.3 - 64 bit\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);

        var (code, stdout, _) = Run("tools", "status", "--z3", fake, "--format", "json");
        Assert.Equal(0, code);
        Assert.DoesNotContain(dir, stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(stdout);
        var z3 = doc.RootElement.GetProperty("tools")[0];
        Assert.True(z3.GetProperty("installed").GetBoolean());
        Assert.True(z3.GetProperty("versionMatches").GetBoolean());
        Assert.Equal("--z3", z3.GetProperty("location").GetString());
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("tools", "frob")]
    [InlineData("tools", "install", "prolog")]
    [InlineData("tools", "status", "--bogus")]
    public void Tools_BadArguments_AreInputErrors(params string[] args)
    {
        var (code, stdout, _) = Run(args.Append("--format").Append("json").ToArray());
        Assert.Equal(3, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("input_error", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ToolsInstall_ThroughTheCli_FailsCleanlyWhenTheMirrorHasNoFile()
    {
        var (code, stdout, _) = WithMirror(new Uri(NewDir() + Path.DirectorySeparatorChar).AbsoluteUri,
            () => Run("tools", "install", "z3", "--to", NewDir(), "--force", "--format", "json"));
        Assert.Equal(3, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Contains(NativeTools.MirrorVariable, doc.RootElement.GetProperty("error").GetProperty("fix").GetString());
    }
}
