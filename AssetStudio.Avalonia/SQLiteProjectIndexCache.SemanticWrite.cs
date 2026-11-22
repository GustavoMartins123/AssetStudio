using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        private const int SemanticRelationCommitBatchSize = 10_000;

        internal SemanticStagingSession BeginSemanticStagingSession(string folderPath, string projectSignature)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("Folder path must not be empty.", nameof(folderPath));
            }

            if (string.IsNullOrWhiteSpace(projectSignature))
            {
                throw new ArgumentException("Project signature must not be empty.", nameof(projectSignature));
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            long projectId = 0;

            lock (WriteGate)
            {
                using var conn = CreateConnectionByDbPath(dbPath);
                using var transaction = conn.BeginTransaction();
                var foundProjectId = FindProjectId(conn, transaction, folderPath, projectSignature);
                if (foundProjectId.HasValue)
                {
                    projectId = foundProjectId.Value;
                    CacheProjectId(dbPath, folderPath, projectSignature, projectId);
                }
                else
                {
                    EvictProjectIdCacheEntry(dbPath, folderPath, projectSignature);
                }

                CreateSemanticStagingTables(conn, transaction);
                transaction.Commit();
            }

            return new SemanticStagingSession(this, folderPath, projectSignature, dbPath, projectId);
        }

        internal sealed class SemanticStagingSession : IDisposable
        {
            private readonly SQLiteProjectIndexCache _owner;
            private readonly string _folderPath;
            private readonly string _projectSignature;
            private readonly string _dbPath;
            private long _projectId;
            private bool _isCommitted;
            private bool _isAborted;
            private bool _isDisposed;

            internal SemanticStagingSession(
                SQLiteProjectIndexCache owner,
                string folderPath,
                string projectSignature,
                string dbPath,
                long projectId)
            {
                _owner = owner;
                _folderPath = folderPath;
                _projectSignature = projectSignature;
                _dbPath = dbPath;
                _projectId = projectId;
            }

            public string FolderPath => _folderPath;
            public string ProjectSignature => _projectSignature;
            public int FlushCount { get; private set; }
            public int StagedSourceFilesCount { get; private set; }
            public int StagedAssetEdgesCount { get; private set; }
            public int StagedModelGroupsCount { get; private set; }
            public int StagedModelGroupMeshesCount { get; private set; }
            public int StagedMeshRenderersCount { get; private set; }
            public int StagedMeshMaterialsCount { get; private set; }
            public int StagedMaterialTexturesCount { get; private set; }
            public int StagedMaterialCachesCount { get; private set; }
            public int StagedCompositeAssetsCount { get; private set; }
            public int StagedCompositeAssetPartsCount { get; private set; }
            public int StagedAnimationClipTargetsCount { get; private set; }
            public int StagedRigBoneHashesCount { get; private set; }
            public int StagedClipBoneHashesCount { get; private set; }

            public int TotalStagedRows =>
                StagedSourceFilesCount
                + StagedAssetEdgesCount
                + StagedModelGroupsCount
                + StagedModelGroupMeshesCount
                + StagedMeshRenderersCount
                + StagedMeshMaterialsCount
                + StagedMaterialTexturesCount
                + StagedMaterialCachesCount
                + StagedCompositeAssetsCount
                + StagedCompositeAssetPartsCount
                + StagedAnimationClipTargetsCount
                + StagedRigBoneHashesCount
                + StagedClipBoneHashesCount;

            public bool HasStagedRelations =>
                StagedAssetEdgesCount > 0
                || StagedModelGroupsCount > 0
                || StagedModelGroupMeshesCount > 0
                || StagedMeshRenderersCount > 0
                || StagedMeshMaterialsCount > 0
                || StagedMaterialTexturesCount > 0
                || StagedMaterialCachesCount > 0
                || StagedCompositeAssetsCount > 0
                || StagedCompositeAssetPartsCount > 0
                || StagedAnimationClipTargetsCount > 0
                || StagedRigBoneHashesCount > 0
                || StagedClipBoneHashesCount > 0;

            public bool HasStagedMaterialRelations =>
                StagedAssetEdgesCount > 0
                || StagedMeshMaterialsCount > 0
                || StagedMaterialTexturesCount > 0
                || StagedMaterialCachesCount > 0
                || StagedCompositeAssetsCount > 0
                || StagedCompositeAssetPartsCount > 0
                || StagedAnimationClipTargetsCount > 0
                || StagedRigBoneHashesCount > 0
                || StagedClipBoneHashesCount > 0;

            public bool IsCommitted => _isCommitted;
            public bool IsAborted => _isAborted;

            public void FlushBatch(SemanticAssetRelations? batch, bool clearBatch = true)
            {
                if (batch == null)
                {
                    return;
                }

                FlushBatch(
                    batch.AssetEdges,
                    batch.MeshRenderers,
                    batch.MeshMaterials,
                    batch.MaterialTextures,
                    batch.ModelGroups,
                    batch.ModelGroupMeshes,
                    batch.SourceFiles,
                    clearLists: clearBatch,
                    animationClipTargets: batch.AnimationClipTargets,
                    rigBoneHashes: batch.RigBoneHashes,
                    clipBoneHashes: batch.ClipBoneHashes,
                    materialCaches: batch.MaterialCaches,
                    compositeAssets: batch.CompositeAssets,
                    compositeAssetParts: batch.CompositeAssetParts);
            }

            public void FlushBatch(
                IReadOnlyList<SemanticAssetEdgeRelation>? edges,
                IReadOnlyList<SemanticMeshRendererRelation>? meshRenderers,
                IReadOnlyList<SemanticMeshMaterialRelation>? meshMaterials,
                IReadOnlyList<SemanticMaterialTextureRelation>? materialTextures,
                IReadOnlyList<SemanticModelGroupRelation>? modelGroups,
                IReadOnlyList<SemanticModelGroupMeshRelation>? modelGroupMeshes,
                IReadOnlyCollection<SemanticSourceFileEntry>? sourceFiles = null,
                bool clearLists = true,
                IReadOnlyList<SemanticAnimationClipTargetRelation>? animationClipTargets = null,
                IReadOnlyList<SemanticRigBoneHashEntry>? rigBoneHashes = null,
                IReadOnlyList<SemanticClipBoneHashEntry>? clipBoneHashes = null,
                IReadOnlyList<SemanticMaterialCacheEntry>? materialCaches = null,
                IReadOnlyList<SemanticCompositeAssetRelation>? compositeAssets = null,
                IReadOnlyList<SemanticCompositeAssetPartRelation>? compositeAssetParts = null)
            {
                EnsureActive();

                var sourceFileCount = sourceFiles?.Count ?? 0;
                var edgeCount = edges?.Count ?? 0;
                var groupCount = modelGroups?.Count ?? 0;
                var groupMeshCount = modelGroupMeshes?.Count ?? 0;
                var rendererCount = meshRenderers?.Count ?? 0;
                var materialCount = meshMaterials?.Count ?? 0;
                var textureCount = materialTextures?.Count ?? 0;
                var materialCacheCount = materialCaches?.Count ?? 0;
                var compositeCount = compositeAssets?.Count ?? 0;
                var compositePartCount = compositeAssetParts?.Count ?? 0;
                var animTargetCount = animationClipTargets?.Count ?? 0;
                var rigHashCount = rigBoneHashes?.Count ?? 0;
                var clipHashCount = clipBoneHashes?.Count ?? 0;
                var batchTotal = sourceFileCount + edgeCount + groupCount + groupMeshCount + rendererCount + materialCount + textureCount + materialCacheCount + compositeCount + compositePartCount + animTargetCount + rigHashCount + clipHashCount;
                if (batchTotal == 0)
                {
                    return;
                }

                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(_dbPath);
                    using var transaction = conn.BeginTransaction();
                    if (sourceFiles != null && sourceFileCount > 0)
                    {
                        InsertStagedSourceFiles(conn, transaction, _projectId, sourceFiles);
                    }
                    if (edges != null && edgeCount > 0)
                    {
                        InsertStagedAssetEdges(conn, transaction, _projectId, edges);
                    }
                    if (modelGroups != null && groupCount > 0)
                    {
                        InsertStagedModelGroups(conn, transaction, _projectId, modelGroups);
                    }
                    if (modelGroupMeshes != null && groupMeshCount > 0)
                    {
                        InsertStagedModelGroupMeshes(conn, transaction, _projectId, modelGroupMeshes);
                    }
                    if (meshRenderers != null && rendererCount > 0)
                    {
                        InsertStagedMeshRenderers(conn, transaction, _projectId, meshRenderers);
                    }
                    if (meshMaterials != null && materialCount > 0)
                    {
                        InsertStagedMeshMaterials(conn, transaction, _projectId, meshMaterials);
                    }
                    if (materialTextures != null && textureCount > 0)
                    {
                        InsertStagedMaterialTextures(conn, transaction, _projectId, materialTextures);
                    }
                    if (materialCaches != null && materialCacheCount > 0)
                    {
                        InsertStagedMaterialCaches(conn, transaction, _projectId, materialCaches);
                    }
                    if (compositeAssets != null && compositeCount > 0)
                    {
                        InsertStagedCompositeAssets(conn, transaction, _projectId, compositeAssets);
                    }
                    if (compositeAssetParts != null && compositePartCount > 0)
                    {
                        InsertStagedCompositeAssetParts(conn, transaction, _projectId, compositeAssetParts);
                    }
                    if (animationClipTargets != null && animTargetCount > 0)
                    {
                        InsertStagedAnimationClipTargets(conn, transaction, _projectId, animationClipTargets);
                    }
                    if (rigBoneHashes != null && rigHashCount > 0)
                    {
                        InsertStagedRigBoneHashes(conn, transaction, _projectId, rigBoneHashes);
                    }
                    if (clipBoneHashes != null && clipHashCount > 0)
                    {
                        InsertStagedClipBoneHashes(conn, transaction, _projectId, clipBoneHashes);
                    }
                    transaction.Commit();
                }

                FlushCount++;
                StagedSourceFilesCount += sourceFileCount;
                StagedAssetEdgesCount += edgeCount;
                StagedModelGroupsCount += groupCount;
                StagedModelGroupMeshesCount += groupMeshCount;
                StagedMeshRenderersCount += rendererCount;
                StagedMeshMaterialsCount += materialCount;
                StagedMaterialTexturesCount += textureCount;
                StagedMaterialCachesCount += materialCacheCount;
                StagedCompositeAssetsCount += compositeCount;
                StagedCompositeAssetPartsCount += compositePartCount;
                StagedAnimationClipTargetsCount += animTargetCount;
                StagedRigBoneHashesCount += rigHashCount;
                StagedClipBoneHashesCount += clipHashCount;

                if (clearLists)
                {
                    ClearIfMutableCollection(sourceFiles);
                    ClearIfMutableList(edges);
                    ClearIfMutableList(modelGroups);
                    ClearIfMutableList(modelGroupMeshes);
                    ClearIfMutableList(meshRenderers);
                    ClearIfMutableList(meshMaterials);
                    ClearIfMutableList(materialTextures);
                    ClearIfMutableList(materialCaches);
                    ClearIfMutableList(compositeAssets);
                    ClearIfMutableList(compositeAssetParts);
                    ClearIfMutableList(animationClipTargets);
                    ClearIfMutableList(rigBoneHashes);
                    ClearIfMutableList(clipBoneHashes);
                }
            }

            public Task<bool> CommitAsync(Action<int, int, string>? progress = null, CancellationToken cancellationToken = default)
            {
                EnsureActive();
                return Task.Run(() => Commit(progress), cancellationToken);
            }

            public bool Commit(Action<int, int, string>? progress = null)
            {
                EnsureActive();

                if (!HasStagedRelations && StagedSourceFilesCount == 0)
                {
                    Abort();
                    return false;
                }

                try
                {
                    // 11 clear steps + 10 staged promotion steps + 1 pre-anim index build + 1 derived cache backfill
                    // + 4 animation promotion sub-steps + 1 post-anim index build + 1 staging cleanup = 29 steps
                    const int totalWork = 29;
                    var reporter = new ThrottledProgressReporter(totalWork, progress);
                    reporter.Report("Waiting for SQLite writer", force: true);

                    lock (WriteGate)
                    {
                        reporter.Report("Opening SQLite connection", force: true);
                        using var conn = CreateConnectionByDbPath(_dbPath);

                        reporter.Report("Resolving project row", force: true);
                        var foundProjectId = FindProjectId(conn, null, _folderPath, _projectSignature);
                        if (foundProjectId == null)
                        {
                            _owner.EvictProjectIdCacheEntry(_dbPath, _folderPath, _projectSignature);
                            using (var cleanupTx = conn.BeginTransaction())
                            {
                                DropSemanticStagingTables(conn, cleanupTx);
                                cleanupTx.Commit();
                            }
                            _isAborted = true;
                            return false;
                        }

                        var projectId = foundProjectId.Value;
                        _projectId = projectId;
                        _owner.CacheProjectId(_dbPath, _folderPath, _projectSignature, projectId);

                        SetSynchronousMode(conn, "OFF");
                        SetCacheSizeKiB(conn, -262144);
                        try
                        {
                            using var transaction = conn.BeginTransaction();
                            SaveProjectIndexingState(
                                conn,
                                transaction,
                                projectId,
                                "saving_connections",
                                reporter.ProcessedWork,
                                reporter.TotalWork,
                                "Preparing staged semantic relation commit",
                                completed: false);

                            DropSemanticBulkSecondaryIndexes(conn, transaction);
                            ClearSemanticRelationTablesForProject(conn, transaction, projectId, reporter.Advance);

                            PromoteStagedSemanticRelationsToLive(conn, transaction, projectId, reporter.Advance);

                            reporter.Report("Rebuilding remaining semantic relation indexes", force: true);
                            CreateSemanticBulkSecondaryIndexes(conn, transaction);
                            reporter.Advance("Rebuilt semantic relation indexes");

                            DropSemanticStagingTables(conn, transaction);
                            reporter.Advance("Cleaned semantic staging tables");

                            reporter.Report("Committing semantic relations", force: true);
                            reporter.MarkCompleted("Semantic relations saved");
                            SaveProjectIndexingState(
                                conn,
                                transaction,
                                projectId,
                                "connections_completed",
                                reporter.ProcessedWork,
                                reporter.TotalWork,
                                reporter.CurrentStage,
                                completed: true);
                            transaction.Commit();
                            _isCommitted = true;
                        }
                        finally
                        {
                            SetCacheSizeKiB(conn, ConnectionCacheSizeKiB);
                            SetSynchronousMode(conn, "NORMAL");
                        }

                        _owner.NotifyDatabaseMutated(_dbPath);
                        reporter.Report("Analyzing SQLite statistics", force: true);
                        TryAnalyzeDatabase(conn, "staged semantic relations");
                        _owner.InvalidateReadConnectionPool(_dbPath);
                        reporter.Report("Checkpointing SQLite WAL", force: true);
                        TryCheckpointWal(conn, "staged semantic relations");
                        reporter.Report("Semantic relations saved", force: true);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to commit staged semantic asset relations: {ex.Message}");
                    return false;
                }
            }

            public void Abort()
            {
                if (_isCommitted || _isAborted)
                {
                    return;
                }

                _isAborted = true;
                try
                {
                    lock (WriteGate)
                    {
                        if (!File.Exists(_dbPath))
                        {
                            return;
                        }

                        using var conn = CreateConnectionByDbPath(_dbPath);
                        using var transaction = conn.BeginTransaction();
                        DropSemanticStagingTables(conn, transaction);
                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to abort semantic staging session: {ex.Message}");
                }
            }

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                if (!_isCommitted && !_isAborted)
                {
                    Abort();
                }
            }

            private void EnsureActive()
            {
                if (_isCommitted)
                {
                    throw new InvalidOperationException("SemanticStagingSession has already been committed.");
                }

                if (_isAborted || _isDisposed)
                {
                    throw new ObjectDisposedException(nameof(SemanticStagingSession));
                }
            }

            private static void ClearIfMutableList<T>(IReadOnlyList<T>? list)
            {
                if (list is List<T> concreteList)
                {
                    concreteList.Clear();
                }
                else if (list is IList untypedList && !untypedList.IsReadOnly && !untypedList.IsFixedSize)
                {
                    untypedList.Clear();
                }
            }

            private static void ClearIfMutableCollection<T>(IReadOnlyCollection<T>? collection)
            {
                if (collection is List<T> concreteList)
                {
                    concreteList.Clear();
                }
                else if (collection is ICollection<T> mutableCollection && !mutableCollection.IsReadOnly && collection is not T[])
                {
                    mutableCollection.Clear();
                }
            }
        }

        internal bool SaveSemanticRelations(
            string folderPath,
            string signature,
            SemanticAssetRelations relations,
            bool replaceExisting = false,
            Action<int, int, string>? progress = null)
        {
            if (relations == null || (!relations.HasRelations && relations.SourceFiles.Count == 0))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                var clearWork = replaceExisting ? 11 : 0;
                var totalWork = Math.Max(1,
                    relations.SourceFiles.Count
                    + relations.AssetEdges.Count
                    + relations.ModelGroups.Count
                    + relations.ModelGroupMeshes.Count
                    + relations.MeshRenderers.Count
                    + relations.MeshMaterials.Count
                    + relations.MaterialTextures.Count
                    + relations.MaterialCaches.Count
                    + relations.CompositeAssets.Count
                    + relations.CompositeAssetParts.Count
                    + relations.AnimationClipTargets.Count
                    + relations.RigBoneHashes.Count
                    + relations.ClipBoneHashes.Count
                    + clearWork
                    + 8);
                var reporter = new ThrottledProgressReporter(totalWork, progress);

                reporter.Report("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    reporter.Report("Opening SQLite connection", force: true);
                    using var conn = CreateConnectionByDbPath(dbPath);

                    reporter.Report("Resolving project row", force: true);
                    var foundProjectId = FindProjectId(conn, null, folderPath, signature);
                    if (foundProjectId == null)
                    {
                        EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                        return false;
                    }

                    var projectId = foundProjectId.Value;
                    CacheProjectId(dbPath, folderPath, signature, projectId);

                    if (replaceExisting)
                    {
                        SaveSemanticRelationsBulkReplace(conn, projectId, relations, reporter);
                        reporter.Report("Analyzing SQLite statistics", force: true);
                        TryAnalyzeDatabase(conn, "semantic relations");
                    }
                    else
                    {
                        SaveSemanticRelationsIncremental(conn, projectId, relations, reporter);
                    }

                    NotifyDatabaseMutated(dbPath);
                    reporter.Report("Checkpointing SQLite WAL", force: true);
                    TryCheckpointWal(conn, "semantic relations");
                    reporter.Report("Semantic relations saved", force: true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save semantic asset relations: {ex.Message}");
                return false;
            }
        }

        private static void SaveSemanticRelationsBulkReplace(
            SqliteConnection conn,
            long projectId,
            SemanticAssetRelations relations,
            ThrottledProgressReporter reporter)
        {
            SetSynchronousMode(conn, "OFF");
            try
            {
                using var transaction = conn.BeginTransaction();
                SaveProjectIndexingState(
                    conn,
                    transaction,
                    projectId,
                    "saving_connections",
                    reporter.ProcessedWork,
                    reporter.TotalWork,
                    "Preparing semantic relation save",
                    completed: false);

                ClearSemanticRelationTablesForProject(conn, transaction, projectId, reporter.Advance);
                DropSemanticBulkSecondaryIndexes(conn, transaction);

                InsertSemanticSourceFiles(conn, transaction, projectId, relations.SourceFiles, reporter.Advance);
                var assetSourceFileIds = LoadAssetSourceFileIdMap(conn, transaction, projectId);

                InsertAssetEdges(conn, transaction, projectId, relations.AssetEdges, 0, relations.AssetEdges.Count, assetSourceFileIds, reporter.Advance);
                InsertModelGroups(conn, transaction, projectId, relations.ModelGroups, 0, relations.ModelGroups.Count, reporter.Advance);
                InsertModelGroupMeshes(conn, transaction, projectId, relations.ModelGroupMeshes, 0, relations.ModelGroupMeshes.Count, reporter.Advance);
                InsertMeshRenderers(conn, transaction, projectId, relations.MeshRenderers, 0, relations.MeshRenderers.Count, reporter.Advance);
                InsertMeshMaterials(conn, transaction, projectId, relations.MeshMaterials, 0, relations.MeshMaterials.Count, reporter.Advance);
                InsertMaterialTextures(conn, transaction, projectId, relations.MaterialTextures, 0, relations.MaterialTextures.Count, reporter.Advance);
                InsertMaterialCaches(conn, transaction, projectId, relations.MaterialCaches, 0, relations.MaterialCaches.Count, reporter.Advance);
                InsertCompositeAssets(conn, transaction, projectId, relations.CompositeAssets, 0, relations.CompositeAssets.Count, reporter.Advance);
                InsertCompositeAssetParts(conn, transaction, projectId, relations.CompositeAssetParts, 0, relations.CompositeAssetParts.Count, reporter.Advance);

                reporter.Report("Building pre-animation relation indexes", force: true);
                CreateSemanticPreAnimationIndexes(conn, transaction);
                BackfillV15DerivedCachesForProject(conn, transaction, projectId);
                StageAndPromoteAnimationRelations(conn, transaction, projectId, relations, reporter.Advance);

                reporter.Report("Rebuilding semantic relation indexes", force: true);
                CreateSemanticBulkSecondaryIndexes(conn, transaction);

                reporter.Report("Committing semantic relations", force: true);
                reporter.MarkCompleted("Semantic relations saved");
                SaveProjectIndexingState(
                    conn,
                    transaction,
                    projectId,
                    "connections_completed",
                    reporter.ProcessedWork,
                    reporter.TotalWork,
                    reporter.CurrentStage,
                    completed: true);
                transaction.Commit();
            }
            finally
            {
                SetSynchronousMode(conn, "NORMAL");
            }
        }

        private static void SaveSemanticRelationsIncremental(
            SqliteConnection conn,
            long projectId,
            SemanticAssetRelations relations,
            ThrottledProgressReporter reporter)
        {
            using (var transaction = conn.BeginTransaction())
            {
                SaveProjectIndexingState(
                    conn,
                    transaction,
                    projectId,
                    "saving_connections",
                    reporter.ProcessedWork,
                    reporter.TotalWork,
                    "Preparing semantic relation save",
                    completed: false);
                InsertSemanticSourceFiles(conn, transaction, projectId, relations.SourceFiles, reporter.Advance);
                SaveProjectIndexingState(
                    conn,
                    transaction,
                    projectId,
                    "saving_connections",
                    reporter.ProcessedWork,
                    reporter.TotalWork,
                    string.IsNullOrWhiteSpace(reporter.CurrentStage) ? "Saving semantic source files" : reporter.CurrentStage,
                    completed: false);
                transaction.Commit();
            }

            Dictionary<string, long> assetSourceFileIds;
            using (var transaction = conn.BeginTransaction())
            {
                assetSourceFileIds = LoadAssetSourceFileIdMap(conn, transaction, projectId);
                transaction.Commit();
            }

            ExecuteInCommitChunks(
                conn,
                relations.AssetEdges.Count,
                (c, tx, offset, count) => InsertAssetEdges(c, tx, projectId, relations.AssetEdges, offset, count, assetSourceFileIds, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.ModelGroups.Count,
                (c, tx, offset, count) => InsertModelGroups(c, tx, projectId, relations.ModelGroups, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.ModelGroupMeshes.Count,
                (c, tx, offset, count) => InsertModelGroupMeshes(c, tx, projectId, relations.ModelGroupMeshes, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.MeshRenderers.Count,
                (c, tx, offset, count) => InsertMeshRenderers(c, tx, projectId, relations.MeshRenderers, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.MeshMaterials.Count,
                (c, tx, offset, count) => InsertMeshMaterials(c, tx, projectId, relations.MeshMaterials, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.MaterialTextures.Count,
                (c, tx, offset, count) => InsertMaterialTextures(c, tx, projectId, relations.MaterialTextures, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.MaterialCaches.Count,
                (c, tx, offset, count) => InsertMaterialCaches(c, tx, projectId, relations.MaterialCaches, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.CompositeAssets.Count,
                (c, tx, offset, count) => InsertCompositeAssets(c, tx, projectId, relations.CompositeAssets, offset, count, reporter.Advance),
                SaveProgressCheckpoint);
            ExecuteInCommitChunks(
                conn,
                relations.CompositeAssetParts.Count,
                (c, tx, offset, count) => InsertCompositeAssetParts(c, tx, projectId, relations.CompositeAssetParts, offset, count, reporter.Advance),
                SaveProgressCheckpoint);

            using (var transaction = conn.BeginTransaction())
            {
                BackfillV15DerivedCachesForProject(conn, transaction, projectId);
                StageAndPromoteAnimationRelations(conn, transaction, projectId, relations, reporter.Advance);
                SaveProgressCheckpoint(conn, transaction);
                transaction.Commit();
            }

            reporter.Report("Committing semantic relations", force: true);
            using (var transaction = conn.BeginTransaction())
            {
                reporter.MarkCompleted("Semantic relations saved");
                SaveProjectIndexingState(
                    conn,
                    transaction,
                    projectId,
                    "connections_completed",
                    reporter.ProcessedWork,
                    reporter.TotalWork,
                    reporter.CurrentStage,
                    completed: true);
                transaction.Commit();
            }

            void SaveProgressCheckpoint(SqliteConnection checkpointConn, SqliteTransaction checkpointTransaction)
            {
                SaveProjectIndexingState(
                    checkpointConn,
                    checkpointTransaction,
                    projectId,
                    "saving_connections",
                    reporter.ProcessedWork,
                    reporter.TotalWork,
                    string.IsNullOrWhiteSpace(reporter.CurrentStage) ? "Saving semantic relations" : reporter.CurrentStage,
                    completed: false);
            }
        }

        internal void ClearSemanticRelations(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    using var transaction = conn.BeginTransaction();
                    var projectId = FindProjectId(conn, transaction, folderPath, signature);
                    if (projectId == null)
                    {
                        EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                        return;
                    }

                    ClearSemanticRelationTablesForProject(conn, transaction, projectId.Value);
                    transaction.Commit();
                    NotifyDatabaseMutated(dbPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to clear semantic asset relations: {ex.Message}");
            }
        }

        private static void ClearSemanticRelationTablesForProject(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            Action<string>? advanceProgress = null)
        {
            var tables = new[]
            {
                "AnimationClipTargets",
                "CompositeAssetParts",
                "CompositeAssets",
                "MeshMaterialResolvedCache",
                "MaterialCache",
                "MaterialTextures",
                "MeshMaterials",
                "MeshRenderers",
                "ModelGroupMeshes",
                "ModelGroups",
                "AssetEdges"
            };

            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table} WHERE ProjectId = @projectId";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Clearing old {table} rows");
            }
        }

        private static void ExecuteInCommitChunks(
            SqliteConnection conn,
            int totalCount,
            Action<SqliteConnection, SqliteTransaction, int, int> insertChunk,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < totalCount; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                insertChunk(conn, transaction, offset, SemanticRelationCommitBatchSize);
                saveProgress(conn, transaction);
                transaction.Commit();
            }
        }

        private static Dictionary<string, long> LoadAssetSourceFileIdMap(SqliteConnection conn, SqliteTransaction transaction, long projectId)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT UniqueID, SourceFileId
                FROM Assets
                WHERE ProjectId = @projectId
                  AND SourceFileId IS NOT NULL";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var uniqueId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!string.IsNullOrEmpty(uniqueId))
                {
                    result[uniqueId] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                }
            }

            return result;
        }

        private static void InsertSemanticSourceFiles(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyCollection<SemanticSourceFileEntry> sourceFiles,
            Action<string>? advanceProgress = null)
        {
            if (sourceFiles.Count == 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                VALUES (@projectId, @serializedFileName, @originalPath, @unityVersion, @objectCount)
                ON CONFLICT(ProjectId, SerializedFileName, OriginalPath)
                DO UPDATE SET
                    UnityVersion = excluded.UnityVersion,
                    ObjectCount = excluded.ObjectCount,
                    LastSeen = CURRENT_TIMESTAMP";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pSerializedFileName = cmd.Parameters.Add("@serializedFileName", SqliteType.Text);
            var pOriginalPath = cmd.Parameters.Add("@originalPath", SqliteType.Text);
            var pUnityVersion = cmd.Parameters.Add("@unityVersion", SqliteType.Text);
            var pObjectCount = cmd.Parameters.Add("@objectCount", SqliteType.Integer);

            pProjectId.Value = projectId;
            var rowIndex = 0;
            foreach (var sourceFile in sourceFiles)
            {
                pSerializedFileName.Value = sourceFile.SerializedFileName;
                pOriginalPath.Value = sourceFile.OriginalPath;
                pUnityVersion.Value = sourceFile.UnityVersion;
                pObjectCount.Value = sourceFile.ObjectCount;
                cmd.ExecuteNonQuery();
                rowIndex++;
                advanceProgress?.Invoke($"Saving semantic source files: {rowIndex:N0}/{sourceFiles.Count:N0}");
            }
        }

        private static void InsertAssetEdges(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticAssetEdgeRelation> edges,
            int startIndex,
            int count,
            IReadOnlyDictionary<string, long> assetSourceFileIds,
            Action<string>? advanceProgress = null)
        {
            if (edges.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO AssetEdges (
                    ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                    SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                VALUES (
                    @projectId, @sourceAssetId, @edgeKind, @slotName, @slotIndex, @targetAssetId,
                    @sourceFileId, @sourcePathId, @targetFileId, @targetPathId, @isResolved)
                ON CONFLICT(ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID)
                DO UPDATE SET
                    TargetAssetUniqueID = excluded.TargetAssetUniqueID,
                    IsResolved = excluded.IsResolved";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pSourceAssetId = cmd.Parameters.Add("@sourceAssetId", SqliteType.Text);
            var pEdgeKind = cmd.Parameters.Add("@edgeKind", SqliteType.Text);
            var pSlotName = cmd.Parameters.Add("@slotName", SqliteType.Text);
            var pSlotIndex = cmd.Parameters.Add("@slotIndex", SqliteType.Integer);
            var pTargetAssetId = cmd.Parameters.Add("@targetAssetId", SqliteType.Text);
            var pSourceFileId = cmd.Parameters.Add("@sourceFileId", SqliteType.Integer);
            var pSourcePathId = cmd.Parameters.Add("@sourcePathId", SqliteType.Integer);
            var pTargetFileId = cmd.Parameters.Add("@targetFileId", SqliteType.Integer);
            var pTargetPathId = cmd.Parameters.Add("@targetPathId", SqliteType.Integer);
            var pIsResolved = cmd.Parameters.Add("@isResolved", SqliteType.Integer);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(edges.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var edge = edges[i];
                pSourceAssetId.Value = edge.SourceAssetId;
                pEdgeKind.Value = edge.EdgeKind;
                pSlotName.Value = edge.SlotName;
                pSlotIndex.Value = edge.SlotIndex;
                pTargetAssetId.Value = edge.TargetAssetId;
                pSourceFileId.Value = assetSourceFileIds.TryGetValue(edge.SourceAssetId, out var sourceFileId)
                    ? sourceFileId
                    : edge.SourceFileId;
                pSourcePathId.Value = edge.SourcePathId;
                pTargetFileId.Value = !string.IsNullOrEmpty(edge.TargetAssetId)
                    && assetSourceFileIds.TryGetValue(edge.TargetAssetId, out var targetFileId)
                        ? targetFileId
                        : edge.TargetFileId;
                pTargetPathId.Value = edge.TargetPathId;
                pIsResolved.Value = edge.IsResolved ? 1 : 0;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving asset edges: {i + 1:N0}/{edges.Count:N0}");
            }
        }

        private static void InsertModelGroups(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticModelGroupRelation> groups,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (groups.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO ModelGroups (
                    ProjectId, GroupAssetUniqueID, GroupKind, GroupName,
                    RootGameObjectAssetUniqueID, RootGameObjectName,
                    AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                    SourceFileName, Confidence, ConfidenceReason)
                VALUES (
                    @projectId, @groupId, @groupKind, @groupName,
                    @rootGameObjectId, @rootGameObjectName,
                    @animatorId, @avatarId, @controllerId,
                    @sourceFileName, @confidence, @confidenceReason)
                ON CONFLICT(ProjectId, GroupAssetUniqueID)
                DO UPDATE SET
                    GroupKind = excluded.GroupKind,
                    GroupName = excluded.GroupName,
                    RootGameObjectAssetUniqueID = excluded.RootGameObjectAssetUniqueID,
                    RootGameObjectName = excluded.RootGameObjectName,
                    AnimatorAssetUniqueID = excluded.AnimatorAssetUniqueID,
                    AvatarAssetUniqueID = excluded.AvatarAssetUniqueID,
                    ControllerAssetUniqueID = excluded.ControllerAssetUniqueID,
                    SourceFileName = excluded.SourceFileName,
                    Confidence = excluded.Confidence,
                    ConfidenceReason = excluded.ConfidenceReason";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pGroupKind = cmd.Parameters.Add("@groupKind", SqliteType.Text);
            var pGroupName = cmd.Parameters.Add("@groupName", SqliteType.Text);
            var pRootGameObjectId = cmd.Parameters.Add("@rootGameObjectId", SqliteType.Text);
            var pRootGameObjectName = cmd.Parameters.Add("@rootGameObjectName", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pControllerId = cmd.Parameters.Add("@controllerId", SqliteType.Text);
            var pSourceFileName = cmd.Parameters.Add("@sourceFileName", SqliteType.Text);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);
            var pConfidenceReason = cmd.Parameters.Add("@confidenceReason", SqliteType.Text);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(groups.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var group = groups[i];
                pGroupId.Value = group.GroupId;
                pGroupKind.Value = group.GroupKind;
                pGroupName.Value = group.GroupName;
                pRootGameObjectId.Value = group.RootGameObjectAssetId;
                pRootGameObjectName.Value = group.RootGameObjectName;
                pAnimatorId.Value = group.AnimatorAssetId;
                pAvatarId.Value = group.AvatarAssetId;
                pControllerId.Value = group.ControllerAssetId;
                pSourceFileName.Value = group.SourceFileName;
                pConfidence.Value = group.Confidence;
                pConfidenceReason.Value = group.ConfidenceReason;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving model groups: {i + 1:N0}/{groups.Count:N0}");
            }
        }

        private static void InsertModelGroupMeshes(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticModelGroupMeshRelation> meshes,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (meshes.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO ModelGroupMeshes (
                    ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID,
                    RendererType, GameObjectAssetUniqueID, GameObjectName,
                    SlotIndex, TransformMatrix, Confidence, ConfidenceReason)
                VALUES (
                    @projectId, @groupId, @meshId, @rendererId,
                    @rendererType, @gameObjectId, @gameObjectName,
                    @slotIndex, @transformMatrix, @confidence, @confidenceReason)
                ON CONFLICT(ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID)
                DO UPDATE SET
                    RendererType = excluded.RendererType,
                    GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                    GameObjectName = excluded.GameObjectName,
                    SlotIndex = excluded.SlotIndex,
                    TransformMatrix = excluded.TransformMatrix,
                    Confidence = excluded.Confidence,
                    ConfidenceReason = excluded.ConfidenceReason";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pMeshId = cmd.Parameters.Add("@meshId", SqliteType.Text);
            var pRendererId = cmd.Parameters.Add("@rendererId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pGameObjectId = cmd.Parameters.Add("@gameObjectId", SqliteType.Text);
            var pGameObjectName = cmd.Parameters.Add("@gameObjectName", SqliteType.Text);
            var pSlotIndex = cmd.Parameters.Add("@slotIndex", SqliteType.Integer);
            var pTransformMatrix = cmd.Parameters.Add("@transformMatrix", SqliteType.Blob);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);
            var pConfidenceReason = cmd.Parameters.Add("@confidenceReason", SqliteType.Text);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(meshes.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var mesh = meshes[i];
                pGroupId.Value = mesh.GroupId;
                pMeshId.Value = mesh.MeshAssetId;
                pRendererId.Value = mesh.RendererAssetId;
                pRendererType.Value = mesh.RendererType;
                pGameObjectId.Value = mesh.GameObjectAssetId;
                pGameObjectName.Value = mesh.GameObjectName;
                pSlotIndex.Value = mesh.SlotIndex;
                pTransformMatrix.Value = (object?)SerializeTransformMatrix(mesh.LocalToWorldMatrix) ?? DBNull.Value;
                pConfidence.Value = mesh.Confidence;
                pConfidenceReason.Value = mesh.ConfidenceReason;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving model group meshes: {i + 1:N0}/{meshes.Count:N0}");
            }
        }

        private static void InsertMeshRenderers(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMeshRendererRelation> renderers,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (renderers.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO MeshRenderers (
                    ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName, Description)
                VALUES (
                    @projectId, @meshAssetId, @rendererAssetId, @rendererType, @gameObjectAssetId, @gameObjectName, @description)
                ON CONFLICT(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType)
                DO UPDATE SET
                    GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                    GameObjectName = excluded.GameObjectName,
                    Description = excluded.Description";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            var pRendererAssetId = cmd.Parameters.Add("@rendererAssetId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pGameObjectAssetId = cmd.Parameters.Add("@gameObjectAssetId", SqliteType.Text);
            var pGameObjectName = cmd.Parameters.Add("@gameObjectName", SqliteType.Text);
            var pDescription = cmd.Parameters.Add("@description", SqliteType.Text);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(renderers.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var renderer = renderers[i];
                pMeshAssetId.Value = renderer.MeshAssetId;
                pRendererAssetId.Value = renderer.RendererAssetId;
                pRendererType.Value = renderer.RendererType;
                pGameObjectAssetId.Value = renderer.GameObjectAssetId;
                pGameObjectName.Value = renderer.GameObjectName;
                pDescription.Value = renderer.Description;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving mesh renderers: {i + 1:N0}/{renderers.Count:N0}");
            }
        }

        private static void InsertMeshMaterials(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMeshMaterialRelation> materials,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (materials.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO MeshMaterials (
                    ProjectId, MeshAssetUniqueID, MaterialAssetUniqueID, RendererAssetUniqueID, RendererType, SubMeshIndex, MaterialSlotIndex, MaterialScore)
                VALUES (
                    @projectId, @meshAssetId, @materialAssetId, @rendererAssetId, @rendererType, @subMeshIndex, @materialSlotIndex, @materialScore)
                ON CONFLICT(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID)
                DO UPDATE SET
                    RendererType = excluded.RendererType,
                    MaterialScore = excluded.MaterialScore";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            var pMaterialAssetId = cmd.Parameters.Add("@materialAssetId", SqliteType.Text);
            var pRendererAssetId = cmd.Parameters.Add("@rendererAssetId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pSubMeshIndex = cmd.Parameters.Add("@subMeshIndex", SqliteType.Integer);
            var pMaterialSlotIndex = cmd.Parameters.Add("@materialSlotIndex", SqliteType.Integer);
            var pMaterialScore = cmd.Parameters.Add("@materialScore", SqliteType.Integer);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(materials.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var material = materials[i];
                pMeshAssetId.Value = material.MeshAssetId;
                pMaterialAssetId.Value = material.MaterialAssetId;
                pRendererAssetId.Value = material.RendererAssetId;
                pRendererType.Value = material.RendererType;
                pSubMeshIndex.Value = material.SubMeshIndex;
                pMaterialSlotIndex.Value = material.MaterialSlotIndex;
                pMaterialScore.Value = material.MaterialScore;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving mesh materials: {i + 1:N0}/{materials.Count:N0}");
            }
        }

        private static void InsertMaterialTextures(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMaterialTextureRelation> textures,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (textures.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO MaterialTextures (
                    ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                    ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource)
                VALUES (
                    @projectId, @materialAssetId, @previewMaterialAssetId, @slotName, @slotIndex, @textureAssetId, @textureFileId, @texturePathId, @isResolved, @isMainTexture,
                    @scaleX, @scaleY, @offsetX, @offsetY, @resolutionSource)
                ON CONFLICT(ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                DO UPDATE SET
                    PreviewMaterialAssetUniqueID = excluded.PreviewMaterialAssetUniqueID,
                    IsResolved = excluded.IsResolved,
                    IsMainTexture = excluded.IsMainTexture,
                    ScaleX = excluded.ScaleX,
                    ScaleY = excluded.ScaleY,
                    OffsetX = excluded.OffsetX,
                    OffsetY = excluded.OffsetY,
                    ResolutionSource = excluded.ResolutionSource";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMaterialAssetId = cmd.Parameters.Add("@materialAssetId", SqliteType.Text);
            var pPreviewMaterialAssetId = cmd.Parameters.Add("@previewMaterialAssetId", SqliteType.Text);
            var pSlotName = cmd.Parameters.Add("@slotName", SqliteType.Text);
            var pSlotIndex = cmd.Parameters.Add("@slotIndex", SqliteType.Integer);
            var pTextureAssetId = cmd.Parameters.Add("@textureAssetId", SqliteType.Text);
            var pTextureFileId = cmd.Parameters.Add("@textureFileId", SqliteType.Integer);
            var pTexturePathId = cmd.Parameters.Add("@texturePathId", SqliteType.Integer);
            var pIsResolved = cmd.Parameters.Add("@isResolved", SqliteType.Integer);
            var pIsMainTexture = cmd.Parameters.Add("@isMainTexture", SqliteType.Integer);
            var pScaleX = cmd.Parameters.Add("@scaleX", SqliteType.Real);
            var pScaleY = cmd.Parameters.Add("@scaleY", SqliteType.Real);
            var pOffsetX = cmd.Parameters.Add("@offsetX", SqliteType.Real);
            var pOffsetY = cmd.Parameters.Add("@offsetY", SqliteType.Real);
            var pResolutionSource = cmd.Parameters.Add("@resolutionSource", SqliteType.Text);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(textures.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var texture = textures[i];
                pMaterialAssetId.Value = texture.MaterialAssetId;
                pPreviewMaterialAssetId.Value = texture.PreviewMaterialAssetId;
                pSlotName.Value = texture.SlotName;
                pSlotIndex.Value = texture.SlotIndex;
                pTextureAssetId.Value = texture.TextureAssetId;
                pTextureFileId.Value = texture.TextureFileId;
                pTexturePathId.Value = texture.TexturePathId;
                pIsResolved.Value = texture.IsResolved ? 1 : 0;
                pIsMainTexture.Value = texture.IsMainTexture ? 1 : 0;
                pScaleX.Value = texture.ScaleX;
                pScaleY.Value = texture.ScaleY;
                pOffsetX.Value = texture.OffsetX;
                pOffsetY.Value = texture.OffsetY;
                pResolutionSource.Value = texture.ResolutionSource;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving material textures: {i + 1:N0}/{textures.Count:N0}");
            }
        }

        private static void InsertMaterialCaches(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMaterialCacheEntry> entries,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (entries.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO MaterialCache (
                    ProjectId, MaterialAssetUniqueID, MaterialName, ParentMaterialAssetUniqueID,
                    MainTextureAssetUniqueID, MainTextureSlotName,
                    BaseColorR, BaseColorG, BaseColorB, BaseColorA,
                    ShaderName, TextureSlotCount, ResolutionSource)
                VALUES (
                    @projectId, @materialAssetId, @materialName, @parentMaterialAssetId,
                    @mainTextureAssetId, @mainTextureSlotName,
                    @baseColorR, @baseColorG, @baseColorB, @baseColorA,
                    @shaderName, @textureSlotCount, @resolutionSource)
                ON CONFLICT(ProjectId, MaterialAssetUniqueID)
                DO UPDATE SET
                    MaterialName = CASE WHEN excluded.MaterialName <> '' THEN excluded.MaterialName ELSE MaterialCache.MaterialName END,
                    ParentMaterialAssetUniqueID = CASE WHEN excluded.ParentMaterialAssetUniqueID <> '' THEN excluded.ParentMaterialAssetUniqueID ELSE MaterialCache.ParentMaterialAssetUniqueID END,
                    MainTextureAssetUniqueID = CASE WHEN excluded.MainTextureAssetUniqueID <> '' THEN excluded.MainTextureAssetUniqueID ELSE MaterialCache.MainTextureAssetUniqueID END,
                    MainTextureSlotName = CASE WHEN excluded.MainTextureSlotName <> '' THEN excluded.MainTextureSlotName ELSE MaterialCache.MainTextureSlotName END,
                    BaseColorR = excluded.BaseColorR,
                    BaseColorG = excluded.BaseColorG,
                    BaseColorB = excluded.BaseColorB,
                    BaseColorA = excluded.BaseColorA,
                    ShaderName = CASE WHEN excluded.ShaderName <> '' THEN excluded.ShaderName ELSE MaterialCache.ShaderName END,
                    TextureSlotCount = MAX(MaterialCache.TextureSlotCount, excluded.TextureSlotCount),
                    ResolutionSource = excluded.ResolutionSource";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMaterialAssetId = cmd.Parameters.Add("@materialAssetId", SqliteType.Text);
            var pMaterialName = cmd.Parameters.Add("@materialName", SqliteType.Text);
            var pParentMaterialAssetId = cmd.Parameters.Add("@parentMaterialAssetId", SqliteType.Text);
            var pMainTextureAssetId = cmd.Parameters.Add("@mainTextureAssetId", SqliteType.Text);
            var pMainTextureSlotName = cmd.Parameters.Add("@mainTextureSlotName", SqliteType.Text);
            var pBaseColorR = cmd.Parameters.Add("@baseColorR", SqliteType.Real);
            var pBaseColorG = cmd.Parameters.Add("@baseColorG", SqliteType.Real);
            var pBaseColorB = cmd.Parameters.Add("@baseColorB", SqliteType.Real);
            var pBaseColorA = cmd.Parameters.Add("@baseColorA", SqliteType.Real);
            var pShaderName = cmd.Parameters.Add("@shaderName", SqliteType.Text);
            var pTextureSlotCount = cmd.Parameters.Add("@textureSlotCount", SqliteType.Integer);
            var pResolutionSource = cmd.Parameters.Add("@resolutionSource", SqliteType.Text);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(entries.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrEmpty(entry.MaterialAssetId))
                {
                    continue;
                }

                pMaterialAssetId.Value = entry.MaterialAssetId;
                pMaterialName.Value = entry.MaterialName;
                pParentMaterialAssetId.Value = entry.ParentMaterialAssetId;
                pMainTextureAssetId.Value = entry.MainTextureAssetId;
                pMainTextureSlotName.Value = entry.MainTextureSlotName;
                pBaseColorR.Value = entry.BaseColorR;
                pBaseColorG.Value = entry.BaseColorG;
                pBaseColorB.Value = entry.BaseColorB;
                pBaseColorA.Value = entry.BaseColorA;
                pShaderName.Value = entry.ShaderName;
                pTextureSlotCount.Value = entry.TextureSlotCount;
                pResolutionSource.Value = entry.ResolutionSource;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving material cache: {i + 1:N0}/{entries.Count:N0}");
            }
        }

        private static void InsertCompositeAssets(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticCompositeAssetRelation> composites,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (composites.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO CompositeAssets (
                    ProjectId, CompositeAssetUniqueID, CompositeKind, DisplayName,
                    RootGameObjectAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                    SourceFileName, MeshPartCount, DistinctMeshCount, DistinctMaterialCount, Confidence, ResolutionSource)
                VALUES (
                    @projectId, @compositeId, @compositeKind, @displayName,
                    @rootGameObjectId, @animatorId, @avatarId, @controllerId,
                    @sourceFileName, @meshPartCount, @distinctMeshCount, @distinctMaterialCount, @confidence, @resolutionSource)
                ON CONFLICT(ProjectId, CompositeAssetUniqueID)
                DO UPDATE SET
                    CompositeKind = excluded.CompositeKind,
                    DisplayName = excluded.DisplayName,
                    RootGameObjectAssetUniqueID = excluded.RootGameObjectAssetUniqueID,
                    AnimatorAssetUniqueID = excluded.AnimatorAssetUniqueID,
                    AvatarAssetUniqueID = excluded.AvatarAssetUniqueID,
                    ControllerAssetUniqueID = excluded.ControllerAssetUniqueID,
                    SourceFileName = excluded.SourceFileName,
                    MeshPartCount = excluded.MeshPartCount,
                    DistinctMeshCount = excluded.DistinctMeshCount,
                    DistinctMaterialCount = excluded.DistinctMaterialCount,
                    Confidence = excluded.Confidence,
                    ResolutionSource = excluded.ResolutionSource";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pCompositeId = cmd.Parameters.Add("@compositeId", SqliteType.Text);
            var pCompositeKind = cmd.Parameters.Add("@compositeKind", SqliteType.Text);
            var pDisplayName = cmd.Parameters.Add("@displayName", SqliteType.Text);
            var pRootGameObjectId = cmd.Parameters.Add("@rootGameObjectId", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pControllerId = cmd.Parameters.Add("@controllerId", SqliteType.Text);
            var pSourceFileName = cmd.Parameters.Add("@sourceFileName", SqliteType.Text);
            var pMeshPartCount = cmd.Parameters.Add("@meshPartCount", SqliteType.Integer);
            var pDistinctMeshCount = cmd.Parameters.Add("@distinctMeshCount", SqliteType.Integer);
            var pDistinctMaterialCount = cmd.Parameters.Add("@distinctMaterialCount", SqliteType.Integer);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);
            var pResolutionSource = cmd.Parameters.Add("@resolutionSource", SqliteType.Text);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(composites.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var comp = composites[i];
                if (string.IsNullOrEmpty(comp.CompositeAssetId))
                {
                    continue;
                }

                pCompositeId.Value = comp.CompositeAssetId;
                pCompositeKind.Value = comp.CompositeKind;
                pDisplayName.Value = comp.DisplayName;
                pRootGameObjectId.Value = comp.RootGameObjectAssetId;
                pAnimatorId.Value = comp.AnimatorAssetId;
                pAvatarId.Value = comp.AvatarAssetId;
                pControllerId.Value = comp.ControllerAssetId;
                pSourceFileName.Value = comp.SourceFileName;
                pMeshPartCount.Value = comp.MeshPartCount;
                pDistinctMeshCount.Value = comp.DistinctMeshCount;
                pDistinctMaterialCount.Value = comp.DistinctMaterialCount;
                pConfidence.Value = comp.Confidence;
                pResolutionSource.Value = comp.ResolutionSource;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving composite assets: {i + 1:N0}/{composites.Count:N0}");
            }
        }

        private static void InsertCompositeAssetParts(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticCompositeAssetPartRelation> parts,
            int startIndex,
            int count,
            Action<string>? advanceProgress = null)
        {
            if (parts.Count == 0 || count <= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO CompositeAssetParts (
                    ProjectId, CompositeAssetUniqueID, PartIndex, MeshAssetUniqueID,
                    RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName,
                    TransformMatrix, PrimaryMaterialAssetUniqueID, Confidence)
                VALUES (
                    @projectId, @compositeId, @partIndex, @meshAssetId,
                    @rendererAssetId, @rendererType, @gameObjectAssetId, @gameObjectName,
                    @transformMatrix, @primaryMaterialAssetId, @confidence)
                ON CONFLICT(ProjectId, CompositeAssetUniqueID, PartIndex)
                DO UPDATE SET
                    MeshAssetUniqueID = excluded.MeshAssetUniqueID,
                    RendererAssetUniqueID = excluded.RendererAssetUniqueID,
                    RendererType = excluded.RendererType,
                    GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                    GameObjectName = excluded.GameObjectName,
                    TransformMatrix = excluded.TransformMatrix,
                    PrimaryMaterialAssetUniqueID = excluded.PrimaryMaterialAssetUniqueID,
                    Confidence = excluded.Confidence";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pCompositeId = cmd.Parameters.Add("@compositeId", SqliteType.Text);
            var pPartIndex = cmd.Parameters.Add("@partIndex", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            var pRendererAssetId = cmd.Parameters.Add("@rendererAssetId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pGameObjectAssetId = cmd.Parameters.Add("@gameObjectAssetId", SqliteType.Text);
            var pGameObjectName = cmd.Parameters.Add("@gameObjectName", SqliteType.Text);
            var pTransformMatrix = cmd.Parameters.Add("@transformMatrix", SqliteType.Blob);
            var pPrimaryMaterialAssetId = cmd.Parameters.Add("@primaryMaterialAssetId", SqliteType.Text);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);

            pProjectId.Value = projectId;
            var endIndex = Math.Min(parts.Count, startIndex + count);
            for (var i = startIndex; i < endIndex; i++)
            {
                var part = parts[i];
                if (string.IsNullOrEmpty(part.CompositeAssetId) || string.IsNullOrEmpty(part.MeshAssetId))
                {
                    continue;
                }

                pCompositeId.Value = part.CompositeAssetId;
                pPartIndex.Value = part.PartIndex;
                pMeshAssetId.Value = part.MeshAssetId;
                pRendererAssetId.Value = part.RendererAssetId;
                pRendererType.Value = part.RendererType;
                pGameObjectAssetId.Value = part.GameObjectAssetId;
                pGameObjectName.Value = part.GameObjectName;
                pTransformMatrix.Value = (object?)SerializeTransformMatrix(part.LocalToWorldMatrix) ?? DBNull.Value;
                pPrimaryMaterialAssetId.Value = part.PrimaryMaterialAssetId;
                pConfidence.Value = part.Confidence;
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving composite asset parts: {i + 1:N0}/{parts.Count:N0}");
            }
        }

        private static void InsertStagedSourceFiles(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyCollection<SemanticSourceFileEntry> sourceFiles)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                VALUES (@projectId, @serializedFileName, @originalPath, @unityVersion, @objectCount);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pSerializedFileName = cmd.Parameters.Add("@serializedFileName", SqliteType.Text);
            var pOriginalPath = cmd.Parameters.Add("@originalPath", SqliteType.Text);
            var pUnityVersion = cmd.Parameters.Add("@unityVersion", SqliteType.Text);
            var pObjectCount = cmd.Parameters.Add("@objectCount", SqliteType.Integer);

            pProjectId.Value = projectId;
            foreach (var sourceFile in sourceFiles)
            {
                pSerializedFileName.Value = sourceFile.SerializedFileName;
                pOriginalPath.Value = sourceFile.OriginalPath;
                pUnityVersion.Value = sourceFile.UnityVersion;
                pObjectCount.Value = sourceFile.ObjectCount;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedAssetEdges(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticAssetEdgeRelation> edges)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_AssetEdges (
                    ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                    SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                VALUES (
                    @projectId, @sourceAssetId, @edgeKind, @slotName, @slotIndex, @targetAssetId,
                    @sourceFileId, @sourcePathId, @targetFileId, @targetPathId, @isResolved);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pSourceAssetId = cmd.Parameters.Add("@sourceAssetId", SqliteType.Text);
            var pEdgeKind = cmd.Parameters.Add("@edgeKind", SqliteType.Text);
            var pSlotName = cmd.Parameters.Add("@slotName", SqliteType.Text);
            var pSlotIndex = cmd.Parameters.Add("@slotIndex", SqliteType.Integer);
            var pTargetAssetId = cmd.Parameters.Add("@targetAssetId", SqliteType.Text);
            var pSourceFileId = cmd.Parameters.Add("@sourceFileId", SqliteType.Integer);
            var pSourcePathId = cmd.Parameters.Add("@sourcePathId", SqliteType.Integer);
            var pTargetFileId = cmd.Parameters.Add("@targetFileId", SqliteType.Integer);
            var pTargetPathId = cmd.Parameters.Add("@targetPathId", SqliteType.Integer);
            var pIsResolved = cmd.Parameters.Add("@isResolved", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                pSourceAssetId.Value = edge.SourceAssetId;
                pEdgeKind.Value = edge.EdgeKind;
                pSlotName.Value = edge.SlotName;
                pSlotIndex.Value = edge.SlotIndex;
                pTargetAssetId.Value = edge.TargetAssetId;
                pSourceFileId.Value = edge.SourceFileId;
                pSourcePathId.Value = edge.SourcePathId;
                pTargetFileId.Value = edge.TargetFileId;
                pTargetPathId.Value = edge.TargetPathId;
                pIsResolved.Value = edge.IsResolved ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedModelGroups(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticModelGroupRelation> groups)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_ModelGroups (
                    ProjectId, GroupAssetUniqueID, GroupKind, GroupName,
                    RootGameObjectAssetUniqueID, RootGameObjectName,
                    AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                    SourceFileName, Confidence, ConfidenceReason)
                VALUES (
                    @projectId, @groupId, @groupKind, @groupName,
                    @rootGameObjectId, @rootGameObjectName,
                    @animatorId, @avatarId, @controllerId,
                    @sourceFileName, @confidence, @confidenceReason);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pGroupKind = cmd.Parameters.Add("@groupKind", SqliteType.Text);
            var pGroupName = cmd.Parameters.Add("@groupName", SqliteType.Text);
            var pRootGameObjectId = cmd.Parameters.Add("@rootGameObjectId", SqliteType.Text);
            var pRootGameObjectName = cmd.Parameters.Add("@rootGameObjectName", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pControllerId = cmd.Parameters.Add("@controllerId", SqliteType.Text);
            var pSourceFileName = cmd.Parameters.Add("@sourceFileName", SqliteType.Text);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);
            var pConfidenceReason = cmd.Parameters.Add("@confidenceReason", SqliteType.Text);

            pProjectId.Value = projectId;
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                pGroupId.Value = group.GroupId;
                pGroupKind.Value = group.GroupKind;
                pGroupName.Value = group.GroupName;
                pRootGameObjectId.Value = group.RootGameObjectAssetId;
                pRootGameObjectName.Value = group.RootGameObjectName;
                pAnimatorId.Value = group.AnimatorAssetId;
                pAvatarId.Value = group.AvatarAssetId;
                pControllerId.Value = group.ControllerAssetId;
                pSourceFileName.Value = group.SourceFileName;
                pConfidence.Value = group.Confidence;
                pConfidenceReason.Value = group.ConfidenceReason;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedModelGroupMeshes(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticModelGroupMeshRelation> meshes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_ModelGroupMeshes (
                    ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID,
                    RendererType, GameObjectAssetUniqueID, GameObjectName,
                    SlotIndex, TransformMatrix, Confidence, ConfidenceReason)
                VALUES (
                    @projectId, @groupId, @meshId, @rendererId,
                    @rendererType, @gameObjectId, @gameObjectName,
                    @slotIndex, @transformMatrix, @confidence, @confidenceReason);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pMeshId = cmd.Parameters.Add("@meshId", SqliteType.Text);
            var pRendererId = cmd.Parameters.Add("@rendererId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pGameObjectId = cmd.Parameters.Add("@gameObjectId", SqliteType.Text);
            var pGameObjectName = cmd.Parameters.Add("@gameObjectName", SqliteType.Text);
            var pSlotIndex = cmd.Parameters.Add("@slotIndex", SqliteType.Integer);
            var pTransformMatrix = cmd.Parameters.Add("@transformMatrix", SqliteType.Blob);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);
            var pConfidenceReason = cmd.Parameters.Add("@confidenceReason", SqliteType.Text);

            pProjectId.Value = projectId;
            for (var i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                pGroupId.Value = mesh.GroupId;
                pMeshId.Value = mesh.MeshAssetId;
                pRendererId.Value = mesh.RendererAssetId;
                pRendererType.Value = mesh.RendererType;
                pGameObjectId.Value = mesh.GameObjectAssetId;
                pGameObjectName.Value = mesh.GameObjectName;
                pSlotIndex.Value = mesh.SlotIndex;
                pTransformMatrix.Value = (object?)SerializeTransformMatrix(mesh.LocalToWorldMatrix) ?? DBNull.Value;
                pConfidence.Value = mesh.Confidence;
                pConfidenceReason.Value = mesh.ConfidenceReason;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedMeshRenderers(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMeshRendererRelation> renderers)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_MeshRenderers (
                    ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName, Description)
                VALUES (
                    @projectId, @meshAssetId, @rendererAssetId, @rendererType, @gameObjectAssetId, @gameObjectName, @description);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            var pRendererAssetId = cmd.Parameters.Add("@rendererAssetId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pGameObjectAssetId = cmd.Parameters.Add("@gameObjectAssetId", SqliteType.Text);
            var pGameObjectName = cmd.Parameters.Add("@gameObjectName", SqliteType.Text);
            var pDescription = cmd.Parameters.Add("@description", SqliteType.Text);

            pProjectId.Value = projectId;
            for (var i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                pMeshAssetId.Value = renderer.MeshAssetId;
                pRendererAssetId.Value = renderer.RendererAssetId;
                pRendererType.Value = renderer.RendererType;
                pGameObjectAssetId.Value = renderer.GameObjectAssetId;
                pGameObjectName.Value = renderer.GameObjectName;
                pDescription.Value = renderer.Description;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedMeshMaterials(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMeshMaterialRelation> materials)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_MeshMaterials (
                    ProjectId, MeshAssetUniqueID, MaterialAssetUniqueID, RendererAssetUniqueID, RendererType, SubMeshIndex, MaterialSlotIndex, MaterialScore)
                VALUES (
                    @projectId, @meshAssetId, @materialAssetId, @rendererAssetId, @rendererType, @subMeshIndex, @materialSlotIndex, @materialScore);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            var pMaterialAssetId = cmd.Parameters.Add("@materialAssetId", SqliteType.Text);
            var pRendererAssetId = cmd.Parameters.Add("@rendererAssetId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pSubMeshIndex = cmd.Parameters.Add("@subMeshIndex", SqliteType.Integer);
            var pMaterialSlotIndex = cmd.Parameters.Add("@materialSlotIndex", SqliteType.Integer);
            var pMaterialScore = cmd.Parameters.Add("@materialScore", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < materials.Count; i++)
            {
                var material = materials[i];
                pMeshAssetId.Value = material.MeshAssetId;
                pMaterialAssetId.Value = material.MaterialAssetId;
                pRendererAssetId.Value = material.RendererAssetId;
                pRendererType.Value = material.RendererType;
                pSubMeshIndex.Value = material.SubMeshIndex;
                pMaterialSlotIndex.Value = material.MaterialSlotIndex;
                pMaterialScore.Value = material.MaterialScore;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedMaterialTextures(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMaterialTextureRelation> textures)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_MaterialTextures (
                    ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                    ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource)
                VALUES (
                    @projectId, @materialAssetId, @previewMaterialAssetId, @slotName, @slotIndex, @textureAssetId, @textureFileId, @texturePathId, @isResolved, @isMainTexture,
                    @scaleX, @scaleY, @offsetX, @offsetY, @resolutionSource);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMaterialAssetId = cmd.Parameters.Add("@materialAssetId", SqliteType.Text);
            var pPreviewMaterialAssetId = cmd.Parameters.Add("@previewMaterialAssetId", SqliteType.Text);
            var pSlotName = cmd.Parameters.Add("@slotName", SqliteType.Text);
            var pSlotIndex = cmd.Parameters.Add("@slotIndex", SqliteType.Integer);
            var pTextureAssetId = cmd.Parameters.Add("@textureAssetId", SqliteType.Text);
            var pTextureFileId = cmd.Parameters.Add("@textureFileId", SqliteType.Integer);
            var pTexturePathId = cmd.Parameters.Add("@texturePathId", SqliteType.Integer);
            var pIsResolved = cmd.Parameters.Add("@isResolved", SqliteType.Integer);
            var pIsMainTexture = cmd.Parameters.Add("@isMainTexture", SqliteType.Integer);
            var pScaleX = cmd.Parameters.Add("@scaleX", SqliteType.Real);
            var pScaleY = cmd.Parameters.Add("@scaleY", SqliteType.Real);
            var pOffsetX = cmd.Parameters.Add("@offsetX", SqliteType.Real);
            var pOffsetY = cmd.Parameters.Add("@offsetY", SqliteType.Real);
            var pResolutionSource = cmd.Parameters.Add("@resolutionSource", SqliteType.Text);

            pProjectId.Value = projectId;
            for (var i = 0; i < textures.Count; i++)
            {
                var texture = textures[i];
                pMaterialAssetId.Value = texture.MaterialAssetId;
                pPreviewMaterialAssetId.Value = texture.PreviewMaterialAssetId;
                pSlotName.Value = texture.SlotName;
                pSlotIndex.Value = texture.SlotIndex;
                pTextureAssetId.Value = texture.TextureAssetId;
                pTextureFileId.Value = texture.TextureFileId;
                pTexturePathId.Value = texture.TexturePathId;
                pIsResolved.Value = texture.IsResolved ? 1 : 0;
                pIsMainTexture.Value = texture.IsMainTexture ? 1 : 0;
                pScaleX.Value = texture.ScaleX;
                pScaleY.Value = texture.ScaleY;
                pOffsetX.Value = texture.OffsetX;
                pOffsetY.Value = texture.OffsetY;
                pResolutionSource.Value = texture.ResolutionSource;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedMaterialCaches(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticMaterialCacheEntry> entries)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_MaterialCache (
                    ProjectId, MaterialAssetUniqueID, MaterialName, ParentMaterialAssetUniqueID,
                    MainTextureAssetUniqueID, MainTextureSlotName,
                    BaseColorR, BaseColorG, BaseColorB, BaseColorA,
                    ShaderName, TextureSlotCount, ResolutionSource)
                VALUES (
                    @projectId, @materialAssetId, @materialName, @parentMaterialAssetId,
                    @mainTextureAssetId, @mainTextureSlotName,
                    @baseColorR, @baseColorG, @baseColorB, @baseColorA,
                    @shaderName, @textureSlotCount, @resolutionSource);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMaterialAssetId = cmd.Parameters.Add("@materialAssetId", SqliteType.Text);
            var pMaterialName = cmd.Parameters.Add("@materialName", SqliteType.Text);
            var pParentMaterialAssetId = cmd.Parameters.Add("@parentMaterialAssetId", SqliteType.Text);
            var pMainTextureAssetId = cmd.Parameters.Add("@mainTextureAssetId", SqliteType.Text);
            var pMainTextureSlotName = cmd.Parameters.Add("@mainTextureSlotName", SqliteType.Text);
            var pBaseColorR = cmd.Parameters.Add("@baseColorR", SqliteType.Real);
            var pBaseColorG = cmd.Parameters.Add("@baseColorG", SqliteType.Real);
            var pBaseColorB = cmd.Parameters.Add("@baseColorB", SqliteType.Real);
            var pBaseColorA = cmd.Parameters.Add("@baseColorA", SqliteType.Real);
            var pShaderName = cmd.Parameters.Add("@shaderName", SqliteType.Text);
            var pTextureSlotCount = cmd.Parameters.Add("@textureSlotCount", SqliteType.Integer);
            var pResolutionSource = cmd.Parameters.Add("@resolutionSource", SqliteType.Text);

            pProjectId.Value = projectId;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrEmpty(entry.MaterialAssetId))
                {
                    continue;
                }

                pMaterialAssetId.Value = entry.MaterialAssetId;
                pMaterialName.Value = entry.MaterialName;
                pParentMaterialAssetId.Value = entry.ParentMaterialAssetId;
                pMainTextureAssetId.Value = entry.MainTextureAssetId;
                pMainTextureSlotName.Value = entry.MainTextureSlotName;
                pBaseColorR.Value = entry.BaseColorR;
                pBaseColorG.Value = entry.BaseColorG;
                pBaseColorB.Value = entry.BaseColorB;
                pBaseColorA.Value = entry.BaseColorA;
                pShaderName.Value = entry.ShaderName;
                pTextureSlotCount.Value = entry.TextureSlotCount;
                pResolutionSource.Value = entry.ResolutionSource;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedCompositeAssets(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticCompositeAssetRelation> composites)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_CompositeAssets (
                    ProjectId, CompositeAssetUniqueID, CompositeKind, DisplayName,
                    RootGameObjectAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                    SourceFileName, MeshPartCount, DistinctMeshCount, DistinctMaterialCount, Confidence, ResolutionSource)
                VALUES (
                    @projectId, @compositeId, @compositeKind, @displayName,
                    @rootGameObjectId, @animatorId, @avatarId, @controllerId,
                    @sourceFileName, @meshPartCount, @distinctMeshCount, @distinctMaterialCount, @confidence, @resolutionSource);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pCompositeId = cmd.Parameters.Add("@compositeId", SqliteType.Text);
            var pCompositeKind = cmd.Parameters.Add("@compositeKind", SqliteType.Text);
            var pDisplayName = cmd.Parameters.Add("@displayName", SqliteType.Text);
            var pRootGameObjectId = cmd.Parameters.Add("@rootGameObjectId", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pControllerId = cmd.Parameters.Add("@controllerId", SqliteType.Text);
            var pSourceFileName = cmd.Parameters.Add("@sourceFileName", SqliteType.Text);
            var pMeshPartCount = cmd.Parameters.Add("@meshPartCount", SqliteType.Integer);
            var pDistinctMeshCount = cmd.Parameters.Add("@distinctMeshCount", SqliteType.Integer);
            var pDistinctMaterialCount = cmd.Parameters.Add("@distinctMaterialCount", SqliteType.Integer);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);
            var pResolutionSource = cmd.Parameters.Add("@resolutionSource", SqliteType.Text);

            pProjectId.Value = projectId;
            for (var i = 0; i < composites.Count; i++)
            {
                var comp = composites[i];
                if (string.IsNullOrEmpty(comp.CompositeAssetId))
                {
                    continue;
                }

                pCompositeId.Value = comp.CompositeAssetId;
                pCompositeKind.Value = comp.CompositeKind;
                pDisplayName.Value = comp.DisplayName;
                pRootGameObjectId.Value = comp.RootGameObjectAssetId;
                pAnimatorId.Value = comp.AnimatorAssetId;
                pAvatarId.Value = comp.AvatarAssetId;
                pControllerId.Value = comp.ControllerAssetId;
                pSourceFileName.Value = comp.SourceFileName;
                pMeshPartCount.Value = comp.MeshPartCount;
                pDistinctMeshCount.Value = comp.DistinctMeshCount;
                pDistinctMaterialCount.Value = comp.DistinctMaterialCount;
                pConfidence.Value = comp.Confidence;
                pResolutionSource.Value = comp.ResolutionSource;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedCompositeAssetParts(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticCompositeAssetPartRelation> parts)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_CompositeAssetParts (
                    ProjectId, CompositeAssetUniqueID, PartIndex, MeshAssetUniqueID,
                    RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName,
                    TransformMatrix, PrimaryMaterialAssetUniqueID, Confidence)
                VALUES (
                    @projectId, @compositeId, @partIndex, @meshAssetId,
                    @rendererAssetId, @rendererType, @gameObjectAssetId, @gameObjectName,
                    @transformMatrix, @primaryMaterialAssetId, @confidence);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pCompositeId = cmd.Parameters.Add("@compositeId", SqliteType.Text);
            var pPartIndex = cmd.Parameters.Add("@partIndex", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            var pRendererAssetId = cmd.Parameters.Add("@rendererAssetId", SqliteType.Text);
            var pRendererType = cmd.Parameters.Add("@rendererType", SqliteType.Text);
            var pGameObjectAssetId = cmd.Parameters.Add("@gameObjectAssetId", SqliteType.Text);
            var pGameObjectName = cmd.Parameters.Add("@gameObjectName", SqliteType.Text);
            var pTransformMatrix = cmd.Parameters.Add("@transformMatrix", SqliteType.Blob);
            var pPrimaryMaterialAssetId = cmd.Parameters.Add("@primaryMaterialAssetId", SqliteType.Text);
            var pConfidence = cmd.Parameters.Add("@confidence", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (string.IsNullOrEmpty(part.CompositeAssetId) || string.IsNullOrEmpty(part.MeshAssetId))
                {
                    continue;
                }

                pCompositeId.Value = part.CompositeAssetId;
                pPartIndex.Value = part.PartIndex;
                pMeshAssetId.Value = part.MeshAssetId;
                pRendererAssetId.Value = part.RendererAssetId;
                pRendererType.Value = part.RendererType;
                pGameObjectAssetId.Value = part.GameObjectAssetId;
                pGameObjectName.Value = part.GameObjectName;
                pTransformMatrix.Value = (object?)SerializeTransformMatrix(part.LocalToWorldMatrix) ?? DBNull.Value;
                pPrimaryMaterialAssetId.Value = part.PrimaryMaterialAssetId;
                pConfidence.Value = part.Confidence;
                cmd.ExecuteNonQuery();
            }
        }

        private static void PromoteStagedSemanticRelationsToLive(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            Action<string>? advanceProgress = null)
        {
            var promotionSteps = new (string StageMessage, string Sql)[]
            {
                (
                    "Promoting staged semantic source files",
                    @"
                    INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                    SELECT @projectId, s.SerializedFileName, s.OriginalPath, s.UnityVersion, s.ObjectCount
                    FROM Stage_SourceFiles s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, SerializedFileName, OriginalPath)
                    DO UPDATE SET
                        UnityVersion = excluded.UnityVersion,
                        ObjectCount = excluded.ObjectCount,
                        LastSeen = CURRENT_TIMESTAMP;"
                ),
                (
                    "Promoting staged asset edges",
                    @"
                    INSERT INTO AssetEdges (
                        ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                        SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                    SELECT
                        @projectId,
                        s.SourceAssetUniqueID,
                        s.EdgeKind,
                        s.SlotName,
                        s.SlotIndex,
                        s.TargetAssetUniqueID,
                        COALESCE(srcAsset.SourceFileId, s.SourceFileId),
                        s.SourcePathID,
                        COALESCE(tgtAsset.SourceFileId, s.TargetFileId),
                        s.TargetPathID,
                        s.IsResolved
                    FROM Stage_AssetEdges s
                    LEFT JOIN Assets srcAsset
                      ON srcAsset.ProjectId = @projectId
                     AND s.SourceAssetUniqueID <> ''
                     AND srcAsset.UniqueID = s.SourceAssetUniqueID
                    LEFT JOIN Assets tgtAsset
                      ON tgtAsset.ProjectId = @projectId
                     AND s.TargetAssetUniqueID <> ''
                     AND tgtAsset.UniqueID = s.TargetAssetUniqueID
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID)
                    DO UPDATE SET
                        TargetAssetUniqueID = excluded.TargetAssetUniqueID,
                        IsResolved = excluded.IsResolved;"
                ),
                (
                    "Promoting staged model groups",
                    @"
                    INSERT INTO ModelGroups (
                        ProjectId, GroupAssetUniqueID, GroupKind, GroupName,
                        RootGameObjectAssetUniqueID, RootGameObjectName,
                        AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                        SourceFileName, Confidence, ConfidenceReason)
                    SELECT
                        @projectId, s.GroupAssetUniqueID, s.GroupKind, s.GroupName,
                        s.RootGameObjectAssetUniqueID, s.RootGameObjectName,
                        s.AnimatorAssetUniqueID, s.AvatarAssetUniqueID, s.ControllerAssetUniqueID,
                        s.SourceFileName, s.Confidence, s.ConfidenceReason
                    FROM Stage_ModelGroups s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, GroupAssetUniqueID)
                    DO UPDATE SET
                        GroupKind = excluded.GroupKind,
                        GroupName = excluded.GroupName,
                        RootGameObjectAssetUniqueID = excluded.RootGameObjectAssetUniqueID,
                        RootGameObjectName = excluded.RootGameObjectName,
                        AnimatorAssetUniqueID = excluded.AnimatorAssetUniqueID,
                        AvatarAssetUniqueID = excluded.AvatarAssetUniqueID,
                        ControllerAssetUniqueID = excluded.ControllerAssetUniqueID,
                        SourceFileName = excluded.SourceFileName,
                        Confidence = excluded.Confidence,
                        ConfidenceReason = excluded.ConfidenceReason;"
                ),
                (
                    "Promoting staged model group meshes",
                    @"
                    INSERT INTO ModelGroupMeshes (
                        ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID,
                        RendererType, GameObjectAssetUniqueID, GameObjectName,
                        SlotIndex, TransformMatrix, Confidence, ConfidenceReason)
                    SELECT
                        @projectId, s.GroupAssetUniqueID, s.MeshAssetUniqueID, s.RendererAssetUniqueID,
                        s.RendererType, s.GameObjectAssetUniqueID, s.GameObjectName,
                        s.SlotIndex, s.TransformMatrix, s.Confidence, s.ConfidenceReason
                    FROM Stage_ModelGroupMeshes s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID)
                    DO UPDATE SET
                        RendererType = excluded.RendererType,
                        GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                        GameObjectName = excluded.GameObjectName,
                        SlotIndex = excluded.SlotIndex,
                        TransformMatrix = excluded.TransformMatrix,
                        Confidence = excluded.Confidence,
                        ConfidenceReason = excluded.ConfidenceReason;"
                ),
                (
                    "Promoting staged mesh renderers",
                    @"
                    INSERT INTO MeshRenderers (
                        ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName, Description)
                    SELECT
                        @projectId, s.MeshAssetUniqueID, s.RendererAssetUniqueID, s.RendererType, s.GameObjectAssetUniqueID, s.GameObjectName, s.Description
                    FROM Stage_MeshRenderers s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType)
                    DO UPDATE SET
                        GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                        GameObjectName = excluded.GameObjectName,
                        Description = excluded.Description;"
                ),
                (
                    "Promoting staged mesh materials",
                    @"
                    INSERT INTO MeshMaterials (
                        ProjectId, MeshAssetUniqueID, MaterialAssetUniqueID, RendererAssetUniqueID, RendererType, SubMeshIndex, MaterialSlotIndex, MaterialScore)
                    SELECT
                        @projectId, s.MeshAssetUniqueID, s.MaterialAssetUniqueID, s.RendererAssetUniqueID, s.RendererType, s.SubMeshIndex, s.MaterialSlotIndex, s.MaterialScore
                    FROM Stage_MeshMaterials s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID)
                    DO UPDATE SET
                        RendererType = excluded.RendererType,
                        MaterialScore = excluded.MaterialScore;"
                ),
                (
                    "Promoting staged material textures",
                    @"
                    INSERT INTO MaterialTextures (
                        ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                        ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource)
                    SELECT
                        @projectId, s.MaterialAssetUniqueID, s.PreviewMaterialAssetUniqueID, s.SlotName, s.SlotIndex, s.TextureAssetUniqueID, s.TextureFileId, s.TexturePathID, s.IsResolved, s.IsMainTexture,
                        s.ScaleX, s.ScaleY, s.OffsetX, s.OffsetY, s.ResolutionSource
                    FROM Stage_MaterialTextures s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                    DO UPDATE SET
                        PreviewMaterialAssetUniqueID = excluded.PreviewMaterialAssetUniqueID,
                        IsResolved = excluded.IsResolved,
                        IsMainTexture = excluded.IsMainTexture,
                        ScaleX = excluded.ScaleX,
                        ScaleY = excluded.ScaleY,
                        OffsetX = excluded.OffsetX,
                        OffsetY = excluded.OffsetY,
                        ResolutionSource = excluded.ResolutionSource;"
                ),
                (
                    "Promoting staged material cache",
                    @"
                    INSERT INTO MaterialCache (
                        ProjectId, MaterialAssetUniqueID, MaterialName, ParentMaterialAssetUniqueID,
                        MainTextureAssetUniqueID, MainTextureSlotName,
                        BaseColorR, BaseColorG, BaseColorB, BaseColorA,
                        ShaderName, TextureSlotCount, ResolutionSource)
                    SELECT
                        @projectId, s.MaterialAssetUniqueID, s.MaterialName, s.ParentMaterialAssetUniqueID,
                        s.MainTextureAssetUniqueID, s.MainTextureSlotName,
                        s.BaseColorR, s.BaseColorG, s.BaseColorB, s.BaseColorA,
                        s.ShaderName, s.TextureSlotCount, s.ResolutionSource
                    FROM Stage_MaterialCache s
                    WHERE s.MaterialAssetUniqueID <> ''
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, MaterialAssetUniqueID)
                    DO UPDATE SET
                        MaterialName = CASE WHEN excluded.MaterialName <> '' THEN excluded.MaterialName ELSE MaterialCache.MaterialName END,
                        ParentMaterialAssetUniqueID = CASE WHEN excluded.ParentMaterialAssetUniqueID <> '' THEN excluded.ParentMaterialAssetUniqueID ELSE MaterialCache.ParentMaterialAssetUniqueID END,
                        MainTextureAssetUniqueID = CASE WHEN excluded.MainTextureAssetUniqueID <> '' THEN excluded.MainTextureAssetUniqueID ELSE MaterialCache.MainTextureAssetUniqueID END,
                        MainTextureSlotName = CASE WHEN excluded.MainTextureSlotName <> '' THEN excluded.MainTextureSlotName ELSE MaterialCache.MainTextureSlotName END,
                        BaseColorR = excluded.BaseColorR,
                        BaseColorG = excluded.BaseColorG,
                        BaseColorB = excluded.BaseColorB,
                        BaseColorA = excluded.BaseColorA,
                        ShaderName = CASE WHEN excluded.ShaderName <> '' THEN excluded.ShaderName ELSE MaterialCache.ShaderName END,
                        TextureSlotCount = MAX(MaterialCache.TextureSlotCount, excluded.TextureSlotCount),
                        ResolutionSource = excluded.ResolutionSource;"
                ),
                (
                    "Promoting staged composite assets",
                    @"
                    INSERT INTO CompositeAssets (
                        ProjectId, CompositeAssetUniqueID, CompositeKind, DisplayName,
                        RootGameObjectAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                        SourceFileName, MeshPartCount, DistinctMeshCount, DistinctMaterialCount, Confidence, ResolutionSource)
                    SELECT
                        @projectId, s.CompositeAssetUniqueID, s.CompositeKind, s.DisplayName,
                        s.RootGameObjectAssetUniqueID, s.AnimatorAssetUniqueID, s.AvatarAssetUniqueID, s.ControllerAssetUniqueID,
                        s.SourceFileName, s.MeshPartCount, s.DistinctMeshCount, s.DistinctMaterialCount, s.Confidence, s.ResolutionSource
                    FROM Stage_CompositeAssets s
                    WHERE s.CompositeAssetUniqueID <> ''
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, CompositeAssetUniqueID)
                    DO UPDATE SET
                        CompositeKind = excluded.CompositeKind,
                        DisplayName = excluded.DisplayName,
                        RootGameObjectAssetUniqueID = excluded.RootGameObjectAssetUniqueID,
                        AnimatorAssetUniqueID = excluded.AnimatorAssetUniqueID,
                        AvatarAssetUniqueID = excluded.AvatarAssetUniqueID,
                        ControllerAssetUniqueID = excluded.ControllerAssetUniqueID,
                        SourceFileName = excluded.SourceFileName,
                        MeshPartCount = excluded.MeshPartCount,
                        DistinctMeshCount = excluded.DistinctMeshCount,
                        DistinctMaterialCount = excluded.DistinctMaterialCount,
                        Confidence = excluded.Confidence,
                        ResolutionSource = excluded.ResolutionSource;"
                ),
                (
                    "Promoting staged composite asset parts",
                    @"
                    INSERT INTO CompositeAssetParts (
                        ProjectId, CompositeAssetUniqueID, PartIndex, MeshAssetUniqueID,
                        RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName,
                        TransformMatrix, PrimaryMaterialAssetUniqueID, Confidence)
                    SELECT
                        @projectId, s.CompositeAssetUniqueID, s.PartIndex, s.MeshAssetUniqueID,
                        s.RendererAssetUniqueID, s.RendererType, s.GameObjectAssetUniqueID, s.GameObjectName,
                        s.TransformMatrix, s.PrimaryMaterialAssetUniqueID, s.Confidence
                    FROM Stage_CompositeAssetParts s
                    WHERE s.CompositeAssetUniqueID <> '' AND s.MeshAssetUniqueID <> ''
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, CompositeAssetUniqueID, PartIndex)
                    DO UPDATE SET
                        MeshAssetUniqueID = excluded.MeshAssetUniqueID,
                        RendererAssetUniqueID = excluded.RendererAssetUniqueID,
                        RendererType = excluded.RendererType,
                        GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                        GameObjectName = excluded.GameObjectName,
                        TransformMatrix = excluded.TransformMatrix,
                        PrimaryMaterialAssetUniqueID = CASE WHEN excluded.PrimaryMaterialAssetUniqueID <> '' THEN excluded.PrimaryMaterialAssetUniqueID ELSE CompositeAssetParts.PrimaryMaterialAssetUniqueID END,
                        Confidence = excluded.Confidence;"
                )
            };

            foreach (var step in promotionSteps)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = step.Sql;
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke(step.StageMessage);
            }

            // CRITICAL: Recreate indexes required by PromoteStagedAnimationRelationsToLive and derived cache
            // materialization BEFORE running them (prevents 364M-row full-table scans on ModelGroups/AssetEdges!).
            CreateSemanticPreAnimationIndexes(conn, transaction);
            advanceProgress?.Invoke("Built pre-animation relation indexes");

            BackfillV15DerivedCachesForProject(conn, transaction, projectId);
            advanceProgress?.Invoke("Materialized resolved material and composite caches");

            PromoteStagedAnimationRelationsToLive(conn, transaction, projectId, advanceProgress);
        }

        private static void InsertStagedAnimationClipTargets(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticAnimationClipTargetRelation> targets)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_AnimationClipTargets (
                    ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                    MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                VALUES (
                    @projectId, @clipId, @animatorId, @avatarId,
                    @meshId, @groupId, @relationKind, @matchScore, @matchedBones, @totalClipBones);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pClipId = cmd.Parameters.Add("@clipId", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pMeshId = cmd.Parameters.Add("@meshId", SqliteType.Text);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pRelationKind = cmd.Parameters.Add("@relationKind", SqliteType.Text);
            var pMatchScore = cmd.Parameters.Add("@matchScore", SqliteType.Integer);
            var pMatchedBones = cmd.Parameters.Add("@matchedBones", SqliteType.Integer);
            var pTotalClipBones = cmd.Parameters.Add("@totalClipBones", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                pClipId.Value = target.ClipAssetId;
                pAnimatorId.Value = target.AnimatorAssetId;
                pAvatarId.Value = target.AvatarAssetId;
                pMeshId.Value = target.MeshAssetId;
                pGroupId.Value = target.ModelGroupId;
                pRelationKind.Value = target.RelationKind;
                pMatchScore.Value = target.MatchScore;
                pMatchedBones.Value = target.MatchedBoneCount;
                pTotalClipBones.Value = target.TotalClipBoneCount;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedRigBoneHashes(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticRigBoneHashEntry> rigHashes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_RigBoneHashes (
                    ProjectId, AvatarAssetUniqueID, AnimatorAssetUniqueID, MeshAssetUniqueID, ModelGroupId, BoneHash)
                VALUES (
                    @projectId, @avatarId, @animatorId, @meshId, @groupId, @boneHash);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pMeshId = cmd.Parameters.Add("@meshId", SqliteType.Text);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pBoneHash = cmd.Parameters.Add("@boneHash", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < rigHashes.Count; i++)
            {
                var entry = rigHashes[i];
                if (entry.BoneHash == 0)
                {
                    continue;
                }

                pAvatarId.Value = entry.AvatarAssetId;
                pAnimatorId.Value = entry.AnimatorAssetId;
                pMeshId.Value = entry.MeshAssetId;
                pGroupId.Value = entry.ModelGroupId;
                pBoneHash.Value = (long)entry.BoneHash;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertStagedClipBoneHashes(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticClipBoneHashEntry> clipHashes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Stage_ClipBoneHashes (
                    ProjectId, ClipAssetUniqueID, BoneHash, TotalClipBoneCount)
                VALUES (
                    @projectId, @clipId, @boneHash, @totalClipBones);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pClipId = cmd.Parameters.Add("@clipId", SqliteType.Text);
            var pBoneHash = cmd.Parameters.Add("@boneHash", SqliteType.Integer);
            var pTotalClipBones = cmd.Parameters.Add("@totalClipBones", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < clipHashes.Count; i++)
            {
                var entry = clipHashes[i];
                if (entry.BoneHash == 0 || string.IsNullOrEmpty(entry.ClipAssetId))
                {
                    continue;
                }

                pClipId.Value = entry.ClipAssetId;
                pBoneHash.Value = (long)entry.BoneHash;
                pTotalClipBones.Value = entry.TotalClipBoneCount;
                cmd.ExecuteNonQuery();
            }
        }

        private static void StageAndPromoteAnimationRelations(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            SemanticAssetRelations relations,
            Action<string>? advanceProgress = null)
        {
            using (var createCmd = conn.CreateCommand())
            {
                createCmd.Transaction = transaction;
                createCmd.CommandText = @"
                    DROP TABLE IF EXISTS Stage_AnimationClipTargets;
                    DROP TABLE IF EXISTS Stage_RigBoneHashes;
                    DROP TABLE IF EXISTS Stage_ClipBoneHashes;

                    CREATE TABLE Stage_AnimationClipTargets (
                        ProjectId INTEGER NOT NULL,
                        ClipAssetUniqueID TEXT NOT NULL,
                        AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                        AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                        MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                        ModelGroupId TEXT NOT NULL DEFAULT '',
                        RelationKind TEXT NOT NULL DEFAULT '',
                        MatchScore INTEGER NOT NULL DEFAULT 0,
                        MatchedBoneCount INTEGER NOT NULL DEFAULT 0,
                        TotalClipBoneCount INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE Stage_RigBoneHashes (
                        ProjectId INTEGER NOT NULL,
                        AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                        AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                        MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                        ModelGroupId TEXT NOT NULL DEFAULT '',
                        BoneHash INTEGER NOT NULL
                    );

                    CREATE TABLE Stage_ClipBoneHashes (
                        ProjectId INTEGER NOT NULL,
                        ClipAssetUniqueID TEXT NOT NULL,
                        BoneHash INTEGER NOT NULL,
                        TotalClipBoneCount INTEGER NOT NULL DEFAULT 0
                    );";
                createCmd.ExecuteNonQuery();
            }

            if (relations.AnimationClipTargets.Count > 0)
            {
                InsertStagedAnimationClipTargets(conn, transaction, projectId, relations.AnimationClipTargets);
            }

            if (relations.RigBoneHashes.Count > 0)
            {
                InsertStagedRigBoneHashes(conn, transaction, projectId, relations.RigBoneHashes);
            }

            if (relations.ClipBoneHashes.Count > 0)
            {
                InsertStagedClipBoneHashes(conn, transaction, projectId, relations.ClipBoneHashes);
            }

            PromoteStagedAnimationRelationsToLive(conn, transaction, projectId, advanceProgress);

            using (var dropCmd = conn.CreateCommand())
            {
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = @"
                    DROP TABLE IF EXISTS Stage_AnimationClipTargets;
                    DROP TABLE IF EXISTS Stage_RigBoneHashes;
                    DROP TABLE IF EXISTS Stage_ClipBoneHashes;";
                dropCmd.ExecuteNonQuery();
            }
        }

        private static void PromoteStagedAnimationRelationsToLive(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            Action<string>? advanceProgress = null)
        {
            // 1. Direct staged AnimationClipTargets
            using (var directCmd = conn.CreateCommand())
            {
                directCmd.Transaction = transaction;
                directCmd.CommandText = @"
                    INSERT INTO AnimationClipTargets (
                        ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                        MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                    SELECT
                        @projectId,
                        s.ClipAssetUniqueID,
                        s.AnimatorAssetUniqueID,
                        s.AvatarAssetUniqueID,
                        s.MeshAssetUniqueID,
                        s.ModelGroupId,
                        s.RelationKind,
                        s.MatchScore,
                        s.MatchedBoneCount,
                        s.TotalClipBoneCount
                    FROM Stage_AnimationClipTargets s
                    WHERE s.ClipAssetUniqueID <> ''
                      AND (s.AvatarAssetUniqueID <> '' OR s.MeshAssetUniqueID <> '' OR s.ModelGroupId <> '')
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                    DO UPDATE SET
                        MatchScore = MAX(AnimationClipTargets.MatchScore, excluded.MatchScore),
                        MatchedBoneCount = MAX(AnimationClipTargets.MatchedBoneCount, excluded.MatchedBoneCount),
                        TotalClipBoneCount = MAX(AnimationClipTargets.TotalClipBoneCount, excluded.TotalClipBoneCount);";
                directCmd.Parameters.AddWithValue("@projectId", projectId);
                directCmd.ExecuteNonQuery();
            }
            advanceProgress?.Invoke("Promoted direct animation clip targets");

            // 2. Cross-bundle direct relations synthesized from AssetEdges + ModelGroups + ModelGroupMeshes.
            // Pre-materialize small WITHOUT ROWID PRIMARY KEY temp lookups so SQLite never re-evaluates
            // correlated scalar subqueries twice per row across large shared-controller fan-outs (zero data loss).
            using (var tempPrepCmd = conn.CreateCommand())
            {
                tempPrepCmd.Transaction = transaction;
                tempPrepCmd.CommandText = @"
                    DROP TABLE IF EXISTS temp.tmp_sem_group_first_mesh;
                    DROP TABLE IF EXISTS temp.tmp_sem_source_first_avatar;
                    DROP TABLE IF EXISTS temp.tmp_sem_source_first_mesh;
                    DROP TABLE IF EXISTS temp.tmp_sem_animator_first_group;

                    CREATE TEMP TABLE tmp_sem_group_first_mesh (
                        GroupId TEXT PRIMARY KEY,
                        MeshId TEXT NOT NULL
                    ) WITHOUT ROWID;

                    INSERT INTO tmp_sem_group_first_mesh (GroupId, MeshId)
                    SELECT GroupAssetUniqueID, MeshAssetUniqueID
                    FROM (
                        SELECT
                            mgm.GroupAssetUniqueID,
                            mgm.MeshAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY mgm.GroupAssetUniqueID
                                ORDER BY mgm.SlotIndex ASC, mgm.MeshAssetUniqueID ASC
                            ) AS rn
                        FROM ModelGroupMeshes mgm
                        WHERE mgm.ProjectId = @projectId
                          AND mgm.GroupAssetUniqueID <> ''
                          AND mgm.MeshAssetUniqueID <> ''
                    )
                    WHERE rn = 1;

                    CREATE TEMP TABLE tmp_sem_source_first_avatar (
                        SourceId TEXT PRIMARY KEY,
                        AvatarId TEXT NOT NULL
                    ) WITHOUT ROWID;

                    INSERT INTO tmp_sem_source_first_avatar (SourceId, AvatarId)
                    SELECT SourceAssetUniqueID, TargetAssetUniqueID
                    FROM (
                        SELECT
                            ae.SourceAssetUniqueID,
                            ae.TargetAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY ae.SourceAssetUniqueID
                                ORDER BY ae.SlotName ASC, ae.SlotIndex ASC
                            ) AS rn
                        FROM AssetEdges ae
                        WHERE ae.ProjectId = @projectId
                          AND ae.EdgeKind = 'Avatar'
                          AND ae.SourceAssetUniqueID <> ''
                          AND ae.TargetAssetUniqueID <> ''
                    )
                    WHERE rn = 1;

                    CREATE TEMP TABLE tmp_sem_source_first_mesh (
                        SourceId TEXT PRIMARY KEY,
                        MeshId TEXT NOT NULL
                    ) WITHOUT ROWID;

                    INSERT INTO tmp_sem_source_first_mesh (SourceId, MeshId)
                    SELECT SourceAssetUniqueID, TargetAssetUniqueID
                    FROM (
                        SELECT
                            me.SourceAssetUniqueID,
                            me.TargetAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY me.SourceAssetUniqueID
                                ORDER BY me.SlotName ASC, me.SlotIndex ASC
                            ) AS rn
                        FROM AssetEdges me
                        WHERE me.ProjectId = @projectId
                          AND me.EdgeKind = 'Mesh'
                          AND me.SourceAssetUniqueID <> ''
                          AND me.TargetAssetUniqueID <> ''
                    )
                    WHERE rn = 1;

                    CREATE TEMP TABLE tmp_sem_animator_first_group (
                        AnimatorId TEXT PRIMARY KEY,
                        GroupId TEXT NOT NULL,
                        AvatarId TEXT NOT NULL
                    ) WITHOUT ROWID;

                    INSERT INTO tmp_sem_animator_first_group (AnimatorId, GroupId, AvatarId)
                    SELECT AnimatorAssetUniqueID, GroupAssetUniqueID, AvatarAssetUniqueID
                    FROM (
                        SELECT
                            mg.AnimatorAssetUniqueID,
                            mg.GroupAssetUniqueID,
                            mg.AvatarAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY mg.AnimatorAssetUniqueID
                                ORDER BY mg.Confidence DESC, mg.GroupAssetUniqueID ASC
                            ) AS rn
                        FROM ModelGroups mg
                        WHERE mg.ProjectId = @projectId
                          AND mg.AnimatorAssetUniqueID <> ''
                    )
                    WHERE rn = 1;";
                tempPrepCmd.Parameters.AddWithValue("@projectId", projectId);
                tempPrepCmd.ExecuteNonQuery();
            }

            try
            {
                using var edgeSynthCmd = conn.CreateCommand();
                edgeSynthCmd.Transaction = transaction;
                edgeSynthCmd.CommandText = @"
                    WITH DirectEdges AS (
                        -- (a) Clip -> Avatar edge (AnimatorAvatar / CompatibleAvatar) joined with ModelGroups or Avatar->Mesh edges
                        SELECT
                            ca.SourceAssetUniqueID AS ClipId,
                            COALESCE(mg.AnimatorAssetUniqueID, '') AS AnimatorId,
                            ca.TargetAssetUniqueID AS AvatarId,
                            COALESCE(gfm.MeshId, sfm.MeshId, '') AS MeshId,
                            COALESCE(mg.GroupAssetUniqueID, '') AS GroupId,
                            CASE WHEN ca.SlotName = 'CompatibleAvatar' THEN 'CompatibleAvatar' ELSE 'AnimatorAvatar' END AS RelationKind,
                            (CASE WHEN ca.SlotName = 'CompatibleAvatar' THEN 8500 ELSE 9500 END)
                                + COALESCE(mg.Confidence, 0)
                                + (CASE WHEN COALESCE(mg.GroupAssetUniqueID, '') <> '' THEN 100 ELSE 0 END)
                                - MIN(MAX(ca.SlotIndex, 0), 100) AS Score
                        FROM AssetEdges ca
                        LEFT JOIN ModelGroups mg
                          ON mg.ProjectId = ca.ProjectId
                         AND mg.AvatarAssetUniqueID = ca.TargetAssetUniqueID
                        LEFT JOIN temp.tmp_sem_group_first_mesh gfm
                          ON gfm.GroupId = mg.GroupAssetUniqueID
                        LEFT JOIN temp.tmp_sem_source_first_mesh sfm
                          ON sfm.SourceId = ca.TargetAssetUniqueID
                        WHERE ca.ProjectId = @projectId
                          AND ca.EdgeKind = 'Avatar'
                          AND ca.SlotName IN ('AnimatorAvatar', 'CompatibleAvatar')
                          AND ca.SourceAssetUniqueID <> ''
                          AND ca.TargetAssetUniqueID <> ''

                        UNION ALL

                        -- (b1) ModelGroups linked directly via ControllerAssetUniqueID -> AnimationClip edges
                        SELECT
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            mg.AnimatorAssetUniqueID AS AnimatorId,
                            mg.AvatarAssetUniqueID AS AvatarId,
                            COALESCE(gfm.MeshId, '') AS MeshId,
                            mg.GroupAssetUniqueID AS GroupId,
                            'AnimatorController' AS RelationKind,
                            10000 + COALESCE(mg.Confidence, 0) + 150 - MIN(MAX(clipEdge.SlotIndex, 0), 100) AS Score
                        FROM ModelGroups mg
                        INNER JOIN AssetEdges clipEdge
                          ON clipEdge.ProjectId = @projectId
                         AND clipEdge.EdgeKind = 'AnimationClip'
                         AND clipEdge.SourceAssetUniqueID = mg.ControllerAssetUniqueID
                         AND clipEdge.TargetAssetUniqueID <> ''
                        LEFT JOIN temp.tmp_sem_group_first_mesh gfm
                          ON gfm.GroupId = mg.GroupAssetUniqueID
                        WHERE mg.ProjectId = @projectId
                          AND mg.ControllerAssetUniqueID <> ''

                        UNION ALL

                        -- (b2) ModelGroups linked via AnimatorOverrideController -> base AnimatorController -> AnimationClip edges
                        SELECT
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            mg.AnimatorAssetUniqueID AS AnimatorId,
                            mg.AvatarAssetUniqueID AS AvatarId,
                            COALESCE(gfm.MeshId, '') AS MeshId,
                            mg.GroupAssetUniqueID AS GroupId,
                            'AnimatorOverrideController' AS RelationKind,
                            9800 + COALESCE(mg.Confidence, 0) + 150 - MIN(MAX(clipEdge.SlotIndex, 0), 100) AS Score
                        FROM ModelGroups mg
                        INNER JOIN AssetEdges ctrlEdge
                          ON ctrlEdge.ProjectId = @projectId
                         AND ctrlEdge.EdgeKind = 'AnimatorController'
                         AND ctrlEdge.SourceAssetUniqueID = mg.ControllerAssetUniqueID
                         AND ctrlEdge.SlotName = 'm_Controller'
                         AND ctrlEdge.TargetAssetUniqueID <> ''
                        INNER JOIN AssetEdges clipEdge
                          ON clipEdge.ProjectId = @projectId
                         AND clipEdge.EdgeKind = 'AnimationClip'
                         AND clipEdge.SourceAssetUniqueID = ctrlEdge.TargetAssetUniqueID
                         AND clipEdge.TargetAssetUniqueID <> ''
                        LEFT JOIN temp.tmp_sem_group_first_mesh gfm
                          ON gfm.GroupId = mg.GroupAssetUniqueID
                        WHERE mg.ProjectId = @projectId
                          AND mg.ControllerAssetUniqueID <> ''

                        UNION ALL

                        -- (c1) Animator / Avatar / Animation source directly holding AnimationClip edge + Avatar/Mesh edges
                        SELECT
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            CASE WHEN srcAsset.Type IN (@animatorType, @animationType) THEN clipEdge.SourceAssetUniqueID ELSE '' END AS AnimatorId,
                            COALESCE(
                                sfa.AvatarId,
                                CASE WHEN srcAsset.Type = @avatarType THEN clipEdge.SourceAssetUniqueID ELSE '' END,
                                ''
                            ) AS AvatarId,
                            COALESCE(sfm.MeshId, '') AS MeshId,
                            COALESCE(afg.GroupId, '') AS GroupId,
                            CASE
                                WHEN clipEdge.SlotName = 'LegacyAnimationClip' THEN 'LegacyAnimation'
                                ELSE 'AnimatorController'
                            END AS RelationKind,
                            9600 - MIN(MAX(clipEdge.SlotIndex, 0), 100) AS Score
                        FROM AssetEdges clipEdge
                        LEFT JOIN Assets srcAsset
                          ON srcAsset.ProjectId = clipEdge.ProjectId
                         AND srcAsset.UniqueID = clipEdge.SourceAssetUniqueID
                        LEFT JOIN temp.tmp_sem_source_first_avatar sfa
                          ON sfa.SourceId = clipEdge.SourceAssetUniqueID
                        LEFT JOIN temp.tmp_sem_source_first_mesh sfm
                          ON sfm.SourceId = clipEdge.SourceAssetUniqueID
                        LEFT JOIN temp.tmp_sem_animator_first_group afg
                          ON afg.AnimatorId = clipEdge.SourceAssetUniqueID
                        WHERE clipEdge.ProjectId = @projectId
                          AND clipEdge.EdgeKind = 'AnimationClip'
                          AND clipEdge.TargetAssetUniqueID <> ''
                          AND srcAsset.Type IN (@animatorType, @avatarType, @animationType)

                        UNION ALL

                        -- (c2) Animator -> AnimatorController edge -> AnimationClip edge
                        SELECT
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            ctrlEdge.SourceAssetUniqueID AS AnimatorId,
                            COALESCE(NULLIF(sfa.AvatarId, ''), afg.AvatarId, '') AS AvatarId,
                            COALESCE(sfm.MeshId, '') AS MeshId,
                            COALESCE(afg.GroupId, '') AS GroupId,
                            'AnimatorController' AS RelationKind,
                            10000 - MIN(MAX(clipEdge.SlotIndex, 0), 100) AS Score
                        FROM AssetEdges ctrlEdge
                        INNER JOIN AssetEdges clipEdge
                          ON clipEdge.ProjectId = @projectId
                         AND clipEdge.EdgeKind = 'AnimationClip'
                         AND clipEdge.SourceAssetUniqueID = ctrlEdge.TargetAssetUniqueID
                         AND clipEdge.TargetAssetUniqueID <> ''
                        LEFT JOIN temp.tmp_sem_source_first_avatar sfa
                          ON sfa.SourceId = ctrlEdge.SourceAssetUniqueID
                        LEFT JOIN temp.tmp_sem_source_first_mesh sfm
                          ON sfm.SourceId = ctrlEdge.SourceAssetUniqueID
                        LEFT JOIN temp.tmp_sem_animator_first_group afg
                          ON afg.AnimatorId = ctrlEdge.SourceAssetUniqueID
                        WHERE ctrlEdge.ProjectId = @projectId
                          AND ctrlEdge.EdgeKind = 'AnimatorController'
                          AND ctrlEdge.SlotName = 'm_Controller'
                          AND ctrlEdge.SourceAssetUniqueID <> ''
                          AND ctrlEdge.TargetAssetUniqueID <> ''
                    )
                    INSERT INTO AnimationClipTargets (
                        ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                        MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                    SELECT
                        @projectId,
                        d.ClipId,
                        d.AnimatorId,
                        d.AvatarId,
                        d.MeshId,
                        d.GroupId,
                        d.RelationKind,
                        MAX(d.Score),
                        0,
                        0
                    FROM DirectEdges d
                    WHERE d.ClipId <> ''
                      AND (d.AvatarId <> '' OR d.MeshId <> '' OR d.GroupId <> '')
                    GROUP BY d.ClipId, d.AvatarId, d.MeshId, d.GroupId, d.AnimatorId, d.RelationKind
                    ON CONFLICT(ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                    DO UPDATE SET
                        MatchScore = MAX(AnimationClipTargets.MatchScore, excluded.MatchScore);";
                edgeSynthCmd.Parameters.AddWithValue("@projectId", projectId);
                edgeSynthCmd.Parameters.AddWithValue("@animatorType", (int)ClassIDType.Animator);
                edgeSynthCmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                edgeSynthCmd.Parameters.AddWithValue("@animationType", (int)ClassIDType.Animation);
                edgeSynthCmd.ExecuteNonQuery();
            }
            finally
            {
                using var cleanupCmd = conn.CreateCommand();
                cleanupCmd.Transaction = transaction;
                cleanupCmd.CommandText = @"
                    DROP TABLE IF EXISTS temp.tmp_sem_group_first_mesh;
                    DROP TABLE IF EXISTS temp.tmp_sem_source_first_avatar;
                    DROP TABLE IF EXISTS temp.tmp_sem_source_first_mesh;
                    DROP TABLE IF EXISTS temp.tmp_sem_animator_first_group;";
                cleanupCmd.ExecuteNonQuery();
            }
            advanceProgress?.Invoke("Synthesized cross-bundle animation targets");

            // 3. Cross-bundle structural bone-hash matching in memory (C# inverted index + Parallel.For).
            // Produces mathematically identical top-16 BoneHashMatch rows as the former SQL CTE without
            // unindexed window-CTE nested loops or multi-GB temp B-trees.
            var promotedAnyAvatarBoneMatch = PromoteStagedBoneHashMatchesInMemory(conn, transaction, projectId, out var hadStagedHashes);
            if (hadStagedHashes)
            {
                if (promotedAnyAvatarBoneMatch)
                {
                    // Also synthesize CompatibleAvatar edges into AssetEdges for top bone-hash matches
                    // so legacy LoadAnimationClipAvatarAssetIds / LoadAnimationClipMeshAssetIds queries
                    // see decoupled bone-hash-matched rigs immediately.
                    using var compatEdgeCmd = conn.CreateCommand();
                    compatEdgeCmd.Transaction = transaction;
                    compatEdgeCmd.CommandText = @"
                        WITH RankedAvatarTargets AS (
                            SELECT
                                t.ClipAssetUniqueID AS ClipId,
                                t.AvatarAssetUniqueID AS AvatarId,
                                ROW_NUMBER() OVER (
                                    PARTITION BY t.ClipAssetUniqueID
                                    ORDER BY MAX(t.MatchScore) DESC, MAX(t.MatchedBoneCount) DESC, t.AvatarAssetUniqueID ASC
                                ) - 1 AS SlotIdx
                            FROM AnimationClipTargets t
                            WHERE t.ProjectId = @projectId
                              AND t.RelationKind = 'BoneHashMatch'
                              AND t.AvatarAssetUniqueID <> ''
                            GROUP BY t.ClipAssetUniqueID, t.AvatarAssetUniqueID
                        )
                        INSERT INTO AssetEdges (
                            ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                            SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                        SELECT
                            @projectId,
                            rat.ClipId,
                            'Avatar',
                            'CompatibleAvatar',
                            rat.SlotIdx,
                            rat.AvatarId,
                            COALESCE(srcAsset.SourceFileId, 0),
                            COALESCE(srcAsset.PathID, 0),
                            COALESCE(tgtAsset.SourceFileId, 0),
                            COALESCE(tgtAsset.PathID, 0),
                            1
                        FROM RankedAvatarTargets rat
                        LEFT JOIN Assets srcAsset
                          ON srcAsset.ProjectId = @projectId
                         AND srcAsset.UniqueID = rat.ClipId
                        LEFT JOIN Assets tgtAsset
                          ON tgtAsset.ProjectId = @projectId
                         AND tgtAsset.UniqueID = rat.AvatarId
                        WHERE rat.SlotIdx < 8
                        ON CONFLICT(ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID)
                        DO UPDATE SET
                            TargetAssetUniqueID = excluded.TargetAssetUniqueID,
                            IsResolved = excluded.IsResolved;";
                    compatEdgeCmd.Parameters.AddWithValue("@projectId", projectId);
                    compatEdgeCmd.ExecuteNonQuery();
                }

                advanceProgress?.Invoke("Matched cross-bundle rig bone hashes");
            }
            else
            {
                advanceProgress?.Invoke("Skipped bone-hash matching (no staged hashes)");
            }

            advanceProgress?.Invoke("Promoted animation clip targets");
        }

        private readonly struct StagedRigKey : IEquatable<StagedRigKey>
        {
            public readonly string AvatarId;
            public readonly string AnimatorId;
            public readonly string MeshId;
            public readonly string GroupId;

            public StagedRigKey(string avatarId, string animatorId, string meshId, string groupId)
            {
                AvatarId = avatarId;
                AnimatorId = animatorId;
                MeshId = meshId;
                GroupId = groupId;
            }

            public bool Equals(StagedRigKey other) =>
                string.Equals(AvatarId, other.AvatarId, StringComparison.Ordinal)
                && string.Equals(AnimatorId, other.AnimatorId, StringComparison.Ordinal)
                && string.Equals(MeshId, other.MeshId, StringComparison.Ordinal)
                && string.Equals(GroupId, other.GroupId, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is StagedRigKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(AvatarId),
                StringComparer.Ordinal.GetHashCode(AnimatorId),
                StringComparer.Ordinal.GetHashCode(MeshId),
                StringComparer.Ordinal.GetHashCode(GroupId));
        }

        private sealed class ResolvedRigDescriptor
        {
            public required string AnimatorId { get; init; }
            public required string AvatarId { get; init; }
            public required string MeshId { get; init; }
            public required string GroupId { get; init; }
            public required long AvatarSourceFileId { get; init; }
            public required long MeshSourceFileId { get; init; }
            public required int StaticBonus { get; init; }
            public required bool IsValidTarget { get; init; }
        }

        private readonly struct StagedClipMatchRow
        {
            public readonly string ClipId;
            public readonly string AnimatorId;
            public readonly string AvatarId;
            public readonly string MeshId;
            public readonly string GroupId;
            public readonly int Score;
            public readonly int MatchedProbes;
            public readonly int TotalClipBones;

            public StagedClipMatchRow(
                string clipId,
                string animatorId,
                string avatarId,
                string meshId,
                string groupId,
                int score,
                int matchedProbes,
                int totalClipBones)
            {
                ClipId = clipId;
                AnimatorId = animatorId;
                AvatarId = avatarId;
                MeshId = meshId;
                GroupId = groupId;
                Score = score;
                MatchedProbes = matchedProbes;
                TotalClipBones = totalClipBones;
            }
        }

        private static void SetCacheSizeKiB(SqliteConnection conn, int cacheSizeKiB)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA cache_size = {cacheSizeKiB};";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Ignore pragma errors
            }
        }

        private static bool PromoteStagedBoneHashMatchesInMemory(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            out bool hadStagedHashes)
        {
            hadStagedHashes = false;

            // 1) Load staged rig bone hashes into compact integer rig IDs and inverted hash -> rigIds map.
            var rigKeyToIndex = new Dictionary<StagedRigKey, int>();
            var rawRigs = new List<StagedRigKey>();
            var hashToRigSet = new Dictionary<uint, HashSet<int>>();
            var neededAvatarIds = new HashSet<string>(StringComparer.Ordinal);
            var neededGroupIds = new HashSet<string>(StringComparer.Ordinal);
            var neededAssetIdsForSourceFile = new HashSet<string>(StringComparer.Ordinal);

            using (var rigCmd = conn.CreateCommand())
            {
                rigCmd.Transaction = transaction;
                rigCmd.CommandText = @"
                    SELECT AvatarAssetUniqueID, AnimatorAssetUniqueID, MeshAssetUniqueID, ModelGroupId, BoneHash
                    FROM Stage_RigBoneHashes
                    WHERE ProjectId = @projectId;";
                rigCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = rigCmd.ExecuteReader();
                while (reader.Read())
                {
                    var avatarId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var animatorId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var meshId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var groupId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var boneHash = unchecked((uint)reader.GetInt64(4));

                    var key = new StagedRigKey(avatarId, animatorId, meshId, groupId);
                    if (!rigKeyToIndex.TryGetValue(key, out var rigIdx))
                    {
                        rigIdx = rawRigs.Count;
                        rigKeyToIndex[key] = rigIdx;
                        rawRigs.Add(key);
                        if (!string.IsNullOrEmpty(avatarId))
                        {
                            neededAvatarIds.Add(avatarId);
                            neededAssetIdsForSourceFile.Add(avatarId);
                        }
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            neededGroupIds.Add(groupId);
                        }
                        if (!string.IsNullOrEmpty(meshId))
                        {
                            neededAssetIdsForSourceFile.Add(meshId);
                        }
                    }

                    if (!hashToRigSet.TryGetValue(boneHash, out var rigSet))
                    {
                        rigSet = new HashSet<int>();
                        hashToRigSet[boneHash] = rigSet;
                    }
                    rigSet.Add(rigIdx);
                }
            }

            if (rawRigs.Count == 0)
            {
                return false;
            }

            // 2) Load staged clip bone hashes grouped by ClipAssetUniqueID.
            var clipProbesMap = new Dictionary<string, (HashSet<uint> Probes, int TotalClipBones)>(StringComparer.Ordinal);
            using (var clipCmd = conn.CreateCommand())
            {
                clipCmd.Transaction = transaction;
                clipCmd.CommandText = @"
                    SELECT ClipAssetUniqueID, BoneHash, TotalClipBoneCount
                    FROM Stage_ClipBoneHashes
                    WHERE ProjectId = @projectId;";
                clipCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = clipCmd.ExecuteReader();
                while (reader.Read())
                {
                    var clipId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrEmpty(clipId))
                    {
                        continue;
                    }

                    var boneHash = unchecked((uint)reader.GetInt64(1));
                    var totalClipBones = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    if (!clipProbesMap.TryGetValue(clipId, out var entry))
                    {
                        entry = (new HashSet<uint>(), totalClipBones);
                    }
                    else if (totalClipBones > entry.TotalClipBones)
                    {
                        entry = (entry.Probes, totalClipBones);
                    }

                    entry.Probes.Add(boneHash);
                    clipProbesMap[clipId] = entry;
                    neededAssetIdsForSourceFile.Add(clipId);
                }
            }

            if (clipProbesMap.Count == 0)
            {
                return false;
            }

            hadStagedHashes = true;

            // 3) Preload small enrichment lookup dictionaries (Avatar -> Group/Animator, Group -> RepMesh, Avatar -> FirstMesh).
            var avatarFirstGroup = new Dictionary<string, (string GroupId, string AnimatorId)>(StringComparer.Ordinal);
            if (neededAvatarIds.Count > 0)
            {
                using var afgCmd = conn.CreateCommand();
                afgCmd.Transaction = transaction;
                afgCmd.CommandText = @"
                    SELECT AvatarAssetUniqueID, GroupAssetUniqueID, AnimatorAssetUniqueID
                    FROM ModelGroups
                    WHERE ProjectId = @projectId
                      AND AvatarAssetUniqueID <> ''
                    ORDER BY Confidence DESC, GroupAssetUniqueID ASC;";
                afgCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = afgCmd.ExecuteReader();
                while (reader.Read())
                {
                    var avatarId = reader.GetString(0);
                    if (neededAvatarIds.Contains(avatarId) && !avatarFirstGroup.ContainsKey(avatarId))
                    {
                        var groupId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        var animatorId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                        avatarFirstGroup[avatarId] = (groupId, animatorId);
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            neededGroupIds.Add(groupId);
                        }
                    }
                }
            }

            var groupRepMesh = new Dictionary<string, string>(StringComparer.Ordinal);
            if (neededGroupIds.Count > 0)
            {
                var bestRepByGroup = new Dictionary<string, (string MeshId, int Confidence, long ByteSize, int SlotIndex)>(StringComparer.Ordinal);
                using var repCmd = conn.CreateCommand();
                repCmd.Transaction = transaction;
                repCmd.CommandText = @"
                    SELECT
                        mgm.GroupAssetUniqueID,
                        mgm.MeshAssetUniqueID,
                        mgm.Confidence,
                        COALESCE(meshAsset.ByteSize, 0),
                        mgm.SlotIndex
                    FROM ModelGroupMeshes mgm
                    LEFT JOIN Assets meshAsset
                      ON meshAsset.ProjectId = mgm.ProjectId
                     AND meshAsset.UniqueID = mgm.MeshAssetUniqueID
                    WHERE mgm.ProjectId = @projectId
                      AND mgm.MeshAssetUniqueID <> '';";
                repCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = repCmd.ExecuteReader();
                while (reader.Read())
                {
                    var groupId = reader.GetString(0);
                    if (!neededGroupIds.Contains(groupId))
                    {
                        continue;
                    }

                    var meshId = reader.GetString(1);
                    var confidence = reader.GetInt32(2);
                    var byteSize = reader.GetInt64(3);
                    var slotIndex = reader.GetInt32(4);

                    if (!bestRepByGroup.TryGetValue(groupId, out var current)
                        || confidence > current.Confidence
                        || (confidence == current.Confidence && byteSize > current.ByteSize)
                        || (confidence == current.Confidence && byteSize == current.ByteSize && slotIndex < current.SlotIndex)
                        || (confidence == current.Confidence && byteSize == current.ByteSize && slotIndex == current.SlotIndex
                            && string.Compare(meshId, current.MeshId, StringComparison.Ordinal) < 0))
                    {
                        bestRepByGroup[groupId] = (meshId, confidence, byteSize, slotIndex);
                    }
                }

                foreach (var kv in bestRepByGroup)
                {
                    groupRepMesh[kv.Key] = kv.Value.MeshId;
                    neededAssetIdsForSourceFile.Add(kv.Value.MeshId);
                }
            }

            var avatarFirstMesh = new Dictionary<string, string>(StringComparer.Ordinal);
            if (neededAvatarIds.Count > 0)
            {
                using var afmCmd = conn.CreateCommand();
                afmCmd.Transaction = transaction;
                afmCmd.CommandText = @"
                    SELECT SourceAssetUniqueID, TargetAssetUniqueID
                    FROM AssetEdges
                    WHERE ProjectId = @projectId
                      AND EdgeKind = 'Mesh'
                      AND SlotName IN ('AnimatorMesh', 'AvatarMesh')
                      AND SourceAssetUniqueID <> ''
                      AND TargetAssetUniqueID <> ''
                    ORDER BY SlotIndex ASC, TargetAssetUniqueID ASC;";
                afmCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = afmCmd.ExecuteReader();
                while (reader.Read())
                {
                    var avatarId = reader.GetString(0);
                    if (neededAvatarIds.Contains(avatarId) && !avatarFirstMesh.ContainsKey(avatarId))
                    {
                        var meshId = reader.GetString(1);
                        avatarFirstMesh[avatarId] = meshId;
                        neededAssetIdsForSourceFile.Add(meshId);
                    }
                }
            }

            // Load SourceFileId only for the distinct assets participating in bone-hash matching.
            var sourceFileByAssetId = new Dictionary<string, long>(neededAssetIdsForSourceFile.Count, StringComparer.Ordinal);
            using (var sfCmd = conn.CreateCommand())
            {
                sfCmd.Transaction = transaction;
                sfCmd.CommandText = @"
                    SELECT UniqueID, SourceFileId
                    FROM Assets
                    WHERE ProjectId = @projectId;";
                sfCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = sfCmd.ExecuteReader();
                while (reader.Read())
                {
                    var uid = reader.GetString(0);
                    if (neededAssetIdsForSourceFile.Contains(uid) && !reader.IsDBNull(1))
                    {
                        sourceFileByAssetId[uid] = reader.GetInt64(1);
                    }
                }
            }

            // 4) Pre-resolve every rig descriptor once (O(rigCount) instead of O(clips * rigs)).
            var resolvedRigs = new ResolvedRigDescriptor[rawRigs.Count];
            for (var i = 0; i < rawRigs.Count; i++)
            {
                var raw = rawRigs[i];
                (string GroupId, string AnimatorId) afg = (string.Empty, string.Empty);
                var hasAfg = !string.IsNullOrEmpty(raw.AvatarId) && avatarFirstGroup.TryGetValue(raw.AvatarId, out afg);
                var animatorId = !string.IsNullOrEmpty(raw.AnimatorId)
                    ? raw.AnimatorId
                    : hasAfg ? afg.AnimatorId : string.Empty;
                var groupId = !string.IsNullOrEmpty(raw.GroupId)
                    ? raw.GroupId
                    : hasAfg ? afg.GroupId : string.Empty;
                var meshId = !string.IsNullOrEmpty(raw.MeshId)
                    ? raw.MeshId
                    : !string.IsNullOrEmpty(groupId) && groupRepMesh.TryGetValue(groupId, out var repMesh)
                        ? repMesh
                        : !string.IsNullOrEmpty(raw.AvatarId) && avatarFirstMesh.TryGetValue(raw.AvatarId, out var firstMesh)
                            ? firstMesh
                            : string.Empty;

                var avatarSf = !string.IsNullOrEmpty(raw.AvatarId) && sourceFileByAssetId.TryGetValue(raw.AvatarId, out var asf) ? asf : -1L;
                var meshSf = !string.IsNullOrEmpty(meshId) && sourceFileByAssetId.TryGetValue(meshId, out var msf) ? msf : -1L;
                var staticBonus = (!string.IsNullOrEmpty(groupId) ? 60 : 0) + (!string.IsNullOrEmpty(meshId) ? 40 : 0);

                resolvedRigs[i] = new ResolvedRigDescriptor
                {
                    AnimatorId = animatorId,
                    AvatarId = raw.AvatarId,
                    MeshId = meshId,
                    GroupId = groupId,
                    AvatarSourceFileId = avatarSf,
                    MeshSourceFileId = meshSf,
                    StaticBonus = staticBonus,
                    IsValidTarget = !string.IsNullOrEmpty(raw.AvatarId) || !string.IsNullOrEmpty(meshId) || !string.IsNullOrEmpty(groupId)
                };
            }

            // Flatten inverted hash index into compact int[] arrays for lock-free parallel reads.
            var rigsByHash = new Dictionary<uint, int[]>(hashToRigSet.Count);
            foreach (var kv in hashToRigSet)
            {
                var arr = new int[kv.Value.Count];
                kv.Value.CopyTo(arr);
                rigsByHash[kv.Key] = arr;
            }
            hashToRigSet.Clear();

            var clips = new (string ClipId, uint[] Probes, int TotalClipBones, long SourceFileId)[clipProbesMap.Count];
            var clipIndex = 0;
            foreach (var kv in clipProbesMap)
            {
                var probeArr = new uint[kv.Value.Probes.Count];
                kv.Value.Probes.CopyTo(probeArr);
                var totalBones = kv.Value.TotalClipBones > 0 ? kv.Value.TotalClipBones : probeArr.Length;
                var clipSf = sourceFileByAssetId.TryGetValue(kv.Key, out var csf) ? csf : -1L;
                clips[clipIndex++] = (kv.Key, probeArr, totalBones, clipSf);
            }
            clipProbesMap.Clear();

            // 5) Parallel bone-hash counting per clip using O(touched) thread-local L1 scratch arrays.
            int rigCount = resolvedRigs.Length;
            var perClipWinners = new List<StagedClipMatchRow>?[clips.Length];
            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 12))
            };

            System.Threading.Tasks.Parallel.For(
                0,
                clips.Length,
                parallelOptions,
                () => (Counts: new int[rigCount], Touched: new int[rigCount]),
                (ci, _, scratch) =>
                {
                    var clip = clips[ci];
                    var probes = clip.Probes;
                    int probeCount = probes.Length;
                    if (probeCount == 0)
                    {
                        return scratch;
                    }

                    var counts = scratch.Counts;
                    var touched = scratch.Touched;
                    int touchedCount = 0;

                    for (int p = 0; p < probeCount; p++)
                    {
                        if (!rigsByHash.TryGetValue(probes[p], out var matchingRigs))
                        {
                            continue;
                        }

                        for (int r = 0; r < matchingRigs.Length; r++)
                        {
                            int rigId = matchingRigs[r];
                            if (counts[rigId] == 0)
                            {
                                touched[touchedCount++] = rigId;
                            }
                            counts[rigId]++;
                        }
                    }

                    if (touchedCount == 0)
                    {
                        return scratch;
                    }

                    int minRequired = probeCount <= 2
                        ? probeCount
                        : probeCount <= 5
                            ? probeCount - 1
                            : Math.Max(3, probeCount / 4);

                    Dictionary<StagedRigKey, (int BestScore, int BestMatched)>? deduped = null;
                    for (int t = 0; t < touchedCount; t++)
                    {
                        int rigId = touched[t];
                        int matched = counts[rigId];
                        counts[rigId] = 0;

                        if (matched < minRequired)
                        {
                            continue;
                        }

                        var rig = resolvedRigs[rigId];
                        if (!rig.IsValidTarget)
                        {
                            continue;
                        }

                        int sameFileBonus = (clip.SourceFileId >= 0
                            && (clip.SourceFileId == rig.AvatarSourceFileId || clip.SourceFileId == rig.MeshSourceFileId))
                            ? 50
                            : 0;
                        int score = ((matched * 1000) / Math.Max(1, probeCount))
                            + (matched * 100)
                            + rig.StaticBonus
                            + sameFileBonus;

                        deduped ??= new Dictionary<StagedRigKey, (int, int)>();
                        var resolvedKey = new StagedRigKey(rig.AvatarId, rig.AnimatorId, rig.MeshId, rig.GroupId);
                        if (deduped.TryGetValue(resolvedKey, out var prev))
                        {
                            deduped[resolvedKey] = (Math.Max(prev.BestScore, score), Math.Max(prev.BestMatched, matched));
                        }
                        else
                        {
                            deduped[resolvedKey] = (score, matched);
                        }
                    }

                    if (deduped == null || deduped.Count == 0)
                    {
                        return scratch;
                    }

                    var rows = new List<StagedClipMatchRow>(deduped.Count);
                    foreach (var kv in deduped)
                    {
                        rows.Add(new StagedClipMatchRow(
                            clip.ClipId,
                            kv.Key.AnimatorId,
                            kv.Key.AvatarId,
                            kv.Key.MeshId,
                            kv.Key.GroupId,
                            kv.Value.BestScore,
                            kv.Value.BestMatched,
                            clip.TotalClipBones));
                    }

                    rows.Sort(static (a, b) =>
                    {
                        int cmp = b.Score.CompareTo(a.Score);
                        if (cmp != 0) return cmp;
                        cmp = b.MatchedProbes.CompareTo(a.MatchedProbes);
                        if (cmp != 0) return cmp;
                        cmp = string.Compare(a.AvatarId, b.AvatarId, StringComparison.Ordinal);
                        if (cmp != 0) return cmp;
                        cmp = string.Compare(a.MeshId, b.MeshId, StringComparison.Ordinal);
                        if (cmp != 0) return cmp;
                        return string.Compare(a.GroupId, b.GroupId, StringComparison.Ordinal);
                    });

                    if (rows.Count > 16)
                    {
                        rows.RemoveRange(16, rows.Count - 16);
                    }

                    perClipWinners[ci] = rows;
                    return scratch;
                },
                _ => { });

            // 6) Bulk upsert winning BoneHashMatch rows into AnimationClipTargets inside the active transaction.
            bool promotedAnyAvatarMatch = false;
            using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO AnimationClipTargets (
                        ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                        MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                    VALUES (
                        @projectId, @clipId, @animatorId, @avatarId,
                        @meshId, @groupId, 'BoneHashMatch', @score, @matched, @total)
                    ON CONFLICT(ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                    DO UPDATE SET
                        MatchScore = MAX(AnimationClipTargets.MatchScore, excluded.MatchScore),
                        MatchedBoneCount = MAX(AnimationClipTargets.MatchedBoneCount, excluded.MatchedBoneCount),
                        TotalClipBoneCount = MAX(AnimationClipTargets.TotalClipBoneCount, excluded.TotalClipBoneCount);";
                var pProjectId = insertCmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pClipId = insertCmd.Parameters.Add("@clipId", SqliteType.Text);
                var pAnimatorId = insertCmd.Parameters.Add("@animatorId", SqliteType.Text);
                var pAvatarId = insertCmd.Parameters.Add("@avatarId", SqliteType.Text);
                var pMeshId = insertCmd.Parameters.Add("@meshId", SqliteType.Text);
                var pGroupId = insertCmd.Parameters.Add("@groupId", SqliteType.Text);
                var pScore = insertCmd.Parameters.Add("@score", SqliteType.Integer);
                var pMatched = insertCmd.Parameters.Add("@matched", SqliteType.Integer);
                var pTotal = insertCmd.Parameters.Add("@total", SqliteType.Integer);
                pProjectId.Value = projectId;
                insertCmd.Prepare();

                for (int ci = 0; ci < perClipWinners.Length; ci++)
                {
                    var rows = perClipWinners[ci];
                    if (rows == null)
                    {
                        continue;
                    }

                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        if (!string.IsNullOrEmpty(row.AvatarId))
                        {
                            promotedAnyAvatarMatch = true;
                        }

                        pClipId.Value = row.ClipId;
                        pAnimatorId.Value = row.AnimatorId;
                        pAvatarId.Value = row.AvatarId;
                        pMeshId.Value = row.MeshId;
                        pGroupId.Value = row.GroupId;
                        pScore.Value = row.Score;
                        pMatched.Value = row.MatchedProbes;
                        pTotal.Value = row.TotalClipBones;
                        insertCmd.ExecuteNonQuery();
                    }
                }
            }

            return promotedAnyAvatarMatch;
        }

        // --- Non-blocking Lazy Semantic Backfill & Background Writer ---

        internal void EnqueueLazySemanticUpsert(string folderPath, string signature, SemanticAssetRelations? delta)
        {
            if (Volatile.Read(ref _disposed) != 0
                || delta == null
                || !delta.HasRelations
                || string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature))
            {
                return;
            }

            var key = $"{NormalizeProjectPathForCache(folderPath)}\u001f{signature}";
            lock (_bgWriteLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (_pendingLazySemanticWritesByKey.TryGetValue(key, out var existing))
                {
                    existing.Delta.Merge(delta);
                }
                else
                {
                    while (_pendingLazySemanticWrites.Count >= MaxPendingLazySemanticWrites)
                    {
                        var dropped = _pendingLazySemanticWrites.Dequeue();
                        _pendingLazySemanticWritesByKey.Remove(dropped.Key);
                    }

                    var merged = new SemanticAssetRelations();
                    merged.Merge(delta);
                    var entry = new PendingLazySemanticWrite(key, folderPath, signature, merged);
                    _pendingLazySemanticWrites.Enqueue(entry);
                    _pendingLazySemanticWritesByKey[key] = entry;
                }

                EnsureBackgroundWriterRunningLocked();
            }
        }

        internal bool TryUpsertMaterialAndMeshCache(
            string folderPath,
            string signature,
            SemanticAssetRelations? delta,
            bool waitForLock = true)
        {
            if (delta == null
                || !delta.HasRelations
                || string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var lockTaken = false;
            try
            {
                if (waitForLock)
                {
                    Monitor.Enter(WriteGate, ref lockTaken);
                }
                else
                {
                    Monitor.TryEnter(WriteGate, TimeSpan.Zero, ref lockTaken);
                    if (!lockTaken)
                    {
                        return false;
                    }
                }

                using var conn = CreateConnectionByDbPath(dbPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return true;
                }

                var pid = projectId.Value;
                if (delta.SourceFiles.Count > 0)
                {
                    InsertSemanticSourceFiles(conn, transaction, pid, delta.SourceFiles);
                }

                if (delta.AssetEdges.Count > 0)
                {
                    InsertAssetEdges(conn, transaction, pid, delta.AssetEdges, 0, delta.AssetEdges.Count, new Dictionary<string, long>(StringComparer.Ordinal));
                }

                if (delta.MeshRenderers.Count > 0)
                {
                    InsertMeshRenderers(conn, transaction, pid, delta.MeshRenderers, 0, delta.MeshRenderers.Count);
                }

                if (delta.MeshMaterials.Count > 0)
                {
                    InsertMeshMaterials(conn, transaction, pid, delta.MeshMaterials, 0, delta.MeshMaterials.Count);
                }

                if (delta.MaterialTextures.Count > 0)
                {
                    InsertMaterialTextures(conn, transaction, pid, delta.MaterialTextures, 0, delta.MaterialTextures.Count);
                }

                if (delta.MaterialCaches.Count > 0)
                {
                    InsertMaterialCaches(conn, transaction, pid, delta.MaterialCaches, 0, delta.MaterialCaches.Count);
                }

                if (delta.CompositeAssets.Count > 0)
                {
                    InsertCompositeAssets(conn, transaction, pid, delta.CompositeAssets, 0, delta.CompositeAssets.Count);
                }

                if (delta.CompositeAssetParts.Count > 0)
                {
                    InsertCompositeAssetParts(conn, transaction, pid, delta.CompositeAssetParts, 0, delta.CompositeAssetParts.Count);
                }

                if (delta.AnimationClipTargets.Count > 0)
                {
                    UpsertDirectAnimationClipTargets(conn, transaction, pid, delta.AnimationClipTargets);
                }

                // Refresh MeshMaterialResolvedCache ONLY for affected meshes and propagate newly resolved MaterialCache
                // entries into any already-cached meshes via idx_mesh_mat_resolved_material (O(1) indexed update!).
                var affectedMeshIds = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < delta.MeshMaterials.Count; i++)
                {
                    var mid = delta.MeshMaterials[i].MeshAssetId;
                    if (!string.IsNullOrEmpty(mid))
                    {
                        affectedMeshIds.Add(mid);
                    }
                }

                var affectedMaterialIds = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < delta.MaterialCaches.Count; i++)
                {
                    var matId = delta.MaterialCaches[i].MaterialAssetId;
                    if (!string.IsNullOrEmpty(matId))
                    {
                        affectedMaterialIds.Add(matId);
                    }
                }
                for (var i = 0; i < delta.MaterialTextures.Count; i++)
                {
                    var matId = delta.MaterialTextures[i].MaterialAssetId;
                    if (!string.IsNullOrEmpty(matId))
                    {
                        affectedMaterialIds.Add(matId);
                    }
                }

                if (affectedMaterialIds.Count > 0)
                {
                    RefreshMaterialCacheFromTexturesForMaterials(conn, transaction, pid, affectedMaterialIds);
                    PropagateMaterialCacheToResolvedMeshes(conn, transaction, pid, affectedMaterialIds);
                }

                if (affectedMeshIds.Count > 0)
                {
                    RefreshResolvedMeshMaterialCacheForMeshes(conn, transaction, pid, affectedMeshIds);
                }

                transaction.Commit();
                NotifyDatabaseMutated(dbPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to lazily upsert semantic cache entries: {ex.Message}");
                return true;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(WriteGate);
                }
            }
        }

        private static void UpsertDirectAnimationClipTargets(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<SemanticAnimationClipTargetRelation> targets)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO AnimationClipTargets (
                    ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                    MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                VALUES (
                    @projectId, @clipId, @animatorId, @avatarId,
                    @meshId, @groupId, @relationKind, @matchScore, @matchedBones, @totalClipBones)
                ON CONFLICT(ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                DO UPDATE SET
                    MatchScore = MAX(AnimationClipTargets.MatchScore, excluded.MatchScore),
                    MatchedBoneCount = MAX(AnimationClipTargets.MatchedBoneCount, excluded.MatchedBoneCount),
                    TotalClipBoneCount = MAX(AnimationClipTargets.TotalClipBoneCount, excluded.TotalClipBoneCount);";

            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pClipId = cmd.Parameters.Add("@clipId", SqliteType.Text);
            var pAnimatorId = cmd.Parameters.Add("@animatorId", SqliteType.Text);
            var pAvatarId = cmd.Parameters.Add("@avatarId", SqliteType.Text);
            var pMeshId = cmd.Parameters.Add("@meshId", SqliteType.Text);
            var pGroupId = cmd.Parameters.Add("@groupId", SqliteType.Text);
            var pRelationKind = cmd.Parameters.Add("@relationKind", SqliteType.Text);
            var pMatchScore = cmd.Parameters.Add("@matchScore", SqliteType.Integer);
            var pMatchedBones = cmd.Parameters.Add("@matchedBones", SqliteType.Integer);
            var pTotalClipBones = cmd.Parameters.Add("@totalClipBones", SqliteType.Integer);

            pProjectId.Value = projectId;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (string.IsNullOrEmpty(target.ClipAssetId)
                    || (string.IsNullOrEmpty(target.AvatarAssetId)
                        && string.IsNullOrEmpty(target.MeshAssetId)
                        && string.IsNullOrEmpty(target.ModelGroupId)))
                {
                    continue;
                }

                pClipId.Value = target.ClipAssetId;
                pAnimatorId.Value = target.AnimatorAssetId;
                pAvatarId.Value = target.AvatarAssetId;
                pMeshId.Value = target.MeshAssetId;
                pGroupId.Value = target.ModelGroupId;
                pRelationKind.Value = target.RelationKind;
                pMatchScore.Value = target.MatchScore;
                pMatchedBones.Value = target.MatchedBoneCount;
                pTotalClipBones.Value = target.TotalClipBoneCount;
                cmd.ExecuteNonQuery();
            }
        }

        private static void RefreshMaterialCacheFromTexturesForMaterials(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IEnumerable<string> materialIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                WITH RankedMainTex AS (
                    SELECT
                        mt.TextureAssetUniqueID,
                        mt.SlotName,
                        mt.PreviewMaterialAssetUniqueID,
                        mt.ResolutionSource,
                        ROW_NUMBER() OVER (
                            ORDER BY
                                mt.IsMainTexture DESC,
                                CASE mt.SlotName
                                    WHEN '_BaseMap' THEN 0
                                    WHEN '_MainTex' THEN 1
                                    WHEN 'texture' THEN 2
                                    WHEN 'Texture' THEN 3
                                    WHEN '_Texture' THEN 4
                                    WHEN '_BaseColorMap' THEN 5
                                    WHEN '_BaseColorTexture' THEN 6
                                    WHEN '_Diffuse' THEN 7
                                    WHEN '_AlbedoMap' THEN 8
                                    WHEN '_Albedo' THEN 9
                                    ELSE 20
                                END,
                                mt.SlotIndex ASC,
                                mt.TextureAssetUniqueID ASC
                        ) AS RowNum
                    FROM MaterialTextures mt
                    WHERE mt.ProjectId = @projectId
                      AND mt.MaterialAssetUniqueID = @materialId
                      AND mt.TextureAssetUniqueID <> ''
                )
                INSERT INTO MaterialCache (
                    ProjectId, MaterialAssetUniqueID, MaterialName, ParentMaterialAssetUniqueID,
                    MainTextureAssetUniqueID, MainTextureSlotName,
                    BaseColorR, BaseColorG, BaseColorB, BaseColorA,
                    ShaderName, TextureSlotCount, ResolutionSource)
                SELECT
                    @projectId,
                    @materialId,
                    COALESCE((SELECT Name FROM Assets WHERE ProjectId = @projectId AND UniqueID = @materialId LIMIT 1), ''),
                    CASE WHEN COALESCE(rmt.PreviewMaterialAssetUniqueID, '') <> '' AND rmt.PreviewMaterialAssetUniqueID <> @materialId
                         THEN rmt.PreviewMaterialAssetUniqueID ELSE '' END,
                    COALESCE(rmt.TextureAssetUniqueID, ''),
                    COALESCE(rmt.SlotName, ''),
                    1.0, 1.0, 1.0, 1.0,
                    '',
                    (SELECT COUNT(*) FROM MaterialTextures WHERE ProjectId = @projectId AND MaterialAssetUniqueID = @materialId),
                    COALESCE(NULLIF(rmt.ResolutionSource, ''), 'lazy_backfill')
                FROM RankedMainTex rmt
                WHERE rmt.RowNum = 1
                ON CONFLICT(ProjectId, MaterialAssetUniqueID)
                DO UPDATE SET
                    MaterialName = CASE WHEN MaterialCache.MaterialName = '' THEN excluded.MaterialName ELSE MaterialCache.MaterialName END,
                    ParentMaterialAssetUniqueID = CASE WHEN MaterialCache.ParentMaterialAssetUniqueID = '' THEN excluded.ParentMaterialAssetUniqueID ELSE MaterialCache.ParentMaterialAssetUniqueID END,
                    MainTextureAssetUniqueID = CASE WHEN excluded.MainTextureAssetUniqueID <> '' THEN excluded.MainTextureAssetUniqueID ELSE MaterialCache.MainTextureAssetUniqueID END,
                    MainTextureSlotName = CASE WHEN excluded.MainTextureSlotName <> '' THEN excluded.MainTextureSlotName ELSE MaterialCache.MainTextureSlotName END,
                    TextureSlotCount = MAX(MaterialCache.TextureSlotCount, excluded.TextureSlotCount);";
            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMaterialId = cmd.Parameters.Add("@materialId", SqliteType.Text);
            pProjectId.Value = projectId;

            foreach (var materialId in materialIds)
            {
                pMaterialId.Value = materialId;
                cmd.ExecuteNonQuery();
            }
        }

        private static void PropagateMaterialCacheToResolvedMeshes(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IEnumerable<string> materialIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                UPDATE MeshMaterialResolvedCache
                SET
                    MaterialName = COALESCE(NULLIF((SELECT mc.MaterialName FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), ''), MaterialName),
                    MainTextureAssetUniqueID = COALESCE(NULLIF((SELECT mc.MainTextureAssetUniqueID FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), ''), MainTextureAssetUniqueID),
                    MainTextureSlotName = COALESCE(NULLIF((SELECT mc.MainTextureSlotName FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), ''), MainTextureSlotName),
                    BaseColorR = COALESCE((SELECT mc.BaseColorR FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), BaseColorR),
                    BaseColorG = COALESCE((SELECT mc.BaseColorG FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), BaseColorG),
                    BaseColorB = COALESCE((SELECT mc.BaseColorB FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), BaseColorB),
                    BaseColorA = COALESCE((SELECT mc.BaseColorA FROM MaterialCache mc WHERE mc.ProjectId = @projectId AND mc.MaterialAssetUniqueID = @materialId LIMIT 1), BaseColorA)
                WHERE ProjectId = @projectId
                  AND MaterialAssetUniqueID = @materialId;";
            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMaterialId = cmd.Parameters.Add("@materialId", SqliteType.Text);
            pProjectId.Value = projectId;

            foreach (var materialId in materialIds)
            {
                pMaterialId.Value = materialId;
                cmd.ExecuteNonQuery();
            }
        }

        private static void RefreshResolvedMeshMaterialCacheForMeshes(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IEnumerable<string> meshIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                WITH MeshSource AS (
                    SELECT SourceFileId
                    FROM Assets
                    WHERE ProjectId = @projectId
                      AND UniqueID = @meshAssetId
                    LIMIT 1
                ),
                CandidateMaterials AS (
                    SELECT DISTINCT MaterialAssetUniqueID
                    FROM MeshMaterials
                    WHERE ProjectId = @projectId
                      AND MeshAssetUniqueID = @meshAssetId
                      AND MaterialAssetUniqueID <> ''
                ),
                MaterialStats AS (
                    SELECT
                        c.MaterialAssetUniqueID,
                        MAX(CASE WHEN (mt.TextureAssetUniqueID <> '' AND mt.IsMainTexture <> 0) OR COALESCE(mc.MainTextureAssetUniqueID, '') <> '' THEN 1 ELSE 0 END) AS HasMainTexture,
                        MAX(CASE WHEN mt.TextureAssetUniqueID <> '' OR COALESCE(mc.MainTextureAssetUniqueID, '') <> '' THEN 1 ELSE 0 END) AS HasTexture
                    FROM CandidateMaterials c
                    LEFT JOIN MaterialTextures mt
                      ON mt.ProjectId = @projectId
                     AND mt.MaterialAssetUniqueID = c.MaterialAssetUniqueID
                    LEFT JOIN MaterialCache mc
                      ON mc.ProjectId = @projectId
                     AND mc.MaterialAssetUniqueID = c.MaterialAssetUniqueID
                    GROUP BY c.MaterialAssetUniqueID
                ),
                RendererStats AS (
                    SELECT
                        mm.RendererAssetUniqueID,
                        MAX(CASE WHEN mm.RendererType <> 'Container' THEN 1 ELSE 0 END) AS RendererScore,
                        SUM(CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 1 ELSE 0 END) AS MaterialCount,
                        COUNT(*) AS RelationCount,
                        MAX(mm.MaterialScore) AS MaterialScore,
                        MAX(CASE WHEN materialAsset.SourceFileId IS NOT NULL AND materialAsset.SourceFileId = (SELECT SourceFileId FROM MeshSource) THEN 1 ELSE 0 END) AS SameSourceScore,
                        SUM(COALESCE(ms.HasMainTexture, 0)) AS MainTextureCount,
                        SUM(COALESCE(ms.HasTexture, 0)) AS TextureCount
                    FROM MeshMaterials mm
                    LEFT JOIN Assets materialAsset
                      ON materialAsset.ProjectId = mm.ProjectId
                     AND materialAsset.UniqueID = mm.MaterialAssetUniqueID
                    LEFT JOIN MaterialStats ms
                      ON ms.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                    WHERE mm.ProjectId = @projectId
                      AND mm.MeshAssetUniqueID = @meshAssetId
                    GROUP BY mm.RendererAssetUniqueID
                ),
                Ranked AS (
                    SELECT
                        mm.MeshAssetUniqueID,
                        mm.SubMeshIndex,
                        mm.MaterialAssetUniqueID,
                        mm.RendererAssetUniqueID,
                        mm.RendererType,
                        COALESCE(NULLIF(mc.MaterialName, ''), matAsset.Name, '') AS MaterialName,
                        COALESCE(mc.MainTextureAssetUniqueID, '') AS MainTextureAssetUniqueID,
                        COALESCE(mc.MainTextureSlotName, '') AS MainTextureSlotName,
                        COALESCE(mc.BaseColorR, 1.0) AS BaseColorR,
                        COALESCE(mc.BaseColorG, 1.0) AS BaseColorG,
                        COALESCE(mc.BaseColorB, 1.0) AS BaseColorB,
                        COALESCE(mc.BaseColorA, 1.0) AS BaseColorA,
                        COALESCE(NULLIF(mc.ResolutionSource, ''), 'lazy_backfill') AS ResolutionSource,
                        ROW_NUMBER() OVER (
                            PARTITION BY mm.SubMeshIndex
                            ORDER BY
                                CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 0 ELSE 1 END,
                                CASE WHEN COALESCE(ms.HasMainTexture, 0) <> 0 THEN 0 ELSE 1 END,
                                CASE WHEN COALESCE(ms.HasTexture, 0) <> 0 THEN 0 ELSE 1 END,
                                COALESCE(rs.RendererScore, 0) DESC,
                                COALESCE(rs.SameSourceScore, 0) DESC,
                                COALESCE(rs.MainTextureCount, 0) DESC,
                                COALESCE(rs.TextureCount, 0) DESC,
                                mm.MaterialScore DESC,
                                COALESCE(rs.MaterialCount, 0) DESC,
                                COALESCE(rs.RelationCount, 0) DESC,
                                mm.MaterialSlotIndex,
                                mm.RendererAssetUniqueID,
                                mm.MaterialAssetUniqueID
                        ) AS RowNum
                    FROM MeshMaterials mm
                    LEFT JOIN RendererStats rs
                      ON rs.RendererAssetUniqueID = mm.RendererAssetUniqueID
                    LEFT JOIN MaterialStats ms
                      ON ms.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                    LEFT JOIN MaterialCache mc
                      ON mc.ProjectId = mm.ProjectId
                     AND mc.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                    LEFT JOIN Assets matAsset
                      ON matAsset.ProjectId = mm.ProjectId
                     AND mm.MaterialAssetUniqueID <> ''
                     AND matAsset.UniqueID = mm.MaterialAssetUniqueID
                    WHERE mm.ProjectId = @projectId
                      AND mm.MeshAssetUniqueID = @meshAssetId
                )
                INSERT INTO MeshMaterialResolvedCache (
                    ProjectId, MeshAssetUniqueID, SubMeshIndex, MaterialAssetUniqueID,
                    RendererAssetUniqueID, RendererType, MaterialName,
                    MainTextureAssetUniqueID, MainTextureSlotName,
                    BaseColorR, BaseColorG, BaseColorB, BaseColorA, ResolutionSource)
                SELECT
                    @projectId,
                    r.MeshAssetUniqueID,
                    r.SubMeshIndex,
                    r.MaterialAssetUniqueID,
                    r.RendererAssetUniqueID,
                    r.RendererType,
                    r.MaterialName,
                    r.MainTextureAssetUniqueID,
                    r.MainTextureSlotName,
                    r.BaseColorR,
                    r.BaseColorG,
                    r.BaseColorB,
                    r.BaseColorA,
                    r.ResolutionSource
                FROM Ranked r
                WHERE r.RowNum = 1
                ON CONFLICT(ProjectId, MeshAssetUniqueID, SubMeshIndex)
                DO UPDATE SET
                    MaterialAssetUniqueID = excluded.MaterialAssetUniqueID,
                    RendererAssetUniqueID = excluded.RendererAssetUniqueID,
                    RendererType = excluded.RendererType,
                    MaterialName = excluded.MaterialName,
                    MainTextureAssetUniqueID = excluded.MainTextureAssetUniqueID,
                    MainTextureSlotName = excluded.MainTextureSlotName,
                    BaseColorR = excluded.BaseColorR,
                    BaseColorG = excluded.BaseColorG,
                    BaseColorB = excluded.BaseColorB,
                    BaseColorA = excluded.BaseColorA,
                    ResolutionSource = excluded.ResolutionSource;";
            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
            var pMeshAssetId = cmd.Parameters.Add("@meshAssetId", SqliteType.Text);
            pProjectId.Value = projectId;

            foreach (var meshId in meshIds)
            {
                pMeshAssetId.Value = meshId;
                cmd.ExecuteNonQuery();
            }
        }

        private void EnsureBackgroundWriterRunningLocked()
        {
            if (_bgWriterActive || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _bgWriterActive = true;
            _bgWriterIdleEvent.Reset();
            _ = Task.Run(DrainBackgroundWritesLoopAsync);
        }

        private async Task DrainBackgroundWritesLoopAsync()
        {
            var busyRetries = 0;
            const int maxBusyRetries = 40;
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    PendingLazySemanticWrite? semanticWork = null;
                    PendingPreviewCacheWrite? previewWork = null;

                    lock (_bgWriteLock)
                    {
                        if (_pendingLazySemanticWrites.Count > 0)
                        {
                            var head = _pendingLazySemanticWrites.Dequeue();
                            if (_pendingLazySemanticWritesByKey.Remove(head.Key, out var latest))
                            {
                                semanticWork = latest;
                            }
                        }
                        else if (_pendingPreviewCacheWrites.Count > 0)
                        {
                            var head = _pendingPreviewCacheWrites.Dequeue();
                            if (_pendingPreviewCacheWritesByKey.Remove(head.Key, out var latest))
                            {
                                previewWork = latest;
                            }
                        }
                        else
                        {
                            _bgWriterActive = false;
                            _bgWriterIdleEvent.Set();
                            return;
                        }
                    }

                    if (semanticWork != null)
                    {
                        var completed = TryUpsertMaterialAndMeshCache(
                            semanticWork.FolderPath,
                            semanticWork.Signature,
                            semanticWork.Delta,
                            waitForLock: false);
                        if (!completed)
                        {
                            busyRetries++;
                            if (busyRetries < maxBusyRetries && Volatile.Read(ref _disposed) == 0)
                            {
                                lock (_bgWriteLock)
                                {
                                    if (!_pendingLazySemanticWritesByKey.TryGetValue(semanticWork.Key, out var newer))
                                    {
                                        _pendingLazySemanticWrites.Enqueue(semanticWork);
                                        _pendingLazySemanticWritesByKey[semanticWork.Key] = semanticWork;
                                    }
                                    else
                                    {
                                        var combined = new SemanticAssetRelations();
                                        combined.Merge(semanticWork.Delta);
                                        combined.Merge(newer.Delta);
                                        _pendingLazySemanticWritesByKey[semanticWork.Key] =
                                            new PendingLazySemanticWrite(semanticWork.Key, semanticWork.FolderPath, semanticWork.Signature, combined);
                                    }
                                }

                                await Task.Delay(50).ConfigureAwait(false);
                                continue;
                            }
                        }
                        else
                        {
                            busyRetries = 0;
                        }
                    }
                    else if (previewWork != null)
                    {
                        var completed = TrySavePreviewCachePayloadNonBlocking(previewWork);
                        if (!completed)
                        {
                            busyRetries++;
                            if (busyRetries < maxBusyRetries && Volatile.Read(ref _disposed) == 0)
                            {
                                lock (_bgWriteLock)
                                {
                                    if (!_pendingPreviewCacheWritesByKey.ContainsKey(previewWork.Key)
                                        && _pendingPreviewCacheWrites.Count < MaxPendingPreviewCacheWrites)
                                    {
                                        _pendingPreviewCacheWrites.Enqueue(previewWork);
                                        _pendingPreviewCacheWritesByKey[previewWork.Key] = previewWork;
                                    }
                                }

                                await Task.Delay(50).ConfigureAwait(false);
                                continue;
                            }
                        }
                        else
                        {
                            busyRetries = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Background SQLite cache writer stopped: {ex.Message}");
            }
            finally
            {
                lock (_bgWriteLock)
                {
                    _bgWriterActive = false;
                    _bgWriterIdleEvent.Set();
                }
            }
        }
    }
}
