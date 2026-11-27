using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssetStudio
{
    public sealed class GltfExportStatistics
    {
        public int TotalNodes { get; init; }
        public int TotalMeshInstances { get; init; }
        public int UniqueGltfMeshes { get; init; }
        public int UniqueGeometryBuffers { get; init; }
        public int ReusedMeshCount { get; init; }
        public int ReusedGeometryCount { get; init; }
        public int MaterialsCount { get; init; }
        public int TexturesCount { get; init; }
        public int SkinsCount { get; init; }
        public int AnimationsCount { get; init; }
        public int CamerasCount { get; init; }
        public int LightsCount { get; init; }
        public int HiddenNodesCount { get; init; }
        public int ApproximatedMaterialsCount { get; init; }
        public int FallbackMaterialsCount { get; init; }
        public IReadOnlyList<MaterialConversionDiagnostic> MaterialDiagnostics { get; init; } = Array.Empty<MaterialConversionDiagnostic>();
        public long OutputBytes { get; init; }

        public double InstancingPercent =>
            TotalMeshInstances > 0
                ? (double)ReusedGeometryCount / TotalMeshInstances * 100.0
                : 0.0;

        public string BuildSummary(string fileName)
        {
            var sizeText = OutputBytes >= 1024 * 1024
                ? $"{OutputBytes / (1024.0 * 1024.0):F2} MB"
                : $"{OutputBytes / 1024.0:F1} KB";

            if (ReusedGeometryCount > 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Exported {fileName} ({sizeText}): {TotalNodes:N0} nodes, {TotalMeshInstances:N0} mesh instances reusing {UniqueGeometryBuffers:N0} unique geometries ({UniqueGltfMeshes:N0} glTF meshes, {InstancingPercent:F0}% instanced), {MaterialsCount:N0} materials, {TexturesCount:N0} textures.");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"Exported {fileName} ({sizeText}): {TotalNodes:N0} nodes, {UniqueGltfMeshes:N0} meshes, {MaterialsCount:N0} materials, {TexturesCount:N0} textures.");
        }
    }

    /// <summary>
    /// Pure-C#, zero-extra-dependency glTF 2.0 (.gltf) and binary GLB v2 (.glb) exporter
    /// consuming AssetStudio's <see cref="IImported"/> model graph (meshes, PBR materials,
    /// node hierarchy, skinning, morph targets / blend shapes, mesh instancing, and keyframed animations).
    /// </summary>
    public sealed partial class GltfExporter
    {
        private const uint GlbMagic = 0x46546C67u;        // "glTF"
        private const uint GlbVersion = 2u;
        private const uint GlbChunkTypeJson = 0x4E4F534Au; // "JSON"
        private const uint GlbChunkTypeBin = 0x004E4942u;  // "BIN\0"

        private const int ComponentTypeUnsignedShort = 5123;
        private const int ComponentTypeUnsignedInt = 5125;
        private const int ComponentTypeFloat = 5126;

        private const int TargetArrayBuffer = 34962;
        private const int TargetElementArrayBuffer = 34963;

        private const int MagFilterLinear = 9729;
        private const int MinFilterLinearMipmapLinear = 9987;
        private const int WrapRepeat = 10497;

        private readonly bool _binary;
        private readonly float _scaleFactor;
        private readonly bool _exportSkins;
        private readonly bool _exportAnimations;
        private readonly bool _exportBlendShape;
        private readonly bool _exportVertexColors;
        private readonly bool _enableInstancing;

        public bool ExportVertexColors { get; set; }
        public GltfExportStatistics LastExportStats { get; private set; } = new GltfExportStatistics();

        private readonly BufferBuilder _buffer = new BufferBuilder();
        private readonly List<GltfNode> _nodes = new List<GltfNode>();
        private readonly List<int> _sceneRootNodes = new List<int>();
        private readonly List<GltfMesh> _meshes = new List<GltfMesh>();
        private readonly List<GltfSkin> _skins = new List<GltfSkin>();
        private readonly List<GltfMaterial> _materials = new List<GltfMaterial>();
        private readonly List<MaterialConversionDiagnostic> _materialDiagnostics = new List<MaterialConversionDiagnostic>();
        private readonly List<GltfTexture> _textures = new List<GltfTexture>();
        private readonly List<GltfImage> _images = new List<GltfImage>();
        private readonly List<GltfAnimation> _animations = new List<GltfAnimation>();
        private readonly List<GltfCamera> _cameras = new List<GltfCamera>();
        private readonly List<GltfLight> _lights = new List<GltfLight>();
        private readonly HashSet<int> _animatedNodeIndices = new HashSet<int>();
        private readonly HashSet<string> _extensionsUsed = new HashSet<string>(StringComparer.Ordinal);

        private readonly List<System.Numerics.Matrix4x4> _nodeGlobalRowMatrices = new List<System.Numerics.Matrix4x4>();
        private readonly List<int> _nodeParentIndices = new List<int>();
        private readonly Dictionary<ImportedFrame, int> _nodeByFrame = new Dictionary<ImportedFrame, int>();
        private readonly Dictionary<string, int> _nodeByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, SharedGeometryEntry> _sharedGeometryByKey = new Dictionary<string, SharedGeometryEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _sharedMeshIndexByInstanceKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private int _totalMeshInstances;
        private int _uniqueGeometryBuffers;
        private int _reusedMeshCount;
        private int _reusedGeometryCount;
        private int _hiddenNodesCount;
        private readonly Dictionary<string, string?> _uniqueSuffixToPath = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly Dictionary<string, int?> _uniqueLeafToNode = new Dictionary<string, int?>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _materialIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImportedTexture> _sourceTextureMap = new Dictionary<string, ImportedTexture>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImportedTexture> _sourceTextureMapIgnoreCase = new Dictionary<string, ImportedTexture>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _textureIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, (int TextureIndex, bool HasMetallicMap, bool HasRoughnessMap)> _packedMrCache =
            new Dictionary<string, (int, bool, bool)>(StringComparer.Ordinal);
        private readonly HashSet<string> _usedExternalTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, MorphMeshTargetInfo> _morphTargetsByPath = new Dictionary<string, MorphMeshTargetInfo>(StringComparer.Ordinal);
        private readonly Dictionary<int, MorphMeshTargetInfo> _morphTargetsByNode = new Dictionary<int, MorphMeshTargetInfo>();
        private readonly Dictionary<string, (int MeshNodeIndex, int TargetIndex, float FullWeight)?> _uniqueMorphChannelLookup =
            new Dictionary<string, (int, int, float)?>(StringComparer.Ordinal);
        private readonly Dictionary<int, SkinSkeletonGroup> _meshIndexToSkeletonGroup = new Dictionary<int, SkinSkeletonGroup>();

        private ImportedFrame? _rootFrame;
        private string _exportDirectory = string.Empty;
        private string _baseFileName = "model";

        public GltfExporter(
            bool binary = true,
            float scaleFactor = 1f,
            bool exportSkins = true,
            bool exportAnimations = true,
            bool exportBlendShape = true,
            bool exportVertexColors = false,
            bool enableInstancing = true)
        {
            _binary = binary;
            _scaleFactor = float.IsFinite(scaleFactor) && scaleFactor > 0f ? scaleFactor : 1f;
            _exportSkins = exportSkins;
            _exportAnimations = exportAnimations;
            _exportBlendShape = exportBlendShape;
            _exportVertexColors = exportVertexColors;
            _enableInstancing = enableInstancing;
            ExportVertexColors = exportVertexColors;
        }

        public void Export(IImported imported, string exportPath)
        {
            if (imported == null) throw new ArgumentNullException(nameof(imported));
            if (string.IsNullOrWhiteSpace(exportPath)) throw new ArgumentException("Export path cannot be empty.", nameof(exportPath));

            var currentCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            var currentUICulture = System.Threading.Thread.CurrentThread.CurrentUICulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

                ResetState();

                _exportDirectory = Path.GetDirectoryName(exportPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(_exportDirectory))
                {
                    Directory.CreateDirectory(_exportDirectory);
                }

                _baseFileName = Path.GetFileNameWithoutExtension(exportPath);
                if (string.IsNullOrWhiteSpace(_baseFileName))
                {
                    _baseFileName = "model";
                }

                _rootFrame = imported.RootFrame;

                BuildSourceTextureMap(imported.TextureList);

                if (imported.RootFrame != null)
                {
                    var visited = new HashSet<ImportedFrame>();
                    int rootIdx = BuildNodeHierarchy(imported.RootFrame, -1, System.Numerics.Matrix4x4.Identity, visited);
                    if (rootIdx >= 0)
                    {
                        _sceneRootNodes.Add(rootIdx);
                    }
                }

                if (imported.MaterialList != null)
                {
                    for (int i = 0; i < imported.MaterialList.Count; i++)
                    {
                        ExportMaterial(imported.MaterialList[i]);
                    }
                }

                var (morphByPath, morphByNode) = BuildMorphLookup(imported.MorphList);

                if (imported.MeshList != null)
                {
                    BuildSkeletonGroups(imported.MeshList);
                    for (int i = 0; i < imported.MeshList.Count; i++)
                    {
                        ExportMesh(imported.MeshList[i], i, morphByPath, morphByNode);
                    }
                }

                if (_exportAnimations && imported.AnimationList != null)
                {
                    for (int i = 0; i < imported.AnimationList.Count; i++)
                    {
                        ExportAnimation(imported.AnimationList[i]);
                    }
                }

                for (int a = 0; a < _animations.Count; a++)
                {
                    var channels = _animations[a].Channels;
                    for (int c = 0; c < channels.Count; c++)
                    {
                        _animatedNodeIndices.Add(channels[c].TargetNode);
                    }
                }

                ExportCameras(imported.CameraList);
                ExportLights(imported.LightList);

                WriteOutput(exportPath);

                long outputBytes = 0;
                try
                {
                    if (File.Exists(exportPath))
                    {
                        outputBytes = new FileInfo(exportPath).Length;
                    }
                }
                catch
                {
                }

                int approximatedMaterials = 0;
                int fallbackMaterials = 0;
                for (int i = 0; i < _materialDiagnostics.Count; i++)
                {
                    var d = _materialDiagnostics[i];
                    if (d.HasApproximations) approximatedMaterials++;
                    if (d.Mode == MaterialConversionMode.BasicFallback) fallbackMaterials++;
                }

                LastExportStats = new GltfExportStatistics
                {
                    TotalNodes = _nodes.Count,
                    TotalMeshInstances = _totalMeshInstances,
                    UniqueGltfMeshes = _meshes.Count,
                    UniqueGeometryBuffers = _uniqueGeometryBuffers,
                    ReusedMeshCount = _reusedMeshCount,
                    ReusedGeometryCount = _reusedGeometryCount,
                    MaterialsCount = _materials.Count,
                    TexturesCount = _textures.Count,
                    SkinsCount = _skins.Count,
                    AnimationsCount = _animations.Count,
                    CamerasCount = _cameras.Count,
                    LightsCount = _lights.Count,
                    HiddenNodesCount = _hiddenNodesCount,
                    ApproximatedMaterialsCount = approximatedMaterials,
                    FallbackMaterialsCount = fallbackMaterials,
                    MaterialDiagnostics = _materialDiagnostics.ToArray(),
                    OutputBytes = outputBytes
                };
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = currentCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = currentUICulture;
            }
        }

        private void ResetState()
        {
            _buffer.Reset();
            _nodes.Clear();
            _sceneRootNodes.Clear();
            _meshes.Clear();
            _skins.Clear();
            _materials.Clear();
            _materialDiagnostics.Clear();
            _textures.Clear();
            _images.Clear();
            _animations.Clear();
            _cameras.Clear();
            _lights.Clear();
            _animatedNodeIndices.Clear();
            _extensionsUsed.Clear();
            _nodeGlobalRowMatrices.Clear();
            _nodeParentIndices.Clear();
            _nodeByFrame.Clear();
            _nodeByPath.Clear();
            _sharedGeometryByKey.Clear();
            _sharedMeshIndexByInstanceKey.Clear();
            _totalMeshInstances = 0;
            _uniqueGeometryBuffers = 0;
            _reusedMeshCount = 0;
            _reusedGeometryCount = 0;
            _hiddenNodesCount = 0;
            _uniqueSuffixToPath.Clear();
            _uniqueLeafToNode.Clear();
            _materialIndexByName.Clear();
            _sourceTextureMap.Clear();
            _sourceTextureMapIgnoreCase.Clear();
            _textureIndexByKey.Clear();
            _packedMrCache.Clear();
            _usedExternalTextureFiles.Clear();
            _morphTargetsByPath.Clear();
            _morphTargetsByNode.Clear();
            _uniqueMorphChannelLookup.Clear();
            _meshIndexToSkeletonGroup.Clear();
            _rootFrame = null;
        }

        // ------------------------------------------------------------------
        // Node Hierarchy & Coordinate Transforms
        // ------------------------------------------------------------------

        private int BuildNodeHierarchy(
            ImportedFrame frame,
            int parentIndex,
            System.Numerics.Matrix4x4 parentGlobalRow,
            HashSet<ImportedFrame> visited)
        {
            if (frame == null || !visited.Add(frame))
            {
                return -1;
            }

            // ModelConverter already converted LocalPosition (-x, y, z) and LocalRotation (Euler XYZ in degrees)
            // into right-handed coordinates matching System.Numerics row-vector Rx * Ry * Rz.
            var translation = new System.Numerics.Vector3(
                SanitizeFloat(frame.LocalPosition.X * _scaleFactor),
                SanitizeFloat(frame.LocalPosition.Y * _scaleFactor),
                SanitizeFloat(frame.LocalPosition.Z * _scaleFactor));
            var rotation = EulerDegreesToQuaternion(frame.LocalRotation);
            var scale = new System.Numerics.Vector3(
                SanitizeScaleFloat(frame.LocalScale.X),
                SanitizeScaleFloat(frame.LocalScale.Y),
                SanitizeScaleFloat(frame.LocalScale.Z));

            var localRow =
                System.Numerics.Matrix4x4.CreateScale(scale) *
                System.Numerics.Matrix4x4.CreateFromQuaternion(rotation) *
                System.Numerics.Matrix4x4.CreateTranslation(translation);
            var globalRow = localRow * parentGlobalRow;

            int nodeIdx = _nodes.Count;
            var node = new GltfNode
            {
                Name = string.IsNullOrEmpty(frame.Name) ? $"Node_{nodeIdx}" : frame.Name,
                Translation = IsNearlyZero(translation) ? null : translation,
                Rotation = IsNearlyIdentity(rotation) ? null : rotation,
                Scale = IsNearlyOne(scale) ? null : scale,
                Extras = frame.Extras,
                IsInitiallyHidden = frame.IsInitiallyHidden,
                HiddenReason = frame.HiddenReason
            };
            if (frame.IsInitiallyHidden)
            {
                _hiddenNodesCount++;
                _extensionsUsed.Add("KHR_node_visibility");
            }
            _nodes.Add(node);
            _nodeGlobalRowMatrices.Add(globalRow);
            _nodeParentIndices.Add(parentIndex);
            _nodeByFrame[frame] = nodeIdx;

            if (parentIndex >= 0)
            {
                _nodes[parentIndex].Children.Add(nodeIdx);
            }

            var normalizedPath = NormalizeFramePath(frame.Path);
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                if (!_nodeByPath.ContainsKey(normalizedPath))
                {
                    _nodeByPath[normalizedPath] = nodeIdx;
                }

                int slashIdx = normalizedPath.IndexOf('/');
                while (slashIdx >= 0 && slashIdx + 1 < normalizedPath.Length)
                {
                    var suffix = normalizedPath.Substring(slashIdx + 1);
                    if (_uniqueSuffixToPath.TryGetValue(suffix, out var existing))
                    {
                        if (existing != null && !string.Equals(existing, normalizedPath, StringComparison.Ordinal))
                        {
                            _uniqueSuffixToPath[suffix] = null;
                        }
                    }
                    else
                    {
                        _uniqueSuffixToPath[suffix] = normalizedPath;
                    }
                    slashIdx = normalizedPath.IndexOf('/', slashIdx + 1);
                }
            }

            if (!string.IsNullOrEmpty(frame.Name))
            {
                if (_uniqueLeafToNode.TryGetValue(frame.Name, out var existingLeaf))
                {
                    if (existingLeaf.HasValue && existingLeaf.Value != nodeIdx)
                    {
                        _uniqueLeafToNode[frame.Name] = null;
                    }
                }
                else
                {
                    _uniqueLeafToNode[frame.Name] = nodeIdx;
                }
            }

            for (int i = 0; i < frame.Count; i++)
            {
                BuildNodeHierarchy(frame[i], nodeIdx, globalRow, visited);
            }

            return nodeIdx;
        }

        private bool TryResolveNodeIndex(string? rawPath, out int nodeIndex)
        {
            nodeIndex = -1;
            var normalized = NormalizeFramePath(rawPath);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            if (_nodeByPath.TryGetValue(normalized, out nodeIndex))
            {
                return true;
            }

            if (_uniqueSuffixToPath.TryGetValue(normalized, out var canonical)
                && canonical != null
                && _nodeByPath.TryGetValue(canonical, out nodeIndex))
            {
                return true;
            }

            if (normalized.IndexOf('/') < 0
                && _uniqueLeafToNode.TryGetValue(normalized, out var leafNode)
                && leafNode.HasValue)
            {
                nodeIndex = leafNode.Value;
                return true;
            }

            var frameMatch = _rootFrame?.FindFrameByPath(normalized);
            if (frameMatch != null && _nodeByPath.TryGetValue(NormalizeFramePath(frameMatch.Path), out nodeIndex))
            {
                return true;
            }

            return false;
        }

        private int CreateExtraNode(string name, int parentIndex, System.Numerics.Matrix4x4? localRow = null)
        {
            int nodeIdx = _nodes.Count;
            var node = new GltfNode
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Node_{nodeIdx}" : name
            };

            var parentGlobal = parentIndex >= 0 ? _nodeGlobalRowMatrices[parentIndex] : System.Numerics.Matrix4x4.Identity;
            var actualLocal = System.Numerics.Matrix4x4.Identity;

            if (localRow.HasValue && System.Numerics.Matrix4x4.Decompose(localRow.Value, out var s, out var r, out var t))
            {
                r = NormalizeQuaternionSafe(r);
                t = new System.Numerics.Vector3(SanitizeFloat(t.X), SanitizeFloat(t.Y), SanitizeFloat(t.Z));
                s = new System.Numerics.Vector3(SanitizeScaleFloat(s.X), SanitizeScaleFloat(s.Y), SanitizeScaleFloat(s.Z));
                node.Translation = IsNearlyZero(t) ? null : t;
                node.Rotation = IsNearlyIdentity(r) ? null : r;
                node.Scale = IsNearlyOne(s) ? null : s;
                actualLocal =
                    System.Numerics.Matrix4x4.CreateScale(s) *
                    System.Numerics.Matrix4x4.CreateFromQuaternion(r) *
                    System.Numerics.Matrix4x4.CreateTranslation(t);
            }

            _nodes.Add(node);
            _nodeGlobalRowMatrices.Add(actualLocal * parentGlobal);
            _nodeParentIndices.Add(parentIndex);

            if (parentIndex >= 0)
            {
                _nodes[parentIndex].Children.Add(nodeIdx);
            }
            else
            {
                _sceneRootNodes.Add(nodeIdx);
            }

            return nodeIdx;
        }

        private bool IsAncestorOrSame(int candidateAncestor, int nodeIndex)
        {
            int current = nodeIndex;
            int guard = 0;
            while (current >= 0 && guard++ <= _nodeParentIndices.Count)
            {
                if (current == candidateAncestor)
                {
                    return true;
                }
                current = _nodeParentIndices[current];
            }
            return false;
        }

        private int? FindCommonAncestor(IReadOnlyList<int> nodeIndices)
        {
            if (nodeIndices.Count == 0)
            {
                return null;
            }

            var firstChain = GetAncestorChain(nodeIndices[0]);
            int commonLength = firstChain.Count;

            for (int i = 1; i < nodeIndices.Count; i++)
            {
                var chain = GetAncestorChain(nodeIndices[i]);
                int maxCheck = Math.Min(commonLength, chain.Count);
                int matched = 0;
                while (matched < maxCheck && firstChain[matched] == chain[matched])
                {
                    matched++;
                }
                commonLength = matched;
                if (commonLength == 0)
                {
                    return null;
                }
            }

            return firstChain[commonLength - 1];
        }

        private List<int> GetAncestorChain(int nodeIndex)
        {
            var chain = new List<int>();
            int current = nodeIndex;
            int guard = 0;
            while (current >= 0 && guard++ <= _nodeParentIndices.Count)
            {
                chain.Add(current);
                current = _nodeParentIndices[current];
            }
            chain.Reverse();
            return chain;
        }

        // ------------------------------------------------------------------
        // Math & Sanitization Helpers
        // ------------------------------------------------------------------

        internal static System.Numerics.Quaternion EulerDegreesToQuaternion(Vector3 eulerDegrees)
        {
            float rx = DegreesToRadians(SanitizeFloat(eulerDegrees.X));
            float ry = DegreesToRadians(SanitizeFloat(eulerDegrees.Y));
            float rz = DegreesToRadians(SanitizeFloat(eulerDegrees.Z));

            // Match ModelConverter.QuaternionToEuler and FbxSharpieExporter.BuildLocalMatrix:
            // row-vector matrix R = Rx * Ry * Rz.
            var rotMat =
                System.Numerics.Matrix4x4.CreateRotationX(rx) *
                System.Numerics.Matrix4x4.CreateRotationY(ry) *
                System.Numerics.Matrix4x4.CreateRotationZ(rz);

            return NormalizeQuaternionSafe(System.Numerics.Quaternion.CreateFromRotationMatrix(rotMat));
        }

        private static System.Numerics.Quaternion NormalizeQuaternionSafe(System.Numerics.Quaternion q)
        {
            float x = SanitizeFloat(q.X);
            float y = SanitizeFloat(q.Y);
            float z = SanitizeFloat(q.Z);
            float w = SanitizeFloat(q.W, 1f);
            double lenSq = (double)x * x + (double)y * y + (double)z * z + (double)w * w;
            if (lenSq <= 1e-12 || !double.IsFinite(lenSq))
            {
                return System.Numerics.Quaternion.Identity;
            }
            double inv = 1.0 / Math.Sqrt(lenSq);
            return new System.Numerics.Quaternion(
                SanitizeFloat((float)(x * inv)),
                SanitizeFloat((float)(y * inv)),
                SanitizeFloat((float)(z * inv)),
                SanitizeFloat((float)(w * inv)));
        }

        private static System.Numerics.Vector3 NormalizeVector3Safe(System.Numerics.Vector3 v, System.Numerics.Vector3 fallback)
        {
            double lenSq = (double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z;
            if (lenSq <= 1e-12 || !double.IsFinite(lenSq))
            {
                return fallback;
            }
            double inv = 1.0 / Math.Sqrt(lenSq);
            return new System.Numerics.Vector3(
                SanitizeFloat((float)(v.X * inv)),
                SanitizeFloat((float)(v.Y * inv)),
                SanitizeFloat((float)(v.Z * inv)));
        }

        private static float DegreesToRadians(float deg) => (float)(deg * (Math.PI / 180.0));

        private static float SanitizeFloat(float value, float fallback = 0f)
        {
            if (!float.IsFinite(value))
            {
                return fallback;
            }
            return value == 0f ? 0f : value; // collapse -0.0f to +0.0f
        }

        private static float SanitizeScaleFloat(float value)
        {
            return float.IsFinite(value) ? (value == 0f ? 0f : value) : 1f;
        }

        private static float Clamp01(float value)
        {
            float s = SanitizeFloat(value);
            if (s < 0f) return 0f;
            if (s > 1f) return 1f;
            return s;
        }

        private static float ShininessToRoughness(float shininess)
        {
            float s = Math.Clamp(SanitizeFloat(shininess, 20f), 0f, 128f);
            return Clamp01(1f - MathF.Sqrt(s / 128f));
        }

        private static bool IsNearlyZero(System.Numerics.Vector3 v)
            => MathF.Abs(v.X) < 1e-6f && MathF.Abs(v.Y) < 1e-6f && MathF.Abs(v.Z) < 1e-6f;

        private static bool IsNearlyOne(System.Numerics.Vector3 v)
            => MathF.Abs(v.X - 1f) < 1e-6f && MathF.Abs(v.Y - 1f) < 1e-6f && MathF.Abs(v.Z - 1f) < 1e-6f;

        private static bool IsNearlyIdentity(System.Numerics.Quaternion q)
            => MathF.Abs(q.X) < 1e-6f && MathF.Abs(q.Y) < 1e-6f && MathF.Abs(q.Z) < 1e-6f && MathF.Abs(MathF.Abs(q.W) - 1f) < 1e-6f;

        private static string NormalizeFramePath(string? path)
            => string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').Trim('/');

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                sb.Append(Array.IndexOf(invalid, ch) >= 0 || ch == '/' || ch == '\\' || ch == '%' ? '_' : ch);
            }
            return sb.ToString().Trim();
        }

    }
}
