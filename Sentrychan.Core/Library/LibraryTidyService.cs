using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Library;

/// <summary>
/// Tidy library as the app runs it: reads the settings and series, knows which files are busy,
/// applies a plan, undoes the last run, and keeps the database in step.
/// </summary>
public sealed class LibraryTidyService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IActiveTorrentFiles _activeTorrents;
    private readonly ITitleResolverService _resolver;
    private readonly ILogger<LibraryTidyService> _logger;

    public LibraryTidyService(
        IDbContextFactory<AppDbContext> dbFactory,
        IActiveTorrentFiles activeTorrents,
        ITitleResolverService resolver,
        ILogger<LibraryTidyService> logger)
    {
        _dbFactory = dbFactory;
        _activeTorrents = activeTorrents;
        _resolver = resolver;
        _logger = logger;
    }

    public static string JournalDir => AppPaths.Combine("tidy-journals");

    public bool CanUndo => TidyExecutor.LatestUndoable(JournalDir) != null;

    public async Task<NamingTemplate> GetNamingAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ReadNamingAsync(db, ct);
    }

    public async Task SaveNamingAsync(NamingPreset preset, string? customTemplate, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await SetAsync(db, NamingTemplate.PresetKey, preset.ToString(), ct);
        await SetAsync(db, NamingTemplate.TemplateKey, customTemplate ?? string.Empty, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>AppConfig key: library folders tidying skips, one name per line.</summary>
    public const string LeaveAloneKey = "TidyLeaveAlone";

    public async Task<IReadOnlyList<string>> GetLeftAloneAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ReadLeftAloneAsync(db, ct);
    }

    /// <summary>Adds a top-level library folder to, or removes it from, the folders tidying skips.</summary>
    public async Task SetLeftAloneAsync(string folderName, bool leaveAlone, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var folders = (await ReadLeftAloneAsync(db, ct)).ToList();
        folders.RemoveAll(f => string.Equals(f, folderName, StringComparison.OrdinalIgnoreCase));
        if (leaveAlone) folders.Add(folderName);
        await SetAsync(db, LeaveAloneKey, string.Join('\n', folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)), ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<List<string>> ReadLeftAloneAsync(AppDbContext db, CancellationToken ct)
    {
        var value = (await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == LeaveAloneKey, ct))?.Value;
        return (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>The plan for the current library, or null when no library folder is set.</summary>
    public async Task<TidyPlan?> PlanAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var library = (await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == "LibraryPath", ct))?.Value;
        if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library)) return null;

        var naming = await ReadNamingAsync(db, ct);
        var series = await db.Series.ToListAsync(ct);
        if (_resolver.IsReady && LibraryMetadata.Backfill(series, _resolver) > 0)
            await db.SaveChangesAsync(ct);

        var skip = await SkipCheckAsync(db, ct);
        var planner = new TidyPlanner(naming, skip,
            _resolver.IsReady ? name => _resolver.ResolveTitle(name)?.MalId : null,
            await ReadLeftAloneAsync(db, ct),
            TidyPlanner.MatcherPlacement(_resolver));

        var plan = await Task.Run(() => planner.Build(library, series.Select(TidySeries.From).ToList()), ct);
        if (InstanceGuard.PausedForOtherInstance)
            plan.Notes.Insert(0, InstanceGuard.PausedMessage + " Tidying is off here too.");
        return plan;
    }

    public async Task<TidyResult> ApplyAsync(TidyPlan plan, CancellationToken ct = default)
    {
        // The other app files downloads into this same library; moving files under it isn't safe.
        if (InstanceGuard.PausedForOtherInstance)
            throw new InvalidOperationException(InstanceGuard.PausedMessage);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var skip = await SkipCheckAsync(db, ct);
        var result = await Task.Run(() => TidyExecutor.Apply(plan, JournalDir, skip), ct);

        await TidyRecords.UpdateAsync(db, plan.LibraryPath, result.Moved, CancellationToken.None);
        _logger.LogInformation("[Tidy] Moved {Moved} file(s), {Failed} left in place; journal {Journal}",
            result.Moved.Count, result.Failed.Count, Path.GetFileName(result.JournalPath));
        return result;
    }

    /// <summary>Reverses the newest tidy that hasn't been undone. Null when there's nothing to undo.</summary>
    public async Task<TidyResult?> UndoLastAsync(CancellationToken ct = default)
    {
        if (InstanceGuard.PausedForOtherInstance)
            throw new InvalidOperationException(InstanceGuard.PausedMessage);

        var journalPath = TidyExecutor.LatestUndoable(JournalDir);
        if (journalPath == null) return null;
        var library = TidyExecutor.Load(journalPath)?.LibraryPath ?? string.Empty;

        var result = await Task.Run(() => TidyExecutor.Undo(journalPath), ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await TidyRecords.UpdateAsync(db, library, result.Moved, CancellationToken.None);
        _logger.LogInformation("[Tidy] Undid {Moved} move(s), {Failed} couldn't be reversed", result.Moved.Count, result.Failed.Count);
        return result;
    }

    /// <summary>
    /// Why a file mustn't move right now: a torrent is writing it, an unfinished download owns
    /// its name, or something has it open (a player). Built once per plan or apply; the open-file
    /// check runs per file, at the moment it's asked.
    /// </summary>
    private async Task<Func<string, string?>> SkipCheckAsync(AppDbContext db, CancellationToken ct)
    {
        var inFlight = await db.DownloadJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Downloading)
            .Select(j => new { j.ExpectedFileName, j.FinalFilePath })
            .ToListAsync(ct);
        var names = inFlight.Select(j => j.ExpectedFileName).Where(n => !string.IsNullOrEmpty(n))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = inFlight.Select(j => j.FinalFilePath).Where(p => !string.IsNullOrEmpty(p))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return path =>
        {
            if (_activeTorrents.IsActive(path)) return "a torrent is still downloading it";
            if (names.Contains(Path.GetFileName(path)) || paths.Contains(path)) return "an unfinished download owns it";
            return IsOpen(path) ? "it's open in another program (a player?)" : null;
        };
    }

    private static bool IsOpen(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static async Task<NamingTemplate> ReadNamingAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.AppConfigs.AsNoTracking()
            .Where(c => c.Key == NamingTemplate.PresetKey || c.Key == NamingTemplate.TemplateKey)
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        return NamingTemplate.FromConfig(rows.GetValueOrDefault(NamingTemplate.PresetKey),
                                         rows.GetValueOrDefault(NamingTemplate.TemplateKey));
    }

    private static async Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row == null) db.AppConfigs.Add(new AppConfig { Key = key, Value = value });
        else row.Value = value;
    }
}
