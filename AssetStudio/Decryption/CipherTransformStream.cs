#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace AssetStudio
{
    public enum StreamCipherAlgorithm
    {
        None = 0,
        Xor = 1,
        AesCbc = 2,
        AesEcb = 3,
        AesCtr = 4,
        ChaCha20 = 5,
        Block512 = 6
    }

    /// <summary>
    /// Seekable, random-access decryption stream supporting standard symmetric ciphers
    /// (AES-CBC, AES-ECB, AES-CTR, ChaCha20) over either the full stream or a bounded byte range.
    /// </summary>
    public sealed class CipherTransformStream : Stream
    {
        private const int AesBlockSize = 16;
        private const int ChaChaBlockSize = 64;

        private readonly StreamCipherAlgorithm algorithm;
        private readonly byte[] key;
        private readonly byte[] ivOrNonce;
        private readonly long headerSkipBytes;
        private readonly long rangeStart;
        private readonly long rangeLength;
        private readonly bool pkcs7Padding;
        private readonly uint counterStart;
        private readonly bool counterLittleEndian;

        private readonly long rawLength;
        private readonly long logicalLength;
        private readonly long cipherRangeStart;
        private readonly long cipherRangeEnd;

        private readonly Aes? aes;
        private readonly byte[] scratchIn = new byte[AesBlockSize * 2];
        private readonly byte[] scratchOut = new byte[AesBlockSize];
        private readonly byte[] ctrBlock = new byte[AesBlockSize];
        private readonly byte[] chaChaBlock = new byte[ChaChaBlockSize];
        private long position;

        public Stream BaseStream { get; }
        public StreamCipherAlgorithm Algorithm => algorithm;
        public ReadOnlySpan<byte> Key => key;
        public ReadOnlySpan<byte> IvOrNonce => ivOrNonce;
        public long HeaderSkipBytes => headerSkipBytes;
        public long RangeStart => rangeStart;
        public long RangeLength => rangeLength;
        public bool Pkcs7Padding => pkcs7Padding;
        public uint CounterStart => counterStart;
        public bool CounterLittleEndian => counterLittleEndian;
        internal bool LeaveBaseStreamOpen { get; set; }

        public CipherTransformStream(
            Stream baseStream,
            StreamCipherAlgorithm algorithm,
            byte[] key,
            byte[]? ivOrNonce = null,
            long headerSkipBytes = 0,
            long rangeStart = 0,
            long rangeLength = -1,
            bool pkcs7Padding = false,
            uint counterStart = 0,
            bool counterLittleEndian = false)
        {
            ArgumentNullException.ThrowIfNull(baseStream);
            ArgumentNullException.ThrowIfNull(key);
            if (headerSkipBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(headerSkipBytes));
            }

            ValidateParameters(algorithm, key, ivOrNonce);

            BaseStream = baseStream;
            this.algorithm = algorithm;
            this.key = (byte[])key.Clone();
            this.ivOrNonce = NormalizeIvOrNonce(algorithm, ivOrNonce);
            this.headerSkipBytes = headerSkipBytes;
            this.rangeStart = Math.Max(0, rangeStart);
            this.rangeLength = rangeLength;
            this.pkcs7Padding = pkcs7Padding;
            this.counterStart = counterStart;
            this.counterLittleEndian = counterLittleEndian;

            rawLength = Math.Max(0, baseStream.Length - headerSkipBytes);
            cipherRangeStart = Math.Min(this.rangeStart, rawLength);
            cipherRangeEnd = rangeLength < 0
                ? rawLength
                : Math.Min(rawLength, cipherRangeStart + rangeLength);

            if (algorithm is StreamCipherAlgorithm.AesCbc or StreamCipherAlgorithm.AesEcb or StreamCipherAlgorithm.AesCtr)
            {
                aes = Aes.Create();
                aes.KeySize = this.key.Length * 8;
                aes.Key = this.key;
                aes.Padding = PaddingMode.None;
                aes.Mode = CipherMode.ECB;
            }

            logicalLength = ComputeLogicalLength();
            position = 0;
        }

        private static void ValidateParameters(StreamCipherAlgorithm algorithm, byte[] key, byte[]? ivOrNonce)
        {
            switch (algorithm)
            {
                case StreamCipherAlgorithm.AesCbc:
                case StreamCipherAlgorithm.AesEcb:
                case StreamCipherAlgorithm.AesCtr:
                    if (key.Length is not (16 or 24 or 32))
                    {
                        throw new ArgumentException($"AES key must be 16, 24, or 32 bytes (got {key.Length}).", nameof(key));
                    }
                    if (algorithm is StreamCipherAlgorithm.AesCbc or StreamCipherAlgorithm.AesCtr
                        && ivOrNonce != null && ivOrNonce.Length != 0 && ivOrNonce.Length != 16 && !(algorithm == StreamCipherAlgorithm.AesCtr && ivOrNonce.Length == 12))
                    {
                        throw new ArgumentException($"AES IV/nonce must be 16 bytes (or 12 bytes for CTR); got {ivOrNonce.Length}.", nameof(ivOrNonce));
                    }
                    break;

                case StreamCipherAlgorithm.ChaCha20:
                    if (key.Length != 32)
                    {
                        throw new ArgumentException($"ChaCha20 key must be 32 bytes (got {key.Length}).", nameof(key));
                    }
                    if (ivOrNonce != null && ivOrNonce.Length != 0 && ivOrNonce.Length != 12)
                    {
                        throw new ArgumentException($"ChaCha20 nonce must be 12 bytes (got {ivOrNonce.Length}).", nameof(ivOrNonce));
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported stream cipher algorithm.");
            }
        }

        private static byte[] NormalizeIvOrNonce(StreamCipherAlgorithm algorithm, byte[]? ivOrNonce)
        {
            if (algorithm == StreamCipherAlgorithm.ChaCha20)
            {
                var nonce = new byte[12];
                if (ivOrNonce != null && ivOrNonce.Length > 0)
                {
                    Buffer.BlockCopy(ivOrNonce, 0, nonce, 0, Math.Min(ivOrNonce.Length, 12));
                }
                return nonce;
            }

            var iv = new byte[AesBlockSize];
            if (ivOrNonce != null && ivOrNonce.Length > 0)
            {
                Buffer.BlockCopy(ivOrNonce, 0, iv, 0, Math.Min(ivOrNonce.Length, AesBlockSize));
            }
            return iv;
        }

        private long ComputeLogicalLength()
        {
            if (!pkcs7Padding
                || (algorithm != StreamCipherAlgorithm.AesCbc && algorithm != StreamCipherAlgorithm.AesEcb)
                || cipherRangeEnd != rawLength)
            {
                return rawLength;
            }

            long cipherBytes = cipherRangeEnd - cipherRangeStart;
            if (cipherBytes < AesBlockSize || (cipherBytes % AesBlockSize) != 0)
            {
                return rawLength;
            }

            long lastBlockIndex = (cipherBytes / AesBlockSize) - 1;
            Span<byte> lastBlockPlain = stackalloc byte[AesBlockSize];
            if (!TryDecryptAesBlock(lastBlockIndex, lastBlockPlain))
            {
                return rawLength;
            }

            byte pad = lastBlockPlain[AesBlockSize - 1];
            if (pad == 0 || pad > AesBlockSize)
            {
                return rawLength;
            }

            for (int i = AesBlockSize - pad; i < AesBlockSize; i++)
            {
                if (lastBlockPlain[i] != pad)
                {
                    return rawLength;
                }
            }

            return rawLength - pad;
        }

        public override bool CanRead => BaseStream.CanRead;
        public override bool CanSeek => BaseStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => logicalLength;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > logicalLength)
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
            if (position >= logicalLength || buffer.IsEmpty)
            {
                return 0;
            }

            int totalToRead = (int)Math.Min(buffer.Length, logicalLength - position);
            int totalRead = 0;

            while (totalRead < totalToRead)
            {
                long currentPos = position + totalRead;
                int remaining = totalToRead - totalRead;

                if (currentPos < cipherRangeStart)
                {
                    int plainLen = (int)Math.Min(remaining, cipherRangeStart - currentPos);
                    int got = ReadRawExact(currentPos, buffer.Slice(totalRead, plainLen));
                    if (got <= 0) break;
                    totalRead += got;
                }
                else if (currentPos >= cipherRangeEnd)
                {
                    int got = ReadRawExact(currentPos, buffer.Slice(totalRead, remaining));
                    if (got <= 0) break;
                    totalRead += got;
                }
                else
                {
                    int cipherLen = (int)Math.Min(remaining, cipherRangeEnd - currentPos);
                    int got = ReadCipherRange(currentPos, buffer.Slice(totalRead, cipherLen));
                    if (got <= 0) break;
                    totalRead += got;
                }
            }

            position += totalRead;
            return totalRead;
        }

        private int ReadRawExact(long logicalOffset, Span<byte> destination)
        {
            BaseStream.Position = headerSkipBytes + logicalOffset;
            int total = 0;
            while (total < destination.Length)
            {
                int n = BaseStream.Read(destination.Slice(total));
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private int ReadCipherRange(long logicalOffset, Span<byte> destination)
        {
            return algorithm switch
            {
                StreamCipherAlgorithm.AesCbc or StreamCipherAlgorithm.AesEcb => ReadAesBlockRange(logicalOffset, destination),
                StreamCipherAlgorithm.AesCtr => ReadAesCtrRange(logicalOffset, destination),
                StreamCipherAlgorithm.ChaCha20 => ReadChaCha20Range(logicalOffset, destination),
                _ => 0
            };
        }

        private int ReadAesBlockRange(long logicalOffset, Span<byte> destination)
        {
            long cipherOffset = logicalOffset - cipherRangeStart;
            long availableCipherBytes = cipherRangeEnd - cipherRangeStart;
            long fullBlockBytes = (availableCipherBytes / AesBlockSize) * AesBlockSize;

            int written = 0;
            Span<byte> plainBlock = stackalloc byte[AesBlockSize];

            while (written < destination.Length)
            {
                long cur = cipherOffset + written;
                if (cur >= fullBlockBytes)
                {
                    // Trailing partial bytes beyond full 16-byte blocks pass through unmodified.
                    int tailLen = destination.Length - written;
                    int rawGot = ReadRawExact(cipherRangeStart + cur, destination.Slice(written, tailLen));
                    written += rawGot;
                    break;
                }

                long blockIndex = cur / AesBlockSize;
                int offsetInBlock = (int)(cur % AesBlockSize);
                if (!TryDecryptAesBlock(blockIndex, plainBlock))
                {
                    break;
                }

                int copyLen = Math.Min(AesBlockSize - offsetInBlock, destination.Length - written);
                plainBlock.Slice(offsetInBlock, copyLen).CopyTo(destination.Slice(written, copyLen));
                written += copyLen;
            }

            return written;
        }

        private bool TryDecryptAesBlock(long blockIndex, Span<byte> plainOut)
        {
            if (aes == null)
            {
                return false;
            }

            long blockOffset = cipherRangeStart + (blockIndex * AesBlockSize);
            if (algorithm == StreamCipherAlgorithm.AesCbc && blockIndex > 0)
            {
                // Read previous ciphertext block + current ciphertext block in one seek/read.
                int read = ReadRawExact(blockOffset - AesBlockSize, scratchIn.AsSpan(0, AesBlockSize * 2));
                if (read < AesBlockSize * 2)
                {
                    return false;
                }

                aes.DecryptEcb(scratchIn.AsSpan(AesBlockSize, AesBlockSize), scratchOut, PaddingMode.None);
                for (int i = 0; i < AesBlockSize; i++)
                {
                    plainOut[i] = (byte)(scratchOut[i] ^ scratchIn[i]);
                }
                return true;
            }
            else
            {
                int read = ReadRawExact(blockOffset, scratchIn.AsSpan(0, AesBlockSize));
                if (read < AesBlockSize)
                {
                    return false;
                }

                aes.DecryptEcb(scratchIn.AsSpan(0, AesBlockSize), scratchOut, PaddingMode.None);
                if (algorithm == StreamCipherAlgorithm.AesCbc)
                {
                    for (int i = 0; i < AesBlockSize; i++)
                    {
                        plainOut[i] = (byte)(scratchOut[i] ^ ivOrNonce[i]);
                    }
                }
                else
                {
                    scratchOut.AsSpan(0, AesBlockSize).CopyTo(plainOut);
                }
                return true;
            }
        }

        private int ReadAesCtrRange(long logicalOffset, Span<byte> destination)
        {
            if (aes == null)
            {
                return 0;
            }

            int rawRead = ReadRawExact(logicalOffset, destination);
            if (rawRead <= 0)
            {
                return 0;
            }

            long cipherOffset = logicalOffset - cipherRangeStart;
            int processed = 0;
            while (processed < rawRead)
            {
                long cur = cipherOffset + processed;
                long blockIndex = cur / AesBlockSize;
                int offsetInBlock = (int)(cur % AesBlockSize);

                BuildCounterBlock(blockIndex, ctrBlock);
                aes.EncryptEcb(ctrBlock, scratchOut, PaddingMode.None);

                int count = Math.Min(AesBlockSize - offsetInBlock, rawRead - processed);
                for (int i = 0; i < count; i++)
                {
                    destination[processed + i] ^= scratchOut[offsetInBlock + i];
                }
                processed += count;
            }

            return rawRead;
        }

        private void BuildCounterBlock(long blockIndex, byte[] target)
        {
            Buffer.BlockCopy(ivOrNonce, 0, target, 0, AesBlockSize);
            ulong add = (ulong)blockIndex + counterStart;

            if (counterLittleEndian)
            {
                ulong carry = add;
                for (int i = 0; i < AesBlockSize && carry != 0; i++)
                {
                    ulong sum = target[i] + (carry & 0xFFUL);
                    target[i] = (byte)sum;
                    carry = (carry >> 8) + (sum >> 8);
                }
            }
            else
            {
                ulong carry = add;
                for (int i = AesBlockSize - 1; i >= 0 && carry != 0; i--)
                {
                    ulong sum = target[i] + (carry & 0xFFUL);
                    target[i] = (byte)sum;
                    carry = (carry >> 8) + (sum >> 8);
                }
            }
        }

        private int ReadChaCha20Range(long logicalOffset, Span<byte> destination)
        {
            int rawRead = ReadRawExact(logicalOffset, destination);
            if (rawRead <= 0)
            {
                return 0;
            }

            long cipherOffset = logicalOffset - cipherRangeStart;
            int processed = 0;
            while (processed < rawRead)
            {
                long cur = cipherOffset + processed;
                uint blockCounter = unchecked(counterStart + (uint)(cur / ChaChaBlockSize));
                int offsetInBlock = (int)(cur % ChaChaBlockSize);

                GenerateChaCha20Block(key, ivOrNonce, blockCounter, chaChaBlock);

                int count = Math.Min(ChaChaBlockSize - offsetInBlock, rawRead - processed);
                for (int i = 0; i < count; i++)
                {
                    destination[processed + i] ^= chaChaBlock[offsetInBlock + i];
                }
                processed += count;
            }

            return rawRead;
        }

        /// <summary>
        /// Standard RFC 7539 / RFC 8439 ChaCha20 64-byte keystream block generator.
        /// </summary>
        internal static void GenerateChaCha20Block(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> nonce12, uint counter, Span<byte> output64)
        {
            Span<uint> state = stackalloc uint[16];
            Span<uint> working = stackalloc uint[16];

            // "expand 32-byte k"
            state[0] = 0x61707865u;
            state[1] = 0x3320646Eu;
            state[2] = 0x79622D32u;
            state[3] = 0x6B206574u;

            for (int i = 0; i < 8; i++)
            {
                state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key32.Slice(i * 4, 4));
            }

            state[12] = counter;
            state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12.Slice(0, 4));
            state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12.Slice(4, 4));
            state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12.Slice(8, 4));

            state.CopyTo(working);

            for (int round = 0; round < 10; round++)
            {
                // Column rounds
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);

                // Diagonal rounds
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }

            for (int i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output64.Slice(i * 4, 4), unchecked(working[i] + state[i]));
            }
        }

        private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
        {
            x[a] = unchecked(x[a] + x[b]); x[d] = RotateLeft(x[d] ^ x[a], 16);
            x[c] = unchecked(x[c] + x[d]); x[b] = RotateLeft(x[b] ^ x[c], 12);
            x[a] = unchecked(x[a] + x[b]); x[d] = RotateLeft(x[d] ^ x[a], 8);
            x[c] = unchecked(x[c] + x[d]); x[b] = RotateLeft(x[b] ^ x[c], 7);
        }

        private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => logicalLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPosition < 0 || newPosition > logicalLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            position = newPosition;
            return position;
        }

        public CipherTransformStream Clone(Stream clonedBaseStream)
            => new CipherTransformStream(
                clonedBaseStream,
                algorithm,
                key,
                ivOrNonce,
                headerSkipBytes,
                rangeStart,
                rangeLength,
                pkcs7Padding,
                counterStart,
                counterLittleEndian);

        public override void Flush() => BaseStream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                aes?.Dispose();
                if (!LeaveBaseStreamOpen)
                {
                    BaseStream.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
