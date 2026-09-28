using System.Security.Cryptography;
using MonoTorrent;

namespace Sentrychan.Core.Services;

/// <summary>
/// Tells a finished video file from one that merely has the right size.
///
/// Torrent engines pre-allocate the full file up front and fill pieces in as they arrive,
/// so an interrupted download sits on disk at full size, unlocked, with every piece that
/// never arrived still reading as zeros. That is exactly what got 16 half-finished episodes
/// moved into the library in September 2026: the app was closed mid-batch, and on the next
/// start the folder watcher saw full-size files nobody held open and filed them.
/// </summary>
public static class VideoIntegrity
{
    // Piece sizes for anime-sized torrents are 256 KiB – 4 MiB, and a missing piece is
    // always a whole piece. Probing 16 KiB every 256 KiB therefore lands inside every
    // missing piece, while reading only 1/16 of the file.
    private const int Stride = 256 * 1024;
    private const int Probe  = 16 * 1024;

    // Container headers can carry legitimate zero padding (Matroska Void elements), so
    // the first MiB is never judged.
    private const long SkipHead = 1024 * 1024;

    /// <summary>
    /// True if the file has a zero-filled 16 KiB block past its header — i.e. a region no
    /// writer ever filled. Compressed video never contains one, so this has no false
    /// positives on real episodes. Returns false for files it can't read.
    /// </summary>
    public static bool LooksIncomplete(string path, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                          bufferSize: 1, FileOptions.RandomAccess);
            var len = fs.Length;
            if (len == 0) return true;               // created, never written
            if (len < SkipHead + Probe) return false; // too small to judge

            var buf = new byte[Probe];
            // Stop one probe short of the end: the tail block may legitimately be short.
            for (long pos = SkipHead; pos + Probe <= len - Probe; pos += Stride)
            {
                ct.ThrowIfCancellationRequested();
                fs.Position = pos;
                fs.ReadExactly(buf);
                if (buf.AsSpan().IndexOfAnyExcept((byte)0) < 0) return true;
            }
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// The engine's cached copy of the torrent a file came from, found by file name.
    /// MonoTorrent keeps every added torrent's metadata in <c>.mt_cache/metadata</c>, which
    /// is what makes an exact check (and a resume that reuses the good pieces) possible.
    /// </summary>
    public static async Task<Dictionary<string, string>> CachedTorrentsByFileNameAsync(
        string downloadPath, CancellationToken ct = default)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(downloadPath, ".mt_cache", "metadata");
        if (!Directory.Exists(dir)) return index;

        foreach (var file in Directory.EnumerateFiles(dir, "*.torrent"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var t = await Torrent.LoadAsync(file);
                if (t.Files.Count == 1)
                    index[Path.GetFileName(t.Files[0].Path.ToString())] = file;
            }
            catch { /* unreadable metadata — skip it */ }
        }
        return index;
    }

    /// <summary>
    /// Fraction of a single-file torrent's pieces that hash correctly on disk — the same
    /// check a torrent client runs on "force recheck". 1.0 means the file is intact.
    /// </summary>
    public static double VerifiedFraction(string path, Torrent torrent, CancellationToken ct = default)
    {
        var hashes = torrent.CreatePieceHashes();
        var pieceLength = torrent.PieceLength;
        var count = torrent.PieceCount;
        var good = 0;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        var buf = new byte[pieceLength];
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var read = fs.ReadAtLeast(buf, pieceLength, throwOnEndOfStream: false);
            var sha1 = SHA1.HashData(buf.AsSpan(0, read));
            if (hashes.GetHash(i).V1Hash.Span.SequenceEqual(sha1)) good++;
        }
        return count == 0 ? 0 : (double)good / count;
    }
}
