using ReactiveUI;
using Sentrychan.Core.Events;
using System;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

public class DownloadConfirmVm : ViewModelBase
{
    public int SeriesId { get; }
    public int MalId { get; }
    public string SeriesTitle { get; }
    public string PosterPath { get; }
    public int EpisodeNumber { get; }
    public string ReleaseGroup { get; }
    public string? Resolution { get; }
    public string SizeDisplay { get; }
    public int Seeders { get; }
    public string DownloadLink { get; }
    public string RssTitle { get; }
    public string EpisodeLabel => $"Episode {EpisodeNumber}";
    public string MetaLine => $"{ReleaseGroup}  ·  {Resolution ?? "?"}  ·  {(string.IsNullOrEmpty(SizeDisplay) ? "?" : SizeDisplay)}  ·  {Seeders} seeders";

    public event Action<DownloadConfirmVm>? DownloadRequested;
    public event Action<DownloadConfirmVm, bool>? SkipRequested;    // bool = isPermanent
    public event Action<DownloadConfirmVm>? AutoDownloadRequested;  // enable auto + download

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipOnceCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipAlwaysCommand { get; }
    public ReactiveCommand<Unit, Unit> AutoDownloadCommand { get; }

    public DownloadConfirmVm(DownloadConfirmationEvent evt)
    {
        SeriesId      = evt.SeriesId;
        MalId         = evt.MalId;
        SeriesTitle   = evt.SeriesTitle;
        PosterPath    = evt.PosterPath;
        EpisodeNumber = evt.EpisodeNumber;
        ReleaseGroup  = evt.ReleaseGroup;
        Resolution    = evt.Resolution;
        SizeDisplay   = evt.SizeDisplay;
        Seeders       = evt.Seeders;
        DownloadLink  = evt.DownloadLink;
        RssTitle      = evt.RssTitle;

        DownloadCommand     = ReactiveCommand.Create(() => DownloadRequested?.Invoke(this));
        SkipOnceCommand     = ReactiveCommand.Create(() => SkipRequested?.Invoke(this, false));
        SkipAlwaysCommand   = ReactiveCommand.Create(() => SkipRequested?.Invoke(this, true));
        AutoDownloadCommand = ReactiveCommand.Create(() => AutoDownloadRequested?.Invoke(this));
    }
}
