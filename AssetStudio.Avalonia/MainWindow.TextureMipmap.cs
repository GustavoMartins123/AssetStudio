using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private int currentPreviewTextureMipLevel;
    private TextureMipmapViewModel? _textureMipmap;

    /// <summary>
    /// Mipmap level selector and HDR/mipmap export commands shown while a Texture2D is previewed.
    /// </summary>
    public TextureMipmapViewModel TextureMipmap => _textureMipmap ??= CreateTextureMipmapViewModel();

    private TextureMipmapViewModel CreateTextureMipmapViewModel()
    {
        var viewModel = new TextureMipmapViewModel
        {
            ExportAllMipsHandler = ExportCurrentTextureMipChainCommandHandlerAsync,
            ExportHdrExrHandler = ExportCurrentTextureHdrExrCommandHandlerAsync,
            ExportHdrTiffHandler = ExportCurrentTextureHdrTiffCommandHandlerAsync
        };
        viewModel.MipChanged += OnTextureMipChanged;
        return viewModel;
    }

    private void ResetTextureMipmapPreview()
    {
        currentPreviewTextureMipLevel = 0;
        _textureMipmap?.Hide();
    }

    private void OnTextureMipChanged(int mipLevel)
    {
        if (currentPreviewTexture == null || mipLevel == currentPreviewTextureMipLevel)
        {
            return;
        }

        currentPreviewTextureMipLevel = mipLevel;
        UpdateImagePreview();
    }

    internal SKBitmap? DecodeTextureMipForPreview(Texture2D texture, int mipLevel, bool flip = true)
    {
        if (texture == null)
        {
            return null;
        }

        var decoded = TextureMipmapService.DecodeMipLevel(
            texture,
            mipLevel,
            flip: flip,
            unpackNormalMap: true,
            forceNormalMapUnpack: false);

        if (decoded == null && mipLevel == 0)
        {
            return texture.ConvertToImage(flip);
        }

        return decoded;
    }

    internal string BuildTextureMipPreviewInfo(Texture2D texture, int mipLevel)
    {
        if (texture == null)
        {
            return string.Empty;
        }

        var chain = TextureMipmapService.ComputeMipChain(texture);
        int clampedMip = chain.Count > 0 ? Math.Clamp(mipLevel, 0, chain.Count - 1) : 0;
        var sb = new StringBuilder();
        sb.Append($"Width: {texture.m_Width}");
        sb.Append($"\nHeight: {texture.m_Height}");

        bool isHdr = HdrTextureExporter.IsHdrFormat(texture.m_TextureFormat);
        sb.Append(isHdr
            ? $"\nFormat: {texture.m_TextureFormat} (HDR)"
            : $"\nFormat: {texture.m_TextureFormat}");

        if (chain.Count > 0)
        {
            var mip = chain[clampedMip];
            sb.Append($"\nMip Level: {mip.Level} / {chain.Count} ({mip.Width}×{mip.Height})");
            sb.Append($"\nMip Bytes: {FormatBytes(mip.ByteSize)}");
        }

        if (texture.m_TextureSettings != null)
        {
            switch (texture.m_TextureSettings.m_FilterMode)
            {
                case 0: sb.Append("\nFilter Mode: Point"); break;
                case 1: sb.Append("\nFilter Mode: Bilinear"); break;
                case 2: sb.Append("\nFilter Mode: Trilinear"); break;
            }
            sb.Append($"\nAnisotropic level: {texture.m_TextureSettings.m_Aniso}");
            sb.Append($"\nMip map bias: {texture.m_TextureSettings.m_MipBias}");
            switch (texture.m_TextureSettings.m_WrapMode)
            {
                case 0: sb.Append("\nWrap mode: Repeat"); break;
                case 1: sb.Append("\nWrap mode: Clamp"); break;
            }
        }

        return sb.ToString();
    }

    internal async Task ExportCurrentTextureMipChainCommandHandlerAsync()
    {
        var texture = ResolveTargetTexture2DForAction();
        if (texture == null)
        {
            StatusStripUpdate("No Texture2D selected for mipmap export.");
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select folder to export mipmap chain");
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        SaveExportFolder(savePath);
        var imageFormat = exportOptions.ConvertTextureFormat;
        StatusStripUpdate($"Exporting mipmap chain for '{texture.m_Name}'...");

        try
        {
            var result = await Task.Run(() => TextureMipmapService.ExportMipChain(
                texture,
                savePath,
                imageFormat: imageFormat,
                flip: true,
                unpackNormalMap: true,
                forceNormalMapUnpack: false));

            StatusStripUpdate($"Exported {result.ExportedMipLevels}/{result.TotalMipLevels} mip level(s) for '{result.TextureName}'.");
            if (exportOptions.OpenAfterExport && result.ExportedMipLevels > 0)
            {
                OpenFolder(result.OutputDirectory);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting texture mipmap chain", ex);
            StatusStripUpdate($"Mipmap export failed: {ex.Message}");
        }
    }

    internal async Task ExportCurrentTextureHdrExrCommandHandlerAsync()
    {
        var texture = ResolveTargetTexture2DForAction();
        if (texture == null)
        {
            StatusStripUpdate("No Texture2D selected for OpenEXR export.");
            return;
        }

        string safeName = AssetExportHelper.FixFileName(
            string.IsNullOrWhiteSpace(texture.m_Name) ? $"Texture2D_{texture.m_PathID}" : texture.m_Name);
        int mipLevel = _textureMipmap is { IsVisible: true } ? _textureMipmap.MipIndex : 0;

        var savePath = await ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: "Export HDR Texture as OpenEXR",
            suggestedFileName: $"{safeName}.exr",
            defaultExtension: "exr",
            suggestedStartDirectory: appSettings.ExportFolderPath,
            fileTypeFilters: new[]
            {
                new FileTypeFilterSpec("OpenEXR (.exr)", "*.exr")
            }));

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(folder))
        {
            SaveExportFolder(folder);
        }

        StatusStripUpdate($"Exporting '{texture.m_Name}' as OpenEXR...");

        try
        {
            var result = await Task.Run(() => HdrTextureExporter.ExportTextureToExr(
                texture,
                savePath,
                pixelFormat: ExrPixelFormat.HalfFloat,
                flipVertical: true,
                mipLevel: mipLevel));

            if (result.Success)
            {
                var rangeNote = result.Metadata is { PreservedTrueFloatRange: true }
                    ? $"true HDR range (max lum {result.Metadata.MaxLuminance:0.##})"
                    : "converted linear float";
                StatusStripUpdate($"Exported OpenEXR: {Path.GetFileName(result.OutputPath)} ({rangeNote}).");
                if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(folder))
                {
                    OpenFolder(folder);
                }
            }
            else
            {
                StatusStripUpdate($"OpenEXR export failed: {result.ErrorMessage ?? "Unknown error"}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting HDR OpenEXR", ex);
            StatusStripUpdate($"OpenEXR export failed: {ex.Message}");
        }
    }

    internal async Task ExportCurrentTextureHdrTiffCommandHandlerAsync()
    {
        var texture = ResolveTargetTexture2DForAction();
        if (texture == null)
        {
            StatusStripUpdate("No Texture2D selected for 32-bit Float TIFF export.");
            return;
        }

        string safeName = AssetExportHelper.FixFileName(
            string.IsNullOrWhiteSpace(texture.m_Name) ? $"Texture2D_{texture.m_PathID}" : texture.m_Name);
        int mipLevel = _textureMipmap is { IsVisible: true } ? _textureMipmap.MipIndex : 0;

        var savePath = await ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: "Export HDR Texture as 32-bit Float TIFF",
            suggestedFileName: $"{safeName}.tif",
            defaultExtension: "tif",
            suggestedStartDirectory: appSettings.ExportFolderPath,
            fileTypeFilters: new[]
            {
                new FileTypeFilterSpec("32-bit Float TIFF (.tif)", "*.tif", "*.tiff")
            }));

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(folder))
        {
            SaveExportFolder(folder);
        }

        StatusStripUpdate($"Exporting '{texture.m_Name}' as 32-bit Float TIFF...");

        try
        {
            var result = await Task.Run(() => HdrTextureExporter.ExportTextureToFloat32Tiff(
                texture,
                savePath,
                flipVertical: true,
                mipLevel: mipLevel));

            if (result.Success)
            {
                var rangeNote = result.Metadata is { PreservedTrueFloatRange: true }
                    ? $"true HDR range (max lum {result.Metadata.MaxLuminance:0.##})"
                    : "converted linear float";
                StatusStripUpdate($"Exported Float32 TIFF: {Path.GetFileName(result.OutputPath)} ({rangeNote}).");
                if (exportOptions.OpenAfterExport && !string.IsNullOrEmpty(folder))
                {
                    OpenFolder(folder);
                }
            }
            else
            {
                StatusStripUpdate($"Float32 TIFF export failed: {result.ErrorMessage ?? "Unknown error"}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting 32-bit Float TIFF", ex);
            StatusStripUpdate($"Float32 TIFF export failed: {ex.Message}");
        }
    }

    internal async Task ExportSelectedSpriteAtlasMetadataCommandHandlerAsync()
    {
        var atlases = ResolveSelectedSpriteAtlasesForAction();
        if (atlases.Count == 0)
        {
            StatusStripUpdate("No SpriteAtlas selected for batch/metadata export.");
            return;
        }

        var savePath = await PickExportFolderDialogAsync("Select folder to export SpriteAtlas batch & metadata");
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        SaveExportFolder(savePath);
        var options = new SpriteAtlasExportOptions
        {
            ImageFormat = exportOptions.ConvertTextureFormat,
            WebpQuality = exportOptions.WebpQuality,
            WebpLossless = exportOptions.WebpLossless,
            PreserveOriginalRect = false,
            ApplyAlphaMask = true,
            ExportImages = true,
            ExportManifestJson = true
        };

        StatusStripUpdate($"Exporting {atlases.Count} SpriteAtlas manifest(s)...");

        try
        {
            int totalSpritesExported = 0;
            int totalAtlasesExported = 0;

            await Task.Run(() =>
            {
                for (int i = 0; i < atlases.Count; i++)
                {
                    var atlas = atlases[i];
                    string targetDir = atlases.Count == 1
                        ? savePath
                        : Path.Combine(
                            savePath,
                            AssetExportHelper.FixFileName(
                                string.IsNullOrWhiteSpace(atlas.m_Name) ? $"SpriteAtlas_{atlas.m_PathID}" : atlas.m_Name));

                    var result = SpriteAtlasMetadataExporter.ExportSpriteAtlasBatch(atlas, targetDir, options);
                    totalSpritesExported += result.ExportedImages;
                    totalAtlasesExported++;
                }
            });

            StatusStripUpdate($"Exported {totalAtlasesExported} SpriteAtlas manifest(s) and {totalSpritesExported} sprite image(s).");
            if (exportOptions.OpenAfterExport)
            {
                OpenFolder(savePath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error exporting SpriteAtlas metadata", ex);
            StatusStripUpdate($"SpriteAtlas export failed: {ex.Message}");
        }
    }

    private Texture2D? ResolveTargetTexture2DForAction()
    {
        if (currentPreviewTexture != null)
        {
            return currentPreviewTexture;
        }

        var selected = GetSelectedAssets();
        for (int i = 0; i < selected.Count; i++)
        {
            var item = selected[i];
            if (item.Type != ClassIDType.Texture2D && item.Asset is not Texture2D)
            {
                continue;
            }

            if (item.Asset == null)
            {
                EnsureLazyAssetReadyForPreview(item);
            }

            if (item.Asset is Texture2D texture)
            {
                return texture;
            }
        }

        return null;
    }

    private List<SpriteAtlas> ResolveSelectedSpriteAtlasesForAction()
    {
        var atlases = new List<SpriteAtlas>();
        var seen = new HashSet<SpriteAtlas>(ReferenceEqualityComparer.Instance);

        var selected = GetSelectedAssets();
        for (int i = 0; i < selected.Count; i++)
        {
            var item = selected[i];
            if (item.Type == ClassIDType.SpriteAtlas || item.Asset is SpriteAtlas)
            {
                if (item.Asset == null)
                {
                    EnsureLazyAssetReadyForPreview(item);
                }

                if (item.Asset is SpriteAtlas atlas && seen.Add(atlas))
                {
                    atlases.Add(atlas);
                }
            }
            else if (item.Type == ClassIDType.Sprite || item.Asset is Sprite)
            {
                if (item.Asset == null)
                {
                    EnsureLazyAssetReadyForPreview(item);
                }

                if (item.Asset is Sprite sprite
                    && sprite.m_SpriteAtlas != null
                    && sprite.m_SpriteAtlas.TryGet(out var parentAtlas)
                    && parentAtlas != null
                    && seen.Add(parentAtlas))
                {
                    atlases.Add(parentAtlas);
                }
            }
        }

        if (atlases.Count == 0
            && currentPreviewSprite?.m_SpriteAtlas != null
            && currentPreviewSprite.m_SpriteAtlas.TryGet(out var previewAtlas)
            && previewAtlas != null)
        {
            atlases.Add(previewAtlas);
        }

        return atlases;
    }
}
