using System.Diagnostics;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.App;

/// <summary>
/// macOS notifications through AppleScript's "display notification". The texts travel as
/// script arguments, never spliced into the script, so nothing in a title can break out.
/// Best-effort; never throws into the caller.
/// </summary>
public sealed class MacNotificationService : INotificationService
{
    public void Notify(string title, string message)
    {
        (title, message) = Privacy(title, message);
        DesktopNotifications.Run("osascript",
            "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
            title, message);
    }

    internal static (string, string) Privacy(string title, string message) =>
        // The system keeps notifications around after the app leaves secret mode.
        Sentrychan.Core.Vault.Privacy.SecretModeActive()
            ? (Sentrychan.Core.BuildInfo.AppName, "You have a new notification.")
            : (title, message);
}

/// <summary>
/// Linux notifications through notify-send (libnotify), which desktops that show notifications
/// have. Without it nothing is shown — the in-app notifications still are.
/// </summary>
public sealed class LinuxNotificationService : INotificationService
{
    public void Notify(string title, string message)
    {
        (title, message) = MacNotificationService.Privacy(title, message);
        DesktopNotifications.Run("notify-send", "--app-name", Sentrychan.Core.BuildInfo.AppName, "--", title, message);
    }
}

internal static class DesktopNotifications
{
    /// <summary>The service for this system (Windows toasts as before).</summary>
    public static INotificationService ForCurrentOs() =>
        OperatingSystem.IsWindows() ? new WindowsNotificationService()
        : OperatingSystem.IsMacOS() ? new MacNotificationService()
        : new LinuxNotificationService();

    public static void Run(string program, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
        }
        catch { /* the tool isn't there: no desktop notification */ }
    }
}
