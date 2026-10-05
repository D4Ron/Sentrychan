using Sentrychan.Core;
using System;
using System.IO;
using System.Security;

namespace Sentrychan.UI.Services;

/// <summary>
/// "Open at login" on macOS: a per-user LaunchAgent that opens the app bundle in the background
/// when the user logs in. A file in ~/Library/LaunchAgents, so it needs no permission prompt and
/// shows up (and can be switched off) in System Settings → General → Login Items.
/// </summary>
public static class LoginItem
{
    /// <summary>Passed on a login start: the app starts minimised instead of in front.</summary>
    public const string BackgroundArg = "--background";

    /// <summary>AppConfig: the first-start question was answered.</summary>
    public const string AskedKey = "LoginItemAsked";

    public static bool IsSupported => OperatingSystem.IsMacOS() && BundlePath != null;

    public static bool IsEnabled => IsSupported && File.Exists(PlistPath);

    private static string Label => BuildInfo.IsPreview ? "app.sentrychan.preview.login" : "app.sentrychan.login";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>The .app the running executable is inside, or null when not run from a bundle.</summary>
    private static string? BundlePath
    {
        get
        {
            var exe = Environment.ProcessPath;
            var i = exe?.IndexOf(".app/", StringComparison.Ordinal) ?? -1;
            return i > 0 ? exe![..(i + 4)] : null;
        }
    }

    public static void Set(bool on)
    {
        if (!IsSupported) return;
        if (!on)
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>/usr/bin/open</string>
                <string>-g</string>
                <string>-a</string>
                <string>{SecurityElement.Escape(BundlePath)}</string>
                <string>--args</string>
                <string>{BackgroundArg}</string>
              </array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }
}
