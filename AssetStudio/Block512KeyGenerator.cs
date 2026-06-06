using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio
{
    public class Block512KeyGenerator
    {
        private byte[] hash2;
        private uint[] state = new uint[16];
        private uint[] keyBuffer = new uint[128];

        public Block512KeyGenerator(string token)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(token);
            using (var sha = SHA512.Create())
            {
                byte[] h1 = sha.ComputeHash(keyBytes);
                hash2 = sha.ComputeHash(h1);

                state[0] = 0x61707865;
                state[1] = 0x3320646e;
                state[2] = 0x79622d32;
                state[3] = 0x6b206574;

                for (int i = 0; i < 8; i++)
                {
                    state[4 + i] = BitConverter.ToUInt32(h1, i * 4);
                }
            }
        }

        private static uint RotateLeft(uint val, int r)
        {
            return (val << r) | (val >> (32 - r));
        }

        private static void QuarterRound(uint[] output, int a, int b, int c, int d)
        {
            output[a] += output[b];
            output[d] = RotateLeft(output[d] ^ output[a], 16);
            output[c] += output[d];
            output[b] = RotateLeft(output[b] ^ output[c], 12);
            output[a] += output[b];
            output[d] = RotateLeft(output[d] ^ output[a], 8);
            output[c] += output[d];
            output[b] = RotateLeft(output[b] ^ output[c], 7);
        }

        private void GenerateBlock(int rounds, int offset)
        {
            if (offset == 0)
            {
                for (int i = 0; i < 16; i++)
                    keyBuffer[offset + i] = state[i];
            }
            else
            {
                for (int i = 0; i < 16; i++)
                    keyBuffer[offset + i] = keyBuffer[offset + i - 16] ^ state[i];
            }

            uint[] mix = new uint[16];
            Array.Copy(keyBuffer, offset, mix, 0, 16);

            for (int i = 0; i < rounds; i += 2)
            {
                QuarterRound(mix, 0, 4, 8, 12);
                QuarterRound(mix, 1, 5, 9, 13);
                QuarterRound(mix, 2, 6, 10, 14);
                QuarterRound(mix, 3, 7, 11, 15);

                QuarterRound(mix, 0, 5, 10, 15);
                QuarterRound(mix, 1, 6, 11, 12);
                QuarterRound(mix, 2, 7, 8, 13);
                QuarterRound(mix, 3, 4, 9, 14);
            }

            if (offset == 0)
            {
                for (int i = 0; i < 16; i++)
                    keyBuffer[offset + i] = mix[i] + state[i];
            }
            else
            {
                for (int i = 0; i < 16; i++)
                    keyBuffer[offset + i] = mix[i] + (keyBuffer[offset + i - 16] ^ state[i]);
            }
        }

        public byte[] GetKey(uint block)
        {
            int h2_idx_1 = (int)((block % 13) | 0x30);
            uint hashPart1 = BitConverter.ToUInt32(hash2, h2_idx_1);

            int h2_idx_2 = (int)((block / 13) % 13);
            uint hashPart2 = BitConverter.ToUInt32(hash2, h2_idx_2);

            int h2_idx_3 = (int)(((block / 169) % 13) | 0x10);
            uint hashPart3 = BitConverter.ToUInt32(hash2, h2_idx_3);

            int h2_idx_4 = (int)(((block / 2197) % 13) | 0x20);
            uint hashPart4 = BitConverter.ToUInt32(hash2, h2_idx_4);

            int val1 = (int)(2 * (block / 169));
            long val2_mul = 0x24924925 * (long)(block / 338);
            int val2 = (int)(val2_mul >> 32);
            int shift = val1 - 28 * val2;

            uint rot1 = RotateLeft(hashPart1, shift);
            uint rot2 = RotateLeft(hashPart2, (int)((3 * (block / 2366)) % 27));

            uint hashPartRotated = rot1 ^ rot2;

            state[13] = hashPartRotated;
            state[14] = hashPartRotated ^ hashPart3;
            state[15] = hashPartRotated ^ hashPart3 ^ hashPart4;

            state[12] = block + 1;
            GenerateBlock(12, 0x00);

            state[12]++;
            GenerateBlock(8, 0x10);

            state[12]++;
            GenerateBlock(8, 0x20);

            state[12]++;
            GenerateBlock(8, 0x30);

            state[12]++;
            GenerateBlock(4, 0x40);

            state[12]++;
            GenerateBlock(4, 0x50);

            state[12]++;
            GenerateBlock(4, 0x60);

            state[12]++;
            GenerateBlock(4, 0x70);

            byte[] result = new byte[512];
            Buffer.BlockCopy(keyBuffer, 0, result, 0, 512);
            return result;
        }
    }
}
