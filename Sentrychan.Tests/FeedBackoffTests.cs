using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public class FeedBackoffTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc);

    private static RssFeed Feed(int failures, TimeSpan sinceLastCheck) =>
        new() { Url = "https://example.test/rss", ConsecutiveFailures = failures, LastCheckedAt = Now - sinceLastCheck };

    [Fact]
    public void A_healthy_or_hiccuping_feed_is_checked_every_time()
    {
        Assert.True(FeedBackoff.IsDue(Feed(0, TimeSpan.FromMinutes(1)), Now));
        Assert.True(FeedBackoff.IsDue(Feed(2, TimeSpan.FromMinutes(1)), Now));
    }

    [Fact]
    public void A_feed_down_for_a_while_waits_longer_but_is_never_given_up()
    {
        Assert.False(FeedBackoff.IsDue(Feed(5, TimeSpan.FromMinutes(15)), Now));
        Assert.True(FeedBackoff.IsDue(Feed(5, TimeSpan.FromMinutes(30)), Now));
        Assert.False(FeedBackoff.IsDue(Feed(12, TimeSpan.FromHours(1)), Now));
        Assert.True(FeedBackoff.IsDue(Feed(12, TimeSpan.FromHours(2)), Now));
        Assert.False(FeedBackoff.IsDue(Feed(500, TimeSpan.FromHours(5)), Now));
        Assert.True(FeedBackoff.IsDue(Feed(500, TimeSpan.FromHours(6)), Now));
    }

    [Fact]
    public void A_feed_never_checked_is_due()
    {
        Assert.True(FeedBackoff.IsDue(new RssFeed { Url = "https://example.test/rss", ConsecutiveFailures = 50 }, Now));
    }
}
