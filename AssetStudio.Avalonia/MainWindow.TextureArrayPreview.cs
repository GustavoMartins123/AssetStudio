using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private Texture2DArray? currentPreviewTextureArray;
    private int currentPreviewTextureArraySlice;
    private TextureArraySliceViewModel? _textureArraySlice;

    /// <summary>
    /// Slice selector shown over the texture preview while a Texture2DArray is previewed.
    /// </summary>
    public TextureArraySliceViewModel TextureArraySlice => _textureArraySlice ??= CreateTextureArraySliceViewModel();

    private TextureArraySliceViewModel CreateTextureArraySliceViewModel()
    {
        var viewModel = new TextureArraySliceViewModel();
        viewModel.SliceChanged += OnTextureArraySliceChanged;
        return viewModel;
    }

    private void PreviewTexture2DArray(AssetItem assetItem, Texture2DArray textureArray)
    {
        currentPreviewTextureArray = textureArray;
        currentPreviewTextureArraySlice = 0;
        TextureArraySlice.Show(textureArray.m_Depth);
        PreviewSession.ActivateSurface(PreviewSurfaceKind.Texture, assetItem, isLoading: true);
        UpdateImagePreview();
    }

    private void ResetTextureArrayPreview()
    {
        currentPreviewTextureArray = null;
        currentPreviewTextureArraySlice = 0;
        _textureArraySlice?.Hide();
    }

    private void OnTextureArraySliceChanged(int slice)
    {
        if (currentPreviewTextureArray == null || slice == currentPreviewTextureArraySlice)
        {
            return;
        }

        currentPreviewTextureArraySlice = slice;
        UpdateImagePreview();
    }

    /// <summary>
    /// Decodes only the requested slice (the converter reads just that slice's byte range).
    /// </summary>
    private static SKBitmap? DecodeTextureArraySliceForPreview(Texture2DArray textureArray, int slice)
    {
        return new Texture2DArrayConverter(textureArray).ConvertSliceToImage(slice, flip: true);
    }

    private static string BuildTextureArrayPreviewInfo(Texture2DArray textureArray, int slice)
    {
        return $"Width: {textureArray.m_Width}\nHeight: {textureArray.m_Height}\nFormat: {textureArray.m_TextureFormat}"
            + $"\nSlice: {slice + 1} of {Math.Max(1, textureArray.m_Depth)}\nMip count: {textureArray.m_MipCount}";
    }

    private static string DescribeTextureArrayFailure(Texture2DArray textureArray)
    {
        if (textureArray.IsTruncated)
        {
            return "Texture2DArray data is truncated";
        }

        return textureArray.IsFormatSupported
            ? $"Could not decode Texture2DArray slice ({textureArray.m_TextureFormat})"
            : $"Unsupported Texture2DArray format: {textureArray.m_TextureFormat}";
    }
}
