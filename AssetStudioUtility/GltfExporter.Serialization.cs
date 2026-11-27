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
                        if (n.Camera.HasValue)
                        {
                            w.WriteNumber("camera", n.Camera.Value);
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
                        bool emitVisibilityFalse = n.IsInitiallyHidden;
                        bool emitVisibilityTrue = !n.IsInitiallyHidden
                            && i < _nodeParentIndices.Count
                            && _nodeParentIndices[i] >= 0
                            && _nodes[_nodeParentIndices[i]].IsInitiallyHidden;
                        bool hasVisibilityExt = emitVisibilityFalse || emitVisibilityTrue;

                        if (n.Light.HasValue || hasVisibilityExt)
                        {
                            w.WriteStartObject("extensions");
                            if (n.Light.HasValue)
                            {
                                w.WriteStartObject("KHR_lights_punctual");
                                w.WriteNumber("light", n.Light.Value);
                                w.WriteEndObject();
                            }
                            if (hasVisibilityExt)
                            {
                                w.WriteStartObject("KHR_node_visibility");
                                w.WriteBoolean("visible", emitVisibilityTrue);
                                w.WriteEndObject();
                            }
                            w.WriteEndObject();
                        }
                        if (n.Extras != null || !string.IsNullOrEmpty(n.HiddenReason))
                        {
                            WriteNodeExtras(w, n.Extras, n.HiddenReason);
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
                        bool hasMaterialExtensions = mat.IsUnlit || mat.TransmissionFactor.HasValue || mat.Ior.HasValue;
                        if (hasMaterialExtensions)
                        {
                            w.WriteStartObject("extensions");
                            if (mat.IsUnlit)
                            {
                                w.WriteStartObject("KHR_materials_unlit");
                                w.WriteEndObject();
                            }
                            else
                            {
                                if (mat.TransmissionFactor.HasValue)
                                {
                                    w.WriteStartObject("KHR_materials_transmission");
                                    w.WriteNumber("transmissionFactor", Clamp01(mat.TransmissionFactor.Value));
                                    w.WriteEndObject();
                                }
                                if (mat.Ior.HasValue)
                                {
                                    w.WriteStartObject("KHR_materials_ior");
                                    w.WriteNumber("ior", Math.Clamp(SanitizeFloat(mat.Ior.Value, 1.5f), 1.0f, 3.0f));
                                    w.WriteEndObject();
                                }
                            }
                            w.WriteEndObject();
                        }
                        if (mat.UnityShaderMetadata != null && HasUnityShaderMetadata(mat.UnityShaderMetadata))
                        {
                            WriteMaterialUnityShaderExtras(w, mat.UnityShaderMetadata);
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

                // cameras
                if (_cameras.Count > 0)
                {
                    w.WriteStartArray("cameras");
                    for (int i = 0; i < _cameras.Count; i++)
                    {
                        var cam = _cameras[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(cam.Name))
                        {
                            w.WriteString("name", cam.Name);
                        }

                        if (cam.IsOrthographic)
                        {
                            w.WriteString("type", "orthographic");
                            w.WriteStartObject("orthographic");
                            w.WriteNumber("xmag", SanitizeFloat(cam.XMag, 5f));
                            w.WriteNumber("ymag", SanitizeFloat(cam.YMag, 5f));
                            w.WriteNumber("zfar", SanitizeFloat(cam.ZFar, 1000f));
                            w.WriteNumber("znear", SanitizeFloat(cam.ZNear, 0.3f));
                            w.WriteEndObject();
                        }
                        else
                        {
                            w.WriteString("type", "perspective");
                            w.WriteStartObject("perspective");
                            if (cam.AspectRatio.HasValue && cam.AspectRatio.Value > 0f)
                            {
                                w.WriteNumber("aspectRatio", SanitizeFloat(cam.AspectRatio.Value, 1f));
                            }
                            w.WriteNumber("yfov", SanitizeFloat(cam.YFovRadians, 1.0471976f));
                            if (cam.ZFar > cam.ZNear)
                            {
                                w.WriteNumber("zfar", SanitizeFloat(cam.ZFar, 1000f));
                            }
                            w.WriteNumber("znear", SanitizeFloat(cam.ZNear, 0.3f));
                            w.WriteEndObject();
                        }

                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                // root extensions (KHR_lights_punctual)
                if (_lights.Count > 0)
                {
                    w.WriteStartObject("extensions");
                    w.WriteStartObject("KHR_lights_punctual");
                    w.WriteStartArray("lights");
                    for (int i = 0; i < _lights.Count; i++)
                    {
                        var light = _lights[i];
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(light.Name))
                        {
                            w.WriteString("name", light.Name);
                        }
                        w.WriteString("type", light.Type);
                        w.WriteStartArray("color");
                        w.WriteNumberValue(Clamp01(light.Color[0]));
                        w.WriteNumberValue(Clamp01(light.Color[1]));
                        w.WriteNumberValue(Clamp01(light.Color[2]));
                        w.WriteEndArray();
                        w.WriteNumber("intensity", Math.Max(0f, SanitizeFloat(light.Intensity, 1f)));
                        if (light.Range.HasValue && light.Range.Value > 0f && !string.Equals(light.Type, "directional", StringComparison.Ordinal))
                        {
                            w.WriteNumber("range", SanitizeFloat(light.Range.Value));
                        }
                        if (string.Equals(light.Type, "spot", StringComparison.Ordinal))
                        {
                            w.WriteStartObject("spot");
                            w.WriteNumber("innerConeAngle", SanitizeFloat(light.InnerConeAngleRadians));
                            w.WriteNumber("outerConeAngle", SanitizeFloat(light.OuterConeAngleRadians, 0.2617994f));
                            w.WriteEndObject();
                        }
                        if (!string.IsNullOrEmpty(light.UnityOriginalType))
                        {
                            w.WriteStartObject("extras");
                            w.WriteString("unityLightType", light.UnityOriginalType);
                            w.WriteEndObject();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.WriteEndObject();
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

        private static void WriteNodeExtras(Utf8JsonWriter w, ImportedFrameExtras? extras, string? hiddenReason)
        {
            w.WriteStartObject("extras");
            if (extras != null)
            {
                w.WriteNumber("pathId", extras.PathId);
                w.WriteString("sourceFile", extras.SourceFile ?? string.Empty);
                w.WriteNumber("tag", extras.Tag);
                w.WriteNumber("layer", extras.Layer);
                if (!extras.IsActive)
                {
                    w.WriteBoolean("isActive", false);
                }
                if (extras.IsScriptSpawned)
                {
                    w.WriteBoolean("scriptSpawned", true);
                    if (extras.ScriptSpawn != null)
                    {
                        var s = extras.ScriptSpawn;
                        w.WriteStartObject("scriptSpawn");
                        w.WriteString("scriptClass", s.ScriptClass ?? string.Empty);
                        w.WriteString("fieldPath", s.FieldPath ?? string.Empty);
                        w.WriteString("sourceKind", s.SourceKind ?? string.Empty);
                        w.WriteString("confidence", s.Confidence ?? string.Empty);
                        w.WriteNumber("ownerPathId", s.OwnerPathId);
                        if (s.AnchorTransformPathId != 0)
                        {
                            w.WriteNumber("anchorTransformPathId", s.AnchorTransformPathId);
                        }
                        if (!string.IsNullOrEmpty(s.TargetFileName))
                        {
                            w.WriteString("targetFileName", s.TargetFileName);
                        }
                        if (s.TargetPathId != 0)
                        {
                            w.WriteNumber("targetPathId", s.TargetPathId);
                        }
                        if (!string.IsNullOrEmpty(s.TargetGuid))
                        {
                            w.WriteString("targetGuid", s.TargetGuid);
                        }
                        w.WriteEndObject();
                    }
                }
            }
            if (!string.IsNullOrEmpty(hiddenReason))
            {
                w.WriteString("hiddenReason", hiddenReason);
            }
            w.WriteEndObject();
        }

        private static bool HasUnityShaderMetadata(ImportedUnityShaderMetadata meta)
        {
            return !string.IsNullOrEmpty(meta.ShaderName)
                || !string.IsNullOrEmpty(meta.Classification)
                || meta.RenderQueue.HasValue
                || !string.IsNullOrEmpty(meta.RenderType)
                || meta.Keywords.Count > 0
                || meta.FloatProperties.Count > 0
                || meta.ColorProperties.Count > 0
                || meta.TextureProperties.Count > 0
                || meta.Tags.Count > 0;
        }

        private static void WriteMaterialUnityShaderExtras(Utf8JsonWriter w, ImportedUnityShaderMetadata meta)
        {
            w.WriteStartObject("extras");
            w.WriteStartObject("unityShader");
            if (!string.IsNullOrEmpty(meta.ShaderName))
            {
                w.WriteString("shaderName", meta.ShaderName);
            }
            if (!string.IsNullOrEmpty(meta.Classification))
            {
                w.WriteString("classification", meta.Classification);
            }
            if (meta.RenderQueue.HasValue)
            {
                w.WriteNumber("renderQueue", meta.RenderQueue.Value);
            }
            if (!string.IsNullOrEmpty(meta.RenderType))
            {
                w.WriteString("renderType", meta.RenderType);
            }
            if (meta.Keywords.Count > 0)
            {
                w.WriteStartArray("keywords");
                for (int i = 0; i < meta.Keywords.Count; i++)
                {
                    if (!string.IsNullOrEmpty(meta.Keywords[i]))
                    {
                        w.WriteStringValue(meta.Keywords[i]);
                    }
                }
                w.WriteEndArray();
            }
            if (meta.FloatProperties.Count > 0)
            {
                w.WriteStartObject("floatProperties");
                foreach (var kv in meta.FloatProperties)
                {
                    w.WriteNumber(kv.Key, SanitizeFloat(kv.Value));
                }
                w.WriteEndObject();
            }
            if (meta.ColorProperties.Count > 0)
            {
                w.WriteStartObject("colorProperties");
                foreach (var kv in meta.ColorProperties)
                {
                    if (kv.Value == null || kv.Value.Length < 4)
                    {
                        continue;
                    }
                    w.WriteStartArray(kv.Key);
                    for (int c = 0; c < 4; c++)
                    {
                        w.WriteNumberValue(SanitizeFloat(kv.Value[c]));
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            if (meta.TextureProperties.Count > 0)
            {
                w.WriteStartObject("textureProperties");
                foreach (var kv in meta.TextureProperties)
                {
                    w.WriteString(kv.Key, kv.Value);
                }
                w.WriteEndObject();
            }
            if (meta.Tags.Count > 0)
            {
                w.WriteStartObject("tags");
                foreach (var kv in meta.Tags)
                {
                    w.WriteString(kv.Key, kv.Value);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private void ExportCameras(List<ImportedCamera>? cameraList)
        {
            if (cameraList == null || cameraList.Count == 0)
            {
                return;
            }

            for (int i = 0; i < cameraList.Count; i++)
            {
                var cam = cameraList[i];
                if (cam == null)
                {
                    continue;
                }

                int targetNodeIdx = ResolveComponentTargetNodeIndex(cam.TargetFrame, cam.Path, cam.Name);
                int cameraIdx = _cameras.Count;

                float nearClip = SanitizeFloat(cam.NearClipPlane, 0.3f);
                float farClip = SanitizeFloat(cam.FarClipPlane, 1000f);

                if (cam.IsOrthographic)
                {
                    float orthoNear = Math.Max(0f, nearClip);
                    float orthoFar = farClip > orthoNear ? farClip : orthoNear + 1000f;
                    float yMag = SanitizeFloat(cam.OrthographicSize, 5f);
                    if (yMag <= 1e-4f)
                    {
                        yMag = 5f;
                    }
                    yMag *= _scaleFactor;
                    float aspect = float.IsFinite(cam.AspectRatio) && cam.AspectRatio > 1e-4f ? cam.AspectRatio : 1f;
                    float xMag = yMag * aspect;

                    _cameras.Add(new GltfCamera
                    {
                        Name = string.IsNullOrWhiteSpace(cam.Name) ? $"Camera_{cameraIdx}" : cam.Name,
                        IsOrthographic = true,
                        XMag = xMag,
                        YMag = yMag,
                        ZNear = orthoNear * _scaleFactor,
                        ZFar = orthoFar * _scaleFactor
                    });
                }
                else
                {
                    float perspNear = Math.Max(1e-4f, nearClip) * _scaleFactor;
                    float perspFar = farClip > nearClip && farClip > 0f ? farClip * _scaleFactor : 0f;
                    float fovDeg = SanitizeFloat(cam.FieldOfViewDegrees, 60f);
                    if (fovDeg <= 0f || fovDeg >= 180f)
                    {
                        fovDeg = 60f;
                    }
                    fovDeg = Math.Clamp(fovDeg, 0.1f, 179.9f);
                    float yFovRad = DegreesToRadians(fovDeg);

                    _cameras.Add(new GltfCamera
                    {
                        Name = string.IsNullOrWhiteSpace(cam.Name) ? $"Camera_{cameraIdx}" : cam.Name,
                        IsOrthographic = false,
                        AspectRatio = float.IsFinite(cam.AspectRatio) && cam.AspectRatio > 1e-4f ? cam.AspectRatio : null,
                        YFovRadians = yFovRad,
                        ZNear = perspNear,
                        ZFar = perspFar
                    });
                }

                // glTF cameras look down local -Z; Unity cameras look down local +Z (preserved after ModelConverter X-reflection).
                // Apply a 180-degree local Y rotation either directly on an empty leaf node or via a child orientation node.
                int attachNodeIdx = AcquireOrientedComponentNode(targetNodeIdx, needsNegativeZFlip: true, suffix: "Camera", isCamera: true);
                _nodes[attachNodeIdx].Camera = cameraIdx;
            }
        }

        private void ExportLights(List<ImportedLight>? lightList)
        {
            if (lightList == null || lightList.Count == 0)
            {
                return;
            }

            for (int i = 0; i < lightList.Count; i++)
            {
                var light = lightList[i];
                if (light == null)
                {
                    continue;
                }

                int targetNodeIdx = ResolveComponentTargetNodeIndex(light.TargetFrame, light.Path, light.Name);
                int lightIdx = _lights.Count;

                string gltfLightType;
                string? unityOriginalType = null;
                bool needsOrientationFlip;
                switch (light.Type)
                {
                    case ImportedLightType.Directional:
                        gltfLightType = "directional";
                        needsOrientationFlip = true;
                        break;
                    case ImportedLightType.Spot:
                        gltfLightType = "spot";
                        needsOrientationFlip = true;
                        break;
                    case ImportedLightType.Area:
                        // KHR_lights_punctual supports directional, point, and spot only; degrade Area cleanly to point with metadata.
                        gltfLightType = "point";
                        unityOriginalType = "Area";
                        needsOrientationFlip = false;
                        break;
                    default:
                        gltfLightType = "point";
                        needsOrientationFlip = false;
                        break;
                }

                float intensity = Math.Max(0f, SanitizeFloat(light.Intensity, 1f));
                float? range = null;
                if (!string.Equals(gltfLightType, "directional", StringComparison.Ordinal))
                {
                    float rawRange = SanitizeFloat(light.Range, 0f);
                    if (rawRange > 0f)
                    {
                        range = rawRange * _scaleFactor;
                    }
                }

                float innerConeRad = 0f;
                float outerConeRad = 0f;
                if (string.Equals(gltfLightType, "spot", StringComparison.Ordinal))
                {
                    // Unity spotAngle / innerSpotAngle are FULL cone angles in degrees;
                    // KHR_lights_punctual requires HALF cone angles in radians with 0 <= innerConeAngle < outerConeAngle <= PI/2.
                    float outerDeg = SanitizeFloat(light.SpotAngleDegrees, 30f);
                    if (outerDeg <= 0f)
                    {
                        outerDeg = 30f;
                    }
                    outerConeRad = Math.Clamp(DegreesToRadians(outerDeg * 0.5f), 0.001f, MathF.PI * 0.5f);

                    float innerDeg = SanitizeFloat(light.InnerSpotAngleDegrees, 0f);
                    if (innerDeg > 0f)
                    {
                        float maxInner = Math.Max(0f, outerConeRad - 1e-4f);
                        innerConeRad = Math.Clamp(DegreesToRadians(innerDeg * 0.5f), 0f, maxInner);
                    }
                }

                _lights.Add(new GltfLight
                {
                    Name = string.IsNullOrWhiteSpace(light.Name) ? $"Light_{lightIdx}" : light.Name,
                    Type = gltfLightType,
                    Color = new[]
                    {
                        Clamp01(SanitizeFloat(light.Color.R, 1f)),
                        Clamp01(SanitizeFloat(light.Color.G, 1f)),
                        Clamp01(SanitizeFloat(light.Color.B, 1f))
                    },
                    Intensity = intensity,
                    Range = range,
                    InnerConeAngleRadians = innerConeRad,
                    OuterConeAngleRadians = outerConeRad,
                    UnityOriginalType = unityOriginalType
                });

                _extensionsUsed.Add("KHR_lights_punctual");

                int attachNodeIdx = AcquireOrientedComponentNode(targetNodeIdx, needsOrientationFlip, suffix: "Light", isCamera: false);
                _nodes[attachNodeIdx].Light = lightIdx;
            }
        }

        private int ResolveComponentTargetNodeIndex(ImportedFrame? targetFrame, string? path, string? fallbackName)
        {
            if (targetFrame != null && _nodeByFrame.TryGetValue(targetFrame, out int frameNodeIdx))
            {
                return frameNodeIdx;
            }

            if (TryResolveNodeIndex(path, out int pathNodeIdx))
            {
                return pathNodeIdx;
            }

            int rootParent = _sceneRootNodes.Count > 0 ? _sceneRootNodes[0] : -1;
            return CreateExtraNode(string.IsNullOrWhiteSpace(fallbackName) ? "SceneNode" : fallbackName, rootParent);
        }

        private int AcquireOrientedComponentNode(int targetNodeIdx, bool needsNegativeZFlip, string suffix, bool isCamera)
        {
            var node = _nodes[targetNodeIdx];
            bool slotTaken = isCamera ? node.Camera.HasValue : node.Light.HasValue;

            if (!needsNegativeZFlip)
            {
                if (!slotTaken)
                {
                    return targetNodeIdx;
                }

                return CreateExtraNode($"{node.Name}_{suffix}", targetNodeIdx);
            }

            bool canModifyNodeRotationInPlace =
                !slotTaken
                && !node.Mesh.HasValue
                && !node.Camera.HasValue
                && !node.Light.HasValue
                && node.Children.Count == 0
                && !_animatedNodeIndices.Contains(targetNodeIdx);

            var flipY180 = System.Numerics.Matrix4x4.CreateRotationY(MathF.PI);
            if (canModifyNodeRotationInPlace)
            {
                var currentRot = node.Rotation ?? System.Numerics.Quaternion.Identity;
                // Row-vector convention: child local R_y(180) applied before parent R maps glTF -Z -> parent +Z (Unity forward).
                var composedMat = flipY180 * System.Numerics.Matrix4x4.CreateFromQuaternion(currentRot);
                var composedRot = NormalizeQuaternionSafe(System.Numerics.Quaternion.CreateFromRotationMatrix(composedMat));
                node.Rotation = IsNearlyIdentity(composedRot) ? null : composedRot;
                return targetNodeIdx;
            }

            return CreateExtraNode($"{node.Name}_{suffix}", targetNodeIdx, localRow: flipY180);
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

    }
}
