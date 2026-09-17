using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>One selectable answer in a quiz round.</summary>
public class QuizOptionVm : ViewModelBase
{
    public string Name { get; }
    private readonly Action<QuizOptionVm> _onSelect;

    private bool _isSelected;   // the player's current (changeable) pick during guessing
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    private bool _isCorrect;    // revealed: this was the right answer
    public bool IsCorrect { get => _isCorrect; set => this.RaiseAndSetIfChanged(ref _isCorrect, value); }

    private bool _isWrong;      // revealed: this was the player's pick and it was wrong
    public bool IsWrong { get => _isWrong; set => this.RaiseAndSetIfChanged(ref _isWrong, value); }

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }

    public QuizOptionVm(string name, Action<QuizOptionVm> onSelect)
    {
        Name = name;
        _onSelect = onSelect;
        SelectCommand = ReactiveCommand.Create(() => _onSelect(this));
    }
}

/// <summary>
/// Timed "guess the anime from its opening" quiz (YouTube/AMQ style). Each round: the OP/ED
/// audio plays and a countdown runs — you can change your pick freely until time is up, then
/// the answer is revealed and the same clip replays as VIDEO to help you remember it.
/// Difficulty = how popular the anime is (AniList tier) + how long you get to guess.
/// </summary>
public class AnimeQuizViewModel : ViewModelBase
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "sentrychan-quiz");

    private readonly IAnimeQuizService _quiz;
    private QuizQuestion? _current;
    private QuizQuestion? _next;
    private QuizOptionVm? _selected;

    private readonly DispatcherTimer _timer;
    private double _remaining;
    private bool _countdownStarted;
    private CancellationTokenSource? _loadCts;   // cancels an in-flight round load on quit/advance

    public int[] RoundOptions { get; } = [5, 10, 15, 20];
    public int[] TimeOptions { get; } = [5, 10, 15, 30];

    // ── Setup options ───────────────────────────────────────────────
    private QuizDifficulty _difficulty = QuizDifficulty.Normal;
    public bool IsEasy      => _difficulty == QuizDifficulty.Easy;
    public bool IsNormal    => _difficulty == QuizDifficulty.Normal;
    public bool IsChallenge => _difficulty == QuizDifficulty.Challenge;
    private void SetDifficulty(QuizDifficulty d)
    {
        _difficulty = d;
        this.RaisePropertyChanged(nameof(IsEasy));
        this.RaisePropertyChanged(nameof(IsNormal));
        this.RaisePropertyChanged(nameof(IsChallenge));
    }

    private int _guessSeconds = 15;
    public int GuessSeconds
    {
        get => _guessSeconds;
        private set
        {
            this.RaiseAndSetIfChanged(ref _guessSeconds, value);
            this.RaisePropertyChanged(nameof(TimeLabel));
            this.RaisePropertyChanged(nameof(IsTime5)); this.RaisePropertyChanged(nameof(IsTime10));
            this.RaisePropertyChanged(nameof(IsTime15)); this.RaisePropertyChanged(nameof(IsTime30));
        }
    }
    public string TimeLabel => $"{GuessSeconds}s";
    public bool IsTime5 => _guessSeconds == 5;
    public bool IsTime10 => _guessSeconds == 10;
    public bool IsTime15 => _guessSeconds == 15;
    public bool IsTime30 => _guessSeconds == 30;

    private bool _progressive;
    public bool Progressive { get => _progressive; set => this.RaiseAndSetIfChanged(ref _progressive, value); }

    private bool _preload;
    public bool Preload { get => _preload; set => this.RaiseAndSetIfChanged(ref _preload, value); }

    // ── Phase ───────────────────────────────────────────────────────
    private enum Phase { Setup, Preparing, Playing, Summary }
    private Phase _phase = Phase.Setup;
    private Phase CurrentPhase
    {
        get => _phase;
        set { _phase = value; this.RaisePropertyChanged(nameof(IsSetup)); this.RaisePropertyChanged(nameof(IsPreparing)); this.RaisePropertyChanged(nameof(IsPlaying)); this.RaisePropertyChanged(nameof(IsSummary)); }
    }
    public bool IsSetup => _phase == Phase.Setup;
    public bool IsPreparing => _phase == Phase.Preparing;
    public bool IsPlaying => _phase == Phase.Playing;
    public bool IsSummary => _phase == Phase.Summary;

    private readonly System.Collections.Generic.Queue<QuizQuestion> _preloadQueue = new();
    private double _preloadProgress;
    public double PreloadProgress { get => _preloadProgress; private set => this.RaiseAndSetIfChanged(ref _preloadProgress, value); }
    private string _preloadStatus = string.Empty;
    public string PreloadStatus { get => _preloadStatus; private set => this.RaiseAndSetIfChanged(ref _preloadStatus, value); }
    public ReactiveCommand<Unit, Unit> CancelPreloadCommand { get; private set; } = null!;

    public ObservableCollection<QuizOptionVm> Options { get; } = [];

    private string _currentMediaUrl = string.Empty;
    /// <summary>The view watches this and (re)starts playback when it changes (audio, then video on reveal).</summary>
    public string CurrentMediaUrl { get => _currentMediaUrl; private set => this.RaiseAndSetIfChanged(ref _currentMediaUrl, value); }

    /// <summary>The snippet start offset (seconds) for this round — reused for audio and the reveal video.</summary>
    public int CurrentStartSeconds { get; private set; }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _loadStatus = "Loading round…";
    public string LoadStatus { get => _loadStatus; private set => this.RaiseAndSetIfChanged(ref _loadStatus, value); }

    // Guessing vs revealed.
    private bool _hasAnswered;
    public bool HasAnswered
    {
        get => _hasAnswered;
        private set { this.RaiseAndSetIfChanged(ref _hasAnswered, value); this.RaisePropertyChanged(nameof(IsGuessing)); }
    }
    public bool IsGuessing => IsPlaying && !HasAnswered;

    private bool _wasCorrect;
    public bool WasCorrect { get => _wasCorrect; private set { this.RaiseAndSetIfChanged(ref _wasCorrect, value); this.RaisePropertyChanged(nameof(ResultColor)); } }
    public string ResultColor => _wasCorrect ? "#4CAF50" : "#EF5350";

    private string _resultText = string.Empty;
    public string ResultText { get => _resultText; private set => this.RaiseAndSetIfChanged(ref _resultText, value); }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; private set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }

    // Countdown.
    private int _remainingSeconds;
    public int RemainingSeconds { get => _remainingSeconds; private set => this.RaiseAndSetIfChanged(ref _remainingSeconds, value); }
    private double _timeProgress;
    public double TimeProgress { get => _timeProgress; private set => this.RaiseAndSetIfChanged(ref _timeProgress, value); }

    // ── Session ─────────────────────────────────────────────────────
    private int _total;
    public int TotalQuestions { get => _total; private set { this.RaiseAndSetIfChanged(ref _total, value); this.RaisePropertyChanged(nameof(ProgressLine)); } }

    private int _number;
    public int QuestionNumber
    {
        get => _number;
        private set { this.RaiseAndSetIfChanged(ref _number, value); this.RaisePropertyChanged(nameof(ProgressLine)); this.RaisePropertyChanged(nameof(IsLastQuestion)); this.RaisePropertyChanged(nameof(NextLabel)); }
    }
    public string ProgressLine => $"Question {QuestionNumber} / {TotalQuestions}";
    public bool IsLastQuestion => QuestionNumber >= TotalQuestions;
    public string NextLabel => IsLastQuestion ? "See results  ▶" : "Next  ▶";

    private int _score;
    public int Score { get => _score; private set { this.RaiseAndSetIfChanged(ref _score, value); this.RaisePropertyChanged(nameof(ScoreLine)); this.RaisePropertyChanged(nameof(SummaryLine)); } }
    private int _streak;
    public int Streak { get => _streak; private set { this.RaiseAndSetIfChanged(ref _streak, value); this.RaisePropertyChanged(nameof(StreakLine)); } }
    private int _best;
    public int Best { get => _best; private set { this.RaiseAndSetIfChanged(ref _best, value); this.RaisePropertyChanged(nameof(StreakLine)); } }

    public string ScoreLine => $"Score {Score}";
    public string StreakLine => $"Streak {Streak}  ·  Best {Best}";
    public string SummaryLine => $"You scored {Score} / {TotalQuestions}";
    public string SummaryDetail => $"Best streak: {Best}   ·   {Percent}% correct";
    private int Percent => TotalQuestions > 0 ? (int)Math.Round(100.0 * Score / TotalQuestions) : 0;

    // ── Commands ────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> QuickStartCommand { get; }
    public ReactiveCommand<int, Unit> StartCommand { get; }
    public ReactiveCommand<string, Unit> SetTimeCommand { get; }
    public ReactiveCommand<Unit, Unit> RevealCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ReplayCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayAgainCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }
    public ReactiveCommand<Unit, Unit> SetEasyCommand { get; }
    public ReactiveCommand<Unit, Unit> SetNormalCommand { get; }
    public ReactiveCommand<Unit, Unit> SetChallengeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenBattleCommand { get; }

    /// <summary>Set by MainWindow to switch to the Battle Royale mode.</summary>
    public Action? OnOpenBattleRoyale { get; set; }

    /// <summary>Raised so the view can stop / replay playback.</summary>
    public event Action? StopPlaybackRequested;
    public event Action? ReplayRequested;
    public void StopPlayback() { _loadCts?.Cancel(); _timer.Stop(); StopPlaybackRequested?.Invoke(); }

    public AnimeQuizViewModel(IAnimeQuizService quiz)
    {
        _quiz = quiz;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;

        QuickStartCommand = ReactiveCommand.CreateFromTask(() => StartSessionAsync(10));
        StartCommand      = ReactiveCommand.CreateFromTask<int>(StartSessionAsync);
        SetTimeCommand    = ReactiveCommand.Create<string>(s => { if (int.TryParse(s, out var n)) GuessSeconds = n; });
        RevealCommand     = ReactiveCommand.Create(RevealNow);
        NextCommand       = ReactiveCommand.CreateFromTask(AdvanceAsync);
        ReplayCommand     = ReactiveCommand.Create(() => ReplayRequested?.Invoke());
        PlayAgainCommand  = ReactiveCommand.Create(ToSetup);
        QuitCommand       = ReactiveCommand.Create(ToSetup);
        SetEasyCommand      = ReactiveCommand.Create(() => SetDifficulty(QuizDifficulty.Easy));
        SetNormalCommand    = ReactiveCommand.Create(() => SetDifficulty(QuizDifficulty.Normal));
        SetChallengeCommand = ReactiveCommand.Create(() => SetDifficulty(QuizDifficulty.Challenge));
        OpenBattleCommand   = ReactiveCommand.Create(() => { StopPlayback(); OnOpenBattleRoyale?.Invoke(); });
        CancelPreloadCommand = ReactiveCommand.Create(ToSetup);

        SafeCleanTemp();
    }

    private void ToSetup()
    {
        StopPlayback();
        CurrentMediaUrl = string.Empty;
        _current = null; _next = null; _selected = null;
        CurrentPhase = Phase.Setup;
    }

    private async Task StartSessionAsync(int count)
    {
        TotalQuestions = Math.Clamp(count, 1, 50);
        Score = 0; Streak = 0; Best = 0;
        QuestionNumber = 0;
        _next = null;
        _preloadQueue.Clear();

        if (Preload)
        {
            CurrentPhase = Phase.Preparing;
            var ok = await PreloadAllAsync();
            if (!ok || CurrentPhase != Phase.Preparing) return; // cancelled / quit during preload
        }

        CurrentPhase = Phase.Playing;
        await LoadRoundAsync();
    }

    /// <summary>Downloads every round's audio + reveal video up front so the session has zero mid-round waits.</summary>
    private async Task<bool> PreloadAllAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        PreloadProgress = 0;
        try
        {
            for (var i = 0; i < TotalQuestions; i++)
            {
                if (ct.IsCancellationRequested) return false;
                PreloadStatus = $"Preloading round {i + 1} of {TotalQuestions}…";
                var q = await _quiz.GetQuestionAsync(_difficulty, i, TotalQuestions, Progressive, ct);
                if (ct.IsCancellationRequested) return false;
                if (q != null && q.Options.Count >= 2)
                {
                    q.LocalAudioPath = await DownloadAsync(q.AudioUrl, ".ogg", ct);
                    q.LocalVideoPath = await DownloadAsync(q.VideoUrl, ".webm", ct);
                    if (ct.IsCancellationRequested) return false;
                    _preloadQueue.Enqueue(q);
                }
                PreloadProgress = (double)(i + 1) / TotalQuestions * 100;
            }
            return _preloadQueue.Count > 0;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { StatusMessage = $"Preload failed: {ex.Message}"; return _preloadQueue.Count > 0; }
    }

    private async Task LoadRoundAsync()
    {
        _timer.Stop();
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _countdownStarted = false;
        IsLoading = true;
        LoadStatus = "Finding a round…";
        HasAnswered = false;
        ResultText = string.Empty;
        StatusMessage = string.Empty;
        _selected = null;
        Options.Clear();
        CurrentMediaUrl = string.Empty;
        QuestionNumber++;

        try
        {
            // Preloaded rounds are already fully on disk; otherwise fetch (or use the prefetched one).
            var fromPreload = _preloadQueue.Count > 0;
            QuizQuestion? q;
            if (fromPreload) q = _preloadQueue.Dequeue();
            else { q = _next; _next = null; q ??= await _quiz.GetQuestionAsync(_difficulty, QuestionNumber - 1, TotalQuestions, Progressive, ct); }
            if (ct.IsCancellationRequested) return;

            if (q == null || q.Options.Count < 2)
            {
                StatusMessage = "Couldn't load a round — check your connection and hit Next.";
                IsLoading = false;
                return;
            }

            _current = q;
            // Ensure the guess clip is on disk first, so playback starts instantly (no streaming buffer).
            if (q.LocalAudioPath == null)
            {
                LoadStatus = "Downloading clip…";
                var progress = new Progress<double>(p => LoadStatus = $"Downloading clip… {(int)(p * 100)}%");
                q.LocalAudioPath = await DownloadAsync(q.AudioUrl, ".ogg", ct, progress);
            }
            if (ct.IsCancellationRequested) return;

            CurrentStartSeconds = Random.Shared.Next(4, 22); // snippet start for the audio guess
            foreach (var o in q.Options) Options.Add(new QuizOptionVm(o, OnSelect));

            // Abort if the user left while we were loading — otherwise audio would start after quit.
            if (ct.IsCancellationRequested || !IsPlaying) return;

            // Kick off playback. The countdown does NOT start here — it starts when the audio
            // actually begins (OnMediaStarted, from the player's Playing event), so buffering
            // never eats your guess time. Keep "Loading…" up until then.
            LoadStatus = "Starting…";
            RemainingSeconds = GuessSeconds;
            TimeProgress = 100;
            CurrentMediaUrl = q.LocalAudioPath ?? q.AudioUrl;

            if (!fromPreload)
            {
                _ = DownloadRevealVideoAsync(q);   // warm the reveal video
                _ = PrefetchNextAsync();           // warm the next round's audio
            }
            _ = FailsafeStartAsync(QuestionNumber); // start anyway if the Playing event never comes
        }
        catch (OperationCanceledException) { /* left the round */ }
        catch (Exception ex) { StatusMessage = $"Couldn't load a round: {ex.Message}"; IsLoading = false; }
    }

    /// <summary>Called by the view when the audio actually starts — begins the countdown.</summary>
    public void OnMediaStarted()
    {
        if (!IsGuessing || _countdownStarted) return;
        _countdownStarted = true;
        IsLoading = false;
        _remaining = GuessSeconds;
        RemainingSeconds = GuessSeconds;
        TimeProgress = 100;
        _timer.Start();
    }

    private async Task FailsafeStartAsync(int round)
    {
        await Task.Delay(10000);
        if (round == QuestionNumber && !_countdownStarted && IsGuessing) OnMediaStarted();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining -= 0.1;
        if (_remaining <= 0)
        {
            _remaining = 0;
            RemainingSeconds = 0;
            TimeProgress = 0;
            Reveal();
            return;
        }
        RemainingSeconds = (int)Math.Ceiling(_remaining);
        TimeProgress = _remaining / Math.Max(1, GuessSeconds) * 100;
    }

    // Player picks / changes their answer during guessing — no reveal yet.
    private void OnSelect(QuizOptionVm chosen)
    {
        if (HasAnswered) return;
        _selected = chosen;
        foreach (var o in Options) o.IsSelected = ReferenceEquals(o, chosen);
    }

    private void RevealNow()
    {
        if (!HasAnswered) Reveal();
    }

    private void Reveal()
    {
        _timer.Stop();
        if (HasAnswered || _current == null) return;
        HasAnswered = true;

        var correct = _current.CorrectAnime;
        foreach (var o in Options)
        {
            o.IsSelected = false;
            if (o.Name == correct) o.IsCorrect = true;
            else if (ReferenceEquals(o, _selected)) o.IsWrong = true;
        }

        WasCorrect = _selected != null && _selected.Name == correct;
        var meta = string.IsNullOrEmpty(_current.ThemeLabel)
            ? "" : $" ({_current.ThemeLabel}{(_current.Year is { } yr ? $", {yr}" : "")})";
        if (WasCorrect) { Score++; Streak++; if (Streak > Best) Best = Streak; ResultText = $"✓ Correct — {correct}{meta}"; }
        else { Streak = 0; ResultText = _selected == null ? $"⏱ Time! It was {correct}{meta}" : $"✗ It was {correct}{meta}"; }

        // Replay the same clip as VIDEO to help remember it.
        CurrentMediaUrl = _current.LocalVideoPath ?? _current.VideoUrl;
    }

    private async Task AdvanceAsync()
    {
        if (IsLastQuestion)
        {
            StopPlayback();
            CurrentMediaUrl = string.Empty;
            CurrentPhase = Phase.Summary;
            return;
        }
        await LoadRoundAsync();
    }

    /// <summary>Answer/select by 1-based index (keyboard 1–4).</summary>
    public void AnswerByIndex(int oneBased)
    {
        try
        {
            if (!IsGuessing) return;
            var i = oneBased - 1;
            if (i >= 0 && i < Options.Count) OnSelect(Options[i]);
        }
        catch { /* never let a keypress crash the app */ }
    }

    // ── Prefetch / downloads ────────────────────────────────────────
    private async Task PrefetchNextAsync()
    {
        try
        {
            var q = await _quiz.GetQuestionAsync(_difficulty, QuestionNumber, TotalQuestions, Progressive).ConfigureAwait(false);
            if (q == null) return;
            q.LocalAudioPath = await DownloadAsync(q.AudioUrl, ".ogg").ConfigureAwait(false);
            _next = q;
        }
        catch { /* best-effort */ }
    }

    private static async Task DownloadRevealVideoAsync(QuizQuestion q)
    {
        try { q.LocalVideoPath = await DownloadAsync(q.VideoUrl, ".webm").ConfigureAwait(false); }
        catch { /* reveal will stream instead */ }
    }

    private static async Task<string?> DownloadAsync(string url, string ext, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            Directory.CreateDirectory(TempDir);
            var path = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ext);
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = File.Create(path);
            var buffer = new byte[65536];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
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
