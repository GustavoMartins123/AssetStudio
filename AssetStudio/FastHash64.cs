#nullable enable
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AssetStudio
{
    /// <summary>
    /// Zero-dependency 64-bit XXH64 block hasher operating on 32-byte (4x8-byte) and 8-byte stripes.
    /// </summary>
    public static class FastHash64
    {
        private const ulong Prime1 = 11400714785074694791UL;
        private const ulong Prime2 = 14029467366897019727UL;
        private const ulong Prime3 = 1609587929392839161UL;
        private const ulong Prime4 = 9650029242287828579UL;
        private const ulong Prime5 = 2870177450012600261UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
        {
            int length = data.Length;
            int offset = 0;
            ulong h64;

            if (length >= 32)
            {
                ulong v1 = seed + Prime1 + Prime2;
                ulong v2 = seed + Prime2;
                ulong v3 = seed;
                ulong v4 = seed - Prime1;

                int limit = length - 32;
                do
                {
                    v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)));
                    v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8)));
                    v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 16, 8)));
                    v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 24, 8)));
                    offset += 32;
                }
                while (offset <= limit);

                h64 = BitOperations.RotateLeft(v1, 1)
                    + BitOperations.RotateLeft(v2, 7)
                    + BitOperations.RotateLeft(v3, 12)
                    + BitOperations.RotateLeft(v4, 18);

                h64 = MergeRound(h64, v1);
                h64 = MergeRound(h64, v2);
                h64 = MergeRound(h64, v3);
                h64 = MergeRound(h64, v4);
            }
            else
            {
                h64 = seed + Prime5;
            }

            h64 += (ulong)length;

            while (offset <= length - 8)
            {
                ulong k1 = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)));
                h64 ^= k1;
                h64 = BitOperations.RotateLeft(h64, 27) * Prime1 + Prime4;
                offset += 8;
            }

            if (offset <= length - 4)
            {
                h64 ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)) * Prime1;
                h64 = BitOperations.RotateLeft(h64, 23) * Prime2 + Prime3;
                offset += 4;
            }

            while (offset < length)
            {
                h64 ^= data[offset] * Prime5;
                h64 = BitOperations.RotateLeft(h64, 11) * Prime1;
                offset++;
            }

            return Avalanche(h64);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Combine(ulong seed, ulong value)
        {
            ulong h64 = seed + Prime5 + sizeof(ulong);
            h64 ^= Round(0, value);
            h64 = BitOperations.RotateLeft(h64, 27) * Prime1 + Prime4;
            return Avalanche(h64);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Round(ulong acc, ulong input)
        {
            acc += input * Prime2;
            acc = BitOperations.RotateLeft(acc, 31);
            acc *= Prime1;
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MergeRound(ulong acc, ulong val)
        {
            val = Round(0, val);
            acc ^= val;
            acc = acc * Prime1 + Prime4;
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Avalanche(ulong h64)
        {
            h64 ^= h64 >> 33;
            h64 *= Prime2;
            h64 ^= h64 >> 29;
            h64 *= Prime3;
            h64 ^= h64 >> 32;
            return h64;
        }
    }
}
