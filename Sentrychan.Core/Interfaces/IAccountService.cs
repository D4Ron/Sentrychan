using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Manages Supabase authentication and cloud library sync.
/// </summary>
public interface IAccountService
{
    /// <summary>True when a valid session is held in memory.</summary>
    bool IsSignedIn { get; }

    /// <summary>The currently authenticated user, or null if not signed in.</summary>
    AccountUser? CurrentUser { get; }

    /// <summary>
    /// Fired on the calling thread whenever sign-in state changes.
    /// Null argument means the user signed out.
    /// </summary>
    event Action<AccountUser?>? AuthStateChanged;

    // ── Realtime push events (see EnableRealtimeAsync) ─────────────

    /// <summary>Someone just addressed a new friend request to me.</summary>
    event Action<FriendshipEntry>? FriendRequestReceived;

    /// <summary>A friendship of mine just flipped to accepted.</summary>
    event Action<FriendshipEntry>? FriendRequestAccepted;

    /// <summary>A friend just posted something to the shared activity feed.</summary>
    event Action<ActivityEntry>? ActivityReceived;

    /// <summary>A friend just opened a Hyperbeam watch room.</summary>
    event Action<WatchRoomEntry>? WatchRoomWentLive;

    /// <summary>A new episode was posted by anyone; the client filters by library.</summary>
    event Action<SeriesAiringEntry>? AiringAlertReceived;

    /// <summary>
    /// Opens the system browser for Google OAuth, starts a local HTTP listener
    /// to receive the redirect, and returns the signed-in user on success.
    /// </summary>
    Task<AccountUser?> SignInWithGoogleAsync(CancellationToken ct = default);

    /// <summary>Revokes the current session and clears stored credentials.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the persisted DPAPI-encrypted session from disk and refreshes
    /// the access token if needed. Returns true if a valid session was restored.
    /// Call this once at app startup.
    /// </summary>
    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes the current user's account and cascades every
    /// owned row (library, manga, friendships, activity, watch rooms,
    /// profile, avatar). Requires the delete-account Edge Function to be
    /// deployed. Returns true on success.
    /// </summary>
    Task<bool> DeleteAccountAsync(CancellationToken ct = default);

    // ── Anime library sync ────────────────────────────────────────

    /// <summary>Upserts the entire local anime library to Supabase.</summary>
    Task SyncLibraryToCloudAsync(IEnumerable<Series> series, CancellationToken ct = default);

    /// <summary>Fetches all anime library entries from Supabase for the current user.</summary>
    Task<List<CloudSeriesEntry>> PullLibraryFromCloudAsync(CancellationToken ct = default);

    // ── Manga library sync ────────────────────────────────────────

    /// <summary>Upserts the entire local manga/novel library to Supabase.</summary>
    Task SyncMangaLibraryToCloudAsync(IEnumerable<Manga> manga, CancellationToken ct = default);

    /// <summary>Fetches all manga/novel library entries from Supabase for the current user.</summary>
    Task<List<CloudMangaEntry>> PullMangaLibraryFromCloudAsync(CancellationToken ct = default);

    // ── Avatar ────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a local image file to the <c>avatars</c> bucket at
    /// <c>&lt;user-id&gt;/avatar&lt;ext&gt;</c> and updates the user's profile
    /// with the resulting public URL. Returns the public URL, or null on failure.
    /// </summary>
    Task<string?> UploadAvatarAsync(string filePath, CancellationToken ct = default);

    // ── Friend system ──────────────────────────────────────────────

    /// <summary>Sends a friend request to the user with the given email.</summary>
    Task<string?> SendFriendRequestAsync(string addresseeEmail, CancellationToken ct = default);

    /// <summary>Sends a friend request to the user with the given UUID (shown in their Account panel).</summary>
    Task<string?> SendFriendRequestByIdAsync(string addresseeUserId, CancellationToken ct = default);

    /// <summary>Accepts a pending friend request. Returns the updated status.</summary>
    Task AcceptFriendRequestAsync(string friendshipId, CancellationToken ct = default);

    /// <summary>Declines or cancels a friend request.</summary>
    Task DeclineFriendRequestAsync(string friendshipId, CancellationToken ct = default);

    /// <summary>Returns all accepted friendships for the current user.</summary>
    Task<List<FriendshipEntry>> GetFriendsAsync(CancellationToken ct = default);

    /// <summary>Returns pending inbound friendship requests.</summary>
    Task<List<FriendshipEntry>> GetPendingRequestsAsync(CancellationToken ct = default);

    /// <summary>Returns the combined activity feed visible to the current user.</summary>
    Task<List<ActivityEntry>> GetActivityFeedAsync(CancellationToken ct = default);

    /// <summary>
    /// Posts an activity event for the current user.
    /// Silently no-ops if not signed in.
    /// </summary>
    Task PostActivityAsync(
        string eventType, string seriesTitle, int malId,
        int? episode = null, CancellationToken ct = default);

    // ── Airing alerts (server-side fan-out) ────────────────────────

    /// <summary>
    /// Records a new release into the shared <c>series_airings</c> table so
    /// every signed-in Sentrychan viewer with this show in their library
    /// gets a live push notification. Called by the RSS/monitor pipeline
    /// whenever it detects a new episode.
    /// </summary>
    Task PostAiringAsync(
        int malId, string seriesTitle, int? episode = null,
        string? resolution = null, string? source = null, string? magnet = null,
        CancellationToken ct = default);

    // ── Watch-room presence (Realtime CRDT) ────────────────────────

    /// <summary>
    /// Joins the presence channel for <paramref name="watchRoomId"/> and
    /// begins tracking this viewer. <paramref name="onSync"/> is invoked
    /// whenever the merged viewer list changes. Returns a handle whose
    /// disposal leaves the channel.
    /// </summary>
    Task<IAsyncDisposable> JoinWatchRoomPresenceAsync(
        string watchRoomId,
        Action<IReadOnlyList<WatchRoomPresence>> onSync,
        CancellationToken ct = default);
}
