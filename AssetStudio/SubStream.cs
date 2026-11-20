#nullable enable
using System;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AssetStudio
{
    public class SubStream : Stream
    {
        public string FilePath { get; }
        public long Offset { get; }
        private readonly long length;
        private SharedFileHandle? sharedHandle;
        private Stream? baseStream;
        private readonly object? streamLock;
        private readonly bool leaveStreamOpen;
        private long position;
        private int disposed;

        public SharedFileHandle? SharedHandle => sharedHandle;

        public SafeFileHandle? SafeFileHandle => sharedHandle?.SafeFileHandle;

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
            var slice = buffer.Slice(0, toRead);

            var handle = sharedHandle;
            if (handle != null)
            {
                int read = RandomAccess.Read(handle.SafeFileHandle, slice, Offset + position);
                position += read;
                return read;
            }

            var stream = baseStream;
            if (stream != null)
            {
                lock (streamLock!)
                {
                    stream.Position = Offset + position;
                    int read = stream.Read(slice);
                    position += read;
                    return read;
                }
            }

            throw new ObjectDisposedException(nameof(SubStream));
        }

        public override int ReadByte()
        {
            Span<byte> oneByte = stackalloc byte[1];
            return Read(oneByte) == 1 ? oneByte[0] : -1;
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
