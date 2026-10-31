using System;
using System.IO;
using System.Runtime.CompilerServices;
using GameShift.Core.Config;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// Points SettingsManager's default folder at a per-run temp directory before any test can touch
/// it, so the suite never reads or writes the real %AppData%\GameShift settings or the real
/// %ProgramData%\GameShift logs.
/// </summary>
internal static class TestAppData
{
    /// <summary>All test folders live here; the verified data folders accept it in place of the user's profile.</summary>
    internal static string TestsRoot { get; } = Path.Combine(Path.GetTempPath(), "gameshift-tests");

    internal static string Root { get; } = Path.Combine(TestsRoot, "appdata-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        UserDataFolder.TestHooks.AllowedRootOverride = TestsRoot;
        SettingsManager.TestHooks.DefaultAppDataPathOverride = Root;
        SettingsManager.TestHooks.LogsPathOverride = Path.Combine(Root, "logs");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Serilog.Log.CloseAndFlush();
            UserDataFolder.TestHooks.ReleaseAtOrBelow(Root);
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { /* Best-effort cleanup */ }
        };
    }
}
