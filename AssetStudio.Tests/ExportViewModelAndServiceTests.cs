using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using K4os.Compression.LZ4;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace AssetStudio.Tests
{
    public class ExportViewModelAndServiceTests
    {
        [Fact]
        public void ExportViewModel_FullLifecycleTransitions_ExportExtractCancelFailAndReset()
        {
            using var vm = new ExportViewModel();

            // 1) Initial Idle state
            Assert.False(vm.IsExporting);
            Assert.False(vm.IsExtracting);
            Assert.False(vm.IsBusy);
            Assert.False(vm.CanCancel);
            Assert.False(vm.IsCancellationRequested);
            Assert.Equal(0, vm.ProgressPercent);
            Assert.Equal(0, vm.CompletedCount);
            Assert.Equal(0, vm.TotalCount);
            Assert.Equal(0, vm.SucceededCount);
            Assert.Equal(0, vm.SkippedCount);
            Assert.Equal(0, vm.FailedCount);
            Assert.Equal(string.Empty, vm.CurrentItemName);
            Assert.Equal(string.Empty, vm.StatusText);
            Assert.Equal(string.Empty, vm.LastExportFolder);
            Assert.Null(vm.LastErrorLogPath);
            Assert.Equal(ExportOperationMode.None, vm.ActiveMode);
            Assert.Equal(ExportOperationStatus.Idle, vm.Status);

            Assert.Throws<ArgumentException>(() => vm.BeginExport(ExportOperationMode.None, 10));

            // 2) Idle -> Exporting (Convert) -> Completed
            var convertToken = vm.BeginExport(ExportOperationMode.Convert, 100, @"C:\Exports\Hero");
            Assert.False(convertToken.IsCancellationRequested);
            Assert.True(vm.IsExporting);
            Assert.False(vm.IsExtracting);
            Assert.True(vm.IsBusy);
            Assert.True(vm.CanCancel);
            Assert.Equal(ExportOperationMode.Convert, vm.ActiveMode);
            Assert.Equal(ExportOperationStatus.Running, vm.Status);
            Assert.Equal(100, vm.TotalCount);
            Assert.Equal(0, vm.ProgressPercent);
            Assert.Equal(@"C:\Exports\Hero", vm.LastExportFolder);
            Assert.Null(vm.LastErrorLogPath);

            vm.ReportProgress(40, 100, "HeroAlbedo.png", succeeded: 36, skipped: 3, failed: 1);
            Assert.Equal(40, vm.CompletedCount);
            Assert.Equal(40, vm.ProgressPercent);
            Assert.Equal("HeroAlbedo.png", vm.CurrentItemName);
            Assert.Equal(36, vm.SucceededCount);
            Assert.Equal(3, vm.SkippedCount);
            Assert.Equal(1, vm.FailedCount);

            vm.Complete(
                succeeded: 95,
                skipped: 3,
                failed: 2,
                errorLogPath: @"C:\Exports\Hero\errors.txt");
            Assert.False(vm.IsExporting);
            Assert.False(vm.IsExtracting);
            Assert.False(vm.IsBusy);
            Assert.False(vm.CanCancel);
            Assert.Equal(ExportOperationMode.None, vm.ActiveMode);
            Assert.Equal(ExportOperationStatus.Completed, vm.Status);
            Assert.Equal(100, vm.ProgressPercent);
            Assert.Equal(100, vm.CompletedCount);
            Assert.Equal(95, vm.SucceededCount);
            Assert.Equal(3, vm.SkippedCount);
            Assert.Equal(2, vm.FailedCount);
            Assert.Equal(string.Empty, vm.CurrentItemName);
            Assert.Equal(@"C:\Exports\Hero", vm.LastExportFolder);
            Assert.Equal(@"C:\Exports\Hero\errors.txt", vm.LastErrorLogPath);
            Assert.Contains("95 assets", vm.StatusText, StringComparison.Ordinal);
            Assert.Contains("Error report: errors.txt.", vm.StatusText, StringComparison.Ordinal);

            // 3) Idle/Completed -> Extracting -> Cancelling -> Cancelled
            var extractToken = vm.BeginExport(ExportOperationMode.ExtractBundle, 20, @"C:\Exports\Bundles");
            Assert.False(vm.IsExporting);
            Assert.True(vm.IsExtracting);
            Assert.True(vm.IsBusy);
            Assert.True(vm.CanCancel);
            Assert.Equal(ExportOperationMode.ExtractBundle, vm.ActiveMode);
            Assert.Null(vm.LastErrorLogPath);
            Assert.Equal(@"C:\Exports\Bundles", vm.LastExportFolder);

            vm.ReportProgress(7, 20, "bundle_07.unity3d", succeeded: 14, skipped: 0, failed: 0);
            Assert.Equal(35, vm.ProgressPercent);
            Assert.Equal(7, vm.CompletedCount);

            vm.RequestCancel();
            Assert.True(extractToken.IsCancellationRequested);
            Assert.False(vm.CanCancel);
            Assert.True(vm.IsCancellationRequested);
            Assert.True(vm.IsBusy);
            Assert.Equal(ExportOperationStatus.Cancelling, vm.Status);
            Assert.Contains("Cancelling", vm.StatusText, StringComparison.OrdinalIgnoreCase);

            // Duplicate RequestCancel must emit zero PropertyChanged events
            var cancelRepeatChanges = 0;
            vm.PropertyChanged += (_, _) => cancelRepeatChanges++;
            vm.RequestCancel();
            Assert.Equal(0, cancelRepeatChanges);

            // Complete auto-detects cancellation from the active token
            vm.Complete(succeeded: 7, skipped: 0, failed: 0);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsExtracting);
            Assert.False(vm.CanCancel);
            Assert.Equal(ExportOperationMode.None, vm.ActiveMode);
            Assert.Equal(ExportOperationStatus.Cancelled, vm.Status);
            Assert.Equal(35, vm.ProgressPercent);
            Assert.Contains("cancelled", vm.StatusText, StringComparison.OrdinalIgnoreCase);

            // 4) Failed state
            _ = vm.BeginExport(ExportOperationMode.XmlList, 50, @"C:\Exports\Xml");
            vm.Fail("Access denied writing assets.xml", @"C:\Exports\Xml\errors.txt");
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsExporting);
            Assert.False(vm.CanCancel);
            Assert.Equal(ExportOperationMode.None, vm.ActiveMode);
            Assert.Equal(ExportOperationStatus.Failed, vm.Status);
            Assert.Equal("Access denied writing assets.xml", vm.StatusText);
            Assert.Equal(@"C:\Exports\Xml\errors.txt", vm.LastErrorLogPath);

            // 5) Reset clears all properties and cancels any in-flight token; repeated Reset emits 0 events
            var resetToken = vm.BeginExport(ExportOperationMode.Animator, 1, @"C:\Exports\Anim");
            vm.Reset();
            Assert.True(resetToken.IsCancellationRequested);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsExporting);
            Assert.False(vm.IsExtracting);
            Assert.False(vm.CanCancel);
            Assert.False(vm.IsCancellationRequested);
            Assert.Equal(0, vm.ProgressPercent);
            Assert.Equal(0, vm.CompletedCount);
            Assert.Equal(0, vm.TotalCount);
            Assert.Equal(0, vm.SucceededCount);
            Assert.Equal(0, vm.SkippedCount);
            Assert.Equal(0, vm.FailedCount);
            Assert.Equal(string.Empty, vm.CurrentItemName);
            Assert.Equal(string.Empty, vm.StatusText);
            Assert.Equal(string.Empty, vm.LastExportFolder);
            Assert.Null(vm.LastErrorLogPath);
            Assert.Equal(ExportOperationMode.None, vm.ActiveMode);
            Assert.Equal(ExportOperationStatus.Idle, vm.Status);

            var resetRepeatChanges = 0;
            vm.PropertyChanged += (_, _) => resetRepeatChanges++;
            vm.Reset();
            Assert.Equal(0, resetRepeatChanges);
        }

        [Fact]
        public void ExportViewModel_ProgressClampingThrottlingDeduplicationAndStaleDispatchProtection()
        {
            long fakeClock = 10_000L;
            var queuedUiActions = new Queue<Action>();
            void DrainUiQueue()
            {
                while (queuedUiActions.Count > 0)
                {
                    queuedUiActions.Dequeue().Invoke();
                }
            }

            using var vm = new ExportViewModel(
                dispatchToUi: action => queuedUiActions.Enqueue(action),
                progressThrottleMs: 50,
                clockTicksProvider: () => fakeClock);

            var changedProps = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    changedProps.Add(e.PropertyName);
                }
            };

            _ = vm.BeginExport(ExportMode.Dump, 1000, @"C:\Exports\Dump");
            DrainUiQueue();
            Assert.Equal(ExportOperationMode.Dump, vm.ActiveMode);

            // First item always dispatches
            vm.ReportProgress(1, 1000, "Item_1", succeeded: 1, skipped: 0, failed: 0);
            DrainUiQueue();
            Assert.Equal(1, vm.CompletedCount);
            Assert.Equal(0, vm.ProgressPercent);
            Assert.Equal("Item_1", vm.CurrentItemName);
            var dispatchesAfterFirst = vm.ProgressDispatchCount;
            Assert.Equal(1, dispatchesAfterFirst);

            // Items 2..9 are all still 0% and within 50ms -> throttled (0 UI dispatches)
            fakeClock += 10;
            for (int i = 2; i <= 9; i++)
            {
                vm.ReportProgress(i, 1000, $"Item_{i}", succeeded: i, skipped: 0, failed: 0);
            }
            Assert.Equal(dispatchesAfterFirst, vm.ProgressDispatchCount);
            Assert.Empty(queuedUiActions);

            // Advancing clock past 50ms allows sub-1% progress to flush with updated item name
            fakeClock += 50;
            vm.ReportProgress(9, 1000, "Item_9", succeeded: 9, skipped: 0, failed: 0);
            DrainUiQueue();
            Assert.Equal(dispatchesAfterFirst + 1, vm.ProgressDispatchCount);
            Assert.Equal(9, vm.CompletedCount);
            Assert.Equal("Item_9", vm.CurrentItemName);

            // Duplicate forced report with identical values must emit ZERO PropertyChanged events
            changedProps.Clear();
            vm.ReportProgress(9, 1000, "Item_9", succeeded: 9, skipped: 0, failed: 0, force: true);
            DrainUiQueue();
            Assert.Empty(changedProps);

            // 1000 rapid reports at same tick throttle to <= 105 dispatches (at most 1 per percent step + final)
            var baselineDispatches = vm.ProgressDispatchCount;
            for (int i = 10; i <= 1000; i++)
            {
                vm.ReportProgress(i, 1000, $"Item_{i}", succeeded: i, skipped: 0, failed: 0);
            }
            DrainUiQueue();
            Assert.InRange(vm.ProgressDispatchCount - baselineDispatches, 99, 105);
            Assert.Equal(100, vm.ProgressPercent);
            Assert.Equal(1000, vm.CompletedCount);

            // Clamping check: out-of-range negative and > total values clamp cleanly to [0..100] and >= 0
            _ = vm.BeginExport(ExportMode.Raw, 50);
            DrainUiQueue();
            vm.ReportProgress(-10, 50, "Neg", succeeded: -5, skipped: -2, failed: -1, force: true);
            DrainUiQueue();
            Assert.Equal(0, vm.CompletedCount);
            Assert.Equal(0, vm.ProgressPercent);
            Assert.Equal(0, vm.SucceededCount);
            Assert.Equal(0, vm.SkippedCount);
            Assert.Equal(0, vm.FailedCount);

            vm.ReportProgress(999, 50, "Over", succeeded: 50, skipped: 0, failed: 0, force: true);
            DrainUiQueue();
            Assert.Equal(50, vm.CompletedCount);
            Assert.Equal(100, vm.ProgressPercent);

            // Stale queued progress update must NOT overwrite Completed state after Complete()
            var secondToken = vm.BeginExport(ExportOperationMode.SplitObjects, 100);
            DrainUiQueue();
            vm.ReportProgress(25, 100, "StaleItem", succeeded: 25, force: true);
            // Do NOT drain yet; complete immediately, then drain stale + final in order
            vm.Complete(succeeded: 100, skipped: 0, failed: 0);
            DrainUiQueue();
            Assert.Equal(ExportOperationStatus.Completed, vm.Status);
            Assert.Equal(100, vm.ProgressPercent);
            Assert.Equal(string.Empty, vm.CurrentItemName);
            Assert.False(secondToken.IsCancellationRequested);

            // Sequential BeginExport while still active cancels the previous operation's token
            var activeToken1 = vm.BeginExport(ExportOperationMode.Convert, 10);
            var activeToken2 = vm.BeginExport(ExportOperationMode.Raw, 20);
            DrainUiQueue();
            Assert.True(activeToken1.IsCancellationRequested);
            Assert.False(activeToken2.IsCancellationRequested);
            Assert.Equal(ExportOperationMode.Raw, vm.ActiveMode);
            Assert.Equal(20, vm.TotalCount);
        }

        [Fact]
        public void AssetExportService_ParallelBatchExport_WithSyntheticAssets_ExportsConvertsSkipsAndUpdatesViewModelAndProgress()
        {
            var tempRoot = CreateTempDirectory("service_parallel_convert");
            try
            {
                var items = new List<AssetItem>();

                // 20 synthetic TextAssets with restored container extensions (.json, .txt) + intentional duplicate names
                for (int i = 0; i < 20; i++)
                {
                    var textAsset = CreateUninitializedObject<TextAsset>(ClassIDType.TextAsset, pathId: 1000 + i);
                    textAsset.m_Name = i < 5 ? "DuplicateConfig" : $"Config_{i:D2}";
                    textAsset.m_Script = Encoding.UTF8.GetBytes($"{{\"index\":{i}}}");
                    items.Add(new AssetItem(textAsset)
                    {
                        Name = textAsset.m_Name,
                        Container = $"assets/configs/item_{i}.json",
                        UniqueID = $"text#{i}"
                    });
                }

                // 5 synthetic MonoScripts
                for (int i = 0; i < 5; i++)
                {
                    var script = CreateUninitializedObject<MonoScript>(ClassIDType.MonoScript, pathId: 2000 + i);
                    script.m_Name = $"Behavior_{i}";
                    script.m_AssemblyName = "Assembly-CSharp.dll";
                    script.m_Namespace = "Game.Core";
                    script.m_ClassName = $"Behavior_{i}";
                    items.Add(new AssetItem(script)
                    {
                        Name = script.m_Name,
                        UniqueID = $"script#{i}"
                    });
                }

                // 2 lazy FBX Material/Shader items -> must be skipped without resolving Handle.RealObject
                var lazyFbxMatHandle = new AssetHandle
                {
                    UniqueID = "lazy_fbx_mat",
                    Name = "HeroFbxMat",
                    Type = ClassIDType.Material,
                    Container = "assets/models/hero.fbx/HeroFbxMat",
                    PathID = 3001,
                    OriginalPath = @"C:\NonExistent\hero.bundle",
                    SerializedFileName = "CAB-hero"
                };
                var lazyFbxMatItem = new AssetItem(lazyFbxMatHandle, 0);
                items.Add(lazyFbxMatItem);

                var progressReports = new ConcurrentQueue<AssetExportProgressReport>();
                var syncProgress = new SynchronousProgress<AssetExportProgressReport>(r => progressReports.Enqueue(r));
                using var vm = new ExportViewModel();
                var token = vm.BeginExport(ExportMode.Convert, items.Count, tempRoot);

                var service = new AssetExportService();
                var summary = service.ExportAssetsBatch(
                    items,
                    ExportMode.Convert,
                    tempRoot,
                    new AssetBatchExportOptions
                    {
                        ExportOptions = new ExportOptionsState
                        {
                            AssetGrouping = AssetGroupOption.TypeName,
                            RestoreExtensionName = true
                        },
                        ViewModel = vm,
                        Progress = syncProgress,
                        MaxDegreeOfParallelism = 4
                    },
                    token);

                vm.Complete(summary.Exported, summary.Skipped, summary.Failed, errorLogPath: summary.ErrorReportPath, wasCancelled: summary.WasCancelled);

                Assert.False(summary.WasCancelled);
                Assert.Equal(25, summary.Exported);
                Assert.Equal(1, summary.Skipped);
                Assert.Equal(0, summary.Failed);
                Assert.Empty(summary.Errors);
                Assert.Equal(1, summary.BatchCount);

                // Lazy FBX material handle was never resolved
                Assert.Null(lazyFbxMatHandle.RealObject);

                // Verify exported files on disk (including all 5 duplicate-named TextAssets with zero overwrites)
                var textAssetDir = Path.Combine(tempRoot, "TextAsset");
                var monoScriptDir = Path.Combine(tempRoot, "MonoScript");
                Assert.Equal(20, Directory.GetFiles(textAssetDir, "*.json").Length);
                Assert.Equal(5, Directory.GetFiles(monoScriptDir, "*.txt").Length);

                var script0Text = File.ReadAllText(Path.Combine(monoScriptDir, "Behavior_0.txt"));
                Assert.Contains("Assembly: Assembly-CSharp.dll", script0Text);
                Assert.Contains("Namespace: Game.Core", script0Text);
                Assert.Contains("Class: Behavior_0", script0Text);

                // export-current.txt must be cleaned up on completion
                Assert.False(File.Exists(Path.Combine(tempRoot, "export-current.txt")));

                // ViewModel and IProgress both observed completion
                Assert.Equal(ExportOperationStatus.Completed, vm.Status);
                Assert.Equal(100, vm.ProgressPercent);
                Assert.Equal(25, vm.SucceededCount);
                Assert.Equal(1, vm.SkippedCount);
                Assert.Equal(0, vm.FailedCount);
                Assert.Equal(items.Count, progressReports.Count);
                Assert.Contains(progressReports, r => r.Completed == items.Count && r.Percent == 100);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetExportService_ParallelBatchExport_CancellationMidBatch_StopsEarlyCleansCurrentFileAndRespectsThrowFlag()
        {
            var tempRoot = CreateTempDirectory("service_cancel_midbatch");
            try
            {
                const int itemCount = 120;
                var items = Enumerable.Range(0, itemCount)
                    .Select(i => new AssetItem(new AssetHandle
                    {
                        UniqueID = $"cancel_uid_{i}",
                        Name = $"Asset_{i:D3}",
                        Type = ClassIDType.TextAsset,
                        PathID = 5000 + i,
                        OriginalPath = $@"C:\Synthetic\bundle_{i % 6}.ab",
                        SerializedFileName = $"CAB-{i % 6}",
                        ByteSize = 1024
                    }, i))
                    .ToList();

                using var vm = new ExportViewModel();
                var token = vm.BeginExport(ExportMode.Convert, itemCount, tempRoot);

                var service = new AssetExportService();
                var processed = 0;

                // 1) Graceful cancellation (ThrowOnCancellation = false)
                var summary = service.ExportAssetsBatch(
                    items,
                    ExportMode.Convert,
                    tempRoot,
                    new AssetBatchExportOptions
                    {
                        IsLazyLoading = true,
                        LazySourceKeyResolver = x => x.Handle?.OriginalPath,
                        MaxSourceFilesPerBatch = 2,
                        MaxDegreeOfParallelism = 1,
                        ViewModel = vm,
                        CustomItemExporter = (asset, dir, ctx) =>
                        {
                            Assert.True(ctx.TryGetAssetFilePath(dir, asset.Name, ".txt", asset, out var filePath));
                            AssetExportService.WriteTextExport(filePath, asset.Name);
                            if (Interlocked.Increment(ref processed) == 15)
                            {
                                vm.RequestCancel();
                            }
                            return true;
                        }
                    },
                    token);

                vm.Complete(summary.Exported, summary.Skipped, summary.Failed, wasCancelled: summary.WasCancelled);

                Assert.True(summary.WasCancelled);
                Assert.Equal(15, summary.Exported);
                Assert.True(summary.Exported < itemCount);
                Assert.False(File.Exists(Path.Combine(tempRoot, "export-current.txt")));
                Assert.Equal(ExportOperationStatus.Cancelled, vm.Status);
                Assert.False(vm.IsBusy);
                Assert.False(vm.CanCancel);
                Assert.True(vm.ProgressPercent > 0 && vm.ProgressPercent < 100);

                // 2) ThrowOnCancellation = true propagates OperationCanceledException while still cleaning up export-current.txt
                var throwDir = Path.Combine(tempRoot, "throw_mode");
                using var cts = new CancellationTokenSource();
                var countBeforeThrow = 0;
                Assert.Throws<OperationCanceledException>(() =>
                {
                    service.ExportAssetsBatch(
                        items,
                        ExportMode.Convert,
                        throwDir,
                        new AssetBatchExportOptions
                        {
                            MaxDegreeOfParallelism = 1,
                            ThrowOnCancellation = true,
                            CustomItemExporter = (asset, dir, ctx) =>
                            {
                                if (Interlocked.Increment(ref countBeforeThrow) >= 3)
                                {
                                    cts.Cancel();
                                }
                                return true;
                            }
                        },
                        cts.Token);
                });
                Assert.False(File.Exists(Path.Combine(throwDir, "export-current.txt")));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetExportService_ParallelBatchExport_ErrorAggregation_WritesErrorsTxtAndFormatsStatus()
        {
            var tempRoot = CreateTempDirectory("service_error_aggregation");
            try
            {
                // Empty errors produces no errors.txt
                Assert.Null(AssetExportService.WriteErrorReport(tempRoot, Array.Empty<string>(), Array.Empty<string>()));
                Assert.False(File.Exists(Path.Combine(tempRoot, "errors.txt")));

                var items = Enumerable.Range(0, 12)
                    .Select(i => new AssetItem(new AssetHandle
                    {
                        UniqueID = $"err_uid_{i}",
                        Name = $"Asset_{i:D2}",
                        Type = ClassIDType.Texture2D,
                        PathID = 9000 + i
                    }, i))
                    .ToList();

                var statusMessages = new ConcurrentQueue<string>();
                var service = new AssetExportService();
                var summary = service.ExportAssetsBatch(
                    items,
                    ExportMode.Convert,
                    tempRoot,
                    new AssetBatchExportOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        WriteErrorReportOnCompletion = true,
                        LoggedErrors = new[] { "Pre-existing load error: corrupt shader header" },
                        StatusUpdate = msg => statusMessages.Enqueue(msg),
                        CustomItemExporter = (asset, dir, ctx) =>
                        {
                            if (asset.PathID % 3 == 0)
                            {
                                throw new InvalidDataException($"Synthetic decode failure for {asset.Name}");
                            }
                            if (asset.PathID % 4 == 0)
                            {
                                return false; // skipped
                            }
                            Assert.True(ctx.TryGetAssetFilePath(dir, asset.Name, ".txt", asset, out var path));
                            AssetExportService.WriteTextExport(path, "ok");
                            return true;
                        }
                    });

                // PathIDs 9000..9011: divisible by 3 -> 9000, 9003, 9006, 9009 (4 failed)
                // Remaining divisible by 4 -> 9004, 9008 (2 skipped)
                // Remaining -> 6 exported
                Assert.Equal(6, summary.Exported);
                Assert.Equal(2, summary.Skipped);
                Assert.Equal(4, summary.Failed);
                Assert.Equal(4, summary.Errors.Count);
                Assert.NotNull(summary.ErrorReportPath);
                Assert.True(File.Exists(summary.ErrorReportPath));

                var reportContent = File.ReadAllText(summary.ErrorReportPath!, Encoding.UTF8);
                Assert.Contains("Logged errors (1)", reportContent);
                Assert.Contains("Pre-existing load error: corrupt shader header", reportContent);
                Assert.Contains("Export errors (4)", reportContent);
                Assert.Contains("Synthetic decode failure for Asset_00", reportContent);
                Assert.Contains("PathID: 9009", reportContent);

                var statusText = AssetExportService.FormatBulkExportStatusSummary(summary);
                Assert.Contains("Finished exporting 6 assets.", statusText);
                Assert.Contains("2 skipped", statusText);
                Assert.Contains("4 failed.", statusText);
                Assert.Contains("Error report: errors.txt.", statusText);
                Assert.Equal(4, statusMessages.Count);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetExportService_LazyBatchGrouping_RespectsMaxFilesAndMaxBytesAndInvokesLifecycleHooks()
        {
            var tempRoot = CreateTempDirectory("service_lazy_batches");
            try
            {
                const int sourceFiles = 9;
                const int assetsPerSource = 5;
                var items = new List<AssetItem>(sourceFiles * assetsPerSource);

                // Interleave items across 9 source files so batching must coalesce each source file cleanly
                for (int a = 0; a < assetsPerSource; a++)
                {
                    for (int s = 0; s < sourceFiles; s++)
                    {
                        items.Add(new AssetItem(new AssetHandle
                        {
                            UniqueID = $"lazy_{s}_{a}",
                            Name = $"Tex_{s}_{a}",
                            Type = ClassIDType.Texture2D,
                            OriginalPath = $@"C:\LazyGame\bundle_{s:D2}.ab",
                            SerializedFileName = $"CAB-{s:D2}",
                            PathID = s * 100 + a + 1,
                            ByteSize = 10_000
                        }, s * 100 + a));
                    }
                }

                // 1) Bound by maxFiles = 4 -> 9 sources => 3 batches (4, 4, 1)
                var fileBounded = AssetExportService.BuildLazyExportBatches(
                    items,
                    x => x.Handle?.OriginalPath,
                    maxSourceFilesPerBatch: 4,
                    maxAssetBytesPerBatch: long.MaxValue);
                Assert.Equal(3, fileBounded.Count);
                Assert.Equal(new[] { 4, 4, 1 }, fileBounded.Select(b => b.SourcePaths.Count).ToArray());
                Assert.Equal(items.Count, fileBounded.Sum(b => b.Items.Count));

                // Every source file appears in exactly one batch (never split across batches)
                var allSources = fileBounded.SelectMany(b => b.SourcePaths).ToList();
                Assert.Equal(sourceFiles, allSources.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Equal(sourceFiles, allSources.Count);

                // 2) Bound by maxBytes = 100,000 (each source = 5 * 10,000 = 50,000 bytes -> max 2 sources per batch => 5 batches)
                var byteBounded = AssetExportService.BuildLazyExportBatches(
                    items,
                    x => x.Handle?.OriginalPath,
                    maxSourceFilesPerBatch: 16,
                    maxAssetBytesPerBatch: 100_000L);
                Assert.Equal(5, byteBounded.Count);
                Assert.All(byteBounded, b => Assert.True(b.SourcePaths.Count <= 2));
                Assert.All(byteBounded, b => Assert.True(b.EstimatedAssetBytes <= 100_000L));

                // 3) Verify ExportAssetsBatch invokes EnsureLazyBatchLoaded, OnBatchLoaded, SuspendLruEviction, ReleaseLazyBatch per batch
                var loadedBatches = new List<int>();
                var releasedBatches = new List<int>();
                var lruSuspensions = 0;
                var lruResumes = 0;
                var service = new AssetExportService();

                var summary = service.ExportAssetsBatch(
                    items,
                    ExportMode.Convert,
                    tempRoot,
                    new AssetBatchExportOptions
                    {
                        IsLazyLoading = true,
                        LazySourceKeyResolver = x => x.Handle?.OriginalPath,
                        MaxSourceFilesPerBatch = 4,
                        MaxAssetBytesPerBatch = long.MaxValue,
                        SnapshotInitiallyLoadedFileNames = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CAB-00" },
                        EnsureLazyBatchLoaded = (batch, ct) => loadedBatches.Add(batch.SourcePaths.Count),
                        ReleaseLazyBatch = (batch, protectedNames) =>
                        {
                            Assert.NotNull(protectedNames);
                            Assert.Contains("CAB-00", protectedNames!);
                            releasedBatches.Add(batch.SourcePaths.Count);
                        },
                        SuspendLruEviction = () =>
                        {
                            Interlocked.Increment(ref lruSuspensions);
                            return new CallbackDisposable(() => Interlocked.Increment(ref lruResumes));
                        },
                        CustomItemExporter = (_, _, _) => true
                    });

                Assert.Equal(items.Count, summary.Exported);
                Assert.Equal(3, summary.BatchCount);
                Assert.Equal(new[] { 4, 4, 1 }, loadedBatches);
                Assert.Equal(new[] { 4, 4, 1 }, releasedBatches);
                Assert.Equal(3, lruSuspensions);
                Assert.Equal(3, lruResumes);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetExportService_UniquePathReservationAndSecurityGuards_PreventCollisionsAndTraversal()
        {
            var tempRoot = CreateTempDirectory("service_unique_and_security");
            try
            {
                // Pre-create base file and base directory to verify collision avoidance against existing filesystem entries
                var baseFile = Path.Combine(tempRoot, "Hero.fbx");
                File.WriteAllText(baseFile, "existing");
                var baseDir = Path.Combine(tempRoot, "HeroFolder");
                Directory.CreateDirectory(baseDir);

                const int workers = 400;
                var reservedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                var fileOutputs = new string[workers];
                Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
                {
                    fileOutputs[i] = AssetExportService.ReserveUniqueFilePath(baseFile, reservedFiles);
                });

                Assert.Equal(workers, fileOutputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.DoesNotContain(Path.GetFullPath(baseFile), fileOutputs, StringComparer.OrdinalIgnoreCase);

                var reservedDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                var dirOutputs = new string[workers];
                Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
                {
                    dirOutputs[i] = AssetExportService.ReserveUniqueDirectoryPath(baseDir, reservedDirs);
                });

                Assert.Equal(workers, dirOutputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.DoesNotContain(Path.GetFullPath(baseDir), dirOutputs, StringComparer.OrdinalIgnoreCase);

                // SEC-01: TryBuildSafeChildPath blocks traversal and rooted entries, allows clean nested paths
                var extractRoot = Path.Combine(tempRoot, "extract_root");
                Assert.False(AssetExportService.TryBuildSafeChildPath(extractRoot, "../../escape.bin", out _, out var reason1));
                Assert.False(string.IsNullOrEmpty(reason1));
                Assert.False(AssetExportService.TryBuildSafeChildPath(extractRoot, @"..\..\win_escape.bin", out _, out _));
                Assert.False(AssetExportService.TryBuildSafeChildPath(extractRoot, "/etc/passwd", out _, out _));
                Assert.False(AssetExportService.TryBuildSafeChildPath(extractRoot, Path.Combine(tempRoot, "rooted.bin"), out _, out _));
                Assert.False(AssetExportService.TryBuildSafeChildPath(extractRoot, "CAB-0/ .. /escape.bin", out _, out _));
                Assert.False(AssetExportService.TryBuildSafeChildPath(extractRoot, "   ", out _, out _));

                Assert.True(AssetExportService.TryBuildSafeChildPath(extractRoot, "CAB-01/Textures/CON.png", out var safeChild, out _));
                Assert.True(AssetExportService.IsPathWithinRoot(safeChild, extractRoot));
                Assert.EndsWith("_CON.png", safeChild, StringComparison.Ordinal);

                // IsPathWithinRoot rejects sibling prefix trick (e.g. extract_root_evil vs extract_root)
                Assert.False(AssetExportService.IsPathWithinRoot(extractRoot + "_evil", extractRoot));
                Assert.True(AssetExportService.IsPathWithinRoot(extractRoot, extractRoot));

                // SEC-02: SanitizeExtension strips dangerous characters and falls back cleanly
                Assert.Equal(".mp4", AssetExportService.SanitizeExtension(null, ".mp4"));
                Assert.Equal(".mp4", AssetExportService.SanitizeExtension("...///", ".mp4"));
                Assert.Equal(".ogg", AssetExportService.SanitizeExtension(".ogg", ".mp4"));
                Assert.Equal(".abc", AssetExportService.SanitizeExtension(".a/b\\c", ".txt"));

                // ShouldSkipConvertedAsset & BuildBatchExecutionPhases
                var tex = new AssetItem(new AssetHandle { Name = "T", Type = ClassIDType.Texture2D, PathID = 1 }, 0);
                var mat = new AssetItem(new AssetHandle { Name = "M", Type = ClassIDType.Material, PathID = 2 }, 1);
                var fbxMat = new AssetItem(new AssetHandle { Name = "FM", Type = ClassIDType.Material, Container = "a/b.fbx/m", PathID = 3 }, 2);
                var mesh = new AssetItem(new AssetHandle { Name = "Mesh", Type = ClassIDType.Mesh, PathID = 4 }, 3);

                var filtered = AssetExportService.FilterAndOrderAssetsForExport(new[] { mesh, fbxMat, mat, tex }, ExportMode.Convert);
                Assert.Equal(3, filtered.Count);
                Assert.Equal(ClassIDType.Texture2D, filtered[0].Type);
                Assert.Equal(ClassIDType.Material, filtered[1].Type);
                Assert.Equal(ClassIDType.Mesh, filtered[2].Type);

                var convertPhases = AssetExportService.BuildBatchExecutionPhases(new[] { mesh, mat, tex }, ExportMode.Convert);
                Assert.Equal(3, convertPhases.Count);
                Assert.Equal(ClassIDType.Texture2D, convertPhases[0][0].Type);
                Assert.Equal(ClassIDType.Material, convertPhases[1][0].Type);
                Assert.Equal(ClassIDType.Mesh, convertPhases[2][0].Type);

                var rawPhases = AssetExportService.BuildBatchExecutionPhases(new[] { mesh, mat, tex }, ExportMode.Raw);
                Assert.Single(rawPhases);
                Assert.Equal(3, rawPhases[0].Count);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetExportService_ParallelExtraction_BlocksZipSlipReportsViewModelProgressAndSupportsCancellation()
        {
            var tempRoot = CreateTempDirectory("service_extract_zipslip");
            try
            {
                var sourceDir = Path.Combine(tempRoot, "bundles");
                var outFilesDir = Path.Combine(tempRoot, "out_files");
                var outFolderDir = Path.Combine(tempRoot, "out_folder");
                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(outFilesDir);
                Directory.CreateDirectory(outFolderDir);

                const int bundleCount = 6;
                var bundlePaths = new string[bundleCount];
                var expectedPayloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                for (int b = 0; b < bundleCount; b++)
                {
                    var payload = Encoding.UTF8.GetBytes($"bundle-payload-{b}-" + new string('Z', 256));
                    var evil = Encoding.UTF8.GetBytes("must-never-be-written");
                    var bundleBytes = BuildSyntheticUnityFsV6Lz4Bundle(new[]
                    {
                        ($"CAB-{b:D2}/data_{b}.assets", payload),
                        ("../../escaped_unix.bin", evil),
                        (@"..\..\escaped_win.bin", evil),
                        ("/etc/passwd", evil),
                        ($"CAB-{b:D2}/ .. /nested_escape.bin", evil)
                    });

                    var subDir = Path.Combine(sourceDir, $"pack_{b % 2}");
                    Directory.CreateDirectory(subDir);
                    var fileName = $"b_{b:D2}.unity3d";
                    var path = Path.Combine(subDir, fileName);
                    File.WriteAllBytes(path, bundleBytes);
                    bundlePaths[b] = path;
                    expectedPayloads[Path.Combine(outFilesDir, fileName + "_unpacked", $"CAB-{b:D2}", $"data_{b}.assets")] = payload;
                }

                // 1) ExtractFiles with ExportViewModel integration
                using var vm = new ExportViewModel();
                var token = vm.BeginExport(ExportOperationMode.ExtractBundle, bundleCount, outFilesDir);
                var extractedFiles = AssetExportService.ExtractFiles(
                    bundlePaths,
                    outFilesDir,
                    statusUpdate: null,
                    progressReport: null,
                    viewModel: vm,
                    maxDegreeOfParallelism: 4,
                    cancellationToken: token);
                vm.Complete(extractedFiles);

                Assert.Equal(bundleCount, extractedFiles);
                Assert.Equal(ExportOperationStatus.Completed, vm.Status);
                Assert.Equal(100, vm.ProgressPercent);
                Assert.Equal(bundleCount, vm.SucceededCount);

                foreach (var kvp in expectedPayloads)
                {
                    Assert.True(File.Exists(kvp.Key));
                    Assert.Equal(kvp.Value, File.ReadAllBytes(kvp.Key));
                }

                Assert.Empty(Directory.GetFiles(tempRoot, "escaped_*", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(tempRoot, "nested_escape.bin", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(tempRoot, "passwd", SearchOption.AllDirectories));

                // 2) ExtractFolder preserves subfolder hierarchy & blocks Zip Slip
                var extractedFolder = AssetExportService.ExtractFolder(
                    sourceDir,
                    outFolderDir,
                    maxDegreeOfParallelism: 4);
                Assert.Equal(bundleCount, extractedFolder);
                Assert.Equal(bundleCount, Directory.GetFiles(outFolderDir, "*", SearchOption.AllDirectories).Length);

                // 3) Pre-cancelled token aborts immediately
                using var cancelledCts = new CancellationTokenSource();
                cancelledCts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                    AssetExportService.ExtractFiles(bundlePaths, Path.Combine(tempRoot, "out_cancel"), cancellationToken: cancelledCts.Token));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetExportService_WriteAssetsXml_WritesArenaAndEagerXmlAndDeletesPartialFileOnCancel()
        {
            var tempRoot = CreateTempDirectory("service_xml_export");
            try
            {
                var arena = new AssetIndexArena();
                var i0 = arena.AddRecord("cab-0", @"C:\Game\a.ab", 11, "HeroMesh", ClassIDType.Mesh, "assets/hero.fbx/HeroMesh", 0, 4096);
                var i1 = arena.AddRecord("cab-1", @"C:\Game\b.ab", 22, "HeroTex", ClassIDType.Texture2D, "assets/hero.png", 0, 8192);

                var arenaXmlPath = Path.Combine(tempRoot, "arena_assets.xml");
                var arenaCount = AssetExportService.WriteArenaAssetsXml(arena, new[] { i0, i1, -1, 999 }, arenaXmlPath);
                Assert.Equal(2, arenaCount);

                var arenaDoc = XDocument.Load(arenaXmlPath);
                var arenaNodes = arenaDoc.Root!.Elements("Asset").ToList();
                Assert.Equal(2, arenaNodes.Count);
                Assert.Equal("HeroMesh", arenaNodes[0].Element("Name")?.Value);
                Assert.Equal("HeroTex", arenaNodes[1].Element("Name")?.Value);

                var eagerItems = new[]
                {
                    new AssetItem(arena.MaterializeHandle(i0), i0),
                    new AssetItem(arena.MaterializeHandle(i1), i1)
                };
                var eagerXmlPath = Path.Combine(tempRoot, "eager_assets.xml");
                var eagerCount = AssetExportService.WriteAssetsXml(eagerItems, eagerXmlPath);
                Assert.Equal(2, eagerCount);

                var eagerDoc = XDocument.Load(eagerXmlPath);
                Assert.Equal(2, eagerDoc.Root!.Elements("Asset").Count());

                // REL-07: Cancelled XML write cleans up partial output file
                var cancelledXmlPath = Path.Combine(tempRoot, "cancelled_assets.xml");
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                    AssetExportService.WriteArenaAssetsXml(arena, new[] { i0, i1 }, cancelledXmlPath, cts.Token));
                Assert.False(File.Exists(cancelledXmlPath));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private sealed class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SynchronousProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private readonly Action _onDispose;
            public CallbackDisposable(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }

        private static T CreateUninitializedObject<T>(ClassIDType type, long pathId) where T : AssetStudio.Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            obj.type = type;
            obj.m_PathID = pathId;
            return obj;
        }

        private static byte[] BuildSyntheticUnityFsV6Lz4Bundle(IReadOnlyList<(string Path, byte[] Payload)> nodes)
        {
            using var combinedPayload = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Count);
            foreach (var node in nodes)
            {
                long offset = combinedPayload.Position;
                combinedPayload.Write(node.Payload, 0, node.Payload.Length);
                dirEntries.Add((offset, node.Payload.Length, 4u, node.Path));
            }
            var rawBlocks = combinedPayload.ToArray();
            var compressedBlocks = CompressLz4(rawBlocks);

            using var blocksInfoMs = new MemoryStream();
            blocksInfoMs.Write(new byte[16]);
            WriteInt32BE(blocksInfoMs, 1);
            WriteUInt32BE(blocksInfoMs, (uint)rawBlocks.Length);
            WriteUInt32BE(blocksInfoMs, (uint)compressedBlocks.Length);
            WriteUInt16BE(blocksInfoMs, (ushort)(StorageBlockFlags)CompressionType.Lz4);
            WriteInt32BE(blocksInfoMs, dirEntries.Count);
            foreach (var n in dirEntries)
            {
                WriteInt64BE(blocksInfoMs, n.offset);
                WriteInt64BE(blocksInfoMs, n.size);
                WriteUInt32BE(blocksInfoMs, n.flags);
                WriteNullTerminatedUtf8(blocksInfoMs, n.path);
            }

            var uncompressedBlocksInfo = blocksInfoMs.ToArray();
            var compressedBlocksInfo = CompressLz4(uncompressedBlocksInfo);
            var flags = (ArchiveFlags)CompressionType.Lz4 | ArchiveFlags.BlocksAndDirectoryInfoCombined;

            var body = new byte[compressedBlocksInfo.Length + compressedBlocks.Length];
            Buffer.BlockCopy(compressedBlocksInfo, 0, body, 0, compressedBlocksInfo.Length);
            Buffer.BlockCopy(compressedBlocks, 0, body, compressedBlocksInfo.Length, compressedBlocks.Length);

            var probe = BuildUnityFsHeader(0, (uint)compressedBlocksInfo.Length, (uint)uncompressedBlocksInfo.Length, flags, body);
            return BuildUnityFsHeader(probe.Length, (uint)compressedBlocksInfo.Length, (uint)uncompressedBlocksInfo.Length, flags, body);
        }

        private static byte[] BuildUnityFsHeader(
            long totalSize,
            uint compressedBlocksInfoSize,
            uint uncompressedBlocksInfoSize,
            ArchiveFlags flags,
            byte[] body)
        {
            using var ms = new MemoryStream();
            WriteNullTerminatedUtf8(ms, "UnityFS");
            WriteUInt32BE(ms, 6u);
            WriteNullTerminatedUtf8(ms, "5.x.x");
            WriteNullTerminatedUtf8(ms, "2022.3.10f1");
            WriteInt64BE(ms, totalSize);
            WriteUInt32BE(ms, compressedBlocksInfoSize);
            WriteUInt32BE(ms, uncompressedBlocksInfoSize);
            WriteUInt32BE(ms, (uint)flags);
            ms.Write(body, 0, body.Length);
            return ms.ToArray();
        }

        private static byte[] CompressLz4(byte[] input)
        {
            var dst = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
            int encoded = LZ4Codec.Encode(input, 0, input.Length, dst, 0, dst.Length, LZ4Level.L00_FAST);
            Assert.True(encoded > 0);
            return dst.AsSpan(0, encoded).ToArray();
        }

        private static void WriteNullTerminatedUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt16BE(Stream stream, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt32BE(Stream stream, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, value);
            stream.Write(buf);
        }

        private static string CreateTempDirectory(string suffix)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"AssetStudio_{suffix}_{Guid.NewGuid():N}");
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
    }
}
