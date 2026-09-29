using System.Reactive;
using ReactiveUI;
using Sentrychan.Core.MihonBackup;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Import a Mihon backup: work out the plan first (what's matched, what's added, what can't be),
/// show it, and only write when the user says so.
/// </summary>
public sealed class MihonImportViewModel : ViewModelBase
{
    private readonly MihonBackupImporter _importer;
    private readonly byte[] _file;
    private ImportPlan? _plan;
    private CancellationTokenSource? _cts;

    public MihonImportViewModel(MihonBackupImporter importer, byte[] file, string fileName)
    {
        _importer = importer;
        _file = file;
        FileName = fileName;
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync, this.WhenAnyValue(x => x.CanImport));
        CancelCommand = ReactiveCommand.Create(() => _cts?.Cancel());
    }

    public string FileName { get; }

    private string _status = "Reading the backup…";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isBusy = true;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private bool _canImport;
    public bool CanImport { get => _canImport; private set => this.RaiseAndSetIfChanged(ref _canImport, value); }

    private bool _isDone;
    public bool IsDone { get => _isDone; private set => this.RaiseAndSetIfChanged(ref _isDone, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private IReadOnlyList<string> _details = [];
    /// <summary>Lines under the summary: what will be added, the new categories.</summary>
    public IReadOnlyList<string> Details { get => _details; private set => this.RaiseAndSetIfChanged(ref _details, value); }

    private IReadOnlyList<UnmatchedRow> _unmatched = [];
    public IReadOnlyList<UnmatchedRow> Unmatched
    {
        get => _unmatched;
        private set { this.RaiseAndSetIfChanged(ref _unmatched, value); this.RaisePropertyChanged(nameof(HasUnmatched)); }
    }

    public bool HasUnmatched => _unmatched.Count > 0;

    private string _missingSources = string.Empty;
    public string MissingSources { get => _missingSources; private set => this.RaiseAndSetIfChanged(ref _missingSources, value); }

    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public sealed record UnmatchedRow(string Title, string Source, string Reason);

    public async Task PlanAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            _plan = await _importer.PlanAsync(_file, _cts.Token);
            var p = _plan;
            Summary = $"{p.InLibrary} already in your library · {p.ToAdd} to add · {p.Unmatched.Count()} can't be matched";
            var lines = new List<string>();
            if (p.InLibrary > 0) lines.Add($"Titles already in your library get their categories, read chapters, bookmarks and history.");
            if (p.ToAdd > 0) lines.Add($"{p.ToAdd} title(s) will be added from installed Mihon extensions.");
            if (p.NewCategories.Count > 0) lines.Add("New categories: " + string.Join(", ", p.NewCategories));
            lines.Add("Nothing you've read here is marked unread.");
            Details = lines;
            ShowUnmatched(p);
            CanImport = p.InLibrary + p.ToAdd > 0 || p.NewCategories.Count > 0;
            Status = CanImport ? "Nothing has changed yet." : "There's nothing in this backup to bring over.";
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private void ShowUnmatched(ImportPlan p)
    {
        Unmatched = p.Unmatched.Select(t => new UnmatchedRow(t.Title, t.SourceName, t.Reason ?? string.Empty)).ToList();
        MissingSources = p.MissingSources.Count == 0 ? string.Empty
            : "Sources of the titles that couldn't be matched: " +
              string.Join(", ", p.MissingSources.Select(s => $"{s.Name} ({s.Titles})"));
    }

    private async Task ImportAsync()
    {
        if (_plan == null) return;
        CanImport = false;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(s => Status = s);
        try
        {
            var r = await _importer.ApplyAsync(_plan, progress, _cts.Token);
            Summary = $"Done: {r.Updated} updated, {r.Added} added, {r.Unmatched.Count} not matched";
            Details =
            [
                $"{r.ChaptersRead} chapter(s) marked read, {r.Bookmarks} bookmark(s), {r.History} history entr{(r.History == 1 ? "y" : "ies")}.",
                r.CategoriesCreated > 0 ? $"{r.CategoriesCreated} categor{(r.CategoriesCreated == 1 ? "y" : "ies")} created." : "No new categories.",
            ];
            ShowUnmatched(_plan);
            Status = "Imported.";
            IsDone = true;
        }
        catch (OperationCanceledException) { Status = "Stopped. What was imported so far stays; importing again carries on."; }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
