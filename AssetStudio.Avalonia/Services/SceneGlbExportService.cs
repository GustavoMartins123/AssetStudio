using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AssetStudio.Avalonia.Services;

public sealed class SceneGlbExportOptions
{
    public required string OutputPath { get; init; }
    public string SceneName { get; init; } = "Scene";

    /// <summary>
    /// Include inactive GameObjects (and their subtrees). Defaults to false so hidden proxies, LODs and
    /// debug helpers are not dumped on top of the visible scene.
    /// </summary>
    public bool IncludeInactive { get; init; }

    /// <summary>
    /// Skip renderers whose enabled flag is false while still keeping their Transform nodes.
    /// </summary>
    public bool SkipDisabledRenderers { get; init; } = true;

    public bool IncludeAnimations { get; init; }
    public bool ExportSkins { get; init; } = true;
    public bool ExportBlendShape { get; init; } = true;
    public bool EnableInstancing { get; init; } = true;
    public float ScaleFactor { get; init; } = 1f;
    public ImageFormat ImageFormat { get; init; } = ImageFormat.Png;
    public bool Binary { get; init; } = true;
}

public sealed class SceneGlbExportResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string SceneName { get; init; } = string.Empty;
    public int RootGameObjectCount { get; init; }
    public GltfExportStatistics Stats { get; init; } = new GltfExportStatistics();
    public string SummaryMessage { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

public sealed class SceneGlbExportService
{
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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            progressCallback?.Invoke(5, 100, $"Converting scene '{options.SceneName}'...");
            var converted = ModelConverter.CreateForScene(
                sceneName: options.SceneName,
                rootGameObjects: rootGameObjects,
                imageFormat: options.ImageFormat,
                includeInactive: options.IncludeInactive,
                includeAnimations: options.IncludeAnimations,
                textureResolver: textureResolver,
                progressCallback: (done, total, status) =>
                {
                    var pct = total > 0 ? 5 + (int)((double)done / total * 75.0) : 40;
                    progressCallback?.Invoke(Math.Clamp(pct, 5, 80), 100, status);
                },
                cancellationToken: cancellationToken,
                meshResolver: meshResolver,
                materialResolver: materialResolver,
                skipDisabledRenderers: options.SkipDisabledRenderers);

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

            progressCallback?.Invoke(100, 100, "Scene export complete.");
            var summary = stats.BuildSummary(Path.GetFileName(fullOutputPath));

            return new SceneGlbExportResult
            {
                Success = true,
                OutputPath = fullOutputPath,
                SceneName = options.SceneName,
                RootGameObjectCount = rootGameObjects.Count,
                Stats = stats,
                SummaryMessage = summary
            };
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(fullOutputPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(fullOutputPath);
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
