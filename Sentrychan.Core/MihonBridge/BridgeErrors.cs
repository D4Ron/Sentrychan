namespace Sentrychan.Core.MihonBridge;

/// <summary>What went wrong, in terms the user can act on.</summary>
public enum BridgeFailure
{
    /// <summary>The helper server isn't installed, isn't running, or didn't answer.</summary>
    NotRunning,

    /// <summary>The site put a browser check (Cloudflare and the like) in front of the request.</summary>
    WebCheck,

    /// <summary>The source wants the user signed in (usually set up in the source's settings).</summary>
    LoginRequired,

    /// <summary>The site or extension failed some other way.</summary>
    Source,
}

/// <summary>A bridge call that failed, with a message fit to show as-is.</summary>
public sealed class BridgeException(BridgeFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public BridgeFailure Failure { get; } = failure;

    /// <summary>
    /// Turns a server error into something plain. The server reports errors as
    /// "Exception while fetching data (/field) : message" followed by a Java stack trace; only
    /// the message is worth showing, and a few kinds get their own explanation.
    /// </summary>
    public static BridgeException FromServer(string raw)
    {
        var message = raw;
        var marker = message.IndexOf(") : ", StringComparison.Ordinal);
        if (message.StartsWith("Exception while fetching data", StringComparison.Ordinal) && marker > 0)
            message = message[(marker + 4)..];
        var end = message.IndexOfAny(['\r', '\n']);
        if (end >= 0) message = message[..end];
        message = message.Trim();
        if (message is "" or "null") message = "The source failed without saying why.";

        // Only the message: every network stack trace passes through the server's Cloudflare
        // interceptor, so the trace would make any failure look like a web check. Sign-in is
        // checked first: extensions word it "log in via WebView".
        var lower = message.ToLowerInvariant();
        if (lower.Contains("login") || lower.Contains("log in") || lower.Contains("sign in")
            || lower.Contains("unauthorized") || lower.Contains("http error 401"))
            return new(BridgeFailure.LoginRequired,
                "This source needs you to sign in — check its settings. (" + message + ")");
        if (lower.Contains("cloudflare") || lower.Contains("captcha") || lower.Contains("webview")
            || lower.Contains("bypass") || lower.Contains("challenge"))
            return new(BridgeFailure.WebCheck,
                "The site asked for a browser check that the helper server couldn't pass. Try again later, " +
                "or turn on \"Allow web checks\" in the Mihon extensions settings. (" + message + ")");
        return new(BridgeFailure.Source, message);
    }

    public static BridgeException NotRunning(string why, Exception? inner = null) =>
        new(BridgeFailure.NotRunning, why, inner);
}
