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
    private SceneExportOptions _lastSceneExportOptions = new();

    private sealed class SceneExportResolverContext
    {
        public HashSet<Material> VisitedMaterials { get; } = new();
        public HashSet<string> VisitedTextures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Mesh?> MeshCache { get; } = new(StringComparer.Ordinal);
        public Dictionary<Renderer, List<Material?>> RendererMaterialsCache { get; } = new();
    }

    private sealed class SceneCandidateBinding
    {
        public required SceneExportCandidate Candidate { get; init; }
        public GameObjectNode? LoadedSceneRootNode { get; init; }
        public SerializedFile? LoadedSerializedFile { get; init; }
        public IReadOnlyList<(string SerializedFileName, string OriginalPath)>? CompanionStageFiles { get; init; }
    }

    private async Task ExportSceneGlbCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var (candidates, bindingMap) = GatherAvailableSceneExportCandidates();
        if (candidates.Count == 0)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Export Scene to .glb",
                "No loaded Scene Hierarchy, BuildSettings scene, .unity scene bundle, streamed scene bundle, or Addressables SceneProvider entry was detected.\n\n" +
                "Load a Unity game folder or scene file (such as level0 or a scene bundle) first.");
            return;
        }

        var dialogRequest = new SceneExportDialogRequest(
            scenes: candidates,
            initialOptions: _lastSceneExportOptions,
            dialogTitle: "Export Unity Scene to .glb (Instanced)");

        var dialogResult = await ViewModel.DialogService.ShowSceneExportDialogAsync(dialogRequest);
        if (dialogResult == null || dialogResult.SelectedScenes.Count == 0)
        {
            return;
        }

        _lastSceneExportOptions = dialogResult.Options;

        var selectedBindings = new List<SceneCandidateBinding>(dialogResult.SelectedScenes.Count);
        foreach (var selectedCandidate in dialogResult.SelectedScenes)
        {
            if (bindingMap.TryGetValue(selectedCandidate.Id, out var binding))
            {
                selectedBindings.Add(binding);
            }
            else
            {
                selectedBindings.Add(new SceneCandidateBinding { Candidate = selectedCandidate });
            }
        }

        var primaryCandidate = selectedBindings[0].Candidate;
        var suggestedBaseName = selectedBindings.Count == 1
            ? primaryCandidate.Name
            : $"{primaryCandidate.Name}_Combined";
        var suggestedFileName = FixFileName(string.IsNullOrWhiteSpace(suggestedBaseName) ? "Scene" : suggestedBaseName) + ".glb";

        var saveFile = await PickSaveSceneGlbDialogAsync("Export Scene to .glb (Instanced)", suggestedFileName);
        if (string.IsNullOrWhiteSpace(saveFile))
        {
            return;
        }

        var exportFolder = Path.GetDirectoryName(saveFile);
        if (!string.IsNullOrEmpty(exportFolder))
        {
            SaveExportFolder(exportFolder);
        }

        var displaySceneName = selectedBindings.Count == 1
            ? primaryCandidate.Name
            : $"{primaryCandidate.Name} (+{selectedBindings.Count - 1} additive)";
        var outputSceneName = !string.IsNullOrWhiteSpace(primaryCandidate.Name) && selectedBindings.Count == 1
            ? primaryCandidate.Name
            : Path.GetFileNameWithoutExtension(saveFile);

        var ctsToken = ExportState.BeginExport(
            ExportOperationMode.SplitObjects,
            100,
            exportFolder,
            $"Preparing scene '{displaySceneName}'...");
        StatusStripUpdate($"Exporting scene '{displaySceneName}' to {Path.GetFileName(saveFile)}...");

        SceneGlbExportResult? result = null;
        try
        {
            var sceneExportOptions = dialogResult.Options;
            var includeAnimations = exportOptions.ExportAnimations;
            var cacheFolder = GetCurrentCacheFolderPath();
            var cacheSignature = currentScanResult != null && !string.IsNullOrWhiteSpace(cacheFolder)
                ? _sqliteCache.GetFolderSignature(currentScanResult)
                : null;
            var projectRoot = !string.IsNullOrWhiteSpace(assetsManager.ProjectRoot)
                ? assetsManager.ProjectRoot
                : cacheFolder;

            await Task.Run(() =>
            {
                using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;
                using var textureResolverScope = ModelConverter.PushTextureResolver(ResolveTextureForModelConverter);

                ctsToken.ThrowIfCancellationRequested();
                ReportSceneExportProgress(4, $"Loading {selectedBindings.Count} selected scene(s)...");

                var rootGameObjects = new List<GameObject>();
                var seenRoots = new HashSet<GameObject>();
                var seedSerializedFiles = new List<SerializedFile>();
                var seenSeedFiles = new HashSet<SerializedFile>();

                var requiredObjectTypes = SceneGlbExportService.GetRequiredSceneObjectTypes(
                    includeAnimations: includeAnimations,
                    exportCamerasAndLights: sceneExportOptions.ExportCamerasAndLights,
                    resolveStaticScriptSpawns: sceneExportOptions.ResolveStaticScriptSpawns);

                // Step 1: Resolve and load every selected scene (supports additive multi-scene merging!).
                for (var i = 0; i < selectedBindings.Count; i++)
                {
                    ctsToken.ThrowIfCancellationRequested();
                    var binding = selectedBindings[i];

                    if (binding.LoadedSceneRootNode != null)
                    {
                        var nodeRoots = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(binding.LoadedSceneRootNode);
                        foreach (var go in nodeRoots)
                        {
                            if (go != null && seenRoots.Add(go))
                            {
                                rootGameObjects.Add(go);
                                if (go.assetsFile != null && seenSeedFiles.Add(go.assetsFile))
                                {
                                    seedSerializedFiles.Add(go.assetsFile);
                                }
                            }
                        }
                        continue;
                    }

                    var targetFiles = EnsureSceneCandidateSerializedFilesLoaded(binding, ctsToken);
                    foreach (var sf in targetFiles)
                    {
                        if (sf != null && seenSeedFiles.Add(sf))
                        {
                            seedSerializedFiles.Add(sf);
                        }
                    }
                }

                // Step 1b: Resolve modular companion stage preset bundles (if any) BEFORE dependency closure
                // so cross-bundle Mesh/Material/Texture2D dependencies of companion stage prefabs are also loaded!
                var companionGroups = new List<(string StageStem, IReadOnlyList<SerializedFile> CompanionFiles)>();
                var companionSerializedFilesSet = new HashSet<SerializedFile>();
                for (var i = 0; i < selectedBindings.Count; i++)
                {
                    ctsToken.ThrowIfCancellationRequested();
                    var binding = selectedBindings[i];
                    var stageStem = ProjectLoadingService.ExtractSceneStageStem(binding.Candidate.Name, binding.Candidate.ScenePath);
                    if (stageStem.Length < 6 || binding.CompanionStageFiles == null || binding.CompanionStageFiles.Count == 0)
                    {
                        continue;
                    }

                    var groupFiles = new List<SerializedFile>();
                    foreach (var (compSfName, compOrigPath) in binding.CompanionStageFiles)
                    {
                        var compDiskPath = TryResolveExistingPath(compOrigPath) ?? ResolveSceneDependencyDiskPath(compSfName);
                        if (!string.IsNullOrWhiteSpace(compDiskPath) && File.Exists(compDiskPath))
                        {
                            if (!assetsManager.TryFindSerializedFile(compSfName, null, out var existingComp) || existingComp?.reader == null)
                            {
                                RemovePendingFileFromProgressiveQueue(compDiskPath);
                                assetsManager.LoadFilesForPreview(compDiskPath);
                                if (!string.IsNullOrWhiteSpace(compSfName))
                                {
                                    assetsManager.WaitForAssetsFileLoaded(compSfName, 5000);
                                }
                            }
                        }

                        if (assetsManager.TryFindSerializedFile(compSfName, null, out var loadedComp) && loadedComp?.reader != null)
                        {
                            if (!groupFiles.Contains(loadedComp))
                            {
                                groupFiles.Add(loadedComp);
                            }
                            companionSerializedFilesSet.Add(loadedComp);
                        }
                    }

                    if (groupFiles.Count > 0)
                    {
                        companionGroups.Add((stageStem, groupFiles));
                    }
                }

                // Step 2: Transitive cross-bundle dependency closure (SQLite SourceFileExternals BFS + levelN<->sharedassetsN + Addressables).
                var closureSeeds = companionSerializedFilesSet.Count > 0
                    ? seedSerializedFiles.Concat(companionSerializedFilesSet).Distinct().ToList()
                    : seedSerializedFiles;

                SceneDependencyClosureResult? dependencyClosure = null;
                if (closureSeeds.Count > 0)
                {
                    ReportSceneExportProgress(10, $"Resolving cross-bundle dependencies for '{displaySceneName}'...");
                    dependencyClosure = SceneGlbExportService.ResolveAndLoadSceneDependencies(
                        assetsManager: assetsManager,
                        seedFiles: closureSeeds,
                        sqliteCache: _sqliteCache,
                        cacheFolderPath: cacheFolder,
                        cacheSignature: cacheSignature,
                        projectRoot: projectRoot,
                        resolveSourcePathByFileName: ResolveSceneDependencyDiskPath,
                        beforeLoadFile: RemovePendingFileFromProgressiveQueue,
                        maxDepth: sceneExportOptions.MaxExternalDependencyDepth,
                        maxAdditionalFiles: sceneExportOptions.MaxExternalFilesToLoad,
                        cancellationToken: ctsToken);

                    HashSet<ClassIDType>? companionObjectTypes = null;
                    if (companionSerializedFilesSet.Count > 0)
                    {
                        companionObjectTypes = new HashSet<ClassIDType>(requiredObjectTypes)
                        {
                            ClassIDType.MonoBehaviour,
                            ClassIDType.MonoScript
                        };
                    }

                    // Materialize scene + companion + dependency objects.
                    ReportSceneExportProgress(16, $"Materializing hierarchy & components ({dependencyClosure.AllLoadedFiles.Count} file(s))...");
                    foreach (var sf in dependencyClosure.AllLoadedFiles)
                    {
                        var typesForSf = companionObjectTypes != null && companionSerializedFilesSet.Contains(sf)
                            ? companionObjectTypes
                            : requiredObjectTypes;
                        MaterializeSceneObjectsForSerializedFile(sf, typesForSf, ctsToken);
                    }
                }

                // Step 3: Collect roots for any lazily loaded scene files not already coming from loaded SceneTreeNodes.
                var lazySeedFiles = seedSerializedFiles
                    .Where(sf => !selectedBindings.Any(b => b.LoadedSceneRootNode != null && rootGameObjects.Any(go => ReferenceEquals(go.assetsFile, sf))))
                    .ToList();

                var usedInactiveFallback = false;
                if (lazySeedFiles.Count > 0)
                {
                    var collectedLazyRoots = sceneExportOptions.IncludeInactive
                        ? lazySeedFiles.SelectMany(sf => SceneGlbExportService.CollectRootGameObjectsFromSerializedFile(sf, includeInactive: true)).ToList()
                        : SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(lazySeedFiles, out usedInactiveFallback);

                    // Fallback to sibling CABs in the same scene bundle(s) if a metadata-only CAB (e.g. *.sharedAssets)
                    // had no GameObjects of its own. Works for both single-scene and additive multi-scene exports.
                    if (collectedLazyRoots.Count == 0)
                    {
                        var candidatePaths = selectedBindings
                            .Select(static b => b.Candidate.OriginalPath)
                            .Where(static p => !string.IsNullOrWhiteSpace(p))
                            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (candidatePaths.Count > 0)
                        {
                            var siblingFiles = GetLoadedFilesSnapshot()
                                .Where(sf => sf?.reader != null
                                    && !seenSeedFiles.Contains(sf)
                                    && candidatePaths.Any(srcPath => IsSameLazySource(sf.originalPath, srcPath) || IsSameLazySource(sf.fullName, srcPath)))
                                .ToList();
                            foreach (var sf in siblingFiles)
                            {
                                MaterializeSceneObjectsForSerializedFile(sf, requiredObjectTypes, ctsToken);
                            }
                            if (siblingFiles.Count > 0)
                            {
                                collectedLazyRoots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(siblingFiles, out usedInactiveFallback);
                            }
                        }
                    }

                    foreach (var go in collectedLazyRoots)
                    {
                        if (go != null && seenRoots.Add(go))
                        {
                            rootGameObjects.Add(go);
                        }
                    }
                }

                if (rootGameObjects.Count == 0)
                {
                    result = new SceneGlbExportResult
                    {
                        Success = false,
                        OutputPath = saveFile,
                        SceneName = outputSceneName,
                        ErrorMessage = $"No GameObject hierarchy was found inside '{displaySceneName}'."
                    };
                    return;
                }

                // Step 4: Resolve modular stage preset attachments (if any) + optional SceneScriptSpawnAnalyzer.
                List<SceneScriptSpawnAttachment>? companionAttachments = null;
                if (companionGroups.Count > 0)
                {
                    companionAttachments = SceneGlbExportService.ResolveCompanionStagePresetAttachments(
                        rootGameObjects,
                        companionGroups,
                        assemblyLoader,
                        ctsToken,
                        preferredStagePresetName: sceneExportOptions.PreferredStagePresetName);
                }

                SceneScriptSpawnAnalysisOptions? scriptSpawnOptions = null;
                if (sceneExportOptions.ResolveStaticScriptSpawns)
                {
                    scriptSpawnOptions = BuildSceneScriptSpawnAnalysisOptions(
                        sceneExportOptions,
                        dependencyClosure?.AddressablesCatalogs,
                        projectRoot,
                        requiredObjectTypes,
                        ctsToken);
                }

                var effectiveIncludeInactive = sceneExportOptions.IncludeInactive
                    || usedInactiveFallback
                    || !rootGameObjects.Any(static go => go != null && go.m_IsActive);

                var options = new SceneGlbExportOptions
                {
                    OutputPath = saveFile,
                    SceneName = outputSceneName,
                    ScenePath = primaryCandidate.ScenePath,
                    BuildIndex = primaryCandidate.BuildIndex,
                    AdditionalSceneNames = selectedBindings.Skip(1).Select(static b => b.Candidate.Name).ToList(),
                    IncludeInactive = effectiveIncludeInactive,
                    SkipDisabledRenderers = true,
                    LodMode = sceneExportOptions.LodMode,
                    ExportCamerasAndLights = sceneExportOptions.ExportCamerasAndLights,
                    HideAuxiliaryAndShadowOnlyRenderers = sceneExportOptions.HideAuxiliaryAndShadowOnlyRenderers,
                    PreferredStagePresetName = sceneExportOptions.PreferredStagePresetName,
                    ResolveStaticScriptSpawns = sceneExportOptions.ResolveStaticScriptSpawns,
                    WriteManifest = sceneExportOptions.WriteManifest,
                    MaxScriptSpawnInstances = sceneExportOptions.MaxScriptSpawnInstances,
                    IncludeAnimations = includeAnimations,
                    ExportSkins = exportOptions.ExportSkins,
                    ExportBlendShape = exportOptions.ExportBlendShape,
                    EnableInstancing = true,
                    ScaleFactor = (float)exportOptions.ScaleFactor,
                    ImageFormat = exportOptions.ConvertTextureFormat,
                    Binary = !saveFile.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase),
                    ResolvedDependencies = dependencyClosure?.ResolvedDependencies,
                    MissingDependencies = dependencyClosure?.MissingDependencies,
                    SceneSerializedFiles = closureSeeds,
                    ScriptSpawnAnalysisOptions = scriptSpawnOptions,
                    PreResolvedScriptSpawnAttachments = companionAttachments
                };

                var resolverContext = new SceneExportResolverContext();
                result = _sceneGlbExportService.ExportScene(
                    rootGameObjects,
                    options,
                    textureResolver: ResolveTextureForModelConverter,
                    progressCallback: (pct, _, status) =>
                    {
                        var mappedPct = 20 + (int)(Math.Clamp(pct, 0, 100) * 0.8);
                        ReportSceneExportProgress(mappedPct, status);
                    },
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
                SceneName = outputSceneName,
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

    private (List<SceneExportCandidate> Candidates, Dictionary<string, SceneCandidateBinding> Bindings) GatherAvailableSceneExportCandidates()
    {
        var candidates = new List<SceneExportCandidate>();
        var bindings = new Dictionary<string, SceneCandidateBinding>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-select ONLY if the user explicitly selected a scene root in Scene Hierarchy.
        // Never bind or pre-select unrelated items highlighted in Asset List (meshes, textures, prefabs, etc.).
        GameObjectNode? activeSceneRoot = null;
        if (SceneTreeView?.SelectedItem is GameObjectNode selectedNode)
        {
            activeSceneRoot = SceneGlbExportService.FindSceneRootNode(selectedNode);
        }
        else
        {
            var checkedNodes = GetSelectedParentNodes();
            if (checkedNodes.Count > 0)
            {
                activeSceneRoot = SceneGlbExportService.FindSceneRootNode(checkedNodes[0]);
            }
        }

        // Fetch indexed scenes from SQLite v16 cache (if available) + live discovery.
        var indexedScenes = new List<IndexedSceneRecord>();
        var cacheFolder = GetCurrentCacheFolderPath();
        if (currentScanResult != null && !string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder))
        {
            var signature = _sqliteCache.GetFolderSignature(currentScanResult);
            indexedScenes.AddRange(_sqliteCache.GetIndexedScenes(cacheFolder, signature));
        }

        var discovered = ProjectLoadingService.DiscoverScenesAndExternals(
            assetsManager,
            !string.IsNullOrWhiteSpace(cacheFolder) ? cacheFolder : assetsManager.ProjectRoot);

        // Prefer live discovered metadata (which includes fresh multi-CAB resolution and streamed RenderSettings scenes),
        // then fill any persisted SQLite entries not re-discovered live.
        var combinedIndexed = discovered.Scenes.Concat(indexedScenes).ToList();

        // Build lookup from SerializedFileName -> richer BuildSettings/Bundle metadata.
        var richByFileName = new Dictionary<string, IndexedSceneRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in combinedIndexed)
        {
            if (rec != null && !string.IsNullOrWhiteSpace(rec.EntrySerializedFileName))
            {
                richByFileName.TryAdd(rec.EntrySerializedFileName, rec);
            }
        }

        (int GoCount, int RendererCount) LookupSceneStats(string? sfName, SerializedFile? loadedSf = null)
        {
            if (!string.IsNullOrWhiteSpace(sfName) && discovered.SceneFileStats.TryGetValue(sfName, out var stats))
            {
                return stats;
            }

            if (loadedSf != null)
            {
                var goCount = 0;
                var rendererCount = 0;
                if (loadedSf.m_Objects != null && loadedSf.m_Objects.Count > 0)
                {
                    for (var i = 0; i < loadedSf.m_Objects.Count; i++)
                    {
                        var info = loadedSf.m_Objects[i];
                        if (info == null) continue;
                        if (info.classID == (int)ClassIDType.GameObject) goCount++;
                        else if (info.classID == (int)ClassIDType.MeshRenderer || info.classID == (int)ClassIDType.SkinnedMeshRenderer) rendererCount++;
                    }
                    return (goCount, rendererCount);
                }

                foreach (var obj in loadedSf.Objects)
                {
                    if (obj is GameObject) goCount++;
                    else if (obj is MeshRenderer || obj is SkinnedMeshRenderer) rendererCount++;
                }
                return (goCount, rendererCount);
            }

            return (-1, -1);
        }

        void AddCandidate(SceneExportCandidate candidate, GameObjectNode? rootNode = null, SerializedFile? loadedFile = null)
        {
            var dedupKey = !string.IsNullOrWhiteSpace(candidate.ScenePath)
                ? $"path:{candidate.ScenePath}"
                : (!string.IsNullOrWhiteSpace(candidate.SerializedFileName)
                    ? $"file:{candidate.SerializedFileName}"
                    : $"id:{candidate.Id}");
            if (!seenKeys.Add(dedupKey))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(candidate.SerializedFileName))
            {
                seenKeys.Add($"file:{candidate.SerializedFileName}");
            }

            IReadOnlyList<(string SerializedFileName, string OriginalPath)>? companionFiles = null;
            if (!string.IsNullOrWhiteSpace(candidate.SerializedFileName)
                && discovered.CompanionStageFilesBySceneSf.TryGetValue(candidate.SerializedFileName, out var foundCompanions))
            {
                companionFiles = foundCompanions;
            }

            if (companionFiles != null && companionFiles.Count > 0)
            {
                var discoveredPresets = DiscoverStagePresetsForCompanionFiles(candidate.Name, candidate.ScenePath, companionFiles);
                if (discoveredPresets.Count > 0)
                {
                    candidate = new SceneExportCandidate
                    {
                        Id = candidate.Id,
                        Name = candidate.Name,
                        ScenePath = candidate.ScenePath,
                        BuildIndex = candidate.BuildIndex,
                        SourceKind = candidate.SourceKind,
                        SerializedFileName = candidate.SerializedFileName,
                        OriginalPath = candidate.OriginalPath,
                        ContainerKey = candidate.ContainerKey,
                        IsPreselected = candidate.IsPreselected,
                        GameObjectCount = candidate.GameObjectCount,
                        RendererCount = candidate.RendererCount,
                        AvailableStagePresets = discoveredPresets
                    };
                }
            }

            candidates.Add(candidate);

            bindings[candidate.Id] = new SceneCandidateBinding
            {
                Candidate = candidate,
                LoadedSceneRootNode = rootNode,
                LoadedSerializedFile = loadedFile,
                CompanionStageFiles = companionFiles
            };
        }

        // 1. Loaded Scene Hierarchy roots (Eager mode or already-loaded scene roots).
        for (var i = 0; i < sceneTreeNodes.Count; i++)
        {
            var rootNode = sceneTreeNodes[i];
            if (rootNode == null)
            {
                continue;
            }

            var rawName = string.IsNullOrWhiteSpace(rootNode.Name) ? $"Scene_{i}" : rootNode.Name;
            var firstGo = SceneGlbExportService.CollectRootGameObjectsFromSceneNode(rootNode).FirstOrDefault();
            var sf = firstGo?.assetsFile;
            var sfName = sf?.fileName ?? rawName;
            richByFileName.TryGetValue(sfName, out var rich);

            var displayName = rich != null && !string.IsNullOrWhiteSpace(rich.Name) ? rich.Name : rawName;
            var isPreselected = ReferenceEquals(rootNode, activeSceneRoot);
            var (goCount, rendererCount) = LookupSceneStats(sfName, sf);

            AddCandidate(new SceneExportCandidate
            {
                Id = $"loaded:{i}:{sfName}",
                Name = displayName,
                ScenePath = rich?.ScenePath ?? sfName,
                BuildIndex = rich?.BuildIndex ?? -1,
                SourceKind = rich != null && !string.IsNullOrWhiteSpace(rich.SourceKind) ? rich.SourceKind : "Loaded",
                SerializedFileName = sfName,
                OriginalPath = sf?.originalPath ?? rich?.EntryOriginalPath ?? string.Empty,
                ContainerKey = rich?.ContainerKey ?? string.Empty,
                IsPreselected = isPreselected,
                GameObjectCount = goCount,
                RendererCount = rendererCount
            }, rootNode: rootNode, loadedFile: sf);
        }

        // 2. Live Discovered + Persisted SQLite v16 Scenes (BuildSettings, AssetBundle .unity, Addressables SceneProvider, Streamed SceneBundle, Heuristic).
        for (var i = 0; i < combinedIndexed.Count; i++)
        {
            var rec = combinedIndexed[i];
            if (rec == null)
            {
                continue;
            }

            var (goCount, rendererCount) = LookupSceneStats(rec.EntrySerializedFileName);

            AddCandidate(new SceneExportCandidate
            {
                Id = $"indexed:{i}:{rec.SourceKind}:{rec.ScenePath}:{rec.EntrySerializedFileName}",
                Name = string.IsNullOrWhiteSpace(rec.Name) ? rec.EntrySerializedFileName : rec.Name,
                ScenePath = rec.ScenePath,
                BuildIndex = rec.BuildIndex,
                SourceKind = string.IsNullOrWhiteSpace(rec.SourceKind) ? "Indexed" : rec.SourceKind,
                SerializedFileName = rec.EntrySerializedFileName,
                OriginalPath = rec.EntryOriginalPath,
                ContainerKey = rec.ContainerKey,
                IsPreselected = false,
                GameObjectCount = goCount,
                RendererCount = rendererCount
            });
        }

        // 3. Final fallback ONLY when no real Unity scenes exist anywhere (e.g. user directly loaded a single standalone .assets/bundle file).
        if (candidates.Count == 0)
        {
            foreach (var sf in GetLoadedFilesSnapshot())
            {
                if (sf == null || ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                {
                    continue;
                }

                var hasReader = sf.reader != null;
                var existingPath = TryResolveExistingPath(sf.originalPath) ?? TryResolveExistingPath(sf.fullName);
                if (!hasReader && string.IsNullOrWhiteSpace(existingPath))
                {
                    continue;
                }

                var (goCount, rendererCount) = LookupSceneStats(sf.fileName, sf);
                if (goCount <= 0)
                {
                    continue;
                }

                var name = !string.IsNullOrWhiteSpace(sf.fileName) ? sf.fileName : Path.GetFileNameWithoutExtension(existingPath ?? "Scene");
                AddCandidate(new SceneExportCandidate
                {
                    Id = $"loadedFile:{sf.fileName}",
                    Name = name,
                    ScenePath = sf.fileName ?? string.Empty,
                    BuildIndex = -1,
                    SourceKind = "Loaded",
                    SerializedFileName = sf.fileName ?? string.Empty,
                    OriginalPath = existingPath ?? string.Empty,
                    IsPreselected = false,
                    GameObjectCount = goCount,
                    RendererCount = rendererCount
                }, loadedFile: hasReader ? sf : null);
            }
        }

        // Sort candidates so explicitly preselected Scene Hierarchy roots appear first, followed by scenes with
        // real 3D geometry (RendererCount > 0), unknown (-1), and 0-renderer bootstrap/controller shells at the bottom.
        candidates.Sort(static (a, b) =>
        {
            var pre = (a.IsPreselected ? 0 : 1).CompareTo(b.IsPreselected ? 0 : 1);
            if (pre != 0) return pre;

            static int RankRenderers(int rendererCount) => rendererCount > 0 ? 0 : (rendererCount < 0 ? 1 : 2);
            var rendRank = RankRenderers(a.RendererCount).CompareTo(RankRenderers(b.RendererCount));
            if (rendRank != 0) return rendRank;

            static int RankDebugScene(SceneExportCandidate c)
            {
                var n = c.Name ?? string.Empty;
                var p = c.ScenePath ?? string.Empty;
                return (n.StartsWith("Debug", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("/Debug/", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
            }
            var debugRank = RankDebugScene(a).CompareTo(RankDebugScene(b));
            if (debugRank != 0) return debugRank;

            var hasBuild = (a.BuildIndex >= 0 ? 0 : 1).CompareTo(b.BuildIndex >= 0 ? 0 : 1);
            if (hasBuild != 0) return hasBuild;

            if (a.BuildIndex >= 0 && b.BuildIndex >= 0)
            {
                var buildCmp = a.BuildIndex.CompareTo(b.BuildIndex);
                if (buildCmp != 0) return buildCmp;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return (candidates, bindings);
    }

    private SceneScriptSpawnAnalysisOptions BuildSceneScriptSpawnAnalysisOptions(
        SceneExportOptions exportOpts,
        IReadOnlyList<ParsedAddressablesCatalog>? catalogs,
        string? projectRoot,
        HashSet<ClassIDType> requiredObjectTypes,
        CancellationToken cancellationToken)
    {
        var managedDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            try
            {
                var directManaged = Path.Combine(projectRoot, "Managed");
                if (Directory.Exists(directManaged))
                {
                    managedDirs.Add(directManaged);
                }

                foreach (var dataDir in Directory.EnumerateDirectories(projectRoot, "*_Data", SearchOption.TopDirectoryOnly))
                {
                    var subManaged = Path.Combine(dataDir, "Managed");
                    if (Directory.Exists(subManaged))
                    {
                        managedDirs.Add(subManaged);
                    }
                }
            }
            catch
            {
            }
        }

        var loadedExtraPrefabBundles = 0;
        const int maxExtraPrefabBundles = 32;

        SerializedFile? EnsureExternalPrefabFileLoaded(string targetFileName)
        {
            if (string.IsNullOrWhiteSpace(targetFileName))
            {
                return null;
            }

            if (assetsManager.TryFindSerializedFile(targetFileName, null, out var existing) && existing?.reader != null)
            {
                MaterializeSceneObjectsForSerializedFile(existing, requiredObjectTypes, cancellationToken);
                return existing;
            }

            if (loadedExtraPrefabBundles >= maxExtraPrefabBundles)
            {
                return null;
            }

            var diskPath = ResolveSceneDependencyDiskPath(targetFileName);
            if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
            {
                return null;
            }

            try
            {
                AssetsManager.ThrowIfMemoryPressureTooHigh("script-spawn prefab loading");
                RemovePendingFileFromProgressiveQueue(diskPath);
                assetsManager.LoadFilesForPreview(diskPath);
                assetsManager.WaitForAssetsFileLoaded(targetFileName, 3000);
                loadedExtraPrefabBundles++;

                if (assetsManager.TryFindSerializedFile(targetFileName, null, out var loaded) && loaded?.reader != null)
                {
                    MaterializeSceneObjectsForSerializedFile(loaded, requiredObjectTypes, cancellationToken);
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Script-spawn resolver failed to load '{targetFileName}': {ex.Message}");
            }

            return null;
        }

        GameObject? ResolveExternalPrefabGameObject(string targetFileName, long targetPathId)
        {
            var sf = EnsureExternalPrefabFileLoaded(targetFileName);
            if (sf == null)
            {
                return null;
            }

            if (sf.TryGetObject(targetPathId, out var obj))
            {
                if (obj is GameObject go)
                {
                    SceneGlbExportService.EnsureGameObjectComponentsLinked(go);
                    return go;
                }
                if (obj is Transform tr && tr.m_GameObject != null && tr.m_GameObject.TryGet(out var trGo) && trGo != null)
                {
                    SceneGlbExportService.EnsureGameObjectComponentsLinked(trGo);
                    return trGo;
                }
            }

            return null;
        }

        GameObject? ResolveAddressablesPrefab(string guid, CatalogAssetEntry entry)
        {
            if (catalogs == null || catalogs.Count == 0 || loadedExtraPrefabBundles >= maxExtraPrefabBundles)
            {
                return null;
            }

            if (!AddressablesCatalogDependencyResolver.TryResolveGuidToBundlePaths(
                    catalogs,
                    guid,
                    out var matches,
                    projectRoot)
                || matches.Count == 0)
            {
                return null;
            }

            var match = matches[0];
            if (string.IsNullOrWhiteSpace(match.PrimaryBundlePath) || !File.Exists(match.PrimaryBundlePath))
            {
                return null;
            }

            try
            {
                AssetsManager.ThrowIfMemoryPressureTooHigh("addressables script-spawn prefab loading");
                RemovePendingFileFromProgressiveQueue(match.PrimaryBundlePath);
                assetsManager.LoadFilesForPreview(match.PrimaryBundlePath);
                loadedExtraPrefabBundles++;

                foreach (var depPath in match.DependencyBundlePaths)
                {
                    if (loadedExtraPrefabBundles >= maxExtraPrefabBundles || !File.Exists(depPath))
                    {
                        break;
                    }

                    RemovePendingFileFromProgressiveQueue(depPath);
                    assetsManager.LoadFilesForPreview(depPath);
                    loadedExtraPrefabBundles++;
                }

                var bundleFiles = GetLoadedFilesSnapshot()
                    .Where(sf => sf?.reader != null
                        && (IsSameLazySource(sf.originalPath, match.PrimaryBundlePath) || IsSameLazySource(sf.fullName, match.PrimaryBundlePath)))
                    .ToList();

                foreach (var sf in bundleFiles)
                {
                    MaterializeSceneObjectsForSerializedFile(sf, requiredObjectTypes, cancellationToken);
                }

                // Try matching AssetBundle.m_Container by entry.InternalId or PrimaryKey first.
                foreach (var sf in bundleFiles)
                {
                    AssetStudio.Object[] objs;
                    lock (sf)
                    {
                        objs = sf.Objects.ToArray();
                    }

                    foreach (var obj in objs)
                    {
                        if (obj is AssetBundle ab && ab.m_Container != null)
                        {
                            foreach (var containerEntry in ab.m_Container)
                            {
                                if (string.Equals(containerEntry.Key, entry.InternalId, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(containerEntry.Key, entry.PrimaryKey, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (containerEntry.Value?.asset != null
                                        && containerEntry.Value.asset.TryGet(out var targetObj)
                                        && targetObj is GameObject prefabGo)
                                    {
                                        SceneGlbExportService.EnsureGameObjectComponentsLinked(prefabGo);
                                        return prefabGo;
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback to the first root GameObject in the prefab bundle.
                var roots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(bundleFiles, out _);
                return roots.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Addressables script-spawn prefab resolver failed for GUID '{guid}': {ex.Message}");
                return null;
            }
        }

        return new SceneScriptSpawnAnalysisOptions
        {
            IncludeInactiveGameObjects = exportOpts.IncludeInactive,
            MaxSpawnedInstances = exportOpts.MaxScriptSpawnInstances,
            EnableMonoIlConfidenceBoost = managedDirs.Count > 0,
            AddressablesCatalogs = catalogs,
            ManagedAssemblySearchDirectories = managedDirs,
            AssemblyLoader = assemblyLoader,
            ExternalGameObjectResolver = ResolveExternalPrefabGameObject,
            AddressablesPrefabResolver = ResolveAddressablesPrefab
        };
    }

    private string? ResolveSceneDependencyDiskPath(string serializedFileName)
    {
        if (string.IsNullOrWhiteSpace(serializedFileName))
        {
            return null;
        }

        var direct = TryResolveExistingPath(serializedFileName);
        if (!string.IsNullOrEmpty(direct))
        {
            return direct;
        }

        if (lazySourcePathBySerializedFile.TryGetValue(serializedFileName, out var mapped))
        {
            direct = TryResolveExistingPath(mapped);
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }
        }

        var indexedPath = assetsManager.ProjectIndex
            .GetHandlesForFile(serializedFileName)
            .Select(ResolveLazyHandleSourcePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (!string.IsNullOrEmpty(indexedPath))
        {
            return indexedPath;
        }

        return FindSourceFileByNameInProjectRoot(serializedFileName);
    }

    private List<SerializedFile> EnsureSceneCandidateSerializedFilesLoaded(SceneCandidateBinding binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var loaded = FindMatchingLoadedSerializedFiles(binding);
        if (loaded.Count > 0 && loaded.Any(SerializedFileContainsGameObjects))
        {
            return loaded;
        }

        var srcPath = TryResolveExistingPath(binding.Candidate.OriginalPath)
            ?? ResolveSceneDependencyDiskPath(binding.Candidate.SerializedFileName);

        if (!string.IsNullOrWhiteSpace(srcPath) && File.Exists(srcPath))
        {
            RemovePendingFileFromProgressiveQueue(srcPath);
            assetsManager.LoadFilesForPreview(srcPath);
            if (!string.IsNullOrWhiteSpace(binding.Candidate.SerializedFileName))
            {
                assetsManager.WaitForAssetsFileLoaded(binding.Candidate.SerializedFileName, 5000);
            }

            loaded = FindMatchingLoadedSerializedFiles(binding, resolvedDiskPath: srcPath);
        }

        return loaded;
    }

    private static bool SerializedFileContainsGameObjects(SerializedFile? sf)
    {
        if (sf == null)
        {
            return false;
        }

        if (sf.m_Objects != null)
        {
            for (var i = 0; i < sf.m_Objects.Count; i++)
            {
                var info = sf.m_Objects[i];
                if (info != null && info.classID == (int)ClassIDType.GameObject)
                {
                    return true;
                }
            }
        }

        return sf.Objects != null && sf.Objects.Any(static o => o is GameObject);
    }

    private static bool SerializedFileContainsRenderSettings(SerializedFile? sf)
    {
        if (sf?.m_Objects == null)
        {
            return false;
        }

        for (var i = 0; i < sf.m_Objects.Count; i++)
        {
            var info = sf.m_Objects[i];
            if (info != null && info.classID == (int)ClassIDType.RenderSettings)
            {
                return true;
            }
        }

        return false;
    }

    private List<SerializedFile> FindMatchingLoadedSerializedFiles(SceneCandidateBinding binding, string? resolvedDiskPath = null)
    {
        if (binding.LoadedSerializedFile?.reader != null)
        {
            return new List<SerializedFile> { binding.LoadedSerializedFile };
        }

        var sourcePath = !string.IsNullOrWhiteSpace(resolvedDiskPath) ? resolvedDiskPath : binding.Candidate.OriginalPath;
        var sfName = binding.Candidate.SerializedFileName;
        var snapshot = GetLoadedFilesSnapshot();

        SerializedFile? TryMatchExactSceneCabFromAssetBundle(IReadOnlyList<SerializedFile> bundleFiles)
        {
            if (bundleFiles.Count <= 1)
            {
                return null;
            }

            var wantedPath = binding.Candidate.ScenePath?.Trim().Replace('\\', '/') ?? string.Empty;
            var wantedKey = binding.Candidate.ContainerKey?.Trim().Replace('\\', '/') ?? string.Empty;
            var wantedLeaf = BuildSettings.ExtractSceneDisplayName(!string.IsNullOrEmpty(wantedPath) ? wantedPath : wantedKey);
            if (wantedPath.Length == 0 && wantedKey.Length == 0 && string.IsNullOrWhiteSpace(wantedLeaf))
            {
                return null;
            }

            var abTypeSet = new HashSet<ClassIDType> { ClassIDType.AssetBundle };
            for (var i = 0; i < bundleFiles.Count; i++)
            {
                var bsf = bundleFiles[i];
                if (bsf?.reader == null)
                {
                    continue;
                }

                SceneGlbExportService.MaterializeSceneObjectsForFile(assetsManager, bsf, abTypeSet);
                AssetStudio.Object[] objs;
                lock (bsf)
                {
                    objs = bsf.Objects != null ? bsf.Objects.ToArray() : Array.Empty<AssetStudio.Object>();
                }

                for (var o = 0; o < objs.Length; o++)
                {
                    if (objs[o] is not AssetBundle ab || ab.m_SceneHashes == null || ab.m_SceneHashes.Length == 0)
                    {
                        continue;
                    }

                    for (var h = 0; h < ab.m_SceneHashes.Length; h++)
                    {
                        var hashKey = ab.m_SceneHashes[h].Key?.Trim().Replace('\\', '/') ?? string.Empty;
                        var cabName = ab.m_SceneHashes[h].Value;
                        if (string.IsNullOrWhiteSpace(hashKey) || string.IsNullOrWhiteSpace(cabName))
                        {
                            continue;
                        }

                        var hashLeaf = BuildSettings.ExtractSceneDisplayName(hashKey);
                        if ((wantedPath.Length > 0 && string.Equals(hashKey, wantedPath, StringComparison.OrdinalIgnoreCase))
                            || (wantedKey.Length > 0 && string.Equals(hashKey, wantedKey, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrWhiteSpace(wantedLeaf) && string.Equals(hashLeaf, wantedLeaf, StringComparison.OrdinalIgnoreCase)))
                        {
                            var exactCab = bundleFiles.FirstOrDefault(sf =>
                                sf?.reader != null && string.Equals(sf.fileName, cabName, StringComparison.OrdinalIgnoreCase));
                            if (exactCab != null)
                            {
                                return exactCab;
                            }
                        }
                    }
                }
            }

            return null;
        }

        List<SerializedFile> PreferSceneFilesInSameBundle(SerializedFile matched)
        {
            var bundlePath = !string.IsNullOrWhiteSpace(sourcePath)
                ? sourcePath
                : (!string.IsNullOrWhiteSpace(matched.originalPath) ? matched.originalPath : matched.fullName);

            if (!string.IsNullOrWhiteSpace(bundlePath)
                && (ProjectLoadingService.IsSharedAssetsCompanionName(matched.fileName) || !SerializedFileContainsGameObjects(matched)))
            {
                var allInBundle = snapshot
                    .Where(sf => sf?.reader != null
                        && (IsSameLazySource(sf.originalPath, bundlePath) || IsSameLazySource(sf.fullName, bundlePath)))
                    .ToList();

                var exactFromHashes = TryMatchExactSceneCabFromAssetBundle(allInBundle);
                if (exactFromHashes != null)
                {
                    return new List<SerializedFile> { exactFromHashes };
                }

                var siblings = allInBundle.Where(sf => !ReferenceEquals(sf, matched)).ToList();

                var renderSettingsSiblings = siblings
                    .Where(static sf => SerializedFileContainsRenderSettings(sf) && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                    .ToList();
                if (renderSettingsSiblings.Count > 0)
                {
                    return renderSettingsSiblings;
                }

                var goSiblings = siblings
                    .Where(static sf => SerializedFileContainsGameObjects(sf) && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                    .ToList();
                if (goSiblings.Count > 0)
                {
                    return goSiblings;
                }
            }

            return new List<SerializedFile> { matched };
        }

        if (!string.IsNullOrWhiteSpace(sfName))
        {
            var exactMatch = snapshot.FirstOrDefault(sf =>
                sf?.reader != null
                && string.Equals(sf.fileName, sfName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(sourcePath)
                    || IsSameLazySource(sf.originalPath, sourcePath)
                    || IsSameLazySource(sf.fullName, sourcePath)));
            if (exactMatch != null)
            {
                return PreferSceneFilesInSameBundle(exactMatch);
            }

            var nameMatch = snapshot.FirstOrDefault(sf =>
                sf?.reader != null
                && string.Equals(sf.fileName, sfName, StringComparison.OrdinalIgnoreCase));
            if (nameMatch != null)
            {
                return PreferSceneFilesInSameBundle(nameMatch);
            }
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var byPath = snapshot
                .Where(sf => sf?.reader != null
                    && (IsSameLazySource(sf.originalPath, sourcePath)
                        || IsSameLazySource(sf.fullName, sourcePath)))
                .ToList();

            var exactFromHashes = TryMatchExactSceneCabFromAssetBundle(byPath);
            if (exactFromHashes != null)
            {
                return new List<SerializedFile> { exactFromHashes };
            }

            var withRenderSettings = byPath
                .Where(static sf => SerializedFileContainsRenderSettings(sf) && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                .ToList();
            if (withRenderSettings.Count > 0)
            {
                return withRenderSettings;
            }

            var withGameObjects = byPath
                .Where(static sf => SerializedFileContainsGameObjects(sf) && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                .ToList();
            if (withGameObjects.Count > 0)
            {
                return withGameObjects;
            }

            return byPath;
        }

        return new List<SerializedFile>();
    }

    private List<string> DiscoverStagePresetsForCompanionFiles(
        string? sceneName,
        string? scenePath,
        IReadOnlyList<(string SerializedFileName, string OriginalPath)> companionFiles)
    {
        var presets = new List<string>();
        var stageStem = ProjectLoadingService.ExtractSceneStageStem(sceneName, scenePath);
        if (stageStem.Length < 6 || companionFiles == null || companionFiles.Count == 0)
        {
            return presets;
        }

        var prefix = stageStem + "_";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < companionFiles.Count; i++)
        {
            var sfName = companionFiles[i].SerializedFileName;
            if (string.IsNullOrWhiteSpace(sfName))
            {
                continue;
            }

            foreach (var handle in assetsManager.ProjectIndex.GetHandlesForFile(sfName))
            {
                if (handle == null || handle.Type != ClassIDType.MonoBehaviour || string.IsNullOrWhiteSpace(handle.Name))
                {
                    continue;
                }

                if (!handle.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = handle.Name.Substring(prefix.Length).Trim();
                if (!string.IsNullOrEmpty(suffix) && seen.Add(suffix))
                {
                    presets.Add(suffix);
                }
            }
        }

        presets.Sort(static (a, b) =>
        {
            var cmp = SceneGlbExportService.ScoreStagePresetName(b).CompareTo(SceneGlbExportService.ScoreStagePresetName(a));
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return presets;
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
            LodMode = _lastSceneExportOptions.LodMode,
            ExportCamerasAndLights = _lastSceneExportOptions.ExportCamerasAndLights,
            HideAuxiliaryAndShadowOnlyRenderers = _lastSceneExportOptions.HideAuxiliaryAndShadowOnlyRenderers,
            PreferredStagePresetName = _lastSceneExportOptions.PreferredStagePresetName,
            ResolveStaticScriptSpawns = false,
            WriteManifest = _lastSceneExportOptions.WriteManifest,
            IncludeAnimations = exportOptions.ExportAnimations,
            ExportSkins = exportOptions.ExportSkins,
            ExportBlendShape = exportOptions.ExportBlendShape,
            EnableInstancing = true,
            ScaleFactor = (float)exportOptions.ScaleFactor,
            ImageFormat = exportOptions.ConvertTextureFormat,
            Binary = binary
        };
    }

    private void MaterializeSceneObjectsForSerializedFile(
        SerializedFile sourceFile,
        HashSet<ClassIDType> objectTypes,
        CancellationToken cancellationToken)
    {
        if (sourceFile == null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (assetsManager.LazyLoading)
        {
            MaterializeReferenceObjects(sourceFile, objectTypes, diagnostics: null, cancellationToken);
        }

        SceneGlbExportService.MaterializeSceneObjectsForFile(assetsManager, sourceFile, objectTypes, cancellationToken);
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
}
