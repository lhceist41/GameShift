using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Journal;

/// <summary>
/// Reverts the persistent state of per-game GameActions from journaled records, without needing the
/// original action instances. Used by the watchdog / boot recovery after a main-app crash.
///
/// <para><c>Kind</c> selects the undo path; <c>Payload</c> is the kind-specific JSON written by
/// <c>GameAction.GetCrashRevertRecord()</c>:</para>
/// <list type="bullet">
///   <item><c>registry</c> - { hive, path, name, kind, existed, value } - restore or delete a value.</item>
///   <item><c>firewall</c> - { ruleName } - remove the firewall rule GameShift created.</item>
///   <item><c>defender</c> - { paths: [...] } - remove the Defender exclusions GameShift created.</item>
///   <item><c>suspend</c> - { name, pids: [...] } - resume processes suspended during the session
///   (PID-reuse guarded) so they are not left frozen after a crash.</item>
/// </list>
/// All operations are idempotent, so a double-revert (e.g. watchdog + a partial normal revert) is safe.
/// A <c>registry</c> or <c>firewall</c> record is acted on only when it names what the removed actions
/// wrote (<see cref="RecoveryInputRules"/>); anything else is skipped and reported.
/// </summary>
public static class GameActionRecovery
{
    private const string FirewallModule = "NetSecurity";
    private const string DefenderModule = "ConfigDefender";
    private const string RemoveFirewallRuleScript =
        "Remove-NetFirewallRule -DisplayName $env:GS_ARG0 -ErrorAction SilentlyContinue";
    private const string RemoveDefenderExclusionScript =
        "Remove-MpPreference -ExclusionPath $env:GS_ARG0 -ErrorAction SilentlyContinue";

    public static GameActionRevertResult Revert(string kind, string payload, ILogger logger) =>
        Revert(kind, payload, logger, ParameterizedPowerShell.Run, RecoveryRegistry.Windows);

    /// <param name="runProcess">Process-start seam; tests pass a fake so no PowerShell runs.</param>
    internal static GameActionRevertResult Revert(
        string kind, string payload, ILogger logger, Func<ProcessStartInfo, int, ProcessRunResult> runProcess) =>
        Revert(kind, payload, logger, runProcess, RecoveryRegistry.Windows);

    /// <param name="registry">Registry seam; tests root it under a throwaway key.</param>
    internal static GameActionRevertResult Revert(
        string kind, string payload, ILogger logger,
        Func<ProcessStartInfo, int, ProcessRunResult> runProcess, RecoveryRegistry registry)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return kind switch
            {
                "registry" => RevertRegistry(root, logger, registry),
                "firewall" => RevertFirewall(root, logger, runProcess),
                "defender" => RevertDefender(root, logger, runProcess),
                "suspend" => RevertSuspend(root, logger),
                _ => UnknownKind(kind, logger),
            };
        }
        catch (JsonException)
        {
            return Refused(kind, payload, logger);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[GameActionRecovery] Failed to revert GameAction kind '{Kind}'", kind);
            return new GameActionRevertResult(RevertOutcome.Failed, $"{kind}: {ex.Message}");
        }
    }

    private static GameActionRevertResult UnknownKind(string kind, ILogger logger)
    {
        logger.Warning("[GameActionRecovery] Unknown GameAction kind '{Kind}' - skipping", kind);
        return new GameActionRevertResult(RevertOutcome.Skipped, $"Unknown game action kind {kind}");
    }

    private static GameActionRevertResult Refused(string kind, string payload, ILogger logger)
    {
        logger.Warning("[GameActionRecovery] Refused a '{Kind}' record GameShift did not write: {Payload}", kind, payload);
        return new GameActionRevertResult(RevertOutcome.Skipped, $"Refused a {kind} record GameShift did not write");
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static GameActionRevertResult RevertRegistry(JsonElement root, ILogger logger, RecoveryRegistry registry)
    {
        string? hive = StringProperty(root, "hive");
        string? path = StringProperty(root, "path");
        string? name = StringProperty(root, "name");
        string? kind = StringProperty(root, "kind");
        bool existed = root.ValueKind == JsonValueKind.Object &&
                       root.TryGetProperty("existed", out var e) && e.ValueKind == JsonValueKind.True;

        if (!RecoveryInputRules.TryLegacyRegistryAction(hive, path, name, kind, existed, out var target))
            return Refused("registry", root.GetRawText(), logger);

        if (!existed)
        {
            registry.DeleteValue(target, path!, name!);
            logger.Information("[GameActionRecovery] Deleted {Hive}\\{Path}\\{Name} (created by GameShift)", hive, path, name);
            return new GameActionRevertResult(RevertOutcome.Reverted);
        }

        var notRestored = new GameActionRevertResult(RevertOutcome.Failed, $@"Not restored: {hive}\{path}\{name}");
        if (!root.TryGetProperty("value", out var v) || v.ValueKind == JsonValueKind.Null)
        {
            logger.Warning("[GameActionRecovery] No original value recorded for {Hive}\\{Path}\\{Name}", hive, path, name);
            return notRestored;
        }

        // Restore with the original value's type - never coerce DWORD/QWORD/BINARY to REG_SZ.
        var valueKind = Enum.Parse<RegistryValueKind>(kind!);
        if (valueKind == RegistryValueKind.MultiString)
        {
            // Earlier versions recorded a multi-string as its .NET type name, not its text.
            logger.Warning("[GameActionRecovery] The original of {Hive}\\{Path}\\{Name} was not recorded; left unchanged", hive, path, name);
            return notRestored;
        }

        object value = valueKind switch
        {
            RegistryValueKind.DWord => v.GetInt32(),
            RegistryValueKind.QWord => v.GetInt64(),
            RegistryValueKind.Binary => v.GetBytesFromBase64(),
            _ => v.GetString() ?? string.Empty,
        };
        if (!registry.SetValue(target, path!, name!, value, valueKind))
        {
            logger.Warning("[GameActionRecovery] Could not open {Hive}\\{Path} to restore {Name}", hive, path, name);
            return notRestored;
        }

        logger.Information("[GameActionRecovery] Restored {Hive}\\{Path}\\{Name} ({Kind})", hive, path, name, kind);
        return new GameActionRevertResult(RevertOutcome.Reverted);
    }

    private static GameActionRevertResult RevertFirewall(
        JsonElement root, ILogger logger, Func<ProcessStartInfo, int, ProcessRunResult> runProcess)
    {
        string? ruleName = StringProperty(root, "ruleName");
        if (!RecoveryInputRules.IsLegacyFirewallRule(ruleName))
            return Refused("firewall", root.GetRawText(), logger);

        var result = runProcess(ParameterizedPowerShell.CreateStartInfo(FirewallModule, RemoveFirewallRuleScript, ruleName!), 15_000);
        if (Succeeded(result))
        {
            logger.Information("[GameActionRecovery] Removed firewall rule '{Rule}'", ruleName);
            return new GameActionRevertResult(RevertOutcome.Reverted);
        }

        logger.Warning("[GameActionRecovery] Removal of firewall rule '{Rule}' not confirmed ({Outcome})", ruleName, Describe(result));
        return new GameActionRevertResult(RevertOutcome.Failed, $"Firewall rule {ruleName} not confirmed removed");
    }

    private static GameActionRevertResult RevertDefender(
        JsonElement root, ILogger logger, Func<ProcessStartInfo, int, ProcessRunResult> runProcess)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            return Refused("defender", root.GetRawText(), logger);

        var notRemoved = new List<string>();
        foreach (var element in paths.EnumerateArray())
        {
            var path = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (string.IsNullOrEmpty(path)) continue;

            var result = runProcess(ParameterizedPowerShell.CreateStartInfo(DefenderModule, RemoveDefenderExclusionScript, path), 15_000);
            if (Succeeded(result))
            {
                logger.Information("[GameActionRecovery] Removed Defender exclusion '{Path}'", path);
            }
            else
            {
                logger.Warning("[GameActionRecovery] Removal of Defender exclusion '{Path}' not confirmed ({Outcome})", path, Describe(result));
                notRemoved.Add(path);
            }
        }

        return notRemoved.Count == 0
            ? new GameActionRevertResult(RevertOutcome.Reverted)
            : new GameActionRevertResult(RevertOutcome.Failed, $"Defender exclusions not confirmed removed: {string.Join(", ", notRemoved)}");
    }

    private static bool Succeeded(ProcessRunResult result) => result.Exited && result.ExitCode == 0;

    private static string Describe(ProcessRunResult result) =>
        result.TimedOut ? "timed out" : !result.Exited ? "PowerShell did not start" : $"exit {result.ExitCode}";

    private static GameActionRevertResult RevertSuspend(JsonElement root, ILogger logger)
    {
        string name = StringProperty(root, "name") ?? "";
        string bareName = Path.GetFileNameWithoutExtension(name);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("pids", out var pids) || pids.ValueKind != JsonValueKind.Array)
            return Refused("suspend", root.GetRawText(), logger);

        var stillSuspended = new List<int>();
        foreach (var element in pids.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int pid)) continue;

            // PID-reuse guard: only resume if the PID still belongs to the same-named process.
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!string.IsNullOrEmpty(bareName) &&
                    !string.Equals(proc.ProcessName, bareName, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Warning(
                        "[GameActionRecovery] PID {Pid} is now {Actual}, expected {Expected} - skipping resume",
                        pid, proc.ProcessName, bareName);
                    continue;
                }
            }
            catch (ArgumentException) { continue; }        // process already exited
            catch (InvalidOperationException) { continue; } // exited between lookup and access

            IntPtr handle = NativeInterop.OpenProcess(NativeInterop.PROCESS_SUSPEND_RESUME, false, pid);
            if (handle == IntPtr.Zero)
            {
                stillSuspended.Add(pid);
                continue;
            }
            try
            {
                int status = NativeInterop.NtResumeProcess(handle);
                if (status == 0)
                    logger.Information("[GameActionRecovery] Resumed {Name} (PID {Pid}) after crash", name, pid);
                else
                    stillSuspended.Add(pid);
            }
            finally
            {
                NativeInterop.CloseHandle(handle);
            }
        }

        return stillSuspended.Count == 0
            ? new GameActionRevertResult(RevertOutcome.Reverted)
            : new GameActionRevertResult(RevertOutcome.Failed, $"Not resumed: {name} (PID {string.Join(", ", stillSuspended)})");
    }
}
