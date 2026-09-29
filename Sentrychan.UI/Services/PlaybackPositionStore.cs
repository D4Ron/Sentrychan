using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentrychan.Core;

namespace Sentrychan.UI.Services;

/// <summary>
/// Where each library file was left off, so playback resumes. Keyed by a hash of the path,
/// not the path itself, so this file names no show. (Vault videos keep their position in
/// the vault's encrypted index instead.)
/// </summary>
public static class PlaybackPositionStore
{
    private static readonly string FilePath = AppPaths.Combine("playback.json");

    private static readonly object Gate = new();
    private static Dictionary<string, double[]>? _positions;

    private static string Key(string path) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));

    private static Dictionary<string, double[]> Positions
    {
        get
        {
            if (_positions != null) return _positions;
            try
            {
                _positions = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { _positions = new(); }
            return _positions;
        }
    }

    /// <summary>(position, duration) in seconds, or null if never played.</summary>
    public static (double Position, double Duration)? Get(string path)
    {
        lock (Gate)
            return Positions.TryGetValue(Key(path), out var v) && v.Length == 2 ? (v[0], v[1]) : null;
    }

    public static void Set(string path, double position, double duration)
    {
        lock (Gate)
        {
            Positions[Key(path)] = [position, duration];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Positions));
            }
            catch { /* resume is a convenience */ }
        }
    }

    /// <summary>Carries saved positions over to files that were renamed or moved (Tidy library, and its undo).</summary>
    public static void Move(IEnumerable<(string From, string To)> moves)
    {
        lock (Gate)
        {
            var changed = false;
            foreach (var (from, to) in moves)
            {
                if (!Positions.Remove(Key(from), out var v)) continue;
                Positions[Key(to)] = v;
                changed = true;
            }
            if (!changed) return;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Positions)); }
            catch { /* resume is a convenience */ }
        }
    }
}
