using System.Collections.Specialized;
using System.Linq;

namespace AssetStudio
{
    public class Object
    {
        public SerializedFile assetsFile;
        public ObjectReader reader;
        public long m_PathID;
        public int[] version;
        protected BuildType buildType;
        public BuildTarget platform;
        public ClassIDType type;
        public SerializedType serializedType;
        public uint byteSize;

        public Object(ObjectReader reader)
        {
            this.reader = reader;
            reader.Reset();
            assetsFile = reader.assetsFile;
            type = reader.type;
            m_PathID = reader.m_PathID;
            version = reader.version;
            buildType = reader.buildType;
            platform = reader.platform;
            serializedType = reader.serializedType;
            byteSize = reader.byteSize;

            if (platform == BuildTarget.NoTarget)
            {
                var m_ObjectHideFlags = reader.ReadUInt32();
            }
        }

        public virtual string Dump()
        {
            if (serializedType?.m_Type != null)
            {
                return ReadWithFreshReader(localReader => TypeTreeHelper.ReadTypeString(serializedType.m_Type, localReader));
            }
            return null;
        }

        public string Dump(TypeTree m_Type)
        {
            if (m_Type != null)
            {
                return ReadWithFreshReader(localReader => TypeTreeHelper.ReadTypeString(m_Type, localReader));
            }
            return null;
        }

        public OrderedDictionary ToType()
        {
            if (serializedType?.m_Type != null)
            {
                return ReadWithFreshReader(localReader => TypeTreeHelper.ReadType(serializedType.m_Type, localReader));
            }
            return null;
        }

        public OrderedDictionary ToType(TypeTree m_Type)
        {
            if (m_Type != null)
            {
                return ReadWithFreshReader(localReader => TypeTreeHelper.ReadType(m_Type, localReader));
            }
            return null;
        }

        public byte[] GetRawData()
        {
            return ReadWithFreshReader(localReader =>
            {
                localReader.Reset();
                return localReader.ReadBytes((int)byteSize);
            });
        }

        private T ReadWithFreshReader<T>(System.Func<ObjectReader, T> read)
        {
            var objectInfo = assetsFile.m_Objects.FirstOrDefault(x => x.m_PathID == m_PathID);
            if (objectInfo == null)
            {
                objectInfo = new ObjectInfo
                {
                    byteStart = reader.byteStart,
                    byteSize = byteSize,
                    classID = (int)type,
                    m_PathID = m_PathID,
                    serializedType = serializedType
                };
            }

            using (var fileReader = assetsFile.reader.Clone())
            {
                var objectReader = new ObjectReader(fileReader, assetsFile, objectInfo);
                return read(objectReader);
            }
        }
    }
}
