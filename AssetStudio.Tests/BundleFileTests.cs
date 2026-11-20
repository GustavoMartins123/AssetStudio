using K4os.Compression.LZ4;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace AssetStudio.Tests
{
    public class BundleFileTests
    {
        private readonly record struct SyntheticBundleNode(string Path, byte[] Payload, uint Flags = 4);

        [Fact]
        public void BundleFile_UnityFSv6_Uncompressed_RoundTripsMultipleNodesAccurately()
        {
            var payloadA = CreateDeterministicPayload(512, seed: 11);
            var payloadB = CreateDeterministicPayload(1337, seed: 29);
            var payloadEmpty = Array.Empty<byte>();
            var nodes = new[]
            {
                new SyntheticBundleNode("CAB-test01/level0", payloadA),
                new SyntheticBundleNode("CAB-test01/sharedassets0.resS", payloadB),
                new SyntheticBundleNode("CAB-test01/empty.bin", payloadEmpty)
            };

            var bundleBytes = BuildUnityFsV6Bundle(
                nodes,
                blocksInfoCompression: CompressionType.None,
                blockCompression: CompressionType.None,
                blocksInfoAtEnd: false,
                splitBlockSize: 400);

            using var ms = new MemoryStream(bundleBytes);
            using var reader = new FileReader("synthetic_uncompressed.bundle", ms);
            Assert.Equal(FileType.BundleFile, reader.FileType);

            using var bundle = new BundleFile(reader);
            Assert.Equal("UnityFS", bundle.m_Header.signature);
            Assert.Equal(6u, bundle.m_Header.version);
            Assert.Equal("5.x.x", bundle.m_Header.unityVersion);
            Assert.Equal("2021.3.16f1", bundle.m_Header.unityRevision);
            Assert.Equal(3, bundle.fileList.Length);

            AssertStreamFile(bundle.fileList[0], "CAB-test01/level0", "level0", payloadA);
            AssertStreamFile(bundle.fileList[1], "CAB-test01/sharedassets0.resS", "sharedassets0.resS", payloadB);
            AssertStreamFile(bundle.fileList[2], "CAB-test01/empty.bin", "empty.bin", payloadEmpty);
        }

        [Theory]
        [InlineData(CompressionType.Lz4, CompressionType.Lz4, false)]
        [InlineData(CompressionType.Lz4HC, CompressionType.Lz4HC, true)]
        [InlineData(CompressionType.Lz4, CompressionType.None, true)]
        public void BundleFile_UnityFSv6_Lz4Compressed_RoundTripsMultipleBlocksAndNodesAccurately(
            CompressionType blocksInfoCompression,
            CompressionType blockCompression,
            bool blocksInfoAtEnd)
        {
            var payloadA = CreateCompressiblePayload(2048, seed: 3);
            var payloadB = CreateCompressiblePayload(3500, seed: 7);
            var payloadC = CreateDeterministicPayload(777, seed: 19);
            var nodes = new[]
            {
                new SyntheticBundleNode("archive/a/first.assets", payloadA),
                new SyntheticBundleNode("archive\\b\\second.resS", payloadB),
                new SyntheticBundleNode("third.dat", payloadC)
            };

            var bundleBytes = BuildUnityFsV6Bundle(
                nodes,
                blocksInfoCompression: blocksInfoCompression,
                blockCompression: blockCompression,
                blocksInfoAtEnd: blocksInfoAtEnd,
                splitBlockSize: 1500);

            using var ms = new MemoryStream(bundleBytes);
            using var reader = new FileReader("synthetic_lz4.bundle", ms);
            Assert.Equal(FileType.BundleFile, reader.FileType);

            using var bundle = new BundleFile(reader);
            Assert.Equal("UnityFS", bundle.m_Header.signature);
            Assert.Equal(6u, bundle.m_Header.version);
            Assert.Equal(3, bundle.fileList.Length);

            AssertStreamFile(bundle.fileList[0], "archive/a/first.assets", "first.assets", payloadA);
            AssertStreamFile(bundle.fileList[1], "archive\\b\\second.resS", "second.resS", payloadB);
            AssertStreamFile(bundle.fileList[2], "third.dat", "third.dat", payloadC);
        }

        [Fact]
        public void BundleFile_TruncatedCompressedBlock_ThrowsEndOfStreamInsteadOfReturningDirtyPoolMemory_RegressionSEC06()
        {
            // Pre-pollute BigArrayPool with recognizable sentinel bytes to ensure truncated reads fail
            // cleanly rather than silently decoding stale pooled memory.
            var poisoned = BigArrayPool<byte>.Shared.Rent(4096);
            Array.Fill(poisoned, (byte)0xDE);
            BigArrayPool<byte>.Shared.Return(poisoned);

            var payload = CreateCompressiblePayload(2048, seed: 41);
            var validBundle = BuildUnityFsV6Bundle(
                new[] { new SyntheticBundleNode("CAB-sec06/data.bin", payload) },
                blocksInfoCompression: CompressionType.Lz4,
                blockCompression: CompressionType.Lz4,
                blocksInfoAtEnd: false,
                splitBlockSize: 2048);

            // Truncate 12 bytes off the end of the compressed block payload
            var truncatedBundle = validBundle.AsSpan(0, validBundle.Length - 12).ToArray();

            using var ms = new MemoryStream(truncatedBundle);
            using var reader = new FileReader("truncated_lz4.bundle", ms);
            Assert.Throws<EndOfStreamException>(() => new BundleFile(reader));
        }

        [Fact]
        public void BundleFile_CorruptedLz4BlockPayload_ThrowsIOException_RegressionSEC06()
        {
            var payload = CreateCompressiblePayload(1024, seed: 53);
            var bundleBytes = BuildUnityFsV6Bundle(
                new[] { new SyntheticBundleNode("CAB-corrupt/data.bin", payload) },
                blocksInfoCompression: CompressionType.None,
                blockCompression: CompressionType.Lz4,
                blocksInfoAtEnd: false,
                splitBlockSize: 1024);

            // Overwrite the compressed block bytes at the end with invalid LZ4 tokens
            for (int i = bundleBytes.Length - 16; i < bundleBytes.Length; i++)
            {
                bundleBytes[i] = 0xFF;
            }

            using var ms = new MemoryStream(bundleBytes);
            using var reader = new FileReader("corrupt_lz4.bundle", ms);
            Assert.Throws<IOException>(() => new BundleFile(reader));
        }

        [Theory]
        [InlineData(ArchiveFlags.BlocksAndDirectoryInfoCombined, 999999u, 64u)]
        [InlineData(ArchiveFlags.BlocksAndDirectoryInfoCombined | ArchiveFlags.BlocksInfoAtTheEnd, 999999u, 64u)]
        [InlineData(ArchiveFlags.BlocksAndDirectoryInfoCombined | ArchiveFlags.BlocksInfoAtTheEnd, 0x80000001u, 64u)]
        [InlineData(ArchiveFlags.BlocksAndDirectoryInfoCombined, 32u, 0x80000001u)]
        public void BundleFile_MalformedBlocksInfoSize_IsRejectedCleanlyWithoutNegativeSeek_RegressionSEC06(
            ArchiveFlags flags,
            uint compressedBlocksInfoSize,
            uint uncompressedBlocksInfoSize)
        {
            var malformed = BuildUnityFsV6RawHeader(
                declaredTotalSize: 256,
                compressedBlocksInfoSize: compressedBlocksInfoSize,
                uncompressedBlocksInfoSize: uncompressedBlocksInfoSize,
                flags: flags,
                trailingBytes: new byte[64]);

            using var ms = new MemoryStream(malformed);
            using var reader = new FileReader("malformed_blocks_info_size.bundle", ms);
            Assert.Throws<InvalidDataException>(() => new BundleFile(reader));
        }

        [Fact]
        public void BundleFile_InlineCompressedBlocksInfoExceedingRemainingStream_ThrowsEndOfStream_RegressionSEC06()
        {
            // compressedBlocksInfoSize <= stream.Length, but > (stream.Length - headerEndPosition)
            var trailing = new byte[16];
            var malformed = BuildUnityFsV6RawHeader(
                declaredTotalSize: 64,
                compressedBlocksInfoSize: 40u,
                uncompressedBlocksInfoSize: 40u,
                flags: ArchiveFlags.BlocksAndDirectoryInfoCombined,
                trailingBytes: trailing);
            Assert.True(malformed.Length >= 40);

            using var ms = new MemoryStream(malformed);
            using var reader = new FileReader("truncated_inline_blocks_info.bundle", ms);
            Assert.Throws<EndOfStreamException>(() => new BundleFile(reader));
        }

        [Theory]
        [InlineData(0L, 129L)]          // size > blocksStream.Length (128)
        [InlineData(64L, 65L)]          // offset + size > blocksStream.Length
        [InlineData(129L, 0L)]          // offset > blocksStream.Length
        [InlineData(-1L, 16L)]          // negative offset
        [InlineData(0L, -16L)]          // negative size
        [InlineData(long.MaxValue, 1L)] // int64 overflow guard
        public void BundleFile_OutOfBoundsNodeOffsetOrSize_ThrowsInvalidDataException_RegressionSEC06(
            long maliciousOffset,
            long maliciousSize)
        {
            var blockData = CreateDeterministicPayload(128, seed: 71);
            var blocksInfo = BuildBlocksInfoBytes(
                new[] { (uncompressedSize: (uint)blockData.Length, compressedSize: (uint)blockData.Length, flags: (StorageBlockFlags)CompressionType.None) },
                new[] { (offset: maliciousOffset, size: maliciousSize, flags: 4u, path: "CAB-oob/evil.bin") });

            var bundleBytes = BuildUnityFsV6RawHeader(
                declaredTotalSize: 0,
                compressedBlocksInfoSize: (uint)blocksInfo.Length,
                uncompressedBlocksInfoSize: (uint)blocksInfo.Length,
                flags: ArchiveFlags.BlocksAndDirectoryInfoCombined,
                trailingBytes: Concat(blocksInfo, blockData));

            using var ms = new MemoryStream(bundleBytes);
            using var reader = new FileReader("oob_node.bundle", ms);
            Assert.Throws<InvalidDataException>(() => new BundleFile(reader));
        }

        [Fact]
        public void BundleFile_InvalidBlocksOrDirectoryCounts_ThrowsInvalidDataException_RegressionSEC06()
        {
            // Negative blocksInfoCount
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[16]); // hash
                WriteInt32BE(ms, -1);   // invalid blocksInfoCount
                var blocksInfo = ms.ToArray();
                var bundleBytes = BuildUnityFsV6RawHeader(
                    0,
                    (uint)blocksInfo.Length,
                    (uint)blocksInfo.Length,
                    ArchiveFlags.BlocksAndDirectoryInfoCombined,
                    blocksInfo);

                using var bundleMs = new MemoryStream(bundleBytes);
                using var reader = new FileReader("neg_blocks_count.bundle", bundleMs);
                Assert.Throws<InvalidDataException>(() => new BundleFile(reader));
            }

            // Huge nodesCount exceeding remaining blocksInfo stream
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[16]); // hash
                WriteInt32BE(ms, 0);    // 0 storage blocks
                WriteInt32BE(ms, 1000); // impossible nodesCount
                var blocksInfo = ms.ToArray();
                var bundleBytes = BuildUnityFsV6RawHeader(
                    0,
                    (uint)blocksInfo.Length,
                    (uint)blocksInfo.Length,
                    ArchiveFlags.BlocksAndDirectoryInfoCombined,
                    blocksInfo);

                using var bundleMs = new MemoryStream(bundleBytes);
                using var reader = new FileReader("huge_nodes_count.bundle", bundleMs);
                Assert.Throws<InvalidDataException>(() => new BundleFile(reader));
            }
        }

        [Fact]
        public void WebFile_ValidUnityWebData_RoundTripsTwoEmbeddedFilesAccurately()
        {
            var file1Bytes = CreateDeterministicPayload(256, seed: 5);
            var file2Bytes = CreateDeterministicPayload(1024, seed: 13);

            var webBytes = BuildWebFile(
                ("Build/level0", file1Bytes),
                ("Resources/sharedassets0.resS", file2Bytes));

            using var ms = new MemoryStream(webBytes);
            using var reader = new FileReader("game.data", ms);
            Assert.Equal(FileType.WebFile, reader.FileType);

            var webFile = new WebFile(reader);
            Assert.Equal(2, webFile.fileList.Length);

            AssertStreamFile(webFile.fileList[0], "Build/level0", "level0", file1Bytes);
            AssertStreamFile(webFile.fileList[1], "Resources/sharedassets0.resS", "sharedassets0.resS", file2Bytes);
        }

        [Fact]
        public void WebFile_MalformedHeaderAndEntryBounds_ThrowsInvalidDataException_RegressionSEC06()
        {
            // 1) Negative headLength
            AssertWebFileRejects(BuildMalformedWebFile(headLength: -1, entries: Array.Empty<(int, int, int, byte[])>(), payload: Array.Empty<byte>()));

            // 2) headLength > stream.Length
            AssertWebFileRejects(BuildMalformedWebFile(headLength: 4096, entries: Array.Empty<(int, int, int, byte[])>(), payload: new byte[16]));

            // 3) Negative pathLength
            AssertWebFileRejects(BuildMalformedWebFile(
                headLength: 0, // computed to cover entries
                entries: new[] { (dataOffset: 40, dataLength: 4, pathLength: -5, pathBytes: Array.Empty<byte>()) },
                payload: new byte[16]));

            // 4) pathLength > remaining stream length
            AssertWebFileRejects(BuildMalformedWebFile(
                headLength: 0,
                entries: new[] { (dataOffset: 40, dataLength: 4, pathLength: 9999, pathBytes: Encoding.UTF8.GetBytes("ab")) },
                payload: new byte[16]));

            // 5) Negative dataOffset
            AssertWebFileRejects(BuildMalformedWebFile(
                headLength: 0,
                entries: new[] { (dataOffset: -1, dataLength: 4, pathLength: 4, pathBytes: Encoding.UTF8.GetBytes("a.bn")) },
                payload: new byte[16]));

            // 6) Negative dataLength
            AssertWebFileRejects(BuildMalformedWebFile(
                headLength: 0,
                entries: new[] { (dataOffset: 32, dataLength: -4, pathLength: 4, pathBytes: Encoding.UTF8.GetBytes("a.bn")) },
                payload: new byte[16]));

            // 7) dataOffset + dataLength > stream.Length
            AssertWebFileRejects(BuildMalformedWebFile(
                headLength: 0,
                entries: new[] { (dataOffset: 32, dataLength: 500, pathLength: 4, pathBytes: Encoding.UTF8.GetBytes("a.bn")) },
                payload: new byte[16]));

            // 8) Int32 overflow in dataOffset + dataLength
            AssertWebFileRejects(BuildMalformedWebFile(
                headLength: 0,
                entries: new[] { (dataOffset: int.MaxValue, dataLength: 16, pathLength: 4, pathBytes: Encoding.UTF8.GetBytes("a.bn")) },
                payload: new byte[16]));
        }

        private static void AssertWebFileRejects(byte[] rawBytes)
        {
            using var ms = new MemoryStream(rawBytes);
            using var reader = new FileReader("malformed.data", ms);
            Assert.Throws<InvalidDataException>(() => new WebFile(reader));
        }

        private static void AssertStreamFile(StreamFile file, string expectedPath, string expectedFileName, byte[] expectedPayload)
        {
            Assert.Equal(expectedPath, file.path);
            Assert.Equal(expectedFileName, file.fileName);
            Assert.NotNull(file.stream);
            Assert.Equal(expectedPayload.Length, file.stream.Length);

            file.stream.Position = 0;
            var actual = new byte[expectedPayload.Length];
            int total = 0;
            while (total < actual.Length)
            {
                int read = file.stream.Read(actual, total, actual.Length - total);
                Assert.True(read > 0);
                total += read;
            }
            Assert.Equal(expectedPayload, actual);
        }

        private static byte[] BuildUnityFsV6Bundle(
            SyntheticBundleNode[] nodes,
            CompressionType blocksInfoCompression,
            CompressionType blockCompression,
            bool blocksInfoAtEnd,
            int splitBlockSize)
        {
            // Concatenate node payloads and record directory offsets
            var combinedPayload = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Length);
            foreach (var node in nodes)
            {
                long offset = combinedPayload.Position;
                combinedPayload.Write(node.Payload, 0, node.Payload.Length);
                dirEntries.Add((offset, node.Payload.Length, node.Flags, node.Path));
            }
            var rawBlocks = combinedPayload.ToArray();

            // Split into storage blocks and compress each block
            var blockInfos = new List<(uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)>();
            var compressedBlocksStream = new MemoryStream();
            int cursor = 0;
            while (cursor < rawBlocks.Length || blockInfos.Count == 0)
            {
                int chunkLen = Math.Min(splitBlockSize, rawBlocks.Length - cursor);
                var slice = rawBlocks.AsSpan(cursor, chunkLen).ToArray();
                byte[] stored = CompressBlock(slice, blockCompression);
                compressedBlocksStream.Write(stored, 0, stored.Length);
                blockInfos.Add(((uint)chunkLen, (uint)stored.Length, (StorageBlockFlags)blockCompression));
                cursor += chunkLen;
                if (rawBlocks.Length == 0)
                {
                    break;
                }
            }
            var compressedBlocksData = compressedBlocksStream.ToArray();

            // Build & optionally compress blocksInfo + directory table
            var uncompressedBlocksInfo = BuildBlocksInfoBytes(blockInfos, dirEntries);
            var compressedBlocksInfo = CompressBlock(uncompressedBlocksInfo, blocksInfoCompression);

            ArchiveFlags flags = (ArchiveFlags)blocksInfoCompression | ArchiveFlags.BlocksAndDirectoryInfoCombined;
            if (blocksInfoAtEnd)
            {
                flags |= ArchiveFlags.BlocksInfoAtTheEnd;
            }

            byte[] body = blocksInfoAtEnd
                ? Concat(compressedBlocksData, compressedBlocksInfo)
                : Concat(compressedBlocksInfo, compressedBlocksData);

            // Build final header with accurate total size
            var probe = BuildUnityFsV6RawHeader(
                declaredTotalSize: 0,
                compressedBlocksInfoSize: (uint)compressedBlocksInfo.Length,
                uncompressedBlocksInfoSize: (uint)uncompressedBlocksInfo.Length,
                flags: flags,
                trailingBytes: body);

            return BuildUnityFsV6RawHeader(
                declaredTotalSize: probe.Length,
                compressedBlocksInfoSize: (uint)compressedBlocksInfo.Length,
                uncompressedBlocksInfoSize: (uint)uncompressedBlocksInfo.Length,
                flags: flags,
                trailingBytes: body);
        }

        private static byte[] CompressBlock(byte[] input, CompressionType compressionType)
        {
            if (compressionType == CompressionType.None || input.Length == 0)
            {
                return input;
            }

            LZ4Level level = compressionType == CompressionType.Lz4HC
                ? LZ4Level.L09_HC
                : LZ4Level.L00_FAST;
            var dst = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
            int encoded = LZ4Codec.Encode(input, 0, input.Length, dst, 0, dst.Length, level);
            Assert.True(encoded > 0);
            return dst.AsSpan(0, encoded).ToArray();
        }

        private static byte[] BuildBlocksInfoBytes(
            IReadOnlyList<(uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)> blocks,
            IReadOnlyList<(long offset, long size, uint flags, string path)> nodes)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[16]); // 16-byte uncompressedDataHash
            WriteInt32BE(ms, blocks.Count);
            foreach (var b in blocks)
            {
                WriteUInt32BE(ms, b.uncompressedSize);
                WriteUInt32BE(ms, b.compressedSize);
                WriteUInt16BE(ms, (ushort)b.flags);
            }
            WriteInt32BE(ms, nodes.Count);
            foreach (var n in nodes)
            {
                WriteInt64BE(ms, n.offset);
                WriteInt64BE(ms, n.size);
                WriteUInt32BE(ms, n.flags);
                WriteNullTerminatedUtf8(ms, n.path);
            }
            return ms.ToArray();
        }

        private static byte[] BuildUnityFsV6RawHeader(
            long declaredTotalSize,
            uint compressedBlocksInfoSize,
            uint uncompressedBlocksInfoSize,
            ArchiveFlags flags,
            byte[] trailingBytes)
        {
            using var ms = new MemoryStream();
            WriteNullTerminatedUtf8(ms, "UnityFS");
            WriteUInt32BE(ms, 6u);
            WriteNullTerminatedUtf8(ms, "5.x.x");
            WriteNullTerminatedUtf8(ms, "2021.3.16f1");
            WriteInt64BE(ms, declaredTotalSize);
            WriteUInt32BE(ms, compressedBlocksInfoSize);
            WriteUInt32BE(ms, uncompressedBlocksInfoSize);
            WriteUInt32BE(ms, (uint)flags);
            ms.Write(trailingBytes, 0, trailingBytes.Length);
            return ms.ToArray();
        }

        private static byte[] BuildWebFile(params (string path, byte[] data)[] files)
        {
            var sigBytes = Encoding.ASCII.GetBytes("UnityWebData1.0\0");
            int headerSize = sigBytes.Length + sizeof(int);
            foreach (var f in files)
            {
                headerSize += 12 + Encoding.UTF8.GetByteCount(f.path);
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(sigBytes);
            bw.Write(headerSize);

            int currentOffset = headerSize;
            foreach (var f in files)
            {
                var pathBytes = Encoding.UTF8.GetBytes(f.path);
                bw.Write(currentOffset);
                bw.Write(f.data.Length);
                bw.Write(pathBytes.Length);
                bw.Write(pathBytes);
                currentOffset += f.data.Length;
            }

            foreach (var f in files)
            {
                bw.Write(f.data);
            }

            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildMalformedWebFile(
            int headLength,
            IReadOnlyList<(int dataOffset, int dataLength, int pathLength, byte[] pathBytes)> entries,
            byte[] payload)
        {
            var sigBytes = Encoding.ASCII.GetBytes("UnityWebData1.0\0");
            using var entriesMs = new MemoryStream();
            using (var ebw = new BinaryWriter(entriesMs, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var entry in entries)
                {
                    ebw.Write(entry.dataOffset);
                    ebw.Write(entry.dataLength);
                    ebw.Write(entry.pathLength);
                    ebw.Write(entry.pathBytes);
                }
            }
            var entriesBytes = entriesMs.ToArray();
            int actualHeadLength = headLength == 0
                ? sigBytes.Length + sizeof(int) + entriesBytes.Length
                : headLength;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(sigBytes);
            bw.Write(actualHeadLength);
            bw.Write(entriesBytes);
            bw.Write(payload);
            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] CreateDeterministicPayload(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 37 + seed * 13 + 7) & 0xFF);
            }
            return data;
        }

        private static byte[] CreateCompressiblePayload(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(((i / 16) * 19 + seed + (i % 4)) & 0xFF);
            }
            return data;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var combined = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combined, 0, a.Length);
            Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
            return combined;
        }

        private static void WriteNullTerminatedUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt16BE(Stream stream, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt32BE(Stream stream, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, value);
            stream.Write(buf);
        }
    }
}
