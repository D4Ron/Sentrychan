namespace Sentrychan.Core.Vault;

/// <summary>
/// The vault's folder can't be reached — usually its drive is disconnected or has stopped
/// answering. Nothing in the vault was touched; it opens again once the drive is back.
/// </summary>
public sealed class VaultUnavailableException : IOException
{
    public VaultUnavailableException(string root, Exception? inner = null)
        : base(MessageFor(root), inner) => Root = root;

    public string Root { get; }

    /// <summary>The drive the vault lives on, e.g. "D:", for messages.</summary>
    public string Drive => Path.GetPathRoot(Root)?.TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } d ? d : Root;

    private static string MessageFor(string root)
    {
        var drive = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar);
        var where = string.IsNullOrEmpty(drive) ? "its drive" : drive;
        return $"The vault is on {where}, which can't be reached right now. Its files stay where they are — " +
               "once the drive is back (reconnect it or restart the PC), the vault opens again.";
    }
}
