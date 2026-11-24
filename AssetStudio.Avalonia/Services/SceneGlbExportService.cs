using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AssetStudio.Avalonia.Services;

public sealed class SceneExportOptions
{
    public bool IncludeInactive { get; init; }
    public SceneLodExportMode LodMode { get; init; } = SceneLodExportMode.Lod0Only;
    public bool ExportCamerasAndLights { get; init; } = true;
    public bool ResolveStaticScriptSpawns { get; init; }
    public bool WriteManifest { get; init; } = true;
    public int MaxExternalDependencyDepth { get; init; } = 4;
    public int MaxExternalFilesToLoad { get; init; } = 64;
    public int MaxScriptSpawnInstances { get; init; } = SceneScriptSpawnAnalysisOptions.DefaultMaxSpawnedInstances;
}

public sealed class SceneGlbExportOptions
{
    public required string OutputPath { get; init; }
    public string SceneName { get; init; } = "Scene";
    public string ScenePath { get; init; } = string.Empty;
    public int BuildIndex { get; init; } = -1;

    /// <summary>
    /// Include inactive GameObjects (and their subtrees). Defaults to false so hidden proxies, LODs and
    /// debug helpers are not dumped on top of the visible scene.
    /// </summary>
    public bool IncludeInactive { get; init; }

    /// <summary>
    /// Skip renderers whose enabled flag is false while still keeping their Transform nodes.
    /// </summary>
    public bool SkipDisabledRenderers { get; init; } = true;

    public SceneLodExportMode LodMode { get; init; } = SceneLodExportMode.Lod0Only;
    public bool ExportCamerasAndLights { get; init; } = true;
    public bool ResolveStaticScriptSpawns { get; init; }
    public bool WriteManifest { get; init; } = true;
    public int MaxScriptSpawnInstances { get; init; } = SceneScriptSpawnAnalysisOptions.DefaultMaxSpawnedInstances;

    public bool IncludeAnimations { get; init; }
    public bool ExportSkins { get; init; } = true;
    public bool ExportBlendShape { get; init; } = true;
    public bool EnableInstancing { get; init; } = true;
    public float ScaleFactor { get; init; } = 1f;
    public ImageFormat ImageFormat { get; init; } = ImageFormat.Png;
    public bool Binary { get; init; } = true;

    public IReadOnlyList<string>? AdditionalSceneNames { get; init; }
    public IReadOnlyList<string>? ResolvedDependencies { get; init; }
    public IReadOnlyList<string>? MissingDependencies { get; init; }
    public IReadOnlyList<SerializedFile>? SceneSerializedFiles { get; init; }
    public SceneScriptSpawnAnalysisOptions? ScriptSpawnAnalysisOptions { get; init; }
    public SceneScriptSpawnAnalysisResult? PreComputedScriptSpawnAnalysis { get; init; }
    public IReadOnlyList<SceneScriptSpawnAttachment>? PreResolvedScriptSpawnAttachments { get; init; }

    public static SceneGlbExportOptions FromSceneExportOptions(
        string outputPath,
        string sceneName,
        SceneExportOptions sceneOptions,
        string? scenePath = null,
        int buildIndex = -1,
        bool includeAnimations = false,
        bool exportSkins = true,
        bool exportBlendShape = true,
        float scaleFactor = 1f,
        ImageFormat imageFormat = ImageFormat.Png,
        bool? binary = null)
    {
        ArgumentNullException.ThrowIfNull(sceneOptions);
        var isBinary = binary ?? !outputPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
        return new SceneGlbExportOptions
        {
            OutputPath = outputPath,
            SceneName = sceneName,
            ScenePath = scenePath ?? string.Empty,
            BuildIndex = buildIndex,
            IncludeInactive = sceneOptions.IncludeInactive,
            SkipDisabledRenderers = true,
            LodMode = sceneOptions.LodMode,
            ExportCamerasAndLights = sceneOptions.ExportCamerasAndLights,
            ResolveStaticScriptSpawns = sceneOptions.ResolveStaticScriptSpawns,
            WriteManifest = sceneOptions.WriteManifest,
            MaxScriptSpawnInstances = sceneOptions.MaxScriptSpawnInstances,
            IncludeAnimations = includeAnimations,
            ExportSkins = exportSkins,
            ExportBlendShape = exportBlendShape,
            EnableInstancing = true,
            ScaleFactor = scaleFactor,
            ImageFormat = imageFormat,
            Binary = isBinary
        };
    }
}

public sealed class SceneExportManifest
{
    public string SceneName { get; init; } = string.Empty;
    public string ScenePath { get; init; } = string.Empty;
    public int BuildIndex { get; init; } = -1;
    public List<string> AdditionalScenes { get; init; } = new();
    public string OutputGlbPath { get; init; } = string.Empty;
    public string ExportTimestampUtc { get; init; } = string.Empty;
    public SceneExportManifestOptions Options { get; init; } = new();
    public SceneExportManifestStatistics Statistics { get; init; } = new();
    public List<string> ResolvedDependencies { get; init; } = new();
    public List<string> MissingDependencies { get; init; } = new();
    public List<SceneExportMissingAsset> MissingAssets { get; init; } = new();
    public SortedDictionary<string, int> UnsupportedComponents { get; init; } = new(StringComparer.Ordinal);
    public SceneExportScriptSpawnsSection ScriptSpawns { get; init; } = new();
}

public sealed class SceneExportManifestOptions
{
    public bool IncludeInactive { get; init; }
    public string LodMode { get; init; } = nameof(SceneLodExportMode.Lod0Only);
    public bool ExportCamerasAndLights { get; init; } = true;
    public bool ResolveStaticScriptSpawns { get; init; }
    public bool WriteManifest { get; init; } = true;
    public bool IncludeAnimations { get; init; }
    public bool ExportSkins { get; init; } = true;
    public bool ExportBlendShape { get; init; } = true;
    public bool EnableInstancing { get; init; } = true;
}

public sealed class SceneExportManifestStatistics
{
    public int Nodes { get; init; }
    public int Meshes { get; init; }
    public int TotalMeshInstances { get; init; }
    public int UniqueGeometryCount { get; init; }
    public int InstancedNodes { get; init; }
    public int Materials { get; init; }
    public int Textures { get; init; }
    public int Cameras { get; init; }
    public int Lights { get; init; }
    public int SkippedLodRenderers { get; init; }
    public int AttachedScriptSpawns { get; init; }
    public int SkippedInactiveObjects { get; init; }
}

public sealed class SceneExportMissingAsset
{
    public string AssetType { get; init; } = string.Empty;
    public long PathId { get; init; }
    public string SourceFile { get; init; } = string.Empty;
    public string OwnerGameObject { get; init; } = string.Empty;
}

public sealed class SceneExportScriptSpawnsSection
{
    public List<SceneExportResolvedScriptSpawnEntry> Resolved { get; init; } = new();
    public List<SceneExportUnresolvedDynamicSpawnerEntry> UnresolvedDynamicSpawners { get; init; } = new();
}

public sealed class SceneExportResolvedScriptSpawnEntry
{
    public string GameObjectPath { get; init; } = string.Empty;
    public long OwnerPathId { get; init; }
    public string ScriptClass { get; init; } = string.Empty;
    public string FieldPath { get; init; } = string.Empty;
    public string TargetFileName { get; init; } = string.Empty;
    public long TargetPathId { get; init; }
    public string TargetGuid { get; init; } = string.Empty;
    public string TargetAssetPath { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public bool ConfirmedByMonoIl { get; init; }
    public bool Attached { get; init; }
}

public sealed class SceneExportUnresolvedDynamicSpawnerEntry
{
    public string GameObjectPath { get; init; } = string.Empty;
    public string ScriptClass { get; init; } = string.Empty;
    public string FieldPath { get; init; } = string.Empty;
    public List<string> CandidateTargets { get; init; } = new();
    public string Confidence { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class SceneDependencyClosureResult
{
    public List<SerializedFile> AllLoadedFiles { get; } = new();
    public List<SerializedFile> DependencyFiles { get; } = new();
    public List<string> ResolvedDependencies { get; } = new();
    public List<string> MissingDependencies { get; } = new();
    public List<ParsedAddressablesCatalog> AddressablesCatalogs { get; } = new();
}

public sealed class SceneGlbExportResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string? ManifestPath { get; init; }
    public SceneExportManifest? Manifest { get; init; }
    public string SceneName { get; init; } = string.Empty;
    public int RootGameObjectCount { get; init; }
    public int SkippedLodRendererCount { get; init; }
    public int AttachedScriptSpawnCount { get; init; }
    public int SkippedInactiveObjectCount { get; init; }
    public GltfExportStatistics Stats { get; init; } = new GltfExportStatistics();
    public string SummaryMessage { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

public sealed class SceneGlbExportService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly HashSet<ClassIDType> AlwaysSupportedSceneComponentTypes = new()
    {
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.LODGroup,
        ClassIDType.Animator,
        ClassIDType.Animation
    };

    private static readonly HashSet<string> UnityBuiltInVirtualResources = new(StringComparer.OrdinalIgnoreCase)
    {
        "unity default resources",
        "unity_builtin_extra"
    };

    public static HashSet<ClassIDType> GetRequiredSceneObjectTypes(
        bool includeAnimations,
        bool exportCamerasAndLights,
        bool resolveStaticScriptSpawns)
    {
        var set = new HashSet<ClassIDType>
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
            ClassIDType.Animation,
            ClassIDType.LODGroup
        };

        if (exportCamerasAndLights)
        {
            set.Add(ClassIDType.Camera);
            set.Add(ClassIDType.Light);
        }

        if (resolveStaticScriptSpawns)
        {
            set.Add(ClassIDType.MonoBehaviour);
            set.Add(ClassIDType.MonoScript);
            set.Add(ClassIDType.AssetBundle);
        }

        if (includeAnimations)
        {
            set.Add(ClassIDType.AnimationClip);
            set.Add(ClassIDType.AnimatorController);
            set.Add(ClassIDType.AnimatorOverrideController);
        }

        return set;
    }

    public static string GetManifestPathForOutput(string outputGlbPath)
    {
        var fullOutputPath = Path.GetFullPath(outputGlbPath);
        var dir = Path.GetDirectoryName(fullOutputPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(fullOutputPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "scene";
        }

        return Path.Combine(dir, baseName + ".manifest.json");
    }

    /// <summary>
    /// Resolves and loads transitive scene dependencies in bounded memory-safe batches:
    /// 1) SQLite Schema v16 <c>SourceFileExternals</c> BFS (when available),
    /// 2) In-memory <c>SerializedFile.m_Externals</c> BFS,
    /// 3) Unity <c>levelN &lt;-&gt; sharedassetsN</c> pairing,
    /// 4) <c>AddressablesCatalogDependencyResolver</c> <c>DirectDependencyInternalIds</c>.
    /// </summary>
    public static SceneDependencyClosureResult ResolveAndLoadSceneDependencies(
        AssetsManager assetsManager,
        IReadOnlyList<SerializedFile> seedFiles,
        SQLiteProjectIndexCache? sqliteCache = null,
        string? cacheFolderPath = null,
        string? cacheSignature = null,
        string? projectRoot = null,
        Func<string, string?>? resolveSourcePathByFileName = null,
        Action<string>? beforeLoadFile = null,
        int maxDepth = 4,
        int maxAdditionalFiles = 64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetsManager);
        ArgumentNullException.ThrowIfNull(seedFiles);

        var result = new SceneDependencyClosureResult();
        var validSeeds = seedFiles.Where(static f => f != null).Distinct().ToList();
        if (validSeeds.Count == 0)
        {
            return result;
        }

        var pathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedFilesSet = new HashSet<SerializedFile>();
        var loadedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedDiskPaths = new HashSet<string>(pathComparer);
        var resolvedDepsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingDepsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initialDiskPaths = new List<string>();
        var searchDirectories = new HashSet<string>(pathComparer);

        void TrackLoadedFile(SerializedFile sf, bool isSeed)
        {
            if (sf == null)
            {
                return;
            }

            if (loadedFilesSet.Add(sf))
            {
                result.AllLoadedFiles.Add(sf);
                if (!isSeed)
                {
                    result.DependencyFiles.Add(sf);
                }
            }

            if (!string.IsNullOrWhiteSpace(sf.fileName))
            {
                loadedFileNames.Add(sf.fileName);
                if (isSeed)
                {
                    seedFileNames.Add(sf.fileName);
                }
                else
                {
                    resolvedDepsSet.Add(sf.fileName);
                }
            }

            var diskPath = ResolveExistingPath(sf.originalPath) ?? ResolveExistingPath(sf.fullName);
            if (!string.IsNullOrWhiteSpace(diskPath))
            {
                var full = SafeGetFullPath(diskPath);
                if (loadedDiskPaths.Add(full) && isSeed)
                {
                    initialDiskPaths.Add(full);
                }

                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    searchDirectories.Add(dir);
                }
            }
        }

        // Snapshot already-loaded files so we don't needlessly reload what is already in RAM.
        var existingSnapshot = SnapshotAssetsFiles(assetsManager);
        var existingByFileName = new Dictionary<string, SerializedFile>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < existingSnapshot.Length; i++)
        {
            var existing = existingSnapshot[i];
            if (existing?.reader != null && !string.IsNullOrWhiteSpace(existing.fileName))
            {
                existingByFileName.TryAdd(existing.fileName, existing);
            }
        }

        foreach (var seed in validSeeds)
        {
            TrackLoadedFile(seed, isSeed: true);
        }

        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            searchDirectories.Add(SafeGetFullPath(projectRoot));
        }

        var effectiveMaxDepth = Math.Max(0, maxDepth);
        var effectiveBudget = Math.Max(0, maxAdditionalFiles);
        var newlyLoadedDiskFiles = 0;

        bool TryLoadDependencyDiskFile(string diskPath, string? expectedFileName)
        {
            if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
            {
                return false;
            }

            var fullPath = SafeGetFullPath(diskPath);
            if (loadedDiskPaths.Contains(fullPath))
            {
                return true;
            }

            if (newlyLoadedDiskFiles >= effectiveBudget)
            {
                return false;
            }

            try
            {
                AssetsManager.ThrowIfMemoryPressureTooHigh("scene dependency loading");
            }
            catch (MemoryPressureException)
            {
                return false;
            }

            try
            {
                beforeLoadFile?.Invoke(fullPath);
                if (assetsManager.LazyLoading)
                {
                    assetsManager.LoadFilesForPreview(fullPath);
                }
                else
                {
                    assetsManager.LoadFiles(fullPath);
                }

                if (!string.IsNullOrWhiteSpace(expectedFileName))
                {
                    assetsManager.WaitForAssetsFileLoaded(expectedFileName, 3000);
                }

                newlyLoadedDiskFiles++;
                loadedDiskPaths.Add(fullPath);

                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    searchDirectories.Add(dir);
                }

                var afterSnapshot = SnapshotAssetsFiles(assetsManager);
                for (var i = 0; i < afterSnapshot.Length; i++)
                {
                    var sf = afterSnapshot[i];
                    if (sf == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(sf.fileName))
                    {
                        existingByFileName[sf.fileName] = sf;
                    }

                    var sfOrig = ResolveExistingPath(sf.originalPath) ?? ResolveExistingPath(sf.fullName);
                    if (!string.IsNullOrWhiteSpace(sfOrig)
                        && string.Equals(SafeGetFullPath(sfOrig), fullPath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                    {
                        TrackLoadedFile(sf, isSeed: false);
                    }
                    else if (!string.IsNullOrWhiteSpace(expectedFileName)
                        && string.Equals(sf.fileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        TrackLoadedFile(sf, isSeed: false);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Scene export could not load dependency '{Path.GetFileName(fullPath)}': {ex.Message}");
                return false;
            }
        }

        string? ResolveCandidateDiskPath(
            string externalName,
            string? externalPathName,
            Dictionary<string, string>? sqlitePathMap)
        {
            var leaf = SQLiteProjectIndexCache.NormalizeExternalLeafName(externalName);
            var altLeaf = SQLiteProjectIndexCache.NormalizeExternalLeafName(externalPathName);

            foreach (var candidate in new[] { leaf, altLeaf })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (sqlitePathMap != null && sqlitePathMap.TryGetValue(candidate, out var fromSqlite) && File.Exists(fromSqlite))
                {
                    return fromSqlite;
                }

                if (resolveSourcePathByFileName != null)
                {
                    var resolved = resolveSourcePathByFileName(candidate);
                    if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                    {
                        return resolved;
                    }
                }

                var indexedPath = assetsManager.ProjectIndex
                    .GetHandlesForFile(candidate)
                    .Select(static h => h?.OriginalPath)
                    .FirstOrDefault(static p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
                if (!string.IsNullOrWhiteSpace(indexedPath))
                {
                    return indexedPath;
                }

                foreach (var dir in searchDirectories)
                {
                    var direct = Path.Combine(dir, candidate);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(externalPathName) && File.Exists(externalPathName))
            {
                return externalPathName;
            }

            return null;
        }

        // Step 1: SQLite transitive SourceFileExternals BFS (O(1) query across entire project index).
        Dictionary<string, string>? sqliteExternalPaths = null;
        if (sqliteCache != null
            && !string.IsNullOrWhiteSpace(cacheFolderPath)
            && !string.IsNullOrWhiteSpace(cacheSignature)
            && seedFileNames.Count > 0
            && effectiveMaxDepth > 0)
        {
            var transitiveExternalNames = sqliteCache.GetTransitiveExternalFileNames(
                cacheFolderPath,
                cacheSignature,
                seedFileNames,
                effectiveMaxDepth);
            if (transitiveExternalNames.Count > 0)
            {
                sqliteExternalPaths = sqliteCache.GetSourceFilePathsByNames(
                    cacheFolderPath,
                    cacheSignature,
                    transitiveExternalNames);

                for (var i = 0; i < transitiveExternalNames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extName = transitiveExternalNames[i];
                    if (string.IsNullOrWhiteSpace(extName)
                        || IsStreamingOrBuiltInExternal(extName)
                        || loadedFileNames.Contains(extName))
                    {
                        continue;
                    }

                    if (existingByFileName.TryGetValue(extName, out var alreadyInRam))
                    {
                        TrackLoadedFile(alreadyInRam, isSeed: false);
                        continue;
                    }

                    var diskPath = ResolveCandidateDiskPath(extName, null, sqliteExternalPaths);
                    if (!string.IsNullOrWhiteSpace(diskPath))
                    {
                        if (!TryLoadDependencyDiskFile(diskPath, extName))
                        {
                            missingDepsSet.Add(extName);
                        }
                    }
                    else
                    {
                        missingDepsSet.Add(extName);
                    }
                }
            }
        }

        // Step 2: levelN <-> sharedassetsN pairing for all seed scene files.
        foreach (var seed in validSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pairedName in GetPairedLevelSharedAssetsFileNames(seed.fileName))
            {
                if (loadedFileNames.Contains(pairedName))
                {
                    continue;
                }

                if (existingByFileName.TryGetValue(pairedName, out var alreadyLoadedPair))
                {
                    TrackLoadedFile(alreadyLoadedPair, isSeed: false);
                    continue;
                }

                var pairedPath = ResolveCandidateDiskPath(pairedName, null, sqliteExternalPaths);
                if (!string.IsNullOrWhiteSpace(pairedPath))
                {
                    TryLoadDependencyDiskFile(pairedPath, pairedName);
                }
            }
        }

        // Step 3: In-memory cycle-safe BFS over SerializedFile.m_Externals up to effectiveMaxDepth.
        var bfsQueue = new Queue<(SerializedFile File, int Depth)>();
        var visitedBfsFiles = new HashSet<SerializedFile>();
        foreach (var loaded in result.AllLoadedFiles)
        {
            if (visitedBfsFiles.Add(loaded))
            {
                bfsQueue.Enqueue((loaded, 0));
            }
        }

        while (bfsQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentFile, depth) = bfsQueue.Dequeue();
            if (depth >= effectiveMaxDepth || currentFile.m_Externals == null || currentFile.m_Externals.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < currentFile.m_Externals.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = currentFile.m_Externals[i];
                if (ext == null)
                {
                    continue;
                }

                var extName = SQLiteProjectIndexCache.NormalizeExternalLeafName(
                    !string.IsNullOrWhiteSpace(ext.fileName) ? ext.fileName : ext.pathName);
                if (string.IsNullOrWhiteSpace(extName) || IsStreamingOrBuiltInExternal(extName))
                {
                    continue;
                }

                if (loadedFileNames.Contains(extName))
                {
                    if (!seedFileNames.Contains(extName))
                    {
                        resolvedDepsSet.Add(extName);
                    }
                    missingDepsSet.Remove(extName);
                    continue;
                }

                if (existingByFileName.TryGetValue(extName, out var alreadyLoaded))
                {
                    TrackLoadedFile(alreadyLoaded, isSeed: false);
                    missingDepsSet.Remove(extName);
                    if (visitedBfsFiles.Add(alreadyLoaded))
                    {
                        bfsQueue.Enqueue((alreadyLoaded, depth + 1));
                    }
                    continue;
                }

                var diskPath = ResolveCandidateDiskPath(extName, ext.pathName, sqliteExternalPaths);
                if (!string.IsNullOrWhiteSpace(diskPath) && TryLoadDependencyDiskFile(diskPath, extName))
                {
                    missingDepsSet.Remove(extName);
                    if (existingByFileName.TryGetValue(extName, out var newlyLoaded) && visitedBfsFiles.Add(newlyLoaded))
                    {
                        bfsQueue.Enqueue((newlyLoaded, depth + 1));
                    }
                    else
                    {
                        // Also enqueue any newly tracked SerializedFiles from that bundle.
                        for (var f = 0; f < result.AllLoadedFiles.Count; f++)
                        {
                            var loadedSf = result.AllLoadedFiles[f];
                            if (visitedBfsFiles.Add(loadedSf))
                            {
                                bfsQueue.Enqueue((loadedSf, depth + 1));
                            }
                        }
                    }
                }
                else
                {
                    missingDepsSet.Add(extName);
                }
            }
        }

        // Step 4: Addressables catalog DirectDependencyInternalIds resolution.
        if (initialDiskPaths.Count > 0 || !string.IsNullOrWhiteSpace(projectRoot))
        {
            var catalogs = AddressablesCatalogDependencyResolver.LoadCatalogsForFiles(initialDiskPaths, projectRoot);
            if (catalogs.Count > 0)
            {
                result.AddressablesCatalogs.AddRange(catalogs);
            }

            var remainingBudget = Math.Max(0, effectiveBudget - newlyLoadedDiskFiles);
            if (initialDiskPaths.Count > 0 && remainingBudget > 0)
            {
                var addressablesDeps = AddressablesCatalogDependencyResolver.ResolveDependenciesForLoadedFiles(
                    initialDiskPaths,
                    result.AllLoadedFiles,
                    loadedFileNames,
                    maxDependencies: remainingBudget,
                    projectRoot: projectRoot);

                for (var i = 0; i < addressablesDeps.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var depBundlePath = addressablesDeps[i];
                    if (string.IsNullOrWhiteSpace(depBundlePath) || !File.Exists(depBundlePath))
                    {
                        continue;
                    }

                    var depLeaf = Path.GetFileName(depBundlePath);
                    if (TryLoadDependencyDiskFile(depBundlePath, expectedFileName: null))
                    {
                        resolvedDepsSet.Add(depLeaf);
                        missingDepsSet.Remove(depLeaf);
                    }
                }
            }
        }

        // Reconcile any external names that got satisfied by a multi-CAB Addressables bundle load.
        foreach (var loadedName in loadedFileNames)
        {
            missingDepsSet.Remove(loadedName);
        }

        result.ResolvedDependencies.AddRange(resolvedDepsSet.OrderBy(static s => s, StringComparer.Ordinal));
        result.MissingDependencies.AddRange(missingDepsSet.OrderBy(static s => s, StringComparer.Ordinal));
        return result;
    }

    public static IReadOnlyList<string> GetPairedLevelSharedAssetsFileNames(string? serializedFileName)
    {
        if (string.IsNullOrWhiteSpace(serializedFileName))
        {
            return Array.Empty<string>();
        }

        var leaf = SQLiteProjectIndexCache.NormalizeExternalLeafName(serializedFileName);
        if (leaf.StartsWith("level", StringComparison.OrdinalIgnoreCase) && leaf.Length > 5)
        {
            var suffix = leaf.Substring(5);
            var dot = suffix.IndexOf('.');
            if (dot >= 0)
            {
                suffix = suffix.Substring(0, dot);
            }

            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var levelIndex) && levelIndex >= 0)
            {
                var paired = new List<string>(2)
                {
                    $"sharedassets{levelIndex.ToString(CultureInfo.InvariantCulture)}.assets"
                };
                if (levelIndex != 0)
                {
                    paired.Add("sharedassets0.assets");
                }
                return paired;
            }
        }
        else if (string.Equals(leaf, "mainData", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "sharedassets0.assets", "resources.assets" };
        }

        return Array.Empty<string>();
    }

    public static void MaterializeSceneObjectsForFile(
        AssetsManager assetsManager,
        SerializedFile sourceFile,
        HashSet<ClassIDType> objectTypes,
        CancellationToken cancellationToken = default)
    {
        if (sourceFile?.reader == null || sourceFile.m_Objects == null || sourceFile.m_Objects.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

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

                    var handle = assetsManager?.ProjectIndex?.GetHandle(sourceFile, objectInfo.m_PathID);
                    if (handle != null)
                    {
                        handle.RealObject = obj;
                        if (assetsManager!.LazyLoading)
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

    /// <summary>
    /// Collects the top-level GameObjects of a SerializedFile (scene or prefab bundle).
    /// Works for both eagerly processed files and lazily materialized files whose GameObjects were not yet
    /// linked to their components.
    /// </summary>
    public static List<GameObject> CollectRootGameObjectsFromSerializedFile(SerializedFile serializedFile, bool includeInactive = true)
    {
        ArgumentNullException.ThrowIfNull(serializedFile);

        var orderedGameObjects = SnapshotGameObjects(serializedFile);
        var fileGameObjects = new HashSet<GameObject>(orderedGameObjects);

        var roots = new List<GameObject>();
        foreach (var go in orderedGameObjects)
        {
            EnsureGameObjectComponentsLinked(go);
            if (!IsRootGameObject(go, fileGameObjects))
            {
                continue;
            }

            if (!includeInactive && !go.m_IsActive)
            {
                continue;
            }

            roots.Add(go);
        }

        return roots;
    }

    /// <summary>
    /// Collects active roots first; when the file has no active root at all (e.g. an all-inactive prefab bundle),
    /// falls back to every root so the export never ends up empty.
    /// </summary>
    public static List<GameObject> CollectRootGameObjectsWithInactiveFallback(
        IEnumerable<SerializedFile> serializedFiles,
        out bool usedInactiveFallback)
    {
        ArgumentNullException.ThrowIfNull(serializedFiles);
        var files = serializedFiles.Where(static f => f != null).Distinct().ToList();

        var roots = CollectDistinctRoots(files, includeInactive: false);
        usedInactiveFallback = false;
        if (roots.Count == 0)
        {
            roots = CollectDistinctRoots(files, includeInactive: true);
            usedInactiveFallback = roots.Count > 0;
        }

        return roots;
    }

    /// <summary>
    /// Selects the material for a renderer slot from a per-renderer resolved material list.
    /// </summary>
    public static Material? SelectMaterialForSlot(IReadOnlyList<Material?>? materials, int slotIndex)
    {
        if (materials == null || slotIndex < 0 || slotIndex >= materials.Count)
        {
            return null;
        }

        return materials[slotIndex];
    }

    /// <summary>
    /// Links Transform / MeshFilter / Renderer / Animator components to a GameObject when the eager
    /// post-processing pass did not run (Lazy Mode materialization).
    /// </summary>
    public static void EnsureGameObjectComponentsLinked(GameObject go)
    {
        if (go?.m_Components == null)
        {
            return;
        }

        if (go.m_Transform != null
            && (go.m_MeshRenderer != null || go.m_SkinnedMeshRenderer != null || go.m_Components.Length <= 1))
        {
            return;
        }

        foreach (var compPtr in go.m_Components)
        {
            if (compPtr == null || compPtr.IsNull || !compPtr.TryGet(out var comp) || comp == null)
            {
                continue;
            }

            switch (comp)
            {
                case Transform tr when go.m_Transform == null:
                    go.m_Transform = tr;
                    break;
                case MeshRenderer mr when go.m_MeshRenderer == null:
                    go.m_MeshRenderer = mr;
                    break;
                case MeshFilter mf when go.m_MeshFilter == null:
                    go.m_MeshFilter = mf;
                    break;
                case SkinnedMeshRenderer smr when go.m_SkinnedMeshRenderer == null:
                    go.m_SkinnedMeshRenderer = smr;
                    break;
                case Animator animator when go.m_Animator == null:
                    go.m_Animator = animator;
                    break;
                case Animation animation when go.m_Animation == null:
                    go.m_Animation = animation;
                    break;
            }
        }
    }

    public static List<GameObject> CollectRootGameObjectsFromSceneNode(GameObjectNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var roots = new List<GameObject>();
        var seen = new HashSet<GameObject>();

        if (node.GameObject != null)
        {
            roots.Add(node.GameObject);
            return roots;
        }

        foreach (var child in node.Children)
        {
            if (child.GameObject != null && seen.Add(child.GameObject))
            {
                roots.Add(child.GameObject);
            }
        }

        return roots;
    }

    public static GameObjectNode FindSceneRootNode(GameObjectNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var current = node;
        while (current.Parent != null)
        {
            current = current.Parent;
        }
        return current;
    }

    public SceneGlbExportResult ExportScene(
        IReadOnlyList<GameObject> rootGameObjects,
        SceneGlbExportOptions options,
        Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null,
        Action<int, int, string>? progressCallback = null,
        CancellationToken cancellationToken = default,
        Func<Renderer, Mesh?>? meshResolver = null,
        Func<Renderer, Mesh, int, Material?>? materialResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return new SceneGlbExportResult
            {
                Success = false,
                SceneName = options.SceneName,
                ErrorMessage = "Output file path must not be empty."
            };
        }

        if (rootGameObjects == null || rootGameObjects.Count == 0)
        {
            return new SceneGlbExportResult
            {
                Success = false,
                OutputPath = options.OutputPath,
                SceneName = options.SceneName,
                ErrorMessage = "No GameObjects were found in the selected scene."
            };
        }

        var fullOutputPath = Path.GetFullPath(options.OutputPath);
        string? writtenManifestPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Optional opt-in static script-spawn resolution.
            SceneScriptSpawnAnalysisResult? spawnAnalysis = options.PreComputedScriptSpawnAnalysis;
            IReadOnlyList<SceneScriptSpawnAttachment>? spawnAttachments = options.PreResolvedScriptSpawnAttachments;
            if (options.ResolveStaticScriptSpawns)
            {
                progressCallback?.Invoke(3, 100, $"Analyzing static script spawns for '{options.SceneName}'...");
                if (spawnAnalysis == null && spawnAttachments == null)
                {
                    var analyzer = new SceneScriptSpawnAnalyzer();
                    var analysisOptions = options.ScriptSpawnAnalysisOptions ?? new SceneScriptSpawnAnalysisOptions
                    {
                        IncludeInactiveGameObjects = options.IncludeInactive,
                        MaxSpawnedInstances = options.MaxScriptSpawnInstances
                    };
                    spawnAnalysis = analyzer.AnalyzeScene(rootGameObjects, analysisOptions, cancellationToken);
                }

                if (spawnAttachments == null && spawnAnalysis != null)
                {
                    spawnAttachments = spawnAnalysis.ToModelConverterAttachments();
                }
            }

            // 2. Collect SCENE-11 diagnostics (inactive objects, unsupported components, missing assets, externals).
            var missingAssetsTracker = new MissingAssetTracker();
            var unsupportedComponents = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var skippedInactiveObjects = 0;

            InspectSceneForDiagnostics(
                rootGameObjects,
                options.IncludeInactive,
                options.ExportCamerasAndLights,
                unsupportedComponents,
                missingAssetsTracker,
                ref skippedInactiveObjects,
                cancellationToken);

            // Wrap resolvers so missing meshes/materials/textures encountered during conversion are captured faithfully.
            Func<Renderer, Mesh?> trackedMeshResolver = renderer =>
            {
                var resolved = meshResolver != null ? meshResolver(renderer) : null;
                if (resolved == null)
                {
                    RecordMissingMeshIfNeeded(renderer, missingAssetsTracker);
                }
                return resolved;
            };

            Func<Renderer, Mesh, int, Material?> trackedMaterialResolver = (renderer, mesh, slotIndex) =>
            {
                var resolved = materialResolver != null ? materialResolver(renderer, mesh, slotIndex) : null;
                if (resolved == null)
                {
                    RecordMissingMaterialIfNeeded(renderer, slotIndex, missingAssetsTracker);
                }
                return resolved;
            };

            Func<Material, string, PPtr<Texture>?, Texture2D?> trackedTextureResolver = (material, propName, texPtr) =>
            {
                var resolved = textureResolver != null ? textureResolver(material, propName, texPtr) : null;
                if (resolved == null && texPtr != null && !texPtr.IsNull && !texPtr.TryGet(out _))
                {
                    var sourceFile = ResolvePPtrSourceFileName(material?.assetsFile, texPtr.m_FileID);
                    missingAssetsTracker.Add("Texture", texPtr.m_PathID, sourceFile, material?.m_Name ?? string.Empty);
                }
                return resolved;
            };

            progressCallback?.Invoke(8, 100, $"Converting scene '{options.SceneName}'...");
            var conversionOptions = new SceneConversionOptions
            {
                LodExportMode = options.LodMode,
                IncludeCamerasAndLights = options.ExportCamerasAndLights,
                SkipDisabledCamerasAndLights = options.SkipDisabledRenderers,
                IncludeNodeProvenanceExtras = true,
                ScriptSpawnAttachments = spawnAttachments,
                MaxScriptSpawnInstances = options.MaxScriptSpawnInstances
            };

            var converted = ModelConverter.CreateForScene(
                sceneName: options.SceneName,
                rootGameObjects: rootGameObjects,
                imageFormat: options.ImageFormat,
                includeInactive: options.IncludeInactive,
                includeAnimations: options.IncludeAnimations,
                textureResolver: trackedTextureResolver,
                progressCallback: (done, total, status) =>
                {
                    var pct = total > 0 ? 8 + (int)((double)done / total * 72.0) : 45;
                    progressCallback?.Invoke(Math.Clamp(pct, 8, 80), 100, status);
                },
                cancellationToken: cancellationToken,
                meshResolver: trackedMeshResolver,
                materialResolver: trackedMaterialResolver,
                skipDisabledRenderers: options.SkipDisabledRenderers,
                sceneOptions: conversionOptions);

            cancellationToken.ThrowIfCancellationRequested();
            progressCallback?.Invoke(85, 100, $"Writing {Path.GetFileName(fullOutputPath)} with glTF instancing...");

            var stats = ModelExporter.ExportGltfWithStats(
                path: fullOutputPath,
                imported: converted,
                binary: options.Binary,
                skins: options.ExportSkins,
                animation: options.IncludeAnimations,
                blendShape: options.ExportBlendShape,
                scaleFactor: options.ScaleFactor,
                combineMeshes: false,
                enableInstancing: options.EnableInstancing);

            SceneExportManifest? manifest = null;
            if (options.WriteManifest)
            {
                progressCallback?.Invoke(94, 100, "Writing scene reconstruction manifest (.manifest.json)...");
                manifest = BuildSceneExportManifest(
                    options,
                    fullOutputPath,
                    rootGameObjects,
                    converted,
                    stats,
                    skippedInactiveObjects,
                    missingAssetsTracker,
                    unsupportedComponents,
                    spawnAnalysis);

                var manifestPath = GetManifestPathForOutput(fullOutputPath);
                var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
                File.WriteAllText(manifestPath, manifestJson);
                writtenManifestPath = manifestPath;
            }

            progressCallback?.Invoke(100, 100, "Scene export complete.");
            var summary = BuildRichSummaryMessage(
                Path.GetFileName(fullOutputPath),
                stats,
                converted.SkippedLodRendererCount,
                converted.AttachedScriptSpawnCount,
                writtenManifestPath);

            return new SceneGlbExportResult
            {
                Success = true,
                OutputPath = fullOutputPath,
                ManifestPath = writtenManifestPath,
                Manifest = manifest,
                SceneName = options.SceneName,
                RootGameObjectCount = rootGameObjects.Count,
                SkippedLodRendererCount = converted.SkippedLodRendererCount,
                AttachedScriptSpawnCount = converted.AttachedScriptSpawnCount,
                SkippedInactiveObjectCount = skippedInactiveObjects,
                Stats = stats,
                SummaryMessage = summary
            };
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(fullOutputPath);
            if (!string.IsNullOrEmpty(writtenManifestPath))
            {
                TryDeletePartialFile(writtenManifestPath);
            }
            throw;
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(fullOutputPath);
            if (!string.IsNullOrEmpty(writtenManifestPath))
            {
                TryDeletePartialFile(writtenManifestPath);
            }
            return new SceneGlbExportResult
            {
                Success = false,
                OutputPath = fullOutputPath,
                SceneName = options.SceneName,
                RootGameObjectCount = rootGameObjects.Count,
                ErrorMessage = ex.Message
            };
        }
    }

    private static SceneExportManifest BuildSceneExportManifest(
        SceneGlbExportOptions options,
        string fullOutputPath,
        IReadOnlyList<GameObject> rootGameObjects,
        ModelConverter converted,
        GltfExportStatistics stats,
        int skippedInactiveObjects,
        MissingAssetTracker missingAssetsTracker,
        SortedDictionary<string, int> unsupportedComponents,
        SceneScriptSpawnAnalysisResult? spawnAnalysis)
    {
        var resolvedDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.ResolvedDependencies != null)
        {
            foreach (var d in options.ResolvedDependencies)
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    resolvedDeps.Add(d);
                }
            }
        }

        if (options.MissingDependencies != null)
        {
            foreach (var d in options.MissingDependencies)
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    missingDeps.Add(d);
                }
            }
        }

        // If caller did not pass pre-computed dependency lists, infer directly from scene SerializedFiles' m_Externals.
        if (resolvedDeps.Count == 0 && missingDeps.Count == 0)
        {
            var sceneFiles = new HashSet<SerializedFile>();
            if (options.SceneSerializedFiles != null)
            {
                foreach (var sf in options.SceneSerializedFiles)
                {
                    if (sf != null)
                    {
                        sceneFiles.Add(sf);
                    }
                }
            }

            foreach (var go in rootGameObjects)
            {
                if (go?.assetsFile != null)
                {
                    sceneFiles.Add(go.assetsFile);
                }
            }

            var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sf in sceneFiles)
            {
                if (!string.IsNullOrWhiteSpace(sf.fileName))
                {
                    loadedNames.Add(sf.fileName);
                }

                if (sf.assetsManager != null)
                {
                    foreach (var loaded in SnapshotAssetsFiles(sf.assetsManager))
                    {
                        if (!string.IsNullOrWhiteSpace(loaded?.fileName))
                        {
                            loadedNames.Add(loaded.fileName);
                        }
                    }
                }
            }

            foreach (var sf in sceneFiles)
            {
                if (sf.m_Externals == null)
                {
                    continue;
                }

                for (var i = 0; i < sf.m_Externals.Count; i++)
                {
                    var ext = sf.m_Externals[i];
                    if (ext == null)
                    {
                        continue;
                    }

                    var extName = SQLiteProjectIndexCache.NormalizeExternalLeafName(
                        !string.IsNullOrWhiteSpace(ext.fileName) ? ext.fileName : ext.pathName);
                    if (string.IsNullOrWhiteSpace(extName) || IsStreamingOrBuiltInExternal(extName))
                    {
                        continue;
                    }

                    if (loadedNames.Contains(extName))
                    {
                        resolvedDeps.Add(extName);
                    }
                    else
                    {
                        missingDeps.Add(extName);
                    }
                }
            }
        }

        foreach (var r in resolvedDeps)
        {
            missingDeps.Remove(r);
        }

        var scriptSpawnsSection = new SceneExportScriptSpawnsSection();
        if (spawnAnalysis != null)
        {
            for (var i = 0; i < spawnAnalysis.ResolvedSpawns.Count; i++)
            {
                var s = spawnAnalysis.ResolvedSpawns[i];
                scriptSpawnsSection.Resolved.Add(new SceneExportResolvedScriptSpawnEntry
                {
                    GameObjectPath = s.OwnerGameObjectPath ?? string.Empty,
                    OwnerPathId = s.OwnerPathId,
                    ScriptClass = s.ScriptClass ?? string.Empty,
                    FieldPath = s.FieldPath ?? string.Empty,
                    TargetFileName = s.TargetFileName ?? string.Empty,
                    TargetPathId = s.TargetPathId,
                    TargetGuid = s.TargetGuid ?? string.Empty,
                    TargetAssetPath = s.TargetAssetPath ?? string.Empty,
                    SourceKind = s.SourceKind.ToString(),
                    Confidence = s.Confidence.ToString(),
                    ConfirmedByMonoIl = s.ConfirmedByMonoIl,
                    Attached = s.ResolvedPrefabRoot != null
                });
            }

            for (var i = 0; i < spawnAnalysis.UnresolvedDynamicSpawners.Count; i++)
            {
                var h = spawnAnalysis.UnresolvedDynamicSpawners[i];
                var candidates = h.CandidateReferences
                    .Where(static c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static c => c, StringComparer.Ordinal)
                    .ToList();

                scriptSpawnsSection.UnresolvedDynamicSpawners.Add(new SceneExportUnresolvedDynamicSpawnerEntry
                {
                    GameObjectPath = h.OwnerGameObjectPath ?? string.Empty,
                    ScriptClass = h.ScriptClass ?? string.Empty,
                    FieldPath = h.FieldPath ?? string.Empty,
                    CandidateTargets = candidates,
                    Confidence = h.Confidence.ToString(),
                    Reason = h.Reason ?? string.Empty
                });
            }

            scriptSpawnsSection.Resolved.Sort(static (a, b) =>
            {
                var cmp = string.CompareOrdinal(a.GameObjectPath, b.GameObjectPath);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.ScriptClass, b.ScriptClass);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.FieldPath, b.FieldPath);
            });

            scriptSpawnsSection.UnresolvedDynamicSpawners.Sort(static (a, b) =>
            {
                var cmp = string.CompareOrdinal(a.GameObjectPath, b.GameObjectPath);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.ScriptClass, b.ScriptClass);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.FieldPath, b.FieldPath);
            });
        }

        return new SceneExportManifest
        {
            SceneName = options.SceneName ?? string.Empty,
            ScenePath = options.ScenePath ?? string.Empty,
            BuildIndex = options.BuildIndex,
            AdditionalScenes = options.AdditionalSceneNames != null
                ? options.AdditionalSceneNames.Where(static n => !string.IsNullOrWhiteSpace(n)).ToList()
                : new List<string>(),
            OutputGlbPath = fullOutputPath,
            ExportTimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Options = new SceneExportManifestOptions
            {
                IncludeInactive = options.IncludeInactive,
                LodMode = options.LodMode.ToString(),
                ExportCamerasAndLights = options.ExportCamerasAndLights,
                ResolveStaticScriptSpawns = options.ResolveStaticScriptSpawns,
                WriteManifest = options.WriteManifest,
                IncludeAnimations = options.IncludeAnimations,
                ExportSkins = options.ExportSkins,
                ExportBlendShape = options.ExportBlendShape,
                EnableInstancing = options.EnableInstancing
            },
            Statistics = new SceneExportManifestStatistics
            {
                Nodes = stats.TotalNodes,
                Meshes = stats.UniqueGltfMeshes,
                TotalMeshInstances = stats.TotalMeshInstances,
                UniqueGeometryCount = stats.UniqueGeometryBuffers,
                InstancedNodes = stats.ReusedGeometryCount,
                Materials = stats.MaterialsCount,
                Textures = stats.TexturesCount,
                Cameras = stats.CamerasCount,
                Lights = stats.LightsCount,
                SkippedLodRenderers = converted.SkippedLodRendererCount,
                AttachedScriptSpawns = converted.AttachedScriptSpawnCount,
                SkippedInactiveObjects = skippedInactiveObjects
            },
            ResolvedDependencies = resolvedDeps.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
            MissingDependencies = missingDeps.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
            MissingAssets = missingAssetsTracker.ToDeterministicList(),
            UnsupportedComponents = new SortedDictionary<string, int>(unsupportedComponents, StringComparer.Ordinal),
            ScriptSpawns = scriptSpawnsSection
        };
    }

    private static string BuildRichSummaryMessage(
        string outputFileName,
        GltfExportStatistics stats,
        int skippedLodRenderers,
        int attachedScriptSpawns,
        string? manifestPath)
    {
        var baseSummary = stats.BuildSummary(outputFileName);
        var extras = new List<string>();
        if (stats.CamerasCount > 0)
        {
            extras.Add($"{stats.CamerasCount:N0} camera(s)");
        }
        if (stats.LightsCount > 0)
        {
            extras.Add($"{stats.LightsCount:N0} light(s)");
        }
        if (skippedLodRenderers > 0)
        {
            extras.Add($"{skippedLodRenderers:N0} LOD renderer(s) filtered");
        }
        if (attachedScriptSpawns > 0)
        {
            extras.Add($"{attachedScriptSpawns:N0} script-spawned prefab(s) attached");
        }
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            extras.Add($"manifest: {Path.GetFileName(manifestPath)}");
        }

        return extras.Count > 0
            ? $"{baseSummary} [{string.Join(", ", extras)}]"
            : baseSummary;
    }

    private static void InspectSceneForDiagnostics(
        IReadOnlyList<GameObject> rootGameObjects,
        bool includeInactive,
        bool exportCamerasAndLights,
        SortedDictionary<string, int> unsupportedComponents,
        MissingAssetTracker missingAssets,
        ref int skippedInactiveObjects,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<GameObject>();
        var stack = new Stack<(GameObject Go, bool AncestorInactive)>();
        for (var i = rootGameObjects.Count - 1; i >= 0; i--)
        {
            var root = rootGameObjects[i];
            if (root != null)
            {
                stack.Push((root, false));
            }
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (go, ancestorInactive) = stack.Pop();
            if (go == null || !visited.Add(go))
            {
                continue;
            }

            EnsureGameObjectComponentsLinked(go);

            var isSelfOrAncestorInactive = ancestorInactive || !go.m_IsActive;
            if (!includeInactive && isSelfOrAncestorInactive)
            {
                skippedInactiveObjects++;
                // Continue walking children only to count inactive subtree members if already linked.
                PushTransformChildren(go.m_Transform, ancestorInactive: true, stack);
                continue;
            }

            if (go.m_Components != null)
            {
                for (var i = 0; i < go.m_Components.Length; i++)
                {
                    var compPtr = go.m_Components[i];
                    if (compPtr == null || compPtr.IsNull)
                    {
                        continue;
                    }

                    var compType = ResolveComponentClassType(go, compPtr);
                    if (compType == ClassIDType.UnknownType)
                    {
                        continue;
                    }

                    var isSupported = AlwaysSupportedSceneComponentTypes.Contains(compType)
                        || (exportCamerasAndLights && (compType == ClassIDType.Camera || compType == ClassIDType.Light));
                    if (!isSupported)
                    {
                        var className = compType.ToString();
                        unsupportedComponents.TryGetValue(className, out var count);
                        unsupportedComponents[className] = count + 1;
                    }
                }
            }

            PushTransformChildren(go.m_Transform, ancestorInactive: false, stack);
        }
    }

    private static void PushTransformChildren(
        Transform? transform,
        bool ancestorInactive,
        Stack<(GameObject Go, bool AncestorInactive)> stack)
    {
        var children = transform?.m_Children;
        if (children == null || children.Length == 0)
        {
            return;
        }

        for (var i = children.Length - 1; i >= 0; i--)
        {
            var childPtr = children[i];
            if (childPtr == null || childPtr.IsNull)
            {
                continue;
            }

            if (!childPtr.TryGet(out var childTr) || childTr == null)
            {
                if (transform?.assetsFile?.assetsManager != null)
                {
                    foreach (var sf in SnapshotAssetsFiles(transform.assetsFile.assetsManager))
                    {
                        if (sf.TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundTr)
                        {
                            childTr = foundTr;
                            break;
                        }
                    }
                }
            }

            if (childTr?.m_GameObject != null
                && !childTr.m_GameObject.IsNull
                && childTr.m_GameObject.TryGet(out var childGo)
                && childGo != null)
            {
                stack.Push((childGo, ancestorInactive));
            }
        }
    }

    private static ClassIDType ResolveComponentClassType(GameObject owner, PPtr<Component> compPtr)
    {
        if (compPtr.TryGet(out var comp) && comp != null)
        {
            return comp.type;
        }

        SerializedFile? targetFile = null;
        if (compPtr.m_FileID == 0)
        {
            targetFile = owner.assetsFile;
        }
        else
        {
            compPtr.TryGetAssetsFile(out targetFile);
        }

        if (targetFile?.ObjectInfoDic != null
            && targetFile.ObjectInfoDic.TryGetValue(compPtr.m_PathID, out var info)
            && info != null)
        {
            return ClassIDTypeHelper.FromClassId(info.classID);
        }

        var manager = owner.assetsFile?.assetsManager;
        if (manager?.ProjectIndex != null)
        {
            var fileName = ResolvePPtrSourceFileName(owner.assetsFile, compPtr.m_FileID);
            if (!string.IsNullOrEmpty(fileName)
                && manager.ProjectIndex.TryGetIndexByUniqueId($"{fileName}#{compPtr.m_PathID}", out var idx))
            {
                return manager.ProjectIndex.Arena.GetType(idx);
            }
        }

        return ClassIDType.UnknownType;
    }

    private static void RecordMissingMeshIfNeeded(Renderer renderer, MissingAssetTracker tracker)
    {
        if (renderer == null)
        {
            return;
        }

        PPtr<Mesh>? meshPtr = null;
        SerializedFile? ownerFile = renderer.assetsFile;
        string ownerGoName = string.Empty;
        GameObject? go = null;

        if (renderer.m_GameObject != null && renderer.m_GameObject.TryGet(out go) && go != null)
        {
            ownerGoName = go.m_Name ?? string.Empty;
        }

        if (renderer is SkinnedMeshRenderer smr)
        {
            meshPtr = smr.m_Mesh;
        }
        else if (go != null)
        {
            EnsureGameObjectComponentsLinked(go);
            if (go.m_MeshFilter != null)
            {
                meshPtr = go.m_MeshFilter.m_Mesh;
                ownerFile = go.m_MeshFilter.assetsFile ?? ownerFile;
            }
        }

        if (meshPtr != null && !meshPtr.IsNull && !meshPtr.TryGet(out _))
        {
            var sourceFile = ResolvePPtrSourceFileName(ownerFile, meshPtr.m_FileID);
            tracker.Add("Mesh", meshPtr.m_PathID, sourceFile, ownerGoName);
        }
    }

    private static void RecordMissingMaterialIfNeeded(Renderer renderer, int slotIndex, MissingAssetTracker tracker)
    {
        if (renderer?.m_Materials == null || slotIndex < 0 || slotIndex >= renderer.m_Materials.Length)
        {
            return;
        }

        var matPtr = renderer.m_Materials[slotIndex];
        if (matPtr == null || matPtr.IsNull || matPtr.TryGet(out _))
        {
            return;
        }

        string ownerGoName = string.Empty;
        if (renderer.m_GameObject != null && renderer.m_GameObject.TryGet(out var go) && go != null)
        {
            ownerGoName = go.m_Name ?? string.Empty;
        }

        var sourceFile = ResolvePPtrSourceFileName(renderer.assetsFile, matPtr.m_FileID);
        tracker.Add("Material", matPtr.m_PathID, sourceFile, ownerGoName);
    }

    private static string ResolvePPtrSourceFileName(SerializedFile? ownerFile, int fileId)
    {
        if (ownerFile == null)
        {
            return string.Empty;
        }

        if (fileId == 0)
        {
            return ownerFile.fileName ?? string.Empty;
        }

        if (fileId > 0 && ownerFile.m_Externals != null && fileId - 1 < ownerFile.m_Externals.Count)
        {
            var ext = ownerFile.m_Externals[fileId - 1];
            if (ext != null)
            {
                return SQLiteProjectIndexCache.NormalizeExternalLeafName(
                    !string.IsNullOrWhiteSpace(ext.fileName) ? ext.fileName : ext.pathName);
            }
        }

        return ownerFile.fileName ?? string.Empty;
    }

    private sealed class MissingAssetTracker
    {
        private readonly HashSet<(string AssetType, long PathId, string SourceFile)> _seen = new();
        private readonly List<SceneExportMissingAsset> _items = new();
        private readonly object _lock = new();

        public void Add(string assetType, long pathId, string sourceFile, string ownerGameObject)
        {
            var safeType = assetType ?? string.Empty;
            var safeFile = sourceFile ?? string.Empty;
            lock (_lock)
            {
                if (_seen.Add((safeType, pathId, safeFile)))
                {
                    _items.Add(new SceneExportMissingAsset
                    {
                        AssetType = safeType,
                        PathId = pathId,
                        SourceFile = safeFile,
                        OwnerGameObject = ownerGameObject ?? string.Empty
                    });
                }
            }
        }

        public List<SceneExportMissingAsset> ToDeterministicList()
        {
            lock (_lock)
            {
                return _items
                    .OrderBy(static x => x.AssetType, StringComparer.Ordinal)
                    .ThenBy(static x => x.SourceFile, StringComparer.Ordinal)
                    .ThenBy(static x => x.PathId)
                    .ToList();
            }
        }
    }

    private static SerializedFile[] SnapshotAssetsFiles(AssetsManager? manager)
    {
        if (manager == null)
        {
            return Array.Empty<SerializedFile>();
        }

        lock (manager.loadLock)
        {
            return manager.assetsFileList.ToArray();
        }
    }

    private static bool IsStreamingOrBuiltInExternal(string fileName)
    {
        return fileName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
            || UnityBuiltInVirtualResources.Contains(fileName);
    }

    private static string? ResolveExistingPath(string? candidate)
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

    private static string SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static List<GameObject> CollectDistinctRoots(IReadOnlyList<SerializedFile> files, bool includeInactive)
    {
        var roots = new List<GameObject>();
        var seen = new HashSet<GameObject>();
        foreach (var file in files)
        {
            foreach (var go in CollectRootGameObjectsFromSerializedFile(file, includeInactive))
            {
                if (seen.Add(go))
                {
                    roots.Add(go);
                }
            }
        }
        return roots;
    }

    private static List<GameObject> SnapshotGameObjects(SerializedFile serializedFile)
    {
        var result = new List<GameObject>();
        var seen = new HashSet<GameObject>();

        // Prefer the immutable ObjectInfo table + thread-safe TryGetObject: lazy materialization / LRU eviction
        // may mutate the Objects list concurrently on other threads.
        var infos = serializedFile.m_Objects;
        if (infos != null && infos.Count > 0)
        {
            foreach (var info in infos)
            {
                if (info == null || info.classID != (int)ClassIDType.GameObject)
                {
                    continue;
                }

                if (serializedFile.TryGetObject(info.m_PathID, out var obj) && obj is GameObject go && seen.Add(go))
                {
                    result.Add(go);
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                result.Clear();
                seen.Clear();
                foreach (var obj in serializedFile.Objects.ToArray())
                {
                    if (obj is GameObject go && seen.Add(go))
                    {
                        result.Add(go);
                    }
                }
                break;
            }
            catch (InvalidOperationException)
            {
                // Collection mutated during snapshot; retry.
            }
            catch (ArgumentException)
            {
                // Collection mutated during ToArray copy; retry.
            }
        }

        return result;
    }

    private static bool IsRootGameObject(GameObject go, HashSet<GameObject> fileGameObjects)
    {
        if (go.m_Transform == null)
        {
            return true;
        }

        var fatherPtr = go.m_Transform.m_Father;
        return fatherPtr == null
            || fatherPtr.IsNull
            || !fatherPtr.TryGet(out var father)
            || father == null
            || !father.m_GameObject.TryGet(out var parentGo)
            || parentGo == null
            || !fileGameObjects.Contains(parentGo);
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
