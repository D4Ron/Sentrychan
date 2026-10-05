using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Library;
using Sentrychan.Core.Models;
using Sentrychan.Core.Sources;
using Sentrychan.Core.Vault;

namespace Sentrychan.Core.Diagnostics;

/// <summary>
/// One zip a tester can send the developer: what this install is and has, checked against the
/// disk — the app, the folders (and whether they can be written), each series and its folder,
/// recent downloads and where they ended up, settings with secrets removed, the sources check,
/// recent warnings and errors — plus the logs and window screenshots. Built to answer "what
/// happened on their machine" without a back-and-forth.
/// </summary>
public static partial class DiagnosticReport
{
    /// <summary>AppConfig: the first-start diagnosis of this build has run (value: the version).</summary>
    public const string FirstRunKey = "DiagnosisDoneFor";

    /// <param name="uiInfo">What only the UI knows: screens, scaling, open windows.</param>
    /// <param name="screenshots">Window screenshots to include (PNG files).</param>
    /// <param name="sourcesCheck">The sources check, already run.</param>
    public static async Task<string> WriteAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        string? uiInfo,
        IEnumerable<string> screenshots,
        IReadOnlyList<SourcesCheckItem>? sourcesCheck,
        string? reason,
        CancellationToken ct = default)
    {
        var text = await DescribeAsync(dbFactory, uiInfo, sourcesCheck, reason, ct);
        var path = Path.Combine(OutputFolder(), $"{BuildInfo.AppName.Replace(' ', '-')}-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await using (var w = new StreamWriter(zip.CreateEntry("report.txt").Open(), new UTF8Encoding(false)))
                await w.WriteAsync(text);

            // The newest few days of logs, and every crash note. Logs are written shared, so read them that way.
            if (Directory.Exists(AppPaths.Logs))
            {
                var logs = Directory.GetFiles(AppPaths.Logs, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Where(f => f.Name.StartsWith("crash", StringComparison.OrdinalIgnoreCase) || f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-4));
                foreach (var log in logs) AddShared(zip, log.FullName, "logs/" + log.Name);
            }
            foreach (var shot in screenshots.Where(File.Exists))
                AddShared(zip, shot, "screens/" + Path.GetFileName(shot));
        }
        return path;
    }

    /// <summary>The Desktop, where a tester finds it; the data folder when there's no Desktop.</summary>
    private static string OutputFolder()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
        var folder = Path.Combine(AppPaths.DataDir, "reports");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void AddShared(ZipArchive zip, string file, string entryName)
    {
        try
        {
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            src.CopyTo(dst);
        }
        catch { /* a file that can't be read just isn't in the report */ }
    }

    public static async Task<string> DescribeAsync(
        IDbContextFactory<AppDbContext> dbFactory, string? uiInfo, IReadOnlyList<SourcesCheckItem>? sourcesCheck, string? reason, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        void H(string title) => sb.AppendLine().AppendLine("== " + title + " " + new string('=', Math.Max(3, 60 - title.Length)));
        void L(string line) => sb.AppendLine(line);

        L($"{BuildInfo.AppName} problem report — {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        if (!string.IsNullOrWhiteSpace(reason)) L("Why: " + reason);

        H("App");
        var asm = Assembly.GetEntryAssembly();
        L($"Version:   {asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm?.GetName().Version?.ToString()}");
        L($"Flavour:   {BuildInfo.Flavor}{(BuildInfo.IsTestBuild ? " (test build)" : "")}");
        L($"System:    {RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture} · process {RuntimeInformation.ProcessArchitecture}");
        L($"Runtime:   {RuntimeInformation.FrameworkDescription} · {Environment.ProcessorCount} cores · culture {System.Globalization.CultureInfo.CurrentCulture.Name}");
        L($"Started:   {Environment.ProcessPath}");
        L($"Data:      {AppPaths.DataDir}");
        L($"Uptime:    {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime):hh\\:mm\\:ss}");

        if (!string.IsNullOrWhiteSpace(uiInfo)) { H("Screens and windows"); sb.Append(uiInfo.TrimEnd()).AppendLine(); }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var config = await db.AppConfigs.AsNoTracking().ToListAsync(ct);
        string? Config(string key) => config.FirstOrDefault(c => c.Key == key)?.Value;

        H("Folders");
        Folder("Anime library", Config("LibraryPath"));
        Folder("Downloads", Config("DownloadPath"));
        Folder("Manga library", Config("MangaLibraryPath"));
        Folder("App data", AppPaths.DataDir);
        Folder("Sources", AppPaths.Sources);

        void Folder(string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) { L($"{label}: (not set)"); return; }
            if (!Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('/', '\\'));
                L($"{label}: {path}  — MISSING (the folder above it {(parent != null && Directory.Exists(parent) ? "exists" : "is missing too")})");
                return;
            }
            L($"{label}: {path}  — exists, {WriteTest(path)}, {FreeSpace(path)}");
        }

        H("Anime library");
        var library = Config("LibraryPath");
        var series = await db.Series.AsNoTracking().OrderBy(s => s.Title).ToListAsync(ct);
        var naming = NamingTemplate.FromConfig(Config(NamingTemplate.PresetKey), Config(NamingTemplate.TemplateKey));
        L($"Naming: {naming.Preset} ({naming.Template})");
        L($"{series.Count} series");
        var libraryExists = !string.IsNullOrWhiteSpace(library) && Directory.Exists(library);
        foreach (var s in series)
        {
            var title = s.IsCensored ? $"[private series #{s.Id}]" : s.Title;
            string folderInfo;
            try
            {
                var expected = LibraryFiling.ShowFolder(naming, s, series);
                var legacy = LibraryFiling.LegacyShowFolder(s.Title);
                var found = libraryExists
                    ? new[] { expected, legacy }.Distinct().Where(f => Directory.Exists(Path.Combine(library!, f))).ToList()
                    : [];
                var videos = found.Sum(f => CountVideos(Path.Combine(library!, f)));
                folderInfo = found.Count > 0
                    ? $"folder \"{found[0]}\" ({videos} video file{(videos == 1 ? "" : "s")})"
                    : $"NO FOLDER (would be \"{(s.IsCensored ? "…" : expected)}\")";
            }
            catch (Exception ex) { folderInfo = "folder check failed: " + ex.Message; }
            L($"- {title} · MAL {s.MalId} · S{s.SeasonNumber} · watched/last ep {s.LastEpisodeNumber} · {s.AiringStatus ?? "?"} · {s.MonitoringState}{(s.TidyExcluded ? " · don't tidy" : "")} · {folderInfo}");
        }
        if (libraryExists)
        {
            var top = Directory.GetDirectories(library!).Select(Path.GetFileName).OrderBy(n => n).ToList();
            L($"Top-level folders in the library ({top.Count}): {string.Join(" | ", top.Take(80))}{(top.Count > 80 ? " …" : "")}");
            foreach (var special in new[] { "_Standalone", "_Unmatched" })
            {
                var dir = Path.Combine(library!, special);
                if (Directory.Exists(dir)) L($"{special}: {CountVideos(dir)} video file(s)");
            }
        }

        H("Recent downloads (newest first)");
        var jobs = await db.DownloadJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(40).ToListAsync(ct);
        if (jobs.Count == 0) L("(none)");
        foreach (var j in jobs)
        {
            var where = j.FinalFilePath is { Length: > 0 } p ? $" → {p} ({(File.Exists(p) ? "file there" : "FILE NOT THERE")})" : "";
            L($"- {j.CreatedAt.ToLocalTime():MM-dd HH:mm} · {j.Status} · {j.Backend} · series {j.SeriesId?.ToString() ?? "-"} ep {j.EpisodeNumber} · {Privacy.Name(j.RssTitle, j.TorrentHash)}{where}");
        }
        L($"Unmatched files waiting: {await db.UnmatchedFiles.CountAsync(ct)}");

        H("Feeds");
        foreach (var f in await db.RssFeeds.AsNoTracking().OrderBy(f => f.Id).ToListAsync(ct))
            L($"- {(f.IsEnabled ? "on " : "off")} {f.FeedType} {Redact(f.Url)} quality={f.PreferredQuality ?? "-"}");

        if (sourcesCheck != null)
        {
            H("Sources check");
            foreach (var i in sourcesCheck) L($"[{i.State}] {i.Title}: {i.Detail}");
        }

        H("Settings (secrets removed)");
        foreach (var c in config.OrderBy(c => c.Key))
            L($"{c.Key} = {(IsSecretKey(c.Key) ? "(hidden)" : Redact(Shorten(c.Value)))}");

        H("Warnings and errors from the logs (most recent last)");
        foreach (var line in RecentProblems(250)) L(line);

        return sb.ToString();
    }

    private static int CountVideos(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Count(f => VideoExtensions.Contains(Path.GetExtension(f)));
        }
        catch { return -1; }
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov" };

    private static string WriteTest(string dir)
    {
        var probe = Path.Combine(dir, $".sentrychan-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return "can create files and folders";
        }
        catch (Exception ex) { return "CAN'T WRITE: " + ex.Message; }
    }

    private static string FreeSpace(string dir)
    {
        try { return $"{new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace / 1_073_741_824.0:0.#} GB free"; }
        catch { return "free space unknown"; }
    }

    private static bool IsSecretKey(string key) => SecretKey().IsMatch(key);

    /// <summary>Query strings can carry personal feed keys; paths and hosts are kept.</summary>
    private static string Redact(string value) => QueryString().Replace(value, "?…");

    private static string Shorten(string value) => value.Length > 300 ? value[..300] + "…" : value;

    /// <summary>The last warning and error lines of recent logs, with their exception lines.</summary>
    public static IReadOnlyList<string> RecentProblems(int max)
    {
        if (!Directory.Exists(AppPaths.Logs)) return [];
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(AppPaths.Logs, "sentrychan-*.log").OrderBy(f => f).TakeLast(3))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var keep = false;
                while (reader.ReadLine() is { } line)
                {
                    if (LogEntryStart().IsMatch(line)) keep = line.Contains("[WRN]") || line.Contains("[ERR]") || line.Contains("[FTL]");
                    if (keep) lines.Add(line);
                }
            }
            catch { /* unreadable log: skip */ }
        }
        return lines.TakeLast(max).ToList();
    }

    [GeneratedRegex(@"password|passwd|token|secret|apikey|api_key|hash|session|pin|cookie|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"\?[^\s""']*")]
    private static partial Regex QueryString();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} ")]
    private static partial Regex LogEntryStart();
}
