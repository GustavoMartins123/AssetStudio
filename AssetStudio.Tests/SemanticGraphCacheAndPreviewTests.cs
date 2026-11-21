using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public class SemanticGraphCacheAndPreviewTests
    {
        private readonly ITestOutputHelper _output;

        public SemanticGraphCacheAndPreviewTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void SchemaV14ToV15_InPlaceUpgrade_PreservesAllTablesAndStatus_CleansEmptyAnimationTargets_AndBackfillsDerivedCaches()
        {
            var tempRoot = CreateTempDirectory("v14_to_v15_upgrade");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "GameData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                var sourcePath = Path.Combine(projectDir, "level0.assets");
                File.WriteAllText(sourcePath, "dummy");

                var scanResult = ProjectScanner.ScanFolder(projectDir);

                string signature;
                string dbPath;
                using (var helperCache = new SQLiteProjectIndexCache(cacheDir))
                {
                    signature = helperCache.GetFolderSignature(scanResult);
                    dbPath = helperCache.GetDbPath(projectDir);
                }

                // Construct a genuine v14 SQLite database on disk (without MaterialCache/MeshMaterialResolvedCache/CompositeAssets/CompositeAssetParts
                // and without MaterialTextures ScaleX/Y/OffsetX/Y/ResolutionSource), containing valid v14 data + empty ghost AnimationClipTargets rows.
                SeedSyntheticV14Database(dbPath, projectDir, signature, sourcePath);

                // Open with v15 SQLiteProjectIndexCache -> triggers in-place UpgradeV14ToV15InPlace.
                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sw = Stopwatch.StartNew();
                var loadedHandles = cache.LoadIndexCache(projectDir, signature);
                sw.Stop();

                Assert.NotNull(loadedHandles);
                Assert.True(loadedHandles!.Count >= 5);
                Assert.True(sw.ElapsedMilliseconds < 5000, $"v14->v15 upgrade took {sw.ElapsedMilliseconds}ms");

                // Indexing state MUST remain 'structure_completed' (not reset!).
                var state = cache.LoadIndexingState(projectDir, signature);
                Assert.NotNull(state);
                Assert.Equal("structure_completed", state!.Status);
                Assert.True(cache.CanUseSemanticRelationCache(projectDir, signature));
                Assert.True(cache.HasCompletedLazyStructureStatus(projectDir, signature));

                // PreviewCacheEntries MUST be preserved intact.
                var preview = cache.LoadPreviewCacheEntry(projectDir, signature, "tex_main", "texture-thumbnail-png", 1, "max=512|w=64");
                Assert.NotNull(preview);
                Assert.Equal("hash_v14_preserved", preview!.PayloadHash);

                // Verify schema version bumped to 15 and new v15 tables + columns + index exist.
                using (var conn = cache.CreateReadConnection(projectDir))
                {
                    Assert.Equal("15", ExecuteScalarString(conn, "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;"));
                    Assert.Equal(0L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AnimationClipTargets WHERE AvatarAssetUniqueID = '' AND MeshAssetUniqueID = '' AND ModelGroupId = '';"));
                    Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AnimationClipTargets;"));
                    Assert.True(ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_model_groups_animator';") == 1);

                    var matTexCols = GetColumnNames(conn, "MaterialTextures");
                    Assert.Contains("ScaleX", matTexCols);
                    Assert.Contains("ScaleY", matTexCols);
                    Assert.Contains("OffsetX", matTexCols);
                    Assert.Contains("OffsetY", matTexCols);
                    Assert.Contains("ResolutionSource", matTexCols);
                }

                // Verify derived v15 tables were backfilled automatically from v14 data!
                var matCache = cache.LoadMaterialCacheEntry(projectDir, signature, "mat_hero");
                Assert.NotNull(matCache);
                Assert.Equal("tex_main", matCache!.MainTextureAssetId);
                Assert.Equal("_MainTex", matCache.MainTextureSlotName);

                var resolvedMeshMats = cache.LoadResolvedMeshMaterials(projectDir, signature, "mesh_hero");
                Assert.Single(resolvedMeshMats);
                Assert.Equal("mat_hero", resolvedMeshMats[0].MaterialAssetId);
                Assert.Equal("tex_main", resolvedMeshMats[0].MainTextureAssetId);

                var composite = cache.LoadCompositeAssetWithParts(projectDir, signature, "mesh_hero");
                Assert.NotNull(composite);
                Assert.Equal("modelgroup:animator:anim_hero", composite!.CompositeAssetId);
                Assert.Equal("Animator", composite.CompositeKind);
                Assert.Equal(2, composite.Parts.Count);
                Assert.Contains("mesh_hero", composite.DistinctMeshIds);
                Assert.Contains("mat_hero", composite.DistinctMaterialIds);
                Assert.Contains("tex_main", composite.DistinctTextureIds);

                // AnimationClipTargets returns only valid non-empty targets.
                var targets = cache.LoadAnimationClipTargets(projectDir, signature, "clip_attack");
                Assert.Single(targets);
                Assert.Equal("avatar_hero", targets[0].AvatarAssetId);
                Assert.Equal("mesh_hero", targets[0].MeshAssetId);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SemanticStagingSession_Commit_With5000ModelGroupsAndEdgeSynthesis_IsFast_HasNoEmptyClipTargets_AndUsesZeroPhysicalScans()
        {
            var tempRoot = CreateTempDirectory("staging_commit_5k_perf");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "GameData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                var sourcePath = Path.Combine(projectDir, "sharedassets0.assets");
                File.WriteAllText(sourcePath, "dummy");

                var scanResult = ProjectScanner.ScanFolder(projectDir);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig = cache.GetFolderSignature(scanResult);

                const int groupCount = 5000;
                var handles = new List<AssetHandle>(capacity: 200);
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 1, ClassIDType.Mesh, "HeroMesh", "mesh_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 2, ClassIDType.Material, "HeroMat", "mat_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 3, ClassIDType.Texture2D, "HeroTex", "tex_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 4, ClassIDType.Avatar, "HeroAvatar", "avatar_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 5, ClassIDType.AnimationClip, "HeroAttack", "clip_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 6, ClassIDType.Animator, "HeroAnimator", "anim_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 7, ClassIDType.AnimatorController, "HeroCtrl", "ctrl_1"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 8, ClassIDType.Animator, "EmptyAnimator", "anim_empty"));
                handles.Add(CreateHandle("sharedassets0.assets", sourcePath, 9, ClassIDType.AnimationClip, "GhostClip", "clip_ghost"));

                Assert.True(cache.SaveIndexCache(projectDir, sig, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                var progressTicks = new List<(int Processed, int Total, string Stage)>();
                var sw = Stopwatch.StartNew();
                using (var session = cache.BeginSemanticStagingSession(projectDir, sig))
                {
                    var batch = new SemanticAssetRelations();
                    batch.SourceFiles.Add(new SemanticSourceFileEntry("sharedassets0.assets", sourcePath, "2022.3.10f1", 5000));
                    batch.MaterialTextures.Add(new SemanticMaterialTextureRelation("mat_1", "mat_1", "_BaseMap", 0, "tex_1", 0, 3, true, true, 2f, 2f, 0.25f, 0.5f, "preparation"));
                    batch.MaterialCaches.Add(new SemanticMaterialCacheEntry("mat_1", "HeroMat", "", "tex_1", "_BaseMap", 0.9f, 0.8f, 0.7f, 1f, "Standard", 1, "preparation"));
                    batch.MeshRenderers.Add(new SemanticMeshRendererRelation("mesh_1", "ren_1", "SkinnedMeshRenderer", "go_1", "HeroGO", "HeroSMR"));
                    batch.MeshMaterials.Add(new SemanticMeshMaterialRelation("mesh_1", "mat_1", "ren_1", "SkinnedMeshRenderer", 0, 0, 10));

                    // Controller -> Clip edge and Animator -> Controller / Avatar / Mesh edges
                    batch.AssetEdges.Add(new SemanticAssetEdgeRelation("ctrl_1", "AnimationClip", "m_AnimationClips", 0, "clip_1", 0, 7, 0, 5, true));
                    batch.AssetEdges.Add(new SemanticAssetEdgeRelation("anim_1", "AnimatorController", "m_Controller", 0, "ctrl_1", 0, 6, 0, 7, true));
                    batch.AssetEdges.Add(new SemanticAssetEdgeRelation("anim_1", "Avatar", "m_Avatar", 0, "avatar_1", 0, 6, 0, 4, true));
                    batch.AssetEdges.Add(new SemanticAssetEdgeRelation("anim_1", "Mesh", "AnimatorMesh", 0, "mesh_1", 0, 6, 0, 1, true));

                    // Ghost Animator -> Controller -> GhostClip with NO avatar, NO mesh, NO group (must NEVER create empty AnimationClipTargets row!)
                    batch.AssetEdges.Add(new SemanticAssetEdgeRelation("ctrl_ghost", "AnimationClip", "m_AnimationClips", 0, "clip_ghost", 0, 70, 0, 9, true));
                    batch.AssetEdges.Add(new SemanticAssetEdgeRelation("anim_empty", "AnimatorController", "m_Controller", 0, "ctrl_ghost", 0, 8, 0, 70, true));

                    // Direct empty row attempt (must also be filtered out!)
                    batch.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation("clip_ghost", "anim_empty", "", "", "", "AnimatorController", 9999));

                    // 5,000 ModelGroups to stress the previously catastrophic OR-join!
                    for (int i = 0; i < groupCount; i++)
                    {
                        var animId = i == 0 ? "anim_1" : $"anim_bulk_{i}";
                        var avId = i == 0 ? "avatar_1" : $"avatar_bulk_{i}";
                        var gId = $"modelgroup:animator:{animId}";
                        batch.ModelGroups.Add(new SemanticModelGroupRelation(gId, "Animator", $"Group_{i}", $"go_{i}", $"GO_{i}", animId, avId, i == 0 ? "ctrl_1" : "", "sharedassets0.assets", 100, "descendant"));
                        batch.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(gId, i == 0 ? "mesh_1" : $"mesh_bulk_{i}", $"ren_{i}", "SkinnedMeshRenderer", $"go_{i}", $"GO_{i}", 0, null, 100, "descendant"));
                    }

                    session.FlushBatch(batch, clearBatch: true);
                    Assert.Equal(0, SemanticGraphService.CountBufferedSemanticRelationRows(batch));

                    var committed = session.Commit((processed, total, stage) => progressTicks.Add((processed, total, stage)));
                    Assert.True(committed);
                }
                sw.Stop();

                _output.WriteLine($"5,000-ModelGroup Commit() completed in {sw.ElapsedMilliseconds} ms ({progressTicks.Count} progress ticks)");
                Assert.True(sw.ElapsedMilliseconds < 10_000, $"Commit took too long: {sw.ElapsedMilliseconds}ms");
                Assert.True(progressTicks.Count >= 10);
                Assert.Equal(progressTicks[^1].Total, progressTicks[^1].Processed);

                // Verify synthesized AnimationClipTargets for clip_1 and ZERO empty rows for clip_ghost!
                var clip1Targets = cache.LoadAnimationClipTargets(projectDir, sig, "clip_1");
                Assert.NotEmpty(clip1Targets);
                Assert.Equal("avatar_1", clip1Targets[0].AvatarAssetId);
                Assert.Equal("mesh_1", clip1Targets[0].MeshAssetId);
                Assert.Equal("modelgroup:animator:anim_1", clip1Targets[0].ModelGroupId);

                Assert.Empty(cache.LoadAnimationClipTargets(projectDir, sig, "clip_ghost"));

                // Verify batch LoadModelGroupMeshesByGroupIds
                var batchedGroups = cache.LoadModelGroupMeshesByGroupIds(projectDir, sig, new[] { "modelgroup:animator:anim_1", "modelgroup:animator:anim_bulk_42" });
                Assert.Equal(2, batchedGroups.Count);
                Assert.Single(batchedGroups["modelgroup:animator:anim_1"]);
                Assert.Equal("mesh_1", batchedGroups["modelgroup:animator:anim_1"][0].MeshAssetId);

                // Verify MaterialTextures UV scale/offset preserved
                var slots = cache.LoadMaterialTextureSlots(projectDir, sig, "mat_1");
                Assert.Single(slots);
                Assert.Equal(2f, slots[0].ScaleX);
                Assert.Equal(0.25f, slots[0].OffsetX);
                Assert.Equal(0.5f, slots[0].OffsetY);
                Assert.Equal("preparation", slots[0].ResolutionSource);

                // Verify MaterialCache base color preserved
                var matEntry = cache.LoadMaterialCacheEntry(projectDir, sig, "mat_1");
                Assert.NotNull(matEntry);
                Assert.Equal(0.9f, matEntry!.BaseColorR, precision: 3);
                Assert.Equal(0.8f, matEntry.BaseColorG, precision: 3);
                Assert.Equal(0.7f, matEntry.BaseColorB, precision: 3);
                Assert.Equal("Standard", matEntry.ShaderName);

                // Verify 0 physical table SCANs on core v15 queries
                using var conn = cache.CreateReadConnection(projectDir);
                Assert.Equal(0L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AnimationClipTargets WHERE AvatarAssetUniqueID = '' AND MeshAssetUniqueID = '' AND ModelGroupId = '';"));
                Assert.True(cache.TryGetCachedProjectId(projectDir, sig, out var projectId));

                AssertZeroPhysicalTableScans(conn, "SELECT * FROM MeshMaterialResolvedCache WHERE ProjectId = @p AND MeshAssetUniqueID = 'mesh_1' ORDER BY SubMeshIndex", projectId);
                AssertZeroPhysicalTableScans(conn, "SELECT * FROM MaterialCache WHERE ProjectId = @p AND MaterialAssetUniqueID = 'mat_1'", projectId);
                AssertZeroPhysicalTableScans(conn, "SELECT * FROM CompositeAssetParts WHERE ProjectId = @p AND MeshAssetUniqueID = 'mesh_1'", projectId);
                AssertZeroPhysicalTableScans(conn, "SELECT * FROM ModelGroups WHERE ProjectId = @p AND AnimatorAssetUniqueID = 'anim_1'", projectId);
                AssertZeroPhysicalTableScans(conn, "SELECT * FROM AnimationClipTargets WHERE ProjectId = @p AND ClipAssetUniqueID = 'clip_1'", projectId);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SemanticGraphService_IsCompatibleSemanticClassId_HandlesUnityBaseClassHierarchies()
        {
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.AnimatorController, ClassIDType.RuntimeAnimatorController));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.AnimatorOverrideController, ClassIDType.RuntimeAnimatorController));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.RuntimeAnimatorController, ClassIDType.RuntimeAnimatorController));

            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.RectTransform, ClassIDType.Transform));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.Transform, ClassIDType.Transform));

            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.Texture2D, ClassIDType.Texture));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.Texture3D, ClassIDType.Texture));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.Cubemap, ClassIDType.Texture));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.RenderTexture, ClassIDType.Texture));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.Texture2DArray, ClassIDType.Texture));

            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.MeshRenderer, ClassIDType.Renderer));
            Assert.True(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.SkinnedMeshRenderer, ClassIDType.Renderer));

            Assert.False(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.Mesh, ClassIDType.Material));
            Assert.False(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.AnimationClip, ClassIDType.RuntimeAnimatorController));
            Assert.False(SemanticGraphService.IsCompatibleSemanticClassId(ClassIDType.GameObject, ClassIDType.Transform));

            // AssetBundle & ResourceManager must be included in LazyConnectionReferenceTypes
            Assert.Contains(ClassIDType.AssetBundle, SemanticGraphService.LazyConnectionReferenceTypes);
            Assert.Contains(ClassIDType.ResourceManager, SemanticGraphService.LazyConnectionReferenceTypes);
        }

        [Fact]
        public void SemanticGraphService_FindSemanticHandleForPPtr_ResolvesCrossFileAnimatorControllerAndRectTransform()
        {
            var assetsManager = new AssetsManager();
            var sourceFile = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sourceFile.assetsManager = assetsManager;
            sourceFile.fileName = "level1";
            sourceFile.originalPath = Path.Combine(Path.GetTempPath(), "level1");
            sourceFile.m_Externals = new List<FileIdentifier>
            {
                new() { fileName = "resources.assets", pathName = "resources.assets" }
            };

            var ctrlHandle = CreateHandle("resources.assets", Path.Combine(Path.GetTempPath(), "resources.assets"), 3449, ClassIDType.AnimatorController, "redlady", "resources.assets#3449");
            var overrideHandle = CreateHandle("resources.assets", Path.Combine(Path.GetTempPath(), "resources.assets"), 3450, ClassIDType.AnimatorOverrideController, "redlady_override", "resources.assets#3450");
            var rectHandle = CreateHandle("resources.assets", Path.Combine(Path.GetTempPath(), "resources.assets"), 9001, ClassIDType.RectTransform, "UIElement", "resources.assets#9001");
            var texHandle = CreateHandle("resources.assets", Path.Combine(Path.GetTempPath(), "resources.assets"), 7777, ClassIDType.Texture2D, "DiffuseTex", "resources.assets#7777");

            assetsManager.ProjectIndex.AddHandle(ctrlHandle);
            assetsManager.ProjectIndex.AddHandle(overrideHandle);
            assetsManager.ProjectIndex.AddHandle(rectHandle);
            assetsManager.ProjectIndex.AddHandle(texHandle);

            var resolvedCtrl = SemanticGraphService.FindSemanticHandleForPPtr(sourceFile, 1, 3449, ClassIDType.RuntimeAnimatorController);
            Assert.NotNull(resolvedCtrl);
            Assert.Equal("resources.assets#3449", resolvedCtrl!.UniqueID);

            var resolvedOverride = SemanticGraphService.FindSemanticHandleForPPtr(sourceFile, 1, 3450, ClassIDType.RuntimeAnimatorController);
            Assert.NotNull(resolvedOverride);
            Assert.Equal("resources.assets#3450", resolvedOverride!.UniqueID);

            var resolvedRect = SemanticGraphService.FindSemanticHandleForPPtr(sourceFile, 1, 9001, ClassIDType.Transform);
            Assert.NotNull(resolvedRect);
            Assert.Equal("resources.assets#9001", resolvedRect!.UniqueID);

            var resolvedTex = SemanticGraphService.FindSemanticHandleForPPtr(sourceFile, 1, 7777, ClassIDType.Texture);
            Assert.NotNull(resolvedTex);
            Assert.Equal("resources.assets#7777", resolvedTex!.UniqueID);
        }

        [Fact]
        public void SemanticGraphService_AnimatorAndLegacyAnimation_FallbackToDescendantMeshFilterMeshRendererWhenNoSkinnedMeshExists()
        {
            var assetsManager = new AssetsManager();
            var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            typeof(SerializedFile).GetField("objectCollectionLock", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(file, new object());
            file.assetsManager = assetsManager;
            file.fileName = "props.assets";
            file.originalPath = Path.Combine(Path.GetTempPath(), "props.assets");
            file.unityVersion = "2022.3.10f1";
            file.Objects = new List<AssetStudio.Object>();
            file.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            file.m_Objects = new List<ObjectInfo>();
            file.m_Externals = new List<FileIdentifier>();

            void Register(AssetStudio.Object obj, long pathId, ClassIDType type, string name)
            {
                obj.assetsFile = file;
                obj.m_PathID = pathId;
                obj.type = type;
                if (obj is NamedObject named)
                {
                    named.m_Name = name;
                }
                file.Objects.Add(obj);
                file.ObjectsDic[pathId] = obj;
                var h = CreateHandle(file.fileName, file.originalPath, pathId, type, name, AssetHandle.BuildUniqueID(file, pathId));
                h.SourceFile = file;
                h.RealObject = obj;
                assetsManager.ProjectIndex.AddHandle(h);
            }

            // Root GameObject (Animator) -> Child GameObject (MeshFilter + MeshRenderer -> PropMesh)
            var rootGo = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            var rootTf = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            var childGo = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            var childTf = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            var propMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            var mf = (MeshFilter)RuntimeHelpers.GetUninitializedObject(typeof(MeshFilter));
            var mr = (MeshRenderer)RuntimeHelpers.GetUninitializedObject(typeof(MeshRenderer));
            var clip1 = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            var ctrl = (AnimatorController)RuntimeHelpers.GetUninitializedObject(typeof(AnimatorController));
            var animator = (Animator)RuntimeHelpers.GetUninitializedObject(typeof(Animator));
            var legacyClip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            var legacyAnim = (Animation)RuntimeHelpers.GetUninitializedObject(typeof(Animation));

            Register(rootGo, 100, ClassIDType.GameObject, "DoorRoot");
            Register(rootTf, 101, ClassIDType.Transform, "");
            Register(childGo, 102, ClassIDType.GameObject, "DoorLeaf");
            Register(childTf, 103, ClassIDType.Transform, "");
            Register(propMesh, 200, ClassIDType.Mesh, "DoorMesh");
            Register(mf, 201, ClassIDType.MeshFilter, "");
            Register(mr, 202, ClassIDType.MeshRenderer, "");
            Register(clip1, 300, ClassIDType.AnimationClip, "DoorOpen");
            Register(ctrl, 301, ClassIDType.AnimatorController, "DoorCtrl");
            Register(animator, 302, ClassIDType.Animator, "");
            Register(legacyClip, 303, ClassIDType.AnimationClip, "DoorCreak");
            Register(legacyAnim, 304, ClassIDType.Animation, "");

            rootTf.m_GameObject = new PPtr<GameObject>(0, 100, file);
            rootTf.m_Father = new PPtr<Transform>(0, 0, file);
            rootTf.m_LocalPosition = new Vector3(0, 0, 0);
            rootTf.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            rootTf.m_LocalScale = new Vector3(1, 1, 1);
            rootGo.m_Transform = rootTf;
            rootGo.m_Components = new[] { new PPtr<Component>(0, 101, file), new PPtr<Component>(0, 302, file), new PPtr<Component>(0, 304, file) };

            childTf.m_GameObject = new PPtr<GameObject>(0, 102, file);
            childTf.m_Father = new PPtr<Transform>(0, 101, file);
            childTf.m_LocalPosition = new Vector3(0, 0, 0);
            childTf.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            childTf.m_LocalScale = new Vector3(1, 1, 1);
            childGo.m_Transform = childTf;
            childGo.m_Components = new[] { new PPtr<Component>(0, 103, file), new PPtr<Component>(0, 201, file), new PPtr<Component>(0, 202, file) };

            mf.m_GameObject = new PPtr<GameObject>(0, 102, file);
            mf.m_Mesh = new PPtr<Mesh>(0, 200, file);
            mr.m_GameObject = new PPtr<GameObject>(0, 102, file);
            mr.m_Materials = Array.Empty<PPtr<Material>>();

            ctrl.m_AnimationClips = new[] { new PPtr<AnimationClip>(0, 300, file) };
            animator.m_GameObject = new PPtr<GameObject>(0, 100, file);
            animator.m_Avatar = new PPtr<Avatar>(0, 0, file);
            animator.m_Controller = new PPtr<RuntimeAnimatorController>(0, 301, file);

            legacyAnim.m_GameObject = new PPtr<GameObject>(0, 100, file);
            legacyAnim.m_Animations = new[] { new PPtr<AnimationClip>(0, 303, file) };

            var buildResult = SemanticGraphService.BuildAssetReferenceIndexForFile(file);
            var rels = buildResult.SemanticRelations;

            var meshId = AssetHandle.BuildUniqueID(file, 200);
            var clip1Id = AssetHandle.BuildUniqueID(file, 300);
            var legacyClipId = AssetHandle.BuildUniqueID(file, 303);

            // Both AnimatorController clip and Legacy Animation clip MUST resolve the descendant MeshFilter/MeshRenderer mesh!
            Assert.Contains(rels.AnimationClipTargets, t =>
                t.ClipAssetId == clip1Id && t.MeshAssetId == meshId && t.RelationKind == "AnimatorController");
            Assert.Contains(rels.AnimationClipTargets, t =>
                t.ClipAssetId == legacyClipId && t.MeshAssetId == meshId && t.RelationKind == "LegacyAnimation");

            // ModelGroupMeshes must record RendererType == "MeshRenderer" (not hardcoded SkinnedMeshRenderer)
            Assert.Contains(rels.ModelGroupMeshes, m => m.MeshAssetId == meshId && m.RendererType == "MeshRenderer");
        }

        [Fact]
        public void MaterialCache_MeshMaterialResolvedCache_RendererDisambiguation_AndLazyBackfillPropagation()
        {
            var tempRoot = CreateTempDirectory("v15_lazy_backfill_propagation");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "GameData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                var sourcePath = Path.Combine(projectDir, "level0.assets");
                File.WriteAllText(sourcePath, "dummy");

                var scanResult = ProjectScanner.ScanFolder(projectDir);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig = cache.GetFolderSignature(scanResult);

                var handles = new List<AssetHandle>
                {
                    CreateHandle("level0.assets", sourcePath, 10, ClassIDType.Mesh, "SharedCrate", "mesh_crate"),
                    CreateHandle("level0.assets", sourcePath, 20, ClassIDType.Material, "WoodMat", "mat_wood"),
                    CreateHandle("level0.assets", sourcePath, 21, ClassIDType.Material, "MetalMat", "mat_metal"),
                    CreateHandle("level0.assets", sourcePath, 30, ClassIDType.Texture2D, "WoodTex", "tex_wood"),
                    CreateHandle("level0.assets", sourcePath, 31, ClassIDType.Texture2D, "MetalTex", "tex_metal")
                };
                Assert.True(cache.SaveIndexCache(projectDir, sig, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                // First: enqueue mesh->material mapping via lazy backfill (before material texture is known).
                var initialBackfill = new SemanticAssetRelations();
                initialBackfill.MeshRenderers.Add(new SemanticMeshRendererRelation("mesh_crate", "ren_wood", "MeshRenderer", "go_w", "WoodCrate", ""));
                initialBackfill.MeshRenderers.Add(new SemanticMeshRendererRelation("mesh_crate", "ren_metal", "MeshRenderer", "go_m", "MetalCrate", ""));
                initialBackfill.MeshMaterials.Add(new SemanticMeshMaterialRelation("mesh_crate", "mat_wood", "ren_wood", "MeshRenderer", 0, 0, 10));
                initialBackfill.MeshMaterials.Add(new SemanticMeshMaterialRelation("mesh_crate", "mat_metal", "ren_metal", "MeshRenderer", 0, 0, 5));
                cache.EnqueueLazySemanticUpsert(projectDir, sig, initialBackfill);
                Assert.True(cache.WaitForBackgroundWritesIdle(TimeSpan.FromSeconds(5)));

                // Next: enqueue MaterialCache lazy backfill for BOTH materials with distinct base colors & textures.
                var matBackfill = new SemanticAssetRelations();
                matBackfill.MaterialCaches.Add(new SemanticMaterialCacheEntry("mat_wood", "WoodMat", "", "tex_wood", "_MainTex", 0.6f, 0.4f, 0.2f, 1f, "Standard", 1, "lazy_backfill"));
                matBackfill.MaterialCaches.Add(new SemanticMaterialCacheEntry("mat_metal", "MetalMat", "", "tex_metal", "_BaseMap", 0.8f, 0.85f, 0.9f, 1f, "URP/Lit", 1, "lazy_backfill"));
                cache.EnqueueLazySemanticUpsert(projectDir, sig, matBackfill);
                Assert.True(cache.WaitForBackgroundWritesIdle(TimeSpan.FromSeconds(5)));

                // Verify propagation into MeshMaterialResolvedCache!
                var resolvedDefault = cache.LoadResolvedMeshMaterials(projectDir, sig, "mesh_crate");
                Assert.Single(resolvedDefault);
                Assert.Equal("mat_wood", resolvedDefault[0].MaterialAssetId);
                Assert.Equal("tex_wood", resolvedDefault[0].MainTextureAssetId);
                Assert.Equal(0.6f, resolvedDefault[0].BaseColorR, precision: 3);
                Assert.Equal("lazy_backfill", resolvedDefault[0].ResolutionSource);

                // Verify per-renderer disambiguation returns MetalMat for ren_metal!
                var resolvedMetal = cache.LoadResolvedMeshMaterials(projectDir, sig, "mesh_crate", rendererAssetId: "ren_metal");
                Assert.Single(resolvedMetal);
                Assert.Equal("mat_metal", resolvedMetal[0].MaterialAssetId);
                Assert.Equal("tex_metal", resolvedMetal[0].MainTextureAssetId);
                Assert.Equal(0.8f, resolvedMetal[0].BaseColorR, precision: 3);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task WriteGateContention_PreviewReadsAndNonBlockingEnqueue_DoNotBlockWhileWriterLockHeld()
        {
            var tempRoot = CreateTempDirectory("writegate_nonblocking");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "GameData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                var sourcePath = Path.Combine(projectDir, "level0.assets");
                File.WriteAllText(sourcePath, "dummy");

                var scanResult = ProjectScanner.ScanFolder(projectDir);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig = cache.GetFolderSignature(scanResult);
                var handles = new List<AssetHandle>
                {
                    CreateHandle("level0.assets", sourcePath, 1, ClassIDType.Mesh, "Crate", "mesh_1"),
                    CreateHandle("level0.assets", sourcePath, 2, ClassIDType.Material, "CrateMat", "mat_1"),
                    CreateHandle("level0.assets", sourcePath, 3, ClassIDType.Texture2D, "CrateTex", "tex_1")
                };
                Assert.True(cache.SaveIndexCache(projectDir, sig, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                var seedRelations = new SemanticAssetRelations();
                seedRelations.MeshMaterials.Add(new SemanticMeshMaterialRelation("mesh_1", "mat_1", "ren_1", "MeshRenderer", 0, 0, 10));
                seedRelations.MaterialCaches.Add(new SemanticMaterialCacheEntry("mat_1", "CrateMat", "", "tex_1", "_MainTex", 1f, 1f, 1f, 1f, "Standard", 1, "preparation"));
                Assert.True(cache.SaveSemanticRelations(projectDir, sig, seedRelations, replaceExisting: true));

                var initialPayload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                cache.SavePreviewCachePayload(
                    projectDir, sig, "tex_1", "texture-thumbnail-png", 1, "max=512|w=2", "hash_init", initialPayload, Path.Combine(tempRoot, "init.png"));

                // Acquire private static WriteGate on another thread for 800ms to simulate an active bulk Commit().
                var writeGateField = typeof(SQLiteProjectIndexCache).GetField("WriteGate", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(writeGateField);
                var writeGate = writeGateField!.GetValue(null)!;

                using var lockAcquired = new ManualResetEventSlim(false);
                using var releaseLock = new ManualResetEventSlim(false);
                var holderTask = Task.Run(() =>
                {
                    lock (writeGate)
                    {
                        lockAcquired.Set();
                        releaseLock.Wait(TimeSpan.FromSeconds(5));
                    }
                });

                Assert.True(lockAcquired.Wait(TimeSpan.FromSeconds(2)));
                try
                {
                    var sw = Stopwatch.StartNew();

                    // All WAL reads (including TryTouch with TimeSpan.Zero!) MUST complete immediately without blocking!
                    Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, sig, "tex_1", "texture-thumbnail-png", 1, "max=512|w=2", out var loadedBytes, out _));
                    Assert.Equal(initialPayload, loadedBytes);

                    Assert.True(cache.TryLoadLatestPreviewCacheBytesByAssetId(projectDir, sig, "tex_1", "texture-thumbnail-png", 1, "max=512|", out var latestBytes, out _));
                    Assert.Equal(initialPayload, latestBytes);

                    var resolvedMats = cache.LoadResolvedMeshMaterials(projectDir, sig, "mesh_1");
                    Assert.Single(resolvedMats);

                    // Non-blocking background enqueues MUST return immediately without waiting for WriteGate!
                    var queuedPayload = new byte[] { 9, 10, 11, 12 };
                    cache.EnqueuePreviewCachePayload(projectDir, sig, "mesh_1", "mesh-geometry", 1, "density=100", "hash_geom", queuedPayload, Path.Combine(tempRoot, "geom.cache"));

                    var lazyRel = new SemanticAssetRelations();
                    lazyRel.MaterialCaches.Add(new SemanticMaterialCacheEntry("mat_2", "Mat2", "", "tex_2", "_BaseMap", 0.5f, 0.5f, 0.5f, 1f, "Lit", 1, "lazy_backfill"));
                    cache.EnqueueLazySemanticUpsert(projectDir, sig, lazyRel);

                    sw.Stop();
                    _output.WriteLine($"All reads + non-blocking enqueues under WriteGate contention completed in {sw.ElapsedMilliseconds} ms");
                    Assert.True(sw.ElapsedMilliseconds < 400, $"Operations blocked on WriteGate ({sw.ElapsedMilliseconds}ms)!");
                }
                finally
                {
                    releaseLock.Set();
                    await holderTask;
                }

                // After WriteGate releases, background writer drains and persists both queued writes!
                Assert.True(cache.WaitForBackgroundWritesIdle(TimeSpan.FromSeconds(5)));
                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, sig, "mesh_1", "mesh-geometry", 1, "density=100", out var persistedGeom, out _));
                Assert.Equal(new byte[] { 9, 10, 11, 12 }, persistedGeom);

                var persistedMat2 = cache.LoadMaterialCacheEntry(projectDir, sig, "mat_2");
                Assert.NotNull(persistedMat2);
                Assert.Equal("tex_2", persistedMat2!.MainTextureAssetId);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SubMeshTextureDeduplication_170SubmeshesWith18UniqueTextures_UploadsOnly18SlotsAndMapsCorrectly()
        {
            const int subMeshCount = 170;
            const int uniqueTexCount = 18;
            var subMeshTexKeys = new string?[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
            {
                // Leave every 19th submesh untextured (null) to verify gap handling.
                subMeshTexKeys[i] = (i % 19 == 0) ? null : $"tex_{(i % uniqueTexCount):D2}";
            }

            MainWindow.BuildSubMeshTextureDeduplicationPlan(subMeshCount, subMeshTexKeys, out var uniqueKeys, out var subMeshToUnique);

            Assert.Equal(uniqueTexCount, uniqueKeys.Count);
            Assert.Equal(subMeshCount, subMeshToUnique.Length);

            var fakeGlIds = Enumerable.Range(100, uniqueTexCount).ToArray();
            for (int i = 0; i < subMeshCount; i++)
            {
                var glId = MeshPreviewControl.ResolveSubMeshTextureId(fakeGlIds, subMeshToUnique, i);
                if (subMeshTexKeys[i] == null)
                {
                    Assert.Equal(-1, subMeshToUnique[i]);
                    Assert.Equal(0, glId);
                }
                else
                {
                    var mappedIdx = subMeshToUnique[i];
                    Assert.InRange(mappedIdx, 0, uniqueTexCount - 1);
                    Assert.Equal(subMeshTexKeys[i], uniqueKeys[mappedIdx]);
                    Assert.Equal(fakeGlIds[mappedIdx], glId);
                }
            }
        }

        [Fact]
        public void SelectBestMeshPreviewCandidate_DoesNotHijackCombinedMeshOrLargeSceneObject_ButSelectsCompactAnimatorGroup()
        {
            var combinedMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            combinedMesh.m_Name = "Combined Mesh (root: scene) 4";
            combinedMesh.m_PathID = 1;

            var singleCandidate = new PreviewCandidateItem { Mesh = combinedMesh, MeshId = "level27#1", Label = "Current mesh" };
            var hugeSceneGroup = new PreviewCandidateItem
            {
                MeshId = "level27#1",
                ModelGroupId = "modelgroup:sceneobject:level27#999",
                ModelGroupName = "SceneRoot",
                ModelGroupKind = "SceneObject",
                ModelGroupMeshIds = Enumerable.Range(1, 102).Select(i => $"level27#{i}").ToArray(),
                ModelGroupMeshCount = 102,
                ModelGroupConfidence = 100,
                Label = "Model group: SceneRoot (102 parts)"
            };

            // Must NOT hijack Combined Mesh into 102-part SceneObject group
            var selected = MainWindow.SelectBestMeshPreviewCandidate(
                combinedMesh,
                new[] { singleCandidate, hugeSceneGroup },
                selectedCandidate: null,
                preferModelGroup: true);
            Assert.Same(singleCandidate, selected);

            // Explicit user selection of SceneObject group MUST still work
            var explicitSelection = MainWindow.SelectBestMeshPreviewCandidate(
                combinedMesh,
                new[] { singleCandidate, hugeSceneGroup },
                selectedCandidate: hugeSceneGroup,
                preferModelGroup: true);
            Assert.Same(hugeSceneGroup, explicitSelection);

            // Compact high-confidence Animator character group SHOULD be auto-selected for regular character meshes
            var heroMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            heroMesh.m_Name = "body";
            heroMesh.m_PathID = 1631;
            var heroSingle = new PreviewCandidateItem { Mesh = heroMesh, MeshId = "res#1631", Label = "Current mesh" };
            var heroAnimatorGroup = new PreviewCandidateItem
            {
                MeshId = "res#1631",
                ModelGroupId = "modelgroup:animator:res#500",
                ModelGroupName = "RedLady",
                ModelGroupKind = "Animator",
                ModelGroupMeshIds = new[] { "res#1631", "res#1632", "res#1633" },
                ModelGroupMeshCount = 3,
                ModelGroupConfidence = 100,
                Label = "Model group: RedLady (3 parts)"
            };
            var selectedHero = MainWindow.SelectBestMeshPreviewCandidate(
                heroMesh,
                new[] { heroSingle, heroAnimatorGroup },
                selectedCandidate: null,
                preferModelGroup: true);
            Assert.Same(heroAnimatorGroup, selectedHero);
        }

        [Fact]
        public void SelectBestAnimationClipCandidate_PrefersClipLinkedLazyTargetOverUnrelatedCurrentPreview()
        {
            var clip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            clip.m_Name = "chargebeam2";

            var unrelatedAvatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            unrelatedAvatar.m_Name = "UnrelatedGolemAvatar";
            unrelatedAvatar.m_PathID = 900;

            var unrelatedMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            unrelatedMesh.m_Name = "GolemBody";
            unrelatedMesh.m_PathID = 901;

            var currentCandidate = new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = unrelatedAvatar,
                Mesh = unrelatedMesh,
                AvatarId = "level0#900",
                MeshId = "level0#901",
                IsCurrentPreviewFallback = true,
                Label = "Current: UnrelatedGolemAvatar -> GolemBody"
            };

            var linkedLazyCandidate = new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = null,
                Mesh = null,
                AvatarId = "resources.assets#3382",
                MeshId = "resources.assets#1631",
                IsCurrentPreviewFallback = false,
                Label = "Avatar relation: red ladyAvatar -> body"
            };

            // Must pick clip's lazy Avatar+Mesh over unrelated loaded Current preview!
            var chosen = MainWindow.SelectBestAnimationClipCandidate(
                clip,
                new[] { currentCandidate, linkedLazyCandidate },
                preferredAvatar: null,
                preferredMesh: null,
                preferredAvatarId: null,
                preferredMeshId: null,
                isCompatibleWithAvatar: (_, _) => false);

            Assert.Same(linkedLazyCandidate, chosen);

            // When a clip-linked ModelGroup exists, it ranks even higher than a plain clip-linked Avatar+Mesh!
            var linkedModelGroup = new PreviewCandidateItem
            {
                AnimationClip = clip,
                AvatarId = "resources.assets#3382",
                MeshId = "resources.assets#1631",
                ModelGroupId = "modelgroup:animator:res#10",
                ModelGroupName = "RedLady",
                ModelGroupMeshIds = new[] { "resources.assets#1631", "resources.assets#1632" },
                ModelGroupMeshCount = 2,
                IsCurrentPreviewFallback = false,
                Label = "Model group: RedLady (2 parts)"
            };

            var chosenGroup = MainWindow.SelectBestAnimationClipCandidate(
                clip,
                new[] { currentCandidate, linkedLazyCandidate, linkedModelGroup },
                preferredAvatar: null,
                preferredMesh: null,
                preferredAvatarId: null,
                preferredMeshId: null,
                isCompatibleWithAvatar: (_, _) => false);

            Assert.Same(linkedModelGroup, chosenGroup);
        }

        [Fact]
        public void CachedSemanticStatus_IsInvalidatedOnMutationAndAvoidsStaleReads()
        {
            var tempRoot = CreateTempDirectory("cached_semantic_status");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "GameData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                var sourcePath = Path.Combine(projectDir, "level0.assets");
                File.WriteAllText(sourcePath, "dummy");

                var scanResult = ProjectScanner.ScanFolder(projectDir);

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var sig = cache.GetFolderSignature(scanResult);
                var handles = new List<AssetHandle>
                {
                    CreateHandle("level0.assets", sourcePath, 1, ClassIDType.Mesh, "Box", "mesh_box")
                };
                Assert.True(cache.SaveIndexCache(projectDir, sig, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                Assert.False(cache.CanUseSemanticRelationCache(projectDir, sig));
                Assert.False(cache.HasSemanticRelations(projectDir, sig));

                var rels = new SemanticAssetRelations();
                rels.MeshMaterials.Add(new SemanticMeshMaterialRelation("mesh_box", "mat_box", "ren_box", "MeshRenderer", 0, 0, 5));
                Assert.True(cache.SaveSemanticRelations(projectDir, sig, rels, replaceExisting: true));

                Assert.True(cache.HasSemanticRelations(projectDir, sig));
                Assert.True(cache.CanUseSemanticRelationCache(projectDir, sig));

                cache.ClearSemanticRelations(projectDir, sig);
                Assert.False(cache.HasSemanticRelations(projectDir, sig));
                Assert.False(cache.CanUseSemanticRelationCache(projectDir, sig));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void RealGameReadOnlyProbe_AnimaGateOfMemories_UpgradeCommitAndResolutionBenchmarks()
        {
            var sourceDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "IndexCache",
                "project_index_4f71c3028b7546f85a11a101b585fe8d.db");
            const string gameDataDir = @"F:\SteamLibrary\steamapps\common\Anima Gate of Memories\AnimaGateOfMemories_Data";

            if (!File.Exists(sourceDbPath) || !Directory.Exists(gameDataDir))
            {
                _output.WriteLine("Skipping real-game read-only probe (Anima Gate of Memories DB/game folder not present on this host).");
                return;
            }

            var tempRoot = CreateTempDirectory("anima_readonly_probe");
            try
            {
                var tempCacheDir = Path.Combine(tempRoot, "IndexCache");
                Directory.CreateDirectory(tempCacheDir);

                // Strictly READ-ONLY copy of DB (+ WAL/SHM if present) into temp dir; NEVER touch original DB or SteamLibrary!
                var tempDbName = Path.GetFileName(sourceDbPath);
                var tempDbPath = Path.Combine(tempCacheDir, tempDbName);
                File.Copy(sourceDbPath, tempDbPath, overwrite: true);
                if (File.Exists(sourceDbPath + "-wal"))
                {
                    File.Copy(sourceDbPath + "-wal", tempDbPath + "-wal", overwrite: true);
                }
                if (File.Exists(sourceDbPath + "-shm"))
                {
                    File.Copy(sourceDbPath + "-shm", tempDbPath + "-shm", overwrite: true);
                }

                // Read existing project FolderPath & SignatureHash directly from copied DB BEFORE opening via SQLiteProjectIndexCache.
                string folderPath;
                string signature;
                string initialStatus;
                string initialSchemaVer;
                using (var preConn = new SqliteConnection($"Data Source={tempDbPath};Mode=ReadOnly;Pooling=False"))
                {
                    preConn.Open();
                    initialSchemaVer = ExecuteScalarString(preConn, "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;");
                    using var projCmd = preConn.CreateCommand();
                    projCmd.CommandText = @"
                        SELECT p.FolderPath, p.SignatureHash, COALESCE(s.Status, '')
                        FROM Projects p
                        LEFT JOIN ProjectIndexingState s ON s.ProjectId = p.Id
                        LIMIT 1;";
                    using var r = projCmd.ExecuteReader();
                    Assert.True(r.Read());
                    folderPath = r.GetString(0);
                    signature = r.GetString(1);
                    initialStatus = r.IsDBNull(2) ? string.Empty : r.GetString(2);
                }

                _output.WriteLine($"Real DB copied to temp: pre-upgrade SchemaVersion={initialSchemaVer}, Status={initialStatus}, Folder={folderPath}");

                using var cache = new SQLiteProjectIndexCache(tempCacheDir);
                var expectedDbPath = cache.GetDbPath(folderPath);
                if (!string.Equals(Path.GetFullPath(expectedDbPath), Path.GetFullPath(tempDbPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(tempDbPath, expectedDbPath, overwrite: true);
                }

                // 1. Measure v14 -> v15 in-place upgrade time!
                var upgradeSw = Stopwatch.StartNew();
                cache.EnsureInitialized(folderPath);
                upgradeSw.Stop();

                var upgradedState = cache.LoadIndexingState(folderPath, signature);
                Assert.NotNull(upgradedState);
                _output.WriteLine($"v14->v15 in-place upgrade: {upgradeSw.ElapsedMilliseconds} ms | Preserved Status: {upgradedState!.Status}");

                long matCacheCount, meshResolvedCount, compositeCount, compositePartsCount, emptyAnimTargets, totalAnimTargets;
                string combinedMeshId;
                string chargebeamId;
                using (var conn = cache.CreateReadConnection(folderPath))
                {
                    Assert.Equal("15", ExecuteScalarString(conn, "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;"));
                    matCacheCount = ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM MaterialCache;");
                    meshResolvedCount = ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM MeshMaterialResolvedCache;");
                    compositeCount = ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM CompositeAssets;");
                    compositePartsCount = ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM CompositeAssetParts;");
                    emptyAnimTargets = ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AnimationClipTargets WHERE AvatarAssetUniqueID = '' AND MeshAssetUniqueID = '' AND ModelGroupId = '';");
                    totalAnimTargets = ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AnimationClipTargets;");

                    combinedMeshId = ExecuteScalarString(conn,
                        "SELECT UniqueID FROM Assets WHERE Name = 'Combined Mesh (root: scene) 4' AND PathID = 1 LIMIT 1;");
                    if (string.IsNullOrEmpty(combinedMeshId))
                    {
                        combinedMeshId = ExecuteScalarString(conn,
                            "SELECT UniqueID FROM Assets WHERE Name LIKE 'Combined Mesh (root: scene)%' AND PathID = 1 LIMIT 1;");
                    }
                    chargebeamId = ExecuteScalarString(conn,
                        "SELECT UniqueID FROM Assets WHERE Name = 'chargebeam2' AND PathID = 2799 LIMIT 1;");
                }

                Assert.Equal(0L, emptyAnimTargets);
                _output.WriteLine($"Backfilled v15 tables: MaterialCache={matCacheCount:N0}, MeshMaterialResolvedCache={meshResolvedCount:N0}, CompositeAssets={compositeCount:N0}, CompositeAssetParts={compositePartsCount:N0}, AnimationClipTargets={totalAnimTargets:N0} (0 empty)");

                // 2. Benchmark LoadResolvedMeshMaterials on Combined Mesh (root: scene) 4 (level27#1, 170 submeshes)!
                if (!string.IsNullOrEmpty(combinedMeshId))
                {
                    var meshQuerySw = Stopwatch.StartNew();
                    var resolved = cache.LoadResolvedMeshMaterials(folderPath, signature, combinedMeshId);
                    meshQuerySw.Stop();

                    var distinctMats = resolved.Select(r => r.MaterialAssetId).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Count();
                    var distinctTexs = resolved.Select(r => r.MainTextureAssetId).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Count();
                    MainWindow.BuildSubMeshTextureDeduplicationPlan(
                        resolved.Count,
                        resolved.Select(r => string.IsNullOrEmpty(r.MainTextureAssetId) ? null : r.MainTextureAssetId).ToArray(),
                        out var uniqueTexKeys,
                        out _);

                    _output.WriteLine($"Combined Mesh ({combinedMeshId}): LoadResolvedMeshMaterials took {meshQuerySw.ElapsedMilliseconds} ms | submeshes={resolved.Count}, distinctMats={distinctMats}, uniqueTextures={uniqueTexKeys.Count} (distinctTexs={distinctTexs})");
                    Assert.True(meshQuerySw.ElapsedMilliseconds < 250);
                    if (resolved.Count >= 100)
                    {
                        Assert.True(uniqueTexKeys.Count < resolved.Count, "Expected significant texture deduplication on 170-submesh Combined Mesh.");
                    }
                }

                // 3. Benchmark AnimationClipTargets lookup for chargebeam2 (PathID 2799) + READ-ONLY resources.assets header/object probe!
                if (!string.IsNullOrEmpty(chargebeamId))
                {
                    var animSw = Stopwatch.StartNew();
                    var targets = cache.LoadAnimationClipTargets(folderPath, signature, chargebeamId);
                    animSw.Stop();
                    _output.WriteLine($"chargebeam2 ({chargebeamId}) SQLite targets: {targets.Count} in {animSw.ElapsedMilliseconds} ms"
                        + (targets.Count > 0 ? $" | top: avatar={targets[0].AvatarName} ({targets[0].AvatarAssetId}), mesh={targets[0].MeshName} ({targets[0].MeshAssetId}), kind={targets[0].RelationKind}" : ""));
                    Assert.True(animSw.ElapsedMilliseconds < 250);
                    Assert.NotEmpty(targets);
                }

                // Strictly READ-ONLY O(1) header + targeted object probe on resources.assets (verifying chargebeam2 #2799 -> redlady AnimatorController #3449 -> Animator #55833 -> Avatar #3382)
                var resourcesAssetsPath = Path.Combine(gameDataDir, "resources.assets");
                if (File.Exists(resourcesAssetsPath))
                {
                    var probeSw = Stopwatch.StartNew();
                    var am = new AssetsManager { LazyLoading = true };
                    using var fileStream = new FileStream(resourcesAssetsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var fileReader = new FileReader(resourcesAssetsPath, fileStream);
                    var resFile = new SerializedFile(fileReader, am);

                    long[] targetPathIds = { 2799, 3449, 55833, 3382, 1631 };
                    foreach (var pid in targetPathIds)
                    {
                        if (resFile.ObjectInfoDic.TryGetValue(pid, out var objInfo))
                        {
                            var objReader = new ObjectReader(fileReader, resFile, objInfo);
                            resFile.AddObject(AssetsManager.CreateObjectFromReader(objReader));
                        }
                    }

                    // Verify ResolveRuntimeAnimatorControllerBackground & GetSemanticAssetIdFromPPtr resolve Animator.m_Controller -> AnimatorController #3449!
                    var redladyAnimator = resFile.Objects.OfType<Animator>().FirstOrDefault(a => a.m_Controller.m_PathID == 3449);
                    Assert.NotNull(redladyAnimator);
                    var resolvedCtrl = SemanticGraphService.ResolveRuntimeAnimatorControllerBackground(
                        resFile,
                        redladyAnimator!.m_Controller);
                    var resolvedCtrlId = SemanticGraphService.GetSemanticAssetIdFromPPtr(
                        resFile,
                        redladyAnimator.m_Controller,
                        ClassIDType.RuntimeAnimatorController);
                    Assert.NotNull(resolvedCtrl);
                    Assert.IsType<AnimatorController>(resolvedCtrl);
                    Assert.EndsWith("#3449", resolvedCtrlId, StringComparison.Ordinal);
                    probeSw.Stop();

                    _output.WriteLine($"READ-ONLY resources.assets PPtr<RuntimeAnimatorController> probe: {probeSw.ElapsedMilliseconds} ms | Animator#{redladyAnimator.m_PathID} -> AnimatorController#{resolvedCtrl!.m_PathID} ({resolvedCtrl.m_Name}, id={resolvedCtrlId})");
                    am.Clear();
                }

                // 4. Benchmark real-data Commit() speed directly on the upgraded temp DB!
                var commitSw = Stopwatch.StartNew();
                var ticks = 0;
                using (var session = cache.BeginSemanticStagingSession(folderPath, signature))
                {
                    using (var rawConn = cache.CreateConnection(folderPath))
                    {
                        using var copyTx = rawConn.BeginTransaction();
                        using var copyCmd = rawConn.CreateCommand();
                        copyCmd.Transaction = copyTx;
                        copyCmd.CommandText = @"
                            INSERT INTO Stage_AssetEdges (
                                ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                                SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                            SELECT
                                ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID,
                                SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved
                            FROM AssetEdges
                            WHERE EdgeKind IN ('AnimationClip', 'AnimatorController', 'Avatar', 'Mesh');

                            INSERT INTO Stage_ModelGroups (
                                ProjectId, GroupAssetUniqueID, GroupKind, GroupName,
                                RootGameObjectAssetUniqueID, RootGameObjectName,
                                AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                                SourceFileName, Confidence, ConfidenceReason)
                            SELECT
                                ProjectId, GroupAssetUniqueID, GroupKind, GroupName,
                                RootGameObjectAssetUniqueID, RootGameObjectName,
                                AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID,
                                SourceFileName, Confidence, ConfidenceReason
                            FROM ModelGroups;

                            INSERT INTO Stage_ModelGroupMeshes (
                                ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID,
                                RendererType, GameObjectAssetUniqueID, GameObjectName,
                                SlotIndex, TransformMatrix, Confidence, ConfidenceReason)
                            SELECT
                                ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID,
                                RendererType, GameObjectAssetUniqueID, GameObjectName,
                                SlotIndex, TransformMatrix, Confidence, ConfidenceReason
                            FROM ModelGroupMeshes;

                            INSERT INTO Stage_MeshRenderers SELECT * FROM MeshRenderers;

                            INSERT INTO Stage_MeshMaterials (
                                ProjectId, MeshAssetUniqueID, MaterialAssetUniqueID, RendererAssetUniqueID,
                                RendererType, SubMeshIndex, MaterialSlotIndex, MaterialScore)
                            SELECT
                                ProjectId, MeshAssetUniqueID, MaterialAssetUniqueID, RendererAssetUniqueID,
                                RendererType, SubMeshIndex, MaterialSlotIndex, MaterialScore
                            FROM MeshMaterials;

                            INSERT INTO Stage_MaterialTextures (
                                ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex,
                                TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                                ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource)
                            SELECT
                                ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex,
                                TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture,
                                ScaleX, ScaleY, OffsetX, OffsetY, ResolutionSource
                            FROM MaterialTextures;

                            INSERT INTO Stage_AnimationClipTargets (
                                ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                                MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                            SELECT
                                ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID,
                                MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount
                            FROM AnimationClipTargets;";
                        copyCmd.ExecuteNonQuery();
                        copyTx.Commit();
                    }

                    var markerBatch = new SemanticAssetRelations();
                    markerBatch.SourceFiles.Add(new SemanticSourceFileEntry("resources.assets", Path.Combine(gameDataDir, "resources.assets"), "5.3.4f1", 1));
                    markerBatch.MaterialCaches.Add(new SemanticMaterialCacheEntry("mat_probe_marker", "ProbeMarker", "", "", "", 1f, 1f, 1f, 1f, "", 0, "preparation"));
                    session.FlushBatch(markerBatch, clearBatch: true);

                    commitSw.Restart();
                    var committed = session.Commit((_, _, _) => ticks++);
                    Assert.True(committed);
                }
                commitSw.Stop();

                _output.WriteLine($"Full real-game AnimaGateOfMemories Commit() benchmark: {commitSw.ElapsedMilliseconds} ms ({ticks} progress ticks) [was 6m41s = 401,000 ms!]");
                Assert.True(commitSw.ElapsedMilliseconds < 30_000, $"Real-game Commit() took {commitSw.ElapsedMilliseconds}ms!");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AnimationPreviewSkinning_FullAvatarHierarchy_UnflippedVertices_AndBindPoseFallback_AreExact()
        {
            static AssetStudio.Matrix4x4 ToAssetMatrix(OpenTK.Mathematics.Matrix4 m) => new AssetStudio.Matrix4x4
            {
                M00 = m.M11, M01 = m.M12, M02 = m.M13, M03 = m.M14,
                M10 = m.M21, M11 = m.M22, M12 = m.M23, M13 = m.M24,
                M20 = m.M31, M21 = m.M32, M22 = m.M33, M23 = m.M34,
                M30 = m.M41, M31 = m.M42, M32 = m.M43, M33 = m.M44
            };

            // Avatar hierarchy:
            //   0: Root        (non-mesh, identity)
            //   1: Root/Point  (intermediate non-mesh parent: t=(0,10,0), q=-90 deg X, s=(2,2,2))
            //   2: Root/Point/Arm (mesh bone 0: t=(3,0,4), q=identity, s=(1,1,1))
            //   3: Root/Point/Arm/Hand (mesh bone 1: t=(2,0,0), q=identity, s=(1,1,1))
            var paths = new[]
            {
                "Root",
                "Root/Point",
                "Root/Point/Arm",
                "Root/Point/Arm/Hand"
            };
            var parentIds = new[] { -1, 0, 1, 2 };
            var qNeg90X = OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitX, -MathF.PI * 0.5f);
            var localPos = new[]
            {
                OpenTK.Mathematics.Vector3.Zero,
                new OpenTK.Mathematics.Vector3(0f, 10f, 0f),
                new OpenTK.Mathematics.Vector3(3f, 0f, 4f),
                new OpenTK.Mathematics.Vector3(2f, 0f, 0f)
            };
            var localRot = new[]
            {
                OpenTK.Mathematics.Quaternion.Identity,
                qNeg90X,
                OpenTK.Mathematics.Quaternion.Identity,
                OpenTK.Mathematics.Quaternion.Identity
            };
            var localScale = new[]
            {
                OpenTK.Mathematics.Vector3.One,
                new OpenTK.Mathematics.Vector3(2f, 2f, 2f),
                OpenTK.Mathematics.Vector3.One,
                OpenTK.Mathematics.Vector3.One
            };

            var nodes = new Node[paths.Length];
            var ids = new uint[paths.Length];
            var tos = new KeyValuePair<uint, string>[paths.Length];
            var poseX = new XForm[paths.Length];
            var restWorlds = new OpenTK.Mathematics.Matrix4[paths.Length];

            for (int i = 0; i < paths.Length; i++)
            {
                ids[i] = MainWindow.ComputeUnityBonePathHash(paths[i]);
                tos[i] = new KeyValuePair<uint, string>(ids[i], paths[i]);

                var node = (Node)RuntimeHelpers.GetUninitializedObject(typeof(Node));
                node.m_ParentId = parentIds[i];
                node.m_AxesId = -1;
                nodes[i] = node;

                var xf = (XForm)RuntimeHelpers.GetUninitializedObject(typeof(XForm));
                xf.t = new AssetStudio.Vector3(localPos[i].X, localPos[i].Y, localPos[i].Z);
                xf.q = new AssetStudio.Quaternion(localRot[i].X, localRot[i].Y, localRot[i].Z, localRot[i].W);
                xf.s = new AssetStudio.Vector3(localScale[i].X, localScale[i].Y, localScale[i].Z);
                poseX[i] = xf;

                var localMat = MeshPreviewSkeletonBuilder.CreateLocalMatrix(localPos[i], localRot[i], localScale[i]);
                restWorlds[i] = parentIds[i] >= 0 ? localMat * restWorlds[parentIds[i]] : localMat;
            }

            var avatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            avatar.m_Name = "SyntheticAvatar";
            avatar.m_PathID = 9001;
            var skeleton = (Skeleton)RuntimeHelpers.GetUninitializedObject(typeof(Skeleton));
            skeleton.m_Node = nodes;
            skeleton.m_ID = ids;
            skeleton.m_AxesArray = Array.Empty<Axes>();
            var skeletonPose = (SkeletonPose)RuntimeHelpers.GetUninitializedObject(typeof(SkeletonPose));
            skeletonPose.m_X = poseX;
            var avatarConstant = (AvatarConstant)RuntimeHelpers.GetUninitializedObject(typeof(AvatarConstant));
            avatarConstant.m_AvatarSkeleton = skeleton;
            avatarConstant.m_AvatarSkeletonPose = skeletonPose;
            avatarConstant.m_DefaultPose = skeletonPose;
            avatar.m_Avatar = avatarConstant;
            avatar.m_TOS = tos;

            // Asymmetric +X vertices in 4-component stride (x, y, z, w) so any -X flip or stride-3 bug fails loudly.
            // Place vertices near Arm/Hand rest world positions so rest bind pose maps them to themselves.
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(mesh, true);
            mesh.m_Name = "SyntheticArmMesh";
            mesh.m_PathID = 9002;
            mesh.m_VertexCount = 3;
            var expectedLocalVerts = new[]
            {
                new OpenTK.Mathematics.Vector3(7f, 18f, 0f),
                new OpenTK.Mathematics.Vector3(10f, 18f, 0f),
                new OpenTK.Mathematics.Vector3(10f, 20f, 1f)
            };
            mesh.m_Vertices = new[]
            {
                expectedLocalVerts[0].X, expectedLocalVerts[0].Y, expectedLocalVerts[0].Z, 1f,
                expectedLocalVerts[1].X, expectedLocalVerts[1].Y, expectedLocalVerts[1].Z, 1f,
                expectedLocalVerts[2].X, expectedLocalVerts[2].Y, expectedLocalVerts[2].Z, 1f
            };
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            mesh.m_BoneNameHashes = new[] { ids[2], ids[3] };
            mesh.m_BindPose = new[]
            {
                ToAssetMatrix(restWorlds[2].Inverted()),
                ToAssetMatrix(restWorlds[3].Inverted())
            };
            mesh.m_Skin = new BoneWeights4[3];
            for (int v = 0; v < 3; v++)
            {
                var bw = (BoneWeights4)RuntimeHelpers.GetUninitializedObject(typeof(BoneWeights4));
                bw.boneIndex = new[] { v == 0 ? 0 : 1, 0, 0, 0 };
                bw.weight = new[] { 1f, 0f, 0f, 0f };
                mesh.m_Skin[v] = bw;
            }

            var rig = PreparedAnimationRigData.TryBuild("synthetic", avatar, mesh);
            Assert.NotNull(rig);
            Assert.True(rig!.HasFullAvatarSkeleton);
            Assert.Equal(4, rig.Skeleton.AvatarNodeCount);
            Assert.Equal(2, rig.Skeleton.MeshBoneCount);
            Assert.True(rig.AvatarDeformChainMask[0]);
            Assert.True(rig.AvatarDeformChainMask[1]); // Intermediate non-mesh parent MUST be in deform chain
            Assert.True(rig.AvatarDeformChainMask[2]);
            Assert.True(rig.AvatarDeformChainMask[3]);

            // 1. Verify ExtractRigMeshVertices keeps +X (no X-mirroring) and supports stride-4 vertices
            Assert.NotNull(rig.SourceVertexData);
            for (int v = 0; v < 3; v++)
            {
                Assert.True((rig.SourceVertexData![v] - expectedLocalVerts[v]).Length < 1e-5f,
                    $"Vertex {v} mismatch: got {rig.SourceVertexData[v]}, expected {expectedLocalVerts[v]}");
            }

            // 2. Animate ONLY intermediate non-mesh node 1 (Root/Point): frame 0 = rest pose, frame 1 = +90 deg Y rotation on top of -90 deg X
            var qFrame1 = qNeg90X * OpenTK.Mathematics.Quaternion.FromAxisAngle(OpenTK.Mathematics.Vector3.UnitY, MathF.PI * 0.5f);
            var rotTracks = new Dictionary<int, List<(float time, OpenTK.Mathematics.Quaternion value)>>
            {
                [1] = new()
                {
                    (0f, qNeg90X),
                    (1f, qFrame1)
                }
            };
            var ok = MeshPreviewAnimationEvaluator.EvaluateFramesFromAvatarSkeletonTracks(
                mesh,
                rig.Skeleton,
                poseX,
                rig.DeformChainMask,
                rig.WeightedBoneMask,
                new Dictionary<int, List<(float time, OpenTK.Mathematics.Vector3 value)>>(),
                rotTracks,
                new Dictionary<int, List<(float time, OpenTK.Mathematics.Vector3 value)>>(),
                sampleRate: 1f,
                maxTime: 2f,
                useCeilingFrameCount: false,
                failIfAllTracksFiltered: true,
                out var frames,
                out var boneMatrices,
                out var renderParents,
                out var resolvedFps,
                out var reason);

            Assert.True(ok, reason);
            Assert.Equal(2, frames.Length);

            var gl = MeshPreviewControl.PrepareAnimatedAvatarGLData(
                mesh, frames, boneMatrices, renderParents, resolvedFps, rig.Skeleton.MeshBoneNames,
                100f, 0.1f, rig.SourceVertexData, rig.SourceMeshMin, rig.SourceMeshMax, rig.GetOrBuildReferenceMesh(100f));
            Assert.NotNull(gl);
            Assert.NotNull(gl!.AnimatedMeshVertices);
            Assert.Equal(2, gl.AnimatedMeshVertices!.Length);

            // Frame 0 is exact rest pose -> bindPose * restWorld == Identity -> vertices unchanged (no -X flip!)
            for (int v = 0; v < 3; v++)
            {
                int srcIdx = gl.ReferenceMesh.SourceVertexIndices[v];
                Assert.True((gl.AnimatedMeshVertices[0][v] - expectedLocalVerts[srcIdx]).Length < 1e-3f,
                    $"Frame 0 skinned vertex {v} drifted: got {gl.AnimatedMeshVertices[0][v]}, expected {expectedLocalVerts[srcIdx]}");
            }

            // Frame 1 rotates intermediate non-mesh node 1 (Root/Point): verify world transform propagated to mesh bones & vertices
            var expectedNode1WorldF1 = MeshPreviewSkeletonBuilder.CreateLocalMatrix(localPos[1], qFrame1, localScale[1]);
            var expectedArmWorldF1 = MeshPreviewSkeletonBuilder.CreateLocalMatrix(localPos[2], localRot[2], localScale[2]) * expectedNode1WorldF1;
            var expectedHandWorldF1 = MeshPreviewSkeletonBuilder.CreateLocalMatrix(localPos[3], localRot[3], localScale[3]) * expectedArmWorldF1;
            Assert.True((frames[1][0] - expectedArmWorldF1.ExtractTranslation()).Length < 1e-3f);
            Assert.True((frames[1][1] - expectedHandWorldF1.ExtractTranslation()).Length < 1e-3f);

            var expectedSkinArmF1 = restWorlds[2].Inverted() * expectedArmWorldF1;
            var expectedSkinHandF1 = restWorlds[3].Inverted() * expectedHandWorldF1;
            for (int v = 0; v < 3; v++)
            {
                int srcIdx = gl.ReferenceMesh.SourceVertexIndices[v];
                var skinMat = srcIdx == 0 ? expectedSkinArmF1 : expectedSkinHandF1;
                var expectedPos = OpenTK.Mathematics.Vector3.TransformPosition(expectedLocalVerts[srcIdx], skinMat);
                Assert.True((gl.AnimatedMeshVertices[1][v] - expectedPos).Length < 1e-3f,
                    $"Frame 1 skinned vertex {v} mismatch: got {gl.AnimatedMeshVertices[1][v]}, expected {expectedPos}");
            }

            // 3. Verify EvaluateFramesFromTracks bind-pose local fallback (avatarDefaultPose == null) is NOT inverted:
            // With an identity root track, child bone world matrix must equal BindPoseInverses[1] (restWorlds[3]).
            var fallbackRotTracks = new Dictionary<int, List<(float time, OpenTK.Mathematics.Quaternion value)>>
            {
                [0] = new() { (0f, rig.Skeleton.BindPoseInverses[0].ExtractRotation()) }
            };
            var okFallback = MeshPreviewAnimationEvaluator.EvaluateFramesFromTracks(
                mesh,
                rig.Skeleton.RestBonePositions,
                rig.Skeleton.MeshParentIndices,
                rig.Skeleton.MeshBoneNames,
                rig.Skeleton.BindPoseInverses,
                rig.DeformChainMask,
                rig.WeightedBoneMask,
                new Dictionary<int, List<(float time, OpenTK.Mathematics.Vector3 value)>>(),
                fallbackRotTracks,
                new Dictionary<int, List<(float time, OpenTK.Mathematics.Vector3 value)>>(),
                sampleRate: 1f,
                maxTime: 1f,
                avatarDefaultPose: null,
                meshBoneToSkelNode: null,
                useCeilingFrameCount: false,
                failIfAllTracksFiltered: false,
                out var fbFrames,
                out var fbMatrices,
                out _,
                out _,
                out var fbReason);

            Assert.True(okFallback, fbReason);
            Assert.Single(fbFrames);
            Assert.True((fbMatrices[0][1].ExtractTranslation() - restWorlds[3].ExtractTranslation()).Length < 1e-3f,
                $"Bind-pose fallback child translation mismatch: got {fbMatrices[0][1].ExtractTranslation()}, expected {restWorlds[3].ExtractTranslation()}");
        }

        [Fact]
        public void AnimationPreviewSkinning_RealGameBaalSdAndRedLady_ProducesTightUndistortedMesh()
        {
            const string resPath = @"F:\SteamLibrary\steamapps\common\Anima Gate of Memories\AnimaGateOfMemories_Data\resources.assets";
            if (!File.Exists(resPath))
            {
                return;
            }

            var am = new AssetsManager();
            am.LoadFiles(resPath);
            var sf = am.assetsFileList[0];

            Mesh? baalMesh = null;
            Avatar? baalAvatar = null;
            AnimationClip? punchClip = null;
            Mesh? redLadyMesh = null;
            Avatar? redLadyAvatar = null;
            AnimationClip? chargeBeamClip = null;

            foreach (var info in sf.m_Objects)
            {
                if (info.m_PathID is not (1681 or 3367 or 2449 or 1631 or 3382 or 2799))
                {
                    continue;
                }

                var reader = new ObjectReader(sf.reader, sf, info);
                switch (info.classID)
                {
                    case (int)ClassIDType.Mesh:
                        var mesh = new Mesh(reader);
                        if (info.m_PathID == 1681) baalMesh = mesh;
                        else if (info.m_PathID == 1631) redLadyMesh = mesh;
                        break;
                    case (int)ClassIDType.Avatar:
                        var av = new Avatar(reader);
                        if (info.m_PathID == 3367) baalAvatar = av;
                        else if (info.m_PathID == 3382) redLadyAvatar = av;
                        break;
                    case (int)ClassIDType.AnimationClip:
                        var clip = new AnimationClip(reader);
                        if (info.m_PathID == 2449) punchClip = clip;
                        else if (info.m_PathID == 2799) chargeBeamClip = clip;
                        break;
                }
            }

            Assert.NotNull(baalMesh);
            Assert.NotNull(baalAvatar);
            Assert.NotNull(punchClip);

            void VerifyClipRig(string label, Avatar avatar, Mesh mesh, AnimationClip clip, int probeFrame, int minTracks, float maxAllowedTriEdge, float maxBoundsMarginRatio)
            {
                var rig = PreparedAnimationRigData.TryBuild(label, avatar, mesh);
                Assert.NotNull(rig);
                Assert.True(rig!.HasFullAvatarSkeleton);

                var defaultPose = avatar.m_Avatar.m_DefaultPose?.m_X ?? avatar.m_Avatar.m_AvatarSkeletonPose?.m_X;
                Assert.NotNull(defaultPose);
                Assert.NotEmpty(defaultPose!);

                var innerClip = clip.m_MuscleClip!.m_Clip;
                var bindings = clip.m_ClipBindingConstant ?? innerClip.ConvertValueArrayToGenericBinding();
                var tracks = new AnimationTrackSet();
                MeshPreviewAnimationEvaluator.ExtractMuscleClipTracks(
                    innerClip,
                    bindings,
                    clip.m_MuscleClip.m_StopTime,
                    rig.TryResolveAvatarNodeBinding,
                    rig.ShouldUseAvatarNodeBinding,
                    tracks,
                    updateMaxTimeBeforeBoundsCheck: true);
                tracks.SortAll();

                Assert.True(tracks.TotalTrackCount >= minTracks,
                    $"{label}: expected >= {minTracks} full-Avatar tracks, got {tracks.TotalTrackCount}");

                var sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
                var maxTime = tracks.MaxTime > 0 ? tracks.MaxTime : clip.m_MuscleClip.m_StopTime;
                var ok = MeshPreviewAnimationEvaluator.EvaluateFramesFromAvatarSkeletonTracks(
                    mesh,
                    rig.Skeleton,
                    defaultPose!,
                    rig.DeformChainMask,
                    rig.WeightedBoneMask,
                    tracks.PosTracks,
                    tracks.RotTracks,
                    tracks.ScaleTracks,
                    sampleRate,
                    maxTime,
                    useCeilingFrameCount: false,
                    failIfAllTracksFiltered: false,
                    out var frames,
                    out var boneMatrices,
                    out var renderParents,
                    out var resolvedFps,
                    out var reason);

                Assert.True(ok, $"{label}: {reason}");
                Assert.NotEmpty(frames);

                var gl = MeshPreviewControl.PrepareAnimatedAvatarGLData(
                    mesh,
                    frames,
                    boneMatrices,
                    renderParents,
                    resolvedFps,
                    rig.Skeleton.MeshBoneNames,
                    100f,
                    0.1f,
                    rig.SourceVertexData,
                    rig.SourceMeshMin,
                    rig.SourceMeshMax,
                    rig.GetOrBuildReferenceMesh(100f));

                Assert.NotNull(gl);
                Assert.NotNull(gl!.AnimatedMeshVertices);
                int f = Math.Clamp(probeFrame, 0, gl.AnimatedMeshVertices!.Length - 1);
                var verts = gl.AnimatedMeshVertices[f];
                var bones = frames[f];
                var indices = gl.ReferenceMesh.Indices;

                var vMin = new OpenTK.Mathematics.Vector3(float.MaxValue);
                var vMax = new OpenTK.Mathematics.Vector3(float.MinValue);
                foreach (var v in verts)
                {
                    Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), $"{label}: non-finite skinned vertex");
                    vMin = OpenTK.Mathematics.Vector3.ComponentMin(vMin, v);
                    vMax = OpenTK.Mathematics.Vector3.ComponentMax(vMax, v);
                }

                var bMin = new OpenTK.Mathematics.Vector3(float.MaxValue);
                var bMax = new OpenTK.Mathematics.Vector3(float.MinValue);
                foreach (var b in bones)
                {
                    bMin = OpenTK.Mathematics.Vector3.ComponentMin(bMin, b);
                    bMax = OpenTK.Mathematics.Vector3.ComponentMax(bMax, b);
                }

                float maxTriEdge = 0f;
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    var p0 = verts[indices[t]];
                    var p1 = verts[indices[t + 1]];
                    var p2 = verts[indices[t + 2]];
                    maxTriEdge = Math.Max(maxTriEdge, Math.Max((p0 - p1).Length, Math.Max((p1 - p2).Length, (p2 - p0).Length)));
                }

                var boneDiag = Math.Max(1e-3f, (bMax - bMin).Length);
                var allowedMargin = boneDiag * maxBoundsMarginRatio;
                _output.WriteLine($"{label} frame {f}/{frames.Length}: tracks={tracks.TotalTrackCount}, maxTriEdge={maxTriEdge:F2}, boneDiag={boneDiag:F2}, bones=({bMin.X:F1},{bMin.Y:F1},{bMin.Z:F1})..({bMax.X:F1},{bMax.Y:F1},{bMax.Z:F1}), verts=({vMin.X:F1},{vMin.Y:F1},{vMin.Z:F1})..({vMax.X:F1},{vMax.Y:F1},{vMax.Z:F1})");

                Assert.True(maxTriEdge < maxAllowedTriEdge,
                    $"{label}: max triangle edge {maxTriEdge:F2} exceeded {maxAllowedTriEdge:F2} (mesh tearing/explosion regression)");
                Assert.True(bMin.X - vMin.X < allowedMargin && vMax.X - bMax.X < allowedMargin
                    && bMin.Y - vMin.Y < allowedMargin && vMax.Y - bMax.Y < allowedMargin
                    && bMin.Z - vMin.Z < allowedMargin && vMax.Z - bMax.Z < allowedMargin,
                    $"{label}: skinned mesh bounds significantly overflow skeleton bounds (margin limit {allowedMargin:F2})");
            }

            // BOSS_baal_sd punch frame 123: previously maxTriEdge=234.35 with tracks=531; now maxTriEdge~15.52 with tracks=600
            VerifyClipRig("baal-sd/punch", baalAvatar!, baalMesh!, punchClip!, probeFrame: 123, minTracks: 590, maxAllowedTriEdge: 25f, maxBoundsMarginRatio: 0.12f);

            if (redLadyAvatar != null && redLadyMesh != null && chargeBeamClip != null)
            {
                VerifyClipRig("red-lady/chargebeam2", redLadyAvatar, redLadyMesh, chargeBeamClip, probeFrame: 30, minTracks: 100, maxAllowedTriEdge: 25f, maxBoundsMarginRatio: 0.35f);
            }
        }

        private static void SeedSyntheticV14Database(string dbPath, string projectDir, string signature, string sourcePath)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE CacheSchema (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO CacheSchema (Key, Value) VALUES ('SemanticSchemaVersion', '14');

                CREATE TABLE Projects (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FolderPath TEXT NOT NULL,
                    SignatureHash TEXT NOT NULL,
                    TotalFiles INTEGER NOT NULL,
                    TotalBytes INTEGER NOT NULL,
                    UnityBundleCount INTEGER NOT NULL,
                    LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UnityVersion TEXT
                );

                CREATE TABLE SourceFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    SerializedFileName TEXT NOT NULL,
                    OriginalPath TEXT NOT NULL DEFAULT '',
                    UnityVersion TEXT NOT NULL DEFAULT '',
                    ObjectCount INTEGER NOT NULL DEFAULT 0,
                    LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE Assets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    UniqueID TEXT NOT NULL,
                    SourceFileId INTEGER REFERENCES SourceFiles(Id) ON DELETE SET NULL,
                    Name TEXT NOT NULL,
                    Type INTEGER NOT NULL,
                    Container TEXT NOT NULL DEFAULT '',
                    PathID INTEGER NOT NULL,
                    ByteStart INTEGER NOT NULL,
                    ByteSize INTEGER NOT NULL,
                    PRIMARY KEY (ProjectId, UniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE PreviewCacheEntries (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    AssetUniqueID TEXT NOT NULL,
                    PreviewKind TEXT NOT NULL,
                    AlgorithmVersion INTEGER NOT NULL,
                    Parameters TEXT NOT NULL DEFAULT '',
                    PayloadHash TEXT NOT NULL,
                    PayloadPath TEXT,
                    ByteSize INTEGER NOT NULL DEFAULT 0,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedUtc TEXT NOT NULL DEFAULT '',
                    LastAccessedUtc TEXT NOT NULL DEFAULT '',
                    PayloadBlob BLOB,
                    PayloadCompression INTEGER NOT NULL DEFAULT 0,
                    UncompressedByteSize INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE ProjectIndexingState (
                    ProjectId INTEGER PRIMARY KEY REFERENCES Projects(Id) ON DELETE CASCADE,
                    Status TEXT NOT NULL DEFAULT 'not_started',
                    TotalFiles INTEGER NOT NULL DEFAULT 0,
                    ProcessedFiles INTEGER NOT NULL DEFAULT 0,
                    PendingFiles INTEGER NOT NULL DEFAULT 0,
                    PercentComplete REAL NOT NULL DEFAULT 0,
                    CurrentFile TEXT NOT NULL DEFAULT '',
                    LastReadFile TEXT NOT NULL DEFAULT '',
                    StartedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    CompletedAt DATETIME
                );

                CREATE TABLE IndexingReadFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    FilePath TEXT NOT NULL,
                    FileName TEXT NOT NULL DEFAULT '',
                    ReadOrder INTEGER NOT NULL DEFAULT 0,
                    ReadAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Status TEXT NOT NULL DEFAULT 'read'
                );

                CREATE TABLE AssetEdges (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    SourceAssetUniqueID TEXT NOT NULL,
                    EdgeKind TEXT NOT NULL,
                    SlotName TEXT NOT NULL DEFAULT '',
                    SlotIndex INTEGER NOT NULL DEFAULT -1,
                    TargetAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SourceFileId INTEGER NOT NULL DEFAULT 0,
                    SourcePathID INTEGER NOT NULL DEFAULT 0,
                    TargetFileId INTEGER NOT NULL DEFAULT 0,
                    TargetPathID INTEGER NOT NULL DEFAULT 0,
                    IsResolved INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetFileId, TargetPathID)
                ) WITHOUT ROWID;

                CREATE TABLE ModelGroups (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    GroupAssetUniqueID TEXT NOT NULL,
                    GroupKind TEXT NOT NULL DEFAULT '',
                    GroupName TEXT NOT NULL DEFAULT '',
                    RootGameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RootGameObjectName TEXT NOT NULL DEFAULT '',
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ControllerAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SourceFileName TEXT NOT NULL DEFAULT '',
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    ConfidenceReason TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE ModelGroupMeshes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    GroupAssetUniqueID TEXT NOT NULL,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    GameObjectAssetUniqueID TEXT NOT NULL DEFAULT '',
                    GameObjectName TEXT NOT NULL DEFAULT '',
                    SlotIndex INTEGER NOT NULL DEFAULT -1,
                    TransformMatrix BLOB,
                    Confidence INTEGER NOT NULL DEFAULT 0,
                    ConfidenceReason TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE MeshRenderers (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL,
                    RendererType TEXT NOT NULL,
                    GameObjectAssetUniqueID TEXT,
                    GameObjectName TEXT,
                    Description TEXT,
                    PRIMARY KEY (ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, RendererType)
                ) WITHOUT ROWID;

                CREATE TABLE MeshMaterials (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MeshAssetUniqueID TEXT NOT NULL,
                    RendererAssetUniqueID TEXT NOT NULL DEFAULT '',
                    SubMeshIndex INTEGER NOT NULL,
                    MaterialSlotIndex INTEGER NOT NULL DEFAULT -1,
                    MaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    RendererType TEXT NOT NULL DEFAULT '',
                    MaterialScore INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, MeshAssetUniqueID, RendererAssetUniqueID, SubMeshIndex, MaterialSlotIndex, MaterialAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE MaterialTextures (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    MaterialAssetUniqueID TEXT NOT NULL,
                    SlotName TEXT NOT NULL,
                    SlotIndex INTEGER NOT NULL,
                    TextureFileId INTEGER NOT NULL DEFAULT 0,
                    TexturePathID INTEGER NOT NULL DEFAULT 0,
                    TextureAssetUniqueID TEXT NOT NULL DEFAULT '',
                    PreviewMaterialAssetUniqueID TEXT NOT NULL DEFAULT '',
                    IsResolved INTEGER NOT NULL DEFAULT 0,
                    IsMainTexture INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE AnimationClipTargets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    ClipAssetUniqueID TEXT NOT NULL,
                    AnimatorAssetUniqueID TEXT NOT NULL DEFAULT '',
                    AvatarAssetUniqueID TEXT NOT NULL DEFAULT '',
                    MeshAssetUniqueID TEXT NOT NULL DEFAULT '',
                    ModelGroupId TEXT NOT NULL DEFAULT '',
                    RelationKind TEXT NOT NULL DEFAULT '',
                    MatchScore INTEGER NOT NULL DEFAULT 0,
                    MatchedBoneCount INTEGER NOT NULL DEFAULT 0,
                    TotalClipBoneCount INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, ClipAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, AnimatorAssetUniqueID, RelationKind)
                ) WITHOUT ROWID;

                INSERT INTO Projects (Id, FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                VALUES (1, @folder, @sig, 1, 64, 1, '2022.3.10f1');

                INSERT INTO ProjectIndexingState (ProjectId, Status, TotalFiles, ProcessedFiles, PendingFiles, PercentComplete, CurrentFile, LastReadFile, CompletedAt)
                VALUES (1, 'structure_completed', 1, 1, 0, 100.0, 'Asset structure ready', 'level0.assets', CURRENT_TIMESTAMP);

                INSERT INTO SourceFiles (Id, ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                VALUES (1, 1, 'level0.assets', @sourcePath, '2022.3.10f1', 6);

                INSERT INTO Assets (ProjectId, UniqueID, SourceFileId, Name, Type, Container, PathID, ByteStart, ByteSize) VALUES
                    (1, 'mesh_hero', 1, 'HeroBody', 43, '', 100, 0, 1024),
                    (1, 'mesh_hair', 1, 'HeroHair', 43, '', 101, 0, 512),
                    (1, 'mat_hero', 1, 'HeroMat', 21, '', 200, 0, 256),
                    (1, 'tex_main', 1, 'HeroDiffuse', 28, '', 300, 0, 4096),
                    (1, 'avatar_hero', 1, 'HeroAvatar', 90, '', 400, 0, 512),
                    (1, 'clip_attack', 1, 'HeroAttack', 74, '', 500, 0, 512);

                INSERT INTO PreviewCacheEntries (ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize, PayloadBlob, PayloadCompression, UncompressedByteSize, UpdatedUtc, LastAccessedUtc)
                VALUES (1, 'tex_main', 'texture-thumbnail-png', 1, 'max=512|w=64', 'hash_v14_preserved', '', 4, X'01020304', 0, 4, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO MaterialTextures (ProjectId, MaterialAssetUniqueID, PreviewMaterialAssetUniqueID, SlotName, SlotIndex, TextureAssetUniqueID, TextureFileId, TexturePathID, IsResolved, IsMainTexture)
                VALUES (1, 'mat_hero', 'mat_hero', '_MainTex', 0, 'tex_main', 0, 300, 1, 1);

                INSERT INTO MeshMaterials (ProjectId, MeshAssetUniqueID, MaterialAssetUniqueID, RendererAssetUniqueID, RendererType, SubMeshIndex, MaterialSlotIndex, MaterialScore)
                VALUES (1, 'mesh_hero', 'mat_hero', 'ren_hero', 'SkinnedMeshRenderer', 0, 0, 10);

                INSERT INTO ModelGroups (ProjectId, GroupAssetUniqueID, GroupKind, GroupName, RootGameObjectAssetUniqueID, RootGameObjectName, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID, SourceFileName, Confidence, ConfidenceReason)
                VALUES (1, 'modelgroup:animator:anim_hero', 'Animator', 'Hero', 'go_hero', 'Hero', 'anim_hero', 'avatar_hero', 'ctrl_hero', 'level0.assets', 100, 'descendant');

                INSERT INTO ModelGroupMeshes (ProjectId, GroupAssetUniqueID, MeshAssetUniqueID, RendererAssetUniqueID, RendererType, GameObjectAssetUniqueID, GameObjectName, SlotIndex, TransformMatrix, Confidence, ConfidenceReason) VALUES
                    (1, 'modelgroup:animator:anim_hero', 'mesh_hero', 'ren_hero', 'SkinnedMeshRenderer', 'go_body', 'Body', 0, NULL, 100, 'descendant'),
                    (1, 'modelgroup:animator:anim_hero', 'mesh_hair', 'ren_hair', 'SkinnedMeshRenderer', 'go_hair', 'Hair', 1, NULL, 100, 'descendant');

                INSERT INTO AnimationClipTargets (ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount) VALUES
                    (1, 'clip_attack', 'anim_hero', 'avatar_hero', 'mesh_hero', 'modelgroup:animator:anim_hero', 'AnimatorController', 10000, 0, 0),
                    (1, 'clip_empty_ghost', 'anim_empty', '', '', '', 'AnimatorController', 10000, 0, 0);";
            cmd.Parameters.AddWithValue("@folder", Path.GetFullPath(projectDir));
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@sourcePath", sourcePath);
            cmd.ExecuteNonQuery();
        }

        private static AssetHandle CreateHandle(string fileName, string originalPath, long pathId, ClassIDType type, string name, string uniqueId)
        {
            return new AssetHandle
            {
                UniqueID = uniqueId,
                SerializedFileName = fileName,
                OriginalPath = originalPath,
                PathID = pathId,
                Type = type,
                Name = name,
                ByteSize = 128
            };
        }

        private static void AssertZeroPhysicalTableScans(SqliteConnection conn, string sql, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            cmd.Parameters.AddWithValue("@p", projectId);
            var lines = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var detail = reader.GetString(3);
                lines.Add(detail);
                Assert.DoesNotContain("SCAN ", detail, StringComparison.OrdinalIgnoreCase);
            }
            Assert.NotEmpty(lines);
        }

        private static long ExecuteScalarInt64(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var val = cmd.ExecuteScalar();
            return val == null || val == DBNull.Value ? 0L : Convert.ToInt64(val, CultureInfo.InvariantCulture);
        }

        private static string ExecuteScalarString(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }

        private static HashSet<string> GetColumnNames(SqliteConnection conn, string tableName)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cols.Add(reader.GetString(1));
            }
            return cols;
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
