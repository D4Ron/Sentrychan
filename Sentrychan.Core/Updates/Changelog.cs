using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentrychan.Core.Updates;

/// <summary>One update's notes. <see cref="Id"/> sorts in release order: the date, plus a letter for a second one that day.</summary>
public sealed record ChangelogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("changes")] IReadOnlyList<string> Changes,
    // Preview-only notes: features a stable build doesn't have yet.
    [property: JsonPropertyName("preview")] bool PreviewOnly = false)
{
    /// <summary>"11 October 2026", from an id like "2026-10-11" or "2026-10-11b".</summary>
    public string DateText => DateTime.TryParseExact(Id.Length >= 10 ? Id[..10] : Id, "yyyy-MM-dd",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
        ? d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : Id;
}

/// <summary>
/// "What's new": the notes shipped with the app (<c>Assets/changelog.json</c>, newest first), shown
/// once on the first start after an update. Which ones are new is decided by the id of the newest
/// note already seen, not by the app's version — the preview built from source keeps one version
/// number across builds.
/// </summary>
public static class Changelog
{
    public const string SeenKey = "ChangelogSeen";

    /// <summary>At most this many are shown at once: someone who skipped a lot of updates sees the latest.</summary>
    public const int MaxShown = 6;

    public static IReadOnlyList<ChangelogEntry> Parse(string json) =>
        JsonSerializer.Deserialize<List<ChangelogEntry>>(json) ?? [];

    /// <summary>The notes for this build, newest first.</summary>
    public static IReadOnlyList<ChangelogEntry> ForBuild(IEnumerable<ChangelogEntry> all, bool isPreview) =>
        all.Where(e => isPreview || !e.PreviewOnly).OrderByDescending(e => e.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// What to show at this start, and the id to remember afterwards. With nothing remembered, a
    /// fresh install shows nothing (it has no "before"); an install that was in use before notes
    /// existed shows the newest ones.
    /// </summary>
    public static (IReadOnlyList<ChangelogEntry> Show, string? Remember) Unseen(
        IReadOnlyList<ChangelogEntry> notes, string? lastSeen, bool usedBefore)
    {
        if (notes.Count == 0) return ([], null);
        var newest = notes[0].Id;
        if (lastSeen == null && !usedBefore) return ([], newest);
        var unseen = notes.Where(e => lastSeen == null || string.CompareOrdinal(e.Id, lastSeen) > 0).Take(MaxShown).ToList();
        return (unseen, unseen.Count > 0 || lastSeen == null ? newest : null);
    }
}
