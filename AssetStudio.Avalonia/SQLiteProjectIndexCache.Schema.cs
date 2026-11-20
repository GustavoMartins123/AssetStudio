using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        /// <summary>
        /// v13: the physical <c>AssetHandles</c> table was folded into the clustered <c>Assets</c>
        /// table (WITHOUT ROWID, PRIMARY KEY (ProjectId, UniqueID)). Every handle is written exactly
        /// once; <c>OriginalPath</c>/<c>SerializedFileName</c> are normalized through <c>SourceFiles</c>
        /// and a read-only <c>AssetHandles</c> compatibility view keeps legacy queries working.
        /// </summary>
        internal const int SemanticSchemaVersion = 13;

        private const string AssetHandlesCompatibilityViewSql = @"
            CREATE VIEW IF NOT EXISTS AssetHandles AS
            SELECT
                Assets.ProjectId AS ProjectId,
                Assets.UniqueID AS UniqueID,
                Assets.Name AS Name,
                Assets.Type AS Type,
                Assets.Container AS Container,
                COALESCE(SourceFiles.OriginalPath, '') AS OriginalPath,
                COALESCE(SourceFiles.SerializedFileName, '') AS SerializedFileName,
                Assets.PathID AS PathID,
                Assets.ByteStart AS ByteStart,
                Assets.ByteSize AS ByteSize,
                Assets.SourceFileId AS SourceFileId
            FROM Assets
            LEFT JOIN SourceFiles
              ON SourceFiles.ProjectId = Assets.ProjectId
             AND SourceFiles.Id = Assets.SourceFileId;";

        private static bool TryUpgradeOrRebuildSchema(SqliteConnection conn)
        {
            var existingVersion = GetExistingSchemaVersion(conn);
            if (existingVersion == null || existingVersion == SemanticSchemaVersion)
            {
                return false;
            }

            if ((existingVersion == 11 || existingVersion == 12) && CanPreserveV11ProjectsAndPreviewCache(conn))
            {
                if (existingVersion == 12 && CanPreserveV12UnifiedAssets(conn))
                {
                    // v12 Assets already holds every handle column (paths via SourceFiles): keep the
                    // whole index cache and just drop the duplicated physical AssetHandles table.
                    UpgradeV12ToV13InPlace(conn);
                }
                else
                {
                    DropLegacyIndexTables(conn);
                }

                return true;
            }

            RebuildSchema(conn);
            return true;
        }

        private static int? GetExistingSchemaVersion(SqliteConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                var tableCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (tableCount == 0)
                {
                    return null;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'CacheSchema'";
                var hasSchemaTable = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                if (!hasSchemaTable)
                {
                    return -1;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1";
                var versionText = cmd.ExecuteScalar()?.ToString();
                return int.TryParse(versionText, out var version) ? version : -1;
            }
        }

        private static bool CanPreserveV11ProjectsAndPreviewCache(SqliteConnection conn)
        {
            var projectColumns = GetTableColumnNames(conn, "Projects");
            var previewColumns = GetTableColumnNames(conn, "PreviewCacheEntries");

            return projectColumns.Contains("Id")
                && projectColumns.Contains("FolderPath")
                && projectColumns.Contains("SignatureHash")
                && projectColumns.Contains("TotalFiles")
                && projectColumns.Contains("TotalBytes")
                && projectColumns.Contains("UnityBundleCount")
                && projectColumns.Contains("LastIndexed")
                && projectColumns.Contains("UnityVersion")
                && previewColumns.Contains("Id")
                && previewColumns.Contains("ProjectId")
                && previewColumns.Contains("AssetUniqueID")
                && previewColumns.Contains("PreviewKind")
                && previewColumns.Contains("AlgorithmVersion")
                && previewColumns.Contains("Parameters")
                && previewColumns.Contains("PayloadHash")
                && previewColumns.Contains("PayloadPath")
                && previewColumns.Contains("ByteSize");
        }

        private static HashSet<string> GetTableColumnNames(SqliteConnection conn, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var infoCmd = conn.CreateCommand();
            infoCmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    columns.Add(reader.GetString(1));
                }
            }

            return columns;
        }

        private static bool CanPreserveV12UnifiedAssets(SqliteConnection conn)
        {
            string assetsDdl;
            using (var ddlCmd = conn.CreateCommand())
            {
                ddlCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Assets' LIMIT 1";
                assetsDdl = ddlCmd.ExecuteScalar()?.ToString() ?? string.Empty;
            }

            if (assetsDdl.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var assetColumns = GetTableColumnNames(conn, "Assets");
            var sourceColumns = GetTableColumnNames(conn, "SourceFiles");
            var hasExpectedColumns = assetColumns.Contains("ProjectId")
                && assetColumns.Contains("UniqueID")
                && assetColumns.Contains("SourceFileId")
                && assetColumns.Contains("Name")
                && assetColumns.Contains("Type")
                && assetColumns.Contains("Container")
                && assetColumns.Contains("PathID")
                && assetColumns.Contains("ByteStart")
                && assetColumns.Contains("ByteSize")
                && sourceColumns.Contains("Id")
                && sourceColumns.Contains("SerializedFileName")
                && sourceColumns.Contains("OriginalPath");
            if (!hasExpectedColumns)
            {
                return false;
            }

            // Every asset row must resolve its origin through SourceFiles, otherwise handles would
            // hydrate without OriginalPath/SerializedFileName; fall back to a re-index in that case.
            using var orphanCmd = conn.CreateCommand();
            orphanCmd.CommandText = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM Assets
                    LEFT JOIN SourceFiles ON SourceFiles.Id = Assets.SourceFileId
                    WHERE SourceFiles.Id IS NULL
                )";
            return Convert.ToInt64(orphanCmd.ExecuteScalar()) == 0;
        }

        private static void UpgradeV12ToV13InPlace(SqliteConnection conn)
        {
            DropAssetHandlesSchemaObject(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DROP INDEX IF EXISTS idx_handles_lookup;
                DROP INDEX IF EXISTS idx_projects_path;";
            cmd.ExecuteNonQuery();
        }

        private static void DropLegacyIndexTables(SqliteConnection conn)
        {
            DropAssetHandlesSchemaObject(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA foreign_keys = OFF;
                DROP TABLE IF EXISTS IndexingReadFiles;
                DROP TABLE IF EXISTS ProjectIndexingState;
                DROP TABLE IF EXISTS MaterialTextures;
                DROP TABLE IF EXISTS MeshMaterials;
                DROP TABLE IF EXISTS MeshRenderers;
                DROP TABLE IF EXISTS ModelGroupMeshes;
                DROP TABLE IF EXISTS ModelGroups;
                DROP TABLE IF EXISTS AssetEdges;
                DROP TABLE IF EXISTS Assets;
                DROP TABLE IF EXISTS SourceFiles;
                DROP INDEX IF EXISTS idx_projects_path;
                PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }

        private static void RebuildSchema(SqliteConnection conn)
        {
            DropAssetHandlesSchemaObject(conn);
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
                    DROP TABLE IF EXISTS Projects;
                    DROP TABLE IF EXISTS CacheSchema;
                    PRAGMA foreign_keys = ON;";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Drops <c>AssetHandles</c> whether it is the legacy (v12 and older) physical table or the
        /// v13 compatibility view. SQLite rejects <c>DROP TABLE</c> on a view and vice versa, even
        /// with <c>IF EXISTS</c>, so the object type is resolved first.
        /// </summary>
        private static void DropAssetHandlesSchemaObject(SqliteConnection conn)
        {
            string? objectType;
            using (var typeCmd = conn.CreateCommand())
            {
                typeCmd.CommandText = "SELECT type FROM sqlite_master WHERE name = 'AssetHandles' AND type IN ('table', 'view') LIMIT 1";
                objectType = typeCmd.ExecuteScalar()?.ToString();
            }

            if (string.IsNullOrEmpty(objectType))
            {
                return;
            }

            using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = string.Equals(objectType, "view", StringComparison.OrdinalIgnoreCase)
                ? "DROP VIEW IF EXISTS AssetHandles;"
                : "PRAGMA foreign_keys = OFF; DROP INDEX IF EXISTS idx_handles_lookup; DROP TABLE IF EXISTS AssetHandles; PRAGMA foreign_keys = ON;";
            dropCmd.ExecuteNonQuery();
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

                            CREATE INDEX IF NOT EXISTS idx_projects_lookup ON Projects(FolderPath COLLATE NOCASE, SignatureHash, LastIndexed DESC);
                            DROP INDEX IF EXISTS idx_projects_path;
                            DROP INDEX IF EXISTS idx_handles_project;
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
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                UniqueID TEXT NOT NULL,
                                SourceFileId INTEGER REFERENCES SourceFiles(Id) ON DELETE SET NULL,
                                Name TEXT NOT NULL,
                                Type INTEGER NOT NULL,
                                Container TEXT NOT NULL DEFAULT '',
                                PathID INTEGER NOT NULL,
                                ByteStart INTEGER NOT NULL,
                                ByteSize INTEGER NOT NULL,
                                PRIMARY KEY (ProjectId, UniqueID)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS AssetEdges (
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
                                IsResolved INTEGER NOT NULL DEFAULT 0,
                                PRIMARY KEY (ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID)
                            ) WITHOUT ROWID;

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
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MeshAssetUniqueID TEXT NOT NULL,
                                RendererAssetUniqueID TEXT NOT NULL,
                                RendererType TEXT NOT NULL,
                                GameObjectAssetUniqueID TEXT,
                                GameObjectName TEXT,
                                Description TEXT,
                                PRIMARY KEY (ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS MeshMaterials (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MeshAssetUniqueID TEXT NOT NULL,
                                RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                                SubMeshIndex INTEGER NOT NULL,
                                MaterialSlotIndex INTEGER NOT NULL DEFAULT -1,
                                MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererType TEXT NOT NULL DEFAULT '',
                                MaterialScore INTEGER NOT NULL DEFAULT 0,
                                PRIMARY KEY (ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS MaterialTextures (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MaterialAssetUniqueID TEXT NOT NULL,
                                SlotName TEXT NOT NULL,
                                SlotIndex INTEGER NOT NULL,
                                TextureFileId INTEGER NOT NULL DEFAULT 0,
                                TexturePathID INTEGER NOT NULL DEFAULT 0,
                                TextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                                PreviewMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                IsResolved INTEGER NOT NULL DEFAULT 0,
                                IsMainTexture INTEGER NOT NULL DEFAULT 0,
                                PRIMARY KEY (ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                            ) WITHOUT ROWID;

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
                                LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                                UpdatedUtc TEXT NOT NULL DEFAULT '',
                                LastAccessedUtc TEXT NOT NULL DEFAULT '',
                                PayloadBlob BLOB,
                                PayloadCompression INTEGER NOT NULL DEFAULT 0,
                                UncompressedByteSize INTEGER NOT NULL DEFAULT 0
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
                            VALUES ('SemanticSchemaVersion', '{SemanticSchemaVersion.ToString(CultureInfo.InvariantCulture)}');

                            CREATE UNIQUE INDEX IF NOT EXISTS idx_source_files_unique ON SourceFiles(ProjectId, SerializedFileName, OriginalPath);
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_target_kind ON AssetEdges(ProjectId, TargetAssetUniqueID, EdgeKind, SlotName, SourceAssetUniqueID, SlotIndex);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_model_groups_unique ON ModelGroups(ProjectId, GroupAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_groups_avatar ON ModelGroups(ProjectId, AvatarAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_model_group_meshes_unique ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_group ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, SlotIndex, MeshAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_candidate ON ModelGroupMeshes(ProjectId, MeshAssetUniqueID, GroupAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_preview_lookup ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID, SlotName, IsMainTexture DESC, SlotIndex, TextureAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_preview_cache_unique ON PreviewCacheEntries(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters);
                            CREATE INDEX IF NOT EXISTS idx_indexing_read_files_project ON IndexingReadFiles(ProjectId, ReadOrder);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_indexing_read_files_unique ON IndexingReadFiles(ProjectId, FilePath);
                        ";
                cmd.ExecuteNonQuery();
            }

            using (var viewCmd = conn.CreateCommand())
            {
                viewCmd.CommandText = AssetHandlesCompatibilityViewSql;
                viewCmd.ExecuteNonQuery();
            }

            EnsurePreviewCacheSchemaUpgrades(conn);
        }

        private static void DropSemanticBulkSecondaryIndexes(SqliteConnection conn, SqliteTransaction transaction)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                DROP INDEX IF EXISTS idx_asset_edges_target_kind;
                DROP INDEX IF EXISTS idx_model_groups_avatar;
                DROP INDEX IF EXISTS idx_model_group_meshes_group;
                DROP INDEX IF EXISTS idx_model_group_meshes_candidate;
                DROP INDEX IF EXISTS idx_material_textures_preview_lookup;";
            cmd.ExecuteNonQuery();
        }

        private static void CreateSemanticBulkSecondaryIndexes(SqliteConnection conn, SqliteTransaction transaction)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_asset_edges_target_kind ON AssetEdges(ProjectId, TargetAssetUniqueID, EdgeKind, SlotName, SourceAssetUniqueID, SlotIndex);
                CREATE INDEX IF NOT EXISTS idx_model_groups_avatar ON ModelGroups(ProjectId, AvatarAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_group_meshes_group ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, SlotIndex, MeshAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_group_meshes_candidate ON ModelGroupMeshes(ProjectId, MeshAssetUniqueID, GroupAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_material_textures_preview_lookup ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID, SlotName, IsMainTexture DESC, SlotIndex, TextureAssetUniqueID);";
            cmd.ExecuteNonQuery();
        }

        private static void EnsurePreviewCacheSchemaUpgrades(SqliteConnection conn)
        {
            var columns = GetTableColumnNames(conn, "PreviewCacheEntries");

            if (!columns.Contains("UpdatedUtc"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = @"
                    ALTER TABLE PreviewCacheEntries ADD COLUMN UpdatedUtc TEXT NOT NULL DEFAULT '';
                    UPDATE PreviewCacheEntries
                    SET UpdatedUtc = COALESCE(NULLIF(CreatedAt, ''), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    WHERE UpdatedUtc = '';";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("LastAccessedUtc"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = @"
                    ALTER TABLE PreviewCacheEntries ADD COLUMN LastAccessedUtc TEXT NOT NULL DEFAULT '';
                    UPDATE PreviewCacheEntries
                    SET LastAccessedUtc = COALESCE(NULLIF(LastAccessed, ''), NULLIF(UpdatedUtc, ''), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    WHERE LastAccessedUtc = '';";
                alterCmd.ExecuteNonQuery();
            }

            // v13 inline preview payload columns (idempotent for preserved v11/v12 tables).
            if (!columns.Contains("PayloadBlob"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE PreviewCacheEntries ADD COLUMN PayloadBlob BLOB;";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("PayloadCompression"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE PreviewCacheEntries ADD COLUMN PayloadCompression INTEGER NOT NULL DEFAULT 0;";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("UncompressedByteSize"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE PreviewCacheEntries ADD COLUMN UncompressedByteSize INTEGER NOT NULL DEFAULT 0;";
                alterCmd.ExecuteNonQuery();
            }

            using (var indexCmd = conn.CreateCommand())
            {
                indexCmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS IX_PreviewCacheEntries_LastAccessed
                    ON PreviewCacheEntries(LastAccessedUtc, UpdatedUtc, Id);";
                indexCmd.ExecuteNonQuery();
            }
        }
    }
}
