using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GameShift.Core.Updates;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// Installed-mode update: staging inside the install folder, verification right before the installer
/// runs, and cleanup. The install folder is a temp directory; no installer is ever run.
/// </summary>
public class InstallerUpdateTests
{
    private static readonly byte[] InstallerBytes = "genuine installer bytes"u8.ToArray();

    private static string DigestOf(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string StageInstaller(string installDir, byte[] bytes)
    {
        var path = InstallerUpdate.CreateStagingPath(installDir);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void CreateStagingPath_IsANewFolderDirectlyUnderInstallDirUpdates()
    {
        using var install = new TempPath();

        var first = InstallerUpdate.CreateStagingPath(install.Path);
        var second = InstallerUpdate.CreateStagingPath(install.Path);

        foreach (var path in new[] { first, second })
        {
            Assert.Equal("GameShift-Setup.exe", Path.GetFileName(path));
            var stagingDir = Path.GetDirectoryName(path)!;
            Assert.True(Directory.Exists(stagingDir));
            Assert.Equal(Path.Combine(install.Path, "updates"), Path.GetDirectoryName(stagingDir));
        }

        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
    }

    [Fact]
    public void Launch_VerifiedInstaller_StartsItSilentlyFromTheStagingFolder()
    {
        using var install = new TempPath();
        var path = StageInstaller(install.Path, InstallerBytes);
        var started = new List<ProcessStartInfo>();

        var result = InstallerUpdate.Launch(path, install.Path, DigestOf(InstallerBytes),
            psi => { started.Add(psi); return true; });

        Assert.True(result);
        var psi = Assert.Single(started);
        Assert.Equal(path, psi.FileName);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1", psi.Arguments);
        Assert.Equal(Path.GetDirectoryName(path), psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void Launch_AcceptsUppercaseDigest()
    {
        using var install = new TempPath();
        var path = StageInstaller(install.Path, InstallerBytes);
        var digest = "SHA256:" + Convert.ToHexString(SHA256.HashData(InstallerBytes));

        Assert.True(InstallerUpdate.Launch(path, install.Path, digest, _ => true));
    }

    [Fact]
    public void Launch_FileChangedAfterDownload_IsRefusedBeforeStartingAndDiscarded()
    {
        using var install = new TempPath();
        var path = StageInstaller(install.Path, InstallerBytes);
        var expected = DigestOf(InstallerBytes);
        File.WriteAllBytes(path, "replaced installer bytes"u8.ToArray());
        var startCalls = 0;

        var result = InstallerUpdate.Launch(path, install.Path, expected, _ => { startCalls++; return true; });

        Assert.False(result);
        Assert.Equal(0, startCalls);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:")]
    [InlineData("sha256:123")]
    [InlineData("sha256:zz4c5fbb1e5f8b0b8a2a7d1e0d4b0b8b4c1b1e0e7a0b0e0f0a0b0c0d0e0f0a0b")]
    public void Launch_WithoutAUsableDigest_IsRefusedBeforeStarting(string? digest)
    {
        using var install = new TempPath();
        var path = StageInstaller(install.Path, InstallerBytes);
        var startCalls = 0;

        var result = InstallerUpdate.Launch(path, install.Path, digest, _ => { startCalls++; return true; });

        Assert.False(result);
        Assert.Equal(0, startCalls);
    }

    [Theory]
    [InlineData(@"GameShift-Setup.exe")]                       // install folder itself
    [InlineData(@"updates\GameShift-Setup.exe")]               // updates root, no staging folder
    [InlineData(@"updates\a\b\GameShift-Setup.exe")]           // nested too deep
    [InlineData(@"updates\a\GameShift.App.exe")]               // not the installer
    [InlineData(@"updates\a\..\..\evil\GameShift-Setup.exe")]  // escapes via dot segments
    public void Launch_PathThatIsNotAStagedInstaller_IsRefusedBeforeStarting(string relativePath)
    {
        using var install = new TempPath();
        var path = Path.Combine(install.Path, relativePath);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, InstallerBytes);
        var startCalls = 0;

        var result = InstallerUpdate.Launch(path, install.Path, DigestOf(InstallerBytes),
            _ => { startCalls++; return true; });

        Assert.False(result);
        Assert.Equal(0, startCalls);
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public void Launch_StagedUnderAnotherInstallFolder_IsRefused()
    {
        using var install = new TempPath();
        using var other = new TempPath();
        var path = StageInstaller(other.Path, InstallerBytes);
        var startCalls = 0;

        var result = InstallerUpdate.Launch(path, install.Path, DigestOf(InstallerBytes),
            _ => { startCalls++; return true; });

        Assert.False(result);
        Assert.Equal(0, startCalls);
    }

    [Fact]
    public void Launch_KeepsTheInstallerLockedAgainstWritesAndDeletionWhileStarting()
    {
        using var install = new TempPath();
        var path = StageInstaller(install.Path, InstallerBytes);
        Exception? writeError = null;
        Exception? deleteError = null;

        var result = InstallerUpdate.Launch(path, install.Path, DigestOf(InstallerBytes), _ =>
        {
            writeError = Record.Exception(() =>
                new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            deleteError = Record.Exception(() => File.Delete(path));
            return true;
        });

        Assert.True(result);
        Assert.IsAssignableFrom<IOException>(writeError);
        Assert.IsAssignableFrom<IOException>(deleteError);
        Assert.Equal(InstallerBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Launch_RealProcessStartsWhileTheInstallerIsLocked()
    {
        // The lock must not stop Windows from opening the image. whoami.exe stands in for the installer:
        // it rejects the installer switches and exits without changing anything.
        using var install = new TempPath();
        var whoami = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        var bytes = File.ReadAllBytes(whoami);
        var path = StageInstaller(install.Path, bytes);
        int? exitCode = null;

        var result = InstallerUpdate.Launch(path, install.Path, DigestOf(bytes), psi =>
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using var process = Process.Start(psi);
            if (process == null) return false;
            if (process.WaitForExit(10_000)) exitCode = process.ExitCode;
            else process.Kill();
            return true;
        });

        Assert.True(result);
        Assert.NotNull(exitCode);
    }

    [Fact]
    public void Launch_StarterFails_ReturnsFalse()
    {
        using var install = new TempPath();
        var path = StageInstaller(install.Path, InstallerBytes);

        Assert.False(InstallerUpdate.Launch(path, install.Path, DigestOf(InstallerBytes), _ => false));
        Assert.False(InstallerUpdate.Launch(path, install.Path, DigestOf(InstallerBytes),
            _ => throw new InvalidOperationException("start failed")));
    }

    [Fact]
    public void CleanupStaging_RemovesUpdatesFolder_AndNothingElse()
    {
        using var install = new TempPath();
        var app = install.GetFile("GameShift.App.exe");
        File.WriteAllText(app, "app");
        StageInstaller(install.Path, InstallerBytes);
        StageInstaller(install.Path, InstallerBytes);

        InstallerUpdate.CleanupStaging(install.Path);

        Assert.False(Directory.Exists(Path.Combine(install.Path, "updates")));
        Assert.True(File.Exists(app));
    }

    [Fact]
    public void CleanupStaging_LeavesAnInstallerInUse_AndRemovesTheRest()
    {
        using var install = new TempPath();
        var inUse = StageInstaller(install.Path, InstallerBytes);
        var stale = StageInstaller(install.Path, InstallerBytes);

        using (new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            InstallerUpdate.CleanupStaging(install.Path);
        }

        Assert.True(File.Exists(inUse));
        Assert.False(Directory.Exists(Path.GetDirectoryName(stale)));

        InstallerUpdate.CleanupStaging(install.Path);
        Assert.False(Directory.Exists(Path.Combine(install.Path, "updates")));
    }

    [Fact]
    public void CleanupStaging_NoUpdatesFolder_DoesNothing()
    {
        using var install = new TempPath();

        InstallerUpdate.CleanupStaging(install.Path);

        Assert.True(Directory.Exists(install.Path));
    }

    [Fact]
    public void DiscardStaged_IgnoresPathsOutsideTheUpdatesFolder()
    {
        using var install = new TempPath();
        var app = install.GetFile("GameShift-Setup.exe");
        File.WriteAllText(app, "not staged");

        InstallerUpdate.DiscardStaged(app, install.Path);

        Assert.True(File.Exists(app));
        Assert.True(Directory.Exists(install.Path));
    }
}
