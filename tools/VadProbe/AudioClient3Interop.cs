using System.Runtime.InteropServices;

namespace WinStream.Tools.VadProbe;

/// <summary>
/// Minimal <c>IAudioClient3</c> interop. NAudio does not surface the shared-mode
/// engine period, and that period is the driver's own statement of what it can
/// sustain — the one capability worth reading straight from the endpoint.
/// </summary>
internal static class AudioClient3Interop
{
    private static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");

    /// <summary>Periods the endpoint reports, in frames at <paramref name="MixSampleRate"/>.</summary>
    internal readonly record struct EnginePeriods(
        int DefaultFrames,
        int FundamentalFrames,
        int MinimumFrames,
        int MaximumFrames,
        int MixSampleRate,
        int MixChannels)
    {
        public double DefaultMilliseconds => FramesToMilliseconds(DefaultFrames);

        public double MinimumMilliseconds => FramesToMilliseconds(MinimumFrames);

        private double FramesToMilliseconds(int frames) =>
            MixSampleRate <= 0 ? 0 : frames * 1000.0 / MixSampleRate;
    }

    /// <summary>
    /// Reads the endpoint's supported shared-mode periods. Returns false when the
    /// endpoint or OS does not implement <c>IAudioClient3</c>.
    /// </summary>
    /// <param name="endpointId">
    /// WASAPI endpoint id. The device is resolved through a fresh COM enumerator
    /// because NAudio's <c>MMDevice</c> is a managed wrapper, not the COM object.
    /// </param>
    internal static bool TryGetEnginePeriods(
        string endpointId,
        out EnginePeriods periods,
        out string? error)
    {
        periods = default;
        error = null;

        var client = IntPtr.Zero;
        var mixFormat = IntPtr.Zero;

        try
        {
            var enumerator = CreateDeviceEnumerator();
            var hrDevice = enumerator.GetDevice(endpointId, out var device);
            if (hrDevice != 0 || device is null)
            {
                error = $"GetDevice failed with HRESULT 0x{hrDevice:X8}.";
                return false;
            }

            var iid = IID_IAudioClient3;
            var hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out client);
            if (hr != 0 || client == IntPtr.Zero)
            {
                error = $"Activate(IAudioClient3) failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            var audioClient = (IAudioClient3)Marshal.GetObjectForIUnknown(client);

            hr = audioClient.GetMixFormat(out mixFormat);
            if (hr != 0 || mixFormat == IntPtr.Zero)
            {
                error = $"GetMixFormat failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            var format = Marshal.PtrToStructure<WaveFormatEx>(mixFormat);

            hr = audioClient.GetSharedModeEnginePeriod(
                mixFormat,
                out var defaultFrames,
                out var fundamentalFrames,
                out var minFrames,
                out var maxFrames);

            if (hr != 0)
            {
                error = $"GetSharedModeEnginePeriod failed with HRESULT 0x{hr:X8}.";
                return false;
            }

            periods = new EnginePeriods(
                (int)defaultFrames,
                (int)fundamentalFrames,
                (int)minFrames,
                (int)maxFrames,
                format.nSamplesPerSec,
                format.nChannels);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (mixFormat != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }

            if (client != IntPtr.Zero)
            {
                Marshal.Release(client);
            }
        }
    }

    private const uint ClsCtxAll = 23;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>
    /// Activates the enumerator from its CLSID rather than a <c>ComImport</c> coclass.
    /// NAudio registers its own coclass for this CLSID, so <c>new</c> would hand back
    /// NAudio's type and the cast to the local interface would fail.
    /// </summary>
    private static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator, throwOnError: true)
            ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID could not be resolved.");

        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("MMDeviceEnumerator could not be created.");

        return (IMMDeviceEnumerator)instance;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            uint clsCtx,
            IntPtr activationParams,
            out IntPtr instance);

        // Remaining IMMDevice members are unused; declared to keep the vtable honest.
        [PreserveSig]
        int OpenPropertyStore(uint access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        // IAudioClient
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr handle);

        [PreserveSig]
        int GetService(ref Guid iid, out IntPtr service);

        // IAudioClient2
        [PreserveSig]
        int IsOffloadCapable(int category, out bool offloadCapable);

        [PreserveSig]
        int SetClientProperties(IntPtr properties);

        [PreserveSig]
        int GetBufferSizeLimits(IntPtr format, bool eventDriven, out long minDuration, out long maxDuration);

        // IAudioClient3
        [PreserveSig]
        int GetSharedModeEnginePeriod(
            IntPtr format,
            out uint defaultPeriodFrames,
            out uint fundamentalPeriodFrames,
            out uint minPeriodFrames,
            out uint maxPeriodFrames);

        [PreserveSig]
        int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);

        [PreserveSig]
        int InitializeSharedAudioStream(uint streamFlags, uint periodFrames, IntPtr format, IntPtr sessionGuid);
    }
}
