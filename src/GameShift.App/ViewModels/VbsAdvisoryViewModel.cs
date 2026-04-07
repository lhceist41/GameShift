using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using GameShift.Core.Optimization;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages VBS/HVCI advisory banner state, including anti-cheat conflict detection.
/// </summary>
public class VbsAdvisoryViewModel : INotifyPropertyChanged
{
    private readonly VbsHvciToggle? _vbsHvciToggle;

    private bool _showVbsBanner;
    private string _vbsBannerMessage = "";
    private bool _isVbsConflict;
    private string _vbsBannerSeverity = "warning";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowVbsBanner
    {
        get => _showVbsBanner;
        private set { _showVbsBanner = value; OnPropertyChanged(); }
    }

    public string VbsBannerMessage
    {
        get => _vbsBannerMessage;
        private set { _vbsBannerMessage = value; OnPropertyChanged(); }
    }

    public bool IsVbsConflict
    {
        get => _isVbsConflict;
        private set { _isVbsConflict = value; OnPropertyChanged(); }
    }

    public string VbsBannerSeverity
    {
        get => _vbsBannerSeverity;
        private set { _vbsBannerSeverity = value; OnPropertyChanged(); }
    }

    public VbsAdvisoryViewModel(VbsHvciToggle? vbsHvciToggle)
    {
        _vbsHvciToggle = vbsHvciToggle;

        // Initial VBS state check with anti-cheat conflict detection
        if (_vbsHvciToggle != null)
        {
            var blockingACs = AntiCheatDetector.GetVbsRequiringAntiCheats();
            if (!_vbsHvciToggle.IsEitherEnabled && blockingACs.Count > 0)
            {
                _isVbsConflict = true;
                _vbsBannerSeverity = "error";
                _showVbsBanner = true;
                var acNames = string.Join(", ", blockingACs.Select(ac => ac.DisplayName));
                _vbsBannerMessage = $"Memory Integrity is disabled but required by {acNames}. " +
                    "You may experience VAN:RESTRICTION errors or anti-cheat failures. " +
                    "Click Re-enable & Reboot to fix.";
            }
            else
            {
                _isVbsConflict = false;
                _vbsBannerSeverity = "warning";
                _showVbsBanner = _vbsHvciToggle.ShouldShowBanner;
                _vbsBannerMessage = _vbsHvciToggle.BannerMessage;
            }
        }
    }

    public void DismissVbsBanner()
    {
        _vbsHvciToggle?.DismissBanner();
        ShowVbsBanner = false;
    }

    public bool DisableVbsHvci()
    {
        if (_vbsHvciToggle == null) return false;
        var result = _vbsHvciToggle.DisableVbsHvci();
        if (result)
        {
            ShowVbsBanner = false;
        }
        return result;
    }

    public bool ReEnableVbsHvci()
    {
        if (_vbsHvciToggle == null) return false;
        var result = _vbsHvciToggle.ReEnableVbsHvci();
        if (result)
        {
            IsVbsConflict = false;
            VbsBannerSeverity = "warning";
            ShowVbsBanner = false;
        }
        return result;
    }

    public void Start() { }
    public void Stop() { }
    public void Cleanup() { }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
