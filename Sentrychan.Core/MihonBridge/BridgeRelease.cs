using System.Runtime.InteropServices;

namespace Sentrychan.Core.MihonBridge;

/// <summary>One downloadable server bundle: its file name and the SHA-256 it must hash to.</summary>
public sealed record BridgeAsset(string FileName, string Sha256)
{
    public bool IsZip => FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Suwayomi-Server release the bridge runs. Pinned on purpose: the GraphQL shapes the client
/// speaks were read from this exact tag's source, and each bundle's hash is checked before
/// anything is unpacked. Moving to a newer release means re-reading its schema, re-recording the
/// test fixtures and pinning the new hashes — never "latest".
/// </summary>
public static class BridgeRelease
{
    public const string Version = "v2.3.2243";

    /// <summary>The server project's own releases (it's MPL-2.0 server software, not a content source).</summary>
    public const string DownloadBase = "https://github.com/Suwayomi/Suwayomi-Server/releases/download/" + Version + "/";

    // Hashed from the published bundles. Each carries its own Java runtime, so nothing has to be
    // installed on the machine first.
    private static readonly Dictionary<string, BridgeAsset> Assets = new()
    {
        ["win-x64"] = new($"Suwayomi-Server-{Version}-windows-x64.zip",
            "895843f48d5735e01bdc43d79ab66e600d6f507076a9b792ffa418a9bbcc32c2"),
        ["linux-x64"] = new($"Suwayomi-Server-{Version}-linux-x64.tar.gz",
            "7ed20b7890a6720c4d5dd51fe9c3247f537ffcab01a1cba5c2a75626743236c3"),
        ["osx-x64"] = new($"Suwayomi-Server-{Version}-macOS-x64.tar.gz",
            "3021ce25ed0366bd91899621ff5e4f0ac0e11be292daa37a9c3dfac9bd7c9591"),
        ["osx-arm64"] = new($"Suwayomi-Server-{Version}-macOS-arm64.tar.gz",
            "884df50945c9c052ec55bea8bb6fd232f9258a5a0bb8a95d2e07850337b2481b"),
    };

    /// <summary>"win-x64", "linux-x64", "osx-arm64"… for this machine, or null for an OS/CPU with no bundle.</summary>
    public static string? CurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch == null) return null;
        if (OperatingSystem.IsWindows()) return "win-" + arch;
        if (OperatingSystem.IsMacOS()) return "osx-" + arch;
        if (OperatingSystem.IsLinux()) return "linux-" + arch;
        return null;
    }

    /// <summary>The bundle for a platform, or null when the release has none (e.g. Windows on ARM).</summary>
    public static BridgeAsset? AssetFor(string? platform) =>
        platform != null && Assets.TryGetValue(platform, out var a) ? a : null;

    public static Uri UrlOf(BridgeAsset asset) => new(DownloadBase + asset.FileName);
}
