namespace Sentrychan.Core.Config;

/// <summary>
/// Supabase project credentials.
///
/// HOW TO SET UP YOUR OWN PROJECT:
///   1. Go to https://supabase.com and create a free project.
///   2. In Project Settings → API, copy your Project URL and anon/public key.
///   3. Paste them below.
///   4. In the SQL Editor, run the migration in CloudSeriesEntry.cs to create the table.
///   5. In Authentication → Providers, enable Google OAuth and add your client ID/secret.
///   6. In Authentication → URL Configuration, add http://localhost:7781/auth/callback
///      to "Redirect URLs".
///
/// The anon key is SAFE to ship in a desktop app — it is a public client key.
/// RLS policies on the user_library table ensure users only see their own rows.
/// </summary>
public static class SupabaseConfig
{
    /// <summary>Your Supabase project URL, e.g. https://xyzcompany.supabase.co</summary>
    public const string Url = "https://jnafdkazhwkmauwtyhfx.supabase.co";

    /// <summary>Your Supabase anon / public key.</summary>
    public const string AnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImpuYWZka2F6aHdrbWF1d3R5aGZ4Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzYyNzEwMTMsImV4cCI6MjA5MTg0NzAxM30.thGZ5QRmoLkKAYQTBwP6t7RKk5HH6BlZ2wKmIA6xMAc";

    /// <summary>Local port for the OAuth callback server.</summary>
    public const int OAuthCallbackPort = 7781;

    /// <summary>Full redirect URI registered in Supabase dashboard.</summary>
    public static string OAuthCallbackUri =>
        $"http://localhost:{OAuthCallbackPort}/auth/callback";

    /// <summary>Returns true when both Url and AnonKey have been filled in.</summary>
    public static bool IsConfigured =>
        !Url.Contains("your-project") && !AnonKey.Contains("your-anon-key");
}
