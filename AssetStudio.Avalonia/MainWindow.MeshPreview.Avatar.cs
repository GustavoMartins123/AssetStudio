using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private List<PreviewCandidateItem> BuildAvatarPreviewCandidates(Avatar avatar, Mesh? preferredMesh = null, string? preferredMeshId = null)
    {
        var candidates = new List<PreviewCandidateItem>();
        var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
        var seenModelGroupIds = new HashSet<string>(StringComparer.Ordinal);
        var avatarId = GetPreviewObjectKey(avatar);

        void AddModelGroup(
            ModelGroupInfo group,
            IReadOnlyList<ModelGroupMeshInfo> groupMeshes,
            string source,
            IReadOnlyList<Mesh>? resolvedMeshes = null)
        {
            if (string.IsNullOrWhiteSpace(group.GroupId) || !seenModelGroupIds.Add(group.GroupId))
            {
                return;
            }

            var dedupedMeshes = DeduplicateModelGroupMeshInfos(groupMeshes);
            var representative = SelectRepresentativeModelGroupMesh(dedupedMeshes);
            if (representative == null)
            {
                return;
            }

            var meshIds = dedupedMeshes
                .Select(mesh => mesh.MeshAssetId)
                .Where(meshId => !string.IsNullOrWhiteSpace(meshId))
                .ToArray();
            var displayName = !string.IsNullOrWhiteSpace(group.GroupName)
                ? group.GroupName
                : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                    ? group.RootGameObjectName
                    : GetPreviewHandleName(representative.MeshAssetId, "Model");
            var representativeName = !string.IsNullOrWhiteSpace(representative.MeshName)
                ? representative.MeshName
                : GetPreviewHandleName(representative.MeshAssetId, "Mesh");
            var pathId = representative.MeshPathId != 0
                ? representative.MeshPathId.ToString(CultureInfo.InvariantCulture)
                : assetsManager.ProjectIndex.GetHandle(representative.MeshAssetId)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?";

            seenMeshIds.Add(representative.MeshAssetId);
            candidates.Add(new PreviewCandidateItem
            {
                Avatar = avatar,
                Mesh = resolvedMeshes?.FirstOrDefault(m => string.Equals(GetPreviewObjectKey(m), representative.MeshAssetId, StringComparison.Ordinal)),
                MeshId = representative.MeshAssetId,
                AvatarId = avatarId,
                ModelGroupId = group.GroupId,
                ModelGroupName = displayName,
                ModelGroupKind = group.GroupKind,
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = dedupedMeshes.ToArray(),
                ModelGroupMeshes = resolvedMeshes ?? Array.Empty<Mesh>(),
                ModelGroupTransforms = dedupedMeshes.Select(mesh => mesh.TransformMatrix).ToArray(),
                ModelGroupMeshCount = meshIds.Length,
                ModelGroupConfidence = group.Confidence,
                Label = $"{source}: {displayName} ({meshIds.Length:N0} parts) -> {representativeName} (Mesh PathID: {pathId})"
            });
        }

        void AddMesh(Mesh? mesh, string? meshId, string source, string? name = null)
        {
            meshId = !string.IsNullOrWhiteSpace(meshId) ? meshId : GetPreviewObjectKey(mesh);
            if (string.IsNullOrWhiteSpace(meshId) || !seenMeshIds.Add(meshId))
            {
                return;
            }

            var displayName = !string.IsNullOrWhiteSpace(name)
                ? name!
                : !string.IsNullOrWhiteSpace(mesh?.m_Name)
                    ? mesh!.m_Name
                    : GetPreviewHandleName(meshId, "Mesh");

            candidates.Add(new PreviewCandidateItem
            {
                Avatar = avatar,
                Mesh = mesh,
                MeshId = meshId,
                Label = $"{source}: {displayName} (Mesh PathID: {mesh?.m_PathID.ToString(CultureInfo.InvariantCulture) ?? assetsManager.ProjectIndex.GetHandle(meshId)?.PathID.ToString(CultureInfo.InvariantCulture) ?? "?"})"
            });
        }

        AddMesh(preferredMesh, preferredMeshId, "Selected");

        if (!string.IsNullOrWhiteSpace(avatarId))
        {
            foreach (var group in LoadModelGroupsByAvatarIdsForPreview(new[] { avatarId }).GetValueOrDefault(avatarId) ?? new List<ModelGroupInfo>())
            {
                AddModelGroup(group, LoadModelGroupMeshesForPreview(group.GroupId), "Model group");
            }
        }

        if (seenModelGroupIds.Count == 0)
        {
            foreach (var eagerGroup in BuildEagerAvatarModelGroups(avatar))
            {
                AddModelGroup(eagerGroup.Group, eagerGroup.Meshes, "Model group", eagerGroup.ResolvedMeshes);
            }
        }

        Mesh? cachedMesh = null;
        List<Mesh>? cachedMeshes = null;
        lock (previewCacheLock)
        {
            if (avatarMeshCache != null)
            {
                avatarMeshCache.TryGetValue(avatar, out cachedMesh);
            }

            if (avatarMeshesCache != null && avatarMeshesCache.TryGetValue(avatar, out var foundMeshes))
            {
                cachedMeshes = new List<Mesh>(foundMeshes);
            }
        }

        if (cachedMesh != null)
        {
            AddMesh(cachedMesh, null, "Loaded");
        }

        if (cachedMeshes != null)
        {
            foreach (var mesh in cachedMeshes)
            {
                AddMesh(mesh, null, "Loaded");
            }
        }

        foreach (var meshId in LoadAvatarMeshAssetIdsForPreview(avatar))
        {
            AddMesh(null, meshId, "Cached relation", GetPreviewHandleName(meshId, "Mesh"));
        }

        if (candidates.Count == 0 && !assetsManager.LazyLoading)
        {
            foreach (var mesh in FindMeshesForAvatar(avatar))
            {
                AddMesh(mesh, null, "Loaded");
            }
        }

        return candidates;
    }

    private Mesh? SelectAvatarPreviewMesh(IReadOnlyList<PreviewCandidateItem> candidates, Mesh? preferredMesh, string? preferredMeshId)
    {
        if (preferredMesh != null)
        {
            return preferredMesh;
        }

        if (!string.IsNullOrWhiteSpace(preferredMeshId))
        {
            return ResolveMeshPreviewCandidate(preferredMeshId);
        }

        var loadedCandidate = candidates.FirstOrDefault(x => x.Mesh != null);
        if (loadedCandidate?.Mesh != null)
        {
            return loadedCandidate.Mesh;
        }

        var idCandidate = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.MeshId));
        if (idCandidate != null)
        {
            var mesh = ResolveMeshPreviewCandidate(idCandidate.MeshId);
            if (mesh != null)
            {
                return mesh;
            }
        }

        return null;
    }

    private void QueueAvatarPreviewCandidateBuild(
        Avatar avatar,
        Mesh? preferredMesh,
        string? preferredMeshId,
        PreviewCandidateItem? selectedCandidateOverride)
    {
        var token = _animationPreviewCoordinator.BeginNewRequest(out var requestId);
        var previewId = texturePreviewIdCounter;
        var selectedAsset = AssetListDataGrid.SelectedItem;
        var densityPercent = GLPreviewControl?.PreviewMeshDensityPercent
            ?? (float)Math.Clamp(appSettings.AvatarPreviewMeshDensityPercent, MinAvatarPreviewMeshDensityPercent, MaxAvatarPreviewMeshDensityPercent);
        var boneScale = GLPreviewControl?.BoneScale
            ?? (float)Math.Clamp(appSettings.AvatarPreviewBoneScale, MinAvatarPreviewBoneScale, MaxAvatarPreviewBoneScale);
        var includeInfo = ViewModel.Options.DisplayInfo;
        var showFullModelGroup = MeshPreview.ShowFullModelGroup;
        StatusStripUpdate("Loading avatar relations & preparing rig...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var candidates = BuildAvatarPreviewCandidates(avatar, preferredMesh, preferredMeshId);
                token.ThrowIfCancellationRequested();

                var activeCandidate = selectedCandidateOverride != null
                    ? candidates.FirstOrDefault(x =>
                        (!string.IsNullOrWhiteSpace(selectedCandidateOverride.ModelGroupId)
                            && string.Equals(x.ModelGroupId, selectedCandidateOverride.ModelGroupId, StringComparison.Ordinal))
                        || (!string.IsNullOrWhiteSpace(selectedCandidateOverride.MeshId)
                            && !selectedCandidateOverride.IsModelGroup
                            && !x.IsModelGroup
                            && string.Equals(x.MeshId, selectedCandidateOverride.MeshId, StringComparison.Ordinal))
                        || (!selectedCandidateOverride.IsModelGroup
                            && !x.IsModelGroup
                            && AreSamePreviewObject(x.Mesh, selectedCandidateOverride.Mesh)))
                        ?? selectedCandidateOverride
                    : candidates.FirstOrDefault(x => x.IsModelGroup) ?? candidates.FirstOrDefault();

                var explicitSingleMeshRequested = selectedCandidateOverride != null
                    && !selectedCandidateOverride.IsModelGroup;

                var avatarMesh = SelectAvatarPreviewMesh(candidates, preferredMesh, preferredMeshId);
                token.ThrowIfCancellationRequested();

                avatarMesh?.EnsureProcessed();
                if (avatarMesh != null)
                {
                    CacheAvatarMeshRelation(avatar, avatarMesh);
                }

                MeshPreviewControl.PreparedAvatarPreviewGLData? preparedGl = null;
                List<CompositePreviewMaterialPart>? materialParts = null;
                var boneJointCount = 0;
                var availableGroupPartCount = 0;

                if (MeshPreviewSkeletonBuilder.TryBuildAvatarSkeletonFromDefaultPose(avatar, out var defaultSkeleton))
                {
                    token.ThrowIfCancellationRequested();
                    var includeAllParts = showFullModelGroup && !explicitSingleMeshRequested;
                    var partSpecs = BuildCompositeAvatarPartSpecsForCandidate(
                        avatar,
                        defaultSkeleton,
                        activeCandidate,
                        avatarMesh,
                        includeAllParts,
                        token,
                        out var primaryPartMesh,
                        out availableGroupPartCount);

                    if (primaryPartMesh != null)
                    {
                        avatarMesh = primaryPartMesh;
                        CacheAvatarMeshRelation(avatar, avatarMesh);
                    }

                    token.ThrowIfCancellationRequested();
                    if (partSpecs.Count > 0)
                    {
                        var useCompositePipeline = partSpecs.Count > 1
                            || !partSpecs[0].IsSkinned
                            || defaultSkeleton.RestWorldMatrices.Length > 0;
                        if (useCompositePipeline)
                        {
                            preparedGl = MeshPreviewControl.PrepareCompositeStaticAvatarGLData(
                                partSpecs,
                                defaultSkeleton.RestWorldPositions,
                                defaultSkeleton.RestWorldMatrices,
                                defaultSkeleton.ParentIndices,
                                defaultSkeleton.NodeNames,
                                densityPercent,
                                boneScale);
                            boneJointCount = defaultSkeleton.NodeCount;
                            materialParts = BuildCompositeMaterialPartsFromGlData(preparedGl, avatarMesh);
                        }
                    }
                    else if (defaultSkeleton.NodeCount >= 2)
                    {
                        // Render 3D skeleton directly even when an Avatar has 0 resolved meshes.
                        preparedGl = MeshPreviewControl.PrepareSkeletonOnlyAvatarGLData(
                            defaultSkeleton.RestWorldPositions,
                            defaultSkeleton.ParentIndices,
                            defaultSkeleton.NodeNames,
                            boneScale);
                        boneJointCount = defaultSkeleton.NodeCount;
                    }
                }

                if (preparedGl == null && avatarMesh != null)
                {
                    global::OpenTK.Mathematics.Vector3[]? bonePositions = null;
                    int[]? parentIndices = null;
                    string[]? boneNames = null;

                    var skinnedRenderer = FindSkinnedRendererForAvatarMesh(avatar, avatarMesh);
                    if (skinnedRenderer == null
                        && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                    {
                        EnsureMeshPreviewDependenciesLoaded(avatarMesh);
                        skinnedRenderer = FindSkinnedRendererForAvatarMesh(avatar, avatarMesh);
                    }

                    if (skinnedRenderer != null
                        && TryBuildSkeletonFromSkinnedRenderer(avatar, avatarMesh, skinnedRenderer, out var rendererBonePositions, out var rendererParentIndices, out var rendererBoneNames))
                    {
                        bonePositions = rendererBonePositions;
                        parentIndices = rendererParentIndices;
                        boneNames = rendererBoneNames;
                    }

                    if (bonePositions == null
                        && MeshPreviewSkeletonBuilder.TryBuildAvatarMeshSkeleton(avatar, avatarMesh, out var avatarSkeleton))
                    {
                        bonePositions = avatarSkeleton.RestBonePositions;
                        parentIndices = avatarSkeleton.MeshParentIndices;
                        boneNames = avatarSkeleton.MeshBoneNames;
                    }

                    token.ThrowIfCancellationRequested();
                    if (bonePositions != null && parentIndices != null)
                    {
                        preparedGl = MeshPreviewControl.PrepareStaticAvatarGLData(
                            avatarMesh,
                            bonePositions,
                            parentIndices,
                            boneNames,
                            densityPercent,
                            boneScale);
                        boneJointCount = bonePositions.Length;
                        materialParts = BuildCompositeMaterialPartsFromGlData(preparedGl, avatarMesh);
                    }
                }

                if (preparedGl != null)
                {
                    var depAssets = new List<AssetStudio.Object?> { avatar, avatarMesh };
                    if (preparedGl.CompositeParts != null)
                    {
                        foreach (var p in preparedGl.CompositeParts)
                        {
                            depAssets.Add(p.Mesh);
                        }
                    }
                    SetActivePreviewDependencyFiles(depAssets.ToArray());
                }

                var infoText = includeInfo && preparedGl != null
                    ? BuildAvatarPreviewInfoText(avatar, candidates, avatarMesh, preparedGl.PartCount, boneJointCount)
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested
                        || !_animationPreviewCoordinator.IsCurrent(requestId)
                        || previewId != texturePreviewIdCounter
                        || !ReferenceEquals(AssetListDataGrid.SelectedItem, selectedAsset))
                    {
                        return;
                    }

                    ApplyPreparedAvatarPreviewOnUI(
                        avatar,
                        avatarMesh,
                        candidates,
                        activeCandidate,
                        preparedGl,
                        materialParts,
                        boneJointCount,
                        availableGroupPartCount,
                        infoText);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Avatar relation query failed for {avatar.m_Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested
                        && _animationPreviewCoordinator.IsCurrent(requestId)
                        && previewId == texturePreviewIdCounter
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, selectedAsset))
                    {
                        ApplyPreparedAvatarPreviewOnUI(
                            avatar,
                            preferredMesh,
                            Array.Empty<PreviewCandidateItem>(),
                            selectedCandidateOverride,
                            null,
                            null,
                            0,
                            0,
                            string.Empty);
                    }
                });
            }
        }, token);
    }

    private void PreviewAvatar(
        Avatar avatar,
        Mesh? preferredMesh = null,
        string? preferredMeshId = null,
        PreviewCandidateItem? selectedCandidateOverride = null,
        bool rebuildCandidateControls = true,
        IReadOnlyList<PreviewCandidateItem>? preparedCandidates = null)
    {
        QueueAvatarPreviewCandidateBuild(avatar, preferredMesh, preferredMeshId, selectedCandidateOverride);
    }

    private void ApplyPreparedAvatarPreviewOnUI(
        Avatar avatar,
        Mesh? avatarMesh,
        IReadOnlyList<PreviewCandidateItem> avatarCandidates,
        PreviewCandidateItem? selectedCandidateOverride,
        MeshPreviewControl.PreparedAvatarPreviewGLData? preparedGl,
        IReadOnlyList<CompositePreviewMaterialPart>? materialParts,
        int boneJointCount,
        int availableGroupPartCount,
        string infoText)
    {
        currentPreviewAvatar = avatar;
        currentPreviewMesh = avatarMesh;
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Avatar,
            AssetListDataGrid?.SelectedItem as AssetItem,
            metadataSummary: infoText,
            isLoading: false);
        MeshPreview.SetMeshAndRigSummary(
            meshSummary: null,
            rigSummary: infoText,
            meshName: avatarMesh?.m_Name,
            avatarName: avatar.m_Name);
        var selectedMeshId = GetPreviewObjectKey(avatarMesh);
        var selectedCandidate = selectedCandidateOverride == null
            ? null
            : avatarCandidates.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(selectedCandidateOverride.ModelGroupId)
                    && string.Equals(x.ModelGroupId, selectedCandidateOverride.ModelGroupId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(selectedCandidateOverride.MeshId)
                    && string.Equals(x.MeshId, selectedCandidateOverride.MeshId, StringComparison.Ordinal))
                || AreSamePreviewObject(x.Mesh, selectedCandidateOverride.Mesh));
        selectedCandidate ??= avatarCandidates.FirstOrDefault(x =>
            AreSamePreviewObject(x.Mesh, avatarMesh)
            || (!string.IsNullOrWhiteSpace(x.MeshId)
                && !string.IsNullOrWhiteSpace(selectedMeshId)
                && string.Equals(x.MeshId, selectedMeshId, StringComparison.Ordinal)));
        BuildPreviewCandidateControls(avatarCandidates, selectedCandidate, "Avatar Mesh");
        UpdateModelGroupToggleContext(avatarCandidates, selectedCandidate, availableGroupPartCount);

        if (preparedGl != null && GLPreviewControl != null)
        {
            ClearMeshMaterialControls();
            GLPreviewControl.ApplyPreparedAvatarPreview(preparedGl);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            if (materialParts != null && materialParts.Count > 0)
            {
                QueueLoadMaterialsForUnifiedMeshPreviewAsync(
                    materialParts,
                    avatarMesh,
                    texturePreviewIdCounter,
                    AssetListDataGrid?.SelectedItem as AssetItem);
            }
            else if (avatarMesh != null)
            {
                QueueLoadMaterialsForUnifiedMeshPreviewAsync(
                    avatarMesh,
                    texturePreviewIdCounter,
                    AssetListDataGrid?.SelectedItem as AssetItem);
            }
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            var statusSummary = preparedGl.IsSkeletonOnly
                ? $"OpenGL Avatar Preview | Skeleton only ({avatar.m_Name}) | Skeleton Joints: {boneJointCount}"
                : preparedGl.PartCount > 1
                    ? $"OpenGL Avatar Preview | Full Object ({preparedGl.PartCount:N0} parts) | Primary: {avatarMesh?.m_Name} | Skeleton Joints: {boneJointCount}"
                    : $"OpenGL Avatar Preview | Mesh: {avatarMesh?.m_Name} | Related meshes: {avatarCandidates.Count} | Skeleton Joints: {boneJointCount}";
            StatusStripUpdate(statusSummary);
            return;
        }

        if (GLPreviewControl != null)
        {
            GLPreviewControl.IsVisible = false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("=========================================");
        sb.AppendLine($"AVATAR: {avatar.m_Name}");
        sb.AppendLine("=========================================");
        sb.AppendLine();
        sb.AppendLine($"Avatar Size: {avatar.m_AvatarSize} bytes");
        sb.AppendLine();
        if (avatarCandidates.Count > 0)
        {
            sb.AppendLine($"Related Meshes ({avatarCandidates.Count}):");
            foreach (var candidate in avatarCandidates.Take(50))
            {
                sb.AppendLine($"  - {candidate.Label}");
            }

            if (avatarCandidates.Count > 50)
            {
                sb.AppendLine($"  ... {avatarCandidates.Count - 50} more");
            }

            sb.AppendLine();
        }

        if (avatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
        {
            sb.AppendLine("Skeleton Nodes Hierarchy:");
            var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
            for (int i = 0; i < skeleton.m_Node.Length; i++)
            {
                var node = skeleton.m_Node[i];
                string name = "Unknown";
                if (skeleton.m_ID != null && i < skeleton.m_ID.Length)
                {
                    name = avatar.FindBonePath(skeleton.m_ID[i]);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = $"Hash_{skeleton.m_ID[i]}";
                    }
                }
                sb.AppendLine($"  [{i}] Node: \"{name}\" (Parent ID: {node.m_ParentId}, Axes ID: {node.m_AxesId})");
            }
        }
        else
        {
            sb.AppendLine("Skeleton nodes are not defined or parsed.");
        }

        var fallbackReport = sb.ToString();
        PreviewSession.ShowTextPreview(fallbackReport, PreviewSurfaceKind.Avatar, AssetListDataGrid?.SelectedItem as AssetItem);
        MeshPreview.RigSummaryText = fallbackReport;
        SetTextWithTruncation(TextPreviewBox, fallbackReport);
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
    }

    private static string BuildAvatarPreviewInfoText(
        Avatar avatar,
        IReadOnlyList<PreviewCandidateItem> relatedMeshes,
        Mesh? selectedMesh,
        int renderedPartCount = 0,
        int skeletonJointCount = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Avatar: {avatar.m_Name} (PathID: {avatar.m_PathID})");
        sb.AppendLine(new string('=', 42));
        sb.AppendLine($"Avatar Size: {avatar.m_AvatarSize} bytes");
        if (skeletonJointCount > 0)
        {
            sb.AppendLine($"Skeleton Joints: {skeletonJointCount}");
        }

        if (renderedPartCount > 1)
        {
            sb.AppendLine($"Rendered Parts: {renderedPartCount}");
        }

        if (selectedMesh != null)
        {
            sb.AppendLine($"Preview Mesh: {selectedMesh.m_Name} (PathID: {selectedMesh.m_PathID})");
        }
        else if (renderedPartCount == 0 && skeletonJointCount > 0)
        {
            sb.AppendLine("Preview Mode: 3D Skeleton Only (no linked meshes resolved)");
        }

        sb.AppendLine($"Related Meshes ({relatedMeshes.Count}):");
        foreach (var candidate in relatedMeshes.Take(12))
        {
            sb.AppendLine($"- {candidate.Label}");
        }

        if (relatedMeshes.Count > 12)
        {
            sb.AppendLine($"... {relatedMeshes.Count - 12} more");
        }

        return sb.ToString();
    }
}
