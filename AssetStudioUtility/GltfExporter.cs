using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AssetStudio
{
    /// <summary>
    /// Pure-C#, zero-extra-dependency glTF 2.0 (.gltf) and binary GLB v2 (.glb) exporter
    /// consuming AssetStudio's <see cref="IImported"/> model graph (meshes, PBR materials,
    /// node hierarchy, skinning, morph targets / blend shapes, and keyframed animations).
    /// </summary>
    public sealed class GltfExporter
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

        private readonly BufferBuilder _buffer = new BufferBuilder();
        private readonly List<GltfNode> _nodes = new List<GltfNode>();
        private readonly List<int> _sceneRootNodes = new List<int>();
        private readonly List<GltfMesh> _meshes = new List<GltfMesh>();
        private readonly List<GltfSkin> _skins = new List<GltfSkin>();
        private readonly List<GltfMaterial> _materials = new List<GltfMaterial>();
        private readonly List<GltfTexture> _textures = new List<GltfTexture>();
        private readonly List<GltfImage> _images = new List<GltfImage>();
        private readonly List<GltfAnimation> _animations = new List<GltfAnimation>();
        private readonly HashSet<string> _extensionsUsed = new HashSet<string>(StringComparer.Ordinal);

        private readonly List<System.Numerics.Matrix4x4> _nodeGlobalRowMatrices = new List<System.Numerics.Matrix4x4>();
        private readonly List<int> _nodeParentIndices = new List<int>();
        private readonly Dictionary<string, int> _nodeByPath = new Dictionary<string, int>(StringComparer.Ordinal);
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
            bool exportBlendShape = true)
        {
            _binary = binary;
            _scaleFactor = float.IsFinite(scaleFactor) && scaleFactor > 0f ? scaleFactor : 1f;
            _exportSkins = exportSkins;
            _exportAnimations = exportAnimations;
            _exportBlendShape = exportBlendShape;
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

                WriteOutput(exportPath);
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
            _textures.Clear();
            _images.Clear();
            _animations.Clear();
            _extensionsUsed.Clear();
            _nodeGlobalRowMatrices.Clear();
            _nodeParentIndices.Clear();
            _nodeByPath.Clear();
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
                Scale = IsNearlyOne(scale) ? null : scale
            };
            _nodes.Add(node);
            _nodeGlobalRowMatrices.Add(globalRow);
            _nodeParentIndices.Add(parentIndex);

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
                if (_rootFrame != null)
                {
                    var rootPath = NormalizeFramePath(_rootFrame.Path);
                    if (!string.IsNullOrEmpty(rootPath) && _nodeByPath.TryGetValue(rootPath, out nodeIndex))
                    {
                        return true;
                    }
                }
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
        // Materials & Textures (PBR Metallic-Roughness + SkiaSharp Packing)
        // ------------------------------------------------------------------

        private void BuildSourceTextureMap(List<ImportedTexture>? textureList)
        {
            if (textureList == null)
            {
                return;
            }

            for (int i = 0; i < textureList.Count; i++)
            {
                var tex = textureList[i];
                if (tex == null || string.IsNullOrEmpty(tex.Name))
                {
                    continue;
                }

                if (!_sourceTextureMap.ContainsKey(tex.Name))
                {
                    _sourceTextureMap[tex.Name] = tex;
                }
                if (!_sourceTextureMapIgnoreCase.ContainsKey(tex.Name))
                {
                    _sourceTextureMapIgnoreCase[tex.Name] = tex;
                }
            }
        }

        private ImportedTexture? FindSourceTexture(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (_sourceTextureMap.TryGetValue(name, out var exact))
            {
                return exact;
            }
            if (_sourceTextureMapIgnoreCase.TryGetValue(name, out var ci))
            {
                return ci;
            }
            return null;
        }

        private void ExportMaterial(ImportedMaterial? mat)
        {
            if (mat == null)
            {
                return;
            }

            string matName = string.IsNullOrEmpty(mat.Name) ? $"Material_{_materials.Count}" : mat.Name;
            if (_materialIndexByName.ContainsKey(matName))
            {
                return;
            }

            ImportedMaterialTexture? diffuseTex = null;
            ImportedMaterialTexture? normalTex = null;
            ImportedMaterialTexture? specularTex = null;
            ImportedMaterialTexture? metallicTex = null;
            ImportedMaterialTexture? emissiveTex = null;
            ImportedMaterialTexture? occlusionTex = null;
            ImportedMaterialTexture? roughnessTex = null;

            if (mat.Textures != null)
            {
                for (int i = 0; i < mat.Textures.Count; i++)
                {
                    var t = mat.Textures[i];
                    if (t == null || string.IsNullOrEmpty(t.Name))
                    {
                        continue;
                    }

                    switch (t.Dest)
                    {
                        case 0:
                            diffuseTex ??= t;
                            break;
                        case 1:
                            normalTex ??= t;
                            break;
                        case 2:
                            specularTex ??= t;
                            break;
                        case 3:
                            metallicTex ??= t;
                            break;
                        case 4:
                            emissiveTex ??= t;
                            break;
                        case 5:
                            occlusionTex ??= t;
                            break;
                        case 8:
                            roughnessTex ??= t;
                            break;
                    }
                }
            }

            float transparency = Clamp01(mat.Transparency);
            float baseAlpha = Clamp01(SanitizeFloat(mat.Diffuse.A, 1f) * (1f - transparency));
            var baseColorFactor = new[]
            {
                Clamp01(SanitizeFloat(mat.Diffuse.R, 0.8f)),
                Clamp01(SanitizeFloat(mat.Diffuse.G, 0.8f)),
                Clamp01(SanitizeFloat(mat.Diffuse.B, 0.8f)),
                baseAlpha
            };

            float scalarMetallic = mat.Metallic.HasValue ? Clamp01(mat.Metallic.Value) : 0f;
            float scalarRoughness = mat.Roughness.HasValue
                ? Clamp01(mat.Roughness.Value)
                : mat.Smoothness.HasValue
                    ? Clamp01(1f - mat.Smoothness.Value)
                    : ShininessToRoughness(mat.Shininess);

            var gltfMat = new GltfMaterial
            {
                Name = matName,
                BaseColorFactor = baseColorFactor,
                MetallicFactor = scalarMetallic,
                RoughnessFactor = scalarRoughness,
                DoubleSided = mat.DoubleSided
            };

            if (diffuseTex != null && TryRegisterTexture(diffuseTex.Name, out int baseColorTexIdx))
            {
                gltfMat.BaseColorTexture = CreateTextureInfo(baseColorTexIdx, diffuseTex);
            }

            // Pack Metallic-Roughness texture into glTF G=Roughness, B=Metallic when source textures exist.
            // Unpopulated channels are written as 255 (1.0) so scalar MetallicFactor / RoughnessFactor
            // apply cleanly without double-multiplication or 8-bit quantization.
            if (metallicTex != null || roughnessTex != null)
            {
                var srcMetal = metallicTex != null ? FindSourceTexture(metallicTex.Name) : null;
                var srcRough = roughnessTex != null ? FindSourceTexture(roughnessTex.Name) : null;
                if (TryPackMetallicRoughnessTexture(
                        matName,
                        srcMetal,
                        srcRough,
                        roughnessTex?.Name,
                        out int mrTexIdx,
                        out bool hasMetallicMap,
                        out bool hasRoughnessMap))
                {
                    var uvSource = metallicTex ?? roughnessTex!;
                    gltfMat.MetallicRoughnessTexture = CreateTextureInfo(mrTexIdx, uvSource);
                    gltfMat.MetallicFactor = hasMetallicMap ? 1f : scalarMetallic;
                    gltfMat.RoughnessFactor = hasRoughnessMap ? 1f : scalarRoughness;
                }
            }
            else if (specularTex != null)
            {
                var srcSpec = FindSourceTexture(specularTex.Name);
                if (srcSpec != null && TryPackSpecularGlossToRoughnessTexture(matName, srcSpec, out int specMrTexIdx))
                {
                    gltfMat.MetallicRoughnessTexture = CreateTextureInfo(specMrTexIdx, specularTex);
                    gltfMat.MetallicFactor = 0f;
                    gltfMat.RoughnessFactor = 1f;
                }
            }

            if (normalTex != null && TryRegisterTexture(normalTex.Name, out int normalTexIdx))
            {
                gltfMat.NormalTexture = CreateTextureInfo(normalTexIdx, normalTex);
                if (mat.BumpScale.HasValue && float.IsFinite(mat.BumpScale.Value) && MathF.Abs(mat.BumpScale.Value - 1f) > 1e-4f)
                {
                    gltfMat.NormalScale = Math.Max(0f, mat.BumpScale.Value);
                }
            }

            if (occlusionTex != null && TryRegisterTexture(occlusionTex.Name, out int occTexIdx))
            {
                gltfMat.OcclusionTexture = CreateTextureInfo(occTexIdx, occlusionTex);
                if (mat.OcclusionStrength.HasValue && float.IsFinite(mat.OcclusionStrength.Value))
                {
                    float strength = Clamp01(mat.OcclusionStrength.Value);
                    if (MathF.Abs(strength - 1f) > 1e-4f)
                    {
                        gltfMat.OcclusionStrength = strength;
                    }
                }
            }

            var emissive = new[]
            {
                Clamp01(SanitizeFloat(mat.Emissive.R, 0f)),
                Clamp01(SanitizeFloat(mat.Emissive.G, 0f)),
                Clamp01(SanitizeFloat(mat.Emissive.B, 0f))
            };
            bool hasEmissiveColor = emissive[0] > 1e-4f || emissive[1] > 1e-4f || emissive[2] > 1e-4f;
            if (emissiveTex != null && TryRegisterTexture(emissiveTex.Name, out int emTexIdx))
            {
                gltfMat.EmissiveTexture = CreateTextureInfo(emTexIdx, emissiveTex);
                gltfMat.EmissiveFactor = hasEmissiveColor ? emissive : new[] { 1f, 1f, 1f };
            }
            else if (hasEmissiveColor)
            {
                gltfMat.EmissiveFactor = emissive;
            }

            // Alpha mode
            if (mat.SurfaceMode.HasValue)
            {
                int modeInt = (int)MathF.Round(mat.SurfaceMode.Value);
                if (modeInt == 1)
                {
                    gltfMat.AlphaMode = "MASK";
                    gltfMat.AlphaCutoff = mat.AlphaCutoff.HasValue ? Clamp01(mat.AlphaCutoff.Value) : 0.5f;
                }
                else if (modeInt >= 2)
                {
                    gltfMat.AlphaMode = "BLEND";
                }
            }
            else if (transparency > 1e-3f || baseAlpha < 0.999f)
            {
                gltfMat.AlphaMode = "BLEND";
            }

            int matIdx = _materials.Count;
            _materials.Add(gltfMat);
            _materialIndexByName[matName] = matIdx;
        }

        private GltfTextureInfo CreateTextureInfo(int textureIndex, ImportedMaterialTexture matTex)
        {
            var info = new GltfTextureInfo { Index = textureIndex };

            float sx = SanitizeFloat(matTex.Scale.X, 1f);
            float sy = SanitizeFloat(matTex.Scale.Y, 1f);
            float ox = SanitizeFloat(matTex.Offset.X, 0f);
            float oy = SanitizeFloat(matTex.Offset.Y, 0f);

            // Unity default scale is (1,1); if uninitialized (0,0), treat as (1,1).
            if (MathF.Abs(sx) < 1e-6f && MathF.Abs(sy) < 1e-6f)
            {
                sx = 1f;
                sy = 1f;
            }

            bool hasTransform =
                MathF.Abs(sx - 1f) > 1e-4f ||
                MathF.Abs(sy - 1f) > 1e-4f ||
                MathF.Abs(ox) > 1e-4f ||
                MathF.Abs(oy) > 1e-4f;

            if (hasTransform)
            {
                // Convert Unity bottom-left UV transform to glTF top-left:
                // v_g' = v_g * sy + (1 - oy - sy)
                info.Offset = new[] { SanitizeFloat(ox), SanitizeFloat(1f - oy - sy) };
                info.Scale = new[] { SanitizeFloat(sx), SanitizeFloat(sy) };
                _extensionsUsed.Add("KHR_texture_transform");
            }

            return info;
        }

        private bool TryRegisterTexture(string textureName, out int textureIndex)
        {
            textureIndex = -1;
            var source = FindSourceTexture(textureName);
            if (source == null || source.Data == null || source.Data.Length == 0)
            {
                return false;
            }

            string cacheKey = $"src:{source.Name}";
            if (_textureIndexByKey.TryGetValue(cacheKey, out textureIndex))
            {
                return true;
            }

            if (!TrySanitizeImageBytes(source.Data, out var encodedBytes, out var mimeType, out var extension))
            {
                Logger.Warning($"Skipping glTF texture '{source.Name}': unable to decode or transcode image to PNG/JPEG.");
                return false;
            }

            textureIndex = RegisterEncodedTexture(cacheKey, source.Name, encodedBytes, mimeType, extension);
            return true;
        }

        private int RegisterEncodedTexture(string cacheKey, string displayName, byte[] encodedBytes, string mimeType, string extension)
        {
            if (_textureIndexByKey.TryGetValue(cacheKey, out int existing))
            {
                return existing;
            }

            string cleanName = Path.GetFileNameWithoutExtension(displayName);
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                cleanName = $"texture_{_images.Count}";
            }

            int imageIdx = _images.Count;
            if (_binary)
            {
                int bvIdx = _buffer.AddBufferView(encodedBytes, target: null);
                _images.Add(new GltfImage
                {
                    Name = cleanName,
                    MimeType = mimeType,
                    BufferView = bvIdx
                });
            }
            else
            {
                string fileName = AllocateUniqueExternalTextureFileName(cleanName, extension);
                string fullPath = string.IsNullOrEmpty(_exportDirectory)
                    ? fileName
                    : Path.Combine(_exportDirectory, fileName);
                File.WriteAllBytes(fullPath, encodedBytes);

                _images.Add(new GltfImage
                {
                    Name = cleanName,
                    MimeType = mimeType,
                    Uri = Uri.EscapeDataString(fileName)
                });
            }

            int texIdx = _textures.Count;
            _textures.Add(new GltfTexture
            {
                Name = cleanName,
                Sampler = 0,
                Source = imageIdx
            });

            _textureIndexByKey[cacheKey] = texIdx;
            return texIdx;
        }

        private string AllocateUniqueExternalTextureFileName(string cleanName, string extension)
        {
            string safeBase = SanitizeFileName(cleanName);
            if (string.IsNullOrWhiteSpace(safeBase))
            {
                safeBase = $"texture_{_images.Count}";
            }

            string candidate = safeBase + extension;
            if (_usedExternalTextureFiles.Add(candidate))
            {
                return candidate;
            }

            for (int i = 1; ; i++)
            {
                candidate = $"{safeBase}_{i}{extension}";
                if (_usedExternalTextureFiles.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private bool TryPackMetallicRoughnessTexture(
            string matName,
            ImportedTexture? metallicSource,
            ImportedTexture? roughnessSource,
            string? roughnessTexName,
            out int textureIndex,
            out bool hasMetallicMap,
            out bool hasRoughnessMap)
        {
            textureIndex = -1;
            hasMetallicMap = false;
            hasRoughnessMap = false;

            string cacheKey = $"mr:{metallicSource?.Name ?? ""}|{roughnessSource?.Name ?? ""}|{roughnessTexName ?? ""}";
            if (_packedMrCache.TryGetValue(cacheKey, out var cached))
            {
                textureIndex = cached.TextureIndex;
                hasMetallicMap = cached.HasMetallicMap;
                hasRoughnessMap = cached.HasRoughnessMap;
                return true;
            }

            using var metalBmp = metallicSource?.Data != null ? DecodeAnyToBgra(metallicSource.Data) : null;
            using var roughBmp = roughnessSource?.Data != null ? DecodeAnyToBgra(roughnessSource.Data) : null;

            if (metalBmp == null && roughBmp == null)
            {
                return false;
            }

            int width = metalBmp?.Width ?? roughBmp!.Width;
            int height = metalBmp?.Height ?? roughBmp!.Height;

            SKBitmap? resizedRough = null;
            try
            {
                var effectiveRough = roughBmp;
                if (metalBmp != null && roughBmp != null && (roughBmp.Width != width || roughBmp.Height != height))
                {
                    var targetInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    resizedRough = roughBmp.Resize(targetInfo, SKSamplingOptions.Default);
                    effectiveRough = resizedRough ?? roughBmp;
                }

                byte[]? metalPixels = null;
                bool metalHasSmoothnessInAlpha = false;
                if (metalBmp != null)
                {
                    metalPixels = new byte[checked(width * height * 4)];
                    metalBmp.CopyBgraPixelsTo(metalPixels);
                    for (int i = 3; i < metalPixels.Length; i += 4)
                    {
                        if (metalPixels[i] < 255)
                        {
                            metalHasSmoothnessInAlpha = true;
                            break;
                        }
                    }
                }

                byte[]? roughPixels = null;
                bool roughIsSmoothness = false;
                if (effectiveRough != null && effectiveRough.Width == width && effectiveRough.Height == height)
                {
                    roughPixels = new byte[checked(width * height * 4)];
                    effectiveRough.CopyBgraPixelsTo(roughPixels);
                    if (!string.IsNullOrEmpty(roughnessTexName))
                    {
                        roughIsSmoothness =
                            roughnessTexName.IndexOf("smooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            roughnessTexName.IndexOf("gloss", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }

                hasMetallicMap = metalPixels != null;
                hasRoughnessMap = roughPixels != null || (metalPixels != null && metalHasSmoothnessInAlpha);

                int pixelCount = checked(width * height);
                var packedBgra = new byte[checked(pixelCount * 4)];

                for (int p = 0; p < pixelCount; p++)
                {
                    int off = p * 4;
                    // BGRA layout: [0]=B, [1]=G, [2]=R, [3]=A
                    // Write 255 (1.0) for unmapped channels so glTF metallicFactor / roughnessFactor apply once cleanly.
                    byte mByte = metalPixels != null ? metalPixels[off + 2] : (byte)255;
                    byte rByte;
                    if (roughPixels != null)
                    {
                        byte val = roughPixels[off + 2];
                        rByte = roughIsSmoothness ? (byte)(255 - val) : val;
                    }
                    else if (metalPixels != null && metalHasSmoothnessInAlpha)
                    {
                        rByte = (byte)(255 - metalPixels[off + 3]);
                    }
                    else
                    {
                        rByte = 255;
                    }

                    // glTF 2.0 metallicRoughnessTexture: G = Roughness, B = Metallic
                    packedBgra[off + 0] = mByte; // B = Metallic
                    packedBgra[off + 1] = rByte; // G = Roughness
                    packedBgra[off + 2] = 255;   // R = unused (occlusion = 1)
                    packedBgra[off + 3] = 255;   // A = opaque
                }

                using var packedBitmap = ImageExtensions.CreateBgraUnpremulBitmap(packedBgra, width, height, flipVertical: false);
                using var ms = packedBitmap.ConvertToStream(ImageFormat.Png);
                var pngBytes = ms.ToArray();

                textureIndex = RegisterEncodedTexture(cacheKey, $"{matName}_metallicRoughness", pngBytes, "image/png", ".png");
                _packedMrCache[cacheKey] = (textureIndex, hasMetallicMap, hasRoughnessMap);
                return true;
            }
            finally
            {
                resizedRough?.Dispose();
            }
        }

        private bool TryPackSpecularGlossToRoughnessTexture(string matName, ImportedTexture specSource, out int textureIndex)
        {
            textureIndex = -1;
            if (specSource.Data == null || specSource.Data.Length == 0)
            {
                return false;
            }

            string cacheKey = $"specmr:{specSource.Name}";
            if (_textureIndexByKey.TryGetValue(cacheKey, out textureIndex))
            {
                return true;
            }

            using var specBmp = DecodeAnyToBgra(specSource.Data);
            if (specBmp == null)
            {
                return false;
            }

            int width = specBmp.Width;
            int height = specBmp.Height;
            var pixels = new byte[checked(width * height * 4)];
            specBmp.CopyBgraPixelsTo(pixels);

            bool hasAlphaSmoothness = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] < 255)
                {
                    hasAlphaSmoothness = true;
                    break;
                }
            }

            if (!hasAlphaSmoothness)
            {
                return false;
            }

            int pixelCount = checked(width * height);
            var packedBgra = new byte[checked(pixelCount * 4)];
            for (int p = 0; p < pixelCount; p++)
            {
                int off = p * 4;
                packedBgra[off + 0] = 0;                       // B = Metallic (0 for specular workflow)
                packedBgra[off + 1] = (byte)(255 - pixels[off + 3]); // G = Roughness (1 - Gloss)
                packedBgra[off + 2] = 255;
                packedBgra[off + 3] = 255;
            }

            using var packedBitmap = ImageExtensions.CreateBgraUnpremulBitmap(packedBgra, width, height, flipVertical: false);
            using var ms = packedBitmap.ConvertToStream(ImageFormat.Png);
            textureIndex = RegisterEncodedTexture(cacheKey, $"{matName}_roughness", ms.ToArray(), "image/png", ".png");
            return true;
        }

        private static bool TrySanitizeImageBytes(byte[] rawBytes, out byte[] encodedBytes, out string mimeType, out string extension)
        {
            if (IsPng(rawBytes))
            {
                encodedBytes = rawBytes;
                mimeType = "image/png";
                extension = ".png";
                return true;
            }

            if (IsJpeg(rawBytes))
            {
                encodedBytes = rawBytes;
                mimeType = "image/jpeg";
                extension = ".jpg";
                return true;
            }

            using var bitmap = DecodeAnyToBgra(rawBytes);
            if (bitmap == null)
            {
                encodedBytes = Array.Empty<byte>();
                mimeType = string.Empty;
                extension = string.Empty;
                return false;
            }

            using var ms = bitmap.ConvertToStream(ImageFormat.Png);
            encodedBytes = ms.ToArray();
            mimeType = "image/png";
            extension = ".png";
            return encodedBytes.Length > 0;
        }

        private static bool IsPng(byte[] data)
        {
            return data.Length >= 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
        }

        private static bool IsJpeg(byte[] data)
        {
            return data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        }

        private static SKBitmap? DecodeAnyToBgra(byte[] data)
        {
            var bmp = ImageExtensions.DecodeBgraUnpremul(data);
            if (bmp != null)
            {
                return bmp;
            }

            return TryDecodeTgaToBgra(data);
        }

        /// <summary>
        /// Minimal pure-C# TGA decoder supporting uncompressed and RLE 24/32/8-bit TGA files
        /// (including AssetStudio's own <c>ImageExtensions.WriteTga32</c> output).
        /// </summary>
        private static SKBitmap? TryDecodeTgaToBgra(byte[] data)
        {
            if (data == null || data.Length < 18)
            {
                return null;
            }

            int idLength = data[0];
            int colorMapType = data[1];
            int imageType = data[2];
            if (colorMapType != 0 || (imageType != 2 && imageType != 3 && imageType != 10 && imageType != 11))
            {
                return null;
            }

            int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12, 2));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14, 2));
            int bpp = data[16];
            byte descriptor = data[17];
            if (width <= 0 || height <= 0 || (bpp != 8 && bpp != 24 && bpp != 32))
            {
                return null;
            }

            int bytesPerPixel = bpp / 8;
            int srcOffset = 18 + idLength;
            if (srcOffset >= data.Length)
            {
                return null;
            }

            long pixelCountLong = (long)width * height;
            if (pixelCountLong <= 0 || pixelCountLong > 64L * 1024 * 1024)
            {
                return null;
            }

            int pixelCount = (int)pixelCountLong;
            var bgra = new byte[checked(pixelCount * 4)];
            bool isRle = imageType == 10 || imageType == 11;

            if (!isRle)
            {
                if (srcOffset + (long)pixelCount * bytesPerPixel > data.Length)
                {
                    return null;
                }

                for (int i = 0; i < pixelCount; i++)
                {
                    ReadTgaPixel(data, srcOffset + i * bytesPerPixel, bytesPerPixel, bgra, i * 4);
                }
            }
            else
            {
                int dstPixel = 0;
                int pos = srcOffset;
                while (dstPixel < pixelCount && pos < data.Length)
                {
                    byte header = data[pos++];
                    int count = (header & 0x7F) + 1;
                    if ((header & 0x80) != 0)
                    {
                        if (pos + bytesPerPixel > data.Length)
                        {
                            return null;
                        }
                        for (int k = 0; k < count && dstPixel < pixelCount; k++, dstPixel++)
                        {
                            ReadTgaPixel(data, pos, bytesPerPixel, bgra, dstPixel * 4);
                        }
                        pos += bytesPerPixel;
                    }
                    else
                    {
                        if (pos + count * bytesPerPixel > data.Length)
                        {
                            return null;
                        }
                        for (int k = 0; k < count && dstPixel < pixelCount; k++, dstPixel++)
                        {
                            ReadTgaPixel(data, pos, bytesPerPixel, bgra, dstPixel * 4);
                            pos += bytesPerPixel;
                        }
                    }
                }

                if (dstPixel < pixelCount)
                {
                    return null;
                }
            }

            bool topOrigin = (descriptor & 0x20) != 0;
            return ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height, flipVertical: !topOrigin);
        }

        private static void ReadTgaPixel(byte[] src, int srcOff, int bytesPerPixel, byte[] dst, int dstOff)
        {
            if (bytesPerPixel == 4)
            {
                dst[dstOff + 0] = src[srcOff + 0];
                dst[dstOff + 1] = src[srcOff + 1];
                dst[dstOff + 2] = src[srcOff + 2];
                dst[dstOff + 3] = src[srcOff + 3];
            }
            else if (bytesPerPixel == 3)
            {
                dst[dstOff + 0] = src[srcOff + 0];
                dst[dstOff + 1] = src[srcOff + 1];
                dst[dstOff + 2] = src[srcOff + 2];
                dst[dstOff + 3] = 255;
            }
            else
            {
                byte g = src[srcOff];
                dst[dstOff + 0] = g;
                dst[dstOff + 1] = g;
                dst[dstOff + 2] = g;
                dst[dstOff + 3] = 255;
            }
        }

        // ------------------------------------------------------------------
        // Meshes, Skinning & Morph Targets
        // ------------------------------------------------------------------

        private (Dictionary<string, ImportedMorph> ByPath, Dictionary<int, ImportedMorph> ByNode) BuildMorphLookup(List<ImportedMorph>? morphList)
        {
            var byPath = new Dictionary<string, ImportedMorph>(StringComparer.Ordinal);
            var byNode = new Dictionary<int, ImportedMorph>();
            if (!_exportBlendShape || morphList == null)
            {
                return (byPath, byNode);
            }

            for (int i = 0; i < morphList.Count; i++)
            {
                var morph = morphList[i];
                if (morph == null || morph.Channels == null || morph.Channels.Count == 0)
                {
                    continue;
                }

                var normPath = NormalizeFramePath(morph.Path);
                if (!byPath.ContainsKey(normPath))
                {
                    byPath[normPath] = morph;
                }

                if (TryResolveNodeIndex(morph.Path, out int morphNodeIdx) && !byNode.ContainsKey(morphNodeIdx))
                {
                    byNode[morphNodeIdx] = morph;
                }
            }

            return (byPath, byNode);
        }

        private void BuildSkeletonGroups(List<ImportedMesh>? meshList)
        {
            _meshIndexToSkeletonGroup.Clear();
            if (!_exportSkins || meshList == null || meshList.Count == 0)
            {
                return;
            }

            var entries = new List<(int MeshIndex, HashSet<int> Joints, int CommonAncestor)>();
            for (int m = 0; m < meshList.Count; m++)
            {
                var mesh = meshList[m];
                if (mesh?.BoneList == null || mesh.BoneList.Count == 0)
                {
                    continue;
                }

                var joints = new HashSet<int>();
                for (int b = 0; b < mesh.BoneList.Count; b++)
                {
                    if (TryResolveNodeIndex(mesh.BoneList[b].Path, out int jointIdx))
                    {
                        joints.Add(jointIdx);
                    }
                }

                if (joints.Count == 0)
                {
                    continue;
                }

                int? ca = FindCommonAncestor(joints.ToList());
                if (ca.HasValue)
                {
                    entries.Add((m, joints, ca.Value));
                }
            }

            if (entries.Count == 0)
            {
                return;
            }

            int count = entries.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    if (Find(a) == Find(b))
                    {
                        continue;
                    }

                    bool merge = entries[a].Joints.Overlaps(entries[b].Joints);
                    if (!merge)
                    {
                        int caA = entries[a].CommonAncestor;
                        int caB = entries[b].CommonAncestor;
                        if (IsAncestorOrSame(caA, caB) || IsAncestorOrSame(caB, caA))
                        {
                            merge = true;
                        }
                        else
                        {
                            int? shared = FindCommonAncestor(new[] { caA, caB });
                            if (shared.HasValue && _nodeParentIndices[shared.Value] >= 0)
                            {
                                // Share a non-scene-root skeleton container (e.g. Character/root).
                                merge = true;
                            }
                            else if (_nodeParentIndices[caA] >= 0 && _nodeParentIndices[caA] == _nodeParentIndices[caB])
                            {
                                // Direct sibling bone roots under the same immediate parent.
                                merge = true;
                            }
                        }
                    }

                    if (merge)
                    {
                        Union(a, b);
                    }
                }
            }

            var grouped = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                int r = Find(i);
                if (!grouped.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    grouped[r] = list;
                }
                list.Add(i);
            }

            foreach (var list in grouped.Values)
            {
                var allJoints = new HashSet<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    allJoints.UnionWith(entries[list[i]].Joints);
                }

                int? groupCommon = FindCommonAncestor(allJoints.ToList());
                if (!groupCommon.HasValue)
                {
                    continue;
                }

                // Blender (glTF-Blender-IO) creates one Armature at the deepest common ancestor of
                // (joints + skeleton), stepping up to parent if that ancestor is itself a joint.
                // Setting skin.skeleton to the non-joint Armature container keeps multi-part characters
                // unified under one Armature with consistent Armature-space inverse bind matrices.
                int armatureNode = allJoints.Contains(groupCommon.Value)
                    ? _nodeParentIndices[groupCommon.Value]
                    : groupCommon.Value;
                int skeletonNode = armatureNode >= 0 ? armatureNode : groupCommon.Value;
                var group = new SkinSkeletonGroup(skeletonNode, armatureNode);

                for (int i = 0; i < list.Count; i++)
                {
                    _meshIndexToSkeletonGroup[entries[list[i]].MeshIndex] = group;
                }
            }
        }

        private void ExportMesh(
            ImportedMesh? mesh,
            int meshIndex,
            Dictionary<string, ImportedMorph> morphByPath,
            Dictionary<int, ImportedMorph> morphByNode)
        {
            if (mesh == null || mesh.VertexList == null || mesh.VertexList.Count == 0)
            {
                return;
            }

            int vertexCount = mesh.VertexList.Count;

            // 1. Collect valid primitives per submesh first so we skip meshes with 0 valid triangles.
            var submeshList = mesh.SubmeshList;
            var primitivePayloads = new List<(List<int> Indices, int MaxIndex, int? MaterialIndex)>();

            if (submeshList != null && submeshList.Count > 0)
            {
                for (int s = 0; s < submeshList.Count; s++)
                {
                    var sub = submeshList[s];
                    var faceList = sub?.FaceList;
                    if (faceList == null || faceList.Count == 0)
                    {
                        continue;
                    }

                    var indices = new List<int>(faceList.Count * 3);
                    int maxIdx = 0;
                    int baseVertex = sub!.BaseVertex;

                    for (int f = 0; f < faceList.Count; f++)
                    {
                        if (!TryResolveFaceIndices(faceList[f], baseVertex, vertexCount, out int i0, out int i1, out int i2))
                        {
                            continue;
                        }

                        // ModelConverter already reversed Unity CW -> Right-Handed CCW winding.
                        indices.Add(i0);
                        indices.Add(i1);
                        indices.Add(i2);
                        if (i0 > maxIdx) maxIdx = i0;
                        if (i1 > maxIdx) maxIdx = i1;
                        if (i2 > maxIdx) maxIdx = i2;
                    }

                    if (indices.Count == 0)
                    {
                        continue;
                    }

                    int? matIdx = null;
                    if (!string.IsNullOrEmpty(sub.Material) && _materialIndexByName.TryGetValue(sub.Material, out int foundMat))
                    {
                        matIdx = foundMat;
                    }

                    primitivePayloads.Add((indices, maxIdx, matIdx));
                }
            }

            if (primitivePayloads.Count == 0)
            {
                return;
            }

            string meshName = string.IsNullOrWhiteSpace(mesh.Path)
                ? $"Mesh_{_meshes.Count}"
                : mesh.Path.Substring(mesh.Path.LastIndexOf('/') + 1);

            // 2. Resolve skinning joints, mesh attachment node, and optional Armature-space canonicalization.
            bool isSkinned = _exportSkins && mesh.BoneList != null && mesh.BoneList.Count > 0;
            List<int>? skinJointNodes = null;
            int resolvedTargetNode = TryResolveNodeIndex(mesh.Path, out int foundTargetNode) ? foundTargetNode : -1;

            if (isSkinned)
            {
                int boneCount = mesh.BoneList!.Count;
                skinJointNodes = new List<int>(boneCount);

                var meshWorldFallback = resolvedTargetNode >= 0
                    ? _nodeGlobalRowMatrices[resolvedTargetNode]
                    : System.Numerics.Matrix4x4.Identity;

                int fallbackParent = _sceneRootNodes.Count > 0 ? _sceneRootNodes[0] : -1;
                var usedJointSet = new HashSet<int>();

                for (int b = 0; b < boneCount; b++)
                {
                    var bone = mesh.BoneList[b];
                    if (TryResolveNodeIndex(bone.Path, out int resolvedJoint))
                    {
                        if (usedJointSet.Add(resolvedJoint))
                        {
                            skinJointNodes.Add(resolvedJoint);
                        }
                        else
                        {
                            // glTF 2.0 requires unique node indices in skin.joints; create an identity child
                            // under the resolved joint so duplicate bone slots still inherit all animations.
                            int dupNode = CreateExtraNode($"{_nodes[resolvedJoint].Name}_Dup_{b}", resolvedJoint);
                            usedJointSet.Add(dupNode);
                            skinJointNodes.Add(dupNode);
                        }
                    }
                    else
                    {
                        string boneName = string.IsNullOrWhiteSpace(bone.Path)
                            ? $"Bone_{b}"
                            : bone.Path.Substring(bone.Path.LastIndexOf('/') + 1);

                        System.Numerics.Matrix4x4? localHint = null;
                        var ibmRow = ConvertUnityBindPoseToRowMatrix(bone.Matrix);
                        if (System.Numerics.Matrix4x4.Invert(ibmRow, out var invIbm))
                        {
                            var jointGlobal = invIbm * meshWorldFallback;
                            var parentGlobal = fallbackParent >= 0 ? _nodeGlobalRowMatrices[fallbackParent] : System.Numerics.Matrix4x4.Identity;
                            if (System.Numerics.Matrix4x4.Invert(parentGlobal, out var invParent))
                            {
                                localHint = jointGlobal * invParent;
                            }
                        }

                        int placeholderNode = CreateExtraNode(boneName, fallbackParent, localHint);
                        usedJointSet.Add(placeholderNode);
                        skinJointNodes.Add(placeholderNode);
                    }
                }
            }

            // Determine the node that will own this mesh (creating a clean child/root node when needed).
            int meshNodeIdx;
            if (resolvedTargetNode >= 0)
            {
                bool targetIsAncestorOfOwnJoint = false;
                if (isSkinned && skinJointNodes != null)
                {
                    for (int j = 0; j < skinJointNodes.Count; j++)
                    {
                        if (IsAncestorOrSame(resolvedTargetNode, skinJointNodes[j]))
                        {
                            targetIsAncestorOfOwnJoint = true;
                            break;
                        }
                    }
                }

                if (_nodes[resolvedTargetNode].Mesh.HasValue || targetIsAncestorOfOwnJoint)
                {
                    meshNodeIdx = CreateExtraNode($"{meshName}_Mesh", resolvedTargetNode);
                }
                else
                {
                    meshNodeIdx = resolvedTargetNode;
                }
            }
            else
            {
                int parentForOrphan = _sceneRootNodes.Count > 0 ? _sceneRootNodes[0] : -1;
                meshNodeIdx = CreateExtraNode(meshName, parentForOrphan);
            }

            // Compute Armature-space canonicalization transform A = G_mesh * inv(G_arma) for skinned meshes.
            // Because (v * A) * (inv(A) * IBM) * G_joint(t) === v * IBM * G_joint(t), this is an exact
            // algebraic identity for runtime glTF skinning while unifying multi-part Unity characters
            // (whose SkinnedMeshRenderers carry differing local offsets/rotations/scales) into a single
            // consistent Armature rest space for Blender and other DCC tools.
            bool applyMeshToArmature = false;
            var meshToArmature = System.Numerics.Matrix4x4.Identity;
            var invMeshToArmature = System.Numerics.Matrix4x4.Identity;
            var meshNormalMatrix = System.Numerics.Matrix4x4.Identity;
            bool flipWinding = false;
            int armatureNode = -1;
            int? skinSkeletonNode = null;
            System.Numerics.Matrix4x4[]? ibmMatrices = null;

            if (isSkinned && skinJointNodes != null && mesh.BoneList != null)
            {
                if (_meshIndexToSkeletonGroup.TryGetValue(meshIndex, out var skelGroup))
                {
                    skinSkeletonNode = skelGroup.SkeletonNode;
                    armatureNode = skelGroup.ArmatureNode;
                }
                else
                {
                    int? localCommon = FindCommonAncestor(skinJointNodes);
                    if (localCommon.HasValue)
                    {
                        var jointSet = new HashSet<int>(skinJointNodes);
                        armatureNode = jointSet.Contains(localCommon.Value)
                            ? _nodeParentIndices[localCommon.Value]
                            : localCommon.Value;
                        skinSkeletonNode = armatureNode >= 0 ? armatureNode : localCommon.Value;
                    }
                }

                int boneCount = mesh.BoneList.Count;
                ibmMatrices = new System.Numerics.Matrix4x4[boneCount];
                var ibmFinite = new bool[boneCount];
                var meshGlobalRow = _nodeGlobalRowMatrices[meshNodeIdx];
                var armaGlobalRow = armatureNode >= 0 ? _nodeGlobalRowMatrices[armatureNode] : System.Numerics.Matrix4x4.Identity;

                for (int b = 0; b < boneCount; b++)
                {
                    var ibmRow = ConvertUnityBindPoseToRowMatrix(mesh.BoneList[b].Matrix);
                    if (IsFiniteInvertibleAffineMatrix(ibmRow))
                    {
                        ibmMatrices[b] = ibmRow;
                        ibmFinite[b] = true;
                    }
                }

                if (System.Numerics.Matrix4x4.Invert(armaGlobalRow, out var invArmaGlobal))
                {
                    var candidateA = meshGlobalRow * invArmaGlobal;
                    candidateA.M14 = 0f;
                    candidateA.M24 = 0f;
                    candidateA.M34 = 0f;
                    candidateA.M44 = 1f;

                    if (!IsMatrixNearlyIdentity(candidateA)
                        && IsFiniteInvertibleAffineMatrix(candidateA)
                        && System.Numerics.Matrix4x4.Invert(candidateA, out var candidateInvA))
                    {
                        candidateInvA.M14 = 0f;
                        candidateInvA.M24 = 0f;
                        candidateInvA.M34 = 0f;
                        candidateInvA.M44 = 1f;

                        double errBefore = 0.0;
                        double errAfter = 0.0;
                        int validCount = 0;

                        for (int b = 0; b < boneCount; b++)
                        {
                            if (!ibmFinite[b])
                            {
                                continue;
                            }

                            var localJointInArma = _nodeGlobalRowMatrices[skinJointNodes[b]] * invArmaGlobal;
                            errBefore += MatrixDistanceFromIdentity(ibmMatrices[b] * localJointInArma);
                            errAfter += MatrixDistanceFromIdentity((candidateInvA * ibmMatrices[b]) * localJointInArma);
                            validCount++;
                        }

                        if (validCount > 0)
                        {
                            double avgBefore = errBefore / validCount;
                            double avgAfter = errAfter / validCount;
                            if (avgAfter < avgBefore * 0.5 || (avgAfter < 1e-2 && avgBefore > 1e-2))
                            {
                                applyMeshToArmature = true;
                                meshToArmature = candidateA;
                                invMeshToArmature = candidateInvA;
                                meshNormalMatrix = BuildNormalMatrix(candidateA, out flipWinding);
                            }
                        }
                    }
                }

                // Finalize IBMs (apply inv(A) when canonicalized, and compute clean fallbacks for any non-finite IBM).
                var effectiveMeshRefRow = applyMeshToArmature ? armaGlobalRow : meshGlobalRow;
                for (int b = 0; b < boneCount; b++)
                {
                    if (ibmFinite[b])
                    {
                        if (applyMeshToArmature)
                        {
                            var adjusted = invMeshToArmature * ibmMatrices[b];
                            adjusted.M14 = 0f;
                            adjusted.M24 = 0f;
                            adjusted.M34 = 0f;
                            adjusted.M44 = 1f;
                            ibmMatrices[b] = adjusted;
                        }
                    }
                    else
                    {
                        var jointGlobalRow = _nodeGlobalRowMatrices[skinJointNodes[b]];
                        if (System.Numerics.Matrix4x4.Invert(jointGlobalRow, out var invJoint))
                        {
                            var fallbackIbm = effectiveMeshRefRow * invJoint;
                            fallbackIbm.M14 = 0f;
                            fallbackIbm.M24 = 0f;
                            fallbackIbm.M34 = 0f;
                            fallbackIbm.M44 = 1f;
                            ibmMatrices[b] = fallbackIbm;
                        }
                        else
                        {
                            ibmMatrices[b] = System.Numerics.Matrix4x4.Identity;
                        }
                    }
                }
            }

            if (flipWinding)
            {
                for (int p = 0; p < primitivePayloads.Count; p++)
                {
                    var idxList = primitivePayloads[p].Indices;
                    for (int t = 0; t + 2 < idxList.Count; t += 3)
                    {
                        (idxList[t + 1], idxList[t + 2]) = (idxList[t + 2], idxList[t + 1]);
                    }
                }
            }

            // 3. Build shared vertex attribute accessors.
            var sharedAttributes = new Dictionary<string, int>(StringComparer.Ordinal);

            var positions = new System.Numerics.Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var v = mesh.VertexList[i].Vertex;
                var pos = new System.Numerics.Vector3(
                    SanitizeFloat(v.X * _scaleFactor),
                    SanitizeFloat(v.Y * _scaleFactor),
                    SanitizeFloat(v.Z * _scaleFactor));
                if (applyMeshToArmature)
                {
                    pos = System.Numerics.Vector3.Transform(pos, meshToArmature);
                }
                positions[i] = new System.Numerics.Vector3(
                    SanitizeFloat(pos.X),
                    SanitizeFloat(pos.Y),
                    SanitizeFloat(pos.Z));
            }
            sharedAttributes["POSITION"] = _buffer.AddVec3Accessor(positions, includeMinMax: true, target: TargetArrayBuffer);

            if (mesh.hasNormal)
            {
                var normals = new System.Numerics.Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var n = mesh.VertexList[i].Normal;
                    var nv = new System.Numerics.Vector3(SanitizeFloat(n.X), SanitizeFloat(n.Y), SanitizeFloat(n.Z));
                    if (applyMeshToArmature)
                    {
                        nv = System.Numerics.Vector3.TransformNormal(nv, meshNormalMatrix);
                    }
                    normals[i] = NormalizeVector3Safe(nv, System.Numerics.Vector3.UnitY);
                }
                sharedAttributes["NORMAL"] = _buffer.AddVec3Accessor(normals, includeMinMax: false, target: TargetArrayBuffer);
            }

            if (mesh.hasTangent)
            {
                var tangents = new System.Numerics.Vector4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var t = mesh.VertexList[i].Tangent;
                    var tv = new System.Numerics.Vector3(SanitizeFloat(t.X), SanitizeFloat(t.Y), SanitizeFloat(t.Z));
                    if (applyMeshToArmature)
                    {
                        tv = System.Numerics.Vector3.TransformNormal(tv, meshToArmature);
                    }
                    var xyz = NormalizeVector3Safe(tv, System.Numerics.Vector3.UnitX);
                    float w = SanitizeFloat(t.W, 1f) < 0f ? -1f : 1f;
                    if (flipWinding) w = -w;
                    tangents[i] = new System.Numerics.Vector4(xyz.X, xyz.Y, xyz.Z, w);
                }
                sharedAttributes["TANGENT"] = _buffer.AddVec4Accessor(tangents, target: TargetArrayBuffer);
            }

            if (mesh.hasUV != null)
            {
                int gltfTexCoordSlot = 0;
                for (int uv = 0; uv < mesh.hasUV.Length; uv++)
                {
                    if (!mesh.hasUV[uv])
                    {
                        continue;
                    }

                    bool hasAnyUvData = false;
                    var uvs = new System.Numerics.Vector2[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var uvChannels = mesh.VertexList[i].UV;
                        if (uvChannels != null && uv < uvChannels.Length && uvChannels[uv] != null && uvChannels[uv].Length >= 2)
                        {
                            hasAnyUvData = true;
                            float u = SanitizeFloat(uvChannels[uv][0]);
                            // Unity textures are exported flipped top-down (flip=true), and glTF (0,0) is TOP-LEFT:
                            // v_gltf = 1 - v_unity.
                            float v = SanitizeFloat(1f - uvChannels[uv][1]);
                            uvs[i] = new System.Numerics.Vector2(u, v);
                        }
                        else if (uvChannels != null && uv < uvChannels.Length && uvChannels[uv] != null && uvChannels[uv].Length == 1)
                        {
                            hasAnyUvData = true;
                            uvs[i] = new System.Numerics.Vector2(SanitizeFloat(uvChannels[uv][0]), 1f);
                        }
                    }

                    if (hasAnyUvData)
                    {
                        sharedAttributes[$"TEXCOORD_{gltfTexCoordSlot}"] = _buffer.AddVec2Accessor(uvs, target: TargetArrayBuffer);
                        gltfTexCoordSlot++;
                    }
                }
            }

            if (mesh.hasColor)
            {
                var colors = new System.Numerics.Vector4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var c = mesh.VertexList[i].Color;
                    colors[i] = new System.Numerics.Vector4(
                        Clamp01(SanitizeFloat(c.R, 1f)),
                        Clamp01(SanitizeFloat(c.G, 1f)),
                        Clamp01(SanitizeFloat(c.B, 1f)),
                        Clamp01(SanitizeFloat(c.A, 1f)));
                }
                sharedAttributes["COLOR_0"] = _buffer.AddVec4Accessor(colors, target: TargetArrayBuffer);
            }

            if (isSkinned && mesh.BoneList != null)
            {
                int boneCount = mesh.BoneList.Count;
                var jointsData = new ushort[vertexCount * 4];
                var weightsData = new System.Numerics.Vector4[vertexCount];

                for (int i = 0; i < vertexCount; i++)
                {
                    var vert = mesh.VertexList[i];
                    ExtractNormalizedTop4Influences(vert.BoneIndices, vert.Weights, boneCount, out var j4, out var w4);

                    int jOff = i * 4;
                    jointsData[jOff + 0] = j4.J0;
                    jointsData[jOff + 1] = j4.J1;
                    jointsData[jOff + 2] = j4.J2;
                    jointsData[jOff + 3] = j4.J3;
                    weightsData[i] = w4;
                }

                sharedAttributes["JOINTS_0"] = _buffer.AddUShort4Accessor(jointsData, target: TargetArrayBuffer);
                sharedAttributes["WEIGHTS_0"] = _buffer.AddVec4Accessor(weightsData, target: TargetArrayBuffer);
            }

            // 4. Morph targets (BlendShapes).
            var normMeshPath = NormalizeFramePath(mesh.Path);
            List<Dictionary<string, int>>? morphTargets = null;
            List<string>? targetNames = null;
            List<float>? targetFullWeights = null;

            ImportedMorph? morph = null;
            if (_exportBlendShape)
            {
                if (!morphByPath.TryGetValue(normMeshPath, out morph) && resolvedTargetNode >= 0)
                {
                    morphByNode.TryGetValue(resolvedTargetNode, out morph);
                }
            }

            if (morph != null && morph.Channels != null && morph.Channels.Count > 0)
            {
                morphTargets = new List<Dictionary<string, int>>(morph.Channels.Count);
                targetNames = new List<string>(morph.Channels.Count);
                targetFullWeights = new List<float>(morph.Channels.Count);

                for (int c = 0; c < morph.Channels.Count; c++)
                {
                    var channel = morph.Channels[c];
                    if (channel == null || channel.KeyframeList == null || channel.KeyframeList.Count == 0)
                    {
                        continue;
                    }

                    // Select the primary full-weight keyframe (usually the last/highest-weight keyframe).
                    var keyframe = channel.KeyframeList[channel.KeyframeList.Count - 1];
                    for (int k = 0; k < channel.KeyframeList.Count; k++)
                    {
                        if (channel.KeyframeList[k].Weight >= keyframe.Weight)
                        {
                            keyframe = channel.KeyframeList[k];
                        }
                    }

                    var posDeltas = new System.Numerics.Vector3[vertexCount];
                    var normDeltas = (keyframe.hasNormals && mesh.hasNormal) ? new System.Numerics.Vector3[vertexCount] : null;
                    var tanDeltas = (keyframe.hasTangents && mesh.hasTangent) ? new System.Numerics.Vector3[vertexCount] : null;

                    if (keyframe.VertexList != null)
                    {
                        for (int mvIdx = 0; mvIdx < keyframe.VertexList.Count; mvIdx++)
                        {
                            var mv = keyframe.VertexList[mvIdx];
                            if (mv == null || mv.Index >= (uint)vertexCount)
                            {
                                continue;
                            }

                            int vIdx = (int)mv.Index;
                            var basePos = mesh.VertexList[vIdx].Vertex;
                            // ImportedMorphVertex.Vertex.Vertex is absolute (base + delta), already in RH space.
                            var dp = new System.Numerics.Vector3(
                                SanitizeFloat((mv.Vertex.Vertex.X - basePos.X) * _scaleFactor),
                                SanitizeFloat((mv.Vertex.Vertex.Y - basePos.Y) * _scaleFactor),
                                SanitizeFloat((mv.Vertex.Vertex.Z - basePos.Z) * _scaleFactor));
                            if (applyMeshToArmature)
                            {
                                dp = System.Numerics.Vector3.TransformNormal(dp, meshToArmature);
                            }
                            posDeltas[vIdx] = new System.Numerics.Vector3(
                                SanitizeFloat(dp.X),
                                SanitizeFloat(dp.Y),
                                SanitizeFloat(dp.Z));

                            if (normDeltas != null)
                            {
                                // ImportedMorphVertex.Vertex.Normal is already a delta in RH space.
                                var dn = new System.Numerics.Vector3(
                                    SanitizeFloat(mv.Vertex.Normal.X),
                                    SanitizeFloat(mv.Vertex.Normal.Y),
                                    SanitizeFloat(mv.Vertex.Normal.Z));
                                if (applyMeshToArmature)
                                {
                                    dn = System.Numerics.Vector3.TransformNormal(dn, meshNormalMatrix);
                                }
                                normDeltas[vIdx] = new System.Numerics.Vector3(
                                    SanitizeFloat(dn.X),
                                    SanitizeFloat(dn.Y),
                                    SanitizeFloat(dn.Z));
                            }

                            if (tanDeltas != null)
                            {
                                // glTF morph target TANGENT is VEC3 (no W component).
                                var dt = new System.Numerics.Vector3(
                                    SanitizeFloat(mv.Vertex.Tangent.X),
                                    SanitizeFloat(mv.Vertex.Tangent.Y),
                                    SanitizeFloat(mv.Vertex.Tangent.Z));
                                if (applyMeshToArmature)
                                {
                                    dt = System.Numerics.Vector3.TransformNormal(dt, meshToArmature);
                                }
                                tanDeltas[vIdx] = new System.Numerics.Vector3(
                                    SanitizeFloat(dt.X),
                                    SanitizeFloat(dt.Y),
                                    SanitizeFloat(dt.Z));
                            }
                        }
                    }

                    var targetDict = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["POSITION"] = _buffer.AddVec3Accessor(posDeltas, includeMinMax: true, target: TargetArrayBuffer)
                    };
                    if (normDeltas != null)
                    {
                        targetDict["NORMAL"] = _buffer.AddVec3Accessor(normDeltas, includeMinMax: false, target: TargetArrayBuffer);
                    }
                    if (tanDeltas != null)
                    {
                        targetDict["TANGENT"] = _buffer.AddVec3Accessor(tanDeltas, includeMinMax: false, target: TargetArrayBuffer);
                    }

                    morphTargets.Add(targetDict);
                    targetNames.Add(string.IsNullOrEmpty(channel.Name) ? $"Target_{morphTargets.Count - 1}" : channel.Name);
                    float fullWeight = SanitizeFloat(keyframe.Weight, 100f);
                    targetFullWeights.Add(fullWeight > 1e-4f ? fullWeight : 100f);
                }

                if (morphTargets.Count == 0)
                {
                    morphTargets = null;
                    targetNames = null;
                    targetFullWeights = null;
                }
            }

            // 5. Build glTF primitives & mesh object.
            var gltfMesh = new GltfMesh
            {
                Name = meshName
            };

            if (morphTargets != null && targetNames != null)
            {
                gltfMesh.Weights = new float[morphTargets.Count]; // default rest pose = 0.0
                gltfMesh.TargetNames = targetNames;
            }

            for (int p = 0; p < primitivePayloads.Count; p++)
            {
                var (indices, maxIdx, matIdx) = primitivePayloads[p];
                int indicesAccessor = _buffer.AddIndicesAccessor(indices, maxIdx);

                gltfMesh.Primitives.Add(new GltfPrimitive
                {
                    Attributes = new Dictionary<string, int>(sharedAttributes, StringComparer.Ordinal),
                    Indices = indicesAccessor,
                    Material = matIdx,
                    Targets = morphTargets
                });
            }

            int meshIdx = _meshes.Count;
            _meshes.Add(gltfMesh);
            _nodes[meshNodeIdx].Mesh = meshIdx;

            if (morphTargets != null && targetNames != null && targetFullWeights != null)
            {
                var info = new MorphMeshTargetInfo(meshNodeIdx, targetNames, targetFullWeights);
                _morphTargetsByNode[meshNodeIdx] = info;
                if (!string.IsNullOrEmpty(normMeshPath) && !_morphTargetsByPath.ContainsKey(normMeshPath))
                {
                    _morphTargetsByPath[normMeshPath] = info;
                }

                for (int t = 0; t < targetNames.Count; t++)
                {
                    var chanName = targetNames[t];
                    if (_uniqueMorphChannelLookup.TryGetValue(chanName, out var existing))
                    {
                        if (existing.HasValue && (existing.Value.MeshNodeIndex != meshNodeIdx || existing.Value.TargetIndex != t))
                        {
                            _uniqueMorphChannelLookup[chanName] = null;
                        }
                    }
                    else
                    {
                        _uniqueMorphChannelLookup[chanName] = (meshNodeIdx, t, targetFullWeights[t]);
                    }
                }
            }

            // 6. Create glTF skin & inverseBindMatrices.
            if (isSkinned && skinJointNodes != null && ibmMatrices != null)
            {
                int ibmAccessor = _buffer.AddMat4Accessor(ibmMatrices);
                int skinIdx = _skins.Count;
                _skins.Add(new GltfSkin
                {
                    Name = $"{meshName}_Skin",
                    InverseBindMatrices = ibmAccessor,
                    Skeleton = skinSkeletonNode,
                    Joints = skinJointNodes
                });
                _nodes[meshNodeIdx].Skin = skinIdx;
            }
        }

        private System.Numerics.Matrix4x4 ConvertUnityBindPoseToRowMatrix(Matrix4x4 matrix)
        {
            // See FbxSharpieExporter.UnityBindPoseToNumerics:
            // AssetStudio.Matrix4x4 stores Unity's translation in row 3 (M30, M31, M32),
            // which maps directly to System.Numerics row-vector M41, M42, M43.
            // Because glTF MAT4 is column-major and P_row = GlobalMesh_row * inv(GlobalJoint_row),
            // (P_row)^T = inv(GlobalJoint_col) * GlobalMesh_col, whose column-major floats are
            // identically [M11,M12,M13,M14, M21,M22,M23,M24, M31,M32,M33,M34, M41,M42,M43,M44]!
            // Under uniform scaleFactor s, IBM' = S * IBM * S^-1 scales only M41, M42, M43 by s.
            return new System.Numerics.Matrix4x4(
                SanitizeFloat(matrix[0, 0]), SanitizeFloat(matrix[0, 1]), SanitizeFloat(matrix[0, 2]), 0f,
                SanitizeFloat(matrix[1, 0]), SanitizeFloat(matrix[1, 1]), SanitizeFloat(matrix[1, 2]), 0f,
                SanitizeFloat(matrix[2, 0]), SanitizeFloat(matrix[2, 1]), SanitizeFloat(matrix[2, 2]), 0f,
                SanitizeFloat(matrix[3, 0] * _scaleFactor), SanitizeFloat(matrix[3, 1] * _scaleFactor), SanitizeFloat(matrix[3, 2] * _scaleFactor), 1f);
        }

        private static bool IsFiniteInvertibleAffineMatrix(System.Numerics.Matrix4x4 m)
        {
            float det = m.GetDeterminant();
            return float.IsFinite(det) && MathF.Abs(det) > 1e-12f;
        }

        private static bool IsMatrixNearlyIdentity(System.Numerics.Matrix4x4 m, float eps = 1e-5f)
        {
            return MathF.Abs(m.M11 - 1f) < eps && MathF.Abs(m.M12) < eps && MathF.Abs(m.M13) < eps && MathF.Abs(m.M14) < eps
                && MathF.Abs(m.M21) < eps && MathF.Abs(m.M22 - 1f) < eps && MathF.Abs(m.M23) < eps && MathF.Abs(m.M24) < eps
                && MathF.Abs(m.M31) < eps && MathF.Abs(m.M32) < eps && MathF.Abs(m.M33 - 1f) < eps && MathF.Abs(m.M34) < eps
                && MathF.Abs(m.M41) < eps && MathF.Abs(m.M42) < eps && MathF.Abs(m.M43) < eps && MathF.Abs(m.M44 - 1f) < eps;
        }

        private static double MatrixDistanceFromIdentity(System.Numerics.Matrix4x4 m)
        {
            double d = 0.0;
            d += Math.Abs(m.M11 - 1.0) + Math.Abs(m.M12) + Math.Abs(m.M13) + Math.Abs(m.M14);
            d += Math.Abs(m.M21) + Math.Abs(m.M22 - 1.0) + Math.Abs(m.M23) + Math.Abs(m.M24);
            d += Math.Abs(m.M31) + Math.Abs(m.M32) + Math.Abs(m.M33 - 1.0) + Math.Abs(m.M34);
            d += Math.Abs(m.M41) + Math.Abs(m.M42) + Math.Abs(m.M43) + Math.Abs(m.M44 - 1.0);
            return double.IsFinite(d) ? d : double.PositiveInfinity;
        }

        private static System.Numerics.Matrix4x4 BuildNormalMatrix(System.Numerics.Matrix4x4 affineMatrix, out bool flipWinding)
        {
            var upper = new System.Numerics.Matrix4x4(
                affineMatrix.M11, affineMatrix.M12, affineMatrix.M13, 0f,
                affineMatrix.M21, affineMatrix.M22, affineMatrix.M23, 0f,
                affineMatrix.M31, affineMatrix.M32, affineMatrix.M33, 0f,
                0f, 0f, 0f, 1f);

            float det = upper.GetDeterminant();
            flipWinding = det < -1e-9f;
            if (MathF.Abs(det) > 1e-12f && System.Numerics.Matrix4x4.Invert(upper, out var invUpper))
            {
                return System.Numerics.Matrix4x4.Transpose(invUpper);
            }

            return upper;
        }

        private static void ExtractNormalizedTop4Influences(
            int[]? boneIndices,
            float[]? weights,
            int boneCount,
            out (ushort J0, ushort J1, ushort J2, ushort J3) joints,
            out System.Numerics.Vector4 normalizedWeights)
        {
            Span<int> topJ = stackalloc int[4];
            Span<float> topW = stackalloc float[4];
            int count = 0;

            if (boneIndices != null && weights != null)
            {
                int len = Math.Min(boneIndices.Length, weights.Length);
                for (int k = 0; k < len; k++)
                {
                    int bi = boneIndices[k];
                    float w = weights[k];
                    if (bi < 0 || bi >= boneCount || bi >= ushort.MaxValue || !float.IsFinite(w) || w <= 0f)
                    {
                        continue;
                    }

                    // Merge duplicate bone indices on the same vertex
                    int existingSlot = -1;
                    for (int s = 0; s < count; s++)
                    {
                        if (topJ[s] == bi)
                        {
                            existingSlot = s;
                            break;
                        }
                    }

                    if (existingSlot >= 0)
                    {
                        topW[existingSlot] += w;
                        continue;
                    }

                    if (count < 4)
                    {
                        topJ[count] = bi;
                        topW[count] = w;
                        count++;
                    }
                    else
                    {
                        int minIdx = 0;
                        for (int s = 1; s < 4; s++)
                        {
                            if (topW[s] < topW[minIdx])
                            {
                                minIdx = s;
                            }
                        }
                        if (w > topW[minIdx])
                        {
                            topJ[minIdx] = bi;
                            topW[minIdx] = w;
                        }
                    }
                }
            }

            // Sort descending by weight for deterministic output
            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    if (topW[b] > topW[a])
                    {
                        (topW[a], topW[b]) = (topW[b], topW[a]);
                        (topJ[a], topJ[b]) = (topJ[b], topJ[a]);
                    }
                }
            }

            double sum = 0.0;
            for (int i = 0; i < count; i++)
            {
                sum += topW[i];
            }

            if (count == 0 || sum <= 1e-8)
            {
                joints = (0, 0, 0, 0);
                normalizedWeights = new System.Numerics.Vector4(1f, 0f, 0f, 0f);
                return;
            }

            float w0 = (float)(topW[0] / sum);
            float w1 = count > 1 ? (float)(topW[1] / sum) : 0f;
            float w2 = count > 2 ? (float)(topW[2] / sum) : 0f;
            float w3 = count > 3 ? (float)(topW[3] / sum) : 0f;

            // Adjust largest weight slightly so floating-point sum is as close to 1.0 as representable.
            float total = w0 + w1 + w2 + w3;
            w0 = Math.Max(0f, w0 + (1f - total));

            joints = (
                w0 > 0f ? (ushort)topJ[0] : (ushort)0,
                w1 > 0f ? (ushort)topJ[1] : (ushort)0,
                w2 > 0f ? (ushort)topJ[2] : (ushort)0,
                w3 > 0f ? (ushort)topJ[3] : (ushort)0);
            normalizedWeights = new System.Numerics.Vector4(w0, w1, w2, w3);
        }

        private static bool TryResolveFaceIndices(ImportedFace? face, int baseVertex, int vertexCount, out int i0, out int i1, out int i2)
        {
            i0 = i1 = i2 = 0;
            if (face?.VertexIndices == null || face.VertexIndices.Length < 3)
            {
                return false;
            }

            i0 = face.VertexIndices[0] + baseVertex;
            i1 = face.VertexIndices[1] + baseVertex;
            i2 = face.VertexIndices[2] + baseVertex;
            if ((uint)i0 < (uint)vertexCount && (uint)i1 < (uint)vertexCount && (uint)i2 < (uint)vertexCount)
            {
                return true;
            }

            i0 = face.VertexIndices[0];
            i1 = face.VertexIndices[1];
            i2 = face.VertexIndices[2];
            return (uint)i0 < (uint)vertexCount && (uint)i1 < (uint)vertexCount && (uint)i2 < (uint)vertexCount;
        }

        // ------------------------------------------------------------------
        // Animations (TRS + Shortest-Arc Quaternions + Morph Weights)
        // ------------------------------------------------------------------

        private void ExportAnimation(ImportedKeyframedAnimation? anim)
        {
            if (anim == null || anim.TrackList == null || anim.TrackList.Count == 0)
            {
                return;
            }

            var gltfAnim = new GltfAnimation
            {
                Name = string.IsNullOrEmpty(anim.Name) ? $"Animation_{_animations.Count}" : anim.Name
            };

            // Group morph weight curves by resolved mesh node so each morphed mesh gets one combined weights channel.
            var morphCurvesByMeshNode = new Dictionary<int, Dictionary<int, (List<ImportedKeyframe<float>> Keys, float FullWeight)>>();
            var usedTrsTargets = new HashSet<(int Node, string Path)>();

            for (int tIdx = 0; tIdx < anim.TrackList.Count; tIdx++)
            {
                var track = anim.TrackList[tIdx];
                if (track == null || track.Path == null)
                {
                    continue;
                }

                if (TryResolveNodeIndex(track.Path, out int nodeIdx))
                {
                    if (track.Translations != null && track.Translations.Count > 0 && usedTrsTargets.Add((nodeIdx, "translation")))
                    {
                        AddVec3Channel(gltfAnim, nodeIdx, "translation", track.Translations, _scaleFactor);
                    }

                    if (track.Rotations != null && track.Rotations.Count > 0 && usedTrsTargets.Add((nodeIdx, "rotation")))
                    {
                        AddRotationChannel(gltfAnim, nodeIdx, track.Rotations);
                    }

                    if (track.Scalings != null && track.Scalings.Count > 0 && usedTrsTargets.Add((nodeIdx, "scale")))
                    {
                        AddVec3Channel(gltfAnim, nodeIdx, "scale", track.Scalings, 1f);
                    }
                }

                if (_exportBlendShape && track.BlendShapes != null && track.BlendShapes.Count > 0)
                {
                    var normTrackPath = NormalizeFramePath(track.Path);
                    for (int b = 0; b < track.BlendShapes.Count; b++)
                    {
                        var bs = track.BlendShapes[b];
                        if (bs == null || bs.Keyframes == null || bs.Keyframes.Count == 0)
                        {
                            continue;
                        }

                        if (TryResolveMorphChannel(normTrackPath, bs.ChannelName, out int meshNodeIdx, out int targetIdx, out float fullWeight))
                        {
                            if (!morphCurvesByMeshNode.TryGetValue(meshNodeIdx, out var channelMap))
                            {
                                channelMap = new Dictionary<int, (List<ImportedKeyframe<float>>, float)>();
                                morphCurvesByMeshNode[meshNodeIdx] = channelMap;
                            }
                            channelMap[targetIdx] = (bs.Keyframes, fullWeight);
                        }
                    }
                }
            }

            foreach (var kv in morphCurvesByMeshNode)
            {
                int meshNodeIdx = kv.Key;
                if (!_morphTargetsByNode.TryGetValue(meshNodeIdx, out var morphInfo) || morphInfo.TargetCount <= 0)
                {
                    continue;
                }

                AddMorphWeightsChannel(gltfAnim, meshNodeIdx, morphInfo.TargetCount, kv.Value);
            }

            if (gltfAnim.Channels.Count > 0)
            {
                _animations.Add(gltfAnim);
            }
        }

        private void AddVec3Channel(
            GltfAnimation gltfAnim,
            int nodeIdx,
            string path,
            List<ImportedKeyframe<Vector3>> rawKeyframes,
            float valueScale)
        {
            var prepared = PrepareStrictMonotonicKeyframes(rawKeyframes);
            if (prepared.Count == 0)
            {
                return;
            }

            bool allStepped = true;
            var times = new float[prepared.Count];
            var values = new System.Numerics.Vector3[prepared.Count];

            for (int i = 0; i < prepared.Count; i++)
            {
                var kf = prepared[i];
                if (!kf.isStepped) allStepped = false;
                times[i] = kf.time;
                if (path == "scale")
                {
                    values[i] = new System.Numerics.Vector3(
                        SanitizeScaleFloat(kf.value.X),
                        SanitizeScaleFloat(kf.value.Y),
                        SanitizeScaleFloat(kf.value.Z));
                }
                else
                {
                    values[i] = new System.Numerics.Vector3(
                        SanitizeFloat(kf.value.X * valueScale),
                        SanitizeFloat(kf.value.Y * valueScale),
                        SanitizeFloat(kf.value.Z * valueScale));
                }
            }

            int inputAcc = _buffer.AddScalarFloatAccessor(times, includeMinMax: true, target: null);
            int outputAcc = _buffer.AddVec3Accessor(values, includeMinMax: false, target: null);

            int samplerIdx = gltfAnim.Samplers.Count;
            gltfAnim.Samplers.Add(new GltfAnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = allStepped ? "STEP" : "LINEAR"
            });
            gltfAnim.Channels.Add(new GltfAnimationChannel
            {
                Sampler = samplerIdx,
                TargetNode = nodeIdx,
                TargetPath = path
            });
        }

        private void AddRotationChannel(
            GltfAnimation gltfAnim,
            int nodeIdx,
            List<ImportedKeyframe<Vector3>> rawKeyframes)
        {
            var prepared = PrepareStrictMonotonicKeyframes(rawKeyframes);
            if (prepared.Count == 0)
            {
                return;
            }

            bool allStepped = true;
            var times = new float[prepared.Count];
            var values = new System.Numerics.Vector4[prepared.Count];
            System.Numerics.Quaternion prevQ = System.Numerics.Quaternion.Identity;

            for (int i = 0; i < prepared.Count; i++)
            {
                var kf = prepared[i];
                if (!kf.isStepped) allStepped = false;
                times[i] = kf.time;

                var q = EulerDegreesToQuaternion(kf.value);
                if (i > 0 && System.Numerics.Quaternion.Dot(prevQ, q) < 0f)
                {
                    // Ensure shortest-arc slerp continuity across keyframes.
                    q = new System.Numerics.Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                }
                prevQ = q;
                values[i] = new System.Numerics.Vector4(q.X, q.Y, q.Z, q.W);
            }

            int inputAcc = _buffer.AddScalarFloatAccessor(times, includeMinMax: true, target: null);
            int outputAcc = _buffer.AddVec4Accessor(values, target: null);

            int samplerIdx = gltfAnim.Samplers.Count;
            gltfAnim.Samplers.Add(new GltfAnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = allStepped ? "STEP" : "LINEAR"
            });
            gltfAnim.Channels.Add(new GltfAnimationChannel
            {
                Sampler = samplerIdx,
                TargetNode = nodeIdx,
                TargetPath = "rotation"
            });
        }

        private void AddMorphWeightsChannel(
            GltfAnimation gltfAnim,
            int meshNodeIdx,
            int targetCount,
            Dictionary<int, (List<ImportedKeyframe<float>> Keys, float FullWeight)> animatedChannels)
        {
            var sortedCurves = new Dictionary<int, (List<ImportedKeyframe<float>> Keys, float FullWeight)>(animatedChannels.Count);
            var rawTimes = new List<float>();

            foreach (var kv in animatedChannels)
            {
                var cleaned = PrepareStrictMonotonicKeyframes(kv.Value.Keys);
                if (cleaned.Count == 0)
                {
                    continue;
                }
                sortedCurves[kv.Key] = (cleaned, kv.Value.FullWeight);
                for (int i = 0; i < cleaned.Count; i++)
                {
                    rawTimes.Add(cleaned[i].time);
                }
            }

            if (rawTimes.Count == 0)
            {
                return;
            }

            rawTimes.Sort();
            var mergedTimes = new List<float>(rawTimes.Count);
            for (int i = 0; i < rawTimes.Count; i++)
            {
                float t = rawTimes[i];
                if (mergedTimes.Count == 0 || t > mergedTimes[mergedTimes.Count - 1] + 1e-6f)
                {
                    mergedTimes.Add(t);
                }
            }

            if (mergedTimes.Count == 0)
            {
                return;
            }

            var timeArray = mergedTimes.ToArray();
            var weightOutput = new float[checked(timeArray.Length * targetCount)];

            for (int k = 0; k < timeArray.Length; k++)
            {
                float t = timeArray[k];
                int rowOffset = k * targetCount;

                foreach (var kv in sortedCurves)
                {
                    int targetIdx = kv.Key;
                    if ((uint)targetIdx >= (uint)targetCount)
                    {
                        continue;
                    }

                    var (keys, fullWeight) = kv.Value;
                    float rawVal = SampleFloatCurve(keys, t);
                    float normalized = fullWeight > 1e-4f ? rawVal / fullWeight : rawVal;
                    weightOutput[rowOffset + targetIdx] = SanitizeFloat(normalized);
                }
            }

            int inputAcc = _buffer.AddScalarFloatAccessor(timeArray, includeMinMax: true, target: null);
            int outputAcc = _buffer.AddScalarFloatAccessor(weightOutput, includeMinMax: false, target: null);

            int samplerIdx = gltfAnim.Samplers.Count;
            gltfAnim.Samplers.Add(new GltfAnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = "LINEAR"
            });
            gltfAnim.Channels.Add(new GltfAnimationChannel
            {
                Sampler = samplerIdx,
                TargetNode = meshNodeIdx,
                TargetPath = "weights"
            });
        }

        private bool TryResolveMorphChannel(
            string normTrackPath,
            string? rawChannelName,
            out int meshNodeIdx,
            out int targetIndex,
            out float fullWeight)
        {
            meshNodeIdx = -1;
            targetIndex = -1;
            fullWeight = 100f;

            if (string.IsNullOrEmpty(rawChannelName))
            {
                return false;
            }

            const string bsPrefix = "blendShape.";
            string cleanName = rawChannelName.StartsWith(bsPrefix, StringComparison.OrdinalIgnoreCase)
                ? rawChannelName.Substring(bsPrefix.Length)
                : rawChannelName;

            // 1. Direct normalized mesh path lookup
            if (!string.IsNullOrEmpty(normTrackPath) && _morphTargetsByPath.TryGetValue(normTrackPath, out var pathInfo))
            {
                if (pathInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                {
                    meshNodeIdx = pathInfo.MeshNodeIndex;
                    return true;
                }
            }

            // 2. Suffix path lookup -> canonical path
            if (!string.IsNullOrEmpty(normTrackPath)
                && _uniqueSuffixToPath.TryGetValue(normTrackPath, out var canonical)
                && canonical != null
                && _morphTargetsByPath.TryGetValue(canonical, out var canonicalInfo))
            {
                if (canonicalInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                {
                    meshNodeIdx = canonicalInfo.MeshNodeIndex;
                    return true;
                }
            }

            // 3. Resolved node or its dedicated child mesh node
            if (TryResolveNodeIndex(normTrackPath, out int resolvedNode))
            {
                if (_morphTargetsByNode.TryGetValue(resolvedNode, out var nodeInfo)
                    && nodeInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                {
                    meshNodeIdx = nodeInfo.MeshNodeIndex;
                    return true;
                }

                var children = _nodes[resolvedNode].Children;
                for (int c = 0; c < children.Count; c++)
                {
                    if (_morphTargetsByNode.TryGetValue(children[c], out var childInfo)
                        && childInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                    {
                        meshNodeIdx = childInfo.MeshNodeIndex;
                        return true;
                    }
                }
            }

            // 4. Unique channel-name fallback across exported morph meshes
            if (_uniqueMorphChannelLookup.TryGetValue(cleanName, out var uniqueMatch) && uniqueMatch.HasValue)
            {
                meshNodeIdx = uniqueMatch.Value.MeshNodeIndex;
                targetIndex = uniqueMatch.Value.TargetIndex;
                fullWeight = uniqueMatch.Value.FullWeight;
                return true;
            }

            return false;
        }

        private static List<ImportedKeyframe<T>> PrepareStrictMonotonicKeyframes<T>(List<ImportedKeyframe<T>> raw)
        {
            var result = new List<ImportedKeyframe<T>>(raw.Count);
            if (raw.Count == 0)
            {
                return result;
            }

            // Sanitize times (non-negative finite) and sort stably by time
            var sanitized = new List<(float Time, int OriginalOrder, ImportedKeyframe<T> Key)>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var k = raw[i];
                if (k == null) continue;
                float t = SanitizeFloat(k.time);
                if (t < 0f) t = 0f;
                sanitized.Add((t, i, k));
            }

            sanitized.Sort(static (a, b) =>
            {
                int cmp = a.Time.CompareTo(b.Time);
                return cmp != 0 ? cmp : a.OriginalOrder.CompareTo(b.OriginalOrder);
            });

            // Deduplicate equal or sub-epsilon timestamps (keeping the latest value at that timestamp)
            for (int i = 0; i < sanitized.Count; i++)
            {
                var item = sanitized[i];
                if (result.Count > 0 && item.Time <= result[result.Count - 1].time + 1e-6f)
                {
                    result[result.Count - 1] = new ImportedKeyframe<T>(result[result.Count - 1].time, item.Key.value, item.Key.isStepped);
                }
                else
                {
                    result.Add(new ImportedKeyframe<T>(item.Time, item.Key.value, item.Key.isStepped));
                }
            }

            return result;
        }

        private static float SampleFloatCurve(List<ImportedKeyframe<float>> keys, float time)
        {
            if (keys.Count == 0) return 0f;
            if (keys.Count == 1 || time <= keys[0].time) return SanitizeFloat(keys[0].value);
            if (time >= keys[keys.Count - 1].time) return SanitizeFloat(keys[keys.Count - 1].value);

            int lo = 0;
            int hi = keys.Count - 1;
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].time <= time) lo = mid;
                else hi = mid;
            }

            var k0 = keys[lo];
            var k1 = keys[hi];
            if (k0.isStepped || k1.time <= k0.time + 1e-7f)
            {
                return SanitizeFloat(k0.value);
            }

            float alpha = (time - k0.time) / (k1.time - k0.time);
            return SanitizeFloat(k0.value + (k1.value - k0.value) * alpha);
        }

        // ------------------------------------------------------------------
        // Output Writers (GLB v2 Binary & Separate glTF 2.0 + .bin)
        // ------------------------------------------------------------------

        private void WriteOutput(string exportPath)
        {
            byte[] binData = _buffer.ToArray();
            string? externalBinFileName = null;

            if (!_binary && binData.Length > 0)
            {
                externalBinFileName = _baseFileName + ".bin";
                string binFullPath = string.IsNullOrEmpty(_exportDirectory)
                    ? externalBinFileName
                    : Path.Combine(_exportDirectory, externalBinFileName);
                File.WriteAllBytes(binFullPath, binData);
            }

            byte[] jsonBytes = BuildJsonBytes(binData.Length, externalBinFileName, indented: !_binary);

            if (_binary)
            {
                WriteGlbFile(exportPath, jsonBytes, binData);
            }
            else
            {
                File.WriteAllBytes(exportPath, jsonBytes);
            }
        }

        private static void WriteGlbFile(string exportPath, byte[] jsonBytes, byte[] binBytes)
        {
            int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
            int jsonChunkLength = jsonBytes.Length + jsonPadding;

            bool hasBin = binBytes.Length > 0;
            int binPadding = hasBin ? (4 - (binBytes.Length % 4)) % 4 : 0;
            int binChunkLength = binBytes.Length + binPadding;

            uint totalLength = checked((uint)(12 + 8 + jsonChunkLength + (hasBin ? 8 + binChunkLength : 0)));

            using var fs = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.None);
            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), GlbMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), GlbVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), totalLength);
            fs.Write(header);

            Span<byte> chunkHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(0, 4), (uint)jsonChunkLength);
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(4, 4), GlbChunkTypeJson);
            fs.Write(chunkHeader);
            fs.Write(jsonBytes);
            for (int i = 0; i < jsonPadding; i++)
            {
                fs.WriteByte(0x20); // ASCII space padding for JSON chunk
            }

            if (hasBin)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(0, 4), (uint)binChunkLength);
                BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(4, 4), GlbChunkTypeBin);
                fs.Write(chunkHeader);
                fs.Write(binBytes);
                for (int i = 0; i < binPadding; i++)
                {
                    fs.WriteByte(0x00); // Zero padding for BIN chunk
                }
            }
        }

        private byte[] BuildJsonBytes(int binByteLength, string? externalBinFileName, bool indented)
        {
            using var ms = new MemoryStream(4096);
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented }))
            {
                w.WriteStartObject();

                // asset (required)
                w.WriteStartObject("asset");
                w.WriteString("version", "2.0");
                w.WriteString("generator", "AssetStudio Native glTF 2.0 Exporter");
                w.WriteEndObject();

                // extensionsUsed
                if (_extensionsUsed.Count > 0)
                {
                    w.WriteStartArray("extensionsUsed");
                    foreach (var ext in _extensionsUsed.OrderBy(static x => x, StringComparer.Ordinal))
                    {
                        w.WriteStringValue(ext);
                    }
                    w.WriteEndArray();
                }

                // scene & scenes
                w.WriteNumber("scene", 0);
                w.WriteStartArray("scenes");
                w.WriteStartObject();
                w.WriteString("name", _rootFrame?.Name is { Length: > 0 } rn ? rn : _baseFileName);
                if (_sceneRootNodes.Count > 0)
                {
                    w.WriteStartArray("nodes");
                    for (int i = 0; i < _sceneRootNodes.Count; i++)
                    {
                        w.WriteNumberValue(_sceneRootNodes[i]);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteEndArray();

                // nodes
                if (_nodes.Count > 0)
                {
                    w.WriteStartArray("nodes");
                    for (int i = 0; i < _nodes.Count; i++)
                    {
                        var n = _nodes[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(n.Name))
                        {
                            w.WriteString("name", n.Name);
                        }
                        if (n.Mesh.HasValue)
                        {
                            w.WriteNumber("mesh", n.Mesh.Value);
                        }
                        if (n.Skin.HasValue)
                        {
                            w.WriteNumber("skin", n.Skin.Value);
                        }
                        if (n.Children.Count > 0)
                        {
                            w.WriteStartArray("children");
                            for (int c = 0; c < n.Children.Count; c++)
                            {
                                w.WriteNumberValue(n.Children[c]);
                            }
                            w.WriteEndArray();
                        }
                        if (n.Translation.HasValue)
                        {
                            var t = n.Translation.Value;
                            w.WriteStartArray("translation");
                            w.WriteNumberValue(SanitizeFloat(t.X));
                            w.WriteNumberValue(SanitizeFloat(t.Y));
                            w.WriteNumberValue(SanitizeFloat(t.Z));
                            w.WriteEndArray();
                        }
                        if (n.Rotation.HasValue)
                        {
                            var r = NormalizeQuaternionSafe(n.Rotation.Value);
                            w.WriteStartArray("rotation");
                            w.WriteNumberValue(SanitizeFloat(r.X));
                            w.WriteNumberValue(SanitizeFloat(r.Y));
                            w.WriteNumberValue(SanitizeFloat(r.Z));
                            w.WriteNumberValue(SanitizeFloat(r.W));
                            w.WriteEndArray();
                        }
                        if (n.Scale.HasValue)
                        {
                            var s = n.Scale.Value;
                            w.WriteStartArray("scale");
                            w.WriteNumberValue(SanitizeScaleFloat(s.X));
                            w.WriteNumberValue(SanitizeScaleFloat(s.Y));
                            w.WriteNumberValue(SanitizeScaleFloat(s.Z));
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // meshes
                if (_meshes.Count > 0)
                {
                    w.WriteStartArray("meshes");
                    for (int i = 0; i < _meshes.Count; i++)
                    {
                        var m = _meshes[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(m.Name))
                        {
                            w.WriteString("name", m.Name);
                        }
                        w.WriteStartArray("primitives");
                        for (int p = 0; p < m.Primitives.Count; p++)
                        {
                            var prim = m.Primitives[p];
                            w.WriteStartObject();

                            w.WriteStartObject("attributes");
                            foreach (var attr in prim.Attributes)
                            {
                                w.WriteNumber(attr.Key, attr.Value);
                            }
                            w.WriteEndObject();

                            w.WriteNumber("indices", prim.Indices);
                            if (prim.Material.HasValue)
                            {
                                w.WriteNumber("material", prim.Material.Value);
                            }
                            if (prim.Targets != null && prim.Targets.Count > 0)
                            {
                                w.WriteStartArray("targets");
                                for (int t = 0; t < prim.Targets.Count; t++)
                                {
                                    w.WriteStartObject();
                                    foreach (var kv in prim.Targets[t])
                                    {
                                        w.WriteNumber(kv.Key, kv.Value);
                                    }
                                    w.WriteEndObject();
                                }
                                w.WriteEndArray();
                            }

                            w.WriteEndObject();
                        }
                        w.WriteEndArray();

                        if (m.Weights != null && m.Weights.Length > 0)
                        {
                            w.WriteStartArray("weights");
                            for (int wi = 0; wi < m.Weights.Length; wi++)
                            {
                                w.WriteNumberValue(SanitizeFloat(m.Weights[wi]));
                            }
                            w.WriteEndArray();
                        }

                        if (m.TargetNames != null && m.TargetNames.Count > 0)
                        {
                            w.WriteStartObject("extras");
                            w.WriteStartArray("targetNames");
                            for (int ti = 0; ti < m.TargetNames.Count; ti++)
                            {
                                w.WriteStringValue(m.TargetNames[ti]);
                            }
                            w.WriteEndArray();
                            w.WriteEndObject();
                        }

                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // skins
                if (_skins.Count > 0)
                {
                    w.WriteStartArray("skins");
                    for (int i = 0; i < _skins.Count; i++)
                    {
                        var sk = _skins[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(sk.Name))
                        {
                            w.WriteString("name", sk.Name);
                        }
                        w.WriteNumber("inverseBindMatrices", sk.InverseBindMatrices);
                        if (sk.Skeleton.HasValue)
                        {
                            w.WriteNumber("skeleton", sk.Skeleton.Value);
                        }
                        w.WriteStartArray("joints");
                        for (int j = 0; j < sk.Joints.Count; j++)
                        {
                            w.WriteNumberValue(sk.Joints[j]);
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // materials
                if (_materials.Count > 0)
                {
                    w.WriteStartArray("materials");
                    for (int i = 0; i < _materials.Count; i++)
                    {
                        var mat = _materials[i];
                        w.WriteStartObject();
                        w.WriteString("name", mat.Name);

                        w.WriteStartObject("pbrMetallicRoughness");
                        w.WriteStartArray("baseColorFactor");
                        for (int c = 0; c < 4; c++)
                        {
                            w.WriteNumberValue(Clamp01(mat.BaseColorFactor[c]));
                        }
                        w.WriteEndArray();
                        if (mat.BaseColorTexture != null)
                        {
                            WriteTextureInfo(w, "baseColorTexture", mat.BaseColorTexture);
                        }
                        w.WriteNumber("metallicFactor", Clamp01(mat.MetallicFactor));
                        w.WriteNumber("roughnessFactor", Clamp01(mat.RoughnessFactor));
                        if (mat.MetallicRoughnessTexture != null)
                        {
                            WriteTextureInfo(w, "metallicRoughnessTexture", mat.MetallicRoughnessTexture);
                        }
                        w.WriteEndObject();

                        if (mat.NormalTexture != null)
                        {
                            WriteTextureInfo(w, "normalTexture", mat.NormalTexture, extraName: mat.NormalScale.HasValue ? "scale" : null, extraValue: mat.NormalScale);
                        }
                        if (mat.OcclusionTexture != null)
                        {
                            WriteTextureInfo(w, "occlusionTexture", mat.OcclusionTexture, extraName: mat.OcclusionStrength.HasValue ? "strength" : null, extraValue: mat.OcclusionStrength);
                        }
                        if (mat.EmissiveTexture != null)
                        {
                            WriteTextureInfo(w, "emissiveTexture", mat.EmissiveTexture);
                        }
                        if (mat.EmissiveFactor != null)
                        {
                            w.WriteStartArray("emissiveFactor");
                            for (int c = 0; c < 3; c++)
                            {
                                w.WriteNumberValue(Clamp01(mat.EmissiveFactor[c]));
                            }
                            w.WriteEndArray();
                        }
                        if (!string.IsNullOrEmpty(mat.AlphaMode) && !string.Equals(mat.AlphaMode, "OPAQUE", StringComparison.Ordinal))
                        {
                            w.WriteString("alphaMode", mat.AlphaMode);
                            if (string.Equals(mat.AlphaMode, "MASK", StringComparison.Ordinal) && mat.AlphaCutoff.HasValue)
                            {
                                w.WriteNumber("alphaCutoff", Clamp01(mat.AlphaCutoff.Value));
                            }
                        }
                        if (mat.DoubleSided)
                        {
                            w.WriteBoolean("doubleSided", true);
                        }

                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // textures, images, samplers
                if (_textures.Count > 0)
                {
                    w.WriteStartArray("textures");
                    for (int i = 0; i < _textures.Count; i++)
                    {
                        var t = _textures[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(t.Name))
                        {
                            w.WriteString("name", t.Name);
                        }
                        w.WriteNumber("sampler", t.Sampler);
                        w.WriteNumber("source", t.Source);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    w.WriteStartArray("samplers");
                    w.WriteStartObject();
                    w.WriteNumber("magFilter", MagFilterLinear);
                    w.WriteNumber("minFilter", MinFilterLinearMipmapLinear);
                    w.WriteNumber("wrapS", WrapRepeat);
                    w.WriteNumber("wrapT", WrapRepeat);
                    w.WriteEndObject();
                    w.WriteEndArray();
                }

                if (_images.Count > 0)
                {
                    w.WriteStartArray("images");
                    for (int i = 0; i < _images.Count; i++)
                    {
                        var img = _images[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(img.Name))
                        {
                            w.WriteString("name", img.Name);
                        }
                        if (img.BufferView.HasValue)
                        {
                            w.WriteNumber("bufferView", img.BufferView.Value);
                            w.WriteString("mimeType", img.MimeType);
                        }
                        else if (!string.IsNullOrEmpty(img.Uri))
                        {
                            w.WriteString("uri", img.Uri);
                            if (!string.IsNullOrEmpty(img.MimeType))
                            {
                                w.WriteString("mimeType", img.MimeType);
                            }
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // animations
                if (_animations.Count > 0)
                {
                    w.WriteStartArray("animations");
                    for (int i = 0; i < _animations.Count; i++)
                    {
                        var a = _animations[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(a.Name))
                        {
                            w.WriteString("name", a.Name);
                        }

                        w.WriteStartArray("channels");
                        for (int c = 0; c < a.Channels.Count; c++)
                        {
                            var ch = a.Channels[c];
                            w.WriteStartObject();
                            w.WriteNumber("sampler", ch.Sampler);
                            w.WriteStartObject("target");
                            w.WriteNumber("node", ch.TargetNode);
                            w.WriteString("path", ch.TargetPath);
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();

                        w.WriteStartArray("samplers");
                        for (int s = 0; s < a.Samplers.Count; s++)
                        {
                            var smp = a.Samplers[s];
                            w.WriteStartObject();
                            w.WriteNumber("input", smp.Input);
                            w.WriteString("interpolation", smp.Interpolation);
                            w.WriteNumber("output", smp.Output);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();

                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // accessors
                if (_buffer.Accessors.Count > 0)
                {
                    w.WriteStartArray("accessors");
                    for (int i = 0; i < _buffer.Accessors.Count; i++)
                    {
                        var acc = _buffer.Accessors[i];
                        w.WriteStartObject();
                        w.WriteNumber("bufferView", acc.BufferView);
                        w.WriteNumber("componentType", acc.ComponentType);
                        w.WriteNumber("count", acc.Count);
                        w.WriteString("type", acc.Type);
                        if (acc.Min != null)
                        {
                            w.WriteStartArray("min");
                            for (int k = 0; k < acc.Min.Length; k++)
                            {
                                w.WriteNumberValue(SanitizeFloat(acc.Min[k]));
                            }
                            w.WriteEndArray();
                        }
                        if (acc.Max != null)
                        {
                            w.WriteStartArray("max");
                            for (int k = 0; k < acc.Max.Length; k++)
                            {
                                w.WriteNumberValue(SanitizeFloat(acc.Max[k]));
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // bufferViews
                if (_buffer.BufferViews.Count > 0)
                {
                    w.WriteStartArray("bufferViews");
                    for (int i = 0; i < _buffer.BufferViews.Count; i++)
                    {
                        var bv = _buffer.BufferViews[i];
                        w.WriteStartObject();
                        w.WriteNumber("buffer", 0);
                        w.WriteNumber("byteOffset", bv.ByteOffset);
                        w.WriteNumber("byteLength", bv.ByteLength);
                        if (bv.Target.HasValue)
                        {
                            w.WriteNumber("target", bv.Target.Value);
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // buffers
                if (binByteLength > 0)
                {
                    w.WriteStartArray("buffers");
                    w.WriteStartObject();
                    if (!string.IsNullOrEmpty(externalBinFileName))
                    {
                        w.WriteString("uri", Uri.EscapeDataString(externalBinFileName));
                    }
                    w.WriteNumber("byteLength", binByteLength);
                    w.WriteEndObject();
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            return ms.ToArray();
        }

        private static void WriteTextureInfo(
            Utf8JsonWriter w,
            string propertyName,
            GltfTextureInfo info,
            string? extraName = null,
            float? extraValue = null)
        {
            w.WriteStartObject(propertyName);
            w.WriteNumber("index", info.Index);
            if (!string.IsNullOrEmpty(extraName) && extraValue.HasValue)
            {
                w.WriteNumber(extraName, SanitizeFloat(extraValue.Value));
            }
            if (info.Offset != null && info.Scale != null)
            {
                w.WriteStartObject("extensions");
                w.WriteStartObject("KHR_texture_transform");
                w.WriteStartArray("offset");
                w.WriteNumberValue(SanitizeFloat(info.Offset[0]));
                w.WriteNumberValue(SanitizeFloat(info.Offset[1]));
                w.WriteEndArray();
                w.WriteStartArray("scale");
                w.WriteNumberValue(SanitizeFloat(info.Scale[0]));
                w.WriteNumberValue(SanitizeFloat(info.Scale[1]));
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
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

        // ------------------------------------------------------------------
        // Internal Model Types & Binary Buffer Builder
        // ------------------------------------------------------------------

        private sealed class GltfNode
        {
            public string Name = string.Empty;
            public int? Mesh;
            public int? Skin;
            public System.Numerics.Vector3? Translation;
            public System.Numerics.Quaternion? Rotation;
            public System.Numerics.Vector3? Scale;
            public List<int> Children = new List<int>();
        }

        private sealed class GltfMesh
        {
            public string Name = string.Empty;
            public List<GltfPrimitive> Primitives = new List<GltfPrimitive>();
            public float[]? Weights;
            public List<string>? TargetNames;
        }

        private sealed class GltfPrimitive
        {
            public Dictionary<string, int> Attributes = new Dictionary<string, int>(StringComparer.Ordinal);
            public int Indices;
            public int? Material;
            public List<Dictionary<string, int>>? Targets;
        }

        private readonly struct SkinSkeletonGroup
        {
            public int SkeletonNode { get; }
            public int ArmatureNode { get; }

            public SkinSkeletonGroup(int skeletonNode, int armatureNode)
            {
                SkeletonNode = skeletonNode;
                ArmatureNode = armatureNode;
            }
        }

        private sealed class GltfSkin
        {
            public string Name = string.Empty;
            public int InverseBindMatrices;
            public int? Skeleton;
            public List<int> Joints = new List<int>();
        }

        private sealed class GltfMaterial
        {
            public string Name = string.Empty;
            public float[] BaseColorFactor = { 0.8f, 0.8f, 0.8f, 1f };
            public GltfTextureInfo? BaseColorTexture;
            public float MetallicFactor;
            public float RoughnessFactor = 0.6f;
            public GltfTextureInfo? MetallicRoughnessTexture;
            public GltfTextureInfo? NormalTexture;
            public float? NormalScale;
            public GltfTextureInfo? OcclusionTexture;
            public float? OcclusionStrength;
            public GltfTextureInfo? EmissiveTexture;
            public float[]? EmissiveFactor;
            public string? AlphaMode;
            public float? AlphaCutoff;
            public bool DoubleSided;
        }

        private sealed class GltfTextureInfo
        {
            public int Index;
            public float[]? Offset;
            public float[]? Scale;
        }

        private sealed class GltfTexture
        {
            public string Name = string.Empty;
            public int Sampler;
            public int Source;
        }

        private sealed class GltfImage
        {
            public string Name = string.Empty;
            public string MimeType = string.Empty;
            public int? BufferView;
            public string? Uri;
        }

        private sealed class GltfAnimation
        {
            public string Name = string.Empty;
            public List<GltfAnimationChannel> Channels = new List<GltfAnimationChannel>();
            public List<GltfAnimationSampler> Samplers = new List<GltfAnimationSampler>();
        }

        private sealed class GltfAnimationChannel
        {
            public int Sampler;
            public int TargetNode;
            public string TargetPath = string.Empty;
        }

        private sealed class GltfAnimationSampler
        {
            public int Input;
            public int Output;
            public string Interpolation = "LINEAR";
        }

        private sealed class GltfBufferView
        {
            public int ByteOffset;
            public int ByteLength;
            public int? Target;
        }

        private sealed class GltfAccessor
        {
            public int BufferView;
            public int ComponentType;
            public int Count;
            public string Type = string.Empty;
            public float[]? Min;
            public float[]? Max;
        }

        private sealed class MorphMeshTargetInfo
        {
            public int MeshNodeIndex { get; }
            public int TargetCount => _targetNames.Count;
            private readonly List<string> _targetNames;
            private readonly List<float> _fullWeights;
            private readonly Dictionary<string, int> _indexByName;

            public MorphMeshTargetInfo(int meshNodeIndex, List<string> targetNames, List<float> fullWeights)
            {
                MeshNodeIndex = meshNodeIndex;
                _targetNames = targetNames;
                _fullWeights = fullWeights;
                _indexByName = new Dictionary<string, int>(targetNames.Count, StringComparer.Ordinal);
                for (int i = 0; i < targetNames.Count; i++)
                {
                    if (!_indexByName.ContainsKey(targetNames[i]))
                    {
                        _indexByName[targetNames[i]] = i;
                    }
                }
            }

            public bool TryFindTarget(string cleanName, string rawName, out int targetIndex, out float fullWeight)
            {
                if (_indexByName.TryGetValue(cleanName, out targetIndex) || _indexByName.TryGetValue(rawName, out targetIndex))
                {
                    fullWeight = _fullWeights[targetIndex];
                    return true;
                }

                for (int i = 0; i < _targetNames.Count; i++)
                {
                    if (string.Equals(_targetNames[i], cleanName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIndex = i;
                        fullWeight = _fullWeights[i];
                        return true;
                    }
                }

                targetIndex = -1;
                fullWeight = 100f;
                return false;
            }
        }

        private sealed class BufferBuilder
        {
            private readonly MemoryStream _stream = new MemoryStream(65536);
            public List<GltfBufferView> BufferViews { get; } = new List<GltfBufferView>();
            public List<GltfAccessor> Accessors { get; } = new List<GltfAccessor>();

            public void Reset()
            {
                _stream.SetLength(0);
                BufferViews.Clear();
                Accessors.Clear();
            }

            public byte[] ToArray() => _stream.ToArray();

            private void AlignTo4()
            {
                int rem = (int)(_stream.Length % 4);
                if (rem != 0)
                {
                    int pad = 4 - rem;
                    for (int i = 0; i < pad; i++)
                    {
                        _stream.WriteByte(0);
                    }
                }
            }

            public int AddBufferView(ReadOnlySpan<byte> bytes, int? target)
            {
                AlignTo4();
                int offset = checked((int)_stream.Length);
                _stream.Write(bytes);

                int idx = BufferViews.Count;
                BufferViews.Add(new GltfBufferView
                {
                    ByteOffset = offset,
                    ByteLength = bytes.Length,
                    Target = target
                });
                return idx;
            }

            public int AddScalarFloatAccessor(ReadOnlySpan<float> values, bool includeMinMax, int? target)
            {
                var bytes = new byte[checked(values.Length * 4)];
                float minVal = float.PositiveInfinity;
                float maxVal = float.NegativeInfinity;

                for (int i = 0; i < values.Length; i++)
                {
                    float v = SanitizeFloat(values[i]);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), v);
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }

                int bv = AddBufferView(bytes, target);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = ComponentTypeFloat,
                    Count = values.Length,
                    Type = "SCALAR",
                    Min = includeMinMax && values.Length > 0 ? new[] { minVal } : null,
                    Max = includeMinMax && values.Length > 0 ? new[] { maxVal } : null
                });
                return accIdx;
            }

            public int AddVec2Accessor(ReadOnlySpan<System.Numerics.Vector2> values, int? target)
            {
                var bytes = new byte[checked(values.Length * 8)];
                for (int i = 0; i < values.Length; i++)
                {
                    int off = i * 8;
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off, 4), SanitizeFloat(values[i].X));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 4, 4), SanitizeFloat(values[i].Y));
                }

                int bv = AddBufferView(bytes, target);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = ComponentTypeFloat,
                    Count = values.Length,
                    Type = "VEC2"
                });
                return accIdx;
            }

            public int AddVec3Accessor(ReadOnlySpan<System.Numerics.Vector3> values, bool includeMinMax, int? target)
            {
                var bytes = new byte[checked(values.Length * 12)];
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;

                for (int i = 0; i < values.Length; i++)
                {
                    float x = SanitizeFloat(values[i].X);
                    float y = SanitizeFloat(values[i].Y);
                    float z = SanitizeFloat(values[i].Z);

                    int off = i * 12;
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off, 4), x);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 4, 4), y);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 8, 4), z);

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (z < minZ) minZ = z;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                    if (z > maxZ) maxZ = z;
                }

                int bv = AddBufferView(bytes, target);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = ComponentTypeFloat,
                    Count = values.Length,
                    Type = "VEC3",
                    Min = includeMinMax && values.Length > 0 ? new[] { minX, minY, minZ } : null,
                    Max = includeMinMax && values.Length > 0 ? new[] { maxX, maxY, maxZ } : null
                });
                return accIdx;
            }

            public int AddVec4Accessor(ReadOnlySpan<System.Numerics.Vector4> values, int? target)
            {
                var bytes = new byte[checked(values.Length * 16)];
                for (int i = 0; i < values.Length; i++)
                {
                    int off = i * 16;
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off, 4), SanitizeFloat(values[i].X));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 4, 4), SanitizeFloat(values[i].Y));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 8, 4), SanitizeFloat(values[i].Z));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 12, 4), SanitizeFloat(values[i].W));
                }

                int bv = AddBufferView(bytes, target);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = ComponentTypeFloat,
                    Count = values.Length,
                    Type = "VEC4"
                });
                return accIdx;
            }

            public int AddUShort4Accessor(ReadOnlySpan<ushort> packedQuadValues, int? target)
            {
                int vertexCount = packedQuadValues.Length / 4;
                var bytes = new byte[checked(vertexCount * 8)];
                for (int i = 0; i < vertexCount * 4; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), packedQuadValues[i]);
                }

                int bv = AddBufferView(bytes, target);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = ComponentTypeUnsignedShort,
                    Count = vertexCount,
                    Type = "VEC4"
                });
                return accIdx;
            }

            public int AddIndicesAccessor(IReadOnlyList<int> indices, int maxIndex)
            {
                // glTF 2.0 spec 3.7.2.1: UNSIGNED_SHORT must NOT contain 65535 (primitive restart).
                bool useUInt16 = maxIndex < ushort.MaxValue;
                int componentType = useUInt16 ? ComponentTypeUnsignedShort : ComponentTypeUnsignedInt;
                byte[] bytes;

                if (useUInt16)
                {
                    bytes = new byte[checked(indices.Count * 2)];
                    for (int i = 0; i < indices.Count; i++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)indices[i]);
                    }
                }
                else
                {
                    bytes = new byte[checked(indices.Count * 4)];
                    for (int i = 0; i < indices.Count; i++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), (uint)indices[i]);
                    }
                }

                int bv = AddBufferView(bytes, TargetElementArrayBuffer);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = componentType,
                    Count = indices.Count,
                    Type = "SCALAR"
                });
                return accIdx;
            }

            public int AddMat4Accessor(ReadOnlySpan<System.Numerics.Matrix4x4> matrices)
            {
                var bytes = new byte[checked(matrices.Length * 64)];
                for (int i = 0; i < matrices.Length; i++)
                {
                    var m = matrices[i];
                    int off = i * 64;
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 0, 4), SanitizeFloat(m.M11));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 4, 4), SanitizeFloat(m.M12));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 8, 4), SanitizeFloat(m.M13));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 12, 4), SanitizeFloat(m.M14));

                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 16, 4), SanitizeFloat(m.M21));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 20, 4), SanitizeFloat(m.M22));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 24, 4), SanitizeFloat(m.M23));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 28, 4), SanitizeFloat(m.M24));

                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 32, 4), SanitizeFloat(m.M31));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 36, 4), SanitizeFloat(m.M32));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 40, 4), SanitizeFloat(m.M33));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 44, 4), SanitizeFloat(m.M34));

                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 48, 4), SanitizeFloat(m.M41));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 52, 4), SanitizeFloat(m.M42));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 56, 4), SanitizeFloat(m.M43));
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 60, 4), SanitizeFloat(m.M44, 1f));
                }

                int bv = AddBufferView(bytes, target: null);
                int accIdx = Accessors.Count;
                Accessors.Add(new GltfAccessor
                {
                    BufferView = bv,
                    ComponentType = ComponentTypeFloat,
                    Count = matrices.Length,
                    Type = "MAT4"
                });
                return accIdx;
            }
        }
    }
}
