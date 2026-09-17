using System;

namespace Sentrychan.Core.Models;

public class WatchPartySession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int? HostedSeriesId { get; set; }
    public string HostedSeriesTitle { get; set; } = string.Empty;
    public int HostedEpisodeNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    
    // Navigation
    public Series? HostedSeries { get; set; }
}
