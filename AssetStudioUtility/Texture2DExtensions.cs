using SkiaSharp;
using System;
using System.IO;

namespace AssetStudio
{
    public static class Texture2DExtensions
    {
        public static SKBitmap ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            if (m_Texture2D.m_Width <= 0 || m_Texture2D.m_Height <= 0)
            {
                Logger.Warning($"Invalid texture size for {m_Texture2D.m_Name}: {m_Texture2D.m_Width}x{m_Texture2D.m_Height}");
                return null;
            }

            var converter = new Texture2DConverter(m_Texture2D);
            byte[] buff;
            long outputSize;
            try
            {
                outputSize = (long)m_Texture2D.m_Width * m_Texture2D.m_Height * 4;
                if (outputSize > int.MaxValue)
                {
                    Logger.Warning($"Texture too large for preview {m_Texture2D.m_Name}: {m_Texture2D.m_Width}x{m_Texture2D.m_Height}");
                    return null;
                }
                buff = BigArrayPool<byte>.Shared.Rent((int)outputSize);
            }
            catch (OutOfMemoryException)
            {
                Logger.Warning($"Out of memory allocating image buffer for {m_Texture2D.m_Name} ({m_Texture2D.m_Width}x{m_Texture2D.m_Height})");
                return null;
            }
            try
            {
                if (converter.DecodeTexture2D(buff))
                {
                    return ImageExtensions.CreateBgraUnpremulBitmap(
                        buff.AsSpan(0, (int)outputSize),
                        m_Texture2D.m_Width,
                        m_Texture2D.m_Height,
                        flipVertical: flip);
                }
                return m_Texture2D.ConvertEncodedImage(flip);
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(buff);
            }
        }

        private static SKBitmap ConvertEncodedImage(this Texture2D m_Texture2D, bool flip)
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

        public static MemoryStream ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip)
        {
            var image = ConvertToImage(m_Texture2D, flip);
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
