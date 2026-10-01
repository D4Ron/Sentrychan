using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Settings → Sources → Mihon extensions. Everything here acts at once (it isn't part of the
/// settings page's Save): turning the bridge on, repositories, installing extensions.
/// Repositories are only ever what the user typed in — nothing is shipped, suggested or pre-filled.
/// </summary>
public sealed class MihonExtensionsViewModel : ViewModelBase
{
    private readonly MihonBridgeService _bridge;
    private readonly IConfigService _config;
    private readonly ISecretModeService? _secretMode;
    private List<BridgeExtension> _allExtensions = [];
    private CancellationTokenSource? _installCts;

    /// <summary>Opens a source's settings; set by the view's owner so the VM stays window-free.</summary>
    public Func<SourcePreferencesViewModel, Task>? ShowPreferences { get; set; }

    public MihonExtensionsViewModel(MihonBridgeService bridge, IConfigService config, ISecretModeService? secretMode)
    {
        _bridge = bridge;
        _config = config;
        _secretMode = secretMode;
        _bridge.StateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RaiseState);

        // The download explanation only matters when there's something to download: a server that's
        // already installed (turned off earlier, or copied in) just turns back on.
        BeginEnableCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_bridge.IsInstalled) await EnableAsync();
            else ConfirmingEnable = true;
        });
        CancelEnableCommand = ReactiveCommand.Create(() => { ConfirmingEnable = false; });
        EnableCommand = ReactiveCommand.CreateFromTask(EnableAsync);
        CancelInstallCommand = ReactiveCommand.Create(() => _installCts?.Cancel());
        DisableCommand = ReactiveCommand.CreateFromTask(DisableAsync);
        StartCommand = ReactiveCommand.CreateFromTask(() => Run(async () => { await _bridge.ClientAsync(); await LoadAsync(); }));
        StopCommand = ReactiveCommand.Create(() => _bridge.Stop());
        AddRepoCommand = ReactiveCommand.CreateFromTask(AddRepoAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(() => Run(async () =>
        {
            var client = await _bridge.ClientAsync();
            SetExtensions(await client.RefreshExtensionsAsync());
        }));
    }

    // ── State ───────────────────────────────────────────────────────

    public bool IsSupported => _bridge.IsSupported;
    public string Version => BridgeRelease.Version;

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; private set { this.RaiseAndSetIfChanged(ref _isEnabled, value); RaiseState(); } }

    public bool IsOff => !IsEnabled && !IsInstalling;
    public bool IsInstalling => _bridge.State == BridgeState.Installing;
    public bool IsReady => IsEnabled && _bridge.IsInstalled && !IsInstalling;
    /// <summary>Turned on, but the install failed or was interrupted: offer it again.</summary>
    public bool NeedsInstall => IsEnabled && !_bridge.IsInstalled && !IsInstalling;
    public bool IsRunning => _bridge.State == BridgeState.Running;
    public bool CanStart => IsReady && !IsRunning && _bridge.State != BridgeState.Starting;

    public string StateText => _bridge.State switch
    {
        BridgeState.Off => "Off",
        BridgeState.NotInstalled => "Not installed",
        BridgeState.Installing => "Installing",
        BridgeState.Stopped => "Installed — starts when a source needs it",
        BridgeState.Starting => "Starting…",
        BridgeState.Running => $"Running on {_bridge.Address?.Authority}",
        BridgeState.Failed => "Couldn't start",
        _ => string.Empty,
    };

    public string? LastError => _bridge.LastError;

    private void RaiseState()
    {
        foreach (var p in new[] { nameof(IsOff), nameof(IsInstalling), nameof(IsReady), nameof(NeedsInstall), nameof(IsRunning),
                     nameof(CanStart), nameof(StateText), nameof(LastError) })
            this.RaisePropertyChanged(p);
    }

    private bool _confirmingEnable;
    public bool ConfirmingEnable { get => _confirmingEnable; set => this.RaiseAndSetIfChanged(ref _confirmingEnable, value); }

    private string _progressText = string.Empty;
    public string ProgressText { get => _progressText; private set => this.RaiseAndSetIfChanged(ref _progressText, value); }

    private double _progress;
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }

    private bool _progressKnown;
    public bool ProgressKnown { get => _progressKnown; private set => this.RaiseAndSetIfChanged(ref _progressKnown, value); }

    private string _message = string.Empty;
    /// <summary>The outcome of the last action, or what went wrong — shown under the section.</summary>
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private bool _allowWebChecks;
    public bool AllowWebChecks
    {
        get => _allowWebChecks;
        set
        {
            if (_allowWebChecks == value) return;
            this.RaiseAndSetIfChanged(ref _allowWebChecks, value);
            _ = _config.SetValueAsync(MihonBridgeService.WebChecksKey, value);
            if (IsRunning) Message = "Applies the next time the server starts (Stop, then use a source).";
        }
    }

    // ── Lists ───────────────────────────────────────────────────────

    public ObservableCollection<RepoRowVm> Repos { get; } = new();
    public ObservableCollection<ExtensionRowVm> Extensions { get; } = new();

    private string _newRepoUrl = string.Empty;
    public string NewRepoUrl { get => _newRepoUrl; set => this.RaiseAndSetIfChanged(ref _newRepoUrl, value); }

    private string _extensionFilter = string.Empty;
    public string ExtensionFilter
    {
        get => _extensionFilter;
        set { this.RaiseAndSetIfChanged(ref _extensionFilter, value); ApplyExtensionFilter(); }
    }

    public bool HasExtensions => Extensions.Count > 0;
    public bool HasRepos => Repos.Count > 0;

    public ReactiveCommand<Unit, Unit> BeginEnableCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEnableCommand { get; }
    public ReactiveCommand<Unit, Unit> EnableCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }
    public ReactiveCommand<Unit, Unit> DisableCommand { get; }
    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> AddRepoCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    // ── Loading ─────────────────────────────────────────────────────

    /// <summary>Reads the saved state. Lists are only fetched when the server is already running — opening Settings never starts it.</summary>
    public async Task LoadAsync()
    {
        IsEnabled = await _bridge.IsEnabledAsync();
        if (IsEnabled) ConfirmingEnable = false;
        _allowWebChecks = await _config.GetValueAsync(MihonBridgeService.WebChecksKey, false);
        this.RaisePropertyChanged(nameof(AllowWebChecks));
        if (!IsRunning) return;
        await Run(async () =>
        {
            var client = await _bridge.ClientAsync();
            SetRepos(await client.GetReposAsync());
            SetExtensions(await client.GetExtensionsAsync());
        });
    }

    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = string.Empty;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; RaiseState(); }
    }

    // ── On / off ────────────────────────────────────────────────────

    private async Task EnableAsync()
    {
        ConfirmingEnable = false;
        _installCts = new CancellationTokenSource();
        var progress = new Progress<BridgeInstallProgress>(p =>
        {
            ProgressKnown = p.Fraction.HasValue;
            Progress = (p.Fraction ?? 0) * 100;
            ProgressText = p.Fraction is { } f ? $"{p.Stage} — {f:P0}" : p.Stage + "…";
        });
        IsEnabled = true;
        await Run(async () =>
        {
            try
            {
                await _bridge.EnableAsync(progress, _installCts.Token);
                ProgressText = string.Empty;
                Message = HasRepos ? "Turned on." : "Turned on. Add a repository below to see its extensions.";
            }
            catch (OperationCanceledException)
            {
                await _bridge.DisableAsync();
                IsEnabled = false;
                ProgressText = string.Empty;
                Message = "Cancelled — nothing was installed.";
            }
            catch
            {
                ProgressText = string.Empty;
                throw;
            }
        });
    }

    private async Task DisableAsync()
    {
        await _bridge.DisableAsync();
        IsEnabled = false;
        Repos.Clear();
        Extensions.Clear();
        _allExtensions = [];
        RaiseLists();
        Message = "Turned off. Its sources are hidden until you turn it back on; nothing was deleted.";
    }

    // ── Repositories ────────────────────────────────────────────────

    private async Task AddRepoAsync()
    {
        var url = NewRepoUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            Message = "Enter the repository's full address (https://…).";
            return;
        }
        await Run(async () =>
        {
            var client = await _bridge.ClientAsync();
            await client.AddRepoAsync(url);
            NewRepoUrl = string.Empty;
            SetRepos(await client.GetReposAsync());
            SetExtensions(await client.RefreshExtensionsAsync());
        });
    }

    private void SetRepos(IReadOnlyList<BridgeRepo> repos)
    {
        Repos.Clear();
        foreach (var r in repos)
            Repos.Add(new RepoRowVm(r, row => Run(async () =>
            {
                var client = await _bridge.ClientAsync();
                await client.RemoveRepoAsync(row.Repo.IndexUrl);
                SetRepos(await client.GetReposAsync());
                SetExtensions(await client.GetExtensionsAsync());
            })));
        RaiseLists();
    }

    // ── Extensions ──────────────────────────────────────────────────

    private void SetExtensions(IReadOnlyList<BridgeExtension> extensions)
    {
        _allExtensions = extensions
            .OrderByDescending(e => e.HasUpdate).ThenByDescending(e => e.IsInstalled).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyExtensionFilter();
    }

    private void ApplyExtensionFilter()
    {
        var secret = _secretMode?.IsSecretModeActive == true;
        var q = ExtensionFilter.Trim();
        Extensions.Clear();
        foreach (var e in _allExtensions)
        {
            // Adult extensions follow the app's rule for adult sources: only in secret mode.
            if (e.IsNsfw && !secret) continue;
            if (q.Length > 0 && !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                             && !e.Language.Equals(q, StringComparison.OrdinalIgnoreCase)) continue;
            Extensions.Add(new ExtensionRowVm(e, ActOnAsync, OpenSettingsAsync, CanConfigure(e)));
        }
        RaiseLists();
    }

    private bool CanConfigure(BridgeExtension e) =>
        e.IsInstalled && _bridge.Sources.Any(s => e.SourceIds.Contains(s.Source.Id) && s.Source.IsConfigurable);

    private void RaiseLists()
    {
        this.RaisePropertyChanged(nameof(HasExtensions));
        this.RaisePropertyChanged(nameof(HasRepos));
    }

    private Task ActOnAsync(ExtensionRowVm row, SuwayomiClient.ExtensionAction action) => Run(async () =>
    {
        row.IsWorking = true;
        try
        {
            var client = await _bridge.ClientAsync();
            await client.UpdateExtensionAsync(row.Extension.PackageName, action);
            await _bridge.RefreshSourcesAsync();
            SetExtensions(await client.GetExtensionsAsync());
            Message = action switch
            {
                SuwayomiClient.ExtensionAction.Install => $"{row.Name} installed — its sources are under Browse.",
                SuwayomiClient.ExtensionAction.Update => $"{row.Name} updated.",
                _ => $"{row.Name} uninstalled.",
            };
        }
        finally { row.IsWorking = false; }
    });

    private Task OpenSettingsAsync(ExtensionRowVm row) => Run(async () =>
    {
        var source = _bridge.Sources.FirstOrDefault(s => row.Extension.SourceIds.Contains(s.Source.Id) && s.Source.IsConfigurable);
        if (source == null || ShowPreferences == null) return;
        var vm = new SourcePreferencesViewModel(source, _bridge);
        await vm.LoadAsync();
        await ShowPreferences(vm);
    });
}

public sealed class RepoRowVm
{
    public RepoRowVm(BridgeRepo repo, Func<RepoRowVm, Task> remove)
    {
        Repo = repo;
        RemoveCommand = ReactiveCommand.CreateFromTask(() => remove(this));
    }

    public BridgeRepo Repo { get; }
    public string Name => string.IsNullOrWhiteSpace(Repo.Name) ? Repo.IndexUrl : Repo.Name;
    public string Url => Repo.IndexUrl;
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

public sealed class ExtensionRowVm : ViewModelBase
{
    public ExtensionRowVm(BridgeExtension extension, Func<ExtensionRowVm, SuwayomiClient.ExtensionAction, Task> act,
        Func<ExtensionRowVm, Task> openSettings, bool canConfigure)
    {
        Extension = extension;
        CanConfigure = canConfigure;
        InstallCommand = ReactiveCommand.CreateFromTask(() => act(this, SuwayomiClient.ExtensionAction.Install));
        UpdateCommand = ReactiveCommand.CreateFromTask(() => act(this, SuwayomiClient.ExtensionAction.Update));
        UninstallCommand = ReactiveCommand.CreateFromTask(() => act(this, SuwayomiClient.ExtensionAction.Uninstall));
        SettingsCommand = ReactiveCommand.CreateFromTask(() => openSettings(this));
    }

    public BridgeExtension Extension { get; }
    public string Name => Extension.Name;
    public string Details => $"{LanguageName(Extension.Language)} · {Extension.Version}";
    public bool IsNsfw => Extension.IsNsfw;
    public bool IsInstalled => Extension.IsInstalled;
    public bool IsNotInstalled => !Extension.IsInstalled;
    public bool HasUpdate => Extension.HasUpdate;
    public bool IsObsolete => Extension.IsObsolete;
    public bool CanConfigure { get; }

    private bool _isWorking;
    public bool IsWorking { get => _isWorking; set => this.RaiseAndSetIfChanged(ref _isWorking, value); }

    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> UninstallCommand { get; }
    public ReactiveCommand<Unit, Unit> SettingsCommand { get; }

    internal static string LanguageName(string code)
    {
        if (code is "all" or "") return "Multi-language";
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }
}
