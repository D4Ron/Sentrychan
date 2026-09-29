using Sentrychan.Core.Vault;

namespace Sentrychan.Tests;

public sealed class VaultRootTests : IDisposable
{
    private readonly string _library = Directory.CreateTempSubdirectory("sentrychan-lib-").FullName;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "sentrychan-data");

    public void Dispose() => Directory.Delete(_library, recursive: true);

    [Fact]
    public void Stable_keeps_its_vault_in_the_library_cache_folder()
    {
        Assert.Equal(Path.Combine(_library, ".cache"), VaultService.DefaultRoot(_library, _data, isPreview: false));
    }

    [Fact]
    public void Preview_never_shares_the_stable_vault_folder()
    {
        var preview = VaultService.DefaultRoot(_library, _data, isPreview: true);
        Assert.Equal(Path.Combine(_library, ".cache-preview"), preview);
        Assert.NotEqual(VaultService.DefaultRoot(_library, _data, isPreview: false), preview);
        // Hidden from the library scan and the vault importer, which skip dot-folders.
        Assert.StartsWith(".", Path.GetFileName(preview));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_usable_library_the_vault_lives_in_the_data_directory(bool isPreview)
    {
        var expected = Path.Combine(_data, "cache", "store");
        Assert.Equal(expected, VaultService.DefaultRoot(null, _data, isPreview));
        Assert.Equal(expected, VaultService.DefaultRoot(Path.Combine(_library, "missing"), _data, isPreview));
    }
}
