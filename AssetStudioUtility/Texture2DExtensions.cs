using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.IO;

namespace AssetStudio
{
    public static class Texture2DExtensions
    {
        public static Image<Bgra32> ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            if (m_Texture2D.m_Width <= 0 || m_Texture2D.m_Height <= 0)
            {
                Logger.Warning($"Invalid texture size for {m_Texture2D.m_Name}: {m_Texture2D.m_Width}x{m_Texture2D.m_Height}");
                return null;
            }

            var converter = new Texture2DConverter(m_Texture2D);
            byte[] buff;
            try
            {
                var outputSize = (long)m_Texture2D.m_Width * m_Texture2D.m_Height * 4;
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
                    var image = Image.LoadPixelData<Bgra32>(buff, m_Texture2D.m_Width, m_Texture2D.m_Height);
                    if (flip)
                    {
                        image.Mutate(x => x.Flip(FlipMode.Vertical));
                    }
                    return image;
                }
                return m_Texture2D.ConvertEncodedImage(flip);
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(buff);
            }
        }

        private static Image<Bgra32> ConvertEncodedImage(this Texture2D m_Texture2D, bool flip)
        {
            try
            {
                var data = m_Texture2D.image_data.GetData();
                if (data == null || data.Length == 0)
                {
                    return null;
                }

                var image = Image.Load<Bgra32>(data);
                if (flip)
                {
                    image.Mutate(x => x.Flip(FlipMode.Vertical));
                }
                return image;
            }
            catch
            {
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
