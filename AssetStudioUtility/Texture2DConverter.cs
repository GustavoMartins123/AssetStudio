using System;
using System.Runtime.CompilerServices;
using Texture2DDecoder;

namespace AssetStudio
{
    public class Texture2DConverter
    {
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
            outPutSize = m_Width * m_Height * 4;
        }

        public bool DecodeTexture2D(byte[] bytes)
        {
            if (reader.Size == 0 || m_Width == 0 || m_Height == 0)
            {
                return false;
            }
            var minDataSize = GetMinimumDataSize(m_TextureFormat, m_Width, m_Height);
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
                reader.GetData(buff);
            }
            catch (Exception ex)
            {
                BigArrayPool<byte>.Shared.Return(buff);
                Logger.Warning($"Failed to read texture data: {ex.Message}");
                return false;
            }
            switch (m_TextureFormat)
            {
                case TextureFormat.Alpha8: //test pass
                    flag = DecodeAlpha8(buff, bytes);
                    break;
                case TextureFormat.ARGB4444: //test pass
                    SwapBytesForXbox(buff);
                    flag = DecodeARGB4444(buff, bytes);
                    break;
                case TextureFormat.RGB24: //test pass
                    flag = DecodeRGB24(buff, bytes);
                    break;
                case TextureFormat.RGBA32: //test pass
                    flag = DecodeRGBA32(buff, bytes);
                    break;
                case TextureFormat.ARGB32: //test pass
                    flag = DecodeARGB32(buff, bytes);
                    break;
                case TextureFormat.ARGBFloat:
                    flag = DecodeARGBFloat(buff, bytes);
                    break;
                case TextureFormat.RGB565: //test pass
                    SwapBytesForXbox(buff);
                    flag = DecodeRGB565(buff, bytes);
                    break;
                case TextureFormat.BGR24:
                    flag = DecodeBGR24(buff, bytes);
                    break;
                case TextureFormat.R16: //test pass
                    flag = DecodeR16(buff, bytes);
                    break;
                case TextureFormat.DXT1: //test pass
                    SwapBytesForXbox(buff);
                    flag = DecodeDXT1(buff, bytes);
                    break;
                case TextureFormat.DXT3:
                    SwapBytesForXbox(buff);
                    flag = DecodeDXT3(buff, bytes);
                    break;
                case TextureFormat.DXT5: //test pass
                    SwapBytesForXbox(buff);
                    flag = DecodeDXT5(buff, bytes);
                    break;
                case TextureFormat.RGBA4444: //test pass
                    flag = DecodeRGBA4444(buff, bytes);
                    break;
                case TextureFormat.BGRA32: //test pass
                    flag = DecodeBGRA32(buff, bytes);
                    break;
                case TextureFormat.RHalf:
                    flag = DecodeRHalf(buff, bytes);
                    break;
                case TextureFormat.RGHalf:
                    flag = DecodeRGHalf(buff, bytes);
                    break;
                case TextureFormat.RGBAHalf: //test pass
                    flag = DecodeRGBAHalf(buff, bytes);
                    break;
                case TextureFormat.RFloat:
                    flag = DecodeRFloat(buff, bytes);
                    break;
                case TextureFormat.RGFloat:
                    flag = DecodeRGFloat(buff, bytes);
                    break;
                case TextureFormat.RGBAFloat:
                    flag = DecodeRGBAFloat(buff, bytes);
                    break;
                case TextureFormat.RGBFloat:
                    flag = DecodeRGBFloat(buff, bytes);
                    break;
                case TextureFormat.YUY2: //test pass
                    flag = DecodeYUY2(buff, bytes);
                    break;
                case TextureFormat.RGB9e5Float: //test pass
                    flag = DecodeRGB9e5Float(buff, bytes);
                    break;
                case TextureFormat.BC6H: //test pass
                    flag = DecodeBC6H(buff, bytes);
                    break;
                case TextureFormat.BC7: //test pass
                    flag = DecodeBC7(buff, bytes);
                    break;
                case TextureFormat.BC4: //test pass
                    flag = DecodeBC4(buff, bytes);
                    break;
                case TextureFormat.BC5: //test pass
                    flag = DecodeBC5(buff, bytes);
                    break;
                case TextureFormat.DXT1Crunched: //test pass
                    flag = DecodeDXT1Crunched(buff, bytes);
                    break;
                case TextureFormat.DXT5Crunched: //test pass
                    flag = DecodeDXT5Crunched(buff, bytes);
                    break;
                case TextureFormat.PVRTC_RGB2: //test pass
                case TextureFormat.PVRTC_RGBA2: //test pass
                    flag = DecodePVRTC(buff, bytes, true);
                    break;
                case TextureFormat.PVRTC_RGB4: //test pass
                case TextureFormat.PVRTC_RGBA4: //test pass
                    flag = DecodePVRTC(buff, bytes, false);
                    break;
                case TextureFormat.ETC_RGB4: //test pass
                case TextureFormat.ETC_RGB4_3DS:
                    flag = DecodeETC1(buff, bytes);
                    break;
                case TextureFormat.ATC_RGB4: //test pass
                    flag = DecodeATCRGB4(buff, bytes);
                    break;
                case TextureFormat.ATC_RGBA8: //test pass
                    flag = DecodeATCRGBA8(buff, bytes);
                    break;
                case TextureFormat.EAC_R: //test pass
                    flag = DecodeEACR(buff, bytes);
                    break;
                case TextureFormat.EAC_R_SIGNED:
                    flag = DecodeEACRSigned(buff, bytes);
                    break;
                case TextureFormat.EAC_RG: //test pass
                    flag = DecodeEACRG(buff, bytes);
                    break;
                case TextureFormat.EAC_RG_SIGNED:
                    flag = DecodeEACRGSigned(buff, bytes);
                    break;
                case TextureFormat.ETC2_RGB: //test pass
                    flag = DecodeETC2(buff, bytes);
                    break;
                case TextureFormat.ETC2_RGBA1: //test pass
                    flag = DecodeETC2A1(buff, bytes);
                    break;
                case TextureFormat.ETC2_RGBA8: //test pass
                case TextureFormat.ETC_RGBA8_3DS:
                    flag = DecodeETC2A8(buff, bytes);
                    break;
                case TextureFormat.ASTC_RGB_4x4: //test pass
                case TextureFormat.ASTC_RGBA_4x4: //test pass
                case TextureFormat.ASTC_HDR_4x4: //test pass
                    flag = DecodeASTC(buff, bytes, 4);
                    break;
                case TextureFormat.ASTC_RGB_5x5: //test pass
                case TextureFormat.ASTC_RGBA_5x5: //test pass
                case TextureFormat.ASTC_HDR_5x5: //test pass
                    flag = DecodeASTC(buff, bytes, 5);
                    break;
                case TextureFormat.ASTC_RGB_6x6: //test pass
                case TextureFormat.ASTC_RGBA_6x6: //test pass
                case TextureFormat.ASTC_HDR_6x6: //test pass
                    flag = DecodeASTC(buff, bytes, 6);
                    break;
                case TextureFormat.ASTC_RGB_8x8: //test pass
                case TextureFormat.ASTC_RGBA_8x8: //test pass
                case TextureFormat.ASTC_HDR_8x8: //test pass
                    flag = DecodeASTC(buff, bytes, 8);
                    break;
                case TextureFormat.ASTC_RGB_10x10: //test pass
                case TextureFormat.ASTC_RGBA_10x10: //test pass
                case TextureFormat.ASTC_HDR_10x10: //test pass
                    flag = DecodeASTC(buff, bytes, 10);
                    break;
                case TextureFormat.ASTC_RGB_12x12: //test pass
                case TextureFormat.ASTC_RGBA_12x12: //test pass
                case TextureFormat.ASTC_HDR_12x12: //test pass
                    flag = DecodeASTC(buff, bytes, 12);
                    break;
                case TextureFormat.RG16: //test pass
                    flag = DecodeRG16(buff, bytes);
                    break;
                case TextureFormat.R8: //test pass
                    flag = DecodeR8(buff, bytes);
                    break;
                case TextureFormat.ETC_RGB4Crunched: //test pass
                    flag = DecodeETC1Crunched(buff, bytes);
                    break;
                case TextureFormat.ETC2_RGBA8Crunched: //test pass
                    flag = DecodeETC2A8Crunched(buff, bytes);
                    break;
                case TextureFormat.RG32: //test pass
                    flag = DecodeRG32(buff, bytes);
                    break;
                case TextureFormat.RGB48: //test pass
                    flag = DecodeRGB48(buff, bytes);
                    break;
                case TextureFormat.RGBA64: //test pass
                    flag = DecodeRGBA64(buff, bytes);
                    break;
            }
            BigArrayPool<byte>.Shared.Return(buff);
            return flag;
        }

        private void SwapBytesForXbox(byte[] image_data)
        {
            if (platform == BuildTarget.XBOX360)
            {
                for (var i = 0; i < reader.Size / 2; i++)
                {
                    var b = image_data[i * 2];
                    image_data[i * 2] = image_data[i * 2 + 1];
                    image_data[i * 2 + 1] = b;
                }
            }
        }

        private bool DecodeAlpha8(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            var span = new Span<byte>(buff);
            span.Fill(0xFF);
            for (var i = 0; i < size; i++)
            {
                buff[i * 4 + 3] = image_data[i];
            }
            return true;
        }

        private bool DecodeARGB4444(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            var pixelNew = new byte[4];
            for (var i = 0; i < size; i++)
            {
                var pixelOldShort = BitConverter.ToUInt16(image_data, i * 2);
                pixelNew[0] = (byte)(pixelOldShort & 0x000f);
                pixelNew[1] = (byte)((pixelOldShort & 0x00f0) >> 4);
                pixelNew[2] = (byte)((pixelOldShort & 0x0f00) >> 8);
                pixelNew[3] = (byte)((pixelOldShort & 0xf000) >> 12);
                for (var j = 0; j < 4; j++)
                    pixelNew[j] = (byte)((pixelNew[j] << 4) | pixelNew[j]);
                pixelNew.CopyTo(buff, i * 4);
            }
            return true;
        }

        private bool DecodeRGB24(byte[] image_data, byte[] buff)
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

        private bool DecodeRGBA32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = image_data[i + 2];
                buff[i + 1] = image_data[i + 1];
                buff[i + 2] = image_data[i + 0];
                buff[i + 3] = image_data[i + 3];
            }
            return true;
        }

        private bool DecodeARGB32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = image_data[i + 3];
                buff[i + 1] = image_data[i + 2];
                buff[i + 2] = image_data[i + 1];
                buff[i + 3] = image_data[i + 0];
            }
            return true;
        }

        private bool DecodeARGBFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = FloatToByte(BitConverter.ToSingle(image_data, i * 4 + 12));
                buff[i + 1] = FloatToByte(BitConverter.ToSingle(image_data, i * 4 + 8));
                buff[i + 2] = FloatToByte(BitConverter.ToSingle(image_data, i * 4 + 4));
                buff[i + 3] = FloatToByte(BitConverter.ToSingle(image_data, i * 4));
            }
            return true;
        }

        private bool DecodeRGB565(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                var p = BitConverter.ToUInt16(image_data, i * 2);
                buff[i * 4] = (byte)((p << 3) | (p >> 2 & 7));
                buff[i * 4 + 1] = (byte)((p >> 3 & 0xfc) | (p >> 9 & 3));
                buff[i * 4 + 2] = (byte)((p >> 8 & 0xf8) | (p >> 13));
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeBGR24(byte[] image_data, byte[] buff)
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

        private bool DecodeR16(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //b
                buff[i * 4 + 1] = 0; //g
                buff[i * 4 + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2)); //r
                buff[i * 4 + 3] = 255; //a
            }
            return true;
        }

        private bool DecodeDXT1(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeDXT1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT3(byte[] image_data, byte[] buff)
        {
            int blockCountX = (m_Width + 3) / 4;
            int blockCountY = (m_Height + 3) / 4;
            int bytesPerBlock = 16;
            
            for (int y = 0; y < blockCountY; y++)
            {
                for (int x = 0; x < blockCountX; x++)
                {
                    int blockIndex = (y * blockCountX + x) * bytesPerBlock;
                    if (blockIndex + 16 > image_data.Length)
                        continue;

                    ulong alphaData = BitConverter.ToUInt64(image_data, blockIndex);
                    ushort c0 = BitConverter.ToUInt16(image_data, blockIndex + 8);
                    ushort c1 = BitConverter.ToUInt16(image_data, blockIndex + 10);
                    uint colorIndices = BitConverter.ToUInt32(image_data, blockIndex + 12);

                    int r5_0 = (c0 >> 11) & 0x1F;
                    int g6_0 = (c0 >> 5) & 0x3F;
                    int b5_0 = c0 & 0x1F;
                    byte r0 = (byte)((r5_0 << 3) | (r5_0 >> 2));
                    byte g0 = (byte)((g6_0 << 2) | (g6_0 >> 4));
                    byte b0 = (byte)((b5_0 << 3) | (b5_0 >> 2));

                    int r5_1 = (c1 >> 11) & 0x1F;
                    int g6_1 = (c1 >> 5) & 0x3F;
                    int b5_1 = c1 & 0x1F;
                    byte r1 = (byte)((r5_1 << 3) | (r5_1 >> 2));
                    byte g1 = (byte)((g6_1 << 2) | (g6_1 >> 4));
                    byte b1 = (byte)((b5_1 << 3) | (b5_1 >> 2));

                    byte r2 = (byte)((2 * r0 + r1) / 3);
                    byte g2 = (byte)((2 * g0 + g1) / 3);
                    byte b2 = (byte)((2 * b0 + b1) / 3);

                    byte r3 = (byte)((r0 + 2 * r1) / 3);
                    byte g3 = (byte)((g0 + 2 * g1) / 3);
                    byte b3 = (byte)((b0 + 2 * b1) / 3);

                    for (int py = 0; py < 4; py++)
                    {
                        int pixelY = y * 4 + py;
                        if (pixelY >= m_Height) continue;

                        for (int px = 0; px < 4; px++)
                        {
                            int pixelX = x * 4 + px;
                            if (pixelX >= m_Width) continue;

                            int pixelIndexInBlock = py * 4 + px;
                            
                            int alphaShift = pixelIndexInBlock * 4;
                            byte alpha4 = (byte)((alphaData >> alphaShift) & 0x0F);
                            byte alpha = (byte)((alpha4 << 4) | alpha4);

                            int colorShift = pixelIndexInBlock * 2;
                            uint colorIndex = (colorIndices >> colorShift) & 0x03;

                            byte r = 0, g = 0, b = 0;
                            switch (colorIndex)
                            {
                                case 0: r = r0; g = g0; b = b0; break;
                                case 1: r = r1; g = g1; b = b1; break;
                                case 2: r = r2; g = g2; b = b2; break;
                                case 3: r = r3; g = g3; b = b3; break;
                            }

                            int outIndex = (pixelY * m_Width + pixelX) * 4;
                            if (outIndex + 4 <= buff.Length)
                            {
                                buff[outIndex] = b;
                                buff[outIndex + 1] = g;
                                buff[outIndex + 2] = r;
                                buff[outIndex + 3] = alpha;
                            }
                        }
                    }
                }
            }
            return true;
        }

        private bool DecodeDXT5(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeDXT5(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeRGBA4444(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            var pixelNew = new byte[4];
            for (var i = 0; i < size; i++)
            {
                var pixelOldShort = BitConverter.ToUInt16(image_data, i * 2);
                pixelNew[0] = (byte)((pixelOldShort & 0x00f0) >> 4);
                pixelNew[1] = (byte)((pixelOldShort & 0x0f00) >> 8);
                pixelNew[2] = (byte)((pixelOldShort & 0xf000) >> 12);
                pixelNew[3] = (byte)(pixelOldShort & 0x000f);
                for (var j = 0; j < 4; j++)
                    pixelNew[j] = (byte)((pixelNew[j] << 4) | pixelNew[j]);
                pixelNew.CopyTo(buff, i * 4);
            }
            return true;
        }

        private bool DecodeBGRA32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = image_data[i];
                buff[i + 1] = image_data[i + 1];
                buff[i + 2] = image_data[i + 2];
                buff[i + 3] = image_data[i + 3];
            }
            return true;
        }

        private bool DecodeRHalf(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = 0;
                buff[i + 2] = (byte)Math.Round(Half.ToHalf(image_data, i / 2) * 255f);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGHalf(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = (byte)Math.Round(Half.ToHalf(image_data, i + 2) * 255f);
                buff[i + 2] = (byte)Math.Round(Half.ToHalf(image_data, i) * 255f);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBAHalf(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = (byte)Math.Round(Half.ToHalf(image_data, i * 2 + 4) * 255f);
                buff[i + 1] = (byte)Math.Round(Half.ToHalf(image_data, i * 2 + 2) * 255f);
                buff[i + 2] = (byte)Math.Round(Half.ToHalf(image_data, i * 2) * 255f);
                buff[i + 3] = (byte)Math.Round(Half.ToHalf(image_data, i * 2 + 6) * 255f);
            }
            return true;
        }

        private bool DecodeRFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = 0;
                buff[i + 2] = (byte)Math.Round(BitConverter.ToSingle(image_data, i) * 255f);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = (byte)Math.Round(BitConverter.ToSingle(image_data, i * 2 + 4) * 255f);
                buff[i + 2] = (byte)Math.Round(BitConverter.ToSingle(image_data, i * 2) * 255f);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBAFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = FloatToByte(BitConverter.ToSingle(image_data, i * 4 + 8));
                buff[i + 1] = FloatToByte(BitConverter.ToSingle(image_data, i * 4 + 4));
                buff[i + 2] = FloatToByte(BitConverter.ToSingle(image_data, i * 4));
                buff[i + 3] = FloatToByte(BitConverter.ToSingle(image_data, i * 4 + 12));
            }
            return true;
        }

        private bool DecodeRGBFloat(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                var input = i * 12;
                var output = i * 4;
                buff[output] = FloatToByte(BitConverter.ToSingle(image_data, input + 8));
                buff[output + 1] = FloatToByte(BitConverter.ToSingle(image_data, input + 4));
                buff[output + 2] = FloatToByte(BitConverter.ToSingle(image_data, input));
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
        private static byte FloatToByte(float value)
        {
            return ClampByte((int)Math.Round(value * 255f));
        }

        private bool DecodeYUY2(byte[] image_data, byte[] buff)
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

        private bool DecodeRGB9e5Float(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                var n = BitConverter.ToInt32(image_data, i);
                var scale = n >> 27 & 0x1f;
                var scalef = Math.Pow(2, scale - 24);
                var b = n >> 18 & 0x1ff;
                var g = n >> 9 & 0x1ff;
                var r = n & 0x1ff;
                buff[i] = (byte)Math.Round(b * scalef * 255f);
                buff[i + 1] = (byte)Math.Round(g * scalef * 255f);
                buff[i + 2] = (byte)Math.Round(r * scalef * 255f);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeBC4(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC4(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC5(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC5(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC6H(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC6(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC7(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC7(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT1Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result))
            {
                if (DecodeDXT1(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodeDXT5Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result))
            {
                if (DecodeDXT5(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodePVRTC(byte[] image_data, byte[] buff, bool is2bpp)
        {
            return TextureDecoder.DecodePVRTC(image_data, m_Width, m_Height, buff, is2bpp);
        }

        private bool DecodeETC1(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeATCRGB4(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeATCRGB4(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeATCRGBA8(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeATCRGBA8(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACR(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACR(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRSigned(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACRSigned(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRG(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACRG(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRGSigned(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACRGSigned(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC2(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2A1(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC2A1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2A8(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC2A8(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeASTC(byte[] image_data, byte[] buff, int blocksize)
        {
            return TextureDecoder.DecodeASTC(image_data, m_Width, m_Height, blocksize, blocksize, buff);
        }

        private bool DecodeRG16(byte[] image_data, byte[] buff)
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

        private bool DecodeR8(byte[] image_data, byte[] buff)
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

        private bool DecodeETC1Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result))
            {
                if (DecodeETC1(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodeETC2A8Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result))
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

        private bool DecodeRG32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;                                                                          //b
                buff[i + 1] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i + 2));     //g
                buff[i + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i));         //r
                buff[i + 3] = byte.MaxValue;                                                          //a
            }
            return true;
        }

        private bool DecodeRGB48(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 6 + 4));     //b
                buff[i * 4 + 1] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 6 + 2)); //g
                buff[i * 4 + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 6));     //r
                buff[i * 4 + 3] = byte.MaxValue;                                                          //a
            }
            return true;
        }

        private bool DecodeRGBA64(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2 + 4));     //b
                buff[i + 1] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2 + 2)); //g
                buff[i + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2));     //r
                buff[i + 3] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2 + 6)); //a
            }
            return true;
        }

        private bool UnpackCrunch(byte[] image_data, out byte[] result)
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
            long pixels = (long)width * height;
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            long blocks = (long)bw * bh;
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
                case TextureFormat.RGBAFloat:
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
                    return (long)((width + 3) / 4) * ((height + 3) / 4) * 16;
                case TextureFormat.ASTC_RGB_5x5:
                case TextureFormat.ASTC_RGBA_5x5:
                case TextureFormat.ASTC_HDR_5x5:
                    return (long)((width + 4) / 5) * ((height + 4) / 5) * 16;
                case TextureFormat.ASTC_RGB_6x6:
                case TextureFormat.ASTC_RGBA_6x6:
                case TextureFormat.ASTC_HDR_6x6:
                    return (long)((width + 5) / 6) * ((height + 5) / 6) * 16;
                case TextureFormat.ASTC_RGB_8x8:
                case TextureFormat.ASTC_RGBA_8x8:
                case TextureFormat.ASTC_HDR_8x8:
                    return (long)((width + 7) / 8) * ((height + 7) / 8) * 16;
                case TextureFormat.ASTC_RGB_10x10:
                case TextureFormat.ASTC_RGBA_10x10:
                case TextureFormat.ASTC_HDR_10x10:
                    return (long)((width + 9) / 10) * ((height + 9) / 10) * 16;
                case TextureFormat.ASTC_RGB_12x12:
                case TextureFormat.ASTC_RGBA_12x12:
                case TextureFormat.ASTC_HDR_12x12:
                    return (long)((width + 11) / 12) * ((height + 11) / 12) * 16;
                case TextureFormat.PVRTC_RGB2:
                case TextureFormat.PVRTC_RGBA2:
                    return Math.Max(pixels / 4, 2);
                case TextureFormat.PVRTC_RGB4:
                case TextureFormat.PVRTC_RGBA4:
                    return Math.Max(pixels / 2, 2);
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
    }
}
