using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class AvaloniaAndStorageTests
    {
        private sealed class TrackingMemoryStream : MemoryStream
        {
            public bool IsDisposed { get; private set; }

            public TrackingMemoryStream(byte[] buffer)
                : base(buffer, writable: false)
            {
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        [Fact]
        public void SQLiteProjectIndexCache_InitializesWalSchemaAndRoundTripsHandlesAndSemanticRelations()
        {
            var tempRoot = CreateTempDirectory("sqlite_cache");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "SampleProject");
                Directory.CreateDirectory(projectDir);
                File.WriteAllBytes(Path.Combine(projectDir, "level0"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

                var scanResult = ProjectScanner.ScanFolder(projectDir);
                var cache = new SQLiteProjectIndexCache(cacheDir);
                var signature = cache.GetFolderSignature(scanResult);

                var sourcePath = Path.Combine(projectDir, "level0");
                var meshId = AssetHandle.BuildUniqueID("level0", sourcePath, 100);
                var materialId = AssetHandle.BuildUniqueID("level0", sourcePath, 200);
                var textureId = AssetHandle.BuildUniqueID("level0", sourcePath, 300);
                var avatarId = AssetHandle.BuildUniqueID("level0", sourcePath, 400);
                var clipId = AssetHandle.BuildUniqueID("level0", sourcePath, 500);
                var rendererId = AssetHandle.BuildUniqueID("level0", sourcePath, 600);
                var rootGoId = AssetHandle.BuildUniqueID("level0", sourcePath, 700);
                var groupId = AssetHandle.BuildUniqueID("level0", sourcePath, 800);

                var handles = new List<AssetHandle>
                {
                    new()
                    {
                        UniqueID = meshId,
                        Name = "HeroMesh",
                        Type = ClassIDType.Mesh,
                        Container = "assets/models/hero.fbx",
                        OriginalPath = sourcePath,
                        SerializedFileName = "level0",
                        PathID = 100,
                        ByteStart = 1024,
                        ByteSize = 4096
                    },
                    new()
                    {
                        UniqueID = materialId,
                        Name = "HeroMat",
                        Type = ClassIDType.Material,
                        Container = "assets/materials/hero.mat",
                        OriginalPath = sourcePath,
                        SerializedFileName = "level0",
                        PathID = 200,
                        ByteStart = 5120,
                        ByteSize = 512
                    },
                    new()
                    {
                        UniqueID = textureId,
                        Name = "HeroAlbedo",
                        Type = ClassIDType.Texture2D,
                        Container = "assets/textures/hero_albedo.png",
                        OriginalPath = sourcePath,
                        SerializedFileName = "level0",
                        PathID = 300,
                        ByteStart = 5632,
                        ByteSize = 16384
                    },
                    new()
                    {
                        UniqueID = avatarId,
                        Name = "HeroAvatar",
                        Type = ClassIDType.Avatar,
                        Container = "assets/models/hero.fbx",
                        OriginalPath = sourcePath,
                        SerializedFileName = "level0",
                        PathID = 400,
                        ByteStart = 22016,
                        ByteSize = 1024
                    },
                    new()
                    {
                        UniqueID = clipId,
                        Name = "HeroIdle",
                        Type = ClassIDType.AnimationClip,
                        Container = "assets/animations/idle.anim",
                        OriginalPath = sourcePath,
                        SerializedFileName = "level0",
                        PathID = 500,
                        ByteStart = 23040,
                        ByteSize = 2048
                    },
                    new()
                    {
                        UniqueID = rendererId,
                        Name = "HeroSkinnedRenderer",
                        Type = ClassIDType.SkinnedMeshRenderer,
                        Container = "assets/models/hero.fbx",
                        OriginalPath = sourcePath,
                        SerializedFileName = "level0",
                        PathID = 600,
                        ByteStart = 25088,
                        ByteSize = 768
                    }
                };

                var progressCalls = 0;
                var saved = cache.SaveIndexCache(
                    projectDir,
                    signature,
                    scanResult,
                    "2022.3.10f1",
                    handles,
                    preserveSemanticRelations: false,
                    progress: (_, _, _) => progressCalls++);
                Assert.True(saved);
                Assert.True(progressCalls > 0);

                var dbPath = cache.GetDbPath(projectDir);
                Assert.True(File.Exists(dbPath));

                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using (var pragmaCmd = conn.CreateCommand())
                    {
                        pragmaCmd.CommandText = "PRAGMA journal_mode;";
                        var mode = pragmaCmd.ExecuteScalar()?.ToString();
                        Assert.Equal("wal", mode, ignoreCase: true);
                    }

                    var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var tableCmd = conn.CreateCommand())
                    {
                        tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                        using var reader = tableCmd.ExecuteReader();
                        while (reader.Read())
                        {
                            tables.Add(reader.GetString(0));
                        }
                    }

                    Assert.Contains("Projects", tables);
                    Assert.Contains("AssetHandles", tables);
                    Assert.Contains("Assets", tables);
                    Assert.Contains("SourceFiles", tables);
                    Assert.Contains("AssetEdges", tables);
                    Assert.Contains("ModelGroups", tables);
                    Assert.Contains("ModelGroupMeshes", tables);
                    Assert.Contains("MeshRenderers", tables);
                    Assert.Contains("MeshMaterials", tables);
                    Assert.Contains("MaterialTextures", tables);
                    Assert.Contains("PreviewCacheEntries", tables);
                    Assert.Contains("ProjectIndexingState", tables);
                    Assert.Contains("IndexingReadFiles", tables);
                    Assert.Contains("CacheSchema", tables);
                }

                var loadedHandles = cache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(loadedHandles);
                Assert.Equal(handles.Count, loadedHandles!.Count);

                var loadedMesh = Assert.Single(loadedHandles, h => h.UniqueID == meshId);
                Assert.Equal("HeroMesh", loadedMesh.Name);
                Assert.Equal(ClassIDType.Mesh, loadedMesh.Type);
                Assert.Equal("assets/models/hero.fbx", loadedMesh.Container);
                Assert.Equal(sourcePath, loadedMesh.OriginalPath);
                Assert.Equal("level0", loadedMesh.SerializedFileName);
                Assert.Equal(100L, loadedMesh.PathID);
                Assert.Equal(1024L, loadedMesh.ByteStart);
                Assert.Equal(4096L, loadedMesh.ByteSize);

                Assert.Null(cache.LoadIndexCache(projectDir, "mismatched_signature"));
                Assert.False(cache.HasSemanticRelations(projectDir, signature));

                var identityMatrix = new float[]
                {
                    1f, 0f, 0f, 0f,
                    0f, 1f, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    0f, 0f, 0f, 1f
                };

                var relations = new SemanticAssetRelations();
                relations.SourceFiles.Add(new SemanticSourceFileEntry("level0", sourcePath, "2022.3.10f1", handles.Count));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    avatarId, "Mesh", "AvatarMesh", 0, meshId, 0, 400, 0, 100, isResolved: true));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    clipId, "Avatar", "AnimatorAvatar", 0, avatarId, 0, 500, 0, 400, isResolved: true));
                relations.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", "HeroRig", rootGoId, "HeroRoot", string.Empty, avatarId, string.Empty, "level0", 95, "ExactMatch"));
                relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshId, rendererId, "SkinnedMeshRenderer", rootGoId, "HeroBody", 0, identityMatrix, 95, "DirectRenderer"));
                relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                    meshId, rendererId, "SkinnedMeshRenderer", rootGoId, "HeroBody", "Primary body mesh"));
                relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshId, materialId, rendererId, "SkinnedMeshRenderer", subMeshIndex: 0, materialSlotIndex: 0, materialScore: 100));
                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    materialId, materialId, "_BaseMap", 0, textureId, 0, 300, isResolved: true, isMainTexture: true));

                Assert.True(cache.SaveSemanticRelations(projectDir, signature, relations, replaceExisting: true));
                Assert.True(cache.HasSemanticRelations(projectDir, signature));

                var meshMaterials = cache.LoadMeshMaterialAssetIds(projectDir, signature, meshId);
                Assert.Single(meshMaterials);
                Assert.Equal(materialId, meshMaterials[0]);

                var materialTextures = cache.LoadMaterialTextureAssetIds(projectDir, signature, materialId);
                Assert.Equal(new[] { textureId }, materialTextures);

                var meshRenderers = cache.LoadMeshRendererAssetIds(projectDir, signature, meshId);
                Assert.Equal(new[] { rendererId }, meshRenderers);

                var avatarMeshes = cache.LoadAvatarMeshAssetIds(projectDir, signature, avatarId);
                Assert.Equal(new[] { meshId }, avatarMeshes);

                var meshAvatars = cache.LoadMeshAvatarAssetIds(projectDir, signature, meshId);
                Assert.Equal(new[] { avatarId }, meshAvatars);

                var clipAvatars = cache.LoadAnimationClipAvatarAssetIds(projectDir, signature, clipId);
                Assert.Equal(new[] { avatarId }, clipAvatars);

                var clipMeshes = cache.LoadAnimationClipMeshAssetIds(projectDir, signature, clipId);
                Assert.Equal(new[] { meshId }, clipMeshes);

                var modelGroups = cache.LoadModelGroupsForAvatarAssetId(projectDir, signature, avatarId);
                var group = Assert.Single(modelGroups);
                Assert.Equal(groupId, group.GroupId);
                Assert.Equal("HeroRig", group.GroupName);
                Assert.Equal(95, group.Confidence);

                var candidates = cache.LoadModelGroupCandidatesForMeshAssetId(projectDir, signature, meshId);
                var candidate = Assert.Single(candidates);
                Assert.Equal(groupId, candidate.Group.GroupId);
                var candidateMesh = Assert.Single(candidate.Meshes);
                Assert.Equal(meshId, candidateMesh.MeshAssetId);
                Assert.Equal("HeroMesh", candidateMesh.MeshName);
                Assert.Equal(identityMatrix, candidateMesh.TransformMatrix);

                cache.SavePreviewCacheEntry(projectDir, signature, textureId, "png", 1, "256x256", "abc123", "/tmp/hero.png", 2048);
                var preview = cache.LoadPreviewCacheEntry(projectDir, signature, textureId, "png", 1, "256x256");
                Assert.NotNull(preview);
                Assert.Equal("abc123", preview!.PayloadHash);
                Assert.Equal("/tmp/hero.png", preview.PayloadPath);
                Assert.Equal(2048L, preview.ByteSize);

                cache.DeleteIndexCache(projectDir);
                Assert.False(File.Exists(dbPath));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectManagerStore_SaveLoadUpdateListSettingsAndRemove_RoundTripsInIsolatedStore()
        {
            var tempRoot = CreateTempDirectory("project_manager_store");
            try
            {
                var storeDir = Path.Combine(tempRoot, "Store");
                var projectRootA = Path.Combine(tempRoot, "GameA");
                var projectRootB = Path.Combine(tempRoot, "GameB");
                Directory.CreateDirectory(projectRootA);
                Directory.CreateDirectory(projectRootB);
                File.WriteAllBytes(Path.Combine(projectRootA, "data.assets"), new byte[64]);

                var store = new ProjectManagerStore(storeDir);
                Assert.Equal(Path.Combine(storeDir, "projects.db"), store.DatabasePath);

                var projectA = new ManagedProject
                {
                    Name = "Alpha Project",
                    AutoDetectedName = "Alpha Auto",
                    UseAutoName = false,
                    ProjectRoot = projectRootA,
                    LastLoadPath = projectRootA,
                    LastExportPath = Path.Combine(tempRoot, "ExportsA"),
                    Stats = new ManagedProjectStats
                    {
                        TotalFiles = 12,
                        TotalBytes = 4096,
                        UnityBundleCount = 3,
                        SerializedFileCount = 4,
                        ResourceFileCount = 5,
                        AssetCount = 150,
                        ExportableAssetCount = 120,
                        LastScanSignature = "12_4096_3"
                    }
                };

                var projectB = new ManagedProject
                {
                    Name = "Beta Project",
                    AutoDetectedName = "Beta Auto",
                    UseAutoName = true,
                    ProjectRoot = projectRootB,
                    LastLoadPath = projectRootB,
                    LastExportPath = Path.Combine(tempRoot, "ExportsB")
                };

                store.SaveProject(projectA);
                store.SaveProject(projectB);

                // Verify WAL mode on ProjectManagerStore database
                using (var conn = new SqliteConnection($"Data Source={store.DatabasePath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA journal_mode;";
                    Assert.Equal("wal", cmd.ExecuteScalar()?.ToString(), ignoreCase: true);
                }

                var loadedA = store.GetProject(projectA.Id);
                Assert.NotNull(loadedA);
                Assert.Equal("Alpha Project", loadedA!.Name);
                Assert.Equal("Alpha Auto", loadedA.AutoDetectedName);
                Assert.False(loadedA.UseAutoName);
                Assert.Equal("Alpha Project", loadedA.DisplayName);
                Assert.Equal(projectRootA, loadedA.ProjectRoot);
                Assert.Equal(12, loadedA.Stats.TotalFiles);
                Assert.Equal(4096L, loadedA.Stats.TotalBytes);
                Assert.Equal(3, loadedA.Stats.UnityBundleCount);
                Assert.Equal(150, loadedA.Stats.AssetCount);
                Assert.Equal(120, loadedA.Stats.ExportableAssetCount);
                Assert.Equal("12_4096_3", loadedA.Stats.LastScanSignature);

                var allProjects = store.GetProjects();
                Assert.Equal(2, allProjects.Count);
                Assert.Contains(allProjects, p => p.Id == projectA.Id);
                Assert.Contains(allProjects, p => p.Id == projectB.Id);

                // Global settings round-trip
                var globalSettings = new AvaloniaAppSettings
                {
                    DisplayInfo = false,
                    DisplayAll = true,
                    SelectedTheme = "Dark",
                    SpecifyUnityVersion = "2022.3.10f1"
                };
                store.SaveGlobalSettings(globalSettings);
                var reloadedGlobal = store.LoadGlobalSettings();
                Assert.False(reloadedGlobal.DisplayInfo);
                Assert.True(reloadedGlobal.DisplayAll);
                Assert.Equal("Dark", reloadedGlobal.SelectedTheme);
                Assert.Equal("2022.3.10f1", reloadedGlobal.SpecifyUnityVersion);

                // Per-project settings round-trip
                var customExportDir = Path.Combine(tempRoot, "CustomExportA");
                var projectSettings = new AvaloniaAppSettings
                {
                    ProjectRoot = projectRootA,
                    LoadFolderPath = projectRootA,
                    ExportFolderPath = customExportDir,
                    DisplayInfo = true,
                    SelectedTheme = "Ocean",
                    AvatarPreviewBoneScale = 1.5
                };
                store.SaveProjectSettings(projectA.Id, projectSettings);

                var updatedA = store.GetProject(projectA.Id)!;
                Assert.Equal(customExportDir, updatedA.LastExportPath);
                var loadedProjectSettings = store.LoadProjectSettings(updatedA);
                Assert.Equal(customExportDir, loadedProjectSettings.ExportFolderPath);
                Assert.Equal("Ocean", loadedProjectSettings.SelectedTheme);
                Assert.Equal(1.5, loadedProjectSettings.AvatarPreviewBoneScale);
                Assert.True(loadedProjectSettings.DisplayInfo);

                // UpdateProjectAfterLoad & TouchProject
                var scanResult = ProjectScanner.ScanFolder(projectRootA);
                store.UpdateProjectAfterLoad(
                    projectA.Id,
                    scanResult,
                    "1_64_0",
                    "Detected Alpha Game",
                    assetCount: 42,
                    exportableAssetCount: 30);
                store.TouchProject(projectA.Id);

                var refreshedA = store.GetProject(projectA.Id)!;
                Assert.Equal("Detected Alpha Game", refreshedA.AutoDetectedName);
                Assert.Equal("1_64_0", refreshedA.Stats.LastScanSignature);
                Assert.Equal(scanResult.TotalFiles, refreshedA.Stats.TotalFiles);
                Assert.Equal(42, refreshedA.Stats.AssetCount);
                Assert.Equal(30, refreshedA.Stats.ExportableAssetCount);
                Assert.NotNull(refreshedA.LastAccessedAtUtc);

                // RemoveProject
                store.RemoveProject(projectB.Id);
                Assert.Null(store.GetProject(projectB.Id));
                var remaining = store.GetProjects();
                Assert.Single(remaining);
                Assert.Equal(projectA.Id, remaining[0].Id);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MainWindow_ExtractStreamFiles_BlocksZipSlipRootedAndDotSegments_ExtractsValidAndDisposesStreams()
        {
            var tempRoot = CreateTempDirectory("extract_stream_files");
            try
            {
                var extractDir = Path.Combine(tempRoot, "out");
                Directory.CreateDirectory(extractDir);

                var validPayload = Encoding.UTF8.GetBytes("valid-stream-payload");
                var evilPayload = Encoding.UTF8.GetBytes("should-never-be-written");

                var traversalUnix = new TrackingMemoryStream(evilPayload);
                var traversalWin = new TrackingMemoryStream(evilPayload);
                var rootedAbsolute = new TrackingMemoryStream(evilPayload);
                var rootedSlash = new TrackingMemoryStream(evilPayload);
                var dotSegment = new TrackingMemoryStream(evilPayload);
                var dotDotSegment = new TrackingMemoryStream(evilPayload);
                var nestedTraversal = new TrackingMemoryStream(evilPayload);
                var validStream = new TrackingMemoryStream(validPayload);

                var files = new[]
                {
                    new StreamFile { path = "../../evil.txt", fileName = "evil.txt", stream = traversalUnix },
                    new StreamFile { path = "..\\..\\evil_win.txt", fileName = "evil_win.txt", stream = traversalWin },
                    new StreamFile { path = Path.Combine(tempRoot, "rooted.txt"), fileName = "rooted.txt", stream = rootedAbsolute },
                    new StreamFile { path = "/etc/passwd", fileName = "passwd", stream = rootedSlash },
                    new StreamFile { path = ".", fileName = ".", stream = dotSegment },
                    new StreamFile { path = "..", fileName = "..", stream = dotDotSegment },
                    new StreamFile { path = "CAB-01/ .. /escape.bin", fileName = "escape.bin", stream = nestedTraversal },
                    new StreamFile { path = "   ", fileName = string.Empty, stream = null! },
                    new StreamFile { path = "CAB-01/sub/valid.bin", fileName = "valid.bin", stream = validStream }
                };

                var extractedCount = MainWindow.ExtractStreamFiles(extractDir, files);
                Assert.Equal(1, extractedCount);

                // Every stream (both blocked and extracted) must be disposed
                Assert.True(traversalUnix.IsDisposed);
                Assert.True(traversalWin.IsDisposed);
                Assert.True(rootedAbsolute.IsDisposed);
                Assert.True(rootedSlash.IsDisposed);
                Assert.True(dotSegment.IsDisposed);
                Assert.True(dotDotSegment.IsDisposed);
                Assert.True(nestedTraversal.IsDisposed);
                Assert.True(validStream.IsDisposed);

                var expectedValidFile = Path.Combine(extractDir, "CAB-01", "sub", "valid.bin");
                Assert.True(File.Exists(expectedValidFile));
                Assert.Equal(validPayload, File.ReadAllBytes(expectedValidFile));

                // Nothing must have escaped outside extractDir
                Assert.False(File.Exists(Path.Combine(tempRoot, "rooted.txt")));
                Assert.False(File.Exists(Path.Combine(tempRoot, "evil.txt")));
                Assert.False(File.Exists(Path.Combine(tempRoot, "evil_win.txt")));
                Assert.Single(Directory.GetFiles(tempRoot, "*", SearchOption.AllDirectories));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MainWindow_BuildSafeClassStructurePath_AndExportClassStructuresToDisk_CloseSec02()
        {
            var tempRoot = CreateTempDirectory("class_structures_sec02");
            try
            {
                var saveDir = Path.Combine(tempRoot, "export_root");
                Directory.CreateDirectory(saveDir);
                var rootWithSep = Path.GetFullPath(saveDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                // Traversal attempts in unityVersion and typeName are neutralized within saveDir
                var traversalPath = MainWindow.BuildSafeClassStructurePath(saveDir, @"..\..\evil", @"..\..\pwn");
                Assert.StartsWith(rootWithSep, traversalPath, comparison);
                Assert.EndsWith(".txt", traversalPath, StringComparison.OrdinalIgnoreCase);

                // Windows reserved device names are safely prefixed
                var reservedPath = MainWindow.BuildSafeClassStructurePath(saveDir, "CON", "NUL");
                Assert.StartsWith(rootWithSep, reservedPath, comparison);
                Assert.Equal("_NUL.txt", Path.GetFileName(reservedPath));
                Assert.Equal("_CON", Path.GetFileName(Path.GetDirectoryName(reservedPath)));

                // Empty savePath throws ArgumentException
                Assert.Throws<ArgumentException>(() => MainWindow.BuildSafeClassStructurePath("   ", "2022.3.10f1", "Texture2D"));
                Assert.Throws<ArgumentException>(() => MainWindow.ExportClassStructuresToDisk(string.Empty, Array.Empty<(string, string, string)>()));
                Assert.Throws<ArgumentNullException>(() => MainWindow.ExportClassStructuresToDisk(saveDir, null!));

                var progressReports = new List<(int Current, int Total)>();
                var items = new (string UnityVersion, string TypeName, string Content)[]
                {
                    ("2022.3.10f1", "28 Texture2D", "class Texture2D { int m_Width; }"),
                    (@"..\..\evil", @"..\..\pwn", "neutralized traversal content"),
                    ("CON", "NUL", "reserved name content")
                };

                var exportedCount = MainWindow.ExportClassStructuresToDisk(
                    saveDir,
                    items,
                    (current, total) => progressReports.Add((current, total)));

                Assert.Equal(3, exportedCount);
                Assert.Equal(new[] { (1, 3), (2, 3), (3, 3) }, progressReports);

                var validFile = Path.Combine(saveDir, "2022.3.10f1", "28 Texture2D.txt");
                Assert.True(File.Exists(validFile));
                Assert.Equal("class Texture2D { int m_Width; }", File.ReadAllText(validFile));

                Assert.True(File.Exists(reservedPath));
                Assert.Equal("reserved name content", File.ReadAllText(reservedPath));

                Assert.True(File.Exists(traversalPath));
                Assert.Equal("neutralized traversal content", File.ReadAllText(traversalPath));

                // All written files must reside strictly within saveDir
                var allFiles = Directory.GetFiles(tempRoot, "*", SearchOption.AllDirectories);
                Assert.Equal(3, allFiles.Length);
                Assert.All(allFiles, file => Assert.StartsWith(rootWithSep, Path.GetFullPath(file), comparison));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MainWindow_GetSafeRelativeExportDirectory_AndFixFileName_SanitizePathsAndReservedNames()
        {
            Assert.Equal(string.Empty, MainWindow.GetSafeRelativeExportDirectory(null));
            Assert.Equal(string.Empty, MainWindow.GetSafeRelativeExportDirectory("   "));

            Assert.Equal(
                Path.Combine("Assets", "Textures", "Hero"),
                MainWindow.GetSafeRelativeExportDirectory(@"Assets\Textures\Hero\diffuse.png"));

            Assert.Equal(
                Path.Combine("Assets", "Models", "Props"),
                MainWindow.GetSafeRelativeExportDirectory("Assets/Models/Props/crate.prefab"));

            Assert.Equal(
                Path.Combine("evil", "Assets", "Sub"),
                MainWindow.GetSafeRelativeExportDirectory("../../evil/../Assets/./Sub/ .. /item.asset"));

            Assert.Equal(
                Path.Combine("Assets", "Sub"),
                MainWindow.GetSafeRelativeExportDirectory("../../Assets/./Sub/ .. /item.asset"));

            Assert.Equal("unnamed", MainWindow.FixFileName(null));
            Assert.Equal("unnamed", MainWindow.FixFileName("   "));
            Assert.Equal("unnamed", MainWindow.FixFileName("..."));
            Assert.Equal("_CON", MainWindow.FixFileName("CON"));
            Assert.Equal("_NUL", MainWindow.FixFileName("NUL"));
            Assert.Equal("_prn.txt", MainWindow.FixFileName("prn.txt"));
            Assert.Equal("_COM1", MainWindow.FixFileName("COM1"));
            Assert.Equal("_LPT9.dat", MainWindow.FixFileName("LPT9.dat"));

            var sanitized = MainWindow.FixFileName("a..b/c\\d:e*f?g\"h<i|j>k. ");
            Assert.DoesNotContain("..", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("/", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", sanitized, StringComparison.Ordinal);
            Assert.False(sanitized.EndsWith('.') || sanitized.EndsWith(' '));

            var longName = new string('a', 200) + "...";
            var truncated = MainWindow.FixFileName(longName);
            Assert.True(truncated.Length <= 120);
            Assert.False(truncated.EndsWith('.') || truncated.EndsWith(' '));
        }

        [Theory]
        [InlineData(true, "Texture2D 512x512", true)]
        [InlineData(false, "Texture2D 512x512", false)]
        [InlineData(true, "", false)]
        [InlineData(true, "   \t\r\n  ", false)]
        [InlineData(true, null, false)]
        [InlineData(false, null, false)]
        public void MainWindow_ShouldShowPreviewInfoOverlay_MatchesToggleAndContent(
            bool displayInfoEnabled,
            string? previewInfoText,
            bool expectedVisible)
        {
            Assert.Equal(expectedVisible, MainWindow.ShouldShowPreviewInfoOverlay(displayInfoEnabled, previewInfoText));
        }

        [Fact]
        public void MainWindow_CollectSafeAssetBundleContainers_GuardsMalformedPreloadRangesWithoutThrowing()
        {
            var results = new List<(PPtr<Object>, string)>();
            MainWindow.CollectSafeAssetBundleContainers(null, results);
            Assert.Empty(results);

            var p0 = CreateSyntheticPPtr(10);
            var p1 = CreateSyntheticPPtr(20);
            var p2 = CreateSyntheticPPtr(30);

            var bundle = (AssetBundle)RuntimeHelpers.GetUninitializedObject(typeof(AssetBundle));
            bundle.m_PreloadTable = new[] { p0, p1, p2 };
            bundle.m_Container = new[]
            {
                new KeyValuePair<string, AssetInfo>("valid_range", CreateAssetInfo(0, 2)),
                new KeyValuePair<string, AssetInfo>("clamped_end", CreateAssetInfo(2, 10)),
                new KeyValuePair<string, AssetInfo>("negative_index", CreateAssetInfo(-1, 2)),
                new KeyValuePair<string, AssetInfo>("zero_size", CreateAssetInfo(0, 0)),
                new KeyValuePair<string, AssetInfo>("negative_size", CreateAssetInfo(0, -5)),
                new KeyValuePair<string, AssetInfo>("out_of_range_index", CreateAssetInfo(99, 1)),
                new KeyValuePair<string, AssetInfo>("int_overflow_index", CreateAssetInfo(int.MaxValue, 10)),
                new KeyValuePair<string, AssetInfo>("int_overflow_size", CreateAssetInfo(1, int.MaxValue)),
                new KeyValuePair<string, AssetInfo>("null_info", null!)
            };

            MainWindow.CollectSafeAssetBundleContainers(bundle, results);

            // valid_range -> [0, 1]; clamped_end -> [2]; int_overflow_size -> [1, 2]
            Assert.Equal(5, results.Count);
            Assert.Equal((10L, "valid_range"), (results[0].Item1.m_PathID, results[0].Item2));
            Assert.Equal((20L, "valid_range"), (results[1].Item1.m_PathID, results[1].Item2));
            Assert.Equal((30L, "clamped_end"), (results[2].Item1.m_PathID, results[2].Item2));
            Assert.Equal((20L, "int_overflow_size"), (results[3].Item1.m_PathID, results[3].Item2));
            Assert.Equal((30L, "int_overflow_size"), (results[4].Item1.m_PathID, results[4].Item2));
        }

        [Fact]
        public void ImportHelper_MergeSplitAssets_ValidAtomicMerge_MissingPart_AndFallbackResolution_RegressionREL07()
        {
            var tempRoot = CreateTempDirectory("merge_split_rel07");
            string? createdFallbackPath = null;
            try
            {
                // 1) Valid .split0 + .split1 atomic merge leaves zero .tmp files
                var validDir = Path.Combine(tempRoot, "valid");
                Directory.CreateDirectory(validDir);
                var part0 = Encoding.ASCII.GetBytes("HEADER-PART-0|");
                var part1 = Encoding.ASCII.GetBytes("BODY-PART-1-END");
                var split0 = Path.Combine(validDir, "level0.split0");
                var split1 = Path.Combine(validDir, "level0.split1");
                File.WriteAllBytes(split0, part0);
                File.WriteAllBytes(split1, part1);

                ImportHelper.MergeSplitAssets(validDir);

                var mergedFile = Path.Combine(validDir, "level0");
                Assert.True(File.Exists(mergedFile));
                Assert.Equal(part0.Concat(part1).ToArray(), File.ReadAllBytes(mergedFile));
                Assert.Empty(Directory.GetFiles(validDir, "*.tmp"));

                var resolvedValid = ImportHelper.ProcessingSplitFiles(new[] { split0, split1 });
                Assert.Single(resolvedValid);
                Assert.Equal(mergedFile, resolvedValid[0]);

                // 2) Missing .split1 leaves zero truncated target and zero .tmp files
                var incompleteDir = Path.Combine(tempRoot, "incomplete");
                Directory.CreateDirectory(incompleteDir);
                File.WriteAllBytes(Path.Combine(incompleteDir, "broken.split0"), part0);
                File.WriteAllBytes(Path.Combine(incompleteDir, "broken.split2"), part1);

                ImportHelper.MergeSplitAssets(incompleteDir);

                Assert.False(File.Exists(Path.Combine(incompleteDir, "broken")));
                Assert.Empty(Directory.GetFiles(incompleteDir, "*.tmp"));

                // 3) Fallback merge path when destination cannot be written directly (directory blocks target path cross-platform)
                var fallbackSourceDir = Path.Combine(tempRoot, "fallback_src");
                Directory.CreateDirectory(fallbackSourceDir);
                var blockedTarget = Path.Combine(fallbackSourceDir, "sharedassets0.assets");
                Directory.CreateDirectory(blockedTarget); // File.Exists == false, File.Move throws IOException/UnauthorizedAccessException
                var fbSplit0 = blockedTarget + ".split0";
                var fbSplit1 = blockedTarget + ".split1";
                File.WriteAllBytes(fbSplit0, part0);
                File.WriteAllBytes(fbSplit1, part1);

                ImportHelper.MergeSplitAssets(fallbackSourceDir);

                Assert.Empty(Directory.GetFiles(fallbackSourceDir, "*.tmp"));
                Assert.True(ImportHelper.TryGetMergedSplitFallbackPath(blockedTarget, out var fallbackPath));
                createdFallbackPath = fallbackPath;
                Assert.False(string.IsNullOrWhiteSpace(fallbackPath));
                Assert.True(File.Exists(fallbackPath));
                Assert.Equal(part0.Concat(part1).ToArray(), File.ReadAllBytes(fallbackPath));

                var resolvedFallback = ImportHelper.ProcessingSplitFiles(new[] { fbSplit0, fbSplit1 });
                Assert.Single(resolvedFallback);
                Assert.Equal(fallbackPath, resolvedFallback[0]);
            }
            finally
            {
                if (!string.IsNullOrEmpty(createdFallbackPath))
                {
                    try
                    {
                        if (File.Exists(createdFallbackPath))
                        {
                            File.Delete(createdFallbackPath);
                        }
                        var fallbackDir = Path.GetDirectoryName(createdFallbackPath);
                        if (!string.IsNullOrEmpty(fallbackDir) && Directory.Exists(fallbackDir) && !Directory.EnumerateFileSystemEntries(fallbackDir).Any())
                        {
                            Directory.Delete(fallbackDir);
                        }
                    }
                    catch
                    {
                    }
                }

                ImportHelper.ClearMergedSplitFallbackPaths();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetHandle_ClearSourceHashCache_EmptiesCacheAndPreservesDeterminism_RegressionPERF05()
        {
            var uniqueName = "perf05_probe_" + Guid.NewGuid().ToString("N");
            var hashBefore = AssetHandle.BuildSourceHash(uniqueName, null);
            Assert.Equal(16, hashBefore.Length);
            Assert.Matches("^[0-9a-f]{16}$", hashBefore);

            var cacheField = typeof(AssetHandle).GetField("SourceHashCache", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(cacheField);
            var cache = Assert.IsType<ConcurrentDictionary<string, string>>(cacheField!.GetValue(null));
            Assert.True(cache.ContainsKey(uniqueName));

            AssetHandle.ClearSourceHashCache();
            Assert.False(cache.ContainsKey(uniqueName));

            var hashAfter = AssetHandle.BuildSourceHash(uniqueName, null);
            Assert.Equal(hashBefore, hashAfter);
            Assert.True(cache.ContainsKey(uniqueName));
        }

        [Fact]
        public void BundleFile_UncompressedBlocksInfoSizeExceedingSafetyCap_ThrowsInvalidDataException()
        {
            uint oversizedUncompressed = (uint)BundleFile.MaxUncompressedBlocksInfoSize + 1u;
            Assert.True(oversizedUncompressed < (uint)int.MaxValue);

            using var headerStream = new MemoryStream();
            WriteNullTerminatedUtf8(headerStream, "UnityFS");
            WriteUInt32BE(headerStream, 6u);
            WriteNullTerminatedUtf8(headerStream, "5.x.x");
            WriteNullTerminatedUtf8(headerStream, "2022.3.10f1");
            WriteInt64BE(headerStream, 256L);
            WriteUInt32BE(headerStream, 32u);
            WriteUInt32BE(headerStream, oversizedUncompressed);
            WriteUInt32BE(headerStream, (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined);
            headerStream.Write(new byte[64]);

            using var ms = new MemoryStream(headerStream.ToArray());
            using var reader = new FileReader("oversized_blocks_info.bundle", ms);
            var ex = Assert.Throws<InvalidDataException>(() => new BundleFile(reader));
            Assert.Contains("safety limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BinaryReaderExtensions_ReadStringToNull_HandlesShortStackallocLongArrayPoolAndEofWithoutNull()
        {
            // 1) Short UTF-8 string (<= 256 bytes -> stackalloc path) with trailing sentinel byte
            const string shortText = "Unity_café_日本語_short";
            var shortBytes = Encoding.UTF8.GetBytes(shortText).Concat(new byte[] { 0x00, 0x5A }).ToArray();
            using (var ms = new MemoryStream(shortBytes))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(shortText, br.ReadStringToNull());
                Assert.Equal((byte)0x5A, br.ReadByte());
            }

            // 2) Exact 256-byte boundary and 257-byte spill to ArrayPool
            var exact256 = new string('x', 256);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(exact256 + "\0")))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(exact256, br.ReadStringToNull());
            }

            var spill257 = new string('y', 257);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(spill257 + "\0")))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(spill257, br.ReadStringToNull());
            }

            // 3) Long UTF-8 string (> 256 bytes -> ArrayPool growth across multiple doublings)
            var longBuilder = new StringBuilder();
            for (int i = 0; i < 120; i++)
            {
                longBuilder.Append("αβγδεζηθ_0123456789_");
            }
            var longText = longBuilder.ToString();
            Assert.True(Encoding.UTF8.GetByteCount(longText) > 2048);
            var longBytes = Encoding.UTF8.GetBytes(longText).Concat(new byte[] { 0x00, 0x7E }).ToArray();
            using (var ms = new MemoryStream(longBytes))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(longText, br.ReadStringToNull());
                Assert.Equal((byte)0x7E, br.ReadByte());
            }

            // 4) EOF without null terminator (both short stackalloc and long ArrayPool paths)
            const string shortNoNull = "eof_without_null_café";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(shortNoNull)))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(shortNoNull, br.ReadStringToNull());
            }

            var longNoNull = new string('Z', 600);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(longNoNull)))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(longNoNull, br.ReadStringToNull());
            }

            // 5) maxLength clamping
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("abcdefgh\0")))
            using (var br = new BinaryReader(ms))
            {
                Assert.Equal(string.Empty, br.ReadStringToNull(0));
                Assert.Equal("abcd", br.ReadStringToNull(4));
            }
        }

        private static PPtr<Object> CreateSyntheticPPtr(long pathId)
        {
            var pptr = (PPtr<Object>)RuntimeHelpers.GetUninitializedObject(typeof(PPtr<Object>));
            pptr.m_FileID = 0;
            pptr.m_PathID = pathId;
            return pptr;
        }

        private static AssetInfo CreateAssetInfo(int preloadIndex, int preloadSize)
        {
            var info = (AssetInfo)RuntimeHelpers.GetUninitializedObject(typeof(AssetInfo));
            info.preloadIndex = preloadIndex;
            info.preloadSize = preloadSize;
            info.asset = CreateSyntheticPPtr(999);
            return info;
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudioTests_{prefix}_{Guid.NewGuid():N}");
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

        private static void WriteNullTerminatedUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, value);
            stream.Write(buf);
        }
    }
}
