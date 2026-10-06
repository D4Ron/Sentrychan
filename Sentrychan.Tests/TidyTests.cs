using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Library;
using Sentrychan.Core.Models;

namespace Sentrychan.Tests;

public sealed class TidyPlannerTests : IDisposable
{
    private readonly string _lib = Directory.CreateTempSubdirectory("sentrychan-lib-").FullName;
    public void Dispose() => Directory.Delete(_lib, recursive: true);

    private string Touch(string relative, string content = "video")
    {
        var path = Path.Combine(_lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string L(string relative) => Path.Combine(_lib, relative.Replace('/', Path.DirectorySeparatorChar));

    private static readonly TidySeries Frieren = new(1, 101, "Frieren", 2023, "TV", 1, 28);

    private TidyPlan Plan(IReadOnlyList<TidySeries> series, Func<string, string?>? skip = null) =>
        new TidyPlanner(NamingTemplate.Default, skip).Build(_lib, series);

    [Fact]
    public void Legacy_layout_moves_to_jellyfin_names_using_the_series_record()
    {
        var src = Touch("Frieren/Season 1/[Grp] Sousou no Frieren - 05 (1080p) [ABCD1234].mkv");

        var item = Assert.Single(Plan([Frieren]).Items);
        Assert.Equal(TidyItemStatus.Ready, item.Status);
        Assert.Equal(src, item.Source);
        Assert.Equal(L("Frieren (2023)/Season 01/Frieren S01E05.mkv"), item.Destination);
        Assert.Equal(1, item.SeriesId);
        Assert.True(item.Selected);
    }

    [Fact]
    public void Untracked_folders_are_named_from_a_cleaned_folder_name()
    {
        Touch("[Grp] Other Show (2019) [BD 1080p]/[Grp] Other Show - 03 [BD 1080p].mkv");
        var item = Assert.Single(Plan([]).Items);
        Assert.Equal(L("Other Show (2019)/Season 01/Other Show S01E03.mkv"), item.Destination);
    }

    [Fact]
    public void Folders_the_user_left_alone_are_skipped_whole_and_listed()
    {
        Touch("One Pace/[One Pace][1000] Wano 55 [1080p][En Sub][AB264EB4].mp4");
        Touch("[Grp] Other Show (2019) [BD 1080p]/[Grp] Other Show - 03 [BD 1080p].mkv");

        var plan = new TidyPlanner(NamingTemplate.Default, leaveAlone: ["one pace"]).Build(_lib, []);

        var item = Assert.Single(plan.Items);
        Assert.StartsWith(L("[Grp] Other Show"), item.Source);
        Assert.Equal(["One Pace"], plan.LeftAlone);
        Assert.Equal(0, plan.AlreadyTidy);
    }

    [Fact]
    public void Tidy_files_are_left_alone_and_counted()
    {
        Touch("Frieren (2023)/Season 01/Frieren S01E05.mkv");
        var plan = Plan([Frieren]);
        Assert.Empty(plan.Items);
        Assert.Equal(1, plan.AlreadyTidy);
    }

    [Fact]
    public void App_folders_and_hidden_folders_are_never_touched()
    {
        Touch("_Unmatched/[Grp] Thing - 01.mkv");
        Touch("_Standalone/Thing/[Grp] Thing - 01.mkv");
        Touch(".cache/ab/abcdef");
        Touch(".cache-preview/Thing - 01.mkv");
        Assert.Empty(Plan([]).Items);
    }

    [Fact]
    public void Seasons_share_one_show_folder_and_continued_counts_are_renumbered()
    {
        var s1 = new TidySeries(1, 101, "Show", 2020, "TV", 1, 12);
        var s2 = new TidySeries(2, 102, "Show Season 2", 2022, "TV", 1, 12);
        Touch("Show/Season 1/[Grp] Show - 12.mkv");
        Touch("Show/Season 2/[Grp] Show S2 - 13.mkv");

        var items = Plan([s1, s2]).Items.OrderBy(i => i.Destination).ToList();
        Assert.Equal(L("Show (2020)/Season 01/Show S01E12.mkv"), items[0].Destination);
        Assert.Equal(L("Show (2020)/Season 02/Show S02E01.mkv"), items[1].Destination);
        Assert.Equal(2, items[1].SeriesId);
        Assert.Contains("across seasons", items[1].Note);
    }

    [Fact]
    public void Ambiguous_numbers_are_reported_and_left_alone()
    {
        var s1 = new TidySeries(1, 101, "Show", 2020, "TV", 1, 12);
        var s2 = new TidySeries(2, 102, "Show Season 2", 2022, "TV", 1, null); // still airing
        Touch("Show/Season 2/[Grp] Show S2 - 14.mkv");

        var item = Assert.Single(Plan([s1, s2]).Items);
        Assert.Equal(TidyItemStatus.Unsure, item.Status);
        Assert.Null(item.Destination);
        Assert.False(item.Selected);
    }

    [Fact]
    public void The_users_numbering_places_what_the_season_lengths_cannot()
    {
        var s1 = new TidySeries(1, 101, "Show", 2020, "TV", 1, null);
        var s2 = new TidySeries(2, 102, "Show Season 2", 2022, "TV", 1, null) { NumberingOffset = 12 };
        Touch("Show/Season 2/[Grp] Show S2 - 14.mkv");
        Touch("Show/Season 2/[Grp] Show S2 - 03.mkv");

        var items = Plan([s1, s2]).Items.OrderBy(i => i.Source).ToList();
        Assert.Equal(TidyItemStatus.Unsure, items[0].Status); // "03": not a straight-through number, so the usual rules — still unsure
        Assert.Equal(L("Show (2020)/Season 02/Show S02E02.mkv"), items[1].Destination);
    }

    [Fact]
    public void Files_without_an_episode_number_and_batches_are_unsure()
    {
        Touch("Frieren/Season 1/Frieren Opening.mkv");
        Touch("Frieren/Season 1/[Grp] Frieren - 01-04 [Batch].mkv");
        Touch("Frieren/Extras/NCOP 01.mkv");
        Assert.All(Plan([Frieren]).Items, i => Assert.Equal(TidyItemStatus.Unsure, i.Status));
    }

    [Fact]
    public void Specials_go_to_season_00()
    {
        Touch("Frieren/Specials/[Grp] Frieren - SP2 [1080p].mkv");
        var item = Assert.Single(Plan([Frieren]).Items);
        Assert.Equal(L("Frieren (2023)/Season 00/Frieren S00E02.mkv"), item.Destination);
    }

    [Fact]
    public void A_movie_series_is_named_by_title_and_year()
    {
        var movie = new TidySeries(5, 105, "Show Movie", 2021, "Movie", 1, 1);
        Touch("Show Movie/[Grp] Show Movie [BD 1080p].mkv");
        var item = Assert.Single(Plan([movie]).Items);
        Assert.Equal(L("Show Movie (2021)/Show Movie (2021).mkv"), item.Destination);
    }

    [Fact]
    public void Sidecars_follow_their_video_and_keep_language_suffixes()
    {
        Touch("Frieren/Season 1/Frieren - 01.mkv");
        Touch("Frieren/Season 1/Frieren - 01.en.ass");
        Touch("Frieren/Season 1/Frieren - 01.pt-BR.forced.srt");
        Touch("Frieren/Season 1/Frieren - 01.nfo");
        Touch("Frieren/Season 1/Frieren - 01-thumb.jpg");
        Touch("Frieren/Season 1/Frieren - 01v2.mkv.part"); // not ours

        var video = Plan([Frieren]).Items.Single(i => i.Source.EndsWith("01.mkv"));
        var targets = video.Sidecars.Select(s => Path.GetFileName(s.To)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["Frieren S01E01-thumb.jpg", "Frieren S01E01.en.ass", "Frieren S01E01.nfo", "Frieren S01E01.pt-BR.forced.srt"],
            targets);
    }

    [Fact]
    public void Collisions_with_existing_files_are_reported_never_planned()
    {
        Touch("Frieren/Season 1/[Grp] Frieren - 05.mkv");
        Touch("Frieren (2023)/Season 01/Frieren S01E05.mkv", "an existing copy");

        var item = Assert.Single(Plan([Frieren]).Items);
        Assert.Equal(TidyItemStatus.Collision, item.Status);
        Assert.False(item.Selected);
        Assert.Contains("already exists", item.Note);
    }

    [Fact]
    public void Two_files_wanting_one_name_collide()
    {
        Touch("Frieren/Season 1/[GrpA] Frieren - 05.mkv");
        Touch("Frieren/Season 1/[GrpB] Frieren - 05.mkv");

        var items = Plan([Frieren]).Items;
        Assert.Equal(1, items.Count(i => i.Status == TidyItemStatus.Ready));
        Assert.Equal(1, items.Count(i => i.Status == TidyItemStatus.Collision));
    }

    [Fact]
    public void Files_in_use_are_skipped_with_the_reason()
    {
        var busy = Touch("Frieren/Season 1/Frieren - 02.mkv");
        var item = Assert.Single(Plan([Frieren], p => p == busy ? "a torrent is still downloading it" : null).Items);
        Assert.Equal(TidyItemStatus.Skipped, item.Status);
        Assert.Equal("a torrent is still downloading it", item.Note);
    }

    [Fact]
    public void Dont_tidy_and_keep_file_names_are_honoured()
    {
        Touch("Frieren/Season 1/[Grp] Frieren - 05.mkv");
        var excluded = Plan([Frieren with { Excluded = true }]);
        Assert.Empty(excluded.Items);
        Assert.Single(excluded.Notes);

        var kept = Assert.Single(Plan([Frieren with { KeepFileNames = true }]).Items);
        Assert.Equal(L("Frieren (2023)/Season 01/[Grp] Frieren - 05.mkv"), kept.Destination);
    }
}

public sealed class TidyExecutorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-tidy-").FullName;
    private string Lib => Path.Combine(_root, "Library");
    private string Journals => Path.Combine(_root, "journals");

    public TidyExecutorTests() => Directory.CreateDirectory(Lib);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative, string content)
    {
        var path = Path.Combine(Lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private Dictionary<string, string> Tree() =>
        Directory.EnumerateFiles(Lib, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => Path.GetRelativePath(Lib, f), File.ReadAllText);

    private static readonly TidySeries Frieren = new(1, 101, "Frieren", 2023, "TV", 1, 28);

    [Fact]
    public void Apply_then_undo_restores_the_library_exactly()
    {
        Touch("Frieren/Season 1/[Grp] Frieren - 01.mkv", "e1");
        Touch("Frieren/Season 1/[Grp] Frieren - 01.en.ass", "subs");
        Touch("Frieren/Season 1/[Grp] Frieren - 02.mkv", "e2");
        Touch("Frieren/notes.txt", "kept");
        var before = Tree();
        var dirsBefore = Directory.EnumerateDirectories(Lib, "*", SearchOption.AllDirectories).Order().ToList();

        var plan = new TidyPlanner(NamingTemplate.Default).Build(Lib, [Frieren]);
        var applied = TidyExecutor.Apply(plan, Journals);

        Assert.Empty(applied.Failed);
        Assert.Equal(3, applied.Moved.Count);
        Assert.Equal("e1", File.ReadAllText(Path.Combine(Lib, "Frieren (2023)", "Season 01", "Frieren S01E01.mkv")));
        Assert.Equal("subs", File.ReadAllText(Path.Combine(Lib, "Frieren (2023)", "Season 01", "Frieren S01E01.en.ass")));
        // The emptied season folder goes; the show folder stays because notes.txt is still in it.
        Assert.False(Directory.Exists(Path.Combine(Lib, "Frieren", "Season 1")));
        Assert.True(File.Exists(Path.Combine(Lib, "Frieren", "notes.txt")));

        Assert.Equal(applied.JournalPath, TidyExecutor.LatestUndoable(Journals));
        var undone = TidyExecutor.Undo(applied.JournalPath);

        Assert.Empty(undone.Failed);
        Assert.Equal(before, Tree());
        Assert.Equal(dirsBefore, Directory.EnumerateDirectories(Lib, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.Null(TidyExecutor.LatestUndoable(Journals));
    }

    [Fact]
    public void Unticked_items_stay_put()
    {
        var keep = Touch("Frieren/Season 1/Frieren - 01.mkv", "e1");
        Touch("Frieren/Season 1/Frieren - 02.mkv", "e2");
        var plan = new TidyPlanner(NamingTemplate.Default).Build(Lib, [Frieren]);
        plan.Items.Single(i => i.Source == keep).Selected = false;

        var result = TidyExecutor.Apply(plan, Journals);
        Assert.Single(result.Moved);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void A_file_that_appears_at_the_destination_after_planning_is_not_overwritten()
    {
        Touch("Frieren/Season 1/Frieren - 01.mkv", "ours");
        var plan = new TidyPlanner(NamingTemplate.Default).Build(Lib, [Frieren]);
        var intruder = Touch("Frieren (2023)/Season 01/Frieren S01E01.mkv", "theirs");

        var result = TidyExecutor.Apply(plan, Journals);
        Assert.Single(result.Failed);
        Assert.Equal("theirs", File.ReadAllText(intruder));
    }

    [Fact]
    public void A_file_busy_at_apply_time_is_skipped()
    {
        var src = Touch("Frieren/Season 1/Frieren - 01.mkv", "e1");
        var plan = new TidyPlanner(NamingTemplate.Default).Build(Lib, [Frieren]);
        var result = TidyExecutor.Apply(plan, Journals, p => p == src ? "open in a player" : null);
        Assert.Equal("open in a player", Assert.Single(result.Failed).Reason);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Undo_after_a_crash_mid_run_reverses_only_what_moved()
    {
        var a = Touch("Frieren/Season 1/Frieren - 01.mkv", "e1");
        var plan = new TidyPlanner(NamingTemplate.Default).Build(Lib, [Frieren]);
        var result = TidyExecutor.Apply(plan, Journals);

        // Pretend the journal also recorded the intent of a move that never happened.
        var journal = TidyExecutor.Load(result.JournalPath)!;
        journal.Moves.Add(new TidyMove(Path.Combine(Lib, "ghost.mkv"), Path.Combine(Lib, "ghost2.mkv"), false, null));
        File.WriteAllText(result.JournalPath, System.Text.Json.JsonSerializer.Serialize(journal));

        var undone = TidyExecutor.Undo(result.JournalPath);
        Assert.True(File.Exists(a));
        Assert.Single(undone.Moved);
    }
}

public sealed class TidyRecordsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-records-").FullName;
    private string Lib => Path.Combine(_dir, "Library");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private AppDbContext Open()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "t.db")};Pooling=False").Options);
        db.Database.Migrate();
        return db;
    }

    private string L(params string[] p) => Path.Combine([Lib, .. p]);

    [Fact]
    public async Task Download_jobs_follow_their_files_and_batch_folders()
    {
        var oldFile = L("Frieren", "Season 1", "[Grp] Frieren - 05.mkv");
        var newFile = L("Frieren (2023)", "Season 01", "Frieren S01E05.mkv");
        await using (var db = Open())
        {
            db.DownloadJobs.AddRange(
                new DownloadJob { DownloadLink = "a", Status = JobStatus.Completed, FinalFilePath = oldFile },
                new DownloadJob { DownloadLink = "b", Status = JobStatus.Completed, FinalFilePath = L("Frieren") },
                new DownloadJob { DownloadLink = "c", Status = JobStatus.Completed, FinalFilePath = "vault" },
                new DownloadJob { DownloadLink = "d", Status = JobStatus.Completed, FinalFilePath = L("Other", "x.mkv") });
            await db.SaveChangesAsync();

            await TidyRecords.UpdateAsync(db, Lib, [new TidyMove(oldFile, newFile, false, 1)]);
        }

        await using (var db = Open())
        {
            var paths = db.DownloadJobs.OrderBy(j => j.DownloadLink).Select(j => j.FinalFilePath).ToList();
            Assert.Equal([newFile, L("Frieren (2023)"), "vault", L("Other", "x.mkv")], paths);
        }
    }

    [Fact]
    public async Task The_original_name_survives_renames_and_is_dropped_when_it_comes_back()
    {
        var original = L("Frieren", "Season 1", "[Grp] Frieren - 05.mkv");
        var tidied   = L("Frieren (2023)", "Season 01", "Frieren S01E05.mkv");
        var again    = L("Frieren", "Season 1", "05.mkv");

        await using var db = Open();
        await TidyRecords.UpdateAsync(db, Lib, [new TidyMove(original, tidied, false, 1)]);
        Assert.Equal("[Grp] Frieren - 05.mkv", await TidyRecords.OriginalNameAsync(db, tidied));

        // A second tidy (another preset) keeps the name it was downloaded as, not the first tidy's.
        await TidyRecords.UpdateAsync(db, Lib, [new TidyMove(tidied, again, false, 1)]);
        Assert.Equal("[Grp] Frieren - 05.mkv", await TidyRecords.OriginalNameAsync(db, again));
        Assert.Single(db.LibraryFileOrigins);

        // Undo back to the downloaded name: nothing left to remember.
        await TidyRecords.UpdateAsync(db, Lib, [new TidyMove(again, original, false, 1)]);
        Assert.Empty(db.LibraryFileOrigins);
        Assert.Equal("[Grp] Frieren - 05.mkv", await TidyRecords.OriginalNameAsync(db, original));
    }

    [Fact]
    public async Task Sidecar_moves_leave_the_records_alone()
    {
        await using var db = Open();
        await TidyRecords.UpdateAsync(db, Lib, [new TidyMove(L("a.ass"), L("b.ass"), true, 1)]);
        Assert.Empty(db.LibraryFileOrigins);
    }
}
