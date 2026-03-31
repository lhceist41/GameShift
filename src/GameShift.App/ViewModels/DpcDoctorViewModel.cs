using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
using Serilog;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for a single driver row in the live DPC monitor table.
/// </summary>
public class DriverRowViewModel : INotifyPropertyChanged
{
    private string _friendlyName = "";
    private string _driverFileName = "";
    private string _category = "";
    private long _dpcCount;
    private double _highestUs;
    private double _avgUs;
    private DpcHealthStatus _severity;
    private double[] _sparklineData = Array.Empty<double>();

    public string FriendlyName { get => _friendlyName; set { _friendlyName = value; OnPropertyChanged(); } }
    public string DriverFileName { get => _driverFileName; set { _driverFileName = value; OnPropertyChanged(); } }
    public string Category { get => _category; set { _category = value; OnPropertyChanged(); } }
    public long DpcCount { get => _dpcCount; set { _dpcCount = value; OnPropertyChanged(); } }
    public double HighestUs { get => _highestUs; set { _highestUs = value; OnPropertyChanged(); OnPropertyChanged(nameof(HighestUsFormatted)); } }
    public double AvgUs { get => _avgUs; set { _avgUs = value; OnPropertyChanged(); OnPropertyChanged(nameof(AvgUsFormatted)); } }
    public DpcHealthStatus Severity { get => _severity; set { _severity = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); } }
    public double[] SparklineData { get => _sparklineData; set { _sparklineData = value; OnPropertyChanged(); } }

    public string HighestUsFormatted => $"{_highestUs:N0} \u00B5s";
    public string AvgUsFormatted => $"{_avgUs:N0} \u00B5s";
    public string StatusText => _severity switch
    {
        DpcHealthStatus.Critical => "Critical",
        DpcHealthStatus.Warning => "Warning",
        _ => "Good"
    };
    public string StatusColor => _severity switch
    {
        DpcHealthStatus.Critical => "#EF4444",
        DpcHealthStatus.Warning => "#F59E0B",
        _ => "#4ADE80"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for a diagnosed issue card.
/// </summary>
public class DiagnosedIssueViewModel : INotifyPropertyChanged
{
    public string DriverFileName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string SimpleExplanation { get; set; } = "";
    public string GamingImpact { get; set; } = "";
    public double PeakLatencyUs { get; set; }
    public DpcHealthStatus Severity { get; set; }
    public List<DriverAutoFix> AutoFixes { get; set; } = new();
    public List<string> ManualFixes { get; set; } = new();
    public bool IsExpanded { get; set; }

    public string PeakFormatted => $"{PeakLatencyUs:N0} \u00B5s";
    public string SeverityText => Severity == DpcHealthStatus.Critical ? "CRITICAL" : "WARNING";
    public string SeverityColor => Severity == DpcHealthStatus.Critical ? "#EF4444" : "#F59E0B";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for a quick fix toggle in the toolbar.
/// </summary>
public class QuickFixViewModel : INotifyPropertyChanged
{
    private bool _isActive;

    public string FixId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SimpleTooltip { get; set; } = "";
    public string TechnicalTooltip { get; set; } = "";
    public bool RequiresReboot { get; set; }
    public DriverAutoFix? Fix { get; set; }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateText)); }
    }

    public string StateText => _isActive ? "Active" : "Inactive";

    public string ImpactFriendly => Fix?.Impact switch
    {
        "High" => "Big improvement",
        "Medium" => "Moderate improvement",
        "Low" => "Small improvement",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for a kernel tuning BCD setting in the DPC Doctor page.
/// Shows current vs recommended value, risk badge, and apply/revert state.
/// </summary>
public class KernelTuningItemViewModel : INotifyPropertyChanged
{
    private string _currentValue = "<not set>";
    private bool _isApplied;
    private string _statusMessage = "";
    private bool _hasWarning;
    private string _warningMessage = "";

    public KernelTuningSetting Setting { get; init; } = null!;
    public string CurrentValue { get => _currentValue; set { _currentValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsChange)); } }
    public bool IsApplied { get => _isApplied; set { _isApplied = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionLabel)); } }
    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
    public bool HasWarning { get => _hasWarning; set { _hasWarning = value; OnPropertyChanged(); } }
    public string WarningMessage { get => _warningMessage; set { _warningMessage = value; OnPropertyChanged(); } }

    public string ActionLabel => _isApplied ? "Revert" : "Apply";
    public bool NeedsChange => !string.Equals(
        _currentValue, Setting.RecommendedValue, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for a single core in the Core Isolation visual map.
/// </summary>
public class CoreMapItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public uint CpuSetId { get; init; }
    public string Label { get; init; } = "";
    public bool IsPCore { get; init; }
    public bool CanSelect { get; init; } // Only P-cores can be selected

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for the DPC Doctor page.
/// Manages ETW trace capture, diagnosed issues, and quick fixes.
/// </summary>
public class DpcDoctorViewModel : INotifyPropertyChanged
{
    private readonly DpcTraceEngine? _traceEngine;
    private readonly DpcFixEngine? _fixEngine;
    private readonly KnownDriverDatabase? _driverDb;
    private readonly DpcLatencyMonitor? _dpcMon;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Dispatcher _dispatcher;

    private DispatcherTimer? _countdownTimer;
    private int _countdownSeconds;

    // -- Bindable properties

    private bool _isAdmin;
    private bool _isCapturing;
    private bool _isSimpleMode = true;
    private string _healthStatus = "Ready";
    private string _healthExplanation = "Start a trace to analyze your system's DPC latency.";
    private string _healthColor = "#808080";
    private double _systemPeakUs;
    private int _captureSeconds;
    private bool _showDiagnosedIssues;
    private bool _hasPendingRebootComparison;
    private bool _showDpcInfoPanel;
    private bool _showFixSuccessBanner;
    private string _fixSuccessMessage = "";
    private string _fixSuccessDetail = "";
    private DispatcherTimer? _successBannerTimer;
    private bool _showRebootPrompt;
    private string _rebootFixName = "";

    public bool ShowFixSuccessBanner { get => _showFixSuccessBanner; set { _showFixSuccessBanner = value; OnPropertyChanged(); } }
    public string FixSuccessMessage { get => _fixSuccessMessage; set { _fixSuccessMessage = value; OnPropertyChanged(); } }
    public string FixSuccessDetail { get => _fixSuccessDetail; set { _fixSuccessDetail = value; OnPropertyChanged(); } }
    public void DismissSuccessBanner() => ShowFixSuccessBanner = false;

    public bool ShowRebootPrompt { get => _showRebootPrompt; set { _showRebootPrompt = value; OnPropertyChanged(); } }
    public string RebootFixName { get => _rebootFixName; set { _rebootFixName = value; OnPropertyChanged(); } }
    public void DismissRebootPrompt() => ShowRebootPrompt = false;

    public bool ShowDpcInfoPanel
    {
        get => _showDpcInfoPanel;
        set { _showDpcInfoPanel = value; OnPropertyChanged(); }
    }

    public void ToggleDpcInfo() => ShowDpcInfoPanel = !ShowDpcInfoPanel;

    public bool IsAdmin { get => _isAdmin; set { _isAdmin = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotAdmin)); } }
    public bool NotAdmin => !_isAdmin;
    public bool IsCapturing { get => _isCapturing; set { _isCapturing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_isCapturing;
    public bool IsSimpleMode
    {
        get => _isSimpleMode;
        set
        {
            _isSimpleMode = value;
            _settings.DpcDoctorSimpleMode = value;
            _saveSettings();
            OnPropertyChanged();
        }
    }
    public string HealthStatus { get => _healthStatus; set { _healthStatus = value; OnPropertyChanged(); } }
    public string HealthExplanation { get => _healthExplanation; set { _healthExplanation = value; OnPropertyChanged(); } }
    public string HealthColor { get => _healthColor; set { _healthColor = value; OnPropertyChanged(); } }
    public double SystemPeakUs { get => _systemPeakUs; set { _systemPeakUs = value; OnPropertyChanged(); OnPropertyChanged(nameof(SystemPeakFormatted)); } }
    public string SystemPeakFormatted => $"{_systemPeakUs:N0} \u00B5s";
    public int CaptureSeconds { get => _captureSeconds; set { _captureSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(CaptureTimeText)); } }
    public string CaptureTimeText => $"{_captureSeconds}s";
    public bool ShowDiagnosedIssues { get => _showDiagnosedIssues; set { _showDiagnosedIssues = value; OnPropertyChanged(); } }
    public bool HasPendingRebootComparison { get => _hasPendingRebootComparison; set { _hasPendingRebootComparison = value; OnPropertyChanged(); } }

    // Fallback DPC value for non-admin limited mode
    private double _fallbackDpcValue;
    public double FallbackDpcValue { get => _fallbackDpcValue; set { _fallbackDpcValue = value; OnPropertyChanged(); } }

    // ── Interrupt affinity status (Sprint 6) ──────────────────────────────────

    private string _gpuInterruptInfo = "Not scanned";
    private string _gpuMsiInfo = "Not scanned";
    private string _usbAffinityInfo = "Not scanned";

    /// <summary>Current GPU interrupt core (e.g. "Core 11" or "Default (OS-managed)").</summary>
    public string GpuInterruptInfo { get => _gpuInterruptInfo; set { _gpuInterruptInfo = value; OnPropertyChanged(); } }

    /// <summary>GPU MSI mode status (e.g. "Enabled" or "Disabled").</summary>
    public string GpuMsiInfo { get => _gpuMsiInfo; set { _gpuMsiInfo = value; OnPropertyChanged(); } }

    /// <summary>USB controller interrupt affinity (e.g. "Core 11" or "Default").</summary>
    public string UsbAffinityInfo { get => _usbAffinityInfo; set { _usbAffinityInfo = value; OnPropertyChanged(); } }

    public ObservableCollection<DriverRowViewModel> DriverRows { get; } = new();
    public ObservableCollection<DiagnosedIssueViewModel> DiagnosedIssues { get; } = new();
    public ObservableCollection<QuickFixViewModel> QuickFixes { get; } = new();
    public ObservableCollection<string> RebootComparisonLines { get; } = new();

    /// <summary>Kernel tuning BCD settings with current/recommended values and apply/revert actions.</summary>
    public ObservableCollection<KernelTuningItemViewModel> KernelTuningItems { get; } = new();

    // ── Core Isolation (Sprint 5C) ────────────────────────────────────────────

    private string _coreIsolationStatus = "Loading...";
    private string _coreIsolationWarning = "";
    private bool _showCoreIsolationWarning;
    private bool _isCoreIsolationAvailable;
    private CoreIsolationStatus? _coreIsolationData;

    public string CoreIsolationStatus { get => _coreIsolationStatus; set { _coreIsolationStatus = value; OnPropertyChanged(); } }
    public string CoreIsolationWarning { get => _coreIsolationWarning; set { _coreIsolationWarning = value; OnPropertyChanged(); } }
    public bool ShowCoreIsolationWarning { get => _showCoreIsolationWarning; set { _showCoreIsolationWarning = value; OnPropertyChanged(); } }
    public bool IsCoreIsolationAvailable { get => _isCoreIsolationAvailable; set { _isCoreIsolationAvailable = value; OnPropertyChanged(); } }

    /// <summary>Core map items for the visual display.</summary>
    public ObservableCollection<CoreMapItemViewModel> CoreMapItems { get; } = new();

    public DpcDoctorViewModel(
        DpcTraceEngine? traceEngine,
        DpcFixEngine? fixEngine,
        KnownDriverDatabase? driverDb,
        DpcLatencyMonitor? dpcMon,
        AppSettings settings,
        Action saveSettings)
    {
        _traceEngine = traceEngine;
        _fixEngine = fixEngine;
        _driverDb = driverDb;
        _dpcMon = dpcMon;
        _settings = settings;
        _saveSettings = saveSettings;
        _dispatcher = Application.Current.Dispatcher;

        IsAdmin = AdminHelper.IsRunningAsAdmin();
        _isSimpleMode = settings.DpcDoctorSimpleMode;

        if (_traceEngine != null)
            _traceEngine.DriversUpdated += OnDriversUpdated;

        // Subscribe to fallback DPC monitor for non-admin mode
        if (_dpcMon != null)
            _dpcMon.LatencySampled += OnFallbackLatencySampled;

        InitializeQuickFixes();
        InitializeKernelTuning();
        InitializeCoreIsolation();
        CheckPendingRebootComparison();
        RefreshInterruptAffinityStatus();
    }

    // -- Commands

    public void StartCapture()
    {
        if (_traceEngine == null || !IsAdmin) return;

        var success = _traceEngine.StartCapture();
        if (!success)
        {
            HealthStatus = "Failed to start";
            HealthExplanation = "Another DPC monitoring tool may be running (like LatencyMon). Close it and try again.";
            HealthColor = "#EF4444";
            return;
        }

        IsCapturing = true;
        ShowDiagnosedIssues = false;
        HealthStatus = "Capturing...";
        HealthExplanation = "Collecting per-driver DPC data. Run for at least 10 seconds for diagnosis.";
        HealthColor = "#3B82F6";
    }

    public void StopCapture()
    {
        if (_traceEngine == null) return;

        // Save peaks before stopping
        _settings.DpcDoctorLastRunPeaks = _traceEngine.GetDriverPeaks();
        _saveSettings();

        _traceEngine.StopCapture();
        IsCapturing = false;

        UpdateHealthBanner();
        AnalyzeDiagnosedIssues();

        if (_settings.PendingRebootFixes.Count > 0)
            GenerateRebootComparison();
    }

    public void RunFor30Seconds()
    {
        StartCapture();
        if (!IsCapturing) return;

        _countdownSeconds = 30;
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (s, e) =>
        {
            _countdownSeconds--;
            if (_countdownSeconds <= 0)
            {
                _countdownTimer.Stop();
                StopCapture();
            }
        };
        _countdownTimer.Start();
    }

    public void ApplyFix(DriverAutoFix fix)
    {
        if (_fixEngine == null) return;

        var result = _fixEngine.ApplyFix(fix);
        if (result.Success)
        {
            RefreshQuickFixStates();

            if (result.RebootRequired)
            {
                FixSuccessMessage = $"\u2705 {fix.Name} applied successfully";
                FixSuccessDetail = GetWhatToExpect(fix) + " A restart is needed for this change to take effect.";
                ShowFixSuccessBanner = true;
                RebootFixName = fix.Name;
                ShowRebootPrompt = true;
                RebootRequested?.Invoke(fix.Name);
                // No auto-dismiss for reboot fixes
            }
            else
            {
                FixSuccessMessage = $"\u2705 {fix.Name} applied successfully";
                FixSuccessDetail = GetWhatToExpect(fix);
                ShowFixSuccessBanner = true;

                // Auto-dismiss after 8 seconds
                _successBannerTimer?.Stop();
                _successBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _successBannerTimer.Tick += (s, e) =>
                {
                    _successBannerTimer.Stop();
                    ShowFixSuccessBanner = false;
                };
                _successBannerTimer.Start();
            }
        }

        FixApplied?.Invoke(result);
    }

    private string GetWhatToExpect(DriverAutoFix fix) => fix.Impact switch
    {
        "High" => "You should notice smoother gameplay. Run DPC Doctor again to verify.",
        "Medium" => "This may reduce occasional hitching. Try a game session to test.",
        _ => "A small improvement \u2014 most noticeable in audio quality or input response."
    };

    public void RevertFix(string fixId)
    {
        if (_fixEngine == null) return;

        var result = _fixEngine.RevertFix(fixId);
        if (result.Success)
            RefreshQuickFixStates();

        FixApplied?.Invoke(result);
    }

    public void ToggleQuickFix(QuickFixViewModel quickFix)
    {
        if (_fixEngine == null || quickFix.Fix == null) return;

        if (quickFix.IsActive)
            RevertFix(quickFix.FixId);
        else
            ApplyFix(quickFix.Fix);
    }

    /// <summary>Event for the page to show fix result feedback (snackbar/dialog).</summary>
    public event Action<DpcFixResult>? FixApplied;

    /// <summary>Event for the page to show reboot required dialog.</summary>
    public event Action<string>? RebootRequested;

    // -- Private methods

    private void OnDriversUpdated(IReadOnlyList<DriverDpcStats> topDrivers)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Update existing rows or add new ones
            for (int i = 0; i < topDrivers.Count; i++)
            {
                var stats = topDrivers[i];

                if (i < DriverRows.Count)
                {
                    var row = DriverRows[i];
                    row.FriendlyName = stats.FriendlyName;
                    row.DriverFileName = stats.DriverFileName;
                    row.Category = stats.Category;
                    row.DpcCount = stats.DpcCount;
                    row.HighestUs = stats.HighestExecutionMicroseconds;
                    row.AvgUs = stats.AverageExecutionMicroseconds;
                    row.Severity = stats.Severity;

                    // Copy sparkline data
                    var sparkline = new double[stats.HistoryCount];
                    for (int j = 0; j < stats.HistoryCount; j++)
                    {
                        var idx = (stats.HistoryIndex - stats.HistoryCount + j + 60) % 60;
                        sparkline[j] = stats.RecentHistory[idx];
                    }
                    row.SparklineData = sparkline;
                }
                else
                {
                    DriverRows.Add(new DriverRowViewModel
                    {
                        FriendlyName = stats.FriendlyName,
                        DriverFileName = stats.DriverFileName,
                        Category = stats.Category,
                        DpcCount = stats.DpcCount,
                        HighestUs = stats.HighestExecutionMicroseconds,
                        AvgUs = stats.AverageExecutionMicroseconds,
                        Severity = stats.Severity
                    });
                }
            }

            // Remove excess rows
            while (DriverRows.Count > topDrivers.Count)
                DriverRows.RemoveAt(DriverRows.Count - 1);

            // Update system peak
            if (_traceEngine != null)
            {
                SystemPeakUs = _traceEngine.SystemPeakDpc;
                CaptureSeconds = _traceEngine.CaptureSeconds;
            }

            UpdateHealthBanner();
        });
    }

    private void UpdateHealthBanner()
    {
        if (_traceEngine == null) return;

        var health = _traceEngine.SystemHealth;
        switch (health)
        {
            case DpcHealthStatus.Good:
                HealthStatus = "DPC Health: Excellent";
                HealthExplanation = "Your system is responding to hardware quickly. Games should feel smooth and responsive.";
                HealthColor = "#4ADE80";
                break;
            case DpcHealthStatus.Warning:
                HealthStatus = "DPC Health: Warning";
                HealthExplanation = "Some hardware drivers are taking longer than they should to respond. You might notice occasional stuttering or audio glitches.";
                HealthColor = "#F59E0B";
                break;
            case DpcHealthStatus.Critical:
                HealthStatus = $"DPC Health: Critical \u2014 {SystemPeakUs:N0} \u00B5s peak";
                HealthExplanation = $"A driver is freezing your system for {SystemPeakUs / 1000.0:N1}ms every time it runs. This directly causes stuttering, audio crackling, and input delay in games. The good news: this is almost always fixable.";
                HealthColor = "#EF4444";
                break;
        }
    }

    private void AnalyzeDiagnosedIssues()
    {
        if (_driverDb == null || _traceEngine == null) return;
        if (_traceEngine.CaptureSeconds < 10) return;

        DiagnosedIssues.Clear();
        var topDrivers = _traceEngine.GetTopDrivers(20);

        foreach (var stats in topDrivers)
        {
            if (stats.Severity == DpcHealthStatus.Good) continue;
            if (!_driverDb.TryGetDriver(stats.DriverFileName, out var info)) continue;

            DiagnosedIssues.Add(new DiagnosedIssueViewModel
            {
                DriverFileName = stats.DriverFileName,
                FriendlyName = info.FriendlyName,
                SimpleExplanation = info.SimpleExplanation,
                GamingImpact = info.GamingImpact,
                PeakLatencyUs = stats.HighestExecutionMicroseconds,
                Severity = stats.Severity,
                AutoFixes = info.AutoFixes,
                ManualFixes = info.ManualFixes
            });
        }

        ShowDiagnosedIssues = DiagnosedIssues.Count > 0;
    }

    private void InitializeQuickFixes()
    {
        if (_driverDb == null || _fixEngine == null) return;

        // Quick fix 1: Disable Dynamic Tick (from ACPI.sys)
        if (_driverDb.TryGetDriver("ACPI.sys", out var acpi))
        {
            var fix = acpi.AutoFixes.FirstOrDefault(f => f.Id == "disable_dynamic_tick");
            if (fix != null)
            {
                QuickFixes.Add(new QuickFixViewModel
                {
                    FixId = fix.Id,
                    Name = "Disable Dynamic Tick",
                    SimpleTooltip = fix.SimpleExplanation,
                    TechnicalTooltip = fix.TechnicalExplanation,
                    RequiresReboot = true,
                    Fix = fix,
                    IsActive = _fixEngine.IsFixActive(fix.Id)
                });
            }
        }

        // Quick fix 2: Force MSI Mode for GPU (detect vendor)
        var gpuInfo = GpuPciDetector.DetectGpuMsiState();
        if (gpuInfo != null)
        {
            var gpuDriver = gpuInfo.Vendor == "NVIDIA" ? "nvlddmkm.sys" : "atikmdag.sys";
            var msiFixId = gpuInfo.Vendor == "NVIDIA" ? "msi_mode_nvidia" : "msi_mode_amd";

            if (_driverDb.TryGetDriver(gpuDriver, out var gpuDb))
            {
                var fix = gpuDb.AutoFixes.FirstOrDefault(f => f.Id == msiFixId);
                if (fix != null)
                {
                    QuickFixes.Add(new QuickFixViewModel
                    {
                        FixId = fix.Id,
                        Name = "Force MSI Mode for GPU",
                        SimpleTooltip = fix.SimpleExplanation,
                        TechnicalTooltip = fix.TechnicalExplanation,
                        RequiresReboot = true,
                        Fix = fix,
                        IsActive = gpuInfo.MsiEnabled || _fixEngine.IsFixActive(fix.Id)
                    });
                }
            }
        }

        // Quick fix 3: Disable HPET (bcdedit)
        QuickFixes.Add(new QuickFixViewModel
        {
            FixId = "disable_hpet",
            Name = "Disable HPET",
            SimpleTooltip = "The High Precision Event Timer is a hardware clock that some systems handle poorly, causing delays. Disabling it forces Windows to use the faster TSC built into your CPU.",
            TechnicalTooltip = "Runs 'bcdedit /set useplatformtick yes' to disable HPET, forcing TSC as the primary clock source.",
            RequiresReboot = true,
            Fix = new DriverAutoFix
            {
                Id = "disable_hpet",
                Name = "Disable HPET",
                ActionType = "BcdEdit",
                Command = "bcdedit /set useplatformtick yes",
                RevertCommand = "bcdedit /deletevalue useplatformtick",
                RequiresReboot = true,
                Impact = "Medium"
            },
            IsActive = _fixEngine.IsFixActive("disable_hpet")
        });
    }

    private void RefreshQuickFixStates()
    {
        if (_fixEngine == null) return;
        foreach (var qf in QuickFixes)
            qf.IsActive = _fixEngine.IsFixActive(qf.FixId);
    }

    private void CheckPendingRebootComparison()
    {
        if (_settings.PendingRebootFixes.Count == 0 || _settings.DpcDoctorLastRunPeaks.Count == 0)
            return;

        HasPendingRebootComparison = true;

        var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        foreach (var fixId in _settings.PendingRebootFixes)
        {
            var applied = _settings.AppliedDpcFixes.FirstOrDefault(f => f.FixId == fixId);
            if (applied == null) continue;

            if (bootTime > applied.AppliedAt)
                RebootComparisonLines.Add($"Reboot complete: {applied.Description} \u2014 run scan to compare");
            else
                RebootComparisonLines.Add($"Pending reboot: {applied.Description}");
        }
    }

    /// <summary>
    /// Called after a post-reboot trace to generate before/after comparison.
    /// </summary>
    public void GenerateRebootComparison()
    {
        if (_traceEngine == null) return;

        var currentPeaks = _traceEngine.GetDriverPeaks();
        var previousPeaks = _settings.DpcDoctorLastRunPeaks;

        RebootComparisonLines.Clear();

        foreach (var (driver, previousPeak) in previousPeaks)
        {
            if (currentPeaks.TryGetValue(driver, out var currentPeak))
            {
                var reduction = previousPeak > 0 ? (1 - currentPeak / previousPeak) * 100 : 0;
                RebootComparisonLines.Add($"{driver}: {previousPeak:N0} \u00B5s \u2192 {currentPeak:N0} \u00B5s ({reduction:N0}% reduction)");
            }
        }

        // Clear pending reboot fixes
        _settings.PendingRebootFixes.Clear();
        _saveSettings();
        HasPendingRebootComparison = RebootComparisonLines.Count > 0;
    }

    private void OnFallbackLatencySampled(object? sender, double value)
    {
        _dispatcher.BeginInvoke(() => FallbackDpcValue = value);
    }

    public void Cleanup()
    {
        if (_traceEngine != null)
            _traceEngine.DriversUpdated -= OnDriversUpdated;

        if (_dpcMon != null)
            _dpcMon.LatencySampled -= OnFallbackLatencySampled;

        _countdownTimer?.Stop();
        _successBannerTimer?.Stop();
    }

    // ── Kernel tuning (Sprint 7) ─────────────────────────────────────────────

    private void InitializeKernelTuning()
    {
        try
        {
            var mgr = new KernelTuningManager();
            var currentValues = mgr.ReadCurrentValues();

            foreach (var setting in KernelTuningManager.AllSettings)
            {
                var item = new KernelTuningItemViewModel
                {
                    Setting = setting,
                    CurrentValue = currentValues.TryGetValue(setting.BcdKey, out var v) && v != null
                        ? v
                        : "<not set>",
                };

                // Check if the current value already matches the recommendation
                item.IsApplied = !item.NeedsChange;

                // Hypervisor: warn about dependencies
                if (setting.Id == "hypervisorlaunchtype" && !item.IsApplied)
                {
                    var deps = mgr.CheckHypervisorDependencies();
                    if (deps.HasDependencies)
                    {
                        item.HasWarning = true;
                        item.WarningMessage = deps.Summary;
                    }
                }

                KernelTuningItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DpcDoctorViewModel] Failed to initialize kernel tuning");
        }
    }

    /// <summary>
    /// Toggles a kernel tuning setting: applies if not active, reverts if active.
    /// </summary>
    public void ToggleKernelTuningSetting(KernelTuningItemViewModel item)
    {
        if (_fixEngine == null) return;

        var mgr = new KernelTuningManager();

        if (item.IsApplied)
        {
            // Revert
            var (success, msg) = mgr.Revert(item.Setting);
            item.StatusMessage = msg;
            if (success)
            {
                item.IsApplied = false;
                item.CurrentValue = "<not set>";
                ShowRebootPrompt = true;
                RebootFixName = item.Setting.DisplayName;
            }
        }
        else
        {
            // Apply
            var (success, msg) = mgr.Apply(item.Setting, item.CurrentValue);
            item.StatusMessage = msg;
            if (success)
            {
                item.IsApplied = true;
                item.CurrentValue = item.Setting.RecommendedValue;
                ShowRebootPrompt = true;
                RebootFixName = item.Setting.DisplayName;

                // Persist in AppSettings for reboot tracking
                _settings.PendingRebootFixes.Add($"bcd_{item.Setting.Id}");
                _settings.AppliedDpcFixes.Add(new AppliedDpcFix
                {
                    FixId = $"bcd_{item.Setting.Id}",
                    Description = item.Setting.DisplayName,
                    ActionType = "BcdEdit",
                    PreviousValue = item.Setting.RevertArgs,
                    Target = item.Setting.ApplyArgs,
                    AppliedAt = DateTime.Now,
                    RequiresReboot = true
                });
                _saveSettings();
            }
        }
    }

    // ── Core Isolation (Sprint 5C) ──────────────────────────────────────────

    private void InitializeCoreIsolation()
    {
        try
        {
            var mgr = new GameShift.Core.Optimization.CoreIsolationManager();
            _coreIsolationData = mgr.GetStatus();

            IsCoreIsolationAvailable = _coreIsolationData.IsHybridCpu;
            CoreIsolationStatus = _coreIsolationData.Message;

            if (!_coreIsolationData.IsHybridCpu)
                return;

            if (_coreIsolationData.PCoreTotalCount < 4)
            {
                ShowCoreIsolationWarning = true;
                CoreIsolationWarning = "Your CPU has fewer than 4 P-cores. Core reservation may not be beneficial.";
            }

            // Build core map
            CoreMapItems.Clear();
            foreach (var core in _coreIsolationData.AllCores)
            {
                CoreMapItems.Add(new CoreMapItemViewModel
                {
                    CpuSetId = core.CpuSetId,
                    Label = core.Label,
                    IsPCore = core.IsPCore,
                    CanSelect = core.IsPCore, // Only P-cores can be reserved
                    IsSelected = _coreIsolationData.ReservedCpuSetIds.Contains(core.CpuSetId)
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DpcDoctorViewModel] Failed to initialize core isolation");
            CoreIsolationStatus = "Failed to detect CPU topology.";
        }
    }

    /// <summary>
    /// Applies the current core map selection as a reservation. Requires reboot.
    /// </summary>
    public void ApplyCoreIsolation(bool restartNow)
    {
        var selected = CoreMapItems.Where(c => c.IsSelected && c.IsPCore).Select(c => c.CpuSetId).ToList();

        var mgr = new GameShift.Core.Optimization.CoreIsolationManager();
        var error = mgr.ApplyReservation(selected);

        if (error != null)
        {
            CoreIsolationStatus = $"Error: {error}";
            return;
        }

        CoreIsolationStatus = $"{selected.Count} P-core(s) reserved. Reboot required.";
        ShowRebootPrompt = true;
        RebootFixName = "Core Isolation";

        if (restartNow)
        {
            global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown", Arguments = "/r /t 10",
                UseShellExecute = false, CreateNoWindow = true
            });
        }
    }

    /// <summary>
    /// Removes the core reservation (deletes ReservedCpuSets). Requires reboot.
    /// </summary>
    public void RemoveCoreIsolation()
    {
        var mgr = new GameShift.Core.Optimization.CoreIsolationManager();
        var error = mgr.RemoveReservation();

        if (error != null)
        {
            CoreIsolationStatus = $"Error: {error}";
            return;
        }

        CoreIsolationStatus = "Reservation removed. Reboot required to restore default scheduling.";
        ShowRebootPrompt = true;
        RebootFixName = "Core Isolation removal";

        // Deselect all in core map
        foreach (var item in CoreMapItems)
            item.IsSelected = false;
    }

    /// <summary>
    /// Selects the default recommended cores (all P-cores except last).
    /// </summary>
    public void SelectDefaultCores()
    {
        var mgr = new GameShift.Core.Optimization.CoreIsolationManager();
        var defaults = new HashSet<uint>(mgr.GetDefaultSuggestion());

        foreach (var item in CoreMapItems)
        {
            if (item.IsPCore)
                item.IsSelected = defaults.Contains(item.CpuSetId);
        }
    }

    // ── Interrupt affinity status ────────────────────────────────────────────

    /// <summary>
    /// Scans GPU and USB controller interrupt configuration and populates the status properties.
    /// Called once at construction and can be called again after applying/reverting fixes.
    /// </summary>
    public void RefreshInterruptAffinityStatus()
    {
        try
        {
            var scanner = new GameShift.Core.SystemTweaks.Tweaks.OptimizeInterruptHandling();
            scanner.ScanDevices();

            // GPU interrupt core
            if (scanner.PrimaryGpu != null)
            {
                var gpuCore = scanner.CurrentGpuInterruptCore;
                GpuInterruptInfo = gpuCore != null
                    ? $"Core {gpuCore} ({scanner.PrimaryGpu.DisplayName})"
                    : $"Default ({scanner.PrimaryGpu.DisplayName})";

                GpuMsiInfo = scanner.PrimaryGpu.MsiEnabled
                    ? "Enabled"
                    : scanner.PrimaryGpu.MsiSupported ? "Disabled (can enable)" : "Not supported";
            }
            else
            {
                GpuInterruptInfo = "No GPU detected";
                GpuMsiInfo = "N/A";
            }

            // USB controller
            if (scanner.PrimaryUsb != null)
            {
                var usbCore = scanner.CurrentUsbInterruptCore;
                UsbAffinityInfo = usbCore != null
                    ? $"Core {usbCore} ({scanner.PrimaryUsb.DisplayName})"
                    : $"Default ({scanner.PrimaryUsb.DisplayName})";
            }
            else
            {
                UsbAffinityInfo = "No USB controller detected";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DpcDoctorViewModel] Failed to refresh interrupt affinity status");
            GpuInterruptInfo = "Scan failed";
            GpuMsiInfo = "Scan failed";
            UsbAffinityInfo = "Scan failed";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
