#nullable enable
using K4os.Compression.LZ4;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class BundleParallelDecompressionTests
    {
        private readonly ITestOutputHelper output;

        public BundleParallelDecompressionTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private readonly record struct SyntheticBundleNode(string Path, byte[] Payload, uint Flags = 4);

        private sealed class NonSeekableWrapperStream : Stream
        {
            private readonly Stream inner;

            public NonSeekableWrapperStream(Stream inner)
            {
                this.inner = inner;
            }

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => inner.Read(buffer);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Theory]
        [InlineData(16, false)]
        [InlineData(32, true)]
        [InlineData(64, false)]
        public void ParallelDecompression_SyntheticMixedBlocks_MemoryAndFileSources_MatchesSequentialByteForByte(
            int blockCount,
            bool blocksInfoAtEnd)
        {
            const int blockSize = 4096;
            int totalSize = blockCount * blockSize;

            var payloadA = CreateCompressiblePayload(totalSize / 3 + 137, seed: 11);
            var payloadB = CreateDeterministicPayload(totalSize / 3 + 251, seed: 29);
            var payloadC = CreateCompressiblePayload(totalSize - payloadA.Length - payloadB.Length, seed: 47);

            var nodes = new[]
            {
                new SyntheticBundleNode("CAB-parallel/level0", payloadA),
                new SyntheticBundleNode("CAB-parallel/sharedassets0.assets", payloadB),
                new SyntheticBundleNode("CAB-parallel/sharedassets0.resS", payloadC)
            };

            var compressionPattern = new[]
            {
                CompressionType.Lz4,
                CompressionType.Lz4HC,
                CompressionType.None,
                CompressionType.Lz4
            };

            var bundleBytes = BuildUnityFsV6BundleMixedBlocks(
                nodes,
                blocksInfoCompression: CompressionType.Lz4HC,
                blockCompressions: compressionPattern,
                blocksInfoAtEnd: blocksInfoAtEnd,
                splitBlockSize: blockSize,
                actualBlockCount: out int actualBlocks);
            Assert.Equal(blockCount, actualBlocks);

            // Baseline: sequential decompression
            byte[][] sequentialPayloads;
            using (var seqMs = new MemoryStream(bundleBytes))
            using (var seqReader = new FileReader("seq_baseline.bundle", seqMs))
            using (var seqBundle = new BundleFile(seqReader, decompressionCache: null, allowParallelDecompression: false))
            {
                Assert.False(seqBundle.UsedParallelDecompression);
                Assert.Equal(nodes.Length, seqBundle.fileList.Length);
                sequentialPayloads = ReadAllNodePayloads(seqBundle.fileList);
            }

            Assert.Equal(payloadA, sequentialPayloads[0]);
            Assert.Equal(payloadB, sequentialPayloads[1]);
            Assert.Equal(payloadC, sequentialPayloads[2]);

            // 1) Exposable MemoryStream (TryGetBuffer == true) -> in-memory parallel decompression
            using (var pubMs = new MemoryStream(bundleBytes, 0, bundleBytes.Length, writable: false, publiclyVisible: true))
            using (var pubReader = new FileReader("parallel_pub_mem.bundle", pubMs))
            using (var pubBundle = new BundleFile(pubReader))
            {
                Assert.True(pubBundle.UsedParallelDecompression);
                AssertNodePayloadsMatch(pubBundle.fileList, nodes, sequentialPayloads);
            }

            // 2) Standard non-exposable MemoryStream (TryGetBuffer == false) -> in-memory parallel decompression
            using (var privMs = new MemoryStream(bundleBytes))
            using (var privReader = new FileReader("parallel_priv_mem.bundle", privMs))
            using (var privBundle = new BundleFile(privReader))
            {
                Assert.True(privBundle.UsedParallelDecompression);
                AssertNodePayloadsMatch(privBundle.fileList, nodes, sequentialPayloads);
            }

            var tempRoot = CreateTempDirectory($"parallel_bundle_{blockCount}");
            try
            {
                var bundlePath = Path.Combine(tempRoot, "mixed_blocks.bundle");
                File.WriteAllBytes(bundlePath, bundleBytes);

                // 3) On-disk FileStream -> in-memory BlocksStream via RandomAccess.Read
                using (var fileReader = new FileReader(bundlePath))
                using (var fileBundle = new BundleFile(fileReader))
                {
                    Assert.True(fileBundle.UsedParallelDecompression);
                    AssertNodePayloadsMatch(fileBundle.fileList, nodes, sequentialPayloads);
                }

                // 4) On-disk FileStream -> disk-backed temporary FileStream via RandomAccess.Read + RandomAccess.Write
                using (var diskOutReader = new FileReader(bundlePath))
                using (var diskOutBundle = new BundleFile(
                    diskOutReader,
                    decompressionCache: null,
                    allowParallelDecompression: true,
                    lowMemoryThresholdOverride: 1024))
                {
                    Assert.True(diskOutBundle.UsedParallelDecompression);
                    Assert.IsType<FileStream>(diskOutBundle.BlocksStream);
                    AssertAllSubStreamsShareSingleHandle(diskOutBundle.fileList);
                    AssertNodePayloadsMatch(diskOutBundle.fileList, nodes, sequentialPayloads);
                }

                // 5) On-disk FileStream -> DecompressedBundleCache disk output path
                var cacheDir = Path.Combine(tempRoot, "Cache");
                var cache = new DecompressedBundleCache(cacheDir, gameScopePath: tempRoot);
                using (var cachedReader = new FileReader(bundlePath))
                using (var cachedBundle = new BundleFile(cachedReader, cache))
                {
                    Assert.True(cachedBundle.UsedParallelDecompression);
                    Assert.False(cachedBundle.LoadedFromDecompressionCache);
                    AssertAllSubStreamsShareSingleHandle(cachedBundle.fileList);
                    AssertNodePayloadsMatch(cachedBundle.fileList, nodes, sequentialPayloads);
                }
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ParallelDecompression_SubStreamBackedSourceBundle_UsesRandomAccessReadAndMatchesSequential()
        {
            var payloadA = CreateCompressiblePayload(16 * 1024, seed: 101);
            var payloadB = CreateDeterministicPayload(16 * 1024, seed: 202);
            var nodes = new[]
            {
                new SyntheticBundleNode("CAB-nested/partA.assets", payloadA),
                new SyntheticBundleNode("CAB-nested/partB.resS", payloadB)
            };

            var bundleBytes = BuildUnityFsV6BundleMixedBlocks(
                nodes,
                blocksInfoCompression: CompressionType.Lz4,
                blockCompressions: new[] { CompressionType.Lz4, CompressionType.None, CompressionType.Lz4HC },
                blocksInfoAtEnd: true,
                splitBlockSize: 2048,
                actualBlockCount: out int blockCount);
            Assert.Equal(16, blockCount);

            var tempRoot = CreateTempDirectory("substream_nested_bundle");
            try
            {
                var outerPath = Path.Combine(tempRoot, "outer_container.bin");
                const int prefixPadding = 513;
                var containerBytes = new byte[prefixPadding + bundleBytes.Length + 257];
                Array.Fill(containerBytes, (byte)0xCC);
                Buffer.BlockCopy(bundleBytes, 0, containerBytes, prefixPadding, bundleBytes.Length);
                File.WriteAllBytes(outerPath, containerBytes);

                using (var subStream = new SubStream(outerPath, prefixPadding, bundleBytes.Length))
                using (var reader = new FileReader(outerPath, subStream))
                using (var bundle = new BundleFile(reader))
                {
                    Assert.True(bundle.UsedParallelDecompression);
                    Assert.Equal(2, bundle.fileList.Length);
                    AssertStreamFilePayload(bundle.fileList[0], payloadA);
                    AssertStreamFilePayload(bundle.fileList[1], payloadB);
                }

                File.Delete(outerPath);
                Assert.False(File.Exists(outerPath));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ParallelDecompression_TruncatedAndCorruptedBlocks_ThrowsExactSequentialExceptionInBlockOrder()
        {
            var payload = CreateCompressiblePayload(16 * 2048, seed: 77);
            var nodes = new[] { new SyntheticBundleNode("CAB-err/data.bin", payload) };

            var validLz4Bundle = BuildUnityFsV6BundleMixedBlocks(
                nodes,
                blocksInfoCompression: CompressionType.None,
                blockCompressions: new[] { CompressionType.Lz4, CompressionType.Lz4HC },
                blocksInfoAtEnd: false,
                splitBlockSize: 2048,
                actualBlockCount: out int lz4BlockCount);
            Assert.Equal(16, lz4BlockCount);

            // 1) Truncated compressed block in MemoryStream and FileStream -> EndOfStreamException
            var truncatedLz4 = validLz4Bundle.AsSpan(0, validLz4Bundle.Length - 20).ToArray();
            using (var ms = new MemoryStream(truncatedLz4, 0, truncatedLz4.Length, writable: false, publiclyVisible: true))
            using (var reader = new FileReader("truncated_parallel.bundle", ms))
            {
                Assert.Throws<EndOfStreamException>(() => new BundleFile(reader));
            }

            // 2) Truncated uncompressed block (CompressionType.None) -> EndOfStreamException
            var validUncompressedBundle = BuildUnityFsV6BundleMixedBlocks(
                nodes,
                blocksInfoCompression: CompressionType.None,
                blockCompressions: new[] { CompressionType.None },
                blocksInfoAtEnd: false,
                splitBlockSize: 2048,
                actualBlockCount: out _);
            var truncatedUncompressed = validUncompressedBundle.AsSpan(0, validUncompressedBundle.Length - 64).ToArray();
            using (var ms = new MemoryStream(truncatedUncompressed, 0, truncatedUncompressed.Length, writable: false, publiclyVisible: true))
            using (var reader = new FileReader("truncated_raw_parallel.bundle", ms))
            {
                Assert.Throws<EndOfStreamException>(() => new BundleFile(reader));
            }

            // 3) Corrupted LZ4 block payload -> IOException
            var corruptedLz4 = (byte[])validLz4Bundle.Clone();
            for (int i = corruptedLz4.Length - 48; i < corruptedLz4.Length - 8; i++)
            {
                corruptedLz4[i] = 0xFF;
            }
            using (var ms = new MemoryStream(corruptedLz4, 0, corruptedLz4.Length, writable: false, publiclyVisible: true))
            using (var reader = new FileReader("corrupt_parallel.bundle", ms))
            {
                Assert.Throws<IOException>(() => new BundleFile(reader));
            }

            var tempRoot = CreateTempDirectory("parallel_errors");
            try
            {
                var truncatedPath = Path.Combine(tempRoot, "truncated.bundle");
                File.WriteAllBytes(truncatedPath, truncatedLz4);
                using (var reader = new FileReader(truncatedPath))
                {
                    Assert.Throws<EndOfStreamException>(() => new BundleFile(
                        reader,
                        decompressionCache: null,
                        allowParallelDecompression: true,
                        lowMemoryThresholdOverride: 1024));
                }

                var corruptPath = Path.Combine(tempRoot, "corrupt.bundle");
                File.WriteAllBytes(corruptPath, corruptedLz4);
                using (var reader = new FileReader(corruptPath))
                {
                    Assert.Throws<IOException>(() => new BundleFile(
                        reader,
                        decompressionCache: null,
                        allowParallelDecompression: true,
                        lowMemoryThresholdOverride: 1024));
                }
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SequentialFallback_LzmaBlocks_TinyBundles_AndCustomStreams_FallsBackCleanlyAndRoundTrips()
        {
            // 1) Tiny bundle (< 4 blocks) -> sequential fallback
            var smallPayload = CreateCompressiblePayload(3000, seed: 5);
            var tinyBundleBytes = BuildUnityFsV6BundleMixedBlocks(
                new[] { new SyntheticBundleNode("CAB-tiny/small.bin", smallPayload) },
                blocksInfoCompression: CompressionType.Lz4,
                blockCompressions: new[] { CompressionType.Lz4 },
                blocksInfoAtEnd: false,
                splitBlockSize: 1024,
                actualBlockCount: out int tinyBlockCount);
            Assert.Equal(3, tinyBlockCount);

            using (var ms = new MemoryStream(tinyBundleBytes))
            using (var reader = new FileReader("tiny.bundle", ms))
            using (var bundle = new BundleFile(reader))
            {
                Assert.False(bundle.UsedParallelDecompression);
                AssertStreamFilePayload(bundle.fileList[0], smallPayload);
            }

            // 2) Multi-block bundle containing LZMA block -> sequential fallback
            var zeroPayload = new byte[64];
            var lzmaBlockBytes = BuildValidZeroPayloadLzmaBlock();
            var lzmaBlocksInfo = BuildBlocksInfoBytes(
                new[]
                {
                    ((uint)16, (uint)lzmaBlockBytes.Length, (StorageBlockFlags)CompressionType.Lzma),
                    ((uint)16, (uint)lzmaBlockBytes.Length, (StorageBlockFlags)CompressionType.Lzma),
                    ((uint)16, (uint)lzmaBlockBytes.Length, (StorageBlockFlags)CompressionType.Lzma),
                    ((uint)16, (uint)lzmaBlockBytes.Length, (StorageBlockFlags)CompressionType.Lzma)
                },
                new[]
                {
                    (0L, (long)zeroPayload.Length, 4u, "CAB-lzma/zeros.bin")
                });
            var allLzmaData = Concat(Concat(lzmaBlockBytes, lzmaBlockBytes), Concat(lzmaBlockBytes, lzmaBlockBytes));
            var lzmaBundleBytes = BuildUnityFsV6HeaderAndBody(
                lzmaBlocksInfo,
                lzmaBlocksInfo,
                ArchiveFlags.BlocksAndDirectoryInfoCombined,
                allLzmaData);

            using (var lzmaMs = new MemoryStream(lzmaBundleBytes))
            using (var lzmaReader = new FileReader("lzma_fallback.bundle", lzmaMs))
            using (var lzmaBundle = new BundleFile(lzmaReader))
            {
                Assert.False(lzmaBundle.UsedParallelDecompression);
                AssertStreamFilePayload(lzmaBundle.fileList[0], zeroPayload);
            }

            // 3) Custom non-FileStream/non-MemoryStream stream -> sequential fallback
            var multiPayload = CreateCompressiblePayload(8 * 1024, seed: 31);
            var multiBundleBytes = BuildUnityFsV6BundleMixedBlocks(
                new[] { new SyntheticBundleNode("CAB-custom/data.bin", multiPayload) },
                blocksInfoCompression: CompressionType.Lz4,
                blockCompressions: new[] { CompressionType.Lz4 },
                blocksInfoAtEnd: false,
                splitBlockSize: 1024,
                actualBlockCount: out int multiBlockCount);
            Assert.Equal(8, multiBlockCount);

            using (var rawMs = new MemoryStream(multiBundleBytes))
            using (var customStream = new NonSeekableWrapperStream(rawMs))
            using (var customReader = new FileReader("custom_stream.bundle", customStream))
            using (var customBundle = new BundleFile(customReader))
            {
                Assert.False(customBundle.UsedParallelDecompression);
                AssertStreamFilePayload(customBundle.fileList[0], multiPayload);
            }
        }

        [Fact]
        public async Task SubStream_ThirtyTwoConcurrentReaders_ShareSingleSafeFileHandle_AndDeleteTempFileCleanlyOnWindows()
        {
            const int readerCount = 32;
            const int sliceSize = 4096;
            var tempRoot = CreateTempDirectory("substream_shared_handle");

            try
            {
                var tempFilePath = Path.Combine(tempRoot, "shared_blocks.tmp");
                var fullData = new byte[readerCount * sliceSize];
                var expectedSlices = new byte[readerCount][];

                for (int i = 0; i < readerCount; i++)
                {
                    expectedSlices[i] = CreateDeterministicPayload(sliceSize, seed: i * 17 + 3);
                    Buffer.BlockCopy(expectedSlices[i], 0, fullData, i * sliceSize, sliceSize);
                }

                File.WriteAllBytes(tempFilePath, fullData);

                // Path-based SubStream construction must deduplicate into one SharedFileHandle
                var subStreams = new SubStream[readerCount];
                for (int i = 0; i < readerCount; i++)
                {
                    subStreams[i] = new SubStream(tempFilePath, (long)i * sliceSize, sliceSize);
                }

                var sharedHandle = subStreams[0].SharedHandle;
                Assert.NotNull(sharedHandle);
                Assert.Equal(readerCount, sharedHandle!.ReferenceCount);

                for (int i = 1; i < readerCount; i++)
                {
                    Assert.Same(sharedHandle, subStreams[i].SharedHandle);
                }

                using var startBarrier = new Barrier(readerCount);
                var tasks = Enumerable.Range(0, readerCount)
                    .Select(index => Task.Run(() =>
                    {
                        startBarrier.SignalAndWait();
                        var stream = subStreams[index];
                        var expected = expectedSlices[index];

                        // 1) Sequential chunked reads via Position
                        var actual = new byte[sliceSize];
                        stream.Position = 0;
                        int offset = 0;
                        while (offset < sliceSize)
                        {
                            int step = Math.Min(373, sliceSize - offset);
                            int read = stream.Read(actual, offset, step);
                            Assert.Equal(step, read);
                            offset += read;
                        }
                        Assert.Equal(expected, actual);

                        // 2) Lock-free positional ReadAt from middle without mutating Position
                        Span<byte> midProbe = stackalloc byte[128];
                        int midRead = stream.ReadAt(1024, midProbe);
                        Assert.Equal(128, midRead);
                        Assert.True(midProbe.SequenceEqual(expected.AsSpan(1024, 128)));

                        // 3) Seek from End and ReadByte
                        stream.Seek(-1, SeekOrigin.End);
                        Assert.Equal(expected[sliceSize - 1], stream.ReadByte());
                        Assert.Equal(-1, stream.ReadByte());
                    }))
                    .ToArray();

                await Task.WhenAll(tasks);

                Assert.True(sharedHandle.IsHandleCreated);
                var underlyingSafeHandle = sharedHandle.SafeFileHandle;
                for (int i = 0; i < readerCount; i++)
                {
                    Assert.Same(underlyingSafeHandle, subStreams[i].SafeFileHandle);
                }

                // Dispose all 32 SubStreams and verify ref-count drops to 0 and file deletes immediately on Windows
                for (int i = 0; i < readerCount; i++)
                {
                    subStreams[i].Dispose();
                    Assert.Equal(readerCount - 1 - i, sharedHandle.ReferenceCount);
                }

                Assert.True(sharedHandle.IsDisposed);
                Assert.Equal(0, sharedHandle.ReferenceCount);

                File.Delete(tempFilePath);
                Assert.False(File.Exists(tempFilePath));

                // Also verify arbitrary non-FileStream fallback with lock + Position synchronization
                using var backingMem = new MemoryStream(fullData);
                var syncRoot = new object();
                var fallbackStreams = Enumerable.Range(0, 8)
                    .Select(i => new SubStream(backingMem, (long)i * sliceSize, sliceSize, syncRoot, leaveOpen: true))
                    .ToArray();

                await Task.WhenAll(fallbackStreams.Select((s, idx) => Task.Run(() =>
                {
                    var buf = new byte[sliceSize];
                    int read = s.Read(buf, 0, sliceSize);
                    Assert.Equal(sliceSize, read);
                    Assert.Equal(expectedSlices[idx], buf);
                    s.Dispose();
                })));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void FastHash64_And_ComputeSourceContentSignature_MatchesXxh64Vectors_AndProducesConsistentCacheSignatures()
        {
            // Canonical XXH64 test vectors:
            // Empty buffer with seed 0 => 0xEF46DB3751D8E999
            Assert.Equal(0xEF46DB3751D8E999UL, FastHash64.Hash(ReadOnlySpan<byte>.Empty, seed: 0));
            // Empty buffer with seed 2654435761 => 0xAC75FDA2929B17EF
            Assert.Equal(0xAC75FDA2929B17EFUL, FastHash64.Hash(ReadOnlySpan<byte>.Empty, seed: 2654435761UL));

            var sample256 = CreateDeterministicPayload(256, seed: 19);
            ulong h1 = FastHash64.Hash(sample256, seed: 0);
            ulong h2 = FastHash64.Hash(sample256, seed: 0);
            Assert.Equal(h1, h2);

            sample256[127] ^= 0x01;
            ulong h3 = FastHash64.Hash(sample256, seed: 0);
            Assert.NotEqual(h1, h3);

            var tempRoot = CreateTempDirectory("fasthash_sig");
            try
            {
                var fileA = Path.Combine(tempRoot, "bundleA.bundle");
                var bytesA = CreateDeterministicPayload(64 * 1024, seed: 123);
                File.WriteAllBytes(fileA, bytesA);

                string sig1 = BundleFile.ComputeSourceContentSignature(fileA, bytesA.Length);
                string sig2 = BundleFile.ComputeSourceContentSignature(fileA, bytesA.Length);
                Assert.StartsWith("s1_", sig1);
                Assert.Equal(19, sig1.Length); // "s1_" + 16 hex digits
                Assert.Equal(sig1, sig2);

                // Modify 1 byte and verify signature changes
                bytesA[bytesA.Length / 2] ^= 0x80;
                File.WriteAllBytes(fileA, bytesA);
                string sigModified = BundleFile.ComputeSourceContentSignature(fileA, bytesA.Length);
                Assert.NotEqual(sig1, sigModified);

                // File must not be locked after ComputeSourceContentSignature
                File.Delete(fileA);
                Assert.False(File.Exists(fileA));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ParallelDecompression_And_FastHash64_BenchmarkSpeedup()
        {
            // 1) 64-block 16 MiB LZ4HC bundle benchmark (FileStream -> MemoryStream)
            const int blockCount = 64;
            const int blockSize = 256 * 1024; // 16 MiB total
            var payload = CreateCompressiblePayload(blockCount * blockSize, seed: 99);
            var nodes = new[] { new SyntheticBundleNode("CAB-bench/large.assets", payload) };
            var bundleBytes = BuildUnityFsV6BundleMixedBlocks(
                nodes,
                blocksInfoCompression: CompressionType.Lz4HC,
                blockCompressions: new[] { CompressionType.Lz4HC, CompressionType.Lz4 },
                blocksInfoAtEnd: false,
                splitBlockSize: blockSize,
                actualBlockCount: out int actualBlocks);
            Assert.Equal(blockCount, actualBlocks);

            var tempRoot = CreateTempDirectory("bundle_perf_bench");
            try
            {
                var bundlePath = Path.Combine(tempRoot, "bench16m.bundle");
                File.WriteAllBytes(bundlePath, bundleBytes);

                // Warmup
                using (var rWarm1 = new FileReader(bundlePath))
                using (var bWarm1 = new BundleFile(rWarm1, null, allowParallelDecompression: false))
                {
                    Assert.False(bWarm1.UsedParallelDecompression);
                }
                using (var rWarm2 = new FileReader(bundlePath))
                using (var bWarm2 = new BundleFile(rWarm2, null, allowParallelDecompression: true))
                {
                    Assert.True(bWarm2.UsedParallelDecompression);
                }

                const int iterations = 5;
                var swSeq = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                {
                    using var r = new FileReader(bundlePath);
                    using var b = new BundleFile(r, null, allowParallelDecompression: false);
                }
                swSeq.Stop();

                var swPar = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                {
                    using var r = new FileReader(bundlePath);
                    using var b = new BundleFile(r, null, allowParallelDecompression: true);
                }
                swPar.Stop();

                double seqMs = swSeq.Elapsed.TotalMilliseconds / iterations;
                double parMs = swPar.Elapsed.TotalMilliseconds / iterations;
                double bundleSpeedup = seqMs / Math.Max(0.01, parMs);

                // 2) FastHash64 vs scalar byte-by-byte FNV-1a on 8 MiB buffer
                var hashBuf = CreateDeterministicPayload(8 * 1024 * 1024, seed: 42);
                ulong fnvWarm = ComputeScalarFnv1a(hashBuf);
                ulong xxhWarm = FastHash64.Hash(hashBuf);
                Assert.NotEqual(fnvWarm, xxhWarm);

                var swFnv = Stopwatch.StartNew();
                ulong fnvAcc = 0;
                for (int i = 0; i < iterations; i++)
                {
                    fnvAcc ^= ComputeScalarFnv1a(hashBuf);
                }
                swFnv.Stop();

                var swXxh = Stopwatch.StartNew();
                ulong xxhAcc = 0;
                for (int i = 0; i < iterations; i++)
                {
                    xxhAcc ^= FastHash64.Hash(hashBuf, (ulong)i);
                }
                swXxh.Stop();
                GC.KeepAlive((fnvAcc, xxhAcc));

                double fnvMs = swFnv.Elapsed.TotalMilliseconds / iterations;
                double xxhMs = swXxh.Elapsed.TotalMilliseconds / iterations;
                double hashSpeedup = fnvMs / Math.Max(0.001, xxhMs);

                output.WriteLine(
                    $"BENCHMARK_RESULTS: 16MiB_64Block_Bundle Sequential={seqMs:F2}ms Parallel={parMs:F2}ms Speedup={bundleSpeedup:F2}x | " +
                    $"8MiB_Hash ScalarFNV1a={fnvMs:F2}ms FastHash64={xxhMs:F2}ms Speedup={hashSpeedup:F2}x");
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static ulong ComputeScalarFnv1a(byte[] buffer)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < buffer.Length; i++)
            {
                hash ^= buffer[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private static byte[][] ReadAllNodePayloads(StreamFile[] files)
        {
            var result = new byte[files.Length][];
            for (int i = 0; i < files.Length; i++)
            {
                var stream = files[i].stream;
                stream.Position = 0;
                var data = new byte[stream.Length];
                int total = 0;
                while (total < data.Length)
                {
                    int r = stream.Read(data, total, data.Length - total);
                    Assert.True(r > 0);
                    total += r;
                }
                result[i] = data;
            }
            return result;
        }

        private static void AssertNodePayloadsMatch(
            StreamFile[] actualFiles,
            SyntheticBundleNode[] expectedNodes,
            byte[][] expectedPayloads)
        {
            Assert.Equal(expectedNodes.Length, actualFiles.Length);
            for (int i = 0; i < expectedNodes.Length; i++)
            {
                Assert.Equal(expectedNodes[i].Path, actualFiles[i].path);
                AssertStreamFilePayload(actualFiles[i], expectedPayloads[i]);
            }
        }

        private static void AssertStreamFilePayload(StreamFile file, byte[] expectedPayload)
        {
            Assert.NotNull(file.stream);
            Assert.Equal(expectedPayload.Length, file.stream.Length);
            file.stream.Position = 0;
            var actual = new byte[expectedPayload.Length];
            int total = 0;
            while (total < actual.Length)
            {
                int r = file.stream.Read(actual, total, actual.Length - total);
                Assert.True(r > 0);
                total += r;
            }
            Assert.Equal(expectedPayload, actual);
        }

        private static void AssertAllSubStreamsShareSingleHandle(StreamFile[] files)
        {
            Assert.NotEmpty(files);
            var firstSub = Assert.IsType<SubStream>(files[0].stream);
            Assert.NotNull(firstSub.SharedHandle);
            for (int i = 1; i < files.Length; i++)
            {
                var sub = Assert.IsType<SubStream>(files[i].stream);
                Assert.Same(firstSub.SharedHandle, sub.SharedHandle);
            }
        }

        private static byte[] BuildValidZeroPayloadLzmaBlock()
        {
            var lzma = new byte[32];
            lzma[0] = 0x5D; // lc=3, lp=0, pb=2
            lzma[1] = 0x00;
            lzma[2] = 0x10; // dictionary size = 4096
            lzma[3] = 0x00;
            lzma[4] = 0x00;
            return lzma;
        }

        private static byte[] BuildUnityFsV6BundleMixedBlocks(
            SyntheticBundleNode[] nodes,
            CompressionType blocksInfoCompression,
            IReadOnlyList<CompressionType> blockCompressions,
            bool blocksInfoAtEnd,
            int splitBlockSize,
            out int actualBlockCount)
        {
            using var combinedPayload = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Length);
            foreach (var node in nodes)
            {
                long offset = combinedPayload.Position;
                combinedPayload.Write(node.Payload, 0, node.Payload.Length);
                dirEntries.Add((offset, node.Payload.Length, node.Flags, node.Path));
            }
            var rawBlocks = combinedPayload.ToArray();

            var blockInfos = new List<(uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)>();
            using var compressedBlocksStream = new MemoryStream();
            int cursor = 0;
            int blockIndex = 0;
            while (cursor < rawBlocks.Length || blockInfos.Count == 0)
            {
                int chunkLen = Math.Min(splitBlockSize, rawBlocks.Length - cursor);
                var slice = rawBlocks.AsSpan(cursor, chunkLen).ToArray();
                var blockComp = blockCompressions[blockIndex % blockCompressions.Count];
                byte[] stored = CompressBlock(slice, blockComp);
                compressedBlocksStream.Write(stored, 0, stored.Length);
                blockInfos.Add(((uint)chunkLen, (uint)stored.Length, (StorageBlockFlags)blockComp));
                cursor += chunkLen;
                blockIndex++;
                if (rawBlocks.Length == 0)
                {
                    break;
                }
            }

            actualBlockCount = blockInfos.Count;
            var compressedBlocksData = compressedBlocksStream.ToArray();
            var uncompressedBlocksInfo = BuildBlocksInfoBytes(blockInfos, dirEntries);
            var compressedBlocksInfo = CompressBlock(uncompressedBlocksInfo, blocksInfoCompression);

            ArchiveFlags flags = (ArchiveFlags)blocksInfoCompression | ArchiveFlags.BlocksAndDirectoryInfoCombined;
            if (blocksInfoAtEnd)
            {
                flags |= ArchiveFlags.BlocksInfoAtTheEnd;
            }

            return BuildUnityFsV6HeaderAndBody(
                uncompressedBlocksInfo,
                compressedBlocksInfo,
                flags,
                compressedBlocksData);
        }

        private static byte[] BuildUnityFsV6HeaderAndBody(
            byte[] uncompressedBlocksInfo,
            byte[] compressedBlocksInfo,
            ArchiveFlags flags,
            byte[] compressedBlocksData)
        {
            bool blocksInfoAtEnd = (flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0;
            byte[] body = blocksInfoAtEnd
                ? Concat(compressedBlocksData, compressedBlocksInfo)
                : Concat(compressedBlocksInfo, compressedBlocksData);

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
            ms.Write(new byte[16]);
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

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudioTests_{prefix}_{Guid.NewGuid():N}");
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
