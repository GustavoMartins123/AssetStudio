using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using Xunit;

namespace AssetStudio.Tests
{
    public class ArchitectureLifecycleAndCoordinatorTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        [Fact]
        public void OwnedResourceScope_DisposesInLifoOrderIsIdempotentAndPreventsLateLeaks()
        {
            var disposalOrder = new List<string>();
            var first = new TrackingDisposable("first", disposalOrder);
            var second = new TrackingDisposable("second", disposalOrder);
            var untracked = new TrackingDisposable("untracked", disposalOrder);
            var third = new TrackingDisposable("third", disposalOrder);

            var scope = new OwnedResourceScope();
            Assert.False(scope.IsDisposed);
            Assert.Equal(0, scope.ResourceCount);

            Assert.Same(first, scope.Track(first));
            Assert.Same(second, scope.Track(second));
            Assert.Same(untracked, scope.Track(untracked));
            Assert.True(scope.Untrack(untracked));
            Assert.False(scope.Untrack(untracked));
            Assert.True(scope.TryTrack(third));
            scope.TrackCleanup(() => disposalOrder.Add("cleanup"));

            Assert.Equal(4, scope.ResourceCount);

            scope.Dispose();
            Assert.True(scope.IsDisposed);
            Assert.Equal(0, scope.ResourceCount);
            Assert.Equal(new[] { "cleanup", "third", "second", "first" }, disposalOrder);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
            Assert.Equal(1, third.DisposeCount);
            Assert.Equal(0, untracked.DisposeCount);

            // Second dispose is a safe idempotent no-op.
            scope.Dispose();
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
            Assert.Equal(1, third.DisposeCount);

            // Tracking after disposal immediately disposes the late resource to prevent leaks.
            var lateTry = new TrackingDisposable("late-try", disposalOrder);
            Assert.False(scope.TryTrack(lateTry));
            Assert.Equal(1, lateTry.DisposeCount);

            var lateTrack = new TrackingDisposable("late-track", disposalOrder);
            Assert.Throws<ObjectDisposedException>(() => scope.Track(lateTrack));
            Assert.Equal(1, lateTrack.DisposeCount);

            untracked.Dispose();
            Assert.Equal(1, untracked.DisposeCount);
        }

        [Fact]
        public void OwnedResourceScope_DisposesAllRemainingResourcesWhenOneThrowsAndCapturesExceptions()
        {
            var disposalOrder = new List<string>();
            var callbackErrors = new List<Exception>();

            var res1 = new TrackingDisposable("r1", disposalOrder);
            var failing1 = new TrackingDisposable("fail1", disposalOrder, throwOnDispose: new InvalidOperationException("first disposal failure"));
            var res2 = new TrackingDisposable("r2", disposalOrder);
            var failing2 = new TrackingDisposable("fail2", disposalOrder, throwOnDispose: new IOException("second disposal failure"));

            using (var scope = new OwnedResourceScope(ex => callbackErrors.Add(ex)))
            {
                scope.Track(res1);
                scope.Track(failing1);
                scope.Track(res2);
                scope.Track(failing2);

                scope.Dispose();

                Assert.Equal(new[] { "fail2", "r2", "fail1", "r1" }, disposalOrder);
                Assert.Equal(1, res1.DisposeCount);
                Assert.Equal(1, failing1.DisposeCount);
                Assert.Equal(1, res2.DisposeCount);
                Assert.Equal(1, failing2.DisposeCount);

                Assert.Equal(2, scope.DisposalExceptions.Count);
                Assert.IsType<IOException>(scope.DisposalExceptions[0]);
                Assert.IsType<InvalidOperationException>(scope.DisposalExceptions[1]);
                Assert.Equal(2, callbackErrors.Count);
            }
        }

        [Fact]
        public void GenerationCancellationGate_RapidBumpsCancelPriorTokensRejectStaleAndDisposeCleanly()
        {
            var gate = new GenerationCancellationGate();
            var generations = new List<(long Generation, CancellationToken Token)>();

            for (int i = 0; i < 25; i++)
            {
                var token = gate.BeginNext(out var gen);
                generations.Add((gen, token));
                Assert.True(gate.IsCurrent(gen));
                Assert.False(token.IsCancellationRequested);

                if (i > 0)
                {
                    var previous = generations[i - 1];
                    Assert.True(previous.Token.IsCancellationRequested);
                    Assert.False(gate.IsCurrent(previous.Generation));
                }
            }

            Assert.Equal(25, gate.CurrentGeneration);
            Assert.Equal(24, gate.CancelledGenerationCount);

            var latest = generations[^1];
            Assert.True(gate.IsCurrent(latest.Generation));
            Assert.False(latest.Token.IsCancellationRequested);

            gate.CancelAll();
            Assert.True(latest.Token.IsCancellationRequested);
            Assert.False(gate.IsCurrent(latest.Generation));
            Assert.Equal(25, gate.CancelledGenerationCount);

            var postCancelToken = gate.BeginNext(out var postCancelGen);
            Assert.True(gate.IsCurrent(postCancelGen));
            Assert.False(postCancelToken.IsCancellationRequested);

            gate.Dispose();
            gate.Dispose(); // Idempotent dispose.
            Assert.True(gate.IsDisposed);
            Assert.True(postCancelToken.IsCancellationRequested);
            Assert.False(gate.IsCurrent(postCancelGen));
            Assert.Throws<ObjectDisposedException>(() => gate.BeginNext(out _));
        }

        [Fact]
        public async Task ProjectSessionCoordinatorService_HeadlessLoadRapidSwitchAndFailure_ManageLifecycleDeterministically()
        {
            using var coordinator = new ProjectSessionCoordinatorService();
            var disposalLog = new ConcurrentQueue<string>();
            var progressSnapshots = new ConcurrentQueue<OperationProgressSnapshot>();
            var progressSink = new DelegateOperationProgressSink(s => progressSnapshots.Enqueue(s));

            // 1. Successful headless load with progress reporting and active session resource ownership.
            TrackingDisposable? alphaResource = null;
            var alphaResult = await coordinator.RunSessionLoadAsync(
                "synthetic-project-alpha",
                activation =>
                {
                    alphaResource = activation.Resources.Track(new TrackingDisposable("alpha-handle", disposalLog));
                    activation.ReportProgress(1, 2, "Indexing", "synthetic-project-alpha");
                    return Task.FromResult("alpha-loaded");
                },
                progressSink);

            Assert.Equal(ExportOutcomeStatus.Completed, alphaResult.Status);
            Assert.True(alphaResult.IsCompleted);
            Assert.Equal("alpha-loaded", alphaResult.Value);
            Assert.Equal("synthetic-project-alpha", coordinator.ActiveProjectKey);
            Assert.True(coordinator.HasActiveSession);
            Assert.NotNull(alphaResource);
            Assert.Equal(0, alphaResource!.DisposeCount);
            Assert.Contains(progressSnapshots, s => s.Stage == "Starting");
            Assert.Contains(progressSnapshots, s => s.Stage == "Indexing" && Math.Abs(s.Percent - 50.0) < 0.01);
            Assert.Contains(progressSnapshots, s => s.Stage == "Completed" && Math.Abs(s.Percent - 100.0) < 0.01);

            // 2. Rapid project switch: beta starts and blocks, then gamma immediately supersedes beta.
            var betaEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackingDisposable? betaResource = null;
            TrackingDisposable? gammaResource = null;

            var betaTask = coordinator.RunSessionLoadAsync(
                "synthetic-project-beta",
                async activation =>
                {
                    betaResource = activation.Resources.Track(new TrackingDisposable("beta-handle", disposalLog));
                    betaEntered.TrySetResult(true);
                    await Task.Delay(TimeSpan.FromSeconds(10), activation.CancellationToken).ConfigureAwait(false);
                    return "beta-loaded";
                });

            await betaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Alpha's resource must already have been disposed when beta began.
            Assert.Equal(1, alphaResource!.DisposeCount);
            Assert.NotNull(betaResource);
            Assert.Equal(0, betaResource!.DisposeCount);

            var gammaResult = await coordinator.RunSessionLoadAsync(
                "synthetic-project-gamma",
                activation =>
                {
                    gammaResource = activation.Resources.Track(new TrackingDisposable("gamma-handle", disposalLog));
                    return Task.FromResult("gamma-loaded");
                });

            var betaResult = await betaTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ExportOutcomeStatus.Cancelled, betaResult.Status);
            Assert.True(betaResult.IsCancelled);
            Assert.Equal(1, betaResource!.DisposeCount);

            Assert.Equal(ExportOutcomeStatus.Completed, gammaResult.Status);
            Assert.True(gammaResult.IsCompleted);
            Assert.Equal("gamma-loaded", gammaResult.Value);
            Assert.Equal("synthetic-project-gamma", coordinator.ActiveProjectKey);
            Assert.NotNull(gammaResource);
            Assert.Equal(0, gammaResource!.DisposeCount);

            // 3. Failed load cleans up partially acquired resources and reports Failed status + diagnostic.
            TrackingDisposable? failedResource = null;
            var failedResult = await coordinator.RunSessionLoadAsync<string>(
                "synthetic-project-corrupt",
                activation =>
                {
                    failedResource = activation.Resources.Track(new TrackingDisposable("failed-handle", disposalLog));
                    throw new InvalidDataException("Synthetic corrupt bundle header");
                });

            Assert.Equal(1, gammaResource!.DisposeCount);
            Assert.Equal(ExportOutcomeStatus.Failed, failedResult.Status);
            Assert.True(failedResult.IsFailed);
            Assert.Single(failedResult.Diagnostics);
            Assert.Equal(ExportDiagnosticSeverity.Error, failedResult.Diagnostics[0].Severity);
            Assert.Contains("Synthetic corrupt bundle header", failedResult.Diagnostics[0].Message, StringComparison.Ordinal);
            Assert.NotNull(failedResource);
            Assert.Equal(1, failedResource!.DisposeCount);
            Assert.False(coordinator.HasActiveSession);

            // 4. Explicit teardown is safe and disposes any newly active session resources.
            var finalActivation = coordinator.BeginSessionTransition("synthetic-project-final");
            var finalResource = finalActivation.Resources.Track(new TrackingDisposable("final-handle", disposalLog));
            Assert.True(coordinator.IsCurrentGeneration(finalActivation.Generation));
            Assert.Equal(0, finalResource.DisposeCount);

            coordinator.TeardownCurrentSession();
            Assert.False(coordinator.IsCurrentGeneration(finalActivation.Generation));
            Assert.False(coordinator.HasActiveSession);
            Assert.Equal(1, finalResource.DisposeCount);
            Assert.True(finalActivation.CancellationToken.IsCancellationRequested);
        }

        [Fact]
        public async Task PreviewResolutionCoordinatorService_RapidSelectionBurst_CancelsStaleAndDisposesSupersededResources()
        {
            using var coordinator = new PreviewResolutionCoordinatorService();
            var trackedByIndex = new ConcurrentDictionary<int, TrackingDisposable>();
            var disposalLog = new ConcurrentQueue<string>();

            // 1. Deterministic superseding of an actively running preview disposes its tracked resource immediately.
            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackingDisposable? firstResource = null;
            var firstTask = coordinator.ResolveLatestAsync(
                "synthetic-asset-initial",
                async context =>
                {
                    firstResource = context.Resources.Track(new TrackingDisposable("initial-preview", disposalLog));
                    firstEntered.TrySetResult(true);
                    await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken).ConfigureAwait(false);
                    return "resolved:initial";
                });

            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(firstResource);
            Assert.Equal(0, firstResource!.DisposeCount);

            // 2. Rapid 12-selection burst cancels the initial preview and all 11 superseded requests; only the latest wins.
            const int burstCount = 12;
            var tasks = new Task<OperationExecutionResult<string>>[burstCount];

            for (int i = 0; i < burstCount; i++)
            {
                int index = i;
                bool isLast = index == burstCount - 1;
                string assetKey = $"synthetic-asset-{index:D2}";

                tasks[index] = coordinator.ResolveLatestAsync(
                    assetKey,
                    async context =>
                    {
                        var resource = new TrackingDisposable(assetKey, disposalLog);
                        if (context.Resources.TryTrack(resource))
                        {
                            trackedByIndex[index] = resource;
                        }

                        if (!isLast)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(500), context.CancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(15), context.CancellationToken).ConfigureAwait(false);
                        }

                        context.CancellationToken.ThrowIfCancellationRequested();
                        return $"resolved:{assetKey}";
                    },
                    debounceDelay: isLast ? TimeSpan.Zero : TimeSpan.FromMilliseconds(20));
            }

            var firstOutcome = await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExportOutcomeStatus.Cancelled, firstOutcome.Status);
            Assert.Equal(1, firstResource!.DisposeCount);

            var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

            for (int i = 0; i < burstCount - 1; i++)
            {
                Assert.Equal(ExportOutcomeStatus.Cancelled, outcomes[i].Status);
                Assert.True(outcomes[i].IsCancelled);
                Assert.Null(outcomes[i].Value);
                if (trackedByIndex.TryGetValue(i, out var staleResource))
                {
                    Assert.Equal(1, staleResource.DisposeCount);
                }
            }

            var winner = outcomes[^1];
            Assert.Equal(ExportOutcomeStatus.Completed, winner.Status);
            Assert.True(winner.IsCompleted);
            Assert.Equal($"resolved:synthetic-asset-{burstCount - 1:D2}", winner.Value);
            Assert.True(coordinator.CancelledResolutionCount >= burstCount);
            Assert.Equal($"synthetic-asset-{burstCount - 1:D2}", coordinator.ActiveAssetKey);

            Assert.True(trackedByIndex.TryGetValue(burstCount - 1, out var winningResource));
            Assert.NotNull(winningResource);
            Assert.Equal(0, winningResource!.DisposeCount);

            // Clearing active preview disposes the winning preview resource deterministically.
            coordinator.ClearActivePreview();
            Assert.Equal(1, winningResource!.DisposeCount);
            Assert.False(coordinator.HasActivePreviewScope);
            Assert.Equal(0, coordinator.ActivePreviewResourceCount);

            // Failing preview resolution reports Failed and releases partial resources.
            TrackingDisposable? failedPreviewResource = null;
            var failedPreview = await coordinator.ResolveLatestAsync<string>(
                "synthetic-asset-broken",
                context =>
                {
                    failedPreviewResource = context.Resources.Track(new TrackingDisposable("broken-preview", disposalLog));
                    throw new InvalidOperationException("Synthetic preview decode error");
                });

            Assert.Equal(ExportOutcomeStatus.Failed, failedPreview.Status);
            Assert.True(failedPreview.IsFailed);
            Assert.Single(failedPreview.Diagnostics);
            Assert.Contains("Synthetic preview decode error", failedPreview.Diagnostics[0].Message, StringComparison.Ordinal);
            Assert.NotNull(failedPreviewResource);
            Assert.Equal(1, failedPreviewResource!.DisposeCount);
        }

        [Fact]
        public void OperationLifecycleContracts_ProgressClampingAndResultOutcomes_BehavePredictably()
        {
            var zeroTotal = OperationProgressSnapshot.Create(completed: 5, total: 0, stage: "Scanning");
            Assert.Equal(0.0, zeroTotal.Percent);
            Assert.Equal("Scanning", zeroTotal.Stage);

            var clampedHigh = OperationProgressSnapshot.Create(completed: 150, total: 100, stage: "Exporting", detail: "mesh", elapsedMs: -5);
            Assert.Equal(100.0, clampedHigh.Percent);
            Assert.Equal(0L, clampedHigh.ElapsedMs);
            Assert.Equal("mesh", clampedHigh.Detail);

            NullOperationProgressSink.Instance.Report(clampedHigh);

            var partial = OperationExecutionResult<int>.Partial(42, summary: "Partial success", elapsedMs: 12);
            Assert.Equal(ExportOutcomeStatus.Partial, partial.Status);
            Assert.True(partial.IsPartial);
            Assert.True(partial.IsSucceeded);
            Assert.False(partial.IsCompleted);
            Assert.False(partial.IsFailed);
            Assert.False(partial.IsCancelled);
            Assert.Equal(42, partial.Value);
        }

        [Fact]
        public void CoordinatorServicesAndModularizedCorePartials_ContainZeroAvaloniaImports()
        {
            var repoRoot = LocateRepositoryRoot();
            var filesToAudit = new[]
            {
                Path.Combine(repoRoot, "AssetStudioUtility", "OperationLifecycle.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "ProjectSessionCoordinatorService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "PreviewResolutionCoordinatorService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "UnityProjectExporterService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "UnityProjectExporterService.Yaml.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "SemanticGraphService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "SemanticGraphService.Materials.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "SemanticGraphService.Animations.cs")
            };

            foreach (var file in filesToAudit)
            {
                Assert.True(File.Exists(file), $"Expected source file not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(
                    AvaloniaUsingRegex.IsMatch(content),
                    $"Pure service/utility file '{Path.GetRelativePath(repoRoot, file)}' must not import Avalonia.");
            }

            // Reflection audit: neither coordinator service exposes Avalonia types in any field or property.
            var serviceTypes = new[]
            {
                typeof(ProjectSessionCoordinatorService),
                typeof(ProjectSessionActivation),
                typeof(PreviewResolutionCoordinatorService),
                typeof(PreviewResolutionContext)
            };

            foreach (var type in serviceTypes)
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNotAvaloniaType(prop.PropertyType, $"{type.Name}.{prop.Name}");
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNotAvaloniaType(field.FieldType, $"{type.Name}.{field.Name}");
                }
            }
        }

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }

        private static void AssertNotAvaloniaType(Type type, string memberName)
        {
            var ns = type.Namespace ?? string.Empty;
            Assert.False(
                string.Equals(ns, "Avalonia", StringComparison.Ordinal) || ns.StartsWith("Avalonia.", StringComparison.Ordinal),
                $"Service member '{memberName}' must not reference Avalonia type '{type.FullName}'.");

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    AssertNotAvaloniaType(arg, memberName);
                }
            }
        }

        private sealed class TrackingDisposable : IDisposable
        {
            private readonly string _name;
            private readonly List<string>? _orderedLog;
            private readonly ConcurrentQueue<string>? _concurrentLog;
            private readonly Exception? _throwOnDispose;
            private int _disposeCount;

            public TrackingDisposable(string name, List<string> orderedLog, Exception? throwOnDispose = null)
            {
                _name = name;
                _orderedLog = orderedLog;
                _throwOnDispose = throwOnDispose;
            }

            public TrackingDisposable(string name, ConcurrentQueue<string> concurrentLog, Exception? throwOnDispose = null)
            {
                _name = name;
                _concurrentLog = concurrentLog;
                _throwOnDispose = throwOnDispose;
            }

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                _orderedLog?.Add(_name);
                _concurrentLog?.Enqueue(_name);
                if (_throwOnDispose != null)
                {
                    throw _throwOnDispose;
                }
            }
        }
    }
}
