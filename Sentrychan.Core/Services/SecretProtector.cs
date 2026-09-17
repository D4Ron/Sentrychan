using System;
using System.Security.Cryptography;
using System.Text;

namespace Sentrychan.Core.Services;

/// <summary>
/// Encrypts small secrets (currently the qBittorrent password) before they go into
/// AppConfigs, which is a plain SQLite file any process running as the user — or
/// anything that scoops up %AppData% — can read.
///
/// Uses Windows DPAPI scoped to the current user: no key to manage, and the ciphertext
/// is useless on another machine or under another account. Values are tagged with a
/// prefix so we can tell an encrypted value from a legacy plaintext one and migrate
/// transparently on the next save rather than locking anyone out of their own config.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:v1:";

    /// <summary>True if the stored value has already been through Protect().</summary>
    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a secret for storage. Returns the input unchanged if it's empty, and
    /// falls back to plaintext if DPAPI is unavailable (non-Windows) — the app must
    /// keep working rather than lose the user's settings.
    /// </summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (IsProtected(plaintext)) return plaintext; // already encrypted — don't double-wrap

        if (!OperatingSystem.IsWindows()) return plaintext;

        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(bytes);
        }
        catch
        {
            return plaintext;
        }
    }

    /// <summary>
    /// Decrypts a stored secret. A value without the prefix is a legacy plaintext
    /// entry and is returned as-is, so existing installs keep working until the next
    /// save re-writes it encrypted.
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!IsProtected(stored)) return stored;

        if (!OperatingSystem.IsWindows()) return string.Empty;

        try
        {
            var payload = stored[Prefix.Length..];
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(payload), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Wrong user/machine, or a corrupted value — treat as unset rather than
            // handing a garbage password to qBittorrent.
            return string.Empty;
        }
    }
}
