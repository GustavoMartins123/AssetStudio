using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class FileReader : EndianBinaryReader
    {
        public string FullPath;
        public string FileName;
        public FileType FileType;

        private static readonly byte[] gzipMagic = { 0x1f, 0x8b };
        private static readonly byte[] brotliMagic = { 0x62, 0x72, 0x6F, 0x74, 0x6C, 0x69 };
        private static readonly byte[] zipMagic = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] zipSpannedMagic = { 0x50, 0x4B, 0x07, 0x08 };

        public FileReader(string path) : this(path, File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }

        public FileReader(string path, Stream stream) : base(stream, EndianType.BigEndian)
        {
            FullPath = Path.GetFullPath(path);
            FileName = Path.GetFileName(path);
            FileType = CheckFileType();
        }

        private FileType CheckFileType()
        {
            var signature = this.ReadStringToNull(20);
            Position = 0;
            switch (signature)
            {
                case "UnityWeb":
                case "UnityRaw":
                case "UnityArchive":
                case "UnityFS":
                    return FileType.BundleFile;
                case "UnityWebData1.0":
                    return FileType.WebFile;
                default:
                    {
                        byte[] magic = ReadBytes(2);
                        Position = 0;
                        if (gzipMagic.SequenceEqual(magic))
                        {
                            return FileType.GZipFile;
                        }
                        Position = 0x20;
                        magic = ReadBytes(6);
                        Position = 0;
                        if (brotliMagic.SequenceEqual(magic))
                        {
                            return FileType.BrotliFile;
                        }
                        if (IsSerializedFile())
                        {
                            return FileType.AssetsFile;
                        }
                        magic = ReadBytes(4);
                        Position = 0;
                        if (zipMagic.SequenceEqual(magic) || zipSpannedMagic.SequenceEqual(magic))
                            return FileType.ZipFile;
                        return FileType.ResourceFile;
                    }
            }
        }

        private bool IsSerializedFile()
        {
            var fileSize = BaseStream.Length;
            if (fileSize < 20)
            {
                return false;
            }
            var m_MetadataSize = ReadUInt32();
            long m_FileSize = ReadUInt32();
            var m_Version = ReadUInt32();
            long m_DataOffset = ReadUInt32();
            var m_Endianess = ReadByte();
            var m_Reserved = ReadBytes(3);
            if (m_Version >= 22)
            {
                if (fileSize < 48)
                {
                    Position = 0;
                    return false;
                }
                m_MetadataSize = ReadUInt32();
                m_FileSize = ReadInt64();
                m_DataOffset = ReadInt64();
            }
            Position = 0;
            if (m_FileSize != fileSize)
            {
                return false;
            }
            if (m_DataOffset > fileSize)
            {
                return false;
            }
            return true;
        }

        public FileReader Clone()
        {
            if (BaseStream is MemoryStream memStream)
            {
                byte[] buffer;
                try
                {
                    buffer = memStream.GetBuffer();
                }
                catch (System.UnauthorizedAccessException)
                {
                    buffer = memStream.ToArray();
                }
                var newStream = new MemoryStream(buffer, 0, (int)memStream.Length, false);
                var clone = new FileReader(FullPath, newStream);
                clone.Endian = Endian;
                return clone;
            }
            else if (BaseStream is SubStream subStream)
            {
                var newStream = new SubStream(subStream.FilePath, subStream.Offset, subStream.Length);
                var clone = new FileReader(FullPath, newStream);
                clone.Endian = Endian;
                return clone;
            }
            else if (BaseStream is FileStream fileStream)
            {
                var streamPath = fileStream.Name;
                var newStream = File.Open(streamPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var clone = new FileReader(FullPath, newStream);
                clone.Endian = Endian;
                return clone;
            }
            else
            {
                try
                {
                    var tempMemStream = new MemoryStream();
                    var originalPosition = BaseStream.Position;
                    BaseStream.Position = 0;
                    BaseStream.CopyTo(tempMemStream);
                    BaseStream.Position = originalPosition;
                    tempMemStream.Position = originalPosition;
                    var clone = new FileReader(FullPath, tempMemStream);
                    clone.Endian = Endian;
                    return clone;
                }
                catch (System.Exception ex)
                {
                    throw new System.NotSupportedException($"Cloning stream type {BaseStream.GetType().Name} failed.", ex);
                }
            }
        }
    }
}
