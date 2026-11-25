using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class LazySceneHierarchyTests
    {
        private readonly ITestOutputHelper _output;

        public LazySceneHierarchyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void LazyScenes_RootPopulationAndOrdering_PutsReal3DScenesAboveZeroRendererShellsAndDeduplicates()
        {
            var discovered = new List<IndexedSceneRecord>
            {
                new()
                {
                    Name = "Bootstrap",
                    ScenePath = "Assets/Scenes/Bootstrap.unity",
                    BuildIndex = 0,
                    EntrySerializedFileName = "level0",
                    EntryOriginalPath = @"C:\Project\level0",
                    SourceKind = "BuildSettings"
                },
                new()
                {
                    Name = "Debug_Sandbox",
                    ScenePath = "Assets/Scenes/Debug/Debug_Sandbox.unity",
                    BuildIndex = 3,
                    EntrySerializedFileName = "level3",
                    EntryOriginalPath = @"C:\Project\level3",
                    SourceKind = "BuildSettings"
                },
                new()
                {
                    Name = "S02_Harbor",
                    ScenePath = "Assets/Scenes/S02_Harbor.unity",
                    BuildIndex = 2,
                    EntrySerializedFileName = "CAB-harbor",
                    EntryOriginalPath = @"C:\Project\harbor.bundle",
                    SourceKind = "AssetBundle"
                },
                new()
                {
                    Name = "S01_Courtyard",
                    ScenePath = "Assets/Scenes/S01_Courtyard.unity",
                    BuildIndex = 1,
                    EntrySerializedFileName = "CAB-courtyard",
                    EntryOriginalPath = @"C:\Project\courtyard.bundle",
                    SourceKind = "AssetBundle"
                },
                new()
                {
                    Name = "Unknown_RemoteScene",
                    ScenePath = "Assets/Scenes/Unknown_RemoteScene.unity",
                    BuildIndex = -1,
                    EntrySerializedFileName = "CAB-unknown",
                    EntryOriginalPath = @"C:\Project\unknown.bundle",
                    SourceKind = "Addressables"
                }
            };

            // SQLite contains a duplicate of S01_Courtyard plus one additional 0-renderer shell.
            var sqlite = new List<IndexedSceneRecord>
            {
                new()
                {
                    Name = "S01_Courtyard_Duplicate",
                    ScenePath = "Assets/Scenes/S01_Courtyard.unity",
                    BuildIndex = 1,
                    EntrySerializedFileName = "CAB-courtyard",
                    EntryOriginalPath = @"C:\Project\courtyard.bundle",
                    SourceKind = "AssetBundle"
                },
                new()
                {
                    Name = "PersistentManagers",
                    ScenePath = "Assets/Scenes/PersistentManagers.unity",
                    BuildIndex = 4,
                    EntrySerializedFileName = "level4",
                    EntryOriginalPath = @"C:\Project\level4",
                    SourceKind = "BuildSettings"
                }
            };

            var stats = new Dictionary<string, (int GameObjectCount, int RendererCount)>(StringComparer.OrdinalIgnoreCase)
            {
                ["level0"] = (12, 0),
                ["level3"] = (90, 22),
                ["CAB-harbor"] = (320, 88),
                ["CAB-courtyard"] = (450, 120),
                ["level4"] = (8, 0)
            };

            var companions = new Dictionary<string, List<(string SerializedFileName, string OriginalPath)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["CAB-courtyard"] = new() { ("CAB-courtyard-preset", @"C:\Project\courtyard_preset.bundle") }
            };

            var descriptors = SceneHierarchyBuilderService.BuildLazySceneDescriptors(
                discovered,
                sqlite,
                stats,
                companions);

            // Deduplicated: 5 discovered + 1 unique sqlite = 6 scenes total.
            Assert.Equal(6, descriptors.Count);

            // Ordering: real 3D non-Debug (BuildIndex 1 then 2) -> real 3D Debug -> unknown (-1) -> 0-renderer shells (BuildIndex 0 then 4).
            Assert.Equal(
                new[]
                {
                    "S01_Courtyard",
                    "S02_Harbor",
                    "Debug_Sandbox",
                    "Unknown_RemoteScene",
                    "Bootstrap",
                    "PersistentManagers"
                },
                descriptors.Select(static d => d.Name).ToArray());

            Assert.Single(descriptors[0].CompanionStageFiles);

            var roots = SceneHierarchyBuilderService.BuildLazySceneRootNodes(descriptors);
            Assert.Equal(6, roots.Count);

            var courtyard = roots[0];
            Assert.True(courtyard.IsLazySceneRoot);
            Assert.False(courtyard.IsSceneLoaded);
            Assert.Contains("450 objects", courtyard.Subtitle);
            Assert.Contains("120 renderers", courtyard.Subtitle);
            Assert.Single(courtyard.Children);
            Assert.True(courtyard.Children[0].IsPlaceholder);
            Assert.False(courtyard.Children[0].ShowCheckBox);

            var bootstrap = roots[4];
            Assert.Contains("12 objects", bootstrap.Subtitle);
            Assert.Contains("0 renderers", bootstrap.Subtitle);
            Assert.Contains("shell", bootstrap.Subtitle);

            // Checking a lazy root must NOT check its placeholder child.
            courtyard.IsChecked = true;
            Assert.False(courtyard.Children[0].IsChecked);

            // Scene search matches lazy scene names but skips placeholder text ("Expand to load...").
            Assert.Empty(SceneTreeSearch.FindMatches(roots, "Expand to load", CancellationToken.None));
            var matches = SceneTreeSearch.FindMatches(roots, "Courtyard", CancellationToken.None);
            Assert.Single(matches);
            Assert.Same(courtyard, matches[0]);
        }

        [Fact]
        public void LazyScenes_OnDemandExpansion_MaterializesGameObjectTransformHierarchyAndCompanionStagePresets()
        {
            using var am = new AssetsManager { LazyLoading = true };
            var sceneFile = CreateSyntheticSerializedFile(am, "CAB-stage-main");
            var companionFile = CreateSyntheticSerializedFile(am, "CAB-stage-preset");

            // Base scene objects (unlinked m_Transform/m_MeshRenderer to mirror Lazy Mode before eager linking).
            var sceneRootGo = CreateSyntheticGameObject(sceneFile, 100, "SceneRoot", isActive: true);
            var sceneRootTr = CreateSyntheticTransform(sceneFile, 101, sceneRootGo, father: null);

            var stageModelGo = CreateSyntheticGameObject(sceneFile, 200, "StageModel", isActive: true);
            var stageModelTr = CreateSyntheticTransform(sceneFile, 201, stageModelGo, father: sceneRootTr);

            var propGo = CreateSyntheticGameObject(sceneFile, 300, "StoneArch", isActive: true);
            var propTr = CreateSyntheticTransform(sceneFile, 301, propGo, father: stageModelTr);
            var propMr = CreateSyntheticMeshRenderer(sceneFile, 302, propGo, enabled: true);
            AttachComponentsOnly(propGo, propTr, propMr);

            var hiddenTriggerGo = CreateSyntheticGameObject(sceneFile, 400, "TriggerZone", isActive: false);
            var hiddenTriggerTr = CreateSyntheticTransform(sceneFile, 401, hiddenTriggerGo, father: sceneRootTr);
            AttachComponentsOnly(hiddenTriggerGo, hiddenTriggerTr);

            stageModelTr.m_Children = new[] { new PPtr<Transform>(0, propTr.m_PathID, sceneFile) };
            AttachComponentsOnly(stageModelGo, stageModelTr);

            sceneRootTr.m_Children = new[]
            {
                new PPtr<Transform>(0, stageModelTr.m_PathID, sceneFile),
                new PPtr<Transform>(0, hiddenTriggerTr.m_PathID, sceneFile)
            };
            AttachComponentsOnly(sceneRootGo, sceneRootTr);

            // Companion stage preset bundle with ScriptableObject preset referencing a prefab hierarchy.
            var presetPrefabRoot = CreateSyntheticGameObject(companionFile, 500, "S10_FLD_COURTYARD_Model", isActive: true);
            var presetPrefabTr = CreateSyntheticTransform(companionFile, 501, presetPrefabRoot, father: null);

            var presetPillar = CreateSyntheticGameObject(companionFile, 510, "PresetPillar", isActive: true);
            var presetPillarTr = CreateSyntheticTransform(companionFile, 511, presetPillar, father: presetPrefabTr);
            var presetPillarMr = CreateSyntheticMeshRenderer(companionFile, 512, presetPillar, enabled: true);
            AttachComponentsOnly(presetPillar, presetPillarTr, presetPillarMr);

            presetPrefabTr.m_Children = new[] { new PPtr<Transform>(0, presetPillarTr.m_PathID, companionFile) };
            AttachComponentsOnly(presetPrefabRoot, presetPrefabTr);

            var presetSo = (MonoBehaviour)RuntimeHelpers.GetUninitializedObject(typeof(MonoBehaviour));
            presetSo.assetsFile = companionFile;
            presetSo.m_PathID = 600;
            presetSo.type = ClassIDType.MonoBehaviour;
            presetSo.m_Name = "S10_FLD_COURTYARD_NOON_SUNNY_DEFAULT";
            presetSo.m_GameObject = new PPtr<GameObject>(0, 0, companionFile);
            companionFile.AddObject(presetSo);

            var descriptor = new LazySceneDescriptor
            {
                Id = "test:courtyard",
                Name = "S10_FLD_COURTYARD",
                ScenePath = "Assets/Scenes/S10_FLD_COURTYARD.unity",
                SerializedFileName = "CAB-stage-main",
                GameObjectCount = 4,
                RendererCount = 1,
                CompanionStageFiles = new[] { ("CAB-stage-preset", string.Empty) }
            };

            LazySceneExpansionResult? expansionResult = null;
            var rootNode = SceneHierarchyBuilderService.BuildLazySceneRootNodes(
                new[] { descriptor },
                onExpanded: node =>
                {
                    expansionResult = SceneHierarchyBuilderService.LoadLazySceneHierarchy(
                        am,
                        node.LazyScene!,
                        cancellationToken: CancellationToken.None,
                        customCompanionFieldProvider: mb =>
                        {
                            if (!ReferenceEquals(mb, presetSo))
                            {
                                return null;
                            }

                            var fields = new OrderedDictionary
                            {
                                ["StagePrefab"] = new OrderedDictionary
                                {
                                    ["m_FileID"] = 0,
                                    ["m_PathID"] = presetPrefabRoot.m_PathID
                                }
                            };
                            return fields;
                        });

                    if (expansionResult.Success && expansionResult.RootChildren.Count > 0)
                    {
                        node.ReplaceChildren(expansionResult.RootChildren);
                        node.IsSceneLoaded = true;
                    }
                }).Single();

            var collectionResetCount = 0;
            ((INotifyCollectionChanged)rootNode.Children).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    collectionResetCount++;
                }
            };

            Assert.Single(rootNode.Children);
            Assert.True(rootNode.Children[0].IsPlaceholder);

            // Trigger on-demand expansion via IsExpanded = true (exact path used by Avalonia TreeViewItem binding).
            rootNode.IsExpanded = true;

            Assert.NotNull(expansionResult);
            Assert.True(expansionResult!.Success, expansionResult.ErrorMessage);
            Assert.True(rootNode.IsSceneLoaded);
            Assert.True(collectionResetCount >= 1);
            Assert.Equal(1, expansionResult.CompanionAttachmentCount);
            Assert.Equal(6, expansionResult.TotalGameObjectCount); // 4 base + 2 companion preset objects
            Assert.Equal(2, expansionResult.RendererCount);        // StoneArch + PresetPillar

            // Verify materialized tree structure: SceneRoot -> [StageModel -> [StoneArch, S10_FLD_COURTYARD_Model -> PresetPillar], TriggerZone].
            var sceneRootNode = Assert.Single(rootNode.Children);
            Assert.Equal("SceneRoot", sceneRootNode.Name);
            Assert.Same(sceneRootGo, sceneRootNode.GameObject);
            Assert.Equal(2, sceneRootNode.ChildCount);

            var stageModelNode = sceneRootNode.Children[0];
            var triggerNode = sceneRootNode.Children[1];
            Assert.Equal("StageModel", stageModelNode.Name);
            Assert.Equal("TriggerZone", triggerNode.Name);
            Assert.True(triggerNode.IsInactiveGameObject);
            Assert.Equal("[Inactive]", triggerNode.Subtitle);

            Assert.Equal(2, stageModelNode.ChildCount);
            Assert.Equal("StoneArch", stageModelNode.Children[0].Name);
            Assert.Equal("S10_FLD_COURTYARD_Model", stageModelNode.Children[1].Name);
            Assert.Single(stageModelNode.Children[1].Children);
            Assert.Equal("PresetPillar", stageModelNode.Children[1].Children[0].Name);

            // Verify GameObjectInspectorService works on materialized lazy node.
            var inspected = GameObjectInspectorService.BuildFromNode(stageModelNode.Children[0]);
            Assert.True(inspected.HasGameObjectData);
            Assert.Equal("StoneArch", inspected.Name);
            Assert.True(inspected.Transform.HasTransform);
            Assert.Contains(inspected.Components, c => c.ComponentType == ClassIDType.MeshRenderer);

            // Verify checkbox propagation works across newly materialized real nodes.
            sceneRootNode.IsChecked = true;
            Assert.True(stageModelNode.IsChecked);
            Assert.True(stageModelNode.Children[0].IsChecked);
            Assert.True(stageModelNode.Children[1].Children[0].IsChecked);
        }

        [Fact]
        public void LazyScenes_OnDemandExpansion_HandlesMissingCorruptEmptyAndCyclicFilesGracefully()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AssetStudio_LazySceneTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var am = new AssetsManager { LazyLoading = true };

                // 1. Missing disk file
                var missingResult = SceneHierarchyBuilderService.LoadLazySceneHierarchy(
                    am,
                    new LazySceneDescriptor
                    {
                        Name = "MissingScene",
                        SerializedFileName = "CAB-does-not-exist",
                        OriginalPath = Path.Combine(tempDir, "missing.bundle")
                    });
                Assert.False(missingResult.Success);
                Assert.Empty(missingResult.RootChildren);
                Assert.False(string.IsNullOrWhiteSpace(missingResult.ErrorMessage));

                // 2. Corrupt/garbage file on disk
                var corruptPath = Path.Combine(tempDir, "corrupt.bundle");
                File.WriteAllBytes(corruptPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
                var corruptResult = SceneHierarchyBuilderService.LoadLazySceneHierarchy(
                    am,
                    new LazySceneDescriptor
                    {
                        Name = "CorruptScene",
                        SerializedFileName = "CAB-corrupt",
                        OriginalPath = corruptPath
                    });
                Assert.False(corruptResult.Success);
                Assert.Empty(corruptResult.RootChildren);
                Assert.False(string.IsNullOrWhiteSpace(corruptResult.ErrorMessage));

                // 3. Valid SerializedFile with zero GameObjects
                _ = CreateSyntheticSerializedFile(am, "CAB-empty-scene");
                var emptyResult = SceneHierarchyBuilderService.LoadLazySceneHierarchy(
                    am,
                    new LazySceneDescriptor
                    {
                        Name = "EmptyScene",
                        SerializedFileName = "CAB-empty-scene"
                    });
                Assert.True(emptyResult.Success);
                Assert.Empty(emptyResult.RootChildren);
                Assert.Contains("No GameObjects", emptyResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

                // 4. Cyclic Transform graph does not infinite-loop or stack-overflow
                var cyclicFile = CreateSyntheticSerializedFile(am, "CAB-cyclic");
                var goA = CreateSyntheticGameObject(cyclicFile, 1, "NodeA", isActive: true);
                var trA = CreateSyntheticTransform(cyclicFile, 2, goA, father: null);
                var goB = CreateSyntheticGameObject(cyclicFile, 3, "NodeB", isActive: true);
                var trB = CreateSyntheticTransform(cyclicFile, 4, goB, father: trA);
                trA.m_Children = new[] { new PPtr<Transform>(0, trB.m_PathID, cyclicFile) };
                trB.m_Children = new[] { new PPtr<Transform>(0, trA.m_PathID, cyclicFile) }; // cycle!
                AttachComponentsOnly(goA, trA);
                AttachComponentsOnly(goB, trB);

                var tree = SceneHierarchyBuilderService.BuildGameObjectTreeNodes(
                    new[] { goA },
                    companionAttachments: null,
                    out var goCount,
                    out _);
                Assert.Single(tree);
                Assert.Equal(2, goCount);
                Assert.Single(tree[0].Children);
                Assert.Empty(tree[0].Children[0].Children);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task VirtualContainers_FromAssetHandles_BuildsSlashPathsHashedFallbackAndSupportsSearchFiltering()
        {
            var handles = new List<AssetHandle>
            {
                new()
                {
                    Name = "hero_icon",
                    Container = "assets/textures/ui/hero_icon.png",
                    Type = ClassIDType.Texture2D,
                    PathID = 101,
                    SerializedFileName = "CAB-ui",
                    OriginalPath = @"C:\Game\ui.bundle",
                    UniqueID = "CAB-ui#101"
                },
                new()
                {
                    Name = "HeroBodyMesh",
                    Container = "assets/characters/hero.prefab",
                    Type = ClassIDType.Mesh,
                    PathID = 201,
                    SerializedFileName = "CAB-hero",
                    OriginalPath = @"C:\Game\hero.bundle",
                    UniqueID = "CAB-hero#201"
                },
                new()
                {
                    Name = "HeroArmorMat",
                    Container = "assets/characters/hero.prefab",
                    Type = ClassIDType.Material,
                    PathID = 202,
                    SerializedFileName = "CAB-hero",
                    OriginalPath = @"C:\Game\hero.bundle",
                    UniqueID = "CAB-hero#202"
                },
                new()
                {
                    Name = "TownBgm",
                    Container = @"Assets\Audio\BGM\town_theme.wav",
                    Type = ClassIDType.AudioClip,
                    PathID = 301,
                    SerializedFileName = "CAB-audio",
                    OriginalPath = @"C:\Game\audio.bundle",
                    UniqueID = "CAB-audio#301"
                },
                new()
                {
                    Name = "CliffRock",
                    Container = string.Empty, // empty -> fallback to bundle filename
                    Type = ClassIDType.Mesh,
                    PathID = 401,
                    SerializedFileName = "CAB-env",
                    OriginalPath = @"C:\Game\Bundles\env_props.bundle",
                    UniqueID = "CAB-env#401"
                },
                new()
                {
                    Name = "FloorDiffuse",
                    Container = "9facb8b5e47a5d71f608f1d9d1a53bd9", // 32-char MD5 hash -> fallback to bundle filename
                    Type = ClassIDType.Texture2D,
                    PathID = 501,
                    SerializedFileName = "CAB-stage",
                    OriginalPath = "/data/bundles/hashed_stage.bundle",
                    UniqueID = "CAB-stage#501"
                },
                new()
                {
                    Name = "GuidKeyedShader",
                    Container = "0a1b2c3d-4e5f-6789-abcd-ef0123456789", // GUID -> fallback to SerializedFileName when OriginalPath empty
                    Type = ClassIDType.Shader,
                    PathID = 601,
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = string.Empty,
                    UniqueID = "sharedassets0.assets#601"
                }
            };

            Assert.False(SceneHierarchyBuilderService.IsEmptyOrHashedContainer("assets/textures/ui/hero_icon.png"));
            Assert.True(SceneHierarchyBuilderService.IsEmptyOrHashedContainer(string.Empty));
            Assert.True(SceneHierarchyBuilderService.IsEmptyOrHashedContainer("9facb8b5e47a5d71f608f1d9d1a53bd9"));
            Assert.True(SceneHierarchyBuilderService.IsEmptyOrHashedContainer("CAB-9facb8b5e47a5d71f608f1d9d1a53bd9"));
            Assert.True(SceneHierarchyBuilderService.IsEmptyOrHashedContainer("0a1b2c3d-4e5f-6789-abcd-ef0123456789"));

            var index = VirtualContainerTreeIndex.BuildFromHandles(handles);
            var vm = new SceneHierarchyViewModel();
            vm.SetContainerIndex(index);

            // Structured "assets" root first, then 3 fallback bundle roots alphabetically.
            Assert.Equal(4, vm.ContainerNodes.Count);
            Assert.Equal(
                new[] { "assets", "env_props.bundle", "hashed_stage.bundle", "sharedassets0.assets" },
                vm.ContainerNodes.Select(static n => n.Name).ToArray());

            // Lazy child materialization: before expansion, "assets" only has 1 placeholder child.
            var assetsRoot = vm.ContainerNodes[0];
            Assert.True(assetsRoot.IsFolder);
            Assert.Single(assetsRoot.Children);
            Assert.True(assetsRoot.Children[0].IsPlaceholder);

            // Expanding "assets" materializes audio, characters, textures lazily.
            assetsRoot.IsExpanded = true;
            Assert.Equal(
                new[] { "Audio", "characters", "textures" },
                assetsRoot.Children.Select(static c => c.Name).ToArray());

            // Single-asset file container "hero_icon.png" renders directly as a leaf under assets/textures/ui.
            var texturesFolder = assetsRoot.Children[2];
            texturesFolder.IsExpanded = true;
            var uiFolder = Assert.Single(texturesFolder.Children);
            uiFolder.IsExpanded = true;
            var heroIconLeaf = Assert.Single(uiFolder.Children);
            Assert.True(heroIconLeaf.IsAssetLeaf);
            Assert.Equal("hero_icon.png", heroIconLeaf.Name);
            Assert.Equal(ClassIDType.Texture2D, heroIconLeaf.AssetType);
            Assert.Equal("CAB-ui#101", heroIconLeaf.UniqueId);

            // Multi-asset container "hero.prefab" stays a folder containing both Mesh & Material leaves.
            var charactersFolder = assetsRoot.Children[1];
            charactersFolder.IsExpanded = true;
            var prefabFolder = Assert.Single(charactersFolder.Children);
            Assert.True(prefabFolder.IsFolder);
            Assert.Equal("hero.prefab", prefabFolder.Name);
            prefabFolder.IsExpanded = true;
            Assert.Equal(2, prefabFolder.Children.Count);
            Assert.Contains(prefabFolder.Children, c => c.Name == "HeroBodyMesh" && c.AssetType == ClassIDType.Mesh);
            Assert.Contains(prefabFolder.Children, c => c.Name == "HeroArmorMat" && c.AssetType == ClassIDType.Material);

            // Fallback bundle folder expands directly into its leaf assets.
            var hashedBundleRoot = vm.ContainerNodes[2];
            hashedBundleRoot.IsExpanded = true;
            var floorLeaf = Assert.Single(hashedBundleRoot.Children);
            Assert.True(floorLeaf.IsAssetLeaf);
            Assert.Equal("FloorDiffuse", floorLeaf.Name);
            Assert.Equal(ClassIDType.Texture2D, floorLeaf.AssetType);

            // Search by container path segment
            var pathSearch = await vm.FilterContainersAsync("characters");
            Assert.Equal(2, pathSearch.TotalMatchedAssets);
            Assert.Single(vm.ContainerNodes);
            Assert.Equal("assets", vm.ContainerNodes[0].Name);
            Assert.True(vm.ContainerNodes[0].IsExpanded);

            // Search by fallback bundle filename
            var bundleSearch = await vm.FilterContainersAsync("hashed_stage");
            Assert.Equal(1, bundleSearch.TotalMatchedAssets);
            Assert.Single(vm.ContainerNodes);
            Assert.Equal("hashed_stage.bundle", vm.ContainerNodes[0].Name);

            // Search by asset name
            var nameSearch = await vm.FilterContainersAsync("TownBgm");
            Assert.Equal(1, nameSearch.TotalMatchedAssets);
            Assert.Single(vm.ContainerNodes);

            // Clearing filter restores all 4 roots
            vm.ClearContainerFilter();
            Assert.Equal(4, vm.ContainerNodes.Count);
        }

        [Fact]
        public async Task VirtualContainers_40kBundlesFromProjectIndex_OpensFastWithBoundedPaginationAndZeroDiskIo()
        {
            const int bundleCount = 40_000;
            var projectIndex = new ProjectIndex();

            // Non-existent disk paths prove 0 disk I/O occurs.
            for (var i = 0; i < bundleCount; i++)
            {
                var fileName = $"bundle_{i:D5}.bundle";
                var container = (i % 10 == 0)
                    ? $"assets/zone_{i % 25}/prop_{i}.prefab"
                    : (i % 3 == 0 ? $"{i:x32}" : string.Empty);

                projectIndex.AddRecord(
                    serializedFileName: $"CAB-{i:x16}",
                    originalPath: $@"Z:\NonExistentDrive\StreamingAssets\{fileName}",
                    pathId: i + 1,
                    name: $"Asset_{i:D5}",
                    type: i % 2 == 0 ? ClassIDType.Texture2D : ClassIDType.Mesh,
                    container: container,
                    byteSize: 1024);
            }

            // Warm up JIT with a tiny index first.
            var warmupIndex = new ProjectIndex();
            warmupIndex.AddRecord("CAB-w", @"Z:\w.bundle", 1, "Warm", ClassIDType.Texture2D, "assets/warm.png");
            _ = VirtualContainerTreeIndex.BuildFromProjectIndex(warmupIndex, includeAllTypes: true).CreateRootNodes();

            var sw = Stopwatch.StartNew();
            var containerIndex = VirtualContainerTreeIndex.BuildFromProjectIndex(
                projectIndex,
                includeAllTypes: true,
                pageSize: SceneHierarchyBuilderService.DefaultContainerPageSize);
            var initialRoots = containerIndex.CreateRootNodes();
            sw.Stop();

            _output.WriteLine(
                $"Built virtual Containers tree for {bundleCount:N0} bundles ({containerIndex.RootFolderCount:N0} roots) in {sw.Elapsed.TotalMilliseconds:F2} ms.");

            // 1 "assets" root + 36,000 unique bundle fallback roots = 36,001 roots.
            Assert.Equal(36_001, containerIndex.RootFolderCount);
            Assert.Equal(bundleCount, containerIndex.TotalAssetCount);

            // Bounded root materialization: DefaultContainerPageSize (500) + 1 "Load more" node.
            Assert.Equal(SceneHierarchyBuilderService.DefaultContainerPageSize + 1, initialRoots.Count);
            Assert.True(initialRoots[^1].IsLoadMoreNode);

            // Fast open budget (generous upper guard for busy CI while logging exact ms).
            Assert.True(
                sw.Elapsed.TotalMilliseconds < 1500,
                $"Building virtual container index for 40k bundles took {sw.Elapsed.TotalMilliseconds:F1} ms.");

            // Test ViewModel pagination + full-index search reaching bundle #39,999 beyond page 1.
            var vm = new SceneHierarchyViewModel();
            vm.SetContainerIndex(containerIndex);
            Assert.Equal(SceneHierarchyBuilderService.DefaultContainerPageSize + 1, vm.ContainerNodes.Count);

            var loadMore = vm.ContainerNodes[^1];
            Assert.True(vm.TryLoadMoreContainerNodes(loadMore));
            Assert.Equal(SceneHierarchyBuilderService.DefaultContainerPageSize * 2 + 1, vm.ContainerNodes.Count);

            var searchResult = await vm.FilterContainersAsync("bundle_39999");
            Assert.Equal(1, searchResult.TotalMatchedAssets);
            Assert.Single(vm.ContainerNodes);
            Assert.Equal("bundle_39999.bundle", vm.ContainerNodes[0].Name);
        }

        [Fact]
        public void SceneHierarchyViewModel_ModeToggleCommandsAndResetIdempotency_WorkCleanly()
        {
            var vm = new SceneHierarchyViewModel();
            var activatedModes = new List<SceneHierarchyViewMode>();
            vm.ModeActivatedAsync = mode =>
            {
                activatedModes.Add(mode);
                return Task.CompletedTask;
            };

            Assert.True(vm.IsScenesMode);
            Assert.False(vm.IsContainersMode);
            Assert.Contains("scene", vm.SearchPlaceholderText, StringComparison.OrdinalIgnoreCase);

            vm.ShowContainersModeCommand.Execute();
            Assert.True(vm.IsContainersMode);
            Assert.False(vm.IsScenesMode);
            Assert.Contains("containers", vm.SearchPlaceholderText, StringComparison.OrdinalIgnoreCase);
            Assert.Single(activatedModes);
            Assert.Equal(SceneHierarchyViewMode.Containers, activatedModes[0]);

            // Clicking active toggle again keeps it checked and does not flip off.
            vm.IsContainersMode = false;
            Assert.True(vm.IsContainersMode);

            vm.ShowScenesModeCommand.Execute();
            Assert.True(vm.IsScenesMode);
            Assert.False(vm.IsContainersMode);
            Assert.Equal(2, activatedModes.Count);

            // Reset returns to default state; second Reset emits zero PropertyChanged events.
            vm.Reset();
            var changedAfterReset = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    changedAfterReset.Add(e.PropertyName);
                }
            };
            vm.Reset();
            Assert.Empty(changedAfterReset);
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

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive)
        {
            var go = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            go.assetsFile = sf;
            go.m_PathID = pathId;
            go.type = ClassIDType.GameObject;
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Components = Array.Empty<PPtr<Component>>();
            sf.AddObject(go);
            return go;
        }

        private static Transform CreateSyntheticTransform(SerializedFile sf, long pathId, GameObject owner, Transform? father)
        {
            var tr = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            tr.assetsFile = sf;
            tr.m_PathID = pathId;
            tr.type = ClassIDType.Transform;
            tr.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            tr.m_LocalPosition = Vector3.Zero;
            tr.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            tr.m_LocalScale = Vector3.One;
            tr.m_Children = Array.Empty<PPtr<Transform>>();
            tr.m_Father = father != null ? new PPtr<Transform>(0, father.m_PathID, sf) : new PPtr<Transform>(0, 0, sf);
            sf.AddObject(tr);
            return tr;
        }

        private static MeshRenderer CreateSyntheticMeshRenderer(SerializedFile sf, long pathId, GameObject owner, bool enabled)
        {
            var mr = (MeshRenderer)RuntimeHelpers.GetUninitializedObject(typeof(MeshRenderer));
            mr.assetsFile = sf;
            mr.m_PathID = pathId;
            mr.type = ClassIDType.MeshRenderer;
            mr.m_GameObject = new PPtr<GameObject>(0, owner.m_PathID, sf);
            mr.m_Enabled = enabled;
            mr.m_Materials = Array.Empty<PPtr<Material>>();
            sf.AddObject(mr);
            return mr;
        }

        private static void AttachComponentsOnly(GameObject go, params Component[] components)
        {
            go.m_Components = components
                .Select(c => new PPtr<Component>(0, c.m_PathID, go.assetsFile))
                .ToArray();
        }
    }
}
