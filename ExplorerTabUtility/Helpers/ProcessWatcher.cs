using System;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace ExplorerTabUtility.Helpers;

public class ProcessEventArgs(int processId, string processName, int sessionId) : EventArgs
{
    public int ProcessId { get; } = processId;
    public int SessionId { get; } = sessionId;
    public string ProcessName { get; } = processName;
    public DateTime Timestamp { get; } = DateTime.Now;
}

public enum SessionFilter
{
    All,
    Current
}

/// <summary>
/// Event-driven watcher for process termination events using kernel wait notifications (zero polling CPU).
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly string _processName;
    private readonly SessionFilter _sessionFilter;
    private readonly int _currentSessionId;
    private readonly ConcurrentDictionary<int, (Process Process, int SessionId)> _trackedProcesses = new();
    private int _disposed;

    public event EventHandler<ProcessEventArgs>? ProcessTerminated;

    public ProcessWatcher(string processName = "explorer", SessionFilter sessionFilter = SessionFilter.Current)
    {
        _processName = processName ?? throw new ArgumentNullException(nameof(processName));
        _sessionFilter = sessionFilter;
        _currentSessionId = Process.GetCurrentProcess().SessionId;
    }

    /// <summary>
    /// Begins tracking the specified process ID for termination using OS kernel exit notifications.
    /// </summary>
    public bool TrackProcess(int processId)
    {
        if (processId <= 0 || Volatile.Read(ref _disposed) != 0)
            return false;

        if (_trackedProcesses.ContainsKey(processId))
            return true;

        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            var sessionId = process.SessionId;

            if (_sessionFilter == SessionFilter.Current && sessionId != _currentSessionId)
            {
                SafeDisposeProcess(process);
                return false;
            }

            process.Exited += OnProcessExited;
            process.EnableRaisingEvents = true;

            if (!_trackedProcesses.TryAdd(processId, (process, sessionId)))
            {
                process.Exited -= OnProcessExited;
                SafeDisposeProcess(process);
                return true;
            }

            // Close the race window if the process exited before Exited was subscribed
            if (process.HasExited && _trackedProcesses.TryRemove(processId, out var info))
            {
                SafeCleanupAndNotifyTermination(processId, info);
                return false;
            }

            return true;
        }
        catch
        {
            if (process != null)
            {
                try { process.Exited -= OnProcessExited; } catch { /* ignored */ }
                SafeDisposeProcess(process);
            }
            return false;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (sender is not Process process) return;

        try
        {
            var processId = process.Id;
            if (_trackedProcesses.TryRemove(processId, out var info))
            {
                SafeCleanupAndNotifyTermination(processId, info);
            }
        }
        catch
        {
            // Process object might be in an inconsistent state after exit
        }
    }

    private void SafeCleanupAndNotifyTermination(int processId, (Process Process, int SessionId) info)
    {
        try
        {
            info.Process.Exited -= OnProcessExited;
            var handler = ProcessTerminated;
            if (handler != null && Volatile.Read(ref _disposed) == 0)
            {
                var args = new ProcessEventArgs(processId, _processName, info.SessionId);
                handler(this, args);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            SafeDisposeProcess(info.Process);
        }
    }

    private static void SafeDisposeProcess(Process process)
    {
        try
        {
            process.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var kvp in _trackedProcesses)
        {
            if (!_trackedProcesses.TryRemove(kvp.Key, out var info))
                continue;

            try
            {
                info.Process.Exited -= OnProcessExited;
            }
            catch
            {
                // ignored
            }

            SafeDisposeProcess(info.Process);
        }
    }
}
