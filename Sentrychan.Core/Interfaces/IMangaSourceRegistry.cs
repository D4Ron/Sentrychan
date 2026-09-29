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

    /// <summary>Takes out a source that went away at runtime (an uninstalled bridged extension).</summary>
    void Remove(IMangaSourceService source) { }

    /// <summary>A source by its stable id (<see cref="MangaSourceInfo.Id"/>) or name; null when none matches — no fallback.</summary>
    IMangaSourceService? Find(string? idOrName) =>
        string.IsNullOrEmpty(idOrName) ? null
        : Sources.FirstOrDefault(s => string.Equals(s.Info.Id, idOrName, StringComparison.Ordinal))
          ?? Sources.FirstOrDefault(s => string.Equals(s.SourceName, idOrName, StringComparison.OrdinalIgnoreCase));
}
