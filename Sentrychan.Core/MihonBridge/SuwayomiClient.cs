using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.MihonBridge;

public sealed record BridgeServerInfo(string Name, string Version);

/// <summary>A source provided by an installed extension. <see cref="Id"/> is the Mihon source id.</summary>
public sealed record BridgeSource(string Id, string Name, string Language, string DisplayName,
    bool IsNsfw, bool SupportsLatest, bool IsConfigurable);

public sealed record BridgeManga(int Id, string Title, string? ThumbnailUrl, string Url, string? RealUrl,
    string? Status, string? Author, string? Artist, string? Description, IReadOnlyList<string> Genres);

public sealed record BridgeMangaPage(IReadOnlyList<BridgeManga> Mangas, bool HasNextPage);

public sealed record BridgeChapter(int Id, string Name, double ChapterNumber, string? Scanlator,
    DateTime? UploadedAt, int SourceOrder, string? RealUrl);

public sealed record BridgeExtension(string PackageName, string Name, string Language, string Version,
    bool IsInstalled, bool HasUpdate, bool IsObsolete, bool IsNsfw, string? StoreUrl, IReadOnlyList<string> SourceIds);

/// <summary>An extension repository (the server calls them stores) the user added.</summary>
public sealed record BridgeRepo(string Name, string IndexUrl);

public enum PreferenceKind { Switch, CheckBox, EditText, List, MultiSelect }

/// <summary>
/// One of a source's settings, as the extension declares it. <see cref="Position"/> is its index
/// in the source's preference list, which is how a change is addressed.
/// </summary>
public sealed record BridgePreference(
    int Position, PreferenceKind Kind, string? Key, string? Title, string? Summary, bool Visible, bool Enabled,
    bool? BoolValue, string? TextValue, IReadOnlyList<string> Values,
    IReadOnlyList<string> Entries, IReadOnlyList<string> EntryValues, string? DialogTitle, string? DialogMessage);

public enum BridgeListing { Popular, Latest, Search }

/// <summary>
/// Talks to the helper server's GraphQL API (<c>/api/graphql</c>). The shapes here were read from
/// the pinned release's schema (<see cref="BridgeRelease.Version"/>) and are tested against
/// responses recorded from it. The server's REST API is deprecated, so only the image URLs it
/// hands back point there.
/// </summary>
public sealed class SuwayomiClient(HttpClient http, Uri baseUri)
{
    public Uri BaseUri { get; } = baseUri;

    /// <summary>Resolves a server-relative path ("/api/v1/manga/1/thumbnail") against the server.</summary>
    public string Absolute(string pathOrUrl) =>
        Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? pathOrUrl
            : new Uri(BaseUri, pathOrUrl).ToString();

    // ── Server ──────────────────────────────────────────────────────

    public async Task<BridgeServerInfo> AboutAsync(CancellationToken ct = default)
    {
        var d = await SendAsync("About", "query About { aboutServer { name version } }", null, ct);
        var a = d.GetProperty("aboutServer");
        return new(Str(a, "name") ?? "", Str(a, "version") ?? "");
    }

    // ── Sources ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BridgeSource>> GetSourcesAsync(CancellationToken ct = default)
    {
        var d = await SendAsync("Sources",
            "query Sources { sources { nodes { id name lang displayName isNsfw supportsLatest isConfigurable } } }", null, ct);
        return d.GetProperty("sources").GetProperty("nodes").EnumerateArray().Select(n => new BridgeSource(
            IdStr(n, "id"), Str(n, "name") ?? "", Str(n, "lang") ?? "all", Str(n, "displayName") ?? Str(n, "name") ?? "",
            Bool(n, "isNsfw"), Bool(n, "supportsLatest"), Bool(n, "isConfigurable"))).ToList();
    }

    // Aliases because the union members share field names with different types ("default" is a
    // number on one, text on another), which GraphQL doesn't allow side by side.
    private const string FilterFields =
        "__typename " +
        "... on HeaderFilter { name } ... on SeparatorFilter { name } " +
        "... on SelectFilter { name values selectDefault: default } " +
        "... on TextFilter { name textDefault: default } " +
        "... on CheckBoxFilter { name checkBoxDefault: default } " +
        "... on TriStateFilter { name triStateDefault: default } " +
        "... on SortFilter { name values sortDefault: default { index ascending } }";

    public async Task<FilterList> GetFiltersAsync(string sourceId, CancellationToken ct = default)
    {
        var d = await SendAsync("Filters",
            "query Filters($id: LongString!) { source(id: $id) { filters { " + FilterFields +
            " ... on GroupFilter { name filters { " + FilterFields + " } } } } }",
            new JsonObject { ["id"] = sourceId }, ct);
        return BridgeFilters.Parse(d.GetProperty("source").GetProperty("filters"));
    }

    public async Task<BridgeMangaPage> FetchMangaPageAsync(string sourceId, BridgeListing listing, int page,
        string? query = null, FilterList? filters = null, CancellationToken ct = default)
    {
        var vars = new JsonObject
        {
            ["source"] = sourceId,
            ["type"] = listing.ToString().ToUpperInvariant(),
            ["page"] = page,
        };
        if (listing == BridgeListing.Search)
        {
            vars["query"] = query ?? "";
            vars["filters"] = filters == null ? new JsonArray() : BridgeFilters.Changes(filters);
        }
        var d = await SendAsync("FetchSourceManga",
            "mutation FetchSourceManga($source: LongString!, $type: FetchSourceMangaType!, $page: Int!, $query: String, $filters: [FilterChangeInput!]) " +
            "{ fetchSourceManga(input: { source: $source, type: $type, page: $page, query: $query, filters: $filters }) " +
            "{ hasNextPage mangas { " + MangaFields + " } } }", vars, ct);
        var p = d.GetProperty("fetchSourceManga");
        return new(p.GetProperty("mangas").EnumerateArray().Select(ParseManga).ToList(), Bool(p, "hasNextPage"));
    }

    // ── Titles and chapters ─────────────────────────────────────────

    private const string MangaFields = "id title thumbnailUrl url realUrl status author artist description genre";

    /// <summary>Refreshes a title from its source and returns it.</summary>
    public async Task<BridgeManga> FetchMangaAsync(int mangaId, CancellationToken ct = default)
    {
        var d = await SendAsync("FetchManga",
            "mutation FetchManga($id: Int!) { fetchManga(input: { id: $id }) { manga { " + MangaFields + " } } }",
            new JsonObject { ["id"] = mangaId }, ct);
        return ParseManga(d.GetProperty("fetchManga").GetProperty("manga"));
    }

    /// <summary>A title the server already knows, by the source's own URL for it; null when it doesn't.</summary>
    public async Task<BridgeManga?> FindMangaAsync(string sourceId, string url, CancellationToken ct = default)
    {
        var d = await SendAsync("FindManga",
            "query FindManga($source: LongString!, $url: String!) { mangas(condition: { sourceId: $source, url: $url }) { nodes { " + MangaFields + " } } }",
            new JsonObject { ["source"] = sourceId, ["url"] = url }, ct);
        return d.GetProperty("mangas").GetProperty("nodes").EnumerateArray().Select(ParseManga).FirstOrDefault();
    }

    /// <summary>Refreshes a title's chapter list from its source and returns it.</summary>
    public async Task<IReadOnlyList<BridgeChapter>> FetchChaptersAsync(int mangaId, CancellationToken ct = default)
    {
        var d = await SendAsync("FetchChapters",
            "mutation FetchChapters($id: Int!) { fetchChapters(input: { mangaId: $id }) { chapters { id name chapterNumber scanlator uploadDate sourceOrder realUrl } } }",
            new JsonObject { ["id"] = mangaId }, ct);
        return d.GetProperty("fetchChapters").GetProperty("chapters").EnumerateArray().Select(ParseChapter).ToList();
    }

    private static BridgeChapter ParseChapter(JsonElement c)
    {
        var upload = Long(c, "uploadDate");
        return new BridgeChapter(c.GetProperty("id").GetInt32(), Str(c, "name") ?? "",
            c.TryGetProperty("chapterNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : -1,
            Str(c, "scanlator"),
            upload is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(upload.Value).UtcDateTime : null,
            c.TryGetProperty("sourceOrder", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0,
            Str(c, "realUrl"));
    }

    /// <summary>
    /// The chapters the server already has for a title, without asking the source — used to
    /// line a restored backup up with the library.
    /// </summary>
    public async Task<IReadOnlyList<(BridgeChapter Chapter, string Url)>> GetStoredChaptersAsync(int mangaId, CancellationToken ct = default)
    {
        var d = await SendAsync("StoredChapters",
            "query StoredChapters($id: Int!) { chapters(condition: { mangaId: $id }) { nodes { id url name chapterNumber scanlator uploadDate sourceOrder realUrl } } }",
            new JsonObject { ["id"] = mangaId }, ct);
        return d.GetProperty("chapters").GetProperty("nodes").EnumerateArray()
            .Select(c => (ParseChapter(c), Str(c, "url") ?? "")).ToList();
    }

    /// <summary>Absolute URLs of a chapter's page images, served by the server itself.</summary>
    public async Task<IReadOnlyList<string>> FetchPagesAsync(int chapterId, CancellationToken ct = default)
    {
        var d = await SendAsync("FetchChapterPages",
            "mutation FetchChapterPages($id: Int!) { fetchChapterPages(input: { chapterId: $id }) { pages } }",
            new JsonObject { ["id"] = chapterId }, ct);
        return d.GetProperty("fetchChapterPages").GetProperty("pages").EnumerateArray()
            .Select(p => Absolute(p.GetString() ?? "")).ToList();
    }

    // ── Source settings ─────────────────────────────────────────────

    private const string PreferenceFields =
        "__typename " +
        "... on SwitchPreference { key title summary visible enabled switchValue: currentValue switchDefault: default } " +
        "... on CheckBoxPreference { key title summary visible enabled checkBoxValue: currentValue checkBoxDefault: default } " +
        "... on EditTextPreference { key title summary visible enabled textValue: currentValue textDefault: default dialogTitle dialogMessage } " +
        "... on ListPreference { key title summary visible enabled listValue: currentValue listDefault: default entries entryValues } " +
        "... on MultiSelectListPreference { key title summary visible enabled multiValue: currentValue multiDefault: default dialogTitle dialogMessage entries entryValues }";

    public async Task<IReadOnlyList<BridgePreference>> GetPreferencesAsync(string sourceId, CancellationToken ct = default)
    {
        var d = await SendAsync("Preferences",
            "query Preferences($id: LongString!) { source(id: $id) { preferences { " + PreferenceFields + " } } }",
            new JsonObject { ["id"] = sourceId }, ct);
        return ParsePreferences(d.GetProperty("source").GetProperty("preferences"));
    }

    /// <summary>Saves one setting and returns the source's settings as they now stand (one may show or hide others).</summary>
    public async Task<IReadOnlyList<BridgePreference>> SetPreferenceAsync(string sourceId, BridgePreference pref,
        object value, CancellationToken ct = default)
    {
        var change = new JsonObject { ["position"] = pref.Position };
        switch (pref.Kind)
        {
            case PreferenceKind.Switch: change["switchState"] = (bool)value; break;
            case PreferenceKind.CheckBox: change["checkBoxState"] = (bool)value; break;
            case PreferenceKind.EditText: change["editTextState"] = (string)value; break;
            case PreferenceKind.List: change["listState"] = (string)value; break;
            case PreferenceKind.MultiSelect:
                change["multiSelectState"] = new JsonArray(((IEnumerable<string>)value).Select(v => (JsonNode)v!).ToArray());
                break;
        }
        var d = await SendAsync("SetPreference",
            "mutation SetPreference($source: LongString!, $change: SourcePreferenceChangeInput!) " +
            "{ updateSourcePreference(input: { source: $source, change: $change }) { preferences { " + PreferenceFields + " } } }",
            new JsonObject { ["source"] = sourceId, ["change"] = change }, ct);
        return ParsePreferences(d.GetProperty("updateSourcePreference").GetProperty("preferences"));
    }

    // ── Extensions and repositories ─────────────────────────────────

    private const string ExtensionFields =
        "pkgName name lang versionName isInstalled hasUpdate isObsolete contentWarning storeIndexUrl source { nodes { id } }";

    public async Task<IReadOnlyList<BridgeExtension>> GetExtensionsAsync(CancellationToken ct = default)
    {
        var d = await SendAsync("Extensions", "query Extensions { extensions { nodes { " + ExtensionFields + " } } }", null, ct);
        return d.GetProperty("extensions").GetProperty("nodes").EnumerateArray().Select(ParseExtension).ToList();
    }

    /// <summary>Re-reads every repository's extension list and returns the result.</summary>
    public async Task<IReadOnlyList<BridgeExtension>> RefreshExtensionsAsync(CancellationToken ct = default)
    {
        var d = await SendAsync("FetchExtensions",
            "mutation FetchExtensions { fetchExtensions(input: {}) { extensions { " + ExtensionFields + " } } }", null, ct);
        return d.GetProperty("fetchExtensions").GetProperty("extensions").EnumerateArray().Select(ParseExtension).ToList();
    }

    public enum ExtensionAction { Install, Update, Uninstall }

    public async Task<BridgeExtension?> UpdateExtensionAsync(string packageName, ExtensionAction action, CancellationToken ct = default)
    {
        var patch = new JsonObject { [action.ToString().ToLowerInvariant()] = true };
        var d = await SendAsync("UpdateExtension",
            "mutation UpdateExtension($id: String!, $patch: UpdateExtensionPatchInput!) " +
            "{ updateExtension(input: { id: $id, patch: $patch }) { extension { " + ExtensionFields + " } } }",
            new JsonObject { ["id"] = packageName, ["patch"] = patch }, ct);
        var e = d.GetProperty("updateExtension").GetProperty("extension");
        return e.ValueKind == JsonValueKind.Null ? null : ParseExtension(e);
    }

    public async Task<IReadOnlyList<BridgeRepo>> GetReposAsync(CancellationToken ct = default)
    {
        var d = await SendAsync("Repos", "query Repos { extensionStores { nodes { name indexUrl } } }", null, ct);
        return d.GetProperty("extensionStores").GetProperty("nodes").EnumerateArray()
            .Select(n => new BridgeRepo(Str(n, "name") ?? "", Str(n, "indexUrl") ?? "")).ToList();
    }

    public async Task<BridgeRepo> AddRepoAsync(string indexUrl, CancellationToken ct = default)
    {
        var d = await SendAsync("AddRepo",
            "mutation AddRepo($url: String!) { addExtensionStore(input: { indexUrl: $url }) { extensionStore { name indexUrl } } }",
            new JsonObject { ["url"] = indexUrl }, ct);
        var s = d.GetProperty("addExtensionStore").GetProperty("extensionStore");
        return new(Str(s, "name") ?? "", Str(s, "indexUrl") ?? indexUrl);
    }

    public async Task RemoveRepoAsync(string indexUrl, CancellationToken ct = default) =>
        await SendAsync("RemoveRepo",
            "mutation RemoveRepo($url: String!) { removeExtensionStore(input: { indexUrl: $url }) { clientMutationId } }",
            new JsonObject { ["url"] = indexUrl }, ct);

    // ── Backups ─────────────────────────────────────────────────────

    /// <summary>
    /// Hands a Mihon backup to the server so it knows the backed-up titles and their chapters by
    /// the sources' own URLs. Only titles and chapters: the app keeps categories, history and
    /// reading state itself, and the server's settings are left alone. Returns the restore's id.
    /// </summary>
    public async Task<string> RestoreBackupAsync(byte[] backup, CancellationToken ct = default)
    {
        // GraphQL multipart request: the file travels as a part and "map" says which variable it fills.
        var operations = new JsonObject
        {
            ["operationName"] = "RestoreBackup",
            ["query"] = "mutation RestoreBackup($backup: Upload!, $flags: PartialBackupFlagsInput) " +
                        "{ restoreBackup(input: { backup: $backup, flags: $flags }) { id } }",
            ["variables"] = new JsonObject
            {
                ["backup"] = null,
                ["flags"] = new JsonObject
                {
                    ["includeManga"] = true, ["includeChapters"] = true, ["includeCategories"] = false,
                    ["includeTracking"] = false, ["includeHistory"] = false, ["includeClientData"] = false,
                    ["includeServerSettings"] = false,
                },
            },
        };
        using var form = new MultipartFormDataContent
        {
            { new StringContent(operations.ToJsonString()), "operations" },
            { new StringContent("""{"0":["variables.backup"]}"""), "map" },
            { new ByteArrayContent(backup), "0", "backup.tachibk" },
        };
        var d = await PostAsync(form, ct);
        return Str(d.GetProperty("restoreBackup"), "id") ?? throw BridgeException.FromServer("The restore didn't start.");
    }

    /// <summary>A restore's progress: state (IDLE, RESTORING_…, SUCCESS, FAILURE), titles done and total.</summary>
    public async Task<(string State, int Done, int Total)> RestoreStatusAsync(string id, CancellationToken ct = default)
    {
        var d = await SendAsync("RestoreStatus",
            "query RestoreStatus($id: String!) { restoreStatus(id: $id) { state mangaProgress totalManga } }",
            new JsonObject { ["id"] = id }, ct);
        var s = d.GetProperty("restoreStatus");
        if (s.ValueKind == JsonValueKind.Null) return ("IDLE", 0, 0);
        return (Str(s, "state") ?? "IDLE", s.GetProperty("mangaProgress").GetInt32(), s.GetProperty("totalManga").GetInt32());
    }

    // ── Transport ───────────────────────────────────────────────────

    /// <summary>Runs one operation and returns its <c>data</c>; server errors become a <see cref="BridgeException"/>.</summary>
    public async Task<JsonElement> SendAsync(string operation, string document, JsonObject? variables, CancellationToken ct)
    {
        var body = new JsonObject { ["operationName"] = operation, ["query"] = document };
        if (variables != null) body["variables"] = variables;

        return await PostAsync(JsonContent.Create(body), ct);
    }

    private async Task<JsonElement> PostAsync(HttpContent content, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(new Uri(BaseUri, "api/graphql"), content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw BridgeException.NotRunning("The Mihon extensions server isn't answering.", ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException)
            {
                throw BridgeException.NotRunning($"The Mihon extensions server answered HTTP {(int)resp.StatusCode} with something that isn't GraphQL.");
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    throw BridgeException.FromServer(Str(errors[0], "message") ?? "Unknown error");
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw BridgeException.NotRunning($"The Mihon extensions server answered HTTP {(int)resp.StatusCode} without data.");
                return data.Clone();
            }
        }
    }

    // ── Parsing ─────────────────────────────────────────────────────

    private static BridgeManga ParseManga(JsonElement m) => new(
        m.GetProperty("id").GetInt32(), Str(m, "title") ?? "", Str(m, "thumbnailUrl"), Str(m, "url") ?? "",
        Str(m, "realUrl"), Str(m, "status"), Str(m, "author"), Str(m, "artist"), Str(m, "description"),
        m.TryGetProperty("genre", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : []);

    private static BridgeExtension ParseExtension(JsonElement e) => new(
        Str(e, "pkgName") ?? "", Str(e, "name") ?? "", Str(e, "lang") ?? "", Str(e, "versionName") ?? "",
        Bool(e, "isInstalled"), Bool(e, "hasUpdate"), Bool(e, "isObsolete"),
        // SAFE / MIXED / NSFW. The server itself counts MIXED as adult; so does the app.
        Str(e, "contentWarning") is "MIXED" or "NSFW",
        Str(e, "storeIndexUrl"),
        e.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.GetProperty("nodes").EnumerateArray().Select(n => IdStr(n, "id")).ToList()
            : []);

    private static IReadOnlyList<BridgePreference> ParsePreferences(JsonElement list)
    {
        var result = new List<BridgePreference>();
        var position = 0;
        foreach (var p in list.EnumerateArray())
        {
            var kind = Str(p, "__typename") switch
            {
                "SwitchPreference" => PreferenceKind.Switch,
                "CheckBoxPreference" => PreferenceKind.CheckBox,
                "EditTextPreference" => PreferenceKind.EditText,
                "ListPreference" => PreferenceKind.List,
                "MultiSelectListPreference" => PreferenceKind.MultiSelect,
                _ => (PreferenceKind?)null,
            };
            if (kind is { } k)
            {
                bool? b = k switch
                {
                    PreferenceKind.Switch => BoolOr(p, "switchValue", "switchDefault"),
                    PreferenceKind.CheckBox => BoolOr(p, "checkBoxValue", "checkBoxDefault"),
                    _ => null,
                };
                var text = k switch
                {
                    PreferenceKind.EditText => Str(p, "textValue") ?? Str(p, "textDefault"),
                    PreferenceKind.List => Str(p, "listValue") ?? Str(p, "listDefault"),
                    _ => null,
                };
                var values = k == PreferenceKind.MultiSelect
                    ? Strings(p, "multiValue") ?? Strings(p, "multiDefault") ?? []
                    : [];
                result.Add(new(position, k, Str(p, "key"), Str(p, "title"), Str(p, "summary"),
                    !p.TryGetProperty("visible", out var v) || v.ValueKind != JsonValueKind.False,
                    !p.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
                    b, text, values, Strings(p, "entries") ?? [], Strings(p, "entryValues") ?? [],
                    Str(p, "dialogTitle"), Str(p, "dialogMessage")));
            }
            position++; // positions count every preference, even a kind this build doesn't know
        }
        return result;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool? BoolOr(JsonElement e, string first, string fallback) =>
        e.TryGetProperty(first, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean()
        : e.TryGetProperty(fallback, out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean()
        : null;

    private static List<string>? Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : null;

    /// <summary>64-bit ids come back as strings (the LongString scalar); accept a number too.</summary>
    private static string IdStr(JsonElement e, string name) =>
        e.GetProperty(name) is var v && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64().ToString(CultureInfo.InvariantCulture)
            : v.GetString() ?? "";

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
    }
}
