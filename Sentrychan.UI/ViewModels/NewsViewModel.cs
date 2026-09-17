using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>One news article card. Click to expand the full blurb in place.</summary>
public class NewsItemVm : ViewModelBase
{
    public string Title { get; }
    public string Summary { get; }
    public string Link { get; }
    public string TimeAgo { get; }
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(SummaryMaxLines));
            this.RaisePropertyChanged(nameof(ExpandHint));
        }
    }

    // Collapsed cards clamp the blurb; expanded shows everything the feed gave us.
    public int SummaryMaxLines => IsExpanded ? int.MaxValue : 2;
    public string ExpandHint => IsExpanded ? "▲ collapse" : "▼ expand";

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleExpandCommand { get; }

    public NewsItemVm(string title, string summary, string link, DateTime? published)
    {
        Title   = title;
        Summary = summary;
        Link    = link;
        TimeAgo = published.HasValue ? FormatTimeAgo(published.Value) : string.Empty;

        OpenCommand = ReactiveCommand.Create(() =>
        {
            if (string.IsNullOrEmpty(Link)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Link) { UseShellExecute = true });
            }
            catch { /* browser open is best-effort */ }
        });

        ToggleExpandCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
    }

    private static string FormatTimeAgo(DateTime published)
    {
        var diff = DateTime.Now - published.ToLocalTime();
        if (diff.TotalMinutes < 60) return $"{Math.Max(1, (int)diff.TotalMinutes)}m ago";
        if (diff.TotalHours < 24)   return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7)     return $"{(int)diff.TotalDays}d ago";
        return published.ToLocalTime().ToString("MMM d, yyyy");
    }
}

/// <summary>
/// "News" page — anime-industry headlines from Anime News Network's RSS feed.
/// Phase 1 of the news/AI-recommendation feature: plain feed, open-in-browser.
/// </summary>
public class NewsViewModel : ViewModelBase
{
    // ANN's full-coverage feed; the newsroom feed is the fallback.
    private static readonly string[] FeedUrls =
    [
        "https://www.animenewsnetwork.com/all/rss.xml",
        "https://www.animenewsnetwork.com/newsroom/rss.xml"
    ];

    private static readonly Regex HtmlTagPattern = new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);

    public ObservableCollection<NewsItemVm> Items { get; } = [];

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private DateTime _lastLoaded = DateTime.MinValue;

    public NewsViewModel()
    {
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
    }

    public async Task InitializeAsync()
    {
        if (Items.Count == 0 || (DateTime.Now - _lastLoaded).TotalMinutes > 15)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = "Fetching anime news...";
        try
        {
            CodeHollow.FeedReader.Feed? feed = null;
            foreach (var url in FeedUrls)
            {
                try
                {
                    feed = await CodeHollow.FeedReader.FeedReader.ReadAsync(url);
                    if (feed.Items.Count > 0) break;
                }
                catch { /* try next source */ }
            }

            if (feed == null || feed.Items.Count == 0)
            {
                StatusMessage = "Couldn't reach Anime News Network — try again later.";
                return;
            }

            var items = feed.Items
                .Take(40)
                .Select(i => new NewsItemVm(
                    title: i.Title?.Trim() ?? "Untitled",
                    summary: CleanHtml(i.Description),
                    link: i.Link ?? string.Empty,
                    published: i.PublishingDate))
                .ToList();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Items.Clear();
                foreach (var item in items) Items.Add(item);
            });

            _lastLoaded = DateTime.Now;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"News load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string CleanHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = HtmlTagPattern.Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        // Keep the FULL text — collapsed cards clamp via MaxLines, and expanding
        // reveals everything the feed provided.
        return Regex.Replace(text, @"\s{2,}", " ").Trim();
    }
}
