using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using System.Text.RegularExpressions;

namespace Sentrychan.Core.Services;

public class LibraryScanService : ILibraryScanService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEpisodeNormalizer _normalizer;
    private readonly ITitleResolverService _titleResolver;
    private readonly ILogger<LibraryScanService> _logger;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov", ".flv", ".wmv"
    };

    // Folders the app itself manages — never "unknown".
    private static readonly HashSet<string> ReservedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Unmatched", "_Standalone"
    };

    private static readonly Regex InvalidFolderCharsPattern =
        new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    public LibraryScanService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEpisodeNormalizer normalizer,
        ITitleResolverService titleResolver,
        ILogger<LibraryScanService> logger)
    {
        _dbFactory = dbFactory;
        _normalizer = normalizer;
        _titleResolver = titleResolver;
        _logger = logger;
    }

    public async Task<LibraryScanReport?> ScanAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var libraryPath = (await db.AppConfigs
            .FirstOrDefaultAsync(c => c.Key == "LibraryPath", ct))?.Value;
        if (string.IsNullOrEmpty(libraryPath))
            return null;

        // Deleted by hand: this used to report "library path not configured" and stop, and nothing
        // made the folder again until a download happened to be filed.
        if (!Library.LibraryFolder.Ensure(libraryPath, out var problem, out var created))
            throw new IOException(problem);
        var recreated = created ? libraryPath : null;
        if (created) _logger.LogWarning("[LibraryScan] the library folder {Path} was missing — created it again", libraryPath);

        var allSeries = await db.Series.AsNoTracking().ToListAsync(ct);

        var topLevelDirs = Directory.EnumerateDirectories(libraryPath)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !ReservedFolders.Contains(d.Name) && !d.Name.StartsWith('.'))
            .ToList();

        // ── Pass 1: claim folders by the app's own naming ──────────────
        // "Show", the naming template's "Show (2023)", or a release-tagged folder all name the
        // show "Show". Every season of a show shares its folder, and a half-tidied library can
        // hold both an old and a new folder — so a series may claim several, and a folder
        // may belong to several series.
        var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // folder name → first series idx
        var foldersBySeries = new Dictionary<int, List<DirectoryInfo>>();            // series idx → folders

        for (int i = 0; i < allSeries.Count; i++)
        {
            var key = Library.LibraryShows.Key(SeasonDetector.ExtractBaseTitle(allSeries[i].Title));
            foreach (var dir in topLevelDirs.Where(d => Library.LibraryShows.FolderKeys(d.Name).Contains(key)))
            {
                claimed.TryAdd(dir.Name, i);
                (foldersBySeries.TryGetValue(i, out var list) ? list : foldersBySeries[i] = []).Add(dir);
            }
        }

        // ── Pass 2: claim leftover folders via the title resolver ─────
        // Handles folders the user created/renamed manually.
        if (_titleResolver.IsReady)
        {
            var byMalId = new Dictionary<int, int>();
            for (int i = 0; i < allSeries.Count; i++)
                if (allSeries[i].MalId > 0)
                    byMalId.TryAdd(allSeries[i].MalId, i);

            foreach (var dir in topLevelDirs.Where(d => !claimed.ContainsKey(d.Name)))
            {
                var resolved = _titleResolver.ResolveTitle(dir.Name);
                if (resolved is { MalId: > 0 }
                    && byMalId.TryGetValue(resolved.MalId, out var idx)
                    && !foldersBySeries.ContainsKey(idx))
                {
                    claimed[dir.Name] = idx;
                    foldersBySeries[idx] = [dir];
                }
            }
        }

        // ── Build per-series results ───────────────────────────────────
        var results = new List<SeriesScanResult>(allSeries.Count);
        for (int i = 0; i < allSeries.Count; i++)
        {
            var series = allSeries[i];
            var expected = SanitizeFolderName(SeasonDetector.ExtractBaseTitle(series.Title));
            var dirs = foldersBySeries.TryGetValue(i, out var found) ? found : [];

            var episodes = new List<int>();
            var mySeason = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);
            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir.FullName, "*", SearchOption.AllDirectories))
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;

                        var name = Path.GetFileNameWithoutExtension(file);
                        int? ep;
                        // A show's seasons share its folder, and names may count straight through
                        // ("Season 02/… - 18" is season 2's episode 6): the matcher says whose episode a
                        // file is, and which, in this series' own numbering.
                        var (verdict, matched) = _titleResolver.IsReady
                            ? ReleaseMatcher.Match(_titleResolver, name, series)
                            : (ReleaseVerdict.Unknown, null);
                        if (verdict == ReleaseVerdict.No) continue;
                        if (verdict == ReleaseVerdict.Yes) ep = matched;
                        else
                        {
                            // A name that says nothing of the show (the Minimal preset's bare "05"):
                            // its season folder tells.
                            if (SeasonFolderNumber(file) is { } folderSeason && folderSeason != mySeason) continue;
                            ep = (_titleResolver.IsReady ? _titleResolver.ParseRelease(name).Episode : null)
                                 ?? _normalizer.ExtractEpisodeNumber(name)
                                 ?? Library.ReleaseNameParser.Parse(name).Episode;
                        }
                        if (ep.HasValue && !episodes.Contains(ep.Value))
                            episodes.Add(ep.Value);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LibraryScan] Failed reading folder {Dir}", dir.FullName);
                }
            }

            results.Add(new SeriesScanResult(
                SeriesId: series.Id,
                MalId: series.MalId,
                Title: series.Title,
                ExpectedFolder: expected,
                FolderExists: dirs.Count > 0,
                EpisodesOnDisk: episodes,
                LastEpisodeNumber: series.LastEpisodeNumber));
        }

        var unknown = topLevelDirs
            .Where(d => !claimed.ContainsKey(d.Name))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "[LibraryScan] {Series} series checked — {Missing} folders missing, {Advance} progress advances, {Unknown} unknown folders",
            results.Count,
            results.Count(r => !r.FolderExists),
            results.Count(r => r.HasProgressAdvance),
            unknown.Count);

        return new LibraryScanReport(results, unknown) { RecreatedLibraryFolder = recreated };
    }

    public async Task<string> RecreateFolderAsync(int seriesId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        string? Config(string key) => db.AppConfigs.AsNoTracking().FirstOrDefault(c => c.Key == key)?.Value;

        var libraryPath = Config("LibraryPath");
        if (string.IsNullOrWhiteSpace(libraryPath))
            throw new InvalidOperationException("No anime folder is set (Settings → General).");
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct)
                     ?? throw new InvalidOperationException("That series isn't in the library any more.");
        var all = await db.Series.AsNoTracking().ToListAsync(ct);

        var naming = Library.NamingTemplate.FromConfig(Config(Library.NamingTemplate.PresetKey), Config(Library.NamingTemplate.TemplateKey));
        var folder = Path.Combine(libraryPath, Library.LibraryFiling.ShowFolder(naming, series, all));
        Directory.CreateDirectory(folder);
        _logger.LogInformation("[LibraryScan] recreated the folder for {Title}: {Folder}", Vault.Privacy.Name(series.Title), folder);
        return folder;
    }

    public async Task<int> ApplyProgressAdvancesAsync(LibraryScanReport report, CancellationToken ct = default)
    {
        var advances = report.ProgressAdvances;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int changed = 0;

        // Progress past a finished season's length can only come from a number counted straight
        // through the show ("Sono Bisque Doll - 24" read as season 2's episode 24, before the
        // matcher knew better): it's the last episode, not 24. Never touches an airing season.
        var ids = report.Series.Select(s => s.SeriesId).ToList();
        foreach (var s in await db.Series.Where(s => ids.Contains(s.Id)).ToListAsync(ct))
        {
            if (s.TotalEpisodes is > 0 and var total && s.LastEpisodeNumber > total
                && AiringStatusNormalizer.Normalize(s.AiringStatus) is "Finished Airing")
            {
                _logger.LogInformation("[LibraryScan] {Title}: progress {Old} is past its {Total} episodes — set to {Total}",
                    s.Title, s.LastEpisodeNumber, total, total);
                s.LastEpisodeNumber = total;
                changed++;
            }
        }
        if (advances.Count == 0)
        {
            if (changed > 0) await db.SaveChangesAsync(ct);
            return changed;
        }

        foreach (var result in advances)
        {
            var series = await db.Series.FindAsync([result.SeriesId], ct);
            if (series == null) continue;

            // Raise only — never lower (deleting watched files ≠ unwatching).
            if (result.MaxEpisodeOnDisk > series.LastEpisodeNumber)
            {
                _logger.LogInformation(
                    "[LibraryScan] {Title}: cursor {Old} → {New} (files on disk)",
                    series.Title, series.LastEpisodeNumber, result.MaxEpisodeOnDisk);
                series.LastEpisodeNumber = result.MaxEpisodeOnDisk;
                changed++;
            }
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    private static string SanitizeFolderName(string name) =>
        InvalidFolderCharsPattern.Replace(name, string.Empty).Trim();

    private static readonly Regex SeasonFolderPattern = new(@"^Season\s*0*(\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The N of the "Season N" folder a file sits in, if it does.</summary>
    private static int? SeasonFolderNumber(string file) =>
        SeasonFolderPattern.Match(Path.GetFileName(Path.GetDirectoryName(file)) ?? "") is { Success: true } m
            ? int.Parse(m.Groups[1].Value) : null;
}
