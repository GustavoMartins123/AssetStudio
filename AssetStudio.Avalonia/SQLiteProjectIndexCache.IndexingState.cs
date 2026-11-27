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

            var dbPath = GetDbPath(folderPath);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                EnsureInitialized(folderPath);
                try
                {
                    lock (WriteGate)
                    {
                        using var conn = CreateConnectionByDbPath(dbPath);
                        using var transaction = conn.BeginTransaction();
                        var isLightweightProgressUpdate = update.NewlyReadFiles.Count == 0
                            && !IsTerminalIndexingStatus(update.Status)
                            && (string.Equals(update.Status, "connecting", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(update.Status, "saving_connections", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(update.Status, "building_structure", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(update.Status, "saving_index", StringComparison.OrdinalIgnoreCase));

                        long projectId;
                        var signatureChanged = false;
                        if (isLightweightProgressUpdate)
                        {
                            var existingId = FindProjectId(conn, transaction, folderPath, signature, _caseSensitivePaths);
                            if (existingId.HasValue)
                            {
                                projectId = existingId.Value;
                            }
                            else
                            {
                                InvalidateProjectIdCacheForDb(dbPath);
                                projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult, _caseSensitivePaths, out signatureChanged);
                            }
                        }
                        else
                        {
                            InvalidateProjectIdCacheForDb(dbPath);
                            projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult, _caseSensitivePaths, out signatureChanged);
                        }

                        if (signatureChanged)
                        {
                            MmapProjectIndexSidecar.TryDeleteFileQuietly(MmapProjectIndexSidecar.GetSidecarPath(dbPath));
                            InvalidateStalePreviewsForChangedProject(conn, transaction, projectId, folderPath, scanResult, signatureChanged: true);
                        }

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
                        if (!isLightweightProgressUpdate || signatureChanged)
                        {
                            NotifyDatabaseMutated(dbPath);
                        }
                        return;
                    }
                }
                catch (SqliteException ex) when (IsSqliteCorruptionException(ex) && attempt == 0)
                {
                    TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while saving indexing progress: {ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    RecordClassifiedException("SqliteIndex", dbPath, ex);
                    Logger.Warning($"Failed to save indexing progress: {ex.Message}");
                    return;
                }
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
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while loading indexing state: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return null;
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                RecordClassifiedException("SqliteIndex", dbPath, ex);
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

            var dbPath = GetDbPath(folderPath);
            // Fast, read-only lookup for project list cards: never create empty DBs, take WriteGate,
            // run schema DDL, or scan full B-trees when displaying saved project metadata.
            if (!File.Exists(dbPath))
            {
                return null;
            }

            try
            {
                var info = new FileInfo(dbPath);
                if (info.Length == 0 || !HasValidSqliteHeader(dbPath))
                {
                    return null;
                }

                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = FindLatestProjectId(conn, null, folderPath, _caseSensitivePaths);
                    if (projectId == null)
                    {
                        return null;
                    }

                    return LoadIndexingState(conn, null, projectId.Value, includeReadFilePaths: false);
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
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                return null;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Older/incomplete DB missing v16 columns/tables; defer schema upgrade until the project is opened.
                return null;
            }
            catch (Exception ex)
            {
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to load latest indexing progress: {ex.Message}");
                return null;
            }
        }

        private static ProjectIndexingState? LoadIndexingState(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            long projectId,
            bool includeReadFilePaths = true)
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

            if (includeReadFilePaths)
            {
                using var cmd = conn.CreateCommand();
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

                return state;
            }

            var readFileCount = 0;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.Transaction = transaction;
                countCmd.CommandText = "SELECT COUNT(*) FROM IndexingReadFiles WHERE ProjectId = @projectId";
                countCmd.Parameters.AddWithValue("@projectId", projectId);
                readFileCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
            }

            if (readFileCount > 0)
            {
                state = new ProjectIndexingState
                {
                    Status = state.Status,
                    TotalFiles = state.TotalFiles,
                    ProcessedFiles = state.ProcessedFiles,
                    PendingFiles = state.PendingFiles,
                    PercentComplete = state.PercentComplete,
                    CurrentFile = state.CurrentFile,
                    LastReadFile = state.LastReadFile,
                    StartedAt = state.StartedAt,
                    UpdatedAt = state.UpdatedAt,
                    CompletedAt = state.CompletedAt,
                    ReadFileCount = readFileCount
                };
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
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                _semanticStatusCache.TryRemove(cacheKey, out _);
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while inspecting semantic relation cache status: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return default;
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                _semanticStatusCache.TryRemove(cacheKey, out _);
                RecordClassifiedException("SqliteIndex", dbPath, ex);
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

        internal bool HasCompletedIndexForSignature(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = FindProjectId(conn, null, folderPath, signature, _caseSensitivePaths);
                    if (projectId == null)
                    {
                        return false;
                    }

                    var status = LoadProjectIndexingStatusFast(conn, null, projectId.Value);
                    return !IsIncompleteIndexCacheStatus(status);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsInterruptedOrIncompleteIndexingStatus(string? status)
        {
            return IsIncompleteIndexCacheStatus(status);
        }

        internal bool HasInterruptedIndexingState(
            string folderPath,
            string? expectedSignature,
            out ProjectIndexingState? state,
            out bool signatureMismatched)
        {
            state = null;
            signatureMismatched = false;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            var dbPath = GetDbPath(folderPath);
            if (!File.Exists(dbPath))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            if (!File.Exists(dbPath))
            {
                return false;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var latestProjectId = FindLatestProjectId(conn, null, folderPath, _caseSensitivePaths);
                    if (latestProjectId == null)
                    {
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedSignature))
                    {
                        using var sigCmd = conn.CreateCommand();
                        sigCmd.CommandText = "SELECT SignatureHash FROM Projects WHERE Id = @projectId LIMIT 1;";
                        sigCmd.Parameters.AddWithValue("@projectId", latestProjectId.Value);
                        var storedSignature = sigCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                        signatureMismatched = !string.Equals(storedSignature, expectedSignature, StringComparison.Ordinal);
                    }

                    state = LoadIndexingState(conn, null, latestProjectId.Value);
                    return state != null && IsIncompleteIndexCacheStatus(state.Status);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while inspecting interrupted indexing state: {ex.Message}", ex);
                return false;
            }
            catch (Exception ex)
            {
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                return false;
            }
        }

        internal bool TryRecoverInterruptedIndexing(string folderPath, string? expectedSignature = null)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            if (!HasInterruptedIndexingState(folderPath, expectedSignature, out var state, out var signatureMismatched))
            {
                return false;
            }

            var dbPath = GetDbPath(folderPath);
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            try
            {
                lock (WriteGate)
                {
                    InvalidateReadConnectionPool(dbPath);
                    InvalidateProjectIdCacheForDb(dbPath);
                    MmapProjectIndexSidecar.DeleteSidecarFiles(dbPath);

                    using var conn = CreateConnectionByDbPath(dbPath);
                    using var tx = conn.BeginTransaction();
                    var projectId = FindLatestProjectId(conn, tx, folderPath, _caseSensitivePaths);
                    if (projectId == null)
                    {
                        return false;
                    }

                    DropSemanticStagingTables(conn, tx);
                    ClearIndexTablesForProject(conn, tx, projectId.Value, preserveSemanticRelations: false);

                    using (var clearPrevCmd = conn.CreateCommand())
                    {
                        clearPrevCmd.Transaction = tx;
                        clearPrevCmd.CommandText = @"
                            DELETE FROM PreviewCacheEntries WHERE ProjectId = @projectId;
                            DELETE FROM IndexingReadFiles WHERE ProjectId = @projectId;";
                        clearPrevCmd.Parameters.AddWithValue("@projectId", projectId.Value);
                        clearPrevCmd.ExecuteNonQuery();
                    }

                    UpsertProjectIndexingStateRow(
                        conn,
                        tx,
                        projectId.Value,
                        "failed",
                        Math.Max(0, state?.TotalFiles ?? 0),
                        0,
                        Math.Max(0, state?.TotalFiles ?? 0),
                        0.0,
                        signatureMismatched
                            ? "Recovered interrupted indexing with mismatched signature; rebuild required"
                            : "Recovered interrupted indexing; rebuild required",
                        string.Empty,
                        completed: false);

                    tx.Commit();
                    NotifyDatabaseMutated(dbPath);
                }

                RecordDiagnostic(
                    CacheFailureKind.InterruptedIndexing,
                    "SqliteIndex",
                    dbPath,
                    $"Cleared partial index and stale sidecar after interrupted indexing state '{state?.Status ?? "unknown"}'"
                        + (signatureMismatched ? " (signature mismatch)" : string.Empty)
                        + ".",
                    recoveredAutomatically: true);
                MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
                return true;
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                return TryRecoverCorruptDatabase(dbPath, $"SQLite corruption during interrupted indexing recovery: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to recover interrupted indexing for '{folderPath}': {ex.Message}");
                return false;
            }
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
