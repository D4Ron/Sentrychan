using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>VM for a single unmatched file row in the resolver panel.</summary>
public class UnmatchedFileRowVm : ViewModelBase
{
    public int DbId { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public string SizeDisplay { get; }
    public DateTime ArrivedAt { get; }

    // Auto-suggested match
    private string _suggestedSeriesTitle = string.Empty;
    public string SuggestedSeriesTitle
    {
        get => _suggestedSeriesTitle;
        set
        {
            this.RaiseAndSetIfChanged(ref _suggestedSeriesTitle, value);
            this.RaisePropertyChanged(nameof(HasSuggestion));
            this.RaisePropertyChanged(nameof(ConfidenceBadge));
        }
    }
    public bool HasSuggestion => !string.IsNullOrEmpty(_suggestedSeriesTitle);

    private int _suggestedSeriesId;
    public int SuggestedSeriesId
    {
        get => _suggestedSeriesId;
        set => this.RaiseAndSetIfChanged(ref _suggestedSeriesId, value);
    }

    private int _suggestedEpisode;
    public int SuggestedEpisode
    {
        get => _suggestedEpisode;
        set => this.RaiseAndSetIfChanged(ref _suggestedEpisode, value);
    }

    private double _confidence;
    public double Confidence
    {
        get => _confidence;
        set
        {
            this.RaiseAndSetIfChanged(ref _confidence, value);
            this.RaisePropertyChanged(nameof(ConfidenceBadge));
            this.RaisePropertyChanged(nameof(ConfidenceColor));
        }
    }
    public string ConfidenceBadge => _confidence switch
    {
        >= 0.85 => "High",
        >= 0.55 => "Medium",
        _ => HasSuggestion ? "Low" : "No match"
    };
    public string ConfidenceColor => _confidence switch
    {
        >= 0.85 => "#4CAF50",
        >= 0.55 => "#FF9800",
        _ => "#9E9E9E"
    };

    // User-selected override (can differ from suggestion)
    private int _selectedSeriesId;
    public int SelectedSeriesId
    {
        get => _selectedSeriesId;
        set => this.RaiseAndSetIfChanged(ref _selectedSeriesId, value);
    }

    private string _selectedSeriesTitle = string.Empty;
    public string SelectedSeriesTitle
    {
        get => _selectedSeriesTitle;
        set => this.RaiseAndSetIfChanged(ref _selectedSeriesTitle, value);
    }

    private int _selectedEpisode;
    public int SelectedEpisode
    {
        get => _selectedEpisode;
        set => this.RaiseAndSetIfChanged(ref _selectedEpisode, value);
    }

    private bool _isResolved;
    public bool IsResolved
    {
        get => _isResolved;
        set => this.RaiseAndSetIfChanged(ref _isResolved, value);
    }

    public UnmatchedFileRowVm(UnmatchedFile model)
    {
        DbId      = model.Id;
        FileName  = model.FileName;
        FilePath  = model.FilePath;
        ArrivedAt = model.ArrivedAt;
        SizeDisplay = model.FileSizeBytes switch
        {
            >= 1_073_741_824 => $"{model.FileSizeBytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{model.FileSizeBytes / 1_048_576.0:F0} MB",
            _                => $"{model.FileSizeBytes / 1024:F0} KB"
        };
    }
}

public class UnmatchedResolverViewModel : ViewModelBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEpisodeNormalizer _normalizer;
    private readonly IFileMovementPipeline _pipeline;
    private readonly ITitleResolverService? _titleResolver;

    public ObservableCollection<UnmatchedFileRowVm> Files { get; } = new();
    public ObservableCollection<Series> LibrarySeries { get; } = new();

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<UnmatchedFileRowVm, Unit> AssignCommand { get; }
    public ReactiveCommand<Unit, Unit> AssignAllHighConfidenceCommand { get; }
    public ReactiveCommand<UnmatchedFileRowVm, Unit> DeleteFileCommand { get; }
    public ReactiveCommand<UnmatchedFileRowVm, Unit> SkipCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanFolderCommand { get; }
    public ReactiveCommand<UnmatchedFileRowVm, Unit> AddAsSeriesCommand { get; }
    public ReactiveCommand<UnmatchedFileRowVm, Unit> OpenFileCommand { get; }
    public ReactiveCommand<UnmatchedFileRowVm, Unit> RevealCommand { get; }

    // Design-time
    public UnmatchedResolverViewModel()
    {
        _dbFactory = null!; _normalizer = null!; _pipeline = null!;
        LoadCommand = ReactiveCommand.Create(() => { });
        AssignCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(_ => { });
        AssignAllHighConfidenceCommand = ReactiveCommand.Create(() => { });
        DeleteFileCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(_ => { });
        SkipCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(_ => { });
        RescanFolderCommand = ReactiveCommand.Create(() => { });
        AddAsSeriesCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(_ => { });
        OpenFileCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(_ => { });
        RevealCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(_ => { });
    }

    public UnmatchedResolverViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        IEpisodeNormalizer normalizer,
        IFileMovementPipeline pipeline)
    {
        _dbFactory = dbFactory;
        _normalizer = normalizer;
        _pipeline = pipeline;
        _titleResolver = App.Services?.GetService(typeof(ITitleResolverService)) as ITitleResolverService;

        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AssignCommand = ReactiveCommand.CreateFromTask<UnmatchedFileRowVm>(AssignAsync);
        AssignAllHighConfidenceCommand = ReactiveCommand.CreateFromTask(AssignAllHighConfidenceAsync);
        DeleteFileCommand = ReactiveCommand.CreateFromTask<UnmatchedFileRowVm>(DeleteFileAsync);
        SkipCommand = ReactiveCommand.CreateFromTask<UnmatchedFileRowVm>(SkipAsync);
        RescanFolderCommand = ReactiveCommand.CreateFromTask(RescanFolderAsync);
        AddAsSeriesCommand = ReactiveCommand.CreateFromTask<UnmatchedFileRowVm>(AddAsSeriesAsync);
        OpenFileCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(OpenFile);
        RevealCommand = ReactiveCommand.Create<UnmatchedFileRowVm>(Reveal);

        _ = LoadAsync(CancellationToken.None);
    }

    /// <summary>Opens the unmatched file with the OS default app for its type.</summary>
    private void OpenFile(UnmatchedFileRowVm row)
    {
        try
        {
            if (string.IsNullOrEmpty(row.FilePath) || !File.Exists(row.FilePath)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.FilePath) { UseShellExecute = true });
        }
        catch { /* OS refused to open it */ }
    }

    /// <summary>Reveals the unmatched file in the system file explorer (selected).</summary>
    private void Reveal(UnmatchedFileRowVm row)
    {
        try
        {
            if (string.IsNullOrEmpty(row.FilePath)) return;
            if (File.Exists(row.FilePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe", Arguments = $"/select,\"{row.FilePath}\"", UseShellExecute = true
                });
            else
            {
                var dir = Path.GetDirectoryName(row.FilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch { /* Explorer refused */ }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Load all library series for the picker dropdown
            var allSeries = await db.Series.AsNoTracking().OrderBy(s => s.Title).ToListAsync(ct);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LibrarySeries.Clear();
                foreach (var s in allSeries) LibrarySeries.Add(s);
            });

            // Load unresolved unmatched files
            var unmatched = await db.UnmatchedFiles
                .AsNoTracking()
                .Where(u => !u.IsResolved)
                .OrderByDescending(u => u.ArrivedAt)
                .ToListAsync(ct);

            // Also scan the _Unmatched folder for files not yet in DB
            var libraryPath = (await db.AppConfigs
                .FirstOrDefaultAsync(c => c.Key == "LibraryPath", ct))?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(libraryPath))
            {
                var unmatchedDir = Path.Combine(libraryPath, "_Unmatched");
                if (Directory.Exists(unmatchedDir))
                {
                    var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { ".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov" };
                    var onDisk = Directory.GetFiles(unmatchedDir)
                        .Where(f => videoExts.Contains(Path.GetExtension(f)))
                        .ToList();

                    foreach (var diskFile in onDisk)
                    {
                        var alreadyTracked = unmatched.Any(u =>
                            string.Equals(u.FilePath, diskFile, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyTracked)
                        {
                            var info = new FileInfo(diskFile);
                            var newEntry = new UnmatchedFile
                            {
                                FileName      = info.Name,
                                FilePath      = diskFile,
                                FileSizeBytes = info.Length,
                                ArrivedAt     = info.CreationTimeUtc,
                                IsResolved    = false
                            };
                            db.UnmatchedFiles.Add(newEntry);
                            unmatched.Add(newEntry);
                        }
                    }
                    if (db.ChangeTracker.HasChanges())
                        await db.SaveChangesAsync(ct);
                }
            }

            // Build row VMs and compute suggestions
            var rows = unmatched.Select(u => new UnmatchedFileRowVm(u)).ToList();
            foreach (var row in rows)
                ComputeSuggestion(row, allSeries);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Files.Clear();
                foreach (var r in rows) Files.Add(r);
                StatusMessage = Files.Count == 0
                    ? "No unmatched files — you're all caught up!"
                    : $"{Files.Count} file(s) need attention";
            });

            // Auto-assign resolver-confirmed matches (0.95 = exact MAL-id hit
            // against a library series). No human review needed — sweep them
            // straight into the library so this tab only holds real questions.
            var autoRows = rows
                .Where(r => r.Confidence >= 0.95 && r.SelectedSeriesId > 0 && r.SelectedEpisode > 0)
                .ToList();
            if (autoRows.Count > 0)
            {
                foreach (var row in autoRows)
                    await AssignAsync(row, ct);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusMessage = $"Auto-assigned {autoRows.Count} file(s); " +
                        (Files.Count == 0 ? "all caught up!" : $"{Files.Count} still need attention"));
            }
        }
        finally { IsLoading = false; }
    }

    private void ComputeSuggestion(UnmatchedFileRowVm row, List<Series> allSeries)
    {
        if (allSeries.Count == 0) return;

        var fileNameNoExt = Path.GetFileNameWithoutExtension(row.FileName);
        var episodeNum = _normalizer.ExtractEpisodeNumber(fileNameNoExt) ?? 0;

        // Resolver-first: canonical MAL id from the offline synonym database.
        if (_titleResolver?.IsReady == true)
        {
            var parsed = _titleResolver.ParseRelease(row.FileName);
            if (parsed.Episode.HasValue) episodeNum = parsed.Episode.Value;

            var resolved = _titleResolver.ResolveRelease(row.FileName);
            if (resolved is { MalId: > 0 })
            {
                var idMatch = allSeries.FirstOrDefault(s => s.MalId == resolved.MalId);
                if (idMatch != null)
                {
                    row.SuggestedSeriesId    = idMatch.Id;
                    row.SuggestedSeriesTitle = idMatch.Title;
                    row.SuggestedEpisode     = episodeNum;
                    row.Confidence           = 0.95;

                    row.SelectedSeriesId    = idMatch.Id;
                    row.SelectedSeriesTitle = idMatch.Title;
                    row.SelectedEpisode     = episodeNum;
                    return;
                }
            }
        }

        Series? bestMatch = null;
        double bestScore = 0;

        foreach (var series in allSeries)
        {
            var titles = new List<string> { series.Title };
            if (!string.IsNullOrEmpty(series.OriginalTitle)) titles.Add(series.OriginalTitle);
            if (!string.IsNullOrEmpty(series.AlternativeTitlesJson))
            {
                try
                {
                    var alts = System.Text.Json.JsonSerializer
                        .Deserialize<List<string>>(series.AlternativeTitlesJson);
                    if (alts != null) titles.AddRange(alts);
                }
                catch { }
            }

            double titleScore = 0;
            foreach (var t in titles)
            {
                double s = ScoreMatch(fileNameNoExt, t);
                if (s > titleScore) titleScore = s;
            }

            if (titleScore > bestScore)
            {
                bestScore  = titleScore;
                bestMatch  = series;
            }
        }

        if (bestMatch != null && bestScore > 0.35)
        {
            row.SuggestedSeriesId    = bestMatch.Id;
            row.SuggestedSeriesTitle = bestMatch.Title;
            row.SuggestedEpisode     = episodeNum;
            row.Confidence           = bestScore;

            // Pre-populate selection with the suggestion
            row.SelectedSeriesId    = bestMatch.Id;
            row.SelectedSeriesTitle = bestMatch.Title;
            row.SelectedEpisode     = episodeNum;
        }
    }

    private double ScoreMatch(string fileName, string seriesTitle)
    {
        // Normalize both
        var fileNorm   = _normalizer.NormalizeTitle(fileName);
        var titleNorm  = _normalizer.NormalizeTitle(seriesTitle);

        if (string.IsNullOrEmpty(titleNorm) || string.IsNullOrEmpty(fileNorm)) return 0;

        // Substring bonus
        double score = fileNorm.Contains(titleNorm) ? 0.6 : 0;

        // Keyword overlap
        var fileWords  = fileNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();
        var titleWords = titleNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();

        if (titleWords.Count > 0)
        {
            double overlap = (double)fileWords.Intersect(titleWords).Count() / titleWords.Count;
            score = Math.Max(score, overlap);
        }

        return score;
    }

    private async Task AssignAsync(UnmatchedFileRowVm row, CancellationToken ct)
    {
        if (row.SelectedSeriesId == 0 || row.SelectedEpisode <= 0)
        {
            StatusMessage = "Select a series and episode number first.";
            return;
        }

        if (!File.Exists(row.FilePath))
        {
            StatusMessage = $"File not found: {row.FileName}";
            return;
        }

        try
        {
            // Trigger the pipeline's filesystem path — it will match by series+episode
            // We inject the file directly by first creating the DownloadJob record
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series.FindAsync(new object[] { row.SelectedSeriesId }, ct);
            if (series == null)
            {
                StatusMessage = "Selected series not found in library.";
                return;
            }

            // Create an explicit job so FinalizeAsync knows where to move the file
            var job = new DownloadJob
            {
                SeriesId      = series.Id,
                EpisodeNumber = row.SelectedEpisode,
                DownloadLink  = row.FilePath,
                RssTitle      = row.FileName,
                ExpectedFileName = row.FileName,
                Backend       = DownloadBackend.FDM,
                Status        = JobStatus.Downloading,
                CreatedAt     = DateTime.UtcNow,
                Series        = series
            };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync(ct);

            // Mark the unmatched record as resolved
            var unmatched = await db.UnmatchedFiles.FindAsync(new object[] { row.DbId }, ct);
            if (unmatched != null) { unmatched.IsResolved = true; await db.SaveChangesAsync(ct); }

            // Hand off to the pipeline via the filesystem event path
            await _pipeline.OnFileSystemEventAsync(row.FilePath, ct);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Files.Remove(row);
                StatusMessage = $"Assigned: {row.FileName} → {series.Title} Ep {row.SelectedEpisode}";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Assignment failed: {ex.Message}";
        }
    }

    private async Task AssignAllHighConfidenceAsync(CancellationToken ct)
    {
        var highConf = Files.Where(f => f.Confidence >= 0.85 && !f.IsResolved).ToList();
        if (highConf.Count == 0)
        {
            StatusMessage = "No high-confidence matches to auto-assign.";
            return;
        }

        StatusMessage = $"Assigning {highConf.Count} high-confidence files...";
        foreach (var row in highConf)
            await AssignAsync(row, ct);

        StatusMessage = $"Auto-assigned {highConf.Count} files.";
    }

    private async Task DeleteFileAsync(UnmatchedFileRowVm row, CancellationToken ct)
    {
        try
        {
            if (File.Exists(row.FilePath)) File.Delete(row.FilePath);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var record = await db.UnmatchedFiles.FindAsync(new object[] { row.DbId }, ct);
            if (record != null) { record.IsResolved = true; await db.SaveChangesAsync(ct); }

            Avalonia.Threading.Dispatcher.UIThread.Post(() => Files.Remove(row));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    private async Task SkipAsync(UnmatchedFileRowVm row, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await db.UnmatchedFiles.FindAsync(new object[] { row.DbId }, ct);
        if (record != null) { record.IsResolved = true; await db.SaveChangesAsync(ct); }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Files.Remove(row));
    }

    private async Task RescanFolderAsync(CancellationToken ct)
    {
        StatusMessage = "Rescanning _Unmatched folder...";
        await LoadAsync(ct);
    }

    /// <summary>
    /// "Add as new series": opens the Add Series search pre-filled with the title
    /// parsed from this file, and after the user adds the series, recomputes
    /// suggestions and auto-assigns every unmatched file that now matches it —
    /// organizing a whole show out of _Unmatched in one action.
    /// </summary>
    private async Task AddAsSeriesAsync(UnmatchedFileRowVm row, CancellationToken ct)
    {
        var api       = App.Services?.GetService(typeof(IAnimeApiService)) as IAnimeApiService;
        var seriesSvc = App.Services?.GetService(typeof(ISeriesService)) as ISeriesService;
        if (api == null || seriesSvc == null) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;

        var vm = new AddSeriesViewModel(api, seriesSvc)
        {
            // Pre-fill triggers the debounced auto-search
            SearchQuery = CleanTitleForSearch(row.FileName)
        };
        var dialog = new Views.Dialogs.AddSeriesDialog { DataContext = vm };
        await dialog.ShowDialog(desktop.MainWindow);

        if (vm.AddedSeries == null) return;

        // Reflect the new series in the main library UI immediately
        (App.Services?.GetService(typeof(MainWindowViewModel)) as MainWindowViewModel)
            ?.AddSeriesToLibrary(vm.AddedSeries);

        StatusMessage = $"Added {vm.AddedSeries.Title} — matching unmatched files...";

        // Recompute suggestions against the expanded library, then auto-assign
        // everything that now points at the new series.
        await LoadAsync(ct);

        var matches = Files
            .Where(f => f.SuggestedSeriesId == vm.AddedSeries.Id
                     && f.Confidence >= 0.55
                     && f.SelectedEpisode > 0)
            .ToList();

        foreach (var m in matches)
            await AssignAsync(m, ct);

        StatusMessage = matches.Count > 0
            ? $"{vm.AddedSeries.Title}: organized {matches.Count} file(s) from _Unmatched"
            : $"{vm.AddedSeries.Title} added — no confident file matches; assign manually.";
    }

    private string CleanTitleForSearch(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var normalized = _normalizer.NormalizeTitle(name);

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Drop leading release-group remnants and trailing episode/quality tokens
        var knownGroups = new[] { "subsplease", "erai", "commie", "horriblesubs", "nyaa", "asw", "judas" };
        if (words.Count > 0 && knownGroups.Any(g => words[0].StartsWith(g)))
            words.RemoveAt(0);

        words.RemoveAll(w => w is "1080p" or "720p" or "480p" or "x265" or "x264" or "hevc" or "aac");

        for (int i = words.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(words[i], out _))
            {
                while (words.Count > i) words.RemoveAt(i);
                break;
            }
        }

        return string.Join(" ", words).Trim();
    }
}
