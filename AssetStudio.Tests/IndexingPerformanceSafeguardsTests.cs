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
        // 5. Zero-Loss Container Mapping & Zero Decompressed Buffer Retention (GC / WeakReference)
        // =========================================================================

        [Fact]
        public async Task AssetsManager_LazyIndexingAndClearKeepIndex_PreservesContainersWithZeroBufferOrSerializedFileRetention()
        {
            var tempRoot = CreateTempDirectory("lazy_zero_retention");
            try
            {
                var bundlePath = Path.Combine(tempRoot, "container_retention.bundle");
                var sfBytes = BuildSerializedFileWithAssetBundleContainersBytes("2022.3.40f1");
                var resSBytes = CreateCompressiblePayload(128 * 1024, seed: 91);
                File.WriteAllBytes(bundlePath, BuildUnityFsV6Bundle(
                    new[]
                    {
                        new SyntheticBundleNode("CAB-ret01", sfBytes, Flags: 4),
                        new SyntheticBundleNode("CAB-ret01.resS", resSBytes, Flags: 0)
                    },
                    splitBlockSize: 4096));

                using var manager = new AssetsManager
                {
                    LazyLoading = true
                };

                // 1) Pass 1 worker indexing must populate all handles + containers + material name fallback
                //    while leaving zero loaded files, zero RealObject references, and zero SourceFile references.
                await manager.IndexSingleFileInWorkerAsync(bundlePath);

                Assert.Empty(manager.assetsFileList);
                Assert.Empty(manager.bundleStreams);
                Assert.Equal(3, manager.ProjectIndex.Count);

                var handles = manager.ProjectIndex.GetHandles().ToDictionary(h => h.PathID);
                Assert.True(handles.TryGetValue(1L, out var abHandle));
                Assert.True(handles.TryGetValue(100L, out var texHandle));
                Assert.True(handles.TryGetValue(200L, out var matHandle));

                Assert.Equal(ClassIDType.AssetBundle, abHandle!.Type);
                Assert.Equal("sample_bundle.bundle", abHandle.Name);
                Assert.Null(abHandle.RealObject);
                Assert.Null(abHandle.SourceFile);

                Assert.Equal(ClassIDType.Texture2D, texHandle!.Type);
                Assert.Equal("sample_albedo", texHandle.Name);
                Assert.Equal("assets/art/textures/sample_albedo.png", texHandle.Container);
                Assert.Null(texHandle.RealObject);
                Assert.Null(texHandle.SourceFile);

                Assert.Equal(ClassIDType.Material, matHandle!.Type);
                Assert.Equal("hero_outfit", matHandle.Name);
                Assert.Equal("assets/art/materials/hero_outfit.mat", matHandle.Container);
                Assert.Null(matHandle.RealObject);
                Assert.Null(matHandle.SourceFile);

                // 2) Materialize AssetBundle on demand during a connections/preview load, capture WeakReferences
                //    to the materialized AssetBundle, SerializedFile, and decompressed CAB buffer, then release.
                var (weakBundleObj, weakSerializedFile, weakCabBuffer) = await LoadMaterializeAndReleaseForGcProbeAsync(manager, bundlePath);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }

                Assert.False(weakBundleObj.IsAlive, "AssetBundle RealObject should not be pinned after file release.");
                Assert.False(weakSerializedFile.IsAlive, "SerializedFile should not be pinned after file release.");
                Assert.False(weakCabBuffer.IsAlive, "Decompressed CAB byte[] buffer should not be pinned after file release.");

                // 3) Verify zero loss of indexed metadata and containers after full GC, and verify on-demand reload works.
                var handlesAfterGc = manager.ProjectIndex.GetHandles().ToDictionary(h => h.PathID);
                Assert.Equal("assets/art/textures/sample_albedo.png", handlesAfterGc[100L].Container);
                Assert.Equal("assets/art/materials/hero_outfit.mat", handlesAfterGc[200L].Container);
                Assert.Equal("hero_outfit", handlesAfterGc[200L].Name);

                manager.LoadFilesForPreview(bundlePath);
                var reloadedAb = manager.ResolveHandle(handlesAfterGc[1L]) as AssetBundle;
                Assert.NotNull(reloadedAb);
                Assert.Equal("sample_bundle.bundle", reloadedAb!.m_Name);
                Assert.Equal(2, reloadedAb.m_Container.Length);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void BundleFile_And_FileReader_PubliclyVisibleMemoryStreams_AvoidDoubleCopyAndClearCacheOnDispose()
        {
            var tempRoot = CreateTempDirectory("filereader_zero_double_copy");
            try
            {
                var sfBytes = BuildMinimalSerializedFileBytes("2022.3.40f1");
                var resSBytes = CreateCompressiblePayload(64 * 1024, seed: 33);

                // 1) Partial decompression path (CAB-only) -> MemoryStream must be publiclyVisible (TryGetBuffer == true)
                var partialBundlePath = Path.Combine(tempRoot, "partial.bundle");
                File.WriteAllBytes(partialBundlePath, BuildUnityFsV6Bundle(
                    new[]
                    {
                        new SyntheticBundleNode("CAB-p01", sfBytes, Flags: 4),
                        new SyntheticBundleNode("CAB-p01.resS", resSBytes, Flags: 0)
                    },
                    splitBlockSize: 4096));

                using (var reader = new FileReader(partialBundlePath))
                {
                    var partialBundle = new BundleFile(reader, decompressionCache: null, allowParallelDecompression: true, serializedFilesOnly: true);
                    Assert.True(partialBundle.UsedPartialDecompression);
                    var memStream = Assert.IsType<MemoryStream>(partialBundle.fileList[0].stream);
                    Assert.True(memStream.TryGetBuffer(out var seg) && seg.Array != null);

                    using var subReader = new FileReader(Path.Combine(tempRoot, "CAB-p01"), memStream);
                    using var cloned = subReader.Clone();
                    var cachedField = typeof(FileReader).GetField("cachedMemoryBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Assert.NotNull(cachedField);
                    Assert.Null(cachedField!.GetValue(subReader));
                }

                // 2) Full in-memory decompression path -> MemoryStream must also be publiclyVisible (TryGetBuffer == true)
                var fullBundlePath = Path.Combine(tempRoot, "full.bundle");
                File.WriteAllBytes(fullBundlePath, BuildUnityFsV6Bundle(
                    new[]
                    {
                        new SyntheticBundleNode("CAB-f01", sfBytes, Flags: 4)
                    },
                    splitBlockSize: 4096));

                using (var reader = new FileReader(fullBundlePath))
                {
                    var fullBundle = new BundleFile(reader, decompressionCache: null, allowParallelDecompression: true, serializedFilesOnly: false);
                    var memStream = Assert.IsType<MemoryStream>(fullBundle.fileList[0].stream);
                    Assert.True(memStream.TryGetBuffer(out var seg) && seg.Array != null);
                }

                // 3) Even when FileReader wraps a non-publicly-visible MemoryStream, Dispose/Close must clear cachedMemoryBuffer
                var privatePayload = CreateCompressiblePayload(16 * 1024, seed: 55);
                var nonVisibleStream = new MemoryStream(privatePayload, 0, privatePayload.Length, writable: false, publiclyVisible: false);
                var cachedBufferField = typeof(FileReader).GetField("cachedMemoryBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

                var nonVisibleReader = new FileReader(Path.Combine(tempRoot, "CAB-nonvis"), nonVisibleStream);
                using (var cloned = nonVisibleReader.Clone())
                {
                    Assert.NotNull(cachedBufferField.GetValue(nonVisibleReader));
                }

                nonVisibleReader.Close();
                Assert.Null(cachedBufferField.GetValue(nonVisibleReader));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static async Task<(WeakReference WeakBundleObj, WeakReference WeakSerializedFile, WeakReference WeakCabBuffer)> LoadMaterializeAndReleaseForGcProbeAsync(
            AssetsManager manager,
            string bundlePath)
        {
            await manager.LoadFilesForConnectionsAsync(bundlePath);
            var sf = Assert.Single(manager.assetsFileList);
            var memStream = Assert.IsType<MemoryStream>(sf.reader.BaseStream);
            Assert.True(memStream.TryGetBuffer(out var seg) && seg.Array != null);

            var abHandle = manager.ProjectIndex.GetHandles().First(h => h.Type == ClassIDType.AssetBundle);
            abHandle.SourceFile = sf;
            var abObj = Assert.IsType<AssetBundle>(manager.ResolveHandle(abHandle));

            var weakBundleObj = new WeakReference(abObj);
            var weakSerializedFile = new WeakReference(sf);
            var weakCabBuffer = new WeakReference(seg.Array!);

            manager.ClearLoadedFilesKeepIndex();
            Assert.Empty(manager.assetsFileList);
            Assert.Equal(0, manager.LruCache.Count);

            var refreshedAbHandle = manager.ProjectIndex.GetHandles().First(h => h.Type == ClassIDType.AssetBundle);
            Assert.Null(refreshedAbHandle.RealObject);
            Assert.Null(refreshedAbHandle.SourceFile);

            return (weakBundleObj, weakSerializedFile, weakCabBuffer);
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

        private static byte[] BuildSerializedFileWithAssetBundleContainersBytes(string unityVersion)
        {
            static void WriteAlignedString(BinaryWriter bw, string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                bw.Write(bytes.Length);
                bw.Write(bytes);
                int pad = (4 - ((int)bw.BaseStream.Position & 3)) & 3;
                for (int i = 0; i < pad; i++)
                {
                    bw.Write((byte)0);
                }
            }

            byte[] abData;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteAlignedString(bw, "sample_bundle.bundle");
                // m_PreloadTable (2 PPtrs: PathID 100 and PathID 200)
                bw.Write(2);
                bw.Write(0); bw.Write(100L);
                bw.Write(0); bw.Write(200L);
                // m_Container (2 entries)
                bw.Write(2);
                WriteAlignedString(bw, "assets/art/textures/sample_albedo.png");
                bw.Write(0); // preloadIndex
                bw.Write(1); // preloadSize
                bw.Write(0); bw.Write(100L); // asset PPtr

                WriteAlignedString(bw, "assets/art/materials/hero_outfit.mat");
                bw.Write(1); // preloadIndex
                bw.Write(1); // preloadSize
                bw.Write(0); bw.Write(200L); // asset PPtr
                bw.Flush();
                abData = ms.ToArray();
            }

            byte[] texData;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteAlignedString(bw, "sample_albedo");
                bw.Write(new byte[32]);
                bw.Flush();
                texData = ms.ToArray();
            }

            byte[] matData;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteAlignedString(bw, ""); // Empty material name to exercise container name fallback
                bw.Write(new byte[32]);
                bw.Flush();
                matData = ms.ToArray();
            }

            uint offset0 = 0;
            uint offset1 = (uint)((abData.Length + 7) & ~7);
            uint offset2 = (uint)(((int)offset1 + texData.Length + 7) & ~7);
            uint totalDataSize = (uint)((int)offset2 + matData.Length);

            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes(unityVersion));
                metaBw.Write((byte)0);
                metaBw.Write((int)BuildTarget.StandaloneWindows64);
                metaBw.Write(false); // m_EnableTypeTree = false

                // 3 types: AssetBundle (142), Texture2D (28), Material (21)
                metaBw.Write(3);
                int[] classIds = { (int)ClassIDType.AssetBundle, (int)ClassIDType.Texture2D, (int)ClassIDType.Material };
                for (int i = 0; i < classIds.Length; i++)
                {
                    metaBw.Write(classIds[i]);
                    metaBw.Write(false); // m_IsStrippedType
                    metaBw.Write((short)-1); // m_ScriptTypeIndex
                    metaBw.Write(new byte[16]); // m_OldTypeHash
                }

                // 3 objects
                metaBw.Write(3);
                (long pathId, uint byteStart, uint byteSize, int typeId)[] objs =
                {
                    (1L, offset0, (uint)abData.Length, 0),
                    (100L, offset1, (uint)texData.Length, 1),
                    (200L, offset2, (uint)matData.Length, 2)
                };

                for (int i = 0; i < objs.Length; i++)
                {
                    int alignPad = (4 - (((int)metaMs.Position + 20) & 3)) & 3;
                    for (int p = 0; p < alignPad; p++)
                    {
                        metaBw.Write((byte)0);
                    }
                    metaBw.Write(objs[i].pathId);
                    metaBw.Write(objs[i].byteStart);
                    metaBw.Write(objs[i].byteSize);
                    metaBw.Write(objs[i].typeId);
                }

                metaBw.Write(0); // scriptCount
                metaBw.Write(0); // externalsCount
                metaBw.Write(0); // refTypesCount
                metaBw.Write((byte)0); // userInformation
                metaBw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            uint dataOffset = (uint)((headerSize + metadata.Length + 15) & ~15);
            uint totalFileSize = dataOffset + totalDataSize;
            var fullBytes = new byte[totalFileSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalFileSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), dataOffset);
            fullBytes[16] = 0; // LittleEndian
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            Buffer.BlockCopy(abData, 0, fullBytes, (int)(dataOffset + offset0), abData.Length);
            Buffer.BlockCopy(texData, 0, fullBytes, (int)(dataOffset + offset1), texData.Length);
            Buffer.BlockCopy(matData, 0, fullBytes, (int)(dataOffset + offset2), matData.Length);
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
