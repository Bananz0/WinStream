// PlayFairDecrypt part 2: DecryptMessage, ModifiedMD5, SapHash, GenerateSessionKey, main decrypt.
// Ported from doubletake's playfair.go.

using System;

namespace WinStream.Network
{
    internal static partial class PlayFairDecrypt
    {
        private static void DecryptMessage(byte[] messageIn, byte[] decryptedMessage)
        {
            byte[] buffer = new byte[16];
            byte mode = messageIn[12];
            var s2 = PlayFairTables.TableS2;
            var s9 = PlayFairTables.TableS9;
            var s10 = PlayFairTables.TableS10;

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 16; j++)
                    buffer[j] = (mode == 3) ? messageIn[(0x80 - 0x10 * i) + j] : messageIn[(0x10 * (i + 1)) + j];

                for (int j = 0; j < 9; j++)
                {
                    int bse = 0x80 - 0x10 * j;
                    int MTI(int idx) => MessageTableIndex(bse + idx);

                    buffer[0x0] = (byte)(s2[MTI(0x0) + buffer[0x0]] ^ PlayFairTables.MessageKey[mode, bse + 0x0]);
                    buffer[0x4] = (byte)(s2[MTI(0x4) + buffer[0x4]] ^ PlayFairTables.MessageKey[mode, bse + 0x4]);
                    buffer[0x8] = (byte)(s2[MTI(0x8) + buffer[0x8]] ^ PlayFairTables.MessageKey[mode, bse + 0x8]);
                    buffer[0xc] = (byte)(s2[MTI(0xc) + buffer[0xc]] ^ PlayFairTables.MessageKey[mode, bse + 0xc]);

                    byte tmp = buffer[0x0d];
                    buffer[0xd] = (byte)(s2[MTI(0xd) + buffer[0x9]] ^ PlayFairTables.MessageKey[mode, bse + 0xd]);
                    buffer[0x9] = (byte)(s2[MTI(0x9) + buffer[0x5]] ^ PlayFairTables.MessageKey[mode, bse + 0x9]);
                    buffer[0x5] = (byte)(s2[MTI(0x5) + buffer[0x1]] ^ PlayFairTables.MessageKey[mode, bse + 0x5]);
                    buffer[0x1] = (byte)(s2[MTI(0x1) + tmp] ^ PlayFairTables.MessageKey[mode, bse + 0x1]);

                    tmp = buffer[0x02];
                    buffer[0x2] = (byte)(s2[MTI(0x2) + buffer[0xa]] ^ PlayFairTables.MessageKey[mode, bse + 0x2]);
                    buffer[0xa] = (byte)(s2[MTI(0xa) + tmp] ^ PlayFairTables.MessageKey[mode, bse + 0xa]);
                    tmp = buffer[0x06];
                    buffer[0x6] = (byte)(s2[MTI(0x6) + buffer[0xe]] ^ PlayFairTables.MessageKey[mode, bse + 0x6]);
                    buffer[0xe] = (byte)(s2[MTI(0xe) + tmp] ^ PlayFairTables.MessageKey[mode, bse + 0xe]);

                    tmp = buffer[0x3];
                    buffer[0x3] = (byte)(s2[MTI(0x3) + buffer[0x7]] ^ PlayFairTables.MessageKey[mode, bse + 0x3]);
                    buffer[0x7] = (byte)(s2[MTI(0x7) + buffer[0xb]] ^ PlayFairTables.MessageKey[mode, bse + 0x7]);
                    buffer[0xb] = (byte)(s2[MTI(0xb) + buffer[0xf]] ^ PlayFairTables.MessageKey[mode, bse + 0xb]);
                    buffer[0xf] = (byte)(s2[MTI(0xf) + tmp] ^ PlayFairTables.MessageKey[mode, bse + 0xf]);

                    // T-table mixing
                    uint b0 = s9[0x000 + buffer[0x0]] ^ s9[0x100 + buffer[0x1]] ^ s9[0x200 + buffer[0x2]] ^ s9[0x300 + buffer[0x3]];
                    uint b1 = s9[0x000 + buffer[0x4]] ^ s9[0x100 + buffer[0x5]] ^ s9[0x200 + buffer[0x6]] ^ s9[0x300 + buffer[0x7]];
                    uint b2 = s9[0x000 + buffer[0x8]] ^ s9[0x100 + buffer[0x9]] ^ s9[0x200 + buffer[0xa]] ^ s9[0x300 + buffer[0xb]];
                    uint b3 = s9[0x000 + buffer[0xc]] ^ s9[0x100 + buffer[0xd]] ^ s9[0x200 + buffer[0xe]] ^ s9[0x300 + buffer[0xf]];

                    BitConverter.TryWriteBytes(buffer.AsSpan(0), b0);
                    BitConverter.TryWriteBytes(buffer.AsSpan(4), b1);
                    BitConverter.TryWriteBytes(buffer.AsSpan(8), b2);
                    BitConverter.TryWriteBytes(buffer.AsSpan(12), b3);
                }

                // Final S-box
                buffer[0x0] = s10[(0x0 << 8) + buffer[0x0]];
                buffer[0x4] = s10[(0x4 << 8) + buffer[0x4]];
                buffer[0x8] = s10[(0x8 << 8) + buffer[0x8]];
                buffer[0xc] = s10[(0xc << 8) + buffer[0xc]];

                byte t = buffer[0x0d];
                buffer[0xd] = s10[(0xd << 8) + buffer[0x9]];
                buffer[0x9] = s10[(0x9 << 8) + buffer[0x5]];
                buffer[0x5] = s10[(0x5 << 8) + buffer[0x1]];
                buffer[0x1] = s10[(0x1 << 8) + t];

                t = buffer[0x02];
                buffer[0x2] = s10[(0x2 << 8) + buffer[0xa]];
                buffer[0xa] = s10[(0xa << 8) + t];
                t = buffer[0x06];
                buffer[0x6] = s10[(0x6 << 8) + buffer[0xe]];
                buffer[0xe] = s10[(0xe << 8) + t];

                t = buffer[0x3];
                buffer[0x3] = s10[(0x3 << 8) + buffer[0x7]];
                buffer[0x7] = s10[(0x7 << 8) + buffer[0xb]];
                buffer[0xb] = s10[(0xb << 8) + buffer[0xf]];
                buffer[0xf] = s10[(0xf << 8) + t];

                // XOR with previous block or IV
                if (mode == 2 || mode == 1 || mode == 0)
                {
                    if (i > 0) XorBlocks(buffer, 0, messageIn, 0x10 * i, decryptedMessage, 0x10 * i);
                    else
                    {
                        byte[] iv = new byte[16];
                        for (int k = 0; k < 16; k++) iv[k] = PlayFairTables.MessageIv[mode, k];
                        XorBlocks(buffer, 0, iv, 0, decryptedMessage, 0x10 * i);
                    }
                }
                else
                {
                    if (i < 7) XorBlocks(buffer, 0, messageIn, 0x70 - 0x10 * i, decryptedMessage, 0x70 - 0x10 * i);
                    else
                    {
                        byte[] iv = new byte[16];
                        for (int k = 0; k < 16; k++) iv[k] = PlayFairTables.MessageIv[mode, k];
                        XorBlocks(buffer, 0, iv, 0, decryptedMessage, 0x70 - 0x10 * i);
                    }
                }
            }
        }

        // --- Rol helpers ---
        private static uint Rol32(uint input, int count) => (input << count) | (input >> (32 - count));
        private static byte Rol8(byte input, int count) => (byte)(((input << count) & 0xff) | (input >> (8 - count)));

        // --- Modified MD5 ---
        private static void ModifiedMD5(byte[] originalBlockIn, byte[] keyIn, byte[] keyOut)
        {
            byte[] blockIn = new byte[64];
            Buffer.BlockCopy(originalBlockIn, 0, blockIn, 0, 64);

            uint[] keyWords = new uint[4];
            for (int i = 0; i < 4; i++)
                keyWords[i] = BitConverter.ToUInt32(keyIn, i * 4);

            uint A = keyWords[0], B = keyWords[1], C = keyWords[2], D = keyWords[3];

            for (int i = 0; i < 64; i++)
            {
                int j;
                if (i < 16) j = i;
                else if (i < 32) j = (5 * i + 1) % 16;
                else if (i < 48) j = (3 * i + 5) % 16;
                else j = (7 * i) % 16;

                // Big-endian read
                uint input = (uint)blockIn[4 * j] << 24 | (uint)blockIn[4 * j + 1] << 16 |
                             (uint)blockIn[4 * j + 2] << 8 | (uint)blockIn[4 * j + 3];

                double sinVal = Math.Abs(Math.Sin(i + 1.0));
                uint constant = (uint)(sinVal * (1L << 32));

                uint Z = A + input + constant;
                int[] md5Shift = { 7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,
                                   5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
                                   4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,
                                   6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21 };

                if (i < 16) Z = Rol32(Z + ((B & C) | (~B & D)), md5Shift[i]);
                else if (i < 32) Z = Rol32(Z + ((B & D) | (C & ~D)), md5Shift[i]);
                else if (i < 48) Z = Rol32(Z + (B ^ C ^ D), md5Shift[i]);
                else Z = Rol32(Z + (C ^ (B | ~D)), md5Shift[i]);

                Z += B;
                uint oldA = A; A = D; D = C; C = B; B = Z;

                if (i == 31)
                {
                    void SwapWords(int a2, int b2)
                    {
                        uint va = BitConverter.ToUInt32(blockIn, (a2 & 15) * 4);
                        uint vb = BitConverter.ToUInt32(blockIn, (b2 & 15) * 4);
                        BitConverter.TryWriteBytes(blockIn.AsSpan((a2 & 15) * 4), vb);
                        BitConverter.TryWriteBytes(blockIn.AsSpan((b2 & 15) * 4), va);
                    }
                    SwapWords((int)(A & 15), (int)(B & 15));
                    SwapWords((int)(C & 15), (int)(D & 15));
                    SwapWords((int)((A & (15 << 4)) >> 4), (int)((B & (15 << 4)) >> 4));
                    SwapWords((int)((A & (15 << 8)) >> 8), (int)((B & (15 << 8)) >> 8));
                    SwapWords((int)((A & (15 << 12)) >> 12), (int)((B & (15 << 12)) >> 12));
                }
            }

            BitConverter.TryWriteBytes(keyOut.AsSpan(0), keyWords[0] + A);
            BitConverter.TryWriteBytes(keyOut.AsSpan(4), keyWords[1] + B);
            BitConverter.TryWriteBytes(keyOut.AsSpan(8), keyWords[2] + C);
            BitConverter.TryWriteBytes(keyOut.AsSpan(12), keyWords[3] + D);
        }

        private static void GenerateSessionKey(byte[] oldSap, byte[] messageIn, byte[] sessionKey)
        {
            byte[] decryptedMessage = new byte[128];
            byte[] newSap = new byte[320];

            DecryptMessage(messageIn, decryptedMessage);

            Buffer.BlockCopy(PlayFairConstants.staticSource1, 0, newSap, 0x000, 17);
            Buffer.BlockCopy(decryptedMessage, 0, newSap, 0x011, 0x80);
            Buffer.BlockCopy(oldSap, 0x80, newSap, 0x091, 0x80);
            Buffer.BlockCopy(PlayFairConstants.staticSource2, 0, newSap, 0x111, 47);
            Buffer.BlockCopy(PlayFairConstants.initialSessionKey, 0, sessionKey, 0, 16);

            byte[] md5Out = new byte[16];
            for (int round = 0; round < 5; round++)
            {
                byte[] bse = new byte[64];
                Buffer.BlockCopy(newSap, round * 64, bse, 0, 64);
                ModifiedMD5(bse, sessionKey, md5Out);
                SapHash(bse, sessionKey);

                for (int i = 0; i < 4; i++)
                {
                    uint skw = BitConverter.ToUInt32(sessionKey, i * 4);
                    uint mdw = BitConverter.ToUInt32(md5Out, i * 4);
                    BitConverter.TryWriteBytes(sessionKey.AsSpan(i * 4), skw + mdw);
                }
            }

            // Byte-swap each 4-byte word
            for (int i = 0; i < 16; i += 4)
            {
                (sessionKey[i], sessionKey[i + 3]) = (sessionKey[i + 3], sessionKey[i]);
                (sessionKey[i + 1], sessionKey[i + 2]) = (sessionKey[i + 2], sessionKey[i + 1]);
            }
            // XOR with 121
            for (int i = 0; i < 16; i++) sessionKey[i] ^= 121;
        }

        /// <summary>
        /// Main decrypt: derives a 16-byte AES key from m3 + ekey.
        /// Matches doubletake's playfairDecrypt(m3, ekey).
        /// </summary>
        public static byte[] Decrypt(byte[] m3, byte[] ekey)
        {
            return DecryptWithSap(m3, ekey, PlayFairConstants.defaultSap);
        }

        private static byte[] DecryptWithSap(byte[] m3, byte[] ekey, byte[] sap)
        {
            byte[] chunk1 = new byte[16];
            byte[] chunk2 = new byte[16];
            Buffer.BlockCopy(ekey, 16, chunk1, 0, 16);
            Buffer.BlockCopy(ekey, 56, chunk2, 0, 16);

            byte[] blockIn = new byte[16];
            byte[] sapKey = new byte[16];
            uint[,] keySchedule = new uint[11, 4];
            byte[] keyOut = new byte[16];

            GenerateSessionKey(sap, m3, sapKey);
            GenerateKeySchedule(sapKey, keySchedule);

            ZXor(chunk2, 0, blockIn, 0, 1);
            Cycle(blockIn, keySchedule);

            for (int i = 0; i < 16; i++)
                keyOut[i] = (byte)(blockIn[i] ^ chunk1[i]);
            XXor(keyOut, 0, keyOut, 0, 1);
            ZXor(keyOut, 0, keyOut, 0, 1);

            return keyOut;
        }
    }
}
