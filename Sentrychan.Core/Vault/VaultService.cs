using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Vault;

public enum VaultKind { Video, Page, File }

public sealed class VaultEntry
{
    public string Id { get; set; } = string.Empty;
    public VaultKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Groups entries that belong together, e.g. the pages of one chapter.</summary>
    public string? Collection { get; set; }
    public int Order { get; set; }
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime AddedAt { get; set; }
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
}

/// <summary>
/// What a collection of pages is — a downloaded chapter or an imported album. Kept in the
/// encrypted index, so it may name the title freely.
/// </summary>
public sealed class VaultCollectionInfo
{
    public string Title { get; set; } = string.Empty;

    /// <summary>What the collection belongs to — the manga title, or the imported folder.</summary>
    public string? Group { get; set; }

    /// <summary>Chapter number when known; imported albums sort by title instead.</summary>
    public double? Sort { get; set; }

    public int LastPage { get; set; }
    public DateTime AddedAt { get; set; }

    /// <summary>How many pages a downloaded chapter should have; more than are stored means it's incomplete.</summary>
    public int? ExpectedPages { get; set; }
}

/// <summary>
/// Encrypted storage for Sentrykun content.
///
/// Files live under a hidden folder as random, extensionless names: Explorer shows no
/// thumbnails, Windows Search can't index them and no other app can open them. What each
/// file is — its title, which chapter it belongs to, where playback stopped — lives only in
/// an index encrypted with the same key, never in the database or the logs.
///
/// The key is 256 random bits, kept on disk protected by Windows (DPAPI) for the current
/// user, so opening the vault inside the app needs no password. That also means it opens
/// only for this Windows account on this machine — hence the passphrase-protected key
/// backup. This guards against someone browsing the PC; it is not forensic-grade security.
/// </summary>
public sealed class VaultService
{
    /// <summary>The AppConfigs key that pins the vault's location once it has been created.</summary>
    public const string RootConfigKey = "VaultRoot";
    private static readonly byte[] DpapiEntropy = "Sentrychan.vault.v1"u8.ToArray();

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<VaultService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _indexLock = new();

    private readonly string _keyPath = AppPaths.VaultKey;

    private byte[]? _key;
    private string _root = string.Empty;
    private Index _index = new();
    // Read from logging on any thread while downloads add to it — must be concurrent.
    private System.Collections.Concurrent.ConcurrentDictionary<string, byte> _private = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public VaultService(IDbContextFactory<AppDbContext> dbFactory, ILogger<VaultService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        Privacy.IsPrivate = key => !string.IsNullOrEmpty(key) && _private.ContainsKey(key);
    }

    private sealed class Index
    {
        public List<VaultEntry> Entries { get; set; } = [];
        public List<string> PrivateDownloads { get; set; } = [];
        public Dictionary<string, VaultCollectionInfo> Collections { get; set; } = new();
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, byte> NewPrivateSet(IEnumerable<string> keys) =>
        new(keys.Select(k => KeyValuePair.Create(k, (byte)0)), StringComparer.OrdinalIgnoreCase);

    public bool IsReady => _key != null;
    public string RootPath => _root;

    public IReadOnlyList<VaultEntry> Entries
    {
        get { lock (_indexLock) return _index.Entries.ToList(); }
    }

    public IReadOnlyList<VaultEntry> Collection(string collection)
    {
        lock (_indexLock)
            return _index.Entries.Where(e => e.Collection == collection).OrderBy(e => e.Order).ToList();
    }

    public VaultEntry? Get(string id)
    {
        lock (_indexLock) return _index.Entries.FirstOrDefault(e => e.Id == id);
    }

    public VaultCollectionInfo? GetCollectionInfo(string collection)
    {
        lock (_indexLock) return _index.Collections.GetValueOrDefault(collection);
    }

    /// <summary>Names a collection (a chapter or an album). Creates or replaces its description.</summary>
    public async Task DescribeCollectionAsync(
        string collection, string title, string? group, double? sort, bool save = true, CancellationToken ct = default,
        int? expectedPages = null)
    {
        await EnsureReadyAsync(ct);
        lock (_indexLock)
        {
            var info = _index.Collections.GetValueOrDefault(collection);
            if (info == null) _index.Collections[collection] = info = new VaultCollectionInfo { AddedAt = DateTime.UtcNow };
            info.Title = title;
            info.Group = group;
            info.Sort = sort;
            if (expectedPages != null) info.ExpectedPages = expectedPages;
        }
        if (save) await SaveIndexAsync(ct);
    }

    /// <summary>Remembers the page a reader stopped on.</summary>
    public async Task SaveCollectionPageAsync(string collection, int page)
    {
        lock (_indexLock)
        {
            if (!_index.Collections.TryGetValue(collection, out var info) || info.LastPage == page) return;
            info.LastPage = page;
        }
        try { await SaveIndexAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "[Vault] page save failed"); }
    }

    /// <summary>Writes the index now — after a run of additions made with <c>save: false</c>.</summary>
    public Task FlushAsync(CancellationToken ct = default) => IsReady ? SaveIndexAsync(ct) : Task.CompletedTask;

    // ── Loading ───────────────────────────────────────────────────────

    /// <summary>Opens (or creates) the vault. Safe to call repeatedly.</summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_key != null) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_key != null) return;

            _root = await ResolveRootAsync(ct);
            Directory.CreateDirectory(_root);
            try
            {
                var dir = new DirectoryInfo(_root);
                dir.Attributes |= FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed;
            }
            catch { /* attributes are cosmetic */ }

            var key = LoadOrCreateKey();
            var indexPath = Path.Combine(_root, "index");
            _index = File.Exists(indexPath)
                ? JsonSerializer.Deserialize<Index>(VaultFormat.Decrypt(await File.ReadAllBytesAsync(indexPath, ct), key)) ?? new()
                : new Index();

            _private = NewPrivateSet(_index.PrivateDownloads);
            _key = key;
            _logger.LogInformation("[Vault] Ready ({Count} item(s))", _index.Entries.Count);
        }
        finally { _gate.Release(); }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
            return ProtectedData.Unprotect(File.ReadAllBytes(_keyPath), DpapiEntropy, DataProtectionScope.CurrentUser);

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllBytes(_keyPath, ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.CurrentUser));
        return key;
    }

    /// <summary>
    /// Where the vault lives, fixed the first time it's created so a later Library Path
    /// change doesn't strand it. Next to the library by default: that's the drive with room
    /// for video.
    /// </summary>
    private async Task<string> ResolveRootAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stored = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == RootConfigKey, ct);
        if (!string.IsNullOrWhiteSpace(stored?.Value)) return stored.Value;

        var library = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "LibraryPath", ct))?.Value;
        var root = DefaultRoot(library, AppPaths.DataDir, BuildInfo.IsPreview);

        db.AppConfigs.Add(new AppConfig { Key = RootConfigKey, Value = root });
        await db.SaveChangesAsync(ct);
        return root;
    }

    /// <summary>
    /// Where a new vault goes: a hidden folder in the library, else inside the data directory.
    /// The preview gets its own folder name because stable and preview usually share a library
    /// but never a key — two apps writing one encrypted index would each lock the other out.
    /// </summary>
    public static string DefaultRoot(string? libraryPath, string dataDir, bool isPreview) =>
        !string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath)
            ? Path.Combine(libraryPath, isPreview ? ".cache-preview" : ".cache")
            : Path.Combine(dataDir, "cache", "store");

    private byte[] Key => _key ?? throw new InvalidOperationException("Vault not opened.");

    private string BlobPath(string id) => Path.Combine(_root, id[..2], id);

    private async Task SaveIndexAsync(CancellationToken ct = default)
    {
        byte[] json;
        lock (_indexLock)
        {
            _index.PrivateDownloads = _private.Keys.ToList();
            json = JsonSerializer.SerializeToUtf8Bytes(_index);
        }
        var path = Path.Combine(_root, "index");
        var tmp = path + ".tmp";
        await _saveGate.WaitAsync(ct);
        try
        {
            await File.WriteAllBytesAsync(tmp, VaultFormat.Encrypt(json, Key), ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _saveGate.Release(); }
        Changed?.Invoke();
    }

    // ── Adding ────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts a file into the vault. With <paramref name="deleteSource"/> the plaintext is
    /// removed once the encrypted copy is safely written.
    /// </summary>
    public async Task<VaultEntry> AddFileAsync(
        string path, string title, VaultKind kind, string? collection = null, int order = 0,
        bool deleteSource = true, IProgress<double>? progress = null, CancellationToken ct = default,
        bool save = true)
    {
        await EnsureReadyAsync(ct);
        var entry = NewEntry(title, kind, collection, order, Path.GetExtension(path));

        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            entry.Size = input.Length;
            await WriteBlobAsync(entry.Id, output => VaultFormat.EncryptAsync(input, input.Length, output, Key, progress, ct));
        }

        await CommitAsync(entry, save, ct);
        if (deleteSource)
        {
            try { File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Vault] Encrypted, but couldn't remove the original"); }
        }
        return entry;
    }

    public async Task<VaultEntry> AddBytesAsync(
        byte[] data, string title, VaultKind kind, string? collection, int order, string extension,
        CancellationToken ct = default, bool save = true)
    {
        await EnsureReadyAsync(ct);
        var entry = NewEntry(title, kind, collection, order, extension);
        entry.Size = data.Length;
        await WriteBlobAsync(entry.Id, output => output.WriteAsync(VaultFormat.Encrypt(data, Key), ct).AsTask());
        await CommitAsync(entry, save, ct);
        return entry;
    }

    private static VaultEntry NewEntry(string title, VaultKind kind, string? collection, int order, string extension) => new()
    {
        Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        Kind = kind,
        Title = title,
        Collection = collection,
        Order = order,
        Extension = extension,
        AddedAt = DateTime.UtcNow,
    };

    private async Task WriteBlobAsync(string id, Func<Stream, Task> write)
    {
        var path = BlobPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            await write(output);
        File.Move(tmp, path, overwrite: true);
    }

    // Rewriting the whole encrypted index per page made a 500-page chapter write it 500
    // times; bulk callers pass save: false and flush once per chapter or album.
    private async Task CommitAsync(VaultEntry entry, bool save, CancellationToken ct)
    {
        lock (_indexLock) _index.Entries.Add(entry);
        if (save) await SaveIndexAsync(ct);
    }

    // ── Reading ───────────────────────────────────────────────────────

    /// <summary>Seekable plaintext of an entry — hand it straight to a player.</summary>
    public Stream OpenRead(string id) =>
        new VaultReadStream(new FileStream(BlobPath(id), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), Key);

    public async Task<byte[]?> ReadAllAsync(string id, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        var path = BlobPath(id);
        if (!File.Exists(path)) return null;
        return await Task.Run(() => VaultFormat.Decrypt(File.ReadAllBytes(path), Key), ct);
    }

    // ── Changing ──────────────────────────────────────────────────────

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        lock (_indexLock) _index.Entries.RemoveAll(e => e.Id == id);
        try { File.Delete(BlobPath(id)); } catch { /* orphaned blob is unreadable anyway */ }
        await SaveIndexAsync(ct);
    }

    public async Task RemoveCollectionAsync(string collection, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        List<VaultEntry> gone;
        lock (_indexLock)
        {
            gone = _index.Entries.Where(e => e.Collection == collection).ToList();
            _index.Entries.RemoveAll(e => e.Collection == collection);
            _index.Collections.Remove(collection);
        }
        foreach (var e in gone)
            try { File.Delete(BlobPath(e.Id)); } catch { }
        await SaveIndexAsync(ct);
    }

    /// <summary>Keys of collections described with this group and title.</summary>
    public IReadOnlyList<string> FindCollections(string group, string title)
    {
        lock (_indexLock)
            return _index.Collections.Where(kv => kv.Value.Group == group && kv.Value.Title == title).Select(kv => kv.Key).ToList();
    }

    /// <summary>Moves a collection's pages and description to a new key. The blobs don't move.</summary>
    public async Task RekeyCollectionAsync(string from, string to, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        lock (_indexLock)
        {
            foreach (var e in _index.Entries.Where(e => e.Collection == from)) e.Collection = to;
            if (_index.Collections.Remove(from, out var info)) _index.Collections[to] = info;
        }
        await SaveIndexAsync(ct);
    }

    public async Task RenameAsync(string id, string title, CancellationToken ct = default)
    {
        var e = Get(id);
        if (e == null) return;
        e.Title = title;
        await SaveIndexAsync(ct);
    }

    public async Task SavePositionAsync(string id, double position, double duration)
    {
        var e = Get(id);
        if (e == null) return;
        e.PositionSeconds = position;
        e.DurationSeconds = duration;
        try { await SaveIndexAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "[Vault] position save failed"); }
    }

    // ── Private downloads ─────────────────────────────────────────────
    // A download started in secret mode is recorded here (encrypted), not in the database,
    // so the pipeline knows to encrypt it on arrival instead of filing it in the library.

    public async Task MarkPrivateAsync(IEnumerable<string?> keys, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        var changed = false;
        foreach (var k in keys)
            if (!string.IsNullOrWhiteSpace(k)) changed |= _private.TryAdd(k, 0);
        if (changed) await SaveIndexAsync(ct);
    }

    public bool IsPrivateDownload(string? key) => !string.IsNullOrEmpty(key) && _private.ContainsKey(key);

    public async Task ForgetPrivateAsync(IEnumerable<string?> keys, CancellationToken ct = default)
    {
        var changed = false;
        foreach (var k in keys)
            if (!string.IsNullOrWhiteSpace(k)) changed |= _private.TryRemove(k, out _);
        if (changed) await SaveIndexAsync(ct);
    }

    // ── Key backup ────────────────────────────────────────────────────
    // The DPAPI-protected key only opens for this Windows user on this machine. A backup
    // wrapped with the user's own passphrase is what survives a reinstall or a new PC.

    public async Task ExportKeyAsync(string path, string passphrase, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrap = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 300_000, HashAlgorithmName.SHA256, 32);
        var sealedKey = VaultFormat.Encrypt(Key, wrap);
        var payload = new byte[4 + salt.Length + sealedKey.Length];
        "SCK1"u8.CopyTo(payload);
        salt.CopyTo(payload, 4);
        sealedKey.CopyTo(payload, 4 + salt.Length);
        await File.WriteAllBytesAsync(path, payload, ct);
    }

    /// <summary>
    /// Restores a key backup. Accepted only if it actually opens this vault's index (or the
    /// vault is still empty), so a wrong backup can't lock the user out of what's there.
    /// </summary>
    public async Task<bool> ImportKeyAsync(string path, string passphrase, CancellationToken ct = default)
    {
        var payload = await File.ReadAllBytesAsync(path, ct);
        if (payload.Length < 20 || !payload.AsSpan(0, 4).SequenceEqual("SCK1"u8)) return false;

        byte[] key;
        try
        {
            var wrap = Rfc2898DeriveBytes.Pbkdf2(passphrase, payload.AsSpan(4, 16), 300_000, HashAlgorithmName.SHA256, 32);
            key = VaultFormat.Decrypt(payload[20..], wrap);
        }
        catch (CryptographicException) { return false; }

        if (string.IsNullOrEmpty(_root)) _root = await ResolveRootAsync(ct);
        var indexPath = Path.Combine(_root, "index");
        Index index = new();
        if (File.Exists(indexPath))
        {
            try { index = JsonSerializer.Deserialize<Index>(VaultFormat.Decrypt(await File.ReadAllBytesAsync(indexPath, ct), key)) ?? new(); }
            catch (CryptographicException) { return false; }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        await File.WriteAllBytesAsync(_keyPath, ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.CurrentUser), ct);
        _key = key;
        _index = index;
        _private = NewPrivateSet(index.PrivateDownloads);
        Changed?.Invoke();
        return true;
    }

    /// <summary>A human-readable total for the vault page.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        0           => "0 KB",
        _           => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
