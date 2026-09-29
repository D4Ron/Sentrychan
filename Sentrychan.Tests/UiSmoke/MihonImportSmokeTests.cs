using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.MihonBackup;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.Tests.MihonBackup;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.Tests.UiSmoke;

public sealed class MihonImportSmokeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-importui-").FullName;

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public async Task The_import_dialog_shows_the_plan_then_the_result()
    {
        var factory = new Factory(Path.Combine(_dir, "t.db"));
        await using (var db = factory.CreateDbContext()) db.Database.Migrate();
        var manga = new MangaService(factory, NullLogger<MangaService>.Instance);
        var library = new MangaLibraryService(factory, NullLogger<MangaLibraryService>.Instance);
        var m = await manga.AddAsync(new Manga { Source = "Old Pack Source", SourceId = "x", Title = "Frieren" });
        await manga.SyncChaptersAsync(m.Id, Enumerable.Range(1, 5).Select(n =>
            new MangaChapterInfo($"c{n}", n.ToString(), n, null, null, "en", null, 10, null)));

        var file = TachibkWriter.Write(new MihonBackupData(
            [
                new BackupManga(9, "/f", "Frieren", null, null, [], 0, null, 0,
                    [TachibkReaderTests.Ch(1, read: true), TachibkReaderTests.Ch(2, read: true)], [0], true, []),
                new BackupManga(9, "/a", "Another Invented Title", null, null, [], 0, null, 0, [], [], true, []),
            ],
            [new BackupCategory("Reading", 0)], [new BackupSource("Example Source", 9)]));
        var importer = new MihonBackupImporter(factory, manga, library, new MangaSourceRegistry([]), null,
            NullLogger<MihonBackupImporter>.Instance);

        var vm = new MihonImportViewModel(importer, file, "mihon-backup.tachibk");
        var dialog = new MihonImportDialog { DataContext = vm };
        dialog.Show();
        await vm.PlanAsync();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CanImport);
        Assert.Single(vm.Unmatched);
        Assert.Contains("Example Source (1)", vm.MissingSources);
        Save(dialog, "mihon-import-plan");

        vm.ImportCommand.Execute().Subscribe();
        for (var i = 0; i < 100 && !vm.IsDone; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.True(vm.IsDone);
        Assert.StartsWith("Done: 1 updated", vm.Summary);
        Save(dialog, "mihon-import-done");
        dialog.Close();
    }

    private static void Save(Avalonia.Controls.Window w, string name)
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }
    }
}
