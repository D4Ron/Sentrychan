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
public class MangaDetailViewModel : ViewModelBase
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
        IMangaDownloadService downloader, Action onBack, Action<int> onRead)
    {
        Manga = manga;
        _mangaService = mangaService;
        _source = source;
        _downloader = downloader;
        _onRead = onRead;

        RefreshCommand     = ReactiveCommand.CreateFromTask(() => LoadChaptersAsync(forceRefresh: true));
        MarkAllReadCommand = ReactiveCommand.CreateFromTask(MarkAllReadAsync);
        BackCommand        = ReactiveCommand.Create(onBack);
        ContinueCommand    = ReactiveCommand.Create(Continue);
        DownloadAllCommand = ReactiveCommand.CreateFromTask(DownloadAllAsync);
    }

    private async Task DownloadChapterAsync(MangaChapterVm vm)
    {
        if (vm.IsDownloaded || vm.IsDownloading) return;
        vm.IsDownloading = true;
        try
        {
            var path = await _downloader.DownloadChapterAsync(Manga, vm.Chapter);
            vm.IsDownloaded = !string.IsNullOrEmpty(path);
            if (vm.IsDownloaded) vm.Chapter.DownloadedPath = path;
            else StatusMessage = "That chapter can't be downloaded (licensed / external).";
        }
        finally { vm.IsDownloading = false; }
    }

    private async Task DownloadAllAsync()
    {
        // Sequential so we don't hammer the source; each chapter is many image requests.
        foreach (var vm in Chapters.ToList())
        {
            if (vm.IsDownloaded) continue;
            await DownloadChapterAsync(vm);
        }
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
            // Newest first reads best in a list.
            foreach (var c in chapters.OrderByDescending(c => c.ChapterSort ?? double.MinValue))
                Chapters.Add(new MangaChapterVm(c, MarkReadAsync, vm => _onRead(vm.Chapter.Id), DownloadChapterAsync, !Manga.IsNovel));
            UpdateContinueState();
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
