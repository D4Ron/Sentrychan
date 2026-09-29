using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>A search result offered as a migration target.</summary>
public sealed class MigrateCandidateVm(MangaSearchResult result, string sourceName)
{
    public MangaSearchResult Result { get; } = result;
    public string Title => Result.Title;
    public string CoverUrl => Result.CoverUrl;
    public string SourceName { get; } = sourceName;
    public string Meta => string.Join("  ·  ", new[]
    {
        Result.Year?.ToString(), Result.Status, Result.LastChapter is { } c ? $"{c:0.#} ch" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// "Migrate": pick another source, search it (the title is pre-filled), pick the match. The
/// title page then moves the library entry across (MangaLibraryService.MigrateAsync).
/// </summary>
public sealed class MigrateViewModel : ViewModelBase
{
    private readonly IMangaSourceRegistry _sources;
    private CancellationTokenSource? _cts;

    public MigrateViewModel(IMangaSourceRegistry sources, string currentSource, bool novels, bool includeAdult, string query)
    {
        _sources = sources;
        SourceNames = sources.Sources
            .Where(s => s.IsNovel == novels && (includeAdult || !s.Info.IsNsfw) && s.SourceName != currentSource)
            .Select(s => s.SourceName).ToArray();
        _selectedSource = SourceNames.FirstOrDefault();
        _query = query;
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
    }

    public string[] SourceNames { get; }
    public bool HasSources => SourceNames.Length > 0;

    private string? _selectedSource;
    public string? SelectedSource { get => _selectedSource; set => this.RaiseAndSetIfChanged(ref _selectedSource, value); }

    private string _query;
    public string Query { get => _query; set => this.RaiseAndSetIfChanged(ref _query, value); }

    public ObservableCollection<MigrateCandidateVm> Results { get; } = new();

    private MigrateCandidateVm? _selected;
    public MigrateCandidateVm? Selected
    {
        get => _selected;
        set { this.RaiseAndSetIfChanged(ref _selected, value); this.RaisePropertyChanged(nameof(CanConfirm)); }
    }
    public bool CanConfirm => Selected != null;

    private string _status = string.Empty;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }

    public IMangaSourceService? TargetSource => SelectedSource == null ? null : _sources.Find(SelectedSource);

    private async Task SearchAsync()
    {
        var source = TargetSource;
        if (source == null || string.IsNullOrWhiteSpace(Query)) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Results.Clear();
        Selected = null;
        Status = $"Searching {source.SourceName}…";
        try
        {
            var page = await source.SearchAsync(Query.Trim(), 1, FilterList.Empty, _cts.Token);
            foreach (var r in page.Items) Results.Add(new MigrateCandidateVm(r, source.SourceName));
            Status = Results.Count == 0 ? "No matches on this source." : string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"Search failed: {ex.Message}"; }
    }
}
