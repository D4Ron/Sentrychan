using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

public enum ChapterDownloadState { Queued, Starting, Downloading, Done, Failed, Cancelled }

/// <summary>
/// Where a chapter download stands. <see cref="PagesTotal"/> is 0 until the page list is known.
/// </summary>
public sealed record ChapterDownloadStatus(
    int ChapterId, int MangaId, ChapterDownloadState State,
    int PagesDone = 0, int PagesTotal = 0, string? Error = null)
{
    public bool IsActive => State is ChapterDownloadState.Queued or ChapterDownloadState.Starting or ChapterDownloadState.Downloading;
}

/// <summary>
/// Downloads manga chapters for offline reading. Manga pages are plain HTTP images
/// (not torrents), so this is its own downloader rather than the torrent queue.
/// </summary>
public interface IMangaDownloadService
{
    /// <summary>
    /// Queues a chapter and completes when it has downloaded. Returns the stored location
    /// (also written to MangaChapter.DownloadedPath), or null if the chapter has no in-app
    /// pages (licensed/external) or was cancelled. Throws on a real failure.
    /// </summary>
    Task<string?> DownloadChapterAsync(Manga manga, MangaChapter chapter,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Raised on any thread whenever a chapter's download status changes.</summary>
    event Action<ChapterDownloadStatus>? StatusChanged;

    /// <summary>The latest status of a chapter queued or downloaded this session, if any.</summary>
    ChapterDownloadStatus? GetStatus(int chapterId);

    /// <summary>Stops a queued or running chapter download. Pages already saved are kept.</summary>
    void Cancel(int chapterId);

    /// <summary>Stops every queued or running download of one manga.</summary>
    void CancelAll(int mangaId);

    /// <summary>
    /// Moves adult chapters downloaded as plain folders into the vault. Returns the count moved.
    /// </summary>
    Task<int> MoveAdultDownloadsIntoVaultAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>Deletes a chapter's downloaded pages and clears its DownloadedPath.</summary>
    Task DeleteChapterDownloadAsync(MangaChapter chapter, CancellationToken ct = default);
}
