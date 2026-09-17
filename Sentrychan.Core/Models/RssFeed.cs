// Models/RssFeed.cs — full replacement
namespace Sentrychan.Core.Models;

public enum FeedType { Priority, Secondary }

public class RssFeed
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public FeedType FeedType { get; set; } = FeedType.Priority;
    public bool IsEnabled { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; } = 0;
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? PreferredQuality { get; set; }
}