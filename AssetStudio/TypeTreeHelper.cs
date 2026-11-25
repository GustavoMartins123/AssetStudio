using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace AssetStudio
{
    public static class TypeTreeHelper
    {
        private const int MaxRecursionDepth = 64;

        private sealed class SkipTableEntry
        {
            public readonly int NodeCount;
            public readonly int[] NextSibling;

            public SkipTableEntry(int nodeCount, int[] nextSibling)
            {
                NodeCount = nodeCount;
                NextSibling = nextSibling;
            }
        }

        private static readonly ConditionalWeakTable<List<TypeTreeNode>, SkipTableEntry> SkipTableCache = new();

        /// <summary>
        /// Returns a precomputed next-sibling index skip table for <paramref name="nodes"/>,
        /// caching the O(N) scan per node list so subtree traversal avoids <see cref="List{T}"/> allocations.
        /// </summary>
        public static int[] GetNextSiblingTable(List<TypeTreeNode> nodes)
        {
            if (nodes == null)
            {
                throw new ArgumentNullException(nameof(nodes));
            }
            if (nodes.Count == 0)
            {
                return Array.Empty<int>();
            }

            if (SkipTableCache.TryGetValue(nodes, out var entry) && entry.NodeCount == nodes.Count)
            {
                return entry.NextSibling;
            }

            var nextSibling = ComputeNextSiblingTable(nodes);
            SkipTableCache.AddOrUpdate(nodes, new SkipTableEntry(nodes.Count, nextSibling));
            return nextSibling;
        }

        private static int[] ComputeNextSiblingTable(List<TypeTreeNode> nodes)
        {
            int count = nodes.Count;
            var nextSibling = new int[count];

            const int StackThreshold = 256;
            int[]? rentedStack = count > StackThreshold ? ArrayPool<int>.Shared.Rent(count) : null;
            Span<int> stack = rentedStack != null ? rentedStack.AsSpan(0, count) : stackalloc int[StackThreshold];

            try
            {
                int sp = 0;
                for (int i = 0; i < count; i++)
                {
                    int level = nodes[i].m_Level;
                    while (sp > 0 && nodes[stack[sp - 1]].m_Level >= level)
                    {
                        nextSibling[stack[--sp]] = i;
                    }
                    stack[sp++] = i;
                }

                while (sp > 0)
                {
                    nextSibling[stack[--sp]] = count;
                }

                return nextSibling;
            }
            finally
            {
                if (rentedStack != null)
                {
                    ArrayPool<int>.Shared.Return(rentedStack);
                }
            }
        }

        internal static void ValidateDepth(int depth)
        {
            if (depth > MaxRecursionDepth)
            {
                throw new InvalidDataException($"TypeTree traversal recursion depth exceeded limit ({MaxRecursionDepth}).");
            }
        }

        private static SerializedType? ResolveReferencedType(SerializedFile? assetsFile, string className, string nsName, string asmName)
        {
            var refTypes = assetsFile?.m_RefTypes;
            if (refTypes == null || string.IsNullOrEmpty(className))
            {
                return null;
            }

            for (int i = 0; i < refTypes.Count; i++)
            {
                var rt = refTypes[i];
                if (rt.m_KlassName == className && rt.m_NameSpace == nsName && rt.m_AsmName == asmName)
                {
                    return rt;
                }
            }

            for (int i = 0; i < refTypes.Count; i++)
            {
                var rt = refTypes[i];
                if (rt.m_KlassName == className && rt.m_NameSpace == nsName)
                {
                    return rt;
                }
            }

            return null;
        }

        public static string ReadTypeString(TypeTree m_Type, ObjectReader reader)
        {
            reader.Reset();
            var sb = new StringBuilder();
            var m_Nodes = m_Type.m_Nodes;
            var nextSibling = GetNextSiblingTable(m_Nodes);
            for (int i = 0; i < m_Nodes.Count; i++)
            {
                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref i, depth: 0, levelOffset: 0);
            }
            var readed = reader.Position - reader.byteStart;
            if (readed != reader.byteSize)
            {
                Logger.Info($"Error while read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }
            return sb.ToString();
        }

        private static void ReadStringValue(StringBuilder sb, List<TypeTreeNode> m_Nodes, int[] nextSibling, ObjectReader reader, ref int i, int depth, int levelOffset = 0)
        {
            ValidateDepth(depth);
            var m_Node = m_Nodes[i];
            var level = m_Node.m_Level + levelOffset;
            var varTypeStr = m_Node.m_Type;
            var varNameStr = m_Node.m_Name;
            string? formattedValue = null;
            var append = true;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    CheckObjectBytesAvailable(reader, 1);
                    formattedValue = reader.ReadSByte().ToString(CultureInfo.InvariantCulture);
                    break;
                case "UInt8":
                    CheckObjectBytesAvailable(reader, 1);
                    formattedValue = reader.ReadByte().ToString(CultureInfo.InvariantCulture);
                    break;
                case "char":
                    formattedValue = ReadCharValue(reader, m_Node.m_ByteSize).ToString();
                    break;
                case "short":
                case "SInt16":
                    CheckObjectBytesAvailable(reader, 2);
                    formattedValue = reader.ReadInt16().ToString(CultureInfo.InvariantCulture);
                    break;
                case "UInt16":
                case "unsigned short":
                    CheckObjectBytesAvailable(reader, 2);
                    formattedValue = reader.ReadUInt16().ToString(CultureInfo.InvariantCulture);
                    break;
                case "int":
                case "SInt32":
                    CheckObjectBytesAvailable(reader, 4);
                    formattedValue = reader.ReadInt32().ToString(CultureInfo.InvariantCulture);
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    CheckObjectBytesAvailable(reader, 4);
                    formattedValue = reader.ReadUInt32().ToString(CultureInfo.InvariantCulture);
                    break;
                case "long long":
                case "SInt64":
                    CheckObjectBytesAvailable(reader, 8);
                    formattedValue = reader.ReadInt64().ToString(CultureInfo.InvariantCulture);
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    CheckObjectBytesAvailable(reader, 8);
                    formattedValue = reader.ReadUInt64().ToString(CultureInfo.InvariantCulture);
                    break;
                case "float":
                    CheckObjectBytesAvailable(reader, 4);
                    formattedValue = reader.ReadSingle().ToString("G9", CultureInfo.InvariantCulture);
                    break;
                case "double":
                    CheckObjectBytesAvailable(reader, 8);
                    formattedValue = reader.ReadDouble().ToString("G17", CultureInfo.InvariantCulture);
                    break;
                case "bool":
                    CheckObjectBytesAvailable(reader, 1);
                    formattedValue = reader.ReadBoolean().ToString(CultureInfo.InvariantCulture);
                    break;
                case "string":
                    append = false;
                    CheckObjectBytesAvailable(reader, 4);
                    var str = reader.ReadAlignedString();
                    sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append(" = \"").Append(str).Append("\"\r\n");
                    i = nextSibling[i] - 1;
                    break;
                case "map":
                    {
                        int mapEnd = nextSibling[i];
                        if (i + 1 < mapEnd && (m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        bool pairAlign = i + 3 < mapEnd && (m_Nodes[i + 3].m_MetaFlag & 0x4000) != 0;
                        append = false;
                        sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                        sb.Append('\t', level + 1).Append("Array Array\r\n");
                        CheckObjectBytesAvailable(reader, 4);
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        sb.Append('\t', level + 1).Append("int size = ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                        int firstIndex = i + 4;
                        int secondIndex = firstIndex < mapEnd ? nextSibling[firstIndex] : mapEnd;
                        if (size > 0 && (firstIndex >= mapEnd || secondIndex >= mapEnd))
                        {
                            throw new InvalidDataException("Invalid map TypeTree node layout.");
                        }
                        i = mapEnd - 1;
                        for (int j = 0; j < size; j++)
                        {
                            sb.Append('\t', level + 2).Append('[').Append(j.ToString(CultureInfo.InvariantCulture)).Append("]\r\n");
                            sb.Append('\t', level + 2).Append("pair data\r\n");
                            int tmp1 = firstIndex;
                            int tmp2 = secondIndex;
                            ReadStringValue(sb, m_Nodes, nextSibling, reader, ref tmp1, depth + 1, levelOffset);
                            ReadStringValue(sb, m_Nodes, nextSibling, reader, ref tmp2, depth + 1, levelOffset);
                            if (pairAlign)
                            {
                                reader.AlignStream();
                            }
                        }
                        break;
                    }
                case "TypelessData":
                    {
                        append = false;
                        CheckObjectBytesAvailable(reader, 4);
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        AdvanceObjectBytes(reader, size);
                        i = nextSibling[i] - 1;
                        sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                        sb.Append('\t', level).Append("int size = ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                        break;
                    }
                case "ReferencedObject":
                    {
                        append = false;
                        sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                        int childStart = i + 1;
                        int childEnd = nextSibling[i];
                        i = childEnd - 1;

                        long rid = 0;
                        string className = string.Empty;
                        string nsName = string.Empty;
                        string asmName = string.Empty;

                        for (int j = childStart; j < childEnd; j++)
                        {
                            var childNode = m_Nodes[j];
                            if (childNode.m_Type == "ReferencedManagedType")
                            {
                                bool typeAlign = (childNode.m_MetaFlag & 0x4000) != 0;
                                int typeLevel = childNode.m_Level + levelOffset;
                                sb.Append('\t', typeLevel).Append(childNode.m_Type).Append(' ').Append(childNode.m_Name).Append("\r\n");
                                className = reader.ReadAlignedString();
                                sb.Append('\t', typeLevel + 1).Append("string class = \"").Append(className).Append("\"\r\n");
                                nsName = reader.ReadAlignedString();
                                sb.Append('\t', typeLevel + 1).Append("string ns = \"").Append(nsName).Append("\"\r\n");
                                asmName = reader.ReadAlignedString();
                                sb.Append('\t', typeLevel + 1).Append("string asm = \"").Append(asmName).Append("\"\r\n");
                                if (typeAlign)
                                {
                                    reader.AlignStream();
                                }
                                j = nextSibling[j] - 1;
                            }
                            else if (childNode.m_Type == "ReferencedObjectData")
                            {
                                bool dataAlign = (childNode.m_MetaFlag & 0x4000) != 0;
                                int dataLevel = childNode.m_Level + levelOffset;
                                sb.Append('\t', dataLevel).Append(childNode.m_Type).Append(' ').Append(childNode.m_Name).Append("\r\n");
                                if (!string.IsNullOrEmpty(className) && rid != -2)
                                {
                                    var refType = ResolveReferencedType(reader.assetsFile, className, nsName, asmName);
                                    if (refType?.m_Type?.m_Nodes is { Count: > 1 } refNodes)
                                    {
                                        var refNextSibling = GetNextSiblingTable(refNodes);
                                        for (int r = 1; r < refNodes.Count; r++)
                                        {
                                            if (refNodes[r].m_Type == "ManagedReferencesRegistry")
                                            {
                                                r = refNextSibling[r] - 1;
                                                continue;
                                            }
                                            ReadStringValue(sb, refNodes, refNextSibling, reader, ref r, depth + 2, dataLevel);
                                        }
                                        if ((refNodes[0].m_MetaFlag & 0x4000) != 0)
                                        {
                                            reader.AlignStream();
                                        }
                                    }
                                }
                                if (dataAlign)
                                {
                                    reader.AlignStream();
                                }
                                j = nextSibling[j] - 1;
                            }
                            else if (childNode.m_Name == "rid" && (childNode.m_Type == "SInt64" || childNode.m_Type == "long long"))
                            {
                                rid = reader.ReadInt64();
                                sb.Append('\t', childNode.m_Level + levelOffset).Append(childNode.m_Type).Append(" rid = ").Append(rid.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                                if ((childNode.m_MetaFlag & 0x4000) != 0)
                                {
                                    reader.AlignStream();
                                }
                            }
                            else
                            {
                                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref j, depth + 1, levelOffset);
                            }
                        }
                        break;
                    }
                default:
                    {
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            int arrayEnd = nextSibling[i];
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            append = false;
                            sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                            sb.Append('\t', level + 1).Append("Array Array\r\n");
                            CheckObjectBytesAvailable(reader, 4);
                            var size = reader.ReadInt32();
                            reader.CheckArrayLength(size);
                            sb.Append('\t', level + 1).Append("int size = ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                            int elemIndex = i + 3;
                            if (size > 0 && elemIndex >= arrayEnd)
                            {
                                throw new InvalidDataException("Invalid Array TypeTree node layout.");
                            }
                            i = arrayEnd - 1;
                            for (int j = 0; j < size; j++)
                            {
                                sb.Append('\t', level + 2).Append('[').Append(j.ToString(CultureInfo.InvariantCulture)).Append("]\r\n");
                                int tmp = elemIndex;
                                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref tmp, depth + 1, levelOffset);
                            }
                            break;
                        }
                        else //Class
                        {
                            append = false;
                            sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                            int childStart = i + 1;
                            int childEnd = nextSibling[i];
                            i = childEnd - 1;
                            for (int j = childStart; j < childEnd; j++)
                            {
                                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref j, depth + 1, levelOffset);
                            }
                            break;
                        }
                    }
            }
            if (append)
                sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append(" = ").Append(formattedValue).Append("\r\n");
            if (align)
                reader.AlignStream();
        }

        public static OrderedDictionary ReadType(TypeTree m_Types, ObjectReader reader)
        {
            reader.Reset();
            var obj = new OrderedDictionary();
            var m_Nodes = m_Types.m_Nodes;
            var nextSibling = GetNextSiblingTable(m_Nodes);
            for (int i = 1; i < m_Nodes.Count; i++)
            {
                var m_Node = m_Nodes[i];
                var varNameStr = m_Node.m_Name;
                obj[varNameStr] = ReadValue(m_Nodes, nextSibling, reader, ref i, depth: 0);
            }
            var readed = reader.Position - reader.byteStart;
            if (readed != reader.byteSize)
            {
                Logger.Info($"Error while read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }
            return obj;
        }

        public static bool TryReadType(TypeTree? m_Types, ObjectReader reader, out OrderedDictionary? result, out string? error)
        {
            result = null;
            error = null;
            if (m_Types?.m_Nodes == null || m_Types.m_Nodes.Count == 0)
            {
                error = "TypeTree is missing or empty.";
                return false;
            }

            try
            {
                result = ReadType(m_Types, reader);
                return result != null;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static object ReadValue(List<TypeTreeNode> m_Nodes, int[] nextSibling, ObjectReader reader, ref int i, int depth)
        {
            ValidateDepth(depth);
            var m_Node = m_Nodes[i];
            var varTypeStr = m_Node.m_Type;
            object value;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    CheckObjectBytesAvailable(reader, 1);
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    CheckObjectBytesAvailable(reader, 1);
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = ReadCharValue(reader, m_Node.m_ByteSize);
                    break;
                case "short":
                case "SInt16":
                    CheckObjectBytesAvailable(reader, 2);
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    CheckObjectBytesAvailable(reader, 2);
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    CheckObjectBytesAvailable(reader, 4);
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    CheckObjectBytesAvailable(reader, 4);
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    CheckObjectBytesAvailable(reader, 8);
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    CheckObjectBytesAvailable(reader, 8);
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    CheckObjectBytesAvailable(reader, 4);
                    value = reader.ReadSingle();
                    break;
                case "double":
                    CheckObjectBytesAvailable(reader, 8);
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    CheckObjectBytesAvailable(reader, 1);
                    value = reader.ReadBoolean();
                    break;
                case "string":
                    CheckObjectBytesAvailable(reader, 4);
                    value = reader.ReadAlignedString();
                    i = nextSibling[i] - 1;
                    break;
                case "map":
                    {
                        int mapEnd = nextSibling[i];
                        if (i + 1 < mapEnd && (m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        bool pairAlign = i + 3 < mapEnd && (m_Nodes[i + 3].m_MetaFlag & 0x4000) != 0;
                        int firstIndex = i + 4;
                        int secondIndex = firstIndex < mapEnd ? nextSibling[firstIndex] : mapEnd;
                        i = mapEnd - 1;
                        CheckObjectBytesAvailable(reader, 4);
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        if (size > 0 && (firstIndex >= mapEnd || secondIndex >= mapEnd))
                        {
                            throw new InvalidDataException("Invalid map TypeTree node layout.");
                        }
                        var dic = new List<KeyValuePair<object, object>>(size);
                        for (int j = 0; j < size; j++)
                        {
                            int tmp1 = firstIndex;
                            int tmp2 = secondIndex;
                            dic.Add(new KeyValuePair<object, object>(
                                ReadValue(m_Nodes, nextSibling, reader, ref tmp1, depth + 1),
                                ReadValue(m_Nodes, nextSibling, reader, ref tmp2, depth + 1)));
                            if (pairAlign)
                            {
                                reader.AlignStream();
                            }
                        }
                        value = dic;
                        break;
                    }
                case "TypelessData":
                    {
                        CheckObjectBytesAvailable(reader, 4);
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        value = reader.ReadBytes(size);
                        i = nextSibling[i] - 1;
                        break;
                    }
                case "ReferencedObject":
                    {
                        int childStart = i + 1;
                        int childEnd = nextSibling[i];
                        i = childEnd - 1;
                        var obj = new OrderedDictionary();
                        long rid = 0;
                        string className = string.Empty;
                        string nsName = string.Empty;
                        string asmName = string.Empty;

                        for (int j = childStart; j < childEnd; j++)
                        {
                            var childNode = m_Nodes[j];
                            if (childNode.m_Type == "ReferencedManagedType")
                            {
                                bool typeAlign = (childNode.m_MetaFlag & 0x4000) != 0;
                                className = reader.ReadAlignedString();
                                nsName = reader.ReadAlignedString();
                                asmName = reader.ReadAlignedString();
                                if (typeAlign)
                                {
                                    reader.AlignStream();
                                }
                                var typeDict = new OrderedDictionary
                                {
                                    ["class"] = className,
                                    ["ns"] = nsName,
                                    ["asm"] = asmName
                                };
                                obj[childNode.m_Name] = typeDict;
                                j = nextSibling[j] - 1;
                            }
                            else if (childNode.m_Type == "ReferencedObjectData")
                            {
                                bool dataAlign = (childNode.m_MetaFlag & 0x4000) != 0;
                                var dataDict = new OrderedDictionary();
                                if (!string.IsNullOrEmpty(className) && rid != -2)
                                {
                                    var refType = ResolveReferencedType(reader.assetsFile, className, nsName, asmName);
                                    if (refType?.m_Type?.m_Nodes is { Count: > 1 } refNodes)
                                    {
                                        var refNextSibling = GetNextSiblingTable(refNodes);
                                        for (int r = 1; r < refNodes.Count; r++)
                                        {
                                            var refMember = refNodes[r];
                                            if (refMember.m_Type == "ManagedReferencesRegistry")
                                            {
                                                r = refNextSibling[r] - 1;
                                                continue;
                                            }
                                            dataDict[refMember.m_Name] = ReadValue(refNodes, refNextSibling, reader, ref r, depth + 2);
                                        }
                                        if ((refNodes[0].m_MetaFlag & 0x4000) != 0)
                                        {
                                            reader.AlignStream();
                                        }
                                    }
                                }
                                if (dataAlign)
                                {
                                    reader.AlignStream();
                                }
                                obj[childNode.m_Name] = dataDict;
                                j = nextSibling[j] - 1;
                            }
                            else if (childNode.m_Name == "rid" && (childNode.m_Type == "SInt64" || childNode.m_Type == "long long"))
                            {
                                rid = reader.ReadInt64();
                                if ((childNode.m_MetaFlag & 0x4000) != 0)
                                {
                                    reader.AlignStream();
                                }
                                obj[childNode.m_Name] = rid;
                            }
                            else
                            {
                                obj[childNode.m_Name] = ReadValue(m_Nodes, nextSibling, reader, ref j, depth + 1);
                            }
                        }
                        value = obj;
                        break;
                    }
                default:
                    {
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            int arrayEnd = nextSibling[i];
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            int elemIndex = i + 3;
                            i = arrayEnd - 1;
                            CheckObjectBytesAvailable(reader, 4);
                            var size = reader.ReadInt32();
                            reader.CheckArrayLength(size);
                            if (size > 0 && elemIndex >= arrayEnd)
                            {
                                throw new InvalidDataException("Invalid Array TypeTree node layout.");
                            }
                            var list = new List<object>(size);
                            for (int j = 0; j < size; j++)
                            {
                                int tmp = elemIndex;
                                list.Add(ReadValue(m_Nodes, nextSibling, reader, ref tmp, depth + 1));
                            }
                            value = list;
                            break;
                        }
                        else //Class
                        {
                            int childStart = i + 1;
                            int childEnd = nextSibling[i];
                            i = childEnd - 1;
                            var obj = new OrderedDictionary();
                            for (int j = childStart; j < childEnd; j++)
                            {
                                var classmember = m_Nodes[j];
                                var name = classmember.m_Name;
                                obj[name] = ReadValue(m_Nodes, nextSibling, reader, ref j, depth + 1);
                            }
                            value = obj;
                            break;
                        }
                    }
            }
            if (align)
                reader.AlignStream();
            return value;
        }

        private static char ReadCharValue(ObjectReader reader, int byteSize = 2)
        {
            if (byteSize == 1)
            {
                CheckObjectBytesAvailable(reader, 1);
                return (char)reader.ReadByte();
            }

            CheckObjectBytesAvailable(reader, 2);
            return (char)reader.ReadUInt16();
        }

        private static void CheckObjectBytesAvailable(ObjectReader reader, long count)
        {
            if (count < 0)
            {
                throw new OverflowException($"Invalid byte length: {count}");
            }

            long remainingStream = reader.BaseStream.Length - reader.BaseStream.Position;
            if (count > remainingStream)
            {
                throw new EndOfStreamException($"Trying to read/skip {count} bytes, but only {remainingStream} bytes remain in stream.");
            }

            long remainingObj = reader.byteSize - (reader.Position - reader.byteStart);
            if (count > remainingObj)
            {
                throw new EndOfStreamException($"Trying to read/skip {count} bytes, but only {remainingObj} bytes remain in object.");
            }
        }

        private static void AdvanceObjectBytes(ObjectReader reader, long count)
        {
            CheckObjectBytesAvailable(reader, count);
            if (count > 0)
            {
                reader.Position += count;
            }
        }

        internal static bool IsClassNode(List<TypeTreeNode> nodes, int index)
        {
            var typeName = nodes[index].m_Type;
            switch (typeName)
            {
                case "SInt8":
                case "UInt8":
                case "char":
                case "short":
                case "SInt16":
                case "UInt16":
                case "unsigned short":
                case "int":
                case "SInt32":
                case "UInt32":
                case "unsigned int":
                case "Type*":
                case "long long":
                case "SInt64":
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                case "float":
                case "double":
                case "bool":
                case "string":
                case "map":
                case "TypelessData":
                    return false;
                default:
                    return !(index < nodes.Count - 1 && nodes[index + 1].m_Type == "Array");
            }
        }

        private static bool TryGetFixedPrimitiveByteSize(TypeTreeNode node, out int byteSize)
        {
            var typeName = node.m_Type;
            switch (typeName)
            {
                case "SInt8":
                case "UInt8":
                case "bool":
                    byteSize = 1;
                    return true;
                case "char":
                    byteSize = node.m_ByteSize == 1 ? 1 : 2;
                    return true;
                case "short":
                case "SInt16":
                case "UInt16":
                case "unsigned short":
                    byteSize = 2;
                    return true;
                case "int":
                case "SInt32":
                case "UInt32":
                case "unsigned int":
                case "Type*":
                case "float":
                    byteSize = 4;
                    return true;
                case "long long":
                case "SInt64":
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                case "double":
                    byteSize = 8;
                    return true;
                default:
                    byteSize = 0;
                    return false;
            }
        }

        /// <summary>
        /// Streaming skip: advances <paramref name="reader"/> past the value at <paramref name="index"/>
        /// without materializing <see cref="OrderedDictionary"/> or allocating <c>TypelessData</c> byte arrays.
        /// Advances <paramref name="index"/> to the last node index belonging to the skipped subtree.
        /// </summary>
        public static void SkipNode(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            var typeName = node.m_Type;
            bool align = (node.m_MetaFlag & 0x4000) != 0;

            if (TryGetFixedPrimitiveByteSize(node, out int scalarBytes))
            {
                AdvanceObjectBytes(reader, scalarBytes);
            }
            else
            {
                switch (typeName)
                {
                    case "string":
                        SkipAlignedString(reader);
                        index = nextSibling[index] - 1;
                        break;
                    case "map":
                        {
                            int mapEnd = nextSibling[index];
                            if (index + 1 < mapEnd && (nodes[index + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            bool pairAlign = index + 3 < mapEnd && (nodes[index + 3].m_MetaFlag & 0x4000) != 0;
                            int firstIndex = index + 4;
                            int secondIndex = firstIndex < mapEnd ? nextSibling[firstIndex] : mapEnd;
                            index = mapEnd - 1;
                            int size = reader.ReadInt32();
                            reader.CheckArrayLength(size);
                            if (size > 0 && (firstIndex >= mapEnd || secondIndex >= mapEnd))
                            {
                                throw new InvalidDataException("Invalid map TypeTree node layout.");
                            }
                            for (int j = 0; j < size; j++)
                            {
                                int tmp1 = firstIndex;
                                int tmp2 = secondIndex;
                                SkipNode(nodes, nextSibling, reader, ref tmp1, depth + 1);
                                SkipNode(nodes, nextSibling, reader, ref tmp2, depth + 1);
                                if (pairAlign)
                                {
                                    reader.AlignStream();
                                }
                            }
                            break;
                        }
                    case "TypelessData":
                        {
                            int size = reader.ReadInt32();
                            if (size < 0)
                            {
                                throw new OverflowException($"Invalid TypelessData size: {size}");
                            }
                            AdvanceObjectBytes(reader, size);
                            index = nextSibling[index] - 1;
                            break;
                        }
                    case "ReferencedObject":
                        {
                            int childStart = index + 1;
                            int childEnd = nextSibling[index];
                            index = childEnd - 1;
                            long rid = 0;
                            string className = string.Empty;
                            string nsName = string.Empty;
                            string asmName = string.Empty;

                            for (int j = childStart; j < childEnd; j++)
                            {
                                var childNode = nodes[j];
                                if (childNode.m_Type == "ReferencedManagedType")
                                {
                                    bool typeAlign = (childNode.m_MetaFlag & 0x4000) != 0;
                                    className = reader.ReadAlignedString();
                                    nsName = reader.ReadAlignedString();
                                    asmName = reader.ReadAlignedString();
                                    if (typeAlign)
                                    {
                                        reader.AlignStream();
                                    }
                                    j = nextSibling[j] - 1;
                                }
                                else if (childNode.m_Type == "ReferencedObjectData")
                                {
                                    bool dataAlign = (childNode.m_MetaFlag & 0x4000) != 0;
                                    if (!string.IsNullOrEmpty(className) && rid != -2)
                                    {
                                        var refType = ResolveReferencedType(reader.assetsFile, className, nsName, asmName);
                                        if (refType?.m_Type?.m_Nodes is { Count: > 1 } refNodes)
                                        {
                                            var refNextSibling = GetNextSiblingTable(refNodes);
                                            for (int r = 1; r < refNodes.Count; r++)
                                            {
                                                if (refNodes[r].m_Type == "ManagedReferencesRegistry")
                                                {
                                                    r = refNextSibling[r] - 1;
                                                    continue;
                                                }
                                                SkipNode(refNodes, refNextSibling, reader, ref r, depth + 2);
                                            }
                                            if ((refNodes[0].m_MetaFlag & 0x4000) != 0)
                                            {
                                                reader.AlignStream();
                                            }
                                        }
                                    }
                                    if (dataAlign)
                                    {
                                        reader.AlignStream();
                                    }
                                    j = nextSibling[j] - 1;
                                }
                                else if (childNode.m_Name == "rid" && (childNode.m_Type == "SInt64" || childNode.m_Type == "long long"))
                                {
                                    rid = reader.ReadInt64();
                                    if ((childNode.m_MetaFlag & 0x4000) != 0)
                                    {
                                        reader.AlignStream();
                                    }
                                }
                                else
                                {
                                    SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                                }
                            }
                            break;
                        }
                    default:
                        {
                            if (index < nodes.Count - 1 && nodes[index + 1].m_Type == "Array")
                            {
                                int arrayEnd = nextSibling[index];
                                if ((nodes[index + 1].m_MetaFlag & 0x4000) != 0)
                                    align = true;
                                int elemIndex = index + 3;
                                index = arrayEnd - 1;
                                int size = reader.ReadInt32();
                                reader.CheckArrayLength(size);
                                if (size > 0 && elemIndex >= arrayEnd)
                                {
                                    throw new InvalidDataException("Invalid Array TypeTree node layout.");
                                }
                                if (size > 0
                                    && (nodes[elemIndex].m_MetaFlag & 0x4000) == 0
                                    && TryGetFixedPrimitiveByteSize(nodes[elemIndex], out int elemBytes))
                                {
                                    AdvanceObjectBytes(reader, (long)size * elemBytes);
                                }
                                else
                                {
                                    for (int j = 0; j < size; j++)
                                    {
                                        int tmp = elemIndex;
                                        SkipNode(nodes, nextSibling, reader, ref tmp, depth + 1);
                                    }
                                }
                            }
                            else
                            {
                                int childStart = index + 1;
                                int childEnd = nextSibling[index];
                                index = childEnd - 1;
                                for (int j = childStart; j < childEnd; j++)
                                {
                                    SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                                }
                            }
                            break;
                        }
                }
            }

            if (align)
                reader.AlignStream();
        }

        private static void SkipAlignedString(ObjectReader reader)
        {
            int length = reader.ReadInt32();
            if (length > 0 && length <= reader.BaseStream.Length - reader.BaseStream.Position)
            {
                AdvanceObjectBytes(reader, length);
                reader.AlignStream(4);
            }
        }

        /// <summary>
        /// Skips a <c>TypelessData</c> or byte-array node without allocating, returning its stream offset and byte size.
        /// </summary>
        public static bool TrySkipBlob(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            out long offset,
            out int size,
            int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            var typeName = node.m_Type;
            bool align = (node.m_MetaFlag & 0x4000) != 0;

            if (typeName == "TypelessData")
            {
                int blobSize = reader.ReadInt32();
                if (blobSize < 0)
                {
                    throw new OverflowException($"Invalid TypelessData size: {blobSize}");
                }
                CheckObjectBytesAvailable(reader, blobSize);
                offset = reader.Position;
                size = blobSize;
                if (blobSize > 0)
                {
                    reader.Position += blobSize;
                }
                if (align)
                    reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            if (index < nodes.Count - 1 && nodes[index + 1].m_Type == "Array")
            {
                int arrayEnd = nextSibling[index];
                int elemIndex = index + 3;
                if (elemIndex < arrayEnd
                    && (nodes[elemIndex].m_Type == "UInt8" || nodes[elemIndex].m_Type == "SInt8")
                    && (nodes[elemIndex].m_MetaFlag & 0x4000) == 0)
                {
                    if ((nodes[index + 1].m_MetaFlag & 0x4000) != 0)
                        align = true;
                    int blobSize = reader.ReadInt32();
                    if (blobSize < 0)
                    {
                        throw new OverflowException($"Invalid byte array size: {blobSize}");
                    }
                    CheckObjectBytesAvailable(reader, blobSize);
                    offset = reader.Position;
                    size = blobSize;
                    if (blobSize > 0)
                    {
                        reader.Position += blobSize;
                    }
                    if (align)
                        reader.AlignStream();
                    index = arrayEnd - 1;
                    return true;
                }
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            offset = 0;
            size = 0;
            return false;
        }

        internal static bool TryReadInt32(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, out int value, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            bool align = (node.m_MetaFlag & 0x4000) != 0;
            bool ok = true;
            switch (node.m_Type)
            {
                case "SInt8": value = reader.ReadSByte(); break;
                case "UInt8": value = reader.ReadByte(); break;
                case "char": value = ReadCharValue(reader); break;
                case "short":
                case "SInt16": value = reader.ReadInt16(); break;
                case "UInt16":
                case "unsigned short": value = reader.ReadUInt16(); break;
                case "int":
                case "SInt32": value = reader.ReadInt32(); break;
                case "UInt32":
                case "unsigned int":
                case "Type*": value = checked((int)reader.ReadUInt32()); break;
                case "long long":
                case "SInt64": value = checked((int)reader.ReadInt64()); break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": value = checked((int)reader.ReadUInt64()); break;
                case "float": value = Convert.ToInt32(reader.ReadSingle()); break;
                case "double": value = Convert.ToInt32(reader.ReadDouble()); break;
                case "bool": value = reader.ReadBoolean() ? 1 : 0; break;
                default:
                    ok = false;
                    value = 0;
                    break;
            }

            if (ok)
            {
                if (align) reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            return false;
        }

        internal static bool TryReadUInt32(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, out uint value, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            bool align = (node.m_MetaFlag & 0x4000) != 0;
            bool ok = true;
            switch (node.m_Type)
            {
                case "SInt8": value = checked((uint)reader.ReadSByte()); break;
                case "UInt8": value = reader.ReadByte(); break;
                case "char": value = ReadCharValue(reader); break;
                case "short":
                case "SInt16": value = checked((uint)reader.ReadInt16()); break;
                case "UInt16":
                case "unsigned short": value = reader.ReadUInt16(); break;
                case "int":
                case "SInt32": value = checked((uint)reader.ReadInt32()); break;
                case "UInt32":
                case "unsigned int":
                case "Type*": value = reader.ReadUInt32(); break;
                case "long long":
                case "SInt64": value = checked((uint)reader.ReadInt64()); break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": value = checked((uint)reader.ReadUInt64()); break;
                case "float": value = Convert.ToUInt32(reader.ReadSingle()); break;
                case "double": value = Convert.ToUInt32(reader.ReadDouble()); break;
                case "bool": value = reader.ReadBoolean() ? 1u : 0u; break;
                default:
                    ok = false;
                    value = 0;
                    break;
            }

            if (ok)
            {
                if (align) reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            return false;
        }

        internal static bool TryReadInt64(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, out long value, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            bool align = (node.m_MetaFlag & 0x4000) != 0;
            bool ok = true;
            switch (node.m_Type)
            {
                case "SInt8": value = reader.ReadSByte(); break;
                case "UInt8": value = reader.ReadByte(); break;
                case "char": value = ReadCharValue(reader); break;
                case "short":
                case "SInt16": value = reader.ReadInt16(); break;
                case "UInt16":
                case "unsigned short": value = reader.ReadUInt16(); break;
                case "int":
                case "SInt32": value = reader.ReadInt32(); break;
                case "UInt32":
                case "unsigned int":
                case "Type*": value = reader.ReadUInt32(); break;
                case "long long":
                case "SInt64": value = reader.ReadInt64(); break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": value = checked((long)reader.ReadUInt64()); break;
                case "float": value = Convert.ToInt64(reader.ReadSingle()); break;
                case "double": value = Convert.ToInt64(reader.ReadDouble()); break;
                case "bool": value = reader.ReadBoolean() ? 1L : 0L; break;
                default:
                    ok = false;
                    value = 0;
                    break;
            }

            if (ok)
            {
                if (align) reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            return false;
        }

        internal static bool TryReadSingle(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, out float value, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            bool align = (node.m_MetaFlag & 0x4000) != 0;
            bool ok = true;
            switch (node.m_Type)
            {
                case "SInt8": value = reader.ReadSByte(); break;
                case "UInt8": value = reader.ReadByte(); break;
                case "char": value = ReadCharValue(reader); break;
                case "short":
                case "SInt16": value = reader.ReadInt16(); break;
                case "UInt16":
                case "unsigned short": value = reader.ReadUInt16(); break;
                case "int":
                case "SInt32": value = reader.ReadInt32(); break;
                case "UInt32":
                case "unsigned int":
                case "Type*": value = reader.ReadUInt32(); break;
                case "long long":
                case "SInt64": value = reader.ReadInt64(); break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": value = reader.ReadUInt64(); break;
                case "float": value = reader.ReadSingle(); break;
                case "double": value = (float)reader.ReadDouble(); break;
                case "bool": value = reader.ReadBoolean() ? 1f : 0f; break;
                default:
                    ok = false;
                    value = 0f;
                    break;
            }

            if (ok)
            {
                if (align) reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            return false;
        }

        internal static bool TryReadBoolean(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, out bool value, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            bool align = (node.m_MetaFlag & 0x4000) != 0;
            bool ok = true;
            switch (node.m_Type)
            {
                case "SInt8": value = reader.ReadSByte() != 0; break;
                case "UInt8": value = reader.ReadByte() != 0; break;
                case "char": value = ReadCharValue(reader) != 0; break;
                case "short":
                case "SInt16": value = reader.ReadInt16() != 0; break;
                case "UInt16":
                case "unsigned short": value = reader.ReadUInt16() != 0; break;
                case "int":
                case "SInt32": value = reader.ReadInt32() != 0; break;
                case "UInt32":
                case "unsigned int":
                case "Type*": value = reader.ReadUInt32() != 0; break;
                case "long long":
                case "SInt64": value = reader.ReadInt64() != 0; break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize": value = reader.ReadUInt64() != 0; break;
                case "float": value = reader.ReadSingle() != 0f; break;
                case "double": value = reader.ReadDouble() != 0d; break;
                case "bool": value = reader.ReadBoolean(); break;
                default:
                    ok = false;
                    value = false;
                    break;
            }

            if (ok)
            {
                if (align) reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            return false;
        }

        internal static bool TryReadString(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, out string value, int depth = 0)
        {
            ValidateDepth(depth);
            var node = nodes[index];
            if (node.m_Type == "string")
            {
                bool align = (node.m_MetaFlag & 0x4000) != 0;
                value = reader.ReadAlignedString();
                if (align) reader.AlignStream();
                index = nextSibling[index] - 1;
                return true;
            }

            SkipNode(nodes, nextSibling, reader, ref index, depth);
            value = string.Empty;
            return false;
        }

        private static readonly byte[] s_syntheticZeroBuffer = new byte[512];

        internal static ObjectReader CreateSyntheticZeroObjectReader(ObjectReader template, uint byteSize)
        {
            var length = (int)Math.Min(Math.Max(byteSize, 1u), (uint)s_syntheticZeroBuffer.Length);
            var stream = new MemoryStream(s_syntheticZeroBuffer, 0, length, writable: false, publiclyVisible: true);
            var endianReader = new EndianBinaryReader(stream, template.Endian);
            var info = new ObjectInfo
            {
                m_PathID = template.m_PathID,
                byteStart = 0,
                byteSize = (uint)length,
                classID = (int)template.type,
                serializedType = template.serializedType
            };
            return new ObjectReader(endianReader, template.assetsFile, info);
        }

        internal static RendererBinaryProbeResult ProbeRendererBinaryLayout(ObjectReader reader, bool isSkinnedMeshRenderer)
        {
            var savedPos = reader.Position;
            var typeName = isSkinnedMeshRenderer ? nameof(SkinnedMeshRenderer) : nameof(MeshRenderer);
            var version = reader.version;
            var hasTypeTree = reader.serializedType?.m_Type?.m_Nodes is { Count: > 0 };

            try
            {
                if (version == null || version.Length < 2)
                {
                    return RendererBinaryProbeResult.Fallback($"{typeName} has invalid Unity version metadata.");
                }

                if (hasTypeTree && (version[0] <= 0 || version[0] >= 7000))
                {
                    return RendererBinaryProbeResult.Fallback($"{typeName} uses version {reader.assetsFile?.unityVersion ?? "unknown"} with embedded TypeTree.");
                }

                long start = reader.byteStart;
                long streamLength = reader.BaseStream.CanSeek ? reader.BaseStream.Length : long.MaxValue;
                if (start < 0 || start > streamLength)
                {
                    return RendererBinaryProbeResult.Fallback($"{typeName} byteStart {start} is out of stream bounds ({streamLength}).");
                }

                long declaredEnd = start + reader.byteSize;
                long limit = Math.Min(declaredEnd, streamLength);
                long pos = start;
                int pptrSize = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;

                static bool Advance(ref long p, long count, long lim)
                {
                    if (count < 0 || count > lim - p)
                    {
                        return false;
                    }
                    p += count;
                    return true;
                }

                static bool Align4(ref long p, long lim)
                {
                    var mod = p & 3L;
                    if (mod != 0)
                    {
                        var pad = 4L - mod;
                        if (pad > lim - p)
                        {
                            return false;
                        }
                        p += pad;
                    }
                    return true;
                }

                bool ReadArrayCount(ref long p, int elemSize, out int count)
                {
                    count = 0;
                    if (4 > limit - p)
                    {
                        return false;
                    }

                    reader.Position = p;
                    count = reader.ReadInt32();
                    p += 4;
                    if (count < 0)
                    {
                        return false;
                    }

                    long bytesNeeded = (long)count * elemSize;
                    if (bytesNeeded < 0 || bytesNeeded > limit - p)
                    {
                        return false;
                    }

                    // Enforce the same 10M guard as BinaryReaderExtensions.CheckArrayLength when small elements could slip past.
                    if (count > 10_000_000 && bytesNeeded > reader.byteSize)
                    {
                        return false;
                    }

                    p += bytesNeeded;
                    return true;
                }

                // Object
                if (reader.platform == BuildTarget.NoTarget)
                {
                    if (!Advance(ref pos, 4, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Object.m_ObjectHideFlags.");
                    }
                    // EditorExtension
                    if (!Advance(ref pos, 2L * pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated EditorExtension prefab PPtrs.");
                    }
                }

                // Component.m_GameObject
                if (!Advance(ref pos, pptrSize, limit))
                {
                    return RendererBinaryProbeResult.Fallback("Truncated Component.m_GameObject.");
                }

                // Renderer header
                if (version[0] < 5)
                {
                    if (!Advance(ref pos, 4, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated legacy Renderer header.");
                    }
                }
                else
                {
                    if (version[0] > 5 || (version[0] == 5 && version[1] >= 4))
                    {
                        int flagBytes = 3; // m_Enabled, m_CastShadows, m_ReceiveShadows
                        if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 2)) flagBytes += 1; // m_DynamicOccludee
                        if (version[0] >= 2021) flagBytes += 1; // m_StaticShadowCaster
                        flagBytes += 3; // m_MotionVectors, m_LightProbeUsage, m_ReflectionProbeUsage
                        if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) flagBytes += 1; // m_RayTracingMode
                        if (version[0] >= 2020) flagBytes += 1; // m_RayTraceProcedural
                        if (version[0] > 2023 || (version[0] == 2023 && version[1] >= 2)) flagBytes += 2; // RayTracingAccelStructBuildFlags
                        if (version[0] >= 6000) flagBytes += 1; // m_SmallMeshCulling
                        if (!Advance(ref pos, flagBytes, limit) || !Align4(ref pos, limit))
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated Renderer flags header.");
                        }
                    }
                    else
                    {
                        if (!Advance(ref pos, 1, limit) || !Align4(ref pos, limit)
                            || !Advance(ref pos, 2, limit) || !Align4(ref pos, limit))
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated Unity 5.0-5.3 Renderer flags.");
                        }
                    }

                    if (version[0] > 6000 || (version[0] == 6000 && version[1] >= 2))
                    {
                        if (!Advance(ref pos, 2, limit) || !Align4(ref pos, limit) || !Advance(ref pos, 4, limit))
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated Unity 6000.2+ MeshLod fields.");
                        }
                    }

                    if (version[0] >= 2018 && !Advance(ref pos, 4, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_RenderingLayerMask.");
                    }

                    if ((version[0] > 2018 || (version[0] == 2018 && version[1] >= 3)) && !Advance(ref pos, 4, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_RendererPriority.");
                    }

                    if (!Advance(ref pos, 4, limit)) // m_LightmapIndex + m_LightmapIndexDynamic
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer lightmap indices.");
                    }
                }

                if (version[0] >= 3 && !Advance(ref pos, 16, limit))
                {
                    return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_LightmapTilingOffset.");
                }

                if (version[0] >= 5 && !Advance(ref pos, 16, limit))
                {
                    return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_LightmapTilingOffsetDynamic.");
                }

                // Renderer.m_Materials
                if (!ReadArrayCount(ref pos, pptrSize, out _))
                {
                    return RendererBinaryProbeResult.Fallback("Invalid or out-of-bounds Renderer.m_Materials array.");
                }

                if (version[0] < 3)
                {
                    if (!Advance(ref pos, 16, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated pre-Unity3 lightmap tiling offset.");
                    }
                }
                else
                {
                    if (version[0] > 5 || (version[0] == 5 && version[1] >= 5))
                    {
                        if (!Advance(ref pos, 4, limit)) // StaticBatchInfo
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_StaticBatchInfo.");
                        }
                    }
                    else
                    {
                        if (!ReadArrayCount(ref pos, 4, out _)) // m_SubsetIndices
                        {
                            return RendererBinaryProbeResult.Fallback("Invalid or out-of-bounds Renderer.m_SubsetIndices array.");
                        }
                    }

                    if (!Advance(ref pos, pptrSize, limit)) // m_StaticBatchRoot
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_StaticBatchRoot.");
                    }
                }

                if (version[0] > 5 || (version[0] == 5 && version[1] >= 4))
                {
                    if (!Advance(ref pos, 2L * pptrSize, limit)) // m_ProbeAnchor + m_LightProbeVolumeOverride
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer probe anchor PPtrs.");
                    }
                }
                else if (version[0] > 3 || (version[0] == 3 && version[1] >= 5))
                {
                    if (!Advance(ref pos, 1, limit) || !Align4(ref pos, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_UseLightProbes.");
                    }
                    if (version[0] >= 5 && !Advance(ref pos, 4, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_ReflectionProbeUsage.");
                    }
                    if (!Advance(ref pos, pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer.m_LightProbeAnchor.");
                    }
                }

                if (version[0] > 4 || (version[0] == 4 && version[1] >= 3))
                {
                    int sortBytes = (version[0] == 4 && version[1] == 3) ? 2 : 4;
                    if (version[0] > 5 || (version[0] == 5 && version[1] >= 6))
                    {
                        sortBytes += 2; // m_SortingLayer
                    }
                    sortBytes += 2; // m_SortingOrder
                    if (!Advance(ref pos, sortBytes, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer sorting fields.");
                    }

                    // AlignStream after m_SortingOrder: only require pad inside limit if more fields follow.
                    long postSortUnaligned = pos;
                    if (!Align4(ref pos, streamLength))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated Renderer post-sorting alignment.");
                    }

                    if (version[0] > 6000 || (version[0] == 6000 && version[1] >= 3))
                    {
                        if (pos > limit || !Advance(ref pos, 4, limit))
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated Unity 6000.3+ Renderer.m_MaskInteraction.");
                        }
                    }
                    else if (!isSkinnedMeshRenderer && version[0] < 5 && pos > limit)
                    {
                        pos = postSortUnaligned;
                    }
                }

                long unalignedFinalEnd = pos;
                if (!isSkinnedMeshRenderer)
                {
                    if (version[0] >= 5 && !Advance(ref pos, pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated MeshRenderer.m_AdditionalVertexStreams.");
                    }
                    if (version[0] >= 2020 && !Advance(ref pos, pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated MeshRenderer.m_EnlightenVertexStream.");
                    }
                    unalignedFinalEnd = pos;
                }
                else
                {
                    if (!Advance(ref pos, 6, limit) || !Align4(ref pos, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated SkinnedMeshRenderer header.");
                    }
                    if (version[0] == 2 && version[1] < 6 && !Advance(ref pos, pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated SkinnedMeshRenderer.m_DisableAnimationWhenOffscreen.");
                    }
                    if (!Advance(ref pos, pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated SkinnedMeshRenderer.m_Mesh.");
                    }
                    if (!ReadArrayCount(ref pos, pptrSize, out _))
                    {
                        return RendererBinaryProbeResult.Fallback("Invalid or out-of-bounds SkinnedMeshRenderer.m_Bones array.");
                    }
                    if ((version[0] > 4 || (version[0] == 4 && version[1] >= 3)) && !ReadArrayCount(ref pos, 4, out _))
                    {
                        return RendererBinaryProbeResult.Fallback("Invalid or out-of-bounds SkinnedMeshRenderer.m_BlendShapeWeights array.");
                    }
                    if ((version[0] > 3 || (version[0] == 3 && version[1] >= 5)) && !Advance(ref pos, pptrSize, limit))
                    {
                        return RendererBinaryProbeResult.Fallback("Truncated SkinnedMeshRenderer.m_RootBone.");
                    }
                    unalignedFinalEnd = pos;
                    if (version[0] > 3 || (version[0] == 3 && version[1] >= 4))
                    {
                        if (!Advance(ref pos, 25, limit)) // AABB (24) + m_DirtyAABB (1)
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated SkinnedMeshRenderer.m_AABB/m_DirtyAABB.");
                        }
                        unalignedFinalEnd = pos;
                        if (!Align4(ref pos, streamLength))
                        {
                            return RendererBinaryProbeResult.Fallback("Truncated SkinnedMeshRenderer final alignment.");
                        }
                    }
                }

                // When an embedded TypeTree is present, detect drifted layouts where binary deserialization
                // did not consume the exact declared object byteSize (e.g., extra/missing fields).
                if (hasTypeTree)
                {
                    long consumedAligned = pos - start;
                    long consumedUnaligned = unalignedFinalEnd - start;
                    if (consumedAligned != reader.byteSize && consumedUnaligned != reader.byteSize)
                    {
                        return RendererBinaryProbeResult.Fallback(
                            $"{typeName} binary layout mismatch (consumed {consumedAligned} bytes, declared byteSize {reader.byteSize}).");
                    }
                }

                return RendererBinaryProbeResult.DirectBinary;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                return RendererBinaryProbeResult.Fallback($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                reader.Position = savedPos;
            }
        }

        internal static void PopulateRendererFromTypeTree(Renderer target, OrderedDictionary dict, ObjectReader templateReader)
        {
            if (TryExtractPPtr<GameObject>(dict, "m_GameObject", target.assetsFile, out var gameObject) && gameObject != null)
            {
                target.m_GameObject = gameObject;
            }
            else
            {
                target.m_GameObject ??= new PPtr<GameObject>(0, 0, target.assetsFile);
            }

            if (TryExtractBoolean(dict, "m_Enabled", out var enabled))
            {
                target.m_Enabled = enabled;
            }

            target.m_Materials = TryExtractPPtrArray<Material>(dict, "m_Materials", target.assetsFile, out var materials)
                ? materials
                : Array.Empty<PPtr<Material>>();

            if (dict.Contains("m_StaticBatchInfo") && dict["m_StaticBatchInfo"] is IDictionary batchDict)
            {
                TryExtractUInt16(batchDict, "firstSubMesh", out var firstSubMesh);
                TryExtractUInt16(batchDict, "subMeshCount", out var subMeshCount);
                target.m_StaticBatchInfo = new StaticBatchInfo(CreateSyntheticZeroObjectReader(templateReader, 4))
                {
                    firstSubMesh = firstSubMesh,
                    subMeshCount = subMeshCount
                };
            }

            if (TryExtractUInt32Array(dict, "m_SubsetIndices", out var subsetIndices))
            {
                target.m_SubsetIndices = subsetIndices;
            }

            if (TryExtractByte(dict, "m_RayTracingAccelStructBuildFlagsOverride", out var rtOverride))
            {
                target.m_RayTracingAccelStructBuildFlagsOverride = rtOverride;
            }
            if (TryExtractByte(dict, "m_RayTracingAccelStructBuildFlags", out var rtFlags))
            {
                target.m_RayTracingAccelStructBuildFlags = rtFlags;
            }
            if (TryExtractByte(dict, "m_SmallMeshCulling", out var smallMeshCulling))
            {
                target.m_SmallMeshCulling = smallMeshCulling;
            }
            if (TryExtractInt16(dict, "m_ForceMeshLod", out var forceMeshLod))
            {
                target.m_ForceMeshLod = forceMeshLod;
            }
            if (TryExtractSingle(dict, "m_MeshLodSelectionBias", out var lodBias))
            {
                target.m_MeshLodSelectionBias = lodBias;
            }
            if (TryExtractUInt32(dict, "m_RenderingLayerMask", out var renderingLayerMask))
            {
                target.m_RenderingLayerMask = renderingLayerMask;
            }
            if (TryExtractInt32(dict, "m_RendererPriority", out var rendererPriority))
            {
                target.m_RendererPriority = rendererPriority;
            }
            if (TryExtractInt16(dict, "m_SortingLayer", out var sortingLayer))
            {
                target.m_SortingLayer = sortingLayer;
            }
            if (TryExtractInt16(dict, "m_SortingOrder", out var sortingOrder))
            {
                target.m_SortingOrder = sortingOrder;
            }
            if (TryExtractInt32(dict, "m_MaskInteraction", out var maskInteraction))
            {
                target.m_MaskInteraction = maskInteraction;
            }
        }

        internal static bool TryExtractPPtr<T>(IDictionary dict, string key, SerializedFile assetsFile, out PPtr<T>? value)
            where T : Object
        {
            value = null;
            if (!dict.Contains(key) || dict[key] is not IDictionary pptrDict)
            {
                return false;
            }

            return TryExtractPPtrFromDictionary(pptrDict, assetsFile, out value);
        }

        internal static bool TryExtractPPtrArray<T>(IDictionary dict, string key, SerializedFile assetsFile, out PPtr<T>[] value)
            where T : Object
        {
            value = Array.Empty<PPtr<T>>();
            if (!dict.Contains(key) || dict[key] is not IEnumerable items || dict[key] is string)
            {
                return false;
            }

            var list = new List<PPtr<T>>();
            foreach (var item in items)
            {
                if (item is IDictionary pptrDict && TryExtractPPtrFromDictionary<T>(pptrDict, assetsFile, out var pptr) && pptr != null)
                {
                    list.Add(pptr);
                }
            }

            value = list.Count == 0 ? Array.Empty<PPtr<T>>() : list.ToArray();
            return true;
        }

        private static bool TryExtractPPtrFromDictionary<T>(IDictionary pptrDict, SerializedFile assetsFile, out PPtr<T>? value)
            where T : Object
        {
            value = null;
            if (!pptrDict.Contains("m_FileID") || !pptrDict.Contains("m_PathID"))
            {
                return false;
            }

            if (!TryConvertInt32(pptrDict["m_FileID"], out int fileId)
                || !TryConvertInt64(pptrDict["m_PathID"], out long pathId))
            {
                return false;
            }

            value = new PPtr<T>(fileId, pathId, assetsFile);
            return true;
        }

        internal static bool TryExtractBoolean(IDictionary dict, string key, out bool value)
        {
            value = false;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToBoolean(dict[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryExtractByte(IDictionary dict, string key, out byte value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToByte(dict[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryExtractInt16(IDictionary dict, string key, out short value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToInt16(dict[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryExtractUInt16(IDictionary dict, string key, out ushort value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToUInt16(dict[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryExtractInt32(IDictionary dict, string key, out int value)
        {
            value = 0;
            return dict.Contains(key) && TryConvertInt32(dict[key], out value);
        }

        internal static bool TryExtractUInt32(IDictionary dict, string key, out uint value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = unchecked((uint)Convert.ToInt64(dict[key], CultureInfo.InvariantCulture));
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryExtractSingle(IDictionary dict, string key, out float value)
        {
            value = 0f;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToSingle(dict[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryExtractSingleArray(IDictionary dict, string key, out float[] value)
        {
            value = Array.Empty<float>();
            if (!dict.Contains(key) || dict[key] is not IEnumerable items || dict[key] is string)
            {
                return false;
            }

            var list = new List<float>();
            foreach (var item in items)
            {
                if (item == null) continue;
                try
                {
                    list.Add(Convert.ToSingle(item, CultureInfo.InvariantCulture));
                }
                catch
                {
                    // Skip non-numeric entries
                }
            }

            value = list.Count == 0 ? Array.Empty<float>() : list.ToArray();
            return true;
        }

        internal static bool TryExtractUInt32Array(IDictionary dict, string key, out uint[] value)
        {
            value = Array.Empty<uint>();
            if (!dict.Contains(key) || dict[key] is not IEnumerable items || dict[key] is string)
            {
                return false;
            }

            var list = new List<uint>();
            foreach (var item in items)
            {
                if (item == null) continue;
                try
                {
                    list.Add(unchecked((uint)Convert.ToInt64(item, CultureInfo.InvariantCulture)));
                }
                catch
                {
                    // Skip non-numeric entries
                }
            }

            value = list.Count == 0 ? Array.Empty<uint>() : list.ToArray();
            return true;
        }

        internal static bool TryExtractVector3(IDictionary dict, string key, out Vector3 value)
        {
            value = Vector3.Zero;
            if (!dict.Contains(key) || dict[key] is not IDictionary vecDict)
            {
                return false;
            }

            TryExtractSingle(vecDict, "x", out float x);
            TryExtractSingle(vecDict, "y", out float y);
            TryExtractSingle(vecDict, "z", out float z);
            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryConvertInt32(object? raw, out int value)
        {
            value = 0;
            if (raw == null) return false;
            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertInt64(object? raw, out long value)
        {
            value = 0;
            if (raw == null) return false;
            try
            {
                value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal readonly struct RendererBinaryProbeResult
    {
        public static readonly RendererBinaryProbeResult DirectBinary = new(useDirectBinary: true, reason: null);

        private RendererBinaryProbeResult(bool useDirectBinary, string? reason)
        {
            UseDirectBinary = useDirectBinary;
            Reason = reason;
        }

        public bool UseDirectBinary { get; }
        public string? Reason { get; }

        public static RendererBinaryProbeResult Fallback(string reason) => new(useDirectBinary: false, reason: reason);
    }
}
