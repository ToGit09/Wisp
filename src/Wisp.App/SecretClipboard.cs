using System.Runtime.InteropServices;
using Avalonia.Input.Platform;

namespace Wisp.App;

/// <summary>
/// Clipboard for one-time codes. On Windows the code is marked so that the clipboard history (Win+V)
/// and cloud clipboard sync skip it; elsewhere the normal clipboard is used.
/// </summary>
static partial class SecretClipboard
{
    public static async Task SetAsync(IClipboard? clipboard, string text)
    {
        if (OperatingSystem.IsWindows() && TrySetWindows(text)) return;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    public static async Task ClearIfAsync(IClipboard? clipboard, string text)
    {
        if (clipboard is null) return;
        try
        {
            if (await clipboard.TryGetTextAsync() == text) await clipboard.ClearAsync();
        }
        catch (Exception) { /* another program holds the clipboard: leave it */ }
    }

    // ───────────────────────── Windows ─────────────────────────

    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    static bool TrySetWindows(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) break;
            if (attempt == 4) return false;
            Thread.Sleep(20);
        }
        try
        {
            if (!EmptyClipboard()) return false;
            if (!SetGlobal(CF_UNICODETEXT, (text + "\0").ToCharArray().SelectMany(BitConverter.GetBytes).ToArray())) return false;
            // Documented Windows formats: not in the clipboard history, not synced to other devices.
            var zero = BitConverter.GetBytes(0);
            SetGlobal(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), zero);
            SetGlobal(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), zero);
            SetGlobal(RegisterClipboardFormatW("CanUploadToCloudClipboard"), zero);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    static bool SetGlobal(uint format, byte[] data)
    {
        if (format == 0) return false;
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return false;
        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) != IntPtr.Zero) return true; // the clipboard owns the memory now
        GlobalFree(handle);
        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr handle);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr handle);
}
