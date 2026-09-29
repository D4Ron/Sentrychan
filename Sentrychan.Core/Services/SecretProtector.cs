using System;
using System.Security.Cryptography;
using System.Text;
using Sentrychan.Core.Secrets;

namespace Sentrychan.Core.Services;

/// <summary>
/// Encrypts small secrets (currently the qBittorrent password) before they go into
/// AppConfigs, which is a plain SQLite file any process running as the user — or
/// anything that scoops up the data folder — can read.
///
/// Sealed by the system's secret store (<see cref="SecretStores"/>): DPAPI on Windows, as
/// always, so existing values keep opening; the Keychain or the Secret Service elsewhere.
/// Values are tagged with a prefix so we can tell an encrypted value from a legacy plaintext
/// one and migrate transparently on the next save rather than locking anyone out of their own
/// config. With no secret store, saving a secret fails with a message saying so — it is never
/// written in the clear.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:v1:";

    /// <summary>True if the stored value has already been through Protect().</summary>
    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a secret for storage. Returns the input unchanged if it's empty.
    /// Throws <see cref="SecretStoreUnavailableException"/> when the system can't keep secrets.
    /// </summary>
    public static string Protect(string? plaintext, ISecretStore? store = null)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (IsProtected(plaintext)) return plaintext; // already encrypted — don't double-wrap

        var bytes = (store ?? SecretStores.Current).Protect(Encoding.UTF8.GetBytes(plaintext));
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decrypts a stored secret. A value without the prefix is a legacy plaintext
    /// entry and is returned as-is, so existing installs keep working until the next
    /// save re-writes it encrypted. Throws <see cref="SecretStoreUnavailableException"/> when
    /// the system can't open secrets at all.
    /// </summary>
    public static string Unprotect(string? stored, ISecretStore? store = null)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!IsProtected(stored)) return stored;

        try
        {
            var payload = stored[Prefix.Length..];
            var bytes = (store ?? SecretStores.Current).Unprotect(Convert.FromBase64String(payload));
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            // Wrong user/machine, or a corrupted value — treat as unset rather than
            // handing a garbage password to qBittorrent.
            return string.Empty;
        }
    }
}
