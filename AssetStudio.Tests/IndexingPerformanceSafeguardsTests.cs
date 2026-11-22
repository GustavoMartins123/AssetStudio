using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using Xunit;

namespace AssetStudio.Tests
{
    public class IndexingPerformanceSafeguardsTests
    {
        private readonly record struct SyntheticBundleNode(string Path, byte[] Payload, uint Flags);

        // =========================================================================
        // 1. DecompressedBundleCache: O(1) in-memory accounting (no per-write disk crawl)
        //    + Lazy indexing bypasses persistent disk decompression cache
        // =========================================================================

        [Fact]
        public void DecompressedBundleCache_RepeatedWritesUnderAndOverBudget_AvoidPerWriteDiskCrawlWhileEnforcingLru()
        {
            var tempRoot = CreateTempDirectory("cache_o1_accounting");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                // Each bundle is 4,096 bytes uncompressed (~4.5 KB on disk with manifest + .ready).
                // Budget of 50,000 bytes fits ~10 bundles before LRU eviction activates.
                var cache = new DecompressedBundleCache(cacheDir, maxTotalBytes: 50_000, gameScopePath: gameDir);

                for (int i = 0; i < 20; i++)
                {
                    var bundlePath = Path.Combine(gameDir, $"bundle_{i:D2}.bundle");
                    var payload = CreateCompressiblePayload(4096, seed: i + 1);
                    File.WriteAllBytes(bundlePath, BuildUnityFsV6Bundle(
                        new[] { new SyntheticBundleNode($"CAB-{i:D2}/data.bin", payload, Flags: 4) },
                        splitBlockSize: 2048));

                    using var reader = new FileReader(bundlePath);
                    using var bundle = new BundleFile(reader, cache);
                    Assert.False(bundle.LoadedFromDecompressionCache);
                }

                // Disk enumeration must have happened at most ONCE (initial index bootstrap), NOT 20 times!
                Assert.Equal(1L, cache.DiskEnumerationCount);
                Assert.True(cache.EvictionCount >= 8);

                var stats = cache.GetStats();
                Assert.True(stats.TotalBytes <= 50_000);
                Assert.True(stats.EntryCount <= 11);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task AssetsManager_LazyIndexingPass_BypassesDiskDecompressionCache_WhilePreviewLoadStillUsesCache()
        {
            var tempRoot = CreateTempDirectory("lazy_bypass_cache");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var sfBytes = BuildMinimalSerializedFileBytes("2022.3.40f1");
                var resSBytes = CreateCompressiblePayload(64 * 1024, seed: 77);
                var bundlePath = Path.Combine(gameDir, "character_bundle.bundle");
                File.WriteAllBytes(bundlePath, BuildUnityFsV6Bundle(
                    new[]
                    {
                        new SyntheticBundleNode("CAB-char01", sfBytes, Flags: 4),
                        new SyntheticBundleNode("CAB-char01.resS", resSBytes, Flags: 0)
                    },
                    splitBlockSize: 4096));

                var cache = new DecompressedBundleCache(cacheDir, maxTotalBytes: 64L * 1024 * 1024, gameScopePath: gameDir);
                using var manager = new AssetsManager
                {
                    LazyLoading = true,
                    DecompressionCache = cache
                };

                // 1) Bulk lazy indexing pass must NOT populate disk DecompressedBundleCache!
                await manager.IndexSingleFileInWorkerAsync(bundlePath);
                Assert.Equal(0L, cache.CacheMisses);
                Assert.Equal(0L, cache.CacheHits);
                Assert.Equal(0, cache.GetStats().EntryCount);

                // 2) On-demand preview load (LoadFilesForPreview -> IsolateStreams = false) DOES use DecompressionCache!
                manager.LoadFilesForPreview(bundlePath);
                Assert.Equal(1L, cache.CacheMisses);
                Assert.Equal(1, cache.GetStats().EntryCount);

                // 3) Second preview load hits disk cache!
                manager.Clear();
                manager.LazyLoading = true;
                manager.DecompressionCache = cache;
                manager.LoadFilesForPreview(bundlePath);
                Assert.Equal(1L, cache.CacheMisses);
                Assert.Equal(1L, cache.CacheHits);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 2. SubStream: Buffered sequential reads collapse thousands of tiny syscalls
        // =========================================================================

        [Fact]
        public void SubStream_BufferedSequentialAndNearbySeekReads_CollapseSyscallsAndMatchExactBytes()
        {
            var tempRoot = CreateTempDirectory("substream_buffered");
            try
            {
                var filePath = Path.Combine(tempRoot, "backing.bin");
                const int prefix = 123;
                const int sliceLen = 96 * 1024;
                var fullData = new byte[prefix + sliceLen + 77];
                new Random(42).NextBytes(fullData);
                File.WriteAllBytes(filePath, fullData);
                var expected = fullData.AsSpan(prefix, sliceLen).ToArray();

                using var sub = new SubStream(filePath, prefix, sliceLen);

                // 1) Read first 16 KB one byte at a time -> must take 1 underlying RandomAccess.Read (not 16,384!)
                for (int i = 0; i < 16_384; i++)
                {
                    int b = sub.ReadByte();
                    Assert.Equal(expected[i], b);
                }
                Assert.Equal(1L, sub.UnderlyingReadCount);

                // 2) Seek backward within the same 32 KB buffered window -> 0 additional syscalls!
                sub.Position = 100;
                var smallBuf = new byte[256];
                Assert.Equal(256, sub.Read(smallBuf, 0, smallBuf.Length));
                Assert.Equal(expected.AsSpan(100, 256).ToArray(), smallBuf);
                Assert.Equal(1L, sub.UnderlyingReadCount);

                // 3) Sequential 4-byte reads across entire 96 KB slice -> ~3-4 syscalls total (not 24,576!)
                sub.Position = 0;
                var rebuilt = new byte[sliceLen];
                for (int pos = 0; pos < sliceLen; pos += 4)
                {
                    int read = sub.Read(rebuilt, pos, 4);
                    Assert.Equal(4, read);
                }
                Assert.Equal(expected, rebuilt);
                Assert.True(sub.UnderlyingReadCount <= 5, $"Expected <= 5 underlying reads, observed {sub.UnderlyingReadCount}");

                // 4) Large direct read (>= 32 KB) bypasses buffer and returns exact bytes
                sub.Position = 8192;
                var largeBuf = new byte[48 * 1024];
                Assert.Equal(largeBuf.Length, sub.Read(largeBuf, 0, largeBuf.Length));
                Assert.Equal(expected.AsSpan(8192, largeBuf.Length).ToArray(), largeBuf);

                // 5) Positional ReadAt does not mutate Position and matches exact bytes
                long savedPos = sub.Position;
                var probe = new byte[512];
                Assert.Equal(512, sub.ReadAt(50_000, probe, 0, 512));
                Assert.Equal(expected.AsSpan(50_000, 512).ToArray(), probe);
                Assert.Equal(savedPos, sub.Position);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 3. BundleFile: CAB-only partial block decompression skips .resS blocks
        // =========================================================================

        [Fact]
        public void BundleFile_SerializedFilesOnly_SkipsResSBlocksAndMatchesFullDecompressionByteForByte()
        {
            var cab1 = CreateDeterministicPayload(5_100, seed: 11);  // Spans blocks 0..1 (4 KB blocks)
            var resS1 = CreateCompressiblePayload(80 * 1024, seed: 22); // ~20 blocks of streaming data
            var cab2 = CreateDeterministicPayload(6_500, seed: 33);  // Starts mid-block after resS1, spans 2-3 blocks
            var resS2 = CreateCompressiblePayload(120 * 1024, seed: 44); // ~30 blocks of streaming data

            var nodes = new[]
            {
                new SyntheticBundleNode("CAB-scene01", cab1, Flags: 4),
                new SyntheticBundleNode("CAB-scene01.resS", resS1, Flags: 0),
                new SyntheticBundleNode("CAB-scene02", cab2, Flags: 4),
                new SyntheticBundleNode("CAB-scene02.resS", resS2, Flags: 0)
            };

            var bundleBytes = BuildUnityFsV6Bundle(nodes, splitBlockSize: 4096);

            // Full baseline decompression
            byte[] fullCab1;
            byte[] fullCab2;
            int fullBlockCount;
            using (var msFull = new MemoryStream(bundleBytes))
            using (var readerFull = new FileReader("multi_cab.bundle", msFull))
            using (var bundleFull = new BundleFile(readerFull))
            {
                Assert.False(bundleFull.UsedPartialDecompression);
                Assert.Equal(4, bundleFull.fileList.Length);
                fullBlockCount = bundleFull.DecompressedBlockCount;
                Assert.True(fullBlockCount >= 50);
                fullCab1 = ReadAllBytes(bundleFull.fileList[0].stream);
                fullCab2 = ReadAllBytes(bundleFull.fileList[2].stream);
            }

            // Partial CAB-only decompression
            using (var msPartial = new MemoryStream(bundleBytes))
            using (var readerPartial = new FileReader("multi_cab.bundle", msPartial))
            using (var bundlePartial = new BundleFile(
                readerPartial,
                decompressionCache: null,
                allowParallelDecompression: true,
                lowMemoryThresholdOverride: 32 * 1024, // Low threshold would spill full 211 KB bundle to temp disk, but CAB-only is only 11.6 KB!
                serializedFilesOnly: true))
            {
                Assert.True(bundlePartial.UsedPartialDecompression);
                Assert.Null(bundlePartial.BlocksStream); // Stayed purely in memory!
                Assert.True(bundlePartial.DecompressedBlockCount <= 6, $"Expected <= 6 blocks decompressed, got {bundlePartial.DecompressedBlockCount} of {fullBlockCount}");
                Assert.Equal(2, bundlePartial.fileList.Length);
                Assert.Equal("CAB-scene01", bundlePartial.fileList[0].path);
                Assert.Equal("CAB-scene02", bundlePartial.fileList[1].path);
                Assert.Equal(fullCab1, ReadAllBytes(bundlePartial.fileList[0].stream));
                Assert.Equal(fullCab2, ReadAllBytes(bundlePartial.fileList[1].stream));
            }
        }

        [Fact]
        public void BundleFile_SerializedFilesOnly_FlaglessBundleStillSkipsResSByExtensionAndFallsBackCleanlyWhenAllBlocksNeeded()
        {
            // (a) Flag 0 on all nodes, but second node is .resS -> extension fallback skips .resS blocks
            var cab = CreateDeterministicPayload(3_000, seed: 5);
            var resS = CreateCompressiblePayload(64 * 1024, seed: 6);
            var flaglessBytes = BuildUnityFsV6Bundle(
                new[]
                {
                    new SyntheticBundleNode("CAB-flagless", cab, Flags: 0),
                    new SyntheticBundleNode("CAB-flagless.resS", resS, Flags: 0)
                },
                splitBlockSize: 4096);

            using (var ms = new MemoryStream(flaglessBytes))
            using (var reader = new FileReader("flagless.bundle", ms))
            using (var bundle = new BundleFile(reader, null, allowParallelDecompression: true, serializedFilesOnly: true))
            {
                Assert.True(bundle.UsedPartialDecompression);
                Assert.Equal(1, bundle.DecompressedBlockCount);
                Assert.Single(bundle.fileList);
                Assert.Equal(cab, ReadAllBytes(bundle.fileList[0].stream));
            }

            // (b) Single CAB node only -> falls back cleanly to normal decompression (nothing to skip)
            var cabOnlyBytes = BuildUnityFsV6Bundle(
                new[] { new SyntheticBundleNode("CAB-only", cab, Flags: 4) },
                splitBlockSize: 4096);

            using (var ms = new MemoryStream(cabOnlyBytes))
            using (var reader = new FileReader("cab_only.bundle", ms))
            using (var bundle = new BundleFile(reader, null, allowParallelDecompression: true, serializedFilesOnly: true))
            {
                Assert.False(bundle.UsedPartialDecompression);
                Assert.Single(bundle.fileList);
                Assert.Equal(cab, ReadAllBytes(bundle.fileList[0].stream));
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static byte[] ReadAllBytes(Stream stream)
        {
            stream.Position = 0;
            var data = new byte[stream.Length];
            int total = 0;
            while (total < data.Length)
            {
                int read = stream.Read(data, total, data.Length - total);
                Assert.True(read > 0);
                total += read;
            }
            return data;
        }

        private static byte[] CreateDeterministicPayload(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static byte[] CreateCompressiblePayload(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(((i / 16) + seed) & 0xFF);
            }
            return data;
        }

        private static byte[] BuildMinimalSerializedFileBytes(string unityVersion)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes(unityVersion));
                metaBw.Write((byte)0);
                metaBw.Write((int)BuildTarget.StandaloneWindows64);
                metaBw.Write(false);
                metaBw.Write(0);
                metaBw.Write(0);
                metaBw.Write(0);
                metaBw.Write(0);
                metaBw.Write((byte)0);
                metaBw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            uint totalSize = (uint)(headerSize + metadata.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), totalSize);
            fullBytes[16] = 0;
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            return fullBytes;
        }

        private static byte[] BuildUnityFsV6Bundle(SyntheticBundleNode[] nodes, int splitBlockSize)
        {
            using var combined = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Length);
            foreach (var node in nodes)
            {
                long offset = combined.Position;
                combined.Write(node.Payload, 0, node.Payload.Length);
                dirEntries.Add((offset, node.Payload.Length, node.Flags, node.Path));
            }
            var raw = combined.ToArray();

            var blocks = new List<(uint uncomp, uint comp, StorageBlockFlags flags)>();
            using var compStream = new MemoryStream();
            int cursor = 0;
            while (cursor < raw.Length || blocks.Count == 0)
            {
                int chunkLen = Math.Min(splitBlockSize, raw.Length - cursor);
                var slice = raw.AsSpan(cursor, chunkLen);
                var dest = new byte[LZ4Codec.MaximumOutputSize(chunkLen)];
                int encoded = LZ4Codec.Encode(slice, dest, LZ4Level.L09_HC);
                if (encoded > 0 && encoded < chunkLen)
                {
                    compStream.Write(dest, 0, encoded);
                    blocks.Add(((uint)chunkLen, (uint)encoded, (StorageBlockFlags)CompressionType.Lz4HC));
                }
                else
                {
                    compStream.Write(slice);
                    blocks.Add(((uint)chunkLen, (uint)chunkLen, (StorageBlockFlags)CompressionType.None));
                }
                cursor += chunkLen;
            }
            var compData = compStream.ToArray();

            byte[] blocksInfoUncomp;
            using (var biMs = new MemoryStream())
            {
                biMs.Write(new byte[16]);
                WriteInt32BE(biMs, blocks.Count);
                foreach (var b in blocks)
                {
                    WriteUInt32BE(biMs, b.uncomp);
                    WriteUInt32BE(biMs, b.comp);
                    WriteUInt16BE(biMs, (ushort)b.flags);
                }
                WriteInt32BE(biMs, dirEntries.Count);
                foreach (var d in dirEntries)
                {
                    WriteInt64BE(biMs, d.offset);
                    WriteInt64BE(biMs, d.size);
                    WriteUInt32BE(biMs, d.flags);
                    biMs.Write(Encoding.UTF8.GetBytes(d.path));
                    biMs.WriteByte(0);
                }
                blocksInfoUncomp = biMs.ToArray();
            }

            var biCompBuf = new byte[LZ4Codec.MaximumOutputSize(blocksInfoUncomp.Length)];
            int biCompLen = LZ4Codec.Encode(blocksInfoUncomp, biCompBuf, LZ4Level.L09_HC);
            byte[] biStored;
            ArchiveFlags flags;
            if (biCompLen > 0 && biCompLen < blocksInfoUncomp.Length)
            {
                biStored = biCompBuf.AsSpan(0, biCompLen).ToArray();
                flags = ArchiveFlags.BlocksAndDirectoryInfoCombined | (ArchiveFlags)CompressionType.Lz4HC;
            }
            else
            {
                biStored = blocksInfoUncomp;
                flags = ArchiveFlags.BlocksAndDirectoryInfoCombined;
            }

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            WriteUInt32BE(ms, 6u);
            ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            ms.Write(Encoding.ASCII.GetBytes("2022.3.40f1\0"));
            var sizePos = ms.Position;
            WriteInt64BE(ms, 0L);
            WriteUInt32BE(ms, (uint)biStored.Length);
            WriteUInt32BE(ms, (uint)blocksInfoUncomp.Length);
            WriteUInt32BE(ms, (uint)flags);
            ms.Write(biStored);
            ms.Write(compData);

            var result = ms.ToArray();
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizePos, 8), result.Length);
            return result;
        }

        private static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, v);
            s.Write(buf);
        }

        private static void WriteInt32BE(Stream s, int v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, v);
            s.Write(buf);
        }

        private static void WriteUInt32BE(Stream s, uint v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, v);
            s.Write(buf);
        }

        private static void WriteInt64BE(Stream s, long v)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, v);
            s.Write(buf);
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_PerfSafeguards_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
