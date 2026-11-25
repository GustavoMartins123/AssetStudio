using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia.Services
{
    public sealed class LazySceneExpansionResult
    {
        public bool Success { get; init; }
        public IReadOnlyList<GameObjectNode> RootChildren { get; init; } = Array.Empty<GameObjectNode>();
        public IReadOnlyList<SerializedFile> LoadedSceneFiles { get; init; } = Array.Empty<SerializedFile>();
        public int TotalGameObjectCount { get; init; }
        public int RendererCount { get; init; }
        public int CompanionAttachmentCount { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
    }

    public sealed class ContainerSearchResult
    {
        public IReadOnlyList<ContainerTreeNode> RootNodes { get; init; } = Array.Empty<ContainerTreeNode>();
        public int TotalMatchedAssets { get; init; }
        public int DisplayedMatchedAssets { get; init; }
        public bool WasTruncated => TotalMatchedAssets > DisplayedMatchedAssets;
    }

    public static class SceneHierarchyBuilderService
    {
        public const string LazyExpandPlaceholderText = "Expand to load scene hierarchy...";
        public const string LazyLoadingPlaceholderText = "Loading scene hierarchy...";
        public const int DefaultContainerPageSize = 150;
        public const int DefaultContainerSearchCap = 1000;

        public static HashSet<ClassIDType> GetSceneHierarchyObjectTypes()
        {
            return new HashSet<ClassIDType>
            {
                ClassIDType.GameObject,
                ClassIDType.Transform,
                ClassIDType.RectTransform,
                ClassIDType.MeshFilter,
                ClassIDType.MeshRenderer,
                ClassIDType.SkinnedMeshRenderer,
                ClassIDType.Animator,
                ClassIDType.Animation,
                ClassIDType.LODGroup,
                ClassIDType.Camera,
                ClassIDType.Light,
                ClassIDType.MonoBehaviour,
                ClassIDType.MonoScript
            };
        }

        public static List<LazySceneDescriptor> BuildLazySceneDescriptors(
            IReadOnlyList<IndexedSceneRecord>? discoveredScenes,
            IReadOnlyList<IndexedSceneRecord>? sqliteIndexedScenes = null,
            IReadOnlyDictionary<string, (int GameObjectCount, int RendererCount)>? sceneFileStats = null,
            IReadOnlyDictionary<string, List<(string SerializedFileName, string OriginalPath)>>? companionStageFilesBySceneSf = null)
        {
            var descriptors = new List<LazySceneDescriptor>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAddRecord(IndexedSceneRecord? rec, int index, string idPrefix)
            {
                if (rec == null)
                {
                    return;
                }

                var scenePath = rec.ScenePath?.Trim() ?? string.Empty;
                var sfName = rec.EntrySerializedFileName?.Trim() ?? string.Empty;
                var dedupKey = !string.IsNullOrWhiteSpace(scenePath)
                    ? $"path:{scenePath}"
                    : (!string.IsNullOrWhiteSpace(sfName)
                        ? $"file:{sfName}"
                        : $"id:{idPrefix}:{index}:{rec.Name}");

                if (!seenKeys.Add(dedupKey))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(sfName))
                {
                    seenKeys.Add($"file:{sfName}");
                }

                var goCount = -1;
                var rendererCount = -1;
                if (!string.IsNullOrWhiteSpace(sfName)
                    && sceneFileStats != null
                    && sceneFileStats.TryGetValue(sfName, out var stats))
                {
                    goCount = stats.GameObjectCount;
                    rendererCount = stats.RendererCount;
                }

                IReadOnlyList<(string SerializedFileName, string OriginalPath)> companionFiles =
                    Array.Empty<(string SerializedFileName, string OriginalPath)>();
                if (!string.IsNullOrWhiteSpace(sfName)
                    && companionStageFilesBySceneSf != null
                    && companionStageFilesBySceneSf.TryGetValue(sfName, out var foundCompanions)
                    && foundCompanions != null
                    && foundCompanions.Count > 0)
                {
                    companionFiles = foundCompanions.ToArray();
                }

                var displayName = !string.IsNullOrWhiteSpace(rec.Name)
                    ? rec.Name.Trim()
                    : (!string.IsNullOrWhiteSpace(sfName)
                        ? sfName
                        : ExtractFileName(rec.EntryOriginalPath));

                descriptors.Add(new LazySceneDescriptor
                {
                    Id = $"{idPrefix}:{index}:{rec.SourceKind}:{scenePath}:{sfName}",
                    Name = string.IsNullOrWhiteSpace(displayName) ? $"Scene_{index}" : displayName,
                    ScenePath = scenePath,
                    BuildIndex = rec.BuildIndex,
                    SourceKind = string.IsNullOrWhiteSpace(rec.SourceKind) ? "Indexed" : rec.SourceKind,
                    SerializedFileName = sfName,
                    OriginalPath = rec.EntryOriginalPath ?? string.Empty,
                    ContainerKey = rec.ContainerKey ?? string.Empty,
                    GameObjectCount = goCount,
                    RendererCount = rendererCount,
                    CompanionStageFiles = companionFiles
                });
            }

            if (discoveredScenes != null)
            {
                for (var i = 0; i < discoveredScenes.Count; i++)
                {
                    TryAddRecord(discoveredScenes[i], i, "discovered");
                }
            }

            if (sqliteIndexedScenes != null)
            {
                for (var i = 0; i < sqliteIndexedScenes.Count; i++)
                {
                    TryAddRecord(sqliteIndexedScenes[i], i, "sqlite");
                }
            }

            SortLazySceneDescriptors(descriptors);
            return descriptors;
        }

        public static void SortLazySceneDescriptors(List<LazySceneDescriptor> descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptors);

            descriptors.Sort(static (a, b) =>
            {
                // Real 3D scenes (RendererCount > 0) first, unknown (-1) next, 0-renderer bootstrap shells at bottom.
                static int RankRenderers(int rendererCount) => rendererCount > 0 ? 0 : (rendererCount < 0 ? 1 : 2);
                var rendRank = RankRenderers(a.RendererCount).CompareTo(RankRenderers(b.RendererCount));
                if (rendRank != 0) return rendRank;

                static int RankDebugScene(LazySceneDescriptor c)
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
        }

        public static List<GameObjectNode> BuildLazySceneRootNodes(
            IReadOnlyList<LazySceneDescriptor> descriptors,
            Action<GameObjectNode>? onExpanded = null)
        {
            ArgumentNullException.ThrowIfNull(descriptors);

            var roots = new List<GameObjectNode>(descriptors.Count);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (descriptor == null)
                {
                    continue;
                }

                var rootNode = new GameObjectNode
                {
                    Name = descriptor.Name,
                    Subtitle = descriptor.FormatStatsSubtitle(),
                    IsLazySceneRoot = true,
                    IsSceneLoaded = false,
                    LazyScene = descriptor
                };
                rootNode.EnableDynamicChildren();
                rootNode.AddChild(new GameObjectNode
                {
                    Name = LazyExpandPlaceholderText,
                    IsPlaceholder = true
                });

                if (onExpanded != null)
                {
                    rootNode.Expanded += onExpanded;
                }

                roots.Add(rootNode);
            }

            return roots;
        }

        public static LazySceneExpansionResult LoadLazySceneHierarchy(
            AssetsManager assetsManager,
            LazySceneDescriptor descriptor,
            AssemblyLoader? assemblyLoader = null,
            Func<string, string?>? resolveSourcePathByFileName = null,
            Action<string>? beforeLoadFile = null,
            CancellationToken cancellationToken = default,
            Func<MonoBehaviour, OrderedDictionary?>? customCompanionFieldProvider = null)
        {
            ArgumentNullException.ThrowIfNull(assetsManager);
            ArgumentNullException.ThrowIfNull(descriptor);

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var lruSuspension = assetsManager.LazyLoading ? assetsManager.LruCache.SuspendEviction() : null;

                var sceneFiles = EnsureSceneSerializedFilesLoaded(
                    assetsManager,
                    descriptor,
                    resolveSourcePathByFileName,
                    beforeLoadFile,
                    cancellationToken);

                if (sceneFiles.Count == 0)
                {
                    return new LazySceneExpansionResult
                    {
                        Success = false,
                        ErrorMessage = $"Scene source file could not be loaded for '{descriptor.Name}'."
                    };
                }

                var hierarchyTypes = GetSceneHierarchyObjectTypes();
                for (var i = 0; i < sceneFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SceneGlbExportService.MaterializeSceneObjectsForFile(
                        assetsManager,
                        sceneFiles[i],
                        hierarchyTypes,
                        cancellationToken);
                }

                var rootGameObjects = CollectDistinctRootsFromFiles(sceneFiles);

                // Sibling CAB fallback when a metadata-only CAB (e.g. *.sharedAssets) has no GameObjects of its own.
                if (rootGameObjects.Count == 0 && !string.IsNullOrWhiteSpace(descriptor.OriginalPath))
                {
                    var seenSet = new HashSet<SerializedFile>(sceneFiles);
                    var siblingFiles = SnapshotAssetsFiles(assetsManager)
                        .Where(sf => sf?.reader != null
                            && !seenSet.Contains(sf)
                            && (IsSamePath(sf.originalPath, descriptor.OriginalPath)
                                || IsSamePath(sf.fullName, descriptor.OriginalPath)))
                        .ToList();

                    foreach (var sibling in siblingFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SceneGlbExportService.MaterializeSceneObjectsForFile(
                            assetsManager,
                            sibling,
                            hierarchyTypes,
                            cancellationToken);
                    }

                    if (siblingFiles.Count > 0)
                    {
                        rootGameObjects = CollectDistinctRootsFromFiles(siblingFiles);
                        if (rootGameObjects.Count > 0)
                        {
                            sceneFiles = siblingFiles;
                        }
                    }
                }

                if (rootGameObjects.Count == 0)
                {
                    return new LazySceneExpansionResult
                    {
                        Success = true,
                        LoadedSceneFiles = sceneFiles,
                        RootChildren = Array.Empty<GameObjectNode>(),
                        ErrorMessage = "No GameObjects found in scene."
                    };
                }

                // Optional companion stage preset bundle resolution (same pipeline as SceneGlbExportService).
                List<SceneScriptSpawnAttachment>? companionAttachments = null;
                if (descriptor.CompanionStageFiles is { Count: > 0 })
                {
                    var stageStem = ProjectLoadingService.ExtractSceneStageStem(descriptor.Name, descriptor.ScenePath);
                    if (stageStem.Length >= 6)
                    {
                        var loadedCompanionFiles = LoadCompanionStageFiles(
                            assetsManager,
                            descriptor.CompanionStageFiles,
                            hierarchyTypes,
                            resolveSourcePathByFileName,
                            beforeLoadFile,
                            cancellationToken);

                        if (loadedCompanionFiles.Count > 0)
                        {
                            var companionGroups = new List<(string StageStem, IReadOnlyList<SerializedFile> CompanionFiles)>
                            {
                                (stageStem, loadedCompanionFiles)
                            };

                            companionAttachments = SceneGlbExportService.ResolveCompanionStagePresetAttachments(
                                rootGameObjects,
                                companionGroups,
                                assemblyLoader,
                                cancellationToken,
                                customCompanionFieldProvider);
                        }
                    }
                }

                var builtTree = BuildGameObjectTreeNodes(
                    rootGameObjects,
                    companionAttachments,
                    out var totalGameObjects,
                    out var totalRenderers);

                return new LazySceneExpansionResult
                {
                    Success = true,
                    RootChildren = builtTree,
                    LoadedSceneFiles = sceneFiles,
                    TotalGameObjectCount = totalGameObjects,
                    RendererCount = totalRenderers,
                    CompanionAttachmentCount = companionAttachments?.Count ?? 0
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LazySceneExpansionResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to load scene: {ex.Message}"
                };
            }
        }

        public static List<GameObjectNode> BuildGameObjectTreeNodes(
            IReadOnlyList<GameObject> rootGameObjects,
            IReadOnlyList<SceneScriptSpawnAttachment>? companionAttachments,
            out int totalGameObjectCount,
            out int rendererCount)
        {
            totalGameObjectCount = 0;
            rendererCount = 0;

            if (rootGameObjects == null || rootGameObjects.Count == 0)
            {
                return new List<GameObjectNode>();
            }

            var localGameObjectCount = 0;
            var localRendererCount = 0;

            var nodeByGameObject = new Dictionary<GameObject, GameObjectNode>();
            var visited = new HashSet<GameObject>();
            var rootNodes = new List<GameObjectNode>(rootGameObjects.Count);

            GameObjectNode BuildSubtreeIterative(GameObject rootGo, string? rootSubtitle = null)
            {
                var rootNode = CreateNodeForGameObject(rootGo, rootSubtitle);
                var stack = new Stack<(GameObject Go, GameObjectNode Node)>();
                stack.Push((rootGo, rootNode));

                while (stack.Count > 0)
                {
                    var (go, node) = stack.Pop();
                    SceneGlbExportService.EnsureGameObjectComponentsLinked(go);

                    if (go.m_MeshRenderer != null || go.m_SkinnedMeshRenderer != null)
                    {
                        localRendererCount++;
                    }

                    var childGameObjects = CollectOrderedChildGameObjects(go);
                    if (childGameObjects.Count == 0)
                    {
                        continue;
                    }

                    var createdChildren = new List<(GameObject ChildGo, GameObjectNode ChildNode)>(childGameObjects.Count);
                    for (var i = 0; i < childGameObjects.Count; i++)
                    {
                        var childGo = childGameObjects[i];
                        if (childGo == null || !visited.Add(childGo))
                        {
                            continue;
                        }

                        var childNode = CreateNodeForGameObject(childGo);
                        node.AddChild(childNode);
                        createdChildren.Add((childGo, childNode));
                    }

                    for (var i = createdChildren.Count - 1; i >= 0; i--)
                    {
                        stack.Push(createdChildren[i]);
                    }
                }

                return rootNode;
            }

            GameObjectNode CreateNodeForGameObject(GameObject go, string? customSubtitle = null)
            {
                localGameObjectCount++;
                var displayName = !string.IsNullOrWhiteSpace(go.m_Name)
                    ? go.m_Name
                    : string.Create(CultureInfo.InvariantCulture, $"GameObject ({go.m_PathID})");
                var subtitle = customSubtitle ?? (!go.m_IsActive ? "[Inactive]" : string.Empty);
                var node = new GameObjectNode
                {
                    Name = displayName,
                    GameObject = go,
                    Subtitle = subtitle
                };
                nodeByGameObject.TryAdd(go, node);
                return node;
            }

            for (var i = 0; i < rootGameObjects.Count; i++)
            {
                var rootGo = rootGameObjects[i];
                if (rootGo == null || !visited.Add(rootGo))
                {
                    continue;
                }

                rootNodes.Add(BuildSubtreeIterative(rootGo));
            }

            // Attach resolved companion stage preset subtrees under their anchor Transform node (or a fallback folder).
            if (companionAttachments is { Count: > 0 })
            {
                GameObjectNode? fallbackCompanionFolder = null;
                for (var i = 0; i < companionAttachments.Count; i++)
                {
                    var attachment = companionAttachments[i];
                    var prefabRoot = attachment?.PrefabRoot;
                    if (prefabRoot == null || !visited.Add(prefabRoot))
                    {
                        continue;
                    }

                    GameObjectNode? anchorNode = null;
                    if (attachment!.AnchorTransform?.m_GameObject != null
                        && !attachment.AnchorTransform.m_GameObject.IsNull
                        && attachment.AnchorTransform.m_GameObject.TryGet(out var anchorGo)
                        && anchorGo != null)
                    {
                        nodeByGameObject.TryGetValue(anchorGo, out anchorNode);
                    }

                    var presetLabel = !string.IsNullOrWhiteSpace(attachment.FieldPath)
                        ? $"[Preset: {attachment.FieldPath}]"
                        : "[Stage Preset]";
                    var presetNode = BuildSubtreeIterative(prefabRoot, presetLabel);

                    if (anchorNode != null)
                    {
                        anchorNode.AddChild(presetNode);
                    }
                    else
                    {
                        fallbackCompanionFolder ??= new GameObjectNode
                        {
                            Name = "Stage Presets (Companion)",
                            Subtitle = "(companion bundle)"
                        };
                        fallbackCompanionFolder.AddChild(presetNode);
                    }
                }

                if (fallbackCompanionFolder != null && fallbackCompanionFolder.ChildCount > 0)
                {
                    rootNodes.Add(fallbackCompanionFolder);
                }
            }

            totalGameObjectCount = localGameObjectCount;
            rendererCount = localRendererCount;
            return rootNodes;
        }

        private static List<GameObject> CollectOrderedChildGameObjects(GameObject parentGo)
        {
            var result = new List<GameObject>();
            var transform = parentGo.m_Transform;
            if (transform == null)
            {
                return result;
            }

            var seen = new HashSet<GameObject>();
            if (transform.m_Children is { Length: > 0 })
            {
                for (var i = 0; i < transform.m_Children.Length; i++)
                {
                    var childPtr = transform.m_Children[i];
                    if (childPtr == null || childPtr.IsNull)
                    {
                        continue;
                    }

                    if (!childPtr.TryGet(out var childTr) || childTr == null)
                    {
                        continue;
                    }

                    if (childTr.m_GameObject != null
                        && !childTr.m_GameObject.IsNull
                        && childTr.m_GameObject.TryGet(out var childGo)
                        && childGo != null
                        && !ReferenceEquals(childGo, parentGo)
                        && seen.Add(childGo))
                    {
                        result.Add(childGo);
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }

            // Fallback: handle synthetic or partially-linked files where only Transform.m_Father was populated.
            var sf = parentGo.assetsFile;
            if (sf?.Objects == null)
            {
                return result;
            }

            AssetStudio.Object[] objs;
            lock (sf)
            {
                objs = sf.Objects.ToArray();
            }

            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] is GameObject candidate
                    && !ReferenceEquals(candidate, parentGo)
                    && candidate.m_Transform?.m_Father != null
                    && !candidate.m_Transform.m_Father.IsNull
                    && candidate.m_Transform.m_Father.TryGet(out var fatherTr)
                    && ReferenceEquals(fatherTr, transform)
                    && seen.Add(candidate))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        private static List<SerializedFile> EnsureSceneSerializedFilesLoaded(
            AssetsManager assetsManager,
            LazySceneDescriptor descriptor,
            Func<string, string?>? resolveSourcePathByFileName,
            Action<string>? beforeLoadFile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var loaded = FindMatchingLoadedSerializedFiles(assetsManager, descriptor, resolvedDiskPath: null);
            if (loaded.Count > 0 && loaded.Any(SerializedFileContainsGameObjects))
            {
                return loaded;
            }

            var diskPath = ResolveExistingFilePath(descriptor.OriginalPath)
                ?? (resolveSourcePathByFileName != null && !string.IsNullOrWhiteSpace(descriptor.SerializedFileName)
                    ? ResolveExistingFilePath(resolveSourcePathByFileName(descriptor.SerializedFileName))
                    : null)
                ?? ResolvePathFromProjectIndex(assetsManager, descriptor.SerializedFileName);

            if (!string.IsNullOrWhiteSpace(diskPath) && File.Exists(diskPath))
            {
                beforeLoadFile?.Invoke(diskPath);
                if (assetsManager.LazyLoading)
                {
                    assetsManager.LoadFilesForPreview(diskPath);
                }
                else
                {
                    assetsManager.LoadFiles(diskPath);
                }

                if (!string.IsNullOrWhiteSpace(descriptor.SerializedFileName))
                {
                    assetsManager.WaitForAssetsFileLoaded(descriptor.SerializedFileName, 5000);
                }

                loaded = FindMatchingLoadedSerializedFiles(assetsManager, descriptor, resolvedDiskPath: diskPath);
            }

            return loaded;
        }

        private static List<SerializedFile> LoadCompanionStageFiles(
            AssetsManager assetsManager,
            IReadOnlyList<(string SerializedFileName, string OriginalPath)> companionSpecs,
            HashSet<ClassIDType> hierarchyTypes,
            Func<string, string?>? resolveSourcePathByFileName,
            Action<string>? beforeLoadFile,
            CancellationToken cancellationToken)
        {
            var loadedCompanions = new List<SerializedFile>();
            for (var i = 0; i < companionSpecs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (compSfName, compOrigPath) = companionSpecs[i];
                var compDiskPath = ResolveExistingFilePath(compOrigPath)
                    ?? (resolveSourcePathByFileName != null && !string.IsNullOrWhiteSpace(compSfName)
                        ? ResolveExistingFilePath(resolveSourcePathByFileName(compSfName))
                        : null)
                    ?? ResolvePathFromProjectIndex(assetsManager, compSfName);

                if (!string.IsNullOrWhiteSpace(compDiskPath) && File.Exists(compDiskPath))
                {
                    if (!assetsManager.TryFindSerializedFile(compSfName, null, out var existingComp) || existingComp?.reader == null)
                    {
                        beforeLoadFile?.Invoke(compDiskPath);
                        if (assetsManager.LazyLoading)
                        {
                            assetsManager.LoadFilesForPreview(compDiskPath);
                        }
                        else
                        {
                            assetsManager.LoadFiles(compDiskPath);
                        }

                        if (!string.IsNullOrWhiteSpace(compSfName))
                        {
                            assetsManager.WaitForAssetsFileLoaded(compSfName, 5000);
                        }
                    }
                }

                if (assetsManager.TryFindSerializedFile(compSfName, null, out var loadedComp)
                    && loadedComp?.reader != null
                    && !loadedCompanions.Contains(loadedComp))
                {
                    SceneGlbExportService.MaterializeSceneObjectsForFile(
                        assetsManager,
                        loadedComp,
                        hierarchyTypes,
                        cancellationToken);
                    loadedCompanions.Add(loadedComp);
                }
            }

            return loadedCompanions;
        }

        private static List<SerializedFile> FindMatchingLoadedSerializedFiles(
            AssetsManager assetsManager,
            LazySceneDescriptor descriptor,
            string? resolvedDiskPath)
        {
            var sourcePath = !string.IsNullOrWhiteSpace(resolvedDiskPath) ? resolvedDiskPath : descriptor.OriginalPath;
            var sfName = descriptor.SerializedFileName;
            var snapshot = SnapshotAssetsFiles(assetsManager);

            SerializedFile? TryMatchExactSceneCabFromAssetBundle(IReadOnlyList<SerializedFile> bundleFiles)
            {
                if (bundleFiles.Count <= 1)
                {
                    return null;
                }

                var wantedPath = descriptor.ScenePath?.Trim().Replace('\\', '/') ?? string.Empty;
                var wantedKey = descriptor.ContainerKey?.Trim().Replace('\\', '/') ?? string.Empty;
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
                    && (ProjectLoadingService.IsSharedAssetsCompanionName(matched.fileName)
                        || !SerializedFileContainsGameObjects(matched)))
                {
                    var allInBundle = snapshot
                        .Where(sf => sf?.reader != null
                            && (IsSamePath(sf.originalPath, bundlePath) || IsSamePath(sf.fullName, bundlePath)))
                        .ToList();

                    var exactFromHashes = TryMatchExactSceneCabFromAssetBundle(allInBundle);
                    if (exactFromHashes != null)
                    {
                        return new List<SerializedFile> { exactFromHashes };
                    }

                    var siblings = allInBundle.Where(sf => !ReferenceEquals(sf, matched)).ToList();
                    var rsSiblings = siblings
                        .Where(static sf => SerializedFileContainsRenderSettings(sf)
                            && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                        .ToList();
                    if (rsSiblings.Count > 0)
                    {
                        return rsSiblings;
                    }

                    var goSiblings = siblings
                        .Where(static sf => SerializedFileContainsGameObjects(sf)
                            && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
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
                        || IsSamePath(sf.originalPath, sourcePath)
                        || IsSamePath(sf.fullName, sourcePath)));
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
                        && (IsSamePath(sf.originalPath, sourcePath) || IsSamePath(sf.fullName, sourcePath)))
                    .ToList();

                var exactFromHashes = TryMatchExactSceneCabFromAssetBundle(byPath);
                if (exactFromHashes != null)
                {
                    return new List<SerializedFile> { exactFromHashes };
                }

                var withRs = byPath
                    .Where(static sf => SerializedFileContainsRenderSettings(sf)
                        && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                    .ToList();
                if (withRs.Count > 0)
                {
                    return withRs;
                }

                var withGo = byPath
                    .Where(static sf => SerializedFileContainsGameObjects(sf)
                        && !ProjectLoadingService.IsSharedAssetsCompanionName(sf.fileName))
                    .ToList();
                if (withGo.Count > 0)
                {
                    return withGo;
                }

                return byPath;
            }

            return new List<SerializedFile>();
        }

        private static List<GameObject> CollectDistinctRootsFromFiles(IReadOnlyList<SerializedFile> files)
        {
            var roots = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            for (var i = 0; i < files.Count; i++)
            {
                var fileRoots = SceneGlbExportService.CollectRootGameObjectsFromSerializedFile(files[i], includeInactive: true);
                for (var r = 0; r < fileRoots.Count; r++)
                {
                    var go = fileRoots[r];
                    if (go != null && seen.Add(go))
                    {
                        roots.Add(go);
                    }
                }
            }

            return roots;
        }

        private static SerializedFile[] SnapshotAssetsFiles(AssetsManager manager)
        {
            lock (manager.loadLock)
            {
                return manager.assetsFileList.ToArray();
            }
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

        private static string? ResolveExistingFilePath(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            try
            {
                return File.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolvePathFromProjectIndex(AssetsManager assetsManager, string? serializedFileName)
        {
            if (string.IsNullOrWhiteSpace(serializedFileName))
            {
                return null;
            }

            foreach (var handle in assetsManager.ProjectIndex.GetHandlesForFile(serializedFileName))
            {
                var existing = ResolveExistingFilePath(handle?.OriginalPath);
                if (!string.IsNullOrEmpty(existing))
                {
                    return existing;
                }
            }

            return null;
        }

        private static bool IsSamePath(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (string.Equals(a, b, comparison))
            {
                return true;
            }

            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Virtual Containers Tree Helpers (Zero Disk I/O, Bounded & Lazy!)
        // ------------------------------------------------------------------

        public static bool IsEmptyOrHashedContainer(string? container)
        {
            if (string.IsNullOrWhiteSpace(container))
            {
                return true;
            }

            var trimmed = container.AsSpan().Trim();
            while (trimmed.Length > 0 && (trimmed[0] == '/' || trimmed[0] == '\\'))
            {
                trimmed = trimmed.Slice(1);
            }
            while (trimmed.Length > 0 && (trimmed[^1] == '/' || trimmed[^1] == '\\'))
            {
                trimmed = trimmed.Slice(0, trimmed.Length - 1);
            }

            if (trimmed.IsEmpty)
            {
                return true;
            }

            var slashIndex = trimmed.IndexOfAny('/', '\\');
            if (slashIndex < 0)
            {
                return IsHashedSegment(trimmed);
            }

            // Multi-segment path: check if ALL segments are short hex shard directories or hashes (e.g. "ab/9facb8b5...").
            var remaining = trimmed;
            var segmentCount = 0;
            var lastSegmentHashed = false;
            var allNonLastAreShardsOrHashes = true;

            while (!remaining.IsEmpty)
            {
                var sep = remaining.IndexOfAny('/', '\\');
                ReadOnlySpan<char> seg;
                if (sep < 0)
                {
                    seg = remaining;
                    remaining = default;
                }
                else
                {
                    seg = remaining.Slice(0, sep);
                    remaining = remaining.Slice(sep + 1);
                }

                if (seg.IsEmpty)
                {
                    continue;
                }

                segmentCount++;
                if (remaining.IsEmpty)
                {
                    lastSegmentHashed = IsHashedSegment(seg);
                }
                else if (!(seg.Length <= 2 && IsAllHex(seg)) && !IsHashedSegment(seg))
                {
                    allNonLastAreShardsOrHashes = false;
                    break;
                }
            }

            return segmentCount > 0 && allNonLastAreShardsOrHashes && lastSegmentHashed;
        }

        private static bool IsHashedSegment(ReadOnlySpan<char> segment)
        {
            if (segment.IsEmpty)
            {
                return true;
            }

            var candidate = segment;
            if (candidate.StartsWith("cab-", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Slice(4);
            }
            else if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var hexPart = candidate.Slice(2);
                return hexPart.Length >= 8 && IsAllHex(hexPart);
            }

            var dot = candidate.LastIndexOf('.');
            if (dot > 0)
            {
                candidate = candidate.Slice(0, dot);
            }

            if (candidate.Length >= 16 && IsAllHex(candidate))
            {
                return true;
            }

            if (candidate.Length >= 8 && IsAllDigits(candidate))
            {
                return true;
            }

            if (candidate.Length == 36 && Guid.TryParseExact(candidate, "D", out _))
            {
                return true;
            }

            return false;
        }

        private static bool IsAllHex(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty)
            {
                return false;
            }

            for (var i = 0; i < span.Length; i++)
            {
                var c = span[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAllDigits(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty)
            {
                return false;
            }

            for (var i = 0; i < span.Length; i++)
            {
                var c = span[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        public static string ResolveContainerFallbackName(string? originalPath, string? serializedFileName)
        {
            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                var leaf = ExtractFileName(originalPath);
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    return leaf;
                }
            }

            if (!string.IsNullOrWhiteSpace(serializedFileName))
            {
                return serializedFileName.Trim();
            }

            return "(Unassigned)";
        }

        public static string NormalizeContainerPath(string? container)
        {
            if (string.IsNullOrWhiteSpace(container))
            {
                return string.Empty;
            }

            var raw = container.Trim();
            if (raw.IndexOf('\\') >= 0)
            {
                raw = raw.Replace('\\', '/');
            }

            while (raw.Contains("//", StringComparison.Ordinal))
            {
                raw = raw.Replace("//", "/", StringComparison.Ordinal);
            }

            return raw.Trim('/');
        }

        public static string ExtractFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var span = path.AsSpan().Trim();
            while (span.Length > 0 && (span[^1] == '/' || span[^1] == '\\'))
            {
                span = span.Slice(0, span.Length - 1);
            }

            var sep = span.LastIndexOfAny('/', '\\');
            return sep >= 0 ? span.Slice(sep + 1).ToString() : span.ToString();
        }
    }

    /// <summary>
    /// Compact, zero-disk-IO virtual folder index for Containers mode.
    /// Groups assets by normalized container path (split by '/') with bundle filename fallback when empty/hashed,
    /// and materializes TreeView nodes lazily &amp; bounded per level so 40k+ bundles open in &lt;150 ms.
    /// </summary>
    public sealed class VirtualContainerTreeIndex
    {
        private readonly AssetIndexArena? _arena;
        private readonly IReadOnlyList<AssetHandle>? _handles;
        private readonly IReadOnlyList<AssetItem>? _eagerItems;

        private readonly int[] _sourceIndices;
        private readonly string?[] _normalizedContainers; // null => fallback to bundle filename (single root folder)
        private readonly string[] _fallbackFolderNames;
        private readonly List<(string Key, int[] EntryIndices, bool IsFallback)> _sortedRootBuckets;
        private readonly int _pageSize;

        private VirtualContainerTreeIndex(
            AssetIndexArena? arena,
            IReadOnlyList<AssetHandle>? handles,
            IReadOnlyList<AssetItem>? eagerItems,
            int[] sourceIndices,
            string?[] normalizedContainers,
            string[] fallbackFolderNames,
            List<(string Key, int[] EntryIndices, bool IsFallback)> sortedRootBuckets,
            int pageSize)
        {
            _arena = arena;
            _handles = handles;
            _eagerItems = eagerItems;
            _sourceIndices = sourceIndices;
            _normalizedContainers = normalizedContainers;
            _fallbackFolderNames = fallbackFolderNames;
            _sortedRootBuckets = sortedRootBuckets;
            _pageSize = Math.Max(1, pageSize);
        }

        public int TotalAssetCount => _sourceIndices.Length;
        public int RootFolderCount => _sortedRootBuckets.Count;
        public int PageSize => _pageSize;

        public static VirtualContainerTreeIndex BuildFromProjectIndex(
            ProjectIndex? projectIndex,
            bool includeAllTypes = false,
            Func<ClassIDType, bool>? exportableFilter = null,
            int pageSize = SceneHierarchyBuilderService.DefaultContainerPageSize,
            CancellationToken cancellationToken = default)
        {
            if (projectIndex == null || projectIndex.Count == 0)
            {
                return CreateEmpty(pageSize);
            }

            var arena = projectIndex.Arena;
            var total = arena.Count;
            var filter = exportableFilter ?? ProjectLoadingService.IsLazyExportableType;

            var sourceIndicesList = new List<int>(total);
            var normalizedContainersList = new List<string?>(total);
            var fallbackNamesList = new List<string>(total);

            var fallbackByOriginId = new Dictionary<int, string>();
            var internedContainers = new Dictionary<string, string>(StringComparer.Ordinal);
            var rootGroups = new Dictionary<string, (List<int> Entries, bool IsFallback)>(StringComparer.OrdinalIgnoreCase);

            for (var idx = 0; idx < total; idx++)
            {
                if ((idx & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!includeAllTypes && !filter(arena.GetType(idx)))
                {
                    continue;
                }

                var origin = arena.GetOrigin(idx);
                if (!fallbackByOriginId.TryGetValue(origin.OriginId, out var fallbackName))
                {
                    fallbackName = SceneHierarchyBuilderService.ResolveContainerFallbackName(
                        origin.OriginalPath,
                        origin.SerializedFileName);
                    fallbackByOriginId[origin.OriginId] = fallbackName;
                }

                var rawContainer = arena.GetContainer(idx);
                string? normalizedPath = null;
                string topSegment;
                bool isFallback;

                if (SceneHierarchyBuilderService.IsEmptyOrHashedContainer(rawContainer))
                {
                    topSegment = fallbackName;
                    isFallback = true;
                }
                else
                {
                    var norm = SceneHierarchyBuilderService.NormalizeContainerPath(rawContainer);
                    if (!internedContainers.TryGetValue(norm, out var interned))
                    {
                        interned = norm;
                        internedContainers[norm] = interned;
                    }

                    normalizedPath = interned;
                    topSegment = ExtractFirstSegment(interned);
                    isFallback = false;
                }

                var entryIndex = sourceIndicesList.Count;
                sourceIndicesList.Add(idx);
                normalizedContainersList.Add(normalizedPath);
                fallbackNamesList.Add(fallbackName);

                AddToRootGroup(rootGroups, topSegment, entryIndex, isFallback);
            }

            return FinalizeIndex(
                arena,
                handles: null,
                eagerItems: null,
                sourceIndicesList,
                normalizedContainersList,
                fallbackNamesList,
                rootGroups,
                pageSize);
        }

        public static VirtualContainerTreeIndex BuildFromHandles(
            IReadOnlyList<AssetHandle>? handles,
            int pageSize = SceneHierarchyBuilderService.DefaultContainerPageSize,
            CancellationToken cancellationToken = default)
        {
            if (handles == null || handles.Count == 0)
            {
                return CreateEmpty(pageSize);
            }

            var total = handles.Count;
            var sourceIndicesList = new List<int>(total);
            var normalizedContainersList = new List<string?>(total);
            var fallbackNamesList = new List<string>(total);

            var internedContainers = new Dictionary<string, string>(StringComparer.Ordinal);
            var fallbackCache = new Dictionary<(string Orig, string Sf), string>();
            var rootGroups = new Dictionary<string, (List<int> Entries, bool IsFallback)>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < total; i++)
            {
                if ((i & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var handle = handles[i];
                if (handle == null)
                {
                    continue;
                }

                var orig = handle.OriginalPath ?? string.Empty;
                var sf = handle.SerializedFileName ?? string.Empty;
                if (!fallbackCache.TryGetValue((orig, sf), out var fallbackName))
                {
                    fallbackName = SceneHierarchyBuilderService.ResolveContainerFallbackName(orig, sf);
                    fallbackCache[(orig, sf)] = fallbackName;
                }

                var rawContainer = handle.Container;
                string? normalizedPath = null;
                string topSegment;
                bool isFallback;

                if (SceneHierarchyBuilderService.IsEmptyOrHashedContainer(rawContainer))
                {
                    topSegment = fallbackName;
                    isFallback = true;
                }
                else
                {
                    var norm = SceneHierarchyBuilderService.NormalizeContainerPath(rawContainer);
                    if (!internedContainers.TryGetValue(norm, out var interned))
                    {
                        interned = norm;
                        internedContainers[norm] = interned;
                    }

                    normalizedPath = interned;
                    topSegment = ExtractFirstSegment(interned);
                    isFallback = false;
                }

                var entryIndex = sourceIndicesList.Count;
                sourceIndicesList.Add(i);
                normalizedContainersList.Add(normalizedPath);
                fallbackNamesList.Add(fallbackName);

                AddToRootGroup(rootGroups, topSegment, entryIndex, isFallback);
            }

            return FinalizeIndex(
                arena: null,
                handles,
                eagerItems: null,
                sourceIndicesList,
                normalizedContainersList,
                fallbackNamesList,
                rootGroups,
                pageSize);
        }

        public static VirtualContainerTreeIndex BuildFromAssetItems(
            IReadOnlyList<AssetItem>? items,
            int pageSize = SceneHierarchyBuilderService.DefaultContainerPageSize,
            CancellationToken cancellationToken = default)
        {
            if (items == null || items.Count == 0)
            {
                return CreateEmpty(pageSize);
            }

            var total = items.Count;
            var sourceIndicesList = new List<int>(total);
            var normalizedContainersList = new List<string?>(total);
            var fallbackNamesList = new List<string>(total);

            var internedContainers = new Dictionary<string, string>(StringComparer.Ordinal);
            var fallbackCache = new Dictionary<(string Orig, string Sf), string>();
            var rootGroups = new Dictionary<string, (List<int> Entries, bool IsFallback)>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < total; i++)
            {
                if ((i & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                var orig = item.Handle?.OriginalPath ?? item.SourceFile?.originalPath ?? item.SourceFile?.fullName ?? string.Empty;
                var sf = item.Handle?.SerializedFileName ?? item.SourceFile?.fileName ?? string.Empty;
                if (!fallbackCache.TryGetValue((orig, sf), out var fallbackName))
                {
                    fallbackName = SceneHierarchyBuilderService.ResolveContainerFallbackName(orig, sf);
                    fallbackCache[(orig, sf)] = fallbackName;
                }

                var rawContainer = item.Container;
                string? normalizedPath = null;
                string topSegment;
                bool isFallback;

                if (SceneHierarchyBuilderService.IsEmptyOrHashedContainer(rawContainer))
                {
                    topSegment = fallbackName;
                    isFallback = true;
                }
                else
                {
                    var norm = SceneHierarchyBuilderService.NormalizeContainerPath(rawContainer);
                    if (!internedContainers.TryGetValue(norm, out var interned))
                    {
                        interned = norm;
                        internedContainers[norm] = interned;
                    }

                    normalizedPath = interned;
                    topSegment = ExtractFirstSegment(interned);
                    isFallback = false;
                }

                var entryIndex = sourceIndicesList.Count;
                sourceIndicesList.Add(i);
                normalizedContainersList.Add(normalizedPath);
                fallbackNamesList.Add(fallbackName);

                AddToRootGroup(rootGroups, topSegment, entryIndex, isFallback);
            }

            return FinalizeIndex(
                arena: null,
                handles: null,
                eagerItems: items,
                sourceIndicesList,
                normalizedContainersList,
                fallbackNamesList,
                rootGroups,
                pageSize);
        }

        public static VirtualContainerTreeIndex CreateEmpty(int pageSize = SceneHierarchyBuilderService.DefaultContainerPageSize)
        {
            return new VirtualContainerTreeIndex(
                arena: null,
                handles: null,
                eagerItems: null,
                Array.Empty<int>(),
                Array.Empty<string?>(),
                Array.Empty<string>(),
                new List<(string, int[], bool)>(),
                pageSize);
        }

        public List<ContainerTreeNode> CreateRootNodes(int startIndex = 0, Action<ContainerTreeNode>? onFolderExpanded = null)
        {
            if (_sortedRootBuckets.Count == 0 || startIndex >= _sortedRootBuckets.Count)
            {
                return new List<ContainerTreeNode>();
            }

            var safeStart = Math.Max(0, startIndex);
            var end = Math.Min(_sortedRootBuckets.Count, safeStart + _pageSize);
            var nodes = new List<ContainerTreeNode>(end - safeStart + 1);

            for (var i = safeStart; i < end; i++)
            {
                var (key, entries, isFallback) = _sortedRootBuckets[i];
                var node = CreateFolderNode(key, key, entries, depth: 0, isFallbackRoot: isFallback, onFolderExpanded);
                nodes.Add(node);
            }

            if (end < _sortedRootBuckets.Count)
            {
                var remaining = _sortedRootBuckets.Count - end;
                nodes.Add(new ContainerTreeNode(
                    name: string.Create(CultureInfo.InvariantCulture, $"Load more ({remaining:N0} remaining)..."),
                    fullPath: string.Empty,
                    isFolder: false,
                    totalAssetCount: remaining,
                    isLoadMoreNode: true,
                    depth: 0,
                    nextPageStartIndex: end));
            }

            return nodes;
        }

        public void MaterializeFolderChildren(ContainerTreeNode folderNode, Action<ContainerTreeNode>? onFolderExpanded = null)
        {
            ArgumentNullException.ThrowIfNull(folderNode);
            if (!folderNode.IsFolder || folderNode.ChildrenMaterialized)
            {
                return;
            }

            folderNode.ChildrenMaterialized = true;
            var entries = folderNode.EntryIndices;
            if (entries == null || entries.Length == 0)
            {
                folderNode.ReplaceChildren(Array.Empty<ContainerTreeNode>());
                return;
            }

            var children = BuildChildrenForEntries(
                entries,
                parentPath: folderNode.FullPath,
                nextDepth: folderNode.Depth + 1,
                isFallbackFolder: folderNode.IsFallbackRootFolder,
                startIndex: 0,
                onFolderExpanded);
            folderNode.ReplaceChildren(children);
        }

        public List<ContainerTreeNode> LoadNextFolderPage(
            ContainerTreeNode parentFolder,
            int startIndex,
            Action<ContainerTreeNode>? onFolderExpanded = null)
        {
            ArgumentNullException.ThrowIfNull(parentFolder);
            var entries = parentFolder.EntryIndices;
            if (entries == null || entries.Length == 0)
            {
                return new List<ContainerTreeNode>();
            }

            return BuildChildrenForEntries(
                entries,
                parentPath: parentFolder.FullPath,
                nextDepth: parentFolder.Depth + 1,
                isFallbackFolder: parentFolder.IsFallbackRootFolder,
                startIndex: startIndex,
                onFolderExpanded);
        }

        public ContainerSearchResult Search(
            string? query,
            int maxLeafMatches = SceneHierarchyBuilderService.DefaultContainerSearchCap,
            CancellationToken cancellationToken = default)
        {
            var needle = query?.Trim() ?? string.Empty;
            if (needle.Length == 0)
            {
                return new ContainerSearchResult
                {
                    RootNodes = CreateRootNodes(),
                    TotalMatchedAssets = TotalAssetCount,
                    DisplayedMatchedAssets = TotalAssetCount
                };
            }

            var cap = Math.Max(1, maxLeafMatches);
            var matchedEntryIndices = new List<int>(Math.Min(cap, 256));
            var totalMatched = 0;

            for (var e = 0; e < _sourceIndices.Length; e++)
            {
                if ((e & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!EntryMatchesQuery(e, needle))
                {
                    continue;
                }

                totalMatched++;
                if (matchedEntryIndices.Count < cap)
                {
                    matchedEntryIndices.Add(e);
                }
            }

            if (matchedEntryIndices.Count == 0)
            {
                return new ContainerSearchResult
                {
                    RootNodes = Array.Empty<ContainerTreeNode>(),
                    TotalMatchedAssets = 0,
                    DisplayedMatchedAssets = 0
                };
            }

            var autoExpand = matchedEntryIndices.Count <= 200;
            var treeRoots = BuildMaterializedTreeForSubset(matchedEntryIndices, autoExpand);
            return new ContainerSearchResult
            {
                RootNodes = treeRoots,
                TotalMatchedAssets = totalMatched,
                DisplayedMatchedAssets = matchedEntryIndices.Count
            };
        }

        private List<ContainerTreeNode> BuildChildrenForEntries(
            int[] entries,
            string parentPath,
            int nextDepth,
            bool isFallbackFolder,
            int startIndex,
            Action<ContainerTreeNode>? onFolderExpanded)
        {
            var subFolderGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var directLeafEntries = new List<(int EntryIndex, string DisplayName)>();

            if (isFallbackFolder)
            {
                for (var i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    directLeafEntries.Add((e, GetAssetDisplayName(e)));
                }
            }
            else
            {
                // Group entries by their next path segment at `nextDepth`.
                var segmentEntries = new Dictionary<string, (List<int> Entries, bool HasDeeper)>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    var path = _normalizedContainers[e];
                    if (string.IsNullOrEmpty(path))
                    {
                        directLeafEntries.Add((e, GetAssetDisplayName(e)));
                        continue;
                    }

                    if (!TryGetSegmentAtDepth(path, nextDepth, out var segment, out var hasMore))
                    {
                        directLeafEntries.Add((e, GetAssetDisplayName(e)));
                        continue;
                    }

                    if (!segmentEntries.TryGetValue(segment, out var bucket))
                    {
                        bucket = (new List<int>(4), hasMore);
                        segmentEntries[segment] = bucket;
                    }
                    else if (hasMore && !bucket.HasDeeper)
                    {
                        segmentEntries[segment] = (bucket.Entries, true);
                    }

                    bucket.Entries.Add(e);
                }

                foreach (var kvp in segmentEntries)
                {
                    var segName = kvp.Key;
                    var (bucketEntries, hasDeeper) = kvp.Value;

                    // Single leaf container file with an extension (e.g. "hero.png") renders directly as a leaf asset!
                    if (!hasDeeper && bucketEntries.Count == 1 && HasFileExtension(segName))
                    {
                        var e = bucketEntries[0];
                        var assetName = GetAssetDisplayName(e);
                        var stem = Path.GetFileNameWithoutExtension(segName);
                        var display = string.IsNullOrWhiteSpace(assetName)
                            || string.Equals(assetName, stem, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(assetName, segName, StringComparison.OrdinalIgnoreCase)
                            ? segName
                            : $"{segName} ({assetName})";
                        directLeafEntries.Add((e, display));
                    }
                    else
                    {
                        subFolderGroups[segName] = bucketEntries;
                    }
                }
            }

            var sortedFolders = subFolderGroups
                .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            directLeafEntries.Sort(static (a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            var totalChildCount = sortedFolders.Count + directLeafEntries.Count;
            var safeStart = Math.Max(0, startIndex);
            var end = Math.Min(totalChildCount, safeStart + _pageSize);
            var result = new List<ContainerTreeNode>(Math.Max(0, end - safeStart) + 1);

            for (var i = safeStart; i < end; i++)
            {
                if (i < sortedFolders.Count)
                {
                    var (segName, folderEntries) = (sortedFolders[i].Key, sortedFolders[i].Value);
                    var fullPath = string.IsNullOrEmpty(parentPath) ? segName : $"{parentPath}/{segName}";
                    var folderNode = CreateFolderNode(
                        segName,
                        fullPath,
                        folderEntries.ToArray(),
                        depth: nextDepth,
                        isFallbackRoot: false,
                        onFolderExpanded);
                    result.Add(folderNode);
                }
                else
                {
                    var leafTuple = directLeafEntries[i - sortedFolders.Count];
                    result.Add(CreateLeafNode(leafTuple.EntryIndex, leafTuple.DisplayName, parentPath, nextDepth));
                }
            }

            if (end < totalChildCount)
            {
                var remaining = totalChildCount - end;
                result.Add(new ContainerTreeNode(
                    name: string.Create(CultureInfo.InvariantCulture, $"Load more ({remaining:N0} remaining)..."),
                    fullPath: parentPath,
                    isFolder: false,
                    totalAssetCount: remaining,
                    isLoadMoreNode: true,
                    depth: nextDepth,
                    nextPageStartIndex: end));
            }

            return result;
        }

        private List<ContainerTreeNode> BuildMaterializedTreeForSubset(List<int> subsetEntries, bool autoExpand)
        {
            var rootGroups = new Dictionary<string, (List<int> Entries, bool IsFallback)>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < subsetEntries.Count; i++)
            {
                var e = subsetEntries[i];
                var path = _normalizedContainers[e];
                if (string.IsNullOrEmpty(path))
                {
                    AddToRootGroup(rootGroups, _fallbackFolderNames[e], e, isFallback: true);
                }
                else
                {
                    AddToRootGroup(rootGroups, ExtractFirstSegment(path), e, isFallback: false);
                }
            }

            var sortedRoots = rootGroups
                .OrderBy(static kv => kv.Value.IsFallback ? 1 : 0)
                .ThenBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var roots = new List<ContainerTreeNode>(sortedRoots.Count);
            for (var i = 0; i < sortedRoots.Count; i++)
            {
                var seg = sortedRoots[i].Key;
                var (entries, isFallback) = sortedRoots[i].Value;
                var rootNode = CreateFolderNode(seg, seg, entries.ToArray(), depth: 0, isFallbackRoot: isFallback, onFolderExpanded: null);
                MaterializeSubtreeRecursive(rootNode, autoExpand, maxDepth: 16);
                roots.Add(rootNode);
            }

            return roots;
        }

        private void MaterializeSubtreeRecursive(ContainerTreeNode folderNode, bool autoExpand, int maxDepth)
        {
            var stack = new Stack<(ContainerTreeNode Node, int RemainingDepth)>();
            stack.Push((folderNode, maxDepth));

            while (stack.Count > 0)
            {
                var (node, remainingDepth) = stack.Pop();
                if (!node.IsFolder)
                {
                    continue;
                }

                MaterializeFolderChildren(node);
                if (autoExpand)
                {
                    node.IsExpanded = true;
                }

                if (remainingDepth <= 0)
                {
                    continue;
                }

                for (var i = node.Children.Count - 1; i >= 0; i--)
                {
                    var child = node.Children[i];
                    if (child.IsFolder)
                    {
                        stack.Push((child, remainingDepth - 1));
                    }
                }
            }
        }

        private ContainerTreeNode CreateFolderNode(
            string name,
            string fullPath,
            int[] entries,
            int depth,
            bool isFallbackRoot,
            Action<ContainerTreeNode>? onFolderExpanded)
        {
            var countLabel = entries.Length == 1
                ? "(1 asset)"
                : string.Create(CultureInfo.InvariantCulture, $"({entries.Length:N0} assets)");
            var folderNode = new ContainerTreeNode(
                name: name,
                fullPath: fullPath,
                isFolder: true,
                totalAssetCount: entries.Length,
                subtitle: countLabel,
                depth: depth)
            {
                EntryIndices = entries,
                IsFallbackRootFolder = isFallbackRoot
            };

            // Add one lightweight placeholder so the TreeView displays the expander arrow immediately.
            var placeholder = new ContainerTreeNode(
                name: "Loading...",
                fullPath: fullPath,
                isFolder: false,
                isPlaceholder: true,
                depth: depth + 1);
            folderNode.ReplaceChildren(new[] { placeholder });

            if (onFolderExpanded != null)
            {
                folderNode.Expanded += onFolderExpanded;
            }

            return folderNode;
        }

        private ContainerTreeNode CreateLeafNode(int entryIndex, string displayName, string parentPath, int depth)
        {
            var srcIdx = _sourceIndices[entryIndex];
            var normalizedContainer = _normalizedContainers[entryIndex];
            var fullPath = !string.IsNullOrEmpty(normalizedContainer)
                ? normalizedContainer
                : (string.IsNullOrEmpty(parentPath) ? displayName : $"{parentPath}/{displayName}");

            if (_arena != null)
            {
                var type = _arena.GetType(srcIdx);
                var pathId = _arena.GetPathId(srcIdx);
                var sfName = _arena.GetSerializedFileName(srcIdx);
                var uniqueId = _arena.GetUniqueId(srcIdx);
                var typeName = AssetItem.GetCachedTypeString(type);
                return new ContainerTreeNode(
                    name: displayName,
                    fullPath: fullPath,
                    isFolder: false,
                    totalAssetCount: 1,
                    subtitle: string.Create(CultureInfo.InvariantCulture, $"PathID {pathId}"),
                    typeBadge: typeName,
                    arenaIndex: srcIdx,
                    assetType: type,
                    pathId: pathId,
                    serializedFileName: sfName,
                    uniqueId: uniqueId,
                    depth: depth);
            }

            if (_handles != null)
            {
                var handle = _handles[srcIdx];
                var typeName = AssetItem.GetCachedTypeString(handle.Type);
                return new ContainerTreeNode(
                    name: displayName,
                    fullPath: fullPath,
                    isFolder: false,
                    totalAssetCount: 1,
                    subtitle: string.Create(CultureInfo.InvariantCulture, $"PathID {handle.PathID}"),
                    typeBadge: typeName,
                    arenaIndex: handle.ArenaIndex,
                    handle: handle,
                    assetType: handle.Type,
                    pathId: handle.PathID,
                    serializedFileName: handle.SerializedFileName,
                    uniqueId: handle.UniqueID,
                    depth: depth);
            }

            var item = _eagerItems![srcIdx];
            var eagerTypeName = AssetItem.GetCachedTypeString(item.Type);
            return new ContainerTreeNode(
                name: displayName,
                fullPath: fullPath,
                isFolder: false,
                totalAssetCount: 1,
                subtitle: string.Create(CultureInfo.InvariantCulture, $"PathID {item.PathID}"),
                typeBadge: eagerTypeName,
                eagerAssetItem: item,
                assetType: item.Type,
                pathId: item.PathID,
                serializedFileName: item.SourceFile?.fileName ?? string.Empty,
                uniqueId: item.Handle?.UniqueID ?? item.UniqueID,
                depth: depth);
        }

        private string GetAssetDisplayName(int entryIndex)
        {
            var srcIdx = _sourceIndices[entryIndex];
            string rawName;
            ClassIDType type;
            long pathId;

            if (_arena != null)
            {
                rawName = _arena.GetName(srcIdx);
                type = _arena.GetType(srcIdx);
                pathId = _arena.GetPathId(srcIdx);
            }
            else if (_handles != null)
            {
                var h = _handles[srcIdx];
                rawName = h.Name ?? string.Empty;
                type = h.Type;
                pathId = h.PathID;
            }
            else
            {
                var item = _eagerItems![srcIdx];
                rawName = item.Name ?? string.Empty;
                type = item.Type;
                pathId = item.PathID;
            }

            return !string.IsNullOrWhiteSpace(rawName)
                ? rawName
                : string.Create(CultureInfo.InvariantCulture, $"{AssetItem.GetCachedTypeString(type)}_{pathId}");
        }

        private bool EntryMatchesQuery(int entryIndex, string query)
        {
            var container = _normalizedContainers[entryIndex];
            if (!string.IsNullOrEmpty(container) && container.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var fallback = _fallbackFolderNames[entryIndex];
            if (!string.IsNullOrEmpty(fallback) && fallback.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var srcIdx = _sourceIndices[entryIndex];
            if (_arena != null)
            {
                var name = _arena.GetName(srcIdx);
                if (!string.IsNullOrEmpty(name) && name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var typeStr = AssetItem.GetCachedTypeString(_arena.GetType(srcIdx));
                return typeStr.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            if (_handles != null)
            {
                var h = _handles[srcIdx];
                if (!string.IsNullOrEmpty(h.Name) && h.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return AssetItem.GetCachedTypeString(h.Type).Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            var item = _eagerItems![srcIdx];
            if (!string.IsNullOrEmpty(item.Name) && item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return AssetItem.GetCachedTypeString(item.Type).Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private static VirtualContainerTreeIndex FinalizeIndex(
            AssetIndexArena? arena,
            IReadOnlyList<AssetHandle>? handles,
            IReadOnlyList<AssetItem>? eagerItems,
            List<int> sourceIndicesList,
            List<string?> normalizedContainersList,
            List<string> fallbackNamesList,
            Dictionary<string, (List<int> Entries, bool IsFallback)> rootGroups,
            int pageSize)
        {
            var sortedRoots = new List<(string Key, int[] EntryIndices, bool IsFallback)>(rootGroups.Count);
            foreach (var kvp in rootGroups)
            {
                sortedRoots.Add((kvp.Key, kvp.Value.Entries.ToArray(), kvp.Value.IsFallback));
            }

            // Show structured container roots (e.g. "assets") first, then bundle-filename fallback folders, alphabetically.
            sortedRoots.Sort(static (a, b) =>
            {
                var fallbackCmp = (a.IsFallback ? 1 : 0).CompareTo(b.IsFallback ? 1 : 0);
                if (fallbackCmp != 0) return fallbackCmp;
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            return new VirtualContainerTreeIndex(
                arena,
                handles,
                eagerItems,
                sourceIndicesList.ToArray(),
                normalizedContainersList.ToArray(),
                fallbackNamesList.ToArray(),
                sortedRoots,
                pageSize);
        }

        private static void AddToRootGroup(
            Dictionary<string, (List<int> Entries, bool IsFallback)> groups,
            string topSegment,
            int entryIndex,
            bool isFallback)
        {
            if (!groups.TryGetValue(topSegment, out var group))
            {
                group = (new List<int>(4), isFallback);
                groups[topSegment] = group;
            }
            else if (!isFallback && group.IsFallback)
            {
                groups[topSegment] = (group.Entries, false);
            }

            group.Entries.Add(entryIndex);
        }

        private static string ExtractFirstSegment(string normalizedPath)
        {
            var slash = normalizedPath.IndexOf('/');
            return slash < 0 ? normalizedPath : normalizedPath.Substring(0, slash);
        }

        private static bool TryGetSegmentAtDepth(string normalizedPath, int depth, out string segment, out bool hasMoreSegments)
        {
            var start = 0;
            var currentDepth = 0;
            var len = normalizedPath.Length;

            while (start < len)
            {
                var slash = normalizedPath.IndexOf('/', start);
                var end = slash < 0 ? len : slash;
                if (currentDepth == depth)
                {
                    segment = normalizedPath.Substring(start, end - start);
                    hasMoreSegments = slash >= 0 && slash + 1 < len;
                    return segment.Length > 0;
                }

                if (slash < 0)
                {
                    break;
                }

                start = slash + 1;
                currentDepth++;
            }

            segment = string.Empty;
            hasMoreSegments = false;
            return false;
        }

        private static bool HasFileExtension(string segment)
        {
            var dot = segment.LastIndexOf('.');
            return dot > 0 && dot < segment.Length - 1;
        }
    }
}
