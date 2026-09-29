using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Sentrychan.Tests.MihonBridge;

/// <summary>
/// Answers GraphQL requests from the recorded fixtures, chosen by operation name, and keeps each
/// request so a test can check what was sent. Anything else (image and download URLs) comes
/// from <see cref="Files"/>.
/// </summary>
internal sealed class FakeSuwayomi : HttpMessageHandler
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Suwayomi", name + ".json"));

    /// <summary>Operation name → fixture name (or raw JSON when it starts with "{").</summary>
    public Dictionary<string, string> Responses { get; } = new()
    {
        ["About"] = "about",
        ["Sources"] = "sources",
        ["Filters"] = "filters-all",
        ["FetchManga"] = "manga",
        ["FindManga"] = "bycond",
        ["FetchChapters"] = "chapters",
        ["FetchChapterPages"] = "pages",
        ["Preferences"] = "preferences",
        ["SetPreference"] = "{\"data\":{\"updateSourcePreference\":" + "{\"preferences\":[]}}}",
        ["Extensions"] = "extensions",
        ["Repos"] = "repos",
    };

    public Dictionary<string, (byte[] Body, HttpStatusCode Status)> Files { get; } = new();

    public List<JsonObject> Requests { get; } = [];

    /// <summary>Until this is true, GraphQL calls fail like a server that isn't listening yet.</summary>
    public Func<bool> Listening { get; set; } = () => true;

    /// <summary>The fetchSourceManga fixture per listing type.</summary>
    public string ListingFixture(string type) => type switch { "LATEST" => "latest", "SEARCH" => "search", _ => "popular" };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (!url.EndsWith("/api/graphql", StringComparison.Ordinal))
        {
            if (Files.TryGetValue(url, out var file))
                return new HttpResponseMessage(file.Status) { Content = new ByteArrayContent(file.Body) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!Listening()) throw new HttpRequestException("Connection refused");
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        lock (Requests) Requests.Add(body);
        var op = body["operationName"]!.GetValue<string>();
        var name = op == "FetchSourceManga"
            ? ListingFixture(body["variables"]!["type"]!.GetValue<string>())
            : Responses.GetValueOrDefault(op) ?? throw new InvalidOperationException("No fixture for " + op);
        var json = name.StartsWith('{') ? name : Fixture(name);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public JsonObject Last(string operation)
    {
        lock (Requests) return Requests.Last(r => r["operationName"]!.GetValue<string>() == operation);
    }
}
