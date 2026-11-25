using Shell32;
using SHDocVw;
using System;
using System.Linq;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;
using ExplorerTabUtility.UI.Views;

namespace ExplorerTabUtility.Hooks;

using WindowEntry = DualKeyEntry<InternetExplorer, nint?, WindowInfo>;

public class ExplorerWatcher : IHook
{
    private const int MaxClosedWindowsHistory = 200;
    private static int _instanceRunning;
    private static Guid _shellBrowserGuid = typeof(IShellBrowser).GUID;

    private ShellWindows? _shellWindows;
    private ShellPathComparer? _shellPathComparer;
    private StaTaskScheduler? _staTaskScheduler;
    private nint _mainWindowHandle;
    private readonly ConcurrentDictionary<nint, long> _processedHWnds = new();
    private readonly ConcurrentDictionary<nint, byte> _knownTopLevelWindows = new();
    private readonly DualKeyDictionary<InternetExplorer, nint?, WindowInfo> _windowEntryDict = [];
    private readonly List<WindowRecord> _closedWindows = new();
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new(), _processLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1, 1);
    private readonly ProcessWatcher _processWatcher;
    private int _mainExplorerProcessId;
    private Timer? _explorerCheckTimer;
    private int _disposed;
    private bool _shellInitialized;

    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private DShellWindowsEvents_WindowRegisteredEventHandler? _windowRegisteredHandler;

    private string _defaultLocation = string.Empty;
    private volatile bool _reuseTabs = true;
    private volatile bool _isForcingTabs;
    public bool IsHookActive => _isForcingTabs;
    public event Action? OnShellInitialized;

    public ExplorerWatcher()
    {
        if (Interlocked.Exchange(ref _instanceRunning, 1) != 0)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");

        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        StartExplorerProcessCheck();
    }

    public void StartHook()
    {
        _isForcingTabs = true;
    }

    public void StopHook()
    {
        _isForcingTabs = false;
    }

    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public void ClearClosedWindows()
    {
        lock (_closedWindowsLock)
            _closedWindows.Clear();
    }

    public IReadOnlyCollection<WindowRecord> GetWindows()
    {
        var result = new List<WindowRecord>();

        // Take a fast snapshot under lock — NEVER make COM calls while holding _windowEntryDictLock
        List<(InternetExplorer Window, WindowInfo Info)> openSnapshot;
        lock (_windowEntryDictLock)
        {
            openSnapshot = new List<(InternetExplorer, WindowInfo)>(_windowEntryDict.Count);
            foreach (var (ie, info) in _windowEntryDict)
                openSnapshot.Add((ie, info));
        }

        // Build open window records primarily from cached metadata
        foreach (var (ie, info) in openSnapshot)
        {
            var location = info.Location;
            var name = info.Name;
            var hwnd = info.WindowHandle;

            if (string.IsNullOrEmpty(location) || hwnd == 0)
            {
                try
                {
                    if (hwnd == 0)
                        hwnd = new IntPtr(ie.HWND);
                    if (string.IsNullOrEmpty(location))
                        location = TryGetLocation(ie);
                    if (string.IsNullOrEmpty(name))
                        name = TryGetLocationName(ie);

                    info.WindowHandle = hwnd;
                    if (!string.IsNullOrEmpty(location)) info.Location = location;
                    if (!string.IsNullOrEmpty(name)) info.Name = name;
                }
                catch
                {
                    continue;
                }
            }

            if (string.IsNullOrEmpty(location))
                continue;

            result.Add(new WindowRecord(location, hwnd, null, name ?? string.Empty));
        }

        // Add closed windows in reverse order (last closed on top)
        lock (_closedWindowsLock)
        {
            for (var i = _closedWindows.Count - 1; i >= 0; i--)
                result.Add(_closedWindows[i]);
        }

        return result.GroupBy(w => w.Location, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    public async Task SwitchTo(string location, nint windowHandle = 0, string[]? selectedItems = null, bool asTab = true, bool duplicate = false)
    {
        var windowToOpen = new WindowRecord(location, windowHandle, selectedItems);
        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowToOpen);
            return;
        }

        await OpenTabNavigateWithSelection(windowToOpen, windowHandle, duplicate, true);
    }

    public Task<bool> IsExplorerEmptySpaceAsync(System.Drawing.Point point)
    {
        return RunInStaThread(() => Helper.IsExplorerEmptySpace(point));
    }

    public nint SearchForTab(string targetPath)
    {
        return SearchForTabAsync(targetPath).GetAwaiter().GetResult();
    }

    private async Task<nint> SearchForTabAsync(string targetPath, nint excludeWindowHandle = 0)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return 0;

        // Snapshot entries quickly under lock — ZERO COM calls under lock
        List<(InternetExplorer Window, WindowInfo Info, nint TabHandle)> candidates;
        lock (_windowEntryDictLock)
        {
            candidates = new List<(InternetExplorer, WindowInfo, nint)>(_windowEntryDict.Count);
            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                if (!tabHandle.HasValue || tabHandle.Value == 0)
                    continue;
                if (excludeWindowHandle != 0 && windowInfo.WindowHandle == excludeWindowHandle)
                    continue;
                if (excludeWindowHandle == 0 && !Helper.IsTimeUp(windowInfo.CreatedAt, 1_000))
                    continue;

                candidates.Add((window, windowInfo, tabHandle.Value));
            }
        }

        if (candidates.Count == 0)
            return 0;

        // Pass 1: Fast O(N) in-memory normalized string comparison (zero COM / zero Shell PIDL calls)
        for (var i = 0; i < candidates.Count; i++)
        {
            var (_, info, tabHandle) = candidates[i];
            if (!string.IsNullOrEmpty(info.Location) &&
                StringComparer.OrdinalIgnoreCase.Equals(targetPath, info.Location) &&
                WinApi.IsWindow(tabHandle))
            {
                return tabHandle;
            }
        }

        // Pass 2: PIDL equivalence fallback on STA thread (ShellPathComparer's IShellFolder is STA)
        return await RunInStaThread(() =>
        {
            var comparer = _shellPathComparer;
            if (comparer == null) return (nint)0;

            nint targetPidl = 0;
            try
            {
                targetPidl = comparer.GetPidlFromPath(targetPath);
                if (targetPidl == 0) return (nint)0;

                for (var i = 0; i < candidates.Count; i++)
                {
                    var (window, info, tabHandle) = candidates[i];
                    if (!WinApi.IsWindow(tabHandle))
                        continue;

                    try
                    {
                        var comparePath = info.Location;
                        if (string.IsNullOrEmpty(comparePath))
                        {
                            comparePath = TryGetLocation(window);
                            if (!string.IsNullOrEmpty(comparePath))
                                info.Location = comparePath;
                        }

                        if (!string.IsNullOrEmpty(comparePath) && comparer.IsEquivalent(targetPath, comparePath, targetPidl))
                            return tabHandle;
                    }
                    catch
                    {
                        // Skip stale/inaccessible tab without aborting search for remaining tabs
                    }
                }

                return (nint)0;
            }
            catch
            {
                return (nint)0;
            }
            finally
            {
                if (targetPidl != 0)
                    Marshal.FreeCoTaskMem(targetPidl);
            }
        });
    }

    public async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        if (windowHandle == 0 || tabHandle == 0 || !WinApi.IsWindow(windowHandle))
            return;

        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length <= 1) return;

        var activeTab = tabs[0];
        if (activeTab == tabHandle) return;

        // Verify target tab actually belongs to this window
        if (Array.IndexOf(tabs, tabHandle) < 0) return;

        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            // Wait briefly for Explorer to switch tabs (short timeout avoids 500ms stall if i was already the active tab)
            var previousTab = activeTab;
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != previousTab,
                timeMs: 120,
                sleepMs: 10);
        }
    }

    public void SelectLastTab(nint windowHandle)
    {
        var count = Helper.GetAllExplorerTabs(windowHandle).Count();
        if (count > 0)
            SelectTabByIndex(windowHandle, count - 1);
    }

    public void SelectTabByIndex(nint windowHandle, int index)
    {
        if (windowHandle == 0 || index < 0) return;
        // Send 0xA221 magic command (CTRL + 1...n) with timeout so a busy Explorer can never deadlock us
        WinApi.SendMessageSafe(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1, 300);
    }

    public async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false, bool lockToOpenWindows = true)
    {
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0 || !Helper.IsFileExplorerWindow(windowHandle))
        {
            await OpenNewWindowWithSelection(new WindowRecord(string.Empty), lockToOpenWindows: lockToOpenWindows);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Post 0xA21B magic command (CTRL + T) asynchronously
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }

    public async Task Open(string? location, bool asTab, nint windowHandle, int delay = 0)
    {
        if (delay > 0)
            await Task.Delay(delay);

        var normalizedPath = Helper.NormalizeLocation(location ?? string.Empty);

        if (normalizedPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            System.IO.File.Exists(normalizedPath))
        {
            try
            {
                Helper.BypassWinForegroundRestrictions();
                Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
                return;
            }
            catch
            {
                // Fall through if ShellExecute failed
            }
        }

        if (!asTab)
        {
            await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
            return;
        }

        // Empty path (e.g. Win+E default shortcut)
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            if (_reuseTabs && GetWindowEntryCount() > 0 && !string.IsNullOrWhiteSpace(_defaultLocation))
            {
                var existingDefaultTab = await SearchForTabAsync(_defaultLocation);
                if (existingDefaultTab != 0)
                {
                    var parentWindow = WinApi.GetParent(existingDefaultTab);
                    if (parentWindow != 0)
                    {
                        await SelectTabByHandle(parentWindow, existingDefaultTab);
                        WinApi.RestoreWindowToForeground(parentWindow);
                        return;
                    }
                }
            }

            // Opens default location natively in Explorer — avoids calling Navigate2("") and stalling 5s!
            await RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        if (GetWindowEntryCount() > 0)
        {
            OpenNewTab(windowHandle, normalizedPath);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }

    public void OpenNewTab(nint windowHandle, string location)
    {
        _ = OpenTabNavigateWithSelectionSafe(new WindowRecord(location, windowHandle), windowHandle);
    }

    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        if (!TryGetWindowAndInfoByTabHandle(activeTabHandle, out var window, out var info))
            return;

        var (location, selectedItems) = await RunInStaThread(() =>
        {
            var loc = !string.IsNullOrEmpty(info.Location) ? info.Location! : TryGetLocation(window);
            var sel = TryGetSelectedItems(window);
            return (loc, sel);
        });

        if (string.IsNullOrEmpty(location)) return;

        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowRecord);
            return;
        }

        await OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }

    public async Task ReopenClosedTab(bool asTab, nint windowHandle = 0)
    {
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
        {
            closedWindow = _closedWindows.LastOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.Location) &&
                !StringComparer.OrdinalIgnoreCase.Equals(w.Location, _defaultLocation));
            if (closedWindow == null) return;
            _closedWindows.Remove(closedWindow);
        }

        if (!asTab)
        {
            closedWindow.ClosedAtTicks = Stopwatch.GetTimestamp();
            await OpenNewWindowWithSelection(closedWindow);
            return;
        }

        await OpenTabNavigateWithSelection(closedWindow, windowHandle);
    }

    public async Task DetachCurrentTab(nint windowHandle)
    {
        if (!Helper.HasMultipleExplorerTabs(windowHandle))
            return;

        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        if (!TryGetWindowAndInfoByTabHandle(activeTabHandle, out var window, out var info))
            return;

        var (location, selectedItems) = await RunInStaThread(() =>
        {
            var loc = !string.IsNullOrEmpty(info.Location) ? info.Location! : TryGetLocation(window);
            var sel = TryGetSelectedItems(window);
            return (loc, sel);
        });

        if (string.IsNullOrEmpty(location)) return;

        var windowRecord = new WindowRecord(location, windowHandle, selectedItems, closedAtTicks: Stopwatch.GetTimestamp());

        // Post 0xA021 magic command (CTRL + W) asynchronously so Explorer cannot deadlock
        WinApi.PostMessage(activeTabHandle, WinApi.WM_COMMAND, 0xA021, 1);

        await OpenNewWindowWithSelection(windowRecord);
    }

    public void SetTargetWindow(nint windowHandle)
    {
        if (Helper.IsFileExplorerWindow(windowHandle))
        {
            _mainWindowHandle = windowHandle;
            _knownTopLevelWindows.TryAdd(windowHandle, 0);
        }
    }

    public void NavigateBackForward(nint windowHandle, bool isForward)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        _ = RunInStaThread(() =>
        {
            try
            {
                if (isForward) window.GoForward();
                else window.GoBack();
            }
            catch
            {
                // Will throw if there is no further history
            }
        });
    }

    private void PreventWindowHiding(nint hWnd)
    {
        if (hWnd == 0) return;

        _processedHWnds[hWnd] = Stopwatch.GetTimestamp();

        // Lazily prune expired entries without allocating Timer/Task.Delay objects
        if (_processedHWnds.Count > 32)
        {
            foreach (var kvp in _processedHWnds)
            {
                if (Helper.IsTimeUp(kvp.Value, 7_000))
                    _processedHWnds.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void OnWindowShown(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        // Fast O(1) rejections — runs on pumping STA thread; NEVER acquire locks or make COM calls here!
        if (!_isForcingTabs || idObject != 0 || idChild != 0 || hWnd == 0) return;

        // NEVER hide an existing / already-known Explorer window (prevents tab-bar corruption & sleep-wake invisibility!)
        if (hWnd == _mainWindowHandle || _knownTopLevelWindows.ContainsKey(hWnd))
            return;

        // Check if OnShellWindowRegistered already cleared this hWnd
        if (_processedHWnds.TryRemove(hWnd, out var markedAt) && !Helper.IsTimeUp(markedAt, 7_000))
            return;

        // Only hide brand-new CabinetWClass windows if there is already an existing tracked Explorer window
        if (_knownTopLevelWindows.IsEmpty)
            return;

        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass"))
            return;

        // Never hide a window that already has multiple tabs or when Ctrl+Shift is held
        if (Helper.HasMultipleExplorerTabs(hWnd) || Helper.IsCtrlShiftDown())
            return;

        Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);
    }

    private (InternetExplorer Window, WindowInfo Info, nint TabHandle, bool ShouldRestore)? GetRecentlyCreatedWindowOnSta()
    {
        var shellWindows = _shellWindows;
        if (shellWindows == null) return null;

        int count;
        try
        {
            count = shellWindows.Count;
        }
        catch
        {
            return null;
        }

        // When a new window is registered, it's typically at the end of the collection
        for (var i = count - 1; i >= 0; i--)
        {
            object? rawItem;
            try
            {
                rawItem = shellWindows.Item(i);
            }
            catch
            {
                continue;
            }

            if (rawItem is not InternetExplorer window)
            {
                ReleaseComObjectSafe(rawItem, final: false);
                continue;
            }

            bool alreadyTracked;
            lock (_windowEntryDictLock)
                alreadyTracked = _windowEntryDict.ContainsPrimary(window);

            if (alreadyTracked)
            {
                // Balance the +1 RCW refcount added by shellWindows.Item(i)
                ReleaseComObjectSafe(window, final: false);
                continue;
            }

            bool seenBefore;
            try
            {
                seenBefore = window.GetProperty("seenBefore") is not null;
                if (!seenBefore)
                    window.PutProperty("seenBefore", true);
            }
            catch
            {
                ReleaseComObjectSafe(window, final: false);
                continue;
            }

            if (seenBefore)
            {
                ReleaseComObjectSafe(window, final: false);
                continue;
            }

            nint hWnd;
            try
            {
                hWnd = new IntPtr(window.HWND);
            }
            catch
            {
                ReleaseComObjectSafe(window, final: true);
                continue;
            }

            // Read initial metadata & tab handle right here on the STA thread (zero extra thread hops!)
            var location = TryGetLocation(window);
            var name = TryGetLocationName(window);
            var tabHandle = TryGetTabHandleOnSta(window);

            var windowInfo = new WindowInfo
            {
                WindowHandle = hWnd,
                Location = string.IsNullOrEmpty(location) ? null : location,
                Name = string.IsNullOrEmpty(name) ? null : name
            };

            var isFirstWindow = false;
            lock (_windowEntryDictLock)
            {
                _windowEntryDict.Add(window, windowInfo);
                if (tabHandle != 0)
                    _windowEntryDict.TryUpdateOptionalKey(window, tabHandle);

                if (_windowEntryDict.Count == 1 || _mainWindowHandle == 0 || !Helper.IsFileExplorerWindow(_mainWindowHandle))
                {
                    _mainWindowHandle = hWnd;
                    _knownTopLevelWindows.TryAdd(hWnd, 0);
                    isFirstWindow = _windowEntryDict.Count == 1;
                }
            }

            var shouldRestore = false;
            if (isFirstWindow && SettingsManager.RestorePreviousWindows)
            {
                lock (_closedWindowsLock)
                    shouldRestore = _closedWindows.Any(w => w.Restore);
            }

            // Track secondary explorer.exe PIDs automatically (zero polling!)
            if (hWnd != 0 && WinApi.GetWindowThreadProcessId(hWnd, out var pid) != 0 && pid != 0)
                _processWatcher.TrackProcess((int)pid);

            return (window, windowInfo, tabHandle, shouldRestore);
        }

        return null;
    }

    private void OnShellWindowRegistered(int lCookie)
    {
        // Immediately offload off the COM event callback so explorer.exe is NEVER blocked
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var self = (ExplorerWatcher)state!;
            _ = self.HandleShellWindowRegisteredAsync();
        }, this);
    }

    private async Task HandleShellWindowRegisteredAsync()
    {
        var showAgain = true;
        nint hWnd = 0;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var shouldOpenAsWindow = Helper.IsCtrlShiftDown();

            var found = await Helper.DoUntilNotDefaultAsync(
                () => RunInStaThread(GetRecentlyCreatedWindowOnSta),
                timeMs: 2_500,
                sleepMs: 40);

            if (found is not { } result)
                return;

            var (window, windowInfo, tabHandle, shouldRestore) = result;
            hWnd = windowInfo.WindowHandle;

            if (shouldRestore)
                _ = RestorePreviousWindows();

            if (tabHandle == 0)
                _ = EnsureTabHandleAsync(window, windowInfo);

            if (shouldOpenAsWindow)
            {
                _knownTopLevelWindows.TryAdd(hWnd, 0);
                PreventWindowHiding(hWnd);
                await RunInStaThread(() => HookWindowEventsOnSta(window, windowInfo));
                return;
            }

            var location = windowInfo.Location;
            if (string.IsNullOrEmpty(location))
            {
                location = await RunInStaThread(() => TryGetLocation(window)) ?? string.Empty;
                if (!string.IsNullOrEmpty(location))
                    windowInfo.Location = location;
            }

            // Control Panel — do not convert to tab
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}", StringComparison.OrdinalIgnoreCase))
            {
                _knownTopLevelWindows.TryAdd(hWnd, 0);
                PreventWindowHiding(hWnd);
                await RemoveAndReleaseWindowAsync(window, windowInfo);
                return;
            }

            // Check if this is a brand-new standalone single-tab window while another Explorer window exists
            var isExistingWindow = hWnd == _mainWindowHandle ||
                                   _knownTopLevelWindows.ContainsKey(hWnd) ||
                                   Helper.HasMultipleExplorerTabs(hWnd);

            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    !isExistingWindow &&
                                    GetWindowEntryCount() > 1;

            // Check detached tab BEFORE hiding the window (prevents hide+show frame glitch on detached tabs!)
            var closedWindow = await TryGetRecentlyClosedWindowAsync(location);
            var isRecentlyClosed = closedWindow != null;

            if (isRecentlyClosed && closedWindow!.SelectedItems is { Length: > 0 })
                await RunInStaThread(() => TrySelectItems(window, closedWindow.SelectedItems));

            if (shouldReopenAsTab && !isRecentlyClosed)
            {
                var targetMainWindow = GetMainWindowHWnd(hWnd);
                if (targetMainWindow != 0)
                {
                    showAgain = false;
                    Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);

                    var selectedItems = await RunInStaThread(() => TryGetSelectedItems(window));

                    _ = OpenTabNavigateWithSelectionSafe(
                        new WindowRecord(location, hWnd, selectedItems),
                        targetMainWindow,
                        excludeWindowHandle: hWnd);

                    await QuitAndRemoveWindowAsync(window, windowInfo);
                    Helper.HiddenWindows.TryRemove(hWnd, out _);
                    return;
                }
            }

            // Keep this window/tab: register as known top-level window and hook events IMMEDIATELY (0ms delay!)
            _knownTopLevelWindows.TryAdd(hWnd, 0);
            PreventWindowHiding(hWnd);
            await RunInStaThread(() => HookWindowEventsOnSta(window, windowInfo));

            // If this could be a reattached tab whose OnQuit fired slightly after WindowRegistered,
            // restore its selection asynchronously without blocking HookWindowEvents or ShowWindow
            if (!isRecentlyClosed &&
                !string.IsNullOrEmpty(location) &&
                !StringComparer.OrdinalIgnoreCase.Equals(location, _defaultLocation))
            {
                _ = TryRestoreDelayedReattachedSelectionAsync(window, windowInfo, location);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HandleShellWindowRegisteredAsync error: {ex.Message}");
        }
        finally
        {
            // ONLY restore if we actually hid this window! (Zero wasted polling for normal '+' tab opens!)
            if (showAgain && hWnd != 0 && Helper.HiddenWindows.ContainsKey(hWnd))
            {
                Helper.ShowWindow(hWnd, removeCache: false);
                _ = Task.Delay(2_000).ContinueWith(t => Helper.HiddenWindows.TryRemove(hWnd, out _), TaskScheduler.Default);
            }
        }
    }

    private async Task TryRestoreDelayedReattachedSelectionAsync(InternetExplorer window, WindowInfo windowInfo, string location)
    {
        try
        {
            var closedWindow = await Helper.DoUntilNotDefaultAsync(
                () => TryGetRecentlyClosedWindowAsync(location),
                timeMs: 500,
                sleepMs: 50);

            if (closedWindow?.SelectedItems is { Length: > 0 } && Volatile.Read(ref windowInfo.IsDisposed) == 0)
                await RunInStaThread(() => TrySelectItems(window, closedWindow.SelectedItems));
        }
        catch
        {
            // Ignored
        }
    }

    private async Task EnsureTabHandleAsync(InternetExplorer window, WindowInfo windowInfo)
    {
        try
        {
            var handle = await Helper.DoUntilNotDefaultAsync(
                () => RunInStaThread(() => Volatile.Read(ref windowInfo.IsDisposed) == 0 ? TryGetTabHandleOnSta(window) : (nint)0),
                timeMs: 1_500,
                sleepMs: 50);

            if (handle != 0 && Volatile.Read(ref windowInfo.IsDisposed) == 0)
            {
                lock (_windowEntryDictLock)
                    _windowEntryDict.TryUpdateOptionalKey(window, handle);
            }
        }
        catch
        {
            // Ignored
        }
    }

    private void HookWindowEventsOnSta(InternetExplorer window, WindowInfo windowInfo)
    {
        if (Volatile.Read(ref windowInfo.IsDisposed) != 0)
            return;

        // Strongly-typed handlers so we can cleanly unsubscribe later
        windowInfo.OnQuitHandler = () =>
        {
            if (Interlocked.Exchange(ref windowInfo.IsDisposed, 1) != 0)
                return;

            var location = windowInfo.Location ?? string.Empty;
            var locationName = windowInfo.Name ?? string.Empty;
            var hwnd = windowInfo.WindowHandle;

            // Read selected items while tab is closing (NO locks held, safe RCW cleanup, skip for default page)
            string[]? selectedItems = null;
            if (!string.IsNullOrEmpty(location) &&
                !StringComparer.OrdinalIgnoreCase.Equals(location, _defaultLocation))
            {
                selectedItems = TryGetSelectedItems(window);
            }

            if (!string.IsNullOrEmpty(location))
            {
                var windowRecord = new WindowRecord(
                    location,
                    hwnd,
                    selectedItems,
                    locationName,
                    closedAtTicks: Stopwatch.GetTimestamp());

                AddClosedWindow(windowRecord);
            }

            RemoveFromTrackingDict(window, hwnd);

            // Offload Unadvise + FinalReleaseComObject to next STA pump turn so we NEVER call Unadvise re-entrantly inside OnQuit!
            PostToStaThread(() => UnhookAndReleaseWindowOnSta(window, windowInfo));
        };

        // Always hook NavigateComplete2 so cached Location/Name stay fresh for SearchForTab, Duplicate, TabSearch, and Restore!
        windowInfo.OnNavigateHandler = (object _, ref object urlObj) =>
        {
            if (Volatile.Read(ref windowInfo.IsDisposed) != 0)
                return;

            var url = urlObj as string;
            if (!string.IsNullOrWhiteSpace(url))
                windowInfo.Location = Helper.NormalizeLocation(url!);

            // Coalesce async metadata refresh onto next STA pump turn (NEVER re-enter Explorer COM synchronously inside NavigateComplete2!)
            if (Interlocked.CompareExchange(ref windowInfo.IsRefreshingMetadata, 1, 0) == 0)
            {
                var needLocationFallback = string.IsNullOrWhiteSpace(url);
                PostToStaThread(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref windowInfo.IsDisposed) != 0) return;

                        if (needLocationFallback)
                        {
                            var loc = TryGetLocation(window);
                            if (!string.IsNullOrEmpty(loc))
                                windowInfo.Location = loc;
                        }

                        var locName = TryGetLocationName(window);
                        if (!string.IsNullOrEmpty(locName))
                            windowInfo.Name = locName;
                    }
                    finally
                    {
                        Volatile.Write(ref windowInfo.IsRefreshingMetadata, 0);
                    }
                });
            }
        };

        try
        {
            window.OnQuit += windowInfo.OnQuitHandler;
            window.NavigateComplete2 += windowInfo.OnNavigateHandler;

            // Populate initial Location/Name if still missing and verify window is still alive
            if (string.IsNullOrEmpty(windowInfo.Location))
                windowInfo.Location = TryGetLocation(window);
            if (string.IsNullOrEmpty(windowInfo.Name))
                windowInfo.Name = TryGetLocationName(window);

            _ = window.HWND;
        }
        catch
        {
            // Window closed immediately before hooks could finish
            if (Interlocked.Exchange(ref windowInfo.IsDisposed, 1) == 0)
            {
                RemoveFromTrackingDict(window, windowInfo.WindowHandle);
                UnhookAndReleaseWindowOnSta(window, windowInfo);
            }
        }
    }

    private void AddClosedWindow(WindowRecord record)
    {
        lock (_closedWindowsLock)
        {
            _closedWindows.Add(record);
            if (_closedWindows.Count > MaxClosedWindowsHistory)
                _closedWindows.RemoveRange(0, _closedWindows.Count - MaxClosedWindowsHistory);
        }
    }

    private void RemoveFromTrackingDict(InternetExplorer window, nint hwnd)
    {
        var stillHasOtherTabsInWindow = false;
        lock (_windowEntryDictLock)
        {
            _windowEntryDict.Remove(window);
            if (hwnd != 0)
            {
                foreach (var (_, entryInfo) in _windowEntryDict)
                {
                    if (entryInfo.WindowHandle == hwnd)
                    {
                        stillHasOtherTabsInWindow = true;
                        break;
                    }
                }
            }
        }

        if (hwnd != 0 && !stillHasOtherTabsInWindow)
        {
            _knownTopLevelWindows.TryRemove(hwnd, out _);
            if (_mainWindowHandle == hwnd)
                _mainWindowHandle = 0;
        }
    }

    private static void UnhookAndReleaseWindowOnSta(InternetExplorer window, WindowInfo windowInfo)
    {
        try
        {
            if (windowInfo.OnQuitHandler != null)
            {
                window.OnQuit -= windowInfo.OnQuitHandler;
                windowInfo.OnQuitHandler = null;
            }
        }
        catch
        {
            // Window COM server may already be gone
        }

        try
        {
            if (windowInfo.OnNavigateHandler != null)
            {
                window.NavigateComplete2 -= windowInfo.OnNavigateHandler;
                windowInfo.OnNavigateHandler = null;
            }
        }
        catch
        {
            // Window COM server may already be gone
        }

        ReleaseComObjectSafe(window, final: true);
    }

    private Task RemoveAndReleaseWindowAsync(InternetExplorer window, WindowInfo windowInfo)
    {
        if (Interlocked.Exchange(ref windowInfo.IsDisposed, 1) != 0)
            return Task.CompletedTask;

        RemoveFromTrackingDict(window, windowInfo.WindowHandle);
        return RunInStaThread(() => UnhookAndReleaseWindowOnSta(window, windowInfo));
    }

    private Task QuitAndRemoveWindowAsync(InternetExplorer window, WindowInfo windowInfo)
    {
        if (Interlocked.Exchange(ref windowInfo.IsDisposed, 1) != 0)
            return Task.CompletedTask;

        RemoveFromTrackingDict(window, windowInfo.WindowHandle);
        return RunInStaThread(() =>
        {
            try
            {
                window.Quit();
            }
            catch
            {
                // Window may already be closing
            }

            UnhookAndReleaseWindowOnSta(window, windowInfo);
        });
    }

    private async Task RestorePreviousWindows()
    {
        try
        {
            // MUST show WPF dialog on the WPF UI Dispatcher thread — NEVER on _staTaskScheduler!
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            var result = await dispatcher.InvokeAsync(() => CustomMessageBox.Show(
                "Do you want to restore previously opened windows?",
                "Explorer Tab Utility",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question)).Task;

            List<WindowRecord> toRestore;
            lock (_closedWindowsLock)
            {
                toRestore = _closedWindows.Where(r => r.Restore).ToList();
                foreach (var record in toRestore)
                    record.Restore = false;
            }

            if (result != MessageBoxResult.Yes) return;

            foreach (var record in toRestore)
            {
                await OpenTabNavigateWithSelectionSafe(record);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RestorePreviousWindows error: {ex.Message}");
        }
    }

    private async Task OpenNewWindowWithSelection(WindowRecord windowToOpen, bool duplicate = true, bool lockToOpenWindows = true)
    {
        var lockAcquired = false;
        if (lockToOpenWindows)
        {
            lockAcquired = await _toOpenWindowsLock.WaitAsync(TimeSpan.FromSeconds(5));
            if (!lockAcquired) return;
        }

        try
        {
            windowToOpen.ClosedAtTicks = Stopwatch.GetTimestamp();
            AddClosedWindow(windowToOpen);

            var hasSelection = windowToOpen.SelectedItems?.Length > 0;

            nint[]? currentWindows = null;
            if (hasSelection)
                currentWindows = Helper.GetAllExplorerWindows().ToArray();

            Helper.BypassWinForegroundRestrictions();

            var location = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            await RunInStaThread(() =>
            {
                Shell? shell = null;
                try
                {
                    shell = new Shell();
                    shell.ShellExecute(location, "", "", duplicate ? "opennewwindow" : "open");
                }
                catch
                {
                    // Ignored
                }
                finally
                {
                    ReleaseComObjectSafe(shell, final: true);
                }
            });

            if (!hasSelection) return;

            var newWindowHandle = await Helper.ListenForNewExplorerWindowAsync(currentWindows ?? []);
            if (newWindowHandle == 0) return;

            var window = FindWindowByHwnd(newWindowHandle);
            if (window == null) return;

            await RunInStaThread(() => TrySelectItems(window, windowToOpen.SelectedItems));
        }
        finally
        {
            if (lockAcquired)
                _toOpenWindowsLock.Release();
        }
    }

    private async Task OpenTabNavigateWithSelectionSafe(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false, nint excludeWindowHandle = 0)
    {
        try
        {
            await OpenTabNavigateWithSelection(windowToOpen, windowHandle, isDuplicate, forceTabReuse, excludeWindowHandle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenTabNavigateWithSelection error: {ex.Message}");
        }
    }

    private async Task OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false, nint excludeWindowHandle = 0)
    {
        var lockAcquired = await _toOpenWindowsLock.WaitAsync(TimeSpan.FromSeconds(5));
        if (!lockAcquired) return;

        nint mainWindowHWnd = 0;

        try
        {
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && GetWindowEntryCount() > 0 && !string.IsNullOrWhiteSpace(windowToOpen.Location))
            {
                var existingTab = await SearchForTabAsync(windowToOpen.Location, excludeWindowHandle);
                if (existingTab != 0)
                {
                    windowHandle = WinApi.GetParent(existingTab);
                    if (windowHandle != 0)
                    {
                        await SelectTabByHandle(windowHandle, existingTab);
                        WinApi.RestoreWindowToForeground(windowHandle);
                        return;
                    }
                }
            }

            // Get the target main Explorer window
            mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(windowToOpen.Handle);

            if (mainWindowHWnd == 0)
            {
                await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false);
                return;
            }

            // Snapshot current tabs in this window
            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();

            // Request Explorer to open a new tab
            await RequestToOpenNewTab(mainWindowHWnd, lockToOpenWindows: false);

            // Wait for the new ShellTabWindowClass HWND
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
            if (newTabHandle == 0) return;

            // Wait for our ShellWindows registration to index the new tab handle
            var foundWindow = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 30);
            if (foundWindow == null) return;
            var targetWindow = foundWindow;

            // Release _toOpenWindowsLock BEFORE waiting for slow folder navigation so concurrent tab opens are never blocked!
            _toOpenWindowsLock.Release();
            lockAcquired = false;

            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            // If target is empty (default Explorer start page) and no items to select, nothing to navigate!
            if (string.IsNullOrWhiteSpace(windowToOpen.Location))
                return;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            DWebBrowserEvents2_NavigateComplete2EventHandler navigateHandler = null!;
            navigateHandler = (object _, ref object _) =>
            {
                tcs.TrySetResult(true);
            };

            var subscribed = await RunInStaThread(() =>
            {
                try
                {
                    targetWindow.NavigateComplete2 += navigateHandler;
                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (!subscribed) return;

            try
            {
                await Navigate(targetWindow, windowToOpen.Location);

                using var cts = new CancellationTokenSource();
                var timeoutTask = Task.Delay(5_000, cts.Token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                cts.Cancel();

                if (completed == tcs.Task && windowToOpen.SelectedItems is { Length: > 0 })
                    await RunInStaThread(() => TrySelectItems(targetWindow, windowToOpen.SelectedItems));
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            finally
            {
                // ALWAYS unsubscribe — even on timeout or exception (fixes COM event sink leak!)
                await RunInStaThread(() =>
                {
                    try { targetWindow.NavigateComplete2 -= navigateHandler; } catch { /* ignored */ }
                });
            }
        }
        finally
        {
            if (lockAcquired)
                _toOpenWindowsLock.Release();
        }
    }

    private async Task<WindowRecord?> TryGetRecentlyClosedWindowAsync(string location, int maxAge = 2_000)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        // Fast-path: collect ONLY records closed within maxAge in the CURRENT session under brief lock.
        // 99.9% of the time (e.g. clicking '+'), this list is EMPTY -> returns in <1µs with ZERO COM calls!
        List<WindowRecord>? recentCandidates = null;
        lock (_closedWindowsLock)
        {
            for (var i = _closedWindows.Count - 1; i >= 0; i--)
            {
                var record = _closedWindows[i];
                // ClosedAtTicks == 0 means deserialized from previous session — NEVER treat as freshly closed!
                if (record.ClosedAtTicks == 0 || Helper.IsTimeUp(record.ClosedAtTicks, maxAge))
                    continue;

                recentCandidates ??= new List<WindowRecord>(4);
                recentCandidates.Add(record);
            }
        }

        if (recentCandidates == null)
            return null;

        // Pass 1: Fast case-insensitive string check (zero COM / zero PIDL!)
        for (var i = 0; i < recentCandidates.Count; i++)
        {
            var candidate = recentCandidates[i];
            if (StringComparer.OrdinalIgnoreCase.Equals(location, candidate.Location))
            {
                lock (_closedWindowsLock)
                    _closedWindows.Remove(candidate);
                return candidate;
            }
        }

        // Pass 2: PIDL equivalence fallback on STA thread (OUTSIDE _closedWindowsLock!)
        var matched = await RunInStaThread(() =>
        {
            var comparer = _shellPathComparer;
            if (comparer == null) return null;

            nint targetPidl = 0;
            try
            {
                targetPidl = comparer.GetPidlFromPath(location);
                if (targetPidl == 0) return null;

                for (var i = 0; i < recentCandidates.Count; i++)
                {
                    var candidate = recentCandidates[i];
                    if (comparer.IsEquivalent(location, candidate.Location, targetPidl))
                        return candidate;
                }

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (targetPidl != 0)
                    Marshal.FreeCoTaskMem(targetPidl);
            }
        });

        if (matched != null)
        {
            lock (_closedWindowsLock)
                _closedWindows.Remove(matched);
        }

        return matched;
    }

    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (_mainWindowHandle != otherThan && Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one (prefer the one with the most tabs, oldest z-order)
        var found = allWindows
            .Where(h => h != otherThan)
            .Reverse()
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count())
            .FirstOrDefault();

        if (found != 0)
        {
            _mainWindowHandle = found;
            _knownTopLevelWindows.TryAdd(found, 0);
            return found;
        }

        return otherThan != 0 && Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
    }

    private int GetWindowEntryCount()
    {
        lock (_windowEntryDictLock)
            return _windowEntryDict.Count;
    }

    private InternetExplorer? FindWindowByHwnd(nint targetHwnd)
    {
        lock (_windowEntryDictLock)
        {
            foreach (var (window, info) in _windowEntryDict)
            {
                if (info.WindowHandle == targetHwnd)
                    return window;
            }
        }
        return null;
    }

    private static nint TryGetTabHandleOnSta(InternetExplorer window)
    {
        IShellBrowser? shellBrowser = null;
        try
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            if (window is not Interop.IServiceProvider sp) return 0;

            sp.QueryService(ref _shellBrowserGuid, ref _shellBrowserGuid, out shellBrowser);
            if (shellBrowser == null) return 0;

            shellBrowser.GetWindow(out var hWnd);
            return hWnd;
        }
        catch
        {
            return 0;
        }
        finally
        {
            ReleaseComObjectSafe(shellBrowser, final: false);
        }
    }

    private static nint GetActiveTabHandle(nint windowHandle)
    {
        if (windowHandle == 0) return 0;
        // Active tab is always at the top of the z-order
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }

    private InternetExplorer? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        lock (_windowEntryDictLock)
            return _windowEntryDict.TryGetValue(tabHandle, out InternetExplorer? foundWindow) ? foundWindow : null;
    }

    private bool TryGetWindowAndInfoByTabHandle(nint tabHandle, out InternetExplorer window, out WindowInfo info)
    {
        lock (_windowEntryDictLock)
        {
            if (_windowEntryDict.TryGetByOptional(tabHandle, out window, out info))
                return true;
        }

        window = null!;
        info = null!;
        return false;
    }

    private static string[]? TryGetSelectedItems(InternetExplorer window)
    {
        ShellFolderView? doc = null;
        FolderItems? selectedItems = null;
        try
        {
            doc = window.Document as ShellFolderView;
            if (doc == null) return null;

            selectedItems = doc.SelectedItems();
            if (selectedItems == null) return null;

            var count = selectedItems.Count;
            if (count <= 0) return null;

            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                FolderItem? item = null;
                try
                {
                    item = selectedItems.Item(i);
                    result[i] = item?.Name ?? string.Empty;
                }
                finally
                {
                    ReleaseComObjectSafe(item, final: false);
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObjectSafe(selectedItems, final: false);
            ReleaseComObjectSafe(doc, final: false);
        }
    }

    private static void TrySelectItems(InternetExplorer window, string[]? names)
    {
        if (names == null || names.Length == 0) return;

        ShellFolderView? document = null;
        Folder? folder = null;
        try
        {
            document = window.Document as ShellFolderView;
            if (document == null) return;

            folder = document.Folder;
            if (folder == null) return;

            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (string.IsNullOrEmpty(name)) continue;

                object? item = null;
                try
                {
                    item = folder.ParseName(name);
                    if (item == null) continue;
                    object itemRef = item;
                    document.SelectItem(ref itemRef, 1);
                }
                finally
                {
                    ReleaseComObjectSafe(item, final: false);
                }
            }
        }
        catch
        {
            // Ignored
        }
        finally
        {
            ReleaseComObjectSafe(folder, final: false);
            ReleaseComObjectSafe(document, final: false);
        }
    }

    private static string TryGetLocation(InternetExplorer window)
    {
        try
        {
            var path = window.LocationURL;
            if (!string.IsNullOrWhiteSpace(path))
                return Helper.NormalizeLocation(path);
        }
        catch
        {
            return string.Empty;
        }

        // Recycle Bin, This PC, Home, etc.
        ShellFolderView? doc = null;
        Folder2? folder = null;
        FolderItem? self = null;
        try
        {
            doc = window.Document as ShellFolderView;
            folder = doc?.Folder as Folder2;
            self = folder?.Self;
            var path = self?.Path;
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Helper.NormalizeLocation(path!);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            ReleaseComObjectSafe(self, final: false);
            ReleaseComObjectSafe(folder, final: false);
            ReleaseComObjectSafe(doc, final: false);
        }
    }

    private static string TryGetLocationName(InternetExplorer window)
    {
        try
        {
            return window.LocationName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task Navigate(InternetExplorer window, string path)
    {
        if (path.IndexOf('#') < 0 && path.IndexOf("%23", StringComparison.OrdinalIgnoreCase) < 0)
        {
            await RunInStaThread(() => window.Navigate2(path));
            return;
        }

        await RunInStaThread(() =>
        {
            Shell? shell = null;
            Folder? folder = null;
            try
            {
                shell = new Shell();
                folder = shell.NameSpace(path);
                if (folder != null)
                    window.Navigate2(folder);
            }
            finally
            {
                ReleaseComObjectSafe(folder, final: true);
                ReleaseComObjectSafe(shell, final: true);
            }
        });
    }

    /// <summary>
    /// Always enqueues the action onto the STA thread's queue (never executes inline),
    /// ensuring COM event callbacks return to explorer.exe before follow-up COM calls run.
    /// </summary>
    private void PostToStaThread(Action action)
    {
        var scheduler = _staTaskScheduler;
        if (scheduler == null || scheduler.IsDisposed)
            return;

        _ = Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, scheduler);
    }

    private Task RunInStaThread(Action action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        var scheduler = _staTaskScheduler;
        if (scheduler == null || scheduler.IsDisposed)
            return Task.CompletedTask;

        if (scheduler.IsOnStaThread)
        {
            action();
            return Task.CompletedTask;
        }

        return Task.Factory.StartNew(action, ct, tco, scheduler);
    }

    private Task<T> RunInStaThread<T>(Func<T> action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        var scheduler = _staTaskScheduler;
        if (scheduler == null || scheduler.IsDisposed)
            return Task.FromResult(default(T)!);

        if (scheduler.IsOnStaThread)
            return Task.FromResult(action());

        return Task.Factory.StartNew(action, ct, tco, scheduler);
    }

    private static void ReleaseComObjectSafe(object? comObj, bool final)
    {
        if (comObj == null) return;
        try
        {
            if (!Marshal.IsComObject(comObj)) return;
            if (final)
                Marshal.FinalReleaseComObject(comObj);
            else
                Marshal.ReleaseComObject(comObj);
        }
        catch
        {
            // Ignore COM release errors on already-disconnected RCWs
        }
    }

    private void StartExplorerProcessCheck()
    {
        lock (_processLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _explorerCheckTimer?.Dispose();
            _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1000);
        }
    }

    private void CheckForMainExplorer(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var pid = Helper.GetMainExplorerProcessId();
        if (pid <= 0) return;

        var initializedNow = false;
        lock (_processLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _mainExplorerProcessId != 0)
                return;

            _explorerCheckTimer?.Dispose();
            _explorerCheckTimer = null;

            _mainExplorerProcessId = pid;
            _processWatcher.TrackProcess(pid);

            try
            {
                InitializeShellObjects();
                initializedNow = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeShellObjects failed: {ex.Message}");
                _mainExplorerProcessId = 0;
                DisposeShellObjects();
                _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 1000, 1000);
            }
        }

        if (initializedNow)
            OnShellInitialized?.Invoke();
    }

    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        // Main explorer.exe process terminated — cleanly tear down and wait for restart
        var wasMainExplorer = false;
        lock (_processLock)
        {
            if (e.ProcessId == _mainExplorerProcessId)
            {
                _mainExplorerProcessId = 0;
                DisposeShellObjects();
                wasMainExplorer = true;
            }
        }

        if (wasMainExplorer)
        {
            StartExplorerProcessCheck();
            return;
        }

        // Secondary explorer.exe process terminated — prune dead windows WITHOUT holding locks across COM calls
        _ = PruneDeadWindowsAsync(recordForRestore: true);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || Volatile.Read(ref _disposed) != 0)
            return;

        // Safety recovery after Sleep/Hibernate (fixes Issue #133):
        // Restore any accidentally hidden windows and prune stale handles
        Helper.RestoreAllHiddenWindows();
        _processedHWnds.Clear();
        _ = PruneDeadWindowsAsync(recordForRestore: false);
    }

    private async Task PruneDeadWindowsAsync(bool recordForRestore)
    {
        List<(InternetExplorer Window, WindowInfo Info)> snapshot;
        lock (_windowEntryDictLock)
        {
            if (_windowEntryDict.Count == 0) return;
            snapshot = new List<(InternetExplorer, WindowInfo)>(_windowEntryDict.Count);
            foreach (var (w, info) in _windowEntryDict)
                snapshot.Add((w, info));
        }

        var deadEntries = await RunInStaThread(() =>
        {
            var dead = new List<(InternetExplorer Window, WindowInfo Info)>();
            foreach (var (window, info) in snapshot)
            {
                try
                {
                    var hwnd = new IntPtr(window.HWND);
                    if (!WinApi.IsWindow(hwnd))
                        dead.Add((window, info));
                }
                catch
                {
                    dead.Add((window, info));
                }
            }
            return dead;
        });

        if (deadEntries == null || deadEntries.Count == 0)
            return;

        var crashRecords = new List<WindowRecord>();
        foreach (var (window, info) in deadEntries)
        {
            if (Interlocked.Exchange(ref info.IsDisposed, 1) != 0)
                continue;

            RemoveFromTrackingDict(window, info.WindowHandle);

            if (recordForRestore && !string.IsNullOrEmpty(info.Location))
                crashRecords.Add(new WindowRecord(info.Location!, info.WindowHandle, name: info.Name ?? string.Empty));

            _ = RunInStaThread(() => UnhookAndReleaseWindowOnSta(window, info));
        }

        if (crashRecords.Count > 0)
        {
            var markRestore = SettingsManager.RestorePreviousWindows && GetWindowEntryCount() == 0;
            lock (_closedWindowsLock)
            {
                foreach (var rec in crashRecords)
                {
                    rec.Restore = markRestore;
                    _closedWindows.Add(rec);
                }
            }
        }
    }

    private void InitializeShellObjects()
    {
        if (_shellInitialized) return;

        var scheduler = new StaTaskScheduler();
        _staTaskScheduler = scheduler;

        if (SettingsManager.ClosedWindows != null)
        {
            lock (_closedWindowsLock)
            {
                _closedWindows.Clear();
                _closedWindows.AddRange(SettingsManager.ClosedWindows);
            }
        }

        // Create STA COM objects & register out-of-context WinEventHook ON the pumping STA thread!
        Task.Factory.StartNew(() =>
        {
            _shellPathComparer = new ShellPathComparer();
            _shellWindows = new ShellWindows();
            _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);

            // Hook already-open windows FIRST before subscribing to WindowRegistered (eliminates startup races)
            var hasOpen = false;
            var count = _shellWindows.Count;
            for (var i = 0; i < count; i++)
            {
                object? rawItem;
                try { rawItem = _shellWindows.Item(i); }
                catch { continue; }

                if (rawItem is not InternetExplorer window)
                {
                    ReleaseComObjectSafe(rawItem, final: false);
                    continue;
                }

                nint hWnd;
                try
                {
                    hWnd = new IntPtr(window.HWND);
                    window.PutProperty("seenBefore", true);
                }
                catch
                {
                    ReleaseComObjectSafe(window, final: true);
                    continue;
                }

                hasOpen = true;
                var location = TryGetLocation(window);
                var name = TryGetLocationName(window);
                var tabHandle = TryGetTabHandleOnSta(window);

                var windowInfo = new WindowInfo
                {
                    WindowHandle = hWnd,
                    Location = string.IsNullOrEmpty(location) ? null : location,
                    Name = string.IsNullOrEmpty(name) ? null : name
                };

                lock (_windowEntryDictLock)
                {
                    _windowEntryDict.Add(window, windowInfo);
                    if (tabHandle != 0)
                        _windowEntryDict.TryUpdateOptionalKey(window, tabHandle);
                }

                if (hWnd != 0)
                {
                    _knownTopLevelWindows.TryAdd(hWnd, 0);
                    if (_mainWindowHandle == 0)
                        _mainWindowHandle = hWnd;

                    if (WinApi.GetWindowThreadProcessId(hWnd, out var pid) != 0 && pid != 0)
                        _processWatcher.TrackProcess((int)pid);
                }

                HookWindowEventsOnSta(window, windowInfo);
            }

            if (hasOpen)
            {
                lock (_closedWindowsLock)
                {
                    foreach (var w in _closedWindows)
                        w.Restore = false;
                }
            }

            // Subscribe to global WindowRegistered event on pumping STA thread
            _windowRegisteredHandler = OnShellWindowRegistered;
            _shellWindows.WindowRegistered += _windowRegisteredHandler;

            // Register WinEvent hook ON the pumping STA thread with WINEVENT_SKIPOWNPROCESS
            _eventObjectShowHookCallback = OnWindowShown;
            _eventObjectShowHookId = WinApi.SetWinEventHook(
                WinApi.EVENT_OBJECT_SHOW,
                WinApi.EVENT_OBJECT_SHOW,
                0,
                _eventObjectShowHookCallback,
                0,
                0,
                WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS);
        }, CancellationToken.None, TaskCreationOptions.None, scheduler).GetAwaiter().GetResult();

        _shellInitialized = true;
    }

    private void DisposeShellObjects()
    {
        if (!_shellInitialized && _staTaskScheduler == null)
            return;

        _shellInitialized = false;

        // Safety: restore any hidden windows before tearing down
        Helper.RestoreAllHiddenWindows();

        PersistWindows();

        // Snapshot and clear managed tracking collections under lock
        List<(InternetExplorer Window, WindowInfo Info)> entries;
        lock (_windowEntryDictLock)
        {
            entries = new List<(InternetExplorer, WindowInfo)>(_windowEntryDict.Count);
            foreach (var (w, info) in _windowEntryDict)
                entries.Add((w, info));
            _windowEntryDict.Clear();
        }

        _knownTopLevelWindows.Clear();
        _processedHWnds.Clear();
        _mainWindowHandle = 0;

        var scheduler = _staTaskScheduler;
        _staTaskScheduler = null;

        var shellWindows = _shellWindows;
        _shellWindows = null;

        var shellPathComparer = _shellPathComparer;
        _shellPathComparer = null;

        var winRegHandler = _windowRegisteredHandler;
        _windowRegisteredHandler = null;

        var hookId = _eventObjectShowHookId;
        _eventObjectShowHookId = 0;
        _eventObjectShowHookCallback = null;

        Action cleanupAction = () =>
        {
            if (hookId != 0)
            {
                try { WinApi.UnhookWinEvent(hookId); } catch { /* ignored */ }
            }

            if (shellWindows != null && winRegHandler != null)
            {
                try { shellWindows.WindowRegistered -= winRegHandler; } catch { /* Explorer may have crashed */ }
            }

            foreach (var (window, info) in entries)
            {
                Interlocked.Exchange(ref info.IsDisposed, 1);
                UnhookAndReleaseWindowOnSta(window, info);
            }

            ReleaseComObjectSafe(shellWindows, final: true);

            try { shellPathComparer?.Dispose(); } catch { /* ignored */ }
        };

        // Run UnhookWinEvent & COM teardown on the owning STA thread (with bounded wait so shutdown never hangs!)
        if (scheduler != null && !scheduler.IsDisposed)
        {
            try
            {
                var task = Task.Factory.StartNew(cleanupAction, CancellationToken.None, TaskCreationOptions.None, scheduler);
                task.Wait(1000);
            }
            catch
            {
                // Bounded wait expired or scheduler faulted
            }

            scheduler.Dispose();
        }
        else
        {
            cleanupAction();
        }
    }

    private void PersistWindows()
    {
        var store = new List<WindowRecord>();
        lock (_closedWindowsLock)
        {
            if (SettingsManager.SaveClosedHistory) store.AddRange(_closedWindows);
            _closedWindows.Clear();
        }

        // Save currently open windows (explorer crash / system restart, logoff / AppExit)
        if (SettingsManager.RestorePreviousWindows)
        {
            lock (_windowEntryDictLock)
            {
                foreach (var info in _windowEntryDict.Values)
                {
                    if (!string.IsNullOrEmpty(info.Location))
                        store.Add(new WindowRecord(info.Location!, name: info.Name ?? string.Empty, restore: true));
                }
            }
        }

        // DistinctBy location (keeping latest)
        var distinctItems = store
            .GroupBy(w => w.Location, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToArray();

        // TakeLast 100
        SettingsManager.ClosedWindows = distinctItems.Skip(Math.Max(0, distinctItems.Length - 100)).ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        lock (_processLock)
        {
            _explorerCheckTimer?.Dispose();
            _explorerCheckTimer = null;
            _mainExplorerProcessId = 0;
            DisposeShellObjects();
        }

        _processWatcher.ProcessTerminated -= OnExplorerProcessTerminated;
        _processWatcher.Dispose();
        _toOpenWindowsLock.Dispose();
        Interlocked.Exchange(ref _instanceRunning, 0);
        GC.SuppressFinalize(this);
    }
}
