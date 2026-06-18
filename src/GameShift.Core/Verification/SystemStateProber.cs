using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using GameShift.Core.System;
using Microsoft.Win32;

namespace GameShift.Core.Verification;

/// <summary>
/// Captures a <see cref="StateProbe"/> of every piece of persistent system state that a GameShift
/// gaming session can touch: the watched registry surface, the active power scheme and its
/// session-relevant setting indexes, all Windows service states, and all scheduled task
/// enabled-states.
///
/// The watchlist is DELIBERATELY independent of the optimization modules' own constants. This
/// class is the auditor: if a module writes to a wrong key or GUID, an independent watchlist
/// catches the residue, while a mirrored list would inherit the bug. Volatile state that cannot
/// outlive the session by definition (process priorities, CPU sets, working sets, the live timer
/// resolution) is intentionally NOT probed; the harness verifies what could persist.
///
/// Services and scheduled tasks are captured by FULL ENUMERATION rather than by a curated list,
/// so anything a module stops or disables is caught without list maintenance. Transitional
/// service states are collapsed (StartPending -> Running, StopPending -> Stopped) and task
/// Ready/Running collapse to Enabled, so unrelated churn does not masquerade as residue.
/// </summary>
public sealed class SystemStateProber
{
    private const int ToolTimeoutMs = 20_000;

    // Power setting GUIDs probed on the ACTIVE scheme (AC and DC indexes).
    // Sources of truth audited this session: PowerPlanSwitcher.SessionOverrides,
    // CpuParkingManager parking + C-state sets, SessionSystemTweaksOptimizer ASPM/USB.
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";

    private static readonly (string SubGroup, string Setting, string Label)[] PowerSettings =
    {
        // Core parking / processor state
        (SubProcessor, "0cc5b647-c1df-4637-891a-dec35c318583", "CPMINCORES"),
        (SubProcessor, "ea062031-0e34-4ff1-9b6d-eb1059334028", "CPMAXCORES"),
        (SubProcessor, "893dee8e-2bef-41e0-89c6-b55d0929964c", "MinProcessorState"),
        (SubProcessor, "2430ab6f-a520-44a2-9601-f7f23b5134b1", "ConcurrencyThreshold"),
        // C-state limiting set
        (SubProcessor, "9943e905-9a30-4ec1-9b99-44dd3b76f7a2", "IdleStateMax"),
        (SubProcessor, "7b224883-b3cc-4d79-819f-8374152cbe7c", "IdlePromote"),
        (SubProcessor, "4b92d758-5a24-4851-a470-815d78aee119", "IdleDemote"),
        (SubProcessor, "6c2993b0-8f48-481f-bcc6-00dd2742aa06", "IdleScaling"),
        (SubProcessor, "c4581c31-89ab-4597-8e2b-9c9cab440e6b", "CsTimeCheck"),
        (SubProcessor, "619b7505-003b-4e82-b7a6-4dd29c300971", "LatencyHintPerf"),
        (SubProcessor, "619b7505-003b-4e82-b7a6-4dd29c300972", "LatencyHintPerf1"),
        (SubProcessor, "616cdaa5-695e-4545-97ad-97dc2d1bdd88", "LatencyUnparked"),
        // Session power overrides
        (SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", "EPP"),
        (SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6864", "EPP-PCores"),
        (SubProcessor, "45bcc044-d885-43e2-8605-ee0ec6e96b59", "BoostPolicy"),
        (SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", "UsbSelectiveSuspend"),
        (SubUsb, "d4e98f31-5ffe-4ce1-be31-1b38b384c009", "Usb3LinkPower"),
        (SubPciExpress, "ee12f906-d277-404b-b6da-e5fa1a576df5", "PcieAspm"),
        (SubDisk, "d639518a-e56d-4345-8af2-b9f32fb26109", "NvmeIdleTimeout"),
        (SubWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a", "WirelessPowerSave"),
    };

    /// <summary>
    /// The probed exe name used by the verification profile, so IFEO residue for it is watched.
    /// </summary>
    public const string ProbeExecutableName = "gameshift.verifyprobe.exe";

    public StateProbe Capture()
    {
        var probe = new StateProbe();

        // ── Registry: full-key captures (all values of the key) ──────────────
        CaptureKey(probe, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\Dwm", 0);
        CaptureKey(probe, Registry.LocalMachine, "HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", 0);
        CaptureKey(probe, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", 0);
        CaptureKey(probe, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", 0);
        CaptureKey(probe, Registry.LocalMachine, "HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", 0);
        CaptureKey(probe, Registry.LocalMachine, "HKLM", @"SYSTEM\CurrentControlSet\Control\PriorityControl", 0);
        CaptureKey(probe, Registry.LocalMachine, "HKLM",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + ProbeExecutableName, 2);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"SOFTWARE\Discord\Modules\discord_overlay2", 0);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", 0);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", 0);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"Control Panel\Desktop", 0);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"Control Panel\Desktop\WindowMetrics", 0);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", 0);
        CaptureKey(probe, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\DWM", 0);

        // ── Registry: per-subkey captures (dynamic scopes) ───────────────────
        CaptureSubkeys(probe, Registry.LocalMachine, "HKLM",
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", 0);          // Nagle
        CaptureSubkeys(probe, Registry.LocalMachine, "HKLM",
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}", 0); // NIC class
        CaptureSubkeys(probe, Registry.LocalMachine, "HKLM",
            @"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}", 0); // HID class
        CaptureSubkeys(probe, Registry.LocalMachine, "HKLM",
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", 1); // GPU class (incl. UMD)

        // ── Power: active scheme + AC/DC indexes of every watched setting ────
        CapturePower(probe);

        // ── Services: full enumeration, start type + collapsed status ────────
        CaptureServices(probe);

        // ── Scheduled tasks: full enumeration, enabled-state only ────────────
        CaptureTasks(probe);

        return probe;
    }

    // ── Registry capture ─────────────────────────────────────────────────────

    private static void CaptureKey(StateProbe probe, RegistryKey root, string hive, string path, int depth)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            var prefix = $"reg:{hive}\\{path}";
            if (key == null)
            {
                probe.Items[$"{prefix}::exists"] = "no";
                return;
            }

            probe.Items[$"{prefix}::exists"] = "yes";
            foreach (var name in key.GetValueNames())
            {
                probe.Items[$"{prefix}\\{(name.Length == 0 ? "(default)" : name)}"] = RenderValue(key, name);
            }

            if (depth > 0)
            {
                foreach (var sub in key.GetSubKeyNames())
                    CaptureKey(probe, root, hive, $@"{path}\{sub}", depth - 1);
            }
        }
        catch (Exception ex)
        {
            probe.Warnings.Add($"registry {hive}\\{path}: {ex.GetType().Name}");
        }
    }

    private static void CaptureSubkeys(StateProbe probe, RegistryKey root, string hive, string parentPath, int depth)
    {
        try
        {
            using var parent = root.OpenSubKey(parentPath);
            if (parent == null)
            {
                probe.Items[$"reg:{hive}\\{parentPath}::exists"] = "no";
                return;
            }

            foreach (var sub in parent.GetSubKeyNames())
            {
                if (sub.Equals("Properties", StringComparison.OrdinalIgnoreCase))
                    continue; // driver-class Properties subkeys deny access by design
                CaptureKey(probe, root, hive, $@"{parentPath}\{sub}", depth);
            }
        }
        catch (Exception ex)
        {
            probe.Warnings.Add($"registry subkeys {hive}\\{parentPath}: {ex.GetType().Name}");
        }
    }

    private static string RenderValue(RegistryKey key, string name)
    {
        try
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return kind switch
            {
                RegistryValueKind.DWord => $"dword:{value}",
                RegistryValueKind.QWord => $"qword:{value}",
                RegistryValueKind.MultiString => "multi:" + string.Join("|", (string[])(value ?? Array.Empty<string>())),
                RegistryValueKind.Binary => RenderBinary((byte[])(value ?? Array.Empty<byte>())),
                _ => RenderString(kind.ToString().ToLowerInvariant(), value?.ToString() ?? ""),
            };
        }
        catch (Exception ex)
        {
            return $"<unreadable:{ex.GetType().Name}>";
        }
    }

    private static string RenderString(string tag, string s) =>
        s.Length <= 200 ? $"{tag}:{s}" : $"{tag}:len{s.Length}:{Convert.ToHexString(SHA1.HashData(global::System.Text.Encoding.UTF8.GetBytes(s)))[..16]}";

    private static string RenderBinary(byte[] bytes) =>
        $"bin:len{bytes.Length}:{Convert.ToHexString(SHA1.HashData(bytes))[..16]}";

    // ── Power capture ────────────────────────────────────────────────────────

    private void CapturePower(StateProbe probe)
    {
        var active = RunTool("powercfg.exe", "/getactivescheme", probe);
        var schemeMatch = Regex.Match(active ?? "",
            "([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase);
        probe.Items["pcfg:active-scheme"] = schemeMatch.Success ? schemeMatch.Value.ToLowerInvariant() : "<unknown>";

        foreach (var (sub, setting, label) in PowerSettings)
        {
            var output = RunTool("powercfg.exe", $"/query SCHEME_CURRENT {sub} {setting}", probe);
            var ac = Regex.Match(output ?? "", @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            var dc = Regex.Match(output ?? "", @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            probe.Items[$"pcfg:{label}:AC"] = ac.Success ? Convert.ToInt64(ac.Groups[1].Value, 16).ToString() : "<not-available>";
            probe.Items[$"pcfg:{label}:DC"] = dc.Success ? Convert.ToInt64(dc.Groups[1].Value, 16).ToString() : "<not-available>";
        }
    }

    // ── Services capture ─────────────────────────────────────────────────────

    private static void CaptureServices(StateProbe probe)
    {
        ServiceController[] services;
        try { services = ServiceController.GetServices(); }
        catch (Exception ex) { probe.Warnings.Add($"services: {ex.GetType().Name}"); return; }

        foreach (var sc in services)
        {
            try
            {
                probe.Items[$"svc:status:{sc.ServiceName}"] = CollapseStatus(sc.Status);
                probe.Items[$"svc:start:{sc.ServiceName}"] = sc.StartType.ToString();
            }
            catch (Exception)
            {
                // Some services deny query access; identical denial on both sides diffs clean.
            }
            finally { sc.Dispose(); }
        }
    }

    private static string CollapseStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.Running => "Running",
        ServiceControllerStatus.StopPending or ServiceControllerStatus.Stopped => "Stopped",
        ServiceControllerStatus.PausePending or ServiceControllerStatus.Paused => "Paused",
        _ => status.ToString(),
    };

    // ── Scheduled tasks capture ──────────────────────────────────────────────

    private void CaptureTasks(StateProbe probe)
    {
        var output = RunTool("schtasks.exe", "/query /fo csv", probe);
        if (output == null) return;

        foreach (var line in output.Split('\n'))
        {
            // CSV: "TaskName","Next Run Time","Status"
            var m = Regex.Match(line.Trim(), "^\"(\\\\[^\"]+)\",\"[^\"]*\",\"([^\"]*)\"");
            if (!m.Success) continue;
            var path = m.Groups[1].Value;
            var status = m.Groups[2].Value;
            // Only the enabled/disabled axis matters; Ready/Running/Queued churn is noise.
            probe.Items[$"task:{path}"] = status.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "Disabled" : "Enabled";
        }
    }

    // ── Tool runner ──────────────────────────────────────────────────────────

    private static string? RunTool(string exe, string arguments, StateProbe probe)
    {
        try
        {
            var result = ProcessRunner.Run(NativeInterop.SystemExePath(exe), arguments, ToolTimeoutMs);
            if (result.TimedOut)
            {
                probe.Warnings.Add($"{exe} {arguments}: timed out");
                return null;
            }
            if (!result.Exited)
                return null; // failed to start
            return result.StdOut;
        }
        catch (Exception ex)
        {
            probe.Warnings.Add($"{exe} {arguments}: {ex.GetType().Name}");
            return null;
        }
    }
}
