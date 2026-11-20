using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using Object = AssetStudio.Object;

namespace AssetStudio.Tests
{
    public class AsyncPreviewResponsivenessTests
    {
        private readonly ITestOutputHelper _output;

        public AsyncPreviewResponsivenessTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void FormatPrefab_HashSetDeduplication_MatchesLegacyListParityAndRunsFastOn5000DuplicateReferences()
        {
            const int totalDuplicateRefs = 5000;
            const int hierarchyObjectCount = 600;
            const int uniqueExternalCount = 40;
            const int uniqueUnresolvedCount = 25;

            var hierarchyList = new List<Object>(hierarchyObjectCount);
            var excludedSet = new HashSet<Object>();
            for (int i = 0; i < hierarchyObjectCount; i++)
            {
                var go = CreateSyntheticObject<GameObject>(1000 + i, ClassIDType.GameObject);
                go.m_Name = $"Node_{i}";
                hierarchyList.Add(go);
                excludedSet.Add(go);
            }

            var externalPool = new Object[uniqueExternalCount];
            for (int i = 0; i < uniqueExternalCount; i++)
            {
                externalPool[i] = CreateSyntheticObject<Texture2D>(50000 + i, ClassIDType.Texture2D);
            }

            var resolvedCandidates = new List<Object?>(totalDuplicateRefs);
            var unresolvedCandidates = new List<string>(totalDuplicateRefs);
            for (int i = 0; i < totalDuplicateRefs; i++)
            {
                // Mix hierarchy objects (must be excluded), duplicates from externalPool, and nulls.
                if (i % 5 == 0)
                {
                    resolvedCandidates.Add(hierarchyList[i % hierarchyObjectCount]);
                }
                else if (i % 17 == 0)
                {
                    resolvedCandidates.Add(null);
                }
                else
                {
                    resolvedCandidates.Add(externalPool[i % uniqueExternalCount]);
                }

                unresolvedCandidates.Add($"FileID: 1, PathID: {900000 + (i % uniqueUnresolvedCount)}");
            }

            // Legacy O(N^2) reference implementation using List.Contains + Distinct
            var legacyResolved = new List<Object>();
            foreach (var candidate in resolvedCandidates)
            {
                if (candidate != null && !hierarchyList.Contains(candidate))
                {
                    legacyResolved.Add(candidate);
                }
            }
            var legacyDistinctResolved = legacyResolved.Distinct().ToList();
            var legacyDistinctUnresolved = unresolvedCandidates.Distinct().ToList();

            // New O(1) HashSet deduplication
            var sw = Stopwatch.StartNew();
            var (uniqueResolved, totalResolvedCount, uniqueUnresolved, totalUnresolvedCount) =
                MainWindow.DeduplicatePrefabReferences(resolvedCandidates, unresolvedCandidates, excludedSet);
            sw.Stop();

            _output.WriteLine($"DeduplicatePrefabReferences on {totalDuplicateRefs:N0} duplicate refs completed in {sw.Elapsed.TotalMilliseconds:0.###} ms.");

            Assert.Equal(legacyResolved.Count, totalResolvedCount);
            Assert.Equal(legacyDistinctResolved.Count, uniqueResolved.Count);
            for (int i = 0; i < legacyDistinctResolved.Count; i++)
            {
                Assert.Same(legacyDistinctResolved[i], uniqueResolved[i]);
            }

            Assert.Equal(unresolvedCandidates.Count, totalUnresolvedCount);
            Assert.Equal(legacyDistinctUnresolved, uniqueUnresolved);
            Assert.True(sw.Elapsed.TotalMilliseconds < 100.0, $"Expected O(1) HashSet deduplication under 100 ms, actual {sw.Elapsed.TotalMilliseconds:0.###} ms.");
        }

        [Fact]
        public void TraverseGameObjectHierarchy_IterativeHashSet_HandlesCyclesAndDuplicatesWithoutRevisit()
        {
            var file = CreateSyntheticSerializedFile("prefab.assets");

            var goA = CreateSyntheticGameObject(file, 1, "Root");
            var goB = CreateSyntheticGameObject(file, 2, "ChildB");
            var goC = CreateSyntheticGameObject(file, 3, "ChildC");

            var trA = CreateSyntheticTransform(file, 10, goA);
            var trB = CreateSyntheticTransform(file, 11, goB);
            var trC = CreateSyntheticTransform(file, 12, goC);

            // Include both duplicate child reference (B twice) and cycle back to root (C -> A)
            trA.m_Children = new[]
            {
                CreateResolvedPPtr(file, trB),
                CreateResolvedPPtr(file, trB),
                CreateResolvedPPtr(file, trC)
            };
            trB.m_Children = new[] { CreateResolvedPPtr(file, trC) };
            trC.m_Children = new[] { CreateResolvedPPtr(file, trA) };

            goA.m_Components = new[] { CreateResolvedPPtr<Component>(file, trA) };
            goB.m_Components = new[] { CreateResolvedPPtr<Component>(file, trB) };
            goC.m_Components = new[] { CreateResolvedPPtr<Component>(file, trC) };

            var gameObjects = new List<GameObject>();
            var visited = new HashSet<GameObject>();
            var components = new List<Component>();
            var hierarchyObjects = new HashSet<Object>();

            MainWindow.TraverseGameObjectHierarchy(goA, gameObjects, visited, components, hierarchyObjects);

            Assert.Equal(3, gameObjects.Count);
            Assert.Same(goA, gameObjects[0]);
            Assert.Same(goB, gameObjects[1]);
            Assert.Same(goC, gameObjects[2]);
            Assert.Equal(3, components.Count);
            Assert.Contains(goA, hierarchyObjects);
            Assert.Contains(trC, hierarchyObjects);
        }

        [Fact]
        public async Task TextAssetPreviewBuilder_OffThreadExecution_BuildsDialogueCardsAndViewModels()
        {
            var callerThreadId = Environment.CurrentManagedThreadId;
            var ammoBytes = BuildSyntheticAmmoEpisodeBytes(dialogueCount: 25);
            var assetItem = CreateSyntheticAssetItem("episode_01_dialogue");

            int workerThreadId = -1;
            var (preview, darkCards, lightCards) = await Task.Run(() =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                var result = TextAssetPreviewBuilder.BuildPreview(assetItem, ammoBytes, string.Empty);
                var dark = MainWindow.BuildTextAssetDialogueCardViewModels(result.DialogueCards, isDark: true);
                var light = MainWindow.BuildTextAssetDialogueCardViewModels(result.DialogueCards, isDark: false);
                return (result, dark, light);
            });

            Assert.NotEqual(callerThreadId, workerThreadId);
            Assert.True(preview.HasDialogueCards);
            Assert.Equal(25, preview.DialogueCards.Count);
            Assert.Equal(preview.DialogueCards.Count, darkCards.Length);
            Assert.Equal(preview.DialogueCards.Count, lightCards.Length);

            Assert.StartsWith("001 | ", darkCards[0].Title, StringComparison.Ordinal);
            Assert.StartsWith("0x", darkCards[0].OffsetText, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(darkCards[0].BodyText));
            Assert.Contains("bytes", darkCards[0].FooterText, StringComparison.Ordinal);
            Assert.NotEqual(darkCards[0].CardBackground, lightCards[0].CardBackground);
            Assert.NotEqual(darkCards[0].BodyForeground, lightCards[0].BodyForeground);

            // Verify cancellation is honored on large dialogue card batches
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                MainWindow.BuildTextAssetDialogueCardViewModels(preview.DialogueCards, isDark: true, cts.Token));
        }

        [Fact]
        public void MainWindow_TextAssetDialogueCardsList_UsesCompiledBindingsWithoutReflection()
        {
            var assembly = typeof(MainWindow).Assembly;
            var assemblyPath = assembly.Location;
            Assert.True(File.Exists(assemblyPath));

            var compiledXamlMethods = new List<MethodInfo>();
            compiledXamlMethods.AddRange(typeof(MainWindow).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name.Contains("XamlIl", StringComparison.Ordinal)));
            foreach (var type in assembly.GetTypes().Where(t => t.FullName?.Contains("XamlIl", StringComparison.Ordinal) == true))
            {
                compiledXamlMethods.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance));
            }

            var calledMembers = new HashSet<string>(StringComparer.Ordinal);
            var module = typeof(MainWindow).Module;
            foreach (var method in compiledXamlMethods)
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null || il.Length < 5)
                {
                    continue;
                }

                for (var i = 0; i <= il.Length - 4; i++)
                {
                    var table = il[i + 3];
                    if (table != 0x06 && table != 0x0A && table != 0x2B)
                    {
                        continue;
                    }

                    var token = BitConverter.ToInt32(il, i);
                    try
                    {
                        var resolved = module.ResolveMember(token);
                        if (resolved?.DeclaringType == typeof(TextAssetDialogueCardViewModel))
                        {
                            calledMembers.Add($"{resolved.DeclaringType.Name}.{resolved.Name}");
                        }
                    }
                    catch
                    {
                    }
                }
            }

            Assert.Contains("TextAssetDialogueCardViewModel.get_Title", calledMembers);
            Assert.Contains("TextAssetDialogueCardViewModel.get_OffsetText", calledMembers);
            Assert.Contains("TextAssetDialogueCardViewModel.get_BodyText", calledMembers);
            Assert.Contains("TextAssetDialogueCardViewModel.get_FooterText", calledMembers);
            Assert.Contains("TextAssetDialogueCardViewModel.get_CardBackground", calledMembers);
            Assert.Contains("TextAssetDialogueCardViewModel.get_CardBorderBrush", calledMembers);
        }

        [Fact]
        public void PrepareGpuTexturePayload_HandlesCompressedPassThrough_CrunchUnpack_AndAstcCpuFallback()
        {
            var desktopWithS3tcNoAstc = TextureGLPreviewControl.GlCapabilities.FromVersionAndExtensions(
                isOpenGles: false,
                majorVersion: 4,
                minorVersion: 3,
                extensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GL_EXT_texture_compression_s3tc" });

            var gles32WithAstc = TextureGLPreviewControl.GlCapabilities.FromVersionAndExtensions(
                isOpenGles: true,
                majorVersion: 3,
                minorVersion: 2,
                extensions: null);

            // 1) Compressed pass-through: DXT1 on Desktop GL with S3TC -> zero CPU decode
            var dxt1Raw = new byte[TextureGLPreviewControl.GetMip0Size(TextureFormat.DXT1, 8, 8)];
            new Random(101).NextBytes(dxt1Raw);
            var dxt1Tex = CreateSyntheticTexture(8, 8, TextureFormat.DXT1, dxt1Raw, pathId: 1001);

            var dxt1Payload = TextureGLPreviewControl.PrepareGpuTexturePayload(dxt1Tex, desktopWithS3tcNoAstc);
            Assert.Equal(PreparedGpuTextureUploadKind.Compressed, dxt1Payload.UploadKind);
            Assert.Equal(0x83F0, dxt1Payload.GlInternalFormat);
            Assert.Equal(dxt1Raw.Length, dxt1Payload.DataLength);
            Assert.False(dxt1Payload.WasCpuDecoded);
            Assert.False(dxt1Payload.WasCrunchUnpacked);

            // 2) Crunch unpack path: DXT1Crunched -> unpacked DXT1 GPU compressed upload when S3TC supported
            var crunchBlob = new byte[] { 0x48, 0x78, 0x01, 0x02 };
            var crunchedTex = CreateSyntheticTexture(8, 8, TextureFormat.DXT1Crunched, crunchBlob, pathId: 1002);
            var crunchedPayload = TextureGLPreviewControl.PrepareGpuTexturePayload(
                crunchedTex,
                desktopWithS3tcNoAstc,
                crunchUnpacker: (_, fmt, _) => fmt == TextureFormat.DXT1Crunched ? dxt1Raw : null);

            Assert.Equal(PreparedGpuTextureUploadKind.Compressed, crunchedPayload.UploadKind);
            Assert.Equal(TextureFormat.DXT1, crunchedPayload.UploadFormat);
            Assert.Equal(0x83F0, crunchedPayload.GlInternalFormat);
            Assert.True(crunchedPayload.WasCrunchUnpacked);
            Assert.False(crunchedPayload.WasCpuDecoded);
            Assert.Same(dxt1Raw, crunchedPayload.Data);

            // 3) ASTC CPU fallback when SupportsAstcLdr == false (Windows Desktop GL)
            var astcRaw = new byte[TextureGLPreviewControl.GetMip0Size(TextureFormat.ASTC_RGBA_4x4, 8, 8)];
            new Random(202).NextBytes(astcRaw);
            for (int i = 0; i < astcRaw.Length; i += 16)
            {
                astcRaw[i] = 0x45;
                astcRaw[i + 1] = 0x00;
            }
            var astcTex = CreateSyntheticTexture(8, 8, TextureFormat.ASTC_RGBA_4x4, astcRaw, pathId: 1003);

            var astcDesktopFallback = TextureGLPreviewControl.PrepareGpuTexturePayload(astcTex, desktopWithS3tcNoAstc);
            Assert.Equal(PreparedGpuTextureUploadKind.Bgra32, astcDesktopFallback.UploadKind);
            Assert.True(astcDesktopFallback.WasCpuDecoded);
            Assert.Equal(8 * 8 * 4, astcDesktopFallback.DataLength);

            var expectedBgra = new byte[8 * 8 * 4];
            Assert.True(new Texture2DConverter(astcTex).DecodeTexture2D(expectedBgra));
            Assert.Equal(expectedBgra, astcDesktopFallback.Data);

            // 4) Same ASTC texture on GLES 3.2 (SupportsAstcLdr == true) -> native compressed pass-through!
            var astcGlesNative = TextureGLPreviewControl.PrepareGpuTexturePayload(astcTex, gles32WithAstc);
            Assert.Equal(PreparedGpuTextureUploadKind.Compressed, astcGlesNative.UploadKind);
            Assert.Equal(0x93B0, astcGlesNative.GlInternalFormat);
            Assert.False(astcGlesNative.WasCpuDecoded);

            // 5) GLES BGRA -> RGBA background swizzle (SupportsBgraUpload == false)
            var bgraRaw = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                bgraRaw[i * 4 + 0] = (byte)(10 + i);  // B
                bgraRaw[i * 4 + 1] = (byte)(50 + i);  // G
                bgraRaw[i * 4 + 2] = (byte)(100 + i); // R
                bgraRaw[i * 4 + 3] = 255;             // A
            }
            var bgraTex = CreateSyntheticTexture(4, 4, TextureFormat.BGRA32, bgraRaw, pathId: 1004);
            var glesSwizzled = TextureGLPreviewControl.PrepareGpuTexturePayload(bgraTex, gles32WithAstc);
            Assert.Equal(PreparedGpuTextureUploadKind.Rgba32, glesSwizzled.UploadKind);
            Assert.Equal(100, glesSwizzled.Data[0]); // R
            Assert.Equal(50, glesSwizzled.Data[1]);  // G
            Assert.Equal(10, glesSwizzled.Data[2]);  // B
            Assert.Equal(255, glesSwizzled.Data[3]); // A

            // 6) PrepareGpuImagePayload for Sprite SKBitmap (Y-flip true on both Desktop GL and GLES)
            using var spriteBmp = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            spriteBmp.Erase(new SKColor(120, 60, 30, 255));
            var spriteDesktop = TextureGLPreviewControl.PrepareGpuImagePayload(spriteBmp, desktopWithS3tcNoAstc);
            Assert.Equal(PreparedGpuTextureUploadKind.Bgra32, spriteDesktop.UploadKind);
            Assert.True(spriteDesktop.NeedsYFlip);
            Assert.Equal(30, spriteDesktop.Data[0]);  // B
            Assert.Equal(60, spriteDesktop.Data[1]);  // G
            Assert.Equal(120, spriteDesktop.Data[2]); // R

            var spriteGles = TextureGLPreviewControl.PrepareGpuImagePayload(spriteBmp, gles32WithAstc);
            Assert.Equal(PreparedGpuTextureUploadKind.Rgba32, spriteGles.UploadKind);
            Assert.True(spriteGles.NeedsYFlip);
            Assert.Equal(120, spriteGles.Data[0]); // R
            Assert.Equal(60, spriteGles.Data[1]);  // G
            Assert.Equal(30, spriteGles.Data[2]);  // B

            // 7) Cancellation honored before decoding
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                TextureGLPreviewControl.PrepareGpuTexturePayload(astcTex, desktopWithS3tcNoAstc, cancelled.Token));
        }

        [Fact]
        public async Task AsyncPreviewRequestCoordinator_Rapid20RequestBurst_CancelsStaleWorkAndLatestWins()
        {
            using var coordinator = new AsyncPreviewRequestCoordinator();
            var applied = new ConcurrentQueue<(long RequestId, string Kind)>();
            var startedCount = 0;

            const int burstCount = 20;
            var previewKinds = new[] { "Shader", "MonoBehaviour", "Animator", "TextAsset", "PrefabInstance", "DefaultDump" };
            var tasks = new Task<(bool Applied, long RequestId, string? Result)>[burstCount];

            for (int i = 0; i < burstCount; i++)
            {
                int index = i;
                bool isLast = index == burstCount - 1;
                string kind = $"{previewKinds[index % previewKinds.Length]}_{index:D2}";

                tasks[index] = coordinator.RunLatestAsync<string>(
                    async (_, ct) =>
                    {
                        Interlocked.Increment(ref startedCount);
                        if (!isLast)
                        {
                            await Task.Delay(600, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(15, ct).ConfigureAwait(false);
                        }

                        ct.ThrowIfCancellationRequested();
                        return kind;
                    },
                    (reqId, result) => applied.Enqueue((reqId, result)));

                if (!isLast)
                {
                    await Task.Delay(3);
                }
            }

            var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(startedCount >= 1);
            Assert.True(coordinator.CancelledRequestCount >= burstCount - 1);

            var winner = Assert.Single(applied);
            Assert.Equal(coordinator.CurrentRequestId, winner.RequestId);
            Assert.Equal($"{previewKinds[(burstCount - 1) % previewKinds.Length]}_{burstCount - 1:D2}", winner.Kind);
            Assert.True(outcomes[^1].Applied);
            Assert.All(outcomes.Take(burstCount - 1), r => Assert.False(r.Applied));

            // TruncatePreviewText enforces 100,000-character preview cap cleanly off UI thread
            Assert.Equal("fallback", MainWindow.TruncatePreviewText(null, "fallback"));
            var exact = new string('A', 100000);
            Assert.Same(exact, MainWindow.TruncatePreviewText(exact));
            var oversized = new string('B', 100050);
            var truncated = MainWindow.TruncatePreviewText(oversized);
            Assert.StartsWith(new string('B', 100), truncated, StringComparison.Ordinal);
            Assert.Contains("Preview truncated", truncated, StringComparison.Ordinal);
        }

        [Fact]
        public void MeshPreviewControl_LazyWireframeIndexGeneration_ProducesExactEdgePairsWithoutListReallocations()
        {
            // 1) Lazy creation gating
            Assert.False(MeshPreviewControl.ShouldCreateWireframeVao(avatarMode: false, currentWireframeMode: 0));
            Assert.True(MeshPreviewControl.ShouldCreateWireframeVao(avatarMode: false, currentWireframeMode: 1));
            Assert.True(MeshPreviewControl.ShouldCreateWireframeVao(avatarMode: false, currentWireframeMode: 2));
            Assert.True(MeshPreviewControl.ShouldCreateWireframeVao(avatarMode: true, currentWireframeMode: 0));

            // 2) Empty & incomplete triangle edge cases
            Assert.Empty(MeshPreviewControl.BuildWireframeLineIndices(ReadOnlySpan<int>.Empty));
            Assert.Empty(MeshPreviewControl.BuildWireframeLineIndices(new[] { 0, 1 }));

            // 3) Single triangle -> exact edge pairs 0-1, 1-2, 2-0
            Assert.Equal(
                new[] { 0, 1, 1, 2, 2, 0 },
                MeshPreviewControl.BuildWireframeLineIndices(new[] { 0, 1, 2 }));

            // 4) Multiple triangles + trailing incomplete indices ignored
            Assert.Equal(
                new[] { 0, 1, 1, 2, 2, 0, 3, 4, 4, 5, 5, 3 },
                MeshPreviewControl.BuildWireframeLineIndices(new[] { 0, 1, 2, 3, 4, 5, 99, 100 }));

            // 5) 100,000-triangle benchmark & exact single-array allocation check (zero List<int> growth)
            const int triangleCount = 100_000;
            var meshIndices = new int[triangleCount * 3];
            for (int t = 0; t < triangleCount; t++)
            {
                meshIndices[t * 3] = t;
                meshIndices[t * 3 + 1] = t + 1;
                meshIndices[t * 3 + 2] = t + 2;
            }

            // Warm up JIT
            _ = MeshPreviewControl.BuildWireframeLineIndices(meshIndices.AsSpan(0, 300));

            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var wireframeIndices = MeshPreviewControl.BuildWireframeLineIndices(meshIndices);
            sw.Stop();
            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

            long expectedArrayBytes = (long)triangleCount * 6 * sizeof(int);
            long actualAlloc = afterAlloc - beforeAlloc;
            _output.WriteLine($"BuildWireframeLineIndices for {triangleCount:N0} triangles: {sw.Elapsed.TotalMilliseconds:0.###} ms, allocated {actualAlloc:N0} B (payload {expectedArrayBytes:N0} B).");

            Assert.Equal(triangleCount * 6, wireframeIndices.Length);
            Assert.Equal(0, wireframeIndices[0]);
            Assert.Equal(1, wireframeIndices[1]);
            Assert.Equal(1, wireframeIndices[2]);
            Assert.Equal(2, wireframeIndices[3]);
            Assert.Equal(2, wireframeIndices[4]);
            Assert.Equal(0, wireframeIndices[5]);
            Assert.Equal(triangleCount - 1, wireframeIndices[^6]);
            Assert.Equal(triangleCount, wireframeIndices[^5]);
            Assert.Equal(triangleCount + 1, wireframeIndices[^2]);
            Assert.Equal(triangleCount - 1, wireframeIndices[^1]);

            // Allow only tiny object-header / alignment overhead (< 1 KB) beyond the single exact int[] allocation.
            Assert.True(actualAlloc <= expectedArrayBytes + 1024,
                $"Expected single pre-sized int[] allocation (~{expectedArrayBytes:N0} B), actual {actualAlloc:N0} B.");
        }

        private static T CreateSyntheticObject<T>(long pathId, ClassIDType type) where T : Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            obj.m_PathID = pathId;
            obj.type = type;
            return obj;
        }

        private static SerializedFile CreateSyntheticSerializedFile(string fileName)
        {
            var manager = new AssetsManager();
            var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            typeof(SerializedFile)
                .GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(file, new object());
            file.assetsManager = manager;
            file.fileName = fileName;
            file.fullName = fileName;
            file.m_Externals = new List<FileIdentifier>();
            file.Objects = new List<Object>();
            file.ObjectsDic = new Dictionary<long, Object>();
            file.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            manager.assetsFileList.Add(file);
            return file;
        }

        private static GameObject CreateSyntheticGameObject(SerializedFile file, long pathId, string name)
        {
            var go = CreateSyntheticObject<GameObject>(pathId, ClassIDType.GameObject);
            go.assetsFile = file;
            go.m_Name = name;
            file.Objects.Add(go);
            file.ObjectsDic[pathId] = go;
            return go;
        }

        private static Transform CreateSyntheticTransform(SerializedFile file, long pathId, GameObject owner)
        {
            var tr = CreateSyntheticObject<Transform>(pathId, ClassIDType.Transform);
            tr.assetsFile = file;
            tr.m_GameObject = CreateResolvedPPtr(file, owner);
            file.Objects.Add(tr);
            file.ObjectsDic[pathId] = tr;
            return tr;
        }

        private static PPtr<T> CreateResolvedPPtr<T>(SerializedFile file, T target) where T : Object
        {
            var pptr = new PPtr<T>(0, target.m_PathID, file);
            pptr.Set(target);
            return pptr;
        }

        private static AssetItem CreateSyntheticAssetItem(string name)
        {
            var item = (AssetItem)RuntimeHelpers.GetUninitializedObject(typeof(AssetItem));
            item.Name = name;
            item.TypeString = nameof(ClassIDType.TextAsset);
            item.Type = ClassIDType.TextAsset;
            return item;
        }

        private static Texture2D CreateSyntheticTexture(int width, int height, TextureFormat format, byte[] rawBytes, long pathId)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.m_Name = $"Synthetic_{format}_{width}x{height}";
            tex.m_PathID = pathId;
            tex.version = new[] { 2022, 3, 10, 1 };
            tex.platform = BuildTarget.StandaloneWindows64;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = format;
            tex.image_data = new ResourceReader(rawBytes);
            return tex;
        }

        private static byte[] BuildSyntheticAmmoEpisodeBytes(int dialogueCount)
        {
            var strings = new List<string>
            {
                "Ammo.DialogueEpisodeCommand",
                "Ammo.CameraEpisodeCommand",
                "Ammo.SoundEpisodeCommand",
                "DialogueText",
                "SpeakerName",
                "Heroine",
                "Narrator",
                "SceneIntro",
                "Chapter01",
                "TransitionFade",
                "EmotionHappy",
                "EmotionSad",
                "VoiceClip01",
                "BackgroundRoom",
                "MusicTheme01",
                "EndMarker"
            };

            for (int i = 0; i < dialogueCount; i++)
            {
                strings.Add($"{{\"Messages\":[\"Line {i + 1:D2}: Welcome back to the sanctuary!\"]}}");
            }

            using var ms = new MemoryStream();
            ms.WriteByte(0x00);
            ms.WriteByte(0x00);
            ms.WriteByte(0x0A); // String table marker
            ms.WriteByte((byte)(strings.Count & 0xFF));
            ms.WriteByte((byte)((strings.Count >> 8) & 0xFF));

            foreach (var str in strings)
            {
                var utf8 = Encoding.UTF8.GetBytes(str);
                ms.WriteByte(0x0C); // String entry marker
                ms.WriteByte((byte)(utf8.Length & 0xFF));
                ms.WriteByte((byte)((utf8.Length >> 8) & 0xFF));
                ms.Write(utf8, 0, utf8.Length);
            }

            return ms.ToArray();
        }
    }
}
