using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
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
            // Fast path: attempt direct ToType() + JSON serialization off the UI thread without wasting a Dump().
            var directJsonResult = await Task.Run<(bool Succeeded, string? Text)>(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var obj = m_MonoBehaviour.ToType();
                    if (obj != null)
                    {
                        token.ThrowIfCancellationRequested();
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                        token.ThrowIfCancellationRequested();
                        return (true, TruncatePreviewText(fbxHeader + json));
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

                return (false, null);
            }, token);

            if (directJsonResult.Succeeded && directJsonResult.Text != null)
            {
                ApplyMonoBehaviourPreviewText(requestId, previewVersion, token, directJsonResult.Text, "MonoBehaviour preview loaded (JSON format).");
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

            var resolved = await Task.Run<(string? Text, string Status)>(() =>
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
                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                            token.ThrowIfCancellationRequested();
                            return (TruncatePreviewText(fbxHeader + json), "MonoBehaviour preview loaded (JSON format).");
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
                    ? (TruncatePreviewText(fbxHeader + dumpStr), "MonoBehaviour loaded (text dump).")
                    : (null, "MonoBehaviour loaded (no dump/types available).");
            }, token);

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
