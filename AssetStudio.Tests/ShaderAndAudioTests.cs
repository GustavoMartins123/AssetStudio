using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Smolv;
using SpirV;
using Xunit;

namespace AssetStudio.Tests
{
    public class ShaderAndAudioTests
    {
        [Fact]
        public void SpirVModule_ReadsFloat32ConstantFromCorrectWordIndex_RegressionREL04()
        {
            // Minimal valid SPIR-V module containing:
            //   %1 = OpTypeFloat 32
            //   %2 = OpConstant %1 3.1415927f (IEEE 754 bits 0x40490FDB)
            //   %3 = OpTypeInt 32 1 (signed 32-bit int)
            //   %4 = OpConstant %3 -42
            const float expectedFloat = 3.1415927f;
            const uint expectedFloatBits = 0x40490FDBu;
            Assert.Equal(expectedFloatBits, BitConverter.SingleToUInt32Bits(expectedFloat));

            const int expectedInt = -42;

            uint[] words =
            {
                // Header
                0x07230203u, // SPIR-V magic
                0x00010000u, // Version 1.0
                0x00080001u, // Generator: Khronos Glslang v1
                5u,          // Bound
                0u,          // Reserved schema

                // OpTypeFloat (opcode 22, wordCount 3): %1 = OpTypeFloat 32
                (3u << 16) | 22u,
                1u,
                32u,

                // OpConstant (opcode 43, wordCount 4): %2 = OpConstant %1 0x40490FDB
                (4u << 16) | 43u,
                1u,
                2u,
                expectedFloatBits,

                // OpTypeInt (opcode 21, wordCount 4): %3 = OpTypeInt 32 1
                (4u << 16) | 21u,
                3u,
                32u,
                1u,

                // OpConstant (opcode 43, wordCount 4): %4 = OpConstant %3 -42
                (4u << 16) | 43u,
                3u,
                4u,
                unchecked((uint)expectedInt)
            };

            var bytes = new byte[words.Length * sizeof(uint)];
            for (int i = 0; i < words.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), words[i]);
            }

            using var ms = new MemoryStream(bytes);
            var module = Module.ReadFrom(ms);

            var floatConst = module.Instructions.Single(instr => instr.ResultId == 2u);
            Assert.IsType<OpConstant>(floatConst.Instruction);
            float parsedFloat = Assert.IsType<float>(floatConst.Value);
            Assert.Equal(expectedFloat, parsedFloat);
            Assert.Equal(expectedFloat, Assert.IsType<float>(floatConst.Operands[2].Value));

            var intConst = module.Instructions.Single(instr => instr.ResultId == 4u);
            Assert.IsType<OpConstant>(intConst.Instruction);
            Assert.Equal(expectedInt, Assert.IsType<int>(intConst.Value));

            var disassembly = new Disassembler().Disassemble(module);
            Assert.Contains("OpConstant", disassembly);
            Assert.Contains(expectedFloat.ToString(), disassembly);
            Assert.Contains(expectedInt.ToString(), disassembly);
        }

        [Fact]
        public void SmolvDecoder_HandlesTruncatedAndMalformedInputGracefully()
        {
            // Empty and short inputs (< 24-byte header)
            Assert.Null(SmolvDecoder.Decode(Array.Empty<byte>()));
            Assert.Null(SmolvDecoder.Decode(new byte[4]));
            Assert.Null(SmolvDecoder.Decode(new byte[23]));
            Assert.False(SmolvDecoder.Decode(new byte[10], new byte[64]));

            // Invalid magic / unsupported version
            var badVersion = BuildSmolvHeader(SmolvDecoder.SmolHeaderMagic, version: 0x00020000u, decodedSize: 64);
            Assert.Equal(0, SmolvDecoder.GetDecodedBufferSize(badVersion));
            Assert.Null(SmolvDecoder.Decode(badVersion));

            // Truncated unterminated varint (all 0x80 bytes) — must return false without hanging
            var unterminatedVarint = BuildSmolvHeader(SmolvDecoder.SmolHeaderMagic, 0x00010000u, decodedSize: 64,
                0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Assert.Null(SmolvDecoder.Decode(unterminatedVarint));
            Assert.False(SmolvDecoder.Decode(unterminatedVarint, new byte[64]));

            // Truncated instruction operands hitting EOF mid-instruction
            var truncatedOp = BuildSmolvHeader(SmolvDecoder.SmolHeaderMagic, 0x00010000u, decodedSize: 64,
                0x45, 0x01); // OpName-like word requiring extra operands, truncated immediately
            Assert.Null(SmolvDecoder.Decode(truncatedOp));

            // Stream overload with inputSize exceeding actual stream length
            using var inMs = new MemoryStream(unterminatedVarint);
            using var outMs = new MemoryStream();
            Assert.False(SmolvDecoder.Decode(inMs, inputSize: 100000, outMs));
        }

        [Fact]
        public void UnityAudioDecoder_RejectsMalformedFsb5HeadersSafely_RegressionSEC09()
        {
            // Null, empty, truncated, and non-FSB5 inputs
            Assert.Null(UnityAudioDecoder.ConvertToWav(null!));
            Assert.Null(UnityAudioDecoder.ConvertToWav(Array.Empty<byte>()));
            Assert.Null(UnityAudioDecoder.ConvertToWav(new byte[32]));
            Assert.Null(UnityAudioDecoder.ConvertToWav(Encoding.ASCII.GetBytes("NOT_FSB5" + new string('\0', 64))));

            // 1) NumChannels == 0 via CHANNELS chunk override (type 1, size 1, data = [0])
            {
                var channelsChunk = BuildFsb5Chunk(chunkType: 1u, moreChunks: false, payload: new byte[] { 0x00 });
                var audioPayload = new byte[140];
                var fsb = BuildFsb5Container(
                    codec: UnityAudioCodec.Fadpcm,
                    numSamples: 1,
                    sampleHeaderExtraChunks: channelsChunk,
                    sizeOfData: (uint)audioPayload.Length,
                    sampleMetaRaw: PackSampleMeta(hasChunks: true, freqId: 8u, chBits: 0u, dataOffsetBlocks32: 0u, sampleCount: 256u),
                    audioData: audioPayload);

                Assert.Null(UnityAudioDecoder.ConvertToWav(fsb));
            }

            // 2) DataOffset > sizeOfData underflow
            {
                var audioPayload = new byte[64];
                var fsb = BuildFsb5Container(
                    codec: UnityAudioCodec.Pcm16,
                    numSamples: 1,
                    sampleHeaderExtraChunks: Array.Empty<byte>(),
                    sizeOfData: (uint)audioPayload.Length,
                    sampleMetaRaw: PackSampleMeta(hasChunks: false, freqId: 8u, chBits: 0u, dataOffsetBlocks32: 32u, sampleCount: 16u), // DataOffset = 1024 > 64
                    audioData: audioPayload);

                Assert.Null(UnityAudioDecoder.ConvertToWav(fsb));
            }

            // 3) Huge numSamples claiming millions of samples in a tiny buffer
            {
                var fsb = BuildFsb5Container(
                    codec: UnityAudioCodec.Pcm16,
                    numSamples: 0x40000000u,
                    sampleHeaderExtraChunks: Array.Empty<byte>(),
                    sizeOfData: 32u,
                    sampleMetaRaw: PackSampleMeta(hasChunks: false, freqId: 8u, chBits: 0u, dataOffsetBlocks32: 0u, sampleCount: 16u),
                    audioData: new byte[32]);

                Assert.Null(UnityAudioDecoder.ConvertToWav(fsb));
            }

            // 4) Huge SampleCount in PCM16 and IMA-ADPCM headers with tiny audio payload (OOM guard)
            {
                var fsbPcm = BuildFsb5Container(
                    codec: UnityAudioCodec.Pcm16,
                    numSamples: 1,
                    sampleHeaderExtraChunks: Array.Empty<byte>(),
                    sizeOfData: 32u,
                    sampleMetaRaw: PackSampleMeta(hasChunks: false, freqId: 8u, chBits: 1u, dataOffsetBlocks32: 0u, sampleCount: 0x3FFFFFFFu),
                    audioData: new byte[32]);

                Assert.Null(UnityAudioDecoder.ConvertToWav(fsbPcm));

                var fsbIma = BuildFsb5Container(
                    codec: UnityAudioCodec.ImaAdpcm,
                    numSamples: 1,
                    sampleHeaderExtraChunks: Array.Empty<byte>(),
                    sizeOfData: 36u,
                    sampleMetaRaw: PackSampleMeta(hasChunks: false, freqId: 8u, chBits: 0u, dataOffsetBlocks32: 0u, sampleCount: 0x3FFFFFFFu),
                    audioData: new byte[36]);

                Assert.Null(UnityAudioDecoder.ConvertToWav(fsbIma));
            }

            // 5) Truncated chunk claiming more bytes than remain in sampleHeaders
            {
                var truncatedChunkHeader = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(truncatedChunkHeader, (1u << 25) | (99999u << 1));
                var fsb = BuildFsb5Container(
                    codec: UnityAudioCodec.Pcm16,
                    numSamples: 1,
                    sampleHeaderExtraChunks: truncatedChunkHeader,
                    sizeOfData: 32u,
                    sampleMetaRaw: PackSampleMeta(hasChunks: true, freqId: 8u, chBits: 0u, dataOffsetBlocks32: 0u, sampleCount: 8u),
                    audioData: new byte[32]);

                Assert.Null(UnityAudioDecoder.ConvertToWav(fsb));
            }
        }

        [Fact]
        public void UnityAudioDecoder_DecodesValidPcm16Fsb5ToWav()
        {
            short[] pcm = { 0, 1000, -1000, 32767 };
            var audioPayload = new byte[pcm.Length * 2];
            for (int i = 0; i < pcm.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(audioPayload.AsSpan(i * 2, 2), pcm[i]);
            }

            var fsb = BuildFsb5Container(
                codec: UnityAudioCodec.Pcm16,
                numSamples: 1,
                sampleHeaderExtraChunks: Array.Empty<byte>(),
                sizeOfData: (uint)audioPayload.Length,
                sampleMetaRaw: PackSampleMeta(hasChunks: false, freqId: 8u, chBits: 0u, dataOffsetBlocks32: 0u, sampleCount: (uint)pcm.Length),
                audioData: audioPayload);

            var wav = UnityAudioDecoder.ConvertToWav(fsb);
            Assert.NotNull(wav);
            Assert.Equal(44 + audioPayload.Length, wav!.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2)));     // mono
            Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4))); // 44.1 kHz
            Assert.Equal(audioPayload, wav.AsSpan(44).ToArray());
        }

        private static byte[] BuildSmolvHeader(uint magic, uint version, int decodedSize, params byte[] body)
        {
            var data = new byte[24 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), magic);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0u);  // generator
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 16); // bound
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0u); // schema
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20, 4), decodedSize);
            Buffer.BlockCopy(body, 0, data, 24, body.Length);
            return data;
        }

        private static ulong PackSampleMeta(bool hasChunks, uint freqId, uint chBits, uint dataOffsetBlocks32, uint sampleCount)
        {
            return (hasChunks ? 1UL : 0UL)
                | ((ulong)(freqId & 0xFu) << 1)
                | ((ulong)(chBits & 0x3u) << 5)
                | ((ulong)(dataOffsetBlocks32 & 0x7FFFFFFu) << 7)
                | ((ulong)(sampleCount & 0x3FFFFFFFu) << 34);
        }

        private static byte[] BuildFsb5Chunk(uint chunkType, bool moreChunks, byte[] payload)
        {
            var chunk = new byte[4 + payload.Length];
            uint header = (moreChunks ? 1u : 0u) | (((uint)payload.Length & 0xFFFFFFu) << 1) | ((chunkType & 0x7Fu) << 25);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0, 4), header);
            Buffer.BlockCopy(payload, 0, chunk, 4, payload.Length);
            return chunk;
        }

        private static byte[] BuildFsb5Container(
            UnityAudioCodec codec,
            uint numSamples,
            byte[] sampleHeaderExtraChunks,
            uint sizeOfData,
            ulong sampleMetaRaw,
            byte[] audioData)
        {
            const uint headerSize = 60u; // FSB5 v1
            uint sizeOfSampleHeaders = (uint)(8 + sampleHeaderExtraChunks.Length);
            var container = new byte[headerSize + sizeOfSampleHeaders + audioData.Length];

            Encoding.ASCII.GetBytes("FSB5").CopyTo(container, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(4, 4), 1u); // version 1
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(8, 4), numSamples);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(12, 4), sizeOfSampleHeaders);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(16, 4), 0u); // sizeOfNameTable
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(20, 4), sizeOfData);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(24, 4), (uint)codec);

            BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan((int)headerSize, 8), sampleMetaRaw);
            Buffer.BlockCopy(sampleHeaderExtraChunks, 0, container, (int)headerSize + 8, sampleHeaderExtraChunks.Length);
            Buffer.BlockCopy(audioData, 0, container, (int)(headerSize + sizeOfSampleHeaders), audioData.Length);
            return container;
        }
    }
}
