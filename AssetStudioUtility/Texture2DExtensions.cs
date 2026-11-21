using SkiaSharp;
using System;
using System.IO;

namespace AssetStudio
{
    public static class Texture2DExtensions
    {
        public static SKBitmap? ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            return ConvertToImage(m_Texture2D, flip, unpackNormalMap: true, forceNormalMapUnpack: false);
        }

        public static SKBitmap? ConvertToImage(this Texture2D m_Texture2D, bool flip, bool unpackNormalMap, bool forceNormalMapUnpack = false)
        {
            if (m_Texture2D.m_Width <= 0 || m_Texture2D.m_Height <= 0)
            {
                Logger.Warning($"Invalid texture size for {m_Texture2D.m_Name}: {m_Texture2D.m_Width}x{m_Texture2D.m_Height}");
                return null;
            }

            long outputSize = (long)m_Texture2D.m_Width * m_Texture2D.m_Height * 4L;
            if (outputSize <= 0 || outputSize > int.MaxValue)
            {
                Logger.Warning($"Texture too large for preview {m_Texture2D.m_Name}: {m_Texture2D.m_Width}x{m_Texture2D.m_Height}");
                return null;
            }

            var converter = new Texture2DConverter(m_Texture2D);
            if (!converter.CanDecode())
            {
                return m_Texture2D.ConvertEncodedImage(flip);
            }

            if (converter.SupportsDirectSpanDecode)
            {
                SKBitmap? bitmap = null;
                try
                {
                    var info = new SKImageInfo(m_Texture2D.m_Width, m_Texture2D.m_Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    bitmap = new SKBitmap(info);
                    var dstSpan = bitmap.GetWritablePixelSpan();
                    int expectedRowBytes = checked(m_Texture2D.m_Width * 4);
                    if (bitmap.RowBytes == expectedRowBytes && dstSpan.Length >= (int)outputSize)
                    {
                        var pixelSpan = dstSpan.Slice(0, (int)outputSize);
                        if (converter.DecodeTexture2D(pixelSpan))
                        {
                            if (unpackNormalMap)
                            {
                                Texture2DConverter.UnpackNormalMapBgra32(pixelSpan, m_Texture2D.m_TextureFormat, forceNormalMapUnpack);
                            }
                            if (flip)
                            {
                                bitmap.FlipVerticalInPlace();
                            }
                            GC.KeepAlive(bitmap);
                            var result = bitmap;
                            bitmap = null;
                            return result;
                        }

                        bitmap.Dispose();
                        bitmap = null;
                        return m_Texture2D.ConvertEncodedImage(flip);
                    }
                }
                catch (OutOfMemoryException)
                {
                    bitmap?.Dispose();
                    Logger.Warning($"Out of memory allocating image buffer for {m_Texture2D.m_Name} ({m_Texture2D.m_Width}x{m_Texture2D.m_Height})");
                    return null;
                }
                finally
                {
                    bitmap?.Dispose();
                }
            }

            byte[] pixels;
            try
            {
                pixels = GC.AllocateUninitializedArray<byte>((int)outputSize, pinned: true);
            }
            catch (OutOfMemoryException)
            {
                Logger.Warning($"Out of memory allocating image buffer for {m_Texture2D.m_Name} ({m_Texture2D.m_Width}x{m_Texture2D.m_Height})");
                return null;
            }

            if (converter.DecodeTexture2D(pixels))
            {
                var pixelSpan = pixels.AsSpan(0, (int)outputSize);
                if (unpackNormalMap)
                {
                    Texture2DConverter.UnpackNormalMapBgra32(pixelSpan, m_Texture2D.m_TextureFormat, forceNormalMapUnpack);
                }
                if (flip)
                {
                    int rowBytes = checked(m_Texture2D.m_Width * 4);
                    ImageExtensions.FlipVerticalInPlace(pixelSpan, rowBytes, rowBytes, m_Texture2D.m_Height);
                }

                return ImageExtensions.WrapBgraUnpremulBitmap(pixels, m_Texture2D.m_Width, m_Texture2D.m_Height);
            }

            return m_Texture2D.ConvertEncodedImage(flip);
        }

        private static SKBitmap? ConvertEncodedImage(this Texture2D m_Texture2D, bool flip)
        {
            try
            {
                var data = m_Texture2D.image_data.GetData();
                if (data == null || data.Length == 0)
                {
                    return null;
                }

                var image = ImageExtensions.DecodeBgraUnpremul(data);
                if (image == null)
                {
                    return null;
                }

                if (flip)
                {
                    image.FlipVerticalInPlace();
                }
                return image;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to decode encoded image for Texture2D {m_Texture2D.m_Name}: {ex.Message}");
                return null;
            }
        }

        public static MemoryStream? ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip)
        {
            return ConvertToStream(m_Texture2D, imageFormat, flip, unpackNormalMap: true, forceNormalMapUnpack: false);
        }

        public static MemoryStream? ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip, bool unpackNormalMap, bool forceNormalMapUnpack = false)
        {
            var image = ConvertToImage(m_Texture2D, flip, unpackNormalMap, forceNormalMapUnpack);
            if (image != null)
            {
                using (image)
                {
                    return image.ConvertToStream(imageFormat);
                }
            }
            return null;
        }
    }
}
