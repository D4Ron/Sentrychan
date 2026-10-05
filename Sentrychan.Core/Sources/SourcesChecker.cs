using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;

namespace Sentrychan.Core.Sources;

public enum CheckState { Ok, Warning, Problem, Info }

/// <summary>One line of the sources check.</summary>
public sealed record SourcesCheckItem(string Title, CheckState State, string Detail);

/// <summary>
/// "Check my sources": what the app has and, when asked, whether it works — each pack loaded and
/// what it brought, each feed fetched, the episode search and every manga source tried once. Run
/// after an import so a user (and the developer, from the log) can see the import took.
/// </summary>
public sealed class SourcesChecker(
    IDbContextFactory<AppDbContext> dbFactory,
    IReleaseProviders releases,
    IMangaSourceRegistry mangaSources,
    MihonBridgeService bridge,
    ISecretModeService secretMode,
    ILogger<SourcesChecker> log,
    ISourcePackHost? packs = null)
{
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(20);

    /// <param name="live">Also reach out: fetch feeds, run a search, open each source's first page.</param>
    public async Task<IReadOnlyList<SourcesCheckItem>> RunAsync(HttpClient http, bool live, CancellationToken ct = default)
    {
        var items = new List<SourcesCheckItem>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var feeds = await db.RssFeeds.AsNoTracking().Where(f => f.IsEnabled).OrderBy(f => f.Id).ToListAsync(ct);
        var groups = (await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == SourcesTransferService.GroupsKey, ct))?.Value;
        var mihonOn = await bridge.IsEnabledAsync(ct);

        // ── Source packs ─────────────────────────────────────────────
        var statuses = packs?.Packs ?? [];
        var files = SourcesTransferService.InstalledPacks();
        if (files.Count == 0)
            items.Add(new("Source packs", feeds.Count > 0 || mihonOn ? CheckState.Info : CheckState.Problem,
                "None installed. A source pack adds episode search and manga sources; feeds work without one."));
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var s = statuses.FirstOrDefault(p => p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            items.Add(s == null
                ? new($"Pack {name}", CheckState.Warning, "installed but not loaded yet — restart Sentrychan")
                : new($"Pack {name}", !s.Loaded ? CheckState.Problem : s.NeedsRestart ? CheckState.Warning : CheckState.Ok, s.Describe()));
        }
        foreach (var stray in StrayItems())
            items.Add(new("Sources folder", CheckState.Warning,
                $"\"{stray}\" isn't in a shape the app reads; it's taken in automatically on the next start."));
        items.Add(new("Sources folder", CheckState.Info, AppPaths.Sources));

        // ── Feeds ────────────────────────────────────────────────────
        if (feeds.Count == 0)
            items.Add(new("RSS feeds", files.Count > 0 || mihonOn ? CheckState.Warning : CheckState.Problem,
                "None. Without a feed nothing is monitored for new episodes."));
        var feedChecks = feeds.Select(async f =>
        {
            var label = "Feed " + Host(f.Url);
            if (!live) return new SourcesCheckItem(label, CheckState.Info, f.Url);
            var check = await RssFeedProbe.CheckAsync(http, f.Url, ct);
            return new SourcesCheckItem(label, check.IsUsable ? CheckState.Ok : CheckState.Problem, check.Message);
        });
        items.AddRange(await Task.WhenAll(feedChecks));

        // ── Preferred groups ─────────────────────────────────────────
        var groupList = SourcesTransferService.SplitGroups(groups);
        items.Add(groupList.Count > 0
            ? new("Preferred groups", CheckState.Ok, string.Join(", ", groupList))
            : new("Preferred groups", CheckState.Info, "None set — releases from any group are taken."));

        // ── Episode search ───────────────────────────────────────────
        if (!releases.HasSearch)
            items.Add(new("Episode search", CheckState.Info, "Not available — it comes with a source pack. Feeds still work."));
        else if (!live)
            items.Add(new("Episode search", CheckState.Ok, "Available"));
        else
            items.Add(await Try("Episode search", async t =>
            {
                var found = await releases.SearchAsync("1080p", ct: t);
                return found.Count > 0
                    ? (CheckState.Ok, $"Works — a test search found {found.Count} releases.")
                    : (CheckState.Warning, "Answered, but a test search found nothing. The site may be down or have changed.");
            }, ct));

        // ── Manga sources from packs ─────────────────────────────────
        var fromPacks = statuses.SelectMany(s => s.MangaSources).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testable = mangaSources.Sources
            .Where(s => fromPacks.Contains(s.SourceName))
            .Where(s => !s.IsAdultSource || secretMode.IsSecretModeActive)
            .ToList();
        if (testable.Count > 0 && !live)
            items.Add(new("Manga sources", CheckState.Ok, string.Join(", ", testable.Select(s => s.SourceName))));
        else if (testable.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            var tests = testable.Select(async src =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await Try("Manga: " + src.SourceName, async t =>
                    {
                        var page = await src.GetPopularAsync(1, t);
                        return page.Items.Count > 0
                            ? (CheckState.Ok, $"Works — {page.Items.Count} titles on its first page.")
                            : (CheckState.Warning, "Answered with nothing. The site may be down or have changed.");
                    }, ct);
                }
                finally { gate.Release(); }
            });
            items.AddRange(await Task.WhenAll(tests));
        }

        // ── Mihon extensions ─────────────────────────────────────────
        items.Add(new("Mihon extensions", CheckState.Info, mihonOn ? $"On ({bridge.State})" : "Off"));

        foreach (var i in items) log.LogInformation("[SourcesCheck] {State} {Title}: {Detail}", i.State, i.Title, i.Detail);
        return items;
    }

    /// <summary>Anything in the sources folder that isn't a pack the loader reads.</summary>
    public static IReadOnlyList<string> StrayItems()
    {
        if (!Directory.Exists(AppPaths.Sources)) return [];
        return Directory.EnumerateFileSystemEntries(AppPaths.Sources)
            .Select(Path.GetFileName)
            .Where(n => n != null && !n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals(SourcesTransferService.ImportedFolder, StringComparison.OrdinalIgnoreCase)
                        && !SourcesInput.IsJunk(n))
            .ToList()!;
    }

    private async Task<SourcesCheckItem> Try(string title, Func<CancellationToken, Task<(CheckState, string)>> test, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LiveTimeout);
        try
        {
            var (state, detail) = await test(timeout.Token);
            return new(title, state, detail);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(title, CheckState.Problem, "No answer within 20 seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "[SourcesCheck] {Title} failed", title);
            return new(title, CheckState.Problem, "Failed — " + ex.Message);
        }
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
