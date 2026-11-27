using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public enum SceneLodExportMode
    {
        Lod0Only = 0,
        AllLods = 1
    }

    public sealed class LodGroupExclusionResult
    {
        public HashSet<Renderer> ExcludedRenderers { get; } = new HashSet<Renderer>();
        public HashSet<(string FileName, long PathId)> ExcludedRendererIds { get; } = new HashSet<(string FileName, long PathId)>();
        public int FilteredLodGroupCount { get; set; }

        public bool IsExcluded(Renderer? renderer)
        {
            if (renderer == null || (ExcludedRenderers.Count == 0 && ExcludedRendererIds.Count == 0))
            {
                return false;
            }

            if (ExcludedRenderers.Contains(renderer))
            {
                return true;
            }

            if (renderer.m_PathID != 0)
            {
                var key = (renderer.assetsFile?.fileName ?? string.Empty, renderer.m_PathID);
                if (ExcludedRendererIds.Contains(key))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class SceneScriptSpawnAttachment
    {
        public Transform? AnchorTransform { get; init; }
        public GameObject? OwnerGameObject { get; init; }
        public long OwnerPathId { get; init; }
        public long AnchorTransformPathId { get; init; }
        public required GameObject PrefabRoot { get; init; }
        public Vector3 LocalPositionOffset { get; init; }
        public Quaternion LocalRotationOffset { get; init; } = new Quaternion(0, 0, 0, 1);
        public Vector3 LocalScaleMultiplier { get; init; } = Vector3.One;
        public string ScriptClass { get; init; } = string.Empty;
        public string FieldPath { get; init; } = string.Empty;
        public string SourceKind { get; init; } = string.Empty;
        public string Confidence { get; init; } = string.Empty;
        public string TargetFileName { get; init; } = string.Empty;
        public long TargetPathId { get; init; }
        public string TargetGuid { get; init; } = string.Empty;
        public bool PreservePrefabRootName { get; init; }
    }

    public sealed class SceneConversionOptions
    {
        public SceneLodExportMode LodExportMode { get; init; } = SceneLodExportMode.Lod0Only;
        public bool IncludeCamerasAndLights { get; init; } = true;
        public bool SkipDisabledCamerasAndLights { get; init; } = true;
        public bool IncludeNodeProvenanceExtras { get; init; } = true;
        public bool HideAuxiliaryAndShadowOnlyRenderers { get; init; } = true;
        public IReadOnlyList<SceneScriptSpawnAttachment>? ScriptSpawnAttachments { get; init; }
        public int MaxScriptSpawnInstances { get; init; } = 512;
        public int MaxTerrainGridResolution { get; init; } = TerrainMeshConverter.DefaultMaxGridResolution;
        public int MaxTerrainBakedTextureSize { get; init; } = TerrainMeshConverter.DefaultMaxBakedTextureSize;
    }

    public partial class ModelConverter : IImported
    {
        public ImportedFrame RootFrame { get; protected set; } = new ImportedFrame();
        public List<ImportedMesh> MeshList { get; protected set; } = new List<ImportedMesh>();
        public List<ImportedMaterial> MaterialList { get; protected set; } = new List<ImportedMaterial>();
        public List<ImportedTexture> TextureList { get; protected set; } = new List<ImportedTexture>();
        public List<ImportedKeyframedAnimation> AnimationList { get; protected set; } = new List<ImportedKeyframedAnimation>();
        public List<ImportedMorph> MorphList { get; protected set; } = new List<ImportedMorph>();
        public List<ImportedCamera> CameraList { get; protected set; } = new List<ImportedCamera>();
        public List<ImportedLight> LightList { get; protected set; } = new List<ImportedLight>();

        public int SkippedLodRendererCount { get; private set; }
        public int AttachedScriptSpawnCount { get; private set; }
        public int HiddenAuxiliaryRendererCount { get; private set; }
        public int ExportedTerrainCount { get; private set; }

        public Avatar? SourceAvatar => avatar;

        private static readonly AsyncLocal<Func<Material, string, PPtr<Texture>?, Texture2D?>?> AmbientTextureResolver = new();

        public static IDisposable PushTextureResolver(Func<Material, string, PPtr<Texture>?, Texture2D?>? resolver)
        {
            var previous = AmbientTextureResolver.Value;
            AmbientTextureResolver.Value = resolver;
            return new TextureResolverScope(previous);
        }

        private sealed class TextureResolverScope : IDisposable
        {
            private readonly Func<Material, string, PPtr<Texture>?, Texture2D?>? _previous;
            private bool _disposed;

            public TextureResolverScope(Func<Material, string, PPtr<Texture>?, Texture2D?>? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    AmbientTextureResolver.Value = _previous;
                }
            }
        }

        private sealed class CachedConvertedStaticMesh
        {
            public required string GeometryKey { get; init; }
            public required bool HasNormal { get; init; }
            public required bool[] HasUV { get; init; }
            public required bool HasTangent { get; init; }
            public required bool HasColor { get; init; }
            public required List<ImportedVertex> VertexList { get; init; }
            public required List<(int BaseVertex, List<ImportedFace> FaceList)> Submeshes { get; init; }
        }

        private ImageFormat imageFormat;
        private Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver;
        // Optional project-aware resolvers used by full scene export so lazy cross-bundle Mesh / Material PPtrs
        // resolve through the exact same ProjectIndex + SQLite semantic pipeline used by Export Model.
        private Func<Renderer, Mesh?>? meshResolver;
        private Func<Renderer, Mesh, int, Material?>? materialResolver;
        private bool skipEmptyMeshes;
        private Avatar? avatar;
        private bool hasRootFrame;
        private string? animatorRootPath;
        private HashSet<AnimationClip> animationClipHashSet = new HashSet<AnimationClip>();
        private Dictionary<AnimationClip, string> boundAnimationPathDic = new Dictionary<AnimationClip, string>();
        private Dictionary<uint, string> bonePathHash = new Dictionary<uint, string>();
        private Dictionary<Texture2D, string> textureNameDictionary = new Dictionary<Texture2D, string>();
        private Dictionary<Transform, ImportedFrame> transformDictionary = new Dictionary<Transform, ImportedFrame>();
        private readonly Dictionary<Mesh, CachedConvertedStaticMesh> _staticMeshGeometryCache = new Dictionary<Mesh, CachedConvertedStaticMesh>();
        Dictionary<uint, string> morphChannelNames = new Dictionary<uint, string>();
        private readonly List<(Texture2D Texture, string Name, string MaterialName, string PropertyName, bool ForceNormalMapUnpack)> pendingTextures = new();
        private readonly HashSet<string> pendingTextureNames = new HashSet<string>(StringComparer.Ordinal);
        private bool _populateFrameExtras;
        private bool _hideAuxiliaryAndShadowOnlyRenderers;
        private readonly HashSet<ImportedFrame> _framesWithVisibleRenderer = new HashSet<ImportedFrame>();
        private readonly HashSet<ImportedFrame> _framesMarkedHidden = new HashSet<ImportedFrame>();
        private readonly HashSet<Renderer> _excludedLodRenderers = new HashSet<Renderer>();
        private readonly HashSet<(string FileName, long PathId)> _excludedLodRendererIds = new HashSet<(string, long)>();
        private readonly Dictionary<long, ImportedFrame> _frameByGameObjectPathId = new Dictionary<long, ImportedFrame>();
        private readonly Dictionary<long, ImportedFrame> _frameByTransformPathId = new Dictionary<long, ImportedFrame>();

        private void SetRootFrame(ImportedFrame frame)
        {
            RootFrame = frame;
            hasRootFrame = true;
        }

        protected ModelConverter(ImageFormat imageFormat, Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null)
        {
            this.imageFormat = imageFormat;
            this.textureResolver = textureResolver ?? AmbientTextureResolver.Value;
        }

        public ModelConverter(GameObject m_GameObject, ImageFormat imageFormat, AnimationClip[]? animationList = null, Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null)
        {
            this.imageFormat = imageFormat;
            this.textureResolver = textureResolver ?? AmbientTextureResolver.Value;
            if (m_GameObject.m_Animator != null)
            {
                InitWithAnimator(m_GameObject.m_Animator);
                if (animationList == null)
                {
                    CollectAnimationClip(m_GameObject.m_Animator);
                }
            }
            else
            {
                InitWithGameObject(m_GameObject);
            }
            if (avatar == null)
            {
                avatar = FindAvatar(m_GameObject);
            }
            if (animationList != null)
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
            FlushPendingTextures();
        }

        public ModelConverter(string rootName, List<GameObject> m_GameObjects, ImageFormat imageFormat, AnimationClip[]? animationList = null, Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null)
        {
            this.imageFormat = imageFormat;
            this.textureResolver = textureResolver ?? AmbientTextureResolver.Value;
            SetRootFrame(CreateFrame(rootName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One));
            foreach (var m_GameObject in m_GameObjects)
            {
                if (m_GameObject.m_Animator != null && animationList == null)
                {
                    CollectAnimationClip(m_GameObject.m_Animator);
                }

                var m_Transform = m_GameObject.m_Transform;
                ConvertTransforms(m_Transform, RootFrame);
                CreateBonePathHash(m_Transform);
            }
            foreach (var m_GameObject in m_GameObjects)
            {
                var m_Transform = m_GameObject.m_Transform;
                ConvertMeshRenderer(m_Transform);
            }
            if (avatar == null)
            {
                foreach (var go in m_GameObjects)
                {
                    avatar = FindAvatar(go);
                    if (avatar != null) break;
                }
            }
            if (animationList != null)
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
            FlushPendingTextures();
        }

        /// <summary>
        /// Converts an entire Unity scene (all root GameObjects and their Transform hierarchy) into an
        /// <see cref="IImported"/> graph optimized for glTF 2.0 instancing: repeated static meshes reuse
        /// cached vertex/index buffers and carry stable <see cref="ImportedMesh.GeometryKey"/> and
        /// <see cref="ImportedMesh.TargetFrame"/> bindings so duplicate sibling GameObject names never collide.
        /// </summary>
        /// <param name="meshResolver">Optional fallback used when a renderer's Mesh PPtr cannot be resolved in memory
        /// (e.g. lazy cross-bundle references resolved through a project index).</param>
        /// <param name="materialResolver">Optional resolver for a renderer material slot (renderer, mesh, slot index).
        /// When it returns null the in-memory PPtr lookup is used.</param>
        /// <param name="skipDisabledRenderers">Skip renderers whose <see cref="Renderer.m_Enabled"/> flag is false
        /// (hidden proxies, LOD/shadow/debug renderers) while still keeping their Transform nodes.</param>
        public static ModelConverter CreateForScene(
            string sceneName,
            IReadOnlyList<GameObject> rootGameObjects,
            ImageFormat imageFormat = ImageFormat.Png,
            bool includeInactive = true,
            bool includeAnimations = false,
            Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null,
            Action<int, int, string>? progressCallback = null,
            CancellationToken cancellationToken = default,
            Func<Renderer, Mesh?>? meshResolver = null,
            Func<Renderer, Mesh, int, Material?>? materialResolver = null,
            bool skipDisabledRenderers = true,
            SceneConversionOptions? sceneOptions = null)
        {
            ArgumentNullException.ThrowIfNull(rootGameObjects);

            var effectiveOptions = sceneOptions ?? new SceneConversionOptions();
            var converter = new ModelConverter(imageFormat, textureResolver)
            {
                meshResolver = meshResolver,
                materialResolver = materialResolver,
                skipEmptyMeshes = true,
                _populateFrameExtras = effectiveOptions.IncludeNodeProvenanceExtras,
                _hideAuxiliaryAndShadowOnlyRenderers = effectiveOptions.HideAuxiliaryAndShadowOnlyRenderers
            };
            var safeSceneName = string.IsNullOrWhiteSpace(sceneName) ? "Scene" : sceneName.Trim();
            converter.SetRootFrame(CreateFrame(safeSceneName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One));

            var totalRoots = rootGameObjects.Count;
            for (int i = 0; i < totalRoots; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var go = rootGameObjects[i];
                if (go == null || (!includeInactive && !go.m_IsActive))
                {
                    continue;
                }

                // Lazily materialized GameObjects (Lazy Mode) are not linked to their components by the
                // eager ProcessAssets pass, so link Transform / MeshFilter / Renderer components for the whole subtree.
                HydrateGameObjectHierarchy(go);

                if (includeAnimations && go.m_Animator != null)
                {
                    converter.CollectAnimationClip(go.m_Animator);
                }

                var tr = go.m_Transform;
                if (tr != null)
                {
                    converter.ConvertTransformsForScene(tr, converter.RootFrame, includeInactive, cancellationToken);
                    converter.CreateBonePathHash(tr);
                }

                if ((i & 31) == 0 || i == totalRoots - 1)
                {
                    progressCallback?.Invoke(i + 1, Math.Max(1, totalRoots * 2), $"Building Transform tree ({i + 1}/{totalRoots})...");
                }
            }

            if (effectiveOptions.LodExportMode == SceneLodExportMode.Lod0Only)
            {
                converter.CollectLodGroupExclusions(rootGameObjects, includeInactive, cancellationToken);
            }

            var visitedRenderers = new HashSet<Transform>();
            for (int i = 0; i < totalRoots; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var go = rootGameObjects[i];
                if (go == null || (!includeInactive && !go.m_IsActive))
                {
                    continue;
                }

                var tr = go.m_Transform;
                if (tr != null)
                {
                    converter.ConvertMeshRendererForScene(
                        tr,
                        visitedRenderers,
                        includeInactive,
                        includeAnimations,
                        skipDisabledRenderers,
                        effectiveOptions,
                        cancellationToken);
                }

                if ((i & 31) == 0 || i == totalRoots - 1)
                {
                    progressCallback?.Invoke(totalRoots + i + 1, Math.Max(1, totalRoots * 2), $"Converting scene meshes ({i + 1}/{totalRoots})...");
                }
            }

            if (effectiveOptions.ScriptSpawnAttachments is { Count: > 0 })
            {
                converter.AttachScriptSpawnedPrefabs(
                    effectiveOptions.ScriptSpawnAttachments,
                    includeInactive,
                    skipDisabledRenderers,
                    effectiveOptions,
                    cancellationToken);
            }

            if (includeAnimations)
            {
                if (converter.avatar == null)
                {
                    for (int i = 0; i < totalRoots; i++)
                    {
                        var go = rootGameObjects[i];
                        if (go != null)
                        {
                            converter.avatar = converter.FindAvatar(go);
                            if (converter.avatar != null) break;
                        }
                    }
                }
                converter.ConvertAnimations();
            }

            converter.FlushPendingTextures();
            return converter;
        }

        private void ConvertTransformsForScene(
            Transform? trans,
            ImportedFrame parent,
            bool includeInactive,
            CancellationToken cancellationToken)
        {
            if (trans == null || transformDictionary.ContainsKey(trans))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!includeInactive && TryResolveTransformGameObject(trans, out var go) && go != null && !go.m_IsActive)
            {
                return;
            }

            var frame = ConvertTransform(trans);
            parent.AddChild(frame);

            if (trans.m_Children == null)
            {
                return;
            }

            foreach (var pptr in trans.m_Children)
            {
                var childPtr = pptr;
                if (childPtr == null || childPtr.IsNull)
                {
                    continue;
                }

                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in SnapshotAssetsFiles(trans.assetsFile?.assetsManager))
                    {
                        if (sf.TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }

                if (child != null)
                {
                    ConvertTransformsForScene(child, frame, includeInactive, cancellationToken);
                }
            }
        }

        private void ConvertMeshRendererForScene(
            Transform? m_Transform,
            HashSet<Transform> visited,
            bool includeInactive,
            bool includeAnimations,
            bool skipDisabledRenderers,
            SceneConversionOptions sceneOptions,
            CancellationToken cancellationToken)
        {
            if (m_Transform == null || !visited.Add(m_Transform))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (TryResolveTransformGameObject(m_Transform, out var m_GameObject) && m_GameObject != null)
            {
                if (!includeInactive && !m_GameObject.m_IsActive)
                {
                    return;
                }

                if (m_GameObject.m_MeshRenderer != null
                    && (!skipDisabledRenderers || m_GameObject.m_MeshRenderer.m_Enabled))
                {
                    if (IsExcludedByLodGroup(m_GameObject.m_MeshRenderer))
                    {
                        SkippedLodRendererCount++;
                    }
                    else
                    {
                        ConvertMeshRenderer(m_GameObject.m_MeshRenderer);
                    }
                }

                if (m_GameObject.m_SkinnedMeshRenderer != null
                    && (!skipDisabledRenderers || m_GameObject.m_SkinnedMeshRenderer.m_Enabled))
                {
                    if (IsExcludedByLodGroup(m_GameObject.m_SkinnedMeshRenderer))
                    {
                        SkippedLodRendererCount++;
                    }
                    else
                    {
                        ConvertMeshRenderer(m_GameObject.m_SkinnedMeshRenderer);
                    }
                }

                var targetFrame = transformDictionary.TryGetValue(m_Transform, out var foundFrame) ? foundFrame : null;
                ConvertTerrainFromGameObject(m_GameObject, targetFrame, skipDisabledRenderers, sceneOptions);

                if (sceneOptions.IncludeCamerasAndLights)
                {
                    ExtractCamerasAndLightsFromGameObject(m_GameObject, targetFrame, sceneOptions.SkipDisabledCamerasAndLights);
                }

                if (includeAnimations && m_GameObject.m_Animation != null)
                {
                    foreach (var animation in m_GameObject.m_Animation.m_Animations)
                    {
                        var animationPtr = animation;
                        if (animationPtr == null || animationPtr.IsNull)
                        {
                            continue;
                        }
                        if (!animationPtr.TryGet(out var animationClip))
                        {
                            foreach (var sf in SnapshotAssetsFiles(m_Transform.assetsFile?.assetsManager))
                            {
                                if (sf.TryGetObject(animationPtr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                {
                                    animationClip = foundClip;
                                    break;
                                }
                            }
                        }
                        if (animationClip != null)
                        {
                            var transformPath = GetTransformPath(m_Transform);
                            if (transformPath != null && !boundAnimationPathDic.ContainsKey(animationClip))
                            {
                                boundAnimationPathDic.Add(animationClip, transformPath);
                            }
                            animationClipHashSet.Add(animationClip);
                        }
                    }
                }
            }

            if (m_Transform.m_Children == null)
            {
                return;
            }

            foreach (var pptr in m_Transform.m_Children)
            {
                var childPtr = pptr;
                if (childPtr == null || childPtr.IsNull)
                {
                    continue;
                }

                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in SnapshotAssetsFiles(m_Transform.assetsFile?.assetsManager))
                    {
                        if (sf.TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }

                if (child != null)
                {
                    ConvertMeshRendererForScene(child, visited, includeInactive, includeAnimations, skipDisabledRenderers, sceneOptions, cancellationToken);
                }
            }
        }

        private void CollectLodGroupExclusions(
            IReadOnlyList<GameObject> rootGameObjects,
            bool includeInactive,
            CancellationToken cancellationToken)
        {
            var lodResult = ComputeLodGroupExclusions(rootGameObjects, targetLodLevel: 0, includeInactive: includeInactive, cancellationToken: cancellationToken);
            _excludedLodRenderers.UnionWith(lodResult.ExcludedRenderers);
            _excludedLodRendererIds.UnionWith(lodResult.ExcludedRendererIds);
        }

        /// <summary>
        /// Single shared LODGroup filter used by both scene .glb conversion and Unity project/package export.
        /// Keeps all renderers belonging to the resolved target LOD tier (including renderers shared with other tiers)
        /// and excludes renderers unique to non-target tiers. Supports both binary LODGroup and TypeTree fallback.
        /// </summary>
        public static LodGroupExclusionResult ComputeLodGroupExclusions(
            IReadOnlyList<GameObject> rootGameObjects,
            int targetLodLevel = 0,
            bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var result = new LodGroupExclusionResult();
            if (rootGameObjects == null || rootGameObjects.Count == 0)
            {
                return result;
            }

            int requestedLod = Math.Max(0, targetLodLevel);
            var visited = new HashSet<Transform>();
            var keptRenderers = new HashSet<Renderer>();
            var keptIds = new HashSet<(string FileName, long PathId)>();
            var perGroupCandidates = new List<(List<Renderer> Rends, List<(string FileName, long PathId)> Keys)>();

            var stack = new Stack<Transform>();
            for (int i = rootGameObjects.Count - 1; i >= 0; i--)
            {
                var rootGo = rootGameObjects[i];
                if (rootGo?.m_Transform == null || (!includeInactive && !rootGo.m_IsActive))
                {
                    continue;
                }

                stack.Push(rootGo.m_Transform);
            }

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trans = stack.Pop();
                if (trans == null || !visited.Add(trans))
                {
                    continue;
                }

                if (TryResolveTransformGameObject(trans, out var go) && go != null)
                {
                    if (!includeInactive && !go.m_IsActive)
                    {
                        continue;
                    }

                    CollectLodExclusionsFromGameObjectStatic(
                        go,
                        requestedLod,
                        keptRenderers,
                        keptIds,
                        result.ExcludedRenderers,
                        result.ExcludedRendererIds,
                        perGroupCandidates);
                }

                if (trans.m_Children == null || trans.m_Children.Length == 0)
                {
                    continue;
                }

                for (int i = trans.m_Children.Length - 1; i >= 0; i--)
                {
                    var childPtr = trans.m_Children[i];
                    if (childPtr == null || childPtr.IsNull)
                    {
                        continue;
                    }

                    if (!childPtr.TryGet(out var child))
                    {
                        foreach (var sf in SnapshotAssetsFiles(trans.assetsFile?.assetsManager))
                        {
                            if (sf.TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                            {
                                child = foundChild;
                                break;
                            }
                        }
                    }

                    if (child != null)
                    {
                        stack.Push(child);
                    }
                }
            }

            result.ExcludedRenderers.ExceptWith(keptRenderers);
            result.ExcludedRendererIds.ExceptWith(keptIds);

            for (int i = 0; i < perGroupCandidates.Count; i++)
            {
                var (rends, keys) = perGroupCandidates[i];
                bool anyExcluded = false;
                for (int r = 0; r < rends.Count; r++)
                {
                    if (result.ExcludedRenderers.Contains(rends[r]))
                    {
                        anyExcluded = true;
                        break;
                    }
                }
                if (!anyExcluded)
                {
                    for (int k = 0; k < keys.Count; k++)
                    {
                        if (result.ExcludedRendererIds.Contains(keys[k]))
                        {
                            anyExcluded = true;
                            break;
                        }
                    }
                }

                if (anyExcluded)
                {
                    result.FilteredLodGroupCount++;
                }
            }

            return result;
        }

        private static void CollectLodExclusionsFromGameObjectStatic(
            GameObject go,
            int requestedLod,
            HashSet<Renderer> keptRenderers,
            HashSet<(string FileName, long PathId)> keptIds,
            HashSet<Renderer> excludedRenderers,
            HashSet<(string FileName, long PathId)> excludedIds,
            List<(List<Renderer> Rends, List<(string FileName, long PathId)> Keys)> perGroupCandidates)
        {
            if (go.m_Components == null)
            {
                return;
            }

            for (int i = 0; i < go.m_Components.Length; i++)
            {
                var compPtr = go.m_Components[i];
                if (compPtr == null || compPtr.IsNull)
                {
                    continue;
                }

                if (!TryResolveComponent(go, compPtr, out var comp) || comp is not LODGroup lodGroup)
                {
                    continue;
                }

                if (!lodGroup.m_Enabled)
                {
                    continue;
                }

                var lods = ExtractLodLevelsForFiltering(lodGroup);
                if (lods.Count == 0)
                {
                    continue;
                }

                int selectedLodIndex = SelectTargetLodLevelIndex(lods, requestedLod);
                if (selectedLodIndex < 0)
                {
                    continue;
                }

                var groupExcludedRends = new List<Renderer>();
                var groupExcludedKeys = new List<(string FileName, long PathId)>();

                for (int l = 0; l < lods.Count; l++)
                {
                    var lod = lods[l];
                    if (lod?.renderers == null)
                    {
                        continue;
                    }

                    bool isSelectedTier = l == selectedLodIndex;
                    for (int r = 0; r < lod.renderers.Length; r++)
                    {
                        var rendPtr = lod.renderers[r];
                        if (rendPtr == null || rendPtr.IsNull)
                        {
                            continue;
                        }

                        var rend = TryResolveRenderer(lodGroup, rendPtr);
                        if (rend != null)
                        {
                            var key = BuildRendererKey(rend);
                            if (isSelectedTier)
                            {
                                keptRenderers.Add(rend);
                                if (key.HasValue) keptIds.Add(key.Value);
                            }
                            else
                            {
                                excludedRenderers.Add(rend);
                                groupExcludedRends.Add(rend);
                                if (key.HasValue)
                                {
                                    excludedIds.Add(key.Value);
                                    groupExcludedKeys.Add(key.Value);
                                }
                            }
                        }
                        else if (!isSelectedTier && rendPtr.m_PathID != 0)
                        {
                            var fileKey = ResolvePPtrFileName(lodGroup.assetsFile, rendPtr.m_FileID);
                            var pair = (fileKey, rendPtr.m_PathID);
                            excludedIds.Add(pair);
                            groupExcludedKeys.Add(pair);
                        }
                    }
                }

                if (groupExcludedRends.Count > 0 || groupExcludedKeys.Count > 0)
                {
                    perGroupCandidates.Add((groupExcludedRends, groupExcludedKeys));
                }
            }
        }

        private static int SelectTargetLodLevelIndex(IReadOnlyList<LOD> lods, int requestedLod)
        {
            static bool HasRenderers(LOD? lod)
            {
                if (lod?.renderers == null || lod.renderers.Length == 0)
                {
                    return false;
                }

                for (int i = 0; i < lod.renderers.Length; i++)
                {
                    if (lod.renderers[i] != null && !lod.renderers[i].IsNull)
                    {
                        return true;
                    }
                }

                return false;
            }

            if (requestedLod >= 0 && requestedLod < lods.Count && HasRenderers(lods[requestedLod]))
            {
                return requestedLod;
            }

            for (int i = Math.Min(requestedLod + 1, lods.Count); i < lods.Count; i++)
            {
                if (HasRenderers(lods[i]))
                {
                    return i;
                }
            }

            for (int i = Math.Min(requestedLod - 1, lods.Count - 1); i >= 0; i--)
            {
                if (HasRenderers(lods[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static List<LOD> ExtractLodLevels(LODGroup lodGroup)
        {
            return ExtractLodLevelsForFiltering(lodGroup);
        }

        private static List<LOD> ExtractLodLevelsForFiltering(LODGroup lodGroup)
        {
            if (lodGroup == null)
            {
                return new List<LOD>();
            }

            if (lodGroup.m_LODs is { Length: > 0 })
            {
                var list = new List<LOD>(lodGroup.m_LODs.Length);
                for (int i = 0; i < lodGroup.m_LODs.Length; i++)
                {
                    if (lodGroup.m_LODs[i] != null)
                    {
                        list.Add(lodGroup.m_LODs[i]);
                    }
                }
                if (list.Count > 0)
                {
                    return list;
                }
            }

            if (lodGroup.serializedType?.m_Type?.m_Nodes is { Count: > 0 })
            {
                try
                {
                    var dict = lodGroup.ToType(lodGroup.serializedType.m_Type);
                    if (dict != null && dict.Contains("m_LODs") && dict["m_LODs"] is System.Collections.IEnumerable lodItems && dict["m_LODs"] is not string)
                    {
                        var result = new List<LOD>();
                        foreach (var lodItem in lodItems)
                        {
                            if (lodItem is not System.Collections.IDictionary lodDict)
                            {
                                continue;
                            }

                            var renderers = new List<PPtr<Renderer>>();
                            if (lodDict.Contains("renderers") && lodDict["renderers"] is System.Collections.IEnumerable rendItems && lodDict["renderers"] is not string)
                            {
                                foreach (var rendItem in rendItems)
                                {
                                    if (rendItem is not System.Collections.IDictionary rendDict)
                                    {
                                        continue;
                                    }

                                    var pptrDict = rendDict.Contains("renderer") && rendDict["renderer"] is System.Collections.IDictionary inner
                                        ? inner
                                        : rendDict;
                                    if (pptrDict.Contains("m_FileID") && pptrDict.Contains("m_PathID")
                                        && pptrDict["m_FileID"] != null && pptrDict["m_PathID"] != null)
                                    {
                                        int fId = Convert.ToInt32(pptrDict["m_FileID"], System.Globalization.CultureInfo.InvariantCulture);
                                        long pId = Convert.ToInt64(pptrDict["m_PathID"], System.Globalization.CultureInfo.InvariantCulture);
                                        if (pId != 0)
                                        {
                                            renderers.Add(new PPtr<Renderer>(fId, pId, lodGroup.assetsFile));
                                        }
                                    }
                                }
                            }

                            float screenRel = lodDict.Contains("screenRelativeHeight") && lodDict["screenRelativeHeight"] != null
                                ? Convert.ToSingle(lodDict["screenRelativeHeight"], System.Globalization.CultureInfo.InvariantCulture)
                                : 0f;
                            float fadeWidth = lodDict.Contains("fadeTransitionWidth") && lodDict["fadeTransitionWidth"] != null
                                ? Convert.ToSingle(lodDict["fadeTransitionWidth"], System.Globalization.CultureInfo.InvariantCulture)
                                : 0f;

                            result.Add(new LOD
                            {
                                screenRelativeHeight = screenRel,
                                fadeTransitionWidth = fadeWidth,
                                renderers = renderers.ToArray()
                            });
                        }
                        return result;
                    }
                }
                catch
                {
                }
            }

            return new List<LOD>();
        }

        private bool IsExcludedByLodGroup(Renderer renderer)
        {
            if (_excludedLodRenderers.Count == 0 && _excludedLodRendererIds.Count == 0)
            {
                return false;
            }

            if (_excludedLodRenderers.Contains(renderer))
            {
                return true;
            }

            var key = BuildRendererKey(renderer);
            return key.HasValue && _excludedLodRendererIds.Contains(key.Value);
        }

        private static (string FileName, long PathId)? BuildRendererKey(Renderer renderer)
        {
            if (renderer.m_PathID == 0)
            {
                return null;
            }

            return (renderer.assetsFile?.fileName ?? string.Empty, renderer.m_PathID);
        }

        private static string ResolvePPtrFileName(SerializedFile? sourceFile, int fileId)
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
                if (!string.IsNullOrEmpty(ext?.fileName))
                {
                    return ext.fileName;
                }
                if (!string.IsNullOrEmpty(ext?.pathName))
                {
                    var normalized = ext.pathName.Replace('\\', '/').TrimEnd('/');
                    var slash = normalized.LastIndexOf('/');
                    return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
                }
            }

            return string.Empty;
        }

        private static bool TryResolveComponent(GameObject ownerGo, PPtr<Component> compPtr, [NotNullWhen(true)] out Component? component)
        {
            component = null;
            if (compPtr.TryGet(out component) && component != null)
            {
                return true;
            }

            foreach (var sf in SnapshotAssetsFiles(ownerGo.assetsFile?.assetsManager))
            {
                if (sf.TryGetObject(compPtr.m_PathID, out var obj) && obj is Component foundComp)
                {
                    component = foundComp;
                    return true;
                }
            }

            return false;
        }

        private static Renderer? TryResolveRenderer(LODGroup lodGroup, PPtr<Renderer> rendPtr)
        {
            if (rendPtr.TryGet(out var renderer) && renderer != null)
            {
                return renderer;
            }

            foreach (var sf in SnapshotAssetsFiles(lodGroup.assetsFile?.assetsManager))
            {
                if (sf.TryGetObject(rendPtr.m_PathID, out var obj) && obj is Renderer foundRend)
                {
                    return foundRend;
                }
            }

            return null;
        }

        private void ConvertTerrainFromGameObject(
            GameObject gameObject,
            ImportedFrame? targetFrame,
            bool skipDisabledRenderers,
            SceneConversionOptions sceneOptions)
        {
            if (gameObject.m_Components == null)
            {
                return;
            }

            var terrainOptions = new TerrainConversionOptions
            {
                MaxGridResolution = sceneOptions.MaxTerrainGridResolution,
                MaxBakedTextureSize = sceneOptions.MaxTerrainBakedTextureSize,
                ImageFormat = imageFormat,
                CompositeSplatmap = true
            };

            for (int i = 0; i < gameObject.m_Components.Length; i++)
            {
                var compPtr = gameObject.m_Components[i];
                if (compPtr == null || compPtr.IsNull || !TryResolveComponent(gameObject, compPtr, out var comp))
                {
                    continue;
                }

                if (comp is not Terrain terrain)
                {
                    continue;
                }

                if ((skipDisabledRenderers && !terrain.Enabled) || !terrain.m_DrawHeightmap)
                {
                    continue;
                }

                TerrainData? terrainData = null;
                if (terrain.m_TerrainData != null && !terrain.m_TerrainData.IsNull)
                {
                    if (!terrain.m_TerrainData.TryGet(out terrainData) || terrainData == null)
                    {
                        foreach (var sf in SnapshotAssetsFiles(terrain.assetsFile?.assetsManager))
                        {
                            if (sf.TryGetObject(terrain.m_TerrainData.m_PathID, out var tdObj) && tdObj is TerrainData foundTd)
                            {
                                terrainData = foundTd;
                                break;
                            }
                        }
                    }
                }

                if (terrainData == null || !terrainData.HasValidHeightmap)
                {
                    continue;
                }

                var baseName = !string.IsNullOrWhiteSpace(gameObject.m_Name)
                    ? gameObject.m_Name
                    : (!string.IsNullOrWhiteSpace(terrainData.m_Name) ? terrainData.m_Name : $"Terrain_{terrain.m_PathID}");

                var converted = TerrainMeshConverter.ConvertToImported(
                    terrain,
                    terrainData,
                    targetFrame,
                    baseName,
                    terrainOptions);
                if (converted == null)
                {
                    continue;
                }

                // Ensure unique material & texture names across multiple terrains in a scene.
                var matName = converted.Material.Name;
                if (ImportedHelpers.FindMaterial(matName, MaterialList) != null)
                {
                    matName = $"{matName}_{terrain.m_PathID}";
                    converted.Material.Name = matName;
                    for (int s = 0; s < converted.Mesh.SubmeshList.Count; s++)
                    {
                        converted.Mesh.SubmeshList[s].Material = matName;
                    }
                }

                if (converted.Texture != null)
                {
                    var texName = converted.Texture.Name;
                    if (IsTextureNameAllocated(texName))
                    {
                        var ext = Path.GetExtension(texName);
                        var stem = Path.GetFileNameWithoutExtension(texName);
                        texName = $"{stem}_{terrain.m_PathID}{ext}";
                        converted.Texture.Name = texName;
                        for (int t = 0; t < converted.Material.Textures.Count; t++)
                        {
                            converted.Material.Textures[t].Name = texName;
                        }
                    }
                    TextureList.Add(converted.Texture);
                }

                MaterialList.Add(converted.Material);
                MeshList.Add(converted.Mesh);
                ExportedTerrainCount++;
            }
        }

        private void ExtractCamerasAndLightsFromGameObject(
            GameObject gameObject,
            ImportedFrame? targetFrame,
            bool skipDisabled)
        {
            if (gameObject.m_Components == null)
            {
                return;
            }

            var framePath = targetFrame?.Path ?? gameObject.m_Name ?? string.Empty;
            var baseName = string.IsNullOrWhiteSpace(gameObject.m_Name)
                ? $"GameObject_{gameObject.m_PathID}"
                : gameObject.m_Name;

            for (int i = 0; i < gameObject.m_Components.Length; i++)
            {
                var compPtr = gameObject.m_Components[i];
                if (compPtr == null || compPtr.IsNull || !TryResolveComponent(gameObject, compPtr, out var comp))
                {
                    continue;
                }

                if (comp is Camera cam)
                {
                    if (skipDisabled && !cam.Enabled)
                    {
                        continue;
                    }

                    CameraList.Add(new ImportedCamera
                    {
                        Name = baseName,
                        Path = framePath,
                        TargetFrame = targetFrame,
                        IsOrthographic = cam.m_Orthographic,
                        FieldOfViewDegrees = cam.m_FieldOfView,
                        OrthographicSize = cam.m_OrthographicSize,
                        AspectRatio = 0f,
                        NearClipPlane = cam.m_NearClipPlane,
                        FarClipPlane = cam.m_FarClipPlane,
                        ClearFlags = cam.m_ClearFlags,
                        Depth = cam.m_Depth,
                        Enabled = cam.Enabled
                    });
                }
                else if (comp is Light light)
                {
                    if (skipDisabled && !light.Enabled)
                    {
                        continue;
                    }

                    var importedType = light.m_Type switch
                    {
                        LightType.Directional => ImportedLightType.Directional,
                        LightType.Spot => ImportedLightType.Spot,
                        LightType.Area or LightType.Disc => ImportedLightType.Area,
                        _ => ImportedLightType.Point
                    };

                    LightList.Add(new ImportedLight
                    {
                        Name = baseName,
                        Path = framePath,
                        TargetFrame = targetFrame,
                        Type = importedType,
                        Color = light.m_Color,
                        Intensity = light.m_Intensity,
                        Range = light.m_Range,
                        SpotAngleDegrees = light.m_SpotAngle,
                        InnerSpotAngleDegrees = light.m_InnerSpotAngle,
                        ShadowType = light.m_Shadows?.m_Type ?? 0,
                        Enabled = light.Enabled
                    });
                }
            }
        }

        private void AttachScriptSpawnedPrefabs(
            IReadOnlyList<SceneScriptSpawnAttachment> attachments,
            bool includeInactive,
            bool skipDisabledRenderers,
            SceneConversionOptions sceneOptions,
            CancellationToken cancellationToken)
        {
            int maxInstances = Math.Max(0, sceneOptions.MaxScriptSpawnInstances);
            if (maxInstances == 0)
            {
                return;
            }

            for (int i = 0; i < attachments.Count && AttachedScriptSpawnCount < maxInstances; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attachment = attachments[i];
                if (attachment?.PrefabRoot == null)
                {
                    continue;
                }

                var parentFrame = ResolveScriptSpawnAnchorFrame(attachment) ?? RootFrame;
                AttachSingleScriptSpawnedPrefab(
                    attachment,
                    parentFrame,
                    includeInactive,
                    skipDisabledRenderers,
                    sceneOptions,
                    cancellationToken);
                AttachedScriptSpawnCount++;
            }
        }

        private ImportedFrame? ResolveScriptSpawnAnchorFrame(SceneScriptSpawnAttachment attachment)
        {
            if (attachment.AnchorTransform != null && transformDictionary.TryGetValue(attachment.AnchorTransform, out var exactAnchor))
            {
                return exactAnchor;
            }

            if (attachment.AnchorTransformPathId != 0 && _frameByTransformPathId.TryGetValue(attachment.AnchorTransformPathId, out var anchorById))
            {
                return anchorById;
            }

            if (attachment.OwnerGameObject?.m_Transform != null
                && transformDictionary.TryGetValue(attachment.OwnerGameObject.m_Transform, out var ownerFrame))
            {
                return ownerFrame;
            }

            if (attachment.OwnerPathId != 0 && _frameByGameObjectPathId.TryGetValue(attachment.OwnerPathId, out var ownerById))
            {
                return ownerById;
            }

            return null;
        }

        private void AttachSingleScriptSpawnedPrefab(
            SceneScriptSpawnAttachment attachment,
            ImportedFrame parentFrame,
            bool includeInactive,
            bool skipDisabledRenderers,
            SceneConversionOptions sceneOptions,
            CancellationToken cancellationToken)
        {
            var prefabRoot = attachment.PrefabRoot;
            HydrateGameObjectHierarchy(prefabRoot);

            var rootTransform = prefabRoot.m_Transform;
            var localMap = new Dictionary<Transform, ImportedFrame>();
            var spawnMetadata = new ImportedScriptSpawnMetadata
            {
                ScriptClass = attachment.ScriptClass ?? string.Empty,
                FieldPath = attachment.FieldPath ?? string.Empty,
                SourceKind = attachment.SourceKind ?? string.Empty,
                Confidence = attachment.Confidence ?? string.Empty,
                OwnerPathId = attachment.OwnerPathId,
                AnchorTransformPathId = attachment.AnchorTransformPathId,
                TargetFileName = attachment.TargetFileName ?? prefabRoot.assetsFile?.fileName ?? string.Empty,
                TargetPathId = attachment.TargetPathId != 0 ? attachment.TargetPathId : prefabRoot.m_PathID,
                TargetGuid = attachment.TargetGuid ?? string.Empty
            };

            if (rootTransform == null)
            {
                var fallbackName = string.IsNullOrWhiteSpace(prefabRoot.m_Name)
                    ? $"[ScriptSpawned] Prefab_{prefabRoot.m_PathID}"
                    : $"[ScriptSpawned] {prefabRoot.m_Name}";
                var fallbackFrame = CreateFrame(
                    fallbackName,
                    attachment.LocalPositionOffset,
                    attachment.LocalRotationOffset,
                    attachment.LocalScaleMultiplier);
                fallbackFrame.Extras = new ImportedFrameExtras
                {
                    PathId = prefabRoot.m_PathID,
                    SourceFile = prefabRoot.assetsFile?.fileName ?? string.Empty,
                    Tag = prefabRoot.m_Tag,
                    Layer = prefabRoot.m_Layer,
                    IsActive = prefabRoot.m_IsActive,
                    IsScriptSpawned = true,
                    ScriptSpawn = spawnMetadata
                };
                parentFrame.AddChild(fallbackFrame);
                return;
            }

            ClonePrefabTransformSubtree(
                rootTransform,
                parentFrame,
                isPrefabRoot: true,
                attachment,
                spawnMetadata,
                localMap,
                includeInactive,
                cancellationToken);

            var localExcludedLods = new HashSet<Renderer>();
            if (sceneOptions.LodExportMode == SceneLodExportMode.Lod0Only)
            {
                CollectPrefabLodExclusions(rootTransform, localExcludedLods, includeInactive, cancellationToken);
            }

            var visitedPrefabRenderers = new HashSet<Transform>();
            ConvertPrefabComponentsRecursive(
                rootTransform,
                visitedPrefabRenderers,
                localMap,
                localExcludedLods,
                includeInactive,
                skipDisabledRenderers,
                sceneOptions,
                cancellationToken);
        }

        private void ClonePrefabTransformSubtree(
            Transform trans,
            ImportedFrame parentFrame,
            bool isPrefabRoot,
            SceneScriptSpawnAttachment attachment,
            ImportedScriptSpawnMetadata spawnMetadata,
            Dictionary<Transform, ImportedFrame> localMap,
            bool includeInactive,
            CancellationToken cancellationToken)
        {
            if (trans == null || localMap.ContainsKey(trans))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            TryResolveTransformGameObject(trans, out var go);
            if (!isPrefabRoot && !includeInactive && go != null && !go.m_IsActive)
            {
                return;
            }

            var rawName = go != null && !string.IsNullOrWhiteSpace(go.m_Name)
                ? go.m_Name
                : $"GameObject_{trans.m_GameObject.m_PathID}";
            var frameName = isPrefabRoot && !attachment.PreservePrefabRootName ? $"[ScriptSpawned] {rawName}" : rawName;

            var pos = trans.m_LocalPosition;
            var rot = trans.m_LocalRotation;
            var scale = trans.m_LocalScale;
            if (isPrefabRoot)
            {
                pos = new Vector3(
                    pos.X + attachment.LocalPositionOffset.X,
                    pos.Y + attachment.LocalPositionOffset.Y,
                    pos.Z + attachment.LocalPositionOffset.Z);
                rot = MultiplyUnityQuaternions(attachment.LocalRotationOffset, rot);
                scale = new Vector3(
                    scale.X * attachment.LocalScaleMultiplier.X,
                    scale.Y * attachment.LocalScaleMultiplier.Y,
                    scale.Z * attachment.LocalScaleMultiplier.Z);
            }

            var frame = new ImportedFrame(trans.m_Children?.Length ?? 0)
            {
                Name = frameName
            };
            SetFrame(frame, pos, rot, scale);

            if (_populateFrameExtras || isPrefabRoot)
            {
                frame.Extras = new ImportedFrameExtras
                {
                    PathId = go?.m_PathID ?? trans.m_GameObject.m_PathID,
                    SourceFile = go?.assetsFile?.fileName ?? trans.assetsFile?.fileName ?? string.Empty,
                    Tag = go?.m_Tag ?? 0,
                    Layer = go?.m_Layer ?? 0,
                    IsActive = go?.m_IsActive ?? true,
                    IsScriptSpawned = true,
                    ScriptSpawn = isPrefabRoot ? spawnMetadata : null
                };
            }

            localMap[trans] = frame;
            parentFrame.AddChild(frame);

            if (trans.m_Children == null)
            {
                return;
            }

            for (int i = 0; i < trans.m_Children.Length; i++)
            {
                var childPtr = trans.m_Children[i];
                if (childPtr == null || childPtr.IsNull)
                {
                    continue;
                }

                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in SnapshotAssetsFiles(trans.assetsFile?.assetsManager))
                    {
                        if (sf.TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }

                if (child != null)
                {
                    ClonePrefabTransformSubtree(
                        child,
                        frame,
                        isPrefabRoot: false,
                        attachment,
                        spawnMetadata,
                        localMap,
                        includeInactive,
                        cancellationToken);
                }
            }
        }

        private static Quaternion MultiplyUnityQuaternions(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        private void CollectPrefabLodExclusions(
            Transform rootTransform,
            HashSet<Renderer> excludedRenderers,
            bool includeInactive,
            CancellationToken cancellationToken)
        {
            var visited = new HashSet<Transform>();
            var kept = new HashSet<Renderer>();
            var stack = new Stack<Transform>();
            stack.Push(rootTransform);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trans = stack.Pop();
                if (trans == null || !visited.Add(trans))
                {
                    continue;
                }

                if (TryResolveTransformGameObject(trans, out var go) && go != null)
                {
                    if (!ReferenceEquals(trans, rootTransform) && !includeInactive && !go.m_IsActive)
                    {
                        continue;
                    }

                    if (go.m_Components != null)
                    {
                        for (int i = 0; i < go.m_Components.Length; i++)
                        {
                            var compPtr = go.m_Components[i];
                            if (compPtr == null || compPtr.IsNull
                                || !TryResolveComponent(go, compPtr, out var comp)
                                || comp is not LODGroup lodGroup
                                || !lodGroup.m_Enabled
                                || lodGroup.m_LODs == null
                                || lodGroup.m_LODs.Length == 0)
                            {
                                continue;
                            }

                            int selectedIdx = -1;
                            for (int l = 0; l < lodGroup.m_LODs.Length; l++)
                            {
                                if (lodGroup.m_LODs[l]?.renderers is { Length: > 0 })
                                {
                                    selectedIdx = l;
                                    break;
                                }
                            }

                            if (selectedIdx < 0)
                            {
                                continue;
                            }

                            for (int l = 0; l < lodGroup.m_LODs.Length; l++)
                            {
                                var lod = lodGroup.m_LODs[l];
                                if (lod?.renderers == null) continue;
                                for (int r = 0; r < lod.renderers.Length; r++)
                                {
                                    var rendPtr = lod.renderers[r];
                                    if (rendPtr == null || rendPtr.IsNull) continue;
                                    var rend = TryResolveRenderer(lodGroup, rendPtr);
                                    if (rend == null) continue;
                                    if (l == selectedIdx) kept.Add(rend);
                                    else excludedRenderers.Add(rend);
                                }
                            }
                        }
                    }
                }

                if (trans.m_Children != null)
                {
                    for (int i = 0; i < trans.m_Children.Length; i++)
                    {
                        var childPtr = trans.m_Children[i];
                        if (childPtr != null && !childPtr.IsNull && childPtr.TryGet(out var child) && child != null)
                        {
                            stack.Push(child);
                        }
                    }
                }
            }

            excludedRenderers.ExceptWith(kept);
        }

        private void ConvertPrefabComponentsRecursive(
            Transform trans,
            HashSet<Transform> visited,
            Dictionary<Transform, ImportedFrame> localMap,
            HashSet<Renderer> localExcludedLods,
            bool includeInactive,
            bool skipDisabledRenderers,
            SceneConversionOptions sceneOptions,
            CancellationToken cancellationToken)
        {
            if (trans == null || !visited.Add(trans))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!localMap.TryGetValue(trans, out var targetFrame))
            {
                return;
            }

            if (TryResolveTransformGameObject(trans, out var go) && go != null)
            {
                if (go.m_MeshRenderer != null
                    && (!skipDisabledRenderers || go.m_MeshRenderer.m_Enabled))
                {
                    if (localExcludedLods.Contains(go.m_MeshRenderer))
                    {
                        SkippedLodRendererCount++;
                    }
                    else
                    {
                        ConvertMeshRenderer(go.m_MeshRenderer, localMap);
                    }
                }

                if (go.m_SkinnedMeshRenderer != null
                    && (!skipDisabledRenderers || go.m_SkinnedMeshRenderer.m_Enabled))
                {
                    if (localExcludedLods.Contains(go.m_SkinnedMeshRenderer))
                    {
                        SkippedLodRendererCount++;
                    }
                    else
                    {
                        ConvertMeshRenderer(go.m_SkinnedMeshRenderer, localMap);
                    }
                }

                if (sceneOptions.IncludeCamerasAndLights)
                {
                    ExtractCamerasAndLightsFromGameObject(go, targetFrame, sceneOptions.SkipDisabledCamerasAndLights);
                }
            }

            if (trans.m_Children == null)
            {
                return;
            }

            for (int i = 0; i < trans.m_Children.Length; i++)
            {
                var childPtr = trans.m_Children[i];
                if (childPtr == null || childPtr.IsNull)
                {
                    continue;
                }

                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in SnapshotAssetsFiles(trans.assetsFile?.assetsManager))
                    {
                        if (sf.TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }

                if (child != null)
                {
                    ConvertPrefabComponentsRecursive(
                        child,
                        visited,
                        localMap,
                        localExcludedLods,
                        includeInactive,
                        skipDisabledRenderers,
                        sceneOptions,
                        cancellationToken);
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

        private static bool TryResolveTransformGameObject(Transform trans, out GameObject? gameObject)
        {
            gameObject = null;
            if (trans.m_GameObject == null || trans.m_GameObject.IsNull)
            {
                return false;
            }

            if (trans.m_GameObject.TryGet(out gameObject) && gameObject != null)
            {
                return true;
            }

            foreach (var sf in SnapshotAssetsFiles(trans.assetsFile?.assetsManager))
            {
                if (sf.TryGetObject(trans.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                {
                    gameObject = foundGo;
                    return true;
                }
            }

            return false;
        }

        private static string BuildStaticMeshGeometryKey(Mesh mesh)
        {
            var fileName = mesh.assetsFile?.fileName;
            if (!string.IsNullOrEmpty(fileName) && mesh.m_PathID != 0)
            {
                return $"{fileName}#{mesh.m_PathID}";
            }

            return $"mem#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mesh)}";
        }

        public ModelConverter(Animator m_Animator, ImageFormat imageFormat, AnimationClip[]? animationList = null, Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null)
        {
            this.imageFormat = imageFormat;
            this.textureResolver = textureResolver ?? AmbientTextureResolver.Value;
            InitWithAnimator(m_Animator);
            if (avatar == null && m_Animator.m_GameObject.TryGet(out var go))
            {
                avatar = FindAvatar(go);
            }
            if (animationList == null)
            {
                CollectAnimationClip(m_Animator);
            }
            else
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
            FlushPendingTextures();
        }

        private void InitWithAnimator(Animator m_Animator)
        {
            if (!m_Animator.m_Avatar.TryGet(out var m_Avatar))
            {
                if (m_Animator.m_Avatar != null && !m_Animator.m_Avatar.IsNull)
                {
                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(m_Animator.m_Avatar.m_PathID, out var obj) && obj is Avatar foundAvatar)
                        {
                            m_Avatar = foundAvatar;
                            Logger.Info($"Found Avatar '{foundAvatar.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                            break;
                        }
                    }
                }
            }
            if (m_Avatar != null)
                avatar = m_Avatar;

            if (!m_Animator.m_GameObject.TryGet(out var m_GameObject))
            {
                if (m_Animator.m_GameObject != null && !m_Animator.m_GameObject.IsNull)
                {
                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(m_Animator.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                        {
                            m_GameObject = foundGo;
                            Logger.Info($"Found GameObject '{foundGo.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                            break;
                        }
                    }
                }
            }
            if (m_GameObject != null)
            {
                InitWithGameObject(m_GameObject, m_Animator.m_HasTransformHierarchy);
            }
            else
            {
                Logger.Error($"GameObject for Animator (PathID: {m_Animator.m_PathID}) could not be resolved.");
            }
        }

        private void InitWithGameObject(GameObject m_GameObject, bool hasTransformHierarchy = true)
        {
            var m_Transform = m_GameObject.m_Transform;
            if (!hasTransformHierarchy)
            {
                ConvertTransforms(m_Transform, null);
                DeoptimizeTransformHierarchy();
            }
            else
            {
                var frameList = new List<ImportedFrame>();
                var visitedFathers = new HashSet<Transform>();
                var tempTransform = m_Transform;
                if (tempTransform != null)
                {
                    visitedFathers.Add(tempTransform);
                }
                while (tempTransform != null)
                {
                    var fatherPtr = tempTransform.m_Father;
                    if (fatherPtr == null || fatherPtr.IsNull)
                        break;
                    Transform? m_Father = null;
                    if (!fatherPtr.TryGet(out m_Father))
                    {
                        foreach (var sf in tempTransform.assetsFile.assetsManager.assetsFileList)
                        {
                            if (sf.ObjectsDic.TryGetValue(fatherPtr.m_PathID, out var obj) && obj is Transform foundFather)
                            {
                                m_Father = foundFather;
                                break;
                            }
                        }
                    }
                    if (m_Father == null || !visitedFathers.Add(m_Father))
                        break;
                    frameList.Add(ConvertTransform(m_Father));
                    tempTransform = m_Father;
                }
                if (frameList.Count > 0)
                {
                    SetRootFrame(frameList[frameList.Count - 1]);
                    for (var i = frameList.Count - 2; i >= 0; i--)
                    {
                        var frame = frameList[i];
                        var parent = frameList[i + 1];
                        parent.AddChild(frame);
                    }
                    ConvertTransforms(m_Transform, frameList[0]);
                }
                else
                {
                    ConvertTransforms(m_Transform, null);
                }

                CreateBonePathHash(m_Transform);
            }

            if (m_Transform != null)
            {
                animatorRootPath = GetTransformPath(m_Transform) ?? RootFrame?.Path;
            }
            ConvertMeshRenderer(m_Transform);
        }

        private void ConvertMeshRenderer(Transform? m_Transform)
        {
            ConvertMeshRenderer(m_Transform, new HashSet<Transform>());
        }

        private void ConvertMeshRenderer(Transform? m_Transform, HashSet<Transform> visited)
        {
            if (m_Transform == null || !visited.Add(m_Transform))
                return;

            if (!m_Transform.m_GameObject.TryGet(out var m_GameObject))
            {
                foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                {
                    if (sf.ObjectsDic.TryGetValue(m_Transform.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        m_GameObject = foundGo;
                        break;
                    }
                }
            }

            if (m_GameObject != null)
            {
                if (m_GameObject.m_MeshRenderer != null)
                {
                    ConvertMeshRenderer(m_GameObject.m_MeshRenderer);
                }

                if (m_GameObject.m_SkinnedMeshRenderer != null)
                {
                    ConvertMeshRenderer(m_GameObject.m_SkinnedMeshRenderer);
                }

                if (m_GameObject.m_Animation != null)
                {
                    foreach (var animation in m_GameObject.m_Animation.m_Animations)
                    {
                        var animationPtr = animation;
                        if (!animationPtr.TryGet(out var animationClip))
                        {
                            foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                            {
                                if (sf.ObjectsDic.TryGetValue(animationPtr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                {
                                    animationClip = foundClip;
                                    break;
                                }
                            }
                        }
                        if (animationClip != null)
                        {
                            var transformPath = GetTransformPath(m_Transform);
                            if (transformPath != null && !boundAnimationPathDic.ContainsKey(animationClip))
                            {
                                boundAnimationPathDic.Add(animationClip, transformPath);
                            }
                            animationClipHashSet.Add(animationClip);
                        }
                    }
                }
            }

            foreach (var pptr in m_Transform.m_Children)
            {
                var childPtr = pptr;
                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }
                if (child != null)
                    ConvertMeshRenderer(child, visited);
            }
        }

        private void CollectAnimationClip(Animator m_Animator)
        {
            RuntimeAnimatorController? m_Controller = null;
            if (!m_Animator.m_Controller.TryGet(out m_Controller))
            {
                if (m_Animator.m_Controller != null && !m_Animator.m_Controller.IsNull)
                {
                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(m_Animator.m_Controller.m_PathID, out var obj) && obj is RuntimeAnimatorController foundController)
                        {
                            m_Controller = foundController;
                            Logger.Info($"Found Controller '{foundController.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                            break;
                        }
                    }
                }
            }

            if (m_Controller != null)
            {
                switch (m_Controller)
                {
                    case AnimatorOverrideController m_AnimatorOverrideController:
                        {
                            foreach (var clipOverride in m_AnimatorOverrideController.m_Clips)
                            {
                                AnimationClip? m_OverrideClip = null;
                                if (clipOverride.m_OverrideClip != null && !clipOverride.m_OverrideClip.TryGet(out m_OverrideClip) && !clipOverride.m_OverrideClip.IsNull)
                                {
                                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                    {
                                        if (sf.ObjectsDic.TryGetValue(clipOverride.m_OverrideClip.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                        {
                                            m_OverrideClip = foundClip;
                                            break;
                                        }
                                    }
                                }

                                if (m_OverrideClip != null)
                                {
                                    animationClipHashSet.Add(m_OverrideClip);
                                }
                                else
                                {
                                    AnimationClip? m_OriginalClip = null;
                                    if (clipOverride.m_OriginalClip != null && !clipOverride.m_OriginalClip.TryGet(out m_OriginalClip) && !clipOverride.m_OriginalClip.IsNull)
                                    {
                                        foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                        {
                                            if (sf.ObjectsDic.TryGetValue(clipOverride.m_OriginalClip.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                            {
                                                m_OriginalClip = foundClip;
                                                break;
                                            }
                                        }
                                    }
                                    if (m_OriginalClip != null)
                                    {
                                        animationClipHashSet.Add(m_OriginalClip);
                                    }
                                }
                            }

                            AnimatorController? m_AnimatorController = null;
                            if (m_AnimatorOverrideController.m_Controller != null && !m_AnimatorOverrideController.m_Controller.TryGet<AnimatorController>(out m_AnimatorController) && !m_AnimatorOverrideController.m_Controller.IsNull)
                            {
                                foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                {
                                    if (sf.ObjectsDic.TryGetValue(m_AnimatorOverrideController.m_Controller.m_PathID, out var obj) && obj is AnimatorController foundController)
                                    {
                                        m_AnimatorController = foundController;
                                        break;
                                    }
                                }
                            }

                            if (m_AnimatorController != null)
                            {
                                foreach (var pptr in m_AnimatorController.m_AnimationClips)
                                {
                                    AnimationClip? m_AnimationClip = null;
                                    if (pptr != null && !pptr.TryGet(out m_AnimationClip) && !pptr.IsNull)
                                    {
                                        foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                        {
                                            if (sf.ObjectsDic.TryGetValue(pptr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                            {
                                                m_AnimationClip = foundClip;
                                                break;
                                            }
                                        }
                                    }
                                    if (m_AnimationClip != null)
                                    {
                                        animationClipHashSet.Add(m_AnimationClip);
                                    }
                                }
                            }
                            break;
                        }

                    case AnimatorController m_AnimatorController:
                        {
                            foreach (var pptr in m_AnimatorController.m_AnimationClips)
                            {
                                AnimationClip? m_AnimationClip = null;
                                if (pptr != null && !pptr.TryGet(out m_AnimationClip) && !pptr.IsNull)
                                {
                                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                    {
                                        if (sf.ObjectsDic.TryGetValue(pptr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                        {
                                            m_AnimationClip = foundClip;
                                            break;
                                        }
                                    }
                                }
                                if (m_AnimationClip != null)
                                {
                                    animationClipHashSet.Add(m_AnimationClip);
                                }
                            }
                            break;
                        }
                }
            }
        }

        private ImportedFrame ConvertTransform(Transform trans)
        {
            if (transformDictionary.TryGetValue(trans, out var existingFrame))
            {
                return existingFrame;
            }

            var frame = new ImportedFrame(trans.m_Children.Length);
            transformDictionary[trans] = frame;
            if (!trans.m_GameObject.TryGet(out var m_GameObject))
            {
                foreach (var sf in trans.assetsFile.assetsManager.assetsFileList)
                {
                    if (sf.ObjectsDic.TryGetValue(trans.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        m_GameObject = foundGo;
                        break;
                    }
                }
            }
            frame.Name = m_GameObject != null ? m_GameObject.m_Name : $"GameObject_{trans.m_GameObject.m_PathID}";
            SetFrame(frame, trans.m_LocalPosition, trans.m_LocalRotation, trans.m_LocalScale);
            if (trans.m_PathID != 0 && !_frameByTransformPathId.ContainsKey(trans.m_PathID))
            {
                _frameByTransformPathId[trans.m_PathID] = frame;
            }
            if (m_GameObject != null)
            {
                if (m_GameObject.m_PathID != 0 && !_frameByGameObjectPathId.ContainsKey(m_GameObject.m_PathID))
                {
                    _frameByGameObjectPathId[m_GameObject.m_PathID] = frame;
                }

                if (_populateFrameExtras)
                {
                    frame.Extras = new ImportedFrameExtras
                    {
                        PathId = m_GameObject.m_PathID,
                        SourceFile = m_GameObject.assetsFile?.fileName ?? string.Empty,
                        Tag = m_GameObject.m_Tag,
                        Layer = m_GameObject.m_Layer,
                        IsActive = m_GameObject.m_IsActive
                    };
                }
            }
            return frame;
        }

        private static ImportedFrame CreateFrame(string name, Vector3 t, Quaternion q, Vector3 s)
        {
            var frame = new ImportedFrame();
            frame.Name = name;
            SetFrame(frame, t, q, s);
            return frame;
        }

        private static void SetFrame(ImportedFrame frame, Vector3 t, Quaternion q, Vector3 s)
        {
            frame.LocalPosition = new Vector3(-t.X, t.Y, t.Z);
            frame.LocalRotation = QuaternionToEuler(new Quaternion(q.X, -q.Y, -q.Z, q.W));
            frame.LocalScale = s;
        }

        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            float sqw = q.W * q.W;
            float sqx = q.X * q.X;
            float sqy = q.Y * q.Y;
            float sqz = q.Z * q.Z;
            float unit = sqx + sqy + sqz + sqw;
            if (unit <= float.Epsilon || float.IsNaN(unit) || float.IsInfinity(unit))
            {
                return Vector3.Zero;
            }
            float invNorm = 1f / (float)Math.Sqrt(unit);
            float qx = q.X * invNorm;
            float qy = q.Y * invNorm;
            float qz = q.Z * invNorm;
            float qw = q.W * invNorm;

            // Compute matrix elements for Euler XYZ rotation (Rx * Ry * Rz)
            // matching FbxSharpieExporter.BuildLocalMatrix and FBX default eEulerXYZ
            float m11 = 1f - 2f * (qy * qy + qz * qz);
            float m12 = 2f * (qx * qy + qz * qw);
            float m13 = 2f * (qx * qz - qy * qw);
            float m23 = 2f * (qy * qz + qx * qw);
            float m33 = 1f - 2f * (qx * qx + qy * qy);

            float sinY = -m13;
            sinY = Math.Max(-1f, Math.Min(1f, sinY));
            double cosY = Math.Sqrt((double)m11 * m11 + (double)m12 * m12);
            float rotY = (float)Math.Atan2(sinY, cosY);

            float rotX, rotZ;
            if (Math.Abs(sinY) < 0.99999f)
            {
                rotX = (float)Math.Atan2(m23, m33);
                rotZ = (float)Math.Atan2(m12, m11);
            }
            else
            {
                rotY = sinY >= 0f ? (float)(Math.PI * 0.5) : -(float)(Math.PI * 0.5);
                float m32 = 2f * (qy * qz - qx * qw);
                float m22 = 1f - 2f * (qx * qx + qz * qz);
                rotX = (float)Math.Atan2(-m32, m22);
                rotZ = 0f;
            }

            const float rad2deg = 180f / (float)Math.PI;
            return new Vector3(rotX * rad2deg, rotY * rad2deg, rotZ * rad2deg);
        }

        private void ConvertTransforms(Transform? trans, ImportedFrame? parent)
        {
            if (trans == null || transformDictionary.ContainsKey(trans))
            {
                return;
            }

            var frame = ConvertTransform(trans);
            if (parent == null)
            {
                SetRootFrame(frame);
            }
            else
            {
                parent.AddChild(frame);
            }
            foreach (var pptr in trans.m_Children)
            {
                var childPtr = pptr;
                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in trans.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }
                if (child != null)
                    ConvertTransforms(child, frame);
            }
        }

        private void ConvertMeshRenderer(Renderer meshR, Dictionary<Transform, ImportedFrame>? frameMapOverride = null)
        {
            var mesh = GetMesh(meshR) ?? TryResolveMeshWithResolver(meshR);
            if (mesh == null)
                return;
            mesh.EnsureProcessed();
            if (skipEmptyMeshes && (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0))
            {
                Logger.Info($"Skipping empty 0-vertex mesh '{mesh.m_Name}' (PathID: {mesh.m_PathID}).");
                return;
            }
            var iMesh = new ImportedMesh();
            if (!meshR.m_GameObject.TryGet(out var m_GameObject2))
            {
                foreach (var sf in SnapshotAssetsFiles(meshR.assetsFile?.assetsManager))
                {
                    if (sf.TryGetObject(meshR.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        m_GameObject2 = foundGo;
                        break;
                    }
                }
            }
            var activeFrameMap = frameMapOverride ?? transformDictionary;
            ImportedFrame? resolvedTargetFrame = null;
            if (m_GameObject2?.m_Transform != null && activeFrameMap.TryGetValue(m_GameObject2.m_Transform, out var exactTargetFrame))
            {
                resolvedTargetFrame = exactTargetFrame;
                iMesh.TargetFrame = exactTargetFrame;
                iMesh.Path = exactTargetFrame.Path;
            }
            else
            {
                iMesh.Path = (m_GameObject2 != null ? GetTransformPath(m_GameObject2.m_Transform) : null) ?? "";
            }
            if (!string.IsNullOrWhiteSpace(mesh.m_Name))
            {
                iMesh.SourceMeshName = mesh.m_Name;
            }
            iMesh.SubmeshList = new List<ImportedSubmesh>();
            var subHashSet = new HashSet<int>();
            var combine = false;
            int firstSubMesh = 0;
            if (meshR.m_StaticBatchInfo?.subMeshCount > 0)
            {
                firstSubMesh = meshR.m_StaticBatchInfo.firstSubMesh;
                var finalSubMesh = meshR.m_StaticBatchInfo.firstSubMesh + meshR.m_StaticBatchInfo.subMeshCount;
                for (int i = meshR.m_StaticBatchInfo.firstSubMesh; i < finalSubMesh; i++)
                {
                    subHashSet.Add(i);
                }
                combine = true;
            }
            else if (meshR.m_SubsetIndices?.Length > 0)
            {
                firstSubMesh = (int)meshR.m_SubsetIndices.Min(x => x);
                foreach (var index in meshR.m_SubsetIndices)
                {
                    subHashSet.Add((int)index);
                }
                combine = true;
            }

            // Fast instancing path for repeated non-batched static MeshRenderers:
            // reuse already-converted vertex & face arrays so scenes with hundreds of repeated
            // props avoid rebuilding identical geometry and share buffers cleanly in glTF 2.0.
            bool canUseStaticGeometryCache = !combine && meshR is not SkinnedMeshRenderer;
            if (canUseStaticGeometryCache && _staticMeshGeometryCache.TryGetValue(mesh, out var cachedStatic))
            {
                iMesh.GeometryKey = cachedStatic.GeometryKey;
                iMesh.hasNormal = cachedStatic.HasNormal;
                iMesh.hasUV = cachedStatic.HasUV;
                iMesh.hasTangent = cachedStatic.HasTangent;
                iMesh.hasColor = cachedStatic.HasColor;
                iMesh.VertexList = cachedStatic.VertexList;

                var cachedResolvedUnityMats = _hideAuxiliaryAndShadowOnlyRenderers ? new List<Material?>(cachedStatic.Submeshes.Count) : null;
                var cachedConvertedMats = _hideAuxiliaryAndShadowOnlyRenderers ? new List<ImportedMaterial>(cachedStatic.Submeshes.Count) : null;

                for (int i = 0; i < cachedStatic.Submeshes.Count; i++)
                {
                    Material? mat = ResolveRendererSubmeshMaterial(meshR, mesh, i, firstSubMesh);
                    ImportedMaterial iMat = ConvertMaterial(mat);
                    cachedResolvedUnityMats?.Add(mat);
                    cachedConvertedMats?.Add(iMat);
                    iMesh.SubmeshList.Add(new ImportedSubmesh
                    {
                        Material = iMat.Name,
                        BaseVertex = cachedStatic.Submeshes[i].BaseVertex,
                        FaceList = cachedStatic.Submeshes[i].FaceList
                    });
                }

                if (_hideAuxiliaryAndShadowOnlyRenderers && resolvedTargetFrame != null)
                {
                    EvaluateAndApplySceneRendererVisibility(meshR, m_GameObject2, resolvedTargetFrame, cachedResolvedUnityMats!, cachedConvertedMats!);
                }

                MeshList.Add(iMesh);
                return;
            }

            iMesh.hasNormal = mesh.m_Normals?.Length > 0;
            iMesh.hasUV = new bool[8];
            for (int uv = 0; uv < 8; uv++)
            {
                iMesh.hasUV[uv] = mesh.GetUV(uv)?.Length > 0;
            }
            iMesh.hasTangent = mesh.m_Tangents != null && mesh.m_Tangents.Length == mesh.m_VertexCount * 4;
            iMesh.hasColor = mesh.m_Colors?.Length > 0;

            var resolvedUnityMaterials = _hideAuxiliaryAndShadowOnlyRenderers ? new List<Material?>(mesh.m_SubMeshes.Length) : null;
            var convertedMaterials = _hideAuxiliaryAndShadowOnlyRenderers ? new List<ImportedMaterial>(mesh.m_SubMeshes.Length) : null;

            int firstFace = 0;
            for (int i = 0; i < mesh.m_SubMeshes.Length; i++)
            {
                int numFaces = (int)mesh.m_SubMeshes[i].indexCount / 3;
                if (subHashSet.Count > 0 && !subHashSet.Contains(i))
                {
                    firstFace += numFaces;
                    continue;
                }
                var submesh = mesh.m_SubMeshes[i];
                var iSubmesh = new ImportedSubmesh();
                Material? mat = ResolveRendererSubmeshMaterial(meshR, mesh, i, firstSubMesh);
                ImportedMaterial iMat = ConvertMaterial(mat);
                resolvedUnityMaterials?.Add(mat);
                convertedMaterials?.Add(iMat);
                iSubmesh.Material = iMat.Name;
                iSubmesh.BaseVertex = checked((int)(mesh.m_SubMeshes[i].firstVertex + mesh.m_SubMeshes[i].baseVertex));

                //Face
                iSubmesh.FaceList = new List<ImportedFace>(numFaces);
                var end = firstFace + numFaces;
                for (int f = firstFace; f < end; f++)
                {
                    if (f * 3 + 2 >= mesh.m_Indices.Count)
                    {
                        break;
                    }
                    var face = new ImportedFace();
                    face.VertexIndices = new int[3];
                    face.VertexIndices[0] = (int)(mesh.m_Indices[f * 3 + 2] - submesh.firstVertex);
                    face.VertexIndices[1] = (int)(mesh.m_Indices[f * 3 + 1] - submesh.firstVertex);
                    face.VertexIndices[2] = (int)(mesh.m_Indices[f * 3] - submesh.firstVertex);
                    iSubmesh.FaceList.Add(face);
                }
                firstFace = end;

                iMesh.SubmeshList.Add(iSubmesh);
            }

            if (_hideAuxiliaryAndShadowOnlyRenderers && resolvedTargetFrame != null)
            {
                EvaluateAndApplySceneRendererVisibility(meshR, m_GameObject2, resolvedTargetFrame, resolvedUnityMaterials!, convertedMaterials!);
            }

            // Shared vertex list
            int vertexCount = (int)mesh.m_VertexCount;
            var uvArrays = new float[8][];
            var uvStrides = new int[8];
            for (int uv = 0; uv < 8; uv++)
            {
                if (iMesh.hasUV[uv])
                {
                    var uvData = mesh.GetUV(uv);
                    if (uvData != null && vertexCount > 0 && uvData.Length >= vertexCount)
                    {
                        uvArrays[uv] = uvData;
                        uvStrides[uv] = uvData.Length / vertexCount;
                    }
                }
            }

            iMesh.VertexList = new List<ImportedVertex>(vertexCount);
            for (var j = 0; j < vertexCount; j++)
            {
                if (mesh.m_Vertices == null)
                {
                    break;
                }
                var iVertex = new ImportedVertex();
                //Vertices
                int posStride = mesh.m_Vertices.Length == vertexCount * 4 ? 4 : 3;
                iVertex.Vertex = new Vector3(-mesh.m_Vertices[j * posStride], mesh.m_Vertices[j * posStride + 1], mesh.m_Vertices[j * posStride + 2]);
                //Normals
                if (iMesh.hasNormal && mesh.m_Normals != null)
                {
                    int normStride = mesh.m_Normals.Length == vertexCount * 4 ? 4 : 3;
                    iVertex.Normal = new Vector3(-mesh.m_Normals[j * normStride], mesh.m_Normals[j * normStride + 1], mesh.m_Normals[j * normStride + 2]);
                }
                //UV
                iVertex.UV = new float[8][];
                for (int uv = 0; uv < 8; uv++)
                {
                    int uvStride = uvStrides[uv];
                    if (uvStride > 0)
                    {
                        var m_UV = uvArrays[uv];
                        int uvOffset = j * uvStride;
                        if (uvStride == 1)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], 0f };
                        }
                        else if (uvOffset + 1 < m_UV.Length)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], m_UV[uvOffset + 1] };
                        }
                    }
                }
                //Tangent
                if (iMesh.hasTangent && mesh.m_Tangents != null)
                {
                    iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], -mesh.m_Tangents[j * 4 + 3]);
                }
                //Colors
                if (iMesh.hasColor && mesh.m_Colors != null)
                {
                    if (mesh.m_Colors.Length == vertexCount * 3)
                    {
                        iVertex.Color = new Color(mesh.m_Colors[j * 3], mesh.m_Colors[j * 3 + 1], mesh.m_Colors[j * 3 + 2], 1.0f);
                    }
                    else
                    {
                        iVertex.Color = new Color(mesh.m_Colors[j * 4], mesh.m_Colors[j * 4 + 1], mesh.m_Colors[j * 4 + 2], mesh.m_Colors[j * 4 + 3]);
                    }
                }
                //BoneInfluence
                if (mesh.m_Skin != null && j < mesh.m_Skin.Length && mesh.m_Skin[j]?.boneIndex != null && mesh.m_Skin[j]?.weight != null)
                {
                    var inf = mesh.m_Skin[j];
                    int rawSlots = Math.Min(inf.boneIndex.Length, inf.weight.Length);
                    int slotCount = Math.Max(4, rawSlots);
                    iVertex.BoneIndices = new int[slotCount];
                    iVertex.Weights = new float[slotCount];
                    for (var k = 0; k < rawSlots; k++)
                    {
                        iVertex.BoneIndices[k] = inf.boneIndex[k];
                        iVertex.Weights[k] = inf.weight[k];
                    }
                }
                iMesh.VertexList.Add(iVertex);
            }

            if (meshR is SkinnedMeshRenderer sMesh)
            {
                EnsureBoneTransformsInHierarchy(sMesh);
                //Bone
                /*
                 * 0 - None
                 * 1 - m_Bones
                 * 2 - m_BoneNameHashes
                 */
                var boneType = 0;
                if (sMesh.m_Bones.Length > 0 && mesh.m_BindPose != null)
                {
                    if (sMesh.m_Bones.Length == mesh.m_BindPose.Length)
                    {
                        var verifiedBoneCount = sMesh.m_Bones.Count(x => TryGetBoneTransform(x, meshR, out _));
                        if (verifiedBoneCount > 0)
                        {
                            boneType = 1;
                        }
                        if (verifiedBoneCount != sMesh.m_Bones.Length)
                        {
                            //尝试使用m_BoneNameHashes 4.3 and up
                            if (mesh.m_BindPose.Length > 0 && (mesh.m_BindPose.Length == mesh.m_BoneNameHashes?.Length))
                            {
                                //有效bone数量是否大于SkinnedMeshRenderer
                                var verifiedBoneCount2 = mesh.m_BoneNameHashes.Count(x => TryResolveBonePathFromHash(x, out _));
                                if (verifiedBoneCount2 > verifiedBoneCount)
                                {
                                    boneType = 2;
                                }
                            }
                        }
                    }
                }
                if (boneType == 0 && mesh.m_BindPose != null)
                {
                    //尝试使用m_BoneNameHashes 4.3 and up
                    if (mesh.m_BindPose.Length > 0 && (mesh.m_BindPose.Length == mesh.m_BoneNameHashes?.Length))
                    {
                        var verifiedBoneCount = mesh.m_BoneNameHashes.Count(x => TryResolveBonePathFromHash(x, out _));
                        if (verifiedBoneCount > 0)
                        {
                            boneType = 2;
                        }
                    }
                }

                if (boneType == 1 && mesh.m_BindPose != null)
                {
                    var boneCount = sMesh.m_Bones.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        var bone = new ImportedBone();
                        if (TryGetBoneTransform(sMesh.m_Bones[i], meshR, out var m_Transform))
                        {
                            bone.Path = GetTransformPath(m_Transform);
                        }
                        var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                        bone.Matrix = convert * mesh.m_BindPose[i] * convert;
                        iMesh.BoneList.Add(bone);
                    }
                }
                else if (boneType == 2 && mesh.m_BoneNameHashes != null && mesh.m_BindPose != null)
                {
                    var boneCount = mesh.m_BindPose.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        var bone = new ImportedBone();
                        var boneHash = mesh.m_BoneNameHashes[i];
                        if (TryResolveBonePathFromHash(boneHash, out var resolvedBonePath))
                        {
                            bone.Path = resolvedBonePath;
                        }
                        var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                        bone.Matrix = convert * mesh.m_BindPose[i] * convert;
                        iMesh.BoneList.Add(bone);
                    }
                }

                //Morphs
                if (mesh.m_Shapes?.channels?.Length > 0)
                {
                    var morph = new ImportedMorph();
                    MorphList.Add(morph);
                    morph.Path = iMesh.Path;
                    morph.Channels = new List<ImportedMorphChannel>(mesh.m_Shapes.channels.Length);
                    for (int i = 0; i < mesh.m_Shapes.channels.Length; i++)
                    {
                        var channel = new ImportedMorphChannel();
                        morph.Channels.Add(channel);
                        var shapeChannel = mesh.m_Shapes.channels[i];

                        var cleanChannelName = NormalizeBlendShapeChannelName(shapeChannel.name);
                        RegisterMorphChannelHashes(shapeChannel, cleanChannelName);

                        channel.Name = cleanChannelName;
                        channel.KeyframeList = new List<ImportedMorphKeyframe>(shapeChannel.frameCount);
                        var frameEnd = shapeChannel.frameIndex + shapeChannel.frameCount;
                        for (int frameIdx = shapeChannel.frameIndex; frameIdx < frameEnd; frameIdx++)
                        {
                            if (frameIdx < 0 || frameIdx >= mesh.m_Shapes.fullWeights.Length || frameIdx >= mesh.m_Shapes.shapes.Length)
                                break;
                            var keyframe = new ImportedMorphKeyframe();
                            channel.KeyframeList.Add(keyframe);
                            keyframe.Weight = mesh.m_Shapes.fullWeights[frameIdx];
                            var shape = mesh.m_Shapes.shapes[frameIdx];
                            keyframe.hasNormals = shape.hasNormals;
                            keyframe.hasTangents = shape.hasTangents;
                            keyframe.VertexList = new List<ImportedMorphVertex>((int)shape.vertexCount);
                            var vertexEnd = shape.firstVertex + shape.vertexCount;
                            for (uint j = shape.firstVertex; j < vertexEnd; j++)
                            {
                                if (j >= (uint)mesh.m_Shapes.vertices.Length)
                                    break;
                                var morphVertex = mesh.m_Shapes.vertices[j];
                                if (morphVertex.index >= (uint)iMesh.VertexList.Count)
                                    continue;
                                var destVertex = new ImportedMorphVertex();
                                destVertex.Index = morphVertex.index;
                                var sourceVertex = iMesh.VertexList[(int)morphVertex.index];
                                destVertex.Vertex = new ImportedVertex();
                                var morphPos = morphVertex.vertex;
                                destVertex.Vertex.Vertex = sourceVertex.Vertex + new Vector3(-morphPos.X, morphPos.Y, morphPos.Z);
                                if (shape.hasNormals)
                                {
                                    var morphNormal = morphVertex.normal;
                                    destVertex.Vertex.Normal = new Vector3(-morphNormal.X, morphNormal.Y, morphNormal.Z);
                                }
                                if (shape.hasTangents)
                                {
                                    var morphTangent = morphVertex.tangent;
                                    destVertex.Vertex.Tangent = new Vector4(-morphTangent.X, morphTangent.Y, morphTangent.Z, 0);
                                }
                                keyframe.VertexList.Add(destVertex);
                            }
                        }
                    }
                }
            }

            // Un-bake world-space vertices for Unity static-batched meshes into the mesh frame's local space
            // so shared parent/sibling frame transforms are NEVER mutated.
            if (combine)
            {
                // Static-batched renderers reference only a small submesh range of the shared combined mesh;
                // keep only the vertices this renderer actually uses instead of duplicating the whole batch per node.
                if (meshR is not SkinnedMeshRenderer)
                {
                    CompactUnreferencedVertices(iMesh);
                }
                UnbakeStaticBatchedMeshToLocalSpace(iMesh);
            }
            else if (canUseStaticGeometryCache)
            {
                var geomKey = BuildStaticMeshGeometryKey(mesh);
                iMesh.GeometryKey = geomKey;
                var cachedSubmeshes = new List<(int BaseVertex, List<ImportedFace> FaceList)>(iMesh.SubmeshList.Count);
                for (int s = 0; s < iMesh.SubmeshList.Count; s++)
                {
                    cachedSubmeshes.Add((iMesh.SubmeshList[s].BaseVertex, iMesh.SubmeshList[s].FaceList));
                }
                _staticMeshGeometryCache[mesh] = new CachedConvertedStaticMesh
                {
                    GeometryKey = geomKey,
                    HasNormal = iMesh.hasNormal,
                    HasUV = iMesh.hasUV,
                    HasTangent = iMesh.hasTangent,
                    HasColor = iMesh.hasColor,
                    VertexList = iMesh.VertexList,
                    Submeshes = cachedSubmeshes
                };
            }

            MeshList.Add(iMesh);
        }

        private Mesh? TryResolveMeshWithResolver(Renderer meshR)
        {
            if (meshResolver == null)
            {
                return null;
            }

            try
            {
                return meshResolver(meshR);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning($"Mesh resolver failed for renderer PathID={meshR.m_PathID}: {ex.Message}");
                return null;
            }
        }

        private Material? ResolveRendererSubmeshMaterial(Renderer meshR, Mesh mesh, int submeshIndex, int firstSubMesh)
        {
            var slotIndex = submeshIndex - firstSubMesh;
            if (materialResolver != null && slotIndex >= 0)
            {
                try
                {
                    var resolved = materialResolver(meshR, mesh, slotIndex);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warning($"Material resolver failed for renderer PathID={meshR.m_PathID}, slot {slotIndex}: {ex.Message}");
                }
            }

            return ResolveRendererSubmeshMaterialInMemory(meshR, submeshIndex, firstSubMesh);
        }

        /// <summary>
        /// Removes vertices that no submesh face references and remaps face indices (BaseVertex becomes 0).
        /// Used for static-batched renderers whose submesh range only touches a slice of a shared combined mesh.
        /// Returns the number of vertices removed.
        /// </summary>
        public static int CompactUnreferencedVertices(ImportedMesh iMesh)
        {
            ArgumentNullException.ThrowIfNull(iMesh);
            var vertices = iMesh.VertexList;
            if (vertices == null || vertices.Count == 0 || iMesh.SubmeshList == null || iMesh.SubmeshList.Count == 0)
            {
                return 0;
            }

            var remap = new int[vertices.Count];
            Array.Fill(remap, -1);
            var compacted = new List<ImportedVertex>();
            foreach (var submesh in iMesh.SubmeshList)
            {
                if (submesh?.FaceList == null)
                {
                    continue;
                }

                foreach (var face in submesh.FaceList)
                {
                    var indices = face?.VertexIndices;
                    if (indices == null)
                    {
                        continue;
                    }

                    for (int k = 0; k < indices.Length; k++)
                    {
                        var absolute = indices[k] + submesh.BaseVertex;
                        if (absolute < 0 || absolute >= vertices.Count)
                        {
                            // Out-of-range indices cannot be remapped safely; keep the mesh untouched.
                            return 0;
                        }
                    }
                }
            }

            foreach (var submesh in iMesh.SubmeshList)
            {
                if (submesh?.FaceList == null)
                {
                    continue;
                }

                var baseVertex = submesh.BaseVertex;
                foreach (var face in submesh.FaceList)
                {
                    var indices = face?.VertexIndices;
                    if (indices == null)
                    {
                        continue;
                    }

                    for (int k = 0; k < indices.Length; k++)
                    {
                        var absolute = indices[k] + baseVertex;
                        var mapped = remap[absolute];
                        if (mapped < 0)
                        {
                            mapped = compacted.Count;
                            remap[absolute] = mapped;
                            compacted.Add(vertices[absolute]);
                        }
                        indices[k] = mapped;
                    }
                }
                submesh.BaseVertex = 0;
            }

            var removed = vertices.Count - compacted.Count;
            iMesh.VertexList = compacted;
            return removed;
        }

        private static Material? ResolveRendererSubmeshMaterialInMemory(Renderer meshR, int submeshIndex, int firstSubMesh)
        {
            var slotIndex = submeshIndex - firstSubMesh;
            if (meshR.m_Materials != null && slotIndex >= 0 && slotIndex < meshR.m_Materials.Length)
            {
                var matPPtr = meshR.m_Materials[slotIndex];
                if (matPPtr != null && !matPPtr.IsNull)
                {
                    if (!matPPtr.TryGet(out var m_Material))
                    {
                        Logger.Warning($"Material TryGet failed for submesh {submeshIndex}: FileID={matPPtr.m_FileID}, PathID={matPPtr.m_PathID}. Searching all loaded files...");
                        foreach (var sf in SnapshotAssetsFiles(meshR.assetsFile?.assetsManager))
                        {
                            if (sf.TryGetObject(matPPtr.m_PathID, out var obj) && obj is Material foundMat)
                            {
                                m_Material = foundMat;
                                Logger.Info($"Found material '{foundMat.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                break;
                            }
                        }
                    }
                    return m_Material;
                }

                Logger.Warning($"Material PPtr is null for submesh {submeshIndex} (m_Materials.Length={meshR.m_Materials.Length}).");
                return null;
            }

            Logger.Warning($"No material slot for submesh {submeshIndex}: index {slotIndex} >= m_Materials.Length {meshR.m_Materials?.Length ?? 0}.");
            return null;
        }

        private void UnbakeStaticBatchedMeshToLocalSpace(ImportedMesh iMesh)
        {
            if (RootFrame == null || iMesh.VertexList == null || iMesh.VertexList.Count == 0)
            {
                return;
            }

            var meshFrame = iMesh.TargetFrame ?? FindFrameForMesh(RootFrame, iMesh.Path);
            if (meshFrame == null)
            {
                return;
            }

            var globalMatrix = ComputeFrameGlobalMatrix(meshFrame);
            if (globalMatrix.IsIdentity || !System.Numerics.Matrix4x4.Invert(globalMatrix, out var invGlobal))
            {
                return;
            }

            var normalMatrix = BuildNormalMatrix(invGlobal, out var flipWinding);
            for (int v = 0; v < iMesh.VertexList.Count; v++)
            {
                var vert = iMesh.VertexList[v];
                var localPos = System.Numerics.Vector3.Transform(
                    new System.Numerics.Vector3(vert.Vertex.X, vert.Vertex.Y, vert.Vertex.Z),
                    invGlobal);
                vert.Vertex = new Vector3(
                    SanitizeFloat(localPos.X, 0f),
                    SanitizeFloat(localPos.Y, 0f),
                    SanitizeFloat(localPos.Z, 0f));

                if (iMesh.hasNormal)
                {
                    vert.Normal = TransformDirectionNormalized(vert.Normal, normalMatrix);
                }

                if (iMesh.hasTangent)
                {
                    var localTan = TransformDirectionNormalized(
                        new Vector3(vert.Tangent.X, vert.Tangent.Y, vert.Tangent.Z),
                        invGlobal);
                    float w = SanitizeFloat(vert.Tangent.W, 1f);
                    vert.Tangent = new Vector4(localTan.X, localTan.Y, localTan.Z, flipWinding ? -w : w);
                }
            }

            if (flipWinding && iMesh.SubmeshList != null)
            {
                for (int s = 0; s < iMesh.SubmeshList.Count; s++)
                {
                    var faceList = iMesh.SubmeshList[s]?.FaceList;
                    if (faceList == null) continue;
                    for (int f = 0; f < faceList.Count; f++)
                    {
                        var indices = faceList[f]?.VertexIndices;
                        if (indices != null && indices.Length >= 3)
                        {
                            (indices[1], indices[2]) = (indices[2], indices[1]);
                        }
                    }
                }
            }
        }

        private static string NormalizeBlendShapeChannelName(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            const string prefix = "blendShape.";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(prefix.Length);
            }

            return name;
        }

        private void RegisterMorphChannelHashes(MeshBlendShapeChannel shapeChannel, string cleanChannelName)
        {
            if (shapeChannel.nameHash != 0)
            {
                morphChannelNames[shapeChannel.nameHash] = cleanChannelName;
            }

            if (!string.IsNullOrEmpty(shapeChannel.name))
            {
                morphChannelNames[ComputeCrc32(shapeChannel.name)] = cleanChannelName;
                morphChannelNames[ComputeCrc32("blendShape." + shapeChannel.name)] = cleanChannelName;
            }

            if (!string.IsNullOrEmpty(cleanChannelName))
            {
                morphChannelNames[ComputeCrc32(cleanChannelName)] = cleanChannelName;
                morphChannelNames[ComputeCrc32("blendShape." + cleanChannelName)] = cleanChannelName;
            }
        }

        private static uint ComputeCrc32(string value)
        {
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(value);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
        }

        private static bool TryGetBoneTransform(PPtr<Transform> bonePtr, Renderer meshR, [NotNullWhen(true)] out Transform? transform)
        {
            if (bonePtr.TryGet(out transform))
                return true;
            if (bonePtr.IsNull)
                return false;
            foreach (var sf in SnapshotAssetsFiles(meshR.assetsFile?.assetsManager))
            {
                if (sf.TryGetObject(bonePtr.m_PathID, out var obj) && obj is Transform foundTransform)
                {
                    transform = foundTransform;
                    return true;
                }
            }
            return false;
        }

        private static Mesh? GetMesh(Renderer meshR)
        {
            if (meshR is SkinnedMeshRenderer sMesh)
            {
                if (!sMesh.m_Mesh.TryGet(out var m_Mesh))
                {
                    if (sMesh.m_Mesh != null && !sMesh.m_Mesh.IsNull)
                    {
                        foreach (var sf in SnapshotAssetsFiles(meshR.assetsFile?.assetsManager))
                        {
                            if (sf.TryGetObject(sMesh.m_Mesh.m_PathID, out var obj) && obj is Mesh foundMesh)
                            {
                                m_Mesh = foundMesh;
                                Logger.Info($"Found mesh '{foundMesh.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                break;
                            }
                        }
                    }
                }
                return m_Mesh;
            }
            else
            {
                if (!meshR.m_GameObject.TryGet(out var m_GameObject))
                {
                    foreach (var sf in SnapshotAssetsFiles(meshR.assetsFile?.assetsManager))
                    {
                        if (sf.TryGetObject(meshR.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                        {
                            m_GameObject = foundGo;
                            break;
                        }
                    }
                }
                if (m_GameObject?.m_MeshFilter != null)
                {
                    if (!m_GameObject.m_MeshFilter.m_Mesh.TryGet(out var m_Mesh))
                    {
                        if (m_GameObject.m_MeshFilter.m_Mesh != null && !m_GameObject.m_MeshFilter.m_Mesh.IsNull)
                        {
                            foreach (var sf in SnapshotAssetsFiles(meshR.assetsFile?.assetsManager))
                            {
                                if (sf.TryGetObject(m_GameObject.m_MeshFilter.m_Mesh.m_PathID, out var obj) && obj is Mesh foundMesh)
                                {
                                    m_Mesh = foundMesh;
                                    Logger.Info($"Found mesh '{foundMesh.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                    break;
                                }
                            }
                        }
                    }
                    return m_Mesh;
                }
            }

            return null;
        }

        private string? GetTransformPath(Transform? transform)
        {
            if (transform != null && transformDictionary.TryGetValue(transform, out var frame))
            {
                return frame.Path;
            }
            return null;
        }

        private string? FixBonePath(AnimationClip m_AnimationClip, string? path)
        {
            if (boundAnimationPathDic.TryGetValue(m_AnimationClip, out var basePath))
            {
                path = string.IsNullOrEmpty(path) ? basePath : basePath + "/" + path;
            }
            return FixBonePath(path);
        }

        private string? FixBonePath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return RootFrame.Path;

            var frame = RootFrame.FindFrameByPath(path);
            return frame?.Path ?? path;
        }

        private static string GetTransformPathByFather(Transform transform)
        {
            return GetTransformPathByFather(transform, new HashSet<Transform>());
        }

        private static string GetTransformPathByFather(Transform transform, HashSet<Transform> visited)
        {
            if (transform == null || !visited.Add(transform))
            {
                return "";
            }
            transform.m_GameObject.TryGet(out var m_GameObject);
            var goName = m_GameObject?.m_Name ?? "";
            if (transform.m_Father.TryGet(out var father) && !visited.Contains(father))
            {
                var fatherPath = GetTransformPathByFather(father, visited);
                return string.IsNullOrEmpty(fatherPath) ? goName : fatherPath + "/" + goName;
            }

            return goName;
        }


        /// <summary>
        /// Thread-safe factory converting a single Unity <see cref="Mesh"/> (including all submeshes,
        /// UV0..UV7, normals, tangents, vertex colors, BlendShapes, and optional bindposes/skinning)
        /// into an <see cref="IImported"/> ready for <see cref="ModelExporter.ExportFbx"/>.
        /// Assigns distinct submesh slot material names so Unity's FBXImporter preserves every submesh natively.
        /// </summary>
        public static ModelConverter CreateForUnityMeshAsset(
            Mesh mesh,
            string meshName,
            bool includeBindPoses = false)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            var cleanName = !string.IsNullOrWhiteSpace(meshName)
                ? meshName.Trim()
                : (!string.IsNullOrWhiteSpace(mesh.m_Name) ? mesh.m_Name.Trim() : $"Mesh_{mesh.m_PathID}");

            var converter = new ModelConverter(ImageFormat.Png, textureResolver: null);
            var rootFrame = CreateFrame(cleanName + "_Root", Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            converter.SetRootFrame(rootFrame);

            var meshFrame = CreateFrame(cleanName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            rootFrame.AddChild(meshFrame);

            converter.ConvertModelGroupMeshPart(mesh, meshFrame.Path, fallbackMaterials: null, renderer: null);

            if (converter.MeshList.Count > 0)
            {
                var iMesh = converter.MeshList[0];

                // Ensure distinct material slot names per submesh so Unity's FBXImporter never collapses
                // multi-material submeshes into a single submesh.
                if (iMesh.SubmeshList != null && iMesh.SubmeshList.Count > 1)
                {
                    converter.MaterialList.Clear();
                    for (int s = 0; s < iMesh.SubmeshList.Count; s++)
                    {
                        var slotMatName = $"mat_{s}";
                        iMesh.SubmeshList[s].Material = slotMatName;
                        converter.MaterialList.Add(new ImportedMaterial
                        {
                            Name = slotMatName,
                            Diffuse = new Color(0.8f, 0.8f, 0.8f, 1f),
                            Ambient = new Color(0.2f, 0.2f, 0.2f, 1f),
                            Specular = new Color(0.2f, 0.2f, 0.2f, 1f),
                            Emissive = new Color(0f, 0f, 0f, 1f),
                            Reflection = new Color(0f, 0f, 0f, 1f)
                        });
                    }
                }

                // Populate synthetic bone frames + exact bindpose matrices directly from Mesh.m_BindPose
                // so SkinnedMeshRenderer meshes export with 100% intact bindposes & boneWeights without
                // walking external scene transform hierarchies (fully thread-safe for parallel export).
                if (includeBindPoses
                    && mesh.m_BindPose is { Length: > 0 }
                    && mesh.m_Skin is { Length: > 0 }
                    && (iMesh.BoneList == null || iMesh.BoneList.Count == 0))
                {
                    int boneCount = mesh.m_BindPose.Length;
                    var convertMat = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int b = 0; b < boneCount; b++)
                    {
                        var boneFrame = CreateFrame($"Bone_{b}", Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
                        rootFrame.AddChild(boneFrame);
                        iMesh.BoneList.Add(new ImportedBone
                        {
                            Path = boneFrame.Path,
                            Matrix = convertMat * mesh.m_BindPose[b] * convertMat
                        });
                    }
                }
            }

            return converter;
        }

        /// <summary>
        /// Thread-safe factory converting <see cref="TerrainMeshData"/> into an <see cref="IImported"/>
        /// ready for <see cref="ModelExporter.ExportFbx"/> using the exact X-mirror/winding convention
        /// of <see cref="TerrainMeshConverter"/>.
        /// </summary>
        public static ModelConverter CreateForTerrainMeshData(TerrainMeshData meshData, string meshName)
        {
            ArgumentNullException.ThrowIfNull(meshData);
            var cleanName = !string.IsNullOrWhiteSpace(meshName) ? meshName.Trim() : "Terrain";

            var converter = new ModelConverter(ImageFormat.Png, textureResolver: null);
            var rootFrame = CreateFrame(cleanName + "_Root", Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            converter.SetRootFrame(rootFrame);

            var meshFrame = CreateFrame(cleanName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            rootFrame.AddChild(meshFrame);

            int vertCount = meshData.Positions.Length;
            var vertices = new List<ImportedVertex>(vertCount);
            for (int i = 0; i < vertCount; i++)
            {
                var p = meshData.Positions[i];
                var n = meshData.Normals[i];
                var uv = meshData.UVs[i];
                var uvs = new float[8][];
                uvs[0] = new[] { uv.X, uv.Y };
                vertices.Add(new ImportedVertex
                {
                    Vertex = new Vector3(-p.X, p.Y, p.Z),
                    Normal = new Vector3(-n.X, n.Y, n.Z),
                    UV = uvs
                });
            }

            int triCount = meshData.Indices.Length / 3;
            var faces = new List<ImportedFace>(triCount);
            for (int t = 0; t < triCount; t++)
            {
                int idx = t * 3;
                faces.Add(new ImportedFace
                {
                    VertexIndices = new[]
                    {
                        meshData.Indices[idx + 2],
                        meshData.Indices[idx + 1],
                        meshData.Indices[idx]
                    }
                });
            }

            var matName = "Terrain_Mat";
            converter.MaterialList.Add(new ImportedMaterial
            {
                Name = matName,
                Diffuse = new Color(0.8f, 0.8f, 0.8f, 1f),
                Ambient = new Color(0.2f, 0.2f, 0.2f, 1f),
                Specular = new Color(0.2f, 0.2f, 0.2f, 1f),
                Emissive = new Color(0f, 0f, 0f, 1f),
                Reflection = new Color(0f, 0f, 0f, 1f)
            });

            converter.MeshList.Add(new ImportedMesh
            {
                Path = meshFrame.Path,
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false },
                VertexList = vertices,
                SubmeshList = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh
                    {
                        Material = matName,
                        BaseVertex = 0,
                        FaceList = faces
                    }
                }
            });

            return converter;
        }

        public static ModelConverter CreateFromModelGroup(
            string? groupName,
            GameObject? rootGameObject,
            IReadOnlyList<Mesh>? meshes,
            IReadOnlyList<Renderer>? renderers,
            Avatar? avatar,
            AnimationClip[]? animationClips,
            ImageFormat imageFormat,
            bool combineMeshes = false,
            Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null)
        {
            return CreateFromModelGroup(
                groupName,
                rootGameObject,
                meshes,
                renderers,
                avatar,
                animationClips,
                imageFormat,
                combineMeshes,
                partTransforms: null,
                partMaterials: null,
                partNames: null,
                textureResolver: textureResolver);
        }

        public static ModelConverter CreateFromModelGroup(
            string? groupName,
            GameObject? rootGameObject,
            IReadOnlyList<Mesh>? meshes,
            IReadOnlyList<Renderer>? renderers,
            Avatar? avatar,
            AnimationClip[]? animationClips,
            ImageFormat imageFormat,
            bool combineMeshes,
            IReadOnlyList<float[]>? partTransforms,
            IReadOnlyList<IReadOnlyList<Material>>? partMaterials = null,
            IReadOnlyList<string>? partNames = null,
            Func<Material, string, PPtr<Texture>?, Texture2D?>? textureResolver = null)
        {
            var converter = new ModelConverter(imageFormat, textureResolver);
            converter.InitFromModelGroup(
                groupName,
                rootGameObject,
                meshes,
                renderers,
                avatar,
                animationClips,
                combineMeshes,
                partTransforms,
                partMaterials,
                partNames);
            return converter;
        }

        private void InitFromModelGroup(
            string? groupName,
            GameObject? rootGameObject,
            IReadOnlyList<Mesh>? meshes,
            IReadOnlyList<Renderer>? renderers,
            Avatar? providedAvatar,
            AnimationClip[]? animationClips,
            bool combineMeshes,
            IReadOnlyList<float[]>? partTransforms,
            IReadOnlyList<IReadOnlyList<Material>>? partMaterials,
            IReadOnlyList<string>? partNames)
        {
            var effectiveGroupName = !string.IsNullOrWhiteSpace(groupName)
                ? groupName
                : !string.IsNullOrWhiteSpace(rootGameObject?.m_Name)
                    ? rootGameObject.m_Name
                    : "ModelGroup";

            if (providedAvatar != null)
            {
                avatar = providedAvatar;
            }

            var convertedRenderers = new HashSet<Renderer>();
            var convertedMeshes = new HashSet<Mesh>();

            if (rootGameObject != null)
            {
                HydrateGameObjectHierarchy(rootGameObject);
                if (rootGameObject.m_Animator != null)
                {
                    InitWithAnimator(rootGameObject.m_Animator);
                    if (animationClips == null)
                    {
                        CollectAnimationClip(rootGameObject.m_Animator);
                    }
                }
                else
                {
                    InitWithGameObject(rootGameObject);
                }

                if (avatar == null)
                {
                    avatar = FindAvatar(rootGameObject);
                }

                CollectConvertedRenderersAndMeshesFromHierarchy(rootGameObject.m_Transform, convertedRenderers, convertedMeshes);
            }

            bool needsAvatarSkeletonBuild = false;
            if (!hasRootFrame)
            {
                SetRootFrame(CreateFrame(effectiveGroupName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One));
                needsAvatarSkeletonBuild = avatar != null;
            }

            // First hydrate real scene Transforms for renderers & their bones BEFORE falling back to Avatar skeleton frames,
            // preventing duplicate bone hierarchies under RootFrame (where mesh binds to Copy 2 while animation targets Copy 1).
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null || convertedRenderers.Contains(renderer))
                    {
                        continue;
                    }

                    var rendererGo = ResolveRendererGameObject(renderer);
                    if (rendererGo != null)
                    {
                        HydrateGameObjectHierarchy(rendererGo);
                        if (rendererGo.m_Transform != null && !transformDictionary.ContainsKey(rendererGo.m_Transform))
                        {
                            // Walk up to the renderer's prefab root ancestor instead of flattening rendererGo directly under RootFrame,
                            // preserving parent rotations/scales and keeping renderer nodes unified with their skeleton hierarchy.
                            AttachTransformChainUnderRoot(rendererGo.m_Transform);
                        }
                    }

                    if (renderer is SkinnedMeshRenderer smr && smr.m_Bones != null)
                    {
                        EnsureBoneTransformsInHierarchy(smr);
                    }
                }
            }

            if (needsAvatarSkeletonBuild && avatar != null)
            {
                TryBuildAvatarSkeletonFrames(meshes);
            }

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null || !convertedRenderers.Add(renderer))
                    {
                        continue;
                    }

                    var rendererMesh = GetMesh(renderer);
                    if (rendererMesh == null && meshes != null && i < meshes.Count)
                    {
                        rendererMesh = meshes[i];
                    }

                    if (rendererMesh == null)
                    {
                        continue;
                    }

                    var rendererGo = ResolveRendererGameObject(renderer);
                    var fallbackMats = partMaterials != null && i < partMaterials.Count ? partMaterials[i] : null;
                    var partTransform = partTransforms != null && i < partTransforms.Count ? partTransforms[i] : null;
                    var explicitName = partNames != null && i < partNames.Count ? partNames[i] : null;

                    string framePath;
                    if (rendererGo?.m_Transform != null && transformDictionary.TryGetValue(rendererGo.m_Transform, out var existingFrame))
                    {
                        framePath = existingFrame.Path;
                    }
                    else
                    {
                        var partFrame = CreatePartFrameUnderRoot(
                            !string.IsNullOrWhiteSpace(explicitName) ? explicitName : rendererGo?.m_Name ?? rendererMesh.m_Name ?? $"Part_{i}",
                            partTransform);
                        framePath = partFrame.Path;
                    }

                    ConvertModelGroupMeshPart(rendererMesh, framePath, fallbackMats, renderer);
                    convertedMeshes.Add(rendererMesh);
                }
            }

            if (meshes != null)
            {
                for (int i = 0; i < meshes.Count; i++)
                {
                    var mesh = meshes[i];
                    if (mesh == null)
                    {
                        continue;
                    }

                    var partTransform = partTransforms != null && i < partTransforms.Count ? partTransforms[i] : null;
                    var partMats = partMaterials != null && i < partMaterials.Count ? partMaterials[i] : null;
                    var explicitName = partNames != null && i < partNames.Count ? partNames[i] : null;

                    // If the same Mesh asset appears multiple times in a ModelGroup with distinct transforms/names,
                    // still emit each part instance when rootGameObject was not used to convert them.
                    if (rootGameObject != null && convertedMeshes.Contains(mesh))
                    {
                        continue;
                    }
                    if (rootGameObject == null && renderers != null && i < renderers.Count && renderers[i] != null && convertedMeshes.Contains(mesh))
                    {
                        continue;
                    }

                    var partName = !string.IsNullOrWhiteSpace(explicitName)
                        ? explicitName
                        : !string.IsNullOrWhiteSpace(mesh.m_Name)
                            ? mesh.m_Name
                            : $"Part_{i}";
                    var partFrame = CreatePartFrameUnderRoot(partName, partTransform);
                    ConvertModelGroupMeshPart(mesh, partFrame.Path, partMats, null);
                    convertedMeshes.Add(mesh);
                }
            }

            if (animationClips != null)
            {
                for (int i = 0; i < animationClips.Length; i++)
                {
                    if (animationClips[i] != null)
                    {
                        animationClipHashSet.Add(animationClips[i]);
                    }
                }
            }

            ConvertAnimations();
            FlushPendingTextures();

            if (combineMeshes)
            {
                CombineMeshes();
            }
        }

        private void AttachTransformChainUnderRoot(Transform startTransform)
        {
            if (RootFrame == null || transformDictionary.ContainsKey(startTransform))
            {
                return;
            }

            var top = startTransform;
            var visitedUp = new HashSet<Transform> { top };
            ImportedFrame? attachParentFrame = null;

            while (top.m_Father != null && !top.m_Father.IsNull && top.m_Father.TryGet(out var father) && father != null)
            {
                if (transformDictionary.TryGetValue(father, out var existingParentFrame))
                {
                    attachParentFrame = existingParentFrame;
                    break;
                }

                if (!visitedUp.Add(father))
                {
                    break;
                }

                top = father;
            }

            if (top.m_GameObject != null && top.m_GameObject.TryGet(out var topGo) && topGo != null)
            {
                HydrateGameObjectHierarchy(topGo);
            }

            ConvertTransforms(top, attachParentFrame ?? RootFrame);
            CreateBonePathHash(top);
        }

        private static void HydrateGameObjectHierarchy(GameObject rootGameObject)
        {
            if (rootGameObject == null)
            {
                return;
            }

            var visited = new HashSet<GameObject>();
            var queue = new Queue<GameObject>();
            queue.Enqueue(rootGameObject);

            while (queue.Count > 0)
            {
                var go = queue.Dequeue();
                if (go == null || !visited.Add(go))
                {
                    continue;
                }

                if (go.m_Components != null)
                {
                    for (int i = 0; i < go.m_Components.Length; i++)
                    {
                        var compPtr = go.m_Components[i];
                        if (compPtr == null || compPtr.IsNull)
                        {
                            continue;
                        }

                        Component? comp = null;
                        if (!compPtr.TryGet(out comp) && go.assetsFile?.assetsManager != null)
                        {
                            var files = SnapshotAssetsFiles(go.assetsFile.assetsManager);
                            for (int f = 0; f < files.Length; f++)
                            {
                                if (files[f].TryGetObject(compPtr.m_PathID, out var obj) && obj is Component foundComp)
                                {
                                    comp = foundComp;
                                    break;
                                }
                            }
                        }

                        if (comp == null)
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
                            case Animator anim when go.m_Animator == null:
                                go.m_Animator = anim;
                                break;
                            case Animation legacyAnim when go.m_Animation == null:
                                go.m_Animation = legacyAnim;
                                break;
                        }
                    }
                }

                if (go.m_Transform?.m_Children != null)
                {
                    for (int c = 0; c < go.m_Transform.m_Children.Length; c++)
                    {
                        var childPtr = go.m_Transform.m_Children[c];
                        if (childPtr == null || childPtr.IsNull)
                        {
                            continue;
                        }

                        if (!childPtr.TryGet(out var childTrans) && go.assetsFile?.assetsManager != null)
                        {
                            var files = SnapshotAssetsFiles(go.assetsFile.assetsManager);
                            for (int f = 0; f < files.Length; f++)
                            {
                                if (files[f].TryGetObject(childPtr.m_PathID, out var obj) && obj is Transform foundTrans)
                                {
                                    childTrans = foundTrans;
                                    break;
                                }
                            }
                        }

                        if (childTrans?.m_GameObject == null || childTrans.m_GameObject.IsNull)
                        {
                            continue;
                        }

                        if (!childTrans.m_GameObject.TryGet(out var childGo) && childTrans.assetsFile?.assetsManager != null)
                        {
                            var files = SnapshotAssetsFiles(childTrans.assetsFile.assetsManager);
                            for (int f = 0; f < files.Length; f++)
                            {
                                if (files[f].TryGetObject(childTrans.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                                {
                                    childGo = foundGo;
                                    break;
                                }
                            }
                        }

                        if (childGo != null && !visited.Contains(childGo))
                        {
                            queue.Enqueue(childGo);
                        }
                    }
                }
            }
        }

        private static void CollectConvertedRenderersAndMeshesFromHierarchy(
            Transform? rootTransform,
            HashSet<Renderer> convertedRenderers,
            HashSet<Mesh> convertedMeshes)
        {
            if (rootTransform == null)
            {
                return;
            }

            var visited = new HashSet<Transform>();
            var stack = new Stack<Transform>();
            stack.Push(rootTransform);

            while (stack.Count > 0)
            {
                var trans = stack.Pop();
                if (trans == null || !visited.Add(trans))
                {
                    continue;
                }

                if (trans.m_GameObject != null && trans.m_GameObject.TryGet(out var go) && go != null)
                {
                    if (go.m_MeshRenderer != null)
                    {
                        convertedRenderers.Add(go.m_MeshRenderer);
                        var mesh = GetMesh(go.m_MeshRenderer);
                        if (mesh != null) convertedMeshes.Add(mesh);
                    }
                    if (go.m_SkinnedMeshRenderer != null)
                    {
                        convertedRenderers.Add(go.m_SkinnedMeshRenderer);
                        var mesh = GetMesh(go.m_SkinnedMeshRenderer);
                        if (mesh != null) convertedMeshes.Add(mesh);
                    }
                }

                if (trans.m_Children != null)
                {
                    for (int i = 0; i < trans.m_Children.Length; i++)
                    {
                        if (trans.m_Children[i] != null && trans.m_Children[i].TryGet(out var child) && child != null)
                        {
                            stack.Push(child);
                        }
                    }
                }
            }
        }

        private static GameObject? ResolveRendererGameObject(Renderer? renderer)
        {
            if (renderer?.m_GameObject == null || renderer.m_GameObject.IsNull)
            {
                return null;
            }

            if (renderer.m_GameObject.TryGet(out var go) && go != null)
            {
                return go;
            }

            if (renderer.assetsFile?.assetsManager != null)
            {
                var files = renderer.assetsFile.assetsManager.assetsFileList;
                for (int i = 0; i < files.Count; i++)
                {
                    if (files[i].ObjectsDic.TryGetValue(renderer.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        return foundGo;
                    }
                }
            }

            return null;
        }

        private void EnsureBoneTransformsInHierarchy(SkinnedMeshRenderer smr)
        {
            if (smr.m_Bones == null || RootFrame == null)
            {
                return;
            }

            var rootBoneTransforms = new HashSet<Transform>();
            for (int i = 0; i < smr.m_Bones.Length; i++)
            {
                if (!TryGetBoneTransform(smr.m_Bones[i], smr, out var boneTrans) || boneTrans == null)
                {
                    continue;
                }

                if (transformDictionary.ContainsKey(boneTrans))
                {
                    continue;
                }

                // Walk up to the highest ancestor transform not yet in transformDictionary
                var top = boneTrans;
                var visitedAncestors = new HashSet<Transform> { top };
                while (top.m_Father != null && !top.m_Father.IsNull)
                {
                    Transform? father = null;
                    if (!top.m_Father.TryGet(out father) && smr.assetsFile?.assetsManager != null)
                    {
                        var files = smr.assetsFile.assetsManager.assetsFileList;
                        for (int f = 0; f < files.Count; f++)
                        {
                            if (files[f].ObjectsDic.TryGetValue(top.m_Father.m_PathID, out var obj) && obj is Transform foundFather)
                            {
                                father = foundFather;
                                break;
                            }
                        }
                    }

                    if (father == null || !visitedAncestors.Add(father))
                    {
                        break;
                    }

                    if (transformDictionary.ContainsKey(father))
                    {
                        ConvertTransforms(top, transformDictionary[father]);
                        CreateBonePathHash(top);
                        top = null;
                        break;
                    }

                    top = father;
                }

                if (top != null && rootBoneTransforms.Add(top))
                {
                    ConvertTransforms(top, RootFrame);
                    CreateBonePathHash(top);
                }
            }
        }

        private void TryBuildAvatarSkeletonFrames(IReadOnlyList<Mesh>? meshes)
        {
            var skeleton = avatar?.m_Avatar?.m_AvatarSkeleton;
            var skeletonPose = avatar?.m_Avatar?.m_DefaultPose?.m_X is { Length: > 0 }
                ? avatar.m_Avatar.m_DefaultPose
                : avatar?.m_Avatar?.m_AvatarSkeletonPose;
            var nodes = skeleton?.m_Node;
            if (avatar == null || skeleton == null || nodes == null || nodes.Length == 0 || skeleton.m_ID == null)
            {
                return;
            }

            BuildSkeletonFramesFromDefaultPose(skeleton, skeletonPose, meshes, preserveExistingTransformFrames: true);
        }

        private ImportedFrame CreatePartFrameUnderRoot(string? preferredName, float[]? unityRowMajorMatrix)
        {
            var baseName = string.IsNullOrWhiteSpace(preferredName) ? "Part" : preferredName;
            var uniqueName = baseName;
            if (RootFrame != null && RootFrame.FindChild(uniqueName, recursive: false) != null)
            {
                for (int suffix = 1; ; suffix++)
                {
                    var candidate = $"{baseName}_{suffix}";
                    if (RootFrame.FindChild(candidate, recursive: false) == null)
                    {
                        uniqueName = candidate;
                        break;
                    }
                }
            }

            var tVec = Vector3.Zero;
            var qQuat = new Quaternion(0, 0, 0, 1);
            var sVec = Vector3.One;

            if (unityRowMajorMatrix != null && unityRowMajorMatrix.Length == 16)
            {
                // ModelGroupMeshInfo.TransformMatrix stores a Unity column-vector 4x4 matrix in row-major order
                // (translation in [3], [7], [11]). Transpose to System.Numerics row-vector layout before decomposing.
                var unityNumerics = new System.Numerics.Matrix4x4(
                    unityRowMajorMatrix[0], unityRowMajorMatrix[4], unityRowMajorMatrix[8], unityRowMajorMatrix[12],
                    unityRowMajorMatrix[1], unityRowMajorMatrix[5], unityRowMajorMatrix[9], unityRowMajorMatrix[13],
                    unityRowMajorMatrix[2], unityRowMajorMatrix[6], unityRowMajorMatrix[10], unityRowMajorMatrix[14],
                    unityRowMajorMatrix[3], unityRowMajorMatrix[7], unityRowMajorMatrix[11], unityRowMajorMatrix[15]);

                if (System.Numerics.Matrix4x4.Decompose(unityNumerics, out var s, out var q, out var t))
                {
                    tVec = new Vector3(t.X, t.Y, t.Z);
                    qQuat = new Quaternion(q.X, q.Y, q.Z, q.W);
                    sVec = new Vector3(s.X, s.Y, s.Z);
                }
                else
                {
                    tVec = new Vector3(unityRowMajorMatrix[3], unityRowMajorMatrix[7], unityRowMajorMatrix[11]);
                }
            }

            var frame = CreateFrame(uniqueName, tVec, qQuat, sVec);
            RootFrame?.AddChild(frame);
            return frame;
        }

        private void ConvertModelGroupMeshPart(Mesh mesh, string? framePath, IReadOnlyList<Material>? fallbackMaterials, Renderer? renderer)
        {
            if (mesh == null)
            {
                return;
            }

            mesh.EnsureProcessed();
            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
            {
                return;
            }

            var iMesh = new ImportedMesh
            {
                Path = framePath ?? string.Empty,
                SubmeshList = new List<ImportedSubmesh>()
            };

            HashSet<int>? subHashSet = null;
            int firstSubMesh = 0;
            if (renderer != null)
            {
                if (renderer.m_StaticBatchInfo?.subMeshCount > 0)
                {
                    firstSubMesh = renderer.m_StaticBatchInfo.firstSubMesh;
                    var finalSubMesh = firstSubMesh + renderer.m_StaticBatchInfo.subMeshCount;
                    subHashSet = new HashSet<int>();
                    for (int i = firstSubMesh; i < finalSubMesh; i++)
                    {
                        subHashSet.Add(i);
                    }
                }
                else if (renderer.m_SubsetIndices?.Length > 0)
                {
                    subHashSet = new HashSet<int>();
                    uint minIdx = uint.MaxValue;
                    for (int i = 0; i < renderer.m_SubsetIndices.Length; i++)
                    {
                        var idx = renderer.m_SubsetIndices[i];
                        if (idx < minIdx) minIdx = idx;
                        subHashSet.Add((int)idx);
                    }
                    firstSubMesh = (int)minIdx;
                }
            }

            iMesh.hasNormal = mesh.m_Normals?.Length > 0;
            iMesh.hasUV = new bool[8];
            for (int uv = 0; uv < 8; uv++)
            {
                iMesh.hasUV[uv] = mesh.GetUV(uv)?.Length > 0;
            }
            iMesh.hasTangent = mesh.m_Tangents != null && mesh.m_Tangents.Length == mesh.m_VertexCount * 4;
            iMesh.hasColor = mesh.m_Colors?.Length > 0;

            var subMeshes = mesh.m_SubMeshes;
            if (subMeshes != null && subMeshes.Length > 0)
            {
                int firstFace = 0;
                for (int i = 0; i < subMeshes.Length; i++)
                {
                    int numFaces = (int)subMeshes[i].indexCount / 3;
                    if (subHashSet != null && subHashSet.Count > 0 && !subHashSet.Contains(i))
                    {
                        firstFace += numFaces;
                        continue;
                    }

                    var submesh = subMeshes[i];
                    var matSlotIndex = i - firstSubMesh;
                    Material? mat = ResolvePartMaterial(renderer, matSlotIndex, fallbackMaterials);
                    var iMat = ConvertMaterial(mat);

                    var iSubmesh = new ImportedSubmesh
                    {
                        Material = iMat.Name,
                        BaseVertex = checked((int)(submesh.firstVertex + submesh.baseVertex)),
                        FaceList = new List<ImportedFace>(numFaces)
                    };

                    var end = firstFace + numFaces;
                    for (int f = firstFace; f < end; f++)
                    {
                        if (f * 3 + 2 >= mesh.m_Indices.Count)
                        {
                            break;
                        }

                        var face = new ImportedFace
                        {
                            VertexIndices = new[]
                            {
                                (int)(mesh.m_Indices[f * 3 + 2] - submesh.firstVertex),
                                (int)(mesh.m_Indices[f * 3 + 1] - submesh.firstVertex),
                                (int)(mesh.m_Indices[f * 3] - submesh.firstVertex)
                            }
                        };
                        iSubmesh.FaceList.Add(face);
                    }

                    firstFace = end;
                    iMesh.SubmeshList.Add(iSubmesh);
                }
            }
            else if (mesh.m_Indices != null && mesh.m_Indices.Count >= 3)
            {
                var mat = ResolvePartMaterial(renderer, 0, fallbackMaterials);
                var iMat = ConvertMaterial(mat);
                int numFaces = mesh.m_Indices.Count / 3;
                var iSubmesh = new ImportedSubmesh
                {
                    Material = iMat.Name,
                    BaseVertex = 0,
                    FaceList = new List<ImportedFace>(numFaces)
                };
                for (int f = 0; f < numFaces; f++)
                {
                    iSubmesh.FaceList.Add(new ImportedFace
                    {
                        VertexIndices = new[]
                        {
                            (int)mesh.m_Indices[f * 3 + 2],
                            (int)mesh.m_Indices[f * 3 + 1],
                            (int)mesh.m_Indices[f * 3]
                        }
                    });
                }
                iMesh.SubmeshList.Add(iSubmesh);
            }

            int vertexCount = mesh.m_VertexCount;
            int posStride = mesh.m_Vertices.Length == vertexCount * 4 ? 4 : 3;
            int normalStride = iMesh.hasNormal && mesh.m_Normals != null && mesh.m_Normals.Length == vertexCount * 4 ? 4 : 3;
            int colorStride = iMesh.hasColor && mesh.m_Colors != null && mesh.m_Colors.Length == vertexCount * 3 ? 3 : 4;
            var uvArrays = new float[8][];
            var uvStrides = new int[8];
            for (int uv = 0; uv < 8; uv++)
            {
                if (iMesh.hasUV[uv])
                {
                    var uvData = mesh.GetUV(uv);
                    if (uvData != null && vertexCount > 0 && uvData.Length >= vertexCount)
                    {
                        uvArrays[uv] = uvData;
                        uvStrides[uv] = uvData.Length / vertexCount;
                    }
                }
            }

            iMesh.VertexList = new List<ImportedVertex>(vertexCount);
            for (int j = 0; j < vertexCount; j++)
            {
                var iVertex = new ImportedVertex
                {
                    Vertex = new Vector3(-mesh.m_Vertices[j * posStride], mesh.m_Vertices[j * posStride + 1], mesh.m_Vertices[j * posStride + 2]),
                    UV = new float[8][]
                };

                if (iMesh.hasNormal && mesh.m_Normals != null && j * normalStride + 2 < mesh.m_Normals.Length)
                {
                    iVertex.Normal = new Vector3(-mesh.m_Normals[j * normalStride], mesh.m_Normals[j * normalStride + 1], mesh.m_Normals[j * normalStride + 2]);
                }

                for (int uv = 0; uv < 8; uv++)
                {
                    int uvStride = uvStrides[uv];
                    if (uvStride > 0)
                    {
                        var m_UV = uvArrays[uv];
                        int uvOffset = j * uvStride;
                        if (uvStride == 1)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], 0f };
                        }
                        else if (uvOffset + 1 < m_UV.Length)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], m_UV[uvOffset + 1] };
                        }
                    }
                }

                if (iMesh.hasTangent && mesh.m_Tangents != null)
                {
                    iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], -mesh.m_Tangents[j * 4 + 3]);
                }

                if (iMesh.hasColor && mesh.m_Colors != null && j * colorStride + 2 < mesh.m_Colors.Length)
                {
                    iVertex.Color = colorStride == 3
                        ? new Color(mesh.m_Colors[j * 3], mesh.m_Colors[j * 3 + 1], mesh.m_Colors[j * 3 + 2], 1.0f)
                        : new Color(mesh.m_Colors[j * 4], mesh.m_Colors[j * 4 + 1], mesh.m_Colors[j * 4 + 2], mesh.m_Colors[j * 4 + 3]);
                }

                if (mesh.m_Skin != null && j < mesh.m_Skin.Length && mesh.m_Skin[j]?.boneIndex != null && mesh.m_Skin[j]?.weight != null)
                {
                    var inf = mesh.m_Skin[j];
                    int rawSlots = Math.Min(inf.boneIndex.Length, inf.weight.Length);
                    int slotCount = Math.Max(4, rawSlots);
                    iVertex.BoneIndices = new int[slotCount];
                    iVertex.Weights = new float[slotCount];
                    for (int k = 0; k < rawSlots; k++)
                    {
                        iVertex.BoneIndices[k] = inf.boneIndex[k];
                        iVertex.Weights[k] = inf.weight[k];
                    }
                }

                iMesh.VertexList.Add(iVertex);
            }

            // Bones (from SkinnedMeshRenderer.m_Bones or Mesh.m_BoneNameHashes)
            if (mesh.m_BindPose != null && mesh.m_BindPose.Length > 0)
            {
                var boneType = 0;
                var sMesh = renderer as SkinnedMeshRenderer;
                if (sMesh != null && sMesh.m_Bones != null && sMesh.m_Bones.Length == mesh.m_BindPose.Length)
                {
                    int verified = 0;
                    for (int b = 0; b < sMesh.m_Bones.Length; b++)
                    {
                        if (TryGetBoneTransform(sMesh.m_Bones[b], sMesh, out _)) verified++;
                    }

                    if (verified > 0)
                    {
                        boneType = 1;
                    }
                    if (verified != sMesh.m_Bones.Length && mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length == mesh.m_BindPose.Length)
                    {
                        int verifiedByHash = 0;
                        for (int b = 0; b < mesh.m_BoneNameHashes.Length; b++)
                        {
                            if (TryResolveBonePathFromHash(mesh.m_BoneNameHashes[b], out _)) verifiedByHash++;
                        }
                        if (verifiedByHash > verified)
                        {
                            boneType = 2;
                        }
                    }
                }

                if (boneType == 0 && mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length == mesh.m_BindPose.Length)
                {
                    int verifiedByHash = 0;
                    for (int b = 0; b < mesh.m_BoneNameHashes.Length; b++)
                    {
                        if (TryResolveBonePathFromHash(mesh.m_BoneNameHashes[b], out _)) verifiedByHash++;
                    }
                    if (verifiedByHash > 0)
                    {
                        boneType = 2;
                    }
                }

                var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                if (boneType == 1 && sMesh?.m_Bones != null)
                {
                    var boneCount = sMesh.m_Bones.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int b = 0; b < boneCount; b++)
                    {
                        var bone = new ImportedBone();
                        if (TryGetBoneTransform(sMesh.m_Bones[b], sMesh, out var boneTransform))
                        {
                            bone.Path = GetTransformPath(boneTransform);
                        }
                        bone.Matrix = convert * mesh.m_BindPose[b] * convert;
                        iMesh.BoneList.Add(bone);
                    }
                }
                else if (boneType == 2 && mesh.m_BoneNameHashes != null)
                {
                    var boneCount = mesh.m_BindPose.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int b = 0; b < boneCount; b++)
                    {
                        var bone = new ImportedBone
                        {
                            Path = TryResolveBonePathFromHash(mesh.m_BoneNameHashes[b], out var resolvedBonePath) ? resolvedBonePath : null,
                            Matrix = convert * mesh.m_BindPose[b] * convert
                        };
                        iMesh.BoneList.Add(bone);
                    }
                }
            }

            // BlendShapes / Morphs
            if (mesh.m_Shapes?.channels?.Length > 0)
            {
                var morph = new ImportedMorph
                {
                    Path = iMesh.Path,
                    Channels = new List<ImportedMorphChannel>(mesh.m_Shapes.channels.Length)
                };
                MorphList.Add(morph);
                for (int i = 0; i < mesh.m_Shapes.channels.Length; i++)
                {
                    var shapeChannel = mesh.m_Shapes.channels[i];
                    var cleanChannelName = NormalizeBlendShapeChannelName(shapeChannel.name);
                    RegisterMorphChannelHashes(shapeChannel, cleanChannelName);

                    var channel = new ImportedMorphChannel
                    {
                        Name = cleanChannelName,
                        KeyframeList = new List<ImportedMorphKeyframe>(shapeChannel.frameCount)
                    };
                    morph.Channels.Add(channel);

                    var frameEnd = shapeChannel.frameIndex + shapeChannel.frameCount;
                    for (int frameIdx = shapeChannel.frameIndex; frameIdx < frameEnd; frameIdx++)
                    {
                        if (frameIdx < 0 || frameIdx >= mesh.m_Shapes.fullWeights.Length || frameIdx >= mesh.m_Shapes.shapes.Length)
                            break;
                        var shape = mesh.m_Shapes.shapes[frameIdx];
                        var keyframe = new ImportedMorphKeyframe
                        {
                            Weight = mesh.m_Shapes.fullWeights[frameIdx],
                            hasNormals = shape.hasNormals,
                            hasTangents = shape.hasTangents,
                            VertexList = new List<ImportedMorphVertex>((int)shape.vertexCount)
                        };
                        channel.KeyframeList.Add(keyframe);

                        var vertexEnd = shape.firstVertex + shape.vertexCount;
                        for (uint j = shape.firstVertex; j < vertexEnd; j++)
                        {
                            if (j >= (uint)mesh.m_Shapes.vertices.Length)
                                break;
                            var morphVertex = mesh.m_Shapes.vertices[j];
                            if (morphVertex.index >= (uint)iMesh.VertexList.Count)
                                continue;
                            var sourceVertex = iMesh.VertexList[(int)morphVertex.index];
                            var destVertex = new ImportedMorphVertex
                            {
                                Index = morphVertex.index,
                                Vertex = new ImportedVertex
                                {
                                    Vertex = sourceVertex.Vertex + new Vector3(-morphVertex.vertex.X, morphVertex.vertex.Y, morphVertex.vertex.Z)
                                }
                            };
                            if (shape.hasNormals)
                            {
                                destVertex.Vertex.Normal = new Vector3(-morphVertex.normal.X, morphVertex.normal.Y, morphVertex.normal.Z);
                            }
                            if (shape.hasTangents)
                            {
                                destVertex.Vertex.Tangent = new Vector4(-morphVertex.tangent.X, morphVertex.tangent.Y, morphVertex.tangent.Z, 0);
                            }
                            keyframe.VertexList.Add(destVertex);
                        }
                    }
                }
            }

            if (subHashSet != null && subHashSet.Count > 0)
            {
                UnbakeStaticBatchedMeshToLocalSpace(iMesh);
            }

            MeshList.Add(iMesh);
        }

        private static Material? ResolvePartMaterial(Renderer? renderer, int slotIndex, IReadOnlyList<Material>? fallbackMaterials)
        {
            if (renderer?.m_Materials != null && slotIndex >= 0 && slotIndex < renderer.m_Materials.Length)
            {
                var matPPtr = renderer.m_Materials[slotIndex];
                if (matPPtr != null && !matPPtr.IsNull)
                {
                    if (matPPtr.TryGet(out var resolvedMat) && resolvedMat != null)
                    {
                        return resolvedMat;
                    }

                    if (renderer.assetsFile?.assetsManager != null)
                    {
                        var files = renderer.assetsFile.assetsManager.assetsFileList;
                        for (int i = 0; i < files.Count; i++)
                        {
                            if (files[i].ObjectsDic.TryGetValue(matPPtr.m_PathID, out var obj) && obj is Material foundMat)
                            {
                                return foundMat;
                            }
                        }
                    }
                }
            }

            if (fallbackMaterials != null && slotIndex >= 0 && slotIndex < fallbackMaterials.Count && fallbackMaterials[slotIndex] != null)
            {
                return fallbackMaterials[slotIndex];
            }

            return null;
        }

    }
}
