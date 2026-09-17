using ReactiveUI;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Interfaces;
using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

public class SeasonalAnimeVm : ViewModelBase
{
    private readonly ISeriesService _seriesService;
    private readonly MainWindowViewModel _mainWindowVm;
    private readonly AnimeResult _anime;

    public AnimeResult Anime => _anime;

    private bool _isInLibrary;
    public bool IsInLibrary
    {
        get => _isInLibrary;
        set => this.RaiseAndSetIfChanged(ref _isInLibrary, value);
    }

    public bool IsCensored { get; }

    private bool _isAdding;
    public bool IsAdding
    {
        get => _isAdding;
        set => this.RaiseAndSetIfChanged(ref _isAdding, value);
    }

    public ReactiveCommand<Unit, Unit> AddToLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDetailsCommand { get; }

    /// <summary>Set when the card represents a downloadable release (Latest page).</summary>
    public ReactiveCommand<Unit, Unit>? DownloadCommand { get; }
    public bool HasDownload => DownloadCommand != null;

    public SeasonalAnimeVm(AnimeResult anime, ISeriesService seriesService, MainWindowViewModel mainWindowVm, Action<AnimeResult> onOpenDetails, bool isInLibrary = false, bool isCensored = false, Func<Task>? onDownload = null, Func<Task>? onShowDetails = null)
    {
        _anime = anime;
        _seriesService = seriesService;
        _mainWindowVm = mainWindowVm;
        _isInLibrary = isInLibrary;
        IsCensored = isCensored;

        AddToLibraryCommand = ReactiveCommand.CreateFromTask(AddAsync,
            this.WhenAnyValue(x => x.IsInLibrary, x => x.IsAdding, (lib, adding) => !lib && !adding));

        // On Latest, Details opens the torrent-info dialog (onShowDetails);
        // on Seasonal it opens the MAL series detail (onOpenDetails).
        OpenDetailsCommand = onShowDetails != null
            ? ReactiveCommand.CreateFromTask(onShowDetails)
            : ReactiveCommand.Create(() => onOpenDetails(anime));

        if (onDownload != null)
            DownloadCommand = ReactiveCommand.CreateFromTask(onDownload);
    }

    private async Task AddAsync()
    {
        IsAdding = true;
        try
        {
            // Prompt for current episode
            int startEpisode = 0;
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var dialog = new Sentrychan.UI.Views.Dialogs.EpisodePickerDialog();
                var result = await dialog.ShowDialog<int>(desktop.MainWindow);
                if (result == -1) 
                {
                    IsAdding = false;
                    return; // Cancelled
                }
                startEpisode = result;
            }

            var series = new Sentrychan.Core.Models.Series
            {
                MalId = _anime.MalId,
                Title = _anime.Title,
                OriginalTitle = _anime.TitleJapanese ?? _anime.Title,
                LastEpisodeNumber = startEpisode,
                AddedAt = DateTime.UtcNow,
                AiringStatus = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(_anime.Status),
                TotalEpisodes = _anime.Episodes,
                IsCensored = IsCensored
            };

            var addedSeries = await _seriesService.AddAsync(series, _anime.LargeImageUrl);
            if (addedSeries != null)
            {
                IsInLibrary = true;
                _mainWindowVm.AddSeriesToLibrary(addedSeries);
            }
        }
        catch (Exception ex)
        {
            // Log or show error
            Console.WriteLine($"[SeasonalAnimeVm] Error adding anime: {ex.Message}");
        }
        finally
        {
            IsAdding = false;
        }
    }
}
