using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class OptimizeWin32PrioritySeparation : ISystemTweak
{
    public string Name => "Optimize Win32PrioritySeparation";
    public string Description => "Short quantum, fixed, no foreground boost — maximum input responsiveness. Applies instantly.";
    public string Category => "CPU Scheduling";
    public bool RequiresReboot => false;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string ValueName = "Win32PrioritySeparation";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            var val = key?.GetValue(ValueName);
            return val is int i && i == 0x28;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        var original = key.GetValue(ValueName);
        key.SetValue(ValueName, 0x28, RegistryValueKind.DWord);
        return JsonSerializer.Serialize(new { Win32PrioritySeparation = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var val = doc.RootElement.GetProperty("Win32PrioritySeparation");
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;
            if (val.ValueKind == JsonValueKind.Null)
                key.SetValue(ValueName, 0x26, RegistryValueKind.DWord); // Windows default
            else
                key.SetValue(ValueName, val.GetInt32(), RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }
}
