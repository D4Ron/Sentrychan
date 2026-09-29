using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public sealed class MangaLibraryServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-mlib-").FullName;
    private readonly Factory _factory;
    private readonly MangaLibraryService _lib;
    private readonly MangaService _manga;

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public MangaLibraryServiceTests()
    {
        _factory = new Factory(Path.Combine(_dir, "t.db"));
        using (var db = _factory.CreateDbContext()) db.Database.Migrate();
        _lib = new MangaLibraryService(_factory, NullLogger<MangaLibraryService>.Instance);
        _manga = new MangaService(_factory, NullLogger<MangaService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MangaChapterInfo Info(double n, DateTime? published = null) =>
        new($"c{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture), n, null, null, "en", null, 10, published);

    private async Task<Manga> AddAsync(string title, int chapters, bool novel = false, bool adult = false, string source = "Src")
    {
        var m = await _manga.AddAsync(new Manga { Source = source, SourceId = title, Title = title, IsNovel = novel, IsCensored = adult });
        await _manga.SyncChaptersAsync(m.Id, Enumerable.Range(1, chapters).Select(i => Info(i)));
        return m;
    }

    private async Task<List<MangaChapter>> ChaptersAsync(int mangaId) =>
        (await _manga.GetByIdAsync(mangaId))!.Chapters.OrderBy(c => c.ChapterSort).ToList();

    [Fact]
    public async Task Categories_are_created_renamed_reordered_and_deleted()
    {
        var a = await _lib.CreateCategoryAsync("Reading");
        var b = await _lib.CreateCategoryAsync("Weekly");
        var c = await _lib.CreateCategoryAsync("Done");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _lib.CreateCategoryAsync("Weekly"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _lib.CreateCategoryAsync("  "));

        await _lib.RenameCategoryAsync(b.Id, "Every week");
        await _lib.ReorderCategoriesAsync([c.Id, a.Id, b.Id]);
        Assert.Equal(["Done", "Reading", "Every week"], (await _lib.GetCategoriesAsync()).Select(x => x.Name));

        await _lib.DeleteCategoryAsync(a.Id);
        Assert.Equal(["Done", "Every week"], (await _lib.GetCategoriesAsync()).Select(x => x.Name));
    }

    [Fact]
    public async Task Titles_are_assigned_to_categories_and_uncategorised_ones_show_under_default()
    {
        var reading = await _lib.CreateCategoryAsync("Reading");
        var m1 = await AddAsync("One", 3);
        var m2 = await AddAsync("Two", 3);
        await _lib.SetCategoriesAsync([m1.Id], [reading.Id]);

        var entries = await _lib.GetEntriesAsync(false, false);
        Assert.Equal(["One"], LibraryQuery.Apply(entries, new(), LibrarySort.Title, true, categoryId: reading.Id).Select(e => e.Manga.Title));
        Assert.Equal(["Two"], LibraryQuery.Apply(entries, new(), LibrarySort.Title, true, categoryId: LibraryQuery.DefaultCategory).Select(e => e.Manga.Title));

        // Deleting the category drops the link, not the title.
        await _lib.DeleteCategoryAsync(reading.Id);
        entries = await _lib.GetEntriesAsync(false, false);
        Assert.All(entries, e => Assert.Empty(e.CategoryIds));
    }

    [Fact]
    public async Task Entries_count_unread_and_downloaded_and_respect_kind_and_adult()
    {
        var m = await AddAsync("Counted", 5);
        await AddAsync("A Novel", 2, novel: true);
        await AddAsync("Hidden", 2, adult: true);
        await _manga.UpdateProgressAsync(m.Id, 2); // progress alone counts chapters 1–2 read
        await using (var db = _factory.CreateDbContext())
        {
            db.MangaChapters.First(c => c.MangaId == m.Id && c.ChapterSort == 5).DownloadedPath = "/x";
            db.SaveChanges();
        }

        var entries = await _lib.GetEntriesAsync(novels: false, includeAdult: false);
        var e = Assert.Single(entries);
        Assert.Equal((5, 3, 1), (e.ChapterCount, e.UnreadCount, e.DownloadedCount));
        Assert.True(e.IsStarted);
        Assert.False(e.IsCompleted);
        Assert.Equal(2, (await _lib.GetEntriesAsync(false, includeAdult: true)).Count);
    }

    [Fact]
    public void Filters_are_include_exclude_or_ignore_and_sorts_tie_break_by_title()
    {
        var m = (string t, int unread, int dl, DateTime added) => new LibraryEntry(
            new Manga { Title = t, AddedAt = added }, 10, unread, dl, null, null, []);
        var entries = new[]
        {
            m("B", 0, 0, new DateTime(2026, 1, 2)),
            m("A", 4, 1, new DateTime(2026, 1, 3)),
            m("C", 4, 0, new DateTime(2026, 1, 1)),
        };

        Assert.Equal(["A", "C"], LibraryQuery.Apply(entries, new(Unread: TriState.Include), LibrarySort.Title, true).Select(e => e.Manga.Title));
        Assert.Equal(["B", "C"], LibraryQuery.Apply(entries, new(Downloaded: TriState.Exclude), LibrarySort.Title, true).Select(e => e.Manga.Title));
        Assert.Equal(["B"], LibraryQuery.Apply(entries, new(Completed: TriState.Include), LibrarySort.Title, true).Select(e => e.Manga.Title));
        Assert.Equal(["A", "C", "B"], LibraryQuery.Apply(entries, new(), LibrarySort.UnreadCount, false).Select(e => e.Manga.Title));
        Assert.Equal(["A", "B", "C"], LibraryQuery.Apply(entries, new(), LibrarySort.DateAdded, false).Select(e => e.Manga.Title));
        Assert.Equal(["C"], LibraryQuery.Apply(entries, new(), LibrarySort.Title, true, search: "c").Select(e => e.Manga.Title));
    }

    [Fact]
    public async Task Only_chapters_found_after_the_first_sync_are_updates()
    {
        var m = await AddAsync("Weekly", 3);
        Assert.Empty(await _lib.GetUpdatesAsync(false, false));

        await _manga.SyncChaptersAsync(m.Id, Enumerable.Range(1, 5).Select(i => Info(i)));
        var updates = await _lib.GetUpdatesAsync(false, false);
        Assert.Equal([4.0, 5.0], updates.Select(u => u.Chapter.ChapterSort!.Value).Order());

        var groups = UpdatesQuery.ByDay(updates);
        Assert.Single(groups);
        Assert.Equal("Today", UpdatesQuery.DayLabel(groups[0].Day, DateOnly.FromDateTime(DateTime.Now)));
    }

    [Fact]
    public async Task History_keeps_the_newest_reading_per_title_and_can_be_removed()
    {
        var m = await AddAsync("Read Me", 3);
        var chapters = await ChaptersAsync(m.Id);
        await _manga.SaveReadingPositionAsync(chapters[0].Id, 5, markRead: true);
        await Task.Delay(20);
        await _manga.SaveReadingPositionAsync(chapters[1].Id, 2, markRead: false);

        var h = Assert.Single(await _lib.GetHistoryAsync(false, false));
        Assert.Equal(chapters[1].Id, h.Chapter.Id);
        Assert.Equal(2, h.LastPage);

        await _lib.RemoveHistoryAsync(m.Id);
        Assert.Empty(await _lib.GetHistoryAsync(false, false));
        Assert.True((await ChaptersAsync(m.Id))[0].IsRead); // read state stays
    }

    [Fact]
    public async Task Marking_unread_lowers_progress_without_unreading_the_rest()
    {
        var m = await AddAsync("Progress", 5);
        await _manga.UpdateProgressAsync(m.Id, 4);
        var chapters = await ChaptersAsync(m.Id);

        await _lib.SetReadAsync(m.Id, [chapters[3].Id], read: false); // chapter 4
        var after = await _manga.GetByIdAsync(m.Id);
        Assert.Equal(3, after!.LastReadChapter);
        Assert.Equal([true, true, true, false, false], after.Chapters.OrderBy(c => c.ChapterSort).Select(c => LibraryQuery.IsRead(after, c)));
    }

    [Fact]
    public async Task Mark_previous_as_read()
    {
        var m = await AddAsync("Previous", 5);
        var chapters = await ChaptersAsync(m.Id);
        await _lib.MarkPreviousReadAsync(chapters[3].Id); // before chapter 4
        var after = await _manga.GetByIdAsync(m.Id);
        Assert.Equal([true, true, true, false, false], after!.Chapters.OrderBy(c => c.ChapterSort).Select(c => c.IsRead));
        Assert.Equal(3, after.LastReadChapter);
    }

    [Fact]
    public async Task Bookmarks_and_chapter_filters()
    {
        var m = await AddAsync("Marks", 4);
        var chapters = await ChaptersAsync(m.Id);
        await _lib.SetBookmarkedAsync([chapters[1].Id, chapters[2].Id], true);
        await _lib.SetBookmarkedAsync([chapters[2].Id], false);
        var marks = await _lib.GetBookmarksAsync(m.Id);
        Assert.Equal([chapters[1].Id], marks);

        var manga = (await _manga.GetByIdAsync(m.Id))!;
        var bookmarked = ChapterQuery.Apply(manga, manga.Chapters, new(Bookmarked: TriState.Include), ChapterSort.Number, false, marks);
        Assert.Equal([2.0], bookmarked.Select(c => c.ChapterSort!.Value));
        var newestFirst = ChapterQuery.Apply(manga, manga.Chapters, new(), ChapterSort.Number, true, marks);
        Assert.Equal([4.0, 3.0, 2.0, 1.0], newestFirst.Select(c => c.ChapterSort!.Value));
        Assert.Equal([4.0, 3.0, 2.0, 1.0], ChapterQuery.Apply(manga, manga.Chapters, new(), ChapterSort.SourceOrder, true, marks).Select(c => c.ChapterSort!.Value));
    }

    [Fact]
    public async Task Download_next_takes_unread_undownloaded_chapters_from_the_earliest()
    {
        var m = await AddAsync("Next", 6);
        await _manga.UpdateProgressAsync(m.Id, 2);
        await using (var db = _factory.CreateDbContext())
        {
            db.MangaChapters.First(c => c.MangaId == m.Id && c.ChapterSort == 3).DownloadedPath = "/x";
            db.SaveChanges();
        }
        var manga = (await _manga.GetByIdAsync(m.Id))!;
        Assert.Equal([4.0, 5.0], ChapterQuery.NextToDownload(manga, manga.Chapters, 2).Select(c => c.ChapterSort!.Value));
        Assert.Equal([4.0, 5.0, 6.0], ChapterQuery.NextToDownload(manga, manga.Chapters, null).Select(c => c.ChapterSort!.Value));
    }

    [Fact]
    public async Task Migration_carries_read_state_bookmarks_history_and_categories()
    {
        var cat = await _lib.CreateCategoryAsync("Keep");
        var m = await AddAsync("Old Home", 4);
        await _lib.SetCategoriesAsync([m.Id], [cat.Id]);
        var chapters = await ChaptersAsync(m.Id);
        await _manga.SaveReadingPositionAsync(chapters[1].Id, 7, markRead: true); // ch 2 read → progress 2
        await _lib.SetBookmarkedAsync([chapters[2].Id], true);                  // ch 3 bookmarked

        var target = new V1Source(browse: false);
        var details = new MangaSearchResult("new-id", "New Home", null, "about", "https://example.test/c.jpg", "ongoing", 2020, 5, [], false);
        var migrated = await _lib.MigrateAsync(m.Id, target, details, Enumerable.Range(1, 5).Select(i => Info(i) with { SourceId = $"n{i}" }).ToList());

        Assert.Equal(("Old Pack Source", "new-id", "New Home"), (migrated.Source, migrated.SourceId, migrated.Title));
        var after = (await _manga.GetByIdAsync(m.Id))!;
        Assert.Equal(5, after.Chapters.Count);
        Assert.All(after.Chapters, c => Assert.StartsWith("n", c.SourceId));
        Assert.Equal([true, true, false, false, false], after.Chapters.OrderBy(c => c.ChapterSort).Select(c => LibraryQuery.IsRead(after, c)));

        var ch3 = after.Chapters.Single(c => c.ChapterSort == 3);
        Assert.Equal([ch3.Id], await _lib.GetBookmarksAsync(m.Id));
        var h = Assert.Single(await _lib.GetHistoryAsync(false, false));
        Assert.Equal((2.0, 7), (h.Chapter.ChapterSort!.Value, h.LastPage));
        Assert.Equal([cat.Id], Assert.Single(await _lib.GetEntriesAsync(false, false)).CategoryIds);
    }

    [Fact]
    public async Task Migrating_onto_a_title_already_in_the_library_is_refused()
    {
        var a = await AddAsync("A", 1, source: "Old Pack Source");
        var b = await AddAsync("B", 1);
        var details = new MangaSearchResult("A", "A", null, null, "", null, null, null, [], false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _lib.MigrateAsync(b.Id, new V1Source(false), details, [Info(1)]));
    }
}
