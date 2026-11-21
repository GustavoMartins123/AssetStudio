using Avalonia.Controls;
using Avalonia.Threading;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
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
        if (projectContext == null || projectAutoIndexStarted || IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        projectAutoIndexStarted = true;
        Dispatcher.UIThread.Post(async () => await StartProjectIndexingOnOpenAsync(), DispatcherPriority.Background);
    }

    private async Task StartProjectIndexingOnOpenAsync()
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

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

        ShowIndexingProgressPanel(IndexingProgressSnapshot.FromUpdate(update, percentDecimals));
    }

    private void ShowIndexingProgressPanel(ProjectIndexingState state, int percentDecimals = 1)
    {
        if (state == null)
        {
            return;
        }

        ShowIndexingProgressPanel(IndexingProgressSnapshot.FromState(state, percentDecimals));
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
        ShowIndexingProgressPanel(new IndexingProgressSnapshot(
            status: IndexingProgressViewModel.ParseStatus(status),
            processedFiles: processedFiles,
            totalFiles: totalFiles,
            currentFile: currentFile,
            lastReadFile: lastReadFile,
            pendingFiles: pendingFiles,
            percentComplete: percentComplete,
            updatedAt: updatedAt,
            rawStatus: status,
            percentDecimals: percentDecimals));
    }

    private void ShowIndexingProgressPanel(IndexingProgressSnapshot snapshot)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowIndexingProgressPanel(snapshot));
            return;
        }

        ViewModel.IndexingProgress.ApplySnapshot(snapshot);
    }

    private void HideIndexingProgressPanel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HideIndexingProgressPanel);
            return;
        }

        ViewModel.IndexingProgress.HidePanel();
    }

    private static string BuildIndexingProgressTitle(string status) =>
        IndexingProgressViewModel.BuildIndexingProgressTitle(status);

    private static bool IsStageProgressStatus(string? status) =>
        IndexingProgressViewModel.IsStageProgressStatus(status);

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
