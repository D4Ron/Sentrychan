namespace Sentrychan.Core.Config;

/// <summary>
/// AniDB UDP API credentials for metadata lookups.
/// Hardcoded like HyperbeamConfig — no UI exposure needed for a single-user desktop app.
/// Fill in your AniDB username and password below.
/// </summary>
public static class AniDbConfig
{
    /// <summary>Your AniDB account username.</summary>
    public const string Username = "your-anidb-username";

    /// <summary>Your AniDB account password.</summary>
    public const string Password = "your-anidb-password";

    /// <summary>UDP client name registered with AniDB (keep short, no spaces).</summary>
    public const string ClientName = "sentrychan";

    /// <summary>Client version reported to AniDB. Increment when releasing.</summary>
    public const int ClientVersion = 1;

    public static bool IsConfigured =>
        !Username.StartsWith("your-") && !Password.StartsWith("your-");
}
