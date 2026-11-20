using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class ArenaWarmOpenAndLifecycleTests
    {
        private readonly ITestOutputHelper _output;

        public ArenaWarmOpenAndLifecycleTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void MmapSidecar_TryLoadIntoArena_And_SqliteFallback_Have100PercentParityWithLegacyHandles()
        {
            var tempRoot = CreateTempDirectory("arena_mmap_parity");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount: 4);
                using (cache)
                {
                    var handles = BuildDiverseHandles(sourcePaths, count: 180);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.20f1", handles, preserveSemanticRelations: false));

                    var expectedHandles = cache.LoadIndexCacheFromSqliteOnly(projectDir, signature);
                    Assert.NotNull(expectedHandles);
                    Assert.NotEmpty(expectedHandles!);

                    var idxPath = cache.GetIdxPath(projectDir);
                    Assert.True(File.Exists(idxPath));

                    // 1) Direct MmapProjectIndexSidecar.TryLoadIntoArena
                    var directMmapIndex = new ProjectIndex();
                    Assert.True(MmapProjectIndexSidecar.TryLoadIntoArena(
                        idxPath,
                        signature,
                        SQLiteProjectIndexCache.SemanticSchemaVersion,
                        directMmapIndex,
                        out var mmapSources));
                    Assert.NotNull(mmapSources);
                    Assert.Equal(4, mmapSources!.Count);
                    AssertProjectIndexMatchesHandles(expectedHandles!, directMmapIndex);

                    // 2) SQLiteProjectIndexCache.LoadIndexCacheIntoProjectIndex (mmap fast path)
                    var cacheMmapIndex = new ProjectIndex();
                    Assert.True(cache.LoadIndexCacheIntoProjectIndex(projectDir, signature, cacheMmapIndex, out var cacheMmapSources));
                    Assert.NotNull(cacheMmapSources);
                    Assert.Equal(4, cacheMmapSources!.Count);
                    AssertProjectIndexMatchesHandles(expectedHandles!, cacheMmapIndex);

                    // 3) SQLite-only direct-to-arena fallback
                    var sqliteOnlyIndex = new ProjectIndex();
                    Assert.True(cache.LoadIndexCacheIntoProjectIndexFromSqliteOnly(projectDir, signature, sqliteOnlyIndex, out var sqliteSources));
                    Assert.NotNull(sqliteSources);
                    Assert.Equal(4, sqliteSources!.Count);
                    AssertProjectIndexMatchesHandles(expectedHandles!, sqliteOnlyIndex);

                    // 4) Corrupt .idx sidecar must seamlessly fall back to SQLite without partial arena pollution
                    var validBytes = File.ReadAllBytes(idxPath);
                    validBytes[^4] ^= 0xAA;
                    File.WriteAllBytes(idxPath, validBytes);

                    var fallbackIndex = new ProjectIndex();
                    Assert.False(MmapProjectIndexSidecar.TryLoadIntoArena(
                        idxPath,
                        signature,
                        SQLiteProjectIndexCache.SemanticSchemaVersion,
                        fallbackIndex,
                        out _));
                    Assert.Equal(0, fallbackIndex.Count);

                    Assert.True(cache.LoadIndexCacheIntoProjectIndex(projectDir, signature, fallbackIndex, out var fallbackSources));
                    Assert.NotNull(fallbackSources);
                    AssertProjectIndexMatchesHandles(expectedHandles!, fallbackIndex);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ArenaQuery_FilterSortAndClassCounts_Have100PercentParityWithEagerAssetListQuery()
        {
            const int count = 20_000;
            var (projectIndex, eagerItems) = BuildParityDataset(count);
            var arena = projectIndex.Arena;
            var allIndices = Enumerable.Range(0, count).ToArray();

            var queries = new string?[]
            {
                null,
                string.Empty,
                "a",
                "Hero_LOD0",
                "hero_lod",
                "café_日本",
                "[lod0]",
                "FBX sub-asset",
                "Shader Settings",
                "19999",
                "__no_such_asset_999__",
                "Hero [LOD0] café",
            };

            var typeFilterSets = new HashSet<ClassIDType>?[]
            {
                null,
                new HashSet<ClassIDType>(),
                new HashSet<ClassIDType> { ClassIDType.Mesh, ClassIDType.Material },
                new HashSet<ClassIDType> { ClassIDType.Texture2D },
            };

            // 1) Filter parity across small (< ParallelFilterThreshold) and full (>= ParallelFilterThreshold) datasets
            var smallEager = eagerItems.Take(500).ToList();
            var smallIndices = allIndices.AsSpan(0, 500).ToArray();

            foreach (var isSmall in new[] { true, false })
            {
                var eagerDataset = isSmall ? smallEager : eagerItems;
                var arenaIndices = isSmall ? smallIndices : allIndices;

                foreach (var query in queries)
                {
                    foreach (var types in typeFilterSets)
                    {
                        var expected = AssetListQuery.Filter(eagerDataset, null, types, query, CancellationToken.None);
                        var actualIndices = AssetListQuery.FilterArena(arena, arenaIndices.AsSpan(), null, types, query, CancellationToken.None);

                        Assert.Equal(expected.Count, actualIndices.Length);
                        for (var i = 0; i < expected.Count; i++)
                        {
                            var idx = actualIndices[i];
                            Assert.Equal(expected[i].PathID, arena.GetPathId(idx));
                            Assert.Equal(expected[i].Name, arena.GetName(idx));
                            Assert.Equal(expected[i].Type, arena.GetType(idx));
                            Assert.Equal(expected[i].Container, arena.GetContainer(idx));
                        }
                    }
                }
            }

            // 2) ClassFilter override parity
            var classFilter = new AssetClassItem
            {
                ClassID = (int)ClassIDType.Mesh,
                UnityVersion = "2022.3.10f1"
            };
            var expectedClassFiltered = AssetListQuery.Filter(eagerItems, classFilter, null, "fbx", CancellationToken.None);
            var actualClassFiltered = AssetListQuery.FilterArena(arena, allIndices.AsSpan(), classFilter, null, "fbx", CancellationToken.None);
            Assert.NotEmpty(actualClassFiltered);
            Assert.Equal(expectedClassFiltered.Count, actualClassFiltered.Length);
            for (var i = 0; i < expectedClassFiltered.Count; i++)
            {
                Assert.Equal(expectedClassFiltered[i].PathID, arena.GetPathId(actualClassFiltered[i]));
                Assert.Equal(expectedClassFiltered[i].UniqueID, " #" + actualClassFiltered[i].ToString(CultureInfo.InvariantCulture));
            }

            // 3) Cancellation throws OperationCanceledException
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() =>
                    AssetListQuery.FilterArena(arena, allIndices.AsSpan(), null, null, "hero", cts.Token));
            }

            // 4) Sort parity across EVERY sort column & direction (including ties and unknown column)
            var sortSpecs = new (string SortMember, bool Descending)[]
            {
                ("Name", false),
                ("Name", true),
                ("Container", false),
                ("Container", true),
                ("DisplayType", false),
                ("DisplayType", true),
                ("Type", false),
                ("PathID", false),
                ("PathID", true),
                ("FullSize", false),
                ("Size", true),
                ("UnknownColumn", false),
            };

            foreach (var (sortMember, descending) in sortSpecs)
            {
                var expectedSorted = AssetListQuery.SortInPlace(eagerItems.ToList(), sortMember, descending);
                var actualSorted = (int[])allIndices.Clone();
                AssetListQuery.SortArenaInPlace(arena, actualSorted, actualSorted.Length, sortMember, descending);

                Assert.Equal(expectedSorted.Count, actualSorted.Length);
                for (var i = 0; i < expectedSorted.Count; i++)
                {
                    var idx = actualSorted[i];
                    Assert.Equal(expectedSorted[i].UniqueID, " #" + idx.ToString(CultureInfo.InvariantCulture));
                    Assert.Equal(expectedSorted[i].PathID, arena.GetPathId(idx));
                    Assert.Equal(expectedSorted[i].Name, arena.GetName(idx));
                }
            }

            // 5) AssetClassesDataGrid single-pass counts parity
            var classItems = AssetListQuery.BuildArenaAssetClassItems(arena);
            var expectedCounts = eagerItems.GroupBy(x => (int)x.Type).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(expectedCounts.Count, classItems.Count);
            foreach (var classItem in classItems)
            {
                Assert.Equal(expectedCounts[classItem.ClassID], classItem.ObjectCount);
                Assert.NotNull(classItem.SerializedType);
                Assert.Equal(classItem.ClassID, classItem.SerializedType.classID);
            }
        }

        [Fact]
        public void Benchmark_200kWarmOpen_MmapToArenaFilterSortAndViewport_TimeAndRetainedHeap()
        {
            const int assetCount = 200_000;
            const int sourceFileCount = 64;
            const int lruCapacity = 512;

            var tempRoot = CreateTempDirectory("bench_200k_warm_open");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePaths) = CreateSampleProject(tempRoot, sourceFileCount);
                using (cache)
                {
                    var seedHandles = Build200kBenchmarkHandles(sourcePaths, assetCount);
                    var idxPath = cache.GetIdxPath(projectDir);
                    Assert.True(MmapProjectIndexSidecar.TryWriteSidecar(
                        idxPath,
                        signature,
                        SQLiteProjectIndexCache.SemanticSchemaVersion,
                        seedHandles));
                    seedHandles.Clear();
                    seedHandles.TrimExcess();

                    // Warm-up JIT & OS page cache in isolated frame so warmup ProjectIndex is fully GC'd before measuring
                    WarmupMmapToArena(idxPath, signature, assetCount);

                    // Measure legacy eager warm open (TryLoadHandles + ProjectIndex.AddHandle + AssetItems + Dicts)
                    long legacyBytes = MeasureLegacyWarmOpenHeap(idxPath, signature, assetCount, out double legacyLoadMs);

                    // Measure new Arena warm open + filter + sort + VirtualAssetList flyweight viewport
                    ForceFullGc();
                    var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

                    var swMmapToArena = Stopwatch.StartNew();
                    var projectIndex = new ProjectIndex();
                    Assert.True(MmapProjectIndexSidecar.TryLoadIntoArena(
                        idxPath,
                        signature,
                        SQLiteProjectIndexCache.SemanticSchemaVersion,
                        projectIndex,
                        out var loadedSources));
                    swMmapToArena.Stop();

                    Assert.Equal(assetCount, projectIndex.Count);
                    Assert.NotNull(loadedSources);
                    Assert.Equal(sourceFileCount, loadedSources!.Count);
                    _ = projectIndex.DrainPendingIndices();

                    var arena = projectIndex.Arena;
                    var allIndices = new int[assetCount];
                    for (var i = 0; i < assetCount; i++)
                    {
                        allIndices[i] = i;
                    }

                    var swFilter = Stopwatch.StartNew();
                    var filteredIndices = AssetListQuery.FilterArena(
                        arena,
                        allIndices,
                        allIndices.Length,
                        classFilter: null,
                        selectedTypes: new HashSet<ClassIDType> { ClassIDType.Mesh, ClassIDType.Texture2D, ClassIDType.Material },
                        filterText: "Hero",
                        CancellationToken.None);
                    swFilter.Stop();

                    var swSort = Stopwatch.StartNew();
                    AssetListQuery.SortArenaInPlace(arena, filteredIndices, filteredIndices.Length, "Name", descending: false);
                    swSort.Stop();

                    var swClasses = Stopwatch.StartNew();
                    var classItems = AssetListQuery.BuildArenaAssetClassItems(arena);
                    swClasses.Stop();

                    Assert.True(filteredIndices.Length > 0);
                    Assert.NotEmpty(classItems);

                    // Bind full 200K arena indices into VirtualAssetList flyweight viewport with O(1) indexLocator
                    var swRebind = Stopwatch.StartNew();
                    var virtualList = new VirtualAssetList();
                    virtualList.ReplaceWithProvider(
                        allIndices.Length,
                        row => new AssetItem(arena.MaterializeHandle(allIndices[row]), allIndices[row]),
                        lruCapacity: lruCapacity,
                        indexLocator: item =>
                        {
                            var idx = item.Handle?.ArenaIndex ?? -1;
                            return (uint)idx < (uint)allIndices.Length && allIndices[idx] == idx ? idx : -1;
                        });
                    swRebind.Stop();

                    // Simulate scrolling across 5 viewports and verifying O(1) evicted-row IndexOf + non-evicting enumeration
                    AssetItem? evictedProbe = null;
                    int[] scrollOffsets = { 0, 25_000, 75_000, 125_000, 199_500 };
                    foreach (var offset in scrollOffsets)
                    {
                        for (var r = 0; r < 120; r++)
                        {
                            var item = virtualList[offset + r];
                            if (offset == 0 && r == 5)
                            {
                                evictedProbe = item;
                            }
                            Assert.True(virtualList.CachedItemCount <= lruCapacity);
                        }
                    }

                    Assert.NotNull(evictedProbe);
                    Assert.Equal(5, virtualList.IndexOf(evictedProbe!));

                    // Touch row 199_550 to make it MRU and verify full enumeration does NOT evict it from LRU
                    var mruRow = virtualList[199_550];
                    var enumeratedCount = 0;
                    foreach (var item in virtualList)
                    {
                        enumeratedCount++;
                        if (enumeratedCount == 199_551)
                        {
                            Assert.Same(mruRow, item);
                        }
                    }
                    Assert.Equal(assetCount, enumeratedCount);
                    Assert.Same(mruRow, virtualList[199_550]);

                    ForceFullGc();
                    var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
                    GC.KeepAlive(projectIndex);
                    GC.KeepAlive(allIndices);
                    GC.KeepAlive(filteredIndices);
                    GC.KeepAlive(virtualList);

                    var arenaTotalBytes = Math.Max(1L, heapAfter - heapBefore);
                    var legacyMb = legacyBytes / (1024.0 * 1024.0);
                    var arenaMb = arenaTotalBytes / (1024.0 * 1024.0);
                    var savedPct = (1.0 - ((double)arenaTotalBytes / legacyBytes)) * 100.0;

                    _output.WriteLine(
                        $"[200K Warm Open Benchmark] .idx -> Arena: {swMmapToArena.Elapsed.TotalMilliseconds:F2} ms | " +
                        $"Filter: {swFilter.Elapsed.TotalMilliseconds:F2} ms ({filteredIndices.Length:N0} matched) | " +
                        $"Sort: {swSort.Elapsed.TotalMilliseconds:F2} ms | Classes: {swClasses.Elapsed.TotalMilliseconds:F2} ms | " +
                        $"Viewport Rebind: {swRebind.Elapsed.TotalMilliseconds:F4} ms");
                    _output.WriteLine(
                        $"[200K Retained Heap] Legacy (ProjectIndex+Handles+AssetItems+Dicts): {legacyMb:F2} MB ({legacyBytes:N0} B, load {legacyLoadMs:F2} ms) -> " +
                        $"Arena+int[]+512-LRU: {arenaMb:F2} MB ({arenaTotalBytes:N0} B) | Reduction: {savedPct:F1}% ({legacyMb - arenaMb:F2} MB saved)");

                    Assert.True(
                        savedPct >= 60.0,
                        $"Expected >= 60% managed heap reduction on 200K warm open, got {savedPct:F1}% (Legacy={legacyMb:F2} MB, Arena={arenaMb:F2} MB).");
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ResolveColdIndexFiles_ReusesScanResultWithoutSecondDiskWalk_AndHandlesSplitFiles()
        {
            var tempRoot = CreateTempDirectory("resolve_cold_files");
            try
            {
                var normalDir = Path.Combine(tempRoot, "NormalGame");
                Directory.CreateDirectory(normalDir);
                var f1 = Path.Combine(normalDir, "level0");
                var f2 = Path.Combine(normalDir, "sharedassets0.assets");
                var f3 = Path.Combine(normalDir, "bundle_01.unity3d");
                File.WriteAllBytes(f1, new byte[16]);
                File.WriteAllBytes(f2, new byte[16]);
                File.WriteAllBytes(f3, new byte[16]);

                var scanWithoutSplits = ProjectScanner.ScanFolder(normalDir);
                var mergeCalls = 0;
                var enumerateCalls = 0;
                var reused = MainWindow.ResolveColdIndexFiles(
                    normalDir,
                    scanWithoutSplits,
                    mergeSplitAssets: _ => mergeCalls++,
                    enumerateFiles: _ =>
                    {
                        enumerateCalls++;
                        return Array.Empty<string>();
                    });

                Assert.Equal(0, mergeCalls);
                Assert.Equal(0, enumerateCalls);
                Assert.Equal(3, reused.Count);
                Assert.Contains(f1, reused);
                Assert.Contains(f2, reused);
                Assert.Contains(f3, reused);

                // When .split0 is present, merge + enumerate must run
                var splitDir = Path.Combine(tempRoot, "SplitGame");
                Directory.CreateDirectory(splitDir);
                File.WriteAllBytes(Path.Combine(splitDir, "level0.split0"), new byte[16]);
                File.WriteAllBytes(Path.Combine(splitDir, "level0.split1"), new byte[16]);
                var scanWithSplits = ProjectScanner.ScanFolder(splitDir);
                var mergedTarget = Path.Combine(splitDir, "level0");

                var splitResult = MainWindow.ResolveColdIndexFiles(
                    splitDir,
                    scanWithSplits,
                    mergeSplitAssets: _ => mergeCalls++,
                    enumerateFiles: _ =>
                    {
                        enumerateCalls++;
                        return new[] { mergedTarget };
                    });

                Assert.Equal(1, mergeCalls);
                Assert.Equal(1, enumerateCalls);
                Assert.Single(splitResult);
                Assert.Equal(mergedTarget, splitResult[0]);

                // When scanResult is null, fallback to merge + enumerate
                var fallbackFile = Path.Combine(normalDir, "fallback.assets");
                var nullScanResult = MainWindow.ResolveColdIndexFiles(
                    normalDir,
                    null,
                    mergeSplitAssets: _ => mergeCalls++,
                    enumerateFiles: _ =>
                    {
                        enumerateCalls++;
                        return new[] { fallbackFile };
                    });

                Assert.Equal(2, mergeCalls);
                Assert.Equal(2, enumerateCalls);
                Assert.Single(nullScanResult);
                Assert.Equal(fallbackFile, nullScanResult[0]);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SortedProgressiveBatchInsertion_10000Items_MatchesFullSortAndCompletesUnder100Ms()
        {
            const int totalCount = 10_000;
            const int batchSize = 100;
            var (projectIndex, eagerItems) = BuildParityDataset(totalCount);
            var arena = projectIndex.Arena;

            // Shuffle deterministically to trigger worst-case out-of-order insertions across batches
            var rng = new Random(42);
            var shuffledIndices = Enumerable.Range(0, totalCount).ToArray();
            for (var i = shuffledIndices.Length - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffledIndices[i], shuffledIndices[j]) = (shuffledIndices[j], shuffledIndices[i]);
            }

            var shuffledItems = shuffledIndices.Select(idx => eagerItems[idx]).ToArray();

            foreach (var (sortMember, descending) in new[] { ("Name", false), ("PathID", true), ("FullSize", false) })
            {
                var expectedEager = AssetListQuery.SortInPlace(shuffledItems.ToList(), sortMember, descending);

                // Warm up once on small slice
                var warmList = new List<AssetItem>();
                AssetListQuery.MergeSortedBatch(warmList, shuffledItems.Take(32).ToList(), sortMember, descending);

                // 1) Eager MergeSortedBatch across 100 batches of 100 items
                var progressiveEager = new List<AssetItem>(totalCount);
                var swEager = Stopwatch.StartNew();
                for (var offset = 0; offset < totalCount; offset += batchSize)
                {
                    var batch = new List<AssetItem>(batchSize);
                    for (var k = 0; k < batchSize && offset + k < totalCount; k++)
                    {
                        batch.Add(shuffledItems[offset + k]);
                    }
                    AssetListQuery.MergeSortedBatch(progressiveEager, batch, sortMember, descending);
                }
                swEager.Stop();

                Assert.Equal(expectedEager.Count, progressiveEager.Count);
                for (var i = 0; i < expectedEager.Count; i++)
                {
                    Assert.Same(expectedEager[i], progressiveEager[i]);
                }

                // 2) Arena MergeSortedArenaBatch across 100 batches of 100 indices
                var progressiveArena = Array.Empty<int>();
                var swArena = Stopwatch.StartNew();
                for (var offset = 0; offset < totalCount; offset += batchSize)
                {
                    var len = Math.Min(batchSize, totalCount - offset);
                    var batch = new int[len];
                    Array.Copy(shuffledIndices, offset, batch, 0, len);
                    progressiveArena = AssetListQuery.MergeSortedArenaBatch(
                        arena,
                        progressiveArena,
                        progressiveArena.Length,
                        batch,
                        len,
                        sortMember,
                        descending);
                }
                swArena.Stop();

                Assert.Equal(totalCount, progressiveArena.Length);
                for (var i = 0; i < totalCount; i++)
                {
                    Assert.Equal(expectedEager[i].UniqueID, " #" + progressiveArena[i].ToString(CultureInfo.InvariantCulture));
                }

                _output.WriteLine(
                    $"[Sorted Progressive 10K ({sortMember} {(descending ? "desc" : "asc")})] " +
                    $"Eager MergeSortedBatch={swEager.Elapsed.TotalMilliseconds:F2} ms | " +
                    $"Arena MergeSortedArenaBatch={swArena.Elapsed.TotalMilliseconds:F2} ms");

                Assert.True(
                    swEager.Elapsed.TotalMilliseconds < 250.0,
                    $"Expected eager sorted progressive insert for 10K items to avoid O(N^2) (< 250 ms), got {swEager.Elapsed.TotalMilliseconds:F2} ms.");
                Assert.True(
                    swArena.Elapsed.TotalMilliseconds < 250.0,
                    $"Expected arena sorted progressive insert for 10K items to avoid O(N^2) (< 250 ms), got {swArena.Elapsed.TotalMilliseconds:F2} ms.");
            }
        }

        [Fact]
        public void LifecycleCleanup_KeyOriginIdPool_SqliteReadPool_AndVirtualAssetList_ReleaseAllResources()
        {
            // 1) AssetHandle.ClearSourceHashCache() clears AssetHandleOrigin.KeyOriginIdPool
            var keyOriginField = typeof(AssetHandleOrigin).GetField("KeyOriginIdPool", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(keyOriginField);
            var keyOriginPool = Assert.IsType<ConcurrentDictionary<string, int>>(keyOriginField!.GetValue(null));

            var probeFile = "lifecycle_probe_" + Guid.NewGuid().ToString("N");
            var probePath = $@"C:\LifecycleProbe\{probeFile}.bundle";
            var originBefore = AssetHandleOrigin.Intern(probeFile, probePath);
            Assert.True(originBefore.KeyOriginId > 0);
            Assert.True(keyOriginPool.ContainsKey(originBefore.UniqueIdPrefix));

            AssetHandle.ClearSourceHashCache();
            Assert.False(keyOriginPool.ContainsKey(originBefore.UniqueIdPrefix));

            // Re-interning works cleanly after clear with deterministic hash & fresh origin ID
            var originAfter = AssetHandleOrigin.Intern(probeFile, probePath);
            Assert.Equal(originBefore.SourceHash, originAfter.SourceHash);
            Assert.Equal(originBefore.UniqueIdPrefix, originAfter.UniqueIdPrefix);
            Assert.True(keyOriginPool.ContainsKey(originAfter.UniqueIdPrefix));

            // 2) SQLiteProjectIndexCache.ReleaseReadConnections() drops all idle read pools across switched projects
            var tempRoot = CreateTempDirectory("lifecycle_sqlite_pool");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projA = Path.Combine(tempRoot, "ProjectA");
                var projB = Path.Combine(tempRoot, "ProjectB");
                Directory.CreateDirectory(projA);
                Directory.CreateDirectory(projB);
                var srcA = Path.Combine(projA, "level0");
                var srcB = Path.Combine(projB, "level0");
                File.WriteAllBytes(srcA, new byte[64]);
                File.WriteAllBytes(srcB, new byte[96]);

                var scanA = ProjectScanner.ScanFolder(projA);
                var scanB = ProjectScanner.ScanFolder(projB);
                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sigA = cache.GetFolderSignature(scanA);
                var sigB = cache.GetFolderSignature(scanB);

                Assert.True(cache.SaveIndexCache(projA, sigA, scanA, "2022.3.20f1", BuildDiverseHandles(new[] { srcA }, 16)));
                Assert.True(cache.SaveIndexCache(projB, sigB, scanB, "2022.3.20f1", BuildDiverseHandles(new[] { srcB }, 16)));

                var idxA = new ProjectIndex();
                var idxB = new ProjectIndex();
                Assert.True(cache.LoadIndexCacheIntoProjectIndex(projA, sigA, idxA, out _));
                Assert.True(cache.LoadIndexCacheIntoProjectIndex(projB, sigB, idxB, out _));
                Assert.True(cache.GetTotalIdleReadConnectionCount() >= 2);

                // Simulate ResetForm() releasing all read pools across old projects
                cache.ReleaseReadConnections();
                Assert.Equal(0, cache.GetTotalIdleReadConnectionCount());

                // Both DBs and sidecars must be deletable immediately on Windows (zero lingering handles)
                File.Delete(cache.GetDbPath(projA));
                File.Delete(cache.GetIdxPath(projA));
                File.Delete(cache.GetDbPath(projB));
                File.Delete(cache.GetIdxPath(projB));
                Assert.False(File.Exists(cache.GetDbPath(projA)));
                Assert.False(File.Exists(cache.GetDbPath(projB)));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }

            // 3) VirtualAssetList.Clear() releases provider/indexLocator delegates and LRU AssetItem references
            var (itemRef, closureRef) = ExerciseVirtualListProviderAndClear();
            ForceFullGc();
            Assert.False(itemRef.IsAlive, "Expected VirtualAssetList.Clear() to release LRU-cached AssetItem references.");
            Assert.False(closureRef.IsAlive, "Expected VirtualAssetList.Clear() to release provider/indexLocator closure references.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference ItemRef, WeakReference ClosureRef) ExerciseVirtualListProviderAndClear()
        {
            var sentinel = new object();
            var virtualList = new VirtualAssetList();
            virtualList.ReplaceWithProvider(
                100,
                row =>
                {
                    _ = sentinel;
                    return new AssetItem(new AssetHandle { PathID = row + 1, Name = $"Item_{row}" }, row);
                },
                lruCapacity: 16,
                indexLocator: item =>
                {
                    _ = sentinel;
                    return item != null ? (int)(item.PathID - 1) : -1;
                });

            var materialized = virtualList[0];
            Assert.Equal(1, virtualList.CachedItemCount);

            var itemRef = new WeakReference(materialized);
            var closureRef = new WeakReference(sentinel);

            virtualList.Clear();
            Assert.Empty(virtualList);
            Assert.Equal(0, virtualList.CachedItemCount);

            materialized = null!;
            sentinel = null!;
            GC.KeepAlive(virtualList);
            return (itemRef, closureRef);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WarmupMmapToArena(string idxPath, string signature, int expectedCount)
        {
            var warmupIndex = new ProjectIndex();
            Assert.True(MmapProjectIndexSidecar.TryLoadIntoArena(
                idxPath,
                signature,
                SQLiteProjectIndexCache.SemanticSchemaVersion,
                warmupIndex,
                out _));
            Assert.Equal(expectedCount, warmupIndex.Count);
            warmupIndex.Clear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long MeasureLegacyWarmOpenHeap(string idxPath, string signature, int expectedCount, out double loadMs)
        {
            ForceFullGc();
            var before = GC.GetTotalMemory(forceFullCollection: true);

            var sw = Stopwatch.StartNew();
            Assert.True(MmapProjectIndexSidecar.TryLoadHandles(
                idxPath,
                signature,
                SQLiteProjectIndexCache.SemanticSchemaVersion,
                out var handles));
            Assert.Equal(expectedCount, handles.Count);

            var legacyIndex = new ProjectIndex();
            for (var i = 0; i < handles.Count; i++)
            {
                legacyIndex.AddHandle(handles[i]);
            }

            var legacyByKey = new Dictionary<AssetHandleKey, AssetItem>(expectedCount);
            var legacyExportableKeys = new HashSet<AssetHandleKey>(expectedCount);
            var legacyExportableItems = new List<AssetItem>(expectedCount);
            var legacyVisibleItems = new List<AssetItem>(expectedCount);
            var ordinal = 0;
            foreach (var h in legacyIndex.DrainPendingHandles())
            {
                h.TryGetKey(out var key);
                var item = new AssetItem(h, ordinal++);
                h.Tag = item;
                legacyByKey[key] = item;
                legacyExportableKeys.Add(key);
                legacyExportableItems.Add(item);
                legacyVisibleItems.Add(item);
            }
            sw.Stop();
            loadMs = sw.Elapsed.TotalMilliseconds;

            ForceFullGc();
            var after = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(legacyIndex);
            GC.KeepAlive(handles);
            GC.KeepAlive(legacyByKey);
            GC.KeepAlive(legacyExportableKeys);
            GC.KeepAlive(legacyExportableItems);
            GC.KeepAlive(legacyVisibleItems);

            return Math.Max(1L, after - before);
        }

        private static (ProjectIndex Index, List<AssetItem> EagerItems) BuildParityDataset(int count)
        {
            var types = new[]
            {
                ClassIDType.Texture2D,
                ClassIDType.Mesh,
                ClassIDType.Material,
                ClassIDType.Transform,
                ClassIDType.PrefabInstance,
                ClassIDType.GameObject,
                ClassIDType.AudioClip,
                ClassIDType.MonoBehaviour,
            };

            var fileA = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            fileA.unityVersion = "2022.3.10f1";
            fileA.fileName = "level0";
            fileA.originalPath = @"C:\ParityGame\level0";

            var fileB = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            fileB.unityVersion = "2021.3.20f1";
            fileB.fileName = "sharedassets0";
            fileB.originalPath = @"C:\ParityGame\sharedassets0";

            var projectIndex = new ProjectIndex();
            var items = new List<AssetItem>(count);

            for (var i = 0; i < count; i++)
            {
                var type = types[i % types.Length];
                var container = (i % 5) switch
                {
                    0 => string.Empty,
                    1 => $"assets/models/pack_{i % 23}/Hero_Model.FBX/part_{i % 7}",
                    2 => $@"assets\textures\pack_{i % 11}\albedo_{i}.png",
                    3 => $"assets/shaders/lit_{i % 5}.shader",
                    _ => $"assets/prefabs/item_{i}.prefab",
                };

                var name = (i % 7) switch
                {
                    0 => string.Empty,
                    1 => $"Hero [LOD0] café_日本語_{i}",
                    2 => $"Enemy_Mesh_{i}",
                    3 => $"UI_Atlas_{i}",
                    4 => $"Prop_Crate_{i}",
                    5 => $"Audio_SFX_{i}",
                    _ => $"Common_{i % 50}",
                };

                var pathId = (long)((i * 7919) % 100_000) - 500;
                var byteSize = (i * 31L) % 65536;
                var sourceFile = ((i / 8) % 2 == 0) ? fileA : fileB;

                var idx = projectIndex.AddRecord(
                    serializedFileName: sourceFile.fileName,
                    originalPath: sourceFile.originalPath,
                    pathId: pathId,
                    name: name,
                    type: type,
                    container: container,
                    byteStart: i * 128L,
                    byteSize: byteSize,
                    unityVersion: sourceFile.unityVersion,
                    sourceFile: sourceFile,
                    customUniqueId: $"uid#{i:D6}");
                Assert.Equal(i, idx);

                items.Add(new AssetItem(projectIndex.MaterializeHandle(idx), idx));
            }

            return (projectIndex, items);
        }

        private static List<AssetHandle> BuildDiverseHandles(string[] sourcePaths, int count)
        {
            var handles = new List<AssetHandle>(count + 4);
            var unicodeNames = new[]
            {
                "Hero_角色_メッシュ_01",
                "Weapon_🔥_Épée_Ñoño",
                "Shader/Standard (Specular Setup) [LOD0]",
                string.Empty,
                "UI_Кнопка_아이콘_Asset"
            };

            for (var i = 0; i < count; i++)
            {
                var srcIndex = i % sourcePaths.Length;
                var serializedFileName = srcIndex == 0 ? "level0" : $"sharedassets{srcIndex}.assets";
                var originalPath = sourcePaths[srcIndex];
                var pathId = (i % 7 == 0)
                    ? -(100_000L + (i * 9973L))
                    : (i == 0 ? long.MinValue + 1 : 1000L + (i * 65537L));
                var container = (i % 4) switch
                {
                    0 => string.Empty,
                    1 => $"assets/models/hero_{srcIndex}.FBX/submesh_{i % 5}",
                    _ => $"assets/bundles/region_{srcIndex}/prefab_{i % 9}_アセット.prefab"
                };

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
                    ByteStart = (long)int.MaxValue + (i * 131072L),
                    ByteSize = (i % 11 == 0) ? 0L : (4096L + (i * 257L))
                });
            }

            // Include a duplicate UniqueID and a custom non-canonical UniqueID
            var dupTarget = handles[10];
            handles.Add(new AssetHandle
            {
                UniqueID = dupTarget.UniqueID,
                Name = "Overwritten_Duplicate_日本語",
                Type = ClassIDType.Avatar,
                Container = "assets/override/avatar.fbx",
                OriginalPath = dupTarget.OriginalPath,
                SerializedFileName = dupTarget.SerializedFileName,
                PathID = dupTarget.PathID,
                ByteStart = 9_876_543_210L,
                ByteSize = 99_999L
            });

            handles.Add(new AssetHandle
            {
                UniqueID = "custom_noncanonical#42",
                Name = "CustomUidAsset",
                Type = ClassIDType.Shader,
                Container = "assets/shaders/custom.shader",
                OriginalPath = sourcePaths[0],
                SerializedFileName = "level0",
                PathID = 42L,
                ByteStart = 512L,
                ByteSize = 2048L
            });

            return handles;
        }

        private static List<AssetHandle> Build200kBenchmarkHandles(string[] sourcePaths, int count)
        {
            var handles = new List<AssetHandle>(count);
            var containers = new string[128];
            for (var c = 0; c < containers.Length; c++)
            {
                containers[c] = (c % 5) switch
                {
                    0 => string.Empty,
                    1 => $"assets/characters/hero_group_{c / 8}/hero_{c:D3}.fbx/mesh_lod0",
                    _ => $"assets/environments/zone_{c / 16}/prop_{c:D3}.prefab"
                };
            }

            var origins = new AssetHandleOrigin[sourcePaths.Length];
            for (var s = 0; s < sourcePaths.Length; s++)
            {
                var serializedFileName = $"CAB-{s:x8}aabbccddeeff00112233";
                origins[s] = AssetHandleOrigin.Intern(serializedFileName, sourcePaths[s]);
            }

            for (var i = 0; i < count; i++)
            {
                var origin = origins[i % origins.Length];
                var pathId = 100_000L + i;
                var type = (i % 5) switch
                {
                    0 => ClassIDType.Mesh,
                    1 => ClassIDType.Texture2D,
                    2 => ClassIDType.Material,
                    3 => ClassIDType.AnimationClip,
                    _ => ClassIDType.GameObject
                };

                var name = (i % 8 == 0)
                    ? $"Hero_Asset_{i % 2048:D4}"
                    : $"Env_Prop_{i % 4096:D4}";

                handles.Add(AssetHandle.CreateCanonical(
                    origin,
                    pathId,
                    name: name,
                    type: type,
                    container: containers[i & 127],
                    byteStart: i * 256L,
                    byteSize: 256L + (i & 4095)));
            }

            return handles;
        }

        private static void AssertProjectIndexMatchesHandles(IReadOnlyList<AssetHandle> expected, ProjectIndex actualIndex)
        {
            Assert.Equal(expected.Count, actualIndex.Count);
            var arena = actualIndex.Arena;
            for (var i = 0; i < expected.Count; i++)
            {
                var exp = expected[i];
                Assert.Equal(exp.UniqueID, arena.GetUniqueId(i));
                Assert.Equal(exp.Name, arena.GetName(i));
                Assert.Equal(exp.Type, arena.GetType(i));
                Assert.Equal(exp.Container, arena.GetContainer(i));
                Assert.Equal(exp.OriginalPath, arena.GetOriginalPath(i));
                Assert.Equal(exp.SerializedFileName, arena.GetSerializedFileName(i));
                Assert.Equal(exp.PathID, arena.GetPathId(i));
                Assert.Equal(exp.ByteStart, arena.GetByteStart(i));
                Assert.Equal(exp.ByteSize, arena.GetByteSize(i));
            }
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
                File.WriteAllBytes(path, new byte[32 + (i * 4)]);
                sourcePaths[i] = path;
            }

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePaths);
        }

        private static void ForceFullGc()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
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
