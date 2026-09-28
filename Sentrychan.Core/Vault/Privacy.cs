namespace Sentrychan.Core.Vault;

/// <summary>
/// Keeps private (Sentrykun) items' names out of places that outlive the session:
/// the log files and Windows' notification history. Anything that writes a release or
/// file name there asks <see cref="Name"/> first.
/// </summary>
public static class Privacy
{
    public const string Placeholder = "(private item)";

    /// <summary>Set by the vault once loaded: whether a download (hash or file name) is private.</summary>
    public static Func<string?, bool> IsPrivate { get; set; } = _ => false;

    /// <summary>True while secret mode is on — everything is treated as private.</summary>
    public static Func<bool> SecretModeActive { get; set; } = () => false;

    /// <summary>The name to log or notify: the real one, or a placeholder if any key marks it private.</summary>
    public static string Name(string? name, params string?[] keys)
    {
        if (SecretModeActive() || IsPrivate(name) || keys.Any(k => IsPrivate(k)))
            return Placeholder;
        return name ?? string.Empty;
    }
}
