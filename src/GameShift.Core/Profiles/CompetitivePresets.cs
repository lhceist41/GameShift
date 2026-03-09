using System.Diagnostics;
using GameShift.Core.Detection;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles.GameActions;

namespace GameShift.Core.Profiles;

/// <summary>
/// Static factory for game-specific preset profiles and actions.
/// Provides pre-configured GameProfile settings and GameAction lists for
/// known competitive games: Overwatch 2, Valorant, League of Legends, Deadlock, and osu!.
/// Provides pre-configured settings and actions for known competitive games.
/// </summary>
public static class CompetitivePresets
{
    // ── Known executable names ────────────────────────────────────────

    /// <summary>Overwatch 2 main game executable name (inside _retail_ subdirectory).</summary>
    public const string Overwatch2Exe = "Overwatch.exe";

    /// <summary>Valorant main game executable name.</summary>
    public const string ValorantExe = "VALORANT-Win64-Shipping.exe";

    /// <summary>League of Legends main game executable name.</summary>
    public const string LeagueExe = "League of Legends.exe";

    /// <summary>Deadlock main game executable name (Valve internal project name: project8).</summary>
    public const string DeadlockExe = "project8.exe";

    /// <summary>osu! main game executable name.</summary>
    public const string OsuExe = "osu!.exe";

    // ── Metadata ────────────────────────────────────────────────────

    private static readonly Dictionary<string, PresetGameMetadata> _metadata =
        new(StringComparer.OrdinalIgnoreCase)
    {
        [Overwatch2Exe] = new()
        {
            DisplayName = "Overwatch 2",
            AntiCheatName = "", // Server-side ML detection only — no kernel anti-cheat
            AntiCheat = AntiCheatType.None,
            VbsSafeToDisable = true
        },
        [ValorantExe] = new()
        {
            DisplayName = "Valorant",
            AntiCheatName = "Riot Vanguard",
            AntiCheat = AntiCheatType.RiotVanguard,
            VbsSafeToDisable = false,
            VbsSafetyReason = "Riot Vanguard enforces VBS/HVCI (Memory Integrity) as a hard requirement. Disabling it causes VAN:RESTRICTION errors and prevents Valorant from launching."
        },
        [LeagueExe] = new()
        {
            DisplayName = "League of Legends",
            AntiCheatName = "Riot Vanguard",
            AntiCheat = AntiCheatType.RiotVanguard,
            VbsSafeToDisable = false,
            VbsSafetyReason = "Riot Vanguard enforces VBS/HVCI (Memory Integrity) as a hard requirement. Disabling it causes VAN:RESTRICTION errors and prevents League of Legends from launching."
        },
        [DeadlockExe] = new()
        {
            DisplayName = "Deadlock",
            AntiCheatName = "VAC",
            AntiCheat = AntiCheatType.ValveAntiCheat,
            VbsSafeToDisable = true
        },
        [OsuExe] = new()
        {
            DisplayName = "osu!",
            AntiCheatName = "",
            AntiCheat = AntiCheatType.None,
            VbsSafeToDisable = true
        }
    };

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Returns metadata about a preset game for UI display, or null if not a preset game.
    /// </summary>
    public static PresetGameMetadata? GetMetadata(string executableName) =>
        _metadata.TryGetValue(executableName, out var m) ? m : null;

    /// <summary>
    /// Returns true if the given executable name matches one of the known preset games.
    /// Comparison is case-insensitive.
    /// </summary>
    /// <param name="executableName">Game executable file name (e.g. "VALORANT-Win64-Shipping.exe").</param>
    public static bool IsPresetGame(string executableName)
    {
        return _metadata.ContainsKey(executableName);
    }

    /// <summary>
    /// Returns a pre-configured GameProfile for the given executable, or null if not a preset game.
    /// The returned profile does NOT have Id, ExecutablePath, or LauncherSource set —
    /// the caller (DetectionOrchestrator) must fill those in from the detected game info.
    /// </summary>
    /// <param name="executableName">Game executable file name.</param>
    /// <returns>Pre-configured GameProfile, or null if not a preset game.</returns>
    public static GameProfile? GetPresetProfile(string executableName)
    {
        if (string.Equals(executableName, Overwatch2Exe, StringComparison.OrdinalIgnoreCase))
            return BuildOverwatch2Profile();

        if (string.Equals(executableName, ValorantExe, StringComparison.OrdinalIgnoreCase))
            return BuildValorantProfile();

        if (string.Equals(executableName, LeagueExe, StringComparison.OrdinalIgnoreCase))
            return BuildLeagueProfile();

        if (string.Equals(executableName, DeadlockExe, StringComparison.OrdinalIgnoreCase))
            return BuildDeadlockProfile();

        if (string.Equals(executableName, OsuExe, StringComparison.OrdinalIgnoreCase))
            return BuildOsuProfile();

        return null;
    }

    /// <summary>
    /// Returns the list of game-specific actions for the given executable.
    /// Returns an empty list if not a preset game.
    /// </summary>
    /// <param name="executableName">Game executable file name.</param>
    /// <returns>List of GameAction instances to apply when this game is active.</returns>
    public static List<GameAction> GetGameActions(string executableName)
    {
        if (string.Equals(executableName, Overwatch2Exe, StringComparison.OrdinalIgnoreCase))
            return BuildOverwatch2Actions();

        if (string.Equals(executableName, ValorantExe, StringComparison.OrdinalIgnoreCase))
            return BuildValorantActions();

        if (string.Equals(executableName, LeagueExe, StringComparison.OrdinalIgnoreCase))
            return BuildLeagueActions();

        if (string.Equals(executableName, DeadlockExe, StringComparison.OrdinalIgnoreCase))
            return BuildDeadlockActions();

        if (string.Equals(executableName, OsuExe, StringComparison.OrdinalIgnoreCase))
            return BuildOsuActions();

        return new List<GameAction>();
    }

    // ── Overwatch 2 ─────────────────────────────────────────────────

    private static GameProfile BuildOverwatch2Profile() => new()
    {
        GameName = "Overwatch 2",
        ExecutableName = Overwatch2Exe,
        AntiCheat = AntiCheatType.None,
        // v1 optimizations — all on
        SuppressServices = true,
        SwitchPowerPlan = true,
        SetTimerResolution = true,
        BoostProcessPriority = true,
        OptimizeMemory = true,
        ReduceVisualEffects = true,
        OptimizeNetwork = true,
        // CPU-bound at high framerates — E-cores hurt performance on Intel hybrid CPUs
        UsePerformanceCoresOnly = true,
        // v2 toggles
        EnableCompetitiveMode = true,  // Overlays can cause alt-tab freezes
        EnableGpuOptimization = true,
        DisableMpo = true,             // MPO documented to cause frame pacing issues
        // v3 toggles
        SuppressScheduledTasks = true,
        UnparkCpuCores = true,
        // v4 toggles
        ManageIoPriority = true,
        EnableEfficiencyMode = true,
        FlushModifiedPages = true,
        ManageMemoryPriority = true,
        // v5 toggles
        PinToVCacheCcd = true,
        SuppressTier2Services = true
    };

    private static List<GameAction> BuildOverwatch2Actions() => new()
    {
        new DefenderExclusionAction(
            "OW2 Defender Exclusions",
            new[]
            {
                @"C:\Program Files (x86)\Overwatch\"
            }),
        new FullscreenOptimizationAction(
            "OW2 Fullscreen + DPI Override",
            @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe",
            includeDpiOverride: true),
        new ProcessPrioritySetAction(
            "OW2 Battle.net Priority Reduction",
            "Battle.net",
            ProcessPriorityClass.BelowNormal),
        new FirewallRuleAction(
            "OW2 Firewall Allow",
            "GameShift_OW2_In",
            @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe",
            "Inbound"),
        new OneTimeTipAction(
            "ow2_reflex",
            "OW2 TIP: Enable NVIDIA Reflex in Video settings. Set to 'On + Boost' for up to 50% lower system latency.",
            hw => hw.GpuVendor == GpuVendor.Nvidia,
            "NVIDIA GPU only"),
        new OneTimeTipAction(
            "ow2_antilag",
            "OW2 WARNING: AMD Anti-Lag+ causes instability and crashes in Overwatch 2 (especially in Stadium mode). Use the in-game 'Reduce Buffering' setting instead for latency reduction.",
            hw => hw.GpuVendor == GpuVendor.Amd,
            "AMD GPU only"),
        new OneTimeTipAction(
            "ow2_fullscreen",
            "OW2 TIP: Use Fullscreen (not Borderless). Borderless adds 20+ ms input lag through Windows DWM forced triple buffering. Enable 'Reduce Buffering' in Video settings."),
        new OneTimeTipAction(
            "ow2_render_scale",
            "OW2 TIP: Set Render Scale to 100% (not Automatic). The 'Automatic' setting can supersample above 100%, wasting GPU headroom that could be used for higher FPS."),
        new OneTimeTipAction(
            "ow2_memory_leak",
            "OW2 TIP: Overwatch 2 has a known memory leak — RAM usage climbs over extended sessions. Restart the game every 1-2 hours during long play sessions to avoid performance degradation."),
        new OneTimeTipAction(
            "ow2_polling_rate",
            "OW2 TIP: Mouse polling rates above 4000 Hz cause severe FPS drops in OW2 (8000 Hz can drop FPS from 600 to 60). Use 1000 Hz for maximum stability."),
        new OneTimeTipAction(
            "ow2_hags",
            "OW2 TIP: HAGS results are mixed in OW2. Some configs see slightly better FPS and input latency, but HAGS + NVIDIA Reflex together can cause frame drops. Test both on and off for your hardware.",
            hw => hw.IsHagsEnabled,
            "HAGS enabled"),
        new OneTimeTipAction(
            "ow2_hybrid_cpu",
            "OW2 TIP: GameShift is pinning Overwatch 2 to P-cores only. Intel 13th/14th gen users: ensure you have the latest BIOS/microcode update installed — these CPUs had documented instability issues.",
            hw => hw.IsHybridCpu,
            "Intel hybrid CPU detected"),
        new OneTimeTipAction(
            "ow2_alt_tab",
            "OW2 TIP: Alt-tabbing can cause hard freezes. GameShift's Competitive Mode suspends overlays that commonly trigger this. If freezes persist, close MSI Afterburner and similar monitoring tools.")
    };

    // ── Valorant ──────────────────────────────────────────────────────

    private static GameProfile BuildValorantProfile() => new()
    {
        GameName = "Valorant",
        ExecutableName = ValorantExe,
        AntiCheat = AntiCheatType.RiotVanguard,
        // v1 optimizations — all on
        SuppressServices = true,
        SwitchPowerPlan = true,
        SetTimerResolution = true,
        BoostProcessPriority = true,
        OptimizeMemory = true,
        ReduceVisualEffects = true,
        OptimizeNetwork = true,
        // Hybrid CPU — not beneficial for Valorant (modern multi-threaded engine)
        UsePerformanceCoresOnly = false,
        // v2 toggles
        EnableCompetitiveMode = true,
        EnableGpuOptimization = true,
        DisableMpo = false, // Valorant doesn't require MPO disabled
        // v3 toggles
        SuppressScheduledTasks = true,
        UnparkCpuCores = true,
        // v4 toggles
        ManageIoPriority = true,
        EnableEfficiencyMode = true,
        FlushModifiedPages = true,
        ManageMemoryPriority = true,
        // v5 toggles
        PinToVCacheCcd = true,
        SuppressTier2Services = true
    };

    private static List<GameAction> BuildValorantActions() => new()
    {
        new DefenderExclusionAction(
            "Valorant Defender Exclusions",
            new[]
            {
                @"C:\Riot Games\VALORANT\",
                @"C:\Program Files\Riot Vanguard\"
            }),
        new FullscreenOptimizationAction(
            "Valorant Fullscreen + DPI Override",
            @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            includeDpiOverride: true),
        new OneTimeTipAction(
            "valorant_xmp",
            "VALORANT TIP: Enable XMP/EXPO in your BIOS for optimal memory performance. Check your motherboard manual for instructions."),
        new OneTimeTipAction(
            "valorant_sharpening",
            "VALORANT TIP: Try enabling Experimental Sharpening in Video settings for clearer visuals without GPU cost."),
        // New actions
        new FirewallRuleAction(
            "Valorant Firewall Allow",
            "GameShift_Valorant_In",
            @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            "Inbound"),
        new OneTimeTipAction(
            "valorant_reflex",
            "VALORANT TIP: Enable NVIDIA Reflex in settings for lower input latency. Set to 'On + Boost' for best results.",
            hw => hw.GpuVendor == GpuVendor.Nvidia,
            "NVIDIA GPU only"),
        new OneTimeTipAction(
            "valorant_hags",
            "VALORANT TIP: If experiencing microstutter, consider disabling Hardware Accelerated GPU Scheduling (HAGS) in Windows Display settings.",
            hw => hw.IsHagsEnabled,
            "HAGS enabled"),
        new OneTimeTipAction(
            "valorant_multithread",
            "VALORANT TIP: Enable Multithreaded Rendering in Video settings for better CPU utilization and higher FPS."),
        new OneTimeTipAction(
            "valorant_vbs",
            "VALORANT SAFETY: Never disable VBS/HVCI, Secure Boot, or TPM. Riot Vanguard requires these security features to run.")
    };

    // ── League of Legends ─────────────────────────────────────────────

    private static GameProfile BuildLeagueProfile() => new()
    {
        GameName = "League of Legends",
        ExecutableName = LeagueExe,
        AntiCheat = AntiCheatType.RiotVanguard,
        // v1 optimizations — all on
        SuppressServices = true,
        SwitchPowerPlan = true,
        SetTimerResolution = true,
        BoostProcessPriority = true,
        OptimizeMemory = true,
        ReduceVisualEffects = true,
        OptimizeNetwork = true,
        // LoL is single-thread bound — performance cores only beneficial
        UsePerformanceCoresOnly = true,
        // v2 toggles
        EnableCompetitiveMode = true,
        EnableGpuOptimization = true,
        DisableMpo = false,
        // v3 toggles
        SuppressScheduledTasks = true,
        UnparkCpuCores = true,
        // v4 toggles
        ManageIoPriority = true,
        EnableEfficiencyMode = true,
        FlushModifiedPages = true,
        ManageMemoryPriority = true,
        // v5 toggles
        PinToVCacheCcd = true,
        SuppressTier2Services = true
    };

    private static List<GameAction> BuildLeagueActions() => new()
    {
        new DefenderExclusionAction(
            "LoL Defender Exclusions",
            new[]
            {
                @"C:\Riot Games\League of Legends\",
                @"C:\Riot Games\Riot Client\",
                @"C:\Program Files\Riot Vanguard\"
            }),
        new FullscreenOptimizationAction(
            "LoL Fullscreen + DPI Override",
            @"C:\Riot Games\League of Legends\Game\League of Legends.exe",
            includeDpiOverride: true),
        // ProcessPrioritySetAction replaces ProcessSuspendAction — less aggressive
        new ProcessPrioritySetAction(
            "LoL Client Priority Reduction",
            "LeagueClientUx",
            ProcessPriorityClass.BelowNormal),
        new FirewallRuleAction(
            "LoL Firewall Allow",
            "GameShift_LoL_In",
            @"C:\Riot Games\League of Legends\Game\League of Legends.exe",
            "Inbound"),
        new GpuRegistryOverrideAction(
            "LoL AMD Anti-Lag Disable",
            GpuVendor.Amd,
            "AntiLag_DevMode",
            "0",
            "AMD GPU only"),
        new OneTimeTipAction(
            "lol_dx11",
            "LEAGUE TIP: League defaults to DX11. If you experience issues, check Video settings for the renderer option."),
        new OneTimeTipAction(
            "lol_client_priority",
            "LEAGUE TIP: The League client (LeagueClientUx.exe) priority is set to Below Normal during gameplay to free CPU time for the game. It will be restored automatically when your game ends."),
        new OneTimeTipAction(
            "lol_vbs",
            "LEAGUE SAFETY: VBS/HVCI must stay enabled for Riot Vanguard. GameShift will not disable VBS when League is installed.")
    };

    // ── Deadlock ──────────────────────────────────────────────────────

    private static GameProfile BuildDeadlockProfile() => new()
    {
        GameName = "Deadlock",
        ExecutableName = DeadlockExe,
        AntiCheat = AntiCheatType.ValveAntiCheat,
        // v1 optimizations — all on
        SuppressServices = true,
        SwitchPowerPlan = true,
        SetTimerResolution = true,
        BoostProcessPriority = true,
        OptimizeMemory = true,
        ReduceVisualEffects = true,
        OptimizeNetwork = true,
        // Deadlock benefits from all cores (Source 2 engine, multi-threaded rendering)
        UsePerformanceCoresOnly = false,
        // v2 toggles
        EnableCompetitiveMode = true,
        EnableGpuOptimization = true,
        DisableMpo = false,
        // v3 toggles
        SuppressScheduledTasks = true,
        UnparkCpuCores = true,
        // v4 toggles
        ManageIoPriority = true,
        EnableEfficiencyMode = true,
        FlushModifiedPages = true,
        ManageMemoryPriority = true,
        // v5 toggles
        PinToVCacheCcd = true,
        SuppressTier2Services = true
    };

    private static List<GameAction> BuildDeadlockActions() => new()
    {
        new DefenderExclusionAction(
            "Deadlock Defender Exclusions",
            new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\"
            }),
        new FullscreenOptimizationAction(
            "Deadlock Fullscreen + DPI Override",
            @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64\project8.exe",
            includeDpiOverride: true),
        new OneTimeTipAction(
            "deadlock_reflex",
            "DEADLOCK TIP: Enable NVIDIA Reflex in Deadlock's video settings if you have an NVIDIA GPU for lower input latency."),
        new OneTimeTipAction(
            "deadlock_ssao",
            "DEADLOCK TIP: Disable SSAO in Deadlock's video settings for a significant FPS boost with minimal visual impact."),
        new OneTimeTipAction(
            "deadlock_launch",
            "DEADLOCK TIP: Add '-high -novid' to Steam launch options for Deadlock to skip intro video and set high process priority."),
        // New actions
        new FirewallRuleAction(
            "Deadlock Firewall Allow",
            "GameShift_Deadlock_In",
            @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64\project8.exe",
            "Inbound"),
        new OneTimeTipAction(
            "deadlock_pcores",
            "DEADLOCK TIP: Consider enabling 'Use Performance Cores Only' in your Deadlock profile for better frame consistency on Intel hybrid CPUs.",
            hw => hw.IsHybridCpu,
            "Hybrid CPU detected"),
        new OneTimeTipAction(
            "deadlock_vram",
            "DEADLOCK TIP: Your GPU has less than 6 GB VRAM. Consider lowering texture quality and disabling shadow details for stable frame rates.",
            hw => !hw.HasSufficientVram(6.0),
            "Less than 6 GB VRAM"),
        new OneTimeTipAction(
            "deadlock_vulkan",
            "DEADLOCK TIP: Try switching to the Vulkan renderer in Deadlock's video settings. AMD GPUs often perform better with Vulkan than DX11.",
            hw => hw.GpuVendor == GpuVendor.Amd,
            "AMD GPU only"),
        new OneTimeTipAction(
            "deadlock_autoexec",
            "DEADLOCK TIP: Create an autoexec.cfg in the game's cfg folder with 'cl_forcepreload 1' and 'mat_queue_mode 2' for better loading and multi-threaded rendering.")
    };

    // ── osu! ──────────────────────────────────────────────────────────

    private static GameProfile BuildOsuProfile() => new()
    {
        GameName = "osu!",
        ExecutableName = OsuExe,
        AntiCheat = AntiCheatType.None,
        SuppressServices = true,
        SwitchPowerPlan = true,
        SetTimerResolution = true,         // Critical for osu! audio/input timing
        BoostProcessPriority = false,      // osu! sets its own High priority internally
        OptimizeMemory = true,
        ReduceVisualEffects = true,
        OptimizeNetwork = true,
        UsePerformanceCoresOnly = true,    // osu! is extremely single-threaded
        EnableCompetitiveMode = false,     // No overlays to kill for rhythm games
        SuspendDiscordOverlay = true,      // But still suspend Discord overlay
        EnableGpuOptimization = true,
        DisableMpo = false,
        // v3 toggles
        SuppressScheduledTasks = true,
        UnparkCpuCores = true,
        // v4 toggles
        ManageIoPriority = true,
        EnableEfficiencyMode = true,
        FlushModifiedPages = true,
        ManageMemoryPriority = true,
        // v5 toggles
        PinToVCacheCcd = true,
        SuppressTier2Services = true
    };

    private static List<GameAction> BuildOsuActions()
    {
        // Resolve osu! install directory via %LOCALAPPDATA%
        var osuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "osu!");

        return new List<GameAction>
        {
            new DefenderExclusionAction(
                "osu! Defender Exclusions",
                new[] { osuDir + @"\" }),
            new FullscreenOptimizationAction(
                "osu! Fullscreen + DPI Override",
                Path.Combine(osuDir, "osu!.exe"),
                includeDpiOverride: true),
            new OneTimeTipAction(
                "osu_raw_input",
                "OSU! TIP: Enable Raw Input in osu! settings (Options > Input) for the most accurate and lowest-latency mouse/tablet input."),
            new OneTimeTipAction(
                "osu_tablet",
                "OSU! TIP: If using a drawing tablet, install OpenTabletDriver for lower latency than manufacturer drivers. See github.com/OpenTabletDriver."),
            new OneTimeTipAction(
                "osu_timer",
                "OSU! TIP: GameShift is setting your system timer resolution to 0.5ms, which improves osu! audio sync and input timing accuracy."),
            new OneTimeTipAction(
                "osu_amd_opengl",
                "OSU! TIP: AMD drivers 22.5.2+ may have issues with OpenGL fullscreen in osu!. If you experience problems, try Borderless or use the DirectX/Compatibility renderer.",
                hw => hw.GpuVendor == GpuVendor.Amd,
                "AMD GPU only"),
            new OneTimeTipAction(
                "osu_optimus",
                "OSU! TIP: NVIDIA Optimus laptops may run osu! on the integrated GPU by default. Open NVIDIA Control Panel and set osu!.exe to use the dedicated GPU.",
                hw => hw.IsLaptop && hw.GpuVendor == GpuVendor.Nvidia,
                "NVIDIA laptop only")
        };
    }
}
