using System.Runtime.InteropServices;
using Microsoft.Win32;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.Config;

namespace GameShift.Core.Optimization;

/// <summary>
/// Visual Effect Reducer - Disables Windows transparency and animations for reduced GPU overhead.
/// Disables transparency via registry (HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize)
/// and animations via SystemParametersInfo. Records original state in snapshot for clean revert.
/// </summary>
public class VisualEffectReducer : IOptimization
{
    private const string TransparencyKeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string TransparencyValueName = "EnableTransparency";

    private bool _originalAnimationState;
    private bool _isApplied;

    public const string OptimizationId = "Visual Effect Reducer";

    /// <inheritdoc/>
    public string Name => OptimizationId;

    /// <inheritdoc/>
    public string Description => "Disables Windows transparency and animations for reduced GPU overhead";

    /// <inheritdoc/>
    public bool IsApplied => _isApplied;

    /// <inheritdoc/>
    public bool IsAvailable => true; // Visual effects work on all Windows versions

    /// <summary>
    /// Applies visual effect optimization by disabling transparency and animations.
    /// Records original settings in snapshot for later restoration.
    /// </summary>
    /// <param name="snapshot">Snapshot to record original state for revert</param>
    /// <param name="profile">Game profile containing process info and settings</param>
    /// <returns>True if optimization applied successfully, false otherwise</returns>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        bool transparencySuccess = false;
        bool animationSuccess = false;

        try
        {
            SettingsManager.Logger.Information("[VisualEffectReducer] Applying visual effect reduction");

            // Disable Transparency
            try
            {
                // Read current transparency value
                object? currentValue = Registry.GetValue(TransparencyKeyPath, TransparencyValueName, null);

                // Record original value in snapshot
                if (currentValue != null)
                {
                    snapshot.RecordRegistryValue(TransparencyKeyPath, TransparencyValueName, currentValue);
                    SettingsManager.Logger.Debug("[VisualEffectReducer] Recorded transparency original value: {Value}", currentValue);
                }
                else
                {
                    // Value doesn't exist, record sentinel
                    snapshot.RecordRegistryValue(TransparencyKeyPath, TransparencyValueName, "__NOT_SET__");
                    SettingsManager.Logger.Debug("[VisualEffectReducer] Transparency value did not exist, recorded sentinel");
                }

                // Set transparency to disabled (0)
                Registry.SetValue(TransparencyKeyPath, TransparencyValueName, 0, RegistryValueKind.DWord);
                SettingsManager.Logger.Information("[VisualEffectReducer] Disabled Windows transparency");
                transparencySuccess = true;
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex, "[VisualEffectReducer] Failed to disable transparency, continuing with animations");
            }

            // Disable Animations
            try
            {
                IntPtr animInfoPtr = IntPtr.Zero;

                try
                {
                    // Get current animation state
                    var animInfo = new NativeInterop.ANIMATIONINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>()
                    };

                    animInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.ANIMATIONINFO>());
                    Marshal.StructureToPtr(animInfo, animInfoPtr, false);

                    if (NativeInterop.SystemParametersInfo(
                        NativeInterop.SPI_GETANIMATION,
                        (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                        animInfoPtr,
                        0))
                    {
                        animInfo = Marshal.PtrToStructure<NativeInterop.ANIMATIONINFO>(animInfoPtr);
                        _originalAnimationState = animInfo.iMinAnimate != 0;
                        SettingsManager.Logger.Debug("[VisualEffectReducer] Recorded animation original state: {State}",
                            _originalAnimationState ? "enabled" : "disabled");

                        // Disable animations (set iMinAnimate to 0)
                        animInfo.iMinAnimate = 0;
                        Marshal.StructureToPtr(animInfo, animInfoPtr, true);

                        if (NativeInterop.SystemParametersInfo(
                            NativeInterop.SPI_SETANIMATION,
                            (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                            animInfoPtr,
                            NativeInterop.SPIF_SENDCHANGE))
                        {
                            SettingsManager.Logger.Information("[VisualEffectReducer] Disabled Windows animations");
                            animationSuccess = true;
                        }
                        else
                        {
                            SettingsManager.Logger.Warning("[VisualEffectReducer] Failed to set animation state");
                        }
                    }
                    else
                    {
                        SettingsManager.Logger.Warning("[VisualEffectReducer] Failed to get current animation state");
                    }
                }
                finally
                {
                    if (animInfoPtr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(animInfoPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex, "[VisualEffectReducer] Failed to disable animations");
            }

            // Consider success if at least one optimization applied
            if (transparencySuccess || animationSuccess)
            {
                _isApplied = true;
                SettingsManager.Logger.Information("[VisualEffectReducer] Visual effect reduction applied (transparency: {Trans}, animations: {Anim})",
                    transparencySuccess, animationSuccess);
                return Task.FromResult(true);
            }
            else
            {
                SettingsManager.Logger.Warning("[VisualEffectReducer] Both transparency and animation optimizations failed");
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[VisualEffectReducer] Failed to apply visual effect reduction");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Reverts visual effect optimization by restoring transparency and animation settings.
    /// Reads original values from snapshot and restores them.
    /// </summary>
    /// <param name="snapshot">Snapshot containing original state to restore</param>
    /// <returns>True if revert succeeded, false otherwise</returns>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        bool transparencySuccess = false;
        bool animationSuccess = false;

        try
        {
            SettingsManager.Logger.Information("[VisualEffectReducer] Reverting visual effect reduction");

            // Restore Transparency
            try
            {
                string compositeKey = $"{TransparencyKeyPath}\\{TransparencyValueName}";

                if (snapshot.RegistryValues.TryGetValue(compositeKey, out object? originalValue))
                {
                    if (originalValue is string strValue && strValue == "__NOT_SET__")
                    {
                        // Value didn't exist originally, delete it
                        try
                        {
                            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                writable: true);

                            if (key != null)
                            {
                                key.DeleteValue(TransparencyValueName, throwOnMissingValue: false);
                                SettingsManager.Logger.Information("[VisualEffectReducer] Deleted transparency value (was not originally set)");
                            }
                        }
                        catch (Exception ex)
                        {
                            SettingsManager.Logger.Warning(ex, "[VisualEffectReducer] Failed to delete transparency value");
                        }
                    }
                    else
                    {
                        // Restore original value
                        Registry.SetValue(TransparencyKeyPath, TransparencyValueName, originalValue, RegistryValueKind.DWord);
                        SettingsManager.Logger.Information("[VisualEffectReducer] Restored transparency to original value: {Value}", originalValue);
                    }

                    transparencySuccess = true;
                }
                else
                {
                    SettingsManager.Logger.Debug("[VisualEffectReducer] No transparency value recorded in snapshot, skipping restore");
                    transparencySuccess = true; // Not an error - might not have been recorded
                }
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex, "[VisualEffectReducer] Failed to restore transparency, continuing with animations");
            }

            // Restore Animations
            try
            {
                if (_originalAnimationState)
                {
                    IntPtr animInfoPtr = IntPtr.Zero;

                    try
                    {
                        var animInfo = new NativeInterop.ANIMATIONINFO
                        {
                            cbSize = (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                            iMinAnimate = 1 // Enable animations
                        };

                        animInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.ANIMATIONINFO>());
                        Marshal.StructureToPtr(animInfo, animInfoPtr, false);

                        if (NativeInterop.SystemParametersInfo(
                            NativeInterop.SPI_SETANIMATION,
                            (uint)Marshal.SizeOf<NativeInterop.ANIMATIONINFO>(),
                            animInfoPtr,
                            NativeInterop.SPIF_SENDCHANGE))
                        {
                            SettingsManager.Logger.Information("[VisualEffectReducer] Restored Windows animations");
                            animationSuccess = true;
                        }
                        else
                        {
                            SettingsManager.Logger.Warning("[VisualEffectReducer] Failed to restore animation state");
                        }
                    }
                    finally
                    {
                        if (animInfoPtr != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(animInfoPtr);
                        }
                    }
                }
                else
                {
                    SettingsManager.Logger.Debug("[VisualEffectReducer] Animations were originally disabled, no restore needed");
                    animationSuccess = true;
                }
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex, "[VisualEffectReducer] Failed to restore animations");
            }

            _isApplied = false;

            // Consider success if at least one revert succeeded
            if (transparencySuccess || animationSuccess)
            {
                SettingsManager.Logger.Information("[VisualEffectReducer] Visual effect reduction reverted (transparency: {Trans}, animations: {Anim})",
                    transparencySuccess, animationSuccess);
                return Task.FromResult(true);
            }
            else
            {
                SettingsManager.Logger.Warning("[VisualEffectReducer] Both transparency and animation reverts failed");
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[VisualEffectReducer] Failed to revert visual effect reduction");
            return Task.FromResult(false);
        }
    }
}
