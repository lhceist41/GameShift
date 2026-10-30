using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.System;

public class StartupAppInfo
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Source { get; set; } = ""; // "Registry HKCU", "Registry HKLM", "Startup Folder"
    public string RegistryPath { get; set; } = "";
    public string RegistryValueName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsRecommendedDisable { get; set; } = false;
}

/// <summary>A Run value renamed to "~Name". <see cref="ValueName"/> includes the "~".</summary>
public sealed record RenamedStartupEntry(bool CurrentUser, string ValueName)
{
    public string OriginalName => ValueName[1..];
}

/// <summary>
/// Reads startup applications from Registry Run/RunOnce keys (HKCU+HKLM)
/// and shell:startup folder. Provides enable/disable toggle by renaming registry values.
/// </summary>
public static class StartupAppManager
{
    private static readonly HashSet<string> _safeToDisable = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "Spotify", "Discord", "Steam", "EpicGamesLauncher",
        "iTunesHelper", "AdobeAAMUpdater", "Dropbox", "GoogleDriveSync",
        "Cortana", "MicrosoftEdgeAutoLaunch", "Skype", "Teams",
        "NahimicSvc", "RtkAudU", "WavesSvc", "NVDisplay.Container",
        "SecurityHealth" // Windows Security icon (safe to hide from startup)
    };

    public static List<StartupAppInfo> GetStartupApps()
    {
        var apps = new List<StartupAppInfo>();

        // HKCU\Software\Microsoft\Windows\CurrentVersion\Run
        ReadRegistryRun(apps, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registry HKCU");

        // HKLM\Software\Microsoft\Windows\CurrentVersion\Run
        ReadRegistryRun(apps, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registry HKLM");

        // Startup folder
        ReadStartupFolder(apps);

        return apps;
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Run values turned off by a "~" prefix (the way <see cref="ToggleStartupApp"/> disables
    /// them) whose original name is free, in the current user's and the machine's Run key.
    /// Nothing records which ones GameShift renamed, so callers must not restore them unasked.
    /// </summary>
    public static List<RenamedStartupEntry> GetRenamedEntries()
    {
        var entries = new List<RenamedStartupEntry>();
        foreach (var currentUser in new[] { true, false })
        {
            using var key = (currentUser ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(RunKeyPath);
            if (key == null) continue;

            foreach (var valueName in SelectRestorableNames(key.GetValueNames()))
            {
                if (key.GetValue(valueName) is string command && !string.IsNullOrWhiteSpace(command))
                    entries.Add(new RenamedStartupEntry(currentUser, valueName));
            }
        }
        return entries;
    }

    /// <summary>
    /// The "~Name" values that can be renamed back: "Name" must not exist, or the rename would
    /// overwrite it. Registry value names compare case-insensitively.
    /// </summary>
    internal static IEnumerable<string> SelectRestorableNames(IReadOnlyCollection<string> valueNames)
    {
        var existing = new HashSet<string>(valueNames, StringComparer.OrdinalIgnoreCase);
        return valueNames.Where(n => n.Length > 1 && n[0] == '~' && !existing.Contains(n[1..]));
    }

    /// <summary>
    /// Renames "~Name" back to "Name", keeping the value's data and kind. Returns null on success,
    /// otherwise why the entry was left as it is.
    /// </summary>
    public static string? RestoreRenamedEntry(RenamedStartupEntry entry)
    {
        try
        {
            using var key = (entry.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine)
                .OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return "The startup key could not be opened.";

            if (key.GetValue(entry.OriginalName) != null)
                return "An entry with the original name already exists, so it was left as it is.";

            var data = key.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data == null) return "The entry no longer exists.";

            key.SetValue(entry.OriginalName, data, key.GetValueKind(entry.ValueName));
            key.DeleteValue(entry.ValueName);
            Log.Information("Restored startup entry {Name}", entry.OriginalName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore startup entry {Name}", entry.ValueName);
            return ex.Message;
        }
    }

    private static void ReadRegistryRun(List<StartupAppInfo> apps, RegistryKey hive, string path, string source)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;

            foreach (var valueName in key.GetValueNames())
            {
                var command = key.GetValue(valueName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(command)) continue;

                bool isDisabled = valueName.StartsWith("~");
                var cleanName = isDisabled ? valueName[1..] : valueName;

                apps.Add(new StartupAppInfo
                {
                    Name = cleanName,
                    Command = command,
                    Source = source,
                    RegistryPath = path,
                    RegistryValueName = valueName,
                    IsEnabled = !isDisabled,
                    IsRecommendedDisable = _safeToDisable.Any(s =>
                        cleanName.Contains(s, StringComparison.OrdinalIgnoreCase))
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read startup entries from {Source} {Path}", source, path);
        }
    }

    private static void ReadStartupFolder(List<StartupAppInfo> apps)
    {
        try
        {
            var startupPath = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Startup);
            if (!Directory.Exists(startupPath)) return;

            foreach (var file in Directory.GetFiles(startupPath))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                apps.Add(new StartupAppInfo
                {
                    Name = name,
                    Command = file,
                    Source = "Startup Folder",
                    IsEnabled = true,
                    IsRecommendedDisable = _safeToDisable.Any(s =>
                        name.Contains(s, StringComparison.OrdinalIgnoreCase))
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read startup folder");
        }
    }

    /// <summary>
    /// Toggle a startup app by renaming the registry value (prefix with ~ to disable).
    /// </summary>
    public static bool ToggleStartupApp(StartupAppInfo app, bool enable)
    {
        if (app.Source == "Startup Folder") return false; // Can't toggle folder shortcuts via registry

        try
        {
            var hive = app.Source.Contains("HKCU") ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = hive.OpenSubKey(app.RegistryPath, writable: true);
            if (key == null) return false;

            var currentValue = key.GetValue(app.RegistryValueName)?.ToString();
            if (currentValue == null) return false;

            if (enable && app.RegistryValueName.StartsWith("~"))
            {
                // Re-enable: rename ~Name to Name
                var newName = app.RegistryValueName[1..];
                key.SetValue(newName, currentValue);
                key.DeleteValue(app.RegistryValueName);
                app.RegistryValueName = newName;
            }
            else if (!enable && !app.RegistryValueName.StartsWith("~"))
            {
                // Disable: rename Name to ~Name
                var newName = "~" + app.RegistryValueName;
                key.SetValue(newName, currentValue);
                key.DeleteValue(app.RegistryValueName);
                app.RegistryValueName = newName;
            }

            app.IsEnabled = enable;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to toggle startup app {Name}", app.Name);
            return false;
        }
    }
}
