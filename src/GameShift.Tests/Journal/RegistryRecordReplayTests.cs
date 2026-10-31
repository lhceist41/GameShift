using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Tests.TestHelpers;
using Microsoft.Win32;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Journal records of the registry-writing modules, in the shape 3.9 wrote them, replayed into a
/// throwaway HKCU tree that stands in for both hives. A record restores exactly what it names, a
/// record that names anything else changes nothing, a missing key is never created, and the result
/// is Failed whenever something was not restored.
/// </summary>
public sealed class RegistryRecordReplayTests : IDisposable
{
    private const RecoveryHive Hklm = RecoveryHive.LocalMachine;
    private const RecoveryHive Hkcu = RecoveryHive.CurrentUser;
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string GamePerfOptions = Ifeo + @"\VALORANT-Win64-Shipping.exe\PerfOptions";
    private const string PoliciesSystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private readonly TestRecoveryRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    private void Seed(RecoveryHive hive, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
    {
        using var key = _registry.CreateKey(hive, path);
        key.SetValue(name, value, kind);
    }

    // ── MPO toggle ────────────────────────────────────────────────────

    private const string Dwm = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    [Fact]
    public void MpoRecord_RestoresEachRecordedValue()
    {
        Seed(Hklm, Dwm, "OverlayTestMode", 5);
        Seed(Hklm, Dwm, "EnableOverlay", 0);
        Seed(Hklm, GraphicsDrivers, "DisableOverlays", 1);

        var result = new MpoToggle { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord("""{"OverlayTestMode":null,"EnableOverlay":1,"DisableOverlays":0}""");

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Null(_registry.Value(Hklm, Dwm, "OverlayTestMode"));
        Assert.Equal(1, _registry.Value(Hklm, Dwm, "EnableOverlay"));
        Assert.Equal(0, _registry.Value(Hklm, GraphicsDrivers, "DisableOverlays"));
    }

    [Fact]
    public void MpoRecord_MissingKey_IsFailedAndNotCreated()
    {
        Seed(Hklm, Dwm, "OverlayTestMode", 5);

        var result = new MpoToggle { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord("""{"OverlayTestMode":0,"DisableOverlays":0}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains("DisableOverlays", result.ErrorMessage);
        Assert.Equal(0, _registry.Value(Hklm, Dwm, "OverlayTestMode"));
        Assert.Null(_registry.Open(Hklm, GraphicsDrivers));
    }

    [Fact]
    public void MpoRecord_ValueThatIsNotAnInt_IsFailedAndNotWritten()
    {
        Seed(Hklm, Dwm, "OverlayTestMode", 5);

        var result = new MpoToggle { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord("""{"OverlayTestMode":"0"}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(5, _registry.Value(Hklm, Dwm, "OverlayTestMode"));
    }

    // ── Visual effects, timer resolution ──────────────────────────────

    private const string Personalize = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [Fact]
    public void VisualEffectsRecord_RestoresTransparencyAndAnimations()
    {
        Seed(Hkcu, Personalize, "EnableTransparency", 0);
        int animationCalls = 0;

        var result = new VisualEffectReducer
        {
            RecoveryRegistry = _registry.Registry,
            EnableAnimation = () => ++animationCalls > 0,
        }.RevertFromRecord("""{"EnableTransparency":1,"__AnimationEnabled__":true}""");

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(1, _registry.Value(Hkcu, Personalize, "EnableTransparency"));
        Assert.Equal(1, animationCalls);
    }

    [Fact]
    public void VisualEffectsRecord_AnimationsNotRestored_IsFailed()
    {
        Seed(Hkcu, Personalize, "EnableTransparency", 0);

        var result = new VisualEffectReducer { RecoveryRegistry = _registry.Registry, EnableAnimation = () => false }
            .RevertFromRecord("""{"EnableTransparency":1,"__AnimationEnabled__":true}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains("animations", result.ErrorMessage);
        Assert.Equal(1, _registry.Value(Hkcu, Personalize, "EnableTransparency"));
    }

    [Fact]
    public void VisualEffectsRecord_TransparencyKeyMissing_IsFailed()
    {
        var result = new VisualEffectReducer { RecoveryRegistry = _registry.Registry, EnableAnimation = () => true }
            .RevertFromRecord("""{"EnableTransparency":1,"__AnimationEnabled__":false}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Null(_registry.Open(Hkcu, Personalize));
    }

    private const string Kernel = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

    [Fact]
    public void TimerRecord_RestoresTheGlobalRequestValue()
    {
        Seed(Hklm, Kernel, "GlobalTimerResolutionRequests", 1);

        var result = new TimerResolutionManager { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord("""{"GlobalTimerResolutionRequests":null}""");

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Null(_registry.Value(Hklm, Kernel, "GlobalTimerResolutionRequests"));
    }

    [Fact]
    public void TimerRecord_KernelKeyMissing_IsFailed()
    {
        var result = new TimerResolutionManager { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord("""{"GlobalTimerResolutionRequests":0}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Null(_registry.Open(Hklm, Kernel));
    }

    // ── IFEO PerfOptions (ProcessPriorityBooster, HybridCpuDetector) ──

    private static string IfeoRecord(string keyPath, string? originalValuesJson) =>
        System.Text.Json.JsonSerializer.Serialize(new { ifeoKeys = new[] { new { keyPath, originalValuesJson } } });

    [Fact]
    public void BoosterRecord_CreatedByGameShift_DeletesTheValueAndTheEmptyKey()
    {
        Seed(Hklm, GamePerfOptions, "CpuPriorityClass", 3);

        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, null));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Null(_registry.Open(Hklm, GamePerfOptions));
        Assert.NotNull(_registry.Open(Hklm, Ifeo + @"\VALORANT-Win64-Shipping.exe"));
    }

    [Fact]
    public void BoosterRecord_CreatedByGameShift_KeepsAnotherModulesValue()
    {
        Seed(Hklm, GamePerfOptions, "CpuPriorityClass", 3);
        Seed(Hklm, GamePerfOptions, "CpuAffinityMask", 0xFF);

        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, null));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Null(_registry.Value(Hklm, GamePerfOptions, "CpuPriorityClass"));
        Assert.Equal(0xFF, _registry.Value(Hklm, GamePerfOptions, "CpuAffinityMask"));
    }

    [Fact]
    public void BoosterRecord_WithOriginal_RestoresIt()
    {
        Seed(Hklm, GamePerfOptions, "CpuPriorityClass", 3);

        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, """{"CpuPriorityClass":2}"""));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(2, _registry.Value(Hklm, GamePerfOptions, "CpuPriorityClass"));
    }

    // The key existed without a priority value: undoing the boost deletes the value, as the live
    // revert does, and keeps the key.
    [Fact]
    public void BoosterRecord_KeyExistedWithoutTheValue_DeletesOnlyTheValue()
    {
        Seed(Hklm, GamePerfOptions, "CpuPriorityClass", 3);

        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, "{}"));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.NotNull(_registry.Open(Hklm, GamePerfOptions));
        Assert.Null(_registry.Value(Hklm, GamePerfOptions, "CpuPriorityClass"));
    }

    [Fact]
    public void BoosterRecord_OriginalButKeyMissing_IsFailedAndNotCreated()
    {
        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, """{"CpuPriorityClass":2}"""));

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Null(_registry.Open(Hklm, GamePerfOptions));
    }

    [Fact]
    public void BoosterRecord_AnotherKey_IsRefusedAndUntouched()
    {
        Seed(Hklm, PoliciesSystem, "EnableLUA", 1);
        Seed(Hklm, PoliciesSystem, "CpuPriorityClass", 7);

        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(PoliciesSystem, """{"CpuPriorityClass":0}"""));

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(1, _registry.Value(Hklm, PoliciesSystem, "EnableLUA"));
        Assert.Equal(7, _registry.Value(Hklm, PoliciesSystem, "CpuPriorityClass"));
    }

    [Fact]
    public void BoosterRecord_AnotherValueName_IsRefusedAndUntouched()
    {
        Seed(Hklm, GamePerfOptions, "CpuPriorityClass", 3);

        var result = new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, """{"Debugger":1}"""));

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Null(_registry.Value(Hklm, GamePerfOptions, "Debugger"));
        Assert.Equal(3, _registry.Value(Hklm, GamePerfOptions, "CpuPriorityClass"));
    }

    [Fact]
    public void HybridRecord_CreatedByGameShift_DeletesTheMaskAndTheEmptyKey()
    {
        Seed(Hklm, GamePerfOptions, "CpuAffinityMask", 0xFF);

        var result = new HybridCpuDetector { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, null));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Null(_registry.Open(Hklm, GamePerfOptions));
    }

    [Fact]
    public void HybridRecord_WithOriginal_RestoresIt_AndRefusesAnotherValueName()
    {
        Seed(Hklm, GamePerfOptions, "CpuAffinityMask", 0xFF);

        var restored = new HybridCpuDetector { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, """{"CpuAffinityMask":15}"""));
        var refused = new HybridCpuDetector { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(IfeoRecord(GamePerfOptions, """{"CpuPriorityClass":1}"""));

        Assert.Equal(OptimizationState.Reverted, restored.State);
        Assert.Equal(OptimizationState.Failed, refused.State);
        Assert.Equal(15, _registry.Value(Hklm, GamePerfOptions, "CpuAffinityMask"));
        Assert.Null(_registry.Value(Hklm, GamePerfOptions, "CpuPriorityClass"));
    }

    // ── Session system tweaks ─────────────────────────────────────────

    private const string SystemProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string Games = SystemProfile + @"\Tasks\Games";
    private const string HidDevice = @"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\0003";

    // The shape 3.9 serialized: PascalCase, Kind is the kind GameShift wrote, Value the original.
    private const string SessionTweaksRecord = """
        {"Backups":[
          {"Hklm":true,"Path":"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile","Name":"NetworkThrottlingIndex","Existed":true,"Kind":"DWord","Value":10},
          {"Hklm":true,"Path":"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games","Name":"Scheduling Category","Existed":true,"Kind":"String","Value":"Medium"},
          {"Hklm":true,"Path":"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games","Name":"Clock Rate","Existed":false,"Kind":"DWord","Value":null},
          {"Hklm":true,"Path":"SYSTEM\\CurrentControlSet\\Control\\Class\\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\\0003","Name":"SelectiveSuspendEnabled","Existed":true,"Kind":"DWord","Value":1}
        ],"Aspm":2}
        """;

    private void SeedSessionTweaksAsApplied()
    {
        Seed(Hklm, SystemProfile, "NetworkThrottlingIndex", -1);
        Seed(Hklm, Games, "Scheduling Category", "High", RegistryValueKind.String);
        Seed(Hklm, Games, "Clock Rate", 10000);
        Seed(Hklm, HidDevice, "SelectiveSuspendEnabled", 0);
    }

    [Fact]
    public void SessionTweaksRecord_RestoresEveryValueAndAspm()
    {
        SeedSessionTweaksAsApplied();
        var powercfg = new List<string>();

        var result = new SessionSystemTweaksOptimizer
        {
            RecoveryRegistry = _registry.Registry,
            Powercfg = args => { powercfg.Add(args); return true; },
        }.RevertFromRecord(SessionTweaksRecord);

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(10, _registry.Value(Hklm, SystemProfile, "NetworkThrottlingIndex"));
        Assert.Equal("Medium", _registry.Value(Hklm, Games, "Scheduling Category"));
        Assert.Null(_registry.Value(Hklm, Games, "Clock Rate"));
        Assert.Equal(1, _registry.Value(Hklm, HidDevice, "SelectiveSuspendEnabled"));
        Assert.Equal(
            new[]
            {
                "/setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 2",
                "/setactive SCHEME_CURRENT",
            },
            powercfg);
    }

    [Fact]
    public void SessionTweaksRecord_AspmNotRestored_IsFailed()
    {
        SeedSessionTweaksAsApplied();

        var result = new SessionSystemTweaksOptimizer
        {
            RecoveryRegistry = _registry.Registry,
            Powercfg = args => !args.StartsWith("/setacvalueindex", StringComparison.Ordinal),
        }.RevertFromRecord(SessionTweaksRecord);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains("PCIe", result.ErrorMessage);
        Assert.Equal(10, _registry.Value(Hklm, SystemProfile, "NetworkThrottlingIndex"));
    }

    [Fact]
    public void SessionTweaksRecord_ForeignEntry_IsRefusedWhileTheOthersAreRestored()
    {
        SeedSessionTweaksAsApplied();
        Seed(Hklm, PoliciesSystem, "EnableLUA", 1);
        const string foreign =
            """{"Hklm":true,"Path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System","Name":"EnableLUA","Existed":true,"Kind":"DWord","Value":0},""";
        var record = SessionTweaksRecord
            .Replace("\"Aspm\":2", "\"Aspm\":null")
            .Replace("\"Backups\":[", "\"Backups\":[" + foreign);

        var result = new SessionSystemTweaksOptimizer { RecoveryRegistry = _registry.Registry, Powercfg = _ => true }
            .RevertFromRecord(record);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(1, _registry.Value(Hklm, PoliciesSystem, "EnableLUA"));
        Assert.Equal(10, _registry.Value(Hklm, SystemProfile, "NetworkThrottlingIndex"));
    }

    [Fact]
    public void SessionTweaksRecord_OriginalOfAnotherKind_IsFailedAndUntouched()
    {
        Seed(Hklm, Games, "Priority", 6);

        var result = new SessionSystemTweaksOptimizer { RecoveryRegistry = _registry.Registry, Powercfg = _ => true }
            .RevertFromRecord("""{"Backups":[{"Hklm":true,"Path":"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games","Name":"Priority","Existed":true,"Kind":"DWord","Value":"2"}],"Aspm":null}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(6, _registry.Value(Hklm, Games, "Priority"));
    }

    // ── Network optimizer ─────────────────────────────────────────────

    private const string TcpInterface = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}";
    private const string NetAdapter = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001";

    private const string NetworkRecord = """
        {
          "Registry:HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\\TcpAckFrequency":null,
          "Registry:HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\\TCPNoDelay":0,
          "Registry:HKLM\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}\\0001\\*InterruptModeration":"1",
          "Service:DoSvc":"Running",
          "Netsh:rsc":"enabled"
        }
        """;

    [Fact]
    public void NetworkRecord_RestoresRegistryServiceAndRsc()
    {
        Seed(Hklm, TcpInterface, "TcpAckFrequency", 1);
        Seed(Hklm, TcpInterface, "TCPNoDelay", 1);
        Seed(Hklm, NetAdapter, "*InterruptModeration", "0", RegistryValueKind.String);
        var started = new List<string>();
        var netsh = new List<string>();

        var result = new NetworkOptimizer
        {
            RecoveryRegistry = _registry.Registry,
            StartService = name => { started.Add(name); return true; },
            Netsh = args => { netsh.Add(args); return (0, string.Empty); },
        }.RevertFromRecord(NetworkRecord);

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Null(_registry.Value(Hklm, TcpInterface, "TcpAckFrequency"));
        Assert.Equal(0, _registry.Value(Hklm, TcpInterface, "TCPNoDelay"));
        Assert.Equal("1", _registry.Value(Hklm, NetAdapter, "*InterruptModeration"));
        Assert.Equal(new[] { "DoSvc" }, started);
        Assert.Equal(new[] { "int tcp set global rsc=enabled" }, netsh);
    }

    [Fact]
    public void NetworkRecord_ServiceNotStarted_IsFailed()
    {
        Seed(Hklm, TcpInterface, "TcpAckFrequency", 1);
        Seed(Hklm, TcpInterface, "TCPNoDelay", 1);
        Seed(Hklm, NetAdapter, "*InterruptModeration", "0", RegistryValueKind.String);

        var result = new NetworkOptimizer
        {
            RecoveryRegistry = _registry.Registry,
            StartService = _ => false,
            Netsh = _ => (0, string.Empty),
        }.RevertFromRecord(NetworkRecord);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains("Service:DoSvc", result.ErrorMessage);
    }

    [Fact]
    public void NetworkRecord_ForeignValuesAndServices_AreRefusedAndUntouched()
    {
        Seed(Hklm, PoliciesSystem, "EnableLUA", 1);
        var started = new List<string>();

        var result = new NetworkOptimizer
        {
            RecoveryRegistry = _registry.Registry,
            StartService = name => { started.Add(name); return true; },
            Netsh = _ => (0, string.Empty),
        }.RevertFromRecord("""
            {
              "Registry:HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\EnableLUA":0,
              "Service:RemoteRegistry":"Running",
              "Netsh:rsc":"enabled store=persistent"
            }
            """);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains("Refused 3", result.ErrorMessage);
        Assert.Equal(1, _registry.Value(Hklm, PoliciesSystem, "EnableLUA"));
        Assert.Empty(started);
    }

    [Fact]
    public void NetworkRecord_MissingInterfaceKey_IsFailedAndNotCreated()
    {
        var result = new NetworkOptimizer { RecoveryRegistry = _registry.Registry, Netsh = _ => (0, string.Empty) }
            .RevertFromRecord("""{"Registry:HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{2D3F6B1A-0C55-4F10-9C1B-3A4D5E6F7A8B}\\TCPNoDelay":0}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Null(_registry.Open(Hklm, TcpInterface));
    }

    // ── GPU driver optimizer ──────────────────────────────────────────

    private const string AmdAdapter = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000";
    private const string AmdUmd = AmdAdapter + @"\UMD";
    private const string NvidiaFts = @"SOFTWARE\NVIDIA Corporation\Global\FTS";

    // The shape 3.9 serialized: the full HKEY_ path, the kind GameShift wrote, and the original
    // value (a byte array as base64).
    private const string GpuRecord = """
        [
          {"KeyPath":"HKEY_CURRENT_USER\\SOFTWARE\\NVIDIA Corporation\\Global\\FTS","ValueName":"EnableRID73779","Existed":true,"Kind":"DWord","Value":0},
          {"KeyPath":"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}\\0000\\UMD","ValueName":"AntiLag_DevMode","Existed":true,"Kind":"String","Value":"0"},
          {"KeyPath":"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}\\0000\\UMD","ValueName":"FlipQueueSize","Existed":true,"Kind":"Binary","Value":"MgA="},
          {"KeyPath":"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}\\0000","ValueName":"EnableUlps","Existed":false,"Kind":"DWord","Value":null},
          {"KeyPath":"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers","ValueName":"TdrDelay","Existed":false,"Kind":"DWord","Value":null}
        ]
        """;

    private void SeedGpuAsApplied()
    {
        Seed(Hkcu, NvidiaFts, "EnableRID73779", 1);
        Seed(Hklm, AmdUmd, "AntiLag_DevMode", "1", RegistryValueKind.String);
        Seed(Hklm, AmdUmd, "FlipQueueSize", new byte[] { 0x31, 0x00 }, RegistryValueKind.Binary);
        Seed(Hklm, AmdAdapter, "EnableUlps", 0);
        Seed(Hklm, GraphicsDrivers, "TdrDelay", 8);
    }

    [Fact]
    public void GpuRecord_RestoresEveryValueWithItsKind()
    {
        SeedGpuAsApplied();

        var result = new GpuDriverOptimizer { RecoveryRegistry = _registry.Registry }.RevertFromRecord(GpuRecord);

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(0, _registry.Value(Hkcu, NvidiaFts, "EnableRID73779"));
        Assert.Equal("0", _registry.Value(Hklm, AmdUmd, "AntiLag_DevMode"));
        Assert.Equal(new byte[] { 0x32, 0x00 }, _registry.Value(Hklm, AmdUmd, "FlipQueueSize"));
        Assert.Null(_registry.Value(Hklm, AmdAdapter, "EnableUlps"));
        Assert.Null(_registry.Value(Hklm, GraphicsDrivers, "TdrDelay"));
    }

    // Registry.SetValue on a full path, which earlier code used, creates every missing key.
    [Fact]
    public void GpuRecord_MissingKey_IsFailedAndNotCreated()
    {
        var result = new GpuDriverOptimizer { RecoveryRegistry = _registry.Registry }.RevertFromRecord("""
            [{"KeyPath":"HKEY_LOCAL_MACHINE\\SOFTWARE\\NVIDIA Corporation\\Global\\NVTweak","ValueName":"NVCplShaderCacheSize","Existed":true,"Kind":"DWord","Value":4}]
            """);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Null(_registry.Open(Hklm, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak"));
    }

    [Fact]
    public void GpuRecord_ExistedWithoutRecordedOriginal_IsFailedAndUntouched()
    {
        SeedGpuAsApplied();

        var result = new GpuDriverOptimizer { RecoveryRegistry = _registry.Registry }.RevertFromRecord("""
            [{"KeyPath":"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers","ValueName":"TdrDelay","Existed":true,"Kind":"DWord","Value":null}]
            """);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(8, _registry.Value(Hklm, GraphicsDrivers, "TdrDelay"));
    }

    [Theory]
    [InlineData("""{"KeyPath":"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System","ValueName":"EnableLUA","Existed":true,"Kind":"DWord","Value":0}""")]
    [InlineData("""{"KeyPath":"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers","ValueName":"TdrDelay","Existed":true,"Kind":"ExpandString","Value":"%TEMP%"}""")]
    [InlineData("""{"KeyPath":"HKEY_USERS\\.DEFAULT\\SOFTWARE\\NVIDIA Corporation\\Global\\FTS","ValueName":"EnableRID73779","Existed":true,"Kind":"DWord","Value":0}""")]
    public void GpuRecord_ForeignEntry_IsRefusedAndUntouched(string entry)
    {
        SeedGpuAsApplied();
        Seed(Hklm, PoliciesSystem, "EnableLUA", 1);

        var result = new GpuDriverOptimizer { RecoveryRegistry = _registry.Registry }.RevertFromRecord($"[{entry}]");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(1, _registry.Value(Hklm, PoliciesSystem, "EnableLUA"));
        Assert.Equal(8, _registry.Value(Hklm, GraphicsDrivers, "TdrDelay"));
    }

    // ── Competitive mode ──────────────────────────────────────────────

    private const string DiscordOverlay = @"SOFTWARE\Discord\Modules\discord_overlay2";

    [Fact]
    public void CompetitiveRecord_RestoresDiscordAndResumesEachOverlay()
    {
        Seed(Hkcu, DiscordOverlay, "enabled", 0);
        var resumed = new List<(int, string)>();

        var result = new CompetitiveMode
        {
            RecoveryRegistry = _registry.Registry,
            ResumeSuspended = (pid, name) => { resumed.Add((pid, name)); return true; },
        }.RevertFromRecord("""{"discordModified":true,"discordPrev":1,"suspended":[{"pid":4100,"name":"Discord"},{"pid":4200,"name":"GameOverlayUI"}]}""");

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(1, _registry.Value(Hkcu, DiscordOverlay, "enabled"));
        Assert.Equal(new[] { (4100, "Discord"), (4200, "GameOverlayUI") }, resumed);
    }

    [Fact]
    public void CompetitiveRecord_ProcessStillSuspended_IsFailed()
    {
        var result = new CompetitiveMode { RecoveryRegistry = _registry.Registry, ResumeSuspended = (_, _) => false }
            .RevertFromRecord("""{"discordModified":false,"discordPrev":null,"suspended":[{"pid":4100,"name":"Discord"}]}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains("Discord (PID 4100)", result.ErrorMessage);
    }

    [Fact]
    public void CompetitiveRecord_AnotherProcess_IsRefusedAndNeverResumed()
    {
        var resumed = new List<string>();

        var result = new CompetitiveMode
        {
            RecoveryRegistry = _registry.Registry,
            ResumeSuspended = (_, name) => { resumed.Add(name); return true; },
        }.RevertFromRecord("""{"discordModified":false,"discordPrev":null,"suspended":[{"pid":4,"name":"System"},{"pid":4100,"name":"nvcontainer"}]}""");

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Equal(new[] { "nvcontainer" }, resumed);
    }
}
