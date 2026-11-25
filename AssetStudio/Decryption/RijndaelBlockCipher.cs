#nullable enable
using System;
using System.Buffers.Binary;

namespace AssetStudio
{
    public sealed class RijndaelBlockCipher
    {
        public const int MaxStateWords = 8;
        public const int MaxRounds = 14;
        public const int MaxKeyScheduleWords = MaxStateWords * (MaxRounds + 1);

        private static readonly byte[] SBox = Convert.FromHexString(
            "637C777BF26B6FC53001672BFED7AB76CA82C97DFA5947F0ADD4A2AF9CA472C0" +
            "B7FD9326363FF7CC34A5E5F171D8311504C723C31896059A071280E2EB27B275" +
            "09832C1A1B6E5AA0523BD6B329E32F8453D100ED20FCB15B6ACBBE394A4C58CF" +
            "D0EFAAFB434D338545F9027F503C9FA851A3408F929D38F5BCB6DA2110FFF3D2" +
            "CD0C13EC5F974417C4A77E3D645D197360814FDC222A908846EEB814DE5E0BDB" +
            "E0323A0A4906245CC2D3AC629195E479E7C8376D8DD54EA96C56F4EA657AAE08" +
            "BA78252E1CA6B4C6E8DD741F4BBD8B8A703EB5664803F60E613557B986C11D9E" +
            "E1F8981169D98E949B1E87E9CE5528DF8CA1890DBFE6426841992D0FB054BB16");

        private static readonly byte[] InvSBox = BuildInverseSBox();
        private static readonly uint[] Te0 = BuildTable(SBox, 2, 1, 1, 3);
        private static readonly uint[] Te1 = RotateTable(Te0, 8);
        private static readonly uint[] Te2 = RotateTable(Te0, 16);
        private static readonly uint[] Te3 = RotateTable(Te0, 24);
        private static readonly uint[] Td0 = BuildTable(InvSBox, 14, 9, 13, 11);
        private static readonly uint[] Td1 = RotateTable(Td0, 8);
        private static readonly uint[] Td2 = RotateTable(Td0, 16);
        private static readonly uint[] Td3 = RotateTable(Td0, 24);
        private static readonly uint[] Imc0 = BuildInvMixColumn(14, 9, 13, 11);
        private static readonly uint[] Imc1 = BuildInvMixColumn(11, 14, 9, 13);
        private static readonly uint[] Imc2 = BuildInvMixColumn(13, 11, 14, 9);
        private static readonly uint[] Imc3 = BuildInvMixColumn(9, 13, 11, 14);
        private static readonly byte[] Mod4 = { 0, 1, 2, 3, 0, 1, 2, 3 };
        private static readonly byte[] Mod6 = { 0, 1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5 };
        private static readonly byte[] Mod8 = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7 };

        private readonly uint[] encryptKeys = new uint[MaxKeyScheduleWords];
        private readonly uint[] decryptKeys = new uint[MaxKeyScheduleWords];
        private readonly int nb;
        private readonly int nr;

        public int BlockSize => nb * 4;
        public int KeySize { get; }
        public int Rounds => nr;

        public RijndaelBlockCipher(ReadOnlySpan<byte> key, int blockSizeBits)
        {
            if (key.Length is not (16 or 24 or 32))
            {
                throw new ArgumentException("Rijndael key must be 16, 24, or 32 bytes.", nameof(key));
            }

            if (blockSizeBits is not (128 or 192 or 256))
            {
                throw new ArgumentException("Rijndael block size must be 128, 192, or 256 bits.", nameof(blockSizeBits));
            }

            nb = blockSizeBits / 32;
            KeySize = key.Length;
            nr = ExpandKey(key, key.Length / 4, nb, encryptKeys, decryptKeys);
        }

        public static int ExpandDecryptKey(ReadOnlySpan<byte> key, int blockSizeBits, Span<uint> destination)
        {
            if (key.Length is not (16 or 24 or 32) || blockSizeBits is not (128 or 192 or 256) || destination.Length < MaxKeyScheduleWords)
            {
                return -1;
            }

            return ExpandKey(key, key.Length / 4, blockSizeBits / 32, Span<uint>.Empty, destination);
        }

        public static void DecryptBlock(
            ReadOnlySpan<uint> decryptKeys,
            int blockSizeBits,
            int rounds,
            ReadOnlySpan<byte> source,
            Span<byte> destination)
        {
            int size = blockSizeBits / 32 * 4;
            if (source.Length < size || destination.Length < size)
            {
                throw new ArgumentException(source.Length < size ? nameof(source) : nameof(destination));
            }

            Span<uint> s = stackalloc uint[MaxStateWords];
            Span<uint> t = stackalloc uint[MaxStateWords];
            LoadState(source, size / 4, s);
            ReadOnlySpan<uint> state = DecryptCore(decryptKeys, size / 4, rounds, s, t);
            StoreState(state.Slice(0, size / 4), destination);
        }

        public void EncryptBlock(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            ValidateBlockArgs(source, destination);
            Span<uint> s = stackalloc uint[MaxStateWords];
            Span<uint> t = stackalloc uint[MaxStateWords];
            LoadState(source, nb, s);

            for (int c = 0; c < nb; c++)
            {
                s[c] ^= encryptKeys[c];
            }

            GetEncryptShifts(nb, out int shift1, out int shift2, out int shift3);
            for (int round = 1; round < nr; round++)
            {
                Mix(s, t, nb, Te0, Te1, Te2, Te3, shift1, shift2, shift3, encryptKeys, round * nb);
                Swap(ref s, ref t);
            }

            Substitute(s, t, nb, SBox, shift1, shift2, shift3, encryptKeys, nr * nb);
            StoreState(t.Slice(0, nb), destination);
        }

        public void DecryptBlock(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            ValidateBlockArgs(source, destination);
            Span<uint> s = stackalloc uint[MaxStateWords];
            Span<uint> t = stackalloc uint[MaxStateWords];
            LoadState(source, nb, s);
            ReadOnlySpan<uint> state = DecryptCore(decryptKeys, nb, nr, s, t);
            StoreState(state.Slice(0, nb), destination);
        }

        private static ReadOnlySpan<uint> DecryptCore(ReadOnlySpan<uint> keys, int nb, int rounds, Span<uint> s, Span<uint> t)
        {
            for (int c = 0; c < nb; c++)
            {
                s[c] ^= keys[rounds * nb + c];
            }

            int shift1 = nb - 1;
            int shift2 = nb - (nb == MaxStateWords ? 3 : 2);
            int shift3 = nb - (nb == MaxStateWords ? 4 : 3);
            for (int round = rounds - 1; round >= 1; round--)
            {
                Mix(s, t, nb, Td0, Td1, Td2, Td3, shift1, shift2, shift3, keys, round * nb);
                Swap(ref s, ref t);
            }

            Substitute(s, t, nb, InvSBox, shift1, shift2, shift3, keys, 0);
            return t;
        }

        private static void GetEncryptShifts(int nb, out int shift1, out int shift2, out int shift3)
        {
            shift1 = 1;
            shift2 = nb == MaxStateWords ? 3 : 2;
            shift3 = nb == MaxStateWords ? 4 : 3;
        }

        private static void Mix(
            ReadOnlySpan<uint> s,
            Span<uint> t,
            int nb,
            uint[] t0,
            uint[] t1,
            uint[] t2,
            uint[] t3,
            int shift1,
            int shift2,
            int shift3,
            ReadOnlySpan<uint> keys,
            int keyBase)
        {
            byte[] mod = nb == 4 ? Mod4 : nb == 6 ? Mod6 : Mod8;
            for (int c = 0; c < nb; c++)
            {
                t[c] = (t0[s[c] & 0xFF]
                        ^ t1[(s[mod[c + shift1]] >> 8) & 0xFF]
                        ^ t2[(s[mod[c + shift2]] >> 16) & 0xFF]
                        ^ t3[s[mod[c + shift3]] >> 24])
                    ^ keys[keyBase + c];
            }
        }

        private static void Substitute(
            ReadOnlySpan<uint> s,
            Span<uint> t,
            int nb,
            byte[] box,
            int shift1,
            int shift2,
            int shift3,
            ReadOnlySpan<uint> keys,
            int keyBase)
        {
            byte[] mod = nb == 4 ? Mod4 : nb == 6 ? Mod6 : Mod8;
            for (int c = 0; c < nb; c++)
            {
                t[c] = (box[s[c] & 0xFF]
                        | (uint)box[(s[mod[c + shift1]] >> 8) & 0xFF] << 8
                        | (uint)box[(s[mod[c + shift2]] >> 16) & 0xFF] << 16
                        | (uint)box[s[mod[c + shift3]] >> 24] << 24)
                    ^ keys[keyBase + c];
            }
        }

        private void ValidateBlockArgs(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (source.Length < BlockSize)
            {
                throw new ArgumentException($"Source must contain at least {BlockSize} bytes.", nameof(source));
            }

            if (destination.Length < BlockSize)
            {
                throw new ArgumentException($"Destination must contain at least {BlockSize} bytes.", nameof(destination));
            }
        }

        private static void LoadState(ReadOnlySpan<byte> source, int words, Span<uint> state)
        {
            for (int c = 0; c < words; c++)
            {
                state[c] = source[c * 4]
                    | (uint)source[c * 4 + 1] << 8
                    | (uint)source[c * 4 + 2] << 16
                    | (uint)source[c * 4 + 3] << 24;
            }
        }

        private static void StoreState(ReadOnlySpan<uint> state, Span<byte> destination)
        {
            for (int c = 0; c < state.Length; c++)
            {
                destination[c * 4] = (byte)state[c];
                destination[c * 4 + 1] = (byte)(state[c] >> 8);
                destination[c * 4 + 2] = (byte)(state[c] >> 16);
                destination[c * 4 + 3] = (byte)(state[c] >> 24);
            }
        }

        private static void Swap(ref Span<uint> a, ref Span<uint> b)
        {
            Span<uint> tmp = a;
            a = b;
            b = tmp;
        }

        private static int ExpandKey(ReadOnlySpan<byte> key, int nk, int nb, Span<uint> encryptSchedule, Span<uint> decryptSchedule)
        {
            int rounds = Math.Max(nk, nb) + 6;
            int total = nb * (rounds + 1);
            Span<uint> w = stackalloc uint[MaxKeyScheduleWords];
            for (int i = 0; i < nk; i++)
            {
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(key.Slice(i * 4, 4));
            }

            byte rcon = 1;
            int position = 0;
            for (int i = nk; i < total; i++)
            {
                uint temp = w[i - 1];
                if (position == 0)
                {
                    temp = SubWord((temp << 8) | (temp >> 24)) ^ (uint)rcon << 24;
                    rcon = XTime(rcon);
                }
                else if (nk > 6 && position == 4)
                {
                    temp = SubWord(temp);
                }

                w[i] = w[i - nk] ^ temp;
                position = position + 1 == nk ? 0 : position + 1;
            }

            for (int i = 0; i < total; i++)
            {
                uint word = BinaryPrimitives.ReverseEndianness(w[i]);
                if (encryptSchedule.Length > 0)
                {
                    encryptSchedule[i] = word;
                }

                if (decryptSchedule.Length > 0)
                {
                    decryptSchedule[i] = i >= nb && i < rounds * nb ? InvMixColumn(word) : word;
                }
            }

            return rounds;
        }

        private static uint SubWord(uint value)
        {
            return (uint)SBox[value >> 24] << 24
                | (uint)SBox[(value >> 16) & 0xFF] << 16
                | (uint)SBox[(value >> 8) & 0xFF] << 8
                | SBox[value & 0xFF];
        }

        private static uint InvMixColumn(uint value)
            => Imc0[value & 0xFF] ^ Imc1[(value >> 8) & 0xFF] ^ Imc2[(value >> 16) & 0xFF] ^ Imc3[value >> 24];

        private static byte XTime(byte value)
        {
            int doubled = value << 1;
            if ((doubled & 0x100) != 0)
            {
                doubled ^= 0x11B;
            }

            return (byte)doubled;
        }

        private static byte Multiply(byte value, int factor)
        {
            int result = 0;
            int v = value;
            for (int f = factor; f != 0; f >>= 1)
            {
                if ((f & 1) != 0)
                {
                    result ^= v;
                }

                v = XTime((byte)v);
            }

            return (byte)result;
        }

        private static byte[] BuildInverseSBox()
        {
            var inverse = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                inverse[SBox[i]] = (byte)i;
            }

            return inverse;
        }

        private static uint[] BuildInvMixColumn(int row0, int row1, int row2, int row3)
        {
            var table = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                byte x = (byte)i;
                table[i] = Multiply(x, row0)
                    | (uint)Multiply(x, row1) << 8
                    | (uint)Multiply(x, row2) << 16
                    | (uint)Multiply(x, row3) << 24;
            }

            return table;
        }

        private static uint[] BuildTable(byte[] box, int row0, int row1, int row2, int row3)
        {
            var table = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                byte x = box[i];
                table[i] = Multiply(x, row0)
                    | (uint)Multiply(x, row1) << 8
                    | (uint)Multiply(x, row2) << 16
                    | (uint)Multiply(x, row3) << 24;
            }

            return table;
        }

        private static uint[] RotateTable(uint[] table, int bits)
        {
            var rotated = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                rotated[i] = (table[i] << bits) | (table[i] >> (32 - bits));
            }

            return rotated;
        }
    }
}
