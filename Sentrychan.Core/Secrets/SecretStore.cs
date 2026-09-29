using System.Security.Cryptography;

namespace Sentrychan.Core.Secrets;

/// <summary>
/// Seals small secrets — the vault key, the sign-in session, stored passwords — so the file or
/// database value they end up in is useless to anyone but this user on this machine.
///
/// <para>Windows: DPAPI, exactly as before, so everything existing installs wrote keeps opening.
/// macOS and Linux: AES-GCM under a per-app master key that lives in the system's own secret
/// store (the Keychain; the Secret Service — GNOME Keyring, KWallet — through libsecret).</para>
///
/// <para>When there's no secret store there's no fallback to plaintext:
/// <see cref="SecretStoreUnavailableException"/> says what's missing.</para>
/// </summary>
public interface ISecretStore
{
    /// <summary>"Windows DPAPI", "macOS Keychain", "Secret Service".</summary>
    string Name { get; }

    /// <summary>Seals <paramref name="data"/>. <paramref name="entropy"/> must be given again to unseal.</summary>
    byte[] Protect(byte[] data, byte[]? entropy = null);

    /// <summary>Opens what <see cref="Protect"/> sealed. Throws <see cref="CryptographicException"/> if it can't.</summary>
    byte[] Unprotect(byte[] data, byte[]? entropy = null);
}

/// <summary>The system has no usable secret store; the message says what to install or enable.</summary>
public sealed class SecretStoreUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>Where the per-app master key is kept on macOS and Linux.</summary>
public interface IMasterKeyVault
{
    string Name { get; }

    /// <summary>The stored key, or null when none was stored yet. Throws <see cref="SecretStoreUnavailableException"/>.</summary>
    byte[]? Read();

    void Write(byte[] key);
}

public static class SecretStores
{
    private static readonly Lazy<ISecretStore> _current = new(() => Select(AppPaths.CurrentOs, LibSecretVault.TryLoad, KeychainVault.Create));

    /// <summary>The store for this system. Choosing it never throws; using an unavailable one does.</summary>
    public static ISecretStore Current => _current.Value;

    /// <summary>The keyring entry's name, per flavour, so stable and preview never share a key.</summary>
    public static string ServiceName => BuildInfo.AppName;

    /// <summary>The selection rule, separated from the machine so it can be tested.</summary>
    public static ISecretStore Select(AppPaths.Os os, Func<(IMasterKeyVault? Vault, string? Problem)> linux,
        Func<IMasterKeyVault> mac)
    {
        switch (os)
        {
            case AppPaths.Os.Windows:
                return new DpapiSecretStore();
            case AppPaths.Os.MacOS:
                return new KeyWrappingSecretStore(mac());
            default:
                var (vault, problem) = linux();
                return vault != null
                    ? new KeyWrappingSecretStore(vault)
                    : new UnavailableSecretStore(problem ?? "No secret store is available.");
        }
    }
}

/// <summary>Windows DPAPI, scoped to the current user — what every Windows build has always used.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    public string Name => "Windows DPAPI";

    public byte[] Protect(byte[] data, byte[]? entropy = null) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();

    public byte[] Unprotect(byte[] data, byte[]? entropy = null) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();
}

/// <summary>A system without a secret store. Every use explains why, instead of storing in the clear.</summary>
public sealed class UnavailableSecretStore(string problem) : ISecretStore
{
    public string Name => "none";
    public string Problem { get; } = problem;
    public byte[] Protect(byte[] data, byte[]? entropy = null) => throw new SecretStoreUnavailableException(Problem);
    public byte[] Unprotect(byte[] data, byte[]? entropy = null) => throw new SecretStoreUnavailableException(Problem);
}

/// <summary>
/// AES-256-GCM under a master key held by the system's secret store. The key is created on first
/// use. Sealed format: "SCS1" | 12-byte nonce | 16-byte tag | ciphertext; the entropy is bound in
/// as associated data, so a value sealed for one purpose can't be opened as another.
/// </summary>
public sealed class KeyWrappingSecretStore(IMasterKeyVault vault) : ISecretStore
{
    private static readonly byte[] Magic = "SCS1"u8.ToArray();
    private readonly object _gate = new();
    private byte[]? _key;

    public string Name => vault.Name;

    private byte[] Key()
    {
        lock (_gate)
        {
            if (_key != null) return _key;
            var key = vault.Read();
            if (key is not { Length: 32 })
            {
                key = RandomNumberGenerator.GetBytes(32);
                vault.Write(key);
            }
            return _key = key;
        }
    }

    public byte[] Protect(byte[] data, byte[]? entropy = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var gcm = new AesGcm(Key(), 16))
            gcm.Encrypt(nonce, data, cipher, tag, entropy);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data, byte[]? entropy = null)
    {
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Not something this secret store sealed.");
        var plain = new byte[data.Length - 32];
        using (var gcm = new AesGcm(Key(), 16))
            gcm.Decrypt(data.AsSpan(4, 12), data.AsSpan(32), data.AsSpan(16, 16), plain, entropy);
        return plain;
    }
}
