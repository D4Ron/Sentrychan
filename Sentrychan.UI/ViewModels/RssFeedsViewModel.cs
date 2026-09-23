using ReactiveUI;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using Sentrychan.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unit = System.Reactive.Unit;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels;

public class RssFeedRowVm : ViewModelBase
{
    public RssFeed Feed { get; set; } = new();
    public bool IsDefault { get; set; }
    public bool IsSecretFeed { get; set; }

    public string DisplayUrl => Feed.Url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
    public string FeedTypeLabel => Feed.FeedType == FeedType.Priority ? "PRIMARY" : "SECONDARY";
    public string QualityLabel => Feed.PreferredQuality ?? "Global";
    public string HealthStatus
    {
        get
        {
            if (!Feed.IsEnabled) return "Disabled";
            if (Feed.ConsecutiveFailures == 0) return "Healthy";
            return $"Failing ({Feed.ConsecutiveFailures})";
        }
    }
    public string HealthColor
    {
        get
        {
            if (!Feed.IsEnabled) return "#808080";
            if (Feed.ConsecutiveFailures == 0) return "#4CAF50";
            if (Feed.ConsecutiveFailures < 3) return "#FFA726";
            return "#F44336";
        }
    }

    public string LastCheckedText => Feed.LastCheckedAt.HasValue
        ? $"Last checked: {Feed.LastCheckedAt.Value.ToLocalTime():HH:mm}"
        : "Never checked";
}

public class RssFeedsViewModel : ViewModelBase
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly IThemeService? _themeService;
    private readonly IRssMonitorService? _rssMonitor;

    public ObservableCollection<RssFeedRowVm> Feeds { get; } = [];

    private string _newFeedUrl = string.Empty;
    public string NewFeedUrl
    {
        get => _newFeedUrl;
        set => this.RaiseAndSetIfChanged(ref _newFeedUrl, value);
    }

    private string _newFeedType = "Primary";
    public string NewFeedType
    {
        get => _newFeedType;
        set => this.RaiseAndSetIfChanged(ref _newFeedType, value);
    }

    private string _newFeedQuality = "Use Global";
    public string NewFeedQuality
    {
        get => _newFeedQuality;
        set => this.RaiseAndSetIfChanged(ref _newFeedQuality, value);
    }

    private string? _addFeedError;
    public string? AddFeedError
    {
        get => _addFeedError;
        set => this.RaiseAndSetIfChanged(ref _addFeedError, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public ReactiveCommand<Unit, Unit> LoadFeedsCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFeedCommand { get; }
    public ReactiveCommand<RssFeedRowVm, Unit> ToggleFeedCommand { get; }
    public ReactiveCommand<RssFeedRowVm, Unit> RemoveFeedCommand { get; }
    public ReactiveCommand<RssFeedRowVm, Unit> TestFeedCommand { get; }

    // Design-time
    public RssFeedsViewModel()
    {
        LoadFeedsCommand = ReactiveCommand.CreateFromTask(LoadFeedsAsync);
        AddFeedCommand = ReactiveCommand.CreateFromTask(AddFeedAsync);
        ToggleFeedCommand = ReactiveCommand.CreateFromTask<RssFeedRowVm>(ToggleFeedAsync);
        RemoveFeedCommand = ReactiveCommand.CreateFromTask<RssFeedRowVm>(RemoveFeedAsync);
        TestFeedCommand = ReactiveCommand.CreateFromTask<RssFeedRowVm>(TestFeedAsync);
    }

    // Runtime
    public RssFeedsViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        IThemeService themeService,
        IRssMonitorService rssMonitor) : this()
    {
        _dbFactory = dbFactory;
        _themeService = themeService;
        _rssMonitor = rssMonitor;

        if (_themeService != null)
        {
            _themeService.ThemeChanged += _ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    LoadFeedsCommand.Execute().Subscribe());
        }
    }

    public async Task LoadFeedsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Feeds.Clear();

        // Only the user's own feeds. (This list used to prepend display-only "default"
        // rows naming specific sites; they were never read by the monitor.)
        if (_dbFactory != null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var userFeeds = await db.RssFeeds
                .OrderBy(f => f.AddedAt)
                .ToListAsync(ct);
            foreach (var f in userFeeds)
                Feeds.Add(new RssFeedRowVm { Feed = f, IsDefault = false });
        }

        IsLoading = false;
    }

    private async Task AddFeedAsync(CancellationToken ct)
    {
        AddFeedError = null;

        if (string.IsNullOrWhiteSpace(NewFeedUrl))
        { AddFeedError = "URL cannot be empty."; return; }

        if (!NewFeedUrl.StartsWith("http://") && !NewFeedUrl.StartsWith("https://"))
        { AddFeedError = "URL must start with http:// or https://"; return; }

        var releases = App.Services?.GetService(typeof(IReleaseProviders)) as IReleaseProviders;
        if (releases?.IsAdultFeed(NewFeedUrl) == true && !(_themeService?.IsSecretMode == true))
        { AddFeedError = "That feed requires secret mode."; return; }

        if (_dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        bool exists = await db.RssFeeds.AnyAsync(f => f.Url == NewFeedUrl, ct);
        if (exists) { AddFeedError = "This feed is already added."; return; }

        var feed = new RssFeed
        {
            Url = NewFeedUrl,
            FeedType = NewFeedType == "Secondary" ? FeedType.Secondary : FeedType.Priority,
            PreferredQuality = NewFeedQuality == "Use Global" ? null : NewFeedQuality,
            IsEnabled = true,
            AddedAt = DateTime.UtcNow
        };
        db.RssFeeds.Add(feed);
        await db.SaveChangesAsync(ct);
        NewFeedUrl = string.Empty;
        await LoadFeedsAsync(ct);
    }

    private async Task ToggleFeedAsync(RssFeedRowVm row, CancellationToken ct)
    {
        if (row.IsDefault || _dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feed = await db.RssFeeds.FindAsync(new object[] { row.Feed.Id }, ct);
        if (feed != null)
        {
            feed.IsEnabled = !feed.IsEnabled;
            await db.SaveChangesAsync(ct);
            row.Feed.IsEnabled = feed.IsEnabled;
            this.RaisePropertyChanged(nameof(Feeds));
        }
    }

    private async Task RemoveFeedAsync(RssFeedRowVm row, CancellationToken ct)
    {
        if (row.IsDefault) return;
        if (_dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feed = await db.RssFeeds.FindAsync(new object[] { row.Feed.Id }, ct);
        if (feed != null)
        {
            db.RssFeeds.Remove(feed);
            await db.SaveChangesAsync(ct);
        }
        Feeds.Remove(row);
    }

    private async Task TestFeedAsync(RssFeedRowVm row, CancellationToken ct)
    {
        if (row.IsDefault || _rssMonitor == null) return;

        IsLoading = true;
        try
        {
            await _rssMonitor.CheckSingleFeedAsync(row.Feed.Id, ct);
            await LoadFeedsAsync(ct); // Refresh health status
        }
        finally
        {
            IsLoading = false;
        }
    }
}
