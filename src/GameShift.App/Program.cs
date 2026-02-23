using System;
using System.IO;
using System.Windows;

namespace GameShift.App;

/// <summary>
/// Explicit entry point that wraps WPF startup in a top-level try/catch.
/// The default auto-generated Main from App.xaml swallows all exceptions silently
/// on WinExe projects. This captures any crash — XAML parse errors, missing assemblies,
/// runtime mismatches — and writes them to crash.log + stderr.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            WriteDiag("Program.Main starting...");

            // Phase 1: Can we even load the WPF-UI assemblies?
            WriteDiag("Phase 1: Testing assembly loads...");
            try
            {
                // Force-load key assemblies to surface missing DLL errors early
                var wpfUiType = typeof(Wpf.Ui.Controls.NavigationView);
                WriteDiag($"  WPF-UI core loaded: {wpfUiType.Assembly.GetName().Version}");

                var trayType = typeof(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon);
                WriteDiag($"  Hardcodet.NotifyIcon loaded: {trayType.Assembly.GetName().Version}");
            }
            catch (Exception ex)
            {
                WriteDiag($"  ASSEMBLY LOAD FAILED: {ex}");
                WriteCrash("ASSEMBLY_LOAD", ex);
                return 1;
            }

            // Phase 2: Create and run the WPF application
            WriteDiag("Phase 2: Creating App instance...");
            var app = new App();

            WriteDiag("Phase 3: Calling InitializeComponent (loads App.xaml resources)...");
            app.InitializeComponent();

            WriteDiag("Phase 4: Calling app.Run()...");
            app.Run();

            WriteDiag("App exited normally.");
            return 0;
        }
        catch (TypeInitializationException ex)
        {
            WriteDiag($"TYPE INIT FAILED: {ex}");
            WriteDiag($"  Inner: {ex.InnerException}");
            WriteCrash("TYPE_INIT", ex);
            return 2;
        }
        catch (System.Windows.Markup.XamlParseException ex)
        {
            WriteDiag($"XAML PARSE FAILED: {ex.Message}");
            WriteDiag($"  Inner: {ex.InnerException}");
            WriteCrash("XAML_PARSE", ex);
            return 3;
        }
        catch (Exception ex)
        {
            WriteDiag($"UNHANDLED: {ex}");
            WriteCrash("PROGRAM_MAIN", ex);
            return 4;
        }
    }

    /// <summary>
    /// Writes a diagnostic line to both stderr and a local diag file.
    /// Uses the app's working directory (not AppData) so it's easy to find.
    /// </summary>
    private static void WriteDiag(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.Error.WriteLine(line);
        try
        {
            File.AppendAllText("gameshift-diag.log", line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>
    /// Writes full exception to crash.log in AppData/GameShift/.
    /// </summary>
    private static void WriteCrash(string type, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameShift");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now}] {type} EXCEPTION:\n{ex}");
        }
        catch { }
    }
}
