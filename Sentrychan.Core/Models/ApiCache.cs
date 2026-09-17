namespace Sentrychan.Core.Models;

public class ApiCache
{
    public string CacheKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}