using System;
using System.IO;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        internal void SaveIndexingProgress(
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            IndexingProgressUpdate update)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || scanResult == null || update == null)
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
                    InvalidateProjectIdCacheForDb(dbPath);
                    using var transaction = conn.BeginTransaction();
                    var projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult);

                    if (update.ProcessedFiles == 0
                        && update.NewlyReadFiles.Count == 0
                        && string.Equals(update.Status, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        using var clearCmd = conn.CreateCommand();
                        clearCmd.Transaction = transaction;
                        clearCmd.CommandText = "DELETE FROM IndexingReadFiles WHERE ProjectId = @projectId";
                        clearCmd.Parameters.AddWithValue("@projectId", projectId);
                        clearCmd.ExecuteNonQuery();
                    }

                    UpsertProjectIndexingStateRow(
                        conn,
                        transaction,
                        projectId,
                        update.Status ?? string.Empty,
                        update.TotalFiles,
                        update.ProcessedFiles,
                        update.PendingFiles,
                        update.PercentComplete,
                        update.CurrentFile ?? string.Empty,
                        update.LastReadFile ?? string.Empty,
                        IsTerminalIndexingStatus(update.Status));

                    if (update.NewlyReadFiles.Count > 0)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT INTO IndexingReadFiles (ProjectId, FilePath, FileName, ReadOrder, Status)
                            VALUES (@projectId, @filePath, @fileName, @readOrder, 'read')
                            ON CONFLICT(ProjectId, FilePath)
                            DO UPDATE SET
                                FileName = excluded.FileName,
                                ReadOrder = excluded.ReadOrder,
                                ReadAt = CURRENT_TIMESTAMP,
                                Status = excluded.Status";

                        var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
                        var pFilePath = cmd.Parameters.Add("@filePath", SqliteType.Text);
                        var pFileName = cmd.Parameters.Add("@fileName", SqliteType.Text);
                        var pReadOrder = cmd.Parameters.Add("@readOrder", SqliteType.Integer);
                        pProjectId.Value = projectId;

                        var firstReadOrder = Math.Max(1, update.ProcessedFiles - update.NewlyReadFiles.Count + 1);
                        for (var i = 0; i < update.NewlyReadFiles.Count; i++)
                        {
                            var filePath = update.NewlyReadFiles[i] ?? string.Empty;
                            pFilePath.Value = filePath;
                            pFileName.Value = Path.GetFileName(filePath);
                            pReadOrder.Value = firstReadOrder + i;
                            cmd.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                    CacheProjectId(dbPath, folderPath, signature, projectId);
                    NotifyDatabaseMutated(dbPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save indexing progress: {ex.Message}");
            }
        }

        internal ProjectIndexingState? LoadIndexingState(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out var fromCache);
                    if (projectId == null)
                    {
                        return null;
                    }

                    var state = LoadIndexingState(conn, null, projectId.Value);
                    if (state == null && fromCache)
                    {
                        var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                        if (verifiedProjectId == null)
                        {
                            return null;
                        }

                        if (verifiedProjectId.Value != projectId.Value)
                        {
                            state = LoadIndexingState(conn, null, verifiedProjectId.Value);
                        }
                    }

                    return state;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                return null;
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                Logger.Warning($"Failed to load indexing progress: {ex.Message}");
                return null;
            }
        }

        internal ProjectIndexingState? LoadLatestIndexingState(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = FindLatestProjectId(conn, null, folderPath);
                    if (projectId == null)
                    {
                        return null;
                    }

                    return LoadIndexingState(conn, null, projectId.Value);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load latest indexing progress: {ex.Message}");
                return null;
            }
        }

        private static ProjectIndexingState? LoadIndexingState(SqliteConnection conn, SqliteTransaction? transaction, long projectId)
        {
            ProjectIndexingState? state = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT Status, TotalFiles, ProcessedFiles, PendingFiles, PercentComplete,
                           CurrentFile, LastReadFile, StartedAt, UpdatedAt, CompletedAt
                    FROM ProjectIndexingState
                    WHERE ProjectId = @projectId
                    LIMIT 1";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    state = new ProjectIndexingState
                    {
                        Status = reader.GetString(0),
                        TotalFiles = reader.GetInt32(1),
                        ProcessedFiles = reader.GetInt32(2),
                        PendingFiles = reader.GetInt32(3),
                        PercentComplete = reader.GetDouble(4),
                        CurrentFile = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        LastReadFile = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        StartedAt = ReadNullableDateTime(reader, 7),
                        UpdatedAt = ReadNullableDateTime(reader, 8),
                        CompletedAt = ReadNullableDateTime(reader, 9)
                    };
                }
            }

            if (state == null)
            {
                return null;
            }

            if (IsSemanticReadyStatus(state.Status)
                && !HasAnyMaterialSemanticRelations(conn, transaction, projectId))
            {
                state = new ProjectIndexingState
                {
                    Status = "connections_failed",
                    TotalFiles = state.TotalFiles,
                    ProcessedFiles = state.ProcessedFiles,
                    PendingFiles = state.PendingFiles,
                    PercentComplete = state.PercentComplete,
                    CurrentFile = "Saved connections missing",
                    LastReadFile = string.Empty,
                    StartedAt = state.StartedAt,
                    UpdatedAt = state.UpdatedAt,
                    CompletedAt = null
                };
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT FilePath
                    FROM IndexingReadFiles
                    WHERE ProjectId = @projectId
                    ORDER BY ReadOrder, Id";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    state.ReadFiles.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                }
            }

            return state;
        }

        private static bool IsSemanticReadyStatus(string? status)
        {
            return string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
        }

        internal bool CanUseSemanticRelationCache(string folderPath, string signature)
        {
            var status = GetOrResolveSemanticCacheStatus(folderPath, signature);
            return status.HasRelations && IsSemanticReadyStatus(status.IndexingStatus);
        }

        internal bool HasCompletedLazyStructureStatus(string folderPath, string signature)
        {
            var status = GetOrResolveSemanticCacheStatus(folderPath, signature);
            return status.HasRelations
                && string.Equals(status.IndexingStatus, "structure_completed", StringComparison.OrdinalIgnoreCase);
        }

        internal CachedSemanticCacheStatus GetOrResolveSemanticCacheStatus(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return default;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var cacheKey = BuildProjectIdCacheKey(dbPath, folderPath, signature);
            var currentGen = GetDatabaseWriteGeneration(dbPath);
            if (_semanticStatusCache.TryGetValue(cacheKey, out var cached)
                && cached.WriteGeneration == currentGen)
            {
                return cached;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out var fromCache);
                    if (projectId == null)
                    {
                        var empty = new CachedSemanticCacheStatus(string.Empty, false, currentGen);
                        _semanticStatusCache[cacheKey] = empty;
                        return empty;
                    }

                    var indexingStatus = LoadProjectIndexingStatusFast(conn, null, projectId.Value);
                    var hasRelations = HasAnyMaterialSemanticRelations(conn, null, projectId.Value);
                    if (!hasRelations && string.IsNullOrEmpty(indexingStatus) && fromCache)
                    {
                        var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                        if (verifiedProjectId == null)
                        {
                            var empty = new CachedSemanticCacheStatus(string.Empty, false, currentGen);
                            _semanticStatusCache[cacheKey] = empty;
                            return empty;
                        }

                        if (verifiedProjectId.Value != projectId.Value)
                        {
                            indexingStatus = LoadProjectIndexingStatusFast(conn, null, verifiedProjectId.Value);
                            hasRelations = HasAnyMaterialSemanticRelations(conn, null, verifiedProjectId.Value);
                        }
                    }

                    var resolved = new CachedSemanticCacheStatus(indexingStatus, hasRelations, GetDatabaseWriteGeneration(dbPath));
                    _semanticStatusCache[cacheKey] = resolved;
                    return resolved;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                return default;
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                _semanticStatusCache.TryRemove(cacheKey, out _);
                Logger.Warning($"Failed to inspect semantic relation cache status: {ex.Message}");
                return default;
            }
        }

        private static string LoadProjectIndexingStatusFast(SqliteConnection conn, SqliteTransaction? transaction, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT Status FROM ProjectIndexingState WHERE ProjectId = @projectId LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }

        private static bool HasAnyMaterialSemanticRelations(SqliteConnection conn, SqliteTransaction? transaction, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT
                    CASE
                        WHEN EXISTS (SELECT 1 FROM MeshMaterialResolvedCache WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM MeshMaterials WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM MaterialCache WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM MaterialTextures WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM AnimationClipTargets WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM ModelGroups WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM ModelGroupMeshes WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM CompositeAssets WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        WHEN EXISTS (SELECT 1 FROM AssetEdges WHERE ProjectId = @projectId LIMIT 1) THEN 1
                        ELSE 0
                    END";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0) != 0;
        }

        private static void SaveProjectIndexingState(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string status,
            int processed,
            int total,
            string stage,
            bool completed)
        {
            var safeTotal = Math.Max(1, total);
            var safeProcessed = Math.Clamp(processed, 0, safeTotal);
            var percent = Math.Min(100, Math.Max(0, safeProcessed * 100.0 / safeTotal));
            var safeStage = stage ?? string.Empty;

            UpsertProjectIndexingStateRow(
                conn,
                transaction,
                projectId,
                status ?? string.Empty,
                safeTotal,
                safeProcessed,
                Math.Max(0, safeTotal - safeProcessed),
                percent,
                safeStage,
                safeStage,
                completed);
        }

        private static void UpsertProjectIndexingStateRow(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string status,
            int totalFiles,
            int processedFiles,
            int pendingFiles,
            double percentComplete,
            string currentFile,
            string lastReadFile,
            bool completed)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO ProjectIndexingState (
                    ProjectId, Status, TotalFiles, ProcessedFiles, PendingFiles, PercentComplete,
                    CurrentFile, LastReadFile, StartedAt, UpdatedAt, CompletedAt)
                VALUES (
                    @projectId, @status, @totalFiles, @processedFiles, @pendingFiles, @percentComplete,
                    @currentFile, @lastReadFile, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, @completedAt)
                ON CONFLICT(ProjectId)
                DO UPDATE SET
                    Status = excluded.Status,
                    TotalFiles = excluded.TotalFiles,
                    ProcessedFiles = excluded.ProcessedFiles,
                    PendingFiles = excluded.PendingFiles,
                    PercentComplete = excluded.PercentComplete,
                    CurrentFile = excluded.CurrentFile,
                    LastReadFile = excluded.LastReadFile,
                    UpdatedAt = CURRENT_TIMESTAMP,
                    CompletedAt = excluded.CompletedAt";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@totalFiles", totalFiles);
            cmd.Parameters.AddWithValue("@processedFiles", processedFiles);
            cmd.Parameters.AddWithValue("@pendingFiles", pendingFiles);
            cmd.Parameters.AddWithValue("@percentComplete", percentComplete);
            cmd.Parameters.AddWithValue("@currentFile", currentFile);
            cmd.Parameters.AddWithValue("@lastReadFile", lastReadFile);
            cmd.Parameters.AddWithValue("@completedAt", completed ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static bool IsTerminalIndexingStatus(string? status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "connections_failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
        }
    }
}
