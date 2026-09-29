using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// The Mihon-style title page's extras on the shared title view-model: chapter filters and
/// sort, bookmarks, multi-select with bulk actions, "download next", "mark previous as read"
/// and migration. The classic page doesn't bind any of it.
/// </summary>
public partial class MangaDetailViewModel
{
    private readonly MangaLibraryService? _library;
    private HashSet<int> _bookmarks = [];

    /// <summary>Chapters after the filters and sort — what the Mihon page lists.</summary>
    public ObservableCollection<MangaChapterVm> VisibleChapters { get; } = [];

    public bool HasLibraryFeatures => _library != null;

    private void InitMihon()
    {
        CycleChapterFilterCommand = ReactiveCommand.Create<string>(CycleChapterFilter);
        SetChapterSortCommand = ReactiveCommand.Create<string>(SetChapterSort);
        SelectAllCommand = ReactiveCommand.Create(() => SetSelection(VisibleChapters, true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetSelection(Chapters, false));
        InvertSelectionCommand = ReactiveCommand.Create(() => { foreach (var c in VisibleChapters) c.IsSelected = !c.IsSelected; });
        SelectRangeCommand = ReactiveCommand.Create(SelectRange);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(() => DownloadAsync(Selected()));
        MarkSelectedReadCommand = ReactiveCommand.CreateFromTask(() => SetReadAsync(Selected(), true));
        MarkSelectedUnreadCommand = ReactiveCommand.CreateFromTask(() => SetReadAsync(Selected(), false));
        BookmarkSelectedCommand = ReactiveCommand.CreateFromTask(() => SetBookmarkedAsync(Selected(), true));
        UnbookmarkSelectedCommand = ReactiveCommand.CreateFromTask(() => SetBookmarkedAsync(Selected(), false));
        DeleteSelectedDownloadsCommand = ReactiveCommand.CreateFromTask(DeleteSelectedDownloadsAsync);
        MarkPreviousReadCommand = ReactiveCommand.CreateFromTask<MangaChapterVm>(MarkPreviousReadAsync);
        ToggleBookmarkCommand = ReactiveCommand.CreateFromTask<MangaChapterVm>(c => SetBookmarkedAsync([c], !c.IsBookmarked));
        DownloadNextCommand = ReactiveCommand.CreateFromTask<string>(DownloadNextAsync);
    }

    // ── Filters and sort ─────────────────────────────────────────────

    private ChapterFilters _chapterFilters = new();
    private ChapterSort _chapterSort = ChapterSort.SourceOrder;
    private bool _sortDescending = true;

    public string UnreadFilterGlyph => Glyph(_chapterFilters.Unread);
    public string DownloadedFilterGlyph => Glyph(_chapterFilters.Downloaded);
    public string BookmarkedFilterGlyph => Glyph(_chapterFilters.Bookmarked);
    public bool ChapterFiltersActive => _chapterFilters != new ChapterFilters();
    public string ChapterSortLabel => (_chapterSort switch
    {
        ChapterSort.Number => "Chapter number",
        ChapterSort.UploadDate => "Upload date",
        _ => "Source order",
    }) + (_sortDescending ? " ↓" : " ↑");

    private static string Glyph(TriState s) => s switch { TriState.Include => "✓", TriState.Exclude => "✕", _ => " " };
    private static TriState Next(TriState s) => s switch
    {
        TriState.Ignore => TriState.Include,
        TriState.Include => TriState.Exclude,
        _ => TriState.Ignore,
    };

    public ReactiveCommand<string, Unit> CycleChapterFilterCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SetChapterSortCommand { get; private set; } = null!;

    private void CycleChapterFilter(string which)
    {
        _chapterFilters = which switch
        {
            "Unread"     => _chapterFilters with { Unread = Next(_chapterFilters.Unread) },
            "Downloaded" => _chapterFilters with { Downloaded = Next(_chapterFilters.Downloaded) },
            "Bookmarked" => _chapterFilters with { Bookmarked = Next(_chapterFilters.Bookmarked) },
            _ => _chapterFilters,
        };
        foreach (var p in new[] { nameof(UnreadFilterGlyph), nameof(DownloadedFilterGlyph), nameof(BookmarkedFilterGlyph), nameof(ChapterFiltersActive) })
            this.RaisePropertyChanged(p);
        RebuildVisible();
    }

    private void SetChapterSort(string name)
    {
        if (!Enum.TryParse<ChapterSort>(name, out var sort)) return;
        if (sort == _chapterSort) _sortDescending = !_sortDescending;
        else { _chapterSort = sort; _sortDescending = true; }
        this.RaisePropertyChanged(nameof(ChapterSortLabel));
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        var order = ChapterQuery.Apply(Manga, Chapters.Select(c => c.Chapter), _chapterFilters, _chapterSort, _sortDescending, _bookmarks);
        VisibleChapters.Clear();
        foreach (var c in order)
            if (_byId.TryGetValue(c.Id, out var vm)) VisibleChapters.Add(vm);
        OnChapterSelectionChanged();
    }

    // ── Selection ────────────────────────────────────────────────────

    public int SelectedChapterCount => Chapters.Count(c => c.IsSelected);
    public bool InChapterSelection => SelectedChapterCount > 0;
    public string ChapterSelectionText => $"{SelectedChapterCount} selected";

    private void OnChapterSelectionChanged()
    {
        this.RaisePropertyChanged(nameof(SelectedChapterCount));
        this.RaisePropertyChanged(nameof(InChapterSelection));
        this.RaisePropertyChanged(nameof(ChapterSelectionText));
    }

    private List<MangaChapterVm> Selected() => Chapters.Where(c => c.IsSelected).ToList();

    private static void SetSelection(IEnumerable<MangaChapterVm> rows, bool on)
    {
        foreach (var c in rows.ToList()) c.IsSelected = on;
    }

    /// <summary>Selects every visible chapter between the first and last already selected, as Mihon's range select.</summary>
    private void SelectRange()
    {
        var idx = VisibleChapters.Select((c, i) => (c, i)).Where(x => x.c.IsSelected).Select(x => x.i).ToList();
        if (idx.Count < 1) return;
        for (var i = idx.Min(); i <= idx.Max(); i++) VisibleChapters[i].IsSelected = true;
    }

    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectNoneCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> InvertSelectionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectRangeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DownloadSelectedCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MarkSelectedReadCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MarkSelectedUnreadCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> BookmarkSelectedCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> UnbookmarkSelectedCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteSelectedDownloadsCommand { get; private set; } = null!;
    public ReactiveCommand<MangaChapterVm, Unit> MarkPreviousReadCommand { get; private set; } = null!;
    public ReactiveCommand<MangaChapterVm, Unit> ToggleBookmarkCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> DownloadNextCommand { get; private set; } = null!;

    // ── Actions ──────────────────────────────────────────────────────

    private async Task DownloadAsync(IEnumerable<MangaChapterVm> rows)
    {
        var todo = rows.Where(c => !c.IsDownloaded && !c.IsBusy).OrderBy(c => c.Sort ?? double.MaxValue).ToList();
        SetSelection(Chapters, false);
        if (todo.Count == 0) { StatusMessage = "Those chapters are already downloaded."; return; }
        await Task.WhenAll(todo.Select(DownloadChapterAsync));
    }

    /// <summary>"Download next 1 / 5 / 10 / unread / all".</summary>
    private Task DownloadNextAsync(string which)
    {
        if (which == "all") return DownloadAllAsync();
        int? count = int.TryParse(which, out var n) ? n : null;
        var next = ChapterQuery.NextToDownload(Manga, Chapters.Select(c => c.Chapter), count)
            .Select(c => _byId.GetValueOrDefault(c.Id)).OfType<MangaChapterVm>();
        return DownloadAsync(next);
    }

    private async Task SetReadAsync(IReadOnlyList<MangaChapterVm> rows, bool read)
    {
        if (_library == null || rows.Count == 0) return;
        await _library.SetReadAsync(Manga.Id, rows.Select(r => r.Chapter.Id), read);
        await ReloadFromDatabaseAsync();
    }

    private async Task MarkPreviousReadAsync(MangaChapterVm row)
    {
        if (_library == null) return;
        await _library.MarkPreviousReadAsync(row.Chapter.Id);
        await ReloadFromDatabaseAsync();
    }

    private async Task SetBookmarkedAsync(IReadOnlyList<MangaChapterVm> rows, bool on)
    {
        if (_library == null || rows.Count == 0) return;
        await _library.SetBookmarkedAsync(rows.Select(r => r.Chapter.Id), on);
        foreach (var r in rows)
        {
            r.IsBookmarked = on;
            if (on) _bookmarks.Add(r.Chapter.Id); else _bookmarks.Remove(r.Chapter.Id);
        }
        SetSelection(Chapters, false);
        RebuildVisible();
    }

    private async Task DeleteSelectedDownloadsAsync()
    {
        foreach (var r in Selected().Where(r => r.IsDownloaded)) await RemoveDownloadAsync(r);
        SetSelection(Chapters, false);
    }

    /// <summary>Re-reads chapters and progress after a bulk change, without asking the source.</summary>
    private async Task ReloadFromDatabaseAsync()
    {
        var stored = await _mangaService.GetByIdAsync(Manga.Id);
        if (stored == null) return;
        Manga = stored;
        PopulateFrom(stored.Chapters);
        UpdateProgressText();
    }

    // ── Categories and migration (dialogs are opened by the view) ────

    public MangaLibraryService? Library => _library;
    public IMangaSourceService Source => _source;

    /// <summary>Moves this title to another source, then reloads it from there.</summary>
    public async Task MigrateAsync(IMangaSourceService target, MangaSearchResult result)
    {
        if (_library == null) return;
        IsLoading = true;
        StatusMessage = $"Moving to {target.SourceName}…";
        try
        {
            var details = await target.GetDetailsAsync(result.SourceId) ?? result;
            var chapters = await target.GetChaptersAsync(result.SourceId, "en");
            Manga = await _library.MigrateAsync(Manga.Id, target, details, chapters);
            MigratedTo?.Invoke(Manga);
        }
        catch (Exception ex) { StatusMessage = $"Couldn't migrate: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>Raised after a migration; the host reopens the title on its new source.</summary>
    public Action<Manga>? MigratedTo { get; set; }
}
