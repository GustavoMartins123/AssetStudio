using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AssetStudio.Avalonia;

internal sealed class TexturePreviewImageResult : System.IDisposable
{
    public TexturePreviewImageResult(Image<Bgra32> image, bool fromCache, bool downscaled, int sourceWidth, int sourceHeight)
    {
        Image = image;
        FromCache = fromCache;
        Downscaled = downscaled;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public Image<Bgra32> Image { get; }
    public bool FromCache { get; }
    public bool Downscaled { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public void Dispose()
    {
        Image.Dispose();
    }
}
