using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
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

        public static string ReadTypeString(TypeTree m_Type, ObjectReader reader)
        {
            reader.Reset();
            var sb = new StringBuilder();
            var m_Nodes = m_Type.m_Nodes;
            var nextSibling = GetNextSiblingTable(m_Nodes);
            for (int i = 0; i < m_Nodes.Count; i++)
            {
                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref i, depth: 0);
            }
            var readed = reader.Position - reader.byteStart;
            if (readed != reader.byteSize)
            {
                Logger.Info($"Error while read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }
            return sb.ToString();
        }

        private static void ReadStringValue(StringBuilder sb, List<TypeTreeNode> m_Nodes, int[] nextSibling, ObjectReader reader, ref int i, int depth)
        {
            ValidateDepth(depth);
            var m_Node = m_Nodes[i];
            var level = m_Node.m_Level;
            var varTypeStr = m_Node.m_Type;
            var varNameStr = m_Node.m_Name;
            object? value = null;
            var append = true;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = ReadCharValue(reader);
                    break;
                case "short":
                case "SInt16":
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    value = reader.ReadSingle();
                    break;
                case "double":
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "string":
                    append = false;
                    var str = reader.ReadAlignedString();
                    sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append(" = \"").Append(str).Append("\"\r\n");
                    i = nextSibling[i] - 1;
                    break;
                case "map":
                    {
                        int mapEnd = nextSibling[i];
                        if (i + 1 < mapEnd && (m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        append = false;
                        sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                        sb.Append('\t', level + 1).Append("Array Array\r\n");
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        sb.Append('\t', level + 1).Append("int size = ").Append(size).Append("\r\n");
                        int firstIndex = i + 4;
                        int secondIndex = firstIndex < mapEnd ? nextSibling[firstIndex] : mapEnd;
                        if (size > 0 && (firstIndex >= mapEnd || secondIndex >= mapEnd))
                        {
                            throw new InvalidDataException("Invalid map TypeTree node layout.");
                        }
                        i = mapEnd - 1;
                        for (int j = 0; j < size; j++)
                        {
                            sb.Append('\t', level + 2).Append('[').Append(j).Append("]\r\n");
                            sb.Append('\t', level + 2).Append("pair data\r\n");
                            int tmp1 = firstIndex;
                            int tmp2 = secondIndex;
                            ReadStringValue(sb, m_Nodes, nextSibling, reader, ref tmp1, depth + 1);
                            ReadStringValue(sb, m_Nodes, nextSibling, reader, ref tmp2, depth + 1);
                        }
                        break;
                    }
                case "TypelessData":
                    {
                        append = false;
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        AdvanceObjectBytes(reader, size);
                        i = nextSibling[i] - 1;
                        sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append("\r\n");
                        sb.Append('\t', level).Append("int size = ").Append(size).Append("\r\n");
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
                            var size = reader.ReadInt32();
                            reader.CheckArrayLength(size);
                            sb.Append('\t', level + 1).Append("int size = ").Append(size).Append("\r\n");
                            int elemIndex = i + 3;
                            if (size > 0 && elemIndex >= arrayEnd)
                            {
                                throw new InvalidDataException("Invalid Array TypeTree node layout.");
                            }
                            i = arrayEnd - 1;
                            for (int j = 0; j < size; j++)
                            {
                                sb.Append('\t', level + 2).Append('[').Append(j).Append("]\r\n");
                                int tmp = elemIndex;
                                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref tmp, depth + 1);
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
                                ReadStringValue(sb, m_Nodes, nextSibling, reader, ref j, depth + 1);
                            }
                            break;
                        }
                    }
            }
            if (append)
                sb.Append('\t', level).Append(varTypeStr).Append(' ').Append(varNameStr).Append(" = ").Append(value).Append("\r\n");
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
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = ReadCharValue(reader);
                    break;
                case "short":
                case "SInt16":
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    value = reader.ReadSingle();
                    break;
                case "double":
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "string":
                    value = reader.ReadAlignedString();
                    i = nextSibling[i] - 1;
                    break;
                case "map":
                    {
                        int mapEnd = nextSibling[i];
                        if (i + 1 < mapEnd && (m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        int firstIndex = i + 4;
                        int secondIndex = firstIndex < mapEnd ? nextSibling[firstIndex] : mapEnd;
                        i = mapEnd - 1;
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
                        }
                        value = dic;
                        break;
                    }
                case "TypelessData":
                    {
                        var size = reader.ReadInt32();
                        reader.CheckArrayLength(size);
                        value = reader.ReadBytes(size);
                        i = nextSibling[i] - 1;
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

        private static char ReadCharValue(ObjectReader reader)
        {
            CheckObjectBytesAvailable(reader, 2);
            Span<byte> charBytes = stackalloc byte[2];
            EndianBinaryReader.ReadStreamExactly(reader.BaseStream, charBytes);
            return BitConverter.ToChar(charBytes);
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

        private static bool TryGetFixedPrimitiveByteSize(string typeName, out int byteSize)
        {
            switch (typeName)
            {
                case "SInt8":
                case "UInt8":
                case "bool":
                    byteSize = 1;
                    return true;
                case "char":
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

            if (TryGetFixedPrimitiveByteSize(typeName, out int scalarBytes))
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
                                    && TryGetFixedPrimitiveByteSize(nodes[elemIndex].m_Type, out int elemBytes))
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
    }
}
