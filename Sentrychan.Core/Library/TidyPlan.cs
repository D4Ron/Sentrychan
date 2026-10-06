namespace Sentrychan.Core.Library;

public enum TidyItemStatus
{
    /// <summary>Will move when applied (if still ticked).</summary>
    Ready,
    /// <summary>Its new name is taken — by a file already there or by another item. Never overwritten.</summary>
    Collision,
    /// <summary>The season or episode can't be worked out with confidence; left alone.</summary>
    Unsure,
    /// <summary>Open, still downloading, owned by an unfinished torrent, or the series is excluded.</summary>
    Skipped,
}

/// <summary>A sidecar (subtitles, .nfo, thumbnail) that moves with its video.</summary>
public sealed record SidecarMove(string From, string To);

/// <summary>One video file in a tidy plan: where it is, where it would go, and why not if it won't.</summary>
public sealed class TidyItem
{
    public required string Source { get; init; }
    public string? Destination { get; init; }
    public TidyItemStatus Status { get; set; }
    public string? Note { get; set; }
    public int? SeriesId { get; init; }

    /// <summary>The show folder it was found in — the plan list groups by it.</summary>
    public required string ShowFolder { get; init; }

    public IReadOnlyList<SidecarMove> Sidecars { get; init; } = [];

    /// <summary>Ticked in the plan list. Only Ready items can be.</summary>
    public bool Selected { get; set; }
}

/// <summary>The whole "old path → new path" list for a library, before anything moves.</summary>
public sealed class TidyPlan
{
    public required string LibraryPath { get; init; }
    public required NamingTemplate Naming { get; init; }
    public List<TidyItem> Items { get; } = [];

    /// <summary>Files already where the template puts them.</summary>
    public int AlreadyTidy { get; set; }

    /// <summary>Whole folders left alone, and why ("Show — Don't tidy").</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Folders skipped because the user chose to leave them alone.</summary>
    public List<string> LeftAlone { get; } = [];

    public int Count(TidyItemStatus status) => Items.Count(i => i.Status == status);
}

/// <summary>A series as the planner needs it; decoupled from EF so plans can be built and tested anywhere.</summary>
public sealed record TidySeries(
    int Id,
    int MalId,
    string Title,
    int? Year,
    string? MediaType,
    int SeasonNumber,
    int? TotalEpisodes,
    bool Excluded = false,
    bool KeepFileNames = false)
{
    public bool IsMovie => string.Equals(MediaType, "Movie", StringComparison.OrdinalIgnoreCase);

    /// <summary>Its place in the show's folder from the season family (SeasonLayout), when known.</summary>
    public SeasonPlacement? Placement { get; init; }

    public int EffectiveSeason => Placement?.Season ?? Services.SeasonSearch.EffectiveSeason(Title, SeasonNumber);

    /// <summary>Where its episodes start in its season: 0 for a season's first part; null when not known.</summary>
    public int? EpisodeOffset => Placement is { } p ? p.EpisodeOffset : 0;

    public static TidySeries From(Models.Series s) => new(
        s.Id, s.MalId, s.Title, s.Year, s.MediaType, s.SeasonNumber, s.TotalEpisodes, s.TidyExcluded, s.KeepFileNames)
    {
        Placement = SeasonLayout.For(s),
    };
}
