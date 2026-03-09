using System.Diagnostics;
using GameShift.Core.Optimization;

namespace GameShift.Core.GameProfiles;

/// <summary>
/// Static class returning the 19 hardcoded built-in game profiles.
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
        Soulframe(),
        CounterStrike2(),
        Fortnite(),
        ApexLegends(),
        Rust(),
        EldenRing(),
        EldenRingNightreign(),
        CallOfDuty(),
        Cyberpunk2077(),
        MinecraftJava(),
        FinalFantasyXiv()
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
        AntiCheat = AntiCheatType.None, // Server-side ML detection only (RICOCHET)
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
        AntiCheat = AntiCheatType.RiotVanguard,
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
        AntiCheat = AntiCheatType.RiotVanguard,
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
        AntiCheat = AntiCheatType.ValveAntiCheat,
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
        AntiCheat = AntiCheatType.None,
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
        AntiCheat = AntiCheatType.BattlEye, // Uses ACE (Tencent) in a BattlEye-like variant
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
        AntiCheat = AntiCheatType.TencentACE,
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
        AntiCheat = AntiCheatType.Proprietary, // mHYProtect/HoYoKProtect
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
        AntiCheat = AntiCheatType.None, // Custom user-mode anti-cheat (same as Warframe)
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

    public static GameProfile CounterStrike2() => new()
    {
        Id = "counter-strike-2",
        DisplayName = "Counter-Strike 2",
        ProcessNames = new[] { "cs2.exe" },
        LauncherProcessNames = new[] { "steamwebhelper.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 8192,
        AntiCheat = AntiCheatType.ValveAntiCheat,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableHags", "DisableMpo" },
        Notes = new[]
        {
            "Source 2 engine -- heavily main-thread-bound. 1-2 cores saturate while others idle. On Intel hybrid CPUs (12th-14th gen), P-core-only affinity provides up to 15% FPS improvement (confirmed by Valve's own built-in CPU Core Usage Preference setting added Dec 2024).",
            "Uses VAC (Valve Anti-Cheat) -- FULLY USER-MODE (Ring 3). No kernel driver. Process priority, CPU affinity, ISLC, timer resolution, and all system optimization tools are completely safe. No confirmed VAC bans from any optimization tool, ever.",
            "VAC overhead is negligible -- brief sub-millisecond scans from steamservice.exe. VACNet/VAC Live (server-side AI) has zero client-side cost.",
            "Memory leak confirmed -- VRAM fills progressively over extended sessions causing FPS degradation. ISLC standby cleaning enabled. Restart the game every 1-2 hours if you notice declining FPS.",
            "0.5ms timer resolution is one of the MOST IMPACTFUL optimizations for CS2. The game does NOT call timeBeginPeriod() internally. Forcing 0.5ms produces 20-30% improvement in 1%/0.1% lows and eliminates visible microstutter. Average FPS is unchanged.",
            "DX11 outperforms Vulkan by ~18% on NVIDIA/AMD GPUs. Vulkan is only recommended for Linux and Intel Arc GPUs. Launch option: -vulkan to switch.",
            "Fullscreen Exclusive is MANDATORY for competitive play -- borderless adds DWM compositor overhead and measurable input lag. CS2 defaults to borderless windowed; players must manually switch. Disable Fullscreen Optimizations on cs2.exe.",
            "NVIDIA Reflex natively supported (up to 35% latency reduction). However, some advanced players report Reflex is 'broken' in CS2 (Jan 2025) and use -noreflex + NVIDIA CP Ultra Low Latency Mode instead.",
            "NO DLSS support (any version), NO Frame Generation. FSR 1.0 (spatial only) available but adds artifacts -- not recommended for competitive. AMD Anti-Lag 2 (driver 24.6.1+) is safe. WARNING: AMD Anti-Lag+ (the original version) caused VAC bans in 2023 -- those bans were reversed and the feature was pulled.",
            "Smoke rendering is the biggest FPS killer -- FPS drops from 200-240 to 70-100 with multiple smokes. Smoke-on-molotov can crater to 30 FPS. This is a CPU-side bottleneck that no settings can fix.",
            "Sub-tick system: 64Hz servers with high-precision timestamps per input. Physics/recoil behave like 128-tick CSGO. Sub-tick uses engine's own QueryPerformanceCounter timers, NOT the OS multimedia timer -- OS timer resolution does not affect sub-tick accuracy, but DOES affect frame scheduling/pacing.",
            "Config location: Steam\\steamapps\\common\\Counter-Strike Global Offensive\\game\\csgo\\cfg\\. Shader cache: steamapps\\shadercache\\730\\. Set NVIDIA Shader Cache Size to 10GB or Unlimited.",
            "Confirmed launch options: -novid -console -high -fullscreen -nojoy +fps_max 0 +exec autoexec.cfg. Non-functional in CS2: -d3d9ex -tickrate 128 -processheap +mat_queue_mode 2 -freq.",
            "steamwebhelper.exe is Steam's Chromium browser -- spawns 3-8 instances consuming 200MB-1.8GB RAM combined. On older CPUs, 10-30% CPU usage. Profile demotes it to Below Normal during gameplay.",
            "FACEIT Anti-Cheat is KERNEL-LEVEL (Ring 0) -- boots with OS, blocks Hyper-V and Memory Integrity. FACEIT AC may conflict with some optimization tools. This profile is for Valve matchmaking -- FACEIT users should be aware of the additional anti-cheat layer."
        }
    };

    public static GameProfile Fortnite() => new()
    {
        Id = "fortnite",
        DisplayName = "Fortnite",
        ProcessNames = new[] { "FortniteClient-Win64-Shipping.exe" },
        LauncherProcessNames = new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 2048,
        AntiCheat = AntiCheatType.EasyAntiCheat,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableMpo" },
        Notes = new[]
        {
            "Unreal Engine 5 -- Game Thread is the primary bottleneck during competitive play. Building triggers game thread spikes (object spawning, collision, network replication). Endgame with 30+ players is the worst case.",
            "Uses Easy Anti-Cheat (EAC) -- KERNEL-LEVEL (Ring 0) driver. EasyAntiCheat_EOS.sys loads when Fortnite launches and sometimes FAILS TO UNLOAD after game exit (persists until reboot). EAC BLOCKS direct SetProcessAffinityMask() and SetPriorityClass() on the game process.",
            "EAC LIMITATION: GameShift uses direct Win32 API calls for priority and affinity. EAC blocks cross-process handle access on the game executable, so priority and affinity settings from this profile may not apply to the game process itself. Launcher demotion, ISLC, timer resolution, and all system tweaks still work normally.",
            "Priority workaround: set via registry at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\FortniteClient-Win64-Shipping.exe\\PerfOptions with DWORD CpuPriorityClass = 3. This is applied at process creation before EAC protection activates.",
            "Affinity workaround: set affinity on EpicGamesLauncher.exe before Fortnite spawns -- child processes inherit affinity. Direct affinity changes on the game process will be blocked by EAC.",
            "ISLC, timer resolution tools, and MSI Afterburner are confirmed safe with EAC. Process Lasso has one isolated EAC ban report (different game, later reversed). RTSS overlays are blocked but never cause bans.",
            "Known EAC bugs: BSODs (DRIVER_VERIFIER_FAILURE, SYSTEM_THREAD_EXCEPTION_NOT_HANDLED), conflicts with ASUS AI Suite / Avast / some motherboard utilities, kernel scan loop bug that pushes System process to 30-40% CPU after game exit.",
            "0.5ms timer resolution provides 25-30% smoother frame-time consistency. Fortnite internally requests 1ms via timeBeginPeriod(1) -- overriding to 0.5ms halves the OS scheduler wake interval for additional benefit.",
            "Ultimate Performance power plan provides ~7% avg FPS gain with significantly reduced frame drops in dense areas. NOTE: AMD CPUs should stick with Balanced (AMD has its own power management).",
            "HAGS is VENDOR-DEPENDENT: NVIDIA should keep HAGS ON (marginal benefit, required for DLSS FG). AMD must turn HAGS OFF -- confirmed 39% degradation in 0.1% lows on RX 9070 XT. Not included in recommended tweaks due to this split.",
            "Three rendering modes: DX12 (default, enables Nanite/Lumen/TSR -- best for mid+ hardware), DX11 (legacy, more stable on old hardware), Performance Mode (-FeatureLevelES31 -- 20-50% FPS boost but items don't render until ~30m). DX12 has ~10% better 0.1% lows than DX11.",
            "NVIDIA Reflex natively integrated (up to 54% latency reduction on RTX 50). Reflex OVERRIDES NVCP Low Latency Mode -- do not stack both. NVCP LLM doesn't work in DX12 anyway.",
            "DLSS 2 Super Resolution supported (use Quality for competitive). DLSS 3/4 Frame Generation supported but adds latency. FSR is NOT natively supported -- UE5 TSR is the cross-platform upscaler. XeSS added Sept 2024.",
            "Server tick rate is 30 Hz for Battle Royale. For context: CS2 runs at 64 Hz, Valorant at 128 Hz.",
            "Nagle's algorithm tweaks (TcpNoDelay, TcpAckFrequency) are PLACEBO for Fortnite -- gameplay uses UDP, not TCP. These only affect matchmaking/chat TCP connections.",
            "EpicWebHelper.exe is Chromium-based, spawns 3-8 instances using 50-200MB RAM each with documented spikes to 26GB (memory leak). Epic launcher sends 14x more telemetry than Steam when idle. Profile demotes all launcher processes to Below Normal.",
            "PSO shader compilation causes first-match stuttering after driver updates (~30,000 PSOs compiled per match). Clear corrupted cache: delete %LOCALAPPDATA%\\NVIDIA\\DXCache (NVIDIA) or reset via AMD Software.",
            "Config: GameUserSettings.ini at %LOCALAPPDATA%\\FortniteGame\\Saved\\Config\\WindowsClient\\. Engine.ini tweaks are mostly blocked by Epic. Set GameUserSettings.ini to Read-Only after editing.",
            "Launch args: -USEALLAVAILABLECORES -NOSPLASH -LANPLAY -dx12 -high -fullscreen. Force Performance Mode: -FeatureLevelES31. Set in Epic Launcher > Settings > Fortnite > Additional Command Line Arguments.",
            "Competitive endgame (20-50+ players): RAM speed matters enormously -- 3600MHz CL16 provides 40% better 1% lows than 2133MHz in stacked endgames. Shadows OFF is the single biggest FPS gain (20-30 FPS)."
        }
    };

    public static GameProfile ApexLegends() => new()
    {
        Id = "apex-legends",
        DisplayName = "Apex Legends",
        ProcessNames = new[] { "r5apex_dx12.exe", "r5apex.exe" },
        LauncherProcessNames = new[] { "EABackgroundService.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 2048,
        AntiCheat = AntiCheatType.EasyAntiCheat,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableHags", "DisableMpo" },
        Notes = new[]
        {
            "Modified Source engine (Titanfall 2 fork) -- heavily main-thread-bound. Two threads carry most load while others are lightly utilized. CPU bottlenecks hard in endgame with many squads.",
            "Uses Easy Anti-Cheat (EAC) -- KERNEL-LEVEL (Ring 0). EAC blocks direct SetProcessAffinityMask() and SetPriorityClass() on r5apex_dx12.exe. Priority set via IFEO registry. Affinity via parent process inheritance.",
            "EAC LIMITATION: GameShift uses direct Win32 API calls for priority and affinity. EAC blocks cross-process handle access on the game executable, so priority and affinity settings from this profile may not apply to the game process itself. Launcher demotion, ISLC, timer resolution, and all system tweaks still work normally.",
            "Priority workaround: set via registry at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\r5apex_dx12.exe\\PerfOptions with DWORD CpuPriorityClass = 3. This is applied at process creation before EAC protection activates.",
            "CRITICAL: r5apex_dx12.exe frequently fails to terminate after game exit, leaving a zombie process consuming 40%+ system memory. If you notice high RAM usage after closing Apex, check Task Manager and manually end the process.",
            "VRAM memory leak tied to texture streaming budget. VRAM usage climbs continuously regardless of configured cap. Setting texture budget to 'None' caps VRAM at ~2GB and prevents the leak.",
            "On AMD multi-CCD CPUs (5900X, 5950X, 7900X, 7950X), Apex has a documented visual jitter bug even when FPS/frametimes appear normal. Workaround: restrict affinity to a single CCD.",
            "DX12 only (DX11 removed late 2024). No DLSS, FSR, or XeSS support -- only Adaptive Resolution Scaling.",
            "NVIDIA Reflex natively supported (Off/On/On+Boost) since Season 16.",
            "HAGS (Hardware Accelerated GPU Scheduling) causes severe stuttering in Apex, especially on AMD GPUs. Disabled by this profile.",
            "Server tick rate is 20Hz (confirmed by Respawn's April 2021 deep-dive, unchanged since). Nagle tweaks irrelevant -- gameplay uses UDP on ports 37000-40000.",
            "Config: videoconfig.txt at %USERPROFILE%\\Saved Games\\Respawn\\Apex\\local\\videoconfig.txt. Set to Read-Only after editing. autoexec.cfg at <Install Dir>\\cfg\\autoexec.cfg.",
            "Launch options: -novid +fps_max 0 -high -fullscreen. REMOVE -threads if present -- incorrect values hurt performance. FPS hard cap: 300.",
            "Shader cache corruption causes stutter after updates. Clear %ProgramData%\\NVIDIA Corporation\\NV_Cache\\ when performance degrades after a game update.",
            "0.5ms timer resolution improves frame pacing consistency. Apex does NOT call timeBeginPeriod() internally."
        }
    };

    public static GameProfile Rust() => new()
    {
        Id = "rust",
        DisplayName = "Rust",
        ProcessNames = new[] { "RustClient.exe" },
        LauncherProcessNames = new[] { "Rust.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 2048,
        AntiCheat = AntiCheatType.EasyAntiCheat,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableMpo" },
        Notes = new[]
        {
            "Unity engine -- aggressively main-thread-bound (4-6 effective threads). Single-core IPC is the dominant performance factor. Large L3 cache CPUs (5800X3D, 7800X3D) excel due to Unity's cache-sensitive memory access patterns.",
            "Uses Easy Anti-Cheat (EAC) -- KERNEL-LEVEL (Ring 0). Same process manipulation restrictions as other EAC titles. Priority via IFEO registry, affinity via parent process inheritance.",
            "EAC LIMITATION: GameShift uses direct Win32 API calls for priority and affinity. EAC blocks cross-process handle access on the game executable, so priority and affinity settings from this profile may not apply to the game process itself. Launcher demotion, ISLC, timer resolution, and all system tweaks still work normally.",
            "Priority workaround: set via registry at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\RustClient.exe\\PerfOptions with DWORD CpuPriorityClass = 3. This is applied at process creation before EAC protection activates.",
            "MOST RAM-DEMANDING game in GameShift's profile set. 16GB is genuinely insufficient for high-pop servers. Typical usage: 8-10GB fresh wipe, 13-16GB+ late wipe (200+ players). Memory grows continuously with no plateau.",
            "gc.buffer is the SINGLE MOST IMPACTFUL optimization for Rust. Controls Unity's incremental garbage collector buffer. Without it, GC runs cause micro-stutters every few seconds. Recommended: 2048 for 16GB systems, 4096 for 32GB. Set via launch option: -gc.buffer 2048",
            "In-game memory cleanup: open console (~) and run 'gc.collect; pool.clear_assets; pool.clear_memory; pool.clear_prefabs' -- reclaims 500MB-2GB with a brief freeze. Do this every 1-2 hours on long sessions.",
            "DX11 only (no DX12/Vulkan). DLSS natively supported (added July 2021, up to 50% boost at 4K). FSR available. NVIDIA Reflex natively supported (up to 38% latency reduction).",
            "Launch option '-window-mode exclusive' forces true exclusive fullscreen -- Rust defaults to a borderless hybrid mode with worse performance.",
            "SSD is MANDATORY -- Facepunch lists it as minimum requirement. HDD load times: 5-15+ minutes vs 1-3 minutes on NVMe.",
            "Known EAC bugs in Rust: BSODs (HYPERVISOR_ERROR, KERNEL_SECURITY_CHECK_FAILURE) during initialization.",
            "Server tick rate: 30 ticks/second. Gameplay uses UDP on port 28015. Nagle tweaks irrelevant (UDP).",
            "Performance killers: Grass Quality, Shadow Quality/Distance/Cascades, Tree/Mesh Quality. Console: graphics.lodbias 0.53, effects.maxgibs -1, sss.enable 0, graphics.waves 0, itemskins 0.",
            "Config: <Steam>\\steamapps\\common\\Rust\\cfg\\client.cfg. Launch options: -gc.buffer 2048 -gc.incremental_milliseconds 1 -forceUnloadBundles -window-mode exclusive. Most other circulated launch options are non-functional.",
            "0.5ms timer resolution provides frame pacing improvement. Ultimate Performance power plan strongly recommended for Rust's single-thread-bound workload."
        }
    };

    public static GameProfile EldenRing() => new()
    {
        Id = "elden-ring",
        DisplayName = "Elden Ring",
        ProcessNames = new[] { "eldenring.exe" },
        LauncherProcessNames = new[] { "start_protected_game.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 1024,
        AntiCheat = AntiCheatType.EasyAntiCheat,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableMpo" },
        Notes = new[]
        {
            "Custom FromSoftware engine -- 1 main rendering thread + 7 worker threads. GPU is massively underutilized; CPU command list processing time exceeds GPU processing time in most frames. The primary stutter is the buffer swap/present instruction stalling for 60ms+ on the CPU.",
            "Uses Easy Anti-Cheat (EAC) -- KERNEL-LEVEL (Ring 0). EAC blocks direct SetProcessAffinityMask() and SetPriorityClass() on eldenring.exe. Priority via IFEO registry. Affinity via parent process inheritance through start_protected_game.exe.",
            "EAC LIMITATION: GameShift uses direct Win32 API calls for priority and affinity. EAC blocks cross-process handle access on the game executable, so priority and affinity settings from this profile may not apply to the game process itself. Launcher demotion, ISLC, timer resolution, and all system tweaks still work normally.",
            "Priority workaround: set via registry at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\eldenring.exe\\PerfOptions with DWORD CpuPriorityClass = 3. This is applied at process creation before EAC protection activates.",
            "REMOVING CPU 0 FROM AFFINITY is the single most impactful optimization. Windows uses Core 0 for USB/HID interrupts, input polling, and kernel DPCs. When the main thread lands on CPU 0, interrupt contention causes severe frametime spikes. Requires ~25 second delay after launch (game needs CPU 0 during initialization). GameShift's affinity system applies immediately at detection -- for Elden Ring, manually removing CPU 0 after 25 seconds via Task Manager provides additional benefit.",
            "On Intel 12th-14th gen hybrid CPUs, Elden Ring shows the WORST stutter of any platform. PC Gamer Nsight analysis: 100ms+ spikes on i7-14700KF from E-core scheduling. P-core-only affinity + CPU 0 removal is critical.",
            "HARD 60 FPS CAP with game logic tied to framerate (inherited from Dark Souls 2 engine). Expected frame time: 16.67ms. Actual: regularly spikes to 30ms, severe stutters reach 60-100ms+.",
            "0.5ms timer resolution is CRITICAL for Elden Ring. Since every frame must land within a 16.67ms budget, even 0.5ms granularity improvement reduces late frame presentation. This is one of the highest-impact timer resolution games in the entire profile set.",
            "Fullscreen mode forces monitor to 60Hz even on higher-refresh panels. BORDERLESS WINDOWED is recommended -- allows native refresh rate, enables HAGS benefits, better alt-tab.",
            "Shader compilation stutter is the most notorious PC issue. DX12 PSOs compiled at runtime on first encounter with new areas/enemies/spells. No async pre-compilation. Set NVIDIA Shader Cache to Unlimited for eldenring.exe.",
            "Disable Control Flow Guard (CFG) in Windows Exploit Protection for eldenring.exe -- known to worsen DX12 compilation stalls.",
            "No native DLSS, FSR, XeSS, or NVIDIA Reflex. Ray tracing (patch 1.09) adds RTAO and RT Sun Shadows with 26-38% FPS reduction and minimal visual improvement.",
            "Multiplayer uses P2P (Peer-to-Peer). Steam P2P ports: TCP 27015, 27036; UDP 27015, 27031-27036. Open NAT recommended. Disabling IPv6 improves P2P stability.",
            "Config: C:\\Users\\<User>\\AppData\\Roaming\\EldenRing\\<SteamID>\\GraphicsConfig.xml (UTF-16 XML).",
            "EAC bypass (launching eldenring.exe directly) forces offline-only mode but gives 10-15 FPS improvement -- confirms EAC overhead is significant for this title.",
            "Community CPU 0 affinity fix available on Nexus Mods. GameShift automates P-core affinity without requiring mods."
        }
    };

    public static GameProfile EldenRingNightreign() => new()
    {
        Id = "elden-ring-nightreign",
        DisplayName = "Elden Ring: Nightreign",
        ProcessNames = new[] { "nightreign.exe" },
        LauncherProcessNames = new[] { "start_protected_game.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 1024,
        AntiCheat = AntiCheatType.EasyAntiCheat,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableMpo" },
        Notes = new[]
        {
            "Identical FromSoftware engine to base Elden Ring -- all Elden Ring optimizations apply directly. Same threading model (1 main + 7 workers), same DX12 renderer, same frame pacing characteristics.",
            "Uses Easy Anti-Cheat (EAC) -- KERNEL-LEVEL (Ring 0). Same restrictions as Elden Ring. Unlike base ER, EAC bypass is NOT viable since Nightreign is always-online multiplayer.",
            "EAC LIMITATION: GameShift uses direct Win32 API calls for priority and affinity. EAC blocks cross-process handle access on the game executable, so priority and affinity settings from this profile may not apply to the game process itself. Launcher demotion, ISLC, timer resolution, and all system tweaks still work normally.",
            "Priority workaround: set via registry at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\nightreign.exe\\PerfOptions with DWORD CpuPriorityClass = 3. This is applied at process creation before EAC protection activates.",
            "HARD 60 FPS CAP persists (pre-rendered cutscenes are 30 FPS). No native V-sync toggle -- causes tearing on some configs. AMD users may be locked to 30fps; fix by forcing V-sync off in Adrenalin (introduces tearing).",
            "CPU 0 removal + P-core affinity applies identically to base Elden Ring. Same 25-second post-launch delay required. GameShift's affinity system applies immediately -- for best results, manually remove CPU 0 after 25 seconds via Task Manager.",
            "0.5ms timer resolution is critical -- same rationale as Elden Ring (16.67ms frame budget makes timer granularity high-impact).",
            "Stutter is 'nowhere near as bad' as base Elden Ring but still present. Same GPU underutilization pattern.",
            "Always-online 3-player co-op roguelike -- network quality directly impacts core gameplay. P2P networking via Steam ports (TCP 27015, 27036; UDP 27015, 27031-27036). Open NAT critical.",
            "No native DLSS, FSR, XeSS, or NVIDIA Reflex. Third-party NRSS mod adds these but requires EAC disabled (not viable for online play).",
            "Memory leaks reported during second-game sessions ('works fine on Nightlord fights, then framedrop on second game'). Restart between extended sessions.",
            "Adds GuardIT DRM on top of Steam DRM (base Elden Ring uses Steam DRM alone). May affect startup time.",
            "Config: C:\\Users\\<User>\\AppData\\Roaming\\NightReign\\GraphicsConfig.xml. Same XML format as base Elden Ring.",
            "Set NVIDIA Shader Cache to Unlimited for nightreign.exe. Disable Control Flow Guard (CFG) in Windows Exploit Protection.",
            "Borderless windowed recommended over fullscreen (same as Elden Ring -- fullscreen locks to 60Hz monitor refresh)."
        }
    };

    public static GameProfile CallOfDuty() => new()
    {
        Id = "call-of-duty",
        DisplayName = "Call of Duty",
        ProcessNames = new[] { "cod.exe" },
        LauncherProcessNames = new[] { "steamwebhelper.exe", "Agent.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 8192,
        AntiCheat = AntiCheatType.Ricochet,
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableMpo" },
        Notes = new[]
        {
            "IW9 engine -- loads up to 24 threads, optimally utilizes 16. Has a built-in 'Render Worker Count' setting that should match P-core count on Intel hybrid CPUs. Config key: RendererWorkerCount in the game's config file. Set to P-core count (e.g., 6 for i5-12600K, 8 for i9-13900K).",
            "Uses RICOCHET Anti-Cheat -- KERNEL-LEVEL (Ring 0), Activision's proprietary system. Driver: brynhildr.sys (~2.2MB) at C:\\ProgramData\\Battle.net_components\\brynhildr_odin2\\. Companion driver: randgrid.sys. Service name: atvi-brynhildr. Loads ON-DEMAND when CoD launches, unloads when game exits (not always-on like Vanguard).",
            "RICOCHET does NOT appear to block SetPriorityClass or SetProcessAffinityMask -- users successfully change priority via Task Manager and Process Lasso. However, ObRegisterCallbacks may cause 'Access Denied' on OpenProcess. Use IFEO registry (PerfOptions\\CpuPriorityClass) for guaranteed priority application.",
            "Priority workaround: set via registry at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\cod.exe\\PerfOptions with DWORD CpuPriorityClass = 3. This is applied at process creation before RICOCHET protection activates.",
            "ISLC confirmed safe with RICOCHET (18+ hours gameplay, no bans). Timer resolution tools, Process Lasso, and MSI Afterburner/RTSS are also safe. WARNING: NVIDIA Profile Inspector DLSS overrides WILL cause shadowbans/permanent bans.",
            "WARNING: Running CoD on Windows 11 with a ReFS-formatted partition triggers an IRREVERSIBLE PERMANENT BAN with no right to appeal. RICOCHET interprets ReFS filesystem behavior as suspicious. Use NTFS only.",
            "WARNING: FACEIT Anti-Cheat conflicts with RICOCHET. Uninstall FACEIT AC before playing CoD to avoid launch failures.",
            "WARNING: Windows Memory Integrity (Core Isolation / HVCI) conflicts with RICOCHET and should be disabled. BO6 Season 2 triggered widespread 'Unexpected Kernel Mode Trap' BSODs after a RICOCHET driver update.",
            "TPM 2.0 and Secure Boot required for Black Ops 7 (Nov 2025) and Warzone Season 01 (Dec 2025) via Azure remote attestation. Not required for BO6 or MWIII.",
            "Memory leaks CONFIRMED and acknowledged on Activision's Trello board. Warzone RAM usage climbs to 97-100% within 30-90 minutes on 16GB systems. 32GB strongly recommended. ISLC standby cleaning is critical.",
            "VRAM management is notoriously poor. Engine UNDERREPORTS usage by 3-6 GB. Use in-game VRAM Scale Target (config key: VideoMemoryScale, range 0.0-2.0, default 0.85). Recommended: 0.60-0.70 for 8GB cards, 0.70-0.80 for 12GB, 0.80 for 16GB+.",
            "Config tweak: GPUUploadHeaps = false in config file. Disables Resizable BAR which causes frame pacing stutters in Warzone (NVIDIA) and all modes (AMD). Widely recommended community fix.",
            "On-Demand Texture Streaming is the PRIMARY cause of packet burst errors. Downloads textures from Activision's CDN during gameplay, competing with UDP packets. Set to 'Minimal' for competitive play.",
            "Shader Installation: CPU-intensive pre-compilation at launch using RendererWorkerCount threads. Shader cache at [Install]\\retail\\shadercache\\ can exceed 10GB. Invalidated by GPU driver updates. Do not interrupt.",
            "NVIDIA Reflex supported (Off/On/On+Boost). On+Boost can trigger DirectX crashes by pushing VRAM to its limit -- use 'On' without Boost for stability.",
            "DLSS 3 (Super Resolution + Frame Generation), FSR 3, and XeSS supported. NOTE: All upscalers had a ~40% performance overhead bug at BO6 launch. FidelityFX CAS with Render Resolution slider is the safest fallback.",
            "Network: UDP port 3074 (primary gameplay). MP 6v6: ~62Hz tick rate. Warzone BR: ~20-24Hz. Ground War: ~62Hz. Nagle tweaks irrelevant (UDP). Set NetworkThrottlingIndex=0xFFFFFFFF and SystemResponsiveness=0 at HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile.",
            "Config files: %USERPROFILE%\\Documents\\Call of Duty\\players\\. BO6: s.1.0.cod24.txt (files: s.1.0.cod24.txt0 and s.1.0.cod24.txt1). MWIII/WZ: options.4.cod23.cst. Set to Read-Only after editing -- game overwrites on every launch.",
            "Crash logs: %LOCALAPPDATA%\\Activision\\Call of Duty\\Crash_reports\\. Sort by date, open with 7-Zip, check dxdiag_oncrash.txt for diagnostics.",
            "Agent.exe (Blizzard Update Agent) can consume 81-90% CPU during background updates and spawn 100+ instances on connection failure. Profile demotes it to Below Normal. Critical for Battle.net users.",
            "CoD launches itself at High priority by default. Some competitive players intentionally DEMOTE to Normal to reduce Discord audio lag and system contention. A GitHub tool (launchandfixcod) automates this."
        }
    };

    public static GameProfile Cyberpunk2077() => new()
    {
        Id = "cyberpunk-2077",
        DisplayName = "Cyberpunk 2077",
        ProcessNames = new[] { "Cyberpunk2077.exe" },
        LauncherProcessNames = Array.Empty<string>(),
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = null,
        IntelHybridPCoreOnly = false,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 4096,
        AntiCheat = AntiCheatType.None, // No anti-cheat, no DRM in retail
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation" },
        Notes = new[]
        {
            "REDengine 4 -- uses a counter-based job dispatcher (detailed in GDC talk). Nearly all engine systems parallelized. Sweet spot: 6-8 cores with SMT/HT. Post-2.0 update, CDPR confirmed ~90% CPU usage across 8 cores. P-core-only would HURT performance -- E-cores handle streaming work.",
            "NO ANTI-CHEAT. NO DRM in retail (Denuvo was only in pre-release review builds). GOG version is fully DRM-free. Full freedom for all process manipulation APIs.",
            "Patch 2.11 added 'Hybrid CPU Utilization' setting for Intel hybrid CPUs. INITIAL IMPLEMENTATION WAS BROKEN -- 1% lows dropped from 136.5 to 11.5 FPS on i9-13900K. Patch 2.12 fixed stuttering but provides no gain over Auto. GameShift should NOT force P-core-only affinity.",
            "AMD SMT BUG: Patch 1.05 only fixed 4-core and 6-core Ryzen CPUs. 8+ core Ryzen (5800X3D, 7800X3D, etc.) STILL affected in current versions. Community hex edit / CET SMT fix gives clear gains in 1% and 0.1% lows (up to 27% improvement on 7800X3D). In-game SMT toggle (Settings > Gameplay > Performance) doesn't fully resolve for 8-core. Nexus Mods fix actively maintained.",
            "HAGS (Hardware Accelerated GPU Scheduling) is REQUIRED for both DLSS Frame Generation AND FSR Frame Generation. CDPR explicitly states this. Do NOT disable for this game.",
            "NVIDIA Reflex supported (On/On+Boost). Automatically enabled and CANNOT be disabled when DLSS Frame Generation is active. Known to cause irregular frame pacing in some configs -- 17.6ms latency with Reflex vs 52.1ms without.",
            "DLSS 3.5 with Ray Reconstruction supported (49% performance gain measured by Digital Foundry -- 69 to 103 FPS on RTX 4090 at 4K). FSR 3.1 with decoupled FG added Patch 2.3 (July 2025). XeSS 2.0 with FG also added Patch 2.3.",
            "Path Tracing (RT Overdrive) is the heaviest graphics mode in any game. RTX 4090 at 4K native: ~18 FPS. With DLSS Performance + FG: ~90+ FPS. Minimum practical GPU for 60fps PT: RTX 4070 with DLSS Balanced + FG at 1440p.",
            "VRAM leak persists in current versions. Progressive FPS degradation over 1-3 hours. GPU utilization spikes to 100% when opening map/inventory after extended play. Restart fully resets performance. ISLC helps but doesn't fully solve.",
            "Config: %LOCALAPPDATA%\\CD Projekt Red\\Cyberpunk 2077\\UserSettings.json. Engine config: <game>\\engine\\config\\platform\\pc\\ -- create custom .ini files here (e.g., gameshift.ini) to override settings without modifying shipped files.",
            "Engine INI tweaks: FloodMinNonLoadingThreads (reserve gameplay threads during loading), StreamMaxLoadingThreads (streaming threads), MaxNodesPerFrame (default 300, community sets 1800 for NVMe). ~1,448 hidden INI settings available via Nexus Mods dump.",
            "The memory_pool_budgets.csv tweak is DEFINITIVELY DEBUNKED. CDPR removed the file in Hotfix 1.05 and stated it was 'a leftover file used during development with no effect on memory allocation.' Any perceived benefit was placebo from the restart.",
            "Crowd Density (in Graphics settings) is actually a CPU setting. Every NPC requires AI pathfinding and physics. In Dogtown, high density can crush even an i9-13900K. Lowering High to Medium yields ~10-15% CPU headroom.",
            "'Fullscreen' mode is actually borderless windowed internally. True exclusive fullscreen can be forced by changing WindowMode to Fullscreen in UserSettings.json.",
            "NVMe SSD highly recommended. CDPR recommends NVMe for RT Overdrive preset. MaxNodesPerFrame=1800 + NVMe dramatically reduces texture pop-in during driving.",
            "Single-player only -- no network requirements, no multiplayer, no ports to configure.",
            "0.5ms timer resolution provides marginal frame pacing improvement. Game is primarily GPU-bound so timer resolution is less impactful than CPU-bound competitive titles. Most beneficial at 1080p high-FPS without RT where CPU bottlenecks emerge."
        }
    };

    public static GameProfile MinecraftJava() => new()
    {
        Id = "minecraft-java",
        DisplayName = "Minecraft: Java Edition",
        ProcessNames = new[] { "javaw.exe" },
        LauncherProcessNames = new[] { "MinecraftLauncher.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 512,
        GamingFreeMemoryThresholdMB = 1024,
        AntiCheat = AntiCheatType.None, // Server-side only (NoCheatPlus, GrimAC)
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation" },
        Notes = new[]
        {
            "Java-based game — runs as javaw.exe. GameShift identifies Minecraft by checking the command line of javaw.exe processes for 'MojangTricksIntelDriversForPerformance' or Minecraft class names (net.minecraft.client.main.Main, net.fabricmc, cpw.mods).",
            "PROCESS DETECTION LIMITATION: javaw.exe is shared by ALL Java applications. Process-name-only detection will false-positive on IntelliJ, Jenkins, etc. Correct detection requires command-line inspection. Detection strings: PRIMARY 'MojangTricksIntelDriversForPerformance' (all official launches), FALLBACK 'net.minecraft.client.main.Main' (vanilla), 'net.fabricmc' (Fabric), 'cpw.mods' (Forge/NeoForge 1.17+), 'net.minecraft.launchwrapper.Launch' (Forge legacy ≤1.12.2). Bedrock Edition (Minecraft.Windows.exe) is a completely separate game — excluded from this profile.",
            "NO ANTI-CHEAT. All anti-cheat is server-side (NoCheatPlus, GrimAC, Watchdog). Full freedom for process priority, CPU affinity, and all system optimization tools.",
            "SINGLE-THREAD-BOUND where it matters most. Despite 30-60 JVM threads, performance is dominated by the Server Thread (20 TPS tick loop) and Render Thread (OpenGL draw calls). Both are extremely IPC-sensitive.",
            "P-core affinity on Intel hybrid CPUs is HIGH IMPACT — eliminates Thread Director scheduling critical game threads onto E-cores, which causes frametime spikes.",
            "JVM arguments are the PRIMARY optimization lever — more impactful than any OS-level tweak. Recommended client-optimized flags (BruceTheMoose G1GC): -Xmx4G -Xms4G -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:G1HeapRegionSize=16M -XX:G1NewSizePercent=23 -XX:G1ReservePercent=20 -XX:SurvivorRatio=32 -XX:MaxTenuringThreshold=1 -XX:+AlwaysPreTouch -XX:+DisableExplicitGC -XX:+PerfDisableSharedMem -XX:+UseCriticalJavaThreadPriority -XX:ThreadPriorityPolicy=1",
            "HEAP ALLOCATION: 2-4 GB (vanilla), 4-6 GB (light mods), 6-8 GB (heavy modpacks). NEVER exceed 12 GB — larger heaps increase GC pause times. Always set -Xms = -Xmx for pre-committed memory.",
            "GraalVM EE provides 20%+ improvement in chunk generation and modest client FPS gains with better 1% lows. Only supports G1GC.",
            "Optimization mod stack (Fabric): Sodium (2-5x FPS rendering rewrite), Lithium (50%+ tick improvement), FerriteCore (30-50% RAM reduction), Iris (shader support), Entity Culling, ImmediatelyFast, Krypton (networking). Sodium supersedes OptiFine with far better performance and mod compatibility.",
            "ISLC's primary value for Minecraft is the 0.5ms timer resolution feature, NOT standby list cleaning. The JVM manages its own heap memory independently of the Windows standby list.",
            "0.5ms timer resolution improves frame pacing by 20-30% in 1%/0.1% lows. Java calls timeBeginPeriod(1) for Thread.sleep() — ISLC's 0.5ms override provides additional precision.",
            "Game tick rate: fixed 20 TPS (50ms budget). Client render loop is decoupled and interpolates between ticks. Chunk loading, entity rendering, and redstone all compete for the Server Thread's 50ms budget.",
            "Network: TCP only (unusual for games), port 25565. TCP_NODELAY=true since version 1.8.1. Nagle's algorithm is not a concern on modern versions.",
            "Vanilla uses OpenGL 3.3 — no DLSS, FSR, NVIDIA Reflex, or any modern GPU features. Sodium requires OpenGL 4.5. VulkanMod replaces renderer with Vulkan 1.2+ but has limited mod compatibility.",
            "Fabric mod loader is preferred over Forge for performance — 30-45 second startup vs Forge's 2-3 minutes, and Fabric mods use 15-25% less RAM.",
            "Common mistake: allocating too much RAM. With proper GC flags (-XX:G1HeapRegionSize=16M prevents chunk data from becoming 'humongous' allocations), 4-6 GB is sufficient for most modded play. Excess heap means longer GC pauses.",
            "Third-party launchers (Prism Launcher, MultiMC, CurseForge) all produce the same javaw.exe game process with Minecraft classes on the classpath. The launcher can be safely demoted regardless of which launcher is used."
        }
    };

    public static GameProfile FinalFantasyXiv() => new()
    {
        Id = "final-fantasy-xiv",
        DisplayName = "Final Fantasy XIV",
        ProcessNames = new[] { "ffxiv_dx11.exe" },
        LauncherProcessNames = new[] { "ffxivlauncher64.exe", "ffxivboot64.exe" },
        GamePriority = ProcessPriorityClass.High,
        LauncherPriority = ProcessPriorityClass.BelowNormal,
        IntelHybridPCoreOnly = true,
        AffinityMask = null,
        GamingStandbyThresholdMB = 1024,
        GamingFreeMemoryThresholdMB = 8192,
        AntiCheat = AntiCheatType.None, // No client-side anti-cheat
        RecommendedTweaks = new[] { "DisableGameDvr", "DisablePowerThrottling", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisableHags", "DisableMpo" },
        Notes = new[]
        {
            "Custom Square Enix engine — main-thread-dominant. Meaningful scaling to 4 cores, diminishing returns to 6. Beyond 6 cores, negligible benefit. CPU bottlenecks hard in crowded areas (Limsa Lominsa, S-Rank hunts, large FATEs).",
            "NO CLIENT-SIDE ANTI-CHEAT. SE explicitly stated they cannot check what programs are installed. SE support has recommended setting priority to High. ACT and Dalamud are far more invasive than GameShift — all system-level tools are 100% safe.",
            "P-core affinity CONFIRMED BENEFICIAL by community. Multiple i9-12900K owners reported Windows intermittently scheduling FFXIV main thread to E-cores, causing sudden frame drops. P-core-only affinity eliminates this.",
            "DX11 only (DX9 removed Patch 6.58). Dawntrail (7.0) added DLSS 2.0 and FSR 1.0 (spatial only). DLSS implementation is buggy — forces dynamic resolution scaling with no manual quality preset without mods (OptiScaler). No XeSS, no NVIDIA Reflex.",
            "HAGS causes severe stuttering in FFXIV — MUST be disabled. Multiple confirmed reports on official Square Enix forums, especially in borderless windowed mode.",
            "Borderless windowed has a BitBlt (legacy) presentation mode bug — breaks G-Sync/FreeSync and causes frame pacing issues. Fixes: Windows 11 'Optimizations for windowed games' toggle, or use Fullscreen Exclusive (recommended for best performance and VRR).",
            "Frame rate cap bug: DX11 client does NOT properly limit FPS when minimized. Game runs at uncapped FPS while minimized, wasting resources and generating heat.",
            "Extended session memory growth: starts at 2.5-3 GB, grows over time. Dawntrail (7.0) worsened this. ACT+Cactbot adds ~6.7 GB over 3 hours. Dalamud plugins have documented memory leaks. ISLC standby cleaning is specifically beneficial for FFXIV's gradual memory accumulation.",
            "0.5ms timer resolution provides marginal but real improvement to input polling and frame timing, particularly for double-weaving oGCDs in combat (2.5s GCD with ~0.6-0.7s animation locks).",
            "Network: TCP for all gameplay. Ports: TCP 55296-55551 (primary gameplay), 80, 443, 8080. Server tick rate ~3 seconds for DoT/HoT/AoE snapshots. Latency directly affects ability queuing — higher RTT extends effective animation lock. Gaming VPNs (Mudfish, ExitLag) help by optimizing routing to data centers.",
            "In-game 'Character and Object Quantity' (System Config → Other → Display Limits) is the single most effective CPU optimization. Reducing from Maximum to Minimum dramatically improves FPS in crowded areas.",
            "Config: %USERPROFILE%\\Documents\\My Games\\FINAL FANTASY XIV - A Realm Reborn\\FFXIV.cfg. Key tunables: ScreenMode (0=Windowed, 1=Fullscreen, 2=Borderless), Fps, FPSInActive, DisplayObjectLimitType, OcclusionCulling.",
            "For extended sessions (4-12+ hours): monitor ffxiv_dx11.exe memory usage. Recommend restart if process exceeds 6-8 GB. Dalamud plugin users should restart every 6-8 hours.",
            "Third-party tools: XIVLauncher (Dalamud plugin framework) is widely used. GShade/ReShade for visual enhancement. ACT for DPS parsing. TexTools for texture modding. None affect GameShift's optimization — all inject into the same ffxiv_dx11.exe process.",
            "XIVLauncher uses XIVLauncher.exe but still launches the same ffxiv_dx11.exe game process. Both official launcher processes (ffxivboot64.exe, ffxivlauncher64.exe) can be safely demoted during gameplay."
        }
    };
}
