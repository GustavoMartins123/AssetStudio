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
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class SmartSceneExportPipelineTests
    {
        // =========================================================================
        // 1. SQLite Schema v16: SourceFileExternals & Scenes CRUD + Cycle-Safe BFS
        // =========================================================================

        [Fact]
        public void SQLiteScenes_CrudAndCycleSafeTransitiveExternalBfs_WorksAcrossDepthAndCycles()
        {
            var tempRoot = CreateTempDirectory("sqlite_scenes_crud");
            try
            {
                var projectDir = Path.Combine(tempRoot, "GameProject");
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(projectDir);

                var level0Path = Path.Combine(projectDir, "level0");
                var shared0Path = Path.Combine(projectDir, "sharedassets0.assets");
                var shared1Path = Path.Combine(projectDir, "sharedassets1.assets");
                var resPath = Path.Combine(projectDir, "resources.assets");
                File.WriteAllBytes(level0Path, new byte[] { 1, 2, 3, 4 });
                File.WriteAllBytes(shared0Path, new byte[] { 5, 6, 7, 8 });
                File.WriteAllBytes(shared1Path, new byte[] { 9, 10, 11, 12 });
                File.WriteAllBytes(resPath, new byte[] { 13, 14, 15, 16 });

                var scan = ProjectScanner.ScanFolder(projectDir);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig = cache.GetFolderSignature(scan);

                var handles = new List<AssetHandle>
                {
                    CreateHandle("level0", level0Path, 10, "RootGO", ClassIDType.GameObject),
                    CreateHandle("sharedassets0.assets", shared0Path, 20, "SharedMesh", ClassIDType.Mesh),
                    CreateHandle("sharedassets1.assets", shared1Path, 30, "SharedMat", ClassIDType.Material),
                    CreateHandle("resources.assets", resPath, 40, "SharedTex", ClassIDType.Texture2D)
                };

                Assert.True(cache.SaveIndexCache(projectDir, sig, scan, "2022.3.40f1", handles, markIndexingCompleted: true));
                var projectId = cache.TryGetProjectId(projectDir, sig);
                Assert.NotNull(projectId);

                // Graph with cycle + self-loop + streaming sidecar (.resS):
                // level0 -> sharedassets0.assets, level0.resS
                // sharedassets0.assets -> sharedassets0.assets (self), sharedassets1.assets
                // sharedassets1.assets -> level0 (cycle back to seed), resources.assets
                var externals = new List<SourceFileExternalRecord>
                {
                    new()
                    {
                        SourceSerializedFileName = "level0",
                        SourceOriginalPath = level0Path,
                        ExternalIndex = 0,
                        ExternalFileName = "sharedassets0.assets",
                        ExternalPathName = "sharedassets0.assets",
                        ExternalGuid = "11111111111111111111111111111111"
                    },
                    new()
                    {
                        SourceSerializedFileName = "level0",
                        SourceOriginalPath = level0Path,
                        ExternalIndex = 1,
                        ExternalFileName = "level0.resS",
                        ExternalPathName = "archive:/level0/level0.resS"
                    },
                    new()
                    {
                        SourceSerializedFileName = "sharedassets0.assets",
                        SourceOriginalPath = shared0Path,
                        ExternalIndex = 0,
                        ExternalFileName = "sharedassets0.assets",
                        ExternalPathName = "sharedassets0.assets"
                    },
                    new()
                    {
                        SourceSerializedFileName = "sharedassets0.assets",
                        SourceOriginalPath = shared0Path,
                        ExternalIndex = 1,
                        ExternalFileName = "sharedassets1.assets",
                        ExternalPathName = "sharedassets1.assets"
                    },
                    new()
                    {
                        SourceSerializedFileName = "sharedassets1.assets",
                        SourceOriginalPath = shared1Path,
                        ExternalIndex = 0,
                        ExternalFileName = "level0",
                        ExternalPathName = "level0"
                    },
                    new()
                    {
                        SourceSerializedFileName = "sharedassets1.assets",
                        SourceOriginalPath = shared1Path,
                        ExternalIndex = 1,
                        ExternalFileName = "resources.assets",
                        ExternalPathName = "resources.assets"
                    }
                };

                Assert.True(cache.SaveSourceFileExternals(projectDir, sig, externals));

                var level0Externals = cache.GetSourceFileExternals(projectDir, sig, "level0");
                Assert.Equal(2, level0Externals.Count);
                Assert.Equal("sharedassets0.assets", level0Externals[0].ExternalFileName);
                Assert.Equal("11111111111111111111111111111111", level0Externals[0].ExternalGuid);

                // Depth 1: only direct non-sidecar external
                var depth1 = cache.GetTransitiveExternalFileNames(projectDir, sig, new[] { "level0" }, maxDepth: 1);
                Assert.Equal(new[] { "sharedassets0.assets" }, depth1);

                // Depth 2: level0 -> sharedassets0 -> sharedassets1
                var depth2 = cache.GetTransitiveExternalFileNames(projectDir, sig, new[] { "level0" }, maxDepth: 2);
                Assert.Equal(new[] { "sharedassets0.assets", "sharedassets1.assets" }, depth2);

                // Depth 4: full closure without infinite loop or returning seed "level0" or ".resS"
                var depth4 = cache.GetTransitiveExternalFileNames((int)projectId!.Value, new[] { "level0" }, maxDepth: 4);
                Assert.Equal(new[] { "sharedassets0.assets", "sharedassets1.assets", "resources.assets" }, depth4);

                // Resolve paths by names
                var pathMap = cache.GetSourceFilePathsByNames(projectDir, sig, depth4);
                Assert.Equal(shared0Path, pathMap["sharedassets0.assets"]);
                Assert.Equal(shared1Path, pathMap["sharedassets1.assets"]);
                Assert.Equal(resPath, pathMap["resources.assets"]);

                // Scenes CRUD & ordering (BuildIndex >= 0 first, then -1 sorted by Name)
                var scenesToSave = new List<IndexedSceneRecord>
                {
                    new()
                    {
                        Name = "DLC_Cave",
                        ScenePath = "assets/scenes/dlc_cave.unity",
                        BuildIndex = -1,
                        EntrySerializedFileName = "sharedassets1.assets",
                        EntryOriginalPath = shared1Path,
                        ContainerKey = "assets/scenes/dlc_cave.unity",
                        SourceKind = "AssetBundle"
                    },
                    new()
                    {
                        Name = "Title",
                        ScenePath = "Assets/Scenes/Title.unity",
                        BuildIndex = 0,
                        EntrySerializedFileName = "level0",
                        EntryOriginalPath = level0Path,
                        SourceKind = "BuildSettings"
                    }
                };

                Assert.True(cache.SaveScenes(projectDir, sig, scenesToSave));

                var loadedBySig = cache.GetIndexedScenes(projectDir, sig);
                Assert.Equal(2, loadedBySig.Count);
                Assert.Equal("Title", loadedBySig[0].Name);
                Assert.Equal(0, loadedBySig[0].BuildIndex);
                Assert.True(loadedBySig[0].EntrySourceFileId > 0);
                Assert.Equal("level0", loadedBySig[0].EntrySerializedFileName);
                Assert.Equal(level0Path, loadedBySig[0].EntryOriginalPath);
                Assert.Equal("BuildSettings", loadedBySig[0].SourceKind);

                Assert.Equal("DLC_Cave", loadedBySig[1].Name);
                Assert.Equal(-1, loadedBySig[1].BuildIndex);
                Assert.Equal("AssetBundle", loadedBySig[1].SourceKind);

                // ProjectId-only overload returns identical results
                var loadedById = cache.GetIndexedScenes((int)projectId.Value);
                Assert.Equal(2, loadedById.Count);
                Assert.Equal("Title", loadedById[0].Name);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SQLiteScenes_OsAwareCaseSensitivity_DistinguishesCaseWhenEnabledAndMergesWhenDisabled()
        {
            var tempRoot = CreateTempDirectory("sqlite_scenes_case");
            try
            {
                var projectDir = Path.Combine(tempRoot, "Project");
                var linuxCacheDir = Path.Combine(tempRoot, "CacheLinux");
                var winCacheDir = Path.Combine(tempRoot, "CacheWin");
                Directory.CreateDirectory(projectDir);

                var fileA = Path.Combine(projectDir, "a.assets");
                var fileB = Path.Combine(projectDir, "b.assets");
                File.WriteAllBytes(fileA, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(fileB, new byte[] { 4, 5, 6 });
                var scan = ProjectScanner.ScanFolder(projectDir);

                // Case-sensitive (Linux semantics)
                using (var linuxCache = new SQLiteProjectIndexCache(linuxCacheDir, caseSensitivePaths: true))
                {
                    var sig = linuxCache.GetFolderSignature(scan);
                    var handles = new[]
                    {
                        CreateHandle("Level0", fileA, 1, "UpperRoot", ClassIDType.GameObject),
                        CreateHandle("level0", fileB, 2, "LowerRoot", ClassIDType.GameObject)
                    };
                    Assert.True(linuxCache.SaveIndexCache(projectDir, sig, scan, "2022.3.10f1", handles, markIndexingCompleted: true));

                    Assert.True(linuxCache.SaveSourceFileExternals(projectDir, sig, new[]
                    {
                        new SourceFileExternalRecord
                        {
                            SourceSerializedFileName = "Level0",
                            SourceOriginalPath = fileA,
                            ExternalIndex = 0,
                            ExternalFileName = "SharedUpper.assets"
                        },
                        new SourceFileExternalRecord
                        {
                            SourceSerializedFileName = "level0",
                            SourceOriginalPath = fileB,
                            ExternalIndex = 0,
                            ExternalFileName = "sharedlower.assets"
                        }
                    }));

                    var lowerDeps = linuxCache.GetTransitiveExternalFileNames(projectDir, sig, new[] { "level0" }, maxDepth: 2);
                    var upperDeps = linuxCache.GetTransitiveExternalFileNames(projectDir, sig, new[] { "Level0" }, maxDepth: 2);

                    Assert.Equal(new[] { "sharedlower.assets" }, lowerDeps);
                    Assert.Equal(new[] { "SharedUpper.assets" }, upperDeps);
                }

                // Case-insensitive (Windows/macOS semantics)
                using (var winCache = new SQLiteProjectIndexCache(winCacheDir, caseSensitivePaths: false))
                {
                    var sig = winCache.GetFolderSignature(scan);
                    var handles = new[]
                    {
                        CreateHandle("level0", fileB, 2, "LowerRoot", ClassIDType.GameObject)
                    };
                    Assert.True(winCache.SaveIndexCache(projectDir, sig, scan, "2022.3.10f1", handles, markIndexingCompleted: true));

                    Assert.True(winCache.SaveSourceFileExternals(projectDir, sig, new[]
                    {
                        new SourceFileExternalRecord
                        {
                            SourceSerializedFileName = "level0",
                            SourceOriginalPath = fileB,
                            ExternalIndex = 0,
                            ExternalFileName = "sharedlower.assets"
                        }
                    }));

                    var queriedUpper = winCache.GetTransitiveExternalFileNames(projectDir, sig, new[] { "LEVEL0" }, maxDepth: 2);
                    Assert.Equal(new[] { "sharedlower.assets" }, queriedUpper);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 2. Scene Discovery: BuildSettings, AssetBundle (preloadSize == 0), Addressables, Fallback
        // =========================================================================

        [Fact]
        public void SceneDiscovery_BuildSettings_AssetBundlePreloadZero_AddressablesSceneProvider_AndFallback()
        {
            var tempRoot = CreateTempDirectory("scene_discovery");
            try
            {
                var projectDir = Path.Combine(tempRoot, "GameData");
                var cacheDir = Path.Combine(tempRoot, "Cache");
                Directory.CreateDirectory(projectDir);

                var level0Path = Path.Combine(projectDir, "level0");
                var level1Path = Path.Combine(projectDir, "level1");
                var level9Path = Path.Combine(projectDir, "level9");
                var dlcBundlePath = Path.Combine(projectDir, "dlc_cave.bundle");
                var arenaBundlePath = Path.Combine(projectDir, "arena_pvp.bundle");
                File.WriteAllBytes(level0Path, new byte[] { 1, 2 });
                File.WriteAllBytes(level1Path, new byte[] { 3, 4 });
                File.WriteAllBytes(level9Path, new byte[] { 5, 6 });
                File.WriteAllBytes(dlcBundlePath, new byte[] { 7, 8 });
                File.WriteAllBytes(arenaBundlePath, new byte[] { 9, 10 });

                using var am = new AssetsManager();
                var ggmSf = CreateSyntheticSerializedFile(am, "globalgamemanagers", "2022.3.20f1");
                ggmSf.originalPath = Path.Combine(projectDir, "globalgamemanagers");

                // (a) BuildSettings (ClassID 141) mapping level0 & level1
                using (var bsMs = new MemoryStream())
                using (var bw = new BinaryWriter(bsMs, Encoding.UTF8, leaveOpen: true))
                {
                    WriteStringArray(bw, new[]
                    {
                        "Assets/Scenes/Title.unity",
                        "Assets/Scenes/TownSquare.unity"
                    });
                    WriteStringArray(bw, Array.Empty<string>());
                    var bsObj = CreateObjectViaAssetsManager(ggmSf, ClassIDType.BuildSettings, pathId: 11, bsMs.ToArray());
                    ggmSf.AddObject(bsObj);
                }

                // (b) AssetBundle with .unity container AND preloadSize == 0 + empty preload table
                var dlcSf = CreateSyntheticSerializedFile(am, "CAB-dlc-cave", "2022.3.20f1");
                dlcSf.originalPath = dlcBundlePath;
                var dlcAssetInfo = (AssetInfo)RuntimeHelpers.GetUninitializedObject(typeof(AssetInfo));
                dlcAssetInfo.preloadIndex = 0;
                dlcAssetInfo.preloadSize = 0;
                dlcAssetInfo.asset = CreatePPtr<AssetStudio.Object>(dlcSf, 0, 500);
                var bundleObj = CreateSyntheticAssetBundle(
                    dlcSf,
                    pathId: 1,
                    name: "dlc_cave",
                    preloadTable: Array.Empty<PPtr<AssetStudio.Object>>(),
                    containerEntries: new[]
                    {
                        new KeyValuePair<string, AssetInfo>("assets/scenes/dlc_cave.unity", dlcAssetInfo)
                    });
                dlcSf.AddObject(bundleObj);

                // Verify CollectSafeAssetBundleContainers also captures preloadSize == 0 direct assets!
                var extractedContainers = ProjectLoadingService.CollectAssetBundleContainers(bundleObj);
                Assert.Single(extractedContainers);
                Assert.Equal("assets/scenes/dlc_cave.unity", extractedContainers[0].Item2);
                Assert.Equal(500, extractedContainers[0].Item1.m_PathID);

                // (c) Addressables SceneProvider catalog entry
                var catalog = new ParsedAddressablesCatalog
                {
                    CatalogPath = Path.Combine(projectDir, "catalog.json"),
                    CatalogDirectory = projectDir,
                    Format = "json"
                };
                catalog.IndexAssetEntry(new CatalogAssetEntry
                {
                    PrimaryKey = "Arena_PvP",
                    InternalId = "Assets/Scenes/Arena_PvP.unity",
                    ProviderId = "UnityEngine.ResourceManagement.ResourceProviders.SceneProvider",
                    PrimaryBundleInternalId = arenaBundlePath,
                    PrimaryBundleFileName = "arena_pvp.bundle",
                    IsScene = true
                });

                // Handles including level0, level1, and fallback level9 (not in BuildSettings!)
                var handles = new List<AssetHandle>
                {
                    CreateHandle("level0", level0Path, 100, "TitleRoot", ClassIDType.GameObject),
                    CreateHandle("level1", level1Path, 200, "TownRoot", ClassIDType.GameObject),
                    CreateHandle("level9", level9Path, 300, "SecretLevelRoot", ClassIDType.GameObject),
                    CreateHandle("CAB-dlc-cave", dlcBundlePath, 500, "CaveRoot", ClassIDType.GameObject),
                    CreateHandle("CAB-arena-pvp", arenaBundlePath, 600, "ArenaRoot", ClassIDType.GameObject)
                };

                var discovered = ProjectLoadingService.DiscoverScenesAndExternals(
                    am,
                    projectDir,
                    handles: handles,
                    catalogsOverride: new[] { catalog });

                Assert.Equal(5, discovered.Scenes.Count);

                var title = discovered.Scenes.Single(s => s.Name == "Title");
                Assert.Equal("BuildSettings", title.SourceKind);
                Assert.Equal(0, title.BuildIndex);
                Assert.Equal("level0", title.EntrySerializedFileName);
                Assert.Equal(level0Path, title.EntryOriginalPath);

                var town = discovered.Scenes.Single(s => s.Name == "TownSquare");
                Assert.Equal("BuildSettings", town.SourceKind);
                Assert.Equal(1, town.BuildIndex);
                Assert.Equal("level1", town.EntrySerializedFileName);

                var cave = discovered.Scenes.Single(s => s.Name == "dlc_cave");
                Assert.Equal("AssetBundle", cave.SourceKind);
                Assert.Equal("assets/scenes/dlc_cave.unity", cave.ContainerKey);
                Assert.Equal("CAB-dlc-cave", cave.EntrySerializedFileName);

                var arena = discovered.Scenes.Single(s => s.Name == "Arena_PvP");
                Assert.Equal("Addressables", arena.SourceKind);
                Assert.Equal("CAB-arena-pvp", arena.EntrySerializedFileName);
                Assert.Equal(arenaBundlePath, arena.EntryOriginalPath);

                var secret = discovered.Scenes.Single(s => s.Name == "level9");
                Assert.Equal("Heuristic", secret.SourceKind);
                Assert.Equal(9, secret.BuildIndex);

                // Persist through SQLite and reload cleanly
                var scan = ProjectScanner.ScanFolder(projectDir);
                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig = cache.GetFolderSignature(scan);
                Assert.True(cache.SaveIndexCache(projectDir, sig, scan, "2022.3.20f1", handles, markIndexingCompleted: true));
                Assert.True(cache.SaveScenes(projectDir, sig, discovered.Scenes));

                var persisted = cache.GetIndexedScenes(projectDir, sig);
                Assert.Equal(5, persisted.Count);
                Assert.Equal("Title", persisted[0].Name);
                Assert.Equal("TownSquare", persisted[1].Name);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 3. End-to-End Smart Scene .glb Export: Closure, LOD0 vs AllLods, Cameras/Lights, Manifest
        // =========================================================================

        [Fact]
        public void SmartSceneExport_Lod0VsAllLods_CamerasLights_UnsupportedComponents_AndScene11Manifest()
        {
            var tempRoot = CreateTempDirectory("smart_scene_export");
            try
            {
                using var am = new AssetsManager();
                var sceneFile = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");
                var sharedFile = CreateSyntheticSerializedFile(am, "sharedassets0.assets", "2022.3.40f1");

                // Add one resolved external (sharedassets0.assets) and one missing external (CAB-missing-props)
                sceneFile.m_Externals.Add(new FileIdentifier { fileName = "sharedassets0.assets", pathName = "sharedassets0.assets" });
                sceneFile.m_Externals.Add(new FileIdentifier { fileName = "CAB-missing-props", pathName = "archive:/CAB-missing-props/CAB-missing-props" });

                var towerLod0Mesh = CreateSyntheticBoxMesh(sharedFile, pathId: 1001, name: "SM_Tower_LOD0");
                var towerLod1Mesh = CreateSyntheticBoxMesh(sharedFile, pathId: 1002, name: "SM_Tower_LOD1");
                var pillarMesh = CreateSyntheticBoxMesh(sharedFile, pathId: 1003, name: "SM_Pillar");
                var stoneMat = CreateSyntheticMaterial(sharedFile, pathId: 2001, name: "Mat_Stone");

                // Root GameObject
                var rootGo = CreateSyntheticGameObject(sceneFile, 100, "TownRoot", isActive: true);
                var rootTr = CreateSyntheticTransform(sceneFile, 101, rootGo, father: null, Vector3.Zero);

                // LODGroup object with LOD0 + LOD1 children
                var towerGo = CreateSyntheticGameObject(sceneFile, 200, "WatchTower", isActive: true);
                var towerTr = CreateSyntheticTransform(sceneFile, 201, towerGo, father: rootTr, new Vector3(10f, 0f, 0f));

                var lod0Go = CreateSyntheticGameObject(sceneFile, 210, "Tower_LOD0", isActive: true);
                var lod0Tr = CreateSyntheticTransform(sceneFile, 211, lod0Go, father: towerTr, Vector3.Zero);
                var lod0Mf = CreateSyntheticMeshFilter(sceneFile, 212, lod0Go, fileId: 1, meshPathId: towerLod0Mesh.m_PathID);
                var lod0Mr = CreateSyntheticMeshRenderer(sceneFile, 213, lod0Go, enabled: true, new[] { CreatePPtr<Material>(sceneFile, 1, stoneMat.m_PathID) });
                AttachComponents(lod0Go, lod0Tr, lod0Mf, lod0Mr);

                var lod1Go = CreateSyntheticGameObject(sceneFile, 220, "Tower_LOD1", isActive: true);
                var lod1Tr = CreateSyntheticTransform(sceneFile, 221, lod1Go, father: towerTr, Vector3.Zero);
                var lod1Mf = CreateSyntheticMeshFilter(sceneFile, 222, lod1Go, fileId: 1, meshPathId: towerLod1Mesh.m_PathID);
                var lod1Mr = CreateSyntheticMeshRenderer(sceneFile, 223, lod1Go, enabled: true, new[] { CreatePPtr<Material>(sceneFile, 1, stoneMat.m_PathID) });
                AttachComponents(lod1Go, lod1Tr, lod1Mf, lod1Mr);

                var lodGroup = CreateSyntheticLodGroup(sceneFile, 202, towerGo, new[] { lod0Mr }, new[] { lod1Mr });
                // Attach unmaterialized BoxCollider (only in ObjectInfoDic!) to verify lazy unsupported-component detection!
                sceneFile.ObjectInfoDic[203] = new ObjectInfo { m_PathID = 203, classID = (int)ClassIDType.BoxCollider };
                towerGo.m_Components = new[]
                {
                    CreatePPtr<Component>(sceneFile, 0, towerTr.m_PathID),
                    CreatePPtr<Component>(sceneFile, 0, lodGroup.m_PathID),
                    CreatePPtr<Component>(sceneFile, 0, 203)
                };
                towerTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sceneFile, 0, lod0Tr.m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, lod1Tr.m_PathID)
                };

                // 3 instanced sibling pillars sharing pillarMesh + stoneMat
                var pillarTransforms = new List<Transform>();
                for (var i = 0; i < 3; i++)
                {
                    var pGo = CreateSyntheticGameObject(sceneFile, 300 + i * 10, "Pillar", isActive: true);
                    var pTr = CreateSyntheticTransform(sceneFile, 301 + i * 10, pGo, father: rootTr, new Vector3(i * 4f, 0f, -5f));
                    var pMf = CreateSyntheticMeshFilter(sceneFile, 302 + i * 10, pGo, fileId: 1, meshPathId: pillarMesh.m_PathID);
                    var pMr = CreateSyntheticMeshRenderer(sceneFile, 303 + i * 10, pGo, enabled: true, new[] { CreatePPtr<Material>(sceneFile, 1, stoneMat.m_PathID) });
                    AttachComponents(pGo, pTr, pMf, pMr);
                    pillarTransforms.Add(pTr);
                }

                // Camera + Directional Light
                var camGo = CreateSyntheticGameObject(sceneFile, 400, "MainCamera", isActive: true);
                var camTr = CreateSyntheticTransform(sceneFile, 401, camGo, father: rootTr, new Vector3(0f, 5f, -10f));
                var camComp = CreateSyntheticPerspectiveCamera(sceneFile, 402, camGo, fov: 60f, near: 0.3f, far: 500f);
                AttachComponents(camGo, camTr, camComp);

                var lightGo = CreateSyntheticGameObject(sceneFile, 410, "SunLight", isActive: true);
                var lightTr = CreateSyntheticTransform(sceneFile, 411, lightGo, father: rootTr, new Vector3(0f, 20f, 0f));
                var lightComp = CreateSyntheticDirectionalLight(sceneFile, 412, lightGo, intensity: 1.5f);
                AttachComponents(lightGo, lightTr, lightComp);

                // Inactive debug object (and child) -> should increment skippedInactiveObjects when IncludeInactive == false
                var inactiveGo = CreateSyntheticGameObject(sceneFile, 500, "DebugProxy", isActive: false);
                var inactiveTr = CreateSyntheticTransform(sceneFile, 501, inactiveGo, father: rootTr, Vector3.Zero);
                var inactiveChildGo = CreateSyntheticGameObject(sceneFile, 510, "DebugProxyChild", isActive: true);
                var inactiveChildTr = CreateSyntheticTransform(sceneFile, 511, inactiveChildGo, father: inactiveTr, Vector3.Zero);
                inactiveTr.m_Children = new[] { CreatePPtr<Transform>(sceneFile, 0, inactiveChildTr.m_PathID) };
                AttachComponents(inactiveGo, inactiveTr);
                AttachComponents(inactiveChildGo, inactiveChildTr);

                // Missing asset prop (points to missing external fileId=2, pathId=9999)
                var brokenGo = CreateSyntheticGameObject(sceneFile, 600, "MissingProp", isActive: true);
                var brokenTr = CreateSyntheticTransform(sceneFile, 601, brokenGo, father: rootTr, new Vector3(-5f, 0f, 0f));
                var brokenMf = CreateSyntheticMeshFilter(sceneFile, 602, brokenGo, fileId: 2, meshPathId: 9999);
                var brokenMr = CreateSyntheticMeshRenderer(sceneFile, 603, brokenGo, enabled: true, new[] { CreatePPtr<Material>(sceneFile, 2, 8888) });
                AttachComponents(brokenGo, brokenTr, brokenMf, brokenMr);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sceneFile, 0, towerTr.m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, pillarTransforms[0].m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, pillarTransforms[1].m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, pillarTransforms[2].m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, camTr.m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, lightTr.m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, inactiveTr.m_PathID),
                    CreatePPtr<Transform>(sceneFile, 0, brokenTr.m_PathID)
                };
                AttachComponents(rootGo, rootTr);

                var service = new SceneGlbExportService();

                // --- Run 1: Default Smart Mode (LOD0Only, IncludeInactive=false, Cameras+Lights=true, Manifest=true) ---
                var lod0GlbPath = Path.Combine(tempRoot, "TownSquare_LOD0.glb");
                var lod0Result = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = lod0GlbPath,
                        SceneName = "TownSquare",
                        ScenePath = "Assets/Scenes/TownSquare.unity",
                        BuildIndex = 1,
                        IncludeInactive = false,
                        LodMode = SceneLodExportMode.Lod0Only,
                        ExportCamerasAndLights = true,
                        ResolveStaticScriptSpawns = false,
                        WriteManifest = true
                    });

                Assert.True(lod0Result.Success, lod0Result.ErrorMessage);
                Assert.True(File.Exists(lod0GlbPath));
                Assert.Equal(1, lod0Result.SkippedLodRendererCount);
                Assert.Equal(2, lod0Result.SkippedInactiveObjectCount);
                Assert.Equal(1, lod0Result.Stats.CamerasCount);
                Assert.Equal(1, lod0Result.Stats.LightsCount);
                Assert.Equal(4, lod0Result.Stats.TotalMeshInstances); // 1 LOD0 tower + 3 pillars
                Assert.Equal(2, lod0Result.Stats.UniqueGltfMeshes);   // SM_Tower_LOD0 + SM_Pillar
                Assert.Equal(2, lod0Result.Stats.ReusedGeometryCount);

                // Verify GLB contains cameras, KHR_lights_punctual, and NO LOD1 mesh!
                using (var glbDoc = ParseGlbJson(lod0GlbPath))
                {
                    var rootEl = glbDoc.RootElement;
                    Assert.Equal(1, rootEl.GetProperty("cameras").GetArrayLength());
                    Assert.Equal(1, rootEl.GetProperty("extensions").GetProperty("KHR_lights_punctual").GetProperty("lights").GetArrayLength());

                    var meshNames = rootEl.GetProperty("meshes")
                        .EnumerateArray()
                        .Select(m => m.GetProperty("name").GetString())
                        .ToList();
                    Assert.Contains("SM_Tower_LOD0", meshNames);
                    Assert.Contains("SM_Pillar", meshNames);
                    Assert.DoesNotContain("SM_Tower_LOD1", meshNames);
                }

                // Verify SCENE-11 Manifest (.manifest.json)
                var expectedManifestPath = Path.Combine(tempRoot, "TownSquare_LOD0.manifest.json");
                Assert.Equal(expectedManifestPath, lod0Result.ManifestPath);
                Assert.True(File.Exists(expectedManifestPath));

                var manifestText = File.ReadAllText(expectedManifestPath);
                Assert.Contains("\n", manifestText); // indented JSON
                using (var mDoc = JsonDocument.Parse(manifestText))
                {
                    var m = mDoc.RootElement;
                    Assert.Equal("TownSquare", m.GetProperty("sceneName").GetString());
                    Assert.Equal("Assets/Scenes/TownSquare.unity", m.GetProperty("scenePath").GetString());
                    Assert.Equal(1, m.GetProperty("buildIndex").GetInt32());
                    Assert.Equal(lod0GlbPath, m.GetProperty("outputGlbPath").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("exportTimestampUtc").GetString()));

                    var optsEl = m.GetProperty("options");
                    Assert.False(optsEl.GetProperty("includeInactive").GetBoolean());
                    Assert.Equal("Lod0Only", optsEl.GetProperty("lodMode").GetString());
                    Assert.True(optsEl.GetProperty("exportCamerasAndLights").GetBoolean());
                    Assert.False(optsEl.GetProperty("resolveStaticScriptSpawns").GetBoolean());
                    Assert.True(optsEl.GetProperty("writeManifest").GetBoolean());

                    var statsEl = m.GetProperty("statistics");
                    Assert.True(statsEl.GetProperty("nodes").GetInt32() >= 6);
                    Assert.Equal(2, statsEl.GetProperty("meshes").GetInt32());
                    Assert.Equal(2, statsEl.GetProperty("uniqueGeometryCount").GetInt32());
                    Assert.Equal(2, statsEl.GetProperty("instancedNodes").GetInt32());
                    Assert.Equal(1, statsEl.GetProperty("materials").GetInt32());
                    Assert.Equal(1, statsEl.GetProperty("cameras").GetInt32());
                    Assert.Equal(1, statsEl.GetProperty("lights").GetInt32());
                    Assert.Equal(1, statsEl.GetProperty("skippedLodRenderers").GetInt32());
                    Assert.Equal(0, statsEl.GetProperty("attachedScriptSpawns").GetInt32());
                    Assert.Equal(2, statsEl.GetProperty("skippedInactiveObjects").GetInt32());

                    var resolvedDeps = m.GetProperty("resolvedDependencies").EnumerateArray().Select(e => e.GetString()).ToList();
                    var missingDeps = m.GetProperty("missingDependencies").EnumerateArray().Select(e => e.GetString()).ToList();
                    Assert.Contains("sharedassets0.assets", resolvedDeps);
                    Assert.Contains("CAB-missing-props", missingDeps);

                    var missingAssets = m.GetProperty("missingAssets").EnumerateArray().ToList();
                    Assert.Contains(missingAssets, a =>
                        a.GetProperty("assetType").GetString() == "Mesh"
                        && a.GetProperty("pathId").GetInt64() == 9999
                        && a.GetProperty("sourceFile").GetString() == "CAB-missing-props");

                    var unsupported = m.GetProperty("unsupportedComponents");
                    Assert.Equal(1, unsupported.GetProperty("BoxCollider").GetInt32());
                }

                // --- Run 2: AllLods + IncludeInactive=true + ExportCamerasAndLights=false + WriteManifest=false ---
                var allLodsGlbPath = Path.Combine(tempRoot, "TownSquare_AllLods.glb");
                var allLodsResult = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = allLodsGlbPath,
                        SceneName = "TownSquare",
                        IncludeInactive = true,
                        LodMode = SceneLodExportMode.AllLods,
                        ExportCamerasAndLights = false,
                        WriteManifest = false
                    });

                Assert.True(allLodsResult.Success, allLodsResult.ErrorMessage);
                Assert.Equal(0, allLodsResult.SkippedLodRendererCount);
                Assert.Equal(0, allLodsResult.SkippedInactiveObjectCount);
                Assert.Equal(0, allLodsResult.Stats.CamerasCount);
                Assert.Equal(0, allLodsResult.Stats.LightsCount);
                Assert.Equal(5, allLodsResult.Stats.TotalMeshInstances); // LOD0 + LOD1 + 3 pillars
                Assert.Null(allLodsResult.ManifestPath);
                Assert.False(File.Exists(Path.Combine(tempRoot, "TownSquare_AllLods.manifest.json")));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SmartSceneExport_OptInStaticScriptSpawns_AttachesPrefabAndLogsDynamicHintsInManifest()
        {
            var tempRoot = CreateTempDirectory("smart_scene_spawns");
            try
            {
                using var am = new AssetsManager();
                var sceneFile = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");
                var prefabFile = CreateSyntheticSerializedFile(am, "CAB-prefabs", "2022.3.40f1");
                sceneFile.m_Externals.Add(new FileIdentifier { fileName = "CAB-prefabs", pathName = "CAB-prefabs" });

                // External prefab in CAB-prefabs
                var crateMesh = CreateSyntheticBoxMesh(prefabFile, pathId: 9001, name: "SM_SpawnedCrate");
                var crateMat = CreateSyntheticMaterial(prefabFile, pathId: 9002, name: "Mat_Crate");
                var cratePrefab = CreateSyntheticGameObject(prefabFile, 9010, "CratePrefab", isActive: true);
                var crateTr = CreateSyntheticTransform(prefabFile, 9011, cratePrefab, father: null, Vector3.Zero);
                var crateMf = CreateSyntheticMeshFilter(prefabFile, 9012, cratePrefab, fileId: 0, meshPathId: crateMesh.m_PathID);
                var crateMr = CreateSyntheticMeshRenderer(prefabFile, 9013, cratePrefab, enabled: true, new[] { CreatePPtr<Material>(prefabFile, 0, crateMat.m_PathID) });
                AttachComponents(cratePrefab, crateTr, crateMf, crateMr);

                // Scene with ground + static spawner MonoBehaviour + dynamic spawner MonoBehaviour
                var rootGo = CreateSyntheticGameObject(sceneFile, 100, "ArenaRoot", isActive: true);
                var rootTr = CreateSyntheticTransform(sceneFile, 101, rootGo, father: null, Vector3.Zero);

                var spawnerGo = CreateSyntheticGameObject(sceneFile, 200, "LootSpawner", isActive: true);
                var spawnerTr = CreateSyntheticTransform(sceneFile, 201, spawnerGo, father: rootTr, new Vector3(3f, 0f, 4f));
                var staticScript = CreateSyntheticMonoScript(sceneFile, 202, "Game.Spawning", "StaticPropSpawner", "Assembly-CSharp.dll");
                var staticMb = CreateSyntheticMonoBehaviour(sceneFile, 203, spawnerGo, staticScript);

                var dynamicScript = CreateSyntheticMonoScript(sceneFile, 204, "Game.Spawning", "DynamicBossSpawner", "Assembly-CSharp.dll");
                var dynamicMb = CreateSyntheticMonoBehaviour(sceneFile, 205, spawnerGo, dynamicScript);
                AttachComponents(spawnerGo, spawnerTr, staticMb, dynamicMb);

                rootTr.m_Children = new[] { CreatePPtr<Transform>(sceneFile, 0, spawnerTr.m_PathID) };
                AttachComponents(rootGo, rootTr);

                var analysisOptions = new SceneScriptSpawnAnalysisOptions
                {
                    CustomFieldProvider = mb =>
                    {
                        if (ReferenceEquals(mb, staticMb))
                        {
                            var fields = new OrderedDictionary
                            {
                                ["cratePrefab"] = new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = cratePrefab.m_PathID },
                                ["spawnOffset"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 1.5f, ["z"] = 0f }
                            };
                            return (fields, null);
                        }

                        if (ReferenceEquals(mb, dynamicMb))
                        {
                            var fields = new OrderedDictionary
                            {
                                ["prefabPath"] = "Prefabs/Bosses/DynamicDragon"
                            };
                            return (fields, null);
                        }

                        return (null, null);
                    }
                };

                var service = new SceneGlbExportService();

                // 1. Default (opt-out): ResolveStaticScriptSpawns == false -> 0 attached spawns
                var optOutGlb = Path.Combine(tempRoot, "Arena_NoSpawns.glb");
                var optOutResult = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = optOutGlb,
                        SceneName = "Arena",
                        ResolveStaticScriptSpawns = false,
                        ScriptSpawnAnalysisOptions = analysisOptions,
                        WriteManifest = true
                    });

                Assert.True(optOutResult.Success, optOutResult.ErrorMessage);
                Assert.Equal(0, optOutResult.AttachedScriptSpawnCount);

                // 2. Opt-in: ResolveStaticScriptSpawns == true -> attaches CratePrefab AND logs DynamicBossSpawner hint!
                var optInGlb = Path.Combine(tempRoot, "Arena_WithSpawns.glb");
                var optInResult = service.ExportScene(
                    new[] { rootGo },
                    new SceneGlbExportOptions
                    {
                        OutputPath = optInGlb,
                        SceneName = "Arena",
                        ResolveStaticScriptSpawns = true,
                        ScriptSpawnAnalysisOptions = analysisOptions,
                        WriteManifest = true
                    });

                Assert.True(optInResult.Success, optInResult.ErrorMessage);
                Assert.Equal(1, optInResult.AttachedScriptSpawnCount);
                Assert.Equal(1, optInResult.Stats.TotalMeshInstances);

                using (var glbDoc = ParseGlbJson(optInGlb))
                {
                    var spawnedNodes = glbDoc.RootElement.GetProperty("nodes")
                        .EnumerateArray()
                        .Where(n => n.TryGetProperty("name", out var nm) && (nm.GetString()?.StartsWith("[ScriptSpawned]", StringComparison.Ordinal) ?? false))
                        .ToList();
                    Assert.Single(spawnedNodes);
                    Assert.True(spawnedNodes[0].GetProperty("extras").TryGetProperty("scriptSpawn", out var spawnExtra));
                    Assert.Equal("Game.Spawning.StaticPropSpawner", spawnExtra.GetProperty("scriptClass").GetString());
                }

                Assert.NotNull(optInResult.ManifestPath);
                using (var mDoc = JsonDocument.Parse(File.ReadAllText(optInResult.ManifestPath!)))
                {
                    var scriptSpawnsEl = mDoc.RootElement.GetProperty("scriptSpawns");
                    var resolved = scriptSpawnsEl.GetProperty("resolved").EnumerateArray().ToList();
                    Assert.Single(resolved);
                    Assert.Equal("Game.Spawning.StaticPropSpawner", resolved[0].GetProperty("scriptClass").GetString());
                    Assert.Equal("cratePrefab", resolved[0].GetProperty("fieldPath").GetString());
                    Assert.True(resolved[0].GetProperty("attached").GetBoolean());

                    var dynamicHints = scriptSpawnsEl.GetProperty("unresolvedDynamicSpawners").EnumerateArray().ToList();
                    Assert.Single(dynamicHints);
                    var hint = dynamicHints[0];
                    Assert.Equal("ArenaRoot/LootSpawner", hint.GetProperty("gameObjectPath").GetString());
                    Assert.Equal("Game.Spawning.DynamicBossSpawner", hint.GetProperty("scriptClass").GetString());
                    Assert.Equal("prefabPath", hint.GetProperty("fieldPath").GetString());
                    Assert.Contains("Prefabs/Bosses/DynamicDragon", hint.GetProperty("candidateTargets").EnumerateArray().Select(e => e.GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(hint.GetProperty("confidence").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(hint.GetProperty("reason").GetString()));
                }
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SmartSceneExport_CrossBundleDependencyClosureAndAdditiveMultiSceneMerge_WorkDeterministically()
        {
            var tempRoot = CreateTempDirectory("smart_scene_closure");
            try
            {
                // Verify levelN <-> sharedassetsN pairing helper
                Assert.Equal(new[] { "sharedassets0.assets" }, SceneGlbExportService.GetPairedLevelSharedAssetsFileNames("level0"));
                Assert.Equal(new[] { "sharedassets3.assets", "sharedassets0.assets" }, SceneGlbExportService.GetPairedLevelSharedAssetsFileNames("level3"));
                Assert.Equal(new[] { "sharedassets0.assets", "resources.assets" }, SceneGlbExportService.GetPairedLevelSharedAssetsFileNames("mainData"));
                Assert.Empty(SceneGlbExportService.GetPairedLevelSharedAssetsFileNames("CAB-abc123"));

                // Verify transitive SQLite + in-memory dependency closure with cycle & missing external
                var projectDir = Path.Combine(tempRoot, "Project");
                var cacheDir = Path.Combine(tempRoot, "Cache");
                Directory.CreateDirectory(projectDir);
                var level0Disk = Path.Combine(projectDir, "level0");
                var shared0Disk = Path.Combine(projectDir, "sharedassets0.assets");
                File.WriteAllBytes(level0Disk, new byte[] { 1, 2 });
                File.WriteAllBytes(shared0Disk, new byte[] { 3, 4 });

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan = ProjectScanner.ScanFolder(projectDir);
                var sig = cache.GetFolderSignature(scan);
                Assert.True(cache.SaveIndexCache(projectDir, sig, scan, "2022.3.40f1", new[]
                {
                    CreateHandle("level0", level0Disk, 1, "SceneA", ClassIDType.GameObject),
                    CreateHandle("sharedassets0.assets", shared0Disk, 2, "SharedMesh", ClassIDType.Mesh)
                }, markIndexingCompleted: true));
                Assert.True(cache.SaveSourceFileExternals(projectDir, sig, new[]
                {
                    new SourceFileExternalRecord
                    {
                        SourceSerializedFileName = "level0",
                        SourceOriginalPath = level0Disk,
                        ExternalIndex = 0,
                        ExternalFileName = "sharedassets0.assets"
                    },
                    new SourceFileExternalRecord
                    {
                        SourceSerializedFileName = "sharedassets0.assets",
                        SourceOriginalPath = shared0Disk,
                        ExternalIndex = 0,
                        ExternalFileName = "CAB-missing-dep"
                    }
                }));

                using var am = new AssetsManager();
                var sceneA = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");
                var shared0 = CreateSyntheticSerializedFile(am, "sharedassets0.assets", "2022.3.40f1");
                var sceneB = CreateSyntheticSerializedFile(am, "level1", "2022.3.40f1");
                sceneA.m_Externals.Add(new FileIdentifier { fileName = "sharedassets0.assets", pathName = "sharedassets0.assets" });
                shared0.m_Externals.Add(new FileIdentifier { fileName = "level0", pathName = "level0" }); // cycle

                // Simulate reader presence so ResolveAndLoadSceneDependencies sees shared0 already in RAM
                var closure = SceneGlbExportService.ResolveAndLoadSceneDependencies(
                    assetsManager: am,
                    seedFiles: new[] { sceneA },
                    sqliteCache: cache,
                    cacheFolderPath: projectDir,
                    cacheSignature: sig,
                    projectRoot: projectDir,
                    maxDepth: 4,
                    maxAdditionalFiles: 16);

                Assert.Contains("CAB-missing-dep", closure.MissingDependencies);

                // Additive multi-scene merge into one instanced GLB
                var sharedMesh = CreateSyntheticBoxMesh(shared0, 7001, "SM_SharedCrate");
                var sharedMat = CreateSyntheticMaterial(shared0, 7002, "Mat_Shared");

                var rootA = CreateSyntheticGameObject(sceneA, 100, "MainSceneRoot", isActive: true);
                var trA = CreateSyntheticTransform(sceneA, 101, rootA, father: null, Vector3.Zero);
                var mfA = CreateSyntheticMeshFilter(sceneA, 102, rootA, fileId: 1, meshPathId: sharedMesh.m_PathID);
                var mrA = CreateSyntheticMeshRenderer(sceneA, 103, rootA, enabled: true, new[] { CreatePPtr<Material>(sceneA, 1, sharedMat.m_PathID) });
                AttachComponents(rootA, trA, mfA, mrA);

                sceneB.m_Externals.Add(new FileIdentifier { fileName = "sharedassets0.assets", pathName = "sharedassets0.assets" });
                var rootB = CreateSyntheticGameObject(sceneB, 200, "AdditivePropsRoot", isActive: true);
                var trB = CreateSyntheticTransform(sceneB, 201, rootB, father: null, new Vector3(8f, 0f, 0f));
                var mfB = CreateSyntheticMeshFilter(sceneB, 202, rootB, fileId: 1, meshPathId: sharedMesh.m_PathID);
                var mrB = CreateSyntheticMeshRenderer(sceneB, 203, rootB, enabled: true, new[] { CreatePPtr<Material>(sceneB, 1, sharedMat.m_PathID) });
                AttachComponents(rootB, trB, mfB, mrB);

                var mergedGlb = Path.Combine(tempRoot, "CombinedScenes.glb");
                var service = new SceneGlbExportService();
                var mergedResult = service.ExportScene(
                    new[] { rootA, rootB },
                    new SceneGlbExportOptions
                    {
                        OutputPath = mergedGlb,
                        SceneName = "MainScene",
                        AdditionalSceneNames = new[] { "AdditiveProps" },
                        WriteManifest = true
                    });

                Assert.True(mergedResult.Success, mergedResult.ErrorMessage);
                Assert.Equal(2, mergedResult.Stats.TotalMeshInstances);
                Assert.Equal(1, mergedResult.Stats.UniqueGltfMeshes); // Instanced across both additive scenes!
                Assert.Equal(1, mergedResult.Stats.ReusedGeometryCount);

                using var mDoc = JsonDocument.Parse(File.ReadAllText(mergedResult.ManifestPath!));
                var additional = mDoc.RootElement.GetProperty("additionalScenes").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Equal(new[] { "AdditiveProps" }, additional);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 4. SceneExportDialogViewModel: Filter, Multi-Scene Selection, Defaults, Commands
        // =========================================================================

        [Fact]
        public void SceneExportDialogViewModel_SearchFilter_MultiSceneAdditiveSelection_Defaults_AndCommands()
        {
            var candidates = new[]
            {
                new SceneExportCandidate
                {
                    Id = "s0",
                    Name = "Title",
                    ScenePath = "Assets/Scenes/Title.unity",
                    BuildIndex = 0,
                    SourceKind = "BuildSettings",
                    SerializedFileName = "level0"
                },
                new SceneExportCandidate
                {
                    Id = "s1",
                    Name = "TownSquare",
                    ScenePath = "Assets/Scenes/World/TownSquare.unity",
                    BuildIndex = 1,
                    SourceKind = "BuildSettings",
                    SerializedFileName = "level1",
                    IsPreselected = true
                },
                new SceneExportCandidate
                {
                    Id = "s2",
                    Name = "TownSquare_Lighting",
                    ScenePath = "assets/scenes/townsquare_lighting.unity",
                    BuildIndex = -1,
                    SourceKind = "AssetBundle",
                    SerializedFileName = "CAB-lighting"
                }
            };

            var vm = new SceneExportDialogViewModel(new SceneExportDialogRequest(candidates));

            // Defaults match specification
            Assert.Equal(SceneLodExportMode.Lod0Only, vm.LodMode);
            Assert.Equal(0, vm.SelectedLodModeIndex);
            Assert.False(vm.IncludeInactive);
            Assert.True(vm.ExportCamerasAndLights);
            Assert.False(vm.ResolveStaticScriptSpawns); // opt-in
            Assert.True(vm.WriteManifest);

            // Explicit pre-selection honored
            Assert.Equal(3, vm.TotalCount);
            Assert.Equal(3, vm.FilteredCount);
            Assert.Equal(1, vm.SelectedCount);
            Assert.True(vm.HasSelection);
            Assert.True(vm.ConfirmCommand.CanExecute());
            Assert.Equal("TownSquare", vm.GetSelectedCandidates().Single().Name);

            // Search filter narrows visible list while keeping existing selections
            vm.SearchText = "Town";
            Assert.Equal(2, vm.FilteredCount);
            Assert.DoesNotContain(vm.FilteredScenes, s => s.Name == "Title");

            // Select all visible (additive multi-scene merge: TownSquare + TownSquare_Lighting)
            vm.SelectAllFilteredCommand.Execute();
            Assert.Equal(2, vm.SelectedCount);
            Assert.Contains("merged additively", vm.SelectionSummaryText, StringComparison.OrdinalIgnoreCase);

            // Clear search -> both remain selected, Title remains unchecked
            vm.ClearSearchCommand.Execute();
            Assert.Equal(string.Empty, vm.SearchText);
            Assert.Equal(3, vm.FilteredCount);
            Assert.Equal(2, vm.SelectedCount);

            // Clear selection disables ConfirmCommand
            vm.ClearSelectionCommand.Execute();
            Assert.Equal(0, vm.SelectedCount);
            Assert.False(vm.HasSelection);
            Assert.False(vm.ConfirmCommand.CanExecute());

            // Select 2 scenes + toggle options + confirm
            vm.AllScenes[1].IsSelected = true;
            vm.AllScenes[2].IsSelected = true;
            vm.SelectedLodModeIndex = 1; // AllLods
            vm.IncludeInactive = true;
            vm.ResolveStaticScriptSpawns = true;
            Assert.Equal(SceneLodExportMode.AllLods, vm.LodMode);

            SceneExportDialogResult? closedResult = null;
            var closeInvocations = 0;
            vm.RequestClose += res =>
            {
                closeInvocations++;
                closedResult = res;
            };

            vm.ConfirmCommand.Execute();
            Assert.Equal(1, closeInvocations);
            Assert.NotNull(closedResult);
            Assert.Equal(2, closedResult!.SelectedScenes.Count);
            Assert.Equal(SceneLodExportMode.AllLods, closedResult.Options.LodMode);
            Assert.True(closedResult.Options.IncludeInactive);
            Assert.True(closedResult.Options.ResolveStaticScriptSpawns);
            Assert.True(closedResult.Options.ExportCamerasAndLights);
            Assert.True(closedResult.Options.WriteManifest);

            // Cancel closes with null
            vm.CancelCommand.Execute();
            Assert.Equal(2, closeInvocations);
            Assert.Null(closedResult);
            Assert.Null(vm.Result);
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static AssetHandle CreateHandle(string sfName, string origPath, long pathId, string name, ClassIDType type, string container = "")
        {
            return new AssetHandle
            {
                SerializedFileName = sfName,
                OriginalPath = origPath,
                PathID = pathId,
                Name = name,
                Type = type,
                Container = container,
                ByteStart = 0,
                ByteSize = 64,
                UniqueID = $"{sfName}#{pathId}"
            };
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager manager, string fileName, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = manager;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.reader = new FileReader(fileName, new MemoryStream(Array.Empty<byte>()));
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            sf.m_Objects = new List<ObjectInfo>();
            sf.m_Externals = new List<FileIdentifier>();
            sf.m_Types = new List<SerializedType>();
            sf.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.LargeFilesSupport };
            sf.SetVersion(unityVersion);

            typeof(SerializedFile)
                .GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sf, new object());

            lock (manager.loadLock)
            {
                manager.assetsFileList.Add(sf);
            }

            return sf;
        }

        private static AssetStudio.Object CreateObjectViaAssetsManager(SerializedFile sf, ClassIDType classId, long pathId, byte[] payload)
        {
            using var ms = new MemoryStream(payload, writable: false);
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
        {
            return new PPtr<T>(fileId, pathId, sf);
        }

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive)
        {
            var go = CreateSyntheticObject<GameObject>(sf, pathId, ClassIDType.GameObject);
            go.m_Name = name;
            go.m_IsActive = isActive;
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

        private static MeshRenderer CreateSyntheticMeshRenderer(SerializedFile sf, long pathId, GameObject go, bool enabled, PPtr<Material>[] materials)
        {
            var mr = CreateSyntheticObject<MeshRenderer>(sf, pathId, ClassIDType.MeshRenderer);
            mr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mr.m_Enabled = enabled;
            mr.m_Materials = materials;
            mr.m_SubsetIndices = Array.Empty<uint>();
            return mr;
        }

        private static LODGroup CreateSyntheticLodGroup(SerializedFile sf, long pathId, GameObject go, Renderer[] lod0Renderers, Renderer[] lod1Renderers)
        {
            var lg = CreateSyntheticObject<LODGroup>(sf, pathId, ClassIDType.LODGroup);
            lg.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            lg.m_Enabled = true;
            lg.m_Size = 5f;
            lg.m_LODs = new[]
            {
                new LOD
                {
                    screenRelativeHeight = 0.5f,
                    renderers = lod0Renderers.Select(r => CreatePPtr<Renderer>(sf, 0, r.m_PathID)).ToArray()
                },
                new LOD
                {
                    screenRelativeHeight = 0.1f,
                    renderers = lod1Renderers.Select(r => CreatePPtr<Renderer>(sf, 0, r.m_PathID)).ToArray()
                }
            };
            return lg;
        }

        private static Camera CreateSyntheticPerspectiveCamera(SerializedFile sf, long pathId, GameObject go, float fov, float near, float far)
        {
            var cam = CreateSyntheticObject<Camera>(sf, pathId, ClassIDType.Camera);
            cam.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            cam.m_Enabled = 1;
            cam.m_Orthographic = false;
            cam.m_FieldOfView = fov;
            cam.m_NearClipPlane = near;
            cam.m_FarClipPlane = far;
            return cam;
        }

        private static Light CreateSyntheticDirectionalLight(SerializedFile sf, long pathId, GameObject go, float intensity)
        {
            var light = CreateSyntheticObject<Light>(sf, pathId, ClassIDType.Light);
            light.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            light.m_Enabled = 1;
            light.m_Type = LightType.Directional;
            light.m_Color = new Color(1f, 0.95f, 0.85f, 1f);
            light.m_Intensity = intensity;
            return light;
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

        private static MonoBehaviour CreateSyntheticMonoBehaviour(SerializedFile sf, long pathId, GameObject go, MonoScript script)
        {
            var mb = CreateSyntheticObject<MonoBehaviour>(sf, pathId, ClassIDType.MonoBehaviour);
            mb.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mb.m_Enabled = 1;
            mb.m_Script = CreatePPtr<MonoScript>(sf, 0, script.m_PathID);
            mb.m_Name = string.Empty;
            return mb;
        }

        private static AssetBundle CreateSyntheticAssetBundle(
            SerializedFile sf,
            long pathId,
            string name,
            PPtr<AssetStudio.Object>[] preloadTable,
            KeyValuePair<string, AssetInfo>[] containerEntries)
        {
            var ab = CreateSyntheticObject<AssetBundle>(sf, pathId, ClassIDType.AssetBundle);
            ab.m_Name = name;
            ab.m_PreloadTable = preloadTable;
            ab.m_Container = containerEntries;
            return ab;
        }

        private static Mesh CreateSyntheticBoxMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = CreateSyntheticObject<Mesh>(sf, pathId, ClassIDType.Mesh);
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new float[]
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f
            };
            mesh.m_Normals = new float[]
            {
                0f, 0f, 1f,
                0f, 0f, 1f,
                0f, 0f, 1f
            };
            mesh.m_UV0 = new float[]
            {
                0f, 0f,
                1f, 0f,
                0f, 1f
            };
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

        private static Material CreateSyntheticMaterial(SerializedFile sf, long pathId, string name)
        {
            var mat = CreateSyntheticObject<Material>(sf, pathId, ClassIDType.Material);
            mat.m_Name = name;
            mat.m_Shader = CreatePPtr<Shader>(sf, 0, 0);
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = Array.Empty<KeyValuePair<string, UnityTexEnv>>(),
                m_Floats = Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = new[] { new KeyValuePair<string, Color>("_BaseColor", new Color(0.8f, 0.7f, 0.6f, 1f)) }
            };
            return mat;
        }

        private static void AttachComponents(GameObject go, params Component[] components)
        {
            go.m_Components = components.Select(c => CreatePPtr<Component>(go.assetsFile, 0, c.m_PathID)).ToArray();
        }

        private static void WriteStringArray(BinaryWriter bw, string[] items)
        {
            bw.Write(items.Length);
            foreach (var item in items)
            {
                var bytes = Encoding.UTF8.GetBytes(item);
                bw.Write(bytes.Length);
                bw.Write(bytes);
                var mod = bytes.Length & 3;
                if (mod != 0)
                {
                    bw.Write(new byte[4 - mod]);
                }
            }
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
