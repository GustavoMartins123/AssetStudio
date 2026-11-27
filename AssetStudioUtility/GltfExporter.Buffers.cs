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
    public sealed partial class GltfExporter
    {
        // ------------------------------------------------------------------
        // Internal Model Types & Binary Buffer Builder
        // ------------------------------------------------------------------

        private void EnsureVertexColorAccessorsForGeometry(
            ImportedMesh mesh,
            int vertexCount,
            List<(List<int> Indices, int MaxIndex, int? MaterialIndex)> primitivePayloads,
            SharedGeometryEntry cachedGeometry)
        {
            bool needsOpaque = false;
            bool needsBlend = false;
            for (int p = 0; p < primitivePayloads.Count; p++)
            {
                var matIdx = primitivePayloads[p].MaterialIndex;
                bool isBlend = matIdx.HasValue
                    && matIdx.Value >= 0
                    && matIdx.Value < _materials.Count
                    && string.Equals(_materials[matIdx.Value].AlphaMode, "BLEND", StringComparison.Ordinal);
                if (isBlend) needsBlend = true;
                else needsOpaque = true;
            }

            if (needsOpaque && !cachedGeometry.OpaqueVertexColorAccessor.HasValue)
            {
                var opaqueColors = new System.Numerics.Vector4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var c = mesh.VertexList[i].Color;
                    opaqueColors[i] = new System.Numerics.Vector4(
                        Clamp01(SanitizeFloat(c.R, 1f)),
                        Clamp01(SanitizeFloat(c.G, 1f)),
                        Clamp01(SanitizeFloat(c.B, 1f)),
                        1f);
                }
                cachedGeometry.OpaqueVertexColorAccessor = _buffer.AddVec4Accessor(opaqueColors, target: TargetArrayBuffer);
            }

            if (needsBlend && !cachedGeometry.BlendVertexColorAccessor.HasValue)
            {
                var blendColors = new System.Numerics.Vector4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var c = mesh.VertexList[i].Color;
                    blendColors[i] = new System.Numerics.Vector4(
                        Clamp01(SanitizeFloat(c.R, 1f)),
                        Clamp01(SanitizeFloat(c.G, 1f)),
                        Clamp01(SanitizeFloat(c.B, 1f)),
                        Clamp01(SanitizeFloat(c.A, 1f)));
                }
                cachedGeometry.BlendVertexColorAccessor = _buffer.AddVec4Accessor(blendColors, target: TargetArrayBuffer);
            }
        }

        private string ComputeGeometryCacheKey(
            ImportedMesh mesh,
            List<(List<int> Indices, int MaxIndex, int? MaterialIndex)> primitivePayloads)
        {
            var exportColors = _exportVertexColors || ExportVertexColors;
            if (!string.IsNullOrWhiteSpace(mesh.GeometryKey))
            {
                var sb = new StringBuilder(mesh.GeometryKey.Length + 48);
                sb.Append("asset:");
                sb.Append(mesh.GeometryKey);
                sb.Append("|v:");
                sb.Append(mesh.VertexList.Count);
                sb.Append("|vc:");
                sb.Append(exportColors ? '1' : '0');
                sb.Append("|p:");
                for (int p = 0; p < primitivePayloads.Count; p++)
                {
                    if (p > 0) sb.Append(',');
                    sb.Append(primitivePayloads[p].Indices.Count);
                }
                return sb.ToString();
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            void AppendInt(int value)
            {
                Span<byte> buf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buf, value);
                hasher.AppendData(buf);
            }

            void AppendFloat(float value)
            {
                Span<byte> buf = stackalloc byte[4];
                float clean = SanitizeFloat(value);
                BinaryPrimitives.WriteInt32LittleEndian(buf, BitConverter.SingleToInt32Bits(clean));
                hasher.AppendData(buf);
            }

            AppendInt(mesh.VertexList.Count);
            AppendInt((mesh.hasNormal ? 1 : 0) | (mesh.hasTangent ? 2 : 0) | (mesh.hasColor && exportColors ? 4 : 0));
            AppendInt(mesh.hasUV?.Length ?? 0);
            if (mesh.hasUV != null)
            {
                for (int u = 0; u < mesh.hasUV.Length; u++)
                {
                    AppendInt(mesh.hasUV[u] ? 1 : 0);
                }
            }

            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                var v = mesh.VertexList[i];
                AppendFloat(v.Vertex.X);
                AppendFloat(v.Vertex.Y);
                AppendFloat(v.Vertex.Z);
                if (mesh.hasNormal)
                {
                    AppendFloat(v.Normal.X);
                    AppendFloat(v.Normal.Y);
                    AppendFloat(v.Normal.Z);
                }
                if (mesh.hasTangent)
                {
                    AppendFloat(v.Tangent.X);
                    AppendFloat(v.Tangent.Y);
                    AppendFloat(v.Tangent.Z);
                    AppendFloat(v.Tangent.W);
                }
                if (mesh.hasUV != null && v.UV != null)
                {
                    for (int u = 0; u < mesh.hasUV.Length; u++)
                    {
                        if (mesh.hasUV[u] && u < v.UV.Length && v.UV[u] != null && v.UV[u].Length > 0)
                        {
                            AppendFloat(v.UV[u][0]);
                            AppendFloat(v.UV[u].Length > 1 ? v.UV[u][1] : 0f);
                        }
                    }
                }
                if (exportColors && mesh.hasColor)
                {
                    AppendFloat(v.Color.R);
                    AppendFloat(v.Color.G);
                    AppendFloat(v.Color.B);
                    AppendFloat(v.Color.A);
                }
            }

            AppendInt(primitivePayloads.Count);
            for (int p = 0; p < primitivePayloads.Count; p++)
            {
                var idxList = primitivePayloads[p].Indices;
                AppendInt(idxList.Count);
                for (int k = 0; k < idxList.Count; k++)
                {
                    AppendInt(idxList[k]);
                }
            }

            return "hash:" + Convert.ToHexString(hasher.GetHashAndReset());
        }

        private static string BuildMeshInstanceCacheKey(
            string geometryKey,
            List<(List<int> Indices, int MaxIndex, int? MaterialIndex)> primitivePayloads)
        {
            var sb = new StringBuilder(geometryKey.Length + primitivePayloads.Count * 6 + 8);
            sb.Append(geometryKey);
            sb.Append("|m:");
            for (int p = 0; p < primitivePayloads.Count; p++)
            {
                if (p > 0) sb.Append(',');
                var matIdx = primitivePayloads[p].MaterialIndex;
                sb.Append(matIdx.HasValue
                    ? matIdx.Value.ToString(CultureInfo.InvariantCulture)
                    : "-");
            }
            return sb.ToString();
        }

        private sealed class SharedGeometryEntry
        {
            public required Dictionary<string, int> SharedAttributes { get; init; }
            public int? OpaqueVertexColorAccessor { get; set; }
            public int? BlendVertexColorAccessor { get; set; }
            public required List<int> PrimitiveIndicesAccessors { get; init; }
        }

        private sealed class GltfNode
        {
            public string Name = string.Empty;
            public int? Mesh;
            public int? Skin;
            public int? Camera;
            public int? Light;
            public System.Numerics.Vector3? Translation;
            public System.Numerics.Quaternion? Rotation;
            public System.Numerics.Vector3? Scale;
            public List<int> Children = new List<int>();
            public ImportedFrameExtras? Extras;
            public bool IsInitiallyHidden;
            public string? HiddenReason;
        }

        private sealed class GltfCamera
        {
            public string Name = string.Empty;
            public bool IsOrthographic;
            public float? AspectRatio;
            public float YFovRadians;
            public float XMag;
            public float YMag;
            public float ZNear;
            public float ZFar;
        }

        private sealed class GltfLight
        {
            public string Name = string.Empty;
            public string Type = "point";
            public float[] Color = { 1f, 1f, 1f };
            public float Intensity = 1f;
            public float? Range;
            public float InnerConeAngleRadians;
            public float OuterConeAngleRadians;
            public string? UnityOriginalType;
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
            public float? TransmissionFactor;
            public float? Ior;
            public bool IsUnlit;
            public ImportedUnityShaderMetadata? UnityShaderMetadata;
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
