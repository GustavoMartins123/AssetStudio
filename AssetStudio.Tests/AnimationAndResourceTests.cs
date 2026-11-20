using System;
using System.IO;
using Xunit;

namespace AssetStudio.Tests
{
    [Collection("ResourceReaderXorHeuristic")]
    public class AnimationAndResourceTests
    {
        #region PackedIntVector Tests

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(8)]
        [InlineData(13)]
        [InlineData(16)]
        [InlineData(24)]
        [InlineData(31)]
        [InlineData(32)]
        public void PackedIntVector_UnpackInts_RoundTripsBitExactAcrossBitWidths(byte bitSize)
        {
            uint maxValue = bitSize == 0 ? 0u : (bitSize == 32 ? uint.MaxValue : (1u << bitSize) - 1u);
            uint[] rawValues = bitSize == 0
                ? new uint[] { 0, 0, 0, 0, 0 }
                : new uint[]
                {
                    0u,
                    1u & maxValue,
                    maxValue,
                    maxValue / 2u,
                    maxValue / 3u,
                    (0xA5A5A5A5u) & maxValue,
                    (0x5A5A5A5Au) & maxValue,
                    bitSize == 32 ? 0x80000000u : maxValue - (1u & maxValue),
                    bitSize == 32 ? 0x7FFFFFFFu : (maxValue >> 1)
                };

            var packed = new PackedIntVector
            {
                m_NumItems = (uint)rawValues.Length,
                m_BitSize = bitSize,
                m_Data = PackUnsignedBits(rawValues, bitSize)
            };

            var unpacked = packed.UnpackInts();

            Assert.Equal(rawValues.Length, unpacked.Length);
            for (int i = 0; i < rawValues.Length; i++)
            {
                Assert.Equal(unchecked((int)rawValues[i]), unpacked[i]);
            }
        }

        [Fact]
        public void PackedIntVector_UnpackInts_EmptyVector_ReturnsEmptyArray()
        {
            var emptyWithBuffer = new PackedIntVector
            {
                m_NumItems = 0,
                m_BitSize = 0,
                m_Data = Array.Empty<byte>()
            };
            Assert.Empty(emptyWithBuffer.UnpackInts());

            var emptyWithNullBuffer = new PackedIntVector
            {
                m_NumItems = 0,
                m_BitSize = 8,
                m_Data = null!
            };
            Assert.Empty(emptyWithNullBuffer.UnpackInts());
        }

        [Fact]
        public void PackedIntVector_UnpackInts_MalformedInputs_ThrowInvalidDataException()
        {
            // m_BitSize > 32
            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = 1,
                m_BitSize = 33,
                m_Data = new byte[16]
            }.UnpackInts());

            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = 0,
                m_BitSize = 64,
                m_Data = Array.Empty<byte>()
            }.UnpackInts());

            // Null m_Data with non-zero items
            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = 4,
                m_BitSize = 8,
                m_Data = null!
            }.UnpackInts());

            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = 4,
                m_BitSize = 0,
                m_Data = null!
            }.UnpackInts());

            // Truncated m_Data (10 items * 5 bits = 50 bits -> 7 bytes needed, only 6 provided)
            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = 10,
                m_BitSize = 5,
                m_Data = new byte[6]
            }.UnpackInts());

            // Oversized m_NumItems (must reject cleanly without OOM or OverflowException)
            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = uint.MaxValue,
                m_BitSize = 0,
                m_Data = Array.Empty<byte>()
            }.UnpackInts());

            Assert.Throws<InvalidDataException>(() => new PackedIntVector
            {
                m_NumItems = 100_000_000u,
                m_BitSize = 1,
                m_Data = new byte[16]
            }.UnpackInts());
        }

        #endregion

        #region PackedFloatVector Tests

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(8)]
        [InlineData(13)]
        [InlineData(16)]
        [InlineData(24)]
        [InlineData(31)]
        [InlineData(32)]
        public void PackedFloatVector_UnpackFloats_RoundTripsBitExactAcrossBitWidths(byte bitSize)
        {
            const float startValue = -12.5f;
            const float rangeValue = 25.0f;
            uint maxValue = bitSize == 0 ? 0u : (bitSize == 32 ? uint.MaxValue : (1u << bitSize) - 1u);

            uint[] rawValues = bitSize == 0
                ? new uint[] { 0, 0, 0, 0, 0, 0 }
                : new uint[]
                {
                    0u,
                    maxValue,
                    maxValue / 2u,
                    maxValue / 4u,
                    (maxValue / 4u) * 3u,
                    (0x12345678u) & maxValue
                };

            var packed = new PackedFloatVector
            {
                m_NumItems = (uint)rawValues.Length,
                m_Start = startValue,
                m_Range = bitSize == 0 ? 0.0f : rangeValue,
                m_BitSize = bitSize,
                m_Data = PackUnsignedBits(rawValues, bitSize)
            };

            var unpackedSingle = packed.UnpackFloats(itemCountInChunk: 1, chunkStride: 4);
            var unpackedChunks = packed.UnpackFloats(itemCountInChunk: 3, chunkStride: 12);

            Assert.Equal(rawValues.Length, unpackedSingle.Length);
            Assert.Equal(unpackedSingle, unpackedChunks);

            float scale = 1.0f / packed.m_Range;
            for (int i = 0; i < rawValues.Length; i++)
            {
                if (bitSize == 0)
                {
                    Assert.Equal(startValue, unpackedSingle[i]);
                    Assert.False(float.IsNaN(unpackedSingle[i]));
                }
                else
                {
                    float expected = rawValues[i] / (scale * maxValue) + startValue;
                    Assert.Equal(expected, unpackedSingle[i]);
                }
            }
        }

        [Fact]
        public void PackedFloatVector_UnpackFloats_ChunkedWithStartAndStride_UnpacksExactSlice()
        {
            const byte bitSize = 13;
            const uint maxValue = (1u << bitSize) - 1u;
            const float startValue = -4.0f;
            const float rangeValue = 8.0f;

            // 12 items: 6 UV0 floats followed by 6 UV1 floats (3 vertices * 2 components each)
            var rawValues = new uint[12];
            for (int i = 0; i < rawValues.Length; i++)
            {
                rawValues[i] = (uint)((i * 733 + 17) % (maxValue + 1));
            }

            var packed = new PackedFloatVector
            {
                m_NumItems = (uint)rawValues.Length,
                m_Start = startValue,
                m_Range = rangeValue,
                m_BitSize = bitSize,
                m_Data = PackUnsignedBits(rawValues, bitSize)
            };

            var all = packed.UnpackFloats(itemCountInChunk: 1, chunkStride: 4);
            // Non-byte-aligned bit start: start = 5 -> startBitPos = 65 (byte 8, bit 1)
            var middleSlice = packed.UnpackFloats(itemCountInChunk: 2, chunkStride: 16, start: 5, numChunks: 3);
            var secondHalf = packed.UnpackFloats(itemCountInChunk: 2, chunkStride: 8, start: 6, numChunks: 3);
            var defaultFromStart = packed.UnpackFloats(itemCountInChunk: 2, chunkStride: 8, start: 6);

            Assert.Equal(6, middleSlice.Length);
            Assert.Equal(all.AsSpan(5, 6).ToArray(), middleSlice);
            Assert.Equal(all.AsSpan(6, 6).ToArray(), secondHalf);
            Assert.Equal(secondHalf, defaultFromStart);
        }

        [Fact]
        public void PackedFloatVector_UnpackFloats_EmptyVectorAndZeroChunks_ReturnEmptyArray()
        {
            var empty = new PackedFloatVector
            {
                m_NumItems = 0,
                m_Start = 1.0f,
                m_Range = 2.0f,
                m_BitSize = 0,
                m_Data = Array.Empty<byte>()
            };

            Assert.Empty(empty.UnpackFloats(3, 12));
            Assert.Empty(empty.UnpackFloats(3, 12, start: 0, numChunks: 0));
        }

        [Fact]
        public void PackedFloatVector_UnpackFloats_InvalidChunkArguments_ThrowArgumentOutOfRangeException()
        {
            var valid = new PackedFloatVector
            {
                m_NumItems = 6,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 8,
                m_Data = new byte[6]
            };

            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 0, chunkStride: 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: -3, chunkStride: 12));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 3, chunkStride: -12));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 3, chunkStride: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 3, chunkStride: 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 3, chunkStride: 6));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 3, chunkStride: 12, start: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => valid.UnpackFloats(itemCountInChunk: 3, chunkStride: 12, start: 0, numChunks: -2));
        }

        [Fact]
        public void PackedFloatVector_UnpackFloats_MalformedDataAndOverflow_ThrowInvalidDataException()
        {
            // m_BitSize > 32
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = 4,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 33,
                m_Data = new byte[32]
            }.UnpackFloats(1, 4));

            // Null m_Data
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = 4,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 8,
                m_Data = null!
            }.UnpackFloats(1, 4));

            // Truncated m_Data (4 items * 13 bits = 52 bits -> 7 bytes needed, 6 provided)
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = 4,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 13,
                m_Data = new byte[6]
            }.UnpackFloats(1, 4));

            // Truncated with start offset
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = 10,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 8,
                m_Data = new byte[8]
            }.UnpackFloats(2, 8, start: 6, numChunks: 2));

            // Requested range exceeds m_NumItems
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = 4,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 8,
                m_Data = new byte[16]
            }.UnpackFloats(2, 8, start: 2, numChunks: 2));

            // Oversized m_NumItems (must not OOM or OverflowException)
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = uint.MaxValue,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 0,
                m_Data = Array.Empty<byte>()
            }.UnpackFloats(1, 4));

            // int overflow in numChunks * itemCountInChunk (100_000 * 100_000 = 10_000_000_000 > int.MaxValue)
            Assert.Throws<InvalidDataException>(() => new PackedFloatVector
            {
                m_NumItems = uint.MaxValue,
                m_Start = 0f,
                m_Range = 1f,
                m_BitSize = 8,
                m_Data = new byte[16]
            }.UnpackFloats(itemCountInChunk: 100_000, chunkStride: 400_000, start: 0, numChunks: 100_000));
        }

        #endregion

        #region PackedQuatVector Tests

        [Fact]
        public void PackedQuatVector_UnpackQuats_RoundTripsAllFlagConfigurationsAndClampsOvershoot()
        {
            // Pack 5 quaternions covering all 4 dropped components + negative sign bit + overshoot case
            var bytes = new byte[5 * 4];
            int indexPos = 0;
            int bitPos = 0;

            void WriteBits(uint value, int bitSize)
            {
                int written = 0;
                while (written < bitSize)
                {
                    int chunk = Math.Min(bitSize - written, 8 - bitPos);
                    uint slice = (value >> written) & ((1u << chunk) - 1u);
                    bytes[indexPos] |= (byte)(slice << bitPos);
                    bitPos += chunk;
                    written += chunk;
                    if (bitPos == 8)
                    {
                        indexPos++;
                        bitPos = 0;
                    }
                }
            }

            void PackQuaternion(Quaternion q, uint dropIndex)
            {
                uint signFlag = q[(int)dropIndex] < 0f ? 4u : 0u;
                uint flags = (dropIndex & 3u) | signFlag;
                WriteBits(flags, 3);
                for (int j = 0; j < 4; j++)
                {
                    if ((flags & 3u) != j)
                    {
                        int bitSize = ((int)(flags & 3u) + 1) % 4 == j ? 9 : 10;
                        uint maxVal = (1u << bitSize) - 1u;
                        uint encoded = (uint)Math.Clamp(Math.Round((q[j] + 1.0f) * 0.5f * maxVal), 0, maxVal);
                        WriteBits(encoded, bitSize);
                    }
                }
            }

            static Quaternion Normalize(float x, float y, float z, float w)
            {
                float invLen = 1.0f / (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                return new Quaternion(x * invLen, y * invLen, z * invLen, w * invLen);
            }

            var expected = new[]
            {
                Normalize(0.85f, 0.25f, -0.35f, 0.30f),  // drop X (0), positive
                Normalize(-0.20f, 0.88f, 0.30f, -0.30f), // drop Y (1), positive
                Normalize(0.30f, -0.25f, 0.85f, 0.35f),  // drop Z (2), positive
                Normalize(0.25f, -0.30f, 0.20f, -0.90f)  // drop W (3), negative
            };

            for (uint idx = 0; idx < 4; idx++)
            {
                PackQuaternion(expected[idx], idx);
            }

            // 5th quaternion: all stored components at max (+1) -> sum = 3 > 1, must clamp remaining to 0 (no NaN!)
            WriteBits(3u, 3);
            WriteBits(511u, 9);
            WriteBits(1023u, 10);
            WriteBits(1023u, 10);

            var packed = new PackedQuatVector
            {
                m_NumItems = 5,
                m_Data = bytes
            };

            var quats = packed.UnpackQuats();

            Assert.Equal(5, quats.Length);
            for (int i = 0; i < 4; i++)
            {
                Assert.InRange(Math.Abs(quats[i].X - expected[i].X), 0f, 0.005f);
                Assert.InRange(Math.Abs(quats[i].Y - expected[i].Y), 0f, 0.005f);
                Assert.InRange(Math.Abs(quats[i].Z - expected[i].Z), 0f, 0.005f);
                Assert.InRange(Math.Abs(quats[i].W - expected[i].W), 0f, 0.005f);
            }

            // Overshoot quaternion must clamp missing component to 0 without NaN
            Assert.False(float.IsNaN(quats[4].W));
            Assert.Equal(0f, quats[4].W);
        }

        [Fact]
        public void PackedQuatVector_UnpackQuats_EmptyAndMalformedInputs()
        {
            Assert.Empty(new PackedQuatVector { m_NumItems = 0, m_Data = Array.Empty<byte>() }.UnpackQuats());
            Assert.Empty(new PackedQuatVector { m_NumItems = 0, m_Data = null! }.UnpackQuats());

            Assert.Throws<InvalidDataException>(() => new PackedQuatVector
            {
                m_NumItems = 1,
                m_Data = null!
            }.UnpackQuats());

            // 2 quaternions require 8 bytes; 7 bytes must fail cleanly
            Assert.Throws<InvalidDataException>(() => new PackedQuatVector
            {
                m_NumItems = 2,
                m_Data = new byte[7]
            }.UnpackQuats());

            // Oversized m_NumItems
            Assert.Throws<InvalidDataException>(() => new PackedQuatVector
            {
                m_NumItems = uint.MaxValue,
                m_Data = new byte[16]
            }.UnpackQuats());
        }

        #endregion

        #region ResourceReader Opt-In XOR Heuristic Tests

        [Fact]
        public void ResourceReader_EnableXorInversionHeuristic_DefaultsToFalse()
        {
            Assert.False(ResourceReader.EnableXorInversionHeuristic);
        }

        [Fact]
        public void ResourceReader_DefaultDisabled_ReturnsHigh0x01PayloadUnmodified()
        {
            Assert.False(ResourceReader.EnableXorInversionHeuristic);

            var payload = new byte[128];
            Array.Fill(payload, (byte)0x01);
            payload[0] = 0x00;
            payload[1] = 0x02;
            payload[63] = 0x42;
            payload[127] = 0xFE;

            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms);
            var reader = new ResourceReader(br, 0, payload.Length);

            // GetData() must NOT invert bytes when heuristic is disabled
            var returned = reader.GetData();
            Assert.Equal(payload, returned);

            // GetData(byte[]) must NOT invert bytes and must leave trailing pool bytes untouched
            var poolBuffer = new byte[256];
            Array.Fill(poolBuffer, (byte)0x55);
            reader.GetData(poolBuffer);

            Assert.Equal(payload, poolBuffer.AsSpan(0, payload.Length).ToArray());
            for (int i = payload.Length; i < poolBuffer.Length; i++)
            {
                Assert.Equal(0x55, poolBuffer[i]);
            }
        }

        [Fact]
        public void ResourceReader_OptInEnabled_XorInvertsHigh0x01PayloadWithoutTouchingTrailingBytes()
        {
            // Payload > 1024 bytes: >50% of first 1024 bytes are 0x01, remainder has arbitrary bytes
            var payload = new byte[1536];
            for (int i = 0; i < 600; i++)
            {
                payload[i] = 0x01;
            }
            for (int i = 600; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 13 + 7) & 0xFF);
            }

            var expectedInverted = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                expectedInverted[i] = (byte)(payload[i] ^ 0xFF);
            }

            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms);
            var reader = new ResourceReader(br, 0, payload.Length);

            ResourceReader.EnableXorInversionHeuristic = true;
            try
            {
                // GetData()
                var returned = reader.GetData();
                Assert.Equal(expectedInverted, returned);

                // GetData(byte[]) with oversized buffer
                var poolBuffer = new byte[payload.Length + 128];
                Array.Fill(poolBuffer, (byte)0xA5);
                reader.GetData(poolBuffer);

                Assert.Equal(expectedInverted, poolBuffer.AsSpan(0, payload.Length).ToArray());
                for (int i = payload.Length; i < poolBuffer.Length; i++)
                {
                    Assert.Equal(0xA5, poolBuffer[i]);
                }
            }
            finally
            {
                ResourceReader.EnableXorInversionHeuristic = false;
            }
        }

        [Fact]
        public void ResourceReader_OptInEnabled_RespectsStrictFiftyPercentThreshold()
        {
            // Exactly 50% (50 of 100) 0x01 bytes -> must NOT invert
            var atThreshold = new byte[100];
            Array.Fill(atThreshold, (byte)0x01, 0, 50);
            Array.Fill(atThreshold, (byte)0x20, 50, 50);

            // Strictly >50% (51 of 100) 0x01 bytes -> MUST invert
            var aboveThreshold = new byte[100];
            Array.Fill(aboveThreshold, (byte)0x01, 0, 51);
            Array.Fill(aboveThreshold, (byte)0x20, 51, 49);

            ResourceReader.EnableXorInversionHeuristic = true;
            try
            {
                using (var ms = new MemoryStream(atThreshold))
                using (var br = new BinaryReader(ms))
                {
                    var reader = new ResourceReader(br, 0, atThreshold.Length);
                    Assert.Equal(atThreshold, reader.GetData());
                }

                using (var ms = new MemoryStream(aboveThreshold))
                using (var br = new BinaryReader(ms))
                {
                    var reader = new ResourceReader(br, 0, aboveThreshold.Length);
                    var actual = reader.GetData();
                    for (int i = 0; i < aboveThreshold.Length; i++)
                    {
                        Assert.Equal((byte)(aboveThreshold[i] ^ 0xFF), actual[i]);
                    }
                }
            }
            finally
            {
                ResourceReader.EnableXorInversionHeuristic = false;
            }
        }

        #endregion

        private static byte[] PackUnsignedBits(uint[] values, int bitSize)
        {
            if (bitSize == 0 || values.Length == 0)
            {
                return Array.Empty<byte>();
            }

            long totalBits = (long)values.Length * bitSize;
            var bytes = new byte[(totalBits + 7) / 8];
            int indexPos = 0;
            int bitPos = 0;

            foreach (uint raw in values)
            {
                int bitsWritten = 0;
                while (bitsWritten < bitSize)
                {
                    int chunk = Math.Min(bitSize - bitsWritten, 8 - bitPos);
                    uint sliceMask = (1u << chunk) - 1u;
                    uint slice = (raw >> bitsWritten) & sliceMask;
                    bytes[indexPos] |= (byte)(slice << bitPos);
                    bitPos += chunk;
                    bitsWritten += chunk;
                    if (bitPos == 8)
                    {
                        indexPos++;
                        bitPos = 0;
                    }
                }
            }

            return bytes;
        }
    }
}
