using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private sealed class PreparedTmpFontPreviewResult
    {
        public required WriteableBitmap Bitmap { get; init; }
        public required string InfoText { get; init; }
        public required string StatusMessage { get; init; }
    }

    private void PreviewMonoBehaviour(AssetItem assetItem, MonoBehaviour m_MonoBehaviour, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.MonoBehaviour,
            assetItem,
            isLoading: true,
            loadingStatusText: $"Loading MonoBehaviour preview: {assetItem.Name}...");
        PreviewSession.TextContent = "Loading MonoBehaviour preview...";
        TextPreviewBox.Text = PreviewSession.TextContent;
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Loading MonoBehaviour preview: {assetItem.Name}...");

        _ = PreviewMonoBehaviourAsync(assetItem, m_MonoBehaviour, fbxHeader, requestId, previewVersion, token);
    }

    private async Task PreviewMonoBehaviourAsync(
        AssetItem assetItem,
        MonoBehaviour m_MonoBehaviour,
        string fbxHeader,
        long requestId,
        long previewVersion,
        CancellationToken token)
    {
        try
        {
            // Fast path: attempt direct ToType() off the UI thread, checking for TextMeshPro TMP_FontAsset first.
            var directResult = await Task.Run<(bool Succeeded, string? Text, PreparedTmpFontPreviewResult? TmpFont)>(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var obj = m_MonoBehaviour.ToType();
                    if (obj != null)
                    {
                        token.ThrowIfCancellationRequested();
                        var tmpFontPreview = TryPrepareTmpFontPreviewBackground(m_MonoBehaviour, obj, token);
                        if (tmpFontPreview != null)
                        {
                            return (true, null, tmpFontPreview);
                        }

                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                        token.ThrowIfCancellationRequested();
                        return (true, TruncatePreviewText(fbxHeader + json), null);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fall through to TypeTree / Dump fallback.
                }

                return (false, null, null);
            }, token);

            if (directResult.TmpFont != null)
            {
                ApplyTmpFontPreview(assetItem, requestId, previewVersion, token, directResult.TmpFont);
                return;
            }

            if (directResult.Succeeded && directResult.Text != null)
            {
                ApplyMonoBehaviourPreviewText(requestId, previewVersion, token, directResult.Text, "MonoBehaviour preview loaded (JSON format).");
                return;
            }

            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            // TypeTree path: assembly folder prompt (if needed) on UI thread, assembly load + ConvertToTypeTree off UI thread.
            TypeTree? typeTree = null;
            try
            {
                typeTree = await MonoBehaviourToTypeTree(m_MonoBehaviour, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                typeTree = null;
            }

            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            var resolved = await Task.Run<(string? Text, string Status, PreparedTmpFontPreviewResult? TmpFont)>(() =>
            {
                token.ThrowIfCancellationRequested();

                if (typeTree != null)
                {
                    try
                    {
                        var obj = m_MonoBehaviour.ToType(typeTree);
                        if (obj != null)
                        {
                            token.ThrowIfCancellationRequested();
                            var tmpFontPreview = TryPrepareTmpFontPreviewBackground(m_MonoBehaviour, obj, token);
                            if (tmpFontPreview != null)
                            {
                                return (null, tmpFontPreview.StatusMessage, tmpFontPreview);
                            }

                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                            token.ThrowIfCancellationRequested();
                            return (TruncatePreviewText(fbxHeader + json), "MonoBehaviour preview loaded (JSON format).", null);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Fall through to text dump fallback.
                    }
                }

                string? dumpStr = null;
                try
                {
                    dumpStr = m_MonoBehaviour.Dump();
                    if (dumpStr == null && typeTree != null)
                    {
                        dumpStr = m_MonoBehaviour.Dump(typeTree);
                    }
                }
                catch (Exception dumpEx)
                {
                    dumpStr = $"Failed to dump MonoBehaviour: {dumpEx.Message}";
                }

                token.ThrowIfCancellationRequested();
                return dumpStr != null
                    ? (TruncatePreviewText(fbxHeader + dumpStr), "MonoBehaviour loaded (text dump).", null)
                    : (null, "MonoBehaviour loaded (no dump/types available).", null);
            }, token);

            if (resolved.TmpFont != null)
            {
                ApplyTmpFontPreview(assetItem, requestId, previewVersion, token, resolved.TmpFont);
                return;
            }

            ApplyMonoBehaviourPreviewText(requestId, previewVersion, token, resolved.Text, resolved.Status);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"MonoBehaviour preview failed for {assetItem.Name}: {ex}");
            var errorText = TruncatePreviewText($"Failed to preview MonoBehaviour {assetItem.Name}:{Environment.NewLine}{ex.Message}");
            ApplyMonoBehaviourPreviewText(requestId, previewVersion, token, errorText, $"MonoBehaviour preview error: {ex.Message}");
        }
    }

    private PreparedTmpFontPreviewResult? TryPrepareTmpFontPreviewBackground(
        MonoBehaviour monoBehaviour,
        OrderedDictionary fields,
        CancellationToken token)
    {
        if (!TmpFontPreviewService.TryExtract(monoBehaviour, fields, out var tmpInfo) || tmpInfo == null)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        var atlasTexture = ResolveTmpFontAtlasTexture(monoBehaviour, tmpInfo);
        if (atlasTexture == null)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        SetActivePreviewDependencyFiles(monoBehaviour, atlasTexture);

        using var previewImage = LoadTexturePreviewThumbnail(atlasTexture, MaxInlinePreviewTextureDimension);
        var image = previewImage?.Image;
        if (image == null)
        {
            return null;
        }

        FontAssetPreviewRenderer.EnhanceFontAtlasVisibility(image);
        token.ThrowIfCancellationRequested();

        var bitmap = CreateWriteableBitmapFromSKBitmap(image);
        var fallbackNames = ResolveTmpFallbackFontNames(monoBehaviour, tmpInfo);
        var infoText = tmpInfo.FormatInfoOverlay(atlasTexture, fallbackNames);
        var status = $"TMP_FontAsset loaded: {tmpInfo.DisplayName} ({atlasTexture.m_Width}x{atlasTexture.m_Height})";

        return new PreparedTmpFontPreviewResult
        {
            Bitmap = bitmap,
            InfoText = infoText,
            StatusMessage = status
        };
    }

    private Texture2D? ResolveTmpFontAtlasTexture(MonoBehaviour monoBehaviour, TmpFontAssetInfo tmpInfo)
    {
        var sourceFile = monoBehaviour.assetsFile;
        if (sourceFile == null)
        {
            return null;
        }

        for (var i = 0; i < tmpInfo.AtlasTextureRefs.Count; i++)
        {
            var texRef = tmpInfo.AtlasTextureRefs[i];
            if (!texRef.IsValid)
            {
                continue;
            }

            var texPtr = new PPtr<Texture>(texRef.FileId, texRef.PathId, sourceFile);
            if (texPtr.TryGet<Texture2D>(out var directTex) && directTex != null)
            {
                return directTex;
            }

            var lazyTex = ResolveTexturePPtrForOwnerPreview(monoBehaviour, texPtr);
            if (lazyTex != null)
            {
                return lazyTex;
            }
        }

        if (tmpInfo.MaterialRef is { IsValid: true } matRef)
        {
            var matPtr = new PPtr<Material>(matRef.FileId, matRef.PathId, sourceFile);
            if (matPtr.TryGet(out var mat) && mat != null)
            {
                var matTex = FindTextureForMaterial(mat);
                if (matTex != null)
                {
                    return matTex;
                }
            }
        }

        return null;
    }

    private List<string> ResolveTmpFallbackFontNames(MonoBehaviour monoBehaviour, TmpFontAssetInfo tmpInfo)
    {
        var names = new List<string>();
        var sourceFile = monoBehaviour.assetsFile;
        if (sourceFile == null || tmpInfo.FallbackFontRefs.Count == 0)
        {
            return names;
        }

        var maxNames = Math.Min(tmpInfo.FallbackFontRefs.Count, 8);
        for (var i = 0; i < maxNames; i++)
        {
            var fbRef = tmpInfo.FallbackFontRefs[i];
            if (!fbRef.IsValid)
            {
                continue;
            }

            string? name = null;
            if (fbRef.FileId == 0)
            {
                if (sourceFile.TryGetObject(fbRef.PathId, out var obj))
                {
                    name = obj is MonoBehaviour fbMb && !string.IsNullOrWhiteSpace(fbMb.m_Name)
                        ? fbMb.m_Name
                        : (obj as NamedObject)?.m_Name;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    var key = AssetHandle.BuildUniqueID(sourceFile, fbRef.PathId);
                    name = assetsManager.ProjectIndex.GetHandle(key)?.Name;
                }
            }
            else if (fbRef.FileId > 0 && sourceFile.m_Externals != null && fbRef.FileId - 1 < sourceFile.m_Externals.Count)
            {
                var ext = sourceFile.m_Externals[fbRef.FileId - 1];
                if (assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile) && extFile != null)
                {
                    if (extFile.TryGetObject(fbRef.PathId, out var extObj))
                    {
                        name = extObj is MonoBehaviour fbMb && !string.IsNullOrWhiteSpace(fbMb.m_Name)
                            ? fbMb.m_Name
                            : (extObj as NamedObject)?.m_Name;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var key = AssetHandle.BuildUniqueID(extFile, fbRef.PathId);
                        name = assetsManager.ProjectIndex.GetHandle(key)?.Name;
                    }
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    var extName = !string.IsNullOrWhiteSpace(ext?.fileName) ? ext!.fileName : ext?.pathName;
                    if (!string.IsNullOrWhiteSpace(extName))
                    {
                        var normalized = extName.Replace('\\', '/').TrimEnd('/');
                        var slash = normalized.LastIndexOf('/');
                        var leaf = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
                        name = assetsManager.ProjectIndex
                            .GetHandlesForFile(leaf)
                            .FirstOrDefault(h => h.PathID == fbRef.PathId)?.Name;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    private void ApplyTmpFontPreview(
        AssetItem assetItem,
        long requestId,
        long previewVersion,
        CancellationToken token,
        PreparedTmpFontPreviewResult tmpPreview)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                tmpPreview.Bitmap.Dispose();
                return;
            }

            PreviewSession.ActivateSurface(
                PreviewSurfaceKind.Font,
                assetItem,
                metadataSummary: tmpPreview.InfoText,
                isLoading: false);
            ImagePreviewBox.Source = tmpPreview.Bitmap;
            ImagePreviewBox.IsVisible = true;
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            SetPreviewInfoOverlay(tmpPreview.InfoText);
            StatusStripUpdate(tmpPreview.StatusMessage);
        });
    }

    private void ApplyMonoBehaviourPreviewText(
        long requestId,
        long previewVersion,
        CancellationToken token,
        string? truncatedText,
        string statusMessage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            if (truncatedText != null)
            {
                PreviewSession.ShowTextPreview(truncatedText, PreviewSurfaceKind.MonoBehaviour, truncate: false);
                TextPreviewBox.Text = truncatedText;
                TextPreviewBox.IsVisible = true;
                PreviewLabel.IsVisible = false;
            }
            else
            {
                PreviewSession.ShowTextPreview(string.Empty, PreviewSurfaceKind.MonoBehaviour, truncate: false);
                TextPreviewBox.Text = string.Empty;
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = ViewModel.Options.DisplayInfo;
            }

            StatusStripUpdate(statusMessage);
        });
    }
}
