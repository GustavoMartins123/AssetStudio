#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AssetStudio
{
    /// <summary>
    /// Fast, allocation-free xxHash64 implementation shared across core scanning and mmap sidecar indexing.
    /// Zero external NuGet dependencies.
    /// </summary>
    public static class XxHash64
    {
        public const int SampleWindowBytes = 4096;
        public const int MaxBoundedSampleBytes = SampleWindowBytes * 3; // 12 KiB (head + mid + tail)

        private const ulong Prime64_1 = 11400714785074694791UL;
        private const ulong Prime64_2 = 14029467366897019727UL;
        private const ulong Prime64_3 = 1609587929392839161UL;
        private const ulong Prime64_4 = 9650029242287828579UL;
        private const ulong Prime64_5 = 2870177450012600261UL;

        public static ulong Compute(ReadOnlySpan<byte> data, ulong seed = 0)
        {
            var length = data.Length;
            var offset = 0;
            ulong h64;

            if (length >= 32)
            {
                ulong v1 = seed + Prime64_1 + Prime64_2;
                ulong v2 = seed + Prime64_2;
                ulong v3 = seed + 0UL;
                ulong v4 = seed - Prime64_1;

                var limit = length - 32;
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
                h64 = seed + Prime64_5;
            }

            h64 += (ulong)length;

            while (offset + 8 <= length)
            {
                ulong k1 = Round(0UL, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)));
                h64 ^= k1;
                h64 = (BitOperations.RotateLeft(h64, 27) * Prime64_1) + Prime64_4;
                offset += 8;
            }

            if (offset + 4 <= length)
            {
                h64 ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)) * Prime64_1;
                h64 = (BitOperations.RotateLeft(h64, 23) * Prime64_2) + Prime64_3;
                offset += 4;
            }

            while (offset < length)
            {
                h64 ^= data[offset] * Prime64_5;
                h64 = BitOperations.RotateLeft(h64, 11) * Prime64_1;
                offset++;
            }

            h64 ^= h64 >> 33;
            h64 *= Prime64_2;
            h64 ^= h64 >> 29;
            h64 *= Prime64_3;
            h64 ^= h64 >> 32;
            return h64;
        }

        /// <summary>
        /// Computes a bounded O(1)-I/O content sample hash (&lt;= 12 KiB total read per file):
        /// reuses the already-read head window [0, min(len, 4096)) and, when len &gt; 4096,
        /// samples middle and tail windows (or the full remainder when len &lt;= 12288).
        /// </summary>
        public static ulong ComputeBoundedSampleHash(Stream stream, long fileSize, ReadOnlySpan<byte> headAlreadyRead)
        {
            return ComputeBoundedSampleHash(stream, fileSize, headAlreadyRead, out _);
        }

        public static ulong ComputeBoundedSampleHash(
            Stream stream,
            long fileSize,
            ReadOnlySpan<byte> headAlreadyRead,
            out long totalBytesSampled)
        {
            ArgumentNullException.ThrowIfNull(stream);

            Span<byte> meta = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(meta.Slice(0, 8), Math.Max(0L, fileSize));
            BinaryPrimitives.WriteInt64LittleEndian(meta.Slice(8, 8), headAlreadyRead.Length);
            ulong hash = Compute(meta, 0x9E3779B97F4A7C15UL);

            if (headAlreadyRead.Length > 0)
            {
                hash = Compute(headAlreadyRead, hash);
            }

            totalBytesSampled = headAlreadyRead.Length;

            if (fileSize <= SampleWindowBytes || !stream.CanSeek)
            {
                return hash;
            }

            if (fileSize <= MaxBoundedSampleBytes)
            {
                int remaining = (int)(fileSize - headAlreadyRead.Length);
                if (remaining > 0)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(remaining);
                    try
                    {
                        stream.Position = headAlreadyRead.Length;
                        int read = ReadUpTo(stream, buffer, remaining);
                        if (read > 0)
                        {
                            hash = MixRegion(hash, headAlreadyRead.Length, buffer.AsSpan(0, read));
                            totalBytesSampled += read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                return hash;
            }

            var window = ArrayPool<byte>.Shared.Rent(SampleWindowBytes);
            try
            {
                long midOffset = Math.Max((long)headAlreadyRead.Length, (fileSize / 2L) - (SampleWindowBytes / 2));
                int midWanted = (int)Math.Min(SampleWindowBytes, fileSize - midOffset);
                if (midWanted > 0)
                {
                    stream.Position = midOffset;
                    int midRead = ReadUpTo(stream, window, midWanted);
                    if (midRead > 0)
                    {
                        hash = MixRegion(hash, midOffset, window.AsSpan(0, midRead));
                        totalBytesSampled += midRead;
                    }
                }

                long tailOffset = Math.Max(0L, fileSize - SampleWindowBytes);
                int tailWanted = (int)Math.Min(SampleWindowBytes, fileSize - tailOffset);
                if (tailWanted > 0)
                {
                    stream.Position = tailOffset;
                    int tailRead = ReadUpTo(stream, window, tailWanted);
                    if (tailRead > 0)
                    {
                        hash = MixRegion(hash, tailOffset, window.AsSpan(0, tailRead));
                        totalBytesSampled += tailRead;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(window);
            }

            return hash;
        }

        public static ulong ComputeFileSampleHash(string filePath, long fileSize, out long totalBytesSampled)
        {
            if (fileSize <= 0)
            {
                Span<byte> zeroMeta = stackalloc byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(zeroMeta.Slice(0, 8), 0L);
                BinaryPrimitives.WriteInt64LittleEndian(zeroMeta.Slice(8, 8), 0L);
                totalBytesSampled = 0;
                return Compute(zeroMeta, 0x9E3779B97F4A7C15UL);
            }

            int headSize = (int)Math.Min(SampleWindowBytes, fileSize);
            var head = ArrayPool<byte>.Shared.Rent(headSize);
            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read = ReadUpTo(stream, head, headSize);
                return ComputeBoundedSampleHash(stream, fileSize, head.AsSpan(0, read), out totalBytesSampled);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }

        private static ulong MixRegion(ulong seed, long offset, ReadOnlySpan<byte> data)
        {
            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(0, 8), offset);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), data.Length);
            ulong mixed = Compute(header, seed);
            return Compute(data, mixed);
        }

        internal static int ReadUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, total, count - total);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Round(ulong acc, ulong input)
        {
            acc += input * Prime64_2;
            acc = BitOperations.RotateLeft(acc, 31);
            acc *= Prime64_1;
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MergeRound(ulong acc, ulong val)
        {
            val = Round(0UL, val);
            acc ^= val;
            acc = (acc * Prime64_1) + Prime64_4;
            return acc;
        }
    }
}
