using System;
using System.Threading;
using System.Runtime.InteropServices;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Interop;

public sealed class ShellPathComparer : IDisposable
{
    private IShellFolder? _desktopFolder;
    private int _disposed;

    public ShellPathComparer()
    {
        Marshal.ThrowExceptionForHR(WinApi.SHGetDesktopFolder(out var pDesktopFolder));

        try
        {
            _desktopFolder = (IShellFolder)Marshal.GetObjectForIUnknown(pDesktopFolder);
        }
        finally
        {
            if (pDesktopFolder != 0)
                Marshal.Release(pDesktopFolder);
        }
    }

    /// <summary>
    /// Converts a path to a PIDL (Pointer to an Item ID List).
    /// </summary>
    /// <param name="path">The file system path to convert.</param>
    /// <returns>A PIDL representing the file system path. It is the caller's responsibility to release it.</returns>
    public nint GetPidlFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Volatile.Read(ref _disposed) != 0)
            return 0;

        var folder = _desktopFolder;
        if (folder == null)
            return 0;

        uint pdwAttributes = 0;
        var hr = folder.ParseDisplayName(0, 0, path!, out _, out var pidl, ref pdwAttributes);
        if (hr != 0)
        {
            if (pidl != 0)
                Marshal.FreeCoTaskMem(pidl);
            return 0;
        }

        return pidl;
    }

    public static string? GetPathFromPidl(nint pidl, uint sigdnName = WinApi.SIGDN_URL)
    {
        if (pidl == 0) return null;
        var hr = WinApi.SHGetNameFromIDList(pidl, sigdnName, out var path);
        return hr == 0 ? path : null;
    }

    public bool CompareIds(nint pidl1, nint pidl2)
    {
        if (pidl1 == 0 || pidl2 == 0 || Volatile.Read(ref _disposed) != 0)
            return false;

        var folder = _desktopFolder;
        return folder != null && folder.CompareIDs(0, pidl1, pidl2) == 0;
    }

    public static bool CompareWithShGetName(nint pidl1, nint pidl2)
    {
        if (pidl1 == 0 || pidl2 == 0) return false;

        var fsPath1 = GetPathFromPidl(pidl1);
        if (fsPath1 == null) return false;

        var fsPath2 = GetPathFromPidl(pidl2);
        return fsPath2 != null && StringComparer.OrdinalIgnoreCase.Equals(fsPath1, fsPath2);
    }

    public bool IsEquivalent(nint pidl1, nint pidl2)
    {
        if (pidl1 == 0 || pidl2 == 0) return false;

        try
        {
            return CompareIds(pidl1, pidl2) || CompareWithShGetName(pidl1, pidl2);
        }
        catch
        {
            return false;
        }
    }

    public bool IsEquivalent(nint pidl1, string? path2)
    {
        if (pidl1 == 0 || string.IsNullOrWhiteSpace(path2)) return false;

        nint pidl2 = 0;
        try
        {
            pidl2 = GetPidlFromPath(path2);
            return pidl2 != 0 && IsEquivalent(pidl1, pidl2);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl2 != 0)
                Marshal.FreeCoTaskMem(pidl2);
        }
    }

    public bool IsEquivalent(string? path1, string? path2)
    {
        if (string.IsNullOrWhiteSpace(path1) || string.IsNullOrWhiteSpace(path2))
            return false;

        if (StringComparer.OrdinalIgnoreCase.Equals(path1, path2))
            return true;

        nint pidl1 = 0;
        nint pidl2 = 0;
        try
        {
            pidl1 = GetPidlFromPath(path1);
            if (pidl1 == 0) return false;

            pidl2 = GetPidlFromPath(path2);
            return pidl2 != 0 && IsEquivalent(pidl1, pidl2);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl1 != 0)
                Marshal.FreeCoTaskMem(pidl1);

            if (pidl2 != 0)
                Marshal.FreeCoTaskMem(pidl2);
        }
    }

    public bool IsEquivalent(string? targetPath, string? comparePath, nint targetPidl)
    {
        if (string.IsNullOrWhiteSpace(comparePath))
            return false;

        if (!string.IsNullOrWhiteSpace(targetPath) && StringComparer.OrdinalIgnoreCase.Equals(targetPath, comparePath))
            return true;

        if (targetPidl == 0)
            return false;

        nint comparePidl = 0;
        try
        {
            comparePidl = GetPidlFromPath(comparePath);
            return comparePidl != 0 && IsEquivalent(targetPidl, comparePidl);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (comparePidl != 0)
                Marshal.FreeCoTaskMem(comparePidl);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var folder = Interlocked.Exchange(ref _desktopFolder, null);
        if (folder != null)
        {
            try
            {
                if (Marshal.IsComObject(folder))
                    Marshal.ReleaseComObject(folder);
            }
            catch
            {
                // Ignore COM release errors during teardown
            }
        }

        GC.SuppressFinalize(this);
    }

    ~ShellPathComparer()
    {
        Dispose();
    }
}
