using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.Core.Vault;
using Sentrychan.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

public sealed class VaultItemVm : ViewModelBase
{
    public VaultItemVm(VaultEntry entry) => Entry = entry;

    public VaultEntry Entry { get; }
    public string Title => Entry.Title;
    public string Details
    {
        get
        {
            var parts = new List<string> { VaultService.FormatSize(Entry.Size), Entry.AddedAt.ToLocalTime().ToString("d MMM yyyy") };
            if (Entry.DurationSeconds > 0 && Entry.PositionSeconds > 0)
                parts.Add(Entry.PositionSeconds >= Entry.DurationSeconds - 30
                    ? "watched"
                    : $"{Entry.PositionSeconds / Entry.DurationSeconds:P0} watched");
            return string.Join("  ·  ", parts);
        }
    }

    private bool _confirmingDelete;
    public bool ConfirmingDelete { get => _confirmingDelete; set => this.RaiseAndSetIfChanged(ref _confirmingDelete, value); }
}

/// <summary>One chapter or imported album: a collection of encrypted pages.</summary>
public sealed class VaultAlbumVm : ViewModelBase
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public double? Sort { get; init; }
    public int PageCount { get; init; }
    public long Size { get; init; }
    public int LastPage { get; init; }
    public DateTime AddedAt { get; init; }

    /// <summary>First page, decoded small, as the card's cover.</summary>
    public string? ThumbUrl { get; init; }

    /// <summary>Set for chapters of a library title: those open in the normal reader, with the rest of the series.</summary>
    public int? ChapterId { get; init; }
    public int? MangaId { get; init; }

    public int? ExpectedPages { get; init; }

    /// <summary>A chapter whose download stopped part-way: some pages never arrived.</summary>
    public bool IsIncomplete => ExpectedPages > PageCount || DownloadUnfinished;

    /// <summary>The library never marked this chapter downloaded — its download stopped part-way.</summary>
    public bool DownloadUnfinished { get; init; }

    public string IncompleteText => ExpectedPages > PageCount
        ? $"Incomplete: {PageCount} of {ExpectedPages} pages. Download it again from the title's page to fetch the rest."
        : "Incomplete: the download didn't finish. Download it again from the title's page to fetch the rest.";

    public string Details
    {
        get
        {
            var read = LastPage <= 0 ? ""
                : LastPage + 1 >= PageCount ? "  ·  finished"
                : $"  ·  page {LastPage + 1} of {PageCount}";
            var pages = ExpectedPages > PageCount ? $"{PageCount} of {ExpectedPages} pages" : $"{PageCount} page{(PageCount == 1 ? "" : "s")}";
            return $"{pages}  ·  {VaultService.FormatSize(Size)}{read}";
        }
    }

    private bool _confirmingDelete;
    public bool ConfirmingDelete { get => _confirmingDelete; set => this.RaiseAndSetIfChanged(ref _confirmingDelete, value); }
}

/// <summary>The albums of one title (or one imported folder).</summary>
public sealed class VaultGroupVm : ViewModelBase
{
    public required string Name { get; init; }
    public List<VaultAlbumVm> Albums { get; init; } = [];
    public string Details => $"{Albums.Count} {(Albums.Count == 1 ? "item" : "items")}  ·  {VaultService.FormatSize(Albums.Sum(a => a.Size))}";

    private bool _confirmingDelete;
    public bool ConfirmingDelete { get => _confirmingDelete; set => this.RaiseAndSetIfChanged(ref _confirmingDelete, value); }
}

/// <summary>
/// The vault page, reachable only in secret mode: private videos, downloaded and imported
/// manga, the import of media already on disk, and the controls that keep secret mode
/// private — the unlock code, the key backup, and the image-cache purge.
/// </summary>
public sealed class VaultViewModel : ViewModelBase
{
    private readonly Action<string, string> _toast;
    private readonly VaultService? _vault;

    /// <summary>Opens albums in the reader: the ordered albums of a group, and which to start on.</summary>
    public Func<IReadOnlyList<VaultAlbumVm>, int, Task>? OpenAlbums { get; set; }

    /// <summary>Opens a library chapter (manga id, chapter id) in the normal reader.</summary>
    public Func<int, int, Task<bool>>? OpenLibraryChapter { get; set; }

    public VaultViewModel(Action<string, string> toast)
    {
        _toast = toast;
        _vault = App.Services?.GetService<VaultService>();

        PlayCommand          = ReactiveCommand.CreateFromTask<VaultItemVm>(PlayAsync);
        DeleteCommand        = ReactiveCommand.CreateFromTask<VaultItemVm>(DeleteAsync);
        CancelDeleteCommand  = ReactiveCommand.Create<VaultItemVm>(i => i.ConfirmingDelete = false);
        ReadAlbumCommand     = ReactiveCommand.CreateFromTask<VaultAlbumVm>(ReadAlbumAsync);
        DeleteAlbumCommand   = ReactiveCommand.CreateFromTask<VaultAlbumVm>(DeleteAlbumAsync);
        CancelDeleteAlbumCommand = ReactiveCommand.Create<VaultAlbumVm>(a => a.ConfirmingDelete = false);
        DeleteGroupCommand   = ReactiveCommand.CreateFromTask<VaultGroupVm>(DeleteGroupAsync);
        CancelDeleteGroupCommand = ReactiveCommand.Create<VaultGroupVm>(g => g.ConfirmingDelete = false);
        AddFolderCommand     = ReactiveCommand.CreateFromTask(AddFolderAsync);
        AddFilesCommand      = ReactiveCommand.CreateFromTask(AddFilesAsync);
        ConfirmImportCommand = ReactiveCommand.CreateFromTask(ImportAsync);
        DiscardPlanCommand   = ReactiveCommand.Create(() => { PendingPlan = null; });
        CancelImportCommand  = ReactiveCommand.Create(() => _importCts?.Cancel());
        MoveMangaCommand     = ReactiveCommand.CreateFromTask(MoveMangaAsync);
        SetPinCommand        = ReactiveCommand.Create(SetPin);
        ExportKeyCommand     = ReactiveCommand.CreateFromTask(ExportKeyAsync);
        ImportKeyCommand     = ReactiveCommand.CreateFromTask(ImportKeyAsync);
        ClearImageCacheCommand = ReactiveCommand.Create(ClearImageCache);

        this.WhenAnyValue(x => x.Filter).Skip(1).Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ => ApplyFilter());
    }

    public ObservableCollection<VaultItemVm> Videos { get; } = new();
    public ObservableCollection<VaultGroupVm> Groups { get; } = new();

    private List<VaultItemVm> _allVideos = [];
    private List<VaultGroupVm> _allGroups = [];

    private int _selectedTab;
    /// <summary>0 Videos · 1 Manga &amp; images · 2 Privacy.</summary>
    public int SelectedTab { get => _selectedTab; set => this.RaiseAndSetIfChanged(ref _selectedTab, value); }

    private string _filter = string.Empty;
    public string Filter { get => _filter; set => this.RaiseAndSetIfChanged(ref _filter, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string _videosHeader = "Videos";
    public string VideosHeader { get => _videosHeader; set => this.RaiseAndSetIfChanged(ref _videosHeader, value); }

    private string _mangaHeader = "Manga & images";
    public string MangaHeader { get => _mangaHeader; set => this.RaiseAndSetIfChanged(ref _mangaHeader, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public bool NoVideos => _allVideos.Count == 0;
    public bool NoAlbums => _allGroups.Count == 0;
    public bool NoFilterMatch => (_allVideos.Count > 0 || _allGroups.Count > 0) && Videos.Count == 0 && Groups.Count == 0;

    public bool UsingBuiltInCode => !SecretPin.IsSet;

    private string? _newPin;
    public string? NewPin { get => _newPin; set => this.RaiseAndSetIfChanged(ref _newPin, value); }

    private string? _confirmPin;
    public string? ConfirmPin { get => _confirmPin; set => this.RaiseAndSetIfChanged(ref _confirmPin, value); }

    private string? _passphrase;
    public string? Passphrase { get => _passphrase; set => this.RaiseAndSetIfChanged(ref _passphrase, value); }

    public ReactiveCommand<VaultItemVm, Unit> PlayCommand { get; }
    public ReactiveCommand<VaultItemVm, Unit> DeleteCommand { get; }
    public ReactiveCommand<VaultItemVm, Unit> CancelDeleteCommand { get; }
    public ReactiveCommand<VaultAlbumVm, Unit> ReadAlbumCommand { get; }
    public ReactiveCommand<VaultAlbumVm, Unit> DeleteAlbumCommand { get; }
    public ReactiveCommand<VaultAlbumVm, Unit> CancelDeleteAlbumCommand { get; }
    public ReactiveCommand<VaultGroupVm, Unit> DeleteGroupCommand { get; }
    public ReactiveCommand<VaultGroupVm, Unit> CancelDeleteGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFilesCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmImportCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscardPlanCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelImportCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveMangaCommand { get; }
    public ReactiveCommand<Unit, Unit> SetPinCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearImageCacheCommand { get; }

    // ── Loading ───────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        if (_vault == null) { Summary = "The vault isn't available."; return; }
        try { await _vault.EnsureReadyAsync(); }
        catch (Exception ex) { Summary = $"The vault couldn't be opened: {ex.Message}"; return; }

        var all = _vault.Entries;

        _allVideos = all.Where(e => e.Kind == VaultKind.Video)
            .OrderBy(e => e.Title, NaturalComparer.Instance)
            .Select(e => new VaultItemVm(e)).ToList();

        var albums = await BuildAlbumsAsync(all);
        _allGroups = albums
            .GroupBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VaultGroupVm
            {
                Name = g.Key,
                Albums = g.OrderBy(a => a.Sort ?? double.MaxValue).ThenBy(a => a.Title, NaturalComparer.Instance).ToList(),
            })
            .OrderByDescending(g => g.Albums.Max(a => a.AddedAt))
            .ToList();

        ApplyFilter();
        this.RaisePropertyChanged(nameof(NoVideos));
        this.RaisePropertyChanged(nameof(NoAlbums));
        this.RaisePropertyChanged(nameof(UsingBuiltInCode));

        VideosHeader = _allVideos.Count > 0 ? $"Videos · {_allVideos.Count}" : "Videos";
        MangaHeader = albums.Count > 0 ? $"Manga & images · {albums.Count}" : "Manga & images";
        Summary = $"{VaultService.FormatSize(all.Sum(e => e.Size))} encrypted  ·  opens only on this PC, for this Windows account";

        // Land on whatever has content, the first time.
        if (!_tabChosen && _allVideos.Count == 0 && albums.Count > 0) SelectedTab = 1;
        _tabChosen = true;
    }

    private bool _tabChosen;

    private async Task<List<VaultAlbumVm>> BuildAlbumsAsync(IReadOnlyList<VaultEntry> all)
    {
        var byKey = all.Where(e => e.Kind == VaultKind.Page && e.Collection != null)
            .GroupBy(e => e.Collection!).ToList();

        // Chapters the downloader put here: find their library title. Older downloads have no
        // name in the vault yet — give them one now, from the database.
        // A finished chapter is found by the path it was downloaded to; one still in progress
        // only by the id in its key. (Ids used to change on every chapter-list refresh, so
        // older keys can name an id that no longer exists — see the re-link below.)
        var chapterKeys = byKey.Where(g => MangaDownloadService.ChapterIdFromCollection(g.Key) != null).Select(g => g.Key).ToList();
        var byPath = new Dictionary<string, (int MangaId, string Manga, MangaChapter Chapter)>();
        var byId = new Dictionary<int, (int MangaId, string Manga, MangaChapter Chapter)>();
        var censored = new List<(int MangaId, string Manga, MangaChapter Chapter)>();
        if (chapterKeys.Count > 0 && App.Services?.GetService<IDbContextFactory<AppDbContext>>() is { } dbf)
        {
            try
            {
                await using var db = await dbf.CreateDbContextAsync();
                var ids = chapterKeys.Select(k => MangaDownloadService.ChapterIdFromCollection(k)!.Value).ToList();
                var paths = chapterKeys.Select(k => MangaDownloadService.VaultPathPrefix + k).ToList();
                var rows = await db.MangaChapters
                    .Where(c => ids.Contains(c.Id) || (c.DownloadedPath != null && paths.Contains(c.DownloadedPath)))
                    .Join(db.Manga, c => c.MangaId, m => m.Id, (c, m) => new { c, m.Title })
                    .ToListAsync();
                foreach (var r in rows)
                {
                    byId[r.c.Id] = (r.c.MangaId, r.Title, r.c);
                    if (r.c.DownloadedPath != null) byPath[r.c.DownloadedPath] = (r.c.MangaId, r.Title, r.c);
                }
                // Candidates for re-linking an orphaned partial download: unfinished chapters of adult titles.
                censored = (await db.MangaChapters.Where(c => c.DownloadedPath == null)
                        .Join(db.Manga.Where(m => m.IsCensored), c => c.MangaId, m => m.Id, (c, m) => new { c, m.Title })
                        .ToListAsync())
                    .Select(r => (r.c.MangaId, r.Title, r.c)).ToList();
            }
            catch { /* the vault still lists them, just without a title */ }
        }

        var named = false;
        var list = new List<VaultAlbumVm>();
        foreach (var g in byKey.ToList())
        {
            var pages = g.OrderBy(e => e.Order).ToList();
            var key = g.Key;
            var info = _vault!.GetCollectionInfo(key);
            var keyId = MangaDownloadService.ChapterIdFromCollection(key);
            (int MangaId, string Manga, MangaChapter Chapter)? lib =
                byPath.TryGetValue(MangaDownloadService.VaultPathPrefix + key, out var p) ? p
                : keyId is { } kid && byId.TryGetValue(kid, out var l) && string.IsNullOrEmpty(l.Chapter.DownloadedPath) ? l
                : null;

            // An unfinished download whose chapter id went stale: find the chapter again by
            // title and chapter label, and move the pages to its current key so a Resume finds them.
            if (lib == null && keyId != null && info?.Group != null)
            {
                var match = censored.Where(c => c.Manga == info.Group && MangaDownloadService.ChapterLabel(c.Chapter) == info.Title).ToList();
                if (match.Count == 1)
                {
                    var newKey = $"manga-chapter-{match[0].Chapter.Id}";
                    if (_vault.Collection(newKey).Count == 0)
                    {
                        await _vault.RekeyCollectionAsync(key, newKey);
                        key = newKey;
                        keyId = match[0].Chapter.Id;
                        lib = match[0];
                    }
                }
            }
            var chapterId = lib?.Chapter.Id;

            if (info == null && lib is { } found)
            {
                await _vault.DescribeCollectionAsync(key, MangaDownloadService.ChapterLabel(found.Chapter), found.Manga,
                    found.Chapter.ChapterSort, save: false);
                info = _vault.GetCollectionInfo(key);
                named = true;
            }

            var first = pages[0];
            list.Add(new VaultAlbumVm
            {
                Key = key,
                Title = info?.Title ?? "Untitled",
                Group = info?.Group ?? "Other",
                Sort = info?.Sort,
                PageCount = pages.Count,
                ExpectedPages = info?.ExpectedPages ?? (lib is { Chapter.Pages: > 0 } lp ? lp.Chapter.Pages : null),
                DownloadUnfinished = lib is { } lc && string.IsNullOrEmpty(lc.Chapter.DownloadedPath),
                Size = pages.Sum(p => p.Size),
                LastPage = lib?.Chapter.LastReadPage ?? info?.LastPage ?? 0,
                AddedAt = info?.AddedAt is { } at && at != default ? at : pages.Max(p => p.AddedAt),
                ThumbUrl = Controls.AsyncImage.VaultScheme + first.Id + first.Extension,
                ChapterId = chapterId,
                MangaId = lib?.MangaId,
            });
        }
        if (named) await _vault!.FlushAsync();
        return list;
    }

    private void ApplyFilter()
    {
        var q = Filter?.Trim() ?? "";
        bool Match(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

        Videos.Clear();
        foreach (var v in _allVideos.Where(v => Match(v.Title))) Videos.Add(v);

        Groups.Clear();
        foreach (var g in _allGroups)
        {
            if (Match(g.Name)) { Groups.Add(g); continue; }
            var hits = g.Albums.Where(a => Match(a.Title)).ToList();
            if (hits.Count > 0) Groups.Add(new VaultGroupVm { Name = g.Name, Albums = hits });
        }
        this.RaisePropertyChanged(nameof(NoFilterMatch));
    }

    // ── Videos ────────────────────────────────────────────────────────

    private async Task PlayAsync(VaultItemVm item)
    {
        // The list as shown is the playlist, so "next" continues through it.
        var ordered = Videos.ToList();
        var playlist = ordered.Select(v => new PlaybackItem(v.Title, VaultId: v.Entry.Id)).ToList();
        await PlayerLauncher.PlayAsync(playlist, ordered.IndexOf(item));
    }

    private async Task DeleteAsync(VaultItemVm item)
    {
        if (_vault == null) return;
        if (!item.ConfirmingDelete) { item.ConfirmingDelete = true; return; }
        await _vault.RemoveAsync(item.Entry.Id);
        await LoadAsync();
    }

    // ── Manga & images ────────────────────────────────────────────────

    private async Task ReadAlbumAsync(VaultAlbumVm album)
    {
        // A chapter of a title still in the library opens like any chapter: progress is
        // tracked and the reader flows on into the next one.
        if (album.MangaId is { } mangaId && album.ChapterId is { } chapterId && OpenLibraryChapter != null
            && await OpenLibraryChapter(mangaId, chapterId))
            return;

        var group = _allGroups.FirstOrDefault(g => g.Albums.Contains(album));
        var albums = group?.Albums ?? [album];
        if (OpenAlbums != null) await OpenAlbums(albums, albums.IndexOf(album));
    }

    private async Task DeleteAlbumAsync(VaultAlbumVm album)
    {
        if (_vault == null) return;
        if (!album.ConfirmingDelete) { album.ConfirmingDelete = true; return; }
        await RemoveAlbumAsync(album);
        await LoadAsync();
    }

    private async Task DeleteGroupAsync(VaultGroupVm group)
    {
        if (_vault == null) return;
        if (!group.ConfirmingDelete) { group.ConfirmingDelete = true; return; }
        IsBusy = true;
        try { foreach (var a in group.Albums) await RemoveAlbumAsync(a); }
        finally { IsBusy = false; }
        Status = $"Deleted {group.Albums.Count} item(s).";
        await LoadAsync();
    }

    private async Task RemoveAlbumAsync(VaultAlbumVm album)
    {
        // A library chapter also forgets it was downloaded, so its row offers Download again.
        if (album.ChapterId is { } id && App.Services?.GetService<IMangaDownloadService>() is { } downloads)
            await downloads.DeleteChapterDownloadAsync(new MangaChapter
            {
                Id = id, DownloadedPath = MangaDownloadService.VaultPathPrefix + album.Key,
            });
        else
            await _vault!.RemoveCollectionAsync(album.Key);
    }

    // ── Importing ─────────────────────────────────────────────────────

    private VaultImportPlan? _pendingPlan;
    public VaultImportPlan? PendingPlan
    {
        get => _pendingPlan;
        set
        {
            this.RaiseAndSetIfChanged(ref _pendingPlan, value);
            this.RaisePropertyChanged(nameof(HasPlan));
        }
    }
    public bool HasPlan => _pendingPlan != null;

    private string _planSummary = string.Empty;
    public string PlanSummary { get => _planSummary; set => this.RaiseAndSetIfChanged(ref _planSummary, value); }

    private string _planPreview = string.Empty;
    public string PlanPreview { get => _planPreview; set => this.RaiseAndSetIfChanged(ref _planPreview, value); }

    private string _planWarning = string.Empty;
    public string PlanWarning { get => _planWarning; set => this.RaiseAndSetIfChanged(ref _planWarning, value); }

    private bool _deleteOriginals = true;
    public bool DeleteOriginals { get => _deleteOriginals; set => this.RaiseAndSetIfChanged(ref _deleteOriginals, value); }

    private bool _isImporting;
    public bool IsImporting { get => _isImporting; set => this.RaiseAndSetIfChanged(ref _isImporting, value); }

    private string _importProgress = string.Empty;
    public string ImportProgress { get => _importProgress; set => this.RaiseAndSetIfChanged(ref _importProgress, value); }

    private CancellationTokenSource? _importCts;

    private async Task AddFolderAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to move into the vault",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

        await _vault.EnsureReadyAsync();
        IsBusy = true;
        Status = "Looking through the folder…";
        try
        {
            var plan = await Task.Run(() => new VaultImporter(_vault).PlanFolder(path));
            await ShowPlanAsync(plan, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) { Status = $"Couldn't read that folder: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task AddFilesAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose files to move into the vault",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Videos, images and comic archives")
                {
                    Patterns = VaultImporter.VideoExtensions.Concat(VaultImporter.ImageExtensions)
                        .Concat(VaultImporter.ArchiveExtensions).Select(e => "*" + e).ToList(),
                },
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;

        await _vault.EnsureReadyAsync();
        var plan = await Task.Run(() => new VaultImporter(_vault).PlanFiles(paths));
        await ShowPlanAsync(plan, null);
    }

    private async Task ShowPlanAsync(VaultImportPlan plan, string? folderName)
    {
        if (plan.IsEmpty)
        {
            PendingPlan = null;
            Status = "Nothing to import there — no videos, images or .cbz/.zip archives.";
            return;
        }

        PlanSummary = folderName == null ? $"Import {plan.Describe()}?" : $"Import “{folderName}”: {plan.Describe()}?";

        var names = plan.Albums.Select(a => a.Group != null && a.Group != a.Title ? $"{a.Group} › {a.Title}" : a.Title)
            .OrderBy(n => n, NaturalComparer.Instance)
            .Concat(plan.Videos.Select(Path.GetFileName)).OfType<string>().ToList();
        PlanPreview = string.Join("\n", names.Take(6)) + (names.Count > 6 ? $"\n… and {names.Count - 6} more" : "");

        PlanWarning = await WarningForAsync(plan);
        Status = string.Empty;
        PendingPlan = plan;
    }

    /// <summary>Picking the library (or a whole drive) by mistake would encrypt everything in it.</summary>
    private static async Task<string> WarningForAsync(VaultImportPlan plan)
    {
        if (plan.Root == null) return string.Empty;
        if (Path.GetPathRoot(plan.Root)?.TrimEnd(Path.DirectorySeparatorChar) == plan.Root.TrimEnd(Path.DirectorySeparatorChar))
            return "This is a whole drive. Everything on it that's a video or image would be moved into the vault.";

        if (App.Services?.GetService<IConfigService>() is { } config)
        {
            var library = await config.GetValueAsync("LibraryPath", string.Empty);
            if (!string.IsNullOrWhiteSpace(library))
            {
                var lib = Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var root = plan.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (lib.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return "This folder contains your whole Library. Importing it would move every episode into the vault.";
            }
        }
        return plan.Videos.Count > 50 ? "That's a lot of videos — check this is the folder you meant." : string.Empty;
    }

    private async Task ImportAsync()
    {
        if (_vault == null || PendingPlan is not { } plan) return;
        IsImporting = true;
        _importCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(s => ImportProgress = s);
            var result = await Task.Run(() => new VaultImporter(_vault).ImportAsync(plan, DeleteOriginals, progress, _importCts.Token));

            var bits = new List<string>();
            if (result.Videos > 0) bits.Add($"{result.Videos} video(s)");
            if (result.Albums > 0) bits.Add($"{result.Albums} album(s), {result.Pages} pages");
            var what = bits.Count == 0 ? "Nothing" : string.Join(" and ", bits);
            Status = result.Errors.Count == 0
                ? $"{what} added.{(DeleteOriginals ? " The originals were removed." : "")}"
                : $"{what} added. {result.Errors.Count} couldn't be — left where they were: {string.Join("; ", result.Errors.Take(3))}";
            if (result.Albums > 0 && result.Videos == 0) SelectedTab = 1;
            else if (result.Videos > 0 && result.Albums == 0) SelectedTab = 0;
        }
        catch (OperationCanceledException)
        {
            Status = "Import stopped. What finished is in the vault; everything else was left where it was.";
        }
        catch (Exception ex) { Status = $"Import failed: {ex.Message}"; }
        finally
        {
            IsImporting = false;
            ImportProgress = string.Empty;
            PendingPlan = null;
            _importCts?.Dispose();
            _importCts = null;
            await LoadAsync();
        }
    }

    private async Task MoveMangaAsync()
    {
        var downloads = App.Services?.GetService<IMangaDownloadService>();
        if (downloads == null) return;
        IsBusy = true;
        try
        {
            var moved = await downloads.MoveAdultDownloadsIntoVaultAsync(new Progress<string>(s => Status = s));
            Status = moved == 0
                ? "Nothing to move — every adult manga download is already in the vault."
                : $"{moved} chapter(s) moved into the vault; their folders were removed.";
        }
        catch (Exception ex) { Status = $"Move failed: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            await LoadAsync();
        }
    }

    // ── Keeping secret mode private ───────────────────────────────────

    private void SetPin()
    {
        if (string.IsNullOrWhiteSpace(NewPin) || NewPin.Length < 4) { Status = "Use at least 4 characters."; return; }
        if (NewPin != ConfirmPin) { Status = "The two entries don't match."; return; }
        SecretPin.Set(NewPin);
        NewPin = ConfirmPin = null;
        this.RaisePropertyChanged(nameof(UsingBuiltInCode));
        Status = "Unlock code changed. The built-in code no longer works.";
    }

    private async Task ExportKeyAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        if (string.IsNullOrEmpty(Passphrase) || Passphrase.Length < 8)
        {
            Status = "Choose a passphrase of at least 8 characters for the backup first.";
            return;
        }
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save vault key backup",
            SuggestedFileName = "sentrychan-key.bak",
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        await _vault.ExportKeyAsync(path, Passphrase);
        Passphrase = null;
        Status = "Key backup saved. Keep it and its passphrase somewhere safe — without both, the vault can't be opened on a new PC.";
    }

    private async Task ImportKeyAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        if (string.IsNullOrEmpty(Passphrase)) { Status = "Enter the backup's passphrase first."; return; }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open vault key backup" });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

        var ok = await _vault.ImportKeyAsync(path, Passphrase);
        Passphrase = null;
        Status = ok ? "Key restored." : "That backup and passphrase don't open this vault.";
        if (ok) await LoadAsync();
    }

    private void ClearImageCache()
    {
        var removed = Controls.AsyncImage.ClearDiskCache();
        Controls.AsyncImage.ClearMemoryCache();
        Status = $"Image cache cleared ({removed} file(s)). Covers load again as you browse.";
    }

    private static TopLevel? TopLevel() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;
}
