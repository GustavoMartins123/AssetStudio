using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class SQLitePerformanceTuningTests
    {
        private static readonly string[] ClusteredWithoutRowIdTables =
        {
            "Assets",
            "AssetEdges",
            "MeshRenderers",
            "MeshMaterials",
            "MaterialTextures"
        };

        private static readonly string[] RemovedRedundantIndexes =
        {
            "idx_projects_path",
            "idx_handles_project",
            "idx_handles_lookup",
            "idx_source_files_project",
            "idx_assets_project",
            "idx_assets_unique",
            "idx_assets_type_source",
            "idx_asset_edges_unique",
            "idx_asset_edges_source",
            "idx_asset_edges_target",
            "idx_asset_edges_source_kind",
            "idx_model_groups_animator",
            "idx_model_group_meshes_mesh",
            "idx_mesh_renderers_unique",
            "idx_mesh_renderers_mesh",
            "idx_mesh_renderers_lookup",
            "idx_mesh_materials_unique",
            "idx_mesh_materials_mesh",
            "idx_mesh_materials_mesh_submesh",
            "idx_mesh_materials_renderer",
            "idx_mesh_materials_rank",
            "idx_material_textures_unique",
            "idx_material_textures_material",
            "idx_material_textures_preview_material",
            "idx_material_textures_material_lookup",
            "idx_material_textures_rank"
        };

        private static readonly string[] ExpectedActiveIndexes =
        {
            "idx_projects_lookup",
            "idx_source_files_unique",
            "idx_asset_edges_target_kind",
            "idx_model_groups_unique",
            "idx_model_groups_avatar",
            "idx_model_group_meshes_unique",
            "idx_model_group_meshes_group",
            "idx_model_group_meshes_candidate",
            "idx_material_textures_preview_lookup",
            "idx_preview_cache_unique",
            "IX_PreviewCacheEntries_LastAccessed",
            "idx_indexing_read_files_project",
            "idx_indexing_read_files_unique"
        };

        [Fact]
        public void FreshDatabase_InitializesTier1PragmasOnWriteAndReadConnections()
        {
            var tempRoot = CreateTempDirectory("sqlite_pragmas");
            try
            {
                var (cache, projectDir, _, _, _) = CreateSampleProject(tempRoot, assetGroupCount: 2);
                cache.EnsureInitialized(projectDir);

                using (var writeConn = cache.CreateConnection(projectDir))
                {
                    Assert.Equal(8192L, ReadPragmaInt64(writeConn, "page_size"));
                    Assert.Equal("wal", ReadPragmaString(writeConn, "journal_mode"), ignoreCase: true);
                    Assert.Equal(1L, ReadPragmaInt64(writeConn, "synchronous"));
                    Assert.Equal(1L, ReadPragmaInt64(writeConn, "foreign_keys"));
                    Assert.Equal(60000L, ReadPragmaInt64(writeConn, "busy_timeout"));
                    Assert.Equal(2L, ReadPragmaInt64(writeConn, "temp_store"));
                    Assert.Equal(-65536L, ReadPragmaInt64(writeConn, "cache_size"));
                    Assert.Equal(268435456L, ReadPragmaInt64(writeConn, "mmap_size"));
                }

                using (var readConn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Equal(1L, ReadPragmaInt64(readConn, "query_only"));
                    Assert.Equal(5000L, ReadPragmaInt64(readConn, "busy_timeout"));
                    Assert.Equal(2L, ReadPragmaInt64(readConn, "temp_store"));
                    Assert.Equal(-65536L, ReadPragmaInt64(readConn, "cache_size"));
                    Assert.Equal(268435456L, ReadPragmaInt64(readConn, "mmap_size"));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SchemaV12_UsesWithoutRowIdOnClusteredTablesAndPrunesRedundantIndexes()
        {
            var tempRoot = CreateTempDirectory("sqlite_schema_v12");
            try
            {
                var (cache, projectDir, _, _, _) = CreateSampleProject(tempRoot, assetGroupCount: 2);
                cache.EnsureInitialized(projectDir);

                using var conn = cache.CreateReadConnection(projectDir);
                using (var versionCmd = conn.CreateCommand())
                {
                    versionCmd.CommandText = "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;";
                    Assert.Equal("14", versionCmd.ExecuteScalar()?.ToString());
                }

                var tableDdl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var tableCmd = conn.CreateCommand())
                {
                    tableCmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND sql IS NOT NULL;";
                    using var reader = tableCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        tableDdl[reader.GetString(0)] = reader.GetString(1);
                    }
                }

                foreach (var table in ClusteredWithoutRowIdTables)
                {
                    Assert.True(tableDdl.TryGetValue(table, out var ddl), $"Expected table '{table}' to exist.");
                    Assert.Contains("WITHOUT ROWID", ddl!, StringComparison.OrdinalIgnoreCase);
                }

                var indexes = GetUserIndexNames(conn);
                foreach (var removed in RemovedRedundantIndexes)
                {
                    Assert.DoesNotContain(removed, indexes);
                }

                foreach (var expected in ExpectedActiveIndexes)
                {
                    Assert.Contains(expected, indexes);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AnalyzeAndExplainQueryPlan_CoreSemanticQueriesUseSearchWithZeroPhysicalTableScans()
        {
            var tempRoot = CreateTempDirectory("sqlite_eqp");
            try
            {
                const int groupCount = 80;
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: groupCount);
                var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount);

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));

                // Verify synchronous was restored to NORMAL after both bulk rebuilds.
                using (var writeConn = cache.CreateConnection(projectDir))
                {
                    Assert.Equal(1L, ReadPragmaInt64(writeConn, "synchronous"));
                    Assert.Equal(8192L, ReadPragmaInt64(writeConn, "page_size"));
                }

                // Functional parity sanity checks on actual cache read APIs.
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Equal(new[] { sampleIds.TextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, sampleIds.MaterialId));
                Assert.Equal(new[] { sampleIds.TextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, sampleIds.PreviewMaterialId));
                Assert.Equal(new[] { sampleIds.RendererId }, cache.LoadMeshRendererAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Equal(new[] { sampleIds.MeshId }, cache.LoadAvatarMeshAssetIds(projectDir, signature, sampleIds.AvatarId));
                Assert.Equal(new[] { sampleIds.AvatarId }, cache.LoadMeshAvatarAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Equal(new[] { sampleIds.AvatarId }, cache.LoadAnimationClipAvatarAssetIds(projectDir, signature, sampleIds.ClipId));
                Assert.Equal(new[] { sampleIds.MeshId }, cache.LoadAnimationClipMeshAssetIds(projectDir, signature, sampleIds.ClipId));
                Assert.Single(cache.LoadModelGroupsForAvatarAssetId(projectDir, signature, sampleIds.AvatarId));
                Assert.Single(cache.LoadModelGroupCandidatesForMeshAssetId(projectDir, signature, sampleIds.MeshId));

                using var conn = cache.CreateReadConnection(projectDir);

                // Verify ANALYZE populated sqlite_stat1.
                using (var statTableCmd = conn.CreateCommand())
                {
                    statTableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1';";
                    Assert.Equal(1L, Convert.ToInt64(statTableCmd.ExecuteScalar()));
                }
                using (var statRowsCmd = conn.CreateCommand())
                {
                    statRowsCmd.CommandText = "SELECT COUNT(*) FROM sqlite_stat1;";
                    Assert.True(Convert.ToInt64(statRowsCmd.ExecuteScalar()) > 0, "Expected ANALYZE to populate sqlite_stat1.");
                }

                Assert.True(cache.TryGetCachedProjectId(projectDir, signature, out var projectId));

                // 1. LoadMeshMaterialAssetIds
                var meshMaterialPlan = AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadMeshMaterialAssetIds",
                    @"
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
                        ORDER BY SubMeshIndex",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                    },
                    allowedCteScanNames: new[] { "MeshSource", "candidate", "RankedMaterials", "rs", "materialStats", "CandidateMaterials", "MaterialStats", "RendererStats" });
                Assert.Contains(meshMaterialPlan, line => line.Contains("SEARCH MeshMaterials USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(meshMaterialPlan, line => line.Contains("SEARCH texture USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(meshMaterialPlan, line => line.Contains("SEARCH Assets USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));

                // 2. LoadMaterialTextureAssetIds (without slotName and with slotName)
                const string materialTextureSql = @"
                    WITH TextureCandidates AS (
                        SELECT TextureAssetUniqueID, SlotName, SlotIndex, IsMainTexture, 0 AS SourceRank
                        FROM MaterialTextures
                        WHERE ProjectId = @projectId
                          AND MaterialAssetUniqueID = @materialAssetId
                          AND TextureAssetUniqueID <> ''
                        UNION ALL
                        SELECT TextureAssetUniqueID, SlotName, SlotIndex, IsMainTexture, 1 AS SourceRank
                        FROM MaterialTextures
                        WHERE ProjectId = @projectId
                          AND PreviewMaterialAssetUniqueID = @materialAssetId
                          AND PreviewMaterialAssetUniqueID <> MaterialAssetUniqueID
                          AND TextureAssetUniqueID <> ''
                    )
                    SELECT TextureAssetUniqueID
                    FROM TextureCandidates
                    GROUP BY TextureAssetUniqueID
                    ORDER BY MAX(IsMainTexture) DESC, MIN(SourceRank), MIN(SlotIndex), TextureAssetUniqueID";
                var materialTexturePlan = AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadMaterialTextureAssetIds",
                    materialTextureSql,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@materialAssetId", sampleIds.MaterialId);
                    },
                    allowedCteScanNames: new[] { "TextureCandidates" });
                Assert.Contains(materialTexturePlan, line => line.Contains("SEARCH MaterialTextures USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(materialTexturePlan, line => line.Contains("SEARCH MaterialTextures USING COVERING INDEX idx_material_textures_preview_lookup", StringComparison.OrdinalIgnoreCase));

                // 3. LoadMeshRendererAssetIds
                var meshRendererPlan = AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadMeshRendererAssetIds",
                    @"
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
                            mr.RendererAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                        cmd.Parameters.AddWithValue("@rendererType", string.Empty);
                    });
                Assert.Contains(meshRendererPlan, line => line.Contains("SEARCH mr USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));

                // 4. LoadAvatarMeshAssetIds
                var avatarMeshPlan = AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadAvatarMeshAssetIds",
                    @"
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
                        ORDER BY MIN(ae.SlotIndex), MIN(target.Name), ae.TargetAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@avatarAssetId", sampleIds.AvatarId);
                        cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                    });
                Assert.Contains(avatarMeshPlan, line => line.Contains("SEARCH ae USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));

                // 5. LoadMeshAvatarAssetIds
                var meshAvatarPlan = AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadMeshAvatarAssetIds",
                    @"
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
                        ORDER BY MIN(ae.SlotIndex), MIN(source.Name), ae.SourceAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                        cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                    });
                Assert.Contains(meshAvatarPlan, line => line.Contains("SEARCH ae USING COVERING INDEX idx_asset_edges_target_kind", StringComparison.OrdinalIgnoreCase));

                // 6. LoadAnimationClipAvatarAssetIds & LoadAnimationClipMeshAssetIds
                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadAnimationClipAvatarAssetIds",
                    @"
                        WITH AvatarCandidates AS (
                            SELECT ae.TargetAssetUniqueID AS AvatarAssetId, ae.SlotIndex AS SortIndex
                            FROM AssetEdges ae
                            WHERE ae.ProjectId = @projectId
                              AND ae.SourceAssetUniqueID = @clipAssetId
                              AND ae.EdgeKind = 'Avatar'
                              AND ae.SlotName IN ('AnimatorAvatar', 'CompatibleAvatar')
                              AND ae.TargetAssetUniqueID <> ''
                            UNION ALL
                            SELECT avatarEdge.TargetAssetUniqueID AS AvatarAssetId, avatarEdge.SlotIndex AS SortIndex
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
                        ORDER BY MIN(candidates.SortIndex), MIN(target.Name), candidates.AvatarAssetId",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@clipAssetId", sampleIds.ClipId);
                        cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                    },
                    allowedCteScanNames: new[] { "candidates", "AvatarCandidates" });

                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadAnimationClipMeshAssetIds",
                    @"
                        WITH MeshCandidates AS (
                            SELECT meshEdge.TargetAssetUniqueID AS MeshAssetId, meshEdge.SlotIndex AS SortIndex
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
                            SELECT meshEdge.TargetAssetUniqueID AS MeshAssetId, meshEdge.SlotIndex AS SortIndex
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
                        ORDER BY MIN(candidates.SortIndex), MIN(target.Name), candidates.MeshAssetId",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@clipAssetId", sampleIds.ClipId);
                        cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                    },
                    allowedCteScanNames: new[] { "candidates", "MeshCandidates" });

                // 7. LoadModelGroupsForAvatarAssetId, LoadModelGroupsForMeshAssetId, LoadModelGroupMeshes & LoadIndexCache
                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadModelGroupsForAvatarAssetId",
                    @"
                        SELECT GroupAssetUniqueID, GroupName
                        FROM ModelGroups
                        WHERE ProjectId = @projectId
                          AND AvatarAssetUniqueID = @avatarAssetId
                        ORDER BY Confidence DESC, GroupName, GroupAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@avatarAssetId", sampleIds.AvatarId);
                    });

                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadModelGroupsForMeshAssetId",
                    @"
                        SELECT modelGroup.GroupAssetUniqueID, COUNT(groupMesh.MeshAssetUniqueID) AS PartCount, MIN(groupMesh.SlotIndex) AS FirstSlot
                        FROM ModelGroups modelGroup
                        INNER JOIN ModelGroupMeshes selectedMesh
                          ON selectedMesh.ProjectId = modelGroup.ProjectId
                         AND selectedMesh.GroupAssetUniqueID = modelGroup.GroupAssetUniqueID
                         AND selectedMesh.MeshAssetUniqueID = @meshAssetId
                        INNER JOIN ModelGroupMeshes groupMesh
                          ON groupMesh.ProjectId = modelGroup.ProjectId
                         AND groupMesh.GroupAssetUniqueID = modelGroup.GroupAssetUniqueID
                        WHERE modelGroup.ProjectId = @projectId
                        GROUP BY modelGroup.GroupAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                    });

                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadModelGroupMeshes",
                    @"
                        SELECT mgm.GroupAssetUniqueID, mgm.MeshAssetUniqueID, COALESCE(mesh.Name, '')
                        FROM ModelGroupMeshes mgm
                        LEFT JOIN Assets mesh
                          ON mesh.ProjectId = mgm.ProjectId
                         AND mesh.UniqueID = mgm.MeshAssetUniqueID
                        WHERE mgm.ProjectId = @projectId
                          AND mgm.GroupAssetUniqueID = @groupAssetId
                        ORDER BY mgm.SlotIndex, mgm.GameObjectName, mesh.Name, mgm.MeshAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@groupAssetId", sampleIds.GroupId);
                    });

                AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "LoadModelGroupCandidatesForMeshAssetId",
                    @"
                        WITH CandidateGroups AS (
                            SELECT
                                modelGroup.GroupAssetUniqueID,
                                modelGroup.GroupName,
                                modelGroup.Confidence,
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
                            GROUP BY modelGroup.GroupAssetUniqueID, modelGroup.GroupName, modelGroup.Confidence
                        )
                        SELECT candidate.GroupAssetUniqueID, groupMesh.MeshAssetUniqueID, COALESCE(mesh.Name, '')
                        FROM CandidateGroups candidate
                        INNER JOIN ModelGroupMeshes groupMesh
                          ON groupMesh.ProjectId = @projectId
                         AND groupMesh.GroupAssetUniqueID = candidate.GroupAssetUniqueID
                        LEFT JOIN Assets mesh
                          ON mesh.ProjectId = groupMesh.ProjectId
                         AND mesh.UniqueID = groupMesh.MeshAssetUniqueID",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@meshAssetId", sampleIds.MeshId);
                    },
                    allowedCteScanNames: new[] { "candidate", "CandidateGroups" });

                var handleLookupPlan = AssertQueryPlanUsesSearchWithoutPhysicalScans(
                    conn,
                    "AssetHandlesLookupByUniqueId",
                    @"
                        SELECT UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize
                        FROM AssetHandles
                        WHERE ProjectId = @projectId
                          AND UniqueID = @uniqueId",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@projectId", projectId);
                        cmd.Parameters.AddWithValue("@uniqueId", sampleIds.MeshId);
                    });
                Assert.Contains(handleLookupPlan, line => line.Contains("SEARCH Assets USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void LegacySchemaV11Database_AutomaticallyUpgradesToV12WithoutRowIdCleanly()
        {
            var tempRoot = CreateTempDirectory("sqlite_v11_upgrade");
            try
            {
                var (bootstrapCache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 2);
                var dbPath = bootstrapCache.GetDbPath(projectDir);

                // Manually create a full legacy v11 database with rowid tables and redundant prefix indexes.
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE CacheSchema (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                        INSERT INTO CacheSchema (Key, Value) VALUES ('SemanticSchemaVersion', '11');

                        CREATE TABLE Projects (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            FolderPath TEXT NOT NULL,
                            SignatureHash TEXT NOT NULL,
                            TotalFiles INTEGER NOT NULL,
                            TotalBytes INTEGER NOT NULL,
                            UnityBundleCount INTEGER NOT NULL,
                            LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                            UnityVersion TEXT
                        );
                        CREATE INDEX idx_projects_path ON Projects(FolderPath);
                        INSERT INTO Projects (FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                        VALUES (@folder, @sig, 1, 16, 0, '2022.3.10f1');

                        CREATE TABLE AssetHandles (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL,
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
                        CREATE INDEX idx_handles_project ON AssetHandles(ProjectId);

                        CREATE TABLE SourceFiles (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL,
                            SerializedFileName TEXT NOT NULL,
                            OriginalPath TEXT NOT NULL DEFAULT '',
                            UnityVersion TEXT NOT NULL DEFAULT '',
                            ObjectCount INTEGER NOT NULL DEFAULT 0,
                            LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP
                        );
                        CREATE INDEX idx_source_files_project ON SourceFiles(ProjectId);

                        CREATE TABLE Assets (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL,
                            SourceFileId INTEGER,
                            UniqueID TEXT NOT NULL,
                            Name TEXT NOT NULL,
                            Type INTEGER NOT NULL,
                            Container TEXT NOT NULL DEFAULT '',
                            PathID INTEGER NOT NULL,
                            ByteStart INTEGER NOT NULL,
                            ByteSize INTEGER NOT NULL
                        );
                        CREATE INDEX idx_assets_project ON Assets(ProjectId);

                        CREATE TABLE AssetEdges (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
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
                        CREATE INDEX idx_asset_edges_source ON AssetEdges(ProjectId, SourceAssetUniqueID);
                        CREATE INDEX idx_asset_edges_target ON AssetEdges(ProjectId, TargetAssetUniqueID);

                        CREATE TABLE MeshRenderers (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL,
                            MeshAssetUniqueID TEXT NOT NULL,
                            RendererAssetUniqueID TEXT NOT NULL,
                            RendererType TEXT NOT NULL,
                            GameObjectAssetUniqueID TEXT,
                            GameObjectName TEXT,
                            Description TEXT
                        );
                        CREATE INDEX idx_mesh_renderers_mesh ON MeshRenderers(ProjectId, MeshAssetUniqueID);

                        CREATE TABLE MeshMaterials (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL,
                            MeshAssetUniqueID TEXT NOT NULL,
                            MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                            RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                            RendererType TEXT NOT NULL DEFAULT '',
                            SubMeshIndex INTEGER NOT NULL,
                            MaterialSlotIndex INTEGER NOT NULL DEFAULT -1,
                            MaterialScore INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE INDEX idx_mesh_materials_mesh ON MeshMaterials(ProjectId, MeshAssetUniqueID);

                        CREATE TABLE MaterialTextures (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL,
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
                        CREATE INDEX idx_material_textures_material ON MaterialTextures(ProjectId, MaterialAssetUniqueID);

                        CREATE TABLE PreviewCacheEntries (
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
                            UpdatedUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000Z',
                            LastAccessedUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000Z'
                        );
                        INSERT INTO PreviewCacheEntries (ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize)
                        VALUES (1, 'preserved_preview', 'png', 1, '256', 'hash123', '/tmp/p.png', 512);";
                    cmd.Parameters.AddWithValue("@folder", projectDir);
                    cmd.Parameters.AddWithValue("@sig", signature);
                    cmd.ExecuteNonQuery();
                }

                SqliteConnection.ClearAllPools();

                // Open with a fresh cache instance -> automatic v11 -> v12 upgrade must succeed cleanly.
                var cacheDir = Path.GetDirectoryName(dbPath)!;
                var upgradedCache = new SQLiteProjectIndexCache(cacheDir);

                // Old handles were cleared so LoadIndexCache returns null (triggering re-index), while preview entry survived.
                Assert.Null(upgradedCache.LoadIndexCache(projectDir, signature));
                var preservedPreview = upgradedCache.LoadPreviewCacheEntry(projectDir, signature, "preserved_preview", "png", 1, "256");
                Assert.NotNull(preservedPreview);
                Assert.Equal("hash123", preservedPreview!.PayloadHash);

                using (var conn = upgradedCache.CreateReadConnection(projectDir))
                {
                    using var versionCmd = conn.CreateCommand();
                    versionCmd.CommandText = "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;";
                    Assert.Equal("14", versionCmd.ExecuteScalar()?.ToString());

                    foreach (var table in ClusteredWithoutRowIdTables)
                    {
                        using var ddlCmd = conn.CreateCommand();
                        ddlCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name;";
                        ddlCmd.Parameters.AddWithValue("@name", table);
                        var ddl = ddlCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                        Assert.Contains("WITHOUT ROWID", ddl, StringComparison.OrdinalIgnoreCase);
                    }

                    var indexes = GetUserIndexNames(conn);
                    foreach (var removed in RemovedRedundantIndexes)
                    {
                        Assert.DoesNotContain(removed, indexes);
                    }
                }

                // Re-saving index + semantic relations on the upgraded DB must succeed cleanly.
                var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 3);
                Assert.True(upgradedCache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(upgradedCache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));
                Assert.Equal(new[] { sampleIds.MaterialId }, upgradedCache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectIdCache_InvalidatesCleanlyAcrossDeleteIndexCacheReSaveAndExternalRowDelete()
        {
            var tempRoot = CreateTempDirectory("sqlite_projectid_cache");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 3);
                var (handles, relations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 3);

                Assert.False(cache.TryGetCachedProjectId(projectDir, signature, out _));
                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(cache.TryGetCachedProjectId(projectDir, signature, out var initialProjectId));
                Assert.True(initialProjectId > 0);

                Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));

                // DeleteIndexCache must evict cached ProjectId and remove DB files.
                cache.DeleteIndexCache(projectDir);
                Assert.False(cache.TryGetCachedProjectId(projectDir, signature, out _));
                Assert.False(File.Exists(cache.GetDbPath(projectDir)));

                // Re-saving recreates DB and repopulates ProjectId cache cleanly.
                var newSignature = signature + "_v2";
                Assert.True(cache.SaveIndexCache(projectDir, newSignature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(cache.TryGetCachedProjectId(projectDir, newSignature, out var rebuiltProjectId));
                Assert.True(rebuiltProjectId > 0);
                Assert.False(cache.TryGetCachedProjectId(projectDir, signature, out _));

                Assert.True(cache.SaveSemanticRelations(projectDir, newSignature, relations, replaceExisting: true));
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, newSignature, sampleIds.MeshId));

                // Simulate external deletion of the Projects row while cache still holds rebuiltProjectId.
                using (var conn = cache.CreateConnection(projectDir))
                {
                    using var delCmd = conn.CreateCommand();
                    delCmd.CommandText = "DELETE FROM Projects WHERE Id = @id;";
                    delCmd.Parameters.AddWithValue("@id", rebuiltProjectId);
                    delCmd.ExecuteNonQuery();
                }

                // Gracefully handles deleted row: LoadIndexCache returns null and evicts stale cache entry; SaveSemanticRelations returns false cleanly.
                Assert.Null(cache.LoadIndexCache(projectDir, newSignature));
                Assert.False(cache.TryGetCachedProjectId(projectDir, newSignature, out _));
                Assert.False(cache.SaveSemanticRelations(projectDir, newSignature, relations, replaceExisting: false));

                // Re-saving restores project cleanly with a fresh ProjectId.
                Assert.True(cache.SaveIndexCache(projectDir, newSignature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.True(cache.TryGetCachedProjectId(projectDir, newSignature, out var recoveredProjectId));
                Assert.True(recoveredProjectId > 0);
                Assert.NotNull(cache.LoadIndexCache(projectDir, newSignature));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void IncrementalAndBulkReplaceSemanticRelations_CoexistWithoutDataLossOrIndexCorruption()
        {
            var tempRoot = CreateTempDirectory("sqlite_bulk_and_incremental");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot, assetGroupCount: 4);
                var (handles, initialRelations, sampleIds) = BuildSyntheticProjectGraph(sourcePath, groupCount: 4);

                var extraMeshId = AssetHandle.BuildUniqueID("level0", sourcePath, 9001);
                var extraMaterialId = AssetHandle.BuildUniqueID("level0", sourcePath, 9002);
                var extraTextureId = AssetHandle.BuildUniqueID("level0", sourcePath, 9003);
                var extraRendererId = AssetHandle.BuildUniqueID("level0", sourcePath, 9004);
                var upgradedMaterialId = AssetHandle.BuildUniqueID("level0", sourcePath, 9005);

                handles.Add(CreateHandle(extraMeshId, "ExtraMesh", ClassIDType.Mesh, sourcePath, 9001));
                handles.Add(CreateHandle(extraMaterialId, "ExtraMat", ClassIDType.Material, sourcePath, 9002));
                handles.Add(CreateHandle(extraTextureId, "ExtraTex", ClassIDType.Texture2D, sourcePath, 9003));
                handles.Add(CreateHandle(extraRendererId, "ExtraRenderer", ClassIDType.SkinnedMeshRenderer, sourcePath, 9004));
                handles.Add(CreateHandle(upgradedMaterialId, "UpgradedMat", ClassIDType.Material, sourcePath, 9005));

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                // Step 1: Bulk replace (replaceExisting: true)
                Assert.True(cache.SaveSemanticRelations(projectDir, signature, initialRelations, replaceExisting: true));
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Empty(cache.LoadMeshMaterialAssetIds(projectDir, signature, extraMeshId));

                // Step 2: Incremental preview update (replaceExisting: false) adding new relations and upserting an existing mesh's material score.
                var incremental = new SemanticAssetRelations();
                incremental.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    extraMeshId, extraRendererId, "SkinnedMeshRenderer", string.Empty, "ExtraGo", "Incremental"));
                incremental.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    extraMeshId, extraMaterialId, extraRendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 100));
                incremental.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    extraMaterialId, extraMaterialId, "_BaseMap", 0, extraTextureId, 0, 9003, isResolved: true, isMainTexture: true));
                // Also add a higher-scored material for sampleIds.MeshId on a different renderer slot to verify upsert/merge without losing initial rows.
                incremental.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    sampleIds.MeshId, upgradedMaterialId, extraRendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 500));
                incremental.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    upgradedMaterialId, upgradedMaterialId, "_BaseMap", 0, extraTextureId, 0, 9003, isResolved: true, isMainTexture: true));

                Assert.True(cache.SaveSemanticRelations(projectDir, signature, incremental, replaceExisting: false));

                // Verify both initial AND incremental relations exist and indexes remain intact.
                Assert.Equal(new[] { extraMaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, extraMeshId));
                Assert.Equal(new[] { extraTextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, extraMaterialId));
                Assert.Equal(new[] { extraRendererId }, cache.LoadMeshRendererAssetIds(projectDir, signature, extraMeshId));
                Assert.Equal(new[] { upgradedMaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));
                Assert.Equal(new[] { sampleIds.TextureId }, cache.LoadMaterialTextureAssetIds(projectDir, signature, sampleIds.MaterialId));

                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    var indexes = GetUserIndexNames(conn);
                    foreach (var expected in ExpectedActiveIndexes)
                    {
                        Assert.Contains(expected, indexes);
                    }
                }

                // Step 3: Subsequent bulk replace (replaceExisting: true) cleanly removes stale incremental rows and keeps indexes & synchronous=NORMAL intact.
                Assert.True(cache.SaveSemanticRelations(projectDir, signature, initialRelations, replaceExisting: true));
                Assert.Empty(cache.LoadMeshMaterialAssetIds(projectDir, signature, extraMeshId));
                Assert.Equal(new[] { sampleIds.MaterialId }, cache.LoadMeshMaterialAssetIds(projectDir, signature, sampleIds.MeshId));

                using (var writeConn = cache.CreateConnection(projectDir))
                {
                    Assert.Equal(1L, ReadPragmaInt64(writeConn, "synchronous"));
                    var indexes = GetUserIndexNames(writeConn);
                    foreach (var expected in ExpectedActiveIndexes)
                    {
                        Assert.Contains(expected, indexes);
                    }
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static List<string> AssertQueryPlanUsesSearchWithoutPhysicalScans(
            SqliteConnection conn,
            string queryName,
            string sql,
            Action<SqliteCommand> bindParameters,
            string[]? allowedCteScanNames = null)
        {
            var planLines = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                bindParameters(cmd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    planLines.Add(reader.GetString(3));
                }
            }

            Assert.NotEmpty(planLines);
            var formattedPlan = string.Join(Environment.NewLine, planLines);

            Assert.True(
                planLines.Any(line => line.Contains("SEARCH ", StringComparison.OrdinalIgnoreCase)),
                $"Expected query '{queryName}' to use SEARCH, but plan was:{Environment.NewLine}{formattedPlan}");

            var allowedCtes = new HashSet<string>(allowedCteScanNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var line in planLines)
            {
                if (!line.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Extract the target identifier right after "SCAN " (e.g. "SCAN ae" -> "ae", "SCAN RankedMaterials" -> "RankedMaterials").
                var remainder = line.Substring(5).Trim();
                var spaceIdx = remainder.IndexOf(' ');
                var target = spaceIdx >= 0 ? remainder.Substring(0, spaceIdx) : remainder;

                var isAllowedCteOrCoroutine = allowedCtes.Contains(target)
                    || (allowedCtes.Count > 0 && target.StartsWith("(subquery-", StringComparison.OrdinalIgnoreCase));

                Assert.True(
                    isAllowedCteOrCoroutine,
                    $"Query '{queryName}' performed an unexpected SCAN on '{target}' (line: '{line}'):{Environment.NewLine}{formattedPlan}");
            }

            return planLines;
        }

        private static HashSet<string> GetUserIndexNames(SqliteConnection conn)
        {
            var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                indexes.Add(reader.GetString(0));
            }

            return indexes;
        }

        private static long ReadPragmaInt64(SqliteConnection conn, string pragmaName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragmaName};";
            var value = cmd.ExecuteScalar();
            Assert.NotNull(value);
            return Convert.ToInt64(value);
        }

        private static string ReadPragmaString(SqliteConnection conn, string pragmaName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragmaName};";
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, ProjectScanResult ScanResult, string SourcePath)
            CreateSampleProject(string tempRoot, int assetGroupCount)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = Path.Combine(tempRoot, "SampleProject");
            Directory.CreateDirectory(projectDir);
            var sourcePath = Path.Combine(projectDir, "level0");
            File.WriteAllBytes(sourcePath, new byte[Math.Max(16, assetGroupCount * 8)]);

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePath);
        }

        private sealed record SampleGraphIds(
            string MeshId,
            string MaterialId,
            string PreviewMaterialId,
            string TextureId,
            string AvatarId,
            string ClipId,
            string RendererId,
            string GroupId);

        private static (List<AssetHandle> Handles, SemanticAssetRelations Relations, SampleGraphIds SampleIds)
            BuildSyntheticProjectGraph(string sourcePath, int groupCount)
        {
            var handles = new List<AssetHandle>(groupCount * 7);
            var relations = new SemanticAssetRelations();
            relations.SourceFiles.Add(new SemanticSourceFileEntry("level0", sourcePath, "2022.3.10f1", groupCount * 7));

            SampleGraphIds? sampleIds = null;
            for (var i = 0; i < groupCount; i++)
            {
                var basePathId = (i + 1) * 100L;
                var meshId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 1);
                var materialId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 2);
                var previewMaterialId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 3);
                var textureId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 4);
                var avatarId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 5);
                var clipId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 6);
                var rendererId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 7);
                var rootGoId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 8);
                var groupId = AssetHandle.BuildUniqueID("level0", sourcePath, basePathId + 9);

                handles.Add(CreateHandle(meshId, $"Mesh_{i:D3}", ClassIDType.Mesh, sourcePath, basePathId + 1));
                handles.Add(CreateHandle(materialId, $"Mat_{i:D3}", ClassIDType.Material, sourcePath, basePathId + 2));
                handles.Add(CreateHandle(previewMaterialId, $"PreviewMat_{i:D3}", ClassIDType.Material, sourcePath, basePathId + 3));
                handles.Add(CreateHandle(textureId, $"Tex_{i:D3}", ClassIDType.Texture2D, sourcePath, basePathId + 4));
                handles.Add(CreateHandle(avatarId, $"Avatar_{i:D3}", ClassIDType.Avatar, sourcePath, basePathId + 5));
                handles.Add(CreateHandle(clipId, $"Clip_{i:D3}", ClassIDType.AnimationClip, sourcePath, basePathId + 6));
                handles.Add(CreateHandle(rendererId, $"Renderer_{i:D3}", ClassIDType.SkinnedMeshRenderer, sourcePath, basePathId + 7));

                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "Mesh", "AvatarMesh", 0, meshId, 0, basePathId + 5, 0, basePathId + 1, isResolved: true));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    clipId, "Avatar", "AnimatorAvatar", 0, avatarId, 0, basePathId + 6, 0, basePathId + 5, isResolved: true));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "AnimationClip", "AnimatorClip", 0, clipId, 0, basePathId + 5, 0, basePathId + 6, isResolved: true));

                relations.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", $"Rig_{i:D3}", rootGoId, $"Root_{i:D3}", string.Empty, avatarId, string.Empty, "level0", 90, "ExactMatch"));
                relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_{i:D3}", 0, null, 90, "DirectRenderer"));
                relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    meshId, rendererId, "SkinnedMeshRenderer", rootGoId, $"Body_{i:D3}", "Primary body"));
                relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshId, materialId, rendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 100));
                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId, previewMaterialId, "_BaseMap", 0, textureId, 0, basePathId + 4, isResolved: true, isMainTexture: true));

                if (i == groupCount / 2)
                {
                    sampleIds = new SampleGraphIds(meshId, materialId, previewMaterialId, textureId, avatarId, clipId, rendererId, groupId);
                }
            }

            return (handles, relations, sampleIds!);
        }

        private static AssetHandle CreateHandle(string uniqueId, string name, ClassIDType type, string sourcePath, long pathId)
        {
            return new AssetHandle
            {
                UniqueID = uniqueId,
                Name = name,
                Type = type,
                Container = $"assets/{name.ToLowerInvariant()}",
                OriginalPath = sourcePath,
                SerializedFileName = "level0",
                PathID = pathId,
                ByteStart = pathId * 64,
                ByteSize = 1024
            };
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudioTests_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
