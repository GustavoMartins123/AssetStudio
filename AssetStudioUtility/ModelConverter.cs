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

    public class ModelConverter : IImported
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

        private ImportedMaterial ConvertMaterial(Material? mat)
        {
            ImportedMaterial? iMat;
            if (mat != null && !string.IsNullOrEmpty(mat.m_Name))
            {
                iMat = ImportedHelpers.FindMaterial(mat.m_Name, MaterialList);
                if (iMat != null)
                {
                    return iMat;
                }
                iMat = new ImportedMaterial();
                iMat.Name = mat.m_Name;
                //default values
                iMat.Diffuse = new Color(0.8f, 0.8f, 0.8f, 1);
                iMat.Ambient = new Color(0.2f, 0.2f, 0.2f, 1);
                iMat.Emissive = new Color(0, 0, 0, 1);
                iMat.Specular = new Color(0.2f, 0.2f, 0.2f, 1);
                iMat.Reflection = new Color(0, 0, 0, 1);
                iMat.Shininess = 20f;
                iMat.Transparency = 0f;
                iMat.DoubleSided = mat.m_DoubleSidedGI;

                var effectiveColors = CollectEffectiveMaterialColors(mat);
                var effectiveFloats = CollectEffectiveMaterialFloatsAndInts(mat);
                var effectiveKeywords = CollectEffectiveMaterialKeywords(mat);
                var effectiveTags = CollectEffectiveMaterialTags(mat);
                var customRenderQueue = ResolveEffectiveRenderQueue(mat);
                var (resolvedShader, shaderName) = ResolveMaterialShaderInfo(mat);
                var shaderInspection = InspectParsedShader(resolvedShader);

                foreach (var kv in shaderInspection.SubShaderTags)
                {
                    if (!effectiveTags.ContainsKey(kv.Key))
                    {
                        effectiveTags[kv.Key] = kv.Value;
                    }
                }

                if (shaderInspection.ForwardPassCullOff)
                {
                    iMat.DoubleSided = true;
                }

                bool hasExplicitBaseColor = false;
                bool alphaClipEnabled = false;
                float? urpSurface = null;
                float? urpBlend = null;
                float? srcBlend = null;
                float? dstBlend = null;
                float? zWrite = null;
                float? explicitIor = null;
                float? explicitOpacity = null;

                foreach (var col in effectiveColors)
                {
                    switch (col.Key)
                    {
                        case "_Color":
                        case "_BaseColor":
                            iMat.Diffuse = col.Value;
                            hasExplicitBaseColor = true;
                            break;
                        case "_SColor":
                            iMat.Ambient = col.Value;
                            break;
                        case "_EmissionColor":
                        case "_EmissiveColor":
                            iMat.Emissive = col.Value;
                            break;
                        case "_SpecularColor":
                        case "_SpecColor":
                            iMat.Specular = col.Value;
                            break;
                        case "_ReflectColor":
                            iMat.Reflection = col.Value;
                            break;
                    }
                }

                foreach (var flt in effectiveFloats)
                {
                    switch (flt.Key)
                    {
                        case "_Shininess":
                            iMat.Shininess = flt.Value;
                            break;
                        case "_Transparency":
                            iMat.Transparency = flt.Value;
                            break;
                        case "_Metallic":
                            iMat.Metallic = flt.Value;
                            break;
                        case "_Glossiness":
                        case "_Smoothness":
                        case "_GlossMapScale":
                            iMat.Smoothness = flt.Value;
                            break;
                        case "_Roughness":
                            iMat.Roughness = flt.Value;
                            break;
                        case "_Cutoff":
                        case "_AlphaCutoff":
                        case "_ClipThreshold":
                            iMat.AlphaCutoff = flt.Value;
                            break;
                        case "_Mode":
                            iMat.SurfaceMode = flt.Value;
                            break;
                        case "_Surface":
                            urpSurface = flt.Value;
                            break;
                        case "_Blend":
                            urpBlend = flt.Value;
                            break;
                        case "_SrcBlend":
                            srcBlend = flt.Value;
                            break;
                        case "_DstBlend":
                            dstBlend = flt.Value;
                            break;
                        case "_ZWrite":
                            zWrite = flt.Value;
                            break;
                        case "_AlphaClip":
                            alphaClipEnabled = flt.Value >= 0.5f;
                            break;
                        case "_Cull":
                            if (flt.Value <= 0.5f)
                            {
                                iMat.DoubleSided = true;
                            }
                            break;
                        case "_BumpScale":
                        case "_NormalScale":
                            iMat.BumpScale = flt.Value;
                            break;
                        case "_OcclusionStrength":
                            iMat.OcclusionStrength = flt.Value;
                            break;
                        case "_IOR":
                        case "_IndexOfRefraction":
                        case "_RefractionIndex":
                            explicitIor = flt.Value;
                            break;
                        case "_Opacity":
                        case "_Alpha":
                        case "_AlphaScale":
                        case "_Fade":
                            explicitOpacity = flt.Value;
                            break;
                    }
                }

                //textures (inherit from m_Parent Material Variant chain when child slots are empty/unset)
                iMat.Textures = new List<ImportedMaterialTexture>();
                var activeResolver = textureResolver ?? AmbientTextureResolver.Value;
                var effectiveTexEnvs = CollectEffectiveMaterialTexEnvs(mat);
                ImportedMaterialTexture? fallbackDiffuseCandidate = null;
                var diffusePriorities = new Dictionary<ImportedMaterialTexture, int>();
                var seenNonDiffuseTextures = new HashSet<Texture2D>();
                var seenNonDiffuseTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var recordedTextureProperties = new Dictionary<string, string>(StringComparer.Ordinal);
                bool hasNormalMapSlot = false;
                bool hasMultipleNormalMapSlots = false;
                int normalMapSlotCount = 0;

                foreach (var (ownerMat, texEnv) in effectiveTexEnvs)
                {
                    if (texEnv.Value == null)
                    {
                        continue;
                    }
                    var textureRef = texEnv.Value.m_Texture;
                    Texture2D? m_Texture2D = null;
                    if (activeResolver != null)
                    {
                        m_Texture2D = activeResolver(ownerMat, texEnv.Key, textureRef);
                        if (m_Texture2D == null && !ReferenceEquals(ownerMat, mat))
                        {
                            m_Texture2D = activeResolver(mat, texEnv.Key, textureRef);
                        }
                    }
                    if (m_Texture2D == null && textureRef != null && !textureRef.IsNull) //TODO other Texture
                    {
                        if (!TryFindTextureInMaterialScope(ownerMat, textureRef, out m_Texture2D)
                            && !ReferenceEquals(ownerMat, mat))
                        {
                            TryFindTextureInMaterialScope(mat, textureRef, out m_Texture2D);
                        }
                    }

                    if (m_Texture2D == null)
                    {
                        if (textureRef != null && !textureRef.IsNull)
                        {
                            Logger.Warning($"Unable to resolve texture reference for material {mat.m_Name}, property {texEnv.Key}, PathID {textureRef.m_PathID}.");
                        }
                        continue;
                    }

                    var texture = new ImportedMaterialTexture();
                    iMat.Textures.Add(texture);

                    int dest = GetTextureDestination(texEnv.Key);
                    // Generic slots like _Texture / _Tex may carry a normal map in custom/ShaderGraph shaders;
                    // never bind a texture whose asset name indicates a normal/bump map to Diffuse (Dest = 0).
                    if (dest == 0 && GetDiffuseSlotPriority(texEnv.Key) >= 31 && IsLikelyNormalMapTextureAssetName(m_Texture2D.m_Name))
                    {
                        dest = 1;
                    }

                    texture.Dest = dest;
                    texture.Name = AllocateOrGetConvertedTextureName(m_Texture2D);
                    texture.Offset = texEnv.Value.m_Offset;
                    texture.Scale = texEnv.Value.m_Scale;
                    recordedTextureProperties[texEnv.Key] = texture.Name;

                    if (texture.Dest == 0)
                    {
                        diffusePriorities[texture] = GetDiffuseSlotPriority(texEnv.Key);
                    }
                    else
                    {
                        seenNonDiffuseTextures.Add(m_Texture2D);
                        seenNonDiffuseTextureNames.Add(texture.Name);
                        if (texture.Dest == 1)
                        {
                            hasNormalMapSlot = true;
                            normalMapSlotCount++;
                            if (normalMapSlotCount >= 2)
                            {
                                hasMultipleNormalMapSlots = true;
                            }
                        }
                        else if (texture.Dest < 0
                            && fallbackDiffuseCandidate == null
                            && !IsLikelyAuxiliaryTextureProperty(texEnv.Key)
                            && !IsLikelyNonDiffuseTextureAssetName(m_Texture2D.m_Name))
                        {
                            fallbackDiffuseCandidate = texture;
                        }
                    }

                    ConvertTexture2D(m_Texture2D, texture.Name, mat.m_Name, texEnv.Key, forceNormalMapUnpack: texture.Dest == 1);
                }

                // When multiple slots map to diffuse (Dest = 0), e.g. _BaseEyeIrisMap + _BaseEyeWhiteMap or _MainTexture + _Texture,
                // ensure the highest-priority diffuse slot comes first so exporters select it as primary baseColorTexture.
                if (diffusePriorities.Count > 1)
                {
                    ImportedMaterialTexture? bestDiffuse = null;
                    int bestPriority = int.MaxValue;
                    for (int i = 0; i < iMat.Textures.Count; i++)
                    {
                        var t = iMat.Textures[i];
                        if (t.Dest == 0 && diffusePriorities.TryGetValue(t, out int prio) && prio < bestPriority)
                        {
                            bestPriority = prio;
                            bestDiffuse = t;
                        }
                    }

                    if (bestDiffuse != null && iMat.Textures[0] != bestDiffuse)
                    {
                        iMat.Textures.Remove(bestDiffuse);
                        iMat.Textures.Insert(0, bestDiffuse);
                    }
                }

                bool hasExplicitDiffuseTexture = iMat.Textures.Any(static t => t.Dest == 0);
                bool hasWaterSpecificProperties = HasWaterSpecificMaterialProperties(effectiveColors, effectiveFloats, effectiveTexEnvs);
                bool hasRefractionSpecificProperties = HasRefractionSpecificMaterialProperties(effectiveFloats, effectiveColors);

                bool isInvisibleShader = shaderInspection.WritesNoColor || IsInvisibleShaderName(shaderName);
                bool isWaterShader = !isInvisibleShader && (
                    IsWaterShaderName(shaderName)
                    || (!hasExplicitDiffuseTexture && (hasWaterSpecificProperties || (hasMultipleNormalMapSlots && !iMat.Textures.Any(static t => t.Dest == 3 || t.Dest == 2)))));
                bool isRefractiveShader = !isInvisibleShader && !isWaterShader && (
                    IsRefractiveOrGlassShaderName(shaderName)
                    || (!hasExplicitDiffuseTexture && hasRefractionSpecificProperties && hasNormalMapSlot));
                bool isSkyShader = !isInvisibleShader && !isWaterShader && IsSkyShaderName(shaderName);

                // Custom/ShaderGraph shaders often name their primary color map generically (e.g. _Texture, Texture2D_XXXX).
                // If no standard diffuse slot was matched, promote the first non-auxiliary texture to diffuse (Dest = 0),
                // EXCEPT on water/refractive/invisible shaders where bump/noise/mask maps must never masquerade as albedo.
                if (!hasExplicitDiffuseTexture && !isWaterShader && !isRefractiveShader && !isInvisibleShader)
                {
                    if (fallbackDiffuseCandidate != null)
                    {
                        fallbackDiffuseCandidate.Dest = 0;
                        hasExplicitDiffuseTexture = true;
                    }
                    else if (activeResolver != null)
                    {
                        // Final fallback: consult injected resolver for SQLite MaterialCache / MeshMaterialResolvedCache main texture
                        // when cross-bundle m_Externals filenames did not resolve via PPtr, strictly rejecting normal/mask/auxiliary maps.
                        var fallbackMainTex = activeResolver(mat, string.Empty, null);
                        if (fallbackMainTex != null
                            && !seenNonDiffuseTextures.Contains(fallbackMainTex)
                            && !IsLikelyNonDiffuseTextureAssetName(fallbackMainTex.m_Name))
                        {
                            var allocatedName = AllocateOrGetConvertedTextureName(fallbackMainTex);
                            if (!seenNonDiffuseTextureNames.Contains(allocatedName))
                            {
                                var fallbackTexEntry = new ImportedMaterialTexture
                                {
                                    Name = allocatedName,
                                    Dest = 0,
                                    Offset = Vector2.Zero,
                                    Scale = new Vector2(1f, 1f)
                                };
                                iMat.Textures.Insert(0, fallbackTexEntry);
                                recordedTextureProperties.TryAdd("_MainTex", allocatedName);
                                ConvertTexture2D(fallbackMainTex, fallbackTexEntry.Name, mat.m_Name, "_MainTex", forceNormalMapUnpack: false);
                                hasExplicitDiffuseTexture = true;
                            }
                        }
                    }
                }

                // Resolve effective render queue, RenderType, blend state, and alpha/surface mode.
                effectiveTags.TryGetValue("RenderType", out var renderTypeTag);
                effectiveTags.TryGetValue("Queue", out var queueTag);
                int? effectiveQueue = customRenderQueue > 0 ? customRenderQueue : TryParseUnityRenderQueueTag(queueTag);

                bool isAdditiveBlend =
                    (urpBlend.HasValue && MathF.Abs(urpBlend.Value - 2f) < 0.25f && (urpSurface ?? 0f) >= 0.5f)
                    || (dstBlend.HasValue && MathF.Abs(dstBlend.Value - 1f) < 0.25f
                        && (!srcBlend.HasValue || MathF.Abs(srcBlend.Value - 1f) < 0.25f || MathF.Abs(srcBlend.Value - 5f) < 0.25f))
                    || shaderInspection.ForwardPassIsAdditive
                    || ContainsShaderToken(shaderName, "additive");

                bool isAlphaBlendFromFloats =
                    dstBlend.HasValue
                    && dstBlend.Value >= 0.5f
                    && !(srcBlend.HasValue && MathF.Abs(srcBlend.Value - 1f) < 0.25f && dstBlend.Value < 0.5f);

                bool isEffectShader = !isInvisibleShader && !isWaterShader && !isRefractiveShader && !isSkyShader
                    && IsEffectShaderName(shaderName);
                bool isUnlitShader = !isInvisibleShader && !isWaterShader && !isRefractiveShader
                    && (IsUnlitShaderName(shaderName)
                        || isSkyShader
                        || isAdditiveBlend
                        || (isEffectShader && !IsExplicitlyLitShaderName(shaderName)));

                bool explicitlyOpaqueOverride =
                    (iMat.SurfaceMode.HasValue && iMat.SurfaceMode.Value < 0.5f && (!effectiveQueue.HasValue || effectiveQueue.Value < 2900))
                    || (urpSurface.HasValue && urpSurface.Value < 0.5f && !alphaClipEnabled && (!effectiveQueue.HasValue || effectiveQueue.Value < 2900));

                if (!iMat.SurfaceMode.HasValue)
                {
                    if (alphaClipEnabled || effectiveKeywords.Contains("_ALPHATEST_ON"))
                    {
                        iMat.SurfaceMode = 1f;
                    }
                    else if ((urpSurface.HasValue && urpSurface.Value >= 0.5f)
                        || effectiveKeywords.Contains("_SURFACE_TYPE_TRANSPARENT")
                        || effectiveKeywords.Contains("_ALPHABLEND_ON")
                        || effectiveKeywords.Contains("_ALPHAPREMULTIPLY_ON")
                        || isAdditiveBlend
                        || isAlphaBlendFromFloats
                        || shaderInspection.ForwardPassIsAlphaBlend)
                    {
                        iMat.SurfaceMode = 3f;
                    }
                    else if (IsTransparentCutoutRenderType(renderTypeTag)
                        || (effectiveQueue.HasValue && effectiveQueue.Value >= 2450 && effectiveQueue.Value < 2900)
                        || ContainsShaderToken(shaderName, "cutout")
                        || ContainsShaderToken(shaderName, "alphatest"))
                    {
                        iMat.SurfaceMode = 1f;
                    }
                    else if (IsTransparentRenderType(renderTypeTag)
                        || (effectiveQueue.HasValue && effectiveQueue.Value >= 2900)
                        || (!explicitlyOpaqueOverride && (isEffectShader || IsTransparentShaderName(shaderName)))
                        || ((zWrite.HasValue && zWrite.Value <= 0.5f || shaderInspection.ForwardPassZWriteOff) && (isEffectShader || isAdditiveBlend)))
                    {
                        iMat.SurfaceMode = 3f;
                    }
                }

                // Apply shader-faithful PBR / extension translation per detected shader class.
                string classification;
                if (isInvisibleShader)
                {
                    classification = "Invisible";
                }
                else if (isWaterShader || isRefractiveShader)
                {
                    classification = isWaterShader ? "Water" : "Refractive";
                    iMat.IsUnlit = false;

                    // Resolve authentic water/glass tint from shader color properties.
                    bool hasShallow = TryGetEffectiveColor(effectiveColors, "_ShallowColor", out var shallowColor)
                        || TryGetEffectiveColor(effectiveColors, "_WaterShallowColor", out shallowColor);
                    bool hasDeep = TryGetEffectiveColor(effectiveColors, "_DeepColor", out var deepColor)
                        || TryGetEffectiveColor(effectiveColors, "_WaterDeepColor", out deepColor);

                    if (hasShallow && hasDeep)
                    {
                        iMat.Diffuse = new Color(
                            deepColor.R * 0.55f + shallowColor.R * 0.45f,
                            deepColor.G * 0.55f + shallowColor.G * 0.45f,
                            deepColor.B * 0.55f + shallowColor.B * 0.45f,
                            Math.Clamp(deepColor.A * 0.55f + shallowColor.A * 0.45f, 0f, 1f));
                    }
                    else if (TryGetFirstEffectiveColor(effectiveColors, out var waterColor,
                        "_WaterColor", "_BaseWaterColor", "_SurfaceColor", "_RefractionColor",
                        "_AbsorptionColor", "_ShallowColor", "_DeepColor", "_TintColor", "_Tint", "_MainColor"))
                    {
                        if (!hasExplicitBaseColor)
                        {
                            iMat.Diffuse = waterColor;
                        }
                    }
                    else if (!hasExplicitBaseColor)
                    {
                        iMat.Diffuse = isWaterShader
                            ? new Color(0.14f, 0.42f, 0.52f, 1.0f)
                            : new Color(0.90f, 0.96f, 0.98f, 1.0f);
                    }

                    if (!iMat.Roughness.HasValue && !iMat.Smoothness.HasValue)
                    {
                        iMat.Roughness = isWaterShader ? 0.08f : 0.05f;
                    }
                    if (!iMat.Metallic.HasValue)
                    {
                        iMat.Metallic = 0f;
                    }

                    float ior = explicitIor.HasValue && float.IsFinite(explicitIor.Value)
                        ? Math.Clamp(explicitIor.Value, 1.0f, 3.0f)
                        : (isWaterShader ? 1.333f : 1.5f);
                    iMat.Ior = ior;

                    float baseTransmission = isWaterShader ? 0.78f : 0.85f;
                    if (explicitOpacity.HasValue && float.IsFinite(explicitOpacity.Value) && explicitOpacity.Value >= 0f && explicitOpacity.Value <= 1f)
                    {
                        baseTransmission = Math.Clamp(1f - explicitOpacity.Value * 0.65f, 0.25f, 0.92f);
                    }

                    bool isExplicitAlphaTranslucent = iMat.Diffuse.A < 0.95f && iMat.SurfaceMode.HasValue && iMat.SurfaceMode.Value >= 2f;
                    if (isExplicitAlphaTranslucent)
                    {
                        iMat.TransmissionFactor = Math.Min(baseTransmission, 0.55f);
                    }
                    else
                    {
                        iMat.TransmissionFactor = baseTransmission;
                        // Standard glTF 2.0 KHR_materials_transmission uses OPAQUE alphaMode when base alpha is 1.0.
                        if (!iMat.SurfaceMode.HasValue || iMat.SurfaceMode.Value >= 2f)
                        {
                            iMat.SurfaceMode = 0f;
                        }
                    }
                }
                else if (isSkyShader)
                {
                    classification = "Sky";
                    iMat.IsUnlit = true;
                    if (!hasExplicitBaseColor
                        && TryGetFirstEffectiveColor(effectiveColors, out var skyColor,
                            "_SkyTint", "_SkyColor", "_Tint", "_TintColor", "_TopColor", "_ZenithColor", "_HorizonColor"))
                    {
                        iMat.Diffuse = skyColor;
                    }
                    if (!iMat.SurfaceMode.HasValue)
                    {
                        iMat.SurfaceMode = 0f;
                    }
                }
                else if (isUnlitShader || isAdditiveBlend || isEffectShader)
                {
                    classification = isAdditiveBlend ? "Additive" : (isEffectShader ? "Effect" : "Unlit");
                    iMat.IsUnlit = isUnlitShader;

                    if (!hasExplicitBaseColor
                        && TryGetFirstEffectiveColor(effectiveColors, out var fxColor,
                            "_TintColor", "_Tint", "_MainColor", "_EffectColor", "_GlowColor", "_FogColor", "_CloudColor", "_EmissionColor"))
                    {
                        iMat.Diffuse = fxColor;
                    }

                    if (explicitOpacity.HasValue && float.IsFinite(explicitOpacity.Value) && explicitOpacity.Value >= 0f && explicitOpacity.Value < 1f)
                    {
                        iMat.Diffuse = new Color(iMat.Diffuse.R, iMat.Diffuse.G, iMat.Diffuse.B, Math.Clamp(iMat.Diffuse.A * explicitOpacity.Value, 0f, 1f));
                    }

                    if (isAdditiveBlend || (isEffectShader && !explicitlyOpaqueOverride))
                    {
                        if (!iMat.SurfaceMode.HasValue || iMat.SurfaceMode.Value < 0.5f)
                        {
                            iMat.SurfaceMode = 3f;
                        }

                        // Untextured additive/fog/effect planes with alpha=1 would render as opaque slabs in glTF viewers;
                        // apply a soft translucent alpha fallback so they remain visually inspectable without obscuring the scene.
                        if (!hasExplicitDiffuseTexture && iMat.Diffuse.A >= 0.95f && iMat.Transparency <= 1e-3f)
                        {
                            iMat.Diffuse = new Color(iMat.Diffuse.R, iMat.Diffuse.G, iMat.Diffuse.B, 0.35f);
                        }
                    }
                }
                else
                {
                    if (!hasExplicitBaseColor
                        && TryGetFirstEffectiveColor(effectiveColors, out var fallbackColor, "_MainColor", "_TintColor", "_Tint"))
                    {
                        iMat.Diffuse = fallbackColor;
                    }

                    if (explicitOpacity.HasValue && float.IsFinite(explicitOpacity.Value) && explicitOpacity.Value >= 0f && explicitOpacity.Value < 1f)
                    {
                        iMat.Diffuse = new Color(iMat.Diffuse.R, iMat.Diffuse.G, iMat.Diffuse.B, Math.Clamp(iMat.Diffuse.A * explicitOpacity.Value, 0f, 1f));
                    }

                    classification = iMat.SurfaceMode.HasValue && (int)MathF.Round(iMat.SurfaceMode.Value) == 1
                        ? "Cutout"
                        : ((iMat.SurfaceMode.HasValue && iMat.SurfaceMode.Value >= 2f) || iMat.Transparency > 1e-3f || iMat.Diffuse.A < 0.999f
                            ? "Transparent"
                            : "Standard");
                }

                iMat.UnityShaderMetadata = BuildUnityShaderMetadata(
                    shaderName,
                    classification,
                    effectiveQueue,
                    renderTypeTag,
                    effectiveKeywords,
                    effectiveFloats,
                    effectiveColors,
                    recordedTextureProperties,
                    effectiveTags);

                MaterialList.Add(iMat);
            }
            else
            {
                iMat = ImportedHelpers.FindMaterial("Material", MaterialList);
                if (iMat == null)
                {
                    iMat = new ImportedMaterial();
                    iMat.Name = "Material";
                    iMat.Diffuse = new Color(0.8f, 0.8f, 0.8f, 1);
                    iMat.Ambient = new Color(0.2f, 0.2f, 0.2f, 1);
                    iMat.Emissive = new Color(0, 0, 0, 1);
                    iMat.Specular = new Color(0.2f, 0.2f, 0.2f, 1);
                    iMat.Reflection = new Color(0, 0, 0, 1);
                    iMat.Shininess = 20f;
                    iMat.Transparency = 0f;
                    iMat.Textures = new List<ImportedMaterialTexture>();
                    MaterialList.Add(iMat);
                }
            }
            return iMat;
        }

        private sealed class ParsedShaderInspection
        {
            public Dictionary<string, string> SubShaderTags { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool WritesNoColor { get; set; }
            public bool ForwardPassIsAdditive { get; set; }
            public bool ForwardPassIsAlphaBlend { get; set; }
            public bool ForwardPassCullOff { get; set; }
            public bool ForwardPassZWriteOff { get; set; }
        }

        private static Dictionary<string, Color> CollectEffectiveMaterialColors(Material mat)
        {
            var result = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visited.Add(current))
            {
                var colors = current.m_SavedProperties?.m_Colors;
                if (colors != null)
                {
                    for (int i = 0; i < colors.Length; i++)
                    {
                        var kv = colors[i];
                        if (!string.IsNullOrEmpty(kv.Key) && !result.ContainsKey(kv.Key))
                        {
                            result[kv.Key] = kv.Value;
                        }
                    }
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private static Dictionary<string, float> CollectEffectiveMaterialFloatsAndInts(Material mat)
        {
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visited.Add(current))
            {
                var floats = current.m_SavedProperties?.m_Floats;
                if (floats != null)
                {
                    for (int i = 0; i < floats.Length; i++)
                    {
                        var kv = floats[i];
                        if (!string.IsNullOrEmpty(kv.Key) && !result.ContainsKey(kv.Key))
                        {
                            result[kv.Key] = kv.Value;
                        }
                    }
                }

                var ints = current.m_SavedProperties?.m_Ints;
                if (ints != null)
                {
                    for (int i = 0; i < ints.Length; i++)
                    {
                        var kv = ints[i];
                        if (!string.IsNullOrEmpty(kv.Key) && !result.ContainsKey(kv.Key))
                        {
                            result[kv.Key] = kv.Value;
                        }
                    }
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private static HashSet<string> CollectEffectiveMaterialKeywords(Material mat)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visited.Add(current))
            {
                if (current.m_ValidKeywords != null)
                {
                    for (int i = 0; i < current.m_ValidKeywords.Length; i++)
                    {
                        var kw = current.m_ValidKeywords[i];
                        if (!string.IsNullOrWhiteSpace(kw))
                        {
                            result.Add(kw.Trim());
                        }
                    }
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private static Dictionary<string, string> CollectEffectiveMaterialTags(Material mat)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visited.Add(current))
            {
                if (current.stringTagMap != null)
                {
                    for (int i = 0; i < current.stringTagMap.Length; i++)
                    {
                        var kv = current.stringTagMap[i];
                        if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value) && !result.ContainsKey(kv.Key))
                        {
                            result[kv.Key] = kv.Value;
                        }
                    }
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private static int ResolveEffectiveRenderQueue(Material mat)
        {
            var visited = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visited.Add(current))
            {
                if (current.m_CustomRenderQueue > 0)
                {
                    return current.m_CustomRenderQueue;
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return 0;
        }

        private static (Shader? Shader, string ShaderName) ResolveMaterialShaderInfo(Material mat)
        {
            var visited = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visited.Add(current))
            {
                var shaderRef = current.m_Shader;
                if (shaderRef != null && !shaderRef.IsNull)
                {
                    if (shaderRef.TryGet(out var shader) && shader != null)
                    {
                        var name = !string.IsNullOrWhiteSpace(shader.m_ParsedForm?.m_Name)
                            ? shader.m_ParsedForm.m_Name
                            : (shader.m_Name ?? string.Empty);
                        return (shader, name);
                    }

                    if (TryFindShaderInMaterialScope(current, shaderRef, out var externalShader, out var indexedName))
                    {
                        var name = externalShader != null
                            ? (!string.IsNullOrWhiteSpace(externalShader.m_ParsedForm?.m_Name) ? externalShader.m_ParsedForm.m_Name : (externalShader.m_Name ?? string.Empty))
                            : (indexedName ?? string.Empty);
                        return (externalShader, name);
                    }
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return (null, string.Empty);
        }

        private static bool TryFindShaderInMaterialScope(
            Material mat,
            PPtr<Shader> shaderRef,
            out Shader? foundShader,
            out string? indexedShaderName)
        {
            foundShader = null;
            indexedShaderName = null;
            var ownerFile = mat.assetsFile;
            if (ownerFile == null || shaderRef == null || shaderRef.IsNull)
            {
                return false;
            }

            if (shaderRef.TryGetAssetsFile(out var targetFile) && targetFile != null)
            {
                if (targetFile.TryGetObject(shaderRef.m_PathID, out var obj) && obj is Shader sh)
                {
                    foundShader = sh;
                    return true;
                }

                var directHandle = ownerFile.assetsManager?.ProjectIndex?.GetHandle(AssetHandle.BuildUniqueID(targetFile, shaderRef.m_PathID));
                if (directHandle != null && !string.IsNullOrWhiteSpace(directHandle.Name))
                {
                    indexedShaderName = directHandle.Name;
                    return true;
                }
            }

            if (shaderRef.m_FileID == 0)
            {
                if (ownerFile.TryGetObject(shaderRef.m_PathID, out var localObj) && localObj is Shader localSh)
                {
                    foundShader = localSh;
                    return true;
                }

                var localHandle = ownerFile.assetsManager?.ProjectIndex?.GetHandle(AssetHandle.BuildUniqueID(ownerFile, shaderRef.m_PathID));
                if (localHandle != null && !string.IsNullOrWhiteSpace(localHandle.Name))
                {
                    indexedShaderName = localHandle.Name;
                    return true;
                }
                return false;
            }

            if (shaderRef.m_FileID > 0
                && ownerFile.m_Externals != null
                && shaderRef.m_FileID - 1 < ownerFile.m_Externals.Count)
            {
                var external = ownerFile.m_Externals[shaderRef.m_FileID - 1];
                var files = SnapshotAssetsFiles(ownerFile.assetsManager);
                for (int i = 0; i < files.Length; i++)
                {
                    var sf = files[i];
                    if (sf == null || !IsMatchingExternalFile(sf, external))
                    {
                        continue;
                    }

                    if (sf.TryGetObject(shaderRef.m_PathID, out var extObj) && extObj is Shader extSh)
                    {
                        foundShader = extSh;
                        return true;
                    }

                    var extHandle = ownerFile.assetsManager?.ProjectIndex?.GetHandle(AssetHandle.BuildUniqueID(sf, shaderRef.m_PathID));
                    if (extHandle != null && !string.IsNullOrWhiteSpace(extHandle.Name))
                    {
                        indexedShaderName = extHandle.Name;
                        return true;
                    }
                }

                var extLeaf = Path.GetFileName(!string.IsNullOrWhiteSpace(external.fileName) ? external.fileName : external.pathName);
                if (!string.IsNullOrWhiteSpace(extLeaf) && ownerFile.assetsManager?.ProjectIndex != null)
                {
                    foreach (var h in ownerFile.assetsManager.ProjectIndex.GetHandlesForFile(extLeaf))
                    {
                        if (h != null && h.PathID == shaderRef.m_PathID && h.Type == ClassIDType.Shader && !string.IsNullOrWhiteSpace(h.Name))
                        {
                            indexedShaderName = h.Name;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static ParsedShaderInspection InspectParsedShader(Shader? shader)
        {
            var info = new ParsedShaderInspection();
            var parsed = shader?.m_ParsedForm;
            if (parsed?.m_SubShaders == null || parsed.m_SubShaders.Length == 0)
            {
                return info;
            }

            var sub = parsed.m_SubShaders[0];
            if (sub == null)
            {
                return info;
            }

            if (sub.m_Tags?.tags != null)
            {
                for (int i = 0; i < sub.m_Tags.tags.Length; i++)
                {
                    var kv = sub.m_Tags.tags[i];
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        info.SubShaderTags[kv.Key] = kv.Value;
                    }
                }
            }

            if (sub.m_Passes == null || sub.m_Passes.Length == 0)
            {
                return info;
            }

            int totalNormalPasses = 0;
            int colorPassCount = 0;
            int colorPassWithColorWriteCount = 0;

            for (int p = 0; p < sub.m_Passes.Length; p++)
            {
                var pass = sub.m_Passes[p];
                if (pass == null || pass.m_Type != PassType.Normal)
                {
                    continue;
                }

                totalNormalPasses++;
                if (IsAuxiliaryNonColorPass(pass))
                {
                    continue;
                }

                colorPassCount++;
                var state = pass.m_State;
                if (state == null)
                {
                    colorPassWithColorWriteCount++;
                    continue;
                }

                bool passWritesColor = true;
                if (state.rtBlend != null && state.rtBlend.Length > 0 && state.rtBlend[0] != null)
                {
                    var blend0 = state.rtBlend[0];
                    if (blend0.colMask != null && string.IsNullOrEmpty(blend0.colMask.name) && blend0.colMask.val <= 0.5f)
                    {
                        passWritesColor = false;
                    }

                    if (passWritesColor && blend0.destBlend != null && string.IsNullOrEmpty(blend0.destBlend.name) && blend0.destBlend.val >= 0.5f)
                    {
                        float srcVal = blend0.srcBlend != null && string.IsNullOrEmpty(blend0.srcBlend.name) ? blend0.srcBlend.val : 5f;
                        float dstVal = blend0.destBlend.val;
                        if (MathF.Abs(dstVal - 1f) < 0.25f && (MathF.Abs(srcVal - 1f) < 0.25f || MathF.Abs(srcVal - 5f) < 0.25f))
                        {
                            info.ForwardPassIsAdditive = true;
                        }
                        else
                        {
                            info.ForwardPassIsAlphaBlend = true;
                        }
                    }
                }

                if (passWritesColor)
                {
                    colorPassWithColorWriteCount++;
                    if (state.culling != null && string.IsNullOrEmpty(state.culling.name) && state.culling.val <= 0.5f)
                    {
                        info.ForwardPassCullOff = true;
                    }
                    if (state.zWrite != null && string.IsNullOrEmpty(state.zWrite.name) && state.zWrite.val <= 0.5f)
                    {
                        info.ForwardPassZWriteOff = true;
                    }
                }
            }

            if (totalNormalPasses > 0 && (colorPassCount == 0 || colorPassWithColorWriteCount == 0))
            {
                info.WritesNoColor = true;
            }

            return info;
        }

        private static bool IsAuxiliaryNonColorPass(SerializedPass pass)
        {
            var passName = pass.m_Name ?? string.Empty;
            if (passName.Equals("ShadowCaster", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("DepthOnly", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("DepthNormals", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("DepthNormalsOnly", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("Meta", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("MotionVectors", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("SceneSelectionPass", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("ScenePickingPass", StringComparison.OrdinalIgnoreCase)
                || passName.Equals("Universal2D", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pass.m_Tags?.tags != null)
            {
                for (int i = 0; i < pass.m_Tags.tags.Length; i++)
                {
                    var kv = pass.m_Tags.tags[i];
                    if (string.Equals(kv.Key, "LightMode", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(kv.Value, "ShadowCaster", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "DepthOnly", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "DepthNormals", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "DepthNormalsOnly", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "Meta", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "MotionVectors", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "Picking", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Value, "SceneSelectionPass", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetEffectiveColor(Dictionary<string, Color> colors, string key, out Color value)
            => colors.TryGetValue(key, out value);

        private static bool TryGetFirstEffectiveColor(Dictionary<string, Color> colors, out Color value, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (colors.TryGetValue(keys[i], out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static int? TryParseUnityRenderQueueTag(string? queueTag)
        {
            if (string.IsNullOrWhiteSpace(queueTag))
            {
                return null;
            }

            var trimmed = queueTag.Trim();
            if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int direct))
            {
                return direct;
            }

            int signIdx = trimmed.IndexOfAny(new[] { '+', '-' });
            string baseName = signIdx >= 0 ? trimmed.Substring(0, signIdx).Trim() : trimmed;
            int offset = 0;
            if (signIdx >= 0 && !int.TryParse(trimmed.Substring(signIdx).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out offset))
            {
                offset = 0;
            }

            int? baseVal = baseName.ToLowerInvariant() switch
            {
                "background" => 1000,
                "geometry" => 2000,
                "alphatest" => 2450,
                "transparent" => 3000,
                "overlay" => 4000,
                _ => null
            };

            return baseVal.HasValue ? baseVal.Value + offset : null;
        }

        private static bool IsTransparentRenderType(string? renderType)
        {
            if (string.IsNullOrWhiteSpace(renderType))
            {
                return false;
            }

            return renderType.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
                || renderType.Equals("Fade", StringComparison.OrdinalIgnoreCase)
                || renderType.Equals("Overlay", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransparentCutoutRenderType(string? renderType)
        {
            if (string.IsNullOrWhiteSpace(renderType))
            {
                return false;
            }

            return renderType.Equals("TransparentCutout", StringComparison.OrdinalIgnoreCase)
                || renderType.Equals("AlphaTest", StringComparison.OrdinalIgnoreCase)
                || renderType.Equals("TreeTransparentCutout", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsShaderToken(string? shaderName, string token)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            var tokens = SplitIdentifierTokens(shaderName);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsAnyShaderToken(string? shaderName, params string[] candidates)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            var tokens = SplitIdentifierTokens(shaderName);
            for (int i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                for (int c = 0; c < candidates.Length; c++)
                {
                    if (string.Equals(tok, candidates[c], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsWaterShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "water", "ocean", "river", "lake", "sea", "pond", "pool", "aquatic", "liquid", "waterfall", "puddle");

        private static bool IsRefractiveOrGlassShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "glass", "refract", "refraction", "refractive", "crystal", "transmissive", "transmission", "ice");

        private static bool IsSkyShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "sky", "skybox", "skydome", "atmosphere", "starfield", "celestial");

        private static bool IsUnlitShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "unlit", "sprites", "sprite", "ui", "gui", "textmeshpro", "tmp", "font");

        private static bool IsExplicitlyLitShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "lit", "pbr", "standard", "principled", "metallic", "specular");

        private static bool IsEffectShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "particles", "particle", "vfx", "fx", "effect", "effects", "fog", "cloud", "clouds", "flare", "halo", "beam", "laser", "trail", "glow", "decal", "decals", "billboard", "mist", "smoke", "fire", "aura", "additive", "multiply");

        private static bool IsTransparentShaderName(string? shaderName)
            => ContainsAnyShaderToken(shaderName, "transparent", "fade", "alphablend", "translucent", "blended");

        private static bool IsInvisibleShaderName(string? shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            return shaderName.IndexOf("ShadowOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || shaderName.IndexOf("ShadowsOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || shaderName.IndexOf("DepthOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || shaderName.IndexOf("DepthMask", StringComparison.OrdinalIgnoreCase) >= 0
                || shaderName.IndexOf("MaskOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || shaderName.IndexOf("NoRender", StringComparison.OrdinalIgnoreCase) >= 0
                || ContainsAnyShaderToken(shaderName, "invisible", "occluder", "occlusiononly");
        }

        private static bool HasWaterSpecificMaterialProperties(
            Dictionary<string, Color> colors,
            Dictionary<string, float> floats,
            List<(Material Owner, KeyValuePair<string, UnityTexEnv> Entry)> texEnvs)
        {
            if (colors.ContainsKey("_ShallowColor")
                || colors.ContainsKey("_DeepColor")
                || colors.ContainsKey("_WaterColor")
                || colors.ContainsKey("_BaseWaterColor")
                || colors.ContainsKey("_FoamColor")
                || colors.ContainsKey("_WaterShallowColor")
                || colors.ContainsKey("_WaterDeepColor"))
            {
                return true;
            }

            foreach (var key in floats.Keys)
            {
                if (key.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Ripple", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Foam", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Caustic", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            for (int i = 0; i < texEnvs.Count; i++)
            {
                var key = texEnvs[i].Entry.Key;
                if (key.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Ripple", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Foam", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("Caustic", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRefractionSpecificMaterialProperties(
            Dictionary<string, float> floats,
            Dictionary<string, Color> colors)
        {
            if (colors.ContainsKey("_RefractionColor") || colors.ContainsKey("_GlassColor"))
            {
                return true;
            }

            foreach (var key in floats.Keys)
            {
                if (key.Equals("_IOR", StringComparison.OrdinalIgnoreCase)
                    || key.IndexOf("Refract", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("IndexOfRefraction", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static ImportedUnityShaderMetadata BuildUnityShaderMetadata(
            string shaderName,
            string classification,
            int? renderQueue,
            string? renderType,
            HashSet<string> keywords,
            Dictionary<string, float> floats,
            Dictionary<string, Color> colors,
            Dictionary<string, string> textures,
            Dictionary<string, string> tags)
        {
            var meta = new ImportedUnityShaderMetadata
            {
                ShaderName = shaderName ?? string.Empty,
                Classification = classification ?? string.Empty,
                RenderQueue = renderQueue,
                RenderType = string.IsNullOrWhiteSpace(renderType) ? null : renderType
            };

            if (keywords.Count > 0)
            {
                meta.Keywords.AddRange(keywords.OrderBy(static k => k, StringComparer.Ordinal));
            }

            foreach (var kv in floats.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                if (!float.IsFinite(kv.Value))
                {
                    continue;
                }

                if (IsKeyUnityShaderFloatProperty(kv.Key))
                {
                    meta.FloatProperties[kv.Key] = kv.Value;
                }
            }

            foreach (var kv in colors.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                if (!IsKeyUnityShaderColorProperty(kv.Key))
                {
                    continue;
                }

                var c = kv.Value;
                if (float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && float.IsFinite(c.A))
                {
                    meta.ColorProperties[kv.Key] = new[] { c.R, c.G, c.B, c.A };
                }
            }

            foreach (var kv in textures.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    meta.TextureProperties[kv.Key] = kv.Value;
                }
            }

            foreach (var kv in tags.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    meta.Tags[kv.Key] = kv.Value;
                }
            }

            return meta;
        }

        private static bool IsKeyUnityShaderFloatProperty(string propName)
        {
            return propName.Equals("_Surface", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Blend", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Mode", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_SrcBlend", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_DstBlend", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_SrcBlendAlpha", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_DstBlendAlpha", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_ZWrite", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Cull", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_AlphaClip", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Cutoff", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_AlphaCutoff", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Metallic", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Smoothness", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Glossiness", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Roughness", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_BumpScale", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_NormalScale", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_OcclusionStrength", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_IOR", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Refraction", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Opacity", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Alpha", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Transparency", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Fade", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKeyUnityShaderColorProperty(string propName)
        {
            return propName.Equals("_BaseColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Color", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_MainColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_TintColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_Tint", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_ShallowColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_DeepColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_WaterColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_FoamColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_SkyTint", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_SkyColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_EmissionColor", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("_SpecColor", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIntentionalUntexturedVisualClassification(string? classification)
        {
            if (string.IsNullOrEmpty(classification))
            {
                return false;
            }

            return classification.Equals("Water", StringComparison.Ordinal)
                || classification.Equals("Refractive", StringComparison.Ordinal)
                || classification.Equals("Sky", StringComparison.Ordinal)
                || classification.Equals("Effect", StringComparison.Ordinal)
                || classification.Equals("Additive", StringComparison.Ordinal)
                || classification.Equals("Unlit", StringComparison.Ordinal)
                || classification.Equals("Transparent", StringComparison.Ordinal);
        }

        private void EvaluateAndApplySceneRendererVisibility(
            Renderer renderer,
            GameObject? gameObject,
            ImportedFrame targetFrame,
            List<Material?> resolvedUnityMaterials,
            List<ImportedMaterial> convertedMaterials)
        {
            if (TryClassifyHiddenAuxiliaryRenderer(renderer, gameObject, targetFrame, resolvedUnityMaterials, convertedMaterials, out var reason))
            {
                if (!_framesWithVisibleRenderer.Contains(targetFrame))
                {
                    targetFrame.IsInitiallyHidden = true;
                    targetFrame.HiddenReason ??= reason;
                    if (_framesMarkedHidden.Add(targetFrame))
                    {
                        HiddenAuxiliaryRendererCount++;
                    }
                }
            }
            else
            {
                _framesWithVisibleRenderer.Add(targetFrame);
                if (_framesMarkedHidden.Remove(targetFrame))
                {
                    HiddenAuxiliaryRendererCount = Math.Max(0, HiddenAuxiliaryRendererCount - 1);
                    targetFrame.IsInitiallyHidden = false;
                    targetFrame.HiddenReason = null;
                }
            }
        }

        private static bool TryClassifyHiddenAuxiliaryRenderer(
            Renderer renderer,
            GameObject? gameObject,
            ImportedFrame targetFrame,
            List<Material?> resolvedUnityMaterials,
            List<ImportedMaterial> convertedMaterials,
            out string reason)
        {
            // 1. Unity ShadowCastingMode.ShadowsOnly (3): never rendered to the color buffer.
            if (renderer.m_CastShadows == Renderer.ShadowCastingModeShadowsOnly)
            {
                reason = "ShadowsOnly";
                return true;
            }

            // 2. Explicitly invisible / non-color-writing shader (ColorMask 0 / ShadowOnly / DepthOnly / Occluder).
            if (convertedMaterials.Count > 0
                && convertedMaterials.All(static m => string.Equals(m.UnityShaderMetadata?.Classification, "Invisible", StringComparison.Ordinal)))
            {
                reason = "InvisibleShader";
                return true;
            }

            int nonNullMaterialSlots = 0;
            if (renderer.m_Materials != null)
            {
                for (int i = 0; i < renderer.m_Materials.Length; i++)
                {
                    var mp = renderer.m_Materials[i];
                    if (mp != null && !mp.IsNull)
                    {
                        nonNullMaterialSlots++;
                    }
                }
            }

            int resolvedCount = 0;
            for (int i = 0; i < resolvedUnityMaterials.Count; i++)
            {
                if (resolvedUnityMaterials[i] != null)
                {
                    resolvedCount++;
                }
            }

            // If external materials simply failed to resolve (non-null PPtrs with 0 resolved Material objects),
            // do NOT mistakenly classify the renderer as an untextured proxy.
            bool hasExplicitEmptyMaterials = nonNullMaterialSlots == 0 && resolvedCount == 0;
            bool allResolvedUntexturedStandard = resolvedCount > 0
                && convertedMaterials.Count > 0
                && convertedMaterials.All(static m =>
                    m.Textures.Count == 0
                    && !IsIntentionalUntexturedVisualClassification(m.UnityShaderMetadata?.Classification));

            if (!hasExplicitEmptyMaterials && !allResolvedUntexturedStandard)
            {
                reason = string.Empty;
                return false;
            }

            bool hasCollider = GameObjectHasColliderComponent(gameObject);
            string framePath = targetFrame.Path;
            string? goName = gameObject?.m_Name;

            // 3. Untextured collider proxy with castShadows=Off on non-default layer or auxiliary hierarchy.
            if (hasCollider
                && renderer.m_CastShadows == Renderer.ShadowCastingModeOff
                && ((gameObject != null && gameObject.m_Layer != 0)
                    || MatchesAuxiliaryProxyNameOrPath(framePath, goName)))
            {
                reason = "ColliderProxy";
                return true;
            }

            // 4. Untextured non-shadow-casting / collider-backed structural proxy (AirWall, Collision, NavMesh, Trigger, Occluder).
            if ((hasCollider || renderer.m_CastShadows == Renderer.ShadowCastingModeOff)
                && MatchesAuxiliaryProxyNameOrPath(framePath, goName))
            {
                reason = hasCollider ? "ColliderProxy" : "AuxiliaryProxy";
                return true;
            }

            // 5. Unambiguous non-rendered structural proxy names even if castShadows was left at default on a culled layer.
            if (MatchesUnambiguousNonRenderedProxyNameOrPath(framePath, goName))
            {
                reason = "AuxiliaryProxy";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private static bool GameObjectHasColliderComponent(GameObject? go)
        {
            if (go?.m_Components == null || go.m_Components.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < go.m_Components.Length; i++)
            {
                var compPtr = go.m_Components[i];
                if (compPtr == null || compPtr.IsNull)
                {
                    continue;
                }

                var classType = ResolveComponentClassTypeQuick(go, compPtr);
                if (IsColliderClassType(classType))
                {
                    return true;
                }
            }

            return false;
        }

        private static ClassIDType ResolveComponentClassTypeQuick(GameObject owner, PPtr<Component> compPtr)
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
                var fileName = ResolvePPtrFileName(owner.assetsFile, compPtr.m_FileID);
                if (!string.IsNullOrEmpty(fileName)
                    && manager.ProjectIndex.TryGetIndexByUniqueId($"{fileName}#{compPtr.m_PathID}", out var idx))
                {
                    return manager.ProjectIndex.Arena.GetType(idx);
                }
            }

            return ClassIDType.UnknownType;
        }

        private static bool IsColliderClassType(ClassIDType type)
        {
            return type == ClassIDType.Collider
                || type == ClassIDType.Collider2D
                || type == ClassIDType.BoxCollider
                || type == ClassIDType.MeshCollider
                || type == ClassIDType.SphereCollider
                || type == ClassIDType.CapsuleCollider
                || type == ClassIDType.TerrainCollider
                || type == ClassIDType.WheelCollider
                || type == ClassIDType.RaycastCollider
                || type == ClassIDType.BoxCollider2D
                || type == ClassIDType.CircleCollider2D
                || type == ClassIDType.PolygonCollider2D
                || type == ClassIDType.EdgeCollider2D
                || type == ClassIDType.CapsuleCollider2D
                || type == ClassIDType.CompositeCollider2D
                || type == ClassIDType.TilemapCollider2D;
        }

        private static bool MatchesUnambiguousNonRenderedProxyNameOrPath(string? framePath, string? goName)
        {
            return ContainsAnyProxyPattern(framePath, unambiguousOnly: true)
                || ContainsAnyProxyPattern(goName, unambiguousOnly: true);
        }

        private static bool MatchesAuxiliaryProxyNameOrPath(string? framePath, string? goName)
        {
            return ContainsAnyProxyPattern(framePath, unambiguousOnly: false)
                || ContainsAnyProxyPattern(goName, unambiguousOnly: false);
        }

        private static bool ContainsAnyProxyPattern(string? text, bool unambiguousOnly)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Check compound substrings across segments (case-insensitive, engine-generic).
            if (text.IndexOf("AirWall", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("InvisibleWall", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("NavMesh", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("NaviMesh", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Occluder", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("ShadowOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("ShadowsOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("ShadowProxy", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("SdwOnly", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Sdw_Only", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("PlayerClip", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("ClipWall", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("BlockWall", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("CameraBlock", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("HitBox", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("HurtBox", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("KillVolume", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (unambiguousOnly)
            {
                return false;
            }

            if (text.IndexOf("Collision", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Collider", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("NavArea", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("BlockVolume", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("BoundsProxy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static List<string> SplitIdentifierTokens(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }

            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                bool isAlnum = char.IsLetterOrDigit(ch);
                if (!isAlnum)
                {
                    if (start >= 0)
                    {
                        tokens.Add(text.Substring(start, i - start));
                        start = -1;
                    }
                    continue;
                }

                if (start < 0)
                {
                    start = i;
                }
                else if (char.IsUpper(ch)
                    && i > start
                    && (char.IsLower(text[i - 1])
                        || (char.IsUpper(text[i - 1]) && i + 1 < text.Length && char.IsLower(text[i + 1]))))
                {
                    tokens.Add(text.Substring(start, i - start));
                    start = i;
                }
            }

            if (start >= 0)
            {
                tokens.Add(text.Substring(start));
            }

            return tokens;
        }

        public static bool IsLikelyNormalMapTextureAssetName(string? textureName)
        {
            if (string.IsNullOrWhiteSpace(textureName))
            {
                return false;
            }

            var tokens = SplitIdentifierTokens(textureName);
            for (int i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                if (tok.Equals("normal", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("normals", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("normalmap", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("nrm", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("norm", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("bump", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("bumpmap", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("ddn", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("dudv", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Single/two-letter suffixes like Foo_N, Foo_NM only when at end of token list (or followed only by digits).
                if ((tok.Equals("n", StringComparison.OrdinalIgnoreCase) || tok.Equals("nm", StringComparison.OrdinalIgnoreCase))
                    && i > 0
                    && (i == tokens.Count - 1 || (i == tokens.Count - 2 && tokens[i + 1].All(char.IsDigit))))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLikelyNonDiffuseTextureAssetName(string? textureName)
        {
            if (IsLikelyNormalMapTextureAssetName(textureName))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(textureName))
            {
                return false;
            }

            var tokens = SplitIdentifierTokens(textureName);
            for (int i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                if (tok.Equals("mask", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("msk", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("orm", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("arm", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("rma", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("mra", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("mrao", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("occlusion", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("occ", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("ao", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("rough", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("roughness", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("smoothness", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("metallic", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("metalness", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("height", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("parallax", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("displacement", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("noise", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("flowmap", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("distort", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("distortion", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("ramp", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("lut", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("matcap", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("sdf", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("caustic", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("caustics", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("lightmap", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("shadowmask", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("emissivemask", StringComparison.OrdinalIgnoreCase)
                    || tok.Equals("emissionmask", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<(Material Owner, KeyValuePair<string, UnityTexEnv> Entry)> CollectEffectiveMaterialTexEnvs(Material mat)
        {
            var result = new List<(Material Owner, KeyValuePair<string, UnityTexEnv> Entry)>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedMats = new HashSet<Material>();
            Material? current = mat;
            int depth = 0;

            while (current != null && depth++ < 8 && visitedMats.Add(current))
            {
                var texEnvs = current.m_SavedProperties?.m_TexEnvs;
                if (texEnvs != null)
                {
                    for (int i = 0; i < texEnvs.Length; i++)
                    {
                        var kv = texEnvs[i];
                        if (kv.Value?.m_Texture == null || kv.Value.m_Texture.IsNull)
                        {
                            continue;
                        }
                        if (seenKeys.Add(kv.Key))
                        {
                            result.Add((current, kv));
                        }
                    }
                }

                if (current.m_Parent != null && !current.m_Parent.IsNull && current.m_Parent.TryGet(out var parentMat))
                {
                    current = parentMat;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private bool IsTextureNameAllocated(string name)
        {
            return pendingTextureNames.Contains(name) || ImportedHelpers.FindTexture(name, TextureList) != null;
        }

        private void ConvertTexture2D(Texture2D m_Texture2D, string name, string materialName, string propertyName, bool forceNormalMapUnpack = false)
        {
            if (IsTextureNameAllocated(name))
            {
                return;
            }

            pendingTextureNames.Add(name);
            pendingTextures.Add((m_Texture2D, name, materialName, propertyName, forceNormalMapUnpack));
        }

        private void FlushPendingTextures()
        {
            if (pendingTextures.Count == 0)
            {
                return;
            }

            // Step 1 (sequential, thread-safe on shared asset/resS streams): decode each Texture2D into an independent SKBitmap.
            var decoded = new List<(string Name, string MaterialName, string PropertyName, SkiaSharp.SKBitmap Bitmap)>(pendingTextures.Count);
            for (int i = 0; i < pendingTextures.Count; i++)
            {
                var item = pendingTextures[i];
                SkiaSharp.SKBitmap? bitmap;
                try
                {
                    bitmap = item.Texture.ConvertToImage(flip: true, unpackNormalMap: true, forceNormalMapUnpack: item.ForceNormalMapUnpack);
                }
                catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
                {
                    Logger.Warning($"Unable to decode texture {item.Texture.m_Name} for material {item.MaterialName}, property {item.PropertyName}: native texture decoder unavailable ({ex.GetType().Name}).");
                    continue;
                }

                if (bitmap == null)
                {
                    Logger.Warning($"Unable to convert texture {item.Texture.m_Name} for material {item.MaterialName}, property {item.PropertyName}.");
                    continue;
                }

                decoded.Add((item.Name, item.MaterialName, item.PropertyName, bitmap));
            }

            pendingTextures.Clear();
            pendingTextureNames.Clear();

            if (decoded.Count == 0)
            {
                return;
            }

            // Step 2 (parallel across CPU cores): encode each independent SKBitmap into PNG/JPEG/BMP/TGA.
            var encodedStreams = new System.IO.MemoryStream?[decoded.Count];
            if (decoded.Count == 1)
            {
                using (decoded[0].Bitmap)
                {
                    encodedStreams[0] = decoded[0].Bitmap.ConvertToStream(imageFormat);
                }
            }
            else
            {
                int maxParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);
                Parallel.For(0, decoded.Count, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, i =>
                {
                    var entry = decoded[i];
                    try
                    {
                        using (entry.Bitmap)
                        {
                            encodedStreams[i] = entry.Bitmap.ConvertToStream(imageFormat);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Unable to encode texture {entry.Name} for material {entry.MaterialName}, property {entry.PropertyName}: {ex.Message}");
                    }
                });
            }

            // Step 3 (deterministic order): append to TextureList and dispose intermediate streams.
            for (int i = 0; i < decoded.Count; i++)
            {
                var stream = encodedStreams[i];
                if (stream == null)
                {
                    continue;
                }

                using (stream)
                {
                    TextureList.Add(new ImportedTexture(stream, decoded[i].Name));
                }
            }
        }

        private string AllocateOrGetConvertedTextureName(Texture2D m_Texture2D)
        {
            var ext = $".{imageFormat.ToString().ToLower()}";
            if (textureNameDictionary.TryGetValue(m_Texture2D, out var textureName))
            {
                return textureName;
            }

            if (IsTextureNameAllocated(m_Texture2D.m_Name + ext))
            {
                for (int i = 1; ; i++)
                {
                    var name = m_Texture2D.m_Name + $" ({i}){ext}";
                    if (!IsTextureNameAllocated(name))
                    {
                        textureNameDictionary.Add(m_Texture2D, name);
                        return name;
                    }
                }
            }

            var defaultName = m_Texture2D.m_Name + ext;
            textureNameDictionary.Add(m_Texture2D, defaultName);
            return defaultName;
        }

        private static bool TryFindTextureInMaterialScope(Material mat, PPtr<Texture> textureRef, [NotNullWhen(true)] out Texture2D? foundTex)
        {
            foundTex = null;
            var ownerFile = mat.assetsFile;
            if (ownerFile == null || textureRef == null || textureRef.IsNull)
            {
                return false;
            }

            if (textureRef.TryGetAssetsFile(out var targetFile)
                && TryResolveTextureFromFile(targetFile, textureRef.m_PathID, out foundTex))
            {
                return true;
            }

            if (textureRef.m_FileID == 0)
            {
                return TryResolveTextureFromFile(ownerFile, textureRef.m_PathID, out foundTex);
            }

            if (textureRef.m_FileID > 0
                && ownerFile.m_Externals != null
                && textureRef.m_FileID - 1 < ownerFile.m_Externals.Count)
            {
                var external = ownerFile.m_Externals[textureRef.m_FileID - 1];
                var files = ownerFile.assetsManager?.assetsFileList;
                if (files != null)
                {
                    for (int i = 0; i < files.Count; i++)
                    {
                        var sf = files[i];
                        if (sf == null || !IsMatchingExternalFile(sf, external))
                        {
                            continue;
                        }

                        if (TryResolveTextureFromFile(sf, textureRef.m_PathID, out var extTex))
                        {
                            foundTex = extTex;
                            Logger.Info($"Found texture '{extTex.m_Name}' in external file '{sf.fileName}'.");
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryResolveTextureFromFile(SerializedFile sourceFile, long pathId, [NotNullWhen(true)] out Texture2D? foundTex)
        {
            foundTex = null;
            if (sourceFile == null)
            {
                return false;
            }

            if (sourceFile.TryGetObject(pathId, out var existing) && existing is Texture2D existingTex)
            {
                foundTex = existingTex;
                return true;
            }

            var handle = sourceFile.assetsManager?.ProjectIndex?.GetHandle(AssetHandle.BuildUniqueID(sourceFile, pathId));
            if (handle != null && sourceFile.assetsManager != null && sourceFile.assetsManager.ResolveHandle(handle) is Texture2D resolvedTex)
            {
                foundTex = resolvedTex;
                return true;
            }

            return false;
        }

        private static bool IsMatchingExternalFile(SerializedFile candidate, FileIdentifier external)
        {
            if (candidate == null || external == null)
            {
                return false;
            }

            var extFile = Path.GetFileName(external.fileName);
            var extPathFile = Path.GetFileName(external.pathName);
            var candFile = Path.GetFileName(candidate.fileName);
            var candOrig = Path.GetFileName(candidate.originalPath);

            if (!string.IsNullOrEmpty(extFile))
            {
                if (string.Equals(candFile, extFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candOrig, extFile, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(extPathFile))
            {
                if (string.Equals(candFile, extPathFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candOrig, extPathFile, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static int GetTextureDestination(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return -1;
            }

            // 1. Explicit non-diffuse / mask / highlight / scroll / matcap exclusions MUST be checked first
            // so e.g. _MaskTexture, _EmissionMask, or _BaseEyeHighlightMap never masquerade as diffuse or emissive maps.
            switch (propertyName)
            {
                case "_MaskTexture":
                case "_MaskMap":
                case "_Mask":
                case "_WeatheringMask":
                case "_EmissionMask":
                case "_DetailAlbedoMap":
                case "_DetailMap":
                case "_DetailMask":
                    return 7;
                case "_MatcapMap":
                case "_Matcap":
                case "_MatcapTex":
                case "_MatcapMaskMap":
                case "_WetTexture":
                case "_BaseEyeHighlightMap":
                case "_BaseEyeHighlightMapBackup":
                case "_BaseEyePupilMap":
                case "_BaseEyePupilMapBackup":
                case "_UVScrollTexture1":
                case "_UVScrollTexture2":
                    return -1;
            }

            switch (propertyName)
            {
                case "_BaseMap":
                case "_MainTex":
                case "_MainTexture":
                case "_Main_Tex":
                case "_BaseColorMap":
                case "_BaseColorTexture":
                case "_BaseColorTex":
                case "_BaseTex":
                case "_BaseTexture":
                case "_Base_Texture":
                case "_Albedo":
                case "_AlbedoMap":
                case "_AlbedoTex":
                case "_AlbedoTexture":
                case "_Diffuse":
                case "_DiffuseMap":
                case "_DiffuseTex":
                case "_DiffuseTexture":
                case "_ColorMap":
                case "_ColorTex":
                case "_ColorTexture":
                case "_Color_Texture":
                case "_FaceTex":
                case "_BaseEyeIrisMap":
                case "_EyeIrisMap":
                case "_IrisMap":
                case "_IrisTex":
                case "_EyeTex":
                case "_EyeMap":
                case "_BaseEyeWhiteMap":
                case "_Texture":
                case "_Tex":
                    return 0;
                case "_BumpMap":
                case "_BumpMap2":
                case "_BumpMap3":
                case "_NormalMap":
                case "_NormalMap2":
                case "_DetailNormalMap":
                case "_WaterNormalMap":
                case "_WaveNormalMap":
                case "_NormalTex":
                case "_NormalTex2":
                case "_BumpTex":
                case "_BumpTex2":
                case "_NrmMap":
                case "_NrmTex":
                case "_Nrm":
                    return 1;
                case "_SpecGlossMap":
                case "_SpecularMap":
                    return 2;
                case "_MetallicGlossMap":
                case "_MetallicMap":
                    return 3;
                case "_EmissionMap":
                case "_EmissiveMap":
                    return 4;
                case "_OcclusionMap":
                case "_AOMap":
                    return 5;
                case "_ParallaxMap":
                case "_HeightMap":
                case "_DispMap":
                case "_DisplacementMap":
                    return 6;
                case "_RoughnessMap":
                case "_SmoothnessMap":
                case "_GlossMap":
                    return 8;
                case "_AlphaMap":
                case "_OpacityMap":
                case "_TransparencyMap":
                    return 9;
            }

            if (propertyName.IndexOf("Highlight", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Pupil", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Backup", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Matcap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("UVScroll", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("WetTexture", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return -1;
            }

            if (propertyName.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Detail", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 7;
            }
            if (propertyName.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("_Nrm", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("NrmMap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("NrmTex", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            if (propertyName.IndexOf("Spec", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }
            if (propertyName.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 3;
            }
            if (propertyName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 4;
            }
            if (propertyName.IndexOf("Occlusion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 5;
            }
            if (propertyName.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 6;
            }
            if (propertyName.IndexOf("Rough", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Smooth", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Gloss", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 8;
            }
            if (propertyName.IndexOf("Albedo", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("BaseColor", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("BaseMap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("BaseTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("MainTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Main_Tex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("MainTexture", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("ColorTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Color_Tex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Diffuse", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("FaceTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("EyeIris", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("IrisMap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("IrisTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("EyeTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("EyeMap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("EyeWhite", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            if (propertyName.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Opacity", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Transparen", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 9;
            }

            return -1;
        }

        public static int GetDiffuseSlotPriority(string propertyName)
        {
            return propertyName switch
            {
                "_BaseMap" => 1,
                "_MainTex" => 2,
                "_MainTexture" => 3,
                "_Main_Tex" => 4,
                "_BaseColorMap" => 5,
                "_BaseColorTexture" => 6,
                "_BaseColorTex" => 7,
                "_BaseTex" => 8,
                "_BaseTexture" => 9,
                "_Base_Texture" => 10,
                "_Albedo" => 11,
                "_AlbedoMap" => 12,
                "_AlbedoTex" => 13,
                "_AlbedoTexture" => 14,
                "_Diffuse" => 15,
                "_DiffuseMap" => 16,
                "_DiffuseTex" => 17,
                "_DiffuseTexture" => 18,
                "_ColorMap" => 19,
                "_ColorTex" => 20,
                "_ColorTexture" => 21,
                "_Color_Texture" => 22,
                "_FaceTex" => 23,
                "_BaseEyeIrisMap" => 24,
                "_EyeIrisMap" => 25,
                "_IrisMap" => 26,
                "_IrisTex" => 27,
                "_EyeTex" => 28,
                "_EyeMap" => 29,
                "_BaseEyeWhiteMap" => 30,
                "_Texture" => 31,
                "_Tex" => 32,
                _ => 50
            };
        }

        private static bool IsLikelyAuxiliaryTextureProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return true;
            }

            return propertyName.IndexOf("Ramp", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Lut", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Matcap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Weathering", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("WetTexture", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Highlight", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Pupil", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Backup", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("UVScroll", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Shade", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Cube", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Refl", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Noise", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Dissolve", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Flow", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Distort", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Rim", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Fresnel", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Outline", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("PBR", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("ORM", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Sdf", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Env", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("LightMap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Gradient", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Splat", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Nrm", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Disp", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("DuDv", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Foam", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Caustic", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Ripple", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Refract", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Depth", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Curvature", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Thickness", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Cavity", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ConvertAnimations()
        {
            foreach (var animationClip in animationClipHashSet)
            {
                var iAnim = new ImportedKeyframedAnimation();
                var name = animationClip.m_Name;
                if (AnimationList.Exists(x => x.Name == name))
                {
                    for (int i = 1; ; i++)
                    {
                        var fixName = name + $"_{i}";
                        if (!AnimationList.Exists(x => x.Name == fixName))
                        {
                            name = fixName;
                            break;
                        }
                    }
                }
                iAnim.Name = name;
                iAnim.SampleRate = animationClip.m_SampleRate;
                iAnim.TrackList = new List<ImportedAnimationKeyframedTrack>();
                AnimationList.Add(iAnim);
                if (animationClip.m_Legacy)
                {
                    foreach (var m_CompressedRotationCurve in animationClip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_CompressedRotationCurve.m_Path));

                        var numKeys = m_CompressedRotationCurve.m_Times.m_NumItems;
                        var data = m_CompressedRotationCurve.m_Times.UnpackInts();
                        var times = new float[numKeys];
                        int t = 0;
                        for (int i = 0; i < numKeys; i++)
                        {
                            t += data[i];
                            times[i] = t * 0.01f;
                        }
                        var quats = m_CompressedRotationCurve.m_Values.UnpackQuats();

                        for (int i = 0; i < numKeys; i++)
                        {
                            var quat = quats[i];
                            var value = QuaternionToEuler(new Quaternion(quat.X, -quat.Y, -quat.Z, quat.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(times[i], value));
                        }
                    }
                    foreach (var m_RotationCurve in animationClip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_RotationCurve.path));
                        foreach (var m_Curve in m_RotationCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z) || float.IsInfinity(m_Curve.outSlope.W);
                            var value = QuaternionToEuler(new Quaternion(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z, m_Curve.value.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, value, isStepped));
                        }
                    }
                    foreach (var m_PositionCurve in animationClip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_PositionCurve.path));
                        foreach (var m_Curve in m_PositionCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                            track.Translations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(-m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z), isStepped));
                        }
                    }
                    foreach (var m_ScaleCurve in animationClip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_ScaleCurve.path));
                        foreach (var m_Curve in m_ScaleCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z), isStepped));
                        }
                    }
                    if (animationClip.m_EulerCurves != null)
                    {
                        foreach (var m_EulerCurve in animationClip.m_EulerCurves)
                        {
                            var track = iAnim.FindTrack(FixBonePath(animationClip, m_EulerCurve.path));
                            int rotOrder = m_EulerCurve.curve.m_RotationOrder;
                            foreach (var m_Curve in m_EulerCurve.curve.m_Curve)
                            {
                                bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z, rotOrder);
                                var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, QuaternionToEuler(qF), isStepped));
                            }
                        }
                    }
                    foreach (var m_FloatCurve in animationClip.m_FloatCurves ?? Array.Empty<FloatCurve>())
                    {
                        if (m_FloatCurve.classID == ClassIDType.SkinnedMeshRenderer) //BlendShape
                        {
                            var channelName = NormalizeBlendShapeChannelName(m_FloatCurve.attribute);
                            var path = ResolveBlendShapeTrackPath(animationClip, m_FloatCurve.path, channelName);
                            var track = iAnim.FindTrack(path);
                            var blendShape = track.FindOrCreateBlendShape(channelName);
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                blendShape.Keyframes.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value, float.IsInfinity(m_Curve.outSlope)));
                            }
                        }
                        else if (m_FloatCurve.classID == ClassIDType.GameObject
                            && string.Equals(m_FloatCurve.attribute, "m_IsActive", StringComparison.Ordinal))
                        {
                            var path = FixBonePath(animationClip, m_FloatCurve.path);
                            var track = iAnim.FindTrack(path);
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                track.Visibilities.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                            }
                        }
                    }
                }
                else if (animationClip.m_MuscleClip?.m_Clip != null)
                {
                    var m_Clip = animationClip.m_MuscleClip.m_Clip;
                    var m_ClipBindingConstant = animationClip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();
                    float clipSampleRate = animationClip.m_SampleRate > 0f && float.IsFinite(animationClip.m_SampleRate)
                        ? animationClip.m_SampleRate
                        : 30f;

                    // 1. StreamedClip: evaluate per-binding via cubic Hermite evaluator (handles sparse partial frames,
                    //    skips sentinels 0 and Count-1, preserves stepped segments, and subdivides curved Hermite spans).
                    uint streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    if (m_Clip.m_StreamedClip != null && streamCount > 0 && m_Clip.m_StreamedClip.data != null && m_Clip.m_StreamedClip.data.Length > 0)
                    {
                        List<StreamedClip.StreamedFrame>? streamedFrames = null;
                        try
                        {
                            streamedFrames = m_Clip.m_StreamedClip.ReadData();
                        }
                        catch
                        {
                            streamedFrames = null;
                        }
                        if (streamedFrames != null && streamedFrames.Count >= 3)
                        {
                            var streamedEval = new HumanoidMuscleDecoder.StreamedCurveEvaluator(streamedFrames);
                            ReadStreamedClipCurves(iAnim, animationClip, m_ClipBindingConstant, streamedEval, (int)streamCount, clipSampleRate);
                        }
                    }

                    // 2. DenseClip (with sample-rate > 0 guard)
                    var m_DenseClip = m_Clip.m_DenseClip;
                    if (m_DenseClip != null && m_DenseClip.m_FrameCount > 0 && m_DenseClip.m_CurveCount > 0 && m_DenseClip.m_SampleArray != null)
                    {
                        float denseSampleRate = m_DenseClip.m_SampleRate > 0f && float.IsFinite(m_DenseClip.m_SampleRate)
                            ? m_DenseClip.m_SampleRate
                            : clipSampleRate;
                        float beginTime = float.IsFinite(m_DenseClip.m_BeginTime) ? m_DenseClip.m_BeginTime : 0f;
                        for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                        {
                            var time = beginTime + frameIndex / denseSampleRate;
                            if (time < 0f) time = 0f;
                            var frameOffset = frameIndex * (int)m_DenseClip.m_CurveCount;
                            for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                            {
                                var index = streamCount + curveIndex;
                                ReadCurveData(iAnim, animationClip, m_ClipBindingConstant, (int)index, time, m_DenseClip.m_SampleArray, frameOffset, ref curveIndex);
                            }
                        }
                    }

                    // 3. ConstantClip (honor m_StartTime, avoid duplicate t=0 key when m_StopTime <= m_StartTime)
                    if (m_Clip.m_ConstantClip?.data != null && m_Clip.m_ConstantClip.data.Length > 0)
                    {
                        var m_ConstantClip = m_Clip.m_ConstantClip;
                        var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                        float startTime = float.IsFinite(animationClip.m_MuscleClip.m_StartTime) && animationClip.m_MuscleClip.m_StartTime > 0f
                            ? animationClip.m_MuscleClip.m_StartTime
                            : 0f;
                        float stopTime = float.IsFinite(animationClip.m_MuscleClip.m_StopTime) && animationClip.m_MuscleClip.m_StopTime > startTime
                            ? animationClip.m_MuscleClip.m_StopTime
                            : startTime;
                        int passCount = stopTime > startTime + 1e-6f ? 2 : 1;
                        for (int i = 0; i < passCount; i++)
                        {
                            float time2 = i == 0 ? startTime : stopTime;
                            for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                            {
                                var index = streamCount + denseCount + curveIndex;
                                ReadCurveData(iAnim, animationClip, m_ClipBindingConstant, (int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                            }
                        }
                    }

                    // 4. Humanoid MuscleClip decoding
                    if (avatar != null)
                    {
                        ReadHumanoidMuscleCurves(iAnim, animationClip);
                    }
                }

                FinalizeAnimationTracks(iAnim);
            }
        }

        private void ReadStreamedClipCurves(
            ImportedKeyframedAnimation iAnim,
            AnimationClip animationClip,
            AnimationClipBindingConstant? m_ClipBindingConstant,
            HumanoidMuscleDecoder.StreamedCurveEvaluator evaluator,
            int streamCount,
            float clipSampleRate)
        {
            if (m_ClipBindingConstant?.genericBindings == null || !evaluator.HasAnyKeys || streamCount <= 0)
            {
                return;
            }

            float dtStep = 1f / Math.Clamp(clipSampleRate, 1f, 120f);
            var tempValues = new float[4];
            var bindingTimes = new List<float>();

            int slotCursor = 0;
            var bindings = m_ClipBindingConstant.genericBindings;
            for (int bIdx = 0; bIdx < bindings.Length; bIdx++)
            {
                var binding = bindings[bIdx];
                int width = GetGenericBindingCurveWidth(binding);
                int startSlot = slotCursor;
                slotCursor += width;

                if (startSlot >= streamCount)
                {
                    break;
                }

                bool isSupportedTransform = binding.typeID == ClassIDType.Transform && binding.attribute is 1 or 2 or 3 or 4;
                bool isBlendShape = binding.typeID == ClassIDType.SkinnedMeshRenderer;
                bool isVisibility = binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974;
                if (!isSupportedTransform && !isBlendShape && !isVisibility)
                {
                    continue;
                }

                // Collect union of key times across all component curves of this binding
                bindingTimes.Clear();
                int activeComponents = 0;
                for (int c = 0; c < width; c++)
                {
                    if (startSlot + c >= streamCount)
                    {
                        break;
                    }
                    if (evaluator.TryGetCurveKeys(startSlot + c, out var keys) && keys.Count > 0)
                    {
                        activeComponents++;
                        for (int k = 0; k < keys.Count; k++)
                        {
                            bindingTimes.Add(keys[k].Time);
                        }
                    }
                }

                if (activeComponents == 0 || bindingTimes.Count == 0)
                {
                    continue;
                }

                bindingTimes.Sort();
                int uniqueCount = 0;
                for (int i = 0; i < bindingTimes.Count; i++)
                {
                    if (uniqueCount == 0 || bindingTimes[i] - bindingTimes[uniqueCount - 1] > 1e-6f)
                    {
                        bindingTimes[uniqueCount++] = bindingTimes[i];
                    }
                }

                // Resolve target track & blendshape channel once per binding
                ImportedAnimationKeyframedTrack? track = null;
                ImportedBlendShape? blendShape = null;
                if (isBlendShape)
                {
                    var channelName = GetChannelNameFromHash(binding.attribute);
                    if (string.IsNullOrEmpty(channelName))
                    {
                        continue;
                    }
                    channelName = NormalizeBlendShapeChannelName(channelName);
                    var bPath = ResolveBlendShapeTrackPath(animationClip, GetPathFromHash(binding.path), channelName);
                    track = iAnim.FindTrack(bPath);
                    blendShape = track.FindOrCreateBlendShape(channelName);
                }
                else
                {
                    var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                    track = iAnim.FindTrack(path);
                }

                float curvatureTol = binding.typeID == ClassIDType.Transform && binding.attribute == 4 ? 0.05f : 1e-3f;

                void EmitSample(float sampleTime, bool stepped)
                {
                    for (int c = 0; c < width; c++)
                    {
                        float fallback = (binding.typeID == ClassIDType.Transform && binding.attribute == 3)
                            ? 1f
                            : (binding.typeID == ClassIDType.Transform && binding.attribute == 2 && c == 3 ? 1f : 0f);
                        tempValues[c] = evaluator.EvaluateOrDefault(startSlot + c, sampleTime, fallback);
                    }

                    if (isBlendShape && blendShape != null)
                    {
                        blendShape.Keyframes.Add(new ImportedKeyframe<float>(sampleTime, tempValues[0], stepped));
                    }
                    else if (isVisibility)
                    {
                        track.Visibilities.Add(new ImportedKeyframe<float>(sampleTime, tempValues[0] >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                    }
                    else if (isSupportedTransform)
                    {
                        switch (binding.attribute)
                        {
                            case 1:
                                track.Translations.Add(new ImportedKeyframe<Vector3>(
                                    sampleTime,
                                    new Vector3(-tempValues[0], tempValues[1], tempValues[2]),
                                    stepped));
                                break;
                            case 2:
                                var eulerFromQuat = QuaternionToEuler(new Quaternion(tempValues[0], -tempValues[1], -tempValues[2], tempValues[3]));
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(sampleTime, eulerFromQuat, stepped));
                                break;
                            case 3:
                                track.Scalings.Add(new ImportedKeyframe<Vector3>(
                                    sampleTime,
                                    new Vector3(tempValues[0], tempValues[1], tempValues[2]),
                                    stepped));
                                break;
                            case 4:
                                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(tempValues[0], tempValues[1], tempValues[2], 4);
                                var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(sampleTime, QuaternionToEuler(qF), stepped));
                                break;
                        }
                    }
                }

                for (int i = 0; i < uniqueCount; i++)
                {
                    float t0 = bindingTimes[i];
                    bool segStepped = isVisibility;
                    if (!segStepped)
                    {
                        segStepped = true;
                        for (int c = 0; c < width; c++)
                        {
                            if (evaluator.TryEvaluate(startSlot + c, t0, out _, out bool compStepped) && !compStepped)
                            {
                                segStepped = false;
                                break;
                            }
                        }
                    }

                    EmitSample(t0, segStepped);

                    // Adaptively subdivide non-stepped Hermite segments whose cubic midpoint diverges from linear
                    if (!segStepped && i + 1 < uniqueCount)
                    {
                        float t1 = bindingTimes[i + 1];
                        float span = t1 - t0;
                        if (span > dtStep * 1.25f)
                        {
                            float tMid = 0.5f * (t0 + t1);
                            bool needsSubdivision = false;
                            for (int c = 0; c < width; c++)
                            {
                                float v0 = evaluator.EvaluateOrDefault(startSlot + c, t0, 0f);
                                float v1 = evaluator.EvaluateOrDefault(startSlot + c, t1, 0f);
                                float vMid = evaluator.EvaluateOrDefault(startSlot + c, tMid, 0f);
                                if (MathF.Abs(vMid - 0.5f * (v0 + v1)) > curvatureTol)
                                {
                                    needsSubdivision = true;
                                    break;
                                }
                            }

                            if (needsSubdivision)
                            {
                                int subSteps = Math.Clamp((int)MathF.Ceiling(span / dtStep), 2, 120);
                                for (int s = 1; s < subSteps; s++)
                                {
                                    float ts = t0 + span * s / subSteps;
                                    EmitSample(ts, stepped: false);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static int GetGenericBindingCurveWidth(GenericBinding binding)
        {
            if (binding.typeID != ClassIDType.Transform)
            {
                return 1;
            }
            return binding.attribute switch
            {
                1 => 3,
                2 => 4,
                3 => 3,
                4 => 3,
                _ => 1
            };
        }

        private string? ResolveBlendShapeTrackPath(AnimationClip animationClip, string? rawPath, string channelName)
        {
            var resolved = FixBonePath(animationClip, rawPath);
            if (!string.IsNullOrEmpty(resolved) && HasMorphWithPath(resolved))
            {
                return resolved;
            }

            var byChannel = GetPathByChannelName(channelName);
            if (!string.IsNullOrEmpty(byChannel))
            {
                return byChannel;
            }

            return resolved;
        }

        private bool HasMorphWithPath(string path)
        {
            var norm = NormalizeFramePath(path);
            for (int i = 0; i < MorphList.Count; i++)
            {
                if (string.Equals(NormalizeFramePath(MorphList[i].Path), norm, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadCurveData(ImportedKeyframedAnimation iAnim, AnimationClip animationClip, AnimationClipBindingConstant m_ClipBindingConstant, int index, float time, float[] data, int offset, ref int curveIndex)
        {
            var binding = m_ClipBindingConstant.FindBinding(index);
            if (binding == null)
            {
                curveIndex++;
                return;
            }
            if (binding.typeID == ClassIDType.SkinnedMeshRenderer) //BlendShape
            {
                var channelName = GetChannelNameFromHash(binding.attribute);
                if (string.IsNullOrEmpty(channelName))
                {
                    curveIndex++;
                    return;
                }
                channelName = NormalizeBlendShapeChannelName(channelName);

                var bPath = ResolveBlendShapeTrackPath(animationClip, GetPathFromHash(binding.path), channelName);
                var bTrack = iAnim.FindTrack(bPath);
                var blendShape = bTrack.FindOrCreateBlendShape(channelName);
                int idxVal = curveIndex++ + offset;
                if ((uint)idxVal < (uint)data.Length)
                {
                    blendShape.Keyframes.Add(new ImportedKeyframe<float>(time, data[idxVal]));
                }
            }
            else if (binding.typeID == ClassIDType.Transform)
            {
                var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);

                switch (binding.attribute)
                {
                    case 1:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            track.Translations.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                            (
                                -data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset]
                            )));
                        }
                        curveIndex += 3;
                        break;
                    case 2:
                        if ((uint)(curveIndex + 3 + offset) < (uint)data.Length)
                        {
                            var value = QuaternionToEuler(new Quaternion
                            (
                                data[curveIndex + offset],
                                -data[curveIndex + 1 + offset],
                                -data[curveIndex + 2 + offset],
                                data[curveIndex + 3 + offset]
                            ));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(time, value));
                        }
                        curveIndex += 4;
                        break;
                    case 3:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                            (
                                data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset]
                            )));
                        }
                        curveIndex += 3;
                        break;
                    case 4:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(
                                data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset],
                                rotationOrder: 4);
                            var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(time, QuaternionToEuler(qF)));
                        }
                        curveIndex += 3;
                        break;
                    default:
                        curveIndex++;
                        break;
                }
            }
            else if (binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974)
            {
                var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);
                int idxVal = curveIndex++ + offset;
                if ((uint)idxVal < (uint)data.Length)
                {
                    track.Visibilities.Add(new ImportedKeyframe<float>(time, data[idxVal] >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                }
            }
            else
            {
                curveIndex++;
            }
        }

        private static void FinalizeAnimationTracks(ImportedKeyframedAnimation iAnim)
        {
            if (iAnim.TrackList == null)
            {
                return;
            }

            for (int i = 0; i < iAnim.TrackList.Count; i++)
            {
                var track = iAnim.TrackList[i];
                SortAndDeduplicateVector3Keyframes(track.Translations);
                SortAndDeduplicateVector3Keyframes(track.Rotations);
                SortAndDeduplicateVector3Keyframes(track.Scalings);
                SortAndDeduplicateFloatKeyframes(track.Visibilities);

                if (track.BlendShapes != null)
                {
                    for (int b = 0; b < track.BlendShapes.Count; b++)
                    {
                        SortAndDeduplicateFloatKeyframes(track.BlendShapes[b].Keyframes);
                    }
                }

                HumanoidMuscleDecoder.UnwrapEulerKeyframes(track.Rotations);
            }
        }

        private static void SortAndDeduplicateVector3Keyframes(List<ImportedKeyframe<Vector3>> keyframes)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return;
            }

            keyframes.RemoveAll(k => !float.IsFinite(k.time) || !float.IsFinite(k.value.X) || !float.IsFinite(k.value.Y) || !float.IsFinite(k.value.Z));
            if (keyframes.Count <= 1)
            {
                if (keyframes.Count == 1 && keyframes[0].time < 0f)
                {
                    keyframes[0].time = 0f;
                }
                return;
            }

            keyframes.Sort((a, b) => a.time.CompareTo(b.time));
            int write = 0;
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k.time < 0f) k.time = 0f;
                if (write > 0 && MathF.Abs(k.time - keyframes[write - 1].time) <= 1e-6f)
                {
                    keyframes[write - 1] = k;
                }
                else
                {
                    keyframes[write++] = k;
                }
            }
            if (write < keyframes.Count)
            {
                keyframes.RemoveRange(write, keyframes.Count - write);
            }
        }

        private static void SortAndDeduplicateFloatKeyframes(List<ImportedKeyframe<float>> keyframes)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return;
            }

            keyframes.RemoveAll(k => !float.IsFinite(k.time) || !float.IsFinite(k.value));
            if (keyframes.Count <= 1)
            {
                if (keyframes.Count == 1 && keyframes[0].time < 0f)
                {
                    keyframes[0].time = 0f;
                }
                return;
            }

            keyframes.Sort((a, b) => a.time.CompareTo(b.time));
            int write = 0;
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k.time < 0f) k.time = 0f;
                if (write > 0 && MathF.Abs(k.time - keyframes[write - 1].time) <= 1e-6f)
                {
                    keyframes[write - 1] = k;
                }
                else
                {
                    keyframes[write++] = k;
                }
            }
            if (write < keyframes.Count)
            {
                keyframes.RemoveRange(write, keyframes.Count - write);
            }
        }

        private bool TryResolveBonePathFromHash(uint hash, [NotNullWhen(true)] out string? resolvedPath)
        {
            resolvedPath = null;
            if (hash == 0)
            {
                return false;
            }

            if (bonePathHash.TryGetValue(hash, out var boneName) && !string.IsNullOrEmpty(boneName))
            {
                var frame = RootFrame?.FindFrameByPath(boneName);
                resolvedPath = frame?.Path ?? boneName;
                return !string.IsNullOrEmpty(resolvedPath);
            }

            if (avatar != null)
            {
                boneName = avatar.FindBonePath(hash);
                if (!string.IsNullOrEmpty(boneName))
                {
                    var frame = RootFrame?.FindFrameByPath(boneName);
                    resolvedPath = frame?.Path ?? boneName;
                    return !string.IsNullOrEmpty(resolvedPath);
                }
            }

            return false;
        }

        private string GetPathFromHash(uint hash)
        {
            if (hash == 0)
            {
                return !string.IsNullOrEmpty(animatorRootPath)
                    ? animatorRootPath
                    : (RootFrame?.Path ?? string.Empty);
            }
            if (bonePathHash.TryGetValue(hash, out var boneName))
            {
                return boneName;
            }
            if (avatar != null)
            {
                boneName = avatar.FindBonePath(hash);
                if (boneName != null)
                {
                    return boneName;
                }
            }
            return $"Bone_{hash}";
        }

        private void CreateBonePathHash(Transform? m_Transform)
        {
            CreateBonePathHash(m_Transform, new HashSet<Transform>());
        }

        private void CreateBonePathHash(Transform? m_Transform, HashSet<Transform> visited)
        {
            if (m_Transform == null || !visited.Add(m_Transform))
            {
                return;
            }
            var fullFramePath = GetTransformPath(m_Transform);
            var fatherPath = GetTransformPathByFather(m_Transform);
            var canonicalPath = !string.IsNullOrEmpty(fullFramePath) ? fullFramePath : fatherPath;

            RegisterPathAndSuffixHashes(fatherPath, canonicalPath, overwriteExact: true);
            if (!string.IsNullOrEmpty(fullFramePath) && !string.Equals(fullFramePath, fatherPath, StringComparison.Ordinal))
            {
                RegisterPathAndSuffixHashes(fullFramePath, canonicalPath, overwriteExact: true);
            }

            foreach (var pptr in m_Transform.m_Children)
            {
                if (pptr.TryGet(out var child))
                    CreateBonePathHash(child, visited);
            }
        }

        private void RegisterPathAndSuffixHashes(string? pathToHash, string canonicalFramePath, bool overwriteExact)
        {
            if (string.IsNullOrEmpty(pathToHash) || string.IsNullOrEmpty(canonicalFramePath))
            {
                return;
            }

            var exactCrc = ComputeCrc32(pathToHash);
            if (overwriteExact || !bonePathHash.ContainsKey(exactCrc))
            {
                bonePathHash[exactCrc] = canonicalFramePath;
            }

            var suffix = pathToHash;
            int index;
            while ((index = suffix.IndexOf("/", StringComparison.Ordinal)) >= 0)
            {
                suffix = suffix.Substring(index + 1);
                if (string.IsNullOrEmpty(suffix))
                {
                    break;
                }
                var suffixCrc = ComputeCrc32(suffix);
                if (!bonePathHash.ContainsKey(suffixCrc))
                {
                    bonePathHash[suffixCrc] = canonicalFramePath;
                }
            }
        }

        private void DeoptimizeTransformHierarchy()
        {
            if (avatar == null)
                throw new Exception("Transform hierarchy has been optimized, but can't find Avatar to deoptimize.");

            var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
            var skeletonPose = avatar.m_Avatar.m_DefaultPose?.m_X is { Length: > 0 }
                ? avatar.m_Avatar.m_DefaultPose
                : avatar.m_Avatar.m_AvatarSkeletonPose;
            var nodes = skeleton?.m_Node;

            if (skeleton == null || nodes == null || nodes.Length == 0 || skeleton.m_ID == null)
                return;

            BuildSkeletonFramesFromDefaultPose(skeleton, skeletonPose, fallbackMeshes: null, preserveExistingTransformFrames: false);
        }

        private void BuildSkeletonFramesFromDefaultPose(Skeleton skeleton, SkeletonPose? skeletonPose, IReadOnlyList<Mesh>? fallbackMeshes, bool preserveExistingTransformFrames)
        {
            var nodes = skeleton.m_Node;
            var skelIds = skeleton.m_ID;
            if (nodes == null || nodes.Length == 0 || skelIds == null || RootFrame == null)
                return;

            var skelCount = nodes.Length;
            bool hasDefaultPose = skeletonPose?.m_X != null && skeletonPose.m_X.Length >= skelCount;

            // Only fall back to m_BindPose decomposition when m_DefaultPose is absent/empty
            int[]? skelNodeToMeshBone = null;
            int[]? meshParentIndices = null;
            System.Numerics.Matrix4x4[]? bindPoseInverses = null;
            System.Numerics.Matrix4x4[]? bindPoses = null;
            bool useBindPoseFallback = false;

            if (!hasDefaultPose)
            {
                Mesh? fallbackMesh = null;
                int bestBoneCount = 0;
                if (fallbackMeshes != null)
                {
                    for (int i = 0; i < fallbackMeshes.Count; i++)
                    {
                        var m = fallbackMeshes[i];
                        if (m == null) continue;
                        m.EnsureProcessed();
                        if (m.m_BoneNameHashes != null && m.m_BindPose != null
                            && m.m_BoneNameHashes.Length == m.m_BindPose.Length
                            && m.m_BoneNameHashes.Length > bestBoneCount)
                        {
                            bestBoneCount = m.m_BoneNameHashes.Length;
                            fallbackMesh = m;
                        }
                    }
                }

                if (fallbackMesh != null && fallbackMesh.m_BindPose != null && fallbackMesh.m_BoneNameHashes != null && bestBoneCount > 0)
                {
                    skelNodeToMeshBone = new int[skelCount];
                    for (int i = 0; i < skelCount; i++) skelNodeToMeshBone[i] = -1;

                    var meshBoneHashToIdx = new Dictionary<uint, int>(bestBoneCount);
                    for (int j = 0; j < bestBoneCount; j++)
                    {
                        meshBoneHashToIdx[fallbackMesh.m_BoneNameHashes[j]] = j;
                    }

                    var meshBoneToSkelNode = new int[bestBoneCount];
                    for (int j = 0; j < bestBoneCount; j++) meshBoneToSkelNode[j] = -1;

                    for (int i = 0; i < skelCount && i < skelIds.Length; i++)
                    {
                        if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                        {
                            skelNodeToMeshBone[i] = mbIdx;
                            meshBoneToSkelNode[mbIdx] = i;
                        }
                    }

                    meshParentIndices = new int[bestBoneCount];
                    for (int mb = 0; mb < bestBoneCount; mb++)
                    {
                        meshParentIndices[mb] = -1;
                        int skelIdx = meshBoneToSkelNode[mb];
                        if (skelIdx < 0) continue;

                        int parentId = nodes[skelIdx].m_ParentId;
                        if (parentId >= 0 && parentId < skelCount && skelNodeToMeshBone[parentId] >= 0)
                        {
                            meshParentIndices[mb] = skelNodeToMeshBone[parentId];
                        }
                    }

                    bindPoseInverses = new System.Numerics.Matrix4x4[bestBoneCount];
                    bindPoses = new System.Numerics.Matrix4x4[bestBoneCount];
                    useBindPoseFallback = true;
                    for (int j = 0; j < bestBoneCount; j++)
                    {
                        var bp = ToNumericsMatrix(fallbackMesh.m_BindPose[j]);
                        bindPoseInverses[j] = bp;
                        bindPoses[j] = System.Numerics.Matrix4x4.Invert(bp, out var inv)
                            ? inv
                            : System.Numerics.Matrix4x4.Identity;
                    }
                }
            }

            var existingTransformFrames = new HashSet<ImportedFrame>(transformDictionary.Values);
            var frames = new ImportedFrame[skelCount];
            for (int i = 0; i < skelCount; i++)
            {
                int parentId = nodes[i].m_ParentId;
                uint nodeId = i < skelIds.Length ? skelIds[i] : (uint)i;
                var fullPath = avatar?.FindBonePath(nodeId);

                // Root node (m_ParentId < 0 with empty bone path / ID == 0) represents RootFrame itself
                if (parentId < 0 && string.IsNullOrEmpty(fullPath))
                {
                    frames[i] = RootFrame;
                    bonePathHash[nodeId] = RootFrame.Path;
                    continue;
                }

                string name;
                if (string.IsNullOrEmpty(fullPath))
                {
                    name = $"Bone_{nodeId}";
                }
                else
                {
                    var lastSlash = fullPath.LastIndexOf('/');
                    name = lastSlash >= 0 ? fullPath.Substring(lastSlash + 1) : fullPath;
                }

                var xform = (skeletonPose?.m_X != null && i < skeletonPose.m_X.Length) ? skeletonPose.m_X[i] : null;
                var tVec = xform?.t ?? Vector3.Zero;
                var qQuat = xform?.q ?? new Quaternion(0, 0, 0, 1);
                var sVec = xform?.s ?? Vector3.One;

                if (useBindPoseFallback && skelNodeToMeshBone != null && meshParentIndices != null && bindPoses != null && bindPoseInverses != null)
                {
                    int mb = skelNodeToMeshBone[i];
                    if (mb >= 0)
                    {
                        int pMb = meshParentIndices[mb];
                        var localMat = pMb >= 0 ? bindPoses[mb] * bindPoseInverses[pMb] : bindPoses[mb];
                        if (System.Numerics.Matrix4x4.Decompose(localMat, out var s, out var q, out var t))
                        {
                            tVec = new Vector3(t.X, t.Y, t.Z);
                            qQuat = new Quaternion(q.X, q.Y, q.Z, q.W);
                            sVec = new Vector3(s.X, s.Y, s.Z);
                        }
                    }
                }

                ImportedFrame parentFrame = (parentId >= 0 && parentId < i && frames[parentId] != null)
                    ? frames[parentId]
                    : RootFrame;

                ImportedFrame? frame = null;
                if (!string.IsNullOrEmpty(fullPath))
                {
                    frame = RootFrame.FindRelativeFrameWithPath(fullPath) ?? RootFrame.FindFrameByPath(fullPath);
                }
                frame ??= parentFrame.FindChild(name, recursive: false) ?? RootFrame.FindChild(name, recursive: false);

                if (frame != null && !ReferenceEquals(frame, RootFrame))
                {
                    // Preserve real scene Transform pose & parenting when building Avatar fallback frames on an unoptimized hierarchy.
                    // For DeoptimizeTransformHierarchy (m_HasTransformHierarchy == false), exposed transforms were flattened under RootFrame
                    // by Unity and MUST be reparented back into the Avatar skeleton.
                    if (!preserveExistingTransformFrames || !existingTransformFrames.Contains(frame))
                    {
                        SetFrame(frame, tVec, qQuat, sVec);
                        if (!ReferenceEquals(frame.Parent, parentFrame))
                        {
                            parentFrame.AddChild(frame);
                        }
                    }
                }
                else
                {
                    frame = CreateFrame(name, tVec, qQuat, sVec);
                    parentFrame.AddChild(frame);
                }

                frames[i] = frame;
            }

            // Connect any out-of-order parent edges and register bone path hashes (full path + relative suffixes)
            for (int i = 0; i < skelCount; i++)
            {
                var frame = frames[i];
                if (frame == null || ReferenceEquals(frame, RootFrame))
                {
                    continue;
                }

                int parentId = nodes[i].m_ParentId;
                var parentFrame = (parentId >= 0 && parentId < frames.Length && frames[parentId] != null && parentId != i)
                    ? frames[parentId]
                    : RootFrame;
                if (frame.Parent == null)
                {
                    parentFrame.AddChild(frame);
                }

                if (i < skelIds.Length)
                {
                    var fullFramePath = frame.Path;
                    bonePathHash[skelIds[i]] = fullFramePath;
                    RegisterPathAndSuffixHashes(fullFramePath, fullFramePath, overwriteExact: true);
                }
            }

            // Register additional Avatar m_TOS hashes (e.g. non-skeleton transform paths used by animation bindings)
            if (avatar?.m_TOS != null)
            {
                for (int i = 0; i < avatar.m_TOS.Length; i++)
                {
                    var entry = avatar.m_TOS[i];
                    if (entry.Key == 0 || string.IsNullOrEmpty(entry.Value) || bonePathHash.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    var matched = RootFrame.FindRelativeFrameWithPath(entry.Value) ?? RootFrame.FindFrameByPath(entry.Value);
                    if (matched != null)
                    {
                        bonePathHash[entry.Key] = matched.Path;
                    }
                }
            }
        }

        private string? GetPathByChannelName(string channelName)
        {
            foreach (var morph in MorphList)
            {
                foreach (var channel in morph.Channels)
                {
                    if (channel.Name == channelName)
                    {
                        return morph.Path;
                    }
                }
            }
            return null;
        }

        private string? GetChannelNameFromHash(uint attribute)
        {
            if (morphChannelNames.TryGetValue(attribute, out var name))
            {
                return name;
            }
            else
            {
                return null;
            }
        }

        private Avatar? FindAvatar(GameObject? go)
        {
            if (go == null) return null;
            if (go.m_Animator != null && go.m_Animator.m_Avatar != null && go.m_Animator.m_Avatar.TryGet(out var av1))
                return av1;

            var trans = go.m_Transform;
            if (trans != null)
            {
                var visited = new HashSet<Transform>();
                var queue = new Queue<Transform>();
                queue.Enqueue(trans);
                visited.Add(trans);
                while (queue.Count > 0)
                {
                    var curr = queue.Dequeue();
                    if (curr.m_GameObject.TryGet(out var childGo) && childGo.m_Animator != null)
                    {
                        if (childGo.m_Animator.m_Avatar != null && childGo.m_Animator.m_Avatar.TryGet(out var av2))
                            return av2;
                    }
                    foreach (var childPtr in curr.m_Children)
                    {
                        if (childPtr.TryGet(out var childTrans) && visited.Add(childTrans))
                            queue.Enqueue(childTrans);
                    }
                }
            }

            if (go.assetsFile?.assetsManager != null)
            {
                // Match ONLY via genuine skeleton bone-hash overlap or explicit non-contradicting name match.
                // NEVER fall back blindly to an unrelated Avatar in the bundle.
                var cleanName = go.m_Name.Replace("_Model", "").Replace("Model", "").Trim();
                int nonZeroHierarchyHashes = 0;
                foreach (var key in bonePathHash.Keys)
                {
                    if (key != 0) nonZeroHierarchyHashes++;
                }

                Avatar? bestByBoneOverlap = null;
                int bestOverlap = 0;
                Avatar? bestByName = null;

                foreach (var sf in go.assetsFile.assetsManager.assetsFileList)
                {
                    foreach (var obj in sf.Objects)
                    {
                        if (obj is not Avatar candidate)
                        {
                            continue;
                        }

                        int overlap = CountAvatarBoneHashOverlap(candidate);
                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            bestByBoneOverlap = candidate;
                        }

                        if (bestByName == null
                            && cleanName.Length >= 3
                            && IsStrongAvatarNameMatch(candidate.m_Name, cleanName)
                            && (nonZeroHierarchyHashes == 0 || overlap > 0))
                        {
                            bestByName = candidate;
                        }
                    }
                }

                int minRequiredOverlap = nonZeroHierarchyHashes >= 3 ? 3 : Math.Max(1, nonZeroHierarchyHashes);
                if (bestByBoneOverlap != null && bestOverlap >= minRequiredOverlap)
                {
                    return bestByBoneOverlap;
                }

                if (bestByName != null)
                {
                    return bestByName;
                }
            }

            return null;
        }

        private int CountAvatarBoneHashOverlap(Avatar candidate)
        {
            if (bonePathHash.Count == 0)
            {
                return 0;
            }

            int overlap = 0;
            var counted = new HashSet<uint>();
            var skelIds = candidate.m_Avatar?.m_AvatarSkeleton?.m_ID;
            if (skelIds != null)
            {
                for (int i = 0; i < skelIds.Length; i++)
                {
                    uint id = skelIds[i];
                    if (id != 0 && counted.Add(id) && bonePathHash.ContainsKey(id))
                    {
                        overlap++;
                    }
                }
            }

            if (candidate.m_TOS != null)
            {
                for (int i = 0; i < candidate.m_TOS.Length; i++)
                {
                    uint id = candidate.m_TOS[i].Key;
                    if (id != 0 && counted.Add(id) && bonePathHash.ContainsKey(id))
                    {
                        overlap++;
                    }
                }
            }

            return overlap;
        }

        private static bool IsStrongAvatarNameMatch(string? avatarName, string cleanName)
        {
            if (string.IsNullOrWhiteSpace(avatarName))
            {
                return false;
            }
            var trimmed = avatarName.Trim();
            if (string.Equals(trimmed, cleanName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (trimmed.EndsWith("Avatar", StringComparison.OrdinalIgnoreCase))
            {
                var stem = trimmed.Substring(0, trimmed.Length - "Avatar".Length).TrimEnd('_', ' ', '-');
                if (string.Equals(stem, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadHumanoidMuscleCurves(ImportedKeyframedAnimation iAnim, AnimationClip animationClip)
        {
            if (avatar == null)
            {
                return;
            }

            var decodedTracks = HumanoidMuscleDecoder.DecodeHumanoidClip(animationClip, avatar);
            if (decodedTracks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < decodedTracks.Count; i++)
            {
                var decoded = decodedTracks[i];
                if (string.IsNullOrEmpty(decoded.BonePath))
                {
                    continue;
                }

                string? resolvedPath = null;
                var skelIds = avatar.m_Avatar?.m_AvatarSkeleton?.m_ID;
                if (skelIds != null && decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skelIds.Length)
                {
                    if (bonePathHash.TryGetValue(skelIds[decoded.AvatarNodeIndex], out var hashedPath)
                        && !string.IsNullOrEmpty(hashedPath))
                    {
                        resolvedPath = FixBonePath(animationClip, hashedPath);
                    }
                }

                string fixedPath = resolvedPath ?? FixBonePath(animationClip, decoded.BonePath) ?? decoded.BonePath;
                var track = iAnim.FindTrack(fixedPath);

                if (track.Translations.Count == 0 && decoded.Translations.Count > 0)
                {
                    for (int k = 0; k < decoded.Translations.Count; k++)
                    {
                        var kf = decoded.Translations[k];
                        track.Translations.Add(new ImportedKeyframe<Vector3>(
                            kf.time,
                            new Vector3(-kf.value.X, kf.value.Y, kf.value.Z),
                            kf.isStepped));
                    }
                }

                if (track.Rotations.Count == 0 && decoded.Rotations.Count > 0)
                {
                    for (int k = 0; k < decoded.Rotations.Count; k++)
                    {
                        var kf = decoded.Rotations[k];
                        var qFbx = new Quaternion(kf.value.X, -kf.value.Y, -kf.value.Z, kf.value.W);
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(
                            kf.time,
                            QuaternionToEuler(qFbx),
                            kf.isStepped));
                    }
                }
            }
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

        public void CombineMeshes(string? combinedMeshName = null)
        {
            if (MeshList == null || MeshList.Count <= 1)
            {
                return;
            }

            string? effectiveName = combinedMeshName;
            if (string.IsNullOrWhiteSpace(effectiveName) && hasRootFrame && RootFrame != null && HasSkinnedMeshesWithRootAsBoneAncestor(MeshList, RootFrame))
            {
                effectiveName = "CombinedMesh";
            }

            ImportedFrame? targetFrame = RootFrame;
            if (RootFrame != null && !string.IsNullOrWhiteSpace(effectiveName))
            {
                var normalizedRootName = RootFrame.Name ?? string.Empty;
                var normalizedRootPath = NormalizeFramePath(RootFrame.Path);
                var normalizedTarget = NormalizeFramePath(effectiveName);
                if (!string.Equals(normalizedRootName, effectiveName, StringComparison.Ordinal)
                    && !string.Equals(normalizedRootPath, normalizedTarget, StringComparison.Ordinal))
                {
                    var existing = FindFrameForMesh(RootFrame, effectiveName);
                    if (existing != null && !IsFrameAncestorOfAnyMeshBone(existing, MeshList, RootFrame))
                    {
                        targetFrame = existing;
                    }
                    else
                    {
                        targetFrame = GetOrCreateDedicatedCombinedMeshFrame(RootFrame, effectiveName);
                    }
                }
            }

            var combinedMesh = CombineMeshesInternal(
                MeshList,
                RootFrame,
                targetFrame,
                effectiveName,
                MorphList,
                AnimationList,
                out var combinedMorphs,
                out var updatedAnimations,
                cloneAnimationsForView: false);

            MeshList = new List<ImportedMesh> { combinedMesh };
            MorphList = combinedMorphs;
            AnimationList = updatedAnimations;
        }

        public static ImportedMesh CombineMeshList(IReadOnlyList<ImportedMesh>? meshes, ImportedFrame? rootFrame, string? combinedName = null)
        {
            if (meshes == null || meshes.Count == 0)
            {
                return new ImportedMesh
                {
                    Path = !string.IsNullOrWhiteSpace(combinedName)
                        ? combinedName
                        : !string.IsNullOrWhiteSpace(rootFrame?.Path)
                            ? rootFrame.Path
                            : "CombinedMesh",
                    VertexList = new List<ImportedVertex>(),
                    SubmeshList = new List<ImportedSubmesh>(),
                    hasUV = new bool[8]
                };
            }

            ImportedFrame? targetFrame = rootFrame;
            if (rootFrame != null && !string.IsNullOrWhiteSpace(combinedName))
            {
                var existing = FindFrameForMesh(rootFrame, combinedName);
                if (existing != null)
                {
                    targetFrame = existing;
                }
            }

            return CombineMeshesInternal(
                meshes,
                rootFrame,
                targetFrame,
                combinedName,
                morphList: null,
                animationList: null,
                out _,
                out _,
                cloneAnimationsForView: false);
        }

        [return: NotNullIfNotNull(nameof(imported))]
        public static IImported? CreateCombinedView(IImported? imported, string? combinedMeshName = null)
        {
            if (imported == null || imported.MeshList == null || imported.MeshList.Count <= 1)
            {
                return imported;
            }

            string? effectiveName = combinedMeshName;
            bool needsDedicatedChild = imported.RootFrame != null
                && HasSkinnedMeshesWithRootAsBoneAncestor(imported.MeshList, imported.RootFrame);
            if (string.IsNullOrWhiteSpace(effectiveName) && needsDedicatedChild)
            {
                effectiveName = "CombinedMesh";
            }

            ImportedFrame? targetFrame = imported.RootFrame;
            if (imported.RootFrame != null && !string.IsNullOrWhiteSpace(effectiveName))
            {
                var existing = FindFrameForMesh(imported.RootFrame, effectiveName);
                if (existing != null && !IsFrameAncestorOfAnyMeshBone(existing, imported.MeshList, imported.RootFrame))
                {
                    targetFrame = existing;
                }
                else if (needsDedicatedChild
                    || (!string.Equals(imported.RootFrame.Name, effectiveName, StringComparison.Ordinal)
                        && !string.Equals(NormalizeFramePath(imported.RootFrame.Path), NormalizeFramePath(effectiveName), StringComparison.Ordinal)))
                {
                    targetFrame = GetOrCreateDedicatedCombinedMeshFrame(imported.RootFrame, effectiveName);
                }
            }

            var combinedMesh = CombineMeshesInternal(
                imported.MeshList,
                imported.RootFrame,
                targetFrame,
                effectiveName,
                imported.MorphList,
                imported.AnimationList,
                out var combinedMorphs,
                out var updatedAnimations,
                cloneAnimationsForView: true);

            return new ImportedWrapper(imported)
            {
                MeshList = new List<ImportedMesh> { combinedMesh },
                MorphList = combinedMorphs,
                AnimationList = updatedAnimations
            };
        }

        private static bool HasSkinnedMeshesWithRootAsBoneAncestor(IReadOnlyList<ImportedMesh> meshes, ImportedFrame rootFrame)
        {
            return IsFrameAncestorOfAnyMeshBone(rootFrame, meshes, rootFrame);
        }

        private static bool IsFrameAncestorOfAnyMeshBone(ImportedFrame candidateAncestor, IReadOnlyList<ImportedMesh> meshes, ImportedFrame rootFrame)
        {
            if (candidateAncestor == null || meshes == null)
            {
                return false;
            }

            var ancestorPath = NormalizeFramePath(candidateAncestor.Path);
            for (int m = 0; m < meshes.Count; m++)
            {
                var boneList = meshes[m]?.BoneList;
                if (boneList == null) continue;
                for (int b = 0; b < boneList.Count; b++)
                {
                    var bonePath = NormalizeFramePath(boneList[b]?.Path);
                    if (string.IsNullOrEmpty(bonePath)) continue;
                    var boneFrame = FindFrameForMesh(rootFrame, bonePath);
                    if (boneFrame != null)
                    {
                        var cur = boneFrame.Parent;
                        while (cur != null)
                        {
                            if (ReferenceEquals(cur, candidateAncestor))
                            {
                                return true;
                            }
                            cur = cur.Parent;
                        }
                    }
                    else if (!string.IsNullOrEmpty(ancestorPath)
                        && bonePath.Length > ancestorPath.Length
                        && bonePath[ancestorPath.Length] == '/'
                        && bonePath.StartsWith(ancestorPath, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static ImportedFrame GetOrCreateDedicatedCombinedMeshFrame(ImportedFrame rootFrame, string preferredName)
        {
            var leafName = preferredName;
            int lastSlash = leafName.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash + 1 < leafName.Length)
            {
                leafName = leafName.Substring(lastSlash + 1);
            }
            if (string.IsNullOrWhiteSpace(leafName) || string.Equals(leafName, rootFrame.Name, StringComparison.Ordinal))
            {
                leafName = "CombinedMesh";
            }

            var existingChild = rootFrame.FindChild(leafName, recursive: false);
            if (existingChild != null && existingChild.Count == 0)
            {
                return existingChild;
            }

            string uniqueName = leafName;
            for (int i = 1; rootFrame.FindChild(uniqueName, recursive: false) != null; i++)
            {
                uniqueName = $"{leafName}_{i}";
            }

            var childFrame = CreateFrame(uniqueName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            rootFrame.AddChild(childFrame);
            return childFrame;
        }

        private readonly struct CombinedPartTransformMeta
        {
            public CombinedPartTransformMeta(
                string normalizedPath,
                int vertexOffset,
                int vertexCount,
                System.Numerics.Matrix4x4 relativeMatrix,
                System.Numerics.Matrix4x4 normalMatrix)
            {
                NormalizedPath = normalizedPath;
                VertexOffset = vertexOffset;
                VertexCount = vertexCount;
                RelativeMatrix = relativeMatrix;
                NormalMatrix = normalMatrix;
            }

            public string NormalizedPath { get; }
            public int VertexOffset { get; }
            public int VertexCount { get; }
            public System.Numerics.Matrix4x4 RelativeMatrix { get; }
            public System.Numerics.Matrix4x4 NormalMatrix { get; }
        }

        private static ImportedMesh CombineMeshesInternal(
            IReadOnlyList<ImportedMesh> meshes,
            ImportedFrame? rootFrame,
            ImportedFrame? targetFrame,
            string? combinedName,
            List<ImportedMorph>? morphList,
            List<ImportedKeyframedAnimation>? animationList,
            out List<ImportedMorph> combinedMorphs,
            out List<ImportedKeyframedAnimation> updatedAnimations,
            bool cloneAnimationsForView)
        {
            int totalVertices = 0;
            int totalSubmeshes = 0;
            bool anyNormal = false;
            bool anyTangent = false;
            bool anyColor = false;
            var anyUV = new bool[8];
            string? firstNonEmptyMeshPath = null;

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh == null) continue;

                if (firstNonEmptyMeshPath == null && !string.IsNullOrWhiteSpace(mesh.Path))
                {
                    firstNonEmptyMeshPath = mesh.Path;
                }

                totalVertices += mesh.VertexList?.Count ?? 0;
                totalSubmeshes += mesh.SubmeshList?.Count ?? 0;
                if (mesh.hasNormal) anyNormal = true;
                if (mesh.hasTangent) anyTangent = true;
                if (mesh.hasColor) anyColor = true;
                if (mesh.hasUV != null)
                {
                    int uvLen = Math.Min(8, mesh.hasUV.Length);
                    for (int u = 0; u < uvLen; u++)
                    {
                        if (mesh.hasUV[u]) anyUV[u] = true;
                    }
                }
            }

            string targetPath;
            if (targetFrame != null && !string.IsNullOrWhiteSpace(targetFrame.Path))
            {
                if (!string.IsNullOrWhiteSpace(combinedName)
                    && !string.Equals(targetFrame.Name, combinedName, StringComparison.Ordinal)
                    && !string.Equals(NormalizeFramePath(targetFrame.Path), NormalizeFramePath(combinedName), StringComparison.Ordinal))
                {
                    targetPath = combinedName;
                }
                else
                {
                    targetPath = targetFrame.Path;
                }
            }
            else if (!string.IsNullOrWhiteSpace(combinedName))
            {
                targetPath = combinedName;
            }
            else
            {
                targetPath = firstNonEmptyMeshPath ?? "CombinedMesh";
            }

            var unifiedBones = new List<ImportedBone>();
            var boneIndexByNormalizedPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var perMeshBoneRemaps = new int[meshes.Count][];
            var partMetas = new List<CombinedPartTransformMeta>(meshes.Count);
            var partRelMatrices = new System.Numerics.Matrix4x4[meshes.Count];
            var partNormalMatrices = new System.Numerics.Matrix4x4[meshes.Count];
            var partFlipWinding = new bool[meshes.Count];

            // Precompute relative transform matrices and unified bone table
            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh == null) continue;

                var meshFrame = FindFrameForMesh(rootFrame, mesh.Path);
                var relMatrix = ComputeRelativeFrameMatrix(meshFrame, targetFrame);
                var normalMatrix = BuildNormalMatrix(relMatrix, out var flipWinding);
                partRelMatrices[m] = relMatrix;
                partNormalMatrices[m] = normalMatrix;
                partFlipWinding[m] = flipWinding;

                if (mesh.BoneList != null && mesh.BoneList.Count > 0)
                {
                    var remap = new int[mesh.BoneList.Count];
                    for (int b = 0; b < mesh.BoneList.Count; b++)
                    {
                        var bone = mesh.BoneList[b];
                        var normBonePath = NormalizeFramePath(bone?.Path);
                        if (!string.IsNullOrEmpty(normBonePath) && boneIndexByNormalizedPath.TryGetValue(normBonePath, out var existingIdx))
                        {
                            remap[b] = existingIdx;
                        }
                        else
                        {
                            var newIdx = unifiedBones.Count;
                            var adjustedBindPose = bone != null
                                ? TransformBindPoseMatrix(bone.Matrix, relMatrix)
                                : default(Matrix4x4);
                            unifiedBones.Add(new ImportedBone
                            {
                                Path = bone?.Path,
                                Matrix = adjustedBindPose
                            });
                            if (!string.IsNullOrEmpty(normBonePath))
                            {
                                boneIndexByNormalizedPath[normBonePath] = newIdx;
                            }
                            remap[b] = newIdx;
                        }
                    }
                    perMeshBoneRemaps[m] = remap;
                }
            }

            bool hasAnySkinning = unifiedBones.Count > 0;
            var rigidMeshBoneIndices = new int[meshes.Count];
            for (int m = 0; m < meshes.Count; m++)
            {
                rigidMeshBoneIndices[m] = -1;
            }

            if (hasAnySkinning)
            {
                var targetGlobal = targetFrame != null
                    ? ComputeFrameGlobalMatrix(targetFrame)
                    : System.Numerics.Matrix4x4.Identity;

                for (int m = 0; m < meshes.Count; m++)
                {
                    var mesh = meshes[m];
                    if (mesh == null || (perMeshBoneRemaps[m] != null && perMeshBoneRemaps[m].Length > 0))
                    {
                        continue;
                    }

                    var meshFrame = FindFrameForMesh(rootFrame, mesh.Path);
                    if (meshFrame == null)
                    {
                        continue;
                    }

                    // 1. Walk up from meshFrame to find the nearest skeleton ancestor bone in unifiedBones
                    int foundBoneIdx = -1;
                    var cur = meshFrame;
                    var visited = new HashSet<ImportedFrame>();
                    while (cur != null && !ReferenceEquals(cur, rootFrame) && visited.Add(cur))
                    {
                        var normCurPath = NormalizeFramePath(cur.Path);
                        if (!string.IsNullOrEmpty(normCurPath) && boneIndexByNormalizedPath.TryGetValue(normCurPath, out var existingBoneIdx))
                        {
                            foundBoneIdx = existingBoneIdx;
                            break;
                        }
                        cur = cur.Parent;
                    }

                    // 2. If no ancestor is in unifiedBones yet, add meshFrame itself as a rigid bone when it is a
                    // non-mirrored child in a full model hierarchy (e.g., Shield / accessory under character sub-root)
                    if (foundBoneIdx < 0
                        && !partFlipWinding[m]
                        && !ReferenceEquals(meshFrame, rootFrame)
                        && (!ReferenceEquals(meshFrame.Parent, rootFrame) || animationList != null || morphList != null))
                    {
                        var boneGlobal = ComputeFrameGlobalMatrix(meshFrame);
                        if (System.Numerics.Matrix4x4.Invert(boneGlobal, out var invBoneGlobal))
                        {
                            var normMeshPath = NormalizeFramePath(meshFrame.Path);
                            var bindPose = FromNumericsMatrix(targetGlobal * invBoneGlobal);
                            foundBoneIdx = unifiedBones.Count;
                            unifiedBones.Add(new ImportedBone
                            {
                                Path = meshFrame.Path,
                                Matrix = bindPose
                            });
                            if (!string.IsNullOrEmpty(normMeshPath))
                            {
                                boneIndexByNormalizedPath[normMeshPath] = foundBoneIdx;
                            }
                        }
                    }

                    rigidMeshBoneIndices[m] = foundBoneIdx;
                }
            }

            var combinedVertices = new List<ImportedVertex>(totalVertices);
            var combinedSubmeshes = new List<ImportedSubmesh>(totalSubmeshes);

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh?.VertexList == null || mesh.VertexList.Count == 0)
                {
                    continue;
                }

                int vertexOffset = combinedVertices.Count;
                int srcVertCount = mesh.VertexList.Count;
                var relMatrix = partRelMatrices[m];
                var normalMatrix = partNormalMatrices[m];
                bool flipWinding = partFlipWinding[m];
                var boneRemap = perMeshBoneRemaps[m];
                bool meshHasSkinning = boneRemap != null && boneRemap.Length > 0;
                int rigidBoneIdx = rigidMeshBoneIndices[m];

                partMetas.Add(new CombinedPartTransformMeta(
                    NormalizeFramePath(mesh.Path),
                    vertexOffset,
                    srcVertCount,
                    relMatrix,
                    normalMatrix));

                for (int v = 0; v < srcVertCount; v++)
                {
                    var src = mesh.VertexList[v];
                    var dst = new ImportedVertex();

                    var transformedPos = System.Numerics.Vector3.Transform(
                        new System.Numerics.Vector3(src.Vertex.X, src.Vertex.Y, src.Vertex.Z),
                        relMatrix);
                    dst.Vertex = new Vector3(
                        SanitizeFloat(transformedPos.X, 0f),
                        SanitizeFloat(transformedPos.Y, 0f),
                        SanitizeFloat(transformedPos.Z, 0f));

                    if (anyNormal)
                    {
                        dst.Normal = mesh.hasNormal
                            ? TransformDirectionNormalized(src.Normal, normalMatrix)
                            : new Vector3(0f, 1f, 0f);
                    }

                    dst.UV = new float[8][];
                    for (int u = 0; u < 8; u++)
                    {
                        if (!anyUV[u]) continue;
                        if (mesh.hasUV != null && u < mesh.hasUV.Length && mesh.hasUV[u]
                            && src.UV != null && u < src.UV.Length && src.UV[u] != null && src.UV[u].Length >= 2)
                        {
                            dst.UV[u] = new[]
                            {
                                SanitizeFloat(src.UV[u][0], 0f),
                                SanitizeFloat(src.UV[u][1], 0f)
                            };
                        }
                        else
                        {
                            dst.UV[u] = new[] { 0f, 0f };
                        }
                    }

                    if (anyTangent)
                    {
                        if (mesh.hasTangent)
                        {
                            var transformedTangent = TransformDirectionNormalized(
                                new Vector3(src.Tangent.X, src.Tangent.Y, src.Tangent.Z),
                                relMatrix);
                            float tangentW = SanitizeFloat(src.Tangent.W, 1f);
                            dst.Tangent = new Vector4(
                                transformedTangent.X,
                                transformedTangent.Y,
                                transformedTangent.Z,
                                flipWinding ? -tangentW : tangentW);
                        }
                        else
                        {
                            dst.Tangent = new Vector4(1f, 0f, 0f, 1f);
                        }
                    }

                    if (anyColor)
                    {
                        dst.Color = mesh.hasColor
                            ? src.Color
                            : new Color(1f, 1f, 1f, 1f);
                    }

                    if (hasAnySkinning)
                    {
                        if (meshHasSkinning && boneRemap != null && src.BoneIndices != null && src.Weights != null)
                        {
                            int rawCount = Math.Min(src.BoneIndices.Length, src.Weights.Length);
                            int slotCount = Math.Max(4, rawCount);
                            dst.BoneIndices = new int[slotCount];
                            dst.Weights = new float[slotCount];
                            for (int k = 0; k < rawCount; k++)
                            {
                                int oldBoneIdx = src.BoneIndices[k];
                                float weight = SanitizeFloat(src.Weights[k], 0f);
                                if (oldBoneIdx >= 0 && oldBoneIdx < boneRemap.Length)
                                {
                                    dst.BoneIndices[k] = boneRemap[oldBoneIdx];
                                    dst.Weights[k] = weight;
                                }
                            }
                        }
                        else
                        {
                            dst.BoneIndices = new int[4];
                            dst.Weights = new float[4];
                            if (rigidBoneIdx >= 0)
                            {
                                dst.BoneIndices[0] = rigidBoneIdx;
                                dst.Weights[0] = 1.0f;
                            }
                        }
                    }

                    combinedVertices.Add(dst);
                }

                if (mesh.SubmeshList != null)
                {
                    for (int s = 0; s < mesh.SubmeshList.Count; s++)
                    {
                        var srcSub = mesh.SubmeshList[s];
                        if (srcSub?.FaceList == null) continue;

                        var dstSub = new ImportedSubmesh
                        {
                            Material = srcSub.Material,
                            BaseVertex = 0,
                            FaceList = new List<ImportedFace>(srcSub.FaceList.Count)
                        };

                        for (int f = 0; f < srcSub.FaceList.Count; f++)
                        {
                            var face = srcSub.FaceList[f];
                            if (!TryResolveCombinedFaceIndices(face, srcSub.BaseVertex, srcVertCount, out int i0, out int i1, out int i2))
                            {
                                continue;
                            }

                            var newFace = new ImportedFace
                            {
                                VertexIndices = flipWinding
                                    ? new[] { i0 + vertexOffset, i2 + vertexOffset, i1 + vertexOffset }
                                    : new[] { i0 + vertexOffset, i1 + vertexOffset, i2 + vertexOffset }
                            };
                            dstSub.FaceList.Add(newFace);
                        }

                        combinedSubmeshes.Add(dstSub);
                    }
                }
            }

            var combinedMesh = new ImportedMesh
            {
                Path = targetPath,
                VertexList = combinedVertices,
                SubmeshList = combinedSubmeshes,
                BoneList = hasAnySkinning ? unifiedBones : null,
                hasNormal = anyNormal,
                hasUV = anyUV,
                hasTangent = anyTangent,
                hasColor = anyColor
            };

            combinedMorphs = MergeMorphsForCombinedMesh(morphList, partMetas, targetPath);
            updatedAnimations = RemapBlendShapeAnimationTracks(animationList, partMetas, targetPath, cloneAnimationsForView);

            return combinedMesh;
        }

        private static List<ImportedMorph> MergeMorphsForCombinedMesh(
            List<ImportedMorph>? morphList,
            List<CombinedPartTransformMeta> partMetas,
            string combinedMeshPath)
        {
            if (morphList == null || morphList.Count == 0)
            {
                return morphList ?? new List<ImportedMorph>();
            }

            var metaByPath = new Dictionary<string, List<CombinedPartTransformMeta>>(StringComparer.Ordinal);
            for (int i = 0; i < partMetas.Count; i++)
            {
                var key = partMetas[i].NormalizedPath ?? string.Empty;
                if (!metaByPath.TryGetValue(key, out var list))
                {
                    list = new List<CombinedPartTransformMeta>(1);
                    metaByPath[key] = list;
                }
                list.Add(partMetas[i]);
            }

            var consumedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var remainingMorphs = new List<ImportedMorph>();
            var mergedChannels = new List<ImportedMorphChannel>();
            var channelByName = new Dictionary<string, ImportedMorphChannel>(StringComparer.Ordinal);

            for (int i = 0; i < morphList.Count; i++)
            {
                var morph = morphList[i];
                if (morph == null) continue;

                var normPath = NormalizeFramePath(morph.Path) ?? string.Empty;
                if (!metaByPath.TryGetValue(normPath, out var candidates) || candidates.Count == 0)
                {
                    remainingMorphs.Add(morph);
                    continue;
                }

                consumedCounts.TryGetValue(normPath, out var useIndex);
                var meta = candidates[Math.Min(useIndex, candidates.Count - 1)];
                consumedCounts[normPath] = useIndex + 1;

                if (morph.Channels == null) continue;

                for (int c = 0; c < morph.Channels.Count; c++)
                {
                    var srcChannel = morph.Channels[c];
                    if (srcChannel == null) continue;

                    var channelName = srcChannel.Name ?? string.Empty;
                    if (!channelByName.TryGetValue(channelName, out var dstChannel))
                    {
                        dstChannel = new ImportedMorphChannel
                        {
                            Name = channelName,
                            KeyframeList = new List<ImportedMorphKeyframe>()
                        };
                        channelByName[channelName] = dstChannel;
                        mergedChannels.Add(dstChannel);
                    }

                    if (srcChannel.KeyframeList == null) continue;

                    for (int k = 0; k < srcChannel.KeyframeList.Count; k++)
                    {
                        var srcKf = srcChannel.KeyframeList[k];
                        if (srcKf == null) continue;

                        ImportedMorphKeyframe? dstKf = null;
                        for (int ek = 0; ek < dstChannel.KeyframeList.Count; ek++)
                        {
                            if (Math.Abs(dstChannel.KeyframeList[ek].Weight - srcKf.Weight) <= 1e-4f)
                            {
                                dstKf = dstChannel.KeyframeList[ek];
                                break;
                            }
                        }

                        if (dstKf == null)
                        {
                            dstKf = new ImportedMorphKeyframe
                            {
                                Weight = srcKf.Weight,
                                hasNormals = srcKf.hasNormals,
                                hasTangents = srcKf.hasTangents,
                                VertexList = new List<ImportedMorphVertex>(srcKf.VertexList?.Count ?? 0)
                            };
                            dstChannel.KeyframeList.Add(dstKf);
                        }
                        else
                        {
                            dstKf.hasNormals |= srcKf.hasNormals;
                            dstKf.hasTangents |= srcKf.hasTangents;
                        }

                        if (srcKf.VertexList == null) continue;

                        for (int v = 0; v < srcKf.VertexList.Count; v++)
                        {
                            var mv = srcKf.VertexList[v];
                            if (mv?.Vertex == null || mv.Index >= (uint)meta.VertexCount)
                            {
                                continue;
                            }

                            var transformedPos = System.Numerics.Vector3.Transform(
                                new System.Numerics.Vector3(mv.Vertex.Vertex.X, mv.Vertex.Vertex.Y, mv.Vertex.Vertex.Z),
                                meta.RelativeMatrix);

                            var dstVert = new ImportedVertex
                            {
                                Vertex = new Vector3(
                                    SanitizeFloat(transformedPos.X, 0f),
                                    SanitizeFloat(transformedPos.Y, 0f),
                                    SanitizeFloat(transformedPos.Z, 0f))
                            };

                            if (srcKf.hasNormals)
                            {
                                var transformedNormal = System.Numerics.Vector3.TransformNormal(
                                    new System.Numerics.Vector3(mv.Vertex.Normal.X, mv.Vertex.Normal.Y, mv.Vertex.Normal.Z),
                                    meta.NormalMatrix);
                                dstVert.Normal = new Vector3(
                                    SanitizeFloat(transformedNormal.X, 0f),
                                    SanitizeFloat(transformedNormal.Y, 0f),
                                    SanitizeFloat(transformedNormal.Z, 0f));
                            }

                            if (srcKf.hasTangents)
                            {
                                var transformedTangent = System.Numerics.Vector3.TransformNormal(
                                    new System.Numerics.Vector3(mv.Vertex.Tangent.X, mv.Vertex.Tangent.Y, mv.Vertex.Tangent.Z),
                                    meta.NormalMatrix);
                                dstVert.Tangent = new Vector4(
                                    SanitizeFloat(transformedTangent.X, 0f),
                                    SanitizeFloat(transformedTangent.Y, 0f),
                                    SanitizeFloat(transformedTangent.Z, 0f),
                                    SanitizeFloat(mv.Vertex.Tangent.W, 0f));
                            }

                            dstKf.VertexList.Add(new ImportedMorphVertex
                            {
                                Index = mv.Index + (uint)meta.VertexOffset,
                                Vertex = dstVert
                            });
                        }
                    }
                }
            }

            if (mergedChannels.Count > 0)
            {
                remainingMorphs.Add(new ImportedMorph
                {
                    Path = combinedMeshPath,
                    Channels = mergedChannels
                });
            }

            return remainingMorphs;
        }

        private static List<ImportedKeyframedAnimation> RemapBlendShapeAnimationTracks(
            List<ImportedKeyframedAnimation>? animationList,
            List<CombinedPartTransformMeta> partMetas,
            string combinedMeshPath,
            bool cloneForView)
        {
            if (animationList == null || animationList.Count == 0 || string.IsNullOrEmpty(combinedMeshPath))
            {
                return animationList ?? new List<ImportedKeyframedAnimation>();
            }

            var combinedNormalizedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < partMetas.Count; i++)
            {
                if (!string.IsNullOrEmpty(partMetas[i].NormalizedPath))
                {
                    combinedNormalizedPaths.Add(partMetas[i].NormalizedPath);
                }
            }

            var normalizedTargetPath = NormalizeFramePath(combinedMeshPath);
            bool anyRemapNeeded = false;
            for (int a = 0; a < animationList.Count && !anyRemapNeeded; a++)
            {
                var tracks = animationList[a]?.TrackList;
                if (tracks == null) continue;
                for (int t = 0; t < tracks.Count; t++)
                {
                    var tr = tracks[t];
                    if (tr?.BlendShapes != null && tr.BlendShapes.Count > 0)
                    {
                        var normTrackPath = NormalizeFramePath(tr.Path);
                        if (!string.IsNullOrEmpty(normTrackPath)
                            && combinedNormalizedPaths.Contains(normTrackPath)
                            && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                        {
                            anyRemapNeeded = true;
                            break;
                        }
                    }
                }
            }

            if (!anyRemapNeeded)
            {
                return animationList;
            }

            var result = cloneForView ? new List<ImportedKeyframedAnimation>(animationList.Count) : animationList;
            for (int a = 0; a < animationList.Count; a++)
            {
                var srcAnim = animationList[a];
                if (srcAnim == null)
                {
                    continue;
                }

                var dstAnim = srcAnim;
                if (cloneForView)
                {
                    dstAnim = new ImportedKeyframedAnimation
                    {
                        Name = srcAnim.Name,
                        SampleRate = srcAnim.SampleRate,
                        TrackList = new List<ImportedAnimationKeyframedTrack>(srcAnim.TrackList?.Count ?? 0)
                    };
                    if (srcAnim.TrackList != null)
                    {
                        for (int t = 0; t < srcAnim.TrackList.Count; t++)
                        {
                            var tr = srcAnim.TrackList[t];
                            dstAnim.TrackList.Add(new ImportedAnimationKeyframedTrack
                            {
                                Path = tr.Path,
                                Translations = tr.Translations,
                                Rotations = tr.Rotations,
                                Scalings = tr.Scalings,
                                Visibilities = tr.Visibilities,
                                BlendShapes = tr.BlendShapes != null
                                    ? new List<ImportedBlendShape>(tr.BlendShapes)
                                    : new List<ImportedBlendShape>()
                            });
                        }
                    }
                    result.Add(dstAnim);
                }

                if (dstAnim.TrackList == null) continue;

                int trackCount = dstAnim.TrackList.Count;
                for (int t = 0; t < trackCount; t++)
                {
                    var tr = dstAnim.TrackList[t];
                    if (tr?.BlendShapes == null || tr.BlendShapes.Count == 0) continue;

                    var normTrackPath = NormalizeFramePath(tr.Path);
                    if (!string.IsNullOrEmpty(normTrackPath)
                        && combinedNormalizedPaths.Contains(normTrackPath)
                        && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                    {
                        var targetTrack = dstAnim.FindTrack(combinedMeshPath);
                        for (int b = 0; b < tr.BlendShapes.Count; b++)
                        {
                            var srcBs = tr.BlendShapes[b];
                            if (srcBs == null) continue;
                            var dstBs = targetTrack.FindOrCreateBlendShape(NormalizeBlendShapeChannelName(srcBs.ChannelName));
                            if (dstBs.Keyframes.Count == 0 && srcBs.Keyframes.Count > 0)
                            {
                                dstBs.Keyframes.AddRange(srcBs.Keyframes);
                            }
                        }
                        tr.BlendShapes.Clear();
                    }
                }
            }

            return result;
        }

        private static bool TryResolveCombinedFaceIndices(ImportedFace face, int baseVertex, int vertexCount, out int index0, out int index1, out int index2)
        {
            index0 = index1 = index2 = 0;
            if (face?.VertexIndices == null || face.VertexIndices.Length < 3)
                return false;

            index0 = face.VertexIndices[0] + baseVertex;
            index1 = face.VertexIndices[1] + baseVertex;
            index2 = face.VertexIndices[2] + baseVertex;
            if ((uint)index0 < (uint)vertexCount && (uint)index1 < (uint)vertexCount && (uint)index2 < (uint)vertexCount)
                return true;

            index0 = face.VertexIndices[0];
            index1 = face.VertexIndices[1];
            index2 = face.VertexIndices[2];
            return (uint)index0 < (uint)vertexCount && (uint)index1 < (uint)vertexCount && (uint)index2 < (uint)vertexCount;
        }

        private static ImportedFrame? FindFrameForMesh(ImportedFrame? rootFrame, string? meshPath)
        {
            if (rootFrame == null || string.IsNullOrWhiteSpace(meshPath))
            {
                return null;
            }

            var normalizedTarget = NormalizeFramePath(meshPath);
            if (string.IsNullOrEmpty(normalizedTarget))
            {
                return null;
            }

            var visited = new HashSet<ImportedFrame>();
            var stack = new Stack<ImportedFrame>();
            stack.Push(rootFrame);

            ImportedFrame? uniqueSuffixMatch = null;
            int suffixMatchCount = 0;
            var suffix = "/" + normalizedTarget;

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame == null || !visited.Add(frame))
                {
                    continue;
                }

                var framePath = NormalizeFramePath(frame.Path);
                if (string.Equals(framePath, normalizedTarget, StringComparison.Ordinal))
                {
                    return frame;
                }

                if (framePath != null && framePath.EndsWith(suffix, StringComparison.Ordinal))
                {
                    uniqueSuffixMatch = frame;
                    suffixMatchCount++;
                }

                for (int i = frame.Count - 1; i >= 0; i--)
                {
                    stack.Push(frame[i]);
                }
            }

            return suffixMatchCount == 1 ? uniqueSuffixMatch : null;
        }

        private static System.Numerics.Matrix4x4 ComputeRelativeFrameMatrix(ImportedFrame? meshFrame, ImportedFrame? targetFrame)
        {
            if (meshFrame == null || ReferenceEquals(meshFrame, targetFrame))
            {
                return System.Numerics.Matrix4x4.Identity;
            }

            // Fast path: walk up parent chain looking for targetFrame (cycle-safe)
            var rel = System.Numerics.Matrix4x4.Identity;
            var visited = new HashSet<ImportedFrame>();
            var current = meshFrame;
            while (current != null && visited.Add(current))
            {
                rel = rel * BuildImportedFrameLocalMatrix(current);
                if (ReferenceEquals(current.Parent, targetFrame))
                {
                    return rel;
                }
                current = current.Parent;
            }

            // Fallback when targetFrame is not a direct ancestor of meshFrame
            if (targetFrame == null)
            {
                return rel;
            }

            var targetGlobal = ComputeFrameGlobalMatrix(targetFrame);
            return System.Numerics.Matrix4x4.Invert(targetGlobal, out var invTarget)
                ? rel * invTarget
                : rel;
        }

        private static System.Numerics.Matrix4x4 ComputeFrameGlobalMatrix(ImportedFrame frame)
        {
            var global = System.Numerics.Matrix4x4.Identity;
            var visited = new HashSet<ImportedFrame>();
            var current = frame;
            while (current != null && visited.Add(current))
            {
                global = global * BuildImportedFrameLocalMatrix(current);
                current = current.Parent;
            }
            return global;
        }

        private static System.Numerics.Matrix4x4 BuildImportedFrameLocalMatrix(ImportedFrame frame)
        {
            var scale = System.Numerics.Matrix4x4.CreateScale(
                SanitizeFloat(frame.LocalScale.X, 0f),
                SanitizeFloat(frame.LocalScale.Y, 0f),
                SanitizeFloat(frame.LocalScale.Z, 0f));
            var rotation =
                System.Numerics.Matrix4x4.CreateRotationX(DegreesToRadians(frame.LocalRotation.X)) *
                System.Numerics.Matrix4x4.CreateRotationY(DegreesToRadians(frame.LocalRotation.Y)) *
                System.Numerics.Matrix4x4.CreateRotationZ(DegreesToRadians(frame.LocalRotation.Z));
            var translation = System.Numerics.Matrix4x4.CreateTranslation(
                SanitizeFloat(frame.LocalPosition.X, 0f),
                SanitizeFloat(frame.LocalPosition.Y, 0f),
                SanitizeFloat(frame.LocalPosition.Z, 0f));
            return scale * rotation * translation;
        }

        private static System.Numerics.Matrix4x4 BuildNormalMatrix(System.Numerics.Matrix4x4 relMatrix, out bool flipWinding)
        {
            var upper = new System.Numerics.Matrix4x4(
                relMatrix.M11, relMatrix.M12, relMatrix.M13, 0f,
                relMatrix.M21, relMatrix.M22, relMatrix.M23, 0f,
                relMatrix.M31, relMatrix.M32, relMatrix.M33, 0f,
                0f, 0f, 0f, 1f);

            var det = upper.GetDeterminant();
            flipWinding = det < -1e-9f;
            if (Math.Abs(det) > 1e-12f && System.Numerics.Matrix4x4.Invert(upper, out var invUpper))
            {
                return System.Numerics.Matrix4x4.Transpose(invUpper);
            }

            return upper;
        }

        private static Vector3 TransformDirectionNormalized(Vector3 dir, System.Numerics.Matrix4x4 normalMatrix)
        {
            var transformed = System.Numerics.Vector3.TransformNormal(
                new System.Numerics.Vector3(
                    SanitizeFloat(dir.X, 0f),
                    SanitizeFloat(dir.Y, 0f),
                    SanitizeFloat(dir.Z, 0f)),
                normalMatrix);
            var lenSq = transformed.LengthSquared();
            if (lenSq > 1e-12f && !float.IsNaN(lenSq) && !float.IsInfinity(lenSq))
            {
                var invLen = 1f / (float)Math.Sqrt(lenSq);
                return new Vector3(transformed.X * invLen, transformed.Y * invLen, transformed.Z * invLen);
            }

            var origLenSq = dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z;
            if (origLenSq > 1e-12f && !float.IsNaN(origLenSq) && !float.IsInfinity(origLenSq))
            {
                var invLen = 1f / (float)Math.Sqrt(origLenSq);
                return new Vector3(dir.X * invLen, dir.Y * invLen, dir.Z * invLen);
            }

            return Vector3.Zero;
        }

        private static Matrix4x4 TransformBindPoseMatrix(Matrix4x4 bindPose, System.Numerics.Matrix4x4 relMatrix)
        {
            if (relMatrix.IsIdentity)
            {
                return bindPose;
            }

            if (System.Numerics.Matrix4x4.Invert(relMatrix, out var invRel))
            {
                var adjusted = invRel * ToNumericsMatrix(bindPose);
                return FromNumericsMatrix(adjusted);
            }

            return bindPose;
        }

        private static System.Numerics.Matrix4x4 ToNumericsMatrix(Matrix4x4 m)
        {
            return new System.Numerics.Matrix4x4(
                m[0, 0], m[0, 1], m[0, 2], m[0, 3],
                m[1, 0], m[1, 1], m[1, 2], m[1, 3],
                m[2, 0], m[2, 1], m[2, 2], m[2, 3],
                m[3, 0], m[3, 1], m[3, 2], m[3, 3]);
        }

        private static Matrix4x4 FromNumericsMatrix(System.Numerics.Matrix4x4 m)
        {
            var result = new Matrix4x4();
            result[0, 0] = SanitizeFloat(m.M11, 0f); result[0, 1] = SanitizeFloat(m.M12, 0f); result[0, 2] = SanitizeFloat(m.M13, 0f); result[0, 3] = SanitizeFloat(m.M14, 0f);
            result[1, 0] = SanitizeFloat(m.M21, 0f); result[1, 1] = SanitizeFloat(m.M22, 0f); result[1, 2] = SanitizeFloat(m.M23, 0f); result[1, 3] = SanitizeFloat(m.M24, 0f);
            result[2, 0] = SanitizeFloat(m.M31, 0f); result[2, 1] = SanitizeFloat(m.M32, 0f); result[2, 2] = SanitizeFloat(m.M33, 0f); result[2, 3] = SanitizeFloat(m.M34, 0f);
            result[3, 0] = SanitizeFloat(m.M41, 0f); result[3, 1] = SanitizeFloat(m.M42, 0f); result[3, 2] = SanitizeFloat(m.M43, 0f); result[3, 3] = SanitizeFloat(m.M44, 0f);
            return result;
        }

        private static float DegreesToRadians(float degrees)
        {
            return (float)(SanitizeFloat(degrees, 0f) * Math.PI / 180.0);
        }

        private static float SanitizeFloat(float value, float fallback)
        {
            return (float.IsNaN(value) || float.IsInfinity(value)) ? fallback : value;
        }

        private static string NormalizeFramePath(string? path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').Trim('/');
        }
    }
}
