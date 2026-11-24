#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AssetStudio.Avalonia.Services;
using Xunit;

namespace AssetStudio.Tests
{
    public class SmartSceneEngineTests
    {
        // =========================================================================
        // 1. Unity Component Parsers: BuildSettings, LODGroup, Camera, Light
        // =========================================================================

        [Fact]
        public void BuildSettings_ParsesModernAndLegacyLevelMappingsAndHandlesCrossPlatformPaths()
        {
            // Unity 6000.0 (level0 -> index 0, level1 -> index 1)
            using (var payload = new MemoryStream())
            using (var bw = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                WriteStringArray(bw, new[]
                {
                    "Assets/Scenes/Title.unity",
                    "Assets\\Scenes\\World\\TownSquare.unity",
                    "Assets/Scenes/Dungeon_01.unity"
                });
                WriteStringArray(bw, new[] { "plugin_a.dll" });

                var sf = CreateSyntheticSerializedFile(null, "globalgamemanagers", "6000.0.28f1");
                var obj = CreateObjectViaAssetsManager(sf, ClassIDType.BuildSettings, pathId: 11, payload.ToArray());

                var bs = Assert.IsType<BuildSettings>(obj);
                Assert.False(bs.IsTruncated);
                Assert.False(bs.UsesMainDataForFirstScene);
                Assert.Equal(3, bs.scenes.Length);
                Assert.Single(bs.preloadedPlugins);

                Assert.True(bs.TryResolveSceneForLevelFile("level0", out var s0, out var idx0));
                Assert.Equal("Assets/Scenes/Title.unity", s0);
                Assert.Equal(0, idx0);

                Assert.True(bs.TryResolveSceneForLevelFile("Data\\level1", out var s1, out var idx1));
                Assert.Equal("Assets\\Scenes\\World\\TownSquare.unity", s1);
                Assert.Equal(1, idx1);
                Assert.Equal("TownSquare", BuildSettings.ExtractSceneDisplayName(s1));

                Assert.True(bs.TryResolveSceneForLevelFile("/game/data/level2", out var s2, out var idx2));
                Assert.Equal("Assets/Scenes/Dungeon_01.unity", s2);
                Assert.Equal(2, idx2);

                Assert.False(bs.TryResolveSceneForLevelFile("mainData", out _, out _));
                Assert.False(bs.TryResolveSceneForLevelFile("level99", out _, out _));

                var map = bs.BuildLevelFileToScenePathMap();
                Assert.Equal(3, map.Count);
                Assert.Equal("Assets/Scenes/Title.unity", map["level0"]);
                Assert.Equal("Assets/Scenes/Dungeon_01.unity", map["level2"]);
            }

            // Unity 4.7 (mainData -> index 0, level0 -> index 1)
            using (var legacyPayload = new MemoryStream())
            using (var bw = new BinaryWriter(legacyPayload, Encoding.UTF8, leaveOpen: true))
            {
                WriteStringArray(bw, new[]
                {
                    "Assets/Scenes/Boot.unity",
                    "Assets/Scenes/Stage1.unity"
                });

                var legacySf = CreateSyntheticSerializedFile(null, "mainData", "4.7.2f1");
                var bsLegacy = Assert.IsType<BuildSettings>(
                    CreateObjectViaAssetsManager(legacySf, ClassIDType.BuildSettings, pathId: 12, legacyPayload.ToArray()));

                Assert.False(bsLegacy.IsTruncated);
                Assert.True(bsLegacy.UsesMainDataForFirstScene);

                Assert.True(bsLegacy.TryResolveSceneForLevelFile("mainData", out var bootPath, out var bootIdx));
                Assert.Equal("Assets/Scenes/Boot.unity", bootPath);
                Assert.Equal(0, bootIdx);

                Assert.True(bsLegacy.TryResolveSceneForLevelFile("level0", out var stagePath, out var stageIdx));
                Assert.Equal("Assets/Scenes/Stage1.unity", stagePath);
                Assert.Equal(1, stageIdx);
            }
        }

        [Fact]
        public void BuildSettings_TruncatedAndMalformedPayloads_DoNotCrashOrAllocateHugeArrays()
        {
            var sf = CreateSyntheticSerializedFile(null, "globalgamemanagers", "2022.3.10f1");

            // 1. Empty 0-byte payload
            var emptyBs = Assert.IsType<BuildSettings>(
                CreateObjectViaAssetsManager(sf, ClassIDType.BuildSettings, pathId: 21, Array.Empty<byte>()));
            Assert.True(emptyBs.IsTruncated);
            Assert.Empty(emptyBs.scenes);

            // 2. Absurdly large scene count (int.MaxValue)
            var hugeCountBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(hugeCountBytes, int.MaxValue);
            var hugeBs = Assert.IsType<BuildSettings>(
                CreateObjectViaAssetsManager(sf, ClassIDType.BuildSettings, pathId: 22, hugeCountBytes));
            Assert.True(hugeBs.IsTruncated);
            Assert.Empty(hugeBs.scenes);

            // 3. Truncated string payload (claims 1 scene with 200-byte string, only 4 bytes follow)
            using var truncMs = new MemoryStream();
            using (var bw = new BinaryWriter(truncMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(1);   // 1 scene
                bw.Write(200); // claims 200 bytes
                bw.Write((uint)0x41424344);
            }
            var truncBs = Assert.IsType<BuildSettings>(
                CreateObjectViaAssetsManager(sf, ClassIDType.BuildSettings, pathId: 23, truncMs.ToArray()));
            Assert.True(truncBs.IsTruncated);
            Assert.Empty(truncBs.scenes);
        }

        [Fact]
        public void LODGroup_ParsesUnity5AndModernLayoutsAndGuardsMalformedInputs()
        {
            // Unity 2021.3 layout (has m_FadeMode, m_AnimateCrossFading, m_LastLODIsBillboard, fadeTransitionWidth)
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, fileId: 0, pathId: 100); // m_GameObject
                WriteVector3(bw, new Vector3(0.5f, 1.25f, -0.5f));
                bw.Write(4.5f); // m_Size
                bw.Write(1);    // m_FadeMode = CrossFade
                bw.Write((byte)1); // m_AnimateCrossFading
                bw.Write((byte)1); // m_LastLODIsBillboard
                bw.Write((ushort)0); // align 4

                bw.Write(2); // 2 LODs
                // LOD0
                bw.Write(0.6f); // screenRelativeHeight
                bw.Write(0.15f); // fadeTransitionWidth
                bw.Write(2); // 2 renderers
                WritePPtr64(bw, 0, 201);
                WritePPtr64(bw, 0, 202);
                // LOD1
                bw.Write(0.2f);
                bw.Write(0.1f);
                bw.Write(1);
                WritePPtr64(bw, 1, 301);

                bw.Write((byte)1); // m_Enabled
                bw.Write(new byte[3]); // align 4

                var sf = CreateSyntheticSerializedFile(null, "level0", "2021.3.16f1");
                var lodGroup = Assert.IsType<LODGroup>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.LODGroup, pathId: 500, ms.ToArray()));

                Assert.False(lodGroup.IsTruncated);
                Assert.True(lodGroup.m_Enabled);
                Assert.Equal(4.5f, lodGroup.m_Size, 4);
                Assert.Equal(1, lodGroup.m_FadeMode);
                Assert.True(lodGroup.m_AnimateCrossFading);
                Assert.True(lodGroup.m_LastLODIsBillboard);
                Assert.Equal(2, lodGroup.m_LODs.Length);
                Assert.Equal(0.6f, lodGroup.m_LODs[0].screenRelativeHeight, 4);
                Assert.Equal(2, lodGroup.m_LODs[0].renderers.Length);
                Assert.Equal(201, lodGroup.m_LODs[0].renderers[0].m_PathID);
                Assert.Equal(202, lodGroup.m_LODs[0].renderers[1].m_PathID);
                Assert.Single(lodGroup.m_LODs[1].renderers);
                Assert.Equal(1, lodGroup.m_LODs[1].renderers[0].m_FileID);
                Assert.Equal(301, lodGroup.m_LODs[1].renderers[0].m_PathID);
            }

            // Malformed huge lodCount must NOT OOM and must set IsTruncated = true
            using (var badMs = new MemoryStream())
            using (var bw = new BinaryWriter(badMs, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, 0, 100); // m_GameObject
                WriteVector3(bw, Vector3.Zero);
                bw.Write(1f);
                bw.Write(0); // fadeMode
                bw.Write(0); // bools + pad
                bw.Write(int.MaxValue); // bogus m_LODs length

                var sf = CreateSyntheticSerializedFile(null, "level0", "6000.0.28f1");
                var badLod = Assert.IsType<LODGroup>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.LODGroup, pathId: 501, badMs.ToArray()));
                Assert.True(badLod.IsTruncated);
                Assert.Empty(badLod.m_LODs);
            }
        }

        [Fact]
        public void Camera_ParsesPerspectiveAndOrthographicAcrossVersionsAndGuardsTruncation()
        {
            // Unity 6000.0 Perspective Camera (with 2022.2+ physical camera block)
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, 0, 100); // m_GameObject
                bw.Write((byte)1); bw.Write(new byte[3]); // m_Enabled + align
                bw.Write(1u); // m_ClearFlags = Skybox
                WriteColor(bw, new Color(0.1f, 0.2f, 0.3f, 1f));
                bw.Write(1); // m_ProjectionMatrixMode
                bw.Write(2); // m_GateFitMode
                // 2022.2+ physical camera block (56 bytes)
                bw.Write(200);   // m_Iso
                bw.Write(0.005f);// m_ShutterSpeed
                bw.Write(16f);   // m_Aperture
                bw.Write(10f);   // m_FocusDistance
                bw.Write(35f);   // m_FocalLength
                bw.Write(5);     // m_BladeCount
                WriteVector2(bw, new Vector2(2f, 11f)); // m_Curvature
                bw.Write(0.25f); // m_BarrelClipping
                bw.Write(0f);    // m_Anamorphism
                WriteVector2(bw, new Vector2(36f, 24f)); // m_SensorSize
                WriteVector2(bw, Vector2.Zero);          // m_LensShift
                // Core camera block
                WriteVector4(bw, new Vector4(0f, 0f, 1f, 1f)); // viewport
                bw.Write(0.25f);   // near clip
                bw.Write(2500f);   // far clip
                bw.Write(75f);     // field of view
                bw.Write((byte)0); bw.Write(new byte[3]); // orthographic = false + align
                bw.Write(5f);      // orthographic size
                bw.Write(-1f);     // depth
                bw.Write(0xFFFFFFFFu); // culling mask
                bw.Write(-1);      // rendering path

                var sf = CreateSyntheticSerializedFile(null, "level0", "6000.0.28f1");
                var cam = Assert.IsType<Camera>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.Camera, pathId: 600, ms.ToArray()));

                Assert.False(cam.IsTruncated);
                Assert.True(cam.Enabled);
                Assert.False(cam.m_Orthographic);
                Assert.Equal(75f, cam.m_FieldOfView, 4);
                Assert.Equal(0.25f, cam.m_NearClipPlane, 4);
                Assert.Equal(2500f, cam.m_FarClipPlane, 4);
                Assert.Equal(1u, cam.m_ClearFlags);
                Assert.Equal(-1f, cam.m_Depth, 4);
                Assert.Equal(35f, cam.m_FocalLength, 4);
            }

            // Unity 5.6 Orthographic Camera (no 2018.2+ physical camera block)
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, 0, 101);
                bw.Write((byte)1); bw.Write(new byte[3]);
                bw.Write(2u); // SolidColor
                WriteColor(bw, new Color(0f, 0f, 0f, 1f));
                WriteVector4(bw, new Vector4(0f, 0f, 1f, 1f));
                bw.Write(0.1f);
                bw.Write(500f);
                bw.Write(60f);
                bw.Write((byte)1); bw.Write(new byte[3]); // orthographic = true
                bw.Write(12.5f); // orthographic size
                bw.Write(2f);    // depth

                var sf = CreateSyntheticSerializedFile(null, "level0", "5.6.7f1");
                var orthoCam = Assert.IsType<Camera>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.Camera, pathId: 601, ms.ToArray()));

                Assert.False(orthoCam.IsTruncated);
                Assert.True(orthoCam.m_Orthographic);
                Assert.Equal(12.5f, orthoCam.m_OrthographicSize, 4);
                Assert.Equal(2u, orthoCam.m_ClearFlags);
                Assert.Equal(2f, orthoCam.m_Depth, 4);
            }

            // Truncated Camera payload after Behaviour header -> safe defaults + IsTruncated
            using (var truncMs = new MemoryStream())
            using (var bw = new BinaryWriter(truncMs, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, 0, 102);
                bw.Write((byte)1); bw.Write(new byte[3]);
                bw.Write(1u); // truncated immediately after m_ClearFlags

                var sf = CreateSyntheticSerializedFile(null, "level0", "2021.3.16f1");
                var truncCam = Assert.IsType<Camera>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.Camera, pathId: 602, truncMs.ToArray()));
                Assert.True(truncCam.IsTruncated);
                Assert.Equal(60f, truncCam.m_FieldOfView, 4);
            }
        }

        [Fact]
        public void Light_ParsesSpotDirectionalPointAreaAndGuardsTruncation()
        {
            // Unity 2021.3 Spot Light (has m_Shape and m_InnerSpotAngle)
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, 0, 200); // m_GameObject
                bw.Write((byte)1); bw.Write(new byte[3]); // m_Enabled
                bw.Write((int)LightType.Spot);
                bw.Write(0); // m_Shape = Cone
                WriteColor(bw, new Color(1f, 0.85f, 0.6f, 1f));
                bw.Write(3.5f);  // m_Intensity
                bw.Write(25f);   // m_Range
                bw.Write(45f);   // m_SpotAngle
                bw.Write(20f);   // m_InnerSpotAngle
                bw.Write(10f);   // m_CookieSize
                // m_Shadows
                bw.Write(2);     // Soft shadows
                bw.Write(-1);    // m_Resolution
                bw.Write(2048);  // m_CustomResolution (5.4+)
                bw.Write(0.8f);  // m_Strength
                bw.Write(0.05f); // m_Bias
                bw.Write(0.4f);  // m_NormalBias
                bw.Write(0.2f);  // m_NearPlane

                var sf = CreateSyntheticSerializedFile(null, "level0", "2021.3.16f1");
                var light = Assert.IsType<Light>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.Light, pathId: 700, ms.ToArray()));

                Assert.False(light.IsTruncated);
                Assert.True(light.Enabled);
                Assert.Equal(LightType.Spot, light.m_Type);
                Assert.Equal(1f, light.m_Color.R, 4);
                Assert.Equal(0.85f, light.m_Color.G, 4);
                Assert.Equal(0.6f, light.m_Color.B, 4);
                Assert.Equal(3.5f, light.m_Intensity, 4);
                Assert.Equal(25f, light.m_Range, 4);
                Assert.Equal(45f, light.m_SpotAngle, 4);
                Assert.Equal(20f, light.m_InnerSpotAngle, 4);
                Assert.Equal(2, light.m_Shadows.m_Type);
                Assert.Equal(0.8f, light.m_Shadows.m_Strength, 4);
            }

            // Truncated Light payload -> safe defaults + IsTruncated
            using (var truncMs = new MemoryStream())
            using (var bw = new BinaryWriter(truncMs, Encoding.UTF8, leaveOpen: true))
            {
                WritePPtr64(bw, 0, 201);
                bw.Write((byte)1); bw.Write(new byte[3]);
                bw.Write((int)LightType.Directional);
                // truncated before m_Color

                var sf = CreateSyntheticSerializedFile(null, "level0", "2021.3.16f1");
                var truncLight = Assert.IsType<Light>(
                    CreateObjectViaAssetsManager(sf, ClassIDType.Light, pathId: 701, truncMs.ToArray()));
                Assert.True(truncLight.IsTruncated);
                Assert.Equal(LightType.Directional, truncLight.m_Type);
            }
        }

        // =========================================================================
        // 2. AssetsManager AssetBundle preloadSize == 0 Container Indexing Fix
        // =========================================================================

        [Fact]
        public void AssetsManager_LazyAssetBundleContainerIndexing_IncludesDirectAssetWhenPreloadSizeIsZero()
        {
            using var am = new AssetsManager { LazyLoading = true };
            var sf = CreateSyntheticSerializedFile(am, "CAB-scene-container-test", "2021.3.16f1");

            // Build an AssetBundle with EMPTY m_PreloadTable and preloadSize == 0,
            // where only assetInfo.asset points to the target asset (standard for scene & lightweight asset bundles).
            var ab = (AssetBundle)RuntimeHelpers.GetUninitializedObject(typeof(AssetBundle));
            ab.assetsFile = sf;
            ab.m_PathID = 1;
            ab.m_Name = "scene_bundle";
            ab.m_PreloadTable = Array.Empty<PPtr<Object>>();
            ab.m_Container = new[]
            {
                new KeyValuePair<string, AssetInfo>(
                    "assets/scenes/castle.unity",
                    new AssetInfoProxy(preloadIndex: 0, preloadSize: 0, new PPtr<Object>(0, 9001, sf))),
                new KeyValuePair<string, AssetInfo>(
                    "assets/prefabs/torch.prefab",
                    new AssetInfoProxy(preloadIndex: 0, preloadSize: 0, new PPtr<Object>(0, 9002, sf)))
            };

            var collected = new List<(PPtr<Object> PPtr, string Container)>();
            AssetsManager.CollectSafeAssetBundleContainersForLazyIndex(ab, collected);

            Assert.Equal(2, collected.Count);
            Assert.Contains(collected, x => x.PPtr.m_PathID == 9001 && x.Container == "assets/scenes/castle.unity");
            Assert.Contains(collected, x => x.PPtr.m_PathID == 9002 && x.Container == "assets/prefabs/torch.prefab");

            // Also verify deduplication when preloadSize > 0 already contains assetInfo.asset
            var sharedPtr = new PPtr<Object>(0, 9010, sf);
            var depPtr = new PPtr<Object>(0, 9011, sf);
            var abWithPreload = (AssetBundle)RuntimeHelpers.GetUninitializedObject(typeof(AssetBundle));
            abWithPreload.assetsFile = sf;
            abWithPreload.m_PathID = 2;
            abWithPreload.m_PreloadTable = new[] { sharedPtr, depPtr };
            abWithPreload.m_Container = new[]
            {
                new KeyValuePair<string, AssetInfo>(
                    "assets/prefabs/pillar.prefab",
                    new AssetInfoProxy(preloadIndex: 0, preloadSize: 2, sharedPtr))
            };

            var collectedWithPreload = new List<(PPtr<Object> PPtr, string Container)>();
            AssetsManager.CollectSafeAssetBundleContainersForLazyIndex(abWithPreload, collectedWithPreload);
            Assert.Equal(2, collectedWithPreload.Count);
            Assert.Equal(1, collectedWithPreload.Count(x => x.PPtr.m_PathID == 9010));
            Assert.Equal(1, collectedWithPreload.Count(x => x.PPtr.m_PathID == 9011));
        }

        [Fact]
        public void AssetsManager_LazyLoadRealBundleWithPreloadSizeZero_PopulatesArenaContainer()
        {
            var tempDir = CreateTempDirectory("lazy_bundle_preload_zero");
            try
            {
                // Build a real SerializedFile with:
                // - PathID 1: AssetBundle with empty m_PreloadTable and m_Container["assets/scenes/arena.unity"] -> preloadSize=0, asset=(0, 2)
                // - PathID 2: GameObject named "ArenaRoot"
                byte[] abBytes;
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    WriteAlignedUtf8String(bw, "scene_arena_bundle"); // NamedObject.m_Name
                    bw.Write(0); // m_PreloadTable count = 0
                    bw.Write(1); // m_Container count = 1
                    WriteAlignedUtf8String(bw, "assets/scenes/arena.unity");
                    bw.Write(0); // preloadIndex = 0
                    bw.Write(0); // preloadSize = 0 (key regression case!)
                    WritePPtr64(bw, 0, 2L); // assetInfo.asset -> PathID 2
                    bw.Flush();
                    abBytes = ms.ToArray();
                }

                byte[] goBytes;
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(0); // m_Components count = 0
                    bw.Write(0); // m_Layer
                    WriteAlignedUtf8String(bw, "ArenaRoot");
                    bw.Write((ushort)0); // m_Tag
                    bw.Write((byte)1);   // m_IsActive
                    bw.Flush();
                    goBytes = ms.ToArray();
                }

                var sfBytes = BuildSerializedFileWithObjects(
                    "2021.3.16f1",
                    new (long pathId, ClassIDType classId, byte[] payload)[]
                    {
                        (1L, ClassIDType.AssetBundle, abBytes),
                        (2L, ClassIDType.GameObject, goBytes)
                    });

                var bundlePath = Path.Combine(tempDir, "scene_arena.bundle");
                File.WriteAllBytes(bundlePath, BuildUncompressedUnityFsBundle("CAB-arena000000000000000000000000", sfBytes));

                using var am = new AssetsManager { LazyLoading = true, AutoLoadCatalogDependencies = false };
                am.LoadFiles(bundlePath);

                Assert.True(am.TryFindIndexedAssetByFileNameAndPathId("CAB-arena000000000000000000000000", 2L, out int arenaIdx, out _, out _, out _));
                Assert.Equal("assets/scenes/arena.unity", am.ProjectIndex.Arena.GetContainer(arenaIdx));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void TypeTreeFallback_ReadsBuildSettingsCameraAndLightFromEmbeddedTypeTree()
        {
            var sf = CreateSyntheticSerializedFile(null, "level0", "6000.9.0f1");

            // Embedded TypeTree for BuildSettings (tests TypeTree path independent of future binary layout changes)
            var bsTree = new TypeTree
            {
                m_Nodes = new List<TypeTreeNode>
                {
                    new TypeTreeNode { m_Level = 0, m_Type = "BuildSettings", m_Name = "Base", m_ByteSize = -1 },
                    new TypeTreeNode { m_Level = 1, m_Type = "vector", m_Name = "scenes", m_ByteSize = -1 },
                    new TypeTreeNode { m_Level = 2, m_Type = "Array", m_Name = "Array", m_ByteSize = -1, m_TypeFlags = 1 },
                    new TypeTreeNode { m_Level = 3, m_Type = "int", m_Name = "size", m_ByteSize = 4 },
                    new TypeTreeNode { m_Level = 3, m_Type = "string", m_Name = "data", m_ByteSize = -1 },
                    new TypeTreeNode { m_Level = 4, m_Type = "Array", m_Name = "Array", m_ByteSize = -1, m_TypeFlags = 1, m_MetaFlag = 0x4000 },
                    new TypeTreeNode { m_Level = 5, m_Type = "int", m_Name = "size", m_ByteSize = 4 },
                    new TypeTreeNode { m_Level = 5, m_Type = "char", m_Name = "data", m_ByteSize = 1 },
                    new TypeTreeNode { m_Level = 1, m_Type = "string", m_Name = "m_Version", m_ByteSize = -1 },
                    new TypeTreeNode { m_Level = 2, m_Type = "Array", m_Name = "Array", m_ByteSize = -1, m_TypeFlags = 1, m_MetaFlag = 0x4000 },
                    new TypeTreeNode { m_Level = 3, m_Type = "int", m_Name = "size", m_ByteSize = 4 },
                    new TypeTreeNode { m_Level = 3, m_Type = "char", m_Name = "data", m_ByteSize = 1 }
                }
            };

            using var bsMs = new MemoryStream();
            using (var bw = new BinaryWriter(bsMs, Encoding.UTF8, leaveOpen: true))
            {
                WriteStringArray(bw, new[] { "Assets/Scenes/Intro.unity", "Assets/Scenes/BossArena.unity" });
                WriteAlignedUtf8String(bw, "6000.9.0f1");
            }
            var bsBytes = bsMs.ToArray();
            using var bsReaderMs = new MemoryStream(bsBytes);
            using var bsEr = new EndianBinaryReader(bsReaderMs, EndianType.LittleEndian);
            var bsInfo = new ObjectInfo
            {
                m_PathID = 900,
                byteStart = 0,
                byteSize = (uint)bsBytes.Length,
                classID = (int)ClassIDType.BuildSettings,
                serializedType = new SerializedType { m_Type = bsTree }
            };
            using var bsObjReader = new ObjectReader(bsEr, sf, bsInfo);
            var parsedBs = Assert.IsType<BuildSettings>(AssetsManager.CreateObjectFromReader(bsObjReader));
            Assert.False(parsedBs.IsTruncated);
            Assert.Equal(2, parsedBs.scenes.Length);
            Assert.Equal("Assets/Scenes/BossArena.unity", parsedBs.scenes[1]);
            Assert.Equal("6000.9.0f1", parsedBs.m_Version);
        }

        // =========================================================================
        // 3. AddressablesCatalogDependencyResolver ByGuid / ByKey / SceneEntries
        // =========================================================================

        [Fact]
        public void AddressablesCatalog_Json_RetainsByGuidByKeyAndSceneProviderEntries()
        {
            var tempDir = CreateTempDirectory("addr_json_guid_scene");
            try
            {
                var aaDir = Path.Combine(tempDir, "Data", "StreamingAssets", "aa");
                var winDir = Path.Combine(aaDir, "StandaloneWindows64");
                Directory.CreateDirectory(winDir);

                var prefabBundlePath = Path.Combine(winDir, "prefabs_enemies.bundle");
                var matBundlePath = Path.Combine(winDir, "shared_materials.bundle");
                var sceneBundlePath = Path.Combine(winDir, "scene_dungeon.bundle");
                File.WriteAllBytes(prefabBundlePath, new byte[16]);
                File.WriteAllBytes(matBundlePath, new byte[16]);
                File.WriteAllBytes(sceneBundlePath, new byte[16]);

                const string goblinGuid = "0123456789abcdef0123456789abcdef";
                const string dungeonSceneGuid = "fedcba9876543210fedcba9876543210";

                var catalogPath = Path.Combine(aaDir, "catalog.json");
                var json = BuildJsonCatalogWithKeysAndScenes(
                    internalIds: new[]
                    {
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/prefabs_enemies.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/shared_materials.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/scene_dungeon.bundle",
                        "Assets/Prefabs/Goblin.prefab",
                        "Assets/Scenes/Dungeon.unity"
                    },
                    buckets: new (string key, int[] entryIndices)[]
                    {
                        ("dep_goblin", new[] { 0, 1 }),     // bucket 0: deps for Goblin (primary = bundle 0, dep = bundle 1)
                        ("dep_dungeon", new[] { 2 }),       // bucket 1: deps for Dungeon scene (primary = bundle 2)
                        ("Enemies/Goblin", new[] { 3 }),    // bucket 2: primary key for Goblin
                        (goblinGuid, new[] { 3 }),          // bucket 3: GUID key for Goblin
                        ("Scenes/Dungeon", new[] { 4 }),    // bucket 4: primary key for Dungeon scene
                        (dungeonSceneGuid, new[] { 4 })     // bucket 5: GUID key for Dungeon scene
                    },
                    entries: new (int internalIdIdx, int providerIdx, int depBucketIdx, int primaryKeyBucketIdx, string? bundleName)[]
                    {
                        (0, 0, -1, -1, "aaaa1111aaaa1111aaaa1111aaaa1111"),
                        (1, 0, -1, -1, "bbbb2222bbbb2222bbbb2222bbbb2222"),
                        (2, 0, -1, -1, "cccc3333cccc3333cccc3333cccc3333"),
                        (3, 1, 0, 2, null), // BundledAssetProvider -> Goblin.prefab
                        (4, 2, 1, 4, null)  // SceneProvider -> Dungeon.unity
                    });
                File.WriteAllText(catalogPath, json, Encoding.UTF8);

                AddressablesCatalogDependencyResolver.ClearCache();
                Assert.True(AddressablesCatalogDependencyResolver.TryParseJsonCatalog(catalogPath, out var parsed));
                Assert.NotNull(parsed);

                // GUID lookup works for both plain hex and formatted GUID
                Assert.True(parsed!.TryGetEntriesByGuid(goblinGuid, out var goblinByGuid));
                Assert.Single(goblinByGuid);
                Assert.Equal("Assets/Prefabs/Goblin.prefab", goblinByGuid[0].InternalId);
                Assert.Equal("Enemies/Goblin", goblinByGuid[0].PrimaryKey);
                Assert.Equal("prefabs_enemies.bundle", goblinByGuid[0].PrimaryBundleFileName);
                Assert.Single(goblinByGuid[0].DependencyBundleInternalIds);
                Assert.False(goblinByGuid[0].IsScene);

                Assert.True(parsed.TryGetEntriesByGuid("01234567-89AB-CDEF-0123-456789ABCDEF", out var goblinFormatted));
                Assert.Same(goblinByGuid[0], goblinFormatted[0]);

                // Key lookup works
                Assert.True(parsed.TryGetEntriesByKey("Enemies/Goblin", out var goblinByKey));
                Assert.Single(goblinByKey);

                // SceneEntries retains SceneProvider entries ONLY
                Assert.Single(parsed.SceneEntries);
                Assert.Equal("Assets/Scenes/Dungeon.unity", parsed.SceneEntries[0].InternalId);
                Assert.Equal("scene_dungeon.bundle", parsed.SceneEntries[0].PrimaryBundleFileName);
                Assert.True(parsed.SceneEntries[0].IsScene);

                // ResolveGuidToBundlePaths resolves primary + dependency local bundle paths
                Assert.True(AddressablesCatalogDependencyResolver.TryResolveGuidToBundlePaths(
                    new[] { parsed },
                    goblinGuid,
                    out var guidMatches));
                Assert.Single(guidMatches);
                Assert.Equal(Path.GetFullPath(prefabBundlePath), guidMatches[0].PrimaryBundlePath, ignoreCase: true);
                Assert.Single(guidMatches[0].DependencyBundlePaths);
                Assert.Equal(Path.GetFullPath(matBundlePath), guidMatches[0].DependencyBundlePaths[0], ignoreCase: true);
            }
            finally
            {
                AddressablesCatalogDependencyResolver.ClearCache();
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void AddressablesCatalog_Binary_RetainsByGuidByKeyAndSceneEntries()
        {
            const string crateGuid = "11223344556677889900aabbccddeeff";
            const string townSceneGuid = "ffeeddccbbaa00998877665544332211";

            var binBytes = BuildBinaryCatalogWithAssetsAndScenes(
                locatorId: "AddressablesMainContentCatalog",
                bundleInternalId: "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/env_pack.bundle",
                bundleName: "envpack1111111111111111111111111",
                prefabInternalId: "Assets/Prefabs/Crate.prefab",
                prefabKey: "Props/Crate",
                prefabGuid: crateGuid,
                sceneInternalId: "Assets/Scenes/Town.unity",
                sceneKey: "Scenes/Town",
                sceneGuid: townSceneGuid);

            Assert.True(AddressablesCatalogDependencyResolver.TryParseBinaryCatalogBytes(
                binBytes,
                "/virtual/aa/catalog.bin",
                "/virtual/aa",
                out var parsed));
            Assert.NotNull(parsed);

            Assert.Single(parsed!.Bundles);
            Assert.True(parsed.TryGetEntriesByGuid(crateGuid, out var crateEntries));
            Assert.Single(crateEntries);
            Assert.Equal("Assets/Prefabs/Crate.prefab", crateEntries[0].InternalId);
            Assert.Equal("env_pack.bundle", crateEntries[0].PrimaryBundleFileName);
            Assert.False(crateEntries[0].IsScene);

            Assert.True(parsed.TryGetEntriesByKey("Props/Crate", out _));
            Assert.Single(parsed.SceneEntries);
            Assert.Equal("Assets/Scenes/Town.unity", parsed.SceneEntries[0].InternalId);
            Assert.True(parsed.SceneEntries[0].IsScene);
            Assert.True(parsed.TryGetEntriesByGuid(townSceneGuid, out var sceneByGuid));
            Assert.Single(sceneByGuid);
            Assert.True(sceneByGuid[0].IsScene);
        }

        // =========================================================================
        // 4. ModelConverter LODGroup Filtering, Cameras/Lights, Extras & GltfExporter
        // =========================================================================

        [Fact]
        public void ModelConverter_Lod0OnlyVsAllLods_FiltersLowerLodRenderersAndKeepsSharedRenderers()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "level0", "2021.3.16f1");
            var boxMesh = CreateSyntheticUnitTriangleMesh(sf, 5001, "SM_Prop");
            var mat = CreateSyntheticMaterial(sf, 6001, "Mat_Prop");

            var rootGo = CreateSyntheticGameObject(sf, 100, "Tree_LODRoot", isActive: true, layer: 8, tag: 3);
            var rootTr = CreateSyntheticTransform(sf, 101, rootGo, father: null, position: Vector3.Zero);

            var lod0Go = CreateSyntheticGameObject(sf, 200, "Tree_LOD0", isActive: true);
            var lod0Tr = CreateSyntheticTransform(sf, 201, lod0Go, father: rootTr, position: Vector3.Zero);
            var lod0Mf = CreateSyntheticMeshFilter(sf, 202, lod0Go, 0, boxMesh.m_PathID);
            var lod0Mr = CreateSyntheticMeshRenderer(sf, 203, lod0Go, true, new[] { new PPtr<Material>(0, mat.m_PathID, sf) });
            AttachComponents(lod0Go, lod0Tr, lod0Mf, lod0Mr);

            var sharedGo = CreateSyntheticGameObject(sf, 210, "Tree_TrunkShared", isActive: true);
            var sharedTr = CreateSyntheticTransform(sf, 211, sharedGo, father: rootTr, position: Vector3.Zero);
            var sharedMf = CreateSyntheticMeshFilter(sf, 212, sharedGo, 0, boxMesh.m_PathID);
            var sharedMr = CreateSyntheticMeshRenderer(sf, 213, sharedGo, true, new[] { new PPtr<Material>(0, mat.m_PathID, sf) });
            AttachComponents(sharedGo, sharedTr, sharedMf, sharedMr);

            var lod1Go = CreateSyntheticGameObject(sf, 220, "Tree_LOD1", isActive: true);
            var lod1Tr = CreateSyntheticTransform(sf, 221, lod1Go, father: rootTr, position: Vector3.Zero);
            var lod1Mf = CreateSyntheticMeshFilter(sf, 222, lod1Go, 0, boxMesh.m_PathID);
            var lod1Mr = CreateSyntheticMeshRenderer(sf, 223, lod1Go, true, new[] { new PPtr<Material>(0, mat.m_PathID, sf) });
            AttachComponents(lod1Go, lod1Tr, lod1Mf, lod1Mr);

            var lod2Go = CreateSyntheticGameObject(sf, 230, "Tree_LOD2", isActive: true);
            var lod2Tr = CreateSyntheticTransform(sf, 231, lod2Go, father: rootTr, position: Vector3.Zero);
            var lod2Mf = CreateSyntheticMeshFilter(sf, 232, lod2Go, 0, boxMesh.m_PathID);
            var lod2Mr = CreateSyntheticMeshRenderer(sf, 233, lod2Go, true, new[] { new PPtr<Material>(0, mat.m_PathID, sf) });
            AttachComponents(lod2Go, lod2Tr, lod2Mf, lod2Mr);

            // LODGroup: LOD0 = {lod0Mr, sharedMr}, LOD1 = {lod1Mr, sharedMr}, LOD2 = {lod2Mr}
            var lodGroup = CreateSyntheticLodGroup(sf, 102, rootGo, enabled: true, new[]
            {
                (0.6f, new Renderer[] { lod0Mr, sharedMr }),
                (0.25f, new Renderer[] { lod1Mr, sharedMr }),
                (0.05f, new Renderer[] { lod2Mr })
            });

            rootTr.m_Children = new[]
            {
                new PPtr<Transform>(0, lod0Tr.m_PathID, sf),
                new PPtr<Transform>(0, sharedTr.m_PathID, sf),
                new PPtr<Transform>(0, lod1Tr.m_PathID, sf),
                new PPtr<Transform>(0, lod2Tr.m_PathID, sf)
            };
            AttachComponents(rootGo, rootTr, lodGroup);

            // 1. Default / Lod0Only: LOD1 & LOD2 excluded, LOD0 & Shared kept!
            var lod0Only = ModelConverter.CreateForScene(
                "TestScene",
                new[] { rootGo },
                sceneOptions: new SceneConversionOptions { LodExportMode = SceneLodExportMode.Lod0Only });

            Assert.Equal(2, lod0Only.MeshList.Count);
            Assert.Equal(2, lod0Only.SkippedLodRendererCount);
            Assert.Contains(lod0Only.MeshList, m => m.TargetFrame?.Name == "Tree_LOD0");
            Assert.Contains(lod0Only.MeshList, m => m.TargetFrame?.Name == "Tree_TrunkShared");
            Assert.DoesNotContain(lod0Only.MeshList, m => m.TargetFrame?.Name == "Tree_LOD1");
            Assert.DoesNotContain(lod0Only.MeshList, m => m.TargetFrame?.Name == "Tree_LOD2");

            // 2. AllLods: all 4 renderers exported
            var allLods = ModelConverter.CreateForScene(
                "TestScene",
                new[] { rootGo },
                sceneOptions: new SceneConversionOptions { LodExportMode = SceneLodExportMode.AllLods });

            Assert.Equal(4, allLods.MeshList.Count);
            Assert.Equal(0, allLods.SkippedLodRendererCount);
        }

        [Fact]
        public void GltfExporter_ExportsCamerasKhrLightsPunctualOrientationNodeExtrasAndPreserves2TierInstancing()
        {
            var tempDir = CreateTempDirectory("gltf_cameras_lights_extras");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2021.3.16f1");
                var mesh = CreateSyntheticUnitTriangleMesh(sf, 5001, "SM_Crate");
                var matRed = CreateSyntheticMaterial(sf, 6001, "Mat_Red");
                var matBlue = CreateSyntheticMaterial(sf, 6002, "Mat_Blue");

                var rootGo = CreateSyntheticGameObject(sf, 100, "SceneRoot", isActive: true, layer: 0, tag: 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, father: null, position: Vector3.Zero);

                // 3 Red Crates + 2 Blue Crates sharing the same Mesh -> verifies 2-tier instancing stays intact
                var children = new List<PPtr<Transform>>();
                for (int i = 0; i < 5; i++)
                {
                    var crateGo = CreateSyntheticGameObject(sf, 200 + i * 10, "Crate", isActive: true, layer: 9, tag: 4);
                    var crateTr = CreateSyntheticTransform(sf, 201 + i * 10, crateGo, father: rootTr, position: new Vector3(i * 3f, 0f, 0f));
                    var crateMf = CreateSyntheticMeshFilter(sf, 202 + i * 10, crateGo, 0, mesh.m_PathID);
                    var crateMr = CreateSyntheticMeshRenderer(sf, 203 + i * 10, crateGo, true,
                        new[] { new PPtr<Material>(0, i < 3 ? matRed.m_PathID : matBlue.m_PathID, sf) });
                    AttachComponents(crateGo, crateTr, crateMf, crateMr);
                    children.Add(new PPtr<Transform>(0, crateTr.m_PathID, sf));
                }

                // Perspective Main Camera (identity rotation -> Unity +Z must become glTF -Z rotated 180 deg around Y!)
                var mainCamGo = CreateSyntheticGameObject(sf, 300, "Main Camera", isActive: true, layer: 0, tag: 5);
                var mainCamTr = CreateSyntheticTransform(sf, 301, mainCamGo, father: rootTr, position: new Vector3(0f, 2f, -10f));
                var mainCam = CreateSyntheticCamera(sf, 302, mainCamGo, enabled: true, ortho: false, fov: 60f, orthoSize: 5f, near: 0.3f, far: 1000f);
                AttachComponents(mainCamGo, mainCamTr, mainCam);
                children.Add(new PPtr<Transform>(0, mainCamTr.m_PathID, sf));

                // Orthographic Minimap Camera
                var orthoCamGo = CreateSyntheticGameObject(sf, 310, "MinimapCam", isActive: true, layer: 5, tag: 0);
                var orthoCamTr = CreateSyntheticTransform(sf, 311, orthoCamGo, father: rootTr, position: new Vector3(0f, 50f, 0f));
                var orthoCam = CreateSyntheticCamera(sf, 312, orthoCamGo, enabled: true, ortho: true, fov: 60f, orthoSize: 20f, near: 0.1f, far: 200f);
                AttachComponents(orthoCamGo, orthoCamTr, orthoCam);
                children.Add(new PPtr<Transform>(0, orthoCamTr.m_PathID, sf));

                // Disabled Camera (must be skipped when SkipDisabledCamerasAndLights == true)
                var disabledCamGo = CreateSyntheticGameObject(sf, 320, "DisabledCam", isActive: true);
                var disabledCamTr = CreateSyntheticTransform(sf, 321, disabledCamGo, father: rootTr, position: Vector3.Zero);
                var disabledCam = CreateSyntheticCamera(sf, 322, disabledCamGo, enabled: false, ortho: false, fov: 45f, orthoSize: 5f, near: 0.3f, far: 100f);
                AttachComponents(disabledCamGo, disabledCamTr, disabledCam);
                children.Add(new PPtr<Transform>(0, disabledCamTr.m_PathID, sf));

                // Directional Light rotated +90 deg around Y in Unity LH (shines toward Unity +X -> glTF -X)
                var sunGo = CreateSyntheticGameObject(sf, 400, "SunLight", isActive: true, layer: 0, tag: 0);
                var unityYaw90 = new Quaternion(0f, MathF.Sin(MathF.PI * 0.25f), 0f, MathF.Cos(MathF.PI * 0.25f));
                var sunTr = CreateSyntheticTransform(sf, 401, sunGo, father: rootTr, position: new Vector3(0f, 10f, 0f), rotation: unityYaw90);
                var sunLight = CreateSyntheticLight(sf, 402, sunGo, enabled: true, LightType.Directional, new Color(1f, 0.95f, 0.85f, 1f), intensity: 1.5f, range: 50f, spotAngle: 30f, innerSpotAngle: 0f);
                AttachComponents(sunGo, sunTr, sunLight);
                children.Add(new PPtr<Transform>(0, sunTr.m_PathID, sf));

                // Point Light
                var pointGo = CreateSyntheticGameObject(sf, 410, "TorchLight", isActive: true);
                var pointTr = CreateSyntheticTransform(sf, 411, pointGo, father: rootTr, position: new Vector3(4f, 2f, 1f));
                var pointLight = CreateSyntheticLight(sf, 412, pointGo, enabled: true, LightType.Point, new Color(1f, 0.6f, 0.2f, 1f), intensity: 2.5f, range: 12f, spotAngle: 30f, innerSpotAngle: 0f);
                AttachComponents(pointGo, pointTr, pointLight);
                children.Add(new PPtr<Transform>(0, pointTr.m_PathID, sf));

                // Spot Light on a GameObject that ALSO has a MeshRenderer -> must use a child orientation node so mesh is NOT rotated 180 deg!
                var lampGo = CreateSyntheticGameObject(sf, 420, "SpotlightFixture", isActive: true, layer: 7, tag: 2);
                var lampTr = CreateSyntheticTransform(sf, 421, lampGo, father: rootTr, position: new Vector3(-2f, 5f, 0f));
                var lampMf = CreateSyntheticMeshFilter(sf, 422, lampGo, 0, mesh.m_PathID);
                var lampMr = CreateSyntheticMeshRenderer(sf, 423, lampGo, true, new[] { new PPtr<Material>(0, matRed.m_PathID, sf) });
                var spotLight = CreateSyntheticLight(sf, 424, lampGo, enabled: true, LightType.Spot, new Color(0.9f, 0.95f, 1f, 1f), intensity: 4f, range: 30f, spotAngle: 60f, innerSpotAngle: 30f);
                AttachComponents(lampGo, lampTr, lampMf, lampMr, spotLight);
                children.Add(new PPtr<Transform>(0, lampTr.m_PathID, sf));

                rootTr.m_Children = children.ToArray();
                AttachComponents(rootGo, rootTr);

                var converted = ModelConverter.CreateForScene(
                    "FullScene",
                    new[] { rootGo },
                    sceneOptions: new SceneConversionOptions
                    {
                        LodExportMode = SceneLodExportMode.Lod0Only,
                        IncludeCamerasAndLights = true,
                        SkipDisabledCamerasAndLights = true,
                        IncludeNodeProvenanceExtras = true
                    });

                Assert.Equal(2, converted.CameraList.Count);
                Assert.Equal(3, converted.LightList.Count);

                var glbPath = Path.Combine(tempDir, "full_scene.glb");
                var stats = ModelExporter.ExportGltfWithStats(glbPath, converted, binary: true, enableInstancing: true);

                // 2-tier instancing preserved across all 6 mesh instances (4 Red + 2 Blue -> 2 glTF meshes, 1 geometry buffer!)
                Assert.Equal(6, stats.TotalMeshInstances);
                Assert.Equal(2, stats.UniqueGltfMeshes);
                Assert.Equal(1, stats.UniqueGeometryBuffers);
                Assert.Equal(4, stats.ReusedMeshCount);
                Assert.Equal(5, stats.ReusedGeometryCount);
                Assert.Equal(2, stats.CamerasCount);
                Assert.Equal(3, stats.LightsCount);

                using var doc = ParseGlbJson(glbPath);
                var root = doc.RootElement;
                Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());

                // extensionsUsed contains KHR_lights_punctual
                var extensionsUsed = root.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("KHR_lights_punctual", extensionsUsed);

                // Validate cameras array (glTF 2.0 spec)
                var cameras = root.GetProperty("cameras");
                Assert.Equal(2, cameras.GetArrayLength());

                var persp = cameras[0];
                Assert.Equal("perspective", persp.GetProperty("type").GetString());
                var perspObj = persp.GetProperty("perspective");
                Assert.Equal(MathF.PI / 3f, perspObj.GetProperty("yfov").GetSingle(), 3); // 60 deg in radians
                Assert.Equal(0.3f, perspObj.GetProperty("znear").GetSingle(), 4);
                Assert.Equal(1000f, perspObj.GetProperty("zfar").GetSingle(), 4);

                var ortho = cameras[1];
                Assert.Equal("orthographic", ortho.GetProperty("type").GetString());
                var orthoObj = ortho.GetProperty("orthographic");
                Assert.Equal(20f, orthoObj.GetProperty("ymag").GetSingle(), 4);
                Assert.Equal(20f, orthoObj.GetProperty("xmag").GetSingle(), 4);
                Assert.Equal(0.1f, orthoObj.GetProperty("znear").GetSingle(), 4);
                Assert.Equal(200f, orthoObj.GetProperty("zfar").GetSingle(), 4);

                // Validate root extensions.KHR_lights_punctual.lights
                var lights = root.GetProperty("extensions").GetProperty("KHR_lights_punctual").GetProperty("lights");
                Assert.Equal(3, lights.GetArrayLength());

                // Directional light MUST NOT emit range or spot
                var dirLightJson = lights[0];
                Assert.Equal("directional", dirLightJson.GetProperty("type").GetString());
                Assert.Equal(1.5f, dirLightJson.GetProperty("intensity").GetSingle(), 4);
                Assert.False(dirLightJson.TryGetProperty("range", out _));
                Assert.False(dirLightJson.TryGetProperty("spot", out _));

                // Point light emits range and NO spot
                var pointLightJson = lights[1];
                Assert.Equal("point", pointLightJson.GetProperty("type").GetString());
                Assert.Equal(12f, pointLightJson.GetProperty("range").GetSingle(), 4);
                Assert.False(pointLightJson.TryGetProperty("spot", out _));

                // Spot light emits HALF angles in radians: 30 deg full -> 15 deg half (0.2618 rad), 60 deg full -> 30 deg half (0.5236 rad)
                var spotLightJson = lights[2];
                Assert.Equal("spot", spotLightJson.GetProperty("type").GetString());
                Assert.Equal(30f, spotLightJson.GetProperty("range").GetSingle(), 4);
                var spotProps = spotLightJson.GetProperty("spot");
                float innerRad = spotProps.GetProperty("innerConeAngle").GetSingle();
                float outerRad = spotProps.GetProperty("outerConeAngle").GetSingle();
                Assert.Equal(MathF.PI / 12f, innerRad, 3);
                Assert.Equal(MathF.PI / 6f, outerRad, 3);
                Assert.True(innerRad >= 0f && innerRad < outerRad && outerRad <= MathF.PI * 0.5f);

                // Validate nodes: camera attachment, directional orientation, mesh+spot child orientation node, and node.extras provenance
                var nodes = root.GetProperty("nodes").EnumerateArray().ToList();

                var mainCamNode = nodes.Single(n => n.GetProperty("name").GetString() == "Main Camera");
                Assert.Equal(0, mainCamNode.GetProperty("camera").GetInt32());
                var camExtras = mainCamNode.GetProperty("extras");
                Assert.Equal(300, camExtras.GetProperty("pathId").GetInt64());
                Assert.Equal("level0", camExtras.GetProperty("sourceFile").GetString());
                Assert.Equal(5, camExtras.GetProperty("tag").GetInt32());
                Assert.Equal(0, camExtras.GetProperty("layer").GetInt32());

                // Main Camera (Unity identity -> Unity forward +Z -> glTF forward +Z) must transform glTF -Z to (0, 0, +1)!
                var camForward = TransformGltfNegativeZByNodeRotation(mainCamNode);
                Assert.InRange(camForward.X, -1e-4f, 1e-4f);
                Assert.InRange(camForward.Y, -1e-4f, 1e-4f);
                Assert.InRange(camForward.Z, 0.999f, 1.001f);

                // SunLight (Unity yaw +90 deg -> Unity forward +X -> glTF -X after X-reflection) must transform glTF -Z to (-1, 0, 0)!
                var sunNode = nodes.Single(n => n.GetProperty("name").GetString() == "SunLight");
                Assert.Equal(0, sunNode.GetProperty("extensions").GetProperty("KHR_lights_punctual").GetProperty("light").GetInt32());
                var sunForward = TransformGltfNegativeZByNodeRotation(sunNode);
                Assert.InRange(sunForward.X, -1.001f, -0.999f);
                Assert.InRange(sunForward.Y, -1e-4f, 1e-4f);
                Assert.InRange(sunForward.Z, -1e-4f, 1e-4f);

                // SpotlightFixture has BOTH a Mesh and a SpotLight -> parent keeps mesh unrotated, child gets light + 180 deg Y flip!
                var fixtureNode = nodes.Single(n => n.GetProperty("name").GetString() == "SpotlightFixture");
                Assert.True(fixtureNode.TryGetProperty("mesh", out _));
                Assert.False(fixtureNode.TryGetProperty("rotation", out _));
                Assert.False(fixtureNode.TryGetProperty("extensions", out _));
                var fixtureChildIdx = fixtureNode.GetProperty("children")[0].GetInt32();
                var fixtureLightChild = nodes[fixtureChildIdx];
                Assert.Equal(2, fixtureLightChild.GetProperty("extensions").GetProperty("KHR_lights_punctual").GetProperty("light").GetInt32());
                var spotForward = TransformGltfNegativeZByNodeRotation(fixtureLightChild);
                Assert.InRange(spotForward.Z, 0.999f, 1.001f);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // =========================================================================
        // 5. SceneScriptSpawnAnalyzer: Static PPtr, Addressables GUID, Cycles, Caps, Dynamic Hints, Mono IL
        // =========================================================================

        [Fact]
        public void SceneScriptSpawnAnalyzer_ResolvesStaticPPtrAddressablesGuidMonoIlBoostAndAttachesToModelConverter()
        {
            var tempDir = CreateTempDirectory("script_spawn_e2e");
            try
            {
                using var am = new AssetsManager();
                var sceneFile = CreateSyntheticSerializedFile(am, "level0", "2021.3.16f1");
                var prefabFile = CreateSyntheticSerializedFile(am, "prefabs.assets", "2021.3.16f1");
                sceneFile.m_Externals.Add(new FileIdentifier { fileName = "prefabs.assets", pathName = "prefabs.assets" });

                // Build external Goblin prefab with a triangle mesh
                var goblinMesh = CreateSyntheticUnitTriangleMesh(prefabFile, 8001, "SM_Goblin");
                var goblinMat = CreateSyntheticMaterial(prefabFile, 8002, "Mat_Goblin");
                var goblinPrefab = CreateSyntheticGameObject(prefabFile, 7001, "GoblinPrefab", isActive: true, layer: 10, tag: 6);
                var goblinTr = CreateSyntheticTransform(prefabFile, 7002, goblinPrefab, father: null, position: Vector3.Zero);
                var goblinMf = CreateSyntheticMeshFilter(prefabFile, 7003, goblinPrefab, 0, goblinMesh.m_PathID);
                var goblinMr = CreateSyntheticMeshRenderer(prefabFile, 7004, goblinPrefab, true, new[] { new PPtr<Material>(0, goblinMat.m_PathID, prefabFile) });
                AttachComponents(goblinPrefab, goblinTr, goblinMf, goblinMr);

                // Build external Chest prefab referenced via Addressables GUID
                const string chestGuid = "abcdef0123456789abcdef0123456789";
                var chestPrefab = CreateSyntheticGameObject(prefabFile, 7101, "TreasureChestPrefab", isActive: true);
                var chestTr = CreateSyntheticTransform(prefabFile, 7102, chestPrefab, father: null, position: Vector3.Zero);
                var chestMf = CreateSyntheticMeshFilter(prefabFile, 7103, chestPrefab, 0, goblinMesh.m_PathID);
                var chestMr = CreateSyntheticMeshRenderer(prefabFile, 7104, chestPrefab, true, new[] { new PPtr<Material>(0, goblinMat.m_PathID, prefabFile) });
                AttachComponents(chestPrefab, chestTr, chestMf, chestMr);

                // Build scene hierarchy: RoomRoot -> SpawnerObj (with MonoBehaviour), SpawnPointAnchor (in-scene Transform), IntraSceneTarget (in-scene GO)
                var roomRoot = CreateSyntheticGameObject(sceneFile, 100, "RoomRoot", isActive: true);
                var roomTr = CreateSyntheticTransform(sceneFile, 101, roomRoot, father: null, position: Vector3.Zero);

                var anchorGo = CreateSyntheticGameObject(sceneFile, 110, "GoblinSpawnAnchor", isActive: true);
                var anchorTr = CreateSyntheticTransform(sceneFile, 111, anchorGo, father: roomTr, position: new Vector3(5f, 0f, 2f));
                AttachComponents(anchorGo, anchorTr);

                var intraSceneGo = CreateSyntheticGameObject(sceneFile, 120, "DoorAlreadyInScene", isActive: true);
                var intraSceneTr = CreateSyntheticTransform(sceneFile, 121, intraSceneGo, father: roomTr, position: new Vector3(1f, 0f, 0f));
                AttachComponents(intraSceneGo, intraSceneTr);

                var spawnerGo = CreateSyntheticGameObject(sceneFile, 130, "EncounterSpawner", isActive: true);
                var spawnerTr = CreateSyntheticTransform(sceneFile, 131, spawnerGo, father: roomTr, position: Vector3.Zero);

                // Point MonoScript to our real test assembly fixture class so pure-BCL MetadataReader IL check runs for real!
                var testAsmPath = typeof(SampleMonoSpawnerFixture).Assembly.Location;
                var testAsmDir = Path.GetDirectoryName(testAsmPath)!;
                var script = CreateSyntheticMonoScript(
                    sceneFile,
                    132,
                    className: nameof(SampleMonoSpawnerFixture),
                    ns: typeof(SampleMonoSpawnerFixture).Namespace ?? string.Empty,
                    assemblyName: Path.GetFileName(testAsmPath));

                var spawnerMb = CreateSyntheticMonoBehaviour(sceneFile, 133, spawnerGo, script, enabled: 1);
                AttachComponents(spawnerGo, spawnerTr, spawnerMb);

                roomTr.m_Children = new[]
                {
                    new PPtr<Transform>(0, anchorTr.m_PathID, sceneFile),
                    new PPtr<Transform>(0, intraSceneTr.m_PathID, sceneFile),
                    new PPtr<Transform>(0, spawnerTr.m_PathID, sceneFile)
                };
                AttachComponents(roomRoot, roomTr);

                // Synthetic Addressables catalog mapping chestGuid -> Assets/Prefabs/TreasureChest.prefab
                var catalog = new ParsedAddressablesCatalog
                {
                    CatalogPath = "/virtual/catalog.json",
                    CatalogDirectory = "/virtual",
                    Format = "json"
                };
                var chestCatalogEntry = new CatalogAssetEntry
                {
                    PrimaryKey = "Props/TreasureChest",
                    InternalId = "Assets/Prefabs/TreasureChest.prefab",
                    ProviderId = "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider",
                    PrimaryBundleInternalId = "{RuntimePath}/prefabs_bundle.bundle",
                    PrimaryBundleFileName = "prefabs_bundle.bundle",
                    IsScene = false
                };
                chestCatalogEntry.Guids.Add(chestGuid);
                catalog.IndexAssetEntry(chestCatalogEntry);

                // MonoBehaviour serialized fields:
                // 1) enemyPrefab: external PPtr<GameObject> -> goblinPrefab (paired with sibling spawnPoint anchor + offset)
                // 2) doorInScene: intra-scene PPtr<GameObject> -> intraSceneGo (MUST be ignored!)
                // 3) chestRef: Addressables AssetReference { m_AssetGUID = chestGuid }
                // 4) missingRef: Addressables AssetReference { m_AssetGUID = unknown } -> UnresolvedDynamicSpawners
                // 5) dynamicResourcePath: string "Prefabs/RandomBoss" -> UnresolvedDynamicSpawners
                var fields = new OrderedDictionary
                {
                    ["spawnPoint"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = anchorTr.m_PathID },
                    ["spawnOffset"] = new OrderedDictionary { ["x"] = 1.5f, ["y"] = 0f, ["z"] = -0.5f },
                    ["enemyPrefab"] = new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = goblinPrefab.m_PathID },
                    ["doorInScene"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = intraSceneGo.m_PathID },
                    ["chestRef"] = new OrderedDictionary { ["m_AssetGUID"] = chestGuid },
                    ["missingRef"] = new OrderedDictionary { ["m_AssetGUID"] = "99999999999999999999999999999999" },
                    ["prefabPath"] = "Prefabs/DynamicRuntimeBoss"
                };

                var analyzer = new SceneScriptSpawnAnalyzer();
                var analysis = analyzer.AnalyzeScene(
                    new[] { roomRoot },
                    new SceneScriptSpawnAnalysisOptions
                    {
                        AddressablesCatalogs = new[] { catalog },
                        ManagedAssemblySearchDirectories = new[] { testAsmDir },
                        EnableMonoIlConfidenceBoost = true,
                        CustomFieldProvider = mb => ReferenceEquals(mb, spawnerMb) ? (fields, null) : (null, null),
                        AddressablesPrefabResolver = (guid, _) => string.Equals(guid, chestGuid, StringComparison.OrdinalIgnoreCase) ? chestPrefab : null
                    });

                Assert.Equal(1, analysis.InspectedMonoBehaviourCount);
                Assert.False(analysis.WasTruncatedByCap);
                Assert.Equal(2, analysis.ResolvedSpawns.Count);

                // Verify PPtr spawn: anchored to GoblinSpawnAnchor, boosted by real Mono IL scan of SampleMonoSpawnerFixture!
                var pptrSpawn = Assert.Single(analysis.ResolvedSpawns, s => s.SourceKind == ScriptSpawnSourceKind.PPtrField);
                Assert.Equal("enemyPrefab", pptrSpawn.FieldPath);
                Assert.Equal(anchorTr.m_PathID, pptrSpawn.AnchorTransformPathId);
                Assert.Same(anchorTr, pptrSpawn.AnchorTransform);
                Assert.Same(goblinPrefab, pptrSpawn.ResolvedPrefabRoot);
                Assert.Equal(1.5f, pptrSpawn.LocalPositionOffset.X, 4);
                Assert.True(pptrSpawn.ConfirmedByMonoIl);
                Assert.Equal(ScriptSpawnConfidence.High, pptrSpawn.Confidence);

                // Verify Addressables GUID spawn
                var addrSpawn = Assert.Single(analysis.ResolvedSpawns, s => s.SourceKind == ScriptSpawnSourceKind.AddressablesGuid);
                Assert.Equal("chestRef", addrSpawn.FieldPath);
                Assert.Equal(chestGuid, addrSpawn.TargetGuid);
                Assert.Equal("prefabs_bundle.bundle", addrSpawn.TargetBundleFileName);
                Assert.Same(chestPrefab, addrSpawn.ResolvedPrefabRoot);

                // Verify intra-scene reference was NOT falsely treated as a spawned prefab
                Assert.DoesNotContain(analysis.ResolvedSpawns, s => s.FieldPath == "doorInScene");

                // Verify honest unresolved dynamic spawner hints (missing GUID + dynamic string prefabPath)
                Assert.Equal(2, analysis.UnresolvedDynamicSpawners.Count);
                Assert.Contains(analysis.UnresolvedDynamicSpawners, h => h.FieldPath == "missingRef" && h.CandidateReferences.Contains("99999999999999999999999999999999"));
                Assert.Contains(analysis.UnresolvedDynamicSpawners, h => h.FieldPath == "prefabPath" && h.CandidateReferences.Contains("Prefabs/DynamicRuntimeBoss"));

                // Verify attaching resolved spawns into ModelConverter.CreateForScene + GltfExporter!
                var converted = ModelConverter.CreateForScene(
                    "SpawnScene",
                    new[] { roomRoot },
                    sceneOptions: new SceneConversionOptions
                    {
                        IncludeNodeProvenanceExtras = true,
                        ScriptSpawnAttachments = analysis.ToModelConverterAttachments()
                    });

                Assert.Equal(2, converted.AttachedScriptSpawnCount);
                Assert.Equal(2, converted.MeshList.Count); // GoblinPrefab + TreasureChestPrefab both share SM_Goblin!

                var glbPath = Path.Combine(tempDir, "spawned_scene.glb");
                var stats = ModelExporter.ExportGltfWithStats(glbPath, converted, binary: true, enableInstancing: true);
                Assert.Equal(2, stats.TotalMeshInstances);
                Assert.Equal(1, stats.UniqueGltfMeshes); // Shared SM_Goblin + Mat_Goblin instanced across both spawned prefabs!

                using var doc = ParseGlbJson(glbPath);
                var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
                var spawnedGoblinNode = nodes.Single(n => n.GetProperty("name").GetString() == "[ScriptSpawned] GoblinPrefab");
                var spawnExtras = spawnedGoblinNode.GetProperty("extras");
                Assert.True(spawnExtras.GetProperty("scriptSpawned").GetBoolean());
                Assert.Equal("enemyPrefab", spawnExtras.GetProperty("scriptSpawn").GetProperty("fieldPath").GetString());
                Assert.Equal("PPtrField", spawnExtras.GetProperty("scriptSpawn").GetProperty("sourceKind").GetString());
                // Offset (1.5, 0, -0.5) in Unity becomes (-1.5, 0, -0.5) in glTF
                Assert.Equal(-1.5f, spawnedGoblinNode.GetProperty("translation")[0].GetSingle(), 4);
                Assert.Equal(-0.5f, spawnedGoblinNode.GetProperty("translation")[2].GetSingle(), 4);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void SceneScriptSpawnAnalyzer_ProtectsAgainstCyclesMaxRecursionDepthAndInstanceCaps()
        {
            using var am = new AssetsManager();
            var sceneFile = CreateSyntheticSerializedFile(am, "level0", "2021.3.16f1");
            var prefabFile = CreateSyntheticSerializedFile(am, "prefabs.assets", "2021.3.16f1");
            sceneFile.m_Externals.Add(new FileIdentifier { fileName = "prefabs.assets", pathName = "prefabs.assets" });
            prefabFile.m_Externals.Add(new FileIdentifier { fileName = "prefabs.assets", pathName = "prefabs.assets" });

            // 1. Cycle test: Scene -> PrefabA -> PrefabB -> PrefabA
            var prefabA = CreateSyntheticGameObject(prefabFile, 5001, "PrefabA", isActive: true);
            var prefabATr = CreateSyntheticTransform(prefabFile, 5002, prefabA, null, Vector3.Zero);
            var prefabAMb = CreateSyntheticMonoBehaviour(prefabFile, 5003, prefabA, script: null, enabled: 1);
            AttachComponents(prefabA, prefabATr, prefabAMb);

            var prefabB = CreateSyntheticGameObject(prefabFile, 5101, "PrefabB", isActive: true);
            var prefabBTr = CreateSyntheticTransform(prefabFile, 5102, prefabB, null, Vector3.Zero);
            var prefabBMb = CreateSyntheticMonoBehaviour(prefabFile, 5103, prefabB, script: null, enabled: 1);
            AttachComponents(prefabB, prefabBTr, prefabBMb);

            var sceneRoot = CreateSyntheticGameObject(sceneFile, 100, "CycleSceneRoot", isActive: true);
            var sceneTr = CreateSyntheticTransform(sceneFile, 101, sceneRoot, null, Vector3.Zero);
            var sceneMb = CreateSyntheticMonoBehaviour(sceneFile, 102, sceneRoot, script: null, enabled: 1);
            AttachComponents(sceneRoot, sceneTr, sceneMb);

            var cycleFields = new Dictionary<MonoBehaviour, OrderedDictionary>
            {
                [sceneMb] = new OrderedDictionary { ["prefabA"] = new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = prefabA.m_PathID } },
                [prefabAMb] = new OrderedDictionary { ["prefabB"] = new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = prefabB.m_PathID } },
                [prefabBMb] = new OrderedDictionary { ["prefabA"] = new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = prefabA.m_PathID } }
            };

            var analyzer = new SceneScriptSpawnAnalyzer();
            var cycleResult = analyzer.AnalyzeScene(
                new[] { sceneRoot },
                new SceneScriptSpawnAnalysisOptions
                {
                    MaxRecursionDepth = 5,
                    MaxSpawnedInstances = 50,
                    EnableMonoIlConfidenceBoost = false,
                    CustomFieldProvider = mb => cycleFields.TryGetValue(mb, out var f) ? (f, null) : (null, null)
                });

            Assert.Equal(2, cycleResult.ResolvedSpawns.Count); // PrefabA (depth 0) + PrefabB (depth 1); PrefabA back-edge blocked!
            Assert.True(cycleResult.CycleGuardsTriggered >= 1);

            // 2. Cap test: array of 20 prefab references capped at MaxSpawnedInstances = 5
            var arrayItems = new List<object>();
            for (int i = 0; i < 20; i++)
            {
                arrayItems.Add(new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = prefabA.m_PathID });
            }
            var capFields = new OrderedDictionary { ["enemyPrefabs"] = arrayItems };

            var capResult = analyzer.AnalyzeScene(
                new[] { sceneRoot },
                new SceneScriptSpawnAnalysisOptions
                {
                    MaxRecursionDepth = 0,
                    MaxSpawnedInstances = 5,
                    EnableMonoIlConfidenceBoost = false,
                    CustomFieldProvider = mb => ReferenceEquals(mb, sceneMb) ? (capFields, null) : (null, null)
                });

            Assert.Equal(5, capResult.ResolvedSpawns.Count);
            Assert.True(capResult.WasTruncatedByCap);
        }

        // =========================================================================
        // Managed IL Fixture Class (scanned by SceneScriptSpawnAnalyzer via MetadataReader)
        // =========================================================================

        private sealed class SampleMonoSpawnerFixture
        {
            public object? enemyPrefab = null;

            public object? SpawnNow()
            {
                return Instantiate(enemyPrefab);
            }

            private static object? Instantiate(object? prefab) => prefab;
        }

        private sealed class AssetInfoProxy : AssetInfo
        {
            public AssetInfoProxy(int preloadIndex, int preloadSize, PPtr<Object> asset)
                : base(CreateDummyAssetInfoReader())
            {
                this.preloadIndex = preloadIndex;
                this.preloadSize = preloadSize;
                this.asset = asset;
            }

            private static ObjectReader CreateDummyAssetInfoReader()
            {
                var bytes = new byte[20];
                var ms = new MemoryStream(bytes);
                var sf = CreateSyntheticSerializedFile(null, "dummy", "2021.3.16f1");
                var info = new ObjectInfo { byteStart = 0, byteSize = (uint)bytes.Length, classID = (int)ClassIDType.AssetBundle, m_PathID = 1 };
                return new ObjectReader(new EndianBinaryReader(ms, EndianType.LittleEndian), sf, info);
            }
        }

        // =========================================================================
        // Test Helpers
        // =========================================================================

        private static System.Numerics.Vector3 TransformGltfNegativeZByNodeRotation(JsonElement node)
        {
            var q = System.Numerics.Quaternion.Identity;
            if (node.TryGetProperty("rotation", out var rotProp))
            {
                q = new System.Numerics.Quaternion(
                    rotProp[0].GetSingle(),
                    rotProp[1].GetSingle(),
                    rotProp[2].GetSingle(),
                    rotProp[3].GetSingle());
            }

            var mat = System.Numerics.Matrix4x4.CreateFromQuaternion(q);
            return System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(0f, 0f, -1f), mat);
        }

        private static Object CreateObjectViaAssetsManager(SerializedFile sf, ClassIDType classId, long pathId, byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var er = new EndianBinaryReader(ms, EndianType.LittleEndian);
            var objInfo = new ObjectInfo
            {
                m_PathID = pathId,
                byteStart = 0,
                byteSize = (uint)payload.Length,
                classID = (int)classId,
                typeID = (int)classId
            };
            using var reader = new ObjectReader(er, sf, objInfo);
            return AssetsManager.CreateObjectFromReader(reader);
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager? manager, string fileName, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = manager!;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.reader = new FileReader(fileName, new MemoryStream());
            sf.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.RefactorTypeData };
            sf.m_TargetPlatform = BuildTarget.StandaloneWindows64;
            sf.SetVersion(unityVersion);
            sf.m_Externals = new List<FileIdentifier>();
            sf.m_Objects = new List<ObjectInfo>();
            sf.Objects = new List<Object>();
            sf.ObjectsDic = new Dictionary<long, Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            typeof(SerializedFile).GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(sf, new object());
            if (manager != null)
            {
                lock (manager.loadLock)
                {
                    manager.assetsFileList.Add(sf);
                }
            }
            return sf;
        }

        private static GameObject CreateSyntheticGameObject(
            SerializedFile sf,
            long pathId,
            string name,
            bool isActive,
            int layer = 0,
            ushort tag = 0)
        {
            var go = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            go.assetsFile = sf;
            go.m_PathID = pathId;
            go.version = sf.version;
            go.type = ClassIDType.GameObject;
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Layer = layer;
            go.m_Tag = tag;
            go.m_Components = Array.Empty<PPtr<Component>>();
            sf.AddObject(go);
            return go;
        }

        private static Transform CreateSyntheticTransform(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            Transform? father,
            Vector3 position,
            Quaternion? rotation = null,
            Vector3? scale = null)
        {
            var tr = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            tr.assetsFile = sf;
            tr.m_PathID = pathId;
            tr.version = sf.version;
            tr.type = ClassIDType.Transform;
            tr.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            tr.m_Father = father != null ? new PPtr<Transform>(0, father.m_PathID, sf) : new PPtr<Transform>(0, 0, sf);
            tr.m_LocalPosition = position;
            tr.m_LocalRotation = rotation ?? new Quaternion(0f, 0f, 0f, 1f);
            tr.m_LocalScale = scale ?? Vector3.One;
            tr.m_Children = Array.Empty<PPtr<Transform>>();
            sf.AddObject(tr);
            return tr;
        }

        private static MeshFilter CreateSyntheticMeshFilter(SerializedFile sf, long pathId, GameObject owner, int meshFileId, long meshPathId)
        {
            var mf = (MeshFilter)RuntimeHelpers.GetUninitializedObject(typeof(MeshFilter));
            mf.assetsFile = sf;
            mf.m_PathID = pathId;
            mf.version = sf.version;
            mf.type = ClassIDType.MeshFilter;
            mf.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            mf.m_Mesh = new PPtr<Mesh>(meshFileId, meshPathId, sf);
            sf.AddObject(mf);
            return mf;
        }

        private static MeshRenderer CreateSyntheticMeshRenderer(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            bool enabled,
            PPtr<Material>[] materials)
        {
            var mr = (MeshRenderer)RuntimeHelpers.GetUninitializedObject(typeof(MeshRenderer));
            mr.assetsFile = sf;
            mr.m_PathID = pathId;
            mr.version = sf.version;
            mr.type = ClassIDType.MeshRenderer;
            mr.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            mr.m_Enabled = enabled;
            mr.m_Materials = materials;
            sf.AddObject(mr);
            return mr;
        }

        private static LODGroup CreateSyntheticLodGroup(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            bool enabled,
            (float height, Renderer[] renderers)[] tiers)
        {
            var lg = (LODGroup)RuntimeHelpers.GetUninitializedObject(typeof(LODGroup));
            lg.assetsFile = sf;
            lg.m_PathID = pathId;
            lg.version = sf.version;
            lg.type = ClassIDType.LODGroup;
            lg.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            lg.m_Enabled = enabled;
            lg.m_LODs = tiers.Select(t => new LOD
            {
                screenRelativeHeight = t.height,
                renderers = t.renderers.Select(r => new PPtr<Renderer>(0, r.m_PathID, sf)).ToArray()
            }).ToArray();
            sf.AddObject(lg);
            return lg;
        }

        private static Camera CreateSyntheticCamera(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            bool enabled,
            bool ortho,
            float fov,
            float orthoSize,
            float near,
            float far)
        {
            var cam = (Camera)RuntimeHelpers.GetUninitializedObject(typeof(Camera));
            cam.assetsFile = sf;
            cam.m_PathID = pathId;
            cam.version = sf.version;
            cam.type = ClassIDType.Camera;
            cam.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            cam.m_Enabled = enabled ? (byte)1 : (byte)0;
            cam.m_Orthographic = ortho;
            cam.m_FieldOfView = fov;
            cam.m_OrthographicSize = orthoSize;
            cam.m_NearClipPlane = near;
            cam.m_FarClipPlane = far;
            cam.m_ClearFlags = 1;
            cam.m_NormalizedViewPortRect = new Vector4(0f, 0f, 1f, 1f);
            sf.AddObject(cam);
            return cam;
        }

        private static Light CreateSyntheticLight(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            bool enabled,
            LightType type,
            Color color,
            float intensity,
            float range,
            float spotAngle,
            float innerSpotAngle)
        {
            var light = (Light)RuntimeHelpers.GetUninitializedObject(typeof(Light));
            light.assetsFile = sf;
            light.m_PathID = pathId;
            light.version = sf.version;
            light.type = ClassIDType.Light;
            light.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            light.m_Enabled = enabled ? (byte)1 : (byte)0;
            light.m_Type = type;
            light.m_Color = color;
            light.m_Intensity = intensity;
            light.m_Range = range;
            light.m_SpotAngle = spotAngle;
            light.m_InnerSpotAngle = innerSpotAngle;
            light.m_Shadows = new ShadowSettings();
            sf.AddObject(light);
            return light;
        }

        private static MonoScript CreateSyntheticMonoScript(
            SerializedFile sf,
            long pathId,
            string className,
            string ns,
            string assemblyName)
        {
            var script = (MonoScript)RuntimeHelpers.GetUninitializedObject(typeof(MonoScript));
            script.assetsFile = sf;
            script.m_PathID = pathId;
            script.version = sf.version;
            script.type = ClassIDType.MonoScript;
            script.m_Name = className;
            script.m_ClassName = className;
            script.m_Namespace = ns;
            script.m_AssemblyName = assemblyName;
            sf.AddObject(script);
            return script;
        }

        private static MonoBehaviour CreateSyntheticMonoBehaviour(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            MonoScript? script,
            byte enabled)
        {
            var mb = (MonoBehaviour)RuntimeHelpers.GetUninitializedObject(typeof(MonoBehaviour));
            mb.assetsFile = sf;
            mb.m_PathID = pathId;
            mb.version = sf.version;
            mb.type = ClassIDType.MonoBehaviour;
            mb.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            mb.m_Enabled = enabled;
            mb.m_Script = script != null ? new PPtr<MonoScript>(0, script.m_PathID, sf) : new PPtr<MonoScript>(0, 0, sf);
            mb.m_Name = script?.m_ClassName ?? $"MonoBehaviour_{pathId}";
            sf.AddObject(mb);
            return mb;
        }

        private static void AttachComponents(GameObject go, params Component[] components)
        {
            go.m_Components = components.Select(c => new PPtr<Component>(0, c.m_PathID, go.assetsFile)).ToArray();
        }

        private static Mesh CreateSyntheticUnitTriangleMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            mesh.assetsFile = sf;
            mesh.m_PathID = pathId;
            mesh.version = sf.version;
            mesh.type = ClassIDType.Mesh;
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new[]
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f
            };
            mesh.m_Normals = new[]
            {
                0f, 0f, 1f,
                0f, 0f, 1f,
                0f, 0f, 1f
            };
            mesh.m_UV0 = new[]
            {
                0f, 0f,
                1f, 0f,
                0f, 1f
            };
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            var sub = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub.firstByte = 0;
            sub.indexCount = 3;
            sub.firstVertex = 0;
            sub.vertexCount = 3;
            sub.baseVertex = 0;
            mesh.m_SubMeshes = new[] { sub };
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            sf.AddObject(mesh);
            return mesh;
        }

        private static Material CreateSyntheticMaterial(SerializedFile sf, long pathId, string name)
        {
            var mat = (Material)RuntimeHelpers.GetUninitializedObject(typeof(Material));
            mat.assetsFile = sf;
            mat.m_PathID = pathId;
            mat.version = sf.version;
            mat.type = ClassIDType.Material;
            mat.m_Name = name;
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = Array.Empty<KeyValuePair<string, UnityTexEnv>>(),
                m_Floats = Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = new[] { new KeyValuePair<string, Color>("_BaseColor", new Color(0.8f, 0.8f, 0.8f, 1f)) }
            };
            sf.AddObject(mat);
            return mat;
        }

        private static string BuildJsonCatalogWithKeysAndScenes(
            string[] internalIds,
            (string key, int[] entryIndices)[] buckets,
            (int internalIdIdx, int providerIdx, int depBucketIdx, int primaryKeyBucketIdx, string? bundleName)[] entries)
        {
            using var keyMs = new MemoryStream();
            var keyOffsets = new int[buckets.Length];
            using (var kbw = new BinaryWriter(keyMs, Encoding.ASCII, leaveOpen: true))
            {
                kbw.Write(buckets.Length);
                for (int i = 0; i < buckets.Length; i++)
                {
                    keyOffsets[i] = (int)keyMs.Position;
                    kbw.Write((byte)0); // AsciiString
                    var bytes = Encoding.ASCII.GetBytes(buckets[i].key);
                    kbw.Write(bytes.Length);
                    kbw.Write(bytes);
                }
            }

            using var bucketMs = new MemoryStream();
            using (var bbw = new BinaryWriter(bucketMs, Encoding.UTF8, leaveOpen: true))
            {
                bbw.Write(buckets.Length);
                for (int i = 0; i < buckets.Length; i++)
                {
                    bbw.Write(keyOffsets[i]);
                    bbw.Write(buckets[i].entryIndices.Length);
                    foreach (var entryIdx in buckets[i].entryIndices)
                    {
                        bbw.Write(entryIdx);
                    }
                }
            }

            using var extraMs = new MemoryStream();
            using var entryMs = new MemoryStream();
            using (var ebw = new BinaryWriter(entryMs, Encoding.UTF8, leaveOpen: true))
            using (var xbw = new BinaryWriter(extraMs, Encoding.UTF8, leaveOpen: true))
            {
                ebw.Write(entries.Length);
                foreach (var entry in entries)
                {
                    int dataIdx = -1;
                    if (!string.IsNullOrEmpty(entry.bundleName))
                    {
                        dataIdx = (int)extraMs.Position;
                        var json = JsonSerializer.Serialize(new
                        {
                            m_BundleName = entry.bundleName,
                            m_Hash = entry.bundleName,
                            m_Crc = 1u
                        });
                        xbw.Write((byte)7); // JsonObject
                        var asm = Encoding.ASCII.GetBytes("Unity.ResourceManager");
                        xbw.Write((byte)asm.Length);
                        xbw.Write(asm);
                        var cls = Encoding.ASCII.GetBytes("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions");
                        xbw.Write((byte)cls.Length);
                        xbw.Write(cls);
                        var jsonBytes = Encoding.Unicode.GetBytes(json);
                        xbw.Write(jsonBytes.Length);
                        xbw.Write(jsonBytes);
                        xbw.Flush();
                    }

                    ebw.Write(entry.internalIdIdx);
                    ebw.Write(entry.providerIdx);
                    ebw.Write(entry.depBucketIdx);
                    ebw.Write(0); // depHash
                    ebw.Write(dataIdx);
                    ebw.Write(entry.primaryKeyBucketIdx);
                    ebw.Write(0); // resourceType
                }
            }

            var root = new
            {
                m_LocatorId = "AddressablesMainContentCatalog",
                m_ProviderIds = new[]
                {
                    "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
                    "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider",
                    "UnityEngine.ResourceManagement.ResourceProviders.SceneProvider"
                },
                m_InternalIdPrefixes = Array.Empty<string>(),
                m_InternalIds = internalIds,
                m_KeyDataString = Convert.ToBase64String(keyMs.ToArray()),
                m_BucketDataString = Convert.ToBase64String(bucketMs.ToArray()),
                m_EntryDataString = Convert.ToBase64String(entryMs.ToArray()),
                m_ExtraDataString = Convert.ToBase64String(extraMs.ToArray())
            };

            return JsonSerializer.Serialize(root);
        }

        private static byte[] BuildBinaryCatalogWithAssetsAndScenes(
            string locatorId,
            string bundleInternalId,
            string bundleName,
            string prefabInternalId,
            string prefabKey,
            string prefabGuid,
            string sceneInternalId,
            string sceneKey,
            string sceneGuid)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // 5 key entries: bundleName, prefabKey, prefabGuid (via ObjectTypeData), sceneKey, sceneGuid
            const uint keysOffset = 36u;
            const int keyCount = 5;
            const int keysByteSize = keyCount * 8;
            bw.Write(new byte[32]);
            bw.Write(keysByteSize);
            bw.Write(new byte[keysByteSize]);

            uint WriteAsciiString(string s)
            {
                var bytes = Encoding.ASCII.GetBytes(s);
                bw.Write(bytes.Length);
                uint offset = (uint)ms.Position;
                bw.Write(bytes);
                return offset;
            }

            uint WriteUInt32Array(uint[] values)
            {
                bw.Write(values.Length * 4);
                uint offset = (uint)ms.Position;
                foreach (var v in values) bw.Write(v);
                return offset;
            }

            uint idOffset = WriteAsciiString(locatorId);
            uint bundleProviderOff = WriteAsciiString("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider");
            uint assetProviderOff = WriteAsciiString("UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider");
            uint sceneProviderOff = WriteAsciiString("UnityEngine.ResourceManagement.ResourceProviders.SceneProvider");

            // Type record for ObjectTypeData key test (System.String)
            uint sysAsmOff = WriteAsciiString("mscorlib");
            uint sysStrOff = WriteAsciiString("System.String");
            uint stringTypeOff = (uint)ms.Position;
            bw.Write(sysAsmOff);
            bw.Write(sysStrOff);

            // Reserve 3 location records: 0=bundle, 1=prefab, 2=scene
            var locOffsets = new uint[3];
            for (int i = 0; i < 3; i++)
            {
                locOffsets[i] = (uint)ms.Position;
                bw.Write(new byte[28]);
            }

            // Location 0: bundle
            uint bundleKeyOff = WriteAsciiString(bundleName);
            uint bundleIdOff = WriteAsciiString(bundleInternalId);
            uint hashOff = (uint)ms.Position;
            bw.Write(new byte[16]);
            uint optBundleNameOff = WriteAsciiString(bundleName);
            uint optDataOff = (uint)ms.Position;
            bw.Write(hashOff);
            bw.Write(optBundleNameOff);
            bw.Write(99u);
            bw.Write(1024u);
            bw.Write(uint.MaxValue);
            uint bundleExtraOff = (uint)ms.Position;
            bw.Write(uint.MaxValue);
            bw.Write(optDataOff);

            WriteLocation(ms, bw, locOffsets[0], bundleKeyOff, bundleIdOff, bundleProviderOff, uint.MaxValue, bundleExtraOff);

            // Dependency set pointing to bundle location 0
            uint depToBundle0 = WriteUInt32Array(new[] { locOffsets[0] });

            // Location 1: prefab
            uint prefabKeyOff = WriteAsciiString(prefabKey);
            uint prefabIdOff = WriteAsciiString(prefabInternalId);
            WriteLocation(ms, bw, locOffsets[1], prefabKeyOff, prefabIdOff, assetProviderOff, depToBundle0, uint.MaxValue);

            // Location 2: scene
            uint sceneKeyOff = WriteAsciiString(sceneKey);
            uint sceneIdOff = WriteAsciiString(sceneInternalId);
            WriteLocation(ms, bw, locOffsets[2], sceneKeyOff, sceneIdOff, sceneProviderOff, depToBundle0, uint.MaxValue);

            // KeyData entries
            uint prefabGuidRawOff = WriteAsciiString(prefabGuid);
            uint prefabGuidObjOff = (uint)ms.Position;
            bw.Write(stringTypeOff);
            bw.Write(prefabGuidRawOff);

            uint sceneGuidOff = WriteAsciiString(sceneGuid);

            var keyRecords = new (uint keyObjOff, uint locSetOff)[]
            {
                (bundleKeyOff, WriteUInt32Array(new[] { locOffsets[0] })),
                (prefabKeyOff, WriteUInt32Array(new[] { locOffsets[1] })),
                (prefabGuidObjOff, WriteUInt32Array(new[] { locOffsets[1] })),
                (sceneKeyOff, WriteUInt32Array(new[] { locOffsets[2] })),
                (sceneGuidOff, WriteUInt32Array(new[] { locOffsets[2] }))
            };

            long endPos = ms.Position;
            for (int i = 0; i < keyRecords.Length; i++)
            {
                ms.Position = keysOffset + i * 8;
                bw.Write(keyRecords[i].keyObjOff);
                bw.Write(keyRecords[i].locSetOff);
            }

            ms.Position = 0;
            bw.Write(AddressablesCatalogDependencyResolver.BinaryCatalogMagic);
            bw.Write(2);
            bw.Write(keysOffset);
            bw.Write(idOffset);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            ms.Position = endPos;
            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteLocation(
            MemoryStream ms,
            BinaryWriter bw,
            uint locOffset,
            uint primaryKeyOff,
            uint internalIdOff,
            uint providerOff,
            uint depSetOff,
            uint extraDataOff)
        {
            long saved = ms.Position;
            ms.Position = locOffset;
            bw.Write(primaryKeyOff);
            bw.Write(internalIdOff);
            bw.Write(providerOff);
            bw.Write(depSetOff);
            bw.Write(0);
            bw.Write(extraDataOff);
            bw.Write(uint.MaxValue);
            ms.Position = saved;
        }

        private static void WriteAlignedUtf8String(BinaryWriter bw, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            bw.Write(bytes.Length);
            bw.Write(bytes);
            int mod = bytes.Length & 3;
            if (mod != 0)
            {
                bw.Write(new byte[4 - mod]);
            }
        }

        private static byte[] BuildSerializedFileWithObjects(
            string unityVersion,
            (long pathId, ClassIDType classId, byte[] payload)[] objects)
        {
            var distinctClassIds = objects.Select(o => (int)o.classId).Distinct().ToList();
            var classIdToTypeIndex = new Dictionary<int, int>();
            for (int i = 0; i < distinctClassIds.Count; i++)
            {
                classIdToTypeIndex[distinctClassIds[i]] = i;
            }

            // Build concatenated object data block (8-byte aligned per object)
            var offsets = new long[objects.Length];
            byte[] dataBlock;
            using (var dataMs = new MemoryStream())
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    while ((dataMs.Position & 7) != 0)
                    {
                        dataMs.WriteByte(0);
                    }
                    offsets[i] = dataMs.Position;
                    dataMs.Write(objects[i].payload, 0, objects[i].payload.Length);
                }
                dataBlock = dataMs.ToArray();
            }

            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var bw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.UTF8.GetBytes(unityVersion));
                bw.Write((byte)0);
                bw.Write((int)BuildTarget.StandaloneWindows64);
                bw.Write(false); // m_EnableTypeTree

                bw.Write(distinctClassIds.Count);
                foreach (var cid in distinctClassIds)
                {
                    bw.Write(cid);
                    bw.Write((byte)0); // m_IsStrippedType
                    bw.Write((short)-1); // m_ScriptTypeIndex
                    bw.Write(new byte[16]); // m_OldTypeHash
                }

                bw.Write(objects.Length);
                for (int i = 0; i < objects.Length; i++)
                {
                    while ((metaMs.Position & 3) != 0) metaMs.WriteByte(0);
                    bw.Write(objects[i].pathId);
                    bw.Write((uint)offsets[i]);
                    bw.Write((uint)objects[i].payload.Length);
                    bw.Write(classIdToTypeIndex[(int)objects[i].classId]);
                }

                bw.Write(0); // scriptCount
                bw.Write(0); // externalsCount
                bw.Write(0); // refTypesCount
                bw.Write((byte)0); // userInformation
                bw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            int dataOffset = (headerSize + metadata.Length + 15) & ~15;
            uint totalSize = (uint)(dataOffset + dataBlock.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), (uint)dataOffset);
            fullBytes[16] = 0; // LittleEndian
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            Buffer.BlockCopy(dataBlock, 0, fullBytes, dataOffset, dataBlock.Length);
            return fullBytes;
        }

        private static byte[] BuildUncompressedUnityFsBundle(string cabName, byte[] serializedFileBytes)
        {
            using var blocksInfoMs = new MemoryStream();
            blocksInfoMs.Write(new byte[16]); // hash
            WriteInt32BE(blocksInfoMs, 1); // 1 block
            WriteUInt32BE(blocksInfoMs, (uint)serializedFileBytes.Length);
            WriteUInt32BE(blocksInfoMs, (uint)serializedFileBytes.Length);
            WriteUInt16BE(blocksInfoMs, (ushort)CompressionType.None);
            WriteInt32BE(blocksInfoMs, 1); // 1 directory node
            WriteInt64BE(blocksInfoMs, 0L);
            WriteInt64BE(blocksInfoMs, serializedFileBytes.Length);
            WriteUInt32BE(blocksInfoMs, 4u);
            var cabBytes = Encoding.UTF8.GetBytes(cabName);
            blocksInfoMs.Write(cabBytes, 0, cabBytes.Length);
            blocksInfoMs.WriteByte(0);
            var blocksInfoBytes = blocksInfoMs.ToArray();

            using var headerMs = new MemoryStream();
            void WriteNullStr(string s)
            {
                var b = Encoding.UTF8.GetBytes(s);
                headerMs.Write(b, 0, b.Length);
                headerMs.WriteByte(0);
            }

            WriteNullStr("UnityFS");
            WriteUInt32BE(headerMs, 6u);
            WriteNullStr("5.x.x");
            WriteNullStr("2021.3.16f1");
            long totalSize = headerMs.Length + 8 + 4 + 4 + 4 + blocksInfoBytes.Length + serializedFileBytes.Length;
            WriteInt64BE(headerMs, totalSize);
            WriteUInt32BE(headerMs, (uint)blocksInfoBytes.Length);
            WriteUInt32BE(headerMs, (uint)blocksInfoBytes.Length);
            WriteUInt32BE(headerMs, (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined);
            headerMs.Write(blocksInfoBytes, 0, blocksInfoBytes.Length);
            headerMs.Write(serializedFileBytes, 0, serializedFileBytes.Length);
            return headerMs.ToArray();
        }

        private static void WriteUInt16BE(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
        private static void WriteInt32BE(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
        private static void WriteUInt32BE(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
        private static void WriteInt64BE(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }

        private static void WriteStringArray(BinaryWriter bw, string[] values)
        {
            bw.Write(values.Length);
            foreach (var s in values)
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                bw.Write(bytes.Length);
                bw.Write(bytes);
                int mod = bytes.Length & 3;
                if (mod != 0)
                {
                    bw.Write(new byte[4 - mod]);
                }
            }
        }

        private static void WritePPtr64(BinaryWriter bw, int fileId, long pathId)
        {
            bw.Write(fileId);
            bw.Write(pathId);
        }

        private static void WriteVector2(BinaryWriter bw, Vector2 v)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
        }

        private static void WriteVector3(BinaryWriter bw, Vector3 v)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
            bw.Write(v.Z);
        }

        private static void WriteVector4(BinaryWriter bw, Vector4 v)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
            bw.Write(v.Z);
            bw.Write(v.W);
        }

        private static void WriteColor(BinaryWriter bw, Color c)
        {
            bw.Write(c.R);
            bw.Write(c.G);
            bw.Write(c.B);
            bw.Write(c.A);
        }

        private static JsonDocument ParseGlbJson(string glbPath)
        {
            var bytes = File.ReadAllBytes(glbPath);
            Assert.True(bytes.Length >= 20);
            Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            Assert.Equal(0x4E4F534Au, jsonType);
            return JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, (int)jsonLen));
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), "AssetStudio_SmartSceneTests_" + prefix + "_" + Guid.NewGuid().ToString("N"));
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
