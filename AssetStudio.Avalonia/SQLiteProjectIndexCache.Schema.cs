using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        /// <summary>
        /// v16: adds deterministic manifest v2 fingerprinting (<c>Projects.FolderPathKey</c>,
        /// <c>Projects.SignatureVersion</c>), non-NOCASE <c>idx_projects_lookup</c> for Linux
        /// case-sensitivity, incremental bundle fingerprint metadata on <c>SourceFiles</c>
        /// (<c>RelativePath</c>, <c>FileSize</c>, <c>LastWriteUtcTicks</c>, <c>ContentSampleHash</c>,
        /// <c>FileKind</c>), and unified scene/external dependency tables (<c>SourceFileExternals</c>,
        /// <c>Scenes</c>, both WITHOUT ROWID).
        /// </summary>
        internal const int SemanticSchemaVersion = 16;

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

        private static bool TryUpgradeOrRebuildSchema(SqliteConnection conn, bool caseSensitivePaths)
        {
            var existingVersion = GetExistingSchemaVersion(conn);
            if (existingVersion == null || existingVersion == SemanticSchemaVersion)
            {
                return false;
            }

            if ((existingVersion == 11 || existingVersion == 12 || existingVersion == 13 || existingVersion == 14 || existingVersion == 15)
                && CanPreserveV11ProjectsAndPreviewCache(conn))
            {
                if (existingVersion == 15 && CanPreserveV12UnifiedAssets(conn))
                {
                    // v15 -> v16 in-place upgrade preserves Projects/SourceFiles/Assets/PreviewCacheEntries
                    // and all v15 semantic tables + valid indexing status, adding v16 fingerprint & scene schema.
                    UpgradeV15ToV16InPlace(conn, caseSensitivePaths);
                }
                else if (existingVersion == 14 && CanPreserveV12UnifiedAssets(conn))
                {
                    // v14 -> v15 -> v16 in-place upgrade preserves everything including PreviewCacheEntries.
                    UpgradeV14ToV15InPlace(conn);
                    UpgradeV15ToV16InPlace(conn, caseSensitivePaths);
                }
                else if ((existingVersion == 12 || existingVersion == 13) && CanPreserveV12UnifiedAssets(conn))
                {
                    // v12/v13 Assets already holds every handle column (paths via SourceFiles): keep the
                    // whole index cache and preview cache in place and add v14/v15/v16 tables & columns.
                    UpgradeV12OrV13ToV15InPlace(conn);
                    UpgradeV15ToV16InPlace(conn, caseSensitivePaths);
                }
                else
                {
                    DropLegacyIndexTables(conn);
                    EnsureV16ProjectsColumnsAndIndex(conn, transaction: null, caseSensitivePaths);
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

        private static HashSet<string> GetTableColumnNames(SqliteConnection conn, string tableName, SqliteTransaction? transaction = null)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var infoCmd = conn.CreateCommand();
            infoCmd.Transaction = transaction;
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

        private static void UpgradeV12OrV13ToV15InPlace(SqliteConnection conn)
        {
            DropAssetHandlesSchemaObject(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DROP INDEX IF EXISTS idx_handles_lookup;
                DROP INDEX IF EXISTS idx_projects_path;";
            cmd.ExecuteNonQuery();

            EnsureMaterialTexturesV15Columns(conn, transaction: null);

            if (GetTableColumnNames(conn, "ProjectIndexingState").Contains("Status"))
            {
                using var statusCmd = conn.CreateCommand();
                statusCmd.CommandText = @"
                    UPDATE ProjectIndexingState
                    SET Status = 'completed',
                        CurrentFile = 'Schema upgraded to v15; connections ready to rebuild'
                    WHERE Status IN ('connections_completed', 'structure_completed', 'building_structure');";
                statusCmd.ExecuteNonQuery();
            }
        }

        private static void UpgradeV14ToV15InPlace(SqliteConnection conn)
        {
            using var transaction = conn.BeginTransaction();
            EnsureMaterialTexturesV15Columns(conn, transaction);
            EnsureV15DerivedCacheTablesAndIndexes(conn, transaction);

            // Clean any empty v14 AnimationClipTargets rows where Avatar, Mesh, and ModelGroupId are all empty.
            if (GetTableColumnNames(conn, "AnimationClipTargets", transaction).Contains("ClipAssetUniqueID"))
            {
                using var cleanCmd = conn.CreateCommand();
                cleanCmd.Transaction = transaction;
                cleanCmd.CommandText = @"
                    DELETE FROM AnimationClipTargets
                    WHERE AvatarAssetUniqueID = ''
                      AND MeshAssetUniqueID = ''
                      AND ModelGroupId = '';";
                cleanCmd.ExecuteNonQuery();
            }

            // Fast set-based SQL backfill of MaterialCache, MeshMaterialResolvedCache, CompositeAssets, and CompositeAssetParts
            // for every existing project without resetting valid structure_completed / connections_completed status.
            var projectIds = new List<long>();
            using (var projCmd = conn.CreateCommand())
            {
                projCmd.Transaction = transaction;
                projCmd.CommandText = "SELECT Id FROM Projects;";
                using var reader = projCmd.ExecuteReader();
                while (reader.Read())
                {
                    projectIds.Add(reader.GetInt64(0));
                }
            }

            foreach (var projectId in projectIds)
            {
                BackfillV15DerivedCachesForProject(conn, transaction, projectId);
            }

            transaction.Commit();
        }

        private static void UpgradeV15ToV16InPlace(SqliteConnection conn, bool caseSensitivePaths)
        {
            using var transaction = conn.BeginTransaction();
            EnsureV16ProjectsColumnsAndIndex(conn, transaction, caseSensitivePaths);
            EnsureV16SourceFilesColumns(conn, transaction);
            EnsureV16SceneTablesAndIndexes(conn, transaction);
            transaction.Commit();
        }

        private static void EnsureV16ProjectsColumnsAndIndex(SqliteConnection conn, SqliteTransaction? transaction, bool caseSensitivePaths)
        {
            var projectColumns = GetTableColumnNames(conn, "Projects", transaction);
            if (projectColumns.Count == 0)
            {
                return;
            }

            if (!projectColumns.Contains("FolderPathKey"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = transaction;
                alterCmd.CommandText = "ALTER TABLE Projects ADD COLUMN FolderPathKey TEXT NOT NULL DEFAULT '';";
                alterCmd.ExecuteNonQuery();
            }

            if (!projectColumns.Contains("SignatureVersion"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = transaction;
                alterCmd.CommandText = "ALTER TABLE Projects ADD COLUMN SignatureVersion INTEGER NOT NULL DEFAULT 1;";
                alterCmd.ExecuteNonQuery();
            }

            // Backfill FolderPathKey and SignatureVersion for any migrated rows missing them.
            var rowsToBackfill = new List<(long Id, string FolderPath, string SignatureHash)>();
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = "SELECT Id, FolderPath, SignatureHash FROM Projects WHERE FolderPathKey = '';";
                using var reader = selectCmd.ExecuteReader();
                while (reader.Read())
                {
                    rowsToBackfill.Add((
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                }
            }

            if (rowsToBackfill.Count > 0)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"
                    UPDATE Projects
                    SET FolderPathKey = @folderPathKey,
                        SignatureVersion = @sigVersion
                    WHERE Id = @id;";
                var pKey = updateCmd.Parameters.Add("@folderPathKey", SqliteType.Text);
                var pSigVer = updateCmd.Parameters.Add("@sigVersion", SqliteType.Integer);
                var pId = updateCmd.Parameters.Add("@id", SqliteType.Integer);

                foreach (var (id, folderPath, signatureHash) in rowsToBackfill)
                {
                    pKey.Value = NormalizeFolderPathKey(folderPath, caseSensitivePaths);
                    pSigVer.Value = signatureHash.StartsWith("v2:", StringComparison.Ordinal) ? 2 : 1;
                    pId.Value = id;
                    updateCmd.ExecuteNonQuery();
                }
            }

            // Recreate idx_projects_lookup without COLLATE NOCASE if it is missing or still uses legacy FolderPath/NOCASE.
            string? existingIndexSql;
            using (var idxCmd = conn.CreateCommand())
            {
                idxCmd.Transaction = transaction;
                idxCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'idx_projects_lookup' LIMIT 1;";
                existingIndexSql = idxCmd.ExecuteScalar()?.ToString();
            }

            var needsRecreate = string.IsNullOrWhiteSpace(existingIndexSql)
                || existingIndexSql.IndexOf("NOCASE", StringComparison.OrdinalIgnoreCase) >= 0
                || existingIndexSql.IndexOf("FolderPathKey", StringComparison.OrdinalIgnoreCase) < 0;

            if (needsRecreate)
            {
                using var recreateCmd = conn.CreateCommand();
                recreateCmd.Transaction = transaction;
                recreateCmd.CommandText = @"
                    DROP INDEX IF EXISTS idx_projects_lookup;
                    CREATE INDEX IF NOT EXISTS idx_projects_lookup ON Projects(FolderPathKey, SignatureHash, LastIndexed DESC);";
                recreateCmd.ExecuteNonQuery();
            }
        }

        private static void EnsureV16SourceFilesColumns(SqliteConnection conn, SqliteTransaction? transaction)
        {
            var sfColumns = GetTableColumnNames(conn, "SourceFiles", transaction);
            if (sfColumns.Count == 0)
            {
                return;
            }

            var additions = new (string Name, string Ddl)[]
            {
                ("RelativePath", "ALTER TABLE SourceFiles ADD COLUMN RelativePath TEXT NOT NULL DEFAULT '';"),
                ("FileSize", "ALTER TABLE SourceFiles ADD COLUMN FileSize INTEGER NOT NULL DEFAULT 0;"),
                ("LastWriteUtcTicks", "ALTER TABLE SourceFiles ADD COLUMN LastWriteUtcTicks INTEGER NOT NULL DEFAULT 0;"),
                ("ContentSampleHash", "ALTER TABLE SourceFiles ADD COLUMN ContentSampleHash INTEGER NOT NULL DEFAULT 0;"),
                ("FileKind", "ALTER TABLE SourceFiles ADD COLUMN FileKind INTEGER NOT NULL DEFAULT 0;")
            };

            foreach (var (name, ddl) in additions)
            {
                if (!sfColumns.Contains(name))
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.Transaction = transaction;
                    alterCmd.CommandText = ddl;
                    alterCmd.ExecuteNonQuery();
                }
            }
        }

        private static void EnsureV16SceneTablesAndIndexes(SqliteConnection conn, SqliteTransaction? transaction)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SourceFileExternals (
                    ProjectId INTEGER NOT NULL,
                    SourceFileId INTEGER NOT NULL,
                    ExternalIndex INTEGER NOT NULL,
                    ExternalFileName TEXT NOT NULL DEFAULT '',
                    ExternalPathName TEXT NOT NULL DEFAULT '',
                    ExternalGuid TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (ProjectId, SourceFileId, ExternalIndex),
                    FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS Scenes (
                    ProjectId INTEGER NOT NULL,
                    SceneId INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    ScenePath TEXT NOT NULL DEFAULT '',
                    BuildIndex INTEGER NOT NULL DEFAULT -1,
                    EntrySourceFileId INTEGER NOT NULL DEFAULT 0,
                    ContainerKey TEXT NOT NULL DEFAULT '',
                    SourceKind TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (ProjectId, SceneId),
                    FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS idx_source_externals_lookup ON SourceFileExternals(ProjectId, SourceFileId);
                CREATE INDEX IF NOT EXISTS idx_scenes_project_build ON Scenes(ProjectId, BuildIndex, Name);";
            cmd.ExecuteNonQuery();
        }

        private static void EnsureMaterialTexturesV15Columns(SqliteConnection conn, SqliteTransaction? transaction)
        {
            var mtColumns = GetTableColumnNames(conn, "MaterialTextures", transaction);
            if (mtColumns.Count == 0)
            {
                return;
            }

            var additions = new (string Name, string Ddl)[]
            {
                ("ScaleX", "ALTER TABLE MaterialTextures ADD COLUMN ScaleX REAL NOT NULL DEFAULT 1.0;"),
                ("ScaleY", "ALTER TABLE MaterialTextures ADD COLUMN ScaleY REAL NOT NULL DEFAULT 1.0;"),
                ("OffsetX", "ALTER TABLE MaterialTextures ADD COLUMN OffsetX REAL NOT NULL DEFAULT 0.0;"),
                ("OffsetY", "ALTER TABLE MaterialTextures ADD COLUMN OffsetY REAL NOT NULL DEFAULT 0.0;"),
                ("ResolutionSource", "ALTER TABLE MaterialTextures ADD COLUMN ResolutionSource TEXT NOT NULL DEFAULT 'preparation';")
            };

            foreach (var (name, ddl) in additions)
            {
                if (!mtColumns.Contains(name))
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.Transaction = transaction;
                    alterCmd.CommandText = ddl;
                    alterCmd.ExecuteNonQuery();
                }
            }
        }

        private static void EnsureV15DerivedCacheTablesAndIndexes(SqliteConnection conn, SqliteTransaction? transaction)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS MaterialCache (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MaterialAssetUniqueID TEXT NOT NULL,
                    MaterialName TEXT NOT NULL DEFAULT '',
                    ParentMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureSlotName TEXT NOT NULL DEFAULT '',
                    BaseColorR REAL NOT NULL DEFAULT 1.0,
                    BaseColorG REAL NOT NULL DEFAULT 1.0,
                    BaseColorB REAL NOT NULL DEFAULT 1.0,
                    BaseColorA REAL NOT NULL DEFAULT 1.0,
                    ShaderName TEXT NOT NULL DEFAULT '',
                    TextureSlotCount INTEGER NOT NULL DEFAULT 0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, MaterialAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS MeshMaterialResolvedCache (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MeshAssetUniqueID TEXT NOT NULL,
                    SubMeshIndex INTEGER NOT NULL,
                    MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    MaterialName TEXT NOT NULL DEFAULT '',
                    MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureSlotName TEXT NOT NULL DEFAULT '',
                    BaseColorR REAL NOT NULL DEFAULT 1.0,
                    BaseColorG REAL NOT NULL DEFAULT 1.0,
                    BaseColorB REAL NOT NULL DEFAULT 1.0,
                    BaseColorA REAL NOT NULL DEFAULT 1.0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, MeshAssetUniqueID, SubMeshIndex)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS CompositeAssets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    CompositeAssetUniqueID TEXT NOT NULL,
                    CompositeKind TEXT NOT NULL,
                    DisplayName TEXT NOT NULL DEFAULT '',
                    RootGameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ControllerAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SourceFileName TEXT NOT NULL DEFAULT '',
                    MeshPartCount INTEGER NOT NULL DEFAULT 0,
                    DistinctMeshCount INTEGER NOT NULL DEFAULT 0,
                    DistinctMaterialCount INTEGER NOT NULL DEFAULT 0,
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, CompositeAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS CompositeAssetParts (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    CompositeAssetUniqueID TEXT NOT NULL,
                    PartIndex INTEGER NOT NULL,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    GameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    GameObjectName TEXT NOT NULL DEFAULT '',
                    TransformMatrix BLOB,
                    PrimaryMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, CompositeAssetUniqueID, PartIndex)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS idx_asset_edges_kind_source ON AssetEdges(ProjectId, EdgeKind, SourceAssetUniqueID, SlotName, TargetAssetUniqueID, SlotIndex);
                CREATE INDEX IF NOT EXISTS idx_model_groups_animator ON ModelGroups(ProjectId, AnimatorAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_groups_controller ON ModelGroups(ProjectId, ControllerAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_material_cache_main_tex ON MaterialCache(ProjectId, MainTextureAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_mesh_mat_resolved_material ON MeshMaterialResolvedCache(ProjectId, MaterialAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_assets_root_go ON CompositeAssets(ProjectId, RootGameObjectAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_assets_avatar ON CompositeAssets(ProjectId, AvatarAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_assets_animator ON CompositeAssets(ProjectId, AnimatorAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_parts_mesh ON CompositeAssetParts(ProjectId, MeshAssetUniqueID, CompositeAssetUniqueID);";
            cmd.ExecuteNonQuery();
        }

        private static void BackfillV15DerivedCachesForProject(SqliteConnection conn, SqliteTransaction transaction, long projectId)
        {
            // 1. Backfill MaterialCache from MaterialTextures + MeshMaterials + Assets using streaming PK window pass (0 unindexed CTE joins)
            var mtCols = GetTableColumnNames(conn, "MaterialTextures", transaction);
            var mmCols = GetTableColumnNames(conn, "MeshMaterials", transaction);
            if (mtCols.Contains("MaterialAssetUniqueID"))
            {
                using var matCmd = conn.CreateCommand();
                matCmd.Transaction = transaction;
                matCmd.CommandText = @"
                    WITH RankedMainTex AS (
                        SELECT
                            mt.MaterialAssetUniqueID,
                            mt.TextureAssetUniqueID,
                            mt.SlotName,
                            mt.PreviewMaterialAssetUniqueID,
                            COUNT(*) OVER (PARTITION BY mt.MaterialAssetUniqueID) AS SlotCount,
                            ROW_NUMBER() OVER (
                                PARTITION BY mt.MaterialAssetUniqueID
                                ORDER BY
                                    CASE WHEN mt.TextureAssetUniqueID <> '' THEN 0 ELSE 1 END,
                                    mt.IsMainTexture DESC,
                                    CASE mt.SlotName
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
                                    END,
                                    mt.SlotIndex ASC,
                                    mt.TextureAssetUniqueID ASC
                            ) AS RowNum
                        FROM MaterialTextures mt
                        WHERE mt.ProjectId = @projectId
                          AND mt.MaterialAssetUniqueID <> ''
                    )
                    INSERT INTO MaterialCache (
                        ProjectId, MaterialAssetUniqueID, MaterialName, ParentMaterialAssetUniqueID,
                        MainTextureAssetUniqueID, MainTextureSlotName,
                        BaseColorR, BaseColorG, BaseColorB, BaseColorA,
                        ShaderName, TextureSlotCount, ResolutionSource)
                    SELECT
                        @projectId,
                        rmt.MaterialAssetUniqueID,
                        COALESCE(a.Name, ''),
                        CASE WHEN COALESCE(rmt.PreviewMaterialAssetUniqueID, '') <> '' AND rmt.PreviewMaterialAssetUniqueID <> rmt.MaterialAssetUniqueID
                             THEN rmt.PreviewMaterialAssetUniqueID ELSE '' END,
                        COALESCE(rmt.TextureAssetUniqueID, ''),
                        CASE WHEN COALESCE(rmt.TextureAssetUniqueID, '') <> '' THEN COALESCE(rmt.SlotName, '') ELSE '' END,
                        1.0, 1.0, 1.0, 1.0,
                        '',
                        COALESCE(rmt.SlotCount, 0),
                        'preparation'
                    FROM RankedMainTex rmt
                    LEFT JOIN Assets a
                      ON a.ProjectId = @projectId
                     AND a.UniqueID = rmt.MaterialAssetUniqueID
                    WHERE rmt.RowNum = 1
                    ON CONFLICT(ProjectId, MaterialAssetUniqueID)
                    DO UPDATE SET
                        MaterialName = CASE WHEN MaterialCache.MaterialName = '' THEN excluded.MaterialName ELSE MaterialCache.MaterialName END,
                        ParentMaterialAssetUniqueID = CASE WHEN MaterialCache.ParentMaterialAssetUniqueID = '' THEN excluded.ParentMaterialAssetUniqueID ELSE MaterialCache.ParentMaterialAssetUniqueID END,
                        MainTextureAssetUniqueID = CASE WHEN MaterialCache.MainTextureAssetUniqueID = '' THEN excluded.MainTextureAssetUniqueID ELSE MaterialCache.MainTextureAssetUniqueID END,
                        MainTextureSlotName = CASE WHEN MaterialCache.MainTextureSlotName = '' THEN excluded.MainTextureSlotName ELSE MaterialCache.MainTextureSlotName END,
                        TextureSlotCount = MAX(MaterialCache.TextureSlotCount, excluded.TextureSlotCount);";
                matCmd.Parameters.AddWithValue("@projectId", projectId);
                matCmd.ExecuteNonQuery();
            }

            if (mmCols.Contains("MaterialAssetUniqueID"))
            {
                using var matFromMeshCmd = conn.CreateCommand();
                matFromMeshCmd.Transaction = transaction;
                matFromMeshCmd.CommandText = @"
                    INSERT OR IGNORE INTO MaterialCache (
                        ProjectId, MaterialAssetUniqueID, MaterialName, ResolutionSource)
                    SELECT DISTINCT
                        @projectId,
                        mm.MaterialAssetUniqueID,
                        COALESCE(a.Name, ''),
                        'preparation'
                    FROM MeshMaterials mm
                    LEFT JOIN Assets a
                      ON a.ProjectId = @projectId
                     AND a.UniqueID = mm.MaterialAssetUniqueID
                    WHERE mm.ProjectId = @projectId
                      AND mm.MaterialAssetUniqueID <> '';";
                matFromMeshCmd.Parameters.AddWithValue("@projectId", projectId);
                matFromMeshCmd.ExecuteNonQuery();
            }

            // 2. Backfill MeshMaterialResolvedCache using streaming window pass over MeshMaterials + PK join into MaterialCache
            if (mmCols.Contains("MeshAssetUniqueID"))
            {
                using var meshMatCmd = conn.CreateCommand();
                meshMatCmd.Transaction = transaction;
                meshMatCmd.CommandText = @"
                    WITH WithRendererStats AS (
                        SELECT
                            mm.MeshAssetUniqueID,
                            mm.SubMeshIndex,
                            mm.MaterialAssetUniqueID,
                            mm.RendererAssetUniqueID,
                            mm.RendererType,
                            mm.MaterialScore,
                            mm.MaterialSlotIndex,
                            COALESCE(mc.MaterialName, '') AS MaterialName,
                            COALESCE(mc.MainTextureAssetUniqueID, '') AS MainTextureAssetUniqueID,
                            COALESCE(mc.MainTextureSlotName, '') AS MainTextureSlotName,
                            COALESCE(mc.BaseColorR, 1.0) AS BaseColorR,
                            COALESCE(mc.BaseColorG, 1.0) AS BaseColorG,
                            COALESCE(mc.BaseColorB, 1.0) AS BaseColorB,
                            COALESCE(mc.BaseColorA, 1.0) AS BaseColorA,
                            COALESCE(NULLIF(mc.ResolutionSource, ''), 'preparation') AS ResolutionSource,
                            CASE WHEN COALESCE(mc.MainTextureAssetUniqueID, '') <> '' THEN 1 ELSE 0 END AS HasMainTexture,
                            CASE WHEN COALESCE(mc.TextureSlotCount, 0) > 0 OR COALESCE(mc.MainTextureAssetUniqueID, '') <> '' THEN 1 ELSE 0 END AS HasTexture,
                            MAX(CASE WHEN mm.RendererType <> 'Container' THEN 1 ELSE 0 END) OVER (PARTITION BY mm.MeshAssetUniqueID, mm.RendererAssetUniqueID) AS RendererScore,
                            SUM(CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 1 ELSE 0 END) OVER (PARTITION BY mm.MeshAssetUniqueID, mm.RendererAssetUniqueID) AS MaterialCount,
                            COUNT(*) OVER (PARTITION BY mm.MeshAssetUniqueID, mm.RendererAssetUniqueID) AS RelationCount,
                            SUM(CASE WHEN COALESCE(mc.MainTextureAssetUniqueID, '') <> '' THEN 1 ELSE 0 END) OVER (PARTITION BY mm.MeshAssetUniqueID, mm.RendererAssetUniqueID) AS MainTextureCount,
                            SUM(CASE WHEN COALESCE(mc.TextureSlotCount, 0) > 0 OR COALESCE(mc.MainTextureAssetUniqueID, '') <> '' THEN 1 ELSE 0 END) OVER (PARTITION BY mm.MeshAssetUniqueID, mm.RendererAssetUniqueID) AS TextureCount
                        FROM MeshMaterials mm
                        LEFT JOIN MaterialCache mc
                          ON mc.ProjectId = mm.ProjectId
                         AND mc.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                        WHERE mm.ProjectId = @projectId
                    ),
                    Ranked AS (
                        SELECT
                            w.MeshAssetUniqueID,
                            w.SubMeshIndex,
                            w.MaterialAssetUniqueID,
                            w.RendererAssetUniqueID,
                            w.RendererType,
                            w.MaterialName,
                            w.MainTextureAssetUniqueID,
                            w.MainTextureSlotName,
                            w.BaseColorR,
                            w.BaseColorG,
                            w.BaseColorB,
                            w.BaseColorA,
                            w.ResolutionSource,
                            ROW_NUMBER() OVER (
                                PARTITION BY w.MeshAssetUniqueID, w.SubMeshIndex
                                ORDER BY
                                    CASE WHEN w.MaterialAssetUniqueID <> '' THEN 0 ELSE 1 END,
                                    CASE WHEN w.HasMainTexture <> 0 THEN 0 ELSE 1 END,
                                    CASE WHEN w.HasTexture <> 0 THEN 0 ELSE 1 END,
                                    w.RendererScore DESC,
                                    w.MainTextureCount DESC,
                                    w.TextureCount DESC,
                                    w.MaterialScore DESC,
                                    w.MaterialCount DESC,
                                    w.RelationCount DESC,
                                    w.MaterialSlotIndex,
                                    w.RendererAssetUniqueID,
                                    w.MaterialAssetUniqueID
                            ) AS RowNum
                        FROM WithRendererStats w
                    )
                    INSERT INTO MeshMaterialResolvedCache (
                        ProjectId, MeshAssetUniqueID, SubMeshIndex, MaterialAssetUniqueID,
                        RendererAssetUniqueID, RendererType, MaterialName,
                        MainTextureAssetUniqueID, MainTextureSlotName,
                        BaseColorR, BaseColorG, BaseColorB, BaseColorA, ResolutionSource)
                    SELECT
                        @projectId,
                        r.MeshAssetUniqueID,
                        r.SubMeshIndex,
                        r.MaterialAssetUniqueID,
                        r.RendererAssetUniqueID,
                        r.RendererType,
                        r.MaterialName,
                        r.MainTextureAssetUniqueID,
                        r.MainTextureSlotName,
                        r.BaseColorR,
                        r.BaseColorG,
                        r.BaseColorB,
                        r.BaseColorA,
                        r.ResolutionSource
                    FROM Ranked r
                    WHERE r.RowNum = 1
                    ON CONFLICT(ProjectId, MeshAssetUniqueID, SubMeshIndex)
                    DO UPDATE SET
                        MaterialAssetUniqueID = excluded.MaterialAssetUniqueID,
                        RendererAssetUniqueID = excluded.RendererAssetUniqueID,
                        RendererType = excluded.RendererType,
                        MaterialName = excluded.MaterialName,
                        MainTextureAssetUniqueID = excluded.MainTextureAssetUniqueID,
                        MainTextureSlotName = excluded.MainTextureSlotName,
                        BaseColorR = excluded.BaseColorR,
                        BaseColorG = excluded.BaseColorG,
                        BaseColorB = excluded.BaseColorB,
                        BaseColorA = excluded.BaseColorA,
                        ResolutionSource = excluded.ResolutionSource;";
                meshMatCmd.Parameters.AddWithValue("@projectId", projectId);
                meshMatCmd.ExecuteNonQuery();
            }

            // 3. Backfill CompositeAssets & CompositeAssetParts from ModelGroups & ModelGroupMeshes using PK lookups
            var mgCols = GetTableColumnNames(conn, "ModelGroups", transaction);
            if (mgCols.Contains("GroupAssetUniqueID"))
            {
                using var compCmd = conn.CreateCommand();
                compCmd.Transaction = transaction;
                compCmd.CommandText = @"
                    WITH GroupPartRows AS (
                        SELECT
                            mgm.GroupAssetUniqueID,
                            ROW_NUMBER() OVER (
                                PARTITION BY mgm.GroupAssetUniqueID
                                ORDER BY mgm.SlotIndex ASC, mgm.Id ASC
                            ) - 1 AS PartIdx,
                            mgm.MeshAssetUniqueID,
                            mgm.RendererAssetUniqueID,
                            mgm.RendererType,
                            mgm.GameObjectAssetUniqueID,
                            mgm.GameObjectName,
                            mgm.TransformMatrix,
                            COALESCE(mrc.MaterialAssetUniqueID, '') AS PrimaryMaterialAssetUniqueID,
                            mgm.Confidence
                        FROM ModelGroupMeshes mgm
                        LEFT JOIN MeshMaterialResolvedCache mrc
                          ON mrc.ProjectId = @projectId
                         AND mrc.MeshAssetUniqueID = mgm.MeshAssetUniqueID
                         AND mrc.SubMeshIndex = 0
                        WHERE mgm.ProjectId = @projectId
                          AND mgm.MeshAssetUniqueID <> ''
                    )
                    INSERT INTO CompositeAssetParts (
                        ProjectId, CompositeAssetUniqueID, PartIndex, MeshAssetUniqueID,
                        RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName,
                        TransformMatrix, PrimaryMaterialAssetUniqueID, Confidence)
                    SELECT
                        @projectId,
                        gpr.GroupAssetUniqueID,
                        gpr.PartIdx,
                        gpr.MeshAssetUniqueID,
                        gpr.RendererAssetUniqueID,
                        gpr.RendererType,
                        gpr.GameObjectAssetUniqueID,
                        gpr.GameObjectName,
                        gpr.TransformMatrix,
                        gpr.PrimaryMaterialAssetUniqueID,
                        gpr.Confidence
                    FROM GroupPartRows gpr
                    WHERE true
                    ON CONFLICT(ProjectId, CompositeAssetUniqueID, PartIndex)
                    DO UPDATE SET
                        MeshAssetUniqueID = excluded.MeshAssetUniqueID,
                        RendererAssetUniqueID = excluded.RendererAssetUniqueID,
                        RendererType = excluded.RendererType,
                        GameObjectAssetUniqueID = excluded.GameObjectAssetUniqueID,
                        GameObjectName = excluded.GameObjectName,
                        TransformMatrix = excluded.TransformMatrix,
                        PrimaryMaterialAssetUniqueID = CASE WHEN excluded.PrimaryMaterialAssetUniqueID <> '' THEN excluded.PrimaryMaterialAssetUniqueID ELSE CompositeAssetParts.PrimaryMaterialAssetUniqueID END,
                        Confidence = excluded.Confidence;

                    INSERT INTO CompositeAssets (
                        ProjectId, CompositeAssetUniqueID, CompositeKind, DisplayName,
                        RootGameObjectAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                        SourceFileName, MeshPartCount, DistinctMeshCount, DistinctMaterialCount, Confidence, ResolutionSource)
                    SELECT
                        @projectId,
                        mg.GroupAssetUniqueID,
                        CASE WHEN mg.GroupKind <> '' THEN mg.GroupKind ELSE 'ModelGroup' END,
                        COALESCE(NULLIF(mg.GroupName, ''), NULLIF(mg.RootGameObjectName, ''), mg.GroupAssetUniqueID),
                        mg.RootGameObjectAssetUniqueID,
                        mg.AnimatorAssetUniqueID,
                        mg.AvatarAssetUniqueID,
                        mg.ControllerAssetUniqueID,
                        mg.SourceFileName,
                        COALESCE(gs.MeshPartCount, 0),
                        COALESCE(gs.DistinctMeshCount, 0),
                        COALESCE(gs.DistinctMaterialCount, 0),
                        mg.Confidence,
                        'preparation'
                    FROM ModelGroups mg
                    LEFT JOIN (
                        SELECT
                            cap.CompositeAssetUniqueID,
                            COUNT(*) AS MeshPartCount,
                            COUNT(DISTINCT cap.MeshAssetUniqueID) AS DistinctMeshCount,
                            COUNT(DISTINCT CASE WHEN cap.PrimaryMaterialAssetUniqueID <> '' THEN cap.PrimaryMaterialAssetUniqueID END) AS DistinctMaterialCount
                        FROM CompositeAssetParts cap
                        WHERE cap.ProjectId = @projectId
                        GROUP BY cap.CompositeAssetUniqueID
                    ) gs
                      ON gs.CompositeAssetUniqueID = mg.GroupAssetUniqueID
                    WHERE mg.ProjectId = @projectId
                    ON CONFLICT(ProjectId, CompositeAssetUniqueID)
                    DO UPDATE SET
                        CompositeKind = excluded.CompositeKind,
                        DisplayName = excluded.DisplayName,
                        RootGameObjectAssetUniqueID = excluded.RootGameObjectAssetUniqueID,
                        AnimatorAssetUniqueID = excluded.AnimatorAssetUniqueID,
                        AvatarAssetUniqueID = excluded.AvatarAssetUniqueID,
                        ControllerAssetUniqueID = excluded.ControllerAssetUniqueID,
                        SourceFileName = excluded.SourceFileName,
                        MeshPartCount = excluded.MeshPartCount,
                        DistinctMeshCount = excluded.DistinctMeshCount,
                        DistinctMaterialCount = excluded.DistinctMaterialCount,
                        Confidence = excluded.Confidence;";
                compCmd.Parameters.AddWithValue("@projectId", projectId);
                compCmd.ExecuteNonQuery();
            }
        }

        private static void DropLegacyIndexTables(SqliteConnection conn)
        {
            DropAssetHandlesSchemaObject(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA foreign_keys = OFF;
                DROP TABLE IF EXISTS IndexingReadFiles;
                DROP TABLE IF EXISTS ProjectIndexingState;
                DROP TABLE IF EXISTS Scenes;
                DROP TABLE IF EXISTS SourceFileExternals;
                DROP TABLE IF EXISTS AnimationClipTargets;
                DROP TABLE IF EXISTS CompositeAssetParts;
                DROP TABLE IF EXISTS CompositeAssets;
                DROP TABLE IF EXISTS MeshMaterialResolvedCache;
                DROP TABLE IF EXISTS MaterialCache;
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
                    DROP TABLE IF EXISTS Scenes;
                    DROP TABLE IF EXISTS SourceFileExternals;
                    DROP TABLE IF EXISTS PreviewCacheEntries;
                    DROP TABLE IF EXISTS AnimationClipTargets;
                    DROP TABLE IF EXISTS CompositeAssetParts;
                    DROP TABLE IF EXISTS CompositeAssets;
                    DROP TABLE IF EXISTS MeshMaterialResolvedCache;
                    DROP TABLE IF EXISTS MaterialCache;
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

        private static void CreateSchema(SqliteConnection conn, bool caseSensitivePaths)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                            CREATE TABLE IF NOT EXISTS Projects (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                FolderPath TEXT NOT NULL,
                                FolderPathKey TEXT NOT NULL DEFAULT '',
                                SignatureHash TEXT NOT NULL,
                                SignatureVersion INTEGER NOT NULL DEFAULT 2,
                                TotalFiles INTEGER NOT NULL,
                                TotalBytes INTEGER NOT NULL,
                                UnityBundleCount INTEGER NOT NULL,
                                LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                                UnityVersion TEXT
                            );

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
                                LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP,
                                RelativePath TEXT NOT NULL DEFAULT '',
                                FileSize INTEGER NOT NULL DEFAULT 0,
                                LastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
                                ContentSampleHash INTEGER NOT NULL DEFAULT 0,
                                FileKind INTEGER NOT NULL DEFAULT 0
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
                                ScaleX REAL NOT NULL DEFAULT 1.0,
                                ScaleY REAL NOT NULL DEFAULT 1.0,
                                OffsetX REAL NOT NULL DEFAULT 0.0,
                                OffsetY REAL NOT NULL DEFAULT 0.0,
                                ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                                PRIMARY KEY (ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS MaterialCache (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MaterialAssetUniqueID TEXT NOT NULL,
                                MaterialName TEXT NOT NULL DEFAULT '',
                                ParentMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                                MainTextureSlotName TEXT NOT NULL DEFAULT '',
                                BaseColorR REAL NOT NULL DEFAULT 1.0,
                                BaseColorG REAL NOT NULL DEFAULT 1.0,
                                BaseColorB REAL NOT NULL DEFAULT 1.0,
                                BaseColorA REAL NOT NULL DEFAULT 1.0,
                                ShaderName TEXT NOT NULL DEFAULT '',
                                TextureSlotCount INTEGER NOT NULL DEFAULT 0,
                                ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                                PRIMARY KEY (ProjectId, MaterialAssetUniqueID)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS MeshMaterialResolvedCache (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                MeshAssetUniqueID TEXT NOT NULL,
                                SubMeshIndex INTEGER NOT NULL,
                                MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererType TEXT NOT NULL DEFAULT '',
                                MaterialName TEXT NOT NULL DEFAULT '',
                                MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                                MainTextureSlotName TEXT NOT NULL DEFAULT '',
                                BaseColorR REAL NOT NULL DEFAULT 1.0,
                                BaseColorG REAL NOT NULL DEFAULT 1.0,
                                BaseColorB REAL NOT NULL DEFAULT 1.0,
                                BaseColorA REAL NOT NULL DEFAULT 1.0,
                                ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                                PRIMARY KEY (ProjectId, MeshAssetUniqueID, SubMeshIndex)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS CompositeAssets (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                CompositeAssetUniqueID TEXT NOT NULL,
                                CompositeKind TEXT NOT NULL,
                                DisplayName TEXT NOT NULL DEFAULT '',
                                RootGameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                                AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                                AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                                ControllerAssetUniqueID TEXT NOT NULL DEFAULT '',
                                SourceFileName TEXT NOT NULL DEFAULT '',
                                MeshPartCount INTEGER NOT NULL DEFAULT 0,
                                DistinctMeshCount INTEGER NOT NULL DEFAULT 0,
                                DistinctMaterialCount INTEGER NOT NULL DEFAULT 0,
                                Confidence INTEGER NOT NULL DEFAULT 0,
                                ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                                PRIMARY KEY (ProjectId, CompositeAssetUniqueID)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS CompositeAssetParts (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                CompositeAssetUniqueID TEXT NOT NULL,
                                PartIndex INTEGER NOT NULL,
                                MeshAssetUniqueID TEXT NOT NULL,
                                RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                                RendererType TEXT NOT NULL DEFAULT '',
                                GameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                                GameObjectName TEXT NOT NULL DEFAULT '',
                                TransformMatrix BLOB,
                                PrimaryMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                                Confidence INTEGER NOT NULL DEFAULT 0,
                                PRIMARY KEY (ProjectId, CompositeAssetUniqueID, PartIndex)
                            ) WITHOUT ROWID;

                            CREATE TABLE IF NOT EXISTS AnimationClipTargets (
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                ClipAssetUniqueID TEXT NOT NULL,
                                AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                                AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                                MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                                ModelGroupId TEXT NOT NULL DEFAULT '',
                                RelationKind TEXT NOT NULL DEFAULT '',
                                MatchScore INTEGER NOT NULL DEFAULT 0,
                                MatchedBoneCount INTEGER NOT NULL DEFAULT 0,
                                TotalClipBoneCount INTEGER NOT NULL DEFAULT 0,
                                PRIMARY KEY (ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
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
                            CREATE INDEX IF NOT EXISTS idx_asset_edges_kind_source ON AssetEdges(ProjectId, EdgeKind, SourceAssetUniqueID, SlotName, TargetAssetUniqueID, SlotIndex);
                CREATE INDEX IF NOT EXISTS idx_model_groups_animator ON ModelGroups(ProjectId, AnimatorAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_groups_controller ON ModelGroups(ProjectId, ControllerAssetUniqueID);
                            CREATE UNIQUE INDEX IF NOT EXISTS idx_model_group_meshes_unique ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_group ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, SlotIndex, MeshAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_model_group_meshes_candidate ON ModelGroupMeshes(ProjectId, MeshAssetUniqueID, GroupAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_textures_preview_lookup ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID, SlotName, IsMainTexture DESC, SlotIndex, TextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_material_cache_main_tex ON MaterialCache(ProjectId, MainTextureAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_mesh_mat_resolved_material ON MeshMaterialResolvedCache(ProjectId, MaterialAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_composite_assets_root_go ON CompositeAssets(ProjectId, RootGameObjectAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_composite_assets_avatar ON CompositeAssets(ProjectId, AvatarAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_composite_assets_animator ON CompositeAssets(ProjectId, AnimatorAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_composite_parts_mesh ON CompositeAssetParts(ProjectId, MeshAssetUniqueID, CompositeAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_anim_clip_targets_clip_score ON AnimationClipTargets(ProjectId, ClipAssetUniqueID, MatchScore DESC, MatchedBoneCount DESC);
                            CREATE INDEX IF NOT EXISTS idx_anim_clip_targets_mesh ON AnimationClipTargets(ProjectId, MeshAssetUniqueID, MatchScore DESC, ClipAssetUniqueID);
                            CREATE INDEX IF NOT EXISTS idx_anim_clip_targets_avatar ON AnimationClipTargets(ProjectId, AvatarAssetUniqueID, MatchScore DESC, ClipAssetUniqueID);
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

            EnsureMaterialTexturesV15Columns(conn, transaction: null);
            EnsureV16ProjectsColumnsAndIndex(conn, transaction: null, caseSensitivePaths);
            EnsureV16SourceFilesColumns(conn, transaction: null);
            EnsureV16SceneTablesAndIndexes(conn, transaction: null);
            EnsurePreviewCacheSchemaUpgrades(conn);
        }

        /// <summary>
        /// O(1) transactional table reset for single-project index databases.
        /// Dropping and recreating WITHOUT ROWID index tables inside the active ACID transaction
        /// reclaims gigabytes of B-tree pages via SQLite's freelist in milliseconds instead of walking
        /// millions of rows with DELETE ... WHERE and writing gigabytes to the WAL, while preserving
        /// Projects, PreviewCacheEntries, ProjectIndexingState, IndexingReadFiles, and CacheSchema.
        /// </summary>
        internal static void RecreateIndexTablesInTransaction(
            SqliteConnection conn,
            SqliteTransaction transaction,
            bool includeSemanticTables)
        {
            using var dropCmd = conn.CreateCommand();
            dropCmd.Transaction = transaction;
            dropCmd.CommandText = includeSemanticTables
                ? @"
                    DROP VIEW IF EXISTS AssetHandles;
                    DROP TABLE IF EXISTS Scenes;
                    DROP TABLE IF EXISTS SourceFileExternals;
                    DROP TABLE IF EXISTS AnimationClipTargets;
                    DROP TABLE IF EXISTS CompositeAssetParts;
                    DROP TABLE IF EXISTS CompositeAssets;
                    DROP TABLE IF EXISTS MeshMaterialResolvedCache;
                    DROP TABLE IF EXISTS MaterialCache;
                    DROP TABLE IF EXISTS MaterialTextures;
                    DROP TABLE IF EXISTS MeshMaterials;
                    DROP TABLE IF EXISTS MeshRenderers;
                    DROP TABLE IF EXISTS ModelGroupMeshes;
                    DROP TABLE IF EXISTS ModelGroups;
                    DROP TABLE IF EXISTS AssetEdges;
                    DROP TABLE IF EXISTS Assets;
                    DROP TABLE IF EXISTS SourceFiles;"
                : @"
                    DROP VIEW IF EXISTS AssetHandles;
                    DROP TABLE IF EXISTS Scenes;
                    DROP TABLE IF EXISTS SourceFileExternals;
                    DROP TABLE IF EXISTS Assets;
                    DROP TABLE IF EXISTS SourceFiles;";
            dropCmd.ExecuteNonQuery();

            using var createCoreCmd = conn.CreateCommand();
            createCoreCmd.Transaction = transaction;
            createCoreCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SourceFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    SerializedFileName TEXT NOT NULL,
                    OriginalPath TEXT NOT NULL DEFAULT '',
                    UnityVersion TEXT NOT NULL DEFAULT '',
                    ObjectCount INTEGER NOT NULL DEFAULT 0,
                    LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP,
                    RelativePath TEXT NOT NULL DEFAULT '',
                    FileSize INTEGER NOT NULL DEFAULT 0,
                    LastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
                    ContentSampleHash INTEGER NOT NULL DEFAULT 0,
                    FileKind INTEGER NOT NULL DEFAULT 0
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

                CREATE UNIQUE INDEX IF NOT EXISTS idx_source_files_unique ON SourceFiles(ProjectId, SerializedFileName, OriginalPath);";
            createCoreCmd.ExecuteNonQuery();

            if (includeSemanticTables)
            {
                RecreateSemanticTablesInTransaction(conn, transaction, dropExistingFirst: false, createSecondaryIndexes: true);
            }

            using (var viewCmd = conn.CreateCommand())
            {
                viewCmd.Transaction = transaction;
                viewCmd.CommandText = AssetHandlesCompatibilityViewSql;
                viewCmd.ExecuteNonQuery();
            }

            EnsureV16SceneTablesAndIndexes(conn, transaction);
        }

        /// <summary>
        /// O(1) transactional reset of semantic relation tables for single-project databases, avoiding
        /// row-by-row DELETE walks across millions of WITHOUT ROWID edges and materials.
        /// </summary>
        internal static void RecreateSemanticTablesInTransaction(
            SqliteConnection conn,
            SqliteTransaction transaction,
            bool dropExistingFirst = true,
            bool createSecondaryIndexes = true)
        {
            if (dropExistingFirst)
            {
                using var dropSemCmd = conn.CreateCommand();
                dropSemCmd.Transaction = transaction;
                dropSemCmd.CommandText = @"
                    DROP TABLE IF EXISTS AnimationClipTargets;
                    DROP TABLE IF EXISTS CompositeAssetParts;
                    DROP TABLE IF EXISTS CompositeAssets;
                    DROP TABLE IF EXISTS MeshMaterialResolvedCache;
                    DROP TABLE IF EXISTS MaterialCache;
                    DROP TABLE IF EXISTS MaterialTextures;
                    DROP TABLE IF EXISTS MeshMaterials;
                    DROP TABLE IF EXISTS MeshRenderers;
                    DROP TABLE IF EXISTS ModelGroupMeshes;
                    DROP TABLE IF EXISTS ModelGroups;
                    DROP TABLE IF EXISTS AssetEdges;";
                dropSemCmd.ExecuteNonQuery();
            }

            using var createSemCmd = conn.CreateCommand();
            createSemCmd.Transaction = transaction;
            createSemCmd.CommandText = @"
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
                    ScaleX REAL NOT NULL DEFAULT 1.0,
                    ScaleY REAL NOT NULL DEFAULT 1.0,
                    OffsetX REAL NOT NULL DEFAULT 0.0,
                    OffsetY REAL NOT NULL DEFAULT 0.0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS MaterialCache (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MaterialAssetUniqueID TEXT NOT NULL,
                    MaterialName TEXT NOT NULL DEFAULT '',
                    ParentMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureSlotName TEXT NOT NULL DEFAULT '',
                    BaseColorR REAL NOT NULL DEFAULT 1.0,
                    BaseColorG REAL NOT NULL DEFAULT 1.0,
                    BaseColorB REAL NOT NULL DEFAULT 1.0,
                    BaseColorA REAL NOT NULL DEFAULT 1.0,
                    ShaderName TEXT NOT NULL DEFAULT '',
                    TextureSlotCount INTEGER NOT NULL DEFAULT 0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, MaterialAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS MeshMaterialResolvedCache (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MeshAssetUniqueID TEXT NOT NULL,
                    SubMeshIndex INTEGER NOT NULL,
                    MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    MaterialName TEXT NOT NULL DEFAULT '',
                    MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureSlotName TEXT NOT NULL DEFAULT '',
                    BaseColorR REAL NOT NULL DEFAULT 1.0,
                    BaseColorG REAL NOT NULL DEFAULT 1.0,
                    BaseColorB REAL NOT NULL DEFAULT 1.0,
                    BaseColorA REAL NOT NULL DEFAULT 1.0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, MeshAssetUniqueID, SubMeshIndex)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS CompositeAssets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    CompositeAssetUniqueID TEXT NOT NULL,
                    CompositeKind TEXT NOT NULL,
                    DisplayName TEXT NOT NULL DEFAULT '',
                    RootGameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ControllerAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SourceFileName TEXT NOT NULL DEFAULT '',
                    MeshPartCount INTEGER NOT NULL DEFAULT 0,
                    DistinctMeshCount INTEGER NOT NULL DEFAULT 0,
                    DistinctMaterialCount INTEGER NOT NULL DEFAULT 0,
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation',
                    PRIMARY KEY (ProjectId, CompositeAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS CompositeAssetParts (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    CompositeAssetUniqueID TEXT NOT NULL,
                    PartIndex INTEGER NOT NULL,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    GameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    GameObjectName TEXT NOT NULL DEFAULT '',
                    TransformMatrix BLOB,
                    PrimaryMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, CompositeAssetUniqueID, PartIndex)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS AnimationClipTargets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    ClipAssetUniqueID TEXT NOT NULL,
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ModelGroupId TEXT NOT NULL DEFAULT '',
                    RelationKind TEXT NOT NULL DEFAULT '',
                    MatchScore INTEGER NOT NULL DEFAULT 0,
                    MatchedBoneCount INTEGER NOT NULL DEFAULT 0,
                    TotalClipBoneCount INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                ) WITHOUT ROWID;

                CREATE UNIQUE INDEX IF NOT EXISTS idx_model_groups_unique ON ModelGroups(ProjectId, GroupAssetUniqueID);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_model_group_meshes_unique ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID);";
            createSemCmd.ExecuteNonQuery();

            if (createSecondaryIndexes)
            {
                CreateSemanticBulkSecondaryIndexes(conn, transaction);
            }
        }

        private static void DropSemanticBulkSecondaryIndexes(SqliteConnection conn, SqliteTransaction transaction)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                DROP INDEX IF EXISTS idx_asset_edges_target_kind;
                DROP INDEX IF EXISTS idx_asset_edges_kind_source;
                DROP INDEX IF EXISTS idx_model_groups_avatar;
                DROP INDEX IF EXISTS idx_model_groups_animator;
                DROP INDEX IF EXISTS idx_model_groups_controller;
                DROP INDEX IF EXISTS idx_model_group_meshes_group;
                DROP INDEX IF EXISTS idx_model_group_meshes_candidate;
                DROP INDEX IF EXISTS idx_material_textures_preview_lookup;
                DROP INDEX IF EXISTS idx_material_cache_main_tex;
                DROP INDEX IF EXISTS idx_mesh_mat_resolved_material;
                DROP INDEX IF EXISTS idx_composite_assets_root_go;
                DROP INDEX IF EXISTS idx_composite_assets_avatar;
                DROP INDEX IF EXISTS idx_composite_assets_animator;
                DROP INDEX IF EXISTS idx_composite_parts_mesh;
                DROP INDEX IF EXISTS idx_anim_clip_targets_clip_score;
                DROP INDEX IF EXISTS idx_anim_clip_targets_mesh;
                DROP INDEX IF EXISTS idx_anim_clip_targets_avatar;";
            cmd.ExecuteNonQuery();
        }

        private static void CreateSemanticPreAnimationIndexes(SqliteConnection conn, SqliteTransaction transaction)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_asset_edges_target_kind ON AssetEdges(ProjectId, TargetAssetUniqueID, EdgeKind, SlotName, SourceAssetUniqueID, SlotIndex);
                CREATE INDEX IF NOT EXISTS idx_model_groups_avatar ON ModelGroups(ProjectId, AvatarAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_asset_edges_kind_source ON AssetEdges(ProjectId, EdgeKind, SourceAssetUniqueID, SlotName, TargetAssetUniqueID, SlotIndex);
                CREATE INDEX IF NOT EXISTS idx_model_groups_animator ON ModelGroups(ProjectId, AnimatorAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_groups_controller ON ModelGroups(ProjectId, ControllerAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_group_meshes_group ON ModelGroupMeshes(ProjectId, GroupAssetUniqueID, SlotIndex, MeshAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_model_group_meshes_candidate ON ModelGroupMeshes(ProjectId, MeshAssetUniqueID, GroupAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_material_textures_preview_lookup ON MaterialTextures(ProjectId, PreviewMaterialAssetUniqueID, SlotName, IsMainTexture DESC, SlotIndex, TextureAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_material_cache_main_tex ON MaterialCache(ProjectId, MainTextureAssetUniqueID);";
            cmd.ExecuteNonQuery();
        }

        private static void CreateSemanticBulkSecondaryIndexes(SqliteConnection conn, SqliteTransaction transaction)
        {
            CreateSemanticPreAnimationIndexes(conn, transaction);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_mesh_mat_resolved_material ON MeshMaterialResolvedCache(ProjectId, MaterialAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_assets_root_go ON CompositeAssets(ProjectId, RootGameObjectAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_assets_avatar ON CompositeAssets(ProjectId, AvatarAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_assets_animator ON CompositeAssets(ProjectId, AnimatorAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_composite_parts_mesh ON CompositeAssetParts(ProjectId, MeshAssetUniqueID, CompositeAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_anim_clip_targets_clip_score ON AnimationClipTargets(ProjectId, ClipAssetUniqueID, MatchScore DESC, MatchedBoneCount DESC);
                CREATE INDEX IF NOT EXISTS idx_anim_clip_targets_mesh ON AnimationClipTargets(ProjectId, MeshAssetUniqueID, MatchScore DESC, ClipAssetUniqueID);
                CREATE INDEX IF NOT EXISTS idx_anim_clip_targets_avatar ON AnimationClipTargets(ProjectId, AvatarAssetUniqueID, MatchScore DESC, ClipAssetUniqueID);";
            cmd.ExecuteNonQuery();
        }

        internal static readonly string[] SemanticStagingTableNames =
        {
            "Stage_SourceFiles",
            "Stage_AssetEdges",
            "Stage_ModelGroups",
            "Stage_ModelGroupMeshes",
            "Stage_MeshRenderers",
            "Stage_MeshMaterials",
            "Stage_MaterialTextures",
            "Stage_MaterialCache",
            "Stage_CompositeAssets",
            "Stage_CompositeAssetParts",
            "Stage_AnimationClipTargets",
            "Stage_RigBoneHashes",
            "Stage_ClipBoneHashes"
        };

        internal static void DropSemanticStagingTables(SqliteConnection conn, SqliteTransaction? transaction = null)
        {
            var stageTables = new List<string>(SemanticStagingTableNames);
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.Transaction = transaction;
                listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Stage_%';";
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        var tableName = reader.GetString(0);
                        if (!string.IsNullOrWhiteSpace(tableName))
                        {
                            stageTables.Add(tableName);
                        }
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tableName in stageTables)
            {
                if (!seen.Add(tableName))
                {
                    continue;
                }

                var escapedName = tableName.Replace("\"", "\"\"");
                using var dropCmd = conn.CreateCommand();
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = $"DROP TABLE IF EXISTS \"{escapedName}\";";
                dropCmd.ExecuteNonQuery();
            }
        }

        internal static void CreateSemanticStagingTables(SqliteConnection conn, SqliteTransaction? transaction = null)
        {
            DropSemanticStagingTables(conn, transaction);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE TABLE Stage_SourceFiles (
                    ProjectId INTEGER NOT NULL,
                    SerializedFileName TEXT NOT NULL,
                    OriginalPath TEXT NOT NULL DEFAULT '',
                    UnityVersion TEXT NOT NULL DEFAULT '',
                    ObjectCount INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE Stage_AssetEdges (
                    ProjectId INTEGER NOT NULL,
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

                CREATE TABLE Stage_ModelGroups (
                    ProjectId INTEGER NOT NULL,
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

                CREATE TABLE Stage_ModelGroupMeshes (
                    ProjectId INTEGER NOT NULL,
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

                CREATE TABLE Stage_MeshRenderers (
                    ProjectId INTEGER NOT NULL,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL,
                    RendererType TEXT NOT NULL,
                    GameObjectAssetUniqueID TEXT,
                    GameObjectName TEXT,
                    Description TEXT
                );

                CREATE TABLE Stage_MeshMaterials (
                    ProjectId INTEGER NOT NULL,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SubMeshIndex INTEGER NOT NULL,
                    MaterialSlotIndex INTEGER NOT NULL DEFAULT -1,
                    MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    MaterialScore INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE Stage_MaterialTextures (
                    ProjectId INTEGER NOT NULL,
                    MaterialAssetUniqueID TEXT NOT NULL,
                    SlotName TEXT NOT NULL,
                    SlotIndex INTEGER NOT NULL,
                    TextureFileId INTEGER NOT NULL DEFAULT 0,
                    TexturePathID INTEGER NOT NULL DEFAULT 0,
                    TextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    PreviewMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    IsResolved INTEGER NOT NULL DEFAULT 0,
                    IsMainTexture INTEGER NOT NULL DEFAULT 0,
                    ScaleX REAL NOT NULL DEFAULT 1.0,
                    ScaleY REAL NOT NULL DEFAULT 1.0,
                    OffsetX REAL NOT NULL DEFAULT 0.0,
                    OffsetY REAL NOT NULL DEFAULT 0.0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation'
                );

                CREATE TABLE Stage_MaterialCache (
                    ProjectId INTEGER NOT NULL,
                    MaterialAssetUniqueID TEXT NOT NULL,
                    MaterialName TEXT NOT NULL DEFAULT '',
                    ParentMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MainTextureSlotName TEXT NOT NULL DEFAULT '',
                    BaseColorR REAL NOT NULL DEFAULT 1.0,
                    BaseColorG REAL NOT NULL DEFAULT 1.0,
                    BaseColorB REAL NOT NULL DEFAULT 1.0,
                    BaseColorA REAL NOT NULL DEFAULT 1.0,
                    ShaderName TEXT NOT NULL DEFAULT '',
                    TextureSlotCount INTEGER NOT NULL DEFAULT 0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation'
                );

                CREATE TABLE Stage_CompositeAssets (
                    ProjectId INTEGER NOT NULL,
                    CompositeAssetUniqueID TEXT NOT NULL,
                    CompositeKind TEXT NOT NULL,
                    DisplayName TEXT NOT NULL DEFAULT '',
                    RootGameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ControllerAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SourceFileName TEXT NOT NULL DEFAULT '',
                    MeshPartCount INTEGER NOT NULL DEFAULT 0,
                    DistinctMeshCount INTEGER NOT NULL DEFAULT 0,
                    DistinctMaterialCount INTEGER NOT NULL DEFAULT 0,
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    ResolutionSource TEXT NOT NULL DEFAULT 'preparation'
                );

                CREATE TABLE Stage_CompositeAssetParts (
                    ProjectId INTEGER NOT NULL,
                    CompositeAssetUniqueID TEXT NOT NULL,
                    PartIndex INTEGER NOT NULL,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    GameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    GameObjectName TEXT NOT NULL DEFAULT '',
                    TransformMatrix BLOB,
                    PrimaryMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    Confidence INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE Stage_AnimationClipTargets (
                    ProjectId INTEGER NOT NULL,
                    ClipAssetUniqueID TEXT NOT NULL,
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ModelGroupId TEXT NOT NULL DEFAULT '',
                    RelationKind TEXT NOT NULL DEFAULT '',
                    MatchScore INTEGER NOT NULL DEFAULT 0,
                    MatchedBoneCount INTEGER NOT NULL DEFAULT 0,
                    TotalClipBoneCount INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE Stage_RigBoneHashes (
                    ProjectId INTEGER NOT NULL,
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ModelGroupId TEXT NOT NULL DEFAULT '',
                    BoneHash INTEGER NOT NULL
                );

                CREATE TABLE Stage_ClipBoneHashes (
                    ProjectId INTEGER NOT NULL,
                    ClipAssetUniqueID TEXT NOT NULL,
                    BoneHash INTEGER NOT NULL,
                    TotalClipBoneCount INTEGER NOT NULL DEFAULT 0
                );";
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
