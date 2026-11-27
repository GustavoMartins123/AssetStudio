using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    /// <summary>
    /// Execution context for a single project session activation, pairing its generation token
    /// with deterministic resource ownership and structured progress reporting.
    /// </summary>
    public sealed class ProjectSessionActivation
    {
        public ProjectSessionActivation(
            long generation,
            string projectKey,
            CancellationToken cancellationToken,
            OwnedResourceScope resources,
            IOperationProgressSink progress)
        {
            Generation = generation;
            ProjectKey = projectKey ?? string.Empty;
            CancellationToken = cancellationToken;
            Resources = resources ?? throw new ArgumentNullException(nameof(resources));
            Progress = progress ?? NullOperationProgressSink.Instance;
        }

        public long Generation { get; }
        public string ProjectKey { get; }
        public CancellationToken CancellationToken { get; }
        public OwnedResourceScope Resources { get; }
        public IOperationProgressSink Progress { get; }

        public void ReportProgress(int completed, int total, string stage, string? detail = null, long elapsedMs = 0)
        {
            Progress.Report(OperationProgressSnapshot.Create(completed, total, stage, detail ?? ProjectKey, elapsedMs));
        }
    }

    /// <summary>
    /// Pure, UI-independent coordinator for project session lifecycle transitions.
    /// Cancels prior in-flight project loads/scans via <see cref="GenerationCancellationGate"/>,
    /// owns per-session resources in an <see cref="OwnedResourceScope"/> disposed deterministically
    /// on project switch or teardown, and returns structured <see cref="OperationExecutionResult{T}"/> outcomes.
    /// </summary>
    public sealed class ProjectSessionCoordinatorService : IDisposable
    {
        private readonly object _gate = new();
        private readonly GenerationCancellationGate _sessionGate = new();
        private readonly Action<Exception>? _onResourceDisposalError;
        private OwnedResourceScope? _activeSessionScope;
        private long _activeScopeGeneration;
        private string? _activeProjectKey;
        private bool _disposed;

        public ProjectSessionCoordinatorService(Action<Exception>? onResourceDisposalError = null)
        {
            _onResourceDisposalError = onResourceDisposalError;
        }

        public long ActiveGeneration => _sessionGate.CurrentGeneration;
        public long CancelledSessionCount => _sessionGate.CancelledGenerationCount;

        public string? ActiveProjectKey
        {
            get
            {
                lock (_gate)
                {
                    return _activeProjectKey;
                }
            }
        }

        public bool HasActiveSession
        {
            get
            {
                lock (_gate)
                {
                    return _activeSessionScope != null && !_activeSessionScope.IsDisposed;
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

        public bool IsCurrentGeneration(long generation)
        {
            return _sessionGate.IsCurrent(generation);
        }

        /// <summary>
        /// Begins a new project session generation, immediately cancelling any prior in-flight session
        /// and deterministically disposing the previous session's tracked resources.
        /// </summary>
        public ProjectSessionActivation BeginSessionTransition(
            string? projectKey = null,
            IOperationProgressSink? progress = null,
            CancellationToken externalCancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var normalizedKey = projectKey?.Trim() ?? string.Empty;
            var sink = progress ?? NullOperationProgressSink.Instance;
            var nextScope = new OwnedResourceScope(_onResourceDisposalError);
            CancellationToken token;
            long generation;
            try
            {
                token = _sessionGate.BeginNext(out generation, externalCancellationToken);
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
                else if (generation > _activeScopeGeneration && _sessionGate.IsCurrent(generation))
                {
                    scopeToDispose = _activeSessionScope;
                    _activeSessionScope = nextScope;
                    _activeScopeGeneration = generation;
                    _activeProjectKey = normalizedKey;
                }
                else
                {
                    scopeToDispose = nextScope;
                }
            }

            scopeToDispose?.Dispose();

            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(ProjectSessionCoordinatorService));
            }

            return new ProjectSessionActivation(generation, normalizedKey, token, nextScope, sink);
        }

        /// <summary>
        /// Registers a disposable resource under the specified session generation.
        /// If the generation is already stale or torn down, the resource is disposed immediately to prevent leaks.
        /// </summary>
        public bool TryTrackSessionResource<T>(long generation, T? resource) where T : class, IDisposable
        {
            if (resource == null)
            {
                return false;
            }

            OwnedResourceScope? targetScope = null;
            lock (_gate)
            {
                if (!_disposed && _activeScopeGeneration == generation && _sessionGate.IsCurrent(generation))
                {
                    targetScope = _activeSessionScope;
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
        /// Registers a cleanup callback under the specified session generation.
        /// If the generation is already stale, the callback is not registered and returns <c>false</c>.
        /// </summary>
        public bool TryTrackSessionCleanup(long generation, Action? cleanupAction)
        {
            if (cleanupAction == null)
            {
                return false;
            }

            OwnedResourceScope? targetScope = null;
            lock (_gate)
            {
                if (!_disposed && _activeScopeGeneration == generation && _sessionGate.IsCurrent(generation))
                {
                    targetScope = _activeSessionScope;
                }
            }

            return targetScope != null && targetScope.TryTrackCleanup(cleanupAction);
        }

        /// <summary>
        /// Executes an asynchronous project load within a managed session generation, reporting progress
        /// and returning a structured outcome distinguishing Completed, Cancelled, and Failed states.
        /// </summary>
        public async Task<OperationExecutionResult<T>> RunSessionLoadAsync<T>(
            string projectKey,
            Func<ProjectSessionActivation, Task<T>> loadWork,
            IOperationProgressSink? progress = null,
            CancellationToken externalCancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(loadWork);

            var sw = Stopwatch.StartNew();
            var normalizedKey = projectKey?.Trim() ?? string.Empty;
            var activation = BeginSessionTransition(normalizedKey, progress, externalCancellationToken);

            try
            {
                activation.ReportProgress(0, 1, "Starting", normalizedKey, sw.ElapsedMilliseconds);
                activation.CancellationToken.ThrowIfCancellationRequested();

                var value = await loadWork(activation).ConfigureAwait(false);
                sw.Stop();

                if (activation.CancellationToken.IsCancellationRequested || !IsCurrentGeneration(activation.Generation))
                {
                    ReleaseScopeIfOwned(activation.Generation, activation.Resources);
                    return OperationExecutionResult<T>.Cancelled(
                        $"Project session '{normalizedKey}' was cancelled or superseded.",
                        sw.ElapsedMilliseconds);
                }

                activation.ReportProgress(1, 1, "Completed", normalizedKey, sw.ElapsedMilliseconds);
                return OperationExecutionResult<T>.Completed(
                    value,
                    string.IsNullOrEmpty(normalizedKey)
                        ? "Project session loaded."
                        : $"Project session '{normalizedKey}' loaded.",
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                ReleaseScopeIfOwned(activation.Generation, activation.Resources);
                return OperationExecutionResult<T>.Cancelled(
                    string.IsNullOrEmpty(normalizedKey)
                        ? "Project session cancelled."
                        : $"Project session '{normalizedKey}' cancelled.",
                    sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                ReleaseScopeIfOwned(activation.Generation, activation.Resources);
                activation.ReportProgress(0, 1, "Failed", ex.Message, sw.ElapsedMilliseconds);
                return OperationExecutionResult<T>.Failed(
                    ex,
                    string.IsNullOrEmpty(normalizedKey)
                        ? $"Project session failed: {ex.Message}"
                        : $"Project session '{normalizedKey}' failed: {ex.Message}",
                    sw.ElapsedMilliseconds,
                    code: "ProjectSessionLoadFailed");
            }
        }

        /// <summary>
        /// Cancels any active session work and deterministically disposes all resources owned by the current session.
        /// Safe to call repeatedly across project resets and window navigation.
        /// </summary>
        public void TeardownCurrentSession()
        {
            var cancelledGen = _sessionGate.CancelAllAndGetGeneration();
            OwnedResourceScope? previousScope = null;
            lock (_gate)
            {
                if (_activeScopeGeneration <= cancelledGen)
                {
                    previousScope = _activeSessionScope;
                    _activeSessionScope = null;
                    _activeScopeGeneration = 0;
                    _activeProjectKey = null;
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
                previousScope = _activeSessionScope;
                _activeSessionScope = null;
                _activeScopeGeneration = 0;
                _activeProjectKey = null;
            }

            _sessionGate.Dispose();
            previousScope?.Dispose();
        }

        private void ReleaseScopeIfOwned(long generation, OwnedResourceScope scope)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeSessionScope, scope) && _activeScopeGeneration == generation)
                {
                    _activeSessionScope = null;
                    _activeScopeGeneration = 0;
                    _activeProjectKey = null;
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
