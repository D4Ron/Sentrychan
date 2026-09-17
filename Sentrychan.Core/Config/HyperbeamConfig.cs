namespace Sentrychan.Core.Config;

/// <summary>
/// Hyperbeam API credentials for Watch Party virtual browser sessions.
///
/// NOT SHIPPED CONFIGURED, DELIBERATELY. Hyperbeam-backed watch parties are an unreleased
/// feature: <c>IHyperbeamService</c> is registered in DI but no view-model consumes it yet,
/// and <see cref="ApiKey"/> is a placeholder, so <see cref="IsConfigured"/> is false and
/// <c>HyperbeamService</c> takes its "not configured" path.
///
/// The API key is a SERVER secret — it authorises billable VM creation — so it must never be
/// committed or compiled into a client build. Anything shipped to users can be string-dumped.
/// Before this feature ships, the call has to move server-side: client → Supabase Edge Function
/// (which holds the key in its environment and checks entitlement) → Hyperbeam. See the
/// "Going public" plan in CLAUDE.md.
///
/// To experiment locally, set the key here but do not commit it.
/// </summary>
public static class HyperbeamConfig
{
    /// <summary>Placeholder. A real key belongs in an Edge Function's environment, not here.</summary>
    public const string ApiKey = "your-hyperbeam-api-key";

    /// <summary>Base URL for the Hyperbeam REST API.</summary>
    public const string ApiBaseUrl = "https://engine.hyperbeam.com/v0";

    /// <summary>Returns true when the API key has been filled in.</summary>
    public static bool IsConfigured =>
        !ApiKey.Contains("your-hyperbeam-api-key");
}
