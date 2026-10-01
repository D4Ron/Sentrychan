using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Sources;

/// <summary>What importing a sources file did, for the summary the user sees.</summary>
public sealed record SourcesImportResult(
    int FeedsAdded,
    int FeedsAlreadyThere,
    IReadOnlyList<string> FeedsRefused,
    bool GroupsSet,
    bool GroupsKept,
    IReadOnlyList<string> PacksInstalled,
    int ReposAdded,
    int ReposWaiting)
{
    public bool NeedsRestart => PacksInstalled.Count > 0;

    public string Summary()
    {
        var parts = new List<string>();
        if (FeedsAdded > 0) parts.Add(FeedsAdded == 1 ? "1 RSS feed added" : $"{FeedsAdded} RSS feeds added");
        if (FeedsAlreadyThere > 0) parts.Add($"{FeedsAlreadyThere} feed(s) you already had");
        if (FeedsRefused.Count > 0) parts.Add($"{FeedsRefused.Count} feed(s) skipped — {FeedsRefused[0]}");
        if (GroupsSet) parts.Add("preferred release groups set");
        if (GroupsKept) parts.Add("your own preferred groups kept");
        if (PacksInstalled.Count > 0) parts.Add($"{PacksInstalled.Count} source pack(s) installed — restart Sentrychan to load them");
        if (ReposAdded > 0) parts.Add($"{ReposAdded} Mihon repositor{(ReposAdded == 1 ? "y" : "ies")} added");
        if (ReposWaiting > 0) parts.Add($"{ReposWaiting} Mihon repositor{(ReposWaiting == 1 ? "y waits" : "ies wait")} until you turn on Mihon extensions (Settings → Sources)");
        return parts.Count == 0 ? "Nothing new — you already had everything in that file." : string.Join("; ", parts) + ".";
    }
}

/// <summary>
/// Moves a user's sources between installs as one <see cref="SourcesFile"/>: export what this app
/// has, import what a file brings. Importing only ever adds — nothing the user has is replaced
/// or removed, except a source pack of the same file name, which is the newer copy.
/// </summary>
public sealed class SourcesTransferService(
    IDbContextFactory<AppDbContext> dbFactory,
    MihonBridgeService bridge,
    ILogger<SourcesTransferService> log,
    string? packsDir = null)
{
    public const string GroupsKey = "PreferredReleaseGroups";

    private readonly string _packsDir = packsDir ?? AppPaths.Sources;

    /// <summary>Source packs installed for this app (the files the plugin loader reads).</summary>
    public static IReadOnlyList<string> InstalledPacks(string? dir = null)
    {
        dir ??= AppPaths.Sources;
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dll").OrderBy(f => f).ToList() : [];
    }

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

    /// <param name="refuseFeed">Why a feed mustn't be added here (an adult feed outside secret mode), or null.</param>
    public async Task<SourcesImportResult> ImportAsync(string path, Func<string, string?>? refuseFeed = null, CancellationToken ct = default)
    {
        var contents = SourcesFile.Read(path);
        var manifest = contents.Manifest;

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

        bool groupsSet = false, groupsKept = false;
        if (!string.IsNullOrWhiteSpace(manifest.PreferredReleaseGroups))
        {
            var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == GroupsKey, ct);
            if (row == null || string.IsNullOrWhiteSpace(row.Value))
            {
                if (row == null) db.AppConfigs.Add(new AppConfig { Key = GroupsKey, Value = manifest.PreferredReleaseGroups.Trim() });
                else row.Value = manifest.PreferredReleaseGroups.Trim();
                groupsSet = true;
            }
            else groupsKept = !string.Equals(row.Value.Trim(), manifest.PreferredReleaseGroups.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        await db.SaveChangesAsync(ct);

        var packs = SourcesFile.ExtractPacks(path, _packsDir);

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

        log.LogInformation("[Sources] imported a sources file: {Feeds} feed(s), {Packs} pack(s), {Repos} repositories",
            added, packs.Count, repos.Count);
        return new(added, already, refused, groupsSet, groupsKept, packs, reposAdded, reposWaiting);
    }
}
