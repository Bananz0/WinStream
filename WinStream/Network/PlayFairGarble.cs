// Garble function - ported from doubletake playfair.go garble()
// Pure arithmetic bit manipulation.
using System;

namespace WinStream.Network
{
    internal static partial class PlayFairDecrypt
    {
        internal static void Garble(byte[] buffer0, byte[] buffer1, byte[] buffer2, byte[] buffer3, byte[] buffer4)
        {
            uint B0(int i) => (uint)buffer0[i];
            uint B1(int i) => (uint)buffer1[i];
            uint B2(int i) => (uint)buffer2[i];
            uint B4(int i) => (uint)buffer4[i];

            uint tmp = 0, tmp2 = 0, tmp3 = 0;
            uint A, B, C, D, E;

            buffer2[12] = (byte)(0x14 + (((B1(64) & 92) | ((B1(99) / 3) & 35)) & B4((int)(Rol8x(buffer4[B1(206) % 21], 4) % 21))));
            buffer1[4] = (byte)((B1(99) / 5) * (B1(99) / 5) * 2);
            buffer2[34] = 0xb8;
            buffer1[153] ^= (byte)(B2((int)(B1(203) % 35)) * B2((int)(B1(203) % 35)) * B1(190));
            buffer0[3] -= (byte)(((B4((int)(B1(205) % 21)) >> 1) & 80) | 0x40);
            buffer0[16] = 0x93;
            buffer0[13] = 0x62;
            buffer1[33] -= (byte)(B4((int)(B1(36) % 21)) & 0xf6);

            tmp2 = B2((int)(B1(67) % 35));
            buffer2[12] = 0x07;
            tmp = B0((int)(B1(181) % 20));
            buffer1[2] -= (byte)(3136 & 0xff);
            buffer0[19] = (byte)(B4((int)(B1(58) % 21)));

            buffer3[0] = (byte)(92 - B2((int)(B1(32) % 35)));
            buffer3[4] = (byte)(B2((int)(B1(15) % 35)) + 0x9e);
            buffer1[34] += (byte)(B4((int)((B2((int)(B1(15) % 35)) + 0x9e) & 0xff) % 21) / 5);
            buffer0[19] += (byte)(0xfffffee6 - ((B0((int)((uint)buffer3[4] % 20)) >> 1) & 102));

            uint shiftAmt = B4((int)(B1(190) % 21)) & 7;
            uint shifted = (B1(72) >> (int)shiftAmt) ^ (B1(72) << ((7 - (int)(B4((int)(B1(190) % 21)) - 1)) & 7));
            buffer1[15] = (byte)((3 * (shifted - (3 * B4((int)(B1(126) % 21))))) ^ B1(15));

            buffer0[15] ^= (byte)(B2((int)(B1(181) % 35)) * B2((int)(B1(181) % 35)) * B2((int)(B1(181) % 35)));
            buffer2[4] ^= (byte)(B1(202) / 3);

            A = 92 - B0((int)((uint)buffer3[0] % 20));
            E = (A & 0xc6) | (~B1(105) & 0xc6) | (A & (~B1(105)));
            buffer2[1] += (byte)(E * E * E);

            buffer0[19] ^= (byte)(((224 | (B4((int)(B1(92) % 21)) & 27)) * B2((int)(B1(41) % 35))) / 3);
            buffer1[140] += (byte)(WeirdRor8(92, (int)(B1(5) & 7)));

            buffer2[12] += (byte)(((((~B1(4)) ^ B2((int)(B1(12) % 35))) | B1(182)) & 192) | (((~B1(4)) ^ B2((int)(B1(12) % 35))) & B1(182)));
            buffer1[36] += 125;

            buffer1[124] = (byte)Rol8x((byte)((((74 & B1(138)) | ((74 | B1(138)) & B0(15))) & B0((int)(B1(43) % 20))) | (((74 & B1(138)) | ((74 | B1(138)) & B0(15)) | B0((int)(B1(43) % 20))) & 95)), 4);

            buffer3[8] = (byte)((((B0((int)((uint)buffer3[4] % 20)) & 95) & ((B4((int)(B1(68) % 21)) & 46) << 1)) | 16) ^ 92);

            A = B1(177) + B4((int)(B1(79) % 21));
            D = (((A >> 1) | ((3 * B1(148)) / 5)) & B2(1)) | ((A >> 1) & ((3 * B1(148)) / 5));
            buffer3[12] = (byte)(-34 - (int)D);

            A = 8 - (B2(22) & 7);
            B = B1(33) >> (int)(A & 7);
            C = B1(33) << (int)(B2(22) & 7);
            buffer2[16] += (byte)(((B2((int)((uint)buffer3[0] % 35)) & 159) | B0((int)((uint)buffer3[4] % 20)) | 8) - ((B ^ C) | 128));

            buffer0[14] ^= (byte)(B2((int)((uint)buffer3[12] % 35)));

            A = WeirdRol8(buffer4[B0((int)(B1(201) % 20)) % 21], (int)((B2((int)(B1(112) % 35)) << 1) & 7));
            D = (B0((int)(B1(208) % 20)) & 131) | (B0((int)(B1(164) % 20)) & 124);
            buffer1[19] += (byte)((A & (D / 5)) | ((A | (D / 5)) & 37));

            buffer2[8] = (byte)(WeirdRor8(140, (int)(((B4((int)(B1(45) % 21)) + 92) * (B4((int)(B1(45) % 21)) + 92)) & 7)));
            buffer1[190] = 56;
            buffer2[8] ^= buffer3[0];

            buffer1[53] = (byte)(~((B0((int)(B1(83) % 20)) | 204) / 5));
            buffer0[13] += (byte)(B0((int)(B1(41) % 20)));
            buffer0[10] = (byte)(((B2((int)((uint)buffer3[0] % 35)) & B1(2)) | ((B2((int)((uint)buffer3[0] % 35)) | B1(2)) & (uint)buffer3[12])) / 15);

            A = (((56 | (B4((int)(B1(2) % 21)) & 68)) | B2((int)((uint)buffer3[8] % 35))) & 42) | (((B4((int)(B1(2) % 21)) & 68) | 56) & B2((int)((uint)buffer3[8] % 35)));
            buffer3[16] = (byte)((A * A) + 110);
            buffer3[20] = (byte)(202 - (uint)buffer3[16]);
            buffer3[24] = buffer1[151];
            buffer2[13] ^= (byte)(B4((int)((uint)buffer3[0] % 21)));

            B = ((B2((int)(B1(179) % 35)) - 38) & 177) | ((uint)buffer3[12] & 177);
            C = (B2((int)(B1(179) % 35)) - 38) & (uint)buffer3[12];
            buffer3[28] = (byte)(30 + ((B | C) * (B | C)));
            buffer3[32] = (byte)((uint)buffer3[28] + 62);

            // Continue in GarblePart2
            GarblePart2(buffer0, buffer1, buffer2, buffer3, buffer4, tmp, tmp2, tmp3);
        }

        // Stub - will be filled in next step
        static partial void GarblePart2(byte[] buffer0, byte[] buffer1, byte[] buffer2, byte[] buffer3, byte[] buffer4, uint tmp, uint tmp2, uint tmp3);
    }
}
