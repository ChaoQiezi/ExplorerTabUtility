using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

// ReSharper disable ArrangeTypeMemberModifiers
// ReSharper disable InconsistentNaming

namespace ExplorerTabUtility.Managers;

public static class ClipboardManager
{
    // P/Invoke declarations
    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);
    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint hMem);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint hMem);

    // Flags for GlobalAlloc
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

    // Clipboard formats
    private const uint CF_BITMAP = 2;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_ENHMETAFILE = 14;

    public static string GetClipboardText()
    {
        var opened = false;
        try
        {
            opened = OpenClipboard(default);
            if (!opened)
                return string.Empty;

            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == default)
                return string.Empty;

            var lockedData = GlobalLock(handle);
            if (lockedData == default)
                return string.Empty;

            try
            {
                var size = GlobalSize(handle);
                if (size == 0 || size > int.MaxValue)
                    return string.Empty;

                var buffer = new byte[(int)size];
                Marshal.Copy(lockedData, buffer, 0, buffer.Length);
                return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error getting clipboard text: {e}");
            return string.Empty;
        }
        finally
        {
            if (opened)
                CloseClipboard();
        }
    }

    public static void SetClipboardText(string text)
    {
        var opened = false;
        try
        {
            opened = OpenClipboard(default);
            if (!opened)
                return;

            EmptyClipboard();

            text = $"{text.TrimEnd('\0')}\0";
            var buffer = Encoding.Unicode.GetBytes(text);
            var size = (nuint)buffer.Length;

            var handle = GlobalAlloc(GHND, size);
            if (handle == default)
                return;

            var pointer = GlobalLock(handle);
            if (pointer == default)
            {
                GlobalFree(handle);
                return;
            }

            Marshal.Copy(buffer, 0, pointer, buffer.Length);
            // Must unlock BEFORE transferring ownership to SetClipboardData
            GlobalUnlock(handle);

            if (SetClipboardData(CF_UNICODETEXT, handle) == default)
                GlobalFree(handle);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error setting clipboard text: {e}");
        }
        finally
        {
            if (opened)
                CloseClipboard();
        }
    }

    // Get the current clipboard data as a dictionary of format and data pairs
    public static Dictionary<uint, byte[]> GetClipboardData()
    {
        var data = new Dictionary<uint, byte[]>();
        var opened = false;
        try
        {
            opened = OpenClipboard(default);
            if (!opened)
                return data;

            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (format is CF_BITMAP or CF_ENHMETAFILE) continue; // Ignore these formats (might cause problems)

                var handle = GetClipboardData(format);
                if (handle == default)
                    continue;

                var lockedData = GlobalLock(handle);
                if (lockedData == default)
                    continue;

                try
                {
                    var size = GlobalSize(handle);
                    if (size == 0 || size > int.MaxValue)
                        continue;

                    var buffer = new byte[(int)size];
                    Marshal.Copy(lockedData, buffer, 0, buffer.Length);
                    data[format] = buffer;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error getting clipboard data: {e}");
        }
        finally
        {
            if (opened)
                CloseClipboard();
        }
        return data;
    }

    // Set the clipboard data from a dictionary of format and data pairs
    public static void SetClipboardData(Dictionary<uint, byte[]> data)
    {
        if (data.Count == 0) return;

        var opened = false;
        try
        {
            opened = OpenClipboard(default);
            if (!opened)
                return;

            EmptyClipboard();
            foreach (var pair in data)
            {
                var format = pair.Key;
                var buffer = pair.Value;
                if (buffer == default || buffer.Length == 0) continue;

                var handle = GlobalAlloc(GHND, (nuint)buffer.Length);
                if (handle == default) continue;

                var pointer = GlobalLock(handle);
                if (pointer == default)
                {
                    GlobalFree(handle);
                    continue;
                }

                Marshal.Copy(buffer, 0, pointer, buffer.Length);
                GlobalUnlock(handle);

                if (SetClipboardData(format, handle) == default)
                    GlobalFree(handle);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error setting clipboard data: {e}");
        }
        finally
        {
            if (opened)
                CloseClipboard();
        }
    }

    // Clear the clipboard
    public static void ClearClipboard()
    {
        var opened = false;
        try
        {
            opened = OpenClipboard(default);
            if (!opened) return;

            EmptyClipboard();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error clearing clipboard: {e}");
        }
        finally
        {
            if (opened)
                CloseClipboard();
        }
    }
}
