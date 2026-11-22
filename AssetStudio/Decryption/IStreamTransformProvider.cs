#nullable enable
using K4os.Compression.LZ4;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public interface IStreamTransformProvider
    {
        string Id { get; }
        int Priority { get; }
        Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader);
    }

    public sealed class DecryptionProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("fileNamePattern")]
        public string? FileNamePattern { get; set; }

        [JsonPropertyName("magicHex")]
        public string? MagicHex { get; set; }

        [JsonPropertyName("magicOffset")]
        public int MagicOffset { get; set; }

        [JsonPropertyName("skipBytes")]
        public long SkipBytes { get; set; }

        [JsonPropertyName("xorKeyHex")]
        public string? XorKeyHex { get; set; }

        [JsonPropertyName("rangeStart")]
        public long RangeStart { get; set; }

        [JsonPropertyName("rangeLength")]
        public long RangeLength { get; set; } = -1;
    }

    public sealed class DeclarativeProfileTransformProvider : IStreamTransformProvider
    {
        private readonly Func<IReadOnlyList<DecryptionProfile>> getProfiles;

        public DeclarativeProfileTransformProvider(Func<IReadOnlyList<DecryptionProfile>> getProfiles)
        {
            this.getProfiles = getProfiles ?? throw new ArgumentNullException(nameof(getProfiles));
        }

        public string Id => "DeclarativeProfile";
        public int Priority => 200;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            var profiles = getProfiles();
            if (profiles.Count == 0)
            {
                return null;
            }

            string fileName = Path.GetFileName(filePath);
            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (!MatchesProfile(profile, fileName, initialHeader, stream.Length))
                {
                    continue;
                }

                byte[]? xorKey = TryParseHexBytes(profile.XorKeyHex);
                if (xorKey != null && xorKey.Length > 0)
                {
                    return new XorTransformStream(
                        stream,
                        xorKey,
                        headerSkipBytes: Math.Max(0, profile.SkipBytes),
                        rangeStart: Math.Max(0, profile.RangeStart),
                        rangeLength: profile.RangeLength);
                }

                if (profile.SkipBytes > 0 && profile.SkipBytes < stream.Length)
                {
                    return new SubStream(stream, profile.SkipBytes, stream.Length - profile.SkipBytes, leaveOpen: false);
                }
            }

            return null;
        }

        private static bool MatchesProfile(
            DecryptionProfile profile,
            string fileName,
            ReadOnlySpan<byte> initialHeader,
            long streamLength)
        {
            if (profile.SkipBytes < 0 || profile.SkipBytes >= streamLength)
            {
                return false;
            }

            bool hasMatcher = false;

            if (!string.IsNullOrWhiteSpace(profile.FileNamePattern))
            {
                hasMatcher = true;
                if (!MatchesWildcard(fileName, profile.FileNamePattern!))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.MagicHex))
            {
                hasMatcher = true;
                byte[]? magic = TryParseHexBytes(profile.MagicHex);
                if (magic == null || magic.Length == 0 || !UnityHeaderProbe.MatchesAt(initialHeader, profile.MagicOffset, magic))
                {
                    return false;
                }
            }

            return hasMatcher;
        }

        private static bool MatchesWildcard(string fileName, string pattern)
            => FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true);

        internal static byte[]? TryParseHexBytes(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            Span<char> cleaned = stackalloc char[hex.Length];
            int len = 0;
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                if (!char.IsWhiteSpace(c) && c != '-')
                {
                    cleaned[len++] = c;
                }
            }

            if (len == 0 || (len & 1) != 0)
            {
                return null;
            }

            try
            {
                return Convert.FromHexString(cleaned.Slice(0, len));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public sealed class OffsetHeaderSkipTransformProvider : IStreamTransformProvider
    {
        public string Id => "OffsetHeaderSkip";
        public int Priority => 300;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 32)
            {
                return null;
            }

            // Do not skip if offset 0 is already a valid Unity bundle or SerializedFile
            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length))
            {
                return null;
            }

            if (!UnityHeaderProbe.TryFindOffsetUnityBundleHeader(initialHeader, stream.Length, out int offset, out _, out _, out _)
                || offset <= 0
                || offset >= stream.Length)
            {
                return null;
            }

            return new SubStream(stream, offset, stream.Length - offset, leaveOpen: false);
        }
    }

    public sealed class KnownPlaintextXorTransformProvider : IStreamTransformProvider
    {
        private static readonly long[] GenericPrefixCutoffs =
        {
            128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 102400, 131072, 262144
        };

        public string Id => "KnownPlaintextXor";
        public int Priority => 400;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 32)
            {
                return null;
            }

            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length))
            {
                return null;
            }

            if (!UnityHeaderProbe.TryDetectRepeatingXorUnityFsKey(initialHeader, stream.Length, out byte[] xorKey, out _, out _))
            {
                return null;
            }

            long rangeLength = DetectXorRangeLength(stream, xorKey);
            stream.Position = 0;
            return new XorTransformStream(stream, xorKey, rangeLength: rangeLength);
        }

        private static long DetectXorRangeLength(Stream stream, byte[] xorKey)
        {
            if (!stream.CanSeek || stream.Length < 64)
            {
                return -1;
            }

            // Read enough decoded header bytes to parse the UnityFS header fields
            int headerBufLen = (int)Math.Min(stream.Length, 256);
            byte[] headerBuf = new byte[headerBufLen];
            stream.Position = 0;
            int read = ReadExactOrAvailable(stream, headerBuf, 0, headerBufLen);
            if (read < 48)
            {
                return -1;
            }

            UnityHeaderProbe.XorBytesRepeating(headerBuf.AsSpan(0, read), 0, xorKey);
            if (!TryParseUnityFsLayout(headerBuf.AsSpan(0, read), stream.Length, out var layout))
            {
                return -1;
            }

            // Fast path: check full-stream XOR first
            if (ValidateBundleBlocksUnderRange(stream, xorKey, -1, layout))
            {
                return -1;
            }

            // Probe generic prefix-only XOR cutoffs (common header/prefix encryption bounds)
            for (int i = 0; i < GenericPrefixCutoffs.Length; i++)
            {
                long cutoff = GenericPrefixCutoffs[i];
                if (cutoff >= stream.Length)
                {
                    break;
                }

                if (ValidateBundleBlocksUnderRange(stream, xorKey, cutoff, layout))
                {
                    return cutoff;
                }
            }

            return -1;
        }

        private readonly struct UnityFsLayout
        {
            public readonly uint Version;
            public readonly long BlocksInfoOffset;
            public readonly int CompressedBlocksInfoSize;
            public readonly int UncompressedBlocksInfoSize;
            public readonly ArchiveFlags Flags;
            public readonly long DataStartOffset;

            public UnityFsLayout(
                uint version,
                long blocksInfoOffset,
                int compressedBlocksInfoSize,
                int uncompressedBlocksInfoSize,
                ArchiveFlags flags,
                long dataStartOffset)
            {
                Version = version;
                BlocksInfoOffset = blocksInfoOffset;
                CompressedBlocksInfoSize = compressedBlocksInfoSize;
                UncompressedBlocksInfoSize = uncompressedBlocksInfoSize;
                Flags = flags;
                DataStartOffset = dataStartOffset;
            }
        }

        private static bool TryParseUnityFsLayout(ReadOnlySpan<byte> decodedHeader, long streamLength, out UnityFsLayout layout)
        {
            layout = default;
            int pos = 0;
            // Skip signature ("UnityFS\0")
            while (pos < decodedHeader.Length && decodedHeader[pos] != 0) pos++;
            if (pos >= decodedHeader.Length) return false;
            pos++;

            if (pos + 4 > decodedHeader.Length) return false;
            uint version = ReadUInt32BE(decodedHeader, pos);
            pos += 4;

            // Skip unityVersion and unityRevision null-terminated strings
            for (int s = 0; s < 2; s++)
            {
                while (pos < decodedHeader.Length && decodedHeader[pos] != 0) pos++;
                if (pos >= decodedHeader.Length) return false;
                pos++;
            }

            if (pos + 20 > decodedHeader.Length) return false;
            long size = ReadInt64BE(decodedHeader, pos); pos += 8;
            uint compBlocksInfo = ReadUInt32BE(decodedHeader, pos); pos += 4;
            uint uncompBlocksInfo = ReadUInt32BE(decodedHeader, pos); pos += 4;
            ArchiveFlags flags = (ArchiveFlags)ReadUInt32BE(decodedHeader, pos); pos += 4;

            if (size <= 0
                || compBlocksInfo == 0
                || uncompBlocksInfo == 0
                || compBlocksInfo > 16 * 1024 * 1024
                || uncompBlocksInfo > 64 * 1024 * 1024
                || compBlocksInfo > streamLength)
            {
                return false;
            }

            if (version >= 7)
            {
                pos = (pos + 15) & ~15;
            }

            bool atEnd = (flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0;
            long blocksInfoOffset = atEnd ? (streamLength - compBlocksInfo) : pos;
            if (blocksInfoOffset < 0 || blocksInfoOffset + compBlocksInfo > streamLength)
            {
                return false;
            }

            long dataStart = atEnd ? pos : (pos + compBlocksInfo);
            if ((flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                dataStart = (dataStart + 15) & ~15;
            }

            if (dataStart < 0 || dataStart > streamLength)
            {
                return false;
            }

            layout = new UnityFsLayout(version, blocksInfoOffset, (int)compBlocksInfo, (int)uncompBlocksInfo, flags, dataStart);
            return true;
        }

        private static bool ValidateBundleBlocksUnderRange(
            Stream stream,
            byte[] xorKey,
            long rangeLength,
            UnityFsLayout layout)
        {
            byte[] compInfo = new byte[layout.CompressedBlocksInfoSize];
            if (!ReadRangeDecoded(stream, layout.BlocksInfoOffset, compInfo, xorKey, rangeLength))
            {
                return false;
            }

            byte[] uncompInfo;
            var infoComp = (CompressionType)(layout.Flags & ArchiveFlags.CompressionTypeMask);
            switch (infoComp)
            {
                case CompressionType.None:
                    if (compInfo.Length != layout.UncompressedBlocksInfoSize) return false;
                    uncompInfo = compInfo;
                    break;
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                    uncompInfo = new byte[layout.UncompressedBlocksInfoSize];
                    try
                    {
                        int written = LZ4Codec.Decode(compInfo, uncompInfo);
                        if (written != layout.UncompressedBlocksInfoSize)
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
                    // For LZMA or other codecs, avoid expensive probing if we cannot cheaply validate blocksInfo
                    return rangeLength < 0;
            }

            // Parse blocksInfo: 16-byte hash + int32 blockCount + blockCount * (uint32 uncomp, uint32 comp, uint16 flags)
            if (uncompInfo.Length < 20)
            {
                return false;
            }

            int blockCount = (int)ReadUInt32BE(uncompInfo, 16);
            if (blockCount <= 0 || blockCount > 1_000_000 || 20L + blockCount * 10L > uncompInfo.Length)
            {
                return false;
            }

            // Validate directory count sanity after blocks table and read first directory node for SerializedFile verification
            int dirOffset = 20 + blockCount * 10;
            if (dirOffset + 4 > uncompInfo.Length)
            {
                return false;
            }
            int dirCount = (int)ReadUInt32BE(uncompInfo, dirOffset);
            if (dirCount <= 0 || dirCount > 100_000)
            {
                return false;
            }

            bool hasFirstSerializedNode = false;
            long firstNodeSize = 0;
            if (dirOffset + 4 + 20 <= uncompInfo.Length)
            {
                long firstNodeOffset = ReadInt64BE(uncompInfo, dirOffset + 4);
                long candidateNodeSize = ReadInt64BE(uncompInfo, dirOffset + 12);
                uint firstNodeFlags = ReadUInt32BE(uncompInfo, dirOffset + 20);
                if ((firstNodeFlags & 4u) != 0 && firstNodeOffset == 0 && candidateNodeSize >= 48)
                {
                    hasFirstSerializedNode = true;
                    firstNodeSize = candidateNodeSize;
                }
            }

            // Verify total compressed block payload fits within stream and probe first, straddling, and last LZ4 block
            long cursor = layout.DataStartOffset;
            int firstIdx = 0;
            int straddleIdx = -1;
            int lastIdx = blockCount - 1;
            long firstOffset = 0, straddleOffset = 0, lastOffset = 0;
            uint firstComp = 0, firstUncomp = 0, firstFlags = 0;
            uint straddleComp = 0, straddleUncomp = 0, straddleFlags = 0;
            uint lastComp = 0, lastUncomp = 0, lastFlags = 0;

            for (int i = 0; i < blockCount; i++)
            {
                int entryPos = 20 + i * 10;
                uint bUncomp = ReadUInt32BE(uncompInfo, entryPos);
                uint bComp = ReadUInt32BE(uncompInfo, entryPos + 4);
                uint bFlags = ReadUInt16BE(uncompInfo, entryPos + 8);

                if (bComp == 0 || bUncomp == 0 || cursor + bComp > stream.Length)
                {
                    return false;
                }

                if (i == firstIdx)
                {
                    firstOffset = cursor; firstComp = bComp; firstUncomp = bUncomp; firstFlags = bFlags;
                }
                if (rangeLength > 0 && straddleIdx < 0 && cursor < rangeLength && cursor + bComp > rangeLength)
                {
                    straddleIdx = i;
                    straddleOffset = cursor; straddleComp = bComp; straddleUncomp = bUncomp; straddleFlags = bFlags;
                }
                if (i == lastIdx)
                {
                    lastOffset = cursor; lastComp = bComp; lastUncomp = bUncomp; lastFlags = bFlags;
                }

                cursor += bComp;
            }

            if (!ValidateSingleBlock(stream, xorKey, rangeLength, firstOffset, firstComp, firstUncomp, firstFlags, out byte[]? firstDecoded))
            {
                return false;
            }
            if (hasFirstSerializedNode && firstDecoded != null && firstDecoded.Length >= 48
                && !UnityHeaderProbe.IsSerializedFileHeader(firstDecoded, firstNodeSize))
            {
                return false;
            }
            if (straddleIdx >= 0 && straddleIdx != firstIdx && straddleIdx != lastIdx
                && !ValidateSingleBlock(stream, xorKey, rangeLength, straddleOffset, straddleComp, straddleUncomp, straddleFlags, out _))
            {
                return false;
            }
            if (lastIdx != firstIdx
                && !ValidateSingleBlock(stream, xorKey, rangeLength, lastOffset, lastComp, lastUncomp, lastFlags, out _))
            {
                return false;
            }

            return true;
        }

        private static bool ValidateSingleBlock(
            Stream stream,
            byte[] xorKey,
            long rangeLength,
            long blockOffset,
            uint compSize,
            uint uncompSize,
            uint blockFlags,
            out byte[]? decodedBlock)
        {
            decodedBlock = null;
            var compType = (CompressionType)(blockFlags & (uint)StorageBlockFlags.CompressionTypeMask);
            if (compType == CompressionType.None)
            {
                if (compSize != uncompSize) return false;
                int inspectLen = (int)Math.Min(compSize, 128);
                byte[] rawBuf = new byte[inspectLen];
                if (!ReadRangeDecoded(stream, blockOffset, rawBuf, xorKey, rangeLength))
                {
                    return false;
                }
                decodedBlock = rawBuf;
                return true;
            }

            if (compType != CompressionType.Lz4 && compType != CompressionType.Lz4HC)
            {
                return true;
            }

            if (compSize > 4 * 1024 * 1024 || uncompSize > 16 * 1024 * 1024)
            {
                return true;
            }

            byte[] compBuf = new byte[compSize];
            if (!ReadRangeDecoded(stream, blockOffset, compBuf, xorKey, rangeLength))
            {
                return false;
            }

            byte[] uncompBuf = new byte[uncompSize];
            try
            {
                if (LZ4Codec.Decode(compBuf, uncompBuf) != (int)uncompSize)
                {
                    return false;
                }
                decodedBlock = uncompBuf;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadRangeDecoded(
            Stream stream,
            long offset,
            byte[] destination,
            byte[] xorKey,
            long rangeLength)
        {
            stream.Position = offset;
            int read = ReadExactOrAvailable(stream, destination, 0, destination.Length);
            if (read != destination.Length)
            {
                return false;
            }

            long xorEnd = rangeLength < 0 ? stream.Length : Math.Min(stream.Length, rangeLength);
            long sliceEnd = Math.Min(offset + destination.Length, xorEnd);
            if (sliceEnd > offset)
            {
                int xorCount = (int)(sliceEnd - offset);
                UnityHeaderProbe.XorBytesRepeating(destination.AsSpan(0, xorCount), offset, xorKey);
            }

            return true;
        }

        private static int ReadExactOrAvailable(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static ushort ReadUInt16BE(ReadOnlySpan<byte> buf, int offset)
            => (ushort)((buf[offset] << 8) | buf[offset + 1]);

        private static uint ReadUInt32BE(ReadOnlySpan<byte> buf, int offset)
            => ((uint)buf[offset] << 24)
                | ((uint)buf[offset + 1] << 16)
                | ((uint)buf[offset + 2] << 8)
                | buf[offset + 3];

        private static long ReadInt64BE(ReadOnlySpan<byte> buf, int offset)
            => ((long)ReadUInt32BE(buf, offset) << 32) | ReadUInt32BE(buf, offset + 4);
    }
}
