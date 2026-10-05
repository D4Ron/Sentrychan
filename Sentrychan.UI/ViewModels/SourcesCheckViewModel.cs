using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Sentrychan.Core;
using Sentrychan.Core.Sources;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>One row of the sources check.</summary>
public sealed class SourcesCheckRowVm(SourcesCheckItem item)
{
    public string Title { get; } = item.Title;
    public string Detail { get; } = item.Detail;
    public string Mark { get; } = item.State switch
    {
        CheckState.Ok => "✓", CheckState.Warning => "!", CheckState.Problem => "✕", _ => "•",
    };
    public bool IsOk => item.State == CheckState.Ok;
    public bool IsWarning => item.State == CheckState.Warning;
    public bool IsProblem => item.State == CheckState.Problem;
}

/// <summary>
/// The sources check, shown after every import and from Settings → Sources: whether what was
/// imported actually took — each pack loaded, each feed answering, search and manga sources working.
/// </summary>
public sealed class SourcesCheckViewModel : ViewModelBase
{
    private static readonly HttpClient Http = CreateHttp();

    public SourcesCheckViewModel()
    {
        var idle = this.WhenAnyValue(x => x.IsRunning, r => !r);
        TestCommand = ReactiveCommand.CreateFromTask(() => RunAsync(live: true), idle);
        RestartCommand = ReactiveCommand.Create(() => App.Restart?.Invoke());
    }

    public ObservableCollection<SourcesCheckRowVm> Rows { get; } = [];
    public ReactiveCommand<Unit, Unit> TestCommand { get; }
    public ReactiveCommand<Unit, Unit> RestartCommand { get; }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => this.RaiseAndSetIfChanged(ref _isRunning, value); }

    private string? _importSummary;
    /// <summary>What the import just did, above the check. Null when the check was opened on its own.</summary>
    public string? ImportSummary { get => _importSummary; set => this.RaiseAndSetIfChanged(ref _importSummary, value); }

    private string _verdict = "";
    public string Verdict { get => _verdict; private set => this.RaiseAndSetIfChanged(ref _verdict, value); }

    private bool _allGood;
    public bool AllGood { get => _allGood; private set => this.RaiseAndSetIfChanged(ref _allGood, value); }

    private bool _needsRestart;
    public bool NeedsRestart { get => _needsRestart; private set => this.RaiseAndSetIfChanged(ref _needsRestart, value); }
    public bool CanRestart => App.Restart != null;

    public string FolderPath => AppPaths.Sources;

    /// <param name="live">Also try every feed, the search and each manga source (takes a few seconds).</param>
    public async Task RunAsync(bool live)
    {
        var checker = App.Services?.GetService<SourcesChecker>();
        if (checker == null) { Verdict = "The check isn't available in this build."; return; }
        IsRunning = true;
        Verdict = live ? "Testing your sources…" : "Looking at your sources…";
        try
        {
            Show(await checker.RunAsync(Http, live), live);
        }
        catch (Exception ex) { Verdict = "The check failed: " + ex.Message; }
        finally { IsRunning = false; }
    }

    public void Show(System.Collections.Generic.IReadOnlyList<SourcesCheckItem> items, bool live)
    {
        Rows.Clear();
        foreach (var i in items) Rows.Add(new SourcesCheckRowVm(i));
        var problems = items.Count(i => i.State == CheckState.Problem);
        var warnings = items.Count(i => i.State == CheckState.Warning);
        AllGood = problems == 0 && warnings == 0;
        NeedsRestart = items.Any(i => i.Detail.Contains("restart Sentrychan", StringComparison.Ordinal));
        Verdict = problems > 0 ? $"{problems} problem{(problems == 1 ? "" : "s")} found — see the red lines below."
            : warnings > 0 ? $"Working, with {warnings} thing{(warnings == 1 ? "" : "s")} to look at."
            : live ? "Everything works." : "Everything is in place. Press Test to try each one.";
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{BuildInfo.AppName.Replace(' ', '-')}/1.0");
        return http;
    }
}
