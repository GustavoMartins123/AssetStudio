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
using System.Text;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class SceneExportFidelityTests
    {
        // =====================================================================
        // 1. Renderer binary parsing: m_CastShadows & m_ReceiveShadows
        // =====================================================================

        [Theory]
        [InlineData("2022.3.40f1", (byte)3, (byte)0)]
        [InlineData("2022.3.40f1", (byte)0, (byte)1)]
        [InlineData("2022.3.40f1", (byte)2, (byte)1)]
        [InlineData("5.3.8f1", (byte)3, (byte)1)]
        [InlineData("4.7.2f1", (byte)0, (byte)0)]
        [InlineData("4.7.2f1", (byte)1, (byte)1)]
        public void RendererBinaryParsing_ExposesCastShadowsAndReceiveShadowsAcrossUnityVersions(
            string unityVersion,
            byte expectedCastShadows,
            byte expectedReceiveShadows)
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-renderer-test", unityVersion);

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                // Component.m_GameObject PPtr (fileID=0, pathID=10; 64-bit pathID because LargeFilesSupport)
                bw.Write(0);
                bw.Write(10L);

                if (sf.version[0] < 5)
                {
                    bw.Write(true); // m_Enabled
                    bw.Write(expectedCastShadows != 0); // m_CastShadows (bool)
                    bw.Write(expectedReceiveShadows != 0); // m_ReceiveShadows (bool)
                    bw.Write((byte)255); // m_LightmapIndex
                    // m_LightmapTilingOffset (Vector4)
                    bw.Write(1f); bw.Write(1f); bw.Write(0f); bw.Write(0f);
                    // m_Materials (empty)
                    bw.Write(0);
                    // m_SubsetIndices (empty)
                    bw.Write(0);
                    // m_StaticBatchRoot PPtr
                    bw.Write(0); bw.Write(0L);
                    // m_UseLightProbes + align
                    bw.Write(false); bw.Write(new byte[3]);
                    // m_LightProbeAnchor PPtr
                    bw.Write(0); bw.Write(0L);
                    // m_SortingLayerID + m_SortingOrder + align
                    bw.Write(0u); bw.Write((short)0); bw.Write((short)0);
                }
                else if (sf.version[0] == 5 && sf.version[1] < 4)
                {
                    bw.Write(true); // m_Enabled
                    bw.Write(new byte[3]); // align
                    bw.Write(expectedCastShadows); // m_CastShadows (byte)
                    bw.Write(expectedReceiveShadows != 0); // m_ReceiveShadows (bool)
                    bw.Write(new byte[2]); // align
                    bw.Write((ushort)65535); // m_LightmapIndex
                    bw.Write((ushort)65535); // m_LightmapIndexDynamic
                    // m_LightmapTilingOffset + Dynamic
                    for (int i = 0; i < 8; i++) bw.Write(0f);
                    // m_Materials (empty)
                    bw.Write(0);
                    // m_SubsetIndices (empty)
                    bw.Write(0);
                    // m_StaticBatchRoot PPtr
                    bw.Write(0); bw.Write(0L);
                    // m_UseLightProbes + align
                    bw.Write(false); bw.Write(new byte[3]);
                    // m_ReflectionProbeUsage (int32)
                    bw.Write(1);
                    // m_LightProbeAnchor PPtr
                    bw.Write(0); bw.Write(0L);
                    // m_SortingLayerID + m_SortingOrder + align
                    bw.Write(0u); bw.Write((short)0); bw.Write((short)0);
                    // MeshRenderer.m_AdditionalVertexStreams PPtr (5.0+)
                    bw.Write(0); bw.Write(0L);
                }
                else
                {
                    // 2022.3 layout
                    bw.Write(true); // m_Enabled
                    bw.Write(expectedCastShadows); // m_CastShadows
                    bw.Write(expectedReceiveShadows); // m_ReceiveShadows
                    bw.Write((byte)1); // m_DynamicOccludee
                    bw.Write((byte)0); // m_StaticShadowCaster
                    bw.Write((byte)1); // m_MotionVectors
                    bw.Write((byte)1); // m_LightProbeUsage
                    bw.Write((byte)1); // m_ReflectionProbeUsage
                    bw.Write((byte)2); // m_RayTracingMode
                    bw.Write((byte)1); // m_RayTraceProcedural
                    bw.Write(new byte[2]); // AlignStream (10 -> 12 bytes)
                    bw.Write(1u); // m_RenderingLayerMask
                    bw.Write(0); // m_RendererPriority
                    bw.Write((ushort)65535); // m_LightmapIndex
                    bw.Write((ushort)65535); // m_LightmapIndexDynamic
                    // m_LightmapTilingOffset + Dynamic
                    for (int i = 0; i < 8; i++) bw.Write(0f);
                    // m_Materials (empty)
                    bw.Write(0);
                    // m_StaticBatchInfo (firstSubMesh, subMeshCount)
                    bw.Write((ushort)0); bw.Write((ushort)0);
                    // m_StaticBatchRoot PPtr
                    bw.Write(0); bw.Write(0L);
                    // m_ProbeAnchor PPtr + m_LightProbeVolumeOverride PPtr
                    bw.Write(0); bw.Write(0L);
                    bw.Write(0); bw.Write(0L);
                    // m_SortingLayerID + m_SortingLayer + m_SortingOrder
                    bw.Write(0u); bw.Write((short)0); bw.Write((short)0);
                    // MeshRenderer.m_AdditionalVertexStreams PPtr
                    bw.Write(0); bw.Write(0L);
                    // MeshRenderer.m_EnlightenVertexStream PPtr
                    bw.Write(0); bw.Write(0L);
                }
            }

            var payload = ms.ToArray();
            var parsed = Assert.IsType<MeshRenderer>(CreateObjectViaAssetsManager(sf, ClassIDType.MeshRenderer, pathId: 50, payload));
            Assert.True(parsed.m_Enabled);
            Assert.Equal(expectedCastShadows, parsed.m_CastShadows);
            Assert.Equal(expectedReceiveShadows, parsed.m_ReceiveShadows);
        }

        // =====================================================================
        // 2. KHR_node_visibility for ShadowsOnly, Collider Proxies & Invisible Shaders
        // =====================================================================

        [Fact]
        public void SceneExport_ShadowsOnlyColliderProxyAndInvisibleShader_PreservedIntactAndHiddenViaKhrNodeVisibility()
        {
            var tempRoot = CreateTempDirectory("scene_fidelity_visibility");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var litShader = CreateSyntheticShader(sf, 10, "Universal Render Pipeline/Lit");
                var depthOnlyShader = CreateSyntheticShader(sf, 11, "Custom/DepthOnlyOccluder", writeColor: false);

                var diffuseTex = CreateSolidRgbaTexture2D(sf, 20, "T_Stone_Albedo", 160, 150, 140, 255);
                var stoneMat = CreateSyntheticMaterial(
                    sf, 30, "Mat_Stone", litShader,
                    texEnvs: new[] { ("_BaseMap", diffuseTex) });
                var untexturedLitMat = CreateSyntheticMaterial(sf, 31, "Lit", litShader);
                var invisibleMat = CreateSyntheticMaterial(sf, 32, "Mat_Occluder", depthOnlyShader);

                var boxMesh = CreateSyntheticBoxMesh(sf, 40, "SM_Box");

                // SceneRoot
                var rootGo = CreateSyntheticGameObject(sf, 100, "SceneRoot", isActive: true, layer: 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                // (a) Visible textured ground -> MUST stay visible
                var groundGo = CreateSyntheticGameObject(sf, 110, "Ground_Floor", isActive: true, layer: 0);
                var groundTr = CreateSyntheticTransform(sf, 111, groundGo, rootTr, Vector3.Zero);
                var groundMf = CreateSyntheticMeshFilter(sf, 112, groundGo, 0, boxMesh.m_PathID);
                var groundMr = CreateSyntheticMeshRenderer(sf, 113, groundGo, true, new[] { CreatePPtr<Material>(sf, 0, stoneMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOn);
                AttachComponents(groundGo, groundTr, groundMf, groundMr);

                // (b) ShadowsOnly roof proxy (even with textured material!) + a visible child ornament under it
                var sdwGo = CreateSyntheticGameObject(sf, 120, "Roof_SDW_ONLY", isActive: true, layer: 0);
                var sdwTr = CreateSyntheticTransform(sf, 121, sdwGo, rootTr, new Vector3(0f, 5f, 0f));
                var sdwMf = CreateSyntheticMeshFilter(sf, 122, sdwGo, 0, boxMesh.m_PathID);
                var sdwMr = CreateSyntheticMeshRenderer(sf, 123, sdwGo, true, new[] { CreatePPtr<Material>(sf, 0, stoneMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeShadowsOnly);
                AttachComponents(sdwGo, sdwTr, sdwMf, sdwMr);

                var visibleChildGo = CreateSyntheticGameObject(sf, 125, "VisibleRoofLantern", isActive: true, layer: 0);
                var visibleChildTr = CreateSyntheticTransform(sf, 126, visibleChildGo, sdwTr, new Vector3(0f, 1f, 0f));
                var visibleChildMf = CreateSyntheticMeshFilter(sf, 127, visibleChildGo, 0, boxMesh.m_PathID);
                var visibleChildMr = CreateSyntheticMeshRenderer(sf, 128, visibleChildGo, true, new[] { CreatePPtr<Material>(sf, 0, stoneMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOn);
                AttachComponents(visibleChildGo, visibleChildTr, visibleChildMf, visibleChildMr);
                sdwTr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, visibleChildTr.m_PathID) };

                // (c) NAVIMESH/AirWall/Wall_01 with untextured Lit, m_CastShadows=0, unmaterialized MeshCollider in ObjectInfoDic, Layer=23
                var navGo = CreateSyntheticGameObject(sf, 130, "NAVIMESH", isActive: true, layer: 23);
                var navTr = CreateSyntheticTransform(sf, 131, navGo, rootTr, Vector3.Zero);
                AttachComponents(navGo, navTr);

                var airWallParentGo = CreateSyntheticGameObject(sf, 132, "AirWall", isActive: true, layer: 23);
                var airWallParentTr = CreateSyntheticTransform(sf, 133, airWallParentGo, navTr, Vector3.Zero);
                AttachComponents(airWallParentGo, airWallParentTr);

                var airWallGo = CreateSyntheticGameObject(sf, 134, "Wall_Blocker_01", isActive: true, layer: 23);
                var airWallTr = CreateSyntheticTransform(sf, 135, airWallGo, airWallParentTr, new Vector3(4f, 0f, 0f));
                var airWallMf = CreateSyntheticMeshFilter(sf, 136, airWallGo, 0, boxMesh.m_PathID);
                var airWallMr = CreateSyntheticMeshRenderer(sf, 137, airWallGo, true, new[] { CreatePPtr<Material>(sf, 0, untexturedLitMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOff);
                // Register MeshCollider in ObjectInfoDic ONLY (lazy unmaterialized collider!)
                sf.ObjectInfoDic[138] = new ObjectInfo { m_PathID = 138, classID = (int)ClassIDType.MeshCollider, typeID = (int)ClassIDType.MeshCollider };
                airWallGo.m_Components = new[]
                {
                    CreatePPtr<Component>(sf, 0, airWallTr.m_PathID),
                    CreatePPtr<Component>(sf, 0, airWallMf.m_PathID),
                    CreatePPtr<Component>(sf, 0, airWallMr.m_PathID),
                    CreatePPtr<Component>(sf, 0, 138)
                };
                navTr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, airWallParentTr.m_PathID) };
                airWallParentTr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, airWallTr.m_PathID) };

                // (d) Invisible / ColorMask 0 shader occluder
                var occGo = CreateSyntheticGameObject(sf, 140, "PortalMesh", isActive: true, layer: 0);
                var occTr = CreateSyntheticTransform(sf, 141, occGo, rootTr, new Vector3(-4f, 0f, 0f));
                var occMf = CreateSyntheticMeshFilter(sf, 142, occGo, 0, boxMesh.m_PathID);
                var occMr = CreateSyntheticMeshRenderer(sf, 143, occGo, true, new[] { CreatePPtr<Material>(sf, 0, invisibleMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOn);
                AttachComponents(occGo, occTr, occMf, occMr);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, groundTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, sdwTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, navTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, occTr.m_PathID)
                };
                AttachComponents(rootGo, rootTr);

                var service = new SceneGlbExportService();

                // Run 1: Default HideAuxiliaryAndShadowOnlyRenderers = true
                var glbHiddenPath = Path.Combine(tempRoot, "VisibilityOn.glb");
                var hiddenResult = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = glbHiddenPath,
                        SceneName = "FidelityScene",
                        HideAuxiliaryAndShadowOnlyRenderers = true,
                        WriteManifest = true
                    });

                Assert.True(hiddenResult.Success, hiddenResult.ErrorMessage);
                Assert.Equal(3, hiddenResult.HiddenAuxiliaryRendererCount);
                Assert.Equal(3, hiddenResult.Stats.HiddenNodesCount);
                Assert.Equal(5, hiddenResult.Stats.TotalMeshInstances); // ALL 5 meshes kept intact, NONE deleted!

                using (var doc = ParseGlbJson(glbHiddenPath))
                {
                    var root = doc.RootElement;
                    var extsUsed = root.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()).ToList();
                    Assert.Contains("KHR_node_visibility", extsUsed);

                    var nodesByName = root.GetProperty("nodes")
                        .EnumerateArray()
                        .ToDictionary(n => n.GetProperty("name").GetString() ?? string.Empty, n => n);

                    // Ground is visible (no KHR_node_visibility extension) and has mesh
                    var groundNode = nodesByName["Ground_Floor"];
                    Assert.True(groundNode.TryGetProperty("mesh", out _));
                    Assert.False(groundNode.TryGetProperty("extensions", out _));

                    // Roof_SDW_ONLY keeps its mesh AND is hidden via KHR_node_visibility
                    var sdwNode = nodesByName["Roof_SDW_ONLY"];
                    Assert.True(sdwNode.TryGetProperty("mesh", out _));
                    Assert.False(sdwNode.GetProperty("extensions").GetProperty("KHR_node_visibility").GetProperty("visible").GetBoolean());
                    Assert.Equal("ShadowsOnly", sdwNode.GetProperty("extras").GetProperty("hiddenReason").GetString());

                    // Visible child under hidden parent gets explicit visible:true override per glTF KHR_node_visibility inheritance!
                    var childNode = nodesByName["VisibleRoofLantern"];
                    Assert.True(childNode.TryGetProperty("mesh", out _));
                    Assert.True(childNode.GetProperty("extensions").GetProperty("KHR_node_visibility").GetProperty("visible").GetBoolean());

                    // AirWall keeps its mesh AND is hidden via KHR_node_visibility
                    var airWallNode = nodesByName["Wall_Blocker_01"];
                    Assert.True(airWallNode.TryGetProperty("mesh", out _));
                    Assert.False(airWallNode.GetProperty("extensions").GetProperty("KHR_node_visibility").GetProperty("visible").GetBoolean());
                    Assert.Equal("ColliderProxy", airWallNode.GetProperty("extras").GetProperty("hiddenReason").GetString());

                    // ColorMask 0 shader occluder keeps its mesh AND is hidden
                    var occNode = nodesByName["PortalMesh"];
                    Assert.True(occNode.TryGetProperty("mesh", out _));
                    Assert.False(occNode.GetProperty("extensions").GetProperty("KHR_node_visibility").GetProperty("visible").GetBoolean());
                    Assert.Equal("InvisibleShader", occNode.GetProperty("extras").GetProperty("hiddenReason").GetString());
                }

                // Verify manifest records the new option and hidden count
                using (var mDoc = JsonDocument.Parse(File.ReadAllText(hiddenResult.ManifestPath!)))
                {
                    Assert.True(mDoc.RootElement.GetProperty("options").GetProperty("hideAuxiliaryAndShadowOnlyRenderers").GetBoolean());
                    Assert.Equal(3, mDoc.RootElement.GetProperty("statistics").GetProperty("hiddenAuxiliaryRenderers").GetInt32());
                }

                // Run 2: HideAuxiliaryAndShadowOnlyRenderers = false -> nothing hidden, KHR_node_visibility omitted
                var glbVisiblePath = Path.Combine(tempRoot, "VisibilityOff.glb");
                var visibleResult = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = glbVisiblePath,
                        SceneName = "FidelityScene",
                        HideAuxiliaryAndShadowOnlyRenderers = false,
                        WriteManifest = false
                    });

                Assert.True(visibleResult.Success, visibleResult.ErrorMessage);
                Assert.Equal(0, visibleResult.HiddenAuxiliaryRendererCount);
                Assert.Equal(0, visibleResult.Stats.HiddenNodesCount);

                using (var doc = ParseGlbJson(glbVisiblePath))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("extensionsUsed", out var extsEl))
                    {
                        var exts = extsEl.EnumerateArray().Select(e => e.GetString()).ToList();
                        Assert.DoesNotContain("KHR_node_visibility", exts);
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 3. Water & Refractive Shaders: Never BumpMap as Albedo + Transmission/IOR
        // =====================================================================

        [Fact]
        public void WaterAndRefractiveShaders_NeverAssignBumpMapAsDiffuse_AndEmitTransmissionIorAndShaderExtras()
        {
            var tempRoot = CreateTempDirectory("scene_fidelity_water");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var waterShader = CreateSyntheticShader(sf, 10, "Custom/Water");
                var glassShader = CreateSyntheticShader(sf, 11, "Custom/RefractiveGlass");

                // Water has ONLY _BumpMap and _BumpMap2 (purple normal map pixels!), NO _BaseMap/_MainTex.
                var bump1 = CreateSolidRgbaTexture2D(sf, 20, "T_Water_Wave_NRM", 128, 128, 255, 255);
                var bump2 = CreateSolidRgbaTexture2D(sf, 21, "T_Water_Detail_NRM", 128, 130, 255, 255);
                var glassNormal = CreateSolidRgbaTexture2D(sf, 22, "T_Glass_Normal", 128, 128, 255, 255);

                var waterMat = CreateSyntheticMaterial(
                    sf, 30, "Mat_RiverWater", waterShader,
                    texEnvs: new[] { ("_BumpMap", bump1), ("_BumpMap2", bump2) },
                    colors: new[]
                    {
                        ("_ShallowColor", new Color(0.10f, 0.55f, 0.65f, 1f)),
                        ("_DeepColor", new Color(0.04f, 0.20f, 0.35f, 1f))
                    },
                    floats: new[] { ("_BumpScale", 0.75f), ("_Surface", 1f) },
                    renderQueue: 3000,
                    keywords: new[] { "_NORMALMAP", "_SURFACE_TYPE_TRANSPARENT" },
                    tags: new[] { ("RenderType", "Transparent") });

                var glassMat = CreateSyntheticMaterial(
                    sf, 31, "Mat_CrystalGlass", glassShader,
                    texEnvs: new[] { ("_NormalMap", glassNormal) },
                    colors: new[] { ("_BaseColor", new Color(0.85f, 0.95f, 1.0f, 1f)) },
                    floats: new[] { ("_IOR", 1.52f) });

                var planeMesh = CreateSyntheticBoxMesh(sf, 40, "SM_WaterPlane");

                var rootGo = CreateSyntheticGameObject(sf, 100, "StageRoot", isActive: true, layer: 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                // Give RIVER a MeshCollider + CastShadows=Off + Layer=4 (Water) to verify water is NEVER mistakenly hidden!
                var riverGo = CreateSyntheticGameObject(sf, 110, "RIVER", isActive: true, layer: 4);
                var riverTr = CreateSyntheticTransform(sf, 111, riverGo, rootTr, Vector3.Zero);
                var riverMf = CreateSyntheticMeshFilter(sf, 112, riverGo, 0, planeMesh.m_PathID);
                var riverMr = CreateSyntheticMeshRenderer(sf, 113, riverGo, true, new[] { CreatePPtr<Material>(sf, 0, waterMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOff);
                sf.ObjectInfoDic[114] = new ObjectInfo { m_PathID = 114, classID = (int)ClassIDType.MeshCollider, typeID = (int)ClassIDType.MeshCollider };
                riverGo.m_Components = new[]
                {
                    CreatePPtr<Component>(sf, 0, riverTr.m_PathID),
                    CreatePPtr<Component>(sf, 0, riverMf.m_PathID),
                    CreatePPtr<Component>(sf, 0, riverMr.m_PathID),
                    CreatePPtr<Component>(sf, 0, 114)
                };

                var glassGo = CreateSyntheticGameObject(sf, 120, "GlassWindow", isActive: true, layer: 0);
                var glassTr = CreateSyntheticTransform(sf, 121, glassGo, rootTr, new Vector3(5f, 0f, 0f));
                var glassMf = CreateSyntheticMeshFilter(sf, 122, glassGo, 0, planeMesh.m_PathID);
                var glassMr = CreateSyntheticMeshRenderer(sf, 123, glassGo, true, new[] { CreatePPtr<Material>(sf, 0, glassMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOn);
                AttachComponents(glassGo, glassTr, glassMf, glassMr);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, riverTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, glassTr.m_PathID)
                };
                AttachComponents(rootGo, rootTr);

                // Deliberately simulate an aggressive SQLite fallback resolver returning _BumpMap when queried for empty main texture!
                Texture2D? AggressiveFallbackResolver(Material mat, string propName, PPtr<Texture>? texPtr)
                {
                    if (string.IsNullOrEmpty(propName) && ReferenceEquals(mat, waterMat))
                    {
                        return bump1;
                    }
                    return null;
                }

                var outGlb = Path.Combine(tempRoot, "WaterFidelity.glb");
                var service = new SceneGlbExportService();
                var result = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = outGlb,
                        SceneName = "RiverScene",
                        HideAuxiliaryAndShadowOnlyRenderers = true,
                        WriteManifest = false
                    },
                    textureResolver: AggressiveFallbackResolver);

                Assert.True(result.Success, result.ErrorMessage);
                Assert.Equal(0, result.HiddenAuxiliaryRendererCount);

                using var doc = ParseGlbJson(outGlb);
                var root = doc.RootElement;
                var extsUsed = root.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("KHR_materials_transmission", extsUsed);
                Assert.Contains("KHR_materials_ior", extsUsed);
                Assert.DoesNotContain("KHR_node_visibility", extsUsed);

                var matsByName = root.GetProperty("materials")
                    .EnumerateArray()
                    .ToDictionary(m => m.GetProperty("name").GetString() ?? string.Empty, m => m);

                // River water material checks
                var gltfWater = matsByName["Mat_RiverWater"];
                var waterPbr = gltfWater.GetProperty("pbrMetallicRoughness");
                Assert.False(waterPbr.TryGetProperty("baseColorTexture", out _), "BumpMap must NEVER be assigned as baseColorTexture!");
                Assert.True(gltfWater.TryGetProperty("normalTexture", out _), "BumpMap must be bound to normalTexture!");
                Assert.Equal(0f, waterPbr.GetProperty("metallicFactor").GetSingle(), 3);
                Assert.True(waterPbr.GetProperty("roughnessFactor").GetSingle() <= 0.15f);

                // Blended shallow/deep water baseColorFactor (blue-teal, not purple/gray)
                var waterColor = waterPbr.GetProperty("baseColorFactor");
                Assert.True(waterColor[2].GetSingle() > waterColor[0].GetSingle());

                var waterExts = gltfWater.GetProperty("extensions");
                Assert.True(waterExts.GetProperty("KHR_materials_transmission").GetProperty("transmissionFactor").GetSingle() > 0.5f);
                Assert.Equal(1.333f, waterExts.GetProperty("KHR_materials_ior").GetProperty("ior").GetSingle(), 3);

                // Shader metadata extras preserved
                var waterMeta = gltfWater.GetProperty("extras").GetProperty("unityShader");
                Assert.Equal("Custom/Water", waterMeta.GetProperty("shaderName").GetString());
                Assert.Equal("Water", waterMeta.GetProperty("classification").GetString());
                Assert.Equal(3000, waterMeta.GetProperty("renderQueue").GetInt32());
                Assert.Equal("Transparent", waterMeta.GetProperty("renderType").GetString());
                var waterKeywords = waterMeta.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()).ToList();
                Assert.Contains("_NORMALMAP", waterKeywords);
                Assert.True(waterMeta.GetProperty("textureProperties").TryGetProperty("_BumpMap", out _));
                Assert.True(waterMeta.GetProperty("textureProperties").TryGetProperty("_BumpMap2", out _));
                Assert.True(waterMeta.GetProperty("colorProperties").TryGetProperty("_ShallowColor", out _));
                Assert.True(waterMeta.GetProperty("colorProperties").TryGetProperty("_DeepColor", out _));

                // Glass refractive material checks
                var gltfGlass = matsByName["Mat_CrystalGlass"];
                Assert.False(gltfGlass.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
                Assert.True(gltfGlass.TryGetProperty("normalTexture", out _));
                Assert.True(gltfGlass.GetProperty("extensions").GetProperty("KHR_materials_transmission").GetProperty("transmissionFactor").GetSingle() > 0.7f);
                Assert.Equal(1.52f, gltfGlass.GetProperty("extensions").GetProperty("KHR_materials_ior").GetProperty("ior").GetSingle(), 3);
                Assert.Equal("Refractive", gltfGlass.GetProperty("extras").GetProperty("unityShader").GetProperty("classification").GetString());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 4. Unlit / Sky / Additive / Effect / Cutout & Non-Diffuse Name Guards
        // =====================================================================

        [Fact]
        public void UnlitSkyAdditiveEffectAndCutoutShaders_EmitUnlitAccurateAlphaModesAndRejectNormalAssetNamesAsAlbedo()
        {
            var tempRoot = CreateTempDirectory("scene_fidelity_unlit_fx");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var skyShader = CreateSyntheticShader(sf, 10, "Skybox/Procedural");
                var additiveShader = CreateSyntheticShader(sf, 11, "Particles/Additive");
                var fogEffectShader = CreateSyntheticShader(sf, 12, "Custom/EffectFog");
                var litShader = CreateSyntheticShader(sf, 13, "Universal Render Pipeline/Lit");
                var customGraphShader = CreateSyntheticShader(sf, 14, "Shader Graphs/RockSurface");

                var foliageTex = CreateSolidRgbaTexture2D(sf, 20, "T_Leaf_Albedo", 60, 140, 45, 200);
                var misnamedNormal = CreateSolidRgbaTexture2D(sf, 21, "T_Rock_Normal", 128, 128, 255, 255);
                var maskNoiseTex = CreateSolidRgbaTexture2D(sf, 22, "T_Surface_Noise", 180, 180, 180, 255);

                var skyMat = CreateSyntheticMaterial(
                    sf, 30, "Mat_SkyDome", skyShader,
                    colors: new[] { ("_SkyTint", new Color(0.42f, 0.68f, 0.96f, 1f)) });

                var additiveMat = CreateSyntheticMaterial(
                    sf, 31, "Mat_GlowBeam", additiveShader,
                    colors: new[] { ("_TintColor", new Color(0.95f, 0.85f, 0.4f, 1f)) },
                    floats: new[] { ("_SrcBlend", 5f), ("_DstBlend", 1f), ("_ZWrite", 0f) });

                var fogMat = CreateSyntheticMaterial(
                    sf, 32, "Mat_ValleyFog", fogEffectShader,
                    colors: new[] { ("_FogColor", new Color(0.75f, 0.82f, 0.90f, 1f)) },
                    renderQueue: 3000,
                    tags: new[] { ("RenderType", "Transparent") });

                var foliageMat = CreateSyntheticMaterial(
                    sf, 33, "Mat_Foliage", litShader,
                    texEnvs: new[] { ("_BaseMap", foliageTex) },
                    floats: new[] { ("_AlphaClip", 1f), ("_Cutoff", 0.45f), ("_Cull", 0f) },
                    keywords: new[] { "_ALPHATEST_ON" });

                // Custom ShaderGraph material binding a normal-named texture to generic _Tex and noise mask to _CustomNoise
                var rockMat = CreateSyntheticMaterial(
                    sf, 34, "Mat_RockGraph", customGraphShader,
                    texEnvs: new[] { ("_Tex", misnamedNormal), ("_CustomNoise", maskNoiseTex) });

                var quadMesh = CreateSyntheticBoxMesh(sf, 40, "SM_Quad");
                var rootGo = CreateSyntheticGameObject(sf, 100, "FxSceneRoot", isActive: true, layer: 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                var children = new List<PPtr<Transform>>();
                (string Name, Material Mat)[] items =
                {
                    ("SkyDome", skyMat),
                    ("GlowBeam", additiveMat),
                    ("ValleyFog", fogMat),
                    ("FoliageCard", foliageMat),
                    ("RockGraphProp", rockMat)
                };

                for (int i = 0; i < items.Length; i++)
                {
                    var idBase = 200 + i * 10;
                    var go = CreateSyntheticGameObject(sf, idBase, items[i].Name, isActive: true, layer: 0);
                    var tr = CreateSyntheticTransform(sf, idBase + 1, go, rootTr, new Vector3(i * 3f, 0f, 0f));
                    var mf = CreateSyntheticMeshFilter(sf, idBase + 2, go, 0, quadMesh.m_PathID);
                    var mr = CreateSyntheticMeshRenderer(sf, idBase + 3, go, true, new[] { CreatePPtr<Material>(sf, 0, items[i].Mat.m_PathID) });
                    AttachComponents(go, tr, mf, mr);
                    children.Add(CreatePPtr<Transform>(sf, 0, tr.m_PathID));
                }

                rootTr.m_Children = children.ToArray();
                AttachComponents(rootGo, rootTr);

                var outGlb = Path.Combine(tempRoot, "UnlitEffects.glb");
                var service = new SceneGlbExportService();
                var result = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = outGlb,
                        SceneName = "FxScene",
                        HideAuxiliaryAndShadowOnlyRenderers = true,
                        WriteManifest = false
                    },
                    // Also verify fallback resolver returning a normal-named texture is rejected for Mat_RockGraph!
                    textureResolver: (mat, prop, _) => string.IsNullOrEmpty(prop) && ReferenceEquals(mat, rockMat) ? misnamedNormal : null);

                Assert.True(result.Success, result.ErrorMessage);
                Assert.Equal(0, result.HiddenAuxiliaryRendererCount);

                using var doc = ParseGlbJson(outGlb);
                var root = doc.RootElement;
                var extsUsed = root.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("KHR_materials_unlit", extsUsed);

                var matsByName = root.GetProperty("materials")
                    .EnumerateArray()
                    .ToDictionary(m => m.GetProperty("name").GetString() ?? string.Empty, m => m);

                // SkyDome: KHR_materials_unlit + OPAQUE + _SkyTint preserved
                var gltfSky = matsByName["Mat_SkyDome"];
                Assert.True(gltfSky.GetProperty("extensions").TryGetProperty("KHR_materials_unlit", out _));
                Assert.False(gltfSky.TryGetProperty("alphaMode", out _));
                Assert.Equal(0.42f, gltfSky.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[0].GetSingle(), 2);
                Assert.Equal("Sky", gltfSky.GetProperty("extras").GetProperty("unityShader").GetProperty("classification").GetString());

                // Additive GlowBeam: KHR_materials_unlit + BLEND + translucent fallback alpha (< 0.9)
                var gltfAdd = matsByName["Mat_GlowBeam"];
                Assert.True(gltfAdd.GetProperty("extensions").TryGetProperty("KHR_materials_unlit", out _));
                Assert.Equal("BLEND", gltfAdd.GetProperty("alphaMode").GetString());
                Assert.True(gltfAdd.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[3].GetSingle() < 0.9f);
                Assert.Equal("Additive", gltfAdd.GetProperty("extras").GetProperty("unityShader").GetProperty("classification").GetString());

                // ValleyFog effect plane: KHR_materials_unlit + BLEND + translucent fallback alpha
                var gltfFog = matsByName["Mat_ValleyFog"];
                Assert.True(gltfFog.GetProperty("extensions").TryGetProperty("KHR_materials_unlit", out _));
                Assert.Equal("BLEND", gltfFog.GetProperty("alphaMode").GetString());
                Assert.True(gltfFog.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[3].GetSingle() < 0.9f);
                Assert.Equal("Effect", gltfFog.GetProperty("extras").GetProperty("unityShader").GetProperty("classification").GetString());

                // FoliageCutout: PBR Lit + MASK + alphaCutoff=0.45 + doubleSided
                var gltfFoliage = matsByName["Mat_Foliage"];
                Assert.False(gltfFoliage.TryGetProperty("extensions", out _));
                Assert.Equal("MASK", gltfFoliage.GetProperty("alphaMode").GetString());
                Assert.Equal(0.45f, gltfFoliage.GetProperty("alphaCutoff").GetSingle(), 3);
                Assert.True(gltfFoliage.GetProperty("doubleSided").GetBoolean());

                // RockGraph: T_Rock_Normal bound via generic _Tex routed to normalTexture, NEVER baseColorTexture; T_Surface_Noise rejected as diffuse
                var gltfRock = matsByName["Mat_RockGraph"];
                Assert.False(gltfRock.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
                Assert.True(gltfRock.TryGetProperty("normalTexture", out _));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 5. Companion Stage Preset Selection (Auto vs Preferred Preset)
        // =====================================================================

        [Fact]
        public void CompanionStagePresetSelection_HonorsPreferredStagePresetNameAndFallsBackToAutoDaytime()
        {
            using var am = new AssetsManager();
            var baseSf = CreateSyntheticSerializedFile(am, "CAB-stagebase01", "2022.3.40f1");
            var presetSf = CreateSyntheticSerializedFile(am, "CAB-stagepresets01", "2022.3.40f1");

            var sceneRootGo = CreateSyntheticGameObject(baseSf, 1, "SceneRoot", isActive: true, layer: 0);
            var sceneRootTr = CreateSyntheticTransform(baseSf, 2, sceneRootGo, null, Vector3.Zero);
            AttachComponents(sceneRootGo, sceneRootTr);

            GameObject BuildPrefab(long idBase, string name)
            {
                var go = CreateSyntheticGameObject(presetSf, idBase, name, isActive: true, layer: 0);
                var tr = CreateSyntheticTransform(presetSf, idBase + 1, go, null, Vector3.Zero);
                AttachComponents(go, tr);
                return go;
            }

            var noonPrefab = BuildPrefab(100, "Stage_Harbor_Model_NOON_SUNNY");
            var eveningPrefab = BuildPrefab(110, "Stage_Harbor_Model_EVENING_CLEAR");
            var nightPrefab = BuildPrefab(120, "Stage_Harbor_Model_NIGHT_CLOUDY");
            var specialPrefab = BuildPrefab(130, "Stage_Harbor_SPECIAL1_VARIANT");

            var script = CreateSyntheticMonoScript(presetSf, 200, "Game.Stage", "StagePreset", "Assembly-CSharp.dll");
            MonoBehaviour CreatePresetSo(long pathId, string name)
            {
                var mb = CreateSyntheticObject<MonoBehaviour>(presetSf, pathId, ClassIDType.MonoBehaviour);
                mb.m_GameObject = CreatePPtr<GameObject>(presetSf, 0, 0);
                mb.m_Enabled = 1;
                mb.m_Script = CreatePPtr<MonoScript>(presetSf, 0, script.m_PathID);
                mb.m_Name = name;
                return mb;
            }

            var noonSo = CreatePresetSo(201, "Stage_Harbor_NOON_SUNNY_NORMAL");
            var eveningSo = CreatePresetSo(202, "Stage_Harbor_EVENING_CLEAR");
            var nightSo = CreatePresetSo(203, "Stage_Harbor_NIGHT_CLOUDY");
            var specialSo = CreatePresetSo(204, "Stage_Harbor_SPECIAL1");

            static OrderedDictionary MakePPtrDict(long pathId) => new()
            {
                ["m_FileID"] = 0,
                ["m_PathID"] = pathId
            };

            OrderedDictionary? FieldProvider(MonoBehaviour mb)
            {
                if (mb.m_PathID == noonSo.m_PathID) return new OrderedDictionary { ["model"] = MakePPtrDict(noonPrefab.m_PathID) };
                if (mb.m_PathID == eveningSo.m_PathID) return new OrderedDictionary { ["model"] = MakePPtrDict(eveningPrefab.m_PathID) };
                if (mb.m_PathID == nightSo.m_PathID) return new OrderedDictionary { ["model"] = MakePPtrDict(nightPrefab.m_PathID) };
                if (mb.m_PathID == specialSo.m_PathID) return new OrderedDictionary { ["model"] = MakePPtrDict(specialPrefab.m_PathID) };
                return null;
            }

            var groups = new[] { ("Stage_Harbor", (IReadOnlyList<SerializedFile>)new[] { presetSf }) };

            // 1. Empty / Auto -> Noon Sunny Normal
            var autoAttachments = SceneGlbExportService.ResolveCompanionStagePresetAttachments(
                new[] { sceneRootGo }, groups, customFieldProvider: FieldProvider, preferredStagePresetName: string.Empty);
            Assert.Equal("Stage_Harbor_Model_NOON_SUNNY", Assert.Single(autoAttachments).PrefabRoot.m_Name);

            // 2. Keyword "Night" -> Night Cloudy
            var nightAttachments = SceneGlbExportService.ResolveCompanionStagePresetAttachments(
                new[] { sceneRootGo }, groups, customFieldProvider: FieldProvider, preferredStagePresetName: "Night");
            Assert.Equal("Stage_Harbor_Model_NIGHT_CLOUDY", Assert.Single(nightAttachments).PrefabRoot.m_Name);

            // 3. Exact suffix "EVENING_CLEAR" -> Evening Clear
            var eveningAttachments = SceneGlbExportService.ResolveCompanionStagePresetAttachments(
                new[] { sceneRootGo }, groups, customFieldProvider: FieldProvider, preferredStagePresetName: "EVENING_CLEAR");
            Assert.Equal("Stage_Harbor_Model_EVENING_CLEAR", Assert.Single(eveningAttachments).PrefabRoot.m_Name);

            // 4. Explicit special preset "SPECIAL1" -> Special1 (even in stripped-TypeTree fallback with null field provider!)
            var specialFallbackAttachments = SceneGlbExportService.ResolveCompanionStagePresetAttachments(
                new[] { sceneRootGo }, groups, customFieldProvider: null, preferredStagePresetName: "SPECIAL1");
            Assert.Contains(specialFallbackAttachments, a => a.PrefabRoot.m_Name == "Stage_Harbor_SPECIAL1_VARIANT");
        }

        // =====================================================================
        // 6. SceneExportDialogViewModel & Options Round-Trip
        // =====================================================================

        [Fact]
        public void SceneExportDialogViewModel_HideAuxiliaryAndPreferredStagePreset_BindAndRoundTripCleanly()
        {
            var candidates = new[]
            {
                new SceneExportCandidate
                {
                    Id = "s1",
                    Name = "HarborStage",
                    ScenePath = "Assets/Scenes/HarborStage.unity",
                    SourceKind = "SceneBundle",
                    SerializedFileName = "CAB-harbor",
                    IsPreselected = true,
                    AvailableStagePresets = new[] { "NOON_SUNNY_NORMAL", "NIGHT_CLOUDY" }
                }
            };

            var vm = new SceneExportDialogViewModel(new SceneExportDialogRequest(candidates));

            // Defaults
            Assert.True(vm.HideAuxiliaryAndShadowOnlyRenderers);
            Assert.Equal(string.Empty, vm.PreferredStagePresetName);
            Assert.Equal(0, vm.SelectedStagePresetIndex);
            Assert.Equal(SceneExportDialogViewModel.AutoStagePresetLabel, vm.StagePresetOptions[0]);
            Assert.Contains("NOON_SUNNY_NORMAL", vm.StagePresetOptions);
            Assert.Contains("NIGHT_CLOUDY", vm.StagePresetOptions);

            // Selecting a discovered preset from ComboBox updates PreferredStagePresetName
            var nightIndex = vm.StagePresetOptions.IndexOf("NIGHT_CLOUDY");
            Assert.True(nightIndex > 0);
            vm.SelectedStagePresetIndex = nightIndex;
            Assert.Equal("NIGHT_CLOUDY", vm.PreferredStagePresetName);

            // Typing a custom preset sets SelectedStagePresetIndex = -1 without erasing text
            vm.PreferredStagePresetName = "EVENING_RAIN";
            Assert.Equal(-1, vm.SelectedStagePresetIndex);
            Assert.Equal("EVENING_RAIN", vm.PreferredStagePresetName);

            // Toggling HideAuxiliaryAndShadowOnlyRenderers off + confirming
            vm.HideAuxiliaryAndShadowOnlyRenderers = false;
            vm.ConfirmCommand.Execute();

            Assert.NotNull(vm.Result);
            Assert.False(vm.Result!.Options.HideAuxiliaryAndShadowOnlyRenderers);
            Assert.Equal("EVENING_RAIN", vm.Result.Options.PreferredStagePresetName);

            var glbOpts = SceneGlbExportOptions.FromSceneExportOptions(
                "harbor.glb",
                "HarborStage",
                vm.Result.Options);
            Assert.False(glbOpts.HideAuxiliaryAndShadowOnlyRenderers);
            Assert.Equal("EVENING_RAIN", glbOpts.PreferredStagePresetName);

            // Selecting index 0 (Auto) resets PreferredStagePresetName back to empty string
            vm.SelectedStagePresetIndex = 0;
            Assert.Equal(string.Empty, vm.PreferredStagePresetName);
        }

        // =====================================================================
        // 7. Headless Blender 5.x Roundtrip: KHR_node_visibility + Water PBR + Unlit
        // =====================================================================

        [Fact]
        public void BlenderHeadlessRoundtrip_KhrNodeVisibilityWaterTransmissionIorAndUnlit_ImportCleanly()
        {
            var blenderExe = TryLocateBlenderExecutable();
            if (blenderExe == null)
            {
                Assert.Fail("Blender was not found (set ASSETSTUDIO_BLENDER_EXE or put blender on PATH).");
            }

            var tempRoot = CreateTempDirectory("scene_fidelity_blender");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var litShader = CreateSyntheticShader(sf, 10, "Universal Render Pipeline/Lit");
                var waterShader = CreateSyntheticShader(sf, 11, "Custom/Water");
                var skyShader = CreateSyntheticShader(sf, 12, "Skybox/Procedural");

                var stoneTex = CreateSolidRgbaTexture2D(sf, 20, "T_Stone_Albedo", 160, 150, 140, 255);
                var waterBump = CreateSolidRgbaTexture2D(sf, 21, "T_Water_Wave_NRM", 128, 128, 255, 255);

                var stoneMat = CreateSyntheticMaterial(sf, 30, "Mat_Stone", litShader, texEnvs: new[] { ("_BaseMap", stoneTex) });
                var untexturedLitMat = CreateSyntheticMaterial(sf, 31, "Lit", litShader);
                var waterMat = CreateSyntheticMaterial(
                    sf, 32, "Mat_RiverWater", waterShader,
                    texEnvs: new[] { ("_BumpMap", waterBump) },
                    colors: new[]
                    {
                        ("_ShallowColor", new Color(0.10f, 0.55f, 0.65f, 1f)),
                        ("_DeepColor", new Color(0.04f, 0.20f, 0.35f, 1f))
                    });
                var skyMat = CreateSyntheticMaterial(
                    sf, 33, "Mat_SkyDome", skyShader,
                    colors: new[] { ("_SkyTint", new Color(0.45f, 0.72f, 0.96f, 1f)) });

                var mesh = CreateSyntheticBoxMesh(sf, 40, "SM_Quad");
                var rootGo = CreateSyntheticGameObject(sf, 100, "StageRoot", isActive: true, layer: 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                // Visible ground
                var groundGo = CreateSyntheticGameObject(sf, 110, "Ground_Floor", isActive: true, layer: 0);
                var groundTr = CreateSyntheticTransform(sf, 111, groundGo, rootTr, Vector3.Zero);
                var groundMf = CreateSyntheticMeshFilter(sf, 112, groundGo, 0, mesh.m_PathID);
                var groundMr = CreateSyntheticMeshRenderer(sf, 113, groundGo, true, new[] { CreatePPtr<Material>(sf, 0, stoneMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOn);
                AttachComponents(groundGo, groundTr, groundMf, groundMr);

                // Hidden ShadowsOnly roof + visible child lantern
                var sdwGo = CreateSyntheticGameObject(sf, 120, "Roof_SDW_ONLY", isActive: true, layer: 0);
                var sdwTr = CreateSyntheticTransform(sf, 121, sdwGo, rootTr, new Vector3(0f, 4f, 0f));
                var sdwMf = CreateSyntheticMeshFilter(sf, 122, sdwGo, 0, mesh.m_PathID);
                var sdwMr = CreateSyntheticMeshRenderer(sf, 123, sdwGo, true, new[] { CreatePPtr<Material>(sf, 0, stoneMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeShadowsOnly);
                AttachComponents(sdwGo, sdwTr, sdwMf, sdwMr);

                var childGo = CreateSyntheticGameObject(sf, 125, "VisibleRoofLantern", isActive: true, layer: 0);
                var childTr = CreateSyntheticTransform(sf, 126, childGo, sdwTr, new Vector3(0f, 1f, 0f));
                var childMf = CreateSyntheticMeshFilter(sf, 127, childGo, 0, mesh.m_PathID);
                var childMr = CreateSyntheticMeshRenderer(sf, 128, childGo, true, new[] { CreatePPtr<Material>(sf, 0, stoneMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOn);
                AttachComponents(childGo, childTr, childMf, childMr);
                sdwTr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, childTr.m_PathID) };

                // Hidden AirWall collider proxy
                var airWallGo = CreateSyntheticGameObject(sf, 130, "AirWall_01", isActive: true, layer: 23);
                var airWallTr = CreateSyntheticTransform(sf, 131, airWallGo, rootTr, new Vector3(3f, 0f, 0f));
                var airWallMf = CreateSyntheticMeshFilter(sf, 132, airWallGo, 0, mesh.m_PathID);
                var airWallMr = CreateSyntheticMeshRenderer(sf, 133, airWallGo, true, new[] { CreatePPtr<Material>(sf, 0, untexturedLitMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOff);
                sf.ObjectInfoDic[134] = new ObjectInfo { m_PathID = 134, classID = (int)ClassIDType.MeshCollider, typeID = (int)ClassIDType.MeshCollider };
                airWallGo.m_Components = new[]
                {
                    CreatePPtr<Component>(sf, 0, airWallTr.m_PathID),
                    CreatePPtr<Component>(sf, 0, airWallMf.m_PathID),
                    CreatePPtr<Component>(sf, 0, airWallMr.m_PathID),
                    CreatePPtr<Component>(sf, 0, 134)
                };

                // Visible Water plane
                var riverGo = CreateSyntheticGameObject(sf, 140, "RIVER", isActive: true, layer: 4);
                var riverTr = CreateSyntheticTransform(sf, 141, riverGo, rootTr, new Vector3(-3f, 0f, 0f));
                var riverMf = CreateSyntheticMeshFilter(sf, 142, riverGo, 0, mesh.m_PathID);
                var riverMr = CreateSyntheticMeshRenderer(sf, 143, riverGo, true, new[] { CreatePPtr<Material>(sf, 0, waterMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOff);
                AttachComponents(riverGo, riverTr, riverMf, riverMr);

                // Visible Unlit SkyDome
                var skyGo = CreateSyntheticGameObject(sf, 150, "SkyDome", isActive: true, layer: 0);
                var skyTr = CreateSyntheticTransform(sf, 151, skyGo, rootTr, new Vector3(0f, 10f, 0f));
                var skyMf = CreateSyntheticMeshFilter(sf, 152, skyGo, 0, mesh.m_PathID);
                var skyMr = CreateSyntheticMeshRenderer(sf, 153, skyGo, true, new[] { CreatePPtr<Material>(sf, 0, skyMat.m_PathID) }, castShadows: Renderer.ShadowCastingModeOff);
                AttachComponents(skyGo, skyTr, skyMf, skyMr);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, groundTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, sdwTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, airWallTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, riverTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, skyTr.m_PathID)
                };
                AttachComponents(rootGo, rootTr);

                var glbPath = Path.Combine(tempRoot, "FidelityRoundtrip.glb");
                var reportPath = Path.Combine(tempRoot, "blender_report.json");
                var scriptPath = Path.Combine(tempRoot, "verify_fidelity.py");

                var service = new SceneGlbExportService();
                var exportResult = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = glbPath,
                        SceneName = "FidelityRoundtrip",
                        HideAuxiliaryAndShadowOnlyRenderers = true,
                        WriteManifest = false
                    });
                Assert.True(exportResult.Success, exportResult.ErrorMessage);

                var pyScript = """
import bpy, json, sys

argv = sys.argv
idx = argv.index("--")
glb_path = argv[idx + 1]
report_path = argv[idx + 2]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb_path)

objects_info = {}
for obj in bpy.data.objects:
    objects_info[obj.name] = {
        "type": obj.type,
        "has_mesh": obj.type == "MESH" and obj.data is not None and len(obj.data.vertices) > 0,
        "hide_viewport": bool(obj.hide_viewport),
        "hide_render": bool(obj.hide_render),
        "hidden_reason": str(obj.get("hiddenReason", "")),
    }

materials_info = {}
for mat in bpy.data.materials:
    info = {
        "has_unity_shader_extra": "unityShader" in mat.keys(),
        "use_nodes": bool(mat.use_nodes),
        "base_color_linked": False,
        "normal_linked": False,
        "transmission": None,
        "ior": None,
        "roughness": None,
        "has_emission_or_background": False,
    }
    if mat.use_nodes and mat.node_tree:
        for node in mat.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                bc = node.inputs.get("Base Color")
                info["base_color_linked"] = bool(bc and bc.is_linked)
                nrm = node.inputs.get("Normal")
                info["normal_linked"] = bool(nrm and nrm.is_linked)
                tw = node.inputs.get("Transmission Weight") or node.inputs.get("Transmission")
                if tw is not None:
                    info["transmission"] = float(tw.default_value)
                ior = node.inputs.get("IOR")
                if ior is not None:
                    info["ior"] = float(ior.default_value)
                rough = node.inputs.get("Roughness")
                if rough is not None:
                    info["roughness"] = float(rough.default_value)
            elif node.type in ("EMISSION", "BACKGROUND"):
                info["has_emission_or_background"] = True
    materials_info[mat.name] = info

with open(report_path, "w", encoding="utf-8") as f:
    json.dump({"objects": objects_info, "materials": materials_info}, f, indent=2)
""";
                File.WriteAllText(scriptPath, pyScript, Encoding.UTF8);

                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = blenderExe,
                        Arguments = $"--background --python \"{scriptPath}\" -- \"{glbPath}\" \"{reportPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                Assert.True(proc.Start());
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                Assert.True(proc.WaitForExit(60000), "Blender headless verification timed out.");
                Assert.True(proc.ExitCode == 0, $"Blender exited with {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
                Assert.True(File.Exists(reportPath), "Blender verification script did not produce report JSON.");

                using var reportDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
                var objs = reportDoc.RootElement.GetProperty("objects");
                var mats = reportDoc.RootElement.GetProperty("materials");

                // Proxy nodes keep their mesh intact in Blender and carry hiddenReason extras
                // (while KHR_node_visibility in the GLB marks them visible:false for glTF 2.0 viewers).
                var sdwObj = objs.GetProperty("Roof_SDW_ONLY");
                Assert.True(sdwObj.GetProperty("has_mesh").GetBoolean());
                Assert.Equal("ShadowsOnly", sdwObj.GetProperty("hidden_reason").GetString());

                var airObj = objs.GetProperty("AirWall_01");
                Assert.True(airObj.GetProperty("has_mesh").GetBoolean());
                Assert.Equal("ColliderProxy", airObj.GetProperty("hidden_reason").GetString());

                // Visible nodes stay visible with mesh intact and no hiddenReason
                foreach (var visibleName in new[] { "Ground_Floor", "VisibleRoofLantern", "RIVER", "SkyDome" })
                {
                    var visObj = objs.GetProperty(visibleName);
                    Assert.True(visObj.GetProperty("has_mesh").GetBoolean(), $"{visibleName} missing mesh.");
                    Assert.False(visObj.GetProperty("hide_render").GetBoolean(), $"{visibleName} unexpectedly hidden in render.");
                    Assert.False(visObj.GetProperty("hide_viewport").GetBoolean(), $"{visibleName} unexpectedly hidden in viewport.");
                    Assert.Equal(string.Empty, visObj.GetProperty("hidden_reason").GetString());
                }

                // Water material in Blender: Transmission > 0.5, IOR ~ 1.333, Roughness <= 0.15, Normal linked, Base Color NOT linked, unityShader extra preserved
                var waterInfo = mats.GetProperty("Mat_RiverWater");
                Assert.False(waterInfo.GetProperty("base_color_linked").GetBoolean());
                Assert.True(waterInfo.GetProperty("normal_linked").GetBoolean());
                Assert.True(waterInfo.GetProperty("transmission").GetSingle() > 0.5f);
                Assert.Equal(1.333f, waterInfo.GetProperty("ior").GetSingle(), 2);
                Assert.True(waterInfo.GetProperty("roughness").GetSingle() <= 0.15f);
                Assert.True(waterInfo.GetProperty("has_unity_shader_extra").GetBoolean());

                // SkyDome material in Blender: unlit (Emission/Background node) + unityShader extra preserved
                var skyInfo = mats.GetProperty("Mat_SkyDome");
                Assert.True(skyInfo.GetProperty("has_emission_or_background").GetBoolean());
                Assert.True(skyInfo.GetProperty("has_unity_shader_extra").GetBoolean());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static string? TryLocateBlenderExecutable()
        {
            var envPath = Environment.GetEnvironmentVariable("ASSETSTUDIO_BLENDER_EXE");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                return envPath;
            }

            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVar))
            {
                var exeName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
                foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir, exeName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        // =====================================================================
        // Synthetic Fixture Helpers (100% Engine-Generic)
        // =====================================================================

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

        private static AssetStudio.Object CreateObjectViaAssetsManager(SerializedFile sf, ClassIDType classId, long pathId, byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var fr = new FileReader(sf.fileName, ms) { Endian = EndianType.LittleEndian };
            var info = new ObjectInfo
            {
                m_PathID = pathId,
                byteStart = 0,
                byteSize = (uint)payload.Length,
                typeID = (int)classId,
                classID = (int)classId
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

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive, int layer)
        {
            var go = CreateSyntheticObject<GameObject>(sf, pathId, ClassIDType.GameObject);
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Layer = layer;
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
            byte castShadows = Renderer.ShadowCastingModeOn,
            byte receiveShadows = 1)
        {
            var mr = CreateSyntheticObject<MeshRenderer>(sf, pathId, ClassIDType.MeshRenderer);
            mr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mr.m_Enabled = enabled;
            mr.m_CastShadows = castShadows;
            mr.m_ReceiveShadows = receiveShadows;
            mr.m_Materials = materials;
            mr.m_SubsetIndices = Array.Empty<uint>();
            return mr;
        }

        private static Shader CreateSyntheticShader(
            SerializedFile sf,
            long pathId,
            string shaderName,
            bool writeColor = true)
        {
            var shader = CreateSyntheticObject<Shader>(sf, pathId, ClassIDType.Shader);
            shader.m_Name = shaderName;

            var parsed = (SerializedShader)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShader));
            parsed.m_Name = shaderName;

            var sub = (SerializedSubShader)RuntimeHelpers.GetUninitializedObject(typeof(SerializedSubShader));
            var subTags = (SerializedTagMap)RuntimeHelpers.GetUninitializedObject(typeof(SerializedTagMap));
            subTags.tags = Array.Empty<KeyValuePair<string, string>>();
            sub.m_Tags = subTags;

            var pass = (SerializedPass)RuntimeHelpers.GetUninitializedObject(typeof(SerializedPass));
            pass.m_Type = PassType.Normal;
            pass.m_Name = "ForwardLit";
            var passTags = (SerializedTagMap)RuntimeHelpers.GetUninitializedObject(typeof(SerializedTagMap));
            passTags.tags = Array.Empty<KeyValuePair<string, string>>();
            pass.m_Tags = passTags;

            var state = (SerializedShaderState)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderState));
            var rt = (SerializedShaderRTBlendState)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderRTBlendState));
            rt.srcBlend = MakeShaderFloat(1f);
            rt.destBlend = MakeShaderFloat(0f);
            rt.colMask = MakeShaderFloat(writeColor ? 15f : 0f);
            state.rtBlend = new[] { rt };
            state.culling = MakeShaderFloat(2f);
            state.zWrite = MakeShaderFloat(1f);
            pass.m_State = state;

            sub.m_Passes = new[] { pass };
            parsed.m_SubShaders = new[] { sub };
            shader.m_ParsedForm = parsed;
            return shader;
        }

        private static SerializedShaderFloatValue MakeShaderFloat(float val)
        {
            var fv = (SerializedShaderFloatValue)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShaderFloatValue));
            fv.val = val;
            fv.name = string.Empty;
            return fv;
        }

        private static Texture2D CreateSolidRgbaTexture2D(SerializedFile sf, long pathId, string name, byte r, byte g, byte b, byte a)
        {
            var tex = CreateSyntheticObject<Texture2D>(sf, pathId, ClassIDType.Texture2D);
            tex.m_Name = name;
            tex.m_Width = 2;
            tex.m_Height = 2;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.m_ImageCount = 1;
            tex.m_TextureDimension = 2;

            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < 4; i++)
            {
                pixels[i * 4 + 0] = r;
                pixels[i * 4 + 1] = g;
                pixels[i * 4 + 2] = b;
                pixels[i * 4 + 3] = a;
            }

            tex.image_data = new ResourceReader(pixels);
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
            int renderQueue = 0,
            string[]? keywords = null,
            (string Key, string Value)[]? tags = null)
        {
            var mat = CreateSyntheticObject<Material>(sf, pathId, ClassIDType.Material);
            mat.m_Name = name;
            mat.m_Shader = shader != null ? CreatePPtr<Shader>(sf, 0, shader.m_PathID) : CreatePPtr<Shader>(sf, 0, 0);
            mat.m_Parent = CreatePPtr<Material>(sf, 0, 0);
            mat.m_CustomRenderQueue = renderQueue;
            mat.m_ValidKeywords = keywords ?? Array.Empty<string>();
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

        private static Mesh CreateSyntheticBoxMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = CreateSyntheticObject<Mesh>(sf, pathId, ClassIDType.Mesh);
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            mesh.m_Normals = new float[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_UV0 = new float[] { 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            var subMesh = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            subMesh.firstByte = 0;
            subMesh.indexCount = 3;
            subMesh.topology = GfxPrimitiveType.Triangles;
            subMesh.firstVertex = 0;
            subMesh.vertexCount = 3;
            mesh.m_SubMeshes = new[] { subMesh };
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            return mesh;
        }

        private static void AttachComponents(GameObject go, params Component[] components)
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
