using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Xunit;

namespace AssetStudio.Tests
{
    public class PreviewUxAndReferencesTests
    {
        #region 3A: 1-Click Export Model from 3D Preview

        [Fact]
        public void PreviewModelExportHelper_ResolvesTargetsAcrossModelGroupSingleMeshAvatarAndAnimationClip()
        {
            var meshA = CreateUninitialized<Mesh>();
            meshA.m_Name = "Hero_Body";
            var meshB = CreateUninitialized<Mesh>();
            meshB.m_Name = "Hero_Head";
            var avatar = CreateUninitialized<Avatar>();
            avatar.m_Name = "HeroAvatar";
            var clip = CreateUninitialized<AnimationClip>();
            clip.m_Name = "RunCycle";

            var groupCandidate = new PreviewCandidateItem
            {
                Mesh = meshA,
                Avatar = avatar,
                ModelGroupId = "modelgroup:animator:hero",
                ModelGroupName = "HeroCharacter",
                ModelGroupKind = "Animator",
                ModelGroupMeshes = new[] { meshA, meshB },
                ModelGroupMeshCount = 2,
                Label = "Model group: HeroCharacter (2 parts)"
            };

            // 1) Multi-part ModelGroup with ShowFullModelGroup = true
            var fullGroupTarget = PreviewModelExportHelper.ResolveExportTarget(
                PreviewSurfaceKind.Mesh3D,
                selectedAsset: null,
                selectedCandidate: groupCandidate,
                currentMesh: meshA,
                currentAvatar: avatar,
                currentClip: null,
                showFullModelGroup: true);
            Assert.True(fullGroupTarget.IsValid);
            Assert.Equal(PreviewModelExportKind.ModelGroup, fullGroupTarget.Kind);
            Assert.True(fullGroupTarget.ExportFullModelGroup);
            Assert.Equal("modelgroup:animator:hero", fullGroupTarget.ModelGroupId);
            Assert.Equal("HeroCharacter", fullGroupTarget.SuggestedBaseName);
            Assert.Equal(2, fullGroupTarget.CandidateMeshes.Count);

            // 2) Same candidate with ShowFullModelGroup = false -> SingleMesh only!
            var singleMeshTarget = PreviewModelExportHelper.ResolveExportTarget(
                PreviewSurfaceKind.Mesh3D,
                selectedAsset: null,
                selectedCandidate: groupCandidate,
                currentMesh: meshA,
                currentAvatar: avatar,
                currentClip: null,
                showFullModelGroup: false);
            Assert.True(singleMeshTarget.IsValid);
            Assert.Equal(PreviewModelExportKind.SingleMesh, singleMeshTarget.Kind);
            Assert.False(singleMeshTarget.ExportFullModelGroup);
            Assert.Equal(string.Empty, singleMeshTarget.ModelGroupId);
            Assert.Same(meshA, singleMeshTarget.PrimaryMesh);
            Assert.Equal("Hero_Body", singleMeshTarget.SuggestedBaseName);

            // 3) AnimationClip + Avatar + ModelGroup (ShowFullModelGroup = true) -> AnimatedModelGroup with clip included!
            var animatedGroupTarget = PreviewModelExportHelper.ResolveExportTarget(
                PreviewSurfaceKind.Animation3D,
                selectedAsset: null,
                selectedCandidate: groupCandidate,
                currentMesh: meshA,
                currentAvatar: avatar,
                currentClip: clip,
                showFullModelGroup: true);
            Assert.True(animatedGroupTarget.IsValid);
            Assert.Equal(PreviewModelExportKind.AnimatedModelGroup, animatedGroupTarget.Kind);
            Assert.True(animatedGroupTarget.ExportFullModelGroup);
            Assert.Same(clip, animatedGroupTarget.AnimationClip);
            Assert.Same(avatar, animatedGroupTarget.Avatar);
            Assert.Equal("HeroCharacter_RunCycle", animatedGroupTarget.SuggestedBaseName);

            // 4) AnimationClip + SingleMesh (ShowFullModelGroup = false) -> AnimatedSingleMesh
            var animatedSingleTarget = PreviewModelExportHelper.ResolveExportTarget(
                PreviewSurfaceKind.Animation3D,
                selectedAsset: null,
                selectedCandidate: groupCandidate,
                currentMesh: meshA,
                currentAvatar: avatar,
                currentClip: clip,
                showFullModelGroup: false);
            Assert.True(animatedSingleTarget.IsValid);
            Assert.Equal(PreviewModelExportKind.AnimatedSingleMesh, animatedSingleTarget.Kind);
            Assert.False(animatedSingleTarget.ExportFullModelGroup);
            Assert.Same(clip, animatedSingleTarget.AnimationClip);
            Assert.Equal("Hero_Body_RunCycle", animatedSingleTarget.SuggestedBaseName);

            // 5) Avatar-only preview -> AvatarRig
            var avatarOnlyTarget = PreviewModelExportHelper.ResolveExportTarget(
                PreviewSurfaceKind.Avatar,
                selectedAsset: null,
                selectedCandidate: null,
                currentMesh: null,
                currentAvatar: avatar,
                currentClip: null,
                showFullModelGroup: true);
            Assert.True(avatarOnlyTarget.IsValid);
            Assert.Equal(PreviewModelExportKind.AvatarRig, avatarOnlyTarget.Kind);
            Assert.Same(avatar, avatarOnlyTarget.Avatar);

            // 6) Non-3D / empty state -> Invalid
            var noneTarget = PreviewModelExportHelper.ResolveExportTarget(
                PreviewSurfaceKind.Texture,
                selectedAsset: null,
                selectedCandidate: null,
                currentMesh: null,
                currentAvatar: null,
                currentClip: null,
                showFullModelGroup: true);
            Assert.False(noneTarget.IsValid);
            Assert.Equal(PreviewModelExportKind.None, noneTarget.Kind);

            // 7) Format dispatch respects .glb, .gltf, and .fbx
            var glbOptions = new ExportOptionsState { ModelFormat = ModelExportFormat.Glb };
            var gltfOptions = new ExportOptionsState { ModelFormat = ModelExportFormat.Gltf };
            var fbxOptions = new ExportOptionsState { ModelFormat = ModelExportFormat.Fbx };
            Assert.Equal("HeroCharacter_RunCycle.glb", animatedGroupTarget.BuildSuggestedFileName(glbOptions));
            Assert.Equal("HeroCharacter_RunCycle.gltf", animatedGroupTarget.BuildSuggestedFileName(gltfOptions));
            Assert.Equal("HeroCharacter_RunCycle.fbx", animatedGroupTarget.BuildSuggestedFileName(fbxOptions));
            Assert.Equal(ModelExportFormat.Glb, PreviewModelExportHelper.ResolveEffectiveFormat("C:/out/model.glb", fbxOptions));
            Assert.Equal(ModelExportFormat.Gltf, PreviewModelExportHelper.ResolveEffectiveFormat("C:/out/model.gltf", fbxOptions));
            Assert.Equal(ModelExportFormat.Fbx, PreviewModelExportHelper.ResolveEffectiveFormat("C:/out/model.fbx", glbOptions));
            Assert.Equal(ModelExportFormat.Glb, PreviewModelExportHelper.ResolveEffectiveFormat("C:/out/model", glbOptions));
        }

        [Fact]
        public async Task MainWindowViewModel_ExportPreviewModelCommand_CanExecuteTracksActive3DSurfaceAndDispatches()
        {
            using var vm = new MainWindowViewModel();

            // Initially disabled on None
            Assert.False(vm.CanExportPreviewModel);
            Assert.False(vm.ExportPreviewModelCommand.CanExecute());

            // Non-model surfaces keep Export Model disabled
            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Texture);
            Assert.False(vm.CanExportPreviewModel);
            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Material);
            Assert.False(vm.CanExportPreviewModel);
            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Shader);
            Assert.False(vm.CanExportPreviewModel);

            // Mesh3D, Avatar, Animation3D all enable ExportPreviewModelCommand
            var canExecuteChangedCount = 0;
            vm.ExportPreviewModelCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Mesh3D);
            Assert.True(vm.CanExportPreviewModel);
            Assert.True(vm.ExportPreviewModelCommand.CanExecute());
            Assert.True(canExecuteChangedCount > 0);

            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Avatar);
            Assert.True(vm.CanExportPreviewModel);

            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Animation3D);
            Assert.True(vm.CanExportPreviewModel);

            var invoked = 0;
            vm.CommandHandlers.ExportPreviewModelAsync = () =>
            {
                invoked++;
                return Task.CompletedTask;
            };
            await vm.ExportPreviewModelCommand.ExecuteAsync();
            Assert.Equal(1, invoked);

            // Reset returns to disabled state
            vm.PreviewSession.Reset();
            Assert.False(vm.CanExportPreviewModel);
            Assert.False(vm.ExportPreviewModelCommand.CanExecute());
        }

        #endregion

        #region 3B: References / Used By Tab & Click-to-Jump Navigation

        [Fact]
        public async Task PreviewSessionViewModel_ThreeTabTransitions_MutuallyExclusive_AndReferencesFilteringWorks()
        {
            using var vm = new PreviewSessionViewModel();

            // Default tab is Preview
            Assert.Equal(RightPanelTab.Preview, vm.ActiveRightTab);
            Assert.True(vm.IsPreviewTabActive);
            Assert.False(vm.IsDumpTabActive);
            Assert.False(vm.IsReferencesTabActive);

            // Switch to Dump
            vm.ActiveRightTab = RightPanelTab.Dump;
            Assert.False(vm.IsPreviewTabActive);
            Assert.True(vm.IsDumpTabActive);
            Assert.False(vm.IsReferencesTabActive);
            Assert.True(ReadPrivateBoolField(vm.ShaderPreview, "_suspended"));

            // Switch to References -> Preview & Dump both false, ShaderPreview stays suspended!
            vm.ActiveRightTab = RightPanelTab.References;
            Assert.False(vm.IsPreviewTabActive);
            Assert.False(vm.IsDumpTabActive);
            Assert.True(vm.IsReferencesTabActive);
            Assert.True(ReadPrivateBoolField(vm.ShaderPreview, "_suspended"));

            // Switch back to Preview -> ShaderPreview resumed
            vm.ActiveRightTab = RightPanelTab.Preview;
            Assert.True(vm.IsPreviewTabActive);
            Assert.False(vm.IsDumpTabActive);
            Assert.False(vm.IsReferencesTabActive);
            Assert.False(ReadPrivateBoolField(vm.ShaderPreview, "_suspended"));

            // Shader surface invariant: IsTextSurfaceVisible MUST remain false for PreviewSurfaceKind.Shader
            vm.ActivateSurface(PreviewSurfaceKind.Shader);
            Assert.False(vm.IsTextSurfaceVisible);

            // Legacy IsDumpTabActive setter compatibility
            vm.IsDumpTabActive = true;
            Assert.Equal(RightPanelTab.Dump, vm.ActiveRightTab);
            vm.IsDumpTabActive = false;
            Assert.Equal(RightPanelTab.Preview, vm.ActiveRightTab);

            // Async references population + stale request rejection + quick filter
            _ = vm.BeginReferencesRequest(out var req1);
            _ = vm.BeginReferencesRequest(out var req2);

            var snapshot = new AssetReferencesSnapshot
            {
                TargetUniqueId = "sharedassets0.assets#100",
                TargetName = "HeroTex",
                TargetType = ClassIDType.Texture2D,
                ReferencedBy = new[]
                {
                    new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets0.assets#200",
                        Name = "HeroArmorMat",
                        AssetType = ClassIDType.Material,
                        TypeName = "Material",
                        Relation = "Texture (_MainTex)",
                        PathId = 200,
                        Direction = AssetReferenceDirection.ReferencedBy
                    },
                    new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets0.assets#201",
                        Name = "HeroIconSprite",
                        AssetType = ClassIDType.Sprite,
                        TypeName = "Sprite",
                        Relation = "Sprite Texture",
                        PathId = 201,
                        Direction = AssetReferenceDirection.ReferencedBy
                    }
                },
                References = new[]
                {
                    new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets0.assets#300",
                        Name = "StandardShader",
                        AssetType = ClassIDType.Shader,
                        TypeName = "Shader",
                        Relation = "Shader",
                        PathId = 300,
                        Direction = AssetReferenceDirection.References
                    }
                }
            };

            // Stale req1 is ignored
            Assert.False(vm.CompleteReferencesRequest(req1, snapshot));
            // Latest req2 succeeds
            Assert.True(vm.CompleteReferencesRequest(req2, snapshot));
            Assert.False(vm.IsReferencesLoading);
            Assert.Equal(2, vm.FilteredReferencedByItems.Count);
            Assert.Single(vm.FilteredReferencesItems);
            Assert.Contains("Referenced By", vm.ReferencedBySectionHeader);
            Assert.Contains("2", vm.ReferencedBySectionHeader);
            Assert.Contains("References", vm.ReferencesSectionHeader);

            // Quick filter narrows both lists without mutating underlying snapshot
            vm.ReferencesFilterText = "Sprite";
            var filteredUsedBy = Assert.Single(vm.FilteredReferencedByItems);
            Assert.Equal("HeroIconSprite", filteredUsedBy.Name);
            Assert.Empty(vm.FilteredReferencesItems);

            // Clearing filter restores full lists
            vm.ReferencesFilterText = string.Empty;
            Assert.Equal(2, vm.FilteredReferencedByItems.Count);
            Assert.Single(vm.FilteredReferencesItems);

            // NavigateToReferencedAssetCommand dispatches with target item
            using var mainVm = new MainWindowViewModel();
            AssetReferenceItem? jumpedTo = null;
            mainVm.CommandHandlers.NavigateToReferencedAssetAsync = item =>
            {
                jumpedTo = item;
                return Task.CompletedTask;
            };
            await mainVm.NavigateToReferencedAssetCommand.ExecuteAsync(filteredUsedBy);
            Assert.Same(filteredUsedBy, jumpedTo);

            // Double Reset() emits 0 PropertyChanged events!
            vm.ActiveRightTab = RightPanelTab.References;
            vm.Reset();
            var firedProps = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    firedProps.Add(e.PropertyName);
                }
            };
            vm.Reset();
            Assert.Empty(firedProps);
        }

        [Fact]
        public void MainWindowAxaml_PreservesNonDetachingThreeTabLayoutAndPureMvvmExportButton()
        {
            var repoRoot = LocateRepositoryRoot();
            var axamlPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml");
            var axaml = File.ReadAllText(axamlPath);

            // Non-detaching TabStrip with Preview, Dump, References
            Assert.Contains("x:Name=\"RightTabControl\"", axaml, StringComparison.Ordinal);
            Assert.Contains("<TabStripItem Content=\"Preview\"", axaml, StringComparison.Ordinal);
            Assert.Contains("<TabStripItem Content=\"Dump\"", axaml, StringComparison.Ordinal);
            Assert.Contains("<TabStripItem Content=\"References\"", axaml, StringComparison.Ordinal);

            // PreviewTabContent remains permanently attached (controlled via IsVisible, not TabControl detachment)
            Assert.Contains("x:Name=\"PreviewTabContent\"", axaml, StringComparison.Ordinal);
            Assert.Contains("IsVisible=\"{Binding PreviewSession.IsPreviewTabActive}\"", axaml, StringComparison.Ordinal);
            Assert.Contains("IsVisible=\"{Binding PreviewSession.IsDumpTabActive}\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"ReferencesTabContent\"", axaml, StringComparison.Ordinal);
            Assert.Contains("IsVisible=\"{Binding PreviewSession.IsReferencesTabActive}\"", axaml, StringComparison.Ordinal);

            // 1-click Export Model button inside Model Controls wired via ExportPreviewModelCommand
            Assert.Contains("x:Name=\"ExportPreviewModelBtn\"", axaml, StringComparison.Ordinal);
            Assert.Contains("Command=\"{Binding ExportPreviewModelCommand}\"", axaml, StringComparison.Ordinal);
            Assert.Contains("NavigateToReferencedAssetCommand", axaml, StringComparison.Ordinal);

            // Pure MVVM: zero Click= event handlers anywhere in MainWindow.axaml
            Assert.DoesNotMatch(new Regex(@"\bClick\s*=", RegexOptions.CultureInvariant), axaml);
        }

        [Fact]
        public void AssetReferencesService_EagerGraph_FindsUsedByAndUsesAcrossCoreUnityTypes()
        {
            var file = CreateSyntheticSerializedFile("sharedassets0.assets");

            var texture = CreateObjectInFile<Texture2D>(file, 10, ClassIDType.Texture2D, "Hero_Albedo");
            var shader = CreateObjectInFile<Shader>(file, 11, ClassIDType.Shader, "Standard");

            var material = CreateObjectInFile<Material>(file, 20, ClassIDType.Material, "Hero_Mat");
            material.m_Shader = CreatePPtr<Shader>(file, 11);
            var mainTexEnv = CreateUninitialized<UnityTexEnv>();
            mainTexEnv.m_Texture = CreatePPtr<Texture>(file, 10);
            material.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = new[]
                {
                    new KeyValuePair<string, UnityTexEnv>("_MainTex", mainTexEnv)
                }
            };

            var sprite = CreateObjectInFile<Sprite>(file, 30, ClassIDType.Sprite, "Hero_Portrait");
            sprite.m_RD = CreateUninitialized<SpriteRenderData>();
            sprite.m_RD.texture = CreatePPtr<Texture2D>(file, 10);

            var font = CreateObjectInFile<Font>(file, 35, ClassIDType.Font, "RetroBitmapFont");
            font.m_Texture = CreatePPtr<Texture>(file, 10);
            font.m_DefaultMaterial = CreatePPtr<Material>(file, 20);

            var mesh = CreateObjectInFile<Mesh>(file, 40, ClassIDType.Mesh, "Hero_Mesh");
            var go = CreateObjectInFile<GameObject>(file, 41, ClassIDType.GameObject, "HeroRoot");
            var smr = CreateObjectInFile<SkinnedMeshRenderer>(file, 42, ClassIDType.SkinnedMeshRenderer, string.Empty);
            smr.m_Mesh = CreatePPtr<Mesh>(file, 40);
            smr.m_GameObject = CreatePPtr<GameObject>(file, 41);
            smr.m_Materials = new[] { CreatePPtr<Material>(file, 20) };
            go.m_Components = new[] { CreatePPtr<Component>(file, 42) };
            go.m_SkinnedMeshRenderer = smr;

            var clip = CreateObjectInFile<AnimationClip>(file, 50, ClassIDType.AnimationClip, "Hero_Idle");
            var controller = CreateObjectInFile<AnimatorController>(file, 51, ClassIDType.AnimatorController, "Hero_Ctrl");
            controller.m_AnimationClips = new[] { CreatePPtr<AnimationClip>(file, 50) };
            var avatar = CreateObjectInFile<Avatar>(file, 52, ClassIDType.Avatar, "Hero_Avatar");
            var animator = CreateObjectInFile<Animator>(file, 53, ClassIDType.Animator, string.Empty);
            animator.m_Avatar = CreatePPtr<Avatar>(file, 52);
            animator.m_Controller = CreatePPtr<RuntimeAnimatorController>(file, 51);
            animator.m_GameObject = CreatePPtr<GameObject>(file, 41);

            var queryContext = new AssetReferencesQueryContext
            {
                LoadedFiles = new[] { file }
            };

            // 1) Texture2D -> Used By Material, Sprite, Font
            var texRefs = AssetReferencesService.BuildReferences(selectedItem: null, texture, queryContext);
            Assert.Contains(texRefs.ReferencedBy, r => r.PathId == 20 && r.AssetType == ClassIDType.Material);
            Assert.Contains(texRefs.ReferencedBy, r => r.PathId == 30 && r.AssetType == ClassIDType.Sprite);
            Assert.Contains(texRefs.ReferencedBy, r => r.PathId == 35 && r.AssetType == ClassIDType.Font);

            // 2) Material -> Uses Shader & Texture2D; Used By SkinnedMeshRenderer & Font
            var matRefs = AssetReferencesService.BuildReferences(selectedItem: null, material, queryContext);
            Assert.Contains(matRefs.References, r => r.PathId == 11 && r.AssetType == ClassIDType.Shader);
            Assert.Contains(matRefs.References, r => r.PathId == 10 && r.AssetType == ClassIDType.Texture2D);
            Assert.Contains(matRefs.ReferencedBy, r => r.PathId == 42 && r.AssetType == ClassIDType.SkinnedMeshRenderer);
            Assert.Contains(matRefs.ReferencedBy, r => r.PathId == 35 && r.AssetType == ClassIDType.Font);

            // 3) Mesh -> Used By SkinnedMeshRenderer & GameObject; Uses Material (inferred from renderer)
            var meshRefs = AssetReferencesService.BuildReferences(selectedItem: null, mesh, queryContext);
            Assert.Contains(meshRefs.ReferencedBy, r => r.PathId == 42 && r.AssetType == ClassIDType.SkinnedMeshRenderer);
            Assert.Contains(meshRefs.ReferencedBy, r => r.PathId == 41 && r.AssetType == ClassIDType.GameObject);
            Assert.Contains(meshRefs.References, r => r.PathId == 20 && r.AssetType == ClassIDType.Material);

            // 4) AnimationClip -> Used By AnimatorController
            var clipRefs = AssetReferencesService.BuildReferences(selectedItem: null, clip, queryContext);
            Assert.Contains(clipRefs.ReferencedBy, r => r.PathId == 51 && r.AssetType == ClassIDType.AnimatorController);

            // 5) Avatar -> Used By Animator
            var avatarRefs = AssetReferencesService.BuildReferences(selectedItem: null, avatar, queryContext);
            Assert.Contains(avatarRefs.ReferencedBy, r => r.PathId == 53 && r.AssetType == ClassIDType.Animator);
        }

        [Fact]
        public void SQLiteProjectIndexCache_LoadAssetReferencesAndModelGroupById_QueriesSemanticV15TablesFast()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudio_RefCacheTest_" + Guid.NewGuid().ToString("N"));
            var projectDir = Path.Combine(tempRoot, "GameData");
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

                // Trigger schema creation via SaveIndexCache
                var handleTex = new AssetHandle
                {
                    UniqueID = "sharedassets0.assets#100",
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = sourceFilePath,
                    PathID = 100,
                    Type = ClassIDType.Texture2D,
                    Name = "Armor_Albedo",
                    ByteSize = 512
                };
                var handleMat = new AssetHandle
                {
                    UniqueID = "sharedassets0.assets#200",
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = sourceFilePath,
                    PathID = 200,
                    Type = ClassIDType.Material,
                    Name = "Armor_Mat",
                    ByteSize = 256
                };
                var handleMesh = new AssetHandle
                {
                    UniqueID = "sharedassets0.assets#300",
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = sourceFilePath,
                    PathID = 300,
                    Type = ClassIDType.Mesh,
                    Name = "Armor_Chest",
                    ByteSize = 1024
                };
                var handleAvatar = new AssetHandle
                {
                    UniqueID = "sharedassets0.assets#400",
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = sourceFilePath,
                    PathID = 400,
                    Type = ClassIDType.Avatar,
                    Name = "Knight_Avatar",
                    ByteSize = 512
                };
                var handleClip = new AssetHandle
                {
                    UniqueID = "sharedassets0.assets#500",
                    SerializedFileName = "sharedassets0.assets",
                    OriginalPath = sourceFilePath,
                    PathID = 500,
                    Type = ClassIDType.AnimationClip,
                    Name = "Knight_Slash",
                    ByteSize = 512
                };

                var handles = new List<AssetHandle> { handleTex, handleMat, handleMesh, handleAvatar, handleClip };
                var projectIndex = new ProjectIndex();
                foreach (var h in handles)
                {
                    projectIndex.AddHandle(h);
                }

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                // Seed v15 semantic graph rows directly for deterministic unit testing
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
                            INSERT INTO MaterialTextures (ProjectId, MaterialAssetUniqueID, SlotName, SlotIndex, TextureFileId, TexturePathID, TextureAssetUniqueID, IsResolved, IsMainTexture)
                            VALUES (@pid, 'sharedassets0.assets#200', '_MainTex', 0, 0, 100, 'sharedassets0.assets#100', 1, 1);

                            INSERT INTO MaterialCache (ProjectId, MaterialAssetUniqueID, MaterialName, MainTextureAssetUniqueID, MainTextureSlotName)
                            VALUES (@pid, 'sharedassets0.assets#200', 'Armor_Mat', 'sharedassets0.assets#100', '_MainTex');

                            INSERT INTO MeshMaterialResolvedCache (ProjectId, MeshAssetUniqueID, SubMeshIndex, MaterialAssetUniqueID, RendererAssetUniqueID, RendererType, MaterialName, MainTextureAssetUniqueID, BaseColorR, BaseColorG, BaseColorB, BaseColorA)
                            VALUES (@pid, 'sharedassets0.assets#300', 0, 'sharedassets0.assets#200', '', 'SkinnedMeshRenderer', 'Armor_Mat', 'sharedassets0.assets#100', 1, 1, 1, 1);

                            INSERT INTO ModelGroups (ProjectId, GroupAssetUniqueID, GroupKind, GroupName, RootGameObjectAssetUniqueID, RootGameObjectName, AnimatorAssetUniqueID, AvatarAssetUniqueID, ControllerAssetUniqueID, SourceFileName, Confidence, ConfidenceReason)
                            VALUES (@pid, 'modelgroup:animator:knight', 'Animator', 'Knight_Root', 'sharedassets0.assets#600', 'Knight_Root', 'sharedassets0.assets#601', 'sharedassets0.assets#400', '', 'sharedassets0.assets', 100, 'Test');

                            INSERT INTO AnimationClipTargets (ProjectId, ClipAssetUniqueID, AnimatorAssetUniqueID, AvatarAssetUniqueID, MeshAssetUniqueID, ModelGroupId, RelationKind, MatchScore)
                            VALUES (@pid, 'sharedassets0.assets#500', 'sharedassets0.assets#601', 'sharedassets0.assets#400', 'sharedassets0.assets#300', 'modelgroup:animator:knight', 'Direct', 100);";
                        cmd.Parameters.AddWithValue("@pid", projectId);
                        cmd.ExecuteNonQuery();
                    }
                }

                // Verify LoadModelGroupById (used by 3A)
                var loadedGroup = cache.LoadModelGroupById(projectDir, signature, "modelgroup:animator:knight");
                Assert.NotNull(loadedGroup);
                Assert.Equal("Knight_Root", loadedGroup!.GroupName);
                Assert.Equal("sharedassets0.assets#400", loadedGroup.AvatarAssetId);

                // Verify LoadAssetReferencesFromSemanticCache via AssetReferencesService (used by 3B)
                var queryCtx = new AssetReferencesQueryContext
                {
                    ProjectIndex = projectIndex,
                    SemanticCacheLoader = (uniqueId, refBy, refs, max) =>
                        cache.LoadAssetReferencesFromSemanticCache(projectDir, signature, uniqueId, refBy, refs, max)
                };

                var matSnapshot = AssetReferencesService.BuildReferences(new AssetItem(handleMat, 0), null, queryCtx);
                Assert.Contains(matSnapshot.References, r => r.AssetUniqueId == "sharedassets0.assets#100" && r.Name == "Armor_Albedo");
                Assert.Contains(matSnapshot.ReferencedBy, r => r.AssetUniqueId == "sharedassets0.assets#300" && r.Name == "Armor_Chest");

                var clipSnapshot = AssetReferencesService.BuildReferences(new AssetItem(handleClip, 0), null, queryCtx);
                Assert.Contains(clipSnapshot.References, r => r.AssetUniqueId == "sharedassets0.assets#400" && r.Name == "Knight_Avatar");
                Assert.Contains(clipSnapshot.References, r => r.AssetUniqueId == "sharedassets0.assets#300" && r.Name == "Armor_Chest");

                var avatarSnapshot = AssetReferencesService.BuildReferences(new AssetItem(handleAvatar, 0), null, queryCtx);
                Assert.Contains(avatarSnapshot.ReferencedBy, r => r.AssetUniqueId == "sharedassets0.assets#500" && r.Name == "Knight_Slash");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        #endregion

        #region 3C: Legacy Bitmap Font & TextMeshPro TMP_FontAsset Preview

        [Fact]
        public void LegacyBitmapFont_EmptyFontData_MetadataAndAtlasVisibilityEnhancementWork()
        {
            var file = CreateSyntheticSerializedFile("sharedassets0.assets");
            var atlasTex = CreateObjectInFile<Texture2D>(file, 700, ClassIDType.Texture2D, "PixelFont_Atlas");
            atlasTex.m_Width = 512;
            atlasTex.m_Height = 256;
            atlasTex.m_TextureFormat = TextureFormat.Alpha8;

            var font = CreateObjectInFile<Font>(file, 701, ClassIDType.Font, "PixelHUD");
            font.m_FontData = Array.Empty<byte>();
            font.m_FontSize = 18f;
            font.m_LineSpacing = 22.5f;
            font.m_CharacterRectCount = 95;
            font.m_FontNames = new[] { "Arial", "Segoe UI" };
            font.m_Texture = CreatePPtr<Texture>(file, 700);

            var overlayInfo = FontAssetPreviewRenderer.BuildLegacyFontInfoText(font, atlasTex);
            Assert.Contains("Legacy Bitmap / Custom Font", overlayInfo);
            Assert.Contains("PixelHUD", overlayInfo);
            Assert.Contains("Font Size: 18", overlayInfo);
            Assert.Contains("Line Spacing: 22.5", overlayInfo);
            Assert.Contains("Characters: 95", overlayInfo);
            Assert.Contains("Arial, Segoe UI", overlayInfo);
            Assert.Contains("512x256", overlayInfo);

            var fallbackText = FontAssetPreviewRenderer.BuildLegacyFontFallbackText(font, "Texture unavailable");
            Assert.Contains("Character Rects: 95", fallbackText);
            Assert.Contains("Arial, Segoe UI", fallbackText);
            Assert.Contains("PathID=700", fallbackText);
            Assert.Contains("Texture unavailable", fallbackText);

            // Alpha-only white-on-transparent SDF/font atlas -> visible opaque bright glyph on dark slate (#1E2127)
            using (var whiteOnTransparent = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                whiteOnTransparent.Erase(new SKColor(255, 255, 255, 0));
                whiteOnTransparent.SetPixel(3, 3, new SKColor(255, 255, 255, 255));

                Assert.True(FontAssetPreviewRenderer.EnhanceFontAtlasVisibility(whiteOnTransparent));
                var bg = whiteOnTransparent.GetPixel(0, 0);
                var fg = whiteOnTransparent.GetPixel(3, 3);
                Assert.Equal(255, bg.Alpha);
                Assert.InRange(bg.Red, 20, 45);
                Assert.Equal(255, fg.Alpha);
                Assert.True(fg.Red >= 240 && fg.Green >= 240 && fg.Blue >= 240);
            }

            // Alpha8 black RGB + alpha -> also enhanced to crisp bright glyph on dark slate
            using (var alpha8 = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                alpha8.Erase(new SKColor(0, 0, 0, 0));
                alpha8.SetPixel(4, 4, new SKColor(0, 0, 0, 240));

                Assert.True(FontAssetPreviewRenderer.EnhanceFontAtlasVisibility(alpha8));
                var fg = alpha8.GetPixel(4, 4);
                Assert.Equal(255, fg.Alpha);
                Assert.True(fg.Red > 200 && fg.Green > 200 && fg.Blue > 200);
            }

            // R8 single-channel atlas -> converted to white-on-dark monochrome
            using (var r8 = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                r8.Erase(new SKColor(0, 0, 0, 255));
                r8.SetPixel(2, 2, new SKColor(255, 0, 0, 255));

                Assert.True(FontAssetPreviewRenderer.EnhanceFontAtlasVisibility(r8));
                var fg = r8.GetPixel(2, 2);
                Assert.Equal(255, fg.Red);
                Assert.Equal(255, fg.Green);
                Assert.Equal(255, fg.Blue);
            }
        }

        [Fact]
        public void FontAssetPreviewRenderer_CmapParser_ExtractsSymbolAndSupplementaryCodepoints()
        {
            // Build minimal OpenType cmap table with Format 4 subtable mapping PUA U+E000..U+E005
            var cmap4 = BuildMinimalCmapFormat4Table(startCode: 0xE000, endCode: 0xE005);
            var parsed4 = FontAssetPreviewRenderer.ExtractCodepointsFromCmapTable(cmap4, maxCount: 32);
            Assert.Equal(new[] { 0xE000, 0xE001, 0xE002, 0xE003, 0xE004, 0xE005 }, parsed4);

            // Build minimal OpenType cmap table with Format 12 subtable mapping U+1F600..U+1F603
            var cmap12 = BuildMinimalCmapFormat12Table(startCode: 0x1F600u, endCode: 0x1F603u);
            var parsed12 = FontAssetPreviewRenderer.ExtractCodepointsFromCmapTable(cmap12, maxCount: 32);
            Assert.Equal(new[] { 0x1F600, 0x1F601, 0x1F602, 0x1F603 }, parsed12);
        }

        [Fact]
        public void TmpFontPreviewService_DetectsAndExtractsModernAndLegacyTmpFontAssets()
        {
            // 1) Modern TMP 2.x/3.x MonoBehaviour fields (works even when script name is null/IL2CPP)
            var modernFields = new OrderedDictionary
            {
                ["m_Name"] = "LiberationSans SDF",
                ["m_FaceInfo"] = new OrderedDictionary
                {
                    ["m_FamilyName"] = "Liberation Sans",
                    ["m_StyleName"] = "Regular",
                    ["m_PointSize"] = 86f,
                    ["m_LineHeight"] = 98.5f,
                    ["m_AscentLine"] = 78f,
                    ["m_DescentLine"] = -20f
                },
                ["m_AtlasWidth"] = 1024,
                ["m_AtlasHeight"] = 1024,
                ["m_AtlasPadding"] = 9,
                ["m_AtlasRenderMode"] = 4169, // SDFAA
                ["m_AtlasPopulationMode"] = 1, // Dynamic
                ["m_AtlasTextures"] = new List<object>
                {
                    new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 9001L }
                },
                ["material"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 9002L },
                ["m_GlyphTable"] = new List<object> { new object(), new object(), new object() },
                ["m_CharacterTable"] = new List<object> { new object(), new object() },
                ["m_FallbackFontAssetTable"] = new List<object>
                {
                    new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 9003L }
                }
            };

            Assert.True(TmpFontPreviewService.IsTmpFontScriptName("TMP_FontAsset"));
            Assert.True(TmpFontPreviewService.IsTmpFontScriptName("TMPro.TMP_FontAsset"));
            Assert.False(TmpFontPreviewService.IsTmpFontScriptName("PlayerController"));

            // Detected via field signature alone (no script name needed -> works for IL2CPP/TypeTree)
            Assert.True(TmpFontPreviewService.TryExtract(monoBehaviour: null, modernFields, out var modernInfo, scriptClassNameOverride: null));
            Assert.NotNull(modernInfo);
            Assert.Equal("Liberation Sans", modernInfo!.FamilyName);
            Assert.Equal("Regular", modernInfo.StyleName);
            Assert.Equal(86f, modernInfo.PointSize);
            Assert.Equal(1024, modernInfo.AtlasWidth);
            Assert.Equal(1024, modernInfo.AtlasHeight);
            Assert.Equal(9, modernInfo.AtlasPadding);
            Assert.Equal("SDFAA_HINTED", modernInfo.AtlasRenderMode);
            Assert.Equal("SDFAA", TmpFontPreviewService.FormatGlyphRenderMode(4165));
            Assert.Equal("Dynamic", modernInfo.AtlasPopulationMode);
            Assert.Equal(3, modernInfo.GlyphCount);
            Assert.Equal(2, modernInfo.CharacterCount);
            Assert.Single(modernInfo.AtlasTextureRefs);
            Assert.Equal(new TmpPPtrRef(0, 9001L), modernInfo.AtlasTextureRefs[0]);
            Assert.Equal(new TmpPPtrRef(0, 9002L), modernInfo.MaterialRef);
            Assert.Single(modernInfo.FallbackFontRefs);
            Assert.Equal(new TmpPPtrRef(0, 9003L), modernInfo.FallbackFontRefs[0]);

            var overlay = modernInfo.FormatInfoOverlay(resolvedAtlas: null, fallbackFontNames: new[] { "NotoSansCJK SDF" });
            Assert.Contains("TMP_FontAsset", overlay);
            Assert.Contains("Liberation Sans", overlay);
            Assert.Contains("1024x1024", overlay);
            Assert.Contains("SDFAA", overlay);
            Assert.Contains("NotoSansCJK SDF", overlay);

            // 2) Legacy TMP 1.x fields (m_fontInfo + atlas + m_glyphInfoList)
            var legacyFields = new OrderedDictionary
            {
                ["m_Name"] = "LegacyTmpFont",
                ["m_fontInfo"] = new OrderedDictionary
                {
                    ["Name"] = "Arial SDF",
                    ["PointSize"] = 48f,
                    ["LineHeight"] = 56f,
                    ["AtlasWidth"] = 512f,
                    ["AtlasHeight"] = 512f,
                    ["Padding"] = 5f,
                    ["CharacterCount"] = 95
                },
                ["atlas"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 8001L },
                ["m_glyphInfoList"] = new List<object> { new object(), new object() }
            };

            Assert.True(TmpFontPreviewService.TryExtract(monoBehaviour: null, legacyFields, out var legacyInfo));
            Assert.NotNull(legacyInfo);
            Assert.Equal("Arial SDF", legacyInfo!.FamilyName);
            Assert.Equal(512, legacyInfo.AtlasWidth);
            Assert.Equal(512, legacyInfo.AtlasHeight);
            Assert.Single(legacyInfo.AtlasTextureRefs);
            Assert.Equal(8001L, legacyInfo.AtlasTextureRefs[0].PathId);

            // 3) Unrelated MonoBehaviour fields -> false
            var unrelatedFields = new OrderedDictionary
            {
                ["m_Name"] = "QuestConfig",
                ["questId"] = 42,
                ["rewardGold"] = 500
            };
            Assert.False(TmpFontPreviewService.TryExtract(monoBehaviour: null, unrelatedFields, out var noInfo, scriptClassNameOverride: "QuestData"));
            Assert.Null(noInfo);
        }

        [Fact]
        public void ExportViewModel_ReportStageProgress_UpdatesFormattedPercent_AndMainWindowViewModel_DisablesPreviewExportWhileRunning()
        {
            using var vm = new MainWindowViewModel();
            vm.PreviewSession.ActivateSurface(PreviewSurfaceKind.Mesh3D, asset: null);
            vm.MeshPreview.IsGeometryControlsVisible = true;

            Assert.True(vm.CanExportPreviewModel);
            Assert.True(vm.CanStartPreviewModelExport);
            Assert.True(vm.ExportPreviewModelCommand.CanExecute(null));

            vm.Export.BeginExport(ExportOperationMode.Animator, 100, targetFolder: "C:\\out", initialStatusText: "Preparing...");
            Assert.True(vm.Export.IsExporting);
            Assert.False(vm.CanStartPreviewModelExport);
            Assert.False(vm.ExportPreviewModelCommand.CanExecute(null));
            Assert.Equal("0%", vm.Export.FormattedPercentText);

            vm.Export.ReportStageProgress(45, "Preparing part 2/4...");
            Assert.Equal(45, vm.Export.ProgressPercent);
            Assert.Equal("45%", vm.Export.FormattedPercentText);
            Assert.Equal("Preparing part 2/4...", vm.Export.StatusText);
            Assert.Equal(45, vm.LoadingProgress);

            vm.Export.ReportStageProgress(120, "Writing hero.glb...");
            Assert.Equal(100, vm.Export.ProgressPercent);
            Assert.Equal("100%", vm.Export.FormattedPercentText);
            Assert.Equal(100, vm.LoadingProgress);

            vm.Export.Complete(1, statusText: "Finished exporting hero.glb");
            Assert.False(vm.Export.IsExporting);
            Assert.True(vm.CanStartPreviewModelExport);
            Assert.True(vm.ExportPreviewModelCommand.CanExecute(null));
        }

        #endregion

        #region Helpers

        private static T CreateUninitialized<T>() where T : class
            => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        private static SerializedFile CreateSyntheticSerializedFile(string fileName)
        {
            var file = CreateUninitialized<SerializedFile>();
            file.fileName = fileName;
            file.fullName = fileName;
            file.originalPath = fileName;
            file.Objects = new List<AssetStudio.Object>();
            file.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            file.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            file.m_Externals = new List<FileIdentifier>();
            var lockField = typeof(SerializedFile).GetField("objectCollectionLock", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(lockField);
            lockField!.SetValue(file, new object());
            return file;
        }

        private static T CreateObjectInFile<T>(SerializedFile file, long pathId, ClassIDType type, string name)
            where T : AssetStudio.Object
        {
            var obj = CreateUninitialized<T>();
            obj.assetsFile = file;
            obj.m_PathID = pathId;
            obj.type = type;
            if (obj is NamedObject named)
            {
                named.m_Name = name;
            }
            else if (obj is GameObject go)
            {
                go.m_Name = name;
            }

            file.Objects.Add(obj);
            file.ObjectsDic[pathId] = obj;
            return obj;
        }

        private static PPtr<T> CreatePPtr<T>(SerializedFile file, long pathId, int fileId = 0)
            where T : AssetStudio.Object
        {
            var pptr = CreateUninitialized<PPtr<T>>();
            pptr.m_FileID = fileId;
            pptr.m_PathID = pathId;
            var assetsFileField = typeof(PPtr<T>).GetField("assetsFile", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(assetsFileField);
            assetsFileField!.SetValue(pptr, file);
            return pptr;
        }

        private static bool ReadPrivateBoolField(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (bool)field!.GetValue(target)!;
        }

        private static byte[] BuildMinimalCmapFormat4Table(ushort startCode, ushort endCode)
        {
            // Header (4) + 1 EncodingRecord (8) = 12 bytes, followed by Format 4 subtable (2 segments: range + 0xFFFF sentinel)
            const int subtableOffset = 12;
            const int segCount = 2;
            const int subtableLen = 14 + segCount * 2 + 2 + segCount * 2;
            var data = new byte[subtableOffset + subtableLen];
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0, 2), 0); // version
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2, 2), 1); // numTables
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), 3); // Windows platform
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), 1); // Unicode BMP
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), subtableOffset);

            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(subtableOffset, 2), 4); // format 4
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(subtableOffset + 2, 2), (ushort)subtableLen);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(subtableOffset + 6, 2), segCount * 2); // segCountX2

            var endCodes = subtableOffset + 14;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(endCodes, 2), endCode);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(endCodes + 2, 2), 0xFFFF);

            var startCodes = endCodes + segCount * 2 + 2; // +2 reservedPad
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(startCodes, 2), startCode);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(startCodes + 2, 2), 0xFFFF);
            return data;
        }

        private static byte[] BuildMinimalCmapFormat12Table(uint startCode, uint endCode)
        {
            const int subtableOffset = 12;
            const int subtableLen = 16 + 12;
            var data = new byte[subtableOffset + subtableLen];
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), 3);  // Windows
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), 10); // Full Unicode
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), subtableOffset);

            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(subtableOffset, 2), 12); // format 12
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(subtableOffset + 4, 4), (uint)subtableLen);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(subtableOffset + 12, 4), 1u); // numGroups
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(subtableOffset + 16, 4), startCode);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(subtableOffset + 20, 4), endCode);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(subtableOffset + 24, 4), 1u);
            return data;
        }

        private static string LocateRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AssetStudio.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate repository root containing AssetStudio.sln.");
        }

        #endregion
    }
}
