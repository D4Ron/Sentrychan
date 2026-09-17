using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sentrychan.UI.Controls;

public class AsyncImage : Image
{
    private static readonly HttpClient _http = CreateHttp();

    // ── Bounded LRU bitmap cache ────────────────────────────────────
    // Decoded pages are large (a 1280×1809 page ≈ 9 MB in RAM), so an uncapped cache
    // could balloon over a long read. Evict least-recently-used entries by estimated
    // byte size, not count. Concurrent because Prefetch writes from background threads.
    private sealed class CacheEntry { public Bitmap Bitmap = null!; public long Tick; public long Bytes; }
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static long _tick;
    private static long _cacheBytes;
    private const long MaxCacheBytes = 256L * 1024 * 1024; // ~256 MB ceiling
    private const long TrimToBytes   = 192L * 1024 * 1024; // trim back down to this
    private static readonly object _trimLock = new();

    private static bool TryGetCached(string key, out Bitmap bmp)
    {
        if (_cache.TryGetValue(key, out var e))
        {
            e.Tick = Interlocked.Increment(ref _tick); // mark most-recently-used
            bmp = e.Bitmap;
            return true;
        }
        bmp = null!;
        return false;
    }

    private static void AddCached(string key, Bitmap bmp)
    {
        long bytes;
        try { var s = bmp.PixelSize; bytes = (long)s.Width * s.Height * 4; }
        catch { bytes = 4L * 1024 * 1024; }

        if (_cache.TryGetValue(key, out var old)) Interlocked.Add(ref _cacheBytes, -old.Bytes);
        _cache[key] = new CacheEntry { Bitmap = bmp, Tick = Interlocked.Increment(ref _tick), Bytes = bytes };
        Interlocked.Add(ref _cacheBytes, bytes);

        if (Interlocked.Read(ref _cacheBytes) > MaxCacheBytes) Trim();
    }

    private static void Trim()
    {
        lock (_trimLock)
        {
            if (Interlocked.Read(ref _cacheBytes) <= MaxCacheBytes) return;
            // Drop oldest-touched entries first. Evicted bitmaps aren't disposed — a
            // currently-displayed one stays alive via its control's Source, and GC
            // reclaims the rest once nothing references them.
            foreach (var kv in _cache.OrderBy(kv => kv.Value.Tick))
            {
                if (Interlocked.Read(ref _cacheBytes) <= TrimToBytes) break;
                if (_cache.TryRemove(kv.Key, out var removed))
                    Interlocked.Add(ref _cacheBytes, -removed.Bytes);
            }
        }
    }

    // ── Persistent disk cache (survives restarts) ───────────────────
    // A remote image fetched once is written to disk so a later session loads it
    // instantly instead of re-downloading — the same win anime posters already get
    // from PosterPath, extended to manga covers/pages (and any remote image). Kept in
    // its OWN directory (not anime's ImageCache) so size-eviction here can never delete
    // a file an anime Series.PosterPath points at. Bounded by total size, oldest-first.
    private static readonly string _diskCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sentrychan", "cache", "images");
    private const long MaxDiskBytes = 600L * 1024 * 1024; // ~600 MB ceiling
    private const long DiskTrimTo   = 450L * 1024 * 1024; // trim back down to this
    private static int _writesSinceTrim;
    private static readonly object _diskTrimLock = new();

    private static string? DiskPathFor(string url)
    {
        try
        {
            var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)));
            var ext = Path.GetExtension(url.Split('?')[0]);
            if (ext.Length is 0 or > 5) ext = ".img";
            return Path.Combine(_diskCacheDir, hash + ext);
        }
        catch { return null; }
    }

    private static async Task WriteDiskCacheAsync(string url, byte[] bytes)
    {
        try
        {
            var path = DiskPathFor(url);
            if (path == null || File.Exists(path)) return;
            Directory.CreateDirectory(_diskCacheDir);
            // Write-then-move so a killed process can't leave a torn (half-decoded) file.
            var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            if (Interlocked.Increment(ref _writesSinceTrim) >= 40) _ = Task.Run(TrimDisk);
        }
        catch { /* disk cache is best-effort — the memory cache still served this load */ }
    }

    private static void TrimDisk()
    {
        lock (_diskTrimLock)
        {
            try
            {
                Interlocked.Exchange(ref _writesSinceTrim, 0);
                var dir = new DirectoryInfo(_diskCacheDir);
                if (!dir.Exists) return;
                var files = dir.GetFiles();
                long total = files.Sum(f => f.Length);
                if (total <= MaxDiskBytes) return;
                // Least-recently-accessed out first.
                foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
                {
                    if (total <= DiskTrimTo) break;
                    try { total -= f.Length; f.Delete(); } catch { /* in use — skip */ }
                }
            }
            catch { /* best effort */ }
        }
    }

    private static HttpClient CreateHttp()
    {
        // A User-Agent is REQUIRED by some image CDNs — MangaDex's cover host returns
        // HTTP 400 without one, which was silently blanking every manga thumbnail.
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/1.0");
        return c;
    }

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<AsyncImage, string?>(nameof(Url));

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>Source name of a manga image, used to look up a required Referer header.</summary>
    public static readonly StyledProperty<string?> SourceNameProperty =
        AvaloniaProperty.Register<AsyncImage, string?>(nameof(SourceName));

    public string? SourceName
    {
        get => GetValue(SourceNameProperty);
        set => SetValue(SourceNameProperty, value);
    }

    // Seeded at startup from the manga source registry: source name → Referer header
    // its image CDN needs (e.g. MangaPill's hotlink-protected CDN 403s without it).
    private static readonly Dictionary<string, string> _refererBySource =
        new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterReferer(string sourceName, string referer) =>
        _refererBySource[sourceName] = referer;

    /// <summary>
    /// True while the current Url is being fetched/decoded (and not a cache hit). The
    /// reader binds a spinner to this so a not-yet-loaded page shows a loader instead
    /// of a blank or stale image.
    /// </summary>
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<AsyncImage, bool>(nameof(IsLoading));

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        private set => SetValue(IsLoadingProperty, value);
    }

    static AsyncImage()
    {
        UrlProperty.Changed.AddClassHandler<AsyncImage>((img, e) =>
            img.LoadImageAsync(e.NewValue as string));
    }

    private static string Normalize(string url) =>
        url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? url.Substring(7).Replace("/", "\\")
            : url;

    private async void LoadImageAsync(string? url)
    {
        StopGif(); // stop any animation owned by the previous url

        if (string.IsNullOrEmpty(url))
        {
            Source = null;
            IsLoading = false;
            return;
        }

        var normalizedUrl = Normalize(url);
        var isGif = LooksLikeGif(normalizedUrl);

        // Cache hit → show instantly. GIFs skip the still-frame cache so they can animate.
        if (!isGif && TryGetCached(normalizedUrl, out var cached))
        {
            Source = cached;
            IsLoading = false;
            return;
        }

        // Not cached: clear the old image and show the loader immediately so a page
        // flip feels responsive instead of lingering on the previous page.
        Source = null;
        IsLoading = true;

        try
        {
            if (isGif)
            {
                var frames = await DecodeGifFramesAsync(normalizedUrl, SourceName);
                if (Url != url) { DisposeFrames(frames); return; } // stale
                if (frames is { Count: > 1 }) StartGif(frames);
                else if (frames is { Count: 1 }) Source = frames[0].Bitmap;
                else Source = await LoadBitmapAsync(normalizedUrl, SourceName); // not really animated
                IsLoading = false;
                return;
            }

            var bitmap = await LoadBitmapAsync(normalizedUrl, SourceName);

            // Stale-load guard: if the Url changed while we were loading (fast page
            // flipping), a newer request owns the control now — drop this result.
            if (Url != url) return;

            if (bitmap != null)
            {
                AddCached(normalizedUrl, bitmap);
                Source = bitmap;
            }
            IsLoading = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AsyncImage failed to load {url}: {ex.Message}");
            if (Url == url) { Source = null; IsLoading = false; }
        }
    }

    private static async Task<Bitmap?> LoadBitmapAsync(string normalizedUrl, string? sourceName)
    {
        var bytes = await LoadBytesAsync(normalizedUrl, sourceName);
        if (bytes == null) return null;
        // Decode off the UI thread — decoding full pages inline causes visible jank.
        return await Task.Run(() => { using var ms = new MemoryStream(bytes); return new Bitmap(ms); });
    }

    /// <summary>Raw image bytes from local file / persistent disk cache / network (referer-aware).</summary>
    private static async Task<byte[]?> LoadBytesAsync(string normalizedUrl, string? sourceName)
    {
        if (File.Exists(normalizedUrl))
            return await File.ReadAllBytesAsync(normalizedUrl);

        if (!normalizedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        // Persistent disk hit → skip the network entirely.
        var diskPath = DiskPathFor(normalizedUrl);
        if (diskPath != null && File.Exists(diskPath))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(diskPath);
                try { File.SetLastAccessTimeUtc(diskPath, DateTime.UtcNow); } catch { /* touch is optional */ }
                return cached;
            }
            catch { /* corrupt/locked cache file → fall through and re-download */ }
        }

        byte[] bytes;
        if (!string.IsNullOrEmpty(sourceName) && _refererBySource.TryGetValue(sourceName, out var referer))
        {
            // Some source CDNs (MangaPill) hotlink-protect: send their Referer.
            using var req = new HttpRequestMessage(HttpMethod.Get, normalizedUrl);
            req.Headers.Referrer = new Uri(referer);
            using var resp = await _http.SendAsync(req);
            bytes = await resp.Content.ReadAsByteArrayAsync();
        }
        else
        {
            bytes = await _http.GetByteArrayAsync(normalizedUrl);
        }

        _ = WriteDiskCacheAsync(normalizedUrl, bytes); // persist for next session (best-effort)
        return bytes;
    }

    /// <summary>
    /// Warms the cache for a URL without any display target — used by the reader to
    /// preload upcoming pages so forward navigation is instant. No-op if already cached.
    /// </summary>
    public static void Prefetch(string? url, string? sourceName)
    {
        if (string.IsNullOrEmpty(url)) return;
        var normalized = Normalize(url);
        // GIFs animate from bytes on display and skip the still-frame cache — just warm the disk.
        if (LooksLikeGif(normalized)) { _ = LoadBytesAsync(normalized, sourceName); return; }
        if (_cache.ContainsKey(normalized)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var bmp = await LoadBitmapAsync(normalized, sourceName);
                if (bmp != null) AddCached(normalized, bmp);
            }
            catch { /* prefetch is best-effort */ }
        });
    }

    // ── Animated GIF support ─────────────────────────────────────────
    // Avalonia's Bitmap only decodes a GIF's first frame, so animated pages (common on
    // e-hentai) sat frozen. Decode all frames with Skia (the same engine Avalonia renders
    // with) and cycle them on a per-frame timer. GIFs are detected by URL extension.
    private readonly record struct GifFrame(Bitmap Bitmap, int DelayMs);
    private const int MaxGifFrames = 600;

    private List<GifFrame>? _gifFrames;
    private int _gifIndex;
    private DispatcherTimer? _gifTimer;

    private static bool LooksLikeGif(string url) =>
        url.Split('?')[0].EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    private static async Task<List<GifFrame>?> DecodeGifFramesAsync(string url, string? sourceName)
    {
        var bytes = await LoadBytesAsync(url, sourceName);
        return bytes == null ? null : await Task.Run(() => DecodeGifFrames(bytes));
    }

    private static List<GifFrame>? DecodeGifFrames(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;

            var count = codec.FrameCount;
            var src = codec.Info;
            var info = new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var infos = codec.FrameInfo;
            var len = info.RowBytes * info.Height;

            using var running = new SKBitmap(info); // accumulating canvas (handles frame disposal)
            var frames = new List<GifFrame>(Math.Min(Math.Max(count, 1), MaxGifFrames));

            for (int i = 0; i < count && i < MaxGifFrames; i++)
            {
                var opts = i == 0 ? new SKCodecOptions(0) : new SKCodecOptions(i, i - 1);
                var res = codec.GetPixels(info, running.GetPixels(), opts);
                if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput) break;

                // Snapshot the current canvas into an Avalonia bitmap.
                var buffer = new byte[len];
                Marshal.Copy(running.GetPixels(), buffer, 0, len);
                var wb = new WriteableBitmap(new PixelSize(info.Width, info.Height),
                    new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = wb.Lock()) Marshal.Copy(buffer, 0, fb.Address, len);

                var d = i < infos.Length ? infos[i].Duration : 100;
                frames.Add(new GifFrame(wb, d <= 0 ? 100 : Math.Max(20, d)));
            }
            return frames.Count > 0 ? frames : null;
        }
        catch { return null; }
    }

    private void StartGif(List<GifFrame> frames)
    {
        _gifFrames = frames;
        _gifIndex = 0;
        Source = frames[0].Bitmap;
        _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frames[0].DelayMs) };
        _gifTimer.Tick += GifTick;
        _gifTimer.Start();
    }

    private void GifTick(object? sender, EventArgs e)
    {
        if (_gifFrames is not { Count: > 1 }) return;
        _gifIndex = (_gifIndex + 1) % _gifFrames.Count;
        Source = _gifFrames[_gifIndex].Bitmap;
        if (_gifTimer != null) _gifTimer.Interval = TimeSpan.FromMilliseconds(_gifFrames[_gifIndex].DelayMs);
    }

    private void StopGif()
    {
        if (_gifTimer != null) { _gifTimer.Stop(); _gifTimer.Tick -= GifTick; _gifTimer = null; }
        var frames = _gifFrames;
        _gifFrames = null;
        if (frames != null)
        {
            if (Source is Bitmap) Source = null; // drop the displayed frame before disposing
            DisposeFrames(frames);
        }
    }

    private static void DisposeFrames(List<GifFrame>? frames)
    {
        if (frames == null) return;
        foreach (var f in frames) { try { f.Bitmap.Dispose(); } catch { /* ignore */ } }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopGif();
    }
}