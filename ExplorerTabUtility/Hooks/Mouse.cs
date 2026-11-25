using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using H.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Hooks;

public sealed class Mouse : IHook
{
    private long _lastClickTicks;
    private Key _lastClickKey;
    private readonly LowLevelMouseHook _lowLevelMouseHook;
    private volatile HotKeyProfile[] _profiles = [];
    private int _disposed;
    public bool IsHookActive => _lowLevelMouseHook.IsStarted;
    public event Action<HotKeyEventArgs>? OnHotKeyProfileTriggered;

    public Mouse(IReadOnlyCollection<HotKeyProfile> hotkeyProfiles)
    {
        UpdateProfiles(hotkeyProfiles);
        _lowLevelMouseHook = new LowLevelMouseHook { AddKeyboardKeys = true };
        _lowLevelMouseHook.Down += LowLevelMouseHook_Down;
    }

    public void UpdateProfiles(IEnumerable<HotKeyProfile> hotkeyProfiles)
    {
        // Pre-filter into a compact immutable array for lock-free, zero-allocation hook dispatch
        _profiles = hotkeyProfiles
            .Where(p => p.IsMouse && p.IsEnabled && p.HotKeys is { Length: > 0 })
            .ToArray();
    }

    public void StartHook()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _lowLevelMouseHook.Start();
    }

    public void StopHook()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _lowLevelMouseHook.Stop();
    }

    private void LowLevelMouseHook_Down(object? sender, MouseEventArgs e)
    {
        var handler = OnHotKeyProfileTriggered;
        if (handler == null) return;

        var profiles = _profiles;
        if (profiles.Length == 0) return;

        var isDoubleClick = IsDoubleClick(e.CurrentKey);

        bool? isFileExplorerForeground = null;
        nint handle = 0;
        for (var i = 0; i < profiles.Length; i++)
        {
            var profile = profiles[i];

            // Skip if it requires double-click and it is not
            if (profile.IsDoubleClick && !isDoubleClick) continue;

            // Skip if keys do not match
            if (!e.Keys.Are(profile.HotKeys!)) continue;

            // Check File Explorer scope only when needed (cached for this click event)
            if (profile.Scope == HotkeyScope.FileExplorer)
            {
                isFileExplorerForeground ??= Helper.IsFileExplorerForeground(out handle);

                if (isFileExplorerForeground == false)
                {
                    handle = 0;
                    continue;
                }
            }

            // Queue the hotkey trigger in a separate thread
#if NET7_0_OR_GREATER
            ThreadPool.QueueUserWorkItem(static s => s.Handler.Invoke(new HotKeyEventArgs(s.Profile, s.Handle, s.Position)),
                new State(handler, profile, handle, e.Position), false);
#else
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var s = (State)state!;
                s.Handler.Invoke(new HotKeyEventArgs(s.Profile, s.Handle, s.Position));
            }, new State(handler, profile, handle, e.Position));
#endif
        }
    }

    private bool IsDoubleClick(Key currentKey)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var isDoubleClick = _lastClickTicks != 0 &&
                            _lastClickKey == currentKey &&
                            !Helper.IsTimeUp(_lastClickTicks, 500);

        _lastClickTicks = nowTicks;
        _lastClickKey = currentKey;
        return isDoubleClick;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lowLevelMouseHook.Down -= LowLevelMouseHook_Down;
        _lowLevelMouseHook.Stop();
        _lowLevelMouseHook.Dispose();
    }

    private readonly struct State(Action<HotKeyEventArgs> handler, HotKeyProfile profile, nint handle, Point position)
    {
        public readonly Action<HotKeyEventArgs> Handler = handler;
        public readonly HotKeyProfile Profile = profile;
        public readonly nint Handle = handle;
        public readonly Point Position = position;
    }
}
