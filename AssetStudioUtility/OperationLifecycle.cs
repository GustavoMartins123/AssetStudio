using System;
using System.Collections.Generic;
using System.Threading;

namespace AssetStudio
{
    /// <summary>
    /// Immutable progress snapshot emitted by long-running load, indexing, preview, and export operations
    /// across both GUI and CLI hosts.
    /// </summary>
    public sealed class OperationProgressSnapshot
    {
        public int Completed { get; init; }
        public int Total { get; init; }
        public double Percent { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public long ElapsedMs { get; init; }

        public static OperationProgressSnapshot Create(
            int completed,
            int total,
            string stage,
            string? detail = null,
            long elapsedMs = 0)
        {
            var safeCompleted = Math.Max(0, completed);
            var safeTotal = Math.Max(0, total);
            var percent = safeTotal > 0
                ? Math.Clamp((double)safeCompleted / safeTotal * 100.0, 0.0, 100.0)
                : 0.0;

            return new OperationProgressSnapshot
            {
                Completed = safeCompleted,
                Total = safeTotal,
                Percent = percent,
                Stage = stage ?? string.Empty,
                Detail = detail ?? string.Empty,
                ElapsedMs = Math.Max(0L, elapsedMs)
            };
        }
    }

    /// <summary>
    /// Sink receiving structured <see cref="OperationProgressSnapshot"/> updates without UI framework coupling.
    /// </summary>
    public interface IOperationProgressSink
    {
        void Report(OperationProgressSnapshot snapshot);
    }

    /// <summary>
    /// No-op <see cref="IOperationProgressSink"/> used when callers do not require progress notifications.
    /// </summary>
    public sealed class NullOperationProgressSink : IOperationProgressSink
    {
        public static NullOperationProgressSink Instance { get; } = new();

        private NullOperationProgressSink()
        {
        }

        public void Report(OperationProgressSnapshot snapshot)
        {
        }
    }

    /// <summary>
    /// Delegate-backed <see cref="IOperationProgressSink"/> forwarding snapshots to a callback.
    /// </summary>
    public sealed class DelegateOperationProgressSink : IOperationProgressSink
    {
        private readonly Action<OperationProgressSnapshot> _callback;

        public DelegateOperationProgressSink(Action<OperationProgressSnapshot> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _callback = callback;
        }

        public void Report(OperationProgressSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _callback(snapshot);
        }
    }

    /// <summary>
    /// Structured operation outcome explicitly distinguishing completed, partial, failed, and cancelled states
    /// along with optional value, diagnostics, human-readable summary, and elapsed milliseconds.
    /// </summary>
    public sealed class OperationExecutionResult<T>
    {
        public ExportOutcomeStatus Status { get; init; } = ExportOutcomeStatus.Completed;
        public T? Value { get; init; }
        public IReadOnlyList<ExportDiagnostic> Diagnostics { get; init; } = Array.Empty<ExportDiagnostic>();
        public string Summary { get; init; } = string.Empty;
        public long ElapsedMs { get; init; }

        public bool IsCompleted => Status == ExportOutcomeStatus.Completed;
        public bool IsPartial => Status == ExportOutcomeStatus.Partial;
        public bool IsSucceeded => Status is ExportOutcomeStatus.Completed or ExportOutcomeStatus.Partial;
        public bool IsCancelled => Status == ExportOutcomeStatus.Cancelled;
        public bool IsFailed => Status == ExportOutcomeStatus.Failed;

        public static OperationExecutionResult<T> Completed(
            T? value,
            string summary = "",
            long elapsedMs = 0,
            IReadOnlyList<ExportDiagnostic>? diagnostics = null)
        {
            return new OperationExecutionResult<T>
            {
                Status = ExportOutcomeStatus.Completed,
                Value = value,
                Summary = summary ?? string.Empty,
                ElapsedMs = Math.Max(0L, elapsedMs),
                Diagnostics = diagnostics ?? Array.Empty<ExportDiagnostic>()
            };
        }

        public static OperationExecutionResult<T> Partial(
            T? value,
            string summary = "",
            long elapsedMs = 0,
            IReadOnlyList<ExportDiagnostic>? diagnostics = null)
        {
            return new OperationExecutionResult<T>
            {
                Status = ExportOutcomeStatus.Partial,
                Value = value,
                Summary = summary ?? string.Empty,
                ElapsedMs = Math.Max(0L, elapsedMs),
                Diagnostics = diagnostics ?? Array.Empty<ExportDiagnostic>()
            };
        }

        public static OperationExecutionResult<T> Cancelled(
            string summary = "Operation cancelled.",
            long elapsedMs = 0,
            IReadOnlyList<ExportDiagnostic>? diagnostics = null)
        {
            return new OperationExecutionResult<T>
            {
                Status = ExportOutcomeStatus.Cancelled,
                Value = default,
                Summary = summary ?? string.Empty,
                ElapsedMs = Math.Max(0L, elapsedMs),
                Diagnostics = diagnostics ?? Array.Empty<ExportDiagnostic>()
            };
        }

        public static OperationExecutionResult<T> Failed(
            string summary,
            long elapsedMs = 0,
            IReadOnlyList<ExportDiagnostic>? diagnostics = null)
        {
            return new OperationExecutionResult<T>
            {
                Status = ExportOutcomeStatus.Failed,
                Value = default,
                Summary = summary ?? string.Empty,
                ElapsedMs = Math.Max(0L, elapsedMs),
                Diagnostics = diagnostics ?? Array.Empty<ExportDiagnostic>()
            };
        }

        public static OperationExecutionResult<T> Failed(
            Exception exception,
            string? summary = null,
            long elapsedMs = 0,
            string code = "OperationFailed")
        {
            ArgumentNullException.ThrowIfNull(exception);

            var diagnostic = new ExportDiagnostic
            {
                Severity = ExportDiagnosticSeverity.Error,
                Outcome = ExportItemOutcome.Failed,
                Code = string.IsNullOrWhiteSpace(code) ? "OperationFailed" : code,
                Message = exception.Message
            };

            return new OperationExecutionResult<T>
            {
                Status = ExportOutcomeStatus.Failed,
                Value = default,
                Summary = string.IsNullOrWhiteSpace(summary) ? exception.Message : summary,
                ElapsedMs = Math.Max(0L, elapsedMs),
                Diagnostics = new[] { diagnostic }
            };
        }
    }

    /// <summary>
    /// Thread-safe deterministic resource ownership container that disposes tracked streams, file handles,
    /// native wrappers, and cleanup callbacks in LIFO order. Disposal is idempotent and captures disposal
    /// exceptions so a single failing resource never prevents remaining resources from being released or
    /// masks a primary operation exception.
    /// </summary>
    public sealed class OwnedResourceScope : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IDisposable> _resources = new();
        private readonly Action<Exception>? _onDisposalError;
        private Exception[] _disposalExceptions = Array.Empty<Exception>();
        private bool _disposed;

        public OwnedResourceScope(Action<Exception>? onDisposalError = null)
        {
            _onDisposalError = onDisposalError;
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

        public int ResourceCount
        {
            get
            {
                lock (_gate)
                {
                    return _resources.Count;
                }
            }
        }

        public IReadOnlyList<Exception> DisposalExceptions
        {
            get
            {
                lock (_gate)
                {
                    return _disposalExceptions;
                }
            }
        }

        public T Track<T>(T resource) where T : class, IDisposable
        {
            ArgumentNullException.ThrowIfNull(resource);

            if (!TryTrack(resource))
            {
                throw new ObjectDisposedException(nameof(OwnedResourceScope));
            }

            return resource;
        }

        public bool TryTrack<T>(T? resource) where T : class, IDisposable
        {
            if (resource == null)
            {
                return false;
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _resources.Add(resource);
                    return true;
                }
            }

            DisposeSingleSafely(resource, out var error);
            if (error != null)
            {
                RecordDisposalException(error);
            }

            return false;
        }

        public void TrackDisposable(IDisposable? resource)
        {
            if (resource == null)
            {
                return;
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _resources.Add(resource);
                    return;
                }
            }

            DisposeSingleSafely(resource, out var error);
            if (error != null)
            {
                RecordDisposalException(error);
            }

            throw new ObjectDisposedException(nameof(OwnedResourceScope));
        }

        public void TrackCleanup(Action? cleanupAction)
        {
            if (cleanupAction == null)
            {
                return;
            }

            TrackDisposable(new CallbackDisposable(cleanupAction));
        }

        public bool TryTrackCleanup(Action? cleanupAction)
        {
            if (cleanupAction == null)
            {
                return false;
            }

            return TryTrack(new CallbackDisposable(cleanupAction));
        }

        public bool Untrack(IDisposable? resource)
        {
            if (resource == null)
            {
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                for (int i = _resources.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_resources[i], resource))
                    {
                        _resources.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
        }

        public void Dispose()
        {
            IDisposable[] snapshot;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                snapshot = _resources.Count == 0 ? Array.Empty<IDisposable>() : _resources.ToArray();
                _resources.Clear();
            }

            List<Exception>? errors = null;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                DisposeSingleSafely(snapshot[i], out var error);
                if (error != null)
                {
                    errors ??= new List<Exception>();
                    errors.Add(error);
                }
            }

            if (errors != null)
            {
                lock (_gate)
                {
                    if (_disposalExceptions.Length == 0)
                    {
                        _disposalExceptions = errors.ToArray();
                    }
                    else
                    {
                        var combined = new Exception[_disposalExceptions.Length + errors.Count];
                        Array.Copy(_disposalExceptions, combined, _disposalExceptions.Length);
                        errors.CopyTo(combined, _disposalExceptions.Length);
                        _disposalExceptions = combined;
                    }
                }
            }
        }

        private void DisposeSingleSafely(IDisposable resource, out Exception? error)
        {
            error = null;
            try
            {
                resource.Dispose();
            }
            catch (Exception ex)
            {
                error = ex;
                if (_onDisposalError != null)
                {
                    try
                    {
                        _onDisposalError(ex);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void RecordDisposalException(Exception error)
        {
            lock (_gate)
            {
                var updated = new Exception[_disposalExceptions.Length + 1];
                if (_disposalExceptions.Length > 0)
                {
                    Array.Copy(_disposalExceptions, updated, _disposalExceptions.Length);
                }

                updated[^1] = error;
                _disposalExceptions = updated;
            }
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private Action? _cleanup;

            public CallbackDisposable(Action cleanup)
            {
                _cleanup = cleanup;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _cleanup, null)?.Invoke();
            }
        }
    }

    /// <summary>
    /// Thread-safe generation-numbered cancellation gate for rapid project and preview transitions.
    /// Starting a newer generation atomically cancels and disposes the prior <see cref="CancellationTokenSource"/>
    /// without leaking wait handles or holding locks while invoking cancellation callbacks.
    /// </summary>
    public sealed class GenerationCancellationGate : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _activeCts;
        private long _currentGeneration;
        private long _cancelledGenerationCount;
        private bool _disposed;

        public long CurrentGeneration => Interlocked.Read(ref _currentGeneration);
        public long CancelledGenerationCount => Interlocked.Read(ref _cancelledGenerationCount);

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

        public CancellationToken BeginNext(out long generation)
        {
            return BeginNext(out generation, CancellationToken.None);
        }

        public CancellationToken BeginNext(out long generation, CancellationToken linkedToken)
        {
            CancellationTokenSource? previousCts;
            CancellationToken token;

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                previousCts = _activeCts;
                var nextCts = linkedToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(linkedToken)
                    : new CancellationTokenSource();
                _activeCts = nextCts;
                generation = Interlocked.Increment(ref _currentGeneration);
                token = nextCts.Token;
            }

            CancelAndDisposeSafely(previousCts);
            return token;
        }

        public bool IsCurrent(long generation)
        {
            return generation > 0 && Interlocked.Read(ref _currentGeneration) == generation;
        }

        public void CancelAll()
        {
            CancelAllAndGetGeneration();
        }

        public long CancelAllAndGetGeneration()
        {
            CancellationTokenSource? previousCts;
            long generation;
            lock (_gate)
            {
                generation = Interlocked.Increment(ref _currentGeneration);
                previousCts = _activeCts;
                _activeCts = null;
            }

            CancelAndDisposeSafely(previousCts);
            return generation;
        }

        public void Dispose()
        {
            CancellationTokenSource? previousCts;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Interlocked.Increment(ref _currentGeneration);
                previousCts = _activeCts;
                _activeCts = null;
            }

            CancelAndDisposeSafely(previousCts);
        }

        private void CancelAndDisposeSafely(CancellationTokenSource? cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                if (!cts.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _cancelledGenerationCount);
                    cts.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException)
            {
            }
            finally
            {
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
