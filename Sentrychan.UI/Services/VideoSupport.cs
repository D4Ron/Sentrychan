using System.Runtime.InteropServices;
using Sentrychan.Core;

namespace Sentrychan.UI.Services;

/// <summary>
/// Whether the in-app player can run, and if not, what to do about it. Windows and macOS builds
/// carry their own VLC library; Linux uses the system's (libvlc), which may not be installed.
/// </summary>
public static class VideoSupport
{
    private static bool _initialized;

    public static bool IsAvailable { get; private set; }

    /// <summary>Why video can't play, in words the user can act on; null when it can.</summary>
    public static string? Problem { get; private set; }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (AppPaths.CurrentOs == AppPaths.Os.Linux && !SystemLibVlcPresent())
        {
            Problem = Explain(AppPaths.Os.Linux);
            return;
        }
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LibVLC Core.Initialize failed: {ex}");
            Problem = Explain(AppPaths.CurrentOs);
        }
    }

    // LibVLCSharp only finds out at the first player on Linux; ask up front so the player can say why.
    private static bool SystemLibVlcPresent() =>
        NativeLibrary.TryLoad("libvlc.so.5", out _) || NativeLibrary.TryLoad("libvlc.so", out _);

    public static string Explain(AppPaths.Os os) => os switch
    {
        AppPaths.Os.Linux =>
            "Video playback uses VLC's library, which isn't installed. Install VLC from your distribution — " +
            "for example sudo apt install vlc (Debian, Ubuntu), sudo dnf install vlc (Fedora, from RPM Fusion) " +
            "or sudo pacman -S vlc (Arch) — then restart Sentrychan.",
        _ => "Video playback couldn't start: the VLC library that comes with Sentrychan didn't load. " +
             "Reinstalling Sentrychan usually fixes this.",
    };
}
