using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Lightweight anime info popup (from Airing Today / Latest). Adds directly to
/// the library — no Add Series search dialog — using the already-known MAL id.
/// </summary>
public class AnimePreviewViewModel : ViewModelBase
{
    private readonly ISeriesService _seriesService;
    private readonly MainWindowViewModel _mainVm;

    public int MalId { get; }
    public string Title { get; }
    public string PosterUrl { get; }
    public string MetaLine { get; }
    public string PageUrl { get; }
    public bool HasPage => !string.IsNullOrEmpty(PageUrl);

    private string _synopsis = "Loading details…";
    public string Synopsis { get => _synopsis; set => this.RaiseAndSetIfChanged(ref _synopsis, value); }

    private bool _inLibrary;
    public bool InLibrary { get => _inLibrary; set => this.RaiseAndSetIfChanged(ref _inLibrary, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private readonly string _status;
    private readonly int? _episodes;

    public event Action? CloseRequested;

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPageCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public AnimePreviewViewModel(
        ISeriesService seriesService, MainWindowViewModel mainVm,
        int malId, string title, string posterUrl, int? episodes,
        string status, int? year, bool inLibrary, string pageUrl)
    {
        _seriesService = seriesService;
        _mainVm = mainVm;
        MalId = malId;
        Title = title;
        PosterUrl = posterUrl;
        _episodes = episodes;
        _status = status;
        _inLibrary = inLibrary;
        PageUrl = pageUrl;

        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(status)) parts.Add(status);
        if (episodes is > 0) parts.Add($"{episodes} eps");
        if (year is > 0) parts.Add(year.ToString()!);
        MetaLine = string.Join("  ·  ", parts);

        AddCommand = ReactiveCommand.CreateFromTask(AddAsync);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
        OpenPageCommand = ReactiveCommand.Create(() =>
        {
            if (string.IsNullOrEmpty(PageUrl)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PageUrl) { UseShellExecute = true }); }
            catch { }
        });
    }

    public void SetSynopsis(string? s) =>
        Synopsis = string.IsNullOrWhiteSpace(s) ? "No description available." : s.Trim();

    private async Task AddAsync()
    {
        if (MalId <= 0 || InLibrary) return;
        IsBusy = true;
        try
        {
            var series = new Series
            {
                MalId = MalId,
                Title = Title,
                OriginalTitle = Title,
                LastEpisodeNumber = -1,
                AddedAt = DateTime.UtcNow,
                AiringStatus = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(_status),
                TotalEpisodes = _episodes
            };
            var added = await _seriesService.AddAsync(series, PosterUrl);
            if (added != null)
            {
                InLibrary = true;
                _mainVm.AddSeriesToLibrary(added);
                _mainVm.ShowToast("Added to library", Title);
            }
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _mainVm.ShowToast("Add failed", ex.Message);
        }
        finally { IsBusy = false; }
    }
}
