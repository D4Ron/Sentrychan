using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Unit = System.Reactive.Unit;

namespace Sentrychan.UI.ViewModels;

public class AddFeedViewModel : ViewModelBase
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private string _statusMessage = string.Empty;

    private string _feedUrl = string.Empty;
    public string FeedUrl
    {
        get => _feedUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _feedUrl, value);
            this.RaisePropertyChanged(nameof(CanAdd));
        }
    }

    private bool _isPriority = true;
    public bool IsPriority
    {
        get => _isPriority;
        set => this.RaiseAndSetIfChanged(ref _isPriority, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private string _preferredQuality = "Global Default";
    public string PreferredQuality
    {
        get => _preferredQuality;
        set => this.RaiseAndSetIfChanged(ref _preferredQuality, value);
    }

    public string[] QualityOptions { get; } = ["Global Default", "1080p", "720p", "480p", "Any"];

    public bool CanAdd => !string.IsNullOrWhiteSpace(FeedUrl)
                       && (FeedUrl.StartsWith("http://") || FeedUrl.StartsWith("https://"));

    private bool _added;
    public bool Added
    {
        get => _added;
        private set => this.RaiseAndSetIfChanged(ref _added, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    // Design-time
    public AddFeedViewModel()
    {
        _dbFactory = null;
        AddCommand = ReactiveCommand.CreateFromTask(async ct => await AddFeedAsync(ct));
    }

    // Runtime
    public AddFeedViewModel(IDbContextFactory<AppDbContext> dbFactory) : this()
    {
        _dbFactory = dbFactory;
    }

    private async Task AddFeedAsync(CancellationToken ct)
    {
        if (_dbFactory == null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var exists = await db.RssFeeds
                .AnyAsync(f => f.Url == FeedUrl, ct);

            if (exists)
            {
                StatusMessage = "Feed already exists";
                return;
            }

            db.RssFeeds.Add(new RssFeed
            {
                Url = FeedUrl.Trim(),
                FeedType = IsPriority ? FeedType.Priority : FeedType.Secondary,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow,
                PreferredQuality = PreferredQuality == "Global Default" ? null : PreferredQuality
            });

            await db.SaveChangesAsync(ct);
            Added = true;

            // Signal dialog to close by setting status then closing
            StatusMessage = "Feed added successfully";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
        }
    }
}