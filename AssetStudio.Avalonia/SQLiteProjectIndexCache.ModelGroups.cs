using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        internal List<ModelGroupInfo> LoadModelGroupsForAvatarAssetId(string folderPath, string signature, string avatarAssetId)
        {
            var result = new List<ModelGroupInfo>();
            if (string.IsNullOrWhiteSpace(avatarAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "model groups", (conn, transaction, projectId) =>
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
                      AND AvatarAssetUniqueID = @avatarAssetId
                    ORDER BY Confidence DESC, GroupName, GroupAssetUniqueID";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@avatarAssetId", avatarAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(ReadModelGroupInfo(reader));
                }
            });

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

            ExecuteProjectReadQuery(folderPath, signature, "model group candidates", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);

                var candidatesById = new Dictionary<string, ModelGroupCandidateInfo>(StringComparer.Ordinal);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var groupId = reader.GetString(0);
                    if (!candidatesById.TryGetValue(groupId, out var candidate))
                    {
                        var group = ReadModelGroupInfo(reader);
                        candidate = new ModelGroupCandidateInfo(group);
                        candidatesById.Add(groupId, candidate);
                        result.Add(candidate);
                    }

                    candidate.Meshes.Add(ReadModelGroupMeshInfo(reader, groupId, meshStartOrdinal: 11));
                }
            });

            return result;
        }

        internal List<ModelGroupInfo> LoadModelGroupsForMeshAssetId(string folderPath, string signature, string meshAssetId)
        {
            var result = new List<ModelGroupInfo>();
            if (string.IsNullOrWhiteSpace(meshAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "model groups for mesh", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@meshAssetId", meshAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(ReadModelGroupInfo(reader));
                }
            });

            return result;
        }

        internal List<ModelGroupMeshInfo> LoadModelGroupMeshes(string folderPath, string signature, string groupAssetId)
        {
            var result = new List<ModelGroupMeshInfo>();
            if (string.IsNullOrWhiteSpace(groupAssetId))
            {
                return result;
            }

            ExecuteProjectReadQuery(folderPath, signature, "model group meshes", (conn, transaction, projectId) =>
            {
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
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@groupAssetId", groupAssetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(ReadModelGroupMeshInfo(reader, reader.GetString(0), meshStartOrdinal: 1));
                }
            });

            return result;
        }

        private static ModelGroupInfo ReadModelGroupInfo(SqliteDataReader reader)
        {
            return new ModelGroupInfo(
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
                reader.GetString(10));
        }

        private static ModelGroupMeshInfo ReadModelGroupMeshInfo(SqliteDataReader reader, string groupId, int meshStartOrdinal)
        {
            return new ModelGroupMeshInfo(
                groupId,
                reader.GetString(meshStartOrdinal),
                reader.GetString(meshStartOrdinal + 1),
                reader.GetString(meshStartOrdinal + 2),
                reader.GetString(meshStartOrdinal + 3),
                reader.GetString(meshStartOrdinal + 4),
                reader.GetInt32(meshStartOrdinal + 5),
                reader.GetInt32(meshStartOrdinal + 6),
                reader.GetString(meshStartOrdinal + 7),
                ReadTransformMatrix(reader, meshStartOrdinal + 8),
                reader.GetString(meshStartOrdinal + 9),
                reader.GetInt64(meshStartOrdinal + 10),
                reader.GetInt64(meshStartOrdinal + 11),
                reader.GetInt32(meshStartOrdinal + 12));
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
    }
}
