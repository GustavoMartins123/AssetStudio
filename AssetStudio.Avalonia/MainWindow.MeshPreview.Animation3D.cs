using AssetStudio;
using AssetStudio.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewAnimationClip(
        AnimationClip clip,
        Avatar? preferredAvatar = null,
        Mesh? preferredMesh = null,
        string? preferredAvatarId = null,
        string? preferredMeshId = null,
        bool rebuildCandidateControls = true,
        PreviewCandidateItem? selectedCandidate = null)
    {
        if (clip.m_PPtrCurves != null && clip.m_PPtrCurves.Length > 0)
        {
            Preview2DAnimationClip(clip);
            return;
        }

        Stop2DAnimation();
        if (rebuildCandidateControls)
        {
            QueueAnimationClipPreviewCandidateBuild(
                clip,
                preferredAvatar,
                preferredMesh,
                preferredAvatarId,
                preferredMeshId,
                selectedCandidate);
            return;
        }

        PreviewCandidateItem? activeCandidate;
        if (selectedCandidate != null)
        {
            activeCandidate = selectedCandidate;
        }
        else
        {
            activeCandidate = new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = preferredAvatar,
                Mesh = preferredMesh,
                AvatarId = preferredAvatarId ?? string.Empty,
                MeshId = preferredMeshId ?? string.Empty
            };
        }

        var resolvedCandidate = ResolveAnimationClipPreviewCandidate(activeCandidate);
        Avatar? avatar = resolvedCandidate?.Avatar;
        Mesh? avatarMesh = resolvedCandidate?.Mesh;

        if (avatar == null && avatarMesh != null)
        {
            if (TryPreviewAnimationClipWithRendererSkeletonFallback(clip, avatarMesh, resolvedCandidate?.MeshId))
            {
                return;
            }

            StatusStripUpdate($"AnimationClip: {clip.m_Name} | Mesh target found, but no usable animation tracks matched {avatarMesh.m_Name}.");
            return;
        }

        if (avatar == null)
        {
            if (TryPreviewAnimationClipWithRendererSkeletonFallback(clip, preferredMesh, preferredMeshId))
            {
                return;
            }

            StatusStripUpdate($"AnimationClip: No compatible Avatar or mesh target found for {clip.m_Name}.");
            return;
        }

        if (avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            StatusStripUpdate("AnimationClip: No Avatar found to preview animation.");
            return;
        }

        if (avatarMesh == null)
        {
            avatarMesh = ResolveFirstMeshForAvatar(avatar);
        }
        avatarMesh?.EnsureProcessed();

        if (avatarMesh == null || !MeshPreviewSkeletonBuilder.TryBuildAvatarMeshSkeleton(avatar, avatarMesh, out var skeleton))
        {
            StatusStripUpdate("AnimationClip: No suitable mesh with bind poses found.");
            return;
        }

        var meshBonePathToIdx = MeshPreviewSkeletonBuilder.BuildBonePathAliasMap(skeleton.MeshBoneNames, skeleton.MeshBoneCount);
        MeshPreviewSkeletonBuilder.BuildSkinWeightMasks(
            avatarMesh,
            skeleton.MeshBoneCount,
            skeleton.MeshParentIndices,
            out var weightedBoneMask,
            out var deformChainMask);

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            var path = avatar.FindBonePath(binding.path);
            if (!string.IsNullOrEmpty(path) && MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, path, out meshBoneIdx))
            {
                return true;
            }

            return skeleton.MeshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        bool ShouldUseBinding(int meshBoneIdx)
        {
            return MeshPreviewSkeletonBuilder.ShouldUseAnimationBinding(
                meshBoneIdx,
                skeleton.MeshBoneCount,
                deformChainMask,
                weightedBoneMask,
                skeleton.MeshBoneNames);
        }

        var muscleClip = clip.m_MuscleClip;
        if (muscleClip?.m_Clip == null)
        {
            StatusStripUpdate("AnimationClip: No muscle clip data.");
            return;
        }

        var tracks = new AnimationTrackSet();
        var innerClip = muscleClip.m_Clip;
        var bindings = clip.m_ClipBindingConstant ?? innerClip.ConvertValueArrayToGenericBinding();
        if (bindings?.genericBindings != null)
        {
            MeshPreviewAnimationEvaluator.ExtractMuscleClipTracks(
                innerClip,
                bindings,
                muscleClip.m_StopTime,
                TryResolveBindingBone,
                ShouldUseBinding,
                tracks,
                updateMaxTimeBeforeBoundsCheck: true);
        }

        tracks.SortAll();
        if (tracks.TotalTrackCount == 0)
        {
            if (GLPreviewControl != null)
            {
                GLPreviewControl.SetAvatar(avatarMesh, skeleton.RestBonePositions, skeleton.MeshParentIndices, skeleton.MeshBoneNames);
                GLPreviewControl.IsVisible = true;
                ShowPreviewGeometryControls(showBoneControls: true);
                GLPreviewControl.Focus();
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = false;
                StatusStripUpdate($"AnimationClip: {clip.m_Name} | No animation tracks extracted, showing bind pose | Bones: {skeleton.MeshBoneCount}");
            }
            return;
        }

        var defaultPose = avatar.m_Avatar.m_DefaultPose?.m_X ?? avatar.m_Avatar.m_AvatarSkeletonPose?.m_X;
        var sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        var maxTime = tracks.MaxTime > 0 ? tracks.MaxTime : muscleClip.m_StopTime > 0 ? muscleClip.m_StopTime : 1f;

        MeshPreviewAnimationEvaluator.EvaluateFramesFromTracks(
            avatarMesh,
            skeleton.RestBonePositions,
            skeleton.MeshParentIndices,
            skeleton.MeshBoneNames,
            skeleton.BindPoseInverses,
            deformChainMask,
            weightedBoneMask,
            tracks.PosTracks,
            tracks.RotTracks,
            tracks.ScaleTracks,
            sampleRate,
            maxTime,
            defaultPose,
            skeleton.MeshBoneToSkelNode,
            useCeilingFrameCount: false,
            failIfAllTracksFiltered: false,
            out var allFrames,
            out var allBoneMatrices,
            out var renderParentIndices,
            out var resolvedSampleRate,
            out _);

        if (GLPreviewControl != null)
        {
            GLPreviewControl.SetAnimatedAvatar(avatarMesh, allFrames, allBoneMatrices, renderParentIndices, resolvedSampleRate, skeleton.MeshBoneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;

            ShowAnimationPlayback(allFrames.Length);

            var targetStatus = resolvedCandidate?.IsModelGroup == true
                ? $" | Model group: {(!string.IsNullOrWhiteSpace(resolvedCandidate.ModelGroupName) ? resolvedCandidate.ModelGroupName : resolvedCandidate.ModelGroupId)} ({resolvedCandidate.ModelGroupMeshCount:N0} parts) | Representative mesh: {avatarMesh.m_Name}"
                : $" | Mesh: {avatarMesh.m_Name}";
            StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name}{targetStatus} | Frames: {allFrames.Length} | FPS: {resolvedSampleRate} | Tracks: {tracks.TotalTrackCount}");
        }
    }

    private bool TryPreviewAnimationClipWithRendererSkeletonFallback(AnimationClip clip, Mesh? preferredMesh = null, string? preferredMeshId = null)
    {
        var meshes = new List<Mesh>();
        void AddMesh(Mesh? mesh)
        {
            if (mesh != null && !meshes.Any(existing => AreSamePreviewObject(existing, mesh)))
            {
                meshes.Add(mesh);
            }
        }

        AddMesh(preferredMesh);
        if (preferredMesh == null && !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            AddMesh(ResolveMeshPreviewCandidate(preferredMeshId));
        }

        AddMesh(currentPreviewMesh);
        if (meshes.Count == 0)
        {
            var firstMeshId = LoadAnimationClipMeshAssetIdsForPreview(clip).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstMeshId))
            {
                AddMesh(ResolveMeshPreviewCandidate(firstMeshId));
            }
        }

        Mesh? staticMesh = null;
        global::OpenTK.Mathematics.Vector3[]? staticBonePositions = null;
        int[]? staticParentIndices = null;
        string[]? staticBoneNames = null;
        string? lastReason = null;

        foreach (var mesh in meshes)
        {
            mesh.EnsureProcessed();
            EnsureMeshPreviewDependenciesLoaded(mesh);
            var renderer = FindSkinnedRendererForAvatarMesh(null, mesh);
            if (renderer == null)
            {
                continue;
            }

            if (!TryBuildSkeletonFromSkinnedRenderer(
                    null,
                    mesh,
                    renderer,
                    out var bonePositions,
                    out var parentIndices,
                    out var boneNames))
            {
                continue;
            }

            if (GLPreviewControl == null)
            {
                return false;
            }

            if (staticMesh == null)
            {
                staticMesh = mesh;
                staticBonePositions = bonePositions;
                staticParentIndices = parentIndices;
                staticBoneNames = boneNames;
            }

            if (TryBuildAnimationFramesFromRendererSkeleton(
                    clip,
                    mesh,
                    bonePositions,
                    parentIndices,
                    boneNames,
                    out var allFrames,
                    out var allBoneMatrices,
                    out var renderParentIndices,
                    out var sampleRate,
                    out var trackCount,
                    out lastReason))
            {
                currentPreviewMesh = mesh;
                currentPreviewAvatar = null;
                GLPreviewControl.SetAnimatedAvatar(mesh, allFrames, allBoneMatrices, renderParentIndices, sampleRate, boneNames);
                GLPreviewControl.IsVisible = true;
                ShowPreviewGeometryControls(showBoneControls: true);
                GLPreviewControl.Focus();
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = false;

                ShowAnimationPlayback(allFrames.Length);

                StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name} | Mesh target: {mesh.m_Name} | Frames: {allFrames.Length} | FPS: {sampleRate:0.##} | Tracks: {trackCount}");
                return true;
            }
        }

        if (staticMesh != null && staticBonePositions != null && staticParentIndices != null)
        {
            currentPreviewMesh = staticMesh;
            currentPreviewAvatar = null;
            GLPreviewControl!.SetAvatar(staticMesh, staticBonePositions, staticParentIndices, staticBoneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate($"AnimationClip: {clip.m_Name} | No matching animation tracks for mesh {staticMesh.m_Name}; showing renderer skeleton. {lastReason}");
            return true;
        }

        return false;
    }

    private bool TryBuildAnimationFramesFromRendererSkeleton(
        AnimationClip clip,
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out float sampleRate,
        out int trackCount,
        out string reason)
    {
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        trackCount = 0;
        reason = string.Empty;

        if (mesh.m_BindPose == null || mesh.m_BindPose.Length == 0)
        {
            reason = "Mesh has no bind poses.";
            return false;
        }

        var meshBoneCount = Math.Min(mesh.m_BindPose.Length, Math.Min(restBonePositions.Length, meshParentIndices.Length));
        if (meshBoneCount == 0)
        {
            reason = "Renderer skeleton has no bones.";
            return false;
        }

        var bindPoseInverses = MeshPreviewSkeletonBuilder.BuildBindPoseInverses(mesh.m_BindPose, meshBoneCount, out _);

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        if (mesh.m_BoneNameHashes != null)
        {
            for (var i = 0; i < Math.Min(mesh.m_BoneNameHashes.Length, meshBoneCount); i++)
            {
                meshBoneHashToIdx[mesh.m_BoneNameHashes[i]] = i;
            }
        }

        var meshBonePathToIdx = MeshPreviewSkeletonBuilder.BuildBonePathAliasMap(meshBoneNames, meshBoneCount);
        MeshPreviewSkeletonBuilder.BuildSkinWeightMasks(
            mesh,
            meshBoneCount,
            meshParentIndices,
            out var weightedBoneMask,
            out var deformChainMask);

        bool TryResolveCurvePath(string? path, out int meshBoneIdx)
        {
            return MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, path, out meshBoneIdx);
        }

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            return meshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        bool ShouldUseBinding(int meshBoneIdx)
        {
            return MeshPreviewSkeletonBuilder.ShouldUseAnimationBinding(
                meshBoneIdx,
                meshBoneCount,
                deformChainMask,
                weightedBoneMask,
                meshBoneNames);
        }

        var tracks = new AnimationTrackSet();
        MeshPreviewAnimationEvaluator.ExtractLegacyCurves(clip, TryResolveCurvePath, ShouldUseBinding, tracks);

        var muscleClip = clip.m_MuscleClip;
        var innerClip = muscleClip?.m_Clip;
        AnimationClipBindingConstant? bindings = null;
        if (innerClip != null)
        {
            bindings = clip.m_ClipBindingConstant;
            if (bindings == null && innerClip.m_Binding != null)
            {
                bindings = innerClip.ConvertValueArrayToGenericBinding();
            }
        }

        if (innerClip != null && bindings?.genericBindings != null)
        {
            MeshPreviewAnimationEvaluator.ExtractMuscleClipTracks(
                innerClip,
                bindings,
                muscleClip?.m_StopTime,
                TryResolveBindingBone,
                ShouldUseBinding,
                tracks);
        }

        tracks.SortAll();
        trackCount = tracks.TotalTrackCount;
        if (trackCount == 0)
        {
            reason = bindings?.genericBindings != null
                ? "Clip bindings did not match renderer bone hashes/names."
                : "Clip has no readable transform binding table.";
            return false;
        }

        return BuildAnimationFramesFromTracks(
            mesh,
            restBonePositions,
            meshParentIndices,
            meshBoneNames,
            bindPoseInverses,
            deformChainMask,
            weightedBoneMask,
            tracks.PosTracks,
            tracks.RotTracks,
            tracks.ScaleTracks,
            sampleRate,
            tracks.MaxTime > 0f ? tracks.MaxTime : muscleClip?.m_StopTime ?? 1f,
            out allFrames,
            out allBoneMatrices,
            out renderParentIndices,
            out reason);
    }

    private static string NormalizePreviewAnimationPath(string? path)
    {
        return MeshPreviewSkeletonBuilder.NormalizeAnimationPath(path);
    }

    private static void AddPreviewBonePathAlias(Dictionary<string, int> bonePathToIndex, string? path, int meshBoneIdx)
    {
        MeshPreviewSkeletonBuilder.AddBonePathAlias(bonePathToIndex, path, meshBoneIdx);
    }

    private static global::OpenTK.Mathematics.Vector3 ToOtkVector3Preview(AssetStudio.Vector3 value)
    {
        return MeshPreviewSkeletonBuilder.ToOtkVector3(value);
    }

    private static global::OpenTK.Mathematics.Quaternion ToOtkQuaternionPreview(AssetStudio.Quaternion value)
    {
        return MeshPreviewSkeletonBuilder.ToOtkQuaternion(value);
    }

    private static global::OpenTK.Mathematics.Matrix4 CreatePreviewLocalMatrix(
        global::OpenTK.Mathematics.Vector3 position,
        global::OpenTK.Mathematics.Quaternion rotation,
        global::OpenTK.Mathematics.Vector3 scale)
    {
        return MeshPreviewSkeletonBuilder.CreateLocalMatrix(position, rotation, scale);
    }

    private static bool IsFinitePreviewVector(global::OpenTK.Mathematics.Vector3 value)
    {
        return MeshPreviewSkeletonBuilder.IsFiniteVector(value);
    }

    private static bool IsAuxiliaryPreviewAnimationBone(string? path)
    {
        return MeshPreviewSkeletonBuilder.IsAuxiliaryAnimationBone(path);
    }

    private static bool BuildAnimationFramesFromTracks(
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        global::OpenTK.Mathematics.Matrix4[] bindPoseInverses,
        bool[] deformChainMask,
        bool[] weightedBoneMask,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> posTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> rotTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> scaleTracks,
        float sampleRate,
        float maxTime,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out string reason)
    {
        return MeshPreviewAnimationEvaluator.EvaluateFramesFromTracks(
            mesh,
            restBonePositions,
            meshParentIndices,
            meshBoneNames,
            bindPoseInverses,
            deformChainMask,
            weightedBoneMask,
            posTracks,
            rotTracks,
            scaleTracks,
            sampleRate,
            maxTime,
            avatarDefaultPose: null,
            meshBoneToSkelNode: null,
            useCeilingFrameCount: true,
            failIfAllTracksFiltered: true,
            out allFrames,
            out allBoneMatrices,
            out renderParentIndices,
            out _,
            out reason);
    }
}
