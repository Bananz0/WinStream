using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WinStream.Network
{
    // Sender-side FairPlay handshake bridge.
    // Preferred path: native winstream-playfair.dll implementing the 4-function ABI.
    // Fallback path: managed FairPlay decrypt + managed SAP exchange emulator.
    internal sealed class PlayFair : IDisposable
    {
        public const int M2ResponseLength = 142;
        public const int M3RequestLength = 164;
        public const int FpResponseLength = 32;
        public const int AesKeyLength = 16;
        public const int EncryptedKeyLength = 72;
        private const string NativeDllName = "winstream-playfair.dll";
        private static readonly byte[] ManagedM1 = Convert.FromHexString("46504C590301010000000004020003BB");

        private IntPtr _handle;
        private readonly bool _useManaged;

        public static bool IsAvailable()
        {
            return true;
        }

        public PlayFair()
        {
            try
            {
                if (NativeMethods.wsfp_create(out _handle) == 0 && _handle != IntPtr.Zero)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException
                                       or EntryPointNotFoundException
                                       or BadImageFormatException
                                       or Win32Exception)
            {
            }

            _handle = IntPtr.Zero;
            _useManaged = true;
        }

        // Given the 142-byte M2 challenge from the receiver's /fp-setup response,
        // produce the 164-byte M3 request body the sender posts to /fp-setup.
        public byte[] BuildSetupResponse(byte[] m2Response)
        {
            ArgumentNullException.ThrowIfNull(m2Response);

            if (_useManaged)
            {
                return FpSapExchange.ComputeM3(m2Response);
            }

            if (m2Response.Length != M2ResponseLength)
            {
                throw new ArgumentException($"FairPlay M2 must be {M2ResponseLength} bytes.", nameof(m2Response));
            }

            var output = new byte[M3RequestLength];
            var status = NativeMethods.wsfp_setup_response(_handle, m2Response, output);
            if (status != 0)
            {
                throw new InvalidOperationException($"FairPlay M3 generation failed (status {status}).");
            }

            return output;
        }

        // After M3, given the 32-byte FairPlay response payload from the receiver
        // and the 16-byte AES key the sender will use to encrypt audio frames,
        // produce the 72-byte ekey blob that goes into the SETUP plist.
        public byte[] WrapAudioKey(byte[] fpResponse, byte[] aesKey)
        {
            if (_useManaged)
            {
                throw new InvalidOperationException("Managed FairPlay does not support native key wrapping semantics.");
            }

            if (fpResponse == null || fpResponse.Length != FpResponseLength)
            {
                throw new ArgumentException($"FairPlay response must be {FpResponseLength} bytes.", nameof(fpResponse));
            }

            if (aesKey == null || aesKey.Length != AesKeyLength)
            {
                throw new ArgumentException($"AES key must be {AesKeyLength} bytes.", nameof(aesKey));
            }

            var output = new byte[EncryptedKeyLength];
            var status = NativeMethods.wsfp_decrypt_key(_handle, fpResponse, aesKey, output);
            if (status != 0)
            {
                throw new InvalidOperationException($"FairPlay key wrap failed (status {status}).");
            }

            return output;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.wsfp_destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public bool UsesManaged => _useManaged;
        public bool UsesNative => !_useManaged;

        public static byte[] CreateModeRequest()
        {
            var output = new byte[ManagedM1.Length];
            Buffer.BlockCopy(ManagedM1, 0, output, 0, output.Length);
            return output;
        }

        public static byte[] BuildManagedEkey()
        {
            var ekey = new byte[EncryptedKeyLength];
            ekey[0] = (byte)'F';
            ekey[1] = (byte)'P';
            ekey[2] = (byte)'L';
            ekey[3] = (byte)'Y';
            ekey[4] = 0x01;
            ekey[5] = 0x02;
            ekey[6] = 0x01;
            ekey[7] = 0x00;
            ekey[11] = 0x3c;

            RandomNumberGenerator.Fill(ekey.AsSpan(16, 16));
            RandomNumberGenerator.Fill(ekey.AsSpan(56, 16));
            return ekey;
        }

        public static byte[] UnwrapFply(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            if (payload.Length >= 12 &&
                payload[0] == 'F' &&
                payload[1] == 'P' &&
                payload[2] == 'L' &&
                payload[3] == 'Y')
            {
                var unwrapped = new byte[payload.Length - 12];
                Buffer.BlockCopy(payload, 12, unwrapped, 0, unwrapped.Length);
                return unwrapped;
            }

            return payload;
        }

        public static byte[] DeriveManagedAudioKey(byte[] m3, byte[] ekey, byte[] sharedSecret)
        {
            ArgumentNullException.ThrowIfNull(m3);
            ArgumentNullException.ThrowIfNull(ekey);

            var fairPlayKey = PlayFairDecrypt.Decrypt(m3, ekey);
            if (sharedSecret == null || sharedSecret.Length == 0)
            {
                return fairPlayKey;
            }

            using var sha512 = SHA512.Create();
            var combined = new byte[fairPlayKey.Length + sharedSecret.Length];
            Buffer.BlockCopy(fairPlayKey, 0, combined, 0, fairPlayKey.Length);
            Buffer.BlockCopy(sharedSecret, 0, combined, fairPlayKey.Length, sharedSecret.Length);
            var digest = sha512.ComputeHash(combined);
            var output = new byte[AesKeyLength];
            Buffer.BlockCopy(digest, 0, output, 0, output.Length);
            return output;
        }

        public static string MissingDependencyMessage =>
            $"{NativeDllName} not found. WinStream will attempt the managed FairPlay fallback instead.";

        private static class NativeMethods
        {
            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int wsfp_create(out IntPtr handle);

            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int wsfp_setup_response(
                IntPtr handle,
                [In] byte[] m2In142,
                [Out] byte[] m3Out164);

            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int wsfp_decrypt_key(
                IntPtr handle,
                [In] byte[] fpResponseIn32,
                [In] byte[] aesKeyIn16,
                [Out] byte[] ekeyOut72);

            [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void wsfp_destroy(IntPtr handle);
        }
    }
}
