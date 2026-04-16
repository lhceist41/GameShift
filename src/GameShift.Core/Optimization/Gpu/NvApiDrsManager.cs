using System.Runtime.InteropServices;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Optimization.Gpu;

/// <summary>
/// Manages NVIDIA Driver Runtime Settings (DRS) profiles via P/Invoke against nvapi64.dll.
///
/// NvAPI does not export functions by name — all access goes through
/// <c>nvapi_QueryInterface(functionId)</c> which returns function pointers.
/// This class caches the required delegates at construction time.
///
/// Gracefully degrades: if <c>nvapi64.dll</c> is not present (no NVIDIA driver installed)
/// or <c>NvAPI_Initialize</c> fails, <see cref="IsAvailable"/> is false and all
/// operations are no-ops.
///
/// <para>Managed settings per-game:</para>
/// <list type="table">
///   <item><term>0x20906015</term><description>Max pre-rendered frames → 1</description></item>
///   <item><term>0x20D690F8</term><description>Power management mode → 1 (Prefer Max Perf)</description></item>
///   <item><term>0x20C392B0</term><description>Shader cache size → 0xFFFFFFFF (Unlimited)</description></item>
///   <item><term>0x20E4E645</term><description>Low Latency Mode → 2 (Ultra)</description></item>
/// </list>
/// </summary>
public sealed class NvApiDrsManager : IDisposable
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // ── NvAPI function IDs ────────────────────────────────────────────────────

    private const uint FnId_Initialize        = 0x0150E828;
    private const uint FnId_Unload            = 0xD22BDD7E;
    private const uint FnId_DRS_CreateSession = 0x0694D52E;
    private const uint FnId_DRS_DestroySession= 0xDAD9CFF8;
    private const uint FnId_DRS_LoadSettings  = 0x375DBD6B;
    private const uint FnId_DRS_SaveSettings  = 0xFCBC7E14;
    private const uint FnId_DRS_GetBaseProfile= 0xDA8466A0;
    private const uint FnId_DRS_GetSetting    = 0x73BF8338;
    private const uint FnId_DRS_SetSetting    = 0x577DD202;

    // ── DRS Setting IDs ───────────────────────────────────────────────────────

    public const uint Setting_PreRenderLimit  = 0x20906015;
    public const uint Setting_PowerMgmtMode   = 0x20D690F8;
    public const uint Setting_ShaderCacheSize = 0x20C392B0;
    public const uint Setting_LowLatencyMode  = 0x20E4E645;

    // ── Struct layout constants ───────────────────────────────────────────────

    // NVDRS_SETTING_V1 layout:
    //   0:    version (4)
    //   4:    settingName (wchar[2048] = 4096)
    //   4100: settingId (4)
    //   4104: settingType (4)  — 0 = DWORD
    //   4108: settingLocation (4)
    //   4112: isCurrentPredefined (4)
    //   4116: isPredefinedValid (4)
    //   4120: currentValue union (4096)
    //   8216: predefinedValue union (4096)
    //   Total: 12312 bytes
    private const int SettingStructSize = 12312;
    private const int SettingVersion    = (1 << 16) | SettingStructSize;

    private const int Off_SettingId           = 4100;
    private const int Off_SettingType         = 4104;
    private const int Off_CurrentValueDword   = 4120;

    // NvAPI status codes
    private const int NVAPI_OK = 0;

    // ── Delegates ─────────────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvAPI_QueryInterface_t(uint functionId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_Initialize_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_Unload_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_CreateSession_t(out IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_DestroySession_t(IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_LoadSettings_t(IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_SaveSettings_t(IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_GetBaseProfile_t(IntPtr hSession, out IntPtr hProfile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_GetSetting_t(IntPtr hSession, IntPtr hProfile, uint settingId, IntPtr pSetting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_DRS_SetSetting_t(IntPtr hSession, IntPtr hProfile, IntPtr pSetting);

    // ── Cached function pointers ──────────────────────────────────────────────

    private NvAPI_Initialize_t? _initialize;
    private NvAPI_Unload_t? _unload;
    private NvAPI_DRS_CreateSession_t? _createSession;
    private NvAPI_DRS_DestroySession_t? _destroySession;
    private NvAPI_DRS_LoadSettings_t? _loadSettings;
    private NvAPI_DRS_SaveSettings_t? _saveSettings;
    private NvAPI_DRS_GetBaseProfile_t? _getBaseProfile;
    private NvAPI_DRS_GetSetting_t? _getSetting;
    private NvAPI_DRS_SetSetting_t? _setSetting;

    private bool _initialized;

    /// <summary>True if nvapi64.dll loaded and NvAPI_Initialize succeeded.</summary>
    public bool IsAvailable { get; private set; }

    // ── Construction / Initialization ─────────────────────────────────────────

    /// <summary>
    /// Loads nvapi64.dll, resolves function pointers, and initializes NvAPI.
    /// Sets <see cref="IsAvailable"/> = false if any step fails (no NVIDIA driver, etc.).
    /// </summary>
    public NvApiDrsManager()
    {
        try
        {
            // Load the DLL — IntPtr.Zero means "not found"
            var lib = NativeLibrary.Load("nvapi64");

            // nvapi_QueryInterface is the only exported symbol
            var qiPtr = NativeLibrary.GetExport(lib, "nvapi_QueryInterface");
            var qi = Marshal.GetDelegateForFunctionPointer<NvAPI_QueryInterface_t>(qiPtr);

            // Resolve all needed function pointers
            _initialize    = GetDelegate<NvAPI_Initialize_t>(qi, FnId_Initialize);
            _unload        = GetDelegate<NvAPI_Unload_t>(qi, FnId_Unload);
            _createSession = GetDelegate<NvAPI_DRS_CreateSession_t>(qi, FnId_DRS_CreateSession);
            _destroySession= GetDelegate<NvAPI_DRS_DestroySession_t>(qi, FnId_DRS_DestroySession);
            _loadSettings  = GetDelegate<NvAPI_DRS_LoadSettings_t>(qi, FnId_DRS_LoadSettings);
            _saveSettings  = GetDelegate<NvAPI_DRS_SaveSettings_t>(qi, FnId_DRS_SaveSettings);
            _getBaseProfile= GetDelegate<NvAPI_DRS_GetBaseProfile_t>(qi, FnId_DRS_GetBaseProfile);
            _getSetting    = GetDelegate<NvAPI_DRS_GetSetting_t>(qi, FnId_DRS_GetSetting);
            _setSetting    = GetDelegate<NvAPI_DRS_SetSetting_t>(qi, FnId_DRS_SetSetting);

            // Initialize NvAPI
            int status = _initialize!();
            if (status != NVAPI_OK)
            {
                _logger.Warning("[NvApiDrs] NvAPI_Initialize failed with status {Status}", status);
                return;
            }

            _initialized = true;
            IsAvailable = true;
            _logger.Information("[NvApiDrs] NvAPI initialized successfully");
        }
        catch (DllNotFoundException)
        {
            _logger.Debug("[NvApiDrs] nvapi64.dll not found — NVIDIA driver not installed");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NvApiDrs] Failed to load NvAPI");
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current DWORD value of a DRS setting from the base (global) profile.
    /// Returns null if the setting is not set or an error occurs.
    /// </summary>
    public uint? ReadGlobalSetting(uint settingId)
    {
        if (!IsAvailable) return null;

        IntPtr hSession = IntPtr.Zero;
        try
        {
            int status = _createSession!(out hSession);
            if (status != NVAPI_OK) return null;

            status = _loadSettings!(hSession);
            if (status != NVAPI_OK) return null;

            status = _getBaseProfile!(hSession, out var hProfile);
            if (status != NVAPI_OK) return null;

            return ReadSettingValue(hSession, hProfile, settingId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NvApiDrs] ReadGlobalSetting failed for 0x{Id:X8}", settingId);
            return null;
        }
        finally
        {
            if (hSession != IntPtr.Zero)
                _destroySession!(hSession);
        }
    }

    /// <summary>
    /// Writes a DWORD value to a DRS setting in the base (global) profile.
    /// Returns the previous value (for revert) or null if the read failed.
    /// </summary>
    public uint? WriteGlobalSetting(uint settingId, uint newValue)
    {
        if (!IsAvailable) return null;

        IntPtr hSession = IntPtr.Zero;
        try
        {
            int status = _createSession!(out hSession);
            if (status != NVAPI_OK)
            {
                _logger.Warning("[NvApiDrs] DRS_CreateSession failed: {Status}", status);
                return null;
            }

            status = _loadSettings!(hSession);
            if (status != NVAPI_OK) return null;

            status = _getBaseProfile!(hSession, out var hProfile);
            if (status != NVAPI_OK) return null;

            // Read current value for backup
            uint? previousValue = ReadSettingValue(hSession, hProfile, settingId);

            // Write new value
            if (!WriteSettingValue(hSession, hProfile, settingId, newValue))
                return null;

            // Persist
            status = _saveSettings!(hSession);
            if (status != NVAPI_OK)
            {
                _logger.Warning("[NvApiDrs] DRS_SaveSettings failed: {Status}", status);
                return null;
            }

            _logger.Information(
                "[NvApiDrs] Set 0x{Id:X8} = {Value} (was: {Prev})",
                settingId, newValue, previousValue?.ToString() ?? "<not set>");

            return previousValue;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NvApiDrs] WriteGlobalSetting failed for 0x{Id:X8}", settingId);
            return null;
        }
        finally
        {
            if (hSession != IntPtr.Zero)
                _destroySession!(hSession);
        }
    }

    /// <summary>
    /// Applies the recommended gaming DRS settings to the global profile.
    /// Returns a dictionary of settingId → previous value for revert.
    /// </summary>
    public Dictionary<uint, uint?> ApplyGamingSettings()
    {
        var backup = new Dictionary<uint, uint?>();
        if (!IsAvailable) return backup;

        var settings = new (uint id, uint value, string name)[]
        {
            (Setting_PreRenderLimit,  1,          "Max Pre-Rendered Frames = 1"),
            (Setting_PowerMgmtMode,   1,          "Power Management = Prefer Max Perf"),
            (Setting_ShaderCacheSize, 0xFFFFFFFF, "Shader Cache = Unlimited"),
            (Setting_LowLatencyMode,  2,          "Low Latency Mode = Ultra"),
        };

        IntPtr hSession = IntPtr.Zero;
        try
        {
            int status = _createSession!(out hSession);
            if (status != NVAPI_OK) return backup;

            _loadSettings!(hSession);
            _getBaseProfile!(hSession, out var hProfile);

            foreach (var (id, value, name) in settings)
            {
                uint? prev = ReadSettingValue(hSession, hProfile, id);
                backup[id] = prev;

                if (WriteSettingValue(hSession, hProfile, id, value))
                    _logger.Information("[NvApiDrs] Applied: {Name}", name);
                else
                    _logger.Warning("[NvApiDrs] Failed to apply: {Name}", name);
            }

            _saveSettings!(hSession);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NvApiDrs] ApplyGamingSettings failed");
        }
        finally
        {
            if (hSession != IntPtr.Zero)
                _destroySession!(hSession);
        }

        return backup;
    }

    /// <summary>
    /// Restores DRS settings from a backup dictionary.
    /// </summary>
    public void RestoreSettings(Dictionary<uint, uint?> backup)
    {
        if (!IsAvailable || backup.Count == 0) return;

        IntPtr hSession = IntPtr.Zero;
        try
        {
            int status = _createSession!(out hSession);
            if (status != NVAPI_OK) return;

            _loadSettings!(hSession);
            _getBaseProfile!(hSession, out var hProfile);

            foreach (var (id, prevValue) in backup)
            {
                if (prevValue.HasValue)
                {
                    WriteSettingValue(hSession, hProfile, id, prevValue.Value);
                    _logger.Debug("[NvApiDrs] Restored 0x{Id:X8} = {Value}", id, prevValue.Value);
                }
            }

            _saveSettings!(hSession);
            _logger.Information("[NvApiDrs] Restored {Count} DRS settings", backup.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NvApiDrs] RestoreSettings failed");
        }
        finally
        {
            if (hSession != IntPtr.Zero)
                _destroySession!(hSession);
        }
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private uint? ReadSettingValue(IntPtr hSession, IntPtr hProfile, uint settingId)
    {
        var buf = Marshal.AllocHGlobal(SettingStructSize);
        try
        {
            // Zero buffer and set version
            for (int i = 0; i < SettingStructSize; i += 4)
                Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt32(buf, 0, SettingVersion);

            int status = _getSetting!(hSession, hProfile, settingId, buf);
            if (status != NVAPI_OK)
                return null;

            // Read settingType at known offset — only handle DWORD (type 0)
            int settingType = Marshal.ReadInt32(buf, Off_SettingType);
            if (settingType != 0)
                return null;

            return (uint)Marshal.ReadInt32(buf, Off_CurrentValueDword);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private bool WriteSettingValue(IntPtr hSession, IntPtr hProfile, uint settingId, uint value)
    {
        var buf = Marshal.AllocHGlobal(SettingStructSize);
        try
        {
            for (int i = 0; i < SettingStructSize; i += 4)
                Marshal.WriteInt32(buf, i, 0);

            Marshal.WriteInt32(buf, 0, SettingVersion);
            Marshal.WriteInt32(buf, Off_SettingId, (int)settingId);
            Marshal.WriteInt32(buf, Off_SettingType, 0); // DWORD
            Marshal.WriteInt32(buf, Off_CurrentValueDword, (int)value);

            int status = _setSetting!(hSession, hProfile, buf);
            return status == NVAPI_OK;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static T? GetDelegate<T>(NvAPI_QueryInterface_t qi, uint functionId) where T : Delegate
    {
        var ptr = qi(functionId);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { _unload?.Invoke(); }
            catch { /* Best-effort cleanup */ }
            _initialized = false;
            IsAvailable = false;
        }
    }
}
