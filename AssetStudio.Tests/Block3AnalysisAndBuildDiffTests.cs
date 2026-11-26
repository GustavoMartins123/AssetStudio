using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class Block3AnalysisAndBuildDiffTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        // =========================================================================
        // 1. BuildDiffService Tests (ANALYSIS-02)
        // =========================================================================

        [Fact]
        public void BuildDiff_CompareAssetSnapshots_DetectsAddedRemovedModifiedMovedAndUnchanged()
        {
            var baseline = new List<BuildDiffAssetSnapshot>
            {
                new("shared0#h#1", "shared0.assets", "v1/shared0.assets", 1, "HeroAlbedo", ClassIDType.Texture2D, "assets/tex/hero.png", 100, 4096),
                new("shared0#h#2", "shared0.assets", "v1/shared0.assets", 2, "HeroMat", ClassIDType.Material, "assets/mat/hero.mat", 200, 1024),
                new("shared0#h#3", "shared0.assets", "v1/shared0.assets", 3, "SwordMesh", ClassIDType.Mesh, "assets/mesh/sword.fbx", 300, 8192),
                new("shared0#h#4", "shared0.assets", "v1/shared0.assets", 4, "LegacyLogo", ClassIDType.Texture2D, "assets/ui/legacy.png", 400, 512)
            };

            var target = new List<BuildDiffAssetSnapshot>
            {
                // Unchanged
                new("shared0#h#1", "shared0.assets", "v2/shared0.assets", 1, "HeroAlbedo", ClassIDType.Texture2D, "assets/tex/hero.png", 100, 4096),
                // Modified (size + name + container changed)
                new("shared0#h#2", "shared0.assets", "v2/shared0.assets", 2, "HeroMat_PBR", ClassIDType.Material, "assets/mat/hero_pbr.mat", 200, 2048),
                // Moved (same PathID, Type, Name, different serialized file!)
                new("weapons#h#3", "weapons.assets", "v2/weapons.assets", 3, "SwordMesh", ClassIDType.Mesh, "assets/mesh/sword.fbx", 300, 8192),
                // Added
                new("weapons#h#5", "weapons.assets", "v2/weapons.assets", 5, "ShieldMesh", ClassIDType.Mesh, "assets/mesh/shield.fbx", 500, 6144)
            };

            var baseFiles = new List<ScannedFileInfo>
            {
                new("shared0.assets", 10000, ProjectFileKind.SerializedFile, 1000, 0x1111UL),
                new("removed.assets", 2500, ProjectFileKind.SerializedFile, 1000, 0x2222UL),
                new("same.assets", 500, ProjectFileKind.SerializedFile, 1000, 0x3333UL)
            };

            var targetFiles = new List<ScannedFileInfo>
            {
                new("shared0.assets", 12000, ProjectFileKind.SerializedFile, 2000, 0x9999UL),
                new("weapons.assets", 9000, ProjectFileKind.SerializedFile, 2000, 0x4444UL),
                new("same.assets", 500, ProjectFileKind.SerializedFile, 1000, 0x3333UL)
            };

            var report = BuildDiffService.CompareAssetSnapshots(
                "Build_1.0",
                baseline,
                "Build_1.1",
                target,
                baseFiles,
                targetFiles);

            Assert.True(report.HasAssetLevelDiff);
            Assert.Equal(1, report.AddedAssets);
            Assert.Equal(1, report.RemovedAssets);
            Assert.Equal(1, report.ModifiedAssets);
            Assert.Equal(1, report.MovedAssets);
            Assert.Equal(1, report.UnchangedAssets);

            Assert.Equal(1, report.AddedFiles);
            Assert.Equal(1, report.RemovedFiles);
            Assert.Equal(1, report.ModifiedFiles);
            Assert.Equal(1, report.UnchangedFiles);

            var modified = Assert.Single(report.AssetEntries, a => a.ChangeKind == BuildDiffChangeKind.Modified);
            Assert.Equal(1024, modified.SizeDelta);
            Assert.Contains("Size:", modified.Detail, StringComparison.Ordinal);
            Assert.Contains("Name:", modified.Detail, StringComparison.Ordinal);
            Assert.Contains("Container:", modified.Detail, StringComparison.Ordinal);

            var moved = Assert.Single(report.AssetEntries, a => a.ChangeKind == BuildDiffChangeKind.Moved);
            Assert.Equal("shared0.assets", moved.BaselineSerializedFileName);
            Assert.Equal("weapons.assets", moved.TargetSerializedFileName);
            Assert.Contains("Moved:", moved.Detail, StringComparison.Ordinal);

            var added = Assert.Single(report.AssetEntries, a => a.ChangeKind == BuildDiffChangeKind.Added);
            Assert.Equal("ShieldMesh", added.Name);

            var removed = Assert.Single(report.AssetEntries, a => a.ChangeKind == BuildDiffChangeKind.Removed);
            Assert.Equal("LegacyLogo", removed.Name);
        }

        [Fact]
        public void BuildDiff_CompareFolders_UsesSidecarSqliteAndLoadedIndex_AndFallsBackWhenUnindexed()
        {
            var tempRoot = CreateTempDirectory("build_diff_folders");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var v1Dir = Path.Combine(tempRoot, "BuildV1");
                var v2Dir = Path.Combine(tempRoot, "BuildV2");
                var v3UnindexedDir = Path.Combine(tempRoot, "BuildV3Unindexed");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(v1Dir);
                Directory.CreateDirectory(v2Dir);
                Directory.CreateDirectory(v3UnindexedDir);

                var fixedTime = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
                var newerTime = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

                // Write UnityFS header bytes so ProjectScanner classifies them as UnityBundle
                var bundleHeaderA = BuildMinimalUnityFsHeader("a");
                var bundleHeaderB1 = BuildMinimalUnityFsHeader("b_version_1");
                var bundleHeaderB2 = BuildMinimalUnityFsHeader("b_version_2_expanded_payload");
                var bundleHeaderC = BuildMinimalUnityFsHeader("c_new_bundle");

                WriteFileWithTimestamp(Path.Combine(v1Dir, "bundle_a.unity3d"), bundleHeaderA, fixedTime);
                WriteFileWithTimestamp(Path.Combine(v1Dir, "bundle_b.unity3d"), bundleHeaderB1, fixedTime);
                WriteFileWithTimestamp(Path.Combine(v1Dir, "readme_noise.txt"), Encoding.UTF8.GetBytes("noise v1"), fixedTime);

                WriteFileWithTimestamp(Path.Combine(v2Dir, "bundle_a.unity3d"), bundleHeaderA, fixedTime);
                WriteFileWithTimestamp(Path.Combine(v2Dir, "bundle_b.unity3d"), bundleHeaderB2, newerTime);
                WriteFileWithTimestamp(Path.Combine(v2Dir, "bundle_c.unity3d"), bundleHeaderC, newerTime);
                WriteFileWithTimestamp(Path.Combine(v2Dir, "notes_noise.txt"), Encoding.UTF8.GetBytes("noise v2"), newerTime);

                WriteFileWithTimestamp(Path.Combine(v3UnindexedDir, "bundle_a.unity3d"), bundleHeaderA, fixedTime);
                WriteFileWithTimestamp(Path.Combine(v3UnindexedDir, "bundle_d.unity3d"), bundleHeaderC, newerTime);

                using var cache = new SQLiteProjectIndexCache(cacheDir);

                // 1) Unindexed fallback: neither folder is cached yet; must produce file-level diff & not create empty DBs
                var unindexedReport = BuildDiffService.CompareFolders(v1Dir, v3UnindexedDir, cache);
                Assert.False(unindexedReport.HasAssetLevelDiff);
                Assert.Empty(unindexedReport.AssetEntries);
                Assert.Contains("File-level diff only", unindexedReport.AssetDiffNote, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(1, unindexedReport.AddedFiles);     // bundle_d.unity3d
                Assert.Equal(1, unindexedReport.RemovedFiles);   // bundle_b.unity3d
                Assert.Equal(1, unindexedReport.UnchangedFiles); // bundle_a.unity3d (noise .txt filtered out!)
                Assert.False(File.Exists(cache.GetDbPath(v1Dir)));
                Assert.False(File.Exists(cache.GetDbPath(v3UnindexedDir)));

                // 2) Index v1 and v2 into SQLite + .idx sidecar
                var v1Scan = ProjectScanner.ScanFolder(v1Dir);
                var v2Scan = ProjectScanner.ScanFolder(v2Dir);
                var v1Sig = cache.GetFolderSignature(v1Scan);
                var v2Sig = cache.GetFolderSignature(v2Scan);

                var v1Handles = new List<AssetHandle>
                {
                    CreateHandle("CAB-A", Path.Combine(v1Dir, "bundle_a.unity3d"), 10, "CommonTex", ClassIDType.Texture2D, "assets/common.png", 1024),
                    CreateHandle("CAB-B", Path.Combine(v1Dir, "bundle_b.unity3d"), 20, "ConfigText", ClassIDType.TextAsset, "assets/config.json", 512),
                    CreateHandle("CAB-B", Path.Combine(v1Dir, "bundle_b.unity3d"), 30, "RelocatedMesh", ClassIDType.Mesh, "assets/mesh/reloc.fbx", 4096)
                };
                var v2Handles = new List<AssetHandle>
                {
                    CreateHandle("CAB-A", Path.Combine(v2Dir, "bundle_a.unity3d"), 10, "CommonTex", ClassIDType.Texture2D, "assets/common.png", 1024),
                    CreateHandle("CAB-B", Path.Combine(v2Dir, "bundle_b.unity3d"), 20, "ConfigText", ClassIDType.TextAsset, "assets/config.json", 768),
                    CreateHandle("CAB-C", Path.Combine(v2Dir, "bundle_c.unity3d"), 30, "RelocatedMesh", ClassIDType.Mesh, "assets/mesh/reloc.fbx", 4096),
                    CreateHandle("CAB-C", Path.Combine(v2Dir, "bundle_c.unity3d"), 40, "NewShader", ClassIDType.Shader, "assets/shaders/new.shader", 2048)
                };

                Assert.True(cache.SaveIndexCache(v1Dir, v1Sig, v1Scan, "2022.3.10f1", v1Handles, markIndexingCompleted: true));
                Assert.True(cache.SaveIndexCache(v2Dir, v2Sig, v2Scan, "2022.3.10f1", v2Handles, markIndexingCompleted: true));
                Assert.True(File.Exists(cache.GetIdxPath(v1Dir)));
                Assert.True(File.Exists(cache.GetIdxPath(v2Dir)));

                // Compare via .idx sidecar path
                var sidecarReport = BuildDiffService.CompareFolders(v1Dir, v2Dir, cache);
                Assert.True(sidecarReport.HasAssetLevelDiff);
                Assert.Contains("MmapSidecar", sidecarReport.AssetDiffNote, StringComparison.Ordinal);
                Assert.Equal(1, sidecarReport.AddedAssets);
                Assert.Equal(0, sidecarReport.RemovedAssets);
                Assert.Equal(1, sidecarReport.ModifiedAssets);
                Assert.Equal(1, sidecarReport.MovedAssets);
                Assert.Equal(1, sidecarReport.UnchangedAssets);
                Assert.Equal(1, sidecarReport.AddedFiles);
                Assert.Equal(1, sidecarReport.ModifiedFiles);
                Assert.Equal(1, sidecarReport.UnchangedFiles);

                // Delete v2 .idx sidecar and confirm SQLite fallback + loadedBaselineIndex both work cleanly
                File.Delete(cache.GetIdxPath(v2Dir));
                var loadedIndex = new ProjectIndex();
                foreach (var h in v1Handles)
                {
                    loadedIndex.AddHandle(h);
                }

                var sqliteFallbackReport = BuildDiffService.CompareFolders(v1Dir, v2Dir, cache, loadedBaselineIndex: loadedIndex);
                Assert.True(sqliteFallbackReport.HasAssetLevelDiff);
                Assert.Contains("LoadedIndex", sqliteFallbackReport.AssetDiffNote, StringComparison.Ordinal);
                Assert.Contains("SQLiteCache", sqliteFallbackReport.AssetDiffNote, StringComparison.Ordinal);
                Assert.Equal(1, sqliteFallbackReport.AddedAssets);
                Assert.Equal(1, sqliteFallbackReport.ModifiedAssets);
                Assert.Equal(1, sqliteFallbackReport.MovedAssets);
                Assert.Equal(1, sqliteFallbackReport.UnchangedAssets);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void BuildDiff_ExportJsonCsvAndMarkdown_WritesValidUtf8NoBomAndEscapesCsvFields()
        {
            var tempRoot = CreateTempDirectory("build_diff_exports");
            try
            {
                var baseline = new List<BuildDiffAssetSnapshot>
                {
                    new("s0#h#1", "s0.assets", "v1/s0.assets", 1, "Hero, \"Prime\"\nTex", ClassIDType.Texture2D, "assets/a,b.png", 0, 100)
                };
                var target = new List<BuildDiffAssetSnapshot>
                {
                    new("s0#h#1", "s0.assets", "v2/s0.assets", 1, "Hero, \"Prime\"\nTex", ClassIDType.Texture2D, "assets/a,b.png", 0, 250),
                    new("s1#h#2", "s1.assets", "v2/s1.assets", 2, "ExtraMat", ClassIDType.Material, "assets/m.mat", 0, 400)
                };

                var report = BuildDiffService.CompareAssetSnapshots("v1", baseline, "v2", target);
                var jsonPath = Path.Combine(tempRoot, "diff.json");
                var csvPath = Path.Combine(tempRoot, "diff.csv");

                BuildDiffService.ExportJson(report, jsonPath);
                BuildDiffService.ExportCsv(report, csvPath);
                var markdown = BuildDiffService.FormatSummaryMarkdown(report);

                // Verify UTF-8 without BOM
                var jsonBytes = File.ReadAllBytes(jsonPath);
                var csvBytes = File.ReadAllBytes(csvPath);
                Assert.False(HasUtf8Bom(jsonBytes));
                Assert.False(HasUtf8Bom(csvBytes));

                // Verify valid JSON structure
                using var doc = JsonDocument.Parse(jsonBytes);
                Assert.Equal(1, doc.RootElement.GetProperty("AddedAssets").GetInt32());
                Assert.Equal(1, doc.RootElement.GetProperty("ModifiedAssets").GetInt32());
                Assert.Equal("Modified", doc.RootElement.GetProperty("AssetEntries")[1].GetProperty("ChangeKind").GetString());

                // Verify proper CSV escaping for commas, quotes, and embedded newlines
                var csvText = Encoding.UTF8.GetString(csvBytes);
                Assert.StartsWith("Scope,ChangeKind,Type,NameOrPath,PathID,BaselineSource,TargetSource,BaselineBytes,TargetBytes,SizeDelta,Detail", csvText, StringComparison.Ordinal);
                Assert.Contains("\"Hero, \"\"Prime\"\"\nTex\"", csvText, StringComparison.Ordinal);
                Assert.Contains("ExtraMat", csvText, StringComparison.Ordinal);

                // Verify Markdown summary
                Assert.Contains("# Build Diff Report", markdown, StringComparison.Ordinal);
                Assert.Contains("| Added | 1 |", markdown, StringComparison.Ordinal);
                Assert.Contains("| Modified | 1 |", markdown, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void BuildDiff_CompareFolders_RespectsCancellationToken()
        {
            var tempRoot = CreateTempDirectory("build_diff_cancel");
            try
            {
                var d1 = Path.Combine(tempRoot, "d1");
                var d2 = Path.Combine(tempRoot, "d2");
                Directory.CreateDirectory(d1);
                Directory.CreateDirectory(d2);

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                Assert.ThrowsAny<OperationCanceledException>(() =>
                    BuildDiffService.CompareFolders(d1, d2, sqliteCache: null, loadedBaselineIndex: null, ct: cts.Token));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 2. SQLiteProjectIndexCache.AnalysisQueries Tests (ANALYSIS-04)
        // =========================================================================

        [Fact]
        public void SqliteAnalysisQueries_ShaderTextureMaterialAvatarAndDispatcher_QueryRealSemanticDatabase()
        {
            var tempRoot = CreateTempDirectory("sqlite_analysis_queries");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "ProjectData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);

                var srcPath = Path.Combine(projectDir, "sharedassets0.assets");
                File.WriteAllBytes(srcPath, BuildMinimalUnityFsHeader("semantic_test"));

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scanResult = ProjectScanner.ScanFolder(projectDir);
                var signature = cache.GetFolderSignature(scanResult);

                var shaderUid = "sharedassets0.assets#h#100";
                var texMainUid = "sharedassets0.assets#h#200";
                var texNormalUid = "sharedassets0.assets#h#201";
                var matHeroUid = "sharedassets0.assets#h#300";
                var matArmorUid = "sharedassets0.assets#h#301";
                var meshHeroUid = "sharedassets0.assets#h#400";
                var meshArmorUid = "sharedassets0.assets#h#401";
                var goHeroUid = "sharedassets0.assets#h#500";
                var animatorUid = "sharedassets0.assets#h#501";
                var avatarUid = "sharedassets0.assets#h#502";
                var clipRunUid = "sharedassets0.assets#h#600";
                var clipIdleUid = "sharedassets0.assets#h#601";
                var groupId = "modelgroup:animator:hero";

                var handles = new List<AssetHandle>
                {
                    CreateHandleWithUid("sharedassets0.assets", srcPath, shaderUid, 100, "Custom/ToonLit", ClassIDType.Shader, "assets/shaders/toonlit.shader", 2048),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, texMainUid, 200, "Hero_Albedo", ClassIDType.Texture2D, "assets/tex/hero_albedo.png", 65536),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, texNormalUid, 201, "Hero_Normal", ClassIDType.Texture2D, "assets/tex/hero_normal.png", 32768),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, matHeroUid, 300, "Hero_Mat", ClassIDType.Material, "assets/mat/hero.mat", 1024),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, matArmorUid, 301, "Armor_Mat", ClassIDType.Material, "assets/mat/armor.mat", 1024),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, meshHeroUid, 400, "Hero_BodyMesh", ClassIDType.Mesh, "assets/mesh/hero.fbx", 16384),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, meshArmorUid, 401, "Armor_Mesh", ClassIDType.Mesh, "assets/mesh/armor.fbx", 8192),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, goHeroUid, 500, "HeroRoot", ClassIDType.GameObject, string.Empty, 256),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, animatorUid, 501, "HeroAnimator", ClassIDType.Animator, string.Empty, 256),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, avatarUid, 502, "HeroAvatar", ClassIDType.Avatar, string.Empty, 512),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, clipRunUid, 600, "Hero_Run", ClassIDType.AnimationClip, "assets/anim/run.anim", 4096),
                    CreateHandleWithUid("sharedassets0.assets", srcPath, clipIdleUid, 601, "Hero_Idle", ClassIDType.AnimationClip, "assets/anim/idle.anim", 3072)
                };

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, markIndexingCompleted: true));

                var relations = new SemanticAssetRelations();
                // Shader -> Materials via MaterialCache.ShaderName AND AssetEdges
                relations.MaterialCaches.Add(new SemanticMaterialCacheEntry(
                    matHeroUid, "Hero_Mat", string.Empty, texMainUid, "_BaseMap",
                    shaderName: "Custom/ToonLit", textureSlotCount: 2));
                relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                    matArmorUid, "Shader", "m_Shader", 0, shaderUid, 0, 301, 0, 100, true));

                // Texture -> Materials via MaterialTextures (both main and non-main _BumpMap slots)
                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    matHeroUid, matHeroUid, "_BaseMap", 0, texMainUid, 0, 200, true, true));
                relations.MaterialTextures.Add(new SemanticMaterialTextureRelation(
                    matHeroUid, matHeroUid, "_BumpMap", 1, texNormalUid, 0, 201, true, false));

                // Materials -> Meshes
                relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshHeroUid, matHeroUid, "sharedassets0.assets#h#700", "SkinnedMeshRenderer", 0, 0, 100));
                relations.MeshMaterials.Add(new SemanticMeshMaterialRelation(
                    meshArmorUid, matArmorUid, "sharedassets0.assets#h#701", "MeshRenderer", 0, 0, 90));

                // ModelGroups & CompositeAssets
                relations.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId, "Animator", "HeroCharacterModel", goHeroUid, "HeroRoot", animatorUid, avatarUid, string.Empty, "sharedassets0.assets", 95, "Exact"));
                relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                    groupId, meshHeroUid, "sharedassets0.assets#h#700", "SkinnedMeshRenderer", goHeroUid, "HeroRoot", 0, null, 95, "Exact"));
                relations.CompositeAssets.Add(new SemanticCompositeAssetRelation(
                    groupId, "Animator", "HeroCharacterModel", goHeroUid, animatorUid, avatarUid, string.Empty, "sharedassets0.assets", 1, 1, 1, 95));
                relations.CompositeAssetParts.Add(new SemanticCompositeAssetPartRelation(
                    groupId, 0, meshHeroUid, "sharedassets0.assets#h#700", "SkinnedMeshRenderer", goHeroUid, "HeroRoot", null, matHeroUid, 95));

                // Avatar / ModelGroup -> AnimationClips
                relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                    clipRunUid, animatorUid, avatarUid, meshHeroUid, groupId, "AvatarHumanoid", 98, 42, 42));
                relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                    clipIdleUid, animatorUid, avatarUid, string.Empty, groupId, "BoneHashMatch", 85, 38, 40));

                using (var session = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    session.FlushBatch(relations, clearBatch: false);
                    Assert.True(session.Commit());
                }

                // 1) FindMaterialsUsingShader (by shader AssetUniqueId AND by ShaderName)
                var byShaderId = cache.FindMaterialsUsingShader(projectDir, signature, shaderUid);
                Assert.Contains(byShaderId.Items, i => i.AssetUniqueId == matHeroUid);
                Assert.Contains(byShaderId.Items, i => i.AssetUniqueId == matArmorUid);

                var byShaderName = cache.FindMaterialsUsingShader(projectDir, signature, "Custom/ToonLit");
                Assert.Contains(byShaderName.Items, i => i.AssetUniqueId == matHeroUid);
                Assert.Contains(byShaderName.Items, i => i.AssetUniqueId == matArmorUid);

                // 2) FindUsagesOfTexture -> finds Material AND downstream Mesh AND downstream Model!
                var mainTexUsages = cache.FindUsagesOfTexture(projectDir, signature, texMainUid);
                Assert.Contains(mainTexUsages.Items, i => i.AssetUniqueId == matHeroUid && i.AssetType == ClassIDType.Material);
                Assert.Contains(mainTexUsages.Items, i => i.AssetUniqueId == meshHeroUid && i.AssetType == ClassIDType.Mesh);
                Assert.Contains(mainTexUsages.Items, i => i.AssetUniqueId == goHeroUid && i.Name == "HeroCharacterModel");

                // Also works for non-main texture slot (_BumpMap) via MaterialTextures!
                var normalTexUsages = cache.FindUsagesOfTexture(projectDir, signature, texNormalUid);
                Assert.Contains(normalTexUsages.Items, i => i.AssetUniqueId == matHeroUid);
                Assert.Contains(normalTexUsages.Items, i => i.AssetUniqueId == meshHeroUid);

                // 3) FindModelsUsingMaterial -> finds Mesh + CompositeAsset/ModelGroup
                var matUsages = cache.FindModelsUsingMaterial(projectDir, signature, matHeroUid);
                Assert.Contains(matUsages.Items, i => i.AssetUniqueId == meshHeroUid);
                Assert.Contains(matUsages.Items, i => i.AssetUniqueId == goHeroUid && i.Name == "HeroCharacterModel");

                // 4) FindAnimationsForAvatarOrRig -> works by AvatarUid, ModelGroupId, AND AnimatorUid, ordered by MatchScore DESC
                var animsByAvatar = cache.FindAnimationsForAvatarOrRig(projectDir, signature, avatarUid);
                Assert.Equal(2, animsByAvatar.Items.Count);
                Assert.Equal(clipRunUid, animsByAvatar.Items[0].AssetUniqueId);
                Assert.Equal(clipIdleUid, animsByAvatar.Items[1].AssetUniqueId);
                Assert.Contains("Score 98", animsByAvatar.Items[0].Detail, StringComparison.Ordinal);
                Assert.Contains("Bones 42/42", animsByAvatar.Items[0].Detail, StringComparison.Ordinal);

                var animsByGroup = cache.FindAnimationsForAvatarOrRig(projectDir, signature, groupId);
                Assert.Equal(2, animsByGroup.Items.Count);

                // 5) Unified GetAssetUsageSummary smart dispatcher
                var shaderSummary = cache.GetAssetUsageSummary(projectDir, signature, shaderUid, ClassIDType.Shader, "Custom/ToonLit");
                Assert.Equal("Shader", shaderSummary.QueryKind);
                Assert.Equal(2, shaderSummary.Items.Count);

                var texSummary = cache.GetAssetUsageSummary(projectDir, signature, texMainUid, ClassIDType.Texture2D, "Hero_Albedo");
                Assert.Equal("Texture", texSummary.QueryKind);
                Assert.True(texSummary.Items.Count >= 3);

                var matSummary = cache.GetAssetUsageSummary(projectDir, signature, matHeroUid, ClassIDType.Material, "Hero_Mat");
                Assert.Equal("Material", matSummary.QueryKind);
                Assert.True(matSummary.Items.Count >= 2);

                var avatarSummary = cache.GetAssetUsageSummary(projectDir, signature, avatarUid, ClassIDType.Avatar, "HeroAvatar");
                Assert.Equal("AnimationRig", avatarSummary.QueryKind);
                Assert.Equal(2, avatarSummary.Items.Count);

                // AnimationClip dispatches to LoadAssetReferencesFromSemanticCache fallback
                var clipSummary = cache.GetAssetUsageSummary(projectDir, signature, clipRunUid, ClassIDType.AnimationClip, "Hero_Run");
                Assert.Equal("References", clipSummary.QueryKind);
                Assert.Contains(clipSummary.Items, i => i.AssetUniqueId == avatarUid);

                // Bounded limit truncation test
                var capped = cache.FindAnimationsForAvatarOrRig(projectDir, signature, avatarUid, maxResults: 1);
                Assert.Single(capped.Items);
                Assert.True(capped.IsTruncated);

                // Built-in shader name lookup (no Shader row in Assets table, only MaterialCache.ShaderName)
                var builtinMatUid = "sharedassets0.assets#h#302";
                var extraRels = new SemanticAssetRelations();
                extraRels.MaterialCaches.Add(new SemanticMaterialCacheEntry(
                    builtinMatUid, "BuiltInStd_Mat", string.Empty, string.Empty, string.Empty,
                    shaderName: "Universal Render Pipeline/Lit", textureSlotCount: 0));
                using (var s2 = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    s2.FlushBatch(relations, clearBatch: false);
                    s2.FlushBatch(extraRels, clearBatch: false);
                    Assert.True(s2.Commit());
                }

                var urpMaterials = cache.FindMaterialsUsingShader(projectDir, signature, "Universal Render Pipeline/Lit");
                Assert.Contains(urpMaterials.Items, i => i.AssetUniqueId == builtinMatUid);

                // FindWhoUsesAsset wrapper: live cache + graceful unavailable fallback
                var viaService = GlobalProjectQueryService.FindWhoUsesAsset(cache, projectDir, signature, texMainUid, ClassIDType.Texture2D, "Hero_Albedo");
                Assert.Equal("Texture", viaService.QueryKind);
                Assert.True(viaService.Items.Count >= 3);

                var noCache = GlobalProjectQueryService.FindWhoUsesAsset(null, projectDir, signature, texMainUid, ClassIDType.Texture2D, "Hero_Albedo");
                Assert.Equal("Unavailable", noCache.QueryKind);
                Assert.Empty(noCache.Items);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SqliteAnalysisQueries_LoadAssetDiffSnapshots_FallsBackToLatestProjectAndGuardsIncompleteIndex()
        {
            var tempRoot = CreateTempDirectory("sqlite_diff_snapshots_fallback");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "ProjectData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);

                var srcPath = Path.Combine(projectDir, "bundle_0.unity3d");
                File.WriteAllBytes(srcPath, BuildMinimalUnityFsHeader("snap_v1"));

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scan1 = ProjectScanner.ScanFolder(projectDir);
                var sig1 = cache.GetFolderSignature(scan1);
                var handles = new List<AssetHandle>
                {
                    CreateHandle("CAB-0", srcPath, 77, "TerrainTex", ClassIDType.Texture2D, "assets/terrain.png", 4096)
                };
                Assert.True(cache.SaveIndexCache(projectDir, sig1, scan1, "2022.3.10f1", handles, markIndexingCompleted: true));

                // Asking with a mismatched newer signature falls back to FindLatestProjectId cleanly
                var latestFallback = cache.LoadAssetDiffSnapshots(projectDir, "v2:different_newer_signature");
                Assert.True(latestFallback.HasSnapshots);
                Assert.Single(latestFallback.Snapshots);
                Assert.Equal(77L, latestFallback.Snapshots[0].PathId);
                Assert.Contains("folder changed since last index", latestFallback.DiagnosticNote, StringComparison.OrdinalIgnoreCase);

                // Incomplete indexing status must refuse partial asset snapshots
                cache.SaveIndexingProgress(projectDir, sig1, scan1, new AssetStudio.Avalonia.Services.IndexingProgressUpdate
                {
                    Status = "running",
                    TotalFiles = 10,
                    ProcessedFiles = 2,
                    PendingFiles = 8
                });

                var incomplete = cache.LoadAssetDiffSnapshots(projectDir, sig1);
                Assert.False(incomplete.HasSnapshots);
                Assert.Contains("incomplete", incomplete.DiagnosticNote, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // =========================================================================
        // 3. GlobalProjectQueryService Tests (ANALYSIS-01 & ANALYSIS-04)
        // =========================================================================

        [Fact]
        public void GlobalSearch_SupportsPathIdGuidTypeFileExcludeQuotedAndCombinedQueries()
        {
            var index = new ProjectIndex();
            index.AddRecord("sharedassets0.assets", "/game/sharedassets0.assets", 101, "Hero_Diffuse", ClassIDType.Texture2D, "assets/tex/hero_diffuse.png", 0, 4096);
            index.AddRecord("sharedassets0.assets", "/game/sharedassets0.assets", 102, "Hero_UI_Icon", ClassIDType.Texture2D, "assets/ui/hero_icon.png", 0, 1024);
            index.AddRecord("sharedassets1.assets", "/game/sharedassets1.assets", 202, "Iron Sword Mat", ClassIDType.Material, "assets/weapons/iron_sword.mat", 0, 512);
            index.AddRecord("sharedassets1.assets", "/game/sharedassets1.assets", 303, "ToonLit", ClassIDType.Shader, "assets/shaders/toon.shader", 0, 2048);

            // 1) pathid: exact match
            var byPathId = GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = "pathid:202" }, index);
            var hitPath = Assert.Single(byPathId.Hits);
            Assert.Equal("Iron Sword Mat", hitPath.Name);
            Assert.Equal("PathID", hitPath.MatchReason);

            // 2) guid: matches UnityProjectGuidUtility.ComputeAssetGuid (both without and with fallback name!)
            var expectedGuidNoFallback = UnityProjectGuidUtility.ComputeAssetGuid("Texture2D", "sharedassets0.assets", 101);
            var byGuid = GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = $"guid:{expectedGuidNoFallback}" }, index);
            var hitGuid = Assert.Single(byGuid.Hits);
            Assert.Equal(101L, hitGuid.PathId);
            Assert.Equal("GUID", hitGuid.MatchReason);

            var expectedGuidWithName = UnityProjectGuidUtility.ComputeAssetGuid("Material", "sharedassets1.assets", 202, "Iron Sword Mat");
            var byGuidPrefix = GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = $"guid:{expectedGuidWithName[..12]}" }, index);
            var hitGuidPrefix = Assert.Single(byGuidPrefix.Hits);
            Assert.Equal(202L, hitGuidPrefix.PathId);

            // 3) type: + -exclude
            var byTypeAndExclude = GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = "type:Texture2D hero -ui" }, index);
            var hitFiltered = Assert.Single(byTypeAndExclude.Hits);
            Assert.Equal("Hero_Diffuse", hitFiltered.Name);

            // 4) file: + quoted phrase
            var byFileAndPhrase = GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = "file:sharedassets1 \"iron sword\"" }, index);
            var hitPhrase = Assert.Single(byFileAndPhrase.Hits);
            Assert.Equal("Iron Sword Mat", hitPhrase.Name);

            // 5) negative -type: filter
            var excludeShader = GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = "file:sharedassets1 -type:Shader" }, index);
            var nonShader = Assert.Single(excludeShader.Hits);
            Assert.Equal(ClassIDType.Material, nonShader.AssetType);
        }

        [Fact]
        public void GlobalSearch_RegexAndInvalidRegexHandling_AreSafeAndAccurate()
        {
            var index = new ProjectIndex();
            index.AddRecord("level0", "/game/level0", 1, "Hero_Idle_01", ClassIDType.AnimationClip, string.Empty, 0, 100);
            index.AddRecord("level0", "/game/level0", 2, "Hero_Run_02", ClassIDType.AnimationClip, string.Empty, 0, 100);
            index.AddRecord("level0", "/game/level0", 3, "Hero_Jump_99", ClassIDType.AnimationClip, string.Empty, 0, 100);

            var validRegexResult = GlobalProjectQueryService.SearchAssets(
                new GlobalSearchQuery { QueryText = @"^Hero_[A-Za-z]+_0[12]$", UseRegex = true },
                index);
            Assert.Null(validRegexResult.ErrorMessage);
            Assert.Equal(2, validRegexResult.Hits.Count);

            var invalidRegexResult = GlobalProjectQueryService.SearchAssets(
                new GlobalSearchQuery { QueryText = "[unclosed(group", UseRegex = true },
                index);
            Assert.NotNull(invalidRegexResult.ErrorMessage);
            Assert.Contains("Invalid regular expression", invalidRegexResult.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(invalidRegexResult.Hits);
        }

        [Fact]
        public void GlobalSearch_BoundedTextAssetContentSearch_ReturnsSnippetByteOffsetAndEnforcesSizeCap()
        {
            var index = new ProjectIndex();
            var smallContent = Encoding.UTF8.GetBytes("header: café\nquest_id = \"QUEST_ALPHA_42\"\nfooter = true");
            var secondContent = Encoding.UTF8.GetBytes("line1\nQUEST_ALPHA_42_BONUS");

            index.AddRecord("data.assets", "/game/data.assets", 10, "DialogueTable", ClassIDType.TextAsset, "assets/text/dialogue.txt", 0, smallContent.Length);
            index.AddRecord("data.assets", "/game/data.assets", 20, "HugeBinaryDump", ClassIDType.TextAsset, "assets/text/huge.bin", 0, GlobalProjectQueryService.MaxTextAssetBytes + 1024);
            index.AddRecord("data.assets", "/game/data.assets", 30, "QuestNotes", ClassIDType.TextAsset, "assets/text/notes.txt", 0, secondContent.Length);

            var hugeAssetWasRead = false;
            byte[]? ReadContent(AssetHandle handle)
            {
                if (handle.PathID == 10) return smallContent;
                if (handle.PathID == 20)
                {
                    hugeAssetWasRead = true;
                    return Encoding.UTF8.GetBytes("QUEST_ALPHA_42");
                }
                if (handle.PathID == 30) return secondContent;
                return null;
            }

            var result = GlobalProjectQueryService.SearchAssetsWithContentReader(
                new GlobalSearchQuery
                {
                    QueryText = "QUEST_ALPHA_42",
                    SearchTextAssetContents = true,
                    MaxTextAssetBytes = GlobalProjectQueryService.MaxTextAssetBytes,
                    MaxTextAssetsScanned = 500
                },
                index,
                assetsManager: null,
                textContentOverride: ReadContent);

            Assert.Null(result.ErrorMessage);
            Assert.False(hugeAssetWasRead, "Oversized TextAsset must be skipped before reading its payload.");
            Assert.Equal(2, result.TextAssetsScanned);
            Assert.Equal(1, result.TextAssetsSkippedBySizeCap);
            Assert.Equal(2, result.Hits.Count);

            var firstHit = Assert.Single(result.Hits, h => h.PathId == 10);
            Assert.Equal("TextAsset Content", firstHit.MatchReason);
            // "header: café\n" is 14 bytes in UTF-8 (é is 2 bytes), followed by "quest_id = \"" (12 bytes) => offset 26!
            var expectedOffset = Encoding.UTF8.GetByteCount("header: café\nquest_id = \"");
            Assert.Equal(expectedOffset, firstHit.MatchByteOffset);
            Assert.Contains("Line 2", firstHit.Detail, StringComparison.Ordinal);
            Assert.Contains("QUEST_ALPHA_42", firstHit.Detail, StringComparison.Ordinal);

            // Also verify MaxTextAssetsScanned cap stops scanning early
            var boundedResult = GlobalProjectQueryService.SearchAssetsWithContentReader(
                new GlobalSearchQuery
                {
                    QueryText = "QUEST_ALPHA_42",
                    SearchTextAssetContents = true,
                    MaxTextAssetsScanned = 1
                },
                index,
                assetsManager: null,
                textContentOverride: ReadContent);
            Assert.Equal(1, boundedResult.TextAssetsScanned);
            Assert.Single(boundedResult.Hits);
            Assert.True(boundedResult.IsTruncated);

            // Also verify regex + negative exclusion inside TextAsset content search
            var regexTextResult = GlobalProjectQueryService.SearchAssetsWithContentReader(
                new GlobalSearchQuery
                {
                    QueryText = @"QUEST_[A-Z]+_42 -BONUS",
                    UseRegex = true,
                    SearchTextAssetContents = true
                },
                index,
                assetsManager: null,
                textContentOverride: ReadContent);
            var regexHit = Assert.Single(regexTextResult.Hits);
            Assert.Equal(10L, regexHit.PathId);
            Assert.Equal(expectedOffset, regexHit.MatchByteOffset);
        }

        [Fact]
        public async Task BuildDiffDialogViewModel_CancelDiffCommand_CancelsInFlightComparisonCleanly()
        {
            using var started = new ManualResetEventSlim(false);
            using var vm = new BuildDiffDialogViewModel(
                sqliteCache: null,
                initialBaselineFolder: "C:/Builds/v1",
                initialTargetFolder: "C:/Builds/v2",
                diffRunner: (_, _, ct) =>
                {
                    started.Set();
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    ct.ThrowIfCancellationRequested();
                    return new BuildDiffReport();
                });

            var runTask = vm.RunDiffCommand.ExecuteAsync();
            Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(vm.IsRunning);
            Assert.True(vm.CancelDiffCommand.CanExecute());

            vm.CancelDiffCommand.Execute();
            await runTask;

            Assert.False(vm.IsRunning);
            Assert.False(vm.HasReport);
            Assert.Contains("cancel", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================================
        // 4. ViewModel & Pure-MVVM Tests
        // =========================================================================

        [Fact]
        public async Task BuildDiffDialogViewModel_CommandsFiltersToggleExportAndCancel_WorkEndToEnd()
        {
            var tempRoot = CreateTempDirectory("vm_build_diff");
            try
            {
                var syntheticReport = BuildDiffService.CompareAssetSnapshots(
                    "base_dir",
                    new[]
                    {
                        new BuildDiffAssetSnapshot("a#h#1", "a.assets", "v1/a.assets", 1, "KeepTex", ClassIDType.Texture2D, "", 0, 100),
                        new BuildDiffAssetSnapshot("a#h#2", "a.assets", "v1/a.assets", 2, "ModMat", ClassIDType.Material, "", 0, 200),
                        new BuildDiffAssetSnapshot("a#h#3", "a.assets", "v1/a.assets", 3, "MovedMesh", ClassIDType.Mesh, "", 0, 300),
                        new BuildDiffAssetSnapshot("a#h#4", "a.assets", "v1/a.assets", 4, "GoneAudio", ClassIDType.AudioClip, "", 0, 400)
                    },
                    "target_dir",
                    new[]
                    {
                        new BuildDiffAssetSnapshot("a#h#1", "a.assets", "v2/a.assets", 1, "KeepTex", ClassIDType.Texture2D, "", 0, 100),
                        new BuildDiffAssetSnapshot("a#h#2", "a.assets", "v2/a.assets", 2, "ModMat", ClassIDType.Material, "", 0, 500),
                        new BuildDiffAssetSnapshot("b#h#3", "b.assets", "v2/b.assets", 3, "MovedMesh", ClassIDType.Mesh, "", 0, 300),
                        new BuildDiffAssetSnapshot("b#h#5", "b.assets", "v2/b.assets", 5, "AddedShader", ClassIDType.Shader, "", 0, 600)
                    },
                    new[] { new ScannedFileInfo("a.assets", 1000, ProjectFileKind.SerializedFile, 10, 1UL) },
                    new[]
                    {
                        new ScannedFileInfo("a.assets", 1300, ProjectFileKind.SerializedFile, 20, 2UL),
                        new ScannedFileInfo("b.assets", 900, ProjectFileKind.SerializedFile, 20, 3UL)
                    });

                using var vm = new BuildDiffDialogViewModel(
                    sqliteCache: null,
                    diffRunner: (_, _, _) => syntheticReport);

                Assert.False(vm.RunDiffCommand.CanExecute());
                Assert.False(vm.ExportJsonCommand.CanExecute());
                Assert.False(vm.ExportCsvCommand.CanExecute());

                vm.PickBaselineFolderAsync = () => Task.FromResult<string?>("C:/Builds/v1");
                vm.PickTargetFolderAsync = () => Task.FromResult<string?>("C:/Builds/v2");

                await vm.PickBaselineCommand.ExecuteAsync();
                await vm.PickTargetCommand.ExecuteAsync();
                Assert.Equal("C:/Builds/v1", vm.BaselineFolder);
                Assert.Equal("C:/Builds/v2", vm.TargetFolder);
                Assert.True(vm.RunDiffCommand.CanExecute());

                await vm.RunDiffCommand.ExecuteAsync();
                Assert.True(vm.HasReport);
                Assert.Equal(5, vm.FilteredResults.Count);
                Assert.Contains("+1 Added", vm.SummaryBadgeText, StringComparison.Ordinal);
                Assert.Contains("->1 Moved", vm.SummaryBadgeText, StringComparison.Ordinal);

                // Filter by change kind
                vm.SelectedChangeFilter = "Added";
                Assert.Single(vm.FilteredResults);
                Assert.Equal("AddedShader", vm.FilteredResults[0].NameOrPath);

                vm.SelectedChangeFilter = "Moved";
                Assert.Single(vm.FilteredResults);
                Assert.Equal("MovedMesh", vm.FilteredResults[0].NameOrPath);

                vm.SelectedChangeFilter = "All";
                vm.HideUnchanged = true;
                Assert.Equal(4, vm.FilteredResults.Count);

                // Search text filter
                vm.SearchText = "ModMat";
                Assert.Single(vm.FilteredResults);
                vm.SearchText = string.Empty;

                // Switch view toggle Assets -> Files
                vm.ViewMode = BuildDiffViewMode.Files;
                Assert.True(vm.IsFilesView);
                Assert.Equal(2, vm.FilteredResults.Count);

                // Export commands via PickExportFileAsync
                var jsonOut = Path.Combine(tempRoot, "vm_diff.json");
                var csvOut = Path.Combine(tempRoot, "vm_diff.csv");
                vm.PickExportFileAsync = (_, ext) => Task.FromResult<string?>(ext == "json" ? jsonOut : csvOut);

                await vm.ExportJsonCommand.ExecuteAsync();
                await vm.ExportCsvCommand.ExecuteAsync();
                Assert.True(File.Exists(jsonOut));
                Assert.True(File.Exists(csvOut));

                var closed = false;
                vm.RequestClose += () => closed = true;
                vm.CloseCommand.Execute();
                Assert.True(closed);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task GlobalSearchDialogViewModel_SearchGraphModesAndNavigation_WorkEndToEnd()
        {
            var tempRoot = CreateTempDirectory("vm_global_search");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "ProjectData");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                var srcPath = Path.Combine(projectDir, "shared0.assets");
                File.WriteAllBytes(srcPath, BuildMinimalUnityFsHeader("vm_search"));

                using var cache = new SQLiteProjectIndexCache(cacheDir);
                var scanResult = ProjectScanner.ScanFolder(projectDir);
                var signature = cache.GetFolderSignature(scanResult);

                var texUid = "shared0.assets#h#11";
                var matUid = "shared0.assets#h#12";
                var handles = new List<AssetHandle>
                {
                    CreateHandleWithUid("shared0.assets", srcPath, texUid, 11, "Banner_Tex", ClassIDType.Texture2D, "assets/tex/banner.png", 2048),
                    CreateHandleWithUid("shared0.assets", srcPath, matUid, 12, "Banner_Mat", ClassIDType.Material, "assets/mat/banner.mat", 512)
                };
                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, markIndexingCompleted: true));

                var rels = new SemanticAssetRelations();
                rels.MaterialCaches.Add(new SemanticMaterialCacheEntry(matUid, "Banner_Mat", string.Empty, texUid, "_MainTex"));
                rels.MaterialTextures.Add(new SemanticMaterialTextureRelation(matUid, matUid, "_MainTex", 0, texUid, 0, 11, true, true));
                using (var s = cache.BeginSemanticStagingSession(projectDir, signature))
                {
                    s.FlushBatch(rels, clearBatch: false);
                    Assert.True(s.Commit());
                }

                var index = new ProjectIndex();
                foreach (var h in handles)
                {
                    index.AddHandle(h);
                }

                using var vm = new GlobalSearchDialogViewModel(index, assetsManager: null, sqliteCache: cache, folderPath: projectDir, signature: signature);
                Assert.True(vm.IsSearchMode);
                Assert.False(vm.GoToSelectedCommand.CanExecute());

                // 1) Execute global search filtered to Texture2D
                vm.QueryText = "Banner";
                vm.SelectedTypeFilterIndex = 1; // Texture2D
                await vm.ExecuteQueryCommand.ExecuteAsync();

                Assert.False(vm.HasError);
                var hit = Assert.Single(vm.Results);
                Assert.Equal(texUid, hit.AssetUniqueId);

                // 2) Navigate to selected fires NavigateToAssetRequested with exact AssetUniqueId
                string? navigatedUid = null;
                GlobalSearchResultItem? navigatedItem = null;
                vm.NavigateToAssetRequested += uid => navigatedUid = uid;
                vm.NavigateToResultRequested += item => navigatedItem = item;

                vm.SelectedResult = hit;
                Assert.True(vm.GoToSelectedCommand.CanExecute());
                vm.GoToSelectedCommand.Execute();
                Assert.Equal(texUid, navigatedUid);
                Assert.Same(hit, navigatedItem);

                // 3) InspectSelectedUsageCommand switches into SemanticUsageGraph mode & queries SQLite graph
                await vm.InspectSelectedUsageCommand.ExecuteAsync();
                Assert.True(vm.IsGraphMode);
                var usageRow = Assert.Single(vm.Results);
                Assert.Equal(matUid, usageRow.AssetUniqueId);

                // 4) Clear & Close commands
                vm.ClearCommand.Execute();
                Assert.Empty(vm.Results);

                var closed = false;
                vm.RequestClose += () => closed = true;
                vm.CloseCommand.Execute();
                Assert.True(closed);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Block3_PureMvvmAudit_ZeroAvaloniaImportsZeroClickHandlersAndCompiledBindings()
        {
            var repoRoot = LocateRepositoryRoot();
            var pureCsFiles = new[]
            {
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "ProjectAnalysis", "BuildDiffService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "ProjectAnalysis", "GlobalProjectQueryService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "SQLiteProjectIndexCache.AnalysisQueries.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels", "BuildDiffDialogViewModel.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels", "GlobalSearchDialogViewModel.cs")
            };

            foreach (var file in pureCsFiles)
            {
                Assert.True(File.Exists(file), $"Expected Block 3 file not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(AvaloniaUsingRegex.IsMatch(content), $"Pure service/ViewModel must not import Avalonia: {file}");
            }

            var axamlFiles = new[]
            {
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "BuildDiffDialog.axaml"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "GlobalSearchDialog.axaml")
            };

            foreach (var file in axamlFiles)
            {
                Assert.True(File.Exists(file), $"Expected Block 3 AXAML file not found: {file}");
                var content = File.ReadAllText(file);
                Assert.Contains("x:CompileBindings=\"True\"", content, StringComparison.Ordinal);
                Assert.Contains("x:DataType=", content, StringComparison.Ordinal);
                Assert.False(AxamlClickAttributeRegex.IsMatch(content), $"AXAML dialog must not contain Click= handlers: {file}");
            }

            // Verify reflection check on Block 3 ViewModel types
            foreach (var type in new[] { typeof(BuildDiffDialogViewModel), typeof(BuildDiffResultRow), typeof(GlobalSearchDialogViewModel), typeof(GlobalSearchResultItem) })
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNotAvaloniaType(prop.PropertyType, $"{type.Name}.{prop.Name}");
                }
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static AssetHandle CreateHandle(
            string serializedFileName,
            string originalPath,
            long pathId,
            string name,
            ClassIDType type,
            string container,
            long byteSize)
        {
            var origin = AssetHandleOrigin.Intern(serializedFileName, originalPath);
            return AssetHandle.CreateCanonical(origin, pathId, name, type, container, 0, byteSize);
        }

        private static AssetHandle CreateHandleWithUid(
            string serializedFileName,
            string originalPath,
            string uniqueId,
            long pathId,
            string name,
            ClassIDType type,
            string container,
            long byteSize)
        {
            var origin = AssetHandleOrigin.Intern(serializedFileName, originalPath);
            return AssetHandle.CreateWithOrigin(origin, uniqueId, pathId, name, type, container, 0, byteSize);
        }

        private static byte[] BuildMinimalUnityFsHeader(string tag)
        {
            // "UnityFS\0" magic so ProjectScanner detects ProjectFileKind.UnityBundle
            var prefix = Encoding.ASCII.GetBytes("UnityFS\0");
            var payload = Encoding.UTF8.GetBytes($"version=6|unity=2022.3.10f1|tag={tag}");
            var result = new byte[prefix.Length + payload.Length];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, result, prefix.Length, payload.Length);
            return result;
        }

        private static void WriteFileWithTimestamp(string path, byte[] bytes, DateTime utcTimestamp)
        {
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, utcTimestamp);
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static string CreateTempDirectory(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"AssetStudio_Block3_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
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

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Unable to locate repository root.");
        }

        private static void AssertNotAvaloniaType(Type type, string memberName)
        {
            var ns = type.Namespace ?? string.Empty;
            Assert.False(
                string.Equals(ns, "Avalonia", StringComparison.Ordinal) || ns.StartsWith("Avalonia.", StringComparison.Ordinal),
                $"ViewModel member '{memberName}' must not reference Avalonia type '{type.FullName}'.");
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    AssertNotAvaloniaType(arg, memberName);
                }
            }
        }
    }
}
