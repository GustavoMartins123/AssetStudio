using System;
using System.Runtime.InteropServices;

namespace Texture2DDecoder
{
    public static unsafe partial class TextureDecoder
    {

        static TextureDecoder()
        {
            DllLoader.PreloadDll(T2DDll.DllName);
        }

        private static bool ValidateBuffers(byte[]? data, int width, int height, byte[]? image, long minDataSize)
        {
            if (data == null || image == null)
            {
                return false;
            }

            return ValidateBuffers(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image), minDataSize);
        }

        private static bool ValidateBuffers(ReadOnlySpan<byte> data, int width, int height, Span<byte> image, long minDataSize)
        {
            if (width <= 0 || height <= 0 || minDataSize <= 0)
            {
                return false;
            }

            long requiredImageSize = (long)width * height * 4;
            if (requiredImageSize <= 0 || image.Length < requiredImageSize)
            {
                return false;
            }

            return data.Length >= minDataSize;
        }

        private static long Block4x4DataSize(int width, int height, int bytesPerBlock)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            return (((long)width + 3) / 4) * (((long)height + 3) / 4) * bytesPerBlock;
        }

        private static long PvrtcDataSize(int width, int height, bool is2bpp)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            long blocksX = is2bpp ? ((long)width + 7) / 8 : ((long)width + 3) / 4;
            long blocksY = ((long)height + 3) / 4;
            return blocksX * blocksY * 8;
        }

        private static long AstcDataSize(int width, int height, int blockWidth, int blockHeight)
        {
            if (width <= 0 || height <= 0 || blockWidth < 2 || blockHeight < 2 || blockWidth > 12 || blockHeight > 12 || blockWidth * blockHeight > 144)
            {
                return 0;
            }

            long blocksX = ((long)width + blockWidth - 1) / blockWidth;
            long blocksY = ((long)height + blockHeight - 1) / blockHeight;
            return blocksX * blocksY * 16;
        }

        public static bool DecodeDXT1(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeDXT1(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeDXT1(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeDXT1(pData, width, height, pImage);
            }
        }

        public static bool DecodeDXT3(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeDXT3(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeDXT3(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeDXT3(pData, width, height, pImage);
            }
        }

        public static bool DecodeDXT5(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeDXT5(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeDXT5(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeDXT5(pData, width, height, pImage);
            }
        }

        public static bool DecodePVRTC(byte[] data, int width, int height, byte[] image, bool is2bpp)
        {
            if (data == null || image == null)
                return false;
            return DecodePVRTC(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image), is2bpp);
        }

        public static bool DecodePVRTC(ReadOnlySpan<byte> data, int width, int height, Span<byte> image, bool is2bpp)
        {
            if (!ValidateBuffers(data, width, height, image, PvrtcDataSize(width, height, is2bpp)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodePVRTC(pData, width, height, pImage, is2bpp);
            }
        }

        public static bool DecodeETC1(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeETC1(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeETC1(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeETC1(pData, width, height, pImage);
            }
        }

        public static bool DecodeETC2(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeETC2(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeETC2(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeETC2(pData, width, height, pImage);
            }
        }

        public static bool DecodeETC2A1(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeETC2A1(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeETC2A1(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeETC2A1(pData, width, height, pImage);
            }
        }

        public static bool DecodeETC2A8(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeETC2A8(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeETC2A8(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeETC2A8(pData, width, height, pImage);
            }
        }

        public static bool DecodeEACR(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeEACR(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeEACR(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeEACR(pData, width, height, pImage);
            }
        }

        public static bool DecodeEACRSigned(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeEACRSigned(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeEACRSigned(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeEACRSigned(pData, width, height, pImage);
            }
        }

        public static bool DecodeEACRG(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeEACRG(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeEACRG(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeEACRG(pData, width, height, pImage);
            }
        }

        public static bool DecodeEACRGSigned(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeEACRGSigned(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeEACRGSigned(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeEACRGSigned(pData, width, height, pImage);
            }
        }

        public static bool DecodeBC4(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeBC4(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeBC4(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeBC4(pData, width, height, pImage);
            }
        }

        public static bool DecodeBC5(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeBC5(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeBC5(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeBC5(pData, width, height, pImage);
            }
        }

        public static bool DecodeBC6(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeBC6(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeBC6(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeBC6(pData, width, height, pImage);
            }
        }

        public static bool DecodeBC7(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeBC7(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeBC7(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeBC7(pData, width, height, pImage);
            }
        }

        public static bool DecodeATCRGB4(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeATCRGB4(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeATCRGB4(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeATCRGB4(pData, width, height, pImage);
            }
        }

        public static bool DecodeATCRGBA8(byte[] data, int width, int height, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeATCRGBA8(new ReadOnlySpan<byte>(data), width, height, new Span<byte>(image));
        }

        public static bool DecodeATCRGBA8(ReadOnlySpan<byte> data, int width, int height, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeATCRGBA8(pData, width, height, pImage);
            }
        }

        public static bool DecodeASTC(byte[] data, int width, int height, int blockWidth, int blockHeight, byte[] image)
        {
            if (data == null || image == null)
                return false;
            return DecodeASTC(new ReadOnlySpan<byte>(data), width, height, blockWidth, blockHeight, new Span<byte>(image));
        }

        public static bool DecodeASTC(ReadOnlySpan<byte> data, int width, int height, int blockWidth, int blockHeight, Span<byte> image)
        {
            if (!ValidateBuffers(data, width, height, image, AstcDataSize(width, height, blockWidth, blockHeight)))
                return false;
            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            fixed (byte* pImage = &MemoryMarshal.GetReference(image))
            {
                return DecodeASTC(pData, width, height, blockWidth, blockHeight, pImage);
            }
        }

        public static byte[]? UnpackCrunch(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            return UnpackCrunch(new ReadOnlySpan<byte>(data));
        }

        public static byte[]? UnpackCrunch(ReadOnlySpan<byte> data) => UnpackCrunch(data, mipLevel: null);

        public static byte[]? UnpackCrunchMip(ReadOnlySpan<byte> data, uint mipLevel) => UnpackCrunch(data, mipLevel);

        private static byte[]? UnpackCrunch(ReadOnlySpan<byte> data, uint? mipLevel)
        {
            if (data.IsEmpty)
            {
                return null;
            }

            void* pBuffer;
            uint bufferSize;

            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            {
                if (mipLevel is uint level)
                {
                    UnpackCrunchLevel(pData, (uint)data.Length, level, out pBuffer, out bufferSize);
                }
                else
                {
                    UnpackCrunch(pData, (uint)data.Length, out pBuffer, out bufferSize);
                }
            }

            if (pBuffer == null)
            {
                return null;
            }

            try
            {
                if (bufferSize == 0 || bufferSize > int.MaxValue)
                {
                    return null;
                }

                var result = new byte[bufferSize];
                Marshal.Copy(new IntPtr(pBuffer), result, 0, (int)bufferSize);
                return result;
            }
            finally
            {
                DisposeBuffer(ref pBuffer);
            }
        }

        public static byte[]? UnpackUnityCrunch(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            return UnpackUnityCrunch(new ReadOnlySpan<byte>(data));
        }

        public static byte[]? UnpackUnityCrunch(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return null;
            }

            void* pBuffer;
            uint bufferSize;

            fixed (byte* pData = &MemoryMarshal.GetReference(data))
            {
                UnpackUnityCrunch(pData, (uint)data.Length, out pBuffer, out bufferSize);
            }

            if (pBuffer == null)
            {
                return null;
            }

            try
            {
                if (bufferSize == 0 || bufferSize > int.MaxValue)
                {
                    return null;
                }

                var result = new byte[bufferSize];
                Marshal.Copy(new IntPtr(pBuffer), result, 0, (int)bufferSize);
                return result;
            }
            finally
            {
                DisposeBuffer(ref pBuffer);
            }
        }

    }
}
