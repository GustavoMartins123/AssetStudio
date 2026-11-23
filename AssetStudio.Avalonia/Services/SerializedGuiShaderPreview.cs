using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class SerializedGuiShaderPreview
{
    internal const string UiElements = "Hidden/Internal-UIRDefault";

    internal static bool IsGuiDraw(string shaderName) => IsRoundedRect(shaderName) || shaderName is
        "Hidden/Internal-GUITextureClip" or "Hidden/Internal-GUITextureClipText" or "Hidden/Internal-GUITextureBlit";

    internal static bool IsRoundedRect(string shaderName) => shaderName is
        "Hidden/Internal-GUIRoundedRect" or "Hidden/Internal-GUIRoundedRectWithColorPerBorder";

    internal static void SetProperties(string shaderName, IDictionary<string, NumericsVector4> values)
    {
        // The preview's GUI input and render target both use gamma-encoded colors.
        values.TryAdd("_ManualTex2SRGB", NumericsVector4.Zero);
        if (IsRoundedRect(shaderName))
        {
            values.TryAdd("_SmoothCorners", NumericsVector4.One);
        }
    }

    internal static void PrepareUiElements(IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, int size)
    {
        values.TryAdd("_1PixelClipInvView", new(2f / size, 2f / size, size / 2f, size / 2f));
        values.TryAdd("_PixelClipRect", new(0, 0, size, size));

        // UIElements 2019.3 reserves three entries for the default transform, clip and opacity.
        const int atlasSize = 64;
        var info = new NumericsVector4[atlasSize * atlasSize];
        info[0] = NumericsVector4.UnitX;
        info[atlasSize] = NumericsVector4.UnitY;
        info[2 * atlasSize] = NumericsVector4.UnitZ;
        info[32 * atlasSize] = new(-float.MaxValue, -float.MaxValue, float.MaxValue, float.MaxValue);
        info[32 * atlasSize + 32] = NumericsVector4.One;
        textures.Add("_ShaderInfoTex", new ShaderPreviewTexture(atlasSize, atlasSize, 1, 2 /* R32G32B32A32_FLOAT */, 16,
            false, MemoryMarshal.AsBytes(info.AsSpan()).ToArray(), 0, 1, 1, 0));
        // The solid geometry uses no gradient entries. Bind the empty atlas row used by the renderer.
        textures.Add("_GradientSettingsTex", new ShaderPreviewTexture(3, 1, 1, D3D11PreviewDevice.Rgba8, 4,
            false, [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255], 0, 1, 1, 0));
    }

    internal static readonly IReadOnlyDictionary<string, NumericsVector4[]> RoundedRectArrays =
        new Dictionary<string, NumericsVector4[]>(StringComparer.Ordinal)
        {
            ["_CornerRadiuses"] = [new(0.24f), new(0.24f), new(0.24f), new(0.24f)],
            ["_BorderWidths"] = [new(0.035f), new(0.035f), new(0.035f), new(0.035f)],
            ["_Rect"] = [new(-1), new(-1), new(2), new(2)],
            ["_BorderColors"] = [NumericsVector4.One, NumericsVector4.One, NumericsVector4.One, NumericsVector4.One]
        };
}
