using System.Collections.Generic;

using System.Collections.Specialized;

namespace AssetStudio
{
    public class UnityTexEnv
    {
        public PPtr<Texture> m_Texture;
        public Vector2 m_Scale;
        public Vector2 m_Offset;

        public UnityTexEnv() { }

        public UnityTexEnv(ObjectReader reader)
        {
            m_Texture = new PPtr<Texture>(reader);
            m_Scale = reader.ReadVector2();
            m_Offset = reader.ReadVector2();
        }
    }

    public class UnityPropertySheet
    {
        public KeyValuePair<string, UnityTexEnv>[] m_TexEnvs;
        public KeyValuePair<string, int>[] m_Ints;
        public KeyValuePair<string, float>[] m_Floats;
        public KeyValuePair<string, Color>[] m_Colors;

        public UnityPropertySheet() { }

        public UnityPropertySheet(ObjectReader reader)
        {
            var version = reader.version;

            int m_TexEnvsSize = reader.ReadInt32();
            m_TexEnvs = new KeyValuePair<string, UnityTexEnv>[m_TexEnvsSize];
            for (int i = 0; i < m_TexEnvsSize; i++)
            {
                m_TexEnvs[i] = new KeyValuePair<string, UnityTexEnv>(reader.ReadAlignedString(), new UnityTexEnv(reader));
            }

            if (version[0] >= 2021) //2021.1 and up
            {
                int m_IntsSize = reader.ReadInt32();
                m_Ints = new KeyValuePair<string, int>[m_IntsSize];
                for (int i = 0; i < m_IntsSize; i++)
                {
                    m_Ints[i] = new KeyValuePair<string, int>(reader.ReadAlignedString(), reader.ReadInt32());
                }
            }

            int m_FloatsSize = reader.ReadInt32();
            m_Floats = new KeyValuePair<string, float>[m_FloatsSize];
            for (int i = 0; i < m_FloatsSize; i++)
            {
                m_Floats[i] = new KeyValuePair<string, float>(reader.ReadAlignedString(), reader.ReadSingle());
            }

            int m_ColorsSize = reader.ReadInt32();
            m_Colors = new KeyValuePair<string, Color>[m_ColorsSize];
            for (int i = 0; i < m_ColorsSize; i++)
            {
                m_Colors[i] = new KeyValuePair<string, Color>(reader.ReadAlignedString(), reader.ReadColor4());
            }
        }
    }

    public sealed class Material : NamedObject
    {
        public PPtr<Shader> m_Shader;
        public PPtr<Material> m_Parent;
        public UnityPropertySheet m_SavedProperties;

        public Material(ObjectReader reader) : base(reader)
        {
            var position = reader.Position;
            if (TryReadFromTypeTree(reader))
            {
                return;
            }
            reader.Position = position;

            m_Shader = new PPtr<Shader>(reader);

            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2))
            {
                m_Parent = new PPtr<Material>(reader);
                var m_ModifiedSerializedProperties = reader.ReadBoolean();
                reader.AlignStream();
            }

            if (version[0] == 4 && version[1] >= 1) //4.x
            {
                var m_ShaderKeywords = reader.ReadStringArray();
            }

            if (version[0] > 2021 || (version[0] == 2021 && version[1] >= 3)) //2021.3 and up
            {
                var m_ValidKeywords = reader.ReadStringArray();
                var m_InvalidKeywords = reader.ReadStringArray();
            }
            else if (version[0] >= 5) //5.0 ~ 2021.2
            {
                var m_ShaderKeywords = reader.ReadAlignedString();
            }

            if (version[0] >= 5) //5.0 and up
            {
                var m_LightmapFlags = reader.ReadUInt32();
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                var m_EnableInstancingVariants = reader.ReadBoolean();
                //var m_DoubleSidedGI = a_Stream.ReadBoolean(); //2017 and up
                reader.AlignStream();
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                var m_CustomRenderQueue = reader.ReadInt32();
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 1)) //5.1 and up
            {
                var stringTagMapSize = reader.ReadInt32();
                for (int i = 0; i < stringTagMapSize; i++)
                {
                    var first = reader.ReadAlignedString();
                    var second = reader.ReadAlignedString();
                }
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                var disabledShaderPasses = reader.ReadStringArray();
            }

            if (version[0] > 2021 || (version[0] == 2021 && version[1] >= 2))
            {
                var m_LockedProperties = reader.ReadAlignedString();
            }

            m_SavedProperties = new UnityPropertySheet(reader);

            //vector m_BuildTextureStacks 2020 and up
        }

        private bool TryReadFromTypeTree(ObjectReader reader)
        {
            if (serializedType?.m_Type == null)
            {
                return false;
            }

            try
            {
                var obj = ToType();
                var savedProperties = GetObject(obj, "m_SavedProperties");
                if (savedProperties == null)
                {
                    return false;
                }

                m_Shader = ReadPPtr<Shader>(GetObject(obj, "m_Shader"), reader.assetsFile);
                m_Parent = ReadPPtr<Material>(GetObject(obj, "m_Parent"), reader.assetsFile);
                m_SavedProperties = new UnityPropertySheet
                {
                    m_TexEnvs = ReadTexEnvs(savedProperties, reader.assetsFile),
                    m_Ints = ReadIntMap(savedProperties, "m_Ints"),
                    m_Floats = ReadFloatMap(savedProperties, "m_Floats"),
                    m_Colors = ReadColorMap(savedProperties, "m_Colors")
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static KeyValuePair<string, UnityTexEnv>[] ReadTexEnvs(OrderedDictionary savedProperties, SerializedFile assetsFile)
        {
            if (!(savedProperties["m_TexEnvs"] is List<KeyValuePair<object, object>> map))
            {
                return new KeyValuePair<string, UnityTexEnv>[0];
            }

            var result = new List<KeyValuePair<string, UnityTexEnv>>(map.Count);
            foreach (var item in map)
            {
                if (!(item.Value is OrderedDictionary value))
                {
                    continue;
                }

                result.Add(new KeyValuePair<string, UnityTexEnv>(
                    item.Key as string ?? string.Empty,
                    new UnityTexEnv
                    {
                        m_Texture = ReadPPtr<Texture>(GetObject(value, "m_Texture"), assetsFile),
                        m_Scale = ReadVector2(GetObject(value, "m_Scale")),
                        m_Offset = ReadVector2(GetObject(value, "m_Offset"))
                    }));
            }
            return result.ToArray();
        }

        private static KeyValuePair<string, int>[] ReadIntMap(OrderedDictionary obj, string name)
        {
            if (!(obj[name] is List<KeyValuePair<object, object>> map))
            {
                return new KeyValuePair<string, int>[0];
            }
            var result = new List<KeyValuePair<string, int>>(map.Count);
            foreach (var item in map)
            {
                result.Add(new KeyValuePair<string, int>(item.Key as string ?? string.Empty, System.Convert.ToInt32(item.Value)));
            }
            return result.ToArray();
        }

        private static KeyValuePair<string, float>[] ReadFloatMap(OrderedDictionary obj, string name)
        {
            if (!(obj[name] is List<KeyValuePair<object, object>> map))
            {
                return new KeyValuePair<string, float>[0];
            }
            var result = new List<KeyValuePair<string, float>>(map.Count);
            foreach (var item in map)
            {
                result.Add(new KeyValuePair<string, float>(item.Key as string ?? string.Empty, System.Convert.ToSingle(item.Value)));
            }
            return result.ToArray();
        }

        private static KeyValuePair<string, Color>[] ReadColorMap(OrderedDictionary obj, string name)
        {
            if (!(obj[name] is List<KeyValuePair<object, object>> map))
            {
                return new KeyValuePair<string, Color>[0];
            }
            var result = new List<KeyValuePair<string, Color>>(map.Count);
            foreach (var item in map)
            {
                result.Add(new KeyValuePair<string, Color>(item.Key as string ?? string.Empty, ReadColor(item.Value as OrderedDictionary)));
            }
            return result.ToArray();
        }

        private static PPtr<T> ReadPPtr<T>(OrderedDictionary obj, SerializedFile assetsFile) where T : Object
        {
            if (obj == null)
            {
                return new PPtr<T>(0, 0, assetsFile);
            }
            return new PPtr<T>(ReadInt32(obj, "m_FileID"), ReadInt64(obj, "m_PathID"), assetsFile);
        }

        private static Vector2 ReadVector2(OrderedDictionary obj)
        {
            return obj == null ? Vector2.Zero : new Vector2(ReadSingle(obj, "x"), ReadSingle(obj, "y"));
        }

        private static Color ReadColor(OrderedDictionary obj)
        {
            return obj == null ? new Color() : new Color(ReadSingle(obj, "r"), ReadSingle(obj, "g"), ReadSingle(obj, "b"), ReadSingle(obj, "a"));
        }

        private static OrderedDictionary GetObject(OrderedDictionary obj, string name)
        {
            return obj != null && obj.Contains(name) ? obj[name] as OrderedDictionary : null;
        }

        private static int ReadInt32(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? System.Convert.ToInt32(obj[name]) : 0;
        }

        private static long ReadInt64(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? System.Convert.ToInt64(obj[name]) : 0;
        }

        private static float ReadSingle(OrderedDictionary obj, string name)
        {
            return obj.Contains(name) ? System.Convert.ToSingle(obj[name]) : 0;
        }
    }
}
