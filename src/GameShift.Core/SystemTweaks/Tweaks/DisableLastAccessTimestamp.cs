using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameShift.Core.System;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables NTFS last access timestamp updates to reduce unnecessary disk I/O during gaming.
/// Every file read normally updates the "last accessed" timestamp - this tweak eliminates that overhead.
/// Uses fsutil to query and set the behavior.
/// Values: 0 = User Managed, enabled; 1 = User Managed, disabled;
///         2 = System Managed, enabled; 3 = System Managed, disabled (Win10+ SSD default).
/// Included in "Apply All Recommended" - universally safe.
/// </summary>
public class DisableLastAccessTimestamp : ISystemTweak
{
    public string Name => "Disable NTFS Last Access Timestamps";
    public string Description => "Prevents unnecessary disk writes on every file read, reducing I/O overhead during gaming. Requires a reboot.";
    public string Category => "File System";
    public bool RequiresReboot => true;

    public bool DetectIsApplied()
    {
        try
        {
            int currentValue = QueryCurrentValue();
            // 1 or 3 = disabled (user-managed or system-managed)
            return currentValue == 1 || currentValue == 3;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        int originalValue = QueryCurrentValue();

        // Only 0/1/2/3 are valid fsutil states. A negative sentinel means the query failed -
        // abort rather than serialize a corrupt baseline we could never revert correctly.
        if (originalValue is < 0 or > 3)
            return null;

        // Already disabled (user- or system-managed) - nothing to do.
        if (originalValue == 1 || originalValue == 3)
            return null;

        // Set to 1 (user-managed, disabled). If fsutil fails, return null so the manager
        // does not record an unapplied tweak as applied.
        var (exitCode, _) = RunFsutil("behavior set disablelastaccess 1");
        if (exitCode != 0)
            return null;

        return JsonSerializer.Serialize(new { DisableLastAccess = originalValue });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            int originalValue = doc.RootElement.GetProperty("DisableLastAccess").GetInt32();

            // Reject a corrupt/sentinel baseline rather than running an invalid fsutil argument.
            if (originalValue is < 0 or > 3)
                return false;

            // 1/3 mean the original state was already disabled - nothing to undo.
            if (originalValue == 1 || originalValue == 3)
                return true;

            var (exitCode, _) = RunFsutil($"behavior set disablelastaccess {originalValue}");
            if (exitCode != 0)
                return false;

            // Confirm the value actually took before reporting success.
            return QueryCurrentValue() == originalValue;
        }
        catch { return false; }
    }

    private static int QueryCurrentValue()
    {
        var (exitCode, output) = RunFsutil("behavior query disablelastaccess");
        if (exitCode != 0 || output == null)
            return -1;

        // Output: "DisableLastAccess = 1" or similar
        var match = Regex.Match(output, @"DisableLastAccess\s*=\s*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>
    /// Runs fsutil and returns its exit code plus stdout. <c>exitCode</c> is -1 if the process
    /// could not start or timed out, so callers can distinguish real success from failure.
    /// </summary>
    private static (int exitCode, string? output) RunFsutil(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NativeInterop.SystemExePath("fsutil.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (-1, null);

            var stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(5000);
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch { /* best effort */ }
                return (-1, output);
            }
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, null);
        }
    }
}
