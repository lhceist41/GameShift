using System.Text.Json;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Journal;

internal enum RecoveryHive
{
    LocalMachine,
    CurrentUser
}

/// <summary>
/// Registry access for the journal revert paths. It only opens keys that already exist: a revert
/// replays values into keys an earlier session wrote, so a missing key is never recreated.
/// </summary>
internal sealed class RecoveryRegistry
{
    public static readonly RecoveryRegistry Windows = new(testRoot: null);

    private readonly string? _testRoot;

    private RecoveryRegistry(string? testRoot) => _testRoot = testRoot;

    /// <summary>
    /// Maps both hives to subkeys of an existing HKCU key, so tests replay records with their real
    /// paths into throwaway keys.
    /// </summary>
    internal static RecoveryRegistry UnderCurrentUser(string testRoot) => new(testRoot);

    public RegistryKey? OpenWritable(RecoveryHive hive, string subKeyPath)
    {
        if (_testRoot != null)
            return Registry.CurrentUser.OpenSubKey($@"{_testRoot}\{hive}\{subKeyPath}", writable: true);

        var root = hive == RecoveryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
        return root.OpenSubKey(subKeyPath, writable: true);
    }

    /// <summary>Deletes a value. A missing key or value means nothing is left to undo.</summary>
    public void DeleteValue(RecoveryHive hive, string subKeyPath, string name)
    {
        using var key = OpenWritable(hive, subKeyPath);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>Writes a value into an existing key; false when the key is missing.</summary>
    public bool SetValue(RecoveryHive hive, string subKeyPath, string name, object value, RegistryValueKind kind)
    {
        using var key = OpenWritable(hive, subKeyPath);
        if (key == null)
            return false;

        key.SetValue(name, value, kind);
        return true;
    }

    /// <summary>
    /// Replays a recorded DWORD: JSON null deletes the value (it was absent before), an int restores
    /// it. Anything else is not a shape GameShift wrote and restores nothing.
    /// </summary>
    public bool RestoreDword(RecoveryHive hive, string subKeyPath, string name, JsonElement original)
    {
        if (original.ValueKind == JsonValueKind.Null)
        {
            DeleteValue(hive, subKeyPath, name);
            return true;
        }

        return original.ValueKind == JsonValueKind.Number &&
               original.TryGetInt32(out int value) &&
               SetValue(hive, subKeyPath, name, value, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Replays the <c>ifeoKeys</c> entries of a ProcessPriorityBooster or HybridCpuDetector record,
    /// each of which names the PerfOptions key of one image and the module's original value there.
    /// </summary>
    /// <returns>The entries that were refused or not restored; empty when everything was undone.</returns>
    public List<string> RevertIfeoRecords(JsonElement ifeoKeys, string valueName, string module, ILogger logger)
    {
        var problems = new List<string>();
        foreach (var entry in ifeoKeys.EnumerateArray())
        {
            var keyPath = entry.ValueKind == JsonValueKind.Object &&
                          entry.TryGetProperty("keyPath", out var keyPathElement) &&
                          keyPathElement.ValueKind == JsonValueKind.String
                ? keyPathElement.GetString()
                : null;

            // A null original means GameShift created the value; otherwise it is the JSON of the
            // values it replaced.
            Dictionary<string, int>? originals = null;
            bool valid = RecoveryInputRules.IsIfeoPerfOptionsPath(keyPath);
            if (valid && entry.TryGetProperty("originalValuesJson", out var originalsElement) &&
                originalsElement.ValueKind != JsonValueKind.Null)
            {
                originals = originalsElement.ValueKind == JsonValueKind.String
                    ? TryParseIfeoOriginals(originalsElement.GetString()!)
                    : null;
                valid = originals != null && RecoveryInputRules.AreIfeoOriginals(originals, valueName);
            }

            if (!valid)
            {
                logger.Warning("[{Module}] Refused an IFEO entry GameShift did not write: {Entry}", module, entry.GetRawText());
                problems.Add("a refused IFEO entry");
                continue;
            }

            try
            {
                if (RevertIfeoValue(keyPath!, valueName, originals))
                {
                    logger.Information("[{Module}] Reverted {Value} at HKLM\\{Path}", module, valueName, keyPath);
                    continue;
                }

                logger.Warning("[{Module}] Could not open HKLM\\{Path} to restore {Value}", module, keyPath, valueName);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[{Module}] Failed to revert {Value} at HKLM\\{Path}", module, valueName, keyPath);
            }

            problems.Add(keyPath!);
        }

        return problems;
    }

    private static Dictionary<string, int>? TryParseIfeoOriginals(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes one IFEO PerfOptions value a module wrote. With recorded originals the value is put
    /// back, or deleted when it was absent; without them GameShift created the value, so it is
    /// deleted and PerfOptions goes too once nothing else is left in it.
    /// </summary>
    /// <returns>False when a recorded original could not be written back.</returns>
    public bool RevertIfeoValue(string perfOptionsPath, string valueName, IReadOnlyDictionary<string, int>? originals)
    {
        if (originals != null && originals.TryGetValue(valueName, out int original))
            return SetValue(RecoveryHive.LocalMachine, perfOptionsPath, valueName, original, RegistryValueKind.DWord);

        bool deleteSubKey;
        using (var perfKey = OpenWritable(RecoveryHive.LocalMachine, perfOptionsPath))
        {
            if (perfKey == null)
                return true;

            perfKey.DeleteValue(valueName, throwOnMissingValue: false);
            deleteSubKey = originals == null && perfKey.ValueCount == 0 && perfKey.SubKeyCount == 0;
        } // Windows refuses to delete a key while this process still holds a handle to it.

        if (deleteSubKey)
        {
            var parentPath = perfOptionsPath[..perfOptionsPath.LastIndexOf('\\')];
            using var parentKey = OpenWritable(RecoveryHive.LocalMachine, parentPath);
            parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);
        }

        return true;
    }
}
