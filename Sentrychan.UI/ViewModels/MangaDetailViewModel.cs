using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// A single manga: metadata, its chapter list (fetched/cached from the source), and
/// reading-progress tracking. Stage 1 tracks progress via "mark read"; Stage 2 adds
/// the actual reader.
/// </summary>
public partial class MangaDetailViewModel : ViewModelBase
{
    private readonly IMangaService _mangaService;
    private readonly IMangaSourceService _source;
    private readonly IMangaDownloadService _downloader;
    private readonly Action<int> _onRead; // opens the reader at a chapter (by DB id)

    public Manga Manga { get; private set; }

    public ObservableCollection<MangaChapterVm> Chapters { get; } = [];

    public string Title => Manga.Title;
    public string CoverUrl => Manga.CoverPath;
    public string? Description => Manga.Description;

    /// <summary>Novel (text) title — hides image-only affordances like offline download.</summary>
    public bool IsNovel => Manga.IsNovel;

    public string StatusLine
    {
        get
        {
            var bits = new System.Collections.Generic.List<string> { _source.SourceName };
            if (Manga.Year.HasValue) bits.Add(Manga.Year.Value.ToString());
            if (!string.IsNullOrEmpty(Manga.Status))
                bits.Add(char.ToUpper(Manga.Status![0]) + Manga.Status.Substring(1));
            return string.Join("  ·  ", bits);
        }
    }

    private string _progressText = string.Empty;
    public string ProgressText { get => _progressText; set => this.RaiseAndSetIfChanged(ref _progressText, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _status = string.Empty;
    public string StatusMessage { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> MarkAllReadCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> ContinueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }

    private bool _canContinue;
    public bool CanContinue { get => _canContinue; set => this.RaiseAndSetIfChanged(ref _canContinue, value); }

    private string _continueLabel = "Read";
    public string ContinueLabel { get => _continueLabel; set => this.RaiseAndSetIfChanged(ref _continueLabel, value); }

    public MangaDetailViewModel(Manga manga, IMangaService mangaService, IMangaSourceService source,
        IMangaDownloadService downloader, Action onBack, Action<int> onRead,
        Sentrychan.Core.MangaLibrary.MangaLibraryService? library = null)
    {
        Manga = manga;
        _mangaService = mangaService;
        _source = source;
        _downloader = downloader;
        _onRead = onRead;
        _library = library;
        InitMihon();

        RefreshCommand     = ReactiveCommand.CreateFromTask(() => LoadChaptersAsync(forceRefresh: true));
        MarkAllReadCommand = ReactiveCommand.CreateFromTask(MarkAllReadAsync);
        BackCommand        = ReactiveCommand.Create(onBack);
        ContinueCommand    = ReactiveCommand.Create(Continue);
        DownloadAllCommand = ReactiveCommand.CreateFromTask(DownloadAllAsync);
        CancelAllDownloadsCommand = ReactiveCommand.Create(() => _downloader.CancelAll(Manga.Id));
        RetryFailedCommand = ReactiveCommand.CreateFromTask(RetryFailedAsync);
        _downloader.StatusChanged += OnDownloadStatus;
    }

    // ── Downloads ─────────────────────────────────────────────────────
    // The downloader owns the queue and reports every change; rows and the header summary
    // just mirror it. So leaving this page and coming back shows downloads still running.

    private readonly System.Collections.Generic.Dictionary<int, MangaChapterVm> _byId = new();

    private void OnDownloadStatus(ChapterDownloadStatus s)
    {
        if (s.MangaId != Manga.Id) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_byId.TryGetValue(s.ChapterId, out var vm)) vm.Apply(s);
            UpdateDownloadSummary();
        });
    }

    /// <summary>Stops listening to the downloader — call when this page is replaced.</summary>
    public void Detach() => _downloader.StatusChanged -= OnDownloadStatus;

    private string _downloadSummary = string.Empty;
    public string DownloadSummary { get => _downloadSummary; private set => this.RaiseAndSetIfChanged(ref _downloadSummary, value); }

    private bool _isDownloadActive;
    public bool IsDownloadActive { get => _isDownloadActive; private set => this.RaiseAndSetIfChanged(ref _isDownloadActive, value); }

    private double _downloadProgress;
    /// <summary>Overall progress of the current batch, 0–100, counting pages of the running chapters.</summary>
    public double DownloadProgress { get => _downloadProgress; private set => this.RaiseAndSetIfChanged(ref _downloadProgress, value); }

    private bool _isDownloadProgressKnown;
    /// <summary>False while the only work is fetching page lists — the bar animates instead of sitting at 0%.</summary>
    public bool IsDownloadProgressKnown { get => _isDownloadProgressKnown; private set => this.RaiseAndSetIfChanged(ref _isDownloadProgressKnown, value); }

    public ReactiveCommand<Unit, Unit> CancelAllDownloadsCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryFailedCommand { get; }

    private int _failedCount;
    public int FailedCount
    {
        get => _failedCount;
        private set { this.RaiseAndSetIfChanged(ref _failedCount, value); this.RaisePropertyChanged(nameof(HasFailed)); }
    }
    public bool HasFailed => _failedCount > 0;

    // Chapters queued in this batch, so the summary can say "3 of 12".
    private int _batchSize;

    private void UpdateDownloadSummary()
    {
        if (IsNovel) return;
        var rows = Chapters.ToList();
        var active = rows.Where(c => c.IsBusy).ToList();
        var done = rows.Count(c => c.IsDownloaded);
        FailedCount = rows.Count(c => c.IsFailed);
        IsDownloadActive = active.Count > 0;

        if (active.Count == 0)
        {
            _batchSize = 0;
            DownloadProgress = 0;
            var failed = FailedCount > 0 ? $"  ·  {FailedCount} failed" : "";
            DownloadSummary = done == 0 && FailedCount == 0 ? "" : $"{done} of {rows.Count} chapters downloaded{failed}";
            return;
        }

        _batchSize = Math.Max(_batchSize, active.Count);
        var finished = _batchSize - active.Count;
        var running = active.Where(c => c.IsDownloading).ToList();
        var fraction = finished + running.Sum(c => c.Progress / 100.0);
        DownloadProgress = 100.0 * fraction / _batchSize;
        IsDownloadProgressKnown = fraction > 0;

        var now = running.FirstOrDefault();
        var current = now == null ? "waiting to start" : $"Ch. {now.Chapter.ChapterNumber}: {now.ProgressText}";
        DownloadSummary = _batchSize == 1
            ? $"Downloading — {current}"
            : $"Downloading — {finished} of {_batchSize} chapters done  ·  {current}";
    }

    private async Task DownloadChapterAsync(MangaChapterVm vm)
    {
        if (vm.IsDownloaded || vm.IsBusy) return;
        try
        {
            var path = await _downloader.DownloadChapterAsync(Manga, vm.Chapter);
            if (!string.IsNullOrEmpty(path))
            {
                vm.Chapter.DownloadedPath = path;
                vm.IsDownloaded = true;
            }
        }
        catch (Exception)
        {
            // The downloader already reported the failure; the row shows it with a Retry.
        }
        finally { UpdateDownloadSummary(); }
    }

    private async Task DownloadAllAsync()
    {
        // Queue everything at once, oldest first; the downloader runs two at a time.
        var todo = Chapters.Where(c => !c.IsDownloaded && !c.IsBusy)
            .OrderBy(c => c.Sort ?? double.MaxValue).ToList();
        if (todo.Count == 0) { StatusMessage = "Every chapter is already downloaded."; return; }
        await Task.WhenAll(todo.Select(DownloadChapterAsync));
    }

    private Task RetryFailedAsync() =>
        Task.WhenAll(Chapters.Where(c => c.IsFailed).OrderBy(c => c.Sort ?? double.MaxValue).ToList().Select(DownloadChapterAsync));

    private void CancelDownload(MangaChapterVm vm) => _downloader.Cancel(vm.Chapter.Id);

    private async Task RemoveDownloadAsync(MangaChapterVm vm)
    {
        await _downloader.DeleteChapterDownloadAsync(vm.Chapter);
        vm.Chapter.DownloadedPath = null;
        vm.IsDownloaded = false;
        vm.Apply(null);
        UpdateDownloadSummary();
    }

    /// <summary>Opens the reader at the first unread chapter (or the last-read one to resume).</summary>
    private void Continue()
    {
        // Chapters are stored newest-first; walk oldest-first to find the next unread.
        var ordered = Chapters.OrderBy(c => c.Sort ?? double.MaxValue).ToList();
        var next = ordered.FirstOrDefault(c => !c.IsRead) ?? ordered.LastOrDefault();
        if (next != null) _onRead(next.Chapter.Id);
    }

    private void UpdateContinueState()
    {
        var hasChapters = Chapters.Count > 0;
        CanContinue = hasChapters;
        ContinueLabel = Manga.LastReadChapter > 0 ? "Continue" : "Read";
    }

    public async Task InitializeAsync()
    {
        if (_library != null) _bookmarks = await _library.GetBookmarksAsync(Manga.Id);
        // Prefer cached chapters for an instant list; refresh from the source if empty.
        var stored = await _mangaService.GetByIdAsync(Manga.Id);
        if (stored != null) Manga = stored;

        if (Manga.Chapters.Count > 0)
        {
            PopulateFrom(Manga.Chapters);
            UpdateProgressText();
        }

        await LoadChaptersAsync(forceRefresh: Manga.Chapters.Count == 0);
    }

    private async Task LoadChaptersAsync(bool forceRefresh)
    {
        if (!forceRefresh && Chapters.Count > 0) return;

        IsLoading = true;
        StatusMessage = "Loading chapters…";
        try
        {
            var infos = await _source.GetChaptersAsync(Manga.SourceId, "en");
            if (infos.Count == 0)
            {
                StatusMessage = "No English chapters found on the source.";
                return;
            }

            var synced = await _mangaService.SyncChaptersAsync(Manga.Id, infos);
            PopulateFrom(synced);
            UpdateProgressText();
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { StatusMessage = $"Failed to load chapters: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void PopulateFrom(System.Collections.Generic.IEnumerable<MangaChapter> chapters)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Chapters.Clear();
            _byId.Clear();
            var vault = Manga.IsCensored && App.Services?.GetService(typeof(Sentrychan.Core.Vault.VaultService))
                is Sentrychan.Core.Vault.VaultService { IsReady: true } v ? v : null;            // Newest first reads best in a list.
            foreach (var c in chapters.OrderByDescending(c => c.ChapterSort ?? double.MinValue))
            {
                var vm = new MangaChapterVm(c, MarkReadAsync, vm => _onRead(vm.Chapter.Id), DownloadChapterAsync,
                    !Manga.IsNovel, CancelDownload, RemoveDownloadAsync)
                {
                    IsRead = Sentrychan.Core.MangaLibrary.LibraryQuery.IsRead(Manga, c),
                    IsBookmarked = _bookmarks.Contains(c.Id),
                    SelectionChanged = OnChapterSelectionChanged,
                };
                // A download started earlier (this page was left and reopened) is still running.
                var status = _downloader.GetStatus(c.Id);
                if (status != null && (status.IsActive || status.State is ChapterDownloadState.Failed or ChapterDownloadState.Cancelled))
                    vm.Apply(status);
                else if (vault != null && string.IsNullOrEmpty(c.DownloadedPath))
                {
                    // Adult chapters download page by page into the vault, so a stopped one can be counted.
                    var key = $"manga-chapter-{c.Id}";
                    var saved = vault.Collection(key).Count;
                    if (saved > 0) vm.SetPartial(saved, vault.GetCollectionInfo(key)?.ExpectedPages ?? c.Pages);
                }
                _byId[c.Id] = vm;
                Chapters.Add(vm);
            }
            UpdateContinueState();
            UpdateDownloadSummary();
            RebuildVisible();
        });
    }

    private async Task MarkReadAsync(MangaChapterVm vm)
    {
        vm.IsRead = true;
        var num = vm.Chapter.ChapterSort;
        if (num.HasValue)
        {
            await _mangaService.UpdateProgressAsync(Manga.Id, num.Value);
            // Mark every earlier chapter read too — you don't read ch.20 without ch.19.
            foreach (var c in Chapters.Where(c => (c.Sort ?? double.MaxValue) <= num.Value))
                c.IsRead = true;
            Manga.LastReadChapter = Math.Max(Manga.LastReadChapter, num.Value);
            UpdateProgressText();
        }
    }

    private async Task MarkAllReadAsync()
    {
        var top = Chapters.Select(c => c.Sort).Where(n => n.HasValue).DefaultIfEmpty(null).Max();
        if (!top.HasValue) return;
        await _mangaService.UpdateProgressAsync(Manga.Id, top.Value);
        foreach (var c in Chapters) c.IsRead = true;
        Manga.LastReadChapter = top.Value;
        UpdateProgressText();
    }

    private void UpdateProgressText()
    {
        var read = Manga.LastReadChapter;
        var total = Manga.TotalChapters;
        ProgressText = total.HasValue
            ? $"Read {read:0.#} / {total:0.#}"
            : read > 0 ? $"Read up to ch. {read:0.#}" : "Not started";
    }
}
