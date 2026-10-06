using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>
/// When a failing feed is checked again. A feed used to be switched off for good after 10 failed
/// checks in a row — a site down for an afternoon (the user's main feed, 2026-10-04) then stayed off
/// until someone noticed. Now it's only checked less often, and a success puts it straight back.
/// A manual check always includes it.
/// </summary>
public static class FeedBackoff
{
    /// <summary>Failures after which the user is told the feed isn't answering (once).</summary>
    public const int NoticeAfter = 10;

    public static TimeSpan Wait(int failures) => failures switch
    {
        < 3 => TimeSpan.Zero,              // a hiccup: keep the normal rhythm
        < NoticeAfter => TimeSpan.FromMinutes(30),
        < 30 => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(6),
    };

    public static bool IsDue(RssFeed feed, DateTime nowUtc) =>
        feed.LastCheckedAt is not { } last || nowUtc - last >= Wait(feed.ConsecutiveFailures);
}
