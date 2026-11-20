using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class HierarchyAndProjectSessionViewModelTests
    {
        private static GameObject NewGameObject() =>
            (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));

        private static GameObjectNode GoNode(string name) =>
            new GameObjectNode { Name = name, GameObject = NewGameObject() };

        private static GameObjectNode FileNode(string name) =>
            new GameObjectNode { Name = name };

        private static GameObjectNode AddChild(GameObjectNode parent, GameObjectNode child)
        {
            parent.AddChild(child);
            return child;
        }

        private static List<GameObjectNode> BuildHierarchy(
            int totalGameObjectNodes,
            int seed,
            string[] namePool,
            out List<GameObjectNode> allGoNodes)
        {
            var random = new Random(seed);
            var roots = new List<GameObjectNode>();
            allGoNodes = new List<GameObjectNode>(totalGameObjectNodes);
            var fileCount = Math.Max(2, totalGameObjectNodes / 3000);
            for (var f = 0; f < fileCount; f++)
            {
                roots.Add(FileNode($"scene_{f}.assets"));
            }

            for (var i = 0; i < totalGameObjectNodes; i++)
            {
                var baseName = namePool[random.Next(namePool.Length)];
                var name = (i % 250 == 0) ? $"TargetRig_{i / 250}" : $"{baseName}_{i % 97}";
                var node = GoNode(name);

                var pick = random.Next(20);
                GameObjectNode parent = allGoNodes.Count == 0 || pick == 0
                    ? roots[random.Next(roots.Count)]
                    : pick < 10
                        ? allGoNodes[random.Next(allGoNodes.Count)]
                        : allGoNodes[random.Next(Math.Max(0, allGoNodes.Count - 48), allGoNodes.Count)];

                parent.AddChild(node);
                allGoNodes.Add(node);
            }

            return roots;
        }

        [Fact]
        public async Task SceneHierarchyViewModel_RootPopulation_AsyncSearch15kNodes_AndBidirectionalCachedMatchCycling()
        {
            var pool = new[] { " Pelvis", "Spine", "LeftArm", "RightArm", "Camera", "Light", "PropMesh" };
            var roots = BuildHierarchy(15_000, seed: 42, pool, out _);

            var vm = new SceneHierarchyViewModel();
            var persistentNodesCollection = vm.Nodes;
            var resetEvents = 0;
            vm.Nodes.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    resetEvents++;
                }
            };

            vm.SetRootNodes(roots);

            Assert.Same(persistentNodesCollection, vm.Nodes);
            Assert.Equal(roots.Count, vm.TotalRootCount);
            Assert.Equal(roots.Count, vm.Nodes.Count);
            Assert.True(resetEvents >= 1);
            Assert.False(vm.HasMatches);
            Assert.Equal(0, vm.MatchCount);
            Assert.Equal(0, vm.CurrentMatchIndex);

            // Search across 15,000+ nodes for "TargetRig_" (inserted every 250 nodes => 60 matches)
            var firstMatch = await vm.SearchNextAsync("TargetRig_");
            Assert.NotNull(firstMatch);
            Assert.Equal("TargetRig_", vm.SearchQuery);
            Assert.False(vm.IsSearching);
            Assert.True(vm.HasMatches);
            Assert.Equal(60, vm.MatchCount);
            Assert.Equal(1, vm.CurrentMatchIndex);
            Assert.Same(firstMatch, vm.CurrentMatchNode);
            Assert.Same(firstMatch, vm.SelectedNode);
            Assert.Equal($"Scene hierarchy match 1/60: {firstMatch!.Name}", vm.SearchStatusText);

            // Ancestors of matched node must be expanded automatically
            var ancestor = firstMatch.Parent;
            Assert.NotNull(ancestor);
            while (ancestor != null)
            {
                Assert.True(ancestor.IsExpanded);
                ancestor = ancestor.Parent;
            }

            var traversalsAfterFirstSearch = vm.TraversalCount;
            Assert.Equal(1, traversalsAfterFirstSearch);

            // Cycle forward through all 60 matches and wrap around to match 1 with ZERO re-traversal
            var visitedForward = new List<GameObjectNode> { firstMatch };
            for (var i = 2; i <= vm.MatchCount; i++)
            {
                var next = await vm.SearchNextAsync("TargetRig_");
                Assert.NotNull(next);
                Assert.Equal(i, vm.CurrentMatchIndex);
                Assert.Same(next, vm.CurrentMatchNode);
                Assert.Same(next, vm.SelectedNode);
                Assert.Equal($"Scene hierarchy match {i}/60: {next!.Name}", vm.SearchStatusText);
                visitedForward.Add(next);
            }

            Assert.Equal(60, visitedForward.Distinct().Count());

            // Wrap-around on 61st SearchNextAsync
            var wrappedFirst = await vm.SearchNextAsync();
            Assert.Same(firstMatch, wrappedFirst);
            Assert.Equal(1, vm.CurrentMatchIndex);

            // Cycle backward from match 1 -> wraps to match 60, then 59, then forward back to 60
            var wrappedLast = await vm.SearchPreviousAsync();
            Assert.Same(visitedForward[^1], wrappedLast);
            Assert.Equal(60, vm.CurrentMatchIndex);
            Assert.Equal($"Scene hierarchy match 60/60: {wrappedLast!.Name}", vm.SearchStatusText);

            var fiftyNinth = await vm.SearchPreviousAsync();
            Assert.Same(visitedForward[^2], fiftyNinth);
            Assert.Equal(59, vm.CurrentMatchIndex);

            var backToSixtieth = await vm.SearchNextAsync();
            Assert.Same(visitedForward[^1], backToSixtieth);
            Assert.Equal(60, vm.CurrentMatchIndex);

            // Zero extra hierarchy traversals occurred during all forward/backward cycling
            Assert.Equal(traversalsAfterFirstSearch, vm.TraversalCount);

            // Query with zero matches clears active selection match index and updates status
            var noMatch = await vm.SearchNextAsync("__no_such_scene_node_xyz__");
            Assert.Null(noMatch);
            Assert.False(vm.HasMatches);
            Assert.Equal(0, vm.MatchCount);
            Assert.Equal(0, vm.CurrentMatchIndex);
            Assert.Null(vm.CurrentMatchNode);
            Assert.Equal("No scene hierarchy match for '__no_such_scene_node_xyz__'.", vm.SearchStatusText);
        }

        [Fact]
        public async Task SceneHierarchyViewModel_RapidQueryChangeCancellation_CancelsStaleAndAppliesLatest()
        {
            var pool = new[] { "AlphaNode", "BetaNode", "GammaNode", "DeltaNode" };
            var roots = BuildHierarchy(120_000, seed: 99, pool, out _);
            var vm = new SceneHierarchyViewModel();
            vm.SetRootNodes(roots);

            // Fire rapid query changes in parallel; earlier queries must be superseded
            var task1 = vm.SearchNextAsync(" Alpha");
            var task2 = vm.SearchNextAsync("Beta");
            var task3 = vm.SearchNextAsync("GammaNode");

            var res1 = await task1;
            var res2 = await task2;
            var res3 = await task3;

            Assert.Null(res1);
            Assert.Null(res2);
            Assert.NotNull(res3);
            Assert.False(vm.IsSearching);
            Assert.Equal("GammaNode", vm.SearchQuery);
            Assert.True(vm.HasMatches);
            Assert.True(vm.MatchCount > 0);
            Assert.Equal(1, vm.CurrentMatchIndex);
            Assert.Same(res3, vm.CurrentMatchNode);
            Assert.Contains("GammaNode", res3!.Name, StringComparison.OrdinalIgnoreCase);

            // Editing SearchQuery property to a new string invalidates cached matches immediately
            vm.SearchQuery = "DeltaNode";
            Assert.False(vm.HasMatches);
            Assert.Equal(0, vm.MatchCount);
            Assert.Equal(0, vm.CurrentMatchIndex);
            Assert.Null(vm.CurrentMatchNode);
        }

        [Fact]
        public async Task SceneHierarchyViewModel_PropertyChangedNotifications_AssetNavigation_ClearSearch_AndResetDeduplication()
        {
            var rootFile = FileNode("level0");
            var parentGo = AddChild(rootFile, GoNode("CharacterRoot"));
            var childGo = AddChild(parentGo, GoNode("WeaponSocket"));
            var roots = new List<GameObjectNode> { rootFile };

            var vm = new SceneHierarchyViewModel();
            var changedProps = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    changedProps.Add(e.PropertyName);
                }
            };

            vm.SetRootNodes(roots);
            Assert.Contains(nameof(SceneHierarchyViewModel.TotalRootCount), changedProps);

            changedProps.Clear();
            var match = await vm.SearchNextAsync("WeaponSocket");
            Assert.Same(childGo, match);
            Assert.Contains(nameof(SceneHierarchyViewModel.SearchQuery), changedProps);
            Assert.Contains(nameof(SceneHierarchyViewModel.MatchCount), changedProps);
            Assert.Contains(nameof(SceneHierarchyViewModel.HasMatches), changedProps);
            Assert.Contains(nameof(SceneHierarchyViewModel.CurrentMatchIndex), changedProps);
            Assert.Contains(nameof(SceneHierarchyViewModel.CurrentMatchNode), changedProps);
            Assert.Contains(nameof(SceneHierarchyViewModel.SelectedNode), changedProps);
            Assert.Contains(nameof(SceneHierarchyViewModel.SearchStatusText), changedProps);

            // Asset navigation (GoToSceneHierarchy equivalent)
            parentGo.IsExpanded = false;
            var dummyObject = NewGameObject();
            var assetWithNode = new AssetItem(dummyObject) { TreeNode = childGo };
            var assetWithoutNode = new AssetItem(dummyObject) { TreeNode = null };

            Assert.False(vm.TryNavigateToAssetNode(assetWithoutNode));
            Assert.Equal("Selected asset has no scene hierarchy node.", vm.SearchStatusText);

            Assert.True(vm.TryNavigateToAssetNode(assetWithNode));
            Assert.Same(childGo, vm.SelectedNode);
            Assert.True(parentGo.IsExpanded);

            // ClearSearch resets query & match state but preserves root nodes
            vm.ClearSearch();
            Assert.Equal(string.Empty, vm.SearchQuery);
            Assert.Equal(0, vm.MatchCount);
            Assert.Equal(0, vm.CurrentMatchIndex);
            Assert.False(vm.HasMatches);
            Assert.Null(vm.CurrentMatchNode);
            Assert.Equal(string.Empty, vm.SearchStatusText);
            Assert.Equal(1, vm.TotalRootCount);
            Assert.Single(vm.Nodes);

            // Reset clears everything including roots and selection
            vm.Reset();
            Assert.Equal(0, vm.TotalRootCount);
            Assert.Empty(vm.Nodes);
            Assert.Empty(vm.RootNodes);
            Assert.Null(vm.SelectedNode);
            Assert.Null(vm.CurrentMatchNode);
            Assert.Equal(string.Empty, vm.SearchQuery);
            Assert.Equal(string.Empty, vm.SearchStatusText);

            // Calling Reset() again when already in default state emits ZERO PropertyChanged events
            changedProps.Clear();
            vm.Reset();
            Assert.Empty(changedProps);
        }

        [Fact]
        public void ProjectSessionViewModel_LoadSwitchResetLifecycle_AndWindowTitleFormattingWithAppVersionInfo()
        {
            var vm = new ProjectSessionViewModel();
            var expectedVersion = AppVersionInfo.PrefixedDisplayVersion;

            // 1) Initial idle state
            Assert.False(vm.HasLoadedProject);
            Assert.Equal(string.Empty, vm.ProjectFolderPath);
            Assert.Equal(string.Empty, vm.ProjectDisplayName);
            Assert.False(vm.IsLazyMode);
            Assert.Equal(string.Empty, vm.UnityVersion);
            Assert.Equal(UnityScriptingBackend.Unknown, vm.ScriptingBackend);
            Assert.False(vm.HasRuntimeBanner);
            Assert.Equal(string.Empty, vm.RuntimeBannerText);
            Assert.Equal($"AssetStudio {expectedVersion}", vm.WindowTitle);

            // 2) Load first project in Eager mode
            vm.SetLoadedProject(
                projectFolderPath: @"C:\Games\StarRail\StarRail_Data",
                displayName: "Honkai: Star Rail",
                isLazyMode: false,
                unityVersion: "2019.4.40f1",
                scriptingBackend: UnityScriptingBackend.Il2Cpp,
                cacheStatusSummary: "Eager load (no SQLite index)");

            Assert.True(vm.HasLoadedProject);
            Assert.Equal(@"C:\Games\StarRail\StarRail_Data", vm.ProjectFolderPath);
            Assert.Equal("Honkai: Star Rail", vm.ProjectDisplayName);
            Assert.False(vm.IsLazyMode);
            Assert.Equal("2019.4.40f1", vm.UnityVersion);
            Assert.Equal(UnityScriptingBackend.Il2Cpp, vm.ScriptingBackend);
            Assert.Equal("Eager load (no SQLite index)", vm.CacheStatusSummary);
            Assert.Equal($"AssetStudio {expectedVersion} - Honkai: Star Rail", vm.WindowTitle);

            // Update window title after SerializedFile header load (productName + unityVersion + platform)
            var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            file.unityVersion = "2019.4.40f1";
            file.m_TargetPlatform = BuildTarget.StandaloneWindows64;

            var loadedTitle = vm.UpdateWindowTitle("StarRail", file);
            Assert.Equal($"AssetStudio {expectedVersion} - StarRail - 2019.4.40f1 - StandaloneWindows64", loadedTitle);
            Assert.Equal(loadedTitle, vm.WindowTitle);

            // Null/empty productName falls back to "no productName" matching legacy MainWindow.WindowSettings.cs
            var fallbackTitle = vm.UpdateWindowTitle(null, file);
            Assert.Equal($"AssetStudio {expectedVersion} - no productName - 2019.4.40f1 - StandaloneWindows64", fallbackTitle);

            // 3) Switch to a second project in Lazy mode (auto-deriving displayName from folder path)
            vm.SetLoadedProject(
                projectFolderPath: @"D:\UnityProjects\ZenlessZoneZero",
                displayName: null,
                isLazyMode: true,
                unityVersion: "2022.3.20f1",
                scriptingBackend: UnityScriptingBackend.Il2Cpp,
                cacheStatusSummary: "Loaded from mmap sidecar (.idx)");

            Assert.True(vm.HasLoadedProject);
            Assert.Equal(@"D:\UnityProjects\ZenlessZoneZero", vm.ProjectFolderPath);
            Assert.Equal("ZenlessZoneZero", vm.ProjectDisplayName);
            Assert.True(vm.IsLazyMode);
            Assert.Equal("2022.3.20f1", vm.UnityVersion);
            Assert.Equal("Loaded from mmap sidecar (.idx)", vm.CacheStatusSummary);

            // Verify POSIX & trailing-slash folder display name extraction works identically across Windows and Linux CI
            vm.SetLoadedProject(
                projectFolderPath: "/opt/games/WutheringWaves/Client/",
                displayName: null,
                isLazyMode: true);
            Assert.Equal("Client", vm.ProjectDisplayName);
            Assert.Equal($"AssetStudio {expectedVersion} - Client", vm.WindowTitle);

            // 4) ResetSession returns everything to clean idle defaults
            vm.ResetSession();
            Assert.False(vm.HasLoadedProject);
            Assert.Equal(string.Empty, vm.ProjectFolderPath);
            Assert.Equal(string.Empty, vm.ProjectDisplayName);
            Assert.False(vm.IsLazyMode);
            Assert.Equal(string.Empty, vm.UnityVersion);
            Assert.Equal(UnityScriptingBackend.Unknown, vm.ScriptingBackend);
            Assert.Equal(string.Empty, vm.CacheStatusSummary);
            Assert.False(vm.HasRuntimeBanner);
            Assert.Equal(string.Empty, vm.RuntimeBannerText);
            Assert.Null(vm.SelectedProjectRecord);
            Assert.Equal($"AssetStudio {expectedVersion}", vm.WindowTitle);
        }

        [Fact]
        public void ProjectSessionViewModel_ApplyProjectAnalysis_FormatsRuntimeBannersForIl2CppMonoAddressablesAndObfuscationHints()
        {
            var vm = new ProjectSessionViewModel();

            // 1) Pending & Running reports
            vm.ApplyProjectAnalysis(new ProjectAnalysisReport { Status = ProjectAnalysisStatus.Pending });
            Assert.Equal(ProjectAnalysisStatus.Pending, vm.AnalysisStatus);
            Assert.True(vm.HasRuntimeBanner);
            Assert.Equal("Analysis: Pending", vm.RuntimeBannerText);

            vm.ApplyProjectAnalysis(new ProjectAnalysisReport { Status = ProjectAnalysisStatus.Running });
            Assert.Equal(ProjectAnalysisStatus.Running, vm.AnalysisStatus);
            Assert.Equal("Analysis: Running...", vm.RuntimeBannerText);

            // 2) Failed report
            vm.ApplyProjectAnalysis(new ProjectAnalysisReport
            {
                Status = ProjectAnalysisStatus.Failed,
                ErrorMessage = "global-metadata.dat truncated"
            });
            Assert.Equal(ProjectAnalysisStatus.Failed, vm.AnalysisStatus);
            Assert.Equal("Analysis: Failed (global-metadata.dat truncated)", vm.RuntimeBannerText);

            // 3) Ready IL2CPP + Addressables + decryption hints
            var il2cppResult = new ProjectAnalysisResult
            {
                FromCache = true,
                Report = new ProjectAnalysisReport
                {
                    ProjectRoot = @"C:\Games\Il2CppTitle",
                    Status = ProjectAnalysisStatus.Ready,
                    UnityVersion = "2022.3.39f1",
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                    Il2CppMetadataVersion = 31,
                    HasAddressables = true,
                    Il2CppScriptCount = 1420,
                    AddressablesBundleCount = 850,
                    DecryptionHintCount = 2
                }
            };
            vm.ApplyProjectAnalysis(il2cppResult);

            Assert.Equal(ProjectAnalysisStatus.Ready, vm.AnalysisStatus);
            Assert.True(vm.HasLoadedProject);
            Assert.Equal(@"C:\Games\Il2CppTitle", vm.ProjectFolderPath);
            Assert.Equal("Il2CppTitle", vm.ProjectDisplayName);
            Assert.Equal("2022.3.39f1", vm.UnityVersion);
            Assert.Equal(UnityScriptingBackend.Il2Cpp, vm.ScriptingBackend);
            Assert.Equal(31, vm.Il2CppMetadataVersion);
            Assert.False(vm.Il2CppMetadataObfuscated);
            Assert.True(vm.HasAddressables);
            Assert.Equal(1420, vm.Il2CppScriptCount);
            Assert.Equal(850, vm.AddressablesBundleCount);
            Assert.Equal(2, vm.DecryptionHintCount);
            Assert.Equal("Analysis loaded from cache", vm.CacheStatusSummary);
            Assert.Contains("Analysis: Ready", vm.RuntimeBannerText);
            Assert.Contains("IL2CPP v31", vm.RuntimeBannerText);
            Assert.Contains("Unity 2022.3.39f1", vm.RuntimeBannerText);
            Assert.Contains("Addressables", vm.RuntimeBannerText);
            Assert.Contains("1,420 scripts", vm.RuntimeBannerText);
            Assert.Contains("850 bundles", vm.RuntimeBannerText);
            Assert.Contains("2 decryption hints", vm.RuntimeBannerText);

            // 4) Ready IL2CPP with obfuscated metadata + singular decryption hint
            var obfuscatedReport = new ProjectAnalysisReport
            {
                ProjectRoot = @"C:\Games\ExiliumGame",
                Status = ProjectAnalysisStatus.Ready,
                UnityVersion = "2021.3.18f1",
                ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                Il2CppMetadataVersion = null,
                Il2CppMetadataObfuscated = true,
                DecryptionHintCount = 1
            };
            vm.ApplyProjectAnalysis(obfuscatedReport);

            Assert.True(vm.Il2CppMetadataObfuscated);
            Assert.Null(vm.Il2CppMetadataVersion);
            Assert.Contains("IL2CPP (obfuscated metadata)", vm.RuntimeBannerText);
            Assert.Contains("1 decryption hint", vm.RuntimeBannerText);
            Assert.DoesNotContain("1 decryption hints", vm.RuntimeBannerText);

            // 5) Ready Mono backend with Managed DLL count
            var monoReport = new ProjectAnalysisReport
            {
                ProjectRoot = @"C:\Games\MonoIndie",
                Status = ProjectAnalysisStatus.Ready,
                UnityVersion = "2020.3.48f1",
                ScriptingBackend = UnityScriptingBackend.Mono,
                HasManagedAssemblies = true,
                ManagedAssemblyCount = 24
            };
            vm.ApplyProjectAnalysis(monoReport);

            Assert.Equal(UnityScriptingBackend.Mono, vm.ScriptingBackend);
            Assert.Equal("2020.3.48f1", vm.UnityVersion);
            Assert.Contains("Mono (24 DLLs)", vm.RuntimeBannerText);
            Assert.Contains("Unity 2020.3.48f1", vm.RuntimeBannerText);

            // 6) Clearing analysis resets banner and flags
            vm.ApplyProjectAnalysis((ProjectAnalysisResult?)null);
            Assert.False(vm.HasRuntimeBanner);
            Assert.Equal(string.Empty, vm.RuntimeBannerText);
            Assert.Null(vm.CurrentAnalysisResult);
            Assert.Null(vm.CurrentAnalysisReport);
        }

        [Fact]
        public void ProjectSessionViewModel_SavedProjectsRefreshSelectionWithProjectManagerStore_AndPropertyChangedDeduplication()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudioTests_ProjSessionVm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var storeDir = Path.Combine(tempRoot, "Store");
                var gameRootA = Path.Combine(tempRoot, "GameAlpha");
                var gameRootB = Path.Combine(tempRoot, "GameBeta");
                Directory.CreateDirectory(gameRootA);
                Directory.CreateDirectory(gameRootB);

                var store = new ProjectManagerStore(storeDir);

                var projectA = new ManagedProject
                {
                    Name = "Alpha Adventure",
                    UseAutoName = false,
                    ProjectRoot = gameRootA,
                    LastAccessedAtUtc = DateTime.UtcNow.AddHours(-2)
                };
                var projectB = new ManagedProject
                {
                    Name = "Beta Arena",
                    UseAutoName = false,
                    ProjectRoot = gameRootB,
                    LastAccessedAtUtc = DateTime.UtcNow
                };

                store.SaveProject(projectA);
                store.SaveProject(projectB);
                store.SaveProjectAnalysisReport(projectB.Id, new ProjectAnalysisReport
                {
                    ProjectRoot = gameRootB,
                    ProjectHash = ProjectRuntimeDetector.ComputeProjectHash(gameRootB),
                    Status = ProjectAnalysisStatus.Ready,
                    UnityVersion = "6000.0.12f1",
                    ScriptingBackend = UnityScriptingBackend.Il2Cpp,
                    Il2CppMetadataVersion = 39,
                    HasAddressables = true
                });

                var vm = new ProjectSessionViewModel();
                var persistentSavedProjects = vm.SavedProjects;

                vm.RefreshSavedProjects(store, selectProjectId: projectA.Id);
                Assert.Same(persistentSavedProjects, vm.SavedProjects);
                Assert.Equal(2, vm.SavedProjects.Count);
                Assert.NotNull(vm.SelectedProjectRecord);
                Assert.Equal(projectA.Id, vm.SelectedProjectRecord!.Id);

                // Select projectB and apply to session
                var loadedB = vm.SavedProjects.First(p => p.Id == projectB.Id);
                vm.SelectProjectRecord(loadedB, applyToSession: true);
                Assert.Equal(projectB.Id, vm.SelectedProjectRecord!.Id);
                Assert.True(vm.HasLoadedProject);
                Assert.Equal(gameRootB, vm.ProjectFolderPath);
                Assert.Equal("Beta Arena", vm.ProjectDisplayName);
                Assert.Equal("6000.0.12f1", vm.UnityVersion);
                Assert.Equal(UnityScriptingBackend.Il2Cpp, vm.ScriptingBackend);
                Assert.Equal(39, vm.Il2CppMetadataVersion);
                Assert.True(vm.HasAddressables);
                Assert.Contains("Beta Arena", vm.WindowTitle);

                // PropertyChanged deduplication check: setting same values emits 0 events
                var changeCount = 0;
                vm.PropertyChanged += (_, _) => changeCount++;

                vm.CacheStatusSummary = "Warm SQLite cache";
                Assert.Equal(1, changeCount);

                changeCount = 0;
                vm.CacheStatusSummary = "Warm SQLite cache";
                vm.IsLazyMode = vm.IsLazyMode;
                vm.SelectedProjectRecord = loadedB;
                Assert.Equal(0, changeCount);

                // ResetSession twice: second call emits 0 PropertyChanged events
                vm.ResetSession(keepSavedProjects: false);
                Assert.Empty(vm.SavedProjects);
                Assert.False(vm.HasLoadedProject);

                changeCount = 0;
                vm.ResetSession(keepSavedProjects: false);
                Assert.Equal(0, changeCount);
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
    }
}
