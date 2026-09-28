using ReactiveUI;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels;

public class FillGapRowVm : ViewModelBase
{
    private readonly FillGapResult _result;

    public FillGapRowVm(FillGapResult result)
    {
        _result = result;
    }

    public int EpisodeNumber => _result.EpisodeNumber;

    /// <summary>Episode 0 is the batch-torrent sentinel row.</summary>
    public string EpisodeLabel => EpisodeNumber == 0 ? "BATCH" : $"EP {EpisodeNumber}";

    public bool HasMatch => _result.BestMatch != null;

    public bool CanDownload => _result.CanDownload;

    public bool IsDamaged => _result.DamagedPath != null;

    public string Title => _result.BestMatch?.Title
        ?? (_result.DamagedPath != null ? System.IO.Path.GetFileName(_result.DamagedPath) : null)
        ?? "No release found matching criteria.";

    /// <summary>What happens to a damaged copy if this row is downloaded.</summary>
    public string DamageNote => _result.RepairTorrentPath != null
        ? "Incomplete file in your library — repaired in place, only the missing parts are downloaded."
        : "Incomplete file in your library — it goes to the Recycle Bin and this release replaces it.";

    public string Size => _result.BestMatch?.SizeDisplay ?? "";

    public string Group => _result.BestMatch?.ReleaseGroup ?? "";

    public int Seeders => _result.BestMatch?.Seeders ?? 0;

    public bool IsSelected
    {
        get => _result.IsSelected;
        set
        {
            if (_result.IsSelected != value)
            {
                _result.IsSelected = value;
                this.RaisePropertyChanged();
            }
        }
    }

    public FillGapResult GetResult() => _result;
}
