using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Sentrychan.Core.MihonBridge;

/// <summary>Where the bridge keeps things, under one root (normally <see cref="AppPaths.MihonBridge"/>).</summary>
public sealed class BridgeLayout(string root)
{
    public string Root { get; } = root;

    /// <summary>The unpacked server bundle for one release. Replaced wholesale on an upgrade.</summary>
    public string ServerDir(string version) => Path.Combine(Root, "server", version);

    /// <summary>
    /// The server's own data (its database, installed extensions, source settings). Kept across
    /// server upgrades — manga and chapter ids the library points at live in there.
    /// </summary>
    public string DataDir => Path.Combine(Root, "data");

    public string DownloadDir => Path.Combine(Root, "download");

    /// <summary>The running server's process id, so a server orphaned by a crash can be stopped next time.</summary>
    public string PidFile => Path.Combine(Root, "server.pid");

    private string Marker(string version) => Path.Combine(ServerDir(version), ".installed");

    public bool IsInstalled(string version) => File.Exists(Marker(version));

    internal void MarkInstalled(string version) => File.WriteAllText(Marker(version), DateTime.UtcNow.ToString("O"));

    /// <summary>The bundled Java runtime's launcher.</summary>
    public string JavaPath(string version) =>
        Path.Combine(ServerDir(version), "jre", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    public string JarPath(string version) => Path.Combine(ServerDir(version), "bin", "Suwayomi-Server.jar");

    /// <summary>
    /// A small native helper the Linux bundle preloads (its own start script does the same); it
    /// turns a native abort inside an extension into an exception instead of a dead server.
    /// </summary>
    public string? PreloadLibrary(string version)
    {
        var path = Path.Combine(ServerDir(version), "bin", "catch_abort.so");
        return OperatingSystem.IsLinux() && File.Exists(path) ? path : null;
    }
}

/// <summary>A download or unpack step, for a progress bar.</summary>
public sealed record BridgeInstallProgress(string Stage, double? Fraction);

/// <summary>
/// Downloads the pinned server bundle, checks its SHA-256 and unpacks it. Nothing is unpacked
/// from a file whose hash doesn't match, and a half-finished install never counts as installed.
/// </summary>
public sealed class BridgeInstaller(HttpClient http, BridgeLayout layout)
{
    // Chromium shell used by the server's desktop launcher; the bridge never opens a window.
    private static readonly string[] Unused = ["electron", "Suwayomi-Launcher.jar", "suwayomi-launcher.sh", "Suwayomi Launcher.bat", "Suwayomi Launcher.command"];

    public async Task InstallAsync(string version, BridgeAsset asset, Uri url,
        IProgress<BridgeInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (layout.IsInstalled(version)) return;
        Directory.CreateDirectory(layout.DownloadDir);
        var archive = Path.Combine(layout.DownloadDir, asset.FileName);

        try
        {
            await DownloadAsync(url, archive, progress, ct);

            progress?.Report(new("Checking the download", null));
            var actual = await HashAsync(archive, ct);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The downloaded server doesn't match the pinned checksum (got {actual[..12]}…, expected {asset.Sha256[..12]}…). Nothing was installed.");

            progress?.Report(new("Unpacking", null));
            var target = layout.ServerDir(version);
            var staging = target + ".partial";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            await ExtractAsync(archive, asset.IsZip, staging, ct);
            var root = SingleTopFolder(staging);
            foreach (var name in Unused)
            {
                var p = Path.Combine(root, name);
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(root, target);
            if (root != staging) Directory.Delete(staging, recursive: true);
            EnsureExecutable(layout.JavaPath(version));
            layout.MarkInstalled(version);
        }
        finally
        {
            try { File.Delete(archive); } catch { /* re-downloaded next time anyway */ }
        }
    }

    /// <summary>Removes every unpacked server except <paramref name="keepVersion"/>. The server's data stays.</summary>
    public void RemoveOtherVersions(string keepVersion)
    {
        var dir = Path.Combine(layout.Root, "server");
        if (!Directory.Exists(dir)) return;
        foreach (var d in Directory.GetDirectories(dir))
            if (!string.Equals(Path.GetFileName(d), keepVersion, StringComparison.Ordinal))
                try { Directory.Delete(d, recursive: true); } catch { /* in use — next time */ }
    }

    private async Task DownloadAsync(Uri url, string path, IProgress<BridgeInstallProgress>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long done = 0, lastReport = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (done - lastReport >= 1 << 20 || done == total)
            {
                lastReport = done;
                progress?.Report(new("Downloading", total is > 0 ? (double)done / total.Value : null));
            }
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }

    private static async Task ExtractAsync(string archive, bool zip, string into, CancellationToken ct)
    {
        if (zip)
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, into), ct);
            return;
        }
        await using var file = File.OpenRead(archive);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        // TarFile keeps the entries' Unix permissions, so the bundled java stays executable.
        await TarFile.ExtractToDirectoryAsync(gz, into, overwriteFiles: true, ct);
    }

    /// <summary>The bundles wrap everything in one versioned folder; unwrap it.</summary>
    private static string SingleTopFolder(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : dir;
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!File.Exists(path)) throw new InvalidDataException("The server bundle has no Java runtime where it should be.");
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
