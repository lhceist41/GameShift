namespace GameShift.Core.Optimization;

/// <summary>
/// Identifies the anti-cheat system used by a game.
/// Used to determine optimization strategy (e.g., runtime API vs IFEO registry fallback)
/// and VBS/HVCI safety gating.
/// </summary>
public enum AntiCheatType
{
    /// <summary>No anti-cheat or user-mode only (OW2 server-side, osu!, Cyberpunk, Minecraft, FFXIV, Soulframe).</summary>
    None = 0,

    /// <summary>Riot Vanguard (Ring 0). Valorant, League of Legends. Requires VBS/HVCI enabled.</summary>
    RiotVanguard,

    /// <summary>Easy Anti-Cheat (Ring 0). Fortnite, Apex Legends, Rust, Elden Ring, Nightreign. Blocks runtime priority/affinity.</summary>
    EasyAntiCheat,

    /// <summary>BattlEye (Ring 0). Arknights Endfield. Blocks runtime priority/affinity.</summary>
    BattlEye,

    /// <summary>FACEIT Anti-Cheat (Ring 0). CS2 on FACEIT. Requires VBS/HVCI enabled.</summary>
    FaceitAC,

    /// <summary>Valve Anti-Cheat (Ring 3, user-mode). CS2, Deadlock. Does not block priority/affinity.</summary>
    ValveAntiCheat,

    /// <summary>RICOCHET (Ring 0). Call of Duty. May block runtime priority — IFEO safer.</summary>
    Ricochet,

    /// <summary>Tencent ACE (Ring 0 kernel driver). Wuthering Waves. IFEO safer.</summary>
    TencentACE,

    /// <summary>Proprietary anti-cheat (varies). Genshin Impact (mHYProtect/HoYoKProtect).</summary>
    Proprietary
}
