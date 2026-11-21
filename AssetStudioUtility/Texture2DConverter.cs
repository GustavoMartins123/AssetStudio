using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Texture2DDecoder;

namespace AssetStudio
{
    public class Texture2DConverter
    {
        private readonly struct PixelSwizzleMasks
        {
            public readonly Vector128<byte> Mask128;
            public readonly Vector256<byte> Lane256;
            public readonly Vector256<byte> Global256;
            public readonly Vector512<byte> Lane512;
            public readonly Vector512<byte> Global512;
            public readonly byte P0;
            public readonly byte P1;
            public readonly byte P2;
            public readonly byte P3;

            private PixelSwizzleMasks(byte p0, byte p1, byte p2, byte p3)
            {
                P0 = p0;
                P1 = p1;
                P2 = p2;
                P3 = p3;

                Mask128 = Vector128.Create(
                    p0, p1, p2, p3,
                    (byte)(4 + p0), (byte)(4 + p1), (byte)(4 + p2), (byte)(4 + p3),
                    (byte)(8 + p0), (byte)(8 + p1), (byte)(8 + p2), (byte)(8 + p3),
                    (byte)(12 + p0), (byte)(12 + p1), (byte)(12 + p2), (byte)(12 + p3));
                Lane256 = Vector256.Create(Mask128, Mask128);
                Global256 = Vector256.Create(Mask128, Mask128 + Vector128.Create((byte)16));
                Lane512 = Vector512.Create(Lane256, Lane256);
                Global512 = Vector512.Create(Global256, Global256 + Vector256.Create((byte)32));
            }

            public static PixelSwizzleMasks Create(byte p0, byte p1, byte p2, byte p3) => new(p0, p1, p2, p3);
        }

        private static readonly PixelSwizzleMasks s_rgbaBgraMasks = PixelSwizzleMasks.Create(2, 1, 0, 3);
        private static readonly PixelSwizzleMasks s_argbToBgraMasks = PixelSwizzleMasks.Create(3, 2, 1, 0);
        private static readonly PixelSwizzleMasks s_abgrToBgraMasks = PixelSwizzleMasks.Create(1, 2, 3, 0);
        private static readonly PixelSwizzleMasks s_bgraToAbgrMasks = PixelSwizzleMasks.Create(3, 0, 1, 2);

        private ResourceReader reader;
        private int m_Width;
        private int m_Height;
        private TextureFormat m_TextureFormat;
        private int[] version;
        private BuildTarget platform;
        private int outPutSize;

        public Texture2DConverter(Texture2D m_Texture2D)
        {
            reader = m_Texture2D.image_data;
            m_Width = m_Texture2D.m_Width;
            m_Height = m_Texture2D.m_Height;
            m_TextureFormat = m_Texture2D.m_TextureFormat;
            version = m_Texture2D.version;
            platform = m_Texture2D.platform;
            long expectedOutput = (m_Width > 0 && m_Height > 0) ? (long)m_Width * m_Height * 4 : 0;
            outPutSize = expectedOutput <= int.MaxValue ? (int)expectedOutput : 0;
        }

        public bool SupportsDirectSpanDecode => true;

        internal bool CanDecode()
        {
            if (reader.Size <= 0 || m_Width <= 0 || m_Height <= 0 || !IsSupportedFormat(m_TextureFormat))
            {
                return false;
            }

            long expectedOutputSize = (long)m_Width * m_Height * 4;
            long minDataSize = GetMinimumDataSize(m_TextureFormat, m_Width, m_Height);
            if (minDataSize > int.MaxValue || expectedOutputSize <= 0 || expectedOutputSize > int.MaxValue)
            {
                return false;
            }

            if (minDataSize > 0 && reader.Size < minDataSize)
            {
                return (double)reader.Size / minDataSize >= 0.1;
            }

            return true;
        }

        public bool DecodeTexture2D(byte[] bytes)
        {
            if (bytes == null)
            {
                return false;
            }

            return DecodeTexture2DCore(bytes.AsSpan(), bytes);
        }

        public bool DecodeTexture2D(Span<byte> bytes)
        {
            return DecodeTexture2DCore(bytes, backingArray: null);
        }

        private bool ShouldUseStreamingDecode(long expectedOutputSize, long minDataSize)
        {
            return expectedOutputSize >= 16 * 1024 * 1024
                && minDataSize > 0
                && reader.Size >= minDataSize
                && GetFormatParams(m_TextureFormat, out _);
        }

        private bool DecodeTexture2DCore(Span<byte> bytes, byte[]? backingArray)
        {
            if (reader.Size <= 0 || m_Width <= 0 || m_Height <= 0)
            {
                return false;
            }

            var minDataSize = GetMinimumDataSize(m_TextureFormat, m_Width, m_Height);
            long expectedOutputSize = (long)m_Width * m_Height * 4;
            if (minDataSize > int.MaxValue || expectedOutputSize <= 0 || expectedOutputSize > bytes.Length)
            {
                return false;
            }

            if (ShouldUseStreamingDecode(expectedOutputSize, minDataSize))
            {
                try
                {
                    if (DecodeTexture2DStreamingCore(bytes, backingArray))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Streaming texture decode failed, falling back to standard: {ex.Message}");
                }
            }

            var truncated = minDataSize > 0 && reader.Size < minDataSize;
            if (truncated)
            {
                var ratio = (double)reader.Size / minDataSize;
                if (ratio < 0.1)
                {
                    Logger.Warning($"Texture data essentially absent for {m_TextureFormat} {m_Width}x{m_Height}: expected {minDataSize} bytes, got {reader.Size}. Skipping.");
                    return false;
                }
                Logger.Warning($"Texture data truncated for {m_TextureFormat} {m_Width}x{m_Height}: expected {minDataSize} bytes, got {reader.Size}. Padding with zeros.");
            }
            var flag = false;
            var allocSize = truncated ? (int)minDataSize : reader.Size;
            byte[] buff;
            try
            {
                buff = BigArrayPool<byte>.Shared.Rent(allocSize);
                if (truncated)
                {
                    Array.Clear(buff, 0, allocSize);
                }
            }
            catch (OutOfMemoryException)
            {
                Logger.Warning($"Out of memory allocating {allocSize} bytes for texture decode");
                return false;
            }
            try
            {
                try
                {
                    reader.GetData(buff);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to read texture data: {ex.Message}");
                    return false;
                }

                if (m_TextureFormat == TextureFormat.ARGB4444
                    || m_TextureFormat == TextureFormat.RGB565
                    || m_TextureFormat == TextureFormat.DXT1
                    || m_TextureFormat == TextureFormat.DXT3
                    || m_TextureFormat == TextureFormat.DXT5)
                {
                    SwapBytesForXbox(buff);
                }

                ReadOnlySpan<byte> inputSpan = buff.AsSpan(0, allocSize);
                Span<byte> outputSpan = bytes.Slice(0, (int)expectedOutputSize);
                if (IsNativeDecodedFormat(m_TextureFormat))
                {
                    flag = DecodeNativeFormat(m_TextureFormat, inputSpan, outputSpan);
                }
                else
                {
                    flag = DecodeManagedFormat(m_TextureFormat, inputSpan, outputSpan);
                }
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(buff);
            }
            return flag;
        }

        private static bool IsSupportedFormat(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.Alpha8 or
                TextureFormat.ARGB4444 or
                TextureFormat.RGB24 or
                TextureFormat.RGBA32 or
                TextureFormat.ARGB32 or
                TextureFormat.ARGBFloat or
                TextureFormat.RGB565 or
                TextureFormat.BGR24 or
                TextureFormat.R16 or
                TextureFormat.DXT1 or
                TextureFormat.DXT3 or
                TextureFormat.DXT5 or
                TextureFormat.RGBA4444 or
                TextureFormat.BGRA32 or
                TextureFormat.RHalf or
                TextureFormat.RGHalf or
                TextureFormat.RGBAHalf or
                TextureFormat.RFloat or
                TextureFormat.RGFloat or
                TextureFormat.RGBAFloat or
                TextureFormat.RGBFloat or
                TextureFormat.YUY2 or
                TextureFormat.RGB9e5Float or
                TextureFormat.BC6H or
                TextureFormat.BC7 or
                TextureFormat.BC4 or
                TextureFormat.BC5 or
                TextureFormat.DXT1Crunched or
                TextureFormat.DXT5Crunched or
                TextureFormat.PVRTC_RGB2 or
                TextureFormat.PVRTC_RGBA2 or
                TextureFormat.PVRTC_RGB4 or
                TextureFormat.PVRTC_RGBA4 or
                TextureFormat.ETC_RGB4 or
                TextureFormat.ETC_RGB4_3DS or
                TextureFormat.ATC_RGB4 or
                TextureFormat.ATC_RGBA8 or
                TextureFormat.EAC_R or
                TextureFormat.EAC_R_SIGNED or
                TextureFormat.EAC_RG or
                TextureFormat.EAC_RG_SIGNED or
                TextureFormat.ETC2_RGB or
                TextureFormat.ETC2_RGBA1 or
                TextureFormat.ETC2_RGBA8 or
                TextureFormat.ETC_RGBA8_3DS or
                TextureFormat.ASTC_RGB_4x4 or
                TextureFormat.ASTC_RGBA_4x4 or
                TextureFormat.ASTC_HDR_4x4 or
                TextureFormat.ASTC_RGB_5x5 or
                TextureFormat.ASTC_RGBA_5x5 or
                TextureFormat.ASTC_HDR_5x5 or
                TextureFormat.ASTC_RGB_6x6 or
                TextureFormat.ASTC_RGBA_6x6 or
                TextureFormat.ASTC_HDR_6x6 or
                TextureFormat.ASTC_RGB_8x8 or
                TextureFormat.ASTC_RGBA_8x8 or
                TextureFormat.ASTC_HDR_8x8 or
                TextureFormat.ASTC_RGB_10x10 or
                TextureFormat.ASTC_RGBA_10x10 or
                TextureFormat.ASTC_HDR_10x10 or
                TextureFormat.ASTC_RGB_12x12 or
                TextureFormat.ASTC_RGBA_12x12 or
                TextureFormat.ASTC_HDR_12x12 or
                TextureFormat.RG16 or
                TextureFormat.R8 or
                TextureFormat.ETC_RGB4Crunched or
                TextureFormat.ETC2_RGBA8Crunched or
                TextureFormat.RG32 or
                TextureFormat.RGB48 or
                TextureFormat.RGBA64 => true,
                _ => false
            };
        }

        private static bool IsNativeDecodedFormat(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.DXT1 or
                TextureFormat.DXT3 or
                TextureFormat.DXT5 or
                TextureFormat.BC6H or
                TextureFormat.BC7 or
                TextureFormat.BC4 or
                TextureFormat.BC5 or
                TextureFormat.DXT1Crunched or
                TextureFormat.DXT5Crunched or
                TextureFormat.PVRTC_RGB2 or
                TextureFormat.PVRTC_RGBA2 or
                TextureFormat.PVRTC_RGB4 or
                TextureFormat.PVRTC_RGBA4 or
                TextureFormat.ETC_RGB4 or
                TextureFormat.ETC_RGB4_3DS or
                TextureFormat.ATC_RGB4 or
                TextureFormat.ATC_RGBA8 or
                TextureFormat.EAC_R or
                TextureFormat.EAC_R_SIGNED or
                TextureFormat.EAC_RG or
                TextureFormat.EAC_RG_SIGNED or
                TextureFormat.ETC2_RGB or
                TextureFormat.ETC2_RGBA1 or
                TextureFormat.ETC2_RGBA8 or
                TextureFormat.ETC_RGBA8_3DS or
                TextureFormat.ASTC_RGB_4x4 or
                TextureFormat.ASTC_RGBA_4x4 or
                TextureFormat.ASTC_HDR_4x4 or
                TextureFormat.ASTC_RGB_5x5 or
                TextureFormat.ASTC_RGBA_5x5 or
                TextureFormat.ASTC_HDR_5x5 or
                TextureFormat.ASTC_RGB_6x6 or
                TextureFormat.ASTC_RGBA_6x6 or
                TextureFormat.ASTC_HDR_6x6 or
                TextureFormat.ASTC_RGB_8x8 or
                TextureFormat.ASTC_RGBA_8x8 or
                TextureFormat.ASTC_HDR_8x8 or
                TextureFormat.ASTC_RGB_10x10 or
                TextureFormat.ASTC_RGBA_10x10 or
                TextureFormat.ASTC_HDR_10x10 or
                TextureFormat.ASTC_RGB_12x12 or
                TextureFormat.ASTC_RGBA_12x12 or
                TextureFormat.ASTC_HDR_12x12 or
                TextureFormat.ETC_RGB4Crunched or
                TextureFormat.ETC2_RGBA8Crunched => true,
                _ => false
            };
        }

        private bool DecodeNativeFormat(TextureFormat format, ReadOnlySpan<byte> buff, Span<byte> bytes)
        {
            switch (format)
            {
                case TextureFormat.DXT1: //test pass
                    return DecodeDXT1(buff, bytes);
                case TextureFormat.DXT3:
                    return DecodeDXT3(buff, bytes);
                case TextureFormat.DXT5: //test pass
                    return DecodeDXT5(buff, bytes);
                case TextureFormat.BC6H: //test pass
                    return DecodeBC6H(buff, bytes);
                case TextureFormat.BC7: //test pass
                    return DecodeBC7(buff, bytes);
                case TextureFormat.BC4: //test pass
                    return DecodeBC4(buff, bytes);
                case TextureFormat.BC5: //test pass
                    return DecodeBC5(buff, bytes);
                case TextureFormat.DXT1Crunched: //test pass
                    return DecodeDXT1Crunched(buff, bytes);
                case TextureFormat.DXT5Crunched: //test pass
                    return DecodeDXT5Crunched(buff, bytes);
                case TextureFormat.PVRTC_RGB2: //test pass
                case TextureFormat.PVRTC_RGBA2: //test pass
                    return DecodePVRTC(buff, bytes, true);
                case TextureFormat.PVRTC_RGB4: //test pass
                case TextureFormat.PVRTC_RGBA4: //test pass
                    return DecodePVRTC(buff, bytes, false);
                case TextureFormat.ETC_RGB4: //test pass
                case TextureFormat.ETC_RGB4_3DS:
                    return DecodeETC1(buff, bytes);
                case TextureFormat.ATC_RGB4: //test pass
                    return DecodeATCRGB4(buff, bytes);
                case TextureFormat.ATC_RGBA8: //test pass
                    return DecodeATCRGBA8(buff, bytes);
                case TextureFormat.EAC_R: //test pass
                    return DecodeEACR(buff, bytes);
                case TextureFormat.EAC_R_SIGNED:
                    return DecodeEACRSigned(buff, bytes);
                case TextureFormat.EAC_RG: //test pass
                    return DecodeEACRG(buff, bytes);
                case TextureFormat.EAC_RG_SIGNED:
                    return DecodeEACRGSigned(buff, bytes);
                case TextureFormat.ETC2_RGB: //test pass
                    return DecodeETC2(buff, bytes);
                case TextureFormat.ETC2_RGBA1: //test pass
                    return DecodeETC2A1(buff, bytes);
                case TextureFormat.ETC2_RGBA8: //test pass
                case TextureFormat.ETC_RGBA8_3DS:
                    return DecodeETC2A8(buff, bytes);
                case TextureFormat.ASTC_RGB_4x4: //test pass
                case TextureFormat.ASTC_RGBA_4x4: //test pass
                case TextureFormat.ASTC_HDR_4x4: //test pass
                    return DecodeASTC(buff, bytes, 4);
                case TextureFormat.ASTC_RGB_5x5: //test pass
                case TextureFormat.ASTC_RGBA_5x5: //test pass
                case TextureFormat.ASTC_HDR_5x5: //test pass
                    return DecodeASTC(buff, bytes, 5);
                case TextureFormat.ASTC_RGB_6x6: //test pass
                case TextureFormat.ASTC_RGBA_6x6: //test pass
                case TextureFormat.ASTC_HDR_6x6: //test pass
                    return DecodeASTC(buff, bytes, 6);
                case TextureFormat.ASTC_RGB_8x8: //test pass
                case TextureFormat.ASTC_RGBA_8x8: //test pass
                case TextureFormat.ASTC_HDR_8x8: //test pass
                    return DecodeASTC(buff, bytes, 8);
                case TextureFormat.ASTC_RGB_10x10: //test pass
                case TextureFormat.ASTC_RGBA_10x10: //test pass
                case TextureFormat.ASTC_HDR_10x10: //test pass
                    return DecodeASTC(buff, bytes, 10);
                case TextureFormat.ASTC_RGB_12x12: //test pass
                case TextureFormat.ASTC_RGBA_12x12: //test pass
                case TextureFormat.ASTC_HDR_12x12: //test pass
                    return DecodeASTC(buff, bytes, 12);
                case TextureFormat.ETC_RGB4Crunched: //test pass
                    return DecodeETC1Crunched(buff, bytes);
                case TextureFormat.ETC2_RGBA8Crunched: //test pass
                    return DecodeETC2A8Crunched(buff, bytes);
                default:
                    return false;
            }
        }

        private bool DecodeManagedFormat(TextureFormat format, ReadOnlySpan<byte> buff, Span<byte> bytes)
        {
            switch (format)
            {
                case TextureFormat.Alpha8: //test pass
                    return DecodeAlpha8(buff, bytes);
                case TextureFormat.ARGB4444: //test pass
                    return DecodeARGB4444(buff, bytes);
                case TextureFormat.RGB24: //test pass
                    return DecodeRGB24(buff, bytes);
                case TextureFormat.RGBA32: //test pass
                    return DecodeRGBA32(buff, bytes);
                case TextureFormat.ARGB32: //test pass
                    return DecodeARGB32(buff, bytes);
                case TextureFormat.ARGBFloat:
                    return DecodeARGBFloat(buff, bytes);
                case TextureFormat.RGB565: //test pass
                    return DecodeRGB565(buff, bytes);
                case TextureFormat.BGR24:
                    return DecodeBGR24(buff, bytes);
                case TextureFormat.R16: //test pass
                    return DecodeR16(buff, bytes);
                case TextureFormat.RGBA4444: //test pass
                    return DecodeRGBA4444(buff, bytes);
                case TextureFormat.BGRA32: //test pass
                    return DecodeBGRA32(buff, bytes);
                case TextureFormat.RHalf:
                    return DecodeRHalf(buff, bytes);
                case TextureFormat.RGHalf:
                    return DecodeRGHalf(buff, bytes);
                case TextureFormat.RGBAHalf: //test pass
                    return DecodeRGBAHalf(buff, bytes);
                case TextureFormat.RFloat:
                    return DecodeRFloat(buff, bytes);
                case TextureFormat.RGFloat:
                    return DecodeRGFloat(buff, bytes);
                case TextureFormat.RGBAFloat:
                    return DecodeRGBAFloat(buff, bytes);
                case TextureFormat.RGBFloat:
                    return DecodeRGBFloat(buff, bytes);
                case TextureFormat.YUY2: //test pass
                    return DecodeYUY2(buff, bytes);
                case TextureFormat.RGB9e5Float: //test pass
                    return DecodeRGB9e5Float(buff, bytes);
                case TextureFormat.RG16: //test pass
                    return DecodeRG16(buff, bytes);
                case TextureFormat.R8: //test pass
                    return DecodeR8(buff, bytes);
                case TextureFormat.RG32: //test pass
                    return DecodeRG32(buff, bytes);
                case TextureFormat.RGB48: //test pass
                    return DecodeRGB48(buff, bytes);
                case TextureFormat.RGBA64: //test pass
                    return DecodeRGBA64(buff, bytes);
                default:
                    return false;
            }
        }

        private void SwapBytesForXbox(byte[] image_data)
        {
            if (platform == BuildTarget.XBOX360)
            {
                var byteCount = Math.Min(reader.Size, image_data.Length) & ~1;
                var words = MemoryMarshal.Cast<byte, ushort>(image_data.AsSpan(0, byteCount));
                for (var i = 0; i < words.Length; i++)
                {
                    words[i] = BinaryPrimitives.ReverseEndianness(words[i]);
                }
            }
        }

        /// <summary>
        /// Swizzles 32-bit pixels between RGBA and BGRA byte order using SIMD instructions.
        /// Supports both separate and identical (in-place) source and destination buffers.
        /// </summary>
        public static void SwizzleRgbaBgra(ReadOnlySpan<byte> source, Span<byte> destination)
            => Swizzle4BytePixels(source, destination, in s_rgbaBgraMasks);

        /// <summary>
        /// Swizzles 32-bit pixels from ARGB to BGRA byte order using SIMD instructions.
        /// </summary>
        public static void SwizzleArgbToBgra(ReadOnlySpan<byte> source, Span<byte> destination)
            => Swizzle4BytePixels(source, destination, in s_argbToBgraMasks);

        /// <summary>
        /// Swizzles 32-bit pixels from ABGR to BGRA byte order using SIMD instructions.
        /// </summary>
        public static void SwizzleAbgrToBgra(ReadOnlySpan<byte> source, Span<byte> destination)
            => Swizzle4BytePixels(source, destination, in s_abgrToBgraMasks);

        /// <summary>
        /// Swizzles 32-bit pixels from BGRA to ABGR byte order (or RGBA to ARGB) using SIMD instructions.
        /// </summary>
        public static void SwizzleBgraToAbgr(ReadOnlySpan<byte> source, Span<byte> destination)
            => Swizzle4BytePixels(source, destination, in s_bgraToAbgrMasks);

        private static void Swizzle4BytePixels(ReadOnlySpan<byte> source, Span<byte> destination, in PixelSwizzleMasks masks)
        {
            int byteCount = source.Length & ~3;
            if (byteCount == 0)
            {
                return;
            }

            if (destination.Length < byteCount)
            {
                throw new ArgumentException($"Destination span length ({destination.Length}) is smaller than required ({byteCount}).", nameof(destination));
            }

            ref byte srcRef = ref MemoryMarshal.GetReference(source);
            ref byte dstRef = ref MemoryMarshal.GetReference(destination);
            if (!Unsafe.AreSame(ref srcRef, ref dstRef) && source.Slice(0, byteCount).Overlaps(destination.Slice(0, byteCount)))
            {
                byte[] temp = BigArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    Span<byte> tempSpan = temp.AsSpan(0, byteCount);
                    Swizzle4BytePixelsCore(ref srcRef, ref MemoryMarshal.GetReference(tempSpan), (nuint)byteCount, in masks);
                    tempSpan.CopyTo(destination);
                    return;
                }
                finally
                {
                    BigArrayPool<byte>.Shared.Return(temp);
                }
            }

            Swizzle4BytePixelsCore(ref srcRef, ref dstRef, (nuint)byteCount, in masks);
        }

        private static void Swizzle4BytePixelsCore(ref byte srcRef, ref byte dstRef, nuint count, in PixelSwizzleMasks masks)
        {
            nuint i = 0;

            if (Avx512BW.IsSupported && count >= 64)
            {
                Vector512<byte> mask = masks.Lane512;
                nuint limit = count - 64;
                for (; i <= limit; i += 64)
                {
                    var vec = Vector512.LoadUnsafe(ref srcRef, i);
                    Avx512BW.Shuffle(vec, mask).StoreUnsafe(ref dstRef, i);
                }
            }
            else if (Vector512.IsHardwareAccelerated && count >= 64)
            {
                Vector512<byte> mask = masks.Global512;
                nuint limit = count - 64;
                for (; i <= limit; i += 64)
                {
                    var vec = Vector512.LoadUnsafe(ref srcRef, i);
                    Vector512.Shuffle(vec, mask).StoreUnsafe(ref dstRef, i);
                }
            }

            if (Avx2.IsSupported && count - i >= 32)
            {
                Vector256<byte> mask = masks.Lane256;
                nuint limit = count - 32;
                for (; i <= limit; i += 32)
                {
                    var vec = Vector256.LoadUnsafe(ref srcRef, i);
                    Avx2.Shuffle(vec, mask).StoreUnsafe(ref dstRef, i);
                }
            }
            else if (Vector256.IsHardwareAccelerated && count - i >= 32)
            {
                Vector256<byte> mask = masks.Global256;
                nuint limit = count - 32;
                for (; i <= limit; i += 32)
                {
                    var vec = Vector256.LoadUnsafe(ref srcRef, i);
                    Vector256.Shuffle(vec, mask).StoreUnsafe(ref dstRef, i);
                }
            }

            if (Vector128.IsHardwareAccelerated && count - i >= 16)
            {
                Vector128<byte> mask = masks.Mask128;
                nuint limit = count - 16;
                for (; i <= limit; i += 16)
                {
                    var vec = Vector128.LoadUnsafe(ref srcRef, i);
                    Vector128.Shuffle(vec, mask).StoreUnsafe(ref dstRef, i);
                }
            }

            nuint p0 = masks.P0;
            nuint p1 = masks.P1;
            nuint p2 = masks.P2;
            nuint p3 = masks.P3;
            for (; i < count; i += 4)
            {
                byte b0 = Unsafe.Add(ref srcRef, i + p0);
                byte b1 = Unsafe.Add(ref srcRef, i + p1);
                byte b2 = Unsafe.Add(ref srcRef, i + p2);
                byte b3 = Unsafe.Add(ref srcRef, i + p3);
                Unsafe.Add(ref dstRef, i) = b0;
                Unsafe.Add(ref dstRef, i + 1) = b1;
                Unsafe.Add(ref dstRef, i + 2) = b2;
                Unsafe.Add(ref dstRef, i + 3) = b3;
            }
        }

        /// <summary>
        /// Decodes ARGB4444 pixels into BGRA32 output using zero-allocation SIMD / register bit operations.
        /// </summary>
        public static void DecodeArgb4444(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount)
            => Decode4444(source, destination, pixelCount, isRgba4444: false);

        /// <summary>
        /// Decodes RGBA4444 pixels into BGRA32 output using zero-allocation SIMD / register bit operations.
        /// </summary>
        public static void DecodeRgba4444(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount)
            => Decode4444(source, destination, pixelCount, isRgba4444: true);

        private static void Decode4444(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount, bool isRgba4444)
        {
            if (pixelCount <= 0)
            {
                return;
            }

            long requiredSrcBytes = (long)pixelCount * 2L;
            long requiredDstBytes = (long)pixelCount * 4L;
            if (source.Length < requiredSrcBytes)
            {
                throw new ArgumentException($"Source buffer length ({source.Length}) is smaller than required ({requiredSrcBytes}).", nameof(source));
            }
            if (destination.Length < requiredDstBytes)
            {
                throw new ArgumentException($"Destination buffer length ({destination.Length}) is smaller than required ({requiredDstBytes}).", nameof(destination));
            }

            int i = 0;
            if (BitConverter.IsLittleEndian)
            {
                ref ushort srcU16 = ref Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(source));
                ref uint dstU32 = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(destination));
                nuint idx = 0;
                nuint count = (nuint)pixelCount;

                if (Vector256.IsHardwareAccelerated && count >= 16)
                {
                    var mask000F = Vector256.Create(0x000Fu);
                    var mask00F0 = Vector256.Create(0x00F0u);
                    var mask0F00 = Vector256.Create(0x0F00u);
                    var maskF000 = Vector256.Create(0xF000u);
                    nuint limit = count - 16;
                    for (; idx <= limit; idx += 16)
                    {
                        var packed = Vector256.LoadUnsafe(ref srcU16, idx);
                        var (lo, hi) = Vector256.Widen(packed);
                        Expand4444Vector256(lo, isRgba4444, mask000F, mask00F0, mask0F00, maskF000).StoreUnsafe(ref dstU32, idx);
                        Expand4444Vector256(hi, isRgba4444, mask000F, mask00F0, mask0F00, maskF000).StoreUnsafe(ref dstU32, idx + 8);
                    }
                }

                if (Vector128.IsHardwareAccelerated && count - idx >= 8)
                {
                    var mask000F = Vector128.Create(0x000Fu);
                    var mask00F0 = Vector128.Create(0x00F0u);
                    var mask0F00 = Vector128.Create(0x0F00u);
                    var maskF000 = Vector128.Create(0xF000u);
                    nuint limit = count - 8;
                    for (; idx <= limit; idx += 8)
                    {
                        var packed = Vector128.LoadUnsafe(ref srcU16, idx);
                        var (lo, hi) = Vector128.Widen(packed);
                        Expand4444Vector128(lo, isRgba4444, mask000F, mask00F0, mask0F00, maskF000).StoreUnsafe(ref dstU32, idx);
                        Expand4444Vector128(hi, isRgba4444, mask000F, mask00F0, mask0F00, maskF000).StoreUnsafe(ref dstU32, idx + 4);
                    }
                }

                i = (int)idx;
            }

            for (; i < pixelCount; i++)
            {
                uint p = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(i * 2, 2));
                uint r = isRgba4444 ? ((p >> 4) | ((p & 0x000Fu) << 12)) : p;
                uint expanded = (r & 0x000Fu)
                              | ((r & 0x00F0u) << 4)
                              | ((r & 0x0F00u) << 8)
                              | ((r & 0xF000u) << 12);
                uint bgra = expanded | (expanded << 4);
                BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(i * 4, 4), bgra);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<uint> Expand4444Vector256(
            Vector256<uint> v,
            bool isRgba4444,
            Vector256<uint> mask000F,
            Vector256<uint> mask00F0,
            Vector256<uint> mask0F00,
            Vector256<uint> maskF000)
        {
            if (isRgba4444)
            {
                v = (v >> 4) | ((v & mask000F) << 12);
            }

            Vector256<uint> expanded = (v & mask000F)
                                     | ((v & mask00F0) << 4)
                                     | ((v & mask0F00) << 8)
                                     | ((v & maskF000) << 12);
            return expanded | (expanded << 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<uint> Expand4444Vector128(
            Vector128<uint> v,
            bool isRgba4444,
            Vector128<uint> mask000F,
            Vector128<uint> mask00F0,
            Vector128<uint> mask0F00,
            Vector128<uint> maskF000)
        {
            if (isRgba4444)
            {
                v = (v >> 4) | ((v & mask000F) << 12);
            }

            Vector128<uint> expanded = (v & mask000F)
                                     | ((v & mask00F0) << 4)
                                     | ((v & mask0F00) << 8)
                                     | ((v & maskF000) << 12);
            return expanded | (expanded << 4);
        }

        private bool DecodeAlpha8(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            buff.Fill(0xFF);
            for (var i = 0; i < size; i++)
            {
                buff[i * 4 + 3] = image_data[i];
            }
            return true;
        }

        private bool DecodeARGB4444(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            DecodeArgb4444(image_data, buff, m_Width * m_Height);
            return true;
        }

        private bool DecodeRGB24(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = image_data[i * 3 + 2];
                buff[i * 4 + 1] = image_data[i * 3 + 1];
                buff[i * 4 + 2] = image_data[i * 3 + 0];
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBA32(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            SwizzleRgbaBgra(image_data.Slice(0, outPutSize), buff.Slice(0, outPutSize));
            return true;
        }

        private bool DecodeARGB32(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            SwizzleArgbToBgra(image_data.Slice(0, outPutSize), buff.Slice(0, outPutSize));
            return true;
        }

        private bool DecodeARGBFloat(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4 + 12, 4)));
                buff[i + 1] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4 + 8, 4)));
                buff[i + 2] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4 + 4, 4)));
                buff[i + 3] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4, 4)));
            }
            return true;
        }

        private bool DecodeRGB565(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                var p = BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 2, 2));
                buff[i * 4] = (byte)((p << 3) | (p >> 2 & 7));
                buff[i * 4 + 1] = (byte)((p >> 3 & 0xfc) | (p >> 9 & 3));
                buff[i * 4 + 2] = (byte)((p >> 8 & 0xf8) | (p >> 13));
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeBGR24(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = image_data[i * 3];
                buff[i * 4 + 1] = image_data[i * 3 + 1];
                buff[i * 4 + 2] = image_data[i * 3 + 2];
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeR16(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //b
                buff[i * 4 + 1] = 0; //g
                buff[i * 4 + 2] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 2, 2))); //r
                buff[i * 4 + 3] = 255; //a
            }
            return true;
        }

        private bool DecodeDXT1(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeDXT1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT3(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeDXT3(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT5(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeDXT5(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeRGBA4444(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            DecodeRgba4444(image_data, buff, m_Width * m_Height);
            return true;
        }

        private bool DecodeBGRA32(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            image_data.Slice(0, outPutSize).CopyTo(buff);
            return true;
        }

        private bool DecodeRHalf(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = 0;
                buff[i + 2] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i / 2, 2)));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGHalf(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i + 2, 2)));
                buff[i + 2] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i, 2)));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBAHalf(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i * 2 + 4, 2)));
                buff[i + 1] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i * 2 + 2, 2)));
                buff[i + 2] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i * 2, 2)));
                buff[i + 3] = FloatToByte((float)BinaryPrimitives.ReadHalfLittleEndian(image_data.Slice(i * 2 + 6, 2)));
            }
            return true;
        }

        private bool DecodeRFloat(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = 0;
                buff[i + 2] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i, 4)));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGFloat(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 2 + 4, 4)));
                buff[i + 2] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 2, 4)));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBAFloat(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4 + 8, 4)));
                buff[i + 1] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4 + 4, 4)));
                buff[i + 2] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4, 4)));
                buff[i + 3] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(i * 4 + 12, 4)));
            }
            return true;
        }

        private bool DecodeRGBFloat(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                var input = i * 12;
                var output = i * 4;
                buff[output] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(input + 8, 4)));
                buff[output + 1] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(input + 4, 4)));
                buff[output + 2] = FloatToByte(BinaryPrimitives.ReadSingleLittleEndian(image_data.Slice(input, 4)));
                buff[output + 3] = 255;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ClampByte(int x)
        {
            return (byte)(byte.MaxValue < x ? byte.MaxValue : (x > byte.MinValue ? x : byte.MinValue));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte FloatToByte(float value)
        {
            if (float.IsNaN(value) || value <= 0f)
                return 0;
            if (value >= 1f)
                return 255;
            return (byte)Math.Round(value * 255f);
        }

        /// <summary>
        /// Heuristic detection for Unity DXT5nm tangent-space normal maps decoded into BGRA32.
        /// Unity packs X into Alpha, Y into Green, and fills Red with 255 (with Blue either 0, 255, or mirrored G).
        /// </summary>
        public static bool IsLikelyDxt5NormalMap(ReadOnlySpan<byte> bgra)
        {
            int pixelCount = bgra.Length / 4;
            if (pixelCount <= 0)
            {
                return false;
            }

            int step = Math.Max(1, pixelCount / 65536);
            int sampled = 0;
            int redNearMax = 0;
            int inUnitDisk = 0;
            int alphaInterior = 0;
            int whitePixels = 0;

            const float inv127_5 = 2f / 255f;
            for (int p = 0; p < pixelCount; p += step)
            {
                int o = p * 4;
                byte b = bgra[o];
                byte g = bgra[o + 1];
                byte r = bgra[o + 2];
                byte a = bgra[o + 3];
                sampled++;

                if (r >= 250)
                {
                    redNearMax++;
                    if (g >= 250 && b >= 250)
                    {
                        whitePixels++;
                    }
                }

                if (a >= 5 && a <= 250)
                {
                    alphaInterior++;
                }

                float nx = a * inv127_5 - 1f;
                float ny = g * inv127_5 - 1f;
                if (nx * nx + ny * ny <= 1.10f)
                {
                    inUnitDisk++;
                }
            }

            if (sampled == 0)
            {
                return false;
            }

            // Reject pure white UI/alpha masks where RGB == 255 everywhere
            if ((long)whitePixels * 100L >= (long)sampled * 95L)
            {
                return false;
            }

            return (long)redNearMax * 100L >= (long)sampled * 95L
                && (long)inUnitDisk * 100L >= (long)sampled * 90L
                && (long)alphaInterior * 100L >= (long)sampled * 15L;
        }

        /// <summary>
        /// Unpacks Unity DXT5nm or BC5 normal map data in-place (BGRA32 layout) into standard
        /// tangent-space RGB normal maps with reconstructed blue (Z) channel and opaque alpha (255).
        /// </summary>
        /// <returns><c>true</c> if the buffer was unpacked; <c>false</c> if left unchanged.</returns>
        public static bool UnpackNormalMapBgra32(Span<byte> bgra, TextureFormat format, bool forceUnpack = false)
        {
            int byteCount = bgra.Length & ~3;
            if (byteCount == 0)
            {
                return false;
            }

            const float inv127_5 = 2f / 255f;

            if (format is TextureFormat.DXT5 or TextureFormat.DXT5Crunched)
            {
                if (!forceUnpack && !IsLikelyDxt5NormalMap(bgra.Slice(0, byteCount)))
                {
                    return false;
                }

                for (int i = 0; i < byteCount; i += 4)
                {
                    byte g = bgra[i + 1];
                    byte a = bgra[i + 3];
                    float nx = a * inv127_5 - 1f;
                    float ny = g * inv127_5 - 1f;
                    float nzSq = 1f - nx * nx - ny * ny;
                    float nz = nzSq > 0f ? MathF.Sqrt(nzSq) : 0f;

                    bgra[i] = FloatToByte(nz * 0.5f + 0.5f); // B = reconstructed Z
                    bgra[i + 1] = g;                          // G = Y
                    bgra[i + 2] = a;                          // R = X (moved from A)
                    bgra[i + 3] = 255;                        // A = opaque
                }
                return true;
            }

            if (format == TextureFormat.BC5 && forceUnpack)
            {
                for (int i = 0; i < byteCount; i += 4)
                {
                    byte g = bgra[i + 1];
                    byte r = bgra[i + 2];
                    float nx = r * inv127_5 - 1f;
                    float ny = g * inv127_5 - 1f;
                    float nzSq = 1f - nx * nx - ny * ny;
                    float nz = nzSq > 0f ? MathF.Sqrt(nzSq) : 0f;

                    bgra[i] = FloatToByte(nz * 0.5f + 0.5f); // B = reconstructed Z
                    bgra[i + 3] = 255;                        // A = opaque
                }
                return true;
            }

            return false;
        }

        private bool DecodeYUY2(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            int p = 0;
            int o = 0;
            int halfWidth = m_Width / 2;
            for (int j = 0; j < m_Height; j++)
            {
                for (int i = 0; i < halfWidth; ++i)
                {
                    int y0 = image_data[p++];
                    int u0 = image_data[p++];
                    int y1 = image_data[p++];
                    int v0 = image_data[p++];
                    int c = y0 - 16;
                    int d = u0 - 128;
                    int e = v0 - 128;
                    buff[o++] = ClampByte((298 * c + 516 * d + 128) >> 8);            // b
                    buff[o++] = ClampByte((298 * c - 100 * d - 208 * e + 128) >> 8);  // g
                    buff[o++] = ClampByte((298 * c + 409 * e + 128) >> 8);            // r
                    buff[o++] = 255;
                    c = y1 - 16;
                    buff[o++] = ClampByte((298 * c + 516 * d + 128) >> 8);            // b
                    buff[o++] = ClampByte((298 * c - 100 * d - 208 * e + 128) >> 8);  // g
                    buff[o++] = ClampByte((298 * c + 409 * e + 128) >> 8);            // r
                    buff[o++] = 255;
                }
            }
            return true;
        }

        private bool DecodeRGB9e5Float(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                var n = BinaryPrimitives.ReadUInt32LittleEndian(image_data.Slice(i, 4));
                var scale = (int)((n >> 27) & 0x1fu);
                var scalef = MathF.ScaleB(1f, scale - 24);
                var b = ((n >> 18) & 0x1ffu) * scalef;
                var g = ((n >> 9) & 0x1ffu) * scalef;
                var r = (n & 0x1ffu) * scalef;
                buff[i] = FloatToByte(b);
                buff[i + 1] = FloatToByte(g);
                buff[i + 2] = FloatToByte(r);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeBC4(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeBC4(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC5(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeBC5(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC6H(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeBC6(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC7(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeBC7(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT1Crunched(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result.Length >= GetMinimumDataSize(TextureFormat.DXT1, m_Width, m_Height))
            {
                if (DecodeDXT1(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodeDXT5Crunched(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result.Length >= GetMinimumDataSize(TextureFormat.DXT5, m_Width, m_Height))
            {
                if (DecodeDXT5(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodePVRTC(ReadOnlySpan<byte> image_data, Span<byte> buff, bool is2bpp)
        {
            return TextureDecoder.DecodePVRTC(image_data, m_Width, m_Height, buff, is2bpp);
        }

        private bool DecodeETC1(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeETC1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeATCRGB4(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeATCRGB4(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeATCRGBA8(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeATCRGBA8(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACR(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeEACR(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRSigned(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeEACRSigned(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRG(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeEACRG(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRGSigned(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeEACRGSigned(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeETC2(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2A1(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeETC2A1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2A8(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            return TextureDecoder.DecodeETC2A8(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeASTC(ReadOnlySpan<byte> image_data, Span<byte> buff, int blocksize)
        {
            return TextureDecoder.DecodeASTC(image_data, m_Width, m_Height, blocksize, blocksize, buff);
        }

        private bool DecodeRG16(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //B
                buff[i * 4 + 1] = image_data[i * 2 + 1];//G
                buff[i * 4 + 2] = image_data[i * 2];//R
                buff[i * 4 + 3] = 255;//A
            }
            return true;
        }

        private bool DecodeR8(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //B
                buff[i * 4 + 1] = 0; //G
                buff[i * 4 + 2] = image_data[i];//R
                buff[i * 4 + 3] = 255;//A
            }
            return true;
        }

        private bool DecodeETC1Crunched(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result.Length >= GetMinimumDataSize(TextureFormat.ETC_RGB4, m_Width, m_Height))
            {
                if (DecodeETC1(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodeETC2A8Crunched(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result.Length >= GetMinimumDataSize(TextureFormat.ETC2_RGBA8, m_Width, m_Height))
            {
                if (DecodeETC2A8(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DownScaleFrom16BitTo8Bit(ushort component)
        {
            return (byte)(((component * 255) + 32895) >> 16);
        }

        private bool DecodeRG32(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;                                                                                              //b
                buff[i + 1] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i + 2, 2))); //g
                buff[i + 2] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i, 2)));     //r
                buff[i + 3] = byte.MaxValue;                                                                              //a
            }
            return true;
        }

        private bool DecodeRGB48(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 6 + 4, 2)));     //b
                buff[i * 4 + 1] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 6 + 2, 2))); //g
                buff[i * 4 + 2] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 6, 2)));     //r
                buff[i * 4 + 3] = byte.MaxValue;                                                                                     //a
            }
            return true;
        }

        private bool DecodeRGBA64(ReadOnlySpan<byte> image_data, Span<byte> buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 2 + 4, 2)));     //b
                buff[i + 1] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 2 + 2, 2))); //g
                buff[i + 2] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 2, 2)));     //r
                buff[i + 3] = DownScaleFrom16BitTo8Bit(BinaryPrimitives.ReadUInt16LittleEndian(image_data.Slice(i * 2 + 6, 2))); //a
            }
            return true;
        }

        private bool UnpackCrunch(ReadOnlySpan<byte> image_data, [NotNullWhen(true)] out byte[]? result)
        {
            if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 3) //2017.3 and up
                || m_TextureFormat == TextureFormat.ETC_RGB4Crunched
                || m_TextureFormat == TextureFormat.ETC2_RGBA8Crunched)
            {
                result = TextureDecoder.UnpackUnityCrunch(image_data);
            }
            else
            {
                result = TextureDecoder.UnpackCrunch(image_data);
            }
            if (result != null)
            {
                return true;
            }
            return false;
        }
        private static long GetMinimumDataSize(TextureFormat format, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }
            long pixels = (long)width * height;
            long bw = ((long)width + 3) / 4;
            long bh = ((long)height + 3) / 4;
            long blocks = bw * bh;
            switch (format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return pixels;
                case TextureFormat.ARGB4444:
                case TextureFormat.RGBA4444:
                case TextureFormat.RGB565:
                case TextureFormat.R16:
                case TextureFormat.RG16:
                    return pixels * 2;
                case TextureFormat.RGB24:
                case TextureFormat.BGR24:
                    return pixels * 3;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                case TextureFormat.RGB9e5Float:
                case TextureFormat.RG32:
                    return pixels * 4;
                case TextureFormat.RGB48:
                    return pixels * 6;
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGBA64:
                    return pixels * 8;
                case TextureFormat.RHalf:
                    return pixels * 2;
                case TextureFormat.RGHalf:
                    return pixels * 4;
                case TextureFormat.RFloat:
                    return pixels * 4;
                case TextureFormat.RGFloat:
                    return pixels * 8;
                case TextureFormat.RGBFloat:
                    return pixels * 12;
                case TextureFormat.RGBAFloat:
                case TextureFormat.ARGBFloat:
                    return pixels * 16;
                case TextureFormat.YUY2:
                    return pixels * 2;
                case TextureFormat.DXT1:
                    return blocks * 8;
                case TextureFormat.DXT3:
                case TextureFormat.DXT5:
                    return blocks * 16;
                case TextureFormat.BC4:
                    return blocks * 8;
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                    return blocks * 16;
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC_RGB4_3DS:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA1:
                case TextureFormat.EAC_R:
                case TextureFormat.EAC_R_SIGNED:
                    return blocks * 8;
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC_RGBA8_3DS:
                case TextureFormat.EAC_RG:
                case TextureFormat.EAC_RG_SIGNED:
                    return blocks * 16;
                case TextureFormat.ASTC_RGB_4x4:
                case TextureFormat.ASTC_RGBA_4x4:
                case TextureFormat.ASTC_HDR_4x4:
                    return (((long)width + 3) / 4) * (((long)height + 3) / 4) * 16;
                case TextureFormat.ASTC_RGB_5x5:
                case TextureFormat.ASTC_RGBA_5x5:
                case TextureFormat.ASTC_HDR_5x5:
                    return (((long)width + 4) / 5) * (((long)height + 4) / 5) * 16;
                case TextureFormat.ASTC_RGB_6x6:
                case TextureFormat.ASTC_RGBA_6x6:
                case TextureFormat.ASTC_HDR_6x6:
                    return (((long)width + 5) / 6) * (((long)height + 5) / 6) * 16;
                case TextureFormat.ASTC_RGB_8x8:
                case TextureFormat.ASTC_RGBA_8x8:
                case TextureFormat.ASTC_HDR_8x8:
                    return (((long)width + 7) / 8) * (((long)height + 7) / 8) * 16;
                case TextureFormat.ASTC_RGB_10x10:
                case TextureFormat.ASTC_RGBA_10x10:
                case TextureFormat.ASTC_HDR_10x10:
                    return (((long)width + 9) / 10) * (((long)height + 9) / 10) * 16;
                case TextureFormat.ASTC_RGB_12x12:
                case TextureFormat.ASTC_RGBA_12x12:
                case TextureFormat.ASTC_HDR_12x12:
                    return (((long)width + 11) / 12) * (((long)height + 11) / 12) * 16;
                case TextureFormat.PVRTC_RGB2:
                case TextureFormat.PVRTC_RGBA2:
                    return (((long)width + 7) / 8) * (((long)height + 3) / 4) * 8;
                case TextureFormat.PVRTC_RGB4:
                case TextureFormat.PVRTC_RGBA4:
                    return blocks * 8;
                case TextureFormat.ATC_RGB4:
                    return blocks * 8;
                case TextureFormat.ATC_RGBA8:
                    return blocks * 16;
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.ETC_RGB4Crunched:
                case TextureFormat.ETC2_RGBA8Crunched:
                    return 0;
                default:
                    return 0;
            }
        }
        private struct FormatParam
        {
            public bool IsCompressed;
            public int BlockWidth;
            public int BlockHeight;
            public int BytesPerBlock; // or bytes per pixel if uncompressed
        }

        private bool GetFormatParams(TextureFormat format, out FormatParam param)
        {
            param = new FormatParam();
            switch (format)
            {
                // Uncompressed
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    param.IsCompressed = false;
                    param.BytesPerBlock = 4;
                    return true;
                case TextureFormat.RGB24:
                case TextureFormat.BGR24:
                    param.IsCompressed = false;
                    param.BytesPerBlock = 3;
                    return true;
                case TextureFormat.RG16:
                    param.IsCompressed = false;
                    param.BytesPerBlock = 2;
                    return true;
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    param.IsCompressed = false;
                    param.BytesPerBlock = 1;
                    return true;

                case TextureFormat.DXT1:
                case TextureFormat.BC4:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC_RGB4_3DS:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA1:
                case TextureFormat.EAC_R:
                case TextureFormat.EAC_R_SIGNED:
                case TextureFormat.ATC_RGB4:
                    param.IsCompressed = true;
                    param.BlockWidth = 4;
                    param.BlockHeight = 4;
                    param.BytesPerBlock = 8;
                    return true;

                case TextureFormat.DXT3:
                case TextureFormat.DXT5:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC_RGBA8_3DS:
                case TextureFormat.EAC_RG:
                case TextureFormat.EAC_RG_SIGNED:
                case TextureFormat.ATC_RGBA8:
                    param.IsCompressed = true;
                    param.BlockWidth = 4;
                    param.BlockHeight = 4;
                    param.BytesPerBlock = 16;
                    return true;

                case TextureFormat.ASTC_RGB_4x4:
                case TextureFormat.ASTC_RGBA_4x4:
                case TextureFormat.ASTC_HDR_4x4:
                    param.IsCompressed = true;
                    param.BlockWidth = 4;
                    param.BlockHeight = 4;
                    param.BytesPerBlock = 16;
                    return true;

                case TextureFormat.ASTC_RGB_5x5:
                case TextureFormat.ASTC_RGBA_5x5:
                case TextureFormat.ASTC_HDR_5x5:
                    param.IsCompressed = true;
                    param.BlockWidth = 5;
                    param.BlockHeight = 5;
                    param.BytesPerBlock = 16;
                    return true;

                case TextureFormat.ASTC_RGB_6x6:
                case TextureFormat.ASTC_RGBA_6x6:
                case TextureFormat.ASTC_HDR_6x6:
                    param.IsCompressed = true;
                    param.BlockWidth = 6;
                    param.BlockHeight = 6;
                    param.BytesPerBlock = 16;
                    return true;

                case TextureFormat.ASTC_RGB_8x8:
                case TextureFormat.ASTC_RGBA_8x8:
                case TextureFormat.ASTC_HDR_8x8:
                    param.IsCompressed = true;
                    param.BlockWidth = 8;
                    param.BlockHeight = 8;
                    param.BytesPerBlock = 16;
                    return true;

                case TextureFormat.ASTC_RGB_10x10:
                case TextureFormat.ASTC_RGBA_10x10:
                case TextureFormat.ASTC_HDR_10x10:
                    param.IsCompressed = true;
                    param.BlockWidth = 10;
                    param.BlockHeight = 10;
                    param.BytesPerBlock = 16;
                    return true;

                case TextureFormat.ASTC_RGB_12x12:
                case TextureFormat.ASTC_RGBA_12x12:
                case TextureFormat.ASTC_HDR_12x12:
                    param.IsCompressed = true;
                    param.BlockWidth = 12;
                    param.BlockHeight = 12;
                    param.BytesPerBlock = 16;
                    return true;
            }
            return false;
        }

        private bool DecodeBlockRow(TextureFormat format, ReadOnlySpan<byte> compressedRow, int width, int height, int blockWidth, int blockHeight, Span<byte> outputRow)
        {
            switch (format)
            {
                case TextureFormat.DXT1:
                    return TextureDecoder.DecodeDXT1(compressedRow, width, height, outputRow);
                case TextureFormat.DXT3:
                    return TextureDecoder.DecodeDXT3(compressedRow, width, height, outputRow);
                case TextureFormat.DXT5:
                    return TextureDecoder.DecodeDXT5(compressedRow, width, height, outputRow);
                case TextureFormat.BC4:
                    return TextureDecoder.DecodeBC4(compressedRow, width, height, outputRow);
                case TextureFormat.BC5:
                    return TextureDecoder.DecodeBC5(compressedRow, width, height, outputRow);
                case TextureFormat.BC6H:
                    return TextureDecoder.DecodeBC6(compressedRow, width, height, outputRow);
                case TextureFormat.BC7:
                    return TextureDecoder.DecodeBC7(compressedRow, width, height, outputRow);
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC_RGB4_3DS:
                    return TextureDecoder.DecodeETC1(compressedRow, width, height, outputRow);
                case TextureFormat.ETC2_RGB:
                    return TextureDecoder.DecodeETC2(compressedRow, width, height, outputRow);
                case TextureFormat.ETC2_RGBA1:
                    return TextureDecoder.DecodeETC2A1(compressedRow, width, height, outputRow);
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC_RGBA8_3DS:
                    return TextureDecoder.DecodeETC2A8(compressedRow, width, height, outputRow);
                case TextureFormat.EAC_R:
                    return TextureDecoder.DecodeEACR(compressedRow, width, height, outputRow);
                case TextureFormat.EAC_R_SIGNED:
                    return TextureDecoder.DecodeEACRSigned(compressedRow, width, height, outputRow);
                case TextureFormat.EAC_RG:
                    return TextureDecoder.DecodeEACRG(compressedRow, width, height, outputRow);
                case TextureFormat.EAC_RG_SIGNED:
                    return TextureDecoder.DecodeEACRGSigned(compressedRow, width, height, outputRow);
                case TextureFormat.ATC_RGB4:
                    return TextureDecoder.DecodeATCRGB4(compressedRow, width, height, outputRow);
                case TextureFormat.ATC_RGBA8:
                    return TextureDecoder.DecodeATCRGBA8(compressedRow, width, height, outputRow);

                case TextureFormat.ASTC_RGB_4x4:
                case TextureFormat.ASTC_RGBA_4x4:
                case TextureFormat.ASTC_HDR_4x4:
                    return TextureDecoder.DecodeASTC(compressedRow, width, height, 4, 4, outputRow);

                case TextureFormat.ASTC_RGB_5x5:
                case TextureFormat.ASTC_RGBA_5x5:
                case TextureFormat.ASTC_HDR_5x5:
                    return TextureDecoder.DecodeASTC(compressedRow, width, height, 5, 5, outputRow);

                case TextureFormat.ASTC_RGB_6x6:
                case TextureFormat.ASTC_RGBA_6x6:
                case TextureFormat.ASTC_HDR_6x6:
                    return TextureDecoder.DecodeASTC(compressedRow, width, height, 6, 6, outputRow);

                case TextureFormat.ASTC_RGB_8x8:
                case TextureFormat.ASTC_RGBA_8x8:
                case TextureFormat.ASTC_HDR_8x8:
                    return TextureDecoder.DecodeASTC(compressedRow, width, height, 8, 8, outputRow);

                case TextureFormat.ASTC_RGB_10x10:
                case TextureFormat.ASTC_RGBA_10x10:
                case TextureFormat.ASTC_HDR_10x10:
                    return TextureDecoder.DecodeASTC(compressedRow, width, height, 10, 10, outputRow);

                case TextureFormat.ASTC_RGB_12x12:
                case TextureFormat.ASTC_RGBA_12x12:
                case TextureFormat.ASTC_HDR_12x12:
                    return TextureDecoder.DecodeASTC(compressedRow, width, height, 12, 12, outputRow);
            }
            return false;
        }

        private bool DecodeTexture2DStreaming(byte[] bytes)
        {
            return bytes != null && DecodeTexture2DStreamingCore(bytes.AsSpan(), bytes);
        }

        private bool DecodeTexture2DStreamingCore(Span<byte> bytes, byte[]? backingArray)
        {
            if (!GetFormatParams(m_TextureFormat, out var param))
            {
                return false;
            }

            const int TargetReadChunkBytes = 256 * 1024;
            const long MaxDecodedStripBytes = 4L * 1024 * 1024;

            using var readSession = reader.OpenReadSession();

            if (param.IsCompressed)
            {
                int blockWidth = param.BlockWidth;
                int blockHeight = param.BlockHeight;
                int bytesPerBlock = param.BytesPerBlock;

                int blocksX = (m_Width + blockWidth - 1) / blockWidth;
                int blocksY = (m_Height + blockHeight - 1) / blockHeight;
                int rowBytes = blocksX * bytesPerBlock;

                long decodedRowBytes = (long)m_Width * blockHeight * 4;
                int maxByDecoded = (int)Math.Max(1L, MaxDecodedStripBytes / Math.Max(1L, decodedRowBytes));
                int maxByCompressed = Math.Max(1, TargetReadChunkBytes / Math.Max(1, rowBytes));
                int stripBlocksY = Math.Min(blocksY, Math.Min(maxByDecoded, maxByCompressed));

                int maxStripCompressedBytes = stripBlocksY * rowBytes;

                bool needsXboxSwap = platform == BuildTarget.XBOX360 &&
                    (m_TextureFormat == TextureFormat.DXT1 || m_TextureFormat == TextureFormat.DXT3 || m_TextureFormat == TextureFormat.DXT5);

                byte[] compressedStrip = BigArrayPool<byte>.Shared.Rent(maxStripCompressedBytes);
                try
                {
                    int outputRowStride = m_Width * 4;
                    for (int by = 0; by < blocksY; by += stripBlocksY)
                    {
                        int blocksInStrip = Math.Min(stripBlocksY, blocksY - by);
                        int chunkHeight = Math.Min(blocksInStrip * blockHeight, m_Height - by * blockHeight);
                        int stripBytes = blocksInStrip * rowBytes;
                        long offset = (long)by * rowBytes;

                        readSession.ReadSegment(offset, compressedStrip, 0, stripBytes);

                        if (needsXboxSwap)
                        {
                            var words = MemoryMarshal.Cast<byte, ushort>(compressedStrip.AsSpan(0, stripBytes & ~1));
                            for (var i = 0; i < words.Length; i++)
                            {
                                words[i] = BinaryPrimitives.ReverseEndianness(words[i]);
                            }
                        }

                        int dstOffset = by * blockHeight * outputRowStride;
                        int dstLength = chunkHeight * outputRowStride;
                        if (!DecodeBlockRow(m_TextureFormat, compressedStrip.AsSpan(0, stripBytes), m_Width, chunkHeight, blockWidth, blockHeight, bytes.Slice(dstOffset, dstLength)))
                        {
                            return false;
                        }
                    }
                    return true;
                }
                finally
                {
                    BigArrayPool<byte>.Shared.Return(compressedStrip);
                }
            }
            else
            {
                // Uncompressed format decoded in multi-row chunks via a single open read session
                int bytesPerPixel = param.BytesPerBlock;
                int rowBytes = m_Width * bytesPerPixel;
                int rowsPerChunk = Math.Clamp(TargetReadChunkBytes / Math.Max(1, rowBytes), 1, m_Height);

                if (m_TextureFormat == TextureFormat.BGRA32)
                {
                    byte[]? bgraRowScratch = null;
                    try
                    {
                        for (int yStart = 0; yStart < m_Height; yStart += rowsPerChunk)
                        {
                            int chunkRows = Math.Min(rowsPerChunk, m_Height - yStart);
                            int chunkBytes = chunkRows * rowBytes;
                            if (backingArray != null)
                            {
                                readSession.ReadSegment((long)yStart * rowBytes, backingArray, yStart * rowBytes, chunkBytes);
                            }
                            else
                            {
                                bgraRowScratch ??= BigArrayPool<byte>.Shared.Rent(rowsPerChunk * rowBytes);
                                readSession.ReadSegment((long)yStart * rowBytes, bgraRowScratch, 0, chunkBytes);
                                bgraRowScratch.AsSpan(0, chunkBytes).CopyTo(bytes.Slice(yStart * rowBytes, chunkBytes));
                            }
                        }
                        return true;
                    }
                    finally
                    {
                        if (bgraRowScratch != null)
                        {
                            BigArrayPool<byte>.Shared.Return(bgraRowScratch);
                        }
                    }
                }

                byte[] tempChunk = BigArrayPool<byte>.Shared.Rent(rowsPerChunk * rowBytes);
                try
                {
                    int outputRowStride = m_Width * 4;
                    for (int yStart = 0; yStart < m_Height; yStart += rowsPerChunk)
                    {
                        int chunkRows = Math.Min(rowsPerChunk, m_Height - yStart);
                        int chunkBytes = chunkRows * rowBytes;
                        readSession.ReadSegment((long)yStart * rowBytes, tempChunk, 0, chunkBytes);

                        if (m_TextureFormat == TextureFormat.RGBA32)
                        {
                            SwizzleRgbaBgra(tempChunk.AsSpan(0, chunkBytes), bytes.Slice(yStart * outputRowStride, chunkBytes));
                            continue;
                        }
                        if (m_TextureFormat == TextureFormat.ARGB32)
                        {
                            SwizzleArgbToBgra(tempChunk.AsSpan(0, chunkBytes), bytes.Slice(yStart * outputRowStride, chunkBytes));
                            continue;
                        }

                        for (int r = 0; r < chunkRows; r++)
                        {
                            int srcRow = r * rowBytes;
                            int dstRow = (yStart + r) * outputRowStride;

                            switch (m_TextureFormat)
                            {
                                case TextureFormat.Alpha8:
                                    for (int x = 0; x < m_Width; x++)
                                    {
                                        int outIdx = dstRow + x * 4;
                                        bytes[outIdx] = 0xFF;
                                        bytes[outIdx + 1] = 0xFF;
                                        bytes[outIdx + 2] = 0xFF;
                                        bytes[outIdx + 3] = tempChunk[srcRow + x];
                                    }
                                    break;
                                case TextureFormat.RGB24:
                                    for (int x = 0; x < m_Width; x++)
                                    {
                                        int inIdx = srcRow + x * 3;
                                        int outIdx = dstRow + x * 4;
                                        bytes[outIdx] = tempChunk[inIdx + 2];
                                        bytes[outIdx + 1] = tempChunk[inIdx + 1];
                                        bytes[outIdx + 2] = tempChunk[inIdx];
                                        bytes[outIdx + 3] = 255;
                                    }
                                    break;
                                case TextureFormat.BGR24:
                                    for (int x = 0; x < m_Width; x++)
                                    {
                                        int inIdx = srcRow + x * 3;
                                        int outIdx = dstRow + x * 4;
                                        bytes[outIdx] = tempChunk[inIdx];
                                        bytes[outIdx + 1] = tempChunk[inIdx + 1];
                                        bytes[outIdx + 2] = tempChunk[inIdx + 2];
                                        bytes[outIdx + 3] = 255;
                                    }
                                    break;
                                case TextureFormat.RG16:
                                    for (int x = 0; x < m_Width; x++)
                                    {
                                        int inIdx = srcRow + x * 2;
                                        int outIdx = dstRow + x * 4;
                                        bytes[outIdx] = 0;
                                        bytes[outIdx + 1] = tempChunk[inIdx + 1];
                                        bytes[outIdx + 2] = tempChunk[inIdx];
                                        bytes[outIdx + 3] = 255;
                                    }
                                    break;
                                case TextureFormat.R8:
                                    for (int x = 0; x < m_Width; x++)
                                    {
                                        int outIdx = dstRow + x * 4;
                                        bytes[outIdx] = 0;
                                        bytes[outIdx + 1] = 0;
                                        bytes[outIdx + 2] = tempChunk[srcRow + x];
                                        bytes[outIdx + 3] = 255;
                                    }
                                    break;
                            }
                        }
                    }
                    return true;
                }
                finally
                {
                    BigArrayPool<byte>.Shared.Return(tempChunk);
                }
            }
        }
    }
}
