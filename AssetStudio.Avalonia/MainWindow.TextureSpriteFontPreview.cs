using Avalonia.Threading;
using AssetStudio;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewTexture2D(AssetItem assetItem, Texture2D m_Texture2D)
    {
        currentPreviewTexture = m_Texture2D;
        UpdateImagePreview();
    }

    private void PreviewSprite(AssetItem assetItem, Sprite m_Sprite)
    {
        EnsureSpritePreviewDependenciesLoaded(m_Sprite);
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
            return;
        }

        long currentId = ++texturePreviewIdCounter;
        StatusStripUpdate("Rendering font preview...");

        Task.Run(() =>
        {
            try
            {
                var fontPreview = FontAssetPreviewRenderer.Render(m_Font.m_Name, m_Font.m_FontData, () => currentId != texturePreviewIdCounter);

                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter)
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
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter)
                    {
                        SetTextWithTruncation(TextPreviewBox, FontAssetPreviewRenderer.BuildFallbackText(m_Font, ex.Message));
                        ImagePreviewBox.IsVisible = false;
                        TextPreviewBox.IsVisible = true;
                        PreviewLabel.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                        StatusStripUpdate($"Unsupported font preview: {ex.Message}");
                    }
                });
            }
        });
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

    private void UpdateImagePreview(bool forceCpu = false)
    {
        var previewTexture = currentPreviewTexture;
        var previewSprite = currentPreviewSprite;
        if (previewTexture == null && previewSprite == null)
            return;

        long currentId = ++texturePreviewIdCounter;

        if (useGpuTexturePreview && TextureGLPreview != null && !forceCpu)
        {
            StatusStripUpdate("Loading preview (GPU)...");

            Task.Run(() =>
            {
                try
                {
                    SKBitmap? decodedImage = null;
                    int width = 0;
                    int height = 0;
                    string infoText = string.Empty;
                    bool isSprite = previewSprite != null;

                    if (previewTexture != null)
                    {
                        width = previewTexture.m_Width;
                        height = previewTexture.m_Height;

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
                    }
                    else if (previewSprite != null)
                    {
                        decodedImage = previewSprite.GetImage();
                        if (decodedImage == null)
                        {
                            throw new Exception("Failed to decode sprite image on CPU.");
                        }
                        width = decodedImage.Width;
                        height = decodedImage.Height;
                        infoText = $"Width: {width}\nHeight: {height}\n";
                    }

                    int validChannel = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (textureChannels[i]) validChannel++;
                    }

                    infoText += "\nChannels: ";
                    if (validChannel == 0)
                    {
                        infoText += "None";
                    }
                    else
                    {
                        var channelNames = new string[4] { "R", "G", "B", "A" };
                        var activeList = new List<string>();
                        for (int i = 0; i < 4; i++)
                        {
                            if (textureChannels[i])
                                activeList.Add(channelNames[i]);
                        }
                        infoText += string.Join(" ", activeList);
                    }
                    infoText += "\nRender mode: GPU (OpenGL)";

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            try
                            {
                                ImagePreviewBox.IsVisible = false;
                                GLPreviewControl.IsVisible = false;
                                TextPreviewBox.IsVisible = false;
                                PreviewLabel.IsVisible = false;
                                TextureGLPreview.IsVisible = true;
                                TextureGLPreview.Focus();

                                if (isSprite && decodedImage != null)
                                {
                                    TextureGLPreview.SetImage(decodedImage);
                                }
                                else if (previewTexture != null)
                                {
                                    TextureGLPreview.SetTexture(previewTexture);
                                }
                                TextureGLPreview.SetChannels(textureChannels);

                                if (displayInfo.IsChecked == true)
                                {
                                    PreviewInfoOverlay.Text = infoText;
                                    PreviewInfoBorder.IsVisible = true;
                                }
                                else
                                {
                                    PreviewInfoBorder.IsVisible = false;
                                }

                                StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle | Drag to Pan, Scroll to Zoom");
                            }
                            catch (Exception ex)
                            {
                                logger.Log(LoggerEvent.Warning, $"GPU texture preview failed setup: {ex.Message}. Falling back to CPU.");
                                UpdateImagePreview(forceCpu: true);
                            }
                            finally
                            {
                                decodedImage?.Dispose();
                            }
                        }
                        else
                        {
                            decodedImage?.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    logger.Log(LoggerEvent.Warning, $"GPU texture preview failed preparation: {ex.Message}. Falling back to CPU.");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId == texturePreviewIdCounter)
                        {
                            UpdateImagePreview(forceCpu: true);
                        }
                    });
                }
            });
            return;
        }

        // CPU Fallback path
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }

        StatusStripUpdate("Loading preview (CPU)...");

        Task.Run(() =>
        {
            try
            {
                SKBitmap? image = null;
                string infoText = string.Empty;
                bool isTexture = previewTexture != null;

                if (previewTexture != null)
                {
                    image = previewTexture.ConvertToImage(true);
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
                    }
                }
                else if (previewSprite != null)
                {
                    image = previewSprite.GetImage();
                    if (image != null)
                    {
                        infoText = $"Width: {image.Width}\nHeight: {image.Height}\n";
                    }
                }

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
                        if (currentId == texturePreviewIdCounter)
                        {
                            StatusStripUpdate(failReason);
                            ImagePreviewBox.IsVisible = false;
                            PreviewInfoBorder.IsVisible = false;
                        }
                    });
                    return;
                }

                using (image)
                {
                    int validChannel = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (textureChannels[i])
                        {
                            validChannel++;
                        }
                    }

                    if (validChannel != 4)
                    {
                        ApplyPreviewChannelMask(image, textureChannels, validChannel);
                    }

                    using (var ms = new MemoryStream())
                    {
                        image.WriteToStream(ms, ImageFormat.Png);
                        ms.Position = 0;
                        var bitmap = new global::Avalonia.Media.Imaging.Bitmap(ms);

                        infoText += "\nChannels: ";
                        if (validChannel == 0)
                        {
                            infoText += "None";
                        }
                        else
                        {
                            var channelNames = new string[4] { "R", "G", "B", "A" };
                            var activeList = new List<string>();
                            for (int i = 0; i < 4; i++)
                            {
                                if (textureChannels[i])
                                    activeList.Add(channelNames[i]);
                            }
                            infoText += string.Join(" ", activeList);
                        }
                        infoText += "\nRender mode: CPU (Fallback)";

                        string finalInfoText = infoText;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (currentId == texturePreviewIdCounter)
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
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter)
                    {
                        StatusStripUpdate($"Error generating preview: {ex.Message}");
                        ImagePreviewBox.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                    }
                });
            }
        });
    }

}