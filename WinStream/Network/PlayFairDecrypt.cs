// Port of doubletake's playfair.go: playfairDecrypt and supporting functions.
// Pure C# implementation — no native dependencies.

using System;

namespace WinStream.Network
{
    internal static partial class PlayFairDecrypt
    {
        // --- XOR helpers ---
        private static void ZXor(byte[] src, int srcOff, byte[] dst, int dstOff, int blocks)
        {
            var zk = PlayFairTables.ZKey;
            for (int j = 0; j < blocks; j++)
                for (int i = 0; i < 16; i++)
                    dst[dstOff + j * 16 + i] = (byte)(src[srcOff + j * 16 + i] ^ zk[i]);
        }

        private static void XXor(byte[] src, int srcOff, byte[] dst, int dstOff, int blocks)
        {
            var xk = PlayFairTables.XKey;
            for (int j = 0; j < blocks; j++)
                for (int i = 0; i < 16; i++)
                    dst[dstOff + j * 16 + i] = (byte)(src[srcOff + j * 16 + i] ^ xk[i]);
        }

        private static void TXor(byte[] src, byte[] dst)
        {
            var tk = PlayFairTables.TKey;
            for (int i = 0; i < 16; i++) dst[i] = (byte)(src[i] ^ tk[i]);
        }

        private static void XorBlocks(byte[] a, int aOff, byte[] b, int bOff, byte[] dst, int dOff)
        {
            for (int i = 0; i < 16; i++) dst[dOff + i] = (byte)(a[aOff + i] ^ b[bOff + i]);
        }

        // --- Table index helpers ---
        private static int TableIndex(int i) => (((31 * i) % 0x28) << 8);
        private static int MessageTableIndex(int i) => (((97 * i) % 144) << 8);
        private static int PermuteTable2Index(int i) => (((71 * i) % 144) << 8);

        // --- PermuteBlock1 ---
        private static void PermuteBlock1(byte[] block)
        {
            var s3 = PlayFairTables.TableS3;
            block[0] = s3[block[0]];
            block[4] = s3[0x400 + block[4]];
            block[8] = s3[0x800 + block[8]];
            block[12] = s3[0xc00 + block[12]];

            byte tmp = block[13];
            block[13] = s3[0x100 + block[9]];
            block[9] = s3[0xd00 + block[5]];
            block[5] = s3[0x900 + block[1]];
            block[1] = s3[0x500 + tmp];

            tmp = block[2];
            block[2] = s3[0xa00 + block[10]];
            block[10] = s3[0x200 + tmp];
            tmp = block[6];
            block[6] = s3[0xe00 + block[14]];
            block[14] = s3[0x600 + tmp];

            tmp = block[3];
            block[3] = s3[0xf00 + block[7]];
            block[7] = s3[0x300 + block[11]];
            block[11] = s3[0x700 + block[15]];
            block[15] = s3[0xb00 + tmp];
        }

        // --- PermuteBlock2 ---
        private static void PermuteBlock2(byte[] block, int round)
        {
            var s4 = PlayFairTables.TableS4;
            int Idx(int r, int c) => PermuteTable2Index(r * 16 + c);

            block[0] = s4[Idx(round, 0) + block[0]];
            block[4] = s4[Idx(round, 4) + block[4]];
            block[8] = s4[Idx(round, 8) + block[8]];
            block[12] = s4[Idx(round, 12) + block[12]];

            byte tmp = block[13];
            block[13] = s4[Idx(round, 13) + block[9]];
            block[9] = s4[Idx(round, 9) + block[5]];
            block[5] = s4[Idx(round, 5) + block[1]];
            block[1] = s4[Idx(round, 1) + tmp];

            tmp = block[2];
            block[2] = s4[Idx(round, 2) + block[10]];
            block[10] = s4[Idx(round, 10) + tmp];
            tmp = block[6];
            block[6] = s4[Idx(round, 6) + block[14]];
            block[14] = s4[Idx(round, 14) + tmp];

            tmp = block[3];
            block[3] = s4[Idx(round, 3) + block[7]];
            block[7] = s4[Idx(round, 7) + block[11]];
            block[11] = s4[Idx(round, 11) + block[15]];
            block[15] = s4[Idx(round, 15) + tmp];
        }

        // --- Key schedule ---
        private static void GenerateKeySchedule(byte[] keyMaterial, uint[,] keySchedule)
        {
            byte[] buf = new byte[16];
            TXor(keyMaterial, buf);

            uint[] keyData = new uint[4];
            for (int i = 0; i < 4; i++)
                keyData[i] = BitConverter.ToUInt32(buf, i * 4);

            var s1 = PlayFairTables.TableS1;
            int ti = 0;
            for (int round = 0; round < 11; round++)
            {
                for (int i = 0; i < 4; i++)
                    BitConverter.TryWriteBytes(buf.AsSpan(i * 4), keyData[i]);

                keySchedule[round, 0] = keyData[0];

                int t1 = TableIndex(ti), t2 = TableIndex(ti + 1), t3 = TableIndex(ti + 2), t4 = TableIndex(ti + 3);
                ti += 4;

                buf[0] ^= (byte)(s1[t1 + buf[0x0d]] ^ PlayFairConstants.indexMangle[round]);
                buf[1] ^= s1[t2 + buf[0x0e]];
                buf[2] ^= s1[t3 + buf[0x0f]];
                buf[3] ^= s1[t4 + buf[0x0c]];

                for (int i = 0; i < 4; i++)
                    keyData[i] = BitConverter.ToUInt32(buf, i * 4);

                keySchedule[round, 1] = keyData[1];
                keyData[1] ^= keyData[0];
                keySchedule[round, 2] = keyData[2];
                keyData[2] ^= keyData[1];
                keySchedule[round, 3] = keyData[3];
                keyData[3] ^= keyData[2];
            }
        }

        // --- AES-like cycle ---
        private static void Cycle(byte[] block, uint[,] ks)
        {
            var s5 = PlayFairTables.TableS5;
            var s6 = PlayFairTables.TableS6;
            var s7 = PlayFairTables.TableS7;
            var s8 = PlayFairTables.TableS8;

            uint[] bw = new uint[4];
            for (int i = 0; i < 4; i++)
                bw[i] = BitConverter.ToUInt32(block, i * 4) ^ ks[10, i];
            for (int i = 0; i < 4; i++)
                BitConverter.TryWriteBytes(block.AsSpan(i * 4), bw[i]);

            PermuteBlock1(block);

            for (int round = 0; round < 9; round++)
            {
                int r = 9 - round;
                for (int col = 0; col < 4; col++)
                {
                    uint kv = ks[r, col];
                    byte k0 = (byte)kv, k1 = (byte)(kv >> 8), k2 = (byte)(kv >> 16), k3 = (byte)(kv >> 24);
                    int b = col * 4;
                    uint ab = s5[block[b + 3] ^ k3] ^ s6[block[b + 2] ^ k2] ^ s7[block[b + 1] ^ k1] ^ s8[block[b] ^ k0];
                    BitConverter.TryWriteBytes(block.AsSpan(b), ab);
                }
                PermuteBlock2(block, 8 - round);
            }

            for (int i = 0; i < 4; i++)
            {
                uint v = BitConverter.ToUInt32(block, i * 4) ^ ks[0, i];
                BitConverter.TryWriteBytes(block.AsSpan(i * 4), v);
            }
        }

        // --- Message decryption (continued in part 2) ---
        // This file is split for manageability. See PlayFairDecryptPart2.cs
    }
}
