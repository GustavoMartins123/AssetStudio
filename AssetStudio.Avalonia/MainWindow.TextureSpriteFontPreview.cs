using Avalonia.Threading;
using AssetStudio;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private Sprite? _spriteDependenciesLoadedFor;

    private void PreviewTexture2D(AssetItem assetItem, Texture2D m_Texture2D)
    {
        currentPreviewTexture = m_Texture2D;
        UpdateImagePreview();
    }

    private void PreviewSprite(AssetItem assetItem, Sprite m_Sprite)
    {
        currentPreviewSprite = m_Sprite;
        UpdateImagePreview();
    }

    private void PreviewFont(AssetItem assetItem, AssetStudio.Font m_Font)
    {
        if (m_Font.m_FontData == null || m_Font.m_FontData.Length == 0)
        {
            StatusStripUpdate("Font has no embedded binary data.");
            var sb = new StringBuilder();
            sb.AppendLine($"Font: {m_Font.m_Name}");
            sb.AppendLine("Format: System or Custom Reference (No embedded data)");
            sb.AppendLine("Data size: 0 bytes");
            sb.AppendLine();
            sb.AppendLine("This font asset does not contain embedded TrueType/OpenType binary data.");
            sb.AppendLine("It may reference a system-installed font or use custom character textures.");
            sb.AppendLine();
            sb.AppendLine("Raw metadata export is still available.");

            SetTextWithTruncation(TextPreviewBox, sb.ToString());
            ImagePreviewBox.IsVisible = false;
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
            if (PreviewInfoBorder != null)
            {
                PreviewInfoBorder.IsVisible = false;
            }
            ClearPreviewCacheBadge();
            return;
        }

        Interlocked.Increment(ref texturePreviewIdCounter);
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        ClearPreviewCacheBadge();
        StatusStripUpdate("Rendering font preview...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var fontPreview = FontAssetPreviewRenderer.Render(
                    m_Font.m_Name,
                    m_Font.m_FontData,
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
                        if (displayInfo.IsChecked == true && PreviewInfoOverlay != null && PreviewInfoBorder != null)
                        {
                            PreviewInfoOverlay.Text = fontPreview.InfoText;
                            PreviewInfoBorder.IsVisible = true;
                        }
                        else if (PreviewInfoBorder != null)
                        {
                            PreviewInfoBorder.IsVisible = false;
                        }
                        StatusStripUpdate($"Font loaded: {m_Font.m_Name}");
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
            catch (Exception ex)
            {
                var fallbackText = TruncatePreviewText(FontAssetPreviewRenderer.BuildFallbackText(m_Font, ex.Message));
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        TextPreviewBox.Text = fallbackText;
                        ImagePreviewBox.IsVisible = false;
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                        StatusStripUpdate($"Unsupported font preview: {ex.Message}");
                    }
                });
            }
        }, token);
    }

    private static void ApplyPreviewChannelMask(SKBitmap image, bool[] channels, int validChannel)
    {
        var pixels = image.GetWritablePixelSpan();
        int width = image.Width;
        int height = image.Height;
        int rowBytes = image.RowBytes;
        byte fillColor = (validChannel == 1 && channels[3]) ? byte.MaxValue : byte.MinValue;
        bool keepR = channels[0];
        bool keepG = channels[1];
        bool keepB = channels[2];
        bool keepA = channels[3];

        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                if (!keepB) row[offset] = fillColor;
                if (!keepG) row[offset + 1] = fillColor;
                if (!keepR) row[offset + 2] = fillColor;
                if (!keepA) row[offset + 3] = byte.MaxValue;
            }
        }
        GC.KeepAlive(image);
    }

    private static void MakeAlphaOnlyTextureVisible(SKBitmap image)
    {
        long rgbSignal = 0;
        long alphaSignal = 0;
        int samples = 0;
        byte minAlpha = byte.MaxValue;
        byte maxAlpha = byte.MinValue;
        int width = image.Width;
        int height = image.Height;
        int rowBytes = image.RowBytes;
        int stepX = Math.Max(1, width / 128);
        int stepY = Math.Max(1, height / 128);

        var pixels = image.GetWritablePixelSpan();
        for (int y = 0; y < height; y += stepY)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (int x = 0; x < width; x += stepX)
            {
                int offset = x * 4;
                byte b = row[offset];
                byte g = row[offset + 1];
                byte r = row[offset + 2];
                byte a = row[offset + 3];
                rgbSignal += r + g + b;
                alphaSignal += a;
                minAlpha = Math.Min(minAlpha, a);
                maxAlpha = Math.Max(maxAlpha, a);
                samples++;
            }
        }

        if (samples == 0
            || rgbSignal / (samples * 3) >= 8
            || alphaSignal / samples <= 8
            || maxAlpha - minAlpha <= 16)
        {
            GC.KeepAlive(image);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                byte value = row[offset + 3];
                row[offset] = value;
                row[offset + 1] = value;
                row[offset + 2] = value;
                row[offset + 3] = byte.MaxValue;
            }
        }
        GC.KeepAlive(image);
    }

    private static bool LimitInlinePreviewImage(ref SKBitmap image)
    {
        return LimitPreviewImage(ref image, MaxInlinePreviewTextureDimension);
    }

    private static bool LimitPreviewImage(ref SKBitmap image, int maxDimension)
    {
        var maxSide = Math.Max(image.Width, image.Height);
        if (maxSide <= maxDimension)
        {
            return false;
        }

        var scale = maxDimension / (float)maxSide;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var resizedInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var resized = image.Resize(resizedInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized == null)
        {
            return false;
        }

        image.Dispose();
        image = resized;
        return true;
    }

    private void EnsureSpriteDependenciesInBackground(Sprite sprite, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(Volatile.Read(ref _spriteDependenciesLoadedFor), sprite))
        {
            return;
        }

        EnsureSpritePreviewDependenciesLoaded(sprite, cancellationToken);
        Volatile.Write(ref _spriteDependenciesLoadedFor, sprite);
    }

    private void UpdateImagePreview(bool forceCpu = false)
    {
        var previewTexture = currentPreviewTexture;
        var previewSprite = currentPreviewSprite;
        if (previewTexture == null && previewSprite == null)
            return;

        Interlocked.Increment(ref texturePreviewIdCounter);
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        var channelsSnapshot = (bool[])textureChannels.Clone();

        if (useGpuTexturePreview && TextureGLPreview != null && !forceCpu)
        {
            var glControl = TextureGLPreview;
            var glCapabilities = glControl.CurrentGlCapabilities;
            StatusStripUpdate("Loading preview (GPU)...");

            _ = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    PreparedGpuTexturePayload preparedPayload;
                    string infoText = string.Empty;

                    if (previewTexture != null)
                    {
                        int width = previewTexture.m_Width;
                        int height = previewTexture.m_Height;

                        infoText = $"Width: {width}\nHeight: {height}\nFormat: {previewTexture.m_TextureFormat}";
                        switch (previewTexture.m_TextureSettings.m_FilterMode)
                        {
                            case 0: infoText += "\nFilter Mode: Point "; break;
                            case 1: infoText += "\nFilter Mode: Bilinear "; break;
                            case 2: infoText += "\nFilter Mode: Trilinear "; break;
                        }
                        infoText += $"\nAnisotropic level: {previewTexture.m_TextureSettings.m_Aniso}\nMip map bias: {previewTexture.m_TextureSettings.m_MipBias}";
                        switch (previewTexture.m_TextureSettings.m_WrapMode)
                        {
                            case 0: infoText += "\nWrap mode: Repeat"; break;
                            case 1: infoText += "\nWrap mode: Clamp"; break;
                        }

                        // Perform .resS I/O, Crunch unpack, and CPU fallback decode (e.g., ASTC) strictly off the UI/GL thread.
                        preparedPayload = TextureGLPreviewControl.PrepareGpuTexturePayload(previewTexture, glCapabilities, token);
                    }
                    else
                    {
                        var sprite = previewSprite!;
                        EnsureSpriteDependenciesInBackground(sprite, token);
                        token.ThrowIfCancellationRequested();

                        using var decodedImage = sprite.GetImage();
                        if (decodedImage == null)
                        {
                            throw new InvalidOperationException("Failed to decode sprite image on CPU.");
                        }

                        infoText = $"Width: {decodedImage.Width}\nHeight: {decodedImage.Height}\n";
                        preparedPayload = TextureGLPreviewControl.PrepareGpuImagePayload(decodedImage, glCapabilities, token);
                    }

                    token.ThrowIfCancellationRequested();

                    int validChannel = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (channelsSnapshot[i]) validChannel++;
                    }

                    infoText += "\nChannels: ";
                    if (validChannel == 0)
                    {
                        infoText += "None";
                    }
                    else
                    {
                        var channelNames = new string[4] { "R", "G", "B", "A" };
                        var activeList = new List<string>(4);
                        for (int i = 0; i < 4; i++)
                        {
                            if (channelsSnapshot[i])
                                activeList.Add(channelNames[i]);
                        }
                        infoText += string.Join(" ", activeList);
                    }
                    infoText += "\nRender mode: GPU (OpenGL)";

                    string finalInfoText = infoText;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            return;
                        }

                        try
                        {
                            ImagePreviewBox.IsVisible = false;
                            GLPreviewControl.IsVisible = false;
                            TextPreviewBox.IsVisible = false;
                            PreviewLabel.IsVisible = false;
                            glControl.IsVisible = true;
                            glControl.Focus();

                            glControl.SetPreparedTexture(preparedPayload);
                            glControl.SetChannels(textureChannels);

                            if (displayInfo.IsChecked == true)
                            {
                                PreviewInfoOverlay.Text = finalInfoText;
                                PreviewInfoBorder.IsVisible = true;
                            }
                            else
                            {
                                PreviewInfoBorder.IsVisible = false;
                            }

                            ClearPreviewCacheBadge();
                            StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle | Drag to Pan, Scroll to Zoom");
                        }
                        catch (Exception ex)
                        {
                            logger.Log(LoggerEvent.Warning, $"GPU texture preview failed setup: {ex.Message}. Falling back to CPU.");
                            UpdateImagePreview(forceCpu: true);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.Log(LoggerEvent.Warning, $"GPU texture preview failed preparation: {ex.Message}. Falling back to CPU.");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            UpdateImagePreview(forceCpu: true);
                        }
                    });
                }
            }, token);
            return;
        }

        // CPU Fallback path
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }

        StatusStripUpdate("Loading preview (CPU)...");

        _ = Task.Run(() =>
        {
            TexturePreviewImageResult? cachedTextureResult = null;
            SKBitmap? ownedSpriteImage = null;
            try
            {
                token.ThrowIfCancellationRequested();
                SKBitmap? image = null;
                string infoText = string.Empty;
                bool isTexture = previewTexture != null;
                bool previewFromCache = false;
                bool previewDownscaled = false;

                if (previewTexture != null)
                {
                    cachedTextureResult = LoadTexturePreviewThumbnail(previewTexture, MaxInlinePreviewTextureDimension);
                    image = cachedTextureResult?.Image;
                    previewFromCache = cachedTextureResult?.FromCache == true;
                    previewDownscaled = cachedTextureResult?.Downscaled == true;
                    if (image != null)
                    {
                        infoText = $"Width: {previewTexture.m_Width}\nHeight: {previewTexture.m_Height}\nFormat: {previewTexture.m_TextureFormat}";
                        switch (previewTexture.m_TextureSettings.m_FilterMode)
                        {
                            case 0: infoText += "\nFilter Mode: Point "; break;
                            case 1: infoText += "\nFilter Mode: Bilinear "; break;
                            case 2: infoText += "\nFilter Mode: Trilinear "; break;
                        }
                        infoText += $"\nAnisotropic level: {previewTexture.m_TextureSettings.m_Aniso}\nMip map bias: {previewTexture.m_TextureSettings.m_MipBias}";
                        switch (previewTexture.m_TextureSettings.m_WrapMode)
                        {
                            case 0: infoText += "\nWrap mode: Repeat"; break;
                            case 1: infoText += "\nWrap mode: Clamp"; break;
                        }
                        if (previewDownscaled)
                        {
                            infoText += $"\nPreview texture downscaled to {image.Width}x{image.Height}";
                        }
                        if (previewFromCache)
                        {
                            infoText += "\nPreview texture loaded from cache";
                        }
                    }
                }
                else if (previewSprite != null)
                {
                    EnsureSpriteDependenciesInBackground(previewSprite, token);
                    token.ThrowIfCancellationRequested();
                    ownedSpriteImage = previewSprite.GetImage();
                    image = ownedSpriteImage;
                    if (image != null)
                    {
                        infoText = $"Width: {image.Width}\nHeight: {image.Height}\n";
                    }
                }

                token.ThrowIfCancellationRequested();

                if (image == null)
                {
                    string failReason = "Unsupported image for preview";
                    if (previewTexture != null)
                    {
                        failReason = $"Unsupported Texture Format: {previewTexture.m_TextureFormat}";
                    }
                    else if (previewSprite != null)
                    {
                        if (previewSprite.m_SpriteAtlas != null && previewSprite.m_SpriteAtlas.TryGet(out var atlas) && atlas.m_RenderDataMap.TryGetValue(previewSprite.m_RenderDataKey, out var atlasData) && atlasData.texture.TryGet(out var tex1))
                            failReason = $"Unsupported Sprite Texture Format: {tex1.m_TextureFormat}";
                        else if (previewSprite.m_RD.texture.TryGet(out var tex2))
                            failReason = $"Unsupported Sprite Texture Format: {tex2.m_TextureFormat}";
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            ClearPreviewCacheBadge();
                            StatusStripUpdate(failReason);
                            ImagePreviewBox.IsVisible = false;
                            PreviewInfoBorder.IsVisible = false;
                        }
                    });
                    return;
                }

                {
                    int validChannel = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (channelsSnapshot[i])
                        {
                            validChannel++;
                        }
                    }

                    if (validChannel != 4)
                    {
                        ApplyPreviewChannelMask(image, channelsSnapshot, validChannel);
                    }

                    token.ThrowIfCancellationRequested();
                    var bitmap = CreateWriteableBitmapFromSKBitmap(image);

                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        bitmap.Dispose();
                        return;
                    }

                    infoText += "\nChannels: ";
                    if (validChannel == 0)
                    {
                        infoText += "None";
                    }
                    else
                    {
                        var channelNames = new string[4] { "R", "G", "B", "A" };
                        var activeList = new List<string>(4);
                        for (int i = 0; i < 4; i++)
                        {
                            if (channelsSnapshot[i])
                                activeList.Add(channelNames[i]);
                        }
                        infoText += string.Join(" ", activeList);
                    }
                    infoText += "\nRender mode: CPU (Fallback)";

                    string finalInfoText = infoText;
                    bool finalFromCache = previewFromCache;
                    string? finalSourceKind = isTexture ? "texture-thumbnail-png" : "sprite";
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                        {
                            ImagePreviewBox.Source = bitmap;
                            ImagePreviewBox.IsVisible = true;
                            TextPreviewBox.IsVisible = false;
                            PreviewLabel.IsVisible = false;

                            if (displayInfo.IsChecked == true)
                            {
                                PreviewInfoOverlay.Text = finalInfoText;
                                PreviewInfoBorder.IsVisible = true;
                            }
                            else
                            {
                                PreviewInfoBorder.IsVisible = false;
                            }

                            UpdatePreviewCacheBadge(fromCache: finalFromCache, sourceKind: finalSourceKind);

                            if (isTexture)
                            {
                                StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle");
                            }
                            else
                            {
                                StatusStripUpdate(string.Empty);
                            }
                        }
                        else
                        {
                            bitmap.Dispose();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        ClearPreviewCacheBadge();
                        StatusStripUpdate($"Error generating preview: {ex.Message}");
                        ImagePreviewBox.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                    }
                });
            }
            finally
            {
                cachedTextureResult?.Dispose();
                ownedSpriteImage?.Dispose();
            }
        }, token);
    }

    public static global::Avalonia.Media.Imaging.WriteableBitmap CreateWriteableBitmapFromSKBitmap(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(image), $"Bitmap dimensions must be positive ({width}x{height}).");
        }

        SKBitmap source = image;
        SKBitmap? converted = null;
        try
        {
            if (image.ColorType != SKColorType.Bgra8888)
            {
                var targetAlpha = image.AlphaType switch
                {
                    SKAlphaType.Opaque => SKAlphaType.Opaque,
                    SKAlphaType.Premul => SKAlphaType.Premul,
                    _ => SKAlphaType.Unpremul
                };
                var targetInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, targetAlpha);
                converted = new SKBitmap(targetInfo);
                using var pixmap = image.PeekPixels();
                bool copied = pixmap != null
                    ? pixmap.ReadPixels(targetInfo, converted.GetPixels(), converted.RowBytes, 0, 0)
                    : image.CopyTo(converted, SKColorType.Bgra8888);
                if (!copied)
                {
                    throw new InvalidOperationException($"Failed to convert bitmap from {image.ColorType} to Bgra8888.");
                }
                source = converted;
            }

            var alphaFormat = source.AlphaType switch
            {
                SKAlphaType.Opaque => global::Avalonia.Platform.AlphaFormat.Opaque,
                SKAlphaType.Premul => global::Avalonia.Platform.AlphaFormat.Premul,
                _ => global::Avalonia.Platform.AlphaFormat.Unpremul
            };

            var wb = new global::Avalonia.Media.Imaging.WriteableBitmap(
                new global::Avalonia.PixelSize(width, height),
                new global::Avalonia.Vector(96, 96),
                global::Avalonia.Platform.PixelFormat.Bgra8888,
                alphaFormat);
            try
            {
                using var fb = wb.Lock();
                CopyBgraBitmapToFramebuffer(source, fb.Address, fb.RowBytes);
                return wb;
            }
            catch
            {
                wb.Dispose();
                throw;
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    public static unsafe void CopyBgraBitmapToFramebuffer(SKBitmap source, IntPtr destinationAddress, int destinationRowBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destinationAddress == IntPtr.Zero)
        {
            throw new ArgumentException("Destination framebuffer address is zero.", nameof(destinationAddress));
        }

        int width = source.Width;
        int height = source.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), $"Bitmap dimensions must be positive ({width}x{height}).");
        }

        int bytesPerRow = checked(width * 4);
        int srcRowBytes = source.RowBytes;
        if (srcRowBytes < bytesPerRow)
        {
            throw new InvalidOperationException($"Source bitmap RowBytes ({srcRowBytes}) is smaller than row byte width ({bytesPerRow}).");
        }
        if (destinationRowBytes < bytesPerRow)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationRowBytes), $"Destination RowBytes ({destinationRowBytes}) is smaller than row byte width ({bytesPerRow}).");
        }

        IntPtr srcPtr = source.GetPixels();
        if (srcPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Source bitmap has no accessible pixel buffer.");
        }

        if (srcRowBytes == bytesPerRow && destinationRowBytes == bytesPerRow)
        {
            long totalBytes = (long)bytesPerRow * height;
            Buffer.MemoryCopy((void*)srcPtr, (void*)destinationAddress, totalBytes, totalBytes);
        }
        else
        {
            byte* srcBase = (byte*)srcPtr;
            byte* dstBase = (byte*)destinationAddress;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    srcBase + (long)y * srcRowBytes,
                    dstBase + (long)y * destinationRowBytes,
                    destinationRowBytes,
                    bytesPerRow);
            }
        }

        GC.KeepAlive(source);
    }

}
