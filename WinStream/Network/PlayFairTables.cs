// Port of doubletake's playfair.go + playfair_tables_compact.go init()
// Pure C# implementation of the FairPlay SAP cipher for AirPlay 2.

using System;
using System.Numerics;

namespace WinStream.Network
{
    /// <summary>
    /// Expands the compact base64 lookup tables at first use.
    /// Ported from doubletake playfair_tables_compact.go init().
    /// </summary>
    internal static class PlayFairTables
    {
        internal static readonly byte[,] MessageKey = new byte[4, 144];
        internal static readonly byte[,] MessageIv = new byte[4, 16];
        internal static readonly byte[] ZKey = new byte[16];
        internal static readonly byte[] XKey = new byte[16];
        internal static readonly byte[] TKey = new byte[16];
        internal static readonly byte[] TableS1 = new byte[10240];
        internal static readonly byte[] TableS2 = new byte[36864];
        internal static readonly byte[] TableS3 = new byte[4096];
        internal static readonly byte[] TableS4 = new byte[36864];
        internal static readonly uint[] TableS5 = new uint[256];
        internal static readonly uint[] TableS6 = new uint[256];
        internal static readonly uint[] TableS7 = new uint[256];
        internal static readonly uint[] TableS8 = new uint[256];
        internal static readonly uint[] TableS9 = new uint[1024];
        internal static readonly byte[] TableS10 = new byte[4096];

        static PlayFairTables()
        {
            var mk = Convert.FromBase64String(PlayFairConstants.messageKeyB64);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 144; j++)
                    MessageKey[i, j] = mk[i * 144 + j];

            var mi = Convert.FromBase64String(PlayFairConstants.messageIvB64);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 16; j++)
                    MessageIv[i, j] = mi[i * 16 + j];

            Buffer.BlockCopy(Convert.FromBase64String(PlayFairConstants.zKeyB64), 0, ZKey, 0, 16);
            Buffer.BlockCopy(Convert.FromBase64String(PlayFairConstants.xKeyB64), 0, XKey, 0, 16);
            Buffer.BlockCopy(Convert.FromBase64String(PlayFairConstants.tKeyB64), 0, TKey, 0, 16);

            ExpandModel(TableS1, Convert.FromBase64String(PlayFairConstants.s1DefsB64), Convert.FromBase64String(PlayFairConstants.s1BasesB64));
            ExpandModel(TableS2, Convert.FromBase64String(PlayFairConstants.s2DefsB64), Convert.FromBase64String(PlayFairConstants.s2BasesB64));
            Buffer.BlockCopy(Convert.FromBase64String(PlayFairConstants.tableS3B64), 0, TableS3, 0, 4096);
            ExpandModel(TableS4, Convert.FromBase64String(PlayFairConstants.s4DefsB64), Convert.FromBase64String(PlayFairConstants.s4BasesB64));
            ExpandModel(TableS10, Convert.FromBase64String(PlayFairConstants.s10DefsB64), Convert.FromBase64String(PlayFairConstants.s10BasesB64));

            var perm = Convert.FromBase64String(PlayFairConstants.s5BasePermB64);
            var maps58 = Convert.FromBase64String(PlayFairConstants.s58MapsB64);
            var maps9 = Convert.FromBase64String(PlayFairConstants.s9MapsB64);
            var tables58 = new uint[][] { TableS5, TableS6, TableS7, TableS8 };

            for (int t = 0; t < 4; t++)
            {
                for (int i = 0; i < 256; i++)
                {
                    byte p = perm[i];
                    uint w = 0;
                    for (int lane = 0; lane < 4; lane++)
                    {
                        int mOff = (t * 4 + lane) * 17;
                        byte[] m = new byte[17];
                        Buffer.BlockCopy(maps58, mOff, m, 0, 17);
                        w |= (uint)AffineByte((byte)i, p, m) << (8 * lane);
                    }
                    tables58[t][i] = w;
                }
            }

            for (int ch = 0; ch < 4; ch++)
            {
                for (int i = 0; i < 256; i++)
                {
                    byte p = perm[i];
                    uint w = 0;
                    for (int lane = 0; lane < 4; lane++)
                    {
                        int mOff = (ch * 4 + lane) * 17;
                        byte[] m = new byte[17];
                        Buffer.BlockCopy(maps9, mOff, m, 0, 17);
                        w |= (uint)AffineByte((byte)i, p, m) << (8 * lane);
                    }
                    TableS9[ch * 256 + i] = w;
                }
            }
        }

        private static byte Parity8(byte x) => (byte)(BitOperations.PopCount(x) & 1);

        private static byte AffineByte(byte i, byte p, byte[] m)
        {
            byte y = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                byte v = (byte)(Parity8((byte)(m[bit] & i)) ^ Parity8((byte)(m[8 + bit] & p)) ^ ((m[16] >> bit) & 1));
                y |= (byte)(v << bit);
            }
            return y;
        }

        private static void ExpandModel(byte[] dst, byte[] defsRaw, byte[] bases)
        {
            int rows = defsRaw.Length / 5;
            for (int r = 0; r < rows; r++)
            {
                int o = r * 5;
                int baseIdx = defsRaw[o];
                byte op = defsRaw[o + 1];
                int a = defsRaw[o + 2], b = defsRaw[o + 3], c = defsRaw[o + 4];
                int baseOff = baseIdx * 256;
                int rowOff = r * 256;

                switch (op)
                {
                    case 0:
                        Buffer.BlockCopy(bases, baseOff, dst, rowOff, 256);
                        break;
                    case 1:
                        for (int x = 0; x < 256; x++)
                            dst[rowOff + x] = bases[baseOff + ((a * x + b) & 255)];
                        break;
                    case 2:
                        for (int x = 0; x < 256; x++)
                            dst[rowOff + x] = (byte)(bases[baseOff + ((a * x + b) & 255)] ^ c);
                        break;
                    case 3:
                        for (int x = 0; x < 256; x++)
                            dst[rowOff + x] = (byte)((bases[baseOff + ((a * x + b) & 255)] + c) & 255);
                        break;
                    case 4:
                        for (int x = 0; x < 256; x++)
                            dst[rowOff + x] = bases[baseOff + (((a * x) & 255) ^ b)];
                        break;
                    case 5:
                        for (int x = 0; x < 256; x++)
                            dst[rowOff + x] = (byte)(bases[baseOff + (((a * x) & 255) ^ b)] ^ c);
                        break;
                }
            }
        }
    }
}
