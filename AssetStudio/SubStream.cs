#nullable enable
using System;
using System.IO;

namespace AssetStudio
{
    public class SubStream : Stream
    {
        public string FilePath { get; }
        public long Offset { get; }
        private readonly long length;
        private FileStream? fileStream;
        private long position;

        public SubStream(string filePath, long offset, long length)
        {
            FilePath = filePath;
            Offset = offset;
            this.length = length;
            this.position = 0;
        }

        private FileStream GetStream()
        {
            if (fileStream == null)
            {
                fileStream = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.RandomAccess);
            }
            return fileStream;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length)
                return 0;

            long remaining = length - position;
            int toRead = (int)Math.Min(count, remaining);

            var stream = GetStream();
            stream.Position = this.Offset + position;
            int read = stream.Read(buffer, offset, toRead);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
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
                throw new ArgumentOutOfRangeException(nameof(offset));

            position = newPosition;
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fileStream?.Dispose();
                fileStream = null;
            }
            base.Dispose(disposing);
        }
    }
}
