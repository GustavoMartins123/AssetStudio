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
        private const int SemanticSchemaVersion = 11;
        private const int SemanticRelationCommitBatchSize = 10_000;
        private const int ReadBusyTimeoutSeconds = 5;
        private static readonly object WriteGate = new object();
        private readonly string _cacheDir;
        private readonly HashSet<string> _initializedDbs = new();
        private readonly object _initLock = new object();

        public SQLiteProjectIndexCache()
        {
            _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "IndexCache");
            Directory.CreateDirectory(_cacheDir);
        }

        private static string GetFolderCacheKey(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }
            try
            {
                var normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch
            {
                var normalized = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
        }

        private string GetDbPath(string folderPath)
        {
            var folderKey = GetFolderCacheKey(folderPath);
            return Path.Combine(_cacheDir, $"project_index_{folderKey}.db");
        }

        private void EnsureInitialized(string folderPath)
        {
            var dbPath = GetDbPath(folderPath);
            lock (_initLock)
            {
                if (_initializedDbs.Contains(dbPath))
                {
                    return;
                }

                InitializeDatabase(folderPath);
                _initializedDbs.Add(dbPath);
            }
        }

        private SqliteConnection CreateConnection(string folderPath)
        {
            var dbPath = GetDbPath(folderPath);
            var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.DefaultTimeout = 60;
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 60000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private SqliteConnection CreateReadConnection(string folderPath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = GetDbPath(folderPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = true,
                DefaultTimeout = ReadBusyTimeoutSeconds
            }.ToString();
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = $@"
                PRAGMA query_only = ON;
                PRAGMA busy_timeout = {ReadBusyTimeoutSeconds * 1000};
                PRAGMA temp_store = MEMORY;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private void InitializeDatabase(string folderPath)
        {
            try
            {
                lock (WriteGate)
                {
                    using (var conn = CreateConnection(folderPath))
                    {
                        using (var pragma = conn.CreateCommand())
                        {
                            pragma.CommandText = @"
                                PRAGMA journal_mode = WAL;
                                PRAGMA synchronous = NORMAL;
                                PRAGMA wal_autocheckpoint = 1000;";
                            pragma.ExecuteNonQuery();
                        }

                        if (ShouldRebuildSchema(conn))
                        {
                            RebuildSchema(conn);
                        }

                        CreateSchema(conn);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize SQLite database cache for {folderPath}: {ex.Message}", ex);
            }
        }

        private static bool IsDatabaseBusy(SqliteException ex)
        {
            return ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;
        }

        private static void TryCheckpointWal(SqliteConnection conn, string context)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                // PASSIVE does not wait for active readers. TRUNCATE can hold the
                // only writer for the full busy timeout while previews are reading.
                cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var busy = reader.FieldCount > 0 && !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var logFrames = reader.FieldCount > 1 && !reader.IsDBNull(1) ? reader.GetInt32(1) : 0;
                    var checkpointedFrames = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetInt32(2) : 0;
                    if (busy != 0)
                    {
                        Logger.Warning($"SQLite WAL checkpoint for {context} could not finish now: {checkpointedFrames:N0}/{logFrames:N0} frames checkpointed, busy={busy}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"SQLite WAL checkpoint for {context} failed: {ex.Message}");
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
                    DROP TABLE IF EXISTS ModelGroupMeshes;
                    DROP TABLE IF EXISTS ModelGroups;
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
                            CREATE INDEX IF NOT EXISTS idx_projects_lookup ON Projects(FolderPath COLLATE NOCASE, SignatureHash, LastIndexed DESC);
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

                            CREATE TABLE IF NOT EXISTS ModelGroups (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                GroupAssetUniqueID TEXT NOT NULL,
                                GroupKind TEXT NOT NULL DEFAULT '',
                                GroupName TEXT NOT NULL DEFAULT '',
                                RootGameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RootGameObjectName TEXT NOT NULL DEFAULT '',
                                AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                                AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                                ControllerAssetUniqueID TEXT NOT NULL DEFAULT '',
                                SourceFileName TEXT NOT NULL DEFAULT '',
                                Confidence INTEGER NOT NULL DEFAULT 0,
                                ConfidenceReason TEXT NOT NULL DEFAULT ''
                            );

                            CREATE TABLE IF NOT EXISTS ModelGroupMeshes (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                GroupAssetUniqueID TEXT NOT NULL,
                                MeshAssetUniqueID TEXT NOT NULL,
                                RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererType TEXT NOT NULL DEFAULT '',
                                GameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                                GameObjectName TEXT NOT NULL DEFAULT '',
                                SlotIndex INTEGER NOT NULL DEFAULT -1,
                                TransformMatrix BLOB,
                                Confidence INTEGER NOT NULL DEFAULT 0,
                                ConfidenceReason TEXT NOT NULL DEFAULT ''
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
                            CREATE INDEX IF NOT EXISTS idx_assets_type_source ON Assets(ProjectId, Type, SourceFileId, UniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_asset_edges_unique ON AssetEdges(ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_source ON AssetEdges(ProjectId, SourceAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_target ON AssetEdges(ProjectId, TargetAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_source_kind ON AssetEdges(ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_target_kind ON AssetEdges(ProjectId, TargetAssetUniqueID, EdgeKind, SourceAssetUniqueID, SlotIndex);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_model_groups_unique ON ModelGroups(ProjectId, GroupAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_groups_animator ON ModelGroups(ProjectId, AnimatorAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_groups_avatar ON ModelGroups(ProjectId, AvatarAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_model_group_meshes_unique ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_group ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, SlotIndex);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_mesh ON ModelGroupMeshes(ProjectId, MeshAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_candidate ON ModelGroupMeshes(ProjectId, MeshAssetUniqueID, GroupAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_mesh_renderers_unique ON MeshRenderers(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType);
                            CREATE INDEX IF NOT EXISTS idx_mesh_renderers_mesh ON MeshRenderers(ProjectId, MeshAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_renderers_lookup ON MeshRenderers(ProjectId, MeshAssetUniqueID, RendererType, RendererAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_mesh_materials_unique ON MeshMaterials(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_materials_mesh ON MeshMaterials(ProjectId, MeshAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_materials_mesh_submesh ON MeshMaterials(ProjectId, MeshAssetUniqueID, SubMeshIndex);
                            CREATE INDEX IF NOT EXISTS idx_mesh_materials_renderer ON MeshMaterials(ProjectId, MeshAssetUniqueID, RendererAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_materials_rank ON MeshMaterials(ProjectId, MeshAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialScore DESC, RendererAssetUniqueID, MaterialAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_material_textures_unique ON MaterialTextures(ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_material ON MaterialTextures(ProjectId, MaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_preview_material ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_material_lookup ON MaterialTextures(ProjectId, MaterialAssetUniqueID, SlotName, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_preview_lookup ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID, SlotName, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_rank ON MaterialTextures(ProjectId, MaterialAssetUniqueID, SlotName, IsMainTexture DESC, SlotIndex, TextureAssetUniqueID);
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
                var processedWork = 0;
                var lastReportedWork = -1;
                var lastReportedTicks = 0L;
                var minWorkDelta = Math.Max(1, totalWork / 100000);

                void ReportProgress(string stage, bool force = false)
                {
                    if (progress == null)
                    {
                        return;
                    }

                    var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsedMs = lastReportedTicks == 0
                        ? double.MaxValue
                        : (nowTicks - lastReportedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    if (!force && processedWork < totalWork && processedWork - lastReportedWork < minWorkDelta && elapsedMs < 100)
                    {
                        return;
                    }

                    lastReportedWork = processedWork;
                    lastReportedTicks = nowTicks;
                    progress(Math.Clamp(processedWork, 0, totalWork), totalWork, stage);
                }

                void AdvanceProgress(string stage)
                {
                    processedWork++;
                    ReportProgress(stage);
                }

                ReportProgress("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    ReportProgress("Opening SQLite connection", force: true);
                    using (var conn = CreateConnection(folderPath))
                    {
                        using (var transaction = conn.BeginTransaction())
                        {
                            ReportProgress("Preparing SQLite project row", force: true);
                            var projectId = EnsureProject(conn, transaction, folderPath, signature, scanResult, unityVersion);
                            ReportProgress("Clearing previous index rows", force: true);
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
                                    AdvanceProgress($"Saving handles: {savedHandleRows:N0}/{handleList.Count:N0}");
                                }
                            }

                            InsertSourceFilesAndAssets(conn, transaction, projectId, handleList, AdvanceProgress);

                            ReportProgress("Committing SQLite transaction", force: true);
                            transaction.Commit();
                            ReportProgress("Checkpointing SQLite WAL", force: true);
                            TryCheckpointWal(conn, "index cache");
                            processedWork = totalWork;
                            ReportProgress("SQLite index cache saved", force: true);
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
                var processedWork = 0;
                var lastReportedWork = -1;
                var lastReportedTicks = 0L;
                var minWorkDelta = Math.Max(1, totalWork / 100000);
                var currentStage = string.Empty;

                void ReportProgress(string stage, bool force = false)
                {
                    if (progress == null)
                    {
                        return;
                    }

                    var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsedMs = lastReportedTicks == 0
                        ? double.MaxValue
                        : (nowTicks - lastReportedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    if (!force && processedWork < totalWork && processedWork - lastReportedWork < minWorkDelta && elapsedMs < 100)
                    {
                        return;
                    }

                    lastReportedWork = processedWork;
                    lastReportedTicks = nowTicks;
                    progress(Math.Clamp(processedWork, 0, totalWork), totalWork, stage);
                }

                void AdvanceProgress(string stage)
                {
                    processedWork++;
                    currentStage = stage;
                    ReportProgress(stage);
                }

                ReportProgress("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    ReportProgress("Opening SQLite connection", force: true);
                    using var conn = CreateConnection(folderPath);

                    long projectId;
                    bool resumePartialSave;
                    using (var transaction = conn.BeginTransaction())
                    {
                        ReportProgress("Resolving project row", force: true);
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
                            processedWork,
                            totalWork,
                            resumePartialSave
                                ? "Resuming partial semantic relation save"
                                : "Preparing semantic relation save",
                            completed: false);
                        transaction.Commit();
                    }

                    if (replaceExisting && !resumePartialSave)
                    {
                        using var transaction = conn.BeginTransaction();
                        ClearSemanticRelationTablesForProject(conn, transaction, projectId, AdvanceProgress);
                        SaveProjectIndexingState(
                            conn,
                            transaction,
                            projectId,
                            "saving_connections",
                            processedWork,
                            totalWork,
                            currentStage,
                            completed: false);
                        transaction.Commit();
                    }
                    else if (replaceExisting && resumePartialSave)
                    {
                        processedWork += clearWork;
                        currentStage = "Resuming partial semantic relation save";
                        ReportProgress(currentStage, force: true);
                    }

                    using (var transaction = conn.BeginTransaction())
                    {
                        InsertSemanticSourceFiles(conn, transaction, projectId, relations.SourceFiles, AdvanceProgress);
                        SaveProjectIndexingState(
                            conn,
                            transaction,
                            projectId,
                            "saving_connections",
                            processedWork,
                            totalWork,
                            string.IsNullOrWhiteSpace(currentStage) ? "Saving semantic source files" : currentStage,
                            completed: false);
                        transaction.Commit();
                    }

                    Dictionary<string, long> assetSourceFileIds;
                    using (var transaction = conn.BeginTransaction())
                    {
                        assetSourceFileIds = LoadAssetSourceFileIdMap(conn, transaction, projectId);
                        transaction.Commit();
                    }

                    InsertAssetEdgesInChunks(conn, projectId, relations.AssetEdges, assetSourceFileIds, AdvanceProgress, SaveProgressCheckpoint);
                    InsertModelGroupsInChunks(conn, projectId, relations.ModelGroups, AdvanceProgress, SaveProgressCheckpoint);
                    InsertModelGroupMeshesInChunks(conn, projectId, relations.ModelGroupMeshes, AdvanceProgress, SaveProgressCheckpoint);
                    InsertMeshRenderersInChunks(conn, projectId, relations.MeshRenderers, AdvanceProgress, SaveProgressCheckpoint);
                    InsertMeshMaterialsInChunks(conn, projectId, relations.MeshMaterials, AdvanceProgress, SaveProgressCheckpoint);
                    InsertMaterialTexturesInChunks(conn, projectId, relations.MaterialTextures, AdvanceProgress, SaveProgressCheckpoint);

                    ReportProgress("Committing semantic relations", force: true);
                    using (var transaction = conn.BeginTransaction())
                    {
                        processedWork = totalWork;
                        currentStage = "Semantic relations saved";
                        SaveProjectIndexingState(
                            conn,
                            transaction,
                            projectId,
                            "connections_completed",
                            processedWork,
                            totalWork,
                            currentStage,
                            completed: true);
                        transaction.Commit();
                    }
                    ReportProgress("Checkpointing SQLite WAL", force: true);
                    TryCheckpointWal(conn, "semantic relations");
                    ReportProgress("Semantic relations saved", force: true);
                    return true;

                    void SaveProgressCheckpoint(SqliteConnection checkpointConn, SqliteTransaction checkpointTransaction)
                    {
                        SaveProjectIndexingState(
                            checkpointConn,
                            checkpointTransaction,
                            projectId,
                            "saving_connections",
                            processedWork,
                            totalWork,
                            string.IsNullOrWhiteSpace(currentStage) ? "Saving semantic relations" : currentStage,
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

        internal bool HasSemanticRelations(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                var projectId = FindProjectId(conn, null, folderPath, signature);
                if (projectId == null)
                {
                    return false;
                }

                var count = CountMaterialSemanticRelations(conn, null, projectId.Value);
                return count > 0;
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to inspect semantic asset relations: {ex.Message}");
                return false;
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

        private static long? FindProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath, string signature)
        {
            var path1 = folderPath;
            var path2 = path1.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id FROM Projects
                WHERE (FolderPath = @path1 COLLATE NOCASE OR FolderPath = @path2 COLLATE NOCASE)
                  AND SignatureHash = @signature
                LIMIT 1";
            cmd.Parameters.AddWithValue("@path1", path1);
            cmd.Parameters.AddWithValue("@path2", path2);
            cmd.Parameters.AddWithValue("@signature", signature);
            var id = cmd.ExecuteScalar();
            return id == null ? null : Convert.ToInt64(id);
        }

        private static long? FindLatestProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath)
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

            EnsureInitialized(folderPath);
            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnection(folderPath);
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
            try
            {
                using var conn = CreateReadConnection(folderPath);
                var projectId = FindProjectId(conn, null, folderPath, signature);
                if (projectId == null)
                {
                    return null;
                }

                var state = LoadIndexingState(conn, null, projectId.Value);
                return state;
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                return null;
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

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                var projectId = FindLatestProjectId(conn, null, folderPath);
                if (projectId == null)
                {
                    return null;
                }

                var state = LoadIndexingState(conn, null, projectId.Value);
                return state;
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

        private static long CountMaterialSemanticRelations(SqliteConnection conn, SqliteTransaction? transaction, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(1) FROM AssetEdges WHERE ProjectId = @projectId) +
                    (SELECT COUNT(1) FROM ModelGroups WHERE ProjectId = @projectId) +
                    (SELECT COUNT(1) FROM ModelGroupMeshes WHERE ProjectId = @projectId) +
                    (SELECT COUNT(1) FROM MeshMaterials WHERE ProjectId = @projectId) +
                    (SELECT COUNT(1) FROM MaterialTextures WHERE ProjectId = @projectId)";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        }

        private static bool IsSemanticSaveInProgress(SqliteConnection conn, SqliteTransaction transaction, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Status
                FROM ProjectIndexingState
                WHERE ProjectId = @projectId
                LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            var status = cmd.ExecuteScalar()?.ToString();
            return string.Equals(status, "saving_connections", StringComparison.OrdinalIgnoreCase);
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
            cmd.Parameters.AddWithValue("@status", status ?? string.Empty);
            cmd.Parameters.AddWithValue("@totalFiles", safeTotal);
            cmd.Parameters.AddWithValue("@processedFiles", safeProcessed);
            cmd.Parameters.AddWithValue("@pendingFiles", Math.Max(0, safeTotal - safeProcessed));
            cmd.Parameters.AddWithValue("@percentComplete", percent);
            cmd.Parameters.AddWithValue("@currentFile", stage ?? string.Empty);
            cmd.Parameters.AddWithValue("@lastReadFile", stage ?? string.Empty);
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

        private static void InsertAssetEdgesInChunks(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<SemanticAssetEdgeRelation> edges,
            IReadOnlyDictionary<string, long> assetSourceFileIds,
            Action<string> advanceProgress,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < edges.Count; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                InsertAssetEdges(conn, transaction, projectId, edges, offset, SemanticRelationCommitBatchSize, assetSourceFileIds, advanceProgress);
                saveProgress(conn, transaction);
                transaction.Commit();
            }
        }

        private static void InsertModelGroupsInChunks(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<SemanticModelGroupRelation> groups,
            Action<string> advanceProgress,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < groups.Count; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                InsertModelGroups(conn, transaction, projectId, groups, offset, SemanticRelationCommitBatchSize, advanceProgress);
                saveProgress(conn, transaction);
                transaction.Commit();
            }
        }

        private static void InsertModelGroupMeshesInChunks(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<SemanticModelGroupMeshRelation> meshes,
            Action<string> advanceProgress,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < meshes.Count; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                InsertModelGroupMeshes(conn, transaction, projectId, meshes, offset, SemanticRelationCommitBatchSize, advanceProgress);
                saveProgress(conn, transaction);
                transaction.Commit();
            }
        }

        private static void InsertMeshRenderersInChunks(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<SemanticMeshRendererRelation> renderers,
            Action<string> advanceProgress,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < renderers.Count; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                InsertMeshRenderers(conn, transaction, projectId, renderers, offset, SemanticRelationCommitBatchSize, advanceProgress);
                saveProgress(conn, transaction);
                transaction.Commit();
            }
        }

        private static void InsertMeshMaterialsInChunks(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<SemanticMeshMaterialRelation> materials,
            Action<string> advanceProgress,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < materials.Count; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                InsertMeshMaterials(conn, transaction, projectId, materials, offset, SemanticRelationCommitBatchSize, advanceProgress);
                saveProgress(conn, transaction);
                transaction.Commit();
            }
        }

        private static void InsertMaterialTexturesInChunks(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<SemanticMaterialTextureRelation> textures,
            Action<string> advanceProgress,
            Action<SqliteConnection, SqliteTransaction> saveProgress)
        {
            for (var offset = 0; offset < textures.Count; offset += SemanticRelationCommitBatchSize)
            {
                using var transaction = conn.BeginTransaction();
                InsertMaterialTextures(conn, transaction, projectId, textures, offset, SemanticRelationCommitBatchSize, advanceProgress);
                saveProgress(conn, transaction);
                transaction.Commit();
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

        private static byte[]? SerializeTransformMatrix(float[]? matrix)
        {
            if (matrix == null || matrix.Length != 16)
            {
                return null;
            }

            var bytes = new byte[sizeof(float) * 16];
            Buffer.BlockCopy(matrix, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[]? ReadTransformMatrix(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            var bytes = reader.GetFieldValue<byte[]>(ordinal);
            if (bytes.Length != sizeof(float) * 16)
            {
                return null;
            }

            var matrix = new float[16];
            Buffer.BlockCopy(bytes, 0, matrix, 0, bytes.Length);
            return matrix;
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

        internal List<string?> LoadMeshMaterialAssetIds(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<string?>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
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
                    CandidateMaterials AS (
                        SELECT DISTINCT MaterialAssetUniqueID
                        FROM MeshMaterials
                        WHERE ProjectId = @projectId
                          AND MeshAssetUniqueID = @meshAssetId
                          AND MaterialAssetUniqueID <> ''
                    ),
                    MaterialStats AS (
                        SELECT
                            candidate.MaterialAssetUniqueID,
                            MAX(CASE WHEN texture.TextureAssetUniqueID <> '' AND texture.IsMainTexture <> 0 THEN 1 ELSE 0 END) AS HasMainTexture,
                            MAX(CASE WHEN texture.TextureAssetUniqueID <> '' THEN 1 ELSE 0 END) AS HasTexture
                        FROM CandidateMaterials candidate
                        LEFT JOIN MaterialTextures texture
                          ON texture.ProjectId = @projectId
                         AND texture.MaterialAssetUniqueID = candidate.MaterialAssetUniqueID
                        GROUP BY candidate.MaterialAssetUniqueID
                    ),
                    RendererStats AS (
                        SELECT
                            mm.RendererAssetUniqueID,
                            MAX(CASE WHEN mm.RendererType <> 'Container' THEN 1 ELSE 0 END) AS RendererScore,
                            SUM(CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 1 ELSE 0 END) AS MaterialCount,
                            COUNT(*) AS RelationCount,
                            MAX(mm.MaterialScore) AS MaterialScore,
                            MAX(CASE WHEN materialAsset.SourceFileId = (SELECT SourceFileId FROM MeshSource) THEN 1 ELSE 0 END) AS SameSourceScore,
                            SUM(COALESCE(materialStats.HasMainTexture, 0)) AS MainTextureCount,
                            SUM(COALESCE(materialStats.HasTexture, 0)) AS TextureCount
                        FROM MeshMaterials mm
                        LEFT JOIN Assets materialAsset
                          ON materialAsset.ProjectId = mm.ProjectId
                         AND materialAsset.UniqueID = mm.MaterialAssetUniqueID
                        LEFT JOIN MaterialStats materialStats
                          ON materialStats.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                        WHERE mm.ProjectId = @projectId
                          AND mm.MeshAssetUniqueID = @meshAssetId
                        GROUP BY mm.RendererAssetUniqueID
                    ),
                    RankedMaterials AS (
                        SELECT
                            mm.SubMeshIndex,
                            mm.MaterialSlotIndex,
                            mm.MaterialAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY mm.SubMeshIndex
                                ORDER BY
                                    CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 0 ELSE 1 END,
                                    CASE WHEN COALESCE(materialStats.HasMainTexture, 0) <> 0 THEN 0 ELSE 1 END,
                                    CASE WHEN COALESCE(materialStats.HasTexture, 0) <> 0 THEN 0 ELSE 1 END,
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
                            ) AS RowNumber
                        FROM MeshMaterials mm
                        LEFT JOIN RendererStats rs
                          ON rs.RendererAssetUniqueID = mm.RendererAssetUniqueID
                        LEFT JOIN MaterialStats materialStats
                          ON materialStats.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                        WHERE mm.ProjectId = @projectId
                          AND mm.MeshAssetUniqueID = @meshAssetId
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

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
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

        internal List<string> LoadAvatarMeshAssetIds(string folderPath, string signature, string avatarAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(avatarAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
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
                    GROUP BY ae.TargetAssetUniqueID
                    ORDER BY MIN(ae.SlotIndex), MIN(target.Name), ae.TargetAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@avatarAssetId", avatarAssetId);
                cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load avatar mesh relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal Dictionary<string, List<string>> LoadAvatarMeshAssetIdsByAvatarIds(string folderPath, string signature, IReadOnlyList<string> avatarAssetIds)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ids = avatarAssetIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0)
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                const int chunkSize = 400;
                for (var offset = 0; offset < ids.Count; offset += chunkSize)
                {
                    var chunk = ids.Skip(offset).Take(chunkSize).ToList();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    var placeholders = new List<string>(chunk.Count);
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        var parameterName = "@avatarId" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        placeholders.Add(parameterName);
                        cmd.Parameters.AddWithValue(parameterName, chunk[i]);
                    }

                    cmd.CommandText = $@"
                        SELECT
                            ae.SourceAssetUniqueID,
                            ae.TargetAssetUniqueID
                        FROM AssetEdges ae
                        INNER JOIN Assets target
                          ON target.ProjectId = ae.ProjectId
                         AND target.UniqueID = ae.TargetAssetUniqueID
                        WHERE ae.ProjectId = @projectId
                          AND ae.SourceAssetUniqueID IN ({string.Join(",", placeholders)})
                          AND ae.EdgeKind = 'Mesh'
                          AND ae.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                          AND ae.TargetAssetUniqueID <> ''
                          AND target.Type = @meshType
                        GROUP BY ae.SourceAssetUniqueID, ae.TargetAssetUniqueID
                        ORDER BY ae.SourceAssetUniqueID, MIN(ae.SlotIndex), MIN(target.Name), ae.TargetAssetUniqueID";
                    cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                    cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var avatarId = reader.GetString(0);
                        var meshId = reader.GetString(1);
                        if (!result.TryGetValue(avatarId, out var meshes))
                        {
                            meshes = new List<string>();
                            result[avatarId] = meshes;
                        }

                        meshes.Add(meshId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load avatar mesh relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<string> LoadMeshAvatarAssetIds(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
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
                    GROUP BY ae.SourceAssetUniqueID
                    ORDER BY MIN(ae.SlotIndex), MIN(source.Name), ae.SourceAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load mesh avatar relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<string> LoadMeshRendererAssetIds(string folderPath, string signature, string meshAssetId, string? rendererType = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT mr.RendererAssetUniqueID
                    FROM MeshRenderers mr
                    WHERE mr.ProjectId = @projectId
                      AND mr.MeshAssetUniqueID = @meshAssetId
                      AND mr.RendererAssetUniqueID <> ''
                      AND (@rendererType = '' OR mr.RendererType = @rendererType)
                    GROUP BY mr.RendererAssetUniqueID
                    ORDER BY
                        CASE WHEN mr.RendererType = 'SkinnedMeshRenderer' THEN 0 ELSE 1 END,
                        MIN(mr.GameObjectName),
                        mr.RendererAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                cmd.Parameters.AddWithValue("@rendererType", rendererType ?? string.Empty);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load mesh renderer relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<string> LoadAnimationClipAvatarAssetIds(string folderPath, string signature, string clipAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(clipAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    WITH AvatarCandidates AS (
                        SELECT
                            ae.TargetAssetUniqueID AS AvatarAssetId,
                            ae.SlotIndex AS SortIndex
                        FROM AssetEdges ae
                        WHERE ae.ProjectId = @projectId
                          AND ae.SourceAssetUniqueID = @clipAssetId
                          AND ae.EdgeKind = 'Avatar'
                          AND ae.SlotName IN ('AnimatorAvatar', 'CompatibleAvatar')
                          AND ae.TargetAssetUniqueID <> ''
                        UNION ALL
                        SELECT
                            avatarEdge.TargetAssetUniqueID AS AvatarAssetId,
                            avatarEdge.SlotIndex AS SortIndex
                        FROM AssetEdges clipEdge
                        INNER JOIN AssetEdges avatarEdge
                          ON avatarEdge.ProjectId = clipEdge.ProjectId
                         AND avatarEdge.SourceAssetUniqueID = clipEdge.SourceAssetUniqueID
                         AND avatarEdge.EdgeKind = 'Avatar'
                         AND avatarEdge.TargetAssetUniqueID <> ''
                        WHERE clipEdge.ProjectId = @projectId
                          AND clipEdge.TargetAssetUniqueID = @clipAssetId
                          AND clipEdge.EdgeKind = 'AnimationClip'
                    )
                    SELECT candidates.AvatarAssetId
                    FROM AvatarCandidates candidates
                    INNER JOIN Assets target
                      ON target.ProjectId = @projectId
                     AND target.UniqueID = candidates.AvatarAssetId
                    WHERE target.Type = @avatarType
                    GROUP BY candidates.AvatarAssetId
                    ORDER BY MIN(candidates.SortIndex), MIN(target.Name), candidates.AvatarAssetId";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@clipAssetId", clipAssetId);
                cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load animation clip avatar relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<string> LoadAnimationClipMeshAssetIds(string folderPath, string signature, string clipAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(clipAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    WITH MeshCandidates AS (
                        SELECT
                            meshEdge.TargetAssetUniqueID AS MeshAssetId,
                            meshEdge.SlotIndex AS SortIndex
                        FROM AssetEdges clipEdge
                        INNER JOIN AssetEdges meshEdge
                          ON meshEdge.ProjectId = clipEdge.ProjectId
                         AND meshEdge.SourceAssetUniqueID = clipEdge.SourceAssetUniqueID
                         AND meshEdge.EdgeKind = 'Mesh'
                         AND meshEdge.SlotName = 'AnimatorMesh'
                         AND meshEdge.TargetAssetUniqueID <> ''
                        WHERE clipEdge.ProjectId = @projectId
                          AND clipEdge.TargetAssetUniqueID = @clipAssetId
                          AND clipEdge.EdgeKind = 'AnimationClip'
                        UNION ALL
                        SELECT
                            meshEdge.TargetAssetUniqueID AS MeshAssetId,
                            meshEdge.SlotIndex AS SortIndex
                        FROM AssetEdges clipAvatarEdge
                        INNER JOIN AssetEdges meshEdge
                          ON meshEdge.ProjectId = clipAvatarEdge.ProjectId
                         AND meshEdge.SourceAssetUniqueID = clipAvatarEdge.TargetAssetUniqueID
                         AND meshEdge.EdgeKind = 'Mesh'
                         AND meshEdge.SlotName IN ('AnimatorMesh', 'AvatarMesh')
                         AND meshEdge.TargetAssetUniqueID <> ''
                        WHERE clipAvatarEdge.ProjectId = @projectId
                          AND clipAvatarEdge.SourceAssetUniqueID = @clipAssetId
                          AND clipAvatarEdge.EdgeKind = 'Avatar'
                    )
                    SELECT candidates.MeshAssetId
                    FROM MeshCandidates candidates
                    INNER JOIN Assets target
                      ON target.ProjectId = @projectId
                     AND target.UniqueID = candidates.MeshAssetId
                    WHERE target.Type = @meshType
                    GROUP BY candidates.MeshAssetId
                    ORDER BY MIN(candidates.SortIndex), MIN(target.Name), candidates.MeshAssetId";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@clipAssetId", clipAssetId);
                cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load animation clip mesh relations from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<ModelGroupInfo> LoadModelGroupsForAvatarAssetId(string folderPath, string signature, string avatarAssetId)
        {
            var result = new List<ModelGroupInfo>();
            if (string.IsNullOrWhiteSpace(avatarAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT
                        GroupAssetUniqueID,
                        GroupKind,
                        GroupName,
                        RootGameObjectAssetUniqueID,
                        RootGameObjectName,
                        AnimatorAssetUniqueID,
                        AvatarAssetUniqueID,
                        ControllerAssetUniqueID,
                        SourceFileName,
                        Confidence,
                        ConfidenceReason
                    FROM ModelGroups
                    WHERE ProjectId = @projectId
                      AND AvatarAssetUniqueID = @avatarAssetId
                    ORDER BY Confidence DESC, GroupName, GroupAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@avatarAssetId", avatarAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new ModelGroupInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        reader.GetInt32(9),
                        reader.GetString(10)));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load model groups from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<ModelGroupCandidateInfo> LoadModelGroupCandidatesForMeshAssetId(
            string folderPath,
            string signature,
            string meshAssetId)
        {
            var result = new List<ModelGroupCandidateInfo>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    WITH CandidateGroups AS (
                        SELECT
                            modelGroup.GroupAssetUniqueID,
                            modelGroup.GroupKind,
                            modelGroup.GroupName,
                            modelGroup.RootGameObjectAssetUniqueID,
                            modelGroup.RootGameObjectName,
                            modelGroup.AnimatorAssetUniqueID,
                            modelGroup.AvatarAssetUniqueID,
                            modelGroup.ControllerAssetUniqueID,
                            modelGroup.SourceFileName,
                            modelGroup.Confidence,
                            modelGroup.ConfidenceReason,
                            COUNT(groupMesh.MeshAssetUniqueID) AS PartCount,
                            MIN(groupMesh.SlotIndex) AS FirstSlot
                        FROM ModelGroups modelGroup
                        INNER JOIN ModelGroupMeshes groupMesh
                          ON groupMesh.ProjectId = modelGroup.ProjectId
                         AND groupMesh.GroupAssetUniqueID = modelGroup.GroupAssetUniqueID
                        WHERE modelGroup.ProjectId = @projectId
                          AND EXISTS (
                              SELECT 1
                              FROM ModelGroupMeshes selectedMesh
                              WHERE selectedMesh.ProjectId = modelGroup.ProjectId
                                AND selectedMesh.GroupAssetUniqueID = modelGroup.GroupAssetUniqueID
                                AND selectedMesh.MeshAssetUniqueID = @meshAssetId
                          )
                        GROUP BY
                            modelGroup.GroupAssetUniqueID,
                            modelGroup.GroupKind,
                            modelGroup.GroupName,
                            modelGroup.RootGameObjectAssetUniqueID,
                            modelGroup.RootGameObjectName,
                            modelGroup.AnimatorAssetUniqueID,
                            modelGroup.AvatarAssetUniqueID,
                            modelGroup.ControllerAssetUniqueID,
                            modelGroup.SourceFileName,
                            modelGroup.Confidence,
                            modelGroup.ConfidenceReason
                    )
                    SELECT
                        candidate.GroupAssetUniqueID,
                        candidate.GroupKind,
                        candidate.GroupName,
                        candidate.RootGameObjectAssetUniqueID,
                        candidate.RootGameObjectName,
                        candidate.AnimatorAssetUniqueID,
                        candidate.AvatarAssetUniqueID,
                        candidate.ControllerAssetUniqueID,
                        candidate.SourceFileName,
                        candidate.Confidence,
                        candidate.ConfidenceReason,
                        groupMesh.MeshAssetUniqueID,
                        groupMesh.RendererAssetUniqueID,
                        groupMesh.RendererType,
                        groupMesh.GameObjectAssetUniqueID,
                        groupMesh.GameObjectName,
                        groupMesh.SlotIndex,
                        groupMesh.Confidence,
                        groupMesh.ConfidenceReason,
                        groupMesh.TransformMatrix,
                        COALESCE(mesh.Name, ''),
                        COALESCE(mesh.PathID, 0),
                        COALESCE(mesh.ByteSize, 0),
                        COALESCE(mesh.Type, 0)
                    FROM CandidateGroups candidate
                    INNER JOIN ModelGroupMeshes groupMesh
                      ON groupMesh.ProjectId = @projectId
                     AND groupMesh.GroupAssetUniqueID = candidate.GroupAssetUniqueID
                    LEFT JOIN Assets mesh
                      ON mesh.ProjectId = groupMesh.ProjectId
                     AND mesh.UniqueID = groupMesh.MeshAssetUniqueID
                    ORDER BY
                        candidate.Confidence DESC,
                        candidate.PartCount,
                        candidate.FirstSlot,
                        candidate.GroupName,
                        candidate.GroupAssetUniqueID,
                        groupMesh.SlotIndex,
                        groupMesh.GameObjectName,
                        mesh.Name,
                        groupMesh.MeshAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);

                var candidatesById = new Dictionary<string, ModelGroupCandidateInfo>(StringComparer.Ordinal);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var groupId = reader.GetString(0);
                    if (!candidatesById.TryGetValue(groupId, out var candidate))
                    {
                        var group = new ModelGroupInfo(
                            groupId,
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetString(6),
                            reader.GetString(7),
                            reader.GetString(8),
                            reader.GetInt32(9),
                            reader.GetString(10));
                        candidate = new ModelGroupCandidateInfo(group);
                        candidatesById.Add(groupId, candidate);
                        result.Add(candidate);
                    }

                    candidate.Meshes.Add(new ModelGroupMeshInfo(
                        groupId,
                        reader.GetString(11),
                        reader.GetString(12),
                        reader.GetString(13),
                        reader.GetString(14),
                        reader.GetString(15),
                        reader.GetInt32(16),
                        reader.GetInt32(17),
                        reader.GetString(18),
                        ReadTransformMatrix(reader, 19),
                        reader.GetString(20),
                        reader.GetInt64(21),
                        reader.GetInt64(22),
                        reader.GetInt32(23)));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load model group candidates from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<ModelGroupInfo> LoadModelGroupsForMeshAssetId(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<ModelGroupInfo>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT
                        modelGroup.GroupAssetUniqueID,
                        modelGroup.GroupKind,
                        modelGroup.GroupName,
                        modelGroup.RootGameObjectAssetUniqueID,
                        modelGroup.RootGameObjectName,
                        modelGroup.AnimatorAssetUniqueID,
                        modelGroup.AvatarAssetUniqueID,
                        modelGroup.ControllerAssetUniqueID,
                        modelGroup.SourceFileName,
                        modelGroup.Confidence,
                        modelGroup.ConfidenceReason,
                        COUNT(groupMesh.MeshAssetUniqueID) AS PartCount,
                        MIN(groupMesh.SlotIndex) AS FirstSlot
                    FROM ModelGroups modelGroup
                    INNER JOIN ModelGroupMeshes selectedMesh
                      ON selectedMesh.ProjectId = modelGroup.ProjectId
                     AND selectedMesh.GroupAssetUniqueID = modelGroup.GroupAssetUniqueID
                     AND selectedMesh.MeshAssetUniqueID = @meshAssetId
                    INNER JOIN ModelGroupMeshes groupMesh
                      ON groupMesh.ProjectId = modelGroup.ProjectId
                     AND groupMesh.GroupAssetUniqueID = modelGroup.GroupAssetUniqueID
                    WHERE modelGroup.ProjectId = @projectId
                    GROUP BY
                        modelGroup.GroupAssetUniqueID,
                        modelGroup.GroupKind,
                        modelGroup.GroupName,
                        modelGroup.RootGameObjectAssetUniqueID,
                        modelGroup.RootGameObjectName,
                        modelGroup.AnimatorAssetUniqueID,
                        modelGroup.AvatarAssetUniqueID,
                        modelGroup.ControllerAssetUniqueID,
                        modelGroup.SourceFileName,
                        modelGroup.Confidence,
                        modelGroup.ConfidenceReason
                    ORDER BY modelGroup.Confidence DESC, PartCount ASC, FirstSlot, modelGroup.GroupName, modelGroup.GroupAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new ModelGroupInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        reader.GetInt32(9),
                        reader.GetString(10)));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load model groups for mesh from SQLite: {ex.Message}");
            }

            return result;
        }

        internal List<ModelGroupMeshInfo> LoadModelGroupMeshes(string folderPath, string signature, string groupAssetId)
        {
            var result = new List<ModelGroupMeshInfo>();
            if (string.IsNullOrWhiteSpace(groupAssetId))
            {
                return result;
            }

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction();
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return result;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT
                        GroupAssetUniqueID,
                        MeshAssetUniqueID,
                        RendererAssetUniqueID,
                        RendererType,
                        GameObjectAssetUniqueID,
                        GameObjectName,
                        SlotIndex,
                        Confidence,
                        ConfidenceReason,
                        TransformMatrix,
                        COALESCE(mesh.Name, ''),
                        COALESCE(mesh.PathID, 0),
                        COALESCE(mesh.ByteSize, 0),
                        COALESCE(mesh.Type, 0)
                    FROM ModelGroupMeshes mgm
                    LEFT JOIN Assets mesh
                      ON mesh.ProjectId = mgm.ProjectId
                     AND mesh.UniqueID = mgm.MeshAssetUniqueID
                    WHERE mgm.ProjectId = @projectId
                      AND mgm.GroupAssetUniqueID = @groupAssetId
                    ORDER BY mgm.SlotIndex, mgm.GameObjectName, mesh.Name, mgm.MeshAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                cmd.Parameters.AddWithValue("@groupAssetId", groupAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new ModelGroupMeshInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetString(8),
                        ReadTransformMatrix(reader, 9),
                        reader.GetString(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.GetInt32(13)));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load model group meshes from SQLite: {ex.Message}");
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

            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                var projectId = FindProjectId(conn, null, folderPath, signature);
                if (projectId == null)
                {
                    return null;
                }

                PreviewCacheEntry? entry = null;
                using (var cmd = conn.CreateCommand())
                {
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
                        entry = new PreviewCacheEntry(
                            reader.GetString(1),
                            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            reader.GetInt64(3));
                    }
                }

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

            var dbPath = GetDbPath(folderPath);
            var walPath = dbPath + "-wal";
            var shmPath = dbPath + "-shm";

            try
            {
                lock (WriteGate)
                {
                    // Force close any pooled connections to this database first
                    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                    {
                        SqliteConnection.ClearPool(conn);
                    }
                    using (var readConn = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = true,
                        DefaultTimeout = ReadBusyTimeoutSeconds
                    }.ToString()))
                    {
                        SqliteConnection.ClearPool(readConn);
                    }

                    if (File.Exists(dbPath))
                    {
                        File.Delete(dbPath);
                    }
                    if (File.Exists(walPath))
                    {
                        File.Delete(walPath);
                    }
                    if (File.Exists(shmPath))
                    {
                        File.Delete(shmPath);
                    }

                    lock (_initLock)
                    {
                        _initializedDbs.Remove(dbPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete SQLite index cache files for {folderPath}: {ex.Message}");
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

    internal sealed class ModelGroupInfo
    {
        public ModelGroupInfo(
            string groupId,
            string groupKind,
            string groupName,
            string rootGameObjectAssetId,
            string rootGameObjectName,
            string animatorAssetId,
            string avatarAssetId,
            string controllerAssetId,
            string sourceFileName,
            int confidence,
            string confidenceReason)
        {
            GroupId = groupId;
            GroupKind = groupKind;
            GroupName = groupName;
            RootGameObjectAssetId = rootGameObjectAssetId;
            RootGameObjectName = rootGameObjectName;
            AnimatorAssetId = animatorAssetId;
            AvatarAssetId = avatarAssetId;
            ControllerAssetId = controllerAssetId;
            SourceFileName = sourceFileName;
            Confidence = confidence;
            ConfidenceReason = confidenceReason;
        }

        public string GroupId { get; }
        public string GroupKind { get; }
        public string GroupName { get; }
        public string RootGameObjectAssetId { get; }
        public string RootGameObjectName { get; }
        public string AnimatorAssetId { get; }
        public string AvatarAssetId { get; }
        public string ControllerAssetId { get; }
        public string SourceFileName { get; }
        public int Confidence { get; }
        public string ConfidenceReason { get; }
    }

    internal sealed class ModelGroupMeshInfo
    {
        public ModelGroupMeshInfo(
            string groupId,
            string meshAssetId,
            string rendererAssetId,
            string rendererType,
            string gameObjectAssetId,
            string gameObjectName,
            int slotIndex,
            int confidence,
            string confidenceReason,
            float[]? transformMatrix,
            string meshName,
            long meshPathId,
            long meshByteSize,
            int meshType)
        {
            GroupId = groupId;
            MeshAssetId = meshAssetId;
            RendererAssetId = rendererAssetId;
            RendererType = rendererType;
            GameObjectAssetId = gameObjectAssetId;
            GameObjectName = gameObjectName;
            SlotIndex = slotIndex;
            Confidence = confidence;
            ConfidenceReason = confidenceReason;
            TransformMatrix = transformMatrix;
            MeshName = meshName;
            MeshPathId = meshPathId;
            MeshByteSize = meshByteSize;
            MeshType = meshType;
        }

        public string GroupId { get; }
        public string MeshAssetId { get; }
        public string RendererAssetId { get; }
        public string RendererType { get; }
        public string GameObjectAssetId { get; }
        public string GameObjectName { get; }
        public int SlotIndex { get; }
        public int Confidence { get; }
        public string ConfidenceReason { get; }
        public float[]? TransformMatrix { get; }
        public string MeshName { get; }
        public long MeshPathId { get; }
        public long MeshByteSize { get; }
        public int MeshType { get; }
    }

    internal sealed class ModelGroupCandidateInfo
    {
        public ModelGroupCandidateInfo(ModelGroupInfo group)
        {
            Group = group;
        }

        public ModelGroupInfo Group { get; }
        public List<ModelGroupMeshInfo> Meshes { get; } = new();
    }
}
