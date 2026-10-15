// Garble part 2 - continuation of the garble function from doubletake playfair.go
using System;

namespace WinStream.Network
{
    internal static partial class PlayFairDecrypt
    {
        static partial void GarblePart2(byte[] buffer0, byte[] buffer1, byte[] buffer2, byte[] buffer3, byte[] buffer4, uint tmp, uint tmp2, uint tmp3)
        {
            uint B0(int i) => (uint)buffer0[i];
            uint B1(int i) => (uint)buffer1[i];
            uint B2(int i) => (uint)buffer2[i];
            uint B4(int i) => (uint)buffer4[i];
            uint A, B, C, D, E, F, G, H, K, M, J, R, S, T, U, V, W, X, Y, Z;
            M=J=G=F=H=K=R=S=T=U=V=W=X=Y=Z=0; A=B=C=D=E=0;

            // eek
            A = (((uint)buffer3[20] + ((uint)buffer3[0] & 74)) | ~B4((int)((uint)buffer3[0] % 21))) & 121;
            B = ((uint)buffer3[20] + ((uint)buffer3[0] & 74)) & ~B4((int)((uint)buffer3[0] % 21));
            tmp3 = A | B;
            C = ((((A | B) ^ 0xffffffa6) | (uint)buffer3[0]) & 4) | (((A | B) ^ 0xffffffa6) & (uint)buffer3[0]);
            buffer1[47] = (byte)((B2((int)(B1(89) % 35)) + C) ^ B1(47));

            buffer3[36] = (byte)((int)((uint)(Rol8((byte)((tmp & 179) + 68), 2)) & B0(3)) | (int)(tmp2 & ~B0(3)) - 15);
            buffer1[123] ^= 221;

            A = (B4((int)((uint)buffer3[0] % 21)) / 3) - B2((int)((uint)buffer3[4] % 35));
            C = ((((uint)buffer3[0] & 163) + 92) & 246) | ((uint)buffer3[0] & 92);
            E = ((C | (uint)buffer3[24]) & 54) | (C & (uint)buffer3[24]);
            buffer3[40] = (byte)(A - E);

            buffer3[44] = (byte)(tmp3 ^ 81 ^ ((((uint)buffer3[0] >> 1) & 101) + 26));
            buffer3[48] = (byte)(B2((int)((uint)buffer3[4] % 35)) & 27);
            buffer3[52] = 27;
            buffer3[56] = 199;

            // caffeine
            { // caffeine block
                uint ab40_24 = ((uint)buffer3[40] | (uint)buffer3[24]) & 177;
                uint ab40x24 = (uint)buffer3[40] & (uint)buffer3[24];
                uint part1 = (ab40_24 | ab40x24) & (((B4((int)((uint)buffer3[0] % 20)) & 177) | 176) | (B4((int)((uint)buffer3[0] % 21)) & ~(uint)3));
                uint part2a = (ab40x24 | (((uint)buffer3[40] | (uint)buffer3[24]) & 177)) & 199;
                uint part2b = (((B4((int)((uint)buffer3[0] % 21)) & 1) + 176) | (B4((int)((uint)buffer3[0] % 21)) & ~(uint)3)) & (uint)buffer3[56];
                uint combined = ((part1 | (part2a | part2b)) & (~(uint)buffer3[52])) | (uint)buffer3[48];
                buffer3[64] = (byte)((uint)buffer3[4] + combined);
            }

            buffer2[33] ^= buffer1[26];
            buffer1[106] ^= (byte)((uint)buffer3[20] ^ 133);

            buffer2[30] = (byte)((((uint)buffer3[64] / 3) - (275 | ((uint)buffer3[0] & 247))) ^ B0((int)(B1(122) % 20)));
            buffer1[22] = (byte)((B2((int)(B1(90) % 35)) & 95) | 68);

            A = (B4((int)((uint)buffer3[36] % 21)) & 184) | (B2((int)((uint)buffer3[44] % 35)) & ~(uint)184);
            buffer2[18] += (byte)((A * A * A) >> 1);

            buffer2[5] -= (byte)(B4((int)(B1(92) % 21)));

            A = (((B1(41) & ~(uint)24) | (B2((int)(B1(183) % 35)) & 24)) & ((uint)buffer3[16] + 53)) | ((uint)buffer3[20] & B2((int)((uint)buffer3[20] % 35)));
            B = (B1(17) & (~(uint)buffer3[44])) | (B0((int)(B1(59) % 20)) & (uint)buffer3[44]);
            buffer2[18] ^= (byte)(A * B);

            A = WeirdRor8(buffer1[11], (int)(B2((int)(B1(28) % 35)) & 7)) & 7;
            B = (((B0((int)(B1(93) % 20)) & ~B0(14)) | (B0(14) & 150)) & ~(uint)28) | (B1(7) & 28);
            buffer2[22] = (byte)((((( B | WeirdRol8(buffer2[(uint)buffer3[0] % 35], (int)A)) & B2(33)) | (B & WeirdRol8(buffer2[(uint)buffer3[0] % 35], (int)A))) + 74) & 0xff);

            A = B4((int)((B0((int)(B1(39) % 20)) ^ 217) % 21));
            buffer0[15] -= (byte)((((((uint)buffer3[20] | (uint)buffer3[0]) & 214) | ((uint)buffer3[20] & (uint)buffer3[0])) & A) | ((((((uint)buffer3[20] | (uint)buffer3[0]) & 214) | ((uint)buffer3[20] & (uint)buffer3[0])) | A) & (uint)buffer3[32]));

            B = (((B2((int)(B1(57) % 35)) & B0((int)((uint)buffer3[64] % 20))) | ((B0((int)((uint)buffer3[64] % 20)) | B2((int)(B1(57) % 35))) & 95) | ((uint)buffer3[64] & 45) | 82) & 32);
            C = ((B2((int)(B1(57) % 35)) & B0((int)((uint)buffer3[64] % 20))) | ((B2((int)(B1(57) % 35)) | B0((int)((uint)buffer3[64] % 20))) & 95)) & (((uint)buffer3[64] & 45) | 82);
            D = ((((uint)buffer3[0] / 3) - ((uint)buffer3[64] | B1(22))) ^ ((uint)buffer3[28] + 62) ^ (B | C));
            T = B0((int)((D & 0xff) % 20));

            buffer3[68] = (byte)((uint)((B0((int)(B1(99) % 20)) * B0((int)(B1(99) % 20)) * B0((int)(B1(99) % 20)) * B0((int)(B1(99) % 20))) | B2((int)((uint)buffer3[64] % 35))));

            U = B0((int)(B1(50) % 20));
            W = B2((int)(B1(138) % 35));
            X = B4((int)(B1(39) % 21));
            Y = B0((int)(B1(4) % 20));
            Z = B4((int)(B1(202) % 21));
            V = B0((int)(B1(151) % 20));
            S = B2((int)(B1(14) % 35));
            R = B0((int)(B1(145) % 20));

            A = (B2((int)((uint)buffer3[68] % 35)) & B0((int)(B1(209) % 20))) | ((B2((int)((uint)buffer3[68] % 35)) | B0((int)(B1(209) % 20))) & 24);
            B = WeirdRol8(buffer4[B1(127) % 21], (int)(B2((int)((uint)buffer3[68] % 35)) & 7));
            C = (A & B0(10)) | (B & ~B0(10));
            D = 7 ^ (B4((int)(B2((int)((uint)buffer3[36] % 35)) % 21)) << 1);
            buffer3[72] = (byte)((C & 71) | (D & ~(uint)71));

            buffer2[2] += (byte)(((((B0((int)((uint)buffer3[20] % 20)) << 1) & 159) | (B4((int)(B1(190) % 21)) & ~(uint)159)) & ((((B4((int)((uint)buffer3[64] % 21)) & 110) | (B0((int)(B1(25) % 20)) & ~(uint)110)) & ~(uint)150) | (B1(25) & 150))));
            buffer2[14] -= (byte)(((B2((int)((uint)buffer3[20] % 35)) & ((uint)buffer3[72] ^ B2((int)(B1(100) % 35)))) & ~(uint)34) | (B1(97) & 34));
            buffer0[17] = 115;

            buffer1[23] ^= (byte)(((((((B4((int)(B1(17) % 21)) | B0((int)((uint)buffer3[20] % 20))) & (uint)buffer3[72]) | (B4((int)(B1(17) % 21)) & B0((int)((uint)buffer3[20] % 20)))) & (B1(50) / 3)) |
                ((((B4((int)(B1(17) % 21)) | B0((int)((uint)buffer3[20] % 20))) & (uint)buffer3[72]) | (B4((int)(B1(17) % 21)) & B0((int)((uint)buffer3[20] % 20))) | (B1(50) / 3)) & 246)) << 1));

            buffer0[13] = (byte)(((((((B0((int)((uint)buffer3[40] % 20)) | B1(10)) & 82) | (B0((int)((uint)buffer3[40] % 20)) & B1(10))) & 209) |
                ((B0((int)(B1(39) % 20)) << 1) & 46)) >> 1));

            buffer2[33] -= (byte)(B1(113) & 9);
            buffer2[28] -= (byte)(((((2 | (B1(110) & 222)) >> 1) & ~(uint)223) | ((uint)buffer3[20] & 223)));

            J = WeirdRol8((byte)(V | Z), (int)(U & 7));
            A = (B2(16) & T) | (W & (~B2(16)));
            B = (B1(33) & 17) | (X & ~(uint)17);
            E = ((Y | ((A + B) / 5)) & 147) | (Y & ((A + B) / 5));
            M = ((uint)buffer3[40] & B4((int)(((uint)buffer3[8] + J + E) & 0xff) % 21)) |
                (((uint)buffer3[40] | B4((int)(((uint)buffer3[8] + J + E) & 0xff) % 21)) & B2(23));

            buffer0[15] = (byte)((((B4((int)((uint)buffer3[20] % 21)) - 48) & (~B1(184))) | ((B4((int)((uint)buffer3[20] % 21)) - 48) & 189) | (189 & ~B1(184))) & (M * M * M));

            buffer2[22] += buffer1[183];
            buffer3[76] = (byte)((3 * B4((int)(B1(1) % 21))) ^ (uint)buffer3[0]);

            A = B2((int)(((uint)buffer3[8] + (J + E)) & 0xff % 35));
            F = (((B4((int)(B1(178) % 21)) & A) | ((B4((int)(B1(178) % 21)) | A) & 209)) * B0((int)(B1(13) % 20))) * (B4((int)(B1(26) % 21)) >> 1);
            G = (F + 0x733ffff9) * 198 - (((F + 0x733ffff9) * 396 + 212) & 212) + 85;
            buffer3[80] = (byte)((uint)buffer3[36] + (G ^ 148) + ((G ^ 107) << 1) - 127);

            buffer3[84] = (byte)((B2((int)((uint)buffer3[64] % 35))) & 245 | (B2((int)((uint)buffer3[20] % 35)) & 10));

            A = B0((int)((uint)buffer3[68] % 20)) | 81;
            buffer2[18] -= (byte)(((A * A * A) & ~(uint)buffer0[15]) | (((uint)buffer3[80] / 15) & (uint)buffer0[15]));

            buffer3[88] = (byte)((uint)buffer3[8] + J + E - B0((int)(B1(160) % 20)) + (B4((int)(B0((int)(((uint)buffer3[8] + J + E) & 255) % 20)) % 21) / 3));

            B = ((R ^ (uint)buffer3[72]) & ~(uint)198) | ((S * S) & 198);
            F = (B4((int)(B1(69) % 21)) & B1(172)) | ((B4((int)(B1(69) % 21)) | B1(172)) & (((uint)buffer3[12] - B) + 77));
            buffer0[16] = (byte)(147 - (((uint)buffer3[72] & ((F & 251) | 1)) | (((F & 250) | (uint)buffer3[72]) & 198)));

            C = (B4((int)(B1(168) % 21)) & B0((int)(B1(29) % 20)) & 7) | ((B4((int)(B1(168) % 21)) | B0((int)(B1(29) % 20))) & 6);
            F = (B4((int)(B1(155) % 21)) & B1(105)) | ((B4((int)(B1(155) % 21)) | B1(105)) & 141);
            buffer0[3] -= (byte)(B4((int)(WeirdRol32((byte)F, C) % 21)));

            buffer1[5] = (byte)(WeirdRor8(buffer0[12], (int)((B0((int)(B1(61) % 20)) / 5) & 7)) ^ ((~B2((int)((uint)buffer3[84] % 35)) & 0xffffffff) / 5));

            buffer1[198] += buffer1[3];

            A = 162 | B2((int)((uint)buffer3[64] % 35));
            buffer1[164] += (byte)((A * A) / 5);

            G = WeirdRor8(139, (int)((uint)buffer3[80] & 7));
            C = ((B4((int)((uint)buffer3[64] % 21)) * B4((int)((uint)buffer3[64] % 21)) * B4((int)((uint)buffer3[64] % 21))) & 95) | (B0((int)((uint)buffer3[40] % 20)) & ~(uint)95);
            buffer3[92] = (byte)((G & 12) | (B0((int)((uint)buffer3[20] % 20)) & 12) | (G & B0((int)((uint)buffer3[20] % 20))) | C);

            buffer2[12] += (byte)(((B1(103) & 32) | ((uint)buffer3[92] & (B1(103) | 60)) | 16) / 3);
            buffer3[96] = buffer1[143];
            buffer3[100] = 27;

            buffer3[104] = (byte)((((((uint)buffer3[40] & ~(uint)buffer2[8]) | (B1(35) & (uint)buffer2[8])) & (uint)buffer3[64]) ^ 119));
            buffer3[108] = (byte)(238 & (((((uint)buffer3[40] & ~(uint)buffer2[8]) | (B1(35) & (uint)buffer2[8])) & (uint)buffer3[64]) << 1));
            buffer3[112] = (byte)((~(uint)buffer3[64] & ((uint)buffer3[84] / 3)) ^ 49);
            buffer3[116] = (byte)(98 & ((~(uint)buffer3[64] & ((uint)buffer3[84] / 3)) << 1));

            // finale
            A = (B1(35) & (uint)buffer2[8]) | ((uint)buffer3[40] & ~(uint)buffer2[8]);
            B = (A & (uint)buffer3[64]) | (((uint)buffer3[84] / 3) & ~(uint)buffer3[64]);
            {
                uint bk = 86 + ((B1(172) & 64) >> 1);
                uint notpart = ((B1(172) & 65) >> 1) ^ 86;
                uint orpart = (~(uint)buffer3[64] & ((uint)buffer3[84] / 3)) | (A & (uint)buffer3[64]);
                buffer1[143] = (byte)((uint)buffer3[96] - ((B & bk) | ((notpart | orpart) & (uint)buffer3[100])));
            }

            buffer2[29] = 162;

            A = (((B4((int)((uint)buffer3[88] % 21)) & 160) | (B0((int)(B1(125) % 20)) & 95)) >> 1);
            B = B2((int)(B1(149) % 35)) ^ (B1(43) * B1(43));
            buffer0[15] += (byte)((B & A) | ((A | B) & 115));

            buffer3[120] = (byte)((uint)buffer3[64] - B0((int)((uint)buffer3[40] % 20)));
            buffer1[95] = (byte)(B4((int)((uint)buffer3[20] % 21)));

            A = WeirdRor8(buffer2[(uint)buffer3[80] % 35], (int)((B2((int)(B1(17) % 35)) * B2((int)(B1(17) % 35)) * B2((int)(B1(17) % 35))) & 7));
            buffer0[7] -= (byte)(A * A);

            buffer2[8] = (byte)((uint)buffer2[8] - B1(184) + (B4((int)(B1(202) % 21)) * B4((int)(B1(202) % 21)) * B4((int)(B1(202) % 21))));
            buffer0[16] = (byte)((B2((int)(B1(102) % 35)) << 1) & 132);

            buffer3[124] = (byte)((B4((int)((uint)buffer3[40] % 21)) >> 1) ^ (uint)buffer3[68]);

            buffer0[7] -= (byte)(B0((int)(B1(191) % 20)) - (((B4((int)(B1(80) % 21)) << 1) & ~(uint)177) | (B4((int)(B4((int)((uint)buffer3[88] % 21)) % 21)) & 177)));
            buffer0[6] = (byte)(B0((int)(B1(119) % 20)));

            A = (B4((int)(B1(190) % 21)) & ~(uint)209) | (B1(118) & 209);
            B = B0((int)((uint)buffer3[120] % 20)) * B0((int)((uint)buffer3[120] % 20));
            buffer0[12] = (byte)((B0((int)((uint)buffer3[84] % 20)) ^ (B2((int)(B1(71) % 35)) + B2((int)(B1(15) % 35)))) & ((A & B) | ((A | B) & 27)));

            B = (B1(32) & B2((int)((uint)buffer3[88] % 35))) | ((B1(32) | B2((int)((uint)buffer3[88] % 35))) & 23);
            D = (((B4((int)(B1(57) % 21)) * 231) & 169) | (B & 86));
            F = (((B0((int)(B1(82) % 20)) & ~(uint)29) | (B4((int)((uint)buffer3[124] % 21)) & 29)) & 190) | (B4((int)(D / 5) % 21) & ~(uint)190);
            H = B0((int)((uint)buffer3[40] % 20)) * B0((int)((uint)buffer3[40] % 20)) * B0((int)((uint)buffer3[40] % 20));
            K = (H & B1(82)) | (H & 92) | (B1(82) & 92);
            buffer3[128] = (byte)(((F & K) | ((F | K) & 192)) ^ (D / 5));

            buffer2[25] ^= (byte)(((B0((int)((uint)buffer3[120] % 20)) << 1) * B1(5)) - (WeirdRol8((byte)(uint)buffer3[76], (int)(B4((int)((uint)buffer3[124] % 21)) & 7)) & ((uint)buffer3[20] + 110)));
        }
    }
}
