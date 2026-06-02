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
/// </list>
/// All operations are idempotent, so a double-revert (e.g. watchdog + a partial normal revert) is safe.
/// </summary>
public static class GameActionRecovery
{
    public static void Revert(string kind, string payload, ILogger logger)
    {
        try
        {
            switch (kind)
            {
                case "registry": RevertRegistry(payload, logger); break;
                case "firewall": RevertFirewall(payload, logger); break;
                case "defender": RevertDefender(payload, logger); break;
                default:
                    logger.Warning("[GameActionRecovery] Unknown GameAction kind '{Kind}' - skipping", kind);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[GameActionRecovery] Failed to revert GameAction kind '{Kind}'", kind);
        }
    }

    private static void RevertRegistry(string payload, ILogger logger)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        string hive = root.TryGetProperty("hive", out var h) ? h.GetString() ?? "HKLM" : "HKLM";
        string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        bool existed = root.TryGetProperty("existed", out var e) && e.ValueKind == JsonValueKind.True;

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) return;

        var baseKey = string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)
            ? Registry.CurrentUser
            : Registry.LocalMachine;

        using var key = baseKey.OpenSubKey(path, writable: true);
        if (key == null) return;

        if (!existed)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            logger.Information("[GameActionRecovery] Deleted {Hive}\\{Path}\\{Name} (created by GameShift)", hive, path, name);
        }
        else if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            key.SetValue(name, v.GetString() ?? "", RegistryValueKind.String);
            logger.Information("[GameActionRecovery] Restored {Hive}\\{Path}\\{Name}", hive, path, name);
        }
    }

    private static void RevertFirewall(string payload, ILogger logger)
    {
        using var doc = JsonDocument.Parse(payload);
        string ruleName = doc.RootElement.TryGetProperty("ruleName", out var r) ? r.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(ruleName)) return;

        RunPowerShell($"Remove-NetFirewallRule -DisplayName '{ruleName.Replace("'", "''")}' -ErrorAction SilentlyContinue");
        logger.Information("[GameActionRecovery] Removed firewall rule '{Rule}'", ruleName);
    }

    private static void RevertDefender(string payload, ILogger logger)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            return;

        foreach (var element in paths.EnumerateArray())
        {
            var path = element.GetString();
            if (string.IsNullOrEmpty(path)) continue;

            RunPowerShell($"Remove-MpPreference -ExclusionPath '{path.Replace("'", "''")}' -ErrorAction SilentlyContinue");
            logger.Information("[GameActionRecovery] Removed Defender exclusion '{Path}'", path);
        }
    }

    private static void RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo(
            NativeInterop.SystemExePath("WindowsPowerShell\\v1.0\\powershell.exe"),
            $"-Command \"{command}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process != null && !process.WaitForExit(15_000))
        {
            try { process.Kill(); } catch { /* best effort */ }
        }
    }
}
