using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Downloads manga chapters for offline reading. Manga pages are plain HTTP images
/// (not torrents), so this is its own downloader rather than the torrent queue.
/// </summary>
public interface IMangaDownloadService
{
    /// <summary>
    /// Downloads every page of a chapter into a per-chapter folder and returns that
    /// folder path (also written to MangaChapter.DownloadedPath). Returns null if the
    /// chapter has no in-app pages (licensed/external) or the download fails.
    /// </summary>
    Task<string?> DownloadChapterAsync(Manga manga, MangaChapter chapter,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Deletes a chapter's downloaded pages and clears its DownloadedPath.</summary>
    Task DeleteChapterDownloadAsync(MangaChapter chapter, CancellationToken ct = default);
}
