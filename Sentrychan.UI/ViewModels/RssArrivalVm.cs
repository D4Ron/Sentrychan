using ReactiveUI;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Represents a single raw RSS arrival item shown in the Discover side panel.
/// No Jikan lookup is performed — the title is taken directly from the RSS feed,
/// making this instant to display without any API rate-limiting delays.
/// </summary>
public class RssArrivalVm : ViewModelBase
{
    public string Title { get; init; } = string.Empty;
    public string EpisodePart { get; init; } = string.Empty;   // e.g. "Ep 12"
    public string ReleaseGroup { get; init; } = string.Empty;  // e.g. "[SubsPlease]"
    public string Quality { get; init; } = string.Empty;       // e.g. "1080p"
    public string FeedUrl { get; init; } = string.Empty;
    public string DownloadLink { get; init; } = string.Empty;
    public DateTime PublishedAt { get; init; }

    /// <summary>
    /// Download command passed in by the parent SeasonalViewModel.
    /// Triggers sending this item's DownloadLink to the chosen backend.
    /// </summary>
    public ReactiveCommand<RssArrivalVm, Unit>? DownloadCommand { get; set; }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set => this.RaiseAndSetIfChanged(ref _isDownloaded, value);
    }

    public bool HasDownloadLink => !string.IsNullOrEmpty(DownloadLink);

    public string PublishedAgo
    {
        get
        {
            var diff = DateTime.Now - PublishedAt;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            return $"{(int)diff.TotalDays}d ago";
        }
    }

    public bool IsCensored => FeedUrl.Contains("sukebei", StringComparison.OrdinalIgnoreCase);
}
