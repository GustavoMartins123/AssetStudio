using System;
using System.Collections.Generic;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class TemporalAntialiasingPreview
{
    internal const string HistoryTexture = "_HistoryTex";

    internal static void SetProperties(IDictionary<string, NumericsVector4> values)
    {
        values.TryAdd("_SharpenParameters", new NumericsVector4(0.3f, 0, 0, 0));
        values.TryAdd("_FinalBlendParameters", new NumericsVector4(0.95f, 0.85f, 6000, 0));
    }

    internal static NumericsVector2 Jitter(float time, int size)
    {
        // The package cycles eight Halton (2, 3) samples, scaled from pixels into texture UVs.
        int index = (int)MathF.Round(time * ShaderPreviewService.FramesPerSecond) & 7;
        return new NumericsVector2(Halton(index, 2), Halton(index, 3)) * (0.75f / size);

        static float Halton(int index, int radix)
        {
            float value = 0, scale = 1f / radix;
            for (; index > 0; index /= radix, scale /= radix)
            {
                value += (index % radix) * scale;
            }
            return value;
        }
    }
}
