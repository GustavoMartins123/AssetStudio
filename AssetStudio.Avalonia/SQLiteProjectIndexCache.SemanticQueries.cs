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

        internal List<string?> LoadMeshMaterialAssetIds(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<string?>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "mesh material relations", (conn, transaction, projectId) =>
            {
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
    }
}
