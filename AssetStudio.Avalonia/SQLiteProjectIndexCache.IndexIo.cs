using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        public List<AssetHandle>? LoadIndexCache(string folderPath, string signature)
        {
            return LoadIndexCacheCore(folderPath, signature, allowMmapSidecar: true);
        }

        internal List<AssetHandle>? LoadIndexCacheFromSqliteOnly(string folderPath, string signature)
        {
            return LoadIndexCacheCore(folderPath, signature, allowMmapSidecar: false);
        }

        private List<AssetHandle>? LoadIndexCacheCore(string folderPath, string signature, bool allowMmapSidecar)
        {
            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
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

                    if (fromCache)
                    {
                        var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                        if (verifiedProjectId == null)
                        {
                            return null;
                        }

                        projectId = verifiedProjectId;
                    }

                    var indexingStatus = LoadProjectIndexingStatus(conn, projectId.Value);
                    if (IsIncompleteIndexCacheStatus(indexingStatus))
                    {
                        return null;
                    }

                    if (allowMmapSidecar
                        && MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SemanticSchemaVersion, out var mmapHandles))
                    {
                        return mmapHandles.Count == 0 ? null : mmapHandles;
                    }

                    var handles = LoadAssetHandlesForProject(conn, projectId.Value, out var incomplete);
                    if (incomplete)
                    {
                        return null;
                    }

                    return handles.Count == 0 ? null : handles;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                Logger.Warning($"Failed to load SQLite index cache: {ex.Message}");
                return null;
            }
        }

        private static List<AssetHandle> LoadAssetHandlesForProject(SqliteConnection conn, long projectId, out bool incomplete)
        {
            var indexingStatus = LoadProjectIndexingStatus(conn, projectId);
            if (IsIncompleteIndexCacheStatus(indexingStatus))
            {
                incomplete = true;
                return new List<AssetHandle>();
            }

            incomplete = false;
            var sourceFiles = LoadSourceFileOriginsForProject(conn, projectId);
            var handles = new List<AssetHandle>();
            using (var cmd = conn.CreateCommand())
            {
                // Schema v13: handles live once in the clustered Assets table (PRIMARY KEY (ProjectId, UniqueID));
                // origins are resolved from the per-project SourceFiles map so path strings are shared, not duplicated.
                cmd.CommandText = @"
                    SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                    FROM Assets
                    WHERE ProjectId = @projectId";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var origin = !reader.IsDBNull(4) && sourceFiles.TryGetValue(reader.GetInt64(4), out var resolved)
                            ? resolved
                            : AssetHandleOrigin.Empty;
                        handles.Add(AssetHandle.CreateWithOrigin(
                            origin,
                            reader.GetString(0),
                            reader.GetInt64(5),
                            reader.GetString(1),
                            (ClassIDType)reader.GetInt32(2),
                            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            reader.GetInt64(6),
                            reader.GetInt64(7)));
                    }
                }
            }

            return handles;
        }

        private static Dictionary<long, AssetHandleOrigin> LoadSourceFileOriginsForProject(SqliteConnection conn, long projectId)
        {
            var result = new Dictionary<long, AssetHandleOrigin>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, SerializedFileName, OriginalPath
                FROM SourceFiles
                WHERE ProjectId = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var serializedFileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var originalPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                result[reader.GetInt64(0)] = AssetHandleOrigin.Intern(serializedFileName, originalPath);
            }

            return result;
        }

        public bool SaveIndexCache(
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            string unityVersion,
            IEnumerable<AssetHandle> handles,
            bool preserveSemanticRelations = false,
            Action<int, int, string>? progress = null)
        {
            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                var handleList = handles?.ToList() ?? new List<AssetHandle>();
                // v13 writes every handle exactly once (unified Assets table) plus one row per source file.
                var totalWork = Math.Max(1, handleList.Count + Math.Max(1, scanResult.TotalFiles));
                var reporter = new ThrottledProgressReporter(totalWork, progress);

                reporter.Report("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    reporter.Report("Opening SQLite connection", force: true);
                    using (var conn = CreateConnectionByDbPath(dbPath))
                    {
                        InvalidateProjectIdCacheForDb(dbPath);
                        long committedProjectId;

                        SetSynchronousMode(conn, "OFF");
                        try
                        {
                            using (var transaction = conn.BeginTransaction())
                            {
                                reporter.Report("Preparing SQLite project row", force: true);
                                var projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult, unityVersion);
                                reporter.Report("Clearing previous index rows", force: true);
                                ClearIndexTablesForProject(conn, transaction, projectId, preserveSemanticRelations);

                                // Single write pass into the unified clustered Assets table (no secondary indexes to rebuild).
                                InsertSourceFilesAndAssets(conn, transaction, projectId, handleList, reporter.Advance);

                                reporter.Report("Committing SQLite transaction", force: true);
                                transaction.Commit();
                                committedProjectId = projectId;
                            }
                        }
                        finally
                        {
                            SetSynchronousMode(conn, "NORMAL");
                        }

                        CacheProjectId(dbPath, folderPath, signature, committedProjectId);
                        reporter.Report("Writing mmap .idx sidecar cache", force: true);
                        MmapProjectIndexSidecar.TryWriteSidecar(
                            MmapProjectIndexSidecar.GetSidecarPath(dbPath),
                            signature,
                            SemanticSchemaVersion,
                            handleList);
                        reporter.Report("Analyzing SQLite statistics", force: true);
                        TryAnalyzeDatabase(conn, "index cache");
                        reporter.Report("Checkpointing SQLite WAL", force: true);
                        TryCheckpointWal(conn, "index cache");
                        reporter.MarkCompleted("SQLite index cache saved");
                        reporter.Report("SQLite index cache saved", force: true);
                        Logger.Info($"Saved index cache in SQLite for: {folderPath}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save SQLite index cache: {ex.Message}");
                return false;
            }
        }

        private static string? LoadProjectIndexingStatus(SqliteConnection conn, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Status
                FROM ProjectIndexingState
                WHERE ProjectId = @projectId
                LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return cmd.ExecuteScalar()?.ToString();
        }

        private static bool IsIncompleteIndexCacheStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            return string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "saving_index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
        }

        private static long EnsureProject(
            SqliteConnection conn,
            SqliteTransaction transaction,
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            string? unityVersion = null)
        {
            var existingProjectId = FindProjectId(conn, transaction, folderPath, signature);
            if (existingProjectId != null)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"
                    UPDATE Projects
                    SET TotalFiles = @totalFiles,
                        TotalBytes = @totalBytes,
                        UnityBundleCount = @unityBundles,
                        UnityVersion = @unityVersion,
                        LastIndexed = CURRENT_TIMESTAMP
                    WHERE Id = @projectId";
                updateCmd.Parameters.AddWithValue("@projectId", existingProjectId.Value);
                updateCmd.Parameters.AddWithValue("@totalFiles", scanResult.TotalFiles);
                updateCmd.Parameters.AddWithValue("@totalBytes", scanResult.TotalBytes);
                updateCmd.Parameters.AddWithValue("@unityBundles", scanResult.UnityBundleCount);
                updateCmd.Parameters.AddWithValue("@unityVersion", unityVersion ?? string.Empty);
                updateCmd.ExecuteNonQuery();
                return existingProjectId.Value;
            }

            using (var deleteOldCmd = conn.CreateCommand())
            {
                var path1 = folderPath;
                var path2 = path1.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                deleteOldCmd.Transaction = transaction;
                deleteOldCmd.CommandText = @"
                    DELETE FROM Projects
                    WHERE FolderPath = @path1 COLLATE NOCASE
                       OR FolderPath = @path2 COLLATE NOCASE";
                deleteOldCmd.Parameters.AddWithValue("@path1", path1);
                deleteOldCmd.Parameters.AddWithValue("@path2", path2);
                deleteOldCmd.ExecuteNonQuery();
            }

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT INTO Projects (FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                VALUES (@path, @signature, @totalFiles, @totalBytes, @unityBundles, @unityVersion);
                SELECT last_insert_rowid();";
            insertCmd.Parameters.AddWithValue("@path", folderPath);
            insertCmd.Parameters.AddWithValue("@signature", signature);
            insertCmd.Parameters.AddWithValue("@totalFiles", scanResult.TotalFiles);
            insertCmd.Parameters.AddWithValue("@totalBytes", scanResult.TotalBytes);
            insertCmd.Parameters.AddWithValue("@unityBundles", scanResult.UnityBundleCount);
            insertCmd.Parameters.AddWithValue("@unityVersion", unityVersion ?? string.Empty);
            return Convert.ToInt64(insertCmd.ExecuteScalar());
        }

        private static void ClearIndexTablesForProject(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            bool preserveSemanticRelations)
        {
            var tables = preserveSemanticRelations
                ? new[]
                {
                    "Assets",
                    "SourceFiles"
                }
                : new[]
                {
                    "MaterialTextures",
                    "MeshMaterials",
                    "MeshRenderers",
                    "ModelGroupMeshes",
                    "ModelGroups",
                    "AssetEdges",
                    "Assets",
                    "SourceFiles"
                };

            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table} WHERE ProjectId = @projectId";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertSourceFilesAndAssets(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyCollection<AssetHandle> handles,
            Action<string>? advanceProgress = null)
        {
            if (handles.Count == 0)
            {
                return;
            }

            var sourceFileIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var sourceGroups = handles
                .GroupBy(h => new
                {
                    SerializedFileName = h.SerializedFileName ?? string.Empty,
                    OriginalPath = h.OriginalPath ?? string.Empty
                })
                .ToList();

            var sourceGroupIndex = 0;
            using (var upsertSourceCmd = conn.CreateCommand())
            {
                upsertSourceCmd.Transaction = transaction;
                upsertSourceCmd.CommandText = @"
                    INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                    VALUES (@projectId, @serializedFileName, @originalPath, '', @objectCount)
                    ON CONFLICT(ProjectId, SerializedFileName, OriginalPath)
                    DO UPDATE SET ObjectCount = excluded.ObjectCount, LastSeen = CURRENT_TIMESTAMP
                    RETURNING Id";
                var pSrcProjectId = upsertSourceCmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pSerializedFileName = upsertSourceCmd.Parameters.Add("@serializedFileName", SqliteType.Text);
                var pOriginalPath = upsertSourceCmd.Parameters.Add("@originalPath", SqliteType.Text);
                var pObjectCount = upsertSourceCmd.Parameters.Add("@objectCount", SqliteType.Integer);
                pSrcProjectId.Value = projectId;

                foreach (var group in sourceGroups)
                {
                    pSerializedFileName.Value = group.Key.SerializedFileName;
                    pOriginalPath.Value = group.Key.OriginalPath;
                    pObjectCount.Value = group.Count();
                    var id = upsertSourceCmd.ExecuteScalar();
                    if (id != null && id != DBNull.Value)
                    {
                        sourceFileIds[GetSourceFileKey(group.Key.SerializedFileName, group.Key.OriginalPath)] = Convert.ToInt64(id);
                    }

                    sourceGroupIndex++;
                    advanceProgress?.Invoke($"Saving source files: {sourceGroupIndex:N0}/{sourceGroups.Count:N0}");
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT INTO Assets (ProjectId, SourceFileId, UniqueID, Name, Type, Container, PathID, ByteStart, ByteSize)
                    VALUES (@projectId, @sourceFileId, @uniqueId, @name, @type, @container, @pathId, @byteStart, @byteSize)
                    ON CONFLICT(ProjectId, UniqueID)
                    DO UPDATE SET
                        SourceFileId = excluded.SourceFileId,
                        Name = excluded.Name,
                        Type = excluded.Type,
                        Container = excluded.Container,
                        PathID = excluded.PathID,
                        ByteStart = excluded.ByteStart,
                        ByteSize = excluded.ByteSize";

                var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pSourceFileId = cmd.Parameters.Add("@sourceFileId", SqliteType.Integer);
                var pUniqueId = cmd.Parameters.Add("@uniqueId", SqliteType.Text);
                var pName = cmd.Parameters.Add("@name", SqliteType.Text);
                var pType = cmd.Parameters.Add("@type", SqliteType.Integer);
                var pContainer = cmd.Parameters.Add("@container", SqliteType.Text);
                var pPathId = cmd.Parameters.Add("@pathId", SqliteType.Integer);
                var pByteStart = cmd.Parameters.Add("@byteStart", SqliteType.Integer);
                var pByteSize = cmd.Parameters.Add("@byteSize", SqliteType.Integer);

                pProjectId.Value = projectId;
                var assetRowIndex = 0;
                // Insert in PRIMARY KEY order so the WITHOUT ROWID B-tree is filled by sequential appends
                // instead of random page splits (SQLite BINARY collation ~ ordinal comparison).
                foreach (var handle in handles.OrderBy(h => h.UniqueID ?? string.Empty, StringComparer.Ordinal))
                {
                    var sourceKey = GetSourceFileKey(handle.SerializedFileName ?? string.Empty, handle.OriginalPath ?? string.Empty);
                    pSourceFileId.Value = sourceFileIds.TryGetValue(sourceKey, out var sourceFileId)
                        ? sourceFileId
                        : DBNull.Value;
                    pUniqueId.Value = handle.UniqueID ?? string.Empty;
                    pName.Value = handle.Name ?? string.Empty;
                    pType.Value = (int)handle.Type;
                    pContainer.Value = handle.Container ?? string.Empty;
                    pPathId.Value = handle.PathID;
                    pByteStart.Value = handle.ByteStart;
                    pByteSize.Value = handle.ByteSize;
                    cmd.ExecuteNonQuery();
                    assetRowIndex++;
                    advanceProgress?.Invoke($"Saving asset rows: {assetRowIndex:N0}/{handles.Count:N0}");
                }
            }
        }

        private static string GetSourceFileKey(string serializedFileName, string originalPath)
        {
            return $"{serializedFileName}\u001f{originalPath}";
        }
    }
}
