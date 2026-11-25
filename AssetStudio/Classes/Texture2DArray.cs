using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public sealed class Texture2DArray : Texture
    {
        public int m_ColorSpace;
        public int m_Format;
        public TextureFormat m_TextureFormat;
        public int m_Width;
        public int m_Height;
        public int m_Depth;
        public int m_MipCount;
        public uint m_DataSize;
        public GLTextureSettings m_TextureSettings;
        public int m_UsageMode;
        public bool m_IsReadable;
        public bool m_IgnoreMipmapLimit;
        public string m_MipmapLimitGroupName = string.Empty;
        public ResourceReader image_data;
        public StreamingInfo? m_StreamData;
        public bool IsFormatSupported { get; private set; }
        public bool IsTruncated { get; private set; }

        public Texture2DArray(ObjectReader reader) : base(reader)
        {
            var position = reader.Position;
            if (TryReadFromTypeTree())
            {
                return;
            }
            reader.Position = position;

            m_TextureSettings = new GLTextureSettings();
            image_data = new ResourceReader(Array.Empty<byte>());

            long objectEnd = Math.Min(reader.byteStart + reader.byteSize, reader.BaseStream.Length);
            if (reader.Position + 28 > objectEnd)
            {
                IsTruncated = true;
                Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): truncated header.");
                return;
            }

            try
            {
                m_ColorSpace = reader.ReadInt32();
                m_Format = reader.ReadInt32();
                m_Width = reader.ReadInt32();
                m_Height = reader.ReadInt32();
                m_Depth = reader.ReadInt32();
                m_MipCount = reader.ReadInt32();
                m_DataSize = reader.ReadUInt32();

                ResolveTextureFormat();

                if (reader.Position + 16 > objectEnd)
                {
                    IsTruncated = true;
                    Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): truncated texture settings.");
                    return;
                }
                m_TextureSettings = new GLTextureSettings(reader);

                if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) // 2020.2 and up
                {
                    if (reader.Position + 4 > objectEnd)
                    {
                        IsTruncated = true;
                        Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): truncated m_UsageMode.");
                        return;
                    }
                    m_UsageMode = reader.ReadInt32();
                }

                if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2)) // 2022.2 and up
                {
                    if (reader.Position + 1 > objectEnd)
                    {
                        IsTruncated = true;
                        Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): truncated mipmap limit flags.");
                        return;
                    }
                    m_IgnoreMipmapLimit = reader.ReadBoolean();
                    reader.AlignStream();
                    if (reader.Position + 4 <= objectEnd)
                    {
                        m_MipmapLimitGroupName = reader.ReadAlignedString();
                    }
                }

                if (reader.Position + 1 <= objectEnd)
                {
                    m_IsReadable = reader.ReadBoolean();
                    reader.AlignStream();
                }

                if (reader.Position + 4 > objectEnd)
                {
                    IsTruncated = true;
                    Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): missing image data header.");
                    return;
                }

                int imageDataSize = reader.ReadInt32();
                if (imageDataSize > 0)
                {
                    long remainingInObj = objectEnd - reader.Position;
                    if (imageDataSize <= remainingInObj)
                    {
                        image_data = new ResourceReader(reader, reader.BaseStream.Position, imageDataSize);
                        reader.BaseStream.Position += imageDataSize;
                        reader.AlignStream();
                        remainingInObj = objectEnd - reader.Position;
                        if (HasStreamData(version) && remainingInObj >= 12)
                        {
                            m_StreamData = new StreamingInfo(reader);
                        }
                    }
                    else
                    {
                        IsTruncated = true;
                        long available = Math.Max(0L, remainingInObj);
                        image_data = new ResourceReader(reader, reader.BaseStream.Position, available);
                        Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): declared image data size {imageDataSize} exceeds remaining object bytes {remainingInObj}.");
                    }
                }
                else if (imageDataSize == 0 && HasStreamData(version) && reader.Position + 12 <= objectEnd)
                {
                    m_StreamData = new StreamingInfo(reader);
                    if (!string.IsNullOrEmpty(m_StreamData.path) && (m_StreamData.path.Length > 260 || m_StreamData.path.Any(char.IsControl)))
                    {
                        IsTruncated = true;
                        m_StreamData = null;
                        Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): invalid StreamingInfo path.");
                        return;
                    }

                    if (!string.IsNullOrEmpty(m_StreamData.path) && m_StreamData.size > 0)
                    {
                        image_data = new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size);
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"Texture2DArray '{m_Name}' (PathID {m_PathID}): failed binary read safely ({ex.Message}).");
            }
        }

        private static bool HasStreamData(int[] version)
        {
            return version.Length >= 2 && ((version[0] == 5 && version[1] >= 3) || version[0] > 5);
        }

        private void ResolveTextureFormat()
        {
            if (version.Length > 0 && version[0] < 2019)
            {
                IsFormatSupported = Enum.IsDefined(typeof(TextureFormat), m_Format);
                m_TextureFormat = IsFormatSupported ? (TextureFormat)m_Format : default;
                return;
            }

            if (TryMapGraphicsFormatToTextureFormat(m_Format, out var mapped))
            {
                m_TextureFormat = mapped;
                IsFormatSupported = true;
            }
            else
            {
                m_TextureFormat = default;
                IsFormatSupported = false;
            }
        }

        public static bool TryMapGraphicsFormatToTextureFormat(int graphicsFormat, out TextureFormat textureFormat)
        {
            switch (graphicsFormat)
            {
                case 1: // R8_SRGB
                case 5: // R8_UNorm
                case 9: // R8_SNorm
                    textureFormat = TextureFormat.R8;
                    return true;
                case 2: // R8G8_SRGB
                case 6: // R8G8_UNorm
                case 10: // R8G8_SNorm
                    textureFormat = TextureFormat.RG16;
                    return true;
                case 3: // R8G8B8_SRGB
                case 7: // R8G8B8_UNorm
                case 11: // R8G8B8_SNorm
                    textureFormat = TextureFormat.RGB24;
                    return true;
                case 4: // R8G8B8A8_SRGB
                case 8: // R8G8B8A8_UNorm
                case 12: // R8G8B8A8_SNorm
                    textureFormat = TextureFormat.RGBA32;
                    return true;
                case 21: // R16_UNorm
                    textureFormat = TextureFormat.R16;
                    return true;
                case 22: // R16G16_UNorm
                    textureFormat = TextureFormat.RG32;
                    return true;
                case 23: // R16G16B16_UNorm
                    textureFormat = TextureFormat.RGB48;
                    return true;
                case 24: // R16G16B16A16_UNorm
                    textureFormat = TextureFormat.RGBA64;
                    return true;
                case 45: // R16_SFloat
                    textureFormat = TextureFormat.RHalf;
                    return true;
                case 46: // R16G16_SFloat
                    textureFormat = TextureFormat.RGHalf;
                    return true;
                case 48: // R16G16B16A16_SFloat
                    textureFormat = TextureFormat.RGBAHalf;
                    return true;
                case 49: // R32_SFloat
                    textureFormat = TextureFormat.RFloat;
                    return true;
                case 50: // R32G32_SFloat
                    textureFormat = TextureFormat.RGFloat;
                    return true;
                case 51: // R32G32B32_SFloat
                    textureFormat = TextureFormat.RGBFloat;
                    return true;
                case 52: // R32G32B32A32_SFloat
                    textureFormat = TextureFormat.RGBAFloat;
                    return true;
                case 58: // B8G8R8A8_SRGB
                case 59: // B8G8R8A8_UNorm
                case 60: // B8G8R8A8_SNorm
                    textureFormat = TextureFormat.BGRA32;
                    return true;
                case 96: // RGBA_DXT1_SRGB
                case 97: // RGBA_DXT1_UNorm
                    textureFormat = TextureFormat.DXT1;
                    return true;
                case 98: // RGBA_DXT3_SRGB
                case 99: // RGBA_DXT3_UNorm
                    textureFormat = TextureFormat.DXT3;
                    return true;
                case 100: // RGBA_DXT5_SRGB
                case 101: // RGBA_DXT5_UNorm
                    textureFormat = TextureFormat.DXT5;
                    return true;
                case 102: // R_BC4_UNorm
                case 103: // R_BC4_SNorm
                    textureFormat = TextureFormat.BC4;
                    return true;
                case 104: // RG_BC5_UNorm
                case 105: // RG_BC5_SNorm
                    textureFormat = TextureFormat.BC5;
                    return true;
                case 106: // RGB_BC6H_UFloat
                case 107: // RGB_BC6H_SFloat
                    textureFormat = TextureFormat.BC6H;
                    return true;
                case 108: // RGBA_BC7_SRGB
                case 109: // RGBA_BC7_UNorm
                    textureFormat = TextureFormat.BC7;
                    return true;
                case 110: // RGB_PVRTC_2Bpp_SRGB
                case 111: // RGB_PVRTC_2Bpp_UNorm
                    textureFormat = TextureFormat.PVRTC_RGB2;
                    return true;
                case 112: // RGB_PVRTC_4Bpp_SRGB
                case 113: // RGB_PVRTC_4Bpp_UNorm
                    textureFormat = TextureFormat.PVRTC_RGB4;
                    return true;
                case 114: // RGBA_PVRTC_2Bpp_SRGB
                case 115: // RGBA_PVRTC_2Bpp_UNorm
                    textureFormat = TextureFormat.PVRTC_RGBA2;
                    return true;
                case 116: // RGBA_PVRTC_4Bpp_SRGB
                case 117: // RGBA_PVRTC_4Bpp_UNorm
                    textureFormat = TextureFormat.PVRTC_RGBA4;
                    return true;
                case 118: // RGB_ETC_UNorm
                    textureFormat = TextureFormat.ETC_RGB4;
                    return true;
                case 119: // RGB_ETC2_SRGB
                case 120: // RGB_ETC2_UNorm
                    textureFormat = TextureFormat.ETC2_RGB;
                    return true;
                case 121: // RGB_A1_ETC2_SRGB
                case 122: // RGB_A1_ETC2_UNorm
                    textureFormat = TextureFormat.ETC2_RGBA1;
                    return true;
                case 123: // RGBA_ETC2_SRGB
                case 124: // RGBA_ETC2_UNorm
                    textureFormat = TextureFormat.ETC2_RGBA8;
                    return true;
                case 125: // R_EAC_UNorm
                    textureFormat = TextureFormat.EAC_R;
                    return true;
                case 126: // R_EAC_SNorm
                    textureFormat = TextureFormat.EAC_R_SIGNED;
                    return true;
                case 127: // RG_EAC_UNorm
                    textureFormat = TextureFormat.EAC_RG;
                    return true;
                case 128: // RG_EAC_SNorm
                    textureFormat = TextureFormat.EAC_RG_SIGNED;
                    return true;
                case 129: // RGBA_ASTC4X4_SRGB
                case 130: // RGBA_ASTC4X4_UNorm
                    textureFormat = TextureFormat.ASTC_RGBA_4x4;
                    return true;
                case 131: // RGBA_ASTC5X5_SRGB
                case 132: // RGBA_ASTC5X5_UNorm
                    textureFormat = TextureFormat.ASTC_RGBA_5x5;
                    return true;
                case 133: // RGBA_ASTC6X6_SRGB
                case 134: // RGBA_ASTC6X6_UNorm
                    textureFormat = TextureFormat.ASTC_RGBA_6x6;
                    return true;
                case 135: // RGBA_ASTC8X8_SRGB
                case 136: // RGBA_ASTC8X8_UNorm
                    textureFormat = TextureFormat.ASTC_RGBA_8x8;
                    return true;
                case 137: // RGBA_ASTC10X10_SRGB
                case 138: // RGBA_ASTC10X10_UNorm
                    textureFormat = TextureFormat.ASTC_RGBA_10x10;
                    return true;
                case 139: // RGBA_ASTC12X12_SRGB
                case 140: // RGBA_ASTC12X12_UNorm
                    textureFormat = TextureFormat.ASTC_RGBA_12x12;
                    return true;
                case 145: // RGBA_ASTC4X4_UFloat
                    textureFormat = TextureFormat.ASTC_HDR_4x4;
                    return true;
                case 146: // RGBA_ASTC5X5_UFloat
                    textureFormat = TextureFormat.ASTC_HDR_5x5;
                    return true;
                case 147: // RGBA_ASTC6X6_UFloat
                    textureFormat = TextureFormat.ASTC_HDR_6x6;
                    return true;
                case 148: // RGBA_ASTC8X8_UFloat
                    textureFormat = TextureFormat.ASTC_HDR_8x8;
                    return true;
                case 149: // RGBA_ASTC10X10_UFloat
                    textureFormat = TextureFormat.ASTC_HDR_10x10;
                    return true;
                case 150: // RGBA_ASTC12X12_UFloat
                    textureFormat = TextureFormat.ASTC_HDR_12x12;
                    return true;
                default:
                    textureFormat = default;
                    return false;
            }
        }

        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(m_TextureSettings), nameof(image_data))]
        private bool TryReadFromTypeTree()
        {
            var typeTree = serializedType?.m_Type;
            if (typeTree == null || typeTree.m_Nodes.Count == 0)
            {
                return false;
            }

            try
            {
                var nodes = typeTree.m_Nodes;
                var nextSibling = TypeTreeHelper.GetNextSiblingTable(nodes);
                reader.Reset();

                int colorSpace = 0;
                int format = 0;
                bool hasFormat = false;
                int width = 0;
                int height = 0;
                int depth = 0;
                int mipCount = 0;
                uint dataSize = 0;
                GLTextureSettings? textureSettings = null;
                int usageMode = 0;
                bool isReadable = false;
                bool ignoreMipmapLimit = false;
                string mipmapLimitGroupName = string.Empty;
                StreamingInfo? streamData = null;
                long imageDataOffset = 0;
                int imageDataSize = 0;
                long imageDataUnderscoreOffset = 0;
                int imageDataUnderscoreSize = 0;

                for (int i = 1; i < nodes.Count; i++)
                {
                    switch (nodes[i].m_Name)
                    {
                        case "m_Name":
                            TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref i, out m_Name);
                            break;
                        case "m_ColorSpace":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out colorSpace))
                                return false;
                            break;
                        case "m_Format":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out format))
                                return false;
                            hasFormat = true;
                            break;
                        case "m_Width":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out width))
                                return false;
                            break;
                        case "m_Height":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out height))
                                return false;
                            break;
                        case "m_Depth":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out depth))
                                return false;
                            break;
                        case "m_MipCount":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out mipCount))
                                return false;
                            break;
                        case "m_DataSize":
                            if (!TypeTreeHelper.TryReadUInt32(nodes, nextSibling, reader, ref i, out dataSize))
                                return false;
                            break;
                        case "m_TextureSettings":
                            textureSettings = ReadTextureSettingsStreaming(nodes, nextSibling, reader, ref i);
                            break;
                        case "m_UsageMode":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out usageMode))
                                return false;
                            break;
                        case "m_IsReadable":
                            if (!TypeTreeHelper.TryReadBoolean(nodes, nextSibling, reader, ref i, out isReadable))
                                return false;
                            break;
                        case "m_IgnoreMipmapLimit":
                            if (!TypeTreeHelper.TryReadBoolean(nodes, nextSibling, reader, ref i, out ignoreMipmapLimit))
                                return false;
                            break;
                        case "m_MipmapLimitGroupName":
                            TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref i, out mipmapLimitGroupName);
                            break;
                        case "m_StreamData":
                            streamData = ReadStreamingInfoStreaming(nodes, nextSibling, reader, ref i);
                            break;
                        case "image data":
                            TypeTreeHelper.TrySkipBlob(nodes, nextSibling, reader, ref i, out imageDataOffset, out imageDataSize);
                            break;
                        case "image_data":
                            TypeTreeHelper.TrySkipBlob(nodes, nextSibling, reader, ref i, out imageDataUnderscoreOffset, out imageDataUnderscoreSize);
                            break;
                        default:
                            TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref i);
                            break;
                    }
                }

                if (width < 0 || height < 0 || depth < 0 || !hasFormat)
                {
                    return false;
                }

                ResourceReader? resourceReader = null;
                if (!string.IsNullOrEmpty(streamData?.path) && streamData.size > 0)
                {
                    resourceReader = new ResourceReader(streamData.path, assetsFile, streamData.offset, streamData.size);
                }
                else if (imageDataSize > 0)
                {
                    resourceReader = new ResourceReader(reader, imageDataOffset, imageDataSize);
                }
                else if (imageDataUnderscoreSize > 0)
                {
                    resourceReader = new ResourceReader(reader, imageDataUnderscoreOffset, imageDataUnderscoreSize);
                }
                else
                {
                    resourceReader = new ResourceReader(reader, reader.BaseStream.Position, 0);
                }

                m_ColorSpace = colorSpace;
                m_Format = format;
                m_Width = width;
                m_Height = height;
                m_Depth = depth;
                m_MipCount = mipCount;
                m_DataSize = dataSize;
                m_TextureSettings = textureSettings ?? new GLTextureSettings();
                m_UsageMode = usageMode;
                m_IsReadable = isReadable;
                m_IgnoreMipmapLimit = ignoreMipmapLimit;
                m_MipmapLimitGroupName = mipmapLimitGroupName;
                m_StreamData = streamData;
                image_data = resourceReader;
                ResolveTextureFormat();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static GLTextureSettings ReadTextureSettingsStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 1);
                return new GLTextureSettings();
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            int filterMode = 0;
            int aniso = 0;
            float mipBias = 0f;
            int wrapMode = 0;
            int wrapU = 0;
            bool hasWrapMode = false;

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_FilterMode":
                        if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out filterMode, depth: 1))
                            throw new InvalidDataException("Invalid m_FilterMode node.");
                        break;
                    case "m_Aniso":
                        if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out aniso, depth: 1))
                            throw new InvalidDataException("Invalid m_Aniso node.");
                        break;
                    case "m_MipBias":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out mipBias, depth: 1))
                            throw new InvalidDataException("Invalid m_MipBias node.");
                        break;
                    case "m_WrapMode":
                        if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out wrapMode, depth: 1))
                            throw new InvalidDataException("Invalid m_WrapMode node.");
                        hasWrapMode = true;
                        break;
                    case "m_WrapU":
                        if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out wrapU, depth: 1))
                            throw new InvalidDataException("Invalid m_WrapU node.");
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 1);
                        break;
                }
            }

            if (align)
            {
                reader.AlignStream();
            }

            return new GLTextureSettings
            {
                m_FilterMode = filterMode,
                m_Aniso = aniso,
                m_MipBias = mipBias,
                m_WrapMode = hasWrapMode ? wrapMode : wrapU
            };
        }

        private static StreamingInfo? ReadStreamingInfoStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 1);
                return null;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            long offset = 0;
            uint size = 0;
            string path = string.Empty;

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "offset":
                        if (!TypeTreeHelper.TryReadInt64(nodes, nextSibling, reader, ref j, out offset, depth: 1))
                            throw new InvalidDataException("Invalid StreamingInfo.offset node.");
                        break;
                    case "size":
                        if (!TypeTreeHelper.TryReadUInt32(nodes, nextSibling, reader, ref j, out size, depth: 1))
                            throw new InvalidDataException("Invalid StreamingInfo.size node.");
                        break;
                    case "path":
                        TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref j, out path, depth: 1);
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 1);
                        break;
                }
            }

            if (align)
            {
                reader.AlignStream();
            }

            return new StreamingInfo
            {
                offset = offset,
                size = size,
                path = path
            };
        }
    }
}
