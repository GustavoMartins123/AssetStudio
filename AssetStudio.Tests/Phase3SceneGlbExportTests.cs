using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
