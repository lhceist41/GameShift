using System.Collections.Generic;
using GameShift.Core.Detection;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="ILibraryScanner"/> for detection tests. Lets a test control the launcher
/// identity, the installed/not-installed gate and the returned game list without touching the
/// registry or the filesystem, and records whether the scan was actually called so a test can
/// prove <see cref="GameDetector.ScanLibraries"/> skips uninstalled launchers.
/// </summary>
public sealed class FakeLibraryScanner : ILibraryScanner
{
    private readonly List<GameInfo> _games;

    public FakeLibraryScanner(string launcherName, bool isInstalled = true, params GameInfo[] games)
    {
        LauncherName = launcherName;
        IsInstalled = isInstalled;
        _games = new List<GameInfo>(games);
    }

    public string LauncherName { get; }

    public bool IsInstalled { get; set; }

    /// <summary>True once <see cref="ScanInstalledGames"/> has been called.</summary>
    public bool ScanCalled { get; private set; }

    /// <summary>
    /// When set, <see cref="ScanInstalledGames"/> throws instead of returning games. Used to prove
    /// an uninstalled launcher is never scanned.
    /// </summary>
    public bool ThrowOnScan { get; set; }

    public List<GameInfo> ScanInstalledGames()
    {
        ScanCalled = true;

        if (ThrowOnScan)
            throw new InvalidOperationException($"{LauncherName} scanner must not be called.");

        return new List<GameInfo>(_games);
    }
}
