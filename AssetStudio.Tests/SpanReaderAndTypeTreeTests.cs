using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace AssetStudio.Tests
{
    public class SpanReaderAndTypeTreeTests
    {
        // =========================================================================
        // 1. Bulk Blittable Array Reads & Bounds Checks
        // =========================================================================

        [Theory]
        [InlineData(EndianType.LittleEndian)]
        [InlineData(EndianType.BigEndian)]
        public void BinaryReaderExtensions_BulkArrays_MatchExactScalarValues(EndianType endian)
        {
            var floatExpected = new[] { 0f, -0f, 1.5f, -42.625f, float.MaxValue, float.MinValue, float.Epsilon, 3.1415927f };
            var intExpected = new[] { int.MinValue, -123456, -1, 0, 1, 987654, int.MaxValue };
            var uintExpected = new[] { 0u, 1u, 0x12345678u, 0xDEADBEEFu, uint.MaxValue };
            var ushortExpected = new ushort[] { 0, 1, 0x1234, 0xABCD, ushort.MaxValue };
            var shortExpected = new short[] { short.MinValue, -1234, -1, 0, 1, 4321, short.MaxValue };
            var longExpected = new[] { long.MinValue, -0x0102030405060708L, -1L, 0L, 1L, 0x1122334455667788L, long.MaxValue };
            var ulongExpected = new[] { 0UL, 1UL, 0xFEDCBA9876543210UL, ulong.MaxValue };
            var doubleExpected = new[] { 0.0, -123456.789012345, double.MaxValue, double.MinValue };
            var boolExpected = new[] { false, true, false, true, true };
            var byteExpected = new byte[] { 0, 1, 127, 128, 255 };
            var vec2Expected = new[] { new Vector2(1.25f, -2.5f), new Vector2(0f, 99.75f), new Vector2(-1000f, 0.001f) };
            var vec3Expected = new[] { new Vector3(1f, 2f, 3f), new Vector3(-4.5f, 5.5f, -6.5f) };
            var vec4Expected = new[] { new Vector4(1f, 2f, 3f, 4f), new Vector4(-1.5f, 0.25f, 8.75f, -16.125f) };
            var quatExpected = new[] { new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), new Quaternion(-0.5f, 0.5f, -0.5f, 0.5f) };
            var matExpected = new[]
            {
                new Matrix4x4(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f }),
                new Matrix4x4(new[] { -1f, 0.5f, 2.25f, -3.75f, 4.5f, -5.5f, 6.5f, -7.5f, 8.25f, -9.25f, 10.5f, -11.5f, 12.125f, -13.125f, 14.5f, 1f })
            };
            var colorExpected = new[] { new Color(0.1f, 0.2f, 0.3f, 1.0f), new Color(1f, 0f, 0.5f, 0.25f) };

            using var ms = new MemoryStream();
            WriteCountAndPrimitives(ms, endian, floatExpected, WriteSingle);
            WriteCountAndPrimitives(ms, endian, intExpected, WriteInt32);
            WriteCountAndPrimitives(ms, endian, uintExpected, WriteUInt32);
            WriteCountAndPrimitives(ms, endian, ushortExpected, WriteUInt16);
            WriteCountAndPrimitives(ms, endian, shortExpected, WriteInt16);
            WriteCountAndPrimitives(ms, endian, longExpected, WriteInt64);
            WriteCountAndPrimitives(ms, endian, ulongExpected, WriteUInt64);
            WriteCountAndPrimitives(ms, endian, doubleExpected, WriteDouble);
            WriteInt32(ms, endian, boolExpected.Length);
            ms.Write(new byte[] { 0, 1, 0, 2, 255 }); // non-0/1 truthy bytes must normalize cleanly
            WriteInt32(ms, endian, byteExpected.Length);
            ms.Write(byteExpected);
            WriteCountAndPrimitives(ms, endian, vec2Expected, (s, e, v) => { WriteSingle(s, e, v.X); WriteSingle(s, e, v.Y); });
            WriteCountAndPrimitives(ms, endian, vec3Expected, (s, e, v) => { WriteSingle(s, e, v.X); WriteSingle(s, e, v.Y); WriteSingle(s, e, v.Z); });
            WriteCountAndPrimitives(ms, endian, vec4Expected, (s, e, v) => { WriteSingle(s, e, v.X); WriteSingle(s, e, v.Y); WriteSingle(s, e, v.Z); WriteSingle(s, e, v.W); });
            WriteCountAndPrimitives(ms, endian, quatExpected, (s, e, q) => { WriteSingle(s, e, q.X); WriteSingle(s, e, q.Y); WriteSingle(s, e, q.Z); WriteSingle(s, e, q.W); });
            WriteCountAndPrimitives(ms, endian, matExpected, (s, e, m) =>
            {
                for (int k = 0; k < 16; k++) WriteSingle(s, e, m[k]);
            });
            WriteCountAndPrimitives(ms, endian, colorExpected, (s, e, c) => { WriteSingle(s, e, c.R); WriteSingle(s, e, c.G); WriteSingle(s, e, c.B); WriteSingle(s, e, c.A); });

            // Empty arrays
            WriteInt32(ms, endian, 0);
            WriteInt32(ms, endian, 0);

            ms.Position = 0;
            using var reader = new EndianBinaryReader(ms, endian);

            Assert.Equal(floatExpected, reader.ReadSingleArray());
            Assert.Equal(intExpected, reader.ReadInt32Array());
            Assert.Equal(uintExpected, reader.ReadUInt32Array());
            Assert.Equal(ushortExpected, reader.ReadUInt16Array());
            Assert.Equal(shortExpected, reader.ReadInt16Array());
            Assert.Equal(longExpected, reader.ReadInt64Array());
            Assert.Equal(ulongExpected, reader.ReadUInt64Array());
            Assert.Equal(doubleExpected, reader.ReadDoubleArray());
            Assert.Equal(boolExpected, reader.ReadBooleanArray());
            Assert.Equal(byteExpected, reader.ReadUInt8Array());
            Assert.Equal(vec2Expected, reader.ReadVector2Array());
            Assert.Equal(vec3Expected, reader.ReadVector3Array());
            Assert.Equal(vec4Expected, reader.ReadVector4Array());
            Assert.Equal(quatExpected, reader.ReadQuaternionArray());
            Assert.Equal(matExpected, reader.ReadMatrixArray());
            Assert.Equal(colorExpected, reader.ReadColorArray());
            Assert.Empty(reader.ReadSingleArray());
            Assert.Empty(reader.ReadMatrixArray());
            Assert.Equal(ms.Length, reader.Position);
        }

        [Theory]
        [InlineData(EndianType.LittleEndian)]
        [InlineData(EndianType.BigEndian)]
        public void BinaryReaderExtensions_SingleStructs_MatchExactValues(EndianType endian)
        {
            var v2 = new Vector2(1.5f, -2.25f);
            var v3 = new Vector3(-3.5f, 4.125f, 5.75f);
            var v4 = new Vector4(1f, -2f, 3f, -4f);
            var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var c = new Color(0.25f, 0.5f, 0.75f, 1.0f);
            var m = new Matrix4x4(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f });

            using var ms = new MemoryStream();
            WriteSingle(ms, endian, v2.X); WriteSingle(ms, endian, v2.Y);
            WriteSingle(ms, endian, v3.X); WriteSingle(ms, endian, v3.Y); WriteSingle(ms, endian, v3.Z);
            WriteSingle(ms, endian, v4.X); WriteSingle(ms, endian, v4.Y); WriteSingle(ms, endian, v4.Z); WriteSingle(ms, endian, v4.W);
            WriteSingle(ms, endian, q.X); WriteSingle(ms, endian, q.Y); WriteSingle(ms, endian, q.Z); WriteSingle(ms, endian, q.W);
            WriteSingle(ms, endian, c.R); WriteSingle(ms, endian, c.G); WriteSingle(ms, endian, c.B); WriteSingle(ms, endian, c.A);
            for (int i = 0; i < 16; i++) WriteSingle(ms, endian, m[i]);

            ms.Position = 0;
            using var reader = new EndianBinaryReader(ms, endian);
            Assert.Equal(v2, reader.ReadVector2());
            Assert.Equal(v3, reader.ReadVector3());
            Assert.Equal(v4, reader.ReadVector4());
            Assert.Equal(q, reader.ReadQuaternion());
            Assert.Equal(c, reader.ReadColor4());
            Assert.Equal(m, reader.ReadMatrix());
            Assert.Equal(ms.Length, reader.Position);
        }

        [Fact]
        public void BinaryReaderExtensions_BulkArrays_WorkThroughNonMemoryStreamChunking()
        {
            const int count = 25000; // > 64 KB chunk boundary for float/int arrays
            var expected = new float[count];
            for (int i = 0; i < count; i++)
            {
                expected[i] = (i - 1000) * 0.125f;
            }

            var raw = new byte[4 + count * 4];
            BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(0, 4), count);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(4 + i * 4, 4), expected[i]);
            }

            // Custom stream that only exposes Read(byte[], int, int) with small 4099-byte chunks
            using var stream = new ChunkedReadOnlyStream(raw, maxReadChunk: 4099);
            using var reader = new EndianBinaryReader(stream, EndianType.BigEndian);
            var actual = reader.ReadSingleArray();

            Assert.Equal(expected, actual);
            Assert.Equal(raw.Length, stream.Position);
        }

        [Fact]
        public void BinaryReaderExtensions_ReadStringToNull_WorksOnChunkedAndUnseekableStreams()
        {
            var longText = new string('A', 400) + "_café_" + new string('B', 300);
            var payload = Encoding.UTF8.GetBytes("alpha\0" + longText + "\0tail");

            // Seekable custom stream (not MemoryStream)
            using (var seekable = new ChunkedReadOnlyStream(payload, maxReadChunk: 64, canSeek: true))
            using (var br = new BinaryReader(seekable))
            {
                Assert.Equal("alpha", br.ReadStringToNull());
                Assert.Equal(longText, br.ReadStringToNull());
                Assert.Equal("tail", br.ReadStringToNull());
                Assert.Equal(string.Empty, br.ReadStringToNull());
            }

            // Unseekable stream fallback
            using (var unseekable = new ChunkedReadOnlyStream(payload, maxReadChunk: 64, canSeek: false))
            using (var br = new BinaryReader(unseekable))
            {
                Assert.Equal("alpha", br.ReadStringToNull());
                Assert.Equal(longText, br.ReadStringToNull());
                Assert.Equal("tail", br.ReadStringToNull());
                Assert.Equal(string.Empty, br.ReadStringToNull());
            }

            // Non-exposable MemoryStream (TryGetBuffer == false)
            using (var hiddenMem = new MemoryStream(payload, 0, payload.Length, writable: false, publiclyVisible: false))
            using (var br = new BinaryReader(hiddenMem))
            {
                Assert.Equal("alpha", br.ReadStringToNull());
                Assert.Equal(longText, br.ReadStringToNull());
                Assert.Equal("tail", br.ReadStringToNull());
            }
        }

        [Fact]
        public void BinaryReaderExtensions_BoundsChecks_RejectNegativeOversizedAndTruncatedArrays()
        {
            // 1) Negative count -> OverflowException
            var negBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(negBytes, -1);
            using (var ms = new MemoryStream(negBytes))
            using (var reader = new EndianBinaryReader(ms, EndianType.LittleEndian))
            {
                Assert.Throws<OverflowException>(() => reader.ReadSingleArray());
            }

            // 2) Count > 10,000,000 -> OverflowException
            var hugeBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(hugeBytes, 10_000_001);
            using (var ms = new MemoryStream(hugeBytes))
            using (var reader = new EndianBinaryReader(ms, EndianType.LittleEndian))
            {
                Assert.Throws<OverflowException>(() => reader.ReadInt32Array());
            }

            // 3) Truncated byte count (count * sizeof(T) > remainingStream even when count <= remainingStream)
            var truncMatrixBytes = new byte[4 + 32]; // 32 bytes left, Matrix4x4 needs 64
            BinaryPrimitives.WriteInt32LittleEndian(truncMatrixBytes.AsSpan(0, 4), 1);
            using (var ms = new MemoryStream(truncMatrixBytes))
            using (var reader = new EndianBinaryReader(ms, EndianType.LittleEndian))
            {
                Assert.Throws<EndOfStreamException>(() => reader.ReadMatrixArray());
            }

            // 4) ObjectReader byteSize boundary check even when underlying file has plenty of bytes
            var fileBytes = new byte[256];
            BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(0, 4), 4); // wants 16 bytes of floats
            var assetsFile = CreateSerializedFileWithData(fileBytes, EndianType.LittleEndian, out var objInfo, objectByteSize: 12);
            using var objReader = new ObjectReader(assetsFile.reader, assetsFile, objInfo);
            objReader.Reset();
            Assert.Throws<EndOfStreamException>(() => objReader.ReadSingleArray());
        }

        // =========================================================================
        // 2. EndianSpanReader Unit Tests + TypeTreeBlobRead Integration
        // =========================================================================

        [Theory]
        [InlineData(EndianType.LittleEndian)]
        [InlineData(EndianType.BigEndian)]
        public void EndianSpanReader_ReadsAllPrimitivesStringsAndAlignment(EndianType endian)
        {
            const short i16 = -0x1234;
            const ushort u16 = 0xABCD;
            const int i32 = -0x12345678;
            const uint u32 = 0x89ABCDEFu;
            const long i64 = -0x0102030405060708L;
            const ulong u64 = 0xFEDCBA9876543210UL;
            const float f32 = 3.1415927f;
            const double f64 = -987654.321098765;
            const string alignedText = "Olá_Unity_日本語";
            const string cString = "TypeTreeNodeName";

            using var ms = new MemoryStream();
            ms.WriteByte(0xFE);
            ms.WriteByte(0x01); // bool true
            WriteInt16(ms, endian, i16);
            WriteUInt16(ms, endian, u16);
            WriteInt32(ms, endian, i32);
            WriteUInt32(ms, endian, u32);
            WriteInt64(ms, endian, i64);
            WriteUInt64(ms, endian, u64);
            WriteSingle(ms, endian, f32);
            WriteDouble(ms, endian, f64);

            // AlignedString
            var utf8 = Encoding.UTF8.GetBytes(alignedText);
            WriteInt32(ms, endian, utf8.Length);
            ms.Write(utf8);
            while (ms.Position % 4 != 0) ms.WriteByte(0);

            // Raw bytes + manual AlignStream(8)
            ms.Write(new byte[] { 0x11, 0x22, 0x33 });
            while (ms.Position % 8 != 0) ms.WriteByte(0);

            // Null-terminated strings
            ms.Write(Encoding.UTF8.GetBytes(cString));
            ms.WriteByte(0);
            ms.Write(Encoding.UTF8.GetBytes("EOFToken"));

            byte[] data = ms.ToArray();
            var reader = new EndianSpanReader(data, endian);

            Assert.Equal(data.Length, reader.Length);
            Assert.Equal(data.Length, reader.Remaining);
            Assert.Equal(unchecked((sbyte)0xFE), reader.ReadSByte());
            Assert.True(reader.ReadBoolean());
            Assert.Equal(i16, reader.ReadInt16());
            Assert.Equal(u16, reader.ReadUInt16());
            Assert.Equal(i32, reader.ReadInt32());
            Assert.Equal(u32, reader.ReadUInt32());
            Assert.Equal(i64, reader.ReadInt64());
            Assert.Equal(u64, reader.ReadUInt64());
            Assert.Equal(f32, reader.ReadSingle());
            Assert.Equal(f64, reader.ReadDouble());
            Assert.Equal(alignedText, reader.ReadAlignedString());
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, reader.ReadBytes(3).ToArray());
            reader.AlignStream(8);
            Assert.Equal(0, reader.Position % 8);
            Assert.Equal(cString, reader.ReadStringToNull());
            Assert.Equal("EOFToken", reader.ReadStringToNull());
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(string.Empty, reader.ReadStringToNull());
        }

        [Fact]
        public void EndianSpanReader_ThrowsOnInvalidBoundsAndAlignment()
        {
            var buf = new byte[] { 1, 2, 3 };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var r = new EndianSpanReader(buf);
                r.Position = -1;
            });
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var r = new EndianSpanReader(buf);
                r.Position = 4;
            });
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var r = new EndianSpanReader(buf);
                r.ReadBytes(-1);
            });
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var r = new EndianSpanReader(buf);
                r.AlignStream(0);
            });
            Assert.Throws<EndOfStreamException>(() =>
            {
                var r = new EndianSpanReader(buf);
                r.ReadInt32();
            });
            Assert.Throws<EndOfStreamException>(() =>
            {
                var r = new EndianSpanReader(buf);
                r.ReadBytes(4);
            });
        }

        [Fact]
        public void SerializedFile_TypeTreeBlobRead_UsesEndianSpanReaderForLocalAndCommonStrings()
        {
            // Local string buffer: "CustomStruct\0myField\0"
            var strBuf = Encoding.UTF8.GetBytes("CustomStruct\0myField\0");
            const uint customStructOffset = 0;
            uint myFieldOffset = (uint)Encoding.UTF8.GetByteCount("CustomStruct\0");
            const uint commonIntOffset = 0x80000000u | 222u;  // "int"
            const uint commonNameOffset = 0x80000000u | 427u; // "m_Name"

            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var bw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.UTF8.GetBytes("2021.3.16f1"));
                bw.Write((byte)0);
                bw.Write((int)BuildTarget.StandaloneWindows64);
                bw.Write(true); // m_EnableTypeTree
                bw.Write(1); // typeCount

                // SerializedType
                bw.Write((int)ClassIDType.MonoBehaviour);
                bw.Write(false); // m_IsStrippedType
                bw.Write((short)-1); // m_ScriptTypeIndex
                bw.Write(new byte[16]); // m_ScriptID (required for classID == 114)
                bw.Write(new byte[16]); // m_OldTypeHash

                // TypeTreeBlob: 2 nodes
                bw.Write(2); // numberOfNodes
                bw.Write(strBuf.Length); // stringBufferSize

                // Node 0: CustomStruct m_Name (level 0)
                bw.Write((ushort)1);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write(customStructOffset);
                bw.Write(commonNameOffset);
                bw.Write(-1);
                bw.Write(0);
                bw.Write(0);

                // Node 1: int myField (level 1)
                bw.Write((ushort)1);
                bw.Write((byte)1);
                bw.Write((byte)0);
                bw.Write(commonIntOffset);
                bw.Write(myFieldOffset);
                bw.Write(4);
                bw.Write(1);
                bw.Write(0);

                bw.Write(strBuf);
                bw.Write(0); // m_TypeDependencies count = 0
                bw.Write(0); // objectCount
                bw.Write(0); // scriptCount
                bw.Write(0); // externalsCount
                bw.Write((byte)0); // userInformation
                bw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            uint totalSize = (uint)(headerSize + metadata.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), totalSize);
            fullBytes[16] = 0; // LittleEndian
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);

            using var fileMs = new MemoryStream(fullBytes);
            using var fileReader = new FileReader("typetree_blob.assets", fileMs);
            var sf = new SerializedFile(fileReader, new AssetsManager());

            Assert.Single(sf.m_Types);
            var nodes = sf.m_Types[0].m_Type!.m_Nodes;
            Assert.Equal(2, nodes.Count);
            Assert.Equal("CustomStruct", nodes[0].m_Type);
            Assert.Equal("m_Name", nodes[0].m_Name);
            Assert.Equal("int", nodes[1].m_Type);
            Assert.Equal("myField", nodes[1].m_Name);
        }

        // =========================================================================
        // 3. Streaming TypeTree Skip-Table, SEC-05 Bounds, and Texture2D / Material Parity
        // =========================================================================

        [Fact]
        public void TypeTreeHelper_SkipTableAndSec05Checks_WorkAcrossEdgeCases()
        {
            var nodes = new List<TypeTreeNode>
            {
                new TypeTreeNode("Root", "Base", 0, false),
                new TypeTreeNode("int", "a", 1, false),
                new TypeTreeNode("Sub", "b", 1, false),
                new TypeTreeNode("float", "x", 2, false),
                new TypeTreeNode("float", "y", 2, false),
                new TypeTreeNode("int", "c", 1, false),
            };

            var skip = TypeTreeHelper.GetNextSiblingTable(nodes);
            Assert.Equal(new[] { 6, 2, 5, 4, 5, 6 }, skip);

            // Mutating the list invalidates stale cached count cleanly
            nodes.Add(new TypeTreeNode("int", "d", 1, false));
            var skip2 = TypeTreeHelper.GetNextSiblingTable(nodes);
            Assert.Equal(new[] { 7, 2, 5, 4, 5, 6, 7 }, skip2);

            // SEC-05: Recursion depth > 64 throws InvalidDataException
            var deepNodes = new List<TypeTreeNode>();
            for (int level = 0; level <= 66; level++)
            {
                deepNodes.Add(new TypeTreeNode("Nested", $"n{level}", level, false));
            }
            var deepTree = new TypeTree { m_Nodes = deepNodes };
            var deepFile = CreateSerializedFileWithData(new byte[16], EndianType.LittleEndian, out var deepObjInfo, 16);
            using var deepReader = new ObjectReader(deepFile.reader, deepFile, deepObjInfo);
            Assert.Throws<InvalidDataException>(() => TypeTreeHelper.ReadType(deepTree, deepReader));

            // SEC-05: TypelessData count exceeding ObjectReader.byteSize throws EndOfStreamException
            var blobNodes = new List<TypeTreeNode>
            {
                new TypeTreeNode("BlobObj", "Base", 0, false),
                new TypeTreeNode("TypelessData", "image data", 1, false),
                new TypeTreeNode("int", "size", 2, false),
                new TypeTreeNode("UInt8", "data", 2, false),
            };
            var blobTree = new TypeTree { m_Nodes = blobNodes };
            var blobData = new byte[64];
            BinaryPrimitives.WriteInt32LittleEndian(blobData.AsSpan(0, 4), 20); // object only has 8 bytes
            var blobFile = CreateSerializedFileWithData(blobData, EndianType.LittleEndian, out var blobObjInfo, 8);
            using var blobReader = new ObjectReader(blobFile.reader, blobFile, blobObjInfo);
            Assert.Throws<EndOfStreamException>(() => TypeTreeHelper.ReadType(blobTree, blobReader));

            int idx = 1;
            blobReader.Reset();
            Assert.Throws<EndOfStreamException>(() =>
                TypeTreeHelper.TrySkipBlob(blobNodes, TypeTreeHelper.GetNextSiblingTable(blobNodes), blobReader, ref idx, out _, out _));
        }

        [Theory]
        [InlineData(EndianType.LittleEndian)]
        [InlineData(EndianType.BigEndian)]
        public void Texture2D_StreamingTypeTree_MatchesToTypeAndAvoidsTypelessDataAllocation(EndianType endian)
        {
            const int payloadBytes = 4 * 1024 * 1024; // 4 MB TypelessData
            var expectedImageBytes = new byte[payloadBytes];
            for (int i = 0; i < expectedImageBytes.Length; i++)
            {
                expectedImageBytes[i] = (byte)((i * 131 + 17) & 0xFF);
            }

            var typeTree = BuildModernTexture2DTypeTree();

            byte[] objectBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, endian, "LargeSyntheticTex");
                WriteInt32(ms, endian, 0); // m_ForcedFallbackFormat
                ms.WriteByte(1); // m_DownscaleFallback
                ms.WriteByte(0); // m_IsAlphaChannelOptional
                AlignStream4(ms);
                WriteInt32(ms, endian, 1024); // m_Width
                WriteInt32(ms, endian, 1024); // m_Height
                WriteInt32(ms, endian, payloadBytes); // m_CompleteImageSize
                WriteInt32(ms, endian, 2); // m_MipsStripped
                WriteInt32(ms, endian, (int)TextureFormat.RGBA32); // m_TextureFormat
                WriteInt32(ms, endian, 11); // m_MipCount
                ms.WriteByte(1); // m_IsReadable
                ms.WriteByte(1); // m_IsPreProcessed
                ms.WriteByte(0); // m_IgnoreMasterTextureLimit
                ms.WriteByte(1); // m_StreamingMipmaps
                AlignStream4(ms);
                WriteInt32(ms, endian, 3); // m_StreamingMipmapsPriority
                WriteInt32(ms, endian, 1); // m_ImageCount
                WriteInt32(ms, endian, 2); // m_TextureDimension
                // m_TextureSettings
                WriteInt32(ms, endian, 2); // m_FilterMode (Trilinear)
                WriteInt32(ms, endian, 8); // m_Aniso
                WriteSingle(ms, endian, -0.5f); // m_MipBias
                WriteInt32(ms, endian, 1); // m_WrapU (Clamp)
                WriteInt32(ms, endian, 0); // m_WrapV
                WriteInt32(ms, endian, 0); // m_WrapW
                WriteInt32(ms, endian, 6); // m_LightmapFormat
                WriteInt32(ms, endian, 1); // m_ColorSpace
                // m_PlatformBlob (6 bytes + align)
                WriteInt32(ms, endian, 6);
                ms.Write(new byte[] { 10, 20, 30, 40, 50, 60 });
                AlignStream4(ms);
                // Custom field only present in TypeTree (proves manual fallback was NOT used)
                WriteInt32(ms, endian, 0x12345678);
                // TypelessData image data (4 MB)
                WriteInt32(ms, endian, payloadBytes);
                ms.Write(expectedImageBytes);
                // m_StreamData
                WriteUInt64(ms, endian, 0); // offset
                WriteUInt32(ms, endian, 0); // size
                WriteAlignedString(ms, endian, string.Empty); // path

                objectBytes = ms.ToArray();
            }

            var assetsFile = CreateSerializedFileWithData(objectBytes, endian, out var objInfo, objectBytes.Length);
            objInfo.classID = (int)ClassIDType.Texture2D;
            objInfo.serializedType = new SerializedType
            {
                classID = (int)ClassIDType.Texture2D,
                m_Type = typeTree
            };

            // Warm up static tables / JIT on a cloned reader before measuring steady-state allocation
            using (var warmupFileReader = assetsFile.reader.Clone())
            {
                var warmupReader = new ObjectReader(warmupFileReader, assetsFile, objInfo);
                _ = new Texture2D(warmupReader);
            }

            // Measure allocations during Texture2D streaming TypeTree parse
            using var clonedReader = assetsFile.reader.Clone();
            var texReader = new ObjectReader(clonedReader, assetsFile, objInfo);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var tex = new Texture2D(texReader);
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            long allocatedDuringParse = allocAfter - allocBefore;

            // Must allocate nowhere near the 4 MB TypelessData payload!
            Assert.True(allocatedDuringParse < 16 * 1024, $"Expected streaming Texture2D parse to skip 4 MB TypelessData without allocating it, but allocated {allocatedDuringParse} bytes.");

            // Verify Dump() / ReadTypeString also works cleanly with skip-table
            string? dumpText = tex.Dump();
            Assert.NotNull(dumpText);
            Assert.Contains("TypelessData image data", dumpText);
            Assert.Contains($"int size = {payloadBytes}", dumpText);

            // Compare field-by-field against ToType() reference
            var dict = tex.ToType();
            Assert.NotNull(dict);
            Assert.Equal("LargeSyntheticTex", tex.m_Name);
            Assert.Equal(Convert.ToInt32(dict!["m_Width"]), tex.m_Width);
            Assert.Equal(Convert.ToInt32(dict["m_Height"]), tex.m_Height);
            Assert.Equal((TextureFormat)Convert.ToInt32(dict["m_TextureFormat"]), tex.m_TextureFormat);
            Assert.Equal(Convert.ToInt32(dict["m_MipCount"]), tex.m_MipCount);

            var settingsDict = Assert.IsType<OrderedDictionary>(dict["m_TextureSettings"]);
            Assert.Equal(Convert.ToInt32(settingsDict["m_FilterMode"]), tex.m_TextureSettings.m_FilterMode);
            Assert.Equal(Convert.ToInt32(settingsDict["m_Aniso"]), tex.m_TextureSettings.m_Aniso);
            Assert.Equal(Convert.ToSingle(settingsDict["m_MipBias"]), tex.m_TextureSettings.m_MipBias);
            Assert.Equal(Convert.ToInt32(settingsDict["m_WrapU"]), tex.m_TextureSettings.m_WrapMode);

            var streamDict = Assert.IsType<OrderedDictionary>(dict["m_StreamData"]);
            Assert.NotNull(tex.m_StreamData);
            Assert.Equal(Convert.ToInt64(streamDict["offset"]), tex.m_StreamData!.offset);
            Assert.Equal(Convert.ToUInt32(streamDict["size"]), tex.m_StreamData.size);
            Assert.Equal(streamDict["path"] as string ?? string.Empty, tex.m_StreamData.path);

            var referenceBytes = Assert.IsType<byte[]>(dict["image data"]);
            Assert.Equal(payloadBytes, tex.image_data.Size);
            Assert.Equal(referenceBytes, tex.image_data.GetData());
        }

        [Fact]
        public void Texture2D_StreamingTypeTree_HandlesLegacyMipMapWrapModeAndStreamedData()
        {
            var typeTree = BuildLegacyStreamedTexture2DTypeTree();
            byte[] objectBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, EndianType.LittleEndian, "LegacyStreamedTex");
                WriteInt32(ms, EndianType.LittleEndian, 512); // m_Width
                WriteInt32(ms, EndianType.LittleEndian, 256); // m_Height
                WriteInt32(ms, EndianType.LittleEndian, (int)TextureFormat.DXT5); // m_TextureFormat
                ms.WriteByte(1); // m_MipMap
                AlignStream4(ms);
                // m_TextureSettings
                WriteInt32(ms, EndianType.LittleEndian, 1); // m_FilterMode
                WriteInt32(ms, EndianType.LittleEndian, 4); // m_Aniso
                WriteSingle(ms, EndianType.LittleEndian, 0.25f); // m_MipBias
                WriteInt32(ms, EndianType.LittleEndian, 2); // m_WrapMode
                // image data (empty because streamed)
                WriteInt32(ms, EndianType.LittleEndian, 0);
                // m_StreamData
                WriteUInt32(ms, EndianType.LittleEndian, 4096); // offset (UInt32 legacy)
                WriteUInt32(ms, EndianType.LittleEndian, 65536); // size
                WriteAlignedString(ms, EndianType.LittleEndian, "sharedassets0.assets.resS");
                objectBytes = ms.ToArray();
            }

            var assetsFile = CreateSerializedFileWithData(objectBytes, EndianType.LittleEndian, out var objInfo, objectBytes.Length, new[] { 5, 3, 0, 1 });
            objInfo.classID = (int)ClassIDType.Texture2D;
            objInfo.serializedType = new SerializedType
            {
                classID = (int)ClassIDType.Texture2D,
                m_Type = typeTree
            };

            using var texReader = new ObjectReader(assetsFile.reader, assetsFile, objInfo);
            var tex = new Texture2D(texReader);

            Assert.Equal("LegacyStreamedTex", tex.m_Name);
            Assert.Equal(512, tex.m_Width);
            Assert.Equal(256, tex.m_Height);
            Assert.Equal(TextureFormat.DXT5, tex.m_TextureFormat);
            Assert.True(tex.m_MipMap);
            Assert.Equal(2, tex.m_TextureSettings.m_WrapMode);
            Assert.NotNull(tex.m_StreamData);
            Assert.Equal(4096L, tex.m_StreamData!.offset);
            Assert.Equal(65536u, tex.m_StreamData.size);
            Assert.Equal("sharedassets0.assets.resS", tex.m_StreamData.path);
            Assert.Equal(65536, tex.image_data.Size);
        }

        [Theory]
        [InlineData(EndianType.LittleEndian)]
        [InlineData(EndianType.BigEndian)]
        public void Material_StreamingTypeTree_MatchesToTypeAcrossAllPropertyMaps(EndianType endian)
        {
            var typeTree = BuildMaterialTypeTree();

            byte[] objectBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, endian, "TestMaterial_Mat");
                // m_Shader PPtr
                WriteInt32(ms, endian, 1);
                WriteInt64(ms, endian, 48);
                // m_Parent PPtr
                WriteInt32(ms, endian, 2);
                WriteInt64(ms, endian, -9999999999L);
                // m_ValidKeywords (2 strings)
                WriteInt32(ms, endian, 2);
                WriteAlignedString(ms, endian, "_NORMALMAP");
                WriteAlignedString(ms, endian, "_ALPHATEST_ON");
                // m_LightmapFlags + m_EnableInstancingVariants + m_CustomRenderQueue
                WriteUInt32(ms, endian, 4u);
                ms.WriteByte(1);
                AlignStream4(ms);
                WriteInt32(ms, endian, 2450);
                // stringTagMap (1 entry)
                WriteInt32(ms, endian, 1);
                WriteAlignedString(ms, endian, "RenderType");
                WriteAlignedString(ms, endian, "TransparentCutout");
                // disabledShaderPasses (1 string)
                WriteInt32(ms, endian, 1);
                WriteAlignedString(ms, endian, "ShadowCaster");

                // m_SavedProperties
                // m_TexEnvs (2 entries)
                WriteInt32(ms, endian, 2);
                WriteAlignedString(ms, endian, "_MainTex");
                WriteInt32(ms, endian, 0);
                WriteInt64(ms, endian, 1001L);
                WriteSingle(ms, endian, 2.0f); WriteSingle(ms, endian, 3.0f);
                WriteSingle(ms, endian, 0.25f); WriteSingle(ms, endian, -0.5f);

                WriteAlignedString(ms, endian, "_BumpMap");
                WriteInt32(ms, endian, 3);
                WriteInt64(ms, endian, 2002L);
                WriteSingle(ms, endian, 1.0f); WriteSingle(ms, endian, 1.0f);
                WriteSingle(ms, endian, 0.0f); WriteSingle(ms, endian, 0.0f);

                // m_Ints (2 entries)
                WriteInt32(ms, endian, 2);
                WriteAlignedString(ms, endian, "_StencilRef");
                WriteInt32(ms, endian, 7);
                WriteAlignedString(ms, endian, "_ZWrite");
                WriteInt32(ms, endian, 1);

                // m_Floats (2 entries)
                WriteInt32(ms, endian, 2);
                WriteAlignedString(ms, endian, "_Cutoff");
                WriteSingle(ms, endian, 0.5f);
                WriteAlignedString(ms, endian, "_Glossiness");
                WriteSingle(ms, endian, 0.875f);

                // m_Colors (2 entries)
                WriteInt32(ms, endian, 2);
                WriteAlignedString(ms, endian, "_Color");
                WriteSingle(ms, endian, 0.8f); WriteSingle(ms, endian, 0.6f); WriteSingle(ms, endian, 0.4f); WriteSingle(ms, endian, 1.0f);
                WriteAlignedString(ms, endian, "_EmissionColor");
                WriteSingle(ms, endian, 0.1f); WriteSingle(ms, endian, 0.2f); WriteSingle(ms, endian, 0.3f); WriteSingle(ms, endian, 0.5f);

                objectBytes = ms.ToArray();
            }

            var assetsFile = CreateSerializedFileWithData(objectBytes, endian, out var objInfo, objectBytes.Length);
            objInfo.classID = (int)ClassIDType.Material;
            objInfo.serializedType = new SerializedType
            {
                classID = (int)ClassIDType.Material,
                m_Type = typeTree
            };

            using var matReader = new ObjectReader(assetsFile.reader, assetsFile, objInfo);
            var mat = new Material(matReader);
            var dict = mat.ToType();
            Assert.NotNull(dict);

            Assert.Equal("TestMaterial_Mat", mat.m_Name);
            var shaderDict = Assert.IsType<OrderedDictionary>(dict!["m_Shader"]);
            Assert.Equal(Convert.ToInt32(shaderDict["m_FileID"]), mat.m_Shader.m_FileID);
            Assert.Equal(Convert.ToInt64(shaderDict["m_PathID"]), mat.m_Shader.m_PathID);

            var parentDict = Assert.IsType<OrderedDictionary>(dict["m_Parent"]);
            Assert.Equal(Convert.ToInt32(parentDict["m_FileID"]), mat.m_Parent.m_FileID);
            Assert.Equal(Convert.ToInt64(parentDict["m_PathID"]), mat.m_Parent.m_PathID);

            var propsDict = Assert.IsType<OrderedDictionary>(dict["m_SavedProperties"]);

            var texEnvsRef = Assert.IsType<List<KeyValuePair<object, object>>>(propsDict["m_TexEnvs"]);
            Assert.Equal(texEnvsRef.Count, mat.m_SavedProperties.m_TexEnvs.Length);
            for (int k = 0; k < texEnvsRef.Count; k++)
            {
                Assert.Equal(texEnvsRef[k].Key, mat.m_SavedProperties.m_TexEnvs[k].Key);
                var vRef = Assert.IsType<OrderedDictionary>(texEnvsRef[k].Value);
                var texRef = Assert.IsType<OrderedDictionary>(vRef["m_Texture"]);
                var scaleRef = Assert.IsType<OrderedDictionary>(vRef["m_Scale"]);
                var offsetRef = Assert.IsType<OrderedDictionary>(vRef["m_Offset"]);
                var vActual = mat.m_SavedProperties.m_TexEnvs[k].Value;
                Assert.Equal(Convert.ToInt32(texRef["m_FileID"]), vActual.m_Texture.m_FileID);
                Assert.Equal(Convert.ToInt64(texRef["m_PathID"]), vActual.m_Texture.m_PathID);
                Assert.Equal(Convert.ToSingle(scaleRef["x"]), vActual.m_Scale.X);
                Assert.Equal(Convert.ToSingle(scaleRef["y"]), vActual.m_Scale.Y);
                Assert.Equal(Convert.ToSingle(offsetRef["x"]), vActual.m_Offset.X);
                Assert.Equal(Convert.ToSingle(offsetRef["y"]), vActual.m_Offset.Y);
            }

            var intsRef = Assert.IsType<List<KeyValuePair<object, object>>>(propsDict["m_Ints"]);
            Assert.Equal(intsRef.Count, mat.m_SavedProperties.m_Ints.Length);
            for (int k = 0; k < intsRef.Count; k++)
            {
                Assert.Equal(intsRef[k].Key, mat.m_SavedProperties.m_Ints[k].Key);
                Assert.Equal(Convert.ToInt32(intsRef[k].Value), mat.m_SavedProperties.m_Ints[k].Value);
            }

            var floatsRef = Assert.IsType<List<KeyValuePair<object, object>>>(propsDict["m_Floats"]);
            Assert.Equal(floatsRef.Count, mat.m_SavedProperties.m_Floats.Length);
            for (int k = 0; k < floatsRef.Count; k++)
            {
                Assert.Equal(floatsRef[k].Key, mat.m_SavedProperties.m_Floats[k].Key);
                Assert.Equal(Convert.ToSingle(floatsRef[k].Value), mat.m_SavedProperties.m_Floats[k].Value);
            }

            var colorsRef = Assert.IsType<List<KeyValuePair<object, object>>>(propsDict["m_Colors"]);
            Assert.Equal(colorsRef.Count, mat.m_SavedProperties.m_Colors.Length);
            for (int k = 0; k < colorsRef.Count; k++)
            {
                Assert.Equal(colorsRef[k].Key, mat.m_SavedProperties.m_Colors[k].Key);
                var cRef = Assert.IsType<OrderedDictionary>(colorsRef[k].Value);
                var cActual = mat.m_SavedProperties.m_Colors[k].Value;
                Assert.Equal(Convert.ToSingle(cRef["r"]), cActual.R);
                Assert.Equal(Convert.ToSingle(cRef["g"]), cActual.G);
                Assert.Equal(Convert.ToSingle(cRef["b"]), cActual.B);
                Assert.Equal(Convert.ToSingle(cRef["a"]), cActual.A);
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static TypeTree BuildModernTexture2DTypeTree()
        {
            var nodes = new List<TypeTreeNode>();
            AddNode(nodes, "Texture2D", "Base", 0, false);
            AddStringNodes(nodes, "m_Name", 1);
            AddNode(nodes, "int", "m_ForcedFallbackFormat", 1, false);
            AddNode(nodes, "bool", "m_DownscaleFallback", 1, false);
            AddNode(nodes, "bool", "m_IsAlphaChannelOptional", 1, true);
            AddNode(nodes, "int", "m_Width", 1, false);
            AddNode(nodes, "int", "m_Height", 1, false);
            AddNode(nodes, "int", "m_CompleteImageSize", 1, false);
            AddNode(nodes, "int", "m_MipsStripped", 1, false);
            AddNode(nodes, "int", "m_TextureFormat", 1, false);
            AddNode(nodes, "int", "m_MipCount", 1, false);
            AddNode(nodes, "bool", "m_IsReadable", 1, false);
            AddNode(nodes, "bool", "m_IsPreProcessed", 1, false);
            AddNode(nodes, "bool", "m_IgnoreMasterTextureLimit", 1, false);
            AddNode(nodes, "bool", "m_StreamingMipmaps", 1, true);
            AddNode(nodes, "int", "m_StreamingMipmapsPriority", 1, false);
            AddNode(nodes, "int", "m_ImageCount", 1, false);
            AddNode(nodes, "int", "m_TextureDimension", 1, false);

            AddNode(nodes, "GLTextureSettings", "m_TextureSettings", 1, false);
            AddNode(nodes, "int", "m_FilterMode", 2, false);
            AddNode(nodes, "int", "m_Aniso", 2, false);
            AddNode(nodes, "float", "m_MipBias", 2, false);
            AddNode(nodes, "int", "m_WrapU", 2, false);
            AddNode(nodes, "int", "m_WrapV", 2, false);
            AddNode(nodes, "int", "m_WrapW", 2, false);

            AddNode(nodes, "int", "m_LightmapFormat", 1, false);
            AddNode(nodes, "int", "m_ColorSpace", 1, false);

            AddNode(nodes, "vector", "m_PlatformBlob", 1, false);
            AddNode(nodes, "Array", "Array", 2, true);
            AddNode(nodes, "int", "size", 3, false);
            AddNode(nodes, "UInt8", "data", 3, false);

            AddNode(nodes, "int", "m_CustomExtraTypeTreeTag", 1, false);

            AddNode(nodes, "TypelessData", "image data", 1, false);
            AddNode(nodes, "int", "size", 2, false);
            AddNode(nodes, "UInt8", "data", 2, false);

            AddNode(nodes, "StreamingInfo", "m_StreamData", 1, false);
            AddNode(nodes, "UInt64", "offset", 2, false);
            AddNode(nodes, "unsigned int", "size", 2, false);
            AddStringNodes(nodes, "path", 2);

            return new TypeTree { m_Nodes = nodes };
        }

        private static TypeTree BuildLegacyStreamedTexture2DTypeTree()
        {
            var nodes = new List<TypeTreeNode>();
            AddNode(nodes, "Texture2D", "Base", 0, false);
            AddStringNodes(nodes, "m_Name", 1);
            AddNode(nodes, "int", "m_Width", 1, false);
            AddNode(nodes, "int", "m_Height", 1, false);
            AddNode(nodes, "int", "m_TextureFormat", 1, false);
            AddNode(nodes, "bool", "m_MipMap", 1, true);
            AddNode(nodes, "GLTextureSettings", "m_TextureSettings", 1, false);
            AddNode(nodes, "int", "m_FilterMode", 2, false);
            AddNode(nodes, "int", "m_Aniso", 2, false);
            AddNode(nodes, "float", "m_MipBias", 2, false);
            AddNode(nodes, "int", "m_WrapMode", 2, false);
            AddNode(nodes, "TypelessData", "image data", 1, false);
            AddNode(nodes, "int", "size", 2, false);
            AddNode(nodes, "UInt8", "data", 2, false);
            AddNode(nodes, "StreamingInfo", "m_StreamData", 1, false);
            AddNode(nodes, "unsigned int", "offset", 2, false);
            AddNode(nodes, "unsigned int", "size", 2, false);
            AddStringNodes(nodes, "path", 2);
            return new TypeTree { m_Nodes = nodes };
        }

        private static TypeTree BuildMaterialTypeTree()
        {
            var nodes = new List<TypeTreeNode>();
            AddNode(nodes, "Material", "Base", 0, false);
            AddStringNodes(nodes, "m_Name", 1);
            AddPPtrNodes(nodes, "PPtr<Shader>", "m_Shader", 1);
            AddPPtrNodes(nodes, "PPtr<Material>", "m_Parent", 1);

            AddNode(nodes, "vector", "m_ValidKeywords", 1, false);
            AddNode(nodes, "Array", "Array", 2, false);
            AddNode(nodes, "int", "size", 3, false);
            AddStringNodes(nodes, "data", 3);

            AddNode(nodes, "unsigned int", "m_LightmapFlags", 1, false);
            AddNode(nodes, "bool", "m_EnableInstancingVariants", 1, true);
            AddNode(nodes, "int", "m_CustomRenderQueue", 1, false);

            AddNode(nodes, "map", "stringTagMap", 1, false);
            AddNode(nodes, "Array", "Array", 2, false);
            AddNode(nodes, "int", "size", 3, false);
            AddNode(nodes, "pair", "data", 3, false);
            AddStringNodes(nodes, "first", 4);
            AddStringNodes(nodes, "second", 4);

            AddNode(nodes, "vector", "disabledShaderPasses", 1, false);
            AddNode(nodes, "Array", "Array", 2, false);
            AddNode(nodes, "int", "size", 3, false);
            AddStringNodes(nodes, "data", 3);

            AddNode(nodes, "UnityPropertySheet", "m_SavedProperties", 1, false);

            // m_TexEnvs
            AddNode(nodes, "map", "m_TexEnvs", 2, false);
            AddNode(nodes, "Array", "Array", 3, false);
            AddNode(nodes, "int", "size", 4, false);
            AddNode(nodes, "pair", "data", 4, false);
            AddStringNodes(nodes, "first", 5);
            AddNode(nodes, "UnityTexEnv", "second", 5, false);
            AddPPtrNodes(nodes, "PPtr<Texture>", "m_Texture", 6);
            AddNode(nodes, "Vector2f", "m_Scale", 6, false);
            AddNode(nodes, "float", "x", 7, false);
            AddNode(nodes, "float", "y", 7, false);
            AddNode(nodes, "Vector2f", "m_Offset", 6, false);
            AddNode(nodes, "float", "x", 7, false);
            AddNode(nodes, "float", "y", 7, false);

            // m_Ints
            AddNode(nodes, "map", "m_Ints", 2, false);
            AddNode(nodes, "Array", "Array", 3, false);
            AddNode(nodes, "int", "size", 4, false);
            AddNode(nodes, "pair", "data", 4, false);
            AddStringNodes(nodes, "first", 5);
            AddNode(nodes, "int", "second", 5, false);

            // m_Floats
            AddNode(nodes, "map", "m_Floats", 2, false);
            AddNode(nodes, "Array", "Array", 3, false);
            AddNode(nodes, "int", "size", 4, false);
            AddNode(nodes, "pair", "data", 4, false);
            AddStringNodes(nodes, "first", 5);
            AddNode(nodes, "float", "second", 5, false);

            // m_Colors
            AddNode(nodes, "map", "m_Colors", 2, false);
            AddNode(nodes, "Array", "Array", 3, false);
            AddNode(nodes, "int", "size", 4, false);
            AddNode(nodes, "pair", "data", 4, false);
            AddStringNodes(nodes, "first", 5);
            AddNode(nodes, "ColorRGBA", "second", 5, false);
            AddNode(nodes, "float", "r", 6, false);
            AddNode(nodes, "float", "g", 6, false);
            AddNode(nodes, "float", "b", 6, false);
            AddNode(nodes, "float", "a", 6, false);

            return new TypeTree { m_Nodes = nodes };
        }

        [Fact]
        public void Texture2D_And_Material_IncompleteTypeTree_FallsBackCleanlyToManualParsing()
        {
            // Incomplete TypeTree (missing m_Width/m_SavedProperties) must restore reader position and parse manually
            var texData = new byte[4] { 0xDE, 0xAD, 0xBE, 0xEF };
            byte[] texObjBytes;
            using (var ms = new MemoryStream())
            {
                WriteAlignedString(ms, EndianType.LittleEndian, "ManualFallbackTex");
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_ForcedFallbackFormat
                ms.WriteByte(0); // m_DownscaleFallback
                ms.WriteByte(0); // m_IsAlphaChannelOptional
                AlignStream4(ms);
                WriteInt32(ms, EndianType.LittleEndian, 2); // m_Width
                WriteInt32(ms, EndianType.LittleEndian, 2); // m_Height
                WriteInt32(ms, EndianType.LittleEndian, 4); // m_CompleteImageSize
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_MipsStripped
                WriteInt32(ms, EndianType.LittleEndian, (int)TextureFormat.Alpha8);
                WriteInt32(ms, EndianType.LittleEndian, 1); // m_MipCount
                ms.WriteByte(1); // m_IsReadable
                ms.WriteByte(0); // m_IsPreProcessed
                ms.WriteByte(0); // m_IgnoreMasterTextureLimit
                ms.WriteByte(0); // m_StreamingMipmaps
                AlignStream4(ms);
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_StreamingMipmapsPriority
                WriteInt32(ms, EndianType.LittleEndian, 1); // m_ImageCount
                WriteInt32(ms, EndianType.LittleEndian, 2); // m_TextureDimension
                WriteInt32(ms, EndianType.LittleEndian, 1); // m_FilterMode
                WriteInt32(ms, EndianType.LittleEndian, 1); // m_Aniso
                WriteSingle(ms, EndianType.LittleEndian, 0f); // m_MipBias
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_WrapU
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_WrapV
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_WrapW
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_LightmapFormat
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_ColorSpace
                WriteInt32(ms, EndianType.LittleEndian, 0); // m_PlatformBlob len
                AlignStream4(ms);
                WriteInt32(ms, EndianType.LittleEndian, texData.Length);
                ms.Write(texData);
                texObjBytes = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(texObjBytes, EndianType.LittleEndian, out var objInfo, texObjBytes.Length);
            objInfo.classID = (int)ClassIDType.Texture2D;
            objInfo.serializedType = new SerializedType
            {
                classID = (int)ClassIDType.Texture2D,
                m_Type = new TypeTree { m_Nodes = new List<TypeTreeNode> { new TypeTreeNode("Texture2D", "Base", 0, false) } }
            };

            using var cloned = sf.reader.Clone();
            var texReader = new ObjectReader(cloned, sf, objInfo);
            var tex = new Texture2D(texReader);
            Assert.Equal("ManualFallbackTex", tex.m_Name);
            Assert.Equal(2, tex.m_Width);
            Assert.Equal(2, tex.m_Height);
            Assert.Equal(TextureFormat.Alpha8, tex.m_TextureFormat);
            Assert.Equal(texData, tex.image_data.GetData());
        }

        private static void AddNode(List<TypeTreeNode> nodes, string type, string name, int level, bool align)
        {
            nodes.Add(new TypeTreeNode(type, name, level, align));
        }

        private static void AddStringNodes(List<TypeTreeNode> nodes, string name, int level)
        {
            AddNode(nodes, "string", name, level, false);
            AddNode(nodes, "Array", "Array", level + 1, false);
            AddNode(nodes, "int", "size", level + 2, false);
            AddNode(nodes, "char", "data", level + 2, false);
        }

        private static void AddPPtrNodes(List<TypeTreeNode> nodes, string type, string name, int level)
        {
            AddNode(nodes, type, name, level, false);
            AddNode(nodes, "int", "m_FileID", level + 1, false);
            AddNode(nodes, "SInt64", "m_PathID", level + 1, false);
        }

        private static SerializedFile CreateSerializedFileWithData(
            byte[] dataSection,
            EndianType endian,
            out ObjectInfo objectInfo,
            int objectByteSize,
            int[]? versionOverride = null)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var bw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.UTF8.GetBytes("2021.3.16f1"));
                bw.Write((byte)0);
                bw.Write((int)BuildTarget.StandaloneWindows64);
                bw.Write(false); // m_EnableTypeTree
                bw.Write(0); // typeCount
                bw.Write(0); // objectCount
                bw.Write(0); // scriptCount
                bw.Write(0); // externalsCount
                bw.Write((byte)0); // userInformation
                bw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            uint unalignedDataOffset = (uint)(headerSize + metadata.Length);
            uint dataOffset = (unalignedDataOffset + 15u) & ~15u; // Unity aligns data section to 16 bytes
            uint totalSize = dataOffset + (uint)dataSection.Length;
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), dataOffset);
            fullBytes[16] = 0; // LittleEndian metadata
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            Buffer.BlockCopy(dataSection, 0, fullBytes, (int)dataOffset, dataSection.Length);

            var ms = new MemoryStream(fullBytes);
            var fileReader = new FileReader("synthetic.assets", ms);
            var sf = new SerializedFile(fileReader, new AssetsManager());
            fileReader.Endian = endian;
            if (versionOverride != null)
            {
                sf.version = versionOverride;
            }

            objectInfo = new ObjectInfo
            {
                byteStart = dataOffset,
                byteSize = (uint)objectByteSize,
                m_PathID = 1
            };
            sf.ObjectInfoDic[objectInfo.m_PathID] = objectInfo;
            return sf;
        }

        private static void WriteCountAndPrimitives<T>(Stream s, EndianType endian, T[] items, Action<Stream, EndianType, T> writeItem)
        {
            WriteInt32(s, endian, items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                writeItem(s, endian, items[i]);
            }
        }

        private static void WriteInt16(Stream s, EndianType endian, short value)
        {
            Span<byte> buf = stackalloc byte[2];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteInt16BigEndian(buf, value);
            else BinaryPrimitives.WriteInt16LittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteUInt16(Stream s, EndianType endian, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteUInt16BigEndian(buf, value);
            else BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteInt32(Stream s, EndianType endian, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteInt32BigEndian(buf, value);
            else BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteUInt32(Stream s, EndianType endian, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            else BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteInt64(Stream s, EndianType endian, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteInt64BigEndian(buf, value);
            else BinaryPrimitives.WriteInt64LittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteUInt64(Stream s, EndianType endian, ulong value)
        {
            Span<byte> buf = stackalloc byte[8];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteUInt64BigEndian(buf, value);
            else BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteSingle(Stream s, EndianType endian, float value)
        {
            Span<byte> buf = stackalloc byte[4];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteSingleBigEndian(buf, value);
            else BinaryPrimitives.WriteSingleLittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteDouble(Stream s, EndianType endian, double value)
        {
            Span<byte> buf = stackalloc byte[8];
            if (endian == EndianType.BigEndian) BinaryPrimitives.WriteDoubleBigEndian(buf, value);
            else BinaryPrimitives.WriteDoubleLittleEndian(buf, value);
            s.Write(buf);
        }

        private static void WriteAlignedString(Stream s, EndianType endian, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(s, endian, bytes.Length);
            s.Write(bytes);
            AlignStream4(s);
        }

        private static void AlignStream4(Stream s)
        {
            long mod = s.Position % 4;
            if (mod != 0)
            {
                for (int i = 0; i < 4 - mod; i++)
                {
                    s.WriteByte(0);
                }
            }
        }

        private sealed class ChunkedReadOnlyStream : Stream
        {
            private readonly byte[] data;
            private readonly int maxReadChunk;
            private readonly bool canSeek;
            private int position;

            public ChunkedReadOnlyStream(byte[] data, int maxReadChunk, bool canSeek = true)
            {
                this.data = data;
                this.maxReadChunk = maxReadChunk;
                this.canSeek = canSeek;
            }

            public override bool CanRead => true;
            public override bool CanSeek => canSeek;
            public override bool CanWrite => false;
            public override long Length => canSeek ? data.Length : throw new NotSupportedException();
            public override long Position
            {
                get => canSeek ? position : throw new NotSupportedException();
                set
                {
                    if (!canSeek) throw new NotSupportedException();
                    if (value < 0 || value > data.Length) throw new ArgumentOutOfRangeException(nameof(value));
                    position = (int)value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int remaining = data.Length - position;
                if (remaining <= 0 || count <= 0)
                {
                    return 0;
                }
                int toRead = Math.Min(Math.Min(count, remaining), maxReadChunk);
                Buffer.BlockCopy(data, position, buffer, offset, toRead);
                position += toRead;
                return toRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                if (!canSeek) throw new NotSupportedException();
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    SeekOrigin.End => data.Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                Position = target;
                return position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
