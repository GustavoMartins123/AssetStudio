#nullable enable
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public sealed class HeaderMaskStream : Stream
    {
        private readonly byte[] mask;
        private readonly long length;
        private long position;

        public Stream InnerStream { get; }
        public int MaskLength => mask.Length;
        internal bool LeaveInnerStreamOpen { get; set; }

        public HeaderMaskStream(Stream innerStream, ReadOnlySpan<byte> mask, bool leaveInnerStreamOpen = false)
        {
            ArgumentNullException.ThrowIfNull(innerStream);
            if (!innerStream.CanRead || !innerStream.CanSeek)
            {
                throw new ArgumentException("Inner stream must be readable and seekable.", nameof(innerStream));
            }

            if (mask.IsEmpty)
            {
                throw new ArgumentException("Mask must not be empty.", nameof(mask));
            }

            InnerStream = innerStream;
            this.mask = mask.ToArray();
            length = innerStream.Length;
            InnerStream.Position = 0;
            position = 0;
            LeaveInnerStreamOpen = leaveInnerStreamOpen;
        }

        public override bool CanRead => InnerStream.CanRead;
        public override bool CanSeek => InnerStream.CanSeek;
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
                InnerStream.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            int read = InnerStream.Read(buffer, offset, count);
            int masked = (int)Math.Min(read, Math.Max(0L, mask.Length - position));
            for (int i = 0; i < masked; i++)
            {
                buffer[offset + i] ^= mask[position + i];
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

            Position = newPosition;
            return position;
        }

        public HeaderMaskStream Clone(Stream clonedInnerStream, bool leaveInnerStreamOpen = false)
            => new HeaderMaskStream(clonedInnerStream, mask, leaveInnerStreamOpen);

        public override void Flush() => InnerStream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !LeaveInnerStreamOpen)
            {
                InnerStream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public static class HeaderMasks
    {
        public static byte[] XorShift32(uint seed, int length, int leftShift1 = 13, int rightShift = 17, int leftShift2 = 5, int byteShift = 11)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var mask = new byte[length];
            uint state = seed;
            for (int i = 0; i < length; i++)
            {
                state ^= state << leftShift1;
                state ^= state >> rightShift;
                state ^= state << leftShift2;
                mask[i] = (byte)(state >> byteShift);
            }

            return mask;
        }

        public static byte[] HmacSha256Counter(ReadOnlySpan<byte> key, int length, int blockLength = 32)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (blockLength is <= 0 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(blockLength));
            }

            var mask = new byte[length];
            Span<byte> counter = stackalloc byte[4];
            Span<byte> block = stackalloc byte[32];
            for (int i = 0; i * blockLength < length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(counter, i);
                HMACSHA256.TryHashData(key, counter, block, out int written);
                int offset = i * blockLength;
                block.Slice(0, Math.Min(written, Math.Min(blockLength, length - offset))).CopyTo(mask.AsSpan(offset));
            }

            return mask;
        }
    }

    public sealed class HeaderMaskProfile
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "xorshift32";

        [JsonPropertyName("length")]
        public int Length { get; set; } = 256;

        [JsonPropertyName("seed")]
        public string? Seed { get; set; }

        [JsonPropertyName("leftShift1")]
        public int LeftShift1 { get; set; } = 13;

        [JsonPropertyName("rightShift")]
        public int RightShift { get; set; } = 17;

        [JsonPropertyName("leftShift2")]
        public int LeftShift2 { get; set; } = 5;

        [JsonPropertyName("byteShift")]
        public int ByteShift { get; set; } = 11;

        [JsonPropertyName("blockLength")]
        public int BlockLength { get; set; } = 32;

        public HeaderMaskProfile Clone() => (HeaderMaskProfile)MemberwiseClone();

        public bool Validate(out string? error)
        {
            error = null;
            string kind = (Type ?? string.Empty).Trim().ToLowerInvariant();
            if (kind == "xorshift32")
            {
                if (!TryParseSeed(out _, out string? seedError))
                {
                    error = seedError;
                    return false;
                }

                if (LeftShift1 is < 1 or > 31 || RightShift is < 1 or > 31 || LeftShift2 is < 1 or > 31 || ByteShift is < 0 or > 24)
                {
                    error = "xorshift32 shifts must be between 1 and 31, and the byte shift between 0 and 24.";
                    return false;
                }
            }
            else if (kind == "hmac-sha256-counter")
            {
                if (BlockLength is <= 0 or > 32)
                {
                    error = "HMAC-SHA256 block length must be between 1 and 32.";
                    return false;
                }
            }
            else
            {
                error = $"Unsupported header mask type '{Type}'.";
                return false;
            }

            if (Length <= 0)
            {
                error = "Header mask length must be positive.";
                return false;
            }

            return true;
        }

        public bool TryCreate(ReadOnlySpan<byte> key, long cipherLength, out byte[] mask)
        {
            mask = Array.Empty<byte>();
            if (!Validate(out _) || cipherLength <= 0)
            {
                return false;
            }

            int length = (int)Math.Min(Length, cipherLength);
            TryParseSeed(out uint seed, out _);
            mask = Type.Trim().ToLowerInvariant() == "xorshift32"
                ? HeaderMasks.XorShift32(seed, length, LeftShift1, RightShift, LeftShift2, ByteShift)
                : HeaderMasks.HmacSha256Counter(key, length, BlockLength);
            return true;
        }

        private bool TryParseSeed(out uint seed, out string? error)
        {
            seed = 0;
            error = null;
            string text = (Seed ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return true;
            }

            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hex)
            {
                text = text.Substring(2);
            }
            else
            {
                hex = text.IndexOfAny(new[] { 'a', 'b', 'c', 'd', 'e', 'f', 'A', 'B', 'C', 'D', 'E', 'F' }) >= 0;
            }

            if (uint.TryParse(text, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out seed))
            {
                return true;
            }

            error = "Header mask seed must be a 32-bit integer.";
            return false;
        }
    }
}
