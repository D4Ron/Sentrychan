using System.Text.Json;
using System.Text.Json.Nodes;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;

namespace Sentrychan.Tests.MihonBridge;

public class SuwayomiClientTests
{
    private readonly FakeSuwayomi _server = new();
    private readonly SuwayomiClient _client;

    public SuwayomiClientTests() =>
        _client = new SuwayomiClient(new HttpClient(_server), new Uri("http://127.0.0.1:4567/"));

    [Fact]
    public async Task Sources_keep_64_bit_ids_as_text()
    {
        var sources = await _client.GetSourcesAsync();
        Assert.Equal(4, sources.Count);
        Assert.Equal("1234567890123456789", sources[1].Id);
        Assert.Equal("-4611686018427387904", sources[2].Id);
        Assert.Equal(new BridgeSource("77", "Grown-ups Only", "all", "Grown-ups Only", true, true, false), sources[3]);
    }

    [Fact]
    public async Task A_listing_asks_for_the_right_type_and_page_and_reads_the_recorded_answer()
    {
        var page = await _client.FetchMangaPageAsync("0", BridgeListing.Popular, 2);
        Assert.False(page.HasNextPage);
        Assert.Equal(["Another Invented Title", "Invented Title"], page.Mangas.Select(m => m.Title));
        Assert.Equal("/api/v1/manga/1/thumbnail", page.Mangas[0].ThumbnailUrl);
        Assert.Equal("UNKNOWN", page.Mangas[0].Status);

        var vars = _server.Last("FetchSourceManga")["variables"]!;
        Assert.Equal("POPULAR", vars["type"]!.GetValue<string>());
        Assert.Equal(2, vars["page"]!.GetValue<int>());
        Assert.Equal("0", vars["source"]!.GetValue<string>()); // LongString travels as text
        Assert.Null(vars["filters"]); // popular takes no filters
    }

    [Fact]
    public async Task A_search_sends_the_query_and_only_changed_filters()
    {
        var filters = await _client.GetFiltersAsync("1234567890123456789");
        filters.Find<SelectFilter>("Status")!.State = 2;
        await _client.FetchMangaPageAsync("1234567890123456789", BridgeListing.Search, 1, "frieren", filters);

        var vars = _server.Last("FetchSourceManga")["variables"]!;
        Assert.Equal("SEARCH", vars["type"]!.GetValue<string>());
        Assert.Equal("frieren", vars["query"]!.GetValue<string>());
        Assert.Equal("""[{"position":1,"selectState":2}]""", vars["filters"]!.ToJsonString());
    }

    [Fact]
    public async Task Chapters_and_pages_come_back_usable()
    {
        var chapters = await _client.FetchChaptersAsync(2);
        Assert.Equal(2, chapters.Count);
        Assert.Equal(1, chapters[0].ChapterNumber);
        Assert.Equal("Chapter 1", chapters[0].Name);
        Assert.NotNull(chapters[0].UploadedAt); // a LongString of milliseconds

        var pages = await _client.FetchPagesAsync(1);
        Assert.Equal("http://127.0.0.1:4567/api/v1/manga/2/chapter/1/page/0", pages[0]);
        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public async Task Details_and_lookup_by_source_url()
    {
        var manga = await _client.FetchMangaAsync(2);
        Assert.Equal("Invented Title", manga.Title);
        var found = await _client.FindMangaAsync("0", "Invented Title");
        Assert.Equal(2, found!.Id);
        var vars = _server.Last("FindManga")["variables"]!;
        Assert.Equal("Invented Title", vars["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Extensions_report_adult_content_and_their_sources()
    {
        var ext = await _client.GetExtensionsAsync();
        Assert.Equal(3, ext.Count);
        Assert.True(ext[0].IsInstalled && ext[0].HasUpdate && !ext[0].IsNsfw);
        Assert.Equal(["1234567890123456789", "-4611686018427387904"], ext[0].SourceIds);
        Assert.True(ext[1].IsNsfw);                 // NSFW
        Assert.True(ext[2].IsNsfw && ext[2].IsObsolete); // MIXED counts as adult, as the server does
    }

    [Fact]
    public async Task Installing_sends_a_patch_with_just_that_action()
    {
        _server.Responses["UpdateExtension"] = """{"data":{"updateExtension":{"extension":null}}}""";
        Assert.Null(await _client.UpdateExtensionAsync("test.example.grownups", SuwayomiClient.ExtensionAction.Install));
        var vars = _server.Last("UpdateExtension")["variables"]!;
        Assert.Equal("""{"install":true}""", vars["patch"]!.ToJsonString());
        Assert.Equal("test.example.grownups", vars["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Preferences_of_every_kind_parse_and_keep_their_positions()
    {
        var prefs = await _client.GetPreferencesAsync("1234567890123456789");
        Assert.Equal([0, 1, 2, 3, 4], prefs.Select(p => p.Position));
        Assert.Equal(false, prefs[0].BoolValue);     // no current value → the default
        Assert.Equal("high", prefs[1].TextValue);
        Assert.Equal(["low", "medium", "high"], prefs[1].EntryValues);
        Assert.False(prefs[2].Visible);
        Assert.Equal(["en"], prefs[3].Values);
        Assert.False(prefs[3].Enabled);
        Assert.Equal(true, prefs[4].BoolValue);

        await _client.SetPreferenceAsync("1234567890123456789", prefs[3], new[] { "en", "es" });
        var change = _server.Last("SetPreference")["variables"]!["change"]!;
        Assert.Equal("""{"position":3,"multiSelectState":["en","es"]}""", change.ToJsonString());
    }

    [Fact]
    public async Task A_server_error_becomes_a_plain_message_without_the_stack_trace()
    {
        _server.Responses["Sources"] = "error-null";
        var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.GetSourcesAsync());
        Assert.Equal(BridgeFailure.Source, ex.Failure);
        Assert.Equal("The source failed without saying why.", ex.Message);
    }

    [Fact]
    public async Task No_server_means_not_running()
    {
        _server.Listening = () => false;
        var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.AboutAsync());
        Assert.Equal(BridgeFailure.NotRunning, ex.Failure);
    }

    [Theory]
    [InlineData("Exception while fetching data (/fetchSourceManga) : Failed to bypass Cloudflare\r\n\r\njava.lang.Exception", BridgeFailure.WebCheck)]
    [InlineData("Exception while fetching data (/fetchChapters) : HTTP error 401\n\tat x", BridgeFailure.LoginRequired)]
    [InlineData("Exception while fetching data (/fetchManga) : Please log in via WebView", BridgeFailure.LoginRequired)]
    [InlineData("Exception while fetching data (/fetchManga) : HTTP error 404", BridgeFailure.Source)]
    public void Server_errors_are_classified(string raw, BridgeFailure expected)
    {
        var ex = BridgeException.FromServer(raw);
        Assert.Equal(expected, ex.Failure);
        Assert.DoesNotContain("\tat ", ex.Message);
    }
}

public class BridgeFilterTests
{
    private static FilterList All() =>
        BridgeFilters.Parse(JsonDocument.Parse(FakeSuwayomi.Fixture("filters-all")).RootElement
            .GetProperty("data").GetProperty("source").GetProperty("filters"));

    [Fact]
    public void Every_mihon_filter_kind_maps_one_to_one()
    {
        var f = All();
        Assert.Equal(9, f.Count); // positions must line up with the server's list
        Assert.IsType<HeaderFilter>(f[0]);
        Assert.Equal(["Any", "Ongoing", "Completed"], ((SelectFilter)f[1]).Values);
        Assert.Equal(string.Empty, ((TextFilter)f[2]).State);
        Assert.True(((CheckBoxFilter)f[3]).State);
        Assert.IsType<SeparatorFilter>(f[4]);
        Assert.Equal(new SortSelection(1, false), ((SortFilter)f[5]).State);
        var genres = (GroupFilter)f[6];
        Assert.Equal(TriState.Exclude, ((TriStateFilter)genres.Filters[1]).State);
        Assert.IsType<HeaderFilter>(f[7]); // an unknown kind holds its place
        Assert.Null(((SortFilter)f[8]).State);
        Assert.False(f.IsChanged);
    }

    [Fact]
    public void Nothing_changed_sends_nothing()
    {
        Assert.Equal("[]", BridgeFilters.Changes(All()).ToJsonString());
    }

    [Fact]
    public void Changes_are_addressed_by_position_and_group_position()
    {
        var f = All().Clone();
        ((TextFilter)f[2]).State = "someone";
        ((CheckBoxFilter)f[3]).State = false;
        ((SortFilter)f[5]).State = new SortSelection(2, true);
        var genres = (GroupFilter)f[6];
        ((TriStateFilter)genres.Filters[0]).State = TriState.Include;
        ((CheckBoxFilter)genres.Filters[2]).State = true;

        var json = BridgeFilters.Changes(f).ToJsonString();
        Assert.Equal(
            """[{"position":2,"textState":"someone"},{"position":3,"checkBoxState":false},""" +
            """{"position":5,"sortState":{"index":2,"ascending":true}},""" +
            """{"position":6,"groupChange":{"position":0,"triState":"INCLUDE"}},""" +
            """{"position":6,"groupChange":{"position":2,"checkBoxState":true}}]""", json);
    }

    [Fact]
    public void The_local_sources_recorded_sort_filter_parses()
    {
        var f = BridgeFilters.Parse(JsonDocument.Parse(FakeSuwayomi.Fixture("filters-local")).RootElement
            .GetProperty("data").GetProperty("source").GetProperty("filters"));
        var sort = Assert.IsType<SortFilter>(Assert.Single(f));
        Assert.Equal(["Title", "Date"], sort.Values);
        Assert.Equal(new SortSelection(0, true), sort.State);
    }
}

public class BridgedMangaSourceTests
{
    private sealed class Bridge(SuwayomiClient client) : IMihonBridge
    {
        public Task<SuwayomiClient> ClientAsync(CancellationToken ct = default) => Task.FromResult(client);
    }

    private readonly FakeSuwayomi _server = new();

    private BridgedMangaSource Source(bool nsfw = false, bool latest = true) =>
        new(new BridgeSource("0", "Local source", "en", "Local source", nsfw, latest, false), "Bridged",
            new Bridge(new SuwayomiClient(new HttpClient(_server), new Uri("http://127.0.0.1:4567/"))));

    [Fact]
    public async Task Results_use_server_ids_and_port_independent_cover_urls()
    {
        IMangaSourceService s = Source();
        var page = await s.GetPopularAsync(1);
        Assert.Equal("1", page.Items[0].SourceId);
        Assert.Equal("mihon-bridge:/api/v1/manga/1/thumbnail", page.Items[0].CoverUrl);
        Assert.Equal(new MangaSourceInfo("0", "Bridged", "en", false, true), s.Info);
    }

    [Fact]
    public async Task Chapters_come_back_ascending_with_numbers_as_text()
    {
        var chapters = await Source().GetChaptersAsync("2");
        Assert.Equal(["1", "2"], chapters.Select(c => c.SourceId));
        Assert.Equal(["1", "2"], chapters.Select(c => c.ChapterNumber));
        Assert.Equal(1, chapters[0].ChapterSort);
        Assert.Equal("Chapter 1", chapters[0].Title);
    }

    [Fact]
    public async Task Latest_is_empty_for_a_source_without_it()
    {
        IMangaSourceService s = Source(latest: false);
        Assert.Same(MangaPage.Empty, await s.GetLatestAsync(1));
        Assert.DoesNotContain(_server.Requests, r => r["operationName"]!.GetValue<string>() == "FetchSourceManga");
    }

    [Fact]
    public async Task Filters_are_fetched_from_the_server()
    {
        IMangaSourceService s = Source();
        Assert.Empty(s.GetFilterList()); // nothing to offer without asking
        Assert.Equal(9, (await s.GetFilterListAsync()).Count);
    }

    [Fact]
    public async Task Adult_sources_are_marked_adult_everywhere()
    {
        var s = Source(nsfw: true);
        Assert.True(s.IsAdultSource);
        Assert.True(((IMangaSourceService)s).Info.IsNsfw);
        Assert.All((await s.SearchAsync("x", 1, FilterList.Empty)).Items, r => Assert.True(r.IsAdult));
    }

    [Fact]
    public async Task Pages_are_absolute_urls_on_the_running_server()
    {
        var pages = await Source().GetPageUrlsAsync("1");
        Assert.StartsWith("http://127.0.0.1:4567/api/v1/manga/2/chapter/1/page/", pages[0]);
        Assert.Empty(await Source().GetPageUrlsAsync("not-a-number"));
    }
}

public class BridgeErrorTests
{
    [Fact]
    public void A_plain_network_failure_is_not_mistaken_for_a_web_check()
    {
        // Recorded shape: the stack passes through the server's Cloudflare interceptor regardless.
        var ex = BridgeException.FromServer(
            "Exception while fetching data (/addExtensionStore) : Unexpected response code for CONNECT: 403\r\n\r\n" +
            "java.io.IOException: Unexpected response code for CONNECT: 403\n\tat eu.kanade.tachiyomi.network.interceptor.CloudflareInterceptor.intercept(CloudflareInterceptor.kt:44)");
        Assert.Equal(BridgeFailure.Source, ex.Failure);
        Assert.Equal("Unexpected response code for CONNECT: 403", ex.Message);
    }
}
