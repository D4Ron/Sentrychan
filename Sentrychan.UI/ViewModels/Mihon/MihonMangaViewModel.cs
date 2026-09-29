using System;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>
/// The Mihon-style manga (or novel) section: Library, Updates, History, Browse and the
/// download queue as tabs. Each tab loads when it's shown, so opening the section is instant.
/// </summary>
public sealed class MihonMangaViewModel : ViewModelBase
{
    public MihonMangaViewModel(
        MangaLibraryService library, IMangaService mangaService, IMangaSourceRegistry sources,
        IMangaDownloadService downloads, ISecretModeService? secretMode, IConfigService? config, bool novels,
        Action<Manga> openTitle, Action<Manga, int> openReader, Func<Task<int>>? checkForUpdates)
    {
        IsNovels = novels;
        Library = new MihonLibraryViewModel(library, mangaService, secretMode, config, novels, openTitle);

        var feed = new MangaFeedHost
        {
            Library = library, Downloads = downloads, SecretMode = secretMode,
            OpenTitle = openTitle, OpenReader = openReader, CheckForUpdates = checkForUpdates,
        };
        Updates = new MangaUpdatesViewModel(feed, novels);
        History = new MangaHistoryViewModel(feed, novels);

        BrowseHost = new MangaBrowseHost
        {
            MangaService = mangaService, Sources = sources, SecretMode = secretMode, Config = config,
            Added = () => _ = Library.LoadAsync(),
        };
        Browse = new MangaBrowseViewModel(BrowseHost, novels);
        Queue = new MangaQueueViewModel(downloads);
    }

    public bool IsNovels { get; }
    public MihonLibraryViewModel Library { get; }
    public MangaUpdatesViewModel Updates { get; }
    public MangaHistoryViewModel History { get; }
    public MangaBrowseViewModel Browse { get; }
    public MangaQueueViewModel Queue { get; }

    /// <summary>Browse's link to the window (the view sets the preview dialog on it).</summary>
    public MangaBrowseHost BrowseHost { get; }

    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set { this.RaiseAndSetIfChanged(ref _selectedTab, value); _ = LoadTabAsync(); }
    }

    /// <summary>Loads (or refreshes) whatever tab is showing — called when the section opens.</summary>
    public Task LoadTabAsync() => SelectedTab switch
    {
        1 => Updates.LoadAsync(),
        2 => History.LoadAsync(),
        3 => Browse.LoadAsync(),
        4 => RefreshQueue(),
        _ => Library.LoadAsync(),
    };

    private Task RefreshQueue()
    {
        Queue.Refresh();
        return Task.CompletedTask;
    }
}
