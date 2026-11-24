#nullable enable
using System;
using System.Buffers.Binary;

namespace AssetStudio
{
    public sealed class RijndaelBlockCipher
    {
        private const int MaxStateWords = 8;
        private const int MaxRounds = 14;

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

        private readonly uint[] encryptKeys = new uint[MaxStateWords * (MaxRounds + 1)];
        private readonly uint[] decryptKeys = new uint[MaxStateWords * (MaxRounds + 1)];
        private readonly int nb;
        private readonly int nr;
        private readonly int encShift1;
        private readonly int encShift2;
        private readonly int encShift3;
        private readonly int decShift1;
        private readonly int decShift2;
        private readonly int decShift3;

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

            int nk = key.Length / 4;
            nb = blockSizeBits / 32;
            nr = Math.Max(nk, nb) + 6;
            KeySize = key.Length;

            bool wide = nb == MaxStateWords;
            encShift1 = 1;
            encShift2 = wide ? 3 : 2;
            encShift3 = wide ? 4 : 3;
            decShift1 = (nb - encShift1) % nb;
            decShift2 = (nb - encShift2) % nb;
            decShift3 = (nb - encShift3) % nb;

            ExpandKey(key, nk);
        }

        public void EncryptBlock(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            ValidateBlockArgs(source, destination);
            Span<uint> s = stackalloc uint[MaxStateWords];
            Span<uint> t = stackalloc uint[MaxStateWords];
            LoadState(source, s);

            for (int c = 0; c < nb; c++)
            {
                s[c] ^= encryptKeys[c];
            }

            for (int round = 1; round < nr; round++)
            {
                Mix(s, t, Te0, Te1, Te2, Te3, encShift1, encShift2, encShift3, encryptKeys, round * nb);
                Swap(ref s, ref t);
            }

            Substitute(s, t, SBox, encShift1, encShift2, encShift3, encryptKeys, nr * nb);
            StoreState(t.Slice(0, nb), destination);
        }

        public void DecryptBlock(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            ValidateBlockArgs(source, destination);
            Span<uint> s = stackalloc uint[MaxStateWords];
            Span<uint> t = stackalloc uint[MaxStateWords];
            LoadState(source, s);

            for (int c = 0; c < nb; c++)
            {
                s[c] ^= decryptKeys[nr * nb + c];
            }

            for (int round = nr - 1; round >= 1; round--)
            {
                Mix(s, t, Td0, Td1, Td2, Td3, decShift1, decShift2, decShift3, decryptKeys, round * nb);
                Swap(ref s, ref t);
            }

            Substitute(s, t, InvSBox, decShift1, decShift2, decShift3, decryptKeys, 0);
            StoreState(t.Slice(0, nb), destination);
        }

        private void Mix(
            ReadOnlySpan<uint> s,
            Span<uint> t,
            uint[] t0,
            uint[] t1,
            uint[] t2,
            uint[] t3,
            int shift1,
            int shift2,
            int shift3,
            uint[] keys,
            int keyBase)
        {
            for (int c = 0; c < nb; c++)
            {
                t[c] = (t0[s[c] & 0xFF]
                        ^ t1[(s[(c + shift1) % nb] >> 8) & 0xFF]
                        ^ t2[(s[(c + shift2) % nb] >> 16) & 0xFF]
                        ^ t3[s[(c + shift3) % nb] >> 24])
                    ^ keys[keyBase + c];
            }
        }

        private void Substitute(
            ReadOnlySpan<uint> s,
            Span<uint> t,
            byte[] box,
            int shift1,
            int shift2,
            int shift3,
            uint[] keys,
            int keyBase)
        {
            for (int c = 0; c < nb; c++)
            {
                t[c] = (box[s[c] & 0xFF]
                        | (uint)box[(s[(c + shift1) % nb] >> 8) & 0xFF] << 8
                        | (uint)box[(s[(c + shift2) % nb] >> 16) & 0xFF] << 16
                        | (uint)box[s[(c + shift3) % nb] >> 24] << 24)
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

        private void LoadState(ReadOnlySpan<byte> source, Span<uint> state)
        {
            for (int c = 0; c < nb; c++)
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

        private void ExpandKey(ReadOnlySpan<byte> key, int nk)
        {
            int total = nb * (nr + 1);
            Span<uint> w = stackalloc uint[MaxStateWords * (MaxRounds + 1)];
            for (int i = 0; i < nk; i++)
            {
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(key.Slice(i * 4, 4));
            }

            byte rcon = 1;
            for (int i = nk; i < total; i++)
            {
                uint temp = w[i - 1];
                if (i % nk == 0)
                {
                    temp = SubWord((temp << 8) | (temp >> 24)) ^ (uint)rcon << 24;
                    rcon = XTime(rcon);
                }
                else if (nk > 6 && i % nk == 4)
                {
                    temp = SubWord(temp);
                }

                w[i] = w[i - nk] ^ temp;
            }

            for (int i = 0; i < total; i++)
            {
                encryptKeys[i] = BinaryPrimitives.ReverseEndianness(w[i]);
                decryptKeys[i] = encryptKeys[i];
            }

            for (int i = nb; i < nr * nb; i++)
            {
                decryptKeys[i] = InvMixColumn(decryptKeys[i]);
            }
        }

        private static uint SubWord(uint value)
        {
            return (uint)SBox[value >> 24] << 24
                | (uint)SBox[(value >> 16) & 0xFF] << 16
                | (uint)SBox[(value >> 8) & 0xFF] << 8
                | SBox[value & 0xFF];
        }

        private static uint InvMixColumn(uint value)
        {
            byte a0 = (byte)value;
            byte a1 = (byte)(value >> 8);
            byte a2 = (byte)(value >> 16);
            byte a3 = (byte)(value >> 24);
            uint r0 = (uint)(Multiply(a0, 14) ^ Multiply(a1, 11) ^ Multiply(a2, 13) ^ Multiply(a3, 9));
            uint r1 = (uint)(Multiply(a0, 9) ^ Multiply(a1, 14) ^ Multiply(a2, 11) ^ Multiply(a3, 13));
            uint r2 = (uint)(Multiply(a0, 13) ^ Multiply(a1, 9) ^ Multiply(a2, 14) ^ Multiply(a3, 11));
            uint r3 = (uint)(Multiply(a0, 11) ^ Multiply(a1, 13) ^ Multiply(a2, 9) ^ Multiply(a3, 14));
            return r0 | r1 << 8 | r2 << 16 | r3 << 24;
        }

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
