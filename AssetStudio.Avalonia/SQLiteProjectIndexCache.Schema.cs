using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        private const int SemanticSchemaVersion = 11;

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
                                LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                                UpdatedUtc TEXT NOT NULL DEFAULT '',
                                LastAccessedUtc TEXT NOT NULL DEFAULT ''
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

            EnsurePreviewCacheSchemaUpgrades(conn);
        }

        private static void EnsurePreviewCacheSchemaUpgrades(SqliteConnection conn)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var infoCmd = conn.CreateCommand())
            {
                infoCmd.CommandText = "PRAGMA table_info(PreviewCacheEntries);";
                using var reader = infoCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(1))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
            }

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
