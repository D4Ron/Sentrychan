namespace Sentrychan.Core;

/// <summary>
/// Keeps stable and preview from working the same library at once. Each flavour holds its own
/// single-instance mutex for its whole lifetime (Program.cs); at startup each checks whether the
/// other flavour's mutex is held. If it is, this app starts with RSS monitoring, torrent
/// downloads, manga downloads and download-folder filing paused: the two apps usually share a
/// library and a download folder, and both working them would download and file everything twice.
///
/// Decided once at startup. If the other app quits, restarting this one resumes normally —
/// deliberately not automatic, so a half-finished download in one app is never picked up by the
/// other mid-flight.
/// </summary>
public static class InstanceGuard
{
    // Stable's names are the ones every earlier build used, so a preview recognises a stable
    // that predates this class.
    public const string StableInstanceMutex  = @"Global\Sentrychan_SingleInstance";
    public const string PreviewInstanceMutex = @"Global\SentrychanPreview_SingleInstance";

    public static string OwnInstanceMutex   => BuildInfo.IsPreview ? PreviewInstanceMutex : StableInstanceMutex;
    public static string OtherInstanceMutex => BuildInfo.IsPreview ? StableInstanceMutex : PreviewInstanceMutex;
    private static string OtherAppName      => BuildInfo.IsPreview ? "Sentrychan" : "Sentrychan Preview";

    /// <summary>True when the other flavour was running as this one started; see the class notes.</summary>
    public static bool PausedForOtherInstance { get; private set; }

    /// <summary>What the banner (and anything refused because of the pause) tells the user.</summary>
    public static string PausedMessage =>
        $"{OtherAppName} is already running — monitoring is paused here so nothing downloads twice. " +
        $"Close {OtherAppName} and restart this app to resume.";

    /// <summary>Called once at startup, after this app has taken its own instance mutex.</summary>
    public static void CheckOtherInstance()
    {
        if (IsHeldElsewhere(OtherInstanceMutex)) PausedForOtherInstance = true;
    }

    /// <summary>
    /// Whether another process currently owns the named mutex. Merely existing isn't enough: on
    /// Linux and macOS a crashed process can leave the backing file behind, so the mutex is
    /// probed — if it can be taken (or was abandoned), nobody is running.
    /// </summary>
    public static bool IsHeldElsewhere(string mutexName)
    {
        Mutex? mutex;
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out mutex)) return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Exists but belongs to another Windows account's session. Assume it's live: pausing
            // needlessly is recoverable, downloading everything twice is not.
            return true;
        }

        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException)
            {
                // The owner died without releasing it; we now own it.
            }
            mutex.ReleaseMutex();
            return false;
        }
    }
}
