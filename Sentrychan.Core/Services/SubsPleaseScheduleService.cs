using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using System.Text.Json;

namespace Sentrychan.Core.Services;

/// <summary>
/// Fetches SubsPlease's schedule API (https://subsplease.org/api/?f=schedule&amp;tz=...).
/// It returns each weekday's shows with release times already converted to the
/// requested IANA timezone, plus poster URLs — the real release calendar.
/// </summary>
public class SubsPleaseScheduleService : IAiringScheduleService
{
    private const string BaseUrl = "https://subsplease.org";
    private readonly HttpClient _http;
    private readonly ILogger<SubsPleaseScheduleService> _logger;

    // Small in-memory cache — the schedule barely changes within a session.
    private List<AiringScheduleEntry>? _cache;
    private DateTime _cacheDay = DateTime.MinValue;
    private DateTime _cachedAt = DateTime.MinValue;

    public SubsPleaseScheduleService(ILogger<SubsPleaseScheduleService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/1.0");
    }

    public async Task<List<AiringScheduleEntry>> GetTodayAsync(CancellationToken ct = default)
    {
        var today = DateTime.Now.DayOfWeek;
        if (_cache != null && _cacheDay == DateTime.Today
            && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30))
            return _cache;

        try
        {
            var tz = ResolveIanaTimeZone();
            var url = $"{BaseUrl}/api/?f=schedule&tz={Uri.EscapeDataString(tz)}";
            var json = await _http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            var result = new List<AiringScheduleEntry>();

            if (doc.RootElement.TryGetProperty("schedule", out var schedule)
                && schedule.TryGetProperty(today.ToString(), out var dayArray)
                && dayArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dayArray.EnumerateArray())
                {
                    var title = GetStr(item, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    var img = GetStr(item, "image_url");
                    var poster = string.IsNullOrEmpty(img)
                        ? string.Empty
                        : (img.StartsWith("http") ? img : BaseUrl + img);

                    var page = GetStr(item, "page");
                    var pageUrl = string.IsNullOrEmpty(page) ? BaseUrl : $"{BaseUrl}/shows/{page}/";

                    result.Add(new AiringScheduleEntry(title, poster, GetStr(item, "time"), pageUrl));
                }
            }

            _cache = result;
            _cacheDay = DateTime.Today;
            _cachedAt = DateTime.UtcNow;
            _logger.LogInformation("[Schedule] SubsPlease: {Count} shows releasing {Day}", result.Count, today);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Schedule] SubsPlease schedule fetch failed");
            return _cache ?? [];
        }
    }

    private static string GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>SubsPlease wants an IANA tz id; Windows uses its own ids, so convert.</summary>
    private static string ResolveIanaTimeZone()
    {
        try
        {
            if (TimeZoneInfo.Local.HasIanaId) return TimeZoneInfo.Local.Id;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana))
                return iana;
        }
        catch { /* fall through */ }
        return "Etc/UTC";
    }
}
