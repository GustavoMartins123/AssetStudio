using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.IO;

namespace AssetStudio
{
    public static class ImageExtensions
    {
        public static Span<byte> GetWritablePixelSpan(this SKBitmap bitmap)
        {
            if (bitmap == null)
            {
                return Span<byte>.Empty;
            }

            var ptr = bitmap.GetPixels();
            var byteCount = bitmap.ByteCount;
            if (ptr == IntPtr.Zero || byteCount <= 0)
            {
                return Span<byte>.Empty;
            }

            unsafe
            {
                return new Span<byte>((void*)ptr, byteCount);
            }
        }

        public static SKBitmap CreateBgraUnpremulBitmap(ReadOnlySpan<byte> bgraPixels, int width, int height, bool flipVertical = false)
        {
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            long requiredBytes = (long)width * height * 4L;
            if (requiredBytes > int.MaxValue || bgraPixels.Length < requiredBytes)
            {
                throw new ArgumentException($"Pixel buffer length ({bgraPixels.Length}) is smaller than required ({requiredBytes}) for {width}x{height} BGRA32.");
            }

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            try
            {
                var dst = bitmap.GetWritablePixelSpan();
                int srcRowBytes = width * 4;
                int dstRowBytes = bitmap.RowBytes;
                if (!flipVertical && dstRowBytes == srcRowBytes)
                {
                    bgraPixels.Slice(0, (int)requiredBytes).CopyTo(dst);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        int srcY = flipVertical ? (height - 1 - y) : y;
                        bgraPixels.Slice(srcY * srcRowBytes, srcRowBytes).CopyTo(dst.Slice(y * dstRowBytes, srcRowBytes));
                    }
                }
                GC.KeepAlive(bitmap);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        public static SKBitmap DecodeBgraUnpremul(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec == null)
            {
                return null;
            }

            var srcInfo = codec.Info;
            if (srcInfo.Width <= 0 || srcInfo.Height <= 0)
            {
                return null;
            }

            var dstInfo = new SKImageInfo(srcInfo.Width, srcInfo.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(dstInfo);
            try
            {
                var result = codec.GetPixels(dstInfo, bitmap.GetPixels());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    bitmap.Dispose();
                    return null;
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        public static void FlipVerticalInPlace(this SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.Height <= 1)
            {
                return;
            }

            int width = bitmap.Width;
            int height = bitmap.Height;
            int rowLength = width * bitmap.BytesPerPixel;
            int rowBytes = bitmap.RowBytes;
            if (rowLength <= 0 || rowBytes < rowLength)
            {
                return;
            }

            var pixels = bitmap.GetWritablePixelSpan();
            byte[] tempRow = BigArrayPool<byte>.Shared.Rent(rowLength);
            try
            {
                var tempSpan = tempRow.AsSpan(0, rowLength);
                int halfHeight = height / 2;
                for (int y = 0; y < halfHeight; y++)
                {
                    var topRow = pixels.Slice(y * rowBytes, rowLength);
                    var bottomRow = pixels.Slice((height - 1 - y) * rowBytes, rowLength);
                    topRow.CopyTo(tempSpan);
                    bottomRow.CopyTo(topRow);
                    tempSpan.CopyTo(bottomRow);
                }
                GC.KeepAlive(bitmap);
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(tempRow);
            }
        }

        public static void CopyBgraPixelsTo(this SKBitmap bitmap, byte[] destination)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            int width = bitmap.Width;
            int height = bitmap.Height;
            int expectedBytes = checked(width * height * 4);
            if (destination.Length < expectedBytes)
            {
                throw new ArgumentException("Destination buffer is too small.", nameof(destination));
            }

            SKBitmap source = bitmap;
            SKBitmap converted = null;
            try
            {
                if (bitmap.ColorType != SKColorType.Bgra8888)
                {
                    converted = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                    if (!bitmap.CopyTo(converted, SKColorType.Bgra8888))
                    {
                        throw new InvalidOperationException($"Failed to convert bitmap from {bitmap.ColorType} to Bgra8888.");
                    }
                    source = converted;
                }

                var srcSpan = source.GetPixelSpan();
                int srcRowBytes = source.RowBytes;
                int dstRowBytes = width * 4;
                if (srcRowBytes == dstRowBytes)
                {
                    srcSpan.Slice(0, expectedBytes).CopyTo(destination);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        srcSpan.Slice(y * srcRowBytes, dstRowBytes).CopyTo(destination.AsSpan(y * dstRowBytes, dstRowBytes));
                    }
                }
                GC.KeepAlive(source);
            }
            finally
            {
                converted?.Dispose();
            }
        }

        public static void WriteToStream(this SKBitmap image, Stream stream, ImageFormat imageFormat)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            switch (imageFormat)
            {
                case ImageFormat.Jpeg:
                    WriteJpeg(image, stream);
                    break;
                case ImageFormat.Png:
                    WritePng(image, stream);
                    break;
                case ImageFormat.Bmp:
                    WriteBmp32(image, stream);
                    break;
                case ImageFormat.Tga:
                    WriteTga32(image, stream);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, "Unsupported image format.");
            }
        }

        public static MemoryStream ConvertToStream(this SKBitmap image, ImageFormat imageFormat)
        {
            var stream = new MemoryStream();
            image.WriteToStream(stream, imageFormat);
            return stream;
        }

        private static void WritePng(SKBitmap image, Stream stream)
        {
            using var pixmap = image.PeekPixels();
            if (pixmap == null)
            {
                throw new InvalidOperationException("Failed to access bitmap pixels for PNG encoding.");
            }

            using var data = pixmap.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
            {
                throw new InvalidOperationException("Failed to encode bitmap as PNG.");
            }

            data.SaveTo(stream);
        }

        private static void WriteJpeg(SKBitmap image, Stream stream)
        {
            using var pixmap = image.PeekPixels();
            if (pixmap == null)
            {
                throw new InvalidOperationException("Failed to access bitmap pixels for JPEG encoding.");
            }

            using var opaquePixmap = new SKPixmap(pixmap.Info.WithAlphaType(SKAlphaType.Opaque), pixmap.GetPixels(), pixmap.RowBytes);
            using var data = opaquePixmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            if (data == null)
            {
                throw new InvalidOperationException("Failed to encode bitmap as JPEG.");
            }

            data.SaveTo(stream);
            GC.KeepAlive(image);
        }

        private static void WriteBmp32(SKBitmap image, Stream stream)
        {
            int width = image.Width;
            int height = image.Height;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"Invalid bitmap dimensions for BMP encoding: {width}x{height}.");
            }

            long imageSizeLong = (long)width * height * 4L;
            long fileSizeLong = 54L + imageSizeLong;
            if (fileSizeLong > uint.MaxValue)
            {
                throw new InvalidOperationException($"Bitmap is too large for BMP format: {width}x{height}.");
            }

            SKBitmap source = image;
            SKBitmap converted = null;
            try
            {
                if (image.ColorType != SKColorType.Bgra8888)
                {
                    converted = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                    if (!image.CopyTo(converted, SKColorType.Bgra8888))
                    {
                        throw new InvalidOperationException($"Failed to convert bitmap from {image.ColorType} to Bgra8888.");
                    }
                    source = converted;
                }

                Span<byte> header = stackalloc byte[54];
                header.Clear();
                header[0] = (byte)'B';
                header[1] = (byte)'M';
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(2, 4), (uint)fileSizeLong);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(10, 4), 54u);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), 40u);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(18, 4), width);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(22, 4), height);
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26, 2), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), 32);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(30, 4), 0u); // BI_RGB
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(34, 4), (uint)imageSizeLong);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(38, 4), 2835);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(42, 4), 2835);
                stream.Write(header);

                var pixels = source.GetPixelSpan();
                int srcRowBytes = source.RowBytes;
                int dstRowBytes = width * 4;
                for (int y = height - 1; y >= 0; y--)
                {
                    stream.Write(pixels.Slice(y * srcRowBytes, dstRowBytes));
                }
                GC.KeepAlive(source);
            }
            finally
            {
                converted?.Dispose();
            }
        }

        private static void WriteTga32(SKBitmap image, Stream stream)
        {
            int width = image.Width;
            int height = image.Height;
            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
            {
                throw new InvalidOperationException($"Invalid bitmap dimensions for TGA encoding: {width}x{height}.");
            }

            SKBitmap source = image;
            SKBitmap converted = null;
            try
            {
                if (image.ColorType != SKColorType.Bgra8888)
                {
                    converted = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                    if (!image.CopyTo(converted, SKColorType.Bgra8888))
                    {
                        throw new InvalidOperationException($"Failed to convert bitmap from {image.ColorType} to Bgra8888.");
                    }
                    source = converted;
                }

                Span<byte> header = stackalloc byte[18];
                header.Clear();
                header[0] = 0; // ID length
                header[1] = 0; // Color map type
                header[2] = 2; // Uncompressed true-color
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(12, 2), (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14, 2), (ushort)height);
                header[16] = 32; // 32bpp BGRA
                header[17] = 0x28; // Top-left origin (0x20) + 8 alpha bits (0x08)
                stream.Write(header);

                var pixels = source.GetPixelSpan();
                int srcRowBytes = source.RowBytes;
                int dstRowBytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    stream.Write(pixels.Slice(y * srcRowBytes, dstRowBytes));
                }
                GC.KeepAlive(source);
            }
            finally
            {
                converted?.Dispose();
            }
        }
    }
}
