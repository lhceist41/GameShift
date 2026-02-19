using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that shows a tip once per tip ID, then never again.
/// Dismissal state is tracked in AppSettings.DismissedTips and persisted to settings.json.
/// Apply shows the tip (if not already dismissed) and marks it dismissed.
/// Revert is a no-op — tips are one-time events, not reversible.
/// Optionally hardware-conditional — only shown when hardware matches.
/// </summary>
public class OneTimeTipAction : GameAction
{
    private readonly string _tipId;
    private readonly string _tipMessage;
    private readonly Func<HardwareScanResult, bool>? _hardwareCondition;
    private readonly string _conditionText;

    /// <param name="tipId">Unique tip identifier, e.g. "valorant_xmp". Persisted to settings.json.</param>
    /// <param name="tipMessage">Human-readable tip message to display once.</param>
    public OneTimeTipAction(string tipId, string tipMessage)
    {
        _tipId = tipId;
        _tipMessage = tipMessage;
        _hardwareCondition = null;
        _conditionText = "";
    }

    /// <param name="tipId">Unique tip identifier.</param>
    /// <param name="tipMessage">Human-readable tip message.</param>
    /// <param name="hardwareCondition">Optional predicate — tip only shown when hardware matches.</param>
    /// <param name="conditionText">Human-readable condition description for UI display.</param>
    public OneTimeTipAction(string tipId, string tipMessage,
        Func<HardwareScanResult, bool>? hardwareCondition,
        string conditionText = "")
    {
        _tipId = tipId;
        _tipMessage = tipMessage;
        _hardwareCondition = hardwareCondition;
        _conditionText = conditionText;
    }

    /// <inheritdoc/>
    public override string Name => $"Tip: {_tipId}";

    /// <inheritdoc/>
    public override int Tier => 3;

    /// <inheritdoc/>
    public override string Impact => "Tip";

    /// <inheritdoc/>
    public override bool IsConditional => _hardwareCondition != null;

    /// <inheritdoc/>
    public override string Condition => _conditionText;

    /// <inheritdoc/>
    public override bool IsHardwareMatch(HardwareScanResult hw) =>
        _hardwareCondition?.Invoke(hw) ?? true;

    /// <inheritdoc/>
    public override void Apply(SystemStateSnapshot snapshot)
    {
        try
        {
            var settings = SettingsManager.Load();

            if (settings.DismissedTips.Contains(_tipId))
            {
                Log.Debug(
                    "OneTimeTipAction: Tip '{TipId}' already dismissed, skipping",
                    _tipId);
                return;
            }

            // Log tip — Phase 11 will wire this to a toast notification
            Log.Information(
                "OneTimeTipAction: {TipMessage} [Tip ID: {TipId}]",
                _tipMessage, _tipId);

            settings.DismissedTips.Add(_tipId);
            SettingsManager.Save(settings);

            Log.Information(
                "OneTimeTipAction: Tip '{TipId}' shown and marked as dismissed",
                _tipId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "OneTimeTipAction: Failed to apply tip '{TipId}'",
                _tipId);
        }
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        // Tips are one-time events — nothing to revert
        Log.Debug("OneTimeTipAction: Revert is no-op for tips");
    }
}
