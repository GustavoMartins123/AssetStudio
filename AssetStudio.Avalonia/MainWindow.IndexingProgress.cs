using Avalonia.Controls;
using Avalonia.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    public void StartProjectIndexingOnOpen()
    {
        if (projectContext == null || projectAutoIndexStarted)
        {
            return;
        }

        projectAutoIndexStarted = true;
        Dispatcher.UIThread.Post(async () => await StartProjectIndexingOnOpenAsync(), DispatcherPriority.Background);
    }

    private async Task StartProjectIndexingOnOpenAsync()
    {
        var root = projectContext?.Project.ProjectRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusStripUpdate("Project opened. Set a project root to index assets.");
            return;
        }

        if (!Directory.Exists(root))
        {
            StatusStripUpdate($"Project root not found: {root}");
            return;
        }

        assetsManager.ProjectRoot = root;
        appSettings.ProjectRoot = root;
        SaveAppSettings();

        await BeginProgressiveLoadAsync(new[] { root }, "Opening project");
    }

    private void StatusStripUpdate(string text)
    {
        _pendingStatusText = text;
        if (!_statusUpdatePending)
        {
            _statusUpdatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                ViewModel.StatusText = _pendingStatusText ?? string.Empty;
                _statusUpdatePending = false;
            }, DispatcherPriority.Background);
        }
    }

    private void SetProgressBarValue(int value)
    {
        Dispatcher.UIThread.Post(() => ViewModel.LoadingProgress = value);
    }

    private void ShowIndexingProgressPanel(IndexingProgressUpdate update, int percentDecimals = 1)
    {
        if (update == null)
        {
            return;
        }

        ShowIndexingProgressPanel(
            update.Status,
            update.ProcessedFiles,
            update.TotalFiles,
            update.PendingFiles,
            update.PercentComplete,
            update.CurrentFile,
            update.LastReadFile,
            null,
            percentDecimals);
    }

    private void ShowIndexingProgressPanel(ProjectIndexingState state, int percentDecimals = 1)
    {
        if (state == null)
        {
            return;
        }

        ShowIndexingProgressPanel(
            state.Status,
            state.ProcessedFiles,
            state.TotalFiles,
            state.PendingFiles,
            state.PercentComplete,
            state.CurrentFile,
            state.LastReadFile,
            state.UpdatedAt,
            percentDecimals);
    }

    private void ShowIndexingProgressPanel(
        string status,
        int processedFiles,
        int totalFiles,
        int pendingFiles,
        double percentComplete,
        string currentFile,
        string lastReadFile,
        DateTime? updatedAt,
        int percentDecimals = 1)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowIndexingProgressPanel(
                status,
                processedFiles,
                totalFiles,
                pendingFiles,
                percentComplete,
                currentFile,
                lastReadFile,
                updatedAt,
                percentDecimals));
            return;
        }

        var percent = Math.Clamp(percentComplete, 0, 100);
        var isStageProgress = IsStageProgressStatus(status);
        var progressDetail = !string.IsNullOrWhiteSpace(currentFile) ? currentFile : lastReadFile;
        var fileName = string.IsNullOrWhiteSpace(progressDetail) || isStageProgress ? string.Empty : Path.GetFileName(progressDetail);
        var unitLabel = isStageProgress ? "steps" : "files";
        var countText = totalFiles > 0
            ? $"{processedFiles:N0}/{totalFiles:N0} {unitLabel}"
            : $"{processedFiles:N0} {unitLabel}";
        var pendingText = pendingFiles > 0 ? $" | {pendingFiles:N0} pending" : string.Empty;
        var fileText = isStageProgress
            ? (string.IsNullOrWhiteSpace(progressDetail) ? string.Empty : $" | {progressDetail}")
            : (string.IsNullOrWhiteSpace(fileName)
                ? string.Empty
                : $" | {(string.IsNullOrWhiteSpace(currentFile) ? "Last" : "Now")}: {fileName}");
        var updatedText = updatedAt.HasValue
            ? $" | Updated {updatedAt.Value.ToLocalTime():HH:mm:ss}"
            : string.Empty;

        IndexingProgressPanel.IsVisible = true;
        IndexingProgressText.Text = $"{BuildIndexingProgressTitle(status)}: {countText}{pendingText}{fileText}{updatedText}";
        IndexingProgressPercentText.Text = percent.ToString("0." + new string('#', Math.Max(0, percentDecimals)), CultureInfo.InvariantCulture) + "%";
        IndexingProgressBar.Value = percent;
    }

    private void HideIndexingProgressPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideIndexingProgressPanel);
            return;
        }

        IndexingProgressPanel.IsVisible = false;
        IndexingProgressText.Text = string.Empty;
        IndexingProgressPercentText.Text = "0%";
        IndexingProgressBar.Value = 0;
    }

    private static string BuildIndexingProgressTitle(string status)
    {
        return (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "paused" => "Indexing paused",
            "cancelling" => "Stopping indexing",
            "cancelled" => "Indexing cancelled",
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
            _ => "Indexing"
        };
    }

    private static bool IsStageProgressStatus(string? status)
    {
        return string.Equals(status, "saving_index", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "saving_connections", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "building_structure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "structure_failed", StringComparison.OrdinalIgnoreCase);
    }

    private void PrioritizeUserInteraction(int milliseconds = UserInteractionPriorityMilliseconds)
    {
        var now = Stopwatch.GetTimestamp();
        var extensionTicks = (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        var until = now + Math.Max(extensionTicks, 1);

        while (true)
        {
            var current = Interlocked.Read(ref userInteractionPriorityUntilTimestamp);
            if (current >= until)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref userInteractionPriorityUntilTimestamp, until, current) == current)
            {
                return;
            }
        }
    }

    private bool IsUserInteractionPriorityActive()
    {
        return Interlocked.Read(ref userInteractionPriorityUntilTimestamp) > Stopwatch.GetTimestamp();
    }

    private bool ShouldPauseBackgroundWork()
    {
        return IsUserInteractionPriorityActive() || Volatile.Read(ref foregroundLazyLoadCount) > 0;
    }

    private bool IsProgressiveIndexingActive()
    {
        return ViewModel.LoadingService.IsIndexingActive;
    }



    private async Task WaitForUserInteractionPriorityToClearAsync(CancellationToken token)
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        while (!token.IsCancellationRequested && ShouldPauseBackgroundWork())
        {
            await Task.Delay(UserInteractionYieldDelayMilliseconds);
        }
    }

    private void YieldBackgroundWorkForUserInteraction()
    {
        if (!assetsManager.LazyLoading)
        {
            return;
        }

        while (ShouldPauseBackgroundWork())
        {
            Thread.Sleep(UserInteractionYieldDelayMilliseconds);
        }
    }
}
