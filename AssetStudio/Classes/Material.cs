using System;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio
{
    public class UnityTexEnv
    {
        public PPtr<Texture> m_Texture;
        public Vector2 m_Scale;
        public Vector2 m_Offset;

        public UnityTexEnv(PPtr<Texture> texture, Vector2 scale, Vector2 offset)
        {
            m_Texture = texture;
            m_Scale = scale;
            m_Offset = offset;
        }

        public UnityTexEnv(ObjectReader reader)
        {
            m_Texture = new PPtr<Texture>(reader);
            m_Scale = reader.ReadVector2();
            m_Offset = reader.ReadVector2();
        }
    }

    public class UnityPropertySheet
    {
        public KeyValuePair<string, UnityTexEnv>[] m_TexEnvs = System.Array.Empty<KeyValuePair<string, UnityTexEnv>>();
        public KeyValuePair<string, int>[] m_Ints = System.Array.Empty<KeyValuePair<string, int>>();
        public KeyValuePair<string, float>[] m_Floats = System.Array.Empty<KeyValuePair<string, float>>();
        public KeyValuePair<string, Color>[] m_Colors = System.Array.Empty<KeyValuePair<string, Color>>();

        public UnityPropertySheet() { }

        public UnityPropertySheet(ObjectReader reader)
        {
            var version = reader.version;

            int m_TexEnvsSize = reader.ReadInt32();
            reader.CheckArrayLength(m_TexEnvsSize);
            m_TexEnvs = new KeyValuePair<string, UnityTexEnv>[m_TexEnvsSize];
            for (int i = 0; i < m_TexEnvsSize; i++)
            {
                m_TexEnvs[i] = new KeyValuePair<string, UnityTexEnv>(reader.ReadAlignedString(), new UnityTexEnv(reader));
            }

            if (version[0] >= 2021) //2021.1 and up
            {
                int m_IntsSize = reader.ReadInt32();
                reader.CheckArrayLength(m_IntsSize);
                m_Ints = new KeyValuePair<string, int>[m_IntsSize];
                for (int i = 0; i < m_IntsSize; i++)
                {
                    m_Ints[i] = new KeyValuePair<string, int>(reader.ReadAlignedString(), reader.ReadInt32());
                }
            }

            int m_FloatsSize = reader.ReadInt32();
            reader.CheckArrayLength(m_FloatsSize);
            m_Floats = new KeyValuePair<string, float>[m_FloatsSize];
            for (int i = 0; i < m_FloatsSize; i++)
            {
                m_Floats[i] = new KeyValuePair<string, float>(reader.ReadAlignedString(), reader.ReadSingle());
            }

            int m_ColorsSize = reader.ReadInt32();
            reader.CheckArrayLength(m_ColorsSize);
            m_Colors = new KeyValuePair<string, Color>[m_ColorsSize];
            for (int i = 0; i < m_ColorsSize; i++)
            {
                m_Colors[i] = new KeyValuePair<string, Color>(reader.ReadAlignedString(), reader.ReadColor4());
            }
        }
    }

    public class BuildTextureStackReference
    {
        public string groupName;
        public string itemName;

        public BuildTextureStackReference(ObjectReader reader)
        {
            groupName = reader.ReadAlignedString();
            itemName = reader.ReadAlignedString();
        }
    }

    public sealed class Material : NamedObject
    {
        public PPtr<Shader> m_Shader;
        public PPtr<Material> m_Parent;
        public string[] m_ValidKeywords = Array.Empty<string>();
        public string[] m_InvalidKeywords = Array.Empty<string>();
        public uint m_LightmapFlags;
        public bool m_EnableInstancingVariants;
        public bool m_DoubleSidedGI;
        public int m_CustomRenderQueue;
        public KeyValuePair<string, string>[] stringTagMap = Array.Empty<KeyValuePair<string, string>>();
        public string[] disabledShaderPasses = Array.Empty<string>();
        public UnityPropertySheet m_SavedProperties;
        public BuildTextureStackReference[] m_BuildTextureStacks = Array.Empty<BuildTextureStackReference>();

        public Material(ObjectReader reader) : base(reader)
        {
            var position = reader.Position;
            if (TryReadFromTypeTree(reader))
            {
                return;
            }
            reader.Position = position;

            m_Shader = new PPtr<Shader>(reader);
            m_Parent = new PPtr<Material>(0, 0, reader.assetsFile);
            if (version[0] == 4 && version[1] >= 1) //4.x
            {
                m_ValidKeywords = reader.ReadStringArray();
            }

            if (version[0] >= 2021) //2021.1 and up
            {
                if (version[0] > 2021 || (version[0] == 2021 && version[1] >= 3)) //2021.3 and up
                {
                    m_ValidKeywords = reader.ReadStringArray();
                    m_InvalidKeywords = reader.ReadStringArray();
                }
                else //2021.1 ~ 2021.2
                {
                    m_ValidKeywords = SplitShaderKeywords(reader.ReadAlignedString());
                }
            }
            else if (version[0] >= 5 && version[0] < 2021) //5.0 ~ 2020.3
            {
                m_ValidKeywords = SplitShaderKeywords(reader.ReadAlignedString());
            }

            if (version[0] >= 5) //5.0 and up
            {
                m_LightmapFlags = reader.ReadUInt32();
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                m_EnableInstancingVariants = reader.ReadBoolean();
                if (version[0] >= 2017) //2017 and up
                {
                    m_DoubleSidedGI = reader.ReadBoolean();
                }
                reader.AlignStream();
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_CustomRenderQueue = reader.ReadInt32();
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 1)) //5.1 and up
            {
                var stringTagMapSize = reader.ReadInt32();
                reader.CheckArrayLength(stringTagMapSize);
                stringTagMap = new KeyValuePair<string, string>[stringTagMapSize];
                for (int i = 0; i < stringTagMapSize; i++)
                {
                    var first = reader.ReadAlignedString();
                    var second = reader.ReadAlignedString();
                    stringTagMap[i] = new KeyValuePair<string, string>(first, second);
                }
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                disabledShaderPasses = reader.ReadStringArray();
            }

            m_SavedProperties = new UnityPropertySheet(reader);

            if (version[0] >= 2020) //2020.1 and up
            {
                int numStacks = reader.ReadInt32();
                reader.CheckArrayLength(numStacks);
                m_BuildTextureStacks = new BuildTextureStackReference[numStacks];
                for (int i = 0; i < numStacks; i++)
                {
                    m_BuildTextureStacks[i] = new BuildTextureStackReference(reader);
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(m_Shader), nameof(m_Parent), nameof(m_SavedProperties))]
        private bool TryReadFromTypeTree(ObjectReader reader)
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

                PPtr<Shader>? shader = null;
                PPtr<Material>? parent = null;
                string[] shaderKeywords = Array.Empty<string>();
                UnityPropertySheet? savedProperties = null;

                for (int i = 1; i < nodes.Count; i++)
                {
                    switch (nodes[i].m_Name)
                    {
                        case "m_Shader":
                            shader = ReadPPtrStreaming<Shader>(nodes, nextSibling, reader, ref i, reader.assetsFile, depth: 0);
                            break;
                        case "m_Parent":
                            parent = ReadPPtrStreaming<Material>(nodes, nextSibling, reader, ref i, reader.assetsFile, depth: 0);
                            break;
                        case "m_ShaderKeywords" when nodes[i].m_Type == "string":
                            if (!TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref i, out string keywords, depth: 0))
                            {
                                throw new InvalidDataException("Invalid Material.m_ShaderKeywords node.");
                            }
                            shaderKeywords = SplitShaderKeywords(keywords);
                            break;
                        case "m_SavedProperties":
                            savedProperties = ReadUnityPropertySheetStreaming(nodes, nextSibling, reader, ref i, reader.assetsFile);
                            break;
                        default:
                            TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref i, depth: 0);
                            break;
                    }
                }

                if (savedProperties == null)
                {
                    return false;
                }

                m_Shader = shader ?? new PPtr<Shader>(0, 0, reader.assetsFile);
                m_Parent = parent ?? new PPtr<Material>(0, 0, reader.assetsFile);
                m_ValidKeywords = shaderKeywords;
                m_SavedProperties = savedProperties;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string[] SplitShaderKeywords(string keywords) => keywords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static UnityPropertySheet? ReadUnityPropertySheetStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            SerializedFile assetsFile)
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

            var texEnvs = Array.Empty<KeyValuePair<string, UnityTexEnv>>();
            var ints = Array.Empty<KeyValuePair<string, int>>();
            var floats = Array.Empty<KeyValuePair<string, float>>();
            var colors = Array.Empty<KeyValuePair<string, Color>>();

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_TexEnvs":
                        texEnvs = ReadTexEnvsStreaming(nodes, nextSibling, reader, ref j, assetsFile);
                        break;
                    case "m_Ints":
                        ints = ReadIntMapStreaming(nodes, nextSibling, reader, ref j);
                        break;
                    case "m_Floats":
                        floats = ReadFloatMapStreaming(nodes, nextSibling, reader, ref j);
                        break;
                    case "m_Colors":
                        colors = ReadColorMapStreaming(nodes, nextSibling, reader, ref j);
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

            return new UnityPropertySheet
            {
                m_TexEnvs = texEnvs,
                m_Ints = ints,
                m_Floats = floats,
                m_Colors = colors
            };
        }

        private static bool TryBeginMap(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            out int size,
            out int firstIndex,
            out int secondIndex,
            out bool align,
            out int mapEnd)
        {
            if (nodes[index].m_Type != "map")
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 2);
                size = 0;
                firstIndex = 0;
                secondIndex = 0;
                align = false;
                mapEnd = 0;
                return false;
            }

            mapEnd = nextSibling[index];
            align = (nodes[index].m_MetaFlag & 0x4000) != 0
                || (index + 1 < mapEnd && (nodes[index + 1].m_MetaFlag & 0x4000) != 0);
            firstIndex = index + 4;
            secondIndex = firstIndex < mapEnd ? nextSibling[firstIndex] : mapEnd;

            size = reader.ReadInt32();
            reader.CheckArrayLength(size);
            if (size > 0 && (firstIndex >= mapEnd || secondIndex >= mapEnd))
            {
                throw new InvalidDataException("Invalid map TypeTree node layout.");
            }

            return true;
        }

        private static KeyValuePair<string, UnityTexEnv>[] ReadTexEnvsStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            SerializedFile assetsFile)
        {
            if (!TryBeginMap(nodes, nextSibling, reader, ref index, out int size, out int firstIndex, out int secondIndex, out bool align, out int mapEnd))
            {
                return Array.Empty<KeyValuePair<string, UnityTexEnv>>();
            }

            index = mapEnd - 1;
            if (size == 0)
            {
                if (align) reader.AlignStream();
                return Array.Empty<KeyValuePair<string, UnityTexEnv>>();
            }

            if (!TypeTreeHelper.IsClassNode(nodes, secondIndex))
            {
                for (int j = 0; j < size; j++)
                {
                    int tmp1 = firstIndex;
                    int tmp2 = secondIndex;
                    TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref tmp1, depth: 3);
                    TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref tmp2, depth: 3);
                }
                if (align) reader.AlignStream();
                return Array.Empty<KeyValuePair<string, UnityTexEnv>>();
            }

            var result = new KeyValuePair<string, UnityTexEnv>[size];
            for (int j = 0; j < size; j++)
            {
                int kIdx = firstIndex;
                TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref kIdx, out string key, depth: 3);
                int vIdx = secondIndex;
                var value = ReadUnityTexEnvStreaming(nodes, nextSibling, reader, ref vIdx, assetsFile);
                result[j] = new KeyValuePair<string, UnityTexEnv>(key, value);
            }

            if (align) reader.AlignStream();
            return result;
        }

        private static UnityTexEnv ReadUnityTexEnvStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            SerializedFile assetsFile)
        {
            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            PPtr<Texture>? texture = null;
            Vector2 scale = Vector2.Zero;
            Vector2 offset = Vector2.Zero;

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_Texture":
                        texture = ReadPPtrStreaming<Texture>(nodes, nextSibling, reader, ref j, assetsFile, depth: 4);
                        break;
                    case "m_Scale":
                        scale = ReadVector2Streaming(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    case "m_Offset":
                        offset = ReadVector2Streaming(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new UnityTexEnv(texture ?? new PPtr<Texture>(0, 0, assetsFile), scale, offset);
        }

        private static KeyValuePair<string, int>[] ReadIntMapStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index)
        {
            if (!TryBeginMap(nodes, nextSibling, reader, ref index, out int size, out int firstIndex, out int secondIndex, out bool align, out int mapEnd))
            {
                return Array.Empty<KeyValuePair<string, int>>();
            }

            index = mapEnd - 1;
            if (size == 0)
            {
                if (align) reader.AlignStream();
                return Array.Empty<KeyValuePair<string, int>>();
            }

            var result = new KeyValuePair<string, int>[size];
            for (int j = 0; j < size; j++)
            {
                int kIdx = firstIndex;
                TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref kIdx, out string key, depth: 3);
                int vIdx = secondIndex;
                if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref vIdx, out int value, depth: 3))
                    throw new InvalidDataException("Invalid m_Ints map value node.");
                result[j] = new KeyValuePair<string, int>(key, value);
            }

            if (align) reader.AlignStream();
            return result;
        }

        private static KeyValuePair<string, float>[] ReadFloatMapStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index)
        {
            if (!TryBeginMap(nodes, nextSibling, reader, ref index, out int size, out int firstIndex, out int secondIndex, out bool align, out int mapEnd))
            {
                return Array.Empty<KeyValuePair<string, float>>();
            }

            index = mapEnd - 1;
            if (size == 0)
            {
                if (align) reader.AlignStream();
                return Array.Empty<KeyValuePair<string, float>>();
            }

            var result = new KeyValuePair<string, float>[size];
            for (int j = 0; j < size; j++)
            {
                int kIdx = firstIndex;
                TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref kIdx, out string key, depth: 3);
                int vIdx = secondIndex;
                if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref vIdx, out float value, depth: 3))
                    throw new InvalidDataException("Invalid m_Floats map value node.");
                result[j] = new KeyValuePair<string, float>(key, value);
            }

            if (align) reader.AlignStream();
            return result;
        }

        private static KeyValuePair<string, Color>[] ReadColorMapStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index)
        {
            if (!TryBeginMap(nodes, nextSibling, reader, ref index, out int size, out int firstIndex, out int secondIndex, out bool align, out int mapEnd))
            {
                return Array.Empty<KeyValuePair<string, Color>>();
            }

            index = mapEnd - 1;
            if (size == 0)
            {
                if (align) reader.AlignStream();
                return Array.Empty<KeyValuePair<string, Color>>();
            }

            var result = new KeyValuePair<string, Color>[size];
            for (int j = 0; j < size; j++)
            {
                int kIdx = firstIndex;
                TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref kIdx, out string key, depth: 3);
                int vIdx = secondIndex;
                var value = ReadColorStreaming(nodes, nextSibling, reader, ref vIdx);
                result[j] = new KeyValuePair<string, Color>(key, value);
            }

            if (align) reader.AlignStream();
            return result;
        }

        private static PPtr<T> ReadPPtrStreaming<T>(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            SerializedFile assetsFile,
            int depth) where T : Object
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth + 1);
                return new PPtr<T>(0, 0, assetsFile);
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            int fileId = 0;
            long pathId = 0;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_FileID":
                        if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out fileId, depth + 1))
                            throw new InvalidDataException("Invalid PPtr.m_FileID node.");
                        break;
                    case "m_PathID":
                        if (!TypeTreeHelper.TryReadInt64(nodes, nextSibling, reader, ref j, out pathId, depth + 1))
                            throw new InvalidDataException("Invalid PPtr.m_PathID node.");
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new PPtr<T>(fileId, pathId, assetsFile);
        }

        private static Vector2 ReadVector2Streaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            int depth)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth + 1);
                return Vector2.Zero;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            float x = 0f;
            float y = 0f;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "x":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out x, depth + 1))
                            throw new InvalidDataException("Invalid Vector2.x node.");
                        break;
                    case "y":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out y, depth + 1))
                            throw new InvalidDataException("Invalid Vector2.y node.");
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new Vector2(x, y);
        }

        private static Color ReadColorStreaming(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 3);
                return new Color();
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            float r = 0f, g = 0f, b = 0f, a = 0f;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "r":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out r, depth: 4))
                            throw new InvalidDataException("Invalid Color.r node.");
                        break;
                    case "g":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out g, depth: 4))
                            throw new InvalidDataException("Invalid Color.g node.");
                        break;
                    case "b":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out b, depth: 4))
                            throw new InvalidDataException("Invalid Color.b node.");
                        break;
                    case "a":
                        if (!TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out a, depth: 4))
                            throw new InvalidDataException("Invalid Color.a node.");
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new Color(r, g, b, a);
        }
    }
}
