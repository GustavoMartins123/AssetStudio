using Avalonia.Controls;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

internal sealed class AsyncPreviewRequestCoordinator : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;
    private long _currentRequestId;
    private long _cancelledRequestCount;

    public long CurrentRequestId => Interlocked.Read(ref _currentRequestId);
    public long CancelledRequestCount => Interlocked.Read(ref _cancelledRequestCount);

    public CancellationToken BeginNewRequest(out long requestId)
    {
        lock (_gate)
        {
            CancelAndDisposeActiveLocked();
            _activeCts = new CancellationTokenSource();
            requestId = Interlocked.Increment(ref _currentRequestId);
            return _activeCts.Token;
        }
    }

    public void CancelCurrent()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _currentRequestId);
            CancelAndDisposeActiveLocked();
        }
    }

    public bool IsCurrent(long requestId)
    {
        return Interlocked.Read(ref _currentRequestId) == requestId;
    }

    public async Task<(bool Applied, long RequestId, T? Result)> RunLatestAsync<T>(
        Func<long, CancellationToken, Task<T>> backgroundWork,
        Action<long, T>? onCompleted = null)
    {
        var token = BeginNewRequest(out var requestId);
        try
        {
            var result = await Task.Run(() => backgroundWork(requestId, token), token).ConfigureAwait(false);
            if (token.IsCancellationRequested || !IsCurrent(requestId))
            {
                return (false, requestId, default);
            }

            onCompleted?.Invoke(requestId, result);
            return (true, requestId, result);
        }
        catch (OperationCanceledException)
        {
            return (false, requestId, default);
        }
    }

    private void CancelAndDisposeActiveLocked()
    {
        if (_activeCts == null)
        {
            return;
        }

        if (!_activeCts.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancelledRequestCount);
            try
            {
                _activeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _activeCts.Dispose();
        _activeCts = null;
    }

    public void Dispose()
    {
        CancelCurrent();
    }
}

public partial class MainWindow : Window
{
    private const int MaxPreviewTextChars = PreviewSessionViewModel.MaxPreviewTextChars;
    internal PreviewSessionViewModel PreviewSession => ViewModel.PreviewSession;
    private AsyncPreviewRequestCoordinator _previewRequestCoordinator => PreviewSession.PreviewCoordinator;
    private AsyncPreviewRequestCoordinator _dumpRequestCoordinator => PreviewSession.DumpCoordinator;
    private readonly SemaphoreSlim _assemblyLoaderGate = new(1, 1);
    private string? _lastPreviewedAssetUniqueId;
    private bool _suppressNextRightTabSelectionPreview;

    private CancellationToken BeginAsyncPreviewRequest(out long requestId, out long previewVersion)
    {
        previewVersion = Volatile.Read(ref texturePreviewIdCounter);
        PreviewSession.SyncPreviewVersion(previewVersion);
        return _previewRequestCoordinator.BeginNewRequest(out requestId);
    }

    private bool IsPreviewRequestCurrent(long requestId, long previewVersion, CancellationToken token)
    {
        return !token.IsCancellationRequested
            && _previewRequestCoordinator.IsCurrent(requestId)
            && Volatile.Read(ref texturePreviewIdCounter) == previewVersion;
    }

    internal static string TruncatePreviewText(string? text, string fallbackText = "")
    {
        return PreviewSessionViewModel.TruncatePreviewText(text, fallbackText);
    }

    private async void AssetListDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingFilterList) return;

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var selectedItem = sender is DataGrid grid ? grid.SelectedItem : AssetListDataGrid.SelectedItem;
        if (selectedItem is AssetItem assetItem)
        {
            var id = assetItem.Handle != null ? assetItem.Handle.UniqueID : assetItem.UniqueID;
            if (!string.Equals(_currentlySelectedUniqueID, id, StringComparison.Ordinal))
            {
                PreviewSession.ShaderPreview.Clear();
            }
            _currentlySelectedUniqueID = id;

            if (RightTabControl.SelectedIndex == 1)
            {
                await UpdateDumpForSelectedAsset();
            }
            else if (RightTabControl.SelectedIndex == 2)
            {
                await UpdateReferencesForSelectedAssetAsync();
            }
            else if (RightTabControl.SelectedIndex == 3)
            {
                await UpdateScriptForSelectedAssetAsync();
            }
            UpdatePreloadWindow(assetItem);
            QueuePreviewAsset(assetItem);
        }
        else
        {
            _currentlySelectedUniqueID = null;
            _dumpRequestCoordinator.CancelCurrent();
            _referencesRequestCoordinator.CancelCurrent();
            _scriptRequestCoordinator.CancelCurrent();
            PreviewSession.DumpTextContent = string.Empty;
            PreviewSession.ClearReferences();
            PreviewSession.ClearScript();
            CancelPendingPreview();
            ClearPreview("Preview Panel");
            lock (preloaderLock)
            {
                preloaderCts?.Cancel();
            }
        }
    }

    private async void RightTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == RightTabControl)
        {
            PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
            if (RightTabControl.SelectedIndex == 1)
            {
                PreviewSession.ActiveRightTab = RightPanelTab.Dump;
                _referencesRequestCoordinator.CancelCurrent();
                _scriptRequestCoordinator.CancelCurrent();
                if (PreviewSession.IsLoading)
                {
                    _lastPreviewedAssetUniqueId = null;
                }
                CancelPendingPreview();
                GLPreviewControl?.SuspendForHiddenTab();
                await UpdateDumpForSelectedAsset();
            }
            else if (RightTabControl.SelectedIndex == 2)
            {
                PreviewSession.ActiveRightTab = RightPanelTab.References;
                _dumpRequestCoordinator.CancelCurrent();
                _scriptRequestCoordinator.CancelCurrent();
                if (PreviewSession.IsLoading)
                {
                    _lastPreviewedAssetUniqueId = null;
                }
                CancelPendingPreview();
                GLPreviewControl?.SuspendForHiddenTab();
                await UpdateReferencesForSelectedAssetAsync();
            }
            else if (RightTabControl.SelectedIndex == 3)
            {
                PreviewSession.ActiveRightTab = RightPanelTab.Script;
                _dumpRequestCoordinator.CancelCurrent();
                _referencesRequestCoordinator.CancelCurrent();
                if (PreviewSession.IsLoading)
                {
                    _lastPreviewedAssetUniqueId = null;
                }
                CancelPendingPreview();
                GLPreviewControl?.SuspendForHiddenTab();
                await UpdateScriptForSelectedAssetAsync();
            }
            else if (RightTabControl.SelectedIndex == 0)
            {
                PreviewSession.ActiveRightTab = RightPanelTab.Preview;
                _dumpRequestCoordinator.CancelCurrent();
                _referencesRequestCoordinator.CancelCurrent();
                _scriptRequestCoordinator.CancelCurrent();
                if (_suppressNextRightTabSelectionPreview)
                {
                    _suppressNextRightTabSelectionPreview = false;
                    return;
                }

                if (!string.IsNullOrEmpty(_currentlySelectedUniqueID)
                    && string.Equals(_lastPreviewedAssetUniqueId, _currentlySelectedUniqueID, StringComparison.Ordinal))
                {
                    GLPreviewControl?.ResumeAfterHiddenTab();
                    TextureGLPreview?.RequestNextFrameRendering();
                }
                else
                {
                    PreviewSelectedAssetImmediately();
                }
            }
        }
    }

    private void CancelPendingPreview()
    {
        previewDebounce?.Cancel();
        CancelVideoPreviewLoad();
        PreviewSession.CancelPendingPreview();
        var nextVersion = Interlocked.Increment(ref texturePreviewIdCounter);
        PreviewSession.SyncPreviewVersion(nextVersion);
    }

    private void PreviewSelectedAssetImmediately()
    {
        if (AssetListDataGrid.SelectedItem is not AssetItem assetItem)
        {
            ClearPreview("Preview Panel");
            return;
        }

        CancelPendingPreview();
        QueuePreviewAsset(assetItem, immediate: true);
    }

    private void QueuePreviewAsset(AssetItem assetItem, bool immediate = false)
    {
        if (RightTabControl.SelectedIndex != 0)
        {
            return;
        }

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        previewDebounce?.Cancel();
        previewDebounce?.Dispose();
        previewDebounce = new CancellationTokenSource();
        var token = previewDebounce.Token;
        _previewRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        OnPreviewAssetQueuedForVideo(assetItem);
        var previewId = Interlocked.Increment(ref texturePreviewIdCounter);

        if (PreviewLabel != null)
        {
            PreviewLabel.IsVisible = ViewModel.Options.DisplayInfo;
            PreviewLabel.Text = ViewModel.Options.DisplayInfo ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate)
                {
                    await Task.Delay(PreviewDebounceMilliseconds, token);
                }

                PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
                var resolvedAsset = await Task.Run(() => ResolveAssetForPreview(assetItem), token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested
                        && previewId == Volatile.Read(ref texturePreviewIdCounter)
                        && RightTabControl.SelectedIndex == 0
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        PreviewAsset(assetItem, resolvedAsset, assetResolutionAttempted: true);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Preview queue failed for {assetItem.Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (previewId == Volatile.Read(ref texturePreviewIdCounter)
                        && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        StatusStripUpdate($"Preview error: {ex.Message}");
                    }
                });
            }
        }, token);
    }

    private void SetTextWithTruncation(TextBox textBox, string? text, string fallbackText = "")
    {
        textBox.Text = TruncatePreviewText(text, fallbackText);
    }

    private async Task UpdateDumpForSelectedAsset()
    {
        if (AssetListDataGrid.SelectedItem is not AssetItem assetItem)
        {
            PreviewSession.CancelPendingDump();
            PreviewSession.DumpTextContent = string.Empty;
            return;
        }

        var token = PreviewSession.BeginDumpRequest(out var requestId, "Loading dump...");
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var shouldPauseIndexing = assetsManager.LazyLoading && assetItem.Handle != null;
        if (shouldPauseIndexing)
        {
            Interlocked.Increment(ref foregroundLazyLoadCount);
        }
        try
        {
            var asset = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (assetsManager.LazyLoading && assetItem.Handle != null)
                {
                    EnsureLazyAssetReadyForPreview(assetItem);
                }

                token.ThrowIfCancellationRequested();
                return assetItem.Asset;
            }, token);

            if (token.IsCancellationRequested
                || !_dumpRequestCoordinator.IsCurrent(requestId)
                || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }

            if (asset == null)
            {
                PreviewSession.CompleteDumpRequest(requestId, null, "No Dump Available", truncate: false);
                return;
            }

            var dump = await DumpAsset(asset, token);
            var truncatedDump = await Task.Run(() => TruncatePreviewText(dump, "No Dump Available"), token);
            if (token.IsCancellationRequested
                || !_dumpRequestCoordinator.IsCurrent(requestId)
                || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }
            PreviewSession.CompleteDumpRequest(requestId, truncatedDump, "No Dump Available", truncate: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_dumpRequestCoordinator.IsCurrent(requestId)
                || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }
            var dumpError = $"Dump {assetItem.Type}:{assetItem.Name} error{Environment.NewLine}{ex.Message}{Environment.NewLine}{ex.StackTrace}";
            PreviewSession.FailDumpRequest(requestId, dumpError);
        }
        finally
        {
            if (shouldPauseIndexing)
            {
                Interlocked.Decrement(ref foregroundLazyLoadCount);
            }
        }
    }

    private async Task<string?> DumpAsset(Object asset, CancellationToken cancellationToken = default)
    {
        if (asset is MonoBehaviour monoBehaviour)
        {
            var dump = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return monoBehaviour.Dump();
            }, cancellationToken);

            if (dump == null)
            {
                var typeTree = await MonoBehaviourToTypeTree(monoBehaviour, cancellationToken);
                dump = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return monoBehaviour.Dump(typeTree);
                }, cancellationToken);
            }
            return dump;
        }
        else
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return asset.Dump();
            }, cancellationToken);
        }
    }

    private async Task EnsureAssemblyLoaderReadyAsync(CancellationToken cancellationToken = default)
    {
        if (assemblyLoader.Loaded)
        {
            return;
        }

        await _assemblyLoaderGate.WaitAsync(cancellationToken);
        try
        {
            if (assemblyLoader.Loaded)
            {
                return;
            }

            string? selectedFolder = TryAutoDiscoverManagedAssemblyDirectory();
            if (string.IsNullOrEmpty(selectedFolder))
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    selectedFolder = await PickAssemblyFolderOnUiThreadAsync();
                }
                else
                {
                    selectedFolder = await Dispatcher.UIThread.InvokeAsync(PickAssemblyFolderOnUiThreadAsync);
                }
            }
            else
            {
                _lastLoadedAssemblyDirectory = selectedFolder;
            }

            if (!string.IsNullOrEmpty(selectedFolder))
            {
                await Task.Run(() =>
                {
                    lock (assemblyLoader)
                    {
                        if (!assemblyLoader.Loaded)
                        {
                            assemblyLoader.Load(selectedFolder);
                        }
                    }
                }, cancellationToken);
            }
            else
            {
                lock (assemblyLoader)
                {
                    assemblyLoader.Loaded = true;
                }
            }
        }
        finally
        {
            _assemblyLoaderGate.Release();
        }
    }

    private string? TryAutoDiscoverManagedAssemblyDirectory()
    {
        static bool DirectoryHasDlls(string dir)
        {
            try
            {
                using var enumerator = Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).GetEnumerator();
                return enumerator.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        var roots = new[]
        {
            _currentProjectAnalysis?.Report?.BackupManagedDirectory,
            GetCurrentCacheFolderPath(),
            assetsManager.ProjectRoot
        };

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            if (i == 0 && DirectoryHasDlls(root))
            {
                return root;
            }

            var directManaged = Path.Combine(root, "Managed");
            if (Directory.Exists(directManaged) && DirectoryHasDlls(directManaged))
            {
                return directManaged;
            }

            try
            {
                foreach (var dataDir in Directory.EnumerateDirectories(root, "*_Data", SearchOption.TopDirectoryOnly))
                {
                    var subManaged = Path.Combine(dataDir, "Managed");
                    if (Directory.Exists(subManaged) && DirectoryHasDlls(subManaged))
                    {
                        return subManaged;
                    }
                }
            }
            catch
            {
            }

            var backupDirs = Services.ProjectAnalysis.ProjectRuntimeDetector.DiscoverBackupManagedDirectories(root);
            for (var b = 0; b < backupDirs.Count; b++)
            {
                if (DirectoryHasDlls(backupDirs[b]))
                {
                    return backupDirs[b];
                }
            }
        }

        return null;
    }

    private async Task<string?> PickAssemblyFolderOnUiThreadAsync()
    {
        var localPath = await PickLoadFolderDialogAsync("Select Assembly Folder");
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            _lastLoadedAssemblyDirectory = localPath;
            SaveLoadFolder(localPath);
            return localPath;
        }

        return null;
    }

    private async Task<TypeTree> MonoBehaviourToTypeTree(MonoBehaviour monoBehaviour, CancellationToken cancellationToken = default)
    {
        await EnsureAssemblyLoaderReadyAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (assemblyLoader)
            {
                return monoBehaviour.ConvertToTypeTree(assemblyLoader);
            }
        }, cancellationToken);
    }

    private AssetStudio.Object? ResolveAssetForPreview(AssetItem assetItem)
    {
        var shouldPauseIndexing = assetsManager.LazyLoading && assetItem.Handle != null;
        if (shouldPauseIndexing)
        {
            Interlocked.Increment(ref foregroundLazyLoadCount);
        }

        try
        {
            if (assetsManager.LazyLoading && assetItem.Handle != null)
            {
                EnsureLazyAssetReadyForPreview(assetItem);
            }

            return assetItem.Asset;
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Error resolving asset for preview {assetItem.Name}: {ex}");
            return null;
        }
        finally
        {
            if (shouldPauseIndexing)
            {
                Interlocked.Decrement(ref foregroundLazyLoadCount);
            }
        }
    }

    private void PreviewPrefabInstanceAsync(AssetItem assetItem, Object prefab, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Prefab,
            assetItem,
            isLoading: true,
            loadingStatusText: $"Loading prefab preview: {assetItem.Name}...");
        PreviewSession.TextContent = "Loading prefab preview...";
        TextPreviewBox.Text = PreviewSession.TextContent;
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Loading prefab preview: {assetItem.Name}...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var formatted = FormatPrefabCore(prefab, token);
                token.ThrowIfCancellationRequested();
                var truncated = TruncatePreviewText(fbxHeader + formatted);

                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    PreviewSession.ShowTextPreview(truncated, PreviewSurfaceKind.Prefab, assetItem, truncate: false);
                    TextPreviewBox.Text = truncated;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Prefab preview loaded: {assetItem.Name}");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Prefab preview failed for {assetItem.Name}: {ex}");
                var errorText = TruncatePreviewText($"Failed to preview prefab {assetItem.Name}:{Environment.NewLine}{ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    PreviewSession.ShowTextPreview(errorText, PreviewSurfaceKind.Prefab, assetItem, truncate: false);
                    TextPreviewBox.Text = errorText;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Prefab preview error: {ex.Message}");
                });
            }
        }, token);
    }

    private void PreviewDefaultDumpAsync(AssetItem assetItem, Object asset, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Text,
            assetItem,
            isLoading: true,
            loadingStatusText: $"Loading asset preview: {assetItem.Name}...");
        PreviewSession.TextContent = "Loading asset preview...";
        TextPreviewBox.Text = PreviewSession.TextContent;
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                string? rawDump;
                try
                {
                    rawDump = asset.Dump();
                }
                catch (Exception dumpEx)
                {
                    rawDump = $"Failed to dump asset: {dumpEx.Message}";
                }

                token.ThrowIfCancellationRequested();
                string? truncated = rawDump != null ? TruncatePreviewText(fbxHeader + rawDump) : null;

                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    if (truncated != null)
                    {
                        PreviewSession.ShowTextPreview(truncated, PreviewSurfaceKind.Text, assetItem, truncate: false);
                        TextPreviewBox.Text = truncated;
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                    }
                    else
                    {
                        PreviewSession.ShowTextPreview(string.Empty, PreviewSurfaceKind.None, assetItem, truncate: false);
                        TextPreviewBox.Text = string.Empty;
                        TextPreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = ViewModel.Options.DisplayInfo;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Asset dump preview failed for {assetItem.Name}: {ex}");
                var errorText = TruncatePreviewText($"Failed to dump asset {assetItem.Name}:{Environment.NewLine}{ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    PreviewSession.ShowTextPreview(errorText, PreviewSurfaceKind.Text, assetItem, truncate: false);
                    TextPreviewBox.Text = errorText;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Preview error: {ex.Message}");
                });
            }
        }, token);
    }

    private void PreviewAsset(AssetItem assetItem, AssetStudio.Object? resolvedAsset = null, bool assetResolutionAttempted = false)
    {
        _previewRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        PreviewSession.ShaderPreview.Clear();
        Interlocked.Increment(ref texturePreviewIdCounter);
        _lastPreviewedAssetUniqueId = assetItem.Handle != null ? assetItem.Handle.UniqueID : assetItem.UniqueID;
        if (!ViewModel.Options.EnablePreview)
        {
            ClearPreview("Preview disabled");
            return;
        }

        var asset = assetResolutionAttempted ? resolvedAsset : (resolvedAsset ?? ResolveAssetForPreview(assetItem));
        if (asset == null)
        {
            ClearPreview("Preview Panel");
            SetTextWithTruncation(TextPreviewBox,
                $"Preview could not load this asset on demand.{Environment.NewLine}" +
                $"Asset: {assetItem.Name}{Environment.NewLine}" +
                $"Type: {assetItem.DisplayType}{Environment.NewLine}" +
                $"PathID: {assetItem.PathID}");
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate($"Preview unavailable for {assetItem.Name}.");
            return;
        }

        TextPreviewBox.IsVisible = false;
        TextPreviewBox.FontFamily = global::Avalonia.Media.FontFamily.Default;
        TextPreviewBox.FontSize = 14;
        ClearTextAssetPreview();
        if (ImagePreviewBox != null)
        {
            ImagePreviewBox.Source = null;
            ImagePreviewBox.IsVisible = false;
        }
        if (GLPreviewControl != null && asset is not Material)
        {
            GLPreviewControl.IsVisible = false;
        }
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }
        ClearPreviewInfoOverlay();
        if (asset is not AudioClip)
        {
            AudioReset();
        }
        if (asset is not VideoClip && asset is not VideoPlayer && asset is not MovieTexture)
        {
            VideoReset();
        }
        ClearMeshMaterialControls();
        ClearPreviewCandidateControls();
        ClearPreviewCacheBadge();
        PreviewSession.ClearShaderPreviewDiagnostic();
        Stop2DAnimation();
        HideAnimationPlayback();
        GLPreviewControl?.StopAnimation();
        currentPreviewMesh = null;
        currentPreviewAvatar = null;
        currentPreviewAnimationClip = null;
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        ResetTextureArrayPreview();
        ResetTextureMipmapPreview();
        currentPreviewAudioClip = null;
        currentPreviewVideoClip = null;

        PreviewLabel.IsVisible = ViewModel.Options.DisplayInfo;
        PreviewLabel.Text = ViewModel.Options.DisplayInfo ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;

        string fbxHeader = string.Empty;
        if (assetItem.DisplayType.Contains("FBX sub-asset"))
        {
            var fbxNodeName = assetItem.TreeNode != null ? assetItem.TreeNode.Name : "[None]";
            fbxHeader = $"[FBX Sub-Asset Container: {Path.GetFileName(assetItem.Container)}]" + Environment.NewLine +
                        $"Associated Scene Hierarchy Node: {fbxNodeName}" + Environment.NewLine +
                        $"(Right-click this item and choose 'Go to scene hierarchy' to view context)" + Environment.NewLine +
                        $"--------------------------------------------------" + Environment.NewLine + Environment.NewLine;
        }

        try
        {
            switch (asset)
            {
                case AudioClip m_AudioClip:
                    PreviewAudioClip(assetItem, m_AudioClip);
                    break;
                case Texture2D m_Texture2D:
                    PreviewTexture2D(assetItem, m_Texture2D);
                    break;
                case Texture2DArray m_Texture2DArray:
                    PreviewTexture2DArray(assetItem, m_Texture2DArray);
                    break;
                case Sprite m_Sprite:
                    PreviewSprite(assetItem, m_Sprite);
                    break;
                case AssetStudio.Font m_Font:
                    PreviewFont(assetItem, m_Font);
                    break;
                case Material m_Material:
                    PreviewMaterial(assetItem, m_Material);
                    break;
                case TextAsset m_TextAsset:
                    PreviewTextAsset(assetItem, m_TextAsset, fbxHeader);
                    break;
                case Shader m_Shader:
                    PreviewShader(assetItem, m_Shader);
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    PreviewMonoBehaviour(assetItem, m_MonoBehaviour, fbxHeader);
                    break;
                case MonoScript m_MonoScript:
                    PreviewMonoScript(assetItem, m_MonoScript);
                    break;
                case Mesh m_Mesh:
                    PreviewMesh(assetItem, m_Mesh);
                    break;
                case Object obj when obj.type == ClassIDType.PrefabInstance:
                    PreviewPrefabInstanceAsync(assetItem, obj, fbxHeader);
                    break;
                case VideoClip m_VideoClip:
                    PreviewVideoClip(assetItem, m_VideoClip);
                    break;
                case VideoPlayer m_VideoPlayer:
                    PreviewVideoPlayer(assetItem, m_VideoPlayer);
                    break;
                case MovieTexture m_MovieTexture:
                    PreviewMovieTexture(assetItem, m_MovieTexture);
                    break;
                case Animator m_Animator:
                    PreviewAnimatorGraph(m_Animator);
                    break;
                case AnimatorController m_AnimatorController:
                    PreviewAnimatorGraph(m_AnimatorController);
                    break;
                case AnimatorOverrideController m_AnimatorOverrideController:
                    PreviewAnimatorGraph(m_AnimatorOverrideController);
                    break;
                case Avatar m_Avatar:
                    PreviewAvatar(m_Avatar);
                    break;
                case AnimationClip m_AnimationClip:
                    PreviewAnimationClip(m_AnimationClip);
                    break;
                default:
                    PreviewDefaultDumpAsync(assetItem, asset, fbxHeader);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Error displaying preview for {assetItem.Name}: {ex.Message}");
            StatusStripUpdate($"Preview error: {ex.Message}");

            if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
            if (TextureGLPreview != null) TextureGLPreview.IsVisible = false;
            if (ImagePreviewBox != null)
            {
                ImagePreviewBox.Source = null;
                ImagePreviewBox.IsVisible = false;
            }
            ClearTextAssetPreview();

            var sb = new StringBuilder();
            sb.AppendLine($"Failed to load preview for asset: {assetItem.Name}");
            sb.AppendLine($"Type: {assetItem.DisplayType}");
            sb.AppendLine($"PathID: {assetItem.PathID}");
            sb.AppendLine();
            sb.AppendLine("Error details:");
            sb.AppendLine(ex.ToString());

            SetTextWithTruncation(TextPreviewBox, sb.ToString());
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
        }
    }
}
