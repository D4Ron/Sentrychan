using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Library;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.Tests.UiSmoke;

[Collection(EnvironmentCollection.Name)] // points SENTRYCHAN_DATA_DIR at a temp folder for the journal
public sealed class TidyDialogSmokeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-tidyui-").FullName;
    private readonly string? _dataDirBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    /// <summary>A resolver whose offline database never loaded — the planner works without one.</summary>
    private sealed class NoResolver : ITitleResolverService
    {
        public bool IsReady => false;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ParsedRelease ParseRelease(string releaseName) => new(releaseName, null, 1, null, null, false);
        public ResolvedAnime? ResolveRelease(string releaseName) => null;
        public ResolvedAnime? ResolveTitle(string title, int season = 1) => null;
        public List<ResolvedAnime> Search(string query, int limit = 20) => [];
    }

    public TidyDialogSmokeTests() => Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, Path.Combine(_root, "data"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, _dataDirBefore);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, "Library", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "v");
    }

    [AvaloniaFact]
    public async Task The_tidy_dialog_lists_the_plan_and_applies_it()
    {
        var factory = new Factory(Path.Combine(_root, "t.db"));
        await using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.AppConfigs.Add(new AppConfig { Key = "LibraryPath", Value = Path.Combine(_root, "Library") });
            db.Series.Add(new Series { MalId = 1, Title = "Frieren", Year = 2023, MediaType = "TV", TotalEpisodes = 28 });
            db.Series.Add(new Series { MalId = 2, Title = "Show", Year = 2020, TotalEpisodes = 12 });
            db.Series.Add(new Series { MalId = 3, Title = "Show Season 2", Year = 2022, TotalEpisodes = null });
            await db.SaveChangesAsync();
        }
        Touch("Frieren/Season 1/[Grp] Sousou no Frieren - 01 (1080p) [ABCD1234].mkv");
        Touch("Frieren/Season 1/[Grp] Sousou no Frieren - 01 (1080p) [ABCD1234].en.ass");
        Touch("Frieren/Season 1/[Grp] Sousou no Frieren - 02 (1080p) [EF567890].mkv");
        Touch("Show/Season 2/[Grp] Show S2 - 14 [1080p].mkv");
        Touch("Frieren (2023)/Season 01/Frieren S01E02.mkv");

        var service = new LibraryTidyService(factory, new ActiveTorrentFiles(), new NoResolver(), NullLogger<LibraryTidyService>.Instance);
        var vm = new TidyLibraryViewModel(service);
        var dialog = new TidyLibraryDialog { DataContext = vm };
        dialog.Show();
        await vm.RefreshAsync();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(1, vm.SelectedCount); // one movable; one collides; one unsure
        var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "tidy-dialog.png"));
        }

        vm.ApplyCommand.Execute().Subscribe();
        // The subtitle moves after its video, so wait for it: it's the last thing to happen.
        var sidecar = Path.Combine(_root, "Library", "Frieren (2023)", "Season 01", "Frieren S01E01.en.ass");
        for (var i = 0; i < 250 && !File.Exists(sidecar); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(File.Exists(Path.Combine(_root, "Library", "Frieren (2023)", "Season 01", "Frieren S01E01.mkv")));
        Assert.True(File.Exists(sidecar));
        dialog.Close();
    }
}
