using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class ExposureHistogramPreview
{
    internal const string BufferName = "_Histogram";
    internal const int BinCount = 64;
    private const float LogMin = -8, LogMax = 4;

    internal static void SetProperties(IDictionary<string, NumericsVector4> values, int size)
    {
        const float scale = 1 / (LogMax - LogMin);
        values.TryAdd("_Params", new NumericsVector4(0.45f, 0.95f, 1f / 32, 2));
        values.TryAdd("_ScaleOffsetRes", new NumericsVector4(scale, -LogMin * scale, size / 2, size / 2));
        values.TryAdd("_DebugWidth", new NumericsVector4(size));
    }

    internal static void Count(ShaderPreviewTexture color, Span<uint> bins, CancellationToken token)
    {
        bins.Clear();
        var pixels = MemoryMarshal.Cast<byte, Half>(color.Pixels.AsSpan());
        // Unity measures luminance after a half-resolution blit of its HDR source.
        int pitch = color.Width * 4;
        for (int y = 0; y < color.Height - 1; y += 2)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < color.Width - 1; x += 2)
            {
                int offset = y * pitch + x * 4;
                float luminance = (Luminance(pixels[offset..]) + Luminance(pixels[(offset + 4)..]) +
                    Luminance(pixels[(offset + pitch)..]) + Luminance(pixels[(offset + pitch + 4)..])) * 0.25f;
                float logLuminance = MathF.Log2(MathF.Max(MathF.Pow(2, LogMin), luminance));
                int bin = Math.Clamp((int)((logLuminance - LogMin) * BinCount / (LogMax - LogMin)), 0, BinCount - 1);
                bins[bin]++;
            }
        }

        static float Luminance(ReadOnlySpan<Half> pixel) => 0.2126f * (float)pixel[0] + 0.7152f * (float)pixel[1] + 0.0722f * (float)pixel[2];
    }
}
