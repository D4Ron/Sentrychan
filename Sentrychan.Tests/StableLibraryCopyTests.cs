using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public sealed class StableLibraryCopyTests : IDisposable
{
    private readonly string _root   = Directory.CreateTempSubdirectory("sentrychan-copy-").FullName;
    private string Stable  => Path.Combine(_root, "Sentrychan");
    private string Preview => Path.Combine(_root, "Sentrychan Preview");

    public StableLibraryCopyTests()
    {
        Directory.CreateDirectory(Stable);
        Directory.CreateDirectory(Preview);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static AppDbContext Open(string dir) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "sentrychan.db"),
            Pooling = false, // let files be moved and deleted as soon as a context is disposed
        }.ToString())
        .Options);

    /// <summary>A stable data folder as a real install leaves it: migrated database plus side files.</summary>
    private void SeedStable()
    {
        using (var db = Open(Stable))
        {
            db.Database.Migrate();
            db.Series.Add(new Series { MalId = 1, Title = "Copied Show", PosterPath = Path.Combine(Stable, "ImageCache", "1.jpg") });
            db.Series.Add(new Series { MalId = 2, Title = "Own Poster", PosterPath = Path.Combine(_root, "elsewhere", "2.jpg") });
            db.Manga.Add(new Manga { Source = "Local", SourceId = "m1", Title = "Copied Manga", CoverPath = Path.Combine(Stable, "Covers", "m1.png") });
            db.DownloadJobs.AddRange(
                new DownloadJob { DownloadLink = "magnet:?xt=urn:btih:done", Status = JobStatus.Completed },
                new DownloadJob { DownloadLink = "magnet:?xt=urn:btih:running", Status = JobStatus.Downloading },
                new DownloadJob { DownloadLink = "magnet:?xt=urn:btih:waiting", Status = JobStatus.Pending });
            db.AppConfigs.AddRange(
                new AppConfig { Key = "VaultRoot", Value = Path.Combine(_root, "Library", ".cache") },
                new AppConfig { Key = "LibraryPath", Value = Path.Combine(_root, "Library") });
            db.SaveChanges();
        }

        Write(Stable, "ImageCache/1.jpg", "poster");
        Write(Stable, "Covers/m1.png", "cover");
        Write(Stable, "sources/Example.Sources.dll", "pack");
        Write(Stable, "sources/nested/readme.txt", "nested");
        Write(Stable, "playback.json", "{}");
        Write(Stable, "vault.key", "secret key");
        Write(Stable, "Auth/session.dat", "session");
        Write(Stable, "logs/sentrychan-20260929.log", "log");
    }

    private static void Write(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private void StageAndApply()
    {
        StableLibraryCopy.Stage(Stable, Preview);
        Assert.True(StableLibraryCopy.IsStaged(Preview));
        Assert.True(StableLibraryCopy.ApplyStaged(Preview));
        Assert.False(StableLibraryCopy.IsStaged(Preview));
    }

    [Fact]
    public void Stable_data_is_never_written()
    {
        SeedStable();
        var before = Snapshot(Stable);

        StageAndApply();

        Assert.Equal(before, Snapshot(Stable));
    }

    [Fact]
    public void Library_and_settings_arrive_and_the_database_still_migrates()
    {
        SeedStable();
        StageAndApply();

        using var db = Open(Preview);
        db.Database.Migrate();
        Assert.Equal(["Copied Show", "Own Poster"], db.Series.OrderBy(s => s.Title).Select(s => s.Title).ToArray());
        Assert.Equal("Copied Manga", db.Manga.Single().Title);
        Assert.Equal(Path.Combine(_root, "Library"), db.AppConfigs.Single(c => c.Key == "LibraryPath").Value);

        Assert.Equal("pack", File.ReadAllText(Path.Combine(Preview, "sources", "Example.Sources.dll")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(Preview, "sources", "nested", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(Preview, "playback.json")));
    }

    [Fact]
    public void Vault_key_sign_in_and_logs_stay_behind_and_the_vault_root_is_reset()
    {
        SeedStable();
        StageAndApply();

        Assert.False(File.Exists(Path.Combine(Preview, "vault.key")));
        Assert.False(Directory.Exists(Path.Combine(Preview, "Auth")));
        Assert.False(Directory.Exists(Path.Combine(Preview, "logs")));

        using var db = Open(Preview);
        Assert.False(db.AppConfigs.Any(c => c.Key == "VaultRoot"));
    }

    [Fact]
    public void Poster_and_cover_paths_follow_the_copied_files()
    {
        SeedStable();
        StageAndApply();

        using var db = Open(Preview);
        var poster = db.Series.Single(s => s.Title == "Copied Show").PosterPath;
        Assert.Equal(Path.Combine(Preview, "ImageCache", "1.jpg"), poster);
        Assert.Equal("poster", File.ReadAllText(poster));

        var cover = db.Manga.Single().CoverPath;
        Assert.Equal(Path.Combine(Preview, "Covers", "m1.png"), cover);
        Assert.True(File.Exists(cover));

        // Only paths inside stable's data folder move.
        Assert.Equal(Path.Combine(_root, "elsewhere", "2.jpg"),
            db.Series.Single(s => s.Title == "Own Poster").PosterPath);
    }

    [Fact]
    public void Downloads_in_flight_stay_with_stable()
    {
        SeedStable();
        StageAndApply();

        using var db = Open(Preview);
        Assert.Equal(JobStatus.Completed, db.DownloadJobs.Single().Status);
    }

    [Fact]
    public void The_copy_is_marked_so_the_offer_is_not_repeated()
    {
        SeedStable();
        StageAndApply();

        using var db = Open(Preview);
        Assert.Equal("true", db.AppConfigs.Single(c => c.Key == StableLibraryCopy.OfferedKey).Value);
    }

    [Fact]
    public void What_the_copy_replaces_is_kept_in_a_backup()
    {
        SeedStable();
        using (var fresh = Open(Preview))
        {
            fresh.Database.Migrate();
            fresh.AppConfigs.Add(new AppConfig { Key = "Marker", Value = "preview's own" });
            fresh.SaveChanges();
        }
        Write(Preview, "sources/Seeded.Sources.dll", "seeded");

        StageAndApply();

        var backup = Assert.Single(Directory.GetDirectories(Preview, StableLibraryCopy.BackupPrefix + "*"));
        Assert.True(File.Exists(Path.Combine(backup, "sentrychan.db")));
        Assert.True(File.Exists(Path.Combine(backup, "sources", "Seeded.Sources.dll")));
        Assert.False(File.Exists(Path.Combine(Preview, "sources", "Seeded.Sources.dll")));

        using var db = Open(Preview);
        Assert.False(db.AppConfigs.Any(c => c.Key == "Marker"));
    }

    [Fact]
    public void Committed_data_still_in_the_write_ahead_log_comes_along()
    {
        SeedStable();
        // Hold a connection open so the last write stays in sentrychan.db-wal, as after a crash.
        using var holder = new SqliteConnection($"Data Source={Path.Combine(Stable, "sentrychan.db")};Pooling=False");
        holder.Open();
        using (var cmd = holder.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_autocheckpoint = 0; INSERT INTO AppConfigs (Key, Value) VALUES ('OnlyInWal', 'yes');";
            cmd.ExecuteNonQuery();
        }
        Assert.True(File.Exists(Path.Combine(Stable, "sentrychan.db-wal")));

        StageAndApply();

        Assert.False(File.Exists(Path.Combine(Preview, "sentrychan.db-wal")));
        using var db = Open(Preview);
        Assert.Equal("yes", db.AppConfigs.Single(c => c.Key == "OnlyInWal").Value);
    }

    [Fact]
    public void An_interrupted_earlier_copy_is_discarded_not_applied()
    {
        SeedStable();
        Write(Preview, "import-from-stable.tmp/sentrychan.db", "half a copy");
        Assert.False(StableLibraryCopy.IsStaged(Preview));
        Assert.False(StableLibraryCopy.ApplyStaged(Preview));

        StageAndApply();
        Assert.False(Directory.Exists(Path.Combine(Preview, "import-from-stable.tmp")));
    }

    [Fact]
    public void Nothing_to_offer_without_a_stable_library()
    {
        Assert.False(StableLibraryCopy.StableDataExists(Stable));
        Assert.Throws<FileNotFoundException>(() => StableLibraryCopy.Stage(Stable, Preview));
        Assert.False(StableLibraryCopy.IsStaged(Preview));
    }
}
