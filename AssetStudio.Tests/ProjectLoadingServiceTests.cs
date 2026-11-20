using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Xunit;

namespace AssetStudio.Tests
{
    public class ProjectLoadingServiceTests
    {
        [Fact]
        public void ExtractedLoadingServices_HaveZeroAvaloniaUiOrMainWindowCoupling()
        {
            var serviceTypes = new[]
            {
                typeof(ProjectLoadingService),
                typeof(SemanticGraphService),
                typeof(LazyAssetResolutionService)
            };

            foreach (var serviceType in serviceTypes)
            {
                foreach (var field in serviceType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    AssertNoUiCoupling(serviceType, field.FieldType, $"field {field.Name}");
                }

                foreach (var prop in serviceType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    AssertNoUiCoupling(serviceType, prop.PropertyType, $"property {prop.Name}");
                }

                foreach (var method in serviceType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNoUiCoupling(serviceType, method.ReturnType, $"method {method.Name} return");
                    foreach (var param in method.GetParameters())
                    {
                        AssertNoUiCoupling(serviceType, param.ParameterType, $"method {method.Name} param {param.Name}");
                    }
                }
            }
        }

        [Fact]
        public void ProjectLoadingService_CacheKeysAndDirectoryContainment_AreDeterministicAndSafe()
        {
            var tempRoot = CreateTempDirectory("pls_cache_keys");
            try
            {
                var projectA = Path.Combine(tempRoot, "ProjectA");
                var projectB = Path.Combine(tempRoot, "ProjectB");
                Directory.CreateDirectory(projectA);
                Directory.CreateDirectory(projectB);

                var keyA1 = ProjectLoadingService.GetFolderCacheKey(projectA);
                var keyA2 = ProjectLoadingService.GetFolderCacheKey(projectA);
                var keyB = ProjectLoadingService.GetFolderCacheKey(projectB);
                Assert.Equal(32, keyA1.Length);
                Assert.Equal(keyA1, keyA2);
                Assert.NotEqual(keyA1, keyB);

                var scopeA1 = ProjectLoadingService.GetDecompressedBundleScopeKey(projectA);
                var scopeA2 = ProjectLoadingService.GetDecompressedBundleScopeKey(projectA + Path.DirectorySeparatorChar);
                var scopeB = ProjectLoadingService.GetDecompressedBundleScopeKey(projectB);
                Assert.Equal(16, scopeA1.Length);
                Assert.Equal(scopeA1, scopeA2);
                Assert.NotEqual(scopeA1, scopeB);

                Assert.Equal(projectA, ProjectLoadingService.ResolveCacheFolderPath(projectA, projectB));
                Assert.Equal(projectB, ProjectLoadingService.ResolveCacheFolderPath(Path.Combine(tempRoot, "NonExistent"), projectB));
                Assert.Equal(string.Empty, ProjectLoadingService.ResolveCacheFolderPath(null, null));

                var childDir = Path.Combine(projectA, "CacheChild");
                Directory.CreateDirectory(childDir);
                File.WriteAllText(Path.Combine(childDir, "payload.bin"), "data");
                Assert.True(ProjectLoadingService.IsPathInsideDirectory(projectA, childDir));
                Assert.False(ProjectLoadingService.IsPathInsideDirectory(projectA, projectB));

                ProjectLoadingService.DeleteDirectoryInsideRoot(projectA, childDir);
                Assert.False(Directory.Exists(childDir));

                Assert.Throws<InvalidOperationException>(() =>
                    ProjectLoadingService.DeleteDirectoryInsideRoot(projectA, projectB));
                Assert.True(Directory.Exists(projectB));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectLoadingService_IndexingStateAndConnectionReadiness_PersistAndEvaluateCorrectly()
        {
            var tempRoot = CreateTempDirectory("pls_indexing_state");
            try
            {
                var cacheDbDir = Path.Combine(tempRoot, "CacheDb");
                var projectDir = Path.Combine(tempRoot, "GameProject");
                Directory.CreateDirectory(cacheDbDir);
                Directory.CreateDirectory(projectDir);

                var dataFile = Path.Combine(projectDir, "sharedassets0.assets");
                File.WriteAllBytes(dataFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                var scanResult = ProjectScanner.ScanFolder(projectDir);

                using (var sqliteCache = new SQLiteProjectIndexCache(cacheDbDir))
                {
                    Assert.Null(ProjectLoadingService.TryLoadIndexingProgress(sqliteCache, projectDir, scanResult));
                    Assert.False(ProjectLoadingService.HasCompletedLazyConnectionBuild(sqliteCache, projectDir, scanResult));
                    Assert.False(ProjectLoadingService.HasCompletedLazyStructureBuild(sqliteCache, projectDir, scanResult));
                    Assert.False(ProjectLoadingService.CanUseLazySemanticRelationCache(sqliteCache, projectDir, scanResult));

                    var runningUpdate = ProjectLoadingService.CreateStepProgressUpdate("running", 3, 10, dataFile);
                    Assert.Equal("running", runningUpdate.Status);
                    Assert.Equal(3, runningUpdate.ProcessedFiles);
                    Assert.Equal(10, runningUpdate.TotalFiles);
                    Assert.Equal(7, runningUpdate.PendingFiles);
                    Assert.Equal(30.0, runningUpdate.PercentComplete, 3);

                    ProjectLoadingService.TrySaveIndexingProgress(sqliteCache, new[] { projectDir }, scanResult, runningUpdate);
                    var loadedRunning = ProjectLoadingService.TryLoadIndexingProgress(sqliteCache, projectDir, scanResult);
                    Assert.NotNull(loadedRunning);
                    Assert.Equal("running", loadedRunning!.Status);
                    Assert.False(ProjectLoadingService.IsCompletedLazyIndexingStatus(loadedRunning.Status));
                    Assert.False(ProjectLoadingService.IsConnectionReadyStatus(loadedRunning.Status));

                    string? skippedStatus = null;
                    ProjectIndexingState? observedState = null;
                    Assert.True(ProjectLoadingService.ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(
                        sqliteCache,
                        lazyLoading: true,
                        scanResult,
                        new[] { projectDir },
                        s => observedState = s,
                        msg => skippedStatus = msg));
                    Assert.NotNull(observedState);
                    Assert.Contains("skipped", skippedStatus ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                    // Save semantic relations and mark connections_completed -> now ready for structure build.
                    var relations = new SemanticAssetRelations();
                    relations.SourceFiles.Add(new SemanticSourceFileEntry("sharedassets0.assets", dataFile, "2022.3.10f1", 1));
                    relations.MeshMaterials.Add(new SemanticMeshMaterialRelation("mesh#1", "mat#2", "ren#3", "MeshRenderer", 0, 0, 1));
                    Assert.True(ProjectLoadingService.TrySaveSemanticRelations(sqliteCache, projectDir, scanResult, relations, replaceExisting: true));

                    var connDone = ProjectLoadingService.CreateStepProgressUpdate("connections_completed", 10, 10, dataFile);
                    ProjectLoadingService.TrySaveIndexingProgress(sqliteCache, new[] { projectDir }, scanResult, connDone);

                    Assert.True(ProjectLoadingService.HasSavedSemanticRelations(sqliteCache, projectDir, scanResult));
                    Assert.True(ProjectLoadingService.HasCompletedLazyConnectionBuild(sqliteCache, projectDir, scanResult));
                    Assert.True(ProjectLoadingService.CanUseLazySemanticRelationCache(sqliteCache, projectDir, scanResult));
                    Assert.False(ProjectLoadingService.ShouldSkipLazyStructureBuildUntilConnectionsAreSaved(
                        sqliteCache,
                        lazyLoading: true,
                        scanResult,
                        new[] { projectDir }));

                    var structDone = ProjectLoadingService.CreateStepProgressUpdate("structure_completed", 6, 6, "Asset structure ready");
                    ProjectLoadingService.TrySaveIndexingProgress(sqliteCache, new[] { projectDir }, scanResult, structDone);
                    Assert.True(ProjectLoadingService.HasCompletedLazyStructureBuild(sqliteCache, projectDir, scanResult));
                }
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectLoadingService_ResolveColdIndexFiles_SkipsRescanUnlessSplitFilesExist()
        {
            var tempRoot = CreateTempDirectory("pls_cold_files");
            try
            {
                var file1 = Path.Combine(tempRoot, "level0");
                var file2 = Path.Combine(tempRoot, "sharedassets0.assets");
                File.WriteAllBytes(file1, new byte[] { 1, 2, 3, 4 });
                File.WriteAllBytes(file2, new byte[] { 5, 6, 7, 8 });

                var scanResult = ProjectScanner.ScanFolder(tempRoot);
                var mergeCalled = false;
                var enumerateCalled = false;
                var coldFiles = ProjectLoadingService.ResolveColdIndexFiles(
                    tempRoot,
                    scanResult,
                    _ => mergeCalled = true,
                    _ =>
                    {
                        enumerateCalled = true;
                        return Array.Empty<string>();
                    });

                Assert.False(mergeCalled);
                Assert.False(enumerateCalled);
                Assert.Equal(2, coldFiles.Count);

                // When a .split0 file is present in scanResult, it must invoke merge & enumerate.
                var splitFile = Path.Combine(tempRoot, "resources.assets.split0");
                File.WriteAllBytes(splitFile, new byte[] { 9, 10 });
                var splitScan = ProjectScanner.ScanFolder(tempRoot);
                var resolvedAfterSplit = ProjectLoadingService.ResolveColdIndexFiles(
                    tempRoot,
                    splitScan,
                    _ => mergeCalled = true,
                    _ =>
                    {
                        enumerateCalled = true;
                        return new[] { file1, file2 };
                    });

                Assert.True(mergeCalled);
                Assert.True(enumerateCalled);
                Assert.Equal(2, resolvedAfterSplit.Count);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectLoadingService_PreloadWindowPlanAndPinning_OrderByDistanceAndEvictOutOfWindowHandles()
        {
            var originalPath = Path.Combine(Path.GetTempPath(), "sharedassets0.assets");
            var items = new List<AssetItem>();
            for (var i = 0; i < 10; i++)
            {
                var handle = new AssetHandle
                {
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = originalPath,
                    PathID = i + 1,
                    Type = ClassIDType.Texture2D,
                    Name = $"Tex_{i}",
                    ByteSize = 128,
                    Container = string.Empty,
                    UniqueID = $"sharedassets0.assets#{i + 1}"
                };
                items.Add(new AssetItem(handle, i));
            }

            var preloaded = new HashSet<string>
            {
                items[0].Handle!.UniqueID,
                items[5].Handle!.UniqueID,
                "stale_file#999"
            };

            var (toLoad, toUnload) = ProjectLoadingService.ComputePreloadWindowPlan(
                items,
                items[5],
                preloaded,
                maxWindowSize: 4);

            Assert.Contains("stale_file#999", toUnload);
            Assert.Contains(items[0].Handle!.UniqueID, toUnload);
            Assert.DoesNotContain(items[5].Handle!.UniqueID, toUnload);
            Assert.Equal(4, toLoad.Count);
            Assert.Equal(items[5], toLoad[0]);

            var lockObj = new object();
            Assert.True(ProjectLoadingService.IsLazyHandlePinnedForPreview(
                items[5].Handle,
                lockObj,
                preloaded,
                currentlySelectedUniqueId: null));
            Assert.True(ProjectLoadingService.IsLazyHandlePinnedForPreview(
                items[9].Handle,
                lockObj,
                preloaded,
                currentlySelectedUniqueId: items[9].Handle!.UniqueID));
            Assert.False(ProjectLoadingService.IsLazyHandlePinnedForPreview(
                items[8].Handle,
                lockObj,
                preloaded,
                currentlySelectedUniqueId: items[9].Handle!.UniqueID));
        }

        [Fact]
        public void SemanticGraphService_BoneHashesProbesMaterialsAndMatrices_AreDeterministic()
        {
            Assert.Equal(1, SemanticGraphService.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 1, environmentValueOverride: ""));
            Assert.Equal(6, SemanticGraphService.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 8, environmentValueOverride: ""));
            Assert.Equal(5, SemanticGraphService.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 8, environmentValueOverride: "5"));

            var hash1 = SemanticGraphService.ComputeUnityBonePathHash("Root\\Bip001\\Pelvis/");
            var hash2 = SemanticGraphService.ComputeUnityBonePathHash("/Root/Bip001/Pelvis");
            Assert.NotEqual(0u, hash1);
            Assert.Equal(hash1, hash2);
            Assert.Equal(0u, SemanticGraphService.ComputeUnityBonePathHash("   "));
            Assert.Equal("Pelvis", SemanticGraphService.GetLeafBoneName("Root\\Bip001\\Pelvis/"));

            var inputHashes = Enumerable.Range(1, 50).Select(x => (uint)x).ToArray();
            var probes = SemanticGraphService.SelectDeterministicBoneHashProbes(inputHashes, maxProbes: 8);
            Assert.Equal(8, probes.Length);
            Assert.Equal(1u, probes[0]);
            Assert.Equal(50u, probes[^1]);

            Assert.True(SemanticGraphService.IsStrongAnimationAvatarMatch(4, 4));
            Assert.False(SemanticGraphService.IsStrongAnimationAvatarMatch(4, 3));
            Assert.True(SemanticGraphService.IsStrongMeshAvatarMatch(20, 6));
            Assert.False(SemanticGraphService.IsStrongMeshAvatarMatch(20, 3));
            Assert.Equal("heroknight", SemanticGraphService.NormalizeAnimatorSearchKey("Assets/Models/HeroKnight.fbx"));

            var relations = new SemanticAssetRelations();
            Assert.Equal(0, SemanticGraphService.CountBufferedSemanticRelationRows(relations));
            relations.AssetEdges.Add(new SemanticAssetEdgeRelation("src#1", "Material", "m_Materials", 0, "mat#2", 0, 1, 0, 2, true));
            relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation("clip#1", "anim#2", "av#3", "mesh#4", "grp#5", "AnimatorController", 9900));
            Assert.Equal(2, SemanticGraphService.CountBufferedSemanticRelationRows(relations));

            var identity = new Matrix4x4
            {
                M00 = 1f,
                M11 = 1f,
                M22 = 1f,
                M33 = 1f
            };
            Assert.Null(SemanticGraphService.MatrixToRowMajorArray(identity, omitIdentity: true));
            Assert.NotNull(SemanticGraphService.MatrixToRowMajorArray(identity, omitIdentity: false));
        }

        [Fact]
        public void LazyAssetResolutionService_ResolvesSourcePathsFallbacksAndExportBatches()
        {
            var tempRoot = CreateTempDirectory("lars_resolution");
            try
            {
                var nestedDir = Path.Combine(tempRoot, "StreamingAssets", "Bundles");
                Directory.CreateDirectory(nestedDir);
                var bundlePath = Path.Combine(nestedDir, "characters.bundle");
                File.WriteAllBytes(bundlePath, new byte[] { 10, 20, 30, 40 });

                var resolutionService = new LazyAssetResolutionService();
                resolutionService.RememberSourcePathForSerializedFile("CAB-1234", bundlePath);

                var mappedHandle = new AssetHandle
                {
                    SerializedFileName = "CAB-1234",
                    OriginalPath = string.Empty,
                    PathID = 101,
                    Type = ClassIDType.Mesh,
                    Name = "HeroMesh",
                    ByteSize = 4096,
                    Container = "assets/characters/hero.prefab",
                    UniqueID = "CAB-1234#101"
                };
                Assert.Equal(bundlePath, resolutionService.ResolveHandleSourcePath(mappedHandle, tempRoot));

                // Test suffix fallback when a project root moved across machines/folders.
                var relocatedOriginalPath = Path.Combine(tempRoot, "OldMachineRoot", "StreamingAssets", "Bundles", "characters.bundle");
                var suffixHandle = new AssetHandle
                {
                    SerializedFileName = "CAB-9999",
                    OriginalPath = relocatedOriginalPath,
                    PathID = 102,
                    Type = ClassIDType.Texture2D,
                    Name = "HeroAlbedo",
                    ByteSize = 8192,
                    Container = string.Empty,
                    UniqueID = "CAB-9999#102"
                };
                Assert.Equal(bundlePath, resolutionService.ResolveHandleSourcePath(suffixHandle, tempRoot));

                // Test batch grouping & handle metadata update/unload.
                var item1 = new AssetItem(mappedHandle, 0);
                var item2 = new AssetItem(suffixHandle, 1);
                LazyAssetResolutionService.UpdateLazyAssetItemHandle(item1, mappedHandle);
                Assert.Equal("HeroMesh", item1.Name);
                Assert.Equal(4096, item1.FullSize);

                var batches = LazyAssetResolutionService.BuildLazyExportBatches(
                    new[] { item1, item2 },
                    item => LazyAssetResolutionService.ResolveLazyExportSourceKey(
                        item,
                        h => resolutionService.ResolveHandleSourcePath(h, tempRoot)));
                Assert.Single(batches);
                Assert.Equal(2, batches[0].Items.Count);

                LazyAssetResolutionService.UnloadAsset(mappedHandle);
                Assert.Null(mappedHandle.RealObject);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void AssertNoUiCoupling(Type serviceType, Type inspectedType, string memberDescription)
        {
            var ns = inspectedType.Namespace ?? string.Empty;
            Assert.False(
                ns.StartsWith("Avalonia.", StringComparison.Ordinal) || string.Equals(ns, "Avalonia", StringComparison.Ordinal),
                $"{serviceType.Name} ({memberDescription}) references Avalonia UI type {inspectedType.FullName}");
            Assert.NotEqual(typeof(MainWindow), inspectedType);

            if (inspectedType.IsGenericType)
            {
                foreach (var arg in inspectedType.GetGenericArguments())
                {
                    AssertNoUiCoupling(serviceType, arg, memberDescription);
                }
            }
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
                // Ignore best-effort cleanup errors.
            }
        }
    }
}
