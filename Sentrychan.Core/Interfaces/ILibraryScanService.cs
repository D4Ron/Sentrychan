namespace Sentrychan.Core.Interfaces;

/// <summary>Disk state of one library series after a scan.</summary>
public record SeriesScanResult(
    int SeriesId,
    int MalId,
    string Title,
    string ExpectedFolder,
    bool FolderExists,
    List<int> EpisodesOnDisk,
    int LastEpisodeNumber)
{
    public int MaxEpisodeOnDisk => EpisodesOnDisk.Count > 0 ? EpisodesOnDisk.Max() : 0;

    /// <summary>Files on disk are AHEAD of the tracking cursor → cursor should advance.</summary>
    public bool HasProgressAdvance => MaxEpisodeOnDisk > LastEpisodeNumber;
}

/// <summary>Result of diffing the library folder against the series database.</summary>
public record LibraryScanReport(
    List<SeriesScanResult> Series,
    List<string> UnknownFolders)
{
    public List<SeriesScanResult> MissingFolders =>
        Series.Where(s => !s.FolderExists).ToList();

    public List<SeriesScanResult> ProgressAdvances =>
        Series.Where(s => s.HasProgressAdvance).ToList();

    public bool HasIssues => MissingFolders.Count > 0 || UnknownFolders.Count > 0;

    /// <summary>The library folder itself was gone and the scan created it again (its path), else null.</summary>
    public string? RecreatedLibraryFolder { get; init; }
}

/// <summary>
/// Reconciles the anime library FOLDER with the series DATABASE — the source of
/// truth for what the user did outside the app (deleted folders, added files,
/// dropped in whole shows manually).
///
/// Design rule: disk contents may RAISE the tracking cursor (new episodes found)
/// but never lower it — deleting watched files is normal cleanup, not "unwatch".
/// </summary>
public interface ILibraryScanService
{
    /// <summary>
    /// Walk the library folder and diff it against the series DB. Returns null when the library
    /// path isn't configured. A configured library folder that was deleted is created again
    /// (<see cref="LibraryScanReport.RecreatedLibraryFolder"/>); if that fails, the OS's reason is thrown.
    /// </summary>
    Task<LibraryScanReport?> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a series' show folder again, named by the current naming template. Returns its
    /// full path; throws with the reason when it can't (no library folder set, no permission…).
    /// </summary>
    Task<string> RecreateFolderAsync(int seriesId, CancellationToken ct = default);

    /// <summary>
    /// Persist the cursor advances found by a scan (LastEpisodeNumber := max
    /// episode on disk, only ever raising it). Returns how many series changed.
    /// </summary>
    Task<int> ApplyProgressAdvancesAsync(LibraryScanReport report, CancellationToken ct = default);
}
