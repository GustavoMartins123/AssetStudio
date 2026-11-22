using System;
using System.Collections.Generic;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class UnityChanToonPreview
{
    internal static IReadOnlySet<string> PassKeywords(ShaderPreviewReader shader, ShaderPreviewPassDefinition pass,
        IReadOnlyDictionary<string, NumericsVector4> values, IReadOnlySet<string> enabled)
    {
        const string prefix = "UnityChanToonShader/";
        if (!shader.Name.StartsWith(prefix, StringComparison.Ordinal) ||
            !shader.Name.Contains("/Toon_ShadingGradeMap", StringComparison.Ordinal))
        {
            return enabled;
        }

        // These shaders compile the clipping and Angel Ring modes into each named pass.
        // UsePass uses the shader that owns the compiled program for these settings.
        bool clipping = shader.Name.Contains("_TransClipping", StringComparison.Ordinal);
        if (pass.Name.Equals("Outline", StringComparison.OrdinalIgnoreCase))
        {
            if (!values.TryGetValue("_OUTLINE", out var outline) || outline.X is not (0f or 1f))
            {
                throw new InvalidOperationException("The toon outline requires its _OUTLINE mode property.");
            }
            var keywords = new HashSet<string>(enabled, StringComparer.Ordinal);
            keywords.Remove("_OUTLINE_NML");
            keywords.Remove("_OUTLINE_POS");
            keywords.Add(outline.X == 0 ? "_OUTLINE_NML" : "_OUTLINE_POS");
            keywords.Add(clipping ? "_IS_OUTLINE_CLIPPING_YES" : "_IS_OUTLINE_CLIPPING_NO");
            return keywords;
        }
        if (pass.LightMode.Equals("ForwardBase", StringComparison.OrdinalIgnoreCase) ||
            pass.LightMode.Equals("ForwardAdd", StringComparison.OrdinalIgnoreCase))
        {
            bool angelRing = shader.Name.StartsWith(prefix + "AngelRing/", StringComparison.Ordinal);
            return new HashSet<string>(enabled, StringComparer.Ordinal)
            {
                pass.LightMode.Equals("ForwardBase", StringComparison.OrdinalIgnoreCase) ? "_IS_PASS_FWDBASE" : "_IS_PASS_FWDDELTA",
                clipping ? "_IS_TRANSCLIPPING_ON" : "_IS_TRANSCLIPPING_OFF",
                angelRing ? "_IS_ANGELRING_ON" : "_IS_ANGELRING_OFF"
            };
        }
        return enabled;
    }
}
