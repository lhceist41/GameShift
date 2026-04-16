using GameShift.Core.Journal;
using GameShift.Watchdog;
using Serilog;
using Serilog.Events;

// ── CLI dispatch ──────────────────────────────────────────────────────────────
// Run from an elevated command prompt:
//   GameShift.Watchdog.exe --install        sc create + description
//   GameShift.Watchdog.exe --uninstall      sc stop + sc delete
//   GameShift.Watchdog.exe --boot-recovery  run once: revert journal + update check
//   GameShift.Watchdog.exe                  run as Windows Service (normal mode)

if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "--install":
            InstallService();
            return;
        case "--uninstall":
            UninstallService();
            return;
        case "--boot-recovery":
            RunBootRecovery();
            return;
    }
}

// ── Logging ───────────────────────────────────────────────────────────────────

var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "GameShift");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, "watchdog.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// ── Host ──────────────────────────────────────────────────────────────────────

try
{
    Log.Information("GameShift Watchdog starting (PID {Pid})", Environment.ProcessId);

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "GameShiftWatchdog";
        })
        .UseSerilog()
        .ConfigureServices(services =>
        {
            services.AddHostedService<WatchdogWorker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "GameShift Watchdog host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ── Boot recovery ─────────────────────────────────────────────────────────────

/// <summary>
/// Runs the boot-recovery sequence:
///   1. Configures Serilog → %ProgramData%\GameShift\watchdog.log (same sink as the service)
///   2. Delegates to BootRecoveryHandler.Run() which handles crash recovery + update detection
///   3. Exits when done (no long-running host required)
/// </summary>
static void RunBootRecovery()
{
    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GameShift");
    Directory.CreateDirectory(logDir);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            Path.Combine(logDir, "watchdog.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    try
    {
        Log.Information("GameShift.Watchdog --boot-recovery started (PID {Pid})", Environment.ProcessId);
        BootRecoveryHandler.Run(Log.Logger);
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

// ── sc.exe helpers ────────────────────────────────────────────────────────────

/// <summary>
/// Installs the watchdog as a Windows Service using sc.exe.
///
/// Equivalent manual commands (run elevated):
///   sc create GameShiftWatchdog binPath= "C:\path\to\GameShift.Watchdog.exe" start= auto
///   sc description GameShiftWatchdog "Monitors GameShift and reverts optimizations if it crashes"
///   sc start GameShiftWatchdog
/// </summary>
static void InstallService()
{
    var exePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine process executable path");

    Console.WriteLine("Installing GameShiftWatchdog service...");
    Console.WriteLine($"  Executable: {exePath}");

    RunSc($"create GameShiftWatchdog binPath= \"{exePath}\" start= auto DisplayName= \"GameShift Watchdog\"");
    RunSc("description GameShiftWatchdog \"Monitors GameShift and reverts optimizations if it crashes\"");

    Console.WriteLine();
    Console.WriteLine("Service installed successfully.");
    Console.WriteLine("Start it now:  sc start GameShiftWatchdog");
    Console.WriteLine("Check status:  sc query GameShiftWatchdog");
}

/// <summary>
/// Removes the watchdog Windows Service.
///
/// Equivalent manual commands (run elevated):
///   sc stop GameShiftWatchdog
///   sc delete GameShiftWatchdog
/// </summary>
static void UninstallService()
{
    Console.WriteLine("Uninstalling GameShiftWatchdog service...");

    RunSc("stop GameShiftWatchdog");
    RunSc("delete GameShiftWatchdog");

    Console.WriteLine();
    Console.WriteLine("Service removed.");
}

static void RunSc(string arguments)
{
    Console.WriteLine($"  sc {arguments}");
    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sc",
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    });

    if (p == null) { Console.WriteLine("  (failed to start sc.exe)"); return; }

    var stderr = "";
    var stderrTask = Task.Run(() => { stderr = p.StandardError.ReadToEnd().Trim(); });
    var stdout = p.StandardOutput.ReadToEnd().Trim();
    stderrTask.Wait(30_000);
    p.WaitForExit(30_000);
    if (!string.IsNullOrEmpty(stdout)) Console.WriteLine($"  {stdout}");
    if (!string.IsNullOrEmpty(stderr)) Console.WriteLine($"  ERROR: {stderr}");
}
