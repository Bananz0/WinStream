#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WinStream.Core.Logging;

namespace WinStream.Audio;

/// <summary>
/// Blocks until an absolute send deadline using a high-resolution waitable timer,
/// closing the last millisecond with a yielding spin.
/// </summary>
/// <remarks>
/// The send pump's own fallback blocks on <see cref="WaitHandle.WaitOne(int)"/>,
/// which rounds up to the process timer quantum — often ~15.6 ms, longer than the
/// ~8 ms period Extreme is trying to hold, so the wait overshoots every packet.
/// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> (Windows 10 1803+) is documented
/// for exactly these few-millisecond delays. Win32 interop lives here rather than
/// in Core so the pump stays platform-agnostic.
/// </remarks>
internal sealed class HighResolutionWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const long SpinFloorTicks = TimeSpan.TicksPerMillisecond;

    private static readonly double StopwatchTicksPerTick =
        Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond;

    private readonly ManualResetEvent? _timer;
    private readonly bool _raisedTimerResolution;
    private WaitHandle[]? _waitPair;
    private WaitHandle? _pairedCancelHandle;
    private bool _disposed;

    public HighResolutionWaiter()
    {
        var handle = CreateWaitableTimerExW(
            IntPtr.Zero,
            lpTimerName: null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);

        if (handle == IntPtr.Zero)
        {
            AppLog.Warn(
                "stream",
                $"High-resolution timer unavailable (error {Marshal.GetLastWin32Error()}); " +
                "pacing falls back to the coarse timer");
        }
        else
        {
            _timer = new ManualResetEvent(false)
            {
                SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true)
            };
        }

        // The spin tail and the coarse fallback both round to the process timer
        // quantum, and since Windows 10 2004 a process gets 1 ms only by asking.
        // Held for the pump's lifetime, which is exactly while audio is streaming.
        _raisedTimerResolution = timeBeginPeriod(1) == 0;
    }

    /// <summary>
    /// Waits <paramref name="waitTicks"/> (100 ns units) or until cancelled.
    /// Returns false only when cancelled, matching the pump's wait seam.
    /// </summary>
    public bool WaitUntilDue(long waitTicks, CancellationToken cancellationToken)
    {
        if (waitTicks <= 0)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(waitTicks * StopwatchTicksPerTick);

        var blockTicks = waitTicks - SpinFloorTicks;
        if (blockTicks > 0 && !Block(blockTicks, cancellationToken))
        {
            return false;
        }

        var spinner = new SpinWait();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            // sleep1Threshold -1 keeps SpinOnce off Thread.Sleep(1), which would
            // overshoot the deadline it is meant to land on.
            spinner.SpinOnce(sleep1Threshold: -1);
        }

        return !cancellationToken.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        if (_raisedTimerResolution)
        {
            timeEndPeriod(1);
        }
    }

    private bool Block(long blockTicks, CancellationToken cancellationToken)
    {
        var dueTime = -blockTicks; // Negative means relative, in 100 ns units.
        if (_timer is null ||
            !SetWaitableTimer(
                _timer.SafeWaitHandle,
                ref dueTime,
                lPeriod: 0,
                IntPtr.Zero,
                IntPtr.Zero,
                fResume: false))
        {
            var blockMs = (int)(blockTicks / TimeSpan.TicksPerMillisecond);
            return blockMs <= 0 || !cancellationToken.WaitHandle.WaitOne(blockMs);
        }

        return WaitHandle.WaitAny(PairFor(cancellationToken)) == 0;
    }

    /// <summary>
    /// The token is the same one for a whole session, so the pair is built once
    /// rather than allocated on every packet.
    /// </summary>
    private WaitHandle[] PairFor(CancellationToken cancellationToken)
    {
        var cancelHandle = cancellationToken.WaitHandle;
        if (_waitPair is null || !ReferenceEquals(_pairedCancelHandle, cancelHandle))
        {
            _pairedCancelHandle = cancelHandle;
            _waitPair = [_timer!, cancelHandle];
        }

        return _waitPair;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        ref long pDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
