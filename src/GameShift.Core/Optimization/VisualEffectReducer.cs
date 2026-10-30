using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer turns off transparency and animations during a game. The class stays so a
/// session journal written by an earlier version still restores them.
/// </summary>
public class VisualEffectReducer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private const string TransparencyRegistrySubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string TransparencyValueName = "EnableTransparency";

    // JSON-only marker that carries the animation state next to the registry value in the record.
    private const string AnimationKey = "__AnimationEnabled__";

    private bool _isApplied;

    public const string OptimizationId = "Visual Effect Reducer";

    public string Name => OptimizationId;
    public string Description => "Revert-only: restores transparency and animations recorded by earlier versions";
    public bool IsApplied => _isApplied;
    public bool IsAvailable => false;

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => Task.FromResult(false);
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => Task.FromResult(true);

    public bool CanApply(SystemContext context) => false;

    public OptimizationResult Apply() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "Revert-only: no longer applied");

    public OptimizationResult Revert() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);

    public bool Verify() => false;

    /// <summary>
    /// Watchdog recovery path: parses the serialized original state from the journal and
    /// restores both the transparency registry value and the animation system parameter
    /// without relying on any live instance state.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            _logger.Information("[VisualEffectReducer] Reverting from journal record (watchdog recovery)");

            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalValueJson);
            if (values == null)
                return RevertFail("Failed to parse originalValueJson");

            // ── Restore transparency registry value ────────────────────────
            if (values.TryGetValue(TransparencyValueName, out var transparencyElement))
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    TransparencyRegistrySubKey, writable: true);
                // A missing key must not abort the whole revert - fall through so the animation
                // restore below still runs (mirrors live Revert() keeping the two independent).
                if (key == null)
                {
                    _logger.Warning(
                        "[VisualEffectReducer] Transparency key unavailable; skipping transparency restore");
                }
                else if (transparencyElement.ValueKind == JsonValueKind.Null)
                {
                    key.DeleteValue(TransparencyValueName, throwOnMissingValue: false);
                    _logger.Information(
                        "[VisualEffectReducer] Deleted transparency value (was absent before session)");
                }
                else if (transparencyElement.ValueKind == JsonValueKind.Number)
                {
                    int val = transparencyElement.GetInt32();
                    key.SetValue(TransparencyValueName, val, RegistryValueKind.DWord);
                    _logger.Information(
                        "[VisualEffectReducer] Restored transparency to {Value}", val);
                }
            }

            // ── Restore animation state ────────────────────────────────────
            if (values.TryGetValue(AnimationKey, out var animationElement))
            {
                bool wasEnabled = animationElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => false
                };

                // Only drive the SPI call if the original state differed from the
                // applied state. If animations were already off before apply, skip.
                if (wasEnabled)
                {
                    if (TrySetAnimation(out _, enable: true))
                        _logger.Information("[VisualEffectReducer] Restored Windows animations (enabled)");
                    else
                        _logger.Warning("[VisualEffectReducer] Failed to restore animation state");
                }
                else
                {
                    _logger.Debug(
                        "[VisualEffectReducer] Animations were originally disabled, no restore needed");
                }
            }

            _isApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VisualEffectReducer] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    /// <summary>
    /// Reads the current animation state via SPI_GETANIMATION. Returns true on success.
    /// </summary>
    private static bool TryGetAnimation(out bool enabled)
    {
        enabled = false;
        IntPtr animInfoPtr = IntPtr.Zero;
        try
        {
            var animInfo = new NativeInterop.ANIMATIONINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>()
            };

            animInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.ANIMATIONINFO>());
            Marshal.StructureToPtr(animInfo, animInfoPtr, false);

            if (!NativeInterop.SystemParametersInfo(
                    NativeInterop.SPI_GETANIMATION,
                    (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                    animInfoPtr,
                    0))
            {
                return false;
            }

            animInfo = Marshal.PtrToStructure<NativeInterop.ANIMATIONINFO>(animInfoPtr);
            enabled = animInfo.iMinAnimate != 0;
            return true;
        }
        finally
        {
            if (animInfoPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(animInfoPtr);
        }
    }

    /// <summary>
    /// Sets the animation state via SPI_SETANIMATION. On success, reports the previous state
    /// (as read during the same call) via <paramref name="previouslyEnabled"/>.
    /// </summary>
    private static bool TrySetAnimation(out bool previouslyEnabled, bool enable)
    {
        previouslyEnabled = false;
        if (!TryGetAnimation(out previouslyEnabled))
            return false;

        IntPtr animInfoPtr = IntPtr.Zero;
        try
        {
            var animInfo = new NativeInterop.ANIMATIONINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                iMinAnimate = enable ? 1 : 0
            };

            animInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.ANIMATIONINFO>());
            Marshal.StructureToPtr(animInfo, animInfoPtr, false);

            return NativeInterop.SystemParametersInfo(
                NativeInterop.SPI_SETANIMATION,
                (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                animInfoPtr,
                NativeInterop.SPIF_SENDCHANGE);
        }
        finally
        {
            if (animInfoPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(animInfoPtr);
        }
    }
}
