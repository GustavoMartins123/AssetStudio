using System;
using System.Collections.Concurrent;
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

        private FileReader(string path, Stream stream, EndianType endian, FileType fileType, byte[] cachedMemoryBuffer) : base(stream, endian)
        {
            FullPath = Path.GetFullPath(path);
            FileName = Path.GetFileName(path);
            FileType = fileType;
            this.cachedMemoryBuffer = cachedMemoryBuffer;
            Position = 0;
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
                    var segmentStream = PooledReadOnlyMemoryStream.Rent(segment.Array, segment.Offset, segment.Count);
                    return new FileReader(FullPath, segmentStream, Endian, FileType, cachedMemoryBuffer);
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

                var newStream = PooledReadOnlyMemoryStream.Rent(cachedMemoryBuffer, 0, cachedMemoryBuffer.Length);
                return new FileReader(FullPath, newStream, Endian, FileType, cachedMemoryBuffer);
            }
            else if (BaseStream is DecryptedStream decStream)
            {
                var clonedUnderlying = CloneStream(decStream.BaseStream);
                var newDecStream = new DecryptedStream(clonedUnderlying, decStream.Token);
                return new FileReader(FullPath, newDecStream, Endian, FileType, cachedMemoryBuffer);
            }
            else if (BaseStream is SubStream subStream)
            {
                var newStream = new SubStream(subStream.FilePath, subStream.Offset, subStream.Length);
                return new FileReader(FullPath, newStream, Endian, FileType, cachedMemoryBuffer);
            }
            else if (BaseStream is FileStream fileStream)
            {
                var streamPath = fileStream.Name;
                var newStream = File.Open(streamPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new FileReader(FullPath, newStream, Endian, FileType, cachedMemoryBuffer);
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
                    return new FileReader(FullPath, tempMemStream, Endian, FileType, cachedMemoryBuffer);
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
                    return PooledReadOnlyMemoryStream.Rent(segment.Array, segment.Offset, segment.Count);
                }
                var buffer = memStream.ToArray();
                return PooledReadOnlyMemoryStream.Rent(buffer, 0, buffer.Length);
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

        private sealed class PooledReadOnlyMemoryStream : Stream
        {
            private static readonly ConcurrentBag<PooledReadOnlyMemoryStream> Pool = new ConcurrentBag<PooledReadOnlyMemoryStream>();
            private byte[] buffer;
            private int origin;
            private int length;
            private int position;
            private bool isOpen;

            public static PooledReadOnlyMemoryStream Rent(byte[] buffer, int offset, int count)
            {
                if (!Pool.TryTake(out var stream))
                {
                    stream = new PooledReadOnlyMemoryStream();
                }

                stream.buffer = buffer;
                stream.origin = offset;
                stream.length = count;
                stream.position = 0;
                stream.isOpen = true;
                return stream;
            }

            public override bool CanRead => isOpen;
            public override bool CanSeek => isOpen;
            public override bool CanWrite => false;
            public override long Length
            {
                get
                {
                    ThrowIfClosed();
                    return length;
                }
            }

            public override long Position
            {
                get
                {
                    ThrowIfClosed();
                    return position;
                }
                set
                {
                    ThrowIfClosed();
                    if (value < 0 || value > length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(value));
                    }
                    position = (int)value;
                }
            }

            public override int Read(byte[] destination, int offset, int count)
            {
                ThrowIfClosed();
                if (destination == null)
                {
                    throw new ArgumentNullException(nameof(destination));
                }
                if (offset < 0 || count < 0 || destination.Length - offset < count)
                {
                    throw new ArgumentOutOfRangeException();
                }

                var remaining = length - position;
                if (remaining <= 0)
                {
                    return 0;
                }

                var bytesToRead = Math.Min(count, remaining);
                Buffer.BlockCopy(buffer, origin + position, destination, offset, bytesToRead);
                position += bytesToRead;
                return bytesToRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                ThrowIfClosed();
                long newPosition;
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        newPosition = offset;
                        break;
                    case SeekOrigin.Current:
                        newPosition = position + offset;
                        break;
                    case SeekOrigin.End:
                        newPosition = length + offset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(origin));
                }

                if (newPosition < 0 || newPosition > length)
                {
                    throw new IOException("Attempted to seek outside the memory stream bounds.");
                }

                position = (int)newPosition;
                return position;
            }

            public override void Flush()
            {
                ThrowIfClosed();
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && isOpen)
                {
                    buffer = null;
                    origin = 0;
                    length = 0;
                    position = 0;
                    isOpen = false;
                    Pool.Add(this);
                }
                base.Dispose(disposing);
            }

            private void ThrowIfClosed()
            {
                if (!isOpen)
                {
                    throw new ObjectDisposedException(nameof(PooledReadOnlyMemoryStream));
                }
            }
        }
    }
}
