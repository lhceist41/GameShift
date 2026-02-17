using System.Management;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Detection;
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
/// GPU registry changes are stored in snapshot.RegistryValues and persisted to
/// active_session.json for crash recovery. The existing SystemStateSnapshot
/// serialization handles GPU entries automatically.
/// </summary>
public class GpuDriverOptimizer : IOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private bool _isApplied;

    // Cached GPU vendor detection result (only detect once per Apply)
    private GpuVendor _detectedVendor = GpuVendor.Unknown;
    private string? _detectedGpuName;

    // Track all registry changes for clean revert
    private readonly List<RegistryChange> _registryChanges = new();

    // ── Display Adapter Class GUID (standard for all GPUs) ───────────
    private const string DisplayAdapterClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DriverClassBasePath = @"SYSTEM\CurrentControlSet\Control\Class\" + DisplayAdapterClassGuid;

    // ── NVIDIA registry paths ───────────────────────────────
    private const string NvidiaFtsPath = @"HKEY_CURRENT_USER\SOFTWARE\NVIDIA Corporation\Global\FTS";
    private const string NvidiaFtsValueName = "EnableRID73779";

    private const string NvidiaTweakPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string NvidiaTweakValueName = "NVCplShaderCacheSize";

    public string Name => "GPU Driver Optimizer";

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
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask;

        try
        {
            _logger.Information(
                "GpuDriverOptimizer: Applying GPU driver optimizations at {Timestamp}",
                DateTime.UtcNow.ToString("o"));

            // ── Step 1: Detect GPU vendor ──
            _detectedVendor = DetectGpuVendor();

            if (_detectedVendor == GpuVendor.Unknown)
            {
                _logger.Warning(
                    "GpuDriverOptimizer: No supported GPU vendor detected (NVIDIA or AMD required). Skipping GPU optimizations.");
                return false;
            }

            _logger.Information(
                "GpuDriverOptimizer: Detected {Vendor} GPU: {GpuName}",
                _detectedVendor,
                _detectedGpuName ?? "Unknown");

            // ── Step 2: Apply vendor-specific optimizations ──
            bool success = _detectedVendor switch
            {
                GpuVendor.Nvidia => ApplyNvidiaOptimizations(snapshot, profile),
                GpuVendor.Amd => ApplyAmdOptimizations(snapshot, profile),
                _ => false
            };

            if (success)
            {
                _isApplied = true;
                _logger.Information(
                    "GpuDriverOptimizer: GPU optimizations applied successfully ({Count} registry values modified)",
                    _registryChanges.Count);
            }
            else
            {
                _logger.Warning(
                    "GpuDriverOptimizer: GPU optimizations partially or fully failed");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "GpuDriverOptimizer: Failed to apply GPU driver optimizations");
            return false;
        }
    }

    /// <summary>
    /// Reverts all GPU registry changes using internally tracked previous values.
    /// Logs advisory that some settings may require a driver restart to take full effect.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask;

        if (!_isApplied)
        {
            return true; // No-op if not applied
        }

        try
        {
            _logger.Information(
                "GpuDriverOptimizer: Reverting GPU driver optimizations at {Timestamp}",
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
                            "GpuDriverOptimizer: Restored {KeyPath}\\{ValueName} = {Value}",
                            change.KeyPath,
                            change.ValueName,
                            change.PreviousValue);
                    }
                    else
                    {
                        // Value did not exist before — delete it
                        DeleteRegistryValue(change.KeyPath, change.ValueName);
                        _logger.Debug(
                            "GpuDriverOptimizer: Deleted {KeyPath}\\{ValueName} (was not present before apply)",
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
                        "GpuDriverOptimizer: Failed to revert {KeyPath}\\{ValueName}",
                        change.KeyPath,
                        change.ValueName);
                }
            }

            _registryChanges.Clear();
            _isApplied = false;
            _detectedVendor = GpuVendor.Unknown;
            _detectedGpuName = null;

            _logger.Information(
                "GpuDriverOptimizer: Revert complete — {SuccessCount} restored, {FailCount} failed",
                successCount,
                failCount);

            _logger.Warning(
                "GpuDriverOptimizer: Some GPU driver settings may require a driver restart to take full effect");

            return failCount == 0;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "GpuDriverOptimizer: Failed to revert GPU driver optimizations");
            return false;
        }
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
                string? compatibility = obj["AdapterCompatibility"]?.ToString();
                string? name = obj["Name"]?.ToString();

                _logger.Debug(
                    "GpuDriverOptimizer: Found GPU — AdapterCompatibility: {Compatibility}, Name: {Name}",
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
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "GpuDriverOptimizer: WMI query for GPU detection failed");
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
                    "GpuDriverOptimizer: Failed to apply NVIDIA Low Latency Mode Ultra");
            }
        }

        // ── Shader Cache Size 10GB ──
        if (profile.OptimizeShaderCache)
        {
            try
            {
                SnapshotAndSetRegistryValue(
                    snapshot,
                    NvidiaTweakPath,
                    NvidiaTweakValueName,
                    10240,
                    RegistryValueKind.DWord,
                    "NVIDIA Shader Cache Size 10GB");
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "GpuDriverOptimizer: Failed to apply NVIDIA Shader Cache Size");
            }
        }

        // ── Power Management Max Performance (DRS profile) ──
        if (profile.ForceMaxPerformancePowerMode)
        {
            // NVIDIA power management, threaded optimization, and texture filtering quality
            // are controlled via DRS (Driver Runtime Settings) profiles. These settings are
            // stored in nvdrsdb.bin and are not reliably accessible via direct registry writes.
            // Users should configure these in NVIDIA Control Panel > Manage 3D Settings:
            //   - Power management mode: Prefer maximum performance
            //   - Threaded optimization: On
            //   - Texture filtering - Quality: High performance
            _logger.Information(
                "GpuDriverOptimizer: NVIDIA power management, threaded optimization, and texture filtering " +
                "are DRS profile settings — configure via NVIDIA Control Panel > Manage 3D Settings for best results. " +
                "Registry-accessible settings (Low Latency, Shader Cache) have been applied.");
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
                "GpuDriverOptimizer: Could not find AMD driver subkey in display adapter registry. " +
                "AMD optimizations cannot be applied via registry.");
            return false;
        }

        string amdUmdPath = $@"HKEY_LOCAL_MACHINE\{amdDriverSubkey}\UMD";

        _logger.Debug(
            "GpuDriverOptimizer: Found AMD driver subkey: {SubkeyPath}",
            amdDriverSubkey);

        // ── Anti-Lag Enable (standard Anti-Lag ONLY — NOT Anti-Lag+ which causes bans) ──
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
                    "GpuDriverOptimizer: Failed to enable AMD Anti-Lag");
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
                "GpuDriverOptimizer: Failed to disable AMD Radeon Chill");
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
                "GpuDriverOptimizer: Failed to enable AMD Surface Format Optimization");
        }

        // ── Shader Cache Enable/Reset ──
        if (profile.OptimizeShaderCache)
        {
            try
            {
                // ShaderCache = 1 enables shader cache
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
                    "GpuDriverOptimizer: Failed to enable AMD Shader Cache");
            }
        }

        return anySuccess;
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
            "GpuDriverOptimizer: {Description} — set {KeyPath}\\{ValueName} = {NewValue} (was: {OldValue})",
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
                    "GpuDriverOptimizer: Display adapter class key not found: {Path}",
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
                                "GpuDriverOptimizer: Found {Vendor} driver at subkey {Subkey}: {DriverDesc}",
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
                        "GpuDriverOptimizer: Error reading driver subkey {Subkey}",
                        subkeyName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "GpuDriverOptimizer: Failed to enumerate display adapter subkeys");
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
                    "GpuDriverOptimizer: Cannot determine registry root for path: {KeyPath}",
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
                "GpuDriverOptimizer: Failed to delete registry value {KeyPath}\\{ValueName}",
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
