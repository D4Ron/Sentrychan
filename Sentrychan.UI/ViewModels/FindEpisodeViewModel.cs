using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>What the Find Episode dialog hands back: the episode and the release the user picked.</summary>
public record FindEpisodePick(int Episode, ReleaseResult Release);

/// <summary>
/// Look for one episode, and choose the release yourself: narrow to a release group, or
/// type your own search when a release is named in a way the automatic search misses
/// (a v2, an odd season label, a different romanisation).
/// </summary>
public class FindEpisodeViewModel : ViewModelBase
{
    public const string AnyGroup = "Any group";

    private readonly IReleaseProviders _releases;
    private readonly Series _series;
    private readonly List<string> _titles;
    private readonly int _season;

    public string SeriesTitle => _series.Title;

    private decimal? _episode;
    public decimal? Episode { get => _episode; set => this.RaiseAndSetIfChanged(ref _episode, value); }

    public ObservableCollection<string> Groups { get; } = new() { AnyGroup };

    private string _selectedGroup = AnyGroup;
    public string SelectedGroup { get => _selectedGroup; set => this.RaiseAndSetIfChanged(ref _selectedGroup, value ?? AnyGroup); }

    private string? _customQuery;
    public string? CustomQuery { get => _customQuery; set => this.RaiseAndSetIfChanged(ref _customQuery, value); }

    public ObservableCollection<ReleaseRowVm> Results { get; } = new();

    private ReleaseRowVm? _selectedResult;
    public ReleaseRowVm? SelectedResult { get => _selectedResult; set => this.RaiseAndSetIfChanged(ref _selectedResult, value); }

    private bool _isSearching;
    public bool IsSearching { get => _isSearching; set => this.RaiseAndSetIfChanged(ref _isSearching, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, FindEpisodePick?> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public FindEpisodeViewModel(Series series, IReleaseProviders releases, IEnumerable<string> preferredGroups)
    {
        _series   = series;
        _releases = releases;
        _season   = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);
        _titles   = TitlesOf(series);
        Episode   = series.LastEpisodeNumber + 1;

        foreach (var g in preferredGroups.Where(g => !string.IsNullOrWhiteSpace(g)))
            AddGroup(g.Trim());

        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync,
            this.WhenAnyValue(x => x.IsSearching, x => x.Episode, (busy, ep) => !busy && ep is > 0));

        DownloadCommand = ReactiveCommand.Create(
            () => SelectedResult == null ? null : new FindEpisodePick((int)(Episode ?? 0), SelectedResult.Release),
            this.WhenAnyValue(x => x.SelectedResult).Select(r => r != null));

        CancelCommand = ReactiveCommand.Create(() => { });
    }

    private async Task SearchAsync()
    {
        var episode = (int)(Episode ?? 0);
        var group   = SelectedGroup == AnyGroup ? null : SelectedGroup;
        var custom  = string.IsNullOrWhiteSpace(CustomQuery) ? null : CustomQuery.Trim();

        IsSearching = true;
        Results.Clear();
        SelectedResult = null;
        Status = $"Searching for episode {episode}{(group != null ? $" from {group}" : "")}…";

        try
        {
            List<ReleaseResult> found = [];

            // Custom text is searched once, as typed. Otherwise try each of the show's titles
            // in turn — groups often release under a different title than MAL's romaji.
            var titles = custom != null ? _titles.Take(1) : _titles;
            foreach (var title in titles)
            {
                found = await _releases.FindEpisodeAsync(new EpisodeQuery(SeasonSearch.StripSeason(title), episode)
                {
                    Season      = _season,
                    Group       = group,
                    CustomQuery = custom,
                }, CancellationToken.None);
                if (found.Count > 0) break;
            }

            foreach (var r in found) Results.Add(new ReleaseRowVm(r));
            foreach (var g in found.Select(r => r.ReleaseGroup)) AddGroup(g);
            SelectedResult = Results.FirstOrDefault();

            Status = found.Count switch
            {
                0 when group != null => $"Nothing from {group} for episode {episode}. Try \"{AnyGroup}\" or your own search text.",
                0 => $"No release found for episode {episode}. Try your own search text — e.g. how the group names it.",
                1 => "1 release found.",
                _ => $"{found.Count} releases found — best match first.",
            };
        }
        catch (Exception ex)
        {
            Status = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void AddGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || group == "Unknown") return;
        if (Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase))) return;
        Groups.Add(group);
    }

    private static List<string> TitlesOf(Series series)
    {
        var titles = new List<string> { series.Title };
        if (!string.IsNullOrEmpty(series.OriginalTitle) && series.OriginalTitle != series.Title)
            titles.Add(series.OriginalTitle);
        if (!string.IsNullOrEmpty(series.AlternativeTitlesJson))
        {
            try
            {
                var alts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(series.AlternativeTitlesJson);
                if (alts != null)
                    titles.AddRange(alts.Where(t => !string.IsNullOrWhiteSpace(t) && !titles.Contains(t)));
            }
            catch { /* malformed alt titles — ignore */ }
        }
        return titles;
    }
}

public class ReleaseRowVm
{
    private static readonly Regex Version = new(@"(?:\d|\s)v(\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ReleaseRowVm(ReleaseResult release)
    {
        Release = release;
        var v = Version.Match(release.Title);
        VersionLabel = v.Success && v.Groups[1].Value != "1" ? $"v{v.Groups[1].Value}" : null;
    }

    public ReleaseResult Release { get; }
    public string Title => Release.Title;
    public string Group => Release.ReleaseGroup;
    public string Resolution => Release.Resolution ?? "";
    public string Size => Release.SizeDisplay;
    public string Seeders => $"▲ {Release.Seeders}";
    public bool IsBatch => Release.IsBatch;

    /// <summary>"v2" etc. for a re-release; null for a first release.</summary>
    public string? VersionLabel { get; }
    public bool HasVersion => VersionLabel != null;
}
