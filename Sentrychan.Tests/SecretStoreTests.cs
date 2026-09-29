using System.Security.Cryptography;
using Sentrychan.Core;
using Sentrychan.Core.Secrets;

namespace Sentrychan.Tests;

public class SecretStoreTests
{
    private sealed class MemoryVault : IMasterKeyVault
    {
        public byte[]? Key { get; set; }
        public int Writes { get; private set; }
        public string Name => "memory";
        public byte[]? Read() => Key;
        public void Write(byte[] key) { Key = key; Writes++; }
    }

    [Fact]
    public void Windows_keeps_dpapi_so_existing_data_opens()
    {
        var store = SecretStores.Select(AppPaths.Os.Windows, () => throw new InvalidOperationException(), () => throw new InvalidOperationException());
        Assert.IsType<DpapiSecretStore>(store);
    }

    [Fact]
    public void MacOS_wraps_with_a_keychain_key()
    {
        var store = SecretStores.Select(AppPaths.Os.MacOS, () => (null, "unused"), () => new MemoryVault());
        Assert.IsType<KeyWrappingSecretStore>(store);
    }

    [Fact]
    public void Linux_uses_the_secret_service_when_it_is_there()
    {
        var store = SecretStores.Select(AppPaths.Os.Linux, () => (new MemoryVault(), null), () => throw new InvalidOperationException());
        Assert.Equal("memory", store.Name);
    }

    [Fact]
    public void Without_a_secret_store_nothing_is_stored_in_the_clear()
    {
        var store = SecretStores.Select(AppPaths.Os.Linux, () => (null, "libsecret isn't installed. " + LibSecretVault.InstallHint),
            () => throw new InvalidOperationException());
        var ex = Assert.Throws<SecretStoreUnavailableException>(() => store.Protect([1, 2, 3]));
        Assert.Contains("libsecret-1-0", ex.Message);
        Assert.Throws<SecretStoreUnavailableException>(() => store.Unprotect([1, 2, 3]));
    }

    [Fact]
    public void Sealed_data_round_trips_with_one_key_made_once()
    {
        var vault = new MemoryVault();
        var store = new KeyWrappingSecretStore(vault);
        var a = store.Protect("session"u8.ToArray(), "entropy"u8.ToArray());
        var b = store.Protect("session"u8.ToArray(), "entropy"u8.ToArray());
        Assert.NotEqual(a, b); // fresh nonce each time
        Assert.Equal("session"u8.ToArray(), store.Unprotect(a, "entropy"u8.ToArray()));
        Assert.Equal(1, vault.Writes);

        // Another run reads the same key from the vault.
        Assert.Equal("session"u8.ToArray(), new KeyWrappingSecretStore(vault).Unprotect(b, "entropy"u8.ToArray()));
        Assert.Equal(1, vault.Writes);
    }

    [Fact]
    public void Wrong_entropy_tampering_or_a_foreign_blob_fail_loudly()
    {
        var store = new KeyWrappingSecretStore(new MemoryVault());
        var sealedData = store.Protect([9, 9, 9], "vault"u8.ToArray());
        Assert.ThrowsAny<CryptographicException>(() => store.Unprotect(sealedData, "other"u8.ToArray()));
        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => store.Unprotect(sealedData, "vault"u8.ToArray()));
        Assert.ThrowsAny<CryptographicException>(() => store.Unprotect(new byte[64]));
        Assert.ThrowsAny<CryptographicException>(() => new KeyWrappingSecretStore(new MemoryVault()).Unprotect(store.Protect([1])));
    }

    [Fact]
    public void Protector_values_keep_their_prefix_and_refuse_plaintext_fallback()
    {
        var store = new KeyWrappingSecretStore(new MemoryVault());
        var stored = Core.Services.SecretProtector.Protect("hunter2", store);
        Assert.StartsWith("enc:v1:", stored);
        Assert.Equal("hunter2", Core.Services.SecretProtector.Unprotect(stored, store));
        Assert.Equal("legacy-plain", Core.Services.SecretProtector.Unprotect("legacy-plain", store)); // old installs

        var none = new UnavailableSecretStore("No keyring.");
        Assert.Throws<SecretStoreUnavailableException>(() => Core.Services.SecretProtector.Protect("hunter2", none));
    }
}

/// <summary>Runs against a real Secret Service when SENTRYCHAN_KEYRING_TEST is set (e.g. under dbus-run-session with gnome-keyring).</summary>
public sealed class KeyringFactAttribute : FactAttribute
{
    public KeyringFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SENTRYCHAN_KEYRING_TEST")))
            Skip = "Set SENTRYCHAN_KEYRING_TEST=1 inside a session with an unlocked Secret Service keyring.";
    }
}

public class LibSecretVaultTests
{
    [KeyringFact]
    public void The_master_key_is_stored_and_read_back_from_the_keyring()
    {
        // The in-memory "session" collection: a real Secret Service round trip without a keyring
        // password prompt, which a headless test can't answer.
        var (vault, problem) = LibSecretVault.TryLoad("Sentrychan Test " + Guid.NewGuid().ToString("N"), "session");
        Assert.True(vault != null, problem);
        var key = RandomNumberGenerator.GetBytes(32);
        vault!.Write(key);
        Assert.Equal(key, vault.Read());

        var store = new KeyWrappingSecretStore(vault);
        Assert.Equal([1, 2, 3], new KeyWrappingSecretStore(vault).Unprotect(store.Protect([1, 2, 3])));
    }
}
