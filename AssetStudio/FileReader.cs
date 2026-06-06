using System.IO;
using System.Text;

namespace AssetStudio
{
    public class FileReader : EndianBinaryReader
    {
        public string FullPath;
        public string FileName;
        public FileType FileType;
        private byte[] cachedMemoryBuffer;
        private readonly object cachedMemoryBufferLock = new object();

        private static readonly byte[] gzipMagic = { 0x1f, 0x8b };
        private static readonly byte[] brotliMagic = { 0x62, 0x72, 0x6F, 0x74, 0x6C, 0x69 };
        private static readonly byte[] zipMagic = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] zipSpannedMagic = { 0x50, 0x4B, 0x07, 0x08 };
        private const int FileTypeHeaderBytes = 64;

        public FileReader(string path) : this(path, File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }

        public FileReader(string path, Stream stream) : base(DecryptionHelper.CheckAndDecryptStream(stream, path), EndianType.BigEndian)
        {
            FullPath = Path.GetFullPath(path);
            FileName = Path.GetFileName(path);
            FileType = CheckFileType();
        }

        private FileType CheckFileType()
        {
            Position = 0;
            var headerLength = (int)System.Math.Min(FileTypeHeaderBytes, BaseStream.Length);
            var header = ReadBytes(headerLength);
            Position = 0;

            var signature = ReadNullTerminatedAscii(header, 20);
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
                        if (StartsWith(header, gzipMagic))
                        {
                            return FileType.GZipFile;
                        }
                        if (MatchesAt(header, 0x20, brotliMagic))
                        {
                            return FileType.BrotliFile;
                        }
                        if (IsSerializedFile(header))
                        {
                            return FileType.AssetsFile;
                        }
                        if (StartsWith(header, zipMagic) || StartsWith(header, zipSpannedMagic))
                            return FileType.ZipFile;
                        return FileType.ResourceFile;
                    }
            }
        }

        private bool IsSerializedFile(byte[] header)
        {
            var fileSize = BaseStream.Length;
            if (fileSize < 20 || header.Length < 20)
            {
                return false;
            }
            var m_MetadataSize = ReadUInt32BigEndian(header, 0);
            long m_FileSize = ReadUInt32BigEndian(header, 4);
            var m_Version = ReadUInt32BigEndian(header, 8);
            long m_DataOffset = ReadUInt32BigEndian(header, 12);
            if (m_Version >= 22)
            {
                if (fileSize < 48 || header.Length < 40)
                {
                    return false;
                }
                m_MetadataSize = ReadUInt32BigEndian(header, 20);
                m_FileSize = ReadInt64BigEndian(header, 24);
                m_DataOffset = ReadInt64BigEndian(header, 32);
            }
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

        private static string ReadNullTerminatedAscii(byte[] buffer, int maxLength)
        {
            var length = System.Math.Min(buffer.Length, maxLength);
            for (int i = 0; i < length; i++)
            {
                if (buffer[i] == 0)
                {
                    length = i;
                    break;
                }
            }
            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        private static bool StartsWith(byte[] buffer, byte[] value)
        {
            return MatchesAt(buffer, 0, value);
        }

        private static bool MatchesAt(byte[] buffer, int offset, byte[] value)
        {
            if (offset < 0 || buffer.Length - offset < value.Length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (buffer[offset + i] != value[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                | ((uint)buffer[offset + 1] << 16)
                | ((uint)buffer[offset + 2] << 8)
                | buffer[offset + 3];
        }

        private static long ReadInt64BigEndian(byte[] buffer, int offset)
        {
            var value = ((ulong)buffer[offset] << 56)
                | ((ulong)buffer[offset + 1] << 48)
                | ((ulong)buffer[offset + 2] << 40)
                | ((ulong)buffer[offset + 3] << 32)
                | ((ulong)buffer[offset + 4] << 24)
                | ((ulong)buffer[offset + 5] << 16)
                | ((ulong)buffer[offset + 6] << 8)
                | buffer[offset + 7];
            return unchecked((long)value);
        }

        public FileReader Clone()
        {
            if (BaseStream is MemoryStream memStream)
            {
                if (memStream.TryGetBuffer(out var segment) && segment.Array != null)
                {
                    var segmentStream = new MemoryStream(segment.Array, segment.Offset, segment.Count, false, true);
                    var segmentClone = new FileReader(FullPath, segmentStream);
                    segmentClone.Endian = Endian;
                    segmentClone.cachedMemoryBuffer = cachedMemoryBuffer;
                    return segmentClone;
                }

                if (cachedMemoryBuffer == null)
                {
                    lock (cachedMemoryBufferLock)
                    {
                        if (cachedMemoryBuffer == null)
                        {
                            cachedMemoryBuffer = memStream.ToArray();
                        }
                    }
                }

                var newStream = new MemoryStream(cachedMemoryBuffer, 0, cachedMemoryBuffer.Length, false, true);
                var clone = new FileReader(FullPath, newStream);
                clone.Endian = Endian;
                clone.cachedMemoryBuffer = cachedMemoryBuffer;
                return clone;
            }
            else if (BaseStream is DecryptedStream decStream)
            {
                var clonedUnderlying = CloneStream(decStream.BaseStream);
                var newDecStream = new DecryptedStream(clonedUnderlying, decStream.Token);
                newDecStream.Position = decStream.Position;
                var clone = new FileReader(FullPath, newDecStream);
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

        private Stream CloneStream(Stream src)
        {
            if (src is MemoryStream memStream)
            {
                if (memStream.TryGetBuffer(out var segment) && segment.Array != null)
                {
                    return new MemoryStream(segment.Array, segment.Offset, segment.Count, false, true);
                }
                return new MemoryStream(memStream.ToArray(), 0, (int)memStream.Length, false, true);
            }
            if (src is SubStream subStream)
            {
                return new SubStream(subStream.FilePath, subStream.Offset, subStream.Length);
            }
            if (src is FileStream fileStream)
            {
                return File.Open(fileStream.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            
            var temp = new MemoryStream();
            var pos = src.Position;
            src.Position = 0;
            src.CopyTo(temp);
            src.Position = pos;
            temp.Position = pos;
            return temp;
        }
    }
}
