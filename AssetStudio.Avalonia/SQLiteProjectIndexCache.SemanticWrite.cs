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
                || StagedAnimationClipTargetsCount > 0
                || StagedRigBoneHashesCount > 0
                || StagedClipBoneHashesCount > 0;

            public bool HasStagedMaterialRelations =>
                StagedAssetEdgesCount > 0
                || StagedMeshMaterialsCount > 0
                || StagedMaterialTexturesCount > 0
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
                    clipBoneHashes: batch.ClipBoneHashes);
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
                IReadOnlyList<SemanticClipBoneHashEntry>? clipBoneHashes = null)
            {
                EnsureActive();

                var sourceFileCount = sourceFiles?.Count ?? 0;
                var edgeCount = edges?.Count ?? 0;
                var groupCount = modelGroups?.Count ?? 0;
                var groupMeshCount = modelGroupMeshes?.Count ?? 0;
                var rendererCount = meshRenderers?.Count ?? 0;
                var materialCount = meshMaterials?.Count ?? 0;
                var textureCount = materialTextures?.Count ?? 0;
                var animTargetCount = animationClipTargets?.Count ?? 0;
                var rigHashCount = rigBoneHashes?.Count ?? 0;
                var clipHashCount = clipBoneHashes?.Count ?? 0;
                var batchTotal = sourceFileCount + edgeCount + groupCount + groupMeshCount + rendererCount + materialCount + textureCount + animTargetCount + rigHashCount + clipHashCount;
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
                    const int totalWork = 16;
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

                            ClearSemanticRelationTablesForProject(conn, transaction, projectId, reporter.Advance);
                            DropSemanticBulkSecondaryIndexes(conn, transaction);

                            PromoteStagedSemanticRelationsToLive(conn, transaction, projectId, reporter.Advance);

                            reporter.Report("Rebuilding semantic relation indexes", force: true);
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
                            SetSynchronousMode(conn, "NORMAL");
                        }

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
                var clearWork = replaceExisting ? 7 : 0;
                var totalWork = Math.Max(1,
                    relations.SourceFiles.Count
                    + relations.AssetEdges.Count
                    + relations.ModelGroups.Count
                    + relations.ModelGroupMeshes.Count
                    + relations.MeshRenderers.Count
                    + relations.MeshMaterials.Count
                    + relations.MaterialTextures.Count
                    + relations.AnimationClipTargets.Count
                    + relations.RigBoneHashes.Count
                    + relations.ClipBoneHashes.Count
                    + clearWork
                    + 4);
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

            using (var transaction = conn.BeginTransaction())
            {
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
                    ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture)
                VALUES (
                    @projectId, @materialAssetId, @previewMaterialAssetId, @slotName, @slotIndex, @textureAssetId, @textureFileId, @texturePathId, @isResolved, @isMainTexture)
                ON CONFLICT(ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                DO UPDATE SET
                    PreviewMaterialAssetUniqueID = excluded.PreviewMaterialAssetUniqueID,
                    IsResolved = excluded.IsResolved,
                    IsMainTexture = excluded.IsMainTexture";

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
                cmd.ExecuteNonQuery();
                advanceProgress?.Invoke($"Saving material textures: {i + 1:N0}/{textures.Count:N0}");
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
                    ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture)
                VALUES (
                    @projectId, @materialAssetId, @previewMaterialAssetId, @slotName, @slotIndex, @textureAssetId, @textureFileId, @texturePathId, @isResolved, @isMainTexture);";

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
                        ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture)
                    SELECT
                        @projectId, s.MaterialAssetUniqueID, s.PreviewMaterialAssetUniqueID, s.SlotName, s.SlotIndex, s.TextureAssetUniqueID, s.TextureFileId, s.TexturePathID, s.IsResolved, s.IsMainTexture
                    FROM Stage_MaterialTextures s
                    WHERE true
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                    DO UPDATE SET
                        PreviewMaterialAssetUniqueID = excluded.PreviewMaterialAssetUniqueID,
                        IsResolved = excluded.IsResolved,
                        IsMainTexture = excluded.IsMainTexture;"
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
                      AND (s.AvatarAssetUniqueID <> '' OR s.MeshAssetUniqueID <> '' OR s.ModelGroupId <> '' OR s.AnimatorAssetUniqueID <> '')
                    ORDER BY s.rowid ASC
                    ON CONFLICT(ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                    DO UPDATE SET
                        MatchScore = MAX(AnimationClipTargets.MatchScore, excluded.MatchScore),
                        MatchedBoneCount = MAX(AnimationClipTargets.MatchedBoneCount, excluded.MatchedBoneCount),
                        TotalClipBoneCount = MAX(AnimationClipTargets.TotalClipBoneCount, excluded.TotalClipBoneCount);";
                directCmd.Parameters.AddWithValue("@projectId", projectId);
                directCmd.ExecuteNonQuery();
            }

            // 2. Cross-bundle direct relations synthesized from AssetEdges + ModelGroups + ModelGroupMeshes
            using (var edgeSynthCmd = conn.CreateCommand())
            {
                edgeSynthCmd.Transaction = transaction;
                edgeSynthCmd.CommandText = @"
                    WITH GroupRepresentativeMesh AS (
                        SELECT
                            mgm.GroupAssetUniqueID,
                            mgm.MeshAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY mgm.GroupAssetUniqueID
                                ORDER BY mgm.Confidence DESC, COALESCE(meshAsset.ByteSize, 0) DESC, mgm.SlotIndex ASC, mgm.MeshAssetUniqueID ASC
                            ) AS RepRank
                        FROM ModelGroupMeshes mgm
                        LEFT JOIN Assets meshAsset
                          ON meshAsset.ProjectId = mgm.ProjectId
                         AND meshAsset.UniqueID = mgm.MeshAssetUniqueID
                        WHERE mgm.ProjectId = @projectId
                          AND mgm.MeshAssetUniqueID <> ''
                    ),
                    ControllerHierarchy AS (
                        SELECT
                            ae.SourceAssetUniqueID AS DerivedControllerId,
                            ae.TargetAssetUniqueID AS BaseControllerId
                        FROM AssetEdges ae
                        WHERE ae.ProjectId = @projectId
                          AND ae.EdgeKind = 'AnimatorController'
                          AND ae.SlotName = 'm_Controller'
                          AND ae.SourceAssetUniqueID <> ''
                          AND ae.TargetAssetUniqueID <> ''
                    ),
                    ControllerClips AS (
                        SELECT
                            clipEdge.SourceAssetUniqueID AS ControllerId,
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            clipEdge.SlotIndex AS SlotIndex,
                            'AnimatorController' AS Kind,
                            10000 AS BaseScore
                        FROM AssetEdges clipEdge
                        WHERE clipEdge.ProjectId = @projectId
                          AND clipEdge.EdgeKind = 'AnimationClip'
                          AND clipEdge.TargetAssetUniqueID <> ''
                        UNION ALL
                        SELECT
                            ch.DerivedControllerId AS ControllerId,
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            clipEdge.SlotIndex AS SlotIndex,
                            'AnimatorOverrideController' AS Kind,
                            9800 AS BaseScore
                        FROM ControllerHierarchy ch
                        INNER JOIN AssetEdges clipEdge
                          ON clipEdge.ProjectId = @projectId
                         AND clipEdge.SourceAssetUniqueID = ch.BaseControllerId
                         AND clipEdge.EdgeKind = 'AnimationClip'
                         AND clipEdge.TargetAssetUniqueID <> ''
                    ),
                    DirectEdges AS (
                        -- (a) Clip -> Avatar edge (AnimatorAvatar / CompatibleAvatar) joined with ModelGroups or Avatar->Mesh edges
                        SELECT
                            ca.SourceAssetUniqueID AS ClipId,
                            COALESCE(mg.AnimatorAssetUniqueID, '') AS AnimatorId,
                            ca.TargetAssetUniqueID AS AvatarId,
                            COALESCE(rep.MeshAssetUniqueID, meshEdge.TargetAssetUniqueID, '') AS MeshId,
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
                        LEFT JOIN GroupRepresentativeMesh rep
                          ON rep.GroupAssetUniqueID = mg.GroupAssetUniqueID
                         AND rep.RepRank = 1
                        LEFT JOIN AssetEdges meshEdge
                          ON meshEdge.ProjectId = ca.ProjectId
                         AND meshEdge.SourceAssetUniqueID = ca.TargetAssetUniqueID
                         AND meshEdge.EdgeKind = 'Mesh'
                         AND meshEdge.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                         AND meshEdge.TargetAssetUniqueID <> ''
                        WHERE ca.ProjectId = @projectId
                          AND ca.EdgeKind = 'Avatar'
                          AND ca.SlotName IN ('AnimatorAvatar', 'CompatibleAvatar')
                          AND ca.SourceAssetUniqueID <> ''
                          AND ca.TargetAssetUniqueID <> ''

                        UNION ALL

                        -- (b) ModelGroups linked via ControllerAssetUniqueID -> ControllerClips
                        SELECT
                            cc.ClipId AS ClipId,
                            mg.AnimatorAssetUniqueID AS AnimatorId,
                            mg.AvatarAssetUniqueID AS AvatarId,
                            COALESCE(rep.MeshAssetUniqueID, '') AS MeshId,
                            mg.GroupAssetUniqueID AS GroupId,
                            cc.Kind AS RelationKind,
                            cc.BaseScore + COALESCE(mg.Confidence, 0) + 150 - MIN(MAX(cc.SlotIndex, 0), 100) AS Score
                        FROM ModelGroups mg
                        INNER JOIN ControllerClips cc
                          ON cc.ControllerId = mg.ControllerAssetUniqueID
                        LEFT JOIN GroupRepresentativeMesh rep
                          ON rep.GroupAssetUniqueID = mg.GroupAssetUniqueID
                         AND rep.RepRank = 1
                        WHERE mg.ProjectId = @projectId
                          AND mg.ControllerAssetUniqueID <> ''

                        UNION ALL

                        -- (c) Animator / Avatar / Controller source directly holding AnimationClip edge + Avatar/Mesh edges
                        SELECT
                            clipEdge.TargetAssetUniqueID AS ClipId,
                            CASE WHEN srcAsset.Type IN (@animatorType, @animationType) THEN clipEdge.SourceAssetUniqueID ELSE '' END AS AnimatorId,
                            COALESCE(
                                avatarEdge.TargetAssetUniqueID,
                                CASE WHEN srcAsset.Type = @avatarType THEN clipEdge.SourceAssetUniqueID ELSE '' END,
                                ''
                            ) AS AvatarId,
                            COALESCE(meshEdge.TargetAssetUniqueID, avatarMeshEdge.TargetAssetUniqueID, '') AS MeshId,
                            COALESCE(mg.GroupAssetUniqueID, '') AS GroupId,
                            CASE
                                WHEN clipEdge.SlotName = 'LegacyAnimationClip' THEN 'LegacyAnimation'
                                ELSE 'AnimatorController'
                            END AS RelationKind,
                            9600 + COALESCE(mg.Confidence, 0) - MIN(MAX(clipEdge.SlotIndex, 0), 100) AS Score
                        FROM AssetEdges clipEdge
                        LEFT JOIN Assets srcAsset
                          ON srcAsset.ProjectId = clipEdge.ProjectId
                         AND srcAsset.UniqueID = clipEdge.SourceAssetUniqueID
                        LEFT JOIN AssetEdges avatarEdge
                          ON avatarEdge.ProjectId = clipEdge.ProjectId
                         AND avatarEdge.SourceAssetUniqueID = clipEdge.SourceAssetUniqueID
                         AND avatarEdge.EdgeKind = 'Avatar'
                         AND avatarEdge.TargetAssetUniqueID <> ''
                        LEFT JOIN AssetEdges meshEdge
                          ON meshEdge.ProjectId = clipEdge.ProjectId
                         AND meshEdge.SourceAssetUniqueID = clipEdge.SourceAssetUniqueID
                         AND meshEdge.EdgeKind = 'Mesh'
                         AND meshEdge.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                         AND meshEdge.TargetAssetUniqueID <> ''
                        LEFT JOIN AssetEdges avatarMeshEdge
                          ON avatarMeshEdge.ProjectId = clipEdge.ProjectId
                         AND avatarMeshEdge.SourceAssetUniqueID = COALESCE(
                                avatarEdge.TargetAssetUniqueID,
                                CASE WHEN srcAsset.Type = @avatarType THEN clipEdge.SourceAssetUniqueID ELSE '' END)
                         AND avatarMeshEdge.EdgeKind = 'Mesh'
                         AND avatarMeshEdge.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                         AND avatarMeshEdge.TargetAssetUniqueID <> ''
                        LEFT JOIN ModelGroups mg
                          ON mg.ProjectId = clipEdge.ProjectId
                         AND (
                             (mg.AnimatorAssetUniqueID <> '' AND mg.AnimatorAssetUniqueID = clipEdge.SourceAssetUniqueID)
                             OR (mg.AvatarAssetUniqueID <> '' AND mg.AvatarAssetUniqueID = COALESCE(
                                    avatarEdge.TargetAssetUniqueID,
                                    CASE WHEN srcAsset.Type = @avatarType THEN clipEdge.SourceAssetUniqueID ELSE '' END))
                         )
                        WHERE clipEdge.ProjectId = @projectId
                          AND clipEdge.EdgeKind = 'AnimationClip'
                          AND clipEdge.TargetAssetUniqueID <> ''
                          AND (
                              COALESCE(avatarEdge.TargetAssetUniqueID, '') <> ''
                              OR COALESCE(meshEdge.TargetAssetUniqueID, '') <> ''
                              OR COALESCE(avatarMeshEdge.TargetAssetUniqueID, '') <> ''
                              OR COALESCE(mg.GroupAssetUniqueID, '') <> ''
                              OR srcAsset.Type IN (@animatorType, @avatarType, @animationType)
                          )
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
                      AND (d.AvatarId <> '' OR d.MeshId <> '' OR d.GroupId <> '' OR d.AnimatorId <> '')
                      AND NOT EXISTS (
                          SELECT 1
                          FROM AnimationClipTargets existing
                          WHERE existing.ProjectId = @projectId
                            AND existing.ClipAssetUniqueID = d.ClipId
                            AND existing.RelationKind = d.RelationKind
                            AND (existing.MeshAssetUniqueID = d.MeshId OR d.MeshId = '')
                            AND existing.MatchScore >= d.Score - 1000
                      )
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

            // 3. Cross-bundle structural bone-hash matching (Stage_ClipBoneHashes ⋈ Stage_RigBoneHashes)
            long clipBoneRowCount;
            long rigBoneRowCount;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.Transaction = transaction;
                countCmd.CommandText = @"
                    SELECT
                        (SELECT COUNT(1) FROM Stage_ClipBoneHashes WHERE ProjectId = @projectId),
                        (SELECT COUNT(1) FROM Stage_RigBoneHashes WHERE ProjectId = @projectId);";
                countCmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = countCmd.ExecuteReader();
                reader.Read();
                clipBoneRowCount = reader.GetInt64(0);
                rigBoneRowCount = reader.GetInt64(1);
            }

            if (clipBoneRowCount > 0 && rigBoneRowCount > 0)
            {
                using (var idxCmd = conn.CreateCommand())
                {
                    idxCmd.Transaction = transaction;
                    idxCmd.CommandText = @"
                        CREATE INDEX IF NOT EXISTS idx_stage_rig_bone_hash
                        ON Stage_RigBoneHashes(ProjectId, BoneHash, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID);
                        CREATE INDEX IF NOT EXISTS idx_stage_clip_bone_hash
                        ON Stage_ClipBoneHashes(ProjectId, ClipAssetUniqueID, BoneHash);";
                    idxCmd.ExecuteNonQuery();
                }

                using (var matchCmd = conn.CreateCommand())
                {
                    matchCmd.Transaction = transaction;
                    matchCmd.CommandText = @"
                        WITH GroupRepresentativeMesh AS (
                            SELECT
                                mgm.GroupAssetUniqueID,
                                mgm.MeshAssetUniqueID,
                                ROW_NUMBER() OVER (
                                    PARTITION BY mgm.GroupAssetUniqueID
                                    ORDER BY mgm.Confidence DESC, COALESCE(meshAsset.ByteSize, 0) DESC, mgm.SlotIndex ASC, mgm.MeshAssetUniqueID ASC
                                ) AS RepRank
                            FROM ModelGroupMeshes mgm
                            LEFT JOIN Assets meshAsset
                              ON meshAsset.ProjectId = mgm.ProjectId
                             AND meshAsset.UniqueID = mgm.MeshAssetUniqueID
                            WHERE mgm.ProjectId = @projectId
                              AND mgm.MeshAssetUniqueID <> ''
                        ),
                        AvatarFirstMesh AS (
                            SELECT
                                ae.SourceAssetUniqueID AS AvatarId,
                                ae.TargetAssetUniqueID AS MeshId,
                                ROW_NUMBER() OVER (
                                    PARTITION BY ae.SourceAssetUniqueID
                                    ORDER BY ae.SlotIndex ASC, ae.TargetAssetUniqueID ASC
                                ) AS MeshRank
                            FROM AssetEdges ae
                            WHERE ae.ProjectId = @projectId
                              AND ae.EdgeKind = 'Mesh'
                              AND ae.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                              AND ae.SourceAssetUniqueID <> ''
                              AND ae.TargetAssetUniqueID <> ''
                        ),
                        AvatarFirstGroup AS (
                            SELECT
                                mg.AvatarAssetUniqueID AS AvatarId,
                                mg.GroupAssetUniqueID AS GroupId,
                                mg.AnimatorAssetUniqueID AS AnimatorId,
                                mg.Confidence AS GroupConfidence,
                                ROW_NUMBER() OVER (
                                    PARTITION BY mg.AvatarAssetUniqueID
                                    ORDER BY mg.Confidence DESC, mg.GroupAssetUniqueID ASC
                                ) AS GroupRank
                            FROM ModelGroups mg
                            WHERE mg.ProjectId = @projectId
                              AND mg.AvatarAssetUniqueID <> ''
                        ),
                        ClipProbeCounts AS (
                            SELECT
                                c.ClipAssetUniqueID,
                                COUNT(DISTINCT c.BoneHash) AS ProbeCount,
                                MAX(c.TotalClipBoneCount) AS TotalClipBones
                            FROM Stage_ClipBoneHashes c
                            WHERE c.ProjectId = @projectId
                            GROUP BY c.ClipAssetUniqueID
                        ),
                        RawRigMatches AS (
                            SELECT
                                c.ClipAssetUniqueID AS ClipId,
                                r.AvatarAssetUniqueID AS RawAvatarId,
                                r.AnimatorAssetUniqueID AS RawAnimatorId,
                                r.MeshAssetUniqueID AS RawMeshId,
                                r.ModelGroupId AS RawGroupId,
                                COUNT(DISTINCT c.BoneHash) AS MatchedProbes,
                                cp.ProbeCount AS ProbeCount,
                                CASE WHEN cp.TotalClipBones > 0 THEN cp.TotalClipBones ELSE cp.ProbeCount END AS TotalClipBones
                            FROM Stage_ClipBoneHashes c
                            INNER JOIN ClipProbeCounts cp
                              ON cp.ClipAssetUniqueID = c.ClipAssetUniqueID
                            INNER JOIN Stage_RigBoneHashes r
                              ON r.ProjectId = c.ProjectId
                             AND r.BoneHash = c.BoneHash
                            WHERE c.ProjectId = @projectId
                            GROUP BY
                                c.ClipAssetUniqueID,
                                r.AvatarAssetUniqueID,
                                r.AnimatorAssetUniqueID,
                                r.MeshAssetUniqueID,
                                r.ModelGroupId
                            HAVING MatchedProbes >= (
                                CASE
                                    WHEN cp.ProbeCount <= 2 THEN cp.ProbeCount
                                    WHEN cp.ProbeCount <= 5 THEN cp.ProbeCount - 1
                                    ELSE MAX(3, cp.ProbeCount / 4)
                                END
                            )
                        ),
                        EnrichedMatches AS (
                            SELECT
                                m.ClipId,
                                COALESCE(NULLIF(m.RawAnimatorId, ''), afg.AnimatorId, '') AS AnimatorId,
                                m.RawAvatarId AS AvatarId,
                                COALESCE(NULLIF(m.RawMeshId, ''), rep.MeshAssetUniqueID, afm.MeshId, '') AS MeshId,
                                COALESCE(NULLIF(m.RawGroupId, ''), afg.GroupId, '') AS GroupId,
                                m.MatchedProbes,
                                m.ProbeCount,
                                m.TotalClipBones,
                                ((m.MatchedProbes * 1000) / MAX(1, m.ProbeCount))
                                    + (m.MatchedProbes * 100)
                                    + (CASE WHEN COALESCE(NULLIF(m.RawGroupId, ''), afg.GroupId, '') <> '' THEN 60 ELSE 0 END)
                                    + (CASE WHEN COALESCE(NULLIF(m.RawMeshId, ''), rep.MeshAssetUniqueID, afm.MeshId, '') <> '' THEN 40 ELSE 0 END)
                                    + (CASE WHEN clipAsset.SourceFileId IS NOT NULL
                                             AND (clipAsset.SourceFileId = avatarAsset.SourceFileId
                                                  OR clipAsset.SourceFileId = meshAsset.SourceFileId)
                                            THEN 50 ELSE 0 END) AS Score
                            FROM RawRigMatches m
                            LEFT JOIN AvatarFirstGroup afg
                              ON m.RawAvatarId <> ''
                             AND afg.AvatarId = m.RawAvatarId
                             AND afg.GroupRank = 1
                            LEFT JOIN GroupRepresentativeMesh rep
                              ON rep.GroupAssetUniqueID = COALESCE(NULLIF(m.RawGroupId, ''), afg.GroupId, '')
                             AND rep.RepRank = 1
                            LEFT JOIN AvatarFirstMesh afm
                              ON m.RawAvatarId <> ''
                             AND afm.AvatarId = m.RawAvatarId
                             AND afm.MeshRank = 1
                            LEFT JOIN Assets clipAsset
                              ON clipAsset.ProjectId = @projectId
                             AND clipAsset.UniqueID = m.ClipId
                            LEFT JOIN Assets avatarAsset
                              ON avatarAsset.ProjectId = @projectId
                             AND m.RawAvatarId <> ''
                             AND avatarAsset.UniqueID = m.RawAvatarId
                            LEFT JOIN Assets meshAsset
                              ON meshAsset.ProjectId = @projectId
                             AND COALESCE(NULLIF(m.RawMeshId, ''), rep.MeshAssetUniqueID, afm.MeshId, '') <> ''
                             AND meshAsset.UniqueID = COALESCE(NULLIF(m.RawMeshId, ''), rep.MeshAssetUniqueID, afm.MeshId, '')
                        ),
                        RankedMatches AS (
                            SELECT
                                e.ClipId,
                                e.AnimatorId,
                                e.AvatarId,
                                e.MeshId,
                                e.GroupId,
                                MAX(e.Score) AS BestScore,
                                MAX(e.MatchedProbes) AS BestMatchedProbes,
                                MAX(e.TotalClipBones) AS TotalClipBones,
                                ROW_NUMBER() OVER (
                                    PARTITION BY e.ClipId
                                    ORDER BY MAX(e.Score) DESC, MAX(e.MatchedProbes) DESC, e.AvatarId ASC, e.MeshId ASC, e.GroupId ASC
                                ) AS RankNum
                            FROM EnrichedMatches e
                            WHERE e.AvatarId <> '' OR e.MeshId <> '' OR e.GroupId <> ''
                            GROUP BY e.ClipId, e.AvatarId, e.MeshId, e.GroupId, e.AnimatorId
                        )
                        INSERT INTO AnimationClipTargets (
                            ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                            MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                        SELECT
                            @projectId,
                            r.ClipId,
                            r.AnimatorId,
                            r.AvatarId,
                            r.MeshId,
                            r.GroupId,
                            'BoneHashMatch',
                            r.BestScore,
                            r.BestMatchedProbes,
                            r.TotalClipBones
                        FROM RankedMatches r
                        WHERE r.RankNum <= 16
                        ON CONFLICT(ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                        DO UPDATE SET
                            MatchScore = MAX(AnimationClipTargets.MatchScore, excluded.MatchScore),
                            MatchedBoneCount = MAX(AnimationClipTargets.MatchedBoneCount, excluded.MatchedBoneCount),
                            TotalClipBoneCount = MAX(AnimationClipTargets.TotalClipBoneCount, excluded.TotalClipBoneCount);";
                    matchCmd.Parameters.AddWithValue("@projectId", projectId);
                    matchCmd.ExecuteNonQuery();
                }

                // Also synthesize CompatibleAvatar edges into AssetEdges for top bone-hash matches
                // so legacy LoadAnimationClipAvatarAssetIds / LoadAnimationClipMeshAssetIds queries
                // see decoupled bone-hash-matched rigs immediately.
                using (var compatEdgeCmd = conn.CreateCommand())
                {
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
            }

            advanceProgress?.Invoke("Promoted animation clip targets");
        }
    }
}
