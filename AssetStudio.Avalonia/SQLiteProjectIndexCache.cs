using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public class SQLiteProjectIndexCache
    {
        private const int SemanticSchemaVersion = 5;
        private readonly string _dbPath;

        private readonly Task _initTask;

        public SQLiteProjectIndexCache()
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "IndexCache");
            Directory.CreateDirectory(cacheDir);
            _dbPath = Path.Combine(cacheDir, "project_index.db");
            _initTask = Task.Run(InitializeDatabase);
        }

        private void EnsureInitialized()
        {
            _initTask.Wait();
        }

        private SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.DefaultTimeout = 60;
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 60000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private void InitializeDatabase()
        {
            try
            {
                using (var conn = CreateConnection())
                {
                    using (var pragma = conn.CreateCommand())
                    {
                        pragma.CommandText = @"
                            PRAGMA journal_mode = WAL;
                            PRAGMA synchronous = NORMAL;";
                        pragma.ExecuteNonQuery();
                    }

                    if (ShouldRebuildSchema(conn))
                    {
                        RebuildSchema(conn);
                    }

                    CreateSchema(conn);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize SQLite database cache: {ex.Message}", ex);
            }
        }

        private static bool ShouldRebuildSchema(SqliteConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                var tableCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (tableCount == 0)
                {
                    return false;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'CacheSchema'";
                var hasSchemaTable = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                if (!hasSchemaTable)
                {
                    return true;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1";
                var versionText = cmd.ExecuteScalar()?.ToString();
                return !int.TryParse(versionText, out var version) || version != SemanticSchemaVersion;
            }
        }

        private static void RebuildSchema(SqliteConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    PRAGMA foreign_keys = OFF;
                    DROP TABLE IF EXISTS IndexingReadFiles;
                    DROP TABLE IF EXISTS ProjectIndexingState;
                    DROP TABLE IF EXISTS PreviewCacheEntries;
                    DROP TABLE IF EXISTS MaterialTextures;
                    DROP TABLE IF EXISTS MeshMaterials;
                    DROP TABLE IF EXISTS MeshRenderers;
                    DROP TABLE IF EXISTS AssetEdges;
                    DROP TABLE IF EXISTS Assets;
                    DROP TABLE IF EXISTS SourceFiles;
                    DROP TABLE IF EXISTS AssetHandles;
                    DROP TABLE IF EXISTS Projects;
                    DROP TABLE IF EXISTS CacheSchema;
                    PRAGMA foreign_keys = ON;";
                cmd.ExecuteNonQuery();
            }
        }

        private static void CreateSchema(SqliteConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                            CREATE TABLE IF NOT EXISTS Projects (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                FolderPath TEXT NOT NULL,
                                SignatureHash TEXT NOT NULL,
                                TotalFiles INTEGER NOT NULL,
                                TotalBytes INTEGER NOT NULL,
                                UnityBundleCount INTEGER NOT NULL,
                                LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                                UnityVersion TEXT
                            );

                            CREATE TABLE IF NOT EXISTS AssetHandles (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                UniqueID TEXT NOT NULL,
                                Name TEXT NOT NULL,
                                Type INTEGER NOT NULL,
                                Container TEXT,
                                OriginalPath TEXT,
                                SerializedFileName TEXT,
                                PathID INTEGER NOT NULL,
                                ByteStart INTEGER NOT NULL,
                                ByteSize INTEGER NOT NULL
                            );

                            CREATE INDEX IF NOT EXISTS idx_projects_path ON Projects(FolderPath);
                            CREATE INDEX IF NOT EXISTS idx_handles_project ON AssetHandles(ProjectId);
                            DROP INDEX IF EXISTS idx_handles_unique;

                            CREATE TABLE IF NOT EXISTS SourceFiles (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                SerializedFileName TEXT NOT NULL,
                                OriginalPath TEXT NOT NULL DEFAULT '',
                                UnityVersion TEXT NOT NULL DEFAULT '',
                                ObjectCount INTEGER NOT NULL DEFAULT 0,
                                LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP
                            );

                            CREATE TABLE IF NOT EXISTS Assets (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                SourceFileId INTEGER REFERENCES SourceFiles(Id) ON DELETE SET NULL,
                                UniqueID TEXT NOT NULL,
                                Name TEXT NOT NULL,
                                Type INTEGER NOT NULL,
                                Container TEXT NOT NULL DEFAULT '',
                                PathID INTEGER NOT NULL,
                                ByteStart INTEGER NOT NULL,
                                ByteSize INTEGER NOT NULL
                            );

                            CREATE TABLE IF NOT EXISTS AssetEdges (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
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

                            CREATE TABLE IF NOT EXISTS MeshRenderers (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MeshAssetUniqueID TEXT NOT NULL,
                                RendererAssetUniqueID TEXT NOT NULL,
                                RendererType TEXT NOT NULL,
                                GameObjectAssetUniqueID TEXT,
                                GameObjectName TEXT,
                                Description TEXT
                            );

                            CREATE TABLE IF NOT EXISTS MeshMaterials (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MeshAssetUniqueID TEXT NOT NULL,
                                MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererType TEXT NOT NULL DEFAULT '',
                                SubMeshIndex INTEGER NOT NULL,
                                MaterialSlotIndex INTEGER NOT NULL DEFAULT -1,
                                MaterialScore INTEGER NOT NULL DEFAULT 0
                            );

                            CREATE TABLE IF NOT EXISTS MaterialTextures (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MaterialAssetUniqueID TEXT NOT NULL,
                                PreviewMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                SlotName TEXT NOT NULL,
                                SlotIndex INTEGER NOT NULL,
                                TextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                                TextureFileId INTEGER NOT NULL DEFAULT 0,
                                TexturePathID INTEGER NOT NULL DEFAULT 0,
                                IsResolved INTEGER NOT NULL DEFAULT 0,
                                IsMainTexture INTEGER NOT NULL DEFAULT 0
                            );

                            CREATE TABLE IF NOT EXISTS PreviewCacheEntries (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                AssetUniqueID TEXT NOT NULL,
                                PreviewKind TEXT NOT NULL,
                                AlgorithmVersion INTEGER NOT NULL,
                                Parameters TEXT NOT NULL DEFAULT '',
                                PayloadHash TEXT NOT NULL,
                                PayloadPath TEXT,
                                ByteSize INTEGER NOT NULL DEFAULT 0,
                                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                                LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP
                            );

                            CREATE TABLE IF NOT EXISTS ProjectIndexingState (
                                ProjectId INTEGER PRIMARY KEY REFERENCES Projects(Id) ON DELETE CASCADE,
                                Status TEXT NOT NULL DEFAULT 'not_started',
                                TotalFiles INTEGER NOT NULL DEFAULT 0,
                                ProcessedFiles INTEGER NOT NULL DEFAULT 0,
                                PendingFiles INTEGER NOT NULL DEFAULT 0,
                                PercentComplete REAL NOT NULL DEFAULT 0,
                                CurrentFile TEXT NOT NULL DEFAULT '',
                                LastReadFile TEXT NOT NULL DEFAULT '',
                                StartedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                                CompletedAt DATETIME
                            );

                            CREATE TABLE IF NOT EXISTS IndexingReadFiles (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                FilePath TEXT NOT NULL,
                                FileName TEXT NOT NULL DEFAULT '',
                                ReadOrder INTEGER NOT NULL DEFAULT 0,
                                ReadAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                                Status TEXT NOT NULL DEFAULT 'read'
                            );

                            CREATE TABLE IF NOT EXISTS CacheSchema (
                                Key TEXT PRIMARY KEY,
                                Value TEXT NOT NULL
                            );

                            INSERT OR REPLACE INTO CacheSchema (Key, Value)
                            VALUES ('SemanticSchemaVersion', '{SemanticSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}');

                            CREATE INDEX IF NOT EXISTS idx_source_files_project ON SourceFiles(ProjectId);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_source_files_unique ON SourceFiles(ProjectId, SerializedFileName, OriginalPath);
                            CREATE INDEX IF NOT EXISTS idx_assets_project ON Assets(ProjectId);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_assets_unique ON Assets(ProjectId, UniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_asset_edges_unique ON AssetEdges(ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_source ON AssetEdges(ProjectId, SourceAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_target ON AssetEdges(ProjectId, TargetAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_mesh_renderers_unique ON MeshRenderers(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType);
                            CREATE INDEX IF NOT EXISTS idx_mesh_renderers_mesh ON MeshRenderers(ProjectId, MeshAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_mesh_materials_unique ON MeshMaterials(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_materials_mesh ON MeshMaterials(ProjectId, MeshAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_materials_renderer ON MeshMaterials(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_material_textures_unique ON MaterialTextures(ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_material ON MaterialTextures(ProjectId, MaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_preview_material ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_material_lookup ON MaterialTextures(ProjectId, MaterialAssetUniqueID, SlotName, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_preview_lookup ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID, SlotName, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_handles_lookup ON AssetHandles(ProjectId, UniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_preview_cache_unique ON PreviewCacheEntries(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters);
                            CREATE INDEX IF NOT EXISTS idx_indexing_read_files_project ON IndexingReadFiles(ProjectId, ReadOrder);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_indexing_read_files_unique ON IndexingReadFiles(ProjectId, FilePath);
                        ";
                cmd.ExecuteNonQuery();
            }
        }

        public string GetFolderSignature(ProjectScanResult scanResult)
        {
            return $"{scanResult.TotalFiles}_{scanResult.TotalBytes}_{scanResult.UnityBundleCount}";
        }

        public List<AssetHandle>? LoadIndexCache(string folderPath, string signature)
        {
            EnsureInitialized();
            try
            {
                using (var conn = CreateConnection())
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

                    // Update LastIndexed timestamp
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE Projects SET LastIndexed = CURRENT_TIMESTAMP WHERE Id = @id";
                        cmd.Parameters.AddWithValue("@id", projectId.Value);
                        cmd.ExecuteNonQuery();
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
                    return handles;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load SQLite index cache: {ex.Message}");
                return null;
            }
        }

        public void SaveIndexCache(
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            string unityVersion,
            IEnumerable<AssetHandle> handles,
            bool preserveSemanticRelations = false)
        {
            EnsureInitialized();
            try
            {
                var handleList = handles?.ToList() ?? new List<AssetHandle>();
                using (var conn = CreateConnection())
                {
                    using (var transaction = conn.BeginTransaction())
                    {
                        var projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult, unityVersion);
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
                            }
                        }

                        InsertSourceFilesAndAssets(conn, transaction, projectId, handleList);

                        transaction.Commit();
                        Logger.Info($"Saved index cache in SQLite for: {folderPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save SQLite index cache: {ex.Message}");
            }
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
                deleteOldCmd.Transaction = transaction;
                deleteOldCmd.CommandText = "DELETE FROM Projects WHERE FolderPath = @path";
                deleteOldCmd.Parameters.AddWithValue("@path", folderPath);
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

        private static void InsertSourceFilesAndAssets(SqliteConnection conn, SqliteTransaction transaction, long projectId, IReadOnlyCollection<AssetHandle> handles)
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
                }
            }
        }

        private static string GetSourceFileKey(string serializedFileName, string originalPath)
        {
            return $"{serializedFileName}\u001f{originalPath}";
        }

        internal bool SaveSemanticRelations(string folderPath, string signature, SemanticAssetRelations relations, bool replaceExisting = false)
        {
            if (relations == null || (!relations.HasRelations && relations.SourceFiles.Count == 0))
            {
                return false;
            }

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return false;
                }

                if (replaceExisting)
                {
                    ClearSemanticRelationTablesForProject(conn, transaction, projectId.Value);
                }

                InsertSemanticSourceFiles(conn, transaction, projectId.Value, relations.SourceFiles);
                InsertAssetEdges(conn, transaction, projectId.Value, relations.AssetEdges);
                InsertMeshRenderers(conn, transaction, projectId.Value, relations.MeshRenderers);
                InsertMeshMaterials(conn, transaction, projectId.Value, relations.MeshMaterials);
                InsertMaterialTextures(conn, transaction, projectId.Value, relations.MaterialTextures);

                transaction.Commit();
                return true;
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

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return;
                }

                ClearSemanticRelationTablesForProject(conn, transaction, projectId.Value);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to clear semantic asset relations: {ex.Message}");
            }
        }

        internal bool HasSemanticRelations(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return false;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var count = CountMaterialSemanticRelations(conn, transaction, projectId.Value);
                transaction.Commit();
                return count > 0;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to inspect semantic asset relations: {ex.Message}");
                return false;
            }
        }

        private static void ClearSemanticRelationTablesForProject(SqliteConnection conn, SqliteTransaction transaction, long projectId)
        {
            var tables = new[]
            {
                "MaterialTextures",
                "MeshMaterials",
                "MeshRenderers",
                "AssetEdges"
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

        private static long? FindProjectId(SqliteConnection conn, SqliteTransaction transaction, string folderPath, string signature)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT Id FROM Projects WHERE FolderPath = @path AND SignatureHash = @signature LIMIT 1";
            cmd.Parameters.AddWithValue("@path", folderPath);
            cmd.Parameters.AddWithValue("@signature", signature);
            var id = cmd.ExecuteScalar();
            return id == null ? null : Convert.ToInt64(id);
        }

        private static long? FindLatestProjectId(SqliteConnection conn, SqliteTransaction transaction, string folderPath)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id
                FROM Projects
                WHERE FolderPath = @path COLLATE NOCASE
                   OR FolderPath = @trimmedPath COLLATE NOCASE
                ORDER BY LastIndexed DESC, Id DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue("@path", folderPath);
            cmd.Parameters.AddWithValue("@trimmedPath", folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var id = cmd.ExecuteScalar();
            return id == null ? null : Convert.ToInt64(id);
        }

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

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
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

                using (var cmd = conn.CreateCommand())
                {
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
                    cmd.Parameters.AddWithValue("@status", update.Status ?? string.Empty);
                    cmd.Parameters.AddWithValue("@totalFiles", update.TotalFiles);
                    cmd.Parameters.AddWithValue("@processedFiles", update.ProcessedFiles);
                    cmd.Parameters.AddWithValue("@pendingFiles", update.PendingFiles);
                    cmd.Parameters.AddWithValue("@percentComplete", update.PercentComplete);
                    cmd.Parameters.AddWithValue("@currentFile", update.CurrentFile ?? string.Empty);
                    cmd.Parameters.AddWithValue("@lastReadFile", update.LastReadFile ?? string.Empty);
                    cmd.Parameters.AddWithValue("@completedAt", IsTerminalIndexingStatus(update.Status) ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

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

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return null;
                }

                var state = LoadIndexingState(conn, transaction, projectId.Value);
                transaction.Commit();
                return state;
            }
            catch (Exception ex)
            {
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

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindLatestProjectId(conn, transaction, folderPath);
                if (projectId == null)
                {
                    return null;
                }

                var state = LoadIndexingState(conn, transaction, projectId.Value);
                transaction.Commit();
                return state;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load latest indexing progress: {ex.Message}");
                return null;
            }
        }

        private static ProjectIndexingState? LoadIndexingState(SqliteConnection conn, SqliteTransaction transaction, long projectId)
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
                && CountMaterialSemanticRelations(conn, transaction, projectId) == 0)
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

        private static long CountMaterialSemanticRelations(SqliteConnection conn, SqliteTransaction transaction, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(1) FROM AssetEdges WHERE ProjectId = @projectId) +
                    (SELECT COUNT(1) FROM MeshMaterials WHERE ProjectId = @projectId) +
                    (SELECT COUNT(1) FROM MaterialTextures WHERE ProjectId = @projectId)";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
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

        private static DateTime? ReadNullableDateTime(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            return DateTime.TryParse(reader.GetString(ordinal), out var value)
                ? value
                : null;
        }

        private static void InsertSemanticSourceFiles(SqliteConnection conn, SqliteTransaction transaction, long projectId, IReadOnlyCollection<SemanticSourceFileEntry> sourceFiles)
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
            foreach (var sourceFile in sourceFiles)
            {
                pSerializedFileName.Value = sourceFile.SerializedFileName;
                pOriginalPath.Value = sourceFile.OriginalPath;
                pUnityVersion.Value = sourceFile.UnityVersion;
                pObjectCount.Value = sourceFile.ObjectCount;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertAssetEdges(SqliteConnection conn, SqliteTransaction transaction, long projectId, IReadOnlyCollection<SemanticAssetEdgeRelation> edges)
        {
            if (edges.Count == 0)
            {
                return;
            }

            var assetSourceFileIds = LoadAssetSourceFileIdMap(conn, transaction, projectId);
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
            foreach (var edge in edges)
            {
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

        private static void InsertMeshRenderers(SqliteConnection conn, SqliteTransaction transaction, long projectId, IReadOnlyCollection<SemanticMeshRendererRelation> renderers)
        {
            if (renderers.Count == 0)
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
            foreach (var renderer in renderers)
            {
                pMeshAssetId.Value = renderer.MeshAssetId;
                pRendererAssetId.Value = renderer.RendererAssetId;
                pRendererType.Value = renderer.RendererType;
                pGameObjectAssetId.Value = renderer.GameObjectAssetId;
                pGameObjectName.Value = renderer.GameObjectName;
                pDescription.Value = renderer.Description;
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertMeshMaterials(SqliteConnection conn, SqliteTransaction transaction, long projectId, IReadOnlyCollection<SemanticMeshMaterialRelation> materials)
        {
            if (materials.Count == 0)
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
            foreach (var material in materials)
            {
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

        private static void InsertMaterialTextures(SqliteConnection conn, SqliteTransaction transaction, long projectId, IReadOnlyCollection<SemanticMaterialTextureRelation> textures)
        {
            if (textures.Count == 0)
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
            foreach (var texture in textures)
            {
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

        internal List<string?> LoadMeshMaterialAssetIds(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<string?>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

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
                    RendererStats AS (
                        SELECT
                            mm.RendererAssetUniqueID,
                            MAX(CASE WHEN mm.RendererType <> 'Container' THEN 1 ELSE 0 END) AS RendererScore,
                            SUM(CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 1 ELSE 0 END) AS MaterialCount,
                            COUNT(*) AS RelationCount,
                            MAX(mm.MaterialScore) AS MaterialScore,
                            MAX(CASE WHEN EXISTS (
                                SELECT 1
                                FROM Assets materialAsset
                                WHERE materialAsset.ProjectId = mm.ProjectId
                                  AND materialAsset.UniqueID = mm.MaterialAssetUniqueID
                                  AND materialAsset.SourceFileId = (SELECT SourceFileId FROM MeshSource)
                            ) THEN 1 ELSE 0 END) AS SameSourceScore,
                            SUM(CASE WHEN EXISTS (
                                SELECT 1
                                FROM MaterialTextures mt
                                WHERE mt.ProjectId = mm.ProjectId
                                  AND mt.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                                  AND mt.TextureAssetUniqueID <> ''
                                  AND mt.IsMainTexture <> 0
                            ) THEN 1 ELSE 0 END) AS MainTextureCount,
                            SUM(CASE WHEN EXISTS (
                                SELECT 1
                                FROM MaterialTextures mt
                                WHERE mt.ProjectId = mm.ProjectId
                                  AND mt.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                                  AND mt.TextureAssetUniqueID <> ''
                            ) THEN 1 ELSE 0 END) AS TextureCount
                        FROM MeshMaterials mm
                        WHERE mm.ProjectId = @projectId
                          AND mm.MeshAssetUniqueID = @meshAssetId
                        GROUP BY mm.RendererAssetUniqueID
                    ),
                    BestRenderer AS (
                        SELECT RendererAssetUniqueID
                        FROM RendererStats
                        ORDER BY
                            RendererScore DESC,
                            MainTextureCount DESC,
                            TextureCount DESC,
                            SameSourceScore DESC,
                            MaterialCount DESC,
                            RelationCount DESC,
                            MaterialScore DESC,
                            RendererAssetUniqueID
                        LIMIT 1
                    ),
                    RankedMaterials AS (
                        SELECT
                            SubMeshIndex,
                            MaterialSlotIndex,
                            MaterialAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY SubMeshIndex
                                ORDER BY
                                    CASE WHEN MaterialAssetUniqueID <> '' THEN 0 ELSE 1 END,
                                    CASE WHEN EXISTS (
                                        SELECT 1
                                        FROM MaterialTextures mt
                                        WHERE mt.ProjectId = MeshMaterials.ProjectId
                                          AND mt.MaterialAssetUniqueID = MeshMaterials.MaterialAssetUniqueID
                                          AND mt.TextureAssetUniqueID <> ''
                                          AND mt.IsMainTexture <> 0
                                    ) THEN 0 ELSE 1 END,
                                    CASE WHEN EXISTS (
                                        SELECT 1
                                        FROM MaterialTextures mt
                                        WHERE mt.ProjectId = MeshMaterials.ProjectId
                                          AND mt.MaterialAssetUniqueID = MeshMaterials.MaterialAssetUniqueID
                                          AND mt.TextureAssetUniqueID <> ''
                                    ) THEN 0 ELSE 1 END,
                                    MaterialScore DESC,
                                    MaterialSlotIndex,
                                    MaterialAssetUniqueID
                            ) AS RowNumber
                        FROM MeshMaterials
                        WHERE ProjectId = @projectId
                          AND MeshAssetUniqueID = @meshAssetId
                          AND RendererAssetUniqueID = (SELECT RendererAssetUniqueID FROM BestRenderer)
                    )
                    SELECT SubMeshIndex, MaterialAssetUniqueID
                    FROM RankedMaterials
                    WHERE RowNumber = 1
                    ORDER BY SubMeshIndex";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var subMeshIndex = reader.GetInt32(0);
                    var materialAssetId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    while (result.Count < subMeshIndex)
                    {
                        result.Add(null);
                    }

                    result.Add(string.IsNullOrWhiteSpace(materialAssetId) ? null : materialAssetId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load mesh material relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<string> LoadMaterialTextureAssetIds(string folderPath, string signature, string materialAssetId, string? slotName = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(materialAssetId))
            {
                return result;
            }

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var slotFilter = string.IsNullOrEmpty(slotName) ? string.Empty : " AND SlotName = @slotName";
                cmd.CommandText = $@"
                    WITH TextureCandidates AS (
                        SELECT
                            TextureAssetUniqueID,
                            SlotName,
                            SlotIndex,
                            IsMainTexture,
                            0 AS SourceRank
                        FROM MaterialTextures
                        WHERE ProjectId = @projectId
                          AND MaterialAssetUniqueID = @materialAssetId
                          AND TextureAssetUniqueID <> ''
                          {slotFilter}
                        UNION ALL
                        SELECT
                            TextureAssetUniqueID,
                            SlotName,
                            SlotIndex,
                            IsMainTexture,
                            1 AS SourceRank
                        FROM MaterialTextures
                        WHERE ProjectId = @projectId
                          AND PreviewMaterialAssetUniqueID = @materialAssetId
                          AND PreviewMaterialAssetUniqueID <> MaterialAssetUniqueID
                          AND TextureAssetUniqueID <> ''
                          {slotFilter}
                    )
                    SELECT TextureAssetUniqueID
                    FROM TextureCandidates
                    GROUP BY TextureAssetUniqueID
                    ORDER BY
                        MAX(IsMainTexture) DESC,
                        MIN(SourceRank),
                        MIN(CASE SlotName
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
                        END),
                        MIN(SlotIndex),
                        TextureAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@materialAssetId", materialAssetId);
                cmd.Parameters.AddWithValue("@slotName", slotName ?? string.Empty);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load material texture relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal PreviewCacheEntry? LoadPreviewCacheEntry(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters)
        {
            if (string.IsNullOrWhiteSpace(assetUniqueId) || string.IsNullOrWhiteSpace(previewKind))
            {
                return null;
            }

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return null;
                }

                PreviewCacheEntry? entry = null;
                long? entryId = null;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT Id, PayloadHash, PayloadPath, ByteSize
                        FROM PreviewCacheEntries
                        WHERE ProjectId = @projectId
                          AND AssetUniqueID = @assetUniqueId
                          AND PreviewKind = @previewKind
                          AND AlgorithmVersion = @algorithmVersion
                          AND Parameters = @parameters
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                    cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@previewKind", previewKind);
                    cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                    cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);

                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        entryId = reader.GetInt64(0);
                        entry = new PreviewCacheEntry(
                            reader.GetString(1),
                            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            reader.GetInt64(3));
                    }
                }

                if (entryId != null)
                {
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = transaction;
                    updateCmd.CommandText = "UPDATE PreviewCacheEntries SET LastAccessed = CURRENT_TIMESTAMP WHERE Id = @id";
                    updateCmd.Parameters.AddWithValue("@id", entryId.Value);
                    updateCmd.ExecuteNonQuery();
                }

                transaction.Commit();
                return entry;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load preview cache entry from SQLite: {ex.Message}");
                return null;
            }
        }

        internal void SavePreviewCacheEntry(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            string payloadHash,
            string payloadPath,
            long byteSize)
        {
            if (string.IsNullOrWhiteSpace(assetUniqueId)
                || string.IsNullOrWhiteSpace(previewKind)
                || string.IsNullOrWhiteSpace(payloadHash)
                || string.IsNullOrWhiteSpace(payloadPath))
            {
                return;
            }

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT INTO PreviewCacheEntries (
                        ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize)
                    VALUES (
                        @projectId, @assetUniqueId, @previewKind, @algorithmVersion, @parameters, @payloadHash, @payloadPath, @byteSize)
                    ON CONFLICT(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters)
                    DO UPDATE SET
                        PayloadHash = excluded.PayloadHash,
                        PayloadPath = excluded.PayloadPath,
                        ByteSize = excluded.ByteSize,
                        LastAccessed = CURRENT_TIMESTAMP";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                cmd.Parameters.AddWithValue("@previewKind", previewKind);
                cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);
                cmd.Parameters.AddWithValue("@payloadHash", payloadHash);
                cmd.Parameters.AddWithValue("@payloadPath", payloadPath);
                cmd.Parameters.AddWithValue("@byteSize", byteSize);
                cmd.ExecuteNonQuery();

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save preview cache entry to SQLite: {ex.Message}");
            }
        }

        public void DeleteIndexCache(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var fullPath = GetFullPathOrOriginal(folderPath);

            EnsureInitialized();
            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        DELETE FROM AssetHandles
                        WHERE ProjectId IN (
                            SELECT Id FROM Projects
                            WHERE FolderPath = @path OR FolderPath = @fullPath
                        )";
                    cmd.Parameters.AddWithValue("@path", folderPath);
                    cmd.Parameters.AddWithValue("@fullPath", fullPath);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM Projects WHERE FolderPath = @path OR FolderPath = @fullPath";
                    cmd.Parameters.AddWithValue("@path", folderPath);
                    cmd.Parameters.AddWithValue("@fullPath", fullPath);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete SQLite index cache: {ex.Message}");
            }
        }

        private static string GetFullPathOrOriginal(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
