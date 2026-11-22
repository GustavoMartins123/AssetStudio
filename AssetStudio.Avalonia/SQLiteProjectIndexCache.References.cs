using System;
using System.Collections.Generic;
using System.Globalization;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        internal ModelGroupInfo? LoadModelGroupById(string folderPath, string signature, string groupId)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(groupId))
            {
                return null;
            }

            ModelGroupInfo? result = null;
            ExecuteProjectReadQuery(folderPath, signature, "model group by id", (conn, transaction, projectId) =>
            {
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
                      AND GroupAssetUniqueID = @groupId
                    LIMIT 1";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@groupId", groupId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    result = ReadModelGroupInfo(reader);
                }
            });

            return result;
        }

        internal void LoadAssetReferencesFromSemanticCache(
            string folderPath,
            string signature,
            string assetUniqueId,
            List<AssetReferenceItem> referencedByOut,
            List<AssetReferenceItem> referencesOut,
            int maxPerDirection = 500)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(assetUniqueId)
                || maxPerDirection <= 0)
            {
                return;
            }

            ExecuteProjectReadQuery(folderPath, signature, "asset references", (conn, transaction, projectId) =>
            {
                // 1) Outgoing AssetEdges (PK: ProjectId, SourceAssetUniqueID, ...)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            ae.TargetAssetUniqueID,
                            ae.EdgeKind,
                            ae.SlotName,
                            ae.SlotIndex,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 0),
                            COALESCE(a.PathID, ae.TargetPathID),
                            COALESCE(sf.SerializedFileName, '')
                        FROM AssetEdges ae
                        LEFT JOIN Assets a
                          ON a.ProjectId = ae.ProjectId
                         AND a.UniqueID = ae.TargetAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE ae.ProjectId = @projectId
                          AND ae.SourceAssetUniqueID = @assetId
                          AND ae.TargetAssetUniqueID <> ''
                          AND ae.TargetAssetUniqueID <> @assetId
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var targetId = reader.GetString(0);
                        var edgeKind = reader.GetString(1);
                        var slotName = reader.GetString(2);
                        var slotIndex = reader.GetInt32(3);
                        var name = reader.GetString(4);
                        var type = (ClassIDType)reader.GetInt32(5);
                        var pathId = reader.GetInt64(6);
                        var fileName = reader.GetString(7);
                        referencesOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = targetId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"{type} ({pathId})" : name,
                            AssetType = type,
                            TypeName = AssetItem.GetCachedTypeString(type),
                            Relation = FormatEdgeRelation(edgeKind, slotName, slotIndex),
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.References
                        });
                    }
                }

                // 2) Incoming AssetEdges (idx_asset_edges_target_kind: ProjectId, TargetAssetUniqueID, ...)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            ae.SourceAssetUniqueID,
                            ae.EdgeKind,
                            ae.SlotName,
                            ae.SlotIndex,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 0),
                            COALESCE(a.PathID, ae.SourcePathID),
                            COALESCE(sf.SerializedFileName, '')
                        FROM AssetEdges ae
                        LEFT JOIN Assets a
                          ON a.ProjectId = ae.ProjectId
                         AND a.UniqueID = ae.SourceAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE ae.ProjectId = @projectId
                          AND ae.TargetAssetUniqueID = @assetId
                          AND ae.SourceAssetUniqueID <> ''
                          AND ae.SourceAssetUniqueID <> @assetId
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var sourceId = reader.GetString(0);
                        var edgeKind = reader.GetString(1);
                        var slotName = reader.GetString(2);
                        var slotIndex = reader.GetInt32(3);
                        var name = reader.GetString(4);
                        var type = (ClassIDType)reader.GetInt32(5);
                        var pathId = reader.GetInt64(6);
                        var fileName = reader.GetString(7);
                        referencedByOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = sourceId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"{type} ({pathId})" : name,
                            AssetType = type,
                            TypeName = AssetItem.GetCachedTypeString(type),
                            Relation = FormatEdgeRelation(edgeKind, slotName, slotIndex),
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.ReferencedBy
                        });
                    }
                }

                // 3) Material -> Texture2D (MaterialTextures PK) & Texture2D <- Material (MaterialCache idx_material_cache_main_tex)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mt.TextureAssetUniqueID,
                            mt.SlotName,
                            mt.IsMainTexture,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 28),
                            COALESCE(a.PathID, mt.TexturePathID),
                            COALESCE(sf.SerializedFileName, '')
                        FROM MaterialTextures mt
                        LEFT JOIN Assets a
                          ON a.ProjectId = mt.ProjectId
                         AND a.UniqueID = mt.TextureAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mt.ProjectId = @projectId
                          AND mt.MaterialAssetUniqueID = @assetId
                          AND mt.TextureAssetUniqueID <> ''
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var texId = reader.GetString(0);
                        var slot = reader.GetString(1);
                        var isMain = reader.GetInt32(2) != 0;
                        var name = reader.GetString(3);
                        var type = (ClassIDType)reader.GetInt32(4);
                        var pathId = reader.GetInt64(5);
                        var fileName = reader.GetString(6);
                        referencesOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = texId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"Texture2D ({pathId})" : name,
                            AssetType = type,
                            TypeName = AssetItem.GetCachedTypeString(type),
                            Relation = isMain && !string.IsNullOrWhiteSpace(slot) ? $"Main Texture ({slot})" : (!string.IsNullOrWhiteSpace(slot) ? $"Texture ({slot})" : "Texture"),
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.References
                        });
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mc.MaterialAssetUniqueID,
                            mc.MainTextureSlotName,
                            COALESCE(NULLIF(mc.MaterialName, ''), a.Name, ''),
                            COALESCE(a.Type, 21),
                            COALESCE(a.PathID, 0),
                            COALESCE(sf.SerializedFileName, '')
                        FROM MaterialCache mc
                        LEFT JOIN Assets a
                          ON a.ProjectId = mc.ProjectId
                         AND a.UniqueID = mc.MaterialAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mc.ProjectId = @projectId
                          AND mc.MainTextureAssetUniqueID = @assetId
                          AND mc.MaterialAssetUniqueID <> ''
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var matId = reader.GetString(0);
                        var slot = reader.GetString(1);
                        var name = reader.GetString(2);
                        var type = (ClassIDType)reader.GetInt32(3);
                        var pathId = reader.GetInt64(4);
                        var fileName = reader.GetString(5);
                        referencedByOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = matId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"Material ({pathId})" : name,
                            AssetType = type,
                            TypeName = AssetItem.GetCachedTypeString(type),
                            Relation = !string.IsNullOrWhiteSpace(slot) ? $"Material ({slot})" : "Material",
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.ReferencedBy
                        });
                    }
                }

                // 4) Mesh -> Materials (MeshMaterialResolvedCache PK) & Material <- Meshes (idx_mesh_mat_resolved_material)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mrc.MaterialAssetUniqueID,
                            mrc.SubMeshIndex,
                            COALESCE(NULLIF(mrc.MaterialName, ''), a.Name, ''),
                            COALESCE(a.Type, 21),
                            COALESCE(a.PathID, 0),
                            COALESCE(sf.SerializedFileName, '')
                        FROM MeshMaterialResolvedCache mrc
                        LEFT JOIN Assets a
                          ON a.ProjectId = mrc.ProjectId
                         AND a.UniqueID = mrc.MaterialAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mrc.ProjectId = @projectId
                          AND mrc.MeshAssetUniqueID = @assetId
                          AND mrc.MaterialAssetUniqueID <> ''
                        ORDER BY mrc.SubMeshIndex
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var matId = reader.GetString(0);
                        var subMesh = reader.GetInt32(1);
                        var name = reader.GetString(2);
                        var type = (ClassIDType)reader.GetInt32(3);
                        var pathId = reader.GetInt64(4);
                        var fileName = reader.GetString(5);
                        referencesOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = matId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"Material ({pathId})" : name,
                            AssetType = type,
                            TypeName = AssetItem.GetCachedTypeString(type),
                            Relation = $"SubMesh {subMesh} Material",
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.References
                        });
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mrc.MeshAssetUniqueID,
                            mrc.SubMeshIndex,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 43),
                            COALESCE(a.PathID, 0),
                            COALESCE(sf.SerializedFileName, '')
                        FROM MeshMaterialResolvedCache mrc
                        LEFT JOIN Assets a
                          ON a.ProjectId = mrc.ProjectId
                         AND a.UniqueID = mrc.MeshAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mrc.ProjectId = @projectId
                          AND mrc.MaterialAssetUniqueID = @assetId
                          AND mrc.MeshAssetUniqueID <> ''
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var meshId = reader.GetString(0);
                        var subMesh = reader.GetInt32(1);
                        var name = reader.GetString(2);
                        var type = (ClassIDType)reader.GetInt32(3);
                        var pathId = reader.GetInt64(4);
                        var fileName = reader.GetString(5);
                        referencedByOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = meshId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"Mesh ({pathId})" : name,
                            AssetType = type,
                            TypeName = AssetItem.GetCachedTypeString(type),
                            Relation = $"Mesh (SubMesh {subMesh})",
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.ReferencedBy
                        });
                    }
                }

                // 5) Mesh <- Renderers / GameObjects (MeshRenderers PK)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mr.RendererAssetUniqueID,
                            mr.RendererType,
                            COALESCE(mr.GameObjectAssetUniqueID, ''),
                            COALESCE(mr.GameObjectName, ''),
                            COALESCE(ra.Name, ''),
                            COALESCE(ra.Type, 0),
                            COALESCE(ra.PathID, 0),
                            COALESCE(ga.Name, ''),
                            COALESCE(ga.Type, 1),
                            COALESCE(ga.PathID, 0),
                            COALESCE(sf.SerializedFileName, '')
                        FROM MeshRenderers mr
                        LEFT JOIN Assets ra
                          ON ra.ProjectId = mr.ProjectId
                         AND ra.UniqueID = mr.RendererAssetUniqueID
                        LEFT JOIN Assets ga
                          ON ga.ProjectId = mr.ProjectId
                         AND COALESCE(mr.GameObjectAssetUniqueID, '') <> ''
                         AND ga.UniqueID = mr.GameObjectAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = ra.ProjectId
                         AND sf.Id = ra.SourceFileId
                        WHERE mr.ProjectId = @projectId
                          AND mr.MeshAssetUniqueID = @assetId
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var rendererId = reader.GetString(0);
                        var rendererType = reader.GetString(1);
                        var goId = reader.GetString(2);
                        var goName = reader.GetString(3);
                        var rName = reader.GetString(4);
                        var rType = (ClassIDType)reader.GetInt32(5);
                        var rPathId = reader.GetInt64(6);
                        var gaName = reader.GetString(7);
                        var gaType = (ClassIDType)reader.GetInt32(8);
                        var gaPathId = reader.GetInt64(9);
                        var fileName = reader.GetString(10);

                        if (!string.IsNullOrWhiteSpace(goId))
                        {
                            var effectiveGoName = !string.IsNullOrWhiteSpace(goName)
                                ? goName
                                : (!string.IsNullOrWhiteSpace(gaName) ? gaName : $"GameObject ({gaPathId})");
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = goId,
                                Name = effectiveGoName,
                                AssetType = gaType == ClassIDType.UnknownType ? ClassIDType.GameObject : gaType,
                                TypeName = AssetItem.GetCachedTypeString(gaType == ClassIDType.UnknownType ? ClassIDType.GameObject : gaType),
                                Relation = string.IsNullOrWhiteSpace(rendererType) ? "Renderer GameObject" : $"via {rendererType}",
                                PathId = gaPathId,
                                SerializedFileName = fileName,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }
                        else if (!string.IsNullOrWhiteSpace(rendererId))
                        {
                            var effectiveType = rType != ClassIDType.UnknownType ? rType : ClassIDType.MeshRenderer;
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = rendererId,
                                Name = !string.IsNullOrWhiteSpace(rName) ? rName : (!string.IsNullOrWhiteSpace(goName) ? goName : $"{rendererType} ({rPathId})"),
                                AssetType = effectiveType,
                                TypeName = string.IsNullOrWhiteSpace(rendererType) ? AssetItem.GetCachedTypeString(effectiveType) : rendererType,
                                Relation = "Mesh Renderer",
                                PathId = rPathId,
                                SerializedFileName = fileName,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }
                    }
                }

                // 6) Mesh <- ModelGroups / CompositeAssets (idx_composite_parts_mesh)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            ca.CompositeAssetUniqueID,
                            ca.DisplayName,
                            ca.CompositeKind,
                            ca.MeshPartCount,
                            ca.AvatarAssetUniqueID,
                            ca.AnimatorAssetUniqueID,
                            ca.RootGameObjectAssetUniqueID,
                            COALESCE(av.Name, ''),
                            COALESCE(av.PathID, 0),
                            COALESCE(an.Name, ''),
                            COALESCE(an.PathID, 0),
                            COALESCE(go.Name, ''),
                            COALESCE(go.PathID, 0)
                        FROM CompositeAssetParts cap
                        INNER JOIN CompositeAssets ca
                          ON ca.ProjectId = cap.ProjectId
                         AND ca.CompositeAssetUniqueID = cap.CompositeAssetUniqueID
                        LEFT JOIN Assets av
                          ON av.ProjectId = ca.ProjectId
                         AND ca.AvatarAssetUniqueID <> ''
                         AND av.UniqueID = ca.AvatarAssetUniqueID
                        LEFT JOIN Assets an
                          ON an.ProjectId = ca.ProjectId
                         AND ca.AnimatorAssetUniqueID <> ''
                         AND an.UniqueID = ca.AnimatorAssetUniqueID
                        LEFT JOIN Assets go
                          ON go.ProjectId = ca.ProjectId
                         AND ca.RootGameObjectAssetUniqueID <> ''
                         AND go.UniqueID = ca.RootGameObjectAssetUniqueID
                        WHERE cap.ProjectId = @projectId
                          AND cap.MeshAssetUniqueID = @assetId
                        LIMIT 64";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var displayName = reader.GetString(1);
                        var kind = reader.GetString(2);
                        var partCount = reader.GetInt32(3);
                        var avatarId = reader.GetString(4);
                        var animatorId = reader.GetString(5);
                        var rootGoId = reader.GetString(6);
                        var avName = reader.GetString(7);
                        var avPathId = reader.GetInt64(8);
                        var anName = reader.GetString(9);
                        var anPathId = reader.GetInt64(10);
                        var goName = reader.GetString(11);
                        var goPathId = reader.GetInt64(12);

                        var groupLabel = string.IsNullOrWhiteSpace(displayName)
                            ? $"{kind} ({partCount} parts)"
                            : $"ModelGroup: {displayName} ({partCount} parts)";

                        if (!string.IsNullOrWhiteSpace(avatarId))
                        {
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = avatarId,
                                Name = string.IsNullOrWhiteSpace(avName) ? displayName : avName,
                                AssetType = ClassIDType.Avatar,
                                TypeName = "Avatar",
                                Relation = groupLabel,
                                PathId = avPathId,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(animatorId))
                        {
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = animatorId,
                                Name = string.IsNullOrWhiteSpace(anName) ? displayName : anName,
                                AssetType = ClassIDType.Animator,
                                TypeName = "Animator",
                                Relation = groupLabel,
                                PathId = anPathId,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(rootGoId))
                        {
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = rootGoId,
                                Name = string.IsNullOrWhiteSpace(goName) ? displayName : goName,
                                AssetType = ClassIDType.GameObject,
                                TypeName = "GameObject",
                                Relation = groupLabel,
                                PathId = goPathId,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }
                    }
                }

                // 7) AnimationClip -> Target Avatars/Meshes/Animators (idx_anim_clip_targets_clip_score)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            t.AvatarAssetUniqueID,
                            t.MeshAssetUniqueID,
                            t.AnimatorAssetUniqueID,
                            t.RelationKind,
                            t.MatchScore,
                            COALESCE(av.Name, ''),
                            COALESCE(av.PathID, 0),
                            COALESCE(ms.Name, ''),
                            COALESCE(ms.PathID, 0),
                            COALESCE(an.Name, ''),
                            COALESCE(an.PathID, 0)
                        FROM AnimationClipTargets t
                        LEFT JOIN Assets av
                          ON av.ProjectId = t.ProjectId
                         AND t.AvatarAssetUniqueID <> ''
                         AND av.UniqueID = t.AvatarAssetUniqueID
                        LEFT JOIN Assets ms
                          ON ms.ProjectId = t.ProjectId
                         AND t.MeshAssetUniqueID <> ''
                         AND ms.UniqueID = t.MeshAssetUniqueID
                        LEFT JOIN Assets an
                          ON an.ProjectId = t.ProjectId
                         AND t.AnimatorAssetUniqueID <> ''
                         AND an.UniqueID = t.AnimatorAssetUniqueID
                        WHERE t.ProjectId = @projectId
                          AND t.ClipAssetUniqueID = @assetId
                        ORDER BY t.MatchScore DESC
                        LIMIT 64";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var avatarId = reader.GetString(0);
                        var meshId = reader.GetString(1);
                        var animatorId = reader.GetString(2);
                        var relKind = reader.GetString(3);
                        var score = reader.GetInt32(4);
                        var avName = reader.GetString(5);
                        var avPathId = reader.GetInt64(6);
                        var msName = reader.GetString(7);
                        var msPathId = reader.GetInt64(8);
                        var anName = reader.GetString(9);
                        var anPathId = reader.GetInt64(10);
                        var suffix = score > 0 ? $" ({relKind}, {score})" : (string.IsNullOrWhiteSpace(relKind) ? string.Empty : $" ({relKind})");

                        if (!string.IsNullOrWhiteSpace(avatarId))
                        {
                            referencesOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = avatarId,
                                Name = string.IsNullOrWhiteSpace(avName) ? $"Avatar ({avPathId})" : avName,
                                AssetType = ClassIDType.Avatar,
                                TypeName = "Avatar",
                                Relation = $"Target Avatar{suffix}",
                                PathId = avPathId,
                                Direction = AssetReferenceDirection.References
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(meshId))
                        {
                            referencesOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = meshId,
                                Name = string.IsNullOrWhiteSpace(msName) ? $"Mesh ({msPathId})" : msName,
                                AssetType = ClassIDType.Mesh,
                                TypeName = "Mesh",
                                Relation = $"Target Mesh{suffix}",
                                PathId = msPathId,
                                Direction = AssetReferenceDirection.References
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(animatorId))
                        {
                            referencesOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = animatorId,
                                Name = string.IsNullOrWhiteSpace(anName) ? $"Animator ({anPathId})" : anName,
                                AssetType = ClassIDType.Animator,
                                TypeName = "Animator",
                                Relation = $"Target Animator{suffix}",
                                PathId = anPathId,
                                Direction = AssetReferenceDirection.References
                            });
                        }
                    }
                }

                // 8) Avatar / Mesh <- AnimationClips (idx_anim_clip_targets_avatar & idx_anim_clip_targets_mesh)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            t.ClipAssetUniqueID,
                            t.RelationKind,
                            t.MatchScore,
                            COALESCE(c.Name, ''),
                            COALESCE(c.PathID, 0),
                            COALESCE(sf.SerializedFileName, '')
                        FROM AnimationClipTargets t
                        LEFT JOIN Assets c
                          ON c.ProjectId = t.ProjectId
                         AND c.UniqueID = t.ClipAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = c.ProjectId
                         AND sf.Id = c.SourceFileId
                        WHERE t.ProjectId = @projectId
                          AND t.AvatarAssetUniqueID = @assetId
                        ORDER BY t.MatchScore DESC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var clipId = reader.GetString(0);
                        var relKind = reader.GetString(1);
                        var score = reader.GetInt32(2);
                        var name = reader.GetString(3);
                        var pathId = reader.GetInt64(4);
                        var fileName = reader.GetString(5);
                        referencedByOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = clipId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"AnimationClip ({pathId})" : name,
                            AssetType = ClassIDType.AnimationClip,
                            TypeName = "AnimationClip",
                            Relation = score > 0 ? $"Animation ({relKind}, {score})" : "AnimationClip",
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.ReferencedBy
                        });
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            t.ClipAssetUniqueID,
                            t.RelationKind,
                            t.MatchScore,
                            COALESCE(c.Name, ''),
                            COALESCE(c.PathID, 0),
                            COALESCE(sf.SerializedFileName, '')
                        FROM AnimationClipTargets t
                        LEFT JOIN Assets c
                          ON c.ProjectId = t.ProjectId
                         AND c.UniqueID = t.ClipAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = c.ProjectId
                         AND sf.Id = c.SourceFileId
                        WHERE t.ProjectId = @projectId
                          AND t.MeshAssetUniqueID = @assetId
                        ORDER BY t.MatchScore DESC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@limit", maxPerDirection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var clipId = reader.GetString(0);
                        var relKind = reader.GetString(1);
                        var score = reader.GetInt32(2);
                        var name = reader.GetString(3);
                        var pathId = reader.GetInt64(4);
                        var fileName = reader.GetString(5);
                        referencedByOut.Add(new AssetReferenceItem
                        {
                            AssetUniqueId = clipId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"AnimationClip ({pathId})" : name,
                            AssetType = ClassIDType.AnimationClip,
                            TypeName = "AnimationClip",
                            Relation = score > 0 ? $"Animation ({relKind}, {score})" : "AnimationClip",
                            PathId = pathId,
                            SerializedFileName = fileName,
                            Direction = AssetReferenceDirection.ReferencedBy
                        });
                    }
                }

                // 9) Avatar -> Meshes & Animators via CompositeAssets (idx_composite_assets_avatar)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            ca.DisplayName,
                            ca.MeshPartCount,
                            ca.AnimatorAssetUniqueID,
                            ca.RootGameObjectAssetUniqueID,
                            cap.MeshAssetUniqueID,
                            COALESCE(m.Name, ''),
                            COALESCE(m.PathID, 0),
                            COALESCE(an.Name, ''),
                            COALESCE(an.PathID, 0),
                            COALESCE(go.Name, ''),
                            COALESCE(go.PathID, 0)
                        FROM CompositeAssets ca
                        LEFT JOIN CompositeAssetParts cap
                          ON cap.ProjectId = ca.ProjectId
                         AND cap.CompositeAssetUniqueID = ca.CompositeAssetUniqueID
                        LEFT JOIN Assets m
                          ON m.ProjectId = cap.ProjectId
                         AND cap.MeshAssetUniqueID <> ''
                         AND m.UniqueID = cap.MeshAssetUniqueID
                        LEFT JOIN Assets an
                          ON an.ProjectId = ca.ProjectId
                         AND ca.AnimatorAssetUniqueID <> ''
                         AND an.UniqueID = ca.AnimatorAssetUniqueID
                        LEFT JOIN Assets go
                          ON go.ProjectId = ca.ProjectId
                         AND ca.RootGameObjectAssetUniqueID <> ''
                         AND go.UniqueID = ca.RootGameObjectAssetUniqueID
                        WHERE ca.ProjectId = @projectId
                          AND ca.AvatarAssetUniqueID = @assetId
                        LIMIT 128";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@assetId", assetUniqueId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var displayName = reader.GetString(0);
                        var partCount = reader.GetInt32(1);
                        var animatorId = reader.GetString(2);
                        var rootGoId = reader.GetString(3);
                        var meshId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                        var mName = reader.GetString(5);
                        var mPathId = reader.GetInt64(6);
                        var anName = reader.GetString(7);
                        var anPathId = reader.GetInt64(8);
                        var goName = reader.GetString(9);
                        var goPathId = reader.GetInt64(10);

                        if (!string.IsNullOrWhiteSpace(meshId))
                        {
                            referencesOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = meshId,
                                Name = string.IsNullOrWhiteSpace(mName) ? $"Mesh ({mPathId})" : mName,
                                AssetType = ClassIDType.Mesh,
                                TypeName = "Mesh",
                                Relation = $"ModelGroup Part ({displayName})",
                                PathId = mPathId,
                                Direction = AssetReferenceDirection.References
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(animatorId))
                        {
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = animatorId,
                                Name = string.IsNullOrWhiteSpace(anName) ? displayName : anName,
                                AssetType = ClassIDType.Animator,
                                TypeName = "Animator",
                                Relation = $"Animator ({partCount} parts)",
                                PathId = anPathId,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(rootGoId))
                        {
                            referencedByOut.Add(new AssetReferenceItem
                            {
                                AssetUniqueId = rootGoId,
                                Name = string.IsNullOrWhiteSpace(goName) ? displayName : goName,
                                AssetType = ClassIDType.GameObject,
                                TypeName = "GameObject",
                                Relation = $"ModelGroup Root ({displayName})",
                                PathId = goPathId,
                                Direction = AssetReferenceDirection.ReferencedBy
                            });
                        }
                    }
                }
            });
        }

        private static string FormatEdgeRelation(string edgeKind, string slotName, int slotIndex)
        {
            var hasSlot = !string.IsNullOrWhiteSpace(slotName);
            if (hasSlot && slotIndex >= 0)
            {
                return $"{edgeKind} ({slotName}[{slotIndex.ToString(CultureInfo.InvariantCulture)}])";
            }

            if (hasSlot)
            {
                return string.Equals(edgeKind, slotName, StringComparison.OrdinalIgnoreCase)
                    ? edgeKind
                    : $"{edgeKind} ({slotName})";
            }

            if (slotIndex >= 0)
            {
                return $"{edgeKind} [{slotIndex.ToString(CultureInfo.InvariantCulture)}]";
            }

            return string.IsNullOrWhiteSpace(edgeKind) ? "Reference" : edgeKind;
        }
    }
}
