#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Xunit;

namespace AssetStudio.Tests
{
    public class Fsb5VorbisDecoderTests
    {
        private const uint KnownTwoModeCrc32 = 0xC04A00F0u; // Used in AnimaGateOfMemories_Data (2 modes: 256/2048)

        [Fact]
        public void Fsb5VorbisCodebookTable_LoadsAll161CanonicalHeadersWithMatchingCrc32AndVorbisPrefix()
        {
            Assert.Equal(161, Fsb5VorbisCodebookTable.Count);
            Assert.False(Fsb5VorbisCodebookTable.TryGetSetupHeader(0xDEADBEEFu, out _));

            int singleModeCount = 0;
            int twoModeCount = 0;

            foreach (uint crc in Fsb5VorbisCodebookTable.KnownCrc32Keys)
            {
                Assert.True(Fsb5VorbisCodebookTable.TryGetSetupHeader(crc, out var header));
                Assert.NotNull(header.SetupPacket);
                Assert.True(header.SetupPacket.Length > 7);
                Assert.Equal((byte)5, header.SetupPacket[0]);
                Assert.Equal("vorbis", Encoding.ASCII.GetString(header.SetupPacket, 1, 6));
                Assert.Equal(crc, ComputeIeeeCrc32(header.SetupPacket));
                Assert.InRange(header.ModeCount, (byte)1, (byte)8);

                if (header.ModeCount == 1) singleModeCount++;
                else if (header.ModeCount == 2) twoModeCount++;
            }

            Assert.Equal(7, singleModeCount);
            Assert.Equal(154, twoModeCount);
        }

        [Fact]
        public void Fsb5VorbisReconstructor_ComputeOggCrc32_MatchesRfc3533Vector()
        {
            // Minimal valid OggS page with 1-byte body [0x01], zeroed CRC field at bytes 22..25
            byte[] page = new byte[29];
            Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
            page[4] = 0;    // version
            page[5] = 0x02; // BOS
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), 0u);
            page[26] = 1;   // 1 segment
            page[27] = 1;   // segment length 1
            page[28] = 0x01;

            // Independent bit-by-bit MSB-first CRC32 (poly 0x04C11DB7, init 0, xorout 0)
            uint expected = ComputeOggCrcBitwise(page);
            uint actual = Fsb5VorbisReconstructor.ComputeOggCrc32(page);
            Assert.Equal(expected, actual);
            Assert.NotEqual(0u, actual);
        }

        [Fact]
        public void Fsb5VorbisReconstructor_GetPacketBlockSize_HandlesModesAndNonAudioPackets()
        {
            Assert.Equal(0, Fsb5VorbisReconstructor.GetPacketBlockSize(ReadOnlySpan<byte>.Empty, 2, 0b10));
            // Non-audio packet: bit 0 == 1
            Assert.Equal(0, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0x01 }, 2, 0b10));
            Assert.Equal(0, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0x03 }, 2, 0b10));

            // 2-mode (mode 0 = short 256, mode 1 = long 2048)
            Assert.Equal(256, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0000 }, 2, 0b10));
            Assert.Equal(2048, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0010 }, 2, 0b10));

            // 1-mode (0 mode bits; always mode 0 = short 256 even if bit 1 is set)
            Assert.Equal(256, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0010 }, 1, 0b00));

            // 4-mode (2 mode bits: bits 1..2; modes 1 and 3 are long)
            Assert.Equal(256, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0000 }, 4, 0b1010));
            Assert.Equal(2048, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0010 }, 4, 0b1010));
            Assert.Equal(256, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0100 }, 4, 0b1010));
            Assert.Equal(2048, Fsb5VorbisReconstructor.GetPacketBlockSize(new byte[] { 0b0000_0110 }, 4, 0b1010));
        }

        [Fact]
        public void UnityAudioDecoder_ConvertToOgg_EmitsValidOggPagesHeadersLoopCommentsAndGranulePositions()
        {
            Assert.True(Fsb5VorbisCodebookTable.TryGetSetupHeader(KnownTwoModeCrc32, out var expectedSetup));

            // Build 4 length-prefixed Vorbis audio packets:
            // pkt0: short (256)  -> granule = 0
            // pkt1: long  (2048) -> granule = (256 + 2048)/4 = 576
            // pkt2: long  (2048) -> granule = 576 + (2048 + 2048)/4 = 1600
            // pkt3: long  (2048) -> raw = 2624, clamped to SampleCount = 2000
            var packets = new[]
            {
                new byte[] { 0x00, 0xAA, 0xBB },       // mode 0 (short)
                new byte[] { 0x02, 0x11, 0x22, 0x33 }, // mode 1 (long)
                new byte[] { 0x02, 0x44, 0x55 },       // mode 1 (long)
                new byte[] { 0x02, 0x66, 0x77, 0x88 }, // mode 1 (long)
            };
            byte[] payload = PackLengthPrefixedPackets(packets, padTo32: true);

            byte[] vorbisChunkPayload = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(vorbisChunkPayload.AsSpan(0, 4), KnownTwoModeCrc32);
            byte[] loopChunkPayload = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(loopChunkPayload.AsSpan(0, 4), 128u);
            BinaryPrimitives.WriteUInt32LittleEndian(loopChunkPayload.AsSpan(4, 4), 1024u);

            byte[] chunks = ConcatBytes(
                BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeLoop, moreChunks: true, loopChunkPayload),
                BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, moreChunks: false, vorbisChunkPayload));

            byte[] fsb = BuildFsb5SingleSample(
                UnityAudioCodec.Vorbis,
                freqId: 8u, // 44100 Hz
                chBits: 1u, // stereo (2 ch)
                sampleCount: 2000u,
                chunks: chunks,
                audioData: payload);

            Assert.Equal(UnityAudioCodec.Vorbis, UnityAudioDecoder.DetectFsb5Codec(fsb));
            Assert.Null(UnityAudioDecoder.ConvertToWav(fsb));

            byte[]? ogg = UnityAudioDecoder.ConvertToOgg(fsb);
            Assert.NotNull(ogg);
            Assert.True(UnityAudioDecoder.TryDecode(fsb, 0, out var decoded, out var ext));
            Assert.Equal(".ogg", ext);
            Assert.Equal(ogg, decoded);

            var parsedPages = ParseAndValidateOggPages(ogg!);
            Assert.True(parsedPages.Count >= 3);

            // Page 0: BOS only, granule 0, contains ONLY Identification packet (30 bytes)
            Assert.Equal(0x02, parsedPages[0].HeaderType);
            Assert.Equal(0L, parsedPages[0].GranulePosition);
            Assert.Equal(0u, parsedPages[0].PageSequence);
            Assert.Single(parsedPages[0].CompletedPackets);
            byte[] ident = parsedPages[0].CompletedPackets[0];
            Assert.Equal(30, ident.Length);
            Assert.Equal((byte)1, ident[0]);
            Assert.Equal("vorbis", Encoding.ASCII.GetString(ident, 1, 6));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ident.AsSpan(7, 4)));
            Assert.Equal((byte)2, ident[11]); // stereo
            Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(ident.AsSpan(12, 4)));
            Assert.Equal((byte)0xB8, ident[28]); // blocksize_0=256, blocksize_1=2048
            Assert.Equal((byte)1, ident[29]);    // framing flag

            // Reassemble all logical packets across the entire stream
            var allPackets = ExtractAllOggPackets(ogg!);
            Assert.Equal(3 + packets.Length, allPackets.Count);

            // Packet 1: Comment header with LOOPSTART=128 and LOOPEND=1024
            byte[] comment = allPackets[1];
            Assert.Equal((byte)3, comment[0]);
            Assert.Equal("vorbis", Encoding.ASCII.GetString(comment, 1, 6));
            string commentText = Encoding.UTF8.GetString(comment);
            Assert.Contains("AssetStudio", commentText);
            Assert.Contains("LOOPSTART=128", commentText);
            Assert.Contains("LOOPEND=1024", commentText);
            Assert.Equal((byte)1, comment[^1]);

            // Packet 2: Exact setup packet from codebook table
            Assert.Equal(expectedSetup.SetupPacket, allPackets[2]);

            // Packets 3..6: Exact audio packets preserved byte-for-byte
            for (int i = 0; i < packets.Length; i++)
            {
                Assert.Equal(packets[i], allPackets[3 + i]);
            }

            // Last page has EOS flag (0x04) and final granule position clamped to SampleCount (2000)
            var lastPage = parsedPages[^1];
            Assert.NotEqual(0, lastPage.HeaderType & 0x04);
            Assert.Equal(2000L, lastPage.GranulePosition);
            for (int i = 0; i < parsedPages.Count - 1; i++)
            {
                Assert.Equal(0, parsedPages[i].HeaderType & 0x04);
            }
        }

        [Fact]
        public void UnityAudioDecoder_ConvertToOgg_SupportsMultiSubsoundAndChunkOverrides()
        {
            uint singleModeCrc = Fsb5VorbisCodebookTable.KnownCrc32Keys
                .First(crc => Fsb5VorbisCodebookTable.TryGetSetupHeader(crc, out var h) && h.ModeCount == 1);

            byte[] sub0Payload = PackLengthPrefixedPackets(new[]
            {
                new byte[] { 0x00, 0x01, 0x02 },
                new byte[] { 0x02, 0x03, 0x04 },
            }, padTo32: true);

            byte[] sub1Payload = PackLengthPrefixedPackets(new[]
            {
                new byte[] { 0x00, 0x10 },
                new byte[] { 0x02, 0x20, 0x30 },
            }, padTo32: true);

            byte[] freq0 = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(freq0, 22050u);
            byte[] vorbis0 = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(vorbis0, singleModeCrc);
            byte[] chunks0 = ConcatBytes(
                BuildFsb5Chunk(2u, moreChunks: true, freq0),
                BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, moreChunks: false, vorbis0));

            byte[] ch1 = new byte[] { 4 }; // 4 channels via CHANNELS chunk
            byte[] freq1 = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(freq1, 48000u);
            byte[] vorbis1 = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(vorbis1, KnownTwoModeCrc32);
            byte[] chunks1 = ConcatBytes(
                BuildFsb5Chunk(1u, moreChunks: true, ch1),
                BuildFsb5Chunk(2u, moreChunks: true, freq1),
                BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, moreChunks: false, vorbis1));

            byte[] fsb = BuildFsb5MultiSample(
                UnityAudioCodec.Vorbis,
                (freqId: 0u, chBits: 0u, sampleCount: 128u, chunks: chunks0, audioData: sub0Payload),
                (freqId: 0u, chBits: 0u, sampleCount: 576u, chunks: chunks1, audioData: sub1Payload));

            byte[]? ogg0 = UnityAudioDecoder.ConvertToOgg(fsb, subsoundIndex: 0);
            byte[]? ogg1 = UnityAudioDecoder.ConvertToOgg(fsb, subsoundIndex: 1);
            Assert.NotNull(ogg0);
            Assert.NotNull(ogg1);
            Assert.Null(UnityAudioDecoder.ConvertToOgg(fsb, subsoundIndex: -1));
            Assert.Null(UnityAudioDecoder.ConvertToOgg(fsb, subsoundIndex: 2));

            var ident0 = ExtractAllOggPackets(ogg0!)[0];
            Assert.Equal((byte)1, ident0[11]);
            Assert.Equal(22050, BinaryPrimitives.ReadInt32LittleEndian(ident0.AsSpan(12, 4)));

            var ident1 = ExtractAllOggPackets(ogg1!)[0];
            Assert.Equal((byte)4, ident1[11]);
            Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(ident1.AsSpan(12, 4)));

            // Verify single-mode granule calculation on sub0: (256+256)/4 = 128
            Assert.Equal(128L, ParseAndValidateOggPages(ogg0!)[^1].GranulePosition);
            // Verify two-mode granule calculation on sub1: (256+2048)/4 = 576
            Assert.Equal(576L, ParseAndValidateOggPages(ogg1!)[^1].GranulePosition);
        }

        [Fact]
        public void UnityAudioDecoder_ConvertToOgg_HandlesExact255MultipleAndMultiPageSpanningPackets()
        {
            // Packet 0: exact multiple of 255 bytes (255 bytes -> lacing [255, 0])
            var pkt255 = new byte[255];
            pkt255[0] = 0x00; // audio, short block
            pkt255[254] = 0x7E;

            // Packet 1: > 255 * 255 = 65025 bytes (65500 bytes -> must span pages with continued=0x01 and granule=-1 on intermediate page)
            var pktHuge = new byte[65500];
            pktHuge[0] = 0x02; // audio, long block
            pktHuge[65499] = 0x5A;

            byte[] payload = PackLengthPrefixedPackets(new[] { pkt255, pktHuge }, padTo32: true);
            byte[] vorbisChunk = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(vorbisChunk, KnownTwoModeCrc32);
            byte[] fsb = BuildFsb5SingleSample(
                UnityAudioCodec.Vorbis,
                freqId: 8u,
                chBits: 0u,
                sampleCount: 576u,
                chunks: BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, false, vorbisChunk),
                audioData: payload);

            byte[]? ogg = UnityAudioDecoder.ConvertToOgg(fsb);
            Assert.NotNull(ogg);

            var pages = ParseAndValidateOggPages(ogg!);
            // At least one page must have continued flag (0x01) because pktHuge exceeds 255 segments
            Assert.Contains(pages, p => (p.HeaderType & 0x01) != 0);
            // Any page where no packet finishes must carry granule_position = -1
            foreach (var p in pages)
            {
                if (p.CompletedPackets.Count == 0)
                {
                    Assert.Equal(-1L, p.GranulePosition);
                }
            }

            var allPackets = ExtractAllOggPackets(ogg!);
            Assert.Equal(5, allPackets.Count); // Ident + Comment + Setup + 2 audio packets
            Assert.Equal(pkt255, allPackets[3]);
            Assert.Equal(pktHuge, allPackets[4]);
            Assert.Equal(576L, pages[^1].GranulePosition);
            Assert.NotEqual(0, pages[^1].HeaderType & 0x04);

            // Also verify 260 tiny packets (crossing 255-segment limit cleanly at packet boundary):
            // NO page may set continued=0x01 because every packet is < 255 bytes and finishes on its own page!
            var tinyPackets = Enumerable.Range(0, 260)
                .Select(i => new byte[] { 0x00, (byte)(i & 0xFF) })
                .ToArray();
            byte[] tinyPayload = PackLengthPrefixedPackets(tinyPackets, padTo32: true);
            byte[] tinyFsb = BuildFsb5SingleSample(
                UnityAudioCodec.Vorbis,
                freqId: 8u,
                chBits: 0u,
                sampleCount: 0u,
                chunks: BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, false, vorbisChunk),
                audioData: tinyPayload);

            byte[]? tinyOgg = UnityAudioDecoder.ConvertToOgg(tinyFsb);
            Assert.NotNull(tinyOgg);
            var tinyPages = ParseAndValidateOggPages(tinyOgg!);
            Assert.All(tinyPages, p => Assert.Equal(0, p.HeaderType & 0x01));
            Assert.Equal(3 + 260, ExtractAllOggPackets(tinyOgg!).Count);
        }

        [Fact]
        public void UnityAudioDecoder_ConvertToOgg_RejectsMalformedAndUnsupportedInputsSafely()
        {
            Assert.Null(UnityAudioDecoder.ConvertToOgg(null!));
            Assert.Null(UnityAudioDecoder.ConvertToOgg(Array.Empty<byte>()));
            Assert.Null(UnityAudioDecoder.ConvertToOgg(Encoding.ASCII.GetBytes("NOT_FSB5" + new string('\0', 64))));

            // Non-Vorbis codec (PCM16)
            byte[] pcmFsb = BuildFsb5SingleSample(UnityAudioCodec.Pcm16, 8u, 0u, 4u, Array.Empty<byte>(), new byte[8]);
            Assert.Null(UnityAudioDecoder.ConvertToOgg(pcmFsb));

            // Missing VORBISDATA chunk
            byte[] validPayload = PackLengthPrefixedPackets(new[] { new byte[] { 0x00, 0x01 } }, padTo32: true);
            byte[] noChunkFsb = BuildFsb5SingleSample(UnityAudioCodec.Vorbis, 8u, 1u, 128u, Array.Empty<byte>(), validPayload);
            Assert.Null(UnityAudioDecoder.ConvertToOgg(noChunkFsb));

            // Unknown CRC32 in VORBISDATA chunk
            byte[] unknownCrcPayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(unknownCrcPayload, 0x12345678u);
            byte[] unknownChunk = BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, false, unknownCrcPayload);
            byte[] unknownCrcFsb = BuildFsb5SingleSample(UnityAudioCodec.Vorbis, 8u, 1u, 128u, unknownChunk, validPayload);
            Assert.Null(UnityAudioDecoder.ConvertToOgg(unknownCrcFsb));

            // Known CRC32 but empty/EOS-only payload (0x0000 and 0xFFFF) and truncated oversized packet length
            byte[] knownCrcPayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(knownCrcPayload, KnownTwoModeCrc32);
            byte[] knownChunk = BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, false, knownCrcPayload);

            Assert.Null(UnityAudioDecoder.ConvertToOgg(BuildFsb5SingleSample(UnityAudioCodec.Vorbis, 8u, 1u, 128u, knownChunk, new byte[32])));
            Assert.Null(UnityAudioDecoder.ConvertToOgg(BuildFsb5SingleSample(UnityAudioCodec.Vorbis, 8u, 1u, 128u, knownChunk, new byte[] { 0xFF, 0xFF, 0x00, 0x00 })));
            Assert.Null(UnityAudioDecoder.ConvertToOgg(BuildFsb5SingleSample(UnityAudioCodec.Vorbis, 8u, 1u, 128u, knownChunk, new byte[] { 0xFE, 0xFF, 0x01, 0x02 })));
        }

        [Fact]
        public void AudioPreviewPreparer_And_AssetExportService_HandleFsb5VorbisReconstructionAndExport()
        {
            byte[] payload = PackLengthPrefixedPackets(new[]
            {
                new byte[] { 0x00, 0x11, 0x22 },
                new byte[] { 0x02, 0x33, 0x44, 0x55 },
            }, padTo32: true);

            byte[] vorbisChunk = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(vorbisChunk, KnownTwoModeCrc32);
            byte[] fsb = BuildFsb5SingleSample(
                UnityAudioCodec.Vorbis,
                freqId: 8u,
                chBits: 1u,
                sampleCount: 576u,
                chunks: BuildFsb5Chunk(Fsb5VorbisReconstructor.ChunkTypeVorbisData, false, vorbisChunk),
                audioData: payload);

            // 1) AnalyzeAudioData produces .ogg directly from FSB5 Vorbis
            var analysis = AudioPreviewPreparer.AnalyzeAudioData(fsb, ".fsb", metadataDurationMs: 13);
            Assert.True(analysis.DecodedFromFsb);
            Assert.Equal(".ogg", analysis.Extension);
            Assert.NotNull(analysis.RebuiltData);
            Assert.Equal("OggS", Encoding.ASCII.GetString(analysis.RebuiltData!, 0, 4));

            var tempDir = Path.Combine(Path.GetTempPath(), "as_vorbis_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fakeWav = UnityAudioDecoder.ConvertToWav(
                    BuildFsb5SingleSample(UnityAudioCodec.Pcm16, 8u, 0u, 441u, Array.Empty<byte>(), new byte[441 * 2]))!;

                // 2) PrepareRawAudioData passes reconstructed .ogg (NOT .fsb) to transcoder and cleans temp files
                string? observedPreviewInputExt = null;
                byte[]? observedPreviewInputHeader = null;
                var prepared = AudioPreviewPreparer.PrepareRawAudioData(
                    fsb,
                    fallbackExtension: ".fsb",
                    metadataDurationMs: 0,
                    isLinux: false,
                    preferTranscode: false,
                    cancellationToken: CancellationToken.None,
                    tempDirectory: tempDir,
                    transcoder: (inPath, outPath, _) =>
                    {
                        observedPreviewInputExt = Path.GetExtension(inPath);
                        observedPreviewInputHeader = File.ReadAllBytes(inPath).Take(4).ToArray();
                        File.WriteAllBytes(outPath, fakeWav);
                        return true;
                    });

                Assert.NotNull(prepared);
                Assert.Equal(".ogg", observedPreviewInputExt);
                Assert.Equal("OggS", Encoding.ASCII.GetString(observedPreviewInputHeader!));
                Assert.Equal(".wav", prepared!.Extension);
                Assert.Equal("FSB5 -> OGG (Vorbis) -> WAV (Transcoded)", prepared.SourceDescription);
                Assert.Equal(10, prepared.DurationMs);
                Assert.Empty(Directory.GetFiles(tempDir));

                // 3) AssetExportService.TryConvertAudioClipToWav feeds reconstructed .ogg to transcoder
                var clip = CreateSyntheticAudioClip("test_vorbis", AudioCompressionFormat.Vorbis, subsoundIndex: 0, fsb);
                string? observedExportInputExt = null;
                var exportedWav = AssetExportService.TryConvertAudioClipToWav(
                    clip,
                    fsb,
                    transcoder: (inPath, outPath, _) =>
                    {
                        observedExportInputExt = Path.GetExtension(inPath);
                        Assert.Equal("OggS", Encoding.ASCII.GetString(File.ReadAllBytes(inPath), 0, 4));
                        File.WriteAllBytes(outPath, fakeWav);
                        return true;
                    },
                    tempDirectory: tempDir);

                Assert.Equal(".ogg", observedExportInputExt);
                Assert.Equal(fakeWav, exportedWav);
                Assert.Empty(Directory.GetFiles(tempDir));

                // 4) ExportStandardConvertedAsset with ConvertAudio=false exports reconstructed .ogg instead of raw .fsb
                var sf = CreateMinimalSerializedFile();
                clip.assetsFile = sf;
                var item = new AssetItem(clip);
                var ctx = new ExportConversionContext(new List<SerializedFile> { sf });
                var optsNoWav = new ExportOptionsState { ConvertAudio = false };
                Assert.True(AssetExportService.ExportStandardConvertedAsset(item, tempDir, ctx, optsNoWav));

                var exportedFiles = Directory.GetFiles(tempDir);
                Assert.Single(exportedFiles);
                Assert.EndsWith(".ogg", exportedFiles[0], StringComparison.OrdinalIgnoreCase);
                Assert.Equal("OggS", Encoding.ASCII.GetString(File.ReadAllBytes(exportedFiles[0]), 0, 4));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void RealAnimaGateOfMemories_CrackingAndSampleVorbisClips_DecodeToPlayablePcmWavViaBundledFFmpeg()
        {
            string gameDataDir = Path.Combine(
                "F:" + Path.DirectorySeparatorChar,
                "SteamLibrary",
                "steamapps",
                "common",
                "Anima Gate of Memories",
                "AnimaGateOfMemories_Data");

            string sharedAssets77 = Path.Combine(gameDataDir, "sharedassets77.assets");
            if (!File.Exists(sharedAssets77))
            {
                // Portable skip when the local Steam title is not installed on CI/Linux runners.
                return;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "as_vorbis_real_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 1) Verify the exact reported clip: PathID 280 (`222521__uagadugu__cracking...`) in sharedassets77.assets
                var manager = new AssetsManager();
                manager.LoadFiles(sharedAssets77);

                var audioClips = manager.assetsFileList
                    .SelectMany(f => f.Objects)
                    .OfType<AudioClip>()
                    .ToList();

                var crackingClip = audioClips.FirstOrDefault(c => c.m_PathID == 280);
                Assert.NotNull(crackingClip);
                Assert.Contains("cracking", crackingClip!.m_Name, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(AudioCompressionFormat.Vorbis, crackingClip.m_CompressionFormat);

                byte[]? rawFsb = crackingClip.m_AudioData.GetData();
                Assert.NotNull(rawFsb);
                Assert.Equal(UnityAudioCodec.Vorbis, UnityAudioDecoder.DetectFsb5Codec(rawFsb));

                byte[]? reconstructedOgg = UnityAudioDecoder.ConvertToOgg(rawFsb!, crackingClip.m_SubsoundIndex);
                Assert.NotNull(reconstructedOgg);
                var pages = ParseAndValidateOggPages(reconstructedOgg!);
                Assert.True(pages.Count >= 3);
                Assert.NotEqual(0, pages[^1].HeaderType & 0x04); // EOS set

                // Decode via full AudioPreviewPreparer pipeline using bundled FFmpeg (avcodec-62 / avformat-62)
                var prepared = AudioPreviewPreparer.PrepareAudioClip(
                    crackingClip,
                    isLinux: false,
                    preferTranscode: false,
                    cancellationToken: CancellationToken.None,
                    tempDirectory: tempDir);

                Assert.NotNull(prepared);
                Assert.Equal(".wav", prepared!.Extension);
                Assert.Equal("FSB5 -> OGG (Vorbis) -> WAV (Transcoded)", prepared.SourceDescription);
                Assert.Equal("RIFF", Encoding.ASCII.GetString(prepared.Data, 0, 4));
                Assert.Equal("WAVE", Encoding.ASCII.GetString(prepared.Data, 8, 4));
                Assert.True(prepared.Data.Length > 44000, $"Expected real PCM WAV payload, got {prepared.Data.Length} bytes");
                Assert.True(prepared.DurationMs > 500, $"Expected >500ms duration, got {prepared.DurationMs} ms");

                long expectedMs = (long)(crackingClip.m_Length * 1000.0);
                if (expectedMs > 0)
                {
                    Assert.InRange(Math.Abs(prepared.DurationMs - expectedMs), 0, 250);
                }

                // Verify decoded PCM samples are genuinely non-silent audio
                bool hasNonZeroPcm = false;
                for (int i = 44; i + 1 < Math.Min(prepared.Data.Length, 44 + 65536); i += 2)
                {
                    if (BinaryPrimitives.ReadInt16LittleEndian(prepared.Data.AsSpan(i, 2)) != 0)
                    {
                        hasNonZeroPcm = true;
                        break;
                    }
                }
                Assert.True(hasNonZeroPcm, "Decoded PCM WAV must contain non-zero audio samples");

                // Verify AssetExportService.TryConvertAudioClipToWav succeeds end-to-end with real FFmpeg too
                byte[]? exportedWav = AssetExportService.TryConvertAudioClipToWav(crackingClip, rawFsb, tempDirectory: tempDir);
                Assert.NotNull(exportedWav);
                Assert.Equal("RIFF", Encoding.ASCII.GetString(exportedWav!, 0, 4));
                Assert.True(exportedWav!.Length > 44000);

                // 2) Verify ALL Vorbis AudioClips in sharedassets77.assets reconstruct and decode cleanly
                var vorbisClips = audioClips
                    .Where(c => c.m_CompressionFormat == AudioCompressionFormat.Vorbis)
                    .Take(10)
                    .ToList();
                Assert.NotEmpty(vorbisClips);

                foreach (var clip in vorbisClips)
                {
                    byte[]? data = clip.m_AudioData.GetData();
                    if (data == null || data.Length < 60 || UnityAudioDecoder.DetectFsb5Codec(data) != UnityAudioCodec.Vorbis)
                        continue;

                    byte[]? ogg = UnityAudioDecoder.ConvertToOgg(data, clip.m_SubsoundIndex);
                    Assert.NotNull(ogg);
                    ParseAndValidateOggPages(ogg!);

                    var preview = AudioPreviewPreparer.PrepareAudioClip(
                        clip,
                        isLinux: false,
                        preferTranscode: false,
                        cancellationToken: CancellationToken.None,
                        tempDirectory: tempDir);
                    Assert.NotNull(preview);
                    Assert.Equal(".wav", preview!.Extension);
                    Assert.Equal("RIFF", Encoding.ASCII.GetString(preview.Data, 0, 4));
                }

                Assert.Empty(Directory.GetFiles(tempDir));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        #region Helpers

        private readonly record struct ParsedOggPage(
            byte HeaderType,
            long GranulePosition,
            uint SerialNumber,
            uint PageSequence,
            List<byte[]> CompletedPackets);

        private static List<ParsedOggPage> ParseAndValidateOggPages(byte[] ogg)
        {
            var pages = new List<ParsedOggPage>();
            var pendingPacket = new MemoryStream();
            int offset = 0;
            uint expectedSeq = 0;

            while (offset < ogg.Length)
            {
                Assert.True(offset + 27 <= ogg.Length, $"Truncated Ogg page header at offset {offset}");
                Assert.Equal("OggS", Encoding.ASCII.GetString(ogg, offset, 4));
                Assert.Equal((byte)0, ogg[offset + 4]);

                byte headerType = ogg[offset + 5];
                long granule = BinaryPrimitives.ReadInt64LittleEndian(ogg.AsSpan(offset + 6, 8));
                uint serial = BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(offset + 14, 4));
                uint seq = BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(offset + 18, 4));
                uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(offset + 22, 4));
                int segmentCount = ogg[offset + 26];

                Assert.Equal(expectedSeq++, seq);
                Assert.True(offset + 27 + segmentCount <= ogg.Length);

                int bodySize = 0;
                for (int s = 0; s < segmentCount; s++)
                {
                    bodySize += ogg[offset + 27 + s];
                }

                int pageLen = 27 + segmentCount + bodySize;
                Assert.True(offset + pageLen <= ogg.Length, $"Truncated Ogg page body at offset {offset}");

                byte[] pageCopy = new byte[pageLen];
                Buffer.BlockCopy(ogg, offset, pageCopy, 0, pageLen);
                pageCopy[22] = pageCopy[23] = pageCopy[24] = pageCopy[25] = 0;
                uint computedCrc = Fsb5VorbisReconstructor.ComputeOggCrc32(pageCopy);
                Assert.Equal(computedCrc, storedCrc);

                var completed = new List<byte[]>();
                int bodyPos = offset + 27 + segmentCount;
                for (int s = 0; s < segmentCount; s++)
                {
                    int segLen = ogg[offset + 27 + s];
                    if (segLen > 0)
                    {
                        pendingPacket.Write(ogg, bodyPos, segLen);
                        bodyPos += segLen;
                    }
                    if (segLen < 255)
                    {
                        completed.Add(pendingPacket.ToArray());
                        pendingPacket.SetLength(0);
                    }
                }

                pages.Add(new ParsedOggPage(headerType, granule, serial, seq, completed));
                offset += pageLen;
            }

            Assert.Equal(0, pendingPacket.Length);
            return pages;
        }

        private static List<byte[]> ExtractAllOggPackets(byte[] ogg)
        {
            return ParseAndValidateOggPages(ogg).SelectMany(p => p.CompletedPackets).ToList();
        }

        private static uint ComputeOggCrcBitwise(ReadOnlySpan<byte> data)
        {
            uint crc = 0;
            foreach (byte b in data)
            {
                crc ^= (uint)b << 24;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : (crc << 1);
                }
            }
            return crc;
        }

        private static uint ComputeIeeeCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
                }
            }
            return ~crc;
        }

        private static byte[] PackLengthPrefixedPackets(byte[][] packets, bool padTo32)
        {
            using var ms = new MemoryStream();
            Span<byte> lenBuf = stackalloc byte[2];
            foreach (var pkt in packets)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)pkt.Length);
                ms.Write(lenBuf);
                ms.Write(pkt, 0, pkt.Length);
            }
            // Write terminating 0x0000 length + 32-byte alignment padding
            ms.WriteByte(0);
            ms.WriteByte(0);
            if (padTo32)
            {
                int rem = (int)(ms.Length % 32);
                if (rem != 0)
                {
                    ms.Write(new byte[32 - rem], 0, 32 - rem);
                }
            }
            return ms.ToArray();
        }

        private static ulong PackFsb5Meta(bool hasChunks, uint freqId, uint chBits, uint dataOffsetBlocks32, uint sampleCount)
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

        private static byte[] BuildFsb5SingleSample(
            UnityAudioCodec codec,
            uint freqId,
            uint chBits,
            uint sampleCount,
            byte[] chunks,
            byte[] audioData)
        {
            return BuildFsb5MultiSample(codec, (freqId, chBits, sampleCount, chunks, audioData));
        }

        private static byte[] BuildFsb5MultiSample(
            UnityAudioCodec codec,
            params (uint freqId, uint chBits, uint sampleCount, byte[] chunks, byte[] audioData)[] samples)
        {
            const uint headerSize = 60u;
            uint sampleHeadersSize = 0;
            uint totalAudioSize = 0;
            var dataOffsets = new uint[samples.Length];

            for (int i = 0; i < samples.Length; i++)
            {
                sampleHeadersSize += (uint)(8 + samples[i].chunks.Length);
                dataOffsets[i] = totalAudioSize;
                uint paddedLen = (uint)((samples[i].audioData.Length + 31) & ~31);
                totalAudioSize += paddedLen;
            }

            var container = new byte[headerSize + sampleHeadersSize + totalAudioSize];
            Encoding.ASCII.GetBytes("FSB5").CopyTo(container, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(4, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(8, 4), (uint)samples.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(12, 4), sampleHeadersSize);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(16, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(20, 4), totalAudioSize);
            BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(24, 4), (uint)codec);

            int hdrPos = (int)headerSize;
            int audioBase = (int)(headerSize + sampleHeadersSize);
            for (int i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                ulong meta = PackFsb5Meta(s.chunks.Length > 0, s.freqId, s.chBits, dataOffsets[i] / 32u, s.sampleCount);
                BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan(hdrPos, 8), meta);
                hdrPos += 8;
                if (s.chunks.Length > 0)
                {
                    Buffer.BlockCopy(s.chunks, 0, container, hdrPos, s.chunks.Length);
                    hdrPos += s.chunks.Length;
                }
                Buffer.BlockCopy(s.audioData, 0, container, audioBase + (int)dataOffsets[i], s.audioData.Length);
            }

            return container;
        }

        private static byte[] ConcatBytes(params byte[][] parts)
        {
            int total = parts.Sum(p => p.Length);
            var result = new byte[total];
            int pos = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, pos, part.Length);
                pos += part.Length;
            }
            return result;
        }

        private static AudioClip CreateSyntheticAudioClip(string name, AudioCompressionFormat format, int subsoundIndex, byte[] audioData)
        {
            var clip = (AudioClip)RuntimeHelpers.GetUninitializedObject(typeof(AudioClip));
            clip.m_Name = name;
            clip.version = new[] { 2022, 3, 0, 0 };
            clip.m_CompressionFormat = format;
            clip.m_SubsoundIndex = subsoundIndex;
            clip.m_AudioData = new ResourceReader(audioData);
            clip.type = ClassIDType.AudioClip;
            clip.m_PathID = 1001;
            return clip;
        }

        private static SerializedFile CreateMinimalSerializedFile()
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.fileName = "synthetic.assets";
            sf.fullName = "synthetic.assets";
            sf.version = new[] { 2022, 3, 0, 0 };
            sf.Objects = new List<Object>();
            return sf;
        }

        #endregion
    }
}
