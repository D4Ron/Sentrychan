using System;
using System.Reactive;
using ReactiveUI;
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

    private bool _isDownloaded;
    public bool IsDownloaded { get => _isDownloaded; set => this.RaiseAndSetIfChanged(ref _isDownloaded, value); }

    private bool _isDownloading;
    public bool IsDownloading { get => _isDownloading; set => this.RaiseAndSetIfChanged(ref _isDownloading, value); }

    public ReactiveCommand<Unit, Unit> MarkReadCommand { get; }
    public ReactiveCommand<Unit, Unit> ReadCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }

    public MangaChapterVm(
        MangaChapter chapter,
        Func<MangaChapterVm, System.Threading.Tasks.Task> onMarkRead,
        Action<MangaChapterVm> onRead,
        Func<MangaChapterVm, System.Threading.Tasks.Task> onDownload,
        bool canDownload = true)
    {
        Chapter = chapter;
        CanDownload = canDownload;
        _isRead = chapter.IsRead;
        _isDownloaded = !string.IsNullOrEmpty(chapter.DownloadedPath);
        MarkReadCommand = ReactiveCommand.CreateFromTask(() => onMarkRead(this));
        ReadCommand = ReactiveCommand.Create(() => onRead(this));
        DownloadCommand = ReactiveCommand.CreateFromTask(() => onDownload(this));
    }
}
