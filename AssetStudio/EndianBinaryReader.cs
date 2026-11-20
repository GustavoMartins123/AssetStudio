using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace AssetStudio
{
    public class EndianBinaryReader : BinaryReader
    {
        private readonly byte[] buffer;

        public EndianType Endian;

        public EndianBinaryReader(Stream stream, EndianType endian = EndianType.BigEndian) : base(stream)
        {
            Endian = endian;
            buffer = new byte[8];
        }

        public long Position
        {
            get => BaseStream.Position;
            set => BaseStream.Position = value;
        }

        /// <summary>
        /// Reads exactly <paramref name="destination"/> bytes from the underlying stream.
        /// </summary>
        public override void ReadExactly(Span<byte> destination) => ReadStreamExactly(BaseStream, destination);

        internal static void ReadStreamExactly(Stream stream, Span<byte> destination)
        {
            if (destination.IsEmpty)
            {
                return;
            }

            if (stream is MemoryStream memStream)
            {
                if (memStream.TryGetBuffer(out var segment))
                {
                    long pos = memStream.Position;
                    long remaining = memStream.Length - pos;
                    if (destination.Length > remaining)
                    {
                        throw new EndOfStreamException($"Unable to read {destination.Length} bytes; only {remaining} bytes remain.");
                    }

                    segment.AsSpan((int)pos, destination.Length).CopyTo(destination);
                    memStream.Position = pos + destination.Length;
                    return;
                }

                stream.ReadExactly(destination);
                return;
            }

            if (stream is FileStream)
            {
                stream.ReadExactly(destination);
                return;
            }

            // Custom stream types (e.g. SubStream, DecryptedStream, PooledReadOnlyMemoryStream)
            // only override Read(byte[], int, int). Chunk via <= 64 KB pooled scratch so BCL
            // Stream.Read(Span<byte>) never rents multi-MB LOH buffers from ArrayPool.
            const int MaxChunkSize = 65536;
            int poolSize = Math.Min(destination.Length, MaxChunkSize);
            byte[] scratch = ArrayPool<byte>.Shared.Rent(poolSize);
            try
            {
                int offset = 0;
                while (offset < destination.Length)
                {
                    int toRead = Math.Min(destination.Length - offset, MaxChunkSize);
                    int read = stream.Read(scratch, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unable to read {destination.Length} bytes; reached EOF after {offset} bytes.");
                    }

                    scratch.AsSpan(0, read).CopyTo(destination.Slice(offset));
                    offset += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public override short ReadInt16()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 2);
                return BinaryPrimitives.ReadInt16BigEndian(buffer);
            }
            return base.ReadInt16();
        }

        public override int ReadInt32()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 4);
                return BinaryPrimitives.ReadInt32BigEndian(buffer);
            }
            return base.ReadInt32();
        }

        public override long ReadInt64()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 8);
                return BinaryPrimitives.ReadInt64BigEndian(buffer);
            }
            return base.ReadInt64();
        }

        public override ushort ReadUInt16()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 2);
                return BinaryPrimitives.ReadUInt16BigEndian(buffer);
            }
            return base.ReadUInt16();
        }

        public override uint ReadUInt32()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 4);
                return BinaryPrimitives.ReadUInt32BigEndian(buffer);
            }
            return base.ReadUInt32();
        }

        public override ulong ReadUInt64()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 8);
                return BinaryPrimitives.ReadUInt64BigEndian(buffer);
            }
            return base.ReadUInt64();
        }

        public override float ReadSingle()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 4);
                return BinaryPrimitives.ReadSingleBigEndian(buffer);
            }
            return base.ReadSingle();
        }

        public override double ReadDouble()
        {
            if (Endian == EndianType.BigEndian)
            {
                BaseStream.ReadExactly(buffer, 0, 8);
                return BinaryPrimitives.ReadDoubleBigEndian(buffer);
            }
            return base.ReadDouble();
        }
    }
}
