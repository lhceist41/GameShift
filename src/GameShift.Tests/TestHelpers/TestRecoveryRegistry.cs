using System;
using GameShift.Core.Journal;
using Microsoft.Win32;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// A <see cref="RecoveryRegistry"/> rooted under a throwaway key in HKCU\Software\GameShiftTests, so
/// journal records with their real HKLM and HKCU paths can be replayed without touching either hive.
/// The whole tree is deleted on dispose.
/// </summary>
public sealed class TestRecoveryRegistry : IDisposable
{
    private readonly string _root = $@"Software\GameShiftTests\{Guid.NewGuid():N}";

    internal RecoveryRegistry Registry { get; }

    public TestRecoveryRegistry()
    {
        Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_root).Dispose();
        Registry = RecoveryRegistry.UnderCurrentUser(_root);
    }

    /// <summary>Creates the key as it would exist on the machine before the replay.</summary>
    internal RegistryKey CreateKey(RecoveryHive hive, string subKeyPath) =>
        Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{_root}\{hive}\{subKeyPath}");

    internal RegistryKey? Open(RecoveryHive hive, string subKeyPath) =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{_root}\{hive}\{subKeyPath}");

    internal object? Value(RecoveryHive hive, string subKeyPath, string name)
    {
        using var key = Open(hive, subKeyPath);
        return key?.GetValue(name);
    }

    public void Dispose() =>
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
}
