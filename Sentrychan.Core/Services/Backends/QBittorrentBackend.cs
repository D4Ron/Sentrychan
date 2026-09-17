using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services.Backends;

public class QBittorrentBackend : IDownloadBackend, IAsyncDisposable
{
    private readonly ILogger<QBittorrentBackend> _logger;
    private HttpClient? _http;
    private string _baseUrl = "http://localhost:8080";
    private string _username = "admin";
    private string _password = "adminadmin";
    private bool _isLoggedIn;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    // Hashes we are actively tracking for completion detection
    private readonly HashSet<string> _trackedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _trackLock = new();

    public string Name => "qBittorrent";
    public string BackendType => "QBittorrent";

    public event Action<BackendCompletionEvent>? DownloadCompleted;

    public QBittorrentBackend(ILogger<QBittorrentBackend> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Configure connection parameters. Called by DownloadBackendRouter
    /// when settings are loaded or changed.
    /// </summary>
    public void Configure(string baseUrl, string username, string password)
    {
        _baseUrl  = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _isLoggedIn = false;
        _http?.Dispose();
        _http = CreateHttpClient();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureHttpClient();
            var response = await _http!.GetAsync($"{_baseUrl}/api/v2/app/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<string?> AddAsync(
        string magnetOrUrl,
        string savePath,
        string? expectedFileName,
        CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        try
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(magnetOrUrl), "urls"     },
                { new StringContent(savePath),    "savepath" },
                { new StringContent("true"),      "sequentialDownload" }
            };

            var response = await _http!.PostAsync(
                $"{_baseUrl}/api/v2/torrents/add", form, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[qBit] Add failed: {Status}", response.StatusCode);
                return null;
            }

            // qBittorrent returns "Ok." on success but doesn't return the hash
            // in the add response. We need to find the hash by querying the torrent list
            // shortly after adding (within a few seconds the torrent appears).
            await Task.Delay(2000, ct);
            var hash = await FindRecentlyAddedHashAsync(magnetOrUrl, ct);

            if (hash != null)
            {
                lock (_trackLock) _trackedHashes.Add(hash);
                EnsurePollingStarted();
                _logger.LogInformation("[qBit] Added torrent, hash: {Hash}", hash);
            }

            return hash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[qBit] Add exception");
            return null;
        }
    }

    public async Task<BackendStatus?> GetStatusAsync(string handle, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        try
        {
            var list = await GetTorrentListAsync(ct);
            var torrent = list.FirstOrDefault(t =>
                string.Equals(t.Hash, handle, StringComparison.OrdinalIgnoreCase));
            return torrent is null ? null : MapToBackendStatus(torrent);
        }
        catch { return null; }
    }

    public async Task<List<BackendStatus>> GetAllStatusAsync(CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        try
        {
            var list = await GetTorrentListAsync(ct);
            return list.Select(MapToBackendStatus).ToList();
        }
        catch { return []; }
    }

    public async Task PauseAsync(string handle, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", handle)]);
        await _http!.PostAsync($"{_baseUrl}/api/v2/torrents/pause", form, ct);
    }

    public async Task ResumeAsync(string handle, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", handle)]);
        await _http!.PostAsync($"{_baseUrl}/api/v2/torrents/resume", form, ct);
    }

    public async Task RemoveAsync(string handle, bool deleteData, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", handle),
            new KeyValuePair<string, string>("deleteFiles", deleteData.ToString().ToLower())
        ]);
        await _http!.PostAsync($"{_baseUrl}/api/v2/torrents/delete", form, ct);

        lock (_trackLock) _trackedHashes.Remove(handle);
    }

    // ── Private ───────────────────────────────────────────────────

    private void EnsureHttpClient()
    {
        _http ??= CreateHttpClient();
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer      = new CookieContainer(),
            UseCookies           = true,
            AllowAutoRedirect    = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        if (_isLoggedIn) return;
        EnsureHttpClient();
        try
        {
            var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", _username),
                new KeyValuePair<string, string>("password", _password)
            ]);
            var response = await _http!.PostAsync(
                $"{_baseUrl}/api/v2/auth/login", form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            _isLoggedIn = body.Trim() == "Ok.";
            if (!_isLoggedIn)
                _logger.LogWarning("[qBit] Login failed — check credentials in Settings");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[qBit] Login exception");
        }
    }

    private async Task<List<QBitTorrentInfo>> GetTorrentListAsync(CancellationToken ct)
    {
        var response = await _http!.GetAsync(
            $"{_baseUrl}/api/v2/torrents/info", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<QBitTorrentInfo>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private async Task<string?> FindRecentlyAddedHashAsync(
        string magnetOrUrl, CancellationToken ct)
    {
        // Extract the info hash from the magnet link if possible
        var magnetHashMatch = System.Text.RegularExpressions.Regex.Match(
            magnetOrUrl, @"urn:btih:([a-fA-F0-9]{40}|[a-zA-Z2-7]{32})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (magnetHashMatch.Success)
            return magnetHashMatch.Groups[1].Value.ToLowerInvariant();

        // Fallback: find the most recently added torrent in qBit's list
        var list = await GetTorrentListAsync(ct);
        var recent = list.OrderByDescending(t => t.AddedOn).FirstOrDefault();
        return recent?.Hash;
    }

    private static BackendStatus MapToBackendStatus(QBitTorrentInfo t)
    {
        var state = t.State switch
        {
            "downloading" or "metaDL" or "forcedDL" => BackendState.Downloading,
            "pausedDL"                               => BackendState.Paused,
            "uploading" or "forcedUP" or "stalledUP" => BackendState.Seeding,
            "pausedUP"                               => BackendState.Completed,
            "checkingUP" or "checkingDL" or "checkingResumeData" => BackendState.Checking,
            "queuedDL" or "queuedUP"                => BackendState.Queued,
            "error" or "missingFiles"               => BackendState.Error,
            _                                        => BackendState.Downloading
        };

        return new BackendStatus
        {
            Handle         = t.Hash,
            Name           = t.Name,
            ProgressPercent = t.Progress * 100.0,
            SpeedBps       = (long)t.Dlspeed,
            EtaSeconds     = t.Eta == int.MaxValue ? 0 : t.Eta,
            State          = state,
            TotalSizeBytes = t.Size,
            // ContentPath is the full path to the downloaded file/folder
            FilePath       = state == BackendState.Seeding ? t.ContentPath : null
        };
    }

    private void EnsurePollingStarted()
    {
        if (_pollTask is { IsCompleted: false }) return;
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[qBit] Polling started");
        // Track previous states to detect transitions
        var previousStates = new Dictionary<string, BackendState>(
            StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                if (!_isLoggedIn) await EnsureLoggedInAsync(ct);
                if (!_isLoggedIn) continue;

                HashSet<string> tracked;
                lock (_trackLock) tracked = new HashSet<string>(_trackedHashes,
                    StringComparer.OrdinalIgnoreCase);

                if (tracked.Count == 0) continue;

                var list = await GetTorrentListAsync(ct);
                foreach (var torrent in list)
                {
                    if (!tracked.Contains(torrent.Hash)) continue;

                    var status = MapToBackendStatus(torrent);
                    previousStates.TryGetValue(torrent.Hash, out var prev);

                    // Fire DownloadCompleted when transitioning INTO Seeding state
                    // (i.e. was Downloading, now Seeding = download finished)
                    if (status.State == BackendState.Seeding &&
                        prev != BackendState.Seeding &&
                        !string.IsNullOrEmpty(status.FilePath))
                    {
                        _logger.LogInformation(
                            "[qBit] Completion detected: {Hash} → {Path}",
                            torrent.Hash, status.FilePath);

                        DownloadCompleted?.Invoke(new BackendCompletionEvent
                        {
                            Handle        = torrent.Hash,
                            FilePath      = status.FilePath,
                            OriginalTitle = torrent.Name,
                            Backend       = BackendType
                        });

                        lock (_trackLock) _trackedHashes.Remove(torrent.Hash);
                    }

                    previousStates[torrent.Hash] = status.State;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[qBit] Poll loop error, continuing");
            }
        }

        _logger.LogInformation("[qBit] Polling stopped");
    }

    public async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask != null)
            try { await _pollTask; } catch { }
        _pollCts?.Dispose();
        _http?.Dispose();
    }

    // ── qBittorrent API response DTOs ─────────────────────────────

    private sealed class QBitTorrentInfo
    {
        [JsonPropertyName("hash")]         public string Hash        { get; set; } = string.Empty;
        [JsonPropertyName("name")]         public string Name        { get; set; } = string.Empty;
        [JsonPropertyName("state")]        public string State       { get; set; } = string.Empty;
        [JsonPropertyName("progress")]     public double Progress    { get; set; }
        [JsonPropertyName("dlspeed")]      public long   Dlspeed     { get; set; }
        [JsonPropertyName("eta")]          public int    Eta         { get; set; }
        [JsonPropertyName("size")]         public long   Size        { get; set; }
        [JsonPropertyName("added_on")]     public long   AddedOn     { get; set; }
        [JsonPropertyName("content_path")] public string ContentPath { get; set; } = string.Empty;
    }
}
