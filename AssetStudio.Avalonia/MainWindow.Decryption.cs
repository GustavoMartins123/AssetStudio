using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private string[]? _lastLoadedPaths;
    private bool _lastLoadWasEager;
    private bool _lastLoadWasSingleFiles;
    private string _lastLoadStatusPrefix = "Indexing folder";
    private int _lastReadableAssetFileCount;
    private int _lastEncryptedFileCount;
    private List<StreamEncryptionBarrier> _lastDetectedBarriers = new();
    private bool _localDecryptionInitialized;

    private void EnsureLocalDecryptionLoaded(string? projectRoot = null)
    {
        try
        {
            if (!_localDecryptionInitialized)
            {
                _localDecryptionInitialized = true;
                var (profiles, plugins) = DecryptionHelper.LoadLocalProfilesAndPlugins(projectRoot);
                if (profiles > 0 || plugins > 0)
                {
                    Logger.Info($"Loaded {profiles} local decryption profile(s) and {plugins} decryption plugin provider(s).");
                }
            }
            else if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            {
                string projectProfiles = Path.Combine(projectRoot, "decryption_profiles.json");
                if (File.Exists(projectProfiles))
                {
                    DecryptionHelper.LoadProfilesFromFile(projectProfiles);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load local decryption profiles/plugins: {ex.Message}");
        }
    }

    private void RecordLoadedSourcePaths(string[] paths, bool isEager, bool isSingleFiles, string statusPrefix)
    {
        _lastLoadedPaths = paths?.ToArray();
        _lastLoadWasEager = isEager;
        _lastLoadWasSingleFiles = isSingleFiles;
        _lastLoadStatusPrefix = string.IsNullOrWhiteSpace(statusPrefix) ? "Indexing folder" : statusPrefix;
    }

    private void ApplyScanCoverageToUi(ProjectScanResult? scanResult)
    {
        if (scanResult == null)
        {
            ApplyManualCoverageToUi(0, 0, Array.Empty<StreamEncryptionBarrier>());
            return;
        }

        ApplyManualCoverageToUi(
            scanResult.ReadableAssetFileCount,
            scanResult.EncryptedFileCount,
            scanResult.EncryptionBarriers);
    }

    private void ApplyManualCoverageToUi(
        int readableAssetFiles,
        int encryptedFiles,
        IReadOnlyList<StreamEncryptionBarrier> barriers)
    {
        _lastReadableAssetFileCount = Math.Max(0, readableAssetFiles);
        _lastEncryptedFileCount = Math.Max(0, encryptedFiles);
        _lastDetectedBarriers = barriers != null ? barriers.ToList() : new List<StreamEncryptionBarrier>();

        ViewModel.ProjectSession.UpdateEncryptionCoverage(_lastReadableAssetFileCount, _lastEncryptedFileCount);
        ViewModel.IndexingProgress.SetEncryptionCoverage(_lastReadableAssetFileCount, _lastEncryptedFileCount);
    }

    private double ComputeEffectiveLoadingProgress(double stagePercent)
    {
        return ProjectScanResult.ScaleProgressToCoverage(
            stagePercent,
            _lastReadableAssetFileCount,
            _lastEncryptedFileCount);
    }

    private string FormatPartialCoverageSuffix()
    {
        if (_lastEncryptedFileCount <= 0)
        {
            return string.Empty;
        }

        var pct = ProjectScanResult.ComputeCoveragePercent(_lastReadableAssetFileCount, _lastEncryptedFileCount);
        var total = _lastReadableAssetFileCount + _lastEncryptedFileCount;
        return string.Create(
            CultureInfo.InvariantCulture,
            $" | Coverage: {pct:0.#}% ({_lastReadableAssetFileCount:N0}/{total:N0} files, {_lastEncryptedFileCount:N0} encrypted — set key anytime)");
    }

    private async Task<ProjectScanResult> PromptForDecryptionKeyOnScanIfNeededAsync(
        string folderPath,
        ProjectScanResult scanResult,
        string statusPrefix)
    {
        if (scanResult == null || !scanResult.HasEncryptedFiles || IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return scanResult!;
        }

        ApplyScanCoverageToUi(scanResult);

        var context = new DecryptionDialogContext(
            IsInitialLoadPrompt: true,
            ProjectRoot: folderPath,
            ReadableAssetFileCount: scanResult.ReadableAssetFileCount,
            EncryptedFileCount: scanResult.EncryptedFileCount,
            CoveragePercent: scanResult.CoveragePercent,
            DetectedBarriers: scanResult.EncryptionBarriers);

        var dialogResult = await ViewModel.DialogService.ShowDecryptionKeyDialogAsync(context);
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return scanResult;
        }

        if (dialogResult?.Action == DecryptionDialogAction.ApplyAndReanalyze)
        {
            EnsureLocalDecryptionLoaded(folderPath);
            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                try
                {
                    ProjectLoadingService.ClearProjectCache(_sqliteCache, folderPath);
                }
                catch
                {
                }
            }

            var rescanned = await ScanFolderForProgressiveIndexAsync(folderPath, statusPrefix);
            if (rescanned != null)
            {
                ApplyScanCoverageToUi(rescanned);
                return rescanned;
            }
        }

        return scanResult;
    }

    private async Task<(int ReadableCount, int EncryptedCount, List<StreamEncryptionBarrier> Barriers)> InspectSingleFilesForEncryptionAsync(
        string[] filePaths,
        bool allowPrompt)
    {
        var (readable, encrypted, barriers) = await Task.Run(() => InspectFilePathsEncryptionCore(filePaths));
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return (readable, encrypted, barriers);
        }

        if (allowPrompt && encrypted > 0)
        {
            ApplyManualCoverageToUi(readable, encrypted, barriers);
            var folder = filePaths.Length > 0 ? Path.GetDirectoryName(filePaths[0]) : null;
            var context = new DecryptionDialogContext(
                IsInitialLoadPrompt: true,
                ProjectRoot: folder,
                ReadableAssetFileCount: readable,
                EncryptedFileCount: encrypted,
                CoveragePercent: ProjectScanResult.ComputeCoveragePercent(readable, encrypted),
                DetectedBarriers: barriers);

            var dialogResult = await ViewModel.DialogService.ShowDecryptionKeyDialogAsync(context);
            if (dialogResult?.Action == DecryptionDialogAction.ApplyAndReanalyze)
            {
                EnsureLocalDecryptionLoaded(folder);
                (readable, encrypted, barriers) = await Task.Run(() => InspectFilePathsEncryptionCore(filePaths));
            }
        }

        return (readable, encrypted, barriers);
    }

    private static (int ReadableCount, int EncryptedCount, List<StreamEncryptionBarrier> Barriers) InspectFilePathsEncryptionCore(string[] filePaths)
    {
        int readable = 0;
        int encrypted = 0;
        var barriers = new List<StreamEncryptionBarrier>();

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            if (DecryptionHelper.InspectFileBarrier(path, out var barrier))
            {
                encrypted++;
                if (barriers.Count < 64)
                {
                    barriers.Add(barrier);
                }
            }
            else
            {
                readable++;
            }
        }

        return (readable, encrypted, barriers);
    }

    private async Task OpenDecryptionSettingsCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        var currentRoot = ResolveCurrentDecryptionTargetRoot();
        EnsureLocalDecryptionLoaded(currentRoot);

        var barriers = new List<StreamEncryptionBarrier>(_lastDetectedBarriers);
        if (barriers.Count == 0 && _currentProjectAnalysis?.DecryptionHints?.Hints != null)
        {
            foreach (var hint in _currentProjectAnalysis.DecryptionHints.Hints.Take(10))
            {
                barriers.Add(new StreamEncryptionBarrier(
                    providerId: hint.Provider,
                    filePath: string.IsNullOrWhiteSpace(hint.CatalogPath) ? hint.Bundle : hint.CatalogPath,
                    reason: hint.Reason,
                    confidence: hint.Confidence,
                    headerPrefixHex: hint.MetadataSummary));
            }
        }

        var context = new DecryptionDialogContext(
            IsInitialLoadPrompt: false,
            ProjectRoot: currentRoot,
            ReadableAssetFileCount: _lastReadableAssetFileCount,
            EncryptedFileCount: _lastEncryptedFileCount,
            CoveragePercent: ProjectScanResult.ComputeCoveragePercent(_lastReadableAssetFileCount, _lastEncryptedFileCount),
            DetectedBarriers: barriers);

        var result = await ViewModel.DialogService.ShowDecryptionKeyDialogAsync(context);
        if (result == null || IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        if (result.Action == DecryptionDialogAction.ApplyAndReanalyze)
        {
            await ReanalyzeWithCurrentDecryptionSettingsAsync(currentRoot);
        }
    }

    private string? ResolveCurrentDecryptionTargetRoot()
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.ProjectSession.ProjectFolderPath)
            && Directory.Exists(ViewModel.ProjectSession.ProjectFolderPath))
        {
            return ViewModel.ProjectSession.ProjectFolderPath;
        }

        var cacheFolder = GetCurrentCacheFolderPath();
        if (!string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder))
        {
            return cacheFolder;
        }

        if (_lastLoadedPaths != null && _lastLoadedPaths.Length > 0)
        {
            if (Directory.Exists(_lastLoadedPaths[0]))
            {
                return _lastLoadedPaths[0];
            }
            return Path.GetDirectoryName(_lastLoadedPaths[0]);
        }

        return null;
    }

    private async Task ReanalyzeWithCurrentDecryptionSettingsAsync(string? currentRoot)
    {
        StatusStripUpdate("Applying decryption settings and re-analyzing...");
        DecryptionHelper.ReloadLocalProfilesAndPlugins(currentRoot);

        if (!string.IsNullOrWhiteSpace(currentRoot) && Directory.Exists(currentRoot))
        {
            try
            {
                ProjectLoadingService.ClearProjectCache(_sqliteCache, currentRoot);
                var analysisService = projectContext?.Store.AnalysisService ?? _projectAnalysisService;
                analysisService.DeleteProjectAnalysisCache(currentRoot);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to clear cached project index before decryption re-analysis: {ex.Message}");
            }
        }

        if (_lastLoadedPaths != null && _lastLoadedPaths.Length > 0)
        {
            var pathsCopy = _lastLoadedPaths.ToArray();
            if (_lastLoadWasSingleFiles)
            {
                await LoadSingleFilesCoreAsync(pathsCopy, allowPrompt: false, statusPrefix: _lastLoadStatusPrefix);
            }
            else if (_lastLoadWasEager && pathsCopy.Length == 1 && Directory.Exists(pathsCopy[0]))
            {
                await LoadFolderEagerAsync(pathsCopy[0], promptForDecryptionKey: false, forceProjectAnalysisRefresh: true);
            }
            else
            {
                await BeginProgressiveLoadAsync(
                    pathsCopy,
                    _lastLoadStatusPrefix,
                    promptForDecryptionKey: false,
                    forceProjectAnalysisRefresh: true);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(currentRoot) && Directory.Exists(currentRoot))
        {
            await BeginProgressiveLoadAsync(
                new[] { currentRoot },
                "Re-analyzing project",
                promptForDecryptionKey: false,
                forceProjectAnalysisRefresh: true);
            return;
        }

        StatusStripUpdate("Decryption settings saved.");
    }
}
