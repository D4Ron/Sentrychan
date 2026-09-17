using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// "Favourite OP" bracket (uwufufu-style worldcup). A set of random openings is preloaded,
/// then presented two at a time; you pick the one you like more and it advances, until a single
/// champion remains. Single-player for now — designed to become a multi-player vote later.
/// </summary>
public class BattleRoyaleViewModel : ViewModelBase
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "sentrychan-br");

    private readonly IAnimeQuizService _quiz;
    private List<QuizTheme> _contestants = [];
    private List<QuizTheme> _winners = [];
    private int _matchIndex;

    public int[] SizeOptions { get; } = [8, 16, 32];

    private enum Phase { Setup, Loading, Bracket, Champion }
    private Phase _phase = Phase.Setup;
    private Phase CurrentPhase
    {
        get => _phase;
        set
        {
            _phase = value;
            this.RaisePropertyChanged(nameof(IsSetup));
            this.RaisePropertyChanged(nameof(IsLoadingPhase));
            this.RaisePropertyChanged(nameof(IsBracket));
            this.RaisePropertyChanged(nameof(IsChampion));
        }
    }
    public bool IsSetup => _phase == Phase.Setup;
    public bool IsLoadingPhase => _phase == Phase.Loading;
    public bool IsBracket => _phase == Phase.Bracket;
    public bool IsChampion => _phase == Phase.Champion;

    private string _loadStatus = string.Empty;
    public string LoadStatus { get => _loadStatus; private set => this.RaiseAndSetIfChanged(ref _loadStatus, value); }

    private QuizTheme? _left;
    public QuizTheme? Left { get => _left; private set { this.RaiseAndSetIfChanged(ref _left, value); this.RaisePropertyChanged(nameof(LeftName)); } }
    private QuizTheme? _right;
    public QuizTheme? Right { get => _right; private set { this.RaiseAndSetIfChanged(ref _right, value); this.RaisePropertyChanged(nameof(RightName)); } }
    public string LeftName => _left?.Display ?? "";
    public string RightName => _right?.Display ?? "";

    private QuizTheme? _champion;
    public QuizTheme? Champion { get => _champion; private set { this.RaiseAndSetIfChanged(ref _champion, value); this.RaisePropertyChanged(nameof(ChampionName)); } }
    public string ChampionName => _champion?.Display ?? "";

    private string _roundName = string.Empty;
    public string RoundName { get => _roundName; private set => this.RaiseAndSetIfChanged(ref _roundName, value); }
    private string _matchLabel = string.Empty;
    public string MatchLabel { get => _matchLabel; private set => this.RaiseAndSetIfChanged(ref _matchLabel, value); }

    private string _nowPlaying = string.Empty;
    public string NowPlaying { get => _nowPlaying; private set => this.RaiseAndSetIfChanged(ref _nowPlaying, value); }

    private string _currentMediaUrl = string.Empty;
    public string CurrentMediaUrl { get => _currentMediaUrl; private set => this.RaiseAndSetIfChanged(ref _currentMediaUrl, value); }

    public ReactiveCommand<int, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> QuickStartCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayLeftCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayRightCommand { get; }
    public ReactiveCommand<Unit, Unit> PickLeftCommand { get; }
    public ReactiveCommand<Unit, Unit> PickRightCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayAgainCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQuizCommand { get; }

    /// <summary>Set by MainWindow to switch to the timed-quiz mode.</summary>
    public Action? OnOpenQuiz { get; set; }

    public event Action? StopPlaybackRequested;
    public void StopPlayback() => StopPlaybackRequested?.Invoke();

    public BattleRoyaleViewModel(IAnimeQuizService quiz)
    {
        _quiz = quiz;
        StartCommand      = ReactiveCommand.CreateFromTask<int>(StartAsync);
        QuickStartCommand = ReactiveCommand.CreateFromTask(() => StartAsync(16));
        PlayLeftCommand   = ReactiveCommand.Create(() => Play(_left));
        PlayRightCommand  = ReactiveCommand.Create(() => Play(_right));
        PickLeftCommand   = ReactiveCommand.Create(() => Pick(_left));
        PickRightCommand  = ReactiveCommand.Create(() => Pick(_right));
        PlayAgainCommand  = ReactiveCommand.Create(ToSetup);
        QuitCommand       = ReactiveCommand.Create(ToSetup);
        OpenQuizCommand   = ReactiveCommand.Create(() => { StopPlayback(); OnOpenQuiz?.Invoke(); });
        SafeCleanTemp();
    }

    private void ToSetup()
    {
        StopPlayback();
        CurrentMediaUrl = string.Empty;
        _contestants = []; _winners = []; Left = null; Right = null; Champion = null;
        CurrentPhase = Phase.Setup;
    }

    private async Task StartAsync(int size)
    {
        CurrentPhase = Phase.Loading;
        LoadStatus = "Fetching openings…";
        try
        {
            var themes = await _quiz.GetRandomThemesAsync(size);
            if (themes.Count < 4) { LoadStatus = "Couldn't fetch enough openings — try again."; return; }

            // Use the largest power of two we actually got (min 4), so the bracket is clean.
            var n = LargestPowerOfTwo(Math.Min(themes.Count, size));
            _contestants = themes.Take(n).ToList();

            // Preload audio so switching between clips is instant.
            for (var i = 0; i < _contestants.Count; i++)
            {
                LoadStatus = $"Preloading openings…  {i + 1} / {_contestants.Count}";
                _contestants[i].LocalAudioPath = await DownloadAsync(_contestants[i].AudioUrl);
            }

            _winners = [];
            _matchIndex = 0;
            CurrentPhase = Phase.Bracket;
            StartRound();
        }
        catch (Exception ex) { LoadStatus = $"Couldn't start: {ex.Message}"; }
    }

    private void StartRound()
    {
        RoundName = RoundNameFor(_contestants.Count);
        _matchIndex = 0;
        ShowMatch();
    }

    private void ShowMatch()
    {
        if (_matchIndex * 2 + 1 >= _contestants.Count)
        {
            // Round finished → winners become the next round (or crown the champion).
            if (_winners.Count == 1) { Champion = _winners[0]; StopPlayback(); CurrentMediaUrl = string.Empty; CurrentPhase = Phase.Champion; return; }
            _contestants = _winners;
            _winners = [];
            StartRound();
            return;
        }

        Left = _contestants[_matchIndex * 2];
        Right = _contestants[_matchIndex * 2 + 1];
        MatchLabel = $"Match {_matchIndex + 1} of {_contestants.Count / 2}";
        NowPlaying = string.Empty;
        Play(_left); // auto-play the first so there's immediately something to hear
    }

    private void Pick(QuizTheme? winner)
    {
        if (winner == null || !IsBracket) return;
        _winners.Add(winner);
        _matchIndex++;
        ShowMatch();
    }

    private void Play(QuizTheme? theme)
    {
        if (theme == null) return;
        NowPlaying = $"▶ {theme.Display}";
        // Bump so the same clip replays even if it was already the current URL.
        CurrentMediaUrl = string.Empty;
        CurrentMediaUrl = theme.LocalAudioPath ?? theme.AudioUrl;
    }

    private static int LargestPowerOfTwo(int n)
    {
        var p = 4;
        while (p * 2 <= n) p *= 2;
        return Math.Min(p, n < 4 ? n : p);
    }

    private static string RoundNameFor(int count) => count switch
    {
        2 => "Final",
        4 => "Semifinals",
        8 => "Quarterfinals",
        _ => $"Round of {count}"
    };

    private static async Task<string?> DownloadAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            Directory.CreateDirectory(TempDir);
            var path = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".ogg");
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using (var fs = File.Create(path))
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            return path;
        }
        catch { return null; }
    }

    private static void SafeCleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempDir))
                try { File.Delete(f); } catch { /* in use */ }
        }
        catch { /* best-effort */ }
    }
}
