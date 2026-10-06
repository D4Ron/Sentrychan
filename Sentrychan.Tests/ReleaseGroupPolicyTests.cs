using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public class ReleaseGroupPolicyTests
{
    private static readonly List<string> Mine = ["GroupA", "GroupB"];
    private static readonly DateTime Now = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(GroupMode.Any, "GroupC", GroupDecision.Take)]
    [InlineData(GroupMode.Only, "GroupC", GroupDecision.Ignore)]
    [InlineData(GroupMode.Prefer, "GroupC", GroupDecision.Wait)]
    [InlineData(GroupMode.Only, "GroupB", GroupDecision.Take)]
    [InlineData(GroupMode.Prefer, "groupa", GroupDecision.Take)]
    public void Each_mode_treats_other_groups_its_own_way(GroupMode mode, string group, GroupDecision expected)
    {
        Assert.Equal(expected, ReleaseGroupPolicy.Decide(mode, group, Mine, firstSeen: null, Now));
    }

    [Fact]
    public void Prefer_takes_another_group_once_the_wait_is_over()
    {
        Assert.Equal(GroupDecision.Wait, ReleaseGroupPolicy.Decide(GroupMode.Prefer, "GroupC", Mine, Now.AddHours(-11), Now));
        Assert.Equal(GroupDecision.Take, ReleaseGroupPolicy.Decide(GroupMode.Prefer, "GroupC", Mine, Now.AddHours(-12), Now));
    }

    [Fact]
    public void Without_a_list_there_is_nothing_to_keep_to()
    {
        Assert.Equal(GroupDecision.Take, ReleaseGroupPolicy.Decide(GroupMode.Only, "GroupC", [], null, Now));
    }

    [Fact]
    public void A_series_can_have_its_own_groups_and_mode()
    {
        var own = new Series { PreferredGroups = "Solo", GroupMode = "Only" };
        var (groups, mode) = ReleaseGroupPolicy.For(own, Mine, GroupMode.Prefer);
        Assert.Equal(["Solo"], groups);
        Assert.Equal(GroupMode.Only, mode);

        var follows = new Series();
        Assert.Equal((Mine, GroupMode.Prefer), ReleaseGroupPolicy.For(follows, Mine, GroupMode.Prefer));
    }

    [Fact]
    public void The_wait_keeps_the_first_release_seen_and_hands_it_over_when_it_ends()
    {
        var series = new Series { MalId = 7, Title = "Show", LastEpisodeNumber = 4 };
        var started = new Dictionary<string, GroupSighting>();
        var first = new RssMonitorService.GroupRule(Mine, GroupMode.Prefer, started, NullLogger.Instance);

        Assert.Equal(GroupDecision.Wait, first.Decide(series, 5, "[GroupC] Show - 05 (1080p).mkv", "magnet:?xt=c", out _));
        Assert.Equal(GroupDecision.Wait, first.Decide(series, 5, "[GroupD] Show - 05 (1080p).mkv", "magnet:?xt=d", out _));
        Assert.True(first.Changed);
        Assert.Empty(first.WaitsOver([series]));

        // Persisted and read back by a check 12 hours later.
        var json = first.SaveSightings();
        var stored = ReleaseGroupPolicy.ReadSightings(json);
        stored["7:5"] = stored["7:5"] with { FirstSeen = stored["7:5"].FirstSeen.AddHours(-12) };
        var later = new RssMonitorService.GroupRule(Mine, GroupMode.Prefer, stored, NullLogger.Instance);
        var (s, ep, sighting) = Assert.Single(later.WaitsOver([series]));
        Assert.Equal((7, 5, "magnet:?xt=c"), (s.MalId, ep, sighting.Link));

        // Once downloaded (or the series has it), the wait is forgotten.
        later.Done(7, 5);
        Assert.Empty(later.WaitsOver([series]));
    }

    [Fact]
    public void A_preferred_release_during_the_wait_ends_it()
    {
        var series = new Series { MalId = 7, Title = "Show", LastEpisodeNumber = 4 };
        var sightings = new Dictionary<string, GroupSighting>();
        var rule = new RssMonitorService.GroupRule(Mine, GroupMode.Prefer, sightings, NullLogger.Instance);
        rule.Decide(series, 5, "[GroupC] Show - 05.mkv", "c", out _);
        Assert.Equal(GroupDecision.Take, rule.Decide(series, 5, "[GroupA] Show - 05.mkv", "a", out var rank));
        Assert.Equal(0, rank);
        rule.Done(7, 5);
        Assert.Empty(sightings);
    }

    [Fact]
    public void Waits_for_episodes_the_series_already_has_are_dropped()
    {
        var series = new Series { MalId = 7, Title = "Show", LastEpisodeNumber = 6 };
        var sightings = new Dictionary<string, GroupSighting> { ["7:5"] = new(Now, "t", "l"), ["9:1"] = new(Now, "t", "l") };
        var rule = new RssMonitorService.GroupRule(Mine, GroupMode.Prefer, sightings, NullLogger.Instance);
        rule.Forget([series]);
        Assert.Empty(sightings);
    }
}
