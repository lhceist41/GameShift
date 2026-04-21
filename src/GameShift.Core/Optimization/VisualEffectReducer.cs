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
/// Visual Effect Reducer - Disables Windows transparency and animations for reduced GPU overhead.
/// Disables transparency via registry (HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize)
/// and animations via SystemParametersInfo. Records original state for clean revert.
///
/// Implements IJournaledOptimization so the watchdog can restore both the registry transparency
/// value and the system-wide animation state from the serialized journal record, even after a
/// main-app crash wipes the in-memory instance fields.
/// </summary>
public class VisualEffectReducer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // Subkey form used with RegistryKey.OpenSubKey (no HKCU prefix).
    private const string TransparencyRegistrySubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Full-qualified form used with Registry.GetValue / SetValue (HKCU prefix).
    private const string TransparencyKeyPath =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string TransparencyValueName = "EnableTransparency";

    // Pseudo-key used inside the serialized original/applied-state dictionary to carry the
    // animation state alongside the real registry value. This is a JSON-only marker —
    // it never touches the registry.
    private const string AnimationKey = "__AnimationEnabled__";

    private bool _isApplied;

    // State captured during Apply() for use in Revert().
    private bool _transparencyPreviouslyExisted;
    private int _transparencyPreviousValue;
    private bool _originalAnimationState;
    private bool _transparencyApplied;
    private bool _animationApplied;

    // Context stored by CanApply() for use by Apply().
    private SystemContext? _context;

    public const string OptimizationId = "Visual Effect Reducer";

    // ── IOptimization ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Name => OptimizationId;

    /// <inheritdoc/>
    public string Description => "Disables Windows transparency and animations for reduced GPU overhead";

    /// <inheritdoc/>
    public bool IsApplied => _isApplied;

    /// <inheritdoc/>
    public bool IsAvailable => true; // Visual effects work on all Windows versions

    /// <summary>
    /// Delegates to the journaled Apply() path. Stores context first via CanApply().
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        var context = new SystemContext { Profile = profile, Snapshot = snapshot };
        if (!CanApply(context))
            return Task.FromResult(true);

        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    /// <summary>
    /// Delegates to the journaled Revert() path.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!_isApplied)
            return Task.FromResult(true);

        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    /// <summary>
    /// Pre-flight check. Stores context for use in Apply().
    /// Visual effect reduction applies at all intensity tiers.
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Disables Windows transparency and animations. Records original state for clean revert.
    /// Returns OptimizationResult with serialized original/applied state covering both the
    /// registry transparency value and the animation flag.
    /// </summary>
    public OptimizationResult Apply()
    {
        var snapshot = _context?.Snapshot;
        _transparencyApplied = false;
        _animationApplied = false;

        var originalState = new Dictionary<string, object?>();
        var appliedState = new Dictionary<string, object?>();

        try
        {
            _logger.Information("[VisualEffectReducer] Applying visual effect reduction");

            // ── Disable Transparency (registry) ────────────────────────────
            try
            {
                object? currentValue = Registry.GetValue(TransparencyKeyPath, TransparencyValueName, null);

                if (currentValue is int existingInt)
                {
                    _transparencyPreviouslyExisted = true;
                    _transparencyPreviousValue = existingInt;
                    originalState[TransparencyValueName] = existingInt;
                    snapshot?.RecordRegistryValue(TransparencyKeyPath, TransparencyValueName, existingInt);
                    _logger.Debug("[VisualEffectReducer] Recorded transparency original value: {Value}", existingInt);
                }
                else if (currentValue != null)
                {
                    // Non-int value (shouldn't happen for a DWORD, but be defensive).
                    _transparencyPreviouslyExisted = true;
                    _transparencyPreviousValue = Convert.ToInt32(currentValue);
                    originalState[TransparencyValueName] = _transparencyPreviousValue;
                    snapshot?.RecordRegistryValue(TransparencyKeyPath, TransparencyValueName, _transparencyPreviousValue);
                    _logger.Debug("[VisualEffectReducer] Recorded transparency original value (converted): {Value}", _transparencyPreviousValue);
                }
                else
                {
                    _transparencyPreviouslyExisted = false;
                    originalState[TransparencyValueName] = null;
                    snapshot?.RecordRegistryValue(TransparencyKeyPath, TransparencyValueName, "__NOT_SET__");
                    _logger.Debug("[VisualEffectReducer] Transparency value did not exist, recorded sentinel");
                }

                Registry.SetValue(TransparencyKeyPath, TransparencyValueName, 0, RegistryValueKind.DWord);
                appliedState[TransparencyValueName] = 0;
                _logger.Information("[VisualEffectReducer] Disabled Windows transparency");
                _transparencyApplied = true;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VisualEffectReducer] Failed to disable transparency, continuing with animations");
            }

            // ── Disable Animations (SystemParametersInfo) ──────────────────
            try
            {
                if (TrySetAnimation(out var previouslyEnabled, enable: false))
                {
                    _originalAnimationState = previouslyEnabled;
                    originalState[AnimationKey] = previouslyEnabled;
                    appliedState[AnimationKey] = false;
                    _logger.Information("[VisualEffectReducer] Disabled Windows animations (was: {State})",
                        previouslyEnabled ? "enabled" : "disabled");
                    _animationApplied = true;
                }
                else
                {
                    _logger.Warning("[VisualEffectReducer] Failed to disable animations");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VisualEffectReducer] Failed to disable animations");
            }

            if (_transparencyApplied || _animationApplied)
            {
                _isApplied = true;
                _logger.Information(
                    "[VisualEffectReducer] Visual effect reduction applied (transparency: {Trans}, animations: {Anim})",
                    _transparencyApplied, _animationApplied);

                return new OptimizationResult(
                    Name: OptimizationId,
                    OriginalValue: JsonSerializer.Serialize(originalState),
                    AppliedValue: JsonSerializer.Serialize(appliedState),
                    State: OptimizationState.Applied);
            }

            _logger.Warning("[VisualEffectReducer] Both transparency and animation optimizations failed");
            return Fail("Both transparency and animation optimizations failed");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VisualEffectReducer] Failed to apply visual effect reduction");
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Reverts transparency and animation settings using state captured during Apply().
    /// </summary>
    public OptimizationResult Revert()
    {
        bool transparencySuccess = false;
        bool animationSuccess = false;

        try
        {
            _logger.Information("[VisualEffectReducer] Reverting visual effect reduction");

            // ── Restore Transparency ──────────────────────────────────────
            if (_transparencyApplied)
            {
                try
                {
                    if (_transparencyPreviouslyExisted)
                    {
                        Registry.SetValue(TransparencyKeyPath, TransparencyValueName,
                            _transparencyPreviousValue, RegistryValueKind.DWord);
                        _logger.Information(
                            "[VisualEffectReducer] Restored transparency to original value: {Value}",
                            _transparencyPreviousValue);
                    }
                    else
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(
                            TransparencyRegistrySubKey, writable: true);
                        if (key != null)
                        {
                            key.DeleteValue(TransparencyValueName, throwOnMissingValue: false);
                            _logger.Information(
                                "[VisualEffectReducer] Deleted transparency value (was not originally set)");
                        }
                    }
                    transparencySuccess = true;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[VisualEffectReducer] Failed to restore transparency");
                }
            }
            else
            {
                transparencySuccess = true; // nothing to revert
            }

            // ── Restore Animations ────────────────────────────────────────
            if (_animationApplied)
            {
                try
                {
                    if (_originalAnimationState)
                    {
                        if (TrySetAnimation(out _, enable: true))
                        {
                            _logger.Information("[VisualEffectReducer] Restored Windows animations");
                            animationSuccess = true;
                        }
                        else
                        {
                            _logger.Warning("[VisualEffectReducer] Failed to restore animation state");
                        }
                    }
                    else
                    {
                        _logger.Debug("[VisualEffectReducer] Animations were originally disabled, no restore needed");
                        animationSuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[VisualEffectReducer] Failed to restore animations");
                }
            }
            else
            {
                animationSuccess = true; // nothing to revert
            }

            _isApplied = false;

            if (transparencySuccess || animationSuccess)
            {
                _logger.Information(
                    "[VisualEffectReducer] Visual effect reduction reverted (transparency: {Trans}, animations: {Anim})",
                    transparencySuccess, animationSuccess);
                return new OptimizationResult(
                    Name: OptimizationId,
                    OriginalValue: string.Empty,
                    AppliedValue: string.Empty,
                    State: OptimizationState.Reverted);
            }

            _logger.Warning("[VisualEffectReducer] Both transparency and animation reverts failed");
            return RevertFail("Both transparency and animation reverts failed");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VisualEffectReducer] Failed to revert visual effect reduction");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Confirms the applied changes are still in effect.
    /// Checks the transparency registry value and the animation system parameter.
    /// </summary>
    public bool Verify()
    {
        if (!_isApplied)
            return false;

        try
        {
            if (_transparencyApplied)
            {
                var current = Registry.GetValue(TransparencyKeyPath, TransparencyValueName, null);
                if (current is not int value || value != 0)
                    return false;
            }

            if (_animationApplied && TryGetAnimation(out var enabled) && enabled)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

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
                if (key == null)
                    return RevertFail("Failed to open transparency registry key");

                if (transparencyElement.ValueKind == JsonValueKind.Null)
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

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
