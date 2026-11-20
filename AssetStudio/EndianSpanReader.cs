using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Zero-copy, stack-allocated reader over a <see cref="ReadOnlySpan{T}"/> of bytes with configurable endianness.
    /// </summary>
    public ref struct EndianSpanReader
    {
        private readonly ReadOnlySpan<byte> buffer;
        private int position;

        public EndianType Endian;

        public EndianSpanReader(ReadOnlySpan<byte> data, EndianType endian = EndianType.BigEndian)
        {
            buffer = data;
            position = 0;
            Endian = endian;
        }

        public readonly int Length => buffer.Length;

        public readonly int Remaining => buffer.Length - position;

        public int Position
        {
            readonly get => position;
            set
            {
                if ((uint)value > (uint)buffer.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), $"Position {value} is outside span length {buffer.Length}.");
                }
                position = value;
            }
        }

        private ReadOnlySpan<byte> ReadSlice(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");
            }
            if (count > buffer.Length - position)
            {
                throw new EndOfStreamException($"Cannot read {count} bytes at position {position} (length {buffer.Length}).");
            }
            var slice = buffer.Slice(position, count);
            position += count;
            return slice;
        }

        public byte ReadByte()
        {
            if (position >= buffer.Length)
            {
                throw new EndOfStreamException($"Cannot read 1 byte at position {position} (length {buffer.Length}).");
            }
            return buffer[position++];
        }

        public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

        public bool ReadBoolean() => ReadByte() != 0;

        public short ReadInt16()
        {
            var slice = ReadSlice(2);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadInt16BigEndian(slice)
                : BinaryPrimitives.ReadInt16LittleEndian(slice);
        }

        public ushort ReadUInt16()
        {
            var slice = ReadSlice(2);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(slice)
                : BinaryPrimitives.ReadUInt16LittleEndian(slice);
        }

        public int ReadInt32()
        {
            var slice = ReadSlice(4);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(slice)
                : BinaryPrimitives.ReadInt32LittleEndian(slice);
        }

        public uint ReadUInt32()
        {
            var slice = ReadSlice(4);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(slice)
                : BinaryPrimitives.ReadUInt32LittleEndian(slice);
        }

        public long ReadInt64()
        {
            var slice = ReadSlice(8);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(slice)
                : BinaryPrimitives.ReadInt64LittleEndian(slice);
        }

        public ulong ReadUInt64()
        {
            var slice = ReadSlice(8);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(slice)
                : BinaryPrimitives.ReadUInt64LittleEndian(slice);
        }

        public float ReadSingle()
        {
            var slice = ReadSlice(4);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(slice)
                : BinaryPrimitives.ReadSingleLittleEndian(slice);
        }

        public double ReadDouble()
        {
            var slice = ReadSlice(8);
            return Endian == EndianType.BigEndian
                ? BinaryPrimitives.ReadDoubleBigEndian(slice)
                : BinaryPrimitives.ReadDoubleLittleEndian(slice);
        }

        public ReadOnlySpan<byte> ReadBytes(int count) => ReadSlice(count);

        public string ReadAlignedString()
        {
            int length = ReadInt32();
            if (length > 0 && length <= Remaining)
            {
                var slice = ReadSlice(length);
                string result = Encoding.UTF8.GetString(slice);
                AlignStream(4);
                return result;
            }
            return string.Empty;
        }

        public string ReadStringToNull(int maxLength = 32767)
        {
            if (maxLength <= 0 || position >= buffer.Length)
            {
                return string.Empty;
            }

            int maxBytes = Math.Min(buffer.Length - position, maxLength);
            var slice = buffer.Slice(position, maxBytes);
            int nullIndex = slice.IndexOf((byte)0);
            if (nullIndex >= 0)
            {
                position += nullIndex + 1;
                return nullIndex == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(slice.Slice(0, nullIndex));
            }

            position += maxBytes;
            return maxBytes == 0
                ? string.Empty
                : Encoding.UTF8.GetString(slice);
        }

        /// <summary>
        /// Advances <see cref="Position"/> to the next multiple of <paramref name="alignment"/>,
        /// clamping at <see cref="Length"/> if trailing padding bytes are absent.
        /// </summary>
        public void AlignStream(int alignment = 4)
        {
            if (alignment <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be positive.");
            }
            if (alignment == 1)
            {
                return;
            }
            int mod = position % alignment;
            if (mod != 0)
            {
                int next = position + (alignment - mod);
                position = next <= buffer.Length ? next : buffer.Length;
            }
        }
    }
}
