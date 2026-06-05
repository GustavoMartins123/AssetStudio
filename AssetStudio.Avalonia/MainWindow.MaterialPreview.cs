using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using AssetStudio;
using SixLabors.ImageSharp.PixelFormats;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewMaterial(AssetItem assetItem, Material m_Material)
    {
        HideAnimationPlayback();
        if (GLPreviewControl != null)
        {
            GLPreviewControl.StopAnimation();
            GLPreviewControl.IsVisible = false;
        }

        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }

        currentPreviewTexture = null;
        var currentId = ++texturePreviewIdCounter;
        TextPreviewBox.Text = $"Material: {m_Material.m_Name}\n\nLoading material preview...";
        TextPreviewBox.IsVisible = true;
        ImagePreviewBox.IsVisible = false;
        PreviewLabel.IsVisible = false;
        PreviewInfoBorder.IsVisible = false;
        StatusStripUpdate("Loading material preview...");

        Task.Run(() =>
        {
            TexturePreviewImageResult? previewImage = null;
            try
            {
                try
                {
                    EnsureMaterialPreviewDependenciesLoaded(m_Material);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to prepare material preview dependencies for {m_Material.m_Name}: {ex.Message}");
                }

                var previewData = BuildMaterialPreviewData(m_Material);
                if (currentId != texturePreviewIdCounter)
                {
                    return;
                }

                if (previewData.PreviewTexture == null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                        {
                            return;
                        }

                        TextPreviewBox.Text = previewData.InfoText;
                        TextPreviewBox.IsVisible = true;
                        ImagePreviewBox.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                        StatusStripUpdate("Material loaded (no texture).");
                    });
                    return;
                }

                previewImage = LoadTexturePreviewThumbnail(previewData.PreviewTexture, MaxCachedPreviewTextureDimension);
                var loadedPreviewImage = previewImage;
                var image = loadedPreviewImage?.Image;
                if (image == null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                        {
                            return;
                        }

                        TextPreviewBox.Text = previewData.InfoText;
                        TextPreviewBox.IsVisible = true;
                        ImagePreviewBox.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                        StatusStripUpdate("Material loaded (no preview texture support).");
                    });
                    return;
                }

                var activePreviewImage = loadedPreviewImage!;
                var materialPreviewWasDownscaled = activePreviewImage.Downscaled;
                var materialPreviewFromCache = activePreviewImage.FromCache;
                var materialPreviewWidth = image.Width;
                var materialPreviewHeight = image.Height;

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
                    image.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < accessor.Height; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (int x = 0; x < accessor.Width; x++)
                            {
                                ref Bgra32 pixel = ref row[x];
                                pixel.R = textureChannels[0] ? pixel.R : (validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue);
                                pixel.G = textureChannels[1] ? pixel.G : (validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue);
                                pixel.B = textureChannels[2] ? pixel.B : (validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue);
                                pixel.A = textureChannels[3] ? pixel.A : byte.MaxValue;
                            }
                        }
                    });
                }
                else
                {
                    MakeAlphaOnlyTextureVisible(image);
                }

                var postedPreviewImage = activePreviewImage;
                var postedPreviewTexture = previewData.PreviewTexture!;
                previewImage = null;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (currentId != texturePreviewIdCounter || !ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                        {
                            return;
                        }

                        currentPreviewTexture = postedPreviewTexture;
                        if (GLPreviewControl != null)
                        {
                            GLPreviewControl.SetMaterialTexture(postedPreviewImage.Image);
                            GLPreviewControl.IsVisible = true;
                            HidePreviewGeometryControls();
                            GLPreviewControl.Focus();
                        }

                        ImagePreviewBox.IsVisible = false;
                        TextPreviewBox.IsVisible = false;
                        PreviewLabel.IsVisible = false;

                        if (displayInfo.IsChecked == true)
                        {
                            var previewInfoText = previewData.InfoText;
                            if (materialPreviewWasDownscaled)
                            {
                                previewInfoText += $"\nPreview texture downscaled to {materialPreviewWidth}x{materialPreviewHeight}";
                            }
                            if (materialPreviewFromCache)
                            {
                                previewInfoText += "\nPreview texture loaded from cache";
                            }

                            PreviewInfoOverlay.Text = previewInfoText;
                            PreviewInfoBorder.IsVisible = true;
                        }
                        else
                        {
                            PreviewInfoBorder.IsVisible = false;
                        }

                        StatusStripUpdate($"Material preview loaded: {postedPreviewTexture.m_Name}");
                    }
                    catch (Exception ex)
                    {
                        logger.Log(LoggerEvent.Error, $"Material preview UI failed for {m_Material.m_Name}: {ex}");
                        if (currentId == texturePreviewIdCounter)
                        {
                            TextPreviewBox.Text = previewData.InfoText + "\n[Error showing preview texture: " + ex.Message + "]";
                            TextPreviewBox.IsVisible = true;
                            ImagePreviewBox.IsVisible = false;
                            PreviewInfoBorder.IsVisible = false;
                            if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                            StatusStripUpdate("Material preview UI error.");
                        }
                    }
                    finally
                    {
                        postedPreviewImage.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (currentId == texturePreviewIdCounter && ReferenceEquals(AssetListDataGrid.SelectedItem, assetItem))
                    {
                        TextPreviewBox.Text = $"Material: {m_Material.m_Name}\n\n[Error loading material preview: {ex.Message}]";
                        TextPreviewBox.IsVisible = true;
                        ImagePreviewBox.IsVisible = false;
                        PreviewInfoBorder.IsVisible = false;
                        if (GLPreviewControl != null) GLPreviewControl.IsVisible = false;
                        StatusStripUpdate("Material preview error.");
                    }
                });
            }
            finally
            {
                previewImage?.Dispose();
            }
        });
    }

    private MaterialPreviewData BuildMaterialPreviewData(Material material)
    {
        return new MaterialPreviewBuilder(
            assetsManager,
            ResolveMaterialForPreview,
            GetMaterialTextureSlot,
            FindTextureForMaterial).Build(material);
    }
}
