using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AssetStudio.Avalonia.Services.Scripting;

namespace AssetStudio.Avalonia.Services;

public sealed class UnityProjectScriptExportSummary
{
    public int ExportedScriptFiles { get; init; }
    public int ExportedAssemblies { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = new();
}

public sealed class UnityProjectExportOptions
{
    public required string OutputDirectory { get; init; }
    public string SceneName { get; init; } = "Scene";
    public string ScenePath { get; init; } = string.Empty;
    public string? UnityVersion { get; init; }

    /// <summary>
    /// Unlike .glb exports, Unity projects natively preserve GameObject.m_IsActive: 0 cleanly,
    /// so inactive GameObjects are included by default.
    /// </summary>
    public bool IncludeInactive { get; init; } = true;

    public bool ExportMeshes { get; init; } = true;
    public bool ExportTextures { get; init; } = true;
    public bool ExportMaterials { get; init; } = true;
    public bool ExportMonoBehaviours { get; init; } = true;
    public bool ExportTerrains { get; init; } = true;
    public bool ExportColliders { get; init; } = true;
    public bool WriteManifest { get; init; } = true;

    public int MaxTerrainGridResolution { get; init; } = TerrainMeshConverter.DefaultMaxGridResolution;
    public int MaxTerrainBakedTextureSize { get; init; } = TerrainMeshConverter.DefaultMaxBakedTextureSize;

    /// <summary>
    /// Optional hook called with the target Assets/Scripts directory so managed scripts can be exported
    /// using the exact same deterministic MonoScript GUIDs referenced by the generated .unity scene.
    /// </summary>
    public Func<string, CancellationToken, UnityProjectScriptExportSummary?>? IncludeScriptsCallback { get; init; }

    public Func<Renderer, Mesh?>? MeshResolver { get; init; }
    public Func<Renderer, Mesh, int, Material?>? MaterialResolver { get; init; }
    public Func<Material, string, PPtr<Texture>?, Texture2D?>? TextureResolver { get; init; }
    public AssemblyLoader? AssemblyLoader { get; init; }
    public Func<MonoBehaviour, OrderedDictionary?>? MonoBehaviourFieldProvider { get; init; }
    public Func<AssetStudio.Object, OrderedDictionary?>? ColliderFieldProvider { get; init; }

    public IReadOnlyList<string>? AdditionalSceneNames { get; init; }
    public IReadOnlyList<string>? ResolvedDependencies { get; init; }
    public IReadOnlyList<string>? MissingDependencies { get; init; }
}

public sealed class UnityProjectShaderReference
{
    public string ShaderName { get; init; } = string.Empty;
    public int UsageCount { get; init; }
    public bool IsCustom { get; init; }
}

public sealed class UnityProjectScriptReference
{
    public string Assembly { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string DeterministicGuid { get; init; } = string.Empty;
    public int InstanceCount { get; init; }
}

public sealed class UnityProjectExportStatistics
{
    public int GameObjects { get; init; }
    public int ActiveGameObjects { get; init; }
    public int InactiveGameObjects { get; init; }
    public int Transforms { get; init; }
    public int MeshFilters { get; init; }
    public int MeshRenderers { get; init; }
    public int SkinnedMeshRenderers { get; init; }
    public int ShadowsOnlyRenderers { get; init; }
    public int Cameras { get; init; }
    public int Lights { get; init; }
    public int LODGroups { get; init; }
    public int Colliders { get; init; }
    public int Terrains { get; init; }
    public int MonoBehaviours { get; init; }
    public int UniqueMeshes { get; init; }
    public int UniqueMaterials { get; init; }
    public int UniqueTextures { get; init; }
}

public sealed class UnityProjectExportManifest
{
    public string SceneName { get; init; } = string.Empty;
    public string ScenePath { get; init; } = string.Empty;
    public string UnityVersion { get; init; } = string.Empty;
    public string ExportTimestampUtc { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public string SceneFilePath { get; init; } = string.Empty;
    public List<string> AdditionalScenes { get; init; } = new();
    public UnityProjectExportStatistics Statistics { get; init; } = new();
    public SortedDictionary<int, string> LayersUsed { get; init; } = new();
    public List<UnityProjectShaderReference> ShadersReferenced { get; init; } = new();
    public List<UnityProjectScriptReference> ScriptsReferenced { get; init; } = new();
    public List<string> ResolvedDependencies { get; init; } = new();
    public List<string> MissingDependencies { get; init; } = new();
    public List<SceneExportMissingAsset> MissingAssets { get; init; } = new();
    public UnityProjectScriptExportSummary? ScriptExportSummary { get; init; }
}

public sealed class UnityProjectExportResult
{
    public bool Success { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public string SceneFilePath { get; init; } = string.Empty;
    public string? ManifestPath { get; init; }
    public UnityProjectExportManifest? Manifest { get; init; }
    public string SceneName { get; init; } = string.Empty;
    public string SummaryMessage { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Exports a Unity scene graph directly into an openable Unity project directory containing valid
/// Unity YAML 1.1 scene (.unity), materials (.mat), meshes (.obj), textures (.png), .meta files with
/// deterministic GUIDs, ProjectSettings (ProjectVersion.txt &amp; TagManager.asset), and ExportManifest.json.
/// </summary>
public sealed class UnityProjectExporterService
{
    private const int StreamWriterBufferSize = 65_536;
    private const int MaxYamlSerializationDepth = 12;
    private const int MaxYamlCollectionElements = 256;
    private const int MaxSanitizedFileNameLength = 96;
    private const string DefaultFallbackUnityVersion = "2022.3.0f1";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<string> YamlSpecialKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "yes", "no", "on", "off", "y", "n", "~", ".nan", ".inf", "-.inf", "+.inf"
    };

    private static readonly HashSet<string> MonoBehaviourHeaderFields = new(StringComparer.Ordinal)
    {
        "m_ObjectHideFlags",
        "m_CorrespondingSourceObject",
        "m_PrefabInstance",
        "m_PrefabAsset",
        "m_GameObject",
        "m_Enabled",
        "m_EditorHideFlags",
        "m_Script",
        "m_Name",
        "m_EditorClassIdentifier"
    };

    private static readonly Dictionary<string, int> BuiltInUnityShaderFileIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Standard"] = 46,
        ["Standard (Specular setup)"] = 45,
        ["Unlit/Color"] = 10750,
        ["Unlit/Texture"] = 10751,
        ["Unlit/Transparent"] = 10752,
        ["Sprites/Default"] = 10753,
        ["UI/Default"] = 10770,
        ["Legacy Shaders/Diffuse"] = 7,
        ["Diffuse"] = 7
    };

    private static readonly string[] UnityBuiltInLayers =
    {
        "Default",
        "TransparentFX",
        "Ignore Raycast",
        "",
        "Water",
        "UI",
        "",
        ""
    };

    // =========================================================================
    // Shared Deterministic GUID Contract (delegated to UnityProjectGuidUtility)
    // =========================================================================

    public static string ComputeMonoScriptGuid(string? assemblyName, string? ns, string? className)
        => UnityProjectGuidUtility.ComputeScriptGuid(assemblyName, ns, className);

    public static string ComputeUnityAssetGuid(string assetKind, string? serializedFileName, long pathId, string? fallbackKey = null)
        => UnityProjectGuidUtility.ComputeAssetGuid(assetKind, serializedFileName, pathId, fallbackKey);

    public static string SanitizeFileName(string? rawName, string fallback = "Asset")
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        var trimmed = rawName.Trim();
        // Strip any path segments so traversal sequences like "../../evil" never survive.
        var lastSep = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (lastSep >= 0)
        {
            trimmed = trimmed[(lastSep + 1)..];
        }

        var sb = new StringBuilder(trimmed.Length);
        var invalidChars = Path.GetInvalidFileNameChars();
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c < 0x20 || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || Array.IndexOf(invalidChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        var cleaned = sb.ToString().Replace("..", "_", StringComparison.Ordinal).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        if (cleaned.Length > MaxSanitizedFileNameLength)
        {
            cleaned = cleaned[..MaxSanitizedFileNameLength].TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return fallback;
            }
        }

        var stemBeforeDot = cleaned.Contains('.') ? cleaned[..cleaned.IndexOf('.')] : cleaned;
        if (WindowsReservedNames.Contains(cleaned) || WindowsReservedNames.Contains(stemBeforeDot))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    // =========================================================================
    // Main Export Entrypoint
    // =========================================================================

    public UnityProjectExportResult ExportSceneToUnityProject(
        IReadOnlyList<GameObject> rootGameObjects,
        UnityProjectExportOptions options,
        Action<int, int, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return new UnityProjectExportResult
            {
                Success = false,
                SceneName = options.SceneName,
                ErrorMessage = "Output directory must not be empty."
            };
        }

        if (rootGameObjects == null || rootGameObjects.Count == 0)
        {
            return new UnityProjectExportResult
            {
                Success = false,
                OutputDirectory = options.OutputDirectory,
                SceneName = options.SceneName,
                ErrorMessage = "No GameObjects were found in the selected scene."
            };
        }

        var sw = Stopwatch.StartNew();
        var rootDirFull = Path.GetFullPath(options.OutputDirectory);
        var rootDirWithSep = rootDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var safeSceneName = SanitizeFileName(options.SceneName, "Scene");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(rootDirFull);

            var projectSettingsDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(rootDirFull, "ProjectSettings"));
            var assetsDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(rootDirFull, "Assets"));
            var scenesDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(assetsDir, "Scenes"));
            var materialsDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(assetsDir, "Materials"));
            var meshesDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(assetsDir, "Meshes"));
            var texturesDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(assetsDir, "Textures"));

            var state = new ExportState(rootDirWithSep, options);
            var unityVersion = DetectUnityVersion(rootGameObjects, options.UnityVersion);

            // Pass 1: Traverse scene graph iteratively, assign deterministic positive fileIDs, and index all unique assets.
            progressCallback?.Invoke(5, 100, $"Indexing scene '{safeSceneName}' hierarchy...");
            CollectSceneGraph(rootGameObjects, state, cancellationToken);

            if (state.SceneNodes.Count == 0)
            {
                return new UnityProjectExportResult
                {
                    Success = false,
                    OutputDirectory = rootDirFull,
                    SceneName = safeSceneName,
                    ErrorMessage = $"No GameObjects remained after filtering '{safeSceneName}'."
                };
            }

            // Pass 2a: Write ProjectSettings & folder .meta files.
            progressCallback?.Invoke(15, 100, "Writing ProjectSettings and folder metadata...");
            WriteProjectVersionTxt(rootDirWithSep, Path.Combine(projectSettingsDir, "ProjectVersion.txt"), unityVersion);
            WriteTagManagerAsset(rootDirWithSep, Path.Combine(projectSettingsDir, "TagManager.asset"), state);
            WriteFolderMeta(rootDirWithSep, Path.Combine(assetsDir, "Scenes.meta"), "Scenes");
            WriteFolderMeta(rootDirWithSep, Path.Combine(assetsDir, "Materials.meta"), "Materials");
            WriteFolderMeta(rootDirWithSep, Path.Combine(assetsDir, "Meshes.meta"), "Meshes");
            WriteFolderMeta(rootDirWithSep, Path.Combine(assetsDir, "Textures.meta"), "Textures");

            // Pass 2b: Export unique Textures (.png + .png.meta).
            if (options.ExportTextures && (state.TexturesToExport.Count > 0 || state.TerrainAssets.Count > 0))
            {
                progressCallback?.Invoke(25, 100, $"Exporting {state.TexturesToExport.Count:N0} texture(s)...");
                ExportTextures(state, texturesDir, cancellationToken);
            }

            // Pass 2c: Export unique Meshes (.obj + .obj.meta).
            if (options.ExportMeshes && state.MeshesToExport.Count > 0)
            {
                progressCallback?.Invoke(45, 100, $"Exporting {state.MeshesToExport.Count:N0} mesh(es)...");
                ExportMeshes(state, meshesDir, cancellationToken);
            }

            // Pass 2d: Export unique Terrains (mesh .obj + baked splatmap .png + terrain .mat).
            if (options.ExportTerrains && state.TerrainAssets.Count > 0)
            {
                progressCallback?.Invoke(60, 100, $"Exporting {state.TerrainAssets.Count:N0} terrain(s)...");
                ExportTerrains(state, meshesDir, texturesDir, materialsDir, cancellationToken);
            }

            // Pass 2e: Export unique Materials (.mat + .mat.meta).
            if (options.ExportMaterials && state.MaterialsToExport.Count > 0)
            {
                progressCallback?.Invoke(70, 100, $"Exporting {state.MaterialsToExport.Count:N0} material(s)...");
                ExportMaterials(state, materialsDir, cancellationToken);
            }

            // Pass 2f: Stream Unity YAML 1.1 Scene (.unity + .unity.meta).
            progressCallback?.Invoke(82, 100, $"Streaming {safeSceneName}.unity...");
            var sceneFilePath = EnsureConfinedPath(rootDirWithSep, Path.Combine(scenesDir, safeSceneName + ".unity"));
            WriteUnitySceneFile(sceneFilePath, state, cancellationToken);
            WriteSceneMetaFile(
                EnsureConfinedPath(rootDirWithSep, sceneFilePath + ".meta"),
                ComputeUnityAssetGuid("scene", options.ScenePath, 0, safeSceneName));

            // Pass 2g: Optional managed C# scripts callback (Block 1 integration hook).
            UnityProjectScriptExportSummary? scriptSummary = null;
            if (options.IncludeScriptsCallback != null)
            {
                progressCallback?.Invoke(92, 100, "Exporting managed C# scripts...");
                var scriptsDir = EnsureConfinedDirectory(rootDirWithSep, Path.Combine(assetsDir, "Scripts"));
                scriptSummary = options.IncludeScriptsCallback(scriptsDir, cancellationToken);
                var scriptsMetaPath = EnsureConfinedPath(rootDirWithSep, Path.Combine(assetsDir, "Scripts.meta"));
                if (!File.Exists(scriptsMetaPath))
                {
                    WriteFolderMeta(rootDirWithSep, scriptsMetaPath, "Scripts");
                }
            }

            sw.Stop();

            // Pass 2h: Write ExportManifest.json (SCENE-11 report).
            UnityProjectExportManifest? manifest = null;
            string? manifestPath = null;
            if (options.WriteManifest)
            {
                progressCallback?.Invoke(96, 100, "Writing ExportManifest.json...");
                manifest = BuildManifest(
                    options,
                    safeSceneName,
                    unityVersion,
                    sw.ElapsedMilliseconds,
                    rootDirFull,
                    sceneFilePath,
                    state,
                    scriptSummary);
                manifestPath = EnsureConfinedPath(rootDirWithSep, Path.Combine(rootDirFull, "ExportManifest.json"));
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions), Utf8NoBom);
            }

            progressCallback?.Invoke(100, 100, "Unity project export complete.");
            var stats = manifest?.Statistics ?? BuildStatistics(state);
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Exported Unity project '{safeSceneName}' ({sw.ElapsedMilliseconds:N0} ms): {stats.GameObjects:N0} GameObjects ({stats.InactiveGameObjects:N0} inactive), {stats.UniqueMeshes:N0} meshes, {stats.UniqueMaterials:N0} materials, {stats.UniqueTextures:N0} textures, {stats.Terrains:N0} terrains, {stats.MonoBehaviours:N0} scripts -> {rootDirFull}");

            return new UnityProjectExportResult
            {
                Success = true,
                OutputDirectory = rootDirFull,
                SceneFilePath = sceneFilePath,
                ManifestPath = manifestPath,
                Manifest = manifest,
                SceneName = safeSceneName,
                SummaryMessage = summary
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UnityProjectExportResult
            {
                Success = false,
                OutputDirectory = rootDirFull,
                SceneName = safeSceneName,
                ErrorMessage = ex.Message
            };
        }
    }

    // =========================================================================
    // Internal State & Asset Tracking Models
    // =========================================================================

    private sealed class SceneNodeRecord
    {
        public required GameObject GameObject { get; init; }
        public required long GameObjectFileId { get; init; }
        public Transform? Transform { get; init; }
        public long TransformFileId { get; init; }
        public int RootOrder { get; set; }
        public List<ExportedComponentRecord> Components { get; } = new();
    }

    private enum ExportedComponentKind
    {
        Transform,
        RectTransform,
        MeshFilter,
        MeshRenderer,
        SkinnedMeshRenderer,
        Camera,
        Light,
        LODGroup,
        BoxCollider,
        SphereCollider,
        CapsuleCollider,
        MeshCollider,
        MonoBehaviour,
        TerrainMeshFilter,
        TerrainMeshRenderer
    }

    private sealed class ExportedComponentRecord
    {
        public required ExportedComponentKind Kind { get; init; }
        public required long FileId { get; init; }
        public required long OwnerGameObjectFileId { get; init; }
        public AssetStudio.Object? SourceObject { get; init; }
        public ExportedMeshAsset? ResolvedMesh { get; init; }
        public List<ExportedMaterialAsset?>? ResolvedMaterials { get; init; }
        public ExportedTerrainAsset? ResolvedTerrain { get; init; }
        public string? MonoScriptGuid { get; init; }
    }

    private sealed class ExportedMeshAsset
    {
        public required Mesh Mesh { get; init; }
        public required string Guid { get; init; }
        public required string SafeName { get; init; }
        public bool Exported { get; set; }
    }

    private sealed class ExportedTextureAsset
    {
        public required Texture2D Texture { get; init; }
        public required string Guid { get; init; }
        public required string SafeName { get; init; }
        public bool IsNormalMap { get; set; }
        public bool Exported { get; set; }
    }

    private sealed class ExportedMaterialAsset
    {
        public required Material Material { get; init; }
        public required string Guid { get; init; }
        public required string SafeName { get; init; }
        public string OriginalShaderName { get; set; } = string.Empty;
        public List<(string PropName, ExportedTextureAsset? Texture, Vector2 Scale, Vector2 Offset)> ResolvedTexEnvs { get; } = new();
    }

    private sealed class ExportedTerrainAsset
    {
        public required Terrain Terrain { get; init; }
        public required TerrainData TerrainData { get; init; }
        public required string SafeName { get; init; }
        public required string MeshGuid { get; init; }
        public required string MaterialGuid { get; init; }
        public required string TextureGuid { get; init; }
        public TerrainMeshData? MeshData { get; set; }
        public bool HasBakedTexture { get; set; }
    }

    private sealed class ScriptUsageCounter
    {
        public required string Assembly { get; init; }
        public required string Namespace { get; init; }
        public required string ClassName { get; init; }
        public required string Guid { get; init; }
        public int Count { get; set; }
    }

    private sealed class ExportState
    {
        private long _nextFileId = 1000;

        public string RootDirWithSep { get; }
        public UnityProjectExportOptions Options { get; }

        public List<SceneNodeRecord> SceneNodes { get; } = new();
        public Dictionary<AssetStudio.Object, long> LocalFileIdByObject { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<(string FileName, long PathId), long> LocalFileIdByKey { get; } = new();

        public Dictionary<(string FileName, long PathId), ExportedMeshAsset> MeshesByKey { get; } = new();
        public Dictionary<Mesh, ExportedMeshAsset> MeshesByRef { get; } = new(ReferenceEqualityComparer.Instance);
        public List<ExportedMeshAsset> MeshesToExport { get; } = new();

        public Dictionary<(string FileName, long PathId), ExportedTextureAsset> TexturesByKey { get; } = new();
        public Dictionary<Texture2D, ExportedTextureAsset> TexturesByRef { get; } = new(ReferenceEqualityComparer.Instance);
        public List<ExportedTextureAsset> TexturesToExport { get; } = new();

        public Dictionary<(string FileName, long PathId), ExportedMaterialAsset> MaterialsByKey { get; } = new();
        public Dictionary<Material, ExportedMaterialAsset> MaterialsByRef { get; } = new(ReferenceEqualityComparer.Instance);
        public List<ExportedMaterialAsset> MaterialsToExport { get; } = new();

        public Dictionary<(string FileName, long PathId), ExportedTerrainAsset> TerrainsByKey { get; } = new();
        public List<ExportedTerrainAsset> TerrainAssets { get; } = new();

        public HashSet<string> UsedMeshFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UsedMaterialFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UsedTextureFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedSet<int> UsedLayers { get; } = new();
        public SortedSet<string> UsedCustomTags { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (int Count, bool IsCustom)> ShaderUsage { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ScriptUsageCounter> ScriptUsage { get; } = new(StringComparer.Ordinal);
        public HashSet<(string Type, long PathId, string SourceFile)> MissingAssetKeys { get; } = new();
        public List<SceneExportMissingAsset> MissingAssets { get; } = new();

        public int ActiveGameObjects;
        public int InactiveGameObjects;
        public int TransformCount;
        public int MeshFilterCount;
        public int MeshRendererCount;
        public int SkinnedMeshRendererCount;
        public int ShadowsOnlyRendererCount;
        public int CameraCount;
        public int LightCount;
        public int LodGroupCount;
        public int ColliderCount;
        public int TerrainCount;
        public int MonoBehaviourCount;

        public ExportState(string rootDirWithSep, UnityProjectExportOptions options)
        {
            RootDirWithSep = rootDirWithSep;
            Options = options;
        }

        public long AllocateFileId() => _nextFileId++;

        public void RegisterLocalObject(AssetStudio.Object? obj, long fileId)
        {
            if (obj == null)
            {
                return;
            }

            LocalFileIdByObject[obj] = fileId;
            if (obj.m_PathID != 0)
            {
                LocalFileIdByKey[(obj.assetsFile?.fileName ?? string.Empty, obj.m_PathID)] = fileId;
            }
        }

        public void RecordMissingAsset(string assetType, long pathId, string sourceFile, string ownerName)
        {
            var key = (assetType, pathId, sourceFile ?? string.Empty);
            if (MissingAssetKeys.Add(key))
            {
                MissingAssets.Add(new SceneExportMissingAsset
                {
                    AssetType = assetType,
                    PathId = pathId,
                    SourceFile = sourceFile ?? string.Empty,
                    OwnerGameObject = ownerName ?? string.Empty
                });
            }
        }

        public string AllocateUniqueAssetName(HashSet<string> usedSet, string? rawName, string fallbackPrefix, long pathId)
        {
            var baseName = SanitizeFileName(rawName, $"{fallbackPrefix}_{Math.Abs(pathId)}");
            if (usedSet.Add(baseName))
            {
                return baseName;
            }

            var withPathId = SanitizeFileName($"{baseName}_{Math.Abs(pathId)}", $"{fallbackPrefix}_{Math.Abs(pathId)}");
            if (usedSet.Add(withPathId))
            {
                return withPathId;
            }

            int suffix = 2;
            while (true)
            {
                var candidate = $"{baseName}_{suffix}";
                if (usedSet.Add(candidate))
                {
                    return candidate;
                }
                suffix++;
            }
        }
    }

    // =========================================================================
    // Pass 1: Scene Graph Collection & Asset Indexing
    // =========================================================================

    private static void CollectSceneGraph(
        IReadOnlyList<GameObject> rootGameObjects,
        ExportState state,
        CancellationToken cancellationToken)
    {
        var visitedGameObjects = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(GameObject Go, int SiblingOrder)>();

        for (int i = rootGameObjects.Count - 1; i >= 0; i--)
        {
            var root = rootGameObjects[i];
            if (root != null)
            {
                stack.Push((root, i));
            }
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (go, siblingOrder) = stack.Pop();
            if (go == null || !visitedGameObjects.Add(go))
            {
                continue;
            }

            if (!state.Options.IncludeInactive && !go.m_IsActive)
            {
                continue;
            }

            SceneGlbExportService.EnsureGameObjectComponentsLinked(go);

            if (go.m_IsActive) state.ActiveGameObjects++;
            else state.InactiveGameObjects++;

            if (go.m_Layer >= 0 && go.m_Layer < 32)
            {
                state.UsedLayers.Add(go.m_Layer);
            }

            var tagString = ResolveUnityTagString(go.m_Tag);
            if (go.m_Tag >= 20000)
            {
                state.UsedCustomTags.Add(tagString);
            }

            var goFileId = state.AllocateFileId();
            state.RegisterLocalObject(go, goFileId);

            Transform? tr = go.m_Transform;
            long trFileId = 0;
            if (tr != null)
            {
                trFileId = state.AllocateFileId();
                state.RegisterLocalObject(tr, trFileId);
                state.TransformCount++;
            }

            var nodeRecord = new SceneNodeRecord
            {
                GameObject = go,
                GameObjectFileId = goFileId,
                Transform = tr,
                TransformFileId = trFileId,
                RootOrder = siblingOrder
            };

            if (tr != null)
            {
                nodeRecord.Components.Add(new ExportedComponentRecord
                {
                    Kind = tr is RectTransform ? ExportedComponentKind.RectTransform : ExportedComponentKind.Transform,
                    FileId = trFileId,
                    OwnerGameObjectFileId = goFileId,
                    SourceObject = tr
                });
            }

            // Index remaining components on this GameObject.
            if (go.m_Components != null)
            {
                for (int c = 0; c < go.m_Components.Length; c++)
                {
                    var compPtr = go.m_Components[c];
                    if (compPtr == null || compPtr.IsNull)
                    {
                        continue;
                    }

                    var compObj = ResolveComponentObject(go, compPtr);
                    if (compObj == null || ReferenceEquals(compObj, tr))
                    {
                        continue;
                    }

                    RegisterComponentForNode(go, nodeRecord, compObj, state);
                }
            }

            state.SceneNodes.Add(nodeRecord);

            // Push Transform children in reverse so they pop in exact Unity sibling order.
            if (tr?.m_Children != null && tr.m_Children.Length > 0)
            {
                for (int i = tr.m_Children.Length - 1; i >= 0; i--)
                {
                    var childPtr = tr.m_Children[i];
                    if (childPtr == null || childPtr.IsNull)
                    {
                        continue;
                    }

                    var childTr = ResolveTransform(tr, childPtr);
                    if (childTr?.m_GameObject != null && !childTr.m_GameObject.IsNull)
                    {
                        var childGo = ResolveGameObject(childTr, childTr.m_GameObject);
                        if (childGo != null)
                        {
                            stack.Push((childGo, i));
                        }
                    }
                }
            }
        }
    }

    private static void RegisterComponentForNode(
        GameObject go,
        SceneNodeRecord node,
        AssetStudio.Object compObj,
        ExportState state)
    {
        switch (compObj)
        {
            case Transform:
                return;

            case MeshFilter mf:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(mf, compId);
                state.MeshFilterCount++;

                ExportedMeshAsset? exportedMesh = null;
                if (state.Options.ExportMeshes)
                {
                    var mesh = ResolveMeshFromMeshFilter(go, mf, state);
                    if (mesh != null)
                    {
                        exportedMesh = RegisterMeshAsset(mesh, state);
                    }
                    else if (mf.m_Mesh != null && !mf.m_Mesh.IsNull)
                    {
                        state.RecordMissingAsset("Mesh", mf.m_Mesh.m_PathID, ResolvePPtrFileName(mf.assetsFile, mf.m_Mesh.m_FileID), go.m_Name ?? string.Empty);
                    }
                }

                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.MeshFilter,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = mf,
                    ResolvedMesh = exportedMesh
                });
                break;
            }

            case MeshRenderer mr:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(mr, compId);
                state.MeshRendererCount++;
                if (mr.m_CastShadows == Renderer.ShadowCastingModeShadowsOnly)
                {
                    state.ShadowsOnlyRendererCount++;
                }

                var mesh = ResolveMeshForRenderer(go, mr, state);
                var mats = ResolveRendererMaterials(mr, mesh, go.m_Name ?? string.Empty, state);

                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.MeshRenderer,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = mr,
                    ResolvedMaterials = mats
                });
                break;
            }

            case SkinnedMeshRenderer smr:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(smr, compId);
                state.SkinnedMeshRendererCount++;
                if (smr.m_CastShadows == Renderer.ShadowCastingModeShadowsOnly)
                {
                    state.ShadowsOnlyRendererCount++;
                }

                var mesh = ResolveMeshForRenderer(go, smr, state);
                ExportedMeshAsset? exportedMesh = state.Options.ExportMeshes && mesh != null
                    ? RegisterMeshAsset(mesh, state)
                    : null;
                if (mesh == null && smr.m_Mesh != null && !smr.m_Mesh.IsNull)
                {
                    state.RecordMissingAsset("Mesh", smr.m_Mesh.m_PathID, ResolvePPtrFileName(smr.assetsFile, smr.m_Mesh.m_FileID), go.m_Name ?? string.Empty);
                }

                var mats = ResolveRendererMaterials(smr, mesh, go.m_Name ?? string.Empty, state);

                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.SkinnedMeshRenderer,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = smr,
                    ResolvedMesh = exportedMesh,
                    ResolvedMaterials = mats
                });
                break;
            }

            case Camera cam:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(cam, compId);
                state.CameraCount++;
                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.Camera,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = cam
                });
                break;
            }

            case Light light:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(light, compId);
                state.LightCount++;
                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.Light,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = light
                });
                break;
            }

            case LODGroup lod:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(lod, compId);
                state.LodGroupCount++;
                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.LODGroup,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = lod
                });
                break;
            }

            case Terrain terrain when state.Options.ExportTerrains:
            {
                var terrainData = ResolveTerrainData(terrain);
                if (terrainData == null || !terrainData.HasValidHeightmap)
                {
                    if (terrain.m_TerrainData != null && !terrain.m_TerrainData.IsNull)
                    {
                        state.RecordMissingAsset("TerrainData", terrain.m_TerrainData.m_PathID, ResolvePPtrFileName(terrain.assetsFile, terrain.m_TerrainData.m_FileID), go.m_Name ?? string.Empty);
                    }
                    break;
                }

                var terrainAsset = RegisterTerrainAsset(terrain, terrainData, go.m_Name, state);
                state.TerrainCount++;

                // Emit MeshFilter + MeshRenderer bound to the exported terrain mesh/material so Unity renders it immediately.
                var mfId = state.AllocateFileId();
                var mrId = state.AllocateFileId();
                state.RegisterLocalObject(terrain, mrId);
                state.MeshFilterCount++;
                state.MeshRendererCount++;

                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.TerrainMeshFilter,
                    FileId = mfId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = terrain,
                    ResolvedTerrain = terrainAsset
                });
                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.TerrainMeshRenderer,
                    FileId = mrId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = terrain,
                    ResolvedTerrain = terrainAsset
                });
                break;
            }

            case MonoBehaviour mb when state.Options.ExportMonoBehaviours:
            {
                var compId = state.AllocateFileId();
                state.RegisterLocalObject(mb, compId);
                state.MonoBehaviourCount++;

                string? scriptGuid = null;
                var monoScript = ResolveMonoScript(mb);
                if (monoScript != null)
                {
                    scriptGuid = ComputeMonoScriptGuid(monoScript.m_AssemblyName, monoScript.m_Namespace, monoScript.m_ClassName);
                    if (!state.ScriptUsage.TryGetValue(scriptGuid, out var counter))
                    {
                        counter = new ScriptUsageCounter
                        {
                            Assembly = monoScript.m_AssemblyName ?? string.Empty,
                            Namespace = monoScript.m_Namespace ?? string.Empty,
                            ClassName = monoScript.m_ClassName ?? string.Empty,
                            Guid = scriptGuid
                        };
                        state.ScriptUsage[scriptGuid] = counter;
                    }
                    counter.Count++;
                }
                else if (mb.m_Script != null && !mb.m_Script.IsNull)
                {
                    state.RecordMissingAsset("MonoScript", mb.m_Script.m_PathID, ResolvePPtrFileName(mb.assetsFile, mb.m_Script.m_FileID), go.m_Name ?? string.Empty);
                }

                node.Components.Add(new ExportedComponentRecord
                {
                    Kind = ExportedComponentKind.MonoBehaviour,
                    FileId = compId,
                    OwnerGameObjectFileId = node.GameObjectFileId,
                    SourceObject = mb,
                    MonoScriptGuid = scriptGuid
                });
                break;
            }

            default:
            {
                if (!state.Options.ExportColliders)
                {
                    return;
                }

                ExportedComponentKind? colliderKind = compObj.type switch
                {
                    ClassIDType.BoxCollider => ExportedComponentKind.BoxCollider,
                    ClassIDType.SphereCollider => ExportedComponentKind.SphereCollider,
                    ClassIDType.CapsuleCollider => ExportedComponentKind.CapsuleCollider,
                    ClassIDType.MeshCollider => ExportedComponentKind.MeshCollider,
                    _ => null
                };

                if (colliderKind.HasValue)
                {
                    var compId = state.AllocateFileId();
                    state.RegisterLocalObject(compObj, compId);
                    state.ColliderCount++;

                    ExportedMeshAsset? colliderMesh = null;
                    if (colliderKind.Value == ExportedComponentKind.MeshCollider && state.Options.ExportMeshes)
                    {
                        var dict = ReadColliderFields(compObj, state);
                        if (dict != null && TryExtractPPtrValues(dict, "m_Mesh", out int mFileId, out long mPathId) && mPathId != 0)
                        {
                            var meshPtr = new PPtr<Mesh>(mFileId, mPathId, compObj.assetsFile);
                            if (meshPtr.TryGet(out var resolvedColMesh) && resolvedColMesh != null)
                            {
                                colliderMesh = RegisterMeshAsset(resolvedColMesh, state);
                            }
                        }
                    }

                    node.Components.Add(new ExportedComponentRecord
                    {
                        Kind = colliderKind.Value,
                        FileId = compId,
                        OwnerGameObjectFileId = node.GameObjectFileId,
                        SourceObject = compObj,
                        ResolvedMesh = colliderMesh
                    });
                }
                break;
            }
        }
    }

    private static ExportedMeshAsset RegisterMeshAsset(Mesh mesh, ExportState state)
    {
        if (state.MeshesByRef.TryGetValue(mesh, out var byRef))
        {
            return byRef;
        }

        var sfName = mesh.assetsFile?.fileName ?? string.Empty;
        var key = (sfName, mesh.m_PathID);
        if (mesh.m_PathID != 0 && state.MeshesByKey.TryGetValue(key, out var existing))
        {
            state.MeshesByRef[mesh] = existing;
            return existing;
        }

        var safeName = state.AllocateUniqueAssetName(state.UsedMeshFileNames, mesh.m_Name, "Mesh", mesh.m_PathID);
        var guid = ComputeUnityAssetGuid("mesh", sfName, mesh.m_PathID, safeName);
        var asset = new ExportedMeshAsset
        {
            Mesh = mesh,
            Guid = guid,
            SafeName = safeName
        };

        state.MeshesByRef[mesh] = asset;
        if (mesh.m_PathID != 0)
        {
            state.MeshesByKey[key] = asset;
        }
        state.MeshesToExport.Add(asset);
        return asset;
    }

    private static ExportedTextureAsset RegisterTextureAsset(Texture2D tex, bool isNormalMap, ExportState state)
    {
        if (state.TexturesByRef.TryGetValue(tex, out var byRef))
        {
            if (isNormalMap) byRef.IsNormalMap = true;
            return byRef;
        }

        var sfName = tex.assetsFile?.fileName ?? string.Empty;
        var key = (sfName, tex.m_PathID);
        if (tex.m_PathID != 0 && state.TexturesByKey.TryGetValue(key, out var existing))
        {
            if (isNormalMap) existing.IsNormalMap = true;
            state.TexturesByRef[tex] = existing;
            return existing;
        }

        var safeName = state.AllocateUniqueAssetName(state.UsedTextureFileNames, tex.m_Name, "Texture", tex.m_PathID);
        var guid = ComputeUnityAssetGuid("texture2d", sfName, tex.m_PathID, safeName);
        var asset = new ExportedTextureAsset
        {
            Texture = tex,
            Guid = guid,
            SafeName = safeName,
            IsNormalMap = isNormalMap || LooksLikeNormalMapName(tex.m_Name)
        };

        state.TexturesByRef[tex] = asset;
        if (tex.m_PathID != 0)
        {
            state.TexturesByKey[key] = asset;
        }
        state.TexturesToExport.Add(asset);
        return asset;
    }

    private static ExportedMaterialAsset RegisterMaterialAsset(Material mat, ExportState state)
    {
        if (state.MaterialsByRef.TryGetValue(mat, out var byRef))
        {
            return byRef;
        }

        var sfName = mat.assetsFile?.fileName ?? string.Empty;
        var key = (sfName, mat.m_PathID);
        if (mat.m_PathID != 0 && state.MaterialsByKey.TryGetValue(key, out var existing))
        {
            state.MaterialsByRef[mat] = existing;
            return existing;
        }

        var safeName = state.AllocateUniqueAssetName(state.UsedMaterialFileNames, mat.m_Name, "Material", mat.m_PathID);
        var guid = ComputeUnityAssetGuid("material", sfName, mat.m_PathID, safeName);
        var shaderName = ResolveShaderName(mat);
        bool isCustomShader = !string.IsNullOrEmpty(shaderName) && !BuiltInUnityShaderFileIds.ContainsKey(shaderName);

        if (!string.IsNullOrEmpty(shaderName))
        {
            if (state.ShaderUsage.TryGetValue(shaderName, out var prev))
            {
                state.ShaderUsage[shaderName] = (prev.Count + 1, prev.IsCustom);
            }
            else
            {
                state.ShaderUsage[shaderName] = (1, isCustomShader);
            }
        }

        var asset = new ExportedMaterialAsset
        {
            Material = mat,
            Guid = guid,
            SafeName = safeName,
            OriginalShaderName = shaderName
        };

        if (mat.m_SavedProperties?.m_TexEnvs != null)
        {
            for (int i = 0; i < mat.m_SavedProperties.m_TexEnvs.Length; i++)
            {
                var kv = mat.m_SavedProperties.m_TexEnvs[i];
                var propName = kv.Key ?? string.Empty;
                var texEnv = kv.Value;
                if (texEnv == null)
                {
                    continue;
                }

                ExportedTextureAsset? texAsset = null;
                if (state.Options.ExportTextures)
                {
                    var tex2d = ResolveMaterialTexture(mat, propName, texEnv.m_Texture, state);
                    if (tex2d != null)
                    {
                        bool isNormalProp = IsNormalMapPropertyName(propName);
                        texAsset = RegisterTextureAsset(tex2d, isNormalProp, state);
                    }
                    else if (texEnv.m_Texture != null && !texEnv.m_Texture.IsNull)
                    {
                        state.RecordMissingAsset("Texture2D", texEnv.m_Texture.m_PathID, ResolvePPtrFileName(mat.assetsFile, texEnv.m_Texture.m_FileID), mat.m_Name ?? string.Empty);
                    }
                }

                asset.ResolvedTexEnvs.Add((propName, texAsset, texEnv.m_Scale, texEnv.m_Offset));
            }
        }

        state.MaterialsByRef[mat] = asset;
        if (mat.m_PathID != 0)
        {
            state.MaterialsByKey[key] = asset;
        }
        state.MaterialsToExport.Add(asset);
        return asset;
    }

    private static ExportedTerrainAsset RegisterTerrainAsset(
        Terrain terrain,
        TerrainData terrainData,
        string? ownerGoName,
        ExportState state)
    {
        var sfName = terrainData.assetsFile?.fileName ?? string.Empty;
        var key = (sfName, terrainData.m_PathID);
        if (terrainData.m_PathID != 0 && state.TerrainsByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var rawName = !string.IsNullOrWhiteSpace(ownerGoName)
            ? ownerGoName
            : (!string.IsNullOrWhiteSpace(terrainData.m_Name) ? terrainData.m_Name : $"Terrain_{terrainData.m_PathID}");
        var safeName = state.AllocateUniqueAssetName(state.UsedMeshFileNames, $"{rawName}_Terrain", "Terrain", terrainData.m_PathID);
        state.UsedMaterialFileNames.Add(safeName + "_Mat");
        state.UsedTextureFileNames.Add(safeName + "_Albedo");

        var meshGuid = ComputeUnityAssetGuid("terrain-mesh", sfName, terrainData.m_PathID, safeName);
        var matGuid = ComputeUnityAssetGuid("terrain-material", sfName, terrainData.m_PathID, safeName + "_Mat");
        var texGuid = ComputeUnityAssetGuid("terrain-texture", sfName, terrainData.m_PathID, safeName + "_Albedo");

        var asset = new ExportedTerrainAsset
        {
            Terrain = terrain,
            TerrainData = terrainData,
            SafeName = safeName,
            MeshGuid = meshGuid,
            MaterialGuid = matGuid,
            TextureGuid = texGuid
        };

        if (terrainData.m_PathID != 0)
        {
            state.TerrainsByKey[key] = asset;
        }
        state.TerrainAssets.Add(asset);
        return asset;
    }

    // =========================================================================
    // Pass 2: Streaming Writers (ProjectSettings, Textures, Meshes, Materials, Terrains, .unity)
    // =========================================================================

    private static void WriteProjectVersionTxt(string rootDirWithSep, string filePath, string unityVersion)
    {
        var confined = EnsureConfinedPath(rootDirWithSep, filePath);
        using var fs = new FileStream(confined, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        using var sw = new StreamWriter(fs, Utf8NoBom, 4096) { NewLine = "\n" };
        sw.Write("m_EditorVersion: ");
        sw.WriteLine(unityVersion);
        sw.Write("m_EditorVersionWithRevision: ");
        sw.Write(unityVersion);
        sw.WriteLine(" (000000000000)");
    }

    private static void WriteTagManagerAsset(string rootDirWithSep, string filePath, ExportState state)
    {
        var confined = EnsureConfinedPath(rootDirWithSep, filePath);
        using var fs = new FileStream(confined, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        using var sw = new StreamWriter(fs, Utf8NoBom, 8192) { NewLine = "\n" };

        sw.WriteLine("%YAML 1.1");
        sw.WriteLine("%TAG !u! tag:unity3d.com,2011:");
        sw.WriteLine("--- !u!78 &1");
        sw.WriteLine("TagManager:");
        sw.WriteLine("  serializedVersion: 2");

        if (state.UsedCustomTags.Count == 0)
        {
            sw.WriteLine("  tags: []");
        }
        else
        {
            sw.WriteLine("  tags:");
            foreach (var tag in state.UsedCustomTags)
            {
                sw.Write("  - ");
                sw.WriteLine(FormatYamlScalarString(tag));
            }
        }

        sw.WriteLine("  layers:");
        for (int i = 0; i < 32; i++)
        {
            string layerName = GetLayerDisplayName(i, state.UsedLayers);
            sw.Write("  - ");
            sw.WriteLine(string.IsNullOrEmpty(layerName) ? string.Empty : FormatYamlScalarString(layerName));
        }

        sw.WriteLine("  m_SortingLayers:");
        sw.WriteLine("  - name: Default");
        sw.WriteLine("    uniqueID: 0");
        sw.WriteLine("    locked: 0");
    }

    private static void WriteFolderMeta(string rootDirWithSep, string metaPath, string folderKey)
    {
        var confined = EnsureConfinedPath(rootDirWithSep, metaPath);
        var guid = ComputeUnityAssetGuid("folder", string.Empty, 0, folderKey);
        using var fs = new FileStream(confined, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        using var sw = new StreamWriter(fs, Utf8NoBom, 4096) { NewLine = "\n" };

        sw.WriteLine("fileFormatVersion: 2");
        sw.Write("guid: ");
        sw.WriteLine(guid);
        sw.WriteLine("folderAsset: yes");
        sw.WriteLine("DefaultImporter:");
        sw.WriteLine("  externalObjects: {}");
        sw.WriteLine("  userData: ");
        sw.WriteLine("  assetBundleName: ");
        sw.WriteLine("  assetBundleVariant: ");
    }

    private static void WriteSceneMetaFile(string metaPath, string guid)
    {
        using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        using var sw = new StreamWriter(fs, Utf8NoBom, 4096) { NewLine = "\n" };

        sw.WriteLine("fileFormatVersion: 2");
        sw.Write("guid: ");
        sw.WriteLine(guid);
        sw.WriteLine("DefaultImporter:");
        sw.WriteLine("  externalObjects: {}");
        sw.WriteLine("  userData: ");
        sw.WriteLine("  assetBundleName: ");
        sw.WriteLine("  assetBundleVariant: ");
    }

    private static void ExportTextures(ExportState state, string texturesDir, CancellationToken cancellationToken)
    {
        for (int i = 0; i < state.TexturesToExport.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = state.TexturesToExport[i];
            var pngPath = EnsureConfinedPath(state.RootDirWithSep, Path.Combine(texturesDir, asset.SafeName + ".png"));
            var metaPath = EnsureConfinedPath(state.RootDirWithSep, pngPath + ".meta");

            try
            {
                using var bmp = asset.Texture.ConvertToImage(flip: true, unpackNormalMap: true, forceNormalMapUnpack: asset.IsNormalMap);
                if (bmp == null)
                {
                    continue;
                }

                using (var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize))
                {
                    bmp.WriteToStream(fs, ImageFormat.Png);
                }

                WriteTextureMetaFile(metaPath, asset.Guid, asset.IsNormalMap);
                asset.Exported = true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"UnityProjectExporter: failed to export texture '{asset.SafeName}': {ex.Message}");
            }
        }
    }

    private static void WriteTextureMetaFile(string metaPath, string guid, bool isNormalMap)
    {
        using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        using var sw = new StreamWriter(fs, Utf8NoBom, 4096) { NewLine = "\n" };

        sw.WriteLine("fileFormatVersion: 2");
        sw.Write("guid: ");
        sw.WriteLine(guid);
        sw.WriteLine("TextureImporter:");
        sw.WriteLine("  internalIDToNameTable: []");
        sw.WriteLine("  externalObjects: {}");
        sw.WriteLine("  serializedVersion: 12");
        sw.WriteLine("  mipmaps:");
        sw.WriteLine("    mipMapMode: 0");
        sw.WriteLine("    enableMipMap: 1");
        sw.Write("    sRGBTexture: ");
        sw.WriteLine(isNormalMap ? "0" : "1");
        sw.Write("    linearTexture: ");
        sw.WriteLine(isNormalMap ? "1" : "0");
        sw.WriteLine("    fadeOut: 0");
        sw.WriteLine("    borderMipMap: 0");
        sw.WriteLine("    mipMapsPreserveCoverage: 0");
        sw.WriteLine("    alphaTestReferenceValue: 0.5");
        sw.WriteLine("    mipMapFadeDistanceStart: 1");
        sw.WriteLine("    mipMapFadeDistanceEnd: 3");
        sw.WriteLine("  bumpmap:");
        sw.WriteLine("    convertToNormalMap: 0");
        sw.WriteLine("    externalNormalMap: 0");
        sw.WriteLine("    heightScale: 0.25");
        sw.WriteLine("    normalMapFilter: 0");
        sw.WriteLine("  isReadable: 0");
        sw.WriteLine("  streamingMipmaps: 0");
        sw.WriteLine("  streamingMipmapsPriority: 0");
        sw.WriteLine("  vTOnly: 0");
        sw.WriteLine("  ignoreMasterTextureLimit: 0");
        sw.WriteLine("  grayScaleToAlpha: 0");
        sw.WriteLine("  generateCubemap: 6");
        sw.WriteLine("  cubemapConvolution: 0");
        sw.WriteLine("  seamlessCubemap: 0");
        sw.WriteLine("  textureFormat: 1");
        sw.WriteLine("  maxTextureSize: 2048");
        sw.WriteLine("  textureSettings:");
        sw.WriteLine("    serializedVersion: 2");
        sw.WriteLine("    filterMode: 1");
        sw.WriteLine("    aniso: 1");
        sw.WriteLine("    mipBias: 0");
        sw.WriteLine("    wrapU: 0");
        sw.WriteLine("    wrapV: 0");
        sw.WriteLine("    wrapW: 0");
        sw.WriteLine("  nPOTScale: 1");
        sw.WriteLine("  lightmap: 0");
        sw.WriteLine("  compressionQuality: 50");
        sw.WriteLine("  spriteMode: 0");
        sw.WriteLine("  spriteExtrude: 1");
        sw.WriteLine("  spriteMeshType: 1");
        sw.WriteLine("  alignment: 0");
        sw.WriteLine("  spritePivot: {x: 0.5, y: 0.5}");
        sw.WriteLine("  spritePixelsToUnits: 100");
        sw.WriteLine("  spriteBorder: {x: 0, y: 0, z: 0, w: 0}");
        sw.WriteLine("  spriteGenerateFallbackPhysicsShape: 1");
        sw.WriteLine("  alphaUsage: 1");
        sw.Write("  alphaIsTransparency: ");
        sw.WriteLine(isNormalMap ? "0" : "1");
        sw.WriteLine("  spriteTessellationDetail: -1");
        sw.Write("  textureType: ");
        sw.WriteLine(isNormalMap ? "1" : "0");
        sw.WriteLine("  textureShape: 1");
        sw.WriteLine("  singleChannelComponent: 0");
        sw.WriteLine("  flipbookRows: 1");
        sw.WriteLine("  flipbookColumns: 1");
        sw.WriteLine("  userData: ");
        sw.WriteLine("  assetBundleName: ");
        sw.WriteLine("  assetBundleVariant: ");
    }

    private static void ExportMeshes(ExportState state, string meshesDir, CancellationToken cancellationToken)
    {
        for (int i = 0; i < state.MeshesToExport.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = state.MeshesToExport[i];
            var objPath = EnsureConfinedPath(state.RootDirWithSep, Path.Combine(meshesDir, asset.SafeName + ".obj"));
            var metaPath = EnsureConfinedPath(state.RootDirWithSep, objPath + ".meta");

            try
            {
                asset.Mesh.EnsureProcessed();
                if (asset.Mesh.m_VertexCount <= 0 || asset.Mesh.m_Vertices == null || asset.Mesh.m_Vertices.Length == 0)
                {
                    continue;
                }

                WriteMeshObjStreaming(objPath, asset.SafeName, asset.Mesh);
                WriteModelImporterMetaFile(metaPath, asset.Guid, asset.SafeName);
                asset.Exported = true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"UnityProjectExporter: failed to export mesh '{asset.SafeName}': {ex.Message}");
            }
        }
    }

    private static void WriteMeshObjStreaming(string objPath, string meshName, Mesh mesh)
    {
        using var fs = new FileStream(objPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        sw.Write("# AssetStudio Unity Project Exporter\n");
        sw.Write("o ");
        sw.WriteLine(meshName);

        int vertexCount = mesh.m_VertexCount;
        var verts = mesh.m_Vertices!;
        int posStride = verts.Length >= vertexCount * 4 ? 4 : 3;

        // Negate X so Unity ModelImporter's OBJ import (which negates X) restores exact original Unity coordinates.
        for (int v = 0; v < vertexCount; v++)
        {
            int o = v * posStride;
            sw.Write("v ");
            sw.Write(FormatFloat(-verts[o]));
            sw.Write(' ');
            sw.Write(FormatFloat(verts[o + 1]));
            sw.Write(' ');
            sw.WriteLine(FormatFloat(verts[o + 2]));
        }

        bool hasUv = mesh.m_UV0 != null && mesh.m_UV0.Length >= vertexCount * 2;
        if (hasUv)
        {
            int uvStride = mesh.m_UV0!.Length / vertexCount;
            for (int v = 0; v < vertexCount; v++)
            {
                int o = v * uvStride;
                sw.Write("vt ");
                sw.Write(FormatFloat(mesh.m_UV0[o]));
                sw.Write(' ');
                sw.WriteLine(FormatFloat(mesh.m_UV0[o + 1]));
            }
        }

        bool hasNormals = mesh.m_Normals != null && mesh.m_Normals.Length >= vertexCount * 3;
        if (hasNormals)
        {
            int nrmStride = mesh.m_Normals!.Length >= vertexCount * 4 ? 4 : 3;
            for (int v = 0; v < vertexCount; v++)
            {
                int o = v * nrmStride;
                sw.Write("vn ");
                sw.Write(FormatFloat(-mesh.m_Normals[o]));
                sw.Write(' ');
                sw.Write(FormatFloat(mesh.m_Normals[o + 1]));
                sw.Write(' ');
                sw.WriteLine(FormatFloat(mesh.m_Normals[o + 2]));
            }
        }

        int subMeshCount = mesh.m_SubMeshes?.Length ?? 0;
        int indexCursor = 0;
        if (subMeshCount > 0 && mesh.m_Indices != null)
        {
            for (int s = 0; s < subMeshCount; s++)
            {
                var sub = mesh.m_SubMeshes![s];
                int faceCount = (int)(sub.indexCount / 3);
                sw.Write("g submesh_");
                sw.WriteLine(s.ToString(CultureInfo.InvariantCulture));
                sw.Write("usemtl mat_");
                sw.WriteLine(s.ToString(CultureInfo.InvariantCulture));

                for (int f = 0; f < faceCount; f++)
                {
                    int idxBase = indexCursor + f * 3;
                    if (idxBase + 2 >= mesh.m_Indices.Count)
                    {
                        break;
                    }

                    // Reverse winding to match X-negation for Unity ModelImporter roundtrip.
                    int a = (int)mesh.m_Indices[idxBase + 2] + 1;
                    int b = (int)mesh.m_Indices[idxBase + 1] + 1;
                    int c = (int)mesh.m_Indices[idxBase + 0] + 1;

                    if (a < 1 || a > vertexCount || b < 1 || b > vertexCount || c < 1 || c > vertexCount)
                    {
                        continue;
                    }

                    WriteObjFace(sw, a, b, c, hasUv, hasNormals);
                }

                indexCursor += faceCount * 3;
            }
        }
        else if (mesh.m_Indices != null && mesh.m_Indices.Count >= 3)
        {
            sw.WriteLine("g submesh_0");
            sw.WriteLine("usemtl mat_0");
            int faceCount = mesh.m_Indices.Count / 3;
            for (int f = 0; f < faceCount; f++)
            {
                int idxBase = f * 3;
                int a = (int)mesh.m_Indices[idxBase + 2] + 1;
                int b = (int)mesh.m_Indices[idxBase + 1] + 1;
                int c = (int)mesh.m_Indices[idxBase + 0] + 1;
                if (a < 1 || a > vertexCount || b < 1 || b > vertexCount || c < 1 || c > vertexCount)
                {
                    continue;
                }

                WriteObjFace(sw, a, b, c, hasUv, hasNormals);
            }
        }
    }

    private static void WriteObjFace(StreamWriter sw, int a, int b, int c, bool hasUv, bool hasNormals)
    {
        sw.Write("f ");
        WriteObjVertexRef(sw, a, hasUv, hasNormals);
        sw.Write(' ');
        WriteObjVertexRef(sw, b, hasUv, hasNormals);
        sw.Write(' ');
        WriteObjVertexRef(sw, c, hasUv, hasNormals);
        sw.WriteLine();
    }

    private static void WriteObjVertexRef(StreamWriter sw, int idx, bool hasUv, bool hasNormals)
    {
        var s = idx.ToString(CultureInfo.InvariantCulture);
        sw.Write(s);
        if (hasUv && hasNormals)
        {
            sw.Write('/');
            sw.Write(s);
            sw.Write('/');
            sw.Write(s);
        }
        else if (hasUv)
        {
            sw.Write('/');
            sw.Write(s);
        }
        else if (hasNormals)
        {
            sw.Write("//");
            sw.Write(s);
        }
    }

    private static void WriteModelImporterMetaFile(string metaPath, string guid, string meshName)
    {
        using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        using var sw = new StreamWriter(fs, Utf8NoBom, 4096) { NewLine = "\n" };

        sw.WriteLine("fileFormatVersion: 2");
        sw.Write("guid: ");
        sw.WriteLine(guid);
        sw.WriteLine("ModelImporter:");
        sw.WriteLine("  serializedVersion: 21300");
        sw.WriteLine("  internalIDToNameTable: []");
        sw.WriteLine("  externalObjects: {}");
        sw.WriteLine("  materials:");
        sw.WriteLine("    materialImportMode: 2");
        sw.WriteLine("    materialName: 0");
        sw.WriteLine("    materialSearch: 1");
        sw.WriteLine("    materialLocation: 1");
        sw.WriteLine("  animations:");
        sw.WriteLine("    legacyGenerateAnimations: 4");
        sw.WriteLine("    bakeSimulation: 0");
        sw.WriteLine("    resampleCurves: 1");
        sw.WriteLine("    optimizeGameObjects: 0");
        sw.WriteLine("    isReadable: 0");
        sw.WriteLine("  meshes:");
        sw.WriteLine("    lODScreenPercentages: []");
        sw.WriteLine("    globalScale: 1");
        sw.WriteLine("    meshCompression: 0");
        sw.WriteLine("    addColliders: 0");
        sw.WriteLine("    useSRGBMaterialColor: 1");
        sw.WriteLine("    sortHierarchyByName: 1");
        sw.WriteLine("    importVisibility: 1");
        sw.WriteLine("    importBlendShapes: 1");
        sw.WriteLine("    importCameras: 1");
        sw.WriteLine("    importLights: 1");
        sw.WriteLine("    fileIdsGeneration: 2");
        sw.WriteLine("    useFileUnits: 1");
        sw.WriteLine("    weldVertices: 1");
        sw.WriteLine("    indexFormat: 0");
        sw.WriteLine("  tangentSpace:");
        sw.WriteLine("    normalSmoothAngle: 60");
        sw.WriteLine("    normalImportMode: 0");
        sw.WriteLine("    tangentImportMode: 3");
        sw.WriteLine("    normalCalculationMode: 4");
        sw.WriteLine("  fileIDToRecycleName:");
        sw.Write("    4300000: ");
        sw.WriteLine(FormatYamlScalarString(meshName));
        sw.WriteLine("  userData: ");
        sw.WriteLine("  assetBundleName: ");
        sw.WriteLine("  assetBundleVariant: ");
    }

    private static void ExportTerrains(
        ExportState state,
        string meshesDir,
        string texturesDir,
        string materialsDir,
        CancellationToken cancellationToken)
    {
        var terrainOpts = new TerrainConversionOptions
        {
            MaxGridResolution = state.Options.MaxTerrainGridResolution,
            MaxBakedTextureSize = state.Options.MaxTerrainBakedTextureSize,
            ImageFormat = ImageFormat.Png,
            CompositeSplatmap = state.Options.ExportTextures
        };

        for (int i = 0; i < state.TerrainAssets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = state.TerrainAssets[i];
            var meshData = TerrainMeshConverter.BuildTerrainMeshData(asset.TerrainData, terrainOpts);
            if (meshData == null || meshData.Positions.Length == 0 || meshData.Indices.Length < 3)
            {
                continue;
            }

            asset.MeshData = meshData;

            // 1. Write Terrain OBJ + meta.
            var objPath = EnsureConfinedPath(state.RootDirWithSep, Path.Combine(meshesDir, asset.SafeName + ".obj"));
            WriteTerrainObjStreaming(objPath, asset.SafeName, meshData);
            WriteModelImporterMetaFile(EnsureConfinedPath(state.RootDirWithSep, objPath + ".meta"), asset.MeshGuid, asset.SafeName);

            // 2. Write baked splatmap PNG + meta (if present).
            if (state.Options.ExportTextures && meshData.BakedAlbedoBytes is { Length: > 0 })
            {
                var pngPath = EnsureConfinedPath(state.RootDirWithSep, Path.Combine(texturesDir, asset.SafeName + "_Albedo.png"));
                File.WriteAllBytes(pngPath, meshData.BakedAlbedoBytes);
                WriteTextureMetaFile(EnsureConfinedPath(state.RootDirWithSep, pngPath + ".meta"), asset.TextureGuid, isNormalMap: false);
                asset.HasBakedTexture = true;
            }

            // 3. Write Terrain Material .mat + meta.
            if (state.Options.ExportMaterials)
            {
                var matName = asset.SafeName + "_Mat";
                var matPath = EnsureConfinedPath(state.RootDirWithSep, Path.Combine(materialsDir, matName + ".mat"));
                WriteTerrainMaterialFile(matPath, matName, asset.HasBakedTexture ? asset.TextureGuid : null);
                WriteMaterialMetaFile(EnsureConfinedPath(state.RootDirWithSep, matPath + ".meta"), asset.MaterialGuid);
            }
        }
    }

    private static void WriteTerrainObjStreaming(string objPath, string meshName, TerrainMeshData meshData)
    {
        using var fs = new FileStream(objPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        sw.Write("# AssetStudio Unity Project Exporter (Terrain Heightmap)\n");
        sw.Write("o ");
        sw.WriteLine(meshName);

        for (int i = 0; i < meshData.Positions.Length; i++)
        {
            var p = meshData.Positions[i];
            sw.Write("v ");
            sw.Write(FormatFloat(-p.X));
            sw.Write(' ');
            sw.Write(FormatFloat(p.Y));
            sw.Write(' ');
            sw.WriteLine(FormatFloat(p.Z));
        }

        for (int i = 0; i < meshData.UVs.Length; i++)
        {
            var uv = meshData.UVs[i];
            sw.Write("vt ");
            sw.Write(FormatFloat(uv.X));
            sw.Write(' ');
            sw.WriteLine(FormatFloat(uv.Y));
        }

        for (int i = 0; i < meshData.Normals.Length; i++)
        {
            var n = meshData.Normals[i];
            sw.Write("vn ");
            sw.Write(FormatFloat(-n.X));
            sw.Write(' ');
            sw.Write(FormatFloat(n.Y));
            sw.Write(' ');
            sw.WriteLine(FormatFloat(n.Z));
        }

        sw.WriteLine("g submesh_0");
        sw.WriteLine("usemtl mat_0");

        int triCount = meshData.Indices.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            int idx = t * 3;
            int a = meshData.Indices[idx + 2] + 1;
            int b = meshData.Indices[idx + 1] + 1;
            int c = meshData.Indices[idx + 0] + 1;
            WriteObjFace(sw, a, b, c, hasUv: true, hasNormals: true);
        }
    }

    private static void WriteTerrainMaterialFile(string matPath, string matName, string? textureGuid)
    {
        using var fs = new FileStream(matPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        using var sw = new StreamWriter(fs, Utf8NoBom, 8192) { NewLine = "\n" };

        sw.WriteLine("%YAML 1.1");
        sw.WriteLine("%TAG !u! tag:unity3d.com,2011:");
        sw.WriteLine("--- !u!21 &2100000");
        sw.WriteLine("Material:");
        sw.WriteLine("  serializedVersion: 8");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_Name: ");
        sw.WriteLine(FormatYamlScalarString(matName));
        sw.WriteLine("  # OriginalShader: Nature/Terrain/Standard");
        sw.WriteLine("  m_Shader: {fileID: 46, guid: 0000000000000000f000000000000000, type: 0}");
        sw.WriteLine("  m_ValidKeywords: []");
        sw.WriteLine("  m_InvalidKeywords: []");
        sw.WriteLine("  m_LightmapFlags: 4");
        sw.WriteLine("  m_EnableInstancingVariants: 0");
        sw.WriteLine("  m_DoubleSidedGI: 0");
        sw.WriteLine("  m_CustomRenderQueue: -1");
        sw.WriteLine("  stringTagMap: {}");
        sw.WriteLine("  disabledShaderPasses: []");
        sw.WriteLine("  m_SavedProperties:");
        sw.WriteLine("    serializedVersion: 3");
        sw.WriteLine("    m_TexEnvs:");
        sw.WriteLine("    - _MainTex:");
        sw.Write("        m_Texture: ");
        sw.WriteLine(textureGuid != null ? $"{{fileID: 2800000, guid: {textureGuid}, type: 3}}" : "{fileID: 0}");
        sw.WriteLine("        m_Scale: {x: 1, y: 1}");
        sw.WriteLine("        m_Offset: {x: 0, y: 0}");
        sw.WriteLine("    m_Ints: []");
        sw.WriteLine("    m_Floats:");
        sw.WriteLine("    - _Glossiness: 0.1");
        sw.WriteLine("    - _Metallic: 0");
        sw.WriteLine("    m_Colors:");
        sw.WriteLine("    - _Color: {r: 1, g: 1, b: 1, a: 1}");
    }

    private static void ExportMaterials(ExportState state, string materialsDir, CancellationToken cancellationToken)
    {
        for (int i = 0; i < state.MaterialsToExport.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = state.MaterialsToExport[i];
            var matPath = EnsureConfinedPath(state.RootDirWithSep, Path.Combine(materialsDir, asset.SafeName + ".mat"));
            var metaPath = EnsureConfinedPath(state.RootDirWithSep, matPath + ".meta");

            WriteMaterialFileStreaming(matPath, asset);
            WriteMaterialMetaFile(metaPath, asset.Guid);
        }
    }

    private static void WriteMaterialFileStreaming(string matPath, ExportedMaterialAsset asset)
    {
        var mat = asset.Material;
        using var fs = new FileStream(matPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        int shaderFileId = 46; // Unity built-in Standard fallback
        if (!string.IsNullOrEmpty(asset.OriginalShaderName)
            && BuiltInUnityShaderFileIds.TryGetValue(asset.OriginalShaderName, out var knownId))
        {
            shaderFileId = knownId;
        }

        sw.WriteLine("%YAML 1.1");
        sw.WriteLine("%TAG !u! tag:unity3d.com,2011:");
        sw.WriteLine("--- !u!21 &2100000");
        sw.WriteLine("Material:");
        sw.WriteLine("  serializedVersion: 8");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_Name: ");
        sw.WriteLine(FormatYamlScalarString(string.IsNullOrEmpty(mat.m_Name) ? asset.SafeName : mat.m_Name));
        if (!string.IsNullOrEmpty(asset.OriginalShaderName))
        {
            sw.Write("  # OriginalShader: ");
            sw.WriteLine(SanitizeYamlComment(asset.OriginalShaderName));
        }
        sw.Write("  m_Shader: {fileID: ");
        sw.Write(shaderFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine(", guid: 0000000000000000f000000000000000, type: 0}");

        WriteYamlStringArray(sw, "  m_ValidKeywords", mat.m_ValidKeywords);
        WriteYamlStringArray(sw, "  m_InvalidKeywords", mat.m_InvalidKeywords);

        sw.Write("  m_LightmapFlags: ");
        sw.WriteLine(mat.m_LightmapFlags.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_EnableInstancingVariants: ");
        sw.WriteLine(mat.m_EnableInstancingVariants ? "1" : "0");
        sw.Write("  m_DoubleSidedGI: ");
        sw.WriteLine(mat.m_DoubleSidedGI ? "1" : "0");
        sw.Write("  m_CustomRenderQueue: ");
        sw.WriteLine(mat.m_CustomRenderQueue.ToString(CultureInfo.InvariantCulture));

        if (mat.stringTagMap == null || mat.stringTagMap.Length == 0)
        {
            sw.WriteLine("  stringTagMap: {}");
        }
        else
        {
            sw.WriteLine("  stringTagMap:");
            for (int i = 0; i < mat.stringTagMap.Length; i++)
            {
                sw.Write("    ");
                sw.Write(FormatYamlKey(mat.stringTagMap[i].Key));
                sw.Write(": ");
                sw.WriteLine(FormatYamlScalarString(mat.stringTagMap[i].Value));
            }
        }

        WriteYamlStringArray(sw, "  disabledShaderPasses", mat.disabledShaderPasses);

        sw.WriteLine("  m_SavedProperties:");
        sw.WriteLine("    serializedVersion: 3");

        if (asset.ResolvedTexEnvs.Count == 0)
        {
            sw.WriteLine("    m_TexEnvs: []");
        }
        else
        {
            sw.WriteLine("    m_TexEnvs:");
            for (int i = 0; i < asset.ResolvedTexEnvs.Count; i++)
            {
                var (propName, texAsset, scale, offset) = asset.ResolvedTexEnvs[i];
                sw.Write("    - ");
                sw.Write(FormatYamlKey(propName));
                sw.WriteLine(":");
                sw.Write("        m_Texture: ");
                if (texAsset != null && texAsset.Exported)
                {
                    sw.Write("{fileID: 2800000, guid: ");
                    sw.Write(texAsset.Guid);
                    sw.WriteLine(", type: 3}");
                }
                else
                {
                    sw.WriteLine("{fileID: 0}");
                }
                sw.Write("        m_Scale: {x: ");
                sw.Write(FormatFloat(scale.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(scale.Y));
                sw.WriteLine("}");
                sw.Write("        m_Offset: {x: ");
                sw.Write(FormatFloat(offset.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(offset.Y));
                sw.WriteLine("}");
            }
        }

        var ints = mat.m_SavedProperties?.m_Ints;
        if (ints == null || ints.Length == 0)
        {
            sw.WriteLine("    m_Ints: []");
        }
        else
        {
            sw.WriteLine("    m_Ints:");
            for (int i = 0; i < ints.Length; i++)
            {
                sw.Write("    - ");
                sw.Write(FormatYamlKey(ints[i].Key));
                sw.Write(": ");
                sw.WriteLine(ints[i].Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        var floats = mat.m_SavedProperties?.m_Floats;
        if (floats == null || floats.Length == 0)
        {
            sw.WriteLine("    m_Floats: []");
        }
        else
        {
            sw.WriteLine("    m_Floats:");
            for (int i = 0; i < floats.Length; i++)
            {
                sw.Write("    - ");
                sw.Write(FormatYamlKey(floats[i].Key));
                sw.Write(": ");
                sw.WriteLine(FormatFloat(floats[i].Value));
            }
        }

        var colors = mat.m_SavedProperties?.m_Colors;
        if (colors == null || colors.Length == 0)
        {
            sw.WriteLine("    m_Colors: []");
        }
        else
        {
            sw.WriteLine("    m_Colors:");
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors[i].Value;
                sw.Write("    - ");
                sw.Write(FormatYamlKey(colors[i].Key));
                sw.Write(": {r: ");
                sw.Write(FormatFloat(c.R));
                sw.Write(", g: ");
                sw.Write(FormatFloat(c.G));
                sw.Write(", b: ");
                sw.Write(FormatFloat(c.B));
                sw.Write(", a: ");
                sw.Write(FormatFloat(c.A));
                sw.WriteLine("}");
            }
        }
    }

    private static void WriteMaterialMetaFile(string metaPath, string guid)
    {
        using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        using var sw = new StreamWriter(fs, Utf8NoBom, 4096) { NewLine = "\n" };

        sw.WriteLine("fileFormatVersion: 2");
        sw.Write("guid: ");
        sw.WriteLine(guid);
        sw.WriteLine("NativeFormatImporter:");
        sw.WriteLine("  externalObjects: {}");
        sw.WriteLine("  mainObjectFileID: 2100000");
        sw.WriteLine("  userData: ");
        sw.WriteLine("  assetBundleName: ");
        sw.WriteLine("  assetBundleVariant: ");
    }

    // =========================================================================
    // Streaming Unity YAML 1.1 Scene Writer (.unity)
    // =========================================================================

    private static void WriteUnitySceneFile(string scenePath, ExportState state, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(scenePath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        sw.WriteLine("%YAML 1.1");
        sw.WriteLine("%TAG !u! tag:unity3d.com,2011:");

        WriteSceneSettingsHeader(sw);

        for (int i = 0; i < state.SceneNodes.Count; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = state.SceneNodes[i];
            WriteGameObjectYaml(sw, node);

            for (int c = 0; c < node.Components.Count; c++)
            {
                WriteComponentYaml(sw, node, node.Components[c], state);
            }
        }
    }

    private static void WriteSceneSettingsHeader(StreamWriter sw)
    {
        sw.WriteLine("--- !u!29 &1");
        sw.WriteLine("OcclusionCullingSettings:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  serializedVersion: 2");
        sw.WriteLine("  m_OcclusionBakeSettings:");
        sw.WriteLine("    smallestOccluder: 5");
        sw.WriteLine("    smallestHole: 0.25");
        sw.WriteLine("    backfaceThreshold: 100");
        sw.WriteLine("  m_SceneGUID: 00000000000000000000000000000000");
        sw.WriteLine("  m_OcclusionCullingData: {fileID: 0}");

        sw.WriteLine("--- !u!104 &2");
        sw.WriteLine("RenderSettings:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  serializedVersion: 9");
        sw.WriteLine("  m_Fog: 0");
        sw.WriteLine("  m_FogColor: {r: 0.5, g: 0.5, b: 0.5, a: 1}");
        sw.WriteLine("  m_FogMode: 3");
        sw.WriteLine("  m_FogDensity: 0.01");
        sw.WriteLine("  m_LinearFogStart: 0");
        sw.WriteLine("  m_LinearFogEnd: 300");
        sw.WriteLine("  m_AmbientSkyColor: {r: 0.212, g: 0.227, b: 0.259, a: 1}");
        sw.WriteLine("  m_AmbientEquatorColor: {r: 0.114, g: 0.125, b: 0.133, a: 1}");
        sw.WriteLine("  m_AmbientGroundColor: {r: 0.047, g: 0.043, b: 0.035, a: 1}");
        sw.WriteLine("  m_AmbientIntensity: 1");
        sw.WriteLine("  m_AmbientMode: 0");
        sw.WriteLine("  m_SubtractiveShadowColor: {r: 0.42, g: 0.478, b: 0.627, a: 1}");
        sw.WriteLine("  m_SkyboxMaterial: {fileID: 0}");
        sw.WriteLine("  m_HaloStrength: 0.5");
        sw.WriteLine("  m_FlareStrength: 1");
        sw.WriteLine("  m_FlareFadeSpeed: 3");
        sw.WriteLine("  m_HaloTexture: {fileID: 0}");
        sw.WriteLine("  m_SpotCookie: {fileID: 0}");
        sw.WriteLine("  m_DefaultReflectionMode: 0");
        sw.WriteLine("  m_DefaultReflectionResolution: 128");
        sw.WriteLine("  m_ReflectionBounces: 1");
        sw.WriteLine("  m_ReflectionIntensity: 1");
        sw.WriteLine("  m_CustomReflection: {fileID: 0}");
        sw.WriteLine("  m_Sun: {fileID: 0}");

        sw.WriteLine("--- !u!157 &3");
        sw.WriteLine("LightmapSettings:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  serializedVersion: 12");
        sw.WriteLine("  m_GIWorkflowMode: 1");
        sw.WriteLine("  m_GISettings:");
        sw.WriteLine("    serializedVersion: 2");
        sw.WriteLine("    m_BounceScale: 1");
        sw.WriteLine("    m_IndirectOutputScale: 1");
        sw.WriteLine("    m_AlbedoBoost: 1");
        sw.WriteLine("    m_EnvironmentLightingMode: 0");
        sw.WriteLine("    m_EnableBakedLightmaps: 1");
        sw.WriteLine("    m_EnableRealtimeLightmaps: 0");
        sw.WriteLine("  m_LightmapEditorSettings:");
        sw.WriteLine("    serializedVersion: 12");
        sw.WriteLine("    m_Resolution: 2");
        sw.WriteLine("    m_BakeResolution: 40");
        sw.WriteLine("  m_LightingDataAsset: {fileID: 0}");
        sw.WriteLine("  m_LightingSettings: {fileID: 0}");

        sw.WriteLine("--- !u!196 &4");
        sw.WriteLine("NavMeshSettings:");
        sw.WriteLine("  serializedVersion: 2");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_BuildSettings:");
        sw.WriteLine("    serializedVersion: 3");
        sw.WriteLine("    agentTypeID: 0");
        sw.WriteLine("    agentRadius: 0.5");
        sw.WriteLine("    agentHeight: 2");
        sw.WriteLine("    agentSlope: 45");
        sw.WriteLine("    agentClimb: 0.4");
        sw.WriteLine("  m_NavMeshData: {fileID: 0}");
    }

    private static void WriteGameObjectYaml(StreamWriter sw, SceneNodeRecord node)
    {
        var go = node.GameObject;
        sw.Write("--- !u!1 &");
        sw.WriteLine(node.GameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("GameObject:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.WriteLine("  serializedVersion: 6");
        if (node.Components.Count == 0)
        {
            sw.WriteLine("  m_Component: []");
        }
        else
        {
            sw.WriteLine("  m_Component:");
            for (int i = 0; i < node.Components.Count; i++)
            {
                sw.Write("  - component: {fileID: ");
                sw.Write(node.Components[i].FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("}");
            }
        }
        sw.Write("  m_Layer: ");
        sw.WriteLine(go.m_Layer.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_Name: ");
        sw.WriteLine(FormatYamlScalarString(string.IsNullOrEmpty(go.m_Name) ? $"GameObject_{go.m_PathID}" : go.m_Name));
        sw.Write("  m_TagString: ");
        sw.WriteLine(FormatYamlScalarString(ResolveUnityTagString(go.m_Tag)));
        sw.WriteLine("  m_Icon: {fileID: 0}");
        sw.WriteLine("  m_NavMeshLayer: 0");
        sw.WriteLine("  m_StaticEditorFlags: 0");
        sw.Write("  m_IsActive: ");
        sw.WriteLine(go.m_IsActive ? "1" : "0");
    }

    private static void WriteComponentYaml(
        StreamWriter sw,
        SceneNodeRecord node,
        ExportedComponentRecord comp,
        ExportState state)
    {
        switch (comp.Kind)
        {
            case ExportedComponentKind.Transform:
            case ExportedComponentKind.RectTransform:
                WriteTransformYaml(sw, node, comp, state);
                break;
            case ExportedComponentKind.MeshFilter:
                WriteMeshFilterYaml(sw, comp);
                break;
            case ExportedComponentKind.MeshRenderer:
                WriteMeshRendererYaml(sw, comp);
                break;
            case ExportedComponentKind.SkinnedMeshRenderer:
                WriteSkinnedMeshRendererYaml(sw, comp, state);
                break;
            case ExportedComponentKind.Camera:
                WriteCameraYaml(sw, comp);
                break;
            case ExportedComponentKind.Light:
                WriteLightYaml(sw, comp);
                break;
            case ExportedComponentKind.LODGroup:
                WriteLodGroupYaml(sw, comp, state);
                break;
            case ExportedComponentKind.BoxCollider:
            case ExportedComponentKind.SphereCollider:
            case ExportedComponentKind.CapsuleCollider:
            case ExportedComponentKind.MeshCollider:
                WriteColliderYaml(sw, comp, state);
                break;
            case ExportedComponentKind.MonoBehaviour:
                WriteMonoBehaviourYaml(sw, comp, state);
                break;
            case ExportedComponentKind.TerrainMeshFilter:
                WriteTerrainMeshFilterYaml(sw, comp);
                break;
            case ExportedComponentKind.TerrainMeshRenderer:
                WriteTerrainMeshRendererYaml(sw, comp);
                break;
        }
    }

    private static void WriteTransformYaml(StreamWriter sw, SceneNodeRecord node, ExportedComponentRecord comp, ExportState state)
    {
        var tr = (Transform)comp.SourceObject!;
        bool isRect = tr is RectTransform;

        sw.Write(isRect ? "--- !u!224 &" : "--- !u!4 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine(isRect ? "RectTransform:" : "Transform:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_LocalRotation: {x: ");
        sw.Write(FormatFloat(tr.m_LocalRotation.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(tr.m_LocalRotation.Y));
        sw.Write(", z: ");
        sw.Write(FormatFloat(tr.m_LocalRotation.Z));
        sw.Write(", w: ");
        sw.Write(FormatFloat(tr.m_LocalRotation.W, 1f));
        sw.WriteLine("}");
        sw.Write("  m_LocalPosition: {x: ");
        sw.Write(FormatFloat(tr.m_LocalPosition.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(tr.m_LocalPosition.Y));
        sw.Write(", z: ");
        sw.Write(FormatFloat(tr.m_LocalPosition.Z));
        sw.WriteLine("}");
        sw.Write("  m_LocalScale: {x: ");
        sw.Write(FormatFloat(tr.m_LocalScale.X, 1f));
        sw.Write(", y: ");
        sw.Write(FormatFloat(tr.m_LocalScale.Y, 1f));
        sw.Write(", z: ");
        sw.Write(FormatFloat(tr.m_LocalScale.Z, 1f));
        sw.WriteLine("}");

        var childIds = new List<long>();
        if (tr.m_Children != null)
        {
            for (int i = 0; i < tr.m_Children.Length; i++)
            {
                var childPtr = tr.m_Children[i];
                if (childPtr != null && !childPtr.IsNull && TryLookupLocalFileId(tr.assetsFile, childPtr.m_FileID, childPtr.m_PathID, state, out long childId))
                {
                    childIds.Add(childId);
                }
            }
        }

        if (childIds.Count == 0)
        {
            sw.WriteLine("  m_Children: []");
        }
        else
        {
            sw.WriteLine("  m_Children:");
            for (int i = 0; i < childIds.Count; i++)
            {
                sw.Write("  - {fileID: ");
                sw.Write(childIds[i].ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("}");
            }
        }

        long fatherId = 0;
        if (tr.m_Father != null && !tr.m_Father.IsNull)
        {
            TryLookupLocalFileId(tr.assetsFile, tr.m_Father.m_FileID, tr.m_Father.m_PathID, state, out fatherId);
        }

        sw.Write("  m_Father: {fileID: ");
        sw.Write(fatherId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_RootOrder: ");
        sw.WriteLine(node.RootOrder.ToString(CultureInfo.InvariantCulture));

        if (tr is RectTransform rt)
        {
            sw.Write("  m_AnchorMin: {x: ");
            sw.Write(FormatFloat(rt.m_AnchorMin.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_AnchorMin.Y));
            sw.WriteLine("}");
            sw.Write("  m_AnchorMax: {x: ");
            sw.Write(FormatFloat(rt.m_AnchorMax.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_AnchorMax.Y));
            sw.WriteLine("}");
            sw.Write("  m_AnchoredPosition: {x: ");
            sw.Write(FormatFloat(rt.m_AnchoredPosition.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_AnchoredPosition.Y));
            sw.WriteLine("}");
            sw.Write("  m_SizeDelta: {x: ");
            sw.Write(FormatFloat(rt.m_SizeDelta.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_SizeDelta.Y));
            sw.WriteLine("}");
            sw.Write("  m_Pivot: {x: ");
            sw.Write(FormatFloat(rt.m_Pivot.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_Pivot.Y));
            sw.WriteLine("}");
        }
    }

    private static void WriteMeshFilterYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        sw.Write("--- !u!33 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshFilter:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Mesh: ");
        if (comp.ResolvedMesh != null && comp.ResolvedMesh.Exported)
        {
            sw.Write("{fileID: 4300000, guid: ");
            sw.Write(comp.ResolvedMesh.Guid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }
    }

    private static void WriteMeshRendererYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var mr = (MeshRenderer)comp.SourceObject!;
        sw.Write("--- !u!23 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshRenderer:");
        WriteCommonRendererProperties(sw, comp.OwnerGameObjectFileId, mr, comp.ResolvedMaterials);
    }

    private static void WriteSkinnedMeshRendererYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var smr = (SkinnedMeshRenderer)comp.SourceObject!;
        sw.Write("--- !u!137 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("SkinnedMeshRenderer:");
        WriteCommonRendererProperties(sw, comp.OwnerGameObjectFileId, smr, comp.ResolvedMaterials);

        sw.Write("  m_Mesh: ");
        if (comp.ResolvedMesh != null && comp.ResolvedMesh.Exported)
        {
            sw.Write("{fileID: 4300000, guid: ");
            sw.Write(comp.ResolvedMesh.Guid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }

        if (smr.m_Bones == null || smr.m_Bones.Length == 0)
        {
            sw.WriteLine("  m_Bones: []");
        }
        else
        {
            sw.WriteLine("  m_Bones:");
            for (int i = 0; i < smr.m_Bones.Length; i++)
            {
                var bonePtr = smr.m_Bones[i];
                long boneId = 0;
                if (bonePtr != null && !bonePtr.IsNull)
                {
                    TryLookupLocalFileId(smr.assetsFile, bonePtr.m_FileID, bonePtr.m_PathID, state, out boneId);
                }
                sw.Write("  - {fileID: ");
                sw.Write(boneId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("}");
            }
        }

        long rootBoneId = 0;
        if (smr.m_RootBone != null && !smr.m_RootBone.IsNull)
        {
            TryLookupLocalFileId(smr.assetsFile, smr.m_RootBone.m_FileID, smr.m_RootBone.m_PathID, state, out rootBoneId);
        }
        sw.Write("  m_RootBone: {fileID: ");
        sw.Write(rootBoneId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
    }

    private static void WriteCommonRendererProperties(
        StreamWriter sw,
        long ownerGoId,
        Renderer renderer,
        List<ExportedMaterialAsset?>? materials)
    {
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(ownerGoId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(renderer.m_Enabled ? "1" : "0");
        sw.Write("  m_CastShadows: ");
        sw.WriteLine(renderer.m_CastShadows.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_ReceiveShadows: ");
        sw.WriteLine(renderer.m_ReceiveShadows.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("  m_DynamicOccludee: 1");
        sw.WriteLine("  m_MotionVectors: 1");
        sw.WriteLine("  m_LightProbeUsage: 1");
        sw.WriteLine("  m_ReflectionProbeUsage: 1");
        sw.Write("  m_RenderingLayerMask: ");
        sw.WriteLine((renderer.m_RenderingLayerMask == 0 ? 1u : renderer.m_RenderingLayerMask).ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_RendererPriority: ");
        sw.WriteLine(renderer.m_RendererPriority.ToString(CultureInfo.InvariantCulture));

        if (materials == null || materials.Count == 0)
        {
            sw.WriteLine("  m_Materials: []");
        }
        else
        {
            sw.WriteLine("  m_Materials:");
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                if (mat != null)
                {
                    sw.Write("  - {fileID: 2100000, guid: ");
                    sw.Write(mat.Guid);
                    sw.WriteLine(", type: 2}");
                }
                else
                {
                    sw.WriteLine("  - {fileID: 0}");
                }
            }
        }

        sw.Write("  m_SortingLayerID: 0\n");
        sw.Write("  m_SortingLayer: ");
        sw.WriteLine(renderer.m_SortingLayer.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_SortingOrder: ");
        sw.WriteLine(renderer.m_SortingOrder.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteTerrainMeshFilterYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var terrainAsset = comp.ResolvedTerrain!;
        var size = terrainAsset.TerrainData.m_Heightmap.TerrainSize;

        sw.Write("--- !u!33 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshFilter:");
        sw.Write("  # OriginalComponent: Terrain (TerrainData: ");
        sw.Write(SanitizeYamlComment(terrainAsset.TerrainData.m_Name ?? terrainAsset.SafeName));
        sw.Write(", resolution: ");
        sw.Write(terrainAsset.TerrainData.m_Heightmap.EffectiveWidth.ToString(CultureInfo.InvariantCulture));
        sw.Write('x');
        sw.Write(terrainAsset.TerrainData.m_Heightmap.EffectiveHeight.ToString(CultureInfo.InvariantCulture));
        sw.Write(", size: ");
        sw.Write(FormatFloat(size.X));
        sw.Write('x');
        sw.Write(FormatFloat(size.Y));
        sw.Write('x');
        sw.Write(FormatFloat(size.Z));
        sw.WriteLine(")");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Mesh: ");
        if (terrainAsset.MeshData != null)
        {
            sw.Write("{fileID: 4300000, guid: ");
            sw.Write(terrainAsset.MeshGuid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }
    }

    private static void WriteTerrainMeshRendererYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var terrainAsset = comp.ResolvedTerrain!;
        var terrain = terrainAsset.Terrain;
        sw.Write("--- !u!23 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshRenderer:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(terrain.Enabled && terrain.m_DrawHeightmap ? "1" : "0");
        sw.Write("  m_CastShadows: ");
        sw.WriteLine(Math.Clamp(terrain.m_ShadowCastingMode, 0, 3).ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("  m_ReceiveShadows: 1");
        sw.WriteLine("  m_Materials:");
        sw.Write("  - {fileID: 2100000, guid: ");
        sw.Write(terrainAsset.MaterialGuid);
        sw.WriteLine(", type: 2}");
    }

    private static void WriteCameraYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var cam = (Camera)comp.SourceObject!;
        sw.Write("--- !u!20 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("Camera:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(cam.Enabled ? "1" : "0");
        sw.WriteLine("  serializedVersion: 2");
        sw.Write("  m_ClearFlags: ");
        sw.WriteLine(cam.m_ClearFlags.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_BackGroundColor: {r: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.R));
        sw.Write(", g: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.G));
        sw.Write(", b: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.B));
        sw.Write(", a: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.A));
        sw.WriteLine("}");
        sw.Write("  m_projectionMatrixMode: ");
        sw.WriteLine(cam.m_ProjectionMatrixMode.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_GateFitMode: ");
        sw.WriteLine(cam.m_GateFitMode.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_SensorSize: {x: ");
        sw.Write(FormatFloat(cam.m_SensorSize.X, 36f));
        sw.Write(", y: ");
        sw.Write(FormatFloat(cam.m_SensorSize.Y, 24f));
        sw.WriteLine("}");
        sw.Write("  m_LensShift: {x: ");
        sw.Write(FormatFloat(cam.m_LensShift.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(cam.m_LensShift.Y));
        sw.WriteLine("}");
        sw.Write("  m_FocalLength: ");
        sw.WriteLine(FormatFloat(cam.m_FocalLength, 50f));
        sw.Write("  m_NormalizedViewPortRect:");
        sw.WriteLine();
        sw.WriteLine("    serializedVersion: 2");
        sw.Write("    x: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.X));
        sw.Write("    y: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.Y));
        sw.Write("    width: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.Z, 1f));
        sw.Write("    height: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.W, 1f));
        sw.Write("  near clip plane: ");
        sw.WriteLine(FormatFloat(cam.m_NearClipPlane, 0.3f));
        sw.Write("  far clip plane: ");
        sw.WriteLine(FormatFloat(cam.m_FarClipPlane, 1000f));
        sw.Write("  field of view: ");
        sw.WriteLine(FormatFloat(cam.m_FieldOfView, 60f));
        sw.Write("  orthographic: ");
        sw.WriteLine(cam.m_Orthographic ? "1" : "0");
        sw.Write("  orthographic size: ");
        sw.WriteLine(FormatFloat(cam.m_OrthographicSize, 5f));
        sw.Write("  m_Depth: ");
        sw.WriteLine(FormatFloat(cam.m_Depth));
        sw.WriteLine("  m_CullingMask:");
        sw.WriteLine("    serializedVersion: 2");
        sw.Write("    m_Bits: ");
        sw.WriteLine(cam.m_CullingMask.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_RenderingPath: ");
        sw.WriteLine(cam.m_RenderingPath.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteLightYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var light = (Light)comp.SourceObject!;
        var shadows = light.m_Shadows ?? new ShadowSettings();

        sw.Write("--- !u!108 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("Light:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(light.Enabled ? "1" : "0");
        sw.WriteLine("  serializedVersion: 10");
        sw.Write("  m_Type: ");
        sw.WriteLine(((int)light.m_Type).ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_Shape: ");
        sw.WriteLine(light.m_Shape.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_Color: {r: ");
        sw.Write(FormatFloat(light.m_Color.R, 1f));
        sw.Write(", g: ");
        sw.Write(FormatFloat(light.m_Color.G, 1f));
        sw.Write(", b: ");
        sw.Write(FormatFloat(light.m_Color.B, 1f));
        sw.Write(", a: ");
        sw.Write(FormatFloat(light.m_Color.A, 1f));
        sw.WriteLine("}");
        sw.Write("  m_Intensity: ");
        sw.WriteLine(FormatFloat(light.m_Intensity, 1f));
        sw.Write("  m_Range: ");
        sw.WriteLine(FormatFloat(light.m_Range, 10f));
        sw.Write("  m_SpotAngle: ");
        sw.WriteLine(FormatFloat(light.m_SpotAngle, 30f));
        sw.Write("  m_InnerSpotAngle: ");
        sw.WriteLine(FormatFloat(light.m_InnerSpotAngle));
        sw.Write("  m_CookieSize: ");
        sw.WriteLine(FormatFloat(light.m_CookieSize, 10f));
        sw.WriteLine("  m_Shadows:");
        sw.Write("    m_Type: ");
        sw.WriteLine(shadows.m_Type.ToString(CultureInfo.InvariantCulture));
        sw.Write("    m_Resolution: ");
        sw.WriteLine(shadows.m_Resolution.ToString(CultureInfo.InvariantCulture));
        sw.Write("    m_CustomResolution: ");
        sw.WriteLine(shadows.m_CustomResolution.ToString(CultureInfo.InvariantCulture));
        sw.Write("    m_Strength: ");
        sw.WriteLine(FormatFloat(shadows.m_Strength, 1f));
        sw.Write("    m_Bias: ");
        sw.WriteLine(FormatFloat(shadows.m_Bias, 0.05f));
        sw.Write("    m_NormalBias: ");
        sw.WriteLine(FormatFloat(shadows.m_NormalBias, 0.4f));
        sw.Write("    m_NearPlane: ");
        sw.WriteLine(FormatFloat(shadows.m_NearPlane, 0.2f));
    }

    private static void WriteLodGroupYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var lodGroup = (LODGroup)comp.SourceObject!;
        sw.Write("--- !u!205 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("LODGroup:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_LocalReferencePoint: {x: ");
        sw.Write(FormatFloat(lodGroup.m_LocalReferencePoint.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(lodGroup.m_LocalReferencePoint.Y));
        sw.Write(", z: ");
        sw.Write(FormatFloat(lodGroup.m_LocalReferencePoint.Z));
        sw.WriteLine("}");
        sw.Write("  m_Size: ");
        sw.WriteLine(FormatFloat(lodGroup.m_Size, 1f));
        sw.Write("  m_FadeMode: ");
        sw.WriteLine(lodGroup.m_FadeMode.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_AnimateCrossFading: ");
        sw.WriteLine(lodGroup.m_AnimateCrossFading ? "1" : "0");
        sw.Write("  m_LastLODIsBillboard: ");
        sw.WriteLine(lodGroup.m_LastLODIsBillboard ? "1" : "0");

        if (lodGroup.m_LODs == null || lodGroup.m_LODs.Length == 0)
        {
            sw.WriteLine("  m_LODs: []");
        }
        else
        {
            sw.WriteLine("  m_LODs:");
            for (int l = 0; l < lodGroup.m_LODs.Length; l++)
            {
                var lod = lodGroup.m_LODs[l];
                if (lod == null)
                {
                    continue;
                }

                sw.Write("  - screenRelativeHeight: ");
                sw.WriteLine(FormatFloat(lod.screenRelativeHeight));
                sw.Write("    fadeTransitionWidth: ");
                sw.WriteLine(FormatFloat(lod.fadeTransitionWidth));

                var rendIds = new List<long>();
                if (lod.renderers != null)
                {
                    for (int r = 0; r < lod.renderers.Length; r++)
                    {
                        var rPtr = lod.renderers[r];
                        if (rPtr != null && !rPtr.IsNull && TryLookupLocalFileId(lodGroup.assetsFile, rPtr.m_FileID, rPtr.m_PathID, state, out long rendId))
                        {
                            rendIds.Add(rendId);
                        }
                    }
                }

                if (rendIds.Count == 0)
                {
                    sw.WriteLine("    renderers: []");
                }
                else
                {
                    sw.WriteLine("    renderers:");
                    for (int r = 0; r < rendIds.Count; r++)
                    {
                        sw.Write("    - renderer: {fileID: ");
                        sw.Write(rendIds[r].ToString(CultureInfo.InvariantCulture));
                        sw.WriteLine("}");
                    }
                }
            }
        }

        sw.Write("  m_Enabled: ");
        sw.WriteLine(lodGroup.m_Enabled ? "1" : "0");
    }

    private static void WriteColliderYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var obj = comp.SourceObject!;
        var fields = ReadColliderFields(obj, state);

        bool enabled = fields == null || !TryReadBool(fields, "m_Enabled", out var en) || en;
        bool isTrigger = fields != null && TryReadBool(fields, "m_IsTrigger", out var trig) && trig;

        switch (comp.Kind)
        {
            case ExportedComponentKind.BoxCollider:
            {
                var size = fields != null && TryReadVector3(fields, "m_Size", out var s) ? s : new Vector3(1f, 1f, 1f);
                var center = fields != null && TryReadVector3(fields, "m_Center", out var c) ? c : Vector3.Zero;
                sw.Write("--- !u!65 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("BoxCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Size: {x: ");
                sw.Write(FormatFloat(size.X, 1f));
                sw.Write(", y: ");
                sw.Write(FormatFloat(size.Y, 1f));
                sw.Write(", z: ");
                sw.Write(FormatFloat(size.Z, 1f));
                sw.WriteLine("}");
                sw.Write("  m_Center: {x: ");
                sw.Write(FormatFloat(center.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(center.Y));
                sw.Write(", z: ");
                sw.Write(FormatFloat(center.Z));
                sw.WriteLine("}");
                break;
            }

            case ExportedComponentKind.SphereCollider:
            {
                float radius = fields != null && TryReadFloat(fields, "m_Radius", out var r) ? r : 0.5f;
                var center = fields != null && TryReadVector3(fields, "m_Center", out var c) ? c : Vector3.Zero;
                sw.Write("--- !u!135 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("SphereCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Radius: ");
                sw.WriteLine(FormatFloat(radius, 0.5f));
                sw.Write("  m_Center: {x: ");
                sw.Write(FormatFloat(center.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(center.Y));
                sw.Write(", z: ");
                sw.Write(FormatFloat(center.Z));
                sw.WriteLine("}");
                break;
            }

            case ExportedComponentKind.CapsuleCollider:
            {
                float radius = fields != null && TryReadFloat(fields, "m_Radius", out var r) ? r : 0.5f;
                float height = fields != null && TryReadFloat(fields, "m_Height", out var h) ? h : 2f;
                int direction = fields != null && TryReadInt(fields, "m_Direction", out var d) ? d : 1;
                var center = fields != null && TryReadVector3(fields, "m_Center", out var c) ? c : Vector3.Zero;
                sw.Write("--- !u!136 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("CapsuleCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Radius: ");
                sw.WriteLine(FormatFloat(radius, 0.5f));
                sw.Write("  m_Height: ");
                sw.WriteLine(FormatFloat(height, 2f));
                sw.Write("  m_Direction: ");
                sw.WriteLine(direction.ToString(CultureInfo.InvariantCulture));
                sw.Write("  m_Center: {x: ");
                sw.Write(FormatFloat(center.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(center.Y));
                sw.Write(", z: ");
                sw.Write(FormatFloat(center.Z));
                sw.WriteLine("}");
                break;
            }

            case ExportedComponentKind.MeshCollider:
            {
                bool convex = fields != null && TryReadBool(fields, "m_Convex", out var cv) && cv;
                int cookingOptions = fields != null && TryReadInt(fields, "m_CookingOptions", out var co) ? co : 30;
                sw.Write("--- !u!64 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("MeshCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Convex: ");
                sw.WriteLine(convex ? "1" : "0");
                sw.Write("  m_CookingOptions: ");
                sw.WriteLine(cookingOptions.ToString(CultureInfo.InvariantCulture));
                sw.Write("  m_Mesh: ");
                if (comp.ResolvedMesh != null && comp.ResolvedMesh.Exported)
                {
                    sw.Write("{fileID: 4300000, guid: ");
                    sw.Write(comp.ResolvedMesh.Guid);
                    sw.WriteLine(", type: 3}");
                }
                else
                {
                    sw.WriteLine("{fileID: 0}");
                }
                break;
            }
        }
    }

    private static void WriteCommonColliderHeader(StreamWriter sw, long ownerGoId, bool enabled, bool isTrigger)
    {
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(ownerGoId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.WriteLine("  m_Material: {fileID: 0}");
        sw.Write("  m_IsTrigger: ");
        sw.WriteLine(isTrigger ? "1" : "0");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(enabled ? "1" : "0");
    }

    private static void WriteMonoBehaviourYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var mb = (MonoBehaviour)comp.SourceObject!;
        sw.Write("--- !u!114 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MonoBehaviour:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(mb.m_Enabled != 0 ? "1" : "0");
        sw.WriteLine("  m_EditorHideFlags: 0");
        sw.Write("  m_Script: ");
        if (!string.IsNullOrEmpty(comp.MonoScriptGuid))
        {
            sw.Write("{fileID: 11500000, guid: ");
            sw.Write(comp.MonoScriptGuid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }
        sw.Write("  m_Name: ");
        sw.WriteLine(FormatYamlScalarString(mb.m_Name ?? string.Empty));
        sw.WriteLine("  m_EditorClassIdentifier: ");

        var fields = ReadMonoBehaviourFields(mb, state);
        if (fields != null && fields.Count > 0)
        {
            foreach (DictionaryEntry entry in fields)
            {
                var key = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(key) || MonoBehaviourHeaderFields.Contains(key))
                {
                    continue;
                }

                WriteYamlProperty(sw, key, entry.Value, indentSpaces: 2, depth: 0, mb.assetsFile, state);
            }
        }
    }

    // =========================================================================
    // Recursive Unity YAML Serializer for MonoBehaviour TypeTree Fields
    // =========================================================================

    private static void WriteYamlProperty(
        StreamWriter sw,
        string key,
        object? value,
        int indentSpaces,
        int depth,
        SerializedFile? ownerFile,
        ExportState state)
    {
        if (depth > MaxYamlSerializationDepth)
        {
            return;
        }

        WriteIndent(sw, indentSpaces);
        sw.Write(FormatYamlKey(key));
        sw.Write(':');

        if (value == null)
        {
            sw.WriteLine(" ");
            return;
        }

        if (TryFormatInlineScalarOrStruct(value, ownerFile, state, out var inlineText))
        {
            sw.Write(' ');
            sw.WriteLine(inlineText);
            return;
        }

        if (value is byte[] bytes)
        {
            int count = Math.Min(bytes.Length, MaxYamlCollectionElements);
            if (count == 0)
            {
                sw.WriteLine(" []");
            }
            else
            {
                sw.Write(' ');
                sw.WriteLine(Convert.ToHexString(bytes.AsSpan(0, count)).ToLowerInvariant());
            }
            return;
        }

        if (value is IDictionary dict)
        {
            if (dict.Count == 0)
            {
                sw.WriteLine(" {}");
                return;
            }

            sw.WriteLine();
            int written = 0;
            foreach (DictionaryEntry child in dict)
            {
                if (written >= MaxYamlCollectionElements)
                {
                    break;
                }

                var childKey = child.Key?.ToString();
                if (string.IsNullOrWhiteSpace(childKey))
                {
                    continue;
                }

                WriteYamlProperty(sw, childKey, child.Value, indentSpaces + 2, depth + 1, ownerFile, state);
                written++;
            }
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var items = new List<object?>();
            foreach (var item in enumerable)
            {
                if (items.Count >= MaxYamlCollectionElements)
                {
                    break;
                }
                items.Add(item);
            }

            if (items.Count == 0)
            {
                sw.WriteLine(" []");
                return;
            }

            sw.WriteLine();
            for (int i = 0; i < items.Count; i++)
            {
                WriteYamlListItem(sw, items[i], indentSpaces, depth + 1, ownerFile, state);
            }
            return;
        }

        sw.Write(' ');
        sw.WriteLine(FormatYamlScalarString(value.ToString() ?? string.Empty));
    }

    private static void WriteYamlListItem(
        StreamWriter sw,
        object? item,
        int indentSpaces,
        int depth,
        SerializedFile? ownerFile,
        ExportState state)
    {
        if (depth > MaxYamlSerializationDepth)
        {
            return;
        }

        WriteIndent(sw, indentSpaces);
        if (item == null)
        {
            sw.WriteLine("- ");
            return;
        }

        if (TryFormatInlineScalarOrStruct(item, ownerFile, state, out var inlineText))
        {
            sw.Write("- ");
            sw.WriteLine(inlineText);
            return;
        }

        if (item is IDictionary dict && dict.Count > 0)
        {
            bool first = true;
            int written = 0;
            foreach (DictionaryEntry child in dict)
            {
                if (written >= MaxYamlCollectionElements)
                {
                    break;
                }

                var childKey = child.Key?.ToString();
                if (string.IsNullOrWhiteSpace(childKey))
                {
                    continue;
                }

                if (first)
                {
                    sw.Write("- ");
                    sw.Write(FormatYamlKey(childKey));
                    sw.Write(':');
                    if (child.Value != null && TryFormatInlineScalarOrStruct(child.Value, ownerFile, state, out var firstInline))
                    {
                        sw.Write(' ');
                        sw.WriteLine(firstInline);
                    }
                    else if (child.Value == null)
                    {
                        sw.WriteLine(" ");
                    }
                    else
                    {
                        sw.WriteLine();
                        WriteYamlNestedValue(sw, child.Value, indentSpaces + 4, depth + 1, ownerFile, state);
                    }
                    first = false;
                }
                else
                {
                    WriteYamlProperty(sw, childKey, child.Value, indentSpaces + 2, depth + 1, ownerFile, state);
                }

                written++;
            }

            if (first)
            {
                sw.WriteLine("- {}");
            }
            return;
        }

        sw.Write("- ");
        sw.WriteLine(FormatYamlScalarString(item.ToString() ?? string.Empty));
    }

    private static void WriteYamlNestedValue(
        StreamWriter sw,
        object value,
        int indentSpaces,
        int depth,
        SerializedFile? ownerFile,
        ExportState state)
    {
        if (depth > MaxYamlSerializationDepth)
        {
            return;
        }

        if (value is IDictionary dict)
        {
            int written = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (written >= MaxYamlCollectionElements) break;
                var k = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(k)) continue;
                WriteYamlProperty(sw, k, entry.Value, indentSpaces, depth + 1, ownerFile, state);
                written++;
            }
        }
        else if (value is IEnumerable enumerable && value is not string)
        {
            int written = 0;
            foreach (var elem in enumerable)
            {
                if (written >= MaxYamlCollectionElements) break;
                WriteYamlListItem(sw, elem, indentSpaces, depth + 1, ownerFile, state);
                written++;
            }
        }
    }

    private static bool TryFormatInlineScalarOrStruct(
        object value,
        SerializedFile? ownerFile,
        ExportState state,
        out string formatted)
    {
        switch (value)
        {
            case bool b:
                formatted = b ? "1" : "0";
                return true;
            case byte u8:
                formatted = u8.ToString(CultureInfo.InvariantCulture);
                return true;
            case sbyte i8:
                formatted = i8.ToString(CultureInfo.InvariantCulture);
                return true;
            case short i16:
                formatted = i16.ToString(CultureInfo.InvariantCulture);
                return true;
            case ushort u16:
                formatted = u16.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i32:
                formatted = i32.ToString(CultureInfo.InvariantCulture);
                return true;
            case uint u32:
                formatted = u32.ToString(CultureInfo.InvariantCulture);
                return true;
            case long i64:
                formatted = i64.ToString(CultureInfo.InvariantCulture);
                return true;
            case ulong u64:
                formatted = u64.ToString(CultureInfo.InvariantCulture);
                return true;
            case float f32:
                formatted = FormatFloat(f32);
                return true;
            case double f64:
                formatted = FormatDouble(f64);
                return true;
            case string str:
                formatted = FormatYamlScalarString(str);
                return true;
            case char ch:
                formatted = FormatYamlScalarString(ch.ToString());
                return true;
            case IDictionary dict:
                // 1) PPtr rewriting ({m_FileID, m_PathID}) -> local {fileID} or external {fileID, guid, type}
                if (dict.Count == 2 && dict.Contains("m_FileID") && dict.Contains("m_PathID")
                    && TryConvertInt(dict["m_FileID"], out int fileId)
                    && TryConvertLong(dict["m_PathID"], out long pathId))
                {
                    formatted = FormatRewrittenPPtr(ownerFile, fileId, pathId, state);
                    return true;
                }

                // 2) Compact Unity math structs ({x, y}, {x, y, z}, {x, y, z, w}, {r, g, b, a}, {x, y, width, height})
                if (dict.Count == 2 && dict.Contains("x") && dict.Contains("y")
                    && TryConvertFloat(dict["x"], out float vx2) && TryConvertFloat(dict["y"], out float vy2))
                {
                    formatted = $"{{x: {FormatFloat(vx2)}, y: {FormatFloat(vy2)}}}";
                    return true;
                }

                if (dict.Count == 3 && dict.Contains("x") && dict.Contains("y") && dict.Contains("z")
                    && TryConvertFloat(dict["x"], out float vx3) && TryConvertFloat(dict["y"], out float vy3) && TryConvertFloat(dict["z"], out float vz3))
                {
                    formatted = $"{{x: {FormatFloat(vx3)}, y: {FormatFloat(vy3)}, z: {FormatFloat(vz3)}}}";
                    return true;
                }

                if (dict.Count == 4 && dict.Contains("x") && dict.Contains("y") && dict.Contains("z") && dict.Contains("w")
                    && TryConvertFloat(dict["x"], out float vx4) && TryConvertFloat(dict["y"], out float vy4)
                    && TryConvertFloat(dict["z"], out float vz4) && TryConvertFloat(dict["w"], out float vw4))
                {
                    formatted = $"{{x: {FormatFloat(vx4)}, y: {FormatFloat(vy4)}, z: {FormatFloat(vz4)}, w: {FormatFloat(vw4)}}}";
                    return true;
                }

                if (dict.Count == 4 && dict.Contains("r") && dict.Contains("g") && dict.Contains("b") && dict.Contains("a")
                    && TryConvertFloat(dict["r"], out float cr) && TryConvertFloat(dict["g"], out float cg)
                    && TryConvertFloat(dict["b"], out float cb) && TryConvertFloat(dict["a"], out float ca))
                {
                    formatted = $"{{r: {FormatFloat(cr)}, g: {FormatFloat(cg)}, b: {FormatFloat(cb)}, a: {FormatFloat(ca)}}}";
                    return true;
                }

                if (dict.Count == 4 && dict.Contains("x") && dict.Contains("y") && dict.Contains("width") && dict.Contains("height")
                    && TryConvertFloat(dict["x"], out float rx) && TryConvertFloat(dict["y"], out float ry)
                    && TryConvertFloat(dict["width"], out float rw) && TryConvertFloat(dict["height"], out float rh))
                {
                    formatted = $"{{x: {FormatFloat(rx)}, y: {FormatFloat(ry)}, width: {FormatFloat(rw)}, height: {FormatFloat(rh)}}}";
                    return true;
                }

                break;
        }

        formatted = string.Empty;
        return false;
    }

    private static string FormatRewrittenPPtr(SerializedFile? ownerFile, int fileId, long pathId, ExportState state)
    {
        if (pathId == 0 || fileId < 0)
        {
            return "{fileID: 0}";
        }

        if (TryLookupLocalFileId(ownerFile, fileId, pathId, state, out long localId))
        {
            return $"{{fileID: {localId.ToString(CultureInfo.InvariantCulture)}}}";
        }

        var targetFileName = ResolvePPtrFileName(ownerFile, fileId);
        var key = (targetFileName, pathId);

        if (state.MeshesByKey.TryGetValue(key, out var meshAsset) && meshAsset.Exported)
        {
            return $"{{fileID: 4300000, guid: {meshAsset.Guid}, type: 3}}";
        }

        if (state.MaterialsByKey.TryGetValue(key, out var matAsset))
        {
            return $"{{fileID: 2100000, guid: {matAsset.Guid}, type: 2}}";
        }

        if (state.TexturesByKey.TryGetValue(key, out var texAsset) && texAsset.Exported)
        {
            return $"{{fileID: 2800000, guid: {texAsset.Guid}, type: 3}}";
        }

        return "{fileID: 0}";
    }

    // =========================================================================
    // Manifest Building
    // =========================================================================

    private static UnityProjectExportStatistics BuildStatistics(ExportState state)
    {
        return new UnityProjectExportStatistics
        {
            GameObjects = state.SceneNodes.Count,
            ActiveGameObjects = state.ActiveGameObjects,
            InactiveGameObjects = state.InactiveGameObjects,
            Transforms = state.TransformCount,
            MeshFilters = state.MeshFilterCount,
            MeshRenderers = state.MeshRendererCount,
            SkinnedMeshRenderers = state.SkinnedMeshRendererCount,
            ShadowsOnlyRenderers = state.ShadowsOnlyRendererCount,
            Cameras = state.CameraCount,
            Lights = state.LightCount,
            LODGroups = state.LodGroupCount,
            Colliders = state.ColliderCount,
            Terrains = state.TerrainCount,
            MonoBehaviours = state.MonoBehaviourCount,
            UniqueMeshes = state.MeshesToExport.Count(static m => m.Exported) + state.TerrainAssets.Count(static t => t.MeshData != null),
            UniqueMaterials = state.MaterialsToExport.Count + (state.Options.ExportMaterials ? state.TerrainAssets.Count(static t => t.MeshData != null) : 0),
            UniqueTextures = state.TexturesToExport.Count(static t => t.Exported) + state.TerrainAssets.Count(static t => t.HasBakedTexture)
        };
    }

    private static UnityProjectExportManifest BuildManifest(
        UnityProjectExportOptions options,
        string safeSceneName,
        string unityVersion,
        long elapsedMs,
        string outputDirFull,
        string sceneFilePath,
        ExportState state,
        UnityProjectScriptExportSummary? scriptSummary)
    {
        var layersUsed = new SortedDictionary<int, string>();
        foreach (var layerIdx in state.UsedLayers)
        {
            layersUsed[layerIdx] = GetLayerDisplayName(layerIdx, state.UsedLayers);
        }

        var shadersReferenced = state.ShaderUsage
            .OrderByDescending(static kv => kv.Value.Count)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new UnityProjectShaderReference
            {
                ShaderName = kv.Key,
                UsageCount = kv.Value.Count,
                IsCustom = kv.Value.IsCustom
            })
            .ToList();

        var scriptsReferenced = state.ScriptUsage.Values
            .OrderByDescending(static s => s.Count)
            .ThenBy(static s => s.Assembly, StringComparer.Ordinal)
            .ThenBy(static s => s.Namespace, StringComparer.Ordinal)
            .ThenBy(static s => s.ClassName, StringComparer.Ordinal)
            .Select(static s => new UnityProjectScriptReference
            {
                Assembly = s.Assembly,
                Namespace = s.Namespace,
                ClassName = s.ClassName,
                DeterministicGuid = s.Guid,
                InstanceCount = s.Count
            })
            .ToList();

        var missingAssetsSorted = state.MissingAssets
            .OrderBy(static m => m.AssetType, StringComparer.Ordinal)
            .ThenBy(static m => m.SourceFile, StringComparer.Ordinal)
            .ThenBy(static m => m.PathId)
            .ToList();

        return new UnityProjectExportManifest
        {
            SceneName = safeSceneName,
            ScenePath = options.ScenePath ?? string.Empty,
            UnityVersion = unityVersion,
            ExportTimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ElapsedMs = elapsedMs,
            OutputDirectory = outputDirFull,
            SceneFilePath = sceneFilePath,
            AdditionalScenes = options.AdditionalSceneNames != null
                ? options.AdditionalSceneNames.Where(static n => !string.IsNullOrWhiteSpace(n)).ToList()
                : new List<string>(),
            Statistics = BuildStatistics(state),
            LayersUsed = layersUsed,
            ShadersReferenced = shadersReferenced,
            ScriptsReferenced = scriptsReferenced,
            ResolvedDependencies = options.ResolvedDependencies != null
                ? options.ResolvedDependencies.Where(static d => !string.IsNullOrWhiteSpace(d)).OrderBy(static d => d, StringComparer.Ordinal).ToList()
                : new List<string>(),
            MissingDependencies = options.MissingDependencies != null
                ? options.MissingDependencies.Where(static d => !string.IsNullOrWhiteSpace(d)).OrderBy(static d => d, StringComparer.Ordinal).ToList()
                : new List<string>(),
            MissingAssets = missingAssetsSorted,
            ScriptExportSummary = scriptSummary
        };
    }

    // =========================================================================
    // Object & Asset Resolution Helpers
    // =========================================================================

    private static string DetectUnityVersion(IReadOnlyList<GameObject> roots, string? explicitVersion)
    {
        if (!string.IsNullOrWhiteSpace(explicitVersion) && !string.Equals(explicitVersion.Trim(), "0.0.0", StringComparison.Ordinal))
        {
            return explicitVersion.Trim();
        }

        for (int i = 0; i < roots.Count; i++)
        {
            var ver = roots[i]?.assetsFile?.unityVersion;
            if (!string.IsNullOrWhiteSpace(ver)
                && !string.Equals(ver, "0.0.0", StringComparison.Ordinal)
                && !string.Equals(ver, "2.5.0f5", StringComparison.Ordinal))
            {
                return ver.Trim();
            }
        }

        return DefaultFallbackUnityVersion;
    }

    private static string ResolveUnityTagString(ushort tagId)
    {
        return tagId switch
        {
            0 => "Untagged",
            1 => "Respawn",
            2 => "Finish",
            3 => "EditorOnly",
            4 => "MainCamera",
            5 => "Player",
            6 => "GameController",
            _ => $"Tag_{tagId.ToString(CultureInfo.InvariantCulture)}"
        };
    }

    private static string GetLayerDisplayName(int layerIndex, SortedSet<int> usedLayers)
    {
        if (layerIndex >= 0 && layerIndex < UnityBuiltInLayers.Length)
        {
            var builtin = UnityBuiltInLayers[layerIndex];
            if (!string.IsNullOrEmpty(builtin))
            {
                return builtin;
            }
            return usedLayers.Contains(layerIndex) ? $"Layer_{layerIndex.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        }

        return usedLayers.Contains(layerIndex) ? $"Layer_{layerIndex.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
    }

    private static AssetStudio.Object? ResolveComponentObject(GameObject owner, PPtr<Component> compPtr)
    {
        if (compPtr.TryGet<AssetStudio.Object>(out var comp) && comp != null)
        {
            return comp;
        }

        if (compPtr.m_FileID == 0 && owner.assetsFile != null && owner.assetsFile.TryGetObject(compPtr.m_PathID, out var localObj))
        {
            return localObj;
        }

        foreach (var sf in SnapshotAssetsFiles(owner.assetsFile?.assetsManager))
        {
            if (sf.TryGetObject(compPtr.m_PathID, out var found))
            {
                return found;
            }
        }

        return null;
    }

    private static Transform? ResolveTransform(Transform owner, PPtr<Transform> ptr)
    {
        if (ptr.TryGet(out var tr) && tr != null)
        {
            return tr;
        }

        foreach (var sf in SnapshotAssetsFiles(owner.assetsFile?.assetsManager))
        {
            if (sf.TryGetObject(ptr.m_PathID, out var found) && found is Transform foundTr)
            {
                return foundTr;
            }
        }

        return null;
    }

    private static GameObject? ResolveGameObject(Component owner, PPtr<GameObject> ptr)
    {
        if (ptr.TryGet(out var go) && go != null)
        {
            return go;
        }

        foreach (var sf in SnapshotAssetsFiles(owner.assetsFile?.assetsManager))
        {
            if (sf.TryGetObject(ptr.m_PathID, out var found) && found is GameObject foundGo)
            {
                return foundGo;
            }
        }

        return null;
    }

    private static Mesh? ResolveMeshFromMeshFilter(GameObject ownerGo, MeshFilter mf, ExportState state)
    {
        if (mf.m_Mesh != null && !mf.m_Mesh.IsNull && mf.m_Mesh.TryGet(out var directMesh) && directMesh != null)
        {
            return directMesh;
        }

        if (state.Options.MeshResolver != null && ownerGo.m_MeshRenderer != null)
        {
            var resolved = state.Options.MeshResolver(ownerGo.m_MeshRenderer);
            if (resolved != null)
            {
                return resolved;
            }
        }

        if (mf.m_Mesh != null && !mf.m_Mesh.IsNull)
        {
            foreach (var sf in SnapshotAssetsFiles(mf.assetsFile?.assetsManager))
            {
                if (sf.TryGetObject(mf.m_Mesh.m_PathID, out var found) && found is Mesh foundMesh)
                {
                    return foundMesh;
                }
            }
        }

        return null;
    }

    private static Mesh? ResolveMeshForRenderer(GameObject ownerGo, Renderer renderer, ExportState state)
    {
        if (renderer is SkinnedMeshRenderer smr)
        {
            if (smr.m_Mesh != null && !smr.m_Mesh.IsNull && smr.m_Mesh.TryGet(out var direct) && direct != null)
            {
                return direct;
            }
            return state.Options.MeshResolver?.Invoke(smr);
        }

        if (ownerGo.m_MeshFilter != null)
        {
            return ResolveMeshFromMeshFilter(ownerGo, ownerGo.m_MeshFilter, state);
        }

        return state.Options.MeshResolver?.Invoke(renderer);
    }

    private static List<ExportedMaterialAsset?> ResolveRendererMaterials(
        Renderer renderer,
        Mesh? mesh,
        string ownerName,
        ExportState state)
    {
        var result = new List<ExportedMaterialAsset?>();
        if (!state.Options.ExportMaterials)
        {
            return result;
        }

        var matPtrs = renderer.m_Materials;
        int slotCount = matPtrs?.Length ?? 0;
        for (int i = 0; i < slotCount; i++)
        {
            Material? resolvedMat = null;
            if (state.Options.MaterialResolver != null && mesh != null)
            {
                try
                {
                    resolvedMat = state.Options.MaterialResolver(renderer, mesh, i);
                }
                catch
                {
                }
            }

            var matPtr = matPtrs![i];
            if (resolvedMat == null && matPtr != null && !matPtr.IsNull)
            {
                matPtr.TryGet(out resolvedMat);
            }

            if (resolvedMat != null)
            {
                result.Add(RegisterMaterialAsset(resolvedMat, state));
            }
            else
            {
                result.Add(null);
                if (matPtr != null && !matPtr.IsNull)
                {
                    state.RecordMissingAsset("Material", matPtr.m_PathID, ResolvePPtrFileName(renderer.assetsFile, matPtr.m_FileID), ownerName);
                }
            }
        }

        return result;
    }

    private static Texture2D? ResolveMaterialTexture(
        Material mat,
        string propName,
        PPtr<Texture>? texPtr,
        ExportState state)
    {
        if (texPtr != null && !texPtr.IsNull && texPtr.TryGet(out var tex) && tex is Texture2D directTex2D)
        {
            return directTex2D;
        }

        if (state.Options.TextureResolver != null)
        {
            try
            {
                var resolved = state.Options.TextureResolver(mat, propName, texPtr);
                if (resolved != null)
                {
                    return resolved;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string ResolveShaderName(Material mat)
    {
        if (mat.m_Shader != null && !mat.m_Shader.IsNull && mat.m_Shader.TryGet(out var shader) && shader != null)
        {
            if (!string.IsNullOrWhiteSpace(shader.m_ParsedForm?.m_Name))
            {
                return shader.m_ParsedForm.m_Name;
            }
            if (!string.IsNullOrWhiteSpace(shader.m_Name))
            {
                return shader.m_Name;
            }
        }

        return string.Empty;
    }

    private static TerrainData? ResolveTerrainData(Terrain terrain)
    {
        if (terrain.m_TerrainData == null || terrain.m_TerrainData.IsNull)
        {
            return null;
        }

        if (terrain.m_TerrainData.TryGet(out var data) && data != null)
        {
            return data;
        }

        foreach (var sf in SnapshotAssetsFiles(terrain.assetsFile?.assetsManager))
        {
            if (sf.TryGetObject(terrain.m_TerrainData.m_PathID, out var found) && found is TerrainData td)
            {
                return td;
            }
        }

        return null;
    }

    private static MonoScript? ResolveMonoScript(MonoBehaviour mb)
    {
        if (mb.m_Script == null || mb.m_Script.IsNull)
        {
            return null;
        }

        if (mb.m_Script.TryGet(out var script) && script != null)
        {
            return script;
        }

        foreach (var sf in SnapshotAssetsFiles(mb.assetsFile?.assetsManager))
        {
            if (sf.TryGetObject(mb.m_Script.m_PathID, out var found) && found is MonoScript foundScript)
            {
                return foundScript;
            }
        }

        return null;
    }

    private static OrderedDictionary? ReadMonoBehaviourFields(MonoBehaviour mb, ExportState state)
    {
        if (state.Options.MonoBehaviourFieldProvider != null)
        {
            try
            {
                var custom = state.Options.MonoBehaviourFieldProvider(mb);
                if (custom != null)
                {
                    return custom;
                }
            }
            catch
            {
            }
        }

        if (mb.serializedType?.m_Type?.m_Nodes is { Count: > 0 })
        {
            try
            {
                return mb.ToType(mb.serializedType.m_Type);
            }
            catch
            {
            }
        }

        if (state.Options.AssemblyLoader != null)
        {
            try
            {
                var typeTree = mb.ConvertToTypeTree(state.Options.AssemblyLoader);
                if (typeTree?.m_Nodes is { Count: > 0 })
                {
                    return mb.ToType(typeTree);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static OrderedDictionary? ReadColliderFields(AssetStudio.Object colliderObj, ExportState state)
    {
        if (state.Options.ColliderFieldProvider != null)
        {
            try
            {
                var custom = state.Options.ColliderFieldProvider(colliderObj);
                if (custom != null)
                {
                    return custom;
                }
            }
            catch
            {
            }
        }

        if (colliderObj.serializedType?.m_Type?.m_Nodes is { Count: > 0 })
        {
            try
            {
                return colliderObj.ToType(colliderObj.serializedType.m_Type);
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool TryLookupLocalFileId(
        SerializedFile? ownerFile,
        int fileId,
        long pathId,
        ExportState state,
        out long localFileId)
    {
        localFileId = 0;
        if (pathId == 0 || fileId < 0)
        {
            return false;
        }

        var targetFileName = ResolvePPtrFileName(ownerFile, fileId);
        if (state.LocalFileIdByKey.TryGetValue((targetFileName, pathId), out localFileId))
        {
            return true;
        }

        if (ownerFile != null && fileId == 0 && ownerFile.TryGetObject(pathId, out var obj) && obj != null)
        {
            if (state.LocalFileIdByObject.TryGetValue(obj, out localFileId))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolvePPtrFileName(SerializedFile? ownerFile, int fileId)
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
            if (!string.IsNullOrWhiteSpace(ext?.fileName))
            {
                return ext.fileName;
            }
            if (!string.IsNullOrWhiteSpace(ext?.pathName))
            {
                return Path.GetFileName(ext.pathName.Replace('\\', '/'));
            }
        }

        return ownerFile.fileName ?? string.Empty;
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

    private static bool IsNormalMapPropertyName(string propName)
    {
        return propName.Contains("Bump", StringComparison.OrdinalIgnoreCase)
            || propName.Contains("Normal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNormalMapName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.EndsWith("_NRM", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_Normal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_Bump", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_NormalMap", StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Path Confinement & YAML Formatting Helpers
    // =========================================================================

    private static string EnsureConfinedDirectory(string rootDirWithSep, string targetDir)
    {
        var confined = EnsureConfinedPath(rootDirWithSep, targetDir);
        Directory.CreateDirectory(confined);
        return confined;
    }

    private static string EnsureConfinedPath(string rootDirWithSep, string candidatePath)
    {
        var full = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootDirWithSep, comparison))
        {
            throw new InvalidOperationException($"Refusing to write outside Unity project root: '{candidatePath}'.");
        }
        return full;
    }

    private static void WriteYamlStringArray(StreamWriter sw, string prefixedKey, string[]? values)
    {
        if (values == null || values.Length == 0)
        {
            sw.Write(prefixedKey);
            sw.WriteLine(": []");
            return;
        }

        sw.Write(prefixedKey);
        sw.WriteLine(":");
        for (int i = 0; i < values.Length; i++)
        {
            sw.Write("  - ");
            sw.WriteLine(FormatYamlScalarString(values[i] ?? string.Empty));
        }
    }

    private static void WriteIndent(StreamWriter sw, int spaces)
    {
        for (int i = 0; i < spaces; i++)
        {
            sw.Write(' ');
        }
    }

    private static string FormatFloat(float value, float fallbackIfNonFinite = 0f)
    {
        float safe = float.IsFinite(value) ? value : fallbackIfNonFinite;
        if (safe == 0f)
        {
            return "0";
        }
        return safe.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value)
    {
        double safe = double.IsFinite(value) ? value : 0.0;
        if (safe == 0.0)
        {
            return "0";
        }
        return safe.ToString(CultureInfo.InvariantCulture);
    }

    private static string SanitizeYamlComment(string text)
    {
        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string FormatYamlKey(string? rawKey)
    {
        var key = string.IsNullOrWhiteSpace(rawKey) ? "unnamed" : rawKey.Trim();
        return NeedsYamlQuoting(key) ? QuoteYamlDouble(key) : key;
    }

    private static string FormatYamlScalarString(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return string.Empty;
        }

        return NeedsYamlQuoting(rawValue) ? QuoteYamlDouble(rawValue) : rawValue;
    }

    private static bool NeedsYamlQuoting(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
        {
            return true;
        }

        if (YamlSpecialKeywords.Contains(s))
        {
            return true;
        }

        char first = s[0];
        if (first is '-' or '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`')
        {
            return true;
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x20 || c == '\r' || c == '\n' || c == ':' || c == '#' || c == '"' || c == '\\' || c == '{' || c == '}' || c == '[' || c == ']')
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteYamlDouble(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // =========================================================================
    // Dictionary Field Conversion Helpers
    // =========================================================================

    private static bool TryExtractPPtrValues(IDictionary dict, string key, out int fileId, out long pathId)
    {
        fileId = 0;
        pathId = 0;
        if (!dict.Contains(key) || dict[key] is not IDictionary pptr)
        {
            return false;
        }

        return pptr.Contains("m_FileID")
            && pptr.Contains("m_PathID")
            && TryConvertInt(pptr["m_FileID"], out fileId)
            && TryConvertLong(pptr["m_PathID"], out pathId);
    }

    private static bool TryReadVector3(IDictionary dict, string key, out Vector3 value)
    {
        value = Vector3.Zero;
        if (!dict.Contains(key) || dict[key] is not IDictionary v)
        {
            return false;
        }

        TryReadFloat(v, "x", out float x);
        TryReadFloat(v, "y", out float y);
        TryReadFloat(v, "z", out float z);
        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryReadFloat(IDictionary dict, string key, out float value)
    {
        value = 0f;
        return dict.Contains(key) && TryConvertFloat(dict[key], out value);
    }

    private static bool TryReadInt(IDictionary dict, string key, out int value)
    {
        value = 0;
        return dict.Contains(key) && TryConvertInt(dict[key], out value);
    }

    private static bool TryReadBool(IDictionary dict, string key, out bool value)
    {
        value = false;
        if (!dict.Contains(key) || dict[key] == null) return false;
        try
        {
            value = Convert.ToBoolean(dict[key], CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertFloat(object? raw, out float value)
    {
        value = 0f;
        if (raw == null) return false;
        try
        {
            value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertInt(object? raw, out int value)
    {
        value = 0;
        if (raw == null) return false;
        try
        {
            value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertLong(object? raw, out long value)
    {
        value = 0;
        if (raw == null) return false;
        try
        {
            value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
