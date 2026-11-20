using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class ConnectionsStagingTests
    {
        private const string OthercideBundlesDir =
            @"H:\SteamLibrary\steamapps\common\Othercide\Othercide_Data\StreamingAssets\aa\Windows\StandaloneWindows64";

        private static readonly string[] LiveSemanticTables =
        {
            "SourceFiles",
            "AssetEdges",
            "ModelGroups",
            "ModelGroupMeshes",
            "MeshRenderers",
            "MeshMaterials",
            "MaterialTextures"
        };

        [Fact]
        public void MultiBatchStreaming_MatchesSaveSemanticRelationsIdentically_DropsStagingTables_AndUsesZeroPhysicalScans()
        {
            var tempRoot = CreateTempDirectory("connections_staging_multibatch");
            try
            {
                const int groupCount = 80;
                const int batchCount = 5;

                var stagedRoot = Path.Combine(tempRoot, "Staged");
                var baselineRoot = Path.Combine(tempRoot, "Baseline");
                var (stagedCache, stagedProjectDir, stagedSig, stagedScan, sourcePath) = CreateSampleProject(stagedRoot, groupCount);
                using var _stagedCache = stagedCache;
                var (baselineCache, baselineProjectDir, baselineSig, baselineScan, _) = CreateSampleProject(baselineRoot, groupCount, existingProjectDir: stagedProjectDir);
                using var _baselineCache = baselineCache;

                var (handles, batches, mergedBaseline, sampleIds) = BuildMultiBatchSyntheticGraphWithOverlaps(sourcePath, groupCount, batchCount);

                Assert.True(stagedCache.SaveIndexCache(stagedProjectDir, stagedSig, stagedScan, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(baselineCache.SaveIndexCache(baselineProjectDir, baselineSig, baselineScan, "2022.3.10f1", handles, preserveSemanticRelations: false));

                using (var session = stagedCache.BeginSemanticStagingSession(stagedProjectDir, stagedSig))
                {
                    // Staging tables must exist while session is active.
                    using (var conn = stagedCache.CreateReadConnection(stagedProjectDir))
                    {
                        var activeStageTables = GetStageTableNames(conn);
                        foreach (var expectedStageTable in SQLiteProjectIndexCache.SemanticStagingTableNames)
                        {
                            Assert.Contains(expectedStageTable, activeStageTables);
                        }
                    }

                    foreach (var batch in batches)
                    {
                        Assert.True(MainWindow.CountBufferedSemanticRelationRows(batch) > 0);
                        session.FlushBatch(batch, clearBatch: true);

                        // In-memory batch lists must be cleared immediately after each flush to keep RAM flat.
                        Assert.Equal(0, MainWindow.CountBufferedSemanticRelationRows(batch));
                        Assert.Empty(batch.SourceFiles);
                        Assert.Empty(batch.AssetEdges);
                        Assert.Empty(batch.ModelGroups);
                        Assert.Empty(batch.ModelGroupMeshes);
                        Assert.Empty(batch.MeshRenderers);
                        Assert.Empty(batch.MeshMaterials);
                        Assert.Empty(batch.MaterialTextures);
                    }

                    Assert.Equal(batchCount, session.FlushCount);
                    Assert.True(session.HasStagedRelations);
                    Assert.True(session.HasStagedMaterialRelations);
                    Assert.True(session.TotalStagedRows > groupCount * 6);

                    // Before Commit(), live semantic tables must still be completely empty.
                    Assert.False(stagedCache.HasSemanticRelations(stagedProjectDir, stagedSig));

                    Assert.True(session.Commit());
                    Assert.True(session.IsCommitted);
                }

                // Baseline bulk save for 100% parity comparison.
                Assert.True(baselineCache.SaveSemanticRelations(baselineProjectDir, baselineSig, mergedBaseline, replaceExisting: true));

                using (var stagedConn = stagedCache.CreateReadConnection(stagedProjectDir))
                using (var baselineConn = baselineCache.CreateReadConnection(baselineProjectDir))
                {
                    // All Stage_* tables must be dropped after commit.
                    Assert.Empty(GetStageTableNames(stagedConn));

                    // Indexing state must be connections_completed.
                    var state = stagedCache.LoadIndexingState(stagedProjectDir, stagedSig);
                    Assert.NotNull(state);
                    Assert.Equal("connections_completed", state!.Status);

                    // Row-by-row, column-by-column parity across every semantic table.
                    AssertSemanticTablesMatchIdentically(stagedConn, baselineConn);

                    // Verify synchronous mode was restored to NORMAL (1) and ANALYZE populated sqlite_stat1.
                    using (var writeConn = stagedCache.CreateConnection(stagedProjectDir))
                    {
                        Assert.Equal(1L, ExecuteScalarInt64(writeConn, "PRAGMA synchronous;"));
                    }
                    Assert.True(ExecuteScalarInt64(stagedConn, "SELECT COUNT(*) FROM sqlite_stat1;") > 0);

                    // Verify EXPLAIN QUERY PLAN has 0 physical table SCANs.
                    Assert.True(stagedCache.TryGetCachedProjectId(stagedProjectDir, stagedSig, out var projectId));
                    VerifyCoreSemanticQueryPlansUseZeroPhysicalScans(stagedConn, projectId, sampleIds);
                }

                // Verify all high-level read query APIs return 100% identical results.
                AssertQueryApiParity(stagedCache, stagedProjectDir, stagedSig, baselineCache, baselineProjectDir, baselineSig, sampleIds);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AtomicityOnCancelOrAbort_LeavesPreviouslyCommittedLiveGraphUntouched_AndDropsStagingTables()
        {
            var tempRoot = CreateTempDirectory("connections_staging_abort");
            try
            {
                const int groupCount = 12;
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, groupCount);
                using var _cache = cache;
                var (handles, v1Batches, v1Merged, sampleIds) = BuildMultiBatchSyntheticGraphWithOverlaps(sourcePath, groupCount, batchCount: 2);

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                // Commit v1 semantic graph via staging session.
                using (var v1Session = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    foreach (var batch in v1Batches)
                    {
                        v1Session.FlushBatch(batch, clearBatch: true);
                    }
                    Assert.True(v1Session.Commit());
                }

                Dictionary<string, List<string>> v1Snapshot;
                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Empty(GetStageTableNames(conn));
                    v1Snapshot = CaptureSemanticTablesSnapshot(conn);
                }

                // Start a second session, flush 3 batches of mutated/poisoned v2 relations, then explicitly Abort().
                var v2Batches = BuildMutatedV2Batches(sourcePath, sampleIds, batchCount: 3);
                using (var abortedSession = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    foreach (var batch in v2Batches)
                    {
                        abortedSession.FlushBatch(batch, clearBatch: true);
                    }

                    Assert.Equal(3, abortedSession.FlushCount);

                    // While staged but uncommitted, live tables must still match v1 100%.
                    using (var midConn = cache.CreateReadConnection(projectDir))
                    {
                        AssertSnapshotsEqual(v1Snapshot, CaptureSemanticTablesSnapshot(midConn));
                    }

                    abortedSession.Abort();
                    Assert.True(abortedSession.IsAborted);
                    Assert.False(abortedSession.IsCommitted);
                }

                // Start a third session, flush batches, and exit using block (Dispose without Commit or explicit Abort).
                var v3Batches = BuildMutatedV2Batches(sourcePath, sampleIds, batchCount: 2);
                using (var disposedSession = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    foreach (var batch in v3Batches)
                    {
                        disposedSession.FlushBatch(batch, clearBatch: true);
                    }
                }

                // Verify v1 live semantic graph is 100% intact and all Stage_* tables are gone.
                using (var verifyConn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Empty(GetStageTableNames(verifyConn));
                    var afterAbortSnapshot = CaptureSemanticTablesSnapshot(verifyConn);
                    AssertSnapshotsEqual(v1Snapshot, afterAbortSnapshot);
                }

                // Verify high-level read APIs still return v1 values, not v2 poison values.
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Equal(new[] { sampleIds.TextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, sampleIds.MaterialId));
                Assert.Equal(new[] { sampleIds.RendererId }, cache.LoadMeshRendererAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Equal(new[] { sampleIds.MeshId }, cache.LoadAvatarMeshAssetIds(projectDir, signature, sampleIds.AvatarId));
                Assert.NotNull(v1Merged);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void CrashRecovery_LeftoverStageTables_AreCleanedAutomaticallyByNewStagingSessionWithoutLeakingStaleRows()
        {
            var tempRoot = CreateTempDirectory("connections_staging_crash_recovery");
            try
            {
                const int groupCount = 10;
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, groupCount);
                using var _cache = cache;
                var (handles, batches, _, sampleIds) = BuildMultiBatchSyntheticGraphWithOverlaps(sourcePath, groupCount, batchCount: 2);

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                // Simulate leftover Stage_* tables from a previous hard crash (including stale rows and an obsolete schema Stage_ table).
                using (var rawConn = cache.CreateConnection(projectDir))
                {
                    using var cmd = rawConn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS Stage_AssetEdges (
                            ProjectId INTEGER NOT NULL,
                            SourceAssetUniqueID TEXT NOT NULL,
                            EdgeKind TEXT NOT NULL,
                            SlotName TEXT NOT NULL DEFAULT '',
                            SlotIndex INTEGER NOT NULL DEFAULT -1,
                            TargetAssetUniqueID TEXT NOT NULL DEFAULT '',
                            SourceFileId INTEGER NOT NULL DEFAULT 0,
                            SourcePathID INTEGER NOT NULL DEFAULT 0,
                            TargetFileId INTEGER NOT NULL DEFAULT 0,
                            TargetPathID INTEGER NOT NULL DEFAULT 0,
                            IsResolved INTEGER NOT NULL DEFAULT 0
                        );
                        INSERT INTO Stage_AssetEdges (
                            ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                            SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                        VALUES (1, 'stale_crash_source', 'Mesh', 'AvatarMesh', 0, 'stale_crash_target', 0, 999, 0, 888, 1);

                        CREATE TABLE IF NOT EXISTS Stage_MeshMaterials (
                            LegacyWrongColumn TEXT
                        );
                        INSERT INTO Stage_MeshMaterials (LegacyWrongColumn) VALUES ('corrupted_schema_from_crash');

                        CREATE TABLE IF NOT EXISTS Stage_LegacyOrphanTable (
                            Id INTEGER PRIMARY KEY,
                            Note TEXT
                        );
                        INSERT INTO Stage_LegacyOrphanTable (Note) VALUES ('should_be_dropped');";
                    cmd.ExecuteNonQuery();
                }

                // Starting a new staging session must automatically drop & recreate clean Stage_* tables without failing.
                using (var recoveredSession = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    using (var checkConn = cache.CreateReadConnection(projectDir))
                    {
                        var stageTables = GetStageTableNames(checkConn);
                        Assert.DoesNotContain("Stage_LegacyOrphanTable", stageTables);
                        Assert.Equal(0L, ExecuteScalarInt64(checkConn, "SELECT COUNT(*) FROM Stage_AssetEdges;"));
                        Assert.Equal(0L, ExecuteScalarInt64(checkConn, "SELECT COUNT(*) FROM Stage_MeshMaterials;"));
                    }

                    foreach (var batch in batches)
                    {
                        recoveredSession.FlushBatch(batch, clearBatch: true);
                    }

                    Assert.True(recoveredSession.Commit());
                }

                using (var finalConn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Empty(GetStageTableNames(finalConn));
                    Assert.Equal(
                        0L,
                        ExecuteScalarInt64(finalConn, "SELECT COUNT(*) FROM AssetEdges WHERE SourceAssetUniqueID = 'stale_crash_source';"));
                }

                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task FlushBatchSixArgumentOverload_ClearsCallerListsAndSupportsCommitAsync()
        {
            var tempRoot = CreateTempDirectory("connections_staging_overload");
            try
            {
                const int groupCount = 8;
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, groupCount);
                using var _cache = cache;
                var (handles, batches, _, sampleIds) = BuildMultiBatchSyntheticGraphWithOverlaps(sourcePath, groupCount, batchCount: 2);

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                using var session = cache.BeginSemanticStagingSession(projectDir, signature);
                foreach (var batch in batches)
                {
                    session.FlushBatch(
                        batch.AssetEdges,
                        batch.MeshRenderers,
                        batch.MeshMaterials,
                        batch.MaterialTextures,
                        batch.ModelGroups,
                        batch.ModelGroupMeshes,
                        batch.SourceFiles);

                    Assert.Empty(batch.AssetEdges);
                    Assert.Empty(batch.MeshRenderers);
                    Assert.Empty(batch.MeshMaterials);
                    Assert.Empty(batch.MaterialTextures);
                    Assert.Empty(batch.ModelGroups);
                    Assert.Empty(batch.ModelGroupMeshes);
                    Assert.Empty(batch.SourceFiles);
                }

                var progressEvents = new List<string>();
                var committed = await session.CommitAsync((processed, total, stage) =>
                {
                    if (!string.IsNullOrWhiteSpace(stage))
                    {
                        progressEvents.Add($"{processed}/{total}:{stage}");
                    }
                });

                Assert.True(committed);
                Assert.NotEmpty(progressEvents);
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void RealWorldOthercideOrSampleBundles_StreamingStagingSession_MatchesSaveSemanticRelationsWithFlatBuffers()
        {
            var tempRoot = CreateTempDirectory("connections_staging_othercide");
            try
            {
                if (Directory.Exists(OthercideBundlesDir))
                {
                    var bundleFiles = Directory.GetFiles(OthercideBundlesDir, "*.bundle", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => new FileInfo(f).Length)
                        .Take(10)
                        .ToList();

                    if (bundleFiles.Count > 0)
                    {
                        VerifyRealBundlesParityAndFlatBuffers(tempRoot, OthercideBundlesDir, bundleFiles);
                    }
                }

                // Always verify synthetic multi-batch fallback parity and read-pool disposal as well
                // so local runs and CI runners without Othercide exercise the exact same code paths.
                const int fallbackGroups = 40;
                var stagedRoot = Path.Combine(tempRoot, "FallbackStaged");
                var baselineRoot = Path.Combine(tempRoot, "FallbackBaseline");
                var (stagedCache, projectDir, sig1, scan1, sourcePath) = CreateSampleProject(stagedRoot, fallbackGroups);
                using var _stagedCache = stagedCache;
                var (baselineCache, _, sig2, scan2, _) = CreateSampleProject(baselineRoot, fallbackGroups, existingProjectDir: projectDir);
                using var _baselineCache = baselineCache;
                var (handles, batches, merged, sampleIds) = BuildMultiBatchSyntheticGraphWithOverlaps(sourcePath, fallbackGroups, batchCount: 4);

                Assert.True(stagedCache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(baselineCache.SaveIndexCache(projectDir, sig2, scan2, "2022.3.10f1", handles, preserveSemanticRelations: false));

                using (var session = stagedCache.BeginSemanticStagingSession(projectDir, sig1))
                {
                    foreach (var batch in batches)
                    {
                        session.FlushBatch(batch, clearBatch: true);
                        Assert.Equal(0, MainWindow.CountBufferedSemanticRelationRows(batch));
                    }
                    Assert.True(session.Commit());
                }

                Assert.True(baselineCache.SaveSemanticRelations(projectDir, sig2, merged, replaceExisting: true));
                AssertQueryApiParity(stagedCache, projectDir, sig1, baselineCache, projectDir, sig2, sampleIds);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
                Assert.False(Directory.Exists(tempRoot));
            }
        }

        private static void VerifyRealBundlesParityAndFlatBuffers(
            string tempRoot,
            string readOnlyProjectFolder,
            IReadOnlyList<string> sampleBundlePaths)
        {
            var stagedCacheDir = Path.Combine(tempRoot, "StagedIndexCache");
            var baselineCacheDir = Path.Combine(tempRoot, "BaselineIndexCache");
            using var stagedCache = new SQLiteProjectIndexCache(stagedCacheDir);
            using var baselineCache = new SQLiteProjectIndexCache(baselineCacheDir);

            var scanResult = ProjectScanner.ScanFolder(readOnlyProjectFolder);
            var signature = stagedCache.GetFolderSignature(scanResult);

            var assetsManager = new AssetsManager
            {
                LazyLoading = false,
                ProjectRoot = readOnlyProjectFolder
            };

            try
            {
                assetsManager.LoadFiles(sampleBundlePaths.ToArray());

                var handles = new List<AssetHandle>();
                foreach (var serializedFile in assetsManager.assetsFileList)
                {
                    if (serializedFile == null)
                    {
                        continue;
                    }

                    foreach (var obj in serializedFile.Objects)
                    {
                        if (obj == null)
                        {
                            continue;
                        }

                        var uniqueId = AssetHandle.BuildUniqueID(serializedFile, obj.m_PathID);
                        var name = obj switch
                        {
                            NamedObject named when !string.IsNullOrEmpty(named.m_Name) => named.m_Name,
                            GameObject go when !string.IsNullOrEmpty(go.m_Name) => go.m_Name,
                            _ => obj.type.ToString()
                        };

                        var handle = new AssetHandle
                        {
                            UniqueID = uniqueId,
                            Name = name,
                            Type = obj.type,
                            Container = string.Empty,
                            OriginalPath = serializedFile.originalPath ?? string.Empty,
                            SerializedFileName = serializedFile.fileName ?? string.Empty,
                            PathID = obj.m_PathID,
                            ByteStart = 0L,
                            ByteSize = obj.byteSize,
                            SourceFile = serializedFile,
                            RealObject = obj
                        };
                        handles.Add(handle);
                        assetsManager.ProjectIndex.AddHandle(handle);
                    }
                }

                Assert.NotEmpty(handles);
                Assert.True(stagedCache.SaveIndexCache(readOnlyProjectFolder, signature, scanResult, "2019.4.1f1", handles, preserveSemanticRelations: false));
                Assert.True(baselineCache.SaveIndexCache(readOnlyProjectFolder, signature, scanResult, "2019.4.1f1", handles, preserveSemanticRelations: false));

                var baselineMerged = new SemanticAssetRelations();
                var streamingBuffer = new SemanticAssetRelations();

                using (var session = stagedCache.BeginSemanticStagingSession(readOnlyProjectFolder, signature))
                {
                    foreach (var serializedFile in assetsManager.assetsFileList)
                    {
                        if (serializedFile == null)
                        {
                            continue;
                        }

                        var fileRelations = new SemanticAssetRelations();
                        MainWindow.BuildSemanticRelationsForFilesBackground(new[] { serializedFile }, fileRelations);

                        baselineMerged.Merge(fileRelations);
                        streamingBuffer.Merge(fileRelations);

                        if (MainWindow.CountBufferedSemanticRelationRows(streamingBuffer) > 0)
                        {
                            session.FlushBatch(streamingBuffer, clearBatch: true);
                            // Verify streaming buffer was immediately emptied after flush (bounded RAM).
                            Assert.Equal(0, MainWindow.CountBufferedSemanticRelationRows(streamingBuffer));
                        }
                    }

                    if (!baselineMerged.HasRelations && baselineMerged.SourceFiles.Count == 0)
                    {
                        return;
                    }

                    Assert.True(session.Commit());
                }

                Assert.True(baselineCache.SaveSemanticRelations(readOnlyProjectFolder, signature, baselineMerged, replaceExisting: true));

                using var stagedConn = stagedCache.CreateReadConnection(readOnlyProjectFolder);
                using var baselineConn = baselineCache.CreateReadConnection(readOnlyProjectFolder);
                Assert.Empty(GetStageTableNames(stagedConn));
                AssertSemanticTablesMatchIdentically(stagedConn, baselineConn);
            }
            finally
            {
                assetsManager.Clear();
            }
        }

        private static void AssertSemanticTablesMatchIdentically(SqliteConnection stagedConn, SqliteConnection baselineConn)
        {
            var stagedSnapshot = CaptureSemanticTablesSnapshot(stagedConn);
            var baselineSnapshot = CaptureSemanticTablesSnapshot(baselineConn);
            AssertSnapshotsEqual(baselineSnapshot, stagedSnapshot);
        }

        private static void AssertSnapshotsEqual(
            Dictionary<string, List<string>> expected,
            Dictionary<string, List<string>> actual)
        {
            foreach (var table in LiveSemanticTables)
            {
                Assert.True(expected.TryGetValue(table, out var expectedRows), $"Missing table '{table}' in expected snapshot.");
                Assert.True(actual.TryGetValue(table, out var actualRows), $"Missing table '{table}' in actual snapshot.");
                Assert.Equal(expectedRows!, actualRows!);
            }
        }

        private static Dictionary<string, List<string>> CaptureSemanticTablesSnapshot(SqliteConnection conn)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            result["SourceFiles"] = QueryFormattedRows(conn, @"
                SELECT SerializedFileName, OriginalPath, UnityVersion, ObjectCount
                FROM SourceFiles
                ORDER BY SerializedFileName, OriginalPath;");

            result["AssetEdges"] = QueryFormattedRows(conn, @"
                SELECT SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                       SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved
                FROM AssetEdges
                ORDER BY SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID;");

            result["ModelGroups"] = QueryFormattedRows(conn, @"
                SELECT GroupAssetUniqueID, GroupKind, GroupName, RootGameObjectAssetUniqueID, RootGameObjectName,
                       AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID, SourceFileName, Confidence, ConfidenceReason
                FROM ModelGroups
                ORDER BY GroupAssetUniqueID;");

            result["ModelGroupMeshes"] = QueryFormattedRows(conn, @"
                SELECT GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID, RendererType,
                       GameObjectAssetUniqueID, GameObjectName, SlotIndex, COALESCE(hex(TransformMatrix), 'NULL'),
                       Confidence, ConfidenceReason
                FROM ModelGroupMeshes
                ORDER BY GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID;");

            result["MeshRenderers"] = QueryFormattedRows(conn, @"
                SELECT MeshAssetUniqueID, RendererAssetUniqueID, RendererType,
                       COALESCE(GameObjectAssetUniqueID, ''), COALESCE(GameObjectName, ''), COALESCE(Description, '')
                FROM MeshRenderers
                ORDER BY MeshAssetUniqueID, RendererAssetUniqueID, RendererType;");

            result["MeshMaterials"] = QueryFormattedRows(conn, @"
                SELECT MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex,
                       MaterialAssetUniqueID, RendererType, MaterialScore
                FROM MeshMaterials
                ORDER BY MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID;");

            result["MaterialTextures"] = QueryFormattedRows(conn, @"
                SELECT MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID,
                       TextureAssetUniqueID, PreviewMaterialAssetUniqueID, IsResolved, IsMainTexture
                FROM MaterialTextures
                ORDER BY MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID;");

            return result;
        }

        private static List<string> QueryFormattedRows(SqliteConnection conn, string sql)
        {
            var rows = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var parts = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    parts[i] = reader.IsDBNull(i)
                        ? "<NULL>"
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                }
                rows.Add(string.Join("\u001f", parts));
            }
            return rows;
        }

        private static void AssertQueryApiParity(
            SQLiteProjectIndexCache stagedCache,
            string stagedDir,
            string stagedSig,
            SQLiteProjectIndexCache baselineCache,
            string baselineDir,
            string baselineSig,
            SampleGraphIds sampleIds)
        {
            Assert.Equal(
                baselineCache.LoadMeshMaterialAssetIds(baselineDir, baselineSig, sampleIds.MeshId),
                stagedCache.LoadMeshMaterialAssetIds(stagedDir, stagedSig, sampleIds.MeshId));
            Assert.Equal(
                baselineCache.LoadMaterialTextureAssetIds(baselineDir, baselineSig, sampleIds.MaterialId),
                stagedCache.LoadMaterialTextureAssetIds(stagedDir, stagedSig, sampleIds.MaterialId));
            Assert.Equal(
                baselineCache.LoadMaterialTextureAssetIds(baselineDir, baselineSig, sampleIds.PreviewMaterialId),
                stagedCache.LoadMaterialTextureAssetIds(stagedDir, stagedSig, sampleIds.PreviewMaterialId));
            Assert.Equal(
                baselineCache.LoadMeshRendererAssetIds(baselineDir, baselineSig, sampleIds.MeshId),
                stagedCache.LoadMeshRendererAssetIds(stagedDir, stagedSig, sampleIds.MeshId));
            Assert.Equal(
                baselineCache.LoadAvatarMeshAssetIds(baselineDir, baselineSig, sampleIds.AvatarId),
                stagedCache.LoadAvatarMeshAssetIds(stagedDir, stagedSig, sampleIds.AvatarId));
            Assert.Equal(
                baselineCache.LoadMeshAvatarAssetIds(baselineDir, baselineSig, sampleIds.MeshId),
                stagedCache.LoadMeshAvatarAssetIds(stagedDir, stagedSig, sampleIds.MeshId));
            Assert.Equal(
                baselineCache.LoadAnimationClipAvatarAssetIds(baselineDir, baselineSig, sampleIds.ClipId),
                stagedCache.LoadAnimationClipAvatarAssetIds(stagedDir, stagedSig, sampleIds.ClipId));
            Assert.Equal(
                baselineCache.LoadAnimationClipMeshAssetIds(baselineDir, baselineSig, sampleIds.ClipId),
                stagedCache.LoadAnimationClipMeshAssetIds(stagedDir, stagedSig, sampleIds.ClipId));

            var baselineGroups = baselineCache.LoadModelGroupsForAvatarAssetId(baselineDir, baselineSig, sampleIds.AvatarId);
            var stagedGroups = stagedCache.LoadModelGroupsForAvatarAssetId(stagedDir, stagedSig, sampleIds.AvatarId);
            Assert.Equal(baselineGroups.Select(g => (g.GroupId, g.Confidence, g.GroupName)), stagedGroups.Select(g => (g.GroupId, g.Confidence, g.GroupName)));

            var baselineMeshes = baselineCache.LoadModelGroupMeshes(baselineDir, baselineSig, sampleIds.GroupId);
            var stagedMeshes = stagedCache.LoadModelGroupMeshes(stagedDir, stagedSig, sampleIds.GroupId);
            Assert.Equal(baselineMeshes.Count, stagedMeshes.Count);
            for (var i = 0; i < baselineMeshes.Count; i++)
            {
                Assert.Equal(baselineMeshes[i].MeshAssetId, stagedMeshes[i].MeshAssetId);
                Assert.Equal(baselineMeshes[i].RendererAssetId, stagedMeshes[i].RendererAssetId);
                Assert.Equal(baselineMeshes[i].Confidence, stagedMeshes[i].Confidence);
                Assert.Equal(baselineMeshes[i].TransformMatrix, stagedMeshes[i].TransformMatrix);
            }
        }

        private static void VerifyCoreSemanticQueryPlansUseZeroPhysicalScans(
            SqliteConnection conn,
            long projectId,
            SampleGraphIds sampleIds)
        {
            AssertQueryPlanUsesSearchWithoutPhysicalScans(
                conn,
                "LoadMeshRendererAssetIds",
                @"
                    SELECT mr.RendererAssetUniqueID
                    FROM MeshRenderers mr
                    WHERE mr.ProjectId = @projectId
                      AND mr.MeshAssetUniqueID = @meshAssetId
                      AND mr.RendererAssetUniqueID <> ''
                    GROUP BY mr.RendererAssetUniqueID",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                });

            AssertQueryPlanUsesSearchWithoutPhysicalScans(
                conn,
                "LoadAvatarMeshAssetIds",
                @"
                    SELECT ae.TargetAssetUniqueID
                    FROM AssetEdges ae
                    INNER JOIN Assets target
                      ON target.ProjectId = ae.ProjectId
                     AND target.UniqueID = ae.TargetAssetUniqueID
                    WHERE ae.ProjectId = @projectId
                      AND ae.SourceAssetUniqueID = @avatarAssetId
                      AND ae.EdgeKind = 'Mesh'
                      AND ae.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                      AND ae.TargetAssetUniqueID <> ''
                      AND target.Type = @meshType
                    GROUP BY ae.TargetAssetUniqueID",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@avatarAssetId", sampleIds.AvatarId);
                    cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                });

            AssertQueryPlanUsesSearchWithoutPhysicalScans(
                conn,
                "LoadMeshAvatarAssetIds",
                @"
                    SELECT ae.SourceAssetUniqueID
                    FROM AssetEdges ae
                    INNER JOIN Assets source
                      ON source.ProjectId = ae.ProjectId
                     AND source.UniqueID = ae.SourceAssetUniqueID
                    WHERE ae.ProjectId = @projectId
                      AND ae.TargetAssetUniqueID = @meshAssetId
                      AND ae.EdgeKind = 'Mesh'
                      AND ae.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                      AND ae.SourceAssetUniqueID <> ''
                      AND source.Type = @avatarType
                    GROUP BY ae.SourceAssetUniqueID",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                    cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                });

            AssertQueryPlanUsesSearchWithoutPhysicalScans(
                conn,
                "LoadModelGroupsForAvatarAssetId",
                @"
                    SELECT GroupAssetUniqueID, GroupName
                    FROM ModelGroups
                    WHERE ProjectId = @projectId
                      AND AvatarAssetUniqueID = @avatarAssetId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@avatarAssetId", sampleIds.AvatarId);
                });

            AssertQueryPlanUsesSearchWithoutPhysicalScans(
                conn,
                "LoadModelGroupMeshes",
                @"
                    SELECT mgm.GroupAssetUniqueID, mgm.MeshAssetUniqueID, COALESCE(mesh.Name, '')
                    FROM ModelGroupMeshes mgm
                    LEFT JOIN Assets mesh
                      ON mesh.ProjectId = mgm.ProjectId
                     AND mesh.UniqueID = mgm.MeshAssetUniqueID
                    WHERE mgm.ProjectId = @projectId
                      AND mgm.GroupAssetUniqueID = @groupAssetId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@groupAssetId", sampleIds.GroupId);
                });
        }

        private static void AssertQueryPlanUsesSearchWithoutPhysicalScans(
            SqliteConnection conn,
            string queryName,
            string sql,
            Action<SqliteCommand> bindParameters)
        {
            var planLines = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                bindParameters(cmd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    planLines.Add(reader.GetString(3));
                }
            }

            Assert.NotEmpty(planLines);
            var formattedPlan = string.Join(Environment.NewLine, planLines);
            Assert.True(
                planLines.Any(line => line.Contains("SEARCH ", StringComparison.OrdinalIgnoreCase)),
                $"Expected query '{queryName}' to use SEARCH, but plan was:{Environment.NewLine}{formattedPlan}");
            Assert.False(
                planLines.Any(line => line.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase)),
                $"Query '{queryName}' performed an unexpected SCAN:{Environment.NewLine}{formattedPlan}");
        }

        private static HashSet<string> GetStageTableNames(SqliteConnection conn)
        {
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Stage_%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
            return tables;
        }

        private static long ExecuteScalarInt64(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
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

        private static (
            List<AssetHandle> Handles,
            List<SemanticAssetRelations> Batches,
            SemanticAssetRelations MergedBaseline,
            SampleGraphIds SampleIds)
            BuildMultiBatchSyntheticGraphWithOverlaps(string sourcePath, int groupCount, int batchCount)
        {
            var handles = new List<AssetHandle>(groupCount * 7);
            var batches = Enumerable.Range(0, batchCount).Select(_ => new SemanticAssetRelations()).ToList();
            var merged = new SemanticAssetRelations();

            SampleGraphIds? sampleIds = null;
            for (var i = 0; i < groupCount; i++)
            {
                var batchIndex = i % batchCount;
                var batch = batches[batchIndex];
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

                batch.SourceFiles.Add(new SemanticSourceFileEntry("level0", sourcePath, "2022.3.10f1", (i + 1) * 7));

                // Initial unresolved or lower-confidence rows in an earlier batch, followed by updated rows in a later batch
                // to verify conflict deduplication and last-write-wins ordering across batches.
                var matrix = new float[]
                {
                    1f, 0f, 0f, i,
                    0f, 1f, 0f, i * 2f,
                    0f, 0f, 1f, i * 3f,
                    0f, 0f, 0f, 1f
                };

                batch.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "Mesh", "AvatarMesh", 0, string.Empty, 0, basePathId + 5, 0, basePathId + 1, isResolved: false));
                batch.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", $"Rig_Draft_{i:D3}", rootGoId, $"Root_{i:D3}", string.Empty, avatarId, string.Empty, "level0", 40, "DraftMatch"));
                batch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_Draft_{i:D3}", 0, null, 40, "DraftRenderer"));
                batch.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_Draft_{i:D3}", "Draft body"));
                batch.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshId, materialId, rendererId, "Container", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 10));
                batch.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId, string.Empty, "_BaseMap", 0, textureId, 0, basePathId + 4, isResolved: false, isMainTexture: false));

                var laterBatch = batches[(batchIndex + 1) % batchCount];
                laterBatch.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "Mesh", "AvatarMesh", 0, meshId, 0, basePathId + 5, 0, basePathId + 1, isResolved: true));
                laterBatch.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    clipId, "Avatar", "AnimatorAvatar", 0, avatarId, 0, basePathId + 6, 0, basePathId + 5, isResolved: true));
                laterBatch.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "AnimationClip", "AnimatorClip", 0, clipId, 0, basePathId + 5, 0, basePathId + 6, isResolved: true));

                laterBatch.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", $"Rig_{i:D3}", rootGoId, $"Root_{i:D3}", string.Empty, avatarId, string.Empty, "level0", 95, "ExactMatch"));
                laterBatch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_{i:D3}", 0, matrix, 95, "DirectRenderer"));
                laterBatch.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_{i:D3}", "Primary body"));
                laterBatch.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshId, materialId, rendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 100));
                laterBatch.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId, previewMaterialId, "_BaseMap", 0, textureId, 0, basePathId + 4, isResolved: true, isMainTexture: true));

                if (i == groupCount / 2)
                {
                    sampleIds = new SampleGraphIds(meshId, materialId, previewMaterialId, textureId, avatarId, clipId, rendererId, groupId);
                }
            }

            foreach (var batch in batches)
            {
                merged.Merge(batch);
            }

            return (handles, batches, merged, sampleIds!);
        }

        private static List<SemanticAssetRelations> BuildMutatedV2Batches(
            string sourcePath,
            SampleGraphIds sampleIds,
            int batchCount)
        {
            var batches = new List<SemanticAssetRelations>(batchCount);
            for (var b = 0; b < batchCount; b++)
            {
                var batch = new SemanticAssetRelations();
                var poisonMaterialId = $"poison_material_batch_{b}";
                var poisonTextureId = $"poison_texture_batch_{b}";
                batch.SourceFiles.Add(new SemanticSourceFileEntry("level0", sourcePath, "2099.9.9f9", 999999 + b));
                batch.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    sampleIds.AvatarId, "Mesh", "AvatarMesh", b, $"poison_mesh_{b}", 0, 1, 0, 99900 + b, isResolved: true));
                batch.ModelGroups.Add(new SemanticModelGroupRelation(
                    sampleIds.GroupId, "Animator", $"PoisonGroup_{b}", "poison_root", "PoisonRoot", string.Empty, sampleIds.AvatarId, string.Empty, "level0", 1, "Poison"));
                batch.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    sampleIds.MeshId, sampleIds.RendererId, "SkinnedMeshRenderer", "poison_go", $"PoisonGo_{b}", "Poisoned"));
                batch.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    sampleIds.MeshId, poisonMaterialId, sampleIds.RendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 9999));
                batch.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    sampleIds.MaterialId, sampleIds.PreviewMaterialId, "_BaseMap", 0, poisonTextureId, 0, 88800 + b, isResolved: true, isMainTexture: true));
                batches.Add(batch);
            }

            return batches;
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, ProjectScanResult ScanResult, string SourcePath)
            CreateSampleProject(string tempRoot, int assetGroupCount, string? existingProjectDir = null)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = existingProjectDir ?? Path.Combine(tempRoot, "SampleProject");
            Directory.CreateDirectory(projectDir);
            var sourcePath = Path.Combine(projectDir, "level0");
            if (!File.Exists(sourcePath))
            {
                File.WriteAllBytes(sourcePath, new byte[Math.Max(16, assetGroupCount * 8)]);
            }

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePath);
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
