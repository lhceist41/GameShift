using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GameShift.Core.Config;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;

namespace GameShift.App.ViewModels;

/// <summary>
/// Detects leftover AMD platform-timer BCD tweaks (disabledynamictick / useplatformtick) applied by
/// an older GameShift version and offers a one-click revert + reboot. Detection runs off the UI
/// thread because it shells bcdedit; the banner appears only on AMD when those tweaks are set and
/// the user has not dismissed it.
/// </summary>
public class AmdTimerAdvisoryViewModel : INotifyPropertyChanged
{
    private bool _showBanner;
    private string _bannerMessage = "";
    private List<KernelTuningSetting> _active = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowBanner { get => _showBanner; private set { _showBanner = value; OnPropertyChanged(); } }
    public string BannerMessage { get => _bannerMessage; private set { _bannerMessage = value; OnPropertyChanged(); } }

    public AmdTimerAdvisoryViewModel()
    {
        _ = Task.Run(Detect);
    }

    private void Detect()
    {
        try
        {
            // CPUID is cached and instant - skip the bcdedit cost entirely on non-AMD machines.
            if (!CpuCapabilities.IsAmd) return;
            if (SettingsManager.Load().AmdPlatformTimerAdvisoryDismissed) return;

            var active = new KernelTuningManager().GetActivePlatformTimerTweaksToFix();
            if (active.Count == 0) return;

            _active = active;
            BannerMessage =
                "An older GameShift setting is forcing a slower platform timer on your AMD CPU, " +
                "which Windows flags as degrading performance. Fix it and restart to restore the " +
                "efficient per-core timer.";
            ShowBanner = true;
        }
        catch { /* best-effort advisory */ }
    }

    /// <summary>
    /// Reverts the detected platform-timer tweaks. Returns true if any were reverted, in which case
    /// the caller prompts for the reboot needed to apply the change.
    /// </summary>
    public bool Fix()
    {
        if (_active.Count == 0) return false;

        var mgr = new KernelTuningManager();
        bool any = false;
        foreach (var setting in _active)
        {
            var (ok, _) = mgr.Revert(setting);
            any |= ok;
        }
        if (any) ShowBanner = false;
        return any;
    }

    public void DismissBanner()
    {
        ShowBanner = false;
        try
        {
            var settings = SettingsManager.Load();
            settings.AmdPlatformTimerAdvisoryDismissed = true;
            SettingsManager.Save(settings);
        }
        catch { /* best-effort */ }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
