using System;
using System.Collections.Generic;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia.Services;

internal sealed class AvatarMeshSkeletonData
{
    public required int MeshBoneCount { get; init; }
    public required global::OpenTK.Mathematics.Vector3[] RestBonePositions { get; init; }
    public required global::OpenTK.Mathematics.Matrix4[] BindPoseInverses { get; init; }
    public required Dictionary<uint, int> MeshBoneHashToIdx { get; init; }
    public required int[] MeshBoneToSkelNode { get; init; }
    public required string[] MeshBoneNames { get; init; }
    public required int[] MeshParentIndices { get; init; }
}

/// <summary>
/// Pure stateless skeleton construction, bind-pose inversion, bone path alias indexing,
/// and skin-weight mask utilities for 3D Avatar and AnimationClip previews.
/// </summary>
internal static class MeshPreviewSkeletonBuilder
{
    public static global::OpenTK.Mathematics.Vector3 GetBindPosePosition(AssetStudio.Matrix4x4 bindPose)
    {
        var otkMat = new global::OpenTK.Mathematics.Matrix4(
            bindPose.M00, bindPose.M01, bindPose.M02, bindPose.M03,
            bindPose.M10, bindPose.M11, bindPose.M12, bindPose.M13,
            bindPose.M20, bindPose.M21, bindPose.M22, bindPose.M23,
            bindPose.M30, bindPose.M31, bindPose.M32, bindPose.M33);
        try
        {
            return otkMat.Inverted().ExtractTranslation();
        }
        catch
        {
            return global::OpenTK.Mathematics.Vector3.Zero;
        }
    }

    public static global::OpenTK.Mathematics.Matrix4[] BuildBindPoseInverses(
        AssetStudio.Matrix4x4[] bindPoses,
        int boneCount,
        out global::OpenTK.Mathematics.Vector3[] restBonePositions)
    {
        var bindPoseInverses = new global::OpenTK.Mathematics.Matrix4[boneCount];
        restBonePositions = new global::OpenTK.Mathematics.Vector3[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            var bp = bindPoses[i];
            var otkMat = new global::OpenTK.Mathematics.Matrix4(
                bp.M00, bp.M01, bp.M02, bp.M03,
                bp.M10, bp.M11, bp.M12, bp.M13,
                bp.M20, bp.M21, bp.M22, bp.M23,
                bp.M30, bp.M31, bp.M32, bp.M33);
            try
            {
                bindPoseInverses[i] = otkMat.Inverted();
                restBonePositions[i] = bindPoseInverses[i].ExtractTranslation();
            }
            catch
            {
                bindPoseInverses[i] = global::OpenTK.Mathematics.Matrix4.Identity;
                restBonePositions[i] = global::OpenTK.Mathematics.Vector3.Zero;
            }
        }

        return bindPoseInverses;
    }

    public static bool TryBuildAvatarMeshSkeleton(
        Avatar avatar,
        Mesh mesh,
        out AvatarMeshSkeletonData skeleton)
    {
        skeleton = null!;
        if (mesh.m_BindPose == null
            || mesh.m_BindPose.Length == 0
            || mesh.m_BoneNameHashes == null
            || mesh.m_BoneNameHashes.Length == 0
            || avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            return false;
        }

        var meshBoneCount = mesh.m_BindPose.Length;
        var nodes = avatar.m_Avatar.m_AvatarSkeleton.m_Node;
        var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
        var skelCount = nodes.Length;

        var bindPoseInverses = BuildBindPoseInverses(mesh.m_BindPose, meshBoneCount, out var restBonePositions);

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        for (var j = 0; j < mesh.m_BoneNameHashes.Length; j++)
        {
            meshBoneHashToIdx[mesh.m_BoneNameHashes[j]] = j;
        }

        var skelNodeToMeshBone = new int[skelCount];
        for (var i = 0; i < skelCount; i++)
        {
            skelNodeToMeshBone[i] = -1;
            if (skelIds != null && i < skelIds.Length && meshBoneHashToIdx.TryGetValue(skelIds[i], out var mbIdx))
            {
                skelNodeToMeshBone[i] = mbIdx;
            }
        }

        var meshBoneToSkelNode = new int[meshBoneCount];
        Array.Fill(meshBoneToSkelNode, -1);
        for (var i = 0; i < skelCount; i++)
        {
            if (skelNodeToMeshBone[i] >= 0)
            {
                meshBoneToSkelNode[skelNodeToMeshBone[i]] = i;
            }
        }

        var meshBoneNames = new string[meshBoneCount];
        for (var mb = 0; mb < meshBoneCount; mb++)
        {
            var skelIdx = meshBoneToSkelNode[mb];
            if (skelIds != null && skelIdx >= 0 && skelIdx < skelIds.Length)
            {
                meshBoneNames[mb] = avatar.FindBonePath(skelIds[skelIdx]) ?? string.Empty;
            }
            else
            {
                meshBoneNames[mb] = string.Empty;
            }
        }

        var meshParentIndices = new int[meshBoneCount];
        for (var mb = 0; mb < meshBoneCount; mb++)
        {
            meshParentIndices[mb] = -1;
            var skelIdx = meshBoneToSkelNode[mb];
            if (skelIdx < 0)
            {
                continue;
            }

            var current = nodes[skelIdx].m_ParentId;
            while (current >= 0 && current < skelCount)
            {
                if (skelNodeToMeshBone[current] >= 0)
                {
                    meshParentIndices[mb] = skelNodeToMeshBone[current];
                    break;
                }

                current = nodes[current].m_ParentId;
            }
        }

        skeleton = new AvatarMeshSkeletonData
        {
            MeshBoneCount = meshBoneCount,
            RestBonePositions = restBonePositions,
            BindPoseInverses = bindPoseInverses,
            MeshBoneHashToIdx = meshBoneHashToIdx,
            MeshBoneToSkelNode = meshBoneToSkelNode,
            MeshBoneNames = meshBoneNames,
            MeshParentIndices = meshParentIndices
        };
        return true;
    }

    public static string NormalizeAnimationPath(string? path)
    {
        return (path ?? string.Empty)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Trim('/');
    }

    public static void AddBonePathAlias(Dictionary<string, int> bonePathToIndex, string? path, int meshBoneIdx)
    {
        path = NormalizeAnimationPath(path);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start < parts.Length; start++)
        {
            var alias = string.Join("/", parts.Skip(start));
            if (!bonePathToIndex.TryGetValue(alias, out var existing))
            {
                bonePathToIndex[alias] = meshBoneIdx;
            }
            else if (existing != meshBoneIdx)
            {
                bonePathToIndex[alias] = -1;
            }
        }
    }

    public static Dictionary<string, int> BuildBonePathAliasMap(string[] meshBoneNames, int meshBoneCount)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = Math.Min(meshBoneNames.Length, meshBoneCount);
        for (var i = 0; i < count; i++)
        {
            AddBonePathAlias(map, meshBoneNames[i], i);
        }

        return map;
    }

    public static bool TryResolvePathAlias(Dictionary<string, int> bonePathToIndex, string? path, out int meshBoneIdx)
    {
        path = NormalizeAnimationPath(path);
        if (!string.IsNullOrEmpty(path))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var start = 0; start < parts.Length; start++)
            {
                var alias = string.Join("/", parts.Skip(start));
                if (bonePathToIndex.TryGetValue(alias, out meshBoneIdx) && meshBoneIdx >= 0)
                {
                    return true;
                }
            }
        }

        meshBoneIdx = -1;
        return false;
    }

    public static global::OpenTK.Mathematics.Vector3 ToOtkVector3(AssetStudio.Vector3 value)
    {
        return new global::OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
    }

    public static global::OpenTK.Mathematics.Quaternion ToOtkQuaternion(AssetStudio.Quaternion value)
    {
        var q = new global::OpenTK.Mathematics.Quaternion(value.X, value.Y, value.Z, value.W);
        if (q.LengthSquared > 0)
        {
            q.Normalize();
        }

        return q;
    }

    public static global::OpenTK.Mathematics.Matrix4 CreateLocalMatrix(
        global::OpenTK.Mathematics.Vector3 position,
        global::OpenTK.Mathematics.Quaternion rotation,
        global::OpenTK.Mathematics.Vector3 scale)
    {
        return global::OpenTK.Mathematics.Matrix4.CreateScale(scale)
            * global::OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rotation)
            * global::OpenTK.Mathematics.Matrix4.CreateTranslation(position);
    }

    public static bool IsFiniteVector(global::OpenTK.Mathematics.Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    public static bool IsAuxiliaryAnimationBone(string? path)
    {
        var normalized = NormalizeAnimationPath(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "ik" || segment.EndsWith("_ik", StringComparison.Ordinal) || segment.StartsWith("ik_", StringComparison.Ordinal)
                || segment.Contains("effector", StringComparison.Ordinal)
                || segment.Contains("target", StringComparison.Ordinal)
                || segment.Contains("pole", StringComparison.Ordinal)
                || segment.Contains("hint", StringComparison.Ordinal)
                || segment.Contains("constraint", StringComparison.Ordinal)
                || segment.Contains("locator", StringComparison.Ordinal)
                || segment.Contains("dummy", StringComparison.Ordinal)
                || segment.Contains("helper", StringComparison.Ordinal)
                || segment.Contains("control", StringComparison.Ordinal)
                || segment.Contains("ctrl", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static void BuildSkinWeightMasks(
        Mesh mesh,
        int meshBoneCount,
        int[] meshParentIndices,
        out bool[] weightedBoneMask,
        out bool[] deformChainMask)
    {
        weightedBoneMask = new bool[meshBoneCount];
        if (mesh.m_Skin != null)
        {
            foreach (var skin in mesh.m_Skin)
            {
                if (skin?.boneIndex == null || skin.weight == null)
                {
                    continue;
                }

                for (var i = 0; i < Math.Min(skin.boneIndex.Length, skin.weight.Length); i++)
                {
                    var boneIdx = skin.boneIndex[i];
                    if (skin.weight[i] > 0.0001f && boneIdx >= 0 && boneIdx < meshBoneCount)
                    {
                        weightedBoneMask[boneIdx] = true;
                    }
                }
            }
        }

        var hasWeightedBones = weightedBoneMask.Any(x => x);
        deformChainMask = new bool[meshBoneCount];
        if (hasWeightedBones)
        {
            for (var i = 0; i < meshBoneCount; i++)
            {
                if (!weightedBoneMask[i])
                {
                    continue;
                }

                var current = i;
                while (current >= 0 && current < meshBoneCount && !deformChainMask[current])
                {
                    deformChainMask[current] = true;
                    current = meshParentIndices[current];
                }
            }
        }
        else
        {
            Array.Fill(deformChainMask, true);
        }
    }

    public static bool ShouldUseAnimationBinding(
        int meshBoneIdx,
        int meshBoneCount,
        bool[] deformChainMask,
        bool[] weightedBoneMask,
        string[] meshBoneNames)
    {
        if (meshBoneIdx < 0 || meshBoneIdx >= meshBoneCount)
        {
            return false;
        }

        if (!deformChainMask[meshBoneIdx])
        {
            return false;
        }

        return weightedBoneMask[meshBoneIdx]
            || !IsAuxiliaryAnimationBone(meshBoneIdx < meshBoneNames.Length ? meshBoneNames[meshBoneIdx] : null);
    }
}
