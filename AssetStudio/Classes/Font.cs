using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public sealed class Font : NamedObject
    {
        public float m_LineSpacing;
        public PPtr<Material>? m_DefaultMaterial;
        public float m_FontSize;
        public PPtr<Texture>? m_Texture;
        public int m_AsciiStartOffset;
        public int m_CharacterRectCount;
        public float m_PixelScale;
        public byte[]? m_FontData;
        public string[]? m_FontNames;

        public Font(ObjectReader reader) : base(reader)
        {
            var objectEnd = reader.byteStart + reader.byteSize;
            if ((version[0] == 5 && version[1] >= 5) || version[0] > 5)//5.5 and up
            {
                m_LineSpacing = reader.ReadSingle();
                m_DefaultMaterial = new PPtr<Material>(reader);
                m_FontSize = reader.ReadSingle();
                m_Texture = new PPtr<Texture>(reader);
                m_AsciiStartOffset = reader.ReadInt32();
                var m_Tracking = reader.ReadSingle();
                var m_CharacterSpacing = reader.ReadInt32();
                var m_CharacterPadding = reader.ReadInt32();
                var m_ConvertCase = reader.ReadInt32();
                int m_CharacterRects_size = reader.ReadInt32();
                m_CharacterRectCount = Math.Max(0, m_CharacterRects_size);
                for (int i = 0; i < m_CharacterRects_size; i++)
                {
                    reader.Position += 44;//CharacterInfo data 41
                }
                int m_KerningValues_size = reader.ReadInt32();
                for (int i = 0; i < m_KerningValues_size; i++)
                {
                    reader.Position += 8;
                }
                m_PixelScale = reader.ReadSingle();
                int m_FontData_size = reader.ReadInt32();
                if (m_FontData_size > 0)
                {
                    m_FontData = reader.ReadBytes(m_FontData_size);
                }

                TryReadTrailingFontNames(reader, objectEnd, hasDescent: true);
            }
            else
            {
                m_AsciiStartOffset = reader.ReadInt32();

                if (version[0] <= 3)
                {
                    int m_FontCountX = reader.ReadInt32();
                    int m_FontCountY = reader.ReadInt32();
                }

                float m_Kerning = reader.ReadSingle();
                m_LineSpacing = reader.ReadSingle();

                if (version[0] <= 3)
                {
                    int m_PerCharacterKerning_size = reader.ReadInt32();
                    for (int i = 0; i < m_PerCharacterKerning_size; i++)
                    {
                        int first = reader.ReadInt32();
                        float second = reader.ReadSingle();
                    }
                }
                else
                {
                    int m_CharacterSpacing = reader.ReadInt32();
                    int m_CharacterPadding = reader.ReadInt32();
                }

                int m_ConvertCase = reader.ReadInt32();
                m_DefaultMaterial = new PPtr<Material>(reader);

                int m_CharacterRects_size = reader.ReadInt32();
                m_CharacterRectCount = Math.Max(0, m_CharacterRects_size);
                for (int i = 0; i < m_CharacterRects_size; i++)
                {
                    int index = reader.ReadInt32();
                    //Rectf uv
                    float uvx = reader.ReadSingle();
                    float uvy = reader.ReadSingle();
                    float uvwidth = reader.ReadSingle();
                    float uvheight = reader.ReadSingle();
                    //Rectf vert
                    float vertx = reader.ReadSingle();
                    float verty = reader.ReadSingle();
                    float vertwidth = reader.ReadSingle();
                    float vertheight = reader.ReadSingle();
                    float width = reader.ReadSingle();

                    if (version[0] >= 4)
                    {
                        var flipped = reader.ReadBoolean();
                        reader.AlignStream();
                    }
                }

                m_Texture = new PPtr<Texture>(reader);

                int m_KerningValues_size = reader.ReadInt32();
                for (int i = 0; i < m_KerningValues_size; i++)
                {
                    int pairfirst = reader.ReadInt16();
                    int pairsecond = reader.ReadInt16();
                    float second = reader.ReadSingle();
                }

                if (version[0] <= 3)
                {
                    var m_GridFont = reader.ReadBoolean();
                    reader.AlignStream();
                }
                else { m_PixelScale = reader.ReadSingle(); }

                int m_FontData_size = reader.ReadInt32();
                if (m_FontData_size > 0)
                {
                    m_FontData = reader.ReadBytes(m_FontData_size);
                }

                if (version[0] >= 4)
                {
                    var hasDescent = version[0] > 5 || (version[0] == 5 && version[1] >= 4);
                    TryReadTrailingFontNames(reader, objectEnd, hasDescent);
                }
            }
        }

        private void TryReadTrailingFontNames(ObjectReader reader, long objectEnd, bool hasDescent)
        {
            try
            {
                if (reader.Position >= objectEnd)
                {
                    return;
                }

                reader.AlignStream();
                var headerBytes = hasDescent ? 16 : 12; // m_Ascent, [m_Descent], m_DefaultStyle, m_FontNames.size
                if (reader.Position + headerBytes > objectEnd)
                {
                    return;
                }

                var m_Ascent = reader.ReadSingle();
                if (hasDescent)
                {
                    var m_Descent = reader.ReadSingle();
                }
                var m_DefaultStyle = reader.ReadUInt32();
                var fontNamesCount = reader.ReadInt32();
                if (fontNamesCount <= 0 || fontNamesCount > 64)
                {
                    return;
                }

                var names = new List<string>(fontNamesCount);
                for (var i = 0; i < fontNamesCount; i++)
                {
                    if (reader.Position + 4 > objectEnd)
                    {
                        return;
                    }

                    var len = reader.ReadInt32();
                    if (len < 0 || len > 1024 || reader.Position + len > objectEnd)
                    {
                        return;
                    }

                    var bytes = reader.ReadBytes(len);
                    reader.AlignStream();
                    var name = Encoding.UTF8.GetString(bytes).Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }

                if (names.Count > 0)
                {
                    m_FontNames = names.ToArray();
                }
            }
            catch
            {
                // Optional trailing Font metadata varies across Unity versions; ignore safely if unavailable.
            }
        }
    }
}
