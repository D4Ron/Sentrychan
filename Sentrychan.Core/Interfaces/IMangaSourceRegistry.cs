namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Holds every available manga source and resolves one by name. Each tracked Manga
/// stores its Source, so detail/reader/download/update all route back to the right
/// connector through here.
/// </summary>
public interface IMangaSourceRegistry
{
    IReadOnlyList<IMangaSourceService> Sources { get; }

    /// <summary>The default browse source (MangaDex when present, else the first usable source).</summary>
    IMangaSourceService Default { get; }

    /// <summary>Resolves a source by name; falls back to Default when unknown.</summary>
    IMangaSourceService Get(string? sourceName);

    /// <summary>Adds a source discovered at runtime (a loaded source-pack plugin). No-op on duplicate names.</summary>
    void Add(IMangaSourceService source);
}
