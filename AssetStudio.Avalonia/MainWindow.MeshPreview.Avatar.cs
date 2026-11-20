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

        void AddModelGroup(ModelGroupInfo group, IReadOnlyList<ModelGroupMeshInfo> groupMeshes, string source)
        {
            if (string.IsNullOrWhiteSpace(group.GroupId) || !seenModelGroupIds.Add(group.GroupId))
            {
                return;
            }

            var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
            if (representative == null)
            {
                return;
            }

            var meshIds = groupMeshes
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
                MeshId = representative.MeshAssetId,
                AvatarId = avatarId,
                ModelGroupId = group.GroupId,
                ModelGroupName = displayName,
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = groupMeshes.ToArray(),
                ModelGroupTransforms = groupMeshes.Select(mesh => mesh.TransformMatrix).ToArray(),
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
        var includeInfo = displayInfo.IsChecked == true;
        StatusStripUpdate("Loading avatar relations & preparing rig...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var candidates = BuildAvatarPreviewCandidates(avatar, preferredMesh, preferredMeshId);
                token.ThrowIfCancellationRequested();
                var avatarMesh = SelectAvatarPreviewMesh(candidates, preferredMesh, preferredMeshId);
                token.ThrowIfCancellationRequested();

                avatarMesh?.EnsureProcessed();
                if (avatarMesh != null)
                {
                    CacheAvatarMeshRelation(avatar, avatarMesh);
                }

                global::OpenTK.Mathematics.Vector3[]? bonePositions = null;
                int[]? parentIndices = null;
                string[]? boneNames = null;

                var skinnedRenderer = avatarMesh != null ? FindSkinnedRendererForAvatarMesh(avatar, avatarMesh) : null;
                if (avatarMesh != null
                    && skinnedRenderer == null
                    && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                {
                    EnsureMeshPreviewDependenciesLoaded(avatarMesh);
                    skinnedRenderer = FindSkinnedRendererForAvatarMesh(avatar, avatarMesh);
                }

                if (avatarMesh != null
                    && skinnedRenderer != null
                    && TryBuildSkeletonFromSkinnedRenderer(avatar, avatarMesh, skinnedRenderer, out var rendererBonePositions, out var rendererParentIndices, out var rendererBoneNames))
                {
                    bonePositions = rendererBonePositions;
                    parentIndices = rendererParentIndices;
                    boneNames = rendererBoneNames;
                }

                if (bonePositions == null
                    && avatarMesh != null
                    && MeshPreviewSkeletonBuilder.TryBuildAvatarMeshSkeleton(avatar, avatarMesh, out var avatarSkeleton))
                {
                    bonePositions = avatarSkeleton.RestBonePositions;
                    parentIndices = avatarSkeleton.MeshParentIndices;
                    boneNames = avatarSkeleton.MeshBoneNames;
                }

                token.ThrowIfCancellationRequested();
                MeshPreviewControl.PreparedAvatarPreviewGLData? preparedGl = null;
                if (avatarMesh != null && bonePositions != null && parentIndices != null)
                {
                    preparedGl = MeshPreviewControl.PrepareStaticAvatarGLData(
                        avatarMesh,
                        bonePositions,
                        parentIndices,
                        boneNames,
                        densityPercent,
                        boneScale);
                }

                var infoText = includeInfo && avatarMesh != null && preparedGl != null
                    ? BuildAvatarPreviewInfoText(avatar, candidates, avatarMesh)
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
                        selectedCandidateOverride,
                        preparedGl,
                        bonePositions?.Length ?? 0,
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
        int boneJointCount,
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

        if (avatarMesh != null && preparedGl != null && GLPreviewControl != null)
        {
            GLPreviewControl.ApplyPreparedAvatarPreview(preparedGl);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            if (displayInfo.IsChecked == true && !string.IsNullOrEmpty(infoText) && PreviewInfoBorder != null && PreviewInfoOverlay != null)
            {
                PreviewInfoOverlay.Text = infoText;
                PreviewInfoBorder.IsVisible = true;
            }
            StatusStripUpdate($"OpenGL Avatar Preview | Mesh: {avatarMesh.m_Name} | Related meshes: {avatarCandidates.Count} | Skeleton Joints: {boneJointCount}");
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

    private static string BuildAvatarPreviewInfoText(Avatar avatar, IReadOnlyList<PreviewCandidateItem> relatedMeshes, Mesh? selectedMesh)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Avatar: {avatar.m_Name} (PathID: {avatar.m_PathID})");
        sb.AppendLine(new string('=', 42));
        sb.AppendLine($"Avatar Size: {avatar.m_AvatarSize} bytes");
        if (selectedMesh != null)
        {
            sb.AppendLine($"Preview Mesh: {selectedMesh.m_Name} (PathID: {selectedMesh.m_PathID})");
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
