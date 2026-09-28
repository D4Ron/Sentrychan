using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Sentrychan.Core.Vault;

/// <summary>An album to import: the images of one folder, or of one .cbz/.zip archive.</summary>
public sealed record VaultAlbumPlan(string Title, string? Group, IReadOnlyList<string> Images, string? Archive = null)
{
    public int PageCount => Archive == null ? Images.Count : ArchivePageCount;
    internal int ArchivePageCount { get; init; }
}

/// <summary>What an import would do, shown to the user before anything is touched.</summary>
public sealed class VaultImportPlan
{
    public string? Root { get; init; }
    public List<string> Videos { get; } = [];
    public List<VaultAlbumPlan> Albums { get; } = [];
    public long TotalBytes { get; set; }

    public int PageCount => Albums.Sum(a => a.PageCount);
    public bool IsEmpty => Videos.Count == 0 && Albums.Count == 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Videos.Count > 0) parts.Add($"{Videos.Count} video{(Videos.Count == 1 ? "" : "s")}");
        if (Albums.Count > 0) parts.Add($"{Albums.Count} album{(Albums.Count == 1 ? "" : "s")} ({PageCount} images)");
        return parts.Count == 0 ? "nothing to import" : $"{string.Join(" and ", parts)}, {VaultService.FormatSize(TotalBytes)}";
    }
}

public sealed record VaultImportResult(int Videos, int Albums, int Pages, IReadOnlyList<string> Errors);

/// <summary>
/// Brings media already on disk into the vault: videos become playable entries, every folder
/// of images (and every .cbz/.zip) becomes an album the reader can open. Plan first, then
/// import, so the user sees what will be encrypted — and removed — before it happens.
/// </summary>
public sealed class VaultImporter
{
    public static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov", ".wmv"];
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif"];
    public static readonly string[] ArchiveExtensions = [".cbz", ".zip"];

    private readonly VaultService _vault;

    public VaultImporter(VaultService vault) => _vault = vault;

    private static bool Has(string[] exts, string path) => exts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ── Planning ──────────────────────────────────────────────────────

    /// <summary>Plans importing everything under a folder, subfolders included.</summary>
    public VaultImportPlan PlanFolder(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var files = new List<string>();
        Walk(root, files);
        return Plan(files, root);
    }

    /// <summary>Plans importing hand-picked files. Loose images become one album per folder.</summary>
    public VaultImportPlan PlanFiles(IEnumerable<string> files) => Plan(files.ToList(), root: null);

    private void Walk(string dir, List<string> files)
    {
        // Never walk into the vault itself — it sits inside the library, which a user may pick.
        if (IsInsideVault(dir)) return;
        try { files.AddRange(Directory.EnumerateFiles(dir)); } catch (UnauthorizedAccessException) { return; }
        foreach (var sub in SafeDirs(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.')) continue; // .cache, .mt_cache, .git …
            try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.System)) continue; } catch { continue; }
            Walk(sub, files);
        }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); } catch { return []; }
    }

    private bool IsInsideVault(string path) =>
        !string.IsNullOrEmpty(_vault.RootPath) &&
        (path + Path.DirectorySeparatorChar).StartsWith(_vault.RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private VaultImportPlan Plan(List<string> files, string? root)
    {
        var plan = new VaultImportPlan { Root = root };
        var rootName = root == null ? null : Path.GetFileName(root);

        string? GroupFor(string dir)
        {
            if (root == null) return Path.GetFileName(dir);
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) return rootName;
            var parent = Path.GetDirectoryName(dir)!;
            return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) ? rootName : Path.GetFileName(parent);
        }

        foreach (var f in files.Where(f => !IsInsideVault(f)))
        {
            if (Has(VideoExtensions, f)) plan.Videos.Add(f);
            else if (Has(ArchiveExtensions, f))
            {
                var pages = CountArchiveImages(f);
                if (pages == 0) continue;
                plan.Albums.Add(new VaultAlbumPlan(Path.GetFileNameWithoutExtension(f), GroupFor(Path.GetDirectoryName(f)!), [], f)
                    { ArchivePageCount = pages });
            }
            else continue;
            plan.TotalBytes += SafeLength(f);
        }

        foreach (var folder in files.Where(f => Has(ImageExtensions, f) && !IsInsideVault(f))
                     .GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
        {
            var images = folder.OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
            plan.Albums.Add(new VaultAlbumPlan(Path.GetFileName(folder.Key), GroupFor(folder.Key), images));
            plan.TotalBytes += images.Sum(SafeLength);
        }

        plan.Videos.Sort((a, b) => NaturalComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        return plan;
    }

    private static long SafeLength(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    private static int CountArchiveImages(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count(e => e.Length > 0 && Has(ImageExtensions, e.FullName));
        }
        catch { return 0; } // not a readable zip — leave it where it is
    }

    // ── Importing ─────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts everything in the plan. With <paramref name="deleteSource"/> each original is
    /// removed once its encrypted copy is written, and folders left empty are removed too.
    /// A file that fails is left untouched and reported; the rest carry on.
    /// </summary>
    public async Task<VaultImportResult> ImportAsync(
        VaultImportPlan plan, bool deleteSource, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _vault.EnsureReadyAsync(ct);
        var errors = new List<string>();
        int videos = 0, albums = 0, pages = 0;
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < plan.Videos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = plan.Videos[i];
            var n = i + 1;
            try
            {
                await _vault.AddFileAsync(path, Path.GetFileNameWithoutExtension(path), VaultKind.Video,
                    deleteSource: deleteSource, ct: ct,
                    progress: new Progress<double>(p => progress?.Report($"Video {n} of {plan.Videos.Count} — {p:P0}")));
                videos++;
                touchedDirs.Add(Path.GetDirectoryName(path)!);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }

        for (int a = 0; a < plan.Albums.Count; a++)
        {
            ct.ThrowIfCancellationRequested();
            var album = plan.Albums[a];
            var key = "album-" + Guid.NewGuid().ToString("N");
            var label = $"Album {a + 1} of {plan.Albums.Count}";
            try
            {
                await _vault.DescribeCollectionAsync(key, album.Title, album.Group, ChapterNumber(album.Title), save: false, ct: ct);
                pages += album.Archive != null
                    ? await ImportArchiveAsync(album.Archive, key, label, progress, ct)
                    : await ImportImagesAsync(album.Images, key, deleteSource, label, progress, ct);
                await _vault.FlushAsync(ct);
                albums++;

                if (album.Archive != null)
                {
                    if (deleteSource) TryDelete(album.Archive);
                    touchedDirs.Add(Path.GetDirectoryName(album.Archive)!);
                }
                else if (album.Images.Count > 0) touchedDirs.Add(Path.GetDirectoryName(album.Images[0])!);
            }
            catch (OperationCanceledException) { await _vault.FlushAsync(CancellationToken.None); throw; }
            catch (Exception ex)
            {
                // Half an album is worse than none: drop what was added and keep the originals.
                errors.Add($"{album.Title}: {ex.Message}");
                try { await _vault.RemoveCollectionAsync(key, CancellationToken.None); } catch { }
            }
        }

        if (deleteSource) RemoveEmptyFolders(touchedDirs, plan.Root);
        return new VaultImportResult(videos, albums, pages, errors);
    }

    private async Task<int> ImportImagesAsync(
        IReadOnlyList<string> images, string key, bool deleteSource, string label, IProgress<string>? progress, CancellationToken ct)
    {
        // Encrypt every page before removing any original, so a failure part-way through
        // leaves the folder whole.
        for (int i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await _vault.AddFileAsync(images[i], $"Page {i + 1}", VaultKind.Page, key, i, deleteSource: false, ct: ct, save: false);
            progress?.Report($"{label} — page {i + 1} of {images.Count}");
        }
        if (deleteSource) foreach (var f in images) TryDelete(f);
        return images.Count;
    }

    private async Task<int> ImportArchiveAsync(string path, string key, string label, IProgress<string>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(e => e.Length > 0 && Has(ImageExtensions, e.FullName))
            .OrderBy(e => e.FullName, NaturalComparer.Instance).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream((int)Math.Min(entries[i].Length, int.MaxValue));
            await using (var s = entries[i].Open()) await s.CopyToAsync(ms, ct);
            await _vault.AddBytesAsync(ms.ToArray(), $"Page {i + 1}", VaultKind.Page, key, i,
                Path.GetExtension(entries[i].FullName), ct, save: false);
            progress?.Report($"{label} — page {i + 1} of {entries.Count}");
        }
        return entries.Count;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* reported by the folder staying */ } }

    /// <summary>Removes folders the import emptied, deepest first, up to and including the picked folder.</summary>
    private static void RemoveEmptyFolders(IEnumerable<string> dirs, string? root)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            // Walk up to the picked folder; without one, only the files' own folders.
            for (var cur = d; cur != null; cur = Path.GetDirectoryName(cur))
            {
                all.Add(cur);
                if (root == null || string.Equals(cur, root, StringComparison.OrdinalIgnoreCase)) break;
                if (!cur.StartsWith(root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        foreach (var d in all.OrderByDescending(d => d.Length))
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
    }

    private static double? ChapterNumber(string title)
    {
        var m = Regex.Match(title, @"(\d+(?:\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

/// <summary>"Page 2" before "Page 10" — digits compare as numbers.</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
