using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Diagnostics;
using Sentrychan.Core.Models;

namespace Sentrychan.Tests;

[Collection(EnvironmentCollection.Name)] // points SENTRYCHAN_DATA_DIR at a temp folder (logs live there)
public sealed class DiagnosticReportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-report-").FullName;
    private readonly string? _dataDirBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);

    public DiagnosticReportTests() => Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, Path.Combine(_root, "data"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, _dataDirBefore);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    [Fact]
    public async Task The_report_checks_folders_and_series_and_leaves_secrets_out()
    {
        var factory = new Factory(Path.Combine(_root, "t.db"));
        var library = Directory.CreateDirectory(Path.Combine(_root, "Anime")).FullName;
        Directory.CreateDirectory(Path.Combine(library, "Kept Show (2024)", "Season 01"));
        File.WriteAllText(Path.Combine(library, "Kept Show (2024)", "Season 01", "Kept Show S01E01.mkv"), "");
        Directory.CreateDirectory(AppPaths.Logs);
        File.WriteAllText(Path.Combine(AppPaths.Logs, "sentrychan-20261005.log"),
            "2026-10-05 10:00:00.000 +00:00 [INF] fine\n2026-10-05 10:00:01.000 +00:00 [WRN] [Pipeline] can't file into the anime folder\n");

        await using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.AppConfigs.AddRange(
                new AppConfig { Key = "LibraryPath", Value = library },
                new AppConfig { Key = "QBitPassword", Value = "hunter2" },
                new AppConfig { Key = "UiLockHash", Value = "pbkdf2$secret" });
            db.Series.AddRange(
                new Series { Title = "Kept Show", MalId = 1, Year = 2024 },
                new Series { Title = "Deleted Show", MalId = 2, Year = 2025 },
                new Series { Title = "Hidden Show", MalId = 3, IsCensored = true });
            db.RssFeeds.Add(new RssFeed { Url = "https://example.test/rss?passkey=abc123" });
            await db.SaveChangesAsync();
        }

        var text = await DiagnosticReport.DescribeAsync(factory, "Screen 1470x956", null, "testing");

        Assert.Contains("can create files and folders", text);
        Assert.Contains("Kept Show · MAL 1", text);
        Assert.Contains("folder \"Kept Show (2024)\" (1 video file)", text);
        Assert.Contains("NO FOLDER (would be \"Deleted Show (2025)\")", text);
        Assert.DoesNotContain("Hidden Show", text);
        Assert.Contains("[WRN] [Pipeline] can't file into the anime folder", text);
        Assert.Contains("Screen 1470x956", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("pbkdf2", text);
        Assert.DoesNotContain("abc123", text);

        var zip = await DiagnosticReport.WriteAsync(factory, null, [], null, null);
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, e => e.FullName == "report.txt");
            Assert.Contains(archive.Entries, e => e.FullName == "logs/sentrychan-20261005.log");
        }
        finally { File.Delete(zip); }
    }
}
