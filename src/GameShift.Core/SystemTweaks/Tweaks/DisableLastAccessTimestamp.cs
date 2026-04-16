using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables NTFS last access timestamp updates to reduce unnecessary disk I/O during gaming.
/// Every file read normally updates the "last accessed" timestamp — this tweak eliminates that overhead.
/// Uses fsutil to query and set the behavior.
/// Values: 0 = User Managed, enabled; 1 = User Managed, disabled;
///         2 = System Managed, enabled; 3 = System Managed, disabled (Win10+ SSD default).
/// Included in "Apply All Recommended" — universally safe.
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

        // Already disabled — skip
        if (originalValue == 1 || originalValue == 3)
            return null;

        // Set to 1 (user-managed, disabled)
        RunFsutil("behavior set disablelastaccess 1");

        return JsonSerializer.Serialize(new { DisableLastAccess = originalValue });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            int originalValue = doc.RootElement.GetProperty("DisableLastAccess").GetInt32();

            RunFsutil($"behavior set disablelastaccess {originalValue}");
            return true;
        }
        catch { return false; }
    }

    private static int QueryCurrentValue()
    {
        var output = RunFsutil("behavior query disablelastaccess");
        if (output == null)
            return -1;

        // Output: "DisableLastAccess = 1" or similar
        var match = Regex.Match(output, @"DisableLastAccess\s*=\s*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static string? RunFsutil(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "fsutil",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(5000);
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}
