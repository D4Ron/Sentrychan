using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// In-app manga reader. Paged (one image at a time) or long-strip/webtoon (all pages
/// stacked, vertical scroll). Persists per-chapter page position for resume, marks a
/// chapter read when finished, and flows into the next/previous chapter at the edges.
/// Licensed/externally-hosted chapters have no in-app pages — those show an
/// "open on the web" fallback.
/// </summary>
public class MangaReaderViewModel : ViewModelBase
{
    private readonly IMangaService _mangaService;
    private readonly IMangaSourceService _source;
    private readonly Action _onClose;
    private readonly List<MangaChapter> _chapters; // ascending
    private CancellationTokenSource? _loadCts;

    // Preview mode: reading a source result that isn't in the library yet. Chapter ids
    // are transient (0), so reading position isn't persisted until the user adds it.
    private readonly IReadOnlyList<MangaChapterInfo>? _previewInfos;
    private readonly Action? _onAdded;

    private readonly IConfigService? _config;

    /// <summary>AppConfig key for the remembered reader mode (true = webtoon/long-strip).</summary>
    public const string ReaderModeKey = "MangaReaderWebtoon";

    public Manga Manga { get; }

    /// <summary>Source name, for AsyncImage to pick the right Referer when streaming pages.</summary>
    public string SourceName => Manga.Source;

    /// <summary>Text (novel) mode vs image (manga) mode — fixed per title.</summary>
    public bool IsNovel => Manga.IsNovel;

    /// <summary>Current chapter's prose (novel mode); paragraphs separated by blank lines.</summary>
    private string _chapterText = string.Empty;
    public string ChapterText { get => _chapterText; private set => this.RaiseAndSetIfChanged(ref _chapterText, value); }

    /// <summary>AppConfig key for the remembered novel reading font size.</summary>
    public const string NovelFontKey = "NovelReaderFontSize";
    private double _novelFontSize = 17;
    public double NovelFontSize
    {
        get => _novelFontSize;
        private set { this.RaiseAndSetIfChanged(ref _novelFontSize, value); this.RaisePropertyChanged(nameof(NovelLineHeight)); }
    }

    /// <summary>Comfortable line spacing that scales with the chosen font size.</summary>
    public double NovelLineHeight => Math.Round(_novelFontSize * 1.6);

    /// <summary>All pages of the current chapter (long-strip binds to this).</summary>
    public ObservableCollection<string> Pages { get; } = [];

    private int _chapterIndex;
    private MangaChapter Current => _chapters[_chapterIndex];

    private int _pageIndex;
    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pageIndex, value);
            this.RaisePropertyChanged(nameof(CurrentPageUrl));
            this.RaisePropertyChanged(nameof(PageIndicator));
            PrefetchAround();
        }
    }

    /// <summary>Preloads the next couple of pages (and the previous) so paging is instant.</summary>
    private void PrefetchAround()
    {
        if (IsLongStrip) return; // long-strip renders all pages already
        foreach (var i in new[] { _pageIndex + 1, _pageIndex + 2, _pageIndex - 1 })
            if (i >= 0 && i < Pages.Count)
                Sentrychan.UI.Controls.AsyncImage.Prefetch(Pages[i], SourceName);
    }

    public string? CurrentPageUrl =>
        Pages.Count > 0 && _pageIndex >= 0 && _pageIndex < Pages.Count ? Pages[_pageIndex] : null;

    private bool _isLongStrip;
    public bool IsLongStrip
    {
        get => _isLongStrip;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLongStrip, value);
            this.RaisePropertyChanged(nameof(IsPaged));
            this.RaisePropertyChanged(nameof(ShowPagedImages));
            this.RaisePropertyChanged(nameof(ShowWebtoonImages));
        }
    }
    public bool IsPaged => !_isLongStrip;

    // Image panels never show for a novel (text mode), whatever the image sub-mode is.
    public bool ShowPagedImages   => IsPaged && !IsNovel;
    public bool ShowWebtoonImages => IsLongStrip && !IsNovel;

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private bool _isExternalOnly;
    public bool IsExternalOnly { get => _isExternalOnly; set => this.RaiseAndSetIfChanged(ref _isExternalOnly, value); }

    // ── Preview-mode reminder state ─────────────────────────────────
    private bool _isPreview;
    public bool IsPreview
    {
        get => _isPreview;
        private set { this.RaiseAndSetIfChanged(ref _isPreview, value); this.RaisePropertyChanged(nameof(ShowPreviewBanner)); }
    }

    private bool _previewHidden;
    /// <summary>The reminder shows only while previewing and not dismissed.</summary>
    public bool ShowPreviewBanner => _isPreview && !_previewHidden;

    // ── Paged-mode zoom ─────────────────────────────────────────────
    private double _zoomLevel = 1.0;
    public double ZoomLevel { get => _zoomLevel; private set => this.RaiseAndSetIfChanged(ref _zoomLevel, value); }

    private bool _isAdding;
    public bool IsAdding { get => _isAdding; private set => this.RaiseAndSetIfChanged(ref _isAdding, value); }

    private bool _addedToLibrary;
    public bool AddedToLibrary { get => _addedToLibrary; private set => this.RaiseAndSetIfChanged(ref _addedToLibrary, value); }

    private string _chapterHeading = string.Empty;
    public string ChapterHeading { get => _chapterHeading; set => this.RaiseAndSetIfChanged(ref _chapterHeading, value); }

    public string PageIndicator => Pages.Count == 0 ? "" : $"{_pageIndex + 1} / {Pages.Count}";

    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenExternalCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> SetPagedCommand { get; }
    public ReactiveCommand<Unit, Unit> SetWebtoonCommand { get; }
    public ReactiveCommand<Unit, Unit> NextChapterCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevChapterCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }
    public ReactiveCommand<Unit, Unit> HidePreviewCommand { get; }

    public MangaReaderViewModel(
        Manga manga, List<MangaChapter> chaptersAscending, int startChapterIndex,
        IMangaService mangaService, IMangaSourceService source, Action onClose,
        bool isPreview = false, IReadOnlyList<MangaChapterInfo>? previewInfos = null, Action? onAdded = null,
        bool startLongStrip = false, IConfigService? config = null, double novelFontSize = 17)
    {
        Manga = manga;
        _chapters = chaptersAscending;
        _chapterIndex = Math.Clamp(startChapterIndex, 0, Math.Max(0, chaptersAscending.Count - 1));
        _mangaService = mangaService;
        _source = source;
        _onClose = onClose;
        _isPreview = isPreview;
        _previewInfos = previewInfos;
        _onAdded = onAdded;
        _config = config;
        _isLongStrip = startLongStrip; // honour the remembered mode
        _novelFontSize = Math.Clamp(novelFontSize, 12, 30);

        NextPageCommand   = ReactiveCommand.CreateFromTask(NextPageAsync);
        PrevPageCommand   = ReactiveCommand.CreateFromTask(PrevPageAsync);
        ToggleModeCommand = ReactiveCommand.Create(() => SetMode(!IsLongStrip));
        SetPagedCommand   = ReactiveCommand.Create(() => SetMode(false));
        SetWebtoonCommand = ReactiveCommand.Create(() => SetMode(true));
        NextChapterCommand = ReactiveCommand.CreateFromTask(() => GoToChapterAsync(_chapterIndex + 1, fromStart: true));
        PrevChapterCommand = ReactiveCommand.CreateFromTask(() => GoToChapterAsync(_chapterIndex - 1, fromStart: true));
        IncreaseFontCommand = ReactiveCommand.Create(() => SetFont(_novelFontSize + 1));
        DecreaseFontCommand = ReactiveCommand.Create(() => SetFont(_novelFontSize - 1));
        ZoomInCommand     = ReactiveCommand.Create(() => ZoomBy(0.25));
        ZoomOutCommand    = ReactiveCommand.Create(() => ZoomBy(-0.25));
        ResetZoomCommand  = ReactiveCommand.Create(() => SetZoom(1.0));
        HidePreviewCommand = ReactiveCommand.Create(() =>
            { _previewHidden = true; this.RaisePropertyChanged(nameof(ShowPreviewBanner)); });
        CloseCommand      = ReactiveCommand.Create(Close);
        OpenExternalCommand = ReactiveCommand.Create(OpenExternal);
        AddToLibraryCommand = ReactiveCommand.CreateFromTask(AddToLibraryAsync);
    }

    /// <summary>Switches the reader mode and remembers the choice for next time.</summary>
    private void SetMode(bool longStrip)
    {
        if (IsLongStrip == longStrip) return;
        IsLongStrip = longStrip;
        if (_config != null) _ = _config.SetValueAsync(ReaderModeKey, longStrip);
    }

    /// <summary>Adjusts (and remembers) the novel reading font size.</summary>
    private void SetFont(double size)
    {
        size = Math.Clamp(size, 12, 30);
        if (Math.Abs(size - _novelFontSize) < 0.1) return;
        NovelFontSize = size;
        if (_config != null) _ = _config.SetValueAsync(NovelFontKey, size);
    }

    /// <summary>Paged-mode zoom (1× fits the window; up to 4×). Called by buttons and Ctrl+wheel.</summary>
    public void ZoomBy(double delta) => SetZoom(_zoomLevel + delta);
    private void SetZoom(double z)
    {
        z = Math.Clamp(z, 1.0, 4.0);
        if (Math.Abs(z - _zoomLevel) < 0.001) return;
        ZoomLevel = z;
    }

    public Task InitializeAsync() => LoadChapterAsync(_chapterIndex, resume: true);

    private async Task LoadChapterAsync(int index, bool resume)
    {
        if (index < 0 || index >= _chapters.Count) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _chapterIndex = index;
        IsLoading = true;
        IsExternalOnly = false;
        ZoomLevel = 1.0; // each chapter starts fit-to-window
        Pages.Clear();
        ChapterText = string.Empty;
        var chapter = Current;
        ChapterHeading = HeadingFor(chapter, IsNovel);

        try
        {
            // Novel: fetch the chapter's prose and render as text (no image pages).
            if (IsNovel)
            {
                var text = await _source.GetChapterTextAsync(chapter.SourceId, ct);
                if (ct.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(text)) { IsExternalOnly = true; return; }
                ChapterText = text!;
                await PersistAsync(markRead: false); // opening a chapter isn't "finished"
                return;
            }

            // Prefer downloaded pages (offline, instant); fall back to streaming.
            var urls = LocalPagesOrNull(chapter) ?? await _source.GetPageUrlsAsync(chapter.SourceId, dataSaver: false, ct);
            if (ct.IsCancellationRequested) return;

            if (urls.Count == 0)
            {
                IsExternalOnly = true;
                PageIndex = 0;
                this.RaisePropertyChanged(nameof(PageIndicator));
                return;
            }

            foreach (var u in urls) Pages.Add(u);

            var start = resume ? Math.Clamp(chapter.LastReadPage, 0, urls.Count - 1) : 0;
            PageIndex = start;
            this.RaisePropertyChanged(nameof(PageIndicator));
            this.RaisePropertyChanged(nameof(CurrentPageUrl));

            // Landing directly on the final page (or a 1-page chapter) counts as read.
            await PersistAsync(markRead: PageIndex >= urls.Count - 1);
        }
        catch (OperationCanceledException) { /* superseded */ }
        finally { IsLoading = false; }
    }

    private static string HeadingFor(MangaChapter c, bool novel)
    {
        // Novels usually carry a real chapter title ("1. Good Morning Brother"); fall back to number.
        if (novel && !string.IsNullOrWhiteSpace(c.Title)) return c.Title!;
        return string.IsNullOrEmpty(c.ChapterNumber) ? "Oneshot" : $"Chapter {c.ChapterNumber}";
    }

    /// <summary>Downloaded chapters read from disk — returns sorted local image paths, or null.</summary>
    private static List<string>? LocalPagesOrNull(MangaChapter chapter)
    {
        if (string.IsNullOrEmpty(chapter.DownloadedPath) || !System.IO.Directory.Exists(chapter.DownloadedPath))
            return null;

        var files = System.IO.Directory.EnumerateFiles(chapter.DownloadedPath)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return files.Count > 0 ? files : null;
    }

    private async Task NextPageAsync()
    {
        // Novel: "next" moves to the next chapter (and marks this one read).
        if (IsNovel)
        {
            await PersistAsync(markRead: true);
            await GoToChapterAsync(_chapterIndex + 1, fromStart: true);
            return;
        }

        if (IsExternalOnly) { await GoToChapterAsync(_chapterIndex + 1, fromStart: true); return; }

        if (_pageIndex < Pages.Count - 1)
        {
            PageIndex++;
            await PersistAsync(markRead: _pageIndex >= Pages.Count - 1);
        }
        else
        {
            // Past the last page → mark read and roll into the next chapter.
            await PersistAsync(markRead: true);
            await GoToChapterAsync(_chapterIndex + 1, fromStart: true);
        }
    }

    private async Task PrevPageAsync()
    {
        if (IsNovel) { await GoToChapterAsync(_chapterIndex - 1, fromStart: true); return; }

        if (_pageIndex > 0) { PageIndex--; await PersistAsync(markRead: false); }
        else await GoToChapterAsync(_chapterIndex - 1, fromStart: false);
    }

    private async Task GoToChapterAsync(int index, bool fromStart)
    {
        if (index < 0 || index >= _chapters.Count) return;
        // When paging backwards into the previous chapter, open it at its last page.
        await LoadChapterAsync(index, resume: !fromStart);
        if (!fromStart && Pages.Count > 0)
        {
            PageIndex = Pages.Count - 1;
            this.RaisePropertyChanged(nameof(PageIndicator));
        }
    }

    /// <summary>Called by the view when the long-strip / novel text scrolls to the bottom.</summary>
    public async void OnReachedEnd()
    {
        if ((IsLongStrip && Pages.Count > 0) || (IsNovel && !string.IsNullOrEmpty(ChapterText)))
            await PersistAsync(markRead: true);
    }

    private async Task PersistAsync(bool markRead)
    {
        // Transient preview chapters have id 0 — nothing to persist until it's added.
        if (Current.Id == 0) return;
        try { await _mangaService.SaveReadingPositionAsync(Current.Id, _pageIndex, markRead); }
        catch { /* progress save is best-effort */ }
    }

    /// <summary>
    /// Preview-mode "add to library": persists the manga, gives the in-memory chapters
    /// their real DB ids so reading progress starts tracking from here on, and clears the
    /// reminder banner. Reading continues uninterrupted either way.
    /// </summary>
    private async Task AddToLibraryAsync()
    {
        if (!IsPreview || IsAdding) return;
        IsAdding = true;
        try
        {
            var added = await _mangaService.AddAsync(Manga);
            Manga.Id = added.Id;

            if (_previewInfos is { Count: > 0 })
            {
                var synced = await _mangaService.SyncChaptersAsync(added.Id, _previewInfos);
                var idBySource = synced
                    .GroupBy(c => c.SourceId)
                    .ToDictionary(g => g.Key, g => g.First().Id);
                foreach (var c in _chapters)
                    if (idBySource.TryGetValue(c.SourceId, out var id)) c.Id = id;
            }

            IsPreview = false;
            AddedToLibrary = true;
            _onAdded?.Invoke();
            await PersistAsync(markRead: false); // carry the current position over

            // Let the confirmation linger briefly, then get out of the reader's way.
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AddedToLibrary = false);
            });
        }
        catch { /* stay in the reader even if the add failed */ }
        finally { IsAdding = false; }
    }

    private void OpenExternal()
    {
        try
        {
            var url = _source.GetChapterWebUrl(Current.SourceId);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void Close()
    {
        _loadCts?.Cancel();
        _onClose();
    }
}
