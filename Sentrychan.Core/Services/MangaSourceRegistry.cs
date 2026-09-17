using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

/// <summary>
/// Holds every available manga/novel source. Built-in sources (Local) come from DI; the
/// rest are source-pack plugins loaded at runtime and added via <see cref="Add"/>, so the
/// shipped app carries no sources of its own.
/// </summary>
public class MangaSourceRegistry : IMangaSourceRegistry
{
    private readonly List<IMangaSourceService> _sources;

    public IReadOnlyList<IMangaSourceService> Sources => _sources;

    public MangaSourceRegistry(IEnumerable<IMangaSourceService> sources)
        => _sources = sources.ToList();

    public void Add(IMangaSourceService source)
    {
        if (source == null) return;
        if (_sources.Any(s => string.Equals(s.SourceName, source.SourceName, StringComparison.OrdinalIgnoreCase)))
            return; // already present (or a duplicate plugin) — keep the first
        _sources.Add(source);
    }

    // MangaDex preferred; else the first ordinary (non-adult, non-novel) source; else anything.
    public IMangaSourceService Default =>
        _sources.FirstOrDefault(s => s.SourceName == "MangaDex")
        ?? _sources.FirstOrDefault(s => !s.IsAdultSource && !s.IsNovel)
        ?? _sources.First();

    public IMangaSourceService Get(string? sourceName) =>
        _sources.FirstOrDefault(s => string.Equals(s.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
        ?? Default;
}
