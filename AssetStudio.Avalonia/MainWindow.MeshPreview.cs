using Avalonia.Threading;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Object = AssetStudio.Object;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewMesh(
        AssetItem assetItem,
        Mesh m_Mesh,
        bool rebuildCandidateControls = true,
        PreviewCandidateItem? selectedCandidate = null,
        bool preferModelGroup = true)
    {
        if (rebuildCandidateControls)
        {
            QueueMeshPreviewCandidateBuild(assetItem, m_Mesh, selectedCandidate, preferModelGroup);
            return;
        }

        var meshPreviewId = texturePreviewIdCounter;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate("Preparing mesh preview...");
        if (displayInfo.IsChecked == true && PreviewInfoBorder != null && PreviewInfoOverlay != null)
        {
            PreviewInfoOverlay.Text = "Loading details...";
            PreviewInfoBorder.IsVisible = true;
        }

        var localAssetItem = assetItem;
        var includeMeshInfo = displayInfo.IsChecked == true;
        Task.Run(() =>
        {
            try
            {
                m_Mesh.EnsureProcessed();
                if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
                {
                    throw new Exception("Mesh contains no vertex data. Companion resource file might be missing or failed to decompress.");
                }

                var uvs = BuildMeshPreviewUvs(m_Mesh);
                var quickInfoText = includeMeshInfo
                    ? FormatMeshPreviewSummary(m_Mesh, localAssetItem) + Environment.NewLine + "Loading material details..."
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = m_Mesh;
                        GLPreviewControl.SetMesh(m_Mesh, uvs);
                        GLPreviewControl.IsVisible = true;
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = quickInfoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

                    StatusStripUpdate("OpenGL Preview | Mesh loaded | Loading materials...");
                });

                if (meshPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                var subMeshTextures = new List<byte[]?>();
                var subMeshTexWidths = new List<int>();
                var subMeshTexHeights = new List<int>();
                var allMaterials = FindMaterialsForMeshPreview(m_Mesh);
                if (allMaterials.Count == 0 && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                {
                    EnsureMeshPreviewDependenciesLoaded(m_Mesh);
                    allMaterials = FindMaterialsForMeshPreview(m_Mesh);
                }

                AddPreviewTextureSlotsForMesh(
                    m_Mesh,
                    allMaterials,
                    subMeshTextures,
                    subMeshTexWidths,
                    subMeshTexHeights);

                var infoText = includeMeshInfo
                    ? assetsManager.LazyLoading
                        ? FormatLazyMeshPreview(m_Mesh, localAssetItem, allMaterials)
                        : FormatMeshPreview(m_Mesh, localAssetItem)
                    : string.Empty;
                var hasTextures = subMeshTextures.Any(t => t != null);

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        return;
                    }

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = m_Mesh;
                        GLPreviewControl.ApplyMeshTextures(m_Mesh, subMeshTextures, subMeshTexWidths, subMeshTexHeights);
                        GLPreviewControl.IsVisible = true;
                        BuildMeshMaterialControls(m_Mesh, allMaterials);
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = infoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

                    StatusStripUpdate(hasTextures
                        ? "OpenGL Preview | 'Ctrl W'=Wireframe | 'Ctrl N'=ReNormal | 'Ctrl S'=Textured/Shaded"
                        : "OpenGL Preview | No texture found for this mesh | 'Ctrl W'=Wireframe | 'Ctrl N'=ReNormal");
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Mesh preview failed for {localAssetItem.Name}: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        StatusStripUpdate($"Mesh preview error: {ex.Message}");
                        if (PreviewInfoOverlay != null)
                        {
                            PreviewInfoOverlay.Text = $"Failed to load mesh: {ex.Message}";
                        }
                    }
                });
            }
        });
    }

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
        var includeMeshInfo = displayInfo.IsChecked == true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Preparing model group preview ({meshes.Count:N0} parts)...");
        if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
        {
            PreviewInfoOverlay.Text = "Loading model group details...";
            PreviewInfoBorder.IsVisible = true;
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
                        GLPreviewControl.SetMeshGroup(meshes, uvs, transforms);
                        GLPreviewControl.IsVisible = true;
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = quickInfoText;
                        PreviewInfoBorder.IsVisible = true;
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

                    if (includeMeshInfo && PreviewInfoBorder != null && PreviewInfoOverlay != null)
                    {
                        PreviewInfoOverlay.Text = infoText;
                        PreviewInfoBorder.IsVisible = true;
                    }

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
                        StatusStripUpdate($"Model group preview error: {ex.Message}");
                    }
                });
            }
        });
    }

    private void AddPreviewTextureSlotsForMesh(
        Mesh mesh,
        IReadOnlyList<Material?> materials,
        List<byte[]?> subMeshTextures,
        List<int> subMeshTexWidths,
        List<int> subMeshTexHeights)
    {
        var slotCount = Math.Max(mesh.m_SubMeshes?.Length ?? 0, materials.Count);
        if (slotCount <= 0 && mesh.m_Indices?.Count > 0)
        {
            slotCount = 1;
        }

        for (int i = 0; i < slotCount; i++)
        {
            byte[]? tb = null;
            int tw = 0, th = 0;

            if (i < materials.Count && materials[i] != null)
            {
                var tex = FindTextureForMaterial(materials[i]!);
                if (tex != null)
                {
                    try
                    {
                        using (var previewImage = LoadTexturePreviewThumbnail(tex, MaxCachedPreviewTextureDimension))
                        {
                            var image = previewImage?.Image;
                            if (image != null)
                            {
                                tw = image.Width;
                                th = image.Height;
                                tb = new byte[tw * th * 4];
                                image.CopyPixelDataTo(tb);
                                for (int p = 0; p < tb.Length; p += 4)
                                {
                                    byte temp = tb[p];
                                    tb[p] = tb[p + 2];
                                    tb[p + 2] = temp;
                                    tb[p + 3] = byte.MaxValue;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            subMeshTextures.Add(tb);
            subMeshTexWidths.Add(tw);
            subMeshTexHeights.Add(th);
        }
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

    private static string GetPreviewObjectKey(AssetStudio.Object? asset)
    {
        return asset?.assetsFile != null
            ? AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID)
            : asset != null
                ? $"runtime:{asset.GetType().Name}:{asset.m_PathID}"
                : string.Empty;
    }

    private static bool AreSamePreviewObject(AssetStudio.Object? left, AssetStudio.Object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        var leftKey = GetPreviewObjectKey(left);
        var rightKey = GetPreviewObjectKey(right);
        return !string.IsNullOrEmpty(leftKey)
            && !string.IsNullOrEmpty(rightKey)
            && string.Equals(leftKey, rightKey, StringComparison.Ordinal);
    }

    private AssetStudio.Object? ResolvePreviewCandidateAsset(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var handle = assetsManager.ProjectIndex.GetHandle(assetId);
        return handle == null ? null : ResolveSemanticRelationHandleForPreview(handle);
    }

    private Mesh? ResolveMeshPreviewCandidate(string? meshId)
    {
        return ResolvePreviewCandidateAsset(meshId) as Mesh;
    }

    private Avatar? ResolveAvatarPreviewCandidate(string? avatarId)
    {
        return ResolvePreviewCandidateAsset(avatarId) as Avatar;
    }

    private string GetPreviewHandleName(string assetId, string fallbackName)
    {
        var handle = string.IsNullOrWhiteSpace(assetId) ? null : assetsManager.ProjectIndex.GetHandle(assetId);
        return !string.IsNullOrWhiteSpace(handle?.Name) ? handle!.Name : fallbackName;
    }

    private List<string> LoadAvatarMeshAssetIdsForPreview(Avatar avatar)
    {
        if (!assetsManager.LazyLoading || avatar.assetsFile == null || currentScanResult == null)
        {
            return new List<string>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<string>();
        }

        var avatarAssetId = AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAvatarMeshAssetIds(folderPath, signature, avatarAssetId);
    }

    private void CacheAvatarMeshRelation(Avatar avatar, Mesh mesh)
    {
        avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
        avatarMeshCache[avatar] = mesh;

        avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
        if (!avatarMeshesCache.TryGetValue(avatar, out var meshes))
        {
            meshes = new List<Mesh>();
            avatarMeshesCache[avatar] = meshes;
        }

        if (!meshes.Any(existing => AreSamePreviewObject(existing, mesh)))
        {
            meshes.Add(mesh);
        }

        meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
        meshAvatarCache[mesh] = avatar;
    }

    private Mesh? ResolveFirstMeshForAvatar(Avatar avatar)
    {
        if (avatarMeshesCache != null
            && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes)
            && cachedMeshes.Count > 0)
        {
            return cachedMeshes[0];
        }

        if (avatarMeshCache != null
            && avatarMeshCache.TryGetValue(avatar, out var cachedMesh)
            && cachedMesh != null)
        {
            return cachedMesh;
        }

        foreach (var meshId in LoadAvatarMeshAssetIdsForPreview(avatar))
        {
            var mesh = ResolveMeshPreviewCandidate(meshId);
            if (mesh == null)
            {
                continue;
            }

            CacheAvatarMeshRelation(avatar, mesh);
            return mesh;
        }

        return assetsManager.LazyLoading ? null : FindMeshesForAvatar(avatar).FirstOrDefault();
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

    private Dictionary<string, List<string>> LoadAvatarMeshAssetIdsByAvatarIdsForPreview(IReadOnlyList<string> avatarIds)
    {
        if (!assetsManager.LazyLoading || currentScanResult == null || avatarIds.Count == 0)
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        return _sqliteCache.LoadAvatarMeshAssetIdsByAvatarIds(folderPath, signature, avatarIds);
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

        if (avatarMeshCache != null && avatarMeshCache.TryGetValue(avatar, out var cachedMesh))
        {
            AddMesh(cachedMesh, null, "Loaded");
        }

        if (avatarMeshesCache != null && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes))
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
        var previewId = texturePreviewIdCounter;
        var selectedAsset = AssetListDataGrid.SelectedItem;
        StatusStripUpdate("Loading avatar relations...");

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = BuildAvatarPreviewCandidates(avatar, preferredMesh, preferredMeshId);
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId != texturePreviewIdCounter
                        || !ReferenceEquals(AssetListDataGrid.SelectedItem, selectedAsset))
                    {
                        return;
                    }

                    PreviewAvatar(
                        avatar,
                        preferredMesh,
                        preferredMeshId,
                        selectedCandidateOverride,
                        rebuildCandidateControls: false,
                        preparedCandidates: candidates);
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Avatar relation query failed for {avatar.m_Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId == texturePreviewIdCounter
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, selectedAsset))
                    {
                        PreviewAvatar(
                            avatar,
                            preferredMesh,
                            preferredMeshId,
                            selectedCandidateOverride,
                            rebuildCandidateControls: false,
                            preparedCandidates: Array.Empty<PreviewCandidateItem>());
                    }
                });
            }
        });
    }

    private void PreviewAvatar(
        Avatar avatar,
        Mesh? preferredMesh = null,
        string? preferredMeshId = null,
        PreviewCandidateItem? selectedCandidateOverride = null,
        bool rebuildCandidateControls = true,
        IReadOnlyList<PreviewCandidateItem>? preparedCandidates = null)
    {
        if (rebuildCandidateControls)
        {
            QueueAvatarPreviewCandidateBuild(avatar, preferredMesh, preferredMeshId, selectedCandidateOverride);
            return;
        }

        currentPreviewAvatar = avatar;
        var avatarCandidates = preparedCandidates?.ToList()
            ?? (selectedCandidateOverride != null
                ? new List<PreviewCandidateItem> { selectedCandidateOverride }
                : new List<PreviewCandidateItem>());
        Mesh? avatarMesh = SelectAvatarPreviewMesh(avatarCandidates, preferredMesh, preferredMeshId);
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
        avatarMesh?.EnsureProcessed();
        if (avatarMesh != null)
        {
            CacheAvatarMeshRelation(avatar, avatarMesh);
            EnsureMeshPreviewDependenciesLoaded(avatarMesh);
        }
        currentPreviewMesh = avatarMesh;

        global::OpenTK.Mathematics.Vector3[]? bonePositions = null;
        int[]? parentIndices = null;
        string[]? boneNames = null;

        var skinnedRenderer = avatarMesh != null ? FindSkinnedRendererForAvatarMesh(avatar, avatarMesh) : null;
        if (avatarMesh != null
            && skinnedRenderer != null
            && TryBuildSkeletonFromSkinnedRenderer(avatar, avatarMesh, skinnedRenderer, out var rendererBonePositions, out var rendererParentIndices, out var rendererBoneNames))
        {
            bonePositions = rendererBonePositions;
            parentIndices = rendererParentIndices;
            boneNames = rendererBoneNames;
        }

        if (bonePositions == null && avatarMesh != null && avatarMesh.m_BindPose != null && avatarMesh.m_BindPose.Length > 0
            && avatarMesh.m_BoneNameHashes != null && avatarMesh.m_BoneNameHashes.Length > 0
            && avatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
        {
            int meshBoneCount = avatarMesh.m_BindPose.Length;
            var nodes = avatar.m_Avatar.m_AvatarSkeleton.m_Node;
            var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
            int skelCount = nodes.Length;

            var meshBonePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            for (int i = 0; i < meshBoneCount; i++)
            {
                var bp = avatarMesh.m_BindPose[i];
                var otkMat = new global::OpenTK.Mathematics.Matrix4(
                    bp.M00, bp.M01, bp.M02, bp.M03,
                    bp.M10, bp.M11, bp.M12, bp.M13,
                    bp.M20, bp.M21, bp.M22, bp.M23,
                    bp.M30, bp.M31, bp.M32, bp.M33
                );
                try
                {
                    var inv = otkMat.Inverted();
                    meshBonePositions[i] = inv.ExtractTranslation();
                }
                catch
                {
                    meshBonePositions[i] = global::OpenTK.Mathematics.Vector3.Zero;
                }
            }

            var meshBoneHashToIdx = new Dictionary<uint, int>();
            for (int j = 0; j < avatarMesh.m_BoneNameHashes.Length; j++)
            {
                meshBoneHashToIdx[avatarMesh.m_BoneNameHashes[j]] = j;
            }

            var skelNodeToMeshBone = new int[skelCount];
            for (int i = 0; i < skelCount; i++)
            {
                skelNodeToMeshBone[i] = -1;
                if (skelIds != null && i < skelIds.Length)
                {
                    if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                    {
                        skelNodeToMeshBone[i] = mbIdx;
                    }
                }
            }

            var meshBoneToSkelNode = new int[meshBoneCount];
            for (int i = 0; i < meshBoneCount; i++) meshBoneToSkelNode[i] = -1;
            for (int i = 0; i < skelCount; i++)
            {
                if (skelNodeToMeshBone[i] >= 0)
                {
                    meshBoneToSkelNode[skelNodeToMeshBone[i]] = i;
                }
            }

            var meshBoneNames = new string[meshBoneCount];
            for (int mb = 0; mb < meshBoneCount; mb++)
            {
                int skelIdx = meshBoneToSkelNode[mb];
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
            for (int mb = 0; mb < meshBoneCount; mb++)
            {
                meshParentIndices[mb] = -1;
                int skelIdx = meshBoneToSkelNode[mb];
                if (skelIdx < 0) continue;

                int current = nodes[skelIdx].m_ParentId;
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

            bonePositions = meshBonePositions;
            parentIndices = meshParentIndices;
            boneNames = meshBoneNames;
        }

        if (avatarMesh != null && bonePositions != null && parentIndices != null && GLPreviewControl != null)
        {
            GLPreviewControl.SetAvatar(avatarMesh, bonePositions, parentIndices, boneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            if (displayInfo.IsChecked == true && PreviewInfoBorder != null && PreviewInfoOverlay != null)
            {
                PreviewInfoOverlay.Text = BuildAvatarPreviewInfoText(avatar, avatarCandidates, avatarMesh);
                PreviewInfoBorder.IsVisible = true;
            }
            StatusStripUpdate($"OpenGL Avatar Preview | Mesh: {avatarMesh.m_Name} | Related meshes: {avatarCandidates.Count} | Skeleton Joints: {bonePositions.Length}");
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

        SetTextWithTruncation(TextPreviewBox, sb.ToString());
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

    private SkinnedMeshRenderer? FindSkinnedRendererForAvatarMesh(Avatar? avatar, Mesh mesh)
    {
        if (mesh.assetsFile == null)
        {
            return null;
        }

        var semanticRenderers = TryLoadSkinnedRenderersForMeshFromSemanticCache(mesh);
        if (semanticRenderers.Count > 0)
        {
            return semanticRenderers
                .OrderByDescending(renderer => ScoreSkinnedRendererForMesh(renderer, avatar, mesh))
                .FirstOrDefault();
        }

        return null;
    }

    private List<SkinnedMeshRenderer> TryLoadSkinnedRenderersForMeshFromSemanticCache(Mesh mesh)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return new List<SkinnedMeshRenderer>();
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return new List<SkinnedMeshRenderer>();
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        if (string.IsNullOrWhiteSpace(meshAssetId))
        {
            return new List<SkinnedMeshRenderer>();
        }

        meshSkinnedRenderersCache ??= new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
        if (meshSkinnedRenderersCache.TryGetValue(meshAssetId, out var cachedRenderers))
        {
            return new List<SkinnedMeshRenderer>(cachedRenderers);
        }

        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var rendererIds = _sqliteCache.LoadMeshRendererAssetIds(folderPath, signature, meshAssetId, "SkinnedMeshRenderer");
        var renderers = new List<SkinnedMeshRenderer>();
        var seenRendererIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rendererId in rendererIds)
        {
            if (string.IsNullOrWhiteSpace(rendererId) || !seenRendererIds.Add(rendererId))
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(rendererId);
            if (handle == null)
            {
                continue;
            }

            if (ResolveSemanticRelationHandleForPreview(handle) is SkinnedMeshRenderer renderer)
            {
                renderers.Add(renderer);
            }
        }

        meshSkinnedRenderersCache[meshAssetId] = renderers;
        return new List<SkinnedMeshRenderer>(renderers);
    }

    private static int ScoreSkinnedRendererForMesh(SkinnedMeshRenderer renderer, Avatar? avatar, Mesh mesh)
    {
        var score = 1000;
        if (renderer.m_Bones != null && mesh.m_BindPose != null)
        {
            if (renderer.m_Bones.Length == mesh.m_BindPose.Length)
            {
                score += 150;
            }
            else if (renderer.m_Bones.Length > 0)
            {
                score += 25;
            }
        }

        if (avatar != null && renderer.assetsFile == avatar.assetsFile)
        {
            score += 20;
        }

        return score;
    }

    private static bool TryBuildSkeletonFromSkinnedRenderer(
        Avatar? avatar,
        Mesh mesh,
        SkinnedMeshRenderer renderer,
        out global::OpenTK.Mathematics.Vector3[] bonePositions,
        out int[] parentIndices,
        out string[] boneNames)
    {
        bonePositions = Array.Empty<global::OpenTK.Mathematics.Vector3>();
        parentIndices = Array.Empty<int>();
        boneNames = Array.Empty<string>();

        if (renderer.assetsFile == null
            || renderer.m_Bones == null
            || renderer.m_Bones.Length == 0
            || mesh.m_BindPose == null
            || mesh.m_BindPose.Length == 0)
        {
            return false;
        }

        var boneCount = Math.Min(renderer.m_Bones.Length, mesh.m_BindPose.Length);
        if (boneCount <= 0)
        {
            return false;
        }

        var boneTransforms = new Transform?[boneCount];
        var transformIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < boneCount; i++)
        {
            var transform = ResolveTransformBackground(renderer.assetsFile, renderer.m_Bones[i]);
            boneTransforms[i] = transform;
            var transformId = GetTransformRelationId(renderer.assetsFile, renderer.m_Bones[i], transform);
            if (!string.IsNullOrEmpty(transformId) && !transformIndexById.ContainsKey(transformId))
            {
                transformIndexById[transformId] = i;
            }
        }

        if (transformIndexById.Count == 0)
        {
            return false;
        }

        bonePositions = new global::OpenTK.Mathematics.Vector3[boneCount];
        parentIndices = new int[boneCount];
        boneNames = new string[boneCount];
        Array.Fill(parentIndices, -1);

        for (var i = 0; i < boneCount; i++)
        {
            bonePositions[i] = GetBindPosePosition(mesh.m_BindPose[i]);
            boneNames[i] = GetTransformDisplayName(renderer.assetsFile, boneTransforms[i])
                ?? (mesh.m_BoneNameHashes != null && i < mesh.m_BoneNameHashes.Length
                    ? avatar?.FindBonePath(mesh.m_BoneNameHashes[i]) ?? string.Empty
                    : string.Empty);
        }

        for (var i = 0; i < boneCount; i++)
        {
            var current = boneTransforms[i];
            if (current?.m_Father == null || current.m_Father.IsNull)
            {
                continue;
            }

            var sourceFile = current.assetsFile ?? renderer.assetsFile;
            var parentTransform = ResolveTransformBackground(sourceFile, current.m_Father);
            if (parentTransform == null)
            {
                continue;
            }

            var parentId = GetSemanticAssetId(parentTransform);
            if (!string.IsNullOrEmpty(parentId) && transformIndexById.TryGetValue(parentId, out var parentIndex))
            {
                parentIndices[i] = parentIndex;
            }
        }

        return parentIndices.Any(index => index >= 0);
    }

    private static string GetTransformRelationId(SerializedFile sourceFile, PPtr<Transform> transformPtr, Transform? transform)
    {
        var transformId = GetSemanticAssetId(transform);
        if (!string.IsNullOrEmpty(transformId))
        {
            return transformId;
        }

        return GetSemanticAssetIdFromPPtr(sourceFile, transformPtr, ClassIDType.Transform);
    }

    private static string? GetTransformDisplayName(SerializedFile sourceFile, Transform? transform)
    {
        if (transform == null)
        {
            return null;
        }

        var transformSource = transform.assetsFile ?? sourceFile;
        var gameObject = ResolveGameObjectBackground(transformSource, transform.m_GameObject);
        return string.IsNullOrWhiteSpace(gameObject?.m_Name) ? null : gameObject.m_Name;
    }

    private static global::OpenTK.Mathematics.Vector3 GetBindPosePosition(AssetStudio.Matrix4x4 bindPose)
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

    private List<PreviewCandidateItem> BuildAnimationClipPreviewCandidates(
        AnimationClip clip,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId = null,
        string? preferredMeshId = null)
    {
        var candidates = new List<PreviewCandidateItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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

        if (currentPreviewMesh != null)
        {
            AddCandidate(currentPreviewAvatar, currentPreviewMesh, null, null, currentPreviewAvatar != null ? "Current" : "Current mesh");
        }

        if (currentPreviewAvatar != null)
        {
            var addedCurrentMesh = false;
            if (avatarMeshesCache != null && avatarMeshesCache.TryGetValue(currentPreviewAvatar, out var currentMeshes))
            {
                foreach (var mesh in currentMeshes)
                {
                    AddCandidate(currentPreviewAvatar, mesh, null, null, "Current avatar");
                    addedCurrentMesh = true;
                }
            }

            if (!addedCurrentMesh
                && avatarMeshCache != null
                && avatarMeshCache.TryGetValue(currentPreviewAvatar, out var cachedMesh)
                && cachedMesh != null)
            {
                AddCandidate(currentPreviewAvatar, cachedMesh, null, null, "Current avatar");
                addedCurrentMesh = true;
            }

            if (!addedCurrentMesh)
            {
                AddCandidate(currentPreviewAvatar, null, null, null, "Current avatar");
            }
        }

        if (assetsManager.LazyLoading)
        {
            var avatarIds = LoadAnimationClipAvatarAssetIdsForPreview(clip);
            var avatarMeshIdsByAvatarId = LoadAvatarMeshAssetIdsByAvatarIdsForPreview(avatarIds);
            var modelGroupsByAvatarId = LoadModelGroupsByAvatarIdsForPreview(avatarIds);
            var avatarIdsCoveredByMesh = new HashSet<string>(StringComparer.Ordinal);
            var avatarIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByAvatar = new HashSet<string>(StringComparer.Ordinal);
            var meshIdsCoveredByModelGroup = new HashSet<string>(StringComparer.Ordinal);

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
        var previewId = texturePreviewIdCounter;
        var selectedAsset = AssetListDataGrid.SelectedItem;
        StatusStripUpdate("Loading animation relations...");

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = BuildAnimationClipPreviewCandidates(
                    clip,
                    preferredAvatar,
                    preferredMesh,
                    preferredAvatarId,
                    preferredMeshId);
                var activeCandidate = selectedCandidate
                    ?? SelectAnimationClipPreviewCandidate(
                        clip,
                        candidates,
                        preferredAvatar,
                        preferredMesh,
                        preferredAvatarId,
                        preferredMeshId);

                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId != texturePreviewIdCounter
                        || !ReferenceEquals(AssetListDataGrid.SelectedItem, selectedAsset))
                    {
                        return;
                    }

                    BuildPreviewCandidateControls(candidates, activeCandidate, "Animation Target");
                    PreviewAnimationClip(
                        clip,
                        preferredAvatar,
                        preferredMesh,
                        preferredAvatarId,
                        preferredMeshId,
                        rebuildCandidateControls: false,
                        selectedCandidate: activeCandidate);
                });
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Animation relation query failed for {clip.m_Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId == texturePreviewIdCounter
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, selectedAsset))
                    {
                        ClearPreviewCandidateControls();
                        PreviewAnimationClip(
                            clip,
                            preferredAvatar,
                            preferredMesh,
                            preferredAvatarId,
                            preferredMeshId,
                            rebuildCandidateControls: false,
                            selectedCandidate: selectedCandidate);
                    }
                });
            }
        });
    }

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

        if (avatar == null || avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            StatusStripUpdate("AnimationClip: No Avatar found to preview animation.");
            return;
        }

        if (avatarMesh == null)
        {
            avatarMesh = ResolveFirstMeshForAvatar(avatar);
        }
        avatarMesh?.EnsureProcessed();

        if (avatarMesh == null || avatarMesh.m_BindPose == null || avatarMesh.m_BindPose.Length == 0
            || avatarMesh.m_BoneNameHashes == null || avatarMesh.m_BoneNameHashes.Length == 0)
        {
            StatusStripUpdate("AnimationClip: No suitable mesh with bind poses found.");
            return;
        }

        // Step 3: Build the bind-pose skeleton (same as PreviewAvatar)
        int meshBoneCount = avatarMesh.m_BindPose.Length;
        var nodes = avatar.m_Avatar.m_AvatarSkeleton.m_Node;
        var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
        int skelCount = nodes.Length;

        var restBonePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
        var bindPoseInverses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            var bp = avatarMesh.m_BindPose[i];
            var otkMat = new global::OpenTK.Mathematics.Matrix4(
                bp.M00, bp.M01, bp.M02, bp.M03,
                bp.M10, bp.M11, bp.M12, bp.M13,
                bp.M20, bp.M21, bp.M22, bp.M23,
                bp.M30, bp.M31, bp.M32, bp.M33
            );
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

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        for (int j = 0; j < avatarMesh.m_BoneNameHashes.Length; j++)
            meshBoneHashToIdx[avatarMesh.m_BoneNameHashes[j]] = j;

        var skelNodeToMeshBone = new int[skelCount];
        for (int i = 0; i < skelCount; i++)
        {
            skelNodeToMeshBone[i] = -1;
            if (skelIds != null && i < skelIds.Length)
                if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                    skelNodeToMeshBone[i] = mbIdx;
        }

        var meshBoneToSkelNode = new int[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++) meshBoneToSkelNode[i] = -1;
        for (int i = 0; i < skelCount; i++)
            if (skelNodeToMeshBone[i] >= 0)
                meshBoneToSkelNode[skelNodeToMeshBone[i]] = i;

        var meshBoneNames = new string[meshBoneCount];
        for (int mb = 0; mb < meshBoneCount; mb++)
        {
            int skelIdx = meshBoneToSkelNode[mb];
            if (skelIds != null && skelIdx >= 0 && skelIdx < skelIds.Length)
            {
                meshBoneNames[mb] = avatar.FindBonePath(skelIds[skelIdx]) ?? string.Empty;
            }
            else
            {
                meshBoneNames[mb] = string.Empty;
            }
        }

        static string NormalizeAnimationPath(string? path)
        {
            return (path ?? string.Empty)
                .Replace("\\", "/", StringComparison.Ordinal)
                .Trim('/');
        }

        var meshBonePathToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void AddBonePathAlias(string? path, int meshBoneIdx)
        {
            path = NormalizeAnimationPath(path);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int start = 0; start < parts.Length; start++)
            {
                var alias = string.Join("/", parts.Skip(start));
                if (!meshBonePathToIdx.TryGetValue(alias, out var existing))
                {
                    meshBonePathToIdx[alias] = meshBoneIdx;
                }
                else if (existing != meshBoneIdx)
                {
                    meshBonePathToIdx[alias] = -1;
                }
            }
        }

        for (int mb = 0; mb < meshBoneCount; mb++)
        {
            AddBonePathAlias(meshBoneNames[mb], mb);
        }

        string GetPathFromHash(uint hash)
        {
            var path = avatar.FindBonePath(hash);
            return string.IsNullOrEmpty(path) ? string.Empty : NormalizeAnimationPath(path);
        }

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            var path = GetPathFromHash(binding.path);
            if (!string.IsNullOrEmpty(path))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int start = 0; start < parts.Length; start++)
                {
                    var alias = string.Join("/", parts.Skip(start));
                    if (meshBonePathToIdx.TryGetValue(alias, out meshBoneIdx) && meshBoneIdx >= 0)
                    {
                        return true;
                    }
                }
            }

            return meshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        static global::OpenTK.Mathematics.Vector3 ToOtkVector3(AssetStudio.Vector3 value)
        {
            return new global::OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
        }

        static global::OpenTK.Mathematics.Quaternion ToOtkQuaternion(AssetStudio.Quaternion value)
        {
            var q = new global::OpenTK.Mathematics.Quaternion(value.X, value.Y, value.Z, value.W);
            if (q.LengthSquared > 0)
            {
                q.Normalize();
            }
            return q;
        }

        static global::OpenTK.Mathematics.Matrix4 CreateLocalMatrix(
            global::OpenTK.Mathematics.Vector3 position,
            global::OpenTK.Mathematics.Quaternion rotation,
            global::OpenTK.Mathematics.Vector3 scale)
        {
            return global::OpenTK.Mathematics.Matrix4.CreateScale(scale)
                * global::OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rotation)
                * global::OpenTK.Mathematics.Matrix4.CreateTranslation(position);
        }

        var meshParentIndices = new int[meshBoneCount];
        for (int mb = 0; mb < meshBoneCount; mb++)
        {
            meshParentIndices[mb] = -1;
            int skelIdx = meshBoneToSkelNode[mb];
            if (skelIdx < 0) continue;
            int current = nodes[skelIdx].m_ParentId;
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

        var weightedBoneMask = new bool[meshBoneCount];
        if (avatarMesh.m_Skin != null)
        {
            foreach (var skin in avatarMesh.m_Skin)
            {
                if (skin?.boneIndex == null || skin.weight == null)
                {
                    continue;
                }

                for (int i = 0; i < Math.Min(skin.boneIndex.Length, skin.weight.Length); i++)
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
        var deformChainMask = new bool[meshBoneCount];
        if (hasWeightedBones)
        {
            for (int i = 0; i < meshBoneCount; i++)
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

        static bool IsFiniteVector(global::OpenTK.Mathematics.Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        static bool IsAuxiliaryAnimationBone(string? path)
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

        bool ShouldUseAnimationBinding(int meshBoneIdx)
        {
            if (meshBoneIdx < 0 || meshBoneIdx >= meshBoneCount)
            {
                return false;
            }

            if (!deformChainMask[meshBoneIdx])
            {
                return false;
            }

            return weightedBoneMask[meshBoneIdx] || !IsAuxiliaryAnimationBone(meshBoneNames[meshBoneIdx]);
        }

        var muscleClip = clip.m_MuscleClip;
        if (muscleClip?.m_Clip == null)
        {
            StatusStripUpdate("AnimationClip: No muscle clip data.");
            return;
        }

        var posTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        var rotTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>>();
        var scaleTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        float maxTime = 0f;

        void AddKeyframe(int meshBoneIdx, uint attribute, float time, float[] data, int offset)
        {
            if (time > maxTime) maxTime = time;
            if (offset < 0 || offset >= data.Length)
            {
                return;
            }
            if (attribute == 1) // Position
            {
                if (offset + 2 >= data.Length) return;
                if (!posTracks.TryGetValue(meshBoneIdx, out var list)) posTracks[meshBoneIdx] = list = new();
                list.Add((time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2])));
            }
            else if (attribute == 2) // Rotation
            {
                if (offset + 3 >= data.Length) return;
                if (!rotTracks.TryGetValue(meshBoneIdx, out var list)) rotTracks[meshBoneIdx] = list = new();
                var q = new global::OpenTK.Mathematics.Quaternion(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]);
                if (q.LengthSquared > 0)
                {
                    q.Normalize();
                }
                list.Add((time, q));
            }
            else if (attribute == 3) // Scale
            {
                if (offset + 2 >= data.Length) return;
                if (!scaleTracks.TryGetValue(meshBoneIdx, out var list)) scaleTracks[meshBoneIdx] = list = new();
                list.Add((time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2])));
            }
            else if (attribute == 4) // Euler rotation
            {
                if (offset + 2 >= data.Length) return;
                if (!rotTracks.TryGetValue(meshBoneIdx, out var list)) rotTracks[meshBoneIdx] = list = new();
                var euler = new global::OpenTK.Mathematics.Vector3(
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset]),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 1]),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 2]));
                list.Add((time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler)));
            }
        }

        if (muscleClip?.m_Clip != null)
        {
            var m_Clip = muscleClip.m_Clip;
            var bindings = clip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();

            if (bindings?.genericBindings != null)
            {
                int GetBindingCurveWidth(GenericBinding binding)
                {
                    if (binding.typeID != ClassIDType.Transform)
                    {
                        return 1;
                    }

                    return binding.attribute switch
                    {
                        1 => 3,
                        2 => 4,
                        3 => 3,
                        4 => 3,
                        _ => 1
                    };
                }

                bool TryFindBindingInfo(int index, out GenericBinding binding, out int startIndex, out int width)
                {
                    var curves = 0;
                    foreach (var candidate in bindings.genericBindings)
                    {
                        var candidateWidth = GetBindingCurveWidth(candidate);
                        if (index >= curves && index < curves + candidateWidth)
                        {
                            binding = candidate;
                            startIndex = curves;
                            width = candidateWidth;
                            return true;
                        }
                        curves += candidateWidth;
                    }

                    binding = default!;
                    startIndex = -1;
                    width = 1;
                    return false;
                }

                void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
                {
                    if (!TryFindBindingInfo(curveIndexInStream, out var binding, out var bindingStart, out var bindingWidth)
                        || curveIndexInStream != bindingStart)
                    {
                        currentIdxOut++;
                        return;
                    }
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        if (TryResolveBindingBone(binding, out int meshBoneIdx) && ShouldUseAnimationBinding(meshBoneIdx))
                        {
                            if (binding.attribute == 1 || binding.attribute == 3 || binding.attribute == 4)
                            {
                                AddKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                                currentIdxOut += bindingWidth;
                            }
                            else if (binding.attribute == 2)
                            {
                                AddKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                                currentIdxOut += bindingWidth;
                            }
                            else currentIdxOut++;
                        }
                        else
                        {
                            currentIdxOut += bindingWidth;
                        }
                    }
                    else
                    {
                        currentIdxOut++;
                    }
                }

                void ProcessStreamedFrame(float time, AssetStudio.StreamedClip.StreamedCurveKey[] keyList)
                {
                    var valuesByIndex = keyList.ToDictionary(x => x.index, x => x.value);
                    var processedStarts = new HashSet<int>();

                    foreach (var key in keyList.OrderBy(x => x.index))
                    {
                        if (!TryFindBindingInfo(key.index, out var binding, out var bindingStart, out var bindingWidth)
                            || !processedStarts.Add(bindingStart)
                            || binding.typeID != ClassIDType.Transform
                            || !TryResolveBindingBone(binding, out int meshBoneIdx)
                            || !ShouldUseAnimationBinding(meshBoneIdx))
                        {
                            continue;
                        }

                        var values = new float[bindingWidth];
                        var hasAllComponents = true;
                        for (int i = 0; i < bindingWidth; i++)
                        {
                            if (valuesByIndex.TryGetValue(bindingStart + i, out var value))
                            {
                                values[i] = value;
                            }
                            else
                            {
                                hasAllComponents = false;
                                break;
                            }
                        }

                        if (hasAllComponents)
                        {
                            AddKeyframe(meshBoneIdx, binding.attribute, time, values, 0);
                        }
                    }
                }

                if (m_Clip.m_StreamedClip != null)
                {
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        ProcessStreamedFrame(frame.time, frame.keyList);
                    }
                }

                if (m_Clip.m_DenseClip != null)
                {
                    var dense = m_Clip.m_DenseClip;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    for (int frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
                    {
                        var time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                        var frameOffset = frameIndex * dense.m_CurveCount;
                        for (int cIdx = 0; cIdx < dense.m_CurveCount;)
                        {
                            ProcessCurveData((int)(streamCount + cIdx), time, dense.m_SampleArray, (int)frameOffset, ref cIdx);
                        }
                    }
                }

                if (m_Clip.m_ConstantClip != null)
                {
                    var constant = m_Clip.m_ConstantClip;
                    var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    var time2 = 0.0f;
                    for (int i = 0; i < 2; i++)
                    {
                        for (int cIdx = 0; cIdx < constant.data.Length;)
                        {
                            ProcessCurveData((int)(streamCount + denseCount + cIdx), time2, constant.data, 0, ref cIdx);
                        }
                        time2 = muscleClip.m_StopTime;
                    }
                }
            }
        }

        foreach (var track in posTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in rotTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in scaleTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }

        if (posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            // Fallback: show static bind pose with message
            if (GLPreviewControl != null)
            {
                GLPreviewControl.SetAvatar(avatarMesh, restBonePositions, meshParentIndices, meshBoneNames);
                GLPreviewControl.IsVisible = true;
                ShowPreviewGeometryControls(showBoneControls: true);
                GLPreviewControl.Focus();
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = false;
                StatusStripUpdate($"AnimationClip: {clip.m_Name} | No animation tracks extracted, showing bind pose | Bones: {meshBoneCount}");
            }
            return;
        }

        // Interpolation helpers
        global::OpenTK.Mathematics.Vector3 EvaluatePos(int meshBoneIdx, float t)
        {
            if (!posTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (int i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    float factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Quaternion EvaluateRot(int meshBoneIdx, float t)
        {
            if (!rotTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (int i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    float factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Quaternion.Slerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Vector3 EvaluateScale(int meshBoneIdx, float t)
        {
            if (!scaleTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (int i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    float factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            try { bindPoses[i] = bindPoseInverses[i].Inverted(); }
            catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
        }

        var restLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        var defaultPose = avatar.m_Avatar.m_DefaultPose?.m_X ?? avatar.m_Avatar.m_AvatarSkeletonPose?.m_X;
        for (int i = 0; i < meshBoneCount; i++)
        {
            int skelIdx = meshBoneToSkelNode[i];
            if (defaultPose != null && skelIdx >= 0 && skelIdx < defaultPose.Length)
            {
                var xform = defaultPose[skelIdx];
                restLocals[i] = CreateLocalMatrix(
                    ToOtkVector3(xform.t),
                    ToOtkQuaternion(xform.q),
                    ToOtkVector3(xform.s));
            }
            else
            {
                int pIdx = meshParentIndices[i];
                if (pIdx >= 0 && pIdx < meshBoneCount)
                {
                    restLocals[i] = bindPoses[i] * bindPoseInverses[pIdx];
                }
                else
                {
                    restLocals[i] = bindPoses[i];
                }
            }
        }

        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        foreach (var restPosition in restBonePositions)
        {
            if (!IsFiniteVector(restPosition))
            {
                continue;
            }

            restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, restPosition);
            restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, restPosition);
        }

        var restExtent = (restMax - restMin).Length;
        if (!float.IsFinite(restExtent) || restExtent <= 0)
        {
            restExtent = 1f;
        }

        var localPositionLimit = Math.Max(1f, restExtent * 2f);
        foreach (var item in posTracks.ToArray())
        {
            var boneIdx = item.Key;
            if (boneIdx < 0 || boneIdx >= meshBoneCount || meshParentIndices[boneIdx] < 0)
            {
                continue;
            }

            var restLocalPosition = restLocals[boneIdx].ExtractTranslation();
            if (item.Value.Any(x => !IsFiniteVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(boneIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            if (item.Value.Any(x => !IsFiniteVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > 10f || Math.Abs(x.value.Y) > 10f || Math.Abs(x.value.Z) > 10f))
            {
                scaleTracks.Remove(item.Key);
            }
        }

        // Step 5: Compute per-frame bone positions
        float sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        if (maxTime <= 0) maxTime = muscleClip?.m_StopTime > 0 ? muscleClip.m_StopTime : 1f;
        int frameCount = (int)(maxTime * sampleRate);
        if (frameCount == 0) frameCount = 1;

        var allFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        var allBoneMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];
        for (int f = 0; f < frameCount; f++)
        {
            float t = f / sampleRate;
            var framePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            var modelMatrices = new global::OpenTK.Mathematics.Matrix4?[meshBoneCount];
            
            global::OpenTK.Mathematics.Matrix4 GetModelMatrix(int bIdx)
            {
                if (modelMatrices[bIdx] is global::OpenTK.Mathematics.Matrix4 cached) return cached;

                var localMat = restLocals[bIdx];
                bool hasPos = posTracks.ContainsKey(bIdx);
                bool hasRot = rotTracks.ContainsKey(bIdx);
                bool hasScale = scaleTracks.ContainsKey(bIdx);

                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvaluatePos(bIdx, t) : localMat.ExtractTranslation();
                    var rot = hasRot ? EvaluateRot(bIdx, t) : localMat.ExtractRotation();
                    var scale = hasScale ? EvaluateScale(bIdx, t) : localMat.ExtractScale();
                    
                    localMat = CreateLocalMatrix(pos, rot, scale);
                }

                int pIdx = meshParentIndices[bIdx];
                if (pIdx >= 0 && pIdx != bIdx && pIdx < meshBoneCount)
                {
                    var pMat = GetModelMatrix(pIdx);
                    var worldMat = localMat * pMat;
                    modelMatrices[bIdx] = worldMat;
                    return worldMat;
                }
                else
                {
                    modelMatrices[bIdx] = localMat;
                    return localMat;
                }
            }

            for (int meshBoneIdx = 0; meshBoneIdx < meshBoneCount; meshBoneIdx++)
            {
                var mat = GetModelMatrix(meshBoneIdx);
                framePositions[meshBoneIdx] = mat.ExtractTranslation();
                frameMatrices[meshBoneIdx] = mat;
            }

            allFrames[f] = framePositions;
            allBoneMatrices[f] = frameMatrices;
        }

        var renderParentIndices = (int[])meshParentIndices.Clone();
        var hiddenRenderBones = new bool[meshBoneCount];
        for (int i = 0; i < meshBoneCount; i++)
        {
            if (!deformChainMask[i] || (!weightedBoneMask[i] && IsAuxiliaryAnimationBone(meshBoneNames[i])))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (int i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx >= 0 && parentIdx < meshBoneCount
                && IsFiniteVector(restBonePositions[i])
                && IsFiniteVector(restBonePositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (restBonePositions[i] - restBonePositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (int i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx < 0 || parentIdx >= meshBoneCount)
            {
                continue;
            }

            foreach (var frame in allFrames)
            {
                if (!IsFiniteVector(frame[i]) || !IsFiniteVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (int f = 0; f < allFrames.Length; f++)
        {
            for (int i = 0; i < meshBoneCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = meshParentIndices[i];
                allFrames[f][i] = parentIdx >= 0 && parentIdx < meshBoneCount
                    ? allFrames[f][parentIdx]
                    : restBonePositions[i];
            }
        }

        // Step 6: Send to GL preview
        if (GLPreviewControl != null)
        {
            GLPreviewControl.SetAnimatedAvatar(avatarMesh, allFrames, allBoneMatrices, renderParentIndices, sampleRate, meshBoneNames);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;

            ShowAnimationPlayback(frameCount);

            var targetStatus = resolvedCandidate?.IsModelGroup == true
                ? $" | Model group: {(!string.IsNullOrWhiteSpace(resolvedCandidate.ModelGroupName) ? resolvedCandidate.ModelGroupName : resolvedCandidate.ModelGroupId)} ({resolvedCandidate.ModelGroupMeshCount:N0} parts) | Representative mesh: {avatarMesh.m_Name}"
                : $" | Mesh: {avatarMesh.m_Name}";
            StatusStripUpdate($"Animation Preview | Clip: {clip.m_Name}{targetStatus} | Frames: {frameCount} | FPS: {sampleRate} | Tracks: {posTracks.Count + rotTracks.Count + scaleTracks.Count}");
        }
    }

    private Mesh? FindBestMeshForAvatar(Avatar avatar)
    {
        return FindMeshesForAvatar(avatar).FirstOrDefault();
    }

    private Avatar? FindBestAvatarForMesh(Mesh mesh)
    {
        if (meshAvatarCache != null && meshAvatarCache.TryGetValue(mesh, out var avatar) && avatar != null)
        {
            return avatar;
        }

        return TryLoadAvatarsForMeshFromSemanticCache(mesh).FirstOrDefault();
    }

    private Mesh? GetCachedMeshForAvatar(Avatar avatar)
    {
        return ResolveFirstMeshForAvatar(avatar);
    }

    private static Dictionary<Avatar, List<Mesh>> BuildAvatarMeshListCache(Dictionary<Avatar, Mesh?>? singleMeshCache)
    {
        var result = new Dictionary<Avatar, List<Mesh>>();
        if (singleMeshCache == null)
        {
            return result;
        }

        foreach (var entry in singleMeshCache)
        {
            result[entry.Key] = entry.Value != null
                ? new List<Mesh> { entry.Value }
                : new List<Mesh>();
        }

        return result;
    }

    private List<Mesh> FindMeshesForAvatar(Avatar avatar)
    {
        if (avatarMeshesCache != null
            && avatarMeshesCache.TryGetValue(avatar, out var cachedMeshes)
            && cachedMeshes.Count > 0)
        {
            return new List<Mesh>(cachedMeshes);
        }

        var meshes = new List<Mesh>();
        if (avatarMeshCache != null
            && avatarMeshCache.TryGetValue(avatar, out var cachedMesh)
            && cachedMesh != null)
        {
            meshes.Add(cachedMesh);
        }

        foreach (var mesh in TryLoadMeshesForAvatarFromSemanticCache(avatar))
        {
            if (!meshes.Any(existing => ReferenceEquals(existing, mesh)))
            {
                meshes.Add(mesh);
            }
        }

        if (meshes.Count > 0)
        {
            avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
            avatarMeshesCache[avatar] = meshes;

            avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
            avatarMeshCache[avatar] = meshes[0];

            meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
            foreach (var mesh in meshes)
            {
                meshAvatarCache[mesh] = avatar;
            }
        }

        return meshes;
    }

    private List<Mesh> TryLoadMeshesForAvatarFromSemanticCache(Avatar avatar)
    {
        var meshes = new List<Mesh>();
        if (!assetsManager.LazyLoading || avatar.assetsFile == null || currentScanResult == null)
        {
            return meshes;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return meshes;
        }

        var avatarAssetId = AssetHandle.BuildUniqueID(avatar.assetsFile, avatar.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var meshIds = _sqliteCache.LoadAvatarMeshAssetIds(folderPath, signature, avatarAssetId);
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

    private List<Avatar> TryLoadAvatarsForMeshFromSemanticCache(Mesh mesh)
    {
        var avatars = new List<Avatar>();
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return avatars;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return avatars;
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var avatarIds = _sqliteCache.LoadMeshAvatarAssetIds(folderPath, signature, meshAssetId);
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
            meshAvatarCache ??= new Dictionary<Mesh, Avatar?>();
            meshAvatarCache[mesh] = avatars[0];

            avatarMeshCache ??= new Dictionary<Avatar, Mesh?>();
            avatarMeshesCache ??= new Dictionary<Avatar, List<Mesh>>();
            foreach (var avatar in avatars)
            {
                avatarMeshCache.TryAdd(avatar, mesh);
                if (!avatarMeshesCache.TryGetValue(avatar, out var meshes))
                {
                    meshes = new List<Mesh>();
                    avatarMeshesCache[avatar] = meshes;
                }

                if (!meshes.Any(existing => ReferenceEquals(existing, mesh)))
                {
                    meshes.Add(mesh);
                }
            }
        }

        return avatars;
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

        if (animationClipAvatarCache != null && animationClipAvatarCache.TryGetValue(clip, out var cachedAvatar))
        {
            AddCandidate(cachedAvatar);
        }

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
            animationClipAvatarCache ??= new Dictionary<AnimationClip, Avatar?>();
            animationClipAvatarCache[clip] = avatars[0];
        }

        return avatars;
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

        var bindPoseInverses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var bp = mesh.m_BindPose[i];
            var otkMat = new global::OpenTK.Mathematics.Matrix4(
                bp.M00, bp.M01, bp.M02, bp.M03,
                bp.M10, bp.M11, bp.M12, bp.M13,
                bp.M20, bp.M21, bp.M22, bp.M23,
                bp.M30, bp.M31, bp.M32, bp.M33);
            try
            {
                bindPoseInverses[i] = otkMat.Inverted();
            }
            catch
            {
                bindPoseInverses[i] = global::OpenTK.Mathematics.Matrix4.Identity;
            }
        }

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        if (mesh.m_BoneNameHashes != null)
        {
            for (var i = 0; i < Math.Min(mesh.m_BoneNameHashes.Length, meshBoneCount); i++)
            {
                meshBoneHashToIdx[mesh.m_BoneNameHashes[i]] = i;
            }
        }

        var meshBonePathToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Min(meshBoneNames.Length, meshBoneCount); i++)
        {
            AddPreviewBonePathAlias(meshBonePathToIdx, meshBoneNames[i], i);
        }

        bool TryResolveCurvePath(string? path, out int meshBoneIdx)
        {
            path = NormalizePreviewAnimationPath(path);
            if (!string.IsNullOrEmpty(path))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var start = 0; start < parts.Length; start++)
                {
                    var alias = string.Join("/", parts.Skip(start));
                    if (meshBonePathToIdx.TryGetValue(alias, out meshBoneIdx) && meshBoneIdx >= 0)
                    {
                        return true;
                    }
                }
            }

            meshBoneIdx = -1;
            return false;
        }

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            return meshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        var weightedBoneMask = new bool[meshBoneCount];
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
        var deformChainMask = new bool[meshBoneCount];
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

        bool ShouldUseAnimationBinding(int meshBoneIdx)
        {
            if (meshBoneIdx < 0 || meshBoneIdx >= meshBoneCount)
            {
                return false;
            }

            if (!deformChainMask[meshBoneIdx])
            {
                return false;
            }

            return weightedBoneMask[meshBoneIdx] || !IsAuxiliaryPreviewAnimationBone(meshBoneNames.ElementAtOrDefault(meshBoneIdx));
        }

        var posTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        var rotTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>>();
        var scaleTracks = new Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>>();
        float maxTime = 0f;

        void AddPosition(int boneIdx, float time, global::OpenTK.Mathematics.Vector3 value)
        {
            if (!ShouldUseAnimationBinding(boneIdx))
            {
                return;
            }

            if (time > maxTime) maxTime = time;
            if (!posTracks.TryGetValue(boneIdx, out var list)) posTracks[boneIdx] = list = new();
            list.Add((time, value));
        }

        void AddRotation(int boneIdx, float time, global::OpenTK.Mathematics.Quaternion value)
        {
            if (!ShouldUseAnimationBinding(boneIdx))
            {
                return;
            }

            if (value.LengthSquared > 0)
            {
                value.Normalize();
            }

            if (time > maxTime) maxTime = time;
            if (!rotTracks.TryGetValue(boneIdx, out var list)) rotTracks[boneIdx] = list = new();
            list.Add((time, value));
        }

        void AddScale(int boneIdx, float time, global::OpenTK.Mathematics.Vector3 value)
        {
            if (!ShouldUseAnimationBinding(boneIdx))
            {
                return;
            }

            if (time > maxTime) maxTime = time;
            if (!scaleTracks.TryGetValue(boneIdx, out var list)) scaleTracks[boneIdx] = list = new();
            list.Add((time, value));
        }

        foreach (var curve in clip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                AddPosition(boneIdx, key.time, ToOtkVector3Preview(key.value));
            }
        }

        foreach (var curve in clip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                AddRotation(boneIdx, key.time, ToOtkQuaternionPreview(key.value));
            }
        }

        foreach (var curve in clip.m_EulerCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                var euler = ToOtkVector3Preview(key.value);
                euler = new global::OpenTK.Mathematics.Vector3(
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.X),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.Y),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.Z));
                AddRotation(boneIdx, key.time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler));
            }
        }

        foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!TryResolveCurvePath(curve.path, out var boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                AddScale(boneIdx, key.time, ToOtkVector3Preview(key.value));
            }
        }

        void AddGenericKeyframe(int meshBoneIdx, uint attribute, float time, float[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return;
            }

            switch (attribute)
            {
                case 1 when offset + 2 < data.Length:
                    AddPosition(meshBoneIdx, time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2]));
                    break;
                case 2 when offset + 3 < data.Length:
                    AddRotation(meshBoneIdx, time, new global::OpenTK.Mathematics.Quaternion(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]));
                    break;
                case 3 when offset + 2 < data.Length:
                    AddScale(meshBoneIdx, time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2]));
                    break;
                case 4 when offset + 2 < data.Length:
                    var euler = new global::OpenTK.Mathematics.Vector3(
                        global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset]),
                        global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 1]),
                        global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 2]));
                    AddRotation(meshBoneIdx, time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler));
                    break;
            }
        }

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
            int GetBindingCurveWidth(GenericBinding binding)
            {
                if (binding.typeID != ClassIDType.Transform)
                {
                    return 1;
                }

                return binding.attribute switch
                {
                    1 => 3,
                    2 => 4,
                    3 => 3,
                    4 => 3,
                    _ => 1
                };
            }

            bool TryFindBindingInfo(int index, out GenericBinding binding, out int startIndex, out int width)
            {
                var curves = 0;
                foreach (var candidate in bindings.genericBindings)
                {
                    var candidateWidth = GetBindingCurveWidth(candidate);
                    if (index >= curves && index < curves + candidateWidth)
                    {
                        binding = candidate;
                        startIndex = curves;
                        width = candidateWidth;
                        return true;
                    }

                    curves += candidateWidth;
                }

                binding = default!;
                startIndex = -1;
                width = 1;
                return false;
            }

            void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
            {
                if (!TryFindBindingInfo(curveIndexInStream, out var binding, out var bindingStart, out var bindingWidth)
                    || curveIndexInStream != bindingStart)
                {
                    currentIdxOut++;
                    return;
                }

                if (binding.typeID == ClassIDType.Transform && TryResolveBindingBone(binding, out var meshBoneIdx))
                {
                    AddGenericKeyframe(meshBoneIdx, binding.attribute, time, data, currentIdxOut + dataOffset);
                }

                currentIdxOut += binding.typeID == ClassIDType.Transform ? bindingWidth : 1;
            }

            void ProcessStreamedFrame(float time, AssetStudio.StreamedClip.StreamedCurveKey[] keyList)
            {
                var valuesByIndex = keyList.ToDictionary(x => x.index, x => x.value);
                var processedStarts = new HashSet<int>();

                foreach (var key in keyList.OrderBy(x => x.index))
                {
                    if (!TryFindBindingInfo(key.index, out var binding, out var bindingStart, out var bindingWidth)
                        || !processedStarts.Add(bindingStart)
                        || binding.typeID != ClassIDType.Transform
                        || !TryResolveBindingBone(binding, out var meshBoneIdx))
                    {
                        continue;
                    }

                    var values = new float[bindingWidth];
                    var hasAllComponents = true;
                    for (var i = 0; i < bindingWidth; i++)
                    {
                        if (valuesByIndex.TryGetValue(bindingStart + i, out var value))
                        {
                            values[i] = value;
                        }
                        else
                        {
                            hasAllComponents = false;
                            break;
                        }
                    }

                    if (hasAllComponents)
                    {
                        AddGenericKeyframe(meshBoneIdx, binding.attribute, time, values, 0);
                    }
                }
            }

            if (innerClip.m_StreamedClip != null)
            {
                var streamedFrames = innerClip.m_StreamedClip.ReadData();
                for (var frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                {
                    var frame = streamedFrames[frameIndex];
                    ProcessStreamedFrame(frame.time, frame.keyList);
                }
            }

            if (innerClip.m_DenseClip != null)
            {
                var dense = innerClip.m_DenseClip;
                var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
                for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
                {
                    var time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                    var frameOffset = frameIndex * dense.m_CurveCount;
                    for (var cIdx = 0; cIdx < dense.m_CurveCount;)
                    {
                        var curveIndex = (int)(streamCount + cIdx);
                        ProcessCurveData(curveIndex, time, dense.m_SampleArray, (int)frameOffset, ref cIdx);
                    }
                }
            }

            if (innerClip.m_ConstantClip != null)
            {
                var constant = innerClip.m_ConstantClip;
                var denseCount = innerClip.m_DenseClip?.m_CurveCount ?? 0;
                var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
                var time = 0.0f;
                for (var i = 0; i < 2; i++)
                {
                    for (var cIdx = 0; cIdx < constant.data.Length;)
                    {
                        var curveIndex = (int)(streamCount + denseCount + cIdx);
                        ProcessCurveData(curveIndex, time, constant.data, 0, ref cIdx);
                    }

                    time = muscleClip?.m_StopTime ?? time;
                }
            }
        }

        foreach (var track in posTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in rotTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
        foreach (var track in scaleTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }

        trackCount = posTracks.Count + rotTracks.Count + scaleTracks.Count;
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
            posTracks,
            rotTracks,
            scaleTracks,
            sampleRate,
            maxTime > 0f ? maxTime : muscleClip?.m_StopTime ?? 1f,
            out allFrames,
            out allBoneMatrices,
            out renderParentIndices,
            out reason);
    }

    private static string NormalizePreviewAnimationPath(string? path)
    {
        return (path ?? string.Empty)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Trim('/');
    }

    private static void AddPreviewBonePathAlias(Dictionary<string, int> bonePathToIndex, string? path, int meshBoneIdx)
    {
        path = NormalizePreviewAnimationPath(path);
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

    private static global::OpenTK.Mathematics.Vector3 ToOtkVector3Preview(AssetStudio.Vector3 value)
    {
        return new global::OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
    }

    private static global::OpenTK.Mathematics.Quaternion ToOtkQuaternionPreview(AssetStudio.Quaternion value)
    {
        var q = new global::OpenTK.Mathematics.Quaternion(value.X, value.Y, value.Z, value.W);
        if (q.LengthSquared > 0)
        {
            q.Normalize();
        }
        return q;
    }

    private static global::OpenTK.Mathematics.Matrix4 CreatePreviewLocalMatrix(
        global::OpenTK.Mathematics.Vector3 position,
        global::OpenTK.Mathematics.Quaternion rotation,
        global::OpenTK.Mathematics.Vector3 scale)
    {
        return global::OpenTK.Mathematics.Matrix4.CreateScale(scale)
            * global::OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rotation)
            * global::OpenTK.Mathematics.Matrix4.CreateTranslation(position);
    }

    private static bool IsFinitePreviewVector(global::OpenTK.Mathematics.Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsAuxiliaryPreviewAnimationBone(string? path)
    {
        var normalized = NormalizePreviewAnimationPath(path).ToLowerInvariant();
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
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        reason = string.Empty;

        var meshBoneCount = Math.Min(mesh.m_BindPose?.Length ?? 0, Math.Min(restBonePositions.Length, meshParentIndices.Length));
        if (meshBoneCount == 0)
        {
            reason = "No bones available for frame generation.";
            return false;
        }

        global::OpenTK.Mathematics.Vector3 EvaluatePos(int meshBoneIdx, float t)
        {
            if (!posTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Quaternion EvaluateRot(int meshBoneIdx, float t)
        {
            if (!rotTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Quaternion.Slerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        global::OpenTK.Mathematics.Vector3 EvaluateScale(int meshBoneIdx, float t)
        {
            if (!scaleTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }
            return track[^1].value;
        }

        var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            try { bindPoses[i] = bindPoseInverses[i].Inverted(); }
            catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
        }

        var restLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var pIdx = meshParentIndices[i];
            restLocals[i] = pIdx >= 0 && pIdx < meshBoneCount
                ? bindPoses[i] * bindPoseInverses[pIdx]
                : bindPoses[i];
        }

        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var restPosition = restBonePositions[i];
            if (!IsFinitePreviewVector(restPosition))
            {
                continue;
            }

            restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, restPosition);
            restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, restPosition);
        }

        var restExtent = (restMax - restMin).Length;
        if (!float.IsFinite(restExtent) || restExtent <= 0)
        {
            restExtent = 1f;
        }

        var localPositionLimit = Math.Max(1f, restExtent * 2f);
        foreach (var item in posTracks.ToArray())
        {
            var boneIdx = item.Key;
            if (boneIdx < 0 || boneIdx >= meshBoneCount || meshParentIndices[boneIdx] < 0)
            {
                continue;
            }

            var restLocalPosition = restLocals[boneIdx].ExtractTranslation();
            if (item.Value.Any(x => !IsFinitePreviewVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(boneIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            if (item.Value.Any(x => !IsFinitePreviewVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > 10f || Math.Abs(x.value.Y) > 10f || Math.Abs(x.value.Z) > 10f))
            {
                scaleTracks.Remove(item.Key);
            }
        }

        if (posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            reason = "All extracted tracks were rejected by safety filters.";
            return false;
        }

        if (sampleRate <= 0f || !float.IsFinite(sampleRate))
        {
            sampleRate = 30f;
        }
        if (maxTime <= 0f || !float.IsFinite(maxTime))
        {
            maxTime = 1f;
        }

        var frameCount = Math.Max(1, (int)Math.Ceiling(maxTime * sampleRate));
        allFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        allBoneMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];
        for (var f = 0; f < frameCount; f++)
        {
            var t = f / sampleRate;
            var framePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            var modelMatrices = new global::OpenTK.Mathematics.Matrix4?[meshBoneCount];

            global::OpenTK.Mathematics.Matrix4 GetModelMatrix(int bIdx)
            {
                if (modelMatrices[bIdx] is global::OpenTK.Mathematics.Matrix4 cached)
                {
                    return cached;
                }

                var localMat = restLocals[bIdx];
                var hasPos = posTracks.ContainsKey(bIdx);
                var hasRot = rotTracks.ContainsKey(bIdx);
                var hasScale = scaleTracks.ContainsKey(bIdx);

                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvaluatePos(bIdx, t) : localMat.ExtractTranslation();
                    var rot = hasRot ? EvaluateRot(bIdx, t) : localMat.ExtractRotation();
                    var scale = hasScale ? EvaluateScale(bIdx, t) : localMat.ExtractScale();
                    localMat = CreatePreviewLocalMatrix(pos, rot, scale);
                }

                var pIdx = meshParentIndices[bIdx];
                if (pIdx >= 0 && pIdx != bIdx && pIdx < meshBoneCount)
                {
                    var worldMat = localMat * GetModelMatrix(pIdx);
                    modelMatrices[bIdx] = worldMat;
                    return worldMat;
                }

                modelMatrices[bIdx] = localMat;
                return localMat;
            }

            for (var meshBoneIdx = 0; meshBoneIdx < meshBoneCount; meshBoneIdx++)
            {
                var mat = GetModelMatrix(meshBoneIdx);
                framePositions[meshBoneIdx] = mat.ExtractTranslation();
                frameMatrices[meshBoneIdx] = mat;
            }

            allFrames[f] = framePositions;
            allBoneMatrices[f] = frameMatrices;
        }

        renderParentIndices = meshParentIndices.Take(meshBoneCount).ToArray();
        var hiddenRenderBones = new bool[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var boneName = i < meshBoneNames.Length ? meshBoneNames[i] : string.Empty;
            if (!deformChainMask[i] || (!weightedBoneMask[i] && IsAuxiliaryPreviewAnimationBone(boneName)))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx >= 0 && parentIdx < meshBoneCount
                && IsFinitePreviewVector(restBonePositions[i])
                && IsFinitePreviewVector(restBonePositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (restBonePositions[i] - restBonePositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx < 0 || parentIdx >= meshBoneCount)
            {
                continue;
            }

            foreach (var frame in allFrames)
            {
                if (!IsFinitePreviewVector(frame[i]) || !IsFinitePreviewVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (var f = 0; f < allFrames.Length; f++)
        {
            for (var i = 0; i < meshBoneCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = meshParentIndices[i];
                allFrames[f][i] = parentIdx >= 0 && parentIdx < meshBoneCount
                    ? allFrames[f][parentIdx]
                    : restBonePositions[i];
            }
        }

        return true;
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
        if (animationClipAvatarCache != null && animationClipAvatarCache.TryGetValue(clip, out var cachedAvatar))
        {
            if (ReferenceEquals(cachedAvatar, avatar))
            {
                return true;
            }
        }

        animationClipTransformBindingsCache ??= new Dictionary<AnimationClip, HashSet<uint>>();
        if (!animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths))
        {
            bindingPaths = GetTransformBindingPathsBackground(clip);
            animationClipTransformBindingsCache[clip] = bindingPaths;
        }

        if (bindingPaths.Count == 0 || avatar.m_TOS == null)
        {
            return false;
        }

        var avatarPathHashes = new HashSet<uint>(avatar.m_TOS.Select(x => x.Key));
        var overlap = bindingPaths.Count(avatarPathHashes.Contains);
        return IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap);
    }

    private Material? ResolveMaterialForPreview(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        return ResolveMaterialForPreviewBackground(material, materialPreviewMaterialCache);
    }

    private Material? ResolveMaterialForPreviewUncached(Material material)
    {
        return ResolveMaterialForPreviewUncachedBackground(material);
    }

    private static readonly string[] PreferredMaterialTextureSlots = MaterialPreviewBuilder.PreferredTextureSlots;

    private Material? FindMaterialForMesh(Mesh mesh)
    {
        return FindMaterialsForMesh(mesh).FirstOrDefault(material => material != null);
    }

    private List<Material?> FindMaterialsForMeshPreview(Mesh mesh)
    {
        if (!assetsManager.LazyLoading)
        {
            return FindMaterialsForMesh(mesh);
        }

        if (meshToMaterialsCache != null && meshToMaterialsCache.TryGetValue(mesh, out var cachedList))
        {
            return new List<Material?>(cachedList);
        }

        var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
        if (semanticMaterials.Count > 0)
        {
            return semanticMaterials;
        }

        if (CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
        {
            return new List<Material?>();
        }

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        if (filesSnapshot.Count > 0)
        {
            using (assetsManager.LruCache.SuspendEviction())
            {
                BuildLazyPreviewReferenceIndexes(filesSnapshot);
            }

            if (meshToMaterialsCache != null && meshToMaterialsCache.TryGetValue(mesh, out cachedList))
            {
                return new List<Material?>(cachedList);
            }
        }

        return new List<Material?>();
    }

    private static readonly HashSet<ClassIDType> MaterialPreviewReferenceTypes = new()
    {
        ClassIDType.Material,
        ClassIDType.Texture2D
    };

    private static readonly HashSet<ClassIDType> MeshPreviewReferenceTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Material
    };

    private static readonly HashSet<ClassIDType> SpritePreviewReferenceTypes = new()
    {
        ClassIDType.Sprite,
        ClassIDType.Texture2D,
        ClassIDType.SpriteAtlas
    };

    private static readonly HashSet<ClassIDType> AnimationClip2DPreviewReferenceTypes = new()
    {
        ClassIDType.AnimationClip,
        ClassIDType.Sprite,
        ClassIDType.Texture2D,
        ClassIDType.SpriteAtlas
    };

    private static readonly HashSet<ClassIDType> LazyConnectionReferenceTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Material,
        ClassIDType.Avatar,
        ClassIDType.Animator,
        ClassIDType.AnimatorController,
        ClassIDType.AnimatorOverrideController,
        ClassIDType.RuntimeAnimatorController
    };

    private static readonly HashSet<string> NonDiffuseSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
        "_Normal", "Normal", "normal",
        "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
        "_EmissionMap", "_ParallaxMap", "_DetailMask",
        "_Cubemap", "_ReflectionTex", "_ShadowMap"
    };

    private Texture2D? FindTextureForMaterial(Material material)
    {
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialMainTextureCache.TryGetValue(material, out var directCachedTexture))
        {
            return directCachedTexture;
        }

        var semanticTexture = TryLoadTextureForMaterialFromSemanticCache(material);
        if (semanticTexture != null)
        {
            return semanticTexture;
        }

        IndexMaterialTextures(material);
        return materialMainTextureCache.TryGetValue(material, out var indexedTexture) ? indexedTexture : null;
    }

    private Texture2D? SelectMainTextureForMaterial(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
    {
        return SelectMainTextureForMaterialBackground(displayMaterial, textureSlots);
    }

    private Texture2D? GetMaterialTextureSlot(Material material, string slotName)
    {
        var cachedTexture = materialTextureSlotsCache != null
            && materialTextureSlotsCache.TryGetValue(material, out var slots)
            && slots.TryGetValue(slotName, out var texture)
            ? texture
            : null;

        if (cachedTexture != null)
        {
            return cachedTexture;
        }

        var semanticTexture = TryLoadTextureForMaterialFromSemanticCache(material, slotName);
        if (semanticTexture != null)
        {
            return semanticTexture;
        }

        IndexMaterialTextures(material);
        return materialTextureSlotsCache != null
               && materialTextureSlotsCache.TryGetValue(material, out slots)
               && slots.TryGetValue(slotName, out texture)
            ? texture
            : null;
    }

    private Texture2D? TryLoadTextureForMaterialFromSemanticCache(Material material, string? slotName = null)
    {
        if (!assetsManager.LazyLoading || material.assetsFile == null || currentScanResult == null)
        {
            return null;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return null;
        }

        var materialAssetId = AssetHandle.BuildUniqueID(material.assetsFile, material.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var texture = TryResolveTextureIdsForMaterialFromSemanticCache(folderPath, signature, material, materialAssetId, slotName);
        if (texture != null)
        {
            return texture;
        }

        var parentMaterialId = GetMaterialParentAssetIdForSemanticCache(material);
        if (!string.IsNullOrEmpty(parentMaterialId)
            && !string.Equals(parentMaterialId, materialAssetId, StringComparison.Ordinal))
        {
            return TryResolveTextureIdsForMaterialFromSemanticCache(folderPath, signature, material, parentMaterialId, slotName);
        }

        return null;
    }

    private Texture2D? TryResolveTextureIdsForMaterialFromSemanticCache(
        string folderPath,
        string signature,
        Material cacheMaterial,
        string materialAssetId,
        string? slotName)
    {
        var textureIds = _sqliteCache.LoadMaterialTextureAssetIds(folderPath, signature, materialAssetId, slotName);
        foreach (var textureId in textureIds)
        {
            var handle = assetsManager.ProjectIndex.GetHandle(textureId);
            if (handle == null)
            {
                continue;
            }

            var asset = ResolveSemanticRelationHandleForPreview(handle);
            if (asset is not Texture2D texture)
            {
                continue;
            }

            if (string.IsNullOrEmpty(slotName))
            {
                materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();
                materialMainTextureCache[cacheMaterial] = texture;
            }
            else
            {
                materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
                if (!materialTextureSlotsCache.TryGetValue(cacheMaterial, out var slots))
                {
                    slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
                    materialTextureSlotsCache[cacheMaterial] = slots;
                }

                slots[slotName] = texture;
            }

            return texture;
        }

        return null;
    }

    private string GetMaterialParentAssetIdForSemanticCache(Material material)
    {
        if (material.assetsFile == null || material.m_Parent == null || material.m_Parent.IsNull)
        {
            return string.Empty;
        }

        if (material.m_Parent.TryGet(out var parentMaterial) && parentMaterial?.assetsFile != null)
        {
            return AssetHandle.BuildUniqueID(parentMaterial.assetsFile, parentMaterial.m_PathID);
        }

        var handle = FindMaterialHandleForPPtr(material, material.m_Parent);
        return handle?.UniqueID ?? string.Empty;
    }

    private void IndexMaterialTextures(Material material)
    {
        materialPreviewMaterialCache ??= new Dictionary<Material, Material?>();
        materialTextureSlotsCache ??= new Dictionary<Material, Dictionary<string, Texture2D?>>();
        materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();

        if (materialTextureSlotsCache.ContainsKey(material) && materialMainTextureCache.ContainsKey(material))
        {
            return;
        }

        var displayMaterial = ResolveMaterialForPreview(material) ?? material;
        if (!materialTextureSlotsCache.TryGetValue(displayMaterial, out var slots))
        {
            slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
            foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
            {
                var textureRef = texEnv.Value?.m_Texture;
                slots[texEnv.Key] = textureRef != null && !textureRef.IsNull
                    ? ResolveTexturePPtrForPreview(displayMaterial, textureRef)
                    : null;
            }

            materialTextureSlotsCache[displayMaterial] = slots;
            materialMainTextureCache[displayMaterial] = SelectMainTextureForMaterial(displayMaterial, slots);
        }

        if (!ReferenceEquals(displayMaterial, material))
        {
            materialTextureSlotsCache[material] = slots;
            materialMainTextureCache[material] = materialMainTextureCache[displayMaterial];
        }
    }

    private void EnsureMeshPreviewDependenciesLoaded(Mesh mesh)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(mesh.assetsFile);
        MaterializeReferenceObjects(mesh.assetsFile, MeshPreviewReferenceTypes);

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, MeshPreviewReferenceTypes);
        }

        BuildLazyPreviewReferenceIndexes(filesSnapshot);
    }

    private void EnsureMaterialPreviewDependenciesLoaded(Material material)
    {
        if (!assetsManager.LazyLoading || material.assetsFile == null)
        {
            return;
        }

        EnsureIndexedExternalSourcesLoaded(material.assetsFile);

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, MaterialPreviewReferenceTypes);
        }
    }

    private void EnsureSpritePreviewDependenciesLoaded(Sprite sprite)
    {
        if (!assetsManager.LazyLoading || sprite.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(sprite.assetsFile);
        MaterializeReferenceObjects(sprite.assetsFile, SpritePreviewReferenceTypes);

        var filesSnapshot = GetLoadedFilesSnapshot();
        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, SpritePreviewReferenceTypes);
        }

        PrepareLoadedSpriteAtlasReferencesForPreview(filesSnapshot);
    }

    private void EnsureAnimationClip2DPreviewDependenciesLoaded(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(clip.assetsFile);
        MaterializeReferenceObjects(clip.assetsFile, AnimationClip2DPreviewReferenceTypes);

        var filesSnapshot = GetLoadedFilesSnapshot();
        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, AnimationClip2DPreviewReferenceTypes);
        }

        PrepareLoadedSpriteAtlasReferencesForPreview(filesSnapshot);
    }

    private void EnsureIndexedExternalSourcesLoaded(SerializedFile sourceFile)
    {
        if (sourceFile == null)
        {
            return;
        }

        var pending = new Queue<SerializedFile>();
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedExternals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(sourceFile);

        while (pending.Count > 0)
        {
            var currentFile = pending.Dequeue();
            if (currentFile == null || !processedFiles.Add(GetSerializedFileDependencyKey(currentFile)))
            {
                continue;
            }

            if (currentFile.m_Externals == null || currentFile.m_Externals.Count == 0)
            {
                continue;
            }

            foreach (var external in currentFile.m_Externals)
            {
                if (external == null || (string.IsNullOrWhiteSpace(external.fileName) && string.IsNullOrWhiteSpace(external.pathName)))
                {
                    continue;
                }

                var externalKey = $"{external.fileName}|{external.pathName}";
                if (!processedExternals.Add(externalKey))
                {
                    continue;
                }

                if (TryGetLoadedExternalFile(external, out var loadedExternalFile))
                {
                    pending.Enqueue(loadedExternalFile);
                    continue;
                }

                var sourcePath = ResolveIndexedExternalSourcePath(external);
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                try
                {
                    RemovePendingFileFromProgressiveQueue(sourcePath);
                    assetsManager.LoadFilesForPreview(sourcePath);
                    assetsManager.WaitForAssetsFileLoaded(external.fileName, 5000);
                    if (TryGetLoadedExternalFile(external, out loadedExternalFile))
                    {
                        pending.Enqueue(loadedExternalFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load external preview source {Path.GetFileName(sourcePath)}: {ex.Message}");
                }
            }
        }
    }

    private List<SerializedFile> GetLoadedFilesSnapshot()
    {
        lock (assetsManager.loadLock)
        {
            return assetsManager.assetsFileList.ToList();
        }
    }

    private bool TryGetLoadedExternalFile(FileIdentifier external, out SerializedFile sourceFile)
    {
        sourceFile = null!;
        if (external == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(external.fileName)
            && ((assetsManager.TryFindSerializedFile(external.fileName, null, out sourceFile) && sourceFile != null)
                || (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out sourceFile) && sourceFile != null)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(external.pathName)
            && assetsManager.TryFindSerializedFile(Path.GetFileName(external.pathName), external.pathName, out sourceFile)
            && sourceFile != null)
        {
            return true;
        }

        return false;
    }

    private string? ResolveIndexedExternalSourcePath(FileIdentifier external)
    {
        if (external == null)
        {
            return null;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidateFileName(candidates, external.fileName);
        AddCandidateFileName(candidates, external.pathName);

        foreach (var candidate in candidates)
        {
            var direct = TryResolveExistingPath(candidate);
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }

            if (!string.IsNullOrEmpty(assetsManager.ProjectRoot))
            {
                direct = TryResolveExistingPath(Path.Combine(assetsManager.ProjectRoot, candidate));
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }
            }

            if (lazySourcePathBySerializedFile.TryGetValue(candidate, out var mappedSourcePath))
            {
                direct = TryResolveExistingPath(mappedSourcePath);
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }
            }

            var indexedPath = assetsManager.ProjectIndex
                .GetHandlesForFile(candidate)
                .Select(ResolveLazyHandleSourcePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (!string.IsNullOrEmpty(indexedPath))
            {
                return indexedPath;
            }
        }

        foreach (var candidate in candidates)
        {
            var found = FindSourceFileByNameInProjectRoot(candidate);
            if (!string.IsNullOrEmpty(found))
            {
                return found;
            }
        }

        return null;
    }

    private static string GetSerializedFileDependencyKey(SerializedFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.originalPath))
        {
            return file.originalPath;
        }

        if (!string.IsNullOrWhiteSpace(file.fullName))
        {
            return file.fullName;
        }

        return file.fileName ?? string.Empty;
    }

    private static void PrepareLoadedSpriteAtlasReferencesForPreview(List<SerializedFile> filesSnapshot)
    {
        if (filesSnapshot.Count == 0)
        {
            return;
        }

        var sprites = new List<Sprite>();
        var atlases = new List<SpriteAtlas>();
        foreach (var file in filesSnapshot)
        {
            AssetStudio.Object[] objects;
            lock (file)
            {
                objects = file.Objects.ToArray();
            }

            foreach (var obj in objects)
            {
                if (obj is Sprite sprite)
                {
                    ResetSpriteReferenceCache(sprite);
                    sprites.Add(sprite);
                }
                else if (obj is SpriteAtlas atlas)
                {
                    ResetSpriteAtlasReferenceCache(atlas);
                    atlases.Add(atlas);
                }
            }
        }

        if (sprites.Count == 0 || atlases.Count == 0)
        {
            return;
        }

        var spriteAtlasCache = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();
        foreach (var atlas in atlases)
        {
            if (atlas.m_RenderDataMap != null)
            {
                foreach (var key in atlas.m_RenderDataMap.Keys)
                {
                    spriteAtlasCache[key] = atlas;
                }
            }

            foreach (var packedSprite in atlas.m_PackedSprites ?? Array.Empty<PPtr<Sprite>>())
            {
                if (packedSprite != null && packedSprite.TryGet(out var sprite) && sprite != null)
                {
                    if (sprite.m_SpriteAtlas == null || sprite.m_SpriteAtlas.IsNull)
                    {
                        sprite.m_SpriteAtlas?.Set(atlas);
                    }
                    else if (sprite.m_SpriteAtlas.TryGet(out var oldAtlas) && oldAtlas?.m_IsVariant == true)
                    {
                        sprite.m_SpriteAtlas.Set(atlas);
                    }
                }
            }
        }

        foreach (var sprite in sprites)
        {
            if (sprite.m_SpriteAtlas != null
                && !sprite.m_SpriteAtlas.IsNull
                && sprite.m_SpriteAtlas.TryGet(out _))
            {
                continue;
            }

            if (sprite.m_RenderDataKey.Key != Guid.Empty
                && spriteAtlasCache.TryGetValue(sprite.m_RenderDataKey, out var atlas)
                && sprite.m_SpriteAtlas != null)
            {
                sprite.m_SpriteAtlas.Set(atlas);
            }
        }
    }

    private static void ResetSpriteReferenceCache(Sprite sprite)
    {
        sprite.m_SpriteAtlas?.ResetCache();
        if (sprite.m_RD == null)
        {
            return;
        }

        sprite.m_RD.texture?.ResetCache();
        sprite.m_RD.alphaTexture?.ResetCache();
        foreach (var secondaryTexture in sprite.m_RD.secondaryTextures ?? Array.Empty<SecondarySpriteTexture>())
        {
            secondaryTexture?.texture?.ResetCache();
        }
    }

    private static void ResetSpriteAtlasReferenceCache(SpriteAtlas atlas)
    {
        foreach (var packedSprite in atlas.m_PackedSprites ?? Array.Empty<PPtr<Sprite>>())
        {
            packedSprite?.ResetCache();
        }

        if (atlas.m_RenderDataMap == null)
        {
            return;
        }

        foreach (var data in atlas.m_RenderDataMap.Values)
        {
            data.texture?.ResetCache();
            data.alphaTexture?.ResetCache();
            foreach (var secondaryTexture in data.secondaryTextures ?? Array.Empty<SecondarySpriteTexture>())
            {
                secondaryTexture?.texture?.ResetCache();
            }
        }
    }

    private void MaterializeReferenceObjects(
        SerializedFile sourceFile,
        HashSet<ClassIDType> referenceTypes,
        LazyConnectionBuildDiagnostics? diagnostics = null)
    {
        var handles = assetsManager.ProjectIndex
            .GetHandlesForFile(sourceFile.fileName)
            .Where(handle => referenceTypes.Contains(handle.Type)
                && (ReferenceEquals(handle.SourceFile, sourceFile)
                    || IsSameLazySource(handle.OriginalPath, sourceFile.originalPath)
                    || string.IsNullOrEmpty(handle.OriginalPath)))
            .ToList();

        diagnostics?.RecordMaterializationCandidateCount(handles.Count);
        foreach (var handle in handles)
        {
            if (handle.SourceFile?.reader == null)
            {
                handle.SourceFile = sourceFile;
            }

            var obj = assetsManager.ResolveHandle(handle);
            if (obj == null)
            {
                diagnostics?.RecordFailedObject();
                continue;
            }

            diagnostics?.RecordResolvedObject(handle.Type);
            if (handle.Tag is AssetItem item)
            {
                UpdateLazyAssetItemHandle(item, handle);
            }
        }
    }

    private void BuildLazyPreviewReferenceIndexes(List<SerializedFile> filesSnapshot)
    {
        BuildAssetReferenceIndexesBackground(
            filesSnapshot,
            new List<AssetItem>(),
            out _,
            out var localMeshToMaterialsCache,
            out var localMeshAssociatedRenderersCache,
            out var localMeshSourceTypesCache,
            out var localMaterialMainTextureCache,
            out var localMaterialPreviewMaterialCache,
            out var localMaterialTextureSlotsCache,
            out _);

        lock (previewCacheLock)
        {
            meshToMaterialsCache = localMeshToMaterialsCache;
            meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
            meshSourceTypesCache = localMeshSourceTypesCache;
            materialMainTextureCache = localMaterialMainTextureCache;
            materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
            materialTextureSlotsCache = localMaterialTextureSlotsCache;
        }
    }

    private static bool IsSameLazySource(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private Texture2D? ResolveTexturePPtr(Material material, PPtr<Texture> textureRef)
    {
        return ResolveTexturePPtrForPreview(material, textureRef);
    }

    private Texture2D? ResolveTexturePPtrForPreview(Material material, PPtr<Texture> textureRef)
    {
        if (textureRef == null || textureRef.IsNull)
        {
            return null;
        }

        var directTexture = ResolveTexturePPtrBackground(material, textureRef);
        if (directTexture != null)
        {
            return directTexture;
        }

        if (!assetsManager.LazyLoading || material.assetsFile == null)
        {
            return null;
        }

        var handle = FindTextureHandleForPPtr(material, textureRef);
        if (handle == null)
        {
            return null;
        }

        return ResolveSemanticRelationHandleForPreview(handle) as Texture2D;
    }

    private AssetHandle? FindTextureHandleForPPtr(Material material, PPtr<Texture> textureRef)
    {
        if (material.assetsFile == null || textureRef == null || textureRef.IsNull)
        {
            return null;
        }

        if (textureRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, textureRef.m_PathID));
            if (IsTexture2DHandleForPath(handle, textureRef.m_PathID))
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (textureRef.m_FileID == 0)
        {
            candidateFileNames.Add(material.assetsFile.fileName);
        }
        else if (textureRef.m_FileID > 0
            && material.assetsFile.m_Externals != null
            && textureRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[textureRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, textureRef.m_PathID));
                if (IsTexture2DHandleForPath(handle, textureRef.m_PathID))
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => IsTexture2DHandleForPath(candidate, textureRef.m_PathID));
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private AssetHandle? FindMaterialHandleForPPtr(Material material, PPtr<Material> materialRef)
    {
        if (material.assetsFile == null || materialRef == null || materialRef.IsNull)
        {
            return null;
        }

        if (materialRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, materialRef.m_PathID));
            if (IsMaterialHandleForPath(handle, materialRef.m_PathID))
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (materialRef.m_FileID == 0)
        {
            candidateFileNames.Add(material.assetsFile.fileName);
        }
        else if (materialRef.m_FileID > 0
            && material.assetsFile.m_Externals != null
            && materialRef.m_FileID - 1 < material.assetsFile.m_Externals.Count)
        {
            var external = material.assetsFile.m_Externals[materialRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, materialRef.m_PathID));
                if (IsMaterialHandleForPath(handle, materialRef.m_PathID))
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => IsMaterialHandleForPath(candidate, materialRef.m_PathID));
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private static void AddCandidateFileName(HashSet<string> candidates, string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return;
        }

        candidates.Add(fileNameOrPath);
        var safeName = Path.GetFileName(fileNameOrPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(safeName))
        {
            candidates.Add(safeName);
        }
    }

    private static bool IsTexture2DHandleForPath(AssetHandle? handle, long pathId)
    {
        return handle != null && handle.PathID == pathId && handle.Type == ClassIDType.Texture2D;
    }

    private static bool IsMaterialHandleForPath(AssetHandle? handle, long pathId)
    {
        return handle != null && handle.PathID == pathId && handle.Type == ClassIDType.Material;
    }

    private void BuildAssetReferenceIndexes()
    {
        BuildAssetReferenceIndexesBackground(
            assetsManager.assetsFileList,
            exportableAssets,
            out var localObjectToAssetItemCache,
            out var localMeshToMaterialsCache,
            out var localMeshAssociatedRenderersCache,
            out var localMeshSourceTypesCache,
            out var localMaterialMainTextureCache,
            out var localMaterialPreviewMaterialCache,
            out var localMaterialTextureSlotsCache,
            out var semanticRelations);

        objectToAssetItemCache = localObjectToAssetItemCache;
        meshToMaterialsCache = localMeshToMaterialsCache;
        meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
        meshSourceTypesCache = localMeshSourceTypesCache;
        materialMainTextureCache = localMaterialMainTextureCache;
        materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
        materialTextureSlotsCache = localMaterialTextureSlotsCache;
        if (!assetsManager.LazyLoading)
        {
            _ = Task.Run(() => TrySaveSemanticRelations(semanticRelations));
        }

        BuildAnimationPreviewIndexesBackground(
            assetsManager.assetsFileList,
            out var localAnimationClipAvatarCache,
            out var localAvatarMeshCache,
            out var localMeshAvatarCache,
            out var localAnimationClipTransformBindingsCache);

        animationClipAvatarCache = localAnimationClipAvatarCache;
        avatarMeshCache = localAvatarMeshCache;
        avatarMeshesCache = BuildAvatarMeshListCache(localAvatarMeshCache);
        meshAvatarCache = localMeshAvatarCache;
        animationClipTransformBindingsCache = localAnimationClipTransformBindingsCache;
    }

    private static int ScoreMaterials(List<Material?> mats)
    {
        return ScoreMaterialsStatic(mats);
    }



    private List<Material?> FindMaterialsForMesh(Mesh mesh)
    {
        if (assetsManager.LazyLoading)
        {
            return FindMaterialsForMeshPreview(mesh);
        }

        if (meshToMaterialsCache == null)
        {
            var semanticMaterials = TryLoadMaterialsForMeshFromSemanticCache(mesh);
            if (semanticMaterials.Count > 0)
            {
                return semanticMaterials;
            }

            BuildAssetReferenceIndexes();
        }

        if (meshToMaterialsCache!.TryGetValue(mesh, out var cachedList))
        {
            return new List<Material?>(cachedList);
        }

        return new List<Material?>();
    }

    private List<Material?> TryLoadMaterialsForMeshFromSemanticCache(Mesh mesh)
    {
        var materials = new List<Material?>();
        if (!assetsManager.LazyLoading || mesh.assetsFile == null || currentScanResult == null)
        {
            return materials;
        }

        var folderPath = GetCurrentCacheFolderPath();
        if (!CanUseLazySemanticRelationCache(folderPath))
        {
            return materials;
        }

        var meshAssetId = AssetHandle.BuildUniqueID(mesh.assetsFile, mesh.m_PathID);
        var signature = _sqliteCache.GetFolderSignature(currentScanResult);
        var materialIds = _sqliteCache.LoadMeshMaterialAssetIds(folderPath, signature, meshAssetId);
        if (materialIds.Count == 0)
        {
            return materials;
        }

        foreach (var materialId in materialIds)
        {
            if (string.IsNullOrWhiteSpace(materialId))
            {
                materials.Add(null);
                continue;
            }

            var handle = assetsManager.ProjectIndex.GetHandle(materialId);
            if (handle == null)
            {
                materials.Add(null);
                continue;
            }

            var asset = ResolveSemanticRelationHandleForPreview(handle);
            if (asset is Material material)
            {
                materials.Add(material);
            }
            else
            {
                materials.Add(null);
            }
        }

        if (materials.Any(material => material != null))
        {
            meshToMaterialsCache ??= new Dictionary<Mesh, List<Material?>>();
            meshToMaterialsCache[mesh] = materials;
            return materials;
        }

        return new List<Material?>();
    }

    private static HashSet<string> GetPathTokens(string path)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = path.Split(new[] { '/', '\\', '_', '.', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length > 2 && 
                !string.Equals(part, "fbx", StringComparison.OrdinalIgnoreCase) && 
                !string.Equals(part, "mat", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "assets", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "models", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "materials", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(part);
            }
        }
        return tokens;
    }

    private static global::OpenTK.Mathematics.Vector2[]? BuildMeshPreviewUvs(Mesh mesh)
    {
        var componentCount = GetMeshPreviewUvComponentCount(mesh.m_UV0, mesh.m_VertexCount);
        if (componentCount < 2)
        {
            return null;
        }

        var uvs = new global::OpenTK.Mathematics.Vector2[mesh.m_VertexCount];
        for (int i = 0; i < mesh.m_VertexCount; i++)
        {
            var offset = i * componentCount;
            uvs[i] = new global::OpenTK.Mathematics.Vector2(mesh.m_UV0[offset], mesh.m_UV0[offset + 1]);
        }

        return uvs;
    }

    private static int GetMeshPreviewUvComponentCount(float[]? uv, int vertexCount)
    {
        if (uv == null || vertexCount <= 0 || uv.Length < vertexCount * 2 || uv.Length % vertexCount != 0)
        {
            return 0;
        }

        var componentCount = uv.Length / vertexCount;
        return componentCount is >= 2 and <= 4 ? componentCount : 0;
    }

    private string FormatMeshPreviewSummary(Mesh mesh, AssetItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mesh Asset: {mesh.m_Name} (PathID: {mesh.m_PathID})");
        sb.AppendLine("==================================================");
        sb.AppendLine($"Vertex Count: {mesh.m_VertexCount}");
        sb.AppendLine($"Submesh Count: {mesh.m_SubMeshes?.Length ?? 0}");
        sb.AppendLine($"Index Count: {mesh.m_Indices?.Count ?? 0}");

        bool isFbx = item.Container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"From FBX Container: {(isFbx ? "Yes" : "No")}");
        if (isFbx)
        {
            sb.AppendLine($"FBX Path: {item.Container}");
        }

        return sb.ToString();
    }

    private string FormatMeshPreview(Mesh mesh, AssetItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mesh Asset: {mesh.m_Name} (PathID: {mesh.m_PathID})");
        sb.AppendLine("==================================================");
        sb.AppendLine($"Vertex Count: {mesh.m_VertexCount}");
        sb.AppendLine($"Submesh Count: {mesh.m_SubMeshes?.Length ?? 0}");
        sb.AppendLine($"Index Count: {mesh.m_Indices?.Count ?? 0}");

        bool isFbx = item.Container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"From FBX Container: {(isFbx ? "Yes" : "No")}");
        if (isFbx)
        {
            sb.AppendLine($"FBX Path: {item.Container}");
        }

        if (meshToMaterialsCache == null || meshAssociatedRenderersCache == null || meshSourceTypesCache == null)
        {
            BuildAssetReferenceIndexes();
        }

        meshSourceTypesCache!.TryGetValue(mesh, out var cachedSourceTypes);
        var sourceTypes = cachedSourceTypes?.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        sb.AppendLine($"Referenced By: {(sourceTypes.Count > 0 ? string.Join(", ", sourceTypes) : "None (Orphaned Mesh)")}");

        var materials = FindMaterialsForMesh(mesh);
        sb.AppendLine($"Associated Materials ({materials.Count}):");
        foreach (var mat in materials)
        {
            if (mat != null)
            {
                sb.AppendLine($"  - {mat.m_Name} (PathID: {mat.m_PathID})");
            }
        }

        meshAssociatedRenderersCache!.TryGetValue(mesh, out var associatedRenderers);
        associatedRenderers ??= new List<string>();
        if (associatedRenderers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Associated Renderers / Filters:");
            foreach (var ar in associatedRenderers.Distinct())
            {
                sb.AppendLine($"  - {ar}");
            }
        }

        sb.AppendLine();
        var dump = mesh.Dump();
        if (dump != null)
        {
            sb.AppendLine("Mesh Serialization Structure:");
            if (dump.Length > 2000)
            {
                sb.AppendLine(dump.Substring(0, 2000));
                sb.AppendLine("...");
                sb.AppendLine("[Dump truncated: too large for side overlay. View full dump in the 'Dump' tab.]");
            }
            else
            {
                sb.AppendLine(dump);
            }
        }

        return sb.ToString();
    }

    private string FormatLazyMeshPreview(Mesh mesh, AssetItem item, List<Material?> materials)
    {
        var sb = new StringBuilder();
        sb.Append(FormatMeshPreviewSummary(mesh, item));

        var folderPath = GetCurrentCacheFolderPath();
        var connectionsReady = CanUseLazySemanticRelationCache(folderPath);
        sb.AppendLine($"Connections: {(connectionsReady ? "Complete" : "Not built yet")}");

        if (connectionsReady)
        {
            sb.AppendLine($"Associated Materials ({materials.Count}):");
            foreach (var mat in materials)
            {
                if (mat != null)
                {
                    sb.AppendLine($"  - {mat.m_Name} (PathID: {mat.m_PathID})");
                }
            }
        }
        else
        {
            sb.AppendLine("Associated Materials: waiting for final connections build.");
        }

        return sb.ToString();
    }

}
