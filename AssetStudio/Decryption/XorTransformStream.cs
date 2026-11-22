#nullable enable
using System;
using System.IO;

namespace AssetStudio
{
    public sealed class XorTransformStream : Stream
    {
        private readonly byte[] xorKey;
        private readonly long headerSkipBytes;
        private readonly long rangeStart;
        private readonly long rangeLength;
        private readonly long length;
        private long position;

        public Stream BaseStream { get; }
        public ReadOnlySpan<byte> XorKey => xorKey;
        public long HeaderSkipBytes => headerSkipBytes;
        public long RangeStart => rangeStart;
        public long RangeLength => rangeLength;
        internal bool LeaveBaseStreamOpen { get; set; }

        public XorTransformStream(
            Stream baseStream,
            byte[] xorKey,
            long headerSkipBytes = 0,
            long rangeStart = 0,
            long rangeLength = -1)
        {
            ArgumentNullException.ThrowIfNull(baseStream);
            ArgumentNullException.ThrowIfNull(xorKey);
            if (xorKey.Length == 0)
            {
                throw new ArgumentException("XOR key must not be empty.", nameof(xorKey));
            }
            if (headerSkipBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(headerSkipBytes));
            }

            BaseStream = baseStream;
            this.xorKey = (byte[])xorKey.Clone();
            this.headerSkipBytes = headerSkipBytes;
            this.rangeStart = Math.Max(0, rangeStart);
            this.length = Math.Max(0, baseStream.Length - headerSkipBytes);
            this.rangeLength = rangeLength < 0 ? this.length : rangeLength;
            this.position = 0;
        }

        public override bool CanRead => BaseStream.CanRead;
        public override bool CanSeek => BaseStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (position >= length || buffer.IsEmpty)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, length - position);
            var targetSlice = buffer.Slice(0, toRead);

            BaseStream.Position = headerSkipBytes + position;
            int read = BaseStream.Read(targetSlice);
            if (read <= 0)
            {
                return 0;
            }

            long readStart = position;
            long readEnd = position + read;
            long xorStart = Math.Max(readStart, rangeStart);
            long xorEnd = rangeLength >= 0
                ? Math.Min(readEnd, rangeStart + rangeLength)
                : readEnd;

            if (xorEnd > xorStart)
            {
                int sliceOffset = (int)(xorStart - readStart);
                int sliceCount = (int)(xorEnd - xorStart);
                UnityHeaderProbe.XorBytesRepeating(
                    targetSlice.Slice(sliceOffset, sliceCount),
                    xorStart - rangeStart,
                    xorKey);
            }

            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPosition < 0 || newPosition > length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            position = newPosition;
            return position;
        }

        public XorTransformStream Clone(Stream clonedBaseStream)
            => new XorTransformStream(clonedBaseStream, xorKey, headerSkipBytes, rangeStart, rangeLength);

        public override void Flush() => BaseStream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !LeaveBaseStreamOpen)
            {
                BaseStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
