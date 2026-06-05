using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AssetStudio.Avalonia;

internal sealed class MaterialPreviewData
{
    public MaterialPreviewData(string infoText, Texture2D? previewTexture)
    {
        InfoText = infoText;
        PreviewTexture = previewTexture;
    }

    public string InfoText { get; }
    public Texture2D? PreviewTexture { get; }
}

internal sealed class MaterialPreviewBuilder
{
    internal static readonly string[] PreferredTextureSlots =
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
        "_Albedo"
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

        AppendShaderReference(sb, displayMaterial);
        sb.AppendLine();
        sb.AppendLine("Texture slots:");

        Texture2D? previewTexture = null;
        var texEnvs = displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>();
        if (texEnvs.Length == 0)
        {
            sb.AppendLine("  <none>");
        }

        foreach (var texEnv in texEnvs)
        {
            sb.Append($"  {texEnv.Key}: ");
            var texEnvValue = texEnv.Value;
            var textureRef = texEnvValue?.m_Texture;
            var texture = texEnvValue != null ? getMaterialTextureSlot(displayMaterial, texEnv.Key) : null;

            if (texture != null && textureRef != null)
            {
                sb.AppendLine($"{texture.m_Name} ({texture.m_Width}x{texture.m_Height}, {texture.m_TextureFormat})");
                sb.AppendLine($"    FileID: {textureRef.m_FileID}, PathID: {textureRef.m_PathID}");
                sb.AppendLine($"    Scale: {texEnvValue?.m_Scale.X}, {texEnvValue?.m_Scale.Y}");
                sb.AppendLine($"    Offset: {texEnvValue?.m_Offset.X}, {texEnvValue?.m_Offset.Y}");
                if (previewTexture == null && IsPreferredTextureSlot(texEnv.Key))
                {
                    previewTexture = texture;
                }
            }
            else
            {
                sb.AppendLine(textureRef == null || textureRef.IsNull
                    ? "null"
                    : $"missing (FileID: {textureRef.m_FileID}, PathID: {textureRef.m_PathID})");
            }
        }

        AppendScalarProperties(sb, displayMaterial);

        if (previewTexture == null)
        {
            previewTexture = findTextureForMaterial(displayMaterial);
        }

        return new MaterialPreviewData(sb.ToString(), previewTexture);
    }

    private void AppendShaderReference(StringBuilder sb, Material material)
    {
        var shaderRef = material.m_Shader;
        if (shaderRef == null || shaderRef.IsNull)
        {
            sb.AppendLine("Shader: <none>");
            return;
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

        if (string.IsNullOrWhiteSpace(shaderName))
        {
            shaderName = "unloaded or unsupported shader";
        }

        sb.AppendLine($"Shader: {shaderName} (FileID: {shaderRef.m_FileID}, PathID: {shaderRef.m_PathID})");
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            sb.AppendLine($"Shader source: {sourceName}");
        }
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

    private static bool IsPreferredTextureSlot(string propertyName)
    {
        return PreferredTextureSlots.Contains(propertyName, StringComparer.OrdinalIgnoreCase);
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
