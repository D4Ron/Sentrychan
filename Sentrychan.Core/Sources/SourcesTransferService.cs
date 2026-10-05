using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Sources;

/// <summary>What importing a sources file did, for the summary the user sees.</summary>
public sealed record SourcesImportResult(
    int FeedsAdded,
    int FeedsAlreadyThere,
    IReadOnlyList<string> FeedsRefused,
    IReadOnlyList<string> GroupsAdded,
    IReadOnlyList<string> PacksInstalled,
    IReadOnlyList<string> Skipped,
    int ReposAdded,
    int ReposWaiting)
{
    /// <summary>Set by the caller once it has tried to load the installed packs.</summary>
    public IReadOnlyList<SourcePackStatus> PackStatus { get; init; } = [];

    public bool NeedsRestart => PackStatus.Any(p => p.NeedsRestart);
    public bool FoundNothing => FeedsAdded == 0 && FeedsAlreadyThere == 0 && FeedsRefused.Count == 0
                                && GroupsAdded.Count == 0 && PacksInstalled.Count == 0 && ReposAdded == 0 && ReposWaiting == 0;

    public string Summary()
    {
        var parts = new List<string>();
        if (FeedsAdded > 0) parts.Add(FeedsAdded == 1 ? "1 RSS feed added" : $"{FeedsAdded} RSS feeds added");
        if (FeedsAlreadyThere > 0) parts.Add($"{FeedsAlreadyThere} feed(s) you already had");
        if (FeedsRefused.Count > 0) parts.Add($"{FeedsRefused.Count} feed(s) skipped — {FeedsRefused[0]}");
        if (GroupsAdded.Count > 0) parts.Add($"preferred groups added: {string.Join(", ", GroupsAdded)}");
        if (PacksInstalled.Count > 0)
            parts.Add(NeedsRestart
                ? $"{PacksInstalled.Count} source pack(s) installed — restart Sentrychan to use the new copy"
                : $"{PacksInstalled.Count} source pack(s) installed and ready");
        if (ReposAdded > 0) parts.Add($"{ReposAdded} Mihon repositor{(ReposAdded == 1 ? "y" : "ies")} added");
        if (ReposWaiting > 0) parts.Add($"{ReposWaiting} Mihon repositor{(ReposWaiting == 1 ? "y waits" : "ies wait")} until you turn on Mihon extensions (Settings → Sources)");
        if (parts.Count == 0)
            return Skipped.Count > 0
                ? "Nothing could be used from that — " + Skipped[0] + "."
                : "Nothing new — you already had everything in that file.";
        return string.Join("; ", parts) + ".";
    }
}

/// <summary>
/// Moves a user's sources between installs: export what this app has as one
/// <see cref="SourcesFile"/>, import whatever the user picked (<see cref="SourcesInput"/>).
/// Importing only ever adds — nothing the user has is replaced or removed, except a source pack of
/// the same file name, which is the newer copy.
/// </summary>
public sealed class SourcesTransferService(
    IDbContextFactory<AppDbContext> dbFactory,
    MihonBridgeService bridge,
    ILogger<SourcesTransferService> log,
    string? packsDir = null)
{
    public const string GroupsKey = "PreferredReleaseGroups";

    /// <summary>Where things the startup tidy took out of the sources folder are kept.</summary>
    public const string ImportedFolder = ".imported";

    private readonly string _packsDir = packsDir ?? AppPaths.Sources;

    /// <summary>Source packs installed for this app (the files the plugin loader reads).</summary>
    public static IReadOnlyList<string> InstalledPacks(string? dir = null)
    {
        dir ??= AppPaths.Sources;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.dll").Where(f => !SourcesInput.IsJunk(Path.GetFileName(f))).OrderBy(f => f).ToList()
            : [];
    }

    public static IReadOnlyList<string> SplitGroups(string? groups) =>
        (groups ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<SourcesManifest> BuildManifestAsync(string? name = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var manifest = new SourcesManifest
        {
            Name = name,
            CreatedAt = DateTime.UtcNow,
            Feeds = (await db.RssFeeds.AsNoTracking().OrderBy(f => f.Id).ToListAsync(ct))
                .Select(f => new SourcesFeed(f.Url, f.FeedType.ToString(), f.PreferredQuality)).ToList(),
            PreferredReleaseGroups = (await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == GroupsKey, ct))?.Value,
        };

        var repos = new List<string>(await bridge.GetPendingRepositoriesAsync(ct));
        // Repositories added in the app live in the server's database: only readable while it runs.
        if (bridge.State == BridgeState.Running)
        {
            try { repos.AddRange((await (await bridge.ClientAsync(ct)).GetReposAsync(ct)).Select(r => r.IndexUrl)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("[Sources] couldn't read Mihon repositories for export: {Message}", ex.Message);
            }
        }
        manifest.MihonRepositories = repos.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return manifest;
    }

    /// <summary>Writes this app's sources to <paramref name="path"/>. Returns what went in.</summary>
    public async Task<SourcesFileContents> ExportAsync(string path, bool includePacks, string? name = null, CancellationToken ct = default)
    {
        var manifest = await BuildManifestAsync(name, ct);
        var packs = includePacks ? InstalledPacks(_packsDir) : [];
        SourcesFile.Write(path, manifest, packs);
        return new(manifest, packs.Select(Path.GetFileName).ToList()!);
    }

    public Task<SourcesImportResult> ImportAsync(string path, Func<string, string?>? refuseFeed = null, CancellationToken ct = default) =>
        ImportAsync([path], refuseFeed, ct);

    /// <param name="paths">Files and folders, in any of the shapes <see cref="SourcesInput"/> reads.</param>
    /// <param name="refuseFeed">Why a feed mustn't be added here (an adult feed outside secret mode), or null.</param>
    public Task<SourcesImportResult> ImportAsync(IEnumerable<string> paths, Func<string, string?>? refuseFeed = null, CancellationToken ct = default)
    {
        var list = paths.ToList();
        log.LogInformation("[Sources] importing from {Paths}", string.Join(" | ", list.Select(Path.GetFileName)));
        return ImportAsync(SourcesInput.Read(list), refuseFeed, ct);
    }

    public async Task<SourcesImportResult> ImportAsync(SourcesInput input, Func<string, string?>? refuseFeed = null, CancellationToken ct = default)
    {
        var manifest = input.Manifest;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = (await db.RssFeeds.Select(f => f.Url).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, already = 0;
        var refused = new List<string>();
        foreach (var feed in manifest.Feeds.Where(f => !string.IsNullOrWhiteSpace(f.Url)))
        {
            var url = feed.Url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                refused.Add("not a web address");
                continue;
            }
            if (!known.Add(url)) { already++; continue; }
            if (refuseFeed?.Invoke(url) is { } why) { refused.Add(why); continue; }
            db.RssFeeds.Add(new RssFeed
            {
                Url = url,
                FeedType = Enum.TryParse<FeedType>(feed.Type, true, out var t) ? t : FeedType.Priority,
                PreferredQuality = string.IsNullOrWhiteSpace(feed.PreferredQuality) ? null : feed.PreferredQuality,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow,
            });
            added++;
        }

        // Groups are merged: the file's groups the user doesn't have go after their own, so their
        // order (which is their priority) stays as it was.
        var groupsAdded = await AddGroupsAsync(db, manifest.PreferredReleaseGroups, ct);
        await db.SaveChangesAsync(ct);

        var packs = new List<string>();
        foreach (var pack in input.Packs)
        {
            Directory.CreateDirectory(_packsDir);
            var dest = Path.Combine(_packsDir, Path.GetFileName(pack.Name));
            var tmp = dest + ".tmp";
            await File.WriteAllBytesAsync(tmp, pack.Bytes, ct);
            File.Move(tmp, dest, overwrite: true);
            packs.Add(pack.Name);
        }

        int reposAdded = 0, reposWaiting = 0;
        var repos = manifest.MihonRepositories
            .Where(r => Uri.TryCreate(r.Trim(), UriKind.Absolute, out var ru) && ru.Scheme is "http" or "https")
            .Select(r => r.Trim()).ToList();
        if (repos.Count > 0)
        {
            await bridge.QueueRepositoriesAsync(repos, ct);
            try { reposAdded = await bridge.ApplyPendingRepositoriesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("[Sources] Mihon repositories wait for the next server start: {Message}", ex.Message);
            }
            reposWaiting = (await bridge.GetPendingRepositoriesAsync(ct)).Count;
        }

        foreach (var s in input.Skipped) log.LogInformation("[Sources] left out: {Why}", s);
        log.LogInformation("[Sources] imported: {Feeds} feed(s) added, {Groups} group(s) added, packs [{Packs}], {Repos} repositories",
            added, groupsAdded.Count, string.Join(", ", packs), repos.Count);
        return new(added, already, refused, groupsAdded, packs, input.Skipped, reposAdded, reposWaiting);
    }

    private static async Task<IReadOnlyList<string>> AddGroupsAsync(AppDbContext db, string? incoming, CancellationToken ct)
    {
        var groups = SplitGroups(incoming);
        if (groups.Count == 0) return [];
        var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == GroupsKey, ct);
        var mine = SplitGroups(row?.Value).ToList();
        var add = groups.Where(g => !mine.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
        if (add.Count == 0) return [];
        var value = string.Join(",", mine.Concat(add));
        if (row == null) db.AppConfigs.Add(new AppConfig { Key = GroupsKey, Value = value });
        else row.Value = value;
        return add;
    }

    /// <summary>
    /// The feeds and preferred groups the loaded packs suggest — added only when the user has no
    /// feeds (resp. no groups) yet, so it never overrides their own choices. With no pack loaded it
    /// does nothing, which keeps the public build from subscribing itself to anything.
    /// </summary>
    public async Task ApplyPackDefaultsAsync(IReleaseProviders releases, CancellationToken ct = default)
    {
        var feeds = releases.DefaultFeeds;
        var groups = releases.DefaultPreferredGroups;
        if (feeds.Count == 0 && string.IsNullOrWhiteSpace(groups)) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (feeds.Count > 0 && !await db.RssFeeds.AnyAsync(ct))
        {
            foreach (var f in feeds)
                db.RssFeeds.Add(new RssFeed
                {
                    Url = f.Url, FeedType = f.Type, PreferredQuality = f.PreferredQuality,
                    IsEnabled = true, AddedAt = DateTime.UtcNow,
                });
            log.LogInformation("[Sources] seeded {Count} default feed(s) from loaded packs", feeds.Count);
        }
        var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == GroupsKey, ct);
        if (!string.IsNullOrWhiteSpace(groups) && string.IsNullOrWhiteSpace(row?.Value))
        {
            if (row == null) db.AppConfigs.Add(new AppConfig { Key = GroupsKey, Value = groups! });
            else row.Value = groups!;
            log.LogInformation("[Sources] preferred groups set from loaded packs");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Run at startup, before packs load: whatever was put into the sources folder by hand in the
    /// wrong shape is set right — the unpacked <c>packs/</c> folder, <c>sources.json</c>, a
    /// <c>.scsources</c> or zip dropped in whole, and the "._" files macOS leaves on copied files.
    /// What was taken in is moved to <see cref="ImportedFolder"/>, never deleted.
    /// </summary>
    public async Task<SourcesImportResult?> TidySourcesFolderAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_packsDir)) return null;

        foreach (var junk in Directory.EnumerateFileSystemEntries(_packsDir)
                     .Where(p => SourcesInput.IsJunk(Path.GetFileName(p)) && Path.GetFileName(p) != "__MACOSX"))
        {
            try { if (File.Exists(junk)) File.Delete(junk); } catch { /* harmless if it stays */ }
        }

        var stray = Directory.EnumerateFileSystemEntries(_packsDir)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                if (name.Equals(ImportedFolder, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
                if (Directory.Exists(p)) return true;
                return !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !SourcesInput.IsJunk(name);
            })
            .ToList();
        if (stray.Count == 0) return null;

        var input = SourcesInput.Read(stray);
        if (input.IsEmpty) return null;

        log.LogInformation("[Sources] found sources put into the sources folder by hand: {Items}",
            string.Join(", ", stray.Select(Path.GetFileName)));
        var result = await ImportAsync(input, ct: ct);

        var keep = Path.Combine(_packsDir, ImportedFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(keep);
        foreach (var item in stray)
        {
            try
            {
                var dest = Path.Combine(keep, Path.GetFileName(item));
                if (Directory.Exists(item)) Directory.Move(item, dest); else File.Move(item, dest);
            }
            catch (Exception ex) { log.LogWarning("[Sources] couldn't move {Item} aside: {Message}", Path.GetFileName(item), ex.Message); }
        }
        return result;
    }
}
