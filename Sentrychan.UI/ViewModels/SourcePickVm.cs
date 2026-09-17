using ReactiveUI;
using Sentrychan.Core.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Windows.Input;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// Represents a pending source-selection card shown when the RSS monitor found
/// multiple release groups for the same episode and no preferred source matched.
/// </summary>
public class SourcePickVm : ViewModelBase
{
    public int MalId { get; }
    public string SeriesTitle { get; }
    public int EpisodeNumber { get; }
    public IReadOnlyList<SourceOptionVm> Options { get; }

    public string HeaderText =>
        $"{SeriesTitle} — Episode {EpisodeNumber} from {Options.Count} sources";

    /// <summary>Raised when the user picks a source. Payload is the chosen candidate.</summary>
    public event Action<EpisodeSourceCandidate>? SourceChosen;

    /// <summary>Raised when the user dismisses the card without picking.</summary>
    public event Action? Dismissed;

    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    public SourcePickVm(MultiSourceEpisodeEvent evt)
    {
        MalId         = evt.MalId;
        SeriesTitle   = evt.SeriesTitle;
        EpisodeNumber = evt.EpisodeNumber;

        Options = evt.Sources
            .Select(s => new SourceOptionVm(s, () => SourceChosen?.Invoke(s)))
            .ToList();

        DismissCommand = ReactiveCommand.Create(() => Dismissed?.Invoke());
    }
}

public class SourceOptionVm : ViewModelBase
{
    public string ReleaseGroup { get; }
    public ReactiveCommand<Unit, Unit> PickCommand { get; }

    public SourceOptionVm(EpisodeSourceCandidate candidate, Action onPick)
    {
        ReleaseGroup = candidate.ReleaseGroup;
        PickCommand  = ReactiveCommand.Create(onPick);
    }
}
