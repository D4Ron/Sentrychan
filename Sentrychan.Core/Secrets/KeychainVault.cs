using System.Runtime.InteropServices;
using System.Text;

namespace Sentrychan.Core.Secrets;

/// <summary>
/// The master key as a generic password in the user's login Keychain (service: the app's name,
/// account "master-key"). Uses the Security framework's generic-password calls directly, so the
/// key never passes through a command line. Needs checking on a Mac.
/// </summary>
public sealed class KeychainVault : IMasterKeyVault
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const string Account = "master-key";

    private readonly byte[] _service;
    private readonly byte[] _account = Encoding.UTF8.GetBytes(Account);

    private KeychainVault(string service) => _service = Encoding.UTF8.GetBytes(service);

    public static IMasterKeyVault Create() => new KeychainVault(SecretStores.ServiceName);

    public string Name => "macOS Keychain";

    public byte[]? Read()
    {
        int status;
        uint length;
        IntPtr data;
        try
        {
            status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
                (uint)_account.Length, _account, out length, out data, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException("The macOS Keychain couldn't be reached.", ex);
        }
        if (status == ErrSecItemNotFound) return null;
        if (status != 0) throw Failed("read", status);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Convert.FromBase64String(Encoding.ASCII.GetString(bytes));
        }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, data); }
    }

    public void Write(byte[] key)
    {
        var secret = Encoding.ASCII.GetBytes(Convert.ToBase64String(key));
        var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, (uint)secret.Length, secret, IntPtr.Zero);
        // Only ever called when Read found nothing; a duplicate means another launch just made one.
        if (status is not (0 or ErrSecDuplicateItem)) throw Failed("store", status);
    }

    private static SecretStoreUnavailableException Failed(string what, int status) =>
        new($"The macOS Keychain refused to {what} Sentrychan's key (error {status}). If you denied access, " +
            "allow it in Keychain Access and try again.");

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
}
