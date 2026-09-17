namespace Sentrychan.Core.Models;

/// <summary>
/// Represents a signed-in Sentrychan account user returned from Supabase Auth.
/// </summary>
public class AccountUser
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Short display string for the topbar avatar button.</summary>
    public string Initials => string.IsNullOrEmpty(DisplayName)
        ? (Email.Length > 0 ? Email[0].ToString().ToUpper() : "?")
        : string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Take(2)
                                   .Select(w => char.ToUpper(w[0])));
}
