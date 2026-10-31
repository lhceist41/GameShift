using System;
using System.IO;
using System.Runtime.CompilerServices;
using GameShift.Core.Config;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// Points SettingsManager's default folder at a per-run temp directory before any test can touch
/// it, so the suite never reads or writes the real %AppData%\GameShift settings and logs.
/// </summary>
internal static class TestAppData
{
    internal static string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "gameshift-tests",
        "appdata-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        SettingsManager.TestHooks.DefaultAppDataPathOverride = Root;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Serilog.Log.CloseAndFlush();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { /* Best-effort cleanup */ }
        };
    }
}
