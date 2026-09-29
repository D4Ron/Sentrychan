using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.ViewModels.Mihon;
using Sentrychan.UI.Views.Mihon;

namespace Sentrychan.Tests.UiSmoke;

/// <summary>
/// Loads each Mihon-style screen with real view-models over a seeded database and lays it out,
/// so a broken binding, template or resource fails here rather than on someone's desktop. Set
/// SENTRYCHAN_SCREENSHOTS to a folder to also save what each screen looks like.
/// </summary>
public sealed class MihonScreensSmokeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-ui-").FullName;
    private readonly Factory _factory;

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private sealed class MemoryConfig : IConfigService
    {
        private readonly Dictionary<string, object?> _values = [];
        public Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var v) && v is T t ? t : defaultValue);
        public Task SetValueAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    public MihonScreensSmokeTests()
    {
        _factory = new Factory(Path.Combine(_dir, "t.db"));
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a view may still hold an image */ }
    }

    private static MangaChapterInfo Info(int n) =>
        new($"c{n}", n.ToString(), n, null, n % 3 == 0 ? "A titled chapter" : null, "en", "Group", 20, new DateTime(2026, 9, n % 28 + 1));

    private async Task<(MangaService, MangaLibraryService, MangaSourceRegistry, MangaDownloadService)> SeedAsync()
    {
        var manga = new MangaService(_factory, NullLogger<MangaService>.Instance);
        var library = new MangaLibraryService(_factory, NullLogger<MangaLibraryService>.Instance);
        var registry = new MangaSourceRegistry([new V1Source(browse: true)]);
        var downloads = new MangaDownloadService(registry, manga, _factory, NullLogger<MangaDownloadService>.Instance);

        var reading = await library.CreateCategoryAsync("Reading");
        await library.CreateCategoryAsync("Weekly");
        foreach (var (title, chapters) in new[] { ("Frieren", 12), ("Dungeon Meshi", 20), ("Blue Period", 8) })
        {
            var m = await manga.AddAsync(new Manga { Source = "Old Pack Source", SourceId = title, Title = title });
            await manga.SyncChaptersAsync(m.Id, Enumerable.Range(1, chapters - 2).Select(Info));
            await manga.SyncChaptersAsync(m.Id, Enumerable.Range(1, chapters).Select(Info)); // two "new" chapters
            var first = (await manga.GetByIdAsync(m.Id))!.Chapters.OrderBy(c => c.ChapterSort).First();
            await manga.SaveReadingPositionAsync(first.Id, 4, markRead: true);
            if (title != "Blue Period") await library.SetCategoriesAsync([m.Id], [reading.Id]);
        }
        return (manga, library, registry, downloads);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string name)
    {
        Pump();
        var dir = Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS");
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task Every_tab_of_the_mihon_manga_section_loads_and_renders()
    {
        var (manga, library, registry, downloads) = await SeedAsync();
        var vm = new MihonMangaViewModel(library, manga, registry, downloads, null, new MemoryConfig(), novels: false,
            openTitle: _ => { }, openReader: (_, _) => { }, checkForUpdates: () => Task.FromResult(0));

        var window = new Window { Width = 1280, Height = 820, Content = new MihonMangaView { DataContext = vm } };
        window.Show();

        await vm.LoadTabAsync();
        // Default (holding the one uncategorised title) comes first, then the user's categories.
        Assert.Equal(["Default (1)", "Reading (2)", "Weekly (0)"], vm.Library.Tabs.Select(t => t.Header));
        vm.Library.SelectedTab = vm.Library.Tabs[1];
        Assert.Equal(2, vm.Library.Items.Count);
        Capture(window, "mihon-library-comfortable");

        vm.Library.DisplayMode = LibraryDisplayMode.List;
        Capture(window, "mihon-library-list");
        vm.Library.DisplayMode = LibraryDisplayMode.CompactGrid;
        vm.Library.Items[0].IsSelected = true;
        Capture(window, "mihon-library-compact-selecting");

        foreach (var (tab, name) in new[] { (1, "updates"), (2, "history"), (3, "browse-sources"), (4, "downloads") })
        {
            vm.SelectedTab = tab;
            await vm.LoadTabAsync();
            Capture(window, "mihon-" + name);
        }

        Assert.Equal(3, vm.Updates.Days.Sum(d => d.Rows.Count) / 2);
        Assert.Equal(3, vm.History.Rows.Count);

        // Browse → a source page.
        vm.SelectedTab = 3;
        vm.Browse.Sources[0].OpenCommand.Execute().Subscribe();
        await Task.Delay(100);
        Pump();
        Assert.NotNull(vm.Browse.CurrentSource);
        Assert.NotEmpty(vm.Browse.CurrentSource!.Results);
        Capture(window, "mihon-browse-source");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_mihon_title_page_loads_and_renders()
    {
        var (manga, library, registry, downloads) = await SeedAsync();
        var target = (await manga.GetAllAsync()).First(m => m.Title == "Frieren");
        var vm = new MangaDetailViewModel(target, manga, registry.Sources[0], downloads, () => { }, _ => { }, library);

        var window = new Window { Width = 1280, Height = 820, Content = new MihonTitleView { DataContext = vm } };
        window.Show();
        await vm.InitializeAsync();
        Pump();
        Assert.Equal(12, vm.VisibleChapters.Count);

        vm.VisibleChapters[2].IsSelected = true;
        vm.VisibleChapters[5].IsSelected = true;
        vm.SelectRangeCommand.Execute().Subscribe();
        Assert.Equal(4, vm.SelectedChapterCount);
        Capture(window, "mihon-title-selecting");
        window.Close();
    }
}
