using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public enum ExrPixelFormat
    {
        HalfFloat = 1,
        Float32 = 2
    }

    public sealed record HdrDecodeMetadata(
        TextureFormat SourceFormat,
        int MipLevel,
        int Width,
        int Height,
        bool PreservedTrueFloatRange,
        float MinValue,
        float MaxValue,
        float MaxLuminance);

    public sealed class HdrExportResult
    {
        public bool Success { get; init; }
        public string OutputPath { get; init; } = string.Empty;
        public HdrDecodeMetadata? Metadata { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public static class HdrTextureExporter
    {
        public const uint ExrMagicNumber = 0x01312F76u; // 20000630
        public const uint ExrVersion2SinglePartScanline = 2u;

        public static bool IsHdrFormat(TextureFormat format)
        {
            return format is TextureFormat.RGBAFloat
                or TextureFormat.RGBAHalf
                or TextureFormat.RGBFloat
                or TextureFormat.ARGBFloat
                or TextureFormat.RFloat
                or TextureFormat.RHalf
                or TextureFormat.RGFloat
                or TextureFormat.RGHalf
                or TextureFormat.RGB9e5Float
                or TextureFormat.BC6H
                or TextureFormat.ASTC_HDR_4x4
                or TextureFormat.ASTC_HDR_5x5
                or TextureFormat.ASTC_HDR_6x6
                or TextureFormat.ASTC_HDR_8x8
                or TextureFormat.ASTC_HDR_10x10
                or TextureFormat.ASTC_HDR_12x12;
        }

        public static bool TryDecodeLinearRgbaFloat(
            Texture2D? texture,
            out float[] rgbaFloats,
            out int width,
            out int height,
            bool flipVertical = true,
            int mipLevel = 0)
        {
            return TryDecodeLinearRgbaFloat(
                texture,
                out rgbaFloats,
                out width,
                out height,
                out _,
                flipVertical,
                mipLevel);
        }

        public static bool TryDecodeLinearRgbaFloat(
            Texture2D? texture,
            out float[] rgbaFloats,
            out int width,
            out int height,
            out HdrDecodeMetadata? metadata,
            bool flipVertical = true,
            int mipLevel = 0)
        {
            rgbaFloats = Array.Empty<float>();
            width = 0;
            height = 0;
            metadata = null;

            if (texture == null || texture.m_Width <= 0 || texture.m_Height <= 0 || mipLevel < 0)
            {
                return false;
            }

            var rawBytes = TextureMipmapService.ReadMipRawBytes(texture, mipLevel, out var mipInfo);
            if (rawBytes == null || mipInfo.Width <= 0 || mipInfo.Height <= 0)
            {
                return false;
            }

            width = mipInfo.Width;
            height = mipInfo.Height;

            long floatCountLong = (long)width * height * 4L;
            if (floatCountLong <= 0 || floatCountLong > Array.MaxLength)
            {
                width = 0;
                height = 0;
                return false;
            }

            var output = new float[(int)floatCountLong];
            bool preservedTrueFloatRange;
            bool decoded;

            switch (texture.m_TextureFormat)
            {
                case TextureFormat.RGBAFloat:
                    decoded = DecodeRgbaFloat(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.ARGBFloat:
                    decoded = DecodeArgbFloat(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RGBFloat:
                    decoded = DecodeRgbFloat(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RGFloat:
                    decoded = DecodeRgFloat(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RFloat:
                    decoded = DecodeRFloat(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RGBAHalf:
                    decoded = DecodeRgbaHalf(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RGHalf:
                    decoded = DecodeRgHalf(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RHalf:
                    decoded = DecodeRHalf(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.RGB9e5Float:
                    decoded = DecodeRgb9e5Float(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                case TextureFormat.BC6H:
                    decoded = DecodeBc6hUf16(rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = true;
                    break;
                default:
                    decoded = DecodeViaConverterFallback(texture, rawBytes, output, width, height, flipVertical);
                    preservedTrueFloatRange = false;
                    break;
            }

            if (!decoded)
            {
                width = 0;
                height = 0;
                return false;
            }

            ComputeRangeMetadata(
                output,
                texture.m_TextureFormat,
                mipLevel,
                width,
                height,
                preservedTrueFloatRange,
                out metadata);

            rgbaFloats = output;
            return true;
        }

        public static void WriteExr(
            Stream stream,
            ReadOnlySpan<float> rgbaFloats,
            int width,
            int height,
            ExrPixelFormat pixelFormat = ExrPixelFormat.HalfFloat)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Image dimensions must be positive.");
            }
            if (pixelFormat != ExrPixelFormat.HalfFloat && pixelFormat != ExrPixelFormat.Float32)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, "Unsupported OpenEXR pixel format.");
            }

            long requiredFloats = (long)width * height * 4L;
            if (rgbaFloats.Length < requiredFloats)
            {
                throw new ArgumentException($"RGBA float span length ({rgbaFloats.Length}) is smaller than required ({requiredFloats}).", nameof(rgbaFloats));
            }

            int bytesPerSample = pixelFormat == ExrPixelFormat.HalfFloat ? 2 : 4;
            long scanlinePixelDataSizeLong = (long)width * 4L * bytesPerSample;
            if (scanlinePixelDataSizeLong > int.MaxValue)
            {
                throw new InvalidOperationException($"Scanline byte size ({scanlinePixelDataSizeLong}) exceeds OpenEXR single scanline limit.");
            }
            int scanlinePixelDataSize = (int)scanlinePixelDataSizeLong;

            // Build OpenEXR 2.0 header in memory first so we know exact offsets without requiring Stream.Seek.
            byte[] headerBytes;
            using (var headerMs = new MemoryStream(512))
            {
                WriteUInt32LE(headerMs, ExrMagicNumber);
                WriteUInt32LE(headerMs, ExrVersion2SinglePartScanline);

                // Required attribute: channels (strict alphabetical order: A, B, G, R)
                WriteChannelsAttribute(headerMs, (int)pixelFormat);

                // Required attribute: compression = 0 (NO_COMPRESSION)
                WriteAttributeHeader(headerMs, "compression", "compression", 1);
                headerMs.WriteByte(0);

                // Required attribute: dataWindow
                WriteBox2iAttribute(headerMs, "dataWindow", 0, 0, width - 1, height - 1);

                // Required attribute: displayWindow
                WriteBox2iAttribute(headerMs, "displayWindow", 0, 0, width - 1, height - 1);

                // Required attribute: lineOrder = 0 (INCREASING_Y)
                WriteAttributeHeader(headerMs, "lineOrder", "lineOrder", 1);
                headerMs.WriteByte(0);

                // Required attribute: pixelAspectRatio = 1.0f
                WriteAttributeHeader(headerMs, "pixelAspectRatio", "float", 4);
                WriteSingleLE(headerMs, 1.0f);

                // Required attribute: screenWindowCenter = (0.0f, 0.0f)
                WriteAttributeHeader(headerMs, "screenWindowCenter", "v2f", 8);
                WriteSingleLE(headerMs, 0.0f);
                WriteSingleLE(headerMs, 0.0f);

                // Required attribute: screenWindowWidth = 1.0f
                WriteAttributeHeader(headerMs, "screenWindowWidth", "float", 4);
                WriteSingleLE(headerMs, 1.0f);

                // End of header marker
                headerMs.WriteByte(0);

                headerBytes = headerMs.ToArray();
            }

            stream.Write(headerBytes, 0, headerBytes.Length);

            // Line offset table: one 64-bit absolute byte offset per scanline
            long offsetTableBytes = (long)height * 8L;
            long firstScanlineOffset = headerBytes.Length + offsetTableBytes;
            long scanlineBlockStride = 8L + scanlinePixelDataSize;

            Span<byte> offsetScratch = stackalloc byte[8];
            for (int y = 0; y < height; y++)
            {
                ulong scanlineOffset = (ulong)(firstScanlineOffset + (long)y * scanlineBlockStride);
                BinaryPrimitives.WriteUInt64LittleEndian(offsetScratch, scanlineOffset);
                stream.Write(offsetScratch);
            }

            // Scanline blocks (y = 0 .. height - 1), planar channel order: A, B, G, R
            var scanlineBuffer = new byte[8 + scanlinePixelDataSize];
            int planeBytes = width * bytesPerSample;
            int offsetA = 8;
            int offsetB = offsetA + planeBytes;
            int offsetG = offsetB + planeBytes;
            int offsetR = offsetG + planeBytes;

            for (int y = 0; y < height; y++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(scanlineBuffer.AsSpan(0, 4), y);
                BinaryPrimitives.WriteInt32LittleEndian(scanlineBuffer.AsSpan(4, 4), scanlinePixelDataSize);

                int rowFloatBase = y * width * 4;
                if (pixelFormat == ExrPixelFormat.HalfFloat)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int p = rowFloatBase + x * 4;
                        int xOff = x * 2;
                        BinaryPrimitives.WriteHalfLittleEndian(scanlineBuffer.AsSpan(offsetA + xOff, 2), (Half)rgbaFloats[p + 3]);
                        BinaryPrimitives.WriteHalfLittleEndian(scanlineBuffer.AsSpan(offsetB + xOff, 2), (Half)rgbaFloats[p + 2]);
                        BinaryPrimitives.WriteHalfLittleEndian(scanlineBuffer.AsSpan(offsetG + xOff, 2), (Half)rgbaFloats[p + 1]);
                        BinaryPrimitives.WriteHalfLittleEndian(scanlineBuffer.AsSpan(offsetR + xOff, 2), (Half)rgbaFloats[p]);
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        int p = rowFloatBase + x * 4;
                        int xOff = x * 4;
                        BinaryPrimitives.WriteSingleLittleEndian(scanlineBuffer.AsSpan(offsetA + xOff, 4), rgbaFloats[p + 3]);
                        BinaryPrimitives.WriteSingleLittleEndian(scanlineBuffer.AsSpan(offsetB + xOff, 4), rgbaFloats[p + 2]);
                        BinaryPrimitives.WriteSingleLittleEndian(scanlineBuffer.AsSpan(offsetG + xOff, 4), rgbaFloats[p + 1]);
                        BinaryPrimitives.WriteSingleLittleEndian(scanlineBuffer.AsSpan(offsetR + xOff, 4), rgbaFloats[p]);
                    }
                }

                stream.Write(scanlineBuffer, 0, scanlineBuffer.Length);
            }
        }

        public static bool TryReadExrForValidation(
            Stream stream,
            out int width,
            out int height,
            out float[] rgbaFloats)
        {
            width = 0;
            height = 0;
            rgbaFloats = Array.Empty<float>();

            if (stream == null || !stream.CanRead)
            {
                return false;
            }

            try
            {
                byte[] data;
                if (stream is MemoryStream ms && ms.TryGetBuffer(out var seg))
                {
                    int start = (int)ms.Position;
                    int remaining = seg.Count - start;
                    if (remaining < 8)
                    {
                        return false;
                    }
                    data = new byte[remaining];
                    Buffer.BlockCopy(seg.Array!, seg.Offset + start, data, 0, remaining);
                }
                else
                {
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    data = copy.ToArray();
                }

                if (data.Length < 16)
                {
                    return false;
                }

                int pos = 0;
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;
                if (magic != ExrMagicNumber)
                {
                    return false;
                }

                uint versionField = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;
                if ((versionField & 0xFFu) != 2u || (versionField & ~0xFFu) != 0u)
                {
                    return false;
                }

                List<(string Name, int PixelType)>? channels = null;
                byte? compression = null;
                (int XMin, int YMin, int XMax, int YMax)? dataWindow = null;
                bool hasDisplayWindow = false;
                bool hasLineOrder = false;

                while (pos < data.Length)
                {
                    if (data[pos] == 0)
                    {
                        pos++;
                        break;
                    }

                    if (!TryReadNullTerminatedAscii(data, ref pos, out string attrName)
                        || !TryReadNullTerminatedAscii(data, ref pos, out string attrType)
                        || pos + 4 > data.Length)
                    {
                        return false;
                    }

                    int attrSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
                    pos += 4;
                    if (attrSize < 0 || pos > data.Length - attrSize)
                    {
                        return false;
                    }

                    int valueStart = pos;
                    int valueEnd = pos + attrSize;

                    if (attrName == "channels" && attrType == "chlist")
                    {
                        channels = new List<(string Name, int PixelType)>();
                        int cPos = valueStart;
                        string? prevName = null;
                        while (cPos < valueEnd)
                        {
                            if (data[cPos] == 0)
                            {
                                cPos++;
                                break;
                            }

                            if (!TryReadNullTerminatedAscii(data, ref cPos, out string chName) || cPos + 16 > valueEnd)
                            {
                                return false;
                            }

                            if (prevName != null && string.CompareOrdinal(prevName, chName) >= 0)
                            {
                                return false; // Must be strictly alphabetical
                            }
                            prevName = chName;

                            int pixelType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cPos, 4));
                            int xSampling = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cPos + 8, 4));
                            int ySampling = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cPos + 12, 4));
                            cPos += 16;

                            if (xSampling != 1 || ySampling != 1 || (pixelType != 1 && pixelType != 2))
                            {
                                return false;
                            }

                            channels.Add((chName, pixelType));
                        }
                    }
                    else if (attrName == "compression" && attrSize == 1)
                    {
                        compression = data[valueStart];
                    }
                    else if (attrName == "dataWindow" && attrType == "box2i" && attrSize == 16)
                    {
                        dataWindow = (
                            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(valueStart, 4)),
                            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(valueStart + 4, 4)),
                            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(valueStart + 8, 4)),
                            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(valueStart + 12, 4)));
                    }
                    else if (attrName == "displayWindow" && attrType == "box2i" && attrSize == 16)
                    {
                        hasDisplayWindow = true;
                    }
                    else if (attrName == "lineOrder" && attrSize == 1)
                    {
                        hasLineOrder = true;
                    }

                    pos = valueEnd;
                }

                if (channels == null || channels.Count == 0 || compression != 0 || dataWindow == null || !hasDisplayWindow || !hasLineOrder)
                {
                    return false;
                }

                var dw = dataWindow.Value;
                long wLong = (long)dw.XMax - dw.XMin + 1L;
                long hLong = (long)dw.YMax - dw.YMin + 1L;
                if (wLong <= 0 || hLong <= 0 || wLong > int.MaxValue || hLong > int.MaxValue)
                {
                    return false;
                }

                int parsedW = (int)wLong;
                int parsedH = (int)hLong;
                long floatCountLong = (long)parsedW * parsedH * 4L;
                if (floatCountLong > Array.MaxLength)
                {
                    return false;
                }

                long offsetTableBytes = (long)parsedH * 8L;
                if (pos > data.Length - offsetTableBytes)
                {
                    return false;
                }

                var offsets = new ulong[parsedH];
                for (int y = 0; y < parsedH; y++)
                {
                    offsets[y] = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + y * 8, 8));
                }

                int expectedScanlineBytes = 0;
                for (int c = 0; c < channels.Count; c++)
                {
                    int bps = channels[c].PixelType == 1 ? 2 : 4;
                    expectedScanlineBytes = checked(expectedScanlineBytes + parsedW * bps);
                }

                var pixels = new float[(int)floatCountLong];
                // Default alpha to 1.0f in case an EXR has no A channel
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 1.0f;
                }

                for (int row = 0; row < parsedH; row++)
                {
                    if (offsets[row] > (ulong)(data.Length - 8))
                    {
                        return false;
                    }

                    int blockOffset = (int)offsets[row];
                    int scanlineY = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(blockOffset, 4));
                    int dataSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(blockOffset + 4, 4));
                    if (dataSize != expectedScanlineBytes || scanlineY < dw.YMin || scanlineY > dw.YMax)
                    {
                        return false;
                    }

                    int pixelStart = blockOffset + 8;
                    if (pixelStart > data.Length - dataSize)
                    {
                        return false;
                    }

                    int dstRow = scanlineY - dw.YMin;
                    int dstRowBase = dstRow * parsedW * 4;
                    int chCursor = pixelStart;

                    for (int c = 0; c < channels.Count; c++)
                    {
                        var (chName, pixelType) = channels[c];
                        int targetChannel = chName switch
                        {
                            "R" => 0,
                            "G" => 1,
                            "B" => 2,
                            "A" => 3,
                            _ => -1
                        };

                        if (pixelType == 1)
                        {
                            if (targetChannel >= 0)
                            {
                                for (int x = 0; x < parsedW; x++)
                                {
                                    pixels[dstRowBase + x * 4 + targetChannel] =
                                        (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(chCursor + x * 2, 2));
                                }
                            }
                            chCursor += parsedW * 2;
                        }
                        else
                        {
                            if (targetChannel >= 0)
                            {
                                for (int x = 0; x < parsedW; x++)
                                {
                                    pixels[dstRowBase + x * 4 + targetChannel] =
                                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(chCursor + x * 4, 4));
                                }
                            }
                            chCursor += parsedW * 4;
                        }
                    }
                }

                width = parsedW;
                height = parsedH;
                rgbaFloats = pixels;
                return true;
            }
            catch
            {
                width = 0;
                height = 0;
                rgbaFloats = Array.Empty<float>();
                return false;
            }
        }

        public static void WriteFloat32Tiff(
            Stream stream,
            ReadOnlySpan<float> rgbaFloats,
            int width,
            int height)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Image dimensions must be positive.");
            }

            long requiredFloats = (long)width * height * 4L;
            if (rgbaFloats.Length < requiredFloats)
            {
                throw new ArgumentException($"RGBA float span length ({rgbaFloats.Length}) is smaller than required ({requiredFloats}).", nameof(rgbaFloats));
            }

            long pixelByteCountLong = requiredFloats * 4L;
            const uint ifdOffset = 8u;
            const ushort entryCount = 12;
            // Header (8) + Count (2) + 12*12 (144) + NextIFD (4) + Pad (2) = 160
            const uint bitsPerSampleOffset = 160u;
            const uint sampleFormatOffset = 168u;
            const uint pixelDataOffset = 176u;

            if (pixelByteCountLong <= 0 || pixelDataOffset + pixelByteCountLong > uint.MaxValue)
            {
                throw new InvalidOperationException($"Image is too large for 32-bit TIFF format ({width}x{height}).");
            }

            uint pixelByteCount = (uint)pixelByteCountLong;

            Span<byte> headerAndIfd = stackalloc byte[(int)pixelDataOffset];
            headerAndIfd.Clear();

            // Little-Endian TIFF Header ('I','I', 42, IFD offset = 8)
            headerAndIfd[0] = (byte)'I';
            headerAndIfd[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(headerAndIfd.Slice(2, 2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(headerAndIfd.Slice(4, 4), ifdOffset);

            // IFD entry count
            BinaryPrimitives.WriteUInt16LittleEndian(headerAndIfd.Slice(8, 2), entryCount);

            int cursor = 10;
            WriteTiffIfdEntryLong(headerAndIfd.Slice(cursor, 12), 256, 1, (uint)width); cursor += 12;          // ImageWidth
            WriteTiffIfdEntryLong(headerAndIfd.Slice(cursor, 12), 257, 1, (uint)height); cursor += 12;         // ImageLength
            WriteTiffIfdEntryOffset(headerAndIfd.Slice(cursor, 12), 258, 3, 4, bitsPerSampleOffset); cursor += 12; // BitsPerSample [32,32,32,32]
            WriteTiffIfdEntryShort(headerAndIfd.Slice(cursor, 12), 259, 1); cursor += 12;                      // Compression = 1 (None)
            WriteTiffIfdEntryShort(headerAndIfd.Slice(cursor, 12), 262, 2); cursor += 12;                      // PhotometricInterpretation = 2 (RGB)
            WriteTiffIfdEntryLong(headerAndIfd.Slice(cursor, 12), 273, 1, pixelDataOffset); cursor += 12;      // StripOffsets
            WriteTiffIfdEntryShort(headerAndIfd.Slice(cursor, 12), 277, 4); cursor += 12;                      // SamplesPerPixel = 4
            WriteTiffIfdEntryLong(headerAndIfd.Slice(cursor, 12), 278, 1, (uint)height); cursor += 12;         // RowsPerStrip
            WriteTiffIfdEntryLong(headerAndIfd.Slice(cursor, 12), 279, 1, pixelByteCount); cursor += 12;       // StripByteCounts
            WriteTiffIfdEntryShort(headerAndIfd.Slice(cursor, 12), 284, 1); cursor += 12;                      // PlanarConfiguration = 1 (Chunky)
            WriteTiffIfdEntryShort(headerAndIfd.Slice(cursor, 12), 338, 2); cursor += 12;                      // ExtraSamples = 2 (Unassociated alpha)
            WriteTiffIfdEntryOffset(headerAndIfd.Slice(cursor, 12), 339, 3, 4, sampleFormatOffset); cursor += 12; // SampleFormat [3,3,3,3] (IEEE float)

            // Next IFD offset = 0 (at cursor == 154)
            BinaryPrimitives.WriteUInt32LittleEndian(headerAndIfd.Slice(cursor, 4), 0u);

            // Out-of-line BitsPerSample = [32, 32, 32, 32]
            for (int c = 0; c < 4; c++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(headerAndIfd.Slice((int)bitsPerSampleOffset + c * 2, 2), 32);
            }

            // Out-of-line SampleFormat = [3, 3, 3, 3] (IEEE floating point)
            for (int c = 0; c < 4; c++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(headerAndIfd.Slice((int)sampleFormatOffset + c * 2, 2), 3);
            }

            stream.Write(headerAndIfd);

            // Uncompressed IEEE-754 float32 RGBA pixel strip (row-by-row to avoid huge temp allocations)
            var rowBuffer = new byte[checked(width * 16)];
            for (int y = 0; y < height; y++)
            {
                int rowFloatBase = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int fIdx = rowFloatBase + x * 4;
                    int bIdx = x * 16;
                    BinaryPrimitives.WriteSingleLittleEndian(rowBuffer.AsSpan(bIdx, 4), rgbaFloats[fIdx]);
                    BinaryPrimitives.WriteSingleLittleEndian(rowBuffer.AsSpan(bIdx + 4, 4), rgbaFloats[fIdx + 1]);
                    BinaryPrimitives.WriteSingleLittleEndian(rowBuffer.AsSpan(bIdx + 8, 4), rgbaFloats[fIdx + 2]);
                    BinaryPrimitives.WriteSingleLittleEndian(rowBuffer.AsSpan(bIdx + 12, 4), rgbaFloats[fIdx + 3]);
                }
                stream.Write(rowBuffer, 0, rowBuffer.Length);
            }
        }

        public static HdrExportResult ExportTextureToExr(
            Texture2D texture,
            string outputPath,
            ExrPixelFormat pixelFormat = ExrPixelFormat.HalfFloat,
            bool flipVertical = true,
            int mipLevel = 0)
        {
            ArgumentNullException.ThrowIfNull(texture);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return new HdrExportResult { Success = false, ErrorMessage = "Output path cannot be empty." };
            }

            if (!TryDecodeLinearRgbaFloat(texture, out var rgbaFloats, out int width, out int height, out var metadata, flipVertical, mipLevel))
            {
                return new HdrExportResult
                {
                    Success = false,
                    OutputPath = outputPath,
                    ErrorMessage = $"Failed to decode texture '{texture.m_Name}' ({texture.m_TextureFormat}, mip {mipLevel}) to linear RGBA float."
                };
            }

            try
            {
                string fullPath = Path.GetFullPath(outputPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                WriteExr(fs, rgbaFloats, width, height, pixelFormat);

                return new HdrExportResult
                {
                    Success = true,
                    OutputPath = fullPath,
                    Metadata = metadata
                };
            }
            catch (Exception ex)
            {
                return new HdrExportResult
                {
                    Success = false,
                    OutputPath = outputPath,
                    Metadata = metadata,
                    ErrorMessage = ex.Message
                };
            }
        }

        public static HdrExportResult ExportTextureToFloat32Tiff(
            Texture2D texture,
            string outputPath,
            bool flipVertical = true,
            int mipLevel = 0)
        {
            ArgumentNullException.ThrowIfNull(texture);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return new HdrExportResult { Success = false, ErrorMessage = "Output path cannot be empty." };
            }

            if (!TryDecodeLinearRgbaFloat(texture, out var rgbaFloats, out int width, out int height, out var metadata, flipVertical, mipLevel))
            {
                return new HdrExportResult
                {
                    Success = false,
                    OutputPath = outputPath,
                    ErrorMessage = $"Failed to decode texture '{texture.m_Name}' ({texture.m_TextureFormat}, mip {mipLevel}) to linear RGBA float."
                };
            }

            try
            {
                string fullPath = Path.GetFullPath(outputPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                WriteFloat32Tiff(fs, rgbaFloats, width, height);

                return new HdrExportResult
                {
                    Success = true,
                    OutputPath = fullPath,
                    Metadata = metadata
                };
            }
            catch (Exception ex)
            {
                return new HdrExportResult
                {
                    Success = false,
                    OutputPath = outputPath,
                    Metadata = metadata,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static bool DecodeRgbaFloat(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 16L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 16;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 16;
                    int d = dstRow + x * 4;
                    dst[d] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s, 4));
                    dst[d + 1] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 4, 4));
                    dst[d + 2] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 8, 4));
                    dst[d + 3] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 12, 4));
                }
            }
            return true;
        }

        private static bool DecodeArgbFloat(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 16L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 16;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 16;
                    int d = dstRow + x * 4;
                    dst[d + 3] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s, 4));
                    dst[d] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 4, 4));
                    dst[d + 1] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 8, 4));
                    dst[d + 2] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 12, 4));
                }
            }
            return true;
        }

        private static bool DecodeRgbFloat(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 12L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 12;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 12;
                    int d = dstRow + x * 4;
                    dst[d] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s, 4));
                    dst[d + 1] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 4, 4));
                    dst[d + 2] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 8, 4));
                    dst[d + 3] = 1.0f;
                }
            }
            return true;
        }

        private static bool DecodeRgFloat(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 8L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 8;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 8;
                    int d = dstRow + x * 4;
                    dst[d] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s, 4));
                    dst[d + 1] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s + 4, 4));
                    dst[d + 2] = 0.0f;
                    dst[d + 3] = 1.0f;
                }
            }
            return true;
        }

        private static bool DecodeRFloat(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 4L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 4;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    int d = dstRow + x * 4;
                    dst[d] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(s, 4));
                    dst[d + 1] = 0.0f;
                    dst[d + 2] = 0.0f;
                    dst[d + 3] = 1.0f;
                }
            }
            return true;
        }

        private static bool DecodeRgbaHalf(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 8L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 8;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 8;
                    int d = dstRow + x * 4;
                    dst[d] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s, 2));
                    dst[d + 1] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s + 2, 2));
                    dst[d + 2] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s + 4, 2));
                    dst[d + 3] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s + 6, 2));
                }
            }
            return true;
        }

        private static bool DecodeRgHalf(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 4L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 4;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    int d = dstRow + x * 4;
                    dst[d] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s, 2));
                    dst[d + 1] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s + 2, 2));
                    dst[d + 2] = 0.0f;
                    dst[d + 3] = 1.0f;
                }
            }
            return true;
        }

        private static bool DecodeRHalf(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 2L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 2;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 2;
                    int d = dstRow + x * 4;
                    dst[d] = (float)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(s, 2));
                    dst[d + 1] = 0.0f;
                    dst[d + 2] = 0.0f;
                    dst[d + 3] = 1.0f;
                }
            }
            return true;
        }

        private static bool DecodeRgb9e5Float(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            long required = (long)width * height * 4L;
            if (src.Length < required) return false;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 4;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(srcRow + x * 4, 4));
                    int expShared = (int)((n >> 27) & 0x1Fu);
                    float scale = MathF.ScaleB(1.0f, expShared - 24);
                    int d = dstRow + x * 4;
                    dst[d] = (n & 0x1FFu) * scale;
                    dst[d + 1] = ((n >> 9) & 0x1FFu) * scale;
                    dst[d + 2] = ((n >> 18) & 0x1FFu) * scale;
                    dst[d + 3] = 1.0f;
                }
            }
            return true;
        }

        private static bool DecodeViaConverterFallback(
            Texture2D texture,
            byte[] rawBytes,
            Span<float> dst,
            int width,
            int height,
            bool flipVertical)
        {
            long bgraBytesLong = (long)width * height * 4L;
            if (bgraBytesLong <= 0 || bgraBytesLong > int.MaxValue)
            {
                return false;
            }

            var reader = new ResourceReader(rawBytes);
            var converter = new Texture2DConverter(
                reader,
                width,
                height,
                texture.m_TextureFormat,
                texture.version ?? Array.Empty<int>(),
                texture.platform);

            if (!converter.CanDecode())
            {
                return false;
            }

            var bgra = new byte[(int)bgraBytesLong];
            if (!converter.DecodeTexture2D(bgra))
            {
                return false;
            }

            bool isSrgb = texture.m_ColorSpace == 1 && !IsHdrFormat(texture.m_TextureFormat);
            const float inv255 = 1.0f / 255.0f;

            for (int y = 0; y < height; y++)
            {
                int dstY = flipVertical ? (height - 1 - y) : y;
                int srcRow = y * width * 4;
                int dstRow = dstY * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    int d = dstRow + x * 4;
                    float b = bgra[s] * inv255;
                    float g = bgra[s + 1] * inv255;
                    float r = bgra[s + 2] * inv255;
                    float a = bgra[s + 3] * inv255;

                    if (isSrgb)
                    {
                        r = SrgbToLinear(r);
                        g = SrgbToLinear(g);
                        b = SrgbToLinear(b);
                    }

                    dst[d] = r;
                    dst[d + 1] = g;
                    dst[d + 2] = b;
                    dst[d + 3] = a;
                }
            }

            return true;
        }

        private static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static void ComputeRangeMetadata(
            ReadOnlySpan<float> rgba,
            TextureFormat format,
            int mipLevel,
            int width,
            int height,
            bool preservedTrueFloatRange,
            out HdrDecodeMetadata metadata)
        {
            float minVal = float.PositiveInfinity;
            float maxVal = float.NegativeInfinity;
            float maxLum = float.NegativeInfinity;

            for (int i = 0; i < rgba.Length; i += 4)
            {
                float r = rgba[i];
                float g = rgba[i + 1];
                float b = rgba[i + 2];

                if (float.IsFinite(r))
                {
                    if (r < minVal) minVal = r;
                    if (r > maxVal) maxVal = r;
                }
                if (float.IsFinite(g))
                {
                    if (g < minVal) minVal = g;
                    if (g > maxVal) maxVal = g;
                }
                if (float.IsFinite(b))
                {
                    if (b < minVal) minVal = b;
                    if (b > maxVal) maxVal = b;
                }

                if (float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b))
                {
                    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (lum > maxLum) maxLum = lum;
                }
            }

            if (!float.IsFinite(minVal)) minVal = 0f;
            if (!float.IsFinite(maxVal)) maxVal = 0f;
            if (!float.IsFinite(maxLum)) maxLum = 0f;

            metadata = new HdrDecodeMetadata(
                SourceFormat: format,
                MipLevel: mipLevel,
                Width: width,
                Height: height,
                PreservedTrueFloatRange: preservedTrueFloatRange,
                MinValue: minVal,
                MaxValue: maxVal,
                MaxLuminance: maxLum);
        }

        #region Managed BC6H UF16 True-Float Decoder

        private readonly struct Bc6hModeInfo
        {
            public readonly byte Transformed;
            public readonly byte PartitionBits;
            public readonly byte EndpointBits;
            public readonly byte DeltaR;
            public readonly byte DeltaG;
            public readonly byte DeltaB;

            public Bc6hModeInfo(byte transformed, byte partitionBits, byte endpointBits, byte deltaR, byte deltaG, byte deltaB)
            {
                Transformed = transformed;
                PartitionBits = partitionBits;
                EndpointBits = endpointBits;
                DeltaR = deltaR;
                DeltaG = deltaG;
                DeltaB = deltaB;
            }
        }

        private static readonly ushort[] Bc6hPartition2 =
        {
            0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80,
            0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
            0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce,
            0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c
        };

        private static readonly byte[] Bc6hAnchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15,
            15,  2,  8,  2,  2,  8,  8, 15,
             2,  8,  2,  2,  8,  8,  2,  2
        };

        private static readonly byte[] Bc6hWeights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly byte[] Bc6hWeights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        private static readonly Bc6hModeInfo[] Bc6hModes =
        {
            new(1, 5, 10,  5,  5,  5), // 0
            new(1, 5,  7,  6,  6,  6), // 1
            new(1, 5, 11,  5,  4,  4), // 2
            new(0, 0, 10, 10, 10, 10), // 3
            new(0, 0,  0,  0,  0,  0), // 4
            new(0, 0,  0,  0,  0,  0), // 5
            new(1, 5, 11,  4,  5,  4), // 6
            new(1, 0, 11,  9,  9,  9), // 7
            new(0, 0,  0,  0,  0,  0), // 8
            new(0, 0,  0,  0,  0,  0), // 9
            new(1, 5, 11,  4,  4,  5), // 10
            new(1, 0, 12,  8,  8,  8), // 11
            new(0, 0,  0,  0,  0,  0), // 12
            new(0, 0,  0,  0,  0,  0), // 13
            new(1, 5,  9,  5,  5,  5), // 14
            new(1, 0, 16,  4,  4,  4), // 15
            new(0, 0,  0,  0,  0,  0), // 16
            new(0, 0,  0,  0,  0,  0), // 17
            new(1, 5,  8,  6,  5,  5), // 18
            new(0, 0,  0,  0,  0,  0), // 19
            new(0, 0,  0,  0,  0,  0), // 20
            new(0, 0,  0,  0,  0,  0), // 21
            new(1, 5,  8,  5,  6,  5), // 22
            new(0, 0,  0,  0,  0,  0), // 23
            new(0, 0,  0,  0,  0,  0), // 24
            new(0, 0,  0,  0,  0,  0), // 25
            new(1, 5,  8,  5,  5,  6), // 26
            new(0, 0,  0,  0,  0,  0), // 27
            new(0, 0,  0,  0,  0,  0), // 28
            new(0, 0,  0,  0,  0,  0), // 29
            new(0, 5,  6,  6,  6,  6), // 30
            new(0, 0,  0,  0,  0,  0)  // 31
        };

        private ref struct Bc6hBitReader
        {
            private readonly ulong _lo;
            private readonly ulong _hi;
            private int _bitPos;

            public Bc6hBitReader(ReadOnlySpan<byte> block)
            {
                _lo = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(0, 8));
                _hi = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8, 8));
                _bitPos = 0;
            }

            public ushort Read(int numBits)
            {
                if (numBits <= 0 || _bitPos >= 128)
                {
                    return 0;
                }

                ulong mask = (1UL << numBits) - 1UL;
                ulong value;
                if (_bitPos >= 64)
                {
                    value = (_hi >> (_bitPos - 64)) & mask;
                }
                else if (_bitPos + numBits <= 64)
                {
                    value = (_lo >> _bitPos) & mask;
                }
                else
                {
                    value = ((_lo >> _bitPos) | (_hi << (64 - _bitPos))) & mask;
                }

                _bitPos += numBits;
                return (ushort)value;
            }
        }

        private static bool DecodeBc6hUf16(ReadOnlySpan<byte> src, Span<float> dst, int width, int height, bool flipVertical)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            long required = (long)blocksX * blocksY * 16L;
            if (src.Length < required)
            {
                return false;
            }

            Span<float> blockRgba = stackalloc float[16 * 4];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int blockByteOffset = (by * blocksX + bx) * 16;
                    DecodeBc6hUf16Block(src.Slice(blockByteOffset, 16), blockRgba);

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height)
                        {
                            break;
                        }

                        int dstY = flipVertical ? (height - 1 - y) : y;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width)
                            {
                                break;
                            }

                            int s = (py * 4 + px) * 4;
                            int d = (dstY * width + x) * 4;
                            dst[d] = blockRgba[s];
                            dst[d + 1] = blockRgba[s + 1];
                            dst[d + 2] = blockRgba[s + 2];
                            dst[d + 3] = 1.0f;
                        }
                    }
                }
            }

            return true;
        }

        private static void DecodeBc6hUf16Block(ReadOnlySpan<byte> block, Span<float> dstRgba)
        {
            var bit = new Bc6hBitReader(block);
            int mode = bit.Read(2);

            Span<int> epR = stackalloc int[4];
            Span<int> epG = stackalloc int[4];
            Span<int> epB = stackalloc int[4];
            epR.Clear();
            epG.Clear();
            epB.Clear();

            if ((mode & 2) != 0)
            {
                mode |= bit.Read(3) << 2;
                if (Bc6hModes[mode].EndpointBits == 0)
                {
                    dstRgba.Clear();
                    for (int i = 3; i < 64; i += 4) dstRgba[i] = 1.0f;
                    return;
                }

                switch (mode)
                {
                    case 2:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(5); epR[0] |= bit.Read(1) << 10;
                        epG[2] |= bit.Read(4); epG[1] |= bit.Read(4); epG[0] |= bit.Read(1) << 10;
                        epB[3] |= bit.Read(1); epG[3] |= bit.Read(4); epB[1] |= bit.Read(4);
                        epB[0] |= bit.Read(1) << 10; epB[3] |= bit.Read(1) << 1;
                        epB[2] |= bit.Read(4); epR[2] |= bit.Read(5); epB[3] |= bit.Read(1) << 2;
                        epR[3] |= bit.Read(5); epB[3] |= bit.Read(1) << 3;
                        break;
                    case 3:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(10); epG[1] |= bit.Read(10); epB[1] |= bit.Read(10);
                        break;
                    case 6:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(4); epR[0] |= bit.Read(1) << 10; epG[3] |= bit.Read(1) << 4;
                        epG[2] |= bit.Read(4); epG[1] |= bit.Read(5); epG[0] |= bit.Read(1) << 10;
                        epG[3] |= bit.Read(4); epB[1] |= bit.Read(4); epB[0] |= bit.Read(1) << 10;
                        epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(4); epR[2] |= bit.Read(4);
                        epB[3] |= bit.Read(1); epB[3] |= bit.Read(1) << 2; epR[3] |= bit.Read(4);
                        epG[2] |= bit.Read(1) << 4; epB[3] |= bit.Read(1) << 3;
                        break;
                    case 7:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(9); epR[0] |= bit.Read(1) << 10;
                        epG[1] |= bit.Read(9); epG[0] |= bit.Read(1) << 10;
                        epB[1] |= bit.Read(9); epB[0] |= bit.Read(1) << 10;
                        break;
                    case 10:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(4); epR[0] |= bit.Read(1) << 10; epB[2] |= bit.Read(1) << 4;
                        epG[2] |= bit.Read(4); epG[1] |= bit.Read(4); epG[0] |= bit.Read(1) << 10;
                        epB[3] |= bit.Read(1); epG[3] |= bit.Read(4); epB[1] |= bit.Read(5);
                        epB[0] |= bit.Read(1) << 10; epB[2] |= bit.Read(4); epR[2] |= bit.Read(4);
                        epB[3] |= bit.Read(1) << 1; epB[3] |= bit.Read(1) << 2; epR[3] |= bit.Read(4);
                        epB[3] |= bit.Read(1) << 4; epB[3] |= bit.Read(1) << 3;
                        break;
                    case 11:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(8); epR[0] |= bit.Read(1) << 11; epR[0] |= bit.Read(1) << 10;
                        epG[1] |= bit.Read(8); epG[0] |= bit.Read(1) << 11; epG[0] |= bit.Read(1) << 10;
                        epB[1] |= bit.Read(8); epB[0] |= bit.Read(1) << 11; epB[0] |= bit.Read(1) << 10;
                        break;
                    case 14:
                        epR[0] |= bit.Read(9); epB[2] |= bit.Read(1) << 4;
                        epG[0] |= bit.Read(9); epG[2] |= bit.Read(1) << 4;
                        epB[0] |= bit.Read(9); epB[3] |= bit.Read(1) << 4;
                        epR[1] |= bit.Read(5); epG[3] |= bit.Read(1) << 4; epG[2] |= bit.Read(4);
                        epG[1] |= bit.Read(5); epB[3] |= bit.Read(1); epG[3] |= bit.Read(4);
                        epB[1] |= bit.Read(5); epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(4);
                        epR[2] |= bit.Read(5); epB[3] |= bit.Read(1) << 2;
                        epR[3] |= bit.Read(5); epB[3] |= bit.Read(1) << 3;
                        break;
                    case 15:
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(4);
                        epR[0] |= bit.Read(1) << 15; epR[0] |= bit.Read(1) << 14; epR[0] |= bit.Read(1) << 13;
                        epR[0] |= bit.Read(1) << 12; epR[0] |= bit.Read(1) << 11; epR[0] |= bit.Read(1) << 10;
                        epG[1] |= bit.Read(4);
                        epG[0] |= bit.Read(1) << 15; epG[0] |= bit.Read(1) << 14; epG[0] |= bit.Read(1) << 13;
                        epG[0] |= bit.Read(1) << 12; epG[0] |= bit.Read(1) << 11; epG[0] |= bit.Read(1) << 10;
                        epB[1] |= bit.Read(4);
                        epB[0] |= bit.Read(1) << 15; epB[0] |= bit.Read(1) << 14; epB[0] |= bit.Read(1) << 13;
                        epB[0] |= bit.Read(1) << 12; epB[0] |= bit.Read(1) << 11; epB[0] |= bit.Read(1) << 10;
                        break;
                    case 18:
                        epR[0] |= bit.Read(8); epG[3] |= bit.Read(1) << 4; epB[2] |= bit.Read(1) << 4;
                        epG[0] |= bit.Read(8); epB[3] |= bit.Read(1) << 2; epG[2] |= bit.Read(1) << 4;
                        epB[0] |= bit.Read(8); epB[3] |= bit.Read(1) << 3; epB[3] |= bit.Read(1) << 4;
                        epR[1] |= bit.Read(6); epG[2] |= bit.Read(4); epG[1] |= bit.Read(5);
                        epB[3] |= bit.Read(1); epG[3] |= bit.Read(4); epB[1] |= bit.Read(5);
                        epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(4);
                        epR[2] |= bit.Read(6); epR[3] |= bit.Read(6);
                        break;
                    case 22:
                        epR[0] |= bit.Read(8); epB[3] |= bit.Read(1); epB[2] |= bit.Read(1) << 4;
                        epG[0] |= bit.Read(8); epG[2] |= bit.Read(1) << 5; epG[2] |= bit.Read(1) << 4;
                        epB[0] |= bit.Read(8); epG[3] |= bit.Read(1) << 5; epB[3] |= bit.Read(1) << 4;
                        epR[1] |= bit.Read(5); epG[3] |= bit.Read(1) << 4; epG[2] |= bit.Read(4);
                        epG[1] |= bit.Read(6); epG[3] |= bit.Read(4); epB[1] |= bit.Read(5);
                        epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(4); epR[2] |= bit.Read(5);
                        epB[3] |= bit.Read(1) << 2; epR[3] |= bit.Read(5); epB[3] |= bit.Read(1) << 3;
                        break;
                    case 26:
                        epR[0] |= bit.Read(8); epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(1) << 4;
                        epG[0] |= bit.Read(8); epB[2] |= bit.Read(1) << 5; epG[2] |= bit.Read(1) << 4;
                        epB[0] |= bit.Read(8); epB[3] |= bit.Read(1) << 5; epB[3] |= bit.Read(1) << 4;
                        epR[1] |= bit.Read(5); epG[3] |= bit.Read(1) << 4; epG[2] |= bit.Read(4);
                        epG[1] |= bit.Read(5); epB[3] |= bit.Read(1); epG[3] |= bit.Read(4);
                        epB[1] |= bit.Read(6); epB[2] |= bit.Read(4); epR[2] |= bit.Read(5);
                        epB[3] |= bit.Read(1) << 2; epR[3] |= bit.Read(5); epB[3] |= bit.Read(1) << 3;
                        break;
                    case 30:
                        epR[0] |= bit.Read(6); epG[3] |= bit.Read(1) << 4; epB[3] |= bit.Read(1);
                        epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(1) << 4;
                        epG[0] |= bit.Read(6); epG[2] |= bit.Read(1) << 5; epB[2] |= bit.Read(1) << 5;
                        epB[3] |= bit.Read(1) << 2; epG[2] |= bit.Read(1) << 4;
                        epB[0] |= bit.Read(6); epG[3] |= bit.Read(1) << 5; epB[3] |= bit.Read(1) << 3;
                        epB[3] |= bit.Read(1) << 5; epB[3] |= bit.Read(1) << 4;
                        epR[1] |= bit.Read(6); epG[2] |= bit.Read(4); epG[1] |= bit.Read(6);
                        epG[3] |= bit.Read(4); epB[1] |= bit.Read(6); epB[2] |= bit.Read(4);
                        epR[2] |= bit.Read(6); epR[3] |= bit.Read(6);
                        break;
                }
            }
            else
            {
                switch (mode)
                {
                    case 0:
                        epG[2] |= bit.Read(1) << 4; epB[2] |= bit.Read(1) << 4; epB[3] |= bit.Read(1) << 4;
                        epR[0] |= bit.Read(10); epG[0] |= bit.Read(10); epB[0] |= bit.Read(10);
                        epR[1] |= bit.Read(5); epG[3] |= bit.Read(1) << 4; epG[2] |= bit.Read(4);
                        epG[1] |= bit.Read(5); epB[3] |= bit.Read(1); epG[3] |= bit.Read(4);
                        epB[1] |= bit.Read(5); epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(4);
                        epR[2] |= bit.Read(5); epB[3] |= bit.Read(1) << 2;
                        epR[3] |= bit.Read(5); epB[3] |= bit.Read(1) << 3;
                        break;
                    case 1:
                        epG[2] |= bit.Read(1) << 5; epG[3] |= bit.Read(1) << 4; epG[3] |= bit.Read(1) << 5;
                        epR[0] |= bit.Read(7); epB[3] |= bit.Read(1); epB[3] |= bit.Read(1) << 1; epB[2] |= bit.Read(1) << 4;
                        epG[0] |= bit.Read(7); epB[2] |= bit.Read(1) << 5; epB[3] |= bit.Read(1) << 2; epG[2] |= bit.Read(1) << 4;
                        epB[0] |= bit.Read(7); epB[3] |= bit.Read(1) << 3; epB[3] |= bit.Read(1) << 5; epB[3] |= bit.Read(1) << 4;
                        epR[1] |= bit.Read(6); epG[2] |= bit.Read(4); epG[1] |= bit.Read(6); epG[3] |= bit.Read(4);
                        epB[1] |= bit.Read(6); epB[2] |= bit.Read(4); epR[2] |= bit.Read(6); epR[3] |= bit.Read(6);
                        break;
                }
            }

            var mi = Bc6hModes[mode];
            int numSubsets = mi.PartitionBits != 0 ? 2 : 1;
            int epCount = numSubsets * 2;
            int epMask = (1 << mi.EndpointBits) - 1;

            if (mi.Transformed != 0)
            {
                for (int i = 1; i < epCount; i++)
                {
                    epR[i] = (epR[0] + SignExtend(epR[i], mi.DeltaR)) & epMask;
                    epG[i] = (epG[0] + SignExtend(epG[i], mi.DeltaG)) & epMask;
                    epB[i] = (epB[0] + SignExtend(epB[i], mi.DeltaB)) & epMask;
                }
            }

            for (int i = 0; i < epCount; i++)
            {
                epR[i] = UnquantizeUf16(epR[i], mi.EndpointBits);
                epG[i] = UnquantizeUf16(epG[i], mi.EndpointBits);
                epB[i] = UnquantizeUf16(epB[i], mi.EndpointBits);
            }

            int partitionIdx = mi.PartitionBits != 0 ? bit.Read(5) : 0;
            int indexBits = mi.PartitionBits != 0 ? 3 : 4;
            byte[] weights = mi.PartitionBits != 0 ? Bc6hWeights3 : Bc6hWeights4;
            ushort p2Mask = Bc6hPartition2[partitionIdx];
            byte a2Idx = Bc6hAnchor2[partitionIdx];

            for (int idx = 0; idx < 16; idx++)
            {
                int subset = mi.PartitionBits != 0 ? ((p2Mask >> idx) & 1) : 0;
                int anchor = subset == 0 ? 0 : a2Idx;
                int bitsToRead = indexBits - (idx == anchor ? 1 : 0);
                int weightIdx = bit.Read(bitsToRead);
                int w = weights[weightIdx];
                int invW = 64 - w;

                int e0 = subset * 2;
                int e1 = e0 + 1;

                int interpR = (epR[e0] * invW + epR[e1] * w + 32) >> 6;
                int interpG = (epG[e0] * invW + epG[e1] * w + 32) >> 6;
                int interpB = (epB[e0] * invW + epB[e1] * w + 32) >> 6;

                ushort halfR = (ushort)((interpR * 31) >> 6);
                ushort halfG = (ushort)((interpG * 31) >> 6);
                ushort halfB = (ushort)((interpB * 31) >> 6);

                int dst = idx * 4;
                dstRgba[dst] = (float)BitConverter.UInt16BitsToHalf(halfR);
                dstRgba[dst + 1] = (float)BitConverter.UInt16BitsToHalf(halfG);
                dstRgba[dst + 2] = (float)BitConverter.UInt16BitsToHalf(halfB);
                dstRgba[dst + 3] = 1.0f;
            }
        }

        private static int SignExtend(int value, int numBits)
        {
            int signBit = 1 << (numBits - 1);
            return (value ^ signBit) - signBit;
        }

        private static int UnquantizeUf16(int value, int endpointBits)
        {
            if (endpointBits >= 15)
            {
                return value;
            }
            if (value == 0)
            {
                return 0;
            }
            if (value == ((1 << endpointBits) - 1))
            {
                return 0xFFFF;
            }
            return ((value << 16) + 0x8000) >> endpointBits;
        }

        #endregion

        #region OpenEXR & TIFF Binary Helpers

        private static void WriteChannelsAttribute(Stream stream, int pixelType)
        {
            // Strict alphabetical order required by OpenEXR specification: A, B, G, R
            ReadOnlySpan<byte> channelChars = stackalloc byte[] { (byte)'A', (byte)'B', (byte)'G', (byte)'R' };
            int valueSize = channelChars.Length * 18 + 1; // 4 * (2 + 4 + 1 + 3 + 4 + 4) + 1 = 73 bytes
            WriteAttributeHeader(stream, "channels", "chlist", valueSize);

            Span<byte> entry = stackalloc byte[18];
            for (int i = 0; i < channelChars.Length; i++)
            {
                entry.Clear();
                entry[0] = channelChars[i];
                entry[1] = 0; // NUL terminator
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(2, 4), pixelType);
                entry[6] = 0; // pLinear
                // entry[7..9] reserved = 0
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(10, 4), 1); // xSampling
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(14, 4), 1); // ySampling
                stream.Write(entry);
            }

            stream.WriteByte(0); // End of chlist
        }

        private static void WriteBox2iAttribute(Stream stream, string name, int xMin, int yMin, int xMax, int yMax)
        {
            WriteAttributeHeader(stream, name, "box2i", 16);
            Span<byte> box = stackalloc byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(box.Slice(0, 4), xMin);
            BinaryPrimitives.WriteInt32LittleEndian(box.Slice(4, 4), yMin);
            BinaryPrimitives.WriteInt32LittleEndian(box.Slice(8, 4), xMax);
            BinaryPrimitives.WriteInt32LittleEndian(box.Slice(12, 4), yMax);
            stream.Write(box);
        }

        private static void WriteAttributeHeader(Stream stream, string name, string type, int size)
        {
            WriteNullTerminatedAscii(stream, name);
            WriteNullTerminatedAscii(stream, type);
            WriteInt32LE(stream, size);
        }

        private static void WriteNullTerminatedAscii(Stream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static bool TryReadNullTerminatedAscii(byte[] data, ref int pos, out string value)
        {
            int start = pos;
            while (pos < data.Length && data[pos] != 0)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                value = string.Empty;
                return false;
            }

            value = Encoding.ASCII.GetString(data, start, pos - start);
            pos++; // consume NUL
            return true;
        }

        private static void WriteUInt32LE(Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt32LE(Stream stream, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteSingleLE(Stream stream, float value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteTiffIfdEntryShort(Span<byte> entry, ushort tag, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(0, 2), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), 3); // SHORT
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(4, 4), 1u);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(8, 2), value);
            entry[10] = 0;
            entry[11] = 0;
        }

        private static void WriteTiffIfdEntryLong(Span<byte> entry, ushort tag, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(0, 2), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), 4); // LONG
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(4, 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8, 4), value);
        }

        private static void WriteTiffIfdEntryOffset(Span<byte> entry, ushort tag, ushort fieldType, uint count, uint offset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(0, 2), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), fieldType);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(4, 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8, 4), offset);
        }

        #endregion
    }
}
