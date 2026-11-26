using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class ColdIndexingParallelTests
    {
        private const string OthercideBundlesDir =
            @"H:\SteamLibrary\steamapps\common\Othercide\Othercide_Data\StreamingAssets\aa\Windows\StandaloneWindows64";

        private readonly ITestOutputHelper _output;

        public ColdIndexingParallelTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AssetsManager_DefaultThreadCount_ScalesWithModernCores_AndHonorsEnvironmentOverrides()
        {
            Assert.Equal(0.75, AssetsManager.DefaultLoadThreadRatio);
            Assert.Equal(0.75, AssetsManager.DefaultReadThreadRatio);
            Assert.Equal(0.75, AssetsManager.DefaultLazyLoadThreadRatio);
            Assert.Equal(0.75, AssetsManager.DefaultLazyReadThreadRatio);
            Assert.Equal(16, AssetsManager.MaxDefaultThreadCount);

            // Simulated core counts without environment variable overrides:
            // 1 core -> 1 thread
            Assert.Equal(1, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 1, environmentValueOverride: ""));
            // 2 cores -> at least 2 threads
            Assert.Equal(2, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 2, environmentValueOverride: ""));
            // 4 cores -> Floor(4 * 0.75) = 3 threads (previously Floor(4 * 0.4) = 1 single-threaded!)
            Assert.Equal(3, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 4, environmentValueOverride: ""));
            // 8 cores -> Floor(8 * 0.75) = 6 threads (previously Floor(8 * 0.4) = 3)
            Assert.Equal(6, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 8, environmentValueOverride: ""));
            // 16 cores -> Floor(16 * 0.75) = 12 threads (previously Floor(16 * 0.4) = 6)
            Assert.Equal(12, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 16, environmentValueOverride: ""));
            // 32 cores -> capped at MaxDefaultThreadCount (16)
            Assert.Equal(16, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 32, environmentValueOverride: ""));

            // Semantic graph parallelism degree across simulated core counts:
            Assert.Equal(1, MainWindow.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 1, environmentValueOverride: ""));
            Assert.Equal(2, MainWindow.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 2, environmentValueOverride: ""));
            Assert.Equal(3, MainWindow.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 4, environmentValueOverride: ""));
            Assert.Equal(6, MainWindow.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 8, environmentValueOverride: ""));
            Assert.Equal(12, MainWindow.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 16, environmentValueOverride: ""));
            Assert.Equal(16, MainWindow.GetSemanticGraphMaxDegreeOfParallelism(processorCountOverride: 32, environmentValueOverride: ""));

            // Explicit environment variable override must take precedence even above 16 or down to 1:
            Assert.Equal(1, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 16, environmentValueOverride: "1"));
            Assert.Equal(24, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 8, environmentValueOverride: "24"));
            Assert.Equal(6, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 8, environmentValueOverride: "0"));
            Assert.Equal(6, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 8, environmentValueOverride: "-5"));
            Assert.Equal(6, AssetsManager.ComputeConfiguredThreadCount("ASSETSTUDIO_TEST_THREADS", AssetsManager.DefaultLoadThreadRatio, processorCountOverride: 8, environmentValueOverride: "invalid"));

            // Verify live Environment.SetEnvironmentVariable is respected immediately by GetConfiguredThreadCount:
            const string envVarName = "ASSETSTUDIO_TEST_DYNAMIC_ENV_THREADS";
            var originalEnv = Environment.GetEnvironmentVariable(envVarName);
            try
            {
                Environment.SetEnvironmentVariable(envVarName, "7");
                Assert.Equal(7, AssetsManager.GetConfiguredThreadCount(envVarName, AssetsManager.DefaultLoadThreadRatio));

                Environment.SetEnvironmentVariable(envVarName, "11");
                Assert.Equal(11, AssetsManager.GetConfiguredThreadCount(envVarName, AssetsManager.DefaultLoadThreadRatio));
            }
            finally
            {
                Environment.SetEnvironmentVariable(envVarName, originalEnv);
            }
        }

        [Fact]
        public void AssetLoadingService_O1PendingQueue_PriorityDequeueAndRemoval_ScalesLinearlyOnThousandsOfFiles()
        {
            var service = new AssetLoadingService();
            const int totalFiles = 5_000;
            var files = new List<string>(totalFiles);
            var expectedPriority = new List<string>();
            var expectedRegular = new List<string>();
            var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < totalFiles; i++)
            {
                var isPriority = (i % 5) == 0;
                var fileName = isPriority
                    ? $"C:\\SyntheticProject\\Bundles\\hero_texture_{i:D5}.bundle"
                    : $"C:\\SyntheticProject\\Bundles\\level_env_{i:D5}.bundle";
                files.Add(fileName);

                if ((i % 97) == 0)
                {
                    toRemove.Add(fileName);
                }
                else if (isPriority)
                {
                    expectedPriority.Add(fileName);
                }
                else
                {
                    expectedRegular.Add(fileName);
                }
            }

            var sw = Stopwatch.StartNew();
            service.InitializePendingQueue(files);
            Assert.Equal(totalFiles, service.PendingCount);

            foreach (var removedPath in toRemove)
            {
                service.RemovePendingFile(removedPath);
            }

            var expectedRemaining = totalFiles - toRemove.Count;
            Assert.Equal(expectedRemaining, service.PendingCount);
            Assert.Equal(expectedRemaining, service.PendingFilesToIndex.Count);

            var keywordSearch = AssetLoadingService.GetPriorityKeywordSearch(new[] { ClassIDType.Texture2D });
            Assert.NotNull(keywordSearch);

            var dequeued = new List<string>(expectedRemaining);
            while (service.TryDequeueNextPendingFile(keywordSearch, out var nextFile))
            {
                dequeued.Add(nextFile);
            }
            sw.Stop();

            Assert.Equal(0, service.PendingCount);
            Assert.Empty(service.PendingFilesToIndex);

            var expectedOrder = expectedPriority.Concat(expectedRegular).ToList();
            Assert.Equal(expectedOrder.Count, dequeued.Count);
            Assert.Equal(expectedOrder, dequeued);

            _output.WriteLine($"O(1) pending queue: initialized, removed {toRemove.Count}, and priority-dequeued {dequeued.Count:N0} files in {sw.Elapsed.TotalMilliseconds:0.##} ms.");
            Assert.True(sw.Elapsed.TotalMilliseconds < 250.0, $"Expected O(1) queue operations on 5,000 files to take < 250 ms, actual {sw.Elapsed.TotalMilliseconds:0.##} ms.");
        }

        [Fact]
        public async Task AssetLoadingService_ContinuousWorkerWindow_SupportsPauseResumeCancelAndProgressSnapshots_AcrossHundredsOfFiles()
        {
            var tempDir = CreateTempDirectory("cold_indexing_continuous_window");
            try
            {
                const int fileCount = 220;
                var syntheticFiles = new List<string>(fileCount);
                for (int i = 0; i < fileCount; i++)
                {
                    var name = (i % 4 == 0) ? $"ui_sprite_{i:D4}.bundle" : $"world_chunk_{i:D4}.bundle";
                    var path = Path.Combine(tempDir, name);
                    File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
                    syntheticFiles.Add(path);
                }

                // 1. Full completion + pause/resume + progress snapshot verification
                var service = new AssetLoadingService();
                using var manager = new AssetsManager { LazyLoading = true, ProjectRoot = tempDir };
                var progressUpdates = new ConcurrentQueue<IndexingProgressUpdate>();
                var finishedTcs = new TaskCompletionSource<(bool WasCancelled, int LoadedCount, int TotalCount)>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cacheSavedCount = 0;
                var scanResult = ProjectScanner.ScanFolder(tempDir);

                var gateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pauseTested = 0;

                service.StartProgressiveIndexing(
                    manager,
                    syntheticFiles,
                    new[] { tempDir },
                    scanResult,
                    new List<ClassIDType> { ClassIDType.Sprite },
                    shouldPauseBackgroundWork: () => false,
                    waitPriorityAsync: async ct =>
                    {
                        if (Interlocked.CompareExchange(ref pauseTested, 1, 0) == 0)
                        {
                            await gateTcs.Task.WaitAsync(ct);
                        }
                    },
                    updateProgress: (_, _) => { },
                    onMemoryPressureError: _ => { },
                    onBatchLoaded: () => { },
                    saveCacheCallback: (_, _) => Interlocked.Increment(ref cacheSavedCount),
                    onIndexingProgressChanged: update => progressUpdates.Enqueue(update),
                    onFinished: (wasCancelled, loaded, total) => finishedTcs.TrySetResult((wasCancelled, loaded, total)));

                // Test PauseIndexing while workers are gated
                service.PauseIndexing();
                Assert.True(service.IsIndexingPaused);

                // Release gate while paused, verify workers stay paused
                gateTcs.TrySetResult(true);
                await Task.Delay(80);
                Assert.True(service.IsIndexingPaused);

                // Resume indexing and wait for full completion
                service.ResumeIndexing();
                Assert.False(service.IsIndexingPaused);

                var (cancelled, loadedCount, totalCount) = await finishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(cancelled);
                Assert.Equal(fileCount, loadedCount);
                Assert.Equal(fileCount, totalCount);
                Assert.Equal(1, cacheSavedCount);
                Assert.Equal(0, service.PendingCount);

                var updatesList = progressUpdates.ToArray();
                Assert.Contains(updatesList, u => u.Status == "paused");
                Assert.Contains(updatesList, u => u.Status == "running");
                Assert.Equal("completed", updatesList[^1].Status);
                Assert.Equal(fileCount, updatesList[^1].ProcessedFiles);
                Assert.Equal(0, updatesList[^1].PendingFiles);
                Assert.Equal(100.0, updatesList[^1].PercentComplete);

                var allNewlyRead = updatesList
                    .Where(u => u.NewlyReadFiles != null)
                    .SelectMany(u => u.NewlyReadFiles)
                    .ToList();
                Assert.Equal(fileCount, allNewlyRead.Count);
                Assert.Equal(
                    syntheticFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                    allNewlyRead.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

                // 2. Cancellation verification
                var cancelService = new AssetLoadingService();
                var cancelUpdates = new ConcurrentQueue<IndexingProgressUpdate>();
                var cancelFinishedTcs = new TaskCompletionSource<(bool WasCancelled, int LoadedCount, int TotalCount)>(TaskCreationOptions.RunContinuationsAsynchronously);

                cancelService.StartProgressiveIndexing(
                    manager,
                    syntheticFiles,
                    new[] { tempDir },
                    scanResult,
                    new List<ClassIDType>(),
                    shouldPauseBackgroundWork: () => false,
                    waitPriorityAsync: async ct =>
                    {
                        await Task.Delay(50, ct);
                    },
                    updateProgress: (_, _) => { },
                    onMemoryPressureError: _ => { },
                    onBatchLoaded: () => { },
                    saveCacheCallback: (_, _) => { },
                    onIndexingProgressChanged: update => cancelUpdates.Enqueue(update),
                    onFinished: (wasCancelled, loaded, total) => cancelFinishedTcs.TrySetResult((wasCancelled, loaded, total)));

                await Task.Delay(30);
                cancelService.StopIndexing();

                var cancelResult = await cancelFinishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(cancelResult.WasCancelled);
                Assert.True(cancelResult.LoadedCount < fileCount);
                var cancelUpdateList = cancelUpdates.ToArray();
                Assert.Contains(cancelUpdateList, u => u.Status == "cancelling");
                Assert.Equal("cancelled", cancelUpdateList[^1].Status);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void BuildSemanticRelationsForFilesBackground_ParallelAcrossMultipleFiles_Produces100PercentIdenticalRelationsToSequential()
        {
            const int fileCount = 24;
            using var assetsManager = new AssetsManager();
            var files = new List<SerializedFile>(fileCount);

            for (int f = 0; f < fileCount; f++)
            {
                files.Add(CreateRichSyntheticSerializedFile(assetsManager, f));
            }

            var sequentialRelations = new SemanticAssetRelations();
            var parallelRelations = new SemanticAssetRelations();

            var seqSw = Stopwatch.StartNew();
            MainWindow.BuildSemanticRelationsForFilesBackground(files, sequentialRelations, maxDegreeOfParallelism: 1);
            seqSw.Stop();

            var parSw = Stopwatch.StartNew();
            MainWindow.BuildSemanticRelationsForFilesBackground(files, parallelRelations, maxDegreeOfParallelism: 8);
            parSw.Stop();

            Assert.Equal(fileCount, sequentialRelations.SourceFiles.Count);
            Assert.True(sequentialRelations.AssetEdges.Count >= fileCount * 4);
            Assert.True(sequentialRelations.ModelGroups.Count >= fileCount);
            Assert.True(sequentialRelations.ModelGroupMeshes.Count >= fileCount);
            Assert.True(sequentialRelations.MeshRenderers.Count >= fileCount);
            Assert.True(sequentialRelations.MeshMaterials.Count >= fileCount);
            Assert.True(sequentialRelations.MaterialTextures.Count >= fileCount);
            Assert.True(sequentialRelations.RigBoneHashes.Count >= fileCount * 8);
            Assert.True(sequentialRelations.ClipBoneHashes.Count >= fileCount * 4);

            AssertSemanticRelationsIdentical(sequentialRelations, parallelRelations);

            _output.WriteLine(
                $"Synthetic {fileCount}-file semantic extraction parity verified: " +
                $"edges={parallelRelations.AssetEdges.Count}, meshMats={parallelRelations.MeshMaterials.Count}, " +
                $"matTexs={parallelRelations.MaterialTextures.Count}, groups={parallelRelations.ModelGroups.Count}, " +
                $"rigHashes={parallelRelations.RigBoneHashes.Count}, clipHashes={parallelRelations.ClipBoneHashes.Count} | " +
                $"seq={seqSw.Elapsed.TotalMilliseconds:0.##} ms, par={parSw.Elapsed.TotalMilliseconds:0.##} ms");
        }

        [SkippableFact]
        public void OthercideRealBundles_ParallelMultiFileSemanticExtraction_MatchesSequentialParityAndMeasuresSpeedup()
        {
            if (!Directory.Exists(OthercideBundlesDir))
            {
                _output.WriteLine($"Othercide directory not present at '{OthercideBundlesDir}'; skipping guarded read-only benchmark.");
                throw new SkipException("Local game files are not installed on this machine.");
            }

            var sampleBundles = Directory.GetFiles(OthercideBundlesDir, "*.bundle", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Length >= 512 * 1024 && fi.Length <= 15 * 1024 * 1024)
                .OrderByDescending(fi => fi.Length)
                .Take(20)
                .Select(fi => fi.FullName)
                .ToArray();

            if (sampleBundles.Length == 0)
            {
                return;
            }

            using var assetsManager = new AssetsManager
            {
                LazyLoading = false,
                ProjectRoot = OthercideBundlesDir
            };

            try
            {
                var loadSw = Stopwatch.StartNew();
                assetsManager.LoadFiles(sampleBundles);
                loadSw.Stop();

                var loadedFiles = assetsManager.assetsFileList.Where(f => f != null).ToList();
                Assert.NotEmpty(loadedFiles);

                // Warm up JIT once on first file
                var warmup = new SemanticAssetRelations();
                MainWindow.BuildSemanticRelationsForFilesBackground(new[] { loadedFiles[0] }, warmup, maxDegreeOfParallelism: 1);

                // 1. Legacy sequential single-file-at-a-time extraction loop (how BuildLazySemanticRelationsForSourcesAsync ran before)
                var sequentialRelations = new SemanticAssetRelations();
                var seqSw = Stopwatch.StartNew();
                for (int i = 0; i < loadedFiles.Count; i++)
                {
                    var singleFileRelations = new SemanticAssetRelations();
                    MainWindow.BuildSemanticRelationsForFilesBackground(new[] { loadedFiles[i] }, singleFileRelations, maxDegreeOfParallelism: 1);
                    sequentialRelations.Merge(singleFileRelations);
                }
                seqSw.Stop();

                // 2. Parallel multi-file batch extraction across CPU cores
                var parallelRelations = new SemanticAssetRelations();
                var parSw = Stopwatch.StartNew();
                MainWindow.BuildSemanticRelationsForFilesBackground(loadedFiles, parallelRelations);
                parSw.Stop();

                AssertSemanticRelationsIdentical(sequentialRelations, parallelRelations);

                var speedup = parSw.Elapsed.TotalMilliseconds > 0
                    ? seqSw.Elapsed.TotalMilliseconds / parSw.Elapsed.TotalMilliseconds
                    : 1.0;

                _output.WriteLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Othercide read-only benchmark ({0} bundles, {1} SerializedFiles, load={2:F1} ms): sequential={3:F2} ms vs parallel={4:F2} ms ({5:F2}x speedup) | edges={6}, meshMats={7}, matTexs={8}, groups={9}, animTargets={10}, rigHashes={11}, clipHashes={12}",
                        sampleBundles.Length,
                        loadedFiles.Count,
                        loadSw.Elapsed.TotalMilliseconds,
                        seqSw.Elapsed.TotalMilliseconds,
                        parSw.Elapsed.TotalMilliseconds,
                        speedup,
                        parallelRelations.AssetEdges.Count,
                        parallelRelations.MeshMaterials.Count,
                        parallelRelations.MaterialTextures.Count,
                        parallelRelations.ModelGroups.Count,
                        parallelRelations.AnimationClipTargets.Count,
                        parallelRelations.RigBoneHashes.Count,
                        parallelRelations.ClipBoneHashes.Count));
            }
            finally
            {
                assetsManager.Clear();
            }
        }

        private static SerializedFile CreateRichSyntheticSerializedFile(AssetsManager assetsManager, int fileIndex)
        {
            var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            typeof(SerializedFile)
                .GetField("objectCollectionLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(file, new object());
            file.assetsManager = assetsManager;
            file.fileName = $"synthetic_bundle_{fileIndex:D3}.assets";
            file.originalPath = $"C:\\SyntheticProject\\Bundles\\bundle_{fileIndex:D3}.bundle";
            file.fullName = file.originalPath;
            file.unityVersion = "2022.3.10f1";
            file.m_Objects = new List<ObjectInfo>();
            file.m_Externals = new List<FileIdentifier>();
            file.Objects = new List<AssetStudio.Object>();
            file.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            file.ObjectInfoDic = new Dictionary<long, ObjectInfo>();

            long basePathId = (fileIndex + 1L) * 10_000L;

            // 1. Root GameObject + Transform
            var rootGo = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            rootGo.assetsFile = file;
            rootGo.m_PathID = basePathId + 1;
            rootGo.type = ClassIDType.GameObject;
            rootGo.m_Name = $"CharacterRoot_{fileIndex:D3}";

            var rootTransform = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            rootTransform.assetsFile = file;
            rootTransform.m_PathID = basePathId + 2;
            rootTransform.type = ClassIDType.Transform;
            rootTransform.m_GameObject = new PPtr<GameObject>(0, rootGo.m_PathID, file);
            rootTransform.m_LocalPosition = new Vector3(0f, 0f, 0f);
            rootTransform.m_LocalRotation = new Quaternion(0f, 0f, 0f, 1f);
            rootTransform.m_LocalScale = new Vector3(1f, 1f, 1f);
            rootTransform.m_Father = new PPtr<Transform>(0, 0, file);
            rootTransform.m_Children = Array.Empty<PPtr<Transform>>();

            // 2. Mesh
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            mesh.assetsFile = file;
            mesh.m_PathID = basePathId + 3;
            mesh.type = ClassIDType.Mesh;
            mesh.m_Name = $"CharacterMesh_{fileIndex:D3}";
            mesh.m_BoneNameHashes = new uint[]
            {
                MainWindow.ComputeUnityBonePathHash("Root"),
                MainWindow.ComputeUnityBonePathHash("Root/Spine"),
                MainWindow.ComputeUnityBonePathHash("Root/Spine/Head"),
                MainWindow.ComputeUnityBonePathHash("Root/LeftArm")
            };

            // 3. Texture2D
            var texture = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            texture.assetsFile = file;
            texture.m_PathID = basePathId + 4;
            texture.type = ClassIDType.Texture2D;
            texture.m_Name = $"CharacterAlbedo_{fileIndex:D3}";
            texture.m_Width = 512;
            texture.m_Height = 512;

            // 4. Material referencing Texture2D
            var material = (Material)RuntimeHelpers.GetUninitializedObject(typeof(Material));
            material.assetsFile = file;
            material.m_PathID = basePathId + 5;
            material.type = ClassIDType.Material;
            material.m_Name = $"CharacterMat_{fileIndex:D3}";
            var texEnv = (UnityTexEnv)RuntimeHelpers.GetUninitializedObject(typeof(UnityTexEnv));
            texEnv.m_Texture = new PPtr<Texture>(0, texture.m_PathID, file);
            texEnv.m_Scale = new Vector2(1f, 1f);
            texEnv.m_Offset = new Vector2(0f, 0f);
            var savedProps = (UnityPropertySheet)RuntimeHelpers.GetUninitializedObject(typeof(UnityPropertySheet));
            savedProps.m_TexEnvs = new[] { new KeyValuePair<string, UnityTexEnv>("_MainTex", texEnv) };
            savedProps.m_Floats = Array.Empty<KeyValuePair<string, float>>();
            savedProps.m_Colors = Array.Empty<KeyValuePair<string, Color>>();
            material.m_SavedProperties = savedProps;

            // 5. SkinnedMeshRenderer linking RootGo, Mesh, and Material
            var smr = (SkinnedMeshRenderer)RuntimeHelpers.GetUninitializedObject(typeof(SkinnedMeshRenderer));
            smr.assetsFile = file;
            smr.m_PathID = basePathId + 6;
            smr.type = ClassIDType.SkinnedMeshRenderer;
            smr.m_GameObject = new PPtr<GameObject>(0, rootGo.m_PathID, file);
            smr.m_Mesh = new PPtr<Mesh>(0, mesh.m_PathID, file);
            smr.m_Materials = new[] { new PPtr<Material>(0, material.m_PathID, file) };
            smr.m_Bones = Array.Empty<PPtr<Transform>>();

            // 6. Avatar with skeleton & TOS bone hashes
            var avatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            avatar.assetsFile = file;
            avatar.m_PathID = basePathId + 7;
            avatar.type = ClassIDType.Avatar;
            avatar.m_Name = $"CharacterRoot_{fileIndex:D3}Avatar";
            var bonePaths = new[]
            {
                "Root",
                "Root/Spine",
                "Root/Spine/Head",
                "Root/LeftArm",
                "Root/RightArm"
            };
            var boneHashes = bonePaths.Select(MainWindow.ComputeUnityBonePathHash).ToArray();
            avatar.m_TOS = bonePaths
                .Select((p, idx) => new KeyValuePair<uint, string>(boneHashes[idx], p))
                .ToArray();
            var skeleton = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            skeleton.m_ID = boneHashes;
            skeleton.m_Node = Array.Empty<Node>();
            skeleton.m_AxesArray = Array.Empty<Axes>();
            var avatarConstant = (AvatarConstant)RuntimeHelpers.GetUninitializedObject(typeof(AvatarConstant));
            avatarConstant.m_AvatarSkeleton = skeleton;
            avatar.m_Avatar = avatarConstant;

            // 7. Animator referencing RootGo and Avatar
            var animator = (Animator)RuntimeHelpers.GetUninitializedObject(typeof(Animator));
            animator.assetsFile = file;
            animator.m_PathID = basePathId + 8;
            animator.type = ClassIDType.Animator;
            animator.m_GameObject = new PPtr<GameObject>(0, rootGo.m_PathID, file);
            animator.m_Avatar = new PPtr<Avatar>(0, avatar.m_PathID, file);
            animator.m_Controller = new PPtr<RuntimeAnimatorController>(0, 0, file);

            // 8. AnimationClip with transform bindings matching the rig bone hashes
            var clip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            clip.assetsFile = file;
            clip.m_PathID = basePathId + 9;
            clip.type = ClassIDType.AnimationClip;
            clip.m_Name = $"CharacterRun_{fileIndex:D3}";
            var clipBindingConstant = (AnimationClipBindingConstant)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClipBindingConstant));
            clipBindingConstant.genericBindings = boneHashes
                .Select(h => new GenericBinding
                {
                    path = h,
                    attribute = 1,
                    typeID = ClassIDType.Transform
                })
                .ToArray();
            clipBindingConstant.pptrCurveMapping = Array.Empty<PPtr<AssetStudio.Object>>();
            clip.m_ClipBindingConstant = clipBindingConstant;

            rootGo.m_Components = new[]
            {
                new PPtr<Component>(0, rootTransform.m_PathID, file),
                new PPtr<Component>(0, smr.m_PathID, file),
                new PPtr<Component>(0, animator.m_PathID, file)
            };
            rootGo.m_Transform = rootTransform;
            rootGo.m_SkinnedMeshRenderer = smr;
            rootGo.m_Animator = animator;

            RegisterSyntheticObject(file, assetsManager, rootGo);
            RegisterSyntheticObject(file, assetsManager, rootTransform);
            RegisterSyntheticObject(file, assetsManager, mesh);
            RegisterSyntheticObject(file, assetsManager, texture);
            RegisterSyntheticObject(file, assetsManager, material);
            RegisterSyntheticObject(file, assetsManager, smr);
            RegisterSyntheticObject(file, assetsManager, avatar);
            RegisterSyntheticObject(file, assetsManager, animator);
            RegisterSyntheticObject(file, assetsManager, clip);

            return file;
        }

        private static void RegisterSyntheticObject(SerializedFile file, AssetsManager assetsManager, AssetStudio.Object obj)
        {
            file.AddObject(obj);
            var info = new ObjectInfo
            {
                m_PathID = obj.m_PathID,
                classID = (int)obj.type,
                byteStart = 0,
                byteSize = 128
            };
            file.m_Objects.Add(info);
            file.ObjectInfoDic[obj.m_PathID] = info;

            var name = obj switch
            {
                NamedObject named => named.m_Name ?? obj.type.ToString(),
                GameObject go => go.m_Name ?? "GameObject",
                _ => obj.type.ToString()
            };

            var handle = new AssetHandle
            {
                UniqueID = AssetHandle.BuildUniqueID(file, obj.m_PathID),
                Name = name,
                Type = obj.type,
                OriginalPath = file.originalPath,
                SerializedFileName = file.fileName,
                PathID = obj.m_PathID,
                ByteStart = 0,
                ByteSize = 128,
                SourceFile = file,
                RealObject = obj
            };
            assetsManager.ProjectIndex.AddHandle(handle);
        }

        private static void AssertSemanticRelationsIdentical(SemanticAssetRelations expected, SemanticAssetRelations actual)
        {
            Assert.Equal(
                expected.SourceFiles.Select(FormatSourceFile),
                actual.SourceFiles.Select(FormatSourceFile));
            Assert.Equal(
                expected.AssetEdges.Select(FormatEdge),
                actual.AssetEdges.Select(FormatEdge));
            Assert.Equal(
                expected.ModelGroups.Select(FormatModelGroup),
                actual.ModelGroups.Select(FormatModelGroup));
            Assert.Equal(
                expected.ModelGroupMeshes.Select(FormatModelGroupMesh),
                actual.ModelGroupMeshes.Select(FormatModelGroupMesh));
            Assert.Equal(
                expected.MeshRenderers.Select(FormatMeshRenderer),
                actual.MeshRenderers.Select(FormatMeshRenderer));
            Assert.Equal(
                expected.MeshMaterials.Select(FormatMeshMaterial),
                actual.MeshMaterials.Select(FormatMeshMaterial));
            Assert.Equal(
                expected.MaterialTextures.Select(FormatMaterialTexture),
                actual.MaterialTextures.Select(FormatMaterialTexture));
            Assert.Equal(
                expected.AnimationClipTargets.Select(FormatAnimTarget),
                actual.AnimationClipTargets.Select(FormatAnimTarget));
            Assert.Equal(
                expected.RigBoneHashes.Select(FormatRigHash),
                actual.RigBoneHashes.Select(FormatRigHash));
            Assert.Equal(
                expected.ClipBoneHashes.Select(FormatClipHash),
                actual.ClipBoneHashes.Select(FormatClipHash));
        }

        private static string FormatSourceFile(SemanticSourceFileEntry s) =>
            $"{s.SerializedFileName}|{s.OriginalPath}|{s.UnityVersion}|{s.ObjectCount}";

        private static string FormatEdge(SemanticAssetEdgeRelation e) =>
            $"{e.SourceAssetId}|{e.EdgeKind}|{e.SlotName}|{e.SlotIndex}|{e.TargetAssetId}|{e.SourceFileId}|{e.SourcePathId}|{e.TargetFileId}|{e.TargetPathId}|{e.IsResolved}";

        private static string FormatModelGroup(SemanticModelGroupRelation g) =>
            $"{g.GroupId}|{g.GroupKind}|{g.GroupName}|{g.RootGameObjectAssetId}|{g.RootGameObjectName}|{g.AnimatorAssetId}|{g.AvatarAssetId}|{g.ControllerAssetId}|{g.SourceFileName}|{g.Confidence}|{g.ConfidenceReason}";

        private static string FormatModelGroupMesh(SemanticModelGroupMeshRelation m) =>
            $"{m.GroupId}|{m.MeshAssetId}|{m.RendererAssetId}|{m.RendererType}|{m.GameObjectAssetId}|{m.GameObjectName}|{m.SlotIndex}|{m.Confidence}|{m.ConfidenceReason}";

        private static string FormatMeshRenderer(SemanticMeshRendererRelation r) =>
            $"{r.MeshAssetId}|{r.RendererAssetId}|{r.RendererType}|{r.GameObjectAssetId}|{r.GameObjectName}|{r.Description}";

        private static string FormatMeshMaterial(SemanticMeshMaterialRelation m) =>
            $"{m.MeshAssetId}|{m.MaterialAssetId}|{m.RendererAssetId}|{m.RendererType}|{m.SubMeshIndex}|{m.MaterialSlotIndex}|{m.MaterialScore}";

        private static string FormatMaterialTexture(SemanticMaterialTextureRelation t) =>
            $"{t.MaterialAssetId}|{t.SlotName}|{t.SlotIndex}|{t.TextureFileId}|{t.TexturePathId}|{t.TextureAssetId}|{t.PreviewMaterialAssetId}|{t.IsResolved}|{t.IsMainTexture}";

        private static string FormatAnimTarget(SemanticAnimationClipTargetRelation a) =>
            $"{a.ClipAssetId}|{a.AnimatorAssetId}|{a.AvatarAssetId}|{a.MeshAssetId}|{a.ModelGroupId}|{a.RelationKind}|{a.MatchScore}|{a.MatchedBoneCount}|{a.TotalClipBoneCount}";

        private static string FormatRigHash(SemanticRigBoneHashEntry r) =>
            $"{r.AvatarAssetId}|{r.AnimatorAssetId}|{r.MeshAssetId}|{r.ModelGroupId}|{r.BoneHash}";

        private static string FormatClipHash(SemanticClipBoneHashEntry c) =>
            $"{c.ClipAssetId}|{c.BoneHash}|{c.TotalClipBoneCount}";

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
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
