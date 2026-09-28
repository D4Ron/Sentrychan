using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Vault;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.Services;

/// <summary>
/// The one way the app plays a video. "Internal" (the default) opens the built-in player;
/// MPV / VLC / PotPlayer hand the file to that program. Vault videos always play in the
/// built-in player — they only exist decrypted in its memory, so no other program can open them.
/// </summary>
public static class PlayerLauncher
{
    public static async Task PlayAsync(IReadOnlyList<PlaybackItem> playlist, int startIndex = 0)
    {
        if (playlist.Count == 0 || App.Services == null) return;
        var item = playlist[Math.Clamp(startIndex, 0, playlist.Count - 1)];

        if (item.FilePath != null && await TryExternalAsync(item.FilePath)) return;

        var vault = App.Services.GetService<VaultService>();
        if (item.VaultId != null && vault != null) await vault.EnsureReadyAsync();
        var log = App.Services.GetService<ILoggerFactory>()?.CreateLogger("Player");

        new VideoPlayerWindow
        {
            DataContext = new VideoPlayerViewModel(playlist, startIndex, vault, log)
        }.Show();
    }

    public static Task PlayFileAsync(string path) =>
        PlayAsync([new PlaybackItem(System.IO.Path.GetFileNameWithoutExtension(path), FilePath: path)]);

    private static async Task<bool> TryExternalAsync(string path)
    {
        var config = App.Services?.GetService<IConfigService>();
        if (config == null) return false;

        var player = await config.GetValueAsync("SelectedPlayer", "Internal", CancellationToken.None);
        var (key, fallback) = player switch
        {
            "MPV"       => ("MpvPath", "mpv"),
            "VLC"       => ("VlcPath", "vlc"),
            "PotPlayer" => ("PotPlayerPath", "PotPlayer64.exe"),
            _           => (null, null),
        };
        if (key == null) return false;

        var exe = await config.GetValueAsync(key, fallback!, CancellationToken.None);
        try
        {
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { path }, UseShellExecute = false });
            return true;
        }
        catch
        {
            return false; // not installed where configured — play it here instead
        }
    }
}
