using System;
using System.Threading;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum ExportOperationMode
    {
        None = 0,
        Convert = 1,
        Raw = 2,
        Dump = 3,
        SplitObjects = 4,
        Animator = 5,
        ExtractBundle = 6,
        XmlList = 7,
    }

    public enum ExportOperationStatus
    {
        Idle = 0,
        Running = 1,
        Cancelling = 2,
        Completed = 3,
        Cancelled = 4,
        Failed = 5,
    }

    public sealed class ExportViewModel : ViewModelBase, IDisposable
    {
        public const int DefaultProgressThrottleMs = 50;

        private readonly Action<Action> _dispatchToUi;
        private readonly int _progressThrottleMs;
        private readonly Func<long> _clockTicksProvider;
        private readonly object _lifecycleLock = new();

        private CancellationTokenSource? _activeCts;
        private long _operationGeneration;
        private int _currentTotalCount;
        private int _lastReportedPercent = -1;
        private long _lastProgressDispatchTicks;
        private int _progressDispatchCount;
        private int _uiMaxCompletedCount;
        private ExportOperationMode _lastStartedMode = ExportOperationMode.None;
        private bool _disposed;

        private bool _isExporting;
        private bool _isExtracting;
        private bool _isBusy;
        private bool _canCancel;
        private bool _isCancellationRequested;
        private int _progressPercent;
        private int _completedCount;
        private int _totalCount;
        private int _succeededCount;
        private int _skippedCount;
        private int _failedCount;
        private string _currentItemName = string.Empty;
        private string _statusText = string.Empty;
        private string _lastExportFolder = string.Empty;
        private string? _lastErrorLogPath;
        private ExportOperationMode _activeMode = ExportOperationMode.None;
        private ExportOperationStatus _status = ExportOperationStatus.Idle;

        public ExportViewModel(
            Action<Action>? dispatchToUi = null,
            int progressThrottleMs = DefaultProgressThrottleMs,
            Func<long>? clockTicksProvider = null)
        {
            _dispatchToUi = dispatchToUi ?? (static action => action());
            _progressThrottleMs = Math.Max(0, progressThrottleMs);
            _clockTicksProvider = clockTicksProvider ?? (static () => Environment.TickCount64);
            _lastProgressDispatchTicks = _clockTicksProvider() - _progressThrottleMs - 1;
        }

        public bool IsExporting
        {
            get => _isExporting;
            private set => SetProperty(ref _isExporting, value);
        }

        public bool IsExtracting
        {
            get => _isExtracting;
            private set => SetProperty(ref _isExtracting, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        public bool CanCancel
        {
            get => _canCancel;
            private set => SetProperty(ref _canCancel, value);
        }

        public bool IsCancellationRequested
        {
            get => _isCancellationRequested;
            private set => SetProperty(ref _isCancellationRequested, value);
        }

        public int ProgressPercent
        {
            get => _progressPercent;
            private set => SetProperty(ref _progressPercent, value);
        }

        public int CompletedCount
        {
            get => _completedCount;
            private set => SetProperty(ref _completedCount, value);
        }

        public int TotalCount
        {
            get => _totalCount;
            private set => SetProperty(ref _totalCount, value);
        }

        public int SucceededCount
        {
            get => _succeededCount;
            private set => SetProperty(ref _succeededCount, value);
        }

        public int SkippedCount
        {
            get => _skippedCount;
            private set => SetProperty(ref _skippedCount, value);
        }

        public int FailedCount
        {
            get => _failedCount;
            private set => SetProperty(ref _failedCount, value);
        }

        public string CurrentItemName
        {
            get => _currentItemName;
            private set => SetProperty(ref _currentItemName, value ?? string.Empty);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value ?? string.Empty);
        }

        public string LastExportFolder
        {
            get => _lastExportFolder;
            private set => SetProperty(ref _lastExportFolder, value ?? string.Empty);
        }

        public string? LastErrorLogPath
        {
            get => _lastErrorLogPath;
            private set => SetProperty(ref _lastErrorLogPath, string.IsNullOrWhiteSpace(value) ? null : value);
        }

        public ExportOperationMode ActiveMode
        {
            get => _activeMode;
            private set => SetProperty(ref _activeMode, value);
        }

        public ExportOperationStatus Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public int ProgressDispatchCount => Volatile.Read(ref _progressDispatchCount);

        public static ExportOperationMode FromExportMode(ExportMode mode)
        {
            return mode switch
            {
                ExportMode.Convert => ExportOperationMode.Convert,
                ExportMode.Raw => ExportOperationMode.Raw,
                ExportMode.Dump => ExportOperationMode.Dump,
                _ => ExportOperationMode.Convert,
            };
        }

        public CancellationToken BeginExport(
            ExportMode mode,
            int totalCount,
            string? targetFolder = null,
            string? initialStatusText = null)
        {
            return BeginExport(FromExportMode(mode), totalCount, targetFolder, initialStatusText);
        }

        public CancellationToken BeginExport(
            ExportOperationMode mode,
            int totalCount,
            string? targetFolder = null,
            string? initialStatusText = null)
        {
            if (mode == ExportOperationMode.None)
            {
                throw new ArgumentException("Export operation mode cannot be None.", nameof(mode));
            }

            CancellationTokenSource newCts;
            CancellationTokenSource? previousCts;
            long generation;
            var safeTotal = Math.Max(0, totalCount);

            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                previousCts = _activeCts;
                newCts = new CancellationTokenSource();
                _activeCts = newCts;
                _lastStartedMode = mode;

                generation = Interlocked.Increment(ref _operationGeneration);
                Volatile.Write(ref _currentTotalCount, safeTotal);
                Volatile.Write(ref _lastReportedPercent, -1);
                Interlocked.Exchange(ref _lastProgressDispatchTicks, _clockTicksProvider() - _progressThrottleMs - 1);
                Interlocked.Exchange(ref _progressDispatchCount, 0);
                Volatile.Write(ref _uiMaxCompletedCount, 0);
            }

            CancelAndDisposeQuietly(previousCts);

            var isExtracting = mode == ExportOperationMode.ExtractBundle;
            var isExporting = !isExtracting;
            var resolvedStatus = !string.IsNullOrWhiteSpace(initialStatusText)
                ? initialStatusText.Trim()
                : BuildStartingStatusText(mode, safeTotal);
            var cleanFolder = string.IsNullOrWhiteSpace(targetFolder) ? null : targetFolder;

            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                ActiveMode = mode;
                Status = ExportOperationStatus.Running;
                IsExporting = isExporting;
                IsExtracting = isExtracting;
                IsBusy = true;
                CanCancel = true;
                IsCancellationRequested = false;
                ProgressPercent = 0;
                CompletedCount = 0;
                TotalCount = safeTotal;
                SucceededCount = 0;
                SkippedCount = 0;
                FailedCount = 0;
                CurrentItemName = string.Empty;
                LastErrorLogPath = null;
                if (cleanFolder != null)
                {
                    LastExportFolder = cleanFolder;
                }
                StatusText = resolvedStatus;
            });

            return newCts.Token;
        }

        public void RequestCancel()
        {
            CancellationTokenSource? ctsToCancel = null;
            ExportOperationMode mode;
            long generation;

            lock (_lifecycleLock)
            {
                if (_activeCts == null || _activeCts.IsCancellationRequested)
                {
                    return;
                }

                ctsToCancel = _activeCts;
                mode = _lastStartedMode;
                generation = Volatile.Read(ref _operationGeneration);
            }

            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var cancellingText = mode == ExportOperationMode.ExtractBundle
                ? "Cancelling extraction..."
                : "Cancelling export...";

            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                CanCancel = false;
                IsCancellationRequested = true;
                if (IsBusy)
                {
                    Status = ExportOperationStatus.Cancelling;
                    StatusText = cancellingText;
                }
            });
        }

        public void ReportProgress(
            int completed,
            int total,
            string? currentItem = null,
            int succeeded = 0,
            int skipped = 0,
            int failed = 0,
            bool force = false)
        {
            var generation = Volatile.Read(ref _operationGeneration);
            var effectiveTotal = total > 0 ? total : Volatile.Read(ref _currentTotalCount);
            if (effectiveTotal < 0)
            {
                effectiveTotal = 0;
            }
            else if (total > 0)
            {
                Volatile.Write(ref _currentTotalCount, total);
            }

            var safeCompleted = effectiveTotal > 0
                ? Math.Clamp(completed, 0, effectiveTotal)
                : Math.Max(0, completed);
            var percent = effectiveTotal > 0
                ? Math.Clamp((int)((double)safeCompleted / effectiveTotal * 100.0), 0, 100)
                : 0;

            var isFinal = effectiveTotal > 0 && safeCompleted >= effectiveTotal;
            var prevPercent = Volatile.Read(ref _lastReportedPercent);
            var isFirst = safeCompleted == 1 && prevPercent < 0;
            var nowTicks = _clockTicksProvider();
            var prevTicks = Interlocked.Read(ref _lastProgressDispatchTicks);

            var percentChanged = percent > prevPercent;
            var timeElapsed = _progressThrottleMs == 0 || (nowTicks - prevTicks >= _progressThrottleMs);

            if (!force && !isFinal && !isFirst && !percentChanged && !timeElapsed)
            {
                return;
            }

            if (percentChanged)
            {
                var currentPrev = prevPercent;
                while (percent > currentPrev)
                {
                    var exchanged = Interlocked.CompareExchange(ref _lastReportedPercent, percent, currentPrev);
                    if (exchanged == currentPrev)
                    {
                        break;
                    }
                    currentPrev = exchanged;
                }
                Interlocked.Exchange(ref _lastProgressDispatchTicks, nowTicks);
            }
            else if (force || isFinal || isFirst)
            {
                if (percent > prevPercent)
                {
                    Interlocked.Exchange(ref _lastReportedPercent, percent);
                }
                Interlocked.Exchange(ref _lastProgressDispatchTicks, nowTicks);
            }
            else
            {
                if (Interlocked.CompareExchange(ref _lastProgressDispatchTicks, nowTicks, prevTicks) != prevTicks)
                {
                    return;
                }
            }

            Interlocked.Increment(ref _progressDispatchCount);

            var safeItem = currentItem ?? string.Empty;
            var safeSucceeded = Math.Max(0, succeeded);
            var safeSkipped = Math.Max(0, skipped);
            var safeFailed = Math.Max(0, failed);

            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                // Prevent out-of-order concurrent worker posts from regressing progress backwards.
                var priorCompleted = Volatile.Read(ref _uiMaxCompletedCount);
                if (!force && safeCompleted < priorCompleted)
                {
                    return;
                }

                Volatile.Write(ref _uiMaxCompletedCount, safeCompleted);
                TotalCount = effectiveTotal;
                CompletedCount = safeCompleted;
                ProgressPercent = percent;
                SucceededCount = safeSucceeded;
                SkippedCount = safeSkipped;
                FailedCount = safeFailed;
                CurrentItemName = safeItem;
            });
        }

        public void UpdateStatusText(string? statusText)
        {
            var generation = Volatile.Read(ref _operationGeneration);
            var cleanText = statusText ?? string.Empty;
            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                StatusText = cleanText;
            });
        }

        public void Complete(
            int succeeded,
            int skipped = 0,
            int failed = 0,
            string? statusText = null,
            string? errorLogPath = null,
            bool wasCancelled = false)
        {
            CancellationTokenSource? ctsToDispose;
            ExportOperationMode completedMode;
            bool effectiveCancelled;
            long generation;
            int totalHint;

            lock (_lifecycleLock)
            {
                ctsToDispose = _activeCts;
                effectiveCancelled = wasCancelled || (ctsToDispose?.IsCancellationRequested ?? false);
                _activeCts = null;
                completedMode = _lastStartedMode;
                totalHint = Volatile.Read(ref _currentTotalCount);
                generation = Interlocked.Increment(ref _operationGeneration);
            }

            DisposeQuietly(ctsToDispose);

            var safeSucceeded = Math.Max(0, succeeded);
            var safeSkipped = Math.Max(0, skipped);
            var safeFailed = Math.Max(0, failed);
            var summedCompleted = safeSucceeded + safeSkipped + safeFailed;
            var finalTotal = totalHint > 0 ? Math.Max(totalHint, summedCompleted) : summedCompleted;
            var finalCompleted = effectiveCancelled
                ? Math.Min(finalTotal > 0 ? finalTotal : summedCompleted, Math.Max(summedCompleted, Volatile.Read(ref _uiMaxCompletedCount)))
                : (finalTotal > 0 ? Math.Max(finalTotal, summedCompleted) : summedCompleted);
            var finalPercent = effectiveCancelled
                ? (finalTotal > 0 ? Math.Clamp((int)((double)finalCompleted / finalTotal * 100.0), 0, 100) : 0)
                : 100;

            var cleanErrorLog = string.IsNullOrWhiteSpace(errorLogPath) ? null : errorLogPath;
            var resolvedStatus = !string.IsNullOrWhiteSpace(statusText)
                ? statusText.Trim()
                : BuildCompletionStatusText(completedMode, safeSucceeded, safeSkipped, safeFailed, cleanErrorLog, effectiveCancelled);

            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                IsExporting = false;
                IsExtracting = false;
                IsBusy = false;
                CanCancel = false;
                IsCancellationRequested = effectiveCancelled;
                ActiveMode = ExportOperationMode.None;
                Status = effectiveCancelled ? ExportOperationStatus.Cancelled : ExportOperationStatus.Completed;
                TotalCount = finalTotal;
                CompletedCount = finalCompleted;
                SucceededCount = safeSucceeded;
                SkippedCount = safeSkipped;
                FailedCount = safeFailed;
                ProgressPercent = finalPercent;
                CurrentItemName = string.Empty;
                LastErrorLogPath = cleanErrorLog;
                StatusText = resolvedStatus;
            });
        }

        public void Fail(string errorMessage, string? errorLogPath = null)
        {
            CancellationTokenSource? ctsToDispose;
            long generation;

            lock (_lifecycleLock)
            {
                ctsToDispose = _activeCts;
                _activeCts = null;
                generation = Interlocked.Increment(ref _operationGeneration);
            }

            DisposeQuietly(ctsToDispose);

            var cleanError = string.IsNullOrWhiteSpace(errorMessage)
                ? "Export failed."
                : errorMessage.Trim();
            var cleanErrorLog = string.IsNullOrWhiteSpace(errorLogPath) ? null : errorLogPath;

            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                IsExporting = false;
                IsExtracting = false;
                IsBusy = false;
                CanCancel = false;
                ActiveMode = ExportOperationMode.None;
                Status = ExportOperationStatus.Failed;
                CurrentItemName = string.Empty;
                if (cleanErrorLog != null)
                {
                    LastErrorLogPath = cleanErrorLog;
                }
                StatusText = cleanError;
            });
        }

        public void Reset()
        {
            CancellationTokenSource? ctsToCancel;
            long generation;

            lock (_lifecycleLock)
            {
                ctsToCancel = _activeCts;
                _activeCts = null;
                _lastStartedMode = ExportOperationMode.None;
                generation = Interlocked.Increment(ref _operationGeneration);
                Volatile.Write(ref _currentTotalCount, 0);
                Volatile.Write(ref _lastReportedPercent, -1);
                Interlocked.Exchange(ref _lastProgressDispatchTicks, _clockTicksProvider() - _progressThrottleMs - 1);
                Interlocked.Exchange(ref _progressDispatchCount, 0);
                Volatile.Write(ref _uiMaxCompletedCount, 0);
            }

            CancelAndDisposeQuietly(ctsToCancel);

            _dispatchToUi(() =>
            {
                if (Volatile.Read(ref _operationGeneration) != generation)
                {
                    return;
                }

                IsExporting = false;
                IsExtracting = false;
                IsBusy = false;
                CanCancel = false;
                IsCancellationRequested = false;
                ProgressPercent = 0;
                CompletedCount = 0;
                TotalCount = 0;
                SucceededCount = 0;
                SkippedCount = 0;
                FailedCount = 0;
                CurrentItemName = string.Empty;
                StatusText = string.Empty;
                LastExportFolder = string.Empty;
                LastErrorLogPath = null;
                ActiveMode = ExportOperationMode.None;
                Status = ExportOperationStatus.Idle;
            });
        }

        public void Dispose()
        {
            CancellationTokenSource? ctsToCancel;
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ctsToCancel = _activeCts;
                _activeCts = null;
                Interlocked.Increment(ref _operationGeneration);
            }

            CancelAndDisposeQuietly(ctsToCancel);
        }

        private static string BuildStartingStatusText(ExportOperationMode mode, int totalCount)
        {
            return mode switch
            {
                ExportOperationMode.ExtractBundle => totalCount > 0 ? $"Extracting {totalCount:N0} files..." : "Extracting files...",
                ExportOperationMode.XmlList => "Exporting asset list to XML...",
                ExportOperationMode.SplitObjects => totalCount > 0 ? $"Exporting {totalCount:N0} objects..." : "Exporting objects...",
                ExportOperationMode.Animator => "Exporting animator...",
                _ => totalCount > 0 ? $"Exporting {totalCount:N0} assets..." : "Exporting assets...",
            };
        }

        private static string BuildCompletionStatusText(
            ExportOperationMode mode,
            int succeeded,
            int skipped,
            int failed,
            string? errorLogPath,
            bool wasCancelled)
        {
            string status;
            if (wasCancelled)
            {
                status = mode == ExportOperationMode.ExtractBundle
                    ? $"Extraction cancelled after {succeeded} files."
                    : $"Export cancelled after {succeeded} assets.";
            }
            else if (mode == ExportOperationMode.ExtractBundle)
            {
                status = $"Finished extracting {succeeded} files.";
            }
            else if (mode == ExportOperationMode.XmlList)
            {
                status = $"Finished exporting asset list to XML with {succeeded} items.";
            }
            else if (mode == ExportOperationMode.SplitObjects)
            {
                status = succeeded == 0 ? "Nothing exported." : $"Finished exporting {succeeded} objects.";
            }
            else
            {
                status = succeeded == 0 ? "Nothing exported." : $"Finished exporting {succeeded} assets.";
            }

            if (skipped > 0)
            {
                status += $" {skipped} skipped (already exported or unsupported).";
            }

            if (failed > 0)
            {
                status += $" {failed} failed.";
            }

            if (!string.IsNullOrWhiteSpace(errorLogPath))
            {
                status += $" Error report: {ExtractFileNameCrossPlatform(errorLogPath)}.";
            }

            return status;
        }

        private static string ExtractFileNameCrossPlatform(string path)
        {
            var span = path.AsSpan().Trim().TrimEnd("/\\");
            if (span.IsEmpty)
            {
                return string.Empty;
            }

            var sepIndex = span.LastIndexOfAny('/', '\\');
            return sepIndex >= 0 ? span[(sepIndex + 1)..].ToString() : span.ToString();
        }

        private static void CancelAndDisposeQuietly(CancellationTokenSource? cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            DisposeQuietly(cts);
        }

        private static void DisposeQuietly(CancellationTokenSource? cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
            }
        }
    }
}
