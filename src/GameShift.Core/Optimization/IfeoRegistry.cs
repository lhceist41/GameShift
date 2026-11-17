using Microsoft.Win32;

namespace GameShift.Core.Optimization;

/// <summary>
/// Registry access for the IFEO paths of ProcessPriorityBooster and HybridCpuDetector: apply,
/// live revert and verify. Paths are relative to HKLM. Unlike <see cref="Journal.RecoveryRegistry"/>
/// it creates keys, because an apply may have to create the image's PerfOptions key.
/// </summary>
internal sealed class IfeoRegistry
{
    public static readonly IfeoRegistry Windows = new(testRoot: null, beforeWrite: null, afterWrite: null);

    private readonly string? _testRoot;
    private readonly Action<string>? _beforeWrite;
    private readonly Action<string>? _afterWrite;

    private IfeoRegistry(string? testRoot, Action<string>? beforeWrite, Action<string>? afterWrite)
    {
        _testRoot = testRoot;
        _beforeWrite = beforeWrite;
        _afterWrite = afterWrite;
    }

    /// <summary>
    /// Maps HKLM to <c>HKCU\{testRoot}\LocalMachine</c>, the layout
    /// <see cref="Journal.RecoveryRegistry.UnderCurrentUser"/> replays into, so a test applies and
    /// replays the same throwaway keys. The callbacks run around every write and name it.
    /// </summary>
    internal static IfeoRegistry UnderCurrentUser(
        string testRoot, Action<string>? beforeWrite = null, Action<string>? afterWrite = null) =>
        new(testRoot, beforeWrite, afterWrite);

    private string Map(string subKeyPath) =>
        _testRoot == null ? subKeyPath : $@"{_testRoot}\LocalMachine\{subKeyPath}";

    private RegistryKey Hive => _testRoot == null ? Registry.LocalMachine : Registry.CurrentUser;

    public RegistryKey? Open(string subKeyPath, bool writable = false) =>
        Hive.OpenSubKey(Map(subKeyPath), writable);

    public RegistryKey? CreateSubKey(string subKeyPath) =>
        Write($"create key {subKeyPath}", () => Hive.CreateSubKey(Map(subKeyPath)));

    public void SetDword(RegistryKey key, string name, int value) =>
        Write($"set value {name}", () => key.SetValue(name, value, RegistryValueKind.DWord));

    public void DeleteValue(RegistryKey key, string name) =>
        Write($"delete value {name}", () => key.DeleteValue(name, throwOnMissingValue: false));

    public void DeleteSubKey(RegistryKey parent, string name) =>
        Write($"delete key {name}", () => parent.DeleteSubKey(name, throwOnMissingSubKey: false));

    private void Write(string description, Action write) =>
        Write(description, () =>
        {
            write();
            return true;
        });

    private T Write<T>(string description, Func<T> write)
    {
        _beforeWrite?.Invoke(description);
        var result = write();
        _afterWrite?.Invoke(description);
        return result;
    }
}
