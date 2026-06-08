using Avalonia.Controls;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
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
            DumpTextBox.Text = string.Empty;
            previewDebounce?.Cancel();
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
                PreviewSelectedAssetImmediately();
            }
        }
    }

    private void CancelPendingPreview()
    {
        previewDebounce?.Cancel();
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
        var previewId = ++texturePreviewIdCounter;

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
                        && previewId == texturePreviewIdCounter
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
                    if (previewId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        StatusStripUpdate($"Preview error: {ex.Message}");
                    }
                });
            }
        }, token);
    }

    private void SetTextWithTruncation(TextBox textBox, string? text, string fallbackText = "")
    {
        if (text == null)
        {
            textBox.Text = fallbackText;
            return;
        }

        const int maxChars = 100000;
        if (text.Length > maxChars)
        {
            textBox.Text = text.Substring(0, maxChars) + 
                $"{Environment.NewLine}...{Environment.NewLine}[Preview truncated: content is too large ({text.Length:N0} characters). Please export the asset to view full content]";
        }
        else
        {
            textBox.Text = text;
        }
    }

    private async Task UpdateDumpForSelectedAsset()
    {
        if (AssetListDataGrid.SelectedItem is not AssetItem assetItem)
        {
            DumpTextBox.Text = string.Empty;
            return;
        }

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
                if (assetsManager.LazyLoading && assetItem.Handle != null)
                {
                    EnsureLazyAssetReadyForPreview(assetItem);
                }

                return assetItem.Asset;
            });
            if (!ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }

            if (asset == null)
            {
                DumpTextBox.Text = "No Dump Available";
                return;
            }

            var dump = await DumpAsset(asset);
            if (!ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
            {
                return;
            }
            SetTextWithTruncation(DumpTextBox, dump, "No Dump Available");
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
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

    private async Task<string?> DumpAsset(Object asset)
    {
        if (asset is MonoBehaviour monoBehaviour)
        {
            var dump = await Task.Run(() => monoBehaviour.Dump());
            if (dump == null)
            {
                var typeTree = await MonoBehaviourToTypeTree(monoBehaviour);
                dump = await Task.Run(() => monoBehaviour.Dump(typeTree));
            }
            return dump;
        }
        else
        {
            return await Task.Run(() => asset.Dump());
        }
    }

    private async Task<TypeTree> MonoBehaviourToTypeTree(MonoBehaviour monoBehaviour)
    {
        if (!assemblyLoader.Loaded)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(await CreateLoadFolderOptions("Select Assembly Folder"));

                if (folders != null && folders.Count > 0)
                {
                    SaveLoadFolder(folders[0].Path.LocalPath);
                    assemblyLoader.Load(folders[0].Path.LocalPath);
                }
                else
                {
                    assemblyLoader.Loaded = true;
                }
            }
            else
            {
                assemblyLoader.Loaded = true;
            }
        }

        return monoBehaviour.ConvertToTypeTree(assemblyLoader);
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

    private void PreviewAsset(AssetItem assetItem, AssetStudio.Object? resolvedAsset = null, bool assetResolutionAttempted = false)
    {
        ++texturePreviewIdCounter;
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
                    SetTextWithTruncation(TextPreviewBox, fbxHeader + (m_Shader.Convert() ?? "Serialized Shader can't be read"));
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    string? dumpStr = null;
                    try
                    {
                        dumpStr = m_MonoBehaviour.Dump();
                    }
                    catch (Exception dumpEx)
                    {
                        dumpStr = $"Failed to dump MonoBehaviour: {dumpEx.Message}";
                    }
                    PreviewMonoBehaviour(assetItem, m_MonoBehaviour, fbxHeader, dumpStr);
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
                    SetTextWithTruncation(TextPreviewBox, fbxHeader + FormatPrefab(obj));
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
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
                    string? rawDump = null;
                    try
                    {
                        rawDump = asset.Dump();
                    }
                    catch (Exception dumpEx)
                    {
                        rawDump = $"Failed to dump asset: {dumpEx.Message}";
                    }
                    if (rawDump != null)
                    {
                        SetTextWithTruncation(TextPreviewBox, fbxHeader + rawDump);
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                    }
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
