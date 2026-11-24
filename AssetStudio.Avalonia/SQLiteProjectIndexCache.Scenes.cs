using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public sealed class SourceFileExternalRecord
    {
        public long SourceFileId { get; init; }
        public string SourceSerializedFileName { get; init; } = string.Empty;
        public string SourceOriginalPath { get; init; } = string.Empty;
        public int ExternalIndex { get; init; }
        public string ExternalFileName { get; init; } = string.Empty;
        public string ExternalPathName { get; init; } = string.Empty;
        public string ExternalGuid { get; init; } = string.Empty;
    }

    public sealed class IndexedSceneRecord
    {
        public int SceneId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ScenePath { get; init; } = string.Empty;
        public int BuildIndex { get; init; } = -1;
        public long EntrySourceFileId { get; init; }
        public string EntrySerializedFileName { get; init; } = string.Empty;
        public string EntryOriginalPath { get; init; } = string.Empty;
        public string ContainerKey { get; init; } = string.Empty;
        public string SourceKind { get; init; } = string.Empty;
    }

    public partial class SQLiteProjectIndexCache
    {
        public long? TryGetProjectId(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return null;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            if (!File.Exists(dbPath))
            {
                return null;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    return TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out _);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to resolve project id from SQLite: {ex.Message}");
                return null;
            }
        }

        public bool SaveSourceFileExternals(
            string folderPath,
            string signature,
            IReadOnlyList<SourceFileExternalRecord> externals,
            bool replaceExisting = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    var projectId = FindProjectId(conn, null, folderPath, signature, _caseSensitivePaths);
                    if (projectId == null)
                    {
                        return false;
                    }

                    return SaveSourceFileExternalsOnConnection(conn, dbPath, projectId.Value, externals, replaceExisting);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save SourceFileExternals to SQLite: {ex.Message}");
                return false;
            }
        }

        public bool SaveSourceFileExternals(
            string folderPath,
            long projectId,
            IReadOnlyList<SourceFileExternalRecord> externals,
            bool replaceExisting = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || projectId <= 0)
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    return SaveSourceFileExternalsOnConnection(conn, dbPath, projectId, externals, replaceExisting);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save SourceFileExternals to SQLite: {ex.Message}");
                return false;
            }
        }

        public List<SourceFileExternalRecord> GetSourceFileExternals(
            string folderPath,
            string signature,
            string? sourceSerializedFileName = null)
        {
            var result = new List<SourceFileExternalRecord>();
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "SourceFileExternals", (conn, tx, projectId) =>
            {
                ReadSourceFileExternalsCore(conn, tx, projectId, sourceFileId: null, sourceSerializedFileName, result);
            });
            return result;
        }

        public List<SourceFileExternalRecord> GetSourceFileExternals(
            string folderPath,
            long projectId,
            long? sourceFileId = null,
            string? sourceSerializedFileName = null)
        {
            var result = new List<SourceFileExternalRecord>();
            if (string.IsNullOrWhiteSpace(folderPath) || projectId <= 0)
            {
                return result;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            if (!File.Exists(dbPath))
            {
                return result;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    ReadSourceFileExternalsCore(conn, null, projectId, sourceFileId, sourceSerializedFileName, result);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to read SourceFileExternals from SQLite: {ex.Message}");
            }

            return result;
        }

        public List<SourceFileExternalRecord> GetSourceFileExternals(
            int projectId,
            long? sourceFileId = null,
            string? sourceSerializedFileName = null)
        {
            var result = new List<SourceFileExternalRecord>();
            if (projectId <= 0)
            {
                return result;
            }

            var dbPath = TryFindDbPathForProjectId(projectId);
            if (string.IsNullOrEmpty(dbPath))
            {
                return result;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    ReadSourceFileExternalsCore(conn, null, projectId, sourceFileId, sourceSerializedFileName, result);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to read SourceFileExternals by project id from SQLite: {ex.Message}");
            }

            return result;
        }

        public List<string> GetTransitiveExternalFileNames(
            string folderPath,
            string signature,
            IEnumerable<string> seedSourceFiles,
            int maxDepth = 4)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature) || seedSourceFiles == null || maxDepth <= 0)
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "transitive SourceFileExternals", (conn, tx, projectId) =>
            {
                result.AddRange(ComputeTransitiveExternalFileNamesCore(conn, tx, projectId, seedSourceFiles, maxDepth));
            });
            return result;
        }

        public List<string> GetTransitiveExternalFileNames(
            string folderPath,
            long projectId,
            IEnumerable<string> seedSourceFiles,
            int maxDepth = 4)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || projectId <= 0 || seedSourceFiles == null || maxDepth <= 0)
            {
                return new List<string>();
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            if (!File.Exists(dbPath))
            {
                return new List<string>();
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    return ComputeTransitiveExternalFileNamesCore(conn, null, projectId, seedSourceFiles, maxDepth);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to resolve transitive SourceFileExternals from SQLite: {ex.Message}");
                return new List<string>();
            }
        }

        public List<string> GetTransitiveExternalFileNames(
            string folderPath,
            int projectId,
            IEnumerable<string> seedSourceFiles,
            int maxDepth = 4)
        {
            return GetTransitiveExternalFileNames(folderPath, (long)projectId, seedSourceFiles, maxDepth);
        }

        public List<string> GetTransitiveExternalFileNames(
            int projectId,
            IEnumerable<string> seedSourceFiles,
            int maxDepth = 4)
        {
            if (projectId <= 0 || seedSourceFiles == null || maxDepth <= 0)
            {
                return new List<string>();
            }

            var dbPath = TryFindDbPathForProjectId(projectId);
            if (string.IsNullOrEmpty(dbPath))
            {
                return new List<string>();
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    return ComputeTransitiveExternalFileNamesCore(conn, null, projectId, seedSourceFiles, maxDepth);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to resolve transitive SourceFileExternals by project id: {ex.Message}");
                return new List<string>();
            }
        }

        public bool SaveScenes(
            string folderPath,
            string signature,
            IReadOnlyList<IndexedSceneRecord> scenes,
            bool replaceExisting = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    var projectId = FindProjectId(conn, null, folderPath, signature, _caseSensitivePaths);
                    if (projectId == null)
                    {
                        return false;
                    }

                    return SaveScenesOnConnection(conn, dbPath, projectId.Value, scenes, replaceExisting);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save Scenes to SQLite: {ex.Message}");
                return false;
            }
        }

        public bool SaveScenes(
            string folderPath,
            long projectId,
            IReadOnlyList<IndexedSceneRecord> scenes,
            bool replaceExisting = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || projectId <= 0)
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    return SaveScenesOnConnection(conn, dbPath, projectId, scenes, replaceExisting);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save Scenes to SQLite: {ex.Message}");
                return false;
            }
        }

        public List<IndexedSceneRecord> GetIndexedScenes(string folderPath, string signature)
        {
            var result = new List<IndexedSceneRecord>();
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "Scenes", (conn, tx, projectId) =>
            {
                ReadIndexedScenesCore(conn, tx, projectId, result);
            });
            return result;
        }

        public List<IndexedSceneRecord> GetIndexedScenes(string folderPath, long projectId)
        {
            var result = new List<IndexedSceneRecord>();
            if (string.IsNullOrWhiteSpace(folderPath) || projectId <= 0)
            {
                return result;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            if (!File.Exists(dbPath))
            {
                return result;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    ReadIndexedScenesCore(conn, null, projectId, result);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load Scenes from SQLite: {ex.Message}");
            }

            return result;
        }

        public List<IndexedSceneRecord> GetIndexedScenes(string folderPath, int projectId)
        {
            return GetIndexedScenes(folderPath, (long)projectId);
        }

        public List<IndexedSceneRecord> GetIndexedScenes(int projectId)
        {
            var result = new List<IndexedSceneRecord>();
            if (projectId <= 0)
            {
                return result;
            }

            var dbPath = TryFindDbPathForProjectId(projectId);
            if (string.IsNullOrEmpty(dbPath))
            {
                return result;
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    ReadIndexedScenesCore(conn, null, projectId, result);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load Scenes by project id from SQLite: {ex.Message}");
            }

            return result;
        }

        public Dictionary<string, string> GetSourceFilePathsByNames(
            string folderPath,
            string signature,
            IEnumerable<string> serializedFileNames)
        {
            var result = new Dictionary<string, string>(_pathComparer);
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature) || serializedFileNames == null)
            {
                return result;
            }

            var requested = new HashSet<string>(_pathComparer);
            foreach (var raw in serializedFileNames)
            {
                var leaf = NormalizeExternalLeafName(raw);
                if (!string.IsNullOrEmpty(leaf))
                {
                    requested.Add(leaf);
                }
            }

            if (requested.Count == 0)
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "SourceFile paths by name", (conn, tx, projectId) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    SELECT SerializedFileName, OriginalPath
                    FROM SourceFiles
                    WHERE ProjectId = @projectId;";
                cmd.Parameters.AddWithValue("@projectId", projectId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var sfName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var origPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (string.IsNullOrWhiteSpace(origPath))
                    {
                        continue;
                    }

                    var normalizedSf = NormalizeExternalLeafName(sfName);
                    if (!string.IsNullOrEmpty(normalizedSf) && requested.Contains(normalizedSf))
                    {
                        result.TryAdd(normalizedSf, origPath);
                    }

                    var origLeaf = NormalizeExternalLeafName(origPath);
                    if (!string.IsNullOrEmpty(origLeaf) && requested.Contains(origLeaf))
                    {
                        result.TryAdd(origLeaf, origPath);
                    }
                }
            });

            return result;
        }

        private bool SaveSourceFileExternalsOnConnection(
            SqliteConnection conn,
            string dbPath,
            long projectId,
            IReadOnlyList<SourceFileExternalRecord>? externals,
            bool replaceExisting)
        {
            SetSynchronousMode(conn, "OFF");
            try
            {
            using var transaction = conn.BeginTransaction();
            var lookup = LoadSourceFileIdLookup(conn, transaction, projectId);

            if (replaceExisting)
            {
                using var clearCmd = conn.CreateCommand();
                clearCmd.Transaction = transaction;
                clearCmd.CommandText = "DELETE FROM SourceFileExternals WHERE ProjectId = @projectId;";
                clearCmd.Parameters.AddWithValue("@projectId", projectId);
                clearCmd.ExecuteNonQuery();
            }

            if (externals != null && externals.Count > 0)
            {
                var prepared = new List<(long SourceFileId, int ExternalIndex, string ExternalFileName, string ExternalPathName, string ExternalGuid)>(externals.Count);
                var touchedSourceIds = replaceExisting ? null : new HashSet<long>();

                for (var i = 0; i < externals.Count; i++)
                {
                    var rec = externals[i];
                    if (rec == null)
                    {
                        continue;
                    }

                    var sourceFileId = rec.SourceFileId > 0
                        ? rec.SourceFileId
                        : ResolveOrInsertSourceFileId(conn, transaction, projectId, lookup, rec.SourceSerializedFileName, rec.SourceOriginalPath);
                    if (sourceFileId <= 0)
                    {
                        continue;
                    }

                    var extPath = rec.ExternalPathName ?? string.Empty;
                    var extFile = !string.IsNullOrWhiteSpace(rec.ExternalFileName)
                        ? rec.ExternalFileName.Trim()
                        : NormalizeExternalLeafName(extPath);
                    var extGuid = rec.ExternalGuid ?? string.Empty;

                    prepared.Add((sourceFileId, Math.Max(0, rec.ExternalIndex), extFile, extPath, extGuid));
                    touchedSourceIds?.Add(sourceFileId);
                }

                if (touchedSourceIds is { Count: > 0 })
                {
                    using var delPerSource = conn.CreateCommand();
                    delPerSource.Transaction = transaction;
                    delPerSource.CommandText = "DELETE FROM SourceFileExternals WHERE ProjectId = @projectId AND SourceFileId = @sourceFileId;";
                    var pDelProj = delPerSource.Parameters.Add("@projectId", SqliteType.Integer);
                    var pDelSrc = delPerSource.Parameters.Add("@sourceFileId", SqliteType.Integer);
                    pDelProj.Value = projectId;
                    foreach (var sid in touchedSourceIds)
                    {
                        pDelSrc.Value = sid;
                        delPerSource.ExecuteNonQuery();
                    }
                }

                // Insert in PK order (ProjectId, SourceFileId, ExternalIndex) for sequential WITHOUT ROWID B-tree writes.
                prepared.Sort(static (a, b) =>
                {
                    var cmp = a.SourceFileId.CompareTo(b.SourceFileId);
                    return cmp != 0 ? cmp : a.ExternalIndex.CompareTo(b.ExternalIndex);
                });

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT OR REPLACE INTO SourceFileExternals (
                        ProjectId, SourceFileId, ExternalIndex, ExternalFileName, ExternalPathName, ExternalGuid)
                    VALUES (
                        @projectId, @sourceFileId, @externalIndex, @externalFileName, @externalPathName, @externalGuid);";
                var pProj = insertCmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pSrc = insertCmd.Parameters.Add("@sourceFileId", SqliteType.Integer);
                var pIdx = insertCmd.Parameters.Add("@externalIndex", SqliteType.Integer);
                var pFile = insertCmd.Parameters.Add("@externalFileName", SqliteType.Text);
                var pPath = insertCmd.Parameters.Add("@externalPathName", SqliteType.Text);
                var pGuid = insertCmd.Parameters.Add("@externalGuid", SqliteType.Text);
                pProj.Value = projectId;

                for (var i = 0; i < prepared.Count; i++)
                {
                    var row = prepared[i];
                    pSrc.Value = row.SourceFileId;
                    pIdx.Value = row.ExternalIndex;
                    pFile.Value = row.ExternalFileName;
                    pPath.Value = row.ExternalPathName;
                    pGuid.Value = row.ExternalGuid;
                    insertCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            }
            finally
            {
                SetSynchronousMode(conn, "NORMAL");
            }
            NotifyDatabaseMutated(dbPath);
            return true;
        }

        private bool SaveScenesOnConnection(
            SqliteConnection conn,
            string dbPath,
            long projectId,
            IReadOnlyList<IndexedSceneRecord>? scenes,
            bool replaceExisting)
        {
            using var transaction = conn.BeginTransaction();
            var lookup = LoadSourceFileIdLookup(conn, transaction, projectId);

            if (replaceExisting)
            {
                using var clearCmd = conn.CreateCommand();
                clearCmd.Transaction = transaction;
                clearCmd.CommandText = "DELETE FROM Scenes WHERE ProjectId = @projectId;";
                clearCmd.Parameters.AddWithValue("@projectId", projectId);
                clearCmd.ExecuteNonQuery();
            }

            if (scenes != null && scenes.Count > 0)
            {
                var usedIds = new HashSet<int>();
                if (!replaceExisting)
                {
                    using var existingCmd = conn.CreateCommand();
                    existingCmd.Transaction = transaction;
                    existingCmd.CommandText = "SELECT SceneId FROM Scenes WHERE ProjectId = @projectId;";
                    existingCmd.Parameters.AddWithValue("@projectId", projectId);
                    using var reader = existingCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        usedIds.Add(reader.GetInt32(0));
                    }
                }

                var ordered = scenes
                    .Where(static s => s != null)
                    .OrderBy(static s => s.BuildIndex >= 0 ? 0 : 1)
                    .ThenBy(static s => s.BuildIndex)
                    .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static s => s.ScenePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static s => s.ContainerKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var nextAutoId = 1;
                var rows = new List<(int SceneId, string Name, string ScenePath, int BuildIndex, long EntrySourceFileId, string ContainerKey, string SourceKind)>(ordered.Count);

                foreach (var scene in ordered)
                {
                    int sceneId;
                    if (scene.SceneId > 0 && usedIds.Add(scene.SceneId))
                    {
                        sceneId = scene.SceneId;
                    }
                    else
                    {
                        while (!usedIds.Add(nextAutoId))
                        {
                            nextAutoId++;
                        }
                        sceneId = nextAutoId++;
                    }

                    var entrySourceFileId = scene.EntrySourceFileId > 0
                        ? scene.EntrySourceFileId
                        : ResolveSourceFileId(lookup, scene.EntrySerializedFileName, scene.EntryOriginalPath);

                    rows.Add((
                        sceneId,
                        scene.Name ?? string.Empty,
                        scene.ScenePath ?? string.Empty,
                        scene.BuildIndex,
                        entrySourceFileId,
                        scene.ContainerKey ?? string.Empty,
                        scene.SourceKind ?? string.Empty));
                }

                rows.Sort(static (a, b) => a.SceneId.CompareTo(b.SceneId));

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT OR REPLACE INTO Scenes (
                        ProjectId, SceneId, Name, ScenePath, BuildIndex, EntrySourceFileId, ContainerKey, SourceKind)
                    VALUES (
                        @projectId, @sceneId, @name, @scenePath, @buildIndex, @entrySourceFileId, @containerKey, @sourceKind);";
                var pProj = insertCmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pSceneId = insertCmd.Parameters.Add("@sceneId", SqliteType.Integer);
                var pName = insertCmd.Parameters.Add("@name", SqliteType.Text);
                var pPath = insertCmd.Parameters.Add("@scenePath", SqliteType.Text);
                var pBuildIdx = insertCmd.Parameters.Add("@buildIndex", SqliteType.Integer);
                var pEntrySrc = insertCmd.Parameters.Add("@entrySourceFileId", SqliteType.Integer);
                var pContainer = insertCmd.Parameters.Add("@containerKey", SqliteType.Text);
                var pKind = insertCmd.Parameters.Add("@sourceKind", SqliteType.Text);
                pProj.Value = projectId;

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    pSceneId.Value = row.SceneId;
                    pName.Value = row.Name;
                    pPath.Value = row.ScenePath;
                    pBuildIdx.Value = row.BuildIndex;
                    pEntrySrc.Value = row.EntrySourceFileId;
                    pContainer.Value = row.ContainerKey;
                    pKind.Value = row.SourceKind;
                    insertCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            NotifyDatabaseMutated(dbPath);
            return true;
        }

        private void ReadSourceFileExternalsCore(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            long projectId,
            long? sourceFileId,
            string? sourceSerializedFileName,
            List<SourceFileExternalRecord> output)
        {
            var normalizedFilter = string.IsNullOrWhiteSpace(sourceSerializedFileName)
                ? null
                : NormalizeExternalLeafName(sourceSerializedFileName);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sourceFileId.HasValue
                ? @"
                    SELECT ext.SourceFileId, COALESCE(sf.SerializedFileName, ''), COALESCE(sf.OriginalPath, ''),
                           ext.ExternalIndex, ext.ExternalFileName, ext.ExternalPathName, ext.ExternalGuid
                    FROM SourceFileExternals ext
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = ext.ProjectId
                     AND sf.Id = ext.SourceFileId
                    WHERE ext.ProjectId = @projectId
                      AND ext.SourceFileId = @sourceFileId
                    ORDER BY ext.SourceFileId, ext.ExternalIndex;"
                : @"
                    SELECT ext.SourceFileId, COALESCE(sf.SerializedFileName, ''), COALESCE(sf.OriginalPath, ''),
                           ext.ExternalIndex, ext.ExternalFileName, ext.ExternalPathName, ext.ExternalGuid
                    FROM SourceFileExternals ext
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = ext.ProjectId
                     AND sf.Id = ext.SourceFileId
                    WHERE ext.ProjectId = @projectId
                    ORDER BY ext.SourceFileId, ext.ExternalIndex;";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            if (sourceFileId.HasValue)
            {
                cmd.Parameters.AddWithValue("@sourceFileId", sourceFileId.Value);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var srcName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (normalizedFilter != null
                    && !string.Equals(NormalizeExternalLeafName(srcName), normalizedFilter, _pathComparison))
                {
                    continue;
                }

                output.Add(new SourceFileExternalRecord
                {
                    SourceFileId = reader.GetInt64(0),
                    SourceSerializedFileName = srcName,
                    SourceOriginalPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ExternalIndex = reader.GetInt32(3),
                    ExternalFileName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    ExternalPathName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    ExternalGuid = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
                });
            }
        }

        private List<string> ComputeTransitiveExternalFileNamesCore(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            long projectId,
            IEnumerable<string> seedSourceFiles,
            int maxDepth)
        {
            var adjacency = new Dictionary<string, List<string>>(_pathComparer);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT COALESCE(sf.SerializedFileName, ''), ext.ExternalFileName, ext.ExternalPathName
                    FROM SourceFileExternals ext
                    JOIN SourceFiles sf
                      ON sf.ProjectId = ext.ProjectId
                     AND sf.Id = ext.SourceFileId
                    WHERE ext.ProjectId = @projectId
                    ORDER BY ext.SourceFileId, ext.ExternalIndex;";
                cmd.Parameters.AddWithValue("@projectId", projectId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var srcLeaf = NormalizeExternalLeafName(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                    if (string.IsNullOrEmpty(srcLeaf))
                    {
                        continue;
                    }

                    var rawExtFile = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var rawExtPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var extLeaf = NormalizeExternalLeafName(!string.IsNullOrWhiteSpace(rawExtFile) ? rawExtFile : rawExtPath);
                    if (string.IsNullOrEmpty(extLeaf) || IsStreamingSidecarName(extLeaf))
                    {
                        continue;
                    }

                    if (!adjacency.TryGetValue(srcLeaf, out var targets))
                    {
                        targets = new List<string>();
                        adjacency[srcLeaf] = targets;
                    }

                    if (!targets.Contains(extLeaf, _pathComparer))
                    {
                        targets.Add(extLeaf);
                    }
                }
            }

            var visited = new HashSet<string>(_pathComparer);
            var queue = new Queue<(string FileName, int Depth)>();
            foreach (var rawSeed in seedSourceFiles)
            {
                var seedLeaf = NormalizeExternalLeafName(rawSeed);
                if (!string.IsNullOrEmpty(seedLeaf) && visited.Add(seedLeaf))
                {
                    queue.Enqueue((seedLeaf, 0));
                }
            }

            var discovered = new List<string>();
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (depth >= maxDepth)
                {
                    continue;
                }

                if (!adjacency.TryGetValue(current, out var nextTargets))
                {
                    continue;
                }

                for (var i = 0; i < nextTargets.Count; i++)
                {
                    var next = nextTargets[i];
                    if (visited.Add(next))
                    {
                        discovered.Add(next);
                        queue.Enqueue((next, depth + 1));
                    }
                }
            }

            return discovered;
        }

        private static void ReadIndexedScenesCore(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            long projectId,
            List<IndexedSceneRecord> output)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT s.SceneId, s.Name, s.ScenePath, s.BuildIndex, s.EntrySourceFileId,
                       COALESCE(sf.SerializedFileName, ''), COALESCE(sf.OriginalPath, ''),
                       s.ContainerKey, s.SourceKind
                FROM Scenes s
                LEFT JOIN SourceFiles sf
                  ON sf.ProjectId = s.ProjectId
                 AND sf.Id = s.EntrySourceFileId
                WHERE s.ProjectId = @projectId
                ORDER BY CASE WHEN s.BuildIndex >= 0 THEN 0 ELSE 1 END, s.BuildIndex ASC, s.Name ASC, s.SceneId ASC;";
            cmd.Parameters.AddWithValue("@projectId", projectId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                output.Add(new IndexedSceneRecord
                {
                    SceneId = reader.GetInt32(0),
                    Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ScenePath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    BuildIndex = reader.GetInt32(3),
                    EntrySourceFileId = reader.GetInt64(4),
                    EntrySerializedFileName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    EntryOriginalPath = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    ContainerKey = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    SourceKind = reader.IsDBNull(8) ? string.Empty : reader.GetString(8)
                });
            }
        }

        private sealed class SourceFileIdLookup
        {
            public SourceFileIdLookup(StringComparer comparer)
            {
                ByCompositeKey = new Dictionary<string, long>(comparer);
                ByOriginalPathKey = new Dictionary<string, long>(comparer);
                BySerializedFileName = new Dictionary<string, long>(comparer);
            }

            public Dictionary<string, long> ByCompositeKey { get; }
            public Dictionary<string, long> ByOriginalPathKey { get; }
            public Dictionary<string, long> BySerializedFileName { get; }
        }

        private SourceFileIdLookup LoadSourceFileIdLookup(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId)
        {
            var lookup = new SourceFileIdLookup(_pathComparer);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id, SerializedFileName, OriginalPath
                FROM SourceFiles
                WHERE ProjectId = @projectId;";
            cmd.Parameters.AddWithValue("@projectId", projectId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var sfName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var origPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                RegisterSourceFileLookup(lookup, id, sfName, origPath);
            }

            return lookup;
        }

        private void RegisterSourceFileLookup(SourceFileIdLookup lookup, long id, string sfName, string origPath)
        {
            var normalizedOrig = string.IsNullOrWhiteSpace(origPath)
                ? string.Empty
                : NormalizeFolderPathKey(origPath, _caseSensitivePaths);
            var normalizedSf = NormalizeExternalLeafName(sfName);

            if (!string.IsNullOrEmpty(normalizedSf) && !string.IsNullOrEmpty(normalizedOrig))
            {
                lookup.ByCompositeKey.TryAdd(GetSourceFileKey(normalizedSf, normalizedOrig), id);
            }

            if (!string.IsNullOrEmpty(normalizedOrig))
            {
                lookup.ByOriginalPathKey.TryAdd(normalizedOrig, id);
            }

            if (!string.IsNullOrEmpty(normalizedSf))
            {
                lookup.BySerializedFileName.TryAdd(normalizedSf, id);
            }
        }

        private long ResolveSourceFileId(SourceFileIdLookup lookup, string? serializedFileName, string? originalPath)
        {
            var normalizedSf = NormalizeExternalLeafName(serializedFileName);
            var normalizedOrig = string.IsNullOrWhiteSpace(originalPath)
                ? string.Empty
                : NormalizeFolderPathKey(originalPath, _caseSensitivePaths);

            if (!string.IsNullOrEmpty(normalizedSf)
                && !string.IsNullOrEmpty(normalizedOrig)
                && lookup.ByCompositeKey.TryGetValue(GetSourceFileKey(normalizedSf, normalizedOrig), out var exactId))
            {
                return exactId;
            }

            if (!string.IsNullOrEmpty(normalizedSf)
                && lookup.BySerializedFileName.TryGetValue(normalizedSf, out var byNameId))
            {
                return byNameId;
            }

            if (!string.IsNullOrEmpty(normalizedOrig)
                && lookup.ByOriginalPathKey.TryGetValue(normalizedOrig, out var byOrigId))
            {
                return byOrigId;
            }

            return 0;
        }

        private long ResolveOrInsertSourceFileId(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            SourceFileIdLookup lookup,
            string? serializedFileName,
            string? originalPath)
        {
            var existingId = ResolveSourceFileId(lookup, serializedFileName, originalPath);
            if (existingId > 0)
            {
                return existingId;
            }

            var sfName = NormalizeExternalLeafName(serializedFileName);
            var origPath = originalPath ?? string.Empty;
            if (string.IsNullOrEmpty(sfName))
            {
                sfName = NormalizeExternalLeafName(origPath);
            }

            if (string.IsNullOrEmpty(sfName))
            {
                return 0;
            }

            using var upsertCmd = conn.CreateCommand();
            upsertCmd.Transaction = transaction;
            upsertCmd.CommandText = @"
                INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                VALUES (@projectId, @serializedFileName, @originalPath, '', 0)
                ON CONFLICT(ProjectId, SerializedFileName, OriginalPath)
                DO UPDATE SET LastSeen = CURRENT_TIMESTAMP
                RETURNING Id;";
            upsertCmd.Parameters.AddWithValue("@projectId", projectId);
            upsertCmd.Parameters.AddWithValue("@serializedFileName", sfName);
            upsertCmd.Parameters.AddWithValue("@originalPath", origPath);
            var idObj = upsertCmd.ExecuteScalar();
            if (idObj == null || idObj == DBNull.Value)
            {
                return 0;
            }

            var newId = Convert.ToInt64(idObj);
            RegisterSourceFileLookup(lookup, newId, sfName, origPath);
            return newId;
        }

        private string? TryFindDbPathForProjectId(long projectId)
        {
            foreach (var entry in _projectIdCache)
            {
                if (entry.Value == projectId)
                {
                    var sep = entry.Key.IndexOf('\u001f');
                    if (sep > 0)
                    {
                        var candidateDb = entry.Key.Substring(0, sep);
                        if (File.Exists(candidateDb))
                        {
                            return candidateDb;
                        }
                    }
                }
            }

            foreach (var dbPath in EnumerateCacheDatabasePaths())
            {
                try
                {
                    using var lease = RentReadConnection(dbPath);
                    var conn = lease.Connection;
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT 1 FROM Projects WHERE Id = @projectId LIMIT 1;";
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        if (cmd.ExecuteScalar() != null)
                        {
                            return dbPath;
                        }
                    }
                    catch
                    {
                        lease.MarkUnhealthy();
                    }
                }
                catch
                {
                    // Skip unreadable cache databases
                }
            }

            return null;
        }

        internal static string NormalizeExternalLeafName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return string.Empty;
            }

            var trimmed = rawName.Trim().Replace('\\', '/').TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static bool IsStreamingSidecarName(string fileName)
        {
            return fileName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
        }
    }
}
