using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// Mirror of a row in <c>public.user_profiles</c>, populated by a Supabase trigger
/// on every <c>auth.users</c> insert/update. Lets us look up a user by email
/// (for friend requests) and display real names/avatars in friend lists —
/// something <c>auth.users</c> can't do because Supabase doesn't expose it
/// through PostgREST.
/// </summary>
[Table("user_profiles")]
public class UserProfileEntry : BaseModel
{
    [PrimaryKey("id", shouldInsert: false)]
    public string? Id { get; set; }

    [Column("email")]
    public string Email { get; set; } = string.Empty;

    [Column("display_name")]
    public string? DisplayName { get; set; }

    [Column("avatar_url")]
    public string? AvatarUrl { get; set; }
}
