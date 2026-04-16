using System.ServiceProcess;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using Serilog;

namespace GameShift.Core.SystemTweaks;

/// <summary>
/// Describes a single BCD (Boot Configuration Data) kernel tuning setting.
/// </summary>
public class KernelTuningSetting
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string BcdKey { get; init; }
    public required string RecommendedValue { get; init; }
    public required string ApplyArgs { get; init; }
    public required string RevertArgs { get; init; }
    public required string Risk { get; init; }
    public required string Tier { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Result of checking for Hyper-V, WSL2, and Docker dependencies before disabling the hypervisor.
/// </summary>
public class HypervisorDependencyCheck
{
    public bool HyperVServiceRunning { get; init; }
    public bool Wsl2ServiceRunning { get; init; }
    public bool DockerServiceRunning { get; init; }

    public bool HasDependencies => HyperVServiceRunning || Wsl2ServiceRunning || DockerServiceRunning;

    public string Summary
    {
        get
        {
            var deps = new List<string>();
            if (HyperVServiceRunning) deps.Add("Hyper-V");
            if (Wsl2ServiceRunning) deps.Add("WSL2");
            if (DockerServiceRunning) deps.Add("Docker");
            return deps.Count > 0
                ? $"Active dependencies: {string.Join(", ", deps)}. Disabling the hypervisor will break these."
                : "No active hypervisor dependencies detected.";
        }
    }
}

/// <summary>
/// Manages BCD kernel tuning settings for gaming performance optimization.
///
/// Six settings are supported (never <c>testsigning</c>, <c>disableintegritychecks</c>,
/// or <c>nointegritychecks</c>):
///
/// | Setting              | Tier        | Risk   |
/// |----------------------|-------------|--------|
/// | disabledynamictick   | Competitive | Low    |
/// | useplatformtick      | Competitive | Low    |
/// | tscsyncpolicy        | Competitive | Low    |
/// | x2apicpolicy         | Both        | Low    |
/// | hypervisorlaunchtype | Competitive | Medium |
/// | useplatformclock     | Both        | Low    |
///
/// All changes require a reboot. Revert deletes the BCD value to restore the Windows default
/// (except <c>hypervisorlaunchtype</c> which reverts to <c>Auto</c>).
/// </summary>
public class KernelTuningManager
{
    private readonly ILogger _logger = SettingsManager.Logger;

    /// <summary>
    /// BCD settings that must NEVER be offered — they disable Secure Boot protections
    /// and trigger anti-cheat bans.
    /// </summary>
    private static readonly HashSet<string> Blocklisted =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "testsigning", "disableintegritychecks", "nointegritychecks"
        };

    /// <summary>All six supported kernel tuning settings.</summary>
    public static readonly KernelTuningSetting[] AllSettings =
    [
        new()
        {
            Id = "disabledynamictick",
            DisplayName = "Disable Dynamic Tick",
            BcdKey = "disabledynamictick",
            RecommendedValue = "Yes",
            ApplyArgs = "/set disabledynamictick yes",
            RevertArgs = "/deletevalue disabledynamictick",
            Risk = "Low",
            Tier = "Competitive",
            Description = "Consistent timer interrupts reduce jitter. Slightly increases idle power."
        },
        new()
        {
            Id = "useplatformtick",
            DisplayName = "Use Platform Tick",
            BcdKey = "useplatformtick",
            RecommendedValue = "Yes",
            ApplyArgs = "/set useplatformtick yes",
            RevertArgs = "/deletevalue useplatformtick",
            Risk = "Low",
            Tier = "Competitive",
            Description = "Forces HPET/TSC as timer source for more accurate timing."
        },
        new()
        {
            Id = "tscsyncpolicy",
            DisplayName = "TSC Sync Policy",
            BcdKey = "tscsyncpolicy",
            RecommendedValue = "Enhanced",
            ApplyArgs = "/set tscsyncpolicy enhanced",
            RevertArgs = "/deletevalue tscsyncpolicy",
            Risk = "Low",
            Tier = "Competitive",
            Description = "Improved multi-core TSC synchronization reduces timer drift."
        },
        new()
        {
            Id = "x2apicpolicy",
            DisplayName = "Enable x2APIC",
            BcdKey = "x2apicpolicy",
            RecommendedValue = "Enable",
            ApplyArgs = "/set x2apicpolicy enable",
            RevertArgs = "/deletevalue x2apicpolicy",
            Risk = "Low",
            Tier = "Both",
            Description = "More efficient interrupt routing via extended APIC mode."
        },
        new()
        {
            Id = "hypervisorlaunchtype",
            DisplayName = "Disable Hypervisor",
            BcdKey = "hypervisorlaunchtype",
            RecommendedValue = "Off",
            ApplyArgs = "/set hypervisorlaunchtype off",
            RevertArgs = "/set hypervisorlaunchtype auto",
            Risk = "Medium",
            Tier = "Competitive",
            Description = "Frees hypervisor resources. Breaks Hyper-V, WSL2, and Docker."
        },
        new()
        {
            Id = "useplatformclock",
            DisplayName = "Remove Platform Clock Override",
            BcdKey = "useplatformclock",
            RecommendedValue = "<not set>",
            ApplyArgs = "/deletevalue useplatformclock",
            RevertArgs = "/set useplatformclock true",
            Risk = "Low",
            Tier = "Both",
            Description = "Removes forced HPET clock that can cause stuttering on some systems."
        },
    ];

    // ── Read current BCD state ────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>bcdedit /enum {current}</c> output and returns a dictionary of
    /// BCD key → current value. Missing keys have null values.
    /// </summary>
    public Dictionary<string, string?> ReadCurrentValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Initialize all known keys to null (not set)
        foreach (var s in AllSettings)
            values[s.BcdKey] = null;

        try
        {
            var (success, output) = RunBcdedit("/enum {current}");
            if (!success || string.IsNullOrEmpty(output))
                return values;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // BCD output format: "key                     value"
                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var val = parts[1].Trim();

                if (values.ContainsKey(key))
                    values[key] = val;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[KernelTuning] Failed to read BCD values");
        }

        return values;
    }

    // ── Apply / Revert ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a single BCD setting. Returns success + message.
    /// Records the pending reboot fix in the state journal.
    /// </summary>
    public (bool Success, string Message) Apply(KernelTuningSetting setting, string? currentValue)
    {
        if (Blocklisted.Contains(setting.BcdKey))
            return (false, $"Setting '{setting.BcdKey}' is blocklisted for safety.");

        _logger.Information("[KernelTuning] Applying: bcdedit {Args}", setting.ApplyArgs);
        var (success, output) = RunBcdedit(setting.ApplyArgs);

        if (!success)
        {
            _logger.Warning("[KernelTuning] bcdedit failed: {Output}", output);
            return (false, $"bcdedit failed: {output}");
        }

        // Record in journal
        try
        {
            var journal = new JournalManager();
            journal.LoadJournal();
            journal.RecordPendingRebootFix($"BCD: {setting.DisplayName} = {setting.RecommendedValue}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[KernelTuning] Failed to record pending reboot fix in journal");
        }

        _logger.Information("[KernelTuning] Applied: {Name}. Reboot required.", setting.DisplayName);
        return (true, $"{setting.DisplayName} applied. Reboot required.");
    }

    /// <summary>
    /// Reverts a BCD setting to its default value.
    /// </summary>
    public (bool Success, string Message) Revert(KernelTuningSetting setting)
    {
        _logger.Information("[KernelTuning] Reverting: bcdedit {Args}", setting.RevertArgs);
        var (success, output) = RunBcdedit(setting.RevertArgs);

        if (!success)
        {
            // /deletevalue on a value that doesn't exist returns error — that's OK
            if (output.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not valid", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("[KernelTuning] Revert returned expected error (value not set): {Output}", output);
                return (true, $"{setting.DisplayName} already at default.");
            }

            _logger.Warning("[KernelTuning] Revert failed: {Output}", output);
            return (false, $"Revert failed: {output}");
        }

        _logger.Information("[KernelTuning] Reverted: {Name}. Reboot required.", setting.DisplayName);
        return (true, $"{setting.DisplayName} reverted to default. Reboot required.");
    }

    // ── Hypervisor dependency check ───────────────────────────────────────────

    /// <summary>
    /// Detects whether Hyper-V, WSL2, or Docker services are active.
    /// Call before offering <c>hypervisorlaunchtype off</c>.
    /// </summary>
    public HypervisorDependencyCheck CheckHypervisorDependencies()
    {
        return new HypervisorDependencyCheck
        {
            HyperVServiceRunning = IsServiceRunning("vmms") || IsServiceRunning("HvHost"),
            Wsl2ServiceRunning   = IsServiceRunning("WslService") || IsServiceRunning("LxssManager"),
            DockerServiceRunning = IsServiceRunning("com.docker.service") || IsServiceRunning("docker"),
        };
    }

    /// <summary>
    /// Returns the settings applicable to the given tier.
    /// Competitive = all 6. Casual = x2apicpolicy + useplatformclock only.
    /// </summary>
    public static KernelTuningSetting[] GetSettingsForTier(string tier)
    {
        if (string.Equals(tier, "Competitive", StringComparison.OrdinalIgnoreCase))
            return AllSettings;

        // Casual: only low-risk, broadly safe settings
        return AllSettings.Where(s => s.Tier == "Both").ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (bool Success, string Output) RunBcdedit(string arguments)
    {
        try
        {
            using var process = global::System.Diagnostics.Process.Start(
                new global::System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

            if (process == null)
                return (false, "Failed to start bcdedit.exe");

            var stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var stdout = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(15_000);
            process.WaitForExit(15_000);

            return process.ExitCode == 0
                ? (true, stdout)
                : (false, !string.IsNullOrEmpty(stderr) ? stderr : stdout);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool IsServiceRunning(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }
}
