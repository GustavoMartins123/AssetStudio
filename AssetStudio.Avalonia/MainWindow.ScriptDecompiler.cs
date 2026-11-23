using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.Services.Scripting;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private readonly ScriptViewerService _scriptViewerService = new();
    private AsyncPreviewRequestCoordinator _scriptRequestCoordinator => PreviewSession.ScriptCoordinator;
    private string? _customScriptsDirectory;
    private string? _lastLoadedAssemblyDirectory;

    private void PreviewMonoScript(AssetItem assetItem, MonoScript m_MonoScript)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        var fullName = !string.IsNullOrWhiteSpace(m_MonoScript.m_Namespace)
            ? $"{m_MonoScript.m_Namespace}.{m_MonoScript.m_ClassName}"
            : m_MonoScript.m_ClassName;

        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Text,
            assetItem,
            title: string.IsNullOrWhiteSpace(fullName) ? assetItem.Name : fullName,
            isLoading: true,
            loadingStatusText: $"Decompiling script: {fullName}...");
        PreviewSession.TextContent = $"Decompiling {fullName}...";
        TextPreviewBox.FontFamily = new global::Avalonia.Media.FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
        TextPreviewBox.FontSize = 12;
        TextPreviewBox.Text = PreviewSession.TextContent;
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Resolving C# source for {fullName}...");

        var lookupRequest = BuildScriptLookupRequest(
            assemblyName: m_MonoScript.m_AssemblyName,
            ns: m_MonoScript.m_Namespace,
            className: m_MonoScript.m_ClassName,
            assetName: assetItem.Name);

        _ = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var result = await _scriptViewerService.ResolveScriptAsync(lookupRequest, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var displayCode = TruncatePreviewText(result.SourceCode);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    PreviewSession.ShowScriptResult(result);
                    PreviewSession.ShowTextPreview(displayCode, PreviewSurfaceKind.Text, assetItem, title: result.Title, truncate: false);
                    TextPreviewBox.FontFamily = new global::Avalonia.Media.FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
                    TextPreviewBox.FontSize = 12;
                    TextPreviewBox.Text = displayCode;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate(result.Success
                        ? $"Script loaded: {result.Title} ({result.BackendBadge})"
                        : $"Script metadata loaded: {result.Title} (use Script tab -> Scripts Folder... to locate assemblies/dump.cs)");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"MonoScript preview failed for {assetItem.Name}: {ex}");
                var fallback = $"Assembly: {m_MonoScript.m_AssemblyName}{Environment.NewLine}"
                    + $"Namespace: {m_MonoScript.m_Namespace}{Environment.NewLine}"
                    + $"Class: {m_MonoScript.m_ClassName}{Environment.NewLine}{Environment.NewLine}"
                    + $"Decompilation error: {ex.Message}";
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    PreviewSession.ShowTextPreview(fallback, PreviewSurfaceKind.Text, assetItem, truncate: false);
                    TextPreviewBox.Text = fallback;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Script preview error: {ex.Message}");
                });
            }
        }, token);
    }

    private async Task UpdateScriptForSelectedAssetAsync(AssetItem? overrideAsset = null)
    {
        var assetItem = overrideAsset ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (assetItem == null)
        {
            PreviewSession.ClearScript();
            return;
        }

        if (assetItem.Type != ClassIDType.MonoScript && assetItem.Type != ClassIDType.MonoBehaviour)
        {
            var nonScriptRequest = new ScriptLookupRequest
            {
                ClassName = string.Empty,
                AssetName = assetItem.Name
            };
            PreviewSession.ShowScriptResult(ScriptDecompilationResult.Unavailable(
                nonScriptRequest,
                $"Selected asset '{assetItem.Name}' ({assetItem.DisplayType}) is not a MonoScript or MonoBehaviour. Select a script asset or MonoBehaviour to view C# code (or click 'Export Ghidra...' to export project IL2CPP symbols).",
                fallbackCode: string.Empty));
            return;
        }

        var token = PreviewSession.BeginScriptRequest(out var requestId, assetItem.Name);
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var shouldPauseIndexing = assetsManager.LazyLoading && assetItem.Handle != null;
        if (shouldPauseIndexing)
        {
            Interlocked.Increment(ref foregroundLazyLoadCount);
        }

        try
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (assetsManager.LazyLoading && assetItem.Handle != null && assetItem.Asset == null)
                {
                    EnsureLazyAssetReadyForPreview(assetItem);
                }

                token.ThrowIfCancellationRequested();
                var asset = assetItem.Asset;
                var scriptIdentity = ResolveScriptIdentityBackground(assetItem, asset, token);
                token.ThrowIfCancellationRequested();

                var request = BuildScriptLookupRequest(
                    assemblyName: scriptIdentity.AssemblyName,
                    ns: scriptIdentity.Namespace,
                    className: scriptIdentity.ClassName,
                    assetName: assetItem.Name);

                return _scriptViewerService.ResolveScript(request, token);
            }, token);

            if (token.IsCancellationRequested
                || !PreviewSession.IsScriptRequestCurrent(requestId, token))
            {
                return;
            }

            PreviewSession.CompleteScriptRequest(requestId, result);
            StatusStripUpdate(result.Success
                ? $"Script loaded: {result.Title} ({result.BackendBadge})"
                : $"Script source not found for {result.Title}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (PreviewSession.IsScriptRequestCurrent(requestId, token))
            {
                PreviewSession.FailScriptRequest(requestId, ex.Message);
                StatusStripUpdate($"Script decompilation error: {ex.Message}");
            }
        }
        finally
        {
            if (shouldPauseIndexing)
            {
                Interlocked.Decrement(ref foregroundLazyLoadCount);
            }
        }
    }

    private (string AssemblyName, string Namespace, string ClassName) ResolveScriptIdentityBackground(
        AssetItem assetItem,
        AssetStudio.Object? asset,
        CancellationToken token)
    {
        if (asset is MonoScript directScript)
        {
            return (
                directScript.m_AssemblyName ?? string.Empty,
                directScript.m_Namespace ?? string.Empty,
                directScript.m_ClassName ?? string.Empty);
        }

        if (asset is MonoBehaviour monoBehaviour)
        {
            var resolvedScript = TryResolveMonoScriptFromMonoBehaviourBackground(monoBehaviour, token);
            if (resolvedScript != null)
            {
                return (
                    resolvedScript.m_AssemblyName ?? string.Empty,
                    resolvedScript.m_Namespace ?? string.Empty,
                    resolvedScript.m_ClassName ?? string.Empty);
            }

            // Fallback: if m_Script points to a MonoScript handle in ProjectIndex, use handle name
            var fallbackName = !string.IsNullOrWhiteSpace(assetItem.Name)
                ? assetItem.Name
                : (monoBehaviour.m_Name ?? string.Empty);
            return (string.Empty, string.Empty, fallbackName);
        }

        return (string.Empty, string.Empty, assetItem.Name ?? string.Empty);
    }

    private MonoScript? TryResolveMonoScriptFromMonoBehaviourBackground(MonoBehaviour monoBehaviour, CancellationToken token)
    {
        var pptr = monoBehaviour.m_Script;
        var sourceFile = monoBehaviour.assetsFile;
        if (pptr == null || pptr.IsNull || sourceFile == null)
        {
            return null;
        }

        // 1. Direct in-memory lookup (same file or already-loaded external serialized file)
        AssetStudio.Object? scriptObj = null;
        string targetFileName = string.Empty;
        string targetUniqueId = string.Empty;

        if (pptr.m_FileID == 0)
        {
            targetFileName = sourceFile.fileName ?? string.Empty;
            targetUniqueId = AssetHandle.BuildUniqueID(sourceFile, pptr.m_PathID);
            sourceFile.TryGetObject(pptr.m_PathID, out scriptObj);
        }
        else if (pptr.m_FileID > 0 && sourceFile.m_Externals != null && pptr.m_FileID - 1 < sourceFile.m_Externals.Count)
        {
            var ext = sourceFile.m_Externals[pptr.m_FileID - 1];
            targetFileName = NormalizeExternalFileNameForInspector(ext?.fileName, ext?.pathName);
            if (assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile) && extFile != null)
            {
                targetFileName = extFile.fileName ?? targetFileName;
                targetUniqueId = AssetHandle.BuildUniqueID(extFile, pptr.m_PathID);
                extFile.TryGetObject(pptr.m_PathID, out scriptObj);
            }
            else if (!string.IsNullOrWhiteSpace(targetFileName))
            {
                targetUniqueId = $"{targetFileName}#{pptr.m_PathID}";
            }
        }

        if (scriptObj is MonoScript inMemoryScript)
        {
            return inMemoryScript;
        }

        // 2. Standard PPtr.TryGet fallback when eager-loaded
        if (!assetsManager.LazyLoading && pptr.TryGet(out var eagerScript) && eagerScript != null)
        {
            return eagerScript;
        }

        // 3. Lazy Mode on-demand resolution of ONLY the single target MonoScript handle
        if (assetsManager.LazyLoading)
        {
            token.ThrowIfCancellationRequested();
            AssetHandle? handle = !string.IsNullOrWhiteSpace(targetUniqueId)
                ? assetsManager.ProjectIndex.GetHandle(targetUniqueId)
                : null;
            if (handle == null && !string.IsNullOrWhiteSpace(targetFileName))
            {
                handle = assetsManager.ProjectIndex.GetHandlesForFile(targetFileName)
                    .FirstOrDefault(h => h.PathID == pptr.m_PathID);
            }

            if (handle != null)
            {
                if (handle.RealObject is MonoScript cachedScript)
                {
                    return cachedScript;
                }

                var tempItem = new AssetItem(handle, 0);
                EnsureLazyAssetReadyForPreview(tempItem);
                if (tempItem.Asset is MonoScript loadedScript)
                {
                    return loadedScript;
                }
            }
        }

        return null;
    }

    private ScriptLookupRequest BuildScriptLookupRequest(
        string? assemblyName,
        string? ns,
        string? className,
        string? assetName)
    {
        var projectRoot = ResolveEffectiveScriptProjectRoot();
        var report = CurrentProjectAnalysisReport;
        var projectAnalysisCacheDir = !string.IsNullOrWhiteSpace(projectRoot)
            ? ProjectAnalysisService.Shared.GetProjectCacheDirectory(projectRoot)
            : null;
        var discovered = ScriptSourceDiscovery.Discover(
            projectRoot: projectRoot,
            customScriptsDirectory: _customScriptsDirectory,
            knownIl2CppArtifacts: report?.Il2CppDumperArtifactPaths,
            gameAssemblyPath: report?.GameAssemblyPath,
            loadedAssemblyDirectory: _lastLoadedAssemblyDirectory,
            globalMetadataPath: report?.GlobalMetadataPath,
            projectAnalysisCacheDirectory: projectAnalysisCacheDir);

        return new ScriptLookupRequest
        {
            AssemblyName = assemblyName ?? string.Empty,
            Namespace = ns ?? string.Empty,
            ClassName = className ?? string.Empty,
            AssetName = assetName ?? string.Empty,
            ProjectRoot = projectRoot,
            CustomScriptsDirectory = _customScriptsDirectory,
            ManagedDirectories = discovered.ManagedDirectories,
            DummyDllDirectories = discovered.DummyDllDirectories,
            DumpCsPath = discovered.DumpCsPath,
            ScriptJsonPath = discovered.ScriptJsonPath,
            Il2CppHeaderPath = discovered.Il2CppHeaderPath,
            GameAssemblyPath = discovered.GameAssemblyPath,
            GlobalMetadataPath = discovered.GlobalMetadataPath,
            ProjectAnalysisCacheDirectory = projectAnalysisCacheDir,
            ScriptingBackend = report?.ScriptingBackend ?? UnityScriptingBackend.Unknown
        };
    }

    private string? ResolveEffectiveScriptProjectRoot()
    {
        if (!string.IsNullOrWhiteSpace(CurrentProjectAnalysisReport?.ProjectRoot)
            && Directory.Exists(CurrentProjectAnalysisReport.ProjectRoot))
        {
            return CurrentProjectAnalysisReport.ProjectRoot;
        }

        if (!string.IsNullOrWhiteSpace(projectContext?.Project.ProjectRoot)
            && Directory.Exists(projectContext.Project.ProjectRoot))
        {
            return projectContext.Project.ProjectRoot;
        }

        if (!string.IsNullOrWhiteSpace(assetsManager.ProjectRoot)
            && Directory.Exists(assetsManager.ProjectRoot))
        {
            return assetsManager.ProjectRoot;
        }

        var cacheFolder = GetCurrentCacheFolderPath();
        return !string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder)
            ? cacheFolder
            : null;
    }

    private async Task ViewSelectedScriptCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (RightTabControl.SelectedIndex != 3)
        {
            RightTabControl.SelectedIndex = 3;
            return;
        }

        await UpdateScriptForSelectedAssetAsync();
    }

    private async Task CopyScriptCodeCommandHandlerAsync()
    {
        var code = PreviewSession.ScriptFullSourceCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        if (await ViewModel.DialogService.CopyTextToClipboardAsync(code))
        {
            StatusStripUpdate($"Copied C# source ({PreviewSession.ScriptTitle}) to clipboard.");
        }
    }

    private async Task SaveScriptCodeCommandHandlerAsync()
    {
        var code = PreviewSession.ScriptFullSourceCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var baseName = !string.IsNullOrWhiteSpace(PreviewSession.ScriptClassName)
            ? PreviewSession.ScriptClassName
            : (!string.IsNullOrWhiteSpace(PreviewSession.ScriptTitle) ? PreviewSession.ScriptTitle : "Script");
        var suggestedFileName = AssetExportService.FixFileName(baseName) + ".cs";

        var savePath = await ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: "Save C# Script",
            suggestedFileName: suggestedFileName,
            defaultExtension: "cs",
            fileTypeFilters: new[] { new FileTypeFilterSpec("C# Source File", "*.cs") }));

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(savePath, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusStripUpdate($"Saved script to {savePath}");
        }
        catch (Exception ex)
        {
            await ViewModel.DialogService.ShowErrorAsync("Save Script Failed", $"Could not save C# script:\n{ex.Message}");
        }
    }

    private async Task ExportGhidraScriptCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var projectRoot = ResolveEffectiveScriptProjectRoot();
        var report = CurrentProjectAnalysisReport;
        var projectAnalysisCacheDir = !string.IsNullOrWhiteSpace(projectRoot)
            ? ProjectAnalysisService.Shared.GetProjectCacheDirectory(projectRoot)
            : null;
        var discovered = ScriptSourceDiscovery.Discover(
            projectRoot: projectRoot,
            customScriptsDirectory: _customScriptsDirectory,
            knownIl2CppArtifacts: report?.Il2CppDumperArtifactPaths,
            gameAssemblyPath: report?.GameAssemblyPath,
            loadedAssemblyDirectory: _lastLoadedAssemblyDirectory,
            globalMetadataPath: report?.GlobalMetadataPath,
            projectAnalysisCacheDirectory: projectAnalysisCacheDir);

        var hasProjectScriptJson = !string.IsNullOrWhiteSpace(discovered.ScriptJsonPath)
            && File.Exists(discovered.ScriptJsonPath);
        var hasSelectedScript = !string.IsNullOrWhiteSpace(PreviewSession.ScriptFullSourceCode);

        var scope = GhidraExportScope.SelectedClass;
        if (hasProjectScriptJson && hasSelectedScript)
        {
            var exportAll = await ViewModel.DialogService.ConfirmAsync(
                "Export for Ghidra",
                $"Project script.json was detected ({Path.GetFileName(discovered.ScriptJsonPath)}).\n\n" +
                $"Click Yes to export ALL project IL2CPP symbols (methods, strings, metadata) + Ghidra apply script.\n" +
                $"Click No to export only the currently selected class ({PreviewSession.ScriptTitle}).");
            scope = exportAll ? GhidraExportScope.FullProject : GhidraExportScope.SelectedClass;
        }
        else if (hasProjectScriptJson)
        {
            scope = GhidraExportScope.FullProject;
        }

        var outFolder = await ViewModel.DialogService.PickOpenFolderAsync(new FolderPickerRequest(
            title: "Select Output Folder for Ghidra Export"));
        if (string.IsNullOrWhiteSpace(outFolder))
        {
            return;
        }

        var binaryName = !string.IsNullOrWhiteSpace(discovered.GameAssemblyPath)
            ? Path.GetFileName(discovered.GameAssemblyPath)
            : (report?.ScriptingBackend == UnityScriptingBackend.Mono ? "Assembly-CSharp.dll" : "GameAssembly.dll");
        var backendLabel = !string.IsNullOrWhiteSpace(PreviewSession.ScriptBackendBadge)
            ? PreviewSession.ScriptBackendBadge
            : (report?.ScriptingBackend.ToString() ?? "Unknown");

        var options = new GhidraExportOptions
        {
            OutputDirectory = outFolder,
            Scope = scope,
            BinaryName = binaryName,
            BackendLabel = backendLabel,
            ClassTitle = PreviewSession.ScriptTitle,
            SelectedClassMethods = PreviewSession.ScriptMethodSymbols,
            ScriptJsonPath = discovered.ScriptJsonPath,
            Il2CppHeaderPath = discovered.Il2CppHeaderPath,
            DecompiledSourceCode = hasSelectedScript ? PreviewSession.ScriptFullSourceCode : null,
            SuggestedSourceFileName = !string.IsNullOrWhiteSpace(PreviewSession.ScriptClassName)
                ? $"{AssetExportService.FixFileName(PreviewSession.ScriptClassName)}.cs"
                : null
        };

        StatusStripUpdate("Exporting Ghidra symbol bundle...");
        var exportResult = await Task.Run(() => _scriptViewerService.ExportForGhidra(options));
        if (exportResult.Success)
        {
            StatusStripUpdate(exportResult.SummaryMessage);
            await ViewModel.DialogService.ShowMessageAsync(
                "Ghidra Export Complete",
                $"{exportResult.SummaryMessage}\n\n" +
                $"Generated files:\n" +
                $"• {Path.GetFileName(exportResult.GhidraScriptPath)}\n" +
                $"• {Path.GetFileName(exportResult.SymbolsJsonPath)}\n" +
                (exportResult.HeaderOutputPath != null ? $"• {Path.GetFileName(exportResult.HeaderOutputPath)}\n" : string.Empty) +
                (exportResult.SourceOutputPath != null ? $"• {Path.GetFileName(exportResult.SourceOutputPath)}\n" : string.Empty) +
                $"• {Path.GetFileName(exportResult.ReadmePath)}");
        }
        else
        {
            StatusStripUpdate($"Ghidra export failed: {exportResult.ErrorMessage}");
            await ViewModel.DialogService.ShowErrorAsync("Ghidra Export Failed", exportResult.ErrorMessage ?? "Unknown error.");
        }
    }

    private async Task SelectScriptsFolderCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var folder = await ViewModel.DialogService.PickOpenFolderAsync(new FolderPickerRequest(
            title: "Select Managed/ or Il2CppDumper Folder (dump.cs / script.json / DummyDll)"));
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _customScriptsDirectory = folder;
        _scriptViewerService.ClearCaches();
        StatusStripUpdate($"Scripts folder set to: {folder}");

        if (RightTabControl.SelectedIndex == 3)
        {
            await UpdateScriptForSelectedAssetAsync();
        }
        else if (AssetListDataGrid.SelectedItem is AssetItem selected && selected.Type == ClassIDType.MonoScript)
        {
            PreviewSelectedAssetImmediately();
        }
    }

    private void ResetScriptDecompilerCaches()
    {
        _scriptRequestCoordinator.CancelCurrent();
        _customScriptsDirectory = null;
        _lastLoadedAssemblyDirectory = null;
        _scriptViewerService.ClearCaches();
        PreviewSession.ClearScript();
    }

    private void DisposeScriptDecompilerService()
    {
        _scriptRequestCoordinator.CancelCurrent();
        _scriptViewerService.Dispose();
    }
}

public sealed class BoolToTextWrappingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TextWrapping wrapping && wrapping != TextWrapping.NoWrap;
    }
}
