using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wisp.Core.Security;

/// <summary>
/// Minimal binding to libsecret (Freedesktop Secret Service: GNOME Keyring, KWallet, KeePassXC).
/// Uses the non-variadic *v_sync functions so the P/Invoke is ABI-safe.
/// </summary>
[SupportedOSPlatform("linux")]
static class LibSecret
{
    const string Secret = "libsecret-1.so.0";
    const string GLib = "libglib-2.0.so.0";
    const string SchemaName = "org.wisp.Vault";
    const string AttributeName = "vault";

    [DllImport(Secret)] static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret)] static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret)] static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret)] static extern void secret_password_free(IntPtr password);
    [DllImport(GLib)] static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);
    [DllImport(GLib)] static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(GLib)] static extern void g_hash_table_unref(IntPtr table);
    [DllImport(GLib)] static extern void g_error_free(IntPtr error);

    public static bool IsAvailable()
    {
        if (!NativeLibrary.TryLoad(Secret, out var handle)) return false;
        NativeLibrary.Free(handle);
        try
        {
            // A lookup for a non-existing item succeeds (returns null) only if a Secret Service daemon answers.
            Lookup("wisp-probe");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Store(string id, string label, string secret) =>
        WithAttributes(id, (schema, attrs) =>
        {
            var labelPtr = Marshal.StringToCoTaskMemUTF8(label);
            var secretPtr = Marshal.StringToCoTaskMemUTF8(secret);
            try
            {
                secret_password_storev_sync(schema, attrs, IntPtr.Zero, labelPtr, secretPtr, IntPtr.Zero, out var error);
                ThrowIfError(error);
            }
            finally
            {
                ZeroAndFree(secretPtr, System.Text.Encoding.UTF8.GetByteCount(secret));
                Marshal.FreeCoTaskMem(labelPtr);
            }
            return 0;
        });

    public static string? Lookup(string id) =>
        WithAttributes(id, (schema, attrs) =>
        {
            var result = secret_password_lookupv_sync(schema, attrs, IntPtr.Zero, out var error);
            ThrowIfError(error);
            if (result == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(result); }
            finally { secret_password_free(result); }
        });

    public static void Clear(string id) =>
        WithAttributes(id, (schema, attrs) =>
        {
            secret_password_clearv_sync(schema, attrs, IntPtr.Zero, out var error);
            ThrowIfError(error);
            return 0;
        });

    static T WithAttributes<T>(string id, Func<IntPtr, IntPtr, T> action)
    {
        var glib = NativeLibrary.Load(GLib);
        var schema = BuildSchema(out var schemaStrings);
        var key = Marshal.StringToCoTaskMemUTF8(AttributeName);
        var value = Marshal.StringToCoTaskMemUTF8(id);
        var table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
        try
        {
            g_hash_table_insert(table, key, value);
            return action(schema, table);
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(schema);
            foreach (var s in schemaStrings) Marshal.FreeCoTaskMem(s);
        }
    }

    /// <summary>
    /// Lays out a SecretSchema struct by hand:
    /// { char* name; int flags; { char* name; int type; } attributes[32]; int reserved; void* reserved1..7; }
    /// </summary>
    static IntPtr BuildSchema(out IntPtr[] strings)
    {
        var ptr = IntPtr.Size;
        var attributeSize = ptr * 2; // pointer + int, padded to pointer alignment
        var size = ptr * 2 + attributeSize * 32 + ptr + ptr * 7;
        var schema = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(schema, i, 0);

        var name = Marshal.StringToCoTaskMemUTF8(SchemaName);
        var attribute = Marshal.StringToCoTaskMemUTF8(AttributeName);
        Marshal.WriteIntPtr(schema, 0, name);
        Marshal.WriteInt32(schema, ptr, 0);                 // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(schema, ptr * 2, attribute);
        Marshal.WriteInt32(schema, ptr * 3, 0);             // SECRET_SCHEMA_ATTRIBUTE_STRING
        strings = [name, attribute];
        return schema;
    }

    static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "Secret Service error";
        g_error_free(error);
        throw new InvalidOperationException(message);
    }

    static void ZeroAndFree(IntPtr ptr, int byteCount)
    {
        for (var i = 0; i < byteCount; i++) Marshal.WriteByte(ptr, i, 0);
        Marshal.FreeCoTaskMem(ptr);
    }
}
