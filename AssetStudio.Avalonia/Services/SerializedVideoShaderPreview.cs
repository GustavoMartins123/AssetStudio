using System;
using System.Collections.Generic;
using System.Threading;

namespace AssetStudio.Avalonia.Services;

internal static class SerializedVideoShaderPreview
{
    internal const string Luma = "_MainTex", BlueDifference = "_SecondTex", RedDifference = "_ThirdTex";

    internal static void CreatePlanes(Dictionary<string, ShaderPreviewTexture> textures, int size)
    {
        foreach (string name in new[] { Luma, BlueDifference, RedDifference })
        {
            // Unity's planar decoder reads all three components through the alpha channel.
            textures.Add(name, new ShaderPreviewTexture(size, size, 1, 65 /* A8_UNORM */, 1, false,
                new byte[size * size], 1, 1, 1, 0));
        }
    }

    internal static void Render(IReadOnlyDictionary<string, ShaderPreviewTexture> textures, float time, CancellationToken token)
    {
        int size = textures[Luma].Width;
        var yPlane = textures[Luma].Pixels;
        var cbPlane = textures[BlueDifference].Pixels;
        var crPlane = textures[RedDifference].Pixels;
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                int bar = 7 - Math.Clamp((int)(u * 8), 0, 7);
                float level = 0.55f + 0.35f * MathF.Cos(MathF.Tau * (v - u * 0.2f - time * 0.5f));
                float r = (bar & 4) != 0 ? level : 0;
                float g = (bar & 2) != 0 ? level : 0;
                float b = (bar & 1) != 0 ? level : 0;
                int index = y * size + x;
                // Encode the test frame in BT.601 studio range for the YCbCr_To_RGB1 pass.
                yPlane[index] = (byte)MathF.Round(16 + 65.481f * r + 128.553f * g + 24.966f * b);
                cbPlane[index] = (byte)MathF.Round(128 - 37.797f * r - 74.203f * g + 112f * b);
                crPlane[index] = (byte)MathF.Round(128 + 112f * r - 93.786f * g - 18.214f * b);
            }
        }
    }
}
