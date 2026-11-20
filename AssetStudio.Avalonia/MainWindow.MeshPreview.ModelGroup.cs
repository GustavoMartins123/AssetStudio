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
                ModelGroupMeshIds = meshIds,
                ModelGroupMeshInfos = groupMeshes.ToArray(),
                ModelGroupTransforms = groupMeshes.Select(part => part.TransformMatrix).ToArray(),
                ModelGroupMeshCount = meshIds.Length,
                ModelGroupConfidence = group.Confidence,
                Label = $"Model group: {groupName} ({meshIds.Length:N0} parts) -> {representativeName}"
            });
        }

        return candidates;
    }

    private PreviewCandidateItem? SelectMeshPreviewCandidate(
        Mesh mesh,
        IReadOnlyList<PreviewCandidateItem> candidates,
        PreviewCandidateItem? selectedCandidate,
        bool preferModelGroup)
    {
        if (selectedCandidate != null)
        {
            var selected = candidates.FirstOrDefault(candidate =>
                (!string.IsNullOrWhiteSpace(selectedCandidate.ModelGroupId)
                    && string.Equals(candidate.ModelGroupId, selectedCandidate.ModelGroupId, StringComparison.Ordinal))
                || AreSamePreviewObject(candidate.Mesh, selectedCandidate.Mesh)
                || (!string.IsNullOrWhiteSpace(selectedCandidate.MeshId)
                    && string.Equals(candidate.MeshId, selectedCandidate.MeshId, StringComparison.Ordinal)));
            if (selected != null)
            {
                return selected;
            }
        }

        if (preferModelGroup)
        {
            var group = candidates.FirstOrDefault(candidate => candidate.IsModelGroup && candidate.ModelGroupMeshIds.Count > 1);
            if (group != null)
            {
                return group;
            }
        }

        return candidates.FirstOrDefault(candidate => AreSamePreviewObject(candidate.Mesh, mesh))
            ?? candidates.FirstOrDefault();
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

        var result = new List<Mesh>();
        foreach (var meshId in meshIds)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(meshId))
            {
                continue;
            }

            var mesh = ResolveMeshPreviewCandidate(meshId);
            if (mesh != null)
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

                var allMaterials = new List<Material?>();
                var subMeshTextures = new List<byte[]?>();
                var subMeshTexWidths = new List<int>();
                var subMeshTexHeights = new List<int>();
                foreach (var mesh in meshes)
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    var materials = FindMaterialsForMeshPreview(mesh);
                    if (materials.Count == 0 && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                    {
                        EnsureMeshPreviewDependenciesLoaded(mesh);
                        materials = FindMaterialsForMeshPreview(mesh);
                    }

                    var slotCountBefore = subMeshTextures.Count;
                    AddPreviewTextureSlotsForMesh(
                        mesh,
                        materials,
                        subMeshTextures,
                        subMeshTexWidths,
                        subMeshTexHeights);

                    var slotCount = subMeshTextures.Count - slotCountBefore;
                    for (var i = 0; i < slotCount; i++)
                    {
                        allMaterials.Add(i < materials.Count ? materials[i] : null);
                    }
                }

                var hasTextures = subMeshTextures.Any(t => t != null);
                var infoText = includeMeshInfo
                    ? FormatModelGroupPreviewSummary(candidate, meshes, localAssetItem, allMaterials)
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        GLPreviewControl.ApplyMeshTextures(subMeshTextures, subMeshTexWidths, subMeshTexHeights);
                        GLPreviewControl.IsVisible = true;
                        BuildMeshMaterialControlsForSlots(allMaterials);
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
