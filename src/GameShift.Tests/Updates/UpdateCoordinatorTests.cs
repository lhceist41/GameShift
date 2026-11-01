using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GameShift.Core.Updates;
using GameShift.Tests.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// A portable copy updates by staging the installer in %ProgramFiles%\GameShift\updates and running it.
/// A temp folder stands in for Program Files; the trust check is a fake, since a folder the test can
/// create is never admin-only. Junctions need no privilege, so the link cases run unelevated.
/// </summary>
public class UpdateCoordinatorTests
{
    private static readonly byte[] InstallerBytes = "genuine installer bytes"u8.ToArray();
    private static readonly string Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(InstallerBytes));

    private sealed class Fixture : IDisposable
    {
        private readonly TempPath _temp = new();

        public Fixture()
        {
            ProgramFiles = Path.Combine(_temp.Path, "Program Files");
            Directory.CreateDirectory(ProgramFiles);
        }

        public string Root => _temp.Path;
        public string ProgramFiles { get; }
        public string DefaultFolder => Path.Combine(ProgramFiles, "GameShift");
        public string Updates => Path.Combine(DefaultFolder, "updates");

        public Func<string, bool> IsTrusted { get; set; } = _ => true;
        public List<(string Path, bool Existed)> Checks { get; } = new();
        public List<string> Downloads { get; } = new();
        public bool DownloadSucceeds { get; set; } = true;

        public PortableUpdateTarget Target(string? registered = null, string? programFiles = "") =>
            new(programFiles == "" ? ProgramFiles : programFiles, registered, path =>
            {
                Checks.Add((path, Directory.Exists(path)));
                return IsTrusted(path)
                    ? new InstallFolderTrustResult(true, Array.Empty<string>())
                    : InstallFolderTrustResult.Untrusted("not admin-only");
            });

        public async Task<StagedUpdate?> DownloadAsync(PortableUpdateTarget target, string assetName = "GameShift-Setup.exe") =>
            (await DownloadResultAsync(target, assetName)).Staged;

        public Task<UpdateDownloadResult> DownloadResultAsync(PortableUpdateTarget target, string assetName = "GameShift-Setup.exe") =>
            UpdateCoordinator.DownloadAsync(Update(assetName), new Progress<double>(), CancellationToken.None,
                (out string? reason) => UpdateCoordinator.ResolveUpdateFolder(InstallMode.Portable, target, create: true, out reason),
                (url, path, size, digest, progress, ct) =>
                {
                    Downloads.Add(path);
                    if (DownloadSucceeds)
                        File.WriteAllBytes(path, InstallerBytes);
                    return Task.FromResult(DownloadSucceeds);
                });

        public string Victim()
        {
            var target = Path.Combine(Root, "victim");
            Directory.CreateDirectory(Path.Combine(target, "updates", "staged"));
            File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(target, "updates", "staged", "GameShift-Setup.exe"), "keep");
            return target;
        }

        public void Dispose() => _temp.Dispose();
    }

    private static UpdateInfo Update(string assetName) => new()
    {
        LatestVersion = "3.10.0",
        DownloadUrl = "https://github.com/lhceist41/GameShift/releases/download/v3.10.0/" + assetName,
        DownloadSize = InstallerBytes.Length,
        ExpectedSha256 = Digest,
        AssetName = assetName,
        Mode = InstallMode.Portable
    };

    [Fact]
    public async Task Download_CreatesTheDefaultFolder_ChecksIt_AndStagesTheInstallerInIt()
    {
        using var f = new Fixture();

        var staged = await f.DownloadAsync(f.Target());

        Assert.NotNull(staged);
        var pattern = "^" + Regex.Escape(f.Updates + "\\") + "[0-9a-f]{32}" + Regex.Escape("\\GameShift-Setup.exe") + "$";
        Assert.Matches(pattern, staged!.Path);
        Assert.Equal(Digest, staged.ExpectedSha256);
        Assert.Equal(InstallerBytes, File.ReadAllBytes(staged.Path));
        Assert.Equal(new[] { staged.Path }, f.Downloads);

        // Program Files first, then the folder made in it, once it exists.
        Assert.Equal(new[] { (f.ProgramFiles, true), (f.DefaultFolder, true) }, f.Checks);
    }

    [Fact]
    public async Task Download_ExistingTrustedDefaultFolder_IsUsedAsItIs()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DefaultFolder);
        File.WriteAllText(Path.Combine(f.DefaultFolder, "other.txt"), "x");

        var staged = await f.DownloadAsync(f.Target());

        Assert.NotNull(staged);
        Assert.True(File.Exists(Path.Combine(f.DefaultFolder, "other.txt")));
    }

    [Fact]
    public async Task Download_NeverWritesNextToTheRunningExe()
    {
        using var f = new Fixture();

        await f.DownloadAsync(f.Target());

        var path = Assert.Single(f.Downloads);
        Assert.StartsWith(f.Updates + "\\", path);
        Assert.False(path.EndsWith(".update", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("GameShift.App.exe")]
    [InlineData("GameShift.zip")]
    public async Task Download_AssetThatIsNotTheInstaller_DownloadsNothing(string assetName)
    {
        using var f = new Fixture();

        Assert.Null(await f.DownloadAsync(f.Target(), assetName));

        Assert.Empty(f.Downloads);
        Assert.False(Directory.Exists(f.DefaultFolder));
    }

    [Fact]
    public async Task Download_InstallRegisteredElsewhere_IsRefused_AndCreatesNothing()
    {
        using var f = new Fixture();

        Assert.Null(await f.DownloadAsync(f.Target(registered: @"D:\Games\GameShift")));

        Assert.Empty(f.Downloads);
        Assert.Empty(f.Checks);
        Assert.False(Directory.Exists(f.DefaultFolder));
    }

    [Fact]
    public async Task Download_UnknownProgramFiles_IsRefused()
    {
        using var f = new Fixture();

        Assert.Null(await f.DownloadAsync(f.Target(programFiles: null)));

        Assert.Empty(f.Downloads);
    }

    [Fact]
    public async Task Download_ProgramFilesNotAdminOnly_IsRefused_BeforeAnythingIsCreated()
    {
        using var f = new Fixture();
        f.IsTrusted = path => path != f.ProgramFiles;

        Assert.Null(await f.DownloadAsync(f.Target()));

        Assert.Empty(f.Downloads);
        Assert.False(Directory.Exists(f.DefaultFolder));
    }

    [Fact]
    public async Task Download_ExistingDefaultFolderNotAdminOnly_IsRefused_AndNothingIsStaged()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DefaultFolder);
        f.IsTrusted = path => path != f.DefaultFolder;

        Assert.Null(await f.DownloadAsync(f.Target()));

        Assert.Empty(f.Downloads);
        Assert.False(Directory.Exists(f.Updates));
    }

    [Fact]
    public async Task Download_CreatedDefaultFolderNotAdminOnly_IsRefused_AndNothingIsStaged()
    {
        using var f = new Fixture();
        f.IsTrusted = path => path != f.DefaultFolder;

        Assert.Null(await f.DownloadAsync(f.Target()));

        Assert.Contains((f.DefaultFolder, true), f.Checks);
        Assert.Empty(f.Downloads);
        Assert.False(Directory.Exists(f.Updates));
    }

    [Fact]
    public async Task Download_WithTheRealTrustCheck_RefusesAFolderTheUserCreated()
    {
        using var f = new Fixture();
        var target = new PortableUpdateTarget(f.ProgramFiles, null, InstallFolderTrust.CheckFolder);

        Assert.Null(await f.DownloadAsync(target));

        Assert.Empty(f.Downloads);
        Assert.False(Directory.Exists(f.DefaultFolder));
    }

    [Fact]
    public async Task Download_DefaultFolderIsAJunction_IsRefused_AndTheTargetIsUntouched()
    {
        using var f = new Fixture();
        var victim = f.Victim();
        var before = UserDataFolderTests.Snapshot(victim);
        using var junction = UserDataFolderTests.Junction(f.DefaultFolder, victim);

        Assert.Null(await f.DownloadAsync(f.Target()));

        Assert.Empty(f.Downloads);
        Assert.Equal(before, UserDataFolderTests.Snapshot(victim));
    }

    [Fact]
    public async Task Download_UpdatesFolderIsAJunction_IsRefused_AndTheTargetIsUntouched()
    {
        using var f = new Fixture();
        var victim = f.Victim();
        var before = UserDataFolderTests.Snapshot(victim);
        Directory.CreateDirectory(f.DefaultFolder);
        using var junction = UserDataFolderTests.Junction(f.Updates, victim);

        await Assert.ThrowsAsync<IOException>(() => f.DownloadAsync(f.Target()));

        Assert.Empty(f.Downloads);
        Assert.Equal(before, UserDataFolderTests.Snapshot(victim));
    }

    [Fact]
    public async Task Download_ProgramFilesIsAJunction_CreatesNothingInItsTarget()
    {
        using var f = new Fixture();
        var victim = f.Victim();
        var before = UserDataFolderTests.Snapshot(victim);
        var linked = Path.Combine(f.Root, "linked");
        using var junction = UserDataFolderTests.Junction(linked, victim);

        Assert.Null(await f.DownloadAsync(f.Target(programFiles: linked)));

        Assert.Empty(f.Downloads);
        Assert.Equal(before, UserDataFolderTests.Snapshot(victim));
    }

    [Fact]
    public async Task Download_Failed_DiscardsTheStagingFolder()
    {
        using var f = new Fixture();
        f.DownloadSucceeds = false;

        Assert.Null(await f.DownloadAsync(f.Target()));

        Assert.Single(f.Downloads);
        Assert.Empty(Directory.GetFileSystemEntries(f.Updates));
    }

    [Fact]
    public async Task Apply_RunsTheStagedInstallerWithTheDefaultFolder_AfterCheckingItAgain()
    {
        using var f = new Fixture();
        var staged = (await f.DownloadAsync(f.Target()))!;
        f.Checks.Clear();
        var launched = new List<(string Path, string Dir, string? Digest)>();

        var ok = UpdateCoordinator.Apply(staged,
            () => UpdateCoordinator.ResolveUpdateFolder(InstallMode.Portable, f.Target(), create: false),
            (path, dir, digest) => { launched.Add((path, dir, digest)); return true; });

        Assert.True(ok);
        Assert.Equal(new[] { (staged.Path, f.DefaultFolder, (string?)Digest) }, launched);
        Assert.Contains((f.DefaultFolder, true), f.Checks);
    }

    [Fact]
    public async Task Apply_DefaultFolderNoLongerAdminOnly_RunsNothing()
    {
        using var f = new Fixture();
        var staged = (await f.DownloadAsync(f.Target()))!;
        f.IsTrusted = path => path != f.DefaultFolder;
        bool launched = false;

        var ok = UpdateCoordinator.Apply(staged,
            () => UpdateCoordinator.ResolveUpdateFolder(InstallMode.Portable, f.Target(), create: false),
            (_, _, _) => launched = true);

        Assert.False(ok);
        Assert.False(launched);
    }

    [Fact]
    public async Task Apply_InstallRegisteredSinceTheDownload_RunsNothing()
    {
        using var f = new Fixture();
        var staged = (await f.DownloadAsync(f.Target()))!;
        bool launched = false;

        var ok = UpdateCoordinator.Apply(staged,
            () => UpdateCoordinator.ResolveUpdateFolder(InstallMode.Portable, f.Target(registered: f.DefaultFolder), create: false),
            (_, _, _) => launched = true);

        Assert.False(ok);
        Assert.False(launched);
    }

    [Fact]
    public async Task Cleanup_RemovesTheUpdatesFolder_AndTheEmptyDefaultFolder()
    {
        using var f = new Fixture();
        await f.DownloadAsync(f.Target());

        UpdateCoordinator.CleanupPortableStaging(f.Target());

        Assert.False(Directory.Exists(f.DefaultFolder));
        Assert.True(Directory.Exists(f.ProgramFiles));
    }

    [Fact]
    public async Task Cleanup_DefaultFolderWithOtherContent_RemovesOnlyTheUpdatesFolder()
    {
        using var f = new Fixture();
        await f.DownloadAsync(f.Target());
        File.WriteAllText(Path.Combine(f.DefaultFolder, "GameShift.App.exe"), "installed");

        UpdateCoordinator.CleanupPortableStaging(f.Target());

        Assert.False(Directory.Exists(f.Updates));
        Assert.True(File.Exists(Path.Combine(f.DefaultFolder, "GameShift.App.exe")));
    }

    [Fact]
    public async Task Cleanup_InstallRegistered_LeavesTheDefaultFolderAlone()
    {
        using var f = new Fixture();
        var staged = (await f.DownloadAsync(f.Target()))!;

        UpdateCoordinator.CleanupPortableStaging(f.Target(registered: @"D:\Games\GameShift"));

        Assert.True(File.Exists(staged.Path));
    }

    [Fact]
    public async Task Cleanup_DefaultFolderNotAdminOnly_RemovesNothing()
    {
        using var f = new Fixture();
        var staged = (await f.DownloadAsync(f.Target()))!;
        f.IsTrusted = path => path != f.DefaultFolder;

        UpdateCoordinator.CleanupPortableStaging(f.Target());

        Assert.True(File.Exists(staged.Path));
    }

    [Fact]
    public void Cleanup_NoDefaultFolder_DoesNothing()
    {
        using var f = new Fixture();

        UpdateCoordinator.CleanupPortableStaging(f.Target());

        Assert.True(Directory.Exists(f.ProgramFiles));
        Assert.False(Directory.Exists(f.DefaultFolder));
    }

    [Fact]
    public void Cleanup_DefaultFolderIsAJunction_LeavesItAndItsTargetAlone()
    {
        using var f = new Fixture();
        var victim = f.Victim();
        var before = UserDataFolderTests.Snapshot(victim);
        using var junction = UserDataFolderTests.Junction(f.DefaultFolder, victim);

        UpdateCoordinator.CleanupPortableStaging(f.Target());

        Assert.Equal(before, UserDataFolderTests.Snapshot(victim));
        Assert.True((File.GetAttributes(f.DefaultFolder) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void Cleanup_UpdatesFolderIsAJunction_LeavesItsTargetAlone()
    {
        using var f = new Fixture();
        var victim = f.Victim();
        var before = UserDataFolderTests.Snapshot(victim);
        Directory.CreateDirectory(f.DefaultFolder);
        using var junction = UserDataFolderTests.Junction(f.Updates, victim);

        UpdateCoordinator.CleanupPortableStaging(f.Target());

        Assert.Equal(before, UserDataFolderTests.Snapshot(victim));
        Assert.True(Directory.Exists(f.DefaultFolder));
    }

    [Fact]
    public async Task DownloadResult_Success_HasNoReason()
    {
        using var f = new Fixture();

        var result = await f.DownloadResultAsync(f.Target());

        Assert.NotNull(result.Staged);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task DownloadResult_InstallRegistered_SaysWhichFolderInstallsUpdates()
    {
        using var f = new Fixture();

        var result = await f.DownloadResultAsync(f.Target(registered: @"D:\Games\GameShift"));

        Assert.Null(result.Staged);
        Assert.Contains(@"D:\Games\GameShift", result.FailureReason);
        Assert.Contains("protected", result.FailureReason);
    }

    [Fact]
    public async Task DownloadResult_ProgramFilesNotAdminOnly_SaysTheFolderIsNotProtected()
    {
        using var f = new Fixture();
        f.IsTrusted = path => path != f.ProgramFiles;

        var result = await f.DownloadResultAsync(f.Target());

        Assert.Null(result.Staged);
        Assert.Contains("protected folder under Program Files", result.FailureReason);
    }

    [Fact]
    public async Task DownloadResult_FailedDownload_SaysSo()
    {
        using var f = new Fixture { DownloadSucceeds = false };

        var result = await f.DownloadResultAsync(f.Target());

        Assert.Null(result.Staged);
        Assert.Equal(UpdateCoordinator.DownloadFailedReason, result.FailureReason);
    }

    [Fact]
    public async Task DownloadResult_NotTheInstaller_SaysThereIsNoInstaller()
    {
        using var f = new Fixture();

        var result = await f.DownloadResultAsync(f.Target(), "GameShift.App.exe");

        Assert.Null(result.Staged);
        Assert.Equal(UpdateCoordinator.NoInstallerReason, result.FailureReason);
    }
}
