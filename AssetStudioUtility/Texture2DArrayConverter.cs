using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio
{
    public sealed class Texture2DArrayConverter
    {
        private readonly Texture2DArray _array;

        public Texture2DArrayConverter(Texture2DArray texture2DArray)
        {
            _array = texture2DArray ?? throw new ArgumentNullException(nameof(texture2DArray));
        }

        public int SliceCount => Math.Max(0, _array.m_Depth);

        public SKBitmap? ConvertSliceToImage(int sliceIndex, bool flip = true)
        {
            if (!TryGetLayout(out long totalBytes, out long sliceStride, out long mip0Bytes, out long outputSize))
            {
                return null;
            }

            if (sliceIndex < 0 || sliceIndex >= _array.m_Depth)
            {
                return null;
            }

            try
            {
                using var session = _array.image_data.OpenReadSession();
                return DecodeSingleSlice(session, sliceIndex, totalBytes, sliceStride, mip0Bytes, outputSize, flip);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Logger.Warning($"Failed to decode Texture2DArray '{_array.m_Name}' slice {sliceIndex}: {ex.Message}");
                return null;
            }
        }

        public List<SKBitmap> ConvertAllSlicesToImages(bool flip = true)
        {
            var results = new List<SKBitmap>();
            if (!TryGetLayout(out long totalBytes, out long sliceStride, out long mip0Bytes, out long outputSize))
            {
                return results;
            }

            try
            {
                using var session = _array.image_data.OpenReadSession();
                results.EnsureCapacity(_array.m_Depth);
                for (int slice = 0; slice < _array.m_Depth; slice++)
                {
                    var bitmap = DecodeSingleSlice(session, slice, totalBytes, sliceStride, mip0Bytes, outputSize, flip);
                    if (bitmap != null)
                    {
                        results.Add(bitmap);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Logger.Warning($"Failed to decode Texture2DArray '{_array.m_Name}': {ex.Message}");
            }

            return results;
        }

        private bool TryGetLayout(out long totalBytes, out long sliceStride, out long mip0Bytes, out long outputSize)
        {
            totalBytes = 0;
            sliceStride = 0;
            mip0Bytes = 0;
            outputSize = 0;

            if (_array.IsTruncated || !_array.IsFormatSupported)
            {
                return false;
            }

            if (_array.m_Width <= 0 || _array.m_Height <= 0 || _array.m_Depth <= 0)
            {
                return false;
            }

            if (!Texture2DConverter.IsSupportedFormat(_array.m_TextureFormat))
            {
                return false;
            }

            outputSize = (long)_array.m_Width * _array.m_Height * 4L;
            if (outputSize <= 0 || outputSize > int.MaxValue)
            {
                return false;
            }

            totalBytes = _array.image_data?.Size ?? 0;
            if (totalBytes <= 0)
            {
                return false;
            }

            mip0Bytes = Texture2DConverter.GetMinimumDataSize(_array.m_TextureFormat, _array.m_Width, _array.m_Height);
            if (mip0Bytes > int.MaxValue)
            {
                return false;
            }

            long mipChainBytes = ComputeMipChainSize(_array.m_TextureFormat, _array.m_Width, _array.m_Height, Math.Max(1, _array.m_MipCount));
            if (_array.m_DataSize > 0 && _array.m_DataSize % (uint)_array.m_Depth == 0)
            {
                long declaredSlice = _array.m_DataSize / (uint)_array.m_Depth;
                if (declaredSlice > 0 && (mip0Bytes <= 0 || declaredSlice >= mip0Bytes))
                {
                    sliceStride = declaredSlice;
                }
            }

            if (sliceStride <= 0 && mipChainBytes > 0 && totalBytes >= mipChainBytes && totalBytes % _array.m_Depth == 0)
            {
                long candidate = totalBytes / _array.m_Depth;
                if (mip0Bytes <= 0 || candidate >= mip0Bytes)
                {
                    sliceStride = candidate;
                }
            }

            if (sliceStride <= 0 && mipChainBytes > 0)
            {
                sliceStride = mipChainBytes;
            }

            if (sliceStride <= 0 && totalBytes >= _array.m_Depth)
            {
                sliceStride = totalBytes / _array.m_Depth;
            }

            if (sliceStride <= 0 || (mip0Bytes > 0 && sliceStride < mip0Bytes))
            {
                return false;
            }

            // Require at least slice 0 to fit in available bytes so truncated single-slice data returns cleanly.
            if (mip0Bytes > 0 && totalBytes < mip0Bytes)
            {
                return false;
            }

            return true;
        }

        private SKBitmap? DecodeSingleSlice(
            ResourceReader.ReadSession session,
            int sliceIndex,
            long totalBytes,
            long sliceStride,
            long mip0Bytes,
            long outputSize,
            bool flip)
        {
            long sliceOffset = (long)sliceIndex * sliceStride;
            long requiredBytes = mip0Bytes > 0 ? mip0Bytes : sliceStride;
            if (sliceOffset < 0 || requiredBytes <= 0 || sliceOffset > totalBytes - requiredBytes)
            {
                return null;
            }

            long bytesToReadLong = mip0Bytes > 0
                ? mip0Bytes
                : Math.Min(sliceStride, totalBytes - sliceOffset);
            if (bytesToReadLong <= 0 || bytesToReadLong > int.MaxValue)
            {
                return null;
            }

            int bytesToRead = (int)bytesToReadLong;
            var sliceBytes = new byte[bytesToRead];
            session.ReadSegment(sliceOffset, sliceBytes, 0, bytesToRead);

            var sliceReader = new ResourceReader(sliceBytes);
            var converter = new Texture2DConverter(
                sliceReader,
                _array.m_Width,
                _array.m_Height,
                _array.m_TextureFormat,
                _array.version,
                _array.platform);

            if (!converter.CanDecode())
            {
                return null;
            }

            var info = new SKImageInfo(_array.m_Width, _array.m_Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            try
            {
                var dstSpan = bitmap.GetWritablePixelSpan();
                int expectedRowBytes = checked(_array.m_Width * 4);
                if (bitmap.RowBytes == expectedRowBytes && dstSpan.Length >= (int)outputSize)
                {
                    var pixelSpan = dstSpan.Slice(0, (int)outputSize);
                    if (!converter.DecodeTexture2D(pixelSpan))
                    {
                        bitmap.Dispose();
                        return null;
                    }
                    if (flip)
                    {
                        bitmap.FlipVerticalInPlace();
                    }
                    GC.KeepAlive(bitmap);
                    return bitmap;
                }

                bitmap.Dispose();
                var pixels = new byte[(int)outputSize];
                if (!converter.DecodeTexture2D(pixels))
                {
                    return null;
                }
                if (flip)
                {
                    int rowBytes = checked(_array.m_Width * 4);
                    ImageExtensions.FlipVerticalInPlace(pixels, rowBytes, rowBytes, _array.m_Height);
                }
                return ImageExtensions.WrapBgraUnpremulBitmap(pixels, _array.m_Width, _array.m_Height);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static long ComputeMipChainSize(TextureFormat format, int width, int height, int mipCount)
        {
            long mip0 = Texture2DConverter.GetMinimumDataSize(format, width, height);
            if (mip0 <= 0)
            {
                return 0;
            }

            long total = 0;
            int w = width;
            int h = height;
            for (int mip = 0; mip < mipCount; mip++)
            {
                long levelSize = Texture2DConverter.GetMinimumDataSize(format, w, h);
                if (levelSize <= 0)
                {
                    return 0;
                }
                total += levelSize;
                if (w == 1 && h == 1)
                {
                    break;
                }
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
            }
            return total;
        }
    }

    public static class Texture2DArrayExtensions
    {
        public static SKBitmap? ConvertSliceToImage(this Texture2DArray texture2DArray, int sliceIndex, bool flip = true)
        {
            return new Texture2DArrayConverter(texture2DArray).ConvertSliceToImage(sliceIndex, flip);
        }

        public static List<SKBitmap> ConvertAllSlicesToImages(this Texture2DArray texture2DArray, bool flip = true)
        {
            return new Texture2DArrayConverter(texture2DArray).ConvertAllSlicesToImages(flip);
        }

        public static MemoryStream? ConvertSliceToStream(this Texture2DArray texture2DArray, int sliceIndex, ImageFormat imageFormat, bool flip = true)
        {
            using var image = texture2DArray.ConvertSliceToImage(sliceIndex, flip);
            return image?.ConvertToStream(imageFormat);
        }
    }
}
