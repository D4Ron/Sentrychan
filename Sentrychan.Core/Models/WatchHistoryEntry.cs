using System;

namespace Sentrychan.Core.Models;

public class WatchHistoryEntry
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    public DateTime WatchedAt { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "Manual"; // "Manual" or "Watch Party"

    // Navigation
    public Series? Series { get; set; }
}

public static class WatchSource
{
    public const string Manual = "Manual";
    public const string WatchParty = "Watch Party";
}
