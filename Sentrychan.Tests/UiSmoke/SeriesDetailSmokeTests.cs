using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.UI.Converters;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views;

namespace Sentrychan.Tests.UiSmoke;

[Collection(EnvironmentCollection.Name)]
public sealed class SeriesDetailSmokeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-detail-").FullName;
    private readonly string? _dataDirBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);

    public SeriesDetailSmokeTests() => Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, Path.Combine(_root, "data"));

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

    /// <summary>Any interface, every method answering at once with nothing (an empty list, null, default).</summary>
    public class Stub<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, Stub<T>>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var type = method!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                object? value = inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>)
                    ? Activator.CreateInstance(inner)
                    : inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    [AvaloniaFact]
    public async Task A_long_show_opens_at_once_when_MyAnimeList_has_nothing()
    {
        var app = Application.Current!;
        app.Resources["MonitoringStateToBoolConverter"] = new MonitoringStateToBoolConverter();

        var factory = new Factory(Path.Combine(_root, "t.db"));
        Series series;
        await using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            series = new Series { Title = "Long Show", MalId = 1, TotalEpisodes = 170, LastEpisodeNumber = 120, AiringStatus = "Finished Airing" };
            db.Series.Add(series);
            await db.SaveChangesAsync();
            for (var ep = 1; ep <= 130; ep++)
            {
                db.DownloadJobs.Add(new DownloadJob { SeriesId = series.Id, EpisodeNumber = ep, RssTitle = $"[Grp] Long Show - {ep:00} (1080p).mkv", Status = JobStatus.Completed });
                db.WatchHistory.Add(new WatchHistoryEntry { SeriesId = series.Id, EpisodeNumber = ep, WatchedAt = DateTime.UtcNow.AddDays(-ep) });
            }
            await db.SaveChangesAsync();
        }

        var locator = new VideoFileLocator(new ConfigService(factory), new EpisodeNormalizer(), factory);
        var clock = Stopwatch.StartNew();
        var vm = new SeriesDetailViewModel(series, Stub<ISeriesService>.Create(), Stub<IAnimeApiService>.Create(), factory,
            Stub<ITitleAliasService>.Create(), Stub<IRssMonitorService>.Create(), locator);
        for (var i = 0; i < 500 && (vm.IsLoading || vm.EpisodeGrid.Count == 0); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        clock.Stop();

        Assert.Equal(170, vm.EpisodeGrid.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.Equal(8, vm.WatchHistoryShown.Count());
        Assert.True(vm.HasMoreHistory);
        Assert.Equal("Any", vm.QualityPreferenceChoice);

        // The online details finish later and say so instead of covering the page.
        for (var i = 0; i < 100 && vm.DetailsStatus?.StartsWith("Loading") != false; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Contains("isn't answering", vm.DetailsStatus);

        var window = new Window { Width = 1300, Height = 1000, Content = new SeriesDetailView { DataContext = vm } };
        window.Show();
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "series-detail.png"));
        }
        window.Close();
    }
}
