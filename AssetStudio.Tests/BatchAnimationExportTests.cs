using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class BatchAnimationExportTests
    {
        [Fact]
        public void ExportMultiPartModelGroupWithThreeAnimationClips_SingleGlb_EmbedsAllThreeAnimationsOnUnifiedJointsAndPreservesTextures()
        {
            var rig = BuildSyntheticMultiPartRigWithThreeClips();

            var converter = ModelConverter.CreateFromModelGroup(
                "HeroCharacter",
                rootGameObject: null,
                meshes: new[] { rig.BodyMesh, rig.ArmorMesh },
                renderers: new Renderer[] { rig.BodySmr, rig.ArmorSmr },
                avatar: rig.Avatar,
                animationClips: new[] { rig.IdleClip, rig.RunClip, rig.AttackClip },
                imageFormat: ImageFormat.Png,
                combineMeshes: false,
                partTransforms: null,
                partMaterials: new IReadOnlyList<Material>[]
                {
                    new[] { rig.BodyMaterial },
                    new[] { rig.ArmorMaterial }
                },
                partNames: new[] { "HeroBody", "HeroArmor" });

            // 1. Single unified skeleton: zero duplicate Hips or Spine nodes under RootFrame
            Assert.Single(converter.RootFrame.FindChilds("Hips").ToList());
            Assert.Single(converter.RootFrame.FindChilds("Spine").ToList());
            Assert.Single(converter.RootFrame.FindChilds("RightArm").ToList());

            // 2. Both parts converted with materials and textures intact
            Assert.Equal(2, converter.MeshList.Count);
            Assert.Equal(2, converter.MaterialList.Count);
            Assert.Equal(2, converter.TextureList.Count);
            Assert.Equal(3, converter.AnimationList.Count);

            var tempDir = CreateTempDirectory("BatchAnimSingleGlb");
            try
            {
                var options = new ExportOptionsState
                {
                    ModelFormat = ModelExportFormat.Glb,
                    ExportSkins = true,
                    ExportAnimations = true,
                    SplitAnimations = false,
                    ExportBlendShape = true,
                    ExportUnityMeta = false,
                    ExportUnityAnimClips = false
                };

                var glbPath = Path.Combine(tempDir, "HeroCharacter.glb");
                var exportedFiles = new List<string>();
                AssetExportService.ExportModel(converter, glbPath, options, exportedFilePaths: exportedFiles);

                Assert.Single(exportedFiles);
                Assert.True(File.Exists(glbPath));

                var doc = ValidateGlbJson(glbPath);
                var root = doc.RootElement;

                Assert.Equal(2, root.GetProperty("meshes").GetArrayLength());
                Assert.Equal(2, root.GetProperty("materials").GetArrayLength());
                Assert.Equal(2, root.GetProperty("textures").GetArrayLength());
                Assert.True(root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() >= 1);

                var allSkinJoints = new HashSet<int>();
                foreach (var skin in skins.EnumerateArray())
                {
                    foreach (var joint in skin.GetProperty("joints").EnumerateArray())
                    {
                        allSkinJoints.Add(joint.GetInt32());
                    }
                }

                // Verify no duplicate skeleton joint node names in glTF nodes
                var jointNodeNames = allSkinJoints
                    .Select(idx => root.GetProperty("nodes")[idx].GetProperty("name").GetString() ?? string.Empty)
                    .ToList();
                Assert.Equal(jointNodeNames.Count, jointNodeNames.Distinct(StringComparer.Ordinal).Count());

                // Verify 3 distinct animations in root.animations, each targeting actual skinned joints
                Assert.True(root.TryGetProperty("animations", out var animations));
                Assert.Equal(3, animations.GetArrayLength());

                var animNames = new List<string>();
                foreach (var anim in animations.EnumerateArray())
                {
                    var animName = anim.GetProperty("name").GetString() ?? string.Empty;
                    animNames.Add(animName);

                    var channels = anim.GetProperty("channels").EnumerateArray().ToList();
                    Assert.NotEmpty(channels);
                    foreach (var channel in channels)
                    {
                        var targetNodeIdx = channel.GetProperty("target").GetProperty("node").GetInt32();
                        Assert.Contains(targetNodeIdx, allSkinJoints);
                    }
                }

                Assert.Contains("Hero_Idle", animNames);
                Assert.Contains("Hero_Run", animNames);
                Assert.Contains("Hero_Attack", animNames);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ExportMultiPartModelGroupWithThreeAnimationClips_SplitAnimationsTrue_ProducesBaseModelAndThreeValidSplitGlbFiles()
        {
            var rig = BuildSyntheticMultiPartRigWithThreeClips();

            var converter = ModelConverter.CreateFromModelGroup(
                "HeroCharacter",
                rootGameObject: null,
                meshes: new[] { rig.BodyMesh, rig.ArmorMesh },
                renderers: new Renderer[] { rig.BodySmr, rig.ArmorSmr },
                avatar: rig.Avatar,
                animationClips: new[] { rig.IdleClip, rig.RunClip, rig.AttackClip },
                imageFormat: ImageFormat.Png,
                combineMeshes: false,
                partTransforms: null,
                partMaterials: new IReadOnlyList<Material>[]
                {
                    new[] { rig.BodyMaterial },
                    new[] { rig.ArmorMaterial }
                },
                partNames: new[] { "HeroBody", "HeroArmor" });

            var tempDir = CreateTempDirectory("BatchAnimSplitGlb");
            try
            {
                var options = new ExportOptionsState
                {
                    ModelFormat = ModelExportFormat.Glb,
                    ExportSkins = true,
                    ExportAnimations = true,
                    SplitAnimations = true,
                    ExportBlendShape = true,
                    ExportUnityMeta = false,
                    ExportUnityAnimClips = false
                };

                Assert.True(options.ExportAnimationsSplit);
                Assert.True(options.SplitAnimations);

                var mainPath = Path.Combine(tempDir, "HeroCharacter.glb");
                var exportedFiles = new List<string>();
                AssetExportService.ExportModel(converter, mainPath, options, exportedFilePaths: exportedFiles);

                // Base model + 3 split animation files = 4 files total
                Assert.Equal(4, exportedFiles.Count);
                Assert.True(File.Exists(mainPath));

                var baseDoc = ValidateGlbJson(mainPath);
                Assert.Equal(2, baseDoc.RootElement.GetProperty("meshes").GetArrayLength());
                Assert.Equal(2, baseDoc.RootElement.GetProperty("materials").GetArrayLength());
                Assert.False(baseDoc.RootElement.TryGetProperty("animations", out _));

                var expectedClips = new[] { "Hero_Idle", "Hero_Run", "Hero_Attack" };
                foreach (var clipName in expectedClips)
                {
                    var splitPath = Path.Combine(tempDir, $"HeroCharacter_{clipName}.glb");
                    Assert.True(File.Exists(splitPath), $"Expected split animation GLB at {splitPath}");
                    Assert.Contains(splitPath, exportedFiles);

                    var splitDoc = ValidateGlbJson(splitPath);
                    var splitRoot = splitDoc.RootElement;
                    Assert.Equal(2, splitRoot.GetProperty("meshes").GetArrayLength());
                    Assert.True(splitRoot.TryGetProperty("skins", out var skins) && skins.GetArrayLength() >= 1);

                    var skinJoints = new HashSet<int>();
                    foreach (var skin in skins.EnumerateArray())
                    {
                        foreach (var j in skin.GetProperty("joints").EnumerateArray())
                        {
                            skinJoints.Add(j.GetInt32());
                        }
                    }

                    Assert.True(splitRoot.TryGetProperty("animations", out var anims));
                    Assert.Equal(1, anims.GetArrayLength());
                    Assert.Equal(clipName, anims[0].GetProperty("name").GetString());

                    var channels = anims[0].GetProperty("channels").EnumerateArray().ToList();
                    Assert.NotEmpty(channels);
                    foreach (var ch in channels)
                    {
                        var nodeIdx = ch.GetProperty("target").GetProperty("node").GetInt32();
                        Assert.Contains(nodeIdx, skinJoints);
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void BatchAnimationExportHelper_ResolveAssetListSelection_HandlesAnimatorMeshAvatarSmrGameObjectAndClipsOnly()
        {
            var rig = BuildSyntheticMultiPartRigWithThreeClips();

            var animatorItem = CreateAssetItem(rig.Animator, ClassIDType.Animator, "HeroRoot");
            var avatarItem = CreateAssetItem(rig.Avatar, ClassIDType.Avatar, "HeroAvatar");
            var bodyMeshItem = CreateAssetItem(rig.BodyMesh, ClassIDType.Mesh, "HeroBody");
            var armorMeshItem = CreateAssetItem(rig.ArmorMesh, ClassIDType.Mesh, "HeroArmor");
            var smrItem = CreateAssetItem(rig.BodySmr, ClassIDType.SkinnedMeshRenderer, "HeroBodySmr");
            var goItem = CreateAssetItem(rig.RootGo, ClassIDType.GameObject, "HeroRootGo");

            var idleItem = CreateAssetItem(rig.IdleClip, ClassIDType.AnimationClip, "Hero_Idle");
            var runItem = CreateAssetItem(rig.RunClip, ClassIDType.AnimationClip, "Hero_Run");
            var attackItem = CreateAssetItem(rig.AttackClip, ClassIDType.AnimationClip, "Hero_Attack");

            // 1. Animator + 3 clips
            var animSel = new[] { animatorItem, idleItem, runItem, attackItem };
            Assert.True(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(animSel));
            var animRes = BatchAnimationExportHelper.ResolveAssetListSelection(animSel);
            Assert.True(animRes.IsValid);
            Assert.Equal(BatchAnimationSelectionKind.ExplicitAnimator, animRes.Kind);
            Assert.Same(rig.Animator, animRes.Animator);
            Assert.Equal(3, animRes.SelectedClips.Count);
            Assert.Equal("HeroRoot", animRes.SuggestedBaseName);

            // 2. Mesh + 2 clips
            var meshSel = new[] { bodyMeshItem, armorMeshItem, idleItem, runItem };
            Assert.True(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(meshSel));
            var meshRes = BatchAnimationExportHelper.ResolveAssetListSelection(meshSel);
            Assert.True(meshRes.IsValid);
            Assert.Equal(BatchAnimationSelectionKind.ExplicitMesh, meshRes.Kind);
            Assert.Same(rig.BodyMesh, meshRes.PrimaryMesh);
            Assert.Equal(2, meshRes.SelectedMeshes.Count);
            Assert.Equal(2, meshRes.SelectedClips.Count);
            Assert.Equal("HeroBody", meshRes.SuggestedBaseName);

            // 3. Avatar + 3 clips
            var avatarSel = new[] { avatarItem, idleItem, runItem, attackItem };
            Assert.True(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(avatarSel));
            var avatarRes = BatchAnimationExportHelper.ResolveAssetListSelection(avatarSel);
            Assert.True(avatarRes.IsValid);
            Assert.Equal(BatchAnimationSelectionKind.ExplicitAvatar, avatarRes.Kind);
            Assert.Same(rig.Avatar, avatarRes.Avatar);
            Assert.Equal(3, avatarRes.SelectedClips.Count);

            // 4. SkinnedMeshRenderer + 2 clips
            var smrSel = new[] { smrItem, idleItem, attackItem };
            Assert.True(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(smrSel));
            var smrRes = BatchAnimationExportHelper.ResolveAssetListSelection(smrSel);
            Assert.True(smrRes.IsValid);
            Assert.Equal(BatchAnimationSelectionKind.ExplicitSkinnedMeshRenderer, smrRes.Kind);
            Assert.Same(rig.BodySmr, smrRes.SkinnedMeshRenderer);
            Assert.Same(rig.BodyMesh, smrRes.PrimaryMesh);

            // 5. GameObject + 2 clips
            var goSel = new[] { goItem, runItem, attackItem };
            Assert.True(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(goSel));
            var goRes = BatchAnimationExportHelper.ResolveAssetListSelection(goSel);
            Assert.True(goRes.IsValid);
            Assert.Equal(BatchAnimationSelectionKind.ExplicitGameObject, goRes.Kind);
            Assert.Same(rig.RootGo, goRes.RootGameObject);

            // 6. Multiple AnimationClips selected alone -> ClipsOnly + prefix extraction
            var clipsOnlySel = new[] { idleItem, runItem, attackItem };
            Assert.True(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(clipsOnlySel));
            var clipsOnlyRes = BatchAnimationExportHelper.ResolveAssetListSelection(clipsOnlySel);
            Assert.True(clipsOnlyRes.IsValid);
            Assert.Equal(BatchAnimationSelectionKind.ClipsOnly, clipsOnlyRes.Kind);
            Assert.Equal(3, clipsOnlyRes.SelectedClips.Count);
            Assert.Equal("Hero", clipsOnlyRes.SuggestedBaseName);

            // 7. Selection without any AnimationClips -> invalid
            var noClipsSel = new[] { animatorItem, bodyMeshItem };
            Assert.False(BatchAnimationExportHelper.CanExportSelectionWithAnimationClips(noClipsSel));
            Assert.False(BatchAnimationExportHelper.ResolveAssetListSelection(noClipsSel).IsValid);
        }

        [Fact]
        public void BatchAnimationExportHelper_SelectSharedPreviewCandidateForClips_PrefersSharedMultiPartModelGroup()
        {
            var rig = BuildSyntheticMultiPartRigWithThreeClips();

            var sharedGroupCandidate = new PreviewCandidateItem
            {
                Avatar = rig.Avatar,
                Mesh = rig.BodyMesh,
                AvatarId = "main.assets#600",
                MeshId = "main.assets#200",
                ModelGroupId = "modelgroup:animator:main.assets#500",
                ModelGroupName = "HeroCharacter",
                ModelGroupMeshIds = new[] { "main.assets#200", "main.assets#201" },
                ModelGroupMeshes = new[] { rig.BodyMesh, rig.ArmorMesh },
                ModelGroupMeshCount = 2,
                ModelGroupConfidence = 100,
                Label = "Model group: HeroCharacter (2 parts)"
            };

            var singlePartNoiseCandidate = new PreviewCandidateItem
            {
                Mesh = rig.BodyMesh,
                MeshId = "main.assets#200",
                Label = "Single mesh: HeroBody"
            };

            var chosen = BatchAnimationExportHelper.SelectSharedPreviewCandidateForClips(
                new[] { rig.IdleClip, rig.RunClip, rig.AttackClip },
                clip => clip == rig.IdleClip
                    ? new[] { singlePartNoiseCandidate, sharedGroupCandidate }
                    : new[] { sharedGroupCandidate });

            Assert.NotNull(chosen);
            Assert.True(chosen!.IsModelGroup);
            Assert.Equal("HeroCharacter", chosen.ModelGroupName);
            Assert.Equal(2, chosen.ModelGroupMeshCount);
        }

        [Fact]
        public void BatchAnimationExportViewModel_SearchFilterSelectAllDeselectAllModeToggleAndConfirm_WorkCleanly()
        {
            var rig = BuildSyntheticMultiPartRigWithThreeClips();

            var items = new[]
            {
                new BatchAnimationClipItemViewModel("Hero_Idle", "main#700", 700, "AnimatorController", 10000, clip: rig.IdleClip),
                new BatchAnimationClipItemViewModel("Hero_Run", "main#701", 701, "Bone match", 9000, clip: rig.RunClip),
                new BatchAnimationClipItemViewModel("Hero_Attack", "main#702", 702, "Bone match", 8800, clip: rig.AttackClip)
            };

            // Verify timing extraction on items
            Assert.All(items, item =>
            {
                Assert.True(item.DurationSeconds > 0f);
                Assert.True(item.FrameCount >= 2);
                Assert.True(item.HasTimingInfo);
                Assert.Contains("fps", item.DurationAndFramesText, StringComparison.Ordinal);
            });

            var initialOptions = new ExportOptionsState
            {
                ModelFormat = ModelExportFormat.Glb,
                SplitAnimations = false
            };

            var vm = new BatchAnimationExportViewModel("HeroCharacter (2 parts)", items, initialOptions);
            Assert.Equal("HeroCharacter (2 parts)", vm.TargetDisplayName);
            Assert.Equal(3, vm.TotalCount);
            Assert.Equal(3, vm.VisibleCount);
            Assert.Equal(3, vm.SelectedCount);
            Assert.True(vm.CanConfirm);
            Assert.True(vm.IsSingleFileMode);
            Assert.False(vm.IsSplitFolderMode);
            Assert.Equal(ModelExportFormat.Glb, vm.ModelFormat);

            // Deselect All clears all 3 items and disables ConfirmCommand
            vm.DeselectAllCommand.Execute(null);
            Assert.Equal(0, vm.SelectedCount);
            Assert.False(vm.CanConfirm);
            Assert.False(vm.ConfirmCommand.CanExecute(null));

            // Filter to "Run", Select All only selects the filtered visible item
            vm.SearchText = "Run";
            Assert.Equal(1, vm.VisibleCount);
            vm.SelectAllCommand.Execute(null);
            Assert.Equal(1, vm.SelectedCount);
            Assert.True(vm.CanConfirm);

            // Clear filter -> 3 visible, 1 selected
            vm.SearchText = string.Empty;
            Assert.Equal(3, vm.VisibleCount);
            Assert.Equal(1, vm.SelectedCount);

            // Select All -> all 3 selected
            vm.SelectAllCommand.Execute(null);
            Assert.Equal(3, vm.SelectedCount);

            // Toggle to Split into folder mode and glTF format
            vm.IsSplitFolderMode = true;
            Assert.True(vm.SplitAnimations);
            Assert.False(vm.IsSingleFileMode);
            vm.ModelFormat = ModelExportFormat.Gltf;
            Assert.Equal(".gltf", vm.ModelExtensionText);

            bool? closedConfirmed = null;
            vm.RequestClose += confirmed => closedConfirmed = confirmed;
            vm.ConfirmCommand.Execute(null);

            Assert.True(closedConfirmed);
            Assert.NotNull(vm.Result);
            Assert.Equal(3, vm.Result!.SelectedClips.Count);
            Assert.True(vm.Result.SplitAnimations);
            Assert.Equal(ModelExportFormat.Gltf, vm.Result.ModelFormat);
        }

        [Fact]
        public async Task MainWindowViewModel_BatchExportPreviewAnimationsCommand_Tracks3DSurfaceAndDispatches()
        {
            using var vm = new MainWindowViewModel();

            Assert.False(vm.CanExportPreviewModel);
            Assert.False(vm.BatchExportPreviewAnimationsCommand.CanExecute());

            var canExecuteChanged = 0;
            vm.BatchExportPreviewAnimationsCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Animation3D);
            Assert.True(vm.CanExportPreviewModel);
            Assert.True(vm.BatchExportPreviewAnimationsCommand.CanExecute());
            Assert.True(canExecuteChanged > 0);

            var dispatched = 0;
            vm.CommandHandlers.BatchExportPreviewAnimationsAsync = () =>
            {
                dispatched++;
                return Task.CompletedTask;
            };

            await vm.BatchExportPreviewAnimationsCommand.ExecuteAsync();
            Assert.Equal(1, dispatched);

            // Verify XAML wiring & pure MVVM (zero Click= attributes in MainWindow.axaml and BatchAnimationExportWindow.axaml)
            var repoRoot = LocateRepositoryRoot();
            var mainAxaml = File.ReadAllText(Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml"));
            Assert.Contains("x:Name=\"BatchExportAnimationsBtn\"", mainAxaml, StringComparison.Ordinal);
            Assert.Contains("Command=\"{Binding BatchExportPreviewAnimationsCommand}\"", mainAxaml, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\bClick\s*=", RegexOptions.CultureInvariant), mainAxaml);

            var batchAxaml = File.ReadAllText(Path.Combine(repoRoot, "AssetStudio.Avalonia", "BatchAnimationExportWindow.axaml"));
            Assert.Contains("x:Name=\"ConfirmBatchExportButton\"", batchAxaml, StringComparison.Ordinal);
            Assert.Contains("Command=\"{Binding ConfirmCommand}\"", batchAxaml, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\bClick\s*=", RegexOptions.CultureInvariant), batchAxaml);
        }

        [Fact]
        public void SQLiteProjectIndexCache_LoadCompatibleAnimationClipsForTarget_ReturnsDeduplicatedClipsOrderedByScore()
        {
            var tempRoot = CreateTempDirectory("BatchAnimSqlite");
            var projectDir = Path.Combine(tempRoot, "ProjectData");
            var cacheDir = Path.Combine(tempRoot, "Cache");
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(cacheDir);

            try
            {
                var sourceFilePath = Path.Combine(projectDir, "sharedassets0.assets");
                File.WriteAllBytes(sourceFilePath, new byte[64]);
                var scanResult = ProjectScanner.ScanFolder(projectDir);
                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var signature = cache.GetFolderSignature(scanResult);
                var dbPath = cache.GetDbPath(projectDir);

                var handles = new List<AssetHandle>
                {
                    new AssetHandle
                    {
                        UniqueID = "sharedassets0.assets#400",
                        SerializedFileName = "sharedassets0.assets",
                        OriginalPath = sourceFilePath,
                        PathID = 400,
                        Type = ClassIDType.Avatar,
                        Name = "Hero_Avatar",
                        ByteSize = 512
                    },
                    new AssetHandle
                    {
                        UniqueID = "sharedassets0.assets#300",
                        SerializedFileName = "sharedassets0.assets",
                        OriginalPath = sourceFilePath,
                        PathID = 300,
                        Type = ClassIDType.Mesh,
                        Name = "Hero_BodyMesh",
                        ByteSize = 1024
                    },
                    new AssetHandle
                    {
                        UniqueID = "sharedassets0.assets#701",
                        SerializedFileName = "sharedassets0.assets",
                        OriginalPath = sourceFilePath,
                        PathID = 701,
                        Type = ClassIDType.AnimationClip,
                        Name = "Hero_Idle",
                        ByteSize = 512
                    },
                    new AssetHandle
                    {
                        UniqueID = "sharedassets0.assets#702",
                        SerializedFileName = "sharedassets0.assets",
                        OriginalPath = sourceFilePath,
                        PathID = 702,
                        Type = ClassIDType.AnimationClip,
                        Name = "Hero_Run",
                        ByteSize = 512
                    },
                    new AssetHandle
                    {
                        UniqueID = "sharedassets0.assets#703",
                        SerializedFileName = "sharedassets0.assets",
                        OriginalPath = sourceFilePath,
                        PathID = 703,
                        Type = ClassIDType.AnimationClip,
                        Name = "Hero_Attack",
                        ByteSize = 512
                    }
                };

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    long projectId;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Id FROM Projects LIMIT 1;";
                        projectId = Convert.ToInt64(cmd.ExecuteScalar());
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            INSERT INTO AnimationClipTargets (ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore, MatchedBoneCount, TotalClipBoneCount)
                            VALUES
                                (@pid, 'sharedassets0.assets#701', 'sharedassets0.assets#601', 'sharedassets0.assets#400', 'sharedassets0.assets#300', 'modelgroup:animator:hero', 'AnimatorController', 9800, 24, 24),
                                (@pid, 'sharedassets0.assets#702', '', 'sharedassets0.assets#400', '', 'modelgroup:animator:hero', 'BoneHashMatch', 8200, 20, 24);

                            INSERT INTO AssetEdges (ProjectId, SourceAssetUniqueID, EdgeKind, SlotName, SlotIndex, TargetAssetUniqueID, SourceFileId, SourcePathID, TargetFileId, TargetPathID, IsResolved)
                            VALUES
                                (@pid, 'sharedassets0.assets#900', 'AnimationClip', 'ControllerClip', 0, 'sharedassets0.assets#703', 0, 900, 0, 703, 1);";
                        cmd.Parameters.AddWithValue("@pid", projectId);
                        cmd.ExecuteNonQuery();
                    }
                }

                var compatible = cache.LoadCompatibleAnimationClipsForTarget(
                    projectDir,
                    signature,
                    avatarAssetId: "sharedassets0.assets#400",
                    modelGroupId: "modelgroup:animator:hero",
                    animatorAssetId: "sharedassets0.assets#601",
                    controllerAssetId: "sharedassets0.assets#900",
                    meshAssetIds: new[] { "sharedassets0.assets#300" });

                Assert.Equal(3, compatible.Count);
                Assert.Equal("Hero_Attack", compatible[0].ClipName);
                Assert.Equal("Hero_Idle", compatible[1].ClipName);
                Assert.Equal("Hero_Run", compatible[2].ClipName);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private sealed class SyntheticMultiPartRig
        {
            public required AssetsManager Manager { get; init; }
            public required SerializedFile MainFile { get; init; }
            public required GameObject RootGo { get; init; }
            public required Animator Animator { get; init; }
            public required Avatar Avatar { get; init; }
            public required Mesh BodyMesh { get; init; }
            public required Mesh ArmorMesh { get; init; }
            public required SkinnedMeshRenderer BodySmr { get; init; }
            public required SkinnedMeshRenderer ArmorSmr { get; init; }
            public required Material BodyMaterial { get; init; }
            public required Material ArmorMaterial { get; init; }
            public required AnimationClip IdleClip { get; init; }
            public required AnimationClip RunClip { get; init; }
            public required AnimationClip AttackClip { get; init; }
        }

        private static SyntheticMultiPartRig BuildSyntheticMultiPartRigWithThreeClips()
        {
            var manager = new AssetsManager();
            var mainFile = CreateSyntheticSerializedFile(manager, "character_main.assets");
            manager.assetsFileList.Add(mainFile);

            var rootGo = CreateSyntheticGameObject(mainFile, 10, "HeroRoot", out var rootTr);
            _ = CreateSyntheticGameObject(mainFile, 11, "Hips", out var hipsTr, rootTr, new Vector3(0, 1f, 0));
            _ = CreateSyntheticGameObject(mainFile, 12, "Spine", out var spineTr, hipsTr, new Vector3(0, 0.5f, 0));
            _ = CreateSyntheticGameObject(mainFile, 13, "RightArm", out var armTr, spineTr, new Vector3(0.4f, 0.3f, 0));
            var bodyGo = CreateSyntheticGameObject(mainFile, 14, "HeroBody", out _, rootTr);
            var armorGo = CreateSyntheticGameObject(mainFile, 15, "HeroArmor", out _, rootTr);

            var bodyTex = CreateSyntheticRgbaTexture(mainFile, 100, "HeroBody_Albedo", 4, 4, 210, 110, 60);
            var armorTex = CreateSyntheticRgbaTexture(mainFile, 101, "HeroArmor_Albedo", 4, 4, 90, 140, 220);

            var bodyTexEnv = CreateUninitialized<UnityTexEnv>();
            bodyTexEnv.m_Texture = new PPtr<Texture>(0, bodyTex.m_PathID, mainFile);
            bodyTexEnv.m_Scale = new Vector2(1f, 1f);
            bodyTexEnv.m_Offset = Vector2.Zero;
            var bodyMat = CreateSyntheticMaterial(mainFile, 400, "HeroBodyMat", new[]
            {
                new KeyValuePair<string, UnityTexEnv>("_MainTex", bodyTexEnv)
            });

            var armorTexEnv = CreateUninitialized<UnityTexEnv>();
            armorTexEnv.m_Texture = new PPtr<Texture>(0, armorTex.m_PathID, mainFile);
            armorTexEnv.m_Scale = new Vector2(1f, 1f);
            armorTexEnv.m_Offset = Vector2.Zero;
            var armorMat = CreateSyntheticMaterial(mainFile, 401, "HeroArmorMat", new[]
            {
                new KeyValuePair<string, UnityTexEnv>("_BaseMap", armorTexEnv)
            });

            var bonePaths = new[] { "Hips", "Hips/Spine", "Hips/Spine/RightArm" };
            var bodyMesh = CreateSyntheticSkinnedMesh(mainFile, 200, "HeroBodyMesh", bonePaths);
            var armorMesh = CreateSyntheticSkinnedMesh(mainFile, 201, "HeroArmorMesh", bonePaths);

            var bonePtrs = new[]
            {
                new PPtr<Transform>(0, hipsTr.m_PathID, mainFile),
                new PPtr<Transform>(0, spineTr.m_PathID, mainFile),
                new PPtr<Transform>(0, armTr.m_PathID, mainFile)
            };

            var bodySmr = CreateUninitialized<SkinnedMeshRenderer>();
            bodySmr.assetsFile = mainFile;
            bodySmr.m_PathID = 300;
            bodySmr.m_GameObject = new PPtr<GameObject>(0, bodyGo.m_PathID, mainFile);
            bodySmr.m_Mesh = new PPtr<Mesh>(0, bodyMesh.m_PathID, mainFile);
            bodySmr.m_Materials = new[] { new PPtr<Material>(0, bodyMat.m_PathID, mainFile) };
            bodySmr.m_Bones = bonePtrs;
            mainFile.AddObject(bodySmr);
            bodyGo.m_SkinnedMeshRenderer = bodySmr;

            var armorSmr = CreateUninitialized<SkinnedMeshRenderer>();
            armorSmr.assetsFile = mainFile;
            armorSmr.m_PathID = 301;
            armorSmr.m_GameObject = new PPtr<GameObject>(0, armorGo.m_PathID, mainFile);
            armorSmr.m_Mesh = new PPtr<Mesh>(0, armorMesh.m_PathID, mainFile);
            armorSmr.m_Materials = new[] { new PPtr<Material>(0, armorMat.m_PathID, mainFile) };
            armorSmr.m_Bones = bonePtrs;
            mainFile.AddObject(armorSmr);
            armorGo.m_SkinnedMeshRenderer = armorSmr;

            uint hipsCrc = ComputeUnityCrc32("Hips");
            uint spineCrc = ComputeUnityCrc32("Hips/Spine");
            uint armCrc = ComputeUnityCrc32("Hips/Spine/RightArm");

            var avatar = CreateUninitialized<Avatar>();
            avatar.assetsFile = mainFile;
            avatar.m_PathID = 600;
            avatar.m_Name = "HeroAvatar";
            avatar.m_TOS = new[]
            {
                new KeyValuePair<uint, string>(0u, string.Empty),
                new KeyValuePair<uint, string>(hipsCrc, "Hips"),
                new KeyValuePair<uint, string>(spineCrc, "Hips/Spine"),
                new KeyValuePair<uint, string>(armCrc, "Hips/Spine/RightArm")
            };
            var avSkel = CreateUninitialized<Skeleton>();
            avSkel.m_Node = new[]
            {
                CreateNode(-1),
                CreateNode(0),
                CreateNode(1),
                CreateNode(2)
            };
            avSkel.m_ID = new[] { 0u, hipsCrc, spineCrc, armCrc };
            var avPose = CreateUninitialized<SkeletonPose>();
            avPose.m_X = new[]
            {
                CreateXForm(Vector3.Zero),
                CreateXForm(new Vector3(0, 1f, 0)),
                CreateXForm(new Vector3(0, 0.5f, 0)),
                CreateXForm(new Vector3(0.4f, 0.3f, 0))
            };
            var avConst = CreateUninitialized<AvatarConstant>();
            avConst.m_AvatarSkeleton = avSkel;
            avConst.m_DefaultPose = avPose;
            avConst.m_AvatarSkeletonPose = avPose;
            avatar.m_Avatar = avConst;
            mainFile.AddObject(avatar);

            var animator = CreateUninitialized<Animator>();
            animator.assetsFile = mainFile;
            animator.m_PathID = 500;
            animator.m_GameObject = new PPtr<GameObject>(0, rootGo.m_PathID, mainFile);
            animator.m_Avatar = new PPtr<Avatar>(0, avatar.m_PathID, mainFile);
            animator.m_Controller = new PPtr<RuntimeAnimatorController>(0, 0, mainFile);
            animator.m_HasTransformHierarchy = true;
            mainFile.AddObject(animator);
            rootGo.m_Animator = animator;

            var idleClip = CreateSyntheticDenseRotationClip(mainFile, 700, "Hero_Idle", hipsCrc, 0.10f, 1.0f);
            var runClip = CreateSyntheticDenseRotationClip(mainFile, 701, "Hero_Run", spineCrc, 0.3826834f, 0.8f);
            var attackClip = CreateSyntheticDenseRotationClip(mainFile, 702, "Hero_Attack", armCrc, 0.7071068f, 0.6f);

            return new SyntheticMultiPartRig
            {
                Manager = manager,
                MainFile = mainFile,
                RootGo = rootGo,
                Animator = animator,
                Avatar = avatar,
                BodyMesh = bodyMesh,
                ArmorMesh = armorMesh,
                BodySmr = bodySmr,
                ArmorSmr = armorSmr,
                BodyMaterial = bodyMat,
                ArmorMaterial = armorMat,
                IdleClip = idleClip,
                RunClip = runClip,
                AttackClip = attackClip
            };
        }

        private static AnimationClip CreateSyntheticDenseRotationClip(
            SerializedFile sf,
            long pathId,
            string name,
            uint targetPathCrc,
            float sinHalfAngleY,
            float durationSeconds)
        {
            var cosHalf = MathF.Sqrt(MathF.Max(0f, 1f - sinHalfAngleY * sinHalfAngleY));
            var clip = CreateUninitialized<AnimationClip>();
            clip.assetsFile = sf;
            clip.m_PathID = pathId;
            clip.m_Name = name;
            clip.m_SampleRate = 30f;
            clip.m_Legacy = false;
            clip.m_ClipBindingConstant = new AnimationClipBindingConstant
            {
                genericBindings = new[]
                {
                    new GenericBinding { path = targetPathCrc, attribute = 2, typeID = ClassIDType.Transform }
                },
                pptrCurveMapping = Array.Empty<PPtr<AssetStudio.Object>>()
            };

            var streamed = CreateUninitialized<StreamedClip>();
            streamed.curveCount = 0;
            streamed.data = Array.Empty<uint>();

            var dense = CreateUninitialized<DenseClip>();
            dense.m_FrameCount = 3;
            dense.m_CurveCount = 4;
            dense.m_SampleRate = 2f / Math.Max(0.1f, durationSeconds);
            dense.m_BeginTime = 0f;
            dense.m_SampleArray = new[]
            {
                0f, 0f, 0f, 1f,
                0f, sinHalfAngleY, 0f, cosHalf,
                0f, 0f, 0f, 1f
            };

            var constant = CreateUninitialized<ConstantClip>();
            constant.data = Array.Empty<float>();

            var innerClip = CreateUninitialized<Clip>();
            innerClip.m_StreamedClip = streamed;
            innerClip.m_DenseClip = dense;
            innerClip.m_ConstantClip = constant;

            var muscleClip = CreateUninitialized<ClipMuscleConstant>();
            muscleClip.m_StartTime = 0f;
            muscleClip.m_StopTime = durationSeconds;
            muscleClip.m_Clip = innerClip;
            clip.m_MuscleClip = muscleClip;

            sf.AddObject(clip);
            return clip;
        }

        private static AssetItem CreateAssetItem(AssetStudio.Object asset, ClassIDType type, string name)
        {
            return new AssetItem(asset)
            {
                Type = type,
                TypeString = type.ToString(),
                Name = name,
                PathID = asset.m_PathID
            };
        }

        private static JsonDocument ValidateGlbJson(string glbPath)
        {
            var bytes = File.ReadAllBytes(glbPath);
            Assert.True(bytes.Length >= 20);
            Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4))); // "glTF"
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));

            var jsonChunkLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            var jsonChunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            Assert.Equal(0x4E4F534Au, jsonChunkType); // "JSON"

            var jsonBytes = bytes.AsSpan(20, jsonChunkLen).ToArray();
            return JsonDocument.Parse(jsonBytes);
        }

        private static T CreateUninitialized<T>() where T : class
            => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        private static Node CreateNode(int parentId)
        {
            var n = CreateUninitialized<Node>();
            n.m_ParentId = parentId;
            n.m_AxesId = -1;
            return n;
        }

        private static XForm CreateXForm(Vector3 t)
        {
            var x = CreateUninitialized<XForm>();
            x.t = t;
            x.q = new Quaternion(0, 0, 0, 1);
            x.s = Vector3.One;
            return x;
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager manager, string fileName)
        {
            var sf = CreateUninitialized<SerializedFile>();
            typeof(SerializedFile)
                .GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sf, new object());
            sf.assetsManager = manager;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.m_Externals = new List<FileIdentifier>();
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.version = new[] { 2022, 3, 10, 1 };
            return sf;
        }

        private static GameObject CreateSyntheticGameObject(
            SerializedFile sf,
            long pathId,
            string name,
            out Transform tr,
            Transform? parent = null,
            Vector3? localPos = null)
        {
            var go = CreateUninitialized<GameObject>();
            go.assetsFile = sf;
            go.m_PathID = pathId;
            go.m_Name = name;

            tr = CreateUninitialized<Transform>();
            tr.assetsFile = sf;
            tr.m_PathID = pathId + 1000;
            tr.m_GameObject = new PPtr<GameObject>(0, pathId, sf);
            tr.m_LocalPosition = localPos ?? Vector3.Zero;
            tr.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            tr.m_LocalScale = Vector3.One;
            tr.m_Father = parent != null ? new PPtr<Transform>(0, parent.m_PathID, sf) : new PPtr<Transform>(0, 0, sf);
            tr.m_Children = Array.Empty<PPtr<Transform>>();

            go.m_Transform = tr;
            go.m_Components = new[] { new PPtr<Component>(0, tr.m_PathID, sf) };

            sf.AddObject(go);
            sf.AddObject(tr);

            if (parent != null)
            {
                var children = parent.m_Children.ToList();
                children.Add(new PPtr<Transform>(0, tr.m_PathID, sf));
                parent.m_Children = children.ToArray();
            }

            return go;
        }

        private static Texture2D CreateSyntheticRgbaTexture(
            SerializedFile sf,
            long pathId,
            string name,
            int width,
            int height,
            byte r,
            byte g,
            byte b)
        {
            var tex = CreateUninitialized<Texture2D>();
            tex.assetsFile = sf;
            tex.m_PathID = pathId;
            tex.m_Name = name;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.version = sf.version;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
            var rr = CreateUninitialized<ResourceReader>();
            typeof(ResourceReader).GetField("needSearch", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, false);
            typeof(ResourceReader).GetField("reader", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, new EndianBinaryReader(new MemoryStream(pixels)));
            typeof(ResourceReader).GetField("offset", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, 0L);
            typeof(ResourceReader).GetField("size", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(rr, (long)pixels.Length);
            tex.image_data = rr;
            sf.AddObject(tex);
            return tex;
        }

        private static Material CreateSyntheticMaterial(
            SerializedFile sf,
            long pathId,
            string name,
            KeyValuePair<string, UnityTexEnv>[] texEnvs)
        {
            var mat = CreateUninitialized<Material>();
            mat.assetsFile = sf;
            mat.m_PathID = pathId;
            mat.m_Name = name;
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs,
                m_Floats = Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = new[] { new KeyValuePair<string, Color>("_BaseColor", new Color(1f, 1f, 1f, 1f)) }
            };
            sf.AddObject(mat);
            return mat;
        }

        private static Mesh CreateSyntheticSkinnedMesh(
            SerializedFile sf,
            long pathId,
            string name,
            string[] bonePaths)
        {
            var mesh = CreateUninitialized<Mesh>();
            mesh.assetsFile = sf;
            mesh.m_PathID = pathId;
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            mesh.m_Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_UV0 = new[] { 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            var subMesh = CreateUninitialized<SubMesh>();
            subMesh.firstByte = 0;
            subMesh.indexCount = 3;
            subMesh.firstVertex = 0;
            subMesh.vertexCount = 3;
            subMesh.baseVertex = 0;
            mesh.m_SubMeshes = new[] { subMesh };
            mesh.m_BoneNameHashes = bonePaths.Select(ComputeUnityCrc32).ToArray();
            mesh.m_BindPose = bonePaths.Select(_ => Matrix4x4.Scale(Vector3.One)).ToArray();
            mesh.m_Skin = new[]
            {
                new BoneWeights4 { boneIndex = new[] { 0, 1, 0, 0 }, weight = new[] { 0.5f, 0.5f, 0f, 0f } },
                new BoneWeights4 { boneIndex = new[] { 1, 2, 0, 0 }, weight = new[] { 0.5f, 0.5f, 0f, 0f } },
                new BoneWeights4 { boneIndex = new[] { 2, 0, 0, 0 }, weight = new[] { 1f, 0f, 0f, 0f } }
            };
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            sf.AddObject(mesh);
            return mesh;
        }

        private static uint ComputeUnityCrc32(string path)
        {
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(path);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
        }

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AssetStudio.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate AssetStudio.sln root.");
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
