using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class SQLiteReadPoolTests
    {
        [Fact]
        public void SequentialQueries_ReuseSinglePooledReadConnectionAndPreservePragmas()
        {
            var tempRoot = CreateTempDirectory("read_pool_hits");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 8);
                using (cache)
                {
                    var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 8);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                    Assert.Equal(0L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(0L, cache.ReadPoolHits);

                    // 20 sequential semantic / ExecuteProjectReadQuery lookups against the same DB
                    for (var i = 0; i < 20; i++)
                    {
                        switch (i % 5)
                        {
                            case 0:
                                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                                break;
                            case 1:
                                Assert.Equal(new[] { sampleIds.TextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, sampleIds.MaterialId));
                                break;
                            case 2:
                                Assert.Equal(new[] { sampleIds.RendererId }, cache.LoadMeshRendererAssetIds(projectDir, signature, sampleIds.MeshId));
                                break;
                            case 3:
                                Assert.Equal(new[] { sampleIds.MeshId }, cache.LoadAvatarMeshAssetIds(projectDir, signature, sampleIds.AvatarId));
                                break;
                            default:
                                var observedProjectId = 0L;
                                cache.ExecuteProjectReadQuery(projectDir, signature, "direct pool check", (conn, tx, projectId) =>
                                {
                                    Assert.NotNull(tx);
                                    observedProjectId = projectId;
                                    Assert.Equal(1L, ReadPragmaInt64(conn, "query_only"));
                                });
                                Assert.True(observedProjectId > 0);
                                break;
                        }
                    }

                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(19L, cache.ReadPoolHits);

                    var dbPath = cache.GetDbPath(projectDir);
                    Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPath));

                    // Verify PRAGMAs remain intact on the pooled connection without re-opening or re-running PRAGMAs
                    using (var lease = cache.RentReadConnection(dbPath))
                    {
                        Assert.True(lease.IsPoolHit);
                        Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                        Assert.Equal(20L, cache.ReadPoolHits);
                        Assert.Equal(ConnectionState.Open, lease.Connection.State);

                        Assert.Equal(1L, ReadPragmaInt64(lease.Connection, "query_only"));
                        Assert.Equal(5000L, ReadPragmaInt64(lease.Connection, "busy_timeout"));
                        Assert.Equal(2L, ReadPragmaInt64(lease.Connection, "temp_store"));
                        Assert.Equal(-65536L, ReadPragmaInt64(lease.Connection, "cache_size"));
                        Assert.Equal(268435456L, ReadPragmaInt64(lease.Connection, "mmap_size"));

                        using var writeAttempt = lease.Connection.CreateCommand();
                        writeAttempt.CommandText = "CREATE TABLE ShouldFailReadOnly(Id INTEGER);";
                        Assert.Throws<SqliteException>(() => writeAttempt.ExecuteNonQuery());
                    }

                    Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPath));
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task ConcurrentReadStress_16Threads320Queries_ZeroExceptionsAccurateResultsAndBoundedPool()
        {
            var tempRoot = CreateTempDirectory("read_pool_stress");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 24);
                using (cache)
                {
                    var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 24);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                    const int threadCount = 16;
                    const int queriesPerThread = 20; // 16 * 20 = 320 parallel semantic queries
                    using var startGate = new ManualResetEventSlim(false);
                    var failures = new ConcurrentQueue<Exception>();
                    var completedQueries = 0;

                    var tasks = new Task[threadCount];
                    for (var t = 0; t < threadCount; t++)
                    {
                        var workerIndex = t;
                        tasks[t] = Task.Run(() =>
                        {
                            try
                            {
                                startGate.Wait();
                                for (var q = 0; q < queriesPerThread; q++)
                                {
                                    switch ((workerIndex + q) % 6)
                                    {
                                        case 0:
                                            Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                                            break;
                                        case 1:
                                            Assert.Equal(new[] { sampleIds.TextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, sampleIds.MaterialId));
                                            break;
                                        case 2:
                                            Assert.Equal(new[] { sampleIds.RendererId }, cache.LoadMeshRendererAssetIds(projectDir, signature, sampleIds.MeshId));
                                            break;
                                        case 3:
                                            Assert.Equal(new[] { sampleIds.MeshId }, cache.LoadAvatarMeshAssetIds(projectDir, signature, sampleIds.AvatarId));
                                            break;
                                        case 4:
                                            Assert.Equal(new[] { sampleIds.AvatarId }, cache.LoadAnimationClipAvatarAssetIds(projectDir, signature, sampleIds.ClipId));
                                            break;
                                        default:
                                            var groups = cache.LoadModelGroupsForAvatarAssetId(projectDir, signature, sampleIds.AvatarId);
                                            var group = Assert.Single(groups);
                                            Assert.Equal(sampleIds.GroupId, group.GroupId);
                                            break;
                                    }

                                    Interlocked.Increment(ref completedQueries);
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Enqueue(ex);
                            }
                        });
                    }

                    startGate.Set();
                    await Task.WhenAll(tasks);

                    Assert.Empty(failures);
                    Assert.Equal(threadCount * queriesPerThread, completedQueries);

                    var dbPath = cache.GetDbPath(projectDir);
                    var cap = SQLiteProjectIndexCache.MaxIdleReadConnectionsPerDatabase;
                    var idleCount = cache.GetIdleReadConnectionCount(dbPath);
                    Assert.InRange(idleCount, 1, cap);
                    Assert.True(cache.ReadPoolHits > 0, "Expected concurrent reads to reuse pooled connections.");

                    // Exceed cap simultaneously, then return all leases -> idle count must stay bounded at cap
                    var overCap = cap + 5;
                    var heldLeases = new List<SQLiteProjectIndexCache.PooledReadConnectionLease>(overCap);
                    try
                    {
                        for (var i = 0; i < overCap; i++)
                        {
                            heldLeases.Add(cache.RentReadConnection(dbPath));
                        }
                    }
                    finally
                    {
                        for (var i = 0; i < heldLeases.Count; i++)
                        {
                            heldLeases[i].Dispose();
                        }
                    }

                    Assert.Equal(cap, cache.GetIdleReadConnectionCount(dbPath));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task DeleteIndexCacheAfterHeavyParallelReads_SucceedsImmediatelyWithoutFileLockAndRebuildsCleanly()
        {
            var tempRoot = CreateTempDirectory("read_pool_delete");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 16);
                using (cache)
                {
                    var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 16);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                    using var startGate = new ManualResetEventSlim(false);
                    var tasks = new Task[16];
                    for (var t = 0; t < tasks.Length; t++)
                    {
                        tasks[t] = Task.Run(() =>
                        {
                            startGate.Wait();
                            for (var i = 0; i < 20; i++)
                            {
                                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                            }
                        });
                    }

                    startGate.Set();
                    await Task.WhenAll(tasks);

                    var dbPath = cache.GetDbPath(projectDir);
                    Assert.True(cache.GetIdleReadConnectionCount(dbPath) >= 1);
                    Assert.True(File.Exists(dbPath));

                    // Must invalidate pooled read connections and delete DB + WAL + SHM immediately on Windows with zero IOException
                    cache.DeleteIndexCache(projectDir);

                    Assert.Equal(0, cache.GetIdleReadConnectionCount(dbPath));
                    Assert.True(cache.GetReadPoolGeneration(dbPath) >= 1);
                    Assert.False(File.Exists(dbPath));
                    Assert.False(File.Exists(dbPath + "-wal"));
                    Assert.False(File.Exists(dbPath + "-shm"));

                    // Recreating and querying the same project path afterwards works cleanly
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));
                    Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void LeaseCheckedOutBeforeInvalidation_IsDisposedInsteadOfRePooledOnReturn()
        {
            var tempRoot = CreateTempDirectory("read_pool_epoch");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 4);
                using (cache)
                {
                    var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 4);
                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                    var dbPath = cache.GetDbPath(projectDir);
                    Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                    Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPath));

                    var checkedOutBeforeInvalidation = cache.RentReadConnection(dbPath);
                    var staleConn = checkedOutBeforeInvalidation.Connection;
                    var initialGen = checkedOutBeforeInvalidation.Generation;
                    Assert.Equal(ConnectionState.Open, staleConn.State);
                    Assert.Equal(0, cache.GetIdleReadConnectionCount(dbPath));

                    // Invalidate while lease is actively held
                    cache.InvalidateReadConnectionPool(dbPath);
                    Assert.Equal(initialGen + 1, cache.GetReadPoolGeneration(dbPath));
                    Assert.Equal(0, cache.GetIdleReadConnectionCount(dbPath));
                    Assert.Equal(ConnectionState.Open, staleConn.State);

                    // Returning stale-epoch lease must dispose it immediately rather than returning it to the pool
                    checkedOutBeforeInvalidation.Dispose();
                    Assert.Equal(ConnectionState.Closed, staleConn.State);
                    Assert.Equal(0, cache.GetIdleReadConnectionCount(dbPath));

                    // Next rent must open a fresh connection on the new epoch
                    using var freshLease = cache.RentReadConnection(dbPath);
                    Assert.False(freshLease.IsPoolHit);
                    Assert.False(ReferenceEquals(staleConn, freshLease.Connection));
                    Assert.Equal(initialGen + 1, freshLease.Generation);
                    Assert.Equal(ConnectionState.Open, freshLease.Connection.State);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MultiDatabaseIsolation_ReleaseReadConnections_AndDispose_CleanlyReleaseAllHandles()
        {
            var tempRoot = CreateTempDirectory("read_pool_isolation");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectA = Path.Combine(tempRoot, "ProjectA");
                var projectB = Path.Combine(tempRoot, "ProjectB");
                Directory.CreateDirectory(projectA);
                Directory.CreateDirectory(projectB);

                var sourceA = Path.Combine(projectA, "level0");
                var sourceB = Path.Combine(projectB, "level0");
                File.WriteAllBytes(sourceA, new byte[128]);
                File.WriteAllBytes(sourceB, new byte[256]);

                var scanA = ProjectScanner.ScanFolder(projectA);
                var scanB = ProjectScanner.ScanFolder(projectB);

                var cache = new SQLiteProjectIndexCache(cacheDir);
                var sigA = cache.GetFolderSignature(scanA);
                var sigB = cache.GetFolderSignature(scanB);
                var (handlesA, relationsA, idsA) = BuildSyntheticProjectGraph(sourceA, groupCount: 4);
                var (handlesB, relationsB, idsB) = BuildSyntheticProjectGraph(sourceB, groupCount: 4);

                Assert.True(cache.SaveIndexCache(projectA, sigA, scanA, "2022.3.10f1", handlesA));
                Assert.True(cache.SaveSemanticRelations(projectA, sigA, relationsA, replaceExisting: true));
                Assert.True(cache.SaveIndexCache(projectB, sigB, scanB, "2022.3.10f1", handlesB));
                Assert.True(cache.SaveSemanticRelations(projectB, sigB, relationsB, replaceExisting: true));

                Assert.Equal(new[] { idsA.MaterialId }, cache.LoadMeshMaterialAssetIds(projectA, sigA, idsA.MeshId));
                Assert.Equal(new[] { idsB.MaterialId }, cache.LoadMeshMaterialAssetIds(projectB, sigB, idsB.MeshId));
                Assert.Equal(2L, cache.PhysicalReadConnectionsOpened);

                var dbPathA = cache.GetDbPath(projectA);
                var dbPathB = cache.GetDbPath(projectB);
                Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPathA));
                Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPathB));

                // Deleting projectA must not disturb projectB's pooled read connection
                cache.DeleteIndexCache(projectA);
                Assert.False(File.Exists(dbPathA));
                Assert.Equal(0, cache.GetIdleReadConnectionCount(dbPathA));
                Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPathB));

                Assert.Equal(new[] { idsB.MaterialId }, cache.LoadMeshMaterialAssetIds(projectB, sigB, idsB.MeshId));
                Assert.Equal(2L, cache.PhysicalReadConnectionsOpened);
                Assert.Equal(1L, cache.ReadPoolHits);

                // ReleaseReadConnections must drain all pooled handles so direct File.Delete succeeds on Windows
                cache.ReleaseReadConnections();
                Assert.Equal(0, cache.GetTotalIdleReadConnectionCount());
                File.Delete(dbPathB);
                Assert.False(File.Exists(dbPathB));

                cache.Dispose();
                Assert.Throws<ObjectDisposedException>(() => cache.RentReadConnection(dbPathB));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void WalReadAfterWrite_AndFaultedQueryRecovery_WorkCleanlyWithPooledConnections()
        {
            var tempRoot = CreateTempDirectory("read_pool_wal_fault");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 4);
                using (cache)
                {
                    var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 4);
                    var extraMeshId = AssetHandle.BuildUniqueID("level0", sourcePath, 9101);
                    var extraMaterialId = AssetHandle.BuildUniqueID("level0", sourcePath, 9102);
                    var extraRendererId = AssetHandle.BuildUniqueID("level0", sourcePath, 9104);

                    handles.Add(CreateHandle(extraMeshId, "ExtraMesh", ClassIDType.Mesh, sourcePath, 9101));
                    handles.Add(CreateHandle(extraMaterialId, "ExtraMat", ClassIDType.Material, sourcePath, 9102));
                    handles.Add(CreateHandle(extraRendererId, "ExtraRenderer", ClassIDType.SkinnedMeshRenderer, sourcePath, 9104));

                    Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                    // Warm pooled read connection before incremental write
                    Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                    Assert.Empty(cache.LoadMeshMaterialAssetIds(projectDir, signature, extraMeshId));
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(1L, cache.ReadPoolHits);

                    // Commit new semantic relation while read connection is pooled
                    var incremental = new SemanticAssetRelations();
                    incremental.MeshRenderers.Add(new SemanticMeshRendererRelation(
                        extraMeshId, extraRendererId, "SkinnedMeshRenderer", string.Empty, "ExtraGo", "Incremental"));
                    incremental.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                        extraMeshId, extraMaterialId, extraRendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 250));
                    Assert.True(cache.SaveSemanticRelations(projectDir, signature, incremental, replaceExisting: false));

                    // Pooled read connection must hit the pool and see the newly committed WAL snapshot
                    Assert.Equal(new[] { extraMaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, extraMeshId));
                    Assert.Equal(1L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(2L, cache.ReadPoolHits);

                    // Faulted ExecuteProjectReadQuery must discard unhealthy connection instead of re-pooling it
                    var dbPath = cache.GetDbPath(projectDir);
                    cache.ExecuteProjectReadQuery(projectDir, signature, "intentional test error", (conn, tx, projectId) =>
                    {
                        using var badCmd = conn.CreateCommand();
                        badCmd.Transaction = tx;
                        badCmd.CommandText = "SELECT * FROM NonExistentTable_ReadPoolTest;";
                        badCmd.ExecuteNonQuery();
                    });

                    Assert.Equal(0, cache.GetIdleReadConnectionCount(dbPath));

                    // Subsequent valid query recovers cleanly by opening a fresh pooled connection
                    Assert.Equal(new[] { extraMaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, extraMeshId));
                    Assert.Equal(2L, cache.PhysicalReadConnectionsOpened);
                    Assert.Equal(1, cache.GetIdleReadConnectionCount(dbPath));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static long ReadPragmaInt64(SqliteConnection conn, string pragmaName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragmaName};";
            var value = cmd.ExecuteScalar();
            Assert.NotNull(value);
            return Convert.ToInt64(value);
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, ProjectScanResult ScanResult, string SourcePath)
            CreateSampleProject(string tempRoot, int assetGroupCount)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = Path.Combine(tempRoot, "SampleProject");
            Directory.CreateDirectory(projectDir);
            var sourcePath = Path.Combine(projectDir, "level0");
            File.WriteAllBytes(sourcePath, new byte[Math.Max(16, assetGroupCount * 8)]);

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePath);
        }

        private sealed record SampleGraphIds(
            string MeshId,
            string MaterialId,
            string PreviewMaterialId,
            string TextureId,
            string AvatarId,
            string ClipId,
            string RendererId,
            string GroupId);

        private static (List<AssetHandle> Handles, SemanticAssetRelations Relations, SampleGraphIds SampleIds)
            BuildSyntheticProjectGraph(string sourcePath, int groupCount)
        {
            var handles = new List<AssetHandle>(groupCount * 7);
            var relations = new SemanticAssetRelations();
            relations.SourceFiles.Add(new SemanticSourceFileEntry("level0", sourcePath, "2022.3.10f1", groupCount * 7));

            SampleGraphIds? sampleIds = null;
            for (var i = 0; i < groupCount; i++)
            {
                var basePathId = (i + 1) * 100L;
                var meshId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 1);
                var materialId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 2);
                var previewMaterialId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 3);
                var textureId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 4);
                var avatarId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 5);
                var clipId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 6);
                var rendererId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 7);
                var rootGoId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 8);
                var groupId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 9);

                handles.Add(CreateHandle(meshId, $"Mesh_{i:D3}", ClassIDType.Mesh, sourcePath, basePathId + 1));
                handles.Add(CreateHandle(materialId, $"Mat_{i:D3}", ClassIDType.Material, sourcePath, basePathId + 2));
                handles.Add(CreateHandle(previewMaterialId, $"PreviewMat_{i:D3}", ClassIDType.Material, sourcePath, basePathId + 3));
                handles.Add(CreateHandle(textureId, $"Tex_{i:D3}", ClassIDType.Texture2D, sourcePath, basePathId + 4));
                handles.Add(CreateHandle(avatarId, $"Avatar_{i:D3}", ClassIDType.Avatar, sourcePath, basePathId + 5));
                handles.Add(CreateHandle(clipId, $"Clip_{i:D3}", ClassIDType.AnimationClip, sourcePath, basePathId + 6));
                handles.Add(CreateHandle(rendererId, $"Renderer_{i:D3}", ClassIDType.SkinnedMeshRenderer, sourcePath, basePathId + 7));

                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "Mesh", "AvatarMesh", 0, meshId, 0, basePathId + 5, 0, basePathId + 1, isResolved: true));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    clipId, "Avatar", "AnimatorAvatar", 0, avatarId, 0, basePathId + 6, 0, basePathId + 5, isResolved: true));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "AnimationClip", "AnimatorClip", 0, clipId, 0, basePathId + 5, 0, basePathId + 6, isResolved: true));

                relations.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", $"Rig_{i:D3}", rootGoId, $"Root_{i:D3}", string.Empty, avatarId, string.Empty, "level0", 90, "ExactMatch"));
                relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_{i:D3}", 0, null, 90, "DirectRenderer"));
                relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_{i:D3}", "Primary body"));
                relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshId, materialId, rendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 100));
                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId, previewMaterialId, "_BaseMap", 0, textureId, 0, basePathId + 4, isResolved: true, isMainTexture: true));

                if (i == groupCount / 2)
                {
                    sampleIds = new SampleGraphIds(meshId, materialId, previewMaterialId, textureId, avatarId, clipId, rendererId, groupId);
                }
            }

            return (handles, relations, sampleIds!);
        }

        private static AssetHandle CreateHandle(string uniqueId, string name, ClassIDType type, string sourcePath, long pathId)
        {
            return new AssetHandle
            {
                UniqueID = uniqueId,
                Name = name,
                Type = type,
                Container = $"assets/{name.ToLowerInvariant()}",
                OriginalPath = sourcePath,
                SerializedFileName = "level0",
                PathID = pathId,
                ByteStart = pathId * 64,
                ByteSize = 1024
            };
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
