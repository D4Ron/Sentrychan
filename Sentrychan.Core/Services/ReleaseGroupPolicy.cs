using System.Text.Json;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>How strictly automatic downloads keep to the preferred release groups.</summary>
public enum GroupMode
{
    /// <summary>Any group; preferred ones only win when several release the same episode.</summary>
    Any,
    /// <summary>Wait for a preferred group, and take another one if none has it after <see cref="ReleaseGroupPolicy.PreferWait"/>.</summary>
    Prefer,
    /// <summary>Only preferred groups.</summary>
    Only,
}

/// <summary>What the monitor does with one release.</summary>
public enum GroupDecision { Take, Wait, Ignore }

/// <summary>
/// Which releases the automatic downloads take, by release group: the one list of preferred
/// groups (highest priority first) and a mode, both overridable per series. Applies to what the
/// app downloads by itself — the feed monitor and its catch-up search — never to something the user
/// picks by hand from Search or Latest.
/// </summary>
public static class ReleaseGroupPolicy
{
    public const string ModeKey = "ReleaseGroupMode";
    public const string GroupsKey = "PreferredReleaseGroups";

    /// <summary>The list that used to say which groups may auto-download; merged into <see cref="GroupsKey"/>.</summary>
    public const string RetiredAutoDownloadKey = "AutoDownloadGroups";

    /// <summary>AppConfig: when each (series, episode) was first seen from a group that isn't preferred.</summary>
    public const string SightingsKey = "ReleaseGroupSightings";

    public static readonly TimeSpan PreferWait = TimeSpan.FromHours(12);

    public const GroupMode DefaultMode = GroupMode.Prefer;

    public static GroupMode ParseMode(string? value, GroupMode fallback = DefaultMode) =>
        Enum.TryParse<GroupMode>(value, ignoreCase: true, out var m) ? m : fallback;

    public static List<string> SplitGroups(string? raw) =>
        (raw ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The groups and mode that apply to a series: its own when set, else the app's.</summary>
    public static (List<string> Groups, GroupMode Mode) For(Series series, List<string> globalGroups, GroupMode globalMode)
    {
        var groups = string.IsNullOrWhiteSpace(series.PreferredGroups) ? globalGroups : SplitGroups(series.PreferredGroups);
        var mode = string.IsNullOrWhiteSpace(series.GroupMode) ? globalMode : ParseMode(series.GroupMode, globalMode);
        return (groups, mode);
    }

    /// <summary>Position in the list (lower = better), or a large number when not in it.</summary>
    public static int Rank(string group, IReadOnlyList<string> groups)
    {
        if (string.IsNullOrWhiteSpace(group)) return 10_000;
        for (var i = 0; i < groups.Count; i++)
            if (group.Contains(groups[i], StringComparison.OrdinalIgnoreCase) ||
                groups[i].Contains(group, StringComparison.OrdinalIgnoreCase))
                return i;
        return 10_000;
    }

    public static bool IsPreferred(string group, IReadOnlyList<string> groups) => Rank(group, groups) < 10_000;

    /// <param name="firstSeen">When this episode was first seen from a group that isn't preferred.</param>
    public static GroupDecision Decide(GroupMode mode, string group, IReadOnlyList<string> groups, DateTime? firstSeen, DateTime nowUtc)
    {
        // No list means nothing to keep to.
        if (groups.Count == 0 || IsPreferred(group, groups)) return GroupDecision.Take;
        return mode switch
        {
            GroupMode.Any => GroupDecision.Take,
            GroupMode.Only => GroupDecision.Ignore,
            _ => firstSeen is { } seen && nowUtc - seen >= PreferWait ? GroupDecision.Take : GroupDecision.Wait,
        };
    }

    /// <summary>When the "Prefer" wait for an episode ends, for log lines and the UI.</summary>
    public static DateTime WaitEnds(DateTime firstSeen) => firstSeen + PreferWait;

    public static string Key(int malId, int episode) => $"{malId}:{episode}";

    public static Dictionary<string, GroupSighting> ReadSightings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, GroupSighting>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Kept for two weeks — long after any wait could matter.</summary>
    public static string WriteSightings(Dictionary<string, GroupSighting> sightings, DateTime nowUtc) =>
        JsonSerializer.Serialize(sightings.Where(s => nowUtc - s.Value.FirstSeen < TimeSpan.FromDays(14))
            .ToDictionary(s => s.Key, s => s.Value));
}

/// <summary>
/// An episode first seen from a group that isn't preferred, while waiting for one that is. The
/// release is kept too: a busy feed has moved on long before the 12 hours are up, so when the
/// wait ends this is what gets downloaded.
/// </summary>
public sealed record GroupSighting(DateTime FirstSeen, string Title, string Link);
