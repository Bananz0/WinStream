#nullable enable

using System;
using System.Runtime.InteropServices;
using WinStream.Core.Logging;

namespace WinStream.Audio;

/// <summary>
/// Registers the calling thread with the MMCSS "Pro Audio" task so the scheduler
/// treats it as multimedia work rather than ordinary background work.
/// </summary>
/// <remarks>
/// Registration is per-thread, so this must be constructed on the thread it is
/// meant to raise and disposed on that same thread. Only threads WinStream owns
/// qualify: nothing documents that thread exit releases the association, so
/// registering a library's callback thread — which cannot be reverted from
/// anywhere — leaks the handle and leaves it boosted for the process lifetime.
/// </remarks>
internal sealed class MmcssHandle : IDisposable
{
    private const string ProAudioTask = "Pro Audio";

    private IntPtr _handle;

    private MmcssHandle(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Raises the calling thread, or returns null when MMCSS declines. Failure is
    /// never fatal: audio keeps flowing at normal priority.
    /// </summary>
    public static MmcssHandle? TryRegisterCurrentThread()
    {
        uint taskIndex = 0;
        var handle = AvSetMmThreadCharacteristicsW(ProAudioTask, ref taskIndex);
        if (handle == IntPtr.Zero)
        {
            AppLog.Warn(
                "audio",
                $"MMCSS Pro Audio unavailable (error {Marshal.GetLastWin32Error()}); " +
                "continuing at normal priority");
            return null;
        }

        return new MmcssHandle(handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        AvRevertMmThreadCharacteristics(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(
        string taskName,
        ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
