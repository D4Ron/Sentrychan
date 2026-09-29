using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>What Browse needs from the rest of the app.</summary>
public sealed class MangaBrowseHost
{
    public required IMangaService MangaService { get; init; }
    public required IMangaSourceRegistry Sources { get; init; }
    public ISecretModeService? SecretMode { get; init; }
    public IConfigService? Config { get; init; }

    /// <summary>Opens the preview dialog for a result (set by the view, which owns the window).</summary>
    public Func<MangaResultVm, IMangaSourceService, Task>? Preview { get; set; }

    /// <summary>Opens the reader on a result without adding it (the preview dialog's "Read").</summary>
    public Func<MangaResultVm, IMangaSourceService, Task>? ReadPreview { get; set; }

    /// <summary>Called after a title is added, so the library can refresh.</summary>
    public Action? Added { get; set; }
}

/// <summary>One source in the Browse list: language, adult flag, pinned, when it was last used.</summary>
public sealed class SourceRowVm : ViewModelBase
{
    public SourceRowVm(IMangaSourceService source, bool pinned, DateTime? lastUsed,
        Action<SourceRowVm> open, Action<SourceRowVm> togglePin)
    {
        Source = source;
        _isPinned = pinned;
        LastUsed = lastUsed;
        OpenCommand = ReactiveCommand.Create(() => open(this));
        TogglePinCommand = ReactiveCommand.Create(() => togglePin(this));
    }

    public IMangaSourceService Source { get; }
    public MangaSourceInfo Info => Source.Info;
    public string Name => Info.Name;
    public string Language => Info.Language.ToUpperInvariant();
    public bool IsNsfw => Info.IsNsfw;
    public DateTime? LastUsed { get; set; }

    public string LastUsedText => LastUsed is { } t ? "Last used " + Humanize(t) : string.Empty;

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { this.RaiseAndSetIfChanged(ref _isPinned, value); this.RaisePropertyChanged(nameof(PinGlyph)); }
    }
    public string PinGlyph => IsPinned ? "★" : "☆";

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePinCommand { get; }

    internal static string Humanize(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }
}

/// <summary>
/// Browse: a list of sources to pick from (pinned first, then most recently used), a page per
/// source (Popular / Latest / filtered search), and global search across every source.
/// </summary>
public sealed class MangaBrowseViewModel : ViewModelBase
{
    public const string PinnedKey   = "MangaSources.Pinned";
    public const string LastUsedKey = "MangaSources.LastUsed";

    private readonly MangaBrowseHost _host;
    private readonly bool _novels;
    private HashSet<string> _pinned = [];
    private Dictionary<string, DateTime> _lastUsed = [];

    public MangaBrowseViewModel(MangaBrowseHost host, bool novels)
    {
        _host = host;
        _novels = novels;
        GlobalSearchCommand = ReactiveCommand.CreateFromTask(GlobalSearchAsync);
        BackCommand = ReactiveCommand.Create(() => { CurrentSource = null; ShowGlobal = false; });
    }

    public ObservableCollection<SourceRowVm> Sources { get; } = new();

    private SourceBrowseViewModel? _currentSource;
    /// <summary>The source page being shown, or null for the list.</summary>
    public SourceBrowseViewModel? CurrentSource
    {
        get => _currentSource;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentSource, value);
            this.RaisePropertyChanged(nameof(ShowList));
        }
    }

    private bool _showGlobal;
    public bool ShowGlobal
    {
        get => _showGlobal;
        private set { this.RaiseAndSetIfChanged(ref _showGlobal, value); this.RaisePropertyChanged(nameof(ShowList)); }
    }

    public bool ShowList => CurrentSource == null && !ShowGlobal;
    public bool HasNoSources => Sources.Count == 0;

    private string _globalQuery = string.Empty;
    public string GlobalQuery { get => _globalQuery; set => this.RaiseAndSetIfChanged(ref _globalQuery, value); }

    public ObservableCollection<GlobalSearchGroupVm> GlobalResults { get; } = new();

    public ReactiveCommand<Unit, Unit> GlobalSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }

    private CancellationTokenSource? _globalCts;

    public async Task LoadAsync()
    {
        if (_host.Config != null)
        {
            _pinned = Deserialize<HashSet<string>>(await _host.Config.GetValueAsync(PinnedKey, string.Empty)) ?? [];
            _lastUsed = Deserialize<Dictionary<string, DateTime>>(await _host.Config.GetValueAsync(LastUsedKey, string.Empty)) ?? [];
        }
        RebuildList();
    }

    private IEnumerable<IMangaSourceService> Visible()
    {
        var secret = _host.SecretMode?.IsSecretModeActive ?? false;
        return _host.Sources.Sources.Where(s => s.IsNovel == _novels && (secret || !s.Info.IsNsfw));
    }

    private void RebuildList()
    {
        Sources.Clear();
        foreach (var s in Visible()
                     .OrderByDescending(s => _pinned.Contains(s.Info.Id))
                     .ThenByDescending(s => _lastUsed.TryGetValue(s.Info.Id, out var t) ? t : DateTime.MinValue)
                     .ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase))
        {
            Sources.Add(new SourceRowVm(s, _pinned.Contains(s.Info.Id),
                _lastUsed.TryGetValue(s.Info.Id, out var t) ? t : null, Open, TogglePin));
        }
        this.RaisePropertyChanged(nameof(HasNoSources));
    }

    private void Open(SourceRowVm row)
    {
        _lastUsed[row.Info.Id] = DateTime.UtcNow;
        _ = SaveAsync(LastUsedKey, _lastUsed);
        CurrentSource = new SourceBrowseViewModel(row.Source, _host, () => { CurrentSource = null; RebuildList(); });
        _ = CurrentSource.ShowPopularAsync();
    }

    private void TogglePin(SourceRowVm row)
    {
        if (!_pinned.Remove(row.Info.Id)) _pinned.Add(row.Info.Id);
        _ = SaveAsync(PinnedKey, _pinned);
        RebuildList();
    }

    /// <summary>Searches every source at once, four at a time; each source's results arrive as they come.</summary>
    private async Task GlobalSearchAsync()
    {
        var q = GlobalQuery.Trim();
        if (q.Length < 2) return;
        _globalCts?.Cancel();
        _globalCts = new CancellationTokenSource();
        var ct = _globalCts.Token;

        ShowGlobal = true;
        CurrentSource = null;
        GlobalResults.Clear();
        var library = (await _host.MangaService.GetAllAsync(ct)).Select(m => (m.Source, m.SourceId)).ToHashSet();

        var groups = Visible().Select(s => new GlobalSearchGroupVm(s, _host, library, row =>
        {
            CurrentSource = new SourceBrowseViewModel(row, _host, () => CurrentSource = null) { Query = q };
            ShowGlobal = false;
            _ = CurrentSource.SearchAsync();
        })).ToList();
        foreach (var g in groups) GlobalResults.Add(g);

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(groups.Select(async g =>
        {
            await gate.WaitAsync(ct);
            try { await g.SearchAsync(q, ct); }
            finally { gate.Release(); }
        }));
    }

    private async Task SaveAsync<T>(string key, T value)
    {
        if (_host.Config != null) await _host.Config.SetValueAsync(key, JsonSerializer.Serialize(value));
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); } catch { return null; }
    }
}

/// <summary>One source's row in global search: its first page of results, or why there are none.</summary>
public sealed class GlobalSearchGroupVm : ViewModelBase
{
    private readonly IMangaSourceService _source;
    private readonly MangaBrowseHost _host;
    private readonly HashSet<(string, string)> _library;

    public GlobalSearchGroupVm(IMangaSourceService source, MangaBrowseHost host,
        HashSet<(string, string)> library, Action<IMangaSourceService> openSource)
    {
        _source = source;
        _host = host;
        _library = library;
        OpenSourceCommand = ReactiveCommand.Create(() => openSource(source));
    }

    public string SourceName => _source.Info.Name;
    public string Language => _source.Info.Language.ToUpperInvariant();
    public ObservableCollection<MangaResultVm> Items { get; } = new();
    public ReactiveCommand<Unit, Unit> OpenSourceCommand { get; }

    private string _status = "Searching…";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isBusy = true;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public async Task SearchAsync(string query, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var page = await _source.SearchAsync(query, 1, FilterList.Empty, timeout.Token);
            foreach (var r in page.Items.Take(12))
                Items.Add(SourceBrowseViewModel.ResultVm(r, _source, _host, _library));
            Status = Items.Count == 0 ? "No results" : string.Empty;
        }
        catch (OperationCanceledException) { Status = ct.IsCancellationRequested ? string.Empty : "Timed out"; }
        catch (Exception ex) { Status = $"Failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}

/// <summary>One source's page: Popular, Latest, or a search with the source's filters.</summary>
public sealed class SourceBrowseViewModel : ViewModelBase
{
    private enum Mode { Popular, Latest, Search }

    private readonly IMangaSourceService _source;
    private readonly MangaBrowseHost _host;
    private Mode _mode;
    private int _page;
    private CancellationTokenSource? _cts;
    private HashSet<(string, string)> _library = [];

    public SourceBrowseViewModel(IMangaSourceService source, MangaBrowseHost host, Action back)
    {
        _source = source;
        _host = host;
        FilterSheet = new FilterSheetVm(SafeFilters(source));
        BackCommand = ReactiveCommand.Create(back);
        PopularCommand = ReactiveCommand.CreateFromTask(ShowPopularAsync);
        LatestCommand = ReactiveCommand.CreateFromTask(ShowLatestAsync);
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync);
        ToggleFiltersCommand = ReactiveCommand.Create(() => { ShowFilters = !ShowFilters; });
        ApplyFiltersCommand = ReactiveCommand.CreateFromTask(async () => { ShowFilters = false; await SearchAsync(); });
    }

    // A pack's broken filter list shouldn't take the page down with it.
    private static FilterList SafeFilters(IMangaSourceService source)
    {
        try { return source.GetFilterList(); } catch { return FilterList.Empty; }
    }

    public string SourceName => _source.Info.Name;
    public bool SupportsLatest => _source.Info.SupportsLatest;
    public bool HasFilters => !FilterSheet.IsEmpty;
    public FilterSheetVm FilterSheet { get; }

    public ObservableCollection<MangaResultVm> Results { get; } = new();

    private string _query = string.Empty;
    public string Query { get => _query; set => this.RaiseAndSetIfChanged(ref _query, value); }

    private bool _showFilters;
    public bool ShowFilters { get => _showFilters; set => this.RaiseAndSetIfChanged(ref _showFilters, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private bool _hasNextPage;
    public bool HasNextPage { get => _hasNextPage; private set => this.RaiseAndSetIfChanged(ref _hasNextPage, value); }

    private string _status = string.Empty;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public bool IsPopular => _mode == Mode.Popular;
    public bool IsLatest => _mode == Mode.Latest;
    public bool IsSearch => _mode == Mode.Search;

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> PopularCommand { get; }
    public ReactiveCommand<Unit, Unit> LatestCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFiltersCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyFiltersCommand { get; }

    public Task ShowPopularAsync() => StartAsync(Mode.Popular);
    public Task ShowLatestAsync() => StartAsync(Mode.Latest);
    public Task SearchAsync() => StartAsync(Mode.Search);

    private async Task StartAsync(Mode mode)
    {
        _mode = mode;
        foreach (var p in new[] { nameof(IsPopular), nameof(IsLatest), nameof(IsSearch) }) this.RaisePropertyChanged(p);
        _page = 0;
        Results.Clear();
        HasNextPage = false;
        _library = (await _host.MangaService.GetAllAsync()).Select(m => (m.Source, m.SourceId)).ToHashSet();
        await LoadMoreAsync();
    }

    public async Task LoadMoreAsync()
    {
        if (IsBusy) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        Status = string.Empty;
        try
        {
            var next = _page + 1;
            var page = _mode switch
            {
                Mode.Latest => await _source.GetLatestAsync(next, ct),
                Mode.Search => await _source.SearchAsync(Query.Trim(), next, FilterSheet.Filters, ct),
                _           => await _source.GetPopularAsync(next, ct),
            };
            if (ct.IsCancellationRequested) return;
            _page = next;
            var seen = Results.Select(r => r.Result.SourceId).ToHashSet();
            foreach (var r in page.Items.Where(r => seen.Add(r.SourceId)))
                Results.Add(ResultVm(r, _source, _host, _library));
            HasNextPage = page.HasNextPage && page.Items.Count > 0;
            if (Results.Count == 0)
                Status = _mode == Mode.Search ? "Nothing matches." : "This source has nothing to list here.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"The source failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>A result card that adds to the library and opens the preview — shared with global search.</summary>
    internal static MangaResultVm ResultVm(MangaSearchResult r, IMangaSourceService source, MangaBrowseHost host,
        HashSet<(string, string)> library) =>
        new(r, source.SourceName,
            async item =>
            {
                if (item.IsInLibrary || item.IsAdding) return;
                item.IsAdding = true;
                try
                {
                    await host.MangaService.AddAsync(item.ToManga(source));
                    item.IsInLibrary = true;
                    host.Added?.Invoke();
                }
                catch (Exception) { /* the card stays addable; nothing to crash over */ }
                finally { item.IsAdding = false; }
            },
            item => { if (host.Preview != null) _ = host.Preview(item, source); },
            library.Contains((source.SourceName, r.SourceId)));
}
