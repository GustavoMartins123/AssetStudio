using System;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class StreamingInfo
    {
        public long offset; //ulong
        public uint size;
        public string path = string.Empty;

        public StreamingInfo() { }

        public StreamingInfo(ObjectReader reader)
        {
            var version = reader.version;

            if (version[0] >= 2020) //2020.1 and up
            {
                offset = reader.ReadInt64();
                size = reader.ReadUInt32();
            }
            else
            {
                offset = reader.ReadUInt32();
                size = reader.ReadUInt32();

                // Heurística genérica: Se o tamanho for 0 e o offset for > 0, 
                // é muito provável que o offset original seja na verdade um Int64 (8 bytes).
                // O que aconteceu foi que lemos a metade inferior como offset, e a metade superior (0) como size.
                // Sendo assim, o tamanho real está nos próximos 4 bytes!
                if (size == 0 && offset > 0)
                {
                    size = reader.ReadUInt32();
                }
            }
            path = reader.ReadAlignedString();
        }
    }

    public class GLTextureSettings
    {
        public int m_FilterMode;
        public int m_Aniso;
        public float m_MipBias;
        public int m_WrapMode;

        public GLTextureSettings() { }

        public GLTextureSettings(ObjectReader reader)
        {
            var version = reader.version;

            m_FilterMode = reader.ReadInt32();
            m_Aniso = reader.ReadInt32();
            m_MipBias = reader.ReadSingle();
            if (version[0] >= 2017)//2017.x and up
            {
                m_WrapMode = reader.ReadInt32(); //m_WrapU
                int m_WrapV = reader.ReadInt32();
                int m_WrapW = reader.ReadInt32();
            }
            else
            {
                m_WrapMode = reader.ReadInt32();
            }
        }
    }

    public sealed class Texture2D : Texture
    {
        public int m_Width;
        public int m_Height;
        public TextureFormat m_TextureFormat;
        public bool m_MipMap;
        public int m_MipCount;
        public GLTextureSettings m_TextureSettings;
        public ResourceReader image_data;
        public StreamingInfo? m_StreamData;

        public Texture2D(ObjectReader reader) : base(reader)
        {
            var position = reader.Position;
            if (TryReadFromTypeTree())
            {
                return;
            }
            reader.Position = position;

            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            var m_CompleteImageSize = reader.ReadInt32();
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_MipsStripped = reader.ReadInt32();
            }
            m_TextureFormat = (TextureFormat)reader.ReadInt32();
            if (version[0] < 5 || (version[0] == 5 && version[1] < 2)) //5.2 down
            {
                m_MipMap = reader.ReadBoolean();
            }
            else
            {
                m_MipCount = reader.ReadInt32();
            }
            if (version[0] > 2 || (version[0] == 2 && version[1] >= 6)) //2.6.0 and up
            {
                var m_IsReadable = reader.ReadBoolean();
            }
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_IsPreProcessed = reader.ReadBoolean();
            }
            if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
            {
                var m_IgnoreMasterTextureLimit = reader.ReadBoolean();
            }
            if (version[0] >= 3) //3.0.0 - 5.4
            {
                if (version[0] < 5 || (version[0] == 5 && version[1] <= 4))
                {
                    var m_ReadAllowed = reader.ReadBoolean();
                }
            }
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2)) //2018.2 and up
            {
                var m_StreamingMipmaps = reader.ReadBoolean();
            }
            reader.AlignStream();
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2)) //2018.2 and up
            {
                var m_StreamingMipmapsPriority = reader.ReadInt32();
            }
            if (version[0] >= 2022) //2022.x and up
            {
                var m_IgnoreMipmapLimit = reader.ReadBoolean();
                reader.AlignStream();
                var m_MipmapLimitGroupName = reader.ReadAlignedString();
            }
            var m_ImageCount = reader.ReadInt32();
            var m_TextureDimension = reader.ReadInt32();
            m_TextureSettings = new GLTextureSettings(reader);
            if (version[0] >= 3) //3.0 and up
            {
                var m_LightmapFormat = reader.ReadInt32();
            }
            if (version[0] > 3 || (version[0] == 3 && version[1] >= 5)) //3.5.0 and up
            {
                var m_ColorSpace = reader.ReadInt32();
            }
            if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) //2020.2 and up
            {
                var m_PlatformBlob = reader.ReadUInt8Array();
                reader.AlignStream();
            }
            
            long posBeforeImageData = reader.BaseStream.Position;
            var image_data_size = 0;
            try
            {
                image_data_size = reader.ReadInt32();
                if (image_data_size == 0 && HasStreamData(version))//5.3.0 and up
                {
                    m_StreamData = new StreamingInfo(reader);
                    // Validação genérica: se o path lido do StreamingInfo for um lixo de memória, é certeza que há um desalinhamento.
                    if (!string.IsNullOrEmpty(m_StreamData.path) && (m_StreamData.path.Length > 260 || m_StreamData.path.Any(c => char.IsControl(c))))
                    {
                        throw new InvalidDataException("Invalid stream path - trying with extra u32 padding");
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is ArgumentOutOfRangeException)
            {
                // Fallback para forks que adicionam um padding u32 antes do tamanho da imagem
                m_StreamData = null;
                image_data_size = 0;
                long objectEnd = reader.byteStart + reader.byteSize;
                if (posBeforeImageData + 8 <= objectEnd && posBeforeImageData + 8 <= reader.BaseStream.Length)
                {
                    reader.BaseStream.Position = posBeforeImageData + 4; // Pula 4 bytes
                    image_data_size = reader.ReadInt32();
                    if (image_data_size == 0 && HasStreamData(version))
                    {
                        m_StreamData = new StreamingInfo(reader);
                    }
                }
                else
                {
                    Logger.Warning($"Texture2D '{m_Name}' (PathID {m_PathID}): skipped extra u32 padding fallback because offset {posBeforeImageData + 8} exceeds object boundary {objectEnd}.");
                    reader.BaseStream.Position = Math.Min(posBeforeImageData, reader.BaseStream.Length);
                }
            }

            ResourceReader resourceReader;
            if (!string.IsNullOrEmpty(m_StreamData?.path) && m_StreamData.size > 0)
            {
                resourceReader = new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size);
            }
            else if (image_data_size > 0)
            {
                resourceReader = new ResourceReader(reader, reader.BaseStream.Position, image_data_size);
            }
            else
            {
                resourceReader = new ResourceReader(reader, reader.BaseStream.Position, 0);
            }
            image_data = resourceReader;
        }

        private static bool HasStreamData(int[] version)
        {
            return (version[0] == 5 && version[1] >= 3) || version[0] > 5;
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

                int width = 0;
                int height = 0;
                int format = 0;
                bool hasFormat = false;
                bool mipMap = false;
                int mipCount = 0;
                GLTextureSettings? textureSettings = null;
                StreamingInfo? streamData = null;
                long imageDataOffset = 0;
                int imageDataSize = 0;
                long imageDataUnderscoreOffset = 0;
                int imageDataUnderscoreSize = 0;

                for (int i = 1; i < nodes.Count; i++)
                {
                    switch (nodes[i].m_Name)
                    {
                        case "m_Width":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out width))
                                return false;
                            break;
                        case "m_Height":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out height))
                                return false;
                            break;
                        case "m_TextureFormat":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out format))
                                return false;
                            hasFormat = true;
                            break;
                        case "m_MipMap":
                            if (!TypeTreeHelper.TryReadBoolean(nodes, nextSibling, reader, ref i, out mipMap))
                                return false;
                            break;
                        case "m_MipCount":
                            if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref i, out mipCount))
                                return false;
                            break;
                        case "m_TextureSettings":
                            textureSettings = ReadTextureSettingsStreaming(nodes, nextSibling, reader, ref i);
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

                if (width <= 0 || height <= 0 || !hasFormat || !Enum.IsDefined(typeof(TextureFormat), format))
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

                if (resourceReader == null)
                {
                    return false;
                }

                m_Width = width;
                m_Height = height;
                m_TextureFormat = (TextureFormat)format;
                m_MipMap = mipMap;
                m_MipCount = mipCount;
                m_TextureSettings = textureSettings ?? new GLTextureSettings();
                m_StreamData = streamData;
                image_data = resourceReader;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static GLTextureSettings ReadTextureSettingsStreaming(System.Collections.Generic.List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
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

        private static StreamingInfo? ReadStreamingInfoStreaming(System.Collections.Generic.List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
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

    public enum TextureFormat
    {
        Alpha8 = 1,
        ARGB4444,
        RGB24,
        RGBA32,
        ARGB32,
        ARGBFloat,
        RGB565,
        BGR24,
        R16,
        DXT1,
        DXT3,
        DXT5,
        RGBA4444,
        BGRA32,
        RHalf,
        RGHalf,
        RGBAHalf,
        RFloat,
        RGFloat,
        RGBAFloat,
        YUY2,
        RGB9e5Float,
        RGBFloat,
        BC6H,
        BC7,
        BC4,
        BC5,
        DXT1Crunched,
        DXT5Crunched,
        PVRTC_RGB2,
        PVRTC_RGBA2,
        PVRTC_RGB4,
        PVRTC_RGBA4,
        ETC_RGB4,
        ATC_RGB4,
        ATC_RGBA8,
        EAC_R = 41,
        EAC_R_SIGNED,
        EAC_RG,
        EAC_RG_SIGNED,
        ETC2_RGB,
        ETC2_RGBA1,
        ETC2_RGBA8,
        ASTC_RGB_4x4,
        ASTC_RGB_5x5,
        ASTC_RGB_6x6,
        ASTC_RGB_8x8,
        ASTC_RGB_10x10,
        ASTC_RGB_12x12,
        ASTC_RGBA_4x4,
        ASTC_RGBA_5x5,
        ASTC_RGBA_6x6,
        ASTC_RGBA_8x8,
        ASTC_RGBA_10x10,
        ASTC_RGBA_12x12,
        ETC_RGB4_3DS,
        ETC_RGBA8_3DS,
        RG16,
        R8,
        ETC_RGB4Crunched,
        ETC2_RGBA8Crunched,
        ASTC_HDR_4x4,
        ASTC_HDR_5x5,
        ASTC_HDR_6x6,
        ASTC_HDR_8x8,
        ASTC_HDR_10x10,
        ASTC_HDR_12x12,
        RG32,
        RGB48,
        RGBA64
    }
}
