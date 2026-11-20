using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public static class BinaryReaderExtensions
    {
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
            if (length > 0 && length <= reader.BaseStream.Length - reader.BaseStream.Position)
            {
                var stringData = reader.ReadBytes(length);
                var result = Encoding.UTF8.GetString(stringData);
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

            const int StackBufferSize = 256;
            Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
            byte[] pooled = null;
            int count = 0;

            try
            {
                var stream = reader.BaseStream;
                while (stream.Position < stream.Length && count < maxLength)
                {
                    var b = reader.ReadByte();
                    if (b == 0)
                    {
                        break;
                    }

                    if (pooled == null)
                    {
                        if (count < StackBufferSize)
                        {
                            stackBuffer[count++] = b;
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

                    pooled[count++] = b;
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
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Vector2 ReadVector2(this BinaryReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        public static Vector3 ReadVector3(this BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Vector4 ReadVector4(this BinaryReader reader)
        {
            return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Color ReadColor4(this BinaryReader reader)
        {
            return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Matrix4x4 ReadMatrix(this BinaryReader reader)
        {
            return new Matrix4x4(reader.ReadSingleArray(16));
        }

        public static void CheckArrayLength(this BinaryReader reader, int length)
        {
            if (length < 0 || length > 10000000)
            {
                throw new OverflowException($"Invalid array length: {length}");
            }

            long remainingStream = reader.BaseStream.Length - reader.BaseStream.Position;
            if (length > remainingStream)
                throw new EndOfStreamException($"Array length {length} exceeds remaining stream length {remainingStream}");

            if (reader is ObjectReader objReader)
            {
                long remainingObj = objReader.byteSize - (objReader.Position - objReader.byteStart);
                if (length > remainingObj)
                    throw new EndOfStreamException($"Array length {length} exceeds remaining object size {remainingObj}");
            }
        }

        public static T[] ReadArray<T>(this BinaryReader reader, Func<T> del, int length)
        {
            reader.CheckArrayLength(length);
            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = del();
            }
            return array;
        }


        public static byte[] ReadUInt8Array(this BinaryReader reader)
        {
            int count = reader.ReadInt32();
            reader.CheckArrayLength(count);
            return reader.ReadBytes(count);
        }

        public static bool[] ReadBooleanArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadBoolean, reader.ReadInt32());
        }

        public static ushort[] ReadUInt16Array(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadUInt16, reader.ReadInt32());
        }

        public static int[] ReadInt32Array(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadInt32, reader.ReadInt32());
        }

        public static int[] ReadInt32Array(this BinaryReader reader, int length)
        {
            return reader.ReadArray(reader.ReadInt32, length);
        }

        public static uint[] ReadUInt32Array(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadUInt32, reader.ReadInt32());
        }

        public static uint[][] ReadUInt32ArrayArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadUInt32Array, reader.ReadInt32());
        }

        public static uint[] ReadUInt32Array(this BinaryReader reader, int length)
        {
            return reader.ReadArray(reader.ReadUInt32, length);
        }

        public static float[] ReadSingleArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadSingle, reader.ReadInt32());
        }

        public static float[] ReadSingleArray(this BinaryReader reader, int length)
        {
            return reader.ReadArray(reader.ReadSingle, length);
        }

        public static string[] ReadStringArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadAlignedString, reader.ReadInt32());
        }

        public static Vector2[] ReadVector2Array(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadVector2, reader.ReadInt32());
        }

        public static Vector4[] ReadVector4Array(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadVector4, reader.ReadInt32());
        }

        public static Matrix4x4[] ReadMatrixArray(this BinaryReader reader)
        {
            return reader.ReadArray(reader.ReadMatrix, reader.ReadInt32());
        }
    }
}
