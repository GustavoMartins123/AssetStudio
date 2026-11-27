using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    /// <summary>
    /// Execution context for a single preview resolution request, pairing its generation token
    /// with an <see cref="OwnedResourceScope"/> that is disposed immediately when a newer preview supersedes it.
    /// </summary>
    public sealed class PreviewResolutionContext
    {
        public PreviewResolutionContext(
            long generation,
            string assetKey,
            CancellationToken cancellationToken,
            OwnedResourceScope resources,
            IOperationProgressSink progress)
        {
            Generation = generation;
            AssetKey = assetKey ?? string.Empty;
            CancellationToken = cancellationToken;
            Resources = resources ?? throw new ArgumentNullException(nameof(resources));
            Progress = progress ?? NullOperationProgressSink.Instance;
        }

        public long Generation { get; }
        public string AssetKey { get; }
        public CancellationToken CancellationToken { get; }
        public OwnedResourceScope Resources { get; }
        public IOperationProgressSink Progress { get; }

        public void ReportProgress(int completed, int total, string stage, string? detail = null, long elapsedMs = 0)
        {
            Progress.Report(OperationProgressSnapshot.Create(completed, total, stage, detail ?? AssetKey, elapsedMs));
        }
    }

    /// <summary>
    /// Pure, UI-independent coordinator for asynchronous preview asset resolution and rapid-selection superseding.
    /// Uses <see cref="GenerationCancellationGate"/> to cancel superseded resolutions and immediately disposes
    /// superseded preview resources via <see cref="OwnedResourceScope"/> when a newer selection arrives or preview is cleared.
    /// </summary>
    public sealed class PreviewResolutionCoordinatorService : IDisposable
    {
        private readonly object _gate = new();
        private readonly GenerationCancellationGate _resolutionGate = new();
        private readonly Action<Exception>? _onResourceDisposalError;
        private OwnedResourceScope? _activePreviewScope;
        private long _activeScopeGeneration;
        private string? _activeAssetKey;
        private bool _disposed;

        public PreviewResolutionCoordinatorService(Action<Exception>? onResourceDisposalError = null)
        {
            _onResourceDisposalError = onResourceDisposalError;
        }

        public long CurrentGeneration => _resolutionGate.CurrentGeneration;
        public long CancelledResolutionCount => _resolutionGate.CancelledGenerationCount;

        public string? ActiveAssetKey
        {
            get
            {
                lock (_gate)
                {
                    return _activeAssetKey;
                }
            }
        }

        public bool HasActivePreviewScope
        {
            get
            {
                lock (_gate)
                {
                    return _activePreviewScope != null && !_activePreviewScope.IsDisposed;
                }
            }
        }

        public int ActivePreviewResourceCount
        {
            get
            {
                lock (_gate)
                {
                    return _activePreviewScope != null && !_activePreviewScope.IsDisposed
                        ? _activePreviewScope.ResourceCount
                        : 0;
                }
            }
        }

        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public bool IsCurrent(long generation)
        {
            return _resolutionGate.IsCurrent(generation);
        }

        /// <summary>
        /// Starts a new preview generation, cancelling any in-flight resolution and immediately disposing
        /// any resources owned by the superseded preview.
        /// </summary>
        public PreviewResolutionContext BeginPreviewRequest(
            string? assetKey = null,
            IOperationProgressSink? progress = null,
            CancellationToken externalCancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var normalizedKey = assetKey?.Trim() ?? string.Empty;
            var sink = progress ?? NullOperationProgressSink.Instance;
            var nextScope = new OwnedResourceScope(_onResourceDisposalError);
            CancellationToken token;
            long generation;
            try
            {
                token = _resolutionGate.BeginNext(out generation, externalCancellationToken);
            }
            catch
            {
                nextScope.Dispose();
                throw;
            }

            OwnedResourceScope? scopeToDispose;
            lock (_gate)
            {
                if (_disposed)
                {
                    scopeToDispose = nextScope;
                }
                else if (generation > _activeScopeGeneration && _resolutionGate.IsCurrent(generation))
                {
                    scopeToDispose = _activePreviewScope;
                    _activePreviewScope = nextScope;
                    _activeScopeGeneration = generation;
                    _activeAssetKey = normalizedKey;
                }
                else
                {
                    scopeToDispose = nextScope;
                }
            }

            scopeToDispose?.Dispose();

            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(PreviewResolutionCoordinatorService));
            }

            return new PreviewResolutionContext(generation, normalizedKey, token, nextScope, sink);
        }

        /// <summary>
        /// Tracks a disposable resource against the specified preview generation.
        /// If the generation is stale or cleared, disposes the resource immediately so handles never leak.
        /// </summary>
        public bool TryTrackActivePreviewResource<T>(long generation, T? resource) where T : class, IDisposable
        {
            if (resource == null)
            {
                return false;
            }

            OwnedResourceScope? targetScope = null;
            lock (_gate)
            {
                if (!_disposed && _activeScopeGeneration == generation && _resolutionGate.IsCurrent(generation))
                {
                    targetScope = _activePreviewScope;
                }
            }

            if (targetScope != null && targetScope.TryTrack(resource))
            {
                return true;
            }

            DisposeUntrackedSafely(resource);
            return false;
        }

        /// <summary>
        /// Tracks a cleanup callback against the specified preview generation.
        /// </summary>
        public bool TryTrackActivePreviewCleanup(long generation, Action? cleanupAction)
        {
            if (cleanupAction == null)
            {
                return false;
            }

            OwnedResourceScope? targetScope = null;
            lock (_gate)
            {
                if (!_disposed && _activeScopeGeneration == generation && _resolutionGate.IsCurrent(generation))
                {
                    targetScope = _activePreviewScope;
                }
            }

            return targetScope != null && targetScope.TryTrackCleanup(cleanupAction);
        }

        /// <summary>
        /// Resolves the latest preview selection asynchronously with optional debounce delay.
        /// Any superseded or cancelled request returns <see cref="ExportOutcomeStatus.Cancelled"/> and releases
        /// its tracked resources deterministically; only the latest active selection completes.
        /// </summary>
        public async Task<OperationExecutionResult<T>> ResolveLatestAsync<T>(
            string assetKey,
            Func<PreviewResolutionContext, Task<T>> resolveWork,
            TimeSpan debounceDelay = default,
            IOperationProgressSink? progress = null,
            CancellationToken externalCancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resolveWork);

            var sw = Stopwatch.StartNew();
            var normalizedKey = assetKey?.Trim() ?? string.Empty;
            var context = BeginPreviewRequest(normalizedKey, progress, externalCancellationToken);

            try
            {
                if (debounceDelay > TimeSpan.Zero)
                {
                    context.ReportProgress(0, 2, "Debouncing", normalizedKey, sw.ElapsedMilliseconds);
                    await Task.Delay(debounceDelay, context.CancellationToken).ConfigureAwait(false);
                }

                context.CancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrent(context.Generation))
                {
                    ReleaseScopeIfOwned(context.Generation, context.Resources);
                    sw.Stop();
                    return OperationExecutionResult<T>.Cancelled(
                        $"Preview resolution for '{normalizedKey}' was superseded.",
                        sw.ElapsedMilliseconds);
                }

                context.ReportProgress(1, 2, "Resolving", normalizedKey, sw.ElapsedMilliseconds);
                var value = await resolveWork(context).ConfigureAwait(false);
                sw.Stop();

                if (context.CancellationToken.IsCancellationRequested || !IsCurrent(context.Generation))
                {
                    ReleaseScopeIfOwned(context.Generation, context.Resources);
                    return OperationExecutionResult<T>.Cancelled(
                        $"Preview resolution for '{normalizedKey}' was superseded or cancelled.",
                        sw.ElapsedMilliseconds);
                }

                context.ReportProgress(2, 2, "Completed", normalizedKey, sw.ElapsedMilliseconds);
                return OperationExecutionResult<T>.Completed(
                    value,
                    string.IsNullOrEmpty(normalizedKey)
                        ? "Preview resolved."
                        : $"Preview resolved for '{normalizedKey}'.",
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                ReleaseScopeIfOwned(context.Generation, context.Resources);
                return OperationExecutionResult<T>.Cancelled(
                    string.IsNullOrEmpty(normalizedKey)
                        ? "Preview resolution cancelled."
                        : $"Preview resolution for '{normalizedKey}' cancelled.",
                    sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                ReleaseScopeIfOwned(context.Generation, context.Resources);
                context.ReportProgress(0, 2, "Failed", ex.Message, sw.ElapsedMilliseconds);
                return OperationExecutionResult<T>.Failed(
                    ex,
                    string.IsNullOrEmpty(normalizedKey)
                        ? $"Preview resolution failed: {ex.Message}"
                        : $"Preview resolution failed for '{normalizedKey}': {ex.Message}",
                    sw.ElapsedMilliseconds,
                    code: "PreviewResolutionFailed");
            }
        }

        /// <summary>
        /// Cancels any in-flight preview resolution and immediately disposes active preview resources.
        /// </summary>
        public void ClearActivePreview()
        {
            var cancelledGen = _resolutionGate.CancelAllAndGetGeneration();
            OwnedResourceScope? previousScope = null;
            lock (_gate)
            {
                if (_activeScopeGeneration <= cancelledGen)
                {
                    previousScope = _activePreviewScope;
                    _activePreviewScope = null;
                    _activeScopeGeneration = 0;
                    _activeAssetKey = null;
                }
            }

            previousScope?.Dispose();
        }

        public void Dispose()
        {
            OwnedResourceScope? previousScope;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                previousScope = _activePreviewScope;
                _activePreviewScope = null;
                _activeScopeGeneration = 0;
                _activeAssetKey = null;
            }

            _resolutionGate.Dispose();
            previousScope?.Dispose();
        }

        private void ReleaseScopeIfOwned(long generation, OwnedResourceScope scope)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activePreviewScope, scope) && _activeScopeGeneration == generation)
                {
                    _activePreviewScope = null;
                    _activeScopeGeneration = 0;
                    _activeAssetKey = null;
                }
            }

            scope.Dispose();
        }

        private void DisposeUntrackedSafely(IDisposable resource)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception ex)
            {
                if (_onResourceDisposalError != null)
                {
                    try
                    {
                        _onResourceDisposalError(ex);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
