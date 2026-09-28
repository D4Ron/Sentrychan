using System;
using System.Reactive;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>One row in a manga's chapter list.</summary>
public class MangaChapterVm : ViewModelBase
{
    public MangaChapter Chapter { get; }

    public double? Sort => Chapter.ChapterSort;

    public string Heading
    {
        get
        {
            var num = string.IsNullOrEmpty(Chapter.ChapterNumber) ? "Oneshot" : $"Ch. {Chapter.ChapterNumber}";
            return string.IsNullOrWhiteSpace(Chapter.Title) ? num : $"{num} — {Chapter.Title}";
        }
    }

    public string SubLine
    {
        get
        {
            var bits = new System.Collections.Generic.List<string>();
            if (Chapter.Pages > 0) bits.Add($"{Chapter.Pages}p");
            if (!string.IsNullOrEmpty(Chapter.ScanlationGroup)) bits.Add(Chapter.ScanlationGroup!);
            if (Chapter.PublishedAt.HasValue) bits.Add(Chapter.PublishedAt.Value.ToLocalTime().ToString("MMM d, yyyy"));
            return string.Join("  ·  ", bits);
        }
    }

    /// <summary>Novels have no offline-download path yet, so their rows hide the download control.</summary>
    public bool CanDownload { get; }

    private bool _isRead;
    public bool IsRead { get => _isRead; set => this.RaiseAndSetIfChanged(ref _isRead, value); }

    // ── Download state ─────────────────────────────────────────────────
    // One of: not downloaded · queued · starting · downloading n/total · downloaded · failed.
    // Every state has its own visible control, so a row never looks idle while work is going on.

    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDownloaded, value);
            this.RaisePropertyChanged(nameof(ShowDownloadButton));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsInVault));
            this.RaisePropertyChanged(nameof(DoneLabel));
        }
    }

    private ChapterDownloadStatus? _status;

    /// <summary>Applies a status from the downloader. Call on the UI thread.</summary>
    public void Apply(ChapterDownloadStatus? status)
    {
        _status = status;
        if (status?.State == ChapterDownloadState.Done) IsDownloaded = true;
        // A cancelled chapter keeps the pages it saved; say so, and the next click resumes.
        if (status is { State: ChapterDownloadState.Cancelled, PagesDone: > 0 }) SetPartial(status.PagesDone, status.PagesTotal);
        ConfirmingRemove = false;
        foreach (var p in new[] { nameof(State), nameof(IsQueued), nameof(IsDownloading), nameof(IsBusy), nameof(IsFailed), nameof(ShowDownloadButton),
                                  nameof(Progress), nameof(IsProgressKnown), nameof(ProgressText), nameof(ErrorText) })
            this.RaisePropertyChanged(p);
    }

    public ChapterDownloadState? State => _status?.State;
    public bool IsQueued => _status?.State == ChapterDownloadState.Queued;
    public bool IsDownloading => _status?.State is ChapterDownloadState.Starting or ChapterDownloadState.Downloading;
    public bool IsFailed => _status?.State == ChapterDownloadState.Failed && !IsDownloaded;
    public bool IsBusy => IsQueued || IsDownloading;
    public bool ShowDownloadButton => !IsDownloaded && !IsBusy && !IsFailed;

    public bool IsProgressKnown => _status is { PagesTotal: > 0 };
    public double Progress => _status is { PagesTotal: > 0 } s ? 100.0 * s.PagesDone / s.PagesTotal : 0;
    public string ProgressText => _status is { PagesTotal: > 0 } s ? $"{s.PagesDone} / {s.PagesTotal} pages" : "Finding pages…";
    public string ErrorText => _status?.Error ?? "Download failed.";

    private int _pagesSaved, _pagesExpected;

    /// <summary>A download that stopped part-way: pages already saved are kept and the next one resumes.</summary>
    public void SetPartial(int saved, int expected)
    {
        _pagesSaved = saved;
        _pagesExpected = expected;
        this.RaisePropertyChanged(nameof(DownloadLabel));
        this.RaisePropertyChanged(nameof(DownloadTip));
    }

    public string DownloadLabel => _pagesSaved <= 0 ? "↓ Download"
        : _pagesExpected > 0 ? $"↓ Resume · {_pagesSaved}/{_pagesExpected}" : $"↓ Resume · {_pagesSaved} saved";

    public string DownloadTip => _pagesSaved <= 0
        ? "Save this chapter so it reads offline"
        : "An earlier download stopped part-way. Resuming fetches only the missing pages.";

    public bool IsInVault => Chapter.DownloadedPath?.StartsWith(Core.Services.MangaDownloadService.VaultPathPrefix) == true;
    public string DoneLabel => IsInVault ? "✓ In vault" : "✓ Downloaded";

    private bool _confirmingRemove;
    public bool ConfirmingRemove { get => _confirmingRemove; set => this.RaiseAndSetIfChanged(ref _confirmingRemove, value); }

    public ReactiveCommand<Unit, Unit> MarkReadCommand { get; }
    public ReactiveCommand<Unit, Unit> ReadCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> KeepDownloadCommand { get; }

    public MangaChapterVm(
        MangaChapter chapter,
        Func<MangaChapterVm, System.Threading.Tasks.Task> onMarkRead,
        Action<MangaChapterVm> onRead,
        Func<MangaChapterVm, System.Threading.Tasks.Task> onDownload,
        bool canDownload = true,
        Action<MangaChapterVm>? onCancel = null,
        Func<MangaChapterVm, System.Threading.Tasks.Task>? onRemove = null)
    {
        Chapter = chapter;
        CanDownload = canDownload;
        _isRead = chapter.IsRead;
        _isDownloaded = !string.IsNullOrEmpty(chapter.DownloadedPath);
        MarkReadCommand = ReactiveCommand.CreateFromTask(() => onMarkRead(this));
        ReadCommand = ReactiveCommand.Create(() => onRead(this));
        DownloadCommand = ReactiveCommand.CreateFromTask(() => onDownload(this));
        CancelDownloadCommand = ReactiveCommand.Create(() => onCancel?.Invoke(this));
        // Two steps: the first click asks, the second removes.
        RemoveDownloadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!ConfirmingRemove) { ConfirmingRemove = true; return; }
            ConfirmingRemove = false;
            if (onRemove != null) await onRemove(this);
        });
        KeepDownloadCommand = ReactiveCommand.Create(() => { ConfirmingRemove = false; });
    }
}
