# Converts doubletake's sapHash and garble functions from Go to C#
$goSrc = Get-Content "D:\WinStream\tmp\WinStreamAirPlay2Harness\tmp\doubletake\internal\airplay\playfair.go" -Raw

# Extract sapHash function body (lines 547-618) and garble (lines 622-886)
# We'll do a direct mechanical translation

$output = @"
// SapHash and Garble - ported from doubletake playfair.go
// These are pure arithmetic/bit-manipulation functions.
using System;

namespace WinStream.Network
{
    internal static partial class PlayFairDecrypt
    {
        private static uint Rol8x(byte input, int count) => (uint)((input << count)) | (uint)(input >> (8 - count));
        private static uint WeirdRor8(byte input, int count)
        {
            if (count == 0) return 0;
            return (uint)((input >> count) & 0xff) | (uint)(input & 0xff) << (8 - count);
        }
        private static uint WeirdRol8(byte input, int count)
        {
            if (count == 0) return 0;
            return (uint)((input << count) & 0xff) | (uint)(input & 0xff) >> (8 - count);
        }
        private static uint WeirdRol32(byte input, uint count)
        {
            if (count == 0) return 0;
            return (uint)input << (int)count ^ (uint)input >> (int)(8 - count);
        }

        internal static void SapHash(byte[] blockIn, byte[] keyOut)
        {
            uint[] blockWords = new uint[16];
            for (int i = 0; i < 16; i++)
                blockWords[i] = BitConverter.ToUInt32(blockIn, i * 4);

            byte[] buffer0 = { 0x96, 0x5F, 0xC6, 0x53, 0xF8, 0x46, 0xCC, 0x18, 0xDF, 0xBE, 0xB2, 0xF8, 0x38, 0xD7, 0xEC, 0x22, 0x03, 0xD1, 0x20, 0x8F };
            byte[] buffer1 = new byte[210];
            byte[] buffer2 = { 0x43, 0x54, 0x62, 0x7A, 0x18, 0xC3, 0xD6, 0xB3, 0x9A, 0x56, 0xF6, 0x1C, 0x14, 0x3F, 0x0C, 0x1D, 0x3B, 0x36, 0x83, 0xB1, 0x39, 0x51, 0x4A, 0xAA, 0x09, 0x3E, 0xFE, 0x44, 0xAF, 0xDE, 0xC3, 0x20, 0x9D, 0x42, 0x3A };
            byte[] buffer3 = new byte[132];
            byte[] buffer4 = { 0xED, 0x25, 0xD1, 0xBB, 0xBC, 0x27, 0x9F, 0x02, 0xA2, 0xA9, 0x11, 0x00, 0x0C, 0xB3, 0x52, 0xC0, 0xBD, 0xE3, 0x1B, 0x49, 0xC7 };
            int[] i0Index = { 18, 22, 23, 0, 5, 19, 32, 31, 10, 21, 30 };

            for (int i = 0; i < 210; i++)
            {
                uint inWord = blockWords[(i % 64) >> 2];
                byte inByte = (byte)((inWord >> ((3 - (i % 4)) << 3)) & 0xff);
                buffer1[i] = inByte;
            }

            for (int i = 0; i < 840; i++)
            {
                byte x = buffer1[(uint)(i - 155) % 210];
                byte y = buffer1[(uint)(i - 57) % 210];
                byte z = buffer1[(uint)(i - 13) % 210];
                byte w = buffer1[(uint)i % 210];
                buffer1[i % 210] = (byte)((Rol8(y, 5) + (Rol8(z, 3) ^ w) - Rol8(x, 7)) & 0xff);
            }

            Garble(buffer0, buffer1, buffer2, buffer3, buffer4);

            for (int i = 0; i < 16; i++) keyOut[i] = 0xE1;
            for (int i = 0; i < 11; i++)
            {
                if (i == 3) keyOut[i] = 0x3d;
                else keyOut[i] = (byte)((keyOut[i] + buffer3[i0Index[i] * 4]) & 0xff);
            }
            for (int i = 0; i < 20; i++) keyOut[i % 16] ^= buffer0[i];
            for (int i = 0; i < 35; i++) keyOut[i % 16] ^= buffer2[i];
            for (int i = 0; i < 210; i++) keyOut[i % 16] ^= buffer1[i];

            for (int j = 0; j < 16; j++)
            {
                for (int i = 0; i < 16; i++)
                {
                    byte xv = keyOut[(uint)(i - 7) % 16];
                    byte yv = keyOut[i % 16];
                    byte zv = keyOut[(uint)(i - 37) % 16];
                    byte wv = keyOut[(uint)(i - 177) % 16];
                    keyOut[i] = (byte)(Rol8(xv, 1) ^ yv ^ Rol8(zv, 6) ^ Rol8(wv, 5));
                }
            }
        }
    }
}
"@

$output | Set-Content "D:\WinStream\WinStream\Network\PlayFairSapHash.cs" -Encoding UTF8
Write-Host "Generated PlayFairSapHash.cs"
