using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AssetStudio
{
    public sealed class GltfValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public int NodeCount { get; internal set; }
        public int MeshCount { get; internal set; }
        public int MaterialCount { get; internal set; }
        public int TextureCount { get; internal set; }
        public int SkinCount { get; internal set; }
        public int AnimationCount { get; internal set; }
        public int AccessorCount { get; internal set; }
    }

    /// <summary>
    /// Strict, zero-dependency structural validator for glTF 2.0 (.gltf) and binary GLB v2 (.glb) files.
    /// Verifies container framing, chunk alignment, node hierarchy acyclicity, buffer/accessor bounds and
    /// min/max invariants, mesh/UV/skinning/morph/animation schemas, material alpha rules, and declared extensions.
    /// </summary>
    public static class Gltf2StructuralValidator
    {
        private const uint GlbMagic = 0x46546C67u;
        private const uint GlbVersion = 2u;
        private const uint GlbChunkTypeJson = 0x4E4F534Au;
        private const uint GlbChunkTypeBin = 0x004E4942u;

        private const int ComponentTypeByte = 5120;
        private const int ComponentTypeUnsignedByte = 5121;
        private const int ComponentTypeShort = 5122;
        private const int ComponentTypeUnsignedShort = 5123;
        private const int ComponentTypeUnsignedInt = 5125;
        private const int ComponentTypeFloat = 5126;

        public static GltfValidationResult ValidateGlbFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var missing = new GltfValidationResult();
                missing.Errors.Add($"GLB file does not exist: '{path}'.");
                return missing;
            }

            return ValidateGlb(File.ReadAllBytes(path));
        }

        public static GltfValidationResult ValidateGlb(ReadOnlySpan<byte> glbBytes)
        {
            var result = new GltfValidationResult();
            if (glbBytes.Length < 12)
            {
                result.Errors.Add($"GLB file is too short ({glbBytes.Length} bytes; minimum header is 12 bytes).");
                return result;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(0, 4));
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(4, 4));
            uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(8, 4));

            if (magic != GlbMagic)
            {
                result.Errors.Add($"Invalid GLB magic 0x{magic:X8} (expected 0x{GlbMagic:X8}).");
                return result;
            }
            if (version != GlbVersion)
            {
                result.Errors.Add($"Unsupported GLB version {version} (expected 2).");
                return result;
            }
            if (declaredLength != (uint)glbBytes.Length)
            {
                result.Errors.Add($"GLB header length {declaredLength} does not match actual file length {glbBytes.Length}.");
            }
            if ((declaredLength & 3u) != 0u)
            {
                result.Errors.Add($"GLB total length {declaredLength} is not 4-byte aligned.");
            }

            int pos = 12;
            if (pos + 8 > glbBytes.Length)
            {
                result.Errors.Add("GLB is missing its mandatory JSON chunk header.");
                return result;
            }

            uint jsonChunkLen = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos, 4));
            uint jsonChunkType = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos + 4, 4));
            pos += 8;

            if (jsonChunkType != GlbChunkTypeJson)
            {
                result.Errors.Add($"First GLB chunk must be JSON (0x{GlbChunkTypeJson:X8}), got 0x{jsonChunkType:X8}.");
                return result;
            }
            if ((jsonChunkLen & 3u) != 0u)
            {
                result.Errors.Add($"JSON chunk length {jsonChunkLen} is not 4-byte aligned.");
            }
            if (pos + jsonChunkLen > glbBytes.Length)
            {
                result.Errors.Add($"JSON chunk length {jsonChunkLen} exceeds GLB bounds.");
                return result;
            }

            var jsonBytes = glbBytes.Slice(pos, (int)jsonChunkLen).ToArray();
            pos += (int)jsonChunkLen;

            byte[]? binChunk = null;
            if (pos < glbBytes.Length)
            {
                if (pos + 8 > glbBytes.Length)
                {
                    result.Errors.Add("Truncated second GLB chunk header.");
                    return result;
                }

                uint binChunkLen = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos, 4));
                uint binChunkType = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos + 4, 4));
                pos += 8;

                if (binChunkType != GlbChunkTypeBin)
                {
                    result.Errors.Add($"Second GLB chunk must be BIN (0x{GlbChunkTypeBin:X8}), got 0x{binChunkType:X8}.");
                }
                if ((binChunkLen & 3u) != 0u)
                {
                    result.Errors.Add($"BIN chunk length {binChunkLen} is not 4-byte aligned.");
                }
                if (pos + binChunkLen > glbBytes.Length)
                {
                    result.Errors.Add($"BIN chunk length {binChunkLen} exceeds GLB bounds.");
                    return result;
                }

                binChunk = glbBytes.Slice(pos, (int)binChunkLen).ToArray();
                pos += (int)binChunkLen;
            }

            if (pos != glbBytes.Length)
            {
                result.Errors.Add($"Unexpected trailing bytes in GLB ({glbBytes.Length - pos} bytes after BIN chunk).");
            }

            ValidateJsonAndBuffers(jsonBytes, binChunk, baseDirectory: null, result);
            return result;
        }

        public static GltfValidationResult ValidateGltfFile(string path)
        {
            var result = new GltfValidationResult();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                result.Errors.Add($"glTF file does not exist: '{path}'.");
                return result;
            }

            var jsonBytes = File.ReadAllBytes(path);
            ValidateJsonAndBuffers(jsonBytes, glbBinChunk: null, baseDirectory: Path.GetDirectoryName(Path.GetFullPath(path)), result);
            return result;
        }

        public static bool TryExtractGlbParts(ReadOnlySpan<byte> glbBytes, out string jsonText, out byte[] binChunk)
        {
            jsonText = string.Empty;
            binChunk = Array.Empty<byte>();
            if (glbBytes.Length < 20) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(0, 4)) != GlbMagic) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(4, 4)) != GlbVersion) return false;

            int pos = 12;
            uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos, 4));
            uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos + 4, 4));
            pos += 8;
            if (jsonType != GlbChunkTypeJson || pos + jsonLen > glbBytes.Length) return false;

            jsonText = Encoding.UTF8.GetString(glbBytes.Slice(pos, (int)jsonLen)).TrimEnd(' ', '\0');
            pos += (int)jsonLen;

            if (pos + 8 <= glbBytes.Length)
            {
                uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos, 4));
                uint binType = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos + 4, 4));
                pos += 8;
                if (binType == GlbChunkTypeBin && pos + binLen <= glbBytes.Length)
                {
                    binChunk = glbBytes.Slice(pos, (int)binLen).ToArray();
                }
            }

            return true;
        }

        private static void ValidateJsonAndBuffers(byte[] jsonBytes, byte[]? glbBinChunk, string? baseDirectory, GltfValidationResult result)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(jsonBytes);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Invalid glTF JSON payload: {ex.Message}");
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    result.Errors.Add("glTF root must be a JSON object.");
                    return;
                }

                // 1. Asset version
                if (!root.TryGetProperty("asset", out var assetEl)
                    || !assetEl.TryGetProperty("version", out var versionEl)
                    || !string.Equals(versionEl.GetString(), "2.0", StringComparison.Ordinal))
                {
                    result.Errors.Add("glTF asset.version must be \"2.0\".");
                }

                // 2. Buffers
                var buffers = new List<byte[]>();
                if (root.TryGetProperty("buffers", out var buffersEl) && buffersEl.ValueKind == JsonValueKind.Array)
                {
                    int bIdx = 0;
                    foreach (var buf in buffersEl.EnumerateArray())
                    {
                        int byteLength = buf.TryGetProperty("byteLength", out var bl) ? bl.GetInt32() : -1;
                        if (byteLength <= 0)
                        {
                            result.Errors.Add($"buffers[{bIdx}].byteLength must be > 0.");
                            buffers.Add(Array.Empty<byte>());
                        }
                        else if (buf.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
                        {
                            var uri = uriEl.GetString() ?? string.Empty;
                            if (baseDirectory != null)
                            {
                                var binPath = Path.Combine(baseDirectory, Uri.UnescapeDataString(uri));
                                if (!File.Exists(binPath))
                                {
                                    result.Errors.Add($"buffers[{bIdx}] external uri '{uri}' not found.");
                                    buffers.Add(Array.Empty<byte>());
                                }
                                else
                                {
                                    var data = File.ReadAllBytes(binPath);
                                    if (data.Length < byteLength)
                                    {
                                        result.Errors.Add($"buffers[{bIdx}] file length {data.Length} < declared byteLength {byteLength}.");
                                    }
                                    buffers.Add(data);
                                }
                            }
                            else
                            {
                                buffers.Add(new byte[byteLength]);
                            }
                        }
                        else if (bIdx == 0 && glbBinChunk != null)
                        {
                            if (glbBinChunk.Length < byteLength)
                            {
                                result.Errors.Add($"buffers[0].byteLength ({byteLength}) exceeds GLB BIN chunk length ({glbBinChunk.Length}).");
                            }
                            buffers.Add(glbBinChunk);
                        }
                        else
                        {
                            result.Errors.Add($"buffers[{bIdx}] has no uri and no GLB BIN chunk.");
                            buffers.Add(Array.Empty<byte>());
                        }

                        bIdx++;
                    }
                }

                // 3. BufferViews
                var bufferViews = new List<(int Buffer, int ByteOffset, int ByteLength, int ByteStride)>();
                if (root.TryGetProperty("bufferViews", out var bvsEl) && bvsEl.ValueKind == JsonValueKind.Array)
                {
                    int bvIdx = 0;
                    foreach (var bv in bvsEl.EnumerateArray())
                    {
                        int bufIdx = bv.TryGetProperty("buffer", out var bProp) ? bProp.GetInt32() : -1;
                        int byteOffset = bv.TryGetProperty("byteOffset", out var boProp) ? boProp.GetInt32() : 0;
                        int byteLength = bv.TryGetProperty("byteLength", out var blProp) ? blProp.GetInt32() : -1;
                        int byteStride = bv.TryGetProperty("byteStride", out var bsProp) ? bsProp.GetInt32() : 0;

                        if (bufIdx < 0 || bufIdx >= buffers.Count)
                        {
                            result.Errors.Add($"bufferViews[{bvIdx}].buffer ({bufIdx}) is out of bounds.");
                        }
                        else if (byteOffset < 0 || byteLength <= 0 || (long)byteOffset + byteLength > buffers[bufIdx].Length)
                        {
                            result.Errors.Add($"bufferViews[{bvIdx}] range [{byteOffset}..{byteOffset + byteLength}] exceeds buffer {bufIdx} length {buffers[bufIdx].Length}.");
                        }

                        bufferViews.Add((bufIdx, byteOffset, byteLength, byteStride));
                        bvIdx++;
                    }
                }

                // 4. Accessors
                var accessors = new List<AccessorInfo>();
                if (root.TryGetProperty("accessors", out var accsEl) && accsEl.ValueKind == JsonValueKind.Array)
                {
                    int accIdx = 0;
                    foreach (var acc in accsEl.EnumerateArray())
                    {
                        int bvIdx = acc.TryGetProperty("bufferView", out var bvProp) ? bvProp.GetInt32() : -1;
                        int byteOffset = acc.TryGetProperty("byteOffset", out var boProp) ? boProp.GetInt32() : 0;
                        int componentType = acc.TryGetProperty("componentType", out var ctProp) ? ctProp.GetInt32() : 0;
                        int count = acc.TryGetProperty("count", out var cProp) ? cProp.GetInt32() : -1;
                        string type = acc.TryGetProperty("type", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;

                        int compSize = GetComponentByteSize(componentType);
                        int compCount = GetTypeComponentCount(type);
                        if (compSize == 0)
                        {
                            result.Errors.Add($"accessors[{accIdx}] has invalid componentType {componentType}.");
                        }
                        if (compCount == 0)
                        {
                            result.Errors.Add($"accessors[{accIdx}] has invalid type '{type}'.");
                        }
                        if (count <= 0)
                        {
                            result.Errors.Add($"accessors[{accIdx}].count must be > 0 (got {count}).");
                        }

                        float[]? min = ReadFloatArray(acc, "min");
                        float[]? max = ReadFloatArray(acc, "max");
                        if (min != null && min.Length != compCount)
                        {
                            result.Errors.Add($"accessors[{accIdx}].min length {min.Length} does not match type '{type}' ({compCount}).");
                        }
                        if (max != null && max.Length != compCount)
                        {
                            result.Errors.Add($"accessors[{accIdx}].max length {max.Length} does not match type '{type}' ({compCount}).");
                        }

                        if (bvIdx >= 0 && bvIdx < bufferViews.Count && compSize > 0 && compCount > 0 && count > 0)
                        {
                            var bv = bufferViews[bvIdx];
                            int totalOffset = bv.ByteOffset + byteOffset;
                            if (totalOffset % compSize != 0)
                            {
                                result.Errors.Add($"accessors[{accIdx}] offset {totalOffset} is not aligned to component size {compSize}.");
                            }

                            int elemSize = compSize * compCount;
                            int stride = bv.ByteStride > 0 ? bv.ByteStride : elemSize;
                            long requiredBytes = byteOffset + (long)(count - 1) * stride + elemSize;
                            if (byteOffset < 0 || requiredBytes > bv.ByteLength)
                            {
                                result.Errors.Add($"accessors[{accIdx}] requires {requiredBytes} bytes, exceeding bufferView[{bvIdx}].byteLength ({bv.ByteLength}).");
                            }
                            else if (componentType == ComponentTypeFloat && bv.Buffer >= 0 && bv.Buffer < buffers.Count && buffers[bv.Buffer].Length >= bv.ByteOffset + requiredBytes)
                            {
                                ValidateFloatAccessorData(buffers[bv.Buffer], totalOffset, stride, count, compCount, min, max, accIdx, result);
                            }
                        }
                        else if (bvIdx < 0 || bvIdx >= bufferViews.Count)
                        {
                            result.Errors.Add($"accessors[{accIdx}].bufferView ({bvIdx}) is out of bounds.");
                        }

                        accessors.Add(new AccessorInfo(bvIdx, byteOffset, componentType, count, type, compCount, min, max));
                        accIdx++;
                    }
                }
                result.AccessorCount = accessors.Count;

                // 5. Declared extensions tracking
                var declaredExtensions = new HashSet<string>(StringComparer.Ordinal);
                if (root.TryGetProperty("extensionsUsed", out var extUsedEl) && extUsedEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ext in extUsedEl.EnumerateArray())
                    {
                        if (ext.GetString() is { Length: > 0 } s)
                        {
                            declaredExtensions.Add(s);
                        }
                    }
                }
                var observedExtensions = new HashSet<string>(StringComparer.Ordinal);

                // 6. Images, Samplers, Textures, Materials
                int imageCount = root.TryGetProperty("images", out var imgsEl) && imgsEl.ValueKind == JsonValueKind.Array ? imgsEl.GetArrayLength() : 0;
                int samplerCount = root.TryGetProperty("samplers", out var sampsEl) && sampsEl.ValueKind == JsonValueKind.Array ? sampsEl.GetArrayLength() : 0;
                if (imageCount > 0)
                {
                    int idx = 0;
                    foreach (var img in imgsEl.EnumerateArray())
                    {
                        if (img.TryGetProperty("bufferView", out var ibv))
                        {
                            int bvIdx = ibv.GetInt32();
                            if (bvIdx < 0 || bvIdx >= bufferViews.Count)
                            {
                                result.Errors.Add($"images[{idx}].bufferView ({bvIdx}) is out of bounds.");
                            }
                        }
                        idx++;
                    }
                }

                int textureCount = root.TryGetProperty("textures", out var texsEl) && texsEl.ValueKind == JsonValueKind.Array ? texsEl.GetArrayLength() : 0;
                result.TextureCount = textureCount;
                if (textureCount > 0)
                {
                    int idx = 0;
                    foreach (var tex in texsEl.EnumerateArray())
                    {
                        if (tex.TryGetProperty("source", out var src) && (src.GetInt32() < 0 || src.GetInt32() >= imageCount))
                        {
                            result.Errors.Add($"textures[{idx}].source is out of bounds.");
                        }
                        if (tex.TryGetProperty("sampler", out var smp) && (smp.GetInt32() < 0 || smp.GetInt32() >= samplerCount))
                        {
                            result.Errors.Add($"textures[{idx}].sampler is out of bounds.");
                        }
                        idx++;
                    }
                }

                int materialCount = root.TryGetProperty("materials", out var matsEl) && matsEl.ValueKind == JsonValueKind.Array ? matsEl.GetArrayLength() : 0;
                result.MaterialCount = materialCount;
                if (materialCount > 0)
                {
                    int idx = 0;
                    foreach (var mat in matsEl.EnumerateArray())
                    {
                        CollectExtensions(mat, observedExtensions);
                        if (mat.TryGetProperty("alphaMode", out var am))
                        {
                            var mode = am.GetString();
                            if (mode is not ("OPAQUE" or "MASK" or "BLEND"))
                            {
                                result.Errors.Add($"materials[{idx}].alphaMode '{mode}' is invalid.");
                            }
                            if (mat.TryGetProperty("alphaCutoff", out _) && mode != "MASK")
                            {
                                result.Errors.Add($"materials[{idx}] specifies alphaCutoff when alphaMode is '{mode}' (only valid for MASK).");
                            }
                        }
                        else if (mat.TryGetProperty("alphaCutoff", out _))
                        {
                            result.Errors.Add($"materials[{idx}] specifies alphaCutoff without alphaMode=\"MASK\".");
                        }

                        if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr))
                        {
                            ValidateTextureRef(pbr, "baseColorTexture", textureCount, $"materials[{idx}].pbrMetallicRoughness", observedExtensions, result);
                            ValidateTextureRef(pbr, "metallicRoughnessTexture", textureCount, $"materials[{idx}].pbrMetallicRoughness", observedExtensions, result);
                            if (pbr.TryGetProperty("baseColorFactor", out var bcf) && bcf.ValueKind == JsonValueKind.Array)
                            {
                                ValidateNormalizedRange(bcf, 4, $"materials[{idx}].pbrMetallicRoughness.baseColorFactor", result);
                            }
                        }
                        ValidateTextureRef(mat, "normalTexture", textureCount, $"materials[{idx}]", observedExtensions, result);
                        ValidateTextureRef(mat, "occlusionTexture", textureCount, $"materials[{idx}]", observedExtensions, result);
                        ValidateTextureRef(mat, "emissiveTexture", textureCount, $"materials[{idx}]", observedExtensions, result);
                        idx++;
                    }
                }

                // 7. Meshes & Primitives & Morph Targets
                var meshTargetCounts = new List<int>();
                int meshCount = root.TryGetProperty("meshes", out var meshesEl) && meshesEl.ValueKind == JsonValueKind.Array ? meshesEl.GetArrayLength() : 0;
                result.MeshCount = meshCount;
                if (meshCount > 0)
                {
                    int mIdx = 0;
                    foreach (var mesh in meshesEl.EnumerateArray())
                    {
                        int firstTargetCount = 0;
                        if (!mesh.TryGetProperty("primitives", out var primsEl) || primsEl.ValueKind != JsonValueKind.Array || primsEl.GetArrayLength() == 0)
                        {
                            result.Errors.Add($"meshes[{mIdx}] must contain at least one primitive.");
                        }
                        else
                        {
                            int pIdx = 0;
                            foreach (var prim in primsEl.EnumerateArray())
                            {
                                if (!prim.TryGetProperty("attributes", out var attrsEl) || attrsEl.ValueKind != JsonValueKind.Object)
                                {
                                    result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}] missing attributes object.");
                                    pIdx++;
                                    continue;
                                }

                                if (!attrsEl.TryGetProperty("POSITION", out var posAccEl))
                                {
                                    result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}] missing required POSITION attribute.");
                                    pIdx++;
                                    continue;
                                }

                                int posAccIdx = posAccEl.GetInt32();
                                int vertexCount = -1;
                                if (posAccIdx < 0 || posAccIdx >= accessors.Count)
                                {
                                    result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].POSITION accessor {posAccIdx} out of bounds.");
                                }
                                else
                                {
                                    var posAcc = accessors[posAccIdx];
                                    vertexCount = posAcc.Count;
                                    if (posAcc.Type != "VEC3" || posAcc.ComponentType != ComponentTypeFloat)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].POSITION must be VEC3 FLOAT.");
                                    }
                                    if (posAcc.Min == null || posAcc.Max == null)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].POSITION accessor must specify min and max.");
                                    }
                                }

                                foreach (var attr in attrsEl.EnumerateObject())
                                {
                                    int aIdx = attr.Value.GetInt32();
                                    if (aIdx < 0 || aIdx >= accessors.Count)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].attributes.{attr.Name} accessor {aIdx} out of bounds.");
                                        continue;
                                    }

                                    var aInfo = accessors[aIdx];
                                    if (vertexCount > 0 && aInfo.Count != vertexCount)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].attributes.{attr.Name} count {aInfo.Count} != POSITION count {vertexCount}.");
                                    }

                                    if (attr.Name.StartsWith("TEXCOORD_", StringComparison.Ordinal) && aInfo.Type != "VEC2")
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].attributes.{attr.Name} must be VEC2.");
                                    }
                                    else if (attr.Name == "JOINTS_0" && (aInfo.Type != "VEC4" || (aInfo.ComponentType != ComponentTypeUnsignedShort && aInfo.ComponentType != ComponentTypeUnsignedByte)))
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].attributes.JOINTS_0 must be VEC4 UNSIGNED_BYTE or UNSIGNED_SHORT.");
                                    }
                                    else if (attr.Name == "WEIGHTS_0" && aInfo.Type != "VEC4")
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].attributes.WEIGHTS_0 must be VEC4.");
                                    }
                                }

                                if (prim.TryGetProperty("indices", out var indEl))
                                {
                                    int indAccIdx = indEl.GetInt32();
                                    if (indAccIdx < 0 || indAccIdx >= accessors.Count)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].indices accessor {indAccIdx} out of bounds.");
                                    }
                                    else
                                    {
                                        var indAcc = accessors[indAccIdx];
                                        if (indAcc.Type != "SCALAR" || (indAcc.ComponentType != ComponentTypeUnsignedShort && indAcc.ComponentType != ComponentTypeUnsignedInt && indAcc.ComponentType != ComponentTypeUnsignedByte))
                                        {
                                            result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].indices must be SCALAR unsigned integer.");
                                        }
                                        else if (vertexCount > 0 && indAcc.BufferView >= 0 && indAcc.BufferView < bufferViews.Count)
                                        {
                                            var bv = bufferViews[indAcc.BufferView];
                                            if (bv.Buffer >= 0 && bv.Buffer < buffers.Count)
                                            {
                                                ValidateIndexAccessor(buffers[bv.Buffer], bv.ByteOffset + indAcc.ByteOffset, indAcc.ComponentType, indAcc.Count, vertexCount, mIdx, pIdx, result);
                                            }
                                        }
                                    }
                                }

                                if (prim.TryGetProperty("material", out var matEl))
                                {
                                    int matIdx = matEl.GetInt32();
                                    if (matIdx < 0 || matIdx >= materialCount)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].material ({matIdx}) out of bounds.");
                                    }
                                }

                                if (prim.TryGetProperty("targets", out var targetsEl) && targetsEl.ValueKind == JsonValueKind.Array)
                                {
                                    int tCount = targetsEl.GetArrayLength();
                                    if (pIdx == 0)
                                    {
                                        firstTargetCount = tCount;
                                    }
                                    else if (tCount != firstTargetCount)
                                    {
                                        result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].targets count ({tCount}) differs from primitive 0 ({firstTargetCount}).");
                                    }

                                    int tIdx = 0;
                                    foreach (var target in targetsEl.EnumerateArray())
                                    {
                                        if (target.TryGetProperty("POSITION", out var tp))
                                        {
                                            int tpIdx = tp.GetInt32();
                                            if (tpIdx < 0 || tpIdx >= accessors.Count)
                                            {
                                                result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].targets[{tIdx}].POSITION accessor out of bounds.");
                                            }
                                            else
                                            {
                                                var tpAcc = accessors[tpIdx];
                                                if (vertexCount > 0 && tpAcc.Count != vertexCount)
                                                {
                                                    result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].targets[{tIdx}].POSITION count {tpAcc.Count} != base vertex count {vertexCount}.");
                                                }
                                                if (tpAcc.Min == null || tpAcc.Max == null)
                                                {
                                                    result.Errors.Add($"meshes[{mIdx}].primitives[{pIdx}].targets[{tIdx}].POSITION must specify min and max.");
                                                }
                                            }
                                        }
                                        tIdx++;
                                    }
                                }

                                pIdx++;
                            }
                        }

                        if (mesh.TryGetProperty("weights", out var weightsEl) && weightsEl.ValueKind == JsonValueKind.Array)
                        {
                            if (weightsEl.GetArrayLength() != firstTargetCount)
                            {
                                result.Errors.Add($"meshes[{mIdx}].weights length ({weightsEl.GetArrayLength()}) != morph target count ({firstTargetCount}).");
                            }
                        }

                        meshTargetCounts.Add(firstTargetCount);
                        mIdx++;
                    }
                }

                // 8. Nodes & Hierarchy Acyclicity
                int nodeCount = root.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Array ? nodesEl.GetArrayLength() : 0;
                result.NodeCount = nodeCount;
                var nodeMeshIndices = new int?[nodeCount];
                var parentOf = new int[nodeCount];
                Array.Fill(parentOf, -1);
                var childrenOf = new List<int>[nodeCount];
                for (int i = 0; i < nodeCount; i++) childrenOf[i] = new List<int>();

                int skinCount = root.TryGetProperty("skins", out var skinsEl) && skinsEl.ValueKind == JsonValueKind.Array ? skinsEl.GetArrayLength() : 0;
                result.SkinCount = skinCount;
                int cameraCount = root.TryGetProperty("cameras", out var camsEl) && camsEl.ValueKind == JsonValueKind.Array ? camsEl.GetArrayLength() : 0;

                if (nodeCount > 0)
                {
                    int nIdx = 0;
                    foreach (var node in nodesEl.EnumerateArray())
                    {
                        CollectExtensions(node, observedExtensions);
                        if (node.TryGetProperty("mesh", out var nm))
                        {
                            int mi = nm.GetInt32();
                            nodeMeshIndices[nIdx] = mi;
                            if (mi < 0 || mi >= meshCount)
                            {
                                result.Errors.Add($"nodes[{nIdx}].mesh ({mi}) is out of bounds.");
                            }
                        }
                        if (node.TryGetProperty("skin", out var ns))
                        {
                            int si = ns.GetInt32();
                            if (si < 0 || si >= skinCount)
                            {
                                result.Errors.Add($"nodes[{nIdx}].skin ({si}) is out of bounds.");
                            }
                            if (!nodeMeshIndices[nIdx].HasValue)
                            {
                                result.Errors.Add($"nodes[{nIdx}] specifies skin without mesh.");
                            }
                        }
                        if (node.TryGetProperty("camera", out var nc))
                        {
                            int ci = nc.GetInt32();
                            if (ci < 0 || ci >= cameraCount)
                            {
                                result.Errors.Add($"nodes[{nIdx}].camera ({ci}) is out of bounds.");
                            }
                        }
                        if (node.TryGetProperty("rotation", out var nrot) && nrot.ValueKind == JsonValueKind.Array)
                        {
                            if (nrot.GetArrayLength() != 4)
                            {
                                result.Errors.Add($"nodes[{nIdx}].rotation must have 4 elements.");
                            }
                            else
                            {
                                var q = new System.Numerics.Quaternion(nrot[0].GetSingle(), nrot[1].GetSingle(), nrot[2].GetSingle(), nrot[3].GetSingle());
                                float len = q.Length();
                                if (!float.IsFinite(len) || MathF.Abs(len - 1f) > 1e-3f)
                                {
                                    result.Errors.Add($"nodes[{nIdx}].rotation is not a unit quaternion (length={len}).");
                                }
                            }
                        }
                        if (node.TryGetProperty("children", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in chEl.EnumerateArray())
                            {
                                int childIdx = c.GetInt32();
                                if (childIdx < 0 || childIdx >= nodeCount)
                                {
                                    result.Errors.Add($"nodes[{nIdx}].children contains out-of-bounds index {childIdx}.");
                                }
                                else if (parentOf[childIdx] >= 0)
                                {
                                    result.Errors.Add($"nodes[{childIdx}] has multiple parents ({parentOf[childIdx]} and {nIdx}).");
                                }
                                else
                                {
                                    parentOf[childIdx] = nIdx;
                                    childrenOf[nIdx].Add(childIdx);
                                }
                            }
                        }

                        nIdx++;
                    }

                    // Cycle detection
                    var visitState = new byte[nodeCount]; // 0=unvisited, 1=visiting, 2=done
                    for (int i = 0; i < nodeCount; i++)
                    {
                        if (visitState[i] == 0 && HasCycle(i, childrenOf, visitState))
                        {
                            result.Errors.Add($"Node hierarchy contains a cycle involving node {i}.");
                            break;
                        }
                    }
                }

                // 9. Scenes
                if (root.TryGetProperty("scenes", out var scenesEl) && scenesEl.ValueKind == JsonValueKind.Array)
                {
                    if (root.TryGetProperty("scene", out var defSceneEl))
                    {
                        int ds = defSceneEl.GetInt32();
                        if (ds < 0 || ds >= scenesEl.GetArrayLength())
                        {
                            result.Errors.Add($"scene index {ds} is out of bounds.");
                        }
                    }

                    int sIdx = 0;
                    foreach (var sc in scenesEl.EnumerateArray())
                    {
                        if (sc.TryGetProperty("nodes", out var snEl) && snEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sn in snEl.EnumerateArray())
                            {
                                int rootNode = sn.GetInt32();
                                if (rootNode < 0 || rootNode >= nodeCount)
                                {
                                    result.Errors.Add($"scenes[{sIdx}].nodes contains out-of-bounds index {rootNode}.");
                                }
                                else if (parentOf[rootNode] >= 0)
                                {
                                    result.Errors.Add($"scenes[{sIdx}] root node {rootNode} also has parent {parentOf[rootNode]}.");
                                }
                            }
                        }
                        sIdx++;
                    }
                }

                // 10. Skins
                if (skinCount > 0)
                {
                    int sIdx = 0;
                    foreach (var skin in skinsEl.EnumerateArray())
                    {
                        int jointsCount = 0;
                        if (!skin.TryGetProperty("joints", out var jointsEl) || jointsEl.ValueKind != JsonValueKind.Array || jointsEl.GetArrayLength() == 0)
                        {
                            result.Errors.Add($"skins[{sIdx}].joints must be a non-empty array.");
                        }
                        else
                        {
                            jointsCount = jointsEl.GetArrayLength();
                            foreach (var j in jointsEl.EnumerateArray())
                            {
                                int jIdx = j.GetInt32();
                                if (jIdx < 0 || jIdx >= nodeCount)
                                {
                                    result.Errors.Add($"skins[{sIdx}].joints contains out-of-bounds node {jIdx}.");
                                }
                            }
                        }

                        if (skin.TryGetProperty("skeleton", out var skEl))
                        {
                            int skIdx = skEl.GetInt32();
                            if (skIdx < 0 || skIdx >= nodeCount)
                            {
                                result.Errors.Add($"skins[{sIdx}].skeleton ({skIdx}) is out of bounds.");
                            }
                        }

                        if (skin.TryGetProperty("inverseBindMatrices", out var ibmEl))
                        {
                            int ibmIdx = ibmEl.GetInt32();
                            if (ibmIdx < 0 || ibmIdx >= accessors.Count)
                            {
                                result.Errors.Add($"skins[{sIdx}].inverseBindMatrices ({ibmIdx}) is out of bounds.");
                            }
                            else
                            {
                                var ibmAcc = accessors[ibmIdx];
                                if (ibmAcc.Type != "MAT4" || ibmAcc.ComponentType != ComponentTypeFloat)
                                {
                                    result.Errors.Add($"skins[{sIdx}].inverseBindMatrices must be MAT4 FLOAT.");
                                }
                                if (ibmAcc.Count != jointsCount)
                                {
                                    result.Errors.Add($"skins[{sIdx}].inverseBindMatrices count ({ibmAcc.Count}) != joints count ({jointsCount}).");
                                }
                            }
                        }
                        sIdx++;
                    }
                }

                // 11. Animations
                int animCount = root.TryGetProperty("animations", out var animsEl) && animsEl.ValueKind == JsonValueKind.Array ? animsEl.GetArrayLength() : 0;
                result.AnimationCount = animCount;
                if (animCount > 0)
                {
                    int aIdx = 0;
                    foreach (var anim in animsEl.EnumerateArray())
                    {
                        var samplers = new List<(int Input, int Output)>();
                        if (anim.TryGetProperty("samplers", out var smpEl) && smpEl.ValueKind == JsonValueKind.Array)
                        {
                            int smpIdx = 0;
                            foreach (var smp in smpEl.EnumerateArray())
                            {
                                int inputIdx = smp.TryGetProperty("input", out var ip) ? ip.GetInt32() : -1;
                                int outputIdx = smp.TryGetProperty("output", out var op) ? op.GetInt32() : -1;
                                if (inputIdx < 0 || inputIdx >= accessors.Count)
                                {
                                    result.Errors.Add($"animations[{aIdx}].samplers[{smpIdx}].input out of bounds.");
                                }
                                else
                                {
                                    var inAcc = accessors[inputIdx];
                                    if (inAcc.Type != "SCALAR" || inAcc.ComponentType != ComponentTypeFloat || inAcc.Min == null || inAcc.Max == null)
                                    {
                                        result.Errors.Add($"animations[{aIdx}].samplers[{smpIdx}].input must be SCALAR FLOAT with min/max.");
                                    }
                                    else if (inAcc.BufferView >= 0 && inAcc.BufferView < bufferViews.Count)
                                    {
                                        var bv = bufferViews[inAcc.BufferView];
                                        if (bv.Buffer >= 0 && bv.Buffer < buffers.Count)
                                        {
                                            ValidateMonotonicTimestamps(buffers[bv.Buffer], bv.ByteOffset + inAcc.ByteOffset, inAcc.Count, aIdx, smpIdx, result);
                                        }
                                    }
                                }
                                if (outputIdx < 0 || outputIdx >= accessors.Count)
                                {
                                    result.Errors.Add($"animations[{aIdx}].samplers[{smpIdx}].output out of bounds.");
                                }

                                samplers.Add((inputIdx, outputIdx));
                                smpIdx++;
                            }
                        }

                        if (anim.TryGetProperty("channels", out var chsEl) && chsEl.ValueKind == JsonValueKind.Array)
                        {
                            int cIdx = 0;
                            foreach (var ch in chsEl.EnumerateArray())
                            {
                                int samplerIdx = ch.TryGetProperty("sampler", out var sp) ? sp.GetInt32() : -1;
                                if (samplerIdx < 0 || samplerIdx >= samplers.Count)
                                {
                                    result.Errors.Add($"animations[{aIdx}].channels[{cIdx}].sampler out of bounds.");
                                    cIdx++;
                                    continue;
                                }

                                if (!ch.TryGetProperty("target", out var target))
                                {
                                    result.Errors.Add($"animations[{aIdx}].channels[{cIdx}] missing target.");
                                    cIdx++;
                                    continue;
                                }

                                string path = target.TryGetProperty("path", out var pathEl) ? (pathEl.GetString() ?? string.Empty) : string.Empty;
                                int targetNode = target.TryGetProperty("node", out var tnEl) ? tnEl.GetInt32() : -1;
                                if (targetNode < 0 || targetNode >= nodeCount)
                                {
                                    result.Errors.Add($"animations[{aIdx}].channels[{cIdx}].target.node ({targetNode}) out of bounds.");
                                }

                                var (inIdx, outIdx) = samplers[samplerIdx];
                                if (inIdx >= 0 && inIdx < accessors.Count && outIdx >= 0 && outIdx < accessors.Count)
                                {
                                    var inAcc = accessors[inIdx];
                                    var outAcc = accessors[outIdx];
                                    switch (path)
                                    {
                                        case "translation":
                                        case "scale":
                                            if (outAcc.Type != "VEC3" || outAcc.Count != inAcc.Count)
                                                result.Errors.Add($"animations[{aIdx}].channels[{cIdx}] '{path}' output must be VEC3 with count {inAcc.Count}.");
                                            break;
                                        case "rotation":
                                            if (outAcc.Type != "VEC4" || outAcc.Count != inAcc.Count)
                                                result.Errors.Add($"animations[{aIdx}].channels[{cIdx}] 'rotation' output must be VEC4 with count {inAcc.Count}.");
                                            break;
                                        case "weights":
                                            int targetMorphCount = targetNode >= 0 && targetNode < nodeCount && nodeMeshIndices[targetNode].HasValue
                                                ? meshTargetCounts[nodeMeshIndices[targetNode]!.Value]
                                                : 0;
                                            if (outAcc.Type != "SCALAR" || targetMorphCount <= 0 || outAcc.Count != inAcc.Count * targetMorphCount)
                                                result.Errors.Add($"animations[{aIdx}].channels[{cIdx}] 'weights' output count {outAcc.Count} != keyframes ({inAcc.Count}) * targets ({targetMorphCount}).");
                                            break;
                                        default:
                                            result.Errors.Add($"animations[{aIdx}].channels[{cIdx}] unsupported target path '{path}'.");
                                            break;
                                    }
                                }

                                cIdx++;
                            }
                        }

                        aIdx++;
                    }
                }

                // 12. Root extensions check
                CollectExtensions(root, observedExtensions);
                foreach (var ext in observedExtensions)
                {
                    if (!declaredExtensions.Contains(ext))
                    {
                        result.Errors.Add($"Extension '{ext}' is used in the glTF payload but missing from top-level extensionsUsed.");
                    }
                }
            }
        }

        private static bool HasCycle(int node, List<int>[] childrenOf, byte[] visitState)
        {
            visitState[node] = 1;
            foreach (var child in childrenOf[node])
            {
                if (visitState[child] == 1) return true;
                if (visitState[child] == 0 && HasCycle(child, childrenOf, visitState)) return true;
            }
            visitState[node] = 2;
            return false;
        }

        private static void CollectExtensions(JsonElement el, HashSet<string> observed)
        {
            if (el.TryGetProperty("extensions", out var extEl) && extEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in extEl.EnumerateObject())
                {
                    observed.Add(prop.Name);
                }
            }
        }

        private static void ValidateTextureRef(
            JsonElement parent,
            string propName,
            int textureCount,
            string context,
            HashSet<string> observedExtensions,
            GltfValidationResult result)
        {
            if (!parent.TryGetProperty(propName, out var texRef) || texRef.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            CollectExtensions(texRef, observedExtensions);
            if (!texRef.TryGetProperty("index", out var idxEl))
            {
                result.Errors.Add($"{context}.{propName} is missing required index.");
                return;
            }

            int texIdx = idxEl.GetInt32();
            if (texIdx < 0 || texIdx >= textureCount)
            {
                result.Errors.Add($"{context}.{propName}.index ({texIdx}) is out of bounds.");
            }
        }

        private static void ValidateNormalizedRange(JsonElement arr, int expectedLen, string context, GltfValidationResult result)
        {
            if (arr.GetArrayLength() != expectedLen)
            {
                result.Errors.Add($"{context} must have {expectedLen} components.");
                return;
            }

            for (int i = 0; i < expectedLen; i++)
            {
                float v = arr[i].GetSingle();
                if (!float.IsFinite(v) || v < -1e-5f || v > 1.00001f)
                {
                    result.Errors.Add($"{context}[{i}]={v} is outside [0, 1].");
                }
            }
        }

        private static void ValidateFloatAccessorData(
            byte[] buffer,
            int byteOffset,
            int stride,
            int count,
            int compCount,
            float[]? declaredMin,
            float[]? declaredMax,
            int accIdx,
            GltfValidationResult result)
        {
            var actualMin = new float[compCount];
            var actualMax = new float[compCount];
            Array.Fill(actualMin, float.PositiveInfinity);
            Array.Fill(actualMax, float.NegativeInfinity);

            for (int i = 0; i < count; i++)
            {
                int elemBase = byteOffset + i * stride;
                for (int c = 0; c < compCount; c++)
                {
                    float v = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(elemBase + c * 4, 4));
                    if (!float.IsFinite(v))
                    {
                        result.Errors.Add($"accessors[{accIdx}] contains non-finite float at element {i} component {c}.");
                        return;
                    }
                    if (v < actualMin[c]) actualMin[c] = v;
                    if (v > actualMax[c]) actualMax[c] = v;
                }
            }

            if (declaredMin != null && declaredMin.Length == compCount)
            {
                for (int c = 0; c < compCount; c++)
                {
                    if (MathF.Abs(declaredMin[c] - actualMin[c]) > 1e-4f)
                    {
                        result.Errors.Add($"accessors[{accIdx}].min[{c}] ({declaredMin[c]}) does not match actual buffer min ({actualMin[c]}).");
                    }
                }
            }

            if (declaredMax != null && declaredMax.Length == compCount)
            {
                for (int c = 0; c < compCount; c++)
                {
                    if (MathF.Abs(declaredMax[c] - actualMax[c]) > 1e-4f)
                    {
                        result.Errors.Add($"accessors[{accIdx}].max[{c}] ({declaredMax[c]}) does not match actual buffer max ({actualMax[c]}).");
                    }
                }
            }
        }

        private static void ValidateIndexAccessor(
            byte[] buffer,
            int byteOffset,
            int componentType,
            int count,
            int vertexCount,
            int meshIdx,
            int primIdx,
            GltfValidationResult result)
        {
            for (int i = 0; i < count; i++)
            {
                uint index = componentType switch
                {
                    ComponentTypeUnsignedByte => buffer[byteOffset + i],
                    ComponentTypeUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(byteOffset + i * 2, 2)),
                    ComponentTypeUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(byteOffset + i * 4, 4)),
                    _ => uint.MaxValue
                };

                if (componentType == ComponentTypeUnsignedShort && index == ushort.MaxValue)
                {
                    result.Errors.Add($"meshes[{meshIdx}].primitives[{primIdx}] UNSIGNED_SHORT index contains forbidden primitive restart value 65535.");
                    return;
                }

                if (index >= (uint)vertexCount)
                {
                    result.Errors.Add($"meshes[{meshIdx}].primitives[{primIdx}] index[{i}]={index} >= vertexCount {vertexCount}.");
                    return;
                }
            }
        }

        private static void ValidateMonotonicTimestamps(
            byte[] buffer,
            int byteOffset,
            int count,
            int animIdx,
            int samplerIdx,
            GltfValidationResult result)
        {
            float prev = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                float t = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(byteOffset + i * 4, 4));
                if (t < 0f || t < prev)
                {
                    result.Errors.Add($"animations[{animIdx}].samplers[{samplerIdx}] timestamp[{i}]={t} is invalid or not monotonic (prev={prev}).");
                    return;
                }
                prev = t;
            }
        }

        private static float[]? ReadFloatArray(JsonElement obj, string propName)
        {
            if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = new float[arr.GetArrayLength()];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = arr[i].GetSingle();
            }
            return values;
        }

        private static int GetComponentByteSize(int componentType) => componentType switch
        {
            ComponentTypeByte or ComponentTypeUnsignedByte => 1,
            ComponentTypeShort or ComponentTypeUnsignedShort => 2,
            ComponentTypeUnsignedInt or ComponentTypeFloat => 4,
            _ => 0
        };

        private static int GetTypeComponentCount(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" or "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => 16,
            _ => 0
        };

        private sealed record AccessorInfo(
            int BufferView,
            int ByteOffset,
            int ComponentType,
            int Count,
            string Type,
            int ComponentCount,
            float[]? Min,
            float[]? Max);
    }
}
