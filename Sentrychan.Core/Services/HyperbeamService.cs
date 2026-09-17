using Microsoft.Extensions.Logging;
using Sentrychan.Core.Config;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sentrychan.Core.Services;

/// <summary>
/// Hyperbeam-backed watch party service.
///
/// Flow:
///   1. Host clicks "Create Room" → POST /vm to Hyperbeam → embed_url returned.
///   2. Room row saved to Supabase watch_rooms table.
///   3. Friends see the room via GetActiveRoomsAsync (Supabase RLS).
///   4. Anyone clicks "Join" → system browser opens embed_url.
///   5. Host clicks "Close" → room marked inactive in Supabase.
/// </summary>
public class HyperbeamService : IHyperbeamService
{
    private readonly ILogger<HyperbeamService> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAccountService _account;
    private Supabase.Client? _supabase;
    private bool _supabaseReady;

    public bool IsConfigured => HyperbeamConfig.IsConfigured && SupabaseConfig.IsConfigured;

    public HyperbeamService(
        ILogger<HyperbeamService> logger,
        IHttpClientFactory httpFactory,
        IAccountService account)
    {
        _logger = logger;
        _httpFactory = httpFactory;
        _account = account;
    }

    private async Task EnsureSupabaseAsync()
    {
        if (_supabaseReady) return;
        if (!SupabaseConfig.IsConfigured) return;

        try
        {
            var options = new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = false,
                AutoRefreshToken = false,
            };
            _supabase = new Supabase.Client(SupabaseConfig.Url, SupabaseConfig.AnonKey, options);
            await _supabase.InitializeAsync();
            _supabaseReady = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hyperbeam] Failed to init Supabase client");
        }
    }

    public async Task<WatchRoomEntry?> CreateRoomAsync(
        string seriesTitle, int malId, int? episode,
        string? startUrl = null, CancellationToken ct = default)
    {
        if (!_account.IsSignedIn || _account.CurrentUser == null)
        {
            _logger.LogWarning("[Hyperbeam] Must be signed in to create a room");
            return null;
        }

        // 1. Create Hyperbeam VM session
        string sessionId;
        string embedUrl;

        if (HyperbeamConfig.IsConfigured)
        {
            var result = await CreateHyperbeamSessionAsync(startUrl, ct);
            if (result == null) return null;
            (sessionId, embedUrl) = result.Value;
        }
        else
        {
            // Placeholder when API key not configured — still saves the room
            sessionId = $"local-{Guid.NewGuid():N}";
            embedUrl = $"https://hyperbeam.com/app?placeholder={sessionId}";
            _logger.LogWarning("[Hyperbeam] API key not configured, using placeholder URL");
        }

        // 2. Save room to Supabase
        await EnsureSupabaseAsync();
        if (_supabase == null) return null;

        try
        {
            var room = new WatchRoomEntry
            {
                HostId = _account.CurrentUser.Id,
                HostName = _account.CurrentUser.DisplayName ?? _account.CurrentUser.Email,
                SeriesTitle = seriesTitle,
                MalId = malId,
                Episode = episode,
                HbSessionId = sessionId,
                EmbedUrl = embedUrl,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };

            var response = await _supabase.From<WatchRoomEntry>().Insert(room);
            var saved = response.Models?.FirstOrDefault();
            _logger.LogInformation("[Hyperbeam] Room created: {Title} → {Url}", seriesTitle, embedUrl);
            return saved ?? room;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hyperbeam] Failed to save room to Supabase");
            return null;
        }
    }

    public async Task<List<WatchRoomEntry>> GetActiveRoomsAsync(CancellationToken ct = default)
    {
        if (!_account.IsSignedIn) return [];

        await EnsureSupabaseAsync();
        if (_supabase == null) return [];

        try
        {
            // RLS on watch_rooms filters to self + accepted friends where is_active = true
            var response = await _supabase.From<WatchRoomEntry>()
                .Where(r => r.IsActive == true)
                .Order(r => r.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Get();
            return response.Models ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hyperbeam] GetActiveRoomsAsync failed");
            return [];
        }
    }

    public async Task CloseRoomAsync(string roomId, CancellationToken ct = default)
    {
        await EnsureSupabaseAsync();
        if (_supabase == null) return;

        try
        {
            await _supabase.From<WatchRoomEntry>()
                .Where(r => r.Id == roomId)
                .Set(r => r.IsActive!, false)
                .Update();
            _logger.LogInformation("[Hyperbeam] Room {Id} closed", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hyperbeam] CloseRoomAsync failed for {Id}", roomId);
        }
    }

    // ── Hyperbeam REST API ────────────────────────────────────────

    private async Task<(string SessionId, string EmbedUrl)?> CreateHyperbeamSessionAsync(
        string? startUrl, CancellationToken ct)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", HyperbeamConfig.ApiKey);

            var body = new Dictionary<string, object>
            {
                ["timeout"] = new { absolute = 7200, inactive = 1800, warning = 60 }
            };
            if (!string.IsNullOrEmpty(startUrl))
                body["start_url"] = startUrl;

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await client.PostAsync($"{HyperbeamConfig.ApiBaseUrl}/vm", content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("[Hyperbeam] API returned {Code}: {Body}", resp.StatusCode, err);
                return null;
            }

            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var sessionId = doc.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
            var embedUrl = doc.RootElement.GetProperty("embed_url").GetString() ?? string.Empty;

            _logger.LogInformation("[Hyperbeam] Session created: {Id}", sessionId);
            return (sessionId, embedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hyperbeam] CreateHyperbeamSessionAsync failed");
            return null;
        }
    }
}
