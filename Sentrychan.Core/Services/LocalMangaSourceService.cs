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

    // The id is the name: tracked manga already store "Local" as their Source.
    public MangaSourceInfo Info => new("Local", "Local", "all", IsNsfw: false, SupportsLatest: true);

    private const int PageSize = IMangaSourceService.DefaultPageSize;
    private static readonly string[] SortValues = ["Title", "Date modified"];

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

    // ── Contract v2 ─────────────────────────────────────────────────

    public FilterList GetFilterList() => new(new SortFilter("Sort by", SortValues, new SortSelection(0, true)));

    public Task<MangaPage> GetPopularAsync(int page, CancellationToken ct = default) =>
        ListAsync(string.Empty, new SortSelection(0, true), page, ct);

    // "Latest" for a folder on disk: whatever changed most recently — a chapter added or re-downloaded.
    public Task<MangaPage> GetLatestAsync(int page, CancellationToken ct = default) =>
        ListAsync(string.Empty, new SortSelection(1, false), page, ct);

    public Task<MangaPage> SearchAsync(string query, int page, FilterList filters, CancellationToken ct = default) =>
        ListAsync(query, filters.Find<SortFilter>("Sort by")?.State ?? new SortSelection(0, true), page, ct);

    private async Task<MangaPage> ListAsync(string query, SortSelection sort, int page, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        if (root == null || page < 1) return MangaPage.Empty;

        var dirs = Directory.EnumerateDirectories(root)
            .Select(d => new DirectoryInfo(d))
            .Where(d => string.IsNullOrWhiteSpace(query) || d.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

        dirs = sort.Index == 1
            ? (sort.Ascending ? dirs.OrderBy(LastChange) : dirs.OrderByDescending(LastChange))
            : (sort.Ascending ? dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                              : dirs.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase));

        var slice = dirs.Skip((page - 1) * PageSize).Take(PageSize + 1).ToList();
        var items = slice.Take(PageSize).Select(d => new MangaSearchResult(
            SourceId: d.Name, Title: d.Name, OriginalTitle: null, Description: null,
            CoverUrl: FirstImageIn(d.FullName) ?? string.Empty,
            Status: null, Year: null, LastChapter: null, AltTitles: [], IsAdult: false)).ToList();
        return new MangaPage(items, slice.Count > PageSize);
    }

    // A manga folder's own timestamp only moves when a chapter folder is added or removed;
    // pages written into an existing chapter show on the chapter folder.
    private static DateTime LastChange(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateDirectories().Select(c => c.LastWriteTimeUtc)
                      .Append(dir.LastWriteTimeUtc).Max();
        }
        catch { return dir.LastWriteTimeUtc; }
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
