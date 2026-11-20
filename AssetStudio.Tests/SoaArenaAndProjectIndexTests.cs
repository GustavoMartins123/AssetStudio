#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class SoaArenaAndProjectIndexTests
    {
        private readonly ITestOutputHelper _output;

        public SoaArenaAndProjectIndexTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AssetHandleKey_Is12Bytes_AndSupportsValueEquality()
        {
            Assert.Equal(12, Unsafe.SizeOf<AssetHandleKey>());
            Assert.Equal(12, AssetHandleKey.SizeInBytes);

            var key1 = new AssetHandleKey(7, -9876543210L);
            var key2 = new AssetHandleKey(7, -9876543210L);
            var key3 = new AssetHandleKey(8, -9876543210L);
            var key4 = new AssetHandleKey(7, -9876543211L);

            Assert.Equal(key1, key2);
            Assert.True(key1 == key2);
            Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
            Assert.NotEqual(key1, key3);
            Assert.NotEqual(key1, key4);
            Assert.False(key1.IsEmpty);
            Assert.True(default(AssetHandleKey).IsEmpty);
        }

        [Fact]
        public void TryParseUniqueId_HandlesAllValidAndEdgeCaseFormats()
        {
            // Standard format
            Assert.True(AssetHandleKey.TryParseUniqueId(
                "level0#0123456789abcdef#123456789".AsSpan(),
                out ReadOnlySpan<char> fileName,
                out ReadOnlySpan<char> sourceHash,
                out long pathId));
            Assert.Equal("level0", fileName.ToString());
            Assert.Equal("0123456789abcdef", sourceHash.ToString());
            Assert.Equal(123456789L, pathId);

            // Negative PathID including Int64 extremes
            Assert.True(AssetHandleKey.TryParseUniqueId(
                $"sharedassets0.assets#fedcba9876543210#{long.MinValue.ToString(CultureInfo.InvariantCulture)}",
                out fileName,
                out sourceHash,
                out pathId));
            Assert.Equal("sharedassets0.assets", fileName.ToString());
            Assert.Equal("fedcba9876543210", sourceHash.ToString());
            Assert.Equal(long.MinValue, pathId);

            // '#' characters inside file name
            Assert.True(AssetHandleKey.TryParseUniqueId(
                "archive#v2#bundle#part1#0123456789abcdef#-42",
                out fileName,
                out sourceHash,
                out pathId));
            Assert.Equal("archive#v2#bundle#part1", fileName.ToString());
            Assert.Equal("0123456789abcdef", sourceHash.ToString());
            Assert.Equal(-42L, pathId);

            // Empty sourceHash
            Assert.True(AssetHandleKey.TryParseUniqueId(
                "level0##777",
                out fileName,
                out sourceHash,
                out pathId));
            Assert.Equal("level0", fileName.ToString());
            Assert.True(sourceHash.IsEmpty);
            Assert.Equal(777L, pathId);

            // '#' in file name AND empty sourceHash
            Assert.True(AssetHandleKey.TryParseUniqueId(
                "bundle#hash_in_name##-999",
                out fileName,
                out sourceHash,
                out pathId));
            Assert.Equal("bundle#hash_in_name", fileName.ToString());
            Assert.True(sourceHash.IsEmpty);
            Assert.Equal(-999L, pathId);

            // Invalid formats
            Assert.False(AssetHandleKey.TryParseUniqueId((string?)null, out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId(string.Empty, out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId("file0#123", out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId("#0123456789abcdef#123", out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId("   #0123456789abcdef#123", out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId("level0#0123456789abcdef#", out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId("level0#0123456789abcdef#not_a_long", out _, out _, out _));
            Assert.False(AssetHandleKey.TryParseUniqueId("level0#bad hash#123", out _, out _, out _));
        }

        [Fact]
        public void ProjectIndex_And_AssetIndexArena_RoundTripAllFieldsWith100PercentParity()
        {
            var projectIndex = new ProjectIndex();
            var tagSentinel = new object();

            var testCases = new[]
            {
                new
                {
                    FileName = "level0",
                    OriginalPath = @"C:\Games\MegaGame\MegaGame_Data\level0",
                    UnityVersion = "2022.3.20f1",
                    Name = "Hero_角色_メッシュ_01",
                    Type = ClassIDType.Mesh,
                    Container = "assets/art/characters/hero/prefabs/hero_角色.prefab",
                    PathID = 1001L,
                    ByteStart = 4096L,
                    ByteSize = 65536L,
                    Tag = (object?)tagSentinel
                },
                new
                {
                    FileName = "archive#v1#CAB-99887766",
                    OriginalPath = @"C:\Games\MegaGame\StreamingAssets\Bundles\archive#v1.bundle",
                    UnityVersion = "2021.3.15f1",
                    Name = "Weapon_🔥_Épée_Ñoño",
                    Type = ClassIDType.Texture2D,
                    Container = @"assets\art\weapons\swords\epee_albedo.png",
                    PathID = -987654321012345L,
                    ByteStart = 128L,
                    ByteSize = 1048576L,
                    Tag = (object?)null
                },
                new
                {
                    FileName = "sharedassets1.assets",
                    OriginalPath = string.Empty,
                    UnityVersion = string.Empty,
                    Name = "UI_Кнопка_아이콘_Asset",
                    Type = ClassIDType.GameObject,
                    Container = "root_only_container.prefab",
                    PathID = long.MinValue,
                    ByteStart = 0L,
                    ByteSize = 512L,
                    Tag = (object?)"string_tag"
                },
                new
                {
                    FileName = "sharedassets1.assets",
                    OriginalPath = string.Empty,
                    UnityVersion = string.Empty,
                    Name = string.Empty,
                    Type = ClassIDType.PrefabInstance,
                    Container = string.Empty,
                    PathID = long.MaxValue,
                    ByteStart = 8192L,
                    ByteSize = 256L,
                    Tag = (object?)null
                }
            };

            foreach (var tc in testCases)
            {
                string uid = AssetHandle.BuildUniqueID(tc.FileName, tc.OriginalPath, tc.PathID);
                var handle = new AssetHandle
                {
                    UniqueID = uid,
                    Name = tc.Name,
                    Type = tc.Type,
                    Container = tc.Container,
                    OriginalPath = tc.OriginalPath,
                    SerializedFileName = tc.FileName,
                    UnityVersion = tc.UnityVersion,
                    PathID = tc.PathID,
                    ByteStart = tc.ByteStart,
                    ByteSize = tc.ByteSize,
                    Tag = tc.Tag
                };

                projectIndex.AddHandle(handle);
            }

            Assert.Equal(testCases.Length, projectIndex.Count);
            Assert.True(projectIndex.Arena.EstimateMemoryBytes() > 0);

            for (int i = 0; i < testCases.Length; i++)
            {
                var tc = testCases[i];
                string expectedUid = AssetHandle.BuildUniqueID(tc.FileName, tc.OriginalPath, tc.PathID);

                Assert.True(projectIndex.TryGetIndexByUniqueId(expectedUid.AsSpan(), out int idx));
                Assert.Equal(i, idx);

                AssetHandleKey key = projectIndex.Arena.GetKey(idx);
                Assert.True(projectIndex.TryGetIndex(key, out int idxByKey));
                Assert.Equal(idx, idxByKey);

                Assert.Equal(expectedUid, projectIndex.Arena.GetUniqueId(idx));
                Assert.Equal(tc.Name, projectIndex.Arena.GetName(idx));
                Assert.Equal(tc.Type, projectIndex.Arena.GetType(idx));
                Assert.Equal(tc.Container, projectIndex.Arena.GetContainer(idx));
                Assert.Equal(tc.FileName, projectIndex.Arena.GetSerializedFileName(idx));
                Assert.Equal(tc.OriginalPath, projectIndex.Arena.GetOriginalPath(idx));
                Assert.Equal(tc.UnityVersion, projectIndex.Arena.GetUnityVersion(idx));
                Assert.Equal(tc.PathID, projectIndex.Arena.GetPathId(idx));
                Assert.Equal(tc.ByteStart, projectIndex.Arena.GetByteStart(idx));
                Assert.Equal(tc.ByteSize, projectIndex.Arena.GetByteSize(idx));

                AssetHandle? resolved = projectIndex.GetHandle(expectedUid);
                Assert.NotNull(resolved);
                Assert.Equal(expectedUid, resolved!.UniqueID);
                Assert.Equal(tc.Name, resolved.Name);
                Assert.Equal(tc.Type, resolved.Type);
                Assert.Equal(tc.Type.ToString(), resolved.TypeString);
                Assert.Equal(tc.Container, resolved.Container);
                Assert.Equal(tc.OriginalPath, resolved.OriginalPath);
                Assert.Equal(tc.FileName, resolved.SerializedFileName);
                Assert.Equal(tc.UnityVersion, resolved.UnityVersion);
                Assert.Equal(tc.PathID, resolved.PathID);
                Assert.Equal(tc.ByteStart, resolved.ByteStart);
                Assert.Equal(tc.ByteSize, resolved.ByteSize);
                Assert.Same(tc.Tag, resolved.Tag);

                // Verify write-through on materialized handle reflects across subsequent lookups
                resolved.Container = tc.Container + "/mutated";
                resolved.Name = tc.Name + "_Mutated";
                AssetHandle? reResolved = projectIndex.GetHandle(expectedUid);
                Assert.NotNull(reResolved);
                Assert.Equal(tc.Container + "/mutated", reResolved!.Container);
                Assert.Equal(tc.Name + "_Mutated", reResolved.Name);
            }

            var sharedHandles = projectIndex.GetHandlesForFile("sharedassets1.assets").ToList();
            Assert.Equal(2, sharedHandles.Count);

            int[] drainedIndices = projectIndex.DrainPendingIndices(2);
            Assert.Equal(new[] { 0, 1 }, drainedIndices);

            var remainingHandles = projectIndex.DrainPendingHandles().ToList();
            Assert.Equal(2, remainingHandles.Count);
            Assert.Empty(projectIndex.DrainPendingHandles());

            projectIndex.Clear();
            Assert.Equal(0, projectIndex.Count);
            Assert.Null(projectIndex.GetHandle(AssetHandle.BuildUniqueID("level0", string.Empty, 1001L)));
        }

        [Fact]
        public void ProjectIndex_DuplicateAndCustomUniqueIds_PreserveLastWriteWins()
        {
            var projectIndex = new ProjectIndex();
            string sourcePath = @"C:\Games\Bundle\level0";
            string canonicalUid = AssetHandle.BuildUniqueID("level0", sourcePath, 500L);

            projectIndex.AddHandle(new AssetHandle
            {
                UniqueID = canonicalUid,
                Name = "FirstVersion",
                Type = ClassIDType.Mesh,
                Container = "assets/v1/mesh.fbx",
                OriginalPath = sourcePath,
                SerializedFileName = "level0",
                PathID = 500L,
                ByteStart = 10,
                ByteSize = 100
            });

            projectIndex.AddHandle(new AssetHandle
            {
                UniqueID = canonicalUid,
                Name = "SecondVersion",
                Type = ClassIDType.Texture2D,
                Container = "assets/v2/tex.png",
                OriginalPath = sourcePath,
                SerializedFileName = "level0",
                PathID = 500L,
                ByteStart = 20,
                ByteSize = 200
            });

            Assert.Equal(1, projectIndex.Count);
            Assert.Single(projectIndex.GetHandlesForFile("level0"));

            AssetHandle? resolved = projectIndex.GetHandle(canonicalUid);
            Assert.NotNull(resolved);
            Assert.Equal("SecondVersion", resolved!.Name);
            Assert.Equal(ClassIDType.Texture2D, resolved.Type);
            Assert.Equal("assets/v2/tex.png", resolved.Container);
            Assert.Equal(20L, resolved.ByteStart);
            Assert.Equal(200L, resolved.ByteSize);

            // Non-canonical single-hash and leading-zero custom UniqueIDs
            string singleHashUid = "file0#42";
            string leadingZeroUid = "level0#0123456789abcdef#007";

            projectIndex.AddHandle(new AssetHandle
            {
                UniqueID = singleHashUid,
                Name = "CustomSingleHash_V1",
                Type = ClassIDType.Material,
                SerializedFileName = "file0",
                PathID = 42L
            });
            projectIndex.AddHandle(new AssetHandle
            {
                UniqueID = singleHashUid,
                Name = "CustomSingleHash_V2",
                Type = ClassIDType.Shader,
                SerializedFileName = "file0",
                PathID = 42L
            });
            projectIndex.AddHandle(new AssetHandle
            {
                UniqueID = leadingZeroUid,
                Name = "LeadingZeroAsset",
                Type = ClassIDType.AudioClip,
                SerializedFileName = "level0",
                PathID = 7L
            });

            Assert.Equal(3, projectIndex.Count);
            Assert.Equal("CustomSingleHash_V2", projectIndex.GetHandle(singleHashUid)!.Name);
            Assert.Equal(ClassIDType.Shader, projectIndex.GetHandle(singleHashUid)!.Type);
            Assert.Equal(leadingZeroUid, projectIndex.GetHandle(leadingZeroUid)!.UniqueID);
            Assert.Equal("LeadingZeroAsset", projectIndex.GetHandle(leadingZeroUid)!.Name);
        }

        [Fact]
        public async Task ProjectIndex_MultiThreadedStressTest_8ThreadsConcurrentReadWriteDrain()
        {
            var projectIndex = new ProjectIndex();
            const int writerThreadCount = 8;
            const int handlesPerThread = 1_500;
            int drainedTotal = 0;
            using var cts = new CancellationTokenSource();

            Task[] writers = Enumerable.Range(0, writerThreadCount).Select(threadId => Task.Run(() =>
            {
                string fileName = $"archive_cab_{threadId % 4:D2}";
                string originalPath = $@"C:\Games\StressTest\Bundles\{fileName}.bundle";
                for (int i = 0; i < handlesPerThread; i++)
                {
                    long pathId = ((long)threadId << 32) | (uint)i;
                    string uid = AssetHandle.BuildUniqueID(fileName, originalPath, pathId);

                    var handle = new AssetHandle
                    {
                        UniqueID = uid,
                        Name = $"Asset_T{threadId}_{i}",
                        Type = (i & 1) == 0 ? ClassIDType.Mesh : ClassIDType.Texture2D,
                        Container = $"assets/bundles/dir_{i % 32}/sub_{i % 8}/item_{i}.prefab",
                        OriginalPath = originalPath,
                        SerializedFileName = fileName,
                        PathID = pathId,
                        ByteStart = i * 64L,
                        ByteSize = 64L
                    };

                    projectIndex.AddHandle(handle);

                    // Overwrite every 10th handle to exercise concurrent in-place updates
                    if (i % 10 == 0)
                    {
                        projectIndex.AddHandle(new AssetHandle
                        {
                            UniqueID = uid,
                            Name = $"Asset_T{threadId}_{i}_Updated",
                            Type = ClassIDType.Material,
                            Container = $"assets/bundles/dir_{i % 32}/sub_{i % 8}/item_{i}.mat",
                            OriginalPath = originalPath,
                            SerializedFileName = fileName,
                            PathID = pathId,
                            ByteStart = i * 128L,
                            ByteSize = 128L
                        });
                    }
                }
            })).ToArray();

            Task[] readers = Enumerable.Range(0, 4).Select(readerId => Task.Run(() =>
            {
                string targetFile = $"archive_cab_{readerId:D2}";
                string originalPath = $@"C:\Games\StressTest\Bundles\{targetFile}.bundle";
                while (!cts.IsCancellationRequested)
                {
                    foreach (var _ in projectIndex.DrainPendingHandles(256))
                    {
                        Interlocked.Increment(ref drainedTotal);
                    }

                    var fileHandles = projectIndex.GetHandlesForFile(targetFile);
                    foreach (var h in fileHandles.Take(8))
                    {
                        Assert.Equal(targetFile, h.SerializedFileName);
                    }

                    long probePathId = ((long)readerId << 32) | 0u;
                    string probeUid = AssetHandle.BuildUniqueID(targetFile, originalPath, probePathId);
                    AssetHandle? lookedUp = projectIndex.GetHandle(probeUid);
                    if (lookedUp != null)
                    {
                        Assert.Equal(probePathId, lookedUp.PathID);
                    }
                }
            })).ToArray();

            await Task.WhenAll(writers);
            cts.Cancel();
            await Task.WhenAll(readers);

            // Drain any remaining handles
            drainedTotal += projectIndex.DrainPendingHandles().Count();

            int expectedUniqueCount = writerThreadCount * handlesPerThread;
            int expectedEnqueuedCount = expectedUniqueCount + (writerThreadCount * (handlesPerThread / 10));

            Assert.Equal(expectedUniqueCount, projectIndex.Count);
            Assert.Equal(expectedEnqueuedCount, drainedTotal);

            // Verify sample records after concurrent writes
            for (int threadId = 0; threadId < writerThreadCount; threadId++)
            {
                string fileName = $"archive_cab_{threadId % 4:D2}";
                string originalPath = $@"C:\Games\StressTest\Bundles\{fileName}.bundle";

                long updatedPathId = ((long)threadId << 32) | 20u;
                string updatedUid = AssetHandle.BuildUniqueID(fileName, originalPath, updatedPathId);
                AssetHandle? updated = projectIndex.GetHandle(updatedUid);
                Assert.NotNull(updated);
                Assert.Equal($"Asset_T{threadId}_20_Updated", updated!.Name);
                Assert.Equal(ClassIDType.Material, updated.Type);

                long normalPathId = ((long)threadId << 32) | 21u;
                string normalUid = AssetHandle.BuildUniqueID(fileName, originalPath, normalPathId);
                AssetHandle? normal = projectIndex.GetHandle(normalUid);
                Assert.NotNull(normal);
                Assert.Equal($"Asset_T{threadId}_21", normal!.Name);
            }
        }

        [Fact]
        public void Benchmark_200000RealisticAssetHandles_MemoryFootprintReduction()
        {
            const int assetCount = 200_000;
            const int bundleCount = 400;

            AssetHandle.ClearSourceHashCache();

            long legacyBytes = MeasureLegacyProjectIndexFootprint(assetCount, bundleCount);

            AssetHandle.ClearSourceHashCache();
            ForceFullGc();

            long soaBefore = GC.GetTotalMemory(forceFullCollection: true);
            ProjectIndex soaIndex = CreateAndPopulateSoaProjectIndex(assetCount, bundleCount, out long estimatedArenaBytes);
            ForceFullGc();
            long soaAfter = GC.GetTotalMemory(forceFullCollection: true);
            long soaBytes = Math.Max(1L, soaAfter - soaBefore);

            Assert.Equal(assetCount, soaIndex.Count);
            VerifySampleLookupsOnSoaIndex(soaIndex, assetCount, bundleCount);
            GC.KeepAlive(soaIndex);

            double legacyMb = legacyBytes / (1024.0 * 1024.0);
            double soaMb = soaBytes / (1024.0 * 1024.0);
            double estimatedMb = estimatedArenaBytes / (1024.0 * 1024.0);
            double reductionPct = (1.0 - ((double)soaBytes / legacyBytes)) * 100.0;

            _output.WriteLine(
                $"200,000 AssetHandles RAM Benchmark: Legacy={legacyMb:F2} MB ({legacyBytes:N0} B) -> " +
                $"SoA ProjectIndex={soaMb:F2} MB ({soaBytes:N0} B, ArenaEstimate={estimatedMb:F2} MB) | " +
                $"Reduction={reductionPct:F1}% ({legacyMb - soaMb:F2} MB saved)");

            Assert.True(
                reductionPct >= 55.0,
                $"Expected at least 55% RAM reduction on 200K handles, but got {reductionPct:F1}% (Legacy={legacyMb:F2} MB, SoA={soaMb:F2} MB).");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long MeasureLegacyProjectIndexFootprint(int assetCount, int bundleCount)
        {
            ForceFullGc();
            long before = GC.GetTotalMemory(forceFullCollection: true);

            var legacyHandles = new ConcurrentDictionary<string, LegacyHandleStub>(StringComparer.Ordinal);
            var legacyByFile = new ConcurrentDictionary<string, ConcurrentBag<LegacyHandleStub>>(StringComparer.Ordinal);
            var legacyPending = new ConcurrentQueue<LegacyHandleStub>();

            for (int i = 0; i < assetCount; i++)
            {
                int bundleIdx = i % bundleCount;
                int dirIdx = i % 120;
                int prefabIdx = i % 2500;
                long pathId = 100_000L + i;

                // Simulate fresh string allocations as produced by SQLiteDataReader / ObjectReader during project load
                string serializedFileName = string.Create(CultureInfo.InvariantCulture, $"CAB-{bundleIdx:x8}00112233445566778899aabb");
                string originalPath = string.Create(CultureInfo.InvariantCulture, $@"C:\Games\StarRail\Game_Data\StreamingAssets\Asb\Windows\bundle_{bundleIdx:D4}.unity3d");
                string sourceHash = AssetHandle.BuildSourceHash(serializedFileName, originalPath);
                string uniqueId = string.Create(CultureInfo.InvariantCulture, $"{serializedFileName}#{sourceHash}#{pathId}");
                string name = string.Create(CultureInfo.InvariantCulture, $"Chr_Avatar_{dirIdx:D3}_MeshPart_{i % 64:D2}");
                string container = string.Create(
                    CultureInfo.InvariantCulture,
                    $"assets/game/content/characters/npc_group_{dirIdx:D3}/avatar_{prefabIdx:D4}/prefabs/avatar_model_{prefabIdx:D4}.prefab");

                var stub = new LegacyHandleStub
                {
                    UniqueID = uniqueId,
                    Name = name,
                    Type = (ClassIDType)((i % 6) + 1),
                    Container = container,
                    OriginalPath = originalPath,
                    SerializedFileName = serializedFileName,
                    PathID = pathId,
                    ByteStart = i * 512L,
                    ByteSize = 512L
                };

                legacyHandles[uniqueId] = stub;
                legacyPending.Enqueue(stub);
                var bag = legacyByFile.GetOrAdd(serializedFileName, static _ => new ConcurrentBag<LegacyHandleStub>());
                bag.Add(stub);
            }

            ForceFullGc();
            long after = GC.GetTotalMemory(forceFullCollection: true);
            Assert.Equal(assetCount, legacyHandles.Count);
            GC.KeepAlive(legacyHandles);
            GC.KeepAlive(legacyByFile);
            GC.KeepAlive(legacyPending);

            return Math.Max(1L, after - before);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ProjectIndex CreateAndPopulateSoaProjectIndex(int assetCount, int bundleCount, out long estimatedArenaBytes)
        {
            var projectIndex = new ProjectIndex();
            PopulateSoaProjectIndexBatch(projectIndex, assetCount, bundleCount);
            estimatedArenaBytes = projectIndex.Arena.EstimateMemoryBytes();
            return projectIndex;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PopulateSoaProjectIndexBatch(ProjectIndex projectIndex, int assetCount, int bundleCount)
        {
            for (int i = 0; i < assetCount; i++)
            {
                int bundleIdx = i % bundleCount;
                int dirIdx = i % 120;
                int prefabIdx = i % 2500;
                long pathId = 100_000L + i;

                string serializedFileName = string.Create(CultureInfo.InvariantCulture, $"CAB-{bundleIdx:x8}00112233445566778899aabb");
                string originalPath = string.Create(CultureInfo.InvariantCulture, $@"C:\Games\StarRail\Game_Data\StreamingAssets\Asb\Windows\bundle_{bundleIdx:D4}.unity3d");
                string sourceHash = AssetHandle.BuildSourceHash(serializedFileName, originalPath);
                string uniqueId = string.Create(CultureInfo.InvariantCulture, $"{serializedFileName}#{sourceHash}#{pathId}");
                string name = string.Create(CultureInfo.InvariantCulture, $"Chr_Avatar_{dirIdx:D3}_MeshPart_{i % 64:D2}");
                string container = string.Create(
                    CultureInfo.InvariantCulture,
                    $"assets/game/content/characters/npc_group_{dirIdx:D3}/avatar_{prefabIdx:D4}/prefabs/avatar_model_{prefabIdx:D4}.prefab");

                projectIndex.AddHandle(new AssetHandle
                {
                    UniqueID = uniqueId,
                    Name = name,
                    Type = (ClassIDType)((i % 6) + 1),
                    Container = container,
                    OriginalPath = originalPath,
                    SerializedFileName = serializedFileName,
                    PathID = pathId,
                    ByteStart = i * 512L,
                    ByteSize = 512L
                });
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void VerifySampleLookupsOnSoaIndex(ProjectIndex projectIndex, int assetCount, int bundleCount)
        {
            int sampleIdx = assetCount - 1;
            int bundleIdx = sampleIdx % bundleCount;
            long pathId = 100_000L + sampleIdx;
            string serializedFileName = $"CAB-{bundleIdx:x8}00112233445566778899aabb";
            string originalPath = $@"C:\Games\StarRail\Game_Data\StreamingAssets\Asb\Windows\bundle_{bundleIdx:D4}.unity3d";
            string uid = AssetHandle.BuildUniqueID(serializedFileName, originalPath, pathId);

            AssetHandle? resolved = projectIndex.GetHandle(uid);
            Assert.NotNull(resolved);
            Assert.Equal(pathId, resolved!.PathID);
            Assert.Equal(serializedFileName, resolved.SerializedFileName);
        }

        private static void ForceFullGc()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private sealed class LegacyHandleStub
        {
            public string UniqueID { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public ClassIDType Type { get; set; }
            public string Container { get; set; } = string.Empty;
            public string OriginalPath { get; set; } = string.Empty;
            public string SerializedFileName { get; set; } = string.Empty;
            public SerializedFile? SourceFile { get; set; }
            public long PathID { get; set; }
            public long ByteStart { get; set; }
            public long ByteSize { get; set; }
            public Object? RealObject { get; set; }
            public object? Tag { get; set; }
        }
    }
}
