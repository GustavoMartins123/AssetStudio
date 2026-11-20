using Avalonia.Controls;
using Avalonia.Threading;
using AssetStudio;
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
    private const int MaxPreviewTextChars = 100000;
    private readonly AsyncPreviewRequestCoordinator _previewRequestCoordinator = new();
    private readonly AsyncPreviewRequestCoordinator _dumpRequestCoordinator = new();
    private readonly SemaphoreSlim _assemblyLoaderGate = new(1, 1);

    private CancellationToken BeginAsyncPreviewRequest(out long requestId, out long previewVersion)
    {
        previewVersion = Volatile.Read(ref texturePreviewIdCounter);
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
        if (text == null)
        {
            return fallbackText;
        }

        if (text.Length > MaxPreviewTextChars)
        {
            return text.Substring(0, MaxPreviewTextChars) +
                $"{Environment.NewLine}...{Environment.NewLine}[Preview truncated: content is too large ({text.Length:N0} characters). Please export the asset to view full content]";
        }

        return text;
    }

    private async void AssetListDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingFilterList) return;

        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        var selectedItem = sender is DataGrid grid ? grid.SelectedItem : AssetListDataGrid.SelectedItem;
        if (selectedItem is AssetItem assetItem)
        {
            var id = assetItem.Handle != null ? assetItem.Handle.UniqueID : assetItem.UniqueID;
            _currentlySelectedUniqueID = id;

            if (RightTabControl.SelectedIndex == 1)
            {
                await UpdateDumpForSelectedAsset();
            }
            UpdatePreloadWindow(assetItem);
            QueuePreviewAsset(assetItem);
        }
        else
        {
            _currentlySelectedUniqueID = null;
            _dumpRequestCoordinator.CancelCurrent();
            DumpTextBox.Text = string.Empty;
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
                CancelPendingPreview();
                await UpdateDumpForSelectedAsset();
            }
            else if (RightTabControl.SelectedIndex == 0)
            {
                _dumpRequestCoordinator.CancelCurrent();
                PreviewSelectedAssetImmediately();
            }
        }
    }

    private void CancelPendingPreview()
    {
        previewDebounce?.Cancel();
        _previewRequestCoordinator.CancelCurrent();
        _animationPreviewCoordinator.CancelCurrent();
        Interlocked.Increment(ref texturePreviewIdCounter);
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
        var previewId = Interlocked.Increment(ref texturePreviewIdCounter);

        if (PreviewLabel != null)
        {
            PreviewLabel.IsVisible = displayInfo.IsChecked == true;
            PreviewLabel.Text = displayInfo.IsChecked == true ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;
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
            _dumpRequestCoordinator.CancelCurrent();
            DumpTextBox.Text = string.Empty;
            return;
        }

        var token = _dumpRequestCoordinator.BeginNewRequest(out var requestId);
        PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);
        DumpTextBox.Text = "Loading dump...";
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
                DumpTextBox.Text = "No Dump Available";
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
            DumpTextBox.Text = truncatedDump;
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
            DumpTextBox.Text = $"Dump {assetItem.Type}:{assetItem.Name} error{Environment.NewLine}{ex.Message}{Environment.NewLine}{ex.StackTrace}";
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

            string? selectedFolder = null;
            if (Dispatcher.UIThread.CheckAccess())
            {
                selectedFolder = await PickAssemblyFolderOnUiThreadAsync();
            }
            else
            {
                selectedFolder = await Dispatcher.UIThread.InvokeAsync(PickAssemblyFolderOnUiThreadAsync);
            }

            if (!string.IsNullOrEmpty(selectedFolder))
            {
                await Task.Run(() => assemblyLoader.Load(selectedFolder), cancellationToken);
            }
            else
            {
                assemblyLoader.Loaded = true;
            }
        }
        finally
        {
            _assemblyLoaderGate.Release();
        }
    }

    private async Task<string?> PickAssemblyFolderOnUiThreadAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select Assembly Folder"));
        if (folders != null && folders.Count > 0)
        {
            var localPath = folders[0].Path.LocalPath;
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
            return monoBehaviour.ConvertToTypeTree(assemblyLoader);
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

    private void PreviewShaderAsync(AssetItem assetItem, Shader m_Shader, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        TextPreviewBox.Text = "Loading shader preview...";
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Loading shader preview: {assetItem.Name}...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var converted = m_Shader.Convert() ?? "Serialized Shader can't be read";
                token.ThrowIfCancellationRequested();
                var truncated = TruncatePreviewText(fbxHeader + converted);

                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    TextPreviewBox.Text = truncated;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Shader preview loaded: {assetItem.Name}");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Shader preview failed for {assetItem.Name}: {ex}");
                var errorText = TruncatePreviewText($"Failed to preview shader {assetItem.Name}:{Environment.NewLine}{ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    TextPreviewBox.Text = errorText;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Shader preview error: {ex.Message}");
                });
            }
        }, token);
    }

    private void PreviewPrefabInstanceAsync(AssetItem assetItem, Object prefab, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        TextPreviewBox.Text = "Loading prefab preview...";
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
        TextPreviewBox.Text = "Loading asset preview...";
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
                        TextPreviewBox.Text = truncated;
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                    }
                    else
                    {
                        TextPreviewBox.Text = string.Empty;
                        TextPreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = displayInfo.IsChecked == true;
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
        Interlocked.Increment(ref texturePreviewIdCounter);
        if (enablePreview.IsChecked != true)
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
        if (PreviewInfoBorder != null)
        {
            PreviewInfoBorder.IsVisible = false;
        }
        if (AudioPanel != null)
        {
            AudioPanel.IsVisible = false;
            if (asset is not AudioClip)
            {
                AudioReset();
            }
        }
        if (VideoClipPanel != null)
        {
            VideoClipPanel.IsVisible = false;
            if (asset is not VideoClip && asset is not VideoPlayer)
            {
                VideoReset();
            }
        }
        ClearMeshMaterialControls();
        ClearPreviewCandidateControls();
        ClearPreviewCacheBadge();
        if (asset is not AnimationClip)
        {
            Stop2DAnimation();
            HideAnimationPlayback();
            GLPreviewControl?.StopAnimation();
            currentPreviewMesh = null;
            currentPreviewAvatar = null;
        }
        currentPreviewTexture = null;
        currentPreviewSprite = null;
        currentPreviewAudioClip = null;
        currentPreviewVideoClip = null;

        PreviewLabel.IsVisible = displayInfo.IsChecked == true;
        PreviewLabel.Text = displayInfo.IsChecked == true ? $"{assetItem.DisplayType}: {assetItem.Name}" : string.Empty;

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
                    PreviewShaderAsync(assetItem, m_Shader, fbxHeader);
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    PreviewMonoBehaviour(assetItem, m_MonoBehaviour, fbxHeader);
                    break;
                case MonoScript m_MonoScript:
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Assembly: {m_MonoScript.m_AssemblyName}");
                        sb.AppendLine($"Namespace: {m_MonoScript.m_Namespace}");
                        sb.AppendLine($"Class: {m_MonoScript.m_ClassName}");
                        SetTextWithTruncation(TextPreviewBox, sb.ToString());
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                    }
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
                case MovieTexture _:
                    StatusStripUpdate("Only supported export.");
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
