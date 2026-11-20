using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        private const int SemanticRelationCommitBatchSize = 10_000;

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
            try
            {
                var clearWork = replaceExisting ? 4 : 0;
                var totalWork = Math.Max(1,
                    relations.SourceFiles.Count
                    + relations.AssetEdges.Count
                    + relations.ModelGroups.Count
                    + relations.ModelGroupMeshes.Count
                    + relations.MeshRenderers.Count
                    + relations.MeshMaterials.Count
                    + relations.MaterialTextures.Count
                    + clearWork
                    + 3);
                var reporter = new ThrottledProgressReporter(totalWork, progress);

                reporter.Report("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    reporter.Report("Opening SQLite connection", force: true);
                    using var conn = CreateConnection(folderPath);

                    long projectId;
                    bool resumePartialSave;
                    using (var transaction = conn.BeginTransaction())
                    {
                        reporter.Report("Resolving project row", force: true);
                        var foundProjectId = FindProjectId(conn, transaction, folderPath, signature);
                        if (foundProjectId == null)
                        {
                            return false;
                        }

                        projectId = foundProjectId.Value;
                        resumePartialSave = replaceExisting
                            && IsSemanticSaveInProgress(conn, transaction, projectId)
                            && CountMaterialSemanticRelations(conn, transaction, projectId) > 0;

                        SaveProjectIndexingState(
                            conn,
                            transaction,
                            projectId,
                            "saving_connections",
                            reporter.ProcessedWork,
                            reporter.TotalWork,
                            resumePartialSave
                                ? "Resuming partial semantic relation save"
                                : "Preparing semantic relation save",
                            completed: false);
                        transaction.Commit();
                    }

                    if (replaceExisting && !resumePartialSave)
                    {
                        using var transaction = conn.BeginTransaction();
                        ClearSemanticRelationTablesForProject(conn, transaction, projectId, reporter.Advance);
                        SaveProjectIndexingState(
                            conn,
                            transaction,
                            projectId,
                            "saving_connections",
                            reporter.ProcessedWork,
                            reporter.TotalWork,
                            reporter.CurrentStage,
                            completed: false);
                        transaction.Commit();
                    }
                    else if (replaceExisting && resumePartialSave)
                    {
                        reporter.Skip(clearWork, "Resuming partial semantic relation save");
                    }

                    using (var transaction = conn.BeginTransaction())
                    {
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
                    reporter.Report("Checkpointing SQLite WAL", force: true);
                    TryCheckpointWal(conn, "semantic relations");
                    reporter.Report("Semantic relations saved", force: true);
                    return true;

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
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save semantic asset relations: {ex.Message}");
                return false;
            }
        }

        internal void ClearSemanticRelations(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return;
            }

            EnsureInitialized(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnection(folderPath);
                    using var transaction = conn.BeginTransaction();
                    var projectId = FindProjectId(conn, transaction, folderPath, signature);
                    if (projectId == null)
                    {
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
    }
}
