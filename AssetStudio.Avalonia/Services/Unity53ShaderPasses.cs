using System;
using System.Linq;

namespace AssetStudio.Avalonia.Services;

internal static class Unity53ShaderPasses
{
    internal static string? ShadowFilter(string name) => name == "Hidden/Internal-PrePassCollectShadows" ? "PCF_5x5" : null;

    internal static string TextureTransform(string shaderName, string textureName) =>
        textureName == "_DissolveSrc" && shaderName.StartsWith("Dissolve/", StringComparison.Ordinal) ? "DissolveSrc_ST" : textureName + "_ST";

    internal static string UsePassShaderName(string name)
    {
        // Unity 5.3 serializes some built-in dependencies under this namespace while preserving the source UsePass name.
        const string prefix = "Legacy Shaders/";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    internal static ShaderPreviewPassDefinition ReadLightMode(LegacyShaderPreview reader, ShaderPreviewPassDefinition pass)
    {
        if (!pass.LightMode.Equals("ShadowCaster", StringComparison.OrdinalIgnoreCase) || !pass.HasDirect3D11 ||
            !pass.Name.Equals("FORWARD", StringComparison.OrdinalIgnoreCase))
        {
            return pass;
        }

        // Some Unity 5.3 assets tag compiled Forward programs as ShadowCaster.
        var variants = reader.GetVariantKeywords(pass, vertex: true).ToArray();
        if (variants.Any(keywords => keywords.Contains("DIRECTIONAL") && keywords.Contains("LIGHTMAP_OFF")) &&
            reader.GetVariantKeywords(pass, vertex: false).Any(keywords => keywords.Contains("DIRECTIONAL") && keywords.Contains("LIGHTMAP_OFF")))
        {
            return pass with { LightMode = "ForwardBase" };
        }
        if (variants.Any(keywords => keywords.Contains("DIRECTIONAL")) && variants.Any(keywords => keywords.Contains("POINT")) &&
            reader.GetVariantKeywords(pass, vertex: false).Any(keywords => keywords.Contains("DIRECTIONAL")))
        {
            return pass with { LightMode = "ForwardAdd" };
        }
        return pass;
    }
}
