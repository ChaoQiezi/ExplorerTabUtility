using System;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using H.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Hooks;

public sealed class Keyboard : IHook
{
    private readonly LowLevelKeyboardHook _lowLevelKeyboardHook;
    private volatile HotKeyProfile[] _profiles = [];
    private int _disposed;
    public bool IsHookActive => _lowLevelKeyboardHook.IsStarted;
    public event Action<HotKeyEventArgs>? OnHotKeyProfileTriggered;

    public Keyboard(IReadOnlyCollection<HotKeyProfile> hotkeyProfiles)
    {
        UpdateProfiles(hotkeyProfiles);
        _lowLevelKeyboardHook = new LowLevelKeyboardHook { Handling = true };
        _lowLevelKeyboardHook.Down += LowLevelKeyboardHook_Down;
    }

    public void UpdateProfiles(IEnumerable<HotKeyProfile> hotkeyProfiles)
    {
        // Pre-filter into a compact immutable array for lock-free, zero-allocation hook dispatch
        _profiles = hotkeyProfiles
            .Where(p => p.IsEnabled && !p.IsMouse && p.HotKeys is { Length: > 0 })
            .ToArray();
    }

    public void StartHook()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _lowLevelKeyboardHook.Start();
    }

    public void StopHook()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _lowLevelKeyboardHook.Stop();
    }

    private void LowLevelKeyboardHook_Down(object? sender, KeyboardEventArgs e)
    {
        var handler = OnHotKeyProfileTriggered;
        if (handler == null) return;

        var profiles = _profiles;
        if (profiles.Length == 0) return;

        bool? isFileExplorerForeground = null;
        nint handle = 0;
        for (var i = 0; i < profiles.Length; i++)
        {
            var profile = profiles[i];

            // Skip if keys do not match
            if (!e.Keys.Are(profile.HotKeys!)) continue;

            // Check File Explorer scope only when needed (cached for this key event)
            if (profile.Scope == HotkeyScope.FileExplorer)
            {
                isFileExplorerForeground ??= Helper.IsFileExplorerForeground(out handle);

                if (isFileExplorerForeground == false)
                {
                    handle = 0;
                    continue;
                }
            }

            // Set handled value
            e.IsHandled = profile.IsHandled;

            // Queue the hotkey trigger in a separate thread
#if NET7_0_OR_GREATER
            ThreadPool.QueueUserWorkItem(static s => s.Handler.Invoke(new HotKeyEventArgs(s.Profile, s.Handle)),
                new State(handler, profile, handle), false);
#else
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var s = (State)state!;
                s.Handler.Invoke(new HotKeyEventArgs(s.Profile, s.Handle));
            }, new State(handler, profile, handle));
#endif
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lowLevelKeyboardHook.Down -= LowLevelKeyboardHook_Down;
        _lowLevelKeyboardHook.Stop();
        _lowLevelKeyboardHook.Dispose();
    }

    private readonly struct State(Action<HotKeyEventArgs> handler, HotKeyProfile profile, nint handle)
    {
        public readonly Action<HotKeyEventArgs> Handler = handler;
        public readonly HotKeyProfile Profile = profile;
        public readonly nint Handle = handle;
    }
}
