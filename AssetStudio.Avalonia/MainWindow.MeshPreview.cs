using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal MeshPreviewViewModel MeshPreview => ViewModel.MeshPreview;
    private long _activeMeshBadgePreviewId;
    private string? _activeMeshBadgeSourceKind;
    private bool _activeMeshGeometryFromCache;
    private bool _activeMeshMaterialsLoading;
    private readonly HashSet<string> _activePreviewDependencyFileNames = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _activePreviewNeedsConnectionRefresh;

    private void SetActivePreviewDependencyFiles(params AssetStudio.Object?[] assets)
    {
        lock (preloaderLock)
        {
            _activePreviewDependencyFileNames.Clear();
            foreach (var asset in assets)
            {
                var fileName = asset?.assetsFile?.fileName;
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    _activePreviewDependencyFileNames.Add(fileName);
                }
            }
        }
    }

    private void AddActivePreviewDependencyFiles(IEnumerable<string?> fileNames)
    {
        lock (preloaderLock)
        {
            foreach (var fileName in fileNames)
            {
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    _activePreviewDependencyFileNames.Add(fileName);
                }
            }
        }
    }

    private void TriggerAutoRefreshActivePreviewIfNeeded()
    {
        if (!_activePreviewNeedsConnectionRefresh)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_activePreviewNeedsConnectionRefresh)
            {
                return;
            }

            if (AssetListDataGrid?.SelectedItem is AssetItem selectedAsset
                && selectedAsset.Type is ClassIDType.AnimationClip or ClassIDType.Avatar or ClassIDType.Mesh)
            {
                _activePreviewNeedsConnectionRefresh = false;
                QueuePreviewAsset(selectedAsset, immediate: true);
            }
        });
    }

    private void OnMeshPreviewGeometryLoaded(bool fromCache)
    {
        if (texturePreviewIdCounter != _activeMeshBadgePreviewId)
        {
            return;
        }

        _activeMeshGeometryFromCache = fromCache;
        PreviewSession.UpdateCacheBadge(
            fromCache: fromCache,
            sourceKind: _activeMeshBadgeSourceKind ?? "mesh-geometry",
            materialsLoading: _activeMeshMaterialsLoading);
        UpdatePreviewCacheBadge(
            fromCache: fromCache,
            sourceKind: _activeMeshBadgeSourceKind ?? "mesh-geometry",
            materialsLoading: _activeMeshMaterialsLoading);
    }

    internal sealed class CompositePreviewMaterialPart
    {
        public required Mesh Mesh { get; init; }
        public string? RendererAssetId { get; init; }
        public int ExpectedSubMeshCount { get; init; }
    }

    private void PreviewMesh(
        AssetItem assetItem,
        Mesh m_Mesh,
        bool rebuildCandidateControls = true,
        PreviewCandidateItem? selectedCandidate = null,
        bool? preferModelGroup = null)
    {
        var effectivePreferModelGroup = preferModelGroup ?? MeshPreview.ShowFullModelGroup;
        if (rebuildCandidateControls)
        {
            QueueMeshPreviewCandidateBuild(assetItem, m_Mesh, selectedCandidate, effectivePreferModelGroup);
            return;
        }

        var meshPreviewId = texturePreviewIdCounter;
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Mesh3D,
            assetItem,
            isLoading: true,
            loadingStatusText: "Preparing mesh preview...");
        MeshPreview.ActiveMeshName = m_Mesh.m_Name ?? assetItem.Name;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate("Preparing mesh preview...");
        if (ViewModel.Options.DisplayInfo)
        {
            SetPreviewInfoOverlay("Loading details...");
        }

        var localAssetItem = assetItem;
        var includeMeshInfo = ViewModel.Options.DisplayInfo;
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
                var meshSummary = FormatMeshPreviewSummary(m_Mesh, localAssetItem);
                var quickInfoText = includeMeshInfo
                    ? meshSummary + Environment.NewLine + "Loading material details..."
                    : string.Empty;

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        return;
                    }

                    MeshPreview.SetMeshAndRigSummary(
                        meshSummary: meshSummary,
                        associatedMaterialsSummary: "Loading material details...",
                        meshName: m_Mesh.m_Name);
                    PreviewSession.PreviewMetadataSummary = quickInfoText;

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = m_Mesh;
                        _activeMeshBadgePreviewId = meshPreviewId;
                        _activeMeshBadgeSourceKind = "mesh-geometry";
                        _activeMeshGeometryFromCache = false;
                        _activeMeshMaterialsLoading = true;
                        PreviewSession.UpdateCacheBadge(fromCache: false, sourceKind: "mesh-geometry", materialsLoading: true);
                        UpdatePreviewCacheBadge(fromCache: false, sourceKind: "mesh-geometry", materialsLoading: true);
                        GLPreviewControl.SetMesh(m_Mesh, uvs);
                        GLPreviewControl.IsVisible = true;
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    StatusStripUpdate("OpenGL Preview | Mesh loaded | Loading materials...");
                });

                if (meshPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                SetActivePreviewDependencyFiles(m_Mesh);

                // Cache-first O(1) lookup via v15 MeshMaterialResolvedCache (WAL read works even during Saving connections).
                var resolvedSlots = TryLoadResolvedMeshMaterialsForPreview(m_Mesh);
                if (meshPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                List<byte[]?> uniqueTextures;
                List<int> uniqueWidths;
                List<int> uniqueHeights;
                int[]? subMeshToTextureIndex;
                List<string> loadedTextureFiles;
                List<Material?>? fallbackMaterials = null;
                string infoText;
                string materialsSummary;
                bool hasAnyMaterial;

                if (resolvedSlots.Count > 0)
                {
                    var slotCount = Math.Max(m_Mesh.m_SubMeshes?.Length ?? 0, resolvedSlots.Max(s => s.SubMeshIndex) + 1);
                    if (slotCount <= 0 && m_Mesh.m_Indices?.Count > 0)
                    {
                        slotCount = 1;
                    }

                    BuildDeduplicatedPreviewTexturesFromResolved(
                        slotCount,
                        resolvedSlots,
                        () => meshPreviewId != texturePreviewIdCounter,
                        out uniqueTextures,
                        out uniqueWidths,
                        out uniqueHeights,
                        out subMeshToTextureIndex,
                        out loadedTextureFiles);

                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (loadedTextureFiles.Count > 0)
                    {
                        AddActivePreviewDependencyFiles(loadedTextureFiles);
                    }

                    var distinctNames = resolvedSlots
                        .Where(s => !string.IsNullOrWhiteSpace(s.MaterialAssetId))
                        .Select(s => !string.IsNullOrWhiteSpace(s.MaterialName)
                            ? s.MaterialName
                            : GetPreviewHandleName(s.MaterialAssetId, "Material"))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    hasAnyMaterial = distinctNames.Count > 0;
                    materialsSummary = $"Associated Materials ({distinctNames.Count}): {string.Join(", ", distinctNames.Take(20))}"
                        + (distinctNames.Count > 20 ? $" (+{distinctNames.Count - 20} more)" : string.Empty);
                    infoText = includeMeshInfo
                        ? FormatLazyMeshPreviewFromResolved(m_Mesh, localAssetItem, resolvedSlots)
                        : string.Empty;
                    _activePreviewNeedsConnectionRefresh = false;
                }
                else
                {
                    var allMaterials = FindMaterialsForMeshPreview(m_Mesh);
                    if (allMaterials.Count == 0 && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                    {
                        EnsureMeshPreviewDependenciesLoaded(m_Mesh);
                        allMaterials = FindMaterialsForMeshPreview(m_Mesh);
                    }

                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    var slotCount = Math.Max(m_Mesh.m_SubMeshes?.Length ?? 0, allMaterials.Count);
                    if (slotCount <= 0 && m_Mesh.m_Indices?.Count > 0)
                    {
                        slotCount = 1;
                    }

                    BuildDeduplicatedPreviewTexturesForMaterials(
                        slotCount,
                        allMaterials,
                        () => meshPreviewId != texturePreviewIdCounter,
                        out uniqueTextures,
                        out uniqueWidths,
                        out uniqueHeights,
                        out subMeshToTextureIndex,
                        out loadedTextureFiles);

                    if (meshPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    if (loadedTextureFiles.Count > 0)
                    {
                        AddActivePreviewDependencyFiles(loadedTextureFiles);
                    }

                    fallbackMaterials = allMaterials;
                    hasAnyMaterial = allMaterials.Any(m => m != null);
                    infoText = includeMeshInfo
                        ? assetsManager.LazyLoading
                            ? FormatLazyMeshPreview(m_Mesh, localAssetItem, allMaterials)
                            : FormatMeshPreview(m_Mesh, localAssetItem)
                        : string.Empty;
                    var distinctMatNames = allMaterials
                        .Where(m => m != null)
                        .Select(m => m!.m_Name)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    materialsSummary = $"Associated Materials ({distinctMatNames.Count}): {string.Join(", ", distinctMatNames.Take(20))}"
                        + (distinctMatNames.Count > 20 ? $" (+{distinctMatNames.Count - 20} more)" : string.Empty);
                    if (!hasAnyMaterial && assetsManager.LazyLoading && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
                    {
                        _activePreviewNeedsConnectionRefresh = true;
                    }
                    else
                    {
                        _activePreviewNeedsConnectionRefresh = false;
                    }
                }

                var hasTextures = uniqueTextures.Any(t => t != null);

                Dispatcher.UIThread.Post(() =>
                {
                    if (meshPreviewId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, localAssetItem))
                    {
                        return;
                    }

                    MeshPreview.SetMeshAndRigSummary(
                        meshSummary: meshSummary,
                        associatedMaterialsSummary: materialsSummary,
                        meshName: m_Mesh.m_Name);
                    PreviewSession.PreviewMetadataSummary = infoText;
                    PreviewSession.IsLoading = false;

                    if (GLPreviewControl != null)
                    {
                        currentPreviewMesh = m_Mesh;
                        GLPreviewControl.ApplyMeshTextures(m_Mesh, uniqueTextures, uniqueWidths, uniqueHeights, subMeshToTextureIndex);
                        GLPreviewControl.IsVisible = true;
                        if (resolvedSlots.Count > 0)
                        {
                            BuildMeshMaterialControlsFromResolved(m_Mesh, resolvedSlots);
                        }
                        else if (fallbackMaterials != null)
                        {
                            BuildMeshMaterialControls(m_Mesh, fallbackMaterials);
                        }
                        ShowPreviewGeometryControls(showBoneControls: false);
                        GLPreviewControl.Focus();
                    }

                    _activeMeshMaterialsLoading = false;
                    UpdatePreviewCacheBadge(
                        fromCache: _activeMeshGeometryFromCache,
                        sourceKind: _activeMeshBadgeSourceKind ?? "mesh-geometry",
                        materialsLoading: false);

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
                        _activeMeshMaterialsLoading = false;
                        PreviewSession.IsLoading = false;
                        ClearPreviewCacheBadge();
                        StatusStripUpdate($"Mesh preview error: {ex.Message}");
                        SetPreviewInfoOverlay($"Failed to load mesh: {ex.Message}");
                    }
                });
            }
        });
    }

    private void QueueLoadMaterialsForUnifiedMeshPreviewAsync(
        Mesh m_Mesh,
        long expectedPreviewId,
        AssetItem? expectedSelectedAsset)
    {
        QueueLoadMaterialsForUnifiedMeshPreviewAsync(
            new[]
            {
                new CompositePreviewMaterialPart
                {
                    Mesh = m_Mesh,
                    RendererAssetId = null,
                    ExpectedSubMeshCount = Math.Max(1, m_Mesh.m_SubMeshes?.Length ?? 0)
                }
            },
            m_Mesh,
            expectedPreviewId,
            expectedSelectedAsset);
    }

    private void QueueLoadMaterialsForUnifiedMeshPreviewAsync(
        IReadOnlyList<CompositePreviewMaterialPart> parts,
        Mesh? expectedPrimaryMesh,
        long expectedPreviewId,
        AssetItem? expectedSelectedAsset)
    {
        if (parts == null || parts.Count == 0)
        {
            return;
        }

        var partsSnapshot = parts.ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                if (expectedPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                var allSlotSpecs = new List<(string MaterialName, global::OpenTK.Mathematics.Vector4 BaseColor)>();
                var allSubMeshTextureIds = new List<string?>();
                var fallbackTexturesBySlot = new Dictionary<int, Texture2D>();

                for (var partIdx = 0; partIdx < partsSnapshot.Length; partIdx++)
                {
                    if (expectedPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

                    var part = partsSnapshot[partIdx];
                    var mesh = part.Mesh;
                    var resolvedPartSlots = TryLoadResolvedMeshMaterialsForPreview(mesh, part.RendererAssetId);
                    if (expectedPreviewId != texturePreviewIdCounter)
                    {
                        return;
                    }

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

                        var slotCount = part.ExpectedSubMeshCount > 0
                            ? part.ExpectedSubMeshCount
                            : Math.Max(mesh.m_SubMeshes?.Length ?? 0, resolvedPartSlots.Max(s => s.SubMeshIndex) + 1);
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
                                allSubMeshTextureIds.Add(string.IsNullOrWhiteSpace(resolved.MainTextureAssetId)
                                    ? null
                                    : resolved.MainTextureAssetId);
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

                        if (expectedPreviewId != texturePreviewIdCounter)
                        {
                            return;
                        }

                        var slotCount = part.ExpectedSubMeshCount > 0
                            ? part.ExpectedSubMeshCount
                            : Math.Max(mesh.m_SubMeshes?.Length ?? 0, materials.Count);
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
                        }
                    }
                }

                if (expectedPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                BuildDeduplicatedPreviewTexturesByAssetIds(
                    allSubMeshTextureIds,
                    () => expectedPreviewId != texturePreviewIdCounter,
                    out var uniqueTextures,
                    out var uniqueWidths,
                    out var uniqueHeights,
                    out var subMeshToTextureIndex,
                    out var loadedTextureFiles);

                if (fallbackTexturesBySlot.Count > 0)
                {
                    foreach (var kvp in fallbackTexturesBySlot)
                    {
                        if (expectedPreviewId != texturePreviewIdCounter)
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

                if (expectedPreviewId != texturePreviewIdCounter)
                {
                    return;
                }

                if (loadedTextureFiles.Count > 0)
                {
                    AddActivePreviewDependencyFiles(loadedTextureFiles);
                }

                var hasAnyTexture = uniqueTextures.Any(t => t != null);
                var hasAnyNamedMaterial = allSlotSpecs.Any(s => !string.Equals(s.MaterialName, "No Material", StringComparison.Ordinal));

                Dispatcher.UIThread.Post(() =>
                {
                    if (IsWindowClosingOrClosed
                        || expectedPreviewId != texturePreviewIdCounter
                        || (expectedPrimaryMesh != null && !ReferenceEquals(currentPreviewMesh, expectedPrimaryMesh))
                        || (expectedSelectedAsset != null && !ReferenceEquals(AssetListDataGrid?.SelectedItem, expectedSelectedAsset)))
                    {
                        return;
                    }

                    if (GLPreviewControl == null)
                    {
                        return;
                    }

                    // Apply textures asynchronously without resetting animation playback frame/timer.
                    if (hasAnyTexture)
                    {
                        GLPreviewControl.ApplyMeshTextures(expectedPrimaryMesh, uniqueTextures, uniqueWidths, uniqueHeights, subMeshToTextureIndex);
                    }

                    if (allSlotSpecs.Count > 0 && (hasAnyTexture || hasAnyNamedMaterial))
                    {
                        BuildMeshMaterialControlsFromSlotSpecs(allSlotSpecs);
                    }
                });
            }
            catch
            {
                // Best-effort material/texture enrichment for Avatar and AnimationClip previews.
            }
        });
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

    private string FormatLazyMeshPreviewFromResolved(
        Mesh mesh,
        AssetItem item,
        IReadOnlyList<ResolvedMeshSubMeshMaterial> resolvedSlots)
    {
        var sb = new StringBuilder();
        sb.Append(FormatMeshPreviewSummary(mesh, item));

        var folderPath = GetCurrentCacheFolderPath();
        var connectionsReady = CanUseLazySemanticRelationCache(folderPath);
        sb.AppendLine($"Connections: {(connectionsReady ? "Complete" : "Cached (lazy backfill)")}");

        var distinctMaterials = resolvedSlots
            .Where(s => !string.IsNullOrWhiteSpace(s.MaterialAssetId))
            .GroupBy(s => s.MaterialAssetId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        sb.AppendLine($"Associated Materials ({distinctMaterials.Count} unique across {resolvedSlots.Count} submesh slots):");
        foreach (var mat in distinctMaterials.Take(32))
        {
            var matName = !string.IsNullOrWhiteSpace(mat.MaterialName)
                ? mat.MaterialName
                : GetPreviewHandleName(mat.MaterialAssetId, "Material");
            sb.AppendLine($"  - {matName}");
        }
        if (distinctMaterials.Count > 32)
        {
            sb.AppendLine($"  ... {distinctMaterials.Count - 32:N0} more");
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
