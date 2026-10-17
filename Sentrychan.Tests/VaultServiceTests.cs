using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using Sentrychan.Core.Vault;

namespace Sentrychan.Tests;

/// <summary>
/// The vault on a drive that misbehaves: one that stops answering must never make the vault look
/// empty, get an empty index saved over the real one, or lose originals the importer removed.
/// </summary>
[Collection(EnvironmentCollection.Name)] // points SENTRYCHAN_DATA_DIR at a temp folder (the vault key lives there)
public sealed class VaultServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-vault-").FullName;
    private readonly string? _dataDirBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public VaultServiceTests() => Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, Path.Combine(_root, "data"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, _dataDirBefore);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string VaultRoot => Path.Combine(_root, "Library", ".cache");

    private async Task<VaultService> NewVaultAsync(string? root = null)
    {
        var factory = new Factory(Path.Combine(_root, "t.db"));
        await using var db = factory.CreateDbContext();
        db.Database.Migrate();
        if (!await db.AppConfigs.AnyAsync(c => c.Key == VaultService.RootConfigKey))
        {
            db.AppConfigs.Add(new AppConfig { Key = VaultService.RootConfigKey, Value = root ?? VaultRoot });
            await db.SaveChangesAsync();
        }
        return new VaultService(factory, NullLogger<VaultService>.Instance);
    }

    private string Album(string name, int pages)
    {
        var dir = Path.Combine(_root, "To import", name);
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= pages; i++) File.WriteAllBytes(Path.Combine(dir, $"{i:00}.png"), [0x89, 0x50, 0x4E, 0x47, (byte)i]);
        return dir;
    }

    [Fact]
    public async Task An_import_removes_the_originals_and_the_vault_reopens_with_them()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        var vault = await NewVaultAsync();
        var source = Album("Album one", 3);
        var importer = new VaultImporter(vault);

        var result = await importer.ImportAsync(importer.PlanFolder(Path.GetDirectoryName(source)!), deleteSource: true);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Pages);
        Assert.False(Directory.Exists(source));
        var reopened = await NewVaultAsync();
        await reopened.EnsureReadyAsync();
        Assert.Equal(3, reopened.Entries.Count);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }, await reopened.ReadAllAsync(reopened.Collection(reopened.Entries[0].Collection!)[0].Id));
    }

    [Fact]
    public async Task Each_save_keeps_the_previous_index_and_a_damaged_index_opens_from_it()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        var vault = await NewVaultAsync();
        await vault.AddBytesAsync([1, 2, 3], "first", VaultKind.File, null, 0, ".bin");
        await vault.AddBytesAsync([4, 5, 6], "second", VaultKind.File, null, 0, ".bin");
        Assert.True(File.Exists(Path.Combine(VaultRoot, "index.bak")));

        File.WriteAllBytes(Path.Combine(VaultRoot, "index"), [1, 2, 3, 4]); // torn write

        var reopened = await NewVaultAsync();
        await reopened.EnsureReadyAsync();
        Assert.Equal(["first"], reopened.Entries.Select(e => e.Title)); // the copy from before the last save
    }

    [Fact]
    public async Task A_missing_index_beside_encrypted_files_is_never_replaced_by_an_empty_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        var vault = await NewVaultAsync();
        await vault.AddBytesAsync([1, 2, 3], "kept", VaultKind.File, null, 0, ".bin");
        File.Delete(Path.Combine(VaultRoot, "index"));
        File.Delete(Path.Combine(VaultRoot, "index.bak"));

        var reopened = await NewVaultAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.EnsureReadyAsync());
        Assert.False(File.Exists(Path.Combine(VaultRoot, "index")));
        Assert.False(reopened.IsReady);
    }

    [Fact]
    public async Task A_vault_whose_folder_is_gone_says_so_and_creates_nothing()
    {
        // The library's drive came back under another letter, or isn't there: no fresh vault in its place.
        var root = Path.Combine(_root, "Gone drive", "Anime", ".cache");
        var vault = await NewVaultAsync(root);

        var ex = await Assert.ThrowsAsync<VaultUnavailableException>(() => vault.EnsureReadyAsync());
        Assert.Contains("can't be reached", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "Gone drive")));
    }

    [Fact]
    public async Task When_the_drive_drops_after_opening_an_import_stops_and_keeps_every_original()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        var vault = await NewVaultAsync();
        await vault.EnsureReadyAsync();
        var source = Album("Album two", 2);
        var importer = new VaultImporter(vault);
        var plan = importer.PlanFolder(Path.GetDirectoryName(source)!);

        Directory.Delete(Path.Combine(_root, "Library"), recursive: true); // the drive went away

        await Assert.ThrowsAsync<VaultUnavailableException>(() => importer.ImportAsync(plan, deleteSource: true));
        Assert.Equal(2, Directory.GetFiles(source).Length);
        Assert.False(Directory.Exists(Path.Combine(_root, "Library")));
    }
}
