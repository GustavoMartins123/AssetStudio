using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AssetStudio.Avalonia;

internal sealed class MaterialPreviewData
{
    public MaterialPreviewData(string infoText, Texture2D? previewTexture, string? resolvedShaderName = null)
    {
        InfoText = infoText;
        PreviewTexture = previewTexture;
        ResolvedShaderName = resolvedShaderName ?? string.Empty;
    }

    public string InfoText { get; }
    public Texture2D? PreviewTexture { get; }
    public string ResolvedShaderName { get; }
}

internal sealed class MaterialPreviewBuilder
{
    public static readonly string[] PreferredTextureSlots =
    {
        "_BaseMap",
        "_MainTex",
        "texture",
        "Texture",
        "_Texture",
        "_BaseColorMap",
        "_BaseColorTexture",
        "_Diffuse",
        "_AlbedoMap",
        "_Albedo",
        "_MainTexture",
        "_BaseColorTex",
        "_AlbedoTex",
        "_DiffuseTex",
        "_DiffuseMap",
        "_FaceTex",
        "_BaseEyeIrisMap",
        "_EyeIrisMap",
        "_IrisMap",
        "_IrisTex",
        "_EyeTex",
        "_EyeMap",
        "_BaseEyeWhiteMap"
    };

    public static readonly HashSet<string> NonDiffuseTextureSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "_BumpMap",
        "_NormalMap",
        "_DetailNormalMap",
        "_DetailNormalMapScale",
        "_Normal",
        "Normal",
        "normal",
        "_MetallicGlossMap",
        "_SpecGlossMap",
        "_OcclusionMap",
        "_EmissionMap",
        "_ParallaxMap",
        "_DetailMask",
        "_Cubemap",
        "_ReflectionTex",
        "_ShadowMap",
        "_MaskTexture",
        "_MaskMap",
        "_Mask",
        "_WeatheringMask",
        "_EmissionMask",
        "_MatcapMap",
        "_Matcap",
        "_MatcapTex",
        "_MatcapMaskMap",
        "_WetTexture",
        "_BaseEyeHighlightMap",
        "_BaseEyeHighlightMapBackup",
        "_BaseEyePupilMap",
        "_BaseEyePupilMapBackup",
        "_UVScrollTexture1",
        "_UVScrollTexture2",
        "_RoughnessMap",
        "_MetallicMap",
        "_AOMap",
        "_AmbientOcclusionMap",
        "_SpecularMap",
        "_SpecMap",
        "_RampMap",
        "_RampTex",
        "_Ramp",
        "_ToonRamp",
        "_LUT",
        "_LutTex",
        "_OutlineTex",
        "_OutlineMap"
    };

    private readonly AssetsManager assetsManager;
    private readonly Func<Material, Material?> resolveMaterialForPreview;
    private readonly Func<Material, string, Texture2D?> getMaterialTextureSlot;
    private readonly Func<Material, Texture2D?> findTextureForMaterial;

    public MaterialPreviewBuilder(
        AssetsManager assetsManager,
        Func<Material, Material?> resolveMaterialForPreview,
        Func<Material, string, Texture2D?> getMaterialTextureSlot,
        Func<Material, Texture2D?> findTextureForMaterial)
    {
        this.assetsManager = assetsManager;
        this.resolveMaterialForPreview = resolveMaterialForPreview;
        this.getMaterialTextureSlot = getMaterialTextureSlot;
        this.findTextureForMaterial = findTextureForMaterial;
    }

    public MaterialPreviewData Build(Material material)
    {
        var displayMaterial = resolveMaterialForPreview(material) ?? material;
        var sb = new StringBuilder();
        sb.AppendLine($"Material: {material.m_Name}");
        if (!ReferenceEquals(displayMaterial, material))
        {
            sb.AppendLine($"Parent material: {displayMaterial.m_Name}");
        }

        var resolvedShaderName = AppendShaderReference(sb, displayMaterial);
        sb.AppendLine();
        sb.AppendLine("Texture slots:");

        var texEnvs = displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>();
        if (texEnvs.Length == 0)
        {
            sb.AppendLine("  <none>");
        }

        var resolvedSlots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        foreach (var texEnv in texEnvs)
        {
            sb.Append($"  {texEnv.Key}: ");
            var texEnvValue = texEnv.Value;
            var textureRef = texEnvValue?.m_Texture;
            var texture = texEnvValue != null ? getMaterialTextureSlot(displayMaterial, texEnv.Key) : null;
            if (!string.IsNullOrWhiteSpace(texEnv.Key))
            {
                resolvedSlots[texEnv.Key] = texture;
            }

            if (texture != null && textureRef != null)
            {
                sb.AppendLine($"{texture.m_Name} ({texture.m_Width}x{texture.m_Height}, {texture.m_TextureFormat})");
                sb.AppendLine($"    FileID: {textureRef.m_FileID}, PathID: {textureRef.m_PathID}");
                sb.AppendLine($"    Scale: {texEnvValue?.m_Scale.X}, {texEnvValue?.m_Scale.Y}");
                sb.AppendLine($"    Offset: {texEnvValue?.m_Offset.X}, {texEnvValue?.m_Offset.Y}");
            }
            else
            {
                sb.AppendLine(textureRef == null || textureRef.IsNull
                    ? "null"
                    : $"missing (FileID: {textureRef.m_FileID}, PathID: {textureRef.m_PathID})");
            }
        }

        AppendScalarProperties(sb, displayMaterial);

        var previewTexture = SelectMainTexture(displayMaterial, resolvedSlots);
        if (previewTexture == null)
        {
            previewTexture = findTextureForMaterial(displayMaterial);
        }

        return new MaterialPreviewData(sb.ToString(), previewTexture, resolvedShaderName);
    }

    public static bool IsPreferredTextureSlot(string? propertyName)
    {
        return !string.IsNullOrWhiteSpace(propertyName)
            && PreferredTextureSlots.Contains(propertyName, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsNonDiffuseTextureSlot(string? propertyName)
    {
        return !string.IsNullOrWhiteSpace(propertyName)
            && NonDiffuseTextureSlots.Contains(propertyName);
    }

    public static string? SelectMainTextureSlotName(Material? displayMaterial)
    {
        var texEnvs = displayMaterial?.m_SavedProperties?.m_TexEnvs;
        if (texEnvs == null || texEnvs.Length == 0)
        {
            return null;
        }

        foreach (var preferredSlot in PreferredTextureSlots)
        {
            foreach (var texEnv in texEnvs)
            {
                if (string.Equals(texEnv.Key, preferredSlot, StringComparison.OrdinalIgnoreCase)
                    && texEnv.Value?.m_Texture != null
                    && !texEnv.Value.m_Texture.IsNull)
                {
                    return texEnv.Key;
                }
            }
        }

        foreach (var texEnv in texEnvs)
        {
            if (!string.IsNullOrWhiteSpace(texEnv.Key)
                && !IsNonDiffuseTextureSlot(texEnv.Key)
                && texEnv.Value?.m_Texture != null
                && !texEnv.Value.m_Texture.IsNull)
            {
                return texEnv.Key;
            }
        }

        return null;
    }

    public static Texture2D? SelectMainTexture(Material? displayMaterial, IReadOnlyDictionary<string, Texture2D?>? textureSlots)
    {
        if (textureSlots == null || textureSlots.Count == 0)
        {
            return null;
        }

        foreach (var preferredSlot in PreferredTextureSlots)
        {
            if (TryGetSlotTexture(textureSlots, preferredSlot, out var preferredTex) && preferredTex != null)
            {
                return preferredTex;
            }
        }

        var texEnvs = displayMaterial?.m_SavedProperties?.m_TexEnvs;
        if (texEnvs != null)
        {
            foreach (var env in texEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Key) || IsNonDiffuseTextureSlot(env.Key))
                {
                    continue;
                }

                if (TryGetSlotTexture(textureSlots, env.Key, out var tex) && tex != null)
                {
                    return tex;
                }
            }
        }
        else
        {
            foreach (var kvp in textureSlots)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !IsNonDiffuseTextureSlot(kvp.Key) && kvp.Value != null)
                {
                    return kvp.Value;
                }
            }
        }

        return null;
    }

    private static bool TryGetSlotTexture(
        IReadOnlyDictionary<string, Texture2D?> textureSlots,
        string slotName,
        out Texture2D? texture)
    {
        if (textureSlots.TryGetValue(slotName, out texture))
        {
            return true;
        }

        foreach (var kvp in textureSlots)
        {
            if (string.Equals(kvp.Key, slotName, StringComparison.OrdinalIgnoreCase))
            {
                texture = kvp.Value;
                return true;
            }
        }

        texture = null;
        return false;
    }

    private string AppendShaderReference(StringBuilder sb, Material material)
    {
        var shaderRef = material.m_Shader;
        if (shaderRef == null || shaderRef.IsNull)
        {
            sb.AppendLine("Shader: <none>");
            return string.Empty;
        }

        var shaderName = TryGetLoadedShaderName(shaderRef);
        var sourceName = string.Empty;
        if (shaderRef.TryGetAssetsFile(out var shaderFile))
        {
            sourceName = shaderFile.fileName;
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                var shaderHandle = assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(shaderFile, shaderRef.m_PathID));
                shaderName = shaderHandle?.Name;
            }
        }

        var resolvedName = shaderName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(shaderName))
        {
            shaderName = "unloaded or unsupported shader";
        }

        sb.AppendLine($"Shader: {shaderName} (FileID: {shaderRef.m_FileID}, PathID: {shaderRef.m_PathID})");
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            sb.AppendLine($"Shader source: {sourceName}");
        }

        return resolvedName;
    }

    private static string? TryGetLoadedShaderName(PPtr<Shader> shaderRef)
    {
        if (shaderRef == null || !shaderRef.TryGetAssetsFile(out var sourceFile) || sourceFile?.ObjectsDic == null)
        {
            return null;
        }

        lock (sourceFile)
        {
            if (sourceFile.ObjectsDic.TryGetValue(shaderRef.m_PathID, out var loadedObject) && loadedObject is Shader shader)
            {
                return shader.m_ParsedForm?.m_Name ?? shader.m_Name;
            }
        }

        return null;
    }

    private static void AppendScalarProperties(StringBuilder sb, Material material)
    {
        var properties = material.m_SavedProperties;
        if (properties == null)
        {
            return;
        }

        AppendIntProperties(sb, properties.m_Ints);
        AppendFloatProperties(sb, properties.m_Floats);
        AppendColorProperties(sb, properties.m_Colors);
    }

    private static void AppendIntProperties(StringBuilder sb, KeyValuePair<string, int>[]? values)
    {
        sb.AppendLine();
        sb.AppendLine("Int properties:");
        if (values == null || values.Length == 0)
        {
            sb.AppendLine("  <none>");
            return;
        }

        foreach (var value in values)
        {
            sb.AppendLine($"  {value.Key}: {value.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void AppendFloatProperties(StringBuilder sb, KeyValuePair<string, float>[]? values)
    {
        sb.AppendLine();
        sb.AppendLine("Float properties:");
        if (values == null || values.Length == 0)
        {
            sb.AppendLine("  <none>");
            return;
        }

        foreach (var value in values)
        {
            sb.AppendLine($"  {value.Key}: {value.Value.ToString("0.######", CultureInfo.InvariantCulture)}");
        }
    }

    private static void AppendColorProperties(StringBuilder sb, KeyValuePair<string, Color>[]? values)
    {
        sb.AppendLine();
        sb.AppendLine("Color properties:");
        if (values == null || values.Length == 0)
        {
            sb.AppendLine("  <none>");
            return;
        }

        foreach (var value in values)
        {
            var color = value.Value;
            sb.AppendLine(
                $"  {value.Key}: R={color.R.ToString("0.######", CultureInfo.InvariantCulture)}, " +
                $"G={color.G.ToString("0.######", CultureInfo.InvariantCulture)}, " +
                $"B={color.B.ToString("0.######", CultureInfo.InvariantCulture)}, " +
                $"A={color.A.ToString("0.######", CultureInfo.InvariantCulture)}");
        }
    }
}
