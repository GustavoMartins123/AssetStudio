using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio
{
    public sealed class TerrainConversionOptions
    {
        public int MaxGridResolution { get; init; } = TerrainMeshConverter.DefaultMaxGridResolution;
        public int MaxBakedTextureSize { get; init; } = TerrainMeshConverter.DefaultMaxBakedTextureSize;
        public ImageFormat ImageFormat { get; init; } = ImageFormat.Png;
        public bool CompositeSplatmap { get; init; } = true;
    }

    public sealed class TerrainMeshData
    {
        public required Vector3[] Positions { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Vector2[] UVs { get; init; }
        public required int[] Indices { get; init; }
        public int GridWidth { get; init; }
        public int GridHeight { get; init; }
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        public Vector3 TerrainSize { get; init; }
        public int SkippedHoleCellCount { get; init; }
        public byte[]? BakedAlbedoBytes { get; init; }
        public int BakedAlbedoWidth { get; init; }
        public int BakedAlbedoHeight { get; init; }
        public int CompositedLayerCount { get; init; }
    }

    public sealed class TerrainConversionResult
    {
        public required ImportedMesh Mesh { get; init; }
        public required ImportedMaterial Material { get; init; }
        public ImportedTexture? Texture { get; init; }
        public required TerrainMeshData MeshData { get; init; }
    }

    /// <summary>
    /// Converts Unity <see cref="Terrain"/> / <see cref="TerrainData"/> heightmaps, hole masks, and
    /// multi-layer splatmaps into bounded indexed triangle geometry and optional baked albedo textures.
    /// </summary>
    public static class TerrainMeshConverter
    {
        public const int DefaultMaxGridResolution = 257;
        public const int MinGridResolution = 2;
        public const int MaxAllowedGridResolution = 513;
        public const int DefaultMaxBakedTextureSize = 1024;
        private const int MaxSplatLayers = 16;
        private const int MaxAlphaTextures = 4;

        private sealed class DecodedSplatLayer
        {
            public required byte[] BgraPixels { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
            public required Vector2 TileSize { get; init; }
            public required Vector2 TileOffset { get; init; }
        }

        private sealed class DecodedAlphaMap
        {
            public required byte[] BgraPixels { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
        }

        /// <summary>
        /// Builds Unity left-handed local-space terrain geometry, analytical normals, UVs, hole-filtered
        /// triangle indices, and optional composited splatmap albedo image bytes.
        /// </summary>
        public static TerrainMeshData? BuildTerrainMeshData(
            TerrainData data,
            TerrainConversionOptions? options = null,
            Func<PPtr<Texture2D>, Texture2D?>? textureResolver = null,
            Func<PPtr<TerrainLayer>, TerrainLayer?>? layerResolver = null)
        {
            if (data == null || !data.HasValidHeightmap)
            {
                return null;
            }

            var opts = options ?? new TerrainConversionOptions();
            var hm = data.m_Heightmap;
            int srcW = hm.EffectiveWidth;
            int srcH = hm.EffectiveHeight;
            var rawHeights = hm.m_Heights;
            var scale = hm.m_Scale;

            int maxGridRes = Math.Clamp(opts.MaxGridResolution, MinGridResolution, MaxAllowedGridResolution);
            int gridW = Math.Clamp(srcW, MinGridResolution, maxGridRes);
            int gridH = Math.Clamp(srcH, MinGridResolution, maxGridRes);

            float terrainSizeX = (srcW - 1) * scale.X;
            float terrainSizeY = scale.Y;
            float terrainSizeZ = (srcH - 1) * scale.Z;
            var terrainSize = new Vector3(terrainSizeX, terrainSizeY, terrainSizeZ);

            // Unity stores normalized heights in [0..32766] (0x7FFE); tolerate 32767 if present.
            int expectedSampleCount = srcW * srcH;
            short maxRaw = 0;
            for (int i = 0; i < expectedSampleCount; i++)
            {
                if (rawHeights[i] > maxRaw)
                {
                    maxRaw = rawHeights[i];
                }
            }

            float heightDenom = maxRaw > 32766 ? 32767f : 32766f;
            float invDenomTimesScaleY = terrainSizeY / heightDenom;

            int vertCount = gridW * gridH;
            var gridHeights = new float[vertCount];
            var positions = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            float invGridWm1 = 1f / (gridW - 1);
            float invGridHm1 = 1f / (gridH - 1);
            float cellSizeX = terrainSizeX * invGridWm1;
            float cellSizeZ = terrainSizeZ * invGridHm1;
            bool exactResolution = gridW == srcW && gridH == srcH;

            for (int gz = 0; gz < gridH; gz++)
            {
                float v = gz * invGridHm1;
                float worldZ = gz * cellSizeZ;
                float srcZf = v * (srcH - 1);
                int sz0 = (int)srcZf;
                int sz1 = Math.Min(srcH - 1, sz0 + 1);
                float tz = srcZf - sz0;

                int rowOffset = gz * gridW;
                int srcRow0 = sz0 * srcW;
                int srcRow1 = sz1 * srcW;

                for (int gx = 0; gx < gridW; gx++)
                {
                    float u = gx * invGridWm1;
                    float worldX = gx * cellSizeX;
                    float y;

                    if (exactResolution)
                    {
                        short raw = rawHeights[gz * srcW + gx];
                        y = Math.Clamp((int)raw, 0, 32767) * invDenomTimesScaleY;
                    }
                    else
                    {
                        float srcXf = u * (srcW - 1);
                        int sx0 = (int)srcXf;
                        int sx1 = Math.Min(srcW - 1, sx0 + 1);
                        float tx = srcXf - sx0;

                        float h00 = Math.Clamp((int)rawHeights[srcRow0 + sx0], 0, 32767);
                        float h10 = Math.Clamp((int)rawHeights[srcRow0 + sx1], 0, 32767);
                        float h01 = Math.Clamp((int)rawHeights[srcRow1 + sx0], 0, 32767);
                        float h11 = Math.Clamp((int)rawHeights[srcRow1 + sx1], 0, 32767);

                        float h0 = h00 + (h10 - h00) * tx;
                        float h1 = h01 + (h11 - h01) * tx;
                        y = (h0 + (h1 - h0) * tz) * invDenomTimesScaleY;
                    }

                    int idx = rowOffset + gx;
                    gridHeights[idx] = y;
                    positions[idx] = new Vector3(worldX, y, worldZ);
                    uvs[idx] = new Vector2(u, v);
                }
            }

            // Compute smooth analytical vertex normals from finite height differences (dh/dx, dh/dz).
            for (int gz = 0; gz < gridH; gz++)
            {
                int zPrev = Math.Max(0, gz - 1);
                int zNext = Math.Min(gridH - 1, gz + 1);
                float dz = (zNext - zPrev) * cellSizeZ;
                float invDz = dz > 1e-6f ? 1f / dz : 0f;

                int rowOffset = gz * gridW;
                int rowPrev = zPrev * gridW;
                int rowNext = zNext * gridW;

                for (int gx = 0; gx < gridW; gx++)
                {
                    int xPrev = Math.Max(0, gx - 1);
                    int xNext = Math.Min(gridW - 1, gx + 1);
                    float dx = (xNext - xPrev) * cellSizeX;
                    float invDx = dx > 1e-6f ? 1f / dx : 0f;

                    float dhdx = (gridHeights[rowOffset + xNext] - gridHeights[rowOffset + xPrev]) * invDx;
                    float dhdz = (gridHeights[rowNext + gx] - gridHeights[rowPrev + gx]) * invDz;

                    float nx = -dhdx;
                    float ny = 1f;
                    float nz = -dhdz;
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-6f)
                    {
                        float invLen = 1f / len;
                        normals[rowOffset + gx] = new Vector3(nx * invLen, ny * invLen, nz * invLen);
                    }
                    else
                    {
                        normals[rowOffset + gx] = new Vector3(0f, 1f, 0f);
                    }
                }
            }

            // Build Unity CW front-facing triangles (respecting TerrainData.m_Holes when present).
            int cellsX = gridW - 1;
            int cellsZ = gridH - 1;
            int holeW = srcW - 1;
            int holeH = srcH - 1;
            var holes = hm.m_Holes;
            bool hasHoles = holes != null && holeW > 0 && holeH > 0 && holes.Length >= holeW * holeH;

            var indicesList = new List<int>(cellsX * cellsZ * 6);
            int skippedHoles = 0;

            for (int cz = 0; cz < cellsZ; cz++)
            {
                int hz = hasHoles
                    ? Math.Clamp((int)(((cz + 0.5f) / cellsZ) * holeH), 0, holeH - 1)
                    : 0;

                int row0 = cz * gridW;
                int row1 = (cz + 1) * gridW;

                for (int cx = 0; cx < cellsX; cx++)
                {
                    if (hasHoles)
                    {
                        int hx = Math.Clamp((int)(((cx + 0.5f) / cellsX) * holeW), 0, holeW - 1);
                        if (holes![hz * holeW + hx] == 0)
                        {
                            skippedHoles++;
                            continue;
                        }
                    }

                    int i00 = row0 + cx;
                    int i10 = row0 + cx + 1;
                    int i01 = row1 + cx;
                    int i11 = row1 + cx + 1;

                    // Unity left-handed CW winding facing +Y:
                    indicesList.Add(i00);
                    indicesList.Add(i01);
                    indicesList.Add(i11);

                    indicesList.Add(i00);
                    indicesList.Add(i11);
                    indicesList.Add(i10);
                }
            }

            byte[]? bakedAlbedoBytes = null;
            int bakedW = 0;
            int bakedH = 0;
            int compositedLayers = 0;

            if (opts.CompositeSplatmap)
            {
                bakedAlbedoBytes = TryCompositeSplatmapTexture(
                    data,
                    terrainSize,
                    Math.Clamp(opts.MaxBakedTextureSize, 2, 4096),
                    opts.ImageFormat,
                    textureResolver,
                    layerResolver,
                    out bakedW,
                    out bakedH,
                    out compositedLayers);
            }

            return new TerrainMeshData
            {
                Positions = positions,
                Normals = normals,
                UVs = uvs,
                Indices = indicesList.ToArray(),
                GridWidth = gridW,
                GridHeight = gridH,
                SourceWidth = srcW,
                SourceHeight = srcH,
                TerrainSize = terrainSize,
                SkippedHoleCellCount = skippedHoles,
                BakedAlbedoBytes = bakedAlbedoBytes,
                BakedAlbedoWidth = bakedW,
                BakedAlbedoHeight = bakedH,
                CompositedLayerCount = compositedLayers
            };
        }

        /// <summary>
        /// Converts a Unity <see cref="Terrain"/> and its <see cref="TerrainData"/> into
        /// <see cref="ImportedMesh"/>, <see cref="ImportedMaterial"/>, and optional baked
        /// <see cref="ImportedTexture"/> following <see cref="ModelConverter"/>'s exact right-handed
        /// X-mirrored coordinate &amp; winding convention for <see cref="GltfExporter"/>.
        /// </summary>
        public static TerrainConversionResult? ConvertToImported(
            Terrain? terrain,
            TerrainData data,
            ImportedFrame? targetFrame,
            string? baseName = null,
            TerrainConversionOptions? options = null,
            Func<PPtr<Texture2D>, Texture2D?>? textureResolver = null,
            Func<PPtr<TerrainLayer>, TerrainLayer?>? layerResolver = null)
        {
            var opts = options ?? new TerrainConversionOptions();
            var meshData = BuildTerrainMeshData(data, opts, textureResolver, layerResolver);
            if (meshData == null || meshData.Positions.Length == 0 || meshData.Indices.Length < 3)
            {
                return null;
            }

            var safeBaseName = !string.IsNullOrWhiteSpace(baseName)
                ? baseName.Trim()
                : (!string.IsNullOrWhiteSpace(data.m_Name) ? data.m_Name.Trim() : $"Terrain_{data.m_PathID}");
            var meshName = $"{safeBaseName}_TerrainMesh";
            var materialName = $"{safeBaseName}_TerrainMat";
            var textureName = $"{safeBaseName}_TerrainAlbedo{opts.ImageFormat.GetFileExtension()}";

            var vertices = new List<ImportedVertex>(meshData.Positions.Length);
            for (int i = 0; i < meshData.Positions.Length; i++)
            {
                var p = meshData.Positions[i];
                var n = meshData.Normals[i];
                var uv = meshData.UVs[i];
                var uvChannels = new float[8][];
                uvChannels[0] = new[] { uv.X, uv.Y };

                vertices.Add(new ImportedVertex
                {
                    // Mirror X to match ModelConverter right-handed space.
                    Vertex = new Vector3(-p.X, p.Y, p.Z),
                    Normal = new Vector3(-n.X, n.Y, n.Z),
                    UV = uvChannels
                });
            }

            int triCount = meshData.Indices.Length / 3;
            var faces = new List<ImportedFace>(triCount);
            for (int t = 0; t < triCount; t++)
            {
                int baseIdx = t * 3;
                // Reverse winding to preserve upward +Y front-facing normals after X-mirror.
                faces.Add(new ImportedFace
                {
                    VertexIndices = new[]
                    {
                        meshData.Indices[baseIdx + 2],
                        meshData.Indices[baseIdx + 1],
                        meshData.Indices[baseIdx]
                    }
                });
            }

            var hasUv = new bool[8];
            hasUv[0] = true;

            var importedMesh = new ImportedMesh
            {
                Path = targetFrame?.Path ?? safeBaseName,
                SourceMeshName = meshName,
                GeometryKey = $"terrain:{data.assetsFile?.fileName ?? string.Empty}:{data.m_PathID}:{meshData.GridWidth}x{meshData.GridHeight}",
                TargetFrame = targetFrame,
                VertexList = vertices,
                SubmeshList = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh
                    {
                        Material = materialName,
                        BaseVertex = 0,
                        FaceList = faces
                    }
                },
                hasNormal = true,
                hasUV = hasUv,
                hasTangent = false,
                hasColor = false
            };

            ImportedTexture? importedTexture = null;
            var importedMaterial = new ImportedMaterial
            {
                Name = materialName,
                Diffuse = meshData.BakedAlbedoBytes != null
                    ? new Color(1f, 1f, 1f, 1f)
                    : new Color(0.56f, 0.59f, 0.52f, 1f),
                Ambient = new Color(0.2f, 0.2f, 0.2f, 1f),
                Specular = new Color(0.04f, 0.04f, 0.04f, 1f),
                Emissive = new Color(0f, 0f, 0f, 1f),
                Metallic = 0f,
                Roughness = 0.90f,
                DoubleSided = false,
                UnityShaderMetadata = new ImportedUnityShaderMetadata
                {
                    ShaderName = "Nature/Terrain/Standard",
                    Classification = "Terrain"
                }
            };

            if (meshData.BakedAlbedoBytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(meshData.BakedAlbedoBytes, writable: false);
                importedTexture = new ImportedTexture(ms, textureName);
                importedMaterial.Textures.Add(new ImportedMaterialTexture
                {
                    Name = textureName,
                    Dest = 0,
                    Offset = Vector2.Zero,
                    Scale = new Vector2(1f, 1f)
                });
                importedMaterial.UnityShaderMetadata.TextureProperties["_MainTex"] = textureName;
            }

            return new TerrainConversionResult
            {
                Mesh = importedMesh,
                Material = importedMaterial,
                Texture = importedTexture,
                MeshData = meshData
            };
        }

        private static byte[]? TryCompositeSplatmapTexture(
            TerrainData data,
            Vector3 terrainSize,
            int maxTextureSize,
            ImageFormat imageFormat,
            Func<PPtr<Texture2D>, Texture2D?>? textureResolver,
            Func<PPtr<TerrainLayer>, TerrainLayer?>? layerResolver,
            out int outWidth,
            out int outHeight,
            out int compositedLayerCount)
        {
            outWidth = 0;
            outHeight = 0;
            compositedLayerCount = 0;

            var splatDb = data.m_SplatDatabase;
            if (splatDb == null)
            {
                return null;
            }

            // 1. Decode diffuse layers (2018.3+ TerrainLayers first, fallback to legacy SplatPrototypes).
            var layers = new List<DecodedSplatLayer?>();
            int maxDiffuseW = 0;
            int maxDiffuseH = 0;
            int validLayerCount = 0;

            if (splatDb.m_TerrainLayers is { Count: > 0 })
            {
                int count = Math.Min(splatDb.m_TerrainLayers.Count, MaxSplatLayers);
                for (int i = 0; i < count; i++)
                {
                    var layerPtr = splatDb.m_TerrainLayers[i];
                    var layer = ResolveTerrainLayer(data, layerPtr, layerResolver);
                    if (layer == null)
                    {
                        layers.Add(null);
                        continue;
                    }

                    var tex = ResolveTexture2D(layer, layer.m_DiffuseTexture, textureResolver);
                    var decoded = TryDecodeTextureBgraBottomUp(tex);
                    if (decoded == null)
                    {
                        layers.Add(null);
                        continue;
                    }

                    var tileSize = new Vector2(
                        float.IsFinite(layer.m_TileSize.X) && MathF.Abs(layer.m_TileSize.X) > 1e-4f ? layer.m_TileSize.X : 15f,
                        float.IsFinite(layer.m_TileSize.Y) && MathF.Abs(layer.m_TileSize.Y) > 1e-4f ? layer.m_TileSize.Y : 15f);
                    var tileOffset = new Vector2(
                        float.IsFinite(layer.m_TileOffset.X) ? layer.m_TileOffset.X : 0f,
                        float.IsFinite(layer.m_TileOffset.Y) ? layer.m_TileOffset.Y : 0f);

                    layers.Add(new DecodedSplatLayer
                    {
                        BgraPixels = decoded.Value.Pixels,
                        Width = decoded.Value.Width,
                        Height = decoded.Value.Height,
                        TileSize = tileSize,
                        TileOffset = tileOffset
                    });
                    validLayerCount++;
                    if (decoded.Value.Width > maxDiffuseW) maxDiffuseW = decoded.Value.Width;
                    if (decoded.Value.Height > maxDiffuseH) maxDiffuseH = decoded.Value.Height;
                }
            }
            else if (splatDb.m_Splats is { Count: > 0 })
            {
                int count = Math.Min(splatDb.m_Splats.Count, MaxSplatLayers);
                for (int i = 0; i < count; i++)
                {
                    var proto = splatDb.m_Splats[i];
                    if (proto?.texture == null)
                    {
                        layers.Add(null);
                        continue;
                    }

                    var tex = ResolveTexture2D(data, proto.texture, textureResolver);
                    var decoded = TryDecodeTextureBgraBottomUp(tex);
                    if (decoded == null)
                    {
                        layers.Add(null);
                        continue;
                    }

                    var tileSize = new Vector2(
                        float.IsFinite(proto.tileSize.X) && MathF.Abs(proto.tileSize.X) > 1e-4f ? proto.tileSize.X : 15f,
                        float.IsFinite(proto.tileSize.Y) && MathF.Abs(proto.tileSize.Y) > 1e-4f ? proto.tileSize.Y : 15f);
                    var tileOffset = new Vector2(
                        float.IsFinite(proto.tileOffset.X) ? proto.tileOffset.X : 0f,
                        float.IsFinite(proto.tileOffset.Y) ? proto.tileOffset.Y : 0f);

                    layers.Add(new DecodedSplatLayer
                    {
                        BgraPixels = decoded.Value.Pixels,
                        Width = decoded.Value.Width,
                        Height = decoded.Value.Height,
                        TileSize = tileSize,
                        TileOffset = tileOffset
                    });
                    validLayerCount++;
                    if (decoded.Value.Width > maxDiffuseW) maxDiffuseW = decoded.Value.Width;
                    if (decoded.Value.Height > maxDiffuseH) maxDiffuseH = decoded.Value.Height;
                }
            }

            if (validLayerCount == 0)
            {
                return null;
            }

            // 2. Decode alphamap textures (RGBA -> 4 layers per alphamap).
            var alphaMaps = new List<DecodedAlphaMap?>();
            int alphaW = 0;
            int alphaH = 0;
            if (splatDb.m_AlphaTextures is { Count: > 0 })
            {
                int count = Math.Min(splatDb.m_AlphaTextures.Count, MaxAlphaTextures);
                for (int i = 0; i < count; i++)
                {
                    var alphaTex = ResolveTexture2D(data, splatDb.m_AlphaTextures[i], textureResolver);
                    var decoded = TryDecodeTextureBgraBottomUp(alphaTex);
                    if (decoded == null)
                    {
                        alphaMaps.Add(null);
                        continue;
                    }

                    alphaMaps.Add(new DecodedAlphaMap
                    {
                        BgraPixels = decoded.Value.Pixels,
                        Width = decoded.Value.Width,
                        Height = decoded.Value.Height
                    });
                    if (decoded.Value.Width > alphaW) alphaW = decoded.Value.Width;
                    if (decoded.Value.Height > alphaH) alphaH = decoded.Value.Height;
                }
            }

            int targetW = alphaW > 0
                ? alphaW
                : (splatDb.m_AlphamapResolution > 0 ? splatDb.m_AlphamapResolution : maxDiffuseW);
            int targetH = alphaH > 0
                ? alphaH
                : (splatDb.m_AlphamapResolution > 0 ? splatDb.m_AlphamapResolution : maxDiffuseH);

            outWidth = Math.Clamp(targetW, 2, maxTextureSize);
            outHeight = Math.Clamp(targetH, 2, maxTextureSize);
            compositedLayerCount = validLayerCount;

            // Find first valid layer for fallback when no alphamap or zero-weight pixels exist.
            DecodedSplatLayer? fallbackLayer = null;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] != null)
                {
                    fallbackLayer = layers[i];
                    break;
                }
            }

            bool hasUsableAlphaMap = false;
            for (int i = 0; i < alphaMaps.Count; i++)
            {
                if (alphaMaps[i] != null)
                {
                    hasUsableAlphaMap = true;
                    break;
                }
            }

            var outBgra = new byte[outWidth * outHeight * 4];
            float sizeX = terrainSize.X > 1e-4f ? terrainSize.X : 1f;
            float sizeZ = terrainSize.Z > 1e-4f ? terrainSize.Z : 1f;

            // Write top-down PNG pixel buffer: py=0 => v_unity=1 (z=max), py=outHeight-1 => v_unity=0 (z=0).
            for (int py = 0; py < outHeight; py++)
            {
                float vUnity = 1f - ((py + 0.5f) / outHeight);
                float worldZ = vUnity * sizeZ;
                int outRow = py * outWidth * 4;

                for (int px = 0; px < outWidth; px++)
                {
                    float uUnity = (px + 0.5f) / outWidth;
                    float worldX = uUnity * sizeX;

                    float accR = 0f, accG = 0f, accB = 0f;
                    float totalWeight = 0f;

                    if (hasUsableAlphaMap)
                    {
                        for (int a = 0; a < alphaMaps.Count; a++)
                        {
                            var alphaMap = alphaMaps[a];
                            if (alphaMap == null)
                            {
                                continue;
                            }

                            int ax = Math.Clamp((int)(uUnity * alphaMap.Width), 0, alphaMap.Width - 1);
                            int ay = Math.Clamp((int)(vUnity * alphaMap.Height), 0, alphaMap.Height - 1);
                            int aOff = (ay * alphaMap.Width + ax) * 4;

                            // SKColorType.Bgra8888 layout: [0]=B, [1]=G, [2]=R, [3]=A!
                            // Unity alphamap channel order: R=layer 0, G=layer 1, B=layer 2, A=layer 3.
                            float w0 = alphaMap.BgraPixels[aOff + 2] * (1f / 255f); // R
                            float w1 = alphaMap.BgraPixels[aOff + 1] * (1f / 255f); // G
                            float w2 = alphaMap.BgraPixels[aOff + 0] * (1f / 255f); // B
                            float w3 = alphaMap.BgraPixels[aOff + 3] * (1f / 255f); // A

                            int baseLayer = a * 4;
                            AccumulateLayer(layers, baseLayer + 0, w0, worldX, worldZ, ref accR, ref accG, ref accB, ref totalWeight);
                            AccumulateLayer(layers, baseLayer + 1, w1, worldX, worldZ, ref accR, ref accG, ref accB, ref totalWeight);
                            AccumulateLayer(layers, baseLayer + 2, w2, worldX, worldZ, ref accR, ref accG, ref accB, ref totalWeight);
                            AccumulateLayer(layers, baseLayer + 3, w3, worldX, worldZ, ref accR, ref accG, ref accB, ref totalWeight);
                        }
                    }

                    byte finalR, finalG, finalB;
                    if (totalWeight > 1e-5f)
                    {
                        float invW = 1f / totalWeight;
                        finalR = ClampByte(accR * invW);
                        finalG = ClampByte(accG * invW);
                        finalB = ClampByte(accB * invW);
                    }
                    else if (fallbackLayer != null)
                    {
                        SampleLayerPixel(fallbackLayer, worldX, worldZ, out finalR, out finalG, out finalB);
                    }
                    else
                    {
                        finalR = 143;
                        finalG = 150;
                        finalB = 133;
                    }

                    int dstOff = outRow + px * 4;
                    outBgra[dstOff + 0] = finalB;
                    outBgra[dstOff + 1] = finalG;
                    outBgra[dstOff + 2] = finalR;
                    outBgra[dstOff + 3] = 255;
                }
            }

            try
            {
                using var bitmap = ImageExtensions.CreateBgraUnpremulBitmap(outBgra, outWidth, outHeight, flipVertical: false);
                using var encoded = bitmap.ConvertToStream(imageFormat);
                return encoded.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Warning($"TerrainMeshConverter: failed to encode baked splatmap texture for '{data.m_Name}': {ex.Message}");
                outWidth = 0;
                outHeight = 0;
                return null;
            }
        }

        private static void AccumulateLayer(
            List<DecodedSplatLayer?> layers,
            int layerIndex,
            float weight,
            float worldX,
            float worldZ,
            ref float accR,
            ref float accG,
            ref float accB,
            ref float totalWeight)
        {
            if (weight <= 1e-5f || layerIndex >= layers.Count)
            {
                return;
            }

            var layer = layers[layerIndex];
            if (layer == null)
            {
                return;
            }

            SampleLayerPixel(layer, worldX, worldZ, out byte r, out byte g, out byte b);
            accR += r * weight;
            accG += g * weight;
            accB += b * weight;
            totalWeight += weight;
        }

        private static void SampleLayerPixel(DecodedSplatLayer layer, float worldX, float worldZ, out byte r, out byte g, out byte b)
        {
            float tu = Wrap01((worldX + layer.TileOffset.X) / layer.TileSize.X);
            float tv = Wrap01((worldZ + layer.TileOffset.Y) / layer.TileSize.Y);
            int sx = Math.Clamp((int)(tu * layer.Width), 0, layer.Width - 1);
            int sy = Math.Clamp((int)(tv * layer.Height), 0, layer.Height - 1);
            int off = (sy * layer.Width + sx) * 4;

            // Bgra8888: [0]=B, [1]=G, [2]=R, [3]=A
            b = layer.BgraPixels[off + 0];
            g = layer.BgraPixels[off + 1];
            r = layer.BgraPixels[off + 2];
        }

        private static float Wrap01(float value)
        {
            if (!float.IsFinite(value))
            {
                return 0f;
            }

            float f = value - MathF.Floor(value);
            return f < 0f ? f + 1f : (f >= 1f ? 0f : f);
        }

        private static byte ClampByte(float value)
        {
            if (!float.IsFinite(value) || value <= 0f) return 0;
            if (value >= 255f) return 255;
            return (byte)(value + 0.5f);
        }

        private static (byte[] Pixels, int Width, int Height)? TryDecodeTextureBgraBottomUp(Texture2D? texture)
        {
            if (texture == null || texture.m_Width <= 0 || texture.m_Height <= 0)
            {
                return null;
            }

            try
            {
                using var bmp = texture.ConvertToImage(flip: false, unpackNormalMap: false, forceNormalMapUnpack: false);
                if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
                {
                    return null;
                }

                var pixels = new byte[checked(bmp.Width * bmp.Height * 4)];
                bmp.CopyBgraPixelsTo(pixels);
                return (pixels, bmp.Width, bmp.Height);
            }
            catch (Exception ex)
            {
                Logger.Warning($"TerrainMeshConverter: failed to decode texture '{texture.m_Name}': {ex.Message}");
                return null;
            }
        }

        private static TerrainLayer? ResolveTerrainLayer(
            TerrainData owner,
            PPtr<TerrainLayer>? ptr,
            Func<PPtr<TerrainLayer>, TerrainLayer?>? customResolver)
        {
            if (ptr == null || ptr.IsNull)
            {
                return null;
            }

            if (customResolver != null)
            {
                var custom = customResolver(ptr);
                if (custom != null)
                {
                    return custom;
                }
            }

            if (ptr.TryGet(out var layer) && layer != null)
            {
                return layer;
            }

            var manager = owner.assetsFile?.assetsManager;
            if (manager != null)
            {
                SerializedFile[] snapshot;
                lock (manager.loadLock)
                {
                    snapshot = manager.assetsFileList.ToArray();
                }
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (snapshot[i].TryGetObject(ptr.m_PathID, out var obj) && obj is TerrainLayer found)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        private static Texture2D? ResolveTexture2D(
            Object owner,
            PPtr<Texture2D>? ptr,
            Func<PPtr<Texture2D>, Texture2D?>? customResolver)
        {
            if (ptr == null || ptr.IsNull)
            {
                return null;
            }

            if (customResolver != null)
            {
                var custom = customResolver(ptr);
                if (custom != null)
                {
                    return custom;
                }
            }

            if (ptr.TryGet(out var tex) && tex != null)
            {
                return tex;
            }

            var manager = owner.assetsFile?.assetsManager;
            if (manager != null)
            {
                SerializedFile[] snapshot;
                lock (manager.loadLock)
                {
                    snapshot = manager.assetsFileList.ToArray();
                }
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (snapshot[i].TryGetObject(ptr.m_PathID, out var obj) && obj is Texture2D found)
                    {
                        return found;
                    }
                }
            }

            return null;
        }
    }
}
