using ReactiveUI;
using System;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

/// <summary>One row in the Library page's "Airing Today" panel (SubsPlease schedule).</summary>
public class AiringTodayRowVm : ViewModelBase
{
    public string Title { get; }
    public string PosterUrl { get; }
    public string AirTime { get; }   // already local, from SubsPlease
    public bool InLibrary { get; }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public AiringTodayRowVm(string title, string posterUrl, string airTime, bool inLibrary, Action onOpen)
    {
        Title = title;
        PosterUrl = posterUrl;
        AirTime = string.IsNullOrWhiteSpace(airTime) ? "—" : airTime;
        InLibrary = inLibrary;
        OpenCommand = ReactiveCommand.Create(onOpen);
    }
}
