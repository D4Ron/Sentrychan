using ReactiveUI;
using Sentrychan.Core.Config;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

public class AccountViewModel : ViewModelBase
{
    private readonly IAccountService?       _account;
    private readonly ISeriesService?        _series;
    private readonly IMangaService?         _manga;
    private readonly INotificationService?  _notifications;

    // ── Auth state ─────────────────────────────────────────────────
    private bool _isSignedIn;
    public bool IsSignedIn
    {
        get => _isSignedIn;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSignedIn, value);
            this.RaisePropertyChanged(nameof(IsSignedOut));
            this.RaisePropertyChanged(nameof(AvatarInitials));
            this.RaisePropertyChanged(nameof(DisplayEmail));
            this.RaisePropertyChanged(nameof(DisplayName));
            this.RaisePropertyChanged(nameof(UserId));
        }
    }
    public bool IsSignedOut => !_isSignedIn;

    private AccountUser? _user;
    public AccountUser? User
    {
        get => _user;
        set
        {
            this.RaiseAndSetIfChanged(ref _user, value);
            IsSignedIn = value != null;
        }
    }

    public string AvatarInitials => _user?.Initials ?? "?";
    public string DisplayEmail   => _user?.Email    ?? string.Empty;
    public string DisplayName    => _user?.DisplayName ?? _user?.Email ?? string.Empty;
    /// <summary>Full user ID shown so friends can add by ID.</summary>
    public string UserId         => _user?.Id ?? string.Empty;

    // ── Supabase configuration state ───────────────────────────────
    public bool   IsSupabaseConfigured => SupabaseConfig.IsConfigured;
    public string NotConfiguredMessage => IsSupabaseConfigured
        ? string.Empty
        : "Supabase is not configured. Edit SupabaseConfig.cs to enable cloud sync.";

    // ── Sync state ─────────────────────────────────────────────────
    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        set => this.RaiseAndSetIfChanged(ref _isSyncing, value);
    }

    private string _syncStatus = string.Empty;
    public string SyncStatus
    {
        get => _syncStatus;
        set => this.RaiseAndSetIfChanged(ref _syncStatus, value);
    }

    // ── Cloud library entries ──────────────────────────────────────
    public ObservableCollection<CloudSeriesEntry> CloudLibrary { get; } = new();

    private bool _hasCloudLibrary;
    public bool HasCloudLibrary
    {
        get => _hasCloudLibrary;
        set => this.RaiseAndSetIfChanged(ref _hasCloudLibrary, value);
    }

    // ── Friends tab ────────────────────────────────────────────────
    public ObservableCollection<FriendshipEntry> Friends         { get; } = new();
    public ObservableCollection<FriendshipEntry> PendingRequests { get; } = new();
    public ObservableCollection<ActivityEntry>   ActivityFeed    { get; } = new();

    private bool _hasFriends;
    public bool HasFriends
    {
        get => _hasFriends;
        set => this.RaiseAndSetIfChanged(ref _hasFriends, value);
    }

    private bool _hasPending;
    public bool HasPending
    {
        get => _hasPending;
        set => this.RaiseAndSetIfChanged(ref _hasPending, value);
    }

    private bool _hasActivity;
    public bool HasActivity
    {
        get => _hasActivity;
        set => this.RaiseAndSetIfChanged(ref _hasActivity, value);
    }

    private bool _isLoadingFriends;
    public bool IsLoadingFriends
    {
        get => _isLoadingFriends;
        set => this.RaiseAndSetIfChanged(ref _isLoadingFriends, value);
    }

    private string _friendsStatus = string.Empty;
    public string FriendsStatus
    {
        get => _friendsStatus;
        set => this.RaiseAndSetIfChanged(ref _friendsStatus, value);
    }

    private string _addFriendId = string.Empty;
    public string AddFriendId
    {
        get => _addFriendId;
        set => this.RaiseAndSetIfChanged(ref _addFriendId, value);
    }

    // ── Commands ───────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> SignInWithGoogleCommand  { get; }
    public ReactiveCommand<Unit, Unit> SignOutCommand           { get; }
    public ReactiveCommand<Unit, Unit> SyncToCloudCommand       { get; }
    public ReactiveCommand<Unit, Unit> PullFromCloudCommand     { get; }
    public ReactiveCommand<Unit, Unit> SyncMangaToCloudCommand  { get; }
    public ReactiveCommand<Unit, Unit> PullMangaFromCloudCommand{ get; }
    public ReactiveCommand<Unit, Unit> LoadFriendsCommand       { get; }
    public ReactiveCommand<Unit, Unit> SendFriendRequestCommand { get; }
    public ReactiveCommand<string, Unit> UploadAvatarCommand    { get; }
    public ReactiveCommand<Unit, Unit> DeleteAccountCommand     { get; }
    public ReactiveCommand<FriendshipEntry, Unit> AcceptFriendCommand  { get; }
    public ReactiveCommand<FriendshipEntry, Unit> DeclineFriendCommand { get; }

    /// <summary>Set by the account settings pane to prompt file-picker + confirm-delete.</summary>
    public Func<Task<string?>>?   PickAvatarFile { get; set; }
    public Func<Task<bool>>?      ConfirmDeleteAccount { get; set; }

    // Design-time
    public AccountViewModel()
    {
        SignInWithGoogleCommand   = ReactiveCommand.Create(() => { });
        SignOutCommand            = ReactiveCommand.Create(() => { });
        SyncToCloudCommand        = ReactiveCommand.Create(() => { });
        PullFromCloudCommand      = ReactiveCommand.Create(() => { });
        SyncMangaToCloudCommand   = ReactiveCommand.Create(() => { });
        PullMangaFromCloudCommand = ReactiveCommand.Create(() => { });
        LoadFriendsCommand        = ReactiveCommand.Create(() => { });
        SendFriendRequestCommand  = ReactiveCommand.Create(() => { });
        UploadAvatarCommand       = ReactiveCommand.Create<string>(_ => { });
        DeleteAccountCommand      = ReactiveCommand.Create(() => { });
        AcceptFriendCommand       = ReactiveCommand.Create<FriendshipEntry>(_ => { });
        DeclineFriendCommand      = ReactiveCommand.Create<FriendshipEntry>(_ => { });
    }

    // Runtime
    public AccountViewModel(
        IAccountService account,
        ISeriesService series,
        IMangaService? manga = null,
        INotificationService? notifications = null) : this()
    {
        _account       = account;
        _series        = series;
        _manga         = manga;
        _notifications = notifications;

        User = account.CurrentUser;

        account.AuthStateChanged += user =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => User = user);
        };

        // Realtime push handlers — all touch observable collections, so hop
        // to the UI thread. Silent no-ops if the events never fire (Supabase
        // unreachable etc.).
        account.FriendRequestReceived += entry => OnUi(async () =>
        {
            PendingRequests.Insert(0, entry);
            HasPending = true;
            FriendsStatus = $"New friend request from {entry.FriendDisplayName ?? entry.RequesterId[..8]}";
            _notifications?.Notify("Sentrychan · new friend request",
                $"{entry.FriendDisplayName ?? "Someone"} wants to be friends");
            await Task.CompletedTask;
        });
        account.FriendRequestAccepted += _ => OnUi(async () =>
        {
            // Cheapest correct thing: re-fetch friends (small list).
            await LoadFriendsAsync();
        });
        account.ActivityReceived += entry => OnUi(async () =>
        {
            ActivityFeed.Insert(0, entry);
            HasActivity = true;
            while (ActivityFeed.Count > 50) ActivityFeed.RemoveAt(ActivityFeed.Count - 1);
            await Task.CompletedTask;
        });
        account.WatchRoomWentLive += room => OnUi(async () =>
        {
            _notifications?.Notify("Sentrychan · friend went live",
                $"{room.HostName} is watching {room.SeriesTitle}");
            await Task.CompletedTask;
        });
        account.AiringAlertReceived += airing => OnUi(async () =>
        {
            // Filter to shows in the local library.
            if (_series != null)
            {
                var mine = await _series.GetAllAsync();
                if (!mine.Any(s => s.MalId == airing.MalId)) return;
                var epLabel = airing.Episode.HasValue ? $" ep. {airing.Episode}" : string.Empty;
                _notifications?.Notify("Sentrychan · new episode",
                    $"{airing.SeriesTitle}{epLabel} just aired");
            }
        });

        SignInWithGoogleCommand   = ReactiveCommand.CreateFromTask(SignInWithGoogleAsync);
        SignOutCommand            = ReactiveCommand.CreateFromTask(SignOutAsync);
        SyncToCloudCommand        = ReactiveCommand.CreateFromTask(SyncToCloudAsync);
        PullFromCloudCommand      = ReactiveCommand.CreateFromTask(PullFromCloudAsync);
        SyncMangaToCloudCommand   = ReactiveCommand.CreateFromTask(SyncMangaToCloudAsync);
        PullMangaFromCloudCommand = ReactiveCommand.CreateFromTask(PullMangaFromCloudAsync);
        LoadFriendsCommand        = ReactiveCommand.CreateFromTask(LoadFriendsAsync);
        SendFriendRequestCommand  = ReactiveCommand.CreateFromTask(SendFriendRequestAsync);
        UploadAvatarCommand       = ReactiveCommand.CreateFromTask<string>(UploadAvatarAsync);
        DeleteAccountCommand      = ReactiveCommand.CreateFromTask(DeleteAccountAsync);
        AcceptFriendCommand       = ReactiveCommand.CreateFromTask<FriendshipEntry>(AcceptAsync);
        DeclineFriendCommand      = ReactiveCommand.CreateFromTask<FriendshipEntry>(DeclineAsync);
    }

    private static void OnUi(Func<Task> work) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () => { try { await work(); } catch { } });

    // ── Auth ───────────────────────────────────────────────────────

    private async Task SignInWithGoogleAsync(CancellationToken ct)
    {
        if (_account == null) return;
        SyncStatus = "Opening browser for Google login…";
        var user = await _account.SignInWithGoogleAsync(ct);
        SyncStatus = user != null ? $"Signed in as {user.Email}" : "Sign-in cancelled or failed";
        if (user != null) await LoadFriendsAsync(ct);
    }

    private async Task SignOutAsync(CancellationToken ct)
    {
        if (_account == null) return;
        await _account.SignOutAsync(ct);
        CloudLibrary.Clear();
        Friends.Clear();
        PendingRequests.Clear();
        ActivityFeed.Clear();
        HasCloudLibrary = HasFriends = HasPending = HasActivity = false;
        SyncStatus = "Signed out";
    }

    // ── Library sync ──────────────────────────────────────────────

    private async Task SyncToCloudAsync(CancellationToken ct)
    {
        if (_account == null || _series == null) return;
        IsSyncing  = true;
        SyncStatus = "Syncing library to cloud…";
        try
        {
            var all = await _series.GetAllAsync(ct);
            await _account.SyncLibraryToCloudAsync(all, ct);
            SyncStatus = $"Synced {all.Count} series  ·  {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) { SyncStatus = $"Sync failed: {ex.Message}"; }
        finally { IsSyncing = false; }
    }

    private async Task PullFromCloudAsync(CancellationToken ct)
    {
        if (_account == null) return;
        IsSyncing  = true;
        SyncStatus = "Pulling library from cloud…";
        try
        {
            var entries = await _account.PullLibraryFromCloudAsync(ct);
            CloudLibrary.Clear();
            foreach (var e in entries) CloudLibrary.Add(e);
            HasCloudLibrary = CloudLibrary.Count > 0;
            SyncStatus = HasCloudLibrary
                ? $"Pulled {entries.Count} series from cloud"
                : "No cloud library found";
        }
        catch (Exception ex) { SyncStatus = $"Pull failed: {ex.Message}"; }
        finally { IsSyncing = false; }
    }

    // ── Friends ────────────────────────────────────────────────────

    private async Task LoadFriendsAsync(CancellationToken ct = default)
    {
        if (_account == null || !IsSignedIn) return;
        IsLoadingFriends = true;
        FriendsStatus    = "Loading…";
        try
        {
            var friends  = await _account.GetFriendsAsync(ct);
            var pending  = await _account.GetPendingRequestsAsync(ct);
            var activity = await _account.GetActivityFeedAsync(ct);

            Friends.Clear();
            foreach (var f in friends)  Friends.Add(f);

            PendingRequests.Clear();
            foreach (var p in pending) PendingRequests.Add(p);

            ActivityFeed.Clear();
            foreach (var a in activity) ActivityFeed.Add(a);

            HasFriends  = Friends.Count > 0;
            HasPending  = PendingRequests.Count > 0;
            HasActivity = ActivityFeed.Count > 0;
            FriendsStatus = string.Empty;
        }
        catch (Exception ex)
        {
            FriendsStatus = $"Failed: {ex.Message}";
        }
        finally { IsLoadingFriends = false; }
    }

    private async Task SendFriendRequestAsync(CancellationToken ct)
    {
        if (_account == null || string.IsNullOrWhiteSpace(AddFriendId)) return;
        FriendsStatus = "Sending request…";
        try
        {
            var result = await _account.SendFriendRequestByIdAsync(AddFriendId.Trim(), ct);
            FriendsStatus = result == "pending"
                ? $"Friend request sent to {AddFriendId[..Math.Min(8, AddFriendId.Length)]}…"
                : "Failed to send request — check the User ID";
            if (result == "pending") AddFriendId = string.Empty;
        }
        catch (Exception ex) { FriendsStatus = $"Error: {ex.Message}"; }
    }

    private async Task AcceptAsync(FriendshipEntry entry, CancellationToken ct)
    {
        if (_account == null || entry.Id == null) return;
        await _account.AcceptFriendRequestAsync(entry.Id, ct);
        await LoadFriendsAsync(ct);
    }

    private async Task DeclineAsync(FriendshipEntry entry, CancellationToken ct)
    {
        if (_account == null || entry.Id == null) return;
        await _account.DeclineFriendRequestAsync(entry.Id, ct);
        PendingRequests.Remove(entry);
        HasPending = PendingRequests.Count > 0;
    }

    // ── Manga library sync ────────────────────────────────────────

    private async Task SyncMangaToCloudAsync(CancellationToken ct)
    {
        if (_account == null || _manga == null) return;
        IsSyncing  = true;
        SyncStatus = "Syncing manga library to cloud…";
        try
        {
            var all = await _manga.GetAllAsync(ct);
            await _account.SyncMangaLibraryToCloudAsync(all, ct);
            SyncStatus = $"Synced {all.Count} manga  ·  {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) { SyncStatus = $"Manga sync failed: {ex.Message}"; }
        finally { IsSyncing = false; }
    }

    private async Task PullMangaFromCloudAsync(CancellationToken ct)
    {
        if (_account == null) return;
        IsSyncing  = true;
        SyncStatus = "Pulling manga library from cloud…";
        try
        {
            var entries = await _account.PullMangaLibraryFromCloudAsync(ct);
            SyncStatus = entries.Count > 0
                ? $"Pulled {entries.Count} manga from cloud"
                : "No cloud manga library found";
        }
        catch (Exception ex) { SyncStatus = $"Pull failed: {ex.Message}"; }
        finally { IsSyncing = false; }
    }

    // ── Avatar ────────────────────────────────────────────────────

    private async Task UploadAvatarAsync(string? filePath, CancellationToken ct)
    {
        if (_account == null) return;
        // Path can come from the command param OR from the pluggable file picker.
        var path = filePath;
        if (string.IsNullOrWhiteSpace(path) && PickAvatarFile != null)
            path = await PickAvatarFile();
        if (string.IsNullOrWhiteSpace(path)) return;

        SyncStatus = "Uploading avatar…";
        var url = await _account.UploadAvatarAsync(path, ct);
        SyncStatus = url != null ? "Avatar updated" : "Avatar upload failed";
        if (url != null)
        {
            // Force AvatarInitials + DisplayName re-notify by re-assigning User.
            this.RaisePropertyChanged(nameof(User));
            this.RaisePropertyChanged(nameof(AvatarInitials));
        }
    }

    // ── Account deletion ──────────────────────────────────────────

    private async Task DeleteAccountAsync(CancellationToken ct)
    {
        if (_account == null || !IsSignedIn) return;
        if (ConfirmDeleteAccount != null && !await ConfirmDeleteAccount()) return;

        SyncStatus = "Deleting account…";
        var ok = await _account.DeleteAccountAsync(ct);
        SyncStatus = ok
            ? "Account deleted. Goodbye."
            : "Deletion failed — is the delete-account Edge Function deployed?";
        if (ok)
        {
            CloudLibrary.Clear();
            Friends.Clear();
            PendingRequests.Clear();
            ActivityFeed.Clear();
            HasCloudLibrary = HasFriends = HasPending = HasActivity = false;
        }
    }
}
