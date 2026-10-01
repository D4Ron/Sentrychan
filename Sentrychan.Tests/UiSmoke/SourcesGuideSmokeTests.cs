using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.Tests.UiSmoke;

[Collection(EnvironmentCollection.Name)] // points SENTRYCHAN_DATA_DIR at a temp folder (the packs folder lives there)
public sealed class SourcesGuideSmokeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-guideui-").FullName;
    private readonly string? _dataDirBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public SourcesGuideSmokeTests() => Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, Path.Combine(_root, "data"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, _dataDirBefore);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public async Task The_guide_shows_for_an_app_without_sources_and_stops_once_it_has_one()
    {
        var factory = new Factory(Path.Combine(_root, "t.db"));
        await using (var db = factory.CreateDbContext()) db.Database.Migrate();
        Assert.False(await SourcesGuideViewModel.HasAnySourcesAsync(factory));

        var vm = new SourcesGuideViewModel(factory, () => null);
        await vm.RefreshAsync();
        Assert.Equal("Right now: no RSS feeds · no source packs · Mihon extensions off", vm.Have);

        var dialog = new SourcesGuideDialog { DataContext = vm };
        dialog.Show();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "sources-guide.png"));
        }

        vm.DontShowAgain = true;
        dialog.Close();
        for (var i = 0; i < 20 && !await SourcesGuideViewModel.IsDismissedAsync(factory); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(await SourcesGuideViewModel.IsDismissedAsync(factory));

        await using (var db = factory.CreateDbContext())
        {
            db.RssFeeds.Add(new RssFeed { Url = "https://example.test/rss" });
            await db.SaveChangesAsync();
        }
        Assert.True(await SourcesGuideViewModel.HasAnySourcesAsync(factory));
    }
}
