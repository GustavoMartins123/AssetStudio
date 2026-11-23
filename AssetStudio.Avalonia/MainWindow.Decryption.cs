using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
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

        var catalogHints = await CollectCatalogDecryptionHintsAsync(
            folderPath,
            scanResult.EncryptionBarriers.Select(static b => b.FilePath));
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return scanResult;
        }

        var context = new DecryptionDialogContext(
            IsInitialLoadPrompt: true,
            ProjectRoot: folderPath,
            ReadableAssetFileCount: scanResult.ReadableAssetFileCount,
            EncryptedFileCount: scanResult.EncryptedFileCount,
            CoveragePercent: scanResult.CoveragePercent,
            DetectedBarriers: scanResult.EncryptionBarriers,
            CatalogHints: catalogHints);

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
            var catalogHints = await CollectCatalogDecryptionHintsAsync(folder, filePaths);
            if (IsWindowClosingOrClosed || _isReturningToProjectManager)
            {
                return (readable, encrypted, barriers);
            }

            var context = new DecryptionDialogContext(
                IsInitialLoadPrompt: true,
                ProjectRoot: folder,
                ReadableAssetFileCount: readable,
                EncryptedFileCount: encrypted,
                CoveragePercent: ProjectScanResult.ComputeCoveragePercent(readable, encrypted),
                DetectedBarriers: barriers,
                CatalogHints: catalogHints);

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
        var catalogHints = await CollectCatalogDecryptionHintsAsync(
            currentRoot,
            barriers.Select(static b => b.FilePath));
        if (IsWindowClosingOrClosed || _isReturningToProjectManager)
        {
            return;
        }

        if (barriers.Count == 0 && catalogHints.Count > 0)
        {
            foreach (var hint in catalogHints)
            {
                if (barriers.Count >= 10)
                {
                    break;
                }

                if (UnityHeaderProbe.IsNonStreamDiagnosticHint(hint.Provider))
                {
                    continue;
                }

                var targetPath = !string.IsNullOrWhiteSpace(hint.Bundle) ? hint.Bundle : hint.CatalogPath;
                var candidates = UnityHeaderProbe.AnalyzeEncryptionCandidates(
                    ReadOnlySpan<byte>.Empty,
                    totalStreamLength: 0,
                    filePath: targetPath,
                    providerHint: hint.Provider,
                    reasonHint: hint.Reason);
                barriers.Add(new StreamEncryptionBarrier(
                    providerId: hint.Provider,
                    filePath: targetPath,
                    reason: hint.Reason,
                    confidence: hint.Confidence,
                    headerPrefixHex: hint.MetadataSummary,
                    candidateAlgorithms: candidates));
            }
        }

        var context = new DecryptionDialogContext(
            IsInitialLoadPrompt: false,
            ProjectRoot: currentRoot,
            ReadableAssetFileCount: _lastReadableAssetFileCount,
            EncryptedFileCount: _lastEncryptedFileCount,
            CoveragePercent: ProjectScanResult.ComputeCoveragePercent(_lastReadableAssetFileCount, _lastEncryptedFileCount),
            DetectedBarriers: barriers,
            CatalogHints: catalogHints);

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

    private async Task<IReadOnlyList<DecryptionHintEntry>> CollectCatalogDecryptionHintsAsync(
        string? projectRoot,
        IEnumerable<string>? sampleFilePaths)
    {
        var existingHints = _currentProjectAnalysis?.DecryptionHints?.Hints
            ?? ViewModel.ProjectSession.CurrentAnalysisResult?.DecryptionHints?.Hints;
        if (existingHints != null && existingHints.Count > 0)
        {
            return existingHints;
        }

        var filesSnapshot = sampleFilePaths?
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Take(16)
            .ToArray()
            ?? Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(projectRoot) && filesSnapshot.Length == 0)
        {
            return Array.Empty<DecryptionHintEntry>();
        }

        return await Task.Run<IReadOnlyList<DecryptionHintEntry>>(() =>
        {
            try
            {
                var catalogPaths = AddressablesCatalogDependencyResolver
                    .FindCatalogPathsForFiles(filesSnapshot, projectRoot)
                    .Take(4)
                    .ToList();

                if (catalogPaths.Count == 0 && !string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
                {
                    var probeDirs = new[]
                    {
                        projectRoot,
                        Path.Combine(projectRoot, "aa"),
                        Path.Combine(projectRoot, "StreamingAssets", "aa")
                    };
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dir in probeDirs)
                    {
                        if (!Directory.Exists(dir))
                        {
                            continue;
                        }

                        foreach (var pattern in new[] { "catalog*.json", "catalog*.bin" })
                        {
                            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                            {
                                if (seen.Add(file))
                                {
                                    catalogPaths.Add(file);
                                    if (catalogPaths.Count >= 4)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (catalogPaths.Count >= 4)
                            {
                                break;
                            }
                        }

                        if (catalogPaths.Count >= 4)
                        {
                            break;
                        }
                    }
                }

                if (catalogPaths.Count == 0)
                {
                    return Array.Empty<DecryptionHintEntry>();
                }

                var analyzer = new AddressablesCatalogAnalyzer();
                var warnings = new List<string>();
                var (_, hintsReport) = analyzer.AnalyzeCatalogs(
                    projectRoot ?? Path.GetDirectoryName(catalogPaths[0]) ?? string.Empty,
                    catalogPaths,
                    warnings);
                return hintsReport.Hints;
            }
            catch
            {
                return Array.Empty<DecryptionHintEntry>();
            }
        });
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
