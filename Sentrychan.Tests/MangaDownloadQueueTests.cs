using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

/// <summary>
/// A source whose page lists wait until the test releases them — so a test decides exactly when
/// each chapter download is "working", and sees which ones the queue started.
/// </summary>
internal sealed class GatedSource : IMangaSourceService
{
    public string SourceName => "Gated";
    public ConcurrentQueue<string> Started { get; } = new();
    public ConcurrentDictionary<string, TaskCompletionSource<List<string>>> Gates { get; } = new();
    public Func<string, List<string>> Pages { get; set; } = _ => [];

    public TaskCompletionSource<List<string>> Gate(string chapter) =>
        Gates.GetOrAdd(chapter, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        Started.Enqueue(chapterSourceId);
        var gate = Gate(chapterSourceId);
        await using var _ = ct.Register(() => gate.TrySetCanceled(ct));
        try { return await gate.Task; }
        finally { Gates.TryRemove(chapterSourceId, out var __); } // a restart after a pause waits on a fresh gate
    }

    public Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default) => Task.FromResult(new List<MangaSearchResult>());
    public Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default) => Task.FromResult<MangaSearchResult?>(null);
    public Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default) => Task.FromResult(new List<MangaChapterInfo>());
    public string GetChapterWebUrl(string chapterSourceId) => chapterSourceId;
}

public sealed class MangaDownloadQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-mangadl-").FullName;
    private readonly GatedSource _source = new();
    private readonly MangaDownloadService _downloads;
    private readonly Manga _manga = new() { Id = 1, Source = "Gated", SourceId = "m", Title = "Queue Test" };

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public MangaDownloadQueueTests()
    {
        var factory = new Factory(Path.Combine(_dir, "t.db"));
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.AppConfigs.Add(new AppConfig { Key = "LibraryPath", Value = _dir });
            db.Manga.Add(new Manga { Id = 1, Source = "Gated", SourceId = "m", Title = "Queue Test" });
            for (var i = 1; i <= 6; i++)
                db.MangaChapters.Add(new MangaChapter { Id = i, MangaId = 1, SourceId = $"c{i}", ChapterNumber = $"{i}" });
            db.SaveChanges();
        }
        _downloads = new MangaDownloadService(new MangaSourceRegistry([_source]),
            new MangaService(factory, NullLogger<MangaService>.Instance), factory, NullLogger<MangaDownloadService>.Instance);
    }

    public void Dispose()
    {
        _downloads.CancelEverything();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a cancelled download may still be letting go */ }
    }

    private static MangaChapter Chapter(int i) => new() { Id = i, MangaId = 1, SourceId = $"c{i}", ChapterNumber = $"{i}" };

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting for the queue");
    }

    private Task<string?> Queue(int i) => _downloads.DownloadChapterAsync(_manga, Chapter(i));

    [Fact]
    public async Task Two_run_at_once_and_the_rest_wait_in_order()
    {
        var tasks = Enumerable.Range(1, 4).Select(Queue).ToList();
        await Until(() => _source.Started.Count == 2);
        await Task.Delay(50);
        Assert.Equal(["c1", "c2"], _source.Started);
        Assert.Equal([1, 2, 3, 4], _downloads.Queue.Select(q => q.Chapter.Id));
        Assert.Equal([true, true, false, false], _downloads.Queue.Select(q => q.IsRunning));

        _source.Gate("c1").SetResult([]);
        await Until(() => _source.Started.Count == 3);
        Assert.Equal("c3", _source.Started.Last());
        Assert.Null(await tasks[0]); // no pages → nothing stored
    }

    [Fact]
    public async Task Moving_a_chapter_to_the_front_runs_it_next()
    {
        foreach (var i in Enumerable.Range(1, 5)) _ = Queue(i);
        await Until(() => _source.Started.Count == 2);

        _downloads.Move(5, 0);
        _source.Gate("c1").SetResult([]);
        await Until(() => _source.Started.Count == 3);
        Assert.Equal("c5", _source.Started.Last());
    }

    [Fact]
    public async Task Pause_interrupts_running_chapters_and_resume_restarts_them_first()
    {
        foreach (var i in Enumerable.Range(1, 3)) _ = Queue(i);
        await Until(() => _source.Started.Count == 2);

        _downloads.PauseAll();
        Assert.True(_downloads.IsPaused);
        await Until(() => _downloads.Queue.All(q => !q.IsRunning));
        Assert.Equal([1, 2, 3], _downloads.Queue.Select(q => q.Chapter.Id));
        Assert.All(_downloads.Queue, q => Assert.Equal(ChapterDownloadState.Queued, q.Status.State));
        await Task.Delay(50);
        Assert.Equal(2, _source.Started.Count); // nothing new started while paused

        _downloads.ResumeAll();
        await Until(() => _source.Started.Count >= 4);
        await Task.Delay(50);
        Assert.Equal(4, _source.Started.Count);
        // The interrupted chapters go first, ahead of c3 which never started.
        Assert.Equal(["c1", "c2"], _source.Started.Skip(2).Order());
    }

    [Fact]
    public async Task Cancelling_a_waiting_chapter_resolves_it_without_running_it()
    {
        foreach (var i in Enumerable.Range(1, 2)) _ = Queue(i);
        var third = Queue(3);
        await Until(() => _source.Started.Count == 2);

        _downloads.Cancel(3);
        Assert.Null(await third);
        Assert.Equal(ChapterDownloadState.Cancelled, _downloads.GetStatus(3)!.State);
        Assert.DoesNotContain(_downloads.Queue, q => q.Chapter.Id == 3);
    }

    [Fact]
    public async Task Asking_twice_for_one_chapter_joins_the_first_request()
    {
        var a = Queue(1);
        var b = Queue(1);
        Assert.Same(a, b);
        Assert.Single(_downloads.Queue);
        _source.Gate("c1").SetResult([]);
        await a;
    }

    [Fact]
    public async Task A_chapter_downloads_its_pages_and_records_where()
    {
        using var server = new PageServer();
        _source.Pages = _ => [server.Url("1.png"), server.Url("2.png")];
        var task = Queue(6);
        await Until(() => _source.Gates.ContainsKey("c6"));
        _source.Gate("c6").SetResult(_source.Pages("c6"));

        var path = await task;
        Assert.NotNull(path);
        Assert.Equal(["001.png", "002.png"], Directory.GetFiles(path!).Select(Path.GetFileName).Order());
        Assert.Equal(ChapterDownloadState.Done, _downloads.GetStatus(6)!.State);
        Assert.Empty(_downloads.Queue);
    }

    /// <summary>Serves a tiny PNG for any path, on a free loopback port.</summary>
    private sealed class PageServer : IDisposable
    {
        private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        private readonly HttpListener _listener = new();
        private readonly string _prefix;

        public PageServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    ctx.Response.ContentType = "image/png";
                    await ctx.Response.OutputStream.WriteAsync(Png);
                    ctx.Response.Close();
                }
            });
        }

        public string Url(string path) => _prefix + path;
        public void Dispose() => _listener.Close();
    }
}
