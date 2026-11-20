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
            EnsureInitialized(folderPath);
            try
            {
                using (var conn = CreateReadConnection(folderPath))
                {
                    long? projectId = null;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Id FROM Projects WHERE FolderPath = @path AND SignatureHash = @signature LIMIT 1";
                        cmd.Parameters.AddWithValue("@path", folderPath);
                        cmd.Parameters.AddWithValue("@signature", signature);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                projectId = reader.GetInt64(0);
                            }
                        }
                    }

                    if (projectId == null)
                    {
                        return null;
                    }

                    var indexingStatus = LoadProjectIndexingStatus(conn, projectId.Value);
                    if (IsIncompleteIndexCacheStatus(indexingStatus))
                    {
                        return null;
                    }

                    var handles = new List<AssetHandle>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            SELECT UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize
                            FROM AssetHandles
                            WHERE ProjectId = @projectId";
                        cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                handles.Add(new AssetHandle
                                {
                                    UniqueID = reader.GetString(0),
                                    Name = reader.GetString(1),
                                    Type = (ClassIDType)reader.GetInt32(2),
                                    Container = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                    OriginalPath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                    SerializedFileName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                                    PathID = reader.GetInt64(6),
                                    ByteStart = reader.GetInt64(7),
                                    ByteSize = reader.GetInt64(8)
                                });
                            }
                        }
                    }
                    return handles.Count == 0 ? null : handles;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load SQLite index cache: {ex.Message}");
                return null;
            }
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
            try
            {
                var handleList = handles?.ToList() ?? new List<AssetHandle>();
                var totalWork = Math.Max(1, handleList.Count * 2 + Math.Max(1, scanResult.TotalFiles));
                var reporter = new ThrottledProgressReporter(totalWork, progress);

                reporter.Report("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    reporter.Report("Opening SQLite connection", force: true);
                    using (var conn = CreateConnection(folderPath))
                    {
                        using (var transaction = conn.BeginTransaction())
                        {
                            reporter.Report("Preparing SQLite project row", force: true);
                            var projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult, unityVersion);
                            reporter.Report("Clearing previous index rows", force: true);
                            ClearIndexTablesForProject(conn, transaction, projectId, preserveSemanticRelations);

                            // Insert handles in batch using parameterized query
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = @"
                                    INSERT INTO AssetHandles (ProjectId, UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize)
                                    VALUES (@projectId, @uniqueId, @name, @type, @container, @originalPath, @serializedFile, @pathId, @byteStart, @byteSize)";
                                cmd.Transaction = transaction;

                                var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
                                var pUniqueId = cmd.Parameters.Add("@uniqueId", SqliteType.Text);
                                var pName = cmd.Parameters.Add("@name", SqliteType.Text);
                                var pType = cmd.Parameters.Add("@type", SqliteType.Integer);
                                var pContainer = cmd.Parameters.Add("@container", SqliteType.Text);
                                var pOriginalPath = cmd.Parameters.Add("@originalPath", SqliteType.Text);
                                var pSerializedFile = cmd.Parameters.Add("@serializedFile", SqliteType.Text);
                                var pPathId = cmd.Parameters.Add("@pathId", SqliteType.Integer);
                                var pByteStart = cmd.Parameters.Add("@byteStart", SqliteType.Integer);
                                var pByteSize = cmd.Parameters.Add("@byteSize", SqliteType.Integer);

                                pProjectId.Value = projectId;

                                var savedHandleRows = 0;
                                foreach (var h in handleList)
                                {
                                    pUniqueId.Value = h.UniqueID ?? string.Empty;
                                    pName.Value = h.Name ?? string.Empty;
                                    pType.Value = (int)h.Type;
                                    pContainer.Value = h.Container ?? (object)DBNull.Value;
                                    pOriginalPath.Value = h.OriginalPath ?? (object)DBNull.Value;
                                    pSerializedFile.Value = h.SerializedFileName ?? (object)DBNull.Value;
                                    pPathId.Value = h.PathID;
                                    pByteStart.Value = h.ByteStart;
                                    pByteSize.Value = h.ByteSize;

                                    cmd.ExecuteNonQuery();
                                    savedHandleRows++;
                                    reporter.Advance($"Saving handles: {savedHandleRows:N0}/{handleList.Count:N0}");
                                }
                            }

                            InsertSourceFilesAndAssets(conn, transaction, projectId, handleList, reporter.Advance);

                            reporter.Report("Committing SQLite transaction", force: true);
                            transaction.Commit();
                            reporter.Report("Checkpointing SQLite WAL", force: true);
                            TryCheckpointWal(conn, "index cache");
                            reporter.MarkCompleted("SQLite index cache saved");
                            reporter.Report("SQLite index cache saved", force: true);
                            Logger.Info($"Saved index cache in SQLite for: {folderPath}");
                            return true;
                        }
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
                    "AssetHandles",
                    "Assets",
                    "SourceFiles"
                }
                : new[]
                {
                    "AssetHandles",
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
            foreach (var group in sourceGroups)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                        VALUES (@projectId, @serializedFileName, @originalPath, '', @objectCount)
                        ON CONFLICT(ProjectId, SerializedFileName, OriginalPath)
                        DO UPDATE SET ObjectCount = excluded.ObjectCount, LastSeen = CURRENT_TIMESTAMP";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@serializedFileName", group.Key.SerializedFileName);
                    cmd.Parameters.AddWithValue("@originalPath", group.Key.OriginalPath);
                    cmd.Parameters.AddWithValue("@objectCount", group.Count());
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT Id FROM SourceFiles
                        WHERE ProjectId = @projectId
                          AND SerializedFileName = @serializedFileName
                          AND OriginalPath = @originalPath
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@serializedFileName", group.Key.SerializedFileName);
                    cmd.Parameters.AddWithValue("@originalPath", group.Key.OriginalPath);
                    var id = cmd.ExecuteScalar();
                    if (id != null)
                    {
                        sourceFileIds[GetSourceFileKey(group.Key.SerializedFileName, group.Key.OriginalPath)] = Convert.ToInt64(id);
                    }
                }

                sourceGroupIndex++;
                advanceProgress?.Invoke($"Saving source files: {sourceGroupIndex:N0}/{sourceGroups.Count:N0}");
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
                foreach (var handle in handles)
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
