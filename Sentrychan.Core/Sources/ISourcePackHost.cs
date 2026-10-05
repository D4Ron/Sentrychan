namespace Sentrychan.Core.Sources;

/// <summary>What one source pack file in the sources folder gave the app.</summary>
public sealed record SourcePackStatus(
    string FileName,
    bool Loaded,
    string? Error,
    IReadOnlyList<string> MangaSources,
    IReadOnlyList<string> ReleaseProviders,
    int Schedules,
    bool NeedsRestart)
{
    public string Describe()
    {
        if (!Loaded) return Error ?? "not loaded";
        if (NeedsRestart) return "a newer copy is installed — restart Sentrychan to use it";
        var parts = new List<string>();
        if (ReleaseProviders.Count > 0) parts.Add($"episode search ({string.Join(", ", ReleaseProviders)})");
        if (MangaSources.Count > 0) parts.Add(MangaSources.Count == 1 ? "1 manga source" : $"{MangaSources.Count} manga sources");
        if (Schedules > 0) parts.Add("airing schedule");
        return parts.Count == 0 ? "loaded, but it adds nothing this app knows" : string.Join(", ", parts);
    }
}

/// <summary>
/// The app side of source packs: which are loaded and what they brought, and loading packs that
/// were installed while the app runs (so an import works without a restart). Implemented by the
/// app's plugin loader; Core only asks.
/// </summary>
public interface ISourcePackHost
{
    IReadOnlyList<SourcePackStatus> Packs { get; }

    /// <summary>
    /// Loads packs in the sources folder that aren't loaded yet and returns every pack's status.
    /// A pack replaced on disk while loaded can't be swapped in place: it says it needs a restart.
    /// </summary>
    IReadOnlyList<SourcePackStatus> LoadNewPacks();
}
