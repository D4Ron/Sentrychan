using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Models;
using Sentrychan.Core.Sources;
using Sentrychan.UI.Services;
using System;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>Where the sources guide sends the user when it closes.</summary>
public enum SourcesGuideExit { Close, FeedSettings, SourceSettings }

/// <summary>
/// "Add your sources": shown at launch while the app has none (no feeds, no source packs, Mihon
/// extensions off), and from Settings → Sources. The app ships with no sources, so a new user
/// otherwise meets an app that silently finds nothing. Names no site — it explains what to look for.
/// </summary>
public sealed class SourcesGuideViewModel : ViewModelBase
{
    /// <summary>AppConfig key: the user asked not to see the guide at launch again.</summary>
    public const string DismissedKey = "SourcesGuideDismissed";

    private static readonly HttpClient Http = CreateHttp();
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Func<Window?> _owner;

    public SourcesGuideViewModel(IDbContextFactory<AppDbContext> dbFactory, Func<Window?> owner)
    {
        _dbFactory = dbFactory;
        _owner = owner;
        var idle = this.WhenAnyValue(x => x.IsBusy, b => !b);
        ImportFileCommand   = ReactiveCommand.CreateFromTask(() => PickAndImportAsync(folder: false), idle);
        ImportFolderCommand = ReactiveCommand.CreateFromTask(() => PickAndImportAsync(folder: true), idle);
        CheckFeedCommand    = ReactiveCommand.CreateFromTask(async () => { await CheckFeedAsync(); }, idle);
        AddFeedCommand      = ReactiveCommand.CreateFromTask(AddFeedAsync, idle);
    }

    public ReactiveCommand<Unit, Unit> ImportFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckFeedCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFeedCommand { get; }

    /// <summary>The check shown under the import once something was imported.</summary>
    public SourcesCheckViewModel Check { get; } = new();

    private bool _showCheck;
    public bool ShowCheck { get => _showCheck; private set => this.RaiseAndSetIfChanged(ref _showCheck, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private string _have = string.Empty;
    /// <summary>What the app has right now, so each step's effect is visible.</summary>
    public string Have { get => _have; private set => this.RaiseAndSetIfChanged(ref _have, value); }

    private string? _fileMessage;
    public string? FileMessage { get => _fileMessage; private set => this.RaiseAndSetIfChanged(ref _fileMessage, value); }

    private string _feedUrl = string.Empty;
    public string FeedUrl
    {
        get => _feedUrl;
        set { this.RaiseAndSetIfChanged(ref _feedUrl, value); FeedMessage = null; FeedOk = null; }
    }

    private string? _feedMessage;
    public string? FeedMessage { get => _feedMessage; private set => this.RaiseAndSetIfChanged(ref _feedMessage, value); }

    private bool? _feedOk;
    /// <summary>The last check's verdict: true usable, false not, null not checked.</summary>
    public bool? FeedOk
    {
        get => _feedOk;
        private set
        {
            this.RaiseAndSetIfChanged(ref _feedOk, value);
            this.RaisePropertyChanged(nameof(FeedGood));
            this.RaisePropertyChanged(nameof(FeedBad));
        }
    }
    public bool FeedGood => FeedOk == true;
    public bool FeedBad => FeedOk == false;

    private bool _dontShowAgain;
    public bool DontShowAgain { get => _dontShowAgain; set => this.RaiseAndSetIfChanged(ref _dontShowAgain, value); }

    // ── What counts as "has sources" ────────────────────────────────

    /// <summary>
    /// True when the app has something to find content with: an RSS feed, an installed source pack,
    /// or Mihon extensions turned on. The guide shows at launch only when all three are missing.
    /// </summary>
    public static async Task<bool> HasAnySourcesAsync(IDbContextFactory<AppDbContext> dbFactory)
    {
        if (SourcesTransferService.InstalledPacks().Count > 0) return true;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.RssFeeds.AnyAsync()) return true;
        var bridge = App.Services?.GetService<MihonBridgeService>();
        return bridge != null && await bridge.IsEnabledAsync();
    }

    public async Task RefreshAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var feeds = await db.RssFeeds.CountAsync();
        var packs = SourcesTransferService.InstalledPacks().Count;
        var bridge = App.Services?.GetService<MihonBridgeService>();
        var mihon = bridge != null && await bridge.IsEnabledAsync();
        Have = "Right now: " + string.Join(" · ",
            feeds switch { 0 => "no RSS feeds", 1 => "1 RSS feed", _ => $"{feeds} RSS feeds" },
            packs switch { 0 => "no source packs", 1 => "1 source pack", _ => $"{packs} source packs" },
            mihon ? "Mihon extensions on" : "Mihon extensions off");
    }

    // ── 1. A sources file or pack ───────────────────────────────────

    private async Task PickAndImportAsync(bool folder)
    {
        if (_owner() is not { } owner) return;
        if (await SourcesFileActions.PickAsync(owner, folder) is { } paths) await ImportAsync(paths);
    }

    /// <summary>Imports picked or dropped paths, then checks — the check is how a user knows it took.</summary>
    public async Task ImportAsync(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        IsBusy = true;
        try
        {
            var result = await SourcesFileActions.ImportPathsAsync(paths);
            FileMessage = result.Summary();
        }
        catch (Exception ex) { FileMessage = "That couldn't be imported: " + ex.Message; }
        finally { IsBusy = false; await RefreshAsync(); }
        ShowCheck = true;
        await Check.RunAsync(live: true);
    }

    // ── 2. An RSS feed ──────────────────────────────────────────────

    private async Task<FeedCheck?> CheckFeedAsync()
    {
        if (string.IsNullOrWhiteSpace(FeedUrl)) { FeedMessage = "Paste a feed link first."; FeedOk = false; return null; }
        IsBusy = true;
        FeedMessage = "Checking…";
        try
        {
            var check = await RssFeedProbe.CheckAsync(Http, FeedUrl);
            FeedMessage = check.Message;
            FeedOk = check.IsUsable;
            return check;
        }
        finally { IsBusy = false; }
    }

    private async Task AddFeedAsync()
    {
        var check = await CheckFeedAsync();
        if (check is not { IsUsable: true }) return;

        var url = FeedUrl.Trim();
        var releases = App.Services?.GetService<IReleaseProviders>();
        var secret = App.Services?.GetService<ISecretModeService>();
        if (releases?.IsAdultFeed(url) == true && secret?.IsSecretModeActive != true)
        {
            FeedMessage = "That feed needs secret mode."; FeedOk = false;
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.RssFeeds.AnyAsync(f => f.Url == url))
        {
            FeedMessage = "You already have that feed.";
            return;
        }
        db.RssFeeds.Add(new RssFeed { Url = url, FeedType = FeedType.Priority, IsEnabled = true, AddedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        _feedUrl = string.Empty;
        this.RaisePropertyChanged(nameof(FeedUrl));
        FeedMessage = check.Message + " Added — monitoring checks it from now on, and you can add more.";
        await RefreshAsync();
    }

    /// <summary>Remembers "don't show at launch" if ticked. Called as the dialog closes.</summary>
    public async Task SaveDismissalAsync()
    {
        if (!DontShowAgain) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.AppConfigs.AnyAsync(c => c.Key == DismissedKey))
            db.AppConfigs.Add(new AppConfig { Key = DismissedKey, Value = "true" });
        await db.SaveChangesAsync();
    }

    public static async Task<bool> IsDismissedAsync(IDbContextFactory<AppDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AppConfigs.AnyAsync(c => c.Key == DismissedKey && c.Value == "true");
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{BuildInfo.AppName.Replace(' ', '-')}/1.0");
        return http;
    }
}
