namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Shared safety guards for per-game actions: the anti-cheat process blocklist that
/// <see cref="ProcessPrioritySetAction"/> checks before touching a process.
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
