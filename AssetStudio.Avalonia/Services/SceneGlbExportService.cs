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
    public bool IncludeInactive { get; init; } = true;
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
    public static List<GameObject> CollectRootGameObjectsFromSerializedFile(SerializedFile serializedFile)
    {
        ArgumentNullException.ThrowIfNull(serializedFile);

        var fileGameObjects = new HashSet<GameObject>();
        var orderedGameObjects = new List<GameObject>();
        foreach (var obj in serializedFile.Objects)
        {
            if (obj is GameObject go && fileGameObjects.Add(go))
            {
                orderedGameObjects.Add(go);
            }
        }

        var roots = new List<GameObject>();
        foreach (var go in orderedGameObjects)
        {
            if (go.m_Transform == null)
            {
                roots.Add(go);
                continue;
            }

            var fatherPtr = go.m_Transform.m_Father;
            if (fatherPtr == null
                || fatherPtr.IsNull
                || !fatherPtr.TryGet(out var father)
                || father == null
                || !father.m_GameObject.TryGet(out var parentGo)
                || parentGo == null
                || !fileGameObjects.Contains(parentGo))
            {
                roots.Add(go);
            }
        }

        return roots;
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
        CancellationToken cancellationToken = default)
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
                cancellationToken: cancellationToken);

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
