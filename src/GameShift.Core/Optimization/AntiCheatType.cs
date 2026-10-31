namespace GameShift.Core.Optimization;

/// <summary>
/// Identifies the anti-cheat system used by a game.
/// Used to determine optimization strategy (e.g., runtime API vs IFEO registry fallback)
/// and VBS/HVCI safety gating.
/// Saved profiles store the numeric value, so members are only ever appended.
/// </summary>
public enum AntiCheatType
{
    /// <summary>No anti-cheat or user-mode only (Overwatch server-side, osu!, Cyberpunk, Minecraft, FFXIV, Soulframe).</summary>
    None = 0,

    /// <summary>Riot Vanguard (Ring 0). Valorant, League of Legends. Requires VBS/HVCI enabled.</summary>
    RiotVanguard,

    /// <summary>Easy Anti-Cheat (Ring 0). Fortnite, Rust, Elden Ring, Nightreign. Blocks runtime priority/affinity.</summary>
    EasyAntiCheat,

    /// <summary>BattlEye (Ring 0). Blocks runtime priority/affinity.</summary>
    BattlEye,

    /// <summary>FACEIT Anti-Cheat (Ring 0). CS2 on FACEIT. Requires VBS/HVCI enabled.</summary>
    FaceitAC,

    /// <summary>Valve Anti-Cheat (Ring 3, user-mode). CS2, Deadlock. Does not block priority/affinity.</summary>
    ValveAntiCheat,

    /// <summary>RICOCHET (Ring 0). Call of Duty. May block runtime priority - IFEO safer.</summary>
    Ricochet,

    /// <summary>Tencent ACE (Ring 0 kernel driver). Wuthering Waves, Arknights: Endfield. IFEO safer.</summary>
    TencentACE,

    /// <summary>Proprietary anti-cheat (varies). Genshin Impact (mHYProtect/HoYoKProtect).</summary>
    Proprietary,

    /// <summary>EA Javelin Anticheat (Ring 0). Apex Legends. Handled like Easy Anti-Cheat.</summary>
    EaJavelin
}
