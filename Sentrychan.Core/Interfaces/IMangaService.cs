using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>Library CRUD + reading-progress for tracked manga. Mirrors ISeriesService.</summary>
public interface IMangaService
{
    Task<List<Manga>> GetAllAsync(CancellationToken ct = default);
    Task<Manga?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Manga?> GetBySourceIdAsync(string source, string sourceId, CancellationToken ct = default);

    Task<Manga> AddAsync(Manga manga, CancellationToken ct = default);
    Task<bool> RemoveAsync(int id, CancellationToken ct = default);

    /// <summary>Sets reading progress to the highest chapter read (never lowers it).</summary>
    Task UpdateProgressAsync(int mangaId, double lastReadChapter, CancellationToken ct = default);

    /// <summary>Marks every cached chapter read and sets progress to the last chapter.</summary>
    Task MarkAllReadAsync(int mangaId, CancellationToken ct = default);

    /// <summary>Replaces the cached chapter list for a manga with a fresh one from the source.</summary>
    Task<List<MangaChapter>> SyncChaptersAsync(int mangaId, IEnumerable<MangaChapterInfo> chapters, CancellationToken ct = default);

    /// <summary>
    /// Saves reader position for a chapter, and marks it read when finished — which also
    /// advances the manga's overall reading progress.
    /// </summary>
    Task SaveReadingPositionAsync(int chapterId, int lastPage, bool markRead, CancellationToken ct = default);
}
