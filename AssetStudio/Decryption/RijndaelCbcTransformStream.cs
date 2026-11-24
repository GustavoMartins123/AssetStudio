#nullable enable
using System;
using System.IO;

namespace AssetStudio
{
    public sealed class RijndaelCbcTransformStream : Stream
    {
        private readonly byte[] iv;
        private readonly long headerSkipBytes;
        private readonly bool pkcs7Padding;
        private readonly long cipherLength;
        private readonly long length;
        private readonly byte[] scratch;
        private readonly byte[] plainBlock;
        private long position;

        public Stream BaseStream { get; }
        public RijndaelBlockCipher Cipher { get; }
        public int BlockSize => Cipher.BlockSize;
        public long HeaderSkipBytes => headerSkipBytes;
        public bool Pkcs7Padding => pkcs7Padding;
        internal bool LeaveBaseStreamOpen { get; set; }

        public RijndaelCbcTransformStream(
            Stream baseStream,
            RijndaelBlockCipher cipher,
            ReadOnlySpan<byte> iv,
            long headerSkipBytes = 0,
            bool pkcs7Padding = false)
        {
            ArgumentNullException.ThrowIfNull(baseStream);
            ArgumentNullException.ThrowIfNull(cipher);
            if (!baseStream.CanRead || !baseStream.CanSeek)
            {
                throw new ArgumentException("Base stream must be readable and seekable.", nameof(baseStream));
            }

            if (headerSkipBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(headerSkipBytes));
            }

            if (iv.Length != cipher.BlockSize)
            {
                throw new ArgumentException($"IV must be exactly {cipher.BlockSize} bytes.", nameof(iv));
            }

            BaseStream = baseStream;
            Cipher = cipher;
            this.iv = iv.ToArray();
            this.headerSkipBytes = headerSkipBytes;
            this.pkcs7Padding = pkcs7Padding;
            scratch = new byte[cipher.BlockSize * 2];
            plainBlock = new byte[cipher.BlockSize];

            long payloadLength = Math.Max(0, baseStream.Length - headerSkipBytes);
            cipherLength = payloadLength / BlockSize * BlockSize;
            length = ComputeLogicalLength();
            position = 0;
        }

        private long ComputeLogicalLength()
        {
            if (!pkcs7Padding || cipherLength < BlockSize)
            {
                return cipherLength;
            }

            if (!TryDecryptBlock(cipherLength / BlockSize - 1, plainBlock))
            {
                return cipherLength;
            }

            int pad = plainBlock[BlockSize - 1];
            if (pad == 0 || pad > BlockSize)
            {
                return cipherLength;
            }

            for (int i = BlockSize - pad; i < BlockSize; i++)
            {
                if (plainBlock[i] != pad)
                {
                    return cipherLength;
                }
            }

            return cipherLength - pad;
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
            int total = 0;
            int blockSize = BlockSize;
            while (total < toRead)
            {
                long current = position + total;
                long blockIndex = current / blockSize;
                int offsetInBlock = (int)(current % blockSize);
                if (!TryDecryptBlock(blockIndex, plainBlock))
                {
                    break;
                }

                int copy = Math.Min(blockSize - offsetInBlock, toRead - total);
                plainBlock.AsSpan(offsetInBlock, copy).CopyTo(buffer.Slice(total, copy));
                total += copy;
            }

            position += total;
            return total;
        }

        private bool TryDecryptBlock(long blockIndex, Span<byte> destination)
        {
            int blockSize = BlockSize;
            bool hasPrevious = blockIndex > 0;
            int needed = hasPrevious ? blockSize * 2 : blockSize;
            long blockOffset = headerSkipBytes + blockIndex * blockSize - (hasPrevious ? blockSize : 0);

            BaseStream.Position = blockOffset;
            int total = 0;
            while (total < needed)
            {
                int read = BaseStream.Read(scratch, total, needed - total);
                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            ReadOnlySpan<byte> previous = hasPrevious ? scratch.AsSpan(0, blockSize) : iv;
            Cipher.DecryptBlock(scratch.AsSpan(hasPrevious ? blockSize : 0, blockSize), destination);
            for (int i = 0; i < blockSize; i++)
            {
                destination[i] ^= previous[i];
            }

            return true;
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

        public RijndaelCbcTransformStream Clone(Stream clonedBaseStream)
            => new RijndaelCbcTransformStream(clonedBaseStream, Cipher, iv, headerSkipBytes, pkcs7Padding);

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
