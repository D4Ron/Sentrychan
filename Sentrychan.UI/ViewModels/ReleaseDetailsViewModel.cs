using ReactiveUI;
using System;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Torrent-level details for one Latest release — the info you'd see on the
/// nyaa/sukebei page (category, size, seeders/leechers, hash, date), sourced
/// straight from the RSS item plus the resolved cover.
/// </summary>
public class ReleaseDetailsViewModel : ViewModelBase
{
    public string Title { get; }
    public string RawTitle { get; }
    public string PosterUrl { get; }
    public string Category { get; }
    public string SizeDisplay { get; }

    // Swarm counts are settable: feeds like SubsPlease's own RSS carry no seeder data,
    // so the dialog opens with 0s and enriches them from the release's nyaa page.
    private int _seeders;
    public int Seeders { get => _seeders; set => this.RaiseAndSetIfChanged(ref _seeders, value); }
    private int _leechers;
    public int Leechers { get => _leechers; set => this.RaiseAndSetIfChanged(ref _leechers, value); }
    private int _downloads;
    public int Downloads { get => _downloads; set => this.RaiseAndSetIfChanged(ref _downloads, value); }

    public string InfoHash { get; }
    public string PublishedDisplay { get; }
    public bool Trusted { get; }
    public string TrustedDisplay => Trusted ? "Trusted ✓" : "Untrusted";
    public string ViewUrl { get; }
    public bool HasViewUrl => !string.IsNullOrEmpty(ViewUrl);
    public bool HasPoster => !string.IsNullOrEmpty(PosterUrl);

    public event Action? DownloadRequested;
    public event Action? CloseRequested;

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPageCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyHashCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public ReleaseDetailsViewModel(
        string title, string rawTitle, string posterUrl, string category,
        string sizeDisplay, int seeders, int leechers, int downloads,
        string infoHash, DateTime? published, bool trusted, string viewUrl)
    {
        Title = title;
        RawTitle = rawTitle;
        PosterUrl = posterUrl;
        Category = string.IsNullOrEmpty(category) ? "—" : category;
        SizeDisplay = string.IsNullOrEmpty(sizeDisplay) ? "—" : sizeDisplay;
        _seeders = seeders;
        _leechers = leechers;
        _downloads = downloads;
        InfoHash = infoHash;
        PublishedDisplay = published.HasValue
            ? published.Value.ToLocalTime().ToString("MMM d, yyyy · HH:mm")
            : "—";
        Trusted = trusted;
        ViewUrl = viewUrl;

        DownloadCommand = ReactiveCommand.Create(() => DownloadRequested?.Invoke());
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
        OpenPageCommand = ReactiveCommand.Create(() =>
        {
            if (string.IsNullOrEmpty(ViewUrl)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(ViewUrl) { UseShellExecute = true });
            }
            catch { }
        });
        CopyHashCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrEmpty(InfoHash)) return;
            var clip = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard : null;
            if (clip != null) await clip.SetTextAsync(InfoHash);
        });
    }
}
