using System;
using System.Collections.Generic;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector4 = System.Numerics.Vector4;
using NumericsVector2 = System.Numerics.Vector2;

namespace AssetStudio.Avalonia.Services;

internal static class TextMeshProShaderPreview
{
    internal static bool IsTextShader(string shaderName) => shaderName.StartsWith("TextMeshPro/", StringComparison.Ordinal);

    internal static void SetDrawProperties(IDictionary<string, NumericsVector4> values)
    {
        // The text fixture is a double-sided glyph plane. TextMeshPro owns this renderer property.
        values.TryAdd("_CullMode", NumericsVector4.Zero);
        values.TryAdd("_EnvMatrixRotation", NumericsVector4.Zero);
        // The text fixture holds the face and outline UVs still unless the text material animates them.
        values.TryAdd("_FaceUVSpeedX", NumericsVector4.Zero);
        values.TryAdd("_FaceUVSpeedY", NumericsVector4.Zero);
        values.TryAdd("_OutlineUVSpeedX", NumericsVector4.Zero);
        values.TryAdd("_OutlineUVSpeedY", NumericsVector4.Zero);
    }

    internal static ShaderPreviewTexture CreateGlyphAtlas(IReadOnlyDictionary<string, NumericsVector4> values)
    {
        const int size = 512;
        bool signedDistance = values.TryGetValue("_GradientScale", out var scale);
        if (signedDistance && (!float.IsFinite(scale.X) || scale.X <= 0))
        {
            throw new NotSupportedException("The text preview requires a positive glyph gradient scale.");
        }
        var left = new NumericsVector2(122, 399);
        var top = new NumericsVector2(256, 103);
        var right = new NumericsVector2(390, 399);
        var barLeft = new NumericsVector2(178, 294);
        var barRight = new NumericsVector2(334, 294);
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var point = new NumericsVector2(x + 0.5f, y + 0.5f);
                float distance = MathF.Min(MathF.Min(Distance(point, left, top), Distance(point, top, right)), Distance(point, barLeft, barRight));
                float coverage = signedDistance ? 0.5f + (25 - distance) / scale.X : 25.5f - distance;
                int offset = (y * size + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                pixels[offset + 3] = (byte)MathF.Round(255 * Math.Clamp(coverage, 0, 1));
            }
        }
        return new ShaderPreviewTexture(size, size, 1, 28, 4, false, pixels, 1, 1, 1, 0);

        static float Distance(NumericsVector2 point, NumericsVector2 start, NumericsVector2 end)
        {
            var line = end - start;
            return (point - start - line * Math.Clamp(NumericsVector2.Dot(point - start, line) / line.LengthSquared(), 0, 1)).Length();
        }
    }

    internal static NumericsMatrix EnvironmentMatrix(NumericsVector4 rotation)
    {
        float degreesToRadians = MathF.PI / 180;
        return NumericsMatrix.CreateRotationZ(rotation.Z * degreesToRadians) * NumericsMatrix.CreateRotationX(rotation.X * degreesToRadians) *
            NumericsMatrix.CreateRotationY(rotation.Y * degreesToRadians);
    }
}
