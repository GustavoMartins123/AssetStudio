using System.Collections.Generic;
using System.IO;
using System.Linq;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class CustomRenderTexturePreview
{
    private const string Centers = "CustomRenderTextureCenters";
    private const string SizesAndRotations = "CustomRenderTextureSizesAndRotations";
    private const string PrimitiveIds = "CustomRenderTexturePrimitiveIDs";

    internal static bool UsesUpdateZones(ShaderPreviewDraw draw) =>
        draw.Pass.Vertex.Buffers.SelectMany(buffer => buffer.Parameters).Any(parameter => parameter.Name == Centers);

    internal static ShaderPreviewDraw Prepare(ShaderPreviewDraw draw, int size)
    {
        var parameters = draw.Pass.Vertex.Buffers.SelectMany(buffer => buffer.Parameters)
            .ToDictionary(parameter => parameter.Name, System.StringComparer.Ordinal);
        if (parameters[Centers] is not { Type: 0, Columns: 4, ArraySize: > 0 and <= 64 } center ||
            parameters.GetValueOrDefault(SizesAndRotations) is not { Type: 0, Columns: 4 } zones ||
            parameters.GetValueOrDefault(PrimitiveIds) is not { Type: 0, Columns: 1 } ids ||
            zones.ArraySize != center.ArraySize || ids.ArraySize != center.ArraySize)
        {
            throw new InvalidDataException("The custom render texture has an unexpected update zone layout.");
        }

        // Unity's default update zone covers the whole texture in normalized coordinates.
        var centers = new NumericsVector4[center.ArraySize];
        var sizes = new NumericsVector4[center.ArraySize];
        centers[0] = new NumericsVector4(0.5f, 0.5f, 0.5f, 0);
        sizes[0] = new NumericsVector4(1, 1, 1, 0);
        return draw with
        {
            Geometry = ShaderPreviewGeometry.ScreenQuad,
            ProceduralVertexCount = 6,
            Values = new Dictionary<string, NumericsVector4>
            {
                ["CustomRenderTextureParameters"] = NumericsVector4.Zero,
                ["_CustomRenderTextureInfo"] = new(size, size, 1, 0),
                ["_TimeEditor"] = NumericsVector4.Zero
            },
            VectorArrays = new Dictionary<string, NumericsVector4[]>
            {
                [Centers] = centers,
                [SizesAndRotations] = sizes,
                [PrimitiveIds] = new NumericsVector4[center.ArraySize]
            }
        };
    }
}
