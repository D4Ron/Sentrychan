namespace Sentrychan.Core.Library;

/// <summary>
/// Making sure the configured library folder exists before anything is filed into it. A user who
/// deletes the folder (to start over, or by mistake) used to stop every download from being filed —
/// each one stayed in Downloads with "library path not configured" in the log.
/// </summary>
public static class LibraryFolder
{
    /// <summary>
    /// True when <paramref name="path"/> exists or was created again. Only re-created inside a
    /// folder that still exists: a library on a disconnected drive mustn't turn into an ordinary
    /// folder on the system disk (macOS would happily create "/Volumes/Drive/Anime" on the boot volume).
    /// </summary>
    public static bool Ensure(string? path, out string? problem, out bool created)
    {
        problem = null;
        created = false;
        if (string.IsNullOrWhiteSpace(path)) { problem = "no anime folder is set (Settings → General)"; return false; }
        if (Directory.Exists(path)) return true;

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent == null || !Directory.Exists(parent))
        {
            problem = $"\"{full}\" is missing and so is the folder it was in — is its drive connected?";
            return false;
        }
        if (OperatingSystem.IsMacOS() && parent.TrimEnd('/') == "/Volumes")
        {
            problem = $"\"{full}\" is a drive that isn't connected";
            return false;
        }
        try
        {
            Directory.CreateDirectory(full);
            created = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"\"{full}\" is missing and couldn't be created again: {ex.Message}";
            return false;
        }
    }
}
