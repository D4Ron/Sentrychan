using Sentrychan.Core.Library;

namespace Sentrychan.Tests;

public class NamingTemplateTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void Jellyfin_preset_is_the_default()
    {
        var n = NamingTemplate.FromConfig(null, null);
        Assert.Equal(NamingPreset.JellyfinPlex, n.Preset);
        Assert.Equal(P("Frieren (2023)", "Season 01", "Frieren S01E05.mkv"),
            n.Render(new EpisodeNaming("Frieren", 2023, 1, 5, ".mkv")));
    }

    [Fact]
    public void Unknown_year_disappears_with_its_brackets()
    {
        Assert.Equal(P("Frieren", "Season 02", "Frieren S02E13.mkv"),
            NamingTemplate.Default.Render(new EpisodeNaming("Frieren", null, 2, 13, "mkv")));
    }

    [Fact]
    public void Minimal_preset()
    {
        Assert.Equal(P("Frieren", "Season 1", "05.mp4"),
            NamingTemplate.For(NamingPreset.Minimal).Render(new EpisodeNaming("Frieren", 2023, 1, 5, ".mp4")));
    }

    [Fact]
    public void Specials_go_to_season_00()
    {
        Assert.Equal(P("Show (2020)", "Season 00", "Show S00E02.mkv"),
            NamingTemplate.Default.Render(new EpisodeNaming("Show", 2020, 0, 2, ".mkv")));
    }

    [Theory]
    [InlineData(NamingPreset.JellyfinPlex)]
    [InlineData(NamingPreset.Minimal)]
    public void Movies_are_named_by_title_and_year_whatever_the_preset(NamingPreset preset)
    {
        Assert.Equal(P("Show Movie (2021)", "Show Movie (2021).mkv"),
            NamingTemplate.For(preset).Render(new EpisodeNaming("Show Movie", 2021, 0, null, ".mkv", IsMovie: true)));
    }

    [Fact]
    public void Custom_template_with_every_token()
    {
        var t = NamingTemplate.For(NamingPreset.Custom, "{Title}/S{Season:00}/[{Group}] {Title} - {Episode:000}{Version} ({Quality})");
        Assert.Equal(NamingPreset.Custom, t.Preset);
        Assert.Equal(P("Show", "S01", "[Grp] Show - 007v2 (1080p).mkv"),
            t.Render(new EpisodeNaming("Show", 2020, 1, 7, ".mkv", "Grp", "1080p", 2)));
        // Missing group and quality leave no empty brackets behind; version 1 isn't written.
        Assert.Equal(P("Show", "S01", "Show - 007.mkv"),
            t.Render(new EpisodeNaming("Show", 2020, 1, 7, ".mkv", null, null, 1)));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{Episode}", "{Title}")]
    [InlineData("{Title}", "{Episode}")]
    [InlineData("{Title}/{Episode} {Ep}", "Unknown token")]
    [InlineData("{Title}/../{Episode}", "'..'")]
    public void Invalid_custom_templates_are_explained_and_fall_back_to_the_default(string template, string expected)
    {
        Assert.Contains(expected, NamingTemplate.Validate(template));
        Assert.Equal(NamingPreset.JellyfinPlex, NamingTemplate.For(NamingPreset.Custom, template).Preset);
    }

    [Fact]
    public void Characters_Windows_forbids_are_dropped_like_the_old_folder_names()
    {
        Assert.Equal(P("ReZERO (2016)", "Season 01", "ReZERO S01E01.mkv"),
            NamingTemplate.Default.Render(new EpisodeNaming("Re:ZERO", 2016, 1, 1, ".mkv")));
        Assert.Equal("Dots", NamingTemplate.CleanSegment("Dots..."));
    }
}

public class ReleaseNameParserTests
{
    [Fact]
    public void Fansub_release()
    {
        var r = ReleaseNameParser.Parse("[Grp] Sousou no Frieren - 13v2 (1080p) [ABCD1234].mkv");
        Assert.Equal("Sousou no Frieren", r.Title);
        Assert.Equal(13, r.Episode);
        Assert.Equal(2, r.Version);
        Assert.Equal("Grp", r.Group);
        Assert.Equal("1080p", r.Resolution);
        Assert.False(r.HasExplicitSeasonEpisode);
    }

    [Fact]
    public void Season_episode_marker_is_explicit()
    {
        var r = ReleaseNameParser.Parse("Show S02E05.mkv");
        Assert.Equal((2, 5), (r.Season, r.Episode));
        Assert.True(r.HasExplicitSeasonEpisode);
    }

    [Fact]
    public void Season_in_the_title()
    {
        var r = ReleaseNameParser.Parse("[Grp] Show 2nd Season - 03 [1080p].mkv");
        Assert.Equal((2, 3), (r.Season, r.Episode));
        Assert.Equal("Show", r.Title);
    }

    [Fact]
    public void Minimal_preset_names_parse_back()
    {
        Assert.Equal(5, ReleaseNameParser.Parse("05.mkv").Episode);
        Assert.Equal(2, ReleaseNameParser.Parse("05v2.mkv").Version);
    }

    [Fact]
    public void Specials_and_ranges()
    {
        var sp = ReleaseNameParser.Parse("[Grp] Show - SP2 [720p].mkv");
        Assert.True(sp.IsSpecial);
        Assert.Equal(2, sp.Episode);
        Assert.Equal("Show", sp.Title);

        Assert.True(ReleaseNameParser.Parse("[Grp] Show - 01-12 [Batch].mkv").IsEpisodeRange);
        Assert.True(ReleaseNameParser.Parse("[Grp] Show Movie [BD 1080p].mkv").IsMovie);
    }

    [Theory]
    [InlineData("(Hi10)_Ladies_versus_Butlers!_-_ED2_(BD_1080p)_(THORA)_(B2AB774E).mkv")]
    [InlineData("[Grp] Show - NCOP1 [1080p].mkv")]
    [InlineData("[Grp] Show - Preview 03 [1080p].mkv")]
    public void Openings_endings_and_previews_are_extras(string name)
    {
        Assert.True(ReleaseNameParser.Parse(name).IsExtra);
    }

    [Fact]
    public void An_ordinary_episode_is_not_an_extra()
    {
        Assert.False(ReleaseNameParser.Parse("[Grp] Show - 02 [1080p].mkv").IsExtra);
    }
}

public class FolderNameCleanerTests
{
    [Theory]
    [InlineData("[Grp] Show Name (2023) [BD 1080p x265 10bit] 01-24 v2", "Show Name", 2023)]
    [InlineData("Show Name", "Show Name", null)]
    [InlineData("Show.Name.1080p.WEB-DL", "Show Name", null)]
    [InlineData("Show Name - Complete Series [Dual Audio]", "Show Name", null)]
    // Scene names, including what a first tidy made of them before they were understood.
    [InlineData("Arcane.S02.COMPLETE.REPACK.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FLUX[TGx]", "Arcane", null)]
    [InlineData("Arcane.S02. .REPACK. .NF. .DDP5.1.Atmos. -FLUX", "Arcane", null)]
    [InlineData("Nukitashi.the.Animation.S01.1080p.BluRay.Dual-Audio.Opus.2.0.x265-StaFer", "Nukitashi the Animation", null)]
    [InlineData("The.All.devouring.Whale.S01.1080p.ADN.WEB-DL.AAC2.0.H.264-VARYG", "The All devouring Whale", null)]
    [InlineData("Nukitashi the Animation S01 2 0 -StaFer", "Nukitashi the Animation", null)]
    [InlineData("The All devouring Whale S01 ADN AAC2 0 H 264-VARYG", "The All devouring Whale", null)]
    [InlineData("Show Name S01 1080p WEB-DL", "Show Name", null)]
    [InlineData("Show.Name.2021.1080p.WEB-DL", "Show Name", 2021)]
    [InlineData("Ladies_versus_Butlers!", "Ladies versus Butlers!", null)]
    [InlineData("Dr. Stone", "Dr. Stone", null)]
    [InlineData("Mushoku Tensei S2", "Mushoku Tensei S2", null)]
    public void Release_tags_resolution_ranges_and_versions_go(string folder, string title, int? year)
    {
        Assert.Equal((title, year), FolderNameCleaner.Clean(folder));
    }
}

public class EpisodeNumberingTests
{
    private static readonly Dictionary<int, int?> TwoSeasons = new() { [1] = 12, [2] = 12 };

    [Fact]
    public void Relative_numbers_are_kept()
    {
        Assert.Equal((2, 5), EpisodeNumbering.Resolve(2, 5, false, TwoSeasons, out _));
    }

    [Fact]
    public void Continued_count_becomes_season_relative()
    {
        Assert.Equal((2, 1), EpisodeNumbering.Resolve(2, 13, false, TwoSeasons, out _));
        Assert.Equal((2, 12), EpisodeNumbering.Resolve(2, 24, false, TwoSeasons, out _));
        // Filed under season 1 because the release didn't say otherwise.
        Assert.Equal((2, 3), EpisodeNumbering.Resolve(1, 15, false, TwoSeasons, out _));
    }

    [Fact]
    public void Numbers_that_fit_nowhere_are_unsure()
    {
        Assert.Null(EpisodeNumbering.Resolve(2, 30, false, TwoSeasons, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Explicit_season_episode_past_the_season_is_unsure_not_remapped()
    {
        Assert.Null(EpisodeNumbering.Resolve(2, 13, true, TwoSeasons, out _));
    }

    [Fact]
    public void Unknown_season_length_only_trusts_numbers_that_cannot_be_a_continued_count()
    {
        var airing = new Dictionary<int, int?> { [1] = 12, [2] = null };
        Assert.Equal((2, 7), EpisodeNumbering.Resolve(2, 7, false, airing, out _));
        Assert.Null(EpisodeNumbering.Resolve(2, 14, false, airing, out _));

        var nothingKnown = new Dictionary<int, int?>();
        Assert.Equal((1, 40), EpisodeNumbering.Resolve(1, 40, false, nothingKnown, out _));
        Assert.Null(EpisodeNumbering.Resolve(3, 2, false, nothingKnown, out _));
        Assert.Equal((3, 2), EpisodeNumbering.Resolve(3, 2, true, nothingKnown, out _));
    }

    [Fact]
    public void Specials_pass_through()
    {
        Assert.Equal((0, 3), EpisodeNumbering.Resolve(0, 3, false, TwoSeasons, out _));
    }
}
