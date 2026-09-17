using ReactiveUI;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Represents a single active download row in the live progress panel.
/// Populated by polling GetAllStatusAsync() on the active backend.
/// </summary>
public class ActiveDownloadRowVm : ViewModelBase
{
    // The handle is the torrent hash for qBit, or job ID string for FDM
    public string Handle { get; init; } = string.Empty;

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _progressPercent, value);
            this.RaisePropertyChanged(nameof(ProgressFraction));
            this.RaisePropertyChanged(nameof(ProgressDisplay));
        }
    }

    // 0.0–1.0 for ProgressBar.Value when Maximum=1
    public double ProgressFraction => ProgressPercent / 100.0;

    public string ProgressDisplay =>
        $"{ProgressPercent:F0}%";

    private string _speedDisplay = string.Empty;
    public string SpeedDisplay
    {
        get => _speedDisplay;
        set => this.RaiseAndSetIfChanged(ref _speedDisplay, value);
    }

    private string _etaDisplay = string.Empty;
    public string EtaDisplay
    {
        get => _etaDisplay;
        set => this.RaiseAndSetIfChanged(ref _etaDisplay, value);
    }

    private string _stateDisplay = string.Empty;
    public string StateDisplay
    {
        get => _stateDisplay;
        set => this.RaiseAndSetIfChanged(ref _stateDisplay, value);
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set => this.RaiseAndSetIfChanged(ref _isPaused, value);
    }
}
