using Microsoft.Extensions.Logging;
using Sentrychan.Core.Config;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Supabase.Gotrue;
using static Supabase.Gotrue.Constants;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;
using Supabase.Realtime.Presence;
using RtChannel = Supabase.Realtime.RealtimeChannel;
using PresenceEventType = Supabase.Realtime.Interfaces.IRealtimePresence.EventType;
using ListenType = Supabase.Realtime.PostgresChanges.PostgresChangesOptions.ListenType;

namespace Sentrychan.Core.Services;

/// <summary>
/// Supabase-backed account service.
///
/// Session lifetime flow:
///   1. App start → TryRestoreSessionAsync() loads DPAPI-encrypted session from disk.
///   2. User clicks Sign In → SignInWithGoogleAsync() opens browser, waits for callback.
///   3. Callback received → exchange code for session, persist, fire AuthStateChanged.
///   4. Sign Out → clear session from memory and disk.
///
/// Library sync uses Supabase PostgREST upsert on the user_library table.
/// </summary>
public class SupabaseAccountService : IAccountService, IAsyncDisposable
{
    private readonly ILogger<SupabaseAccountService> _logger;
    private Supabase.Client? _supabase;
    private AccountUser? _currentUser;
    private bool _initialized;

    // ── Session persistence ────────────────────────────────────────
    private static readonly string _sessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sentrychan", "Auth");
    private static readonly string _sessionFile = Path.Combine(_sessionDir, "session.dat");

    // ── Realtime channels (auto-attached on sign-in, torn down on sign-out) ─
    private readonly List<RtChannel> _subscribedChannels = new();

    public bool IsSignedIn => _currentUser != null;
    public AccountUser? CurrentUser => _currentUser;
    public event Action<AccountUser?>? AuthStateChanged;

    public event Action<FriendshipEntry>?     FriendRequestReceived;
    public event Action<FriendshipEntry>?     FriendRequestAccepted;
    public event Action<ActivityEntry>?       ActivityReceived;
    public event Action<WatchRoomEntry>?      WatchRoomWentLive;
    public event Action<SeriesAiringEntry>?   AiringAlertReceived;

    public SupabaseAccountService(ILogger<SupabaseAccountService> logger)
    {
        _logger = logger;
    }

    // ── Initialization ─────────────────────────────────────────────

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (!SupabaseConfig.IsConfigured)
        {
            _logger.LogInformation("[Account] Supabase not configured — account features disabled");
            return;
        }

        try
        {
            var options = new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = true,
                AutoRefreshToken    = true,
            };
            _supabase = new Supabase.Client(SupabaseConfig.Url, SupabaseConfig.AnonKey, options);
            await _supabase.InitializeAsync();
            _logger.LogInformation("[Account] Supabase client initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] Failed to initialize Supabase client");
            _supabase = null;
        }
    }

    // ── OAuth sign-in ──────────────────────────────────────────────

    public async Task<AccountUser?> SignInWithGoogleAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        if (_supabase == null) return null;

        try
        {
            // 1. Ask Supabase for the Google OAuth URL with PKCE
            var signInOptions = new SignInOptions
            {
                FlowType   = OAuthFlowType.PKCE,   // Supabase.Gotrue.Constants.OAuthFlowType
                RedirectTo = SupabaseConfig.OAuthCallbackUri,
            };

            var oauthResponse = await _supabase.Auth.SignIn(
                Supabase.Gotrue.Constants.Provider.Google, signInOptions);

            if (oauthResponse?.Uri == null)
            {
                _logger.LogWarning("[Account] Supabase returned no OAuth URI");
                return null;
            }

            // 2. Start the local callback server, but DON'T await it yet — it blocks until the
            //    redirect arrives, and that only happens once the browser has opened (step 3).
            //    Awaiting here deadlocked sign-in: the browser never opened and it timed out.
            var callbackTask = RunCallbackServerAsync(SupabaseConfig.OAuthCallbackPort, ct);

            // 3. Open the browser to the Google consent screen.
            Process.Start(new ProcessStartInfo
            {
                FileName        = oauthResponse.Uri.ToString(),
                UseShellExecute = true
            });

            // 4. Now wait for Google → Supabase → localhost to deliver the auth code.
            var (authCode, receivedState) = await callbackTask;

            if (string.IsNullOrEmpty(authCode))
            {
                _logger.LogWarning("[Account] OAuth callback timed out or was cancelled");
                return null;
            }

            // 5. Exchange the code for a session using PKCE
            var session = await _supabase.Auth.ExchangeCodeForSession(
                oauthResponse.PKCEVerifier ?? string.Empty, authCode);

            if (session?.User == null)
            {
                _logger.LogWarning("[Account] Code exchange returned no session");
                return null;
            }

            return await ApplySessionAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] SignInWithGoogleAsync failed");
            return null;
        }
    }

    // ── Session restore ────────────────────────────────────────────

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        if (_supabase == null) return false;

        try
        {
            var stored = LoadStoredSession();
            if (stored == null) return false;

            // Let Supabase refresh the access token if needed
            var session = await _supabase.Auth.SetSession(
                stored.AccessToken, stored.RefreshToken, false);

            if (session?.User == null) return false;

            await ApplySessionAsync(session, ct);
            _logger.LogInformation("[Account] Session restored for {Email}", _currentUser?.Email);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Account] Failed to restore session — will require re-login");
            DeleteStoredSession();
            return false;
        }
    }

    // ── Sign out ───────────────────────────────────────────────────

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        // Realtime channels can't survive a session change — tear them down first.
        TeardownRealtime();

        try
        {
            if (_supabase != null)
                await _supabase.Auth.SignOut();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Account] SignOut call failed, clearing locally anyway");
        }

        _currentUser = null;
        DeleteStoredSession();
        AuthStateChanged?.Invoke(null);
        _logger.LogInformation("[Account] Signed out");
    }

    // ── Library sync ───────────────────────────────────────────────

    public async Task SyncLibraryToCloudAsync(
        IEnumerable<Series> series, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return;

        try
        {
            var entries = series.Select(s => new CloudSeriesEntry
            {
                UserId         = _currentUser.Id,
                MalId          = s.MalId,
                Title          = s.Title,
                LastEpisode    = s.LastEpisodeNumber,
                TotalEpisodes  = s.TotalEpisodes ?? 0,
                AiringStatus   = s.AiringStatus,
                MonitoringState = s.MonitoringState.ToString(),
                UpdatedAt      = DateTime.UtcNow,
            }).ToList();

            if (entries.Count == 0) return;

            await _supabase.From<CloudSeriesEntry>().Upsert(entries);
            _logger.LogInformation("[Account] Synced {Count} series to cloud", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] SyncLibraryToCloudAsync failed");
        }
    }

    public async Task<List<CloudSeriesEntry>> PullLibraryFromCloudAsync(
        CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null)
            return [];

        try
        {
            var response = await _supabase.From<CloudSeriesEntry>()
                .Where(e => e.UserId == _currentUser.Id)
                .Get();
            return response.Models ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] PullLibraryFromCloudAsync failed");
            return [];
        }
    }

    // ── Friend system ──────────────────────────────────────────────

    public async Task<string?> SendFriendRequestAsync(
        string addresseeEmail, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return null;
        if (string.IsNullOrWhiteSpace(addresseeEmail)) return null;

        var email = addresseeEmail.Trim().ToLowerInvariant();
        if (string.Equals(email, _currentUser.Email, StringComparison.OrdinalIgnoreCase))
            return "You can't send a friend request to yourself.";

        try
        {
            // Resolve email → user id via public.user_profiles (populated by trigger).
            var profileResp = await _supabase.From<UserProfileEntry>()
                .Filter("email", Supabase.Postgrest.Constants.Operator.ILike, email)
                .Limit(1)
                .Get();

            var target = profileResp.Models?.FirstOrDefault();
            if (target?.Id is null)
                return $"No Sentrychan user found for {addresseeEmail}. They need to sign in once first.";

            return await SendFriendRequestByIdAsync(target.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] SendFriendRequestAsync failed");
            return null;
        }
    }

    public async Task<string?> SendFriendRequestByIdAsync(
        string addresseeUserId, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return null;
        if (string.Equals(addresseeUserId, _currentUser.Id, StringComparison.OrdinalIgnoreCase))
            return "You can't send a friend request to yourself.";

        try
        {
            var entry = new FriendshipEntry
            {
                RequesterId = _currentUser.Id,
                AddresseeId = addresseeUserId,
                Status      = "pending",
                CreatedAt   = DateTime.UtcNow,
            };
            await _supabase.From<FriendshipEntry>().Insert(entry);
            _logger.LogInformation("[Friends] Sent request to {Id}", addresseeUserId);
            return "pending";
        }
        catch (Supabase.Postgrest.Exceptions.PostgrestException pex)
            when (pex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // unique(requester_id, addressee_id) already fires — treat as idempotent.
            return "already-sent";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] SendFriendRequestByIdAsync failed");
            return null;
        }
    }

    public async Task AcceptFriendRequestAsync(string friendshipId, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null) return;
        try
        {
            await _supabase.From<FriendshipEntry>()
                .Where(f => f.Id == friendshipId)
                .Set(f => f.Status!, "accepted")
                .Update();
            _logger.LogInformation("[Friends] Accepted friendship {Id}", friendshipId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] AcceptFriendRequestAsync failed");
        }
    }

    public async Task DeclineFriendRequestAsync(string friendshipId, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null) return;
        try
        {
            await _supabase.From<FriendshipEntry>()
                .Where(f => f.Id == friendshipId)
                .Delete();
            _logger.LogInformation("[Friends] Declined/removed friendship {Id}", friendshipId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] DeclineFriendRequestAsync failed");
        }
    }

    public async Task<List<FriendshipEntry>> GetFriendsAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return [];
        try
        {
            var response = await _supabase.From<FriendshipEntry>()
                .Where(f => f.Status == "accepted")
                .Get();
            var list = response.Models ?? [];
            var profiles = await FetchProfilesAsync(
                list.Select(f => f.PeerIdFor(_currentUser.Id)), ct);
            foreach (var f in list)
            {
                var peer = f.PeerIdFor(_currentUser.Id);
                f.FriendDisplayName ??= profiles.TryGetValue(peer, out var p)
                    ? (p.DisplayName ?? p.Email)
                    : TruncateId(peer);
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] GetFriendsAsync failed");
            return [];
        }
    }

    public async Task<List<FriendshipEntry>> GetPendingRequestsAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return [];
        try
        {
            // Inbound pending requests addressed to me
            var response = await _supabase.From<FriendshipEntry>()
                .Where(f => f.AddresseeId == _currentUser.Id && f.Status == "pending")
                .Get();
            var list = response.Models ?? [];
            var profiles = await FetchProfilesAsync(list.Select(f => f.RequesterId), ct);
            foreach (var f in list)
                f.FriendDisplayName ??= profiles.TryGetValue(f.RequesterId, out var p)
                    ? (p.DisplayName ?? p.Email)
                    : TruncateId(f.RequesterId);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] GetPendingRequestsAsync failed");
            return [];
        }
    }

    public async Task<List<ActivityEntry>> GetActivityFeedAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return [];
        try
        {
            // RLS on activity_feed already filters to self + accepted friends
            var response = await _supabase.From<ActivityEntry>()
                .Order(a => a.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(50)
                .Get();
            var list = response.Models ?? [];
            var profiles = await FetchProfilesAsync(list.Select(a => a.UserId), ct);
            foreach (var a in list)
                a.AuthorDisplayName ??= profiles.TryGetValue(a.UserId, out var p)
                    ? (p.DisplayName ?? p.Email)
                    : TruncateId(a.UserId);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Friends] GetActivityFeedAsync failed");
            return [];
        }
    }

    /// <summary>
    /// Batches a set of user ids into one PostgREST call against
    /// <c>user_profiles</c> so friend/activity lists don't fan out into N+1
    /// lookups. Returns an empty map on failure — callers fall back to
    /// truncated ids.
    /// </summary>
    private async Task<Dictionary<string, UserProfileEntry>> FetchProfilesAsync(
        IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (ids.Count == 0 || _supabase == null) return [];
        try
        {
            var resp = await _supabase.From<UserProfileEntry>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, ids)
                .Get();
            return (resp.Models ?? [])
                .Where(p => p.Id != null)
                .ToDictionary(p => p.Id!, p => p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Friends] FetchProfilesAsync failed — falling back to ids");
            return [];
        }
    }

    public async Task PostActivityAsync(
        string eventType, string seriesTitle, int malId,
        int? episode = null, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return;
        try
        {
            var entry = new ActivityEntry
            {
                UserId      = _currentUser.Id,
                EventType   = eventType,
                SeriesTitle = seriesTitle,
                MalId       = malId,
                Episode     = episode,
                CreatedAt   = DateTime.UtcNow,
            };
            await _supabase.From<ActivityEntry>().Insert(entry);
        }
        catch (Exception ex)
        {
            // Non-fatal — activity posting failure should never block the user
            _logger.LogWarning(ex, "[Friends] PostActivityAsync failed for {Title}", seriesTitle);
        }
    }

    private static string TruncateId(string id) =>
        id.Length > 8 ? id[..8] + "…" : id;

    // ── Helpers ────────────────────────────────────────────────────

    private async Task<AccountUser> ApplySessionAsync(Session session, CancellationToken ct)
    {
        var user = new AccountUser
        {
            Id          = session.User!.Id ?? string.Empty,
            Email       = session.User.Email ?? string.Empty,
            DisplayName = session.User.UserMetadata?.TryGetValue("full_name", out var name) == true
                              ? name?.ToString() : null,
            AvatarUrl   = session.User.UserMetadata?.TryGetValue("avatar_url", out var av) == true
                              ? av?.ToString() : null,
            CreatedAt   = session.User.CreatedAt,
        };

        _currentUser = user;
        PersistSession(session.AccessToken!, session.RefreshToken!);
        AuthStateChanged?.Invoke(user);
        _logger.LogInformation("[Account] Signed in: {Email}", user.Email);

        // Attach realtime subscriptions in the background — never block sign-in.
        _ = Task.Run(() => SubscribeRealtimeAsync(user.Id), ct);
        return user;
    }

    /// <summary>
    /// Starts a minimal HTTP server on localhost:{port} and waits for a
    /// GET /auth/callback?code=... request. Returns (code, state) on success.
    /// </summary>
    private static async Task<(string Code, string State)> RunCallbackServerAsync(
        int port, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/auth/callback/");
        listener.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            // GetContextAsync doesn't accept a CancellationToken; we wrap with a Task.Run
            var contextTask = Task.Run(() => listener.GetContextAsync(), timeoutCts.Token);
            var context     = await contextTask.WaitAsync(timeoutCts.Token);

            var query = context.Request.Url?.Query ?? string.Empty;
            var qs    = HttpUtility.ParseQueryString(query);
            var code  = qs["code"]  ?? string.Empty;
            var state = qs["state"] ?? string.Empty;

            // Send a friendly browser page so the user knows to return to the app
            const string html = """
                <html><body style="font-family:sans-serif;text-align:center;padding:60px">
                <h2>✓ Signed in successfully</h2>
                <p>You can close this tab and return to Sentrychan.</p>
                </body></html>
                """;
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType     = "text/html";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, timeoutCts.Token);
            context.Response.Close();

            return (code, state);
        }
        catch (OperationCanceledException)
        {
            return (string.Empty, string.Empty);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── DPAPI session storage ──────────────────────────────────────

    private record StoredSession(string AccessToken, string RefreshToken);

    private static void PersistSession(string accessToken, string refreshToken)
    {
        try
        {
            Directory.CreateDirectory(_sessionDir);
            var json     = JsonSerializer.Serialize(new StoredSession(accessToken, refreshToken));
            var plain    = Encoding.UTF8.GetBytes(json);
            var cipher   = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_sessionFile, cipher);
        }
        catch { /* non-fatal — user will just need to re-login */ }
    }

    private static StoredSession? LoadStoredSession()
    {
        try
        {
            if (!File.Exists(_sessionFile)) return null;
            var cipher = File.ReadAllBytes(_sessionFile);
            var plain  = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            var json   = Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch { return null; }
    }

    private static void DeleteStoredSession()
    {
        try { if (File.Exists(_sessionFile)) File.Delete(_sessionFile); }
        catch { }
    }

    // ── Realtime subscription plumbing ─────────────────────────────

    /// <summary>
    /// Subscribes to every postgres_changes stream this user should hear about
    /// (friend requests addressed to them, friendship status flips, activity from
    /// friends, watch rooms going live, and shared airing alerts). Fires the
    /// matching public event so view-models can update UI without polling.
    /// Called on sign-in and after session restore.
    /// </summary>
    private async Task SubscribeRealtimeAsync(string myUserId)
    {
        if (_supabase == null) return;
        try
        {
            // Filter friendships to just those addressed to me — everything else
            // is noise and would waste bandwidth. The 6-arg overload takes
            // (database, schema, table, column, value, parameters).
            var friendshipsIn = _supabase.Realtime.Channel(
                "realtime", "public", "friendships",
                "addressee_id", myUserId, null!);
            friendshipsIn.AddPostgresChangeHandler(ListenType.Inserts, (_, change) =>
            {
                var m = change.Model<FriendshipEntry>();
                if (m != null) FriendRequestReceived?.Invoke(m);
            });
            friendshipsIn.AddPostgresChangeHandler(ListenType.Updates, (_, change) =>
            {
                var m = change.Model<FriendshipEntry>();
                if (m?.Status == "accepted") FriendRequestAccepted?.Invoke(m);
            });
            await friendshipsIn.Subscribe();
            _subscribedChannels.Add(friendshipsIn);

            // My own outbound requests can also flip to accepted — filter the
            // other side of the pair.
            var friendshipsOut = _supabase.Realtime.Channel(
                "realtime", "public", "friendships",
                "requester_id", myUserId, null!);
            friendshipsOut.AddPostgresChangeHandler(ListenType.Updates, (_, change) =>
            {
                var m = change.Model<FriendshipEntry>();
                if (m?.Status == "accepted") FriendRequestAccepted?.Invoke(m);
            });
            await friendshipsOut.Subscribe();
            _subscribedChannels.Add(friendshipsOut);

            // RLS on activity_feed already limits visible rows to self + friends,
            // so an unfiltered subscription is what we want.
            var activity = _supabase.Realtime.Channel(
                "realtime", "public", "activity_feed");
            activity.AddPostgresChangeHandler(ListenType.Inserts, (_, change) =>
            {
                var m = change.Model<ActivityEntry>();
                if (m != null && m.UserId != myUserId) ActivityReceived?.Invoke(m);
            });
            await activity.Subscribe();
            _subscribedChannels.Add(activity);

            var rooms = _supabase.Realtime.Channel(
                "realtime", "public", "watch_rooms");
            rooms.AddPostgresChangeHandler(ListenType.Inserts, (_, change) =>
            {
                var m = change.Model<WatchRoomEntry>();
                if (m != null && m.HostId != myUserId && m.IsActive)
                    WatchRoomWentLive?.Invoke(m);
            });
            await rooms.Subscribe();
            _subscribedChannels.Add(rooms);

            var airings = _supabase.Realtime.Channel(
                "realtime", "public", "series_airings");
            airings.AddPostgresChangeHandler(ListenType.Inserts, (_, change) =>
            {
                var m = change.Model<SeriesAiringEntry>();
                if (m != null) AiringAlertReceived?.Invoke(m);
            });
            await airings.Subscribe();
            _subscribedChannels.Add(airings);

            _logger.LogInformation("[Realtime] Subscribed to {N} channels", _subscribedChannels.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Realtime] Subscription setup failed — live updates disabled");
        }
    }

    private void TeardownRealtime()
    {
        foreach (var ch in _subscribedChannels)
        {
            try { ch.Unsubscribe(); } catch { /* channel may already be dead */ }
        }
        _subscribedChannels.Clear();
    }

    // ── Manga library cloud sync ───────────────────────────────────

    public async Task SyncMangaLibraryToCloudAsync(
        IEnumerable<Manga> manga, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return;
        try
        {
            var entries = manga.Select(m => new CloudMangaEntry
            {
                UserId          = _currentUser.Id,
                Source          = m.Source,
                SourceId        = m.SourceId,
                Title           = m.Title,
                CoverUrl        = string.IsNullOrEmpty(m.CoverPath) || m.CoverPath.StartsWith("http")
                                      ? m.CoverPath : null,
                TotalChapters   = m.TotalChapters,
                LastReadChapter = m.LastReadChapter,
                IsNovel         = m.IsNovel,
                IsCensored      = m.IsCensored,
                UpdatedAt       = DateTime.UtcNow,
            }).ToList();
            if (entries.Count == 0) return;

            await _supabase.From<CloudMangaEntry>().Upsert(entries);
            _logger.LogInformation("[Account] Synced {Count} manga to cloud", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] SyncMangaLibraryToCloudAsync failed");
        }
    }

    public async Task<List<CloudMangaEntry>> PullMangaLibraryFromCloudAsync(
        CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return [];
        try
        {
            var response = await _supabase.From<CloudMangaEntry>()
                .Where(e => e.UserId == _currentUser.Id)
                .Get();
            return response.Models ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] PullMangaLibraryFromCloudAsync failed");
            return [];
        }
    }

    // ── Avatar upload ──────────────────────────────────────────────

    public async Task<string?> UploadAvatarAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null || _currentUser == null) return null;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"))
                ext = ".png";

            // Deterministic path so a re-upload replaces the previous file.
            var objectPath = $"{_currentUser.Id}/avatar{ext}";
            var bytes      = await File.ReadAllBytesAsync(filePath, ct);
            var bucket     = _supabase.Storage.From("avatars");

            await bucket.Upload(bytes, objectPath, new Supabase.Storage.FileOptions
            {
                CacheControl = "3600",
                Upsert       = true,
                ContentType  = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp"           => "image/webp",
                    ".gif"            => "image/gif",
                    _                 => "image/png",
                },
            });

            // getPublicUrl is a cheap synchronous helper — no round-trip.
            var publicUrl = bucket.GetPublicUrl(objectPath);

            // Mirror the URL into user_profiles so friend UIs pick it up.
            try
            {
                await _supabase.From<UserProfileEntry>()
                    .Where(p => p.Id == _currentUser.Id)
                    .Set(p => p.AvatarUrl!, publicUrl)
                    .Update();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[Account] Avatar row update failed"); }

            _currentUser.AvatarUrl = publicUrl;
            AuthStateChanged?.Invoke(_currentUser);
            return publicUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] UploadAvatarAsync failed for {Path}", filePath);
            return null;
        }
    }

    // ── Account deletion (Edge Function) ───────────────────────────

    public async Task<bool> DeleteAccountAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null) return false;
        try
        {
            // The Edge Function reads the caller's JWT from Authorization and
            // deletes exactly that user server-side using service_role. The
            // client's second arg is the caller's access token, which the SDK
            // forwards as Authorization: Bearer <token>.
            var accessToken = _supabase.Auth.CurrentSession?.AccessToken ?? SupabaseConfig.AnonKey;
            await _supabase.Functions.Invoke(
                "delete-account", accessToken,
                new Supabase.Functions.Client.InvokeFunctionOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                    },
                    Body = new Dictionary<string, object>(),
                });
            _logger.LogInformation("[Account] Account deletion request accepted");

            // Clear local session — Supabase has cascaded the DB rows already.
            TeardownRealtime();
            _currentUser = null;
            DeleteStoredSession();
            AuthStateChanged?.Invoke(null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Account] DeleteAccountAsync failed (is the Edge Function deployed?)");
            return false;
        }
    }

    // ── Airing alerts ──────────────────────────────────────────────

    public async Task PostAiringAsync(
        int malId, string seriesTitle, int? episode = null,
        string? resolution = null, string? source = null, string? magnet = null,
        CancellationToken ct = default)
    {
        if (!IsSignedIn || _supabase == null) return;
        try
        {
            var entry = new SeriesAiringEntry
            {
                MalId       = malId,
                SeriesTitle = seriesTitle,
                Episode     = episode,
                Resolution  = resolution,
                Source      = source,
                Magnet      = magnet,
                CreatedAt   = DateTime.UtcNow,
            };
            await _supabase.From<SeriesAiringEntry>().Insert(entry);
        }
        catch (Exception ex)
        {
            // Never let a failed alert break the caller (RSS monitor).
            _logger.LogWarning(ex, "[Airings] PostAiringAsync failed for {Title}", seriesTitle);
        }
    }

    // ── Watch-room presence ────────────────────────────────────────

    public async Task<IAsyncDisposable> JoinWatchRoomPresenceAsync(
        string watchRoomId,
        Action<IReadOnlyList<WatchRoomPresence>> onSync,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        if (_supabase == null || _currentUser == null)
            return new PresenceHandle(null, null);

        try
        {
            // Presence key is per-user so the same viewer opening a second window
            // still counts once.
            var channel  = _supabase.Realtime.Channel($"watch-room:{watchRoomId}");
            var presence = channel.Register<WatchRoomPresence>(_currentUser.Id);

            presence.AddPresenceEventHandler(PresenceEventType.Sync, (_, _) =>
            {
                try
                {
                    var flat = presence.CurrentState
                        .SelectMany(kv => kv.Value)
                        .ToList();
                    onSync(flat);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Presence] Sync handler threw");
                }
            });

            await channel.Subscribe();

            presence.Track(new WatchRoomPresence
            {
                UserId      = _currentUser.Id,
                DisplayName = _currentUser.DisplayName ?? _currentUser.Email,
                AvatarUrl   = _currentUser.AvatarUrl,
                JoinedAt    = DateTime.UtcNow,
            });

            return new PresenceHandle(channel, presence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Presence] Join failed for room {Id}", watchRoomId);
            return new PresenceHandle(null, null);
        }
    }

    private sealed class PresenceHandle : IAsyncDisposable
    {
        private readonly RtChannel? _channel;
        private readonly RealtimePresence<WatchRoomPresence>? _presence;
        public PresenceHandle(RtChannel? channel, RealtimePresence<WatchRoomPresence>? presence)
        { _channel = channel; _presence = presence; }

        public ValueTask DisposeAsync()
        {
            try { _presence?.Untrack(); } catch { }
            try { _channel?.Unsubscribe(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        TeardownRealtime();
        // Supabase.Client does not implement IDisposable — nothing else to dispose.
        return ValueTask.CompletedTask;
    }
}
