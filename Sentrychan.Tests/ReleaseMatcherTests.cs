using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public class ReleaseMatcherTests
{
    /// <summary>
    /// The offline database in miniature: titles resolve by exact (normalised) name, season-aware the
    /// way the real resolver tries "Title 3rd Season" first; release names are parsed for real.
    /// </summary>
    private sealed class FakeResolver(params ResolvedAnime[][] chains) : ITitleResolverService
    {
        private static readonly TitleResolverService Parser = new(NullLogger<TitleResolverService>.Instance);
        private readonly Dictionary<string, ResolvedAnime> _byName = new();

        public FakeResolver Name(string name, ResolvedAnime entry) { _byName[Key(name)] = entry; return this; }

        private static string Key(string s) =>
            string.Join(' ', new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ParsedRelease ParseRelease(string releaseName) => Parser.ParseRelease(releaseName);
        public ResolvedAnime? ResolveRelease(string releaseName)
        {
            var p = ParseRelease(releaseName);
            return ResolveTitle(p.Title, p.Season);
        }
        public ResolvedAnime? ResolveTitle(string title, int season = 1) =>
            (season > 1 && _byName.TryGetValue(Key($"{title} {Ordinal(season)} season"), out var s) ? s : null)
            ?? (_byName.TryGetValue(Key(title), out var t) ? t : null);
        public ResolvedAnime? GetByMalId(int malId) => chains.SelectMany(c => c).FirstOrDefault(a => a.MalId == malId);
        public IReadOnlyList<ResolvedAnime> GetSeasonChain(int malId) =>
            chains.FirstOrDefault(c => c.Any(a => a.MalId == malId)) ?? (GetByMalId(malId) is { } x ? [x] : []);
        public List<ResolvedAnime> Search(string query, int limit = 20) => [];

        private static string Ordinal(int n) => n switch { 2 => "2nd", 3 => "3rd", _ => $"{n}th" };
    }

    private static ResolvedAnime A(int id, string title, int? eps) => new(id, title, eps, null, null, 2020, "FALL", "FINISHED", "TV");

    // Bleach: Sennen Kessen-hen, as MAL splits it: 13 + 13 + 14 + 12.
    private static readonly ResolvedAnime Cour1 = A(41467, "Bleach: Sennen Kessen-hen", 13);
    private static readonly ResolvedAnime Cour2 = A(53998, "Bleach: Sennen Kessen-hen - Ketsubetsu-tan", 13);
    private static readonly ResolvedAnime Cour3 = A(56784, "Bleach: Sennen Kessen-hen - Soukoku-tan", 14);
    private static readonly ResolvedAnime Cour4 = A(60636, "Bleach: Sennen Kessen-hen - Kashin-tan", 12);

    private static FakeResolver Bleach(ResolvedAnime? cour2 = null) =>
        new FakeResolver([Cour1, cour2 ?? Cour2, Cour3, Cour4])
            .Name("Bleach - Sennen Kessen Hen", Cour1)
            .Name("BLEACH Thousand-Year Blood War", Cour1)
            .Name("Bleach - Sennen Kessen-hen - Ketsubetsu-tan", cour2 ?? Cour2)
            .Name("Bleach", A(269, "Bleach", 366));

    private static Series S(ResolvedAnime a) => new() { MalId = a.MalId, Title = a.CanonicalTitle };

    private static string Verdict(ITitleResolverService r, string release, ResolvedAnime entry) =>
        ReleaseMatcher.Match(r, release, S(entry)) switch
        {
            (ReleaseVerdict.Yes, var ep) => $"ep{ep}",
            (var v, _) => v.ToString(),
        };

    [Theory]
    // Counted straight through under the first cour's title.
    [InlineData("[Grp] Bleach - Sennen Kessen Hen - 48 (1080p) [F6CC4D70].mkv", "No", "No", "No", "ep8")]
    [InlineData("[Grp] Bleach - Sennen Kessen Hen - 41 (1080p).mkv", "No", "No", "No", "ep1")]
    [InlineData("[Grp] Bleach - Sennen Kessen Hen - 07 (1080p).mkv", "ep7", "No", "No", "No")]
    [InlineData("[Grp] Bleach - Sennen Kessen Hen - 20 (1080p).mkv", "No", "ep7", "No", "No")]
    // "S01E45": season 1 by name, numbered straight through.
    [InlineData("[Grp] BLEACH Thousand-Year Blood War S01E45 1080p WEB-DL.mkv", "No", "No", "No", "ep5")]
    // Named after its cour and numbered within it.
    [InlineData("[Grp] Bleach - Sennen Kessen-hen - Ketsubetsu-tan - 05 [1080p].mkv", "No", "ep5", "No", "No")]
    // The cour given as a season.
    [InlineData("[Grp] BLEACH Thousand-Year Blood War S04E08 [1080p].mkv", "No", "No", "No", "ep8")]
    // A different show of the same name.
    [InlineData("[Grp] Bleach - 366 (1080p).mkv", "No", "No", "No", "No")]
    public void A_split_show_lands_on_the_right_cour_in_its_own_numbering(string release, string c1, string c2, string c3, string c4)
    {
        var r = Bleach();
        Assert.Equal([c1, c2, c3, c4], new[] { Cour1, Cour2, Cour3, Cour4 }.Select(c => Verdict(r, release, c)));
    }

    [Fact]
    public void An_unknown_earlier_length_is_never_guessed()
    {
        var r = Bleach(cour2: A(53998, "Bleach: Sennen Kessen-hen - Ketsubetsu-tan", null));
        Assert.Equal("No", Verdict(r, "[Grp] Bleach - Sennen Kessen Hen - 48 (1080p).mkv", Cour4));
        Assert.Null(ReleaseMatcher.AbsoluteEpisode(r, S(Cour4), 8));
    }

    [Fact]
    public void An_airing_seasons_placeholder_length_is_ignored()
    {
        // In its first week the database lists the airing season with 1 episode, and the next part
        // is already announced.
        static ResolvedAnime Airing(int id, string title, int? eps, string status) => new(id, title, eps, null, null, 2026, "FALL", status, "TV");
        var s1 = A(54492, "Kusuriya no Hitorigoto", 24);
        var s2 = A(58514, "Kusuriya no Hitorigoto 2nd Season", 24);
        var s3 = Airing(61987, "Kusuriya no Hitorigoto 3rd Season", 1, "UPCOMING");
        var s3p2 = Airing(62841, "Kusuriya no Hitorigoto 3rd Season Part 2", null, "UPCOMING");
        var r = new FakeResolver([s1, s2, s3, s3p2])
            .Name("Kusuriya no Hitorigoto", s1)
            .Name("Kusuriya no Hitorigoto 3rd Season", s3);
        var all = new[] { s1, s2, s3, s3p2 };

        Assert.Equal(["No", "No", "ep5", "No"], all.Select(a => Verdict(r, "[Grp] Kusuriya no Hitorigoto S3 - 05 (1080p).mkv", a)));
        Assert.Equal(["No", "No", "ep5", "No"], all.Select(a => Verdict(r, "[Grp] Kusuriya no Hitorigoto - 53 (1080p).mkv", a)));
        Assert.Equal(["ep20", "No", "No", "No"], all.Select(a => Verdict(r, "[Grp] Kusuriya no Hitorigoto - 20 (1080p).mkv", a)));
    }

    [Fact]
    public void Seasons_split_in_parts_are_found_by_their_titles()
    {
        var s1 = A(31240, "Re:Zero kara Hajimeru Isekai Seikatsu", 25);
        var s2 = A(39587, "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season", 13);
        var s2p2 = A(42203, "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2", 12);
        var s3 = A(54857, "Re:Zero kara Hajimeru Isekai Seikatsu 3rd Season", 16);
        var r = new FakeResolver([s1, s2, s2p2, s3])
            .Name("Re Zero kara Hajimeru Isekai Seikatsu", s1)
            .Name("Re Zero kara Hajimeru Isekai Seikatsu 3rd Season", s3);
        var all = new[] { s1, s2, s2p2, s3 };

        Assert.Equal(["No", "No", "No", "ep1"], all.Select(a => Verdict(r, "[Grp] Re Zero kara Hajimeru Isekai Seikatsu - 51 (1080p).mkv", a)));
        Assert.Equal(["No", "No", "No", "ep1"], all.Select(a => Verdict(r, "[Grp] Re Zero kara Hajimeru Isekai Seikatsu S3 - 01 (1080p).mkv", a)));
        Assert.Equal(51, ReleaseMatcher.AbsoluteEpisode(r, S(s3), 1));
    }

    [Fact]
    public void A_show_MAL_doesnt_split_matches_as_before()
    {
        var show = A(21, "One Piece", 1000); // the database's count runs behind a long show
        var r = new FakeResolver().Name("One Piece", show);
        Assert.Equal("ep1150", Verdict(r, "[Grp] One Piece - 1150 (1080p).mkv", show));
        Assert.Equal("No", Verdict(r, "[Grp] One Piece - 1150 (1080p).mkv", A(813, "Dragon Ball Z", 291)));
        Assert.Null(ReleaseMatcher.AbsoluteEpisode(r, S(show), 5));
    }

    [Fact]
    public void An_unidentified_release_is_left_to_title_matching()
    {
        var r = Bleach();
        Assert.Equal(ReleaseVerdict.Unknown, ReleaseMatcher.Match(r, "[Grp] Something Else Entirely - 03 [1080p].mkv", S(Cour4)).Verdict);
    }

    // ── Families linked by MAL's sequel/prequel relations ───────────

    [Fact]
    public void With_the_original_series_in_the_family_numbers_still_count_from_the_title_named()
    {
        var original = A(269, "Bleach", 366);
        var r = new FakeResolver([original, Cour1, Cour2, Cour3, Cour4])
            .Name("Bleach - Sennen Kessen Hen", Cour1)
            .Name("BLEACH Thousand-Year Blood War", Cour1)
            .Name("Bleach", original);
        var all = new[] { original, Cour1, Cour2, Cour3, Cour4 };

        Assert.Equal(["No", "No", "No", "No", "ep8"], all.Select(a => Verdict(r, "[Grp] Bleach - Sennen Kessen Hen - 48 (1080p).mkv", a)));
        Assert.Equal(["No", "No", "No", "No", "ep8"], all.Select(a => Verdict(r, "[Grp] BLEACH Thousand-Year Blood War S04E08 [1080p].mkv", a)));
        Assert.Equal(["ep366", "No", "No", "No", "No"], all.Select(a => Verdict(r, "[Grp] Bleach - 366 (1080p).mkv", a)));
    }

    [Fact]
    public void A_season_with_its_own_title_is_found_by_its_straight_through_number()
    {
        var s1 = A(40748, "Jujutsu Kaisen", 24);
        var s2 = A(51009, "Jujutsu Kaisen 2nd Season", 23);
        var s3 = A(57658, "Jujutsu Kaisen: Shimetsu Kaiyuu - Zenpen", 12);
        var s4 = new ResolvedAnime(63824, "Jujutsu Kaisen: Shimetsu Kaiyuu - Kouhen", 1, null, null, 2026, "FALL", "UPCOMING", "TV");
        var r = new FakeResolver([s1, s2, s3, s4]).Name("Jujutsu Kaisen", s1);
        var all = new[] { s1, s2, s3, s4 };

        Assert.Equal(["No", "No", "ep3", "No"], all.Select(a => Verdict(r, "[Grp] Jujutsu Kaisen - 50 (1080p).mkv", a)));
        Assert.Equal(["No", "No", "ep12", "No"], all.Select(a => Verdict(r, "[Grp] Jujutsu Kaisen - 59 (1080p).mkv", a)));
        Assert.Equal(["No", "No", "No", "ep1"], all.Select(a => Verdict(r, "[Grp] Jujutsu Kaisen - 60 (1080p).mkv", a)));
        Assert.Equal(["ep24", "No", "No", "No"], all.Select(a => Verdict(r, "[Grp] Jujutsu Kaisen - 24 (1080p).mkv", a)));
        Assert.Contains(("Jujutsu Kaisen", 50), ReleaseMatcher.AbsoluteForms(r, S(s3), 3));
    }

    [Fact]
    public void A_season_split_in_parts_counts_on_through_them()
    {
        var s1 = A(38671, "Enen no Shouboutai", 24);
        var s2 = A(40956, "Enen no Shouboutai: Ni no Shou", 24);
        var s3 = A(51818, "Enen no Shouboutai: San no Shou", 12);
        var s3p2 = A(59229, "Enen no Shouboutai: San no Shou Part 2", 13);
        var r = new FakeResolver([s1, s2, s3, s3p2]).Name("Enen no Shouboutai", s1);
        var all = new[] { s1, s2, s3, s3p2 };

        Assert.Equal(["No", "No", "No", "ep2"], all.Select(a => Verdict(r, "[Grp] Enen no Shouboutai S3 - 14 (1080p).mkv", a)));
        Assert.Equal(["No", "No", "ep5", "No"], all.Select(a => Verdict(r, "[Grp] Enen no Shouboutai S3 - 05 (1080p).mkv", a)));
        Assert.Equal(["ep14", "No", "No", "No"], all.Select(a => Verdict(r, "[Grp] Enen no Shouboutai - 14 (1080p).mkv", a)));
    }

    [Fact]
    public void Later_parts_share_their_seasons_number()
    {
        ResolvedAnime T(string title) => A(1, title, 12);
        Assert.Equal([1, 1, 2, 2, 3], ReleaseMatcher.SeasonsOf(
        [
            T("Mushoku Tensei: Isekai Ittara Honki Dasu"),
            T("Mushoku Tensei: Isekai Ittara Honki Dasu Part 2"),
            T("Mushoku Tensei II: Isekai Ittara Honki Dasu"),
            T("Mushoku Tensei II: Isekai Ittara Honki Dasu Part 2"),
            T("Mushoku Tensei III: Isekai Ittara Honki Dasu"),
        ]));
        Assert.Equal([1, 2, 3, 3], ReleaseMatcher.SeasonsOf(
            [T("Enen no Shouboutai"), T("Enen no Shouboutai: Ni no Shou"), T("Enen no Shouboutai: San no Shou"), T("Enen no Shouboutai: San no Shou Part 2")]));
    }

    [Theory]
    [InlineData("Bleach: Sennen Kessen-hen - Kashin-tan", "bleach sennen kessen hen")]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2", "re zero kara hajimeru isekai seikatsu")]
    [InlineData("Bleach", "bleach")]
    public void Chain_keys_drop_the_season_part_and_cour(string title, string key)
    {
        Assert.Equal(key, TitleResolverService.ChainKey(title));
    }
}
