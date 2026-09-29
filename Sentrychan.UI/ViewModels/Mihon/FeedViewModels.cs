using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>What the Updates and History pages need from the rest of the app.</summary>
public sealed class MangaFeedHost
{
    public required MangaLibraryService Library { get; init; }
    public required IMangaDownloadService Downloads { get; init; }
    public ISecretModeService? SecretMode { get; init; }
    public required Action<Manga> OpenTitle { get; init; }
    public required Action<Manga, int> OpenReader { get; init; }

    /// <summary>Checks sources for new chapters; returns how many titles had some.</summary>
    public Func<Task<int>>? CheckForUpdates { get; init; }

    public bool IncludeAdult => SecretMode?.IsSecretModeActive ?? false;
}

public sealed class UpdateRowVm : ViewModelBase
{
    public UpdateRowVm(ChapterUpdate update, MangaFeedHost host)
    {
        Update = update;
        _isDownloaded = !string.IsNullOrEmpty(update.Chapter.DownloadedPath);
        ReadCommand = ReactiveCommand.Create(() => host.OpenReader(update.Manga, update.Chapter.Id));
        OpenTitleCommand = ReactiveCommand.Create(() => host.OpenTitle(update.Manga));
        DownloadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsDownloaded || IsDownloading) return;
            IsDownloading = true;
            try { IsDownloaded = await host.Downloads.DownloadChapterAsync(update.Manga, update.Chapter) != null; }
            catch { /* the queue page shows why */ }
            finally { IsDownloading = false; }
        });
    }

    public ChapterUpdate Update { get; }
    public string Title => Update.Manga.Title;
    public string CoverPath => Update.Manga.CoverPath;
    public string Source => Update.Manga.Source;
    public string Chapter => string.IsNullOrEmpty(Update.Chapter.ChapterNumber) ? "Oneshot"
        : string.IsNullOrWhiteSpace(Update.Chapter.Title) ? $"Chapter {Update.Chapter.ChapterNumber}"
        : $"Chapter {Update.Chapter.ChapterNumber} — {Update.Chapter.Title}";
    public string Time => Update.FetchedAt.ToLocalTime().ToString("HH:mm");
    public bool IsRead => LibraryQuery.IsRead(Update.Manga, Update.Chapter);

    private bool _isDownloaded;
    public bool IsDownloaded { get => _isDownloaded; private set => this.RaiseAndSetIfChanged(ref _isDownloaded, value); }

    private bool _isDownloading;
    public bool IsDownloading { get => _isDownloading; private set => this.RaiseAndSetIfChanged(ref _isDownloading, value); }

    public ReactiveCommand<Unit, Unit> ReadCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTitleCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
}

public sealed class UpdateDayVm(string label, IReadOnlyList<UpdateRowVm> rows)
{
    public string Label { get; } = label;
    public IReadOnlyList<UpdateRowVm> Rows { get; } = rows;
}

/// <summary>Updates: new chapters across the library, newest first, grouped by day.</summary>
public sealed class MangaUpdatesViewModel : ViewModelBase
{
    private readonly MangaFeedHost _host;
    private readonly bool _novels;

    public MangaUpdatesViewModel(MangaFeedHost host, bool novels)
    {
        _host = host;
        _novels = novels;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        CheckCommand = ReactiveCommand.CreateFromTask(CheckAsync);
    }

    public ObservableCollection<UpdateDayVm> Days { get; } = new();
    public bool IsEmpty => Days.Count == 0;

    private string? _status;
    public string? Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; private set => this.RaiseAndSetIfChanged(ref _isChecking, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckCommand { get; }

    public async Task LoadAsync()
    {
        var updates = await _host.Library.GetUpdatesAsync(_novels, _host.IncludeAdult);
        var today = DateOnly.FromDateTime(DateTime.Now);
        Days.Clear();
        foreach (var (day, items) in UpdatesQuery.ByDay(updates))
            Days.Add(new UpdateDayVm(UpdatesQuery.DayLabel(day, today), items.Select(u => new UpdateRowVm(u, _host)).ToList()));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    private async Task CheckAsync()
    {
        if (_host.CheckForUpdates == null || IsChecking) return;
        IsChecking = true;
        Status = "Checking your library for new chapters…";
        try
        {
            var n = await _host.CheckForUpdates();
            Status = n == 0 ? "No new chapters." : n == 1 ? "1 title has new chapters." : $"{n} titles have new chapters.";
            await LoadAsync();
        }
        catch (Exception ex) { Status = $"Check failed: {ex.Message}"; }
        finally { IsChecking = false; }
    }
}

public sealed class HistoryRowVm : ViewModelBase
{
    public HistoryRowVm(HistoryEntry entry, MangaFeedHost host, Func<HistoryRowVm, Task> remove)
    {
        Entry = entry;
        ResumeCommand = ReactiveCommand.Create(() => host.OpenReader(entry.Manga, entry.Chapter.Id));
        OpenTitleCommand = ReactiveCommand.Create(() => host.OpenTitle(entry.Manga));
        RemoveCommand = ReactiveCommand.CreateFromTask(() => remove(this));
    }

    public HistoryEntry Entry { get; }
    public string Title => Entry.Manga.Title;
    public string CoverPath => Entry.Manga.CoverPath;
    public string Source => Entry.Manga.Source;
    public string Detail
    {
        get
        {
            var ch = string.IsNullOrEmpty(Entry.Chapter.ChapterNumber) ? "Oneshot" : $"Ch. {Entry.Chapter.ChapterNumber}";
            var page = Entry.LastPage > 0 ? $" · page {Entry.LastPage + 1}" : string.Empty;
            return $"{ch}{page} · {SourceRowVm.Humanize(Entry.ReadAt)}";
        }
    }

    public ReactiveCommand<Unit, Unit> ResumeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTitleCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

/// <summary>History: what was read recently, one row per title, with resume and remove.</summary>
public sealed class MangaHistoryViewModel : ViewModelBase
{
    private readonly MangaFeedHost _host;
    private readonly bool _novels;

    public MangaHistoryViewModel(MangaFeedHost host, bool novels)
    {
        _host = host;
        _novels = novels;
        ClearCommand = ReactiveCommand.CreateFromTask(ClearAsync);
    }

    public ObservableCollection<HistoryRowVm> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;

    private bool _confirmingClear;
    public bool ConfirmingClear { get => _confirmingClear; set => this.RaiseAndSetIfChanged(ref _confirmingClear, value); }

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public async Task LoadAsync()
    {
        Rows.Clear();
        foreach (var h in await _host.Library.GetHistoryAsync(_novels, _host.IncludeAdult))
            Rows.Add(new HistoryRowVm(h, _host, RemoveAsync));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    private async Task RemoveAsync(HistoryRowVm row)
    {
        await _host.Library.RemoveHistoryAsync(row.Entry.Manga.Id);
        Rows.Remove(row);
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    private async Task ClearAsync()
    {
        if (!ConfirmingClear) { ConfirmingClear = true; return; }
        ConfirmingClear = false;
        await _host.Library.ClearHistoryAsync();
        await LoadAsync();
    }
}

public sealed class QueueRowVm : ViewModelBase
{
    public QueueRowVm(MangaQueueItem item, MangaQueueViewModel owner, int index)
    {
        Item = item;
        Index = index;
        CancelCommand = ReactiveCommand.Create(() => owner.Downloads.Cancel(item.Chapter.Id));
        MoveUpCommand = ReactiveCommand.Create(() => owner.Downloads.Move(item.Chapter.Id, Index - 1));
        MoveDownCommand = ReactiveCommand.Create(() => owner.Downloads.Move(item.Chapter.Id, Index + 1));
        MoveToTopCommand = ReactiveCommand.Create(() => owner.Downloads.Move(item.Chapter.Id, 0));
    }

    public MangaQueueItem Item { get; }
    public int Index { get; }
    public string Title => Item.Manga.Title;
    public string Chapter => Core.Services.MangaDownloadService.ChapterLabel(Item.Chapter);
    public bool IsRunning => Item.IsRunning;
    public bool IsProgressKnown => Item.Status.PagesTotal > 0;
    public double Progress => Item.Status.PagesTotal > 0 ? 100.0 * Item.Status.PagesDone / Item.Status.PagesTotal : 0;
    public string State => Item.IsRunning
        ? (Item.Status.PagesTotal > 0 ? $"{Item.Status.PagesDone} / {Item.Status.PagesTotal} pages" : "Finding pages…")
        : Item.Status.PagesDone > 0 ? $"Waiting · {Item.Status.PagesDone} pages kept" : "Waiting";

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveToTopCommand { get; }
}

/// <summary>The manga download queue: reorder, pause/resume all, cancel. Mirrors MangaDownloadService.</summary>
public sealed class MangaQueueViewModel : ViewModelBase
{
    private bool _refreshPending;

    public MangaQueueViewModel(IMangaDownloadService downloads)
    {
        Downloads = downloads;
        TogglePauseCommand = ReactiveCommand.Create(() =>
        {
            if (Downloads.IsPaused) Downloads.ResumeAll(); else Downloads.PauseAll();
        });
        CancelAllCommand = ReactiveCommand.Create(() =>
        {
            if (!ConfirmingCancel) { ConfirmingCancel = true; return; }
            ConfirmingCancel = false;
            Downloads.CancelEverything();
        });
        downloads.QueueChanged += Schedule;
        downloads.StatusChanged += _ => Schedule();
        Refresh();
    }

    public IMangaDownloadService Downloads { get; }
    public ObservableCollection<QueueRowVm> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
    public bool IsPaused => Downloads.IsPaused;
    public string PauseText => IsPaused ? "Resume all" : "Pause all";
    public string Summary => Rows.Count == 0 ? "Nothing queued."
        : $"{Rows.Count(r => r.IsRunning)} downloading · {Rows.Count(r => !r.IsRunning)} waiting{(IsPaused ? " · paused" : "")}";

    private bool _confirmingCancel;
    public bool ConfirmingCancel { get => _confirmingCancel; set => this.RaiseAndSetIfChanged(ref _confirmingCancel, value); }

    public ReactiveCommand<Unit, Unit> TogglePauseCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAllCommand { get; }

    // Page progress arrives many times a second from background threads; redraw at most ~4×/s.
    private void Schedule()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() => { _refreshPending = false; Refresh(); }, TimeSpan.FromMilliseconds(250));
    }

    public void Refresh()
    {
        var queue = Downloads.Queue;
        Rows.Clear();
        for (var i = 0; i < queue.Count; i++) Rows.Add(new QueueRowVm(queue[i], this, i));
        foreach (var p in new[] { nameof(IsEmpty), nameof(IsPaused), nameof(PauseText), nameof(Summary) })
            this.RaisePropertyChanged(p);
    }
}
