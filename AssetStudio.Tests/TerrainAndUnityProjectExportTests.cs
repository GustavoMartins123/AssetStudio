#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.Scripting;
using SkiaSharp;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class TerrainAndUnityProjectExportTests
    {
        // =====================================================================
        // 1. Terrain / TerrainLayer / TerrainData Parsing (Binary, TypeTree & Truncation)
        // =====================================================================

        [Fact]
        public void TerrainBinaryParsing_ReadsModernAndLegacyLayoutsAndHandlesTruncationSafely()
        {
            using var am = new AssetsManager();

            // 1) Unity 2022.3 binary layout
            var sf2022 = CreateSyntheticSerializedFile(am, "CAB-terrain-2022", "2022.3.40f1");
            byte[] payload2022;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(0); bw.Write(100L); // m_GameObject
                bw.Write((byte)1); bw.Write(new byte[3]); // m_Enabled + align
                bw.Write(0); bw.Write(200L); // m_TerrainData
                bw.Write(4500f); // m_TreeDistance
                bw.Write(75f);   // m_TreeBillboardDistance
                bw.Write(8f);    // m_TreeCrossFadeLength
                bw.Write(40);    // m_TreeMaximumFullLODCount
                bw.Write(120f);  // m_DetailObjectDistance
                bw.Write(0.85f); // m_DetailObjectDensity
                bw.Write(4f);    // m_HeightmapPixelError
                bw.Write(1500f); // m_SplatMapDistance
                bw.Write(1);     // m_HeightmapMaximumLOD
                bw.Write(2);     // m_ShadowCastingMode (TwoSided)
                bw.Write(true);  // m_DrawHeightmap
                bw.Write(true);  // m_DrawInstanced
                bw.Write(false); // m_DrawTreesAndFoliage
                bw.Write(false); // m_StaticShadowCaster (2021.1+)
                bw.Write(1);     // m_ReflectionProbeUsage
                bw.Write(0); bw.Write(300L); // m_MaterialTemplate
                payload2022 = ms.ToArray();
            }

            var terrain2022 = Assert.IsType<Terrain>(CreateObjectViaAssetsManager(sf2022, ClassIDType.Terrain, 50, payload2022));
            Assert.False(terrain2022.IsTruncated);
            Assert.True(terrain2022.Enabled);
            Assert.Equal(200L, terrain2022.m_TerrainData.m_PathID);
            Assert.Equal(4500f, terrain2022.m_TreeDistance);
            Assert.Equal(75f, terrain2022.m_TreeBillboardDistance);
            Assert.Equal(120f, terrain2022.m_DetailObjectDistance);
            Assert.Equal(1500f, terrain2022.m_SplatMapDistance);
            Assert.Equal(1, terrain2022.m_HeightmapMaximumLOD);
            Assert.Equal(2, terrain2022.m_ShadowCastingMode);
            Assert.True(terrain2022.m_DrawHeightmap);
            Assert.True(terrain2022.m_DrawInstanced);
            Assert.False(terrain2022.m_DrawTreesAndFoliage);
            Assert.Equal(300L, terrain2022.m_MaterialTemplate.m_PathID);

            // 1b) TypeTree-first path for Terrain
            var terrainTypeTree = BuildTerrainTypeTree();
            var terrainTt = Assert.IsType<Terrain>(CreateObjectViaAssetsManager(sf2022, ClassIDType.Terrain, 55, payload2022, terrainTypeTree));
            Assert.False(terrainTt.IsTruncated);
            Assert.True(terrainTt.Enabled);
            Assert.Equal(200L, terrainTt.m_TerrainData.m_PathID);
            Assert.Equal(4500f, terrainTt.m_TreeDistance);
            Assert.Equal(2, terrainTt.m_ShadowCastingMode);
            Assert.True(terrainTt.m_DrawHeightmap);
            Assert.Equal(300L, terrainTt.m_MaterialTemplate.m_PathID);

            // 2) Unity 2018.4 binary layout (legacy m_CastShadows + m_MaterialType block)
            var sf2018 = CreateSyntheticSerializedFile(am, "CAB-terrain-2018", "2018.4.36f1");
            byte[] payload2018;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(0); bw.Write(101L); // m_GameObject
                bw.Write((byte)1); bw.Write(new byte[3]); // m_Enabled + align
                bw.Write(0); bw.Write(201L); // m_TerrainData
                bw.Write(3000f); bw.Write(60f); bw.Write(6f); bw.Write(30);
                bw.Write(90f); bw.Write(0.75f); bw.Write(5f); bw.Write(900f); bw.Write(0);
                bw.Write(true);  // m_CastShadows
                bw.Write(true);  // m_DrawHeightmap
                bw.Write(false); // m_DrawInstanced (2018.3+)
                bw.Write(true);  // m_DrawTreesAndFoliage
                bw.Write(1);     // m_ReflectionProbeUsage
                bw.Write(3);     // m_MaterialType
                bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(1f); // m_LegacySpecular
                bw.Write(0.5f);  // m_LegacyShininess
                bw.Write(true); bw.Write(new byte[3]); // m_UseDefaultSmoothness + align
                bw.Write(0); bw.Write(301L); // m_MaterialTemplate
                payload2018 = ms.ToArray();
            }

            var terrain2018 = Assert.IsType<Terrain>(CreateObjectViaAssetsManager(sf2018, ClassIDType.Terrain, 51, payload2018));
            Assert.False(terrain2018.IsTruncated);
            Assert.Equal(201L, terrain2018.m_TerrainData.m_PathID);
            Assert.Equal(1, terrain2018.m_ShadowCastingMode);
            Assert.True(terrain2018.m_CastShadows);
            Assert.Equal(301L, terrain2018.m_MaterialTemplate.m_PathID);

            // 3) Truncated payloads (0 bytes and partial 18 bytes) must NEVER throw!
            var truncEmpty = Assert.IsType<Terrain>(CreateObjectViaAssetsManager(sf2022, ClassIDType.Terrain, 52, Array.Empty<byte>()));
            Assert.True(truncEmpty.IsTruncated);

            var truncPartial = Assert.IsType<Terrain>(CreateObjectViaAssetsManager(sf2022, ClassIDType.Terrain, 53, payload2022.Take(20).ToArray()));
            Assert.True(truncPartial.IsTruncated);
        }

        [Fact]
        public void TerrainLayerParsing_BinaryTypeTreeAndTruncationSafety()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-terrain-layer", "2022.3.40f1");

            byte[] payload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteAlignedString(bw, "GrassLayer");
                bw.Write(0); bw.Write(10L); // m_DiffuseTexture
                bw.Write(0); bw.Write(11L); // m_NormalMapTexture
                bw.Write(0); bw.Write(12L); // m_MaskMapTexture
                bw.Write(20f); bw.Write(25f); // m_TileSize
                bw.Write(2f); bw.Write(3f);   // m_TileOffset
                bw.Write(0.1f); bw.Write(0.2f); bw.Write(0.3f); bw.Write(1f); // m_Specular
                bw.Write(0.25f); // m_Metallic
                bw.Write(0.6f);  // m_Smoothness
                bw.Write(1.25f); // m_NormalScale
                bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f); // m_DiffuseRemapMin
                bw.Write(1f); bw.Write(0.9f); bw.Write(0.8f); bw.Write(1f); // m_DiffuseRemapMax
                payload = ms.ToArray();
            }

            // Binary fallback
            var layerBin = Assert.IsType<TerrainLayer>(CreateObjectViaAssetsManager(sf, ClassIDType.TerrainLayer, 70, payload));
            Assert.False(layerBin.IsTruncated);
            Assert.Equal("GrassLayer", layerBin.m_Name);
            Assert.Equal(10L, layerBin.m_DiffuseTexture.m_PathID);
            Assert.Equal(11L, layerBin.m_NormalMapTexture.m_PathID);
            Assert.Equal(12L, layerBin.m_MaskMapTexture.m_PathID);
            Assert.Equal(20f, layerBin.m_TileSize.X);
            Assert.Equal(25f, layerBin.m_TileSize.Y);
            Assert.Equal(2f, layerBin.m_TileOffset.X);
            Assert.Equal(3f, layerBin.m_TileOffset.Y);
            Assert.Equal(0.25f, layerBin.m_Metallic);
            Assert.Equal(0.6f, layerBin.m_Smoothness);
            Assert.Equal(1.25f, layerBin.m_NormalScale);
            Assert.Equal(0.9f, layerBin.m_DiffuseRemapMax.Y);

            // TypeTree-first path
            var layerTypeTree = BuildTerrainLayerTypeTree();
            var layerTt = Assert.IsType<TerrainLayer>(CreateObjectViaAssetsManager(sf, ClassIDType.TerrainLayer, 71, payload, layerTypeTree));
            Assert.False(layerTt.IsTruncated);
            Assert.Equal("GrassLayer", layerTt.m_Name);
            Assert.Equal(10L, layerTt.m_DiffuseTexture.m_PathID);
            Assert.Equal(20f, layerTt.m_TileSize.X);
            Assert.Equal(1.25f, layerTt.m_NormalScale);

            // Truncated streams
            var trunc0 = Assert.IsType<TerrainLayer>(CreateObjectViaAssetsManager(sf, ClassIDType.TerrainLayer, 72, Array.Empty<byte>()));
            Assert.True(trunc0.IsTruncated);
            var truncSmall = Assert.IsType<TerrainLayer>(CreateObjectViaAssetsManager(sf, ClassIDType.TerrainLayer, 73, payload.Take(16).ToArray()));
            Assert.True(truncSmall.IsTruncated);
        }

        [Fact]
        public void TerrainDataParsing_Supports2019_3ModernAnd2018LegacyBinaryAndTypeTreeAndTruncation()
        {
            using var am = new AssetsManager();

            // 1) Unity 2022.3 TerrainData (m_Resolution + m_Holes + m_TerrainLayers)
            var sfModern = CreateSyntheticSerializedFile(am, "CAB-td-2022", "2022.3.40f1");
            short[] modernHeights = { 0, 8192, 16383, 8192, 16383, 24574, 16383, 24574, 32766 }; // 3x3
            byte[] modernHoles = { 1, 0, 1, 1 }; // 2x2 cells, one hole!
            var modernPayload = BuildModernTerrainDataBinary("IslandTerrain", new long[] { 501, 502 }, new long[] { 601 }, modernHeights, modernHoles, 3, new Vector3(10f, 50f, 10f), includeDetailScatterMode: true);

            // Binary fallback
            var tdModernBin = Assert.IsType<TerrainData>(CreateObjectViaAssetsManager(sfModern, ClassIDType.TerrainData, 80, modernPayload));
            Assert.False(tdModernBin.IsTruncated);
            Assert.True(tdModernBin.HasValidHeightmap);
            Assert.Equal("IslandTerrain", tdModernBin.m_Name);
            Assert.Equal(2, tdModernBin.m_SplatDatabase.m_TerrainLayers.Count);
            Assert.Equal(501L, tdModernBin.m_SplatDatabase.m_TerrainLayers[0].m_PathID);
            Assert.Single(tdModernBin.m_SplatDatabase.m_AlphaTextures);
            Assert.Equal(601L, tdModernBin.m_SplatDatabase.m_AlphaTextures[0].m_PathID);
            Assert.Equal(3, tdModernBin.m_Heightmap.m_Resolution);
            Assert.Equal(9, tdModernBin.m_Heightmap.m_Heights.Length);
            Assert.Equal(32766, tdModernBin.m_Heightmap.m_Heights[8]);
            Assert.Equal(new byte[] { 1, 0, 1, 1 }, tdModernBin.m_Heightmap.m_Holes);
            Assert.Equal(new Vector3(20f, 50f, 20f), tdModernBin.m_Heightmap.TerrainSize);

            // Streaming TypeTree path (zero-boxing heights + skips DetailDatabase)
            var modernTypeTree = BuildModernTerrainDataTypeTree();
            var tdModernTt = Assert.IsType<TerrainData>(CreateObjectViaAssetsManager(sfModern, ClassIDType.TerrainData, 81, modernPayload, modernTypeTree));
            Assert.False(tdModernTt.IsTruncated);
            Assert.True(tdModernTt.HasValidHeightmap);
            Assert.Equal("IslandTerrain", tdModernTt.m_Name);
            Assert.Equal(2, tdModernTt.m_SplatDatabase.m_TerrainLayers.Count);
            Assert.Equal(3, tdModernTt.m_Heightmap.m_Resolution);
            Assert.Equal(32766, tdModernTt.m_Heightmap.m_Heights[8]);
            Assert.Equal((byte)0, tdModernTt.m_Heightmap.m_Holes[1]);

            // 2) Legacy Unity 2018.2 TerrainData (m_Width/m_Height + m_Splats SplatPrototype)
            var sfLegacy = CreateSyntheticSerializedFile(am, "CAB-td-2018", "2018.2.21f1");
            var legacyPayload = BuildLegacyTerrainDataBinary("LegacyValley", new long[] { 701 }, new long[] { 801 }, modernHeights, 3, 3, new Vector3(5f, 30f, 5f));

            var tdLegacyBin = Assert.IsType<TerrainData>(CreateObjectViaAssetsManager(sfLegacy, ClassIDType.TerrainData, 82, legacyPayload));
            Assert.False(tdLegacyBin.IsTruncated);
            Assert.True(tdLegacyBin.HasValidHeightmap);
            Assert.Equal("LegacyValley", tdLegacyBin.m_Name);
            Assert.Single(tdLegacyBin.m_SplatDatabase.m_Splats);
            Assert.Equal(701L, tdLegacyBin.m_SplatDatabase.m_Splats[0].texture!.m_PathID);
            Assert.Equal(3, tdLegacyBin.m_Heightmap.m_Width);
            Assert.Equal(3, tdLegacyBin.m_Heightmap.m_Height);
            Assert.Equal(new Vector3(10f, 30f, 10f), tdLegacyBin.m_Heightmap.TerrainSize);

            // 3) Truncated TerrainData (0 bytes + mid-heightmap truncation) must NEVER throw
            var truncEmpty = Assert.IsType<TerrainData>(CreateObjectViaAssetsManager(sfModern, ClassIDType.TerrainData, 83, Array.Empty<byte>()));
            Assert.True(truncEmpty.IsTruncated);
            Assert.False(truncEmpty.HasValidHeightmap);

            var truncMid = Assert.IsType<TerrainData>(CreateObjectViaAssetsManager(sfModern, ClassIDType.TerrainData, 84, modernPayload.Take(modernPayload.Length - 24).ToArray()));
            Assert.True(truncMid.IsTruncated);
        }

        // =====================================================================
        // 2. TerrainMeshConverter: Heightmap, Normals, Downsampling, Holes & 4-Channel Splatmap
        // =====================================================================

        [Fact]
        public void TerrainMeshConverter_HeightmapNormalsDownsamplingHolesAndSplatmapCompositing_AreAccurate()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-terrain-conv", "2022.3.40f1");

            // (A) 3x3 sloped heightmap along +X with 1 hole cell out of 4 cells:
            // x=0 => h=0, x=1 => h=16383 (0.5), x=2 => h=32766 (1.0)
            // scale = (10, 20, 10) => terrain size = (20, 20, 20), slope dh/dx = 20/20 = 1.0!
            // Analytical Unity normal should be normalize((-1, 1, 0)) = (-0.7071, 0.7071, 0)
            var td = CreateSyntheticTerrainData(
                sf, 10, "SlopeTerrain",
                width: 3, height: 3,
                scale: new Vector3(10f, 20f, 10f),
                heights: new short[]
                {
                    0, 16383, 32766,
                    0, 16383, 32766,
                    0, 16383, 32766
                },
                holes: new byte[] { 1, 0, 1, 1 }); // 1 hole cell -> 3 solid cells = 6 triangles = 18 indices!

            var meshData = TerrainMeshConverter.BuildTerrainMeshData(td, new TerrainConversionOptions { CompositeSplatmap = false });
            Assert.NotNull(meshData);
            Assert.Equal(9, meshData!.Positions.Length);
            Assert.Equal(1, meshData.SkippedHoleCellCount);
            Assert.Equal(18, meshData.Indices.Length); // 3 solid cells * 2 tris * 3 indices
            Assert.Equal(new Vector3(0f, 0f, 0f), meshData.Positions[0]);
            Assert.Equal(10f, meshData.Positions[1].X, 3);
            Assert.Equal(10f, meshData.Positions[1].Y, 2);
            Assert.Equal(20f, meshData.Positions[8].X, 3);
            Assert.Equal(20f, meshData.Positions[8].Y, 2);
            Assert.Equal(20f, meshData.Positions[8].Z, 3);

            // Verify analytical smooth normal on slope dh/dx = 1, dh/dz = 0 -> (-0.7071, 0.7071, 0)
            float expectedInvSqrt2 = 1f / MathF.Sqrt(2f);
            Assert.Equal(-expectedInvSqrt2, meshData.Normals[4].X, 3);
            Assert.Equal(expectedInvSqrt2, meshData.Normals[4].Y, 3);
            Assert.Equal(0f, meshData.Normals[4].Z, 3);

            // Verify UVs span [0..1]
            Assert.Equal(0f, meshData.UVs[0].X, 4);
            Assert.Equal(0f, meshData.UVs[0].Y, 4);
            Assert.Equal(1f, meshData.UVs[8].X, 4);
            Assert.Equal(1f, meshData.UVs[8].Y, 4);

            // (B) Downsampling 65x65 -> MaxGridResolution = 17
            var hiHeights = new short[65 * 65];
            for (int z = 0; z < 65; z++)
            {
                for (int x = 0; x < 65; x++)
                {
                    hiHeights[z * 65 + x] = (short)(x * 32766 / 64);
                }
            }
            var hiTd = CreateSyntheticTerrainData(sf, 11, "HiResTerrain", 65, 65, new Vector3(2f, 100f, 2f), hiHeights);
            var downData = TerrainMeshConverter.BuildTerrainMeshData(hiTd, new TerrainConversionOptions { MaxGridResolution = 17, CompositeSplatmap = false });
            Assert.NotNull(downData);
            Assert.Equal(17, downData!.GridWidth);
            Assert.Equal(17, downData.GridHeight);
            Assert.Equal(17 * 17, downData.Positions.Length);
            Assert.Equal(16 * 16 * 6, downData.Indices.Length);
            Assert.Equal(128f, downData.TerrainSize.X, 3);
            Assert.Equal(100f, downData.Positions[^1].Y, 2);

            // (C) 4-channel RGBA splatmap compositing into valid PNG bytes!
            // 4 solid-color diffuse textures:
            // Layer 0 (R): Red (220, 20, 20)
            // Layer 1 (G): Green (20, 210, 30)
            // Layer 2 (B): Blue (25, 40, 230)
            // Layer 3 (A): Yellow (240, 220, 30)
            var texRed = CreateSolidRgbaTexture2D(sf, 20, "T_Red", 220, 20, 20, 255);
            var texGreen = CreateSolidRgbaTexture2D(sf, 21, "T_Green", 20, 210, 30, 255);
            var texBlue = CreateSolidRgbaTexture2D(sf, 22, "T_Blue", 25, 40, 230, 255);
            var texYellow = CreateSolidRgbaTexture2D(sf, 23, "T_Yellow", 240, 220, 30, 255);

            var l0 = CreateSyntheticTerrainLayer(sf, 30, "L0", texRed);
            var l1 = CreateSyntheticTerrainLayer(sf, 31, "L1", texGreen);
            var l2 = CreateSyntheticTerrainLayer(sf, 32, "L2", texBlue);
            var l3 = CreateSyntheticTerrainLayer(sf, 33, "L3", texYellow);

            // 2x2 bottom-up Unity alphamap:
            // row 0 (bottom, v_unity=0): px0 = R(layer0=Red), px1 = G(layer1=Green)
            // row 1 (top,    v_unity=1): px0 = B(layer2=Blue), px1 = A(layer3=Yellow)
            var alphaTex = CreateCustomRgbaTexture2D(sf, 40, "SplatAlpha0", 2, 2, new byte[]
            {
                // row 0 (bottom in Unity)
                255, 0, 0, 0,     // (0,0) -> Layer 0 Red
                0, 255, 0, 0,     // (1,0) -> Layer 1 Green
                // row 1 (top in Unity)
                0, 0, 255, 0,     // (0,1) -> Layer 2 Blue
                0, 0, 0, 255      // (1,1) -> Layer 3 Yellow
            });

            td.m_SplatDatabase.m_TerrainLayers.Add(CreatePPtr<TerrainLayer>(sf, 0, l0.m_PathID));
            td.m_SplatDatabase.m_TerrainLayers.Add(CreatePPtr<TerrainLayer>(sf, 0, l1.m_PathID));
            td.m_SplatDatabase.m_TerrainLayers.Add(CreatePPtr<TerrainLayer>(sf, 0, l2.m_PathID));
            td.m_SplatDatabase.m_TerrainLayers.Add(CreatePPtr<TerrainLayer>(sf, 0, l3.m_PathID));
            td.m_SplatDatabase.m_AlphaTextures.Add(CreatePPtr<Texture2D>(sf, 0, alphaTex.m_PathID));
            td.m_SplatDatabase.m_AlphamapResolution = 2;

            var composited = TerrainMeshConverter.BuildTerrainMeshData(td, new TerrainConversionOptions { CompositeSplatmap = true, MaxBakedTextureSize = 64 });
            Assert.NotNull(composited);
            Assert.NotNull(composited!.BakedAlbedoBytes);
            Assert.Equal(4, composited.CompositedLayerCount);
            Assert.Equal(2, composited.BakedAlbedoWidth);
            Assert.Equal(2, composited.BakedAlbedoHeight);

            // Verify PNG magic signature and decode pixels to verify exact channel mapping + top-down orientation!
            Assert.True(composited.BakedAlbedoBytes!.Length > 8);
            Assert.Equal((byte)0x89, composited.BakedAlbedoBytes[0]);
            Assert.Equal((byte)'P', composited.BakedAlbedoBytes[1]);
            Assert.Equal((byte)'N', composited.BakedAlbedoBytes[2]);
            Assert.Equal((byte)'G', composited.BakedAlbedoBytes[3]);

            using var decodedPng = SKBitmap.Decode(composited.BakedAlbedoBytes);
            Assert.NotNull(decodedPng);
            // Top-left (0, 0) in PNG = v_unity=1, u=0 => Layer 2 Blue
            var topLeft = decodedPng.GetPixel(0, 0);
            Assert.Equal(25, topLeft.Red);
            Assert.Equal(40, topLeft.Green);
            Assert.Equal(230, topLeft.Blue);
            // Top-right (1, 0) in PNG = v_unity=1, u=1 => Layer 3 Yellow
            var topRight = decodedPng.GetPixel(1, 0);
            Assert.Equal(240, topRight.Red);
            Assert.Equal(220, topRight.Green);
            Assert.Equal(30, topRight.Blue);
            // Bottom-left (0, 1) in PNG = v_unity=0, u=0 => Layer 0 Red
            var bottomLeft = decodedPng.GetPixel(0, 1);
            Assert.Equal(220, bottomLeft.Red);
            Assert.Equal(20, bottomLeft.Green);
            Assert.Equal(20, bottomLeft.Blue);
            // Bottom-right (1, 1) in PNG = v_unity=0, u=1 => Layer 1 Green
            var bottomRight = decodedPng.GetPixel(1, 1);
            Assert.Equal(20, bottomRight.Red);
            Assert.Equal(210, bottomRight.Green);
            Assert.Equal(30, bottomRight.Blue);
        }

        // =====================================================================
        // 3. ModelConverter.CreateForScene + GltfExporter with Terrain
        // =====================================================================

        [Fact]
        public void SceneGlbExport_WithTerrain_ExportsValidGlbAtTerrainTransformAndMarksTerrainSupported()
        {
            var tempRoot = CreateTempDirectory("terrain_glb");
            try
            {
                // Verify required scene types include Terrain & Colliders
                var reqTypes = SceneGlbExportService.GetRequiredSceneObjectTypes(false, true, false);
                Assert.Contains(ClassIDType.Terrain, reqTypes);
                Assert.Contains(ClassIDType.TerrainData, reqTypes);
                Assert.Contains(ClassIDType.TerrainLayer, reqTypes);
                Assert.Contains(ClassIDType.BoxCollider, reqTypes);
                Assert.Contains(ClassIDType.SphereCollider, reqTypes);
                Assert.Contains(ClassIDType.CapsuleCollider, reqTypes);
                Assert.Contains(ClassIDType.MeshCollider, reqTypes);
                Assert.Contains(ClassIDType.TerrainCollider, reqTypes);

                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var grassTex = CreateSolidRgbaTexture2D(sf, 20, "T_Grass", 60, 160, 50, 255);
                var grassLayer = CreateSyntheticTerrainLayer(sf, 21, "Grass", grassTex);
                var alphaTex = CreateSolidRgbaTexture2D(sf, 22, "Splat0", 255, 0, 0, 0);
                var td = CreateSyntheticTerrainData(
                    sf, 30, "MeadowData", 5, 5, new Vector3(10f, 25f, 10f),
                    Enumerable.Range(0, 25).Select(i => (short)(i * 1000)).ToArray());
                td.m_SplatDatabase.m_TerrainLayers.Add(CreatePPtr<TerrainLayer>(sf, 0, grassLayer.m_PathID));
                td.m_SplatDatabase.m_AlphaTextures.Add(CreatePPtr<Texture2D>(sf, 0, alphaTex.m_PathID));

                var rootGo = CreateSyntheticGameObject(sf, 100, "WorldRoot", true, 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                var terrainGo = CreateSyntheticGameObject(sf, 110, "MainTerrain", true, 0);
                var terrainTr = CreateSyntheticTransform(sf, 111, terrainGo, rootTr, new Vector3(100f, 5f, -50f));
                var terrainComp = CreateSyntheticTerrain(sf, 112, terrainGo, td);
                AttachComponents(terrainGo, terrainTr, terrainComp);
                rootTr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, terrainTr.m_PathID) };
                AttachComponents(rootGo, rootTr);

                // Direct ModelConverter check
                var converted = ModelConverter.CreateForScene(
                    "MeadowScene",
                    new[] { rootGo },
                    sceneOptions: new SceneConversionOptions { MaxTerrainGridResolution = 5 });
                Assert.Equal(1, converted.ExportedTerrainCount);
                Assert.Single(converted.MeshList);
                Assert.Single(converted.TextureList);

                // Full SceneGlbExportService roundtrip
                var glbPath = Path.Combine(tempRoot, "MeadowScene.glb");
                var service = new SceneGlbExportService();
                var result = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = glbPath,
                        SceneName = "MeadowScene",
                        WriteManifest = true
                    });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(File.Exists(glbPath));
                Assert.NotNull(result.Manifest);
                Assert.False(result.Manifest!.UnsupportedComponents.ContainsKey(nameof(ClassIDType.Terrain)));

                using var doc = ParseGlbJson(glbPath);
                var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
                var terrainNode = nodes.First(n => n.GetProperty("name").GetString() == "MainTerrain");
                Assert.True(terrainNode.TryGetProperty("mesh", out _));
                var translation = terrainNode.GetProperty("translation");
                Assert.Equal(-100f, translation[0].GetSingle(), 3); // X mirrored in glTF
                Assert.Equal(5f, translation[1].GetSingle(), 3);
                Assert.Equal(-50f, translation[2].GetSingle(), 3);
                Assert.Equal(1, doc.RootElement.GetProperty("textures").GetArrayLength());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 4. UnityProjectExporterService: Full Fidelity Scene + Deterministic GUID Parity
        // =====================================================================

        [Fact]
        public void UnityProjectExporter_FullScene_WritesValidYamlMetaManifestAndMatchesMonoScriptGuidContract()
        {
            var tempRoot = CreateTempDirectory("unity_proj_full");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                // Assets
                var customShader = CreateSyntheticShader(sf, 10, "Custom/StylizedLit");
                var albedoTex = CreateSolidRgbaTexture2D(sf, 20, "T_Brick_Albedo", 180, 90, 70, 255);
                var normalTex = CreateSolidRgbaTexture2D(sf, 21, "T_Brick_Normal", 128, 128, 255, 255);
                var matA = CreateSyntheticMaterial(
                    sf, 30, "Mat_Brick", customShader,
                    texEnvs: new[] { ("_BaseMap", albedoTex), ("_BumpMap", normalTex) },
                    colors: new[] { ("_BaseColor", new Color(0.9f, 0.8f, 0.7f, 1f)) },
                    floats: new[] { ("_Smoothness", 0.35f), ("_Metallic", 0.1f) },
                    renderQueue: 2000,
                    keywords: new[] { "_NORMALMAP", "_SPECULARHIGHLIGHTS_OFF" },
                    tags: new[] { ("RenderType", "Opaque") });
                var matB = CreateSyntheticMaterial(sf, 31, "Mat_Trim", customShader, floats: new[] { ("_Glossiness", 0.5f) });
                var multiSubMesh = CreateSyntheticTwoSubmeshMesh(sf, 40, "SM_Tower");

                // Terrain assets
                var grassTex = CreateSolidRgbaTexture2D(sf, 22, "T_Meadow", 50, 140, 40, 255);
                var grassLayer = CreateSyntheticTerrainLayer(sf, 23, "MeadowLayer", grassTex);
                var splatAlpha = CreateSolidRgbaTexture2D(sf, 24, "SplatAlpha", 255, 0, 0, 0);
                var terrainData = CreateSyntheticTerrainData(sf, 45, "HillData", 3, 3, new Vector3(10f, 15f, 10f), new short[] { 0, 5000, 10000, 5000, 15000, 20000, 10000, 20000, 32766 });
                terrainData.m_SplatDatabase.m_TerrainLayers.Add(CreatePPtr<TerrainLayer>(sf, 0, grassLayer.m_PathID));
                terrainData.m_SplatDatabase.m_AlphaTextures.Add(CreatePPtr<Texture2D>(sf, 0, splatAlpha.m_PathID));

                // MonoScript
                var monoScript = CreateSyntheticMonoScript(sf, 50, "Game.World", "RegionController", "Assembly-CSharp.dll");

                // Scene Hierarchy
                var rootGo = CreateSyntheticGameObject(sf, 100, "StageRoot", isActive: true, layer: 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                // (1) Multi-submesh Tower with ShadowsOnly renderer + BoxCollider + MeshCollider
                var towerGo = CreateSyntheticGameObject(sf, 110, "Tower_ShadowCaster", isActive: true, layer: 23, tag: 20001);
                var towerTr = CreateSyntheticTransform(sf, 111, towerGo, rootTr, new Vector3(12.5f, 0f, -4.25f));
                var towerMf = CreateSyntheticMeshFilter(sf, 112, towerGo, 0, multiSubMesh.m_PathID);
                var towerMr = CreateSyntheticMeshRenderer(
                    sf, 113, towerGo, true,
                    new[] { CreatePPtr<Material>(sf, 0, matA.m_PathID), CreatePPtr<Material>(sf, 0, matB.m_PathID) },
                    castShadows: Renderer.ShadowCastingModeShadowsOnly);
                var boxCol = CreateSyntheticObject<AssetStudio.Object>(sf, 114, ClassIDType.BoxCollider);
                var meshCol = CreateSyntheticObject<AssetStudio.Object>(sf, 115, ClassIDType.MeshCollider);
                AttachComponents(towerGo, towerTr, towerMf, towerMr, boxCol, meshCol);

                // (2) Inactive GameObject with LODGroup pointing to Tower MeshRenderer
                var inactiveGo = CreateSyntheticGameObject(sf, 120, "HiddenTrigger: Zone #1", isActive: false, layer: 23);
                var inactiveTr = CreateSyntheticTransform(sf, 121, inactiveGo, rootTr, new Vector3(1f, 2f, 3f));
                var lodGroup = CreateSyntheticObject<LODGroup>(sf, 122, ClassIDType.LODGroup);
                lodGroup.m_GameObject = CreatePPtr<GameObject>(sf, 0, inactiveGo.m_PathID);
                lodGroup.m_Enabled = true;
                lodGroup.m_Size = 10f;
                lodGroup.m_LODs = new[]
                {
                    new LOD
                    {
                        screenRelativeHeight = 0.25f,
                        fadeTransitionWidth = 0.1f,
                        renderers = new[] { CreatePPtr<Renderer>(sf, 0, towerMr.m_PathID) }
                    }
                };
                AttachComponents(inactiveGo, inactiveTr, lodGroup);

                // (3) Camera with custom m_CullingMask & Directional Light
                var camGo = CreateSyntheticGameObject(sf, 130, "MainCamera", isActive: true, layer: 0, tag: 4);
                var camTr = CreateSyntheticTransform(sf, 131, camGo, rootTr, new Vector3(0f, 10f, -20f));
                var cam = CreateSyntheticObject<Camera>(sf, 132, ClassIDType.Camera);
                cam.m_GameObject = CreatePPtr<GameObject>(sf, 0, camGo.m_PathID);
                cam.m_Enabled = 1;
                cam.m_ClearFlags = 1;
                cam.m_FieldOfView = 55f;
                cam.m_NearClipPlane = 0.1f;
                cam.m_FarClipPlane = 500f;
                cam.m_CullingMask = 0x0080_0001u; // Layers 0 & 23
                var sunLight = CreateSyntheticObject<Light>(sf, 133, ClassIDType.Light);
                sunLight.m_GameObject = CreatePPtr<GameObject>(sf, 0, camGo.m_PathID);
                sunLight.m_Enabled = 1;
                sunLight.m_Type = LightType.Directional;
                sunLight.m_Intensity = 1.75f;
                sunLight.m_Color = new Color(1f, 0.95f, 0.85f, 1f);
                sunLight.m_Shadows = new ShadowSettings { m_Type = 2, m_Strength = 0.8f };
                AttachComponents(camGo, camTr, cam, sunLight);

                // (4) Terrain node
                var terrainGo = CreateSyntheticGameObject(sf, 140, "WorldTerrain", isActive: true, layer: 0);
                var terrainTr = CreateSyntheticTransform(sf, 141, terrainGo, rootTr, new Vector3(-50f, 0f, -50f));
                var terrainComp = CreateSyntheticTerrain(sf, 142, terrainGo, terrainData);
                AttachComponents(terrainGo, terrainTr, terrainComp);

                // (5) MonoBehaviour with MonoScript + custom TypeTree fields (including PPtrs + YAML special chars)
                var scriptGo = CreateSyntheticGameObject(sf, 150, "RegionMgr", isActive: true, layer: 0);
                var scriptTr = CreateSyntheticTransform(sf, 151, scriptGo, rootTr, Vector3.Zero);
                var mb = CreateSyntheticObject<MonoBehaviour>(sf, 152, ClassIDType.MonoBehaviour);
                mb.m_GameObject = CreatePPtr<GameObject>(sf, 0, scriptGo.m_PathID);
                mb.m_Enabled = 1;
                mb.m_Script = CreatePPtr<MonoScript>(sf, 0, monoScript.m_PathID);
                mb.m_Name = string.Empty;
                AttachComponents(scriptGo, scriptTr, mb);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, towerTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, inactiveTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, camTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, terrainTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, scriptTr.m_PathID)
                };
                AttachComponents(rootGo, rootTr);

                OrderedDictionary? MbFields(MonoBehaviour behaviour)
                {
                    if (!ReferenceEquals(behaviour, mb)) return null;
                    return new OrderedDictionary
                    {
                        ["regionTitle"] = "Harbor: North #2",
                        ["spawnDelay"] = 1.5f,
                        ["isBossZone"] = true,
                        ["spawnPoint"] = new OrderedDictionary { ["x"] = 4f, ["y"] = 1f, ["z"] = -2f },
                        ["towerTarget"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = towerGo.m_PathID },
                        ["overrideMaterial"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = matA.m_PathID }
                    };
                }

                OrderedDictionary? ColliderFields(AssetStudio.Object col)
                {
                    if (ReferenceEquals(col, boxCol))
                    {
                        return new OrderedDictionary
                        {
                            ["m_Enabled"] = true,
                            ["m_IsTrigger"] = true,
                            ["m_Size"] = new OrderedDictionary { ["x"] = 2f, ["y"] = 5f, ["z"] = 2f },
                            ["m_Center"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 2.5f, ["z"] = 0f }
                        };
                    }
                    if (ReferenceEquals(col, meshCol))
                    {
                        return new OrderedDictionary
                        {
                            ["m_Enabled"] = true,
                            ["m_IsTrigger"] = false,
                            ["m_Convex"] = true,
                            ["m_Mesh"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = multiSubMesh.m_PathID }
                        };
                    }
                    return null;
                }

                var scriptsCallbackInvoked = false;
                var exporter = new UnityProjectExporterService();
                var result = exporter.ExportSceneToUnityProject(
                    new[] { rootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = tempRoot,
                        SceneName = "HarborStage",
                        ScenePath = "Assets/Scenes/HarborStage.unity",
                        IncludeInactive = true,
                        WriteManifest = true,
                        MonoBehaviourFieldProvider = MbFields,
                        ColliderFieldProvider = ColliderFields,
                        IncludeScriptsCallback = (scriptsDir, _) =>
                        {
                            scriptsCallbackInvoked = true;
                            File.WriteAllText(Path.Combine(scriptsDir, "RegionController.cs"), "public class RegionController {}");
                            return new UnityProjectScriptExportSummary
                            {
                                ExportedScriptFiles = 1,
                                ExportedAssemblies = 1,
                                OutputDirectory = scriptsDir
                            };
                        }
                    });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(scriptsCallbackInvoked);

                // 1) ProjectSettings
                var projVerPath = Path.Combine(tempRoot, "ProjectSettings", "ProjectVersion.txt");
                Assert.True(File.Exists(projVerPath));
                Assert.Contains("m_EditorVersion: 2022.3.40f1", File.ReadAllText(projVerPath));

                var tagMgrPath = Path.Combine(tempRoot, "ProjectSettings", "TagManager.asset");
                Assert.True(File.Exists(tagMgrPath));
                var tagMgrText = File.ReadAllText(tagMgrPath);
                Assert.StartsWith("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n", tagMgrText);
                Assert.Contains("Layer_23", tagMgrText);
                Assert.Contains("Tag_20001", tagMgrText);

                // 2) Folder .meta files
                foreach (var folder in new[] { "Scenes", "Materials", "Meshes", "Textures", "Scripts" })
                {
                    var folderMeta = Path.Combine(tempRoot, "Assets", folder + ".meta");
                    Assert.True(File.Exists(folderMeta), $"Missing folder meta: {folderMeta}");
                    Assert.Contains("folderAsset: yes", File.ReadAllText(folderMeta));
                }

                // 3) .unity scene YAML fidelity
                Assert.True(File.Exists(result.SceneFilePath));
                Assert.True(File.Exists(result.SceneFilePath + ".meta"));
                var sceneText = File.ReadAllText(result.SceneFilePath);
                Assert.StartsWith("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n", sceneText);
                Assert.Contains("--- !u!29 &1", sceneText);
                Assert.Contains("--- !u!104 &2", sceneText);
                Assert.Contains("--- !u!157 &3", sceneText);
                Assert.Contains("--- !u!196 &4", sceneText);
                Assert.Contains("m_IsActive: 0", sceneText); // Inactive GameObject preserved!
                Assert.Contains("m_Layer: 23", sceneText);
                Assert.Contains("m_CastShadows: 3", sceneText); // ShadowsOnly preserved!
                Assert.Contains("m_Bits: 8388609", sceneText); // Camera CullingMask 0x00800001 preserved!
                Assert.Contains("LODGroup:", sceneText);
                Assert.Contains("BoxCollider:", sceneText);
                Assert.Contains("MeshCollider:", sceneText);
                Assert.Contains("m_Convex: 1", sceneText);
                Assert.Contains("# OriginalComponent: Terrain", sceneText);
                Assert.Contains("regionTitle: \"Harbor: North #2\"", sceneText); // YAML-escaped string!
                Assert.Contains("spawnPoint: {x: 4, y: 1, z: -2}", sceneText);

                // 4) Exact deterministic MonoScript GUID parity with shared Block 1 contract!
                var expectedScriptGuid = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes("unity-script|assembly-csharp|Game.World|RegionController")).AsSpan(0, 16))
                    .ToLowerInvariant();
                Assert.Equal(expectedScriptGuid, UnityProjectExporterService.ComputeMonoScriptGuid("Assembly-CSharp.dll", "Game.World", "RegionController"));
                Assert.Equal(expectedScriptGuid, UnityProjectExporterService.ComputeMonoScriptGuid("  assembly-csharp  ", "Game.World", "RegionController"));
                Assert.Contains($"m_Script: {{fileID: 11500000, guid: {expectedScriptGuid}, type: 3}}", sceneText);

                // Verify MonoBehaviour PPtr to exported Material was rewritten with its exact GUID!
                var expectedMatAGuid = UnityProjectExporterService.ComputeUnityAssetGuid("material", "level0", matA.m_PathID, "Mat_Brick");
                Assert.Contains($"overrideMaterial: {{fileID: 2100000, guid: {expectedMatAGuid}, type: 2}}", sceneText);

                // 5) Material .mat + .mat.meta
                var matAPath = Path.Combine(tempRoot, "Assets", "Materials", "Mat_Brick.mat");
                Assert.True(File.Exists(matAPath));
                Assert.True(File.Exists(matAPath + ".meta"));
                var matAText = File.ReadAllText(matAPath);
                Assert.StartsWith("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n", matAText);
                Assert.Contains("# OriginalShader: Custom/StylizedLit", matAText);
                Assert.Contains("_NORMALMAP", matAText);
                Assert.Contains("_Smoothness: 0.35", matAText);
                var expectedAlbedoGuid = UnityProjectExporterService.ComputeUnityAssetGuid("texture2d", "level0", albedoTex.m_PathID, "T_Brick_Albedo");
                var expectedNormalGuid = UnityProjectExporterService.ComputeUnityAssetGuid("texture2d", "level0", normalTex.m_PathID, "T_Brick_Normal");
                Assert.Contains($"guid: {expectedAlbedoGuid}", matAText);
                Assert.Contains($"guid: {expectedNormalGuid}", matAText);

                // 6) Multi-submesh .obj + companion .mtl + .obj.meta (Unity 6 internalIDToNameTable + legacy recycle table)
                var meshPath = Path.Combine(tempRoot, "Assets", "Meshes", "SM_Tower.obj");
                var mtlPath = Path.Combine(tempRoot, "Assets", "Meshes", "SM_Tower.mtl");
                Assert.True(File.Exists(meshPath));
                Assert.True(File.Exists(meshPath + ".meta"));
                Assert.True(File.Exists(mtlPath));
                Assert.True(File.Exists(mtlPath + ".meta"));
                var objText = File.ReadAllText(meshPath);
                Assert.Contains("mtllib SM_Tower.mtl", objText);
                Assert.Contains("g SM_Tower", objText);
                Assert.DoesNotContain("g submesh_", objText);
                Assert.Contains("usemtl mat_0", objText);
                Assert.Contains("usemtl mat_1", objText);
                var mtlText = File.ReadAllText(mtlPath);
                Assert.Contains("newmtl mat_0", mtlText);
                Assert.Contains("newmtl mat_1", mtlText);
                var meshMetaText = File.ReadAllText(meshPath + ".meta");
                Assert.Contains("internalIDToNameTable:", meshMetaText);
                Assert.Contains("43: 4300000", meshMetaText);
                Assert.Contains("second: SM_Tower", meshMetaText);
                Assert.Contains("4300000: SM_Tower", meshMetaText);

                // 7) Textures .png + .png.meta (normal map has textureType: 1, albedo has textureType: 0)
                var albedoMeta = File.ReadAllText(Path.Combine(tempRoot, "Assets", "Textures", "T_Brick_Albedo.png.meta"));
                var normalMeta = File.ReadAllText(Path.Combine(tempRoot, "Assets", "Textures", "T_Brick_Normal.png.meta"));
                Assert.Contains("textureType: 0", albedoMeta);
                Assert.Contains("sRGBTexture: 1", albedoMeta);
                Assert.Contains("textureType: 1", normalMeta);
                Assert.Contains("sRGBTexture: 0", normalMeta);

                // 8) ExportManifest.json
                Assert.NotNull(result.ManifestPath);
                Assert.True(File.Exists(result.ManifestPath!));
                using var manifestDoc = JsonDocument.Parse(File.ReadAllText(result.ManifestPath!));
                var rootEl = manifestDoc.RootElement;
                Assert.Equal("2022.3.40f1", rootEl.GetProperty("unityVersion").GetString());
                var stats = rootEl.GetProperty("statistics");
                Assert.Equal(6, stats.GetProperty("gameObjects").GetInt32());
                Assert.Equal(5, stats.GetProperty("activeGameObjects").GetInt32());
                Assert.Equal(1, stats.GetProperty("inactiveGameObjects").GetInt32());
                Assert.Equal(1, stats.GetProperty("shadowsOnlyRenderers").GetInt32());
                Assert.Equal(1, stats.GetProperty("cameras").GetInt32());
                Assert.Equal(1, stats.GetProperty("lights").GetInt32());
                Assert.Equal(1, stats.GetProperty("lodGroups").GetInt32());
                Assert.Equal(2, stats.GetProperty("colliders").GetInt32());
                Assert.Equal(1, stats.GetProperty("terrains").GetInt32());
                Assert.Equal(1, stats.GetProperty("monoBehaviours").GetInt32());
                Assert.Equal("Layer_23", rootEl.GetProperty("layersUsed").GetProperty("23").GetString());
                var scriptsArr = rootEl.GetProperty("scriptsReferenced").EnumerateArray().ToList();
                Assert.Single(scriptsArr);
                Assert.Equal(expectedScriptGuid, scriptsArr[0].GetProperty("deterministicGuid").GetString());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 5. Path-Traversal & Windows Reserved Filename Safety
        // =====================================================================

        [Fact]
        public void UnityProjectExporter_SanitizesPathTraversalAndReservedWindowsDeviceNames()
        {
            Assert.Equal("evil", UnityProjectExporterService.SanitizeFileName("../../evil"));
            Assert.Equal("_CON", UnityProjectExporterService.SanitizeFileName("CON"));
            Assert.Equal("_NUL", UnityProjectExporterService.SanitizeFileName("NUL"));
            Assert.Equal("_COM1.txt", UnityProjectExporterService.SanitizeFileName("..\\COM1.txt"));

            var tempRoot = CreateTempDirectory("unity_proj_safety");
            var projectDir = Path.Combine(tempRoot, "SafeProject");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var shader = CreateSyntheticShader(sf, 10, "Standard");
                var tex = CreateSolidRgbaTexture2D(sf, 20, "..\\..\\COM1", 100, 120, 140, 255);
                var mat = CreateSyntheticMaterial(sf, 30, "NUL", shader, texEnvs: new[] { ("_MainTex", tex) });
                var mesh = CreateSyntheticTwoSubmeshMesh(sf, 40, "CON");

                var go = CreateSyntheticGameObject(sf, 100, "../../Escape", true, 0);
                var tr = CreateSyntheticTransform(sf, 101, go, null, Vector3.Zero);
                var mf = CreateSyntheticMeshFilter(sf, 102, go, 0, mesh.m_PathID);
                var mr = CreateSyntheticMeshRenderer(sf, 103, go, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                AttachComponents(go, tr, mf, mr);

                var exporter = new UnityProjectExporterService();
                var result = exporter.ExportSceneToUnityProject(
                    new[] { go },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = projectDir,
                        SceneName = "../../evil",
                        WriteManifest = true
                    });

                Assert.True(result.Success, result.ErrorMessage);
                var fullProjectPrefix = Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                // Ensure NOTHING escaped outside projectDir!
                var allFilesInTemp = Directory.GetFiles(tempRoot, "*", SearchOption.AllDirectories);
                Assert.NotEmpty(allFilesInTemp);
                foreach (var f in allFilesInTemp)
                {
                    Assert.StartsWith(fullProjectPrefix, Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase);
                }

                Assert.True(File.Exists(Path.Combine(projectDir, "Assets", "Meshes", "_CON.obj")));
                Assert.True(File.Exists(Path.Combine(projectDir, "Assets", "Materials", "_NUL.mat")));
                Assert.True(File.Exists(Path.Combine(projectDir, "Assets", "Textures", "_COM1.png")));
                Assert.True(File.Exists(Path.Combine(projectDir, "Assets", "Scenes", "evil.unity")));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 6. Streaming Performance Benchmark: 2,000-GameObject Scene (< 2s)
        // =====================================================================

        [Fact]
        public void UnityProjectExporter_2000GameObjectScene_StreamsUnderTwoSeconds()
        {
            var tempRoot = CreateTempDirectory("unity_proj_perf");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var shader = CreateSyntheticShader(sf, 10, "Standard");
                var sharedMat = CreateSyntheticMaterial(sf, 30, "Mat_SharedProp", shader);
                var sharedMesh = CreateSyntheticTwoSubmeshMesh(sf, 40, "SM_SharedProp");

                var rootGo = CreateSyntheticGameObject(sf, 1, "PerfRoot", true, 0);
                var rootTr = CreateSyntheticTransform(sf, 2, rootGo, null, Vector3.Zero);

                const int childCount = 2000;
                var childPtrs = new PPtr<Transform>[childCount];
                for (int i = 0; i < childCount; i++)
                {
                    long baseId = 1000L + i * 10L;
                    var go = CreateSyntheticGameObject(sf, baseId, $"Prop_{i:D4}", isActive: (i % 10) != 0, layer: i % 3 == 0 ? 8 : 0);
                    var tr = CreateSyntheticTransform(sf, baseId + 1, go, rootTr, new Vector3(i * 1.5f, 0f, (i % 50) * 2f));
                    var mf = CreateSyntheticMeshFilter(sf, baseId + 2, go, 0, sharedMesh.m_PathID);
                    var mr = CreateSyntheticMeshRenderer(sf, baseId + 3, go, true, new[] { CreatePPtr<Material>(sf, 0, sharedMat.m_PathID) });
                    AttachComponents(go, tr, mf, mr);
                    childPtrs[i] = CreatePPtr<Transform>(sf, 0, tr.m_PathID);
                }

                rootTr.m_Children = childPtrs;
                AttachComponents(rootGo, rootTr);

                var exporter = new UnityProjectExporterService();
                var sw = Stopwatch.StartNew();
                var result = exporter.ExportSceneToUnityProject(
                    new[] { rootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = tempRoot,
                        SceneName = "LargeScene2000",
                        WriteManifest = true
                    });
                sw.Stop();

                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected < 2000 ms for 2,000 GameObjects, took {sw.ElapsedMilliseconds} ms.");
                Assert.NotNull(result.Manifest);
                Assert.Equal(2001, result.Manifest!.Statistics.GameObjects);
                Assert.Equal(1, result.Manifest.Statistics.UniqueMeshes);
                Assert.Equal(1, result.Manifest.Statistics.UniqueMaterials);
                Assert.Equal(200, result.Manifest.Statistics.InactiveGameObjects);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ExportSceneToUnityProject_WithCSharpProjectExporterCallback_MatchesMonoScriptGuidBitForBit()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-crossblock-bridge", "2022.3.40f1");

            var script = CreateSyntheticObject<MonoScript>(sf, 500, ClassIDType.MonoScript);
            script.m_AssemblyName = "Assembly-CSharp.dll";
            script.m_Namespace = "Game.Bridge";
            script.m_ClassName = "BridgeBehaviour";

            var rootGo = CreateSyntheticGameObject(sf, 100, "BridgeRoot", isActive: true, layer: 0);
            var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);
            var mb = CreateSyntheticObject<MonoBehaviour>(sf, 102, ClassIDType.MonoBehaviour);
            mb.m_GameObject = CreatePPtr<GameObject>(sf, 0, rootGo.m_PathID);
            mb.m_Enabled = 1;
            mb.m_Script = CreatePPtr<MonoScript>(sf, 0, script.m_PathID);
            mb.m_Name = "BridgeBehaviour";
            AttachComponents(rootGo, rootTr, mb);
            rootGo.m_Transform = rootTr;

            var tempRoot = CreateTempDirectory("CrossBlockBridge");
            try
            {
                // Prepare a dump.cs source containing the exact same class so CSharpProjectExporterService
                // emits Assets/Scripts/.../BridgeBehaviour.cs + BridgeBehaviour.cs.meta.
                var dumpDir = Path.Combine(tempRoot, "Il2CppSource");
                Directory.CreateDirectory(dumpDir);
                var dumpCsPath = Path.Combine(dumpDir, "dump.cs");
                File.WriteAllText(dumpCsPath,
                    "// Image 0: Assembly-CSharp.dll - 0\n" +
                    "// Namespace: Game.Bridge\n" +
                    "public class BridgeBehaviour : MonoBehaviour // TypeDefIndex: 1\n" +
                    "{\n" +
                    "    public float speed;\n" +
                    "}\n",
                    new UTF8Encoding(false));

                var projectOut = Path.Combine(tempRoot, "UnityOut");
                var exporter = new UnityProjectExporterService();
                var scriptExporter = new CSharpProjectExporterService();

                var result = exporter.ExportSceneToUnityProject(
                    new[] { rootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = projectOut,
                        SceneName = "BridgeScene",
                        IncludeScriptsCallback = (scriptsDir, ct) =>
                        {
                            var sr = scriptExporter.Export(new CSharpProjectExportOptions
                            {
                                OutputDirectory = scriptsDir,
                                ProjectName = "BridgeScene",
                                LayoutMode = CSharpProjectLayoutMode.UnityAssetsScripts,
                                GenerateUnityMetaFiles = true,
                                GenerateAsmdefFiles = true,
                                CopyReferenceDlls = false,
                                DumpCsPath = dumpCsPath
                            }, cancellationToken: ct);
                            return new UnityProjectScriptExportSummary
                            {
                                ExportedScriptFiles = sr.TotalTypesExported,
                                ExportedAssemblies = sr.GameAssembliesCount,
                                OutputDirectory = scriptsDir
                            };
                        }
                    });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.NotNull(result.Manifest?.ScriptExportSummary);
                Assert.True(result.Manifest!.ScriptExportSummary!.ExportedScriptFiles >= 1);

                var expectedGuid = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp.dll", "Game.Bridge", "BridgeBehaviour");
                Assert.Equal(expectedGuid, UnityProjectExporterService.ComputeMonoScriptGuid("Assembly-CSharp", "Game.Bridge", "BridgeBehaviour"));

                var sceneYaml = File.ReadAllText(result.SceneFilePath);
                Assert.Contains($"m_Script: {{fileID: 11500000, guid: {expectedGuid}, type: 3}}", sceneYaml, StringComparison.Ordinal);

                var metaFiles = Directory.GetFiles(Path.Combine(projectOut, "Assets", "Scripts"), "BridgeBehaviour.cs.meta", SearchOption.AllDirectories);
                Assert.Single(metaFiles);
                var scriptMeta = File.ReadAllText(metaFiles[0]);
                Assert.Contains($"guid: {expectedGuid}", scriptMeta, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void TerrainDataBinaryParsing_WithOpaqueDetailDatabaseAndPreloadShaders_RecoversHeightmapAndHolesFromTail()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "sharedassets2.assets", "6000.3.2f1");

            // Build a 33x33 modern Unity 6000.3 TerrainData with opaque non-empty DetailDatabase bytes
            // (simulating real Unity grass/detail prototypes without TypeTree) + m_Holes +
            // m_PrecomputedError + m_MinMaxPatchHeights + m_PreloadShaders!
            const int res = 33;
            var heights = new short[res * res];
            for (int i = 0; i < heights.Length; i++) heights[i] = (short)((i * 19) % 30000);
            var holes = new byte[(res - 1) * (res - 1)];
            Array.Fill(holes, (byte)1);
            holes[10] = 0;

            byte[] payload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteAlignedString(bw, "RealWorldDetailTerrain");
                // SplatDatabase (2018.3+)
                bw.Write(1); bw.Write(0); bw.Write(401L); // 1 TerrainLayer
                bw.Write(1); bw.Write(0); bw.Write(402L); // 1 AlphaTexture
                bw.Write(512); // m_AlphamapResolution
                bw.Write(1024); // m_BaseMapResolution

                // Opaque DetailDatabase blob with 5 grass prototypes (breaks naive sequential DetailDatabase reader!)
                bw.Write(0); // m_Patches
                bw.Write(5); // m_DetailPrototypes = 5!
                bw.Write(new byte[256]); // arbitrary opaque DetailDatabase payload (4-byte aligned)

                // Heightmap (2019.3–6000.4)
                bw.Write(heights.Length);
                foreach (var h in heights) bw.Write(h);
                AlignBinaryWriter(bw);
                bw.Write(holes.Length);
                bw.Write(holes);
                AlignBinaryWriter(bw);
                bw.Write(0); // m_HolesLOD
                bw.Write(true); bw.Write(new byte[3]); // m_EnableHolesTextureCompression + align
                bw.Write(4); bw.Write(0.1f); bw.Write(0.2f); bw.Write(0.3f); bw.Write(0.4f); // m_PrecomputedError
                bw.Write(8); for (int i = 0; i < 8; i++) bw.Write(0.05f * i); // m_MinMaxPatchHeights
                bw.Write(res); // m_Resolution
                bw.Write(5);   // m_Levels
                bw.Write(10f); bw.Write(600f); bw.Write(10f); // m_Scale

                // m_PreloadShaders (5 shader PPtrs)
                bw.Write(5);
                for (int i = 0; i < 5; i++) { bw.Write(0); bw.Write(1000L + i); }

                payload = ms.ToArray();
            }

            var td = Assert.IsType<TerrainData>(CreateObjectViaAssetsManager(sf, ClassIDType.TerrainData, 9901, payload, typeTree: null));
            Assert.False(td.IsTruncated);
            Assert.True(td.HasValidHeightmap);
            Assert.Equal("RealWorldDetailTerrain", td.m_Name);
            Assert.Single(td.m_SplatDatabase.m_TerrainLayers);
            Assert.Single(td.m_SplatDatabase.m_AlphaTextures);
            Assert.Equal(res, td.m_Heightmap.EffectiveWidth);
            Assert.Equal(res, td.m_Heightmap.EffectiveHeight);
            Assert.Equal(heights.Length, td.m_Heightmap.m_Heights.Length);
            Assert.Equal(heights[55], td.m_Heightmap.m_Heights[55]);
            Assert.Equal(holes.Length, td.m_Heightmap.m_Holes.Length);
            Assert.Equal(0, td.m_Heightmap.m_Holes[10]);
            Assert.Equal(600f, td.m_Heightmap.m_Scale.Y);
        }

        // =====================================================================
        // Synthetic Fixture & Binary / TypeTree Helpers
        // =====================================================================

        private static byte[] BuildModernTerrainDataBinary(
            string name,
            long[] terrainLayerPathIds,
            long[] alphaTexPathIds,
            short[] heights,
            byte[] holes,
            int resolution,
            Vector3 scale,
            bool includeDetailScatterMode)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            WriteAlignedString(bw, name);

            // SplatDatabase (2018.3+: no legacy m_ColorSpace/m_MaterialRequiresMetallic)
            bw.Write(terrainLayerPathIds.Length);
            foreach (var id in terrainLayerPathIds) { bw.Write(0); bw.Write(id); }
            bw.Write(alphaTexPathIds.Length);
            foreach (var id in alphaTexPathIds) { bw.Write(0); bw.Write(id); }
            bw.Write(64); // m_AlphamapResolution
            bw.Write(64); // m_BaseMapResolution

            // Empty DetailDatabase
            bw.Write(0); // m_Patches
            bw.Write(0); // m_DetailPrototypes
            bw.Write(0); // m_PatchCount
            bw.Write(0); // m_PatchSamples
            bw.Write(0); // m_RandomRotations
            bw.Write(0.7f); bw.Write(0.7f); bw.Write(0.7f); bw.Write(1f); // WavingGrassTint
            bw.Write(0.5f); bw.Write(0.5f); bw.Write(0.5f); // Strength/Amount/Speed
            bw.Write(0); // m_TreeInstances
            bw.Write(0); // m_TreePrototypes
            bw.Write(0); // m_PreloadTextureAtlasData
            if (includeDetailScatterMode)
            {
                bw.Write(0); // m_DetailScatterMode (2022.2+)
            }

            // Heightmap (2019.3+)
            bw.Write(heights.Length);
            foreach (var h in heights) bw.Write(h);
            AlignBinaryWriter(bw);

            bw.Write(holes.Length);
            bw.Write(holes);
            AlignBinaryWriter(bw);

            bw.Write(0); // m_HolesLOD
            bw.Write(true); bw.Write(new byte[3]); // m_EnableHolesTextureCompression + align
            bw.Write(0); // m_PrecomputedError (vector<float>)
            bw.Write(0); // m_MinMaxPatchHeights (vector<float>)
            bw.Write(resolution);
            bw.Write(1); // m_Levels
            bw.Write(scale.X); bw.Write(scale.Y); bw.Write(scale.Z);
            bw.Write(0); // m_PreloadShaders (vector<PPtr<Shader>>)

            return ms.ToArray();
        }

        private static byte[] BuildLegacyTerrainDataBinary(
            string name,
            long[] splatDiffusePathIds,
            long[] alphaTexPathIds,
            short[] heights,
            int width,
            int height,
            Vector3 scale)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            WriteAlignedString(bw, name);

            // Legacy SplatDatabase (< 2018.3)
            bw.Write(splatDiffusePathIds.Length);
            foreach (var id in splatDiffusePathIds)
            {
                bw.Write(0); bw.Write(id); // texture
                bw.Write(0); bw.Write(0L); // normalMap
                bw.Write(15f); bw.Write(15f); // tileSize
                bw.Write(0f); bw.Write(0f);   // tileOffset
                bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f); // specularMetallic
                bw.Write(0.2f); // smoothness
            }
            bw.Write(alphaTexPathIds.Length);
            foreach (var id in alphaTexPathIds) { bw.Write(0); bw.Write(id); }
            bw.Write(64); bw.Write(64); bw.Write(1);
            bw.Write(false); bw.Write(false); bw.Write(new byte[2]);

            // Empty DetailDatabase (pre-2022.2)
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(0.7f); bw.Write(0.7f); bw.Write(0.7f); bw.Write(1f);
            bw.Write(0.5f); bw.Write(0.5f); bw.Write(0.5f);
            bw.Write(0); bw.Write(0); bw.Write(0);

            // Legacy Heightmap (< 2019.3)
            bw.Write(heights.Length);
            foreach (var h in heights) bw.Write(h);
            AlignBinaryWriter(bw);
            bw.Write(0); // m_PrecomputedError
            bw.Write(0); // m_MinMaxPatchHeights
            bw.Write(width);
            bw.Write(height);
            bw.Write(1f); // m_Thickness
            bw.Write(1);  // m_Levels
            bw.Write(scale.X); bw.Write(scale.Y); bw.Write(scale.Z);

            return ms.ToArray();
        }

        private static TypeTree BuildTerrainTypeTree()
        {
            var nodes = new List<TypeTreeNode>();
            void Add(int level, string type, string name, int meta = 0) =>
                nodes.Add(new TypeTreeNode(type, name, level, false) { m_MetaFlag = meta });
            void AddPPtr(int level, string ptrType, string name)
            {
                Add(level, ptrType, name);
                Add(level + 1, "int", "m_FileID");
                Add(level + 1, "SInt64", "m_PathID");
            }

            Add(0, "Terrain", "Base");
            AddPPtr(1, "PPtr<GameObject>", "m_GameObject");
            Add(1, "UInt8", "m_Enabled", 0x4000);
            AddPPtr(1, "PPtr<TerrainData>", "m_TerrainData");
            Add(1, "float", "m_TreeDistance");
            Add(1, "float", "m_TreeBillboardDistance");
            Add(1, "float", "m_TreeCrossFadeLength");
            Add(1, "int", "m_TreeMaximumFullLODCount");
            Add(1, "float", "m_DetailObjectDistance");
            Add(1, "float", "m_DetailObjectDensity");
            Add(1, "float", "m_HeightmapPixelError");
            Add(1, "float", "m_SplatMapDistance");
            Add(1, "int", "m_HeightmapMaximumLOD");
            Add(1, "int", "m_ShadowCastingMode");
            Add(1, "bool", "m_DrawHeightmap");
            Add(1, "bool", "m_DrawInstanced");
            Add(1, "bool", "m_DrawTreesAndFoliage");
            Add(1, "bool", "m_StaticShadowCaster", 0x4000);
            Add(1, "int", "m_ReflectionProbeUsage");
            AddPPtr(1, "PPtr<Material>", "m_MaterialTemplate");
            return new TypeTree { m_Nodes = nodes };
        }

        private static TypeTree BuildTerrainLayerTypeTree()
        {
            var nodes = new List<TypeTreeNode>();
            void Add(int level, string type, string name, int meta = 0) =>
                nodes.Add(new TypeTreeNode(type, name, level, false) { m_MetaFlag = meta });
            void AddPPtr(int level, string name)
            {
                Add(level, "PPtr<Texture2D>", name);
                Add(level + 1, "int", "m_FileID");
                Add(level + 1, "SInt64", "m_PathID");
            }
            void AddVec2(int level, string name)
            {
                Add(level, "Vector2f", name);
                Add(level + 1, "float", "x");
                Add(level + 1, "float", "y");
            }
            void AddVec4(int level, string name)
            {
                Add(level, "Vector4f", name);
                Add(level + 1, "float", "x");
                Add(level + 1, "float", "y");
                Add(level + 1, "float", "z");
                Add(level + 1, "float", "w");
            }

            Add(0, "TerrainLayer", "Base");
            Add(1, "string", "m_Name", 0x4000);
            AddPPtr(1, "m_DiffuseTexture");
            AddPPtr(1, "m_NormalMapTexture");
            AddPPtr(1, "m_MaskMapTexture");
            AddVec2(1, "m_TileSize");
            AddVec2(1, "m_TileOffset");
            Add(1, "ColorRGBA", "m_Specular");
            Add(2, "float", "r"); Add(2, "float", "g"); Add(2, "float", "b"); Add(2, "float", "a");
            Add(1, "float", "m_Metallic");
            Add(1, "float", "m_Smoothness");
            Add(1, "float", "m_NormalScale");
            AddVec4(1, "m_DiffuseRemapMin");
            AddVec4(1, "m_DiffuseRemapMax");
            return new TypeTree { m_Nodes = nodes };
        }

        private static TypeTree BuildModernTerrainDataTypeTree()
        {
            var nodes = new List<TypeTreeNode>();
            void Add(int level, string type, string name, int meta = 0) =>
                nodes.Add(new TypeTreeNode(type, name, level, false) { m_MetaFlag = meta });
            void AddPPtrVec(int level, string name, string ptrType)
            {
                Add(level, "vector", name);
                Add(level + 1, "Array", "Array");
                Add(level + 2, "int", "size");
                Add(level + 2, ptrType, "data");
                Add(level + 3, "int", "m_FileID");
                Add(level + 3, "SInt64", "m_PathID");
            }

            Add(0, "TerrainData", "Base");
            Add(1, "string", "m_Name", 0x4000);

            Add(1, "SplatDatabase", "m_SplatDatabase");
            AddPPtrVec(2, "m_TerrainLayers", "PPtr<TerrainLayer>");
            AddPPtrVec(2, "m_AlphaTextures", "PPtr<Texture2D>");
            Add(2, "int", "m_AlphamapResolution");
            Add(2, "int", "m_BaseMapResolution");

            // DetailDatabase layout matching BuildModernTerrainDataBinary (will be skipped by streaming parser!)
            Add(1, "DetailDatabase", "m_DetailDatabase");
            Add(2, "vector", "m_Patches"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "int", "data");
            Add(2, "vector", "m_DetailPrototypes"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "int", "data");
            Add(2, "int", "m_PatchCount");
            Add(2, "int", "m_PatchSamples");
            Add(2, "vector", "m_RandomRotations"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "Vector3f", "data"); Add(5, "float", "x"); Add(5, "float", "y"); Add(5, "float", "z");
            Add(2, "ColorRGBA", "WavingGrassTint"); Add(3, "float", "r"); Add(3, "float", "g"); Add(3, "float", "b"); Add(3, "float", "a");
            Add(2, "float", "m_WavingGrassStrength");
            Add(2, "float", "m_WavingGrassAmount");
            Add(2, "float", "m_WavingGrassSpeed");
            Add(2, "vector", "m_TreeInstances"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "int", "data");
            Add(2, "vector", "m_TreePrototypes"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "int", "data");
            AddPPtrVec(2, "m_PreloadTextureAtlasData", "PPtr<Texture2D>");
            Add(2, "int", "m_DetailScatterMode");

            Add(1, "Heightmap", "m_Heightmap");
            Add(2, "vector", "m_Heights"); Add(3, "Array", "Array", 0x4000); Add(4, "int", "size"); Add(4, "SInt16", "data");
            Add(2, "vector", "m_Holes"); Add(3, "Array", "Array", 0x4000); Add(4, "int", "size"); Add(4, "UInt8", "data");
            Add(2, "vector", "m_HolesLOD"); Add(3, "Array", "Array", 0x4000); Add(4, "int", "size"); Add(4, "UInt8", "data");
            Add(2, "bool", "m_EnableHolesTextureCompression", 0x4000);
            Add(2, "vector", "m_PrecomputedError"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "float", "data");
            Add(2, "vector", "m_MinMaxPatchHeights"); Add(3, "Array", "Array"); Add(4, "int", "size"); Add(4, "float", "data");
            Add(2, "int", "m_Resolution");
            Add(2, "int", "m_Levels");
            Add(2, "Vector3f", "m_Scale"); Add(3, "float", "x"); Add(3, "float", "y"); Add(3, "float", "z");
            AddPPtrVec(1, "m_PreloadShaders", "PPtr<Shader>");

            return new TypeTree { m_Nodes = nodes };
        }

        private static void WriteAlignedString(BinaryWriter bw, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            bw.Write(bytes.Length);
            bw.Write(bytes);
            AlignBinaryWriter(bw);
        }

        private static void AlignBinaryWriter(BinaryWriter bw)
        {
            var mod = bw.BaseStream.Position % 4;
            if (mod != 0)
            {
                for (int i = 0; i < 4 - mod; i++) bw.Write((byte)0);
            }
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager am, string fileName, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = am;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.reader = new FileReader(fileName, new MemoryStream(Array.Empty<byte>()));
            sf.m_TargetPlatform = BuildTarget.StandaloneWindows64;
            sf.m_Externals = new List<FileIdentifier>();
            sf.m_Objects = new List<ObjectInfo>();
            sf.m_Types = new List<SerializedType>();
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            sf.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.LargeFilesSupport };
            sf.SetVersion(unityVersion);
            typeof(SerializedFile)
                .GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sf, new object());
            lock (am.loadLock)
            {
                am.assetsFileList.Add(sf);
            }
            return sf;
        }

        private static AssetStudio.Object CreateObjectViaAssetsManager(
            SerializedFile sf,
            ClassIDType classId,
            long pathId,
            byte[] payload,
            TypeTree? typeTree = null)
        {
            using var ms = new MemoryStream(payload);
            using var fr = new FileReader(sf.fileName, ms) { Endian = EndianType.LittleEndian };
            var info = new ObjectInfo
            {
                m_PathID = pathId,
                byteStart = 0,
                byteSize = (uint)payload.Length,
                typeID = (int)classId,
                classID = (int)classId,
                serializedType = typeTree != null ? new SerializedType { classID = (int)classId, m_Type = typeTree } : null
            };
            var objReader = new ObjectReader(fr, sf, info);
            return AssetsManager.CreateObjectFromReader(objReader);
        }

        private static T CreateSyntheticObject<T>(SerializedFile sf, long pathId, ClassIDType type) where T : AssetStudio.Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            obj.assetsFile = sf;
            obj.m_PathID = pathId;
            obj.type = type;
            obj.version = sf.version;
            obj.platform = BuildTarget.StandaloneWindows64;
            sf.AddObject(obj);
            sf.ObjectInfoDic[pathId] = new ObjectInfo { m_PathID = pathId, classID = (int)type, typeID = (int)type };
            return obj;
        }

        private static PPtr<T> CreatePPtr<T>(SerializedFile sf, int fileId, long pathId) where T : AssetStudio.Object
            => new PPtr<T>(fileId, pathId, sf);

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive, int layer, ushort tag = 0)
        {
            var go = CreateSyntheticObject<GameObject>(sf, pathId, ClassIDType.GameObject);
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Layer = layer;
            go.m_Tag = tag;
            go.m_Components = Array.Empty<PPtr<Component>>();
            return go;
        }

        private static Transform CreateSyntheticTransform(SerializedFile sf, long pathId, GameObject go, Transform? father, Vector3 pos)
        {
            var tr = CreateSyntheticObject<Transform>(sf, pathId, ClassIDType.Transform);
            tr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            tr.m_LocalPosition = pos;
            tr.m_LocalRotation = new Quaternion(0f, 0f, 0f, 1f);
            tr.m_LocalScale = Vector3.One;
            tr.m_Father = father != null ? CreatePPtr<Transform>(sf, 0, father.m_PathID) : CreatePPtr<Transform>(sf, 0, 0);
            tr.m_Children = Array.Empty<PPtr<Transform>>();
            return tr;
        }

        private static MeshFilter CreateSyntheticMeshFilter(SerializedFile sf, long pathId, GameObject go, int fileId, long meshPathId)
        {
            var mf = CreateSyntheticObject<MeshFilter>(sf, pathId, ClassIDType.MeshFilter);
            mf.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mf.m_Mesh = CreatePPtr<Mesh>(sf, fileId, meshPathId);
            return mf;
        }

        private static MeshRenderer CreateSyntheticMeshRenderer(
            SerializedFile sf,
            long pathId,
            GameObject go,
            bool enabled,
            PPtr<Material>[] materials,
            byte castShadows = Renderer.ShadowCastingModeOn)
        {
            var mr = CreateSyntheticObject<MeshRenderer>(sf, pathId, ClassIDType.MeshRenderer);
            mr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mr.m_Enabled = enabled;
            mr.m_CastShadows = castShadows;
            mr.m_ReceiveShadows = 1;
            mr.m_Materials = materials;
            mr.m_SubsetIndices = Array.Empty<uint>();
            return mr;
        }

        private static Terrain CreateSyntheticTerrain(SerializedFile sf, long pathId, GameObject go, TerrainData data)
        {
            var t = CreateSyntheticObject<Terrain>(sf, pathId, ClassIDType.Terrain);
            t.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            t.m_Enabled = 1;
            t.m_DrawHeightmap = true;
            t.m_DrawTreesAndFoliage = true;
            t.m_ShadowCastingMode = 2;
            t.m_TerrainData = CreatePPtr<TerrainData>(sf, 0, data.m_PathID);
            t.m_MaterialTemplate = CreatePPtr<Material>(sf, 0, 0);
            return t;
        }

        private static TerrainData CreateSyntheticTerrainData(
            SerializedFile sf,
            long pathId,
            string name,
            int width,
            int height,
            Vector3 scale,
            short[] heights,
            byte[]? holes = null)
        {
            var td = CreateSyntheticObject<TerrainData>(sf, pathId, ClassIDType.TerrainData);
            td.m_Name = name;
            td.m_SplatDatabase = new TerrainSplatDatabase();
            td.m_Heightmap = new TerrainHeightmap
            {
                m_Width = width,
                m_Height = height,
                m_Resolution = Math.Max(width, height),
                m_Scale = scale,
                m_Heights = heights,
                m_Holes = holes ?? Array.Empty<byte>()
            };
            return td;
        }

        private static TerrainLayer CreateSyntheticTerrainLayer(SerializedFile sf, long pathId, string name, Texture2D diffuse)
        {
            var layer = CreateSyntheticObject<TerrainLayer>(sf, pathId, ClassIDType.TerrainLayer);
            layer.m_Name = name;
            layer.m_DiffuseTexture = CreatePPtr<Texture2D>(sf, 0, diffuse.m_PathID);
            layer.m_NormalMapTexture = CreatePPtr<Texture2D>(sf, 0, 0);
            layer.m_MaskMapTexture = CreatePPtr<Texture2D>(sf, 0, 0);
            layer.m_TileSize = new Vector2(10f, 10f);
            layer.m_TileOffset = Vector2.Zero;
            return layer;
        }

        private static Shader CreateSyntheticShader(SerializedFile sf, long pathId, string shaderName)
        {
            var shader = CreateSyntheticObject<Shader>(sf, pathId, ClassIDType.Shader);
            shader.m_Name = shaderName;
            var parsed = (SerializedShader)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShader));
            parsed.m_Name = shaderName;
            parsed.m_SubShaders = Array.Empty<SerializedSubShader>();
            shader.m_ParsedForm = parsed;
            return shader;
        }

        private static Texture2D CreateSolidRgbaTexture2D(SerializedFile sf, long pathId, string name, byte r, byte g, byte b, byte a)
        {
            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < 4; i++)
            {
                pixels[i * 4 + 0] = r;
                pixels[i * 4 + 1] = g;
                pixels[i * 4 + 2] = b;
                pixels[i * 4 + 3] = a;
            }
            return CreateCustomRgbaTexture2D(sf, pathId, name, 2, 2, pixels);
        }

        private static Texture2D CreateCustomRgbaTexture2D(SerializedFile sf, long pathId, string name, int width, int height, byte[] rgbaPixels)
        {
            var tex = CreateSyntheticObject<Texture2D>(sf, pathId, ClassIDType.Texture2D);
            tex.m_Name = name;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.m_ImageCount = 1;
            tex.m_TextureDimension = 2;
            tex.image_data = new ResourceReader(rgbaPixels);
            return tex;
        }

        private static Material CreateSyntheticMaterial(
            SerializedFile sf,
            long pathId,
            string name,
            Shader? shader = null,
            (string Key, Texture2D Texture)[]? texEnvs = null,
            (string Key, Color Color)[]? colors = null,
            (string Key, float Value)[]? floats = null,
            int renderQueue = 2000,
            string[]? keywords = null,
            (string Key, string Value)[]? tags = null)
        {
            var mat = CreateSyntheticObject<Material>(sf, pathId, ClassIDType.Material);
            mat.m_Name = name;
            mat.m_Shader = shader != null ? CreatePPtr<Shader>(sf, 0, shader.m_PathID) : CreatePPtr<Shader>(sf, 0, 0);
            mat.m_Parent = CreatePPtr<Material>(sf, 0, 0);
            mat.m_CustomRenderQueue = renderQueue;
            mat.m_ValidKeywords = keywords ?? Array.Empty<string>();
            mat.m_InvalidKeywords = Array.Empty<string>();
            mat.disabledShaderPasses = Array.Empty<string>();
            mat.stringTagMap = tags != null
                ? tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToArray()
                : Array.Empty<KeyValuePair<string, string>>();
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs != null
                    ? texEnvs.Select(t => new KeyValuePair<string, UnityTexEnv>(
                        t.Key,
                        new UnityTexEnv(CreatePPtr<Texture>(sf, 0, t.Texture.m_PathID), new Vector2(1f, 1f), Vector2.Zero))).ToArray()
                    : Array.Empty<KeyValuePair<string, UnityTexEnv>>(),
                m_Ints = Array.Empty<KeyValuePair<string, int>>(),
                m_Floats = floats != null
                    ? floats.Select(f => new KeyValuePair<string, float>(f.Key, f.Value)).ToArray()
                    : Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = colors != null
                    ? colors.Select(c => new KeyValuePair<string, Color>(c.Key, c.Color)).ToArray()
                    : Array.Empty<KeyValuePair<string, Color>>()
            };
            return mat;
        }

        private static MonoScript CreateSyntheticMonoScript(SerializedFile sf, long pathId, string ns, string className, string assemblyName)
        {
            var ms = CreateSyntheticObject<MonoScript>(sf, pathId, ClassIDType.MonoScript);
            ms.m_Name = className;
            ms.m_Namespace = ns;
            ms.m_ClassName = className;
            ms.m_AssemblyName = assemblyName;
            return ms;
        }

        private static Mesh CreateSyntheticTwoSubmeshMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = CreateSyntheticObject<Mesh>(sf, pathId, ClassIDType.Mesh);
            mesh.m_Name = name;
            mesh.m_VertexCount = 4;
            mesh.m_Vertices = new float[]
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                1f, 1f, 0f,
                0f, 1f, 0f
            };
            mesh.m_Normals = new float[]
            {
                0f, 0f, -1f,
                0f, 0f, -1f,
                0f, 0f, -1f,
                0f, 0f, -1f
            };
            mesh.m_UV0 = new float[]
            {
                0f, 0f,
                1f, 0f,
                1f, 1f,
                0f, 1f
            };
            mesh.m_Indices = new List<uint> { 0, 1, 2, 0, 2, 3 };

            var sub0 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub0.firstByte = 0; sub0.indexCount = 3; sub0.topology = GfxPrimitiveType.Triangles; sub0.firstVertex = 0; sub0.vertexCount = 4;
            var sub1 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub1.firstByte = 6; sub1.indexCount = 3; sub1.topology = GfxPrimitiveType.Triangles; sub1.firstVertex = 0; sub1.vertexCount = 4;
            mesh.m_SubMeshes = new[] { sub0, sub1 };

            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            return mesh;
        }

        private static void AttachComponents(GameObject go, params AssetStudio.Object[] components)
        {
            go.m_Components = components.Select(c => CreatePPtr<Component>(go.assetsFile, 0, c.m_PathID)).ToArray();
        }

        private static JsonDocument ParseGlbJson(string glbPath)
        {
            var bytes = File.ReadAllBytes(glbPath);
            Assert.True(bytes.Length >= 20);
            Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            var jsonChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            var jsonChunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            Assert.Equal(0x4E4F534Au, jsonChunkType);
            var jsonText = Encoding.UTF8.GetString(bytes, 20, (int)jsonChunkLength).TrimEnd('\0', ' ');
            return JsonDocument.Parse(jsonText);
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
