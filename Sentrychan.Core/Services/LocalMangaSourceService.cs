using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

/// <summary>
/// Reads manga from a local folder — the "import what you already have" source, and it
/// happens to read the exact layout MangaDownloadService writes
/// ({root}/{Title}/{Chapter n}/001.jpg…). Zero network. Configured via the
/// "LocalMangaPath" AppConfig; SourceIds are folder paths relative to that root.
/// </summary>
public class LocalMangaSourceService : IMangaSourceService
{
    public string SourceName => "Local";

    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private static readonly Regex ChapterNum = new(@"(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<LocalMangaSourceService> _logger;

    public LocalMangaSourceService(IDbContextFactory<AppDbContext> dbFactory, ILogger<LocalMangaSourceService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public const string RootConfigKey = "LocalMangaPath";

    private async Task<string?> RootAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var path = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == RootConfigKey, ct))?.Value;
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        var root = await RootAsync(ct);
        if (root == null || page > 1) return []; // local returns everything on page 1

        var results = new List<MangaSearchResult>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(query) &&
                name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

            results.Add(new MangaSearchResult(
                SourceId: name, Title: name, OriginalTitle: null, Description: null,
                CoverUrl: FirstImageIn(dir) ?? string.Empty,
                Status: null, Year: null, LastChapter: null, AltTitles: [], IsAdult: false));
            if (results.Count >= limit) break;
        }
        return results;
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        var root = await RootAsync(ct);
        if (root == null) return null;
        var dir = Path.Combine(root, sourceId);
        if (!Directory.Exists(dir)) return null;
        return new MangaSearchResult(sourceId, Path.GetFileName(dir), null, null,
            FirstImageIn(dir) ?? string.Empty, null, null, null, [], false);
    }

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        var root = await RootAsync(ct);
        if (root == null) return [];
        var dir = Path.Combine(root, sourceId);
        if (!Directory.Exists(dir)) return [];

        var chapters = new List<MangaChapterInfo>();
        foreach (var chDir in Directory.EnumerateDirectories(dir))
        {
            var pages = CountImages(chDir);
            if (pages == 0) continue;

            var chName = Path.GetFileName(chDir);
            var m = ChapterNum.Match(chName);
            var numStr = m.Success ? m.Groups[1].Value : "";
            double? sort = double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

            // Chapter SourceId is the relative "manga/chapter" path.
            chapters.Add(new MangaChapterInfo(
                $"{sourceId}/{chName}", numStr, sort, null, chName, "local", "Local", pages, null));
        }
        return chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        var root = await RootAsync(ct);
        if (root == null) return [];
        var dir = Path.Combine(root, chapterSourceId);
        if (!Directory.Exists(dir)) return [];

        return Directory.EnumerateFiles(dir)
            .Where(IsImage)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList(); // local file paths — AsyncImage loads these directly
    }

    public string GetChapterWebUrl(string chapterSourceId) => chapterSourceId;

    // ── Helpers ─────────────────────────────────────────────────────

    private static bool IsImage(string f) =>
        ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant());

    private static int CountImages(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Count(IsImage); }
        catch { return 0; }
    }

    private static string? FirstImageIn(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(IsImage)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
