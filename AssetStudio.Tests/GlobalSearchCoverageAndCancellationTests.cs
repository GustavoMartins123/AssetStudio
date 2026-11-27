#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class GlobalSearchCoverageAndCancellationTests
    {
        [Fact]
        public void GlobalSearch_MetadataOnly_ReportsExplicitIndexedScopeAndNotExaminedScopes()
        {
            var index = new ProjectIndex();
            index.AddRecord("shared0.assets", "/synthetic/shared0.assets", 1, "HeroAlbedo", ClassIDType.Texture2D, "assets/tex/hero.png", 0, 2048);
            index.AddRecord("shared0.assets", "/synthetic/shared0.assets", 2, "QuestConfig", ClassIDType.TextAsset, "assets/data/quest.json", 0, 512);
            index.AddRecord("shared0.assets", "/synthetic/shared0.assets", 3, "HeroBehaviour", ClassIDType.MonoBehaviour, string.Empty, 0, 256);

            var result = GlobalProjectQueryService.SearchAssets(
                new GlobalSearchQuery { QueryText = "Hero", SearchTextAssetContents = false },
                index);

            Assert.Null(result.ErrorMessage);
            Assert.Equal(2, result.Hits.Count);
            Assert.Equal(GlobalSearchIndexSource.ProjectIndex, result.Coverage.IndexSource);
            Assert.Equal(GlobalSearchCoverageStatus.CompleteMetadataOnly, result.Coverage.Status);
            Assert.Equal(3, result.Coverage.IndexedAssetsCount);
            Assert.Equal(0, result.Coverage.LoadedAssetsCount);
            Assert.Equal(3, result.Coverage.AssetsScannedCount);
            Assert.False(result.Coverage.IsPartial);
            Assert.Contains("Metadata Only", result.Coverage.BadgeText, StringComparison.Ordinal);
            Assert.Contains(result.Coverage.NotExaminedScopes, s => s.Contains("TextAsset contents", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Coverage.NotExaminedScopes, s => s.Contains("MonoBehaviour", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Coverage.NotExaminedScopes, s => s.Contains("script", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GlobalSearch_TextAssetLimits_ReportsPartialTextContentCoverageAccurately()
        {
            var index = new ProjectIndex();
            var smallBody = Encoding.UTF8.GetBytes("target_token_alpha");
            index.AddRecord("data.assets", "/synthetic/data.assets", 11, "HugeBlob", ClassIDType.TextAsset, "", 0, GlobalProjectQueryService.MaxTextAssetBytes + 512);
            index.AddRecord("data.assets", "/synthetic/data.assets", 12, "MissingOnDisk", ClassIDType.TextAsset, "", 0, 128);
            index.AddRecord("data.assets", "/synthetic/data.assets", 10, "NoteA", ClassIDType.TextAsset, "", 0, smallBody.Length);
            index.AddRecord("data.assets", "/synthetic/data.assets", 13, "NoteB", ClassIDType.TextAsset, "", 0, smallBody.Length);

            byte[]? Read(AssetHandle handle) => handle.PathID switch
            {
                10 => smallBody,
                13 => smallBody,
                _ => null
            };

            var result = GlobalProjectQueryService.SearchAssetsWithContentReader(
                new GlobalSearchQuery
                {
                    QueryText = "target_token_alpha",
                    SearchTextAssetContents = true,
                    MaxTextAssetsScanned = 1
                },
                index,
                assetsManager: null,
                textContentOverride: Read);

            Assert.Equal(GlobalSearchCoverageStatus.PartialTextContentLimits, result.Coverage.Status);
            Assert.True(result.Coverage.IsPartial);
            Assert.Equal(4, result.Coverage.TextAssetsEligibleCount);
            Assert.Equal(1, result.Coverage.TextAssetsExaminedCount);
            Assert.Equal(1, result.Coverage.TextAssetsSkippedBySizeCapCount);
            Assert.Equal(1, result.Coverage.TextAssetsUnavailableCount);
            Assert.Equal(1, result.Coverage.TextAssetsSkippedByCountCapCount);
            Assert.True(result.Coverage.TextAssetCountLimitReached);
            Assert.Contains("Partial (TextAsset limits)", result.Coverage.BadgeText, StringComparison.Ordinal);
            Assert.Contains(result.Coverage.NotExaminedScopes, s => s.Contains("size cap", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Coverage.NotExaminedScopes, s => s.Contains("content cap", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Coverage.NotExaminedScopes, s => s.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GlobalSearch_ResultLimit_ReportsPartialResultLimitCoverage()
        {
            var index = new ProjectIndex();
            for (int i = 1; i <= 10; i++)
            {
                index.AddRecord("pack.assets", "/synthetic/pack.assets", i, $"Prop_{i}", ClassIDType.Mesh, "", 0, 100);
            }

            var result = GlobalProjectQueryService.SearchAssets(
                new GlobalSearchQuery { QueryText = "Prop_", MaxResults = 3 },
                index);

            Assert.Equal(3, result.Hits.Count);
            Assert.True(result.IsTruncated);
            Assert.True(result.Coverage.ResultLimitReached);
            Assert.Equal(GlobalSearchCoverageStatus.PartialResultLimit, result.Coverage.Status);
            Assert.Contains("Partial (result cap 3)", result.Coverage.BadgeText, StringComparison.Ordinal);
        }

        [Fact]
        public void GlobalSearch_Cancellation_ThrowsByDefaultAndReturnsPartialCancelledResultWhenEnabled()
        {
            var index = new ProjectIndex();
            for (int i = 1; i <= 50; i++)
            {
                index.AddRecord("big.assets", "/synthetic/big.assets", i, $"Asset_{i}", ClassIDType.Texture2D, "", 0, 100);
            }

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                GlobalProjectQueryService.SearchAssets(new GlobalSearchQuery { QueryText = "Asset_" }, index, ct: cts.Token));

            var partial = GlobalProjectQueryService.SearchAssets(
                new GlobalSearchQuery { QueryText = "Asset_", ReturnPartialOnCancellation = true },
                index,
                ct: cts.Token);

            Assert.True(partial.WasCancelled);
            Assert.True(partial.Coverage.WasCancelled);
            Assert.Equal(GlobalSearchCoverageStatus.Cancelled, partial.Coverage.Status);
            Assert.Contains("Cancelled", partial.Coverage.BadgeText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cancelled", partial.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GlobalSearchDialogViewModel_CancelCommandAndContextPreservation_WorkCleanly()
        {
            using var started = new ManualResetEventSlim(false);
            using var cancelVm = new GlobalSearchDialogViewModel(
                projectIndex: null,
                searchRunner: (query, ct) =>
                {
                    started.Set();
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    ct.ThrowIfCancellationRequested();
                    return GlobalSearchResult.Empty;
                });

            cancelVm.QueryText = "Hero";
            var searchTask = cancelVm.ExecuteQueryCommand.ExecuteAsync();
            Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(cancelVm.IsBusy);
            Assert.True(cancelVm.CancelQueryCommand.CanExecute());

            cancelVm.CancelQueryCommand.Execute();
            await searchTask;

            Assert.False(cancelVm.IsBusy);
            Assert.False(cancelVm.CancelQueryCommand.CanExecute());
            Assert.Contains("cancel", cancelVm.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GlobalSearchCoverageStatus.Cancelled, cancelVm.LastSearchCoverage.Status);

            // Context preservation: Search -> Navigate -> Graph -> Back to Search preserves query, hits, selection & coverage
            var index = new ProjectIndex();
            index.AddRecord("s0.assets", "/synthetic/s0.assets", 7, "BannerTex", ClassIDType.Texture2D, "assets/banner.png", 0, 512);
            index.AddRecord("s0.assets", "/synthetic/s0.assets", 8, "BannerMat", ClassIDType.Material, "assets/banner.mat", 0, 256);

            using var vm = new GlobalSearchDialogViewModel(index);
            vm.QueryText = "Banner";
            await vm.ExecuteQueryCommand.ExecuteAsync();

            Assert.Equal(2, vm.Results.Count);
            Assert.True(vm.IsSearchCoverageVisible);
            Assert.Contains("Metadata Only", vm.CoverageBadgeText, StringComparison.Ordinal);
            Assert.Contains("Not examined:", vm.NotExaminedText, StringComparison.Ordinal);

            var chosen = vm.Results[1];
            vm.SelectedResult = chosen;

            GlobalSearchResultItem? navigated = null;
            vm.NavigateToResultRequested += item => navigated = item;
            vm.GoToSelectedCommand.Execute();
            Assert.Same(chosen, navigated);
            Assert.Same(chosen, vm.SelectedResult);
            Assert.Equal(2, vm.Results.Count);

            // Switch to Graph mode and back to Search mode -> original search context restored intact!
            vm.SwitchToGraphModeCommand.Execute();
            Assert.True(vm.IsGraphMode);
            Assert.False(vm.IsSearchCoverageVisible);

            vm.SwitchToSearchModeCommand.Execute();
            Assert.True(vm.IsSearchMode);
            Assert.Equal("Banner", vm.QueryText);
            Assert.Equal(2, vm.Results.Count);
            Assert.Same(chosen, vm.SelectedResult);
            Assert.True(vm.IsSearchCoverageVisible);
            Assert.Contains("Metadata Only", vm.CoverageBadgeText, StringComparison.Ordinal);
        }
    }
}
