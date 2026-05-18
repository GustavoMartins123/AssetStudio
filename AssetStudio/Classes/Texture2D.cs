using System;
using System.Collections.Specialized;
using System.Linq;

namespace AssetStudio
{
    public class StreamingInfo
    {
        public long offset; //ulong
        public uint size;
        public string path;

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
        public StreamingInfo m_StreamData;

        public Texture2D(ObjectReader reader) : base(reader)
        {
            if (TryReadFromTypeTree())
            {
                return;
            }

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
                if ((version[0] == 5 && version[1] >= 3) || version[0] > 5)//5.3.0 and up
                {
                    m_StreamData = new StreamingInfo(reader);
                    // Validação genérica: se o path lido do StreamingInfo for um lixo de memória, é certeza que há um desalinhamento.
                    if (!string.IsNullOrEmpty(m_StreamData.path) && (m_StreamData.path.Length > 260 || m_StreamData.path.Any(c => char.IsControl(c))))
                    {
                        throw new Exception("Invalid stream path - trying with extra u32 padding");
                    }
                }
            }
            catch
            {
                // Fallback para forks que adicionam um padding u32 antes do tamanho da imagem
                reader.BaseStream.Position = posBeforeImageData + 4; // Pula 4 bytes
                image_data_size = reader.ReadInt32();
                if ((version[0] == 5 && version[1] >= 3) || version[0] > 5)
                {
                    m_StreamData = new StreamingInfo(reader);
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

        private bool TryReadFromTypeTree()
        {
            if (serializedType?.m_Type == null)
            {
                return false;
            }

            try
            {
                var obj = ToType();
                if (obj == null)
                {
                    ResetForManualParsing();
                    return false;
                }

                var width = GetInt32(obj, "m_Width");
                var height = GetInt32(obj, "m_Height");
                var format = GetInt32(obj, "m_TextureFormat");
                if (width <= 0 || height <= 0 || !Enum.IsDefined(typeof(TextureFormat), format))
                {
                    ResetForManualParsing();
                    return false;
                }

                m_Width = width;
                m_Height = height;
                m_TextureFormat = (TextureFormat)format;
                m_MipMap = GetBoolean(obj, "m_MipMap");
                m_MipCount = GetInt32(obj, "m_MipCount");
                m_TextureSettings = ReadTextureSettings(GetObject(obj, "m_TextureSettings"));

                var streamData = GetObject(obj, "m_StreamData");
                if (streamData != null)
                {
                    m_StreamData = new StreamingInfo
                    {
                        offset = GetInt64(streamData, "offset"),
                        size = GetUInt32(streamData, "size"),
                        path = GetString(streamData, "path")
                    };
                }

                ResourceReader resourceReader = null;
                if (!string.IsNullOrEmpty(m_StreamData?.path) && m_StreamData.size > 0)
                {
                    resourceReader = new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size);
                }
                else if (GetByteArray(obj, "image data") is byte[] data && data.Length > 0)
                {
                    resourceReader = new ResourceReader(data);
                }
                else if (GetByteArray(obj, "image_data") is byte[] imageData && imageData.Length > 0)
                {
                    resourceReader = new ResourceReader(imageData);
                }

                if (resourceReader == null)
                {
                    ResetForManualParsing();
                    return false;
                }

                image_data = resourceReader;
                return true;
            }
            catch
            {
                ResetForManualParsing();
                return false;
            }
        }

        private void ResetForManualParsing()
        {
            reader.Reset();
            if (platform == BuildTarget.NoTarget)
            {
                var m_ObjectHideFlags = reader.ReadUInt32();
            }
        }

        private static GLTextureSettings ReadTextureSettings(OrderedDictionary obj)
        {
            if (obj == null)
            {
                return new GLTextureSettings();
            }

            return new GLTextureSettings
            {
                m_FilterMode = GetInt32(obj, "m_FilterMode"),
                m_Aniso = GetInt32(obj, "m_Aniso"),
                m_MipBias = GetSingle(obj, "m_MipBias"),
                m_WrapMode = obj.Contains("m_WrapMode") ? GetInt32(obj, "m_WrapMode") : GetInt32(obj, "m_WrapU")
            };
        }

        private static OrderedDictionary GetObject(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? obj[name] as OrderedDictionary : null;
        }

        private static byte[] GetByteArray(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? obj[name] as byte[] : null;
        }

        private static string GetString(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? obj[name] as string ?? string.Empty : string.Empty;
        }

        private static bool GetBoolean(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) && Convert.ToBoolean(obj[name]);
        }

        private static int GetInt32(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? Convert.ToInt32(obj[name]) : 0;
        }

        private static long GetInt64(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? Convert.ToInt64(obj[name]) : 0;
        }

        private static uint GetUInt32(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? Convert.ToUInt32(obj[name]) : 0;
        }

        private static float GetSingle(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? Convert.ToSingle(obj[name]) : 0;
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
