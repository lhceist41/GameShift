using System.Management;
using System.Text.Json;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Journal;
using GameShift.Core.Optimization.Gpu;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Detects GPU vendor (NVIDIA/AMD) via WMI and applies vendor-specific registry optimizations
/// during gaming sessions.
///
/// - GPU vendor detection via WMI (NVIDIA or AMD)
/// - NVIDIA optimizations (Low Latency Mode Ultra, Shader Cache 10GB)
/// - AMD optimizations (Anti-Lag enable, Radeon Chill disable, Surface Format, Shader Cache)
/// - Snapshot-based Apply/Revert for all registry changes
/// - GameProfile toggle integration (EnableGpuOptimization master toggle)
///
/// GPU registry changes are captured in the in-memory SystemStateSnapshot (RegistryValues) for
/// end-of-session revert, and (via IJournaledOptimization) recorded in the state journal so the
/// boot-recovery path can restore them after a crash.
/// </summary>
public class GpuDriverOptimizer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private bool _isApplied;
    private SystemContext? _context;

    // Cached GPU vendor detection result (only detect once per Apply)
    private GpuVendor _detectedVendor = GpuVendor.Unknown;
    private string? _detectedGpuName;

    // Track all registry changes for clean revert
    private readonly List<RegistryChange> _registryChanges = new();

    // Vendor-specific SDK managers (loaded lazily based on detected vendor)
    private NvApiDrsManager? _nvApiDrs;
    private AdlxManager? _adlx;
    private Dictionary<uint, uint?>? _nvApiDrsBackup;

    // ── Display Adapter Class GUID (standard for all GPUs) ───────────
    private const string DisplayAdapterClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DriverClassBasePath = @"SYSTEM\CurrentControlSet\Control\Class\" + DisplayAdapterClassGuid;

    // ── NVIDIA registry paths ───────────────────────────────
    private const string NvidiaFtsPath = @"HKEY_CURRENT_USER\SOFTWARE\NVIDIA Corporation\Global\FTS";
    private const string NvidiaFtsValueName = "EnableRID73779";

    private const string NvidiaTweakPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string NvidiaTweakValueName = "NVCplShaderCacheSize";

    public const string OptimizationId = "GPU Driver Optimizer";

    public string Name => OptimizationId;

    public string Description => "Applies vendor-specific GPU registry optimizations (NVIDIA/AMD) for reduced latency and better performance";

    public bool IsApplied => _isApplied;

    /// <summary>
    /// GPU optimization is always available. If no supported GPU is detected,
    /// ApplyAsync returns false gracefully without error.
    /// </summary>
    public bool IsAvailable => true;

    /// <summary>
    /// Detects GPU vendor and applies vendor-specific registry optimizations.
    /// All registry values are snapshotted before modification for crash recovery.
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        CanApply(new SystemContext { Profile = profile, Snapshot = snapshot });
        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    public OptimizationResult Apply()
    {
        if (_context == null)
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "No context");

        bool success = ApplyInternal(_context.Snapshot, _context.Profile);

        var entries = _registryChanges.Select(c => new GpuChangeEntry
        {
            KeyPath = c.KeyPath,
            ValueName = c.ValueName,
            Existed = c.PreviouslyExisted,
            Kind = c.ValueKind.ToString(),
            Value = c.PreviouslyExisted ? c.PreviousValue : null
        }).ToList();

        return new OptimizationResult(
            OptimizationId,
            JsonSerializer.Serialize(entries),
            string.Empty,
            success ? OptimizationState.Applied : OptimizationState.Failed);
    }

    private bool ApplyInternal(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            _logger.Information(
                "[GpuDriverOptimizer] Applying GPU driver optimizations at {Timestamp}",
                DateTime.UtcNow.ToString("o"));

            // ── Step 1: Detect GPU vendor ──
            _detectedVendor = DetectGpuVendor();

            if (_detectedVendor == GpuVendor.Unknown)
            {
                _logger.Warning(
                    "[GpuDriverOptimizer] No supported GPU vendor detected (NVIDIA or AMD required). Skipping GPU optimizations.");
                return false;
            }

            _logger.Information(
                "[GpuDriverOptimizer] Detected {Vendor} GPU: {GpuName}",
                _detectedVendor,
                _detectedGpuName ?? "Unknown");

            // ── Step 2: Apply vendor-specific optimizations ──
            bool success = _detectedVendor switch
            {
                GpuVendor.Nvidia => ApplyNvidiaOptimizations(snapshot, profile),
                GpuVendor.Amd => ApplyAmdOptimizations(snapshot, profile),
                _ => false
            };

            // ── Step 3: TDR timeout extension (all vendors) ──
            ApplyTdrTweaks(snapshot);

            // Mark applied if ANY registry change was recorded - including TDR-only changes when the
            // vendor-specific step failed - so Revert/watchdog undoes them instead of orphaning them.
            _isApplied = _registryChanges.Count > 0;

            if (success)
            {
                _logger.Information(
                    "[GpuDriverOptimizer] GPU optimizations applied successfully ({Count} registry values modified)",
                    _registryChanges.Count);
            }
            else if (_isApplied)
            {
                _logger.Warning(
                    "[GpuDriverOptimizer] GPU vendor step failed, but {Count} change(s) (e.g. TDR) were recorded and will be reverted",
                    _registryChanges.Count);
            }
            else
            {
                _logger.Warning(
                    "[GpuDriverOptimizer] GPU optimizations partially or fully failed");
            }

            // Treat any recorded change as "applied" so the engine journals it for clean revert.
            return success || _isApplied;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "[GpuDriverOptimizer] Failed to apply GPU driver optimizations");
            return false;
        }
    }

    /// <summary>
    /// Reverts all GPU registry changes using internally tracked previous values.
    /// Logs advisory that some settings may require a driver restart to take full effect.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!_isApplied) return Task.FromResult(true);
        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    public bool Verify() => _isApplied;

    public OptimizationResult Revert()
    {
        if (!_isApplied)
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);

        try
        {
            _logger.Information(
                "[GpuDriverOptimizer] Reverting GPU driver optimizations at {Timestamp}",
                DateTime.UtcNow.ToString("o"));

            int successCount = 0;
            int failCount = 0;

            // Revert in reverse order (LIFO)
            for (int i = _registryChanges.Count - 1; i >= 0; i--)
            {
                var change = _registryChanges[i];
                try
                {
                    if (change.PreviouslyExisted)
                    {
                        // Restore previous value
                        Registry.SetValue(change.KeyPath, change.ValueName, change.PreviousValue!, change.ValueKind);
                        _logger.Debug(
                            "[GpuDriverOptimizer] Restored {KeyPath}\\{ValueName} = {Value}",
                            change.KeyPath,
                            change.ValueName,
                            change.PreviousValue);
                    }
                    else
                    {
                        // Value did not exist before - delete it
                        DeleteRegistryValue(change.KeyPath, change.ValueName);
                        _logger.Debug(
                            "[GpuDriverOptimizer] Deleted {KeyPath}\\{ValueName} (was not present before apply)",
                            change.KeyPath,
                            change.ValueName);
                    }

                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    _logger.Warning(
                        ex,
                        "[GpuDriverOptimizer] Failed to revert {KeyPath}\\{ValueName}",
                        change.KeyPath,
                        change.ValueName);
                }
            }

            _registryChanges.Clear();

            // NvAPI DRS cleanup (disabled pending struct validation, but clean up if ever enabled)
            if (_nvApiDrs != null)
            {
                try
                {
                    if (_nvApiDrsBackup != null)
                        _nvApiDrs.RestoreSettings(_nvApiDrsBackup);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[GpuDriverOptimizer] Failed to restore NvAPI DRS settings");
                }
                _nvApiDrs.Dispose();
                _nvApiDrs = null;
                _nvApiDrsBackup = null;
            }

            // Revert ADLX Anti-Lag
            if (_adlx != null)
            {
                try
                {
                    _adlx.DisableAntiLag();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[GpuDriverOptimizer] Failed to revert ADLX Anti-Lag");
                }
                _adlx.Dispose();
                _adlx = null;
            }

            _isApplied = false;
            _detectedVendor = GpuVendor.Unknown;
            _detectedGpuName = null;

            _logger.Information(
                "[GpuDriverOptimizer] Revert complete - {SuccessCount} restored, {FailCount} failed",
                successCount,
                failCount);

            _logger.Warning(
                "[GpuDriverOptimizer] Some GPU driver settings may require a driver restart to take full effect");

            return new OptimizationResult(OptimizationId, string.Empty, string.Empty,
                failCount == 0 ? OptimizationState.Reverted : OptimizationState.Failed);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "[GpuDriverOptimizer] Failed to revert GPU driver optimizations");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Watchdog recovery: restores the registry changes recorded in the journal OriginalValue JSON.
    /// SDK-level driver settings (NvAPI DRS / ADLX) need the live session and cannot be reverted
    /// statelessly, so only the persistent registry state (P-states, TDR, shader cache, etc.) is restored.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<GpuChangeEntry>>(originalValueJson);
            if (entries == null)
            {
                // Apply always journals a list (at minimum "[]"); a literal JSON null means the
                // record is corrupt. Report failure instead of claiming a successful revert.
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
                    "Corrupt journal record (null entry list)");
            }

            int successCount = 0, failCount = 0;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                try
                {
                    if (!e.Existed)
                    {
                        DeleteRegistryValue(e.KeyPath, e.ValueName);
                    }
                    else if (e.Value is JsonElement el)
                    {
                        var kind = Enum.TryParse<RegistryValueKind>(e.Kind, out var k) ? k : RegistryValueKind.DWord;
                        object restored = kind switch
                        {
                            RegistryValueKind.Binary => el.GetBytesFromBase64(),
                            RegistryValueKind.String or RegistryValueKind.ExpandString => el.GetString() ?? "",
                            RegistryValueKind.QWord => el.GetInt64(),
                            _ => el.GetInt32()
                        };
                        Registry.SetValue(e.KeyPath, e.ValueName, restored, kind);
                    }
                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    _logger.Warning(ex, "[GpuDriverOptimizer] RevertFromRecord failed for {KeyPath}\\{ValueName}", e.KeyPath, e.ValueName);
                }
            }

            _logger.Information("[GpuDriverOptimizer] RevertFromRecord - {Success} restored, {Fail} failed", successCount, failCount);
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty,
                failCount == 0 ? OptimizationState.Reverted : OptimizationState.Failed);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[GpuDriverOptimizer] RevertFromRecord failed");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    private sealed class GpuChangeEntry
    {
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public bool Existed { get; set; }
        public string Kind { get; set; } = "";
        public object? Value { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════
    // GPU Vendor Detection
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detects GPU vendor via WMI query on Win32_VideoController.
    /// Checks AdapterCompatibility and Name for NVIDIA or AMD.
    /// For multi-GPU systems, uses the first matching NVIDIA or AMD adapter.
    /// </summary>
    private GpuVendor DetectGpuVendor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AdapterCompatibility, Name FROM Win32_VideoController");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    string? compatibility = obj["AdapterCompatibility"]?.ToString();
                    string? name = obj["Name"]?.ToString();

                    _logger.Debug(
                        "[GpuDriverOptimizer] Found GPU - AdapterCompatibility: {Compatibility}, Name: {Name}",
                        compatibility ?? "<null>",
                        name ?? "<null>");

                    // Check for NVIDIA
                    if (ContainsIgnoreCase(compatibility, "NVIDIA") ||
                        ContainsIgnoreCase(name, "NVIDIA"))
                    {
                        _detectedGpuName = name;
                        return GpuVendor.Nvidia;
                    }

                    // Check for AMD
                    if (ContainsIgnoreCase(compatibility, "AMD") ||
                        ContainsIgnoreCase(compatibility, "Advanced Micro Devices") ||
                        ContainsIgnoreCase(name, "AMD") ||
                        ContainsIgnoreCase(name, "Radeon"))
                    {
                        _detectedGpuName = name;
                        return GpuVendor.Amd;
                    }
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[GpuDriverOptimizer] WMI query for GPU detection failed");
        }

        return GpuVendor.Unknown;
    }

    // ════════════════════════════════════════════════════════════════════
    // NVIDIA Optimizations
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies NVIDIA-specific registry optimizations:
    /// - Low Latency Mode Ultra (EnableRID73779 = 1)
    /// - Shader Cache Size 10GB (NVCplShaderCacheSize = 10240)
    /// - Power management and DRS profile settings are logged as requiring NVIDIA Control Panel.
    /// </summary>
    private bool ApplyNvidiaOptimizations(SystemStateSnapshot snapshot, GameProfile profile)
    {
        bool anySuccess = false;

        // ── Low Latency Mode Ultra ──
        if (profile.EnableLowLatencyMode)
        {
            try
            {
                SnapshotAndSetRegistryValue(
                    snapshot,
                    NvidiaFtsPath,
                    NvidiaFtsValueName,
                    1,
                    RegistryValueKind.DWord,
                    "NVIDIA Low Latency Mode Ultra");
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "[GpuDriverOptimizer] Failed to apply NVIDIA Low Latency Mode Ultra");
            }
        }

        // ── Shader Cache Size 16GB ──
        if (profile.OptimizeShaderCache)
        {
            try
            {
                SnapshotAndSetRegistryValue(
                    snapshot,
                    NvidiaTweakPath,
                    NvidiaTweakValueName,
                    16384,
                    RegistryValueKind.DWord,
                    "NVIDIA Shader Cache Size 16GB");
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "[GpuDriverOptimizer] Failed to apply NVIDIA Shader Cache Size");
            }
        }

        // ── NvAPI DRS Profile Settings ──
        // NvAPI DRS uses native P/Invoke with complex struct layouts that vary by driver
        // version. Disabled until struct validation is complete - native access violations
        // in nvapi64.dll cannot be caught by managed exception handlers and crash the app.
        // The registry-based settings above (Low Latency, Shader Cache) cover the most
        // impactful optimizations. DRS settings (power management, pre-render limit) can
        // be configured via NVIDIA Control Panel > Manage 3D Settings.
        _logger.Information(
            "[GpuDriverOptimizer] NvAPI DRS is disabled pending struct layout validation. " +
            "Power management and pre-render settings available in NVIDIA Control Panel.");

        // ── NVIDIA nvlddmkm kernel driver tweaks ──
        const string nvlddmkmPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";

        // Force P-State 0 - prevent clock ramping during gaming for consistent frame times
        try
        {
            SnapshotAndSetRegistryValue(
                snapshot, nvlddmkmPath,
                "DisableDynamicPstate", 1, RegistryValueKind.DWord,
                "NVIDIA Force P-State 0");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to set DisableDynamicPstate");
        }

        // CUDA spin mode - lowest latency scheduling for compute/shader work
        try
        {
            SnapshotAndSetRegistryValue(
                snapshot, nvlddmkmPath,
                "RmCudaSchedulingMode", 1, RegistryValueKind.DWord,
                "NVIDIA CUDA Spin Scheduling");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to set RmCudaSchedulingMode");
        }

        return anySuccess;
    }

    // ════════════════════════════════════════════════════════════════════
    // AMD Optimizations
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies AMD-specific registry optimizations via the UMD (User Mode Driver) subkey:
    /// - Anti-Lag = Enabled (standard Anti-Lag only, NOT Anti-Lag+ which causes bans)
    /// - Radeon Chill = Disabled
    /// - Surface Format Optimization = Enabled
    /// - Shader Cache = Enabled
    /// </summary>
    private bool ApplyAmdOptimizations(SystemStateSnapshot snapshot, GameProfile profile)
    {
        bool anySuccess = false;

        // Find the AMD driver subkey
        string? amdDriverSubkey = FindDriverSubkey("AMD", "Advanced Micro Devices");
        if (amdDriverSubkey == null)
        {
            _logger.Warning(
                "[GpuDriverOptimizer] Could not find AMD driver subkey in display adapter registry. " +
                "AMD optimizations cannot be applied via registry.");
            return false;
        }

        string amdUmdPath = $@"HKEY_LOCAL_MACHINE\{amdDriverSubkey}\UMD";

        _logger.Debug(
            "[GpuDriverOptimizer] Found AMD driver subkey: {SubkeyPath}",
            amdDriverSubkey);

        // ── Anti-Lag Enable (standard Anti-Lag ONLY - NOT Anti-Lag+ which causes bans) ──
        if (profile.EnableLowLatencyMode)
        {
            try
            {
                // AntiLag_DevMode = 1 enables standard Anti-Lag
                SnapshotAndSetRegistryValue(
                    snapshot,
                    amdUmdPath,
                    "AntiLag_DevMode",
                    "1",
                    RegistryValueKind.String,
                    "AMD Anti-Lag (standard only, NOT Anti-Lag+)");
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "[GpuDriverOptimizer] Failed to enable AMD Anti-Lag");
            }
        }

        // ── Radeon Chill Disable ──
        try
        {
            // Chill_Enabled = 0 disables Radeon Chill (prevents dynamic FPS throttling)
            SnapshotAndSetRegistryValue(
                snapshot,
                amdUmdPath,
                "Chill_Enabled",
                "0",
                RegistryValueKind.String,
                "AMD Radeon Chill Disabled");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[GpuDriverOptimizer] Failed to disable AMD Radeon Chill");
        }

        // ── Surface Format Optimization Enable ──
        try
        {
            // SurfaceFormatReplacements = 1 enables surface format optimization
            SnapshotAndSetRegistryValue(
                snapshot,
                amdUmdPath,
                "SurfaceFormatReplacements",
                "1",
                RegistryValueKind.String,
                "AMD Surface Format Optimization Enabled");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[GpuDriverOptimizer] Failed to enable AMD Surface Format Optimization");
        }

        // ── Shader Cache Enable/Reset ──
        if (profile.OptimizeShaderCache)
        {
            try
            {
                SnapshotAndSetRegistryValue(
                    snapshot,
                    amdUmdPath,
                    "ShaderCache",
                    "1",
                    RegistryValueKind.String,
                    "AMD Shader Cache Enabled");
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "[GpuDriverOptimizer] Failed to enable AMD Shader Cache");
            }
        }

        // ── FlipQueueSize = 1 (pre-rendered frames) ──
        try
        {
            // UMD\FlipQueueSize = REG_BINARY [0x31, 0x00] represents "1" as a wide-char string
            SnapshotAndSetRegistryValue(
                snapshot,
                amdUmdPath,
                "FlipQueueSize",
                new byte[] { 0x31, 0x00 },
                RegistryValueKind.Binary,
                "AMD Pre-Rendered Frames = 1");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to set AMD FlipQueueSize");
        }

        // ── Driver class key tweaks (EnableUlps, PP_SclkDeepSleepDisable) ──
        string amdDriverClassPath = $@"HKEY_LOCAL_MACHINE\{amdDriverSubkey}";

        // Disable Ultra Low Power State - prevents aggressive clock gating during gaming
        try
        {
            SnapshotAndSetRegistryValue(
                snapshot,
                amdDriverClassPath,
                "EnableUlps",
                0,
                RegistryValueKind.DWord,
                "AMD ULPS Disabled");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to disable AMD ULPS");
        }

        // Disable deep clock sleep - prevents latency spikes from clock ramp-up
        try
        {
            SnapshotAndSetRegistryValue(
                snapshot,
                amdDriverClassPath,
                "PP_SclkDeepSleepDisable",
                1,
                RegistryValueKind.DWord,
                "AMD Deep Clock Sleep Disabled");
            anySuccess = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to disable AMD deep clock sleep");
        }

        // ── ADLX Anti-Lag (try SDK first, fall back to registry above) ──
        try
        {
            _adlx = new AdlxManager();
            if (_adlx.IsAvailable && profile.EnableLowLatencyMode)
            {
                if (_adlx.EnableAntiLag())
                {
                    _logger.Information("[GpuDriverOptimizer] AMD Anti-Lag enabled via ADLX");
                }
            }
            else if (!_adlx.IsAvailable)
            {
                _logger.Debug("[GpuDriverOptimizer] ADLX not available - Anti-Lag controlled via registry");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] ADLX Anti-Lag setup failed");
        }

        return anySuccess;
    }

    // ════════════════════════════════════════════════════════════════════
    // Registry Helpers
    // ════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════
    // TDR Timeout Extension (All Vendors)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extends TDR (Timeout Detection and Recovery) delays to prevent false GPU timeout resets
    /// during heavy shader compilation in modern games. Applied for all GPU vendors.
    /// </summary>
    private void ApplyTdrTweaks(SystemStateSnapshot snapshot)
    {
        const string tdrPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

        // TdrDelay: seconds before GPU timeout reset (default 2, extended to 8)
        try
        {
            SnapshotAndSetRegistryValue(
                snapshot, tdrPath,
                "TdrDelay", 8, RegistryValueKind.DWord,
                "TDR Delay 8s (prevent false GPU resets)");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to set TdrDelay");
        }

        // TdrDdiDelay: DDI timeout in seconds (default 5, extended to 10)
        try
        {
            SnapshotAndSetRegistryValue(
                snapshot, tdrPath,
                "TdrDdiDelay", 10, RegistryValueKind.DWord,
                "TDR DDI Delay 10s (prevent false resets during shader compile)");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GpuDriverOptimizer] Failed to set TdrDdiDelay");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Registry Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Snapshots the current registry value, records it in the SystemStateSnapshot for crash
    /// recovery, then sets the new value. Tracks the change internally for clean revert.
    /// </summary>
    private void SnapshotAndSetRegistryValue(
        SystemStateSnapshot snapshot,
        string keyPath,
        string valueName,
        object newValue,
        RegistryValueKind valueKind,
        string description)
    {
        // Read current value
        object? currentValue = Registry.GetValue(keyPath, valueName, null);
        bool previouslyExisted = currentValue != null;

        // Record in SystemStateSnapshot for crash recovery
        snapshot.RecordRegistryValue(keyPath, valueName, currentValue ?? "__NOT_SET__");

        // Track internally for revert
        _registryChanges.Add(new RegistryChange(
            keyPath,
            valueName,
            currentValue,
            previouslyExisted,
            valueKind));

        // Apply new value
        Registry.SetValue(keyPath, valueName, newValue, valueKind);

        _logger.Information(
            "[GpuDriverOptimizer] {Description} - set {KeyPath}\\{ValueName} = {NewValue} (was: {OldValue})",
            description,
            keyPath,
            valueName,
            newValue,
            previouslyExisted ? currentValue : "<not set>");
    }

    /// <summary>
    /// Finds the driver subkey under the display adapter class GUID that matches the given
    /// vendor name(s). Iterates 0000, 0001, 0002, ... checking DriverDesc for a match.
    /// </summary>
    private string? FindDriverSubkey(params string[] vendorNames)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DriverClassBasePath);
            if (classKey == null)
            {
                _logger.Debug(
                    "[GpuDriverOptimizer] Display adapter class key not found: {Path}",
                    DriverClassBasePath);
                return null;
            }

            foreach (string subkeyName in classKey.GetSubKeyNames())
            {
                // Only check numeric subkeys (0000, 0001, 0002, ...)
                if (!int.TryParse(subkeyName, out _))
                    continue;

                try
                {
                    using var subkey = classKey.OpenSubKey(subkeyName);
                    if (subkey == null)
                        continue;

                    string? driverDesc = subkey.GetValue("DriverDesc")?.ToString();
                    if (string.IsNullOrEmpty(driverDesc))
                        continue;

                    foreach (string vendor in vendorNames)
                    {
                        if (driverDesc.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Debug(
                                "[GpuDriverOptimizer] Found {Vendor} driver at subkey {Subkey}: {DriverDesc}",
                                vendor,
                                subkeyName,
                                driverDesc);
                            return $@"{DriverClassBasePath}\{subkeyName}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(
                        ex,
                        "[GpuDriverOptimizer] Error reading driver subkey {Subkey}",
                        subkeyName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[GpuDriverOptimizer] Failed to enumerate display adapter subkeys");
        }

        return null;
    }

    /// <summary>
    /// Deletes a registry value that was created during Apply (did not previously exist).
    /// Handles both HKLM and HKCU paths.
    /// </summary>
    private void DeleteRegistryValue(string keyPath, string valueName)
    {
        try
        {
            // Parse the root key and subpath from the full path
            RegistryKey? rootKey = null;
            string subPath;

            if (keyPath.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.LocalMachine;
                subPath = keyPath.Substring(@"HKEY_LOCAL_MACHINE\".Length);
            }
            else if (keyPath.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.CurrentUser;
                subPath = keyPath.Substring(@"HKEY_CURRENT_USER\".Length);
            }
            else
            {
                _logger.Warning(
                    "[GpuDriverOptimizer] Cannot determine registry root for path: {KeyPath}",
                    keyPath);
                return;
            }

            using var key = rootKey.OpenSubKey(subPath, writable: true);
            if (key != null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[GpuDriverOptimizer] Failed to delete registry value {KeyPath}\\{ValueName}",
                keyPath,
                valueName);
        }
    }

    /// <summary>
    /// Case-insensitive string contains check with null safety.
    /// </summary>
    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return source != null && source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    // ════════════════════════════════════════════════════════════════════
    // Internal types
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks a single registry change for clean revert.
    /// </summary>
    private record RegistryChange(
        string KeyPath,
        string ValueName,
        object? PreviousValue,
        bool PreviouslyExisted,
        RegistryValueKind ValueKind);
}
