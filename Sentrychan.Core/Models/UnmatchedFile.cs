namespace Sentrychan.Core.Models;

/// <summary>
/// Represents a file that arrived in the download folder but couldn't be matched
/// to any library series. Persisted so it survives app restarts.
/// </summary>
public class UnmatchedFile
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime ArrivedAt { get; set; } = DateTime.UtcNow;
    public bool IsResolved { get; set; } = false;
}
