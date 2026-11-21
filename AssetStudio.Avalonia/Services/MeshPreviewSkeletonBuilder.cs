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
    public int AvatarNodeCount { get; init; }
    public int[] AvatarParentIndices { get; init; } = Array.Empty<int>();
    public string[] AvatarNodeNames { get; init; } = Array.Empty<string>();
    public Dictionary<uint, int> AvatarNodeHashToIdx { get; init; } = new();
    public int[] SkelNodeToMeshBone { get; init; } = Array.Empty<int>();
}

internal sealed class AvatarDefaultSkeletonData
{
    public required int NodeCount { get; init; }
    public required int[] ParentIndices { get; init; }
    public required string[] NodeNames { get; init; }
    public required Dictionary<uint, int> NodeHashToIdx { get; init; }
    public required Dictionary<string, int> NodePathToIdx { get; init; }
    public required Dictionary<uint, int> DirectBindingPathToNodeIdx { get; init; }
    public required global::OpenTK.Mathematics.Vector3[] RestLocalPositions { get; init; }
    public required global::OpenTK.Mathematics.Quaternion[] RestLocalRotations { get; init; }
    public required global::OpenTK.Mathematics.Vector3[] RestLocalScales { get; init; }
    public required global::OpenTK.Mathematics.Matrix4[] RestLocalMatrices { get; init; }
    public required global::OpenTK.Mathematics.Matrix4[] RestWorldMatrices { get; init; }
    public required global::OpenTK.Mathematics.Vector3[] RestWorldPositions { get; init; }

    public bool TryResolveBindingNode(Avatar? avatar, GenericBinding binding, out int nodeIdx)
    {
        if (DirectBindingPathToNodeIdx.TryGetValue(binding.path, out nodeIdx))
        {
            return nodeIdx >= 0;
        }

        if (avatar != null)
        {
            var path = avatar.FindBonePath(binding.path);
            if (!string.IsNullOrEmpty(path)
                && MeshPreviewSkeletonBuilder.TryResolvePathAlias(NodePathToIdx, path, out nodeIdx))
            {
                return true;
            }
        }

        return NodeHashToIdx.TryGetValue(binding.path, out nodeIdx);
    }

    public bool TryResolvePathNode(string? path, out int nodeIdx)
    {
        return MeshPreviewSkeletonBuilder.TryResolvePathAlias(NodePathToIdx, path, out nodeIdx);
    }
}

/// <summary>
/// Pure stateless skeleton construction, bind-pose inversion, bone path alias indexing,
/// and skin-weight mask utilities for 3D Avatar and AnimationClip previews.
/// </summary>
internal static class MeshPreviewSkeletonBuilder
{
    public static bool TryBuildAvatarSkeletonFromDefaultPose(
        Avatar avatar,
        out AvatarDefaultSkeletonData skeleton)
    {
        skeleton = null!;
        var avatarConstant = avatar?.m_Avatar;
        var nodes = avatarConstant?.m_AvatarSkeleton?.m_Node;
        if (avatarConstant == null || nodes == null || nodes.Length == 0)
        {
            return false;
        }

        var pose = avatarConstant.m_DefaultPose?.m_X is { Length: > 0 } defaultPose
            ? defaultPose
            : avatarConstant.m_AvatarSkeletonPose?.m_X;
        if (pose == null || pose.Length == 0)
        {
            return false;
        }

        var skelCount = nodes.Length;
        var skelIds = avatarConstant.m_AvatarSkeleton.m_ID;
        var tosByHash = new Dictionary<uint, string>(avatar!.m_TOS?.Length ?? 0);
        if (avatar.m_TOS != null)
        {
            foreach (var pair in avatar.m_TOS)
            {
                if (pair.Key != 0 && !string.IsNullOrEmpty(pair.Value))
                {
                    tosByHash[pair.Key] = pair.Value;
                }
            }
        }

        var parentIndices = new int[skelCount];
        var nodeNames = new string[skelCount];
        var nodeHashToIdx = new Dictionary<uint, int>(skelCount);
        for (var i = 0; i < skelCount; i++)
        {
            parentIndices[i] = nodes[i].m_ParentId;
            if (skelIds != null && i < skelIds.Length)
            {
                var id = skelIds[i];
                nodeHashToIdx[id] = i;
                nodeNames[i] = tosByHash.TryGetValue(id, out var path) ? path : string.Empty;
            }
            else
            {
                nodeNames[i] = string.Empty;
            }
        }

        var nodePathToIdx = BuildBonePathAliasMap(nodeNames, skelCount);
        var directBindingPathToNodeIdx = new Dictionary<uint, int>(tosByHash.Count + nodeHashToIdx.Count);
        foreach (var kvp in nodeHashToIdx)
        {
            directBindingPathToNodeIdx[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in tosByHash)
        {
            if (directBindingPathToNodeIdx.ContainsKey(kvp.Key))
            {
                continue;
            }

            if (TryResolvePathAlias(nodePathToIdx, kvp.Value, out var resolvedIdx))
            {
                directBindingPathToNodeIdx[kvp.Key] = resolvedIdx;
            }
        }

        var restLocalPositions = new global::OpenTK.Mathematics.Vector3[skelCount];
        var restLocalRotations = new global::OpenTK.Mathematics.Quaternion[skelCount];
        var restLocalScales = new global::OpenTK.Mathematics.Vector3[skelCount];
        var restLocalMatrices = new global::OpenTK.Mathematics.Matrix4[skelCount];
        for (var i = 0; i < skelCount; i++)
        {
            if (i < pose.Length && pose[i] != null)
            {
                var xf = pose[i];
                var pos = ToOtkVector3(xf.t);
                if (!IsFiniteVector(pos))
                {
                    pos = global::OpenTK.Mathematics.Vector3.Zero;
                }

                var rot = ToOtkQuaternion(xf.q);
                if (!float.IsFinite(rot.X) || !float.IsFinite(rot.Y) || !float.IsFinite(rot.Z) || !float.IsFinite(rot.W) || rot.LengthSquared <= 0f)
                {
                    rot = global::OpenTK.Mathematics.Quaternion.Identity;
                }

                var scale = ToOtkVector3(xf.s);
                if (!IsFiniteVector(scale))
                {
                    scale = global::OpenTK.Mathematics.Vector3.One;
                }

                restLocalPositions[i] = pos;
                restLocalRotations[i] = rot;
                restLocalScales[i] = scale;
                restLocalMatrices[i] = CreateLocalMatrix(pos, rot, scale);
            }
            else
            {
                restLocalPositions[i] = global::OpenTK.Mathematics.Vector3.Zero;
                restLocalRotations[i] = global::OpenTK.Mathematics.Quaternion.Identity;
                restLocalScales[i] = global::OpenTK.Mathematics.Vector3.One;
                restLocalMatrices[i] = global::OpenTK.Mathematics.Matrix4.Identity;
            }
        }

        var cachedWorlds = new global::OpenTK.Mathematics.Matrix4?[skelCount];
        var visiting = new bool[skelCount];
        global::OpenTK.Mathematics.Matrix4 GetRestWorld(int idx)
        {
            if (cachedWorlds[idx] is global::OpenTK.Mathematics.Matrix4 cached)
            {
                return cached;
            }

            if (visiting[idx])
            {
                return restLocalMatrices[idx];
            }

            visiting[idx] = true;
            var parent = parentIndices[idx];
            var world = parent >= 0 && parent < skelCount && parent != idx
                ? restLocalMatrices[idx] * GetRestWorld(parent)
                : restLocalMatrices[idx];
            visiting[idx] = false;
            cachedWorlds[idx] = world;
            return world;
        }

        var restWorldMatrices = new global::OpenTK.Mathematics.Matrix4[skelCount];
        var restWorldPositions = new global::OpenTK.Mathematics.Vector3[skelCount];
        var finiteCount = 0;
        for (var i = 0; i < skelCount; i++)
        {
            var world = GetRestWorld(i);
            restWorldMatrices[i] = world;
            var pos = world.ExtractTranslation();
            if (IsFiniteVector(pos))
            {
                restWorldPositions[i] = pos;
                finiteCount++;
            }
            else
            {
                restWorldPositions[i] = global::OpenTK.Mathematics.Vector3.Zero;
            }
        }

        if (finiteCount == 0)
        {
            return false;
        }

        skeleton = new AvatarDefaultSkeletonData
        {
            NodeCount = skelCount,
            ParentIndices = parentIndices,
            NodeNames = nodeNames,
            NodeHashToIdx = nodeHashToIdx,
            NodePathToIdx = nodePathToIdx,
            DirectBindingPathToNodeIdx = directBindingPathToNodeIdx,
            RestLocalPositions = restLocalPositions,
            RestLocalRotations = restLocalRotations,
            RestLocalScales = restLocalScales,
            RestLocalMatrices = restLocalMatrices,
            RestWorldMatrices = restWorldMatrices,
            RestWorldPositions = restWorldPositions
        };
        return true;
    }

    public static bool TryConvertRowMajorTransformMatrix(
        float[]? matrix,
        out global::OpenTK.Mathematics.Matrix4 result)
    {
        result = global::OpenTK.Mathematics.Matrix4.Identity;
        if (matrix == null || matrix.Length != 16)
        {
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            if (!float.IsFinite(matrix[i]))
            {
                return false;
            }
        }

        // ModelGroupMeshInfo.TransformMatrix stores column-vector row-major entries (translation in [3, 7, 11]).
        // Transpose into OpenTK row-vector Matrix4 (translation in row 3 = M41, M42, M43).
        result = new global::OpenTK.Mathematics.Matrix4(
            matrix[0], matrix[4], matrix[8], matrix[12],
            matrix[1], matrix[5], matrix[9], matrix[13],
            matrix[2], matrix[6], matrix[10], matrix[14],
            matrix[3], matrix[7], matrix[11], matrix[15]);
        return true;
    }

    public static int[] MapSkinnedMeshBonesToAvatarNodes(
        Mesh mesh,
        Avatar? avatar,
        AvatarDefaultSkeletonData avatarSkeleton,
        IReadOnlyList<string?>? rendererBonePaths = null)
    {
        var boneCount = mesh.m_BindPose?.Length ?? 0;
        if (boneCount <= 0)
        {
            return Array.Empty<int>();
        }

        var map = new int[boneCount];
        Array.Fill(map, -1);

        var hashes = mesh.m_BoneNameHashes;
        for (var b = 0; b < boneCount; b++)
        {
            if (hashes != null && b < hashes.Length)
            {
                var hash = hashes[b];
                if (avatarSkeleton.DirectBindingPathToNodeIdx.TryGetValue(hash, out var byDirect) && byDirect >= 0)
                {
                    map[b] = byDirect;
                    continue;
                }

                if (avatarSkeleton.NodeHashToIdx.TryGetValue(hash, out var byHash) && byHash >= 0)
                {
                    map[b] = byHash;
                    continue;
                }

                if (avatar != null)
                {
                    var bonePath = avatar.FindBonePath(hash);
                    if (!string.IsNullOrEmpty(bonePath)
                        && TryResolvePathAlias(avatarSkeleton.NodePathToIdx, bonePath, out var byTosAlias))
                    {
                        map[b] = byTosAlias;
                        continue;
                    }
                }
            }

            if (rendererBonePaths != null && b < rendererBonePaths.Count)
            {
                var rendererPath = rendererBonePaths[b];
                if (!string.IsNullOrWhiteSpace(rendererPath)
                    && TryResolvePathAlias(avatarSkeleton.NodePathToIdx, rendererPath, out var byRendererPath))
                {
                    map[b] = byRendererPath;
                }
            }
        }

        return map;
    }

    public static bool TryResolveRigidPartAvatarNode(
        AvatarDefaultSkeletonData avatarSkeleton,
        string? relativeTransformPath,
        string? gameObjectName,
        string? meshName,
        out int nodeIdx,
        out bool isExactNodeMatch)
    {
        nodeIdx = -1;
        isExactNodeMatch = false;

        var normalizedRelative = NormalizeAnimationPath(relativeTransformPath);
        if (!string.IsNullOrEmpty(normalizedRelative))
        {
            if (TryResolvePathAlias(avatarSkeleton.NodePathToIdx, normalizedRelative, out nodeIdx))
            {
                isExactNodeMatch = true;
                return true;
            }

            // Walk ancestor prefixes (e.g. "Hips/Spine/Hand_R/SwordProp" -> "Hips/Spine/Hand_R")
            var segments = normalizedRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var len = segments.Length - 1; len >= 1; len--)
            {
                var ancestorPath = string.Join("/", segments.Take(len));
                if (TryResolvePathAlias(avatarSkeleton.NodePathToIdx, ancestorPath, out nodeIdx))
                {
                    isExactNodeMatch = false;
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(gameObjectName)
            && TryResolvePathAlias(avatarSkeleton.NodePathToIdx, gameObjectName, out nodeIdx))
        {
            isExactNodeMatch = true;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(meshName)
            && TryResolvePathAlias(avatarSkeleton.NodePathToIdx, meshName, out nodeIdx))
        {
            isExactNodeMatch = true;
            return true;
        }

        nodeIdx = -1;
        isExactNodeMatch = false;
        return false;
    }

    public static bool IsFiniteMatrix(in global::OpenTK.Mathematics.Matrix4 m)
    {
        return float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
            && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
            && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
            && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
    }

    public static global::OpenTK.Mathematics.Matrix4 ComputeRigidPartLocalOffset(
        global::OpenTK.Mathematics.Matrix4? staticPartTransform,
        int attachedNodeIdx,
        bool isExactNodeMatch,
        global::OpenTK.Mathematics.Matrix4[] restWorldMatrices,
        global::OpenTK.Mathematics.Matrix4? explicitLocalToBoneOffset = null)
    {
        if (explicitLocalToBoneOffset.HasValue && IsFiniteMatrix(explicitLocalToBoneOffset.Value))
        {
            return explicitLocalToBoneOffset.Value;
        }

        if (attachedNodeIdx < 0 || attachedNodeIdx >= restWorldMatrices.Length)
        {
            return staticPartTransform.HasValue && IsFiniteMatrix(staticPartTransform.Value)
                ? staticPartTransform.Value
                : global::OpenTK.Mathematics.Matrix4.Identity;
        }

        // When the rigid MeshRenderer sits directly on the matched AvatarNode itself, its mesh vertices
        // are already in that node's local space, so its local offset relative to the node is Identity.
        if (isExactNodeMatch)
        {
            return global::OpenTK.Mathematics.Matrix4.Identity;
        }

        if (!staticPartTransform.HasValue || !IsFiniteMatrix(staticPartTransform.Value))
        {
            return global::OpenTK.Mathematics.Matrix4.Identity;
        }

        try
        {
            var invNodeRest = restWorldMatrices[attachedNodeIdx].Inverted();
            var localOffset = staticPartTransform.Value * invNodeRest;
            if (IsFiniteMatrix(localOffset))
            {
                return localOffset;
            }
        }
        catch
        {
        }

        return global::OpenTK.Mathematics.Matrix4.Identity;
    }

    public static void BuildCompositeAvatarDeformChainMasks(
        AvatarDefaultSkeletonData avatarSkeleton,
        IReadOnlyList<(Mesh Mesh, int[] BoneToNodeMap)> skinnedParts,
        IReadOnlyList<int> rigidAttachNodeIndices,
        out bool[] activeNodeMask,
        out bool[] deformChainMask)
    {
        var nodeCount = avatarSkeleton.NodeCount;
        activeNodeMask = new bool[nodeCount];
        deformChainMask = new bool[nodeCount];
        if (nodeCount <= 0)
        {
            return;
        }

        var hasAnyActive = false;

        if (skinnedParts != null)
        {
            for (var p = 0; p < skinnedParts.Count; p++)
            {
                var (mesh, boneToNodeMap) = skinnedParts[p];
                if (mesh?.m_Skin == null || boneToNodeMap == null || boneToNodeMap.Length == 0)
                {
                    continue;
                }

                var meshWeighted = new bool[boneToNodeMap.Length];
                var anyWeightedOnPart = false;
                foreach (var skin in mesh.m_Skin)
                {
                    if (skin?.boneIndex == null || skin.weight == null)
                    {
                        continue;
                    }

                    var count = Math.Min(skin.boneIndex.Length, skin.weight.Length);
                    for (var i = 0; i < count; i++)
                    {
                        var bIdx = skin.boneIndex[i];
                        if (skin.weight[i] > 0.0001f && bIdx >= 0 && bIdx < boneToNodeMap.Length)
                        {
                            meshWeighted[bIdx] = true;
                            anyWeightedOnPart = true;
                        }
                    }
                }

                for (var b = 0; b < boneToNodeMap.Length; b++)
                {
                    if (anyWeightedOnPart && !meshWeighted[b])
                    {
                        continue;
                    }

                    var nodeIdx = boneToNodeMap[b];
                    if (nodeIdx >= 0 && nodeIdx < nodeCount)
                    {
                        activeNodeMask[nodeIdx] = true;
                        hasAnyActive = true;
                    }
                }
            }
        }

        if (rigidAttachNodeIndices != null)
        {
            for (var i = 0; i < rigidAttachNodeIndices.Count; i++)
            {
                var nodeIdx = rigidAttachNodeIndices[i];
                if (nodeIdx >= 0 && nodeIdx < nodeCount)
                {
                    activeNodeMask[nodeIdx] = true;
                    hasAnyActive = true;
                }
            }
        }

        if (hasAnyActive)
        {
            for (var s = 0; s < nodeCount; s++)
            {
                if (!activeNodeMask[s])
                {
                    continue;
                }

                var current = s;
                var guard = 0;
                while (current >= 0 && current < nodeCount && !deformChainMask[current] && guard++ < nodeCount)
                {
                    deformChainMask[current] = true;
                    current = current < avatarSkeleton.ParentIndices.Length ? avatarSkeleton.ParentIndices[current] : -1;
                }
            }
        }
        else
        {
            Array.Fill(deformChainMask, true);
        }
    }
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

        var avatarParentIndices = new int[skelCount];
        var avatarNodeNames = new string[skelCount];
        var avatarNodeHashToIdx = new Dictionary<uint, int>(skelCount);
        for (var i = 0; i < skelCount; i++)
        {
            avatarParentIndices[i] = nodes[i].m_ParentId;
            if (skelIds != null && i < skelIds.Length)
            {
                avatarNodeHashToIdx[skelIds[i]] = i;
                avatarNodeNames[i] = avatar.FindBonePath(skelIds[i]) ?? string.Empty;
            }
            else
            {
                avatarNodeNames[i] = string.Empty;
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
            MeshParentIndices = meshParentIndices,
            AvatarNodeCount = skelCount,
            AvatarParentIndices = avatarParentIndices,
            AvatarNodeNames = avatarNodeNames,
            AvatarNodeHashToIdx = avatarNodeHashToIdx,
            SkelNodeToMeshBone = skelNodeToMeshBone
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

    public static void BuildAvatarDeformChainMasks(
        int avatarNodeCount,
        int[] avatarParentIndices,
        int[] skelNodeToMeshBone,
        bool[] meshWeightedBoneMask,
        out bool[] avatarWeightedNodeMask,
        out bool[] avatarDeformChainMask)
    {
        avatarWeightedNodeMask = new bool[avatarNodeCount];
        avatarDeformChainMask = new bool[avatarNodeCount];
        if (avatarNodeCount <= 0)
        {
            return;
        }

        var hasAnyWeighted = false;
        for (var s = 0; s < avatarNodeCount && s < skelNodeToMeshBone.Length; s++)
        {
            var mb = skelNodeToMeshBone[s];
            if (mb >= 0 && mb < meshWeightedBoneMask.Length && meshWeightedBoneMask[mb])
            {
                avatarWeightedNodeMask[s] = true;
                hasAnyWeighted = true;
            }
        }

        if (hasAnyWeighted)
        {
            for (var s = 0; s < avatarNodeCount; s++)
            {
                if (!avatarWeightedNodeMask[s])
                {
                    continue;
                }

                var current = s;
                var guard = 0;
                while (current >= 0 && current < avatarNodeCount && !avatarDeformChainMask[current] && guard++ < avatarNodeCount)
                {
                    avatarDeformChainMask[current] = true;
                    current = current < avatarParentIndices.Length ? avatarParentIndices[current] : -1;
                }
            }
        }
        else
        {
            Array.Fill(avatarDeformChainMask, true);
        }
    }
}
