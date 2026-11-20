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

        private static bool ValidateBuffers(byte[] data, int width, int height, byte[] image, long minDataSize)
        {
            if (data == null || image == null || width <= 0 || height <= 0 || minDataSize <= 0)
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
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeDXT1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeDXT5(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeDXT5(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodePVRTC(byte[] data, int width, int height, byte[] image, bool is2bpp)
        {
            if (!ValidateBuffers(data, width, height, image, PvrtcDataSize(width, height, is2bpp)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodePVRTC(pData, width, height, pImage, is2bpp);
                }
            }
        }

        public static bool DecodeETC1(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2A1(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2A1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2A8(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2A8(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACR(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACR(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRSigned(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRSigned(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRG(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRG(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRGSigned(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRGSigned(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC4(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC4(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC5(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC5(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC6(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC6(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC7(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC7(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeATCRGB4(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 8)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeATCRGB4(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeATCRGBA8(byte[] data, int width, int height, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, Block4x4DataSize(width, height, 16)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeATCRGBA8(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeASTC(byte[] data, int width, int height, int blockWidth, int blockHeight, byte[] image)
        {
            if (!ValidateBuffers(data, width, height, image, AstcDataSize(width, height, blockWidth, blockHeight)))
                return false;
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeASTC(pData, width, height, blockWidth, blockHeight, pImage);
                }
            }
        }

        public static byte[] UnpackCrunch(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            void* pBuffer;
            uint bufferSize;

            fixed (byte* pData = data)
            {
                UnpackCrunch(pData, (uint)data.Length, out pBuffer, out bufferSize);
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

        public static byte[] UnpackUnityCrunch(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            void* pBuffer;
            uint bufferSize;

            fixed (byte* pData = data)
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
