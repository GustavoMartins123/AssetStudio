using System;
using System.Collections.Generic;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class SerializedGuiShaderPreview
{
    internal static bool IsGuiDraw(string shaderName) => shaderName is "Hidden/Internal-GUITextureClip" or
        "Hidden/Internal-GUITextureClipText" or "Hidden/Internal-GUITextureBlit" or "Hidden/Internal-GUIRoundedRect";

    internal static void SetProperties(IDictionary<string, NumericsVector4> values)
    {
        // The preview's GUI input and render target both use gamma-encoded colors.
        values.TryAdd("_ManualTex2SRGB", NumericsVector4.Zero);
    }

    internal static readonly IReadOnlyDictionary<string, NumericsVector4[]> RoundedRectArrays =
        new Dictionary<string, NumericsVector4[]>(StringComparer.Ordinal)
        {
            ["_CornerRadiuses"] = [new(0.24f), new(0.24f), new(0.24f), new(0.24f)],
            ["_BorderWidths"] = [new(0.035f), new(0.035f), new(0.035f), new(0.035f)],
            ["_Rect"] = [new(-1), new(-1), new(2), new(2)]
        };
}
