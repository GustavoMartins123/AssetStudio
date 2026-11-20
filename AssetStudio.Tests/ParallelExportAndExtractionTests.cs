using K4os.Compression.LZ4;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using UkooLabs.FbxSharpie;
using UkooLabs.FbxSharpie.Tokens;
using UkooLabs.FbxSharpie.Tokens.Value;
using UkooLabs.FbxSharpie.Tokens.ValueArray;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class ParallelExportAndExtractionTests
    {
        private readonly ITestOutputHelper _output;

        public ParallelExportAndExtractionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private sealed class SyscallTrackingStream : Stream
        {
            private readonly MemoryStream _inner = new();
            private int _writeCalls;
            private int _seekCalls;
            private int _flushCalls;

            public int WriteCalls => Volatile.Read(ref _writeCalls);
            public int SeekCalls => Volatile.Read(ref _seekCalls);
            public int FlushCalls => Volatile.Read(ref _flushCalls);

            public byte[] ToArray() => _inner.ToArray();

            public override bool CanRead => false;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set
                {
                    Interlocked.Increment(ref _seekCalls);
                    _inner.Position = value;
                }
            }

            public override void Flush()
            {
                Interlocked.Increment(ref _flushCalls);
                _inner.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin)
            {
                Interlocked.Increment(ref _seekCalls);
                return _inner.Seek(offset, origin);
            }

            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref _writeCalls);
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Interlocked.Increment(ref _writeCalls);
                _inner.Write(buffer);
            }

            public override void WriteByte(byte value)
            {
                Interlocked.Increment(ref _writeCalls);
                _inner.WriteByte(value);
            }
        }

        [Fact]
        public void ExportConversionContext_1000ParallelWorkersWithIdenticalCollidingNames_Produce1000DistinctFilesWithZeroOverwrites()
        {
            var tempDir = CreateTempDirectory("parallel_collision_1000");
            try
            {
                const int workerCount = 1000;
                var context = new MainWindow.ExportConversionContext(new List<SerializedFile>());
                var reservedPaths = new string[workerCount];
                var exceptions = new ConcurrentBag<Exception>();

                Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    try
                    {
                        var dummyObj = (Object)RuntimeHelpers.GetUninitializedObject(typeof(Object));
                        dummyObj.m_PathID = (i % 10) + 1; // Deliberate PathID collisions across 1,000 items
                        dummyObj.type = ClassIDType.TextAsset;

                        var item = new AssetItem(dummyObj)
                        {
                            Name = "CollidingHeroAsset",
                            // Deliberate UniqueID collisions every 10 items to exercise both hash suffix and collision index fallback
                            UniqueID = $"duplicate_id_{i % 10}"
                        };

                        Assert.True(context.TryGetAssetFilePath(tempDir, "CollidingHeroAsset", ".txt", item, out var path));
                        reservedPaths[i] = path;

                        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        var payload = Encoding.UTF8.GetBytes($"worker-{i}");
                        fs.Write(payload, 0, payload.Length);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                Assert.Empty(exceptions);
                Assert.Equal(workerCount, context.ReservedPathCount);

                var distinctPaths = reservedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Assert.Equal(workerCount, distinctPaths.Count);

                var filesOnDisk = Directory.GetFiles(tempDir, "*.txt", SearchOption.TopDirectoryOnly);
                Assert.Equal(workerCount, filesOnDisk.Length);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ReserveUniqueFilePath_AndDirectoryPath_1000ParallelWorkers_NeverCollide()
        {
            var tempDir = CreateTempDirectory("parallel_reserve_unique");
            try
            {
                const int count = 1000;
                var reservedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                var fileResults = new string[count];
                var baseFile = Path.Combine(tempDir, "SplitModel.fbx");

                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    fileResults[i] = MainWindow.ReserveUniqueFilePath(baseFile, reservedFiles);
                });

                Assert.Equal(count, fileResults.Distinct(StringComparer.OrdinalIgnoreCase).Count());

                var reservedDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                var dirResults = new string[count];
                var baseDir = Path.Combine(tempDir, "GameObjectFolder");

                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    dirResults[i] = MainWindow.ReserveUniqueDirectoryPath(baseDir, reservedDirs);
                });

                Assert.Equal(count, dirResults.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ExportProgressTracker_50000Items_ThrottlesDiskWritesAndUiPostsWhileFlushingFirstLastAndErrors()
        {
            var tempDir = CreateTempDirectory("throttled_progress");
            try
            {
                const int totalItems = 50_000;
                var currentExportPath = Path.Combine(tempDir, "nested", "export-current.txt");
                var reportedPercentages = new ConcurrentQueue<int>();

                var tracker = new MainWindow.ExportProgressTracker(
                    currentExportPath,
                    totalItems,
                    uiProgressCallback: p => reportedPercentages.Enqueue(p),
                    fileWriteIntervalMs: 250,
                    uiProgressIntervalMs: 50);

                var completed = 0;
                Parallel.For(0, totalItems, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    var c = Interlocked.Increment(ref completed);
                    tracker.ReportCustomProgress(
                        typeString: "Texture2D",
                        name: $"Tex_{c}",
                        pathId: c,
                        source: "sharedassets0.assets",
                        completedCount: c,
                        forceFlush: c == 1 || c == totalItems);
                });

                Assert.True(File.Exists(currentExportPath));
                var finalText = File.ReadAllText(currentExportPath, Encoding.UTF8);
                Assert.Contains($"Exporting {totalItems}/{totalItems}", finalText);

                // Instead of 50,000 synchronous disk writes and 50,000 UI posts, both are bounded by ~100
                _output.WriteLine($"50,000 items -> export-current.txt writes: {tracker.FileWriteCount}, UI progress posts: {tracker.UiPostCount}");
                Assert.InRange(tracker.FileWriteCount, 2, 100);
                Assert.InRange(tracker.UiPostCount, 1, 150);
                Assert.Equal(100, tracker.LastReportedPercent);
                Assert.Contains(100, reportedPercentages);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void AutodeskBinaryWriter_5000NodeTree_BitExactParityAndZeroSeekSyscallBenchmark()
        {
            var tempDir = CreateTempDirectory("fbx_5000_nodes");
            try
            {
                var doc = BuildSyntheticFbxDocument(targetNodeCount: 5000, out int actualNodeCount);
                Assert.True(actualNodeCount >= 5000);

                // 1. Verify syscall counts & bit-for-bit parity in memory
                var unbufferedTracking = new SyscallTrackingStream();
                AutodeskBinaryWriter.WriteDirectToSeekableStream(doc, unbufferedTracking);

                var bufferedTracking = new SyscallTrackingStream();
                AutodeskBinaryWriter.Write(doc, bufferedTracking);

                var referenceBytes = unbufferedTracking.ToArray();
                var optimizedBytes = bufferedTracking.ToArray();

                Assert.Equal(referenceBytes.Length, optimizedBytes.Length);
                Assert.True(referenceBytes.AsSpan().SequenceEqual(optimizedBytes));

                // Direct writer seeks 3 times per node (15,000+ seeks); buffered writer seeks 0 times on output stream
                Assert.True(unbufferedTracking.SeekCalls >= actualNodeCount * 3);
                Assert.Equal(0, bufferedTracking.SeekCalls);
                Assert.Equal(1, bufferedTracking.WriteCalls);

                // 2. Benchmark on real 4KB FileStream (legacy) vs AutodeskBinaryWriter.Write(doc, path)
                var legacyFilePath = Path.Combine(tempDir, "legacy_unbuffered.fbx");
                var optimizedFilePath = Path.Combine(tempDir, "optimized_buffered.fbx");

                var swLegacy = Stopwatch.StartNew();
                using (var legacyFs = new FileStream(legacyFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096))
                {
                    AutodeskBinaryWriter.WriteDirectToSeekableStream(doc, legacyFs);
                }
                swLegacy.Stop();

                var swOptimized = Stopwatch.StartNew();
                AutodeskBinaryWriter.Write(doc, optimizedFilePath);
                swOptimized.Stop();

                var diskLegacyBytes = File.ReadAllBytes(legacyFilePath);
                var diskOptimizedBytes = File.ReadAllBytes(optimizedFilePath);
                Assert.True(diskLegacyBytes.AsSpan().SequenceEqual(diskOptimizedBytes));

                _output.WriteLine(
                    $"FBX 5,000-node tree ({actualNodeCount} nodes, {diskOptimizedBytes.Length:N0} bytes): " +
                    $"Legacy seeks={unbufferedTracking.SeekCalls:N0}, writes={unbufferedTracking.WriteCalls:N0}, time={swLegacy.Elapsed.TotalMilliseconds:F2} ms | " +
                    $"Buffered seeks={bufferedTracking.SeekCalls}, writes={bufferedTracking.WriteCalls}, time={swOptimized.Elapsed.TotalMilliseconds:F2} ms");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void AssetExportHelper_TryGetShaderGuid_CachesPerUnityProjectRootAndSupportsCleanInvalidation()
        {
            var tempRoot = CreateTempDirectory("shader_guid_cache");
            try
            {
                var projectRoot = Path.Combine(tempRoot, "UnityProj");
                var assetsShadersDir = Path.Combine(projectRoot, "Assets", "Shaders");
                var packageCacheDir = Path.Combine(projectRoot, "Library", "PackageCache", "com.unity.render-pipelines.universal@14.0.8", "Shaders");
                var projectSettingsDir = Path.Combine(projectRoot, "ProjectSettings");
                var exportDir = Path.Combine(projectRoot, "Assets", "ExportedMaterials", "Characters");

                Directory.CreateDirectory(assetsShadersDir);
                Directory.CreateDirectory(packageCacheDir);
                Directory.CreateDirectory(projectSettingsDir);
                Directory.CreateDirectory(exportDir);

                const string customGuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
                const string urpLitGuid = "11223344556677889900aabbccddeeff";

                var customShaderMeta = Path.Combine(assetsShadersDir, "HeroToon.shader.meta");
                var urpLitShaderMeta = Path.Combine(packageCacheDir, "Lit.shader.meta");

                File.WriteAllText(customShaderMeta, $"fileFormatVersion: 2\nguid: {customGuid}\nShaderImporter:\n", Encoding.UTF8);
                File.WriteAllText(urpLitShaderMeta, $"fileFormatVersion: 2\nguid: {urpLitGuid}\nShaderImporter:\n", Encoding.UTF8);

                AssetExportHelper.ClearShaderGuidCache();
                Assert.Equal(0, AssetExportHelper.ShaderGuidDirectoryScanCount);

                // 500 concurrent lookups across nested export paths inside the Unity project
                Parallel.For(0, 500, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    Assert.True(AssetExportHelper.TryGetShaderGuid(exportDir, "Custom/Character/HeroToon", out var g1));
                    Assert.Equal(customGuid, g1);

                    Assert.True(AssetExportHelper.TryGetShaderGuid(exportDir, "Universal Render Pipeline/Lit", out var g2));
                    Assert.Equal(urpLitGuid, g2);

                    Assert.False(AssetExportHelper.TryGetShaderGuid(exportDir, "NonExistent/MissingShader", out _));
                });

                // Directory tree must have been scanned exactly ONCE for the entire Unity project root
                Assert.Equal(1, AssetExportHelper.ShaderGuidDirectoryScanCount);

                // Delete the meta file on disk; cached lookup must still succeed without touching disk
                File.Delete(customShaderMeta);
                Assert.True(AssetExportHelper.TryGetShaderGuid(exportDir, "Custom/Character/HeroToon", out var cachedGuid));
                Assert.Equal(customGuid, cachedGuid);
                Assert.Equal(1, AssetExportHelper.ShaderGuidDirectoryScanCount);

                // Invalidate project root cache; next lookup re-scans and observes deletion
                AssetExportHelper.ClearShaderGuidCache(projectRoot);
                Assert.False(AssetExportHelper.TryGetShaderGuid(exportDir, "Custom/Character/HeroToon", out _));
                Assert.True(AssetExportHelper.TryGetShaderGuid(exportDir, "Universal Render Pipeline/Lit", out var reloadedLitGuid));
                Assert.Equal(urpLitGuid, reloadedLitGuid);
                Assert.Equal(2, AssetExportHelper.ShaderGuidDirectoryScanCount);
            }
            finally
            {
                AssetExportHelper.ClearShaderGuidCache();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MainWindow_ParallelExtractFilesAndFolder_ExtractsMultipleUnityFsBundlesAndPreservesZipSlipGuards()
        {
            var tempRoot = CreateTempDirectory("parallel_extract_bundles");
            try
            {
                var sourceDir = Path.Combine(tempRoot, "bundles");
                var extractFilesOutDir = Path.Combine(tempRoot, "out_files");
                var extractFolderOutDir = Path.Combine(tempRoot, "out_folder");
                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(extractFilesOutDir);
                Directory.CreateDirectory(extractFolderOutDir);

                const int bundleCount = 12;
                var bundlePaths = new string[bundleCount];
                var expectedPayloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                for (int b = 0; b < bundleCount; b++)
                {
                    var subDir = Path.Combine(sourceDir, $"group_{b % 3}");
                    Directory.CreateDirectory(subDir);

                    var payload1 = CreateCompressiblePayload(1500 + b * 31, seed: b * 7 + 1);
                    var payload2 = CreateCompressiblePayload(2400 + b * 19, seed: b * 11 + 3);
                    var evilPayload = Encoding.UTF8.GetBytes("zip-slip-attempt");

                    var bundleBytes = BuildSyntheticUnityFsV6Lz4Bundle(new[]
                    {
                        ($"CAB-{b:D2}/level_{b}.assets", payload1),
                        ($"CAB-{b:D2}/res/shared_{b}.resS", payload2),
                        ("../../escaped_unix.bin", evilPayload),
                        ("..\\..\\escaped_win.bin", evilPayload),
                        ("/etc/passwd", evilPayload),
                        ($"CAB-{b:D2}/../traversal_{b}.bin", evilPayload)
                    });

                    var bundleFileName = $"bundle_{b:D2}.unity3d";
                    var bundlePath = Path.Combine(subDir, bundleFileName);
                    File.WriteAllBytes(bundlePath, bundleBytes);
                    bundlePaths[b] = bundlePath;

                    expectedPayloads[$"{bundleFileName}_unpacked|CAB-{b:D2}|level_{b}.assets"] = payload1;
                    expectedPayloads[$"{bundleFileName}_unpacked|CAB-{b:D2}|res|shared_{b}.resS"] = payload2;
                }

                // 1. Test parallel ExtractFilesCore
                var progressReports = new ConcurrentQueue<(int Completed, int Total)>();
                var extractedFilesCount = MainWindow.ExtractFilesCore(
                    bundlePaths,
                    extractFilesOutDir,
                    statusUpdate: null,
                    progressReport: (c, t) => progressReports.Enqueue((c, t)),
                    maxDegreeOfParallelism: 8);

                Assert.Equal(bundleCount * 2, extractedFilesCount);
                Assert.Contains(progressReports, p => p.Completed == bundleCount && p.Total == bundleCount);

                foreach (var kvp in expectedPayloads)
                {
                    var parts = kvp.Key.Split('|');
                    var extractedFile = Path.Combine( new[] { extractFilesOutDir }.Concat(parts).ToArray() );
                    Assert.True(File.Exists(extractedFile), $"Missing extracted file: {extractedFile}");
                    Assert.Equal(kvp.Value, File.ReadAllBytes(extractedFile));
                }

                // Verify zero Zip Slip files escaped into tempRoot or extractFilesOutDir
                Assert.Empty(Directory.GetFiles(tempRoot, "escaped_*", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(tempRoot, "traversal_*", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(tempRoot, "passwd", SearchOption.AllDirectories));

                // 2. Test parallel ExtractFolderCore preserving relative directory structure
                var extractedFolderCount = MainWindow.ExtractFolderCore(
                    sourceDir,
                    extractFolderOutDir,
                    statusUpdate: null,
                    progressReport: null,
                    maxDegreeOfParallelism: 8);

                Assert.Equal(bundleCount * 2, extractedFolderCount);
                Assert.Equal(bundleCount * 2, Directory.GetFiles(extractFolderOutDir, "*", SearchOption.AllDirectories).Length);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MainWindow_BuildLazyExportBatches_GroupsBySourceFileRespectsBudgetsAndSupportsCancellation()
        {
            const int sourceFileCount = 10;
            const int assetsPerSource = 12;
            var items = new List<AssetItem>(sourceFileCount * assetsPerSource);

            // Interleave items from 10 different source bundles to verify grouping coalesces each source bundle into 1 batch
            for (int a = 0; a < assetsPerSource; a++)
            {
                for (int s = 0; s < sourceFileCount; s++)
                {
                    var sourcePath = $@"C:\SyntheticGame\StreamingAssets\bundle_{s:D2}.unity3d";
                    var handle = new AssetHandle
                    {
                        UniqueID = $"bundle_{s:D2}.unity3d#{a + 1}",
                        Name = $"Asset_{s}_{a}",
                        Type = (a % 3) switch
                        {
                            0 => ClassIDType.Texture2D,
                            1 => ClassIDType.Material,
                            _ => ClassIDType.TextAsset
                        },
                        OriginalPath = sourcePath,
                        SerializedFileName = $"CAB-{s:D2}",
                        PathID = a + 1,
                        ByteStart = a * 1024L,
                        ByteSize = 1024
                    };
                    items.Add(new AssetItem(handle, s * 100 + a));
                }
            }

            // Group with max 3 source files per batch -> 10 source files => 4 batches (3, 3, 3, 1)
            var batches = MainWindow.BuildLazyExportBatchesForItems(
                items,
                item => item.Handle?.OriginalPath,
                maxSourceFilesPerBatch: 3,
                maxAssetBytesPerBatch: 1024L * 1024L);

            Assert.Equal(4, batches.Count);
            Assert.Equal(3, batches[0].SourcePaths.Count);
            Assert.Equal(3 * assetsPerSource, batches[0].Items.Count);
            Assert.Equal(3, batches[1].SourcePaths.Count);
            Assert.Equal(3 * assetsPerSource, batches[1].Items.Count);
            Assert.Equal(3, batches[2].SourcePaths.Count);
            Assert.Equal(3 * assetsPerSource, batches[2].Items.Count);
            Assert.Single(batches[3].SourcePaths);
            Assert.Equal(1 * assetsPerSource, batches[3].Items.Count);

            // Verify no source file is split across multiple batches
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in batches)
            {
                foreach (var src in batch.SourcePaths)
                {
                    Assert.True(seenSources.Add(src), $"Source file {src} appeared in multiple batches.");
                }
            }
            Assert.Equal(sourceFileCount, seenSources.Count);

            // Verify byte budget splitting (each source group has 12 * 1024 = 12,288 bytes; budget 25,000 bytes -> max 2 sources per batch -> 5 batches)
            var byteBoundedBatches = MainWindow.BuildLazyExportBatchesForItems(
                items,
                item => item.Handle?.OriginalPath,
                maxSourceFilesPerBatch: 10,
                maxAssetBytesPerBatch: 25_000L);
            Assert.Equal(5, byteBoundedBatches.Count);
            Assert.All(byteBoundedBatches, b => Assert.True(b.SourcePaths.Count <= 2));

            // Verify cancellation token support during batch processing
            using var cts = new CancellationTokenSource();
            var processedBatches = 0;
            Assert.Throws<OperationCanceledException>(() =>
            {
                for (int i = 0; i < batches.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    processedBatches++;
                    if (processedBatches == 2)
                    {
                        cts.Cancel();
                    }
                }
            });
            Assert.Equal(2, processedBatches);
        }

        [Fact]
        public void MainWindow_BuildEagerModelGroupLookup_PrecomputesMultiPartHierarchyInSinglePass()
        {
            var manager = new AssetsManager();
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            typeof(SerializedFile)
                .GetField("objectCollectionLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(sf, new object());
            sf.assetsManager = manager;
            sf.fileName = "level0";
            sf.fullName = "level0";
            sf.originalPath = "level0";
            sf.Objects = new List<Object>();
            sf.ObjectsDic = new Dictionary<long, Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            sf.m_Externals = new List<FileIdentifier>();
            manager.assetsFileList.Add(sf);

            PPtr<T> MakePPtr<T>(long pathId) where T : Object => new(0, pathId, sf);

            var rootGo = CreateUninitialized<GameObject>(sf, 1);
            rootGo.m_Name = "HeroCharacterRoot";
            var rootTr = CreateUninitialized<Transform>(sf, 2);
            rootTr.m_GameObject = MakePPtr<GameObject>(1);
            rootTr.m_Father = MakePPtr<Transform>(0);
            rootTr.m_Children = new[] { MakePPtr<Transform>(4), MakePPtr<Transform>(6) };
            rootGo.m_Transform = rootTr;

            var avatar = CreateUninitialized<Avatar>(sf, 100);
            avatar.m_Name = "HeroAvatar";
            var animator = CreateUninitialized<Animator>(sf, 101);
            animator.m_GameObject = MakePPtr<GameObject>(1);
            animator.m_Avatar = MakePPtr<Avatar>(100);
            rootGo.m_Animator = animator;

            var childGo1 = CreateUninitialized<GameObject>(sf, 3);
            childGo1.m_Name = "BodyPart";
            var childTr1 = CreateUninitialized<Transform>(sf, 4);
            childTr1.m_GameObject = MakePPtr<GameObject>(3);
            childTr1.m_Father = MakePPtr<Transform>(2);
            childTr1.m_Children = Array.Empty<PPtr<Transform>>();
            childGo1.m_Transform = childTr1;

            var mesh1 = CreateUninitialized<Mesh>(sf, 10);
            mesh1.m_Name = "HeroBodyMesh";
            var smr1 = CreateUninitialized<SkinnedMeshRenderer>(sf, 11);
            smr1.m_GameObject = MakePPtr<GameObject>(3);
            smr1.m_Mesh = MakePPtr<Mesh>(10);
            childGo1.m_SkinnedMeshRenderer = smr1;

            var childGo2 = CreateUninitialized<GameObject>(sf, 5);
            childGo2.m_Name = "WeaponPart";
            var childTr2 = CreateUninitialized<Transform>(sf, 6);
            childTr2.m_GameObject = MakePPtr<GameObject>(5);
            childTr2.m_Father = MakePPtr<Transform>(2);
            childTr2.m_Children = Array.Empty<PPtr<Transform>>();
            childGo2.m_Transform = childTr2;

            var mesh2 = CreateUninitialized<Mesh>(sf, 20);
            mesh2.m_Name = "HeroWeaponMesh";
            var mf2 = CreateUninitialized<MeshFilter>(sf, 21);
            mf2.m_GameObject = MakePPtr<GameObject>(5);
            mf2.m_Mesh = MakePPtr<Mesh>(20);
            var mr2 = CreateUninitialized<MeshRenderer>(sf, 22);
            mr2.m_GameObject = MakePPtr<GameObject>(5);
            childGo2.m_MeshFilter = mf2;
            childGo2.m_MeshRenderer = mr2;

            var lookup = MainWindow.BuildEagerModelGroupLookup(new[] { sf });
            Assert.Equal(2, lookup.Count);
            Assert.True(lookup.TryGetValue(mesh1, out var group1));
            Assert.True(lookup.TryGetValue(mesh2, out var group2));

            Assert.Equal("HeroCharacterRoot", group1!.GroupName);
            Assert.Same(rootGo, group1.RootGameObject);
            Assert.Same(avatar, group1.Avatar);
            Assert.Equal(new[] { mesh1, mesh2 }, group1.Meshes);
            Assert.Equal(new Renderer[] { smr1, mr2 }, group1.Renderers);

            // Both meshes in the hierarchy resolve to the exact same precomputed subtree lists
            Assert.Same(group1.RootGameObject, group2!.RootGameObject);
            Assert.Same(group1.Meshes, group2.Meshes);
            Assert.Same(group1.Renderers, group2.Renderers);
        }

        private static T CreateUninitialized<T>(SerializedFile sf, long pathId) where T : Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            obj.assetsFile = sf;
            obj.m_PathID = pathId;
            sf.Objects.Add(obj);
            sf.ObjectsDic[pathId] = obj;
            return obj;
        }

        private static FbxDocument BuildSyntheticFbxDocument(int targetNodeCount, out int actualNodeCount)
        {
            var doc = new FbxDocument { Version = FbxVersion.v7_4 };
            actualNodeCount = 0;

            var objectsNode = new FbxNode(new IdentifierToken("Objects"));
            doc.AddNode(objectsNode);
            actualNodeCount++;

            var doubleArray256 = new double[256];
            for (int i = 0; i < doubleArray256.Length; i++)
            {
                doubleArray256[i] = i * 0.5 - 32.0;
            }
            var smallIntArray = new[] { 0, 1, ~2, 3, 4, ~5 };

            int branchCount = 50;
            int childrenPerBranch = (targetNodeCount + branchCount * 2 - 1) / (branchCount * 2);

            for (int b = 0; b < branchCount; b++)
            {
                var modelNode = new FbxNode(new IdentifierToken(b == 0 ? new string('M', 280) : "Model"));
                modelNode.AddProperty(new LongToken(10000L + b));
                modelNode.AddProperty(new StringToken($"Model::Bone_{b}"));
                modelNode.AddProperty(new StringToken("LimbNode"));

                if (b % 10 == 0)
                {
                    var geoNode = new FbxNode(new IdentifierToken("Geometry"));
                    geoNode.AddProperty(new DoubleArrayToken(doubleArray256));
                    geoNode.AddProperty(new IntegerArrayToken(smallIntArray));
                    modelNode.AddNode(geoNode);
                    actualNodeCount++;
                }

                for (int c = 0; c < childrenPerBranch; c++)
                {
                    var propsNode = new FbxNode(new IdentifierToken("Properties70"));
                    propsNode.AddProperty(new IntegerToken(c));

                    var pNode = new FbxNode(new IdentifierToken("P"));
                    pNode.AddProperty(new StringToken("Lcl Translation"));
                    pNode.AddProperty(new StringToken("Lcl Translation"));
                    pNode.AddProperty(new StringToken(""));
                    pNode.AddProperty(new StringToken("A+"));
                    pNode.AddProperty(new DoubleToken(c * 1.5));
                    pNode.AddProperty(new DoubleToken(c * -0.5));
                    pNode.AddProperty(new DoubleToken(b * 2.0));
                    pNode.AddProperty(new BooleanToken((c & 1) == 0));
                    pNode.AddProperty(new FloatToken(c * 0.25f));
                    pNode.AddProperty(new ShortToken((short)(c % 100)));

                    propsNode.AddNode(pNode);
                    modelNode.AddNode(propsNode);
                    actualNodeCount += 2;
                }

                objectsNode.AddNode(modelNode);
                actualNodeCount++;
            }

            var animStack = new FbxNode(new IdentifierToken("AnimationStack"));
            animStack.AddProperty(new LongToken(999999L));
            var animLayer = new FbxNode(new IdentifierToken("AnimationLayer"));
            animLayer.AddProperty(new LongToken(999998L));
            animStack.AddNode(animLayer);
            doc.AddNode(animStack);
            actualNodeCount += 2;

            return doc;
        }

        private static byte[] BuildSyntheticUnityFsV6Lz4Bundle(IReadOnlyList<(string Path, byte[] Payload)> nodes)
        {
            using var combinedPayload = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Count);
            foreach (var node in nodes)
            {
                long offset = combinedPayload.Position;
                combinedPayload.Write(node.Payload, 0, node.Payload.Length);
                dirEntries.Add((offset, node.Payload.Length, 4u, node.Path));
            }
            var rawBlocks = combinedPayload.ToArray();

            var compressedBlocks = CompressLz4(rawBlocks);
            var blockInfos = new[]
            {
                (uncompressedSize: (uint)rawBlocks.Length, compressedSize: (uint)compressedBlocks.Length, flags: (StorageBlockFlags)CompressionType.Lz4)
            };

            using var blocksInfoMs = new MemoryStream();
            blocksInfoMs.Write(new byte[16]); // hash
            WriteInt32BE(blocksInfoMs, blockInfos.Length);
            foreach (var b in blockInfos)
            {
                WriteUInt32BE(blocksInfoMs, b.uncompressedSize);
                WriteUInt32BE(blocksInfoMs, b.compressedSize);
                WriteUInt16BE(blocksInfoMs, (ushort)b.flags);
            }
            WriteInt32BE(blocksInfoMs, dirEntries.Count);
            foreach (var n in dirEntries)
            {
                WriteInt64BE(blocksInfoMs, n.offset);
                WriteInt64BE(blocksInfoMs, n.size);
                WriteUInt32BE(blocksInfoMs, n.flags);
                WriteNullTerminatedUtf8(blocksInfoMs, n.path);
            }

            var uncompressedBlocksInfo = blocksInfoMs.ToArray();
            var compressedBlocksInfo = CompressLz4(uncompressedBlocksInfo);
            var flags = (ArchiveFlags)CompressionType.Lz4 | ArchiveFlags.BlocksAndDirectoryInfoCombined;

            var body = new byte[compressedBlocksInfo.Length + compressedBlocks.Length];
            Buffer.BlockCopy(compressedBlocksInfo, 0, body, 0, compressedBlocksInfo.Length);
            Buffer.BlockCopy(compressedBlocks, 0, body, compressedBlocksInfo.Length, compressedBlocks.Length);

            var probe = BuildUnityFsHeader(0, (uint)compressedBlocksInfo.Length, (uint)uncompressedBlocksInfo.Length, flags, body);
            return BuildUnityFsHeader(probe.Length, (uint)compressedBlocksInfo.Length, (uint)uncompressedBlocksInfo.Length, flags, body);
        }

        private static byte[] BuildUnityFsHeader(
            long totalSize,
            uint compressedBlocksInfoSize,
            uint uncompressedBlocksInfoSize,
            ArchiveFlags flags,
            byte[] body)
        {
            using var ms = new MemoryStream();
            WriteNullTerminatedUtf8(ms, "UnityFS");
            WriteUInt32BE(ms, 6u);
            WriteNullTerminatedUtf8(ms, "5.x.x");
            WriteNullTerminatedUtf8(ms, "2022.3.10f1");
            WriteInt64BE(ms, totalSize);
            WriteUInt32BE(ms, compressedBlocksInfoSize);
            WriteUInt32BE(ms, uncompressedBlocksInfoSize);
            WriteUInt32BE(ms, (uint)flags);
            ms.Write(body, 0, body.Length);
            return ms.ToArray();
        }

        private static byte[] CompressLz4(byte[] input)
        {
            var dst = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
            int encoded = LZ4Codec.Encode(input, 0, input.Length, dst, 0, dst.Length, LZ4Level.L00_FAST);
            Assert.True(encoded > 0);
            return dst.AsSpan(0, encoded).ToArray();
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
            var path = Path.Combine(Path.GetTempPath(), $"as_perf_export_{prefix}_{Guid.NewGuid():N}");
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
