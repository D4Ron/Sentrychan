namespace Sentrychan.UI.Services;

/// <summary>Public links the app points people to — one place to change them.</summary>
public static class AppLinks
{
    public const string Website = "https://d4ron.github.io/Sentrychan/";
    public const string Discord = "https://discord.gg/zbeeCZJy4";
    public const string Issues = "https://github.com/D4Ron/Sentrychan/issues";

    public static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser — nothing useful to do */ }
    }
}
