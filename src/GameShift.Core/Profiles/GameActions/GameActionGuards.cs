namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Shared safety guards for per-game actions. Single source of truth for the anti-cheat process
/// blocklist so <see cref="ProcessSuspendAction"/> and <see cref="ProcessPrioritySetAction"/> can
/// never drift apart (e.g. one being updated while the other lags).
/// </summary>
public static class GameActionGuards
{
    /// <summary>
    /// Processes that must never be suspended or have their priority changed - anti-cheat software
    /// and security agents. Case-insensitive; entries include the ".exe" extension.
    /// </summary>
    public static readonly IReadOnlySet<string> AntiCheatBlocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "vgc.exe",
        "vgtray.exe",
        "EasyAntiCheat.exe",
        "EasyAntiCheat_EOS.exe",
        "BEService.exe",
        "BattlEye.exe",
        "FACEITClient.exe",
    };
}
