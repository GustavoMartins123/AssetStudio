using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        internal bool HasSemanticRelations(string folderPath, string signature)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            return GetOrResolveSemanticCacheStatus(folderPath, signature).HasRelations;
        }

        internal List<ResolvedMeshSubMeshMaterial> LoadResolvedMeshMaterials(
            string folderPath,
            string signature,
            string meshAssetId,
            string? rendererAssetId = null)
        {
            var result = new List<ResolvedMeshSubMeshMaterial>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "resolved mesh materials", (conn, transaction, projectId) =>
            {
                if (!string.IsNullOrWhiteSpace(rendererAssetId))
                {
                    using var rendererCmd = conn.CreateCommand();
                    rendererCmd.Transaction = transaction;
                    rendererCmd.CommandText = @"
                        WITH RankedBySubMesh AS (
                            SELECT
                                mm.MeshAssetUniqueID,
                                mm.SubMeshIndex,
                                mm.MaterialAssetUniqueID,
                                mm.RendererAssetUniqueID,
                                mm.RendererType,
                                COALESCE(NULLIF(mc.MaterialName, ''), matAsset.Name, '') AS MaterialName,
                                COALESCE(mc.MainTextureAssetUniqueID, '') AS MainTextureAssetUniqueID,
                                COALESCE(mc.MainTextureSlotName, '') AS MainTextureSlotName,
                                COALESCE(mc.BaseColorR, 1.0) AS BaseColorR,
                                COALESCE(mc.BaseColorG, 1.0) AS BaseColorG,
                                COALESCE(mc.BaseColorB, 1.0) AS BaseColorB,
                                COALESCE(mc.BaseColorA, 1.0) AS BaseColorA,
                                COALESCE(NULLIF(mc.ResolutionSource, ''), 'preparation') AS ResolutionSource,
                                ROW_NUMBER() OVER (
                                    PARTITION BY mm.SubMeshIndex
                                    ORDER BY
                                        CASE WHEN mm.MaterialAssetUniqueID <> '' THEN 0 ELSE 1 END,
                                        mm.MaterialSlotIndex ASC,
                                        mm.MaterialAssetUniqueID ASC
                                ) AS RowNum
                            FROM MeshMaterials mm
                            LEFT JOIN MaterialCache mc
                              ON mc.ProjectId = mm.ProjectId
                             AND mc.MaterialAssetUniqueID = mm.MaterialAssetUniqueID
                            LEFT JOIN Assets matAsset
                              ON matAsset.ProjectId = mm.ProjectId
                             AND mm.MaterialAssetUniqueID <> ''
                             AND matAsset.UniqueID = mm.MaterialAssetUniqueID
                            WHERE mm.ProjectId = @projectId
                              AND mm.MeshAssetUniqueID = @meshAssetId
                              AND mm.RendererAssetUniqueID = @rendererAssetId
                        )
                        SELECT
                            MeshAssetUniqueID, SubMeshIndex, MaterialAssetUniqueID,
                            RendererAssetUniqueID, RendererType, MaterialName,
                            MainTextureAssetUniqueID, MainTextureSlotName,
                            BaseColorR, BaseColorG, BaseColorB, BaseColorA, ResolutionSource
                        FROM RankedBySubMesh
                        WHERE RowNum = 1
                        ORDER BY SubMeshIndex";
                    rendererCmd.Parameters.AddWithValue("@projectId", projectId);
                    rendererCmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                    rendererCmd.Parameters.AddWithValue("@rendererAssetId", rendererAssetId);
                    using var reader = rendererCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(ReadResolvedMeshSubMeshMaterial(reader));
                    }

                    if (result.Count > 0)
                    {
                        return;
                    }
                }

                // O(1) PK range scan on MeshMaterialResolvedCache
                using (var cacheCmd = conn.CreateCommand())
                {
                    cacheCmd.Transaction = transaction;
                    cacheCmd.CommandText = @"
                        SELECT
                            MeshAssetUniqueID, SubMeshIndex, MaterialAssetUniqueID,
                            RendererAssetUniqueID, RendererType, MaterialName,
                            MainTextureAssetUniqueID, MainTextureSlotName,
                            BaseColorR, BaseColorG, BaseColorB, BaseColorA, ResolutionSource
                        FROM MeshMaterialResolvedCache
                        WHERE ProjectId = @projectId
                          AND MeshAssetUniqueID = @meshAssetId
                        ORDER BY SubMeshIndex";
                    cacheCmd.Parameters.AddWithValue("@projectId", projectId);
                    cacheCmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                    using var reader = cacheCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(ReadResolvedMeshSubMeshMaterial(reader));
                    }
                }
            });

            return result;
        }

        private static ResolvedMeshSubMeshMaterial ReadResolvedMeshSubMeshMaterial(SqliteDataReader reader)
        {
            return new ResolvedMeshSubMeshMaterial(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? 1f : (float)reader.GetDouble(8),
                reader.IsDBNull(9) ? 1f : (float)reader.GetDouble(9),
                reader.IsDBNull(10) ? 1f : (float)reader.GetDouble(10),
                reader.IsDBNull(11) ? 1f : (float)reader.GetDouble(11),
                reader.IsDBNull(12) ? "preparation" : reader.GetString(12));
        }

        internal MaterialCacheEntryInfo? LoadMaterialCacheEntry(string folderPath, string signature, string materialAssetId)
        {
            if (string.IsNullOrWhiteSpace(materialAssetId))
            {
                return null;
            }

            MaterialCacheEntryInfo? result = null;
            ExecuteProjectReadQuery(folderPath, signature, "material cache entry", (conn, transaction, projectId) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT
                        MaterialAssetUniqueID, MaterialName, ParentMaterialAssetUniqueID,
                        MainTextureAssetUniqueID, MainTextureSlotName,
                        BaseColorR, BaseColorG, BaseColorB, BaseColorA,
                        ShaderName, TextureSlotCount, ResolutionSource
                    FROM MaterialCache
                    WHERE ProjectId = @projectId
                      AND MaterialAssetUniqueID = @materialAssetId
                    LIMIT 1";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@materialAssetId", materialAssetId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    result = new MaterialCacheEntryInfo(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        reader.IsDBNull(5) ? 1f : (float)reader.GetDouble(5),
                        reader.IsDBNull(6) ? 1f : (float)reader.GetDouble(6),
                        reader.IsDBNull(7) ? 1f : (float)reader.GetDouble(7),
                        reader.IsDBNull(8) ? 1f : (float)reader.GetDouble(8),
                        reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                        reader.IsDBNull(11) ? "preparation" : reader.GetString(11));
                }
            });

            return result;
        }

        internal List<MaterialTextureSlotInfo> LoadMaterialTextureSlots(string folderPath, string signature, string materialAssetId)
        {
            var result = new List<MaterialTextureSlotInfo>();
            if (string.IsNullOrWhiteSpace(materialAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "material texture slots", (conn, transaction, projectId) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT
                        MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex,
                        TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                        ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource
                    FROM (
                        SELECT
                            MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex,
                            TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                            ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource, 0 AS SourceRank
                        FROM MaterialTextures
                        WHERE ProjectId = @projectId
                          AND MaterialAssetUniqueID = @materialAssetId
                        UNION ALL
                        SELECT
                            MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex,
                            TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                            ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource, 1 AS SourceRank
                        FROM MaterialTextures
                        WHERE ProjectId = @projectId
                          AND PreviewMaterialAssetUniqueID = @materialAssetId
                          AND PreviewMaterialAssetUniqueID <> MaterialAssetUniqueID
                    )
                    ORDER BY SourceRank ASC, IsMainTexture DESC, SlotIndex ASC, SlotName ASC";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@materialAssetId", materialAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new MaterialTextureSlotInfo(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7) != 0,
                        reader.GetInt32(8) != 0,
                        reader.IsDBNull(9) ? 1f : (float)reader.GetDouble(9),
                        reader.IsDBNull(10) ? 1f : (float)reader.GetDouble(10),
                        reader.IsDBNull(11) ? 0f : (float)reader.GetDouble(11),
                        reader.IsDBNull(12) ? 0f : (float)reader.GetDouble(12),
                        reader.IsDBNull(13) ? "preparation" : reader.GetString(13)));
                }
            });

            return result;
        }

        internal CompositeAssetInfo? LoadCompositeAssetWithParts(string folderPath, string signature, string compositeOrMemberAssetId)
        {
            if (string.IsNullOrWhiteSpace(compositeOrMemberAssetId))
            {
                return null;
            }

            CompositeAssetInfo? result = null;
            ExecuteProjectReadQuery(folderPath, signature, "composite asset with parts", (conn, transaction, projectId) =>
            {
                var compositeId = string.Empty;
                using (var findCmd = conn.CreateCommand())
                {
                    findCmd.Transaction = transaction;
                    findCmd.CommandText = @"
                        SELECT CompositeAssetUniqueID
                        FROM CompositeAssets
                        WHERE ProjectId = @projectId
                          AND CompositeAssetUniqueID = @id
                        LIMIT 1";
                    findCmd.Parameters.AddWithValue("@projectId", projectId);
                    findCmd.Parameters.AddWithValue("@id", compositeOrMemberAssetId);
                    compositeId = findCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(compositeId))
                {
                    using var memberCmd = conn.CreateCommand();
                    memberCmd.Transaction = transaction;
                    memberCmd.CommandText = @"
                        SELECT ca.CompositeAssetUniqueID
                        FROM CompositeAssetParts cap
                        INNER JOIN CompositeAssets ca
                          ON ca.ProjectId = cap.ProjectId
                         AND ca.CompositeAssetUniqueID = cap.CompositeAssetUniqueID
                        WHERE cap.ProjectId = @projectId
                          AND cap.MeshAssetUniqueID = @id
                        ORDER BY
                            CASE WHEN ca.CompositeKind = 'Animator' THEN 0 ELSE 1 END,
                            ca.Confidence DESC,
                            ca.MeshPartCount ASC,
                            ca.CompositeAssetUniqueID ASC
                        LIMIT 1";
                    memberCmd.Parameters.AddWithValue("@projectId", projectId);
                    memberCmd.Parameters.AddWithValue("@id", compositeOrMemberAssetId);
                    compositeId = memberCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(compositeId))
                {
                    return;
                }

                result = LoadSingleCompositeAssetWithPartsCore(conn, transaction, projectId, compositeId);
            });

            return result;
        }

        internal List<CompositeAssetInfo> LoadCompositeAssetsForMesh(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<CompositeAssetInfo>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "composite assets for mesh", (conn, transaction, projectId) =>
            {
                var compositeIds = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT DISTINCT ca.CompositeAssetUniqueID
                        FROM CompositeAssetParts cap
                        INNER JOIN CompositeAssets ca
                          ON ca.ProjectId = cap.ProjectId
                         AND ca.CompositeAssetUniqueID = cap.CompositeAssetUniqueID
                        WHERE cap.ProjectId = @projectId
                          AND cap.MeshAssetUniqueID = @meshAssetId
                        ORDER BY
                            CASE WHEN ca.CompositeKind = 'Animator' THEN 0 ELSE 1 END,
                            ca.Confidence DESC,
                            ca.MeshPartCount ASC,
                            ca.DisplayName ASC,
                            ca.CompositeAssetUniqueID ASC";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        compositeIds.Add(reader.GetString(0));
                    }
                }

                foreach (var cid in compositeIds)
                {
                    var info = LoadSingleCompositeAssetWithPartsCore(conn, transaction, projectId, cid);
                    if (info != null)
                    {
                        result.Add(info);
                    }
                }
            });

            return result;
        }

        private static CompositeAssetInfo? LoadSingleCompositeAssetWithPartsCore(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string compositeId)
        {
            CompositeAssetInfo? info = null;
            using (var headerCmd = conn.CreateCommand())
            {
                headerCmd.Transaction = transaction;
                headerCmd.CommandText = @"
                    SELECT
                        CompositeAssetUniqueID, CompositeKind, DisplayName,
                        RootGameObjectAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                        SourceFileName, MeshPartCount, DistinctMeshCount, DistinctMaterialCount, Confidence, ResolutionSource
                    FROM CompositeAssets
                    WHERE ProjectId = @projectId
                      AND CompositeAssetUniqueID = @compositeId
                    LIMIT 1";
                headerCmd.Parameters.AddWithValue("@projectId", projectId);
                headerCmd.Parameters.AddWithValue("@compositeId", compositeId);
                using var reader = headerCmd.ExecuteReader();
                if (reader.Read())
                {
                    info = new CompositeAssetInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetInt32(11),
                        reader.GetString(12));
                }
            }

            if (info == null)
            {
                return null;
            }

            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            var seenMaterials = new HashSet<string>(StringComparer.Ordinal);
            var seenTextures = new HashSet<string>(StringComparer.Ordinal);

            using (var partsCmd = conn.CreateCommand())
            {
                partsCmd.Transaction = transaction;
                partsCmd.CommandText = @"
                    SELECT
                        cap.CompositeAssetUniqueID,
                        cap.PartIndex,
                        cap.MeshAssetUniqueID,
                        cap.RendererAssetUniqueID,
                        cap.RendererType,
                        cap.GameObjectAssetUniqueID,
                        cap.GameObjectName,
                        cap.TransformMatrix,
                        cap.PrimaryMaterialAssetUniqueID,
                        cap.Confidence,
                        COALESCE(mesh.Name, ''),
                        COALESCE(mesh.PathID, 0),
                        COALESCE(mesh.ByteSize, 0),
                        COALESCE(mc.MainTextureAssetUniqueID, '')
                    FROM CompositeAssetParts cap
                    LEFT JOIN Assets mesh
                      ON mesh.ProjectId = cap.ProjectId
                     AND mesh.UniqueID = cap.MeshAssetUniqueID
                    LEFT JOIN MaterialCache mc
                      ON mc.ProjectId = cap.ProjectId
                     AND cap.PrimaryMaterialAssetUniqueID <> ''
                     AND mc.MaterialAssetUniqueID = cap.PrimaryMaterialAssetUniqueID
                    WHERE cap.ProjectId = @projectId
                      AND cap.CompositeAssetUniqueID = @compositeId
                    ORDER BY cap.PartIndex ASC";
                partsCmd.Parameters.AddWithValue("@projectId", projectId);
                partsCmd.Parameters.AddWithValue("@compositeId", compositeId);
                using var reader = partsCmd.ExecuteReader();
                while (reader.Read())
                {
                    var meshId = reader.GetString(2);
                    var matId = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
                    var texId = reader.IsDBNull(13) ? string.Empty : reader.GetString(13);
                    info.Parts.Add(new CompositeAssetPartInfo(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        meshId,
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        ReadTransformMatrix(reader, 7),
                        matId,
                        reader.GetInt32(9),
                        reader.GetString(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12)));

                    if (!string.IsNullOrEmpty(meshId) && seenMeshes.Add(meshId))
                    {
                        info.DistinctMeshIds.Add(meshId);
                    }
                    if (!string.IsNullOrEmpty(matId) && seenMaterials.Add(matId))
                    {
                        info.DistinctMaterialIds.Add(matId);
                    }
                    if (!string.IsNullOrEmpty(texId) && seenTextures.Add(texId))
                    {
                        info.DistinctTextureIds.Add(texId);
                    }
                }
            }

            return info;
        }

        internal List<string?> LoadMeshMaterialAssetIds(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<string?>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "mesh material relations", (conn, transaction, projectId) =>
            {
                // Fast v15 path: single PK range scan on pre-ranked MeshMaterialResolvedCache (<0.2ms)
                using (var fastCmd = conn.CreateCommand())
                {
                    fastCmd.Transaction = transaction;
                    fastCmd.CommandText = @"
                        SELECT SubMeshIndex, MaterialAssetUniqueID
                        FROM MeshMaterialResolvedCache
                        WHERE ProjectId = @projectId
                          AND MeshAssetUniqueID = @meshAssetId
                        ORDER BY SubMeshIndex";
                    fastCmd.Parameters.AddWithValue("@projectId", projectId);
                    fastCmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                    using var fastReader = fastCmd.ExecuteReader();
                    while (fastReader.Read())
                    {
                        var subMeshIndex = fastReader.GetInt32(0);
                        var materialAssetId = fastReader.IsDBNull(1) ? string.Empty : fastReader.GetString(1);
                        while (result.Count < subMeshIndex)
                        {
                            result.Add(null);
                        }

                        result.Add(string.IsNullOrWhiteSpace(materialAssetId) ? null : materialAssetId);
                    }
                }

                if (result.Count > 0)
                {
                    return;
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
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
            });

            return result;
        }

        internal List<string> LoadMaterialTextureAssetIds(string folderPath, string signature, string materialAssetId, string? slotName = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(materialAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "material texture relations", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@materialAssetId", materialAssetId);
                cmd.Parameters.AddWithValue("@slotName", slotName ?? string.Empty);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            });

            return result;
        }

        internal List<string> LoadAvatarMeshAssetIds(string folderPath, string signature, string avatarAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(avatarAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "avatar mesh relations", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@avatarAssetId", avatarAssetId);
                cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            });

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

            ExecuteProjectReadQuery(folderPath, signature, "avatar mesh relations", (conn, transaction, projectId) =>
            {
                const int chunkSize = 400;
                for (var offset = 0; offset < ids.Count; offset += chunkSize)
                {
                    var chunk = ids.Skip(offset).Take(chunkSize).ToList();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    var placeholders = new List<string>(chunk.Count);
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        var parameterName = "@avatarId" + i.ToString(CultureInfo.InvariantCulture);
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
                    cmd.Parameters.AddWithValue("@projectId", projectId);
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
            });

            return result;
        }

        internal List<string> LoadMeshAvatarAssetIds(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "mesh avatar relations", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            });

            return result;
        }

        internal List<string> LoadMeshRendererAssetIds(string folderPath, string signature, string meshAssetId, string? rendererType = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "mesh renderer relations", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                cmd.Parameters.AddWithValue("@rendererType", rendererType ?? string.Empty);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            });

            return result;
        }

        internal List<AnimationClipTargetInfo> LoadAnimationClipTargets(
            string folderPath,
            string signature,
            string clipAssetId,
            int maxResults = 32)
        {
            var result = new List<AnimationClipTargetInfo>();
            if (string.IsNullOrWhiteSpace(clipAssetId) || maxResults <= 0)
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "animation clip target relations", (conn, transaction, projectId) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    SELECT
                        t.ClipAssetUniqueID,
                        t.AnimatorAssetUniqueID,
                        t.AvatarAssetUniqueID,
                        t.MeshAssetUniqueID,
                        t.ModelGroupId,
                        t.RelationKind,
                        t.MatchScore,
                        t.MatchedBoneCount,
                        t.TotalClipBoneCount,
                        COALESCE(avatarAsset.Name, ''),
                        COALESCE(meshAsset.Name, ''),
                        COALESCE(
                            NULLIF(mg.GroupName, ''),
                            NULLIF(mg.RootGameObjectName, ''),
                            ''
                        ),
                        COALESCE(mg.Confidence, 0)
                    FROM AnimationClipTargets t
                    LEFT JOIN Assets avatarAsset
                      ON avatarAsset.ProjectId = t.ProjectId
                     AND t.AvatarAssetUniqueID <> ''
                     AND avatarAsset.UniqueID = t.AvatarAssetUniqueID
                    LEFT JOIN Assets meshAsset
                      ON meshAsset.ProjectId = t.ProjectId
                     AND t.MeshAssetUniqueID <> ''
                     AND meshAsset.UniqueID = t.MeshAssetUniqueID
                    LEFT JOIN ModelGroups mg
                      ON mg.ProjectId = t.ProjectId
                     AND t.ModelGroupId <> ''
                     AND mg.GroupAssetUniqueID = t.ModelGroupId
                    WHERE t.ProjectId = @projectId
                      AND t.ClipAssetUniqueID = @clipAssetId
                      AND (t.AvatarAssetUniqueID <> '' OR t.MeshAssetUniqueID <> '' OR t.ModelGroupId <> '')
                    ORDER BY
                        CASE
                            WHEN t.AvatarAssetUniqueID <> '' AND t.MeshAssetUniqueID <> '' THEN 0
                            WHEN t.ModelGroupId <> '' THEN 1
                            WHEN t.MeshAssetUniqueID <> '' THEN 2
                            ELSE 3
                        END,
                        t.MatchScore DESC,
                        t.MatchedBoneCount DESC,
                        CASE WHEN t.ModelGroupId <> '' THEN 0 ELSE 1 END,
                        t.AvatarAssetUniqueID,
                        t.MeshAssetUniqueID,
                        t.ModelGroupId
                    LIMIT @maxResults";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@clipAssetId", clipAssetId);
                cmd.Parameters.AddWithValue("@maxResults", maxResults);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new AnimationClipTargetInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetString(9),
                        reader.GetString(10),
                        reader.GetString(11),
                        reader.GetInt32(12)));
                }
            });

            return result;
        }

        internal List<string> LoadAnimationClipAvatarAssetIds(string folderPath, string signature, string clipAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(clipAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "animation clip avatar relations", (conn, transaction, projectId) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    WITH AvatarCandidates AS (
                        SELECT
                            t.AvatarAssetUniqueID AS AvatarAssetId,
                            -t.MatchScore AS SortIndex
                        FROM AnimationClipTargets t
                        WHERE t.ProjectId = @projectId
                          AND t.ClipAssetUniqueID = @clipAssetId
                          AND t.AvatarAssetUniqueID <> ''
                        UNION ALL
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@clipAssetId", clipAssetId);
                cmd.Parameters.AddWithValue("@avatarType", (int)ClassIDType.Avatar);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }
            });

            return result;
        }

        internal List<string> LoadAnimationClipMeshAssetIds(string folderPath, string signature, string clipAssetId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(clipAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "animation clip mesh relations", (conn, transaction, projectId) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    WITH MeshCandidates AS (
                        SELECT
                            t.MeshAssetUniqueID AS MeshAssetId,
                            -t.MatchScore AS SortIndex
                        FROM AnimationClipTargets t
                        WHERE t.ProjectId = @projectId
                          AND t.ClipAssetUniqueID = @clipAssetId
                          AND t.MeshAssetUniqueID <> ''
                        UNION ALL
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@clipAssetId", clipAssetId);
                cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(reader.GetString(0));
                    }
                });

                return result;
            }

            internal List<CompatibleAnimationClipInfo> LoadCompatibleAnimationClipsForTarget(
                string folderPath,
                string signature,
                string? avatarAssetId = null,
                string? modelGroupId = null,
                string? animatorAssetId = null,
                string? controllerAssetId = null,
                IReadOnlyList<string>? meshAssetIds = null,
                int maxResults = 512)
            {
                var result = new List<CompatibleAnimationClipInfo>();
                if (maxResults <= 0)
                {
                    return result;
                }

                var cleanAvatarId = avatarAssetId?.Trim() ?? string.Empty;
                var cleanGroupId = modelGroupId?.Trim() ?? string.Empty;
                var cleanAnimatorId = animatorAssetId?.Trim() ?? string.Empty;
                var cleanControllerId = controllerAssetId?.Trim() ?? string.Empty;
                var cleanMeshIds = (meshAssetIds ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Take(64)
                    .ToList();

                if (string.IsNullOrEmpty(cleanAvatarId)
                    && string.IsNullOrEmpty(cleanGroupId)
                    && string.IsNullOrEmpty(cleanAnimatorId)
                    && string.IsNullOrEmpty(cleanControllerId)
                    && cleanMeshIds.Count == 0)
                {
                    return result;
                }

                ExecuteProjectReadQuery(folderPath, signature, "compatible animation clips for target", (conn, transaction, projectId) =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@clipType", (int)ClassIDType.AnimationClip);
                    cmd.Parameters.AddWithValue("@maxResults", maxResults);

                    var unionQueries = new List<string>();
                    if (!string.IsNullOrEmpty(cleanAvatarId))
                    {
                        cmd.Parameters.AddWithValue("@avatarId", cleanAvatarId);
                        unionQueries.Add(@"
                            SELECT
                                t.ClipAssetUniqueID AS ClipId,
                                t.RelationKind AS RelKind,
                                t.MatchScore AS Score,
                                t.MatchedBoneCount AS MatchedBones,
                                t.TotalClipBoneCount AS TotalBones
                            FROM AnimationClipTargets t
                            WHERE t.ProjectId = @projectId
                              AND t.AvatarAssetUniqueID = @avatarId");
                    }

                    if (!string.IsNullOrEmpty(cleanGroupId))
                    {
                        cmd.Parameters.AddWithValue("@groupId", cleanGroupId);
                        unionQueries.Add(@"
                            SELECT
                                t.ClipAssetUniqueID AS ClipId,
                                t.RelationKind AS RelKind,
                                t.MatchScore AS Score,
                                t.MatchedBoneCount AS MatchedBones,
                                t.TotalClipBoneCount AS TotalBones
                            FROM AnimationClipTargets t
                            WHERE t.ProjectId = @projectId
                              AND t.ModelGroupId = @groupId");
                    }

                    if (!string.IsNullOrEmpty(cleanAnimatorId))
                    {
                        cmd.Parameters.AddWithValue("@animatorId", cleanAnimatorId);
                        unionQueries.Add(@"
                            SELECT
                                t.ClipAssetUniqueID AS ClipId,
                                t.RelationKind AS RelKind,
                                t.MatchScore AS Score,
                                t.MatchedBoneCount AS MatchedBones,
                                t.TotalClipBoneCount AS TotalBones
                            FROM AnimationClipTargets t
                            WHERE t.ProjectId = @projectId
                              AND t.AnimatorAssetUniqueID = @animatorId");
                    }

                    if (cleanMeshIds.Count > 0)
                    {
                        var meshPlaceholders = new List<string>(cleanMeshIds.Count);
                        for (var i = 0; i < cleanMeshIds.Count; i++)
                        {
                            var paramName = "@meshId" + i.ToString(CultureInfo.InvariantCulture);
                            meshPlaceholders.Add(paramName);
                            cmd.Parameters.AddWithValue(paramName, cleanMeshIds[i]);
                        }

                        unionQueries.Add($@"
                            SELECT
                                t.ClipAssetUniqueID AS ClipId,
                                t.RelationKind AS RelKind,
                                t.MatchScore AS Score,
                                t.MatchedBoneCount AS MatchedBones,
                                t.TotalClipBoneCount AS TotalBones
                            FROM AnimationClipTargets t
                            WHERE t.ProjectId = @projectId
                              AND t.MeshAssetUniqueID IN ({string.Join(",", meshPlaceholders)})");
                    }

                    if (!string.IsNullOrEmpty(cleanControllerId))
                    {
                        cmd.Parameters.AddWithValue("@controllerId", cleanControllerId);
                        unionQueries.Add(@"
                            SELECT
                                ae.TargetAssetUniqueID AS ClipId,
                                'AnimatorController' AS RelKind,
                                10000 AS Score,
                                0 AS MatchedBones,
                                0 AS TotalBones
                            FROM AssetEdges ae
                            WHERE ae.ProjectId = @projectId
                              AND ae.SourceAssetUniqueID = @controllerId
                              AND ae.EdgeKind = 'AnimationClip'
                              AND ae.TargetAssetUniqueID <> ''");
                    }

                    cmd.CommandText = $@"
                        WITH RawCandidates AS (
                            {string.Join(" UNION ALL ", unionQueries)}
                        )
                        SELECT
                            rc.ClipId,
                            COALESCE(clipAsset.Name, ''),
                            COALESCE(clipAsset.PathID, 0),
                            MAX(rc.RelKind),
                            MAX(rc.Score) AS BestScore,
                            MAX(rc.MatchedBones) AS BestMatchedBones,
                            MAX(rc.TotalBones) AS BestTotalBones
                        FROM RawCandidates rc
                        INNER JOIN Assets clipAsset
                          ON clipAsset.ProjectId = @projectId
                         AND clipAsset.UniqueID = rc.ClipId
                         AND clipAsset.Type = @clipType
                        WHERE rc.ClipId <> ''
                        GROUP BY rc.ClipId
                        ORDER BY BestScore DESC, BestMatchedBones DESC, clipAsset.Name ASC, rc.ClipId ASC
                        LIMIT @maxResults";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(new CompatibleAnimationClipInfo(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetInt64(2),
                            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                            reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
                    }
                });

                return result;
            }
        }
    }
