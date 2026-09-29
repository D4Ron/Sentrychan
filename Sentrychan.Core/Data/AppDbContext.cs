using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Series> Series => Set<Series>();
    public DbSet<Manga> Manga => Set<Manga>();
    public DbSet<MangaChapter> MangaChapters => Set<MangaChapter>();
    public DbSet<RssFeed> RssFeeds => Set<RssFeed>();
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<ApiCache> ApiCaches => Set<ApiCache>();
    public DbSet<WatchPartySession> WatchPartySessions => Set<WatchPartySession>();
    public DbSet<WatchPartyParticipant> WatchPartyParticipants => Set<WatchPartyParticipant>();
    public DbSet<WatchPartyMessage> WatchPartyMessages => Set<WatchPartyMessage>();
    public DbSet<WatchHistoryEntry> WatchHistory => Set<WatchHistoryEntry>();
    public DbSet<TitleAlias> TitleAliases => Set<TitleAlias>();
    public DbSet<UnmatchedFile> UnmatchedFiles => Set<UnmatchedFile>();
    public DbSet<SkippedDownload> SkippedDownloads => Set<SkippedDownload>();
    public DbSet<LibraryFileOrigin> LibraryFileOrigins => Set<LibraryFileOrigin>();
    public DbSet<MangaCategory> MangaCategories => Set<MangaCategory>();
    public DbSet<MangaCategoryLink> MangaCategoryLinks => Set<MangaCategoryLink>();
    public DbSet<MangaChapterBookmark> MangaChapterBookmarks => Set<MangaChapterBookmark>();
    public DbSet<MangaReadingHistory> MangaReadingHistory => Set<MangaReadingHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Series
        modelBuilder.Entity<Series>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.MalId).IsUnique();
            e.Property(s => s.Title).IsRequired();
        });

        modelBuilder.Entity<Series>().Property(s => s.AutoDownload).HasDefaultValue(false);
        modelBuilder.Entity<Series>().Ignore(s => s.IsMovie);

        // LibraryFileOrigin — one row per renamed library file. NOCASE: Windows paths are
        // case-insensitive, and a lookup must not miss because Explorer spelled it differently.
        modelBuilder.Entity<LibraryFileOrigin>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Path).IsRequired().UseCollation("NOCASE");
            e.HasIndex(o => o.Path).IsUnique();
        });

        // Manga
        modelBuilder.Entity<Manga>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.Source, m.SourceId }).IsUnique();
            e.Property(m => m.Title).IsRequired();
            e.HasMany(m => m.Chapters)
             .WithOne(c => c.Manga)
             .HasForeignKey(c => c.MangaId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // MangaChapter
        modelBuilder.Entity<MangaChapter>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.MangaId, c.SourceId }).IsUnique();
            e.HasIndex(c => c.FetchedAt);
        });

        // Manga library: categories, bookmarks, reading history. Everything cascades with the
        // manga or chapter it hangs off, so removing a title leaves nothing behind.
        modelBuilder.Entity<MangaCategory>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired();
        });
        modelBuilder.Entity<MangaCategoryLink>(e =>
        {
            e.HasKey(l => new { l.MangaId, l.CategoryId });
            e.HasOne(l => l.Manga).WithMany().HasForeignKey(l => l.MangaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Category).WithMany().HasForeignKey(l => l.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MangaChapterBookmark>(e =>
        {
            e.HasKey(b => b.ChapterId);
            e.HasOne(b => b.Chapter).WithMany().HasForeignKey(b => b.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MangaReadingHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.HasIndex(h => h.ChapterId).IsUnique();
            e.HasIndex(h => h.ReadAt);
            e.HasOne(h => h.Manga).WithMany().HasForeignKey(h => h.MangaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(h => h.Chapter).WithMany().HasForeignKey(h => h.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        // SkippedDownload
        modelBuilder.Entity<SkippedDownload>(e =>
        {
            e.HasKey(sd => sd.Id);
            e.HasIndex(sd => new { sd.SeriesId, sd.EpisodeNumber });
            e.HasOne(sd => sd.Series)
             .WithMany()
             .HasForeignKey(sd => sd.SeriesId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // RssFeed
        modelBuilder.Entity<RssFeed>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.Url).IsUnique();
            e.Property(f => f.FeedType)
             .HasConversion<string>();
        });

        // DownloadJob
        modelBuilder.Entity<DownloadJob>(e =>
        {
            e.HasKey(j => j.Id);
            e.Property(j => j.Status)
             .HasConversion<string>();
            e.Property(j => j.Backend)
             .HasConversion<string>();
            e.HasOne(j => j.Series)
             .WithMany(s => s.DownloadJobs)
             .HasForeignKey(j => j.SeriesId)
             .IsRequired(false)              // standalone downloads have no series
             .OnDelete(DeleteBehavior.Cascade);
        });

        // AppConfig — composite key is just the Key string
        modelBuilder.Entity<AppConfig>(e =>
        {
            e.HasKey(c => c.Key);
        });
        // ApiCache — composite key is just the CacheKey string, with an index on ExpiresAt for cleanup queries
        modelBuilder.Entity<ApiCache>(e =>
        {
            e.HasKey(c => c.CacheKey);
            e.HasIndex(c => c.ExpiresAt);
        });

        // WatchPartySession
        modelBuilder.Entity<WatchPartySession>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).IsRequired();
            e.HasOne(s => s.HostedSeries)
             .WithMany()
             .HasForeignKey(s => s.HostedSeriesId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // WatchPartyParticipant
        modelBuilder.Entity<WatchPartyParticipant>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasOne(p => p.Session)
             .WithMany()
             .HasForeignKey(p => p.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // WatchPartyMessage
        modelBuilder.Entity<WatchPartyMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasOne(m => m.Session)
             .WithMany()
             .HasForeignKey(m => m.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // WatchHistoryEntry
        modelBuilder.Entity<WatchHistoryEntry>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasOne(w => w.Series)
             .WithMany()
             .HasForeignKey(w => w.SeriesId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // TitleAlias
        modelBuilder.Entity<TitleAlias>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasOne(a => a.Series)
             .WithMany()
             .HasForeignKey(a => a.SeriesId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // UnmatchedFile
        modelBuilder.Entity<UnmatchedFile>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.FilePath);
            e.HasIndex(u => u.IsResolved);
        });
    }
}
