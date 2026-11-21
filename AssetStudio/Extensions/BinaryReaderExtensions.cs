using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AssetStudio
{
    public static class BinaryReaderExtensions
    {
        private const int MaxArrayLength = 10000000;
        private const long MaxByteArrayLength = 536_870_912; // 512 MiB

        public static void AlignStream(this BinaryReader reader)
        {
            reader.AlignStream(4);
        }

        public static void AlignStream(this BinaryReader reader, int alignment)
        {
            var pos = reader.BaseStream.Position;
            var mod = pos % alignment;
            if (mod != 0)
            {
                reader.BaseStream.Position += alignment - mod;
            }
        }

        public static string ReadAlignedString(this BinaryReader reader)
        {
            var length = reader.ReadInt32();
            var stream = reader.BaseStream;
            if (length > 0 && length <= stream.Length - stream.Position)
            {
                if (reader is ObjectReader objReader)
                {
                    long remainingObj = objReader.byteSize - (objReader.Position - objReader.byteStart);
                    if (length > remainingObj)
                    {
                        throw new EndOfStreamException($"Trying to read aligned string of {length} bytes, but only {remainingObj} bytes remain in the object.");
                    }
                }

                string result;
                if (stream is MemoryStream memStream && memStream.TryGetBuffer(out var segment))
                {
                    long pos = memStream.Position;
                    result = Encoding.UTF8.GetString(segment.AsSpan((int)pos, length));
                    memStream.Position = pos + length;
                }
                else if (length <= 512)
                {
                    Span<byte> stackData = stackalloc byte[length];
                    EndianBinaryReader.ReadStreamExactly(stream, stackData);
                    result = Encoding.UTF8.GetString(stackData);
                }
                else
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        var span = rented.AsSpan(0, length);
                        EndianBinaryReader.ReadStreamExactly(stream, span);
                        result = Encoding.UTF8.GetString(span);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }

                reader.AlignStream(4);
                return result;
            }
            return "";
        }

        public static string ReadStringToNull(this BinaryReader reader, int maxLength = 32767)
        {
            if (maxLength <= 0)
            {
                return string.Empty;
            }

            var stream = reader.BaseStream;
            if (stream is MemoryStream memStream && memStream.TryGetBuffer(out var segment))
            {
                long pos = memStream.Position;
                long len = memStream.Length;
                if (pos >= len)
                {
                    return string.Empty;
                }

                int maxToScan = (int)Math.Min(len - pos, (long)maxLength);
                var span = segment.AsSpan((int)pos, maxToScan);
                int nullIndex = span.IndexOf((byte)0);
                if (nullIndex >= 0)
                {
                    memStream.Position = pos + nullIndex + 1;
                    return nullIndex == 0
                        ? string.Empty
                        : Encoding.UTF8.GetString(span.Slice(0, nullIndex));
                }

                memStream.Position = pos + maxToScan;
                return maxToScan == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(span);
            }

            if (stream.CanSeek)
            {
                return ReadStringToNullSeekable(stream, maxLength);
            }

            return ReadStringToNullUnseekable(reader, stream, maxLength);
        }

        private static string ReadStringToNullSeekable(Stream stream, int maxLength)
        {
            long pos = stream.Position;
            long len = stream.Length;
            if (pos >= len)
            {
                return string.Empty;
            }

            const int StackBufferSize = 256;
            Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
            byte[]? pooled = null;
            int count = 0;

            try
            {
                while (count < maxLength)
                {
                    long remainingStream = len - pos;
                    if (remainingStream <= 0)
                    {
                        break;
                    }

                    int remainingBudget = maxLength - count;
                    if (pooled == null)
                    {
                        int toRead = (int)Math.Min(Math.Min((long)StackBufferSize, remainingBudget), remainingStream);
                        int read = stream.Read(stackBuffer.Slice(0, toRead));
                        if (read <= 0)
                        {
                            break;
                        }

                        var readSlice = stackBuffer.Slice(0, read);
                        int nullIdx = readSlice.IndexOf((byte)0);
                        if (nullIdx >= 0)
                        {
                            if (read > nullIdx + 1)
                            {
                                stream.Position = pos + nullIdx + 1;
                            }
                            count = nullIdx;
                            break;
                        }

                        pos += read;
                        count = read;
                        if (count >= maxLength || pos >= len)
                        {
                            break;
                        }

                        int initialSize = Math.Min(StackBufferSize * 2, maxLength);
                        pooled = ArrayPool<byte>.Shared.Rent(initialSize);
                        readSlice.CopyTo(pooled);
                    }
                    else
                    {
                        if (count >= pooled.Length)
                        {
                            int nextSize = pooled.Length > maxLength / 2 ? maxLength : pooled.Length * 2;
                            var nextBuffer = ArrayPool<byte>.Shared.Rent(nextSize);
                            Buffer.BlockCopy(pooled, 0, nextBuffer, 0, count);
                            ArrayPool<byte>.Shared.Return(pooled);
                            pooled = nextBuffer;
                        }

                        int chunkCap = pooled.Length - count;
                        int toRead = (int)Math.Min(Math.Min((long)chunkCap, remainingBudget), remainingStream);
                        int read = stream.Read(pooled, count, toRead);
                        if (read <= 0)
                        {
                            break;
                        }

                        var readSlice = pooled.AsSpan(count, read);
                        int nullIdx = readSlice.IndexOf((byte)0);
                        if (nullIdx >= 0)
                        {
                            if (read > nullIdx + 1)
                            {
                                stream.Position = pos + nullIdx + 1;
                            }
                            count += nullIdx;
                            break;
                        }

                        pos += read;
                        count += read;
                    }
                }

                if (count == 0)
                {
                    return string.Empty;
                }

                return pooled != null
                    ? Encoding.UTF8.GetString(pooled, 0, count)
                    : Encoding.UTF8.GetString(stackBuffer.Slice(0, count));
            }
            finally
            {
                if (pooled != null)
                {
                    ArrayPool<byte>.Shared.Return(pooled);
                }
            }
        }

        private static string ReadStringToNullUnseekable(BinaryReader reader, Stream stream, int maxLength)
        {
            const int StackBufferSize = 256;
            Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
            byte[]? pooled = null;
            int count = 0;

            try
            {
                while (count < maxLength)
                {
                    int b = stream.ReadByte();
                    if (b <= 0)
                    {
                        break;
                    }

                    if (pooled == null)
                    {
                        if (count < StackBufferSize)
                        {
                            stackBuffer[count++] = (byte)b;
                            continue;
                        }

                        int initialSize = Math.Min(StackBufferSize * 2, maxLength);
                        pooled = ArrayPool<byte>.Shared.Rent(initialSize);
                        stackBuffer.CopyTo(pooled);
                    }
                    else if (count == pooled.Length)
                    {
                        int nextSize = pooled.Length > maxLength / 2 ? maxLength : pooled.Length * 2;
                        var nextBuffer = ArrayPool<byte>.Shared.Rent(nextSize);
                        Buffer.BlockCopy(pooled, 0, nextBuffer, 0, count);
                        ArrayPool<byte>.Shared.Return(pooled);
                        pooled = nextBuffer;
                    }

                    pooled[count++] = (byte)b;
                }

                if (count == 0)
                {
                    return string.Empty;
                }

                return pooled != null
                    ? Encoding.UTF8.GetString(pooled, 0, count)
                    : Encoding.UTF8.GetString(stackBuffer.Slice(0, count));
            }
            finally
            {
                if (pooled != null)
                {
                    ArrayPool<byte>.Shared.Return(pooled);
                }
            }
        }

        public static Quaternion ReadQuaternion(this BinaryReader reader)
        {
            return ReadBlittableStruct<Quaternion>(reader, 4);
        }

        public static Vector2 ReadVector2(this BinaryReader reader)
        {
            return ReadBlittableStruct<Vector2>(reader, 4);
        }

        public static Vector3 ReadVector3(this BinaryReader reader)
        {
            return ReadBlittableStruct<Vector3>(reader, 4);
        }

        public static Vector4 ReadVector4(this BinaryReader reader)
        {
            return ReadBlittableStruct<Vector4>(reader, 4);
        }

        public static Color ReadColor4(this BinaryReader reader)
        {
            return ReadBlittableStruct<Color>(reader, 4);
        }

        public static Matrix4x4 ReadMatrix(this BinaryReader reader)
        {
            CheckBlittableArrayLength(reader, 1, Unsafe.SizeOf<Matrix4x4>());
            return ReadBlittableStruct<Matrix4x4>(reader, 4);
        }

        public static void CheckArrayLength(this BinaryReader reader, int length)
        {
            CheckBlittableArrayLength(reader, length, 1);
        }

        private static void CheckBlittableArrayLength(BinaryReader reader, int length, int elementSize)
        {
            if (length < 0)
            {
                throw new OverflowException($"Invalid array length: {length}");
            }

            long byteCount = (long)length * elementSize;
            if (byteCount > MaxByteArrayLength)
            {
                throw new OverflowException($"Invalid array length: {length}");
            }

            if (length > MaxArrayLength && (!reader.BaseStream.CanSeek || (reader.BaseStream.Length - reader.BaseStream.Position) < byteCount))
            {
                throw new OverflowException($"Invalid array length: {length}");
            }

            long remainingStream = reader.BaseStream.Length - reader.BaseStream.Position;
            if (byteCount > remainingStream)
            {
                throw new EndOfStreamException($"Array length {length} ({byteCount} bytes) exceeds remaining stream length {remainingStream}");
            }

            if (reader is ObjectReader objReader)
            {
                long remainingObj = objReader.byteSize - (objReader.Position - objReader.byteStart);
                if (byteCount > remainingObj)
                {
                    throw new EndOfStreamException($"Array length {length} ({byteCount} bytes) exceeds remaining object size {remainingObj}");
                }
            }
        }

        public static T[] ReadArray<T>(this BinaryReader reader, Func<T> del, int length)
        {
            reader.CheckArrayLength(length);
            if (length == 0)
            {
                return Array.Empty<T>();
            }

            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = del();
            }
            return array;
        }

        private static bool NeedsEndianSwap(BinaryReader reader)
        {
            var streamEndian = reader is EndianBinaryReader endianReader
                ? endianReader.Endian
                : EndianType.LittleEndian;
            return BitConverter.IsLittleEndian
                ? streamEndian == EndianType.BigEndian
                : streamEndian == EndianType.LittleEndian;
        }

        private static void ReverseEndiannessInPlace(Span<byte> byteSpan, int swapUnitBytes)
        {
            switch (swapUnitBytes)
            {
                case 2:
                    var span16 = MemoryMarshal.Cast<byte, ushort>(byteSpan);
                    BinaryPrimitives.ReverseEndianness(span16, span16);
                    break;
                case 4:
                    var span32 = MemoryMarshal.Cast<byte, uint>(byteSpan);
                    BinaryPrimitives.ReverseEndianness(span32, span32);
                    break;
                case 8:
                    var span64 = MemoryMarshal.Cast<byte, ulong>(byteSpan);
                    BinaryPrimitives.ReverseEndianness(span64, span64);
                    break;
            }
        }

        private static T ReadBlittableStruct<T>(BinaryReader reader, int swapUnitBytes) where T : unmanaged
        {
            Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
            EndianBinaryReader.ReadStreamExactly(reader.BaseStream, bytes);
            if (swapUnitBytes > 1 && NeedsEndianSwap(reader))
            {
                ReverseEndiannessInPlace(bytes, swapUnitBytes);
            }
            return MemoryMarshal.Read<T>(bytes);
        }

        private static T[] ReadBlittableArray<T>(BinaryReader reader, int length, int swapUnitBytes) where T : unmanaged
        {
            CheckBlittableArrayLength(reader, length, Unsafe.SizeOf<T>());
            if (length == 0)
            {
                return Array.Empty<T>();
            }

            var array = GC.AllocateUninitializedArray<T>(length);
            var byteSpan = MemoryMarshal.AsBytes(array.AsSpan());
            EndianBinaryReader.ReadStreamExactly(reader.BaseStream, byteSpan);
            if (swapUnitBytes > 1 && NeedsEndianSwap(reader))
            {
                ReverseEndiannessInPlace(byteSpan, swapUnitBytes);
            }
            return array;
        }

        public static byte[] ReadUInt8Array(this BinaryReader reader)
        {
            return reader.ReadUInt8Array(reader.ReadInt32());
        }

        public static byte[] ReadUInt8Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<byte>(reader, length, 1);
        }

        public static bool[] ReadBooleanArray(this BinaryReader reader)
        {
            return reader.ReadBooleanArray(reader.ReadInt32());
        }

        public static bool[] ReadBooleanArray(this BinaryReader reader, int length)
        {
            var array = ReadBlittableArray<bool>(reader, length, 1);
            var bytes = MemoryMarshal.AsBytes(array.AsSpan());
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] > 1)
                {
                    bytes[i] = 1;
                }
            }
            return array;
        }

        public static short[] ReadInt16Array(this BinaryReader reader)
        {
            return reader.ReadInt16Array(reader.ReadInt32());
        }

        public static short[] ReadInt16Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<short>(reader, length, 2);
        }

        public static ushort[] ReadUInt16Array(this BinaryReader reader)
        {
            return reader.ReadUInt16Array(reader.ReadInt32());
        }

        public static ushort[] ReadUInt16Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<ushort>(reader, length, 2);
        }

        public static int[] ReadInt32Array(this BinaryReader reader)
        {
            return reader.ReadInt32Array(reader.ReadInt32());
        }

        public static int[] ReadInt32Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<int>(reader, length, 4);
        }

        public static uint[] ReadUInt32Array(this BinaryReader reader)
        {
            return reader.ReadUInt32Array(reader.ReadInt32());
        }

        public static uint[][] ReadUInt32ArrayArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadUInt32Array, reader.ReadInt32());
        }

        public static uint[] ReadUInt32Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<uint>(reader, length, 4);
        }

        public static long[] ReadInt64Array(this BinaryReader reader)
        {
            return reader.ReadInt64Array(reader.ReadInt32());
        }

        public static long[] ReadInt64Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<long>(reader, length, 8);
        }

        public static ulong[] ReadUInt64Array(this BinaryReader reader)
        {
            return reader.ReadUInt64Array(reader.ReadInt32());
        }

        public static ulong[] ReadUInt64Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<ulong>(reader, length, 8);
        }

        public static float[] ReadSingleArray(this BinaryReader reader)
        {
            return reader.ReadSingleArray(reader.ReadInt32());
        }

        public static float[] ReadSingleArray(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<float>(reader, length, 4);
        }

        public static double[] ReadDoubleArray(this BinaryReader reader)
        {
            return reader.ReadDoubleArray(reader.ReadInt32());
        }

        public static double[] ReadDoubleArray(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<double>(reader, length, 8);
        }

        public static string[] ReadStringArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadAlignedString, reader.ReadInt32());
        }

        public static Vector2[] ReadVector2Array(this BinaryReader reader)
        {
            return reader.ReadVector2Array(reader.ReadInt32());
        }

        public static Vector2[] ReadVector2Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<Vector2>(reader, length, 4);
        }

        public static Vector3[] ReadVector3Array(this BinaryReader reader)
        {
            return reader.ReadVector3Array(reader.ReadInt32());
        }

        public static Vector3[] ReadVector3Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<Vector3>(reader, length, 4);
        }

        public static Vector4[] ReadVector4Array(this BinaryReader reader)
        {
            return reader.ReadVector4Array(reader.ReadInt32());
        }

        public static Vector4[] ReadVector4Array(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<Vector4>(reader, length, 4);
        }

        public static Quaternion[] ReadQuaternionArray(this BinaryReader reader)
        {
            return reader.ReadQuaternionArray(reader.ReadInt32());
        }

        public static Quaternion[] ReadQuaternionArray(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<Quaternion>(reader, length, 4);
        }

        public static Matrix4x4[] ReadMatrixArray(this BinaryReader reader)
        {
            return reader.ReadMatrixArray(reader.ReadInt32());
        }

        public static Matrix4x4[] ReadMatrixArray(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<Matrix4x4>(reader, length, 4);
        }

        public static Color[] ReadColorArray(this BinaryReader reader)
        {
            return reader.ReadColorArray(reader.ReadInt32());
        }

        public static Color[] ReadColorArray(this BinaryReader reader, int length)
        {
            return ReadBlittableArray<Color>(reader, length, 4);
        }
    }
}
