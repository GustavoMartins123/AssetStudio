using Avalonia.Threading;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void QueueMeshPreviewCandidateBuild(
        AssetItem assetItem,
        Mesh mesh,
        PreviewCandidateItem? selectedCandidate,
        bool preferModelGroup)
    {
        var previewId = texturePreviewIdCounter;
        StatusStripUpdate("Loading model relations...");

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = BuildMeshPreviewCandidates(mesh);
                var activeCandidate = SelectMeshPreviewCandidate(mesh, candidates, selectedCandidate, preferModelGroup);
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId != texturePreviewIdCounter
                        || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        return;
                    }

                    BuildPreviewCandidateControls(candidates, activeCandidate, "Model Group");
                    UpdateModelGroupToggleContext(candidates, activeCandidate);
                    if (activeCandidate?.IsModelGroup == true)
                    {
                        QueuePreviewCandidateSelection(activeCandidate);
                        return;
                    }

                    PreviewMesh(
                        assetItem,
                        mesh,
                        rebuildCandidateControls: false,
                        selectedCandidate: activeCandidate,
                        preferModelGroup: preferModelGroup);
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Model relation query failed for {assetItem.Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId == texturePreviewIdCounter
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        ClearPreviewCandidateControls();
                        PreviewMesh(
                            assetItem,
                            mesh,
                            rebuildCandidateControls: false,
                            selectedCandidate: selectedCandidate,
                            preferModelGroup: preferModelGroup);
                    }
                });
            }
        });
    }

    private List<PreviewCandidateItem> BuildMeshPreviewCandidates(Mesh mesh)
    {
        var candidates = new List<PreviewCandidateItem>();
        var meshId = GetPreviewObjectKey(mesh);
        var currentLabel = !string.IsNullOrWhiteSpace(mesh.m_Name)
            ? mesh.m_Name
            : GetPreviewHandleName(meshId, "Mesh");
        candidates.Add(new PreviewCandidateItem
        {
            Mesh = mesh,
            MeshId = meshId,
            Label = $"Current mesh: {currentLabel} (Mesh PathID: {mesh.m_PathID})"
        });

        if (string.IsNullOrWhiteSpace(meshId))
        {
            return candidates;
        }

        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenGroupPartSignatures = new HashSet<string>(StringComparer.Ordinal);

        // Prefer v15 pre-computed CompositeAssets / CompositeAssetParts (fast WAL lookup).
        var composites = LoadCompositeAssetsForMeshForPreview(meshId);
        foreach (var composite in composites)
        {
            if (string.IsNullOrWhiteSpace(composite.CompositeAssetId) || !seenGroups.Add(composite.CompositeAssetId))
            {
                continue;
            }

            var groupMeshes = FilterActivePreviewModelGroupParts(
                composite.Parts
                    .Select(p => new ModelGroupMeshInfo(
                        composite.CompositeAssetId,
                        p.MeshAssetId,
                        p.RendererAssetId,
                        p.RendererType,
                        p.GameObjectAssetId,
                        p.GameObjectName,
                        p.PartIndex,
                        p.Confidence,
                        string.Empty,
                        p.TransformMatrix,
                        p.MeshName,
                        p.MeshPathId,
                        p.MeshByteSize,
                        (int)ClassIDType.Mesh))
                    .ToList(),
                meshId);
            var meshIds = groupMeshes
                .Select(item => item.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            if (meshIds.Length < 2)
            {
                continue;
            }

            var partSignature = string.Join("\u001f", groupMeshes.Select(part => $"{part.MeshAssetId}:{part.RendererAssetId}:{part.SlotIndex}"));
            if (!seenGroupPartSignatures.Add(partSignature))
            {
                continue;
            }

            var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
            var representativeMeshId = representative?.MeshAssetId ?? meshId;
            var groupName = !string.IsNullOrWhiteSpace(composite.DisplayName)
                ? composite.DisplayName
                : GetPreviewHandleName(representativeMeshId, "Model");
            var representativeName = !string.IsNullOrWhiteSpace(representative?.MeshName)
                ? representative!.MeshName
                : GetPreviewHandleName(representativeMeshId, "Mesh");

            candidates.Add(new PreviewCandidateItem
            {
                Mesh = string.Equals(representativeMeshId, meshId, StringComparison.Ordinal) ? mesh : null,
                MeshId = representativeMeshId,
                ModelGroupId = composite.CompositeAssetId,
                ModelGroupName = groupName,
                ModelGroupKind = composite.CompositeKind,
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = groupMeshes.ToArray(),
                ModelGroupTransforms = groupMeshes.Select(part => part.TransformMatrix).ToArray(),
                ModelGroupMeshCount = meshIds.Length,
                ModelGroupConfidence = composite.Confidence,
                Label = $"Model group: {groupName} ({meshIds.Length:N0} parts) -> {representativeName}"
            });
        }

        if (composites.Count == 0)
        {
            foreach (var groupCandidate in LoadModelGroupCandidatesForMeshAssetIdForPreview(meshId))
            {
                var group = groupCandidate.Group;
                if (string.IsNullOrWhiteSpace(group.GroupId) || !seenGroups.Add(group.GroupId))
                {
                    continue;
                }

                var groupMeshes = FilterActivePreviewModelGroupParts(groupCandidate.Meshes, meshId);
                var meshIds = groupMeshes
                    .Select(item => item.MeshAssetId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToArray();
                if (meshIds.Length < 2)
                {
                    continue;
                }

                var partSignature = string.Join("\u001f", groupMeshes.Select(part => $"{part.MeshAssetId}:{part.RendererAssetId}:{part.SlotIndex}"));
                if (!seenGroupPartSignatures.Add(partSignature))
                {
                    continue;
                }

                var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
                var representativeMeshId = representative?.MeshAssetId ?? meshId;
                var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
                    ? group.GroupName
                    : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                        ? group.RootGameObjectName
                        : GetPreviewHandleName(representativeMeshId, "Model");
                var representativeName = !string.IsNullOrWhiteSpace(representative?.MeshName)
                    ? representative!.MeshName
                    : GetPreviewHandleName(representativeMeshId, "Mesh");

                candidates.Add(new PreviewCandidateItem
                {
                    Mesh = string.Equals(representativeMeshId, meshId, StringComparison.Ordinal) ? mesh : null,
                    MeshId = representativeMeshId,
                    ModelGroupId = group.GroupId,
                    ModelGroupName = groupName,
                    ModelGroupKind = group.GroupKind,
                    ModelGroupMeshIds = meshIds,
                    ModelGroupMeshInfos = groupMeshes.ToArray(),
                    ModelGroupTransforms = groupMeshes.Select(part => part.TransformMatrix).ToArray(),
                    ModelGroupMeshCount = meshIds.Length,
                    ModelGroupConfidence = group.Confidence,
                    Label = $"Model group: {groupName} ({meshIds.Length:N0} parts) -> {representativeName}"
                });
            }
        }

        // Local SerializedFile fallback when SQLite semantic relations are unavailable or still building
        // (e.g. single-file loads or clicking a multi-SMR character mesh before connections finish).
        if (candidates.Count <= 1 && mesh.assetsFile != null)
        {
            TryAddLocalSerializedFileModelGroupCandidates(
                mesh,
                meshId,
                candidates,
                seenGroups,
                seenGroupPartSignatures);
        }

        return candidates;
    }

    internal static List<ModelGroupMeshInfo> FilterActivePreviewModelGroupParts(
        IReadOnlyList<ModelGroupMeshInfo> infos,
        string? selectedMeshId = null)
    {
        if (infos == null || infos.Count == 0)
        {
            return new List<ModelGroupMeshInfo>();
        }

        var hasHighConfidenceActive = false;
        var hasExplicitInactiveMarker = false;
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            if (!string.IsNullOrEmpty(info.ConfidenceReason)
                && info.ConfidenceReason.Contains("(inactive)", StringComparison.OrdinalIgnoreCase))
            {
                hasExplicitInactiveMarker = true;
            }
            else if (info.Confidence >= 80)
            {
                hasHighConfidenceActive = true;
            }
        }

        if (!hasExplicitInactiveMarker && !hasHighConfidenceActive)
        {
            return infos.ToList();
        }

        bool IsInactivePart(ModelGroupMeshInfo info)
        {
            if (!string.IsNullOrEmpty(info.ConfidenceReason)
                && info.ConfidenceReason.Contains("(inactive)", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.IsNullOrEmpty(info.ConfidenceReason)
                && hasHighConfidenceActive
                && info.Confidence > 0
                && info.Confidence <= 40;
        }

        var filtered = new List<ModelGroupMeshInfo>(infos.Count);
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            var isSelected = !string.IsNullOrWhiteSpace(selectedMeshId)
                && string.Equals(info.MeshAssetId, selectedMeshId, StringComparison.Ordinal);
            if (isSelected || !IsInactivePart(info))
            {
                filtered.Add(info);
            }
        }

        return filtered.Count > 0 ? filtered : infos.ToList();
    }

    private void EnsureLocalHierarchyObjectsLoadedForFile(SerializedFile? sourceFile)
    {
        if (sourceFile == null || !assetsManager.LazyLoading || assetsManager.ProjectIndex == null)
        {
            return;
        }

        var handles = assetsManager.ProjectIndex.GetHandlesForFile(sourceFile.fileName);
        if (handles == null)
        {
            return;
        }

        foreach (var handle in handles)
        {
            if (handle == null || handle.RealObject != null)
            {
                continue;
            }

            if (handle.Type != ClassIDType.GameObject
                && handle.Type != ClassIDType.Transform
                && handle.Type != ClassIDType.Animator
                && handle.Type != ClassIDType.SkinnedMeshRenderer
                && handle.Type != ClassIDType.MeshRenderer
                && handle.Type != ClassIDType.MeshFilter)
            {
                continue;
            }

            if (handle.SourceFile?.reader == null)
            {
                handle.SourceFile = sourceFile;
            }

            try
            {
                assetsManager.ResolveHandle(handle);
            }
            catch
            {
                // Best-effort local hierarchy resolution
            }
        }
    }

    private void TryAddLocalSerializedFileModelGroupCandidates(
        Mesh mesh,
        string meshId,
        List<PreviewCandidateItem> candidates,
        HashSet<string> seenGroups,
        HashSet<string> seenGroupPartSignatures)
    {
        var sourceFile = mesh.assetsFile;
        if (sourceFile == null)
        {
            return;
        }

        EnsureLocalHierarchyObjectsLoadedForFile(sourceFile);
        var localRelations = Services.SemanticGraphService.BuildAssetReferenceIndexForFile(sourceFile).SemanticRelations;
        if (localRelations.ModelGroups.Count == 0 || localRelations.ModelGroupMeshes.Count == 0)
        {
            return;
        }

        var meshesByGroupId = localRelations.ModelGroupMeshes
            .GroupBy(m => m.GroupId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var group in localRelations.ModelGroups
            .OrderByDescending(g => string.Equals(g.GroupKind, "Animator", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(g => g.Confidence))
        {
            if (string.IsNullOrWhiteSpace(group.GroupId)
                || !seenGroups.Add(group.GroupId)
                || !meshesByGroupId.TryGetValue(group.GroupId, out var rawParts))
            {
                continue;
            }

            if (!rawParts.Any(p => string.Equals(p.MeshAssetId, meshId, StringComparison.Ordinal)))
            {
                continue;
            }

            var mappedParts = rawParts.Select(p =>
            {
                var handle = assetsManager.ProjectIndex?.GetHandle(p.MeshAssetId);
                return new ModelGroupMeshInfo(
                    p.GroupId,
                    p.MeshAssetId,
                    p.RendererAssetId,
                    p.RendererType,
                    p.GameObjectAssetId,
                    p.GameObjectName,
                    p.SlotIndex,
                    p.Confidence,
                    p.ConfidenceReason,
                    p.LocalToWorldMatrix,
                    handle?.Name ?? (string.Equals(p.MeshAssetId, meshId, StringComparison.Ordinal) ? mesh.m_Name ?? string.Empty : string.Empty),
                    handle?.PathID ?? (string.Equals(p.MeshAssetId, meshId, StringComparison.Ordinal) ? mesh.m_PathID : 0L),
                    handle?.ByteSize ?? 0L,
                    (int)ClassIDType.Mesh);
            }).ToList();

            var groupMeshes = FilterActivePreviewModelGroupParts(mappedParts, meshId);
            var meshIds = groupMeshes
                .Select(item => item.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            if (meshIds.Length < 2)
            {
                continue;
            }

            var partSignature = string.Join("\u001f", groupMeshes.Select(part => $"{part.MeshAssetId}:{part.RendererAssetId}:{part.SlotIndex}"));
            if (!seenGroupPartSignatures.Add(partSignature))
            {
                continue;
            }

            var representative = SelectRepresentativeModelGroupMesh(groupMeshes);
            var representativeMeshId = representative?.MeshAssetId ?? meshId;
            var groupName = !string.IsNullOrWhiteSpace(group.GroupName)
                ? group.GroupName
                : !string.IsNullOrWhiteSpace(group.RootGameObjectName)
                    ? group.RootGameObjectName
                    : GetPreviewHandleName(representativeMeshId, "Model");
            var representativeName = !string.IsNullOrWhiteSpace(representative?.MeshName)
                ? representative!.MeshName
                : GetPreviewHandleName(representativeMeshId, "Mesh");

            candidates.Add(new PreviewCandidateItem
            {
                Mesh = string.Equals(representativeMeshId, meshId, StringComparison.Ordinal) ? mesh : null,
                MeshId = representativeMeshId,
                ModelGroupId = group.GroupId,
                ModelGroupName = groupName,
                ModelGroupKind = group.GroupKind,
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = groupMeshes.ToArray(),
                ModelGroupTransforms = groupMeshes.Select(part => part.TransformMatrix).ToArray(),
                ModelGroupMeshCount = meshIds.Length,
                ModelGroupConfidence = group.Confidence,
                Label = $"Model group: {groupName} ({meshIds.Length:N0} parts) -> {representativeName}"
            });
        }
    }

    private PreviewCandidateItem? SelectMeshPreviewCandidate(
        Mesh mesh,
        IReadOnlyList<PreviewCandidateItem> candidates,
        PreviewCandidateItem? selectedCandidate,
        bool preferModelGroup)
    {
        return SelectBestMeshPreviewCandidate(mesh, candidates, selectedCandidate, preferModelGroup);
    }

    private static readonly HashSet<string> GiantSceneContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SceneRoot", "Scene", "Level", "LevelRoot", "Environment", "Env",
        "World", "Terrain", "Map", "Geometry", "Static", "StaticGeometry",
        "Props", "Decor", "Scenery", "Background", "Lighting", "Stage",
        "Architecture", "Objects", "Prefabs", "Root"
    };

    private static bool IsGiantSceneContainerName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        return trimmed.StartsWith("Combined Mesh", StringComparison.OrdinalIgnoreCase)
            || GiantSceneContainerNames.Contains(trimmed);
    }

    internal static PreviewCandidateItem? SelectBestMeshPreviewCandidate(
        Mesh mesh,
        IReadOnlyList<PreviewCandidateItem> candidates,
        PreviewCandidateItem? selectedCandidate,
        bool preferModelGroup)
    {
        if (selectedCandidate != null)
        {
            var selected = candidates.FirstOrDefault(candidate =>
                selectedCandidate.IsModelGroup
                    ? string.Equals(candidate.ModelGroupId, selectedCandidate.ModelGroupId, StringComparison.Ordinal)
                    : !candidate.IsModelGroup
                        && (AreSamePreviewObject(candidate.Mesh, selectedCandidate.Mesh)
                            || (!string.IsNullOrWhiteSpace(selectedCandidate.MeshId)
                                && string.Equals(candidate.MeshId, selectedCandidate.MeshId, StringComparison.Ordinal))));
            if (selected != null)
            {
                return selected;
            }
        }

        // Auto-select compact high-confidence Animator groups (2..64 parts, covering multi-link chains)
        // and compact high-confidence SceneObject prop/armor assemblies (2..16 parts), while NEVER
        // hijacking Combined Mesh* or giant/root scene containers.
        var isCombinedSceneMesh = mesh?.m_Name != null
            && mesh.m_Name.StartsWith("Combined Mesh", StringComparison.OrdinalIgnoreCase);
        if (preferModelGroup && !isCombinedSceneMesh)
        {
            static int EffectivePartCount(PreviewCandidateItem c) =>
                Math.Max(c.ModelGroupMeshCount, Math.Max(c.ModelGroupMeshIds.Count, c.ModelGroupMeshInfos.Count));

            static bool IsAnimatorGroup(PreviewCandidateItem c) =>
                string.Equals(c.ModelGroupKind, "Animator", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(c.ModelGroupId)
                    && !c.ModelGroupId.Contains(":sceneobject:", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(c.ModelGroupKind));

            var animatorGroup = candidates.FirstOrDefault(candidate =>
            {
                var partCount = EffectivePartCount(candidate);
                return candidate.IsModelGroup
                    && partCount >= 2
                    && partCount <= 64
                    && candidate.ModelGroupConfidence >= 80
                    && IsAnimatorGroup(candidate);
            });
            if (animatorGroup != null)
            {
                return animatorGroup;
            }

            var sceneObjectGroup = candidates.FirstOrDefault(candidate =>
            {
                var partCount = EffectivePartCount(candidate);
                if (!candidate.IsModelGroup
                    || partCount < 2
                    || partCount > 16
                    || candidate.ModelGroupConfidence < 80
                    || IsGiantSceneContainerName(candidate.ModelGroupName))
                {
                    return false;
                }

                if (candidate.ModelGroupMeshInfos.Any(part =>
                    !string.IsNullOrWhiteSpace(part.MeshName)
                    && part.MeshName.StartsWith("Combined Mesh", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                return true;
            });
            if (sceneObjectGroup != null)
            {
                return sceneObjectGroup;
            }
        }

        return candidates.FirstOrDefault(candidate => !candidate.IsModelGroup && AreSamePreviewObject(candidate.Mesh, mesh))
            ?? candidates.FirstOrDefault(candidate => !candidate.IsModelGroup)
            ?? candidates.FirstOrDefault();
    }

    private List<CompositeAssetInfo> LoadCompositeAssetsForMeshForPreview(string meshId)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || string.IsNullOrWhiteSpace(meshId))
        {
            return new List<CompositeAssetInfo>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new List<CompositeAssetInfo>();
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadCompositeAssetsForMesh(folderPath, signature, meshId);
    }

    private List<ModelGroupCandidateInfo> LoadModelGroupCandidatesForMeshAssetIdForPreview(string meshId)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || string.IsNullOrWhiteSpace(meshId))
        {
            return new List<ModelGroupCandidateInfo>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<ModelGroupCandidateInfo>();
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadModelGroupCandidatesForMeshAssetId(folderPath, signature, meshId);
    }

    private Dictionary<string, List<ModelGroupInfo>> LoadModelGroupsByAvatarIdsForPreview(IReadOnlyList<string> avatarIds)
    {
        var result = new Dictionary<string, List<ModelGroupInfo>>(StringComparer.Ordinal);
        if (!assetsManager.LazyLoading || currentScanResult == null || avatarIds.Count == 0)
        {
            return result;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return result;
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        foreach (var avatarId in avatarIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var groups = _sqliteCache.LoadModelGroupsForAvatarAssetId(folderPath, signature, avatarId);
            if (groups.Count > 0)
            {
                result[avatarId] = groups;
            }
        }

        return result;
    }

    private List<ModelGroupMeshInfo> LoadModelGroupMeshesForPreview(string groupId)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || string.IsNullOrWhiteSpace(groupId))
        {
            return new List<ModelGroupMeshInfo>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<ModelGroupMeshInfo>();
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadModelGroupMeshes(folderPath, signature, groupId);
    }

    private ModelGroupInfo? LoadModelGroupByIdForPreview(string groupId)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return null;
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadModelGroupById(folderPath, signature, groupId);
    }

    internal static bool IsOverlayOrAuxiliaryModelGroupMesh(ModelGroupMeshInfo? mesh)
    {
        if (mesh == null)
        {
            return false;
        }

        return IsOverlayOrAuxiliaryMeshName(mesh.MeshName)
            || IsOverlayOrAuxiliaryMeshName(mesh.GameObjectName);
    }

    internal static bool IsOverlayOrAuxiliaryMeshName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("Decal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Shadow", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Outline", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Effect", StringComparison.OrdinalIgnoreCase);
    }

    internal static ModelGroupMeshInfo? SelectRepresentativeModelGroupMesh(IReadOnlyList<ModelGroupMeshInfo> meshes)
    {
        if (meshes == null || meshes.Count == 0)
        {
            return null;
        }

        return meshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.MeshAssetId))
            .OrderByDescending(mesh => mesh.Confidence)
            .ThenBy(mesh => IsOverlayOrAuxiliaryModelGroupMesh(mesh) ? 1 : 0)
            .ThenByDescending(mesh => mesh.MeshByteSize)
            .ThenBy(mesh => mesh.SlotIndex)
            .FirstOrDefault();
    }

    private List<Mesh> ResolveModelGroupMeshesForPreview(PreviewCandidateItem candidate, CancellationToken token)
    {
        if (candidate.ModelGroupMeshes.Count > 0)
        {
            return candidate.ModelGroupMeshes.ToList();
        }

        var meshIds = candidate.ModelGroupMeshInfos.Count > 0
            ? candidate.ModelGroupMeshInfos
                .Select(mesh => mesh.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray()
            : candidate.ModelGroupMeshIds;
        if (meshIds.Count == 0 && !string.IsNullOrWhiteSpace(candidate.ModelGroupId))
        {
            meshIds = LoadModelGroupMeshesForPreview(candidate.ModelGroupId)
                .Select(mesh => mesh.MeshAssetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
        }

        // Batch-resolve distinct mesh IDs grouped by source file path so each .assets file loads once.
        var resolvedById = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        var handlesToResolve = new List<(string MeshId, AssetHandle Handle, string SourceKey)>();
        foreach (var distinctId in meshIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(distinctId);
            if (handle == null)
            {
                continue;
            }

            if (handle.RealObject is Mesh existingMesh)
            {
                resolvedById[distinctId] = existingMesh;
            }
            else
            {
                var sourceKey = ResolveLazyHandleSourcePath(handle) ?? handle.SerializedFileName ?? string.Empty;
                handlesToResolve.Add((distinctId, handle, sourceKey));
            }
        }

        foreach (var group in handlesToResolve.GroupBy(x => x.SourceKey, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in group)
            {
                token.ThrowIfCancellationRequested();
                if (ResolveSemanticRelationHandleForPreview(item.Handle) is Mesh mesh)
                {
                    resolvedById[item.MeshId] = mesh;
                }
            }
        }

        var result = new List<Mesh>(meshIds.Count);
        foreach (var meshId in meshIds)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(meshId) && resolvedById.TryGetValue(meshId, out var mesh))
            {
                result.Add(mesh);
            }
        }

        return result;
    }

    private IReadOnlyList<float[]?> ResolveModelGroupTransformsForPreview(PreviewCandidateItem candidate, int expectedCount)
    {
        if (candidate.ModelGroupTransforms.Count > 0)
        {
            return candidate.ModelGroupTransforms;
        }

        if (candidate.ModelGroupMeshInfos.Count > 0)
        {
            return candidate.ModelGroupMeshInfos.Select(mesh => mesh.TransformMatrix).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(candidate.ModelGroupId))
        {
            var groupMeshes = LoadModelGroupMeshesForPreview(candidate.ModelGroupId);
            if (groupMeshes.Count > 0)
            {
                return groupMeshes.Select(mesh => mesh.TransformMatrix).ToArray();
            }
        }

        return expectedCount > 0
            ? Enumerable.Repeat<float[]?>(null, expectedCount).ToArray()
            : Array.Empty<float[]?>();
    }

    private void PreviewModelGroupCandidate(PreviewCandidateItem candidate)
    {
        var meshes = candidate.ModelGroupMeshes.Count > 0
            ? candidate.ModelGroupMeshes.ToList()
            : ResolveModelGroupMeshesForPreview(candidate, CancellationToken.None);
        var transforms = ResolveModelGroupTransformsForPreview(candidate, meshes.Count);
        if (meshes.Count == 0 && candidate.Mesh != null)
        {
            meshes.Add(candidate.Mesh);
            transforms = new float[]?[] { null };
        }

        if (meshes.Count == 0)
        {
            StatusStripUpdate("Model group preview: no mesh parts were resolved from saved connections.");
            return;
        }

        var meshPreviewId = texturePreviewIdCounter;
        var localAssetItem = AssetListDataGrid.SelectedItem as AssetItem;
        var includeMeshInfo = ViewModel.Options.DisplayInfo;
        PreviewSession.ActivateSurface(
            ViewModels.PreviewSurfaceKind.Mesh3D,
            localAssetItem,
            isLoading: true,
            loadingStatusText: $"Preparing model group preview ({meshes.Count:N0} parts)...");
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Preparing model group preview ({meshes.Count:N0} parts)...");
        if (includeMeshInfo)
        {
            SetPreviewInfoOverlay("Loading model group details...");
        }

        Task.Run(() =>
        {
            try
            {
                foreach (var mesh in meshes)
                {
                    mesh.EnsureProcessed();
                }

                var uvs = meshes.Select(BuildMeshPreviewUvs).ToArray();
                var quickInfoText = includeMeshInfo
                    ? FormatModelGroupPreviewSummary(candidate, meshes, localAssetItem, null) + Environment.NewLine + "Loading material details..."
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = candidate.Mesh ?? meshes[0];
                        _activeMeshBadgePreviewId = meshPreviewId;
                        _activeMeshBadgeSourceKind = "model-group";
                        _activeMeshGeometryFromCache = false;
                        _activeMeshMaterialsLoading = true;
                        UpdatePreviewCacheBadge(fromCache: false, sourceKind: "model-group", materialsLoading: true);
                        GLPreviewControl.SetMeshGroup(meshes, uvs, transforms);
                        GLPreviewControl.IsVisible = true;
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo)
                    {
                        SetPreviewInfoOverlay(quickInfoText);
                    }

                    StatusStripUpdate($"OpenGL Preview | Model group loaded ({meshes.Count:N0} parts) | Loading materials...");
                });

                // Resolve materials per part using v15 cache (with per-renderer disambiguation) and deduplicate textures across ALL parts.
                var allSlotSpecs = new List<(string MaterialName, global::OpenTK.Mathematics.Vector4 BaseColor)>();
                var allSubMeshTextureIds = new List<string?>();
                var allSubMeshPreserveAlpha = new List<bool>();
                var fallbackTexturesBySlot = new Dictionary<int, Texture2D>();
                var allMaterialNames = new List<string>();
                for (var partIdx = 0; partIdx < meshes.Count; partIdx++)
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    var mesh = meshes[partIdx];
                    var partInfo = partIdx < candidate.ModelGroupMeshInfos.Count
                        ? candidate.ModelGroupMeshInfos[partIdx]
                        : null;
                    var partRendererId = partInfo?.RendererAssetId;
                    var partGameObjectName = partInfo?.GameObjectName;
                    var resolvedPartSlots = TryLoadResolvedMeshMaterialsForPreview(mesh, partRendererId);

                    if (resolvedPartSlots.Count > 0)
                    {
                        var bySubMesh = new Dictionary<int, ResolvedMeshSubMeshMaterial>(resolvedPartSlots.Count);
                        foreach (var slot in resolvedPartSlots)
                        {
                            if (slot.SubMeshIndex >= 0 && !bySubMesh.ContainsKey(slot.SubMeshIndex))
                            {
                                bySubMesh[slot.SubMeshIndex] = slot;
                            }
                        }

                        var slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, resolvedPartSlots.Max(s => s.SubMeshIndex) + 1);
                        if (slotCount <= 0 && mesh.m_Indices?.Count > 0)
                        {
                            slotCount = 1;
                        }

                        for (var s = 0; s < slotCount; s++)
                        {
                            if (bySubMesh.TryGetValue(s, out var resolved))
                            {
                                var matName = !string.IsNullOrWhiteSpace(resolved.MaterialName)
                                    ? resolved.MaterialName
                                    : !string.IsNullOrWhiteSpace(resolved.MaterialAssetId)
                                        ? GetPreviewHandleName(resolved.MaterialAssetId, "Material")
                                        : "No Material";
                                allSlotSpecs.Add((matName, new global::OpenTK.Mathematics.Vector4(
                                    ClampPreviewColor(resolved.BaseColorR),
                                    ClampPreviewColor(resolved.BaseColorG),
                                    ClampPreviewColor(resolved.BaseColorB),
                                    ClampPreviewColor(resolved.BaseColorA))));
                                allSubMeshTextureIds.Add(string.IsNullOrWhiteSpace(resolved.MainTextureAssetId) ? null : resolved.MainTextureAssetId);
                                allSubMeshPreserveAlpha.Add(ShouldPreserveResolvedSlotTextureAlphaForPreview(resolved, mesh.m_Name, partGameObjectName));
                                if (!string.IsNullOrWhiteSpace(resolved.MaterialAssetId))
                                {
                                    allMaterialNames.Add(matName);
                                }
                            }
                            else
                            {
                                allSlotSpecs.Add(("No Material", global::OpenTK.Mathematics.Vector4.One));
                                allSubMeshTextureIds.Add(null);
                                allSubMeshPreserveAlpha.Add(IsDecalMeshOrObjectName(mesh.m_Name) || IsDecalMeshOrObjectName(partGameObjectName));
                            }
                        }
                    }
                    else
                    {
                        var materials = FindMaterialsForMeshPreview(mesh, partRendererId);
                        if (!materials.Any(m => m != null))
                        {
                            EnsureMeshPreviewDependenciesLoaded(mesh);
                            materials = FindMaterialsForMeshPreview(mesh, partRendererId);
                        }

                        var slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, materials.Count);
                        if (slotCount <= 0 && mesh.m_Indices?.Count > 0)
                        {
                            slotCount = 1;
                        }

                        for (var s = 0; s < slotCount; s++)
                        {
                            var mat = s < materials.Count ? materials[s] : null;
                            var matName = string.IsNullOrEmpty(mat?.m_Name) ? "No Material" : mat!.m_Name;
                            allSlotSpecs.Add((matName, GetMeshMaterialBaseColor(mat)));
                            var tex = mat != null ? FindTextureForMaterial(mat) : null;
                            var texKey = tex != null ? GetPreviewObjectKey(tex) : null;
                            var preserveAlpha = IsDecalMeshOrObjectName(mesh.m_Name)
                                || IsDecalMeshOrObjectName(partGameObjectName)
                                || ShouldPreserveMaterialTextureAlpha(mat);
                            if (tex != null && !string.IsNullOrWhiteSpace(texKey))
                            {
                                fallbackTexturesBySlot[allSubMeshTextureIds.Count] = tex;
                                allSubMeshTextureIds.Add(texKey);
                            }
                            else
                            {
                                allSubMeshTextureIds.Add(null);
                            }
                            allSubMeshPreserveAlpha.Add(preserveAlpha);
                            if (mat != null)
                            {
                                allMaterialNames.Add(matName);
                            }
                        }
                    }
                }

                if (meshPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                BuildDeduplicatedPreviewTexturesByAssetIds(
                    allSubMeshTextureIds,
                    () => meshPreviewId != texturePreviewIdCounter,
                    out var uniqueTextures,
                    out var uniqueWidths,
                    out var uniqueHeights,
                    out var subMeshToTextureIndex,
                    out var loadedTextureFiles,
                    allSubMeshPreserveAlpha);

                // Decode any non-indexed eager fallback textures that weren't in ProjectIndex.
                if (fallbackTexturesBySlot.Count > 0)
                {
                    foreach (var kvp in fallbackTexturesBySlot)
                    {
                        if (meshPreviewId != texturePreviewIdCounter)
                        {
                            return;
                        }

                        var mappedIdx = kvp.Key < subMeshToTextureIndex.Length ? subMeshToTextureIndex[kvp.Key] : -1;
                        if (mappedIdx >= 0 && mappedIdx < uniqueTextures.Count && uniqueTextures[mappedIdx] == null)
                        {
                            try
                            {
                                var preserveAlpha = kvp.Key < allSubMeshPreserveAlpha.Count && allSubMeshPreserveAlpha[kvp.Key];
                                using var previewImg = LoadTexturePreviewThumbnail(kvp.Value, MaxCachedPreviewTextureDimension);
                                var rgba = ConvertPreviewImageToRgbaBytes(previewImg, out var tw, out var th, preserveAlpha);
                                if (rgba != null)
                                {
                                    uniqueTextures[mappedIdx] = rgba;
                                    uniqueWidths[mappedIdx] = tw;
                                    uniqueHeights[mappedIdx] = th;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }

                if (meshPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                SetActivePreviewDependencyFiles(meshes.Cast<AssetStudio.Object?>().ToArray());
                if (loadedTextureFiles.Count > 0)
                {
                    AddActivePreviewDependencyFiles(loadedTextureFiles);
                }

                var hasTextures = uniqueTextures.Any(t => t != null);
                var infoText = includeMeshInfo
                    ? FormatModelGroupPreviewSummaryWithNames(candidate, meshes, localAssetItem, allMaterialNames)
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        GLPreviewControl.ApplyMeshTextures(uniqueTextures, uniqueWidths, uniqueHeights, subMeshToTextureIndex);
                        GLPreviewControl.IsVisible = true;
                        BuildMeshMaterialControlsFromSlotSpecs(allSlotSpecs);
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo)
                    {
                        SetPreviewInfoOverlay(infoText);
                    }

                    _activeMeshMaterialsLoading = false;
                    PreviewSession.IsLoading = false;
                    UpdatePreviewCacheBadge(
                        fromCache: _activeMeshGeometryFromCache,
                        sourceKind: _activeMeshBadgeSourceKind ?? "model-group",
                        materialsLoading: false);

                    StatusStripUpdate(hasTextures
                        ? $"OpenGL Preview | Model group: {candidate.ModelGroupName} | Parts: {meshes.Count:N0} | Textured"
                        : $"OpenGL Preview | Model group: {candidate.ModelGroupName} | Parts: {meshes.Count:N0} | No textures found");
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Model group preview failed for {candidate.ModelGroupName}: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId == texturePreviewIdCounter)
                    {
                        _activeMeshMaterialsLoading = false;
                        PreviewSession.IsLoading = false;
                        ClearPreviewCacheBadge();
                        StatusStripUpdate($"Model group preview error: {ex.Message}");
                    }
                });
            }
        });
    }

    private string FormatModelGroupPreviewSummaryWithNames(
        PreviewCandidateItem candidate,
        IReadOnlyList<Mesh> meshes,
        AssetItem? item,
        IReadOnlyList<string> materialNames)
    {
        var sb = new StringBuilder();
        var groupName = !string.IsNullOrWhiteSpace(candidate.ModelGroupName)
            ? candidate.ModelGroupName
            : "Model group";
        sb.AppendLine($"Model Group: {groupName}");
        sb.AppendLine("==================================================");
        if (item != null)
        {
            sb.AppendLine($"Selected Asset: {item.Name} (PathID: {item.PathID})");
        }
        var kind = !string.IsNullOrWhiteSpace(candidate.ModelGroupKind)
            ? candidate.ModelGroupKind
            : (candidate.ModelGroupId.Contains(":sceneobject:", StringComparison.OrdinalIgnoreCase) ? "SceneObject" : "Animator");
        sb.AppendLine($"Group Kind: {kind}");
        sb.AppendLine($"Parts: {meshes.Count}");
        sb.AppendLine($"Confidence: {candidate.ModelGroupConfidence}");

        var vertexTotal = meshes.Sum(mesh => Math.Max(0, mesh.m_VertexCount));
        var indexTotal = meshes.Sum(mesh => mesh.m_Indices?.Count ?? 0);
        sb.AppendLine($"Vertex Count: {vertexTotal:N0}");
        sb.AppendLine($"Index Count: {indexTotal:N0}");

        var distinctNames = materialNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        sb.AppendLine($"Material Slots: {materialNames.Count} ({distinctNames.Count} unique)");
        foreach (var matName in distinctNames.Take(16))
        {
            sb.AppendLine($"  - {matName}");
        }

        sb.AppendLine();
        sb.AppendLine("Mesh Parts:");
        foreach (var mesh in meshes.Take(24))
        {
            sb.AppendLine($"  - {mesh.m_Name} (PathID: {mesh.m_PathID}, Submeshes: {mesh.m_SubMeshes?.Length ?? 0})");
        }
        if (meshes.Count > 24)
        {
            sb.AppendLine($"  ... {meshes.Count - 24:N0} more");
        }

        return sb.ToString();
    }

    private string FormatModelGroupPreviewSummary(
        PreviewCandidateItem candidate,
        IReadOnlyList<Mesh> meshes,
        AssetItem? item,
        IReadOnlyList<Material?>? materials)
    {
        var sb = new StringBuilder();
        var groupName = !string.IsNullOrWhiteSpace(candidate.ModelGroupName)
            ? candidate.ModelGroupName
            : "Model group";
        sb.AppendLine($"Model Group: {groupName}");
        sb.AppendLine("==================================================");
        if (item != null)
        {
            sb.AppendLine($"Selected Asset: {item.Name} (PathID: {item.PathID})");
        }
        sb.AppendLine($"Group Kind: {(candidate.ModelGroupId.Contains(":sceneobject:", StringComparison.OrdinalIgnoreCase) ? "SceneObject" : "Animator")}");
        sb.AppendLine($"Parts: {meshes.Count}");
        sb.AppendLine($"Confidence: {candidate.ModelGroupConfidence}");

        var vertexTotal = meshes.Sum(mesh => Math.Max(0, mesh.m_VertexCount));
        var indexTotal = meshes.Sum(mesh => mesh.m_Indices?.Count ?? 0);
        sb.AppendLine($"Vertex Count: {vertexTotal:N0}");
        sb.AppendLine($"Index Count: {indexTotal:N0}");

        if (materials != null)
        {
            sb.AppendLine($"Material Slots: {materials.Count}");
            foreach (var material in materials.Where(material => material != null).Take(16))
            {
                sb.AppendLine($"  - {material!.m_Name} (PathID: {material.m_PathID})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Mesh Parts:");
        foreach (var mesh in meshes.Take(24))
        {
            sb.AppendLine($"  - {mesh.m_Name} (PathID: {mesh.m_PathID}, Submeshes: {mesh.m_SubMeshes?.Length ?? 0})");
        }
        if (meshes.Count > 24)
        {
            sb.AppendLine($"  ... {meshes.Count - 24:N0} more");
        }

        return sb.ToString();
    }

    private void UpdateModelGroupToggleContext(
        IReadOnlyList<PreviewCandidateItem>? candidates,
        PreviewCandidateItem? activeCandidate,
        int explicitAvailablePartCount = 0)
    {
        static int GetPartCount(PreviewCandidateItem? item)
        {
            if (item == null || !item.IsModelGroup)
            {
                return 0;
            }

            return Math.Max(
                item.ModelGroupMeshCount,
                Math.Max(
                    item.ModelGroupMeshInfos.Count,
                    Math.Max(item.ModelGroupMeshIds.Count, item.ModelGroupMeshes.Count)));
        }

        var activeGroupParts = GetPartCount(activeCandidate);
        var bestGroupParts = Math.Max(explicitAvailablePartCount, activeGroupParts);
        if (candidates != null)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var count = GetPartCount(c);
                if (count > bestGroupParts)
                {
                    bestGroupParts = count;
                }
            }
        }

        var displayParts = activeGroupParts > 1 ? activeGroupParts : bestGroupParts;
        MeshPreview.SetModelGroupContext(hasMultiPartGroup: displayParts > 1, partCount: displayParts);
    }

    private static List<ModelGroupMeshInfo> DeduplicateModelGroupMeshInfos(IReadOnlyList<ModelGroupMeshInfo> infos)
    {
        if (infos == null || infos.Count == 0)
        {
            return new List<ModelGroupMeshInfo>();
        }

        var result = new List<ModelGroupMeshInfo>(infos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            if (string.IsNullOrWhiteSpace(info.MeshAssetId))
            {
                continue;
            }

            string key;
            if (!string.IsNullOrWhiteSpace(info.RendererAssetId))
            {
                key = $"{info.MeshAssetId}\u001f{info.RendererAssetId}";
            }
            else if (!string.IsNullOrWhiteSpace(info.GameObjectAssetId))
            {
                key = $"{info.MeshAssetId}\u001fgo:{info.GameObjectAssetId}\u001f{info.SlotIndex}";
            }
            else
            {
                key = $"{info.MeshAssetId}\u001fslot:{i}:{info.GameObjectName}";
            }

            if (seen.Add(key))
            {
                result.Add(info);
            }
        }

        return result;
    }

    private List<MeshPreviewControl.AvatarPreviewPartSpec> BuildCompositeAvatarPartSpecsForCandidate(
        Avatar? avatar,
        Services.AvatarDefaultSkeletonData defaultSkeleton,
        PreviewCandidateItem? candidate,
        Mesh? fallbackMesh,
        bool includeAllGroupParts,
        CancellationToken token,
        out Mesh? primaryMesh,
        out int availableGroupPartCount,
        IReadOnlyDictionary<long, int>? transformPathIdToNodeIdx = null)
    {
        primaryMesh = fallbackMesh;
        availableGroupPartCount = 0;
        var specs = new List<MeshPreviewControl.AvatarPreviewPartSpec>();

        var rawInfos = candidate?.ModelGroupMeshInfos is { Count: > 0 } candidateInfos
            ? candidateInfos
            : (!string.IsNullOrWhiteSpace(candidate?.ModelGroupId)
                ? LoadModelGroupMeshesForPreview(candidate!.ModelGroupId)
                : (IReadOnlyList<ModelGroupMeshInfo>)Array.Empty<ModelGroupMeshInfo>());

        var fallbackMeshKey = fallbackMesh != null ? GetPreviewObjectKey(fallbackMesh) : candidate?.MeshId;
        var dedupInfos = FilterActivePreviewModelGroupParts(DeduplicateModelGroupMeshInfos(rawInfos), fallbackMeshKey);
        var preResolvedMeshes = candidate?.ModelGroupMeshes ?? Array.Empty<Mesh>();
        availableGroupPartCount = Math.Max(
            dedupInfos.Count,
            Math.Max(preResolvedMeshes.Count, candidate?.ModelGroupMeshIds.Count ?? 0));

        var resolvedPairs = new List<(Mesh Mesh, ModelGroupMeshInfo? Info)>();
        if (includeAllGroupParts && dedupInfos.Count > 0)
        {
            var limit = Math.Min(dedupInfos.Count, MeshPreviewControl.MaxCompositeAvatarParts);
            var resolvedById = new Dictionary<string, Mesh>(StringComparer.Ordinal);

            // Seed from pre-resolved meshes when available (e.g. eager mode or candidate pre-fetch).
            for (var i = 0; i < preResolvedMeshes.Count; i++)
            {
                var pm = preResolvedMeshes[i];
                if (pm == null)
                {
                    continue;
                }

                var key = GetPreviewObjectKey(pm);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    resolvedById.TryAdd(key, pm);
                }
            }

            if (fallbackMesh != null)
            {
                var fbKey = GetPreviewObjectKey(fallbackMesh);
                if (!string.IsNullOrWhiteSpace(fbKey))
                {
                    resolvedById.TryAdd(fbKey, fallbackMesh);
                }
            }

            // Batch-resolve distinct missing Mesh IDs grouped by source file so each .assets loads once.
            var handlesToResolve = new List<(string MeshId, AssetHandle Handle, string SourceKey)>();
            var queuedDistinctIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < limit; i++)
            {
                var meshId = dedupInfos[i].MeshAssetId;
                if (string.IsNullOrWhiteSpace(meshId) || resolvedById.ContainsKey(meshId) || !queuedDistinctIds.Add(meshId))
                {
                    continue;
                }

                var handle = assetsManager.ProjectIndex.GetHandle(meshId);
                if (handle == null)
                {
                    continue;
                }

                if (handle.RealObject is Mesh loadedMesh)
                {
                    resolvedById[meshId] = loadedMesh;
                }
                else
                {
                    var sourceKey = ResolveLazyHandleSourcePath(handle) ?? handle.SerializedFileName ?? string.Empty;
                    handlesToResolve.Add((meshId, handle, sourceKey));
                }
            }

            foreach (var sourceGroup in handlesToResolve.GroupBy(x => x.SourceKey, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in sourceGroup)
                {
                    token.ThrowIfCancellationRequested();
                    if (ResolveSemanticRelationHandleForPreview(item.Handle) is Mesh mesh)
                    {
                        resolvedById[item.MeshId] = mesh;
                    }
                }
            }

            for (var i = 0; i < limit; i++)
            {
                token.ThrowIfCancellationRequested();
                var info = dedupInfos[i];
                if (resolvedById.TryGetValue(info.MeshAssetId, out var mesh))
                {
                    resolvedPairs.Add((mesh, info));
                }
                else if (i < preResolvedMeshes.Count && preResolvedMeshes[i] != null)
                {
                    resolvedPairs.Add((preResolvedMeshes[i], info));
                }
            }
        }
        else if (includeAllGroupParts && preResolvedMeshes.Count > 0)
        {
            var limit = Math.Min(preResolvedMeshes.Count, MeshPreviewControl.MaxCompositeAvatarParts);
            for (var i = 0; i < limit; i++)
            {
                if (preResolvedMeshes[i] != null)
                {
                    resolvedPairs.Add((preResolvedMeshes[i], i < dedupInfos.Count ? dedupInfos[i] : null));
                }
            }
        }
        else
        {
            var singleMesh = fallbackMesh;
            ModelGroupMeshInfo? singleInfo = null;
            if (singleMesh != null)
            {
                var singleKey = GetPreviewObjectKey(singleMesh);
                singleInfo = dedupInfos.FirstOrDefault(info =>
                    !string.IsNullOrWhiteSpace(singleKey)
                    && string.Equals(info.MeshAssetId, singleKey, StringComparison.Ordinal));
            }
            else if (dedupInfos.Count > 0)
            {
                singleInfo = SelectRepresentativeModelGroupMesh(dedupInfos);
                if (singleInfo != null)
                {
                    singleMesh = ResolveMeshPreviewCandidate(singleInfo.MeshAssetId);
                }
            }

            if (singleMesh != null)
            {
                resolvedPairs.Add((singleMesh, singleInfo));
            }
        }

        // Order skinned parts first (largest vertex count first) so primaryMesh is deterministic.
        Mesh? bestPrimarySkinned = null;
        Mesh? bestPrimaryAny = null;
        foreach (var pair in resolvedPairs)
        {
            token.ThrowIfCancellationRequested();
            var mesh = pair.Mesh;
            var info = pair.Info;
            mesh.EnsureProcessed();
            if (mesh.m_VertexCount <= 0
                || mesh.m_Vertices == null
                || mesh.m_Vertices.Length == 0
                || mesh.m_Indices == null
                || mesh.m_Indices.Count < 3)
            {
                continue;
            }

            var isSkinned = mesh.m_BindPose is { Length: > 0 }
                && mesh.m_Skin is { Length: > 0 }
                && mesh.m_Skin.Length >= mesh.m_VertexCount;

            var meshAssetId = !string.IsNullOrWhiteSpace(info?.MeshAssetId)
                ? info!.MeshAssetId
                : GetPreviewObjectKey(mesh);

            if (isSkinned)
            {
                int[] boneMap;
                if (transformPathIdToNodeIdx != null && transformPathIdToNodeIdx.Count > 0)
                {
                    boneMap = TryMapSkinnedMeshBonesViaTransformIds(avatar, mesh, info?.RendererAssetId, transformPathIdToNodeIdx);
                }
                else
                {
                    boneMap = Array.Empty<int>();
                }

                if (boneMap.Length == 0 || boneMap.All(idx => idx < 0))
                {
                    boneMap = Services.MeshPreviewSkeletonBuilder.MapSkinnedMeshBonesToAvatarNodes(mesh, avatar, defaultSkeleton);
                    if (boneMap.Length > 0 && boneMap.All(idx => idx < 0))
                    {
                        var rendererBonePaths = TryExtractLoadedSkinnedRendererBonePaths(avatar, mesh, info?.RendererAssetId);
                        if (rendererBonePaths != null && rendererBonePaths.Length > 0)
                        {
                            boneMap = Services.MeshPreviewSkeletonBuilder.MapSkinnedMeshBonesToAvatarNodes(
                                mesh,
                                avatar,
                                defaultSkeleton,
                                rendererBonePaths);
                        }
                    }
                }

                specs.Add(new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = mesh,
                    MeshAssetId = meshAssetId,
                    RendererAssetId = info?.RendererAssetId,
                    RendererType = !string.IsNullOrWhiteSpace(info?.RendererType) ? info!.RendererType : "SkinnedMeshRenderer",
                    GameObjectName = info?.GameObjectName,
                    IsSkinned = true,
                    BoneToSkeletonNodeMap = boneMap
                });

                bool isOverlayPart = IsOverlayOrAuxiliaryMeshName(mesh.m_Name) || IsOverlayOrAuxiliaryMeshName(info?.GameObjectName);
                bool bestSkinnedIsOverlay = bestPrimarySkinned != null && IsOverlayOrAuxiliaryMeshName(bestPrimarySkinned.m_Name);
                if (bestPrimarySkinned == null
                    || (bestSkinnedIsOverlay && !isOverlayPart)
                    || (bestSkinnedIsOverlay == isOverlayPart && mesh.m_VertexCount > bestPrimarySkinned.m_VertexCount))
                {
                    bestPrimarySkinned = mesh;
                }
            }
            else
            {
                TryResolveLoadedRigidPartTransform(
                    info,
                    defaultSkeleton,
                    out var relativeTransformPath,
                    out var loadedAttachNodeIdx,
                    out var loadedIsExactMatch,
                    out var explicitLocalOffset);

                int attachNodeIdx;
                bool isExactNodeMatch;
                if (loadedAttachNodeIdx >= 0)
                {
                    attachNodeIdx = loadedAttachNodeIdx;
                    isExactNodeMatch = loadedIsExactMatch;
                }
                else
                {
                    Services.MeshPreviewSkeletonBuilder.TryResolveRigidPartAvatarNode(
                        defaultSkeleton,
                        relativeTransformPath,
                        info?.GameObjectName,
                        mesh.m_Name,
                        out attachNodeIdx,
                        out isExactNodeMatch);
                }

                global::OpenTK.Mathematics.Matrix4? staticTransform =
                    Services.MeshPreviewSkeletonBuilder.TryConvertRowMajorTransformMatrix(info?.TransformMatrix, out var convertedStatic)
                        ? convertedStatic
                        : null;
                var rigidLocalOffset = Services.MeshPreviewSkeletonBuilder.ComputeRigidPartLocalOffset(
                    staticTransform,
                    attachNodeIdx,
                    isExactNodeMatch,
                    defaultSkeleton.RestWorldMatrices,
                    explicitLocalOffset);

                specs.Add(new MeshPreviewControl.AvatarPreviewPartSpec
                {
                    Mesh = mesh,
                    MeshAssetId = meshAssetId,
                    RendererAssetId = info?.RendererAssetId,
                    RendererType = !string.IsNullOrWhiteSpace(info?.RendererType) ? info!.RendererType : "MeshRenderer",
                    GameObjectName = info?.GameObjectName,
                    IsSkinned = false,
                    RigidAttachNodeIndex = attachNodeIdx,
                    RigidLocalOffset = rigidLocalOffset
                });
            }

            bool isOverlayAny = IsOverlayOrAuxiliaryMeshName(mesh.m_Name) || IsOverlayOrAuxiliaryMeshName(info?.GameObjectName);
            bool bestAnyIsOverlay = bestPrimaryAny != null && IsOverlayOrAuxiliaryMeshName(bestPrimaryAny.m_Name);
            if (bestPrimaryAny == null
                || (bestAnyIsOverlay && !isOverlayAny)
                || (bestAnyIsOverlay == isOverlayAny && mesh.m_VertexCount > bestPrimaryAny.m_VertexCount))
            {
                bestPrimaryAny = mesh;
            }
        }

        primaryMesh = bestPrimarySkinned ?? bestPrimaryAny ?? fallbackMesh;
        return specs;
    }

    private int[] TryMapSkinnedMeshBonesViaTransformIds(
        Avatar? avatar,
        Mesh mesh,
        string? rendererAssetId,
        IReadOnlyDictionary<long, int> transformPathIdToNodeIdx)
    {
        if (mesh?.m_BindPose == null || mesh.m_BindPose.Length == 0)
        {
            return Array.Empty<int>();
        }

        SkinnedMeshRenderer? smr = null;
        if (!string.IsNullOrWhiteSpace(rendererAssetId))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(rendererAssetId);
            if (handle?.RealObject is SkinnedMeshRenderer loadedSmr)
            {
                smr = loadedSmr;
            }
        }

        smr ??= FindSkinnedRendererForAvatarMesh(avatar, mesh);
        if (smr?.m_Bones == null || smr.m_Bones.Length == 0 || smr.assetsFile == null)
        {
            return Array.Empty<int>();
        }

        var boneCount = mesh.m_BindPose.Length;
        var map = new int[boneCount];
        Array.Fill(map, -1);
        var limit = Math.Min(boneCount, smr.m_Bones.Length);
        for (var b = 0; b < limit; b++)
        {
            var bonePtr = smr.m_Bones[b];
            if (bonePtr == null || bonePtr.IsNull)
            {
                continue;
            }

            if (transformPathIdToNodeIdx.TryGetValue(bonePtr.m_PathID, out var directIdx))
            {
                map[b] = directIdx;
                continue;
            }

            var boneTr = ResolveTransformBackground(smr.assetsFile, bonePtr);
            if (boneTr != null && transformPathIdToNodeIdx.TryGetValue(boneTr.m_PathID, out var resolvedIdx))
            {
                map[b] = resolvedIdx;
            }
        }

        return map;
    }

    private string?[]? TryExtractLoadedSkinnedRendererBonePaths(Avatar? avatar, Mesh mesh, string? rendererAssetId)
    {
        SkinnedMeshRenderer? smr = null;
        if (!string.IsNullOrWhiteSpace(rendererAssetId))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(rendererAssetId);
            if (handle?.RealObject is SkinnedMeshRenderer loadedSmr)
            {
                smr = loadedSmr;
            }
        }

        smr ??= FindSkinnedRendererForAvatarMesh(avatar, mesh);
        if (smr?.m_Bones == null || smr.m_Bones.Length == 0 || smr.assetsFile == null)
        {
            return null;
        }

        var paths = new string?[smr.m_Bones.Length];
        for (var i = 0; i < smr.m_Bones.Length; i++)
        {
            var tf = Services.SemanticGraphService.ResolveTransformBackground(smr.assetsFile, smr.m_Bones[i]);
            if (tf != null)
            {
                paths[i] = BuildTransformPathFromLoadedHierarchy(smr.assetsFile, tf);
            }
        }

        return paths;
    }

    private void TryResolveLoadedRigidPartTransform(
        ModelGroupMeshInfo? info,
        Services.AvatarDefaultSkeletonData defaultSkeleton,
        out string? relativeTransformPath,
        out int attachedNodeIdx,
        out bool isExactNodeMatch,
        out global::OpenTK.Mathematics.Matrix4? explicitLocalOffset)
    {
        relativeTransformPath = null;
        attachedNodeIdx = -1;
        isExactNodeMatch = false;
        explicitLocalOffset = null;

        if (info == null || string.IsNullOrWhiteSpace(info.GameObjectAssetId))
        {
            return;
        }

        var goHandle = assetsManager.ProjectIndex.GetHandle(info.GameObjectAssetId);
        if (goHandle?.RealObject is not GameObject go || go.assetsFile == null)
        {
            return;
        }

        var sourceFile = go.assetsFile;
        var tf = Services.SemanticGraphService.ResolveTransformForGameObjectBackground(sourceFile, go);
        if (tf == null)
        {
            return;
        }

        // Walk up the loaded Transform hierarchy to collect path segments and local transforms.
        var chain = new List<(Transform Transform, string Name)>();
        var visited = new HashSet<long>();
        var current = tf;
        for (var depth = 0; current != null && depth < 128; depth++)
        {
            if (!visited.Add(current.m_PathID))
            {
                break;
            }

            var currentSource = current.assetsFile ?? sourceFile;
            var currentGo = Services.SemanticGraphService.ResolveGameObjectBackground(currentSource, current.m_GameObject);
            chain.Add((current, currentGo?.m_Name ?? string.Empty));
            if (current.m_Father == null || current.m_Father.IsNull)
            {
                break;
            }

            current = Services.SemanticGraphService.ResolveTransformBackground(currentSource, current.m_Father);
        }

        if (chain.Count == 0)
        {
            return;
        }

        // Build full path top-down excluding empty root names.
        var fullSegments = new List<string>(chain.Count);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(chain[i].Name))
            {
                fullSegments.Add(chain[i].Name);
            }
        }

        relativeTransformPath = fullSegments.Count > 0 ? string.Join("/", fullSegments) : null;

        // Walk from the GameObject upward to find the closest AvatarNode in the hierarchy, accumulating exact local offset.
        var localToAncestor = global::OpenTK.Mathematics.Matrix4.Identity;
        for (var i = 0; i < chain.Count; i++)
        {
            // Build suffix path up to this node from top down.
            var ancestorPathParts = new List<string>(chain.Count - i);
            for (var k = chain.Count - 1; k >= i; k--)
            {
                if (!string.IsNullOrWhiteSpace(chain[k].Name))
                {
                    ancestorPathParts.Add(chain[k].Name);
                }
            }

            var candidatePath = ancestorPathParts.Count > 0 ? string.Join("/", ancestorPathParts) : chain[i].Name;
            if (!string.IsNullOrWhiteSpace(candidatePath)
                && Services.MeshPreviewSkeletonBuilder.TryResolvePathAlias(defaultSkeleton.NodePathToIdx, candidatePath, out var foundNodeIdx))
            {
                attachedNodeIdx = foundNodeIdx;
                isExactNodeMatch = i == 0;
                explicitLocalOffset = i == 0 ? global::OpenTK.Mathematics.Matrix4.Identity : localToAncestor;
                return;
            }

            var t = chain[i].Transform;
            var stepLocal = Services.MeshPreviewSkeletonBuilder.CreateLocalMatrix(
                Services.MeshPreviewSkeletonBuilder.ToOtkVector3(t.m_LocalPosition),
                Services.MeshPreviewSkeletonBuilder.ToOtkQuaternion(t.m_LocalRotation),
                Services.MeshPreviewSkeletonBuilder.ToOtkVector3(t.m_LocalScale));
            localToAncestor = localToAncestor * stepLocal;
        }
    }

    private static string? BuildTransformPathFromLoadedHierarchy(SerializedFile sourceFile, Transform transform)
    {
        var segments = new List<string>();
        var visited = new HashSet<long>();
        var current = transform;
        for (var depth = 0; current != null && depth < 128; depth++)
        {
            if (!visited.Add(current.m_PathID))
            {
                break;
            }

            var currentSource = current.assetsFile ?? sourceFile;
            var go = Services.SemanticGraphService.ResolveGameObjectBackground(currentSource, current.m_GameObject);
            if (!string.IsNullOrWhiteSpace(go?.m_Name))
            {
                segments.Add(go!.m_Name);
            }

            if (current.m_Father == null || current.m_Father.IsNull)
            {
                break;
            }

            current = Services.SemanticGraphService.ResolveTransformBackground(currentSource, current.m_Father);
        }

        if (segments.Count == 0)
        {
            return null;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static List<CompositePreviewMaterialPart> BuildCompositeMaterialPartsFromGlData(
        MeshPreviewControl.PreparedAvatarPreviewGLData? glData,
        Mesh? fallbackMesh)
    {
        var result = new List<CompositePreviewMaterialPart>();
        if (glData?.CompositeParts is { Count: > 0 } parts)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                result.Add(new CompositePreviewMaterialPart
                {
                    Mesh = part.Mesh,
                    RendererAssetId = part.RendererAssetId,
                    ExpectedSubMeshCount = Math.Max(1, part.SubMeshCount)
                });
            }

            return result;
        }

        var singleMesh = glData?.Mesh ?? fallbackMesh;
        if (singleMesh != null)
        {
            var expectedCount = glData?.SubMeshIndexCounts is { Length: > 0 } counts
                ? counts.Length
                : Math.Max(1, singleMesh.m_SubMeshes?.Length ?? 0);
            result.Add(new CompositePreviewMaterialPart
            {
                Mesh = singleMesh,
                RendererAssetId = null,
                ExpectedSubMeshCount = expectedCount
            });
        }

        return result;
    }

    private List<(ModelGroupInfo Group, List<ModelGroupMeshInfo> Meshes, List<Mesh> ResolvedMeshes)> BuildEagerAvatarModelGroups(Avatar avatar)
    {
        var result = new List<(ModelGroupInfo, List<ModelGroupMeshInfo>, List<Mesh>)>();
        var loadedFiles = GetLoadedFilesSnapshot();
        var avatarKey = GetPreviewObjectKey(avatar);
        if (loadedFiles.Count == 0 || string.IsNullOrWhiteSpace(avatarKey))
        {
            return result;
        }

        foreach (var file in loadedFiles)
        {
            if (file == null)
            {
                continue;
            }

            AssetStudio.Object[] objs;
            lock (file)
            {
                objs = file.Objects.ToArray();
            }

            var animators = objs.OfType<Animator>().ToList();
            if (animators.Count == 0)
            {
                continue;
            }

            List<SkinnedMeshRenderer>? fileSmrs = null;
            List<MeshRenderer>? fileMrs = null;
            foreach (var animator in animators)
            {
                if (animator.m_Avatar == null || animator.m_Avatar.IsNull)
                {
                    continue;
                }

                if (!animator.m_Avatar.TryGet(out var resolvedAvatar) || !AreSamePreviewObject(resolvedAvatar, avatar))
                {
                    continue;
                }

                var animatorGo = Services.SemanticGraphService.ResolveGameObjectBackground(file, animator.m_GameObject);
                if (animatorGo == null)
                {
                    continue;
                }

                fileSmrs ??= objs.OfType<SkinnedMeshRenderer>().ToList();
                fileMrs ??= objs.OfType<MeshRenderer>().ToList();
                var rootGoId = GetPreviewObjectKey(animatorGo);
                var animatorId = GetPreviewObjectKey(animator);
                var groupId = $"modelgroup:animator:{animatorId}";
                var groupName = !string.IsNullOrWhiteSpace(animatorGo.m_Name) ? animatorGo.m_Name : avatar.m_Name ?? "Model";

                var partInfos = new List<ModelGroupMeshInfo>();
                var partMeshes = new List<Mesh>();
                var slotIdx = 0;

                foreach (var smr in fileSmrs)
                {
                    var smrGo = Services.SemanticGraphService.ResolveGameObjectBackground(file, smr.m_GameObject);
                    if (smrGo == null || !IsSameOrDescendantGameObject(file, smrGo, animatorGo))
                    {
                        continue;
                    }

                    if (!smr.m_Mesh.TryGet(out var smrMesh) || smrMesh == null)
                    {
                        continue;
                    }

                    partInfos.Add(new ModelGroupMeshInfo(
                        groupId,
                        GetPreviewObjectKey(smrMesh),
                        GetPreviewObjectKey(smr),
                        "SkinnedMeshRenderer",
                        GetPreviewObjectKey(smrGo),
                        smrGo.m_Name ?? string.Empty,
                        slotIdx++,
                        100,
                        "Eager Animator descendant",
                        BuildLocalToGroupRootMatrix(file, smrGo, rootGoId),
                        smrMesh.m_Name ?? string.Empty,
                        smrMesh.m_PathID,
                        smrMesh.byteSize,
                        (int)ClassIDType.Mesh));
                    partMeshes.Add(smrMesh);
                }

                if (partInfos.Count == 0)
                {
                    foreach (var mr in fileMrs)
                    {
                        var mrGo = Services.SemanticGraphService.ResolveGameObjectBackground(file, mr.m_GameObject);
                        if (mrGo == null || !IsSameOrDescendantGameObject(file, mrGo, animatorGo))
                        {
                            continue;
                        }

                        Mesh? mrMesh = null;
                        if (mrGo.m_Components != null)
                        {
                            foreach (var compPtr in mrGo.m_Components)
                            {
                                if (compPtr.TryGet(out var comp) && comp is MeshFilter mf && mf.m_Mesh.TryGet(out var foundMesh) && foundMesh != null)
                                {
                                    mrMesh = foundMesh;
                                    break;
                                }
                            }
                        }

                        if (mrMesh == null)
                        {
                            continue;
                        }

                        partInfos.Add(new ModelGroupMeshInfo(
                            groupId,
                            GetPreviewObjectKey(mrMesh),
                            GetPreviewObjectKey(mr),
                            "MeshRenderer",
                            GetPreviewObjectKey(mrGo),
                            mrGo.m_Name ?? string.Empty,
                            slotIdx++,
                            80,
                            "Eager Animator MeshRenderer descendant",
                            BuildLocalToGroupRootMatrix(file, mrGo, rootGoId),
                            mrMesh.m_Name ?? string.Empty,
                            mrMesh.m_PathID,
                            mrMesh.byteSize,
                            (int)ClassIDType.Mesh));
                        partMeshes.Add(mrMesh);
                    }
                }

                if (partInfos.Count > 0)
                {
                    var group = new ModelGroupInfo(
                        groupId,
                        "Animator",
                        groupName,
                        rootGoId,
                        animatorGo.m_Name ?? string.Empty,
                        animatorId,
                        avatarKey,
                        string.Empty,
                        file.fileName ?? string.Empty,
                        partInfos[0].Confidence,
                        partInfos[0].ConfidenceReason);
                    result.Add((group, partInfos, partMeshes));
                }
            }
        }

        return result;
    }
}
