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
    private MeshPreviewViewModel? _meshPreview;
    internal MeshPreviewViewModel MeshPreview => _meshPreview ??= new MeshPreviewViewModel(DispatchToPreviewUi);
    private long _activeMeshBadgePreviewId;
    private string? _activeMeshBadgeSourceKind;
    private bool _activeMeshGeometryFromCache;
    private bool _activeMeshMaterialsLoading;

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
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Mesh3D,
            assetItem,
            isLoading: true,
            loadingStatusText: "Preparing mesh preview...");
        MeshPreview.ActiveMeshName = m_Mesh.m_Name ?? assetItem.Name;
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
                var materialsSummary = $"Associated Materials ({allMaterials.Count(m => m != null)}): " +
                    string.Join(", ", allMaterials.Where(m => m != null).Select(m => m!.m_Name));
                var hasTextures = subMeshTextures.Any(t => t != null);

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

                    _activeMeshMaterialsLoading = false;
                    PreviewSession.UpdateCacheBadge(
                        fromCache: _activeMeshGeometryFromCache,
                        sourceKind: _activeMeshBadgeSourceKind ?? "mesh-geometry",
                        materialsLoading: false);
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
                        PreviewSession.ClearCacheBadge();
                        ClearPreviewCacheBadge();
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
