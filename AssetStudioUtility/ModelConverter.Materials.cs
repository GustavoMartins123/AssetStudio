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
    public partial class ModelConverter
    {
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

    }
}
