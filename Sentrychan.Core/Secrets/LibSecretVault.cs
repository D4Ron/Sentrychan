using System.Runtime.InteropServices;

namespace Sentrychan.Core.Secrets;

/// <summary>
/// The master key in the Secret Service (GNOME Keyring, KWallet, KeePassXC…) through libsecret,
/// under a schema of its own with one attribute, the app's name. Uses the non-variadic
/// <c>secret_password_storev_sync</c>/<c>lookupv_sync</c>, which take a GHashTable of attributes.
/// </summary>
public sealed class LibSecretVault : IMasterKeyVault
{
    public const string InstallHint =
        "Sentrychan keeps its keys in the system keyring (the Secret Service: GNOME Keyring, KWallet or " +
        "KeePassXC) through libsecret. Install libsecret (package libsecret-1-0 on Debian/Ubuntu, libsecret on " +
        "Fedora/Arch) and make sure a keyring is running and unlocked, then start Sentrychan again.";

    private const string SchemaName = "app.sentrychan.MasterKey";
    private const string Attribute = "app";

    private readonly Api _api;
    private readonly string _app;
    private readonly string? _collection;

    private LibSecretVault(Api api, string app, string? collection)
    {
        _api = api;
        _app = app;
        _collection = collection;
    }

    public string Name => "Secret Service";

    /// <summary>The vault, or why there can't be one (libsecret or GLib not installed).</summary>
    public static (IMasterKeyVault? Vault, string? Problem) TryLoad() => TryLoad(SecretStores.ServiceName, collection: null);

    /// <summary><paramref name="collection"/>: null for the user's default keyring; tests use "session" (in memory, no prompt).</summary>
    internal static (IMasterKeyVault? Vault, string? Problem) TryLoad(string app, string? collection)
    {
        var api = Api.TryLoad(out var missing);
        return api == null
            ? (null, $"{missing} isn't installed. {InstallHint}")
            : (new LibSecretVault(api, app, collection), null);
    }

    public byte[]? Read()
    {
        using var call = new Call(_api, _app);
        var result = _api.Lookup(call.Schema, call.Attributes, IntPtr.Zero, out var error);
        call.ThrowIf(error, "read");
        if (result == IntPtr.Zero) return null;
        try { return Convert.FromBase64String(Marshal.PtrToStringUTF8(result) ?? ""); }
        finally { _api.Free(result); }
    }

    public void Write(byte[] key)
    {
        using var call = new Call(_api, _app);
        var label = Marshal.StringToCoTaskMemUTF8($"{_app} key");
        var secret = Marshal.StringToCoTaskMemUTF8(Convert.ToBase64String(key));
        var collection = _collection == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(_collection);
        try
        {
            var ok = _api.Store(call.Schema, call.Attributes, collection, label, secret, IntPtr.Zero, out var error);
            call.ThrowIf(error, "store");
            if (!ok) throw new SecretStoreUnavailableException("The keyring didn't store Sentrychan's key. " + InstallHint);
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUTF8(secret);
            Marshal.FreeCoTaskMem(label);
            if (collection != IntPtr.Zero) Marshal.FreeCoTaskMem(collection);
        }
    }

    /// <summary>The schema struct and the attribute table for one call, freed afterwards.</summary>
    private sealed class Call : IDisposable
    {
        // SecretSchema: name*, flags (int, padded), 32 × {name*, type (int, padded)}, reserved int
        // (padded), 7 reserved pointers — 592 bytes on 64-bit.
        private const int SchemaSize = 8 + 8 + 32 * 16 + 8 + 7 * 8;
        private readonly Api _api;
        private readonly IntPtr _schemaName, _attrName, _attrValue;

        public IntPtr Schema { get; }
        public IntPtr Attributes { get; }

        public Call(Api api, string app)
        {
            _api = api;
            _schemaName = Marshal.StringToCoTaskMemUTF8(SchemaName);
            _attrName = Marshal.StringToCoTaskMemUTF8(Attribute);
            _attrValue = Marshal.StringToCoTaskMemUTF8(app);
            Schema = Marshal.AllocHGlobal(SchemaSize);
            Marshal.Copy(new byte[SchemaSize], 0, Schema, SchemaSize);
            Marshal.WriteIntPtr(Schema, 0, _schemaName);
            Marshal.WriteInt32(Schema, 8, 0);                  // SECRET_SCHEMA_NONE
            Marshal.WriteIntPtr(Schema, 16, _attrName);         // attributes[0].name
            Marshal.WriteInt32(Schema, 24, 0);                  // SECRET_SCHEMA_ATTRIBUTE_STRING
            Attributes = api.NewTable();
            api.Insert(Attributes, _attrName, _attrValue);
        }

        public void ThrowIf(IntPtr error, string what)
        {
            if (error == IntPtr.Zero) return;
            var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
            _api.FreeError(error);
            throw new SecretStoreUnavailableException($"The keyring couldn't {what} Sentrychan's key: {message}. {InstallHint}");
        }

        public void Dispose()
        {
            _api.Unref(Attributes);
            Marshal.FreeHGlobal(Schema);
            Marshal.FreeCoTaskMem(_schemaName);
            Marshal.FreeCoTaskMem(_attrName);
            Marshal.FreeCoTaskMem(_attrValue);
        }
    }

    /// <summary>The handful of libsecret and GLib functions, bound at run time so a missing library is a message, not a crash.</summary>
    private sealed class Api
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StoreFn(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password,
            IntPtr cancellable, out IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LookupFn(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFn(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr TableNewFn(IntPtr hash, IntPtr equal);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InsertFn(IntPtr table, IntPtr key, IntPtr value);

        private readonly StoreFn _store;
        private readonly LookupFn _lookup;
        private readonly FreeFn _free, _unref, _errorFree;
        private readonly TableNewFn _tableNew;
        private readonly InsertFn _insert;
        private readonly IntPtr _strHash, _strEqual;

        private Api(IntPtr secret, IntPtr glib)
        {
            _store = Bind<StoreFn>(secret, "secret_password_storev_sync");
            _lookup = Bind<LookupFn>(secret, "secret_password_lookupv_sync");
            _free = Bind<FreeFn>(secret, "secret_password_free");
            _tableNew = Bind<TableNewFn>(glib, "g_hash_table_new");
            _insert = Bind<InsertFn>(glib, "g_hash_table_insert");
            _unref = Bind<FreeFn>(glib, "g_hash_table_unref");
            _errorFree = Bind<FreeFn>(glib, "g_error_free");
            _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
            _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
        }

        private static T Bind<T>(IntPtr lib, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

        public static Api? TryLoad(out string missing)
        {
            missing = "libsecret";
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()) return null;
            if (!NativeLibrary.TryLoad("libsecret-1.so.0", out var secret)) return null;
            missing = "GLib";
            if (!NativeLibrary.TryLoad("libglib-2.0.so.0", out var glib)) return null;
            try { return new Api(secret, glib); }
            catch (EntryPointNotFoundException) { missing = "A recent enough libsecret"; return null; }
        }

        public bool Store(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error) =>
            _store(schema, attributes, collection, label, password, cancellable, out error) != 0;

        public IntPtr Lookup(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error) =>
            _lookup(schema, attributes, cancellable, out error);

        public void Free(IntPtr password) => _free(password);
        public IntPtr NewTable() => _tableNew(_strHash, _strEqual);
        public void Insert(IntPtr table, IntPtr key, IntPtr value) => _insert(table, key, value);
        public void Unref(IntPtr table) => _unref(table);
        public void FreeError(IntPtr error) => _errorFree(error);
    }
}
