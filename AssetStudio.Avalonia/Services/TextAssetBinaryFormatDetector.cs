using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using AssetStudio;

namespace AssetStudio.Avalonia.Services;

public enum TextAssetEmbeddedMediaKind
{
    Unknown = 0,
    Text = 1,
    Video = 2,
    Audio = 3,
    Image = 4,
    StructuredBinary = 5,
    PlainText = Text
}

public sealed class TextAssetFormatDetectionResult
{
    public required TextAssetEmbeddedMediaKind MediaKind { get; init; }
    public required string FormatId { get; init; }
    public required string FormatName { get; init; }
    public required string RecommendedExtension { get; init; }
    public required string MimeType { get; init; }
    public int ByteSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public int TotalFrames { get; init; }
    public double DurationSeconds { get; init; }
    public string VideoCodec { get; init; } = string.Empty;
    public string AudioCodec { get; init; } = string.Empty;
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public int SubsoundCount { get; init; }
    public int StreamCount { get; init; }
    public int MipMapCount { get; init; }
    public TextureFormat? DdsTextureFormat { get; init; }
    public int DdsDataOffset { get; init; }
    public int ArchiveEntryCount { get; init; }
    public IReadOnlyList<string> ArchiveEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KeyValuePair<string, string>> MetadataFields { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    public bool IsPlayableVideo { get; init; }
    public bool IsPlayableAudio { get; init; }
    public bool IsPreviewableImage { get; init; }
    public bool IsRenderableFont { get; init; }
    public string SummaryText { get; init; } = string.Empty;

    public bool IsDetected => MediaKind != TextAssetEmbeddedMediaKind.Unknown;

    public bool IsBinaryFormat =>
        MediaKind is TextAssetEmbeddedMediaKind.Video
            or TextAssetEmbeddedMediaKind.Audio
            or TextAssetEmbeddedMediaKind.Image
            or TextAssetEmbeddedMediaKind.StructuredBinary;
}

/// <summary>
/// Pure C# O(1) header and magic-byte detector for multimedia, archives, fonts, databases,
/// bytecode, and structured binary containers embedded inside Unity <see cref="TextAsset"/> objects.
/// Never scans full multi-megabyte payloads on the calling thread.
/// </summary>
public static class TextAssetBinaryFormatDetector
{
    private const int MaxHeaderScanBytes = 64 * 1024;
    private const int MaxZipInspectBytes = 64 * 1024 * 1024;
    private const int MaxListedZipEntries = 32;

    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] AsfGuid = { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };
    private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
    private static readonly byte[] RarMagic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 };
    private static readonly byte[] SqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly byte[] Ktx1Magic = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Ktx2Magic = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    public static TextAssetFormatDetectionResult Detect(
        byte[]? data,
        string? assetNameHint = null,
        string? containerHint = null,
        bool inspectArchiveEntries = true)
    {
        data ??= Array.Empty<byte>();
        var extHint = ExtractExtensionHint(assetNameHint, containerHint);

        if (data.Length == 0)
        {
            return BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "Empty",
                formatName: "Empty TextAsset",
                extension: !string.IsNullOrEmpty(extHint) ? extHint : ".txt",
                mimeType: "text/plain",
                byteSize: 0);
        }

        // 1. RIFF family: AVI, WAV, WebP
        if (data.Length >= 12 && MatchesAscii(data, 0, "RIFF"))
        {
            if (MatchesAscii(data, 8, "AVI ") || MatchesAscii(data, 8, "AVIX"))
            {
                return ParseAvi(data);
            }

            if (MatchesAscii(data, 8, "WAVE"))
            {
                return ParseWav(data);
            }

            if (MatchesAscii(data, 8, "WEBP"))
            {
                return ParseWebP(data);
            }
        }

        // 2. Ogg family: Ogg Theora/VP8/Dirac Video vs Ogg Vorbis/Opus/FLAC Audio
        if (data.Length >= 4 && MatchesAscii(data, 0, "OggS"))
        {
            return ParseOgg(data, extHint);
        }

        // 3. Video containers: MP4/MOV/M4V/3GP (ftyp), WebM/MKV (EBML), FLV, ASF/WMV, Bink, CRI USM, MPEG-PS, MPEG-TS
        if (TryDetectIsoBmff(data, extHint, out var isoResult))
        {
            return isoResult;
        }

        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            return ParseEbmlMatroskaOrWebM(data, extHint);
        }

        if (data.Length >= 4 && data[0] == (byte)'F' && data[1] == (byte)'L' && data[2] == (byte)'V' && data[3] == 0x01)
        {
            return ParseFlv(data);
        }

        if (data.Length >= 16 && data.AsSpan(0, 16).SequenceEqual(AsfGuid))
        {
            return ParseAsf(data, extHint);
        }

        if (TryDetectBink(data, out var binkResult))
        {
            return binkResult;
        }

        if (data.Length >= 4 && MatchesAscii(data, 0, "CRID"))
        {
            return ParseCriUsm(data);
        }

        if (TryDetectMpegPs(data, out var mpegPsResult))
        {
            return mpegPsResult;
        }

        // 4. Audio containers: FLAC, FSB5, AIFF/AIFC, MIDI, Wwise BNK/AKPK, ID3/MP3, ADTS AAC
        if (data.Length >= 4 && MatchesAscii(data, 0, "fLaC"))
        {
            return ParseFlac(data);
        }

        if (data.Length >= 4 && MatchesAscii(data, 0, "FSB5"))
        {
            return ParseFsb5(data);
        }

        if (data.Length >= 12 && MatchesAscii(data, 0, "FORM") && (MatchesAscii(data, 8, "AIFF") || MatchesAscii(data, 8, "AIFC")))
        {
            return ParseAiff(data);
        }

        if (data.Length >= 14 && MatchesAscii(data, 0, "MThd") && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) == 6)
        {
            return ParseMidi(data);
        }

        if (data.Length >= 8 && (MatchesAscii(data, 0, "BKHD") || MatchesAscii(data, 0, "AKPK")))
        {
            return ParseWwiseBank(data);
        }

        if (data.Length >= 3 && MatchesAscii(data, 0, "ID3"))
        {
            return ParseMp3WithId3(data);
        }

        // 5. Image formats: PNG, JPEG, GIF, DDS, KTX/KTX2, PSD, TIFF, BMP, ICO/CUR
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(PngMagic))
        {
            return ParsePng(data);
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ParseJpeg(data);
        }

        if (data.Length >= 10 && (MatchesAscii(data, 0, "GIF87a") || MatchesAscii(data, 0, "GIF89a")))
        {
            return ParseGif(data);
        }

        if (data.Length >= 128 && MatchesAscii(data, 0, "DDS ") && ReadUInt32LE(data, 4) == 124)
        {
            return ParseDds(data);
        }

        if (data.Length >= 12 && (data.AsSpan(0, 12).SequenceEqual(Ktx1Magic) || data.AsSpan(0, 12).SequenceEqual(Ktx2Magic)))
        {
            return ParseKtx(data);
        }

        if (data.Length >= 26 && MatchesAscii(data, 0, "8BPS"))
        {
            ushort version = ReadUInt16BE(data, 4);
            if (version is 1 or 2)
            {
                return ParsePsd(data, version);
            }
        }

        if (data.Length >= 8
            && ((data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 42 && data[3] == 0)
                || (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0 && data[3] == 42)))
        {
            return ParseTiff(data);
        }

        if (TryDetectBmp(data, out var bmpResult))
        {
            return bmpResult;
        }

        if (TryDetectIcoOrCur(data, extHint, out var icoResult))
        {
            return icoResult;
        }

        // 6. Structured Binaries / Archives / Databases / Fonts / Documents / Bytecode
        if (data.Length >= 4
            && data[0] == (byte)'P'
            && data[1] == (byte)'K'
            && ((data[2] == 0x03 && data[3] == 0x04) || (data[2] == 0x05 && data[3] == 0x06)))
        {
            return ParseZip(data, extHint, inspectArchiveEntries);
        }

        if (data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 0x08)
        {
            return ParseGzip(data);
        }

        if (data.Length >= 6 && data.AsSpan(0, 6).SequenceEqual(SevenZipMagic))
        {
            return ParseSevenZip(data);
        }

        if (data.Length >= 7 && data.AsSpan(0, 6).SequenceEqual(RarMagic))
        {
            return ParseRar(data);
        }

        if (data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
        {
            return ParseZstandard(data);
        }

        if (data.Length >= 4 && data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
        {
            return ParseLz4(data);
        }

        if (data.Length >= 16 && data.AsSpan(0, 16).SequenceEqual(SqliteMagic))
        {
            return ParseSqlite(data);
        }

        if (TryDetectFont(data, extHint, out var fontResult))
        {
            return fontResult;
        }

        if (data.Length >= 5 && MatchesAscii(data, 0, "%PDF-"))
        {
            return ParsePdf(data);
        }

        if (TryDetectSwf(data, out var swfResult))
        {
            return swfResult;
        }

        if (TryDetectUnityBundle(data, out var bundleResult))
        {
            return bundleResult;
        }

        if (TryDetectLuaBytecode(data, out var luaBytecodeResult))
        {
            return luaBytecodeResult;
        }

        if (data.Length >= 8
            && data[0] == 0x00 && data[1] == (byte)'a' && data[2] == (byte)'s' && data[3] == (byte)'m'
            && data[4] == 0x01 && data[5] == 0x00 && data[6] == 0x00 && data[7] == 0x00)
        {
            return ParseWasm(data);
        }

        if (TryDetectPeExecutable(data, extHint, out var peResult))
        {
            return peResult;
        }

        if (data.Length >= 4 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F')
        {
            return ParseElf(data);
        }

        // 7. Frame-sync formats that require stricter multi-byte validation (ADTS AAC, MPEG Audio MP3, MPEG-TS)
        if (TryDetectAdtsAac(data, extHint, out var aacResult))
        {
            return aacResult;
        }

        if (TryDetectRawMp3Frame(data, extHint, out var mp3Result))
        {
            return mp3Result;
        }

        if (TryDetectMpegTs(data, extHint, out var mpegTsResult))
        {
            return mpegTsResult;
        }

        // 8. Structured binary data formats (BSON / MessagePack / FlatBuffers)
        if (TryDetectStructuredBinarySerialization(data, extHint, out var binaryDataResult))
        {
            return binaryDataResult;
        }

        // 9. Text & UTF-16 BOM / UTF-8 structured text formats (JSON, XML, SVG, YAML, Shader, Lua, CSV, Plain Text)
        if (TryDetectTextFormat(data, extHint, out var textResult))
        {
            return textResult;
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Unknown,
            formatId: "Binary",
            formatName: "Binary Data",
            extension: !string.IsNullOrEmpty(extHint) && extHint != ".txt" ? extHint : ".bytes",
            mimeType: "application/octet-stream",
            byteSize: data.Length);
    }

    private static TextAssetFormatDetectionResult ParseAvi(byte[] data)
    {
        int limit = Math.Min(data.Length, MaxHeaderScanBytes);
        int offset = 12;

        int avihWidth = 0;
        int avihHeight = 0;
        int strfWidth = 0;
        int strfHeight = 0;
        int totalFrames = 0;
        int streamFrames = 0;
        int streams = 0;
        uint maxBytesPerSec = 0;
        double avihFps = 0;
        double strhFps = 0;
        string videoCodec = string.Empty;
        string audioCodec = string.Empty;
        int audioChannels = 0;
        int audioSampleRate = 0;
        int audioBitsPerSample = 0;
        int videoBitDepth = 0;
        string softwareEncoder = string.Empty;
        string currentStreamType = string.Empty;

        while (offset + 8 <= limit)
        {
            string chunkId = Encoding.ASCII.GetString(data, offset, 4);
            uint chunkSize = ReadUInt32LE(data, offset + 4);

            if (chunkId == "LIST")
            {
                if (offset + 12 > limit)
                {
                    break;
                }

                string listType = Encoding.ASCII.GetString(data, offset + 8, 4);
                if (listType is "hdrl" or "strl" or "odml" or "INFO")
                {
                    if (listType == "strl")
                    {
                        currentStreamType = string.Empty;
                    }
                    offset += 12;
                    continue;
                }

                if (listType == "movi")
                {
                    break;
                }

                long nextList = (long)offset + 8 + chunkSize + (chunkSize & 1);
                if (nextList <= offset || nextList > limit)
                {
                    break;
                }
                offset = (int)nextList;
                continue;
            }

            int body = offset + 8;
            if (chunkId == "avih" && chunkSize >= 40 && body + 40 <= limit)
            {
                uint microSecPerFrame = ReadUInt32LE(data, body);
                maxBytesPerSec = ReadUInt32LE(data, body + 4);
                totalFrames = (int)ReadUInt32LE(data, body + 16);
                streams = (int)ReadUInt32LE(data, body + 24);
                avihWidth = ReadInt32LE(data, body + 32);
                avihHeight = Math.Abs(ReadInt32LE(data, body + 36));
                if (microSecPerFrame > 0)
                {
                    avihFps = 1_000_000.0 / microSecPerFrame;
                }
            }
            else if (chunkId == "strh" && chunkSize >= 8 && body + 8 <= limit)
            {
                currentStreamType = ReadCleanFourCC(data, body);
                string handler = ReadCleanFourCC(data, body + 4);
                if (currentStreamType == "vids")
                {
                    if (!string.IsNullOrWhiteSpace(handler) && string.IsNullOrEmpty(videoCodec))
                    {
                        videoCodec = handler;
                    }

                    if (chunkSize >= 36 && body + 36 <= limit)
                    {
                        uint scale = ReadUInt32LE(data, body + 20);
                        uint rate = ReadUInt32LE(data, body + 24);
                        uint length = ReadUInt32LE(data, body + 32);
                        if (scale > 0 && rate > 0)
                        {
                            double candidateFps = (double)rate / scale;
                            if (candidateFps is > 0.1 and < 1000.0)
                            {
                                strhFps = candidateFps;
                            }
                        }
                        if (length > 0 && streamFrames == 0)
                        {
                            streamFrames = (int)length;
                        }
                    }
                }
                else if (currentStreamType == "auds" && !string.IsNullOrWhiteSpace(handler) && string.IsNullOrEmpty(audioCodec))
                {
                    audioCodec = handler;
                }
            }
            else if (chunkId == "strf")
            {
                if (currentStreamType == "vids" && chunkSize >= 20 && body + 20 <= limit)
                {
                    int biWidth = ReadInt32LE(data, body + 4);
                    int biHeight = Math.Abs(ReadInt32LE(data, body + 8));
                    ushort biBitCount = ReadUInt16LE(data, body + 14);
                    string biCompression = ReadCleanFourCC(data, body + 16);
                    if (biWidth > 0 && strfWidth == 0)
                    {
                        strfWidth = biWidth;
                    }
                    if (biHeight > 0 && strfHeight == 0)
                    {
                        strfHeight = biHeight;
                    }
                    if (biBitCount > 0)
                    {
                        videoBitDepth = biBitCount;
                    }
                    if (!string.IsNullOrWhiteSpace(biCompression))
                    {
                        videoCodec = biCompression;
                    }
                }
                else if (currentStreamType == "auds" && chunkSize >= 14 && body + 14 <= limit)
                {
                    ushort formatTag = ReadUInt16LE(data, body);
                    audioChannels = ReadUInt16LE(data, body + 2);
                    audioSampleRate = (int)ReadUInt32LE(data, body + 4);
                    if (chunkSize >= 16 && body + 16 <= limit)
                    {
                        audioBitsPerSample = ReadUInt16LE(data, body + 14);
                    }
                    audioCodec = FormatWaveFormatTag(formatTag);
                }
            }
            else if (chunkId == "ISFT" && chunkSize > 0 && body < limit)
            {
                int len = (int)Math.Min(chunkSize, (uint)(limit - body));
                softwareEncoder = ReadCleanAsciiString(data, body, len);
            }

            long padded = (long)chunkSize + (chunkSize & 1);
            long nextOffset = (long)offset + 8 + padded;
            if (nextOffset <= offset || nextOffset > limit)
            {
                break;
            }
            offset = (int)nextOffset;
        }

        int finalWidth = avihWidth > 0 ? avihWidth : strfWidth;
        int finalHeight = avihHeight > 0 ? avihHeight : strfHeight;
        double finalFps = strhFps > 0 ? strhFps : avihFps;
        int finalFrames = totalFrames > 0 ? totalFrames : streamFrames;
        double duration = finalFrames > 0 && finalFps > 0 ? finalFrames / finalFps : 0;
        if (string.IsNullOrWhiteSpace(videoCodec))
        {
            videoCodec = "Raw/Unknown";
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "RIFF AVI (Audio Video Interleave)"),
            new("Video Codec", videoCodec)
        };
        if (finalWidth > 0 && finalHeight > 0)
        {
            fields.Add(new("Resolution", $"{finalWidth}x{finalHeight}"));
        }
        if (finalFps > 0)
        {
            fields.Add(new("Frame Rate", $"{finalFps.ToString("F2", CultureInfo.InvariantCulture)} FPS"));
        }
        if (finalFrames > 0)
        {
            fields.Add(new("Total Frames", finalFrames.ToString("N0", CultureInfo.InvariantCulture)));
        }
        if (duration > 0)
        {
            fields.Add(new("Estimated Duration", $"{duration.ToString("F2", CultureInfo.InvariantCulture)} s"));
        }
        if (streams > 0)
        {
            fields.Add(new("Stream Count", streams.ToString(CultureInfo.InvariantCulture)));
        }
        if (videoBitDepth > 0)
        {
            fields.Add(new("Color Depth", $"{videoBitDepth}-bit"));
        }
        if (!string.IsNullOrEmpty(audioCodec))
        {
            string audioDesc = audioCodec;
            if (audioChannels > 0) audioDesc += $", {audioChannels}ch";
            if (audioSampleRate > 0) audioDesc += $", {audioSampleRate} Hz";
            if (audioBitsPerSample > 0) audioDesc += $", {audioBitsPerSample}-bit";
            fields.Add(new("Audio Stream", audioDesc));
        }
        else
        {
            fields.Add(new("Audio Stream", "None"));
        }
        if (maxBytesPerSec > 0)
        {
            fields.Add(new("Max Data Rate", $"{maxBytesPerSec.ToString("N0", CultureInfo.InvariantCulture)} bytes/sec"));
        }
        if (!string.IsNullOrEmpty(softwareEncoder))
        {
            fields.Add(new("Encoder / Software", softwareEncoder));
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "AVI",
            formatName: $"AVI Video ({videoCodec})",
            extension: ".avi",
            mimeType: "video/x-msvideo",
            byteSize: data.Length,
            width: finalWidth,
            height: finalHeight,
            frameRate: finalFps,
            totalFrames: finalFrames,
            durationSeconds: duration,
            videoCodec: videoCodec,
            audioCodec: audioCodec,
            sampleRate: audioSampleRate,
            channels: audioChannels,
            bitsPerSample: audioBitsPerSample,
            streamCount: streams,
            isPlayableVideo: true,
            metadataFields: fields);
    }

    private static bool TryDetectIsoBmff(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 12 || !MatchesAscii(data, 4, "ftyp"))
        {
            return false;
        }

        uint ftypSize = ReadUInt32BE(data, 0);
        string majorBrand = ReadCleanFourCC(data, 8);
        var compatibleBrands = new List<string>();
        int ftypEnd = (int)Math.Min(data.Length, Math.Max(16u, Math.Min(ftypSize, 128u)));
        for (int pos = 16; pos + 4 <= ftypEnd; pos += 4)
        {
            string brand = ReadCleanFourCC(data, pos);
            if (!string.IsNullOrWhiteSpace(brand) && !compatibleBrands.Contains(brand))
            {
                compatibleBrands.Add(brand);
            }
        }

        // Check for M4A audio-only container vs MP4/MOV/M4V/3GP video
        bool isM4a = string.Equals(majorBrand, "M4A", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".m4a", StringComparison.OrdinalIgnoreCase);

        if (isM4a)
        {
            var audioFields = new List<KeyValuePair<string, string>>
            {
                new("Container", "ISO Base Media (MPEG-4 Audio / M4A)"),
                new("Major Brand", majorBrand)
            };
            if (compatibleBrands.Count > 0)
            {
                audioFields.Add(new("Compatible Brands", string.Join(", ", compatibleBrands)));
            }

            result = BuildResult(
                TextAssetEmbeddedMediaKind.Audio,
                formatId: "M4A",
                formatName: "MPEG-4 Audio (M4A)",
                extension: ".m4a",
                mimeType: "audio/mp4",
                byteSize: data.Length,
                audioCodec: "AAC / ALAC",
                isPlayableAudio: true,
                metadataFields: audioFields);
            return true;
        }

        string formatId = "MP4";
        string formatName = "MPEG-4 Video (MP4)";
        string extension = ".mp4";
        string mimeType = "video/mp4";

        if (string.Equals(majorBrand, "qt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".mov", StringComparison.OrdinalIgnoreCase))
        {
            formatId = "MOV";
            formatName = "QuickTime Movie (MOV)";
            extension = ".mov";
            mimeType = "video/quicktime";
        }
        else if (string.Equals(majorBrand, "M4V", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".m4v", StringComparison.OrdinalIgnoreCase))
        {
            formatId = "M4V";
            formatName = "MPEG-4 Video (M4V)";
            extension = ".m4v";
            mimeType = "video/x-m4v";
        }
        else if (majorBrand.StartsWith("3gp", StringComparison.OrdinalIgnoreCase)
            || majorBrand.StartsWith("3g2", StringComparison.OrdinalIgnoreCase))
        {
            formatId = "3GP";
            formatName = "3GPP Multimedia (3GP)";
            extension = ".3gp";
            mimeType = "video/3gpp";
        }

        // Scan first 64 KB for tkhd dimensions / mvhd duration / stsd codec tags if moov is near the start
        int width = 0;
        int height = 0;
        double durationSeconds = 0;
        string videoCodec = string.Empty;
        string audioCodec = string.Empty;
        ScanIsoBmffHeaderHints(data, out width, out height, out durationSeconds, out videoCodec, out audioCodec);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", $"ISO Base Media ({formatId})"),
            new("Major Brand", string.IsNullOrEmpty(majorBrand) ? "isom" : majorBrand)
        };
        if (compatibleBrands.Count > 0)
        {
            fields.Add(new("Compatible Brands", string.Join(", ", compatibleBrands)));
        }
        if (!string.IsNullOrEmpty(videoCodec))
        {
            fields.Add(new("Video Codec", videoCodec));
        }
        if (width > 0 && height > 0)
        {
            fields.Add(new("Resolution", $"{width}x{height}"));
        }
        if (durationSeconds > 0)
        {
            fields.Add(new("Duration", $"{durationSeconds.ToString("F2", CultureInfo.InvariantCulture)} s"));
        }
        if (!string.IsNullOrEmpty(audioCodec))
        {
            fields.Add(new("Audio Codec", audioCodec));
        }

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: formatId,
            formatName: formatName,
            extension: extension,
            mimeType: mimeType,
            byteSize: data.Length,
            width: width,
            height: height,
            durationSeconds: durationSeconds,
            videoCodec: videoCodec,
            audioCodec: audioCodec,
            isPlayableVideo: true,
            metadataFields: fields);
        return true;
    }

    private static void ScanIsoBmffHeaderHints(
        byte[] data,
        out int width,
        out int height,
        out double durationSeconds,
        out string videoCodec,
        out string audioCodec)
    {
        width = 0;
        height = 0;
        durationSeconds = 0;
        videoCodec = string.Empty;
        audioCodec = string.Empty;

        int limit = Math.Min(data.Length, MaxHeaderScanBytes);
        for (int i = 4; i + 8 <= limit; i++)
        {
            // mvhd box
            if (data[i] == (byte)'m' && data[i + 1] == (byte)'v' && data[i + 2] == (byte)'h' && data[i + 3] == (byte)'d')
            {
                int body = i + 4;
                if (body + 20 <= limit)
                {
                    byte version = data[body];
                    if (version == 0 && body + 20 <= limit)
                    {
                        uint timeScale = ReadUInt32BE(data, body + 12);
                        uint duration = ReadUInt32BE(data, body + 16);
                        if (timeScale > 0 && duration > 0 && duration != uint.MaxValue)
                        {
                            durationSeconds = (double)duration / timeScale;
                        }
                    }
                    else if (version == 1 && body + 32 <= limit)
                    {
                        uint timeScale = ReadUInt32BE(data, body + 20);
                        ulong duration = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(body + 24, 8));
                        if (timeScale > 0 && duration > 0 && duration != ulong.MaxValue)
                        {
                            durationSeconds = (double)duration / timeScale;
                        }
                    }
                }
            }
            // tkhd box
            else if (data[i] == (byte)'t' && data[i + 1] == (byte)'k' && data[i + 2] == (byte)'h' && data[i + 3] == (byte)'d')
            {
                int body = i + 4;
                if (body + 84 <= limit)
                {
                    byte version = data[body];
                    int dimOffset = version == 1 ? body + 88 : body + 76;
                    if (dimOffset + 8 <= limit)
                    {
                        int wFixed = (int)(ReadUInt32BE(data, dimOffset) >> 16);
                        int hFixed = (int)(ReadUInt32BE(data, dimOffset + 4) >> 16);
                        if (wFixed is > 0 and <= 16384 && hFixed is > 0 and <= 16384)
                        {
                            width = wFixed;
                            height = hFixed;
                        }
                    }
                }
            }
            // Video codec sample entry FourCCs
            else if (string.IsNullOrEmpty(videoCodec))
            {
                if (MatchesAscii(data, i, "avc1") || MatchesAscii(data, i, "avc3"))
                {
                    videoCodec = "H.264 / AVC";
                }
                else if (MatchesAscii(data, i, "hvc1") || MatchesAscii(data, i, "hev1"))
                {
                    videoCodec = "H.265 / HEVC";
                }
                else if (MatchesAscii(data, i, "vp09"))
                {
                    videoCodec = "VP9";
                }
                else if (MatchesAscii(data, i, "av01"))
                {
                    videoCodec = "AV1";
                }
                else if (MatchesAscii(data, i, "mp4v"))
                {
                    videoCodec = "MPEG-4 Visual";
                }
            }

            if (string.IsNullOrEmpty(audioCodec))
            {
                if (MatchesAscii(data, i, "mp4a"))
                {
                    audioCodec = "AAC";
                }
                else if (MatchesAscii(data, i, "Opus"))
                {
                    audioCodec = "Opus";
                }
                else if (MatchesAscii(data, i, "ac-3") || MatchesAscii(data, i, "ec-3"))
                {
                    audioCodec = "AC-3 / E-AC-3";
                }
            }
        }
    }

    private static TextAssetFormatDetectionResult ParseEbmlMatroskaOrWebM(byte[] data, string extHint)
    {
        int limit = Math.Min(data.Length, 8192);
        string headerAscii = Encoding.ASCII.GetString(data, 0, limit);
        bool isWebM = headerAscii.Contains("webm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".webm", StringComparison.OrdinalIgnoreCase);

        string videoCodec = string.Empty;
        if (headerAscii.Contains("V_VP9", StringComparison.Ordinal)) videoCodec = "VP9";
        else if (headerAscii.Contains("V_VP8", StringComparison.Ordinal)) videoCodec = "VP8";
        else if (headerAscii.Contains("V_AV1", StringComparison.Ordinal)) videoCodec = "AV1";
        else if (headerAscii.Contains("V_MPEG4/ISO/AVC", StringComparison.Ordinal)) videoCodec = "H.264 / AVC";
        else if (headerAscii.Contains("V_MPEGH/ISO/HEVC", StringComparison.Ordinal)) videoCodec = "H.265 / HEVC";

        string audioCodec = string.Empty;
        if (headerAscii.Contains("A_OPUS", StringComparison.Ordinal)) audioCodec = "Opus";
        else if (headerAscii.Contains("A_VORBIS", StringComparison.Ordinal)) audioCodec = "Vorbis";
        else if (headerAscii.Contains("A_AAC", StringComparison.Ordinal)) audioCodec = "AAC";

        int width = 0;
        int height = 0;
        for (int i = 0; i + 8 <= limit; i++)
        {
            // Matroska PixelWidth (0xB0) followed by 2-byte big-endian size (0x82) and PixelHeight (0xBA 0x82)
            if (data[i] == 0xB0 && data[i + 1] == 0x82 && i + 4 <= limit)
            {
                int w = ReadUInt16BE(data, i + 2);
                if (i + 8 <= limit && data[i + 4] == 0xBA && data[i + 5] == 0x82)
                {
                    int h = ReadUInt16BE(data, i + 6);
                    if (w is >= 16 and <= 16384 && h is >= 16 and <= 16384)
                    {
                        width = w;
                        height = h;
                        break;
                    }
                }
            }
        }

        string formatId = isWebM ? "WebM" : "MKV";
        string formatName = isWebM ? "WebM Video" : "Matroska Video (MKV)";
        string extension = isWebM ? ".webm" : ".mkv";
        string mimeType = isWebM ? "video/webm" : "video/x-matroska";

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", isWebM ? "EBML / WebM" : "EBML / Matroska (MKV)")
        };
        if (!string.IsNullOrEmpty(videoCodec))
        {
            fields.Add(new("Video Codec", videoCodec));
        }
        if (width > 0 && height > 0)
        {
            fields.Add(new("Resolution", $"{width}x{height}"));
        }
        if (!string.IsNullOrEmpty(audioCodec))
        {
            fields.Add(new("Audio Codec", audioCodec));
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: formatId,
            formatName: formatName,
            extension: extension,
            mimeType: mimeType,
            byteSize: data.Length,
            width: width,
            height: height,
            videoCodec: videoCodec,
            audioCodec: audioCodec,
            isPlayableVideo: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseOgg(byte[] data, string extHint)
    {
        int limit = Math.Min(data.Length, 16384);
        int theoraPos = FindPattern(data, limit, 0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a');
        int vorbisPos = FindPattern(data, limit, 0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s');
        int opusPos = FindAsciiPattern(data, limit, "OpusHead");
        int vp8Pos = FindAsciiPattern(data, limit, "OVP80");
        int diracPos = FindAsciiPattern(data, limit, "BBCD");

        // Parse Vorbis audio header if present
        int audioChannels = 0;
        int audioSampleRate = 0;
        int nominalBitrate = 0;
        string audioCodec = string.Empty;

        if (vorbisPos >= 0 && vorbisPos + 28 <= data.Length)
        {
            audioCodec = "Vorbis";
            audioChannels = data[vorbisPos + 11];
            audioSampleRate = (int)ReadUInt32LE(data, vorbisPos + 12);
            nominalBitrate = ReadInt32LE(data, vorbisPos + 20);
        }
        else if (opusPos >= 0 && opusPos + 16 <= data.Length)
        {
            audioCodec = "Opus";
            audioChannels = data[opusPos + 9];
            int inputRate = (int)ReadUInt32LE(data, opusPos + 12);
            audioSampleRate = inputRate > 0 ? inputRate : 48000;
        }

        // Check if this is an Ogg Theora / VP8 / Dirac video container
        bool isVideo = theoraPos >= 0 || vp8Pos >= 0 || diracPos >= 0
            || (string.Equals(extHint, ".ogv", StringComparison.OrdinalIgnoreCase) && vorbisPos < 0 && opusPos < 0);

        if (isVideo)
        {
            int width = 0;
            int height = 0;
            double fps = 0;
            string videoCodec = theoraPos >= 0 ? "Theora" : vp8Pos >= 0 ? "VP8" : diracPos >= 0 ? "Dirac" : "Theora";

            if (theoraPos >= 0 && theoraPos + 30 <= data.Length)
            {
                int fmbw = ReadUInt16BE(data, theoraPos + 10) * 16;
                int fmbh = ReadUInt16BE(data, theoraPos + 12) * 16;
                int picw = (data[theoraPos + 14] << 16) | (data[theoraPos + 15] << 8) | data[theoraPos + 16];
                int pich = (data[theoraPos + 17] << 16) | (data[theoraPos + 18] << 8) | data[theoraPos + 19];
                width = picw > 0 ? picw : fmbw;
                height = pich > 0 ? pich : fmbh;

                uint frn = ReadUInt32BE(data, theoraPos + 22);
                uint frd = ReadUInt32BE(data, theoraPos + 26);
                if (frn > 0 && frd > 0)
                {
                    double candidateFps = (double)frn / frd;
                    if (candidateFps is > 0.1 and < 1000.0)
                    {
                        fps = candidateFps;
                    }
                }
            }

            var videoFields = new List<KeyValuePair<string, string>>
            {
                new("Container", "Ogg Multimedia (OggS)"),
                new("Video Codec", videoCodec)
            };
            if (width > 0 && height > 0)
            {
                videoFields.Add(new("Resolution", $"{width}x{height}"));
            }
            if (fps > 0)
            {
                videoFields.Add(new("Frame Rate", $"{fps.ToString("F2", CultureInfo.InvariantCulture)} FPS"));
            }
            if (!string.IsNullOrEmpty(audioCodec))
            {
                videoFields.Add(new("Audio Stream", $"{audioCodec} ({audioChannels}ch, {audioSampleRate} Hz)"));
            }

            return BuildResult(
                TextAssetEmbeddedMediaKind.Video,
                formatId: "OGV",
                formatName: $"Ogg Video ({videoCodec})",
                extension: ".ogv",
                mimeType: "video/ogg",
                byteSize: data.Length,
                width: width,
                height: height,
                frameRate: fps,
                videoCodec: videoCodec,
                audioCodec: audioCodec,
                sampleRate: audioSampleRate,
                channels: audioChannels,
                isPlayableVideo: true,
                metadataFields: videoFields);
        }

        // Otherwise Ogg Audio (Vorbis / Opus)
        bool isOpus = string.Equals(audioCodec, "Opus", StringComparison.Ordinal);
        string finalAudioCodec = !string.IsNullOrEmpty(audioCodec) ? audioCodec : "Vorbis";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "Ogg Audio (OggS)"),
            new("Audio Codec", finalAudioCodec)
        };
        if (audioChannels > 0)
        {
            fields.Add(new("Channels", audioChannels.ToString(CultureInfo.InvariantCulture)));
        }
        if (audioSampleRate > 0)
        {
            fields.Add(new("Sample Rate", $"{audioSampleRate} Hz"));
        }
        if (nominalBitrate > 0)
        {
            fields.Add(new("Nominal Bitrate", $"{nominalBitrate / 1000} kbps"));
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: isOpus ? "Opus" : "OGG",
            formatName: isOpus ? "Ogg Opus Audio" : "Ogg Vorbis Audio",
            extension: isOpus && string.Equals(extHint, ".opus", StringComparison.OrdinalIgnoreCase) ? ".opus" : ".ogg",
            mimeType: "audio/ogg",
            byteSize: data.Length,
            audioCodec: finalAudioCodec,
            sampleRate: audioSampleRate,
            channels: audioChannels,
            isPlayableAudio: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseFlv(byte[] data)
    {
        byte flags = data.Length > 4 ? data[4] : (byte)0;
        bool hasVideo = (flags & 0x01) != 0;
        bool hasAudio = (flags & 0x04) != 0;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "Adobe Flash Video (FLV v1)"),
            new("Streams", $"Video: {(hasVideo ? "Yes" : "No")}, Audio: {(hasAudio ? "Yes" : "No")}")
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "FLV",
            formatName: "Flash Video (FLV)",
            extension: ".flv",
            mimeType: "video/x-flv",
            byteSize: data.Length,
            isPlayableVideo: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseAsf(byte[] data, string extHint)
    {
        bool isWma = string.Equals(extHint, ".wma", StringComparison.OrdinalIgnoreCase);
        if (isWma)
        {
            return BuildResult(
                TextAssetEmbeddedMediaKind.Audio,
                formatId: "WMA",
                formatName: "Windows Media Audio (ASF/WMA)",
                extension: ".wma",
                mimeType: "audio/x-ms-wma",
                byteSize: data.Length,
                audioCodec: "WMA",
                isPlayableAudio: true,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Container", "Advanced Systems Format (ASF / WMA)")
                });
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "WMV",
            formatName: "Windows Media Video (ASF/WMV)",
            extension: ".wmv",
            mimeType: "video/x-ms-wmv",
            byteSize: data.Length,
            videoCodec: "WMV",
            isPlayableVideo: true,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", "Advanced Systems Format (ASF / WMV)")
            });
    }

    private static bool TryDetectBink(byte[] data, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 4)
        {
            return false;
        }

        bool isBink1 = data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'K' && data[3] is >= (byte)'a' and <= (byte)'z';
        bool isBink2 = data[0] == (byte)'K' && data[1] == (byte)'B' && data[2] == (byte)'2' && data[3] is >= (byte)'a' and <= (byte)'z';
        if (!isBink1 && !isBink2)
        {
            return false;
        }

        string sig = Encoding.ASCII.GetString(data, 0, 4);
        int frames = data.Length >= 12 ? (int)ReadUInt32LE(data, 8) : 0;
        int width = data.Length >= 24 ? ReadInt32LE(data, 20) : 0;
        int height = data.Length >= 28 ? ReadInt32LE(data, 24) : 0;
        double fps = 0;
        if (data.Length >= 36)
        {
            uint fpsNum = ReadUInt32LE(data, 28);
            uint fpsDen = ReadUInt32LE(data, 32);
            if (fpsNum > 0 && fpsDen > 0)
            {
                double candidate = (double)fpsNum / fpsDen;
                if (candidate is > 0.1 and < 1000.0)
                {
                    fps = candidate;
                }
            }
        }

        if (width is < 0 or > 32768) width = 0;
        if (height is < 0 or > 32768) height = 0;
        if (frames < 0) frames = 0;

        string codecName = isBink2 ? $"Bink 2 ({sig})" : $"Bink 1 ({sig})";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "RAD Game Tools Bink Video"),
            new("Video Codec", codecName)
        };
        if (width > 0 && height > 0)
        {
            fields.Add(new("Resolution", $"{width}x{height}"));
        }
        if (fps > 0)
        {
            fields.Add(new("Frame Rate", $"{fps.ToString("F2", CultureInfo.InvariantCulture)} FPS"));
        }
        if (frames > 0)
        {
            fields.Add(new("Total Frames", frames.ToString("N0", CultureInfo.InvariantCulture)));
        }

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "Bink",
            formatName: $"Bink Video ({sig})",
            extension: isBink2 ? ".bk2" : ".bik",
            mimeType: "video/vnd.radgamettools.bink",
            byteSize: data.Length,
            width: width,
            height: height,
            frameRate: fps,
            totalFrames: frames,
            durationSeconds: frames > 0 && fps > 0 ? frames / fps : 0,
            videoCodec: codecName,
            isPlayableVideo: isBink1,
            metadataFields: fields);
        return true;
    }

    private static TextAssetFormatDetectionResult ParseCriUsm(byte[] data)
    {
        uint headerSize = data.Length >= 8 ? ReadUInt32BE(data, 4) : 0;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "CRI Middleware Sofdec2 (USM / CRID)"),
            new("Header Chunk Size", $"{headerSize.ToString("N0", CultureInfo.InvariantCulture)} bytes")
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "USM",
            formatName: "CRI Sofdec2 Video (USM)",
            extension: ".usm",
            mimeType: "video/x-cri-usm",
            byteSize: data.Length,
            videoCodec: "CRI Sofdec2",
            isPlayableVideo: false,
            metadataFields: fields);
    }

    private static bool TryDetectMpegPs(byte[] data, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 12 || data[0] != 0x00 || data[1] != 0x00 || data[2] != 0x01)
        {
            return false;
        }

        if (data[3] != 0xBA && data[3] != 0xB3)
        {
            return false;
        }

        int width = 0;
        int height = 0;
        double fps = 0;
        int limit = Math.Min(data.Length, 4096);
        int seqPos = FindPattern(data, limit, 0x00, 0x00, 0x01, 0xB3);
        if (seqPos >= 0 && seqPos + 8 <= data.Length)
        {
            width = (data[seqPos + 4] << 4) | (data[seqPos + 5] >> 4);
            height = ((data[seqPos + 5] & 0x0F) << 8) | data[seqPos + 6];
            int fpsCode = data[seqPos + 7] & 0x0F;
            fps = fpsCode switch
            {
                1 => 23.976,
                2 => 24.0,
                3 => 25.0,
                4 => 29.97,
                5 => 30.0,
                6 => 50.0,
                7 => 59.94,
                8 => 60.0,
                _ => 0
            };
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "MPEG Program Stream (MPEG-PS)"),
            new("Video Codec", "MPEG-1 / MPEG-2")
        };
        if (width > 0 && height > 0)
        {
            fields.Add(new("Resolution", $"{width}x{height}"));
        }
        if (fps > 0)
        {
            fields.Add(new("Frame Rate", $"{fps.ToString("F2", CultureInfo.InvariantCulture)} FPS"));
        }

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "MPEG-PS",
            formatName: "MPEG Program Stream Video",
            extension: ".mpg",
            mimeType: "video/mpeg",
            byteSize: data.Length,
            width: width,
            height: height,
            frameRate: fps,
            videoCodec: "MPEG",
            isPlayableVideo: true,
            metadataFields: fields);
        return true;
    }

    private static bool TryDetectMpegTs(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        bool matches188 = data.Length >= 377
            && data[0] == 0x47
            && data[188] == 0x47
            && data[376] == 0x47;
        bool matchesShortWithHint = data.Length >= 189
            && data[0] == 0x47
            && data[188] == 0x47
            && (string.Equals(extHint, ".ts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extHint, ".m2ts", StringComparison.OrdinalIgnoreCase));

        if (!matches188 && !matchesShortWithHint)
        {
            return false;
        }

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Video,
            formatId: "MPEG-TS",
            formatName: "MPEG Transport Stream (MPEG-TS)",
            extension: ".ts",
            mimeType: "video/mp2t",
            byteSize: data.Length,
            videoCodec: "MPEG-TS",
            isPlayableVideo: true,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", "MPEG-2 Transport Stream (188-byte packets)")
            });
        return true;
    }

    private static TextAssetFormatDetectionResult ParseWav(byte[] data)
    {
        int limit = Math.Min(data.Length, MaxHeaderScanBytes);
        int offset = 12;
        ushort formatTag = 1;
        int channels = 0;
        int sampleRate = 0;
        int byteRate = 0;
        int bitsPerSample = 0;
        long dataChunkSize = 0;

        while (offset + 8 <= limit)
        {
            string chunkId = Encoding.ASCII.GetString(data, offset, 4);
            uint chunkSize = ReadUInt32LE(data, offset + 4);
            int body = offset + 8;

            if (chunkId.Equals("fmt ", StringComparison.OrdinalIgnoreCase) && chunkSize >= 16 && body + 16 <= limit)
            {
                formatTag = ReadUInt16LE(data, body);
                channels = ReadUInt16LE(data, body + 2);
                sampleRate = (int)ReadUInt32LE(data, body + 4);
                byteRate = (int)ReadUInt32LE(data, body + 8);
                bitsPerSample = ReadUInt16LE(data, body + 14);
            }
            else if (chunkId.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                dataChunkSize = chunkSize;
                break;
            }

            long padded = (long)chunkSize + (chunkSize & 1);
            long next = (long)offset + 8 + padded;
            if (next <= offset || next > limit)
            {
                break;
            }
            offset = (int)next;
        }

        string codec = FormatWaveFormatTag(formatTag);
        double duration = byteRate > 0 && dataChunkSize > 0 ? (double)dataChunkSize / byteRate : 0;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "RIFF WAVE"),
            new("Audio Codec", codec)
        };
        if (channels > 0)
        {
            fields.Add(new("Channels", channels.ToString(CultureInfo.InvariantCulture)));
        }
        if (sampleRate > 0)
        {
            fields.Add(new("Sample Rate", $"{sampleRate} Hz"));
        }
        if (bitsPerSample > 0)
        {
            fields.Add(new("Bits Per Sample", $"{bitsPerSample}-bit"));
        }
        if (duration > 0)
        {
            fields.Add(new("Duration", $"{duration.ToString("F2", CultureInfo.InvariantCulture)} s"));
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "WAV",
            formatName: $"WAV Audio ({codec})",
            extension: ".wav",
            mimeType: "audio/wav",
            byteSize: data.Length,
            durationSeconds: duration,
            audioCodec: codec,
            sampleRate: sampleRate,
            channels: channels,
            bitsPerSample: bitsPerSample,
            isPlayableAudio: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseFlac(byte[] data)
    {
        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        long totalSamples = 0;

        if (data.Length >= 26 && (data[4] & 0x7F) == 0)
        {
            // STREAMINFO block at offset 8 (18+ bytes)
            sampleRate = (data[18] << 12) | (data[19] << 4) | (data[20] >> 4);
            channels = ((data[20] >> 1) & 0x07) + 1;
            bitsPerSample = (((data[20] & 0x01) << 4) | (data[21] >> 4)) + 1;
            totalSamples = (((long)(data[21] & 0x0F)) << 32)
                | ((long)data[22] << 24)
                | ((long)data[23] << 16)
                | ((long)data[24] << 8)
                | data[25];
        }

        double duration = sampleRate > 0 && totalSamples > 0 ? (double)totalSamples / sampleRate : 0;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "Free Lossless Audio Codec (FLAC)"),
            new("Audio Codec", "FLAC")
        };
        if (channels > 0) fields.Add(new("Channels", channels.ToString(CultureInfo.InvariantCulture)));
        if (sampleRate > 0) fields.Add(new("Sample Rate", $"{sampleRate} Hz"));
        if (bitsPerSample > 0) fields.Add(new("Bits Per Sample", $"{bitsPerSample}-bit"));
        if (totalSamples > 0) fields.Add(new("Total Samples", totalSamples.ToString("N0", CultureInfo.InvariantCulture)));
        if (duration > 0) fields.Add(new("Duration", $"{duration.ToString("F2", CultureInfo.InvariantCulture)} s"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "FLAC",
            formatName: "FLAC Lossless Audio",
            extension: ".flac",
            mimeType: "audio/flac",
            byteSize: data.Length,
            durationSeconds: duration,
            audioCodec: "FLAC",
            sampleRate: sampleRate,
            channels: channels,
            bitsPerSample: bitsPerSample,
            isPlayableAudio: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseFsb5(byte[] data)
    {
        uint version = data.Length >= 8 ? ReadUInt32LE(data, 4) : 1;
        int subsoundCount = data.Length >= 12 ? (int)ReadUInt32LE(data, 8) : 0;
        UnityAudioCodec codec = UnityAudioDecoder.DetectFsb5Codec(data);

        int sampleRate = 0;
        int channels = 0;
        long sampleCount = 0;
        int headerSize = version == 0 ? 64 : 60;
        if (data.Length >= headerSize + 8 && subsoundCount > 0)
        {
            try
            {
                using var ms = new MemoryStream(data, headerSize, 8, writable: false);
                using var br = new BinaryReader(ms);
                var meta = new UnityAudioSampleMetadata();
                meta.Read(br);
                sampleRate = meta.Frequency;
                channels = meta.NumChannels;
                sampleCount = meta.SampleCount;
            }
            catch
            {
            }
        }

        double duration = sampleRate > 0 && sampleCount > 0 ? (double)sampleCount / sampleRate : 0;
        string codecText = codec != UnityAudioCodec.None ? codec.ToString() : "Unknown";

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", $"FMOD Sample Bank v5 (FSB5 v{version})"),
            new("Audio Codec", codecText),
            new("Subsounds", subsoundCount.ToString(CultureInfo.InvariantCulture))
        };
        if (channels > 0) fields.Add(new("Channels", channels.ToString(CultureInfo.InvariantCulture)));
        if (sampleRate > 0) fields.Add(new("Sample Rate", $"{sampleRate} Hz"));
        if (sampleCount > 0) fields.Add(new("Sample Count", sampleCount.ToString("N0", CultureInfo.InvariantCulture)));
        if (duration > 0) fields.Add(new("Duration", $"{duration.ToString("F2", CultureInfo.InvariantCulture)} s"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "FSB5",
            formatName: $"FMOD Sample Bank (FSB5 {codecText})",
            extension: ".fsb",
            mimeType: "audio/x-fsb",
            byteSize: data.Length,
            durationSeconds: duration,
            audioCodec: codecText,
            sampleRate: sampleRate,
            channels: channels,
            subsoundCount: subsoundCount,
            isPlayableAudio: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseAiff(byte[] data)
    {
        bool isAifc = MatchesAscii(data, 8, "AIFC");
        int limit = Math.Min(data.Length, 4096);
        int offset = 12;
        int channels = 0;
        uint sampleFrames = 0;
        int bitsPerSample = 0;
        int sampleRate = 0;

        while (offset + 8 <= limit)
        {
            string chunkId = Encoding.ASCII.GetString(data, offset, 4);
            uint chunkSize = ReadUInt32BE(data, offset + 4);
            int body = offset + 8;

            if (chunkId == "COMM" && chunkSize >= 18 && body + 18 <= limit)
            {
                channels = ReadUInt16BE(data, body);
                sampleFrames = ReadUInt32BE(data, body + 2);
                bitsPerSample = ReadUInt16BE(data, body + 6);
                sampleRate = DecodeIeee80ExtendedSampleRate(data.AsSpan(body + 8, 10));
                break;
            }

            long padded = (long)chunkSize + (chunkSize & 1);
            long next = (long)offset + 8 + padded;
            if (next <= offset || next > limit) break;
            offset = (int)next;
        }

        double duration = sampleRate > 0 && sampleFrames > 0 ? (double)sampleFrames / sampleRate : 0;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", isAifc ? "Audio Interchange File Format Compressed (AIFC)" : "Audio Interchange File Format (AIFF)")
        };
        if (channels > 0) fields.Add(new("Channels", channels.ToString(CultureInfo.InvariantCulture)));
        if (sampleRate > 0) fields.Add(new("Sample Rate", $"{sampleRate} Hz"));
        if (bitsPerSample > 0) fields.Add(new("Bits Per Sample", $"{bitsPerSample}-bit"));
        if (duration > 0) fields.Add(new("Duration", $"{duration.ToString("F2", CultureInfo.InvariantCulture)} s"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "AIFF",
            formatName: isAifc ? "AIFF-C Audio" : "AIFF Audio",
            extension: ".aiff",
            mimeType: "audio/aiff",
            byteSize: data.Length,
            durationSeconds: duration,
            audioCodec: isAifc ? "AIFC" : "PCM (Big-Endian)",
            sampleRate: sampleRate,
            channels: channels,
            bitsPerSample: bitsPerSample,
            isPlayableAudio: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseMidi(byte[] data)
    {
        ushort formatType = ReadUInt16BE(data, 8);
        ushort tracks = ReadUInt16BE(data, 10);
        ushort division = ReadUInt16BE(data, 12);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "Standard MIDI File (SMF)"),
            new("MIDI Format", formatType.ToString(CultureInfo.InvariantCulture)),
            new("Tracks", tracks.ToString(CultureInfo.InvariantCulture)),
            new("Time Division", $"{division} ticks/quarter-note")
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "MIDI",
            formatName: "Standard MIDI Audio (MID)",
            extension: ".mid",
            mimeType: "audio/midi",
            byteSize: data.Length,
            audioCodec: "MIDI",
            streamCount: tracks,
            isPlayableAudio: false,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseWwiseBank(byte[] data)
    {
        bool isAkpk = MatchesAscii(data, 0, "AKPK");
        uint version = data.Length >= 12 ? ReadUInt32LE(data, 8) : 0;
        uint bankId = data.Length >= 16 ? ReadUInt32LE(data, 12) : 0;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", isAkpk ? "Audiokinetic Wwise Package (AKPK)" : "Audiokinetic Wwise SoundBank (BKHD)")
        };
        if (version > 0) fields.Add(new("Bank Version", version.ToString(CultureInfo.InvariantCulture)));
        if (bankId > 0) fields.Add(new("SoundBank ID", $"0x{bankId:X8}"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "WwiseBNK",
            formatName: isAkpk ? "Wwise Audio Package (PCK)" : "Wwise SoundBank (BNK)",
            extension: isAkpk ? ".pck" : ".bnk",
            mimeType: "application/x-wwise-soundbank",
            byteSize: data.Length,
            audioCodec: "Wwise",
            isPlayableAudio: false,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseMp3WithId3(byte[] data)
    {
        int verMajor = data.Length > 3 ? data[3] : 0;
        int verRev = data.Length > 4 ? data[4] : 0;
        int id3Size = 0;
        if (data.Length >= 10)
        {
            id3Size = ((data[6] & 0x7F) << 21)
                | ((data[7] & 0x7F) << 14)
                | ((data[8] & 0x7F) << 7)
                | (data[9] & 0x7F);
        }

        int sampleRate = 0;
        int channels = 0;
        int bitrateKbps = 0;
        int frameStart = 10 + id3Size;
        if (frameStart + 4 <= data.Length)
        {
            TryParseMpegAudioFrameHeader(data, frameStart, out sampleRate, out channels, out bitrateKbps, out _);
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", $"MP3 Audio (ID3v2.{verMajor}.{verRev})"),
            new("Audio Codec", "MPEG-1/2 Audio Layer III (MP3)"),
            new("ID3 Tag Size", $"{id3Size.ToString("N0", CultureInfo.InvariantCulture)} bytes")
        };
        if (sampleRate > 0) fields.Add(new("Sample Rate", $"{sampleRate} Hz"));
        if (channels > 0) fields.Add(new("Channels", channels.ToString(CultureInfo.InvariantCulture)));
        if (bitrateKbps > 0) fields.Add(new("Bitrate", $"{bitrateKbps} kbps"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "MP3",
            formatName: "MP3 Audio",
            extension: ".mp3",
            mimeType: "audio/mpeg",
            byteSize: data.Length,
            audioCodec: "MP3",
            sampleRate: sampleRate,
            channels: channels,
            isPlayableAudio: true,
            metadataFields: fields);
    }

    private static bool TryDetectAdtsAac(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 7 || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
        {
            return false;
        }

        int freqIdx = (data[2] >> 2) & 0x0F;
        if (freqIdx >= 13)
        {
            return false;
        }

        int frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] >> 5) & 0x07);
        if (frameLength < 7)
        {
            return false;
        }

        bool hasSecondFrame = data.Length >= frameLength + 2
            && data[frameLength] == 0xFF
            && (data[frameLength + 1] & 0xF6) == 0xF0;
        bool hasHint = string.Equals(extHint, ".aac", StringComparison.OrdinalIgnoreCase);

        if (!hasSecondFrame && !hasHint && data.Length > 256)
        {
            return false;
        }

        int[] aacRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        int sampleRate = aacRates[freqIdx];
        int channels = ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "ADTS Audio Data Transport Stream"),
            new("Audio Codec", "AAC"),
            new("Sample Rate", $"{sampleRate} Hz")
        };
        if (channels > 0) fields.Add(new("Channels", channels.ToString(CultureInfo.InvariantCulture)));

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "AAC",
            formatName: "AAC Audio (ADTS)",
            extension: ".aac",
            mimeType: "audio/aac",
            byteSize: data.Length,
            audioCodec: "AAC",
            sampleRate: sampleRate,
            channels: channels,
            isPlayableAudio: true,
            metadataFields: fields);
        return true;
    }

    private static bool TryDetectRawMp3Frame(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (!TryParseMpegAudioFrameHeader(data, 0, out int sampleRate, out int channels, out int bitrateKbps, out int frameLen))
        {
            return false;
        }

        bool hasSecondSync = frameLen > 4
            && data.Length >= frameLen + 2
            && data[frameLen] == 0xFF
            && (data[frameLen + 1] & 0xE0) == 0xE0;
        bool hasMp3Hint = string.Equals(extHint, ".mp3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".mp2", StringComparison.OrdinalIgnoreCase);

        if (!hasSecondSync && !hasMp3Hint && data.Length > frameLen + 4)
        {
            return false;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "MPEG Audio Elementary Stream"),
            new("Audio Codec", "MP3"),
            new("Sample Rate", $"{sampleRate} Hz"),
            new("Channels", channels.ToString(CultureInfo.InvariantCulture)),
            new("Bitrate", $"{bitrateKbps} kbps")
        };

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Audio,
            formatId: "MP3",
            formatName: "MP3 Audio",
            extension: ".mp3",
            mimeType: "audio/mpeg",
            byteSize: data.Length,
            audioCodec: "MP3",
            sampleRate: sampleRate,
            channels: channels,
            isPlayableAudio: true,
            metadataFields: fields);
        return true;
    }

    private static bool TryParseMpegAudioFrameHeader(
        byte[] data,
        int offset,
        out int sampleRate,
        out int channels,
        out int bitrateKbps,
        out int frameLength)
    {
        sampleRate = 0;
        channels = 0;
        bitrateKbps = 0;
        frameLength = 0;

        if (offset + 4 > data.Length || data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0)
        {
            return false;
        }

        int versionBits = (data[offset + 1] >> 3) & 0x03;
        int layerBits = (data[offset + 1] >> 1) & 0x03;
        int bitrateIdx = (data[offset + 2] >> 4) & 0x0F;
        int sampleRateIdx = (data[offset + 2] >> 2) & 0x03;
        int padding = (data[offset + 2] >> 1) & 0x01;
        int channelMode = (data[offset + 3] >> 6) & 0x03;

        // versionBits == 1 is reserved; layerBits == 0 is reserved (or ADTS AAC); bitrateIdx 0/15 and sampleRateIdx 3 are invalid
        if (versionBits == 1 || layerBits != 1 || bitrateIdx is 0 or 15 || sampleRateIdx == 3)
        {
            return false;
        }

        int[] ratesMpeg1 = { 44100, 48000, 32000 };
        int[] ratesMpeg2 = { 22050, 24000, 16000 };
        int[] ratesMpeg25 = { 11025, 12000, 8000 };
        sampleRate = versionBits switch
        {
            3 => ratesMpeg1[sampleRateIdx],
            2 => ratesMpeg2[sampleRateIdx],
            _ => ratesMpeg25[sampleRateIdx]
        };

        int[] bitratesMpeg1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        int[] bitratesMpeg2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        bitrateKbps = versionBits == 3 ? bitratesMpeg1L3[bitrateIdx] : bitratesMpeg2L3[bitrateIdx];
        channels = channelMode == 3 ? 1 : 2;

        int coeff = versionBits == 3 ? 144 : 72;
        frameLength = sampleRate > 0 ? (coeff * bitrateKbps * 1000) / sampleRate + padding : 0;
        return sampleRate > 0 && bitrateKbps > 0;
    }

    private static TextAssetFormatDetectionResult ParsePng(byte[] data)
    {
        int width = 0;
        int height = 0;
        int bitDepth = 0;
        string colorTypeDesc = string.Empty;

        if (data.Length >= 26 && MatchesAscii(data, 12, "IHDR"))
        {
            width = (int)ReadUInt32BE(data, 16);
            height = (int)ReadUInt32BE(data, 20);
            bitDepth = data[24];
            byte colorType = data[25];
            colorTypeDesc = colorType switch
            {
                0 => "Grayscale",
                2 => "RGB",
                3 => "Indexed Palette",
                4 => "Grayscale + Alpha",
                6 => "RGBA",
                _ => $"Type {colorType}"
            };
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", "Portable Network Graphics (PNG)")
        };
        if (width > 0 && height > 0) fields.Add(new("Dimensions", $"{width}x{height}"));
        if (bitDepth > 0) fields.Add(new("Bit Depth", $"{bitDepth}-bit"));
        if (!string.IsNullOrEmpty(colorTypeDesc)) fields.Add(new("Color Type", colorTypeDesc));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "PNG",
            formatName: "PNG Image",
            extension: ".png",
            mimeType: "image/png",
            byteSize: data.Length,
            width: width,
            height: height,
            bitsPerSample: bitDepth,
            isPreviewableImage: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseJpeg(byte[] data)
    {
        int width = 0;
        int height = 0;
        int components = 0;
        int precision = 0;
        string sofType = "Baseline DCT";

        int limit = Math.Min(data.Length, MaxHeaderScanBytes);
        int pos = 2;
        while (pos + 4 <= limit)
        {
            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            while (pos < limit && data[pos] == 0xFF)
            {
                pos++;
            }
            if (pos >= limit) break;

            byte marker = data[pos++];
            if (marker is 0xD8 or 0xD9 or 0x00 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (pos + 2 > limit) break;
            ushort segLen = ReadUInt16BE(data, pos);
            if (segLen < 2) break;

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 && pos + 7 < limit)
            {
                sofType = marker == 0xC2 ? "Progressive DCT" : "Baseline DCT";
                precision = data[pos + 2];
                height = ReadUInt16BE(data, pos + 3);
                width = ReadUInt16BE(data, pos + 5);
                components = data[pos + 7];
                break;
            }

            pos += segLen;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", $"JPEG Image ({sofType})")
        };
        if (width > 0 && height > 0) fields.Add(new("Dimensions", $"{width}x{height}"));
        if (components > 0) fields.Add(new("Channels", components.ToString(CultureInfo.InvariantCulture)));
        if (precision > 0) fields.Add(new("Sample Precision", $"{precision}-bit"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "JPEG",
            formatName: "JPEG Image",
            extension: ".jpg",
            mimeType: "image/jpeg",
            byteSize: data.Length,
            width: width,
            height: height,
            channels: components,
            bitsPerSample: precision,
            isPreviewableImage: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseGif(byte[] data)
    {
        string version = Encoding.ASCII.GetString(data, 0, 6);
        int width = ReadUInt16LE(data, 6);
        int height = ReadUInt16LE(data, 8);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", $"CompuServe GIF ({version})"),
            new("Dimensions", $"{width}x{height}")
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "GIF",
            formatName: $"GIF Image ({version})",
            extension: ".gif",
            mimeType: "image/gif",
            byteSize: data.Length,
            width: width,
            height: height,
            isPreviewableImage: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseWebP(byte[] data)
    {
        int width = 0;
        int height = 0;
        string chunkFormat = "VP8";

        if (data.Length >= 16)
        {
            chunkFormat = Encoding.ASCII.GetString(data, 12, 4).Trim();
            if (MatchesAscii(data, 12, "VP8 ") && data.Length >= 30)
            {
                if (data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
                {
                    width = ReadUInt16LE(data, 26) & 0x3FFF;
                    height = ReadUInt16LE(data, 28) & 0x3FFF;
                }
            }
            else if (MatchesAscii(data, 12, "VP8L") && data.Length >= 25 && data[20] == 0x2F)
            {
                uint bits = ReadUInt32LE(data, 21);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }
            else if (MatchesAscii(data, 12, "VP8X") && data.Length >= 30)
            {
                width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
                height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            }
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", $"WebP Image ({chunkFormat})")
        };
        if (width > 0 && height > 0) fields.Add(new("Dimensions", $"{width}x{height}"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "WebP",
            formatName: $"WebP Image ({chunkFormat})",
            extension: ".webp",
            mimeType: "image/webp",
            byteSize: data.Length,
            width: width,
            height: height,
            isPreviewableImage: true,
            metadataFields: fields);
    }

    private static bool TryDetectBmp(byte[] data, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            return false;
        }

        uint dataOffset = ReadUInt32LE(data, 10);
        uint dibSize = ReadUInt32LE(data, 14);
        if (dibSize is not (12 or 40 or 52 or 56 or 108 or 124) || dataOffset < 14 + dibSize)
        {
            return false;
        }

        int width;
        int height;
        int bpp;
        if (dibSize == 12)
        {
            width = ReadUInt16LE(data, 18);
            height = ReadUInt16LE(data, 20);
            bpp = ReadUInt16LE(data, 24);
        }
        else
        {
            if (data.Length < 30) return false;
            width = ReadInt32LE(data, 18);
            height = Math.Abs(ReadInt32LE(data, 22));
            bpp = ReadUInt16LE(data, 28);
        }

        if (width is <= 0 or > 32768 || height is <= 0 or > 32768)
        {
            return false;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", "Windows Bitmap (BMP)"),
            new("Dimensions", $"{width}x{height}"),
            new("Bits Per Pixel", $"{bpp}-bit")
        };

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "BMP",
            formatName: "BMP Image",
            extension: ".bmp",
            mimeType: "image/bmp",
            byteSize: data.Length,
            width: width,
            height: height,
            bitsPerSample: bpp,
            isPreviewableImage: true,
            metadataFields: fields);
        return true;
    }

    private static bool TryDetectIcoOrCur(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 22 || data[0] != 0 || data[1] != 0)
        {
            return false;
        }

        ushort type = ReadUInt16LE(data, 2);
        ushort count = ReadUInt16LE(data, 4);
        if (type is not (1 or 2) || count is 0 or > 256)
        {
            return false;
        }

        uint bytesInRes = ReadUInt32LE(data, 14);
        uint imageOffset = ReadUInt32LE(data, 18);
        uint expectedDirSize = 6u + (uint)count * 16u;
        if (bytesInRes == 0 || imageOffset < expectedDirSize)
        {
            return false;
        }

        // Guard against false positives on raw binary arrays starting with 00 00 01 00
        bool validOffset = imageOffset <= (uint)data.Length
            || string.Equals(extHint, ".ico", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".cur", StringComparison.OrdinalIgnoreCase);
        if (!validOffset)
        {
            return false;
        }

        int width = data[6] == 0 ? 256 : data[6];
        int height = data[7] == 0 ? 256 : data[7];
        int bpp = ReadUInt16LE(data, 12);
        bool isCur = type == 2;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", isCur ? "Windows Cursor (CUR)" : "Windows Icon (ICO)"),
            new("Image Count", count.ToString(CultureInfo.InvariantCulture)),
            new("Primary Dimensions", $"{width}x{height}")
        };
        if (bpp > 0) fields.Add(new("Bits Per Pixel", $"{bpp}-bit"));

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: isCur ? "CUR" : "ICO",
            formatName: isCur ? "Windows Cursor (CUR)" : "Windows Icon (ICO)",
            extension: isCur ? ".cur" : ".ico",
            mimeType: "image/x-icon",
            byteSize: data.Length,
            width: width,
            height: height,
            bitsPerSample: bpp,
            isPreviewableImage: true,
            metadataFields: fields);
        return true;
    }

    private static TextAssetFormatDetectionResult ParseTiff(byte[] data)
    {
        bool littleEndian = data[0] == (byte)'I';
        uint ifdOffset = littleEndian ? ReadUInt32LE(data, 4) : ReadUInt32BE(data, 4);
        int width = 0;
        int height = 0;

        if (ifdOffset >= 8 && ifdOffset + 2 <= data.Length && ifdOffset < MaxHeaderScanBytes)
        {
            int pos = (int)ifdOffset;
            ushort entryCount = littleEndian ? ReadUInt16LE(data, pos) : ReadUInt16BE(data, pos);
            pos += 2;
            for (int i = 0; i < entryCount && pos + 12 <= data.Length; i++, pos += 12)
            {
                ushort tag = littleEndian ? ReadUInt16LE(data, pos) : ReadUInt16BE(data, pos);
                ushort fieldType = littleEndian ? ReadUInt16LE(data, pos + 2) : ReadUInt16BE(data, pos + 2);
                int val = fieldType == 3
                    ? (littleEndian ? ReadUInt16LE(data, pos + 8) : ReadUInt16BE(data, pos + 8))
                    : (int)(littleEndian ? ReadUInt32LE(data, pos + 8) : ReadUInt32BE(data, pos + 8));

                if (tag == 0x0100) width = val;
                else if (tag == 0x0101) height = val;
            }
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", $"Tagged Image File Format (TIFF, {(littleEndian ? "Little-Endian" : "Big-Endian")})")
        };
        if (width > 0 && height > 0) fields.Add(new("Dimensions", $"{width}x{height}"));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "TIFF",
            formatName: "TIFF Image",
            extension: ".tif",
            mimeType: "image/tiff",
            byteSize: data.Length,
            width: width,
            height: height,
            isPreviewableImage: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseDds(byte[] data)
    {
        int height = ReadInt32LE(data, 12);
        int width = ReadInt32LE(data, 16);
        int mipMapCount = Math.Max(1, ReadInt32LE(data, 28));
        uint pfFlags = ReadUInt32LE(data, 80);
        string fourCC = ReadCleanFourCC(data, 84);
        uint rgbBitCount = ReadUInt32LE(data, 88);
        uint rMask = ReadUInt32LE(data, 92);

        int dataOffset = 128;
        TextureFormat? texFormat = null;
        string pixelFormatDesc = !string.IsNullOrEmpty(fourCC) ? fourCC : $"{rgbBitCount}-bit Uncompressed";

        if (fourCC == "DXT1") texFormat = TextureFormat.DXT1;
        else if (fourCC == "DXT3") texFormat = TextureFormat.DXT3;
        else if (fourCC == "DXT5") texFormat = TextureFormat.DXT5;
        else if (fourCC is "ATI1" or "BC4U" or "BC4S") texFormat = TextureFormat.BC4;
        else if (fourCC is "ATI2" or "BC5U" or "BC5S") texFormat = TextureFormat.BC5;
        else if (fourCC == "DX10" && data.Length >= 148)
        {
            dataOffset = 148;
            uint dxgiFormat = ReadUInt32LE(data, 128);
            pixelFormatDesc = $"DX10 (DXGI_FORMAT={dxgiFormat})";
            texFormat = dxgiFormat switch
            {
                71 or 72 => TextureFormat.DXT1,
                74 or 75 => TextureFormat.DXT3,
                77 or 78 => TextureFormat.DXT5,
                80 or 81 => TextureFormat.BC4,
                83 or 84 => TextureFormat.BC5,
                95 or 96 => TextureFormat.BC6H,
                98 or 99 => TextureFormat.BC7,
                28 or 29 => TextureFormat.RGBA32,
                87 or 91 => TextureFormat.BGRA32,
                _ => null
            };
        }
        else if ((pfFlags & 0x40) != 0) // DDPF_RGB
        {
            if (rgbBitCount == 32)
            {
                texFormat = rMask == 0x00FF0000 ? TextureFormat.BGRA32 : TextureFormat.RGBA32;
            }
            else if (rgbBitCount == 24)
            {
                texFormat = rMask == 0x00FF0000 ? TextureFormat.BGR24 : TextureFormat.RGB24;
            }
            else if (rgbBitCount == 16)
            {
                texFormat = TextureFormat.RGB565;
            }
        }
        else if (rgbBitCount == 8)
        {
            texFormat = TextureFormat.Alpha8;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", "DirectDraw Surface (DDS)"),
            new("Dimensions", $"{width}x{height}"),
            new("Pixel Format", pixelFormatDesc),
            new("MipMap Levels", mipMapCount.ToString(CultureInfo.InvariantCulture))
        };
        if (texFormat.HasValue)
        {
            fields.Add(new("Decoded TextureFormat", texFormat.Value.ToString()));
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "DDS",
            formatName: $"DDS Texture ({pixelFormatDesc})",
            extension: ".dds",
            mimeType: "image/vnd-ms.dds",
            byteSize: data.Length,
            width: width,
            height: height,
            mipMapCount: mipMapCount,
            ddsTextureFormat: texFormat,
            ddsDataOffset: dataOffset,
            isPreviewableImage: true,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseKtx(byte[] data)
    {
        bool isKtx2 = data.AsSpan(0, 12).SequenceEqual(Ktx2Magic);
        int width = 0;
        int height = 0;
        int mips = 0;

        if (!isKtx2 && data.Length >= 64)
        {
            width = ReadInt32LE(data, 36);
            height = Math.Max(1, ReadInt32LE(data, 40));
            mips = Math.Max(1, ReadInt32LE(data, 56));
        }
        else if (isKtx2 && data.Length >= 48)
        {
            width = ReadInt32LE(data, 20);
            height = Math.Max(1, ReadInt32LE(data, 24));
            mips = Math.Max(1, ReadInt32LE(data, 40));
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", isKtx2 ? "Khronos Texture v2 (KTX2)" : "Khronos Texture v1 (KTX)")
        };
        if (width > 0 && height > 0) fields.Add(new("Dimensions", $"{width}x{height}"));
        if (mips > 0) fields.Add(new("MipMap Levels", mips.ToString(CultureInfo.InvariantCulture)));

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: isKtx2 ? "KTX2" : "KTX",
            formatName: isKtx2 ? "Khronos Texture (KTX2)" : "Khronos Texture (KTX)",
            extension: isKtx2 ? ".ktx2" : ".ktx",
            mimeType: isKtx2 ? "image/ktx2" : "image/ktx",
            byteSize: data.Length,
            width: width,
            height: height,
            mipMapCount: mips,
            isPreviewableImage: false,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParsePsd(byte[] data, ushort version)
    {
        int channels = ReadUInt16BE(data, 12);
        int height = (int)ReadUInt32BE(data, 14);
        int width = (int)ReadUInt32BE(data, 18);
        int depth = ReadUInt16BE(data, 22);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Format", version == 2 ? "Adobe Photoshop Big (PSB)" : "Adobe Photoshop Document (PSD)"),
            new("Dimensions", $"{width}x{height}"),
            new("Channels", channels.ToString(CultureInfo.InvariantCulture)),
            new("Bit Depth", $"{depth}-bit")
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.Image,
            formatId: "PSD",
            formatName: "Adobe Photoshop Document (PSD)",
            extension: ".psd",
            mimeType: "image/vnd.adobe.photoshop",
            byteSize: data.Length,
            width: width,
            height: height,
            channels: channels,
            bitsPerSample: depth,
            isPreviewableImage: false,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseZip(byte[] data, string extHint, bool inspectArchiveEntries)
    {
        var entries = new List<string>();
        int totalEntries = 0;

        if (inspectArchiveEntries && data.Length <= MaxZipInspectBytes)
        {
            try
            {
                using var ms = new MemoryStream(data, writable: false);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
                totalEntries = archive.Entries.Count;
                foreach (var entry in archive.Entries)
                {
                    if (entries.Count < MaxListedZipEntries)
                    {
                        entries.Add(string.Create(CultureInfo.InvariantCulture, $"{entry.FullName} ({entry.Length:N0} bytes, compressed {entry.CompressedLength:N0} bytes)"));
                    }
                }
            }
            catch
            {
            }
        }

        // Fallback to first Local File Header if ZipArchive central directory was truncated
        if (totalEntries == 0 && data.Length >= 30 && data[2] == 0x03 && data[3] == 0x04)
        {
            ushort nameLen = ReadUInt16LE(data, 26);
            uint compSize = ReadUInt32LE(data, 18);
            uint uncompSize = ReadUInt32LE(data, 22);
            if (nameLen > 0 && 30 + nameLen <= data.Length)
            {
                string firstEntryName = Encoding.UTF8.GetString(data, 30, nameLen);
                entries.Add(string.Create(CultureInfo.InvariantCulture, $"{firstEntryName} ({uncompSize:N0} bytes, compressed {compSize:N0} bytes)"));
                totalEntries = 1;
            }
        }

        string extension = extHint switch
        {
            ".apk" or ".jar" or ".docx" or ".xlsx" or ".pptx" => extHint,
            _ => ".zip"
        };

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "PKZIP Archive"),
            new("Entry Count", totalEntries.ToString("N0", CultureInfo.InvariantCulture))
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "ZIP",
            formatName: "ZIP Archive",
            extension: extension,
            mimeType: "application/zip",
            byteSize: data.Length,
            archiveEntryCount: totalEntries,
            archiveEntries: entries,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseGzip(byte[] data)
    {
        byte flags = data.Length > 3 ? data[3] : (byte)0;
        string originalName = string.Empty;
        int pos = 10;

        if ((flags & 0x04) != 0 && pos + 2 <= data.Length)
        {
            int xlen = ReadUInt16LE(data, pos);
            pos += 2 + xlen;
        }

        if ((flags & 0x08) != 0 && pos < data.Length)
        {
            int end = pos;
            int maxScan = Math.Min(data.Length, pos + 256);
            while (end < maxScan && data[end] != 0)
            {
                end++;
            }
            if (end > pos)
            {
                originalName = ReadCleanAsciiString(data, pos, end - pos);
            }
        }

        uint uncompressedMod32 = data.Length >= 18 ? ReadUInt32LE(data, data.Length - 4) : 0;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", "GZIP Compressed Archive (DEFLATE)")
        };
        if (!string.IsNullOrEmpty(originalName))
        {
            fields.Add(new("Original Filename", originalName));
        }
        if (uncompressedMod32 > 0)
        {
            fields.Add(new("Uncompressed Size (mod 2^32)", $"{uncompressedMod32.ToString("N0", CultureInfo.InvariantCulture)} bytes"));
        }

        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "GZIP",
            formatName: "GZIP Compressed Data",
            extension: ".gz",
            mimeType: "application/gzip",
            byteSize: data.Length,
            metadataFields: fields);
    }

    private static TextAssetFormatDetectionResult ParseSevenZip(byte[] data)
    {
        string ver = data.Length >= 8 ? $"{data[6]}.{data[7]}" : "0.4";
        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "7Z",
            formatName: $"7-Zip Archive (v{ver})",
            extension: ".7z",
            mimeType: "application/x-7z-compressed",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", $"7-Zip Archive (Format v{ver})")
            });
    }

    private static TextAssetFormatDetectionResult ParseRar(byte[] data)
    {
        bool isRar5 = data.Length >= 8 && data[6] == 0x01 && data[7] == 0x00;
        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "RAR",
            formatName: isRar5 ? "RAR5 Archive" : "RAR4 Archive",
            extension: ".rar",
            mimeType: "application/vnd.rar",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", isRar5 ? "RAR Archive v5.0" : "RAR Archive v4.x")
            });
    }

    private static TextAssetFormatDetectionResult ParseZstandard(byte[] data)
    {
        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "ZSTD",
            formatName: "Zstandard Compressed Frame (ZSTD)",
            extension: ".zst",
            mimeType: "application/zstd",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", "Zstandard Frame (RFC 8878)")
            });
    }

    private static TextAssetFormatDetectionResult ParseLz4(byte[] data)
    {
        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "LZ4",
            formatName: "LZ4 Compressed Frame",
            extension: ".lz4",
            mimeType: "application/x-lz4",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", "LZ4 Frame Format")
            });
    }

    private static TextAssetFormatDetectionResult ParseSqlite(byte[] data)
    {
        int pageSize = 0;
        uint pageCount = 0;
        uint schemaFormat = 0;
        uint textEncoding = 0;
        uint userVersion = 0;
        uint sqliteVersion = 0;

        if (data.Length >= 20)
        {
            ushort rawPageSize = ReadUInt16BE(data, 16);
            pageSize = rawPageSize == 1 ? 65536 : rawPageSize;
        }
        if (data.Length >= 32) pageCount = ReadUInt32BE(data, 28);
        if (data.Length >= 48) schemaFormat = ReadUInt32BE(data, 44);
        if (data.Length >= 64)
        {
            textEncoding = ReadUInt32BE(data, 56);
            userVersion = ReadUInt32BE(data, 60);
        }
        if (data.Length >= 100) sqliteVersion = ReadUInt32BE(data, 96);

        string encodingName = textEncoding switch
        {
            1 => "UTF-8",
            2 => "UTF-16le",
            3 => "UTF-16be",
            _ => "UTF-8"
        };

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Database Engine", "SQLite 3"),
            new("Page Size", $"{pageSize.ToString("N0", CultureInfo.InvariantCulture)} bytes"),
            new("Text Encoding", encodingName)
        };
        if (pageCount > 0) fields.Add(new("Page Count", pageCount.ToString("N0", CultureInfo.InvariantCulture)));
        if (schemaFormat > 0) fields.Add(new("Schema Format", schemaFormat.ToString(CultureInfo.InvariantCulture)));
        if (userVersion > 0) fields.Add(new("User Version", userVersion.ToString(CultureInfo.InvariantCulture)));
        if (sqliteVersion > 0) fields.Add(new("SQLite Version Number", sqliteVersion.ToString(CultureInfo.InvariantCulture)));

        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "SQLite",
            formatName: "SQLite 3 Database",
            extension: ".sqlite",
            mimeType: "application/vnd.sqlite3",
            byteSize: data.Length,
            metadataFields: fields);
    }

    private static bool TryDetectFont(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 12)
        {
            return false;
        }

        if (MatchesAscii(data, 0, "wOFF"))
        {
            ushort numTables = data.Length >= 14 ? ReadUInt16BE(data, 12) : (ushort)0;
            result = BuildResult(
                TextAssetEmbeddedMediaKind.StructuredBinary,
                formatId: "WOFF",
                formatName: "Web Open Font Format (WOFF)",
                extension: ".woff",
                mimeType: "font/woff",
                byteSize: data.Length,
                isRenderableFont: false,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Font Container", "WOFF 1.0"),
                    new KeyValuePair<string, string>("Table Count", numTables.ToString(CultureInfo.InvariantCulture))
                });
            return true;
        }

        if (MatchesAscii(data, 0, "wOF2"))
        {
            ushort numTables = data.Length >= 14 ? ReadUInt16BE(data, 12) : (ushort)0;
            result = BuildResult(
                TextAssetEmbeddedMediaKind.StructuredBinary,
                formatId: "WOFF2",
                formatName: "Web Open Font Format 2 (WOFF2)",
                extension: ".woff2",
                mimeType: "font/woff2",
                byteSize: data.Length,
                isRenderableFont: false,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Font Container", "WOFF 2.0"),
                    new KeyValuePair<string, string>("Table Count", numTables.ToString(CultureInfo.InvariantCulture))
                });
            return true;
        }

        if (MatchesAscii(data, 0, "ttcf"))
        {
            uint numFonts = ReadUInt32BE(data, 8);
            if (numFonts is >= 1 and <= 256)
            {
                result = BuildResult(
                    TextAssetEmbeddedMediaKind.StructuredBinary,
                    formatId: "TTC",
                    formatName: "TrueType Font Collection (TTC)",
                    extension: ".ttc",
                    mimeType: "font/collection",
                    byteSize: data.Length,
                    isRenderableFont: true,
                    metadataFields: new[]
                    {
                        new KeyValuePair<string, string>("Font Container", "TrueType Collection (ttcf)"),
                        new KeyValuePair<string, string>("Font Count", numFonts.ToString(CultureInfo.InvariantCulture))
                    });
                return true;
            }
        }

        bool isOtto = MatchesAscii(data, 0, "OTTO");
        bool isTtfMagic = (data[0] == 0x00 && data[1] == 0x01 && data[2] == 0x00 && data[3] == 0x00)
            || MatchesAscii(data, 0, "true");
        if (!isOtto && !isTtfMagic)
        {
            return false;
        }

        ushort tableCount = ReadUInt16BE(data, 4);
        if (tableCount is 0 or > 128)
        {
            return false;
        }

        // Validate SFNT table tags if present to prevent false-positiving on 00 01 00 00 binary integers
        var tableTags = new List<string>();
        int maxTablesToRead = Math.Min((int)tableCount, 24);
        for (int i = 0; i < maxTablesToRead; i++)
        {
            int recOffset = 12 + i * 16;
            if (recOffset + 4 > data.Length)
            {
                break;
            }

            if (!IsPrintableFourCC(data, recOffset))
            {
                return false;
            }
            tableTags.Add(Encoding.ASCII.GetString(data, recOffset, 4).Trim());
        }

        bool hasFontHint = string.Equals(extHint, ".ttf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".otf", StringComparison.OrdinalIgnoreCase);
        if (isTtfMagic && tableTags.Count == 0 && !hasFontHint)
        {
            return false;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Font Format", isOtto ? "OpenType / CFF (OTF)" : "TrueType Font (TTF)"),
            new("SFNT Table Count", tableCount.ToString(CultureInfo.InvariantCulture))
        };
        if (tableTags.Count > 0)
        {
            fields.Add(new("SFNT Tables", string.Join(", ", tableTags)));
        }

        result = BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: isOtto ? "OTF" : "TTF",
            formatName: isOtto ? "OpenType Font (OTF)" : "TrueType Font (TTF)",
            extension: isOtto ? ".otf" : ".ttf",
            mimeType: isOtto ? "font/otf" : "font/ttf",
            byteSize: data.Length,
            isRenderableFont: true,
            metadataFields: fields);
        return true;
    }

    private static TextAssetFormatDetectionResult ParsePdf(byte[] data)
    {
        int maxLen = Math.Min(data.Length, 16);
        string header = ReadCleanAsciiString(data, 0, maxLen);
        string version = header.Length > 5 ? header.Substring(5).Split('\r', '\n', ' ')[0] : "1.x";

        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "PDF",
            formatName: $"PDF Document (v{version})",
            extension: ".pdf",
            mimeType: "application/pdf",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Document Format", $"Portable Document Format (PDF {version})")
            });
    }

    private static bool TryDetectSwf(byte[] data, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 8)
        {
            return false;
        }

        bool isSwf = (data[0] is (byte)'F' or (byte)'C' or (byte)'Z')
            && data[1] == (byte)'W'
            && data[2] == (byte)'S'
            && data[3] is >= 1 and <= 50;
        if (!isSwf)
        {
            return false;
        }

        uint uncompressedLength = ReadUInt32LE(data, 4);
        if (uncompressedLength < 8)
        {
            return false;
        }

        string compression = data[0] switch
        {
            (byte)'C' => "Zlib Compressed (CWS)",
            (byte)'Z' => "LZMA Compressed (ZWS)",
            _ => "Uncompressed (FWS)"
        };

        result = BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "SWF",
            formatName: $"Shockwave Flash / Scaleform (SWF v{data[3]})",
            extension: ".swf",
            mimeType: "application/x-shockwave-flash",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Container", $"Adobe Flash / Scaleform SWF v{data[3]}"),
                new KeyValuePair<string, string>("Compression", compression),
                new KeyValuePair<string, string>("Uncompressed Length", $"{uncompressedLength.ToString("N0", CultureInfo.InvariantCulture)} bytes")
            });
        return true;
    }

    private static bool TryDetectUnityBundle(byte[] data, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 8)
        {
            return false;
        }

        string? signature = null;
        if (MatchesAscii(data, 0, "UnityFS\0")) signature = "UnityFS";
        else if (MatchesAscii(data, 0, "UnityWeb\0")) signature = "UnityWeb";
        else if (MatchesAscii(data, 0, "UnityRaw\0")) signature = "UnityRaw";
        else if (MatchesAscii(data, 0, "UnityArchive\0")) signature = "UnityArchive";

        if (signature == null)
        {
            return false;
        }

        int pos = signature.Length + 1;
        uint formatVersion = pos + 4 <= data.Length ? ReadUInt32BE(data, pos) : 0;
        pos += 4;
        string playerVersion = ReadNullTerminatedAscii(data, ref pos, 32);
        string engineVersion = ReadNullTerminatedAscii(data, ref pos, 32);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Container", $"Unity AssetBundle ({signature})"),
            new("Archive Format Version", formatVersion.ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrEmpty(playerVersion)) fields.Add(new("Player Version", playerVersion));
        if (!string.IsNullOrEmpty(engineVersion)) fields.Add(new("Engine Revision", engineVersion));

        result = BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "UnityFS",
            formatName: $"Unity AssetBundle ({signature})",
            extension: ".bundle",
            mimeType: "application/x-unity-bundle",
            byteSize: data.Length,
            metadataFields: fields);
        return true;
    }

    private static bool TryDetectLuaBytecode(byte[] data, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length >= 5 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a')
        {
            byte verByte = data[4];
            int major = (verByte >> 4) & 0x0F;
            int minor = verByte & 0x0F;
            string verText = $"{major}.{minor}";

            result = BuildResult(
                TextAssetEmbeddedMediaKind.StructuredBinary,
                formatId: "LuaBytecode",
                formatName: $"Lua Compiled Bytecode (Lua {verText})",
                extension: ".luac",
                mimeType: "application/x-lua-bytecode",
                byteSize: data.Length,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Bytecode Format", $"Lua {verText} Precompiled Chunk (0x{verByte:X2})")
                });
            return true;
        }

        if (data.Length >= 4 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'J')
        {
            byte ver = data[3];
            result = BuildResult(
                TextAssetEmbeddedMediaKind.StructuredBinary,
                formatId: "LuaBytecode",
                formatName: $"LuaJIT Compiled Bytecode (v{ver})",
                extension: ".luac",
                mimeType: "application/x-luajit-bytecode",
                byteSize: data.Length,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Bytecode Format", $"LuaJIT Precompiled Chunk (v{ver})")
                });
            return true;
        }

        return false;
    }

    private static TextAssetFormatDetectionResult ParseWasm(byte[] data)
    {
        uint version = ReadUInt32LE(data, 4);
        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "WASM",
            formatName: $"WebAssembly Binary Module (v{version})",
            extension: ".wasm",
            mimeType: "application/wasm",
            byteSize: data.Length,
            metadataFields: new[]
            {
                new KeyValuePair<string, string>("Module Format", $"WebAssembly Binary (WASM v{version})")
            });
    }

    private static bool TryDetectPeExecutable(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;
        if (data.Length < 64 || data[0] != (byte)'M' || data[1] != (byte)'Z')
        {
            return false;
        }

        int peOffset = ReadInt32LE(data, 0x3C);
        bool hasValidPeSignature = peOffset >= 0x40
            && peOffset + 24 <= data.Length
            && MatchesAscii(data, peOffset, "PE\0\0");
        bool hasPeHint = string.Equals(extHint, ".dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".exe", StringComparison.OrdinalIgnoreCase);

        if (!hasValidPeSignature && !hasPeHint)
        {
            return false;
        }

        string arch = "Unknown";
        int sections = 0;
        bool isDll = true;

        if (hasValidPeSignature)
        {
            ushort machine = ReadUInt16LE(data, peOffset + 4);
            sections = ReadUInt16LE(data, peOffset + 6);
            ushort characteristics = ReadUInt16LE(data, peOffset + 22);
            isDll = (characteristics & 0x2000) != 0;
            arch = machine switch
            {
                0x014C => "x86 (32-bit)",
                0x8664 => "x86_64 (64-bit)",
                0xAA64 => "ARM64",
                0x01C0 or 0x01C4 => "ARM",
                _ => $"0x{machine:X4}"
            };
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Executable Format", isDll ? "Portable Executable Dynamic Library (PE/DLL)" : "Portable Executable (PE/EXE)"),
            new("Architecture", arch)
        };
        if (sections > 0) fields.Add(new("Section Count", sections.ToString(CultureInfo.InvariantCulture)));

        result = BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "PE",
            formatName: isDll ? $"PE Dynamic Library ({arch})" : $"PE Executable ({arch})",
            extension: isDll ? ".dll" : ".exe",
            mimeType: "application/vnd.microsoft.portable-executable",
            byteSize: data.Length,
            metadataFields: fields);
        return true;
    }

    private static TextAssetFormatDetectionResult ParseElf(byte[] data)
    {
        string bits = data.Length > 4 ? (data[4] == 2 ? "64-bit" : data[4] == 1 ? "32-bit" : "Unknown") : "Unknown";
        string endian = data.Length > 5 ? (data[5] == 1 ? "Little-Endian" : data[5] == 2 ? "Big-Endian" : "Unknown") : "Unknown";
        string arch = "Unknown";

        if (data.Length >= 20)
        {
            bool le = data[5] != 2;
            ushort machine = le ? ReadUInt16LE(data, 18) : ReadUInt16BE(data, 18);
            arch = machine switch
            {
                0x03 => "x86",
                0x28 => "ARM",
                0x3E => "x86_64",
                0xB7 => "AArch64 (ARM64)",
                0xF3 => "RISC-V",
                _ => $"0x{machine:X4}"
            };
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Executable Format", $"Executable and Linkable Format (ELF {bits})"),
            new("Endianness", endian),
            new("Architecture", arch)
        };

        return BuildResult(
            TextAssetEmbeddedMediaKind.StructuredBinary,
            formatId: "ELF",
            formatName: $"ELF Binary ({bits} {arch})",
            extension: ".so",
            mimeType: "application/x-elf",
            byteSize: data.Length,
            metadataFields: fields);
    }

    private static bool TryDetectStructuredBinarySerialization(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;

        // 1. BSON document: int32LE totalLength == data.Length, ends with 0x00, valid element type 0x01..0x13 + ASCII key
        if (data.Length >= 8)
        {
            int declaredLen = ReadInt32LE(data, 0);
            byte firstType = data[4];
            if (declaredLen == data.Length
                && data[^1] == 0x00
                && firstType is >= 0x01 and <= 0x13)
            {
                int keyEnd = 5;
                while (keyEnd < Math.Min(data.Length - 1, 69) && data[keyEnd] is >= 0x20 and <= 0x7E)
                {
                    keyEnd++;
                }

                if (keyEnd > 5 && data[keyEnd] == 0x00)
                {
                    string firstKey = Encoding.ASCII.GetString(data, 5, keyEnd - 5);
                    result = BuildResult(
                        TextAssetEmbeddedMediaKind.StructuredBinary,
                        formatId: "BSON",
                        formatName: "Binary JSON Document (BSON)",
                        extension: ".bson",
                        mimeType: "application/bson",
                        byteSize: data.Length,
                        metadataFields: new[]
                        {
                            new KeyValuePair<string, string>("Serialization Format", "BSON (Binary JSON)"),
                            new KeyValuePair<string, string>("Document Size", $"{declaredLen.ToString("N0", CultureInfo.InvariantCulture)} bytes"),
                            new KeyValuePair<string, string>("First Field Key", firstKey)
                        });
                    return true;
                }
            }
        }

        // 2. MessagePack map (fixmap 0x81..0x8F or map16 0xDE) starting with fixstr key (0xA1..0xBF)
        if (data.Length >= 4)
        {
            int keyOffset = -1;
            int mapEntries = 0;
            if (data[0] is >= 0x81 and <= 0x8F && data[1] is >= 0xA1 and <= 0xBF)
            {
                mapEntries = data[0] & 0x0F;
                keyOffset = 1;
            }
            else if (data[0] == 0xDE && data.Length >= 6 && data[3] is >= 0xA1 and <= 0xBF)
            {
                mapEntries = ReadUInt16BE(data, 1);
                keyOffset = 3;
            }

            if (keyOffset > 0)
            {
                int keyLen = data[keyOffset] & 0x1F;
                if (keyLen >= 2 && keyOffset + 1 + keyLen <= data.Length && IsAsciiIdentifier(data, keyOffset + 1, keyLen))
                {
                    string firstKey = Encoding.ASCII.GetString(data, keyOffset + 1, keyLen);
                    result = BuildResult(
                        TextAssetEmbeddedMediaKind.StructuredBinary,
                        formatId: "MessagePack",
                        formatName: "MessagePack Binary Map",
                        extension: ".msgpack",
                        mimeType: "application/vnd.msgpack",
                        byteSize: data.Length,
                        metadataFields: new[]
                        {
                            new KeyValuePair<string, string>("Serialization Format", "MessagePack"),
                            new KeyValuePair<string, string>("Root Map Entries", mapEntries.ToString(CultureInfo.InvariantCulture)),
                            new KeyValuePair<string, string>("First Key", firstKey)
                        });
                    return true;
                }
            }
        }

        if (string.Equals(extHint, ".msgpack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".mpk", StringComparison.OrdinalIgnoreCase))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.StructuredBinary,
                formatId: "MessagePack",
                formatName: "MessagePack Binary Data",
                extension: ".msgpack",
                mimeType: "application/vnd.msgpack",
                byteSize: data.Length,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Serialization Format", "MessagePack (Extension Hint)")
                });
            return true;
        }

        if (string.Equals(extHint, ".fbs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".flatbuffers", StringComparison.OrdinalIgnoreCase))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.StructuredBinary,
                formatId: "FlatBuffers",
                formatName: "FlatBuffers Binary Data",
                extension: ".bin",
                mimeType: "application/octet-stream",
                byteSize: data.Length,
                metadataFields: new[]
                {
                    new KeyValuePair<string, string>("Serialization Format", "FlatBuffers")
                });
            return true;
        }

        return false;
    }

    private static bool TryDetectTextFormat(byte[] data, string extHint, out TextAssetFormatDetectionResult result)
    {
        result = null!;

        // 1. UTF-16 LE / BE with BOM
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            // Ensure not UTF-32 LE BOM (FF FE 00 00)
            if (data.Length < 4 || data[2] != 0x00 || data[3] != 0x00)
            {
                result = BuildResult(
                    TextAssetEmbeddedMediaKind.Text,
                    formatId: "UTF-16LE",
                    formatName: "UTF-16 Little-Endian Text",
                    extension: !string.IsNullOrEmpty(extHint) ? extHint : ".txt",
                    mimeType: "text/plain; charset=utf-16le",
                    byteSize: data.Length);
                return true;
            }
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "UTF-16BE",
                formatName: "UTF-16 Big-Endian Text",
                extension: !string.IsNullOrEmpty(extHint) ? extHint : ".txt",
                mimeType: "text/plain; charset=utf-16be",
                byteSize: data.Length);
            return true;
        }

        // 2. Inspect first 4096 bytes for strict UTF-8 and absence of binary control markers
        int sampleBytes = Math.Min(data.Length, 4096);
        int nullCount = 0;
        int controlCount = 0;
        for (int i = 0; i < sampleBytes; i++)
        {
            byte b = data[i];
            if (b == 0) nullCount++;
            else if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D)) controlCount++;
        }

        if (nullCount > 0 || controlCount > Math.Max(4, sampleBytes / 100))
        {
            return false;
        }

        if (!TryDecodeUtf8Prefix(data, sampleBytes, out string sampleText))
        {
            return false;
        }

        string trimmed = sampleText.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (string.Equals(extHint, ".json", StringComparison.OrdinalIgnoreCase)
            || ((trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                && (trimmed.Contains("\":", StringComparison.Ordinal) || trimmed.EndsWith("}", StringComparison.Ordinal) || trimmed.EndsWith("]", StringComparison.Ordinal))))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "JSON",
                formatName: "JSON Text",
                extension: ".json",
                mimeType: "application/json",
                byteSize: data.Length);
            return true;
        }

        if (string.Equals(extHint, ".svg", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "SVG",
                formatName: "SVG Vector Image (XML)",
                extension: ".svg",
                mimeType: "image/svg+xml",
                byteSize: data.Length);
            return true;
        }

        if (string.Equals(extHint, ".xml", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith("<", StringComparison.Ordinal) && trimmed.Contains(">", StringComparison.Ordinal) && trimmed.Contains("</", StringComparison.Ordinal)))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "XML",
                formatName: "XML Document",
                extension: ".xml",
                mimeType: "application/xml",
                byteSize: data.Length);
            return true;
        }

        if (string.Equals(extHint, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".yml", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("%YAML", StringComparison.Ordinal)
            || trimmed.StartsWith("---\n", StringComparison.Ordinal)
            || trimmed.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "YAML",
                formatName: "YAML Document",
                extension: ".yaml",
                mimeType: "application/x-yaml",
                byteSize: data.Length);
            return true;
        }

        if (string.Equals(extHint, ".shader", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".cginc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extHint, ".hlsl", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Shader \"", StringComparison.Ordinal))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "Shader",
                formatName: "Shader Source Text",
                extension: ".shader",
                mimeType: "text/x-shader",
                byteSize: data.Length);
            return true;
        }

        if (string.Equals(extHint, ".lua", StringComparison.OrdinalIgnoreCase))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "Lua",
                formatName: "Lua Script Text",
                extension: ".lua",
                mimeType: "text/x-lua",
                byteSize: data.Length);
            return true;
        }

        if (string.Equals(extHint, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            result = BuildResult(
                TextAssetEmbeddedMediaKind.Text,
                formatId: "CSV",
                formatName: "CSV Table Text",
                extension: ".csv",
                mimeType: "text/csv",
                byteSize: data.Length);
            return true;
        }

        result = BuildResult(
            TextAssetEmbeddedMediaKind.Text,
            formatId: "UTF-8",
            formatName: "UTF-8 Text",
            extension: !string.IsNullOrEmpty(extHint) ? extHint : ".txt",
            mimeType: "text/plain; charset=utf-8",
            byteSize: data.Length);
        return true;
    }

    private static TextAssetFormatDetectionResult BuildResult(
        TextAssetEmbeddedMediaKind mediaKind,
        string formatId,
        string formatName,
        string extension,
        string mimeType,
        int byteSize,
        int width = 0,
        int height = 0,
        double frameRate = 0,
        int totalFrames = 0,
        double durationSeconds = 0,
        string videoCodec = "",
        string audioCodec = "",
        int sampleRate = 0,
        int channels = 0,
        int bitsPerSample = 0,
        int subsoundCount = 0,
        int streamCount = 0,
        int mipMapCount = 0,
        TextureFormat? ddsTextureFormat = null,
        int ddsDataOffset = 0,
        int archiveEntryCount = 0,
        IReadOnlyList<string>? archiveEntries = null,
        IReadOnlyList<KeyValuePair<string, string>>? metadataFields = null,
        bool isPlayableVideo = false,
        bool isPlayableAudio = false,
        bool isPreviewableImage = false,
        bool isRenderableFont = false)
    {
        var fields = metadataFields ?? Array.Empty<KeyValuePair<string, string>>();
        var entries = archiveEntries ?? Array.Empty<string>();

        var sb = new StringBuilder();
        sb.AppendLine($"Format: {formatName}");
        sb.AppendLine($"Category: {mediaKind}");
        sb.AppendLine($"Extension: {extension} ({mimeType})");
        sb.AppendLine($"Size: {byteSize.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        foreach (var kv in fields)
        {
            sb.AppendLine($"{kv.Key}: {kv.Value}");
        }

        return new TextAssetFormatDetectionResult
        {
            MediaKind = mediaKind,
            FormatId = formatId,
            FormatName = formatName,
            RecommendedExtension = extension,
            MimeType = mimeType,
            ByteSize = byteSize,
            Width = width,
            Height = height,
            FrameRate = frameRate,
            TotalFrames = totalFrames,
            DurationSeconds = durationSeconds,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            SubsoundCount = subsoundCount,
            StreamCount = streamCount,
            MipMapCount = mipMapCount,
            DdsTextureFormat = ddsTextureFormat,
            DdsDataOffset = ddsDataOffset,
            ArchiveEntryCount = archiveEntryCount,
            ArchiveEntries = entries,
            MetadataFields = fields,
            IsPlayableVideo = isPlayableVideo,
            IsPlayableAudio = isPlayableAudio,
            IsPreviewableImage = isPreviewableImage,
            IsRenderableFont = isRenderableFont,
            SummaryText = sb.ToString().TrimEnd()
        };
    }

    private static string ExtractExtensionHint(string? assetNameHint, string? containerHint)
    {
        if (!string.IsNullOrWhiteSpace(assetNameHint))
        {
            var ext = TryGetCleanExtension(assetNameHint);
            if (!string.IsNullOrEmpty(ext))
            {
                return ext;
            }
        }

        if (!string.IsNullOrWhiteSpace(containerHint))
        {
            var ext = TryGetCleanExtension(containerHint);
            if (!string.IsNullOrEmpty(ext))
            {
                return ext;
            }
        }

        return string.Empty;
    }

    private static string TryGetCleanExtension(string pathOrName)
    {
        try
        {
            int dot = pathOrName.LastIndexOf('.');
            if (dot < 0 || dot == pathOrName.Length - 1)
            {
                return string.Empty;
            }

            var ext = pathOrName.Substring(dot).Trim().ToLowerInvariant();
            if (ext.Length is < 2 or > 12)
            {
                return string.Empty;
            }

            for (int i = 1; i < ext.Length; i++)
            {
                char c = ext[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return string.Empty;
                }
            }

            return ext;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatWaveFormatTag(ushort tag)
    {
        return tag switch
        {
            0x0001 => "PCM",
            0x0002 => "MS ADPCM",
            0x0003 => "IEEE Float",
            0x0006 => "A-Law",
            0x0007 => "Mu-Law",
            0x0011 => "IMA ADPCM",
            0x0050 => "MPEG Layer I/II",
            0x0055 => "MP3 (MPEG Layer III)",
            0x0161 => "WMA v2",
            0x0162 => "WMA Pro",
            0x2000 => "AC-3",
            0xFFFE => "WAVE_FORMAT_EXTENSIBLE",
            _ => $"0x{tag:X4}"
        };
    }

    private static int DecodeIeee80ExtendedSampleRate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10)
        {
            return 0;
        }

        int exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(2, 8));
        if (exponent == 0 && mantissa == 0)
        {
            return 0;
        }

        double value = Math.ScaleB((double)mantissa, exponent - 16383 - 63);
        return value is > 0 and < 10_000_000 ? (int)Math.Round(value) : 0;
    }

    private static bool TryDecodeUtf8Prefix(byte[] data, int maxBytes, out string text)
    {
        text = string.Empty;
        int len = Math.Min(data.Length, maxBytes);
        // Trim incomplete trailing multi-byte UTF-8 sequence at boundary
        if (len < data.Length && len >= 4)
        {
            int check = len - 1;
            int cont = 0;
            while (check >= 0 && cont < 3 && (data[check] & 0xC0) == 0x80)
            {
                check--;
                cont++;
            }
            if (check >= 0 && data[check] >= 0xC0)
            {
                byte lead = data[check];
                int expectedLen = (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
                if (len - check < expectedLen)
                {
                    len = check;
                }
            }
        }

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data, 0, len);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesAscii(byte[] data, int offset, string ascii)
    {
        if (offset < 0 || offset + ascii.Length > data.Length)
        {
            return false;
        }

        for (int i = 0; i < ascii.Length; i++)
        {
            if (data[offset + i] != (byte)ascii[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPrintableFourCC(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return false;
        for (int i = 0; i < 4; i++)
        {
            byte b = data[offset + i];
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    private static bool IsAsciiIdentifier(byte[] data, int offset, int length)
    {
        if (offset + length > data.Length || length <= 0) return false;
        for (int i = 0; i < length; i++)
        {
            byte b = data[offset + i];
            bool ok = (b is >= (byte)'a' and <= (byte)'z')
                || (b is >= (byte)'A' and <= (byte)'Z')
                || (b is >= (byte)'0' and <= (byte)'9')
                || b is (byte)'_' or (byte)'-' or (byte)'.';
            if (!ok) return false;
        }
        return true;
    }

    private static string ReadCleanFourCC(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
        {
            return string.Empty;
        }

        Span<char> chars = stackalloc char[4];
        int len = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = data[offset + i];
            if (b is >= 0x20 and <= 0x7E)
            {
                chars[len++] = (char)b;
            }
        }
        return len > 0 ? new string(chars.Slice(0, len)).Trim() : string.Empty;
    }

    private static string ReadCleanAsciiString(byte[] data, int offset, int length)
    {
        int end = Math.Min(data.Length, offset + length);
        var sb = new StringBuilder(Math.Max(0, end - offset));
        for (int i = offset; i < end; i++)
        {
            byte b = data[i];
            if (b == 0) break;
            if (b is >= 0x20 and <= 0x7E)
            {
                sb.Append((char)b);
            }
        }
        return sb.ToString().Trim();
    }

    private static string ReadNullTerminatedAscii(byte[] data, ref int offset, int maxLen)
    {
        int start = offset;
        int limit = Math.Min(data.Length, start + maxLen);
        while (offset < limit && data[offset] != 0)
        {
            offset++;
        }
        string result = offset > start ? ReadCleanAsciiString(data, start, offset - start) : string.Empty;
        if (offset < data.Length && data[offset] == 0)
        {
            offset++;
        }
        return result;
    }

    private static int FindPattern(byte[] data, int limit, params byte[] pattern)
    {
        int end = Math.Min(data.Length, limit) - pattern.Length;
        for (int i = 0; i <= end; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static int FindAsciiPattern(byte[] data, int limit, string pattern)
    {
        int end = Math.Min(data.Length, limit) - pattern.Length;
        for (int i = 0; i <= end; i++)
        {
            if (MatchesAscii(data, i, pattern))
            {
                return i;
            }
        }
        return -1;
    }

    private static ushort ReadUInt16LE(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static ushort ReadUInt16BE(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint ReadUInt32LE(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static int ReadInt32LE(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
}
