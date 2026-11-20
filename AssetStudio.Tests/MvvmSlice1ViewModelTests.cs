using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class MvvmSlice1ViewModelTests
    {
        [Fact]
        public async Task AssetListViewModel_EagerAndArenaModes_SearchClassFilterSortAndClassCounts_HaveFullParity()
        {
            const int count = 600;
            var (projectIndex, eagerItems) = BuildTestDataset(count);

            var eagerVm = new AssetListViewModel();
            var arenaVm = new AssetListViewModel();

            eagerVm.SetEagerAssets(eagerItems);
            arenaVm.SetArenaAssets(projectIndex);

            Assert.False(eagerVm.IsArenaMode);
            Assert.True(arenaVm.IsArenaMode);
            Assert.Equal(count, eagerVm.TotalAssetCount);
            Assert.Equal(count, arenaVm.TotalAssetCount);
            Assert.Equal(count, eagerVm.VisibleAssetCount);
            Assert.Equal(count, arenaVm.VisibleAssetCount);
            Assert.Equal(count, eagerVm.VisibleItems.Count);
            Assert.Equal(count, arenaVm.VisibleItems.Count);

            // 1) Verify AssetClasses counts match exact Type distribution
            var expectedCountsByClassId = eagerItems
                .GroupBy(x => (int)x.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            var eagerCountsByClassId = eagerVm.AssetClasses
                .GroupBy(c => c.ClassID)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ObjectCount));
            var arenaCountsByClassId = arenaVm.AssetClasses
                .GroupBy(c => c.ClassID)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ObjectCount));

            Assert.Equal(expectedCountsByClassId.Count, eagerCountsByClassId.Count);
            Assert.Equal(expectedCountsByClassId.Count, arenaCountsByClassId.Count);
            foreach (var (classId, expectedCount) in expectedCountsByClassId)
            {
                Assert.Equal(expectedCount, eagerCountsByClassId[classId]);
                Assert.Equal(expectedCount, arenaCountsByClassId[classId]);
            }

            // 2) Verify single-term and multi-term case-insensitive text search in Eager & Arena modes
            var searchQueries = new[]
            {
                "hero",
                "HERO [LOD0]",
                "Hero café",
                "hero Mesh",
                "Enemy_Mesh",
                "FBX sub-asset",
                "albedo Texture2D",
                "__non_existent_query_xyz__",
                string.Empty,
            };

            foreach (var query in searchQueries)
            {
                Assert.True(await eagerVm.FilterAsync(query));
                Assert.True(await arenaVm.FilterAsync(query));

                Assert.Equal(eagerVm.VisibleAssetCount, arenaVm.VisibleAssetCount);
                AssertViewportParity(eagerVm, arenaVm);
            }

            //Verify multi-term query specifically narrowed results compared to single term
            Assert.True(await eagerVm.FilterAsync("Hero"));
            var heroOnlyCount = eagerVm.VisibleAssetCount;
            Assert.True(heroOnlyCount > 0);

            Assert.True(await eagerVm.FilterAsync("Hero Mesh"));
            Assert.True(await arenaVm.FilterAsync("Hero Mesh"));
            Assert.True(eagerVm.VisibleAssetCount > 0);
            Assert.True(eagerVm.VisibleAssetCount < heroOnlyCount);
            Assert.All(eagerVm.VisibleItems, item =>
            {
                Assert.True(
                    item.Name.Contains("Hero", StringComparison.OrdinalIgnoreCase)
                    || item.Container.Contains("Hero", StringComparison.OrdinalIgnoreCase));
                Assert.True(
                    item.Name.Contains("Mesh", StringComparison.OrdinalIgnoreCase)
                    || item.TypeString.Contains("Mesh", StringComparison.OrdinalIgnoreCase)
                    || item.DisplayType.Contains("Mesh", StringComparison.OrdinalIgnoreCase));
            });
            AssertViewportParity(eagerVm, arenaVm);

            // Reset search text
            Assert.True(await eagerVm.FilterAsync(string.Empty));
            Assert.True(await arenaVm.FilterAsync(string.Empty));

            // 3) Class filtering: single class, multi-class, empty class set, and "All" toggle
            eagerVm.SetSelectedClassTypes(new[] { ClassIDType.Texture2D });
            arenaVm.SetSelectedClassTypes(new[] { ClassIDType.Texture2D });
            Assert.False(eagerVm.IsAllTypesSelected);
            Assert.False(arenaVm.IsAllTypesSelected);
            Assert.True(await eagerVm.FilterAsync());
            Assert.True(await arenaVm.FilterAsync());
            Assert.Equal(expectedCountsByClassId[(int)ClassIDType.Texture2D], eagerVm.VisibleAssetCount);
            Assert.Equal(eagerVm.VisibleAssetCount, arenaVm.VisibleAssetCount);
            Assert.All(eagerVm.VisibleItems, x => Assert.Equal(ClassIDType.Texture2D, x.Type));
            AssertViewportParity(eagerVm, arenaVm);

            // Multi-class filter (Mesh + Material)
            eagerVm.ToggleClassType(ClassIDType.Mesh);
            arenaVm.ToggleClassType(ClassIDType.Mesh);
            Assert.True(await eagerVm.FilterAsync());
            Assert.True(await arenaVm.FilterAsync());
            var expectedTwoTypes = expectedCountsByClassId[(int)ClassIDType.Texture2D] + expectedCountsByClassId[(int)ClassIDType.Mesh];
            Assert.Equal(expectedTwoTypes, eagerVm.VisibleAssetCount);
            AssertViewportParity(eagerVm, arenaVm);

            // Unchecking all individual types via SetClassTypeSelected automatically restores IsAllTypesSelected = true
            eagerVm.SetClassTypeSelected(ClassIDType.Texture2D, false);
            eagerVm.SetClassTypeSelected(ClassIDType.Mesh, false);
            arenaVm.SetClassTypeSelected(ClassIDType.Texture2D, false);
            arenaVm.SetClassTypeSelected(ClassIDType.Mesh, false);
            Assert.True(eagerVm.IsAllTypesSelected);
            Assert.True(arenaVm.IsAllTypesSelected);
            Assert.True(await eagerVm.FilterAsync());
            Assert.True(await arenaVm.FilterAsync());
            Assert.Equal(count, eagerVm.VisibleAssetCount);
            Assert.Equal(count, arenaVm.VisibleAssetCount);

            // Explicit SelectAllClassTypes after multi-class selection
            eagerVm.SetSelectedClassTypes(new[] { ClassIDType.AudioClip, ClassIDType.Material });
            arenaVm.SetSelectedClassTypes(new[] { ClassIDType.AudioClip, ClassIDType.Material });
            eagerVm.SelectAllClassTypes();
            arenaVm.SelectAllClassTypes();
            Assert.True(eagerVm.IsAllTypesSelected);
            Assert.Empty(eagerVm.SelectedClassTypes);

            // ClassFilterOverride (specific ClassID + UnityVersion)
            var overrideClass = new AssetClassItem
            {
                ClassID = (int)ClassIDType.Mesh,
                Name = "Mesh",
                UnityVersion = "2022.3.10f1",
            };
            eagerVm.SetClassFilterOverride(overrideClass);
            arenaVm.SetClassFilterOverride(overrideClass);
            Assert.True(eagerVm.HasClassFilterOverride);
            Assert.Contains("Mesh v2022.3.10f1", eagerVm.ClassFilterButtonText, StringComparison.Ordinal);
            Assert.True(await eagerVm.FilterAsync());
            Assert.True(await arenaVm.FilterAsync());
            Assert.True(eagerVm.VisibleAssetCount > 0);
            AssertViewportParity(eagerVm, arenaVm);

            eagerVm.ClearClassFilterOverride();
            arenaVm.ClearClassFilterOverride();
            Assert.False(eagerVm.HasClassFilterOverride);
            Assert.True(await eagerVm.FilterAsync());
            Assert.True(await arenaVm.FilterAsync());
            Assert.Equal(count, eagerVm.VisibleAssetCount);

            // 4) Column sorting across all columns in both ascending and descending directions
            var columns = new[] { "Name", "Container", "DisplayType", "Type", "PathID", "FullSize", "Size" };
            foreach (var col in columns)
            {
                // First click on new column -> Ascending
                Assert.True(await eagerVm.SortByColumnAsync(col));
                Assert.True(await arenaVm.SortByColumnAsync(col));
                Assert.Equal(col, eagerVm.SortColumn);
                Assert.True(eagerVm.SortAscending);
                Assert.False(eagerVm.SortDescending);
                AssertViewportParity(eagerVm, arenaVm);

                // Second click on same column -> Descending
                Assert.True(await eagerVm.SortByColumnAsync(col));
                Assert.True(await arenaVm.SortByColumnAsync(col));
                Assert.Equal(col, eagerVm.SortColumn);
                Assert.False(eagerVm.SortAscending);
                Assert.True(eagerVm.SortDescending);
                AssertViewportParity(eagerVm, arenaVm);
            }
        }

        [Fact]
        public void AssetListViewModel_VirtualAssetListViewport_PreservesSingleInstanceAndBoundsArenaLru()
        {
            const int count = 2500;
            var (projectIndex, eagerItems) = BuildTestDataset(count);

            var vm = new AssetListViewModel();
            var initialVisibleItemsInstance = vm.VisibleItems;
            var initialAssetClassesInstance = vm.AssetClasses;

            var collectionResetCount = 0;
            vm.VisibleItems.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    collectionResetCount++;
                }
            };

            // Eager mode uses ReplaceBacking on the same persistent VirtualAssetList instance
            vm.SetEagerAssets(eagerItems);
            Assert.Same(initialVisibleItemsInstance, vm.VisibleItems);
            Assert.Same(initialAssetClassesInstance, vm.AssetClasses);
            Assert.True(collectionResetCount >= 1);
            Assert.Equal(0, vm.VisibleItems.CachedItemCount);
            Assert.Same(eagerItems[42], vm.VisibleItems[42]);

            // Switch to Arena mode -> same persistent VirtualAssetList instance, now in 512-LRU provider mode
            vm.SetArenaAssets(projectIndex);
            Assert.Same(initialVisibleItemsInstance, vm.VisibleItems);
            Assert.Equal(count, vm.VisibleItems.Count);
            Assert.Equal(0, vm.VisibleItems.CachedItemCount);

            // Scroll through 1,200 rows; LRU cache must never exceed ArenaViewportLruCapacity (512)
            AssetItem? firstRow = null;
            for (var i = 0; i < 1200; i++)
            {
                var rowItem = vm.VisibleItems[i];
                if (i == 0)
                {
                    firstRow = rowItem;
                }
                Assert.Equal(eagerItems[i].Name, rowItem.Name);
                Assert.Equal(eagerItems[i].PathID, rowItem.PathID);
                Assert.True(vm.VisibleItems.CachedItemCount <= AssetListViewModel.ArenaViewportLruCapacity);
            }

            Assert.Equal(AssetListViewModel.ArenaViewportLruCapacity, vm.VisibleItems.CachedItemCount);
            Assert.NotNull(firstRow);
            // O(1) indexLocator resolves evicted row 0 without linear scan
            Assert.Equal(0, vm.VisibleItems.IndexOf(firstRow!));
        }

        [Fact]
        public async Task AssetListViewModel_SelectionPreservation_RestoresAssetSelectionKeysAcrossFilterAndSortInEagerAndArenaModes()
        {
            const int count = 300;
            var (projectIndex, eagerItems) = BuildTestDataset(count);

            foreach (var isArena in new[] { false, true })
            {
                var vm = new AssetListViewModel();
                if (isArena)
                {
                    vm.SetArenaAssets(projectIndex);
                }
                else
                {
                    vm.SetEagerAssets(eagerItems);
                }

                // Pick two Hero items and one Enemy item
                var heroItem1 = vm.VisibleItems.First(x => x.Name.StartsWith("Hero", StringComparison.Ordinal));
                var heroItem2 = vm.VisibleItems.Skip(20).First(x => x.Name.StartsWith("Hero", StringComparison.Ordinal));
                var enemyItem = vm.VisibleItems.First(x => x.Name.StartsWith("Enemy", StringComparison.Ordinal));

                Assert.True(AssetListViewModel.TryGetAssetSelectionKey(heroItem1, out var heroKey1));
                Assert.True(AssetListViewModel.TryGetAssetSelectionKey(heroItem2, out var heroKey2));
                Assert.True(AssetListViewModel.TryGetAssetSelectionKey(enemyItem, out var enemyKey));

                vm.SetSelection(new[] { heroItem1, heroItem2, enemyItem }, primaryItem: heroItem2);
                Assert.Equal(3, vm.SelectedAssetCount);
                Assert.Equal(heroKey2, vm.PrimarySelectedKey);

                // Sort by Name descending -> in Arena mode VirtualAssetList clears its LRU cache and re-materializes rows;
                // selection must be restored to the 3 surviving items by AssetSelectionKey.
                Assert.True(await vm.SortByColumnAsync("Name", ascending: false));
                Assert.Equal(3, vm.SelectedAssetCount);
                Assert.Contains(heroKey1, vm.SelectedKeys);
                Assert.Contains(heroKey2, vm.SelectedKeys);
                Assert.Contains(enemyKey, vm.SelectedKeys);
                Assert.Equal(heroKey2, vm.PrimarySelectedKey);
                Assert.NotNull(vm.PrimarySelectedItem);
                Assert.Equal(heroItem2.Name, vm.PrimarySelectedItem!.Name);

                // Filter to "Hero" -> enemyItem is filtered out, leaving the 2 Hero items selected
                Assert.True(await vm.FilterAsync("Hero"));
                Assert.Equal(2, vm.SelectedAssetCount);
                Assert.Contains(heroKey1, vm.SelectedKeys);
                Assert.Contains(heroKey2, vm.SelectedKeys);
                Assert.DoesNotContain(enemyKey, vm.SelectedKeys);
                Assert.Equal(heroKey2, vm.PrimarySelectedKey);
            }
        }

        [Fact]
        public async Task AssetListViewModel_ProgressiveBatchInsertionWhileSorted_MaintainsExactSortedOrderInEagerAndArenaModes()
        {
            const int totalCount = 600;
            const int batchSize = 75;
            var (projectIndex, eagerItems) = BuildTestDataset(totalCount);

            // Shuffle order deterministically to test out-of-order batch merging
            var rng = new Random(1337);
            var shuffledIndices = Enumerable.Range(0, totalCount).ToArray();
            for (var i = shuffledIndices.Length - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffledIndices[i], shuffledIndices[j]) = (shuffledIndices[j], shuffledIndices[i]);
            }

            var eagerVm = new AssetListViewModel();
            var arenaVm = new AssetListViewModel();
            arenaVm.BeginArenaMode(projectIndex);

            // Set active sort on PathID descending and active search filter "a" before batches arrive
            Assert.True(await eagerVm.SortByColumnAsync("PathID", ascending: false));
            Assert.True(await arenaVm.SortByColumnAsync("PathID", ascending: false));
            eagerVm.SearchText = "a";
            arenaVm.SearchText = "a";

            for (var offset = 0; offset < totalCount; offset += batchSize)
            {
                var sliceIndices = shuffledIndices.Skip(offset).Take(batchSize).ToArray();
                var sliceItems = sliceIndices.Select(idx => eagerItems[idx]).ToList();

                eagerVm.AppendFilteredEagerAssetsToVisible(sliceItems, addToExportable: true);
                arenaVm.AppendFilteredArenaIndicesToVisible(sliceIndices, addToExportable: true);

                // Verify both VMs stay in strict descending PathID order after EVERY batch
                Assert.Equal(eagerVm.TotalAssetCount, arenaVm.TotalAssetCount);
                Assert.Equal(eagerVm.VisibleAssetCount, arenaVm.VisibleAssetCount);
                for (var i = 1; i < eagerVm.VisibleItems.Count; i++)
                {
                    var prev = eagerVm.VisibleItems[i - 1];
                    var curr = eagerVm.VisibleItems[i];
                    Assert.True(prev.PathID >= curr.PathID, $"Out-of-order PathID at index {i}: {prev.PathID} < {curr.PathID}");
                }
                AssertViewportParity(eagerVm, arenaVm);
            }

            // Final progressive state must match a full one-shot filter+sort
            var referenceVm = new AssetListViewModel();
            referenceVm.SetEagerAssets(eagerItems);
            Assert.True(await referenceVm.SortByColumnAsync("PathID", ascending: false));
            Assert.True(await referenceVm.FilterAsync("a"));

            Assert.Equal(referenceVm.VisibleAssetCount, eagerVm.VisibleAssetCount);
            AssertViewportParity(referenceVm, eagerVm);
            AssertViewportParity(referenceVm, arenaVm);
        }

        [Fact]
        public async Task AssetListViewModel_RapidOverlappingFilterAsyncBurst_CancelsSupersededAndEndsOnLatestQuery()
        {
            const int count = 6000; // Above ParallelFilterThreshold (4096) to exercise parallel chunk cancellation
            var (projectIndex, eagerItems) = BuildTestDataset(count);

            foreach (var isArena in new[] { false, true })
            {
                var vm = new AssetListViewModel();
                if (isArena)
                {
                    vm.SetArenaAssets(projectIndex);
                }
                else
                {
                    vm.SetEagerAssets(eagerItems);
                }

                var burstQueries = new[]
                {
                    "a",
                    "Enemy",
                    "UI_Atlas",
                    "Prop_Crate",
                    "Audio_SFX",
                    "Common",
                    "FBX sub-asset",
                    "Shader Settings",
                    "Hero [LOD0]",
                    "Enemy_Mesh_1",
                };

                var tasks = new Task<bool>[burstQueries.Length];
                for (var i = 0; i < burstQueries.Length; i++)
                {
                    tasks[i] = vm.FilterAsync(burstQueries[i]);
                }

                var results = await Task.WhenAll(tasks);

                // The final query in the burst must win and be applied
                Assert.True(results[^1]);
                Assert.Equal("Enemy_Mesh_1", vm.SearchText);
                Assert.True(vm.VisibleAssetCount > 0);
                Assert.All(vm.VisibleItems, item =>
                    Assert.Contains("Enemy_Mesh_1", item.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public async Task AssetListViewModel_PropertyChangedNotifications_AndResetCleanup_AreComplete()
        {
            var (projectIndex, eagerItems) = BuildTestDataset(120);
            var vm = new AssetListViewModel();
            var changedProps = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    changedProps.Add(e.PropertyName);
                }
            };

            vm.SetArenaAssets(projectIndex);
            Assert.Contains(nameof(AssetListViewModel.IsArenaMode), changedProps);
            Assert.Contains(nameof(AssetListViewModel.TotalAssetCount), changedProps);
            Assert.Contains(nameof(AssetListViewModel.VisibleAssetCount), changedProps);
            Assert.Contains(nameof(AssetListViewModel.StatusSummary), changedProps);

            changedProps.Clear();
            vm.SearchText = "Hero";
            Assert.Contains(nameof(AssetListViewModel.SearchText), changedProps);

            Assert.True(await vm.FilterAsync());
            Assert.Contains(nameof(AssetListViewModel.VisibleAssetCount), changedProps);

            changedProps.Clear();
            Assert.True(await vm.SortByColumnAsync("FullSize"));
            Assert.Contains(nameof(AssetListViewModel.SortColumn), changedProps);

            changedProps.Clear();
            Assert.True(await vm.SortByColumnAsync("FullSize"));
            Assert.Contains(nameof(AssetListViewModel.SortAscending), changedProps);
            Assert.Contains(nameof(AssetListViewModel.SortDescending), changedProps);

            // Select an item and verify SelectedAssetCount notification
            changedProps.Clear();
            vm.SelectItem(vm.VisibleItems[0]);
            Assert.Equal(1, vm.SelectedAssetCount);
            Assert.Contains(nameof(AssetListViewModel.SelectedAssetCount), changedProps);

            // Filter AssetClasses via ClassSearchText
            var totalClasses = vm.AssetClasses.Count;
            Assert.True(totalClasses > 1);
            vm.ClassSearchText = "Mesh";
            Assert.True(vm.AssetClasses.Count > 0 && vm.AssetClasses.Count < totalClasses);

            // Reset() clears all state, selection, filters, sorting, and collections
            vm.Reset();
            Assert.False(vm.IsArenaMode);
            Assert.Equal(0, vm.TotalAssetCount);
            Assert.Equal(0, vm.VisibleAssetCount);
            Assert.Equal(0, vm.SelectedAssetCount);
            Assert.Empty(vm.VisibleItems);
            Assert.Empty(vm.AssetClasses);
            Assert.Empty(vm.AllAssetClasses);
            Assert.Equal(string.Empty, vm.SearchText);
            Assert.Equal(string.Empty, vm.ClassSearchText);
            Assert.Null(vm.SortColumn);
            Assert.True(vm.SortAscending);
            Assert.True(vm.IsAllTypesSelected);
            Assert.Null(vm.ClassFilterOverride);
            Assert.Equal("Ready", vm.StatusSummary);
            Assert.Equal(120, eagerItems.Count);
        }

        [Fact]
        public void IndexingProgressViewModel_FullLifecycleTransitions_AndMainWindowViewModelSync()
        {
            var loadingService = new AssetLoadingService();
            var mainVm = new MainWindowViewModel(loadingService);
            var vm = mainVm.IndexingProgress;

            // 1) Initial Idle state
            Assert.Equal(IndexingStatus.Idle, vm.Status);
            Assert.False(vm.IsActive);
            Assert.False(vm.IsIndexing);
            Assert.False(vm.IsConnectionsPhase);
            Assert.False(vm.IsPaused);
            Assert.False(vm.IsIndeterminate);
            Assert.False(vm.IsPanelVisible);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.False(vm.CanCancel);
            Assert.Equal(0.0, vm.ProgressPercent);

            // 2) Idle -> Scanning (indeterminate when totalFiles == 0, then determinate)
            vm.BeginScanning("C:\\Game\\Bundles", scannedFiles: 15, totalFiles: 0);
            Assert.Equal(IndexingStatus.Scanning, vm.Status);
            Assert.True(vm.IsActive);
            Assert.False(vm.IsIndexing);
            Assert.True(vm.IsIndeterminate);
            Assert.True(vm.IsPanelVisible);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.True(vm.CanCancel);
            Assert.True(mainVm.IsStopEnabled);
            Assert.Contains("Scanning files", vm.PrimaryStatusText, StringComparison.Ordinal);

            vm.UpdateScanningProgress(scannedFiles: 50, totalFiles: 200, currentTarget: "level0");
            Assert.False(vm.IsIndeterminate);
            Assert.Equal(25.0, vm.ProgressPercent);
            Assert.Equal("25%", vm.FormattedPercentText);

            // 3) Scanning -> Indexing
            vm.BeginIndexing(totalFiles: 200, currentFile: @"C:\Game\Bundles\chunk_01.bundle");
            Assert.Equal(IndexingStatus.Indexing, vm.Status);
            Assert.True(vm.IsActive);
            Assert.True(vm.IsIndexing);
            Assert.False(vm.IsConnectionsPhase);
            Assert.False(vm.IsPaused);
            Assert.False(vm.IsIndeterminate);
            Assert.True(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.True(vm.CanCancel);
            Assert.True(mainVm.IsPauseEnabled);
            Assert.False(mainVm.IsResumeEnabled);
            Assert.True(mainVm.IsStopEnabled);
            Assert.Equal("chunk_01.bundle", vm.CurrentFileName);
            Assert.Equal(0.0, vm.ProgressPercent);

            vm.UpdateIndexingProgress(processedFiles: 80, totalFiles: 200, currentFile: @"C:\Game\Bundles\chunk_80.bundle");
            Assert.Equal(80, vm.ProcessedFiles);
            Assert.Equal(200, vm.TotalFiles);
            Assert.Equal(120, vm.PendingFiles);
            Assert.Equal(40.0, vm.ProgressPercent);
            Assert.Equal("40%", vm.FormattedPercentText);
            Assert.Equal("chunk_80.bundle", vm.CurrentFileName);
            Assert.Contains("Indexing: 80/200 files | 120 pending | Now: chunk_80.bundle", vm.PrimaryStatusText, StringComparison.Ordinal);

            // 4) Indexing -> Paused
            vm.Pause();
            Assert.Equal(IndexingStatus.Paused, vm.Status);
            Assert.True(vm.IsActive);
            Assert.False(vm.IsIndexing);
            Assert.True(vm.IsPaused);
            Assert.False(vm.CanPause);
            Assert.True(vm.CanResume);
            Assert.True(vm.CanCancel);
            Assert.False(mainVm.IsPauseEnabled);
            Assert.True(mainVm.IsResumeEnabled);
            Assert.True(mainVm.IsStopEnabled);
            Assert.Contains("Indexing paused", vm.PrimaryStatusText, StringComparison.Ordinal);

            // 5) Paused -> Resumed (Indexing)
            vm.Resume(@"C:\Game\Bundles\chunk_81.bundle");
            Assert.Equal(IndexingStatus.Indexing, vm.Status);
            Assert.True(vm.IsIndexing);
            Assert.False(vm.IsPaused);
            Assert.True(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.True(vm.CanCancel);
            Assert.Equal("chunk_81.bundle", vm.CurrentFileName);

            // 6) Indexing -> Connecting
            vm.BeginConnections(totalSources: 50, currentSource: @"C:\Game\Bundles\sharedassets0.assets");
            Assert.Equal(IndexingStatus.Connecting, vm.Status);
            Assert.True(vm.IsActive);
            Assert.False(vm.IsIndexing);
            Assert.True(vm.IsConnectionsPhase);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.False(vm.CanCancel);
            Assert.Contains("Building connections", vm.PrimaryStatusText, StringComparison.Ordinal);

            vm.UpdateConnectionsProgress(processed: 25, total: 50, currentSource: @"C:\Game\Bundles\sharedassets25.assets");
            Assert.Equal(25, vm.ProcessedFiles);
            Assert.Equal(50, vm.TotalFiles);
            Assert.Equal(50.0, vm.ProgressPercent);
            Assert.Equal("sharedassets25.assets", vm.CurrentFileName);
            Assert.Contains("Building connections: 25/50 files | 25 pending | Now: sharedassets25.assets", vm.PrimaryStatusText, StringComparison.Ordinal);

            // 7) Connecting -> Completed
            vm.MarkCompleted(processedFiles: 50, totalFiles: 50, rawStatus: "connections_completed");
            Assert.Equal(IndexingStatus.Completed, vm.Status);
            Assert.False(vm.IsActive);
            Assert.False(vm.IsIndexing);
            Assert.False(vm.IsConnectionsPhase);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.False(vm.CanCancel);
            Assert.Equal(100.0, vm.ProgressPercent);
            Assert.Equal("100%", vm.FormattedPercentText);
            Assert.Contains("Connections complete", vm.PrimaryStatusText, StringComparison.Ordinal);

            // 8) Cancelling -> Cancelled path
            vm.BeginIndexing(totalFiles: 100, currentFile: "file1.bundle");
            vm.UpdateIndexingProgress(processedFiles: 30, totalFiles: 100, currentFile: "file30.bundle");
            vm.BeginCancelling();
            Assert.Equal(IndexingStatus.Cancelling, vm.Status);
            Assert.True(vm.IsActive);
            Assert.False(vm.CanPause);
            Assert.False(vm.CanResume);
            Assert.False(vm.CanCancel);
            Assert.Contains("Stopping indexing", vm.PrimaryStatusText, StringComparison.Ordinal);

            vm.MarkCancelled(processedFiles: 30, totalFiles: 100, lastFile: "file30.bundle");
            Assert.Equal(IndexingStatus.Cancelled, vm.Status);
            Assert.False(vm.IsActive);
            Assert.Equal(30.0, vm.ProgressPercent);
            Assert.Contains("Indexing cancelled", vm.PrimaryStatusText, StringComparison.Ordinal);

            // 9) Failed path
            vm.MarkFailed("SQLite write error", rawStatus: "failed");
            Assert.Equal(IndexingStatus.Failed, vm.Status);
            Assert.False(vm.IsActive);
            Assert.Equal("SQLite write error", vm.DetailStatusText);
            Assert.Contains("Indexing failed", vm.PrimaryStatusText, StringComparison.Ordinal);
        }

        [Fact]
        public void IndexingProgressViewModel_ApplySnapshotIncompleteSessionBannerAndNoOpPropertyChangedDeduplication()
        {
            var vm = new IndexingProgressViewModel();

            // 1) Incomplete previous session banner state
            vm.SetIncompletePreviousSession("paused", 62.5, @"C:\Game\Bundles\hero_04.bundle", processedFiles: 125, totalFiles: 200);
            Assert.True(vm.HasPreviousIncompleteSession);
            Assert.Equal("Previous indexing state: paused, 62.5% complete, last file: hero_04.bundle", vm.IncompleteSessionSummary);
            Assert.Equal(IndexingStatus.Paused, vm.Status);
            Assert.Equal(62.5, vm.ProgressPercent);

            // Completed status clears incomplete session banner
            vm.SetIncompletePreviousSession("structure_completed", 100.0, "hero_04.bundle");
            Assert.False(vm.HasPreviousIncompleteSession);
            Assert.Equal(string.Empty, vm.IncompleteSessionSummary);

            // 2) ApplyUpdate parity with IndexingProgressUpdate (including stage progress with 3 decimal places)
            var stageUpdate = new IndexingProgressUpdate
            {
                Status = "saving_index",
                ProcessedFiles = 1,
                TotalFiles = 3,
                PendingFiles = 2,
                PercentComplete = 33.333,
                CurrentFile = "Writing AssetHandles table",
                LastReadFile = "Writing AssetHandles table",
            };
            vm.ApplyUpdate(stageUpdate, percentDecimals: 3);
            Assert.Equal(IndexingStatus.Indexing, vm.Status);
            Assert.Equal("saving_index", vm.RawStatus);
            Assert.Equal("33.333%", vm.FormattedPercentText);
            Assert.Equal("Saving index cache: 1/3 steps | 2 pending | Writing AssetHandles table", vm.PrimaryStatusText);
            // Stage progress is non-pausable
            Assert.False(vm.CanPause);
            Assert.False(vm.CanCancel);

            // 3) Duplicate / no-op updates must emit ZERO PropertyChanged events
            var snapshot = IndexingProgressSnapshot.FromUpdate(stageUpdate, percentDecimals: 3);
            var changeCount = 0;
            vm.PropertyChanged += (_, _) => changeCount++;

            vm.ApplySnapshot(snapshot);
            vm.ApplyUpdate(stageUpdate, percentDecimals: 3);
            Assert.Equal(0, changeCount);

            // 4) Reset clears everything and duplicate Reset emits zero extra events
            vm.Reset();
            Assert.Equal(IndexingStatus.Idle, vm.Status);
            Assert.False(vm.IsActive);
            Assert.False(vm.IsPanelVisible);
            Assert.False(vm.HasPreviousIncompleteSession);
            Assert.Equal(string.Empty, vm.PrimaryStatusText);
            Assert.Equal("0%", vm.FormattedPercentText);

            changeCount = 0;
            vm.Reset();
            Assert.Equal(0, changeCount);
        }

        [Theory]
        [InlineData("", "", "none")]
        [InlineData("   \t  ", "", "none")]
        [InlineData("/", "/", "/")]
        [InlineData("\\", "\\", "\\")]
        [InlineData(@"C:\", "C:", "C:")]
        [InlineData("chunk_01.bundle", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData(@"C:\Game\Bundles\chunk_01.bundle", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData("/opt/game/bundles/chunk_01.bundle", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData(@"C:\Game/Bundles\chunk_01.bundle", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData(@"C:\Game\Bundles/chunk_01.bundle", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData(@"C:\Game\Bundles\chunk_01.bundle\", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData("/opt/game/bundles/chunk_01.bundle/", "chunk_01.bundle", "chunk_01.bundle")]
        [InlineData(@"C:\Game\Bundles\", "Bundles", "Bundles")]
        [InlineData("/opt/game/bundles/", "bundles", "bundles")]
        public void IndexingProgressViewModel_CrossPlatformPathSeparators_ExtractFileNameConsistently(
            string rawPath,
            string expectedCurrentFileName,
            string expectedIncompleteSummaryFileName)
        {
            var vm = new IndexingProgressViewModel();

            vm.BeginIndexing(totalFiles: 10, currentFile: rawPath);
            Assert.Equal(expectedCurrentFileName, vm.CurrentFileName);

            vm.SetIncompletePreviousSession("paused", 50.0, lastReadFile: rawPath, processedFiles: 5, totalFiles: 10);
            Assert.Equal(
                $"Previous indexing state: paused, 50% complete, last file: {expectedIncompleteSummaryFileName}",
                vm.IncompleteSessionSummary);
        }

        private static void AssertViewportParity(AssetListViewModel left, AssetListViewModel right)
        {
            Assert.Equal(left.VisibleItems.Count, right.VisibleItems.Count);
            for (var i = 0; i < left.VisibleItems.Count; i++)
            {
                var l = left.VisibleItems[i];
                var r = right.VisibleItems[i];
                Assert.Equal(l.PathID, r.PathID);
                Assert.Equal(l.Name, r.Name);
                Assert.Equal(l.Type, r.Type);
                Assert.Equal(l.Container, r.Container);
                Assert.Equal(l.FullSize, r.FullSize);
                Assert.Equal(l.DisplayType, r.DisplayType);
            }
        }

        private static (ProjectIndex Index, List<AssetItem> EagerItems) BuildTestDataset(int count)
        {
            var types = new[]
            {
                ClassIDType.Texture2D,
                ClassIDType.Mesh,
                ClassIDType.Material,
                ClassIDType.Transform,
                ClassIDType.PrefabInstance,
                ClassIDType.GameObject,
                ClassIDType.AudioClip,
                ClassIDType.MonoBehaviour,
            };

            var fileA = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            fileA.unityVersion = "2022.3.10f1";
            fileA.fileName = "level0";
            fileA.originalPath = @"C:\MvvmTestGame\level0";

            var fileB = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            fileB.unityVersion = "2021.3.20f1";
            fileB.fileName = "sharedassets0";
            fileB.originalPath = @"C:\MvvmTestGame\sharedassets0";

            var projectIndex = new ProjectIndex();
            var items = new List<AssetItem>(count);

            for (var i = 0; i < count; i++)
            {
                var type = types[i % types.Length];
                var container = (i % 5) switch
                {
                    0 => string.Empty,
                    1 => $"assets/models/pack_{i % 23}/Hero_Model.FBX/part_{i % 7}",
                    2 => $@"assets\textures\pack_{i % 11}\albedo_{i}.png",
                    3 => $"assets/shaders/lit_{i % 5}.shader",
                    _ => $"assets/prefabs/item_{i}.prefab",
                };

                var name = (i % 7) switch
                {
                    0 => $"Unnamed_{i}",
                    1 => $"Hero [LOD0] café_日本語_{i}",
                    2 => $"Enemy_Mesh_{i}",
                    3 => $"UI_Atlas_{i}",
                    4 => $"Prop_Crate_{i}",
                    5 => $"Audio_SFX_{i}",
                    _ => $"Common_{i % 50}",
                };

                var pathId = (long)((i * 7919) % 100_000) - 500;
                var byteSize = (i * 31L) % 65536;
                var sourceFile = ((i / 8) % 2 == 0) ? fileA : fileB;

                var idx = projectIndex.AddRecord(
                    serializedFileName: sourceFile.fileName,
                    originalPath: sourceFile.originalPath,
                    pathId: pathId,
                    name: name,
                    type: type,
                    container: container,
                    byteStart: i * 128L,
                    byteSize: byteSize,
                    unityVersion: sourceFile.unityVersion,
                    sourceFile: sourceFile,
                    customUniqueId: $"uid#{i:D6}");
                Assert.Equal(i, idx);

                items.Add(new AssetItem(projectIndex.MaterializeHandle(idx), idx));
            }

            _ = projectIndex.DrainPendingIndices();
            return (projectIndex, items);
        }
    }
}
