#nullable enable
using System;
using System.IO;

namespace AssetStudio
{
    public class DecryptedStream : Stream
    {
        public Stream BaseStream { get; }
        public string Token { get; }
        private readonly Block512KeyGenerator keyGen;
        private readonly long length;
        private long position;

        private uint cachedBlockIndex = uint.MaxValue;
        private byte[]? cachedKeystream;

        public DecryptedStream(Stream baseStream, string token)
        {
            BaseStream = baseStream;
            Token = token;
            keyGen = new Block512KeyGenerator(token);
            // The encrypted bundle payload is BaseStream length minus the 26-byte header
            length = Math.Max(0, baseStream.Length - 26);
            position = 0;
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
            if (toRead <= 0)
                return 0;

            // Seek the underlying stream to the offset in the payload (header is 26 bytes)
            BaseStream.Position = 26 + position;
            int read = BaseStream.Read(buffer, offset, toRead);
            if (read <= 0)
                return read;

            long curPos = position;
            int bytesRemaining = read;
            while (bytesRemaining > 0)
            {
                uint blockIndex = (uint)(curPos / 512);
                int offsetInBlock = (int)(curPos % 512);
                int bytesToDecrypt = Math.Min(bytesRemaining, 512 - offsetInBlock);

                if (cachedBlockIndex != blockIndex)
                {
                    cachedBlockIndex = blockIndex;
                    cachedKeystream = keyGen.GetKey(blockIndex);
                }

                for (int i = 0; i < bytesToDecrypt; i++)
                {
                    buffer[offset + (int)(curPos - position) + i] ^= cachedKeystream![offsetInBlock + i];
                }

                curPos += bytesToDecrypt;
                bytesRemaining -= bytesToDecrypt;
            }

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
        public override void Flush() => BaseStream.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                BaseStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
