using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.MihonBackup;

public enum ImportMatch
{
    /// <summary>Already in the library: categories and reading state are brought over.</summary>
    InLibrary,

    /// <summary>Not in the library, but its source is an installed Mihon extension: it's added.</summary>
    Add,

    /// <summary>Can't be placed; <see cref="ImportTitle.Reason"/> says why.</summary>
    Unmatched,
}

/// <summary>One backed-up title and what the import will do with it.</summary>
public sealed class ImportTitle
{
    internal ImportTitle(BackupManga backup, string sourceName) { Backup = backup; SourceName = sourceName; }

    public BackupManga Backup { get; }
    public string Title => Backup.Title;

    /// <summary>The source's name as the backup lists it.</summary>
    public string SourceName { get; }

    public ImportMatch Match { get; internal set; }
    public string? Reason { get; internal set; }

    internal int? MangaId { get; set; }
    internal BridgedMangaSource? Bridged { get; set; }
    internal BridgeManga? BridgeManga { get; set; }
}

/// <summary>What an import would do, worked out before anything is written.</summary>
public sealed class ImportPlan
{
    internal ImportPlan(MihonBackupData backup, byte[] file, List<ImportTitle> titles, List<string> newCategories)
    {
        Backup = backup;
        File = file;
        Titles = titles;
        NewCategories = newCategories;
    }

    public MihonBackupData Backup { get; }
    internal byte[] File { get; }
    public IReadOnlyList<ImportTitle> Titles { get; }
    public IReadOnlyList<string> NewCategories { get; }

    public int InLibrary => Titles.Count(t => t.Match == ImportMatch.InLibrary);
    public int ToAdd => Titles.Count(t => t.Match == ImportMatch.Add);
    public IEnumerable<ImportTitle> Unmatched => Titles.Where(t => t.Match == ImportMatch.Unmatched);

    /// <summary>Sources titles couldn't be matched for, most titles first — what to install to match more.</summary>
    public IReadOnlyList<(string Name, int Titles)> MissingSources =>
        Unmatched.GroupBy(t => t.SourceName).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToList();
}

public sealed record ImportReport(int Updated, int Added, int ChaptersRead, int Bookmarks, int History,
    int CategoriesCreated, IReadOnlyList<ImportTitle> Unmatched);

/// <summary>
/// Imports a Mihon backup: library entries, categories, read chapters, bookmarks and history.
///
/// <para>Titles whose source is an installed Mihon extension (the bridge) are matched exactly, by
/// the source's own URL: the backup is handed to the helper server first so it knows those
/// titles and their chapters, which also means adding them costs no requests to the sites.
/// Without the bridge, titles already in the library are matched by name and their chapters by
/// number, so categories and reading state still come over.</para>
///
/// <para>Reading state only ever moves forward: nothing read here is marked unread.</para>
/// </summary>
public sealed class MihonBackupImporter(
    IDbContextFactory<AppDbContext> dbFactory,
    IMangaService mangaService,
    MangaLibraryService library,
    IMangaSourceRegistry registry,
    MihonBridgeService? bridge,
    ILogger<MihonBackupImporter> log)
{
    public async Task<ImportPlan> PlanAsync(byte[] file, CancellationToken ct = default)
    {
        var backup = TachibkReader.Read(file);
        var inLibrary = await mangaService.GetAllAsync(ct);
        var bridgeOn = bridge != null && await bridge.IsEnabledAsync(ct);
        var bridged = registry.Sources.OfType<BridgedMangaSource>().ToDictionary(s => s.Source.Id);
        SuwayomiClient? client = null;
        string? bridgeProblem = null;
        var titles = new List<ImportTitle>();

        foreach (var m in backup.Manga)
        {
            ct.ThrowIfCancellationRequested();
            var t = new ImportTitle(m, backup.SourceName(m.Source) ?? $"Source {m.Source}");

            if (bridgeOn && bridgeProblem == null
                && bridged.TryGetValue(m.Source.ToString(CultureInfo.InvariantCulture), out var source))
            {
                try
                {
                    client ??= await bridge!.ClientAsync(ct);
                    t.Bridged = source;
                    t.BridgeManga = await client.FindMangaAsync(source.Source.Id, m.Url, ct);
                    var existing = t.BridgeManga == null ? null
                        : inLibrary.FirstOrDefault(x => x.Source == source.SourceName
                            && x.SourceId == t.BridgeManga.Id.ToString(CultureInfo.InvariantCulture));
                    if (existing != null) { t.Match = ImportMatch.InLibrary; t.MangaId = existing.Id; }
                    else if (m.Favorite) t.Match = ImportMatch.Add;
                    else continue; // only read, never in their library: nothing to bring over
                    titles.Add(t);
                    continue;
                }
                catch (BridgeException ex)
                {
                    // One failed start is enough: match the rest by name.
                    bridgeProblem = ex.Message;
                    t.Bridged = null;
                }
            }

            var match = ByTitle(inLibrary, m.Title, t.SourceName, out var ambiguous);
            if (match != null) { t.Match = ImportMatch.InLibrary; t.MangaId = match.Id; }
            else if (!m.Favorite) continue;
            else
            {
                t.Match = ImportMatch.Unmatched;
                t.Reason = ambiguous ? "Several titles in your library have this name."
                    : bridgeProblem != null ? "Mihon extensions couldn't be reached: " + bridgeProblem
                    : !bridgeOn ? "Not in your library, and Mihon extensions are off."
                    : $"Not in your library, and {t.SourceName} isn't installed as a Mihon extension.";
            }
            titles.Add(t);
        }

        var have = (await library.GetCategoriesAsync(ct)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newCategories = backup.Categories.OrderBy(c => c.Order).Select(c => c.Name.Trim())
            .Where(n => n.Length > 0 && !have.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ImportPlan(backup, file, titles, newCategories);
    }

    public async Task<ImportReport> ApplyAsync(ImportPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Titles the helper server doesn't know yet: restore the backup into it first.
        var unknown = plan.Titles.Where(t => t.Match == ImportMatch.Add && t.BridgeManga == null).ToList();
        if (unknown.Count > 0) await RestoreIntoBridgeAsync(plan, unknown, progress, ct);

        var categoryIds = await EnsureCategoriesAsync(plan, ct);
        int updated = 0, added = 0, read = 0, bookmarks = 0, history = 0;
        var todo = plan.Titles.Where(t => t.Match != ImportMatch.Unmatched).ToList();
        for (var i = 0; i < todo.Count; i++)
        {
            var t = todo[i];
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Importing {i + 1} of {todo.Count}: {t.Title}");
            var adding = t.Match == ImportMatch.Add;
            try
            {
                if (adding) t.MangaId = await AddAsync(t, ct);
                var (r, b, h) = await ApplyReadingStateAsync(t, ct);
                await ApplyCategoriesAsync(t, categoryIds, ct);
                if (adding) added++; else updated++;
                read += r; bookmarks += b; history += h;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Mihon import: {Title} failed", t.Title);
                if (adding && t.MangaId != null) { added++; continue; } // it's in the library; the rest can be redone
                t.Match = ImportMatch.Unmatched;
                t.Reason = ex.Message;
            }
        }
        return new ImportReport(updated, added, read, bookmarks, history, plan.NewCategories.Count,
            plan.Unmatched.ToList());
    }

    // ── Steps ───────────────────────────────────────────────────────

    private async Task RestoreIntoBridgeAsync(ImportPlan plan, List<ImportTitle> unknown, IProgress<string>? progress, CancellationToken ct)
    {
        var client = await bridge!.ClientAsync(ct);
        progress?.Report("Handing the backup to the Mihon extensions server…");
        var id = await client.RestoreBackupAsync(plan.File, ct);
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (true)
        {
            var (state, done, total) = await client.RestoreStatusAsync(id, ct);
            if (state is "SUCCESS" or "FAILURE") break;
            if (DateTime.UtcNow > deadline) break;
            if (total > 0) progress?.Report($"The Mihon extensions server is reading the backup: {done} of {total}…");
            await Task.Delay(500, ct);
        }
        foreach (var t in unknown)
        {
            t.BridgeManga = await client.FindMangaAsync(t.Bridged!.Source.Id, t.Backup.Url, ct);
            if (t.BridgeManga != null) continue;
            t.Match = ImportMatch.Unmatched;
            t.Reason = "The Mihon extensions server couldn't restore this title.";
        }
    }

    private async Task<Dictionary<long, int>> EnsureCategoriesAsync(ImportPlan plan, CancellationToken ct)
    {
        foreach (var name in plan.NewCategories) await library.CreateCategoryAsync(name, ct);
        var byName = (await library.GetCategoriesAsync(ct)).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var byOrder = new Dictionary<long, int>();
        foreach (var c in plan.Backup.Categories)
            if (byName.TryGetValue(c.Name.Trim(), out var id)) byOrder.TryAdd(c.Order, id);
        return byOrder;
    }

    private async Task<int> AddAsync(ImportTitle t, CancellationToken ct)
    {
        var source = t.Bridged!;
        var details = source.ToResult(t.BridgeManga!);
        var manga = await mangaService.AddAsync(new Manga
        {
            Source = source.SourceName,
            SourceId = details.SourceId,
            Title = details.Title,
            Description = details.Description,
            CoverPath = details.CoverUrl,
            Status = details.Status,
            IsCensored = details.IsAdult,
            AddedAt = t.Backup.DateAdded > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t.Backup.DateAdded).UtcDateTime : DateTime.UtcNow,
        }, ct);
        return manga.Id;
    }

    private async Task<(int Read, int Bookmarks, int History)> ApplyReadingStateAsync(ImportTitle t, CancellationToken ct)
    {
        var mangaId = t.MangaId!.Value;
        // A bridged title's chapters are the server's, keyed by the source's URL — exact. They're
        // read from the server's database (no request to the site) and synced in place.
        Dictionary<string, string>? chapterByUrl = null;
        if (t.Bridged != null && t.BridgeManga != null)
        {
            var stored = await (await bridge!.ClientAsync(ct)).GetStoredChaptersAsync(t.BridgeManga.Id, ct);
            if (stored.Count > 0)
                await mangaService.SyncChaptersAsync(mangaId, t.Bridged.ToChapterInfos(stored.Select(s => s.Chapter)), ct);
            chapterByUrl = stored.ToDictionary(s => s.Url, s => s.Chapter.Id.ToString(CultureInfo.InvariantCulture));
        }

        List<MangaChapter> chapters;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            chapters = await db.MangaChapters.AsNoTracking().Where(c => c.MangaId == mangaId).ToListAsync(ct);
        var bySourceId = chapters.GroupBy(c => c.SourceId).ToDictionary(g => g.Key, g => g.First());

        MangaChapter? Find(string url, BackupChapter? backup)
        {
            if (chapterByUrl != null)
                return chapterByUrl.TryGetValue(url, out var sid) && bySourceId.TryGetValue(sid, out var c) ? c : null;
            return backup == null ? null : ChapterMatcher.Find(chapters, backup);
        }

        var readIds = new List<int>();
        var bookmarkIds = new List<int>();
        var backupByUrl = new Dictionary<string, BackupChapter>();
        foreach (var bc in t.Backup.Chapters)
        {
            backupByUrl.TryAdd(bc.Url, bc);
            if (!bc.Read && !bc.Bookmark) continue;
            var c = Find(bc.Url, bc);
            if (c == null) continue;
            if (bc.Read && !c.IsRead) readIds.Add(c.Id);
            if (bc.Bookmark) bookmarkIds.Add(c.Id);
        }
        if (readIds.Count > 0) await library.SetReadAsync(mangaId, readIds, read: true, ct);
        if (bookmarkIds.Count > 0)
        {
            var already = await library.GetBookmarksAsync(mangaId, ct);
            var add = bookmarkIds.Where(id => !already.Contains(id)).ToList();
            if (add.Count > 0) await library.SetBookmarkedAsync(add, true, ct);
            bookmarkIds = add;
        }

        var history = 0;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            foreach (var h in t.Backup.History.Where(h => h.LastRead > 0))
            {
                var c = Find(h.Url, backupByUrl.GetValueOrDefault(h.Url));
                if (c == null) continue;
                var at = DateTimeOffset.FromUnixTimeMilliseconds(h.LastRead).UtcDateTime;
                var row = await db.MangaReadingHistory.FirstOrDefaultAsync(x => x.ChapterId == c.Id, ct);
                if (row == null)
                {
                    db.MangaReadingHistory.Add(new MangaReadingHistory { MangaId = mangaId, ChapterId = c.Id, ReadAt = at });
                    history++;
                }
                else if (row.ReadAt < at) { row.ReadAt = at; history++; }
            }
            await db.SaveChangesAsync(ct);
        }
        return (readIds.Count, bookmarkIds.Count, history);
    }

    private async Task ApplyCategoriesAsync(ImportTitle t, Dictionary<long, int> byOrder, CancellationToken ct)
    {
        var wanted = t.Backup.Categories.Where(byOrder.ContainsKey).Select(o => byOrder[o]).ToList();
        if (wanted.Count == 0) return;
        List<int> current;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            current = await db.MangaCategoryLinks.Where(l => l.MangaId == t.MangaId).Select(l => l.CategoryId).ToListAsync(ct);
        if (wanted.All(current.Contains)) return;
        await library.SetCategoriesAsync([t.MangaId!.Value], current.Union(wanted), ct);
    }

    // ── Matching by name ────────────────────────────────────────────

    /// <summary>
    /// A library title with the same name (ignoring case, spacing and punctuation). With several,
    /// the one on a source of the same name wins; otherwise it's ambiguous and nothing is guessed.
    /// </summary>
    internal static Manga? ByTitle(IReadOnlyList<Manga> library, string title, string sourceName, out bool ambiguous)
    {
        ambiguous = false;
        var key = Normalize(title);
        if (key.Length == 0) return null;
        var hits = library.Where(m => Normalize(m.Title) == key).ToList();
        if (hits.Count <= 1) return hits.FirstOrDefault();
        var sameSource = hits.Where(m => string.Equals(m.Source, sourceName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameSource.Count == 1) return sameSource[0];
        ambiguous = true;
        return null;
    }

    internal static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>Finds a library chapter for a backed-up one when there's no shared key: by number, else by name.</summary>
internal static class ChapterMatcher
{
    public static MangaChapter? Find(IReadOnlyList<MangaChapter> chapters, BackupChapter backup)
    {
        if (backup.ChapterNumber >= 0)
        {
            var hits = chapters.Where(c => c.ChapterSort is { } s && Math.Abs(s - backup.ChapterNumber) < 0.0005).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
                return hits.FirstOrDefault(c => string.Equals(c.ScanlationGroup, backup.Scanlator, StringComparison.OrdinalIgnoreCase))
                       ?? hits[0];
        }
        var name = backup.Name.Trim();
        return chapters.FirstOrDefault(c => string.Equals(c.Title?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(c.ChapterNumber, name, StringComparison.OrdinalIgnoreCase));
    }
}
