using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Vault;
using Sentrychan.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

public sealed class VaultItemVm : ViewModelBase
{
    public VaultItemVm(VaultEntry entry) => Entry = entry;

    public VaultEntry Entry { get; }
    public string Title => Entry.Title;
    public string Details
    {
        get
        {
            var parts = new List<string> { VaultService.FormatSize(Entry.Size), Entry.AddedAt.ToLocalTime().ToString("d MMM yyyy") };
            if (Entry.DurationSeconds > 0 && Entry.PositionSeconds > 0)
                parts.Add(Entry.PositionSeconds >= Entry.DurationSeconds - 30
                    ? "watched"
                    : $"{Entry.PositionSeconds / Entry.DurationSeconds:P0} watched");
            return string.Join("  ·  ", parts);
        }
    }

    private bool _confirmingDelete;
    public bool ConfirmingDelete { get => _confirmingDelete; set => this.RaiseAndSetIfChanged(ref _confirmingDelete, value); }
}

/// <summary>
/// The vault page, reachable only in secret mode: private videos, plus the controls that keep
/// secret mode private — the unlock PIN, the key backup, and the image-cache purge.
/// </summary>
public sealed class VaultViewModel : ViewModelBase
{
    private static readonly string[] VideoPatterns = ["*.mkv", "*.mp4", "*.avi", "*.webm", "*.m4v", "*.mov", "*.wmv"];

    private readonly Action<string, string> _toast;
    private readonly VaultService? _vault;

    public VaultViewModel(Action<string, string> toast)
    {
        _toast = toast;
        _vault = App.Services?.GetService<VaultService>();

        PlayCommand          = ReactiveCommand.CreateFromTask<VaultItemVm>(PlayAsync);
        DeleteCommand        = ReactiveCommand.CreateFromTask<VaultItemVm>(DeleteAsync);
        CancelDeleteCommand  = ReactiveCommand.Create<VaultItemVm>(i => i.ConfirmingDelete = false);
        AddFilesCommand      = ReactiveCommand.CreateFromTask(AddFilesAsync);
        MoveMangaCommand     = ReactiveCommand.CreateFromTask(MoveMangaAsync);
        SetPinCommand        = ReactiveCommand.Create(SetPin);
        ExportKeyCommand     = ReactiveCommand.CreateFromTask(ExportKeyAsync);
        ImportKeyCommand     = ReactiveCommand.CreateFromTask(ImportKeyAsync);
        ClearImageCacheCommand = ReactiveCommand.Create(ClearImageCache);
    }

    public ObservableCollection<VaultItemVm> Videos { get; } = new();

    private string _summary = string.Empty;
    public string Summary { get => _summary; set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public bool IsEmpty => Videos.Count == 0;

    public bool UsingBuiltInCode => !SecretPin.IsSet;

    private string? _newPin;
    public string? NewPin { get => _newPin; set => this.RaiseAndSetIfChanged(ref _newPin, value); }

    private string? _confirmPin;
    public string? ConfirmPin { get => _confirmPin; set => this.RaiseAndSetIfChanged(ref _confirmPin, value); }

    private string? _passphrase;
    public string? Passphrase { get => _passphrase; set => this.RaiseAndSetIfChanged(ref _passphrase, value); }

    public ReactiveCommand<VaultItemVm, Unit> PlayCommand { get; }
    public ReactiveCommand<VaultItemVm, Unit> DeleteCommand { get; }
    public ReactiveCommand<VaultItemVm, Unit> CancelDeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFilesCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveMangaCommand { get; }
    public ReactiveCommand<Unit, Unit> SetPinCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearImageCacheCommand { get; }

    public async Task LoadAsync()
    {
        if (_vault == null) { Summary = "The vault isn't available."; return; }
        try { await _vault.EnsureReadyAsync(); }
        catch (Exception ex) { Summary = $"The vault couldn't be opened: {ex.Message}"; return; }

        var all = _vault.Entries;
        var videos = all.Where(e => e.Kind == VaultKind.Video).OrderByDescending(e => e.AddedAt).ToList();
        var chapters = all.Where(e => e.Kind == VaultKind.Page).Select(e => e.Collection).Distinct().Count();

        Videos.Clear();
        foreach (var v in videos) Videos.Add(new VaultItemVm(v));
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(UsingBuiltInCode));

        Summary = $"{videos.Count} video(s), {chapters} manga chapter(s) · {VaultService.FormatSize(all.Sum(e => e.Size))} encrypted";
    }

    // ── Videos ────────────────────────────────────────────────────────

    private async Task PlayAsync(VaultItemVm item)
    {
        // The whole list is the playlist, oldest first, so "next" continues through it.
        var ordered = Videos.Reverse().ToList();
        var playlist = ordered.Select(v => new PlaybackItem(v.Title, VaultId: v.Entry.Id)).ToList();
        await PlayerLauncher.PlayAsync(playlist, ordered.IndexOf(item));
    }

    private async Task DeleteAsync(VaultItemVm item)
    {
        if (_vault == null) return;
        if (!item.ConfirmingDelete) { item.ConfirmingDelete = true; return; }
        await _vault.RemoveAsync(item.Entry.Id);
        Videos.Remove(item);
        this.RaisePropertyChanged(nameof(IsEmpty));
        await LoadAsync();
    }

    /// <summary>Encrypts chosen video files into the vault and removes the originals.</summary>
    private async Task AddFilesAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add videos to the vault (the originals are removed)",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Videos") { Patterns = VideoPatterns }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;

        IsBusy = true;
        var done = 0;
        try
        {
            foreach (var path in paths)
            {
                var n = done + 1;
                var progress = new Progress<double>(p => Status = $"Encrypting {n} of {paths.Count} — {p:P0}");
                await _vault.AddFileAsync(path, Path.GetFileNameWithoutExtension(path), VaultKind.Video,
                    deleteSource: true, progress: progress);
                done++;
            }
            Status = $"{done} file(s) added. The originals were removed.";
        }
        catch (Exception ex)
        {
            Status = $"Stopped after {done} file(s): {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await LoadAsync();
        }
    }

    private async Task MoveMangaAsync()
    {
        var downloads = App.Services?.GetService<IMangaDownloadService>();
        if (downloads == null) return;
        IsBusy = true;
        try
        {
            var moved = await downloads.MoveAdultDownloadsIntoVaultAsync(new Progress<string>(s => Status = s));
            Status = moved == 0
                ? "No adult manga downloads outside the vault."
                : $"{moved} chapter(s) moved into the vault; their folders were removed.";
        }
        catch (Exception ex) { Status = $"Move failed: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            await LoadAsync();
        }
    }

    // ── Keeping secret mode private ───────────────────────────────────

    private void SetPin()
    {
        if (string.IsNullOrWhiteSpace(NewPin) || NewPin.Length < 4) { Status = "Use at least 4 characters."; return; }
        if (NewPin != ConfirmPin) { Status = "The two entries don't match."; return; }
        SecretPin.Set(NewPin);
        NewPin = ConfirmPin = null;
        this.RaisePropertyChanged(nameof(UsingBuiltInCode));
        Status = "Unlock code changed. The built-in code no longer works.";
    }

    private async Task ExportKeyAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        if (string.IsNullOrEmpty(Passphrase) || Passphrase.Length < 8)
        {
            Status = "Choose a passphrase of at least 8 characters for the backup first.";
            return;
        }
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save vault key backup",
            SuggestedFileName = "sentrychan-key.bak",
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        await _vault.ExportKeyAsync(path, Passphrase);
        Passphrase = null;
        Status = "Key backup saved. Keep it and its passphrase somewhere safe — without both, the vault can't be opened on a new PC.";
    }

    private async Task ImportKeyAsync()
    {
        if (_vault == null || TopLevel() is not { } top) return;
        if (string.IsNullOrEmpty(Passphrase)) { Status = "Enter the backup's passphrase first."; return; }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open vault key backup" });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

        var ok = await _vault.ImportKeyAsync(path, Passphrase);
        Passphrase = null;
        Status = ok ? "Key restored." : "That backup and passphrase don't open this vault.";
        if (ok) await LoadAsync();
    }

    private void ClearImageCache()
    {
        var removed = Controls.AsyncImage.ClearDiskCache();
        Controls.AsyncImage.ClearMemoryCache();
        Status = $"Image cache cleared ({removed} file(s)). Covers load again as you browse.";
    }

    private static TopLevel? TopLevel() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;
}
