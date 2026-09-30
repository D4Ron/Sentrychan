using ReactiveUI;
using Sentrychan.Core.Library;
using Sentrychan.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>One file in the tidy plan list: before → after, and whether it will move.</summary>
public sealed class TidyRowVm : ReactiveObject
{
    private readonly Action _selectionChanged;

    public TidyRowVm(TidyItem item, string library, Action selectionChanged, Func<string, Task> leaveFolderAlone)
    {
        Item = item;
        _selectionChanged = selectionChanged;
        Before = Path.GetRelativePath(library, item.Source);
        After  = item.Destination is { } d ? Path.GetRelativePath(library, d) : "—  stays where it is";
        LeaveFolderAloneCommand = ReactiveCommand.CreateFromTask(() => leaveFolderAlone(item.ShowFolder));
    }

    public ReactiveCommand<Unit, Unit> LeaveFolderAloneCommand { get; }
    public string LeaveFolderAloneText => $"Always leave “{ShowFolder}” alone";

    public TidyItem Item { get; }
    public string Before { get; }
    public string After { get; }
    public string ShowFolder => Item.ShowFolder;
    public string? Note => Item.Note;
    public bool HasNote => !string.IsNullOrEmpty(Item.Note);

    public bool CanSelect => Item.Status == TidyItemStatus.Ready;
    public bool IsGood => Item.Status == TidyItemStatus.Ready;
    public bool IsBad  => Item.Status is TidyItemStatus.Collision or TidyItemStatus.Unsure;

    public string StatusText => Item.Status switch
    {
        TidyItemStatus.Ready     => "Move",
        TidyItemStatus.Collision => "Collision",
        TidyItemStatus.Unsure    => "Unsure",
        _                        => "Skipped",
    };

    public string? SidecarText => Item.Sidecars.Count switch
    {
        0 => null,
        1 => "+ 1 subtitle / extra file",
        var n => $"+ {n} subtitle / extra files",
    };

    public bool Selected
    {
        get => Item.Selected;
        set
        {
            if (!CanSelect || Item.Selected == value) return;
            Item.Selected = value;
            this.RaisePropertyChanged();
            _selectionChanged();
        }
    }
}

/// <summary>A library folder the user told tidying to skip, with the way back.</summary>
public sealed class LeftAloneFolderVm
{
    public LeftAloneFolderVm(string name, Func<string, Task> includeAgain)
    {
        Name = name;
        IncludeAgainCommand = ReactiveCommand.CreateFromTask(() => includeAgain(name));
    }

    public string Name { get; }
    public ReactiveCommand<Unit, Unit> IncludeAgainCommand { get; }
}

/// <summary>
/// The Tidy library dialog: builds the plan, lists every file before → after with a tick box,
/// moves only what's ticked, and can undo the last run.
/// </summary>
public sealed class TidyLibraryViewModel : ViewModelBase
{
    private readonly LibraryTidyService _tidy;
    private TidyPlan? _plan;

    public TidyLibraryViewModel(LibraryTidyService tidy)
    {
        _tidy = tidy;
        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        RefreshCommand    = ReactiveCommand.CreateFromTask(RefreshAsync, idle);
        ApplyCommand      = ReactiveCommand.CreateFromTask(ApplyAsync,
            this.WhenAnyValue(x => x.IsBusy, x => x.SelectedCount, (busy, n) => !busy && n > 0));
        UndoCommand       = ReactiveCommand.CreateFromTask(UndoAsync,
            this.WhenAnyValue(x => x.IsBusy, x => x.CanUndo, (busy, can) => !busy && can));
        SelectAllCommand  = ReactiveCommand.Create(() => SetAll(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAll(false));
    }

    public ObservableCollection<TidyRowVm> Rows { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public ObservableCollection<LeftAloneFolderVm> LeftAlone { get; } = new();
    public bool HasLeftAlone => LeftAlone.Count > 0;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectNoneCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private string _summary = "Looking at your library…";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string? _status;
    public string? Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _namingText = string.Empty;
    public string NamingText { get => _namingText; private set => this.RaiseAndSetIfChanged(ref _namingText, value); }

    private bool _canUndo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }

    private int _selectedCount;
    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedCount, value);
            this.RaisePropertyChanged(nameof(ApplyText));
        }
    }

    public string ApplyText => SelectedCount == 1 ? "Move 1 file" : $"Move {SelectedCount} files";
    public bool HasNotes => Notes.Count > 0;
    public bool IsEmpty => !IsBusy && Rows.Count == 0;

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            CanUndo = _tidy.CanUndo;
            _plan = await _tidy.PlanAsync();
            Rows.Clear();
            Notes.Clear();
            LeftAlone.Clear();
            if (_plan == null)
            {
                Summary = "Set a library folder in Settings → General first.";
                SelectedCount = 0;
                return;
            }

            NamingText = _plan.Naming.Preset switch
            {
                NamingPreset.JellyfinPlex => "Naming: Jellyfin/Plex",
                NamingPreset.Minimal      => "Naming: Minimal",
                _                         => $"Naming: {_plan.Naming.Template}",
            };
            foreach (var note in _plan.Notes) Notes.Add(note);
            foreach (var folder in _plan.LeftAlone) LeftAlone.Add(new LeftAloneFolderVm(folder, f => SetLeftAloneAsync(f, false)));

            // Problems first, so what won't move is seen before anything is applied.
            foreach (var item in _plan.Items
                         .OrderBy(i => i.Status == TidyItemStatus.Ready ? 1 : 0)
                         .ThenBy(i => i.ShowFolder, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(i => i.Source, StringComparer.OrdinalIgnoreCase))
                Rows.Add(new TidyRowVm(item, _plan.LibraryPath, UpdateCounts, f => SetLeftAloneAsync(f, true)));

            Summary =
                $"{_plan.Count(TidyItemStatus.Ready)} to move · {_plan.Count(TidyItemStatus.Collision)} collisions · " +
                $"{_plan.Count(TidyItemStatus.Unsure)} unsure · {_plan.Count(TidyItemStatus.Skipped)} skipped · " +
                $"{_plan.AlreadyTidy} already tidy";
            UpdateCounts();
        }
        catch (Exception ex)
        {
            Summary = $"Couldn't read the library: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            this.RaisePropertyChanged(nameof(HasNotes));
            this.RaisePropertyChanged(nameof(HasLeftAlone));
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    private async Task ApplyAsync()
    {
        if (_plan == null) return;
        IsBusy = true;
        try
        {
            var result = await _tidy.ApplyAsync(_plan);
            PlaybackPositionStore.Move(result.Moved.Where(m => !m.IsSidecar).Select(m => (m.From, m.To)));
            var videos = result.Moved.Count(m => !m.IsSidecar);
            Status = result.Failed.Count == 0
                ? $"Moved {videos} file(s). \"Undo last tidy\" puts them back."
                : $"Moved {videos} file(s); {result.Failed.Count} stayed put — {result.Failed[0].Reason}.";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private async Task UndoAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _tidy.UndoLastAsync();
            if (result == null) Status = "There's no tidy to undo.";
            else
            {
                PlaybackPositionStore.Move(result.Moved.Where(m => !m.IsSidecar).Select(m => (m.From, m.To)));
                Status = result.Failed.Count == 0
                    ? $"Put {result.Moved.Count} file(s) back where they were."
                    : $"Put {result.Moved.Count} file(s) back; {result.Failed.Count} couldn't be — {result.Failed[0].Reason}.";
            }
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private async Task SetLeftAloneAsync(string folder, bool leaveAlone)
    {
        try
        {
            await _tidy.SetLeftAloneAsync(folder, leaveAlone);
            Status = leaveAlone
                ? $"“{folder}” will be left alone from now on."
                : $"“{folder}” is included in tidying again.";
        }
        catch (Exception ex) { Status = ex.Message; }
        await RefreshAsync();
    }

    private void SetAll(bool selected)
    {
        foreach (var row in Rows.Where(r => r.CanSelect)) row.Selected = selected;
        UpdateCounts();
    }

    private void UpdateCounts() => SelectedCount = Rows.Count(r => r.CanSelect && r.Selected);
}
