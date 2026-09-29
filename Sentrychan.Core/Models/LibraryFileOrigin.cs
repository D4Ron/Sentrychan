namespace Sentrychan.Core.Models;

/// <summary>
/// The name a library file arrived with, kept once the app renames it (the naming template,
/// or Tidy library). Episode repair finds a file's torrent — and puts the file back for the
/// torrent to check — by that original name, and the file locator still recognises an episode
/// by it when the new name carries a different number (absolute episode 13 → S02E01).
/// </summary>
public class LibraryFileOrigin
{
    public int Id { get; set; }

    /// <summary>Where the file is now (absolute).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>File name (with extension) as it was downloaded.</summary>
    public string OriginalName { get; set; } = string.Empty;

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
