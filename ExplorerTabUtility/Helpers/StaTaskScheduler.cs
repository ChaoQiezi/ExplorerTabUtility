using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Helpers;

/// <summary>
/// A dedicated STA TaskScheduler that runs a genuine Win32 message pump via MsgWaitForMultipleObjectsEx.
/// Ensures COM STA apartments and out-of-context WinEvent hooks receive messages reliably with 0% idle CPU.
/// </summary>
public sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private readonly Thread _staThread;
    private readonly ConcurrentQueue<Task> _tasks = new();
    private readonly AutoResetEvent _taskEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly ManualResetEventSlim _startedEvent = new(false);
    private uint _staThreadId;
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public bool IsOnStaThread => Thread.CurrentThread == _staThread;
    public override int MaximumConcurrencyLevel => 1;

    public StaTaskScheduler()
    {
        _staThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ExplorerWatcher STA Thread"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        _startedEvent.Wait();
    }

    private void Run()
    {
        _staThreadId = WinApi.GetCurrentThreadId();

        // Force creation of this thread's Win32 message queue before signaling ready
        WinApi.PeekMessage(out _, 0, 0, 0, 0);
        _startedEvent.Set();

        var stopHandle = _stopEvent.SafeWaitHandle;
        var taskHandle = _taskEvent.SafeWaitHandle;
        var stopRefAdded = false;
        var taskRefAdded = false;

        try
        {
            stopHandle.DangerousAddRef(ref stopRefAdded);
            taskHandle.DangerousAddRef(ref taskRefAdded);

            var handles = new[]
            {
                stopHandle.DangerousGetHandle(),
                taskHandle.DangerousGetHandle()
            };

            while (Volatile.Read(ref _disposed) == 0)
            {
                if (!PumpMessages())
                    break;

                if (!DrainTasks())
                    break;

                if (Volatile.Read(ref _disposed) != 0)
                    break;

                // Wait for either stop event, new task event, or any Win32/COM/WinEvent message
                var waitResult = WinApi.MsgWaitForMultipleObjectsEx(
                    (uint)handles.Length,
                    handles,
                    WinApi.INFINITE,
                    WinApi.QS_ALLINPUT,
                    WinApi.MWMO_INPUTAVAILABLE);

                if (waitResult == WinApi.WAIT_OBJECT_0 || waitResult == WinApi.WAIT_FAILED)
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Fatal exception in STA message loop: {ex}");
        }
        finally
        {
            if (taskRefAdded) taskHandle.DangerousRelease();
            if (stopRefAdded) stopHandle.DangerousRelease();
        }
    }

    private bool PumpMessages()
    {
        while (WinApi.PeekMessage(out var msg, 0, 0, 0, WinApi.PM_REMOVE))
        {
            if (msg.message == WinApi.WM_QUIT)
                return false;

            WinApi.TranslateMessage(ref msg);
            WinApi.DispatchMessage(ref msg);
        }

        return true;
    }

    private bool DrainTasks()
    {
        while (_tasks.TryDequeue(out var task))
        {
            try
            {
                TryExecuteTask(task);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in STA thread task: {ex}");
            }

            // Interleave Win32/COM message pumping so a burst of tasks cannot starve COM or WinEvents
            if (!PumpMessages())
                return false;
        }

        return true;
    }

    protected override void QueueTask(Task task)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _tasks.Enqueue(task);
        try
        {
            _taskEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // Scheduler is being disposed concurrently
        }
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        return Thread.CurrentThread == _staThread && TryExecuteTask(task);
    }

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        return _tasks.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _stopEvent.Set();
            if (_staThreadId != 0)
                WinApi.PostThreadMessage(_staThreadId, WinApi.WM_QUIT, 0, 0);
        }
        catch
        {
            // Ignore errors while waking up the STA thread
        }

        // Bounded join so Windows shutdown or a hung COM call never blocks indefinitely
        if (Thread.CurrentThread != _staThread && _staThread.IsAlive)
            _staThread.Join(1500);

        _taskEvent.Dispose();
        _stopEvent.Dispose();
        _startedEvent.Dispose();
    }
}
