using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class Phase3SceneGlbExportTests
    {
        private readonly ITestOutputHelper _output;

        public Phase3SceneGlbExportTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void GltfExporter_SceneWith20DuplicateSiblingNamedTrees_InstancesSingleMeshAndPreservesEveryTransform()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var imported = BuildSyntheticInstancedForestScene(
                    treeCount: 20,
                    redCrateCount: 0,
                    blueCrateCount: 0);

                var instancedPath = Path.Combine(tempDir, "forest_instanced.glb");
                var nonInstancedPath = Path.Combine(tempDir, "forest_plain.glb");

                var instancedStats = ModelExporter.ExportGltfWithStats(
                    instancedPath,
                    imported,
                    binary: true,
                    skins: true,
                    animation: false,
                    blendShape: true,
                    enableInstancing: true);

                var plainStats = ModelExporter.ExportGltfWithStats(
                    nonInstancedPath,
                    imported,
                    binary: true,
                    skins: true,
                    animation: false,
                    blendShape: true,
                    enableInstancing: false);

                Assert.True(File.Exists(instancedPath));
                Assert.True(File.Exists(nonInstancedPath));

                // Stats must report 20 instances reusing 1 unique mesh & 1 unique geometry buffer (95% instanced)
                Assert.Equal(20, instancedStats.TotalMeshInstances);
                Assert.Equal(1, instancedStats.UniqueGltfMeshes);
                Assert.Equal(1, instancedStats.UniqueGeometryBuffers);
                Assert.Equal(19, instancedStats.ReusedMeshCount);
                Assert.Equal(19, instancedStats.ReusedGeometryCount);
                Assert.True(instancedStats.InstancingPercent >= 94.9);

                // Instanced GLB must be dramatically smaller than non-instanced GLB
                Assert.True(
                    instancedStats.OutputBytes < plainStats.OutputBytes,
                    $"Expected instanced GLB ({instancedStats.OutputBytes} B) to be smaller than non-instanced ({plainStats.OutputBytes} B).");

                using var doc = ParseGlbJson(instancedPath);
                var root = doc.RootElement;

                // Exactly 1 glTF mesh shared across all 20 Tree_Pine nodes
                Assert.Equal(1, root.GetProperty("meshes").GetArrayLength());
                Assert.Equal("SM_Tree_Pine", root.GetProperty("meshes")[0].GetProperty("name").GetString());

                // Verify ALL 20 sibling "Tree_Pine" nodes exist, each references mesh 0, and each has its OWN unique translation!
                var treeNodes = root.GetProperty("nodes")
                    .EnumerateArray()
                    .Where(n => n.TryGetProperty("name", out var nm) && nm.GetString() == "Tree_Pine")
                    .ToList();

                Assert.Equal(20, treeNodes.Count);
                var observedXPositions = new HashSet<float>();
                foreach (var treeNode in treeNodes)
                {
                    Assert.True(treeNode.TryGetProperty("mesh", out var meshProp));
                    Assert.Equal(0, meshProp.GetInt32());

                    float tx = treeNode.TryGetProperty("translation", out var transProp)
                        ? transProp[0].GetSingle()
                        : 0f;
                    observedXPositions.Add(tx);
                }

                // Every single sibling Tree_Pine got its own exact Transform instead of colliding on path "Scene/Environment/Tree_Pine"
                Assert.Equal(20, observedXPositions.Count);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_MaterialVariantsOnSharedGeometry_ReuseExactSameVertexAndIndexAccessors()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                // 10 Red Crates + 10 Blue Crates sharing the EXACT SAME box geometry (no explicit GeometryKey -> tests structural hash too!)
                var imported = BuildSyntheticInstancedForestScene(
                    treeCount: 0,
                    redCrateCount: 10,
                    blueCrateCount: 10,
                    omitGeometryKeys: true);

                var glbPath = Path.Combine(tempDir, "crates_variants.glb");
                var stats = ModelExporter.ExportGltfWithStats(glbPath, imported, binary: true, enableInstancing: true);

                Assert.Equal(20, stats.TotalMeshInstances);
                Assert.Equal(2, stats.UniqueGltfMeshes);         // 1 glTF mesh for Red, 1 for Blue
                Assert.Equal(1, stats.UniqueGeometryBuffers);    // ONLY 1 vertex/index buffer stored in binary chunk!
                Assert.Equal(18, stats.ReusedMeshCount);         // 9 Red + 9 Blue reused via Tier 1
                Assert.Equal(19, stats.ReusedGeometryCount);     // 19 of 20 instances reused geometry buffers!

                using var doc = ParseGlbJson(glbPath);
                var meshes = doc.RootElement.GetProperty("meshes");
                Assert.Equal(2, meshes.GetArrayLength());

                var prim0 = meshes[0].GetProperty("primitives")[0];
                var prim1 = meshes[1].GetProperty("primitives")[0];

                // Both material variants MUST reference the exact same POSITION, NORMAL, TEXCOORD_0, and indices accessors
                Assert.Equal(
                    prim0.GetProperty("attributes").GetProperty("POSITION").GetInt32(),
                    prim1.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                Assert.Equal(
                    prim0.GetProperty("attributes").GetProperty("NORMAL").GetInt32(),
                    prim1.GetProperty("attributes").GetProperty("NORMAL").GetInt32());
                Assert.Equal(
                    prim0.GetProperty("indices").GetInt32(),
                    prim1.GetProperty("indices").GetInt32());

                // Only the material slot differs!
                Assert.NotEqual(
                    prim0.GetProperty("material").GetInt32(),
                    prim1.GetProperty("material").GetInt32());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_NestedSceneHierarchy_PreservesParentChildChainsAndEmptyAnchorNodes()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var root = new ImportedFrame { Name = "Level0", LocalScale = new Vector3(1, 1, 1) };
                var building = new ImportedFrame
                {
                    Name = "Building_A",
                    LocalPosition = new Vector3(10, 0, 5),
                    LocalScale = new Vector3(1, 1, 1)
                };
                var floor = new ImportedFrame
                {
                    Name = "Floor_1",
                    LocalPosition = new Vector3(0, 3.5f, 0),
                    LocalScale = new Vector3(1, 1, 1)
                };
                var spawnAnchor = new ImportedFrame
                {
                    Name = "EmptySpawnAnchor",
                    LocalPosition = new Vector3(1, 0, -2),
                    LocalScale = new Vector3(1, 1, 1)
                };
                var prop = new ImportedFrame
                {
                    Name = "Lamp",
                    LocalPosition = new Vector3(2, 1, 0),
                    LocalScale = new Vector3(0.5f, 0.5f, 0.5f)
                };

                root.AddChild(building);
                building.AddChild(floor);
                floor.AddChild(spawnAnchor);
                floor.AddChild(prop);

                var imported = new SyntheticSceneImported(root);
                imported.MaterialList.Add(CreateSimpleMaterial("Mat_Lamp", 1f, 0.9f, 0.6f));
                imported.MeshList.Add(CreateUnitBoxMesh("Level0/Building_A/Floor_1/Lamp", prop, "SM_Lamp", "geom:lamp", "Mat_Lamp"));

                var glbPath = Path.Combine(tempDir, "nested_level.glb");
                ModelExporter.ExportGltf(glbPath, imported, binary: true, enableInstancing: true);

                using var doc = ParseGlbJson(glbPath);
                var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
                var byName = nodes
                    .Select((el, idx) => (Name: el.GetProperty("name").GetString()!, Index: idx, Node: el))
                    .ToDictionary(x => x.Name, x => x);

                Assert.True(byName.ContainsKey("Level0"));
                Assert.True(byName.ContainsKey("Building_A"));
                Assert.True(byName.ContainsKey("Floor_1"));
                Assert.True(byName.ContainsKey("EmptySpawnAnchor"));
                Assert.True(byName.ContainsKey("Lamp"));

                // Verify exact parent -> child links
                Assert.Contains(byName["Building_A"].Index, GetChildren(byName["Level0"].Node));
                Assert.Contains(byName["Floor_1"].Index, GetChildren(byName["Building_A"].Node));
                Assert.Contains(byName["EmptySpawnAnchor"].Index, GetChildren(byName["Floor_1"].Node));
                Assert.Contains(byName["Lamp"].Index, GetChildren(byName["Floor_1"].Node));

                // Empty anchor has no mesh, Lamp has mesh 0 and scale [0.5, 0.5, 0.5]
                Assert.False(byName["EmptySpawnAnchor"].Node.TryGetProperty("mesh", out _));
                Assert.Equal(0, byName["Lamp"].Node.GetProperty("mesh").GetInt32());
                Assert.Equal(0.5f, byName["Lamp"].Node.GetProperty("scale")[0].GetSingle(), 4);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task MainWindowViewModel_SceneExportCommands_WorkHeadlessly()
        {
            using var vm = new MainWindowViewModel();
            var sceneCalls = 0;
            var subtreeCalls = 0;

            vm.CommandHandlers.ExportSceneGlbAsync = () =>
            {
                sceneCalls++;
                return Task.CompletedTask;
            };
            vm.CommandHandlers.ExportSelectedSubtreeGlbAsync = () =>
            {
                subtreeCalls++;
                return Task.CompletedTask;
            };

            await vm.ExportSceneGlbCommand.ExecuteAsync();
            await vm.ExportSelectedSubtreeGlbCommand.ExecuteAsync();

            Assert.Equal(1, sceneCalls);
            Assert.Equal(1, subtreeCalls);
        }

        [Fact]
        public void SceneGlbExportService_OptionalLocalSceneFile_ExportsValidInstancedGlbWhenConfigured()
        {
            var levelPath = Environment.GetEnvironmentVariable("ASSETSTUDIO_TEST_SCENE_FILE");
            if (string.IsNullOrWhiteSpace(levelPath) || !File.Exists(levelPath))
            {
                _output.WriteLine("Skipping optional external scene file test (ASSETSTUDIO_TEST_SCENE_FILE not set).");
                return;
            }

            var tempDir = CreateTempDirectory();
            try
            {
                using var am = new AssetsManager();
                am.AutoLoadExternalDependencies = false;
                am.LoadFiles(levelPath);
                Assert.NotEmpty(am.assetsFileList);

                var roots = SceneGlbExportService.CollectRootGameObjectsFromSerializedFile(am.assetsFileList[0]);
                Assert.NotEmpty(roots);

                var outGlb = Path.Combine(tempDir, "scene_export.glb");
                var service = new SceneGlbExportService();
                var result = service.ExportScene(
                    roots,
                    new SceneGlbExportOptions
                    {
                        OutputPath = outGlb,
                        SceneName = Path.GetFileNameWithoutExtension(levelPath),
                        IncludeInactive = true,
                        IncludeAnimations = false,
                        EnableInstancing = true
                    });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(File.Exists(outGlb));
                Assert.True(result.Stats.TotalNodes >= roots.Count);

                using var doc = ParseGlbJson(outGlb);
                Assert.Equal("2.0", doc.RootElement.GetProperty("asset").GetProperty("version").GetString());
                _output.WriteLine(result.SummaryMessage);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void SceneGlbExportService_EmptyInputsAndHelperMethods_HandleEdgeCasesCleanly()
        {
            var service = new SceneGlbExportService();
            var emptyPathResult = service.ExportScene(
                Array.Empty<GameObject>(),
                new SceneGlbExportOptions { OutputPath = string.Empty, SceneName = "Empty" });
            Assert.False(emptyPathResult.Success);
            Assert.False(string.IsNullOrWhiteSpace(emptyPathResult.ErrorMessage));

            var emptyRootsResult = service.ExportScene(
                Array.Empty<GameObject>(),
                new SceneGlbExportOptions { OutputPath = Path.Combine(Path.GetTempPath(), "empty_scene.glb"), SceneName = "Empty" });
            Assert.False(emptyRootsResult.Success);
            Assert.Contains("No GameObjects", emptyRootsResult.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

            // Scene root traversal climbs to top-level scene node
            var sceneRoot = new GameObjectNode { Name = "level0" };
            var childA = new GameObjectNode { Name = "Environment" };
            var childB = new GameObjectNode { Name = "Tree_01" };
            sceneRoot.AddChild(childA);
            childA.AddChild(childB);
            Assert.Same(sceneRoot, SceneGlbExportService.FindSceneRootNode(childB));
            Assert.Same(sceneRoot, SceneGlbExportService.FindSceneRootNode(sceneRoot));
        }

        [Fact]
        public void SceneGlbExportService_LazyCrossBundleMeshesMaterialsAndTextures_MatchExportModelWithInstancing()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                using var sceneManager = new AssetsManager();
                var sceneFile = CreateSyntheticSerializedFile(sceneManager, "CAB-scene-001");

                // Simulate external bundles that are NOT loaded in sceneManager.assetsFileList (Lazy Mode!)
                using var meshBundleManager = new AssetsManager();
                var meshBundleFile = CreateSyntheticSerializedFile(meshBundleManager, "CAB-meshes-002");
                var externalMesh = CreateSyntheticTwoSubmeshMesh(meshBundleFile, pathId: 5001, name: "SM_ShopBuilding");

                using var matBundleManager = new AssetsManager();
                var matBundleFile = CreateSyntheticSerializedFile(matBundleManager, "CAB-materials-003");
                using var texBundleManager = new AssetsManager();
                var texBundleFile = CreateSyntheticSerializedFile(texBundleManager, "CAB-textures-004");

                var roofTex = CreateSyntheticRgbaTexture(texBundleFile, pathId: 7001, name: "T_ShopRoof_D", 140, 60, 180, 255);
                var starTex = CreateSyntheticRgbaTexture(texBundleFile, pathId: 7002, name: "T_ShopStar_Cutout", 255, 220, 40, 255);

                var roofMat = CreateSyntheticMaterial(
                    matBundleFile,
                    pathId: 6001,
                    name: "Mat_ShopRoof",
                    texEnvs: new[]
                    {
                        new KeyValuePair<string, UnityTexEnv>("_BaseMap", CreateTexEnv(matBundleFile, fileId: 1, pathId: 7001))
                    });

                var starMat = CreateSyntheticMaterial(
                    matBundleFile,
                    pathId: 6002,
                    name: "Mat_ShopStarCutout",
                    texEnvs: new[]
                    {
                        new KeyValuePair<string, UnityTexEnv>("_BaseMap", CreateTexEnv(matBundleFile, fileId: 1, pathId: 7002))
                    },
                    floats: new[]
                    {
                        new KeyValuePair<string, float>("_AlphaClip", 1f),
                        new KeyValuePair<string, float>("_Cutoff", 0.5f)
                    });

                // Build an UNHYDRATED Lazy Mode scene hierarchy (GameObject.m_Transform / m_MeshRenderer / m_MeshFilter are null;
                // only m_Components PPtrs exist — exact state of Lazy Mode before eager ProcessAssets!)
                var rootGo = CreateSyntheticGameObject(sceneFile, 100, "ShopRoot", isActive: true);
                var rootTr = CreateSyntheticTransform(sceneFile, 101, rootGo, father: null, position: Vector3.Zero);

                var shopA = CreateSyntheticGameObject(sceneFile, 200, "ShopBuilding", isActive: true);
                var shopATr = CreateSyntheticTransform(sceneFile, 201, shopA, father: rootTr, position: new Vector3(0, 0, 0));
                var shopAMf = CreateSyntheticMeshFilter(sceneFile, 202, shopA, meshFileId: 1, meshPathId: 5001);
                var shopAMr = CreateSyntheticMeshRenderer(
                    sceneFile,
                    203,
                    shopA,
                    enabled: true,
                    materials: new[]
                    {
                        CreatePPtr<Material>(sceneFile, fileId: 2, pathId: 6001),
                        CreatePPtr<Material>(sceneFile, fileId: 2, pathId: 6002)
                    });
                AttachComponentsOnly(shopA, shopATr, shopAMf, shopAMr);

                // Second sibling with the EXACT SAME name referencing the same external mesh + materials -> tests instancing!
                var shopB = CreateSyntheticGameObject(sceneFile, 300, "ShopBuilding", isActive: true);
                var shopBTr = CreateSyntheticTransform(sceneFile, 301, shopB, father: rootTr, position: new Vector3(12f, 0f, -4f));
                var shopBMf = CreateSyntheticMeshFilter(sceneFile, 302, shopB, meshFileId: 1, meshPathId: 5001);
                var shopBMr = CreateSyntheticMeshRenderer(
                    sceneFile,
                    303,
                    shopB,
                    enabled: true,
                    materials: new[]
                    {
                        CreatePPtr<Material>(sceneFile, fileId: 2, pathId: 6001),
                        CreatePPtr<Material>(sceneFile, fileId: 2, pathId: 6002)
                    });
                AttachComponentsOnly(shopB, shopBTr, shopBMf, shopBMr);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sceneFile, 0, shopATr.m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, shopBTr.m_PathID)
                };
                AttachComponentsOnly(rootGo, rootTr);

                // Collect roots from the unhydrated SerializedFile
                var roots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(
                    new[] { sceneFile },
                    out var usedInactiveFallback);
                Assert.False(usedInactiveFallback);
                Assert.Single(roots);
                Assert.Same(rootGo, roots[0]);

                int meshResolverCalls = 0;
                int materialResolverCalls = 0;
                var outGlb = Path.Combine(tempDir, "lazy_shop_parity.glb");
                var service = new SceneGlbExportService();
                var result = service.ExportScene(
                    roots,
                    new SceneGlbExportOptions
                    {
                        OutputPath = outGlb,
                        SceneName = "CAB-scene-001",
                        IncludeInactive = false,
                        SkipDisabledRenderers = true,
                        EnableInstancing = true
                    },
                    textureResolver: (mat, prop, texRef) =>
                    {
                        if (texRef != null && texRef.m_PathID == 7001) return roofTex;
                        if (texRef != null && texRef.m_PathID == 7002) return starTex;
                        return null;
                    },
                    meshResolver: r =>
                    {
                        meshResolverCalls++;
                        return externalMesh;
                    },
                    materialResolver: (r, mesh, slot) =>
                    {
                        materialResolverCalls++;
                        return slot == 0 ? roofMat : (slot == 1 ? starMat : null);
                    });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(File.Exists(outGlb));
                Assert.True(meshResolverCalls >= 2);
                Assert.True(materialResolverCalls >= 4);

                // Full parity: 2 materials, 2 textures, 0 fallback gray materials, AND glTF 2.0 instancing preserved!
                Assert.Equal(2, result.Stats.MaterialsCount);
                Assert.Equal(2, result.Stats.TexturesCount);
                Assert.Equal(2, result.Stats.TotalMeshInstances);
                Assert.Equal(1, result.Stats.UniqueGltfMeshes);
                Assert.Equal(1, result.Stats.UniqueGeometryBuffers);
                Assert.Equal(1, result.Stats.ReusedMeshCount);

                using var doc = ParseGlbJson(outGlb);
                var materials = doc.RootElement.GetProperty("materials").EnumerateArray().ToList();
                Assert.Equal(2, materials.Count);

                var gltfRoof = materials.Single(m => m.GetProperty("name").GetString() == "Mat_ShopRoof");
                var gltfStar = materials.Single(m => m.GetProperty("name").GetString() == "Mat_ShopStarCutout");

                Assert.True(gltfRoof.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
                Assert.True(gltfStar.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
                Assert.Equal("MASK", gltfStar.GetProperty("alphaMode").GetString());
                Assert.Equal(0.5f, gltfStar.GetProperty("alphaCutoff").GetSingle(), 3);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ModelConverter_CreateForScene_FiltersInactiveObjectsAndDisabledRenderers_AndFallsBackWhenAllInactive()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-filter-scene");
            var mesh = CreateSyntheticTwoSubmeshMesh(sf, 4001, "SM_Prop");
            var mat = CreateSyntheticMaterial(sf, 4002, "Mat_Prop", Array.Empty<KeyValuePair<string, UnityTexEnv>>());

            var rootGo = CreateSyntheticGameObject(sf, 10, "SceneRoot", isActive: true);
            var rootTr = CreateSyntheticTransform(sf, 11, rootGo, null, Vector3.Zero);

            var visibleGo = CreateSyntheticGameObject(sf, 20, "VisibleProp", isActive: true);
            var visibleTr = CreateSyntheticTransform(sf, 21, visibleGo, rootTr, new Vector3(1, 0, 0));
            var visibleMf = CreateSyntheticMeshFilter(sf, 22, visibleGo, 0, mesh.m_PathID);
            var visibleMr = CreateSyntheticMeshRenderer(sf, 23, visibleGo, enabled: true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID), CreatePPtr<Material>(sf, 0, mat.m_PathID) });
            AttachComponentsOnly(visibleGo, visibleTr, visibleMf, visibleMr);

            var disabledRendererGo = CreateSyntheticGameObject(sf, 30, "DisabledShadowProxy", isActive: true);
            var disabledTr = CreateSyntheticTransform(sf, 31, disabledRendererGo, rootTr, new Vector3(2, 0, 0));
            var disabledMf = CreateSyntheticMeshFilter(sf, 32, disabledRendererGo, 0, mesh.m_PathID);
            var disabledMr = CreateSyntheticMeshRenderer(sf, 33, disabledRendererGo, enabled: false, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID), CreatePPtr<Material>(sf, 0, mat.m_PathID) });
            AttachComponentsOnly(disabledRendererGo, disabledTr, disabledMf, disabledMr);

            var inactiveGo = CreateSyntheticGameObject(sf, 40, "InactiveLodProxy", isActive: false);
            var inactiveTr = CreateSyntheticTransform(sf, 41, inactiveGo, rootTr, new Vector3(3, 0, 0));
            var inactiveMf = CreateSyntheticMeshFilter(sf, 42, inactiveGo, 0, mesh.m_PathID);
            var inactiveMr = CreateSyntheticMeshRenderer(sf, 43, inactiveGo, enabled: true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID), CreatePPtr<Material>(sf, 0, mat.m_PathID) });
            AttachComponentsOnly(inactiveGo, inactiveTr, inactiveMf, inactiveMr);

            rootTr.m_Children = new[]
            {
                CreatePPtr<Transform>(sf, 0, visibleTr.m_PathID),
                CreatePPtr<Transform>(sf, 0, disabledTr.m_PathID),
                CreatePPtr<Transform>(sf, 0, inactiveTr.m_PathID)
            };
            AttachComponentsOnly(rootGo, rootTr);

            // Default clean scene export: IncludeInactive = false, SkipDisabledRenderers = true
            var cleanConvert = ModelConverter.CreateForScene(
                "FilterScene",
                new[] { rootGo },
                includeInactive: false,
                skipDisabledRenderers: true);

            Assert.Single(cleanConvert.MeshList);
            Assert.Equal("VisibleProp", cleanConvert.MeshList[0].TargetFrame?.Name);
            // Disabled renderer's Transform node is still preserved (only its hidden MeshRenderer is skipped),
            // whereas the inactive subtree is excluded completely.
            Assert.NotNull(cleanConvert.RootFrame.FindFrameByPath("FilterScene/SceneRoot/DisabledShadowProxy"));
            Assert.Null(cleanConvert.RootFrame.FindFrameByPath("FilterScene/SceneRoot/InactiveLodProxy"));

            // With includeInactive: true and skipDisabledRenderers: false -> all 3 renderers are exported
            var fullConvert = ModelConverter.CreateForScene(
                "FilterScene",
                new[] { rootGo },
                includeInactive: true,
                skipDisabledRenderers: false);
            Assert.Equal(3, fullConvert.MeshList.Count);

            // All-inactive prefab SerializedFile must fall back cleanly instead of returning 0 roots
            using var prefabAm = new AssetsManager();
            var prefabSf = CreateSyntheticSerializedFile(prefabAm, "CAB-inactive-prefab");
            var inactiveRootGo = CreateSyntheticGameObject(prefabSf, 100, "InactivePrefabRoot", isActive: false);
            var inactiveRootTr = CreateSyntheticTransform(prefabSf, 101, inactiveRootGo, null, Vector3.Zero);
            AttachComponentsOnly(inactiveRootGo, inactiveRootTr);

            var fallbackRoots = SceneGlbExportService.CollectRootGameObjectsWithInactiveFallback(
                new[] { prefabSf },
                out var usedInactiveFallback);
            Assert.True(usedInactiveFallback);
            Assert.Single(fallbackRoots);
            Assert.Same(inactiveRootGo, fallbackRoots[0]);
        }

        [Fact]
        public void ModelConverter_CreateForScene_StaticBatchedRenderers_CompactVerticesAndUnbakePerTargetFrame()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-static-batch");

            // Combined static-batch mesh with 6 vertices:
            // - Submesh 0 (verts 0..2) baked in world space at Unity X = 10 -> Imported X = -10
            // - Submesh 1 (verts 3..5) baked in world space at Unity X = -5 -> Imported X = +5
            var combinedMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            combinedMesh.assetsFile = sf;
            combinedMesh.m_PathID = 8001;
            combinedMesh.m_Name = "Combined Mesh (root: scene)";
            combinedMesh.m_VertexCount = 6;
            combinedMesh.m_Vertices = new[]
            {
                10f, 0f, 0f,  11f, 0f, 0f,  10f, 1f, 0f,
                -5f, 0f, 0f,  -4f, 0f, 0f,  -5f, 1f, 0f
            };
            combinedMesh.m_Normals = new[]
            {
                0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f,
                0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f
            };
            combinedMesh.m_UV0 = new[]
            {
                0f, 0f,  1f, 0f,  0f, 1f,
                0f, 0f,  1f, 0f,  0f, 1f
            };
            combinedMesh.m_Indices = new List<uint> { 0, 1, 2, 3, 4, 5 };

            var sub0 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub0.indexCount = 3;
            sub0.firstVertex = 0;
            sub0.vertexCount = 3;
            sub0.baseVertex = 0;

            var sub1 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub1.indexCount = 3;
            sub1.firstVertex = 3;
            sub1.vertexCount = 3;
            sub1.baseVertex = 0;

            combinedMesh.m_SubMeshes = new[] { sub0, sub1 };
            MarkMeshProcessed(combinedMesh);
            sf.AddObject(combinedMesh);

            var matA = CreateSyntheticMaterial(sf, 8101, "Mat_PropA", Array.Empty<KeyValuePair<string, UnityTexEnv>>());
            var matB = CreateSyntheticMaterial(sf, 8102, "Mat_PropB", Array.Empty<KeyValuePair<string, UnityTexEnv>>());

            var rootGo = CreateSyntheticGameObject(sf, 1, "BatchRoot", isActive: true);
            var rootTr = CreateSyntheticTransform(sf, 2, rootGo, null, Vector3.Zero);

            // Give BOTH sibling renderers the exact same GameObject name to verify TargetFrame-based unbaking!
            var propA = CreateSyntheticGameObject(sf, 10, "BatchedProp", isActive: true);
            var propATr = CreateSyntheticTransform(sf, 11, propA, rootTr, new Vector3(10f, 0f, 0f));
            var propAMf = CreateSyntheticMeshFilter(sf, 12, propA, 0, combinedMesh.m_PathID);
            var propAMr = CreateSyntheticMeshRenderer(sf, 13, propA, enabled: true, new[] { CreatePPtr<Material>(sf, 0, matA.m_PathID) });
            propAMr.m_StaticBatchInfo = CreateStaticBatchInfo(firstSubMesh: 0, subMeshCount: 1);
            AttachComponentsOnly(propA, propATr, propAMf, propAMr);

            var propB = CreateSyntheticGameObject(sf, 20, "BatchedProp", isActive: true);
            var propBTr = CreateSyntheticTransform(sf, 21, propB, rootTr, new Vector3(-5f, 0f, 0f));
            var propBMf = CreateSyntheticMeshFilter(sf, 22, propB, 0, combinedMesh.m_PathID);
            var propBMr = CreateSyntheticMeshRenderer(sf, 23, propB, enabled: true, new[] { CreatePPtr<Material>(sf, 0, matB.m_PathID) });
            propBMr.m_StaticBatchInfo = CreateStaticBatchInfo(firstSubMesh: 1, subMeshCount: 1);
            AttachComponentsOnly(propB, propBTr, propBMf, propBMr);

            rootTr.m_Children = new[]
            {
                CreatePPtr<Transform>(sf, 0, propATr.m_PathID),
                CreatePPtr<Transform>(sf, 0, propBTr.m_PathID)
            };
            AttachComponentsOnly(rootGo, rootTr);

            var convert = ModelConverter.CreateForScene(
                "StaticBatchScene",
                new[] { rootGo },
                includeInactive: false,
                skipDisabledRenderers: true,
                materialResolver: (r, _, slot) => slot == 0 ? (ReferenceEquals(r, propAMr) ? matA : matB) : null);

            Assert.Equal(2, convert.MeshList.Count);

            // Each static-batched renderer must have ONLY its own 3 compacted vertices (not all 6!)
            // and must be unbaked into local space (origin ~ (0,0,0)) using its own exact TargetFrame!
            foreach (var iMesh in convert.MeshList)
            {
                Assert.Equal(3, iMesh.VertexList.Count);
                Assert.Single(iMesh.SubmeshList);
                Assert.Equal(0, iMesh.SubmeshList[0].BaseVertex);
                Assert.All(iMesh.VertexList, v => Assert.InRange(v.Vertex.X, -1.01f, 0.01f));
            }

            Assert.Equal("Mat_PropA", convert.MeshList[0].SubmeshList[0].Material);
            Assert.Equal("Mat_PropB", convert.MeshList[1].SubmeshList[0].Material);
        }

        [Fact]
        public void ModelConverter_CreateForScene_MaterialVariantResolvesParentMaterialTexturesUsingParentOwner()
        {
            using var am = new AssetsManager();
            var parentFile = CreateSyntheticSerializedFile(am, "CAB-parent-mat");
            var childFile = CreateSyntheticSerializedFile(am, "CAB-child-mat");
            var sceneFile = CreateSyntheticSerializedFile(am, "CAB-variant-scene");

            var parentTex = CreateSyntheticRgbaTexture(parentFile, 9001, "T_ParentBase", 80, 180, 90, 255);
            var parentMat = CreateSyntheticMaterial(
                parentFile,
                9002,
                "Mat_Parent",
                new[]
                {
                    new KeyValuePair<string, UnityTexEnv>("_MainTex", CreateTexEnv(parentFile, fileId: 0, pathId: 9001))
                });

            var childVariantMat = CreateSyntheticMaterial(
                childFile,
                9003,
                "Mat_ChildVariant",
                Array.Empty<KeyValuePair<string, UnityTexEnv>>());
            childVariantMat.m_Parent = CreatePPtr<Material>(childFile, fileId: 0, pathId: parentMat.m_PathID);
            childVariantMat.m_Parent.Set(parentMat);

            var mesh = CreateSyntheticTwoSubmeshMesh(sceneFile, 9010, "SM_VariantBox");
            var go = CreateSyntheticGameObject(sceneFile, 9020, "VariantNode", isActive: true);
            var tr = CreateSyntheticTransform(sceneFile, 9021, go, null, Vector3.Zero);
            var mf = CreateSyntheticMeshFilter(sceneFile, 9022, go, 0, mesh.m_PathID);
            var mr = CreateSyntheticMeshRenderer(sceneFile, 9023, go, enabled: true, new[]
            {
                CreatePPtr<Material>(sceneFile, 0, childVariantMat.m_PathID),
                CreatePPtr<Material>(sceneFile, 0, childVariantMat.m_PathID)
            });
            AttachComponentsOnly(go, tr, mf, mr);

            Material? observedResolverOwner = null;
            var convert = ModelConverter.CreateForScene(
                "VariantScene",
                new[] { go },
                textureResolver: (ownerMat, prop, texRef) =>
                {
                    if (texRef != null && texRef.m_PathID == 9001)
                    {
                        observedResolverOwner = ownerMat;
                        return ReferenceEquals(ownerMat, parentMat) ? parentTex : null;
                    }
                    return null;
                },
                materialResolver: (_, _, _) => childVariantMat);

            Assert.Same(parentMat, observedResolverOwner);
            Assert.Single(convert.TextureList);
            Assert.Contains(convert.MaterialList, m => m.Name == "Mat_ChildVariant" && m.Textures.Count == 1);
        }

        [Fact]
        public void ModelConverter_CreateForScene_SkipsZeroVertexEmptyMeshes_AndSelectMaterialForSlotHandlesBounds()
        {
            using var am = new AssetsManager();
            var sf = CreateSyntheticSerializedFile(am, "CAB-empty-mesh");

            var emptyMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            emptyMesh.assetsFile = sf;
            emptyMesh.m_PathID = 9501;
            emptyMesh.m_Name = "SM_EmptyPlaceholder";
            emptyMesh.m_VertexCount = 0;
            emptyMesh.m_Vertices = Array.Empty<float>();
            emptyMesh.m_Indices = new List<uint>();
            emptyMesh.m_SubMeshes = Array.Empty<SubMesh>();
            MarkMeshProcessed(emptyMesh);
            sf.AddObject(emptyMesh);

            var go = CreateSyntheticGameObject(sf, 9510, "EmptyMeshNode", isActive: true);
            var tr = CreateSyntheticTransform(sf, 9511, go, null, Vector3.Zero);
            var mf = CreateSyntheticMeshFilter(sf, 9512, go, 0, emptyMesh.m_PathID);
            var mr = CreateSyntheticMeshRenderer(sf, 9513, go, enabled: true, Array.Empty<PPtr<Material>>());
            AttachComponentsOnly(go, tr, mf, mr);

            var convert = ModelConverter.CreateForScene("EmptyMeshScene", new[] { go });
            Assert.Empty(convert.MeshList);
            Assert.NotNull(convert.RootFrame.FindFrameByPath("EmptyMeshScene/EmptyMeshNode"));

            var mat = CreateSyntheticMaterial(sf, 9520, "Mat_Slot", Array.Empty<KeyValuePair<string, UnityTexEnv>>());
            Assert.Null(SceneGlbExportService.SelectMaterialForSlot(null, 0));
            Assert.Null(SceneGlbExportService.SelectMaterialForSlot(new Material?[] { mat }, -1));
            Assert.Null(SceneGlbExportService.SelectMaterialForSlot(new Material?[] { mat }, 1));
            Assert.Same(mat, SceneGlbExportService.SelectMaterialForSlot(new Material?[] { mat }, 0));
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager manager, string fileName)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = manager;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.reader = new FileReader(fileName, new MemoryStream(Array.Empty<byte>()));
            sf.version = new[] { 2022, 3, 56, 1 };
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.m_Objects = new List<ObjectInfo>();
            sf.m_Externals = new List<FileIdentifier>();
            typeof(SerializedFile)
                .GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(sf, new object());
            lock (manager.loadLock)
            {
                manager.assetsFileList.Add(sf);
            }
            return sf;
        }

        private static PPtr<T> CreatePPtr<T>(SerializedFile sf, int fileId, long pathId) where T : AssetStudio.Object
        {
            return new PPtr<T>(fileId, pathId, sf);
        }

        private static UnityTexEnv CreateTexEnv(SerializedFile sf, int fileId, long pathId)
        {
            var env = (UnityTexEnv)RuntimeHelpers.GetUninitializedObject(typeof(UnityTexEnv));
            env.m_Texture = CreatePPtr<Texture>(sf, fileId, pathId);
            env.m_Scale = new Vector2(1f, 1f);
            env.m_Offset = Vector2.Zero;
            return env;
        }

        private static StaticBatchInfo CreateStaticBatchInfo(ushort firstSubMesh, ushort subMeshCount)
        {
            var info = (StaticBatchInfo)RuntimeHelpers.GetUninitializedObject(typeof(StaticBatchInfo));
            info.firstSubMesh = firstSubMesh;
            info.subMeshCount = subMeshCount;
            return info;
        }

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive)
        {
            var go = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            go.assetsFile = sf;
            go.m_PathID = pathId;
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Components = Array.Empty<PPtr<Component>>();
            sf.AddObject(go);
            return go;
        }

        private static Transform CreateSyntheticTransform(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            Transform? father,
            Vector3 position)
        {
            var tr = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            tr.assetsFile = sf;
            tr.m_PathID = pathId;
            tr.m_GameObject = CreatePPtr<GameObject>(sf, 0, owner.m_PathID);
            tr.m_LocalPosition = position;
            tr.m_LocalRotation = new Quaternion(0f, 0f, 0f, 1f);
            tr.m_LocalScale = new Vector3(1f, 1f, 1f);
            tr.m_Children = Array.Empty<PPtr<Transform>>();
            tr.m_Father = father != null
                ? CreatePPtr<Transform>(sf, 0, father.m_PathID)
                : CreatePPtr<Transform>(sf, 0, 0);
            sf.AddObject(tr);
            return tr;
        }

        private static MeshFilter CreateSyntheticMeshFilter(
            SerializedFile sf,
            long pathId,
            GameObject owner,
            int meshFileId,
            long meshPathId)
        {
            var mf = (MeshFilter)RuntimeHelpers.GetUninitializedObject(typeof(MeshFilter));
            mf.assetsFile = sf;
            mf.m_PathID = pathId;
            mf.m_GameObject = CreatePPtr<GameObject>(sf, 0, owner.m_PathID);
            mf.m_Mesh = CreatePPtr<Mesh>(sf, meshFileId, meshPathId);
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
            mr.m_GameObject = CreatePPtr<GameObject>(sf, 0, owner.m_PathID);
            mr.m_Enabled = enabled;
            mr.m_Materials = materials;
            sf.AddObject(mr);
            return mr;
        }

        private static void AttachComponentsOnly(GameObject go, params Component[] components)
        {
            go.m_Components = components
                .Select(c => CreatePPtr<Component>(go.assetsFile, 0, c.m_PathID))
                .ToArray();
        }

        private static Mesh CreateSyntheticTwoSubmeshMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            mesh.assetsFile = sf;
            mesh.m_PathID = pathId;
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            mesh.m_Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_UV0 = new[] { 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_Indices = new List<uint> { 0, 1, 2, 0, 1, 2 };

            var sub0 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub0.indexCount = 3;
            sub0.firstVertex = 0;
            sub0.vertexCount = 3;
            sub0.baseVertex = 0;

            var sub1 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub1.indexCount = 3;
            sub1.firstVertex = 0;
            sub1.vertexCount = 3;
            sub1.baseVertex = 0;

            mesh.m_SubMeshes = new[] { sub0, sub1 };
            MarkMeshProcessed(mesh);
            sf.AddObject(mesh);
            return mesh;
        }

        private static void MarkMeshProcessed(Mesh mesh)
        {
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
        }

        private static Material CreateSyntheticMaterial(
            SerializedFile sf,
            long pathId,
            string name,
            KeyValuePair<string, UnityTexEnv>[] texEnvs,
            KeyValuePair<string, float>[]? floats = null)
        {
            var mat = (Material)RuntimeHelpers.GetUninitializedObject(typeof(Material));
            mat.assetsFile = sf;
            mat.m_PathID = pathId;
            mat.m_Name = name;
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs,
                m_Floats = floats ?? Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = new[] { new KeyValuePair<string, Color>("_BaseColor", new Color(1f, 1f, 1f, 1f)) }
            };
            sf.AddObject(mat);
            return mat;
        }

        private static Texture2D CreateSyntheticRgbaTexture(
            SerializedFile sf,
            long pathId,
            string name,
            byte r,
            byte g,
            byte b,
            byte a)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.assetsFile = sf;
            tex.m_PathID = pathId;
            tex.m_Name = name;
            tex.m_Width = 4;
            tex.m_Height = 4;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.version = sf.version;
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = a;
            }

            var rr = (ResourceReader)RuntimeHelpers.GetUninitializedObject(typeof(ResourceReader));
            typeof(ResourceReader).GetField("needSearch", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, false);
            typeof(ResourceReader).GetField("reader", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, new EndianBinaryReader(new MemoryStream(pixels)));
            typeof(ResourceReader).GetField("offset", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, 0L);
            typeof(ResourceReader).GetField("size", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, (long)pixels.Length);
            tex.image_data = rr;
            sf.AddObject(tex);
            return tex;
        }

        private static SyntheticSceneImported BuildSyntheticInstancedForestScene(
            int treeCount,
            int redCrateCount,
            int blueCrateCount,
            bool omitGeometryKeys = false)
        {
            var root = new ImportedFrame { Name = "SceneRoot", LocalScale = new Vector3(1, 1, 1) };
            var env = new ImportedFrame { Name = "Environment", LocalScale = new Vector3(1, 1, 1) };
            root.AddChild(env);

            var imported = new SyntheticSceneImported(root);
            imported.MaterialList.Add(CreateSimpleMaterial("Mat_Tree", 0.2f, 0.6f, 0.2f));
            imported.MaterialList.Add(CreateSimpleMaterial("Mat_CrateRed", 0.8f, 0.2f, 0.2f));
            imported.MaterialList.Add(CreateSimpleMaterial("Mat_CrateBlue", 0.2f, 0.3f, 0.9f));

            for (int i = 0; i < treeCount; i++)
            {
                // Intentionally give ALL 20 sibling trees the exact same name "Tree_Pine"
                var treeFrame = new ImportedFrame
                {
                    Name = "Tree_Pine",
                    LocalPosition = new Vector3(i * 4.0f, 0f, (i % 5) * 3.0f),
                    LocalRotation = new Vector3(0f, i * 15f, 0f),
                    LocalScale = new Vector3(1f, 1f + (i % 3) * 0.1f, 1f)
                };
                env.AddChild(treeFrame);

                imported.MeshList.Add(CreateUnitBoxMesh(
                    path: treeFrame.Path,
                    targetFrame: treeFrame,
                    sourceMeshName: "SM_Tree_Pine",
                    geometryKey: omitGeometryKeys ? null : "level0#1001",
                    materialName: "Mat_Tree",
                    subdivisions: 6));
            }

            for (int i = 0; i < redCrateCount; i++)
            {
                var crateFrame = new ImportedFrame
                {
                    Name = "Crate",
                    LocalPosition = new Vector3(i * 2.5f, 0f, -5f),
                    LocalScale = new Vector3(1, 1, 1)
                };
                env.AddChild(crateFrame);

                imported.MeshList.Add(CreateUnitBoxMesh(
                    path: crateFrame.Path,
                    targetFrame: crateFrame,
                    sourceMeshName: "SM_Crate",
                    geometryKey: omitGeometryKeys ? null : "level0#2001",
                    materialName: "Mat_CrateRed",
                    subdivisions: 4));
            }

            for (int i = 0; i < blueCrateCount; i++)
            {
                var crateFrame = new ImportedFrame
                {
                    Name = "Crate",
                    LocalPosition = new Vector3(i * 2.5f, 0f, -10f),
                    LocalScale = new Vector3(1, 1, 1)
                };
                env.AddChild(crateFrame);

                imported.MeshList.Add(CreateUnitBoxMesh(
                    path: crateFrame.Path,
                    targetFrame: crateFrame,
                    sourceMeshName: "SM_Crate",
                    geometryKey: omitGeometryKeys ? null : "level0#2001",
                    materialName: "Mat_CrateBlue",
                    subdivisions: 4));
            }

            return imported;
        }

        private static ImportedMaterial CreateSimpleMaterial(string name, float r, float g, float b)
        {
            return new ImportedMaterial
            {
                Name = name,
                Diffuse = new Color(r, g, b, 1f),
                Metallic = 0.1f,
                Roughness = 0.7f
            };
        }

        private static ImportedMesh CreateUnitBoxMesh(
            string path,
            ImportedFrame targetFrame,
            string sourceMeshName,
            string? geometryKey,
            string materialName,
            int subdivisions = 2)
        {
            var mesh = new ImportedMesh
            {
                Path = path,
                SourceMeshName = sourceMeshName,
                GeometryKey = geometryKey,
                TargetFrame = targetFrame,
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false }
            };

            // Build a deterministic grid-based quad mesh with (subdivisions+1)^2 vertices so buffer savings are measurable
            int stride = subdivisions + 1;
            for (int z = 0; z < stride; z++)
            {
                for (int x = 0; x < stride; x++)
                {
                    float u = (float)x / subdivisions;
                    float v = (float)z / subdivisions;
                    mesh.VertexList.Add(new ImportedVertex
                    {
                        Vertex = new Vector3(u - 0.5f, 0f, v - 0.5f),
                        Normal = new Vector3(0f, 1f, 0f),
                        UV = new[] { new[] { u, v } }
                    });
                }
            }

            var sub = new ImportedSubmesh { Material = materialName };
            for (int z = 0; z < subdivisions; z++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int i0 = z * stride + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + stride;
                    int i3 = i2 + 1;
                    sub.FaceList.Add(new ImportedFace { VertexIndices = new[] { i0, i2, i1 } });
                    sub.FaceList.Add(new ImportedFace { VertexIndices = new[] { i1, i2, i3 } });
                }
            }

            mesh.SubmeshList.Add(sub);
            return mesh;
        }

        private static List<int> GetChildren(JsonElement node)
        {
            if (!node.TryGetProperty("children", out var childrenProp))
            {
                return new List<int>();
            }
            return childrenProp.EnumerateArray().Select(c => c.GetInt32()).ToList();
        }

        private static JsonDocument ParseGlbJson(string glbPath)
        {
            var bytes = File.ReadAllBytes(glbPath);
            Assert.True(bytes.Length >= 20, "GLB file is too small.");
            Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            Assert.Equal((uint)bytes.Length, totalLength);

            uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            Assert.Equal(0x4E4F534Au, jsonType);

            var jsonText = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen);
            return JsonDocument.Parse(jsonText);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "AssetStudio_Phase3Tests_" + Guid.NewGuid().ToString("N"));
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

        private sealed class SyntheticSceneImported : IImported
        {
            public ImportedFrame RootFrame { get; }
            public List<ImportedMesh> MeshList { get; } = new();
            public List<ImportedMaterial> MaterialList { get; } = new();
            public List<ImportedTexture> TextureList { get; } = new();
            public List<ImportedKeyframedAnimation> AnimationList { get; } = new();
            public List<ImportedMorph> MorphList { get; } = new();

            public SyntheticSceneImported(ImportedFrame rootFrame)
            {
                RootFrame = rootFrame;
            }
        }
    }
}
