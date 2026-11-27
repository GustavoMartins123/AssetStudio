using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests;

public class TextAssetBinaryFormatTests
{
    private readonly ITestOutputHelper _output;

    public TextAssetBinaryFormatTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Detect_AviWithAvihStrhStrfMjpgAndInfoIsft_ExtractsFullVideoAndAudioMetadata()
    {
        byte[] aviBytes = BuildSyntheticAviBytes(
            width: 1920,
            height: 1080,
            microSecPerFrame: 16683,
            totalFrames: 944,
            videoCodecFourCc: "MJPG",
            rateScale: 1001,
            rateValue: 60000,
            includeMp3AudioStream: true,
            encoderSoftware: "Lavf57.29.101");

        var detected = TextAssetBinaryFormatDetector.Detect(aviBytes, "intrologos1080.avi");

        Assert.Equal(TextAssetEmbeddedMediaKind.Video, detected.MediaKind);
        Assert.Equal("AVI", detected.FormatId);
        Assert.Equal(".avi", detected.RecommendedExtension);
        Assert.Equal("video/x-msvideo", detected.MimeType);
        Assert.True(detected.IsPlayableVideo);
        Assert.True(detected.IsBinaryFormat);
        Assert.Equal(1920, detected.Width);
        Assert.Equal(1080, detected.Height);
        Assert.Equal("MJPG", detected.VideoCodec);
        Assert.Equal(944, detected.TotalFrames);
        Assert.InRange(detected.FrameRate, 59.90, 60.00);
        Assert.InRange(detected.DurationSeconds, 15.70, 15.80);
        Assert.Equal("MP3 (MPEG Layer III)", detected.AudioCodec);
        Assert.Equal(2, detected.Channels);
        Assert.Equal(44100, detected.SampleRate);
        Assert.Contains("1920x1080", detected.SummaryText, StringComparison.Ordinal);
        Assert.Contains("MJPG", detected.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Lavf57.29.101", detected.SummaryText, StringComparison.Ordinal);

        // Verify TextAssetPreviewBuilder produces structured metadata instead of raw binary string dump
        var item = CreateSyntheticAssetItem("intrologos1080.avi");
        var preview = TextAssetPreviewBuilder.BuildPreview(item, aviBytes, string.Empty);
        Assert.False(preview.HasDialogueCards);
        Assert.NotNull(preview.DetectedFormat);
        Assert.Equal("AVI", preview.DetectedFormat!.FormatId);
        Assert.Contains("Detected format: AVI Video (MJPG)", preview.DetailsText, StringComparison.Ordinal);
        Assert.Contains("Resolution: 1920x1080", preview.DetailsText, StringComparison.Ordinal);
        Assert.Contains("Encoder / Software: Lavf57.29.101", preview.DetailsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_AllVideoFormats_Mp4MovWebMMkvOgvFlvWmvMpegPsMpegTsBinkAndCriUsm()
    {
        // 1. MP4 (ftypisom + mvhd + tkhd + avc1 + mp4a)
        var mp4Bytes = BuildSyntheticMp4Bytes("isom", width: 1280, height: 720, timeScale: 1000, duration: 5000);
        var mp4 = TextAssetBinaryFormatDetector.Detect(mp4Bytes, "cutscene");
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, mp4.MediaKind);
        Assert.Equal("MP4", mp4.FormatId);
        Assert.Equal(".mp4", mp4.RecommendedExtension);
        Assert.True(mp4.IsPlayableVideo);
        Assert.Equal(1280, mp4.Width);
        Assert.Equal(720, mp4.Height);
        Assert.Equal(5.0, mp4.DurationSeconds, 2);
        Assert.Equal("H.264 / AVC", mp4.VideoCodec);
        Assert.Equal("AAC", mp4.AudioCodec);

        // 2. QuickTime MOV (ftypqt  )
        var movBytes = BuildSyntheticMp4Bytes("qt  ", width: 1920, height: 1080, timeScale: 600, duration: 1800);
        var mov = TextAssetBinaryFormatDetector.Detect(movBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, mov.MediaKind);
        Assert.Equal("MOV", mov.FormatId);
        Assert.Equal(".mov", mov.RecommendedExtension);
        Assert.True(mov.IsPlayableVideo);

        // 3. WebM (EBML 1A 45 DF A3 + DocType webm + V_VP9 + PixelWidth/Height)
        var webmBytes = new List<byte> { 0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84 };
        webmBytes.AddRange(Encoding.ASCII.GetBytes("webmV_VP9A_OPUS"));
        webmBytes.AddRange(new byte[] { 0xB0, 0x82, 0x07, 0x80, 0xBA, 0x82, 0x04, 0x38 }); // 1920x1080
        var webm = TextAssetBinaryFormatDetector.Detect(webmBytes.ToArray());
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, webm.MediaKind);
        Assert.Equal("WebM", webm.FormatId);
        Assert.Equal(".webm", webm.RecommendedExtension);
        Assert.Equal(1920, webm.Width);
        Assert.Equal(1080, webm.Height);
        Assert.Equal("VP9", webm.VideoCodec);
        Assert.Equal("Opus", webm.AudioCodec);
        Assert.True(webm.IsPlayableVideo);

        // 4. Matroska MKV (EBML + matroska + V_MPEG4/ISO/AVC)
        var mkvBytes = new List<byte> { 0x1A, 0x45, 0xDF, 0xA3 };
        mkvBytes.AddRange(Encoding.ASCII.GetBytes("matroskaV_MPEG4/ISO/AVCA_VORBIS"));
        var mkv = TextAssetBinaryFormatDetector.Detect(mkvBytes.ToArray());
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, mkv.MediaKind);
        Assert.Equal("MKV", mkv.FormatId);
        Assert.Equal(".mkv", mkv.RecommendedExtension);
        Assert.Equal("H.264 / AVC", mkv.VideoCodec);
        Assert.Equal("Vorbis", mkv.AudioCodec);

        // 5. Ogg Theora Video (OggS + \x80theora header + \x01vorbis audio stream)
        var ogvBytes = BuildSyntheticOggTheoraBytes(width: 1280, height: 720, fpsNum: 30000, fpsDen: 1001, includeVorbis: true);
        var ogv = TextAssetBinaryFormatDetector.Detect(ogvBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, ogv.MediaKind);
        Assert.Equal("OGV", ogv.FormatId);
        Assert.Equal(".ogv", ogv.RecommendedExtension);
        Assert.Equal("Theora", ogv.VideoCodec);
        Assert.Equal(1280, ogv.Width);
        Assert.Equal(720, ogv.Height);
        Assert.InRange(ogv.FrameRate, 29.95, 30.00);
        Assert.Equal("Vorbis", ogv.AudioCodec);
        Assert.True(ogv.IsPlayableVideo);

        // 6. FLV (FLV\x01 with video+audio flags = 0x05)
        var flv = TextAssetBinaryFormatDetector.Detect(new byte[] { (byte)'F', (byte)'L', (byte)'V', 0x01, 0x05, 0, 0, 0, 9 });
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, flv.MediaKind);
        Assert.Equal("FLV", flv.FormatId);
        Assert.Equal(".flv", flv.RecommendedExtension);
        Assert.True(flv.IsPlayableVideo);

        // 7. ASF / WMV
        var asfBytes = new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C, 0, 0, 0, 0 };
        var wmv = TextAssetBinaryFormatDetector.Detect(asfBytes, "intro.wmv");
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, wmv.MediaKind);
        Assert.Equal("WMV", wmv.FormatId);
        Assert.Equal(".wmv", wmv.RecommendedExtension);

        // 8. MPEG-PS (00 00 01 BA + 00 00 01 B3 sequence header 640x480 @ 29.97fps)
        var mpegPsBytes = new byte[]
        {
            0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00,
            0x00, 0x00, 0x01, 0xB3, 0x28, 0x01, 0xE0, 0x24 // width = 0x280 (640), height = 0x1E0 (480), fpsCode = 4 (29.97)
        };
        var mpegPs = TextAssetBinaryFormatDetector.Detect(mpegPsBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, mpegPs.MediaKind);
        Assert.Equal("MPEG-PS", mpegPs.FormatId);
        Assert.Equal(".mpg", mpegPs.RecommendedExtension);
        Assert.Equal(640, mpegPs.Width);
        Assert.Equal(480, mpegPs.Height);
        Assert.Equal(29.97, mpegPs.FrameRate, 2);

        // 9. MPEG-TS (188-byte sync cadence 0x47 at 0, 188, 376)
        var tsBytes = new byte[400];
        tsBytes[0] = 0x47;
        tsBytes[188] = 0x47;
        tsBytes[376] = 0x47;
        var ts = TextAssetBinaryFormatDetector.Detect(tsBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, ts.MediaKind);
        Assert.Equal("MPEG-TS", ts.FormatId);
        Assert.Equal(".ts", ts.RecommendedExtension);

        // 10. Bink Video (BIKi)
        var binkBytes = new byte[44];
        Encoding.ASCII.GetBytes("BIKi").CopyTo(binkBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(binkBytes.AsSpan(8, 4), 600); // 600 frames
        BinaryPrimitives.WriteInt32LittleEndian(binkBytes.AsSpan(20, 4), 1920); // width
        BinaryPrimitives.WriteInt32LittleEndian(binkBytes.AsSpan(24, 4), 1080); // height
        BinaryPrimitives.WriteUInt32LittleEndian(binkBytes.AsSpan(28, 4), 30); // fpsNum
        BinaryPrimitives.WriteUInt32LittleEndian(binkBytes.AsSpan(32, 4), 1); // fpsDen
        var bink = TextAssetBinaryFormatDetector.Detect(binkBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, bink.MediaKind);
        Assert.Equal("Bink", bink.FormatId);
        Assert.Equal(".bik", bink.RecommendedExtension);
        Assert.Equal(1920, bink.Width);
        Assert.Equal(1080, bink.Height);
        Assert.Equal(30.0, bink.FrameRate, 2);
        Assert.Equal(600, bink.TotalFrames);

        // 11. CRI Sofdec2 USM (CRID)
        var usmBytes = new byte[32];
        Encoding.ASCII.GetBytes("CRID").CopyTo(usmBytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(usmBytes.AsSpan(4, 4), 2048);
        var usm = TextAssetBinaryFormatDetector.Detect(usmBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Video, usm.MediaKind);
        Assert.Equal("USM", usm.FormatId);
        Assert.Equal(".usm", usm.RecommendedExtension);
    }

    [Fact]
    public void Detect_AllAudioFormats_WavOggVorbisOpusMp3FlacFsb5AiffMidiWwiseAndAac()
    {
        // 1. WAV (RIFF....WAVE with PCM fmt + data)
        var wavBytes = BuildSyntheticWavBytes(channels: 2, sampleRate: 48000, bitsPerSample: 16, pcmPayloadBytes: 96000);
        var wav = TextAssetBinaryFormatDetector.Detect(wavBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, wav.MediaKind);
        Assert.Equal("WAV", wav.FormatId);
        Assert.Equal(".wav", wav.RecommendedExtension);
        Assert.Equal("PCM", wav.AudioCodec);
        Assert.Equal(2, wav.Channels);
        Assert.Equal(48000, wav.SampleRate);
        Assert.Equal(16, wav.BitsPerSample);
        Assert.Equal(0.5, wav.DurationSeconds, 2);
        Assert.True(wav.IsPlayableAudio);

        // 2. OGG Vorbis (OggS + \x01vorbis)
        var oggVorbisBytes = BuildSyntheticOggVorbisBytes(channels: 2, sampleRate: 44100, nominalBitrate: 192000);
        var ogg = TextAssetBinaryFormatDetector.Detect(oggVorbisBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, ogg.MediaKind);
        Assert.Equal("OGG", ogg.FormatId);
        Assert.Equal(".ogg", ogg.RecommendedExtension);
        Assert.Equal("Vorbis", ogg.AudioCodec);
        Assert.Equal(2, ogg.Channels);
        Assert.Equal(44100, ogg.SampleRate);
        Assert.True(ogg.IsPlayableAudio);

        // 3. OGG Opus (OggS + OpusHead)
        var opusBytes = new byte[64];
        Encoding.ASCII.GetBytes("OggS").CopyTo(opusBytes, 0);
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(opusBytes, 28);
        opusBytes[36] = 1; // version
        opusBytes[37] = 2; // channels
        BinaryPrimitives.WriteUInt32LittleEndian(opusBytes.AsSpan(40, 4), 48000);
        var opus = TextAssetBinaryFormatDetector.Detect(opusBytes, "voice.opus");
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, opus.MediaKind);
        Assert.Equal("Opus", opus.FormatId);
        Assert.Equal(".opus", opus.RecommendedExtension);
        Assert.Equal("Opus", opus.AudioCodec);
        Assert.Equal(2, opus.Channels);
        Assert.Equal(48000, opus.SampleRate);

        // 4. MP3 with ID3v2 header + MPEG1 Layer 3 128kbps 44100Hz frame
        var mp3Id3Bytes = new byte[32];
        Encoding.ASCII.GetBytes("ID3").CopyTo(mp3Id3Bytes, 0);
        mp3Id3Bytes[3] = 3; // ID3v2.3
        // ID3 size = 10 -> frame at offset 20
        mp3Id3Bytes[9] = 10;
        mp3Id3Bytes[20] = 0xFF;
        mp3Id3Bytes[21] = 0xFB; // MPEG1 Layer3
        mp3Id3Bytes[22] = 0x90; // 128kbps, 44100Hz
        mp3Id3Bytes[23] = 0x00; // Stereo
        var mp3 = TextAssetBinaryFormatDetector.Detect(mp3Id3Bytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, mp3.MediaKind);
        Assert.Equal("MP3", mp3.FormatId);
        Assert.Equal(".mp3", mp3.RecommendedExtension);
        Assert.Equal(44100, mp3.SampleRate);
        Assert.Equal(2, mp3.Channels);
        Assert.True(mp3.IsPlayableAudio);

        // 5. Raw MP3 frame without ID3
        var rawMp3 = TextAssetBinaryFormatDetector.Detect(new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00 }, "bgm.mp3");
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, rawMp3.MediaKind);
        Assert.Equal("MP3", rawMp3.FormatId);

        // 6. FLAC (fLaC + STREAMINFO: 44100Hz, 2ch, 24-bit, 88200 samples = 2.0s)
        var flacBytes = BuildSyntheticFlacBytes(sampleRate: 44100, channels: 2, bitsPerSample: 24, totalSamples: 88200);
        var flac = TextAssetBinaryFormatDetector.Detect(flacBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, flac.MediaKind);
        Assert.Equal("FLAC", flac.FormatId);
        Assert.Equal(".flac", flac.RecommendedExtension);
        Assert.Equal(44100, flac.SampleRate);
        Assert.Equal(2, flac.Channels);
        Assert.Equal(24, flac.BitsPerSample);
        Assert.Equal(2.0, flac.DurationSeconds, 2);
        Assert.True(flac.IsPlayableAudio);

        // 7. FSB5 (FMOD Sample Bank v5 with Vorbis codec = 15, 2 subsounds, 48000Hz stereo)
        var fsb5Bytes = new byte[72];
        Encoding.ASCII.GetBytes("FSB5").CopyTo(fsb5Bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fsb5Bytes.AsSpan(4, 4), 1); // version 1 (60B header)
        BinaryPrimitives.WriteUInt32LittleEndian(fsb5Bytes.AsSpan(8, 4), 2); // 2 subsounds
        BinaryPrimitives.WriteUInt32LittleEndian(fsb5Bytes.AsSpan(24, 4), (uint)UnityAudioCodec.Vorbis);
        // Sample metadata uint64 at offset 60: freqId=9 (48000Hz -> bits 1..4 = 9<<1 = 18), chBits=1 (stereo -> 1<<5 = 32), sampleCount=96000 (<<34)
        ulong sampleMeta = (9UL << 1) | (1UL << 5) | (96000UL << 34);
        BinaryPrimitives.WriteUInt64LittleEndian(fsb5Bytes.AsSpan(60, 8), sampleMeta);
        var fsb = TextAssetBinaryFormatDetector.Detect(fsb5Bytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, fsb.MediaKind);
        Assert.Equal("FSB5", fsb.FormatId);
        Assert.Equal(".fsb", fsb.RecommendedExtension);
        Assert.Equal("Vorbis", fsb.AudioCodec);
        Assert.Equal(2, fsb.SubsoundCount);
        Assert.Equal(48000, fsb.SampleRate);
        Assert.Equal(2, fsb.Channels);
        Assert.Equal(2.0, fsb.DurationSeconds, 2);
        Assert.True(fsb.IsPlayableAudio);

        // 8. AIFF (FORM....AIFF + COMM chunk with 44100Hz 80-bit IEEE extended)
        var aiffBytes = new byte[38];
        Encoding.ASCII.GetBytes("FORM").CopyTo(aiffBytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aiffBytes.AsSpan(4, 4), 30);
        Encoding.ASCII.GetBytes("AIFFCOMM").CopyTo(aiffBytes, 8);
        BinaryPrimitives.WriteUInt32BigEndian(aiffBytes.AsSpan(16, 4), 18);
        BinaryPrimitives.WriteUInt16BigEndian(aiffBytes.AsSpan(20, 2), 2); // 2 channels
        BinaryPrimitives.WriteUInt32BigEndian(aiffBytes.AsSpan(22, 4), 44100); // 44100 frames
        BinaryPrimitives.WriteUInt16BigEndian(aiffBytes.AsSpan(26, 2), 16); // 16-bit
        // 44100.0 in 80-bit IEEE 754 extended: 40 0E AC 44 00 00 00 00 00 00
        new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }.CopyTo(aiffBytes, 28);
        var aiff = TextAssetBinaryFormatDetector.Detect(aiffBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, aiff.MediaKind);
        Assert.Equal("AIFF", aiff.FormatId);
        Assert.Equal(".aiff", aiff.RecommendedExtension);
        Assert.Equal(2, aiff.Channels);
        Assert.Equal(44100, aiff.SampleRate);
        Assert.Equal(16, aiff.BitsPerSample);
        Assert.Equal(1.0, aiff.DurationSeconds, 2);

        // 9. MIDI (MThd)
        var midiBytes = new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 1, 0, 8, 0x01, 0xE0 };
        var midi = TextAssetBinaryFormatDetector.Detect(midiBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, midi.MediaKind);
        Assert.Equal("MIDI", midi.FormatId);
        Assert.Equal(".mid", midi.RecommendedExtension);
        Assert.Equal(8, midi.StreamCount);

        // 10. Wwise SoundBank (BKHD)
        var bkhdBytes = new byte[20];
        Encoding.ASCII.GetBytes("BKHD").CopyTo(bkhdBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bkhdBytes.AsSpan(4, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bkhdBytes.AsSpan(8, 4), 134); // Wwise version
        BinaryPrimitives.WriteUInt32LittleEndian(bkhdBytes.AsSpan(12, 4), 0x12345678);
        var bnk = TextAssetBinaryFormatDetector.Detect(bkhdBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, bnk.MediaKind);
        Assert.Equal("WwiseBNK", bnk.FormatId);
        Assert.Equal(".bnk", bnk.RecommendedExtension);

        // 11. ADTS AAC (FF F1 50 80 02 1F FC -> MPEG-4 AAC LC, 44100Hz, 2ch, frameLen=16)
        var aacBytes = new byte[18];
        aacBytes[0] = 0xFF;
        aacBytes[1] = 0xF1;
        aacBytes[2] = 0x50; // freqIdx = 4 (44100Hz)
        aacBytes[3] = 0x80; // ch = 2, frameLen high = 0
        aacBytes[4] = 0x02; // frameLen mid = 2 -> 2<<3 = 16
        aacBytes[5] = 0x1F; // frameLen low = 0
        aacBytes[6] = 0xFC;
        aacBytes[16] = 0xFF;
        aacBytes[17] = 0xF1;
        var aac = TextAssetBinaryFormatDetector.Detect(aacBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Audio, aac.MediaKind);
        Assert.Equal("AAC", aac.FormatId);
        Assert.Equal(".aac", aac.RecommendedExtension);
        Assert.Equal(44100, aac.SampleRate);
        Assert.Equal(2, aac.Channels);
    }

    [Fact]
    public void Detect_AllImageFormats_PngJpegGifWebPBmpIcoTiffDdsKtxAndPsd_PlusDecoding()
    {
        // 1. PNG (IHDR 256x128 8-bit RGBA + actual SkiaSharp encode/decode test)
        using var srcBmp = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        srcBmp.Erase(new SKColor(40, 120, 220, 255));
        using var pngImg = SKImage.FromBitmap(srcBmp);
        using var pngData = pngImg.Encode(SKEncodedImageFormat.Png, 100);
        byte[] pngBytes = pngData.ToArray();

        var png = TextAssetBinaryFormatDetector.Detect(pngBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, png.MediaKind);
        Assert.Equal("PNG", png.FormatId);
        Assert.Equal(".png", png.RecommendedExtension);
        Assert.Equal(32, png.Width);
        Assert.Equal(16, png.Height);
        Assert.True(png.IsPreviewableImage);

        using var decodedPng = MainWindow.DecodeEmbeddedTextAssetImage(pngBytes, png, "test.png");
        Assert.NotNull(decodedPng);
        Assert.Equal(32, decodedPng!.Width);
        Assert.Equal(16, decodedPng.Height);

        // 2. JPEG (FF D8 FF + SOF0 marker 640x360 3-channel)
        var jpegBytes = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x68, 0x02, 0x80, 0x03 // precision=8, height=360 (0x0168), width=640 (0x0280), components=3
        };
        var jpeg = TextAssetBinaryFormatDetector.Detect(jpegBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, jpeg.MediaKind);
        Assert.Equal("JPEG", jpeg.FormatId);
        Assert.Equal(".jpg", jpeg.RecommendedExtension);
        Assert.Equal(640, jpeg.Width);
        Assert.Equal(360, jpeg.Height);
        Assert.Equal(3, jpeg.Channels);

        // 3. GIF (GIF89a 512x256)
        var gifBytes = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x00, 0x02, 0x00, 0x01, 0xF7, 0, 0 };
        var gif = TextAssetBinaryFormatDetector.Detect(gifBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, gif.MediaKind);
        Assert.Equal("GIF", gif.FormatId);
        Assert.Equal(".gif", gif.RecommendedExtension);
        Assert.Equal(512, gif.Width);
        Assert.Equal(256, gif.Height);

        // 4. WebP (VP8X 800x600)
        var webpBytes = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(webpBytes, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(webpBytes, 8);
        Encoding.ASCII.GetBytes("VP8X").CopyTo(webpBytes, 12);
        // width-1 = 799 (0x00031F), height-1 = 599 (0x000257)
        webpBytes[24] = 0x1F; webpBytes[25] = 0x03; webpBytes[26] = 0x00;
        webpBytes[27] = 0x57; webpBytes[28] = 0x02; webpBytes[29] = 0x00;
        var webp = TextAssetBinaryFormatDetector.Detect(webpBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, webp.MediaKind);
        Assert.Equal("WebP", webp.FormatId);
        Assert.Equal(".webp", webp.RecommendedExtension);
        Assert.Equal(800, webp.Width);
        Assert.Equal(600, webp.Height);

        // 5. BMP (BM + 40-byte BITMAPINFOHEADER 128x64 32bpp)
        var bmpBytes = new byte[54];
        bmpBytes[0] = (byte)'B'; bmpBytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmpBytes.AsSpan(10, 4), 54); // dataOffset
        BinaryPrimitives.WriteUInt32LittleEndian(bmpBytes.AsSpan(14, 4), 40); // dibSize
        BinaryPrimitives.WriteInt32LittleEndian(bmpBytes.AsSpan(18, 4), 128); // width
        BinaryPrimitives.WriteInt32LittleEndian(bmpBytes.AsSpan(22, 4), 64);  // height
        BinaryPrimitives.WriteUInt16LittleEndian(bmpBytes.AsSpan(28, 2), 32); // bpp
        var bmp = TextAssetBinaryFormatDetector.Detect(bmpBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, bmp.MediaKind);
        Assert.Equal("BMP", bmp.FormatId);
        Assert.Equal(".bmp", bmp.RecommendedExtension);
        Assert.Equal(128, bmp.Width);
        Assert.Equal(64, bmp.Height);
        Assert.Equal(32, bmp.BitsPerSample);

        // 6. ICO (00 00 01 00 + 1 entry 64x64 32bpp)
        var icoBytes = new byte[64];
        icoBytes[2] = 1; // ICO
        icoBytes[4] = 1; // 1 image
        icoBytes[6] = 64; // width
        icoBytes[7] = 64; // height
        BinaryPrimitives.WriteUInt16LittleEndian(icoBytes.AsSpan(12, 2), 32); // bpp
        BinaryPrimitives.WriteUInt32LittleEndian(icoBytes.AsSpan(14, 4), 40); // bytesInRes
        BinaryPrimitives.WriteUInt32LittleEndian(icoBytes.AsSpan(18, 4), 22); // imageOffset
        var ico = TextAssetBinaryFormatDetector.Detect(icoBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, ico.MediaKind);
        Assert.Equal("ICO", ico.FormatId);
        Assert.Equal(".ico", ico.RecommendedExtension);
        Assert.Equal(64, ico.Width);
        Assert.Equal(64, ico.Height);

        // 7. TIFF (II*\0 + IFD0 with ImageWidth=1024, ImageLength=512)
        var tiffBytes = new byte[34];
        tiffBytes[0] = (byte)'I'; tiffBytes[1] = (byte)'I'; tiffBytes[2] = 42; tiffBytes[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(tiffBytes.AsSpan(4, 4), 8); // IFD at 8
        BinaryPrimitives.WriteUInt16LittleEndian(tiffBytes.AsSpan(8, 2), 2); // 2 entries
        // Entry 0: Tag 0x0100 (Width), Type 4 (LONG), Count 1, Val 1024
        BinaryPrimitives.WriteUInt16LittleEndian(tiffBytes.AsSpan(10, 2), 0x0100);
        BinaryPrimitives.WriteUInt16LittleEndian(tiffBytes.AsSpan(12, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiffBytes.AsSpan(14, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiffBytes.AsSpan(18, 4), 1024);
        // Entry 1: Tag 0x0101 (Height), Type 4 (LONG), Count 1, Val 512
        BinaryPrimitives.WriteUInt16LittleEndian(tiffBytes.AsSpan(22, 2), 0x0101);
        BinaryPrimitives.WriteUInt16LittleEndian(tiffBytes.AsSpan(24, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiffBytes.AsSpan(26, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiffBytes.AsSpan(30, 4), 512);
        var tiff = TextAssetBinaryFormatDetector.Detect(tiffBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, tiff.MediaKind);
        Assert.Equal("TIFF", tiff.FormatId);
        Assert.Equal(".tif", tiff.RecommendedExtension);
        Assert.Equal(1024, tiff.Width);
        Assert.Equal(512, tiff.Height);

        // 8. DDS (DDS  4x4 DXT1 texture + actual decode to SKBitmap via Texture2DConverter!)
        var ddsBytes = new byte[128 + 8];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(ddsBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(ddsBytes.AsSpan(4, 4), 124); // dwSize
        BinaryPrimitives.WriteInt32LittleEndian(ddsBytes.AsSpan(12, 4), 4);   // height
        BinaryPrimitives.WriteInt32LittleEndian(ddsBytes.AsSpan(16, 4), 4);   // width
        BinaryPrimitives.WriteInt32LittleEndian(ddsBytes.AsSpan(28, 4), 1);   // mipMapCount
        BinaryPrimitives.WriteUInt32LittleEndian(ddsBytes.AsSpan(76, 4), 32); // ddspf.dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(ddsBytes.AsSpan(80, 4), 4);  // DDPF_FOURCC
        Encoding.ASCII.GetBytes("DXT1").CopyTo(ddsBytes, 84);
        // 8-byte solid white DXT1 block
        ddsBytes[128] = 0xFF; ddsBytes[129] = 0xFF; ddsBytes[130] = 0xFF; ddsBytes[131] = 0xFF;
        var dds = TextAssetBinaryFormatDetector.Detect(ddsBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, dds.MediaKind);
        Assert.Equal("DDS", dds.FormatId);
        Assert.Equal(".dds", dds.RecommendedExtension);
        Assert.Equal(4, dds.Width);
        Assert.Equal(4, dds.Height);
        Assert.Equal(TextureFormat.DXT1, dds.DdsTextureFormat);

        using var decodedDds = MainWindow.DecodeEmbeddedTextAssetImage(ddsBytes, dds, "icon.dds");
        Assert.NotNull(decodedDds);
        Assert.Equal(4, decodedDds!.Width);
        Assert.Equal(4, decodedDds.Height);

        // 9. KTX2
        var ktx2Bytes = new byte[48];
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(ktx2Bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(ktx2Bytes.AsSpan(20, 4), 512);
        BinaryPrimitives.WriteInt32LittleEndian(ktx2Bytes.AsSpan(24, 4), 512);
        BinaryPrimitives.WriteInt32LittleEndian(ktx2Bytes.AsSpan(40, 4), 10);
        var ktx2 = TextAssetBinaryFormatDetector.Detect(ktx2Bytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, ktx2.MediaKind);
        Assert.Equal("KTX2", ktx2.FormatId);
        Assert.Equal(".ktx2", ktx2.RecommendedExtension);
        Assert.Equal(512, ktx2.Width);
        Assert.Equal(512, ktx2.Height);
        Assert.Equal(10, ktx2.MipMapCount);

        // 10. PSD (8BPS v1 2048x1024 4ch 8-bit)
        var psdBytes = new byte[26];
        Encoding.ASCII.GetBytes("8BPS").CopyTo(psdBytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(psdBytes.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(psdBytes.AsSpan(12, 2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(psdBytes.AsSpan(14, 4), 1024);
        BinaryPrimitives.WriteUInt32BigEndian(psdBytes.AsSpan(18, 4), 2048);
        BinaryPrimitives.WriteUInt16BigEndian(psdBytes.AsSpan(22, 2), 8);
        var psd = TextAssetBinaryFormatDetector.Detect(psdBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Image, psd.MediaKind);
        Assert.Equal("PSD", psd.FormatId);
        Assert.Equal(".psd", psd.RecommendedExtension);
        Assert.Equal(2048, psd.Width);
        Assert.Equal(1024, psd.Height);
        Assert.Equal(4, psd.Channels);
    }

    [Fact]
    public void Detect_AllStructuredBinaryFormats_ZipGzip7zRarZstdLz4SqliteFontsPdfSwfUnityFsLuaWasmPeElfAndMsgPack()
    {
        // 1. ZIP with multiple entries
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e1 = archive.CreateEntry("configs/game.json");
                using (var s1 = e1.Open())
                {
                    s1.Write(Encoding.UTF8.GetBytes("{\"level\":1}"));
                }
                var e2 = archive.CreateEntry("scripts/init.lua");
                using (var s2 = e2.Open())
                {
                    s2.Write(Encoding.UTF8.GetBytes("print('ok')"));
                }
            }
            zipBytes = ms.ToArray();
        }
        var zip = TextAssetBinaryFormatDetector.Detect(zipBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.StructuredBinary, zip.MediaKind);
        Assert.Equal("ZIP", zip.FormatId);
        Assert.Equal(".zip", zip.RecommendedExtension);
        Assert.Equal(2, zip.ArchiveEntryCount);
        Assert.Equal(2, zip.ArchiveEntries.Count);
        Assert.Contains(zip.ArchiveEntries, e => e.Contains("configs/game.json", StringComparison.Ordinal));
        Assert.Contains(zip.ArchiveEntries, e => e.Contains("scripts/init.lua", StringComparison.Ordinal));

        var zipPreview = TextAssetPreviewBuilder.BuildPreview(CreateSyntheticAssetItem("pack.zip"), zipBytes, string.Empty);
        Assert.Contains("Archive entries (showing 2 of 2):", zipPreview.DetailsText, StringComparison.Ordinal);
        Assert.Contains("configs/game.json", zipPreview.DetailsText, StringComparison.Ordinal);

        // 2. GZIP (1F 8B 08 with FNAME flag 0x08)
        var gzList = new List<byte> { 0x1F, 0x8B, 0x08, 0x08, 0, 0, 0, 0, 0, 0 };
        gzList.AddRange(Encoding.ASCII.GetBytes("leveldata.bin\0"));
        gzList.AddRange(new byte[] { 1, 2, 3, 4, 0x00, 0x10, 0x00, 0x00 }); // trailing size = 4096
        var gz = TextAssetBinaryFormatDetector.Detect(gzList.ToArray());
        Assert.Equal(TextAssetEmbeddedMediaKind.StructuredBinary, gz.MediaKind);
        Assert.Equal("GZIP", gz.FormatId);
        Assert.Equal(".gz", gz.RecommendedExtension);
        Assert.Contains("leveldata.bin", gz.SummaryText, StringComparison.Ordinal);

        // 3. 7-Zip, RAR5, Zstandard, LZ4
        var sevenZ = TextAssetBinaryFormatDetector.Detect(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });
        Assert.Equal("7Z", sevenZ.FormatId);
        Assert.Equal(".7z", sevenZ.RecommendedExtension);

        var rar5 = TextAssetBinaryFormatDetector.Detect(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        Assert.Equal("RAR", rar5.FormatId);
        Assert.Equal(".rar", rar5.RecommendedExtension);

        var zstd = TextAssetBinaryFormatDetector.Detect(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x00 });
        Assert.Equal("ZSTD", zstd.FormatId);
        Assert.Equal(".zst", zstd.RecommendedExtension);

        var lz4 = TextAssetBinaryFormatDetector.Detect(new byte[] { 0x04, 0x22, 0x4D, 0x18, 0x60, 0x40 });
        Assert.Equal("LZ4", lz4.FormatId);
        Assert.Equal(".lz4", lz4.RecommendedExtension);

        // 4. SQLite 3 (SQLite format 3\0 + pageSize=4096, pageCount=25, encoding=1 UTF-8, userVersion=7)
        var sqliteBytes = new byte[100];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(sqliteBytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(sqliteBytes.AsSpan(16, 2), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(sqliteBytes.AsSpan(28, 4), 25);
        BinaryPrimitives.WriteUInt32BigEndian(sqliteBytes.AsSpan(44, 4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(sqliteBytes.AsSpan(56, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(sqliteBytes.AsSpan(60, 4), 7);
        BinaryPrimitives.WriteUInt32BigEndian(sqliteBytes.AsSpan(96, 4), 3045001);
        var sqlite = TextAssetBinaryFormatDetector.Detect(sqliteBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.StructuredBinary, sqlite.MediaKind);
        Assert.Equal("SQLite", sqlite.FormatId);
        Assert.Equal(".sqlite", sqlite.RecommendedExtension);
        Assert.Contains("4,096 bytes", sqlite.SummaryText, StringComparison.Ordinal);
        Assert.Contains("UTF-8", sqlite.SummaryText, StringComparison.Ordinal);

        // 5. Fonts: TTF, OTF, WOFF2
        var ttfBytes = new byte[28];
        ttfBytes[0] = 0x00; ttfBytes[1] = 0x01; ttfBytes[2] = 0x00; ttfBytes[3] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(ttfBytes.AsSpan(4, 2), 1); // 1 table
        Encoding.ASCII.GetBytes("head").CopyTo(ttfBytes, 12);
        var ttf = TextAssetBinaryFormatDetector.Detect(ttfBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.StructuredBinary, ttf.MediaKind);
        Assert.Equal("TTF", ttf.FormatId);
        Assert.Equal(".ttf", ttf.RecommendedExtension);
        Assert.True(ttf.IsRenderableFont);

        var otfBytes = new byte[28];
        Encoding.ASCII.GetBytes("OTTO").CopyTo(otfBytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(otfBytes.AsSpan(4, 2), 1);
        Encoding.ASCII.GetBytes("CFF ").CopyTo(otfBytes, 12);
        var otf = TextAssetBinaryFormatDetector.Detect(otfBytes);
        Assert.Equal("OTF", otf.FormatId);
        Assert.Equal(".otf", otf.RecommendedExtension);
        Assert.True(otf.IsRenderableFont);

        var woff2Bytes = new byte[20];
        Encoding.ASCII.GetBytes("wOF2").CopyTo(woff2Bytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(woff2Bytes.AsSpan(12, 2), 14);
        var woff2 = TextAssetBinaryFormatDetector.Detect(woff2Bytes);
        Assert.Equal("WOFF2", woff2.FormatId);
        Assert.Equal(".woff2", woff2.RecommendedExtension);
        Assert.False(woff2.IsRenderableFont);

        // 6. PDF, SWF, UnityFS AssetBundle
        var pdf = TextAssetBinaryFormatDetector.Detect(Encoding.ASCII.GetBytes("%PDF-1.7\n%...."));
        Assert.Equal("PDF", pdf.FormatId);
        Assert.Equal(".pdf", pdf.RecommendedExtension);

        var swfBytes = new byte[] { (byte)'C', (byte)'W', (byte)'S', 14, 0x00, 0x20, 0x00, 0x00 };
        var swf = TextAssetBinaryFormatDetector.Detect(swfBytes);
        Assert.Equal("SWF", swf.FormatId);
        Assert.Equal(".swf", swf.RecommendedExtension);

        var bundleList = new List<byte>();
        bundleList.AddRange(Encoding.ASCII.GetBytes("UnityFS\0"));
        bundleList.AddRange(new byte[] { 0, 0, 0, 7 }); // version 7
        bundleList.AddRange(Encoding.ASCII.GetBytes("5.x.x\02021.3.16f1\0"));
        var bundle = TextAssetBinaryFormatDetector.Detect(bundleList.ToArray());
        Assert.Equal("UnityFS", bundle.FormatId);
        Assert.Equal(".bundle", bundle.RecommendedExtension);
        Assert.Contains("2021.3.16f1", bundle.SummaryText, StringComparison.Ordinal);

        // 7. Lua Bytecode, WASM, PE/DLL, ELF, BSON, MessagePack
        var luaBc = TextAssetBinaryFormatDetector.Detect(new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a', 0x53, 0x00 });
        Assert.Equal("LuaBytecode", luaBc.FormatId);
        Assert.Equal(".luac", luaBc.RecommendedExtension);
        Assert.Contains("Lua 5.3", luaBc.FormatName, StringComparison.Ordinal);

        var wasm = TextAssetBinaryFormatDetector.Detect(new byte[] { 0x00, (byte)'a', (byte)'s', (byte)'m', 0x01, 0x00, 0x00, 0x00 });
        Assert.Equal("WASM", wasm.FormatId);
        Assert.Equal(".wasm", wasm.RecommendedExtension);

        var peBytes = new byte[128];
        peBytes[0] = (byte)'M'; peBytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(peBytes.AsSpan(0x3C, 4), 0x40);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(peBytes, 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(0x44, 2), 0x8664); // x86_64
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(0x46, 2), 5);      // 5 sections
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(0x56, 2), 0x2022); // DLL
        var pe = TextAssetBinaryFormatDetector.Detect(peBytes);
        Assert.Equal("PE", pe.FormatId);
        Assert.Equal(".dll", pe.RecommendedExtension);
        Assert.Contains("x86_64", pe.FormatName, StringComparison.Ordinal);

        var elfBytes = new byte[24];
        elfBytes[0] = 0x7F; elfBytes[1] = (byte)'E'; elfBytes[2] = (byte)'L'; elfBytes[3] = (byte)'F';
        elfBytes[4] = 2; // 64-bit
        elfBytes[5] = 1; // LE
        BinaryPrimitives.WriteUInt16LittleEndian(elfBytes.AsSpan(18, 2), 0xB7); // AArch64
        var elf = TextAssetBinaryFormatDetector.Detect(elfBytes);
        Assert.Equal("ELF", elf.FormatId);
        Assert.Equal(".so", elf.RecommendedExtension);
        Assert.Contains("AArch64", elf.FormatName, StringComparison.Ordinal);

        var msgPackBytes = new byte[] { 0x82, 0xA6, (byte)'p', (byte)'l', (byte)'a', (byte)'y', (byte)'e', (byte)'r', 0x01, 0xA2, (byte)'h', (byte)'p', 0x64 };
        var msgPack = TextAssetBinaryFormatDetector.Detect(msgPackBytes);
        Assert.Equal("MessagePack", msgPack.FormatId);
        Assert.Equal(".msgpack", msgPack.RecommendedExtension);
    }

    [Fact]
    public void Detect_TextFormatsAndExportExtensionRestoration_PreservesPlainTextAndDialogueCards()
    {
        // Plain UTF-8 JSON, XML, YAML, Shader, UTF-16LE BOM
        var jsonBytes = Encoding.UTF8.GetBytes("{\n  \"hero\": \"Bearer\",\n  \"level\": 42\n}");
        var json = TextAssetBinaryFormatDetector.Detect(jsonBytes);
        Assert.Equal(TextAssetEmbeddedMediaKind.Text, json.MediaKind);
        Assert.Equal("JSON", json.FormatId);
        Assert.False(json.IsBinaryFormat);

        var jsonItem = CreateSyntheticAssetItem("hero_stats");
        var jsonPreview = TextAssetPreviewBuilder.BuildPreview(jsonItem, jsonBytes, string.Empty);
        Assert.Equal("{\n  \"hero\": \"Bearer\",\n  \"level\": 42\n}", jsonPreview.DetailsText);

        // UTF-16LE with BOM (contains NUL bytes every other byte, must decode cleanly as plain text!)
        var utf16Text = "Hello from UTF-16 TextAsset!\r\nLine 2.";
        var utf16Bytes = new List<byte> { 0xFF, 0xFE };
        utf16Bytes.AddRange(Encoding.Unicode.GetBytes(utf16Text));
        var utf16Preview = TextAssetPreviewBuilder.BuildPreview(CreateSyntheticAssetItem("utf16_note"), utf16Bytes.ToArray(), string.Empty);
        Assert.Equal(utf16Text, utf16Preview.DetailsText);
        Assert.Equal("UTF-16 Little-Endian Text", utf16Preview.FormatName);

        // Export extension restoration & duplicate extension stripping for embedded AVI TextAsset
        var aviBytes = BuildSyntheticAviBytes(1920, 1080, 16683, 944, "MJPG", 1001, 60000, false, "Lavf");
        var textAsset = (TextAsset)RuntimeHelpers.GetUninitializedObject(typeof(TextAsset));
        textAsset.m_Name = "intrologos1080.avi";
        textAsset.m_PathID = 60;
        textAsset.m_Script = aviBytes;
        var assetItem = new AssetItem(textAsset)
        {
            Name = "intrologos1080.avi",
            Container = string.Empty,
            UniqueID = "sharedassets0.assets#60"
        };

        var exportOptions = new ExportOptionsState { RestoreExtensionName = true };
        Assert.Equal(".avi", AssetExportService.GetTextAssetExtension(assetItem, exportOptions));

        var detected = TextAssetBinaryFormatDetector.Detect(aviBytes, assetItem.Name);
        var syntheticVideo = MainWindow.CreateSyntheticVideoClipForTextAsset(textAsset, assetItem.Name, aviBytes, detected);
        Assert.Equal("intrologos1080", syntheticVideo.m_Name);
        Assert.Equal("intrologos1080.avi", syntheticVideo.m_OriginalPath);
        Assert.Equal(1920u, syntheticVideo.m_Width);
        Assert.Equal(1080u, syntheticVideo.m_Height);
        Assert.Equal(944ul, syntheticVideo.m_FrameCount);
        Assert.Equal(aviBytes.Length, syntheticVideo.m_ExternalResources.m_Size);

        var descriptor = MainWindow.BuildTextAssetEmbeddedVideoDescriptor(assetItem, textAsset, detected);
        Assert.Contains("intrologos1080.avi", descriptor.Title, StringComparison.Ordinal);
        Assert.Equal(".avi", descriptor.Extension);
        Assert.Contains("1920x1080", descriptor.ResolutionText, StringComparison.Ordinal);
        Assert.Contains("MJPG", descriptor.FormatLabel, StringComparison.Ordinal);
    }

    private static AssetItem CreateSyntheticAssetItem(string name)
    {
        var item = (AssetItem)RuntimeHelpers.GetUninitializedObject(typeof(AssetItem));
        item.Name = name;
        item.Container = string.Empty;
        item.TypeString = nameof(ClassIDType.TextAsset);
        item.Type = ClassIDType.TextAsset;
        return item;
    }

    private static byte[] BuildSyntheticAviBytes(
        int width,
        int height,
        uint microSecPerFrame,
        uint totalFrames,
        string videoCodecFourCc,
        uint rateScale,
        uint rateValue,
        bool includeMp3AudioStream,
        string encoderSoftware)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(2048u);
        bw.Write(Encoding.ASCII.GetBytes("AVI "));

        // LIST hdrl
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(512u);
        bw.Write(Encoding.ASCII.GetBytes("hdrl"));

        // avih (56 bytes)
        bw.Write(Encoding.ASCII.GetBytes("avih"));
        bw.Write(56u);
        bw.Write(microSecPerFrame);
        bw.Write(25000u); // maxBytesPerSec
        bw.Write(0u);     // padding
        bw.Write(0x910u); // flags
        bw.Write(totalFrames);
        bw.Write(0u);     // initialFrames
        bw.Write(includeMp3AudioStream ? 2u : 1u); // streams
        bw.Write(0x100000u); // suggestedBufferSize
        bw.Write(width);
        bw.Write(height);
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);

        // Video LIST strl
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(116u);
        bw.Write(Encoding.ASCII.GetBytes("strl"));

        // strh (56 bytes)
        bw.Write(Encoding.ASCII.GetBytes("strh"));
        bw.Write(56u);
        bw.Write(Encoding.ASCII.GetBytes("vids"));
        bw.Write(Encoding.ASCII.GetBytes(videoCodecFourCc.PadRight(4).Substring(0, 4)));
        bw.Write(0u); // flags
        bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write(0u); // initialFrames
        bw.Write(rateScale);
        bw.Write(rateValue);
        bw.Write(0u); // start
        bw.Write(totalFrames);
        bw.Write(0x100000u);
        bw.Write(10000u);
        bw.Write(0u);
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)width); bw.Write((short)height);

        // strf BITMAPINFOHEADER (40 bytes)
        bw.Write(Encoding.ASCII.GetBytes("strf"));
        bw.Write(40u);
        bw.Write(40u);
        bw.Write(width);
        bw.Write(height);
        bw.Write((ushort)1);
        bw.Write((ushort)24);
        bw.Write(Encoding.ASCII.GetBytes(videoCodecFourCc.PadRight(4).Substring(0, 4)));
        bw.Write((uint)(width * height * 3));
        bw.Write(0); bw.Write(0); bw.Write(0u); bw.Write(0u);

        if (includeMp3AudioStream)
        {
            bw.Write(Encoding.ASCII.GetBytes("LIST"));
            bw.Write(92u);
            bw.Write(Encoding.ASCII.GetBytes("strl"));

            bw.Write(Encoding.ASCII.GetBytes("strh"));
            bw.Write(56u);
            bw.Write(Encoding.ASCII.GetBytes("auds"));
            bw.Write(0u);
            for (int i = 0; i < 12; i++) bw.Write(0u);

            bw.Write(Encoding.ASCII.GetBytes("strf"));
            bw.Write(16u);
            bw.Write((ushort)0x0055); // MP3
            bw.Write((ushort)2);      // 2 channels
            bw.Write(44100u);         // 44100 Hz
            bw.Write(16000u);         // avgBytesPerSec
            bw.Write((ushort)1);      // blockAlign
            bw.Write((ushort)16);     // bitsPerSample
        }

        if (!string.IsNullOrEmpty(encoderSoftware))
        {
            byte[] isftBytes = Encoding.ASCII.GetBytes(encoderSoftware + "\0");
            bw.Write(Encoding.ASCII.GetBytes("LIST"));
            bw.Write((uint)(12 + isftBytes.Length));
            bw.Write(Encoding.ASCII.GetBytes("INFO"));
            bw.Write(Encoding.ASCII.GetBytes("ISFT"));
            bw.Write((uint)isftBytes.Length);
            bw.Write(isftBytes);
            if ((isftBytes.Length & 1) != 0) bw.Write((byte)0);
        }

        // LIST movi
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(16u);
        bw.Write(Encoding.ASCII.GetBytes("movi"));
        bw.Write(Encoding.ASCII.GetBytes("00dc"));
        bw.Write(4u);
        bw.Write(0xD8FFu);

        return ms.ToArray();
    }

    private static byte[] BuildSyntheticMp4Bytes(string majorBrand, int width, int height, uint timeScale, uint duration)
    {
        var buf = new byte[256];
        // ftyp box (24 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 24);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(buf, 4);
        Encoding.ASCII.GetBytes(majorBrand.PadRight(4).Substring(0, 4)).CopyTo(buf, 8);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 512);
        Encoding.ASCII.GetBytes("isommp42").CopyTo(buf, 16);

        // mvhd at 28
        Encoding.ASCII.GetBytes("mvhd").CopyTo(buf, 28);
        buf[32] = 0; // version 0
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(44, 4), timeScale);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(48, 4), duration);

        // tkhd at 60
        Encoding.ASCII.GetBytes("tkhd").CopyTo(buf, 60);
        buf[64] = 0; // version 0 -> dimOffset at 64 + 76 = 140
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(140, 4), (uint)(width << 16));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(144, 4), (uint)(height << 16));

        // codec FourCCs
        Encoding.ASCII.GetBytes("avc1").CopyTo(buf, 160);
        Encoding.ASCII.GetBytes("mp4a").CopyTo(buf, 180);
        return buf;
    }

    private static byte[] BuildSyntheticOggTheoraBytes(int width, int height, uint fpsNum, uint fpsDen, bool includeVorbis)
    {
        var buf = new byte[160];
        Encoding.ASCII.GetBytes("OggS").CopyTo(buf, 0);
        int t = 28;
        buf[t] = 0x80;
        Encoding.ASCII.GetBytes("theora").CopyTo(buf, t + 1);
        buf[t + 7] = 3; buf[t + 8] = 2; buf[t + 9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(t + 10, 2), (ushort)((width + 15) / 16));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(t + 12, 2), (ushort)((height + 15) / 16));
        buf[t + 14] = (byte)((width >> 16) & 0xFF);
        buf[t + 15] = (byte)((width >> 8) & 0xFF);
        buf[t + 16] = (byte)(width & 0xFF);
        buf[t + 17] = (byte)((height >> 16) & 0xFF);
        buf[t + 18] = (byte)((height >> 8) & 0xFF);
        buf[t + 19] = (byte)(height & 0xFF);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(t + 22, 4), fpsNum);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(t + 26, 4), fpsDen);

        if (includeVorbis)
        {
            int v = 96;
            buf[v] = 0x01;
            Encoding.ASCII.GetBytes("vorbis").CopyTo(buf, v + 1);
            buf[v + 11] = 2; // 2ch
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(v + 12, 4), 48000);
        }

        return buf;
    }

    private static byte[] BuildSyntheticOggVorbisBytes(byte channels, uint sampleRate, int nominalBitrate)
    {
        var buf = new byte[64];
        Encoding.ASCII.GetBytes("OggS").CopyTo(buf, 0);
        int v = 28;
        buf[v] = 0x01;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(buf, v + 1);
        buf[v + 11] = channels;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(v + 12, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(v + 20, 4), nominalBitrate);
        return buf;
    }

    private static byte[] BuildSyntheticWavBytes(ushort channels, uint sampleRate, ushort bitsPerSample, uint pcmPayloadBytes)
    {
        var buf = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 36 + pcmPayloadBytes);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(buf, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), sampleRate);
        uint byteRate = sampleRate * channels * (uint)(bitsPerSample / 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), (ushort)(channels * (bitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), pcmPayloadBytes);
        return buf;
    }

    private static byte[] BuildSyntheticFlacBytes(int sampleRate, int channels, int bitsPerSample, long totalSamples)
    {
        var buf = new byte[42];
        Encoding.ASCII.GetBytes("fLaC").CopyTo(buf, 0);
        buf[4] = 0x80; // Last-metadata-block + STREAMINFO (0)
        buf[5] = 0; buf[6] = 0; buf[7] = 34;
        // STREAMINFO at 8..42; bytes 18..25 pack sampleRate (20b), channels-1 (3b), bps-1 (5b), totalSamples (36b)
        ulong packed = ((ulong)sampleRate << 44)
            | ((ulong)((channels - 1) & 0x07) << 41)
            | ((ulong)((bitsPerSample - 1) & 0x1F) << 36)
            | ((ulong)totalSamples & 0xFFFFFFFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(18, 8), packed);
        return buf;
    }
}
