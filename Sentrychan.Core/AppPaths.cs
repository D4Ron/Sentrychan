namespace Sentrychan.Core;

/// <summary>
/// Where Sentrychan keeps its own files: database, logs, caches, source packs, keys.
/// Everything that used to build <c>%AppData%/Sentrychan</c> by hand asks here instead, so
/// the preview flavour and tests can point the whole app somewhere else in one place.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Environment variable that replaces the data directory outright. Needed for tests and
    /// manual testing: .NET resolves the roaming folder through the shell, not %APPDATA%, so
    /// redirecting that variable doesn't move anything.
    /// </summary>
    public const string DataDirVariable = "SENTRYCHAN_DATA_DIR";

    public const string StableFolderName  = "Sentrychan";
    public const string PreviewFolderName = "Sentrychan Preview";

    private static string RoamingRoot => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>This build's data directory. Not created here — see <see cref="EnsureDataDir"/>.</summary>
    public static string DataDir =>
        ResolveDataDir(Environment.GetEnvironmentVariable(DataDirVariable), RoamingRoot, BuildInfo.IsPreview);

    /// <summary>
    /// Where the stable app keeps its data, whatever this build is. The preview reads it once,
    /// on first run, to offer a copy of the library; nothing ever writes to it from here.
    /// Deliberately ignores <see cref="DataDirVariable"/>, which redirects this build only.
    /// </summary>
    public static string StableDataDir => Path.Combine(RoamingRoot, StableFolderName);

    public static string Database => Path.Combine(DataDir, "sentrychan.db");
    public static string Logs     => Path.Combine(DataDir, "logs");
    public static string Sources  => Path.Combine(DataDir, "sources");
    public static string VaultKey => Path.Combine(DataDir, "vault.key");

    /// <summary>A path inside the data directory.</summary>
    public static string Combine(params string[] parts) => Path.Combine([DataDir, .. parts]);

    public static string EnsureDataDir()
    {
        var dir = DataDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The rule behind <see cref="DataDir"/>, separated from the environment so it can be tested:
    /// a non-blank override wins (made absolute); otherwise a per-flavour folder under the
    /// roaming application-data root.
    /// </summary>
    public static string ResolveDataDir(string? overrideDir, string roamingRoot, bool isPreview)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(overrideDir.Trim());
        return Path.Combine(roamingRoot, isPreview ? PreviewFolderName : StableFolderName);
    }
}
