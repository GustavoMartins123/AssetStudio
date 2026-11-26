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
using System.ComponentModel;
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
    private readonly AssemblyExplorerService _assemblyExplorerService = new();
    private readonly CSharpProjectExporterService _csharpProjectExporter = new();
    private AsyncPreviewRequestCoordinator _scriptRequestCoordinator => PreviewSession.ScriptCoordinator;
    private string? _customScriptsDirectory;
    private string? _lastLoadedAssemblyDirectory;
    private string? _assemblyExplorerLoadedSourceKey;
    private bool _scriptExplorerEventsWired;
    private bool _suppressAssemblyTypeSelection;

    private void WireScriptExplorerEvents()
    {
        if (_scriptExplorerEventsWired)
        {
            return;
        }

        _scriptExplorerEventsWired = true;
        PreviewSession.PropertyChanged += OnPreviewSessionPropertyChangedForScriptExplorer;
    }

    private void OnPreviewSessionPropertyChangedForScriptExplorer(object? sender, PropertyChangedEventArgs e)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (e.PropertyName == nameof(PreviewSessionViewModel.SelectedAssemblyType))
        {
            if (!_suppressAssemblyTypeSelection && PreviewSession.SelectedAssemblyType is AssemblyTypeEntry entry)
            {
                ViewModel.SelectAssemblyExplorerTypeCommand.Execute(entry);
            }
        }
        else if (e.PropertyName == nameof(PreviewSessionViewModel.IsAssemblyExplorerVisible)
            && PreviewSession.IsAssemblyExplorerVisible)
        {
            EnsureAssemblyExplorerLoadedIfNeeded();
        }
    }

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

        var scanSnapshot = currentScanResult;
        var cacheFolder = GetCurrentCacheFolderPath();

        _ = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var (fallbackTypeTree, fallbackSourceFile) = TryResolveTypeTreeForScriptBackground(
                    assetItem,
                    m_MonoScript,
                    m_MonoScript.m_AssemblyName,
                    m_MonoScript.m_Namespace,
                    m_MonoScript.m_ClassName,
                    scanSnapshot,
                    cacheFolder,
                    token);
                var lookupRequest = BuildScriptLookupRequest(
                    assemblyName: m_MonoScript.m_AssemblyName,
                    ns: m_MonoScript.m_Namespace,
                    className: m_MonoScript.m_ClassName,
                    assetName: assetItem.Name,
                    fallbackTypeTree: fallbackTypeTree,
                    fallbackTypeTreeSourceFile: fallbackSourceFile);

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
                        : $"Script source unavailable: {result.Title}");
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
        // Lazily populate Assembly Explorer ONLY when the user opens the Script tab (never on startup/project load)
        EnsureAssemblyExplorerLoadedIfNeeded();

        var assetItem = overrideAsset ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (assetItem == null)
        {
            if (PreviewSession.SelectedAssemblyType == null)
            {
                PreviewSession.ClearScript();
            }
            return;
        }

        if (assetItem.Type != ClassIDType.MonoScript && assetItem.Type != ClassIDType.MonoBehaviour)
        {
            // Keep any explicitly clicked Assembly Explorer type visible if user hasn't picked a script asset
            if (PreviewSession.SelectedAssemblyType != null && overrideAsset == null)
            {
                return;
            }

            var nonScriptRequest = new ScriptLookupRequest
            {
                ClassName = string.Empty,
                AssetName = assetItem.Name
            };
            PreviewSession.ShowScriptResult(ScriptDecompilationResult.Unavailable(
                nonScriptRequest,
                $"Selected asset '{assetItem.Name}' ({assetItem.DisplayType}) is not a MonoScript or MonoBehaviour. Select a script asset, browse the Assembly Explorer, or export a C# project.",
                fallbackCode: string.Empty));
            PreviewSession.ClearScriptUsages();
            return;
        }

        var token = PreviewSession.BeginScriptRequest(out var requestId, assetItem.Name);
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var shouldPauseIndexing = assetsManager.LazyLoading && assetItem.Handle != null;
        if (shouldPauseIndexing)
        {
            Interlocked.Increment(ref foregroundLazyLoadCount);
        }

        var scanSnapshot = currentScanResult;
        var cacheFolder = GetCurrentCacheFolderPath();

        try
        {
            var resolved = await Task.Run(() =>
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

                var (fallbackTypeTree, fallbackSourceFile) = TryResolveTypeTreeForScriptBackground(
                    assetItem,
                    asset,
                    scriptIdentity.AssemblyName,
                    scriptIdentity.Namespace,
                    scriptIdentity.ClassName,
                    scanSnapshot,
                    cacheFolder,
                    token);

                var request = BuildScriptLookupRequest(
                    assemblyName: scriptIdentity.AssemblyName,
                    ns: scriptIdentity.Namespace,
                    className: scriptIdentity.ClassName,
                    assetName: assetItem.Name,
                    fallbackTypeTree: fallbackTypeTree,
                    fallbackTypeTreeSourceFile: fallbackSourceFile);

                var decompResult = _scriptViewerService.ResolveScript(request, token);
                MonoScript? directMonoScript = asset as MonoScript;
                return (Result: decompResult, Identity: scriptIdentity, DirectMonoScript: directMonoScript);
            }, token);

            if (token.IsCancellationRequested
                || !PreviewSession.IsScriptRequestCurrent(requestId, token))
            {
                return;
            }

            PreviewSession.CompleteScriptRequest(requestId, resolved.Result);
            StatusStripUpdate(resolved.Result.Success
                ? $"Script loaded: {resolved.Result.Title} ({resolved.Result.BackendBadge})"
                : $"Script source not found for {resolved.Result.Title}.");

            var effectiveAsm = !string.IsNullOrWhiteSpace(resolved.Result.AssemblyName)
                ? resolved.Result.AssemblyName
                : resolved.Identity.AssemblyName;
            var effectiveNs = !string.IsNullOrWhiteSpace(resolved.Result.Namespace)
                ? resolved.Result.Namespace
                : resolved.Identity.Namespace;
            var effectiveClass = !string.IsNullOrWhiteSpace(resolved.Result.ClassName)
                ? resolved.Result.ClassName
                : resolved.Identity.ClassName;
            var scriptHandle = assetItem.Type == ClassIDType.MonoScript ? assetItem.Handle : null;

            RefreshScriptUsagesAsync(effectiveAsm, effectiveNs, effectiveClass, scriptHandle, resolved.DirectMonoScript);
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

    private (TypeTree? TypeTree, string? SourceFileName) TryResolveTypeTreeForScriptBackground(
        AssetItem assetItem,
        AssetStudio.Object? asset,
        string? assemblyName,
        string? ns,
        string? className,
        ProjectScanResult? scanSnapshot,
        string? cacheFolder,
        CancellationToken token)
    {
        // 1. Direct TypeTree on selected MonoBehaviour (zero extra I/O)
        if (asset is MonoBehaviour directMb && directMb.serializedType?.m_Type?.m_Nodes is { Count: > 1 })
        {
            return (directMb.serializedType.m_Type, directMb.assetsFile?.fileName);
        }

        var cleanClass = className?.Trim() ?? string.Empty;
        var cleanNs = ns?.Trim() ?? string.Empty;
        var cleanAsm = NormalizeAssemblyShortName(assemblyName);

        // 2. Scan already-loaded SerializedFiles (m_RefTypes / m_Types / loaded MonoBehaviours referencing this MonoScript)
        var targetScriptPathId = asset is MonoScript msObj ? msObj.m_PathID : (assetItem.Type == ClassIDType.MonoScript ? assetItem.PathID : 0L);
        var targetScriptFile = asset?.assetsFile ?? assetItem.SourceFile;
        var targetScriptFileName = targetScriptFile?.fileName ?? assetItem.Handle?.SerializedFileName ?? string.Empty;

        var loadedFiles = assetsManager.assetsFileList.ToArray();
        for (var f = 0; f < loadedFiles.Length; f++)
        {
            token.ThrowIfCancellationRequested();
            var file = loadedFiles[f];
            if (file == null)
            {
                continue;
            }

            // 2a. Check SerializedType metadata tables (m_Types / m_RefTypes)
            var matchedType = TryMatchSerializedTypeTree(file.m_Types, cleanClass, cleanNs, cleanAsm)
                ?? TryMatchSerializedTypeTree(file.m_RefTypes, cleanClass, cleanNs, cleanAsm);
            if (matchedType != null)
            {
                return (matchedType, file.fileName);
            }

            // 2b. Check loaded MonoBehaviour objects in this SerializedFile referencing the selected MonoScript
            if (targetScriptPathId != 0)
            {
                AssetStudio.Object[] objectsSnapshot;
                lock (file)
                {
                    objectsSnapshot = file.Objects.ToArray();
                }

                for (var i = 0; i < objectsSnapshot.Length; i++)
                {
                    if (objectsSnapshot[i] is not MonoBehaviour candidateMb
                        || candidateMb.serializedType?.m_Type?.m_Nodes is not { Count: > 1 }
                        || candidateMb.m_Script == null
                        || candidateMb.m_Script.IsNull
                        || candidateMb.m_Script.m_PathID != targetScriptPathId)
                    {
                        continue;
                    }

                    if (DoesPPtrTargetFile(file, candidateMb.m_Script.m_FileID, targetScriptFile, targetScriptFileName))
                    {
                        return (candidateMb.serializedType.m_Type, file.fileName);
                    }
                }
            }
        }

        // 3. Lazy Mode: query SQLite semantic cache for 1 MonoBehaviour referencing this MonoScript and materialize ONLY that handle
        if (assetsManager.LazyLoading
            && assetItem.Type == ClassIDType.MonoScript
            && scanSnapshot != null
            && !string.IsNullOrWhiteSpace(cacheFolder))
        {
            try
            {
                var selfUid = asset?.assetsFile != null
                    ? AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID)
                    : (assetItem.Handle?.UniqueID ?? assetItem.UniqueID);
                if (!string.IsNullOrWhiteSpace(selfUid))
                {
                    var signature = _sqliteCache.GetFolderSignature(scanSnapshot);
                    var usedBy = new List<AssetReferenceItem>();
                    var uses = new List<AssetReferenceItem>();
                    _sqliteCache.LoadAssetReferencesFromSemanticCache(cacheFolder, signature, selfUid, usedBy, uses, maxPerDirection: 32);

                    foreach (var refItem in usedBy)
                    {
                        token.ThrowIfCancellationRequested();
                        if (refItem.AssetType != ClassIDType.MonoBehaviour || string.IsNullOrWhiteSpace(refItem.AssetUniqueId))
                        {
                            continue;
                        }

                        var mbHandle = assetsManager.ProjectIndex.GetHandle(refItem.AssetUniqueId);
                        if (mbHandle == null)
                        {
                            continue;
                        }

                        if (mbHandle.RealObject is MonoBehaviour cachedMb && cachedMb.serializedType?.m_Type?.m_Nodes is { Count: > 1 })
                        {
                            return (cachedMb.serializedType.m_Type, cachedMb.assetsFile?.fileName);
                        }

                        var tempMbItem = new AssetItem(mbHandle, 0);
                        EnsureLazyAssetReadyForPreview(tempMbItem);
                        if (tempMbItem.Asset is MonoBehaviour loadedMb && loadedMb.serializedType?.m_Type?.m_Nodes is { Count: > 1 })
                        {
                            return (loadedMb.serializedType.m_Type, loadedMb.assetsFile?.fileName);
                        }

                        // Stop after trying the first referencing MonoBehaviour to avoid I/O amplification
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best-effort fallback; never break script resolution
            }
        }

        return (null, null);
    }

    private static TypeTree? TryMatchSerializedTypeTree(
        IReadOnlyList<SerializedType>? types,
        string cleanClass,
        string cleanNs,
        string cleanAsm)
    {
        if (types == null || types.Count == 0 || string.IsNullOrWhiteSpace(cleanClass))
        {
            return null;
        }

        TypeTree? looseMatch = null;
        for (var i = 0; i < types.Count; i++)
        {
            var st = types[i];
            if (st?.m_Type?.m_Nodes is not { Count: > 1 } || string.IsNullOrWhiteSpace(st.m_KlassName))
            {
                continue;
            }

            if (!string.Equals(st.m_KlassName, cleanClass, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nsMatch = string.IsNullOrEmpty(cleanNs)
                || string.Equals(st.m_NameSpace ?? string.Empty, cleanNs, StringComparison.OrdinalIgnoreCase);
            var asmMatch = string.IsNullOrEmpty(cleanAsm)
                || string.Equals(NormalizeAssemblyShortName(st.m_AsmName), cleanAsm, StringComparison.OrdinalIgnoreCase);

            if (nsMatch && asmMatch)
            {
                return st.m_Type;
            }

            if (nsMatch)
            {
                looseMatch ??= st.m_Type;
            }
        }

        return looseMatch;
    }

    private static bool DoesPPtrTargetFile(
        SerializedFile ownerFile,
        int fileId,
        SerializedFile? targetFile,
        string targetFileName)
    {
        if (fileId == 0)
        {
            return ReferenceEquals(ownerFile, targetFile)
                || (!string.IsNullOrWhiteSpace(targetFileName)
                    && string.Equals(ownerFile.fileName, targetFileName, StringComparison.OrdinalIgnoreCase));
        }

        if (fileId > 0 && ownerFile.m_Externals != null && fileId - 1 < ownerFile.m_Externals.Count)
        {
            var ext = ownerFile.m_Externals[fileId - 1];
            var extName = NormalizeExternalFileNameForInspector(ext?.fileName, ext?.pathName);
            return !string.IsNullOrWhiteSpace(targetFileName)
                && string.Equals(extName, targetFileName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string NormalizeAssemblyShortName(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return string.Empty;
        }

        var trimmed = Path.GetFileName(assemblyName.Trim());
        return trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(0, trimmed.Length - 4)
            : trimmed;
    }

    private ScriptLookupRequest BuildScriptLookupRequest(
        string? assemblyName,
        string? ns,
        string? className,
        string? assetName,
        TypeTree? fallbackTypeTree = null,
        string? fallbackTypeTreeSourceFile = null)
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
            ScriptingBackend = report?.ScriptingBackend ?? UnityScriptingBackend.Unknown,
            FallbackTypeTree = fallbackTypeTree,
            FallbackTypeTreeSourceFile = fallbackTypeTreeSourceFile
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
            title: "Select Scripts Folder (Managed / dump.cs / DummyDll)"));
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _customScriptsDirectory = folder;
        _assemblyExplorerLoadedSourceKey = null;
        _scriptViewerService.ClearCaches();
        StatusStripUpdate($"Scripts folder set to: {folder}");

        if (RightTabControl.SelectedIndex == 3)
        {
            EnsureAssemblyExplorerLoadedIfNeeded(forceReload: true);
            await UpdateScriptForSelectedAssetAsync();
        }
        else if (AssetListDataGrid.SelectedItem is AssetItem selected && selected.Type == ClassIDType.MonoScript)
        {
            PreviewSelectedAssetImmediately();
        }
    }

    private DiscoveredScriptSources DiscoverCurrentScriptSources(out string sourceKey)
    {
        var projectRoot = ResolveEffectiveScriptProjectRoot();
        var report = CurrentProjectAnalysisReport;
        var projectAnalysisCacheDir = !string.IsNullOrWhiteSpace(projectRoot)
            ? ProjectAnalysisService.Shared.GetProjectCacheDirectory(projectRoot)
            : null;

        sourceKey = string.Join("|",
            projectRoot ?? string.Empty,
            _customScriptsDirectory ?? string.Empty,
            _lastLoadedAssemblyDirectory ?? string.Empty,
            report?.GlobalMetadataPath ?? string.Empty,
            report?.GameAssemblyPath ?? string.Empty);

        return ScriptSourceDiscovery.Discover(
            projectRoot: projectRoot,
            customScriptsDirectory: _customScriptsDirectory,
            knownIl2CppArtifacts: report?.Il2CppDumperArtifactPaths,
            gameAssemblyPath: report?.GameAssemblyPath,
            loadedAssemblyDirectory: _lastLoadedAssemblyDirectory,
            globalMetadataPath: report?.GlobalMetadataPath,
            projectAnalysisCacheDirectory: projectAnalysisCacheDir);
    }

    private void EnsureAssemblyExplorerLoadedIfNeeded(bool forceReload = false)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var discovered = DiscoverCurrentScriptSources(out var sourceKey);
        if (!forceReload && string.Equals(_assemblyExplorerLoadedSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _assemblyExplorerLoadedSourceKey = sourceKey;
        var token = PreviewSession.BeginAssemblyExplorerLoad(out var requestId);

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var catalog = _assemblyExplorerService.ScanDiscoveredSources(discovered, token);
                if (token.IsCancellationRequested || !PreviewSession.IsAssemblyExplorerRequestCurrent(requestId, token))
                {
                    return;
                }

                PreviewSession.CompleteAssemblyExplorerLoad(requestId, catalog);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (PreviewSession.IsAssemblyExplorerRequestCurrent(requestId, token))
                {
                    PreviewSession.FailAssemblyExplorerLoad(requestId, ex.Message);
                }
            }
        }, token);
    }

    private async Task SelectAssemblyExplorerTypeCommandHandlerAsync(AssemblyTypeEntry? entry)
    {
        if (IsWindowClosingOrClosed || entry == null)
        {
            return;
        }

        var token = PreviewSession.BeginScriptRequest(out var requestId, entry.FullName);
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);

        try
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var discovered = DiscoverCurrentScriptSources(out _);
                var managedDirs = new List<string>(discovered.ManagedDirectories);
                var dummyDirs = new List<string>(discovered.DummyDllDirectories);

                var entryDir = !string.IsNullOrWhiteSpace(entry.AssemblyPath)
                    ? Path.GetDirectoryName(entry.AssemblyPath)
                    : null;
                if (!string.IsNullOrWhiteSpace(entryDir) && Directory.Exists(entryDir))
                {
                    if (entry.SourceKind == ScriptSourceKind.Il2CppDummyAssemblyDecompiled)
                    {
                        dummyDirs.Insert(0, entryDir);
                    }
                    else if (entry.SourceKind == ScriptSourceKind.MonoAssemblyDecompiled)
                    {
                        managedDirs.Insert(0, entryDir);
                    }
                }

                var request = new ScriptLookupRequest
                {
                    AssemblyName = entry.AssemblyFileName,
                    Namespace = entry.Namespace,
                    ClassName = entry.ClassName,
                    AssetName = entry.ClassName,
                    ProjectRoot = ResolveEffectiveScriptProjectRoot(),
                    CustomScriptsDirectory = _customScriptsDirectory,
                    ManagedDirectories = managedDirs,
                    DummyDllDirectories = dummyDirs,
                    DumpCsPath = entry.SourceKind == ScriptSourceKind.Il2CppDumpCs && File.Exists(entry.AssemblyPath)
                        ? entry.AssemblyPath
                        : discovered.DumpCsPath,
                    ScriptJsonPath = discovered.ScriptJsonPath,
                    Il2CppHeaderPath = discovered.Il2CppHeaderPath,
                    GameAssemblyPath = discovered.GameAssemblyPath,
                    GlobalMetadataPath = entry.SourceKind == ScriptSourceKind.Il2CppGlobalMetadata && File.Exists(entry.AssemblyPath)
                        ? entry.AssemblyPath
                        : discovered.GlobalMetadataPath,
                    ScriptingBackend = CurrentProjectAnalysisReport?.ScriptingBackend ?? UnityScriptingBackend.Unknown
                };

                return _scriptViewerService.ResolveScript(request, token);
            }, token);

            if (token.IsCancellationRequested || !PreviewSession.IsScriptRequestCurrent(requestId, token))
            {
                return;
            }

            PreviewSession.CompleteScriptRequest(requestId, result);
            StatusStripUpdate(result.Success
                ? $"Script loaded: {result.Title} ({result.BackendBadge})"
                : $"Script source not found for {entry.FullName}.");

            RefreshScriptUsagesAsync(entry.AssemblyName, entry.Namespace, entry.ClassName, selectedMonoScriptHandle: null, selectedMonoScript: null);
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
    }

    private void RefreshScriptUsagesAsync(
        string? assemblyName,
        string? ns,
        string? className,
        AssetHandle? selectedMonoScriptHandle,
        MonoScript? selectedMonoScript)
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(className) && selectedMonoScript == null && selectedMonoScriptHandle == null)
        {
            PreviewSession.ClearScriptUsages();
            return;
        }

        var token = PreviewSession.BeginScriptUsageRequest(out var requestId);
        var loadedFilesSnapshot = GetLoadedFilesSnapshot();
        var scanSnapshot = currentScanResult;
        var cacheFolder = GetCurrentCacheFolderPath();
        var isLazy = assetsManager.LazyLoading;

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                Action<string, List<AssetReferenceItem>, List<AssetReferenceItem>, int>? semanticLoader = null;
                if (scanSnapshot != null && !string.IsNullOrWhiteSpace(cacheFolder))
                {
                    var signature = _sqliteCache.GetFolderSignature(scanSnapshot);
                    semanticLoader = (uid, usedByOut, usesOut, limit) =>
                        _sqliteCache.LoadAssetReferencesFromSemanticCache(cacheFolder, signature, uid, usedByOut, usesOut, limit);
                }

                Func<AssetHandle, MonoScript?>? lazyVerifier = null;
                if (isLazy)
                {
                    lazyVerifier = handle =>
                    {
                        if (handle.RealObject is MonoScript cached)
                        {
                            return cached;
                        }
                        var tempItem = new AssetItem(handle, 0);
                        EnsureLazyAssetReadyForPreview(tempItem);
                        return tempItem.Asset as MonoScript;
                    };
                }

                var ctx = new ScriptUsageLookupContext
                {
                    ProjectIndex = assetsManager.ProjectIndex,
                    LoadedFiles = loadedFilesSnapshot,
                    SemanticCacheLoader = semanticLoader,
                    BoundedLazyMonoScriptLoader = lazyVerifier,
                    MaxResults = 250
                };

                var usageResult = ScriptUsageLookupService.FindUsages(
                    assemblyName,
                    ns,
                    className,
                    selectedMonoScriptHandle,
                    selectedMonoScript,
                    ctx,
                    token);

                if (token.IsCancellationRequested || !PreviewSession.IsScriptUsageRequestCurrent(requestId, token))
                {
                    return;
                }

                PreviewSession.CompleteScriptUsageRequest(requestId, usageResult);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (PreviewSession.IsScriptUsageRequestCurrent(requestId, token))
                {
                    PreviewSession.CompleteScriptUsageRequest(requestId, ScriptUsageLookupResult.Empty);
                }
            }
        }, token);
    }

    private async Task NavigateToScriptUsageCommandHandlerAsync(ScriptUsageItem? usage)
    {
        if (IsWindowClosingOrClosed || usage == null || !usage.IsNavigable)
        {
            return;
        }

        await NavigateToReferencedAssetCommandHandlerAsync(usage.ToAssetReferenceItem());
    }

    private async Task ExportCSharpProjectCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var discovered = DiscoverCurrentScriptSources(out _);
        if (!discovered.HasAnySource)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Export C# Script Project",
                "No managed assemblies, IL2CPP DummyDlls, dump.cs, or global-metadata.dat were detected for this project.\n\nUse 'Scripts Folder...' in the Script tab to point to a Managed or Il2CppDumper folder first.");
            return;
        }

        var outFolder = await ViewModel.DialogService.PickOpenFolderAsync(new FolderPickerRequest(
            title: "Select Output Folder for C# Script Project (.sln/.csproj)"));
        if (string.IsNullOrWhiteSpace(outFolder))
        {
            return;
        }

        var projectRoot = ResolveEffectiveScriptProjectRoot();
        var rawProjectName = !string.IsNullOrWhiteSpace(projectContext?.Project.Name)
            ? projectContext!.Project.Name
            : (!string.IsNullOrWhiteSpace(projectRoot)
                ? Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : "ExportedScripts");
        var projectName = ScriptExportPathSanitizer.SanitizeSegment(rawProjectName, "ExportedScripts");

        var options = new CSharpProjectExportOptions
        {
            OutputDirectory = outFolder,
            ProjectName = projectName,
            LayoutMode = CSharpProjectLayoutMode.StandaloneDotNetSolution,
            IncludeEngineAndThirdPartyAssemblies = false,
            GenerateUnityMetaFiles = true,
            GenerateAsmdefFiles = true,
            CopyReferenceDlls = true,
            ManagedDirectories = discovered.ManagedDirectories,
            DummyDllDirectories = discovered.DummyDllDirectories,
            DumpCsPath = discovered.DumpCsPath,
            ScriptJsonPath = discovered.ScriptJsonPath,
            GlobalMetadataPath = discovered.GlobalMetadataPath,
            GameAssemblyPath = discovered.GameAssemblyPath,
            ProjectRoot = projectRoot,
            CustomScriptsDirectory = _customScriptsDirectory
        };

        var progress = new Progress<CSharpProjectExportProgress>(p =>
        {
            StatusStripUpdate($"Exporting C# project: {p.StatusMessage} ({p.CompletedTypes:N0} types)");
        });

        StatusStripUpdate($"Exporting C# script project '{projectName}'...");
        CSharpProjectExportResult exportResult;
        try
        {
            exportResult = await _csharpProjectExporter.ExportAsync(options, progress);
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"C# project export failed: {ex.Message}");
            await ViewModel.DialogService.ShowErrorAsync("Export C# Script Project Failed", ex.Message);
            return;
        }

        if (exportResult.Success)
        {
            StatusStripUpdate(exportResult.SummaryMessage);
            var details = new StringBuilder();
            details.AppendLine(exportResult.SummaryMessage);
            details.AppendLine();
            details.AppendLine($"Mode: {exportResult.Mode}");
            details.AppendLine($"Game Assemblies: {exportResult.GameAssembliesCount:N0}");
            details.AppendLine($"Reference DLLs: {exportResult.ReferenceAssembliesCount:N0}");
            details.AppendLine($"Types Exported: {exportResult.TotalTypesExported:N0}");
            if (exportResult.FailedTypesCount > 0)
            {
                details.AppendLine($"Stub Fallbacks (Decompile Errors): {exportResult.FailedTypesCount:N0}");
            }
            if (exportResult.BrokenCycles.Count > 0)
            {
                details.AppendLine($"Dependency Cycles Broken: {exportResult.BrokenCycles.Count:N0}");
            }
            if (!string.IsNullOrWhiteSpace(exportResult.SolutionPath))
            {
                details.AppendLine($"Solution: {exportResult.SolutionPath}");
            }
            details.AppendLine($"Report: {exportResult.ReportPath}");

            await ViewModel.DialogService.ShowMessageAsync("C# Script Project Export Complete", details.ToString().TrimEnd());
        }
        else
        {
            StatusStripUpdate($"C# project export failed: {exportResult.ErrorMessage}");
            await ViewModel.DialogService.ShowErrorAsync("Export C# Script Project Failed", exportResult.ErrorMessage);
        }
    }

    private void ResetScriptDecompilerCaches()
    {
        _scriptRequestCoordinator.CancelCurrent();
        PreviewSession.AssemblyExplorerCoordinator.CancelCurrent();
        PreviewSession.ScriptUsageCoordinator.CancelCurrent();
        _customScriptsDirectory = null;
        _lastLoadedAssemblyDirectory = null;
        _assemblyExplorerLoadedSourceKey = null;
        _scriptViewerService.ClearCaches();
        _suppressAssemblyTypeSelection = true;
        try
        {
            PreviewSession.ClearScript();
            PreviewSession.ClearAssemblyExplorer();
        }
        finally
        {
            _suppressAssemblyTypeSelection = false;
        }
    }

    private void DisposeScriptDecompilerService()
    {
        if (_scriptExplorerEventsWired)
        {
            _scriptExplorerEventsWired = false;
            PreviewSession.PropertyChanged -= OnPreviewSessionPropertyChangedForScriptExplorer;
        }
        _scriptRequestCoordinator.CancelCurrent();
        PreviewSession.AssemblyExplorerCoordinator.CancelCurrent();
        PreviewSession.ScriptUsageCoordinator.CancelCurrent();
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
