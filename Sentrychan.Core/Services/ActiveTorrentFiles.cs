using System.Collections.Concurrent;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

/// <inheritdoc cref="IActiveTorrentFiles"/>
public class ActiveTorrentFiles : IActiveTorrentFiles
{
    // Paths are compared case-insensitively and normalised, since the watcher and
    // the torrent engine can spell the same file differently (relative segments,
    // trailing separators, casing).
    private readonly ConcurrentDictionary<string, byte> _paths =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string path)
    {
        var key = Normalise(path);
        if (key != null) _paths[key] = 0;
    }

    public void Unregister(string path)
    {
        var key = Normalise(path);
        if (key != null) _paths.TryRemove(key, out _);
    }

    public bool IsActive(string path)
    {
        var key = Normalise(path);
        if (key == null) return false;
        if (_paths.ContainsKey(key)) return true;

        // A multi-file torrent registers its containing directory, so also treat
        // anything underneath a registered directory as owned.
        foreach (var registered in _paths.Keys)
        {
            if (key.StartsWith(registered + Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? Normalise(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path;   // unparseable — fall back to the raw string
        }
    }
}
