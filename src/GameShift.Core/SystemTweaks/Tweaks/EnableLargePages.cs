using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Grants the SeLockMemoryPrivilege (Lock Pages in Memory) to the current user's
/// Administrators group via secedit export/modify/import. This enables games that
/// support Large Pages (2MB pages instead of 4KB) to reduce TLB misses.
///
/// Games that benefit:
///   - Unreal Engine 5 games (automatic when privilege is available)
///   - Minecraft Java Edition (with -XX:+UseLargePages JVM flag)
///   - Source 2 games (Deadlock, CS2) may benefit with -largepages launch option
///
/// NOT included in "Apply All Recommended" — opt-in only.
/// Requires logoff/reboot to take effect.
/// </summary>
public class EnableLargePages : ISystemTweak
{
    public string Name => "Enable Large Pages Privilege";
    public string Description => "Grants the Lock Pages in Memory privilege, enabling games that support large pages (2MB) to reduce TLB misses and improve memory access performance. Requires logoff or reboot.";
    public string Category => "Memory";
    public bool RequiresReboot => true; // Logoff suffices, but reboot is safer messaging

    public bool DetectIsApplied()
    {
        try
        {
            return HasLockMemoryPrivilege();
        }
        catch
        {
            return false;
        }
    }

    public string? Apply()
    {
        bool alreadyHad = HasLockMemoryPrivilege();
        if (alreadyHad)
        {
            Log.Information("[LargePages] SeLockMemoryPrivilege already granted");
            return null;
        }

        if (!GrantLockMemoryPrivilege())
        {
            return null;
        }

        Log.Information("[LargePages] SeLockMemoryPrivilege granted — logoff/reboot required");
        return JsonSerializer.Serialize(new { WasGranted = false });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            bool wasGranted = doc.RootElement.GetProperty("WasGranted").GetBoolean();

            if (wasGranted)
            {
                // Was already granted before GameShift — nothing to revert
                return true;
            }

            if (!RevokeLockMemoryPrivilege())
            {
                return false;
            }

            Log.Information("[LargePages] SeLockMemoryPrivilege revoked — logoff/reboot required");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LargePages] Failed to revert");
            return false;
        }
    }

    /// <summary>
    /// Checks if the current user (or Administrators group) has SeLockMemoryPrivilege.
    /// Uses secedit /export to read the local security policy.
    /// </summary>
    private static bool HasLockMemoryPrivilege()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_check_{Guid.NewGuid():N}.inf");
        try
        {
            var (exitCode, _) = RunProcess(NativeInterop.SystemExePath("secedit.exe"),
                $"/export /cfg \"{tempFile}\" /areas USER_RIGHTS");

            if (exitCode != 0) return false;

            string content = File.ReadAllText(tempFile);

            foreach (string line in content.Split('\n'))
            {
                if (line.TrimStart().StartsWith("SeLockMemoryPrivilege", StringComparison.OrdinalIgnoreCase))
                {
                    string currentUserSid = GetCurrentUserSid();
                    // Check for current user SID or Administrators group
                    return line.Contains(currentUserSid) || line.Contains("*S-1-5-32-544");
                }
            }

            return false; // Privilege not assigned to anyone
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LargePages] Failed to check SeLockMemoryPrivilege");
            return false;
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    /// <summary>
    /// Grants SeLockMemoryPrivilege to the current user via secedit export/modify/import.
    /// </summary>
    private static bool GrantLockMemoryPrivilege()
    {
        string exportFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_export_{Guid.NewGuid():N}.inf");
        string importFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_import_{Guid.NewGuid():N}.inf");
        string dbFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_{Guid.NewGuid():N}.sdb");

        try
        {
            // Step 1: Export current security policy
            var (exitCode1, output1) = RunProcess(NativeInterop.SystemExePath("secedit.exe"),
                $"/export /cfg \"{exportFile}\" /areas USER_RIGHTS");
            if (exitCode1 != 0)
            {
                Log.Warning("[LargePages] secedit export failed: {Output}", output1);
                return false;
            }

            // Step 2: Modify the exported policy
            string content = File.ReadAllText(exportFile);
            string currentUserSid = GetCurrentUserSid();

            if (content.Contains("SeLockMemoryPrivilege"))
            {
                // Add current user SID to existing line
                content = content.Replace(
                    "SeLockMemoryPrivilege =",
                    $"SeLockMemoryPrivilege = *{currentUserSid},");
            }
            else
            {
                // Add new line after [Privilege Rights] section header
                content = content.Replace(
                    "[Privilege Rights]",
                    $"[Privilege Rights]\r\nSeLockMemoryPrivilege = *{currentUserSid}");
            }

            File.WriteAllText(importFile, content);

            // Step 3: Import modified policy
            var (exitCode2, output2) = RunProcess(NativeInterop.SystemExePath("secedit.exe"),
                $"/configure /db \"{dbFile}\" /cfg \"{importFile}\" /areas USER_RIGHTS");

            if (exitCode2 != 0)
            {
                Log.Warning("[LargePages] secedit configure failed: {Output}", output2);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LargePages] Failed to grant SeLockMemoryPrivilege");
            return false;
        }
        finally
        {
            TryDeleteFile(exportFile);
            TryDeleteFile(importFile);
            TryDeleteFile(dbFile);
            TryDeleteFile(dbFile + ".log");
            TryDeleteFile(dbFile + ".jfm");
        }
    }

    /// <summary>
    /// Revokes SeLockMemoryPrivilege from the current user via secedit export/modify/import.
    /// </summary>
    private static bool RevokeLockMemoryPrivilege()
    {
        string exportFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_export_{Guid.NewGuid():N}.inf");
        string importFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_import_{Guid.NewGuid():N}.inf");
        string dbFile = Path.Combine(Path.GetTempPath(), $"gameshift_secpol_{Guid.NewGuid():N}.sdb");

        try
        {
            var (exitCode1, output1) = RunProcess(NativeInterop.SystemExePath("secedit.exe"),
                $"/export /cfg \"{exportFile}\" /areas USER_RIGHTS");
            if (exitCode1 != 0)
            {
                Log.Warning("[LargePages] secedit export failed: {Output}", output1);
                return false;
            }

            string content = File.ReadAllText(exportFile);
            string currentUserSid = GetCurrentUserSid();

            // Remove user SID from the privilege line (handle various formats)
            content = content.Replace($"*{currentUserSid},", "");
            content = content.Replace($",*{currentUserSid}", "");
            content = content.Replace($"*{currentUserSid}", "");

            // If the line is now empty (no other principals), remove it entirely
            content = Regex.Replace(content, @"SeLockMemoryPrivilege\s*=\s*\r?\n", "");

            File.WriteAllText(importFile, content);

            var (exitCode2, output2) = RunProcess(NativeInterop.SystemExePath("secedit.exe"),
                $"/configure /db \"{dbFile}\" /cfg \"{importFile}\" /areas USER_RIGHTS");

            if (exitCode2 != 0)
            {
                Log.Warning("[LargePages] secedit configure failed: {Output}", output2);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LargePages] Failed to revoke SeLockMemoryPrivilege");
            return false;
        }
        finally
        {
            TryDeleteFile(exportFile);
            TryDeleteFile(importFile);
            TryDeleteFile(dbFile);
            TryDeleteFile(dbFile + ".log");
            TryDeleteFile(dbFile + ".jfm");
        }
    }

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "";
    }

    private static (int exitCode, string output) RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (-1, "Failed to start process");

            var error = "";
            var stderrTask = Task.Run(() => { error = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(15000);
            process.WaitForExit(15000);

            return (process.ExitCode, string.IsNullOrEmpty(output) ? error : output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }
}
