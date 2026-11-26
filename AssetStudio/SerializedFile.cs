using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetStudio
{
    public class SerializedFile
    {
        public AssetsManager assetsManager;
        public FileReader reader;
        public string fullName;
        public string originalPath;
        public string fileName;
        public bool IsProcessed = false;
        public int[] version = { 0, 0, 0, 0 };
        public BuildType? buildType;
        public List<Object> Objects;
        public Dictionary<long, Object> ObjectsDic;
        public Dictionary<long, ObjectInfo> ObjectInfoDic;
        private readonly object objectCollectionLock = new object();

        public SerializedFileHeader header;
        private byte m_FileEndianess;
        public string unityVersion = "2.5.0f5";
        public BuildTarget m_TargetPlatform = BuildTarget.UnknownPlatform;
        private bool m_EnableTypeTree = true;
        public List<SerializedType> m_Types;
        public int bigIDEnabled = 0;
        public List<ObjectInfo> m_Objects;
        private List<LocalSerializedObjectIdentifier>? m_ScriptTypes;
        public List<FileIdentifier> m_Externals;
        public List<SerializedType>? m_RefTypes;
        public string? userInformation;
        /// <summary>Types whose TypeTree was extracted to a separate TypeTree data archive and is not resolved yet.</summary>
        public int ExternalTypeTreeCount;

        public SerializedFile(FileReader reader, AssetsManager assetsManager)
        {
            this.assetsManager = assetsManager;
            this.reader = reader;
            fullName = reader.FullPath;
            originalPath = reader.FullPath;
            fileName = reader.FileName;

            // ReadHeader
            header = new SerializedFileHeader();
            header.m_MetadataSize = reader.ReadUInt32();
            header.m_FileSize = reader.ReadUInt32();
            header.m_Version = (SerializedFileFormatVersion)reader.ReadUInt32();
            header.m_DataOffset = reader.ReadUInt32();

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_9)
            {
                header.m_Endianess = reader.ReadByte();
                header.m_Reserved = reader.ReadBytes(3);
                m_FileEndianess = header.m_Endianess;
            }
            else
            {
                reader.Position = header.m_FileSize - header.m_MetadataSize;
                m_FileEndianess = reader.ReadByte();
            }

            if (header.m_Version >= SerializedFileFormatVersion.LargeFilesSupport)
            {
                header.m_MetadataSize = reader.ReadUInt32();
                header.m_FileSize = reader.ReadInt64();
                header.m_DataOffset = reader.ReadInt64();
                reader.ReadInt64(); // unknown
            }

            // ReadMetadata
            if (m_FileEndianess == 0)
            {
                reader.Endian = EndianType.LittleEndian;
            }
            if (header.m_Version >= SerializedFileFormatVersion.Unknown_7)
            {
                unityVersion = reader.ReadStringToNull();
                SetVersion(unityVersion);
            }
            if (header.m_Version >= SerializedFileFormatVersion.Unknown_8)
            {
                m_TargetPlatform = (BuildTarget)reader.ReadInt32();
                if (!Enum.IsDefined(typeof(BuildTarget), m_TargetPlatform))
                {
                    m_TargetPlatform = BuildTarget.UnknownPlatform;
                }
            }
            if (header.m_Version >= SerializedFileFormatVersion.HasTypeTreeHashes)
            {
                m_EnableTypeTree = reader.ReadBoolean();
            }

            // Read Types
            int typeCount = reader.ReadInt32();
            ValidateCount(typeCount, 4, "type");
            m_Types = new List<SerializedType>(typeCount);
            for (int i = 0; i < typeCount; i++)
            {
                m_Types.Add(ReadSerializedType(false));
            }
            var serializedTypeByClassId = new Dictionary<int, SerializedType>(typeCount);
            foreach (var serializedType in m_Types)
            {
                if (!serializedTypeByClassId.ContainsKey(serializedType.classID))
                {
                    serializedTypeByClassId.Add(serializedType.classID, serializedType);
                }
            }

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_7 && header.m_Version < SerializedFileFormatVersion.Unknown_14)
            {
                bigIDEnabled = reader.ReadInt32();
            }

            // Read Objects
            int objectCount = reader.ReadInt32();
            ValidateCount(objectCount, 16, "object");
            m_Objects = new List<ObjectInfo>(objectCount);
            Objects = new List<Object>(objectCount);
            ObjectsDic = new Dictionary<long, Object>(objectCount);
            ObjectInfoDic = new Dictionary<long, ObjectInfo>(objectCount);
            for (int i = 0; i < objectCount; i++)
            {
                var objectInfo = new ObjectInfo();
                if (bigIDEnabled != 0)
                {
                    objectInfo.m_PathID = reader.ReadInt64();
                }
                else if (header.m_Version < SerializedFileFormatVersion.Unknown_14)
                {
                    objectInfo.m_PathID = reader.ReadInt32();
                }
                else
                {
                    reader.AlignStream();
                    objectInfo.m_PathID = reader.ReadInt64();
                }

                if (header.m_Version >= SerializedFileFormatVersion.LargeFilesSupport)
                    objectInfo.byteStart = reader.ReadInt64();
                else
                    objectInfo.byteStart = reader.ReadUInt32();

                objectInfo.byteStart += header.m_DataOffset;
                objectInfo.byteSize = reader.ReadUInt32();
                objectInfo.typeID = reader.ReadInt32();
                if (header.m_Version < SerializedFileFormatVersion.RefactoredClassId)
                {
                    objectInfo.classID = reader.ReadUInt16();
                    serializedTypeByClassId.TryGetValue(objectInfo.typeID, out objectInfo.serializedType);
                }
                else
                {
                    if (objectInfo.typeID < 0 || objectInfo.typeID >= m_Types.Count)
                    {
                        throw new InvalidDataException($"Object {objectInfo.m_PathID} references type index {objectInfo.typeID}, but only {m_Types.Count} types are defined.");
                    }
                    var type = m_Types[objectInfo.typeID];
                    objectInfo.serializedType = type;
                    objectInfo.classID = type.classID;
                }
                if (header.m_Version < SerializedFileFormatVersion.HasScriptTypeIndex)
                {
                    objectInfo.isDestroyed = reader.ReadUInt16();
                }
                if (header.m_Version >= SerializedFileFormatVersion.HasScriptTypeIndex && header.m_Version < SerializedFileFormatVersion.RefactorTypeData)
                {
                    var m_ScriptTypeIndex = reader.ReadInt16();
                    if (objectInfo.serializedType != null)
                        objectInfo.serializedType.m_ScriptTypeIndex = m_ScriptTypeIndex;
                }
                if (header.m_Version == SerializedFileFormatVersion.SupportsStrippedObject || header.m_Version == SerializedFileFormatVersion.RefactoredClassId)
                {
                    objectInfo.stripped = reader.ReadByte();
                }
                m_Objects.Add(objectInfo);
                ObjectInfoDic[objectInfo.m_PathID] = objectInfo;
            }

            if (header.m_Version >= SerializedFileFormatVersion.HasScriptTypeIndex)
            {
                int scriptCount = reader.ReadInt32();
                ValidateCount(scriptCount, 8, "script type");
                m_ScriptTypes = new List<LocalSerializedObjectIdentifier>(scriptCount);
                for (int i = 0; i < scriptCount; i++)
                {
                    var m_ScriptType = new LocalSerializedObjectIdentifier();
                    m_ScriptType.localSerializedFileIndex = reader.ReadInt32();
                    if (header.m_Version < SerializedFileFormatVersion.Unknown_14)
                    {
                        m_ScriptType.localIdentifierInFile = reader.ReadInt32();
                    }
                    else
                    {
                        reader.AlignStream();
                        m_ScriptType.localIdentifierInFile = reader.ReadInt64();
                    }
                    m_ScriptTypes.Add(m_ScriptType);
                }
            }

            int externalsCount = reader.ReadInt32();
            ValidateCount(externalsCount, 1, "external");
            m_Externals = new List<FileIdentifier>(externalsCount);
            for (int i = 0; i < externalsCount; i++)
            {
                var m_External = new FileIdentifier();
                if (header.m_Version >= SerializedFileFormatVersion.Unknown_6)
                {
                    var tempEmpty = reader.ReadStringToNull();
                }
                if (header.m_Version >= SerializedFileFormatVersion.Unknown_5)
                {
                    m_External.guid = new Guid(reader.ReadBytes(16));
                    m_External.type = reader.ReadInt32();
                }
                m_External.pathName = reader.ReadStringToNull();
                m_External.fileName = Path.GetFileName(m_External.pathName.Replace('\\', '/'));
                m_Externals.Add(m_External);
            }

            if (header.m_Version >= SerializedFileFormatVersion.SupportsRefObject)
            {
                int refTypesCount = reader.ReadInt32();
                ValidateCount(refTypesCount, 4, "reference type");
                m_RefTypes = new List<SerializedType>(refTypesCount);
                for (int i = 0; i < refTypesCount; i++)
                {
                    m_RefTypes.Add(ReadSerializedType(true));
                }
            }

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_5)
            {
                userInformation = reader.ReadStringToNull();
            }

            //reader.AlignStream(16);
        }

        public void SetVersion(string? stringVersion)
        {
            if (stringVersion != null && stringVersion != strippedVersion)
            {
                unityVersion = stringVersion;
                // Build type letter ("f", "p", "b", "a"...) is optional, e.g. "2021.3.16" has none.
                var buildSplit = Regex.Replace(stringVersion, @"\d", "").Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                buildType = new BuildType(buildSplit.Length > 0 ? buildSplit[0] : string.Empty);
                var versionSplit = Regex.Replace(stringVersion, @"\D", ".").Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                // Deserializers index version[0..3] unconditionally, so always keep at least 4 components.
                var parsedVersion = new int[Math.Max(4, versionSplit.Length)];
                for (int i = 0; i < versionSplit.Length; i++)
                {
                    int.TryParse(versionSplit[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsedVersion[i]);
                }
                version = parsedVersion;
            }
        }

        private void ValidateCount(int count, int minBytesPerItem, string name)
        {
            ValidateCount(reader, count, minBytesPerItem, name, fileName);
        }

        private static void ValidateCount(EndianBinaryReader reader, int count, int minBytesPerItem, string name, string sourceName)
        {
            var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (count < 0 || (long)count * minBytesPerItem > remaining)
            {
                throw new InvalidDataException($"Invalid {name} count {count} in {sourceName}: only {remaining} bytes remain.");
            }
        }

        private SerializedType ReadSerializedType(bool isRefType)
        {
            var type = new SerializedType();

            type.classID = reader.ReadInt32();

            if (header.m_Version >= SerializedFileFormatVersion.RefactoredClassId)
            {
                type.m_IsStrippedType = reader.ReadBoolean();
            }

            if (header.m_Version >= SerializedFileFormatVersion.RefactorTypeData)
            {
                type.m_ScriptTypeIndex = reader.ReadInt16();
            }

            if (header.m_Version >= SerializedFileFormatVersion.HasTypeTreeHashes)
            {
                if (isRefType && type.m_ScriptTypeIndex >= 0)
                {
                    type.m_ScriptID = reader.ReadBytes(16);
                }
                else if ((header.m_Version < SerializedFileFormatVersion.RefactoredClassId && type.classID < 0) || (header.m_Version >= SerializedFileFormatVersion.RefactoredClassId && type.classID == 114))
                {
                    type.m_ScriptID = reader.ReadBytes(16);
                }
                type.m_OldTypeHash = reader.ReadBytes(16);
            }

            if (m_EnableTypeTree)
            {
                type.m_Type = new TypeTree();
                type.m_Type.m_Nodes = new List<TypeTreeNode>();
                if (header.m_Version >= SerializedFileFormatVersion.ExtractableTypeTree)
                {
                    type.m_TypeTreeHash = reader.ReadBytes(16);
                    if (!TypeTreeDataBlockRead(type.m_Type))
                    {
                        type.m_Type = null;
                        ExternalTypeTreeCount++;
                    }
                }
                else if (header.m_Version >= SerializedFileFormatVersion.Unknown_12 || header.m_Version == SerializedFileFormatVersion.Unknown_10)
                {
                    TypeTreeBlobRead(type.m_Type);
                }
                else
                {
                    ReadTypeTree(type.m_Type);
                }
                if (header.m_Version >= SerializedFileFormatVersion.StoresTypeDependencies)
                {
                    if (isRefType)
                    {
                        type.m_KlassName = reader.ReadStringToNull();
                        type.m_NameSpace = reader.ReadStringToNull();
                        type.m_AsmName = reader.ReadStringToNull();
                    }
                    else
                    {
                        type.m_TypeDependencies = reader.ReadInt32Array();
                    }
                }
            }

            return type;
        }

        private const int MaxTypeTreeDepth = 64;

        private void ReadTypeTree(TypeTree m_Type, int level = 0)
        {
            if (level > MaxTypeTreeDepth)
            {
                throw new InvalidDataException($"TypeTree recursion depth exceeded limit ({MaxTypeTreeDepth}).");
            }
            var typeTreeNode = new TypeTreeNode();
            m_Type.m_Nodes.Add(typeTreeNode);
            typeTreeNode.m_Level = level;
            typeTreeNode.m_Type = reader.ReadStringToNull();
            typeTreeNode.m_Name = reader.ReadStringToNull();
            typeTreeNode.m_ByteSize = reader.ReadInt32();
            if (header.m_Version == SerializedFileFormatVersion.Unknown_2)
            {
                var variableCount = reader.ReadInt32();
            }
            if (header.m_Version != SerializedFileFormatVersion.Unknown_3)
            {
                typeTreeNode.m_Index = reader.ReadInt32();
            }
            typeTreeNode.m_TypeFlags = reader.ReadInt32();
            typeTreeNode.m_Version = reader.ReadInt32();
            if (header.m_Version != SerializedFileFormatVersion.Unknown_3)
            {
                typeTreeNode.m_MetaFlag = reader.ReadInt32();
            }

            int childrenCount = reader.ReadInt32();
            // Smallest legacy node: two empty strings + 4 Int32 fields.
            ValidateCount(childrenCount, 18, "TypeTree child");
            for (int i = 0; i < childrenCount; i++)
            {
                ReadTypeTree(m_Type, level + 1);
            }
        }

        // Format 23 stores each TypeTree as a block: u32 size, then 'tthm' magic, data version, node count,
        // string buffer size, the 32-byte nodes and the string buffer (the same layout an extracted TypeTree file uses).
        private const uint TypeTreeDataMagic = 0x7474686D;
        private const int TypeTreeDataHeaderSize = 16;
        private const int TypeTreeDataNodeSize = 32;

        // Returns false for an empty block: the build moved this TypeTree to a separate TypeTree data archive
        // and only its hash (m_TypeTreeHash) stays in the file.
        private bool TypeTreeDataBlockRead(TypeTree m_Type)
        {
            uint blockSize = reader.ReadUInt32();
            if (blockSize == 0)
            {
                return false;
            }
            ReadTypeTreeData(reader, blockSize, m_Type, fileName);
            return true;
        }

        /// <summary>
        /// Reads one 'tthm' TypeTree data block of <paramref name="blockSize"/> bytes at the reader position.
        /// Used for TypeTrees inside format 23 files and for the entries of an extracted TypeTree data archive.
        /// </summary>
        internal static void ReadTypeTreeData(EndianBinaryReader reader, long blockSize, TypeTree m_Type, string sourceName)
        {
            long blockStart = reader.Position;
            long remaining = reader.BaseStream.Length - blockStart;
            if (blockSize < TypeTreeDataHeaderSize || blockSize > remaining)
            {
                throw new InvalidDataException($"Invalid TypeTree data block size {blockSize} in {sourceName}: only {remaining} bytes remain.");
            }
            uint magic = reader.ReadUInt32();
            if (magic != TypeTreeDataMagic)
            {
                throw new InvalidDataException($"Invalid TypeTree data block in {sourceName}: expected 'tthm' header, found 0x{magic:X8}.");
            }
            uint dataVersion = reader.ReadUInt32();
            if (dataVersion != (uint)SerializedFileFormatVersion.ExtractableTypeTree)
            {
                throw new InvalidDataException($"Unsupported TypeTree data version {dataVersion} in {sourceName} (supported: {(uint)SerializedFileFormatVersion.ExtractableTypeTree}).");
            }
            int numberOfNodes = reader.ReadInt32();
            int stringBufferSize = reader.ReadInt32();
            if (numberOfNodes < 0 || stringBufferSize < 0
                || TypeTreeDataHeaderSize + (long)numberOfNodes * TypeTreeDataNodeSize + stringBufferSize != blockSize)
            {
                throw new InvalidDataException($"Invalid TypeTree data block in {sourceName}: {numberOfNodes} nodes and {stringBufferSize} string bytes do not fill {blockSize} bytes.");
            }
            ReadTypeTreeBlobNodes(reader, m_Type, numberOfNodes, stringBufferSize, hasRefTypeHash: true, sourceName);
            if (reader.Position != blockStart + blockSize)
            {
                throw new InvalidDataException($"TypeTree data block in {sourceName} ended at {reader.Position}, expected {blockStart + blockSize}.");
            }
        }

        private void TypeTreeBlobRead(TypeTree m_Type)
        {
            int numberOfNodes = reader.ReadInt32();
            int stringBufferSize = reader.ReadInt32();
            ReadTypeTreeBlobNodes(reader, m_Type, numberOfNodes, stringBufferSize, header.m_Version >= SerializedFileFormatVersion.TypeTreeNodeWithTypeFlags, fileName);
        }

        private static void ReadTypeTreeBlobNodes(EndianBinaryReader reader, TypeTree m_Type, int numberOfNodes, int stringBufferSize, bool hasRefTypeHash, string sourceName)
        {
            ValidateCount(reader, numberOfNodes, 24, "TypeTree node", sourceName);
            ValidateCount(reader, stringBufferSize, 1, "TypeTree string buffer byte", sourceName);
            m_Type.m_Nodes.EnsureCapacity(m_Type.m_Nodes.Count + numberOfNodes);
            for (int i = 0; i < numberOfNodes; i++)
            {
                var typeTreeNode = new TypeTreeNode();
                m_Type.m_Nodes.Add(typeTreeNode);
                typeTreeNode.m_Version = reader.ReadUInt16();
                typeTreeNode.m_Level = reader.ReadByte();
                typeTreeNode.m_TypeFlags = reader.ReadByte();
                typeTreeNode.m_TypeStrOffset = reader.ReadUInt32();
                typeTreeNode.m_NameStrOffset = reader.ReadUInt32();
                typeTreeNode.m_ByteSize = reader.ReadInt32();
                typeTreeNode.m_Index = reader.ReadInt32();
                typeTreeNode.m_MetaFlag = reader.ReadInt32();
                if (hasRefTypeHash)
                {
                    typeTreeNode.m_RefTypeHash = reader.ReadUInt64();
                }
            }
            m_Type.m_StringBuffer = reader.ReadBytes(stringBufferSize);

            var stringBufferReader = new EndianSpanReader(m_Type.m_StringBuffer, reader.Endian);
            for (int i = 0; i < numberOfNodes; i++)
            {
                var m_Node = m_Type.m_Nodes[i];
                m_Node.m_Type = ReadString(ref stringBufferReader, m_Node.m_TypeStrOffset);
                m_Node.m_Name = ReadString(ref stringBufferReader, m_Node.m_NameStrOffset);
            }

            static string ReadString(ref EndianSpanReader stringBufferReader, uint value)
            {
                var isOffset = (value & 0x80000000) == 0;
                if (isOffset)
                {
                    if (value >= (uint)stringBufferReader.Length)
                    {
                        throw new InvalidDataException($"Invalid TypeTree string offset {value} (string buffer size {stringBufferReader.Length}).");
                    }
                    stringBufferReader.Position = (int)value;
                    return stringBufferReader.ReadStringToNull();
                }
                var offset = value & 0x7FFFFFFF;
                if (CommonString.StringBuffer.TryGetValue(offset, out var str))
                {
                    return str;
                }
                return offset.ToString();
            }
        }

        /// <summary>
        /// Attaches TypeTrees that the build extracted to a separate archive, matched by m_TypeTreeHash.
        /// Returns how many types are still without their TypeTree.
        /// </summary>
        public int ResolveExternalTypeTrees(TypeTreeStore store)
        {
            if (ExternalTypeTreeCount == 0 || store.Count == 0)
            {
                return ExternalTypeTreeCount;
            }
            int missing = 0;
            foreach (var list in new[] { m_Types, m_RefTypes })
            {
                if (list == null)
                {
                    continue;
                }
                foreach (var type in list)
                {
                    if (type.m_Type != null || type.m_TypeTreeHash == null)
                    {
                        continue;
                    }
                    if (store.TryGet(type.m_TypeTreeHash, out var tree))
                    {
                        type.m_Type = tree;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }
            ExternalTypeTreeCount = missing;
            return missing;
        }

        public void AddObject(Object obj)
        {
            lock (objectCollectionLock)
            {
                if (ObjectsDic.ContainsKey(obj.m_PathID))
                {
                    return;
                }

                Objects.Add(obj);
                ObjectsDic.Add(obj.m_PathID, obj);
            }
        }

        public bool TryGetObject(long pathID, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Object obj)
        {
            lock (objectCollectionLock)
            {
                return ObjectsDic.TryGetValue(pathID, out obj);
            }
        }

        public void RemoveObject(Object obj)
        {
            lock (objectCollectionLock)
            {
                Objects.Remove(obj);
                ObjectsDic.Remove(obj.m_PathID);
            }
        }

        public bool IsVersionStripped => unityVersion == strippedVersion;

        private const string strippedVersion = "0.0.0";
    }
}
