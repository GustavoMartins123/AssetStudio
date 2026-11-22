#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AssetStudio
{
    public class SubStream : Stream
    {
        private const int DefaultReadBufferSize = 32 * 1024;

        public string FilePath { get; }
        public long Offset { get; }
        private readonly long length;
        private SharedFileHandle? sharedHandle;
        private Stream? baseStream;
        private readonly object? streamLock;
        private readonly bool leaveStreamOpen;
        private long position;
        private int disposed;
        private byte[]? readBuffer;
        private long readBufferFilePos;
        private int readBufferCount;
        private long underlyingReadCount;

        public long UnderlyingReadCount => Volatile.Read(ref underlyingReadCount);

        public SharedFileHandle? SharedHandle => sharedHandle;

        public SafeFileHandle? SafeFileHandle => sharedHandle?.SafeFileHandle;

        public Stream? BaseStream => baseStream;

        public SubStream(string filePath, long offset, long length)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(filePath));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            FilePath = filePath;
            Offset = offset;
            this.length = length;
            this.position = 0;
            this.sharedHandle = SharedFileHandle.Acquire(filePath);
            this.leaveStreamOpen = true;
        }

        public SubStream(SharedFileHandle sharedHandle, long offset, long length)
        {
            if (sharedHandle == null)
            {
                throw new ArgumentNullException(nameof(sharedHandle));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            this.sharedHandle = sharedHandle.AddRef();
            FilePath = sharedHandle.FilePath;
            Offset = offset;
            this.length = length;
            this.position = 0;
            this.leaveStreamOpen = true;
        }

        public SubStream(Stream baseStream, long offset, long length, object? syncRoot = null, bool leaveOpen = true)
        {
            if (baseStream == null)
            {
                throw new ArgumentNullException(nameof(baseStream));
            }
            if (!baseStream.CanRead || !baseStream.CanSeek)
            {
                throw new ArgumentException("Base stream must support reading and seeking.", nameof(baseStream));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            FilePath = baseStream is FileStream fs ? fs.Name : string.Empty;
            Offset = offset;
            this.length = length;
            this.position = 0;
            this.baseStream = baseStream;
            this.streamLock = syncRoot ?? baseStream;
            this.leaveStreamOpen = leaveOpen;
        }

        public override bool CanRead => Volatile.Read(ref disposed) == 0;
        public override bool CanSeek => Volatile.Read(ref disposed) == 0;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                ThrowIfDisposed();
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("Invalid offset and count for buffer length.");
            }

            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            if (position >= length || buffer.IsEmpty)
            {
                return 0;
            }

            long remaining = length - position;
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int totalCopied = 0;

            while (totalCopied < toRead)
            {
                int need = toRead - totalCopied;

                // 1) Serve directly from read-ahead buffer when current position falls within buffered window.
                var buf = readBuffer;
                if (buf != null)
                {
                    long bufOffset = position - readBufferFilePos;
                    if ((ulong)bufOffset < (ulong)readBufferCount)
                    {
                        int available = readBufferCount - (int)bufOffset;
                        int copyCount = Math.Min(need, available);
                        buf.AsSpan((int)bufOffset, copyCount).CopyTo(buffer.Slice(totalCopied, copyCount));
                        position += copyCount;
                        totalCopied += copyCount;
                        continue;
                    }
                }

                // 2) Bypass read-ahead buffer for large reads to avoid an extra copy.
                if (need >= DefaultReadBufferSize)
                {
                    int directRead = ReadUncached(buffer.Slice(totalCopied, need), position);
                    if (directRead <= 0)
                    {
                        break;
                    }
                    position += directRead;
                    totalCopied += directRead;
                    continue;
                }

                // 3) Refill bounded read-ahead buffer for small sequential/nearby reads.
                if (buf == null)
                {
                    int targetSize = (int)Math.Min(DefaultReadBufferSize, length);
                    buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, targetSize));
                    readBuffer = buf;
                }

                int maxChunk = Math.Min(DefaultReadBufferSize, buf.Length);
                int refillSize = (int)Math.Min(maxChunk, length - position);
                int filled = ReadUncached(buf.AsSpan(0, refillSize), position);
                readBufferFilePos = position;
                readBufferCount = filled;

                if (filled <= 0)
                {
                    break;
                }

                int take = Math.Min(need, filled);
                buf.AsSpan(0, take).CopyTo(buffer.Slice(totalCopied, take));
                position += take;
                totalCopied += take;
            }

            return totalCopied;
        }

        public override int ReadByte()
        {
            ThrowIfDisposed();
            if (position >= length)
            {
                return -1;
            }

            var buf = readBuffer;
            if (buf != null)
            {
                long bufOffset = position - readBufferFilePos;
                if ((ulong)bufOffset < (ulong)readBufferCount)
                {
                    position++;
                    return buf[(int)bufOffset];
                }
            }

            Span<byte> oneByte = stackalloc byte[1];
            return Read(oneByte) == 1 ? oneByte[0] : -1;
        }

        private int ReadUncached(Span<byte> slice, long subStreamOffset)
        {
            var handle = sharedHandle;
            if (handle != null)
            {
                Interlocked.Increment(ref underlyingReadCount);
                return RandomAccess.Read(handle.SafeFileHandle, slice, Offset + subStreamOffset);
            }

            var stream = baseStream;
            if (stream != null)
            {
                lock (streamLock!)
                {
                    Interlocked.Increment(ref underlyingReadCount);
                    stream.Position = Offset + subStreamOffset;
                    return stream.Read(slice);
                }
            }

            throw new ObjectDisposedException(nameof(SubStream));
        }

        public int ReadAt(long subStreamOffset, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("Invalid offset and count for buffer length.");
            }

            return ReadAt(subStreamOffset, buffer.AsSpan(offset, count));
        }

        public int ReadAt(long subStreamOffset, Span<byte> buffer)
        {
            ThrowIfDisposed();
            if (subStreamOffset < 0 || subStreamOffset > length)
            {
                throw new ArgumentOutOfRangeException(nameof(subStreamOffset));
            }
            if (subStreamOffset >= length || buffer.IsEmpty)
            {
                return 0;
            }

            long remaining = length - subStreamOffset;
            int toRead = (int)Math.Min(buffer.Length, remaining);
            var slice = buffer.Slice(0, toRead);

            var handle = sharedHandle;
            if (handle != null)
            {
                return RandomAccess.Read(handle.SafeFileHandle, slice, Offset + subStreamOffset);
            }

            var stream = baseStream;
            if (stream != null)
            {
                lock (streamLock!)
                {
                    stream.Position = Offset + subStreamOffset;
                    return stream.Read(slice);
                }
            }

            throw new ObjectDisposedException(nameof(SubStream));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            long newPosition = position;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;
                case SeekOrigin.Current:
                    newPosition += offset;
                    break;
                case SeekOrigin.End:
                    newPosition = length + offset;
                    break;
            }

            if (newPosition < 0 || newPosition > length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            position = newPosition;
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SubStream));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && disposing)
            {
                var buf = Interlocked.Exchange(ref readBuffer, null);
                readBufferCount = 0;
                if (buf != null)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                var handle = Interlocked.Exchange(ref sharedHandle, null);
                handle?.Release();

                var stream = Interlocked.Exchange(ref baseStream, null);
                if (stream != null && !leaveStreamOpen)
                {
                    stream.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
