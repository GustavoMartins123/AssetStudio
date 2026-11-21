using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class MovieTexturePreviewTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private readonly ITestOutputHelper _output;

        public MovieTexturePreviewTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void OggTheoraHeader_WithVorbisAudio_ParsesExactDimensionsFpsBitrateAndExtension()
        {
            var oggData = BuildSyntheticOggTheoraBytes(
                pictureWidth: 1920,
                pictureHeight: 1080,
                macroblockWidth: 120,
                macroblockHeight: 68,
                fpsNumerator: 60000,
                fpsDenominator: 1001,
                nominalBitrateBps: 4_500_000,
                includeVorbisHeader: true,
                vorbisChannels: 2,
                vorbisSampleRate: 48000);

            var metadata = MovieTexturePreviewService.InspectBytes(
                oggData,
                assetName: "OpeningCutscene",
                isLooping: true,
                hasLinkedAudioClip: true,
                linkedAudioClipName: "OpeningCutscene_Audio",
                linkedAudioChannels: 2,
                linkedAudioFrequency: 44100);

            Assert.True(metadata.HasMovieData);
            Assert.Equal("OpeningCutscene", metadata.AssetName);
            Assert.Equal("Ogg Theora", metadata.DetectedFormat);
            Assert.Equal("Theora", metadata.VideoCodec);
            Assert.Equal(".ogv", metadata.RecommendedExtension);
            Assert.Equal(1920, metadata.Width);
            Assert.Equal(1080, metadata.Height);
            Assert.Equal(60000u, metadata.FrameRateNumerator);
            Assert.Equal(1001u, metadata.FrameRateDenominator);
            Assert.Equal(60000.0 / 1001.0, metadata.FrameRate, precision: 4);
            Assert.Equal(4_500_000, metadata.NominalBitrateBps);
            Assert.Equal("3.2.1", metadata.TheoraVersion);
            Assert.Equal(oggData.Length, metadata.ByteSize);
            Assert.True(metadata.IsLooping);
            Assert.True(metadata.HasEmbeddedAudio);
            Assert.Equal("Vorbis", metadata.EmbeddedAudioCodec);
            Assert.Equal(2, metadata.EmbeddedAudioChannels);
            Assert.Equal(48000, metadata.EmbeddedAudioSampleRate);
            Assert.True(metadata.HasLinkedAudioClip);
            Assert.Equal("OpeningCutscene_Audio", metadata.LinkedAudioClipName);
            Assert.Equal("Resolution: 1920x1080", metadata.ResolutionText);
            Assert.Equal("Frame Rate: 59.94 FPS", metadata.FrameRateText);
            Assert.Contains(".ogv", metadata.FormatLabelText, StringComparison.Ordinal);
            Assert.Contains("Embedded Vorbis (2ch 48000Hz)", metadata.AudioLabelText, StringComparison.Ordinal);
            Assert.Contains("Linked AudioClip: OpeningCutscene_Audio (2ch 44100Hz)", metadata.AudioLabelText, StringComparison.Ordinal);
            Assert.Contains("m_Loop = true", metadata.ReadyInfoText, StringComparison.Ordinal);
        }

        [Fact]
        public void OggTheoraHeader_MacroblockFallbackAndOpusAudio_ParsesCorrectly()
        {
            var oggData = BuildSyntheticOggTheoraBytes(
                pictureWidth: 0,
                pictureHeight: 0,
                macroblockWidth: 80, // 80 * 16 = 1280
                macroblockHeight: 45, // 45 * 16 = 720
                fpsNumerator: 30,
                fpsDenominator: 1,
                nominalBitrateBps: 2_000_000,
                includeVorbisHeader: false,
                includeOpusHeader: true,
                opusChannels: 2,
                opusSampleRate: 48000);

            var metadata = MovieTexturePreviewService.InspectBytes(oggData, assetName: "Logo720p", isLooping: false);

            Assert.True(metadata.HasMovieData);
            Assert.Equal("Ogg Theora", metadata.DetectedFormat);
            Assert.Equal(".ogv", metadata.RecommendedExtension);
            Assert.Equal(1280, metadata.Width);
            Assert.Equal(720, metadata.Height);
            Assert.Equal(30.0, metadata.FrameRate, precision: 3);
            Assert.True(metadata.HasEmbeddedAudio);
            Assert.Equal("Opus", metadata.EmbeddedAudioCodec);
            Assert.Equal(2, metadata.EmbeddedAudioChannels);
            Assert.Equal(48000, metadata.EmbeddedAudioSampleRate);
            Assert.False(metadata.IsLooping);
        }

        [Fact]
        public void AlternativeContainers_AviMp4MovWebmMkvMpegFlvWmv_AreDetectedAccurately()
        {
            // 1. RIFF/AVI with avih (1920x1080, 40000 usec/frame = 25 FPS) + strh vidsMJPG + auds
            var aviBytes = BuildSyntheticAviBytes(width: 1920, height: 1080, usecPerFrame: 40000, fourCc: "MJPG", includeAudioStream: true);
            var aviMeta = MovieTexturePreviewService.InspectBytes(aviBytes, "intrologos1080");
            Assert.Equal("AVI", aviMeta.DetectedFormat);
            Assert.Equal("MJPG", aviMeta.VideoCodec);
            Assert.Equal(".avi", aviMeta.RecommendedExtension);
            Assert.Equal(1920, aviMeta.Width);
            Assert.Equal(1080, aviMeta.Height);
            Assert.Equal(25.0, aviMeta.FrameRate, precision: 2);
            Assert.True(aviMeta.HasEmbeddedAudio);

            // 2. MP4 ftyp isom with tkhd (1280x720)
            var mp4Bytes = BuildSyntheticMp4Bytes("isom", width: 1280, height: 720);
            var mp4Meta = MovieTexturePreviewService.InspectBytes(mp4Bytes, "cutscene_mp4");
            Assert.Equal("MP4", mp4Meta.DetectedFormat);
            Assert.Equal(".mp4", mp4Meta.RecommendedExtension);
            Assert.Equal(1280, mp4Meta.Width);
            Assert.Equal(720, mp4Meta.Height);

            // 3. QuickTime MOV (ftypqt  )
            var movBytes = BuildSyntheticMp4Bytes("qt  ", width: 640, height: 480);
            var movMeta = MovieTexturePreviewService.InspectBytes(movBytes, "quicktime_clip");
            Assert.Equal("QuickTime MOV", movMeta.DetectedFormat);
            Assert.Equal(".mov", movMeta.RecommendedExtension);
            Assert.Equal(640, movMeta.Width);
            Assert.Equal(480, movMeta.Height);

            // 4. WebM (EBML + webm doctype)
            var webmBytes = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84, (byte)'w', (byte)'e', (byte)'b', (byte)'m', 0x00 };
            var webmMeta = MovieTexturePreviewService.InspectBytes(webmBytes, "clip_webm");
            Assert.Equal("WebM", webmMeta.DetectedFormat);
            Assert.Equal("VP8/VP9", webmMeta.VideoCodec);
            Assert.Equal(".webm", webmMeta.RecommendedExtension);

            // 5. Matroska MKV (EBML + matroska doctype)
            var mkvBytes = new byte[]
            {
                0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x88,
                (byte)'m', (byte)'a', (byte)'t', (byte)'r', (byte)'o', (byte)'s', (byte)'k', (byte)'a'
            };
            var mkvMeta = MovieTexturePreviewService.InspectBytes(mkvBytes, "clip_mkv");
            Assert.Equal("Matroska (MKV)", mkvMeta.DetectedFormat);
            Assert.Equal(".mkv", mkvMeta.RecommendedExtension);

            // 6. MPEG-PS (00 00 01 BA)
            var mpegBytes = new byte[] { 0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00 };
            var mpegMeta = MovieTexturePreviewService.InspectBytes(mpegBytes, "clip_mpg");
            Assert.Equal("MPEG", mpegMeta.DetectedFormat);
            Assert.Equal(".mpg", mpegMeta.RecommendedExtension);

            // 7. FLV (FLV\x01 with audio+video flags 0x05)
            var flvBytes = new byte[] { (byte)'F', (byte)'L', (byte)'V', 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 };
            var flvMeta = MovieTexturePreviewService.InspectBytes(flvBytes, "clip_flv");
            Assert.Equal("FLV", flvMeta.DetectedFormat);
            Assert.Equal(".flv", flvMeta.RecommendedExtension);
            Assert.True(flvMeta.HasEmbeddedAudio);

            // 8. WMV / ASF
            var wmvBytes = new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA };
            var wmvMeta = MovieTexturePreviewService.InspectBytes(wmvBytes, "clip_wmv");
            Assert.Equal("WMV/ASF", wmvMeta.DetectedFormat);
            Assert.Equal(".wmv", wmvMeta.RecommendedExtension);
        }

        [Fact]
        public void EmptyOrCorruptMovieData_HandledGracefullyWithoutThrowing()
        {
            // 1. Null MovieTexture
            var nullMeta = MovieTexturePreviewService.Inspect(null);
            Assert.False(nullMeta.HasMovieData);
            Assert.Equal("Empty", nullMeta.DetectedFormat);
            Assert.Equal(0, nullMeta.ByteSize);
            Assert.False(string.IsNullOrWhiteSpace(nullMeta.SummaryText));

            // 2. Empty byte array
            var emptyMeta = MovieTexturePreviewService.InspectBytes(ReadOnlySpan<byte>.Empty, "EmptyMovie", isLooping: true);
            Assert.False(emptyMeta.HasMovieData);
            Assert.Equal("Empty", emptyMeta.DetectedFormat);
            Assert.True(emptyMeta.IsLooping);
            Assert.Contains("EmptyMovie", emptyMeta.SummaryText, StringComparison.Ordinal);
            Assert.Contains("No embedded m_MovieData", emptyMeta.SummaryText, StringComparison.Ordinal);

            // 3. Truncated OggS with incomplete \x80theora header (< 42 bytes)
            var truncatedOgg = new byte[]
            {
                (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0x00, 0x02,
                0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a', 0x03, 0x02
            };
            var truncatedMeta = MovieTexturePreviewService.InspectBytes(truncatedOgg, "TruncatedOgg");
            Assert.True(truncatedMeta.HasMovieData);
            Assert.Equal("Ogg Media", truncatedMeta.DetectedFormat);
            Assert.Equal(".ogv", truncatedMeta.RecommendedExtension);
            Assert.Equal(0, truncatedMeta.Width);
            Assert.Equal(0, truncatedMeta.Height);

            // 4. Random unrecognized binary bytes
            var unknownBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78 };
            var unknownMeta = MovieTexturePreviewService.InspectBytes(unknownBytes, "UnknownBlob");
            Assert.True(unknownMeta.HasMovieData);
            Assert.Equal("Unknown", unknownMeta.DetectedFormat);
            Assert.Equal(".ogv", unknownMeta.RecommendedExtension);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MovieTexture_BinaryDeserialization_PreservesLoopFlagAudioClipPPtrAndMovieData(bool expectedLoop)
        {
            var theoraBytes = BuildSyntheticOggTheoraBytes(
                pictureWidth: 854,
                pictureHeight: 480,
                macroblockWidth: 54,
                macroblockHeight: 30,
                fpsNumerator: 24,
                fpsDenominator: 1,
                nominalBitrateBps: 1_200_000,
                includeVorbisHeader: true,
                vorbisChannels: 2,
                vorbisSampleRate: 44100);

            byte[] objectBytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                // NamedObject.m_Name ("CreditsMovie" = 12 chars, 4-byte aligned)
                const string name = "CreditsMovie";
                bw.Write(name.Length);
                bw.Write(Encoding.UTF8.GetBytes(name));

                // Texture base fields for Unity 5.3.4f1: none (only 2017.3+ reads m_ForcedFallbackFormat etc.)
                // MovieTexture fields:
                // 1) bool m_Loop + AlignStream(4)
                bw.Write(expectedLoop);
                bw.Write(new byte[3]); // 4-byte alignment padding

                // 2) PPtr<AudioClip>: int m_FileID, int64 m_PathID (version >= 14)
                bw.Write(0); // m_FileID
                bw.Write(987654L); // m_PathID

                // 3) byte[] m_MovieData: int32 length + bytes
                bw.Write(theoraBytes.Length);
                bw.Write(theoraBytes);
                bw.Flush();
                objectBytes = ms.ToArray();
            }

            var sf = CreateSerializedFileWithData(
                objectBytes,
                EndianType.LittleEndian,
                out var objInfo,
                objectBytes.Length,
                new[] { 5, 3, 4, 1 });
            objInfo.classID = (int)ClassIDType.MovieTexture;
            objInfo.typeID = (int)ClassIDType.MovieTexture;

            // Add a linked AudioClip at PathID 987654 in the same SerializedFile to verify PPtr resolution!
            var linkedClip = (AudioClip)RuntimeHelpers.GetUninitializedObject(typeof(AudioClip));
            linkedClip.m_Name = "CreditsAudioTrack";
            linkedClip.m_PathID = 987654L;
            linkedClip.m_Channels = 2;
            linkedClip.m_Frequency = 44100;
            linkedClip.assetsFile = sf;
            sf.AddObject(linkedClip);

            using var objReader = new ObjectReader(sf.reader, sf, objInfo);
            var movieTexture = new MovieTexture(objReader);

            Assert.Equal("CreditsMovie", movieTexture.m_Name);
            Assert.Equal(expectedLoop, movieTexture.m_Loop);
            Assert.NotNull(movieTexture.m_AudioClip);
            Assert.Equal(0, movieTexture.m_AudioClip.m_FileID);
            Assert.Equal(987654L, movieTexture.m_AudioClip.m_PathID);
            Assert.Equal(theoraBytes, movieTexture.m_MovieData);
            Assert.Equal(objReader.byteStart + objReader.byteSize, objReader.Position);

            var metadata = MovieTexturePreviewService.Inspect(movieTexture);
            Assert.Equal(expectedLoop, metadata.IsLooping);
            Assert.True(metadata.HasMovieData);
            Assert.Equal(854, metadata.Width);
            Assert.Equal(480, metadata.Height);
            Assert.Equal(24.0, metadata.FrameRate, precision: 3);
            Assert.True(metadata.HasLinkedAudioClip);
            Assert.Equal("CreditsAudioTrack", metadata.LinkedAudioClipName);
            Assert.Equal(2, metadata.LinkedAudioChannels);
            Assert.Equal(44100, metadata.LinkedAudioFrequency);
        }

        [Fact]
        public void MovieTexturePreviewService_IsPureMvvm_AndMainWindowExposesPreviewEmbeddedVideoBytesHook()
        {
            var repoRoot = LocateRepositoryRoot();
            var servicePath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "MovieTexturePreviewService.cs");
            Assert.True(File.Exists(servicePath), $"Service file not found: {servicePath}");

            var sourceText = File.ReadAllText(servicePath);
            Assert.False(
                AvaloniaUsingRegex.IsMatch(sourceText),
                "MovieTexturePreviewService.cs must have ZERO 'using Avalonia' imports.");

            // Verify MainWindow.PreviewEmbeddedVideoBytes exact shared hook signature
            var method = typeof(MainWindow).GetMethod(
                "PreviewEmbeddedVideoBytes",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(method);

            var parameters = method!.GetParameters();
            Assert.Equal(11, parameters.Length);
            Assert.Equal(typeof(AssetItem), parameters[0].ParameterType);
            Assert.Equal(typeof(string), parameters[1].ParameterType);
            Assert.Equal(typeof(byte[]), parameters[2].ParameterType);
            Assert.Equal(typeof(string), parameters[3].ParameterType);
            Assert.Equal(typeof(string), parameters[4].ParameterType);
            Assert.Equal(typeof(string), parameters[5].ParameterType);
            Assert.Equal(typeof(string), parameters[6].ParameterType);
            Assert.Equal(typeof(string), parameters[7].ParameterType);
            Assert.Equal(typeof(string), parameters[8].ParameterType);
            Assert.Equal(typeof(string), parameters[9].ParameterType);
            Assert.Equal(typeof(bool), parameters[10].ParameterType);
        }

        [Fact]
        public void RealGameReadOnlyProbe_AnimaGateOfMemories_ChecksMovieTextureAndEmbeddedAviVideo()
        {
            var sourceDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "IndexCache",
                "project_index_4f71c3028b7546f85a11a101b585fe8d.db");
            const string gameDataDir = @"F:\SteamLibrary\steamapps\common\Anima Gate of Memories\AnimaGateOfMemories_Data";

            if (!File.Exists(sourceDbPath) || !Directory.Exists(gameDataDir))
            {
                _output.WriteLine("Skipping real-game read-only MovieTexture check (Anima Gate of Memories DB/game folder not present).");
                return;
            }

            using var conn = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Assets WHERE Type = 152;";
            var movieTextureCount = Convert.ToInt64(countCmd.ExecuteScalar() ?? 0L);

            using var sampleMtCmd = conn.CreateCommand();
            sampleMtCmd.CommandText = @"
                SELECT a.UniqueID, a.PathID, a.Name, a.ByteSize, COALESCE(sf.SerializedFileName, ''), COALESCE(sf.OriginalPath, '')
                FROM Assets a
                LEFT JOIN SourceFiles sf ON sf.Id = a.SourceFileId
                WHERE a.Type = 152 AND a.ByteSize > 64
                ORDER BY a.ByteSize ASC
                LIMIT 3;";

            var sampleMovies = new System.Collections.Generic.List<(string UniqueId, long PathId, string Name, long ByteSize, string FileName, string OriginalPath)>();
            using (var mtReader = sampleMtCmd.ExecuteReader())
            {
                while (mtReader.Read())
                {
                    sampleMovies.Add((
                        mtReader.GetString(0),
                        mtReader.GetInt64(1),
                        mtReader.GetString(2),
                        mtReader.GetInt64(3),
                        mtReader.GetString(4),
                        mtReader.GetString(5)));
                }
            }

            foreach (var sample in sampleMovies)
            {
                _output.WriteLine($"Real MovieTexture in AnimaGateOfMemories DB: id={sample.UniqueId}, file={sample.FileName}, pathId={sample.PathId}, name={sample.Name}, bytes={sample.ByteSize:N0}");
            }

            // Read-only deserialize & inspect a real MovieTexture object directly from the game's serialized file!
            int inspectedRealMovieCount = 0;
            foreach (var sample in sampleMovies)
            {
                var candidatePath = !string.IsNullOrWhiteSpace(sample.OriginalPath) && File.Exists(sample.OriginalPath)
                    ? sample.OriginalPath
                    : Path.Combine(gameDataDir, sample.FileName);
                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                var am = new AssetsManager { LazyLoading = true };
                try
                {
                    using var fs = new FileStream(candidatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var fr = new FileReader(candidatePath, fs);
                    var sf = new SerializedFile(fr, am);
                    if (sf.ObjectInfoDic.TryGetValue(sample.PathId, out var objInfo))
                    {
                        var objReader = new ObjectReader(fr, sf, objInfo);
                        var realMovie = new MovieTexture(objReader);
                        var meta = MovieTexturePreviewService.Inspect(realMovie);
                        inspectedRealMovieCount++;
                        _output.WriteLine(
                            $"Inspected real MovieTexture '{realMovie.m_Name}' (PathID={sample.PathId}): " +
                            $"format={meta.DetectedFormat}, codec={meta.VideoCodec}, ext={meta.RecommendedExtension}, " +
                            $"res={meta.Width}x{meta.Height}, fps={meta.FrameRate:F2}, bytes={meta.ByteSize:N0}, " +
                            $"loop={realMovie.m_Loop}, hasLinkedAudio={meta.HasLinkedAudioClip} ({meta.LinkedAudioClipName})");

                        Assert.True(meta.HasMovieData);
                        Assert.True(meta.ByteSize > 0);
                        Assert.Equal(realMovie.m_Loop, meta.IsLooping);
                    }
                }
                finally
                {
                    am.Clear();
                }
            }

            using var aviCmd = conn.CreateCommand();
            aviCmd.CommandText = "SELECT UniqueID, PathID, Name, ByteSize, Type FROM Assets WHERE Name LIKE '%.avi%' OR Name = 'intrologos1080.avi' OR Name = 'intrologos1080' LIMIT 5;";
            using var reader = aviCmd.ExecuteReader();
            int videoAssetsFound = 0;
            while (reader.Read())
            {
                videoAssetsFound++;
                _output.WriteLine($"Found video asset in AnimaGateOfMemories DB: id={reader.GetString(0)}, pathId={reader.GetInt64(1)}, name={reader.GetString(2)}, bytes={reader.GetInt64(3):N0}, classId={reader.GetInt32(4)}");
            }

            _output.WriteLine($"AnimaGateOfMemories_Data read-only check: MovieTexture (ClassID 152) count = {movieTextureCount}, inspectedRealMovieCount = {inspectedRealMovieCount}, AVI video assets found = {videoAssetsFound}");
            Assert.True(movieTextureCount > 0);
            Assert.True(inspectedRealMovieCount > 0);
        }

        private static byte[] BuildSyntheticOggTheoraBytes(
            int pictureWidth,
            int pictureHeight,
            ushort macroblockWidth,
            ushort macroblockHeight,
            uint fpsNumerator,
            uint fpsDenominator,
            int nominalBitrateBps,
            bool includeVorbisHeader,
            int vorbisChannels = 2,
            uint vorbisSampleRate = 48000,
            bool includeOpusHeader = false,
            byte opusChannels = 2,
            uint opusSampleRate = 48000)
        {
            using var ms = new MemoryStream();

            // Page 0: Ogg capture pattern + Theora 42-byte Identification Header (\x80theora)
            var theoraHeader = new byte[42];
            theoraHeader[0] = 0x80;
            Encoding.ASCII.GetBytes("theora", theoraHeader.AsSpan(1, 6));
            theoraHeader[7] = 3; // VMAJ
            theoraHeader[8] = 2; // VMIN
            theoraHeader[9] = 1; // VREV
            BinaryPrimitives.WriteUInt16BigEndian(theoraHeader.AsSpan(10, 2), macroblockWidth);
            BinaryPrimitives.WriteUInt16BigEndian(theoraHeader.AsSpan(12, 2), macroblockHeight);

            // 24-bit big-endian PICW & PICH
            theoraHeader[14] = (byte)((pictureWidth >> 16) & 0xFF);
            theoraHeader[15] = (byte)((pictureWidth >> 8) & 0xFF);
            theoraHeader[16] = (byte)(pictureWidth & 0xFF);
            theoraHeader[17] = (byte)((pictureHeight >> 16) & 0xFF);
            theoraHeader[18] = (byte)((pictureHeight >> 8) & 0xFF);
            theoraHeader[19] = (byte)(pictureHeight & 0xFF);

            BinaryPrimitives.WriteUInt32BigEndian(theoraHeader.AsSpan(22, 4), fpsNumerator);
            BinaryPrimitives.WriteUInt32BigEndian(theoraHeader.AsSpan(26, 4), fpsDenominator);
            theoraHeader[30] = 0;
            theoraHeader[31] = 0;
            theoraHeader[32] = 1; // PARN = 1
            theoraHeader[33] = 0;
            theoraHeader[34] = 0;
            theoraHeader[35] = 1; // PARD = 1

            theoraHeader[37] = (byte)((nominalBitrateBps >> 16) & 0xFF);
            theoraHeader[38] = (byte)((nominalBitrateBps >> 8) & 0xFF);
            theoraHeader[39] = (byte)(nominalBitrateBps & 0xFF);

            WriteOggPage(ms, theoraHeader, serialNumber: 1, pageSeq: 0, headerTypeFlag: 0x02);

            if (includeVorbisHeader)
            {
                var vorbisHeader = new byte[30];
                vorbisHeader[0] = 0x01;
                Encoding.ASCII.GetBytes("vorbis", vorbisHeader.AsSpan(1, 6));
                BinaryPrimitives.WriteUInt32LittleEndian(vorbisHeader.AsSpan(7, 4), 0u); // vorbis_version = 0
                vorbisHeader[11] = (byte)vorbisChannels;
                BinaryPrimitives.WriteUInt32LittleEndian(vorbisHeader.AsSpan(12, 4), vorbisSampleRate);
                WriteOggPage(ms, vorbisHeader, serialNumber: 2, pageSeq: 0, headerTypeFlag: 0x02);
            }

            if (includeOpusHeader)
            {
                var opusHeader = new byte[19];
                Encoding.ASCII.GetBytes("OpusHead", opusHeader.AsSpan(0, 8));
                opusHeader[8] = 1; // version
                opusHeader[9] = opusChannels;
                BinaryPrimitives.WriteUInt32LittleEndian(opusHeader.AsSpan(12, 4), opusSampleRate);
                WriteOggPage(ms, opusHeader, serialNumber: 3, pageSeq: 0, headerTypeFlag: 0x02);
            }

            return ms.ToArray();
        }

        private static void WriteOggPage(Stream stream, byte[] payload, uint serialNumber, uint pageSeq, byte headerTypeFlag)
        {
            Span<byte> pageHeader = stackalloc byte[28];
            pageHeader[0] = (byte)'O';
            pageHeader[1] = (byte)'g';
            pageHeader[2] = (byte)'g';
            pageHeader[3] = (byte)'S';
            pageHeader[4] = 0; // version
            pageHeader[5] = headerTypeFlag;
            BinaryPrimitives.WriteUInt64LittleEndian(pageHeader.Slice(6, 8), 0UL);
            BinaryPrimitives.WriteUInt32LittleEndian(pageHeader.Slice(14, 4), serialNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(pageHeader.Slice(18, 4), pageSeq);
            BinaryPrimitives.WriteUInt32LittleEndian(pageHeader.Slice(22, 4), 0u);
            pageHeader[26] = 1; // 1 segment
            pageHeader[27] = (byte)Math.Min(255, payload.Length);
            stream.Write(pageHeader);
            stream.Write(payload, 0, payload.Length);
        }

        private static byte[] BuildSyntheticAviBytes(int width, int height, uint usecPerFrame, string fourCc, bool includeAudioStream)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(256u);
            bw.Write(Encoding.ASCII.GetBytes("AVI "));
            bw.Write(Encoding.ASCII.GetBytes("LIST"));
            bw.Write(192u);
            bw.Write(Encoding.ASCII.GetBytes("hdrl"));

            // avih chunk (56 bytes payload)
            bw.Write(Encoding.ASCII.GetBytes("avih"));
            bw.Write(56u);
            var avih = new byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0, 4), usecPerFrame);
            BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(32, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(36, 4), height);
            bw.Write(avih);

            // strh chunk for video ('vids' + fourCC)
            bw.Write(Encoding.ASCII.GetBytes("strh"));
            bw.Write(56u);
            bw.Write(Encoding.ASCII.GetBytes("vids"));
            bw.Write(Encoding.ASCII.GetBytes(fourCc.PadRight(4).Substring(0, 4)));
            bw.Write(new byte[48]);

            if (includeAudioStream)
            {
                bw.Write(Encoding.ASCII.GetBytes("strh"));
                bw.Write(56u);
                bw.Write(Encoding.ASCII.GetBytes("auds"));
                bw.Write(new byte[52]);
            }

            bw.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildSyntheticMp4Bytes(string majorBrand, int width, int height)
        {
            var bytes = new byte[128];
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 20u);
            Encoding.ASCII.GetBytes("ftyp", bytes.AsSpan(4, 4));
            Encoding.ASCII.GetBytes(majorBrand.PadRight(4).Substring(0, 4), bytes.AsSpan(8, 4));

            // Add version-0 tkhd box at offset 24: width at tkhd+4+80, height at tkhd+4+84 (16.16 fixed point)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 92u);
            Encoding.ASCII.GetBytes("tkhd", bytes.AsSpan(24, 4));
            bytes[28] = 0; // version 0
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24 + 4 + 80, 4), (uint)(width << 16));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24 + 4 + 84, 4), (uint)(height << 16));
            return bytes;
        }

        private static SerializedFile CreateSerializedFileWithData(
            byte[] objectBytes,
            EndianType endian,
            out ObjectInfo objectInfo,
            int objectByteSize,
            int[]? version = null)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var bw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Encoding.UTF8.GetBytes("5.3.4f1"));
                bw.Write((byte)0);
                bw.Write((int)BuildTarget.StandaloneWindows64);
                bw.Write(false);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write((byte)0);
                bw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            int dataOffset = (headerSize + metadata.Length + 15) & ~15;
            uint totalSize = (uint)(dataOffset + objectBytes.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), (uint)dataOffset);
            fullBytes[16] = 0;
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            Buffer.BlockCopy(objectBytes, 0, fullBytes, dataOffset, objectBytes.Length);

            var fileMs = new MemoryStream(fullBytes);
            var fileReader = new FileReader("movietexture_test.assets", fileMs);
            var sf = new SerializedFile(fileReader, new AssetsManager());
            sf.header.m_Endianess = endian == EndianType.BigEndian ? (byte)1 : (byte)0;
            sf.reader.Endian = endian;
            if (version != null)
            {
                sf.version = version;
            }

            objectInfo = new ObjectInfo
            {
                byteStart = dataOffset,
                byteSize = (uint)objectByteSize,
                m_PathID = 42,
                classID = (int)ClassIDType.MovieTexture,
                typeID = (int)ClassIDType.MovieTexture
            };
            return sf;
        }

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }
    }
}
