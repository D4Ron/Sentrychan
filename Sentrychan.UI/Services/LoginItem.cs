using Sentrychan.Core;
using System;
using System.IO;
using System.Security;

namespace Sentrychan.UI.Services;

/// <summary>
/// "Start when I sign in". Windows: a value under the user's Run key. macOS: a per-user
/// LaunchAgent that opens the app bundle, which shows up (and can be switched off) in System
/// Settings → General → Login Items. Neither needs a permission prompt. Both start the app with
/// <see cref="BackgroundArg"/>, so it starts out of the way.
/// </summary>
public static class LoginItem
{
    /// <summary>Passed on a sign-in start: the app starts in the tray (Windows) or minimised (macOS).</summary>
    public const string BackgroundArg = "--background";

    /// <summary>AppConfig: the first-start question was answered.</summary>
    public const string AskedKey = "LoginItemAsked";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSupported =>
        (OperatingSystem.IsWindows() && Environment.ProcessPath != null) || (OperatingSystem.IsMacOS() && BundlePath != null);

    /// <summary>What the option is called on this system.</summary>
    public static string Label => OperatingSystem.IsWindows() ? "Start with Windows" : "Open at login";

    public static bool IsEnabled
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(BuildInfo.AppName) is string;
            }
            return IsSupported && File.Exists(PlistPath);
        }
    }

    public static void Set(bool on)
    {
        if (!IsSupported) return;
        if (OperatingSystem.IsWindows()) { SetWindows(on); return; }
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
              <key>Label</key><string>{AgentLabel}</string>
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

    /// <summary>
    /// The installed exe keeps its path across updates (the installer swaps the folder's contents),
    /// so the Run entry stays valid; a portable copy registers wherever it runs from.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindows(bool on)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(BuildInfo.AppName, $"\"{Environment.ProcessPath}\" {BackgroundArg}");
        else if (key.GetValue(BuildInfo.AppName) != null) key.DeleteValue(BuildInfo.AppName);
    }

    private static string AgentLabel => BuildInfo.IsPreview ? "app.sentrychan.preview.login" : "app.sentrychan.login";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", AgentLabel + ".plist");

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
}
