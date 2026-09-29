using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels.Mihon;

public enum LibraryDisplayMode { ComfortableGrid, CompactGrid, List, CoverOnly }

/// <summary>A title on the library shelf.</summary>
public sealed class LibraryItemVm : ViewModelBase
{
    private readonly MihonLibraryViewModel _owner;

    public LibraryItemVm(LibraryEntry entry, MihonLibraryViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        OpenCommand = ReactiveCommand.Create(() => _owner.Activate(this));
        ToggleSelectCommand = ReactiveCommand.Create(() => { IsSelected = !IsSelected; });
    }

    public LibraryEntry Entry { get; }
    public Manga Manga => Entry.Manga;
    public string Title => Manga.Title;
    public string CoverPath => Manga.CoverPath;
    public string Source => Manga.Source;

    public bool ShowUnread => _owner.ShowUnreadBadge && Entry.UnreadCount > 0;
    public bool ShowDownloaded => _owner.ShowDownloadedBadge && Entry.DownloadedCount > 0;
    public string UnreadText => Entry.UnreadCount > 999 ? "999+" : Entry.UnreadCount.ToString();
    public string DownloadedText => Entry.DownloadedCount.ToString();

    public string Detail => Entry.ChapterCount == 0 ? Manga.Source
        : Entry.UnreadCount == 0 ? $"{Manga.Source} · all read"
        : $"{Manga.Source} · {Entry.UnreadCount} unread of {Entry.ChapterCount}";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { this.RaiseAndSetIfChanged(ref _isSelected, value); _owner.SelectionChanged(); }
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSelectCommand { get; }

    internal void RefreshBadges()
    {
        this.RaisePropertyChanged(nameof(ShowUnread));
        this.RaisePropertyChanged(nameof(ShowDownloaded));
    }
}

/// <summary>A category tab: a user category, or Default for titles on none.</summary>
public sealed class CategoryTabVm(int id, string name, int count)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Header => $"{Name} ({Count})";
}

/// <summary>A category in the "set categories" and "edit categories" lists.</summary>
public sealed class CategoryChoiceVm : ViewModelBase
{
    public CategoryChoiceVm(MangaCategory category, bool isChecked)
    {
        Category = category;
        _isChecked = isChecked;
        _name = category.Name;
    }

    public MangaCategory Category { get; }

    private string _name;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => this.RaiseAndSetIfChanged(ref _isChecked, value); }
}

/// <summary>
/// The Mihon-style library: category tabs, four display modes, include/exclude filters,
/// sorts, unread/downloaded badges, and multi-select for bulk actions.
/// </summary>
public sealed class MihonLibraryViewModel : ViewModelBase
{
    private const string SettingsKey = "MangaLibrary.View";

    private readonly MangaLibraryService _library;
    private readonly IMangaService _mangaService;
    private readonly ISecretModeService? _secretMode;
    private readonly IConfigService? _config;
    private readonly bool _novels;
    private readonly Action<Manga> _open;
    private List<LibraryEntry> _entries = [];
    private List<MangaCategory> _categories = [];
    private bool _loadingSettings;

    public MihonLibraryViewModel(MangaLibraryService library, IMangaService mangaService, ISecretModeService? secretMode,
        IConfigService? config, bool novels, Action<Manga> open)
    {
        _library = library;
        _mangaService = mangaService;
        _secretMode = secretMode;
        _config = config;
        _novels = novels;
        _open = open;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        ClearSelectionCommand = ReactiveCommand.Create(() => { foreach (var i in Items) i.IsSelected = false; });
        SelectAllCommand = ReactiveCommand.Create(() => { foreach (var i in Items) i.IsSelected = true; });
        MarkSelectedReadCommand = ReactiveCommand.CreateFromTask(() => MarkSelectedAsync(true));
        MarkSelectedUnreadCommand = ReactiveCommand.CreateFromTask(() => MarkSelectedAsync(false));
        RemoveSelectedCommand = ReactiveCommand.CreateFromTask(RemoveSelectedAsync);
        CycleFilterCommand = ReactiveCommand.Create<string>(CycleFilter);
        SetSortCommand = ReactiveCommand.Create<string>(SetSort);
        ResetFiltersCommand = ReactiveCommand.Create(() => { Filters = new LibraryFilters(); });
    }

    public ObservableCollection<CategoryTabVm> Tabs { get; } = new();
    public ObservableCollection<LibraryItemVm> Items { get; } = new();

    private CategoryTabVm? _selectedTab;
    public CategoryTabVm? SelectedTab
    {
        get => _selectedTab;
        set { this.RaiseAndSetIfChanged(ref _selectedTab, value); Rebuild(); }
    }

    public bool ShowTabs => Tabs.Count > 1;
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText => _entries.Count == 0
        ? "Your library is empty. Add titles from Browse."
        : "Nothing here matches the filters.";

    private string _search = string.Empty;
    public string Search { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value); Rebuild(); } }

    // ── Display ──────────────────────────────────────────────────────

    public string[] DisplayModes { get; } = ["Comfortable grid", "Compact grid", "List", "Cover only"];

    private LibraryDisplayMode _displayMode = LibraryDisplayMode.ComfortableGrid;
    public LibraryDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _displayMode, value);
            foreach (var p in new[] { nameof(IsComfortable), nameof(IsCompact), nameof(IsList), nameof(IsCoverOnly), nameof(DisplayModeLabel) })
                this.RaisePropertyChanged(p);
            SaveSettings();
        }
    }

    public string DisplayModeLabel
    {
        get => DisplayModes[(int)DisplayMode];
        set { var i = Array.IndexOf(DisplayModes, value); if (i >= 0) DisplayMode = (LibraryDisplayMode)i; }
    }

    public bool IsComfortable => DisplayMode == LibraryDisplayMode.ComfortableGrid;
    public bool IsCompact => DisplayMode == LibraryDisplayMode.CompactGrid;
    public bool IsList => DisplayMode == LibraryDisplayMode.List;
    public bool IsCoverOnly => DisplayMode == LibraryDisplayMode.CoverOnly;

    private bool _showUnreadBadge = true;
    public bool ShowUnreadBadge
    {
        get => _showUnreadBadge;
        set { this.RaiseAndSetIfChanged(ref _showUnreadBadge, value); foreach (var i in Items) i.RefreshBadges(); SaveSettings(); }
    }

    private bool _showDownloadedBadge = true;
    public bool ShowDownloadedBadge
    {
        get => _showDownloadedBadge;
        set { this.RaiseAndSetIfChanged(ref _showDownloadedBadge, value); foreach (var i in Items) i.RefreshBadges(); SaveSettings(); }
    }

    // ── Filters and sort ─────────────────────────────────────────────

    private LibraryFilters _filters = new();
    public LibraryFilters Filters
    {
        get => _filters;
        set
        {
            this.RaiseAndSetIfChanged(ref _filters, value);
            foreach (var p in new[] { nameof(DownloadedGlyph), nameof(UnreadGlyph), nameof(StartedGlyph), nameof(CompletedGlyph), nameof(FiltersActive) })
                this.RaisePropertyChanged(p);
            Rebuild();
            SaveSettings();
        }
    }

    public bool FiltersActive => Filters.IsActive;
    public string DownloadedGlyph => Glyph(Filters.Downloaded);
    public string UnreadGlyph => Glyph(Filters.Unread);
    public string StartedGlyph => Glyph(Filters.Started);
    public string CompletedGlyph => Glyph(Filters.Completed);
    private static string Glyph(TriState s) => s switch { TriState.Include => "✓", TriState.Exclude => "✕", _ => " " };

    private static TriState Next(TriState s) => s switch
    {
        TriState.Ignore => TriState.Include,
        TriState.Include => TriState.Exclude,
        _ => TriState.Ignore,
    };

    private void CycleFilter(string which) => Filters = which switch
    {
        "Downloaded" => Filters with { Downloaded = Next(Filters.Downloaded) },
        "Unread"     => Filters with { Unread = Next(Filters.Unread) },
        "Started"    => Filters with { Started = Next(Filters.Started) },
        "Completed"  => Filters with { Completed = Next(Filters.Completed) },
        _ => Filters,
    };

    private LibrarySort _sort = LibrarySort.Title;
    private bool _ascending = true;

    public string SortLabel => (_sort switch
    {
        LibrarySort.LastRead => "Last read",
        LibrarySort.LatestChapter => "Latest chapter",
        LibrarySort.UnreadCount => "Unread count",
        LibrarySort.DateAdded => "Date added",
        _ => "Title",
    }) + (_ascending ? " ↑" : " ↓");

    /// <summary>Picks a sort; picking the current one flips its direction.</summary>
    private void SetSort(string name)
    {
        if (!Enum.TryParse<LibrarySort>(name, out var sort)) return;
        if (sort == _sort) _ascending = !_ascending;
        else
        {
            _sort = sort;
            // Title reads A→Z; the others are most useful biggest/newest first.
            _ascending = sort == LibrarySort.Title;
        }
        this.RaisePropertyChanged(nameof(SortLabel));
        Rebuild();
        SaveSettings();
    }

    // ── Selection ────────────────────────────────────────────────────

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool InSelection => SelectedCount > 0;
    public string SelectionText => $"{SelectedCount} selected";

    internal void SelectionChanged()
    {
        this.RaisePropertyChanged(nameof(SelectedCount));
        this.RaisePropertyChanged(nameof(InSelection));
        this.RaisePropertyChanged(nameof(SelectionText));
    }

    /// <summary>A click opens the title — or, while selecting, toggles it.</summary>
    internal void Activate(LibraryItemVm item)
    {
        if (InSelection) item.IsSelected = !item.IsSelected;
        else _open(item.Manga);
    }

    public IReadOnlyList<Manga> SelectedManga => Items.Where(i => i.IsSelected).Select(i => i.Manga).ToList();

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MarkSelectedReadCommand { get; }
    public ReactiveCommand<Unit, Unit> MarkSelectedUnreadCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }
    public ReactiveCommand<string, Unit> CycleFilterCommand { get; }
    public ReactiveCommand<string, Unit> SetSortCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetFiltersCommand { get; }

    private async Task MarkSelectedAsync(bool read)
    {
        foreach (var m in SelectedManga)
        {
            var full = await _mangaService.GetByIdAsync(m.Id);
            if (full != null) await _library.SetReadAsync(m.Id, full.Chapters.Select(c => c.Id), read);
        }
        await LoadAsync();
    }

    private bool _confirmingRemove;
    public bool ConfirmingRemove { get => _confirmingRemove; set => this.RaiseAndSetIfChanged(ref _confirmingRemove, value); }

    private async Task RemoveSelectedAsync()
    {
        if (!ConfirmingRemove) { ConfirmingRemove = true; return; }
        ConfirmingRemove = false;
        foreach (var m in SelectedManga) await _mangaService.RemoveAsync(m.Id);
        await LoadAsync();
    }

    // ── Categories ───────────────────────────────────────────────────

    public IReadOnlyList<MangaCategory> Categories => _categories;

    /// <summary>The choices for "Set categories": checked where every selected title already is.</summary>
    public List<CategoryChoiceVm> CategoryChoicesFor(IReadOnlyList<Manga> manga)
    {
        var ids = manga.Select(m => m.Id).ToHashSet();
        var sets = _entries.Where(e => ids.Contains(e.Manga.Id)).Select(e => e.CategoryIds).ToList();
        return _categories.Select(c => new CategoryChoiceVm(c, sets.Count > 0 && sets.All(s => s.Contains(c.Id)))).ToList();
    }

    public async Task SetCategoriesAsync(IReadOnlyList<Manga> manga, IEnumerable<int> categoryIds)
    {
        await _library.SetCategoriesAsync(manga.Select(m => m.Id), categoryIds);
        await LoadAsync();
    }

    public MangaLibraryService Library => _library;

    // ── Loading ──────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        await LoadSettingsAsync();
        var secret = _secretMode?.IsSecretModeActive ?? false;
        _categories = await _library.GetCategoriesAsync();
        _entries = await _library.GetEntriesAsync(_novels, secret);

        var selectedId = SelectedTab?.Id;
        Tabs.Clear();
        var uncategorised = _entries.Count(e => e.CategoryIds.Count == 0);
        // Default is always there with no categories; with some, only while it has titles.
        if (_categories.Count == 0 || uncategorised > 0)
            Tabs.Add(new CategoryTabVm(LibraryQuery.DefaultCategory, "Default", uncategorised));
        foreach (var c in _categories)
            Tabs.Add(new CategoryTabVm(c.Id, c.Name, _entries.Count(e => e.CategoryIds.Contains(c.Id))));

        _selectedTab = Tabs.FirstOrDefault(t => t.Id == selectedId) ?? Tabs.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedTab));
        this.RaisePropertyChanged(nameof(ShowTabs));
        this.RaisePropertyChanged(nameof(Categories));
        Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        // With categories, a tab shows its own; without, Default shows everything.
        int? category = _categories.Count == 0 ? null : SelectedTab?.Id;
        foreach (var e in LibraryQuery.Apply(_entries, Filters, _sort, _ascending, Search, category))
            Items.Add(new LibraryItemVm(e, this));
        SelectionChanged();
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(EmptyText));
    }

    private sealed record ViewSettings(LibraryDisplayMode Display, LibrarySort Sort, bool Ascending,
        LibraryFilters Filters, bool UnreadBadge, bool DownloadedBadge);

    private bool _settingsLoaded;

    private async Task LoadSettingsAsync()
    {
        if (_settingsLoaded || _config == null) return;
        _settingsLoaded = true;
        try
        {
            var json = await _config.GetValueAsync(SettingsKey + (_novels ? ".Novels" : ""), string.Empty);
            if (string.IsNullOrWhiteSpace(json)) return;
            var s = JsonSerializer.Deserialize<ViewSettings>(json);
            if (s == null) return;
            _loadingSettings = true;
            DisplayMode = s.Display;
            _sort = s.Sort;
            _ascending = s.Ascending;
            ShowUnreadBadge = s.UnreadBadge;
            ShowDownloadedBadge = s.DownloadedBadge;
            this.RaisePropertyChanged(nameof(SortLabel));
            Filters = s.Filters ?? new();
        }
        catch { /* defaults */ }
        finally { _loadingSettings = false; }
    }

    private void SaveSettings()
    {
        if (_loadingSettings || _config == null) return;
        var json = JsonSerializer.Serialize(new ViewSettings(DisplayMode, _sort, _ascending, Filters, ShowUnreadBadge, ShowDownloadedBadge));
        _ = _config.SetValueAsync(SettingsKey + (_novels ? ".Novels" : ""), json);
    }
}

/// <summary>"Edit categories": add, rename, reorder, delete.</summary>
public sealed class EditCategoriesViewModel : ViewModelBase
{
    private readonly MangaLibraryService _library;

    public EditCategoriesViewModel(MangaLibraryService library)
    {
        _library = library;
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync);
        RenameCommand = ReactiveCommand.CreateFromTask<CategoryChoiceVm>(RenameAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<CategoryChoiceVm>(DeleteAsync);
        MoveUpCommand = ReactiveCommand.CreateFromTask<CategoryChoiceVm>(c => MoveAsync(c, -1));
        MoveDownCommand = ReactiveCommand.CreateFromTask<CategoryChoiceVm>(c => MoveAsync(c, +1));
    }

    public ObservableCollection<CategoryChoiceVm> Categories { get; } = new();

    private string _newName = string.Empty;
    public string NewName { get => _newName; set => this.RaiseAndSetIfChanged(ref _newName, value); }

    private string? _error;
    public string? Error { get => _error; private set => this.RaiseAndSetIfChanged(ref _error, value); }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<CategoryChoiceVm, Unit> RenameCommand { get; }
    public ReactiveCommand<CategoryChoiceVm, Unit> DeleteCommand { get; }
    public ReactiveCommand<CategoryChoiceVm, Unit> MoveUpCommand { get; }
    public ReactiveCommand<CategoryChoiceVm, Unit> MoveDownCommand { get; }

    public async Task LoadAsync()
    {
        Categories.Clear();
        foreach (var c in await _library.GetCategoriesAsync()) Categories.Add(new CategoryChoiceVm(c, false));
    }

    private async Task Guard(Func<Task> action)
    {
        try { Error = null; await action(); }
        catch (InvalidOperationException ex) { Error = ex.Message; }
        await LoadAsync();
    }

    private Task AddAsync() => Guard(async () => { await _library.CreateCategoryAsync(NewName); NewName = string.Empty; });
    private Task RenameAsync(CategoryChoiceVm c) => Guard(() => _library.RenameCategoryAsync(c.Category.Id, c.Name));
    private Task DeleteAsync(CategoryChoiceVm c) => Guard(() => _library.DeleteCategoryAsync(c.Category.Id));

    private Task MoveAsync(CategoryChoiceVm c, int delta) => Guard(async () =>
    {
        var ids = Categories.Select(x => x.Category.Id).ToList();
        var i = ids.IndexOf(c.Category.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        await _library.ReorderCategoriesAsync(ids);
    });
}
