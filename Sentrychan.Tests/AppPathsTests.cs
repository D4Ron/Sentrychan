using Sentrychan.Core;

namespace Sentrychan.Tests;

// Shares the process environment with anything else that sets SENTRYCHAN_DATA_DIR.
[Collection(EnvironmentCollection.Name)]
public class AppPathsTests
{
    private static readonly string Roaming = Path.Combine(Path.GetTempPath(), "roaming");

    [Fact]
    public void Stable_uses_the_Sentrychan_folder()
    {
        Assert.Equal(Path.Combine(Roaming, "Sentrychan"),
            AppPaths.ResolveDataDir(null, Roaming, isPreview: false));
    }

    [Fact]
    public void Preview_gets_its_own_folder_so_it_never_shares_stable_data()
    {
        var preview = AppPaths.ResolveDataDir(null, Roaming, isPreview: true);
        Assert.Equal(Path.Combine(Roaming, "Sentrychan Preview"), preview);
        Assert.NotEqual(AppPaths.ResolveDataDir(null, Roaming, isPreview: false), preview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Override_wins_for_either_flavour(bool isPreview)
    {
        var custom = Path.Combine(Path.GetTempPath(), "sentrychan-custom");
        Assert.Equal(custom, AppPaths.ResolveDataDir(custom, Roaming, isPreview));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_override_is_ignored(string blank)
    {
        Assert.Equal(Path.Combine(Roaming, "Sentrychan"),
            AppPaths.ResolveDataDir(blank, Roaming, isPreview: false));
    }

    [Fact]
    public void Relative_override_is_made_absolute()
    {
        var resolved = AppPaths.ResolveDataDir("some-data", Roaming, isPreview: false);
        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(Path.GetFullPath("some-data"), resolved);
    }

    [Fact]
    public void Environment_override_moves_every_derived_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sentrychan-tests-" + Guid.NewGuid().ToString("N"));
        var before = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable);
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, dir);
        try
        {
            Assert.Equal(dir, AppPaths.DataDir);
            Assert.Equal(Path.Combine(dir, "sentrychan.db"), AppPaths.Database);
            Assert.Equal(Path.Combine(dir, "logs"), AppPaths.Logs);
            Assert.Equal(Path.Combine(dir, "sources"), AppPaths.Sources);
            Assert.Equal(Path.Combine(dir, "vault.key"), AppPaths.VaultKey);
            Assert.Equal(Path.Combine(dir, "cache", "images"), AppPaths.Combine("cache", "images"));

            // The stable folder is where a preview copies from; redirecting this build must not move it.
            Assert.False(AppPaths.StableDataDir.StartsWith(dir, StringComparison.Ordinal));

            Assert.False(Directory.Exists(dir));
            AppPaths.EnsureDataDir();
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, before);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

public class AppPathsPerOsTests
{
    private const string Home = "/home/someone";

    [Fact]
    public void Windows_keeps_roaming_app_data()
    {
        Assert.Equal(@"C:\Users\someone\AppData\Roaming",
            AppPaths.PlatformRoot(AppPaths.Os.Windows, @"C:\Users\someone\AppData\Roaming", @"C:\Users\someone", null));
    }

    [Fact]
    public void MacOS_uses_application_support()
    {
        Assert.Equal(Path.Combine("/Users/someone", "Library", "Application Support"),
            AppPaths.PlatformRoot(AppPaths.Os.MacOS, "/Users/someone/.config", "/Users/someone", "/ignored"));
    }

    [Theory]
    [InlineData(null, "/home/someone/.local/share")]
    [InlineData("", "/home/someone/.local/share")]
    [InlineData("relative/data", "/home/someone/.local/share")] // not absolute → ignored, per the XDG spec
    [InlineData("/data/xdg", "/data/xdg")]
    public void Linux_follows_xdg_data_home(string? xdg, string expected)
    {
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar),
            AppPaths.PlatformRoot(AppPaths.Os.Linux, "/home/someone/.config", Home, xdg)
                .Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Each_flavour_gets_its_folder_under_the_platform_root()
    {
        var root = AppPaths.PlatformRoot(AppPaths.Os.Linux, "", Home, null);
        Assert.EndsWith(Path.Combine(".local", "share", "Sentrychan Preview"), AppPaths.ResolveDataDir(null, root, isPreview: true));
    }
}
