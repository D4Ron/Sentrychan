using System;
using System.Reactive;
using ReactiveUI;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

public enum MangaReadingBucket { RecentlyAdded, Reading, Completed, PlanToRead }

/// <summary>
/// A library manga card. Computes reading status, unread count and a status-dot colour
/// the same way the anime SeriesCard surfaces airing status — so the manga library gets
/// the same at-a-glance QOL.
/// </summary>
public class MangaCardVm : ViewModelBase
{
    public Manga Manga { get; }

    public string Title => Manga.Title;
    public string CoverPath => Manga.CoverPath;
    public string Source => Manga.Source;

    public MangaReadingBucket Bucket { get; }

    /// <summary>Chapters the source lists beyond what's been read (0 if unknown/caught up).</summary>
    public int UnreadCount { get; }
    public bool HasUnread => UnreadCount > 0;
    public string UnreadBadge => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    public string ProgressText
    {
        get
        {
            var read = Manga.LastReadChapter;
            if (Manga.TotalChapters is { } total && total > 0)
                return $"Ch. {read:0.#} / {total:0.#}";
            return read > 0 ? $"Ch. {read:0.#}" : "Not started";
        }
    }

    /// <summary>Hex for the status dot — green reading, violet done, amber unread-new, muted unstarted.</summary>
    public string StatusColor => Bucket switch
    {
        MangaReadingBucket.Completed => "#8B5CF6",
        MangaReadingBucket.Reading   => "#00C853",
        _ when HasUnread             => "#FFB300",
        _                            => "#3A3A60"
    };

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> MarkAllReadCommand { get; }

    public MangaCardVm(Manga manga,
        Action<Manga> onOpen, Func<Manga, System.Threading.Tasks.Task> onRemove,
        Func<Manga, System.Threading.Tasks.Task> onMarkAllRead)
    {
        Manga = manga;
        Bucket = Classify(manga, out var unread);
        UnreadCount = unread;

        OpenCommand        = ReactiveCommand.Create(() => onOpen(manga));
        RemoveCommand      = ReactiveCommand.CreateFromTask(() => onRemove(manga));
        MarkAllReadCommand = ReactiveCommand.CreateFromTask(() => onMarkAllRead(manga));
    }

    private static MangaReadingBucket Classify(Manga m, out int unread)
    {
        var total = m.TotalChapters ?? 0;
        var read  = m.LastReadChapter;
        unread = total > read ? (int)Math.Ceiling(total - read) : 0;

        // Recently added takes precedence, mirroring the anime library.
        if ((DateTime.UtcNow - m.AddedAt).TotalDays <= 7) return MangaReadingBucket.RecentlyAdded;
        if (total > 0 && read >= total) return MangaReadingBucket.Completed;
        if (read > 0) return MangaReadingBucket.Reading;
        return MangaReadingBucket.PlanToRead;
    }
}
