using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinStream.Audio
{
    internal sealed class AacEldEncoder : IDisposable
    {
        private const string NativeDllName = "WinStreamAacEld.dll";
        private IntPtr _handle;

        public static bool IsAvailable()
        {
            try
            {
                using var probe = new AacEldEncoder(44100, 2, 192000);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or Win32Exception)
            {
                return false;
            }
        }

        public AacEldEncoder(int sampleRate, int channels, int bitrate)
        {
            var status = NativeMethods.WinStreamAacEldCreate(sampleRate, channels, bitrate, out _handle);
            if (status != 0 || _handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"AAC-ELD encoder initialization failed with status {status}.");
            }
        }

        public byte[] Encode(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var output = new byte[4096];
            var status = NativeMethods.WinStreamAacEldEncode(
                _handle,
                pcmData,
                pcmData.Length,
                output,
                output.Length,
                out var outputLength);
            if (status != 0)
            {
                throw new InvalidOperationException($"AAC-ELD encode failed with status {status}.");
            }

            if (outputLength <= 0)
            {
                return Array.Empty<byte>();
            }

            Array.Resize(ref output, outputLength);
            return output;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.WinStreamAacEldDestroy(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public static string BuildMissingDependencyMessage(Exception ex)
        {
            return ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or Win32Exception
                ? $"{NativeDllName} is required for AirPlay 2 AAC-ELD RTP audio. Build or place the native FDK-AAC wrapper beside WinStream.exe."
                : ex.Message;
        }

        private static class NativeMethods
        {
            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int WinStreamAacEldCreate(
                int sampleRate,
                int channels,
                int bitrate,
                out IntPtr handle);

            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int WinStreamAacEldEncode(
                IntPtr handle,
                byte[] pcmData,
                int pcmLength,
                byte[] output,
                int outputCapacity,
                out int outputLength);

            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void WinStreamAacEldDestroy(IntPtr handle);
        }
    }
}
