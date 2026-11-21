using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private static readonly PropertyInfo? VideoClipWidthProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_Width));
    private static readonly PropertyInfo? VideoClipHeightProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_Height));
    private static readonly PropertyInfo? VideoClipProxyWidthProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_ProxyWidth));
    private static readonly PropertyInfo? VideoClipProxyHeightProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_ProxyHeight));
    private static readonly PropertyInfo? VideoClipFrameRateProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_FrameRate));
    private static readonly PropertyInfo? VideoClipFrameCountProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_FrameCount));
    private static readonly PropertyInfo? VideoClipAudioChannelCountProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_AudioChannelCount));
    private static readonly PropertyInfo? VideoClipAudioSampleRateProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_AudioSampleRate));
    private static readonly PropertyInfo? VideoClipAudioLanguageProperty = typeof(VideoClip).GetProperty(nameof(VideoClip.m_AudioLanguage));

    private bool TryPreviewTextAssetEmbeddedMedia(
        AssetItem assetItem,
        TextAsset m_TextAsset,
        byte[] data,
        TextAssetFormatDetectionResult detected,
        string fbxHeader)
    {
        if (detected.MediaKind == TextAssetEmbeddedMediaKind.Video && detected.IsPlayableVideo)
        {
            PreviewTextAssetEmbeddedVideo(assetItem, m_TextAsset, data, detected);
            return true;
        }

        if (detected.MediaKind == TextAssetEmbeddedMediaKind.Audio && detected.IsPlayableAudio)
        {
            PreviewTextAssetEmbeddedAudio(assetItem, m_TextAsset, data, detected);
            return true;
        }

        if (detected.MediaKind == TextAssetEmbeddedMediaKind.Image && detected.IsPreviewableImage)
        {
            PreviewTextAssetEmbeddedImageAsync(assetItem, data, detected, fbxHeader);
            return true;
        }

        if (detected.IsRenderableFont)
        {
            PreviewTextAssetEmbeddedFontAsync(assetItem, data, detected, fbxHeader);
            return true;
        }

        return false;
    }

    private void PreviewTextAssetEmbeddedVideo(
        AssetItem assetItem,
        TextAsset m_TextAsset,
        byte[] data,
        TextAssetFormatDetectionResult detected)
    {
        var descriptor = BuildTextAssetEmbeddedVideoDescriptor(assetItem, m_TextAsset, detected);
        var syntheticVideoClip = CreateSyntheticVideoClipForTextAsset(m_TextAsset, assetItem.Name, data, detected);

        TextPreviewBox.IsVisible = false;
        ImagePreviewBox.IsVisible = false;
        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
        if (TextureGLPreview != null) TextureGLPreview.IsVisible = false;
        PreviewLabel.IsVisible = false;
        ClearPreviewInfoOverlay();

        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Video,
            assetItem,
            title: descriptor.Title,
            metadataSummary: detected.SummaryText);

        PreviewVideoClip(assetItem, syntheticVideoClip);

        MediaPlayback.SetMediaTitle(descriptor.Title);
        MediaPlayback.SetMediaInfo(descriptor.ReadyInfoText);

        if (VideoResolutionLabel != null)
        {
            VideoResolutionLabel.Text = descriptor.ResolutionText;
        }
        if (VideoFrameRateLabel != null)
        {
            VideoFrameRateLabel.Text = descriptor.FrameRateText;
        }
        if (VideoFormatLabel != null)
        {
            VideoFormatLabel.Text = descriptor.FormatLabel;
        }
        if (VideoAudioLabel != null)
        {
            VideoAudioLabel.Text = descriptor.AudioText;
            bool hasAudio = !string.IsNullOrEmpty(detected.AudioCodec) || detected.Channels > 0;
            VideoAudioLabel.Foreground = new global::Avalonia.Media.SolidColorBrush(
                global::Avalonia.Media.Color.Parse(hasAudio ? "#00e676" : "#ff9800"));
        }

        StatusStripUpdate($"Loaded embedded {detected.FormatName} TextAsset: {assetItem.Name} ({data.Length:N0} bytes)");
    }

    internal static (
        string Title,
        string AssetIdKey,
        string Extension,
        string FormatLabel,
        string ResolutionText,
        string FrameRateText,
        string AudioText,
        string ReadyInfoText) BuildTextAssetEmbeddedVideoDescriptor(
        AssetItem assetItem,
        TextAsset textAsset,
        TextAssetFormatDetectionResult detected)
    {
        string rawName = !string.IsNullOrWhiteSpace(assetItem?.Name)
            ? assetItem.Name
            : (!string.IsNullOrWhiteSpace(textAsset?.m_Name) ? textAsset.m_Name : "TextAsset");
        string title = $"{rawName} (TextAsset {detected.FormatId})";
        string assetIdKey = textAsset != null ? GetPreviewAssetId(textAsset) : (assetItem?.UniqueID ?? rawName);
        string extension = !string.IsNullOrWhiteSpace(detected.RecommendedExtension) ? detected.RecommendedExtension : ".avi";

        var formatSb = new StringBuilder($"Format: {detected.FormatId}");
        if (!string.IsNullOrWhiteSpace(detected.VideoCodec))
        {
            formatSb.Append($" | Video Codec: {detected.VideoCodec}");
        }
        formatSb.Append($" | Size: {detected.ByteSize:N0} bytes");

        string resolutionText = detected.Width > 0 && detected.Height > 0
            ? $"Resolution: {detected.Width}x{detected.Height}"
            : "Resolution: Embedded stream (probing via FFmpeg)";

        var frameSb = new StringBuilder();
        if (detected.FrameRate > 0)
        {
            frameSb.Append($"Frame Rate: {detected.FrameRate.ToString("F2", CultureInfo.InvariantCulture)} FPS");
        }
        else
        {
            frameSb.Append("Frame Rate: Probing...");
        }
        if (detected.TotalFrames > 0)
        {
            frameSb.Append($" | Frames: {detected.TotalFrames:N0}");
        }
        if (detected.DurationSeconds > 0)
        {
            frameSb.Append($" | Duration: {detected.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        }

        string audioText;
        if (!string.IsNullOrWhiteSpace(detected.AudioCodec) || detected.Channels > 0)
        {
            var audioSb = new StringBuilder("Audio: ");
            audioSb.Append(!string.IsNullOrWhiteSpace(detected.AudioCodec) ? detected.AudioCodec : "yes");
            if (detected.Channels > 0)
            {
                audioSb.Append($" | {detected.Channels}ch");
            }
            if (detected.SampleRate > 0)
            {
                audioSb.Append($" {detected.SampleRate}Hz");
            }
            if (detected.BitsPerSample > 0)
            {
                audioSb.Append($" ({detected.BitsPerSample}-bit)");
            }
            audioText = audioSb.ToString();
        }
        else
        {
            audioText = "Audio: no audio stream in header";
        }

        string readyInfoText = $"Embedded {detected.FormatName} inside Unity TextAsset. Ready to play or export ({extension}).";
        return (
            title,
            assetIdKey,
            extension,
            formatSb.ToString(),
            resolutionText,
            frameSb.ToString(),
            audioText,
            readyInfoText);
    }

    internal static VideoClip CreateSyntheticVideoClipForTextAsset(
        TextAsset textAsset,
        string? displayName,
        byte[] videoBytes,
        TextAssetFormatDetectionResult detected)
    {
        videoBytes ??= Array.Empty<byte>();
        string ext = !string.IsNullOrWhiteSpace(detected.RecommendedExtension) ? detected.RecommendedExtension : ".avi";
        string rawName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : (!string.IsNullOrWhiteSpace(textAsset?.m_Name) ? textAsset.m_Name : "TextAssetVideo");

        string baseName = StripDuplicateTrailingExtension(rawName, ext);

        var clip = (VideoClip)RuntimeHelpers.GetUninitializedObject(typeof(VideoClip));
        if (textAsset != null)
        {
            clip.assetsFile = textAsset.assetsFile;
            clip.m_PathID = textAsset.m_PathID;
            clip.version = textAsset.version;
            clip.platform = textAsset.platform;
        }
        clip.type = ClassIDType.VideoClip;
        clip.m_Name = baseName;
        clip.m_OriginalPath = baseName + ext;

        var extRes = (StreamedResource)RuntimeHelpers.GetUninitializedObject(typeof(StreamedResource));
        extRes.m_Source = string.Empty;
        extRes.m_Offset = 0;
        extRes.m_Size = videoBytes.Length;
        clip.m_ExternalResources = extRes;
        clip.m_VideoData = new ResourceReader(videoBytes);

        uint width = (uint)Math.Max(0, detected.Width);
        uint height = (uint)Math.Max(0, detected.Height);
        VideoClipWidthProperty?.SetValue(clip, width);
        VideoClipHeightProperty?.SetValue(clip, height);
        VideoClipProxyWidthProperty?.SetValue(clip, width);
        VideoClipProxyHeightProperty?.SetValue(clip, height);
        VideoClipFrameRateProperty?.SetValue(clip, Math.Max(0.0, detected.FrameRate));
        VideoClipFrameCountProperty?.SetValue(clip, (ulong)Math.Max(0, detected.TotalFrames));

        var channels = detected.Channels > 0 ? new[] { (ushort)detected.Channels } : Array.Empty<ushort>();
        var rates = detected.SampleRate > 0 ? new[] { (uint)detected.SampleRate } : Array.Empty<uint>();
        VideoClipAudioChannelCountProperty?.SetValue(clip, channels);
        VideoClipAudioSampleRateProperty?.SetValue(clip, rates);
        VideoClipAudioLanguageProperty?.SetValue(clip, Array.Empty<string>());

        return clip;
    }

    private void PreviewTextAssetEmbeddedAudio(
        AssetItem assetItem,
        TextAsset m_TextAsset,
        byte[] data,
        TextAssetFormatDetectionResult detected)
    {
        var syntheticAudioClip = CreateSyntheticAudioClipForTextAsset(m_TextAsset, assetItem.Name, data, detected);
        string infoText = BuildTextAssetEmbeddedAudioInfoText(detected);
        string title = $"{assetItem.Name} (TextAsset {detected.FormatId})";

        TextPreviewBox.IsVisible = false;
        ImagePreviewBox.IsVisible = false;
        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
        if (TextureGLPreview != null) TextureGLPreview.IsVisible = false;
        PreviewLabel.IsVisible = false;
        ClearPreviewInfoOverlay();

        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Audio,
            assetItem,
            title: title,
            metadataSummary: detected.SummaryText);

        AudioReset();
        currentPreviewAudioClip = syntheticAudioClip;
        EnsureAudioPlaybackService();
        MediaPlayback.SelectAudio(title, infoText);

        StatusStripUpdate($"Loaded embedded {detected.FormatName} TextAsset: {assetItem.Name} ({data.Length:N0} bytes)");
    }

    internal static string BuildTextAssetEmbeddedAudioInfoText(TextAssetFormatDetectionResult detected)
    {
        var sb = new StringBuilder();
        sb.Append($"Embedded format: {detected.FormatName}");
        if (detected.Channels > 0)
        {
            sb.Append($" | {detected.Channels}ch");
        }
        if (detected.SampleRate > 0)
        {
            sb.Append($" | {detected.SampleRate} Hz");
        }
        if (detected.BitsPerSample > 0)
        {
            sb.Append($" | {detected.BitsPerSample}-bit");
        }
        if (detected.SubsoundCount > 1)
        {
            sb.Append($" | {detected.SubsoundCount} subsounds");
        }
        if (detected.DurationSeconds > 0)
        {
            sb.Append($" | {detected.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        }
        return sb.ToString();
    }

    internal static AudioClip CreateSyntheticAudioClipForTextAsset(
        TextAsset textAsset,
        string? displayName,
        byte[] audioBytes,
        TextAssetFormatDetectionResult detected)
    {
        audioBytes ??= Array.Empty<byte>();
        string rawName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : (!string.IsNullOrWhiteSpace(textAsset?.m_Name) ? textAsset.m_Name : "TextAssetAudio");

        var clip = (AudioClip)RuntimeHelpers.GetUninitializedObject(typeof(AudioClip));
        if (textAsset != null)
        {
            clip.assetsFile = textAsset.assetsFile;
            clip.m_PathID = textAsset.m_PathID;
            clip.platform = textAsset.platform;
        }
        clip.type = ClassIDType.AudioClip;
        clip.m_Name = rawName;
        clip.m_Channels = detected.Channels;
        clip.m_Frequency = detected.SampleRate;
        clip.m_BitsPerSample = detected.BitsPerSample;
        clip.m_Length = (float)Math.Max(0.0, detected.DurationSeconds);
        clip.m_Size = audioBytes.Length;
        clip.m_AudioData = new ResourceReader(audioBytes);

        if (string.Equals(detected.FormatId, "AIFF", StringComparison.OrdinalIgnoreCase))
        {
            clip.version = new[] { 4, 0, 0, 0 };
            clip.m_Type = FMODSoundType.AIFF;
        }
        else if (string.Equals(detected.FormatId, "WAV", StringComparison.OrdinalIgnoreCase))
        {
            clip.version = new[] { 4, 0, 0, 0 };
            clip.m_Type = FMODSoundType.WAV;
        }
        else
        {
            clip.version = textAsset?.version is { Length: > 0 } ? textAsset.version : new[] { 2022, 3, 0, 0 };
            clip.m_CompressionFormat = detected.FormatId switch
            {
                "MP3" => AudioCompressionFormat.MP3,
                "AAC" or "M4A" => AudioCompressionFormat.AAC,
                "OGG" or "Opus" => AudioCompressionFormat.Vorbis,
                _ => AudioCompressionFormat.PCM
            };
        }

        return clip;
    }

    private void PreviewTextAssetEmbeddedImageAsync(
        AssetItem assetItem,
        byte[] data,
        TextAssetFormatDetectionResult detected,
        string fbxHeader)
    {
        Interlocked.Increment(ref texturePreviewIdCounter);
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Texture,
            assetItem,
            isLoading: true,
            loadingStatusText: $"Loading embedded {detected.FormatId} image: {assetItem.Name}...");
        ClearPreviewCacheBadge();
        StatusStripUpdate($"Loading embedded {detected.FormatName} TextAsset: {assetItem.Name}...");

        _ = Task.Run(() =>
        {
            SKBitmap? skBitmap = null;
            try
            {
                token.ThrowIfCancellationRequested();
                skBitmap = DecodeEmbeddedTextAssetImage(data, detected, assetItem.Name);
                token.ThrowIfCancellationRequested();

                if (skBitmap == null || skBitmap.Width <= 0 || skBitmap.Height <= 0)
                {
                    skBitmap?.Dispose();
                    skBitmap = null;
                    var fallbackPreview = TextAssetPreviewBuilder.BuildPreview(assetItem, data, fbxHeader);
                    var fallbackText = TruncatePreviewText(fallbackPreview.DetailsText);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            return;
                        }

                        PreviewSession.ShowTextPreview(fallbackText, PreviewSurfaceKind.Text, assetItem, truncate: false);
                        TextPreviewBox.FontFamily = TextAssetMonoFontFamily;
                        TextPreviewBox.FontSize = 13;
                        TextPreviewBox.Text = fallbackText;
                        TextPreviewBox.IsVisible = true;
                        ImagePreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = false;
                        ClearPreviewInfoOverlay();
                        StatusStripUpdate($"TextAsset metadata loaded: {detected.FormatName} ({data.Length:N0} bytes).");
                    });
                    return;
                }

                int originalWidth = skBitmap.Width;
                int originalHeight = skBitmap.Height;
                bool downscaled = LimitInlinePreviewImage(ref skBitmap);
                token.ThrowIfCancellationRequested();

                var avaloniaBitmap = CreateWriteableBitmapFromSKBitmap(skBitmap);
                if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                {
                    avaloniaBitmap.Dispose();
                    return;
                }

                var infoSb = new StringBuilder();
                infoSb.AppendLine($"Embedded TextAsset: {detected.FormatName}");
                infoSb.AppendLine($"Width: {originalWidth}");
                infoSb.AppendLine($"Height: {originalHeight}");
                infoSb.AppendLine($"Size: {data.Length:N0} bytes");
                if (detected.MipMapCount > 1)
                {
                    infoSb.AppendLine($"MipMaps: {detected.MipMapCount}");
                }
                if (downscaled)
                {
                    infoSb.AppendLine($"Downscaled to: {skBitmap.Width}x{skBitmap.Height}");
                }
                string overlayText = infoSb.ToString().TrimEnd();

                Dispatcher.UIThread.Post(() =>
                {
                    if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        ImagePreviewBox.Source = avaloniaBitmap;
                        ImagePreviewBox.IsVisible = true;
                        TextPreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = false;
                        SetPreviewInfoOverlay(overlayText);
                        StatusStripUpdate($"Loaded embedded {detected.FormatName} ({originalWidth}x{originalHeight}, {data.Length:N0} bytes).");
                    }
                    else
                    {
                        avaloniaBitmap.Dispose();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Warning, $"Embedded image preview fallback for {assetItem.Name}: {ex.Message}");
                var fallbackPreview = TextAssetPreviewBuilder.BuildPreview(assetItem, data, fbxHeader);
                var fallbackText = TruncatePreviewText(fallbackPreview.DetailsText);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    PreviewSession.ShowTextPreview(fallbackText, PreviewSurfaceKind.Text, assetItem, truncate: false);
                    TextPreviewBox.FontFamily = TextAssetMonoFontFamily;
                    TextPreviewBox.FontSize = 13;
                    TextPreviewBox.Text = fallbackText;
                    TextPreviewBox.IsVisible = true;
                    ImagePreviewBox.IsVisible = false;
                    PreviewLabel.IsVisible = false;
                    ClearPreviewInfoOverlay();
                    StatusStripUpdate($"TextAsset metadata loaded: {detected.FormatName} ({data.Length:N0} bytes).");
                });
            }
            finally
            {
                skBitmap?.Dispose();
            }
        }, token);
    }

    internal static SKBitmap? DecodeEmbeddedTextAssetImage(
        byte[] data,
        TextAssetFormatDetectionResult detected,
        string? assetName = null)
    {
        if (data == null || data.Length == 0)
        {
            return null;
        }

        if (string.Equals(detected.FormatId, "DDS", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeDdsToSKBitmap(data, detected, assetName);
        }

        return ImageExtensions.DecodeBgraUnpremul(data);
    }

    internal static SKBitmap? TryDecodeDdsToSKBitmap(
        byte[] data,
        TextAssetFormatDetectionResult detected,
        string? assetName = null)
    {
        if (data == null
            || detected.Width <= 0
            || detected.Height <= 0
            || !detected.DdsTextureFormat.HasValue
            || detected.DdsDataOffset <= 0
            || detected.DdsDataOffset >= data.Length)
        {
            return null;
        }

        int payloadLength = data.Length - detected.DdsDataOffset;
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(data, detected.DdsDataOffset, payload, 0, payloadLength);

        var syntheticTex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
        syntheticTex.m_Name = !string.IsNullOrWhiteSpace(assetName) ? assetName : "EmbeddedDDS";
        syntheticTex.m_PathID = 0;
        syntheticTex.version = new[] { 2022, 3, 10, 1 };
        syntheticTex.platform = BuildTarget.StandaloneWindows64;
        syntheticTex.m_Width = detected.Width;
        syntheticTex.m_Height = detected.Height;
        syntheticTex.m_TextureFormat = detected.DdsTextureFormat.Value;
        syntheticTex.image_data = new ResourceReader(payload);

        try
        {
            return syntheticTex.ConvertToImage(flip: false, unpackNormalMap: false);
        }
        catch
        {
            return null;
        }
    }

    private void PreviewTextAssetEmbeddedFontAsync(
        AssetItem assetItem,
        byte[] data,
        TextAssetFormatDetectionResult detected,
        string fbxHeader)
    {
        Interlocked.Increment(ref texturePreviewIdCounter);
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Font,
            assetItem,
            isLoading: true,
            loadingStatusText: $"Rendering embedded {detected.FormatId} font: {assetItem.Name}...");
        ClearPreviewCacheBadge();
        StatusStripUpdate($"Rendering embedded {detected.FormatName} TextAsset: {assetItem.Name}...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var fontPreview = FontAssetPreviewRenderer.Render(
                    $"{assetItem.Name} ({detected.FormatId})",
                    data,
                    () => !IsPreviewRequestCurrent(requestId, previewVersion, token));

                if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                {
                    fontPreview.Bitmap?.Dispose();
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        ImagePreviewBox.Source = fontPreview.Bitmap;
                        ImagePreviewBox.IsVisible = true;
                        TextPreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = false;
                        SetPreviewInfoOverlay(detected.SummaryText);
                        StatusStripUpdate($"Loaded embedded {detected.FormatName}: {assetItem.Name} ({data.Length:N0} bytes).");
                    }
                    else
                    {
                        fontPreview.Bitmap?.Dispose();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                var fallbackPreview = TextAssetPreviewBuilder.BuildPreview(assetItem, data, fbxHeader);
                var fallbackText = TruncatePreviewText(fallbackPreview.DetailsText);
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        PreviewSession.ShowTextPreview(fallbackText, PreviewSurfaceKind.Text, assetItem, truncate: false);
                        TextPreviewBox.FontFamily = TextAssetMonoFontFamily;
                        TextPreviewBox.FontSize = 13;
                        TextPreviewBox.Text = fallbackText;
                        ImagePreviewBox.IsVisible = false;
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                        ClearPreviewInfoOverlay();
                        StatusStripUpdate($"TextAsset font metadata loaded: {detected.FormatName} ({data.Length:N0} bytes).");
                    }
                });
            }
        }, token);
    }

    private static string StripDuplicateTrailingExtension(string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(extension))
        {
            return name ?? "unnamed";
        }

        if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return name.Substring(0, name.Length - extension.Length);
        }

        return name;
    }
}
