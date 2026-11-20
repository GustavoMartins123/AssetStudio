using Avalonia.Threading;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private List<AnimationClipTargetInfo> LoadAnimationClipTargetsForPreview(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return new List<AnimationClipTargetInfo>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<AnimationClipTargetInfo>();
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAnimationClipTargets(folderPath, signature, clipAssetId);
    }

    private List<string> LoadAnimationClipAvatarAssetIdsForPreview(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAnimationClipAvatarAssetIds(folderPath, signature, clipAssetId);
    }

    private List<string> LoadAnimationClipMeshAssetIdsForPreview(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAnimationClipMeshAssetIds(folderPath, signature, clipAssetId);
    }

    private List<PreviewCandidateItem> BuildAnimationClipPreviewCandidates(
        AnimationClip clip,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId = null,
        string? preferredMeshId = null,
        Avatar? currentAvatarSnapshot = null,
        Mesh? currentMeshSnapshot = null)
    {
        var candidates = new List<PreviewCandidateItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var activePreviewAvatar = currentAvatarSnapshot ?? currentPreviewAvatar;
        var activePreviewMesh = currentMeshSnapshot ?? currentPreviewMesh;

        void AddCandidate(
            Avatar? avatar,
            Mesh? mesh,
            string? avatarId,
            string? meshId,
            string source,
            string? avatarName = null,
            string? meshName = null,
            string? modelGroupId = null,
            string? modelGroupName = null,
            IReadOnlyList<string>? modelGroupMeshIds = null,
            IReadOnlyList<ModelGroupMeshInfo>? modelGroupMeshInfos = null,
            int modelGroupConfidence = 0)
        {
            avatarId = !string.IsNullOrWhiteSpace(avatarId) ? avatarId : GetPreviewObjectKey(avatar);
            meshId = !string.IsNullOrWhiteSpace(meshId) ? meshId : GetPreviewObjectKey(mesh);
            if (avatar == null
                && mesh == null
                && string.IsNullOrWhiteSpace(avatarId)
                && string.IsNullOrWhiteSpace(meshId)
                && string.IsNullOrWhiteSpace(modelGroupId))
            {
                return;
            }

            var key = !string.IsNullOrWhiteSpace(modelGroupId)
                ? $"group:{modelGroupId}|{avatarId}|{meshId}"
                : $"{avatarId}|{meshId}";
            if (!seen.Add(key))
            {
                return;
            }

            avatarName = !string.IsNullOrWhiteSpace(avatarName)
                ? avatarName
                : !string.IsNullOrWhiteSpace(avatar?.m_Name)
                    ? avatar!.m_Name
                    : !string.IsNullOrWhiteSpace(avatarId)
                        ? GetPreviewHandleName(avatarId!, "Avatar")
                        : string.Empty;
            meshName = !string.IsNullOrWhiteSpace(meshName)
                ? meshName
                : !string.IsNullOrWhiteSpace(mesh?.m_Name)
                    ? mesh!.m_Name
                    : !string.IsNullOrWhiteSpace(meshId)
                        ? GetPreviewHandleName(meshId!, "Mesh")
                        : string.Empty;

            var modelGroupPartCount = modelGroupMeshIds?.Count ?? 0;
            var label = !string.IsNullOrWhiteSpace(modelGroupId)
                ? $"{source}: {(!string.IsNullOrWhiteSpace(modelGroupName) ? modelGroupName : "Model")} ({modelGroupPartCount:N0} parts)"
                : !string.IsNullOrWhiteSpace(avatarName) && !string.IsNullOrWhiteSpace(meshName)
                    ? $"{source}: {avatarName} -> {meshName}"
                    : !string.IsNullOrWhiteSpace(avatarName)
                        ? $"{source}: {avatarName}"
                        : $"{source}: {meshName}";

            if (!string.IsNullOrWhiteSpace(modelGroupId) && !string.IsNullOrWhiteSpace(meshName))
            {
                label += $" -> {meshName}";
            }

            if (mesh != null)
            {
                label += $" (Mesh PathID: {mesh.m_PathID})";
            }
            else if (!string.IsNullOrWhiteSpace(meshId))
            {
                label += $" (Mesh PathID: {assetsManager.ProjectIndex.GetHandle(meshId!)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?"})";
            }
            else if (avatar != null)
            {
                label += $" (Avatar PathID: {avatar.m_PathID})";
            }
            else if (!string.IsNullOrWhiteSpace(avatarId))
            {
                label += $" (Avatar PathID: {assetsManager.ProjectIndex.GetHandle(avatarId!)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?"})";
            }

            candidates.Add(new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = avatar,
                Mesh = mesh,
                AvatarId = avatarId ?? string.Empty,
                MeshId = meshId ?? string.Empty,
                ModelGroupId = modelGroupId ?? string.Empty,
                ModelGroupName = modelGroupName ?? string.Empty,
                ModelGroupMeshIds = modelGroupMeshIds ?? Array.Empty<string>(),
                ModelGroupMeshInfos = modelGroupMeshInfos ?? Array.Empty<ModelGroupMeshInfo>(),
                ModelGroupTransforms = modelGroupMeshInfos?.Select(mesh => mesh.TransformMatrix).ToArray() ?? Array.Empty<float[]?>(),
                ModelGroupMeshCount = modelGroupMeshIds?.Count ?? 0,
                ModelGroupConfidence = modelGroupConfidence,
                Label = label
            });
        }

        if (preferredAvatar != null
            || preferredMesh != null
            || !string.IsNullOrWhiteSpace(preferredAvatarId)
            || !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            AddCandidate(preferredAvatar, preferredMesh, preferredAvatarId, preferredMeshId, "Selected");
        }

        if (activePreviewMesh != null)
        {
            AddCandidate(activePreviewAvatar, activePreviewMesh, null, null, activePreviewAvatar != null ? "Current" : "Current mesh");
        }

        if (activePreviewAvatar != null)
        {
            var addedCurrentMesh = false;
            List<Mesh>? currentMeshes = null;
            Mesh? cachedMesh = null;
            lock (previewCacheLock)
            {
                if (avatarMeshesCache != null && avatarMeshesCache.TryGetValue(activePreviewAvatar, out var foundMeshes))
                {
                    currentMeshes = new List<Mesh>(foundMeshes);
                }

                if (avatarMeshCache != null)
                {
                    avatarMeshCache.TryGetValue(activePreviewAvatar, out cachedMesh);
                }
            }

            if (currentMeshes != null)
            {
                foreach (var mesh in currentMeshes)
                {
                    AddCandidate(activePreviewAvatar, mesh, null, null, "Current avatar");
                    addedCurrentMesh = true;
                }
            }

            if (!addedCurrentMesh && cachedMesh != null)
            {
                AddCandidate(activePreviewAvatar, cachedMesh, null, null, "Current avatar");
                addedCurrentMesh = true;
            }

            if (!addedCurrentMesh)
            {
                AddCandidate(activePreviewAvatar, null, null, null, "Current avatar");
            }
        }

        if (assetsManager.LazyLoading)
        {
            var avatarIdsCoveredByMesh = new HashSet<string>(StringComparer.Ordinal);
            var avatarIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByAvatar = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);

            // Fast v14 pre-indexed single-query lookup from AnimationClipTargets
            var preIndexedTargets = LoadAnimationClipTargetsForPreview(clip);
            foreach (var target in preIndexedTargets)
            {
                if (!string.IsNullOrWhiteSpace(target.ModelGroupId))
                {
                    var groupMeshes = LoadModelGroupMeshesForPreview(target.ModelGroupId);
                    var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
                    var repMeshId = representative?.MeshAssetId ?? target.MeshAssetId;
                    if (!string.IsNullOrWhiteSpace(repMeshId))
                    {
                        var meshIds = groupMeshes
                            .Select(mesh => mesh.MeshAssetId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToArray();
                        foreach (var meshId in meshIds)
                        {
                            meshIdsCoveredByModelGroup.Add(meshId);
                        }

                        if (!string.IsNullOrWhiteSpace(target.AvatarAssetId))
                        {
                            avatarIdsCoveredByModelGroup.Add(target.AvatarAssetId);
                            avatarIdsCoveredByMesh.Add(target.AvatarAssetId);
                            meshIdsCoveredByAvatar.Add(repMeshId);
                        }

                        var groupName = !string.IsNullOrWhiteSpace(target.ModelGroupName)
                            ? target.ModelGroupName
                            : GetPreviewHandleName(repMeshId, "Model");
                        var meshName = !string.IsNullOrWhiteSpace(representative?.MeshName)
                            ? representative!.MeshName
                            : !string.IsNullOrWhiteSpace(target.MeshName)
                                ? target.MeshName
                                : GetPreviewHandleName(repMeshId, "Mesh");
                        var avatarName = !string.IsNullOrWhiteSpace(target.AvatarName)
                            ? target.AvatarName
                            : !string.IsNullOrWhiteSpace(target.AvatarAssetId)
                                ? GetPreviewHandleName(target.AvatarAssetId, "Avatar")
                                : string.Empty;

                        AddCandidate(
                            null,
                            null,
                            target.AvatarAssetId,
                            repMeshId,
                            "Model group",
                            avatarName,
                            meshName,
                            target.ModelGroupId,
                            groupName,
                            meshIds,
                            groupMeshes,
                            target.ModelGroupConfidence);
                        continue;
                    }
                }

                var relationSource = target.RelationKind switch
                {
                    "BoneHashMatch" => "Bone match",
                    "Container" => "Container",
                    "LegacyAnimation" => "Legacy animation",
                    _ => !string.IsNullOrWhiteSpace(target.AvatarAssetId) ? "Avatar relation" : "Animator mesh"
                };

                if (!string.IsNullOrWhiteSpace(target.AvatarAssetId) && !string.IsNullOrWhiteSpace(target.MeshAssetId))
                {
                    avatarIdsCoveredByMesh.Add(target.AvatarAssetId);
                    meshIdsCoveredByAvatar.Add(target.MeshAssetId);
                }

                AddCandidate(
                    null,
                    null,
                    target.AvatarAssetId,
                    target.MeshAssetId,
                    relationSource,
                    !string.IsNullOrWhiteSpace(target.AvatarName) ? target.AvatarName : null,
                    !string.IsNullOrWhiteSpace(target.MeshName) ? target.MeshName : null);
            }

            var avatarIds = LoadAnimationClipAvatarAssetIdsForPreview(clip);
            var avatarMeshIdsByAvatarId = LoadAvatarMeshAssetIdsByAvatarIdsForPreview(avatarIds);
            var modelGroupsByAvatarId = LoadModelGroupsByAvatarIdsForPreview(avatarIds);

            foreach (var avatarId in avatarIds)
            {
                if (modelGroupsByAvatarId.TryGetValue(avatarId, out var modelGroups) && modelGroups.Count > 0)
                {
                    foreach (var group in modelGroups)
                    {
                        var groupMeshes = LoadModelGroupMeshesForPreview(group.GroupId);
                        var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
                        if (representative == null)
                        {
                            continue;
                        }

                        var meshIds = groupMeshes
                            .Select(mesh => mesh.MeshAssetId)
                            .Where(meshId => !string.IsNullOrWhiteSpace(meshId))
                            .ToArray();
                        foreach (var meshId in meshIds)
                        {
                            meshIdsCoveredByModelGroup.Add(meshId);
                        }

                        avatarIdsCoveredByModelGroup.Add(avatarId);
                        avatarIdsCoveredByMesh.Add(avatarId);
                        meshIdsCoveredByAvatar.Add(representative.MeshAssetId);

                        var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
                            ? group.GroupName
                            : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                                ? group.RootGameObjectName
                                : GetPreviewHandleName(representative.MeshAssetId, "Model");
                        var meshName = !string.IsNullOrWhiteSpace(representative.MeshName)
                            ? representative.MeshName
                            : GetPreviewHandleName(representative.MeshAssetId, "Mesh");

                        AddCandidate(
                            null,
                            null,
                            avatarId,
                            representative.MeshAssetId,
                            "Model group",
                            GetPreviewHandleName(avatarId, "Avatar"),
                            meshName,
                            group.GroupId,
                            groupName,
                            meshIds,
                            groupMeshes,
                            group.Confidence);
                    }

                    if (avatarIdsCoveredByModelGroup.Contains(avatarId))
                    {
                        continue;
                    }
                }

                if (avatarMeshIdsByAvatarId.TryGetValue(avatarId, out var avatarMeshIds) && avatarMeshIds.Count > 0)
                {
                    foreach (var meshId in avatarMeshIds)
                    {
                        if (string.IsNullOrWhiteSpace(meshId))
                        {
                            continue;
                        }

                        meshIdsCoveredByAvatar.Add(meshId);
                        avatarIdsCoveredByMesh.Add(avatarId);
                        AddCandidate(
                            null,
                            null,
                            avatarId,
                            meshId,
                            "Avatar relation",
                            GetPreviewHandleName(avatarId, "Avatar"),
                            GetPreviewHandleName(meshId, "Mesh"));
                    }

                    continue;
                }

                AddCandidate(null, null, avatarId, null, "Avatar relation", GetPreviewHandleName(avatarId, "Avatar"));
            }

            foreach (var meshId in LoadAnimationClipMeshAssetIdsForPreview(clip))
            {
                if (meshIdsCoveredByAvatar.Contains(meshId) || meshIdsCoveredByModelGroup.Contains(meshId))
                {
                    continue;
                }

                AddCandidate(null, null, null, meshId, "Animator mesh", meshName: GetPreviewHandleName(meshId, "Mesh"));
            }

            if (meshIdsCoveredByAvatar.Count > 0)
            {
                candidates.RemoveAll(candidate =>
                    (string.IsNullOrWhiteSpace(candidate.AvatarId)
                        && candidate.Avatar == null
                        && !string.IsNullOrWhiteSpace(candidate.MeshId)
                        && (meshIdsCoveredByAvatar.Contains(candidate.MeshId)
                            || meshIdsCoveredByModelGroup.Contains(candidate.MeshId)))
                    || (!string.IsNullOrWhiteSpace(candidate.AvatarId)
                        && candidate.Mesh == null
                        && string.IsNullOrWhiteSpace(candidate.MeshId)
                        && avatarIdsCoveredByMesh.Contains(candidate.AvatarId))
                    || (!string.IsNullOrWhiteSpace(candidate.AvatarId)
                        && candidate.Mesh == null
                        && !string.IsNullOrWhiteSpace(candidate.MeshId)
                        && !candidate.IsModelGroup
                        && avatarIdsCoveredByModelGroup.Contains(candidate.AvatarId)
                        && meshIdsCoveredByModelGroup.Contains(candidate.MeshId)));
            }
        }
        else
        {
            foreach (var candidateAvatar in FindCandidateAvatarsForAnimationClip(clip))
            {
                var meshes = FindMeshesForAvatar(candidateAvatar);
                if (meshes.Count == 0)
                {
                    AddCandidate(candidateAvatar, null, null, null, "Avatar relation");
                    continue;
                }

                foreach (var mesh in meshes)
                {
                    AddCandidate(candidateAvatar, mesh, null, null, "Avatar relation");
                }
            }
        }

        return candidates;
    }

    private PreviewCandidateItem? SelectAnimationClipPreviewCandidate(
        AnimationClip clip,
        IReadOnlyList<PreviewCandidateItem> candidates,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId = null,
        string? preferredMeshId = null)
    {
        if (preferredAvatar != null
            || preferredMesh != null
            || !string.IsNullOrWhiteSpace(preferredAvatarId)
            || !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            var preferred = candidates.FirstOrDefault(candidate =>
                (preferredAvatar == null || AreSamePreviewObject(candidate.Avatar, preferredAvatar))
                && (preferredMesh == null || AreSamePreviewObject(candidate.Mesh, preferredMesh))
                && (string.IsNullOrWhiteSpace(preferredAvatarId) || string.Equals(candidate.AvatarId, preferredAvatarId, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(preferredMeshId) || string.Equals(candidate.MeshId, preferredMeshId, StringComparison.Ordinal)));
            if (preferred != null)
            {
                return preferred;
            }
        }

        var compatibleAvatar = candidates.FirstOrDefault(candidate =>
            candidate.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null
            && candidate.Mesh != null
            && IsAnimationClipCompatibleWithAvatar(clip, candidate.Avatar));
        if (compatibleAvatar != null)
        {
            return compatibleAvatar;
        }

        if (assetsManager.LazyLoading)
        {
            var lazyModelGroup = candidates.FirstOrDefault(candidate =>
                candidate.IsModelGroup
                && (!string.IsNullOrWhiteSpace(candidate.MeshId) || candidate.Mesh != null));
            if (lazyModelGroup != null)
            {
                return lazyModelGroup;
            }

            var lazyAvatar = candidates.FirstOrDefault(candidate =>
                candidate.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null
                && candidate.Mesh != null);
            if (lazyAvatar != null)
            {
                return lazyAvatar;
            }
        }

        var animatorMesh = candidates.FirstOrDefault(candidate =>
            candidate.Avatar == null
            && candidate.Mesh != null
            && candidate.Label.StartsWith("Animator mesh:", StringComparison.Ordinal));
        if (animatorMesh != null)
        {
            return animatorMesh;
        }

        return candidates.FirstOrDefault(candidate => candidate.Mesh != null)
            ?? candidates.FirstOrDefault(candidate => candidate.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
            ?? candidates.FirstOrDefault();
    }

    private PreviewCandidateItem? ResolveAnimationClipPreviewCandidate(PreviewCandidateItem? candidate, bool cacheRelation = true)
    {
        if (candidate == null)
        {
            return null;
        }

        var avatar = candidate.Avatar;
        var mesh = candidate.Mesh;
        if (avatar == null && !string.IsNullOrWhiteSpace(candidate.AvatarId))
        {
            avatar = ResolveAvatarPreviewCandidate(candidate.AvatarId);
        }

        if (mesh == null && !string.IsNullOrWhiteSpace(candidate.MeshId))
        {
            mesh = ResolveMeshPreviewCandidate(candidate.MeshId);
        }

        if (mesh == null && avatar != null)
        {
            mesh = ResolveFirstMeshForAvatar(avatar);
        }

        if (cacheRelation && avatar != null && mesh != null)
        {
            CacheAvatarMeshRelation(avatar, mesh);
        }

        return new PreviewCandidateItem
        {
            AnimationClip = candidate.AnimationClip,
            Avatar = avatar,
            Mesh = mesh,
            AvatarId = !string.IsNullOrWhiteSpace(candidate.AvatarId) ? candidate.AvatarId : GetPreviewObjectKey(avatar),
            MeshId = !string.IsNullOrWhiteSpace(candidate.MeshId) ? candidate.MeshId : GetPreviewObjectKey(mesh),
            ModelGroupId = candidate.ModelGroupId,
            ModelGroupName = candidate.ModelGroupName,
            ModelGroupMeshIds = candidate.ModelGroupMeshIds,
            ModelGroupMeshInfos = candidate.ModelGroupMeshInfos,
            ModelGroupMeshes = candidate.ModelGroupMeshes,
            ModelGroupTransforms = candidate.ModelGroupTransforms,
            ModelGroupMeshCount = candidate.ModelGroupMeshCount,
            ModelGroupConfidence = candidate.ModelGroupConfidence,
            Label = candidate.Label
        };
    }

    private void QueueAnimationClipPreviewCandidateBuild(
        AnimationClip clip,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId,
        string? preferredMeshId,
        PreviewCandidateItem? selectedCandidate)
    {
        QueueAnimationClipPreviewAsync(
            clip,
            preferredAvatar,
            preferredMesh,
            preferredAvatarId,
            preferredMeshId,
            rebuildCandidateControls: true,
            selectedCandidate: selectedCandidate);
    }

    private List<Avatar> FindCandidateAvatarsForAnimationClip(AnimationClip clip)
    {
        var avatars = new List<Avatar>();
        var seenAvatarIds = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(Avatar? avatar)
        {
            if (avatar == null)
            {
                return;
            }

            var avatarId = avatar.assetsFile != null
                ? AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID)
                : $"runtime:{avatar.m_PathID}";
            if (seenAvatarIds.Add(avatarId))
            {
                avatars.Add(avatar);
            }
        }

        foreach (var avatar in TryLoadAvatarsForAnimationClipFromSemanticCache(clip))
        {
            AddCandidate(avatar);
        }

        Avatar? cachedAvatar = null;
        lock (previewCacheLock)
        {
            if (animationClipAvatarCache != null)
            {
                animationClipAvatarCache.TryGetValue(clip, out cachedAvatar);
            }
        }
        AddCandidate(cachedAvatar);

        return avatars;
    }

    private List<Avatar> TryLoadAvatarsForAnimationClipFromSemanticCache(AnimationClip clip)
    {
        var avatars = new List<Avatar>();
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return avatars;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return avatars;
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var avatarIds = _sqliteCache.LoadAnimationClipAvatarAssetIds(folderPath, signature, clipAssetId);
        var seenAvatarIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var avatarId in avatarIds)
        {
            if (string.IsNullOrWhiteSpace(avatarId) || !seenAvatarIds.Add(avatarId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(avatarId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Avatar avatar)
            {
                avatars.Add(avatar);
            }
        }

        if (avatars.Count > 0)
        {
            lock (previewCacheLock)
            {
                animationClipAvatarCache ??= new Dictionary<AnimationClip, Avatar?>();
                animationClipAvatarCache[clip] = avatars[0];
            }
        }

        return avatars;
    }

    private List<Mesh> TryLoadMeshesForAnimationClipFromSemanticCache(AnimationClip clip)
    {
        var meshes = new List<Mesh>();
        if (!assetsManager.LazyLoading || clip.assetsFile == null || currentScanResult == null)
        {
            return meshes;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return meshes;
        }

        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var meshIds = _sqliteCache.LoadAnimationClipMeshAssetIds(folderPath, signature, clipAssetId);
        var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meshId in meshIds)
        {
            if (string.IsNullOrWhiteSpace(meshId) || !seenMeshIds.Add(meshId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(meshId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is Mesh mesh)
            {
                meshes.Add(mesh);
            }
        }

        return meshes;
    }

    private bool IsAnimationClipCompatibleWithAvatar(AnimationClip clip, Avatar avatar)
    {
        HashSet<uint>? bindingPaths;
        lock (previewCacheLock)
        {
            if (animationClipAvatarCache != null
                && animationClipAvatarCache.TryGetValue(clip, out var cachedAvatar)
                && ReferenceEquals(cachedAvatar, avatar))
            {
                return true;
            }

            animationClipTransformBindingsCache ??= new Dictionary<AnimationClip, HashSet<uint>>();
            animationClipTransformBindingsCache.TryGetValue(clip, out bindingPaths);
        }

        if (bindingPaths == null)
        {
            bindingPaths = GetTransformBindingPathsBackground(clip);
            lock (previewCacheLock)
            {
                animationClipTransformBindingsCache ??= new Dictionary<AnimationClip, HashSet<uint>>();
                animationClipTransformBindingsCache[clip] = bindingPaths;
            }
        }

        if (bindingPaths.Count == 0 || avatar.m_TOS == null)
        {
            return false;
        }

        var avatarPathHashes = new HashSet<uint>(avatar.m_TOS.Select(x => x.Key));
        var overlap = bindingPaths.Count(avatarPathHashes.Contains);
        return IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap);
    }
}
