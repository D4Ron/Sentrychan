using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Library;
using Sentrychan.Core.Models;

namespace Sentrychan.Tests;

public class SeasonLayoutTests
{
    /// <summary>A season chain and nothing else — all the layout reads.</summary>
    internal sealed class Chain(params ResolvedAnime[] chain) : ITitleResolverService
    {
        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ParsedRelease ParseRelease(string releaseName) => throw new NotSupportedException();
        public ResolvedAnime? ResolveRelease(string releaseName) => null;
        public ResolvedAnime? ResolveTitle(string title, int season = 1) => null;
        public List<ResolvedAnime> Search(string query, int limit = 20) => [];
        public ResolvedAnime? GetByMalId(int malId) => chain.FirstOrDefault(a => a.MalId == malId);
        public IReadOnlyList<ResolvedAnime> GetSeasonChain(int malId) => chain.Any(a => a.MalId == malId) ? chain : [];
    }

    internal static ResolvedAnime A(int id, string title, int? eps, int year = 2020, string status = "FINISHED") =>
        new(id, title, eps, null, null, year, "FALL", status, "TV");

    internal static readonly ResolvedAnime[] Mushoku =
    [
        A(1, "Mushoku Tensei: Isekai Ittara Honki Dasu", 11, 2021),
        A(2, "Mushoku Tensei: Isekai Ittara Honki Dasu Part 2", 12, 2021),
        A(3, "Mushoku Tensei II: Isekai Ittara Honki Dasu", 12, 2023),
        A(4, "Mushoku Tensei II: Isekai Ittara Honki Dasu Part 2", 12, 2024),
        A(5, "Mushoku Tensei III: Isekai Ittara Honki Dasu", 14, 2026, "ONGOING"),
    ];

    [Fact]
    public void Parts_continue_their_season_and_can_be_kept_apart()
    {
        var r = new Chain(Mushoku);
        Assert.Equal(
            [new(1, 0), new(1, 11), new(2, 0), new(2, 12), new SeasonPlacement(3, 0)],
            Mushoku.Select(a => SeasonLayout.Place(r, a.MalId, a.CanonicalTitle, separateParts: false)));
        Assert.Equal([1, 2, 3, 4, 5],
            Mushoku.Select(a => SeasonLayout.Place(r, a.MalId, a.CanonicalTitle, separateParts: true)!.Season));
        // Tracked under a release-style title, the season is the same.
        Assert.Equal(new SeasonPlacement(3, 0), SeasonLayout.Place(r, 5, "Mushoku Tensei S3", false));
    }

    private static readonly ResolvedAnime[] Bleach =
    [
        A(269, "Bleach", 366, 2004),
        A(41467, "Bleach: Sennen Kessen-hen", 13, 2022),
        A(53998, "Bleach: Sennen Kessen-hen - Ketsubetsu-tan", 13, 2023),
        A(56784, "Bleach: Sennen Kessen-hen - Soukoku-tan", 14, 2024),
        A(60636, "Bleach: Sennen Kessen-hen - Kashin-tan", 12, 2026, "ONGOING"),
    ];

    [Fact]
    public void An_arc_named_on_its_own_gets_its_own_folder_from_season_1()
    {
        var r = new Chain(Bleach);
        Assert.Equal(new SeasonPlacement(1, 0, 2022), SeasonLayout.Place(r, 41467, Bleach[1].CanonicalTitle, false));
        Assert.Equal(new SeasonPlacement(1, 40, 2022), SeasonLayout.Place(r, 60636, Bleach[4].CanonicalTitle, false));
        Assert.Equal(new SeasonPlacement(1, 0), SeasonLayout.Place(r, 269, "Bleach", false));
        Assert.Equal(LibraryShows.ShowKey("Bleach: Sennen Kessen-hen"), LibraryShows.ShowKey(Bleach[4].CanonicalTitle));
        Assert.NotEqual(LibraryShows.ShowKey("Bleach"), LibraryShows.ShowKey(Bleach[4].CanonicalTitle));
    }

    [Fact]
    public void A_season_with_its_own_name_tracked_under_the_shows_title_stays_in_the_shows_folder()
    {
        var r = new Chain(
            A(40748, "Jujutsu Kaisen", 24, 2020),
            A(51009, "Jujutsu Kaisen 2nd Season", 23, 2023),
            A(57658, "Jujutsu Kaisen: Shimetsu Kaiyuu - Zenpen", 12, 2026),
            A(63824, "Jujutsu Kaisen: Shimetsu Kaiyuu - Kouhen", null, 2026, "UPCOMING"));
        Assert.Equal(new SeasonPlacement(3, 0), SeasonLayout.Place(r, 57658, "Jujutsu Kaisen S3", false));
        Assert.Equal(new SeasonPlacement(3, 12), SeasonLayout.Place(r, 63824, "Jujutsu Kaisen S3 Part 2", false));
        Assert.Equal(new SeasonPlacement(1, 12, 2026), SeasonLayout.Place(r, 63824, "Jujutsu Kaisen: Shimetsu Kaiyuu - Kouhen", false));
    }

    [Fact]
    public void A_later_cour_is_filed_in_its_arcs_folder_with_the_season_numbers_continued()
    {
        var before = SeasonLayout.Resolver;
        SeasonLayout.Resolver = new Chain(Bleach);
        try
        {
            var cour2 = new Series { Id = 2, MalId = 53998, Title = Bleach[2].CanonicalTitle, Year = 2023 };
            var (relative, renamed) = LibraryFiling.Destination(NamingTemplate.Default, cour2, [cour2],
                "[Grp] Bleach - Sennen Kessen-hen - Ketsubetsu-tan - 01 (1080p).mkv", 1, 1);
            Assert.True(renamed);
            Assert.Equal(Path.Combine("Bleach Sennen Kessen-hen (2022)", "Season 01", "Bleach Sennen Kessen-hen S01E14.mkv"), relative);

            var separate = new Series { Id = 2, MalId = 53998, Title = Bleach[2].CanonicalTitle, Year = 2023, SeparateParts = true };
            Assert.Equal(Path.Combine("Bleach Sennen Kessen-hen (2022)", "Season 02", "Bleach Sennen Kessen-hen S02E01.mkv"),
                LibraryFiling.Destination(NamingTemplate.Default, separate, [separate], "x - 01.mkv", 1, 1).Relative);
        }
        finally { SeasonLayout.Resolver = before; }
    }
}
