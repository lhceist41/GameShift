using System;
using System.Collections.Generic;
using GameShift.Core.Updates;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// How a portable copy and the installed copy hand over at startup. The file and trust checks are
/// fakes that record what they were asked about.
/// </summary>
public class InstallHandoverTests
{
    private const string InstallDir = @"C:\Program Files\GameShift";
    private const string InstalledExe = @"C:\Program Files\GameShift\GameShift.App.exe";
    private const string PortableExe = @"C:\Users\me\Downloads\GameShift.App.exe";

    private static readonly InstallationInfo PortableWithInstall = new(InstallMode.Portable, PortableExe, InstallDir);
    private static readonly InstallationInfo PortableAlone = new(InstallMode.Portable, PortableExe, null);
    private static readonly InstallationInfo Installed = new(InstallMode.Installed, InstalledExe, InstallDir);

    private sealed class Checks
    {
        public bool ExeExists { get; set; } = true;
        public bool Trusted { get; set; } = true;
        public List<string> Exists { get; } = new();
        public List<string> Trust { get; } = new();

        public StartupHandover Decide(InstallationInfo install, bool startedAtLogon) =>
            InstallHandover.DecideAtStart(install, startedAtLogon,
                path => { Exists.Add(path); return ExeExists; },
                path =>
                {
                    Trust.Add(path);
                    return Trusted
                        ? new InstallFolderTrustResult(true, Array.Empty<string>())
                        : InstallFolderTrustResult.Untrusted("Users can write to the folder");
                });
    }

    [Fact]
    public void PortableWithTrustedInstall_NotAtLogon_OffersTheExeTheTrustCheckPassed()
    {
        var c = new Checks();

        var h = c.Decide(PortableWithInstall, startedAtLogon: false);

        Assert.Equal(HandoverKind.OfferInstalledCopy, h.Kind);
        Assert.Equal(InstalledExe, h.InstalledExePath);
        Assert.Equal(new[] { InstalledExe }, c.Trust);
        Assert.Null(h.RefusedMessage);
    }

    [Fact]
    public void PortableWithTrustedInstall_AtLogon_ExitsQuietly_WithNothingToStart()
    {
        var h = new Checks().Decide(PortableWithInstall, startedAtLogon: true);

        Assert.Equal(HandoverKind.ExitQuietly, h.Kind);
        Assert.Null(h.InstalledExePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortableWithUntrustedInstall_OffersNothing_AndTellsTheUserAndTheLog(bool startedAtLogon)
    {
        var c = new Checks { Trusted = false };

        var h = c.Decide(PortableWithInstall, startedAtLogon);

        Assert.Equal(HandoverKind.InstallRefused, h.Kind);
        Assert.Null(h.InstalledExePath);
        Assert.Contains(InstallDir, h.RefusedMessage);
        Assert.Contains("not protected", h.RefusedMessage);
        Assert.Equal(new[] { "Users can write to the folder" }, h.Reasons);
    }

    [Fact]
    public void TrustCheckThatThrows_IsARefusal()
    {
        var h = InstallHandover.DecideAtStart(PortableWithInstall, false, _ => true,
            _ => throw new InvalidOperationException("boom"));

        Assert.Equal(HandoverKind.InstallRefused, h.Kind);
        Assert.Null(h.InstalledExePath);
        Assert.Contains(h.Reasons!, r => r.Contains("boom"));
    }

    [Fact]
    public void MissingInstalledExe_OffersNothing_AndIsNotTrustChecked()
    {
        var c = new Checks { ExeExists = false };

        var h = c.Decide(PortableWithInstall, startedAtLogon: false);

        Assert.Equal(HandoverKind.InstallRefused, h.Kind);
        Assert.Null(h.InstalledExePath);
        Assert.Contains("missing", h.RefusedMessage);
        Assert.Equal(new[] { InstalledExe }, c.Exists);
        Assert.Empty(c.Trust);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledCopy_NeverHandsOver(bool startedAtLogon)
    {
        var c = new Checks();

        var h = c.Decide(Installed, startedAtLogon);

        Assert.Equal(HandoverKind.None, h.Kind);
        Assert.Empty(c.Exists);
        Assert.Empty(c.Trust);
    }

    [Fact]
    public void PortableWithNothingInstalled_HandsOverNothing()
    {
        var h = new Checks().Decide(PortableAlone, startedAtLogon: true);

        Assert.Equal(HandoverKind.None, h.Kind);
    }

    [Theory]
    [InlineData(@"C:\Program Files\GameShift\GameShift.App.exe")]
    [InlineData(@"c:\program files\gameshift\GameShift.App.exe")]
    public void CopyInTheUntrustedRegisteredFolderItself_IsRefused_NeverOfferedItself(string exePath)
    {
        // Detect made it portable because its own folder failed the trust check; a later check passing
        // must not make it start its own exe.
        var c = new Checks();

        var h = c.Decide(new InstallationInfo(InstallMode.Portable, exePath, InstallDir), startedAtLogon: false);

        Assert.Equal(HandoverKind.InstallRefused, h.Kind);
        Assert.Null(h.InstalledExePath);
        Assert.Empty(c.Trust);
    }

    [Fact]
    public void InstallOffer_OnlyForAPortableCopyWithNothingInstalled_Once_AndNotAtLogon()
    {
        Assert.True(InstallHandover.ShouldOfferInstall(PortableAlone, offerShown: false, startedAtLogon: false));

        Assert.False(InstallHandover.ShouldOfferInstall(PortableAlone, offerShown: true, startedAtLogon: false));
        Assert.False(InstallHandover.ShouldOfferInstall(PortableAlone, offerShown: false, startedAtLogon: true));
        Assert.False(InstallHandover.ShouldOfferInstall(PortableWithInstall, offerShown: false, startedAtLogon: false));
        Assert.False(InstallHandover.ShouldOfferInstall(Installed, offerShown: false, startedAtLogon: false));
        Assert.False(InstallHandover.ShouldOfferInstall(new InstallationInfo(InstallMode.Installed, InstalledExe, null), offerShown: false, startedAtLogon: false));
    }

    [Fact]
    public void OldCopyNote_InstalledCopy_NamesTheRecordedFile_WhenItIsStillThere()
    {
        var note = InstallHandover.DecideOldCopyNote(Installed, PortableExe, path => path == PortableExe);

        Assert.True(note.ClearRecord);
        Assert.Equal(PortableExe, note.PathToMention);
    }

    [Fact]
    public void OldCopyNote_FileAlreadyGone_ClearsTheRecordWithoutANote()
    {
        var note = InstallHandover.DecideOldCopyNote(Installed, PortableExe, _ => false);

        Assert.True(note.ClearRecord);
        Assert.Null(note.PathToMention);
    }

    [Fact]
    public void OldCopyNote_RecordedPathInTheInstallFolder_IsNeverMentioned()
    {
        var note = InstallHandover.DecideOldCopyNote(Installed, InstalledExe, _ => true);

        Assert.True(note.ClearRecord);
        Assert.Null(note.PathToMention);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OldCopyNote_NothingRecorded_NoNote(string? recorded)
    {
        var note = InstallHandover.DecideOldCopyNote(Installed, recorded, _ => true);

        Assert.False(note.ClearRecord);
        Assert.Null(note.PathToMention);
    }

    [Fact]
    public void OldCopyNote_PortableCopy_KeepsTheRecordForTheInstalledCopy()
    {
        // The portable copy records the path just before the installer runs; it must survive until the
        // installed copy starts.
        var note = InstallHandover.DecideOldCopyNote(PortableAlone, PortableExe, _ => true);

        Assert.False(note.ClearRecord);
        Assert.Null(note.PathToMention);
    }

    [Fact]
    public void OldCopyNote_RelativeRecordedPath_IsClearedWithoutANote()
    {
        var note = InstallHandover.DecideOldCopyNote(Installed, "GameShift.App.exe", _ => true);

        Assert.True(note.ClearRecord);
        Assert.Null(note.PathToMention);
    }
}
