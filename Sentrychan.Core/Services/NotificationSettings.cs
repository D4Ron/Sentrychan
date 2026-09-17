namespace Sentrychan.Core.Services;

public enum NotificationLevel { Off, Important, All }

/// <summary>
/// Process-wide notification preferences, cached from AppConfigs. Decides whether an
/// event is shown at all, and whether an "important" one fires a Windows toast or an
/// in-app toast. Loaded at startup and refreshed when Settings are saved.
/// </summary>
public static class NotificationSettings
{
    public const string LevelKey   = "NotificationLevel";    // "Off" | "Important" | "All"
    public const string WindowsKey = "NotificationsWindows"; // "true" | "false"

    /// <summary>Default is deliberately quiet: only important events.</summary>
    public static NotificationLevel Level { get; private set; } = NotificationLevel.Important;

    /// <summary>Whether important events go to Windows toasts (else in-app only).</summary>
    public static bool UseWindows { get; private set; } = true;

    public static void Apply(string? level, bool useWindows)
    {
        Level = level switch
        {
            "Off" => NotificationLevel.Off,
            "All" => NotificationLevel.All,
            _     => NotificationLevel.Important
        };
        UseWindows = useWindows;
    }

    /// <summary>Show this event at all? Routine events only appear at the "All" level.</summary>
    public static bool ShouldShow(bool important) =>
        Level != NotificationLevel.Off && (important || Level == NotificationLevel.All);

    /// <summary>Route to a Windows toast (vs in-app)? Only important events ever do.</summary>
    public static bool ToWindows(bool important) => UseWindows && important;
}
