using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sentrychan.UI.Services;

/// <summary>
/// Disk-backed memo of "release → cover image URL" lookups.
///
/// Resolving a secret-mode thumbnail costs a view-page scrape or a CDN probe. Those
/// answers never change for a given release, but the cache used to live only in a
/// static dictionary — so every app restart paid the full cost again and the Latest
/// page crawled on each cold load. Persisting it makes a revisit essentially instant.
///
/// Negative results are cached too (as null): "this release has no cover" is just as
/// expensive to discover and just as stable, and not caching it meant repeatedly
/// re-scraping the misses — the slowest rows.
/// </summary>
public static class ThumbUrlCache
{
    private static readonly ConcurrentDictionary<string, string?> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object SaveLock = new();
    private static bool _loaded;
    private static bool _dirty;

    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sentrychan", "cache", "thumb-urls.json");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (SaveLock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(CacheFile)) return;
                var json = File.ReadAllText(CacheFile);
                var map = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
                if (map == null) return;
                foreach (var (k, v) in map) Entries[k] = v;
            }
            catch { /* a corrupt cache is not worth failing over — start empty */ }
        }
    }

    /// <summary>True if we've already looked this key up before (hit or miss).</summary>
    public static bool TryGet(string key, out string? url)
    {
        EnsureLoaded();
        return Entries.TryGetValue(key, out url);
    }

    public static void Set(string key, string? url)
    {
        EnsureLoaded();
        Entries[key] = url;
        _dirty = true;
    }

    /// <summary>Writes the cache out. Called once a resolution pass finishes.</summary>
    public static void Flush()
    {
        if (!_dirty) return;
        lock (SaveLock)
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                var dir = Path.GetDirectoryName(CacheFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var snapshot = new Dictionary<string, string?>(Entries);
                // Write-then-move so an interrupted write can't leave a torn file.
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, CacheFile, overwrite: true);
            }
            catch { /* best effort — the in-memory cache still works this session */ }
        }
    }
}
