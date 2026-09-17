using ReactiveUI;
using Sentrychan.Core.Models;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

public class NyaaResultRowVm : ViewModelBase
{
    private readonly NyaaResult _result;

    public NyaaResultRowVm(NyaaResult result, ReactiveCommand<NyaaResultRowVm, Unit> downloadCommand)
    {
        _result = result;
        DownloadCommand = downloadCommand;
    }

    public string Title => _result.Title;
    public string Size => _result.SizeDisplay;
    public string Date => _result.PublishedAt.ToString("MMM dd yyyy");
    public int Seeders => _result.Seeders;
    public int Leechers => _result.Leechers;
    public string Category => "Anime"; // Default or inferred

    // Computed UI styling properties based on group/quality
    public bool IsTrusted => Title.Contains("[SubsPlease]") || Title.Contains("[Erai-raws]");
    public string HighlightColor => IsTrusted ? "#4CAF50" : "#FFFFFF"; // Green for trusted

    public string ParsedQuality
    {
        get
        {
            if (Title.Contains("1080p", System.StringComparison.OrdinalIgnoreCase)) return "1080p";
            if (Title.Contains("720p", System.StringComparison.OrdinalIgnoreCase)) return "720p";
            if (Title.Contains("480p", System.StringComparison.OrdinalIgnoreCase)) return "480p";
            return "Unknown";
        }
    }

    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        private set => this.RaiseAndSetIfChanged(ref _isDownloaded, value);
    }

    public void MarkDownloaded() => IsDownloaded = true;

    public ReactiveCommand<NyaaResultRowVm, Unit> DownloadCommand { get; }

    public NyaaResult GetUnderlyingResult() => _result;
}
