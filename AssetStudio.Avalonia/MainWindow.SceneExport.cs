using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private readonly SceneGlbExportService _sceneGlbExportService = new();

    private static readonly HashSet<ClassIDType> SceneExportBaseObjectTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Mesh,
        ClassIDType.Material,
        ClassIDType.Animator,
        ClassIDType.Avatar,
        ClassIDType.Animation
    };

    private static readonly HashSet<ClassIDType> SceneExportAnimatedObjectTypes = new(SceneExportBaseObjectTypes)
    {
        ClassIDType.AnimationClip,
        ClassIDType.AnimatorController,
        ClassIDType.AnimatorOverrideController
    };

    private sealed record LazySceneExportTarget(
        string? SourcePath,
        string? SerializedFileName,
        string SceneName,
        SerializedFile? LoadedSerializedFile,
        AssetItem? SelectedItem);

    private sealed class SceneExportResolverContext
    {
        public HashSet<Material> VisitedMaterials { get; } = new();
        public HashSet<string> VisitedTextures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Mesh?> MeshCache { get; } = new(StringComparer.Ordinal);
        public Dictionary<Renderer, List<Material?>> RendererMaterialsCache { get; } = new();
    }

    private async Task ExportSceneGlbCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        // Path 1: Scene Hierarchy already loaded (Eager mode or loaded scene files)
        if (sceneTreeNodes.Count > 0)
        {
            GameObjectNode? targetSceneRoot = null;
            if (SceneTreeView?.SelectedItem is GameObjectNode selectedNode)
            {
                targetSceneRoot = SceneGlbExportService.FindSceneRootNode(selectedNode);
            }
            else
            {
                var checkedNodes = GetSelectedParentNodes();
                if (checkedNodes.Count > 0)
                {
                    targetSceneRoot = SceneGlbExportService.FindSceneRootNode(checkedNodes[0]);
                }
            }

            List<GameObject> rootGameObjects;
            string sceneName;

            if (targetSceneRoot != null)
            {
                sceneName = string.IsNullOrWhiteSpace(targetSceneRoot.Name) ? "Scene" : targetSceneRoot.Name;
                rootGameObjects = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(targetSceneRoot);
            }
            else if (sceneTreeNodes.Count == 1)
            {
                sceneName = string.IsNullOrWhiteSpace(sceneTreeNodes[0].Name) ? "Scene" : sceneTreeNodes[0].Name;
                rootGameObjects = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(sceneTreeNodes[0]);
            }
            else
            {
                var exportAllScenes = await ViewModel.DialogService.ConfirmAsync(
                    "Export Scene to .glb",
                    $"Multiple scenes are loaded ({sceneTreeNodes.Count} scene roots).\n\n" +
                    $"Click Yes to export ALL loaded scenes into one instanced .glb.\n" +
                    $"Click No to export only the first scene ({sceneTreeNodes[0].Name}).\n\n" +
                    $"Tip: Select or right-click a specific scene in Scene Hierarchy to export just that scene.");

                if (exportAllScenes)
                {
                    sceneName = "AllScenes";
                    rootGameObjects = new List<GameObject>();
                    var seen = new HashSet<GameObject>();
                    foreach (var sceneRoot in sceneTreeNodes)
                    {
                        foreach (var go in SceneGlbExportService.CollectRootGameObjectsFromSceneNode(sceneRoot))
                        {
                            if (seen.Add(go))
                            {
                                rootGameObjects.Add(go);
                            }
                        }
                    }
                }
                else
                {
                    sceneName = string.IsNullOrWhiteSpace(sceneTreeNodes[0].Name) ? "Scene" : sceneTreeNodes[0].Name;
                    rootGameObjects = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(sceneTreeNodes[0]);
                }
            }

            if (rootGameObjects.Count == 0)
            {
                StatusStripUpdate("No GameObjects found in the selected scene.");
                return;
            }

            await RunSceneGlbExportAsync(sceneName, rootGameObjects);
            return;
        }

        // Path 2: Lazy Mode (no Scene Hierarchy built yet) -> resolve and materialize directly inside the main assetsManager
        // so cross-bundle Addressables, ProjectIndex, SQLite v15 material/texture caches, and Material Variants all work identically to Export Model.
        var target = ResolveLazySceneExportTarget();
        if (target == null)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Export Scene to .glb",
                "No loaded Scene Hierarchy or scene file (levelN / scene bundle) was detected.\n\n" +
                "Load a scene file (such as level0 or a scene bundle) with 'Load file' or 'Load folder (Full / Scene Hierarchy)', or select an asset from a scene bundle in the Asset List first.");
            return;
        }

        var suggestedName = FixFileName(target.SceneName) + ".glb";
        var saveFile = await PickSaveSceneGlbDialogAsync("Export Scene to .glb (Instanced)", suggestedName);
        if (string.IsNullOrWhiteSpace(saveFile))
        {
            return;
        }

        var exportFolder = Path.GetDirectoryName(saveFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var displaySourceName = !string.IsNullOrWhiteSpace(target.SerializedFileName)
            ? target.SerializedFileName
            : (!string.IsNullOrWhiteSpace(target.SourcePath) ? Path.GetFileName(target.SourcePath) : target.SceneName);

        var ctsToken = ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            100,
            exportFolder,
            $"Loading scene '{displaySourceName}'...");
        StatusStripUpdate($"Loading scene '{displaySourceName}' for .glb export...");

        SceneGlbExportResult? result = null;
        try
        {
            var includeAnimations = exportOptions.ExportAnimations;
            await Task.Run(() =>
            {
                using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;
                using var textureResolverScope = ModelConverter.PushTextureResolver(ResolveTextureForModelConverter);

                ctsToken.ThrowIfCancellationRequested();
                ReportSceneExportProgress(5, $"Materializing hierarchy for '{displaySourceName}'...");

                var targetFiles = EnsureLazySceneSerializedFilesLoaded(target, ctsToken);
                foreach (var sf in targetFiles)
                {
                    MaterializeSceneObjectsForSerializedFile(sf, includeAnimations, ctsToken);
                }

                ctsToken.ThrowIfCancellationRequested();
                var roots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(targetFiles, out var usedInactiveFallback);

                // If the targeted SerializedFile had 0 GameObjects (for example a mesh-only CAB), check sibling CABs in the same bundle
                // and then follow the semantic cache from the selected Mesh/Avatar to its owning prefab/scene SerializedFile.
                if (roots.Count == 0 && !string.IsNullOrWhiteSpace(target.SourcePath))
                {
                    var siblingFiles = GetLoadedFilesSnapshot()
                        .Where(sf => sf?.reader != null
                            && !targetFiles.Contains(sf)
                            && (IsSameLazySource(sf.originalPath, target.SourcePath)
                                || IsSameLazySource(sf.fullName, target.SourcePath)))
                        .ToList();
                    if (siblingFiles.Count > 0)
                    {
                        foreach (var sf in siblingFiles)
                        {
                            MaterializeSceneObjectsForSerializedFile(sf, includeAnimations, ctsToken);
                        }
                        roots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(siblingFiles, out usedInactiveFallback);
                    }
                }

                if (roots.Count == 0 && target.SelectedItem != null)
                {
                    var semanticOwnerFile = TryResolveSemanticSceneFileForSelectedAsset(target.SelectedItem, ctsToken);
                    if (semanticOwnerFile != null)
                    {
                        MaterializeSceneObjectsForSerializedFile(semanticOwnerFile, includeAnimations, ctsToken);
                        roots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(
                            new[] { semanticOwnerFile },
                            out usedInactiveFallback);
                    }
                }

                if (roots.Count == 0)
                {
                    result = new SceneGlbExportResult
                    {
                        Success = false,
                        OutputPath = saveFile,
                        SceneName = Path.GetFileNameWithoutExtension(saveFile),
                        ErrorMessage = $"No GameObject hierarchy was found inside '{displaySourceName}'."
                    };
                    return;
                }

                var options = BuildSceneGlbExportOptions(
                    saveFile,
                    Path.GetFileNameWithoutExtension(saveFile),
                    includeInactive: usedInactiveFallback);
                var resolverContext = new SceneExportResolverContext();
                result = _sceneGlbExportService.ExportScene(
                    roots,
                    options,
                    textureResolver: ResolveTextureForModelConverter,
                    progressCallback: (pct, _, status) => ReportSceneExportProgress(pct, status),
                    cancellationToken: ctsToken,
                    meshResolver: renderer => ResolveSceneMeshForRenderer(renderer, resolverContext),
                    materialResolver: (renderer, mesh, slotIndex) => ResolveSceneMaterialForSubmesh(renderer, mesh, slotIndex, resolverContext));
            }, ctsToken);
        }
        catch (OperationCanceledException)
        {
            result = new SceneGlbExportResult
            {
                Success = false,
                OutputPath = saveFile,
                SceneName = Path.GetFileNameWithoutExtension(saveFile),
                ErrorMessage = "Scene export cancelled."
            };
        }

        await CompleteSceneGlbExportUiAsync(result, exportFolder);
    }

    private async Task ExportSelectedSubtreeGlbCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        GameObjectNode? targetNode = SceneTreeView?.SelectedItem as GameObjectNode;
        if (targetNode == null)
        {
            var checkedNodes = GetSelectedParentNodes();
            if (checkedNodes.Count > 0)
            {
                targetNode = checkedNodes[0];
            }
        }

        if (targetNode == null)
        {
            StatusStripUpdate("Select a GameObject or Scene node in Scene Hierarchy first.");
            return;
        }

        var roots = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(targetNode);
        if (roots.Count == 0)
        {
            StatusStripUpdate("Selected node contains no GameObjects to export.");
            return;
        }

        var sceneName = string.IsNullOrWhiteSpace(targetNode.Name) ? "Subtree" : targetNode.Name;
        await RunSceneGlbExportAsync(sceneName, roots);
    }

    private async Task RunSceneGlbExportAsync(
        string sceneName,
        List<GameObject> rootGameObjects)
    {
        var suggestedFileName = FixFileName(sceneName) + ".glb";
        var exportFile = await PickSaveSceneGlbDialogAsync($"Export Scene '{sceneName}' to .glb (Instanced)", suggestedFileName);
        if (string.IsNullOrWhiteSpace(exportFile))
        {
            return;
        }

        var exportFolder = Path.GetDirectoryName(exportFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var ctsToken = ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            100,
            exportFolder,
            $"Exporting scene '{sceneName}'...");
        StatusStripUpdate($"Exporting scene '{sceneName}' to {Path.GetFileName(exportFile)}...");

        SceneGlbExportResult? result = null;
        try
        {
            // Keep inactive proxies/LODs excluded by default, but fall back cleanly if every selected root is inactive.
            var hasActiveRoot = rootGameObjects.Any(static go => go != null && go.m_IsActive);
            var options = BuildSceneGlbExportOptions(exportFile, sceneName, includeInactive: !hasActiveRoot);
            await Task.Run(() =>
            {
                using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;
                using var textureResolverScope = ModelConverter.PushTextureResolver(ResolveTextureForModelConverter);
                var resolverContext = new SceneExportResolverContext();
                result = _sceneGlbExportService.ExportScene(
                    rootGameObjects,
                    options,
                    textureResolver: ResolveTextureForModelConverter,
                    progressCallback: (pct, _, status) => ReportSceneExportProgress(pct, status),
                    cancellationToken: ctsToken,
                    meshResolver: renderer => ResolveSceneMeshForRenderer(renderer, resolverContext),
                    materialResolver: (renderer, mesh, slotIndex) => ResolveSceneMaterialForSubmesh(renderer, mesh, slotIndex, resolverContext));
            }, ctsToken);
        }
        catch (OperationCanceledException)
        {
            result = new SceneGlbExportResult
            {
                Success = false,
                OutputPath = exportFile,
                SceneName = sceneName,
                ErrorMessage = "Scene export cancelled."
            };
        }

        await CompleteSceneGlbExportUiAsync(result, exportFolder);
    }

    private SceneGlbExportOptions BuildSceneGlbExportOptions(string outputPath, string sceneName, bool includeInactive = false)
    {
        var binary = !outputPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
        return new SceneGlbExportOptions
        {
            OutputPath = outputPath,
            SceneName = sceneName,
            IncludeInactive = includeInactive,
            SkipDisabledRenderers = true,
            IncludeAnimations = exportOptions.ExportAnimations,
            ExportSkins = exportOptions.ExportSkins,
            ExportBlendShape = exportOptions.ExportBlendShape,
            EnableInstancing = true,
            ScaleFactor = (float)exportOptions.ScaleFactor,
            ImageFormat = exportOptions.ConvertTextureFormat,
            Binary = binary
        };
    }

    private List<SerializedFile> EnsureLazySceneSerializedFilesLoaded(LazySceneExportTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (target.SelectedItem?.Handle != null)
        {
            TryAttachLoadedSourceFile(target.SelectedItem.Handle);
        }

        var loaded = FindMatchingLoadedSerializedFiles(target);
        if (loaded.Count > 0)
        {
            return loaded;
        }

        if (!string.IsNullOrWhiteSpace(target.SourcePath) && File.Exists(target.SourcePath))
        {
            RemovePendingFileFromProgressiveQueue(target.SourcePath);
            assetsManager.LoadFilesForPreview(target.SourcePath);
            if (!string.IsNullOrWhiteSpace(target.SerializedFileName))
            {
                assetsManager.WaitForAssetsFileLoaded(target.SerializedFileName, 5000);
            }

            if (target.SelectedItem?.Handle != null)
            {
                TryAttachLoadedSourceFile(target.SelectedItem.Handle);
            }

            loaded = FindMatchingLoadedSerializedFiles(target);
        }

        return loaded;
    }

    private List<SerializedFile> FindMatchingLoadedSerializedFiles(LazySceneExportTarget target)
    {
        if (target.LoadedSerializedFile?.reader != null)
        {
            return new List<SerializedFile> { target.LoadedSerializedFile };
        }

        if (target.SelectedItem?.Handle?.SourceFile?.reader != null)
        {
            return new List<SerializedFile> { target.SelectedItem.Handle.SourceFile };
        }

        if (target.SelectedItem?.SourceFile?.reader != null)
        {
            return new List<SerializedFile> { target.SelectedItem.SourceFile };
        }

        var snapshot = GetLoadedFilesSnapshot();
        if (!string.IsNullOrWhiteSpace(target.SerializedFileName))
        {
            var exactMatch = snapshot.FirstOrDefault(sf =>
                sf?.reader != null
                && string.Equals(sf.fileName, target.SerializedFileName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(target.SourcePath)
                    || IsSameLazySource(sf.originalPath, target.SourcePath)
                    || IsSameLazySource(sf.fullName, target.SourcePath)));
            if (exactMatch != null)
            {
                return new List<SerializedFile> { exactMatch };
            }

            var nameMatch = snapshot.FirstOrDefault(sf =>
                sf?.reader != null
                && string.Equals(sf.fileName, target.SerializedFileName, StringComparison.OrdinalIgnoreCase));
            if (nameMatch != null)
            {
                return new List<SerializedFile> { nameMatch };
            }
        }

        if (!string.IsNullOrWhiteSpace(target.SourcePath))
        {
            return snapshot
                .Where(sf => sf?.reader != null
                    && (IsSameLazySource(sf.originalPath, target.SourcePath)
                        || IsSameLazySource(sf.fullName, target.SourcePath)))
                .ToList();
        }

        return new List<SerializedFile>();
    }

    private void MaterializeSceneObjectsForSerializedFile(
        SerializedFile sourceFile,
        bool includeAnimations,
        CancellationToken cancellationToken)
    {
        if (sourceFile == null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var objectTypes = includeAnimations ? SceneExportAnimatedObjectTypes : SceneExportBaseObjectTypes;

        if (assetsManager.LazyLoading)
        {
            MaterializeReferenceObjects(sourceFile, objectTypes, diagnostics: null, cancellationToken);
        }

        // Sweep m_Objects directly for any hierarchy/renderer/mesh/material object not covered by ProjectIndex handles.
        if (sourceFile.reader == null || sourceFile.m_Objects == null || sourceFile.m_Objects.Count == 0)
        {
            return;
        }

        FileReader? localReader = null;
        try
        {
            var infos = sourceFile.m_Objects;
            for (var i = 0; i < infos.Count; i++)
            {
                if ((i & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var objectInfo = infos[i];
                if (objectInfo == null)
                {
                    continue;
                }

                var classId = ClassIDTypeHelper.FromClassId(objectInfo.classID);
                if (!objectTypes.Contains(classId))
                {
                    continue;
                }

                if (sourceFile.TryGetObject(objectInfo.m_PathID, out var existing) && existing != null)
                {
                    continue;
                }

                localReader ??= sourceFile.reader.Clone();
                try
                {
                    var objectReader = new ObjectReader(localReader, sourceFile, objectInfo);
                    var obj = AssetsManager.CreateObjectFromReader(objectReader);
                    sourceFile.AddObject(obj);

                    var handle = assetsManager.ProjectIndex?.GetHandle(sourceFile, objectInfo.m_PathID);
                    if (handle != null)
                    {
                        handle.RealObject = obj;
                        if (assetsManager.LazyLoading)
                        {
                            assetsManager.LruCache.RecordAccess(handle);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Scene export skipped object {classId} PathID={objectInfo.m_PathID} in '{sourceFile.fileName}': {ex.Message}");
                }
            }
        }
        finally
        {
            localReader?.Dispose();
        }
    }

    private SerializedFile? TryResolveSemanticSceneFileForSelectedAsset(AssetItem selectedItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AssetStudio.Object? asset = selectedItem.Asset;
        if (asset == null && selectedItem.Handle != null)
        {
            asset = ResolveSemanticRelationHandleForPreview(selectedItem.Handle);
        }

        if (asset is Mesh mesh)
        {
            var renderer = TryFindRendererForMeshFromSemanticCache(mesh);
            if (renderer?.assetsFile != null)
            {
                return renderer.assetsFile;
            }

            if (TryFindModelGroupForMesh(mesh, out var group, out var parts) && group != null)
            {
                if (!string.IsNullOrWhiteSpace(group.RootGameObjectAssetId))
                {
                    var rootGo = ResolvePreviewCandidateAsset(group.RootGameObjectAssetId) as GameObject;
                    if (rootGo?.assetsFile != null)
                    {
                        return rootGo.assetsFile;
                    }
                }

                var firstRendererId = parts.FirstOrDefault(static p => !string.IsNullOrWhiteSpace(p.RendererAssetId))?.RendererAssetId;
                if (!string.IsNullOrWhiteSpace(firstRendererId))
                {
                    var partRenderer = ResolvePreviewCandidateAsset(firstRendererId) as Renderer;
                    if (partRenderer?.assetsFile != null)
                    {
                        return partRenderer.assetsFile;
                    }
                }
            }
        }
        else if (asset is Animator animator && animator.m_GameObject != null && animator.m_GameObject.TryGet(out var animGo) && animGo?.assetsFile != null)
        {
            return animGo.assetsFile;
        }

        return null;
    }

    private Mesh? ResolveSceneMeshForRenderer(Renderer renderer, SceneExportResolverContext context)
    {
        if (renderer == null)
        {
            return null;
        }

        PPtr<Mesh>? meshPtr = null;
        AssetStudio.Object? meshOwner = null;
        if (renderer is SkinnedMeshRenderer smr)
        {
            meshPtr = smr.m_Mesh;
            meshOwner = smr;
        }
        else
        {
            GameObject? go = null;
            if (renderer.m_GameObject != null && !renderer.m_GameObject.IsNull)
            {
                renderer.m_GameObject.TryGet(out go);
            }

            if (go != null)
            {
                if (go.m_MeshFilter == null)
                {
                    SceneGlbExportService.EnsureGameObjectComponentsLinked(go);
                }

                if (go.m_MeshFilter != null)
                {
                    meshPtr = go.m_MeshFilter.m_Mesh;
                    meshOwner = go.m_MeshFilter;
                }
            }
        }

        if (meshPtr == null || meshPtr.IsNull || meshOwner == null)
        {
            return null;
        }

        var cacheKey = $"{meshOwner.assetsFile?.fileName ?? string.Empty}|{meshPtr.m_FileID}|{meshPtr.m_PathID}";
        if (context.MeshCache.TryGetValue(cacheKey, out var cachedMesh))
        {
            return cachedMesh;
        }

        Mesh? resolved = null;
        if (!meshPtr.TryGet(out resolved) && assetsManager.LazyLoading)
        {
            var meshHandle = FindMeshHandleForPPtr(meshOwner, meshPtr);
            if (meshHandle != null)
            {
                resolved = ResolveSemanticRelationHandleForPreview(meshHandle) as Mesh;
            }
        }

        context.MeshCache[cacheKey] = resolved;
        return resolved;
    }

    private AssetHandle? FindMeshHandleForPPtr(AssetStudio.Object owner, PPtr<Mesh> meshRef)
    {
        if (owner?.assetsFile == null || meshRef == null || meshRef.IsNull)
        {
            return null;
        }

        if (meshRef.TryGetAssetsFile(out var loadedSourceFile))
        {
            var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(loadedSourceFile, meshRef.m_PathID));
            if (handle != null && handle.PathID == meshRef.m_PathID && handle.Type == ClassIDType.Mesh)
            {
                return handle;
            }
        }

        var candidateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (meshRef.m_FileID == 0)
        {
            candidateFileNames.Add(owner.assetsFile.fileName);
        }
        else if (meshRef.m_FileID > 0
            && owner.assetsFile.m_Externals != null
            && meshRef.m_FileID - 1 < owner.assetsFile.m_Externals.Count)
        {
            var external = owner.assetsFile.m_Externals[meshRef.m_FileID - 1];
            AddCandidateFileName(candidateFileNames, external.fileName);
            AddCandidateFileName(candidateFileNames, external.pathName);

            if (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out var sourceFile))
            {
                AddCandidateFileName(candidateFileNames, sourceFile.fileName);
                var handle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, meshRef.m_PathID));
                if (handle != null && handle.PathID == meshRef.m_PathID && handle.Type == ClassIDType.Mesh)
                {
                    return handle;
                }
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            var handle = assetsManager.ProjectIndex
                .GetHandlesForFile(fileName)
                .FirstOrDefault(candidate => candidate != null
                    && candidate.PathID == meshRef.m_PathID
                    && candidate.Type == ClassIDType.Mesh);
            if (handle != null)
            {
                return handle;
            }
        }

        return null;
    }

    private Material? ResolveSceneMaterialForSubmesh(
        Renderer renderer,
        Mesh mesh,
        int slotIndex,
        SceneExportResolverContext context)
    {
        if (renderer == null || mesh == null || slotIndex < 0)
        {
            return null;
        }

        if (!context.RendererMaterialsCache.TryGetValue(renderer, out var resolvedMaterials))
        {
            resolvedMaterials = ResolveSceneMaterialsForRenderer(renderer, mesh, context);
            context.RendererMaterialsCache[renderer] = resolvedMaterials;
        }

        var material = SceneGlbExportService.SelectMaterialForSlot(resolvedMaterials, slotIndex);
        if (material == null && renderer.m_StaticBatchInfo?.subMeshCount > 0)
        {
            material = SceneGlbExportService.SelectMaterialForSlot(resolvedMaterials, slotIndex + renderer.m_StaticBatchInfo.firstSubMesh);
        }

        return material;
    }

    private List<Material?> ResolveSceneMaterialsForRenderer(
        Renderer renderer,
        Mesh mesh,
        SceneExportResolverContext context)
    {
        // Reuse Export Model's exact material + Material Variant + texture materialization pipeline.
        var materials = ResolveExportMaterialsForPart(
            mesh,
            renderer,
            context.VisitedMaterials,
            context.VisitedTextures);

        // When a multi-material renderer resolved some local slots directly via PPtr while other cross-bundle
        // slots remained null, fill the remaining null slots from SQLite v15 per-renderer/mesh resolution.
        if (assetsManager.LazyLoading && materials.Any(static m => m == null))
        {
            var effectiveRendererId = GetPreviewObjectKey(renderer);
            var resolvedSlots = TryLoadResolvedMeshMaterialsForPreview(mesh, effectiveRendererId);
            if (resolvedSlots.Count > 0)
            {
                var newlyResolved = new List<Material?>();
                foreach (var slot in resolvedSlots)
                {
                    if (slot.SubMeshIndex < 0)
                    {
                        continue;
                    }

                    while (materials.Count <= slot.SubMeshIndex)
                    {
                        materials.Add(null);
                    }

                    if (materials[slot.SubMeshIndex] != null)
                    {
                        continue;
                    }

                    var resolvedMat = !string.IsNullOrWhiteSpace(slot.MaterialAssetId)
                        ? ResolvePreviewCandidateAsset(slot.MaterialAssetId) as Material
                        : null;
                    if (resolvedMat == null)
                    {
                        continue;
                    }

                    materials[slot.SubMeshIndex] = resolvedMat;
                    newlyResolved.Add(resolvedMat);

                    if (!string.IsNullOrWhiteSpace(slot.MainTextureAssetId))
                    {
                        Texture2D? resolvedTex = context.VisitedTextures.Add($"resolvedTex|{slot.MainTextureAssetId}")
                            ? ResolvePreviewCandidateAsset(slot.MainTextureAssetId) as Texture2D
                            : assetsManager.ProjectIndex?.GetHandle(slot.MainTextureAssetId)?.RealObject as Texture2D;

                        if (resolvedTex != null)
                        {
                            lock (previewCacheLock)
                            {
                                materialMainTextureCache ??= new Dictionary<Material, Texture2D?>();
                                materialMainTextureCache[resolvedMat] = resolvedTex;
                            }
                        }
                    }
                }

                if (newlyResolved.Count > 0)
                {
                    EnsureMaterialTexturesLoadedForExport(newlyResolved, context.VisitedMaterials, context.VisitedTextures);
                }
            }
        }

        return materials;
    }

    private void ReportSceneExportProgress(int percent, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsWindowClosingOrClosed)
            {
                return;
            }
            ViewModel.LoadingProgress = Math.Clamp(percent, 0, 100);
            StatusStripUpdate(status);
        }, DispatcherPriority.Background);
    }

    private async Task CompleteSceneGlbExportUiAsync(SceneGlbExportResult? result, string? exportFolder)
    {
        ViewModel.LoadingProgress = 100;
        if (result != null && result.Success)
        {
            ExportState.Complete(1, statusText: result.SummaryMessage, errorLogPath: null);
            StatusStripUpdate(result.SummaryMessage);

            if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(exportFolder))
            {
                OpenFolder(exportFolder);
            }
            else
            {
                await ViewModel.DialogService.ShowMessageAsync("Scene Export Complete", result.SummaryMessage);
            }
        }
        else
        {
            var errorMsg = result?.ErrorMessage ?? "Scene export failed.";
            ExportState.Fail(errorMsg, null);
            StatusStripUpdate($"Scene export failed: {errorMsg}");
            await ViewModel.DialogService.ShowErrorAsync("Scene Export Failed", errorMsg);
        }
    }

    private async Task<string?> PickSaveSceneGlbDialogAsync(string title, string suggestedFileName)
    {
        var filters = new[]
        {
            new FileTypeFilterSpec("glTF Binary Scene (*.glb)", "*.glb"),
            new FileTypeFilterSpec("glTF Separate Scene (*.gltf)", "*.gltf")
        };

        var result = await ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: title,
            suggestedFileName: suggestedFileName,
            defaultExtension: "glb",
            suggestedStartDirectory: GetSuggestedExportStartDirectory(),
            fileTypeFilters: filters));

        return result;
    }

    private string? GetSuggestedExportStartDirectory()
    {
        if (!string.IsNullOrWhiteSpace(appSettings.ExportFolderPath) && Directory.Exists(appSettings.ExportFolderPath))
        {
            return appSettings.ExportFolderPath;
        }
        return null;
    }

    private LazySceneExportTarget? ResolveLazySceneExportTarget()
    {
        // 1. Selected asset in Asset List (SceneAsset, GameObject, Mesh, Animator, or any asset inside a scene/prefab bundle)
        if (AssetListDataGrid?.SelectedItem is AssetItem selected)
        {
            var loadedSf = selected.SourceFile?.reader != null
                ? selected.SourceFile
                : (selected.Handle?.SourceFile?.reader != null ? selected.Handle.SourceFile : null);
            var sfName = loadedSf?.fileName
                ?? selected.SourceFile?.fileName
                ?? selected.Handle?.SerializedFileName;

            var srcPath = (selected.Handle != null ? ResolveLazyHandleSourcePath(selected.Handle) : null)
                ?? TryResolveExistingPath(selected.SourceFile?.originalPath)
                ?? TryResolveExistingPath(selected.SourceFile?.fullName)
                ?? TryResolveExistingPath(selected.Handle?.OriginalPath);

            if (loadedSf != null || (!string.IsNullOrWhiteSpace(srcPath) && File.Exists(srcPath)))
            {
                var sceneName = selected.Type == ClassIDType.SceneAsset && !string.IsNullOrWhiteSpace(selected.Name)
                    ? selected.Name
                    : (!string.IsNullOrWhiteSpace(sfName)
                        ? sfName
                        : (!string.IsNullOrWhiteSpace(srcPath) ? Path.GetFileNameWithoutExtension(srcPath) : "Scene"));
                return new LazySceneExportTarget(srcPath, sfName, sceneName, loadedSf, selected);
            }
        }

        // 2. Loaded SerializedFiles that look like scenes (levelN / mainData / contain GameObjects)
        foreach (var sf in GetLoadedFilesSnapshot())
        {
            if (sf == null)
            {
                continue;
            }

            var hasReader = sf.reader != null;
            var existingPath = TryResolveExistingPath(sf.originalPath) ?? TryResolveExistingPath(sf.fullName);
            if (!hasReader && string.IsNullOrWhiteSpace(existingPath))
            {
                continue;
            }

            var looksLikeScene = sf.fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sf.fileName, "mainData", StringComparison.OrdinalIgnoreCase)
                || sf.m_Objects.Any(static info => info != null && info.classID == (int)ClassIDType.GameObject)
                || sf.Objects.Any(static o => o is GameObject);
            if (looksLikeScene)
            {
                var sceneName = !string.IsNullOrWhiteSpace(sf.fileName)
                    ? sf.fileName
                    : (!string.IsNullOrWhiteSpace(existingPath) ? Path.GetFileNameWithoutExtension(existingPath) : "Scene");
                return new LazySceneExportTarget(existingPath, sf.fileName, sceneName, hasReader ? sf : null, null);
            }
        }

        // 3. Scan Lazy ProjectIndex Arena for a levelN / scene bundle source file
        var arena = assetsManager.ProjectIndex.Arena;
        for (int i = 0; i < arena.Count; i++)
        {
            if (arena.GetType(i) != ClassIDType.GameObject)
            {
                continue;
            }

            var sfName = arena.GetSerializedFileName(i);
            var origPath = arena.GetOriginalPath(i);
            if (string.IsNullOrWhiteSpace(origPath))
            {
                continue;
            }

            var looksLikeScene = sfName.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sfName, "mainData", StringComparison.OrdinalIgnoreCase)
                || origPath.Contains("scene", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeScene)
            {
                continue;
            }

            var resolvedPath = TryResolveExistingPath(origPath)
                ?? FindSourceFileByNameInProjectRoot(GetSafeFileName(origPath));
            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
            {
                var sceneName = !string.IsNullOrWhiteSpace(sfName)
                    ? sfName
                    : Path.GetFileNameWithoutExtension(resolvedPath);
                return new LazySceneExportTarget(resolvedPath, sfName, sceneName, null, null);
            }
        }

        return null;
    }
}
