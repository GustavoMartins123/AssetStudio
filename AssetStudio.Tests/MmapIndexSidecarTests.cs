using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class MmapIndexSidecarTests
    {
        private readonly ITestOutputHelper _output;

        public MmapIndexSidecarTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void SaveAndLoadIndexCache_MmapSidecarAndSqliteFallback_Have100PercentFieldForFieldParity()
        {
            var tempRoot = CreateTempDirectory("mmap_parity");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount: 4);
                using (cache)
                {
                    var handles = new List<AssetHandle>();
                    var unicodeNames = new[]
                    {
                        "Hero_角色_メッシュ_01",
                        "Weapon_🔥_Épée_Ñoño",
                        "Shader/Standard (Specular Setup) [LOD0]",
                        string.Empty,
                        "UI_Кнопка_아이콘_Asset"
                    };

                    for (var i = 0; i < 120; i++)
                    {
                        var srcIndex = i % sourcePaths.Length;
                        var serializedFileName = srcIndex == 0 ? "level0" : $"sharedassets{srcIndex}.assets";
                        var originalPath = sourcePaths[srcIndex];
                        var pathId = (i % 7 == 0)
                            ? -(100000L + (i * 9973L))
                            : (i == 0 ? long.MinValue + 1 : 1000L + (i * 65537L));
                        var byteStart = (long)int.MaxValue + (i * 131072L);
                        var byteSize = (i % 11 == 0) ? 0L : (4096L + (i * 257L));
                        var container = (i % 4 == 0)
                            ? string.Empty
                            : $"assets/bundles/region_{srcIndex}/prefab_{i % 9}_アセット.prefab";

                        handles.Add(new AssetHandle
                        {
                            UniqueID = AssetHandle.BuildUniqueID(serializedFileName, originalPath, pathId),
                            Name = $"{unicodeNames[i % unicodeNames.Length]}_{i:D4}",
                            Type = (i % 6) switch
                            {
                                0 => ClassIDType.Mesh,
                                1 => ClassIDType.Material,
                                2 => ClassIDType.Texture2D,
                                3 => ClassIDType.AnimationClip,
                                4 => ClassIDType.GameObject,
                                _ => ClassIDType.MonoBehaviour
                            },
                            Container = container,
                            OriginalPath = originalPath,
                            SerializedFileName = serializedFileName,
                            PathID = pathId,
                            ByteStart = byteStart,
                            ByteSize = byteSize
                        });
                    }

                    // Also include a duplicate UniqueID to verify identical deduplication behavior
                    var dupTarget = handles[15];
                    handles.Add(new AssetHandle
                    {
                        UniqueID = dupTarget.UniqueID,
                        Name = "Overwritten_Duplicate_Name_日本語",
                        Type = ClassIDType.Avatar,
                        Container = "assets/override/hero_avatar.fbx",
                        OriginalPath = dupTarget.OriginalPath,
                        SerializedFileName = dupTarget.SerializedFileName,
                        PathID = dupTarget.PathID,
                        ByteStart = 9_876_543_210L,
                        ByteSize = 123_456L
                    });

                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", handles, preserveSemanticRelations: false));

                    var idxPath = cache.GetIdxPath(projectDir);
                    Assert.True(File.Exists(idxPath));
                    Assert.False(File.Exists(MmapProjectIndexSidecar.GetTempSidecarPath(idxPath)));

                    // 1) Load via .idx mmap fast path
                    var mmapLoaded = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(mmapLoaded);

                    // 2) Load directly via SQLite-only fallback path
                    var sqliteLoaded = cache.LoadIndexCacheFromSqliteOnly(projectDir, signature);
                    Assert.NotNull(sqliteLoaded);

                    // 3) Delete .idx and verify public LoadIndexCache falls back to SQLite with identical output
                    File.Delete(idxPath);
                    Assert.False(File.Exists(idxPath));
                    var fallbackLoaded = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(fallbackLoaded);

                    AssertHandleListsIdentical(sqliteLoaded!, mmapLoaded!);
                    AssertHandleListsIdentical(sqliteLoaded!, fallbackLoaded!);

                    // Verify per-SourceFile string reference sharing in mmap-loaded handles
                    var level0Handles = mmapLoaded!.Where(h => h.SerializedFileName == "level0").ToList();
                    Assert.True(level0Handles.Count > 1);
                    Assert.All(level0Handles, h => Assert.Same(level0Handles[0].OriginalPath, h.OriginalPath));
                    Assert.All(level0Handles, h => Assert.Same(level0Handles[0].SerializedFileName, h.SerializedFileName));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void CorruptTruncatedOrStaleSidecar_SeamlesslyFallsBackToSqliteWithoutThrowing()
        {
            var tempRoot = CreateTempDirectory("mmap_corruption_fallback");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount: 2);
                using (cache)
                {
                    var handles = BuildSyntheticHandles(sourcePaths, count: 64);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", handles, preserveSemanticRelations: false));

                    var idxPath = cache.GetIdxPath(projectDir);
                    var validBytes = File.ReadAllBytes(idxPath);
                    var expectedReference = cache.LoadIndexCacheFromSqliteOnly(projectDir, signature)!;
                    Assert.Equal(64, expectedReference.Count);

                    // Case 1: Flipped byte in payload (string pool / records)
                    var flippedPayload = (byte[])validBytes.Clone();
                    flippedPayload[flippedPayload.Length - 5] ^= 0xFF;
                    File.WriteAllBytes(idxPath, flippedPayload);
                    Assert.False(MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SQLiteProjectIndexCache.SemanticSchemaVersion, out _));
                    var loadedAfterPayloadFlip = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(loadedAfterPayloadFlip);
                    AssertHandleListsIdentical(expectedReference, loadedAfterPayloadFlip!);

                    // Case 2: Flipped byte in header (magic / version / counts)
                    var flippedHeader = (byte[])validBytes.Clone();
                    flippedHeader[0] ^= 0x01;
                    File.WriteAllBytes(idxPath, flippedHeader);
                    Assert.False(MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SQLiteProjectIndexCache.SemanticSchemaVersion, out _));
                    var loadedAfterHeaderFlip = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(loadedAfterHeaderFlip);
                    AssertHandleListsIdentical(expectedReference, loadedAfterHeaderFlip!);

                    // Case 3: Truncated file (half length & shorter than header)
                    File.WriteAllBytes(idxPath, validBytes.AsSpan(0, validBytes.Length / 2).ToArray());
                    Assert.False(MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SQLiteProjectIndexCache.SemanticSchemaVersion, out _));
                    var loadedAfterTruncate = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(loadedAfterTruncate);
                    AssertHandleListsIdentical(expectedReference, loadedAfterTruncate!);

                    File.WriteAllBytes(idxPath, validBytes.AsSpan(0, 16).ToArray());
                    Assert.False(MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SQLiteProjectIndexCache.SemanticSchemaVersion, out _));
                    AssertHandleListsIdentical(expectedReference, cache.LoadIndexCache(projectDir, signature)!);

                    // Case 4: Stale signature inside .idx sidecar while SQLite has current signature
                    Assert.True(MmapProjectIndexSidecar.TryWriteSidecar(
                        idxPath,
                        signature + "_stale_sidecar",
                        SQLiteProjectIndexCache.SemanticSchemaVersion,
                        handles));
                    Assert.False(MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SQLiteProjectIndexCache.SemanticSchemaVersion, out _));
                    var loadedAfterStaleSig = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(loadedAfterStaleSig);
                    AssertHandleListsIdentical(expectedReference, loadedAfterStaleSig!);

                    // Case 5: Stale SchemaVersion in .idx sidecar
                    Assert.True(MmapProjectIndexSidecar.TryWriteSidecar(
                        idxPath,
                        signature,
                        SQLiteProjectIndexCache.SemanticSchemaVersion - 1,
                        handles));
                    Assert.False(MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SQLiteProjectIndexCache.SemanticSchemaVersion, out _));
                    AssertHandleListsIdentical(expectedReference, cache.LoadIndexCache(projectDir, signature)!);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void OverwriteAndDeleteIndexCache_HaveZeroFileLocksOnWindowsAndCleanUpAllSidecars()
        {
            var tempRoot = CreateTempDirectory("mmap_overwrite_delete");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount: 2);
                using (cache)
                {
                    var dbPath = cache.GetDbPath(projectDir);
                    var idxPath = cache.GetIdxPath(projectDir);
                    var tempIdxPath = MmapProjectIndexSidecar.GetTempSidecarPath(idxPath);

                    var v1Handles = BuildSyntheticHandles(sourcePaths, count: 100);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", v1Handles));
                    Assert.True(File.Exists(idxPath));

                    // Repeatedly load via mmap and immediately overwrite via SaveIndexCache -> zero sharing violations on Windows
                    for (var round = 0; round < 5; round++)
                    {
                        var loaded = cache.LoadIndexCache(projectDir, signature);
                        Assert.NotNull(loaded);
                        Assert.Equal(100 + round, loaded!.Count);

                        // Also open & dispose low-level zero-deserialization snapshot reader
                        Assert.True(MmapProjectIndexSidecar.TryOpenSnapshot(
                            idxPath,
                            signature,
                            SQLiteProjectIndexCache.SemanticSchemaVersion,
                            out var snapshot));
                        Assert.NotNull(snapshot);
                        using (snapshot!)
                        {
                            Assert.Equal(100 + round, snapshot.RecordCount);
                            Assert.Equal(2, snapshot.SourceFileCount);
                            Assert.True(snapshot.Records.Length == 100 + round);
                            Assert.True(snapshot.StringPool.Length > 0);
                        }

                        var nextHandles = BuildSyntheticHandles(sourcePaths, count: 101 + round);
                        Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", nextHandles));
                    }

                    // Plant legacy .idx.tmp and unique .idx.<pid>.<guid>.tmp files to verify DeleteIndexCache cleans all of them
                    var uniqueTempIdxPath = MmapProjectIndexSidecar.CreateUniqueTempSidecarPath(idxPath);
                    File.WriteAllBytes(tempIdxPath, new byte[] { 1, 2, 3 });
                    File.WriteAllBytes(uniqueTempIdxPath, new byte[] { 4, 5, 6 });
                    Assert.True(File.Exists(tempIdxPath));
                    Assert.True(File.Exists(uniqueTempIdxPath));

                    cache.DeleteIndexCache(projectDir);

                    Assert.False(File.Exists(dbPath));
                    Assert.False(File.Exists(dbPath + "-wal"));
                    Assert.False(File.Exists(dbPath + "-shm"));
                    Assert.False(File.Exists(idxPath));
                    Assert.False(File.Exists(tempIdxPath));
                    Assert.False(File.Exists(uniqueTempIdxPath));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AllFourReadCallers_UseRentReadConnectionPool_AndPragmaOptimizeGuardsReadOnlyConnections()
        {
            var tempRoot = CreateTempDirectory("read_pool_all_four");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount: 1);
                using (cache)
                {
                    var handles = BuildSyntheticHandles(sourcePaths, count: 16);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", handles));

                    var relations = new SemanticAssetRelations();
                    relations.SourceFiles.Add(new SemanticSourceFileEntry("level0", sourcePaths[0], "2022.3.20f1", handles.Count));
                    relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                        handles[0].UniqueID, handles[0].UniqueID, "_MainTex", 0, handles[1].UniqueID, 0, handles[1].PathID, isResolved: true, isMainTexture: true));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                    cache.SavePreviewCachePayload(
                        projectDir,
                        signature,
                        handles[0].UniqueID,
                        "texture-thumbnail-png",
                        1,
                        "256",
                        new byte[] { 10, 20, 30, 40, 50, 60 });

                    Assert.Equal(0L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(0L, cache.ReadPoolHits);

                    // 1) LoadIndexCache (IndexIo.cs)
                    var loadedHandles = cache.LoadIndexCache(projectDir, signature);
                    Assert.NotNull(loadedHandles);
                    Assert.Equal(16, loadedHandles!.Count);
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(0L, cache.ReadPoolHits);

                    // 2) LoadPreviewCacheEntry (PreviewCache.cs)
                    var previewEntry = cache.LoadPreviewCacheEntry(projectDir, signature, handles[0].UniqueID, "texture-thumbnail-png", 1, "256");
                    Assert.NotNull(previewEntry);
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(1L, cache.ReadPoolHits);

                    // 3) GetPreviewCacheStats (PreviewCache.cs)
                    var stats = cache.GetPreviewCacheStats(projectDir);
                    Assert.Equal(1, stats.EntryCount);
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(2L, cache.ReadPoolHits);

                    // 4) HasSemanticRelations (SemanticQueries.cs)
                    Assert.True(cache.HasSemanticRelations(projectDir, signature));
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(3L, cache.ReadPoolHits);

                    // Verify TryOptimizeDatabase succeeds on read-write connection and safely skips read-only pooled connection
                    var dbPath = cache.GetDbPath(projectDir);
                    using (var rwConn = cache.CreateConnection(projectDir))
                    {
                        SQLiteProjectIndexCache.TryOptimizeDatabase(rwConn, "unit test rw");
                    }

                    using (var roLease = cache.RentReadConnection(dbPath))
                    {
                        SQLiteProjectIndexCache.TryOptimizeDatabase(roLease.Connection, "unit test ro");
                    }
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Benchmark_50000SyntheticAssetHandles_MmapSidecarVsSqliteWarmLoad()
        {
            var tempRoot = CreateTempDirectory("mmap_bench_50k");
            try
            {
                const int handleCount = 50_000;
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount: 16);
                using (cache)
                {
                    var handles = BuildSyntheticHandles(sourcePaths, handleCount);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", handles, preserveSemanticRelations: false));

                    var idxPath = cache.GetIdxPath(projectDir);
                    var dbPath = cache.GetDbPath(projectDir);
                    var idxSizeKiB = new FileInfo(idxPath).Length / 1024.0;
                    var dbSizeKiB = new FileInfo(dbPath).Length / 1024.0;

                    // Warm-up both paths once
                    var warmMmap = cache.LoadIndexCache(projectDir, signature);
                    var warmSqlite = cache.LoadIndexCacheFromSqliteOnly(projectDir, signature);
                    Assert.NotNull(warmMmap);
                    Assert.NotNull(warmSqlite);
                    Assert.Equal(handleCount, warmMmap!.Count);
                    Assert.Equal(handleCount, warmSqlite!.Count);

                    const int iterations = 5;
                    var swSqlite = Stopwatch.StartNew();
                    for (var i = 0; i < iterations; i++)
                    {
                        var list = cache.LoadIndexCacheFromSqliteOnly(projectDir, signature);
                        Assert.Equal(handleCount, list!.Count);
                    }
                    swSqlite.Stop();

                    var swMmap = Stopwatch.StartNew();
                    for (var i = 0; i < iterations; i++)
                    {
                        var list = cache.LoadIndexCache(projectDir, signature);
                        Assert.Equal(handleCount, list!.Count);
                    }
                    swMmap.Stop();

                    var sqliteAvgMs = swSqlite.Elapsed.TotalMilliseconds / iterations;
                    var mmapAvgMs = swMmap.Elapsed.TotalMilliseconds / iterations;
                    var speedup = sqliteAvgMs / Math.Max(0.001, mmapAvgMs);

                    _output.WriteLine(
                        $"50,000 AssetHandles Warm Load (avg of {iterations} runs): SQLite={sqliteAvgMs:F2} ms ({dbSizeKiB:F0} KiB), " +
                        $"Mmap .idx={mmapAvgMs:F2} ms ({idxSizeKiB:F0} KiB), Speedup={speedup:F2}x");

                    Assert.True(mmapAvgMs < sqliteAvgMs, $"Expected mmap .idx warm load ({mmapAvgMs:F2} ms) to be faster than SQLite ({sqliteAvgMs:F2} ms).");
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void AssertHandleListsIdentical(IReadOnlyList<AssetHandle> expected, IReadOnlyList<AssetHandle> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var exp = expected[i];
                var act = actual[i];
                Assert.Equal(exp.UniqueID, act.UniqueID);
                Assert.Equal(exp.Name, act.Name);
                Assert.Equal(exp.Type, act.Type);
                Assert.Equal(exp.Container, act.Container);
                Assert.Equal(exp.OriginalPath, act.OriginalPath);
                Assert.Equal(exp.SerializedFileName, act.SerializedFileName);
                Assert.Equal(exp.PathID, act.PathID);
                Assert.Equal(exp.ByteStart, act.ByteStart);
                Assert.Equal(exp.ByteSize, act.ByteSize);
            }
        }

        private static List<AssetHandle> BuildSyntheticHandles(string[] sourcePaths, int count)
        {
            var handles = new List<AssetHandle>(count);
            var containers = new string[64];
            for (var c = 0; c < containers.Length; c++)
            {
                containers[c] = c % 8 == 0
                    ? string.Empty
                    : $"assets/characters/group_{c / 4}/model_{c:D2}.prefab";
            }

            for (var i = 0; i < count; i++)
            {
                var srcIdx = i % sourcePaths.Length;
                var serializedFileName = srcIdx == 0 ? "level0" : $"sharedassets{srcIdx}.assets";
                var originalPath = sourcePaths[srcIdx];
                var pathId = 1000L + i;
                handles.Add(new AssetHandle
                {
                    UniqueID = AssetHandle.BuildUniqueID(serializedFileName, originalPath, pathId),
                    Name = $"Asset_{i % 512:D4}",
                    Type = (i % 4) switch
                    {
                        0 => ClassIDType.Mesh,
                        1 => ClassIDType.Material,
                        2 => ClassIDType.Texture2D,
                        _ => ClassIDType.GameObject
                    },
                    Container = containers[i & 63],
                    OriginalPath = originalPath,
                    SerializedFileName = serializedFileName,
                    PathID = pathId,
                    ByteStart = i * 256L,
                    ByteSize = 128L + (i & 1023)
                });
            }

            return handles;
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, ProjectScanResult ScanResult, string[] SourcePaths)
            CreateSampleProject(string tempRoot, int sourceFileCount)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = Path.Combine(tempRoot, "SampleProject");
            Directory.CreateDirectory(projectDir);

            var count = Math.Max(1, sourceFileCount);
            var sourcePaths = new string[count];
            for (var i = 0; i < count; i++)
            {
                var fileName = i == 0 ? "level0" : $"sharedassets{i}.assets";
                var path = Path.Combine(projectDir, fileName);
                File.WriteAllBytes(path, new byte[32 + (i * 8)]);
                sourcePaths[i] = path;
            }

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePaths);
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
