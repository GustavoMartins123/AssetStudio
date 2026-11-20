using System;
using System.Globalization;
using System.IO;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum IndexingStatus
    {
        Idle = 0,
        Scanning = 1,
        Indexing = 2,
        Paused = 3,
        Cancelling = 4,
        Connecting = 5,
        Completed = 6,
        Cancelled = 7,
        Failed = 8,
    }

    public readonly record struct IndexingProgressSnapshot
    {
        public IndexingStatus Status { get; init; }
        public string RawStatus { get; init; }
        public int ProcessedFiles { get; init; }
        public int TotalFiles { get; init; }
        public int PendingFiles { get; init; }
        public double? PercentComplete { get; init; }
        public string CurrentFile { get; init; }
        public string LastReadFile { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public int PercentDecimals { get; init; }

        public IndexingProgressSnapshot(
            IndexingStatus status,
            int processedFiles,
            int totalFiles,
            string? currentFile = null,
            string? lastReadFile = null,
            int? pendingFiles = null,
            double? percentComplete = null,
            DateTime? updatedAt = null,
            string? rawStatus = null,
            int percentDecimals = 1)
        {
            var safeTotal = Math.Max(0, totalFiles);
            var safeProcessed = Math.Max(0, processedFiles);
            Status = status;
            RawStatus = string.IsNullOrWhiteSpace(rawStatus)
                ? IndexingProgressViewModel.ToDefaultRawStatus(status)
                : rawStatus.Trim();
            ProcessedFiles = safeProcessed;
            TotalFiles = safeTotal;
            PendingFiles = Math.Max(0, pendingFiles ?? (safeTotal > 0 ? safeTotal - safeProcessed : 0));
            PercentComplete = percentComplete;
            CurrentFile = currentFile ?? string.Empty;
            LastReadFile = lastReadFile ?? string.Empty;
            UpdatedAt = updatedAt;
            PercentDecimals = Math.Max(0, percentDecimals);
        }

        public static IndexingProgressSnapshot FromUpdate(IndexingProgressUpdate update, int percentDecimals = 1)
        {
            ArgumentNullException.ThrowIfNull(update);
            var rawStatus = (update.Status ?? string.Empty).Trim();
            return new IndexingProgressSnapshot(
                status: IndexingProgressViewModel.ParseStatus(rawStatus),
                processedFiles: update.ProcessedFiles,
                totalFiles: update.TotalFiles,
                currentFile: update.CurrentFile,
                lastReadFile: update.LastReadFile,
                pendingFiles: update.PendingFiles,
                percentComplete: update.PercentComplete,
                updatedAt: null,
                rawStatus: rawStatus,
                percentDecimals: percentDecimals);
        }

        internal static IndexingProgressSnapshot FromState(ProjectIndexingState state, int percentDecimals = 1)
        {
            ArgumentNullException.ThrowIfNull(state);
            var rawStatus = (state.Status ?? string.Empty).Trim();
            return new IndexingProgressSnapshot(
                status: IndexingProgressViewModel.ParseStatus(rawStatus),
                processedFiles: state.ProcessedFiles,
                totalFiles: state.TotalFiles,
                currentFile: state.CurrentFile,
                lastReadFile: state.LastReadFile,
                pendingFiles: state.PendingFiles,
                percentComplete: state.PercentComplete,
                updatedAt: state.UpdatedAt,
                rawStatus: rawStatus,
                percentDecimals: percentDecimals);
        }
    }

    public sealed class IndexingProgressViewModel : ViewModelBase
    {
        private IndexingStatus _status = IndexingStatus.Idle;
        private string _rawStatus = string.Empty;
        private bool _isActive;
        private bool _isIndexing;
        private bool _isConnectionsPhase;
        private bool _isPaused;
        private bool _isIndeterminate;
        private bool _isPanelVisible;
        private int _processedFiles;
        private int _totalFiles;
        private int _pendingFiles;
        private double _progressPercent;
        private string _formattedPercentText = "0%";
        private string _currentFileName = string.Empty;
        private string _primaryStatusText = string.Empty;
        private string _detailStatusText = string.Empty;
        private bool _canPause;
        private bool _canResume;
        private bool _canCancel;
        private bool _hasPreviousIncompleteSession;
        private string _incompleteSessionSummary = string.Empty;

        public IndexingStatus Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public string RawStatus
        {
            get => _rawStatus;
            private set => SetProperty(ref _rawStatus, value ?? string.Empty);
        }

        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

        public bool IsIndexing
        {
            get => _isIndexing;
            private set => SetProperty(ref _isIndexing, value);
        }

        public bool IsConnectionsPhase
        {
            get => _isConnectionsPhase;
            private set => SetProperty(ref _isConnectionsPhase, value);
        }

        public bool IsPaused
        {
            get => _isPaused;
            private set => SetProperty(ref _isPaused, value);
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            private set => SetProperty(ref _isIndeterminate, value);
        }

        public bool IsPanelVisible
        {
            get => _isPanelVisible;
            private set => SetProperty(ref _isPanelVisible, value);
        }

        public int ProcessedFiles
        {
            get => _processedFiles;
            private set => SetProperty(ref _processedFiles, value);
        }

        public int TotalFiles
        {
            get => _totalFiles;
            private set => SetProperty(ref _totalFiles, value);
        }

        public int PendingFiles
        {
            get => _pendingFiles;
            private set => SetProperty(ref _pendingFiles, value);
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetProperty(ref _progressPercent, value);
        }

        public string FormattedPercentText
        {
            get => _formattedPercentText;
            private set => SetProperty(ref _formattedPercentText, value ?? "0%");
        }

        public string CurrentFileName
        {
            get => _currentFileName;
            private set => SetProperty(ref _currentFileName, value ?? string.Empty);
        }

        public string PrimaryStatusText
        {
            get => _primaryStatusText;
            private set => SetProperty(ref _primaryStatusText, value ?? string.Empty);
        }

        public string DetailStatusText
        {
            get => _detailStatusText;
            private set => SetProperty(ref _detailStatusText, value ?? string.Empty);
        }

        public bool CanPause
        {
            get => _canPause;
            private set => SetProperty(ref _canPause, value);
        }

        public bool CanResume
        {
            get => _canResume;
            private set => SetProperty(ref _canResume, value);
        }

        public bool CanCancel
        {
            get => _canCancel;
            private set => SetProperty(ref _canCancel, value);
        }

        public bool HasPreviousIncompleteSession
        {
            get => _hasPreviousIncompleteSession;
            private set => SetProperty(ref _hasPreviousIncompleteSession, value);
        }

        public string IncompleteSessionSummary
        {
            get => _incompleteSessionSummary;
            private set => SetProperty(ref _incompleteSessionSummary, value ?? string.Empty);
        }

        public void BeginScanning(string? currentTarget = null, int scannedFiles = 0, int totalFiles = 0)
        {
            var safeTotal = Math.Max(0, totalFiles);
            var safeScanned = Math.Max(0, scannedFiles);
            var percent = safeTotal > 0
                ? Math.Clamp(safeScanned * 100.0 / safeTotal, 0.0, 100.0)
                : 0.0;

            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Scanning,
                processedFiles: safeScanned,
                totalFiles: safeTotal,
                currentFile: currentTarget,
                lastReadFile: currentTarget,
                pendingFiles: safeTotal > 0 ? Math.Max(0, safeTotal - safeScanned) : 0,
                percentComplete: percent,
                rawStatus: "scanning",
                percentDecimals: 1));
        }

        public void UpdateScanningProgress(int scannedFiles, int totalFiles = 0, string? currentTarget = null)
        {
            BeginScanning(currentTarget, scannedFiles, totalFiles);
        }

        public void BeginIndexing(int totalFiles, string? currentFile = null)
        {
            var safeTotal = Math.Max(0, totalFiles);
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Indexing,
                processedFiles: 0,
                totalFiles: safeTotal,
                currentFile: currentFile,
                lastReadFile: string.Empty,
                pendingFiles: safeTotal,
                percentComplete: 0.0,
                rawStatus: "running",
                percentDecimals: 1));
        }

        public void UpdateIndexingProgress(
            int processedFiles,
            int totalFiles,
            string? currentFile = null,
            string? lastReadFile = null,
            int percentDecimals = 1)
        {
            var safeTotal = Math.Max(0, totalFiles);
            var safeProcessed = Math.Clamp(processedFiles, 0, safeTotal > 0 ? safeTotal : Math.Max(0, processedFiles));
            var percent = safeTotal > 0
                ? Math.Clamp(safeProcessed * 100.0 / safeTotal, 0.0, 100.0)
                : 100.0;

            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Indexing,
                processedFiles: safeProcessed,
                totalFiles: safeTotal,
                currentFile: currentFile,
                lastReadFile: lastReadFile ?? currentFile,
                pendingFiles: Math.Max(0, safeTotal - safeProcessed),
                percentComplete: percent,
                rawStatus: "running",
                percentDecimals: percentDecimals));
        }

        public void Pause(string? currentFile = null)
        {
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Paused,
                processedFiles: _processedFiles,
                totalFiles: _totalFiles,
                currentFile: currentFile ?? _currentFileName,
                lastReadFile: _currentFileName,
                pendingFiles: _pendingFiles,
                percentComplete: _progressPercent,
                rawStatus: "paused",
                percentDecimals: 1));
        }

        public void Resume(string? currentFile = null)
        {
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Indexing,
                processedFiles: _processedFiles,
                totalFiles: _totalFiles,
                currentFile: currentFile ?? _currentFileName,
                lastReadFile: _currentFileName,
                pendingFiles: _pendingFiles,
                percentComplete: _progressPercent,
                rawStatus: "running",
                percentDecimals: 1));
        }

        public void BeginCancelling()
        {
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Cancelling,
                processedFiles: _processedFiles,
                totalFiles: _totalFiles,
                currentFile: _currentFileName,
                lastReadFile: _currentFileName,
                pendingFiles: _pendingFiles,
                percentComplete: _progressPercent,
                rawStatus: "cancelling",
                percentDecimals: 1));
        }

        public void BeginConnections(int totalSources = 0, string? currentSource = null)
        {
            var safeTotal = Math.Max(0, totalSources);
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Connecting,
                processedFiles: 0,
                totalFiles: safeTotal,
                currentFile: currentSource,
                lastReadFile: string.Empty,
                pendingFiles: safeTotal,
                percentComplete: 0.0,
                rawStatus: "connecting",
                percentDecimals: 1));
        }

        public void UpdateConnectionsProgress(int processed, int total, string? currentSource = null)
        {
            var safeTotal = Math.Max(0, total);
            var safeProcessed = Math.Clamp(processed, 0, safeTotal > 0 ? safeTotal : Math.Max(0, processed));
            var percent = safeTotal > 0
                ? Math.Clamp(safeProcessed * 100.0 / safeTotal, 0.0, 100.0)
                : 0.0;

            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Connecting,
                processedFiles: safeProcessed,
                totalFiles: safeTotal,
                currentFile: currentSource,
                lastReadFile: currentSource,
                pendingFiles: Math.Max(0, safeTotal - safeProcessed),
                percentComplete: percent,
                rawStatus: "connecting",
                percentDecimals: 1));
        }

        public void MarkCompleted(int? processedFiles = null, int? totalFiles = null, string? detail = null, string rawStatus = "completed")
        {
            var finalTotal = Math.Max(0, totalFiles ?? (_totalFiles > 0 ? _totalFiles : (processedFiles ?? 0)));
            var finalProcessed = Math.Max(0, processedFiles ?? (finalTotal > 0 ? finalTotal : _processedFiles));
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Completed,
                processedFiles: finalProcessed,
                totalFiles: finalTotal,
                currentFile: detail,
                lastReadFile: detail ?? _currentFileName,
                pendingFiles: 0,
                percentComplete: 100.0,
                rawStatus: string.IsNullOrWhiteSpace(rawStatus) ? "completed" : rawStatus,
                percentDecimals: 1));
        }

        public void MarkCancelled(int? processedFiles = null, int? totalFiles = null, string? lastFile = null)
        {
            var finalTotal = Math.Max(0, totalFiles ?? _totalFiles);
            var finalProcessed = Math.Clamp(processedFiles ?? _processedFiles, 0, finalTotal > 0 ? finalTotal : Math.Max(0, processedFiles ?? _processedFiles));
            var percent = finalTotal > 0
                ? Math.Clamp(finalProcessed * 100.0 / finalTotal, 0.0, 100.0)
                : _progressPercent;

            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Cancelled,
                processedFiles: finalProcessed,
                totalFiles: finalTotal,
                currentFile: string.Empty,
                lastReadFile: lastFile ?? _currentFileName,
                pendingFiles: Math.Max(0, finalTotal - finalProcessed),
                percentComplete: percent,
                rawStatus: "cancelled",
                percentDecimals: 1));
        }

        public void MarkFailed(string? reason = null, string rawStatus = "failed")
        {
            var cleanReason = reason?.Trim() ?? string.Empty;
            ApplySnapshot(new IndexingProgressSnapshot(
                status: IndexingStatus.Failed,
                processedFiles: _processedFiles,
                totalFiles: _totalFiles,
                currentFile: cleanReason,
                lastReadFile: string.IsNullOrWhiteSpace(cleanReason) ? _currentFileName : cleanReason,
                pendingFiles: _pendingFiles,
                percentComplete: _progressPercent,
                rawStatus: string.IsNullOrWhiteSpace(rawStatus) ? "failed" : rawStatus,
                percentDecimals: 1));

            if (!string.IsNullOrWhiteSpace(cleanReason))
            {
                DetailStatusText = cleanReason;
            }
        }

        public void SetIncompletePreviousSession(
            string status,
            double percentComplete,
            string? lastReadFile = null,
            int processedFiles = 0,
            int totalFiles = 0)
        {
            var normalizedStatus = (status ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalizedStatus) || IsCompletedLazyIndexingStatus(normalizedStatus))
            {
                ClearIncompletePreviousSession();
                return;
            }

            var safePercent = Math.Clamp(percentComplete, 0.0, 100.0);
            var safeLastFile = string.IsNullOrWhiteSpace(lastReadFile)
                ? "none"
                : ExtractFileNameSafe(lastReadFile);

            HasPreviousIncompleteSession = true;
            IncompleteSessionSummary =
                $"Previous indexing state: {normalizedStatus}, {safePercent.ToString("0.##", CultureInfo.InvariantCulture)}% complete, last file: {safeLastFile}";

            if (totalFiles > 0 || processedFiles > 0)
            {
                ApplySnapshot(new IndexingProgressSnapshot(
                    status: ParseStatus(normalizedStatus),
                    processedFiles: processedFiles,
                    totalFiles: totalFiles,
                    currentFile: string.Empty,
                    lastReadFile: lastReadFile,
                    pendingFiles: Math.Max(0, totalFiles - processedFiles),
                    percentComplete: safePercent,
                    rawStatus: normalizedStatus,
                    percentDecimals: 1));
            }
        }

        internal void SetIncompletePreviousSession(ProjectIndexingState? state)
        {
            if (state == null || IsCompletedLazyIndexingStatus(state.Status))
            {
                ClearIncompletePreviousSession();
                return;
            }

            var safePercent = Math.Clamp(state.PercentComplete, 0.0, 100.0);
            var safeLastFile = string.IsNullOrWhiteSpace(state.LastReadFile)
                ? "none"
                : ExtractFileNameSafe(state.LastReadFile);

            HasPreviousIncompleteSession = true;
            IncompleteSessionSummary =
                $"Previous indexing state: {state.Status}, {safePercent.ToString("0.##", CultureInfo.InvariantCulture)}% complete, last file: {safeLastFile}";
            ApplySnapshot(IndexingProgressSnapshot.FromState(state));
        }

        public void ClearIncompletePreviousSession()
        {
            HasPreviousIncompleteSession = false;
            IncompleteSessionSummary = string.Empty;
        }

        public void ApplyUpdate(IndexingProgressUpdate update, int percentDecimals = 1)
        {
            if (update == null)
            {
                return;
            }

            ApplySnapshot(IndexingProgressSnapshot.FromUpdate(update, percentDecimals));
        }

        internal void ApplyState(ProjectIndexingState state, int percentDecimals = 1)
        {
            if (state == null)
            {
                return;
            }

            ApplySnapshot(IndexingProgressSnapshot.FromState(state, percentDecimals));
        }

        public void ApplySnapshot(IndexingProgressSnapshot snapshot)
        {
            var rawStatus = string.IsNullOrWhiteSpace(snapshot.RawStatus)
                ? ToDefaultRawStatus(snapshot.Status)
                : snapshot.RawStatus.Trim();
            var resolvedStatus = snapshot.Status != IndexingStatus.Idle || string.IsNullOrWhiteSpace(rawStatus)
                ? snapshot.Status
                : ParseStatus(rawStatus);

            if (resolvedStatus == IndexingStatus.Idle && string.IsNullOrWhiteSpace(rawStatus))
            {
                Reset();
                return;
            }

            var safeTotal = Math.Max(0, snapshot.TotalFiles);
            var safeProcessed = Math.Max(0, snapshot.ProcessedFiles);
            var safePending = Math.Max(0, snapshot.PendingFiles);
            var computedPercent = snapshot.PercentComplete
                ?? (safeTotal > 0
                    ? safeProcessed * 100.0 / safeTotal
                    : (resolvedStatus == IndexingStatus.Completed ? 100.0 : 0.0));
            var percent = Math.Clamp(computedPercent, 0.0, 100.0);

            var isStageProgress = IsStageProgressStatus(rawStatus);
            var currentFile = snapshot.CurrentFile ?? string.Empty;
            var lastReadFile = snapshot.LastReadFile ?? string.Empty;
            var progressDetail = !string.IsNullOrWhiteSpace(currentFile) ? currentFile : lastReadFile;
            var fileName = string.IsNullOrWhiteSpace(progressDetail) || isStageProgress
                ? string.Empty
                : ExtractFileNameSafe(progressDetail);
            var effectiveFileName = isStageProgress ? progressDetail : fileName;

            var unitLabel = isStageProgress ? "steps" : "files";
            var countText = safeTotal > 0
                ? $"{safeProcessed:N0}/{safeTotal:N0} {unitLabel}"
                : $"{safeProcessed:N0} {unitLabel}";
            var pendingText = safePending > 0 ? $" | {safePending:N0} pending" : string.Empty;
            var fileText = isStageProgress
                ? (string.IsNullOrWhiteSpace(progressDetail) ? string.Empty : $" | {progressDetail}")
                : (string.IsNullOrWhiteSpace(fileName)
                    ? string.Empty
                    : $" | {(string.IsNullOrWhiteSpace(currentFile) ? "Last" : "Now")}: {fileName}");
            var updatedText = snapshot.UpdatedAt.HasValue
                ? $" | Updated {snapshot.UpdatedAt.Value.ToLocalTime():HH:mm:ss}"
                : string.Empty;

            var title = BuildIndexingProgressTitle(rawStatus, resolvedStatus);
            var primaryText = $"{title}: {countText}{pendingText}{fileText}{updatedText}";
            var detailText = $"{countText}{pendingText}{fileText}{updatedText}";
            var decimals = Math.Max(0, snapshot.PercentDecimals);
            var formattedPercent = percent.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture) + "%";

            var isActive = resolvedStatus is IndexingStatus.Scanning
                or IndexingStatus.Indexing
                or IndexingStatus.Paused
                or IndexingStatus.Cancelling
                or IndexingStatus.Connecting;
            var isIndexing = resolvedStatus == IndexingStatus.Indexing;
            var isConnections = resolvedStatus == IndexingStatus.Connecting;
            var isPaused = resolvedStatus == IndexingStatus.Paused;
            var isIndeterminate = isActive && safeTotal <= 0;

            var isPausableIndexing = resolvedStatus == IndexingStatus.Indexing && !isStageProgress;
            var canPause = isPausableIndexing;
            var canResume = resolvedStatus == IndexingStatus.Paused;
            var canCancel = (resolvedStatus is IndexingStatus.Scanning or IndexingStatus.Indexing or IndexingStatus.Paused)
                && !isStageProgress;

            Status = resolvedStatus;
            RawStatus = rawStatus;
            IsActive = isActive;
            IsIndexing = isIndexing;
            IsConnectionsPhase = isConnections;
            IsPaused = isPaused;
            IsIndeterminate = isIndeterminate;
            IsPanelVisible = true;
            ProcessedFiles = safeProcessed;
            TotalFiles = safeTotal;
            PendingFiles = safePending;
            ProgressPercent = percent;
            FormattedPercentText = formattedPercent;
            CurrentFileName = effectiveFileName;
            PrimaryStatusText = primaryText;
            DetailStatusText = detailText;
            CanPause = canPause;
            CanResume = canResume;
            CanCancel = canCancel;
        }

        public void HidePanel()
        {
            IsPanelVisible = false;
            PrimaryStatusText = string.Empty;
            DetailStatusText = string.Empty;
            FormattedPercentText = "0%";
            ProgressPercent = 0.0;
        }

        public void Reset()
        {
            Status = IndexingStatus.Idle;
            RawStatus = string.Empty;
            IsActive = false;
            IsIndexing = false;
            IsConnectionsPhase = false;
            IsPaused = false;
            IsIndeterminate = false;
            IsPanelVisible = false;
            ProcessedFiles = 0;
            TotalFiles = 0;
            PendingFiles = 0;
            ProgressPercent = 0.0;
            FormattedPercentText = "0%";
            CurrentFileName = string.Empty;
            PrimaryStatusText = string.Empty;
            DetailStatusText = string.Empty;
            CanPause = false;
            CanResume = false;
            CanCancel = false;
            HasPreviousIncompleteSession = false;
            IncompleteSessionSummary = string.Empty;
        }

        public static IndexingStatus ParseStatus(string? status)
        {
            return (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "" or "idle" or "ready" => IndexingStatus.Idle,
                "scanning" or "scan" => IndexingStatus.Scanning,
                "running" or "indexing" or "saving_index" or "building_structure" => IndexingStatus.Indexing,
                "paused" => IndexingStatus.Paused,
                "cancelling" or "stopping" => IndexingStatus.Cancelling,
                "connecting" or "saving_connections" => IndexingStatus.Connecting,
                "completed" or "connections_completed" or "structure_completed" => IndexingStatus.Completed,
                "cancelled" or "canceled" => IndexingStatus.Cancelled,
                "failed" or "connections_failed" or "structure_failed" => IndexingStatus.Failed,
                _ => IndexingStatus.Indexing,
            };
        }

        public static string ToDefaultRawStatus(IndexingStatus status)
        {
            return status switch
            {
                IndexingStatus.Idle => string.Empty,
                IndexingStatus.Scanning => "scanning",
                IndexingStatus.Indexing => "running",
                IndexingStatus.Paused => "paused",
                IndexingStatus.Cancelling => "cancelling",
                IndexingStatus.Connecting => "connecting",
                IndexingStatus.Completed => "completed",
                IndexingStatus.Cancelled => "cancelled",
                IndexingStatus.Failed => "failed",
                _ => string.Empty,
            };
        }

        public static string BuildIndexingProgressTitle(string? rawStatus, IndexingStatus fallbackStatus = IndexingStatus.Indexing)
        {
            var normalized = (rawStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized switch
                {
                    "scanning" or "scan" => "Scanning files",
                    "paused" => "Indexing paused",
                    "cancelling" or "stopping" => "Stopping indexing",
                    "cancelled" or "canceled" => "Indexing cancelled",
                    "saving_index" => "Saving index cache",
                    "saving_connections" => "Saving connections",
                    "connecting" => "Building connections",
                    "connections_completed" => "Connections complete",
                    "building_structure" => "Building asset structure",
                    "structure_completed" => "Asset structure complete",
                    "structure_failed" => "Asset structure failed",
                    "completed" => "Indexing complete",
                    "failed" => "Indexing failed",
                    "connections_failed" => "Connections build failed",
                    _ => "Indexing",
                };
            }

            return fallbackStatus switch
            {
                IndexingStatus.Scanning => "Scanning files",
                IndexingStatus.Paused => "Indexing paused",
                IndexingStatus.Cancelling => "Stopping indexing",
                IndexingStatus.Connecting => "Building connections",
                IndexingStatus.Completed => "Indexing complete",
                IndexingStatus.Cancelled => "Indexing cancelled",
                IndexingStatus.Failed => "Indexing failed",
                _ => "Indexing",
            };
        }

        public static bool IsStageProgressStatus(string? status)
        {
            return string.Equals(status, "saving_index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "saving_connections", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsCompletedLazyIndexingStatus(string? status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "connections_completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractFileNameSafe(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? path.Trim() : name;
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}
