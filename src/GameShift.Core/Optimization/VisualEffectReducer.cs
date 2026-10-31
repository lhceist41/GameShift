using System.Runtime.InteropServices;
using System.Text.Json;
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

    internal RecoveryRegistry RecoveryRegistry { get; init; } = RecoveryRegistry.Windows;

    /// <summary>Turns Windows animations on; a seam so the journal revert can be tested without SPI.</summary>
    internal Func<bool> EnableAnimation { get; init; } = () => TrySetAnimation(out _, enable: true);

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

            var notRestored = new List<string>();

            // ── Restore transparency registry value ────────────────────────
            // A failure here must not stop the animation restore below (live Revert() keeps the two
            // independent too).
            if (values.TryGetValue(TransparencyValueName, out var transparencyElement))
            {
                if (RecoveryRegistry.RestoreDword(
                        RecoveryHive.CurrentUser, TransparencyRegistrySubKey, TransparencyValueName, transparencyElement))
                {
                    _logger.Information("[VisualEffectReducer] Restored transparency from the journal record");
                }
                else
                {
                    _logger.Warning("[VisualEffectReducer] Could not restore transparency from the journal record");
                    notRestored.Add("transparency");
                }
            }

            // ── Restore animation state ────────────────────────────────────
            if (values.TryGetValue(AnimationKey, out var animationElement))
            {
                // Only drive the SPI call if the original state differed from the
                // applied state. If animations were already off before apply, skip.
                if (animationElement.ValueKind == JsonValueKind.True)
                {
                    if (EnableAnimation())
                    {
                        _logger.Information("[VisualEffectReducer] Restored Windows animations (enabled)");
                    }
                    else
                    {
                        _logger.Warning("[VisualEffectReducer] Failed to restore animation state");
                        notRestored.Add("animations");
                    }
                }
                else if (animationElement.ValueKind == JsonValueKind.False)
                {
                    _logger.Debug(
                        "[VisualEffectReducer] Animations were originally disabled, no restore needed");
                }
                else
                {
                    _logger.Warning("[VisualEffectReducer] Animation state in the journal record is not a boolean");
                    notRestored.Add("animations");
                }
            }

            _isApplied = false;
            return notRestored.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : RevertFail($"Not restored: {string.Join(", ", notRestored)}");
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
