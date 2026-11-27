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

            var consumedTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var preservedChannels = new List<string>(10) { "baseColorFactor" };
            var matExtensions = new HashSet<string>(StringComparer.Ordinal);
            var approximations = new List<string>();
            var ignoredTextureSlots = new List<string>();
            var missingTextures = new List<string>();
            bool maskDowngradedToOpaque = false;
            bool specGlossConverted = false;

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
                        default:
                            ignoredTextureSlots.Add(t.Name);
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

            if (diffuseTex != null)
            {
                if (TryRegisterTexture(diffuseTex.Name, out int baseColorTexIdx))
                {
                    gltfMat.BaseColorTexture = CreateTextureInfo(baseColorTexIdx, diffuseTex);
                    consumedTextureNames.Add(diffuseTex.Name);
                    preservedChannels.Add("baseColorTexture");
                    if (gltfMat.BaseColorTexture.Offset != null) matExtensions.Add("KHR_texture_transform");
                }
                else
                {
                    missingTextures.Add($"Diffuse:{diffuseTex.Name}");
                }
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
                    if (metallicTex != null && srcMetal != null) consumedTextureNames.Add(metallicTex.Name);
                    if (roughnessTex != null && srcRough != null) consumedTextureNames.Add(roughnessTex.Name);
                    preservedChannels.Add("metallicRoughnessTexture");
                    if (gltfMat.MetallicRoughnessTexture.Offset != null) matExtensions.Add("KHR_texture_transform");
                }
                else
                {
                    if (metallicTex != null) missingTextures.Add($"Metallic:{metallicTex.Name}");
                    if (roughnessTex != null) missingTextures.Add($"Roughness:{roughnessTex.Name}");
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
                    consumedTextureNames.Add(specularTex.Name);
                    preservedChannels.Add("metallicRoughnessTexture");
                    if (gltfMat.MetallicRoughnessTexture.Offset != null) matExtensions.Add("KHR_texture_transform");
                    specGlossConverted = true;
                }
                else if (srcSpec == null)
                {
                    missingTextures.Add($"Specular:{specularTex.Name}");
                }
                else
                {
                    specGlossConverted = true;
                }
            }

            if (normalTex != null)
            {
                if (TryRegisterTexture(normalTex.Name, out int normalTexIdx))
                {
                    gltfMat.NormalTexture = CreateTextureInfo(normalTexIdx, normalTex);
                    consumedTextureNames.Add(normalTex.Name);
                    preservedChannels.Add("normalTexture");
                    if (gltfMat.NormalTexture.Offset != null) matExtensions.Add("KHR_texture_transform");
                    if (mat.BumpScale.HasValue && float.IsFinite(mat.BumpScale.Value) && MathF.Abs(mat.BumpScale.Value - 1f) > 1e-4f)
                    {
                        gltfMat.NormalScale = Math.Max(0f, mat.BumpScale.Value);
                    }
                }
                else
                {
                    missingTextures.Add($"Normal:{normalTex.Name}");
                }
            }

            if (occlusionTex != null)
            {
                if (TryRegisterOcclusionTexture(matName, occlusionTex.Name, out int occTexIdx))
                {
                    gltfMat.OcclusionTexture = CreateTextureInfo(occTexIdx, occlusionTex);
                    consumedTextureNames.Add(occlusionTex.Name);
                    preservedChannels.Add("occlusionTexture");
                    if (gltfMat.OcclusionTexture.Offset != null) matExtensions.Add("KHR_texture_transform");
                    if (mat.OcclusionStrength.HasValue && float.IsFinite(mat.OcclusionStrength.Value))
                    {
                        float strength = Clamp01(mat.OcclusionStrength.Value);
                        if (MathF.Abs(strength - 1f) > 1e-4f)
                        {
                            gltfMat.OcclusionStrength = strength;
                        }
                    }
                }
                else
                {
                    missingTextures.Add($"Occlusion:{occlusionTex.Name}");
                }
            }

            var emissive = new[]
            {
                Clamp01(SanitizeFloat(mat.Emissive.R, 0f)),
                Clamp01(SanitizeFloat(mat.Emissive.G, 0f)),
                Clamp01(SanitizeFloat(mat.Emissive.B, 0f))
            };
            bool hasEmissiveColor = emissive[0] > 1e-4f || emissive[1] > 1e-4f || emissive[2] > 1e-4f;
            if (emissiveTex != null)
            {
                if (TryRegisterTexture(emissiveTex.Name, out int emTexIdx))
                {
                    gltfMat.EmissiveTexture = CreateTextureInfo(emTexIdx, emissiveTex);
                    gltfMat.EmissiveFactor = hasEmissiveColor ? emissive : new[] { 1f, 1f, 1f };
                    consumedTextureNames.Add(emissiveTex.Name);
                    preservedChannels.Add("emissiveTexture");
                    if (gltfMat.EmissiveTexture.Offset != null) matExtensions.Add("KHR_texture_transform");
                }
                else
                {
                    missingTextures.Add($"Emissive:{emissiveTex.Name}");
                    if (hasEmissiveColor)
                    {
                        gltfMat.EmissiveFactor = emissive;
                        preservedChannels.Add("emissiveFactor");
                    }
                }
            }
            else if (hasEmissiveColor)
            {
                gltfMat.EmissiveFactor = emissive;
                preservedChannels.Add("emissiveFactor");
            }

            // Alpha mode
            if (mat.SurfaceMode.HasValue)
            {
                int modeInt = (int)MathF.Round(mat.SurfaceMode.Value);
                if (modeInt == 1)
                {
                    if (gltfMat.BaseColorTexture != null)
                    {
                        gltfMat.AlphaMode = "MASK";
                        gltfMat.AlphaCutoff = mat.AlphaCutoff.HasValue ? Clamp01(mat.AlphaCutoff.Value) : 0.5f;
                    }
                    else
                    {
                        // Downgrade MASK without a baseColorTexture to OPAQUE so untextured materials never clip geometry to invisible.
                        gltfMat.AlphaMode = "OPAQUE";
                        gltfMat.AlphaCutoff = null;
                        maskDowngradedToOpaque = true;
                    }
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

            if (mat.IsUnlit)
            {
                gltfMat.IsUnlit = true;
                _extensionsUsed.Add("KHR_materials_unlit");
                matExtensions.Add("KHR_materials_unlit");
            }
            else
            {
                if (mat.TransmissionFactor.HasValue && float.IsFinite(mat.TransmissionFactor.Value))
                {
                    float tf = Clamp01(mat.TransmissionFactor.Value);
                    if (tf > 1e-4f)
                    {
                        gltfMat.TransmissionFactor = tf;
                        _extensionsUsed.Add("KHR_materials_transmission");
                        matExtensions.Add("KHR_materials_transmission");
                    }
                }

                if (mat.Ior.HasValue && float.IsFinite(mat.Ior.Value))
                {
                    float ior = Math.Clamp(mat.Ior.Value, 1.0f, 3.0f);
                    if (gltfMat.TransmissionFactor.HasValue || MathF.Abs(ior - 1.5f) > 1e-4f)
                    {
                        gltfMat.Ior = ior;
                        _extensionsUsed.Add("KHR_materials_ior");
                        matExtensions.Add("KHR_materials_ior");
                    }
                }
            }

            gltfMat.UnityShaderMetadata = mat.UnityShaderMetadata;

            var effectiveAlphaMode = gltfMat.AlphaMode ?? "OPAQUE";
            preservedChannels.Add($"alphaMode:{effectiveAlphaMode}");
            if (gltfMat.DoubleSided) preservedChannels.Add("doubleSided");
            if (matExtensions.Contains("KHR_texture_transform")) preservedChannels.Add("uvTransform");

            _materialDiagnostics.Add(BuildMaterialConversionDiagnostic(
                matName,
                mat,
                gltfMat,
                effectiveAlphaMode,
                consumedTextureNames,
                preservedChannels,
                matExtensions,
                approximations,
                ignoredTextureSlots,
                missingTextures,
                specGlossConverted,
                maskDowngradedToOpaque));

            int matIdx = _materials.Count;
            _materials.Add(gltfMat);
            _materialIndexByName[matName] = matIdx;
        }

        private static MaterialConversionDiagnostic BuildMaterialConversionDiagnostic(
            string matName,
            ImportedMaterial mat,
            GltfMaterial gltfMat,
            string effectiveAlphaMode,
            HashSet<string> consumedTextureNames,
            List<string> preservedChannels,
            HashSet<string> matExtensions,
            List<string> approximations,
            List<string> ignoredTextureSlots,
            List<string> missingTextures,
            bool specGlossConverted,
            bool maskDowngradedToOpaque)
        {
            var shaderName = mat.UnityShaderMetadata?.ShaderName ?? string.Empty;
            var classification = mat.UnityShaderMetadata?.Classification ?? string.Empty;

            if (mat.UnityShaderMetadata?.TextureProperties != null)
            {
                foreach (var kv in mat.UnityShaderMetadata.TextureProperties)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value) && !consumedTextureNames.Contains(kv.Value))
                    {
                        if (!ignoredTextureSlots.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            ignoredTextureSlots.Add(kv.Key);
                        }
                    }
                }
            }

            if (specGlossConverted)
            {
                approximations.Add("Specular-glossiness map approximated as metallic-roughness (metallic=0, roughness=1-gloss).");
            }
            if (maskDowngradedToOpaque)
            {
                approximations.Add("AlphaMode MASK downgraded to OPAQUE because no baseColorTexture was resolved.");
            }

            bool isInvisible = string.Equals(classification, "Invisible", StringComparison.OrdinalIgnoreCase);
            if (isInvisible)
            {
                approximations.Add("Shader writes no color; exported as basic fallback material.");
            }
            else if (string.Equals(classification, "Water", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification, "Refractive", StringComparison.OrdinalIgnoreCase))
            {
                approximations.Add("Water/refractive shader approximated via KHR_materials_transmission and KHR_materials_ior.");
            }
            else if (string.Equals(classification, "Additive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification, "Effect", StringComparison.OrdinalIgnoreCase))
            {
                approximations.Add("Additive/effect shader approximated via glTF BLEND alpha mode.");
            }
            else if (ShaderConversionClassifier.IsUrpShader(shaderName))
            {
                approximations.Add($"URP shader '{shaderName}' approximated with glTF 2.0 metallic-roughness PBR.");
            }
            else if (ShaderConversionClassifier.IsHdrpShader(shaderName))
            {
                approximations.Add($"HDRP shader '{shaderName}' approximated with glTF 2.0 metallic-roughness PBR.");
            }
            else if (ShaderConversionClassifier.IsShaderGraphShader(shaderName))
            {
                approximations.Add($"Shader Graph '{shaderName}' approximated with glTF 2.0 metallic-roughness PBR.");
            }
            else if (ShaderConversionClassifier.IsStandardSpecularShader(shaderName))
            {
                approximations.Add("Built-in Standard (Specular setup) approximated with glTF 2.0 metallic-roughness PBR.");
            }
            else if (!string.IsNullOrWhiteSpace(shaderName)
                && !ShaderConversionClassifier.IsStandardBuiltInPbrShader(shaderName)
                && !ShaderConversionClassifier.IsBuiltInUnlitShader(shaderName))
            {
                approximations.Add($"Custom shader '{shaderName}' approximated with glTF 2.0 PBR (custom shader programs are not converted).");
            }

            if (!gltfMat.IsUnlit
                && !mat.Roughness.HasValue
                && !mat.Smoothness.HasValue
                && gltfMat.MetallicRoughnessTexture == null
                && (mat.UnityShaderMetadata?.FloatProperties.ContainsKey("_Shininess") == true
                    || MathF.Abs(mat.Shininess - 20f) > 1e-3f))
            {
                approximations.Add("Legacy Shininess scalar converted to roughness approximation.");
            }

            bool hasAnyResolvedTexture = gltfMat.BaseColorTexture != null
                || gltfMat.MetallicRoughnessTexture != null
                || gltfMat.NormalTexture != null
                || gltfMat.OcclusionTexture != null
                || gltfMat.EmissiveTexture != null;

            MaterialConversionMode mode;
            if (isInvisible || (missingTextures.Count > 0 && !hasAnyResolvedTexture))
            {
                mode = MaterialConversionMode.BasicFallback;
            }
            else if (gltfMat.IsUnlit && approximations.Count == 0 && ignoredTextureSlots.Count == 0 && missingTextures.Count == 0)
            {
                mode = MaterialConversionMode.Unlit;
            }
            else if (approximations.Count > 0 || ignoredTextureSlots.Count > 0 || missingTextures.Count > 0)
            {
                mode = MaterialConversionMode.PbrApproximation;
            }
            else
            {
                mode = MaterialConversionMode.StandardPbr;
            }

            return new MaterialConversionDiagnostic
            {
                MaterialName = matName,
                ShaderName = shaderName,
                ShaderClassification = classification,
                Mode = mode,
                AlphaMode = effectiveAlphaMode,
                PreservedChannels = preservedChannels,
                ExtensionsUsed = matExtensions.OrderBy(static e => e, StringComparer.Ordinal).ToList(),
                Approximations = approximations,
                IgnoredTextureSlots = ignoredTextureSlots,
                MissingTextures = missingTextures
            };
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

        private bool TryRegisterOcclusionTexture(string matName, string textureName, out int textureIndex)
        {
            textureIndex = -1;
            var source = FindSourceTexture(textureName);
            if (source == null || source.Data == null || source.Data.Length == 0)
            {
                return false;
            }

            string packedCacheKey = $"occg:{source.Name}";
            if (_textureIndexByKey.TryGetValue(packedCacheKey, out textureIndex))
            {
                return textureIndex >= 0;
            }

            using var bmp = DecodeAnyToBgra(source.Data);
            if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
            {
                int width = bmp.Width;
                int height = bmp.Height;
                int pixelCount = checked(width * height);
                var pixels = new byte[checked(pixelCount * 4)];
                bmp.CopyBgraPixelsTo(pixels);

                long sumR = 0;
                long sumG = 0;
                int nonGrayCount = 0;
                byte minG = 255;

                for (int p = 0; p < pixelCount; p++)
                {
                    int off = p * 4;
                    byte b = pixels[off + 0];
                    byte g = pixels[off + 1];
                    byte r = pixels[off + 2];

                    sumR += r;
                    sumG += g;
                    if (g < minG)
                    {
                        minG = g;
                    }

                    if (Math.Abs(r - g) > 4 || Math.Abs(g - b) > 4)
                    {
                        nonGrayCount++;
                    }
                }

                double meanR = (double)sumR / pixelCount;
                double meanG = (double)sumG / pixelCount;
                double nonGrayRatio = (double)nonGrayCount / pixelCount;

                // Unity shaders (URP/HDRP/custom MaskMaps) frequently store Ambient Occlusion in channel G
                // while channel R holds dark Metallic/Specular (~0). glTF 2.0 samples occlusion strictly from R,
                // which would multiply lit albedo by ~0 (pitch black). Repack channel G -> grayscale R/G/B when detected.
                if (nonGrayRatio >= 0.10 && meanG >= 140.0 && (meanG - meanR) >= 50.0)
                {
                    if (minG >= 250)
                    {
                        _textureIndexByKey[packedCacheKey] = -1;
                        return false;
                    }

                    var packedBgra = new byte[checked(pixelCount * 4)];
                    for (int p = 0; p < pixelCount; p++)
                    {
                        int off = p * 4;
                        byte ao = pixels[off + 1];
                        packedBgra[off + 0] = ao;
                        packedBgra[off + 1] = ao;
                        packedBgra[off + 2] = ao;
                        packedBgra[off + 3] = 255;
                    }

                    using var packedBitmap = ImageExtensions.CreateBgraUnpremulBitmap(packedBgra, width, height, flipVertical: false);
                    using var ms = packedBitmap.ConvertToStream(ImageFormat.Png);
                    textureIndex = RegisterEncodedTexture(packedCacheKey, $"{matName}_occlusion", ms.ToArray(), "image/png", ".png");
                    return true;
                }
            }

            return TryRegisterTexture(textureName, out textureIndex);
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

    }
}
