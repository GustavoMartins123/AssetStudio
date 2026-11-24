#nullable enable
using K4os.Compression.LZ4;
using System;
using System.IO;

namespace AssetStudio
{
    public static class UnityFsRangeProbe
    {
        private const uint BlocksInfoAtTheEnd = 0x80;
        private const uint BlocksInfoNeedPaddingAtStart = 0x200;
        private const int MaxBlocksInfoSize = 16 * 1024 * 1024;
        private const int MaxProbeBlockCompressed = 4 * 1024 * 1024;
        private const int MaxProbeBlockUncompressed = 16 * 1024 * 1024;
        private static readonly long[] RangeCandidates = { 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576 };

        private readonly struct Layout
        {
            public readonly long BlocksInfoOffset;
            public readonly int CompressedBlocksInfoSize;
            public readonly int UncompressedBlocksInfoSize;
            public readonly uint Flags;
            public readonly long DataStart;

            public Layout(long blocksInfoOffset, int compressedBlocksInfoSize, int uncompressedBlocksInfoSize, uint flags, long dataStart)
            {
                BlocksInfoOffset = blocksInfoOffset;
                CompressedBlocksInfoSize = compressedBlocksInfoSize;
                UncompressedBlocksInfoSize = uncompressedBlocksInfoSize;
                Flags = flags;
                DataStart = dataStart;
            }
        }

        public readonly struct Result
        {
            public Result(bool valid, long firstPassing, long lastPassing, bool whole)
            {
                Valid = valid;
                FirstPassing = firstPassing;
                LastPassing = lastPassing;
                Whole = whole;
            }

            public bool Valid { get; }
            public long FirstPassing { get; }
            public long LastPassing { get; }
            public bool Whole { get; }
        }

        public static bool ValidateRange(Stream stream, ReadOnlySpan<byte> header, byte[] key, long range)
            => TryParseLayout(header, stream.Length, out Layout layout) && Validate(stream, layout, key, range);

        public static Result Detect(Stream stream, ReadOnlySpan<byte> header, byte[] key)
        {
            if (!TryParseLayout(header, stream.Length, out Layout layout))
            {
                return new Result(false, 0, 0, false);
            }

            int passing = 0;
            long firstPassing = 0;
            long lastPassing = 0;
            foreach (long range in RangeCandidates)
            {
                if (range >= stream.Length)
                {
                    break;
                }

                if (Validate(stream, layout, key, range))
                {
                    if (passing == 0)
                    {
                        firstPassing = range;
                    }

                    lastPassing = range;
                    passing++;
                }
            }

            bool whole = Validate(stream, layout, key, -1);
            return new Result(passing > 0 || whole, firstPassing, lastPassing, whole);
        }

        private static bool TryParseLayout(ReadOnlySpan<byte> header, long totalLength, out Layout layout)
        {
            layout = default;
            int pos = 8;
            if (header.Length < pos + 4)
            {
                return false;
            }

            uint version = UnityHeaderProbe.ReadUInt32BigEndian(header, pos);
            pos += 4;
            for (int token = 0; token < 2; token++)
            {
                int end = header.Slice(pos).IndexOf((byte)0);
                if (end < 0)
                {
                    return false;
                }

                pos += end + 1;
            }

            if (pos + 20 > header.Length)
            {
                return false;
            }

            long size = UnityHeaderProbe.ReadInt64BigEndian(header, pos);
            pos += 8;
            uint compressed = UnityHeaderProbe.ReadUInt32BigEndian(header, pos);
            pos += 4;
            uint uncompressed = UnityHeaderProbe.ReadUInt32BigEndian(header, pos);
            pos += 4;
            uint flags = UnityHeaderProbe.ReadUInt32BigEndian(header, pos);
            pos += 4;

            if (size != totalLength || compressed == 0 || uncompressed == 0 || compressed > MaxBlocksInfoSize)
            {
                return false;
            }

            if (version >= 7)
            {
                pos = (pos + 15) & ~15;
            }

            bool atEnd = (flags & BlocksInfoAtTheEnd) != 0;
            long blocksInfoOffset = atEnd ? totalLength - compressed : pos;
            long dataStart = atEnd ? pos : pos + compressed;
            if ((flags & BlocksInfoNeedPaddingAtStart) != 0)
            {
                dataStart = (dataStart + 15) & ~15;
            }

            if (blocksInfoOffset < 0 || blocksInfoOffset + compressed > totalLength || dataStart < 0 || dataStart > totalLength)
            {
                return false;
            }

            layout = new Layout(blocksInfoOffset, (int)compressed, (int)uncompressed, flags, dataStart);
            return true;
        }

        private static bool Validate(Stream stream, Layout layout, byte[] key, long range)
        {
            var compressedInfo = new byte[layout.CompressedBlocksInfoSize];
            if (!ReadDecoded(stream, layout.BlocksInfoOffset, compressedInfo, key, range))
            {
                return false;
            }

            byte[] info;
            switch (layout.Flags & 0x3F)
            {
                case 0:
                    if (compressedInfo.Length != layout.UncompressedBlocksInfoSize)
                    {
                        return false;
                    }

                    info = compressedInfo;
                    break;
                case 2:
                case 3:
                    if (!Lz4BlockValidator.IsValid(compressedInfo, layout.UncompressedBlocksInfoSize))
                    {
                        return false;
                    }

                    info = new byte[layout.UncompressedBlocksInfoSize];
                    try
                    {
                        if (LZ4Codec.Decode(compressedInfo, info) != info.Length)
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }

                    break;
                default:
                    return range < 0;
            }

            if (info.Length < 24)
            {
                return false;
            }

            int blockCount = (int)ReadUInt32BE(info, 16);
            if (blockCount <= 0 || blockCount > 1_000_000 || 20L + blockCount * 10L + 4 > info.Length)
            {
                return false;
            }

            int directoryOffset = 20 + blockCount * 10;
            int nodeCount = (int)ReadUInt32BE(info, directoryOffset);
            if (nodeCount <= 0 || nodeCount > 100_000)
            {
                return false;
            }

            bool hasFirstNode = directoryOffset + 24 <= info.Length
                && (ReadUInt32BE(info, directoryOffset + 20) & 4u) != 0
                && ReadInt64BE(info, directoryOffset + 4) == 0;
            long firstNodeSize = hasFirstNode ? ReadInt64BE(info, directoryOffset + 12) : 0;

            long cursor = layout.DataStart;
            long firstOffset = 0, lastOffset = 0, straddleOffset = -1;
            uint firstCompressed = 0, firstUncompressed = 0, firstFlags = 0;
            uint lastCompressed = 0, lastUncompressed = 0, lastFlags = 0;
            uint straddleCompressed = 0, straddleUncompressed = 0, straddleFlags = 0;
            for (int i = 0; i < blockCount; i++)
            {
                int entry = 20 + i * 10;
                uint uncompressed = ReadUInt32BE(info, entry);
                uint compressed = ReadUInt32BE(info, entry + 4);
                uint flags = ReadUInt16BE(info, entry + 8);
                if (compressed == 0 || uncompressed == 0 || cursor + compressed > stream.Length)
                {
                    return false;
                }

                if (i == 0)
                {
                    firstOffset = cursor;
                    firstCompressed = compressed;
                    firstUncompressed = uncompressed;
                    firstFlags = flags;
                }

                if (range > 0 && straddleOffset < 0 && cursor < range && cursor + compressed > range)
                {
                    straddleOffset = cursor;
                    straddleCompressed = compressed;
                    straddleUncompressed = uncompressed;
                    straddleFlags = flags;
                }

                if (i == blockCount - 1)
                {
                    lastOffset = cursor;
                    lastCompressed = compressed;
                    lastUncompressed = uncompressed;
                    lastFlags = flags;
                }

                cursor += compressed;
            }

            if (cursor < stream.Length && !IsZeroPadding(stream, key, range, cursor))
            {
                return false;
            }

            if (!ValidateBlock(stream, key, range, firstOffset, firstCompressed, firstUncompressed, firstFlags, decode: true, out byte[]? firstDecoded))
            {
                return false;
            }

            if (hasFirstNode && firstNodeSize >= 48
                && (firstDecoded == null || firstDecoded.Length < 48 || !UnityHeaderProbe.IsSerializedFileHeader(firstDecoded, firstNodeSize)))
            {
                return false;
            }

            if (straddleOffset >= 0 && straddleOffset != firstOffset && straddleOffset != lastOffset
                && !ValidateBlock(stream, key, range, straddleOffset, straddleCompressed, straddleUncompressed, straddleFlags, decode: false, out _))
            {
                return false;
            }

            return lastOffset == firstOffset
                || ValidateBlock(stream, key, range, lastOffset, lastCompressed, lastUncompressed, lastFlags, decode: false, out _);
        }

        private static bool IsZeroPadding(Stream stream, byte[] key, long range, long offset)
        {
            var buffer = new byte[64 * 1024];
            while (offset < stream.Length)
            {
                int count = (int)Math.Min(buffer.Length, stream.Length - offset);
                if (!ReadDecoded(stream, offset, buffer.AsSpan(0, count), key, range))
                {
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] != 0)
                    {
                        return false;
                    }
                }

                offset += count;
            }

            return true;
        }

        private static bool ValidateBlock(
            Stream stream,
            byte[] key,
            long range,
            long offset,
            uint compressed,
            uint uncompressed,
            uint flags,
            bool decode,
            out byte[]? decoded)
        {
            decoded = null;
            uint type = flags & 0x3F;
            if (type == 0)
            {
                if (compressed != uncompressed)
                {
                    return false;
                }

                var raw = new byte[(int)Math.Min(compressed, 128u)];
                if (!ReadDecoded(stream, offset, raw, key, range))
                {
                    return false;
                }

                decoded = raw;
                return true;
            }

            if (type is not (2 or 3))
            {
                return true;
            }

            if (compressed > MaxProbeBlockCompressed || uncompressed > MaxProbeBlockUncompressed)
            {
                return true;
            }

            var compressedBuffer = new byte[compressed];
            if (!ReadDecoded(stream, offset, compressedBuffer, key, range)
                || !Lz4BlockValidator.IsValid(compressedBuffer, (int)uncompressed))
            {
                return false;
            }

            if (!decode)
            {
                return true;
            }

            var uncompressedBuffer = new byte[uncompressed];
            try
            {
                if (LZ4Codec.Decode(compressedBuffer, uncompressedBuffer) != (int)uncompressed)
                {
                    return false;
                }

                decoded = uncompressedBuffer;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadDecoded(Stream stream, long offset, Span<byte> destination, byte[] key, long range)
        {
            stream.Position = offset;
            int total = 0;
            while (total < destination.Length)
            {
                int read = stream.Read(destination.Slice(total));
                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            long end = range < 0 ? stream.Length : Math.Min(stream.Length, range);
            long sliceEnd = Math.Min(offset + destination.Length, end);
            if (sliceEnd > offset)
            {
                AesCtr.XorLittleEndian(destination.Slice(0, (int)(sliceEnd - offset)), key, offset);
            }

            return true;
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset) => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

        private static uint ReadUInt32BE(byte[] buffer, int offset)
            => ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

        private static long ReadInt64BE(byte[] buffer, int offset) => ((long)ReadUInt32BE(buffer, offset) << 32) | ReadUInt32BE(buffer, offset + 4);
    }

    internal static class Lz4BlockValidator
    {
        public static bool IsValid(ReadOnlySpan<byte> source, int decodedLength)
        {
            int input = 0;
            int output = 0;
            while (input < source.Length)
            {
                byte token = source[input++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    byte extra;
                    do
                    {
                        if (input >= source.Length)
                        {
                            return false;
                        }

                        extra = source[input++];
                        literals += extra;
                        if (literals > decodedLength - output)
                        {
                            return false;
                        }
                    } while (extra == 255);
                }

                if (literals > decodedLength - output || literals > source.Length - input)
                {
                    return false;
                }

                input += literals;
                output += literals;
                if (input == source.Length)
                {
                    break;
                }

                if (input + 2 > source.Length)
                {
                    return false;
                }

                int offset = source[input] | source[input + 1] << 8;
                input += 2;
                if (offset == 0 || offset > output)
                {
                    return false;
                }

                int match = token & 15;
                if (match == 15)
                {
                    byte extra;
                    do
                    {
                        if (input >= source.Length)
                        {
                            return false;
                        }

                        extra = source[input++];
                        match += extra;
                        if (match + 4 > decodedLength - output)
                        {
                            return false;
                        }
                    } while (extra == 255);
                }

                match += 4;
                if (match > decodedLength - output)
                {
                    return false;
                }

                output += match;
            }

            return output == decodedLength;
        }
    }
}
