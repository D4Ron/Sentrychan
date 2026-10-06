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

    [AvaloniaFact]
    public async Task The_parts_switch_shows_only_for_a_show_with_parts_and_saves_at_once()
    {
        var factory = new Factory(Path.Combine(_root, "p.db"));
        Series part2, plain;
        await using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            part2 = new Series { Title = SeasonLayoutTests.Mushoku[1].CanonicalTitle, MalId = 2, TotalEpisodes = 12 };
            plain = new Series { Title = "Plain Show", MalId = 99 };
            db.Series.AddRange(part2, plain);
            await db.SaveChangesAsync();
        }

        var before = Sentrychan.Core.Library.SeasonLayout.Resolver;
        Sentrychan.Core.Library.SeasonLayout.Resolver = new SeasonLayoutTests.Chain(SeasonLayoutTests.Mushoku);
        try
        {
            SeriesDetailViewModel Vm(Series s) => new(s, Stub<ISeriesService>.Create(), Stub<IAnimeApiService>.Create(), factory,
                Stub<ITitleAliasService>.Create(), Stub<IRssMonitorService>.Create(), new VideoFileLocator(new ConfigService(factory), new EpisodeNormalizer(), factory));
            Assert.False(Vm(plain).HasParts);

            var vm = Vm(part2);
            Assert.True(vm.HasParts);
            vm.SeparateParts = true;
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
                await using var db = factory.CreateDbContext();
                if (db.Series.Single(s => s.Id == part2.Id).SeparateParts) break;
            }
            await using (var db = factory.CreateDbContext())
                Assert.True(db.Series.Single(s => s.Id == part2.Id).SeparateParts, "the switch wasn't saved");

            // "Groups release episode 1 as 12": saved as an offset of 11, and the tiles show both numbers.
            vm.EpisodeOneNumber = "12";
            for (var i = 0; i < 100 && vm.EpisodeGrid.FirstOrDefault()?.AbsoluteText != "#12"; i++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal("#12", vm.EpisodeGrid[0].AbsoluteText);
            Assert.Equal("#23", vm.EpisodeGrid[11].AbsoluteText);
            Assert.Contains("#12–23 counted straight through (your setting)", vm.FamilyLine);
            await using (var db = factory.CreateDbContext())
                Assert.Equal(11, db.Series.Single(s => s.Id == part2.Id).EpisodeNumberOffset);

            Application.Current!.Resources["MonitoringStateToBoolConverter"] = new MonitoringStateToBoolConverter();
            var window = new Window { Width = 1300, Height = 2000, Content = new SeriesDetailView { DataContext = vm } };
            window.Show();
            for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
                frame!.Save(Path.Combine(Directory.CreateDirectory(dir).FullName, "series-detail-numbers.png"));
            window.Close();
        }
        finally { Sentrychan.Core.Library.SeasonLayout.Resolver = before; }
    }
}
