using System.Diagnostics;

namespace Sentrychan.UI.Services;

/// <summary>
/// Shows a folder, or a file within its folder, in the system's file manager: Explorer on
/// Windows, Finder on macOS, whatever xdg-open picks on Linux (which has no common way to
/// select a file, so the folder opens). Best-effort — never throws.
/// </summary>
public static class ShellLauncher
{
    public static void OpenFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Run("explorer.exe", dir);
        else if (OperatingSystem.IsMacOS()) Run("open", dir);
        else Run("xdg-open", dir);
    }

    /// <summary>The file selected in its folder where the system can; otherwise its folder.</summary>
    public static void Reveal(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        if (!File.Exists(file)) { OpenFolder(Path.GetDirectoryName(file)); return; }
        if (OperatingSystem.IsWindows())
        {
            // Explorer wants exactly /select,"path" — the quoting .NET would add confuses it.
            try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
            catch { /* Explorer refused */ }
        }
        else if (OperatingSystem.IsMacOS()) Run("open", "-R", file);
        else OpenFolder(Path.GetDirectoryName(file));
    }

    private static void Run(string program, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
        }
        catch { /* no file manager to hand it to */ }
    }
}
