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

            var groupMeshes = composite.Parts
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
                .ToList();
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

                var groupMeshes = groupCandidate.Meshes;
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

        return candidates;
    }

    private PreviewCandidateItem? SelectMeshPreviewCandidate(
        Mesh mesh,
        IReadOnlyList<PreviewCandidateItem> candidates,
        PreviewCandidateItem? selectedCandidate,
        bool preferModelGroup)
    {
        return SelectBestMeshPreviewCandidate(mesh, candidates, selectedCandidate, preferModelGroup);
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

        // Only auto-switch to compact high-confidence Animator character groups (<= 16 parts).
        // Never hijack a single selected mesh or huge scene Combined Mesh into a 100-part SceneObject group;
        // SceneObject groups stay selectable in the Model Group dropdown.
        var isCombinedSceneMesh = mesh?.m_Name != null
            && mesh.m_Name.StartsWith("Combined Mesh", StringComparison.OrdinalIgnoreCase);
        if (preferModelGroup && !isCombinedSceneMesh)
        {
            var group = candidates.FirstOrDefault(candidate =>
                candidate.IsModelGroup
                && candidate.ModelGroupMeshIds.Count > 1
                && candidate.ModelGroupMeshCount <= 16
                && candidate.ModelGroupConfidence >= 80
                && (string.Equals(candidate.ModelGroupKind, "Animator", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(candidate.ModelGroupId)
                        && !candidate.ModelGroupId.Contains(":sceneobject:", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(candidate.ModelGroupKind))));
            if (group != null)
            {
                return group;
            }
        }

        return candidates.FirstOrDefault(candidate => AreSamePreviewObject(candidate.Mesh, mesh))
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

    private static ModelGroupMeshInfo? SelectRepresentativeModelGroupMesh(IReadOnlyList<ModelGroupMeshInfo> meshes)
    {
        return meshes
            .Where(mesh => !string.IsNullOrWhiteSpace(mesh.MeshAssetId))
            .OrderByDescending(mesh => mesh.Confidence)
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
                var fallbackTexturesBySlot = new Dictionary<int, Texture2D>();
                var allMaterialNames = new List<string>();
                for (var partIdx = 0; partIdx < meshes.Count; partIdx++)
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    var mesh = meshes[partIdx];
                    var partRendererId = partIdx < candidate.ModelGroupMeshInfos.Count
                        ? candidate.ModelGroupMeshInfos[partIdx].RendererAssetId
                        : null;
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
                                if (!string.IsNullOrWhiteSpace(resolved.MaterialAssetId))
                                {
                                    allMaterialNames.Add(matName);
                                }
                            }
                            else
                            {
                                allSlotSpecs.Add(("No Material", global::OpenTK.Mathematics.Vector4.One));
                                allSubMeshTextureIds.Add(null);
                            }
                        }
                    }
                    else
                    {
                        var materials = FindMaterialsForMeshPreview(mesh);
                        if (materials.Count == 0 && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                        {
                            EnsureMeshPreviewDependenciesLoaded(mesh);
                            materials = FindMaterialsForMeshPreview(mesh);
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
                            if (tex != null && !string.IsNullOrWhiteSpace(texKey))
                            {
                                fallbackTexturesBySlot[allSubMeshTextureIds.Count] = tex;
                                allSubMeshTextureIds.Add(texKey);
                            }
                            else
                            {
                                allSubMeshTextureIds.Add(null);
                            }
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
                    out var loadedTextureFiles);

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
                                using var previewImg = LoadTexturePreviewThumbnail(kvp.Value, MaxCachedPreviewTextureDimension);
                                var rgba = ConvertPreviewImageToRgbaBytes(previewImg, out var tw, out var th);
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
}
