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
        public string? AppliedDecryptionProvider { get; }
        public StreamEncryptionBarrier? EncryptionBarrier { get; }
        private byte[]? cachedMemoryBuffer;
        private readonly object cachedMemoryBufferLock = new object();

        private static readonly byte[] gzipMagic = { 0x1f, 0x8b };
        private static readonly byte[] brotliMagic = { 0x62, 0x72, 0x6F, 0x74, 0x6C, 0x69 };
        private static readonly byte[] zipMagic = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] zipSpannedMagic = { 0x50, 0x4B, 0x07, 0x08 };
        private const int FileTypeHeaderBytes = 64;

        public FileReader(string path) : this(path, File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }

        public FileReader(string path, Stream stream)
            : this(path, PrepareStream(stream, path), EndianType.BigEndian, isInitialOpen: true)
        {
        }

        private FileReader(string path, (Stream Stream, string? ProviderId, StreamEncryptionBarrier? Barrier) prepared, EndianType endian, bool isInitialOpen)
            : base(prepared.Stream, endian)
        {
            FullPath = Path.GetFullPath(path);
            FileName = Path.GetFileName(path);
            AppliedDecryptionProvider = prepared.ProviderId;
            EncryptionBarrier = prepared.Barrier;
            FileType = CheckFileType();
        }

        private FileReader(string path, Stream stream, EndianType endian, FileType fileType, byte[]? cachedMemoryBuffer, string? appliedDecryptionProvider = null, StreamEncryptionBarrier? encryptionBarrier = null) : base(stream, endian)
        {
            FullPath = path;
            FileName = Path.GetFileName(path);
            FileType = fileType;
            AppliedDecryptionProvider = appliedDecryptionProvider;
            EncryptionBarrier = encryptionBarrier;
            this.cachedMemoryBuffer = cachedMemoryBuffer;
            Position = 0;
        }

        private static (Stream Stream, string? ProviderId, StreamEncryptionBarrier? Barrier) PrepareStream(Stream stream, string path)
        {
            var resolved = DecryptionHelper.CheckAndDecryptStream(stream, path, out var providerId, out var barrier) ?? stream;
            return (resolved, providerId, barrier);
        }

        private FileType CheckFileType()
        {
            Position = 0;
            var headerLength = (int)System.Math.Min(FileTypeHeaderBytes, BaseStream.Length);
            var header = ReadBytes(headerLength);
            Position = 0;

            var signature = UnityHeaderProbe.ReadNullTerminatedAscii(header, 0, 20, out _);
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
                        if (UnityHeaderProbe.MatchesAt(header, 0, gzipMagic))
                        {
                            return FileType.GZipFile;
                        }
                        if (UnityHeaderProbe.MatchesAt(header, 0x20, brotliMagic))
                        {
                            return FileType.BrotliFile;
                        }
                        if (UnityHeaderProbe.IsSerializedFileHeader(header, BaseStream.Length))
                        {
                            return FileType.AssetsFile;
                        }
                        if (UnityHeaderProbe.MatchesAt(header, 0, zipMagic) || UnityHeaderProbe.MatchesAt(header, 0, zipSpannedMagic))
                            return FileType.ZipFile;
                        return FileType.ResourceFile;
                    }
            }
        }

        public FileReader Clone()
        {
            if (BaseStream is MemoryStream memStream)
            {
                if (memStream.TryGetBuffer(out var segment) && segment.Array != null)
                {
                    var segmentStream = PooledReadOnlyMemoryStream.Rent(segment.Array, segment.Offset, segment.Count);
                    return new FileReader(FullPath, segmentStream, Endian, FileType, cachedMemoryBuffer, AppliedDecryptionProvider, EncryptionBarrier);
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
                return new FileReader(FullPath, newStream, Endian, FileType, cachedMemoryBuffer, AppliedDecryptionProvider, EncryptionBarrier);
            }
            else if (BaseStream is DecryptedStream decStream)
            {
                var clonedUnderlying = CloneStream(decStream.BaseStream);
                var newDecStream = new DecryptedStream(clonedUnderlying, decStream.Token);
                return new FileReader(FullPath, newDecStream, Endian, FileType, cachedMemoryBuffer, AppliedDecryptionProvider, EncryptionBarrier);
            }
            else if (BaseStream is XorTransformStream xorStream)
            {
                var clonedUnderlying = CloneStream(xorStream.BaseStream);
                var newXorStream = xorStream.Clone(clonedUnderlying);
                return new FileReader(FullPath, newXorStream, Endian, FileType, cachedMemoryBuffer, AppliedDecryptionProvider, EncryptionBarrier);
            }
            else if (BaseStream is CipherTransformStream cipherStream)
            {
                var clonedUnderlying = CloneStream(cipherStream.BaseStream);
                var newCipherStream = cipherStream.Clone(clonedUnderlying);
                return new FileReader(FullPath, newCipherStream, Endian, FileType, cachedMemoryBuffer, AppliedDecryptionProvider, EncryptionBarrier);
            }
            else if (BaseStream is SubStream subStream)
            {
                Stream newStream = !string.IsNullOrEmpty(subStream.FilePath)
                    ? new SubStream(subStream.FilePath, subStream.Offset, subStream.Length)
                    : subStream.BaseStream != null
                        ? new SubStream(CloneStream(subStream.BaseStream), subStream.Offset, subStream.Length, leaveOpen: false)
                        : throw new NotSupportedException("Cannot clone detached SubStream.");
                return new FileReader(FullPath, newStream, Endian, FileType, cachedMemoryBuffer, AppliedDecryptionProvider, EncryptionBarrier);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cachedMemoryBuffer = null;
            }
            base.Dispose(disposing);
        }

        private sealed class PooledReadOnlyMemoryStream : Stream
        {
            private static readonly ConcurrentBag<PooledReadOnlyMemoryStream> Pool = new ConcurrentBag<PooledReadOnlyMemoryStream>();
            private byte[] buffer = Array.Empty<byte>();
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
                    buffer = Array.Empty<byte>();
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
