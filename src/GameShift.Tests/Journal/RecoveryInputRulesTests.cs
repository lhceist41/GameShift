using GameShift.Core.Journal;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// The journal is replayed elevated, so each revert path acts only on what GameShift itself wrote.
/// Every accepted case below is a target the 3.8 or 3.9 apply paths recorded; every rejected case is
/// a way a tampered record could aim the replay somewhere else.
/// </summary>
public class RecoveryInputRulesTests
{
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // ── powercfg tokens ───────────────────────────────────────────────

    [Theory]
    [InlineData("54533251-82be-4824-96c1-47b60b740d00", "54533251-82be-4824-96c1-47b60b740d00")]
    [InlineData("{8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C}", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    public void PowerGuid_IsPassedOnInCanonicalForm(string recorded, string expected)
    {
        Assert.True(RecoveryInputRules.TryPowerGuid(recorded, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("SCHEME_CURRENT")]
    [InlineData("54533251-82be-4824-96c1-47b60b740d00 /h")]
    [InlineData("54533251-82be-4824-96c1-47b60b740d00\" /delete x")]
    [InlineData("")]
    [InlineData(null)]
    public void PowerGuid_RefusesAnythingElse(string? recorded)
    {
        Assert.False(RecoveryInputRules.TryPowerGuid(recorded, out _));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("-1", -1)]
    public void PowerIndex_AcceptsTheDecimalInt32sCaptured(string recorded, int expected)
    {
        Assert.True(RecoveryInputRules.TryPowerIndex(recorded, out int index));
        Assert.Equal(expected, index);
    }

    [Theory]
    [InlineData("0x10")]
    [InlineData("5 /hibernate off")]
    [InlineData(" 5")]
    [InlineData("4294967296")]
    [InlineData("")]
    public void PowerIndex_RefusesAnythingElse(string recorded)
    {
        Assert.False(RecoveryInputRules.TryPowerIndex(recorded, out _));
    }

    // ── IFEO ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Ifeo + @"\VALORANT-Win64-Shipping.exe\PerfOptions")]
    [InlineData(Ifeo + @"\League of Legends.exe\PerfOptions")]
    [InlineData(Ifeo + @"\osu!.exe\PerfOptions")]
    public void IfeoPath_AcceptsThePerfOptionsKeyOfOneImage(string keyPath)
    {
        Assert.True(RecoveryInputRules.IsIfeoPerfOptionsPath(keyPath));
    }

    [Theory]
    [InlineData(Ifeo + @"\game.exe\..\..\..\Policies\System\PerfOptions")]
    [InlineData(Ifeo + @"\game.exe\Extra\PerfOptions")]
    [InlineData(Ifeo + @"\game\PerfOptions")]
    [InlineData(Ifeo + @"\.exe\PerfOptions")]
    [InlineData(Ifeo + @"\game.exe")]
    [InlineData(Ifeo + @"\game.exe\PerfOptions\Sub")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\game.exe\PerfOptions")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options Extra\game.exe\PerfOptions")]
    [InlineData(null)]
    public void IfeoPath_RefusesAnyOtherKey(string? keyPath)
    {
        Assert.False(RecoveryInputRules.IsIfeoPerfOptionsPath(keyPath));
    }

    [Fact]
    public void IfeoOriginals_HoldOnlyTheModulesOwnValue()
    {
        Assert.True(RecoveryInputRules.AreIfeoOriginals(new Dictionary<string, int> { ["CpuPriorityClass"] = 2 }, "CpuPriorityClass"));
        Assert.True(RecoveryInputRules.AreIfeoOriginals(new Dictionary<string, int>(), "CpuPriorityClass"));
        Assert.False(RecoveryInputRules.AreIfeoOriginals(new Dictionary<string, int> { ["Debugger"] = 1 }, "CpuPriorityClass"));
        Assert.False(RecoveryInputRules.AreIfeoOriginals(new Dictionary<string, int> { ["CpuAffinityMask"] = 1 }, "CpuPriorityClass"));
    }

    // ── Session system tweaks ─────────────────────────────────────────

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", "DWord")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "DWord")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Affinity", "DWord")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Background Only", "String")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Clock Rate", "DWord")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", "DWord")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", "DWord")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "String")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "String")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\0003", "SelectiveSuspendEnabled", "DWord")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\0012", "EnhancedPowerMgmtEnabled", "DWord")]
    public void SessionTweak_AcceptsEveryValueTheTweaksWrote(string path, string name, string kind)
    {
        Assert.True(RecoveryInputRules.IsSessionTweak(hklm: true, path, name, kind));
    }

    [Theory]
    [InlineData(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", "DWord")]
    [InlineData(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "Debugger", "DWord")]
    [InlineData(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", "String")]
    [InlineData(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Background Only", "ExpandString")]
    [InlineData(true, @"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\0003\Device Parameters", "SelectiveSuspendEnabled", "DWord")]
    [InlineData(true, @"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\Properties", "SelectiveSuspendEnabled", "DWord")]
    [InlineData(false, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "DWord")]
    public void SessionTweak_RefusesAnythingElse(bool hklm, string path, string name, string kind)
    {
        Assert.False(RecoveryInputRules.IsSessionTweak(hklm, path, name, kind));
    }

    // ── Network optimizer ─────────────────────────────────────────────

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\TcpAckFrequency")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\TCPNoDelay")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001\*InterruptModeration")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001\*LsoV2IPv4")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001\*LsoV2IPv6")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001\*RscIPv4")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001\*RscIPv6")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NetworkThrottlingIndex")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\SystemResponsiveness")]
    public void NetworkValue_AcceptsEveryValueTheOptimizerWrote(string fullPath)
    {
        Assert.True(RecoveryInputRules.TryNetworkRegistryValue(fullPath, out var subKeyPath, out var valueName));
        Assert.Equal(fullPath, $@"HKLM\{subKeyPath}\{valueName}");
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\EnableLUA")]
    [InlineData(@"HKCU\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\TcpAckFrequency")]
    [InlineData(@"HKEY_USERS\.DEFAULT\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\TcpAckFrequency")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\NotAGuid\TcpAckFrequency")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\Sub\TcpAckFrequency")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\DhcpNameServer")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\Properties\*RscIPv4")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000\*RscIPv4")]
    [InlineData(@"HKLM\TcpAckFrequency")]
    public void NetworkValue_RefusesAnythingElse(string fullPath)
    {
        Assert.False(RecoveryInputRules.TryNetworkRegistryValue(fullPath, out _, out _));
    }

    // ── GPU driver optimizer ──────────────────────────────────────────

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID73779", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global\NVTweak", "NVCplShaderCacheSize", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm", "DisableDynamicPstate", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm", "RmCudaSchedulingMode", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDdiDelay", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "Chill_Enabled", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "SurfaceFormatReplacements", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "ShaderCache", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "FlipQueueSize", "Binary")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0001", "EnableUlps", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0001", "PP_SclkDeepSleepDisable", "DWord")]
    public void GpuDriverValue_AcceptsEveryValueTheOptimizerWrote(string keyPath, string valueName, string kind)
    {
        Assert.True(RecoveryInputRules.TryGpuDriverValue(keyPath, valueName, kind, out _, out _, out _));
    }

    [Fact]
    public void GpuDriverValue_SplitsTheHiveFromTheRecordedPath()
    {
        Assert.True(RecoveryInputRules.TryGpuDriverValue(
            @"HKEY_CURRENT_USER\SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID73779", "DWord",
            out var hive, out var subKeyPath, out _));
        Assert.Equal(RecoveryHive.CurrentUser, hive);
        Assert.Equal(@"SOFTWARE\NVIDIA Corporation\Global\FTS", subKeyPath);

        Assert.True(RecoveryInputRules.TryGpuDriverValue(
            @"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "FlipQueueSize", "Binary",
            out hive, out subKeyPath, out var kind));
        Assert.Equal(RecoveryHive.LocalMachine, hive);
        Assert.Equal(DisplayClass + @"\0000\UMD", subKeyPath);
        Assert.Equal(Microsoft.Win32.RegistryValueKind.Binary, kind);
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", "DWord")]
    [InlineData(@"HKEY_USERS\.DEFAULT\SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID73779", "DWord")]
    [InlineData(@"HKEY_CLASSES_ROOT\exefile\shell\open\command", "", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay", "ExpandString")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay", "QWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "Binary")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD", "Debugger", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000\UMD\Sub", "ShaderCache", "String")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\Properties", "EnableUlps", "DWord")]
    [InlineData(@"HKEY_LOCAL_MACHINE\" + DisplayClass + @"\0000", "FlipQueueSize", "Binary")]
    [InlineData(@"HKEY_CURRENT_USER\" + DisplayClass + @"\0000", "EnableUlps", "DWord")]
    public void GpuDriverValue_RefusesAnythingElse(string keyPath, string valueName, string kind)
    {
        Assert.False(RecoveryInputRules.TryGpuDriverValue(keyPath, valueName, kind, out _, out _, out _));
    }

    // ── Scheduled tasks, services, overlays ───────────────────────────

    [Fact]
    public void SuppressedTasks_AreTheEighteenTasksThatWereSuppressed()
    {
        Assert.Equal(18, RecoveryInputRules.SuppressedTasks.Count);
        Assert.Contains(@"\Microsoft\Windows\Defrag\ScheduledDefrag", RecoveryInputRules.SuppressedTasks);
        Assert.Contains(@"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan", RecoveryInputRules.SuppressedTasks);
        Assert.DoesNotContain(@"\Microsoft\Windows\Defrag\ScheduledDefrag"" /tr ""cmd.exe", RecoveryInputRules.SuppressedTasks);
    }

    [Theory]
    [InlineData("SysMain", true)]
    [InlineData("wuauserv", true)]
    [InlineData("WdiSystemHost", true)]
    [InlineData(@"..\evil", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void ServiceName_OnlySyntaxIsChecked(string? name, bool expected)
    {
        Assert.Equal(expected, RecoveryInputRules.IsServiceName(name));
    }

    [Fact]
    public void ServiceName_RefusesOverlongNames()
    {
        Assert.True(RecoveryInputRules.IsServiceName(new string('s', 256)));
        Assert.False(RecoveryInputRules.IsServiceName(new string('s', 257)));
    }

    [Theory]
    [InlineData("Discord", true)]
    [InlineData("GameOverlayUI", true)]
    [InlineData("NVIDIA Share", true)]
    [InlineData("nvcontainer", true)]
    [InlineData("lsass", false)]
    [InlineData("csrss", false)]
    public void SuspendedOverlay_IsOneOfTheOverlaysThatWereSuspended(string name, bool expected)
    {
        Assert.Equal(expected, RecoveryInputRules.IsSuspendedOverlay(name));
    }

    // ── Game action records ───────────────────────────────────────────

    [Theory]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "String", false)]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"D:\Games\Overwatch\Overwatch.exe", "String", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "String", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "DWord", true)]
    [InlineData("HKLM", DisplayClass + @"\0002", "AntiLag_DevMode", "String", false)]
    // The driver override recorded the kind the value already had.
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "ExpandString", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "MultiString", true)]
    public void LegacyRegistryAction_AcceptsTheShapesTheRemovedActionsWrote(
        string hive, string path, string name, string kind, bool existed)
    {
        Assert.True(RecoveryInputRules.TryLegacyRegistryAction(hive, path, name, kind, existed, out _));
    }

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", "DWord", true)]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"C:\evil.exe", "String", false)]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "relative.exe", "String", false)]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", @"C:\Games\game.dll", "String", false)]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", @"C:\Games\game.exe", "Binary", true)]
    [InlineData("HKU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", @"C:\Games\game.exe", "String", false)]
    [InlineData(null, DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "String", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "Chill_Enabled", "String", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "Unknown", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "AntiLag_DevMode", "None", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD", "Chill_Enabled", "ExpandString", true)]
    [InlineData("HKLM", DisplayClass + @"\0000\UMD\Sub", "AntiLag_DevMode", "String", true)]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0000", "AntiLag_DevMode", "String", true)]
    public void LegacyRegistryAction_RefusesAnythingElse(
        string? hive, string path, string name, string kind, bool existed)
    {
        Assert.False(RecoveryInputRules.TryLegacyRegistryAction(hive, path, name, kind, existed, out _));
    }

    [Theory]
    [InlineData("GameShift_OW2_In", true)]
    [InlineData("GameShift_Valorant_In", true)]
    [InlineData("GameShift_LoL_In", true)]
    [InlineData("GameShift_Deadlock_In", true)]
    [InlineData("Block telemetry", false)]
    [InlineData("gameshift_ow2_in", false)]
    [InlineData(null, false)]
    public void LegacyFirewallRule_IsOneOfThePresetRules(string? ruleName, bool expected)
    {
        Assert.Equal(expected, RecoveryInputRules.IsLegacyFirewallRule(ruleName));
    }
}
