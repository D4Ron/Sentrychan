namespace Sentrychan.Core;

/// <summary>
/// What kind of build this is. <see cref="Flavor"/> comes from the MSBuild <c>Flavor</c>
/// property (BuildInfo.g.cs, generated at build time); everything else derives from it.
/// </summary>
public static partial class BuildInfo
{
    /// <summary>
    /// The preview is a separate app that installs beside stable: its own data folder, vault
    /// root, update channel and name, so trying it can't disturb a stable install.
    /// </summary>
    public static bool IsPreview => Flavor == "Preview";

    /// <summary>The name shown to people — window title, tray, notifications.</summary>
    public static string AppName => IsPreview ? "Sentrychan Preview" : "Sentrychan";
}
