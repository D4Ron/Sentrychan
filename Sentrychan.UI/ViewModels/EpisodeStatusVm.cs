using Avalonia;
using Avalonia.Media;
using ReactiveUI;

namespace Sentrychan.UI.ViewModels;

public enum EpisodeStatus
{
    Missing    = 0,  // no file, not watched, not manually marked
    Pending    = 1,  // DownloadJob exists but file not yet on disk
    Downloaded = 2,  // file exists on disk OR user manually marked
    Watched    = 3,  // user has set their current episode to >= this ep
}

public class EpisodeStatusVm : ViewModelBase
{
    private int _episodeNumber;
    public int EpisodeNumber
    {
        get => _episodeNumber;
        set => this.RaiseAndSetIfChanged(ref _episodeNumber, value);
    }

    private EpisodeStatus _status;
    public EpisodeStatus Status
    {
        get => _status;
        set 
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(StatusBrush));
            this.RaisePropertyChanged(nameof(TooltipText));
        }
    }

    private bool _isCurrentEpisode;
    public bool IsCurrentEpisode
    {
        get => _isCurrentEpisode;
        set => this.RaiseAndSetIfChanged(ref _isCurrentEpisode, value);
    }

    public DateTime? DownloadedAt { get; set; }

    public IBrush StatusBrush => (Application.Current!.Styles.TryGetResource(Status switch
    {
        EpisodeStatus.Watched    => "StatusGreenBrush",
        EpisodeStatus.Downloaded => "AccentBrush",
        EpisodeStatus.Pending    => "StatusAmberBrush",
        _                        => "StatusRedBrush"
    }, null, out var res) ? (IBrush)res! : Brushes.Red);

    public string TooltipText => Status switch
    {
        EpisodeStatus.Watched    => $"Episode {EpisodeNumber}: Watched",
        EpisodeStatus.Downloaded => $"Episode {EpisodeNumber}: Downloaded",
        EpisodeStatus.Pending    => $"Episode {EpisodeNumber}: Downloading...",
        _                        => $"Episode {EpisodeNumber}: Missing"
    };
}
