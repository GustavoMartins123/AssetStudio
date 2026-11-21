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
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new List<AnimationClipTargetInfo>();
        }

        // Read directly via WAL without blocking on full connections_completed so lazy_backfill and partial caches work immediately.
        var clipAssetId = AssetHandle.BuildUniqueID(clip.assetsFile, clip.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAnimationClipTargets(folderPath, signature, clipAssetId);
    }

    private Dictionary<string, List<ModelGroupMeshInfo>> LoadModelGroupMeshesByGroupIdsForPreview(IReadOnlyList<string> groupIds)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || groupIds.Count == 0)
        {
            return new Dictionary<string, List<ModelGroupMeshInfo>>(StringComparer.Ordinal);
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new Dictionary<string, List<ModelGroupMeshInfo>>(StringComparer.Ordinal);
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadModelGroupMeshesByGroupIds(folderPath, signature, groupIds);
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
            int modelGroupConfidence = 0,
            bool isCurrentPreviewFallback = false)
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
                IsCurrentPreviewFallback = isCurrentPreviewFallback,
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
            AddCandidate(
                activePreviewAvatar,
                activePreviewMesh,
                null,
                null,
                activePreviewAvatar != null ? "Current" : "Current mesh",
                isCurrentPreviewFallback: true);
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
                    AddCandidate(activePreviewAvatar, mesh, null, null, "Current avatar", isCurrentPreviewFallback: true);
                    addedCurrentMesh = true;
                }
            }

            if (!addedCurrentMesh && cachedMesh != null)
            {
                AddCandidate(activePreviewAvatar, cachedMesh, null, null, "Current avatar", isCurrentPreviewFallback: true);
                addedCurrentMesh = true;
            }

            if (!addedCurrentMesh)
            {
                AddCandidate(activePreviewAvatar, null, null, null, "Current avatar", isCurrentPreviewFallback: true);
            }
        }

        if (assetsManager.LazyLoading)
        {
            var avatarIdsCoveredByMesh = new HashSet<string>(StringComparer.Ordinal);
            var avatarIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByAvatar = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);

            // Fast v15 pre-indexed single-query lookup from AnimationClipTargets + batched ModelGroupMeshes lookup.
            var preIndexedTargets = LoadAnimationClipTargetsForPreview(clip);
            var groupIdsToBatch = preIndexedTargets
                .Where(t => !string.IsNullOrWhiteSpace(t.ModelGroupId))
                .Select(t => t.ModelGroupId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var batchedGroupMeshes = groupIdsToBatch.Count > 0
                ? LoadModelGroupMeshesByGroupIdsForPreview(groupIdsToBatch)
                : new Dictionary<string, List<ModelGroupMeshInfo>>(StringComparer.Ordinal);

            var hasCompletePreIndexedTarget = false;
            var addedClipLinkedCandidate = false;
            foreach (var target in preIndexedTargets)
            {
                if (!string.IsNullOrWhiteSpace(target.ModelGroupId))
                {
                    batchedGroupMeshes.TryGetValue(target.ModelGroupId, out var groupMeshes);
                    groupMeshes ??= new List<ModelGroupMeshInfo>();
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
                        addedClipLinkedCandidate = true;
                        hasCompletePreIndexedTarget = true;
                        continue;
                    }
                }

                if (string.IsNullOrWhiteSpace(target.AvatarAssetId) && string.IsNullOrWhiteSpace(target.MeshAssetId))
                {
                    continue;
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
                    hasCompletePreIndexedTarget = true;
                }

                AddCandidate(
                    null,
                    null,
                    target.AvatarAssetId,
                    target.MeshAssetId,
                    relationSource,
                    !string.IsNullOrWhiteSpace(target.AvatarName) ? target.AvatarName : null,
                    !string.IsNullOrWhiteSpace(target.MeshName) ? target.MeshName : null);
                addedClipLinkedCandidate = true;
            }

            // Only run legacy multi-query fallback when AnimationClipTargets did not already provide a complete target.
            if (!hasCompletePreIndexedTarget && CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
            {
                var avatarIds = LoadAnimationClipAvatarAssetIdsForPreview(clip);
                var avatarMeshIdsByAvatarId = LoadAvatarMeshAssetIdsByAvatarIdsForPreview(avatarIds);
                var modelGroupsByAvatarId = LoadModelGroupsByAvatarIdsForPreview(avatarIds);

                var legacyGroupIds = modelGroupsByAvatarId.Values
                    .SelectMany(list => list.Select(g => g.GroupId))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var legacyBatchedGroupMeshes = legacyGroupIds.Count > 0
                    ? LoadModelGroupMeshesByGroupIdsForPreview(legacyGroupIds)
                    : new Dictionary<string, List<ModelGroupMeshInfo>>(StringComparer.Ordinal);

                foreach (var avatarId in avatarIds)
                {
                    if (modelGroupsByAvatarId.TryGetValue(avatarId, out var modelGroups) && modelGroups.Count > 0)
                    {
                        foreach (var group in modelGroups)
                        {
                            legacyBatchedGroupMeshes.TryGetValue(group.GroupId, out var groupMeshes);
                            groupMeshes ??= new List<ModelGroupMeshInfo>();
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
                            addedClipLinkedCandidate = true;
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
                            addedClipLinkedCandidate = true;
                        }

                        continue;
                    }

                    AddCandidate(null, null, avatarId, null, "Avatar relation", GetPreviewHandleName(avatarId, "Avatar"));
                    addedClipLinkedCandidate = true;
                }

                foreach (var meshId in LoadAnimationClipMeshAssetIdsForPreview(clip))
                {
                    if (meshIdsCoveredByAvatar.Contains(meshId) || meshIdsCoveredByModelGroup.Contains(meshId))
                    {
                        continue;
                    }

                    AddCandidate(null, null, null, meshId, "Animator mesh", meshName: GetPreviewHandleName(meshId, "Mesh"));
                    addedClipLinkedCandidate = true;
                }
            }

            // On-demand local-file fallback when connections are not built yet or SQLite returned 0 usable targets.
            if (!addedClipLinkedCandidate)
            {
                TryAddLocalFallbackAnimationClipCandidates(
                    clip,
                    (avatar, mesh, avatarId, meshId, source) =>
                    {
                        AddCandidate(avatar, mesh, avatarId, meshId, source);
                        if (!string.IsNullOrWhiteSpace(avatarId) && !string.IsNullOrWhiteSpace(meshId))
                        {
                            avatarIdsCoveredByMesh.Add(avatarId);
                            meshIdsCoveredByAvatar.Add(meshId);
                        }
                    });
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

    private void TryAddLocalFallbackAnimationClipCandidates(
        AnimationClip clip,
        Action<Avatar?, Mesh?, string?, string?, string> addCandidate)
    {
        if (clip.assetsFile == null)
        {
            return;
        }

        try
        {
            using (assetsManager.LruCache.SuspendEviction())
            {
                MaterializeReferenceObjects(clip.assetsFile, AnimationLocalFallbackReferenceTypes);
            }

            var filesToInspect = new List<SerializedFile> { clip.assetsFile };
            var seenFiles = new HashSet<SerializedFile> { clip.assetsFile };
            foreach (var loadedFile in GetLoadedFilesSnapshot())
            {
                if (loadedFile != null && seenFiles.Add(loadedFile))
                {
                    filesToInspect.Add(loadedFile);
                }
            }

            var clipId = GetPreviewObjectKey(clip);
            var backfill = new SemanticAssetRelations();
            var discoveredAny = false;

            // 1. Run SemanticGraphService locally on clip.assetsFile (and loaded files) to catch same-file Controller -> Animator -> Avatar + Mesh and Legacy Animation -> Mesh.
            var localRelations = new SemanticAssetRelations();
            Services.SemanticGraphService.BuildSemanticRelationsForFilesBackground(
                new[] { clip.assetsFile },
                localRelations,
                maxDegreeOfParallelism: 1);

            foreach (var target in localRelations.AnimationClipTargets)
            {
                if (!string.Equals(target.ClipAssetId, clipId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(target.AvatarAssetId)
                    && string.IsNullOrWhiteSpace(target.MeshAssetId)
                    && string.IsNullOrWhiteSpace(target.ModelGroupId))
                {
                    continue;
                }

                addCandidate(null, null, target.AvatarAssetId, target.MeshAssetId, "Local rig");
                backfill.AnimationClipTargets.Add(target);
                discoveredAny = true;
            }

            // 2. Also link Controller -> Animator -> Avatar + Mesh when Controller and Animator are across already-loaded files or via AssetEdges in clip.assetsFile.
            if (!discoveredAny)
            {
                var controllersForClip = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in localRelations.AssetEdges)
                {
                    if (string.Equals(edge.EdgeKind, "AnimationClip", StringComparison.Ordinal)
                        && string.Equals(edge.TargetAssetId, clipId, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(edge.SourceAssetId))
                    {
                        controllersForClip.Add(edge.SourceAssetId);
                    }
                }

                if (controllersForClip.Count > 0)
                {
                    foreach (var file in filesToInspect)
                    {
                        var rels = ReferenceEquals(file, clip.assetsFile)
                            ? localRelations
                            : Services.SemanticGraphService.BuildAssetReferenceIndexForFile(file).SemanticRelations;
                        foreach (var ctrlEdge in rels.AssetEdges)
                        {
                            if (!string.Equals(ctrlEdge.EdgeKind, "AnimatorController", StringComparison.Ordinal)
                                || !controllersForClip.Contains(ctrlEdge.TargetAssetId))
                            {
                                continue;
                            }

                            var animatorId = ctrlEdge.SourceAssetId;
                            var avatarId = rels.AssetEdges
                                .FirstOrDefault(e => string.Equals(e.SourceAssetId, animatorId, StringComparison.Ordinal)
                                    && string.Equals(e.EdgeKind, "Avatar", StringComparison.Ordinal)
                                    && !string.IsNullOrWhiteSpace(e.TargetAssetId))
                                ?.TargetAssetId ?? string.Empty;
                            var meshId = rels.AssetEdges
                                .FirstOrDefault(e => string.Equals(e.SourceAssetId, animatorId, StringComparison.Ordinal)
                                    && string.Equals(e.EdgeKind, "Mesh", StringComparison.Ordinal)
                                    && !string.IsNullOrWhiteSpace(e.TargetAssetId))
                                ?.TargetAssetId ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(avatarId) || !string.IsNullOrWhiteSpace(meshId))
                            {
                                addCandidate(null, null, avatarId, meshId, "Local controller");
                                backfill.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                                    clipId,
                                    animatorId,
                                    avatarId,
                                    meshId,
                                    string.Empty,
                                    "AnimatorController",
                                    9500));
                                discoveredAny = true;
                            }
                        }
                    }
                }
            }

            // 3. On-demand bone-hash match against Avatar.m_TOS / m_AvatarSkeleton in clip.assetsFile + loaded files.
            var clipBindings = GetTransformBindingPathsBackground(clip);
            if (clipBindings.Count > 0)
            {
                var scoredAvatars = new List<(Avatar Avatar, int Overlap, bool SameFile)>();
                foreach (var file in filesToInspect)
                {
                    AssetStudio.Object[] objs;
                    lock (file)
                    {
                        objs = file.Objects.ToArray();
                    }

                    foreach (var avatar in objs.OfType<Avatar>())
                    {
                        var rigHashes = Services.SemanticGraphService.ExtractAvatarRigBoneHashes(avatar);
                        if (rigHashes.Count == 0)
                        {
                            continue;
                        }

                        var overlap = clipBindings.Count(rigHashes.Contains);
                        if (IsStrongAnimationAvatarMatch(clipBindings.Count, overlap))
                        {
                            scoredAvatars.Add((avatar, overlap, ReferenceEquals(file, clip.assetsFile)));
                        }
                    }
                }

                foreach (var match in scoredAvatars
                    .OrderByDescending(m => m.Overlap)
                    .ThenByDescending(m => m.SameFile)
                    .Take(4))
                {
                    var avatar = match.Avatar;
                    var avatarId = GetPreviewObjectKey(avatar);
                    var avatarMeshId = string.Empty;

                    // Check same-file AvatarMesh/AnimatorMesh edges first, then SkinnedMeshRenderer bone overlap.
                    if (avatar.assetsFile != null)
                    {
                        var avatarFileRels = ReferenceEquals(avatar.assetsFile, clip.assetsFile)
                            ? localRelations
                            : Services.SemanticGraphService.BuildAssetReferenceIndexForFile(avatar.assetsFile).SemanticRelations;
                        avatarMeshId = avatarFileRels.AssetEdges
                            .FirstOrDefault(e => string.Equals(e.SourceAssetId, avatarId, StringComparison.Ordinal)
                                && string.Equals(e.EdgeKind, "Mesh", StringComparison.Ordinal)
                                && !string.IsNullOrWhiteSpace(e.TargetAssetId))
                            ?.TargetAssetId ?? string.Empty;
                    }

                    addCandidate(avatar, null, avatarId, avatarMeshId, "Local bone match");
                    backfill.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        string.Empty,
                        avatarId,
                        avatarMeshId,
                        string.Empty,
                        "BoneHashMatch",
                        match.Overlap * 100,
                        match.Overlap,
                        clipBindings.Count));
                }
            }

            if (backfill.HasRelations)
            {
                EnqueueLazySemanticRelationsBackfill(backfill);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Local animation fallback failed for {clip.m_Name}: {ex.Message}");
        }
    }

    private PreviewCandidateItem? SelectAnimationClipPreviewCandidate(
        AnimationClip clip,
        IReadOnlyList<PreviewCandidateItem> candidates,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId = null,
        string? preferredMeshId = null)
    {
        return SelectBestAnimationClipCandidate(
            clip,
            candidates,
            preferredAvatar,
            preferredMesh,
            preferredAvatarId,
            preferredMeshId,
            IsAnimationClipCompatibleWithAvatar);
    }

    internal static PreviewCandidateItem? SelectBestAnimationClipCandidate(
        AnimationClip clip,
        IReadOnlyList<PreviewCandidateItem> candidates,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId,
        string? preferredMeshId,
        Func<AnimationClip, Avatar, bool>? isCompatibleWithAvatar = null)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return null;
        }

        // 1. Explicit user/preferred match
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

        static bool HasAvatar(PreviewCandidateItem c) =>
            c.Avatar != null || !string.IsNullOrWhiteSpace(c.AvatarId);
        static bool HasMesh(PreviewCandidateItem c) =>
            c.Mesh != null || !string.IsNullOrWhiteSpace(c.MeshId);

        // 2. Clip-linked ModelGroup with Avatar + Mesh (lazy IDs or loaded objects)
        var linkedModelGroupWithAvatar = candidates.FirstOrDefault(c =>
            !c.IsCurrentPreviewFallback && c.IsModelGroup && HasAvatar(c) && HasMesh(c));
        if (linkedModelGroupWithAvatar != null)
        {
            return linkedModelGroupWithAvatar;
        }

        // 3. Clip-linked Avatar + Mesh (lazy IDs or loaded objects)
        var linkedAvatarAndMesh = candidates.FirstOrDefault(c =>
            !c.IsCurrentPreviewFallback && HasAvatar(c) && HasMesh(c));
        if (linkedAvatarAndMesh != null)
        {
            return linkedAvatarAndMesh;
        }

        // 4. Clip-linked ModelGroup with Mesh
        var linkedModelGroup = candidates.FirstOrDefault(c =>
            !c.IsCurrentPreviewFallback && c.IsModelGroup && HasMesh(c));
        if (linkedModelGroup != null)
        {
            return linkedModelGroup;
        }

        // 5. Clip-linked Mesh-only
        var linkedMeshOnly = candidates.FirstOrDefault(c =>
            !c.IsCurrentPreviewFallback && HasMesh(c));
        if (linkedMeshOnly != null)
        {
            return linkedMeshOnly;
        }

        // 6. Current preview Avatar + Mesh ONLY if bone-compatible with clip
        if (isCompatibleWithAvatar != null)
        {
            var compatibleCurrent = candidates.FirstOrDefault(c =>
                c.IsCurrentPreviewFallback
                && c.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null
                && c.Mesh != null
                && isCompatibleWithAvatar(clip, c.Avatar));
            if (compatibleCurrent != null)
            {
                return compatibleCurrent;
            }
        }

        // 7. Clip-linked Avatar-only (lazy ID or loaded object)
        var linkedAvatarOnly = candidates.FirstOrDefault(c =>
            !c.IsCurrentPreviewFallback && HasAvatar(c));
        if (linkedAvatarOnly != null)
        {
            return linkedAvatarOnly;
        }

        // 8. Remaining non-Current fallback, then compatible Current avatar, then first available
        return candidates.FirstOrDefault(c => !c.IsCurrentPreviewFallback)
            ?? (isCompatibleWithAvatar != null
                ? candidates.FirstOrDefault(c => c.Avatar?.m_Avatar?.m_AvatarSkeleton?.m_Node != null && isCompatibleWithAvatar(clip, c.Avatar))
                : null)
            ?? candidates.FirstOrDefault(c => c.Mesh != null)
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
            ModelGroupKind = candidate.ModelGroupKind,
            ModelGroupMeshIds = candidate.ModelGroupMeshIds,
            ModelGroupMeshInfos = candidate.ModelGroupMeshInfos,
            ModelGroupMeshes = candidate.ModelGroupMeshes,
            ModelGroupTransforms = candidate.ModelGroupTransforms,
            ModelGroupMeshCount = candidate.ModelGroupMeshCount,
            ModelGroupConfidence = candidate.ModelGroupConfidence,
            IsCurrentPreviewFallback = candidate.IsCurrentPreviewFallback,
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
