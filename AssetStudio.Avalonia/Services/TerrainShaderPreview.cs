using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal sealed record TerrainPreviewMesh(NumericsVector3[] Positions, NumericsVector3[] Normals, NumericsVector2[] Uvs, uint[] Indices);

internal enum TerrainPreviewPass { Surface, Basemap, Normalmap, HeightCopy, AdditionalLayers, LayerChannel }

internal static class TerrainShaderPreview
{
    private const float MaxHeight = 32766f;
    private const int LayersPerPass = 4;
    private const string MainTexture = ImageEffectPreview.MainTexture;

    internal static TerrainPreviewPass? Find(string name) => name switch
    {
        "Nature/Terrain/Diffuse" or "Nature/Terrain/Standard" => TerrainPreviewPass.Surface,
        "Hidden/TerrainEngine/Splatmap/Diffuse-BaseGen" or "Hidden/TerrainEngine/Splatmap/Standard-BaseGen" => TerrainPreviewPass.Basemap,
        "Hidden/TerrainEngine/Splatmap/Diffuse-AddPass" or "Hidden/TerrainEngine/Splatmap/Standard-AddPass" => TerrainPreviewPass.AdditionalLayers,
        "Hidden/TerrainEngine/GenerateNormalmap" => TerrainPreviewPass.Normalmap,
        "Hidden/TerrainEngine/HeightBlitCopy" => TerrainPreviewPass.HeightCopy,
        "Hidden/TerrainEngine/TerrainLayerUtils" => TerrainPreviewPass.LayerChannel,
        _ => null
    };

    internal static TerrainPreviewMesh? Prepare(Shader shader, TerrainPreviewPass pass,
        IDictionary<string, NumericsVector4> values, IDictionary<string, ShaderPreviewTexture> textures,
        Func<AssetHandle, AssetStudio.Object?> resolveAsset, Func<SerializedFile, PPtr<AssetStudio.Object>, AssetStudio.Object> resolveObject,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture, CancellationToken token,
        out IReadOnlyDictionary<string, NumericsVector4[]>? vectorArrays)
    {
        vectorArrays = null;
        var arena = shader.assetsFile.assetsManager.ProjectIndex.Arena;
        AssetHandle? handle = null;
        for (int i = 0; i < arena.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (arena.GetType(i) != ClassIDType.TerrainData)
            {
                continue;
            }
            if (handle != null)
            {
                throw new NotSupportedException("The terrain shader preview requires one TerrainData asset in the current project.");
            }
            handle = arena.MaterializeHandle(i);
        }
        if (handle == null || resolveAsset(handle) is not { type: ClassIDType.TerrainData, version: [2019, 3, ..] } asset)
        {
            throw new NotSupportedException("The terrain shader preview requires a readable Unity 2019.3 TerrainData asset.");
        }
        var file = asset.assetsFile;
        var info = file.ObjectInfoDic[asset.m_PathID];
        var nodes = ReadNodes(info, ClassIDType.TerrainData);
        var next = TypeTreeHelper.GetNextSiblingTable(nodes);
        using var source = file.reader.Clone();
        var reader = new ObjectReader(source, file, info);
        reader.Reset();

        int splats = SeekField(reader, nodes, next, 0, 1, "m_SplatDatabase");
        int layersField = SeekField(reader, nodes, next, splats, splats + 1, "m_TerrainLayers");
        var layers = ReadReferences<AssetStudio.Object>(reader);
        _ = SeekField(reader, nodes, next, splats, next[layersField], "m_AlphaTextures");
        var maps = ReadReferences<Texture>(reader);

        int layerStart = pass == TerrainPreviewPass.AdditionalLayers ? LayersPerPass : 0;
        int layerCount = Math.Min(LayersPerPass, layers.Length - layerStart);
        bool heightBlit = pass is TerrainPreviewPass.Normalmap or TerrainPreviewPass.HeightCopy;
        if (!heightBlit && (layerCount <= 0 || maps.Length <= layerStart / LayersPerPass))
        {
            throw new NotSupportedException($"The terrain has no layer group for its {pass} pass.");
        }
        if (pass == TerrainPreviewPass.LayerChannel)
        {
            // TerrainPaintUtility selects the first serialized layer from channel R of the first weight map.
            values["_LayerMask"] = new(1, 0, 0, 0);
            textures.Add(MainTexture, ShaderPreviewService.ReadTexture(resolveTexture(file, maps[0]),
                ShaderPreviewTextureDimension.Texture2D));
            return null;
        }

        // Seek from the root because the remainder of the splat database may contain version-specific fields.
        reader.Reset();
        int heightmap = SeekField(reader, nodes, next, 0, 1, "m_Heightmap");
        int heightsField = SeekField(reader, nodes, next, heightmap, heightmap + 1, "m_Heights");
        int sampleCount = reader.ReadInt32();
        if (sampleCount is < 4 or > 4097 * 4097 || sampleCount * sizeof(short) > reader.byteStart + reader.byteSize - reader.Position)
        {
            throw new InvalidDataException("The terrain has an invalid height array.");
        }
        bool mesh = pass is TerrainPreviewPass.Surface or TerrainPreviewPass.AdditionalLayers;
        short[]? heights = mesh || heightBlit ? reader.ReadInt16Array(sampleCount) : null;
        if (heights == null)
        {
            reader.Position += sampleCount * sizeof(short);
        }
        reader.AlignStream();
        int holesField = SeekField(reader, nodes, next, heightmap, next[heightsField], "m_Holes");
        int holes = reader.ReadInt32();
        if (holes != 0)
        {
            throw new NotSupportedException("The terrain shader preview needs the terrain's hole mask.");
        }
        reader.AlignStream();
        int resolutionField = SeekField(reader, nodes, next, heightmap, next[holesField], "m_Resolution");
        int resolution = reader.ReadInt32();
        _ = SeekField(reader, nodes, next, heightmap, next[resolutionField], "m_Scale");
        var scale = new NumericsVector3(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
        if (resolution is < 2 or > 4097 || sampleCount != resolution * resolution ||
            scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
        {
            throw new InvalidDataException("The terrain heightmap resolution or scale is invalid.");
        }

        if (heightBlit)
        {
            if (pass == TerrainPreviewPass.Normalmap)
            {
                var offsets = new NumericsVector4[9];
                // The preview uses one tile. Edge taps sample the nearest texel in that tile.
                Array.Fill(offsets, new NumericsVector4((65535f / MaxHeight) * scale.Y, 0, 0, 0));
                vectorArrays = new Dictionary<string, NumericsVector4[]> { ["_TerrainTilesScaleOffsets"] = offsets };
                values["_TerrainNormalmapGenSize"] = new(1f / resolution, 1f / resolution, 1f / scale.X, 1f / scale.Z);
            }
            else
            {
                // The paint context and the source tile share the same elevation range.
                values["_Height_Scale"] = NumericsVector4.One;
                values["_Height_Offset"] = NumericsVector4.Zero;
            }
            textures.Add(MainTexture, new ShaderPreviewTexture(resolution, resolution, 1, 56 /* R16_UNORM */, sizeof(short),
                false, MemoryMarshal.AsBytes(heights!.AsSpan()).ToArray(), 0, 1, 1, 0));
            return null;
        }

        float width = (resolution - 1) * scale.X, length = (resolution - 1) * scale.Z;
        textures.Add("_Control", ShaderPreviewService.ReadTexture(resolveTexture(file, maps[layerStart / LayersPerPass]),
            ShaderPreviewTextureDimension.Texture2D));
        values["_Control_ST"] = new(1, 1, 0, 0);
        int normalMaps = 0;
        for (int i = 0; i < layerCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var layer = resolveObject(file, layers[layerStart + i]);
            var layerFile = layer.assetsFile;
            var layerInfo = layerFile.ObjectInfoDic[layer.m_PathID];
            var layerNodes = ReadNodes(layerInfo, ClassIDType.TerrainLayer);
            var layerNext = TypeTreeHelper.GetNextSiblingTable(layerNodes);
            using var layerStream = layerFile.reader.Clone();
            var layerReader = new ObjectReader(layerStream, layerFile, layerInfo);
            layerReader.Reset();
            int diffuseField = SeekField(layerReader, layerNodes, layerNext, 0, 1, "m_DiffuseTexture");
            var diffuse = new PPtr<Texture>(layerReader);
            int normalField = SeekField(layerReader, layerNodes, layerNext, 0, layerNext[diffuseField], "m_NormalMapTexture");
            var normal = new PPtr<Texture>(layerReader);
            int tileSizeField = SeekField(layerReader, layerNodes, layerNext, 0, layerNext[normalField], "m_TileSize");
            float tileWidth = ReadFinite(layerReader), tileLength = ReadFinite(layerReader);
            int tileOffsetField = SeekField(layerReader, layerNodes, layerNext, 0, layerNext[tileSizeField], "m_TileOffset");
            float offsetX = ReadFinite(layerReader), offsetY = ReadFinite(layerReader);
            if (tileWidth <= 0 || tileLength <= 0)
            {
                throw new InvalidDataException("A terrain layer has a non-positive tile size.");
            }
            int metallicField = SeekField(layerReader, layerNodes, layerNext, 0, layerNext[tileOffsetField], "m_Metallic");
            values[$"_Metallic{i}"] = new(ReadFinite(layerReader));
            int smoothnessField = SeekField(layerReader, layerNodes, layerNext, 0, layerNext[metallicField], "m_Smoothness");
            values[$"_Smoothness{i}"] = new(ReadFinite(layerReader));
            values[$"_Splat{i}_ST"] = new(width / tileWidth, length / tileLength, offsetX / tileWidth, offsetY / tileLength);
            textures.Add($"_Splat{i}", ShaderPreviewService.ReadTexture(resolveTexture(layerFile, diffuse),
                ShaderPreviewTextureDimension.Texture2D));
            if (mesh && !normal.IsNull)
            {
                _ = SeekField(layerReader, layerNodes, layerNext, 0, layerNext[smoothnessField], "m_NormalScale");
                values[$"_NormalScale{i}"] = new(ReadFinite(layerReader));
                textures.Add($"_Normal{i}", ShaderPreviewService.ReadTexture(resolveTexture(layerFile, normal),
                    ShaderPreviewTextureDimension.Texture2D));
                normalMaps++;
            }
        }
        if (normalMaps != 0 && normalMaps != layerCount)
        {
            throw new NotSupportedException("The terrain shader preview needs normal maps for every active layer of this texture group.");
        }
        // Shader variants always bind a group of four slots. Absent layers have zero control-map weight.
        var inactiveLayer = new ShaderPreviewTexture(1, 1, 1, D3D11PreviewDevice.Rgba8, 4, false, new byte[4], 1, 0, 1, 0);
        for (int i = layerCount; i < LayersPerPass; i++)
        {
            values[$"_Metallic{i}"] = NumericsVector4.Zero;
            values[$"_Smoothness{i}"] = NumericsVector4.Zero;
            values[$"_Splat{i}_ST"] = new(1, 1, 0, 0);
            textures.Add($"_Splat{i}", inactiveLayer);
            if (normalMaps > 0)
            {
                values[$"_NormalScale{i}"] = NumericsVector4.Zero;
                textures.Add($"_Normal{i}", inactiveLayer);
            }
        }
        return mesh ? CreateMesh(heights!, resolution, scale, token) : null;
    }

    private static TerrainPreviewMesh CreateMesh(short[] heights, int resolution, NumericsVector3 scale, CancellationToken token)
    {
        const int previewSegments = 128;
        int cells = Math.Min(resolution - 1, previewSegments);
        int stride = cells + 1;
        var positions = new NumericsVector3[stride * stride];
        var normals = new NumericsVector3[positions.Length];
        var uvs = new NumericsVector2[positions.Length];
        var indices = new uint[cells * cells * 6];
        float width = (resolution - 1) * scale.X, depth = (resolution - 1) * scale.Z;
        float normalize = 2f / MathF.Max(width, depth);
        float heightScale = scale.Y / MaxHeight;
        ushort min = ushort.MaxValue, max = 0;
        foreach (short value in heights)
        {
            ushort sample = (ushort)value;
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
        }
        float center = (min + max) * 0.5f * heightScale;
        for (int y = 0; y <= cells; y++)
        {
            token.ThrowIfCancellationRequested();
            int row = y * (resolution - 1) / cells;
            float v = (float)row / (resolution - 1);
            for (int x = 0; x <= cells; x++)
            {
                int column = x * (resolution - 1) / cells;
                float u = (float)column / (resolution - 1);
                int vertex = y * stride + x;
                positions[vertex] = new NumericsVector3((u - 0.5f) * width, Height(column, row) - center, (v - 0.5f) * depth) * normalize;
                uvs[vertex] = new NumericsVector2(u, v);
                int left = Math.Max(0, column - 1), right = Math.Min(resolution - 1, column + 1);
                int down = Math.Max(0, row - 1), up = Math.Min(resolution - 1, row + 1);
                normals[vertex] = NumericsVector3.Normalize(new NumericsVector3(
                    (Height(left, row) - Height(right, row)) / ((right - left) * scale.X),
                    1, (Height(column, down) - Height(column, up)) / ((up - down) * scale.Z)));
                if (x < cells && y < cells)
                {
                    int index = (y * cells + x) * 6;
                    indices[index] = (uint)vertex;
                    indices[index + 1] = (uint)(vertex + stride);
                    indices[index + 2] = (uint)(vertex + 1);
                    indices[index + 3] = (uint)(vertex + 1);
                    indices[index + 4] = (uint)(vertex + stride);
                    indices[index + 5] = (uint)(vertex + stride + 1);
                }
            }
        }
        return new TerrainPreviewMesh(positions, normals, uvs, indices);

        float Height(int x, int y) => (ushort)heights[y * resolution + x] * heightScale;
    }

    private static List<TypeTreeNode> ReadNodes(ObjectInfo info, ClassIDType type) =>
        info.classID == (int)type && info.serializedType?.m_Type?.m_Nodes is { Count: > 1 } nodes ? nodes :
            throw new NotSupportedException($"The {type} asset has no serialized field layout.");

    private static int SeekField(ObjectReader reader, List<TypeTreeNode> nodes, int[] next, int parent, int start, string name)
    {
        for (int i = start; i < next[parent]; i++)
        {
            if (nodes[i].m_Name == name)
            {
                return i;
            }
            TypeTreeHelper.SkipNode(nodes, next, reader, ref i);
        }
        throw new InvalidDataException($"The terrain data has no field '{name}'.");
    }

    private static PPtr<T>[] ReadReferences<T>(ObjectReader reader) where T : AssetStudio.Object
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 256 || count * 12L > reader.byteStart + reader.byteSize - reader.Position)
        {
            throw new InvalidDataException("A terrain reference array has an invalid length.");
        }
        var pointers = new PPtr<T>[count];
        for (int i = 0; i < count; i++)
        {
            pointers[i] = new PPtr<T>(reader);
        }
        reader.AlignStream();
        return pointers;
    }

    private static float ReadFinite(ObjectReader reader)
    {
        float value = reader.ReadSingle();
        return float.IsFinite(value) ? value : throw new InvalidDataException("The terrain has a non-finite setting.");
    }
}
