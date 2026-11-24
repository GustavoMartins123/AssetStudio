using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using AssetStudio.Avalonia.Services.Scripting;

namespace AssetStudio.Avalonia.Services;

public enum ScriptSpawnConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum ScriptSpawnSourceKind
{
    PPtrField = 0,
    AddressablesGuid = 1
}

public sealed class SceneScriptSpawnAnalysisOptions
{
    public const int DefaultMaxRecursionDepth = 3;
    public const int DefaultMaxSpawnedInstances = 512;

    public int MaxRecursionDepth { get; init; } = DefaultMaxRecursionDepth;
    public int MaxSpawnedInstances { get; init; } = DefaultMaxSpawnedInstances;
    public bool IncludeInactiveGameObjects { get; init; }
    public bool EnableMonoIlConfidenceBoost { get; init; } = true;
    public IReadOnlyList<ParsedAddressablesCatalog>? AddressablesCatalogs { get; init; }
    public IReadOnlyList<string>? ManagedAssemblySearchDirectories { get; init; }
    public AssemblyLoader? AssemblyLoader { get; init; }
    public Func<MonoBehaviour, (OrderedDictionary? Fields, TypeTree? TypeTree)>? CustomFieldProvider { get; init; }
    public Func<string, long, GameObject?>? ExternalGameObjectResolver { get; init; }
    public Func<string, CatalogAssetEntry, GameObject?>? AddressablesPrefabResolver { get; init; }
}

public sealed class ResolvedScriptSpawn
{
    public string OwnerGameObjectPath { get; init; } = string.Empty;
    public long OwnerPathId { get; init; }
    public long AnchorTransformPathId { get; init; }
    public string ScriptClass { get; init; } = string.Empty;
    public string FieldPath { get; init; } = string.Empty;
    public string TargetFileName { get; init; } = string.Empty;
    public long TargetPathId { get; init; }
    public string TargetGuid { get; init; } = string.Empty;
    public string TargetBundleFileName { get; init; } = string.Empty;
    public string TargetAssetPath { get; init; } = string.Empty;
    public ScriptSpawnConfidence Confidence { get; init; }
    public ScriptSpawnSourceKind SourceKind { get; init; }
    public int RecursionDepth { get; init; }
    public Vector3 LocalPositionOffset { get; init; }
    public bool ConfirmedByMonoIl { get; init; }
    public GameObject? OwnerGameObject { get; init; }
    public Transform? AnchorTransform { get; init; }
    public GameObject? ResolvedPrefabRoot { get; init; }
}

public sealed class UnresolvedDynamicSpawnerHint
{
    public string OwnerGameObjectPath { get; init; } = string.Empty;
    public long OwnerPathId { get; init; }
    public string ScriptClass { get; init; } = string.Empty;
    public string FieldPath { get; init; } = string.Empty;
    public List<string> CandidateReferences { get; } = new();
    public ScriptSpawnConfidence Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class SceneScriptSpawnAnalysisResult
{
    public List<ResolvedScriptSpawn> ResolvedSpawns { get; } = new();
    public List<UnresolvedDynamicSpawnerHint> UnresolvedDynamicSpawners { get; } = new();
    public bool WasTruncatedByCap { get; set; }
    public int InspectedMonoBehaviourCount { get; set; }
    public int CycleGuardsTriggered { get; set; }
    public int MaxDepthGuardsTriggered { get; set; }

    public List<SceneScriptSpawnAttachment> ToModelConverterAttachments()
    {
        var attachments = new List<SceneScriptSpawnAttachment>();
        for (int i = 0; i < ResolvedSpawns.Count; i++)
        {
            var spawn = ResolvedSpawns[i];
            if (spawn.ResolvedPrefabRoot == null)
            {
                continue;
            }

            attachments.Add(new SceneScriptSpawnAttachment
            {
                AnchorTransform = spawn.AnchorTransform,
                OwnerGameObject = spawn.OwnerGameObject,
                OwnerPathId = spawn.OwnerPathId,
                AnchorTransformPathId = spawn.AnchorTransformPathId,
                PrefabRoot = spawn.ResolvedPrefabRoot,
                LocalPositionOffset = spawn.LocalPositionOffset,
                ScriptClass = spawn.ScriptClass,
                FieldPath = spawn.FieldPath,
                SourceKind = spawn.SourceKind.ToString(),
                Confidence = spawn.Confidence.ToString(),
                TargetFileName = spawn.TargetFileName,
                TargetPathId = spawn.TargetPathId,
                TargetGuid = spawn.TargetGuid
            });
        }

        return attachments;
    }
}

/// <summary>
/// Pure-C#, opt-in static analyzer that inspects scene <see cref="MonoBehaviour"/> components
/// to discover statically placeable prefab references (<c>PPtr&lt;GameObject&gt;</c>/<c>Transform</c>/<c>Prefab</c>
/// and Addressables <c>m_AssetGUID</c> fields) while honestly reporting dynamic/imperative spawners
/// without inventing speculative geometry.
/// </summary>
public sealed class SceneScriptSpawnAnalyzer
{
    private const int MaxFieldWalkDepth = 8;
    private const int MaxCollectionElements = 128;

    private static readonly HashSet<string> StandardHeaderFields = new(StringComparer.Ordinal)
    {
        "m_GameObject",
        "m_Enabled",
        "m_Script",
        "m_Name",
        "m_EditorHideFlags",
        "m_EditorClassIdentifier"
    };

    private static readonly HashSet<string> SpawnablePPtrTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameObject",
        "Transform",
        "RectTransform",
        "Prefab",
        "PrefabInstance",
        "Object",
        "UnityEngine.Object"
    };

    private static readonly HashSet<string> NonSpawnablePPtrTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Texture",
        "Texture2D",
        "Cubemap",
        "RenderTexture",
        "Sprite",
        "SpriteAtlas",
        "Material",
        "Shader",
        "Mesh",
        "AudioClip",
        "AudioMixer",
        "AnimationClip",
        "RuntimeAnimatorController",
        "AnimatorController",
        "Avatar",
        "Font",
        "TextAsset",
        "ScriptableObject",
        "MonoScript",
        "VideoClip",
        "PhysicMaterial",
        "PhysicsMaterial2D"
    };

    private static readonly string[] NonPrefabAssetExtensions =
    {
        ".mat", ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".exr", ".hdr", ".ktx",
        ".wav", ".ogg", ".mp3", ".aif", ".flac",
        ".shader", ".compute", ".anim", ".controller", ".overrideController",
        ".asset", ".txt", ".json", ".xml", ".bytes", ".unity", ".ttf", ".otf", ".mp4", ".webm"
    };

    private sealed class MonoIlScriptSignals
    {
        public HashSet<string> InstantiateFields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DynamicLoaderCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DynamicLiteralKeys { get; } = new();
    }

    private sealed class ExtractionContext
    {
        public required SceneScriptSpawnAnalysisOptions Options { get; init; }
        public required SceneScriptSpawnAnalysisResult Result { get; init; }
        public required HashSet<(string FileName, long PathId)> SceneGameObjectIds { get; init; }
        public required HashSet<(string FileName, long PathId)> SceneTransformIds { get; init; }
        public required Dictionary<string, MonoIlScriptSignals?> IlSignalCache { get; init; }
        public required IReadOnlyList<SerializedFile> LoadedSerializedFiles { get; init; }
        public required HashSet<string> ActivePrefabChain { get; init; }
    }

    public SceneScriptSpawnAnalysisResult AnalyzeScene(
        IReadOnlyList<GameObject> sceneRootGameObjects,
        SceneScriptSpawnAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sceneRootGameObjects);

        var effectiveOptions = options ?? new SceneScriptSpawnAnalysisOptions();
        var result = new SceneScriptSpawnAnalysisResult();
        if (sceneRootGameObjects.Count == 0 || effectiveOptions.MaxSpawnedInstances <= 0)
        {
            return result;
        }

        var sceneGameObjects = new List<(GameObject GameObject, string Path)>();
        var sceneGameObjectIds = new HashSet<(string FileName, long PathId)>();
        var sceneTransformIds = new HashSet<(string FileName, long PathId)>();
        var loadedFiles = new List<SerializedFile>();
        var seenFiles = new HashSet<SerializedFile>();

        CollectHierarchy(
            sceneRootGameObjects,
            effectiveOptions.IncludeInactiveGameObjects,
            sceneGameObjects,
            sceneGameObjectIds,
            sceneTransformIds,
            loadedFiles,
            seenFiles,
            cancellationToken);

        var ctx = new ExtractionContext
        {
            Options = effectiveOptions,
            Result = result,
            SceneGameObjectIds = sceneGameObjectIds,
            SceneTransformIds = sceneTransformIds,
            IlSignalCache = new Dictionary<string, MonoIlScriptSignals?>(StringComparer.OrdinalIgnoreCase),
            LoadedSerializedFiles = loadedFiles,
            ActivePrefabChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        for (int i = 0; i < sceneGameObjects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ResolvedSpawns.Count >= effectiveOptions.MaxSpawnedInstances)
            {
                result.WasTruncatedByCap = true;
                break;
            }

            var (go, goPath) = sceneGameObjects[i];
            AnalyzeGameObjectMonoBehaviours(
                go,
                goPath,
                anchorOverride: null,
                ownerPathIdOverride: 0,
                recursionDepth: 0,
                ctx,
                cancellationToken);
        }

        return result;
    }

    private static void CollectHierarchy(
        IReadOnlyList<GameObject> roots,
        bool includeInactive,
        List<(GameObject GameObject, string Path)> orderedOut,
        HashSet<(string FileName, long PathId)> goIdsOut,
        HashSet<(string FileName, long PathId)> trIdsOut,
        List<SerializedFile> loadedFilesOut,
        HashSet<SerializedFile> seenFiles,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<GameObject>();
        var stack = new Stack<(GameObject Go, string ParentPath)>();

        for (int i = roots.Count - 1; i >= 0; i--)
        {
            if (roots[i] != null)
            {
                stack.Push((roots[i], string.Empty));
            }
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (go, parentPath) = stack.Pop();
            if (go == null || !visited.Add(go))
            {
                continue;
            }

            if (!includeInactive && !go.m_IsActive)
            {
                continue;
            }

            RegisterSerializedFiles(go.assetsFile, loadedFilesOut, seenFiles);
            EnsureTransformLinked(go);

            var name = string.IsNullOrWhiteSpace(go.m_Name) ? $"GameObject_{go.m_PathID}" : go.m_Name;
            var fullPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

            orderedOut.Add((go, fullPath));
            goIdsOut.Add((go.assetsFile?.fileName ?? string.Empty, go.m_PathID));
            if (go.m_Transform != null)
            {
                trIdsOut.Add((go.m_Transform.assetsFile?.fileName ?? string.Empty, go.m_Transform.m_PathID));
            }

            var children = go.m_Transform?.m_Children;
            if (children == null || children.Length == 0)
            {
                continue;
            }

            for (int c = children.Length - 1; c >= 0; c--)
            {
                var childPtr = children[c];
                if (childPtr == null || childPtr.IsNull)
                {
                    continue;
                }

                if (childPtr.TryGet(out var childTr) && childTr != null
                    && childTr.m_GameObject != null
                    && !childTr.m_GameObject.IsNull
                    && childTr.m_GameObject.TryGet(out var childGo)
                    && childGo != null)
                {
                    stack.Push((childGo, fullPath));
                }
            }
        }
    }

    private static void RegisterSerializedFiles(
        SerializedFile? file,
        List<SerializedFile> loadedFilesOut,
        HashSet<SerializedFile> seenFiles)
    {
        if (file != null && seenFiles.Add(file))
        {
            loadedFilesOut.Add(file);
        }

        var manager = file?.assetsManager;
        if (manager == null)
        {
            return;
        }

        SerializedFile[] snapshot;
        lock (manager.loadLock)
        {
            snapshot = manager.assetsFileList.ToArray();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            var sf = snapshot[i];
            if (sf != null && seenFiles.Add(sf))
            {
                loadedFilesOut.Add(sf);
            }
        }
    }

    private static void EnsureTransformLinked(GameObject go)
    {
        if (go.m_Transform != null || go.m_Components == null)
        {
            return;
        }

        for (int i = 0; i < go.m_Components.Length; i++)
        {
            var compPtr = go.m_Components[i];
            if (compPtr != null && !compPtr.IsNull && compPtr.TryGet(out var comp) && comp is Transform tr)
            {
                go.m_Transform = tr;
                break;
            }
        }
    }

    private void AnalyzeGameObjectMonoBehaviours(
        GameObject go,
        string goPath,
        Transform? anchorOverride,
        long ownerPathIdOverride,
        int recursionDepth,
        ExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        if (go.m_Components == null || go.m_Components.Length == 0)
        {
            return;
        }

        EnsureTransformLinked(go);

        for (int i = 0; i < go.m_Components.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
            {
                ctx.Result.WasTruncatedByCap = true;
                return;
            }

            var compPtr = go.m_Components[i];
            if (compPtr == null || compPtr.IsNull)
            {
                continue;
            }

            if (!compPtr.TryGet(out var comp) || comp is not MonoBehaviour mb)
            {
                continue;
            }

            if (!ctx.Options.IncludeInactiveGameObjects && mb.m_Enabled == 0)
            {
                continue;
            }

            AnalyzeMonoBehaviour(
                go,
                goPath,
                mb,
                anchorOverride,
                ownerPathIdOverride,
                recursionDepth,
                ctx,
                cancellationToken);
        }
    }

    private void AnalyzeMonoBehaviour(
        GameObject ownerGo,
        string ownerGoPath,
        MonoBehaviour mb,
        Transform? anchorOverride,
        long ownerPathIdOverride,
        int recursionDepth,
        ExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        ctx.Result.InspectedMonoBehaviourCount++;

        var (scriptAssembly, scriptNamespace, scriptClassName) = ResolveScriptIdentity(mb);
        var scriptFullName = string.IsNullOrWhiteSpace(scriptNamespace)
            ? scriptClassName
            : $"{scriptNamespace}.{scriptClassName}";

        var (fields, typeTree) = ReadMonoBehaviourFields(mb, ctx.Options);
        var fieldTypeMap = typeTree != null
            ? TypeTreeScriptReconstructor.ExtractTopLevelFieldTypes(typeTree)
            : null;
        var pptrFieldTypeByPath = typeTree != null
            ? ExtractTypeTreeFieldTypes(typeTree)
            : null;

        MonoIlScriptSignals? ilSignals = null;
        if (ctx.Options.EnableMonoIlConfidenceBoost
            && ctx.Options.ManagedAssemblySearchDirectories is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(scriptClassName))
        {
            ilSignals = GetOrAnalyzeMonoIlSignals(
                scriptAssembly,
                scriptNamespace,
                scriptClassName,
                ctx.Options.ManagedAssemblySearchDirectories,
                ctx.IlSignalCache);
        }

        int spawnsBefore = ctx.Result.ResolvedSpawns.Count;
        int hintsBefore = ctx.Result.UnresolvedDynamicSpawners.Count;

        if (fields != null && fields.Count > 0)
        {
            WalkFieldDictionary(
                dict: fields,
                prefix: string.Empty,
                depth: 0,
                ownerGo: ownerGo,
                ownerGoPath: ownerGoPath,
                mb: mb,
                scriptFullName: scriptFullName,
                anchorOverride: anchorOverride,
                ownerPathIdOverride: ownerPathIdOverride,
                recursionDepth: recursionDepth,
                topLevelFieldTypes: fieldTypeMap,
                typeTreePathTypes: pptrFieldTypeByPath,
                ilSignals: ilSignals,
                ctx: ctx,
                cancellationToken: cancellationToken);
        }

        // If Mono IL revealed imperative/dynamic loading calls (Resources.Load / Addressables.InstantiateAsync / LoadAsset)
        // with no statically resolved spawns on this MonoBehaviour, report an honest dynamic spawner hint.
        if (ilSignals != null
            && ilSignals.DynamicLoaderCalls.Count > 0
            && ctx.Result.ResolvedSpawns.Count == spawnsBefore
            && ctx.Result.UnresolvedDynamicSpawners.Count == hintsBefore)
        {
            var hint = new UnresolvedDynamicSpawnerHint
            {
                OwnerGameObjectPath = ownerGoPath,
                OwnerPathId = ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
                ScriptClass = scriptFullName,
                FieldPath = string.Empty,
                Confidence = ScriptSpawnConfidence.Medium,
                Reason = $"Script invokes dynamic asset loading ({string.Join(", ", ilSignals.DynamicLoaderCalls.OrderBy(static x => x, StringComparer.Ordinal))}); runtime placement cannot be determined statically."
            };
            for (int k = 0; k < ilSignals.DynamicLiteralKeys.Count; k++)
            {
                hint.CandidateReferences.Add(ilSignals.DynamicLiteralKeys[k]);
            }
            ctx.Result.UnresolvedDynamicSpawners.Add(hint);
        }
    }

    private void WalkFieldDictionary(
        IDictionary dict,
        string prefix,
        int depth,
        GameObject ownerGo,
        string ownerGoPath,
        MonoBehaviour mb,
        string scriptFullName,
        Transform? anchorOverride,
        long ownerPathIdOverride,
        int recursionDepth,
        Dictionary<string, string>? topLevelFieldTypes,
        Dictionary<string, string>? typeTreePathTypes,
        MonoIlScriptSignals? ilSignals,
        ExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        if (depth > MaxFieldWalkDepth || ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
        {
            if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
            {
                ctx.Result.WasTruncatedByCap = true;
            }
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Check if this dictionary IS an Addressables AssetReference ({ m_AssetGUID: "..." })
        if (TryExtractAssetReferenceGuid(dict, out var normalizedGuid))
        {
            HandleAddressablesGuidField(
                normalizedGuid,
                prefix,
                ownerGo,
                ownerGoPath,
                scriptFullName,
                anchorOverride ?? ownerGo.m_Transform,
                ownerPathIdOverride,
                localOffset: Vector3.Zero,
                recursionDepth,
                ilSignals,
                ctx,
                cancellationToken);
            return;
        }

        // Check if this dictionary IS a PPtr ({ m_FileID, m_PathID })
        if (TryReadPPtrDictionary(dict, out int fileId, out long pathId))
        {
            HandlePPtrField(
                fileId,
                pathId,
                prefix,
                ownerGo,
                ownerGoPath,
                mb,
                scriptFullName,
                anchorTransform: anchorOverride ?? ownerGo.m_Transform,
                ownerPathIdOverride: ownerPathIdOverride,
                localOffset: Vector3.Zero,
                recursionDepth: recursionDepth,
                topLevelFieldTypes: topLevelFieldTypes,
                typeTreePathTypes: typeTreePathTypes,
                ilSignals: ilSignals,
                ctx: ctx,
                cancellationToken: cancellationToken);
            return;
        }

        // Look for sibling spawn-point Transform anchor and local offset vector inside this struct/object
        var siblingAnchor = FindSiblingAnchorTransform(dict, prefix, mb, typeTreePathTypes, ctx) ?? anchorOverride ?? ownerGo.m_Transform;
        var siblingOffset = FindSiblingLocalOffset(dict);

        foreach (DictionaryEntry entry in dict)
        {
            if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
            {
                ctx.Result.WasTruncatedByCap = true;
                return;
            }

            var key = entry.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(key) || StandardHeaderFields.Contains(key))
            {
                continue;
            }

            var fieldPath = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            var value = entry.Value;

            if (value is IDictionary childDict)
            {
                if (TryExtractAssetReferenceGuid(childDict, out var childGuid))
                {
                    HandleAddressablesGuidField(
                        childGuid,
                        fieldPath,
                        ownerGo,
                        ownerGoPath,
                        scriptFullName,
                        siblingAnchor,
                        ownerPathIdOverride,
                        siblingOffset,
                        recursionDepth,
                        ilSignals,
                        ctx,
                        cancellationToken);
                }
                else if (TryReadPPtrDictionary(childDict, out int childFileId, out long childPathId))
                {
                    HandlePPtrField(
                        childFileId,
                        childPathId,
                        fieldPath,
                        ownerGo,
                        ownerGoPath,
                        mb,
                        scriptFullName,
                        anchorTransform: siblingAnchor,
                        ownerPathIdOverride: ownerPathIdOverride,
                        localOffset: siblingOffset,
                        recursionDepth: recursionDepth,
                        topLevelFieldTypes: topLevelFieldTypes,
                        typeTreePathTypes: typeTreePathTypes,
                        ilSignals: ilSignals,
                        ctx: ctx,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    WalkFieldDictionary(
                        childDict,
                        fieldPath,
                        depth + 1,
                        ownerGo,
                        ownerGoPath,
                        mb,
                        scriptFullName,
                        siblingAnchor,
                        ownerPathIdOverride,
                        recursionDepth,
                        topLevelFieldTypes,
                        typeTreePathTypes,
                        ilSignals,
                        ctx,
                        cancellationToken);
                }
            }
            else if (value is string strVal)
            {
                MaybeRecordDynamicStringSpawnerHint(
                    key,
                    fieldPath,
                    strVal,
                    ownerGo,
                    ownerGoPath,
                    ownerPathIdOverride,
                    scriptFullName,
                    ctx);
            }
            else if (value is IEnumerable enumerable)
            {
                int idx = 0;
                foreach (var elem in enumerable)
                {
                    if (idx >= MaxCollectionElements || ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
                    {
                        if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
                        {
                            ctx.Result.WasTruncatedByCap = true;
                        }
                        break;
                    }

                    var elemPath = $"{fieldPath}[{idx.ToString(CultureInfo.InvariantCulture)}]";
                    if (elem is IDictionary elemDict)
                    {
                        if (TryExtractAssetReferenceGuid(elemDict, out var elemGuid))
                        {
                            HandleAddressablesGuidField(
                                elemGuid,
                                elemPath,
                                ownerGo,
                                ownerGoPath,
                                scriptFullName,
                                siblingAnchor,
                                ownerPathIdOverride,
                                siblingOffset,
                                recursionDepth,
                                ilSignals,
                                ctx,
                                cancellationToken);
                        }
                        else if (TryReadPPtrDictionary(elemDict, out int elemFileId, out long elemPathId))
                        {
                            HandlePPtrField(
                                elemFileId,
                                elemPathId,
                                elemPath,
                                ownerGo,
                                ownerGoPath,
                                mb,
                                scriptFullName,
                                anchorTransform: siblingAnchor,
                                ownerPathIdOverride: ownerPathIdOverride,
                                localOffset: siblingOffset,
                                recursionDepth: recursionDepth,
                                topLevelFieldTypes: topLevelFieldTypes,
                                typeTreePathTypes: typeTreePathTypes,
                                ilSignals: ilSignals,
                                ctx: ctx,
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            WalkFieldDictionary(
                                elemDict,
                                elemPath,
                                depth + 1,
                                ownerGo,
                                ownerGoPath,
                                mb,
                                scriptFullName,
                                siblingAnchor,
                                ownerPathIdOverride,
                                recursionDepth,
                                topLevelFieldTypes,
                                typeTreePathTypes,
                                ilSignals,
                                ctx,
                                cancellationToken);
                        }
                    }
                    else if (elem is string elemStr)
                    {
                        MaybeRecordDynamicStringSpawnerHint(
                            key,
                            elemPath,
                            elemStr,
                            ownerGo,
                            ownerGoPath,
                            ownerPathIdOverride,
                            scriptFullName,
                            ctx);
                    }

                    idx++;
                }
            }
        }
    }

    private void HandlePPtrField(
        int fileId,
        long pathId,
        string fieldPath,
        GameObject ownerGo,
        string ownerGoPath,
        MonoBehaviour mb,
        string scriptFullName,
        Transform? anchorTransform,
        long ownerPathIdOverride,
        Vector3 localOffset,
        int recursionDepth,
        Dictionary<string, string>? topLevelFieldTypes,
        Dictionary<string, string>? typeTreePathTypes,
        MonoIlScriptSignals? ilSignals,
        ExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        if (pathId == 0 || fileId < 0)
        {
            return;
        }

        if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
        {
            ctx.Result.WasTruncatedByCap = true;
            return;
        }

        var declaredType = LookupDeclaredPPtrInnerType(fieldPath, topLevelFieldTypes, typeTreePathTypes);
        if (!string.IsNullOrEmpty(declaredType) && NonSpawnablePPtrTypes.Contains(declaredType))
        {
            return;
        }

        var sourceFile = mb.assetsFile;
        var targetFileName = ResolveExternalFileName(sourceFile, fileId);
        bool isExternal = fileId != 0;

        // Intra-scene reference filter: if the target is in the same scene file AND already part of the scene hierarchy, skip it.
        if (!isExternal)
        {
            var localKey = (sourceFile?.fileName ?? string.Empty, pathId);
            if (ctx.SceneGameObjectIds.Contains(localKey) || ctx.SceneTransformIds.Contains(localKey))
            {
                return;
            }
        }

        GameObject? resolvedPrefab = ResolveTargetGameObject(sourceFile, fileId, pathId, targetFileName, ctx);
        if (resolvedPrefab != null)
        {
            // Verify resolved prefab is NOT already in the scene hierarchy (even across files with matching PathIDs)
            var resolvedKey = (resolvedPrefab.assetsFile?.fileName ?? targetFileName, resolvedPrefab.m_PathID);
            if (ctx.SceneGameObjectIds.Contains(resolvedKey))
            {
                return;
            }

            // If a Transform resolved to a child inside a prefab, walk up to the prefab root GameObject
            resolvedPrefab = WalkToPrefabRoot(resolvedPrefab);
        }
        else
        {
            // When target isn't loaded in memory, only accept if TypeTree declares a spawnable PPtr type or field name indicates a prefab
            bool isDeclaredSpawnable = !string.IsNullOrEmpty(declaredType) && SpawnablePPtrTypes.Contains(declaredType);
            if (!isExternal || (!isDeclaredSpawnable && !FieldNameSuggestsPrefab(fieldPath)))
            {
                return;
            }
        }

        var confirmedByIl = IsFieldConfirmedByMonoIl(fieldPath, ilSignals);
        var confidence = ComputePPtrConfidence(confirmedByIl, resolvedPrefab != null, declaredType, fieldPath);
        var prefabCycleKey = BuildPrefabCycleKey(
            resolvedPrefab?.assetsFile?.fileName ?? targetFileName,
            resolvedPrefab?.m_PathID ?? pathId,
            guid: string.Empty);

        if (ctx.ActivePrefabChain.Contains(prefabCycleKey))
        {
            ctx.Result.CycleGuardsTriggered++;
            return;
        }

        var resolvedSpawn = new ResolvedScriptSpawn
        {
            OwnerGameObjectPath = ownerGoPath,
            OwnerPathId = ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
            AnchorTransformPathId = anchorTransform?.m_PathID ?? 0,
            ScriptClass = scriptFullName,
            FieldPath = fieldPath,
            TargetFileName = resolvedPrefab?.assetsFile?.fileName ?? targetFileName,
            TargetPathId = resolvedPrefab?.m_PathID ?? pathId,
            TargetGuid = string.Empty,
            Confidence = confidence,
            SourceKind = ScriptSpawnSourceKind.PPtrField,
            RecursionDepth = recursionDepth,
            LocalPositionOffset = localOffset,
            ConfirmedByMonoIl = confirmedByIl,
            OwnerGameObject = ownerGo,
            AnchorTransform = anchorTransform,
            ResolvedPrefabRoot = resolvedPrefab
        };

        ctx.Result.ResolvedSpawns.Add(resolvedSpawn);

        if (resolvedPrefab != null)
        {
            TryRecurseIntoSpawnedPrefab(
                resolvedPrefab,
                prefabCycleKey,
                ownerGoPath,
                anchorTransform,
                ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
                recursionDepth,
                ctx,
                cancellationToken);
        }
    }

    private void HandleAddressablesGuidField(
        string normalizedGuid,
        string fieldPath,
        GameObject ownerGo,
        string ownerGoPath,
        string scriptFullName,
        Transform? anchorTransform,
        long ownerPathIdOverride,
        Vector3 localOffset,
        int recursionDepth,
        MonoIlScriptSignals? ilSignals,
        ExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
        {
            ctx.Result.WasTruncatedByCap = true;
            return;
        }

        CatalogAssetEntry? matchedEntry = null;
        if (ctx.Options.AddressablesCatalogs != null)
        {
            for (int c = 0; c < ctx.Options.AddressablesCatalogs.Count; c++)
            {
                var catalog = ctx.Options.AddressablesCatalogs[c];
                if (catalog != null && catalog.TryGetEntriesByGuid(normalizedGuid, out var entries))
                {
                    matchedEntry = SelectBestPrefabCatalogEntry(entries);
                    if (matchedEntry != null)
                    {
                        break;
                    }
                }
            }
        }

        if (matchedEntry == null)
        {
            var hint = new UnresolvedDynamicSpawnerHint
            {
                OwnerGameObjectPath = ownerGoPath,
                OwnerPathId = ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
                ScriptClass = scriptFullName,
                FieldPath = fieldPath,
                Confidence = ScriptSpawnConfidence.Medium,
                Reason = "Addressables AssetReference GUID could not be matched to a prefab entry in the loaded catalogs."
            };
            hint.CandidateReferences.Add(normalizedGuid);
            ctx.Result.UnresolvedDynamicSpawners.Add(hint);
            return;
        }

        if (IsNonPrefabAssetPath(matchedEntry.InternalId))
        {
            return;
        }

        GameObject? resolvedPrefab = ResolveAddressablesPrefab(normalizedGuid, matchedEntry, ctx);
        if (resolvedPrefab != null)
        {
            resolvedPrefab = WalkToPrefabRoot(resolvedPrefab);
        }

        var prefabCycleKey = BuildPrefabCycleKey(
            resolvedPrefab?.assetsFile?.fileName ?? matchedEntry.PrimaryBundleFileName,
            resolvedPrefab?.m_PathID ?? 0,
            normalizedGuid);

        if (ctx.ActivePrefabChain.Contains(prefabCycleKey))
        {
            ctx.Result.CycleGuardsTriggered++;
            return;
        }

        var confirmedByIl = IsFieldConfirmedByMonoIl(fieldPath, ilSignals);
        var confidence = confirmedByIl || resolvedPrefab != null
            ? ScriptSpawnConfidence.High
            : ScriptSpawnConfidence.Medium;

        ctx.Result.ResolvedSpawns.Add(new ResolvedScriptSpawn
        {
            OwnerGameObjectPath = ownerGoPath,
            OwnerPathId = ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
            AnchorTransformPathId = anchorTransform?.m_PathID ?? 0,
            ScriptClass = scriptFullName,
            FieldPath = fieldPath,
            TargetFileName = resolvedPrefab?.assetsFile?.fileName ?? matchedEntry.PrimaryBundleFileName,
            TargetPathId = resolvedPrefab?.m_PathID ?? 0,
            TargetGuid = normalizedGuid,
            TargetBundleFileName = matchedEntry.PrimaryBundleFileName,
            TargetAssetPath = matchedEntry.InternalId,
            Confidence = confidence,
            SourceKind = ScriptSpawnSourceKind.AddressablesGuid,
            RecursionDepth = recursionDepth,
            LocalPositionOffset = localOffset,
            ConfirmedByMonoIl = confirmedByIl,
            OwnerGameObject = ownerGo,
            AnchorTransform = anchorTransform,
            ResolvedPrefabRoot = resolvedPrefab
        });

        if (resolvedPrefab != null)
        {
            TryRecurseIntoSpawnedPrefab(
                resolvedPrefab,
                prefabCycleKey,
                ownerGoPath,
                anchorTransform,
                ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
                recursionDepth,
                ctx,
                cancellationToken);
        }
    }

    private void TryRecurseIntoSpawnedPrefab(
        GameObject prefabRoot,
        string prefabCycleKey,
        string ownerGoPath,
        Transform? anchorTransform,
        long rootOwnerPathId,
        int currentDepth,
        ExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        int nextDepth = currentDepth + 1;
        if (nextDepth > ctx.Options.MaxRecursionDepth)
        {
            ctx.Result.MaxDepthGuardsTriggered++;
            return;
        }

        if (!ctx.ActivePrefabChain.Add(prefabCycleKey))
        {
            ctx.Result.CycleGuardsTriggered++;
            return;
        }

        try
        {
            var prefabSubtree = new List<(GameObject GameObject, string Path)>();
            var dummyGoIds = new HashSet<(string, long)>();
            var dummyTrIds = new HashSet<(string, long)>();
            var dummyFiles = new List<SerializedFile>();
            var dummySeen = new HashSet<SerializedFile>();

            CollectHierarchy(
                new[] { prefabRoot },
                includeInactive: true,
                prefabSubtree,
                dummyGoIds,
                dummyTrIds,
                dummyFiles,
                dummySeen,
                cancellationToken);

            for (int i = 0; i < prefabSubtree.Count; i++)
            {
                if (ctx.Result.ResolvedSpawns.Count >= ctx.Options.MaxSpawnedInstances)
                {
                    ctx.Result.WasTruncatedByCap = true;
                    break;
                }

                var (subGo, subPath) = prefabSubtree[i];
                AnalyzeGameObjectMonoBehaviours(
                    subGo,
                    $"{ownerGoPath}/[ScriptSpawned] {subPath}",
                    anchorOverride: anchorTransform,
                    ownerPathIdOverride: rootOwnerPathId,
                    recursionDepth: nextDepth,
                    ctx: ctx,
                    cancellationToken: cancellationToken);
            }
        }
        finally
        {
            ctx.ActivePrefabChain.Remove(prefabCycleKey);
        }
    }

    private static void MaybeRecordDynamicStringSpawnerHint(
        string fieldName,
        string fieldPath,
        string stringValue,
        GameObject ownerGo,
        string ownerGoPath,
        long ownerPathIdOverride,
        string scriptFullName,
        ExtractionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(stringValue))
        {
            return;
        }

        var trimmed = stringValue.Trim();
        bool fieldIndicatesDynamicSpawn =
            fieldName.Contains("prefabPath", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("resourcePath", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("addressableKey", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("spawnKey", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("prefabName", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("prefabKey", StringComparison.OrdinalIgnoreCase)
            || (fieldName.Contains("assetPath", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));

        if (!fieldIndicatesDynamicSpawn)
        {
            return;
        }

        var hint = new UnresolvedDynamicSpawnerHint
        {
            OwnerGameObjectPath = ownerGoPath,
            OwnerPathId = ownerPathIdOverride != 0 ? ownerPathIdOverride : ownerGo.m_PathID,
            ScriptClass = scriptFullName,
            FieldPath = fieldPath,
            Confidence = ScriptSpawnConfidence.Low,
            Reason = "String-keyed prefab or resource path requires runtime loading and cannot be placed statically."
        };
        hint.CandidateReferences.Add(trimmed);
        ctx.Result.UnresolvedDynamicSpawners.Add(hint);
    }

    private static Transform? FindSiblingAnchorTransform(
        IDictionary dict,
        string prefix,
        MonoBehaviour mb,
        Dictionary<string, string>? typeTreePathTypes,
        ExtractionContext ctx)
    {
        foreach (DictionaryEntry entry in dict)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(key) || StandardHeaderFields.Contains(key))
            {
                continue;
            }

            if (entry.Value is not IDictionary childDict || !TryReadPPtrDictionary(childDict, out int fileId, out long pathId) || pathId == 0)
            {
                continue;
            }

            if (fileId != 0)
            {
                continue;
            }

            var fullPath = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            bool nameLooksLikeAnchor =
                key.Contains("spawnPoint", StringComparison.OrdinalIgnoreCase)
                || key.Contains("anchor", StringComparison.OrdinalIgnoreCase)
                || key.Contains("socket", StringComparison.OrdinalIgnoreCase)
                || key.Contains("mount", StringComparison.OrdinalIgnoreCase)
                || key.Contains("point", StringComparison.OrdinalIgnoreCase)
                || key.Contains("parent", StringComparison.OrdinalIgnoreCase);

            bool typeIsTransform = typeTreePathTypes != null
                && typeTreePathTypes.TryGetValue(NormalizeFieldPathForTypeTree(fullPath), out var ttType)
                && (string.Equals(ttType, "Transform", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ttType, "RectTransform", StringComparison.OrdinalIgnoreCase));

            if (!nameLooksLikeAnchor && !typeIsTransform)
            {
                continue;
            }

            var keyTuple = (mb.assetsFile?.fileName ?? string.Empty, pathId);
            if (!ctx.SceneTransformIds.Contains(keyTuple))
            {
                continue;
            }

            if (mb.assetsFile != null && mb.assetsFile.TryGetObject(pathId, out var obj) && obj is Transform anchorTr)
            {
                return anchorTr;
            }
        }

        return null;
    }

    private static Vector3 FindSiblingLocalOffset(IDictionary dict)
    {
        foreach (DictionaryEntry entry in dict)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(key) || StandardHeaderFields.Contains(key))
            {
                continue;
            }

            bool nameLooksLikeOffset =
                key.Contains("offset", StringComparison.OrdinalIgnoreCase)
                || key.Contains("localPosition", StringComparison.OrdinalIgnoreCase)
                || key.Contains("spawnPosition", StringComparison.OrdinalIgnoreCase);

            if (!nameLooksLikeOffset || entry.Value is not IDictionary vecDict)
            {
                continue;
            }

            if (vecDict.Contains("x") && vecDict.Contains("y") && vecDict.Contains("z"))
            {
                try
                {
                    float x = Convert.ToSingle(vecDict["x"], CultureInfo.InvariantCulture);
                    float y = Convert.ToSingle(vecDict["y"], CultureInfo.InvariantCulture);
                    float z = Convert.ToSingle(vecDict["z"], CultureInfo.InvariantCulture);
                    if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z))
                    {
                        return new Vector3(x, y, z);
                    }
                }
                catch
                {
                }
            }
        }

        return Vector3.Zero;
    }

    private static GameObject? ResolveTargetGameObject(
        SerializedFile? sourceFile,
        int fileId,
        long pathId,
        string targetFileName,
        ExtractionContext ctx)
    {
        if (sourceFile != null)
        {
            var pptr = new PPtr<Object>(fileId, pathId, sourceFile);
            if (pptr.TryGet(out var resolvedObj) && resolvedObj != null)
            {
                if (resolvedObj is GameObject go)
                {
                    return go;
                }
                if (resolvedObj is Transform tr && tr.m_GameObject != null && tr.m_GameObject.TryGet(out var trGo))
                {
                    return trGo;
                }
            }
        }

        if (ctx.Options.ExternalGameObjectResolver != null)
        {
            var customResolved = ctx.Options.ExternalGameObjectResolver(targetFileName, pathId);
            if (customResolved != null)
            {
                return customResolved;
            }
        }

        if (!string.IsNullOrEmpty(targetFileName))
        {
            for (int i = 0; i < ctx.LoadedSerializedFiles.Count; i++)
            {
                var sf = ctx.LoadedSerializedFiles[i];
                if (!string.Equals(sf.fileName, targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sf.TryGetObject(pathId, out var obj))
                {
                    if (obj is GameObject go) return go;
                    if (obj is Transform tr && tr.m_GameObject != null && tr.m_GameObject.TryGet(out var trGo)) return trGo;
                }
            }
        }

        return null;
    }

    private static GameObject? ResolveAddressablesPrefab(
        string normalizedGuid,
        CatalogAssetEntry entry,
        ExtractionContext ctx)
    {
        if (ctx.Options.AddressablesPrefabResolver != null)
        {
            var custom = ctx.Options.AddressablesPrefabResolver(normalizedGuid, entry);
            if (custom != null)
            {
                return custom;
            }
        }

        var expectedContainerPath = entry.InternalId.Replace('\\', '/');
        var expectedPrimaryKey = entry.PrimaryKey.Replace('\\', '/');

        for (int i = 0; i < ctx.LoadedSerializedFiles.Count; i++)
        {
            var sf = ctx.LoadedSerializedFiles[i];
            if (!string.IsNullOrWhiteSpace(entry.PrimaryBundleFileName)
                && !string.IsNullOrWhiteSpace(sf.originalPath)
                && !string.Equals(Path.GetFileName(sf.originalPath), entry.PrimaryBundleFileName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sf.fileName, entry.PrimaryBundleFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var obj in sf.Objects.ToArray())
            {
                if (obj is not AssetBundle ab || ab.m_Container == null)
                {
                    continue;
                }

                for (int c = 0; c < ab.m_Container.Length; c++)
                {
                    var kv = ab.m_Container[c];
                    var containerKey = kv.Key?.Replace('\\', '/') ?? string.Empty;
                    if (string.IsNullOrEmpty(containerKey))
                    {
                        continue;
                    }

                    bool match =
                        (!string.IsNullOrEmpty(expectedContainerPath)
                            && string.Equals(containerKey, expectedContainerPath, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrEmpty(expectedPrimaryKey)
                            && string.Equals(containerKey, expectedPrimaryKey, StringComparison.OrdinalIgnoreCase));

                    if (!match || kv.Value?.asset == null || kv.Value.asset.IsNull)
                    {
                        continue;
                    }

                    if (kv.Value.asset.TryGet(out var assetObj))
                    {
                        if (assetObj is GameObject go) return go;
                        if (assetObj is Transform tr && tr.m_GameObject != null && tr.m_GameObject.TryGet(out var trGo)) return trGo;
                    }
                }
            }
        }

        return null;
    }

    private static GameObject WalkToPrefabRoot(GameObject candidate)
    {
        EnsureTransformLinked(candidate);
        var currentGo = candidate;
        var visited = new HashSet<Transform>();
        var currentTr = candidate.m_Transform;

        while (currentTr != null && visited.Add(currentTr))
        {
            if (currentTr.m_Father == null
                || currentTr.m_Father.IsNull
                || !currentTr.m_Father.TryGet(out var fatherTr)
                || fatherTr == null)
            {
                break;
            }

            if (fatherTr.m_GameObject != null
                && !fatherTr.m_GameObject.IsNull
                && fatherTr.m_GameObject.TryGet(out var fatherGo)
                && fatherGo != null)
            {
                currentGo = fatherGo;
                currentTr = fatherTr;
            }
            else
            {
                break;
            }
        }

        return currentGo;
    }

    private static (OrderedDictionary? Fields, TypeTree? TypeTree) ReadMonoBehaviourFields(
        MonoBehaviour mb,
        SceneScriptSpawnAnalysisOptions options)
    {
        if (options.CustomFieldProvider != null)
        {
            try
            {
                var custom = options.CustomFieldProvider(mb);
                if (custom.Fields != null || custom.TypeTree != null)
                {
                    return custom;
                }
            }
            catch
            {
            }
        }

        var embeddedTree = mb.serializedType?.m_Type;
        if (embeddedTree?.m_Nodes is { Count: > 0 })
        {
            try
            {
                return (mb.ToType(embeddedTree), embeddedTree);
            }
            catch
            {
                return (null, embeddedTree);
            }
        }

        if (options.AssemblyLoader != null)
        {
            try
            {
                var convertedTree = mb.ConvertToTypeTree(options.AssemblyLoader);
                if (convertedTree?.m_Nodes is { Count: > 0 })
                {
                    return (mb.ToType(convertedTree), convertedTree);
                }
            }
            catch
            {
            }
        }

        return (null, null);
    }

    private static (string AssemblyName, string Namespace, string ClassName) ResolveScriptIdentity(MonoBehaviour mb)
    {
        if (mb.m_Script != null && !mb.m_Script.IsNull && mb.m_Script.TryGet(out var script) && script != null)
        {
            return (
                script.m_AssemblyName ?? string.Empty,
                script.m_Namespace ?? string.Empty,
                script.m_ClassName ?? string.Empty);
        }

        var fallbackName = !string.IsNullOrWhiteSpace(mb.m_Name) ? mb.m_Name : $"MonoBehaviour_{mb.m_PathID}";
        return (string.Empty, string.Empty, fallbackName);
    }

    private static CatalogAssetEntry? SelectBestPrefabCatalogEntry(IReadOnlyList<CatalogAssetEntry> entries)
    {
        CatalogAssetEntry? fallback = null;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || e.IsScene)
            {
                continue;
            }

            if (e.InternalId.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }

            if (fallback == null && !IsNonPrefabAssetPath(e.InternalId))
            {
                fallback = e;
            }
        }

        return fallback;
    }

    private static bool IsNonPrefabAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        for (int i = 0; i < NonPrefabAssetExtensions.Length; i++)
        {
            if (trimmed.EndsWith(NonPrefabAssetExtensions[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractAssetReferenceGuid(IDictionary dict, out string normalizedGuid)
    {
        normalizedGuid = string.Empty;
        if (!dict.Contains("m_AssetGUID") || dict["m_AssetGUID"] is not string rawGuid)
        {
            return false;
        }

        return AddressablesCatalogDependencyResolver.TryNormalizeAssetGuid(rawGuid, out normalizedGuid);
    }

    private static bool TryReadPPtrDictionary(IDictionary dict, out int fileId, out long pathId)
    {
        fileId = 0;
        pathId = 0;
        if (dict.Count != 2 || !dict.Contains("m_FileID") || !dict.Contains("m_PathID"))
        {
            return false;
        }

        try
        {
            fileId = Convert.ToInt32(dict["m_FileID"], CultureInfo.InvariantCulture);
            pathId = Convert.ToInt64(dict["m_PathID"], CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ExtractTypeTreeFieldTypes(TypeTree typeTree)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodes = typeTree.m_Nodes;
        if (nodes == null || nodes.Count < 2)
        {
            return result;
        }

        var pathStack = new Stack<(int Level, string Name)>();
        for (int i = 1; i < nodes.Count; i++)
        {
            var node = nodes[i];
            while (pathStack.Count > 0 && pathStack.Peek().Level >= node.m_Level)
            {
                pathStack.Pop();
            }

            if (string.Equals(node.m_Type, "Array", StringComparison.Ordinal)
                || string.Equals(node.m_Name, "Array", StringComparison.Ordinal)
                || string.Equals(node.m_Name, "data", StringComparison.Ordinal))
            {
                if (TryExtractPPtrInnerType(node.m_Type, out var elemPptrType) && pathStack.Count > 0)
                {
                    var arrPath = BuildPathFromStack(pathStack);
                    if (!string.IsNullOrEmpty(arrPath))
                    {
                        result[arrPath] = elemPptrType;
                    }
                }
                continue;
            }

            pathStack.Push((node.m_Level, node.m_Name ?? string.Empty));
            if (TryExtractPPtrInnerType(node.m_Type, out var pptrInner))
            {
                var fullPath = BuildPathFromStack(pathStack);
                if (!string.IsNullOrEmpty(fullPath))
                {
                    result[fullPath] = pptrInner;
                }
            }
        }

        return result;
    }

    private static string BuildPathFromStack(Stack<(int Level, string Name)> stack)
    {
        var items = stack.Reverse().Select(static x => x.Name).Where(static n => !string.IsNullOrEmpty(n));
        return string.Join('.', items);
    }

    private static bool TryExtractPPtrInnerType(string? rawType, out string innerType)
    {
        innerType = string.Empty;
        if (string.IsNullOrWhiteSpace(rawType)
            || !rawType.StartsWith("PPtr<", StringComparison.Ordinal)
            || !rawType.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var inner = rawType.Substring(5, rawType.Length - 6).Trim();
        if (inner.StartsWith('$'))
        {
            inner = inner.Substring(1);
        }

        innerType = inner;
        return !string.IsNullOrEmpty(innerType);
    }

    private static string LookupDeclaredPPtrInnerType(
        string fieldPath,
        Dictionary<string, string>? topLevelTypes,
        Dictionary<string, string>? pathTypes)
    {
        var normalized = NormalizeFieldPathForTypeTree(fieldPath);
        if (pathTypes != null && pathTypes.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        var topField = ExtractTopLevelFieldName(fieldPath);
        if (topLevelTypes != null && topLevelTypes.TryGetValue(topField, out var csType))
        {
            if (csType.StartsWith("List<", StringComparison.Ordinal) && csType.EndsWith(">", StringComparison.Ordinal))
            {
                return csType.Substring(5, csType.Length - 6);
            }
            if (csType.EndsWith("[]", StringComparison.Ordinal))
            {
                return csType.Substring(0, csType.Length - 2);
            }
            return csType;
        }

        return string.Empty;
    }

    private static string NormalizeFieldPathForTypeTree(string fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath) || fieldPath.IndexOf('[') < 0)
        {
            return fieldPath;
        }

        var sb = new System.Text.StringBuilder(fieldPath.Length);
        bool inBracket = false;
        for (int i = 0; i < fieldPath.Length; i++)
        {
            char ch = fieldPath[i];
            if (ch == '[')
            {
                inBracket = true;
            }
            else if (ch == ']')
            {
                inBracket = false;
            }
            else if (!inBracket)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string ExtractTopLevelFieldName(string fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath))
        {
            return string.Empty;
        }

        int cut = fieldPath.Length;
        int dot = fieldPath.IndexOf('.');
        if (dot >= 0 && dot < cut) cut = dot;
        int bracket = fieldPath.IndexOf('[');
        if (bracket >= 0 && bracket < cut) cut = bracket;
        return fieldPath.Substring(0, cut);
    }

    private static bool FieldNameSuggestsPrefab(string fieldPath)
    {
        return fieldPath.Contains("prefab", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("spawn", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("template", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("prototype", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("instance", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("enemy", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("prop", StringComparison.OrdinalIgnoreCase)
            || fieldPath.Contains("object", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFieldConfirmedByMonoIl(string fieldPath, MonoIlScriptSignals? ilSignals)
    {
        if (ilSignals == null || ilSignals.InstantiateFields.Count == 0 || string.IsNullOrWhiteSpace(fieldPath))
        {
            return false;
        }

        var topField = ExtractTopLevelFieldName(fieldPath);
        if (ilSignals.InstantiateFields.Contains(topField))
        {
            return true;
        }

        var normalized = NormalizeFieldPathForTypeTree(fieldPath);
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < normalized.Length)
        {
            var leafField = normalized.Substring(lastDot + 1);
            if (ilSignals.InstantiateFields.Contains(leafField))
            {
                return true;
            }
        }

        return false;
    }

    private static ScriptSpawnConfidence ComputePPtrConfidence(
        bool confirmedByIl,
        bool hasResolvedPrefab,
        string declaredType,
        string fieldPath)
    {
        if (confirmedByIl)
        {
            return ScriptSpawnConfidence.High;
        }

        if (hasResolvedPrefab && (FieldNameSuggestsPrefab(fieldPath) || string.Equals(declaredType, "Prefab", StringComparison.OrdinalIgnoreCase)))
        {
            return ScriptSpawnConfidence.High;
        }

        if (hasResolvedPrefab || (!string.IsNullOrEmpty(declaredType) && SpawnablePPtrTypes.Contains(declaredType)))
        {
            return ScriptSpawnConfidence.Medium;
        }

        return ScriptSpawnConfidence.Low;
    }

    private static string ResolveExternalFileName(SerializedFile? sourceFile, int fileId)
    {
        if (sourceFile == null)
        {
            return string.Empty;
        }

        if (fileId == 0)
        {
            return sourceFile.fileName ?? string.Empty;
        }

        if (fileId > 0 && sourceFile.m_Externals != null && fileId - 1 < sourceFile.m_Externals.Count)
        {
            var ext = sourceFile.m_Externals[fileId - 1];
            if (!string.IsNullOrWhiteSpace(ext?.fileName))
            {
                return ext.fileName;
            }
            if (!string.IsNullOrWhiteSpace(ext?.pathName))
            {
                var normalized = ext.pathName.Replace('\\', '/').TrimEnd('/');
                var slash = normalized.LastIndexOf('/');
                return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            }
        }

        return string.Empty;
    }

    private static string BuildPrefabCycleKey(string fileName, long pathId, string guid)
    {
        if (!string.IsNullOrEmpty(guid))
        {
            return $"guid:{guid}";
        }

        return $"pptr:{fileName}#{pathId.ToString(CultureInfo.InvariantCulture)}";
    }

    // ------------------------------------------------------------------
    // Lightweight Managed Assembly IL Inspection (pure BCL MetadataReader)
    // ------------------------------------------------------------------

    private static MonoIlScriptSignals? GetOrAnalyzeMonoIlSignals(
        string assemblyName,
        string scriptNamespace,
        string scriptClassName,
        IReadOnlyList<string> searchDirectories,
        Dictionary<string, MonoIlScriptSignals?> cache)
    {
        var cacheKey = $"{assemblyName}|{scriptNamespace}|{scriptClassName}";
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        MonoIlScriptSignals? analyzed = null;
        try
        {
            var dllPath = FindManagedAssemblyPath(assemblyName, searchDirectories);
            if (!string.IsNullOrEmpty(dllPath))
            {
                analyzed = InspectManagedTypeIl(dllPath, scriptNamespace, scriptClassName);
            }
        }
        catch
        {
            analyzed = null;
        }

        cache[cacheKey] = analyzed;
        return analyzed;
    }

    private static string? FindManagedAssemblyPath(string assemblyName, IReadOnlyList<string> searchDirectories)
    {
        var candidateNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            var clean = assemblyName.Trim();
            candidateNames.Add(clean.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".dll");
        }
        else
        {
            candidateNames.Add("Assembly-CSharp.dll");
            candidateNames.Add("Assembly-CSharp-firstpass.dll");
        }

        for (int d = 0; d < searchDirectories.Count; d++)
        {
            var dir = searchDirectories[d];
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            for (int n = 0; n < candidateNames.Count; n++)
            {
                var candidate = Path.Combine(dir, candidateNames[n]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static MonoIlScriptSignals? InspectManagedTypeIl(string dllPath, string scriptNamespace, string scriptClassName)
    {
        using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var peReader = new PEReader(fs);
        if (!peReader.HasMetadata)
        {
            return null;
        }

        var md = peReader.GetMetadataReader();
        TypeDefinitionHandle matchedTypeHandle = default;

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(typeHandle);
            var typeName = md.GetString(td.Name);
            if (!string.Equals(typeName, scriptClassName, StringComparison.Ordinal))
            {
                continue;
            }

            var typeNs = GetEffectiveTypeNamespace(md, td);
            if (string.IsNullOrEmpty(scriptNamespace) || string.Equals(typeNs, scriptNamespace, StringComparison.Ordinal))
            {
                matchedTypeHandle = typeHandle;
                break;
            }
        }

        if (matchedTypeHandle.IsNil)
        {
            return null;
        }

        var signals = new MonoIlScriptSignals();
        var matchedType = md.GetTypeDefinition(matchedTypeHandle);

        foreach (var methodHandle in matchedType.GetMethods())
        {
            var methodDef = md.GetMethodDefinition(methodHandle);
            if (methodDef.RelativeVirtualAddress == 0)
            {
                continue;
            }

            MethodBodyBlock body;
            try
            {
                body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress);
            }
            catch
            {
                continue;
            }

            var il = body.GetILBytes();
            if (il == null || il.Length == 0)
            {
                continue;
            }

            ScanMethodIl(il, md, signals);
        }

        return signals;
    }

    private static string GetEffectiveTypeNamespace(MetadataReader md, TypeDefinition td)
    {
        var current = td;
        int guard = 0;
        while (guard++ < 16)
        {
            var ns = md.GetString(current.Namespace);
            if (!string.IsNullOrEmpty(ns))
            {
                return ns;
            }

            var declaringHandle = current.GetDeclaringType();
            if (declaringHandle.IsNil)
            {
                return string.Empty;
            }

            current = md.GetTypeDefinition(declaringHandle);
        }

        return string.Empty;
    }

    private static void ScanMethodIl(byte[] il, MetadataReader md, MonoIlScriptSignals signals)
    {
        var recentLoadedFields = new List<string>(8);
        var recentStringLiterals = new List<string>(4);
        int pos = 0;

        while (pos < il.Length)
        {
            byte op = il[pos++];
            if (op == 0xFE)
            {
                if (pos >= il.Length) break;
                byte op2 = il[pos++];
                pos += GetTwoByteOpcodeOperandLength(op2);
                continue;
            }

            switch (op)
            {
                // ldfld (0x7B), ldflda (0x7C), ldsfld (0x7E), ldsflda (0x7F)
                case 0x7B:
                case 0x7C:
                case 0x7E:
                case 0x7F:
                    if (pos + 4 > il.Length) return;
                    int fieldToken = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    var fieldName = TryResolveFieldTokenName(md, fieldToken);
                    if (!string.IsNullOrEmpty(fieldName))
                    {
                        if (recentLoadedFields.Count >= 8)
                        {
                            recentLoadedFields.RemoveAt(0);
                        }
                        recentLoadedFields.Add(fieldName);
                    }
                    break;

                // ldstr (0x72)
                case 0x72:
                    if (pos + 4 > il.Length) return;
                    int userStrToken = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    try
                    {
                        var handle = MetadataTokens.UserStringHandle(userStrToken & 0x00FFFFFF);
                        var str = md.GetUserString(handle);
                        if (!string.IsNullOrWhiteSpace(str) && str.Length <= 256)
                        {
                            if (recentStringLiterals.Count >= 4)
                            {
                                recentStringLiterals.RemoveAt(0);
                            }
                            recentStringLiterals.Add(str);
                        }
                    }
                    catch
                    {
                    }
                    break;

                // call (0x28), callvirt (0x6F)
                case 0x28:
                case 0x6F:
                    if (pos + 4 > il.Length) return;
                    int methodToken = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    var (declaringType, methodName) = TryResolveMethodTokenName(md, methodToken);
                    if (!string.IsNullOrEmpty(methodName))
                    {
                        if (string.Equals(methodName, "Instantiate", StringComparison.Ordinal)
                            || string.Equals(methodName, "InstantiateAsync", StringComparison.Ordinal))
                        {
                            for (int f = 0; f < recentLoadedFields.Count; f++)
                            {
                                signals.InstantiateFields.Add(recentLoadedFields[f]);
                            }

                            if (recentLoadedFields.Count == 0
                                && (declaringType.Contains("Addressables", StringComparison.Ordinal)
                                    || string.Equals(methodName, "InstantiateAsync", StringComparison.Ordinal)))
                            {
                                signals.DynamicLoaderCalls.Add(string.IsNullOrEmpty(declaringType) ? methodName : $"{declaringType}.{methodName}");
                                AddDistinctStrings(signals.DynamicLiteralKeys, recentStringLiterals);
                            }
                        }
                        else if ((declaringType.Contains("Resources", StringComparison.Ordinal)
                                && (methodName.StartsWith("Load", StringComparison.Ordinal)))
                            || (declaringType.Contains("Addressables", StringComparison.Ordinal)
                                && methodName.StartsWith("LoadAsset", StringComparison.Ordinal))
                            || (declaringType.Contains("AssetBundle", StringComparison.Ordinal)
                                && methodName.StartsWith("LoadAsset", StringComparison.Ordinal)))
                        {
                            signals.DynamicLoaderCalls.Add(string.IsNullOrEmpty(declaringType) ? methodName : $"{declaringType}.{methodName}");
                            AddDistinctStrings(signals.DynamicLiteralKeys, recentStringLiterals);
                        }
                    }
                    break;

                // switch (0x45): int32 count + count * int32 offsets
                case 0x45:
                    if (pos + 4 > il.Length) return;
                    int count = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    if (count < 0 || (long)count * 4 > il.Length - pos) return;
                    pos += count * 4;
                    break;

                default:
                    pos += GetSingleByteOpcodeOperandLength(op);
                    break;
            }
        }
    }

    private static void AddDistinctStrings(List<string> target, List<string> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (!target.Contains(source[i], StringComparer.Ordinal))
            {
                target.Add(source[i]);
            }
        }
    }

    private static string? TryResolveFieldTokenName(MetadataReader md, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            return handle.Kind switch
            {
                HandleKind.FieldDefinition => md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static (string DeclaringType, string MethodName) TryResolveMethodTokenName(MetadataReader md, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            }

            if (handle.Kind == HandleKind.MemberReference)
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                var name = md.GetString(mr.Name);
                var parentName = TryResolveTypeHandleName(md, mr.Parent);
                return (parentName, name);
            }

            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var mdef = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                var name = md.GetString(mdef.Name);
                var parentName = TryResolveTypeHandleName(md, mdef.GetDeclaringType());
                return (parentName, name);
            }
        }
        catch
        {
        }

        return (string.Empty, string.Empty);
    }

    private static string TryResolveTypeHandleName(MetadataReader md, EntityHandle handle)
    {
        try
        {
            return handle.Kind switch
            {
                HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Name),
                HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetSingleByteOpcodeOperandLength(byte op)
    {
        // Standard ECMA-335 single-byte CIL opcode operand sizes
        return op switch
        {
            0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F
                or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37
                or 0xDE => 1,
            0x20 or 0x22 or 0x27 or 0x28 or 0x29 or 0x38 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F
                or 0x40 or 0x41 or 0x42 or 0x43 or 0x44 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75
                or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D or 0x8F
                or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xDD => 4,
            0x21 or 0x23 => 8,
            _ => 0
        };
    }

    private static int GetTwoByteOpcodeOperandLength(byte op2)
    {
        return op2 switch
        {
            0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E => 2,
            0x06 or 0x07 or 0x15 or 0x16 or 0x1C => 4,
            0x12 => 1,
            _ => 0
        };
    }
}
