using System.Diagnostics;

namespace GameShift.Core.GameProfiles;

/// <summary>
/// Static class returning the 9 hardcoded built-in game profiles.
/// </summary>
public static class BuiltInProfiles
{
    public static IReadOnlyList<GameProfile> GetAll() => new[]
    {
        Overwatch2(),
        Valorant(),
        LeagueOfLegends(),
        Deadlock(),
        Osu(),
        ArknightsEndfield(),
        WutheringWaves(),
        GenshinImpact(),
        Soulframe()
    };

    public static GameProfile Overwatch2() => new()
    {
        Id = "overwatch2",
        DisplayName = "Overwatch 2",
        ProcessNames = new[] { "Overwatch.exe" },
        LauncherProcessNames = new[] { "Battle.net.exe", "Agent.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 512,
        GamingFreeMemoryThresholdMB = 3072,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisableHags", "DisableMpo", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisablePowerThrottling" },
        Notes = new[]
        {
            "OW2 has a documented memory leak — RAM usage climbs from ~3GB to 7-8GB over extended sessions. Aggressive standby list cleaning helps.",
            "Battle.net launcher consumes significant CPU. Profile sets it to Below Normal priority during gameplay.",
            "On Intel hybrid CPUs (12th-14th gen), E-cores hurt OW2 performance. Profile auto-pins to P-cores only.",
            "NVIDIA users: Enable Reflex (On + Boost) in OW2 settings. Use Fullscreen (not Borderless). Set Shader Cache to 10GB in NVIDIA Control Panel.",
            "Known bug: Alt-tabbing breaks Reduce Buffering — toggle it OFF then ON after returning to game.",
            "Mouse polling rate: Use 1000Hz. OW2 drops from 600 to 60 FPS at 8000Hz polling."
        }
    };

    public static GameProfile Valorant() => new()
    {
        Id = "valorant",
        DisplayName = "Valorant",
        ProcessNames = new[] { "VALORANT-Win64-Shipping.exe" },
        LauncherProcessNames = new[] { "RiotClientServices.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = null,
        GamingFreeMemoryThresholdMB = null,
        RecommendedTweaks = new[] { "DisableGameDvr", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisablePowerThrottling" },
        Notes = new[]
        {
            "Valorant uses Vanguard kernel-level anti-cheat (Ring 0). Process priority/affinity changes are safe — Vanguard targets gameplay memory manipulation, not system optimization tools.",
            "NVIDIA users: Enable Reflex (On + Boost) in Valorant settings. AMD users: Enable Radeon Anti-Lag (NOT Anti-Lag+).",
            "128-tick servers update faster than 60 FPS can display — aim for 240+ FPS for competitive play.",
            "CPU-bound game favoring single-core performance. Timer resolution and priority boost have measurable impact.",
            "Mouse polling rates above 2000Hz cause engine stuttering — use 1000Hz.",
            "HAGS recommendation: Community split. Not included in recommended tweaks — test individually."
        }
    };

    public static GameProfile LeagueOfLegends() => new()
    {
        Id = "leagueoflegends",
        DisplayName = "League of Legends",
        ProcessNames = new[] { "League of Legends.exe" },
        LauncherProcessNames = new[] { "RiotClientServices.exe", "LeagueClient.exe", "LeagueClientUx.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = false,
        AffinityMask = null,
        GamingStandbyThresholdMB = null,
        GamingFreeMemoryThresholdMB = null,
        RecommendedTweaks = new[] { "DisableGameDvr", "OptimizeMmcss", "DisablePowerThrottling" },
        Notes = new[]
        {
            "LoL's gameplay loop runs primarily on a single thread. Single-core CPU performance matters most.",
            "LoL now uses Vanguard anti-cheat (same as Valorant). Priority/affinity changes are safe.",
            "The League Client (Electron-based) is a significant resource hog. Profile demotes all client processes to Below Normal.",
            "Lightweight game — if you're not hitting 144+ FPS, background processes are likely the bottleneck, not hardware.",
            "Timer resolution from Background Mode helps with LoL's frame pacing."
        }
    };

    public static GameProfile Deadlock() => new()
    {
        Id = "deadlock",
        DisplayName = "Deadlock",
        ProcessNames = new[] { "deadlock.exe" },
        LauncherProcessNames = Array.Empty<string>(),
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = null,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 2048,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisableMpo", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisablePowerThrottling" },
        Notes = new[]
        {
            "Source 2 engine is heavily CPU-dependent. Minion AI and ability physics tax the processor — Shadows and Physics are the most CPU-expensive settings.",
            "Intel hybrid CPUs: Confirmed E-core performance issue. Users report 50+ FPS gain from P-core-only affinity on 13th gen.",
            "DX11 is more consistent than Vulkan on most setups. Vulkan may help on CPU-limited configs.",
            "Uses VAC (not kernel-level) — all optimization tools are safe.",
            "DLSS/FSR2 support available for GPU-bound scenarios. Heavy VRAM usage: 6-9GB at higher settings."
        }
    };

    public static GameProfile Osu() => new()
    {
        Id = "osu",
        DisplayName = "osu!",
        ProcessNames = new[] { "osu!.exe" },
        LauncherProcessNames = Array.Empty<string>(),
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = null,
        IntelHybridPCoreOnly = false,
        AffinityMask = null,
        GamingStandbyThresholdMB = null,
        GamingFreeMemoryThresholdMB = null,
        RecommendedTweaks = new[] { "DisableGameDvr", "OptimizeWin32PrioritySeparation", "DisablePowerThrottling" },
        Notes = new[]
        {
            "Extremely latency-sensitive rhythm game. Timer resolution (0.5ms from Background Mode) is the single most impactful optimization.",
            "MUST use exclusive fullscreen (not borderless) — adds up to one full frame of latency otherwise.",
            "Frame limiter: Set to Unlimited if your PC sustains 500+ FPS without drops, otherwise use Optimal. Never use VSync.",
            "Disable fullscreen optimizations on osu!.exe via Compatibility tab.",
            "Raw Input: ON. This is critical for consistent aim.",
            "NVIDIA users: Set Low Latency Mode to Ultra and Max Pre-Rendered Frames to 1 in NVIDIA Control Panel.",
            "On laptops with hybrid GPU (Optimus): osu! often runs better on integrated graphics. Test both."
        }
    };

    public static GameProfile ArknightsEndfield() => new()
    {
        Id = "arknights-endfield",
        DisplayName = "Arknights: Endfield",
        ProcessNames = new[] { "Endfield.exe" },
        LauncherProcessNames = new[] { "Launcher.exe", "PlatformProcess.exe", "QtWebEngineProcess.exe", "CefViewWing.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 3072,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss" },
        Notes = new[]
        {
            "Modified Unity engine — main-thread dominant with 6-8 worker threads. CPU bottleneck during multi-operator battles and zone transitions.",
            "Uses ACE (Anti-Cheat Expert) by Tencent in an obfuscated variant. Likely USER-MODE only (game runs on Linux via Proton which cannot execute kernel drivers). Process priority and ISLC are safe. Third-party filter/frame interpolation apps are officially confirmed to cause freezes.",
            "Memory leak confirmed — performance degrades over extended sessions. ISLC standby cleaning enabled. Restart the game every 2-3 hours if you notice increasing stutter.",
            "Default renderer is Vulkan. DX11 is the safer fallback for crash-prone systems (toggle in launcher near Play button). Vulkan can be unstable on Intel/AMD GPU drivers.",
            "DLSS 4 with Multi Frame Generation supported day-one (RTX 50 series gets ~3x FPS at 4K). DLSS Balanced recommended. Frame Generation adds input latency — skip for action combat. NVIDIA Reflex supported but mutually exclusive with Frame Generation.",
            "NO FSR or XeSS support — AMD users only get TAAU (Temporal AA Upscaling) as a non-DLSS alternative.",
            "Shader compilation stutter is a documented issue, especially on first load. The 'Releasing Resources' hang involves asset unpacking — SSD installation strongly recommended.",
            "NVIDIA App auto-optimization causes blurriness and performance issues — disable 'Automatically optimize newly added games' (official Gryphline support notice).",
            "Scene Details, Volumetric Fog, Ambient Details, and Vegetation Density are the biggest CPU-side FPS drains. Lower these before touching resolution.",
            "Max FPS cap is 480 FPS. V-Sync OFF, use G-Sync/FreeSync instead."
        }
    };

    public static GameProfile WutheringWaves() => new()
    {
        Id = "wuthering-waves",
        DisplayName = "Wuthering Waves",
        ProcessNames = new[] { "Client-Win64-Shipping.exe" },
        LauncherProcessNames = new[] { "launcher.exe", "launcher_epic.exe", "KRLauncherEpic" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 3072,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation" },
        Notes = new[]
        {
            "Unreal Engine 4.26.2.0 (Kuro Games fork — NOT UE5). Heavily 2-thread dominant — in dense areas, 2 cores hit 100% while the rest idle. This is the primary performance bottleneck and cannot be fixed by settings alone.",
            "Uses ACE (Anti-Cheat Expert) KERNEL-LEVEL (Ring 0) driver by Tencent. ACE_BASE.sys loads at game launch. CPU overhead is SEVERE — GPUView analysis shows ACE uses as much CPU as the game's own renderer during loading. System-wide stuttering begins at login when ACE activates.",
            "ACE causes KERNEL_SECURITY_CHECK_FAILURE BSODs on some systems. The driver FAILS Windows Driver Verifier testing. If you experience blue screens while playing WuWa, the anti-cheat driver is the likely cause.",
            "BSOD mitigation: Enable 'Kernel-mode Hardware-enforced Stack Protection' in Windows Security (isolates driver memory from ACE). Disable 'USB Selective Suspend' in power plan (confirmed to reduce ACE-related stuttering).",
            "Game fails to trigger proper GPU boost clocks — sometimes idles at ~1200 MHz instead of 2000+ MHz. Set NVIDIA Control Panel Power Management Mode to 'Prefer Maximum Performance' for Client-Win64-Shipping.exe.",
            "DX12 is default since launcher 1.6.2 for RTX 20+ GPUs and is REQUIRED for DLSS 3, Reflex, Ray Tracing, and XeSS. DX12 provides significant uplift in complex scenes but causes crashes on some GPUs (RX 7800 XT, RTX 3080 documented). DX11 fallback available.",
            "DLSS 3 Frame Generation (RTX 40/50, DX12), NVIDIA Reflex (RTX 40+, DX12), FSR3 Frame Generation (AMD RX 6000/7000/9000 only), Intel XeSS (DX12). All require DX12.",
            "Game runs borderless windowed by default (no native exclusive fullscreen). Fix: disable fullscreen optimizations on Client-Win64-Shipping.exe (right-click > Properties > Compatibility).",
            "Config: Engine.ini at <install>\\Wuthering Waves Game\\Client\\Saved\\Config\\WindowsNoEditor\\Engine.ini — set to READ-ONLY if changes revert. GameUserSettings.ini resets on launch, don't edit.",
            "Launch args (direct shortcut to .exe, Steam may not pass through): -dx11, -dx12, -SkipSplash, -USEALLAVAILABLECORES",
            "Close background apps aggressively. Discord overlay, MSI Afterburner overlay, and RGB software all increase ACE's CPU overhead and have been flagged.",
            "Alt-tabbing triggers stuttering spikes (ACE re-initializes checks on focus change). Minimize tab-switching.",
            "Install on NVMe SSD — community reports ~70% stutter reduction vs SATA SSD. Shader compilation stutter is a major UE4 issue, especially after patches and driver changes.",
            "Restart game every 3-4 hours for memory leak. Only a full PC restart reclaims all leaked memory."
        }
    };

    public static GameProfile GenshinImpact() => new()
    {
        Id = "genshin-impact",
        DisplayName = "Genshin Impact",
        ProcessNames = new[] { "GenshinImpact.exe", "YuanShen.exe" },
        LauncherProcessNames = new[] { "launcher.exe", "HoYoPlay.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = false,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 3072,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss" },
        Notes = new[]
        {
            "Custom-modified Unity engine forked from Unity 2017.4.30 LTS. Settings stored in Windows Registry at HKCU\\Software\\miHoYo\\Genshin Impact (not config files). Delete the key to reset all settings.",
            "Uses HoYoKProtect.sys / mhyprot3.sys kernel-level anti-cheat (Ring 0, proprietary). Loads only during gameplay, does NOT persist at boot or after closing. Replaced the notorious mhyprot2.sys after v3.2.",
            "Anti-cheat runs a validation loop every 100 seconds — negligible CPU overhead during normal gameplay. ISLC, RTSS, MSI Afterburner, and Lossless Scaling are all safe (widely used without bans).",
            "DX11 ONLY — no DX12, no Vulkan option. 'Fullscreen' mode still composites through DWM (no true exclusive fullscreen). Borderless workaround: -screen-fullscreen 0 -popupwindow launch args.",
            "NO DLSS, NO Reflex, NO native Frame Generation. AMD FSR 2 is the only upscaler (added v3.2). Users rely on Lossless Scaling (third-party) for frame generation.",
            "FPS hardcapped at 60. Third-party FPS unlockers are NOW HIGH RISK as of Feb 2025 — miHoYo added targetFramerate property detection (error 10310-4001 followed by bans).",
            "Native frame limiter has notoriously poor frame pacing. Use RTSS external limiter at 58 FPS for 60Hz targets. NVIDIA CP or AMD FRTC at 58 FPS also works.",
            "Memory leak via fast-travel pattern — VRAM accumulates without being freed. Restart every 1-2 hours during heavy exploration if you have 8GB VRAM or less.",
            "Uses KCP protocol over UDP for gameplay (ports UDP 22101-22102, 42472; TCP 42472). Nagle's algorithm tweaks are irrelevant for gameplay traffic since it's UDP. Standard TCP registry tweaks only affect auth/API.",
            "Environment Detail is the biggest CPU performance lever — keep at Medium or lower. Crowd Density to Low. Volumetric Fog and Reflections are expensive for marginal visual gain.",
            "Unity config dialog: hold Shift while clicking Launch. Launch args: -screen-fullscreen 0 -popupwindow (borderless), -screen-width 1920 -screen-height 1080 (set resolution).",
            "Set NVIDIA CP: Power Management to Prefer Max Performance, V-Sync Off. Enable 100GB shader cache in NVIDIA CP to reduce shader compilation stutter.",
            "Compatibility fix: right-click GenshinImpact.exe > Compatibility > 'Disable fullscreen optimizations' to fix 'Bad Module Info' crashes and reduce DWM latency."
        }
    };

    public static GameProfile Soulframe() => new()
    {
        Id = "soulframe",
        DisplayName = "Soulframe (Placeholder)",
        ProcessNames = new[] { "Soulframe.x64.exe", "Soulframe.exe" },
        LauncherProcessNames = new[] { "Launcher.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = false,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 2048,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss" },
        Notes = new[]
        {
            "\u26a0\ufe0f PLACEHOLDER PROFILE — Soulframe is in pre-alpha ('Preludes'). Process names and optimization details are based on Warframe (same Evolution Engine, same studio Digital Extremes). This profile will be updated at launch.",
            "Evolution Engine (C++, proprietary) with job-based multi-threaded renderer. CRITICAL: verify 'Multi-threaded rendering' is ENABLED in the launcher — this single toggle provides 50-100% FPS improvement and is the most impactful setting.",
            "Warframe uses CUSTOM USER-MODE anti-cheat (NOT EAC, NOT BattlEye, NOT kernel-level). All system optimization tools (ISLC, Process Lasso, RTSS, MSI Afterburner) are fully compatible. If Soulframe follows suit, this is the safest profile for optimization tools.",
            "DX11 (default, stable) and DX12 (toggle in launcher — slightly faster, better multi-core scaling, but known AMD GPU crashes). DLSS supported (RTX cards), FSR 2.2 supported (all GPUs), XeSS supported (DX12 only). No native Frame Generation.",
            "'Reduce Frame Latency' is a DE-proprietary setting (NOT NVIDIA Reflex) — reduces input-to-display time AND VRAM usage. Available on DX11 and DX12. Disabled by default, requires restart.",
            "Warframe uses PEER-TO-PEER networking for mission gameplay (one player hosts). Ports: UDP 4950/4955, TCP 6695-6699. Port forwarding recommended over UPnP. If Soulframe is MMORPG with dedicated servers, networking profile will change significantly.",
            "Config file: %LocalAppData%\\Warframe\\EE.cfg (text format, fully editable). Settings not present use system defaults. Can be backed up and transferred between PCs.",
            "Launcher options: DX11/DX12 toggle, Multi-threaded rendering toggle, Verify/Optimize download cache. 'Get Logs' exports EE.cfg + logs to Desktop for debugging.",
            "Open-world areas (Plains of Eidolon, Orb Vallis, Cambion Drift, Duviri) with dynamic lighting are the most CPU-intensive. Soulframe's persistent open world will amplify this.",
            "Known issue: Intel 13th & 14th Gen CPU crashes (related to Intel voltage/microcode instability, not game-specific).",
            "This profile will be updated when Soulframe officially launches. Check for GameShift updates post-launch."
        }
    };
}
