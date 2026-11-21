using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class MovieTextureMetadata
    {
        public string AssetName { get; init; } = string.Empty;
        public string DetectedFormat { get; init; } = "Unknown";
        public string VideoCodec { get; init; } = "Unknown";
        public string RecommendedExtension { get; init; } = ".ogv";
        public int Width { get; init; }
        public int Height { get; init; }
        public double FrameRate { get; init; }
        public uint FrameRateNumerator { get; init; }
        public uint FrameRateDenominator { get; init; }
        public int NominalBitrateBps { get; init; }
        public string TheoraVersion { get; init; } = string.Empty;
        public int ByteSize { get; init; }
        public bool HasMovieData { get; init; }
        public bool HasEmbeddedAudio { get; init; }
        public string EmbeddedAudioCodec { get; init; } = string.Empty;
        public int EmbeddedAudioChannels { get; init; }
        public int EmbeddedAudioSampleRate { get; init; }
        public bool HasLinkedAudioClip { get; init; }
        public string LinkedAudioClipName { get; init; } = string.Empty;
        public int LinkedAudioChannels { get; init; }
        public int LinkedAudioFrequency { get; init; }
        public bool IsLooping { get; init; }
        public string ResolutionText { get; init; } = string.Empty;
        public string FrameRateText { get; init; } = string.Empty;
        public string FormatLabelText { get; init; } = string.Empty;
        public string AudioLabelText { get; init; } = string.Empty;
        public string ReadyInfoText { get; init; } = string.Empty;
        public string SummaryText { get; init; } = string.Empty;
    }

    /// <summary>
    /// Pure C# header and metadata inspector for Unity <see cref="MovieTexture"/> assets (ClassID 152).
    /// Supports Ogg Theora (<c>OggS</c> + <c>\x80theora</c>) identification header decoding, embedded
    /// Ogg Vorbis/Opus stream detection, linked <see cref="AudioClip"/> metadata correlation, and fallback
    /// container sniffing for AVI, MP4/MOV, WebM/MKV, MPEG, FLV, and WMV/ASF.
    /// </summary>
    public static class MovieTexturePreviewService
    {
        private const int MaxHeaderScanBytes = 256 * 1024;

        private static ReadOnlySpan<byte> TheoraHeaderMagic => new byte[]
        {
            0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a'
        };

        private static ReadOnlySpan<byte> VorbisHeaderMagic => new byte[]
        {
            0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'
        };

        private static ReadOnlySpan<byte> OpusHeaderMagic => new byte[]
        {
            (byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'H', (byte)'e', (byte)'a', (byte)'d'
        };

        private static ReadOnlySpan<byte> AsfHeaderMagic => new byte[]
        {
            0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11
        };

        public static MovieTextureMetadata Inspect(MovieTexture? movieTexture)
        {
            if (movieTexture == null)
            {
                return InspectBytes(ReadOnlySpan<byte>.Empty, assetName: "MovieTexture");
            }

            var hasLinkedClip = false;
            var linkedName = string.Empty;
            var linkedChannels = 0;
            var linkedFreq = 0;

            if (movieTexture.m_AudioClip != null && !movieTexture.m_AudioClip.IsNull)
            {
                hasLinkedClip = true;
                AudioClip? resolvedClip = null;
                if (!movieTexture.m_AudioClip.TryGet(out resolvedClip) || resolvedClip == null)
                {
                    resolvedClip = TryResolveLinkedAudioClipFromAssetsFile(movieTexture.m_AudioClip);
                }

                if (resolvedClip != null)
                {
                    linkedName = !string.IsNullOrWhiteSpace(resolvedClip.m_Name)
                        ? resolvedClip.m_Name
                        : $"AudioClip (PathID {movieTexture.m_AudioClip.m_PathID})";
                    linkedChannels = resolvedClip.m_Channels;
                    linkedFreq = resolvedClip.m_Frequency;
                }
                else
                {
                    linkedName = $"PPtr<AudioClip> (FileID {movieTexture.m_AudioClip.m_FileID}, PathID {movieTexture.m_AudioClip.m_PathID})";
                }
            }

            var movieBytes = movieTexture.m_MovieData ?? Array.Empty<byte>();
            var name = !string.IsNullOrWhiteSpace(movieTexture.m_Name) ? movieTexture.m_Name : "MovieTexture";

            return InspectBytes(
                movieBytes,
                assetName: name,
                isLooping: movieTexture.m_Loop,
                hasLinkedAudioClip: hasLinkedClip,
                linkedAudioClipName: linkedName,
                linkedAudioChannels: linkedChannels,
                linkedAudioFrequency: linkedFreq);
        }

        public static MovieTextureMetadata InspectBytes(
            ReadOnlySpan<byte> movieData,
            string assetName = "MovieTexture",
            bool isLooping = false,
            bool hasLinkedAudioClip = false,
            string? linkedAudioClipName = null,
            int linkedAudioChannels = 0,
            int linkedAudioFrequency = 0)
        {
            var resolvedAssetName = string.IsNullOrWhiteSpace(assetName) ? "MovieTexture" : assetName;
            var resolvedLinkedName = linkedAudioClipName ?? string.Empty;
            var byteSize = movieData.Length;

            if (byteSize == 0)
            {
                var emptyAudioText = FormatAudioLabelText(
                    hasEmbeddedAudio: false,
                    embeddedCodec: string.Empty,
                    embeddedChannels: 0,
                    embeddedSampleRate: 0,
                    hasLinkedAudioClip: hasLinkedAudioClip,
                    linkedAudioClipName: resolvedLinkedName,
                    linkedChannels: linkedAudioChannels,
                    linkedFrequency: linkedAudioFrequency);

                var emptySummary = BuildSummaryText(
                    resolvedAssetName,
                    detectedFormat: "Empty",
                    videoCodec: "None",
                    extension: ".ogv",
                    byteSize: 0,
                    width: 0,
                    height: 0,
                    frameRate: 0,
                    theoraVersion: string.Empty,
                    nominalBitrateBps: 0,
                    isLooping: isLooping,
                    audioText: emptyAudioText,
                    hasMovieData: false);

                return new MovieTextureMetadata
                {
                    AssetName = resolvedAssetName,
                    DetectedFormat = "Empty",
                    VideoCodec = "None",
                    RecommendedExtension = ".ogv",
                    ByteSize = 0,
                    HasMovieData = false,
                    HasLinkedAudioClip = hasLinkedAudioClip,
                    LinkedAudioClipName = resolvedLinkedName,
                    LinkedAudioChannels = linkedAudioChannels,
                    LinkedAudioFrequency = linkedAudioFrequency,
                    IsLooping = isLooping,
                    ResolutionText = "Resolution: N/A (empty MovieData)",
                    FrameRateText = "Frame Rate: N/A",
                    FormatLabelText = "Format: Empty (0 bytes)",
                    AudioLabelText = emptyAudioText,
                    ReadyInfoText = "MovieTexture contains no embedded video data.",
                    SummaryText = emptySummary
                };
            }

            string detectedFormat = "Unknown";
            string videoCodec = "Unknown";
            string recommendedExtension = ".ogv";
            int width = 0;
            int height = 0;
            double frameRate = 0.0;
            uint fpsNum = 0;
            uint fpsDen = 0;
            int nominalBitrateBps = 0;
            string theoraVersion = string.Empty;
            bool hasEmbeddedAudio = false;
            string embeddedAudioCodec = string.Empty;
            int embeddedAudioChannels = 0;
            int embeddedAudioSampleRate = 0;

            var scanSlice = movieData.Length > MaxHeaderScanBytes
                ? movieData.Slice(0, MaxHeaderScanBytes)
                : movieData;

            if (IsOggContainer(scanSlice) || FindSequence(scanSlice, TheoraHeaderMagic) >= 0)
            {
                recommendedExtension = ".ogv";
                if (TryParseOggTheora(
                    scanSlice,
                    out width,
                    out height,
                    out frameRate,
                    out fpsNum,
                    out fpsDen,
                    out nominalBitrateBps,
                    out theoraVersion,
                    out hasEmbeddedAudio,
                    out embeddedAudioCodec,
                    out embeddedAudioChannels,
                    out embeddedAudioSampleRate))
                {
                    detectedFormat = "Ogg Theora";
                    videoCodec = "Theora";
                }
                else
                {
                    TryParseOggAudioHeaders(scanSlice, out hasEmbeddedAudio, out embeddedAudioCodec, out embeddedAudioChannels, out embeddedAudioSampleRate);
                    detectedFormat = "Ogg Media";
                    videoCodec = "Ogg";
                }
            }
            else if (TryDetectAvi(
                scanSlice,
                out videoCodec,
                out width,
                out height,
                out frameRate,
                out hasEmbeddedAudio,
                out embeddedAudioCodec,
                out embeddedAudioChannels,
                out embeddedAudioSampleRate))
            {
                detectedFormat = "AVI";
                recommendedExtension = ".avi";
            }
            else if (TryDetectMp4OrMov(scanSlice, out detectedFormat, out videoCodec, out recommendedExtension, out width, out height))
            {
                // Populated by TryDetectMp4OrMov
            }
            else if (TryDetectEbml(scanSlice, out detectedFormat, out videoCodec, out recommendedExtension))
            {
                // Populated by TryDetectEbml
            }
            else if (TryDetectMpeg(scanSlice, out detectedFormat, out videoCodec, out recommendedExtension))
            {
                // Populated by TryDetectMpeg
            }
            else if (scanSlice.Length >= 4
                     && scanSlice[0] == (byte)'F'
                     && scanSlice[1] == (byte)'L'
                     && scanSlice[2] == (byte)'V'
                     && scanSlice[3] == 0x01)
            {
                detectedFormat = "FLV";
                videoCodec = "FLV";
                recommendedExtension = ".flv";
                if (scanSlice.Length >= 5 && (scanSlice[4] & 0x04) != 0)
                {
                    hasEmbeddedAudio = true;
                    embeddedAudioCodec = "FLV Audio";
                }
            }
            else if (scanSlice.Length >= 8 && scanSlice.Slice(0, 8).SequenceEqual(AsfHeaderMagic))
            {
                detectedFormat = "WMV/ASF";
                videoCodec = "WMV";
                recommendedExtension = ".wmv";
            }

            var resolutionText = width > 0 && height > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Resolution: {width}x{height}")
                : "Resolution: Probed by FFmpeg on load";

            var frameRateText = frameRate > 0.0
                ? string.Create(CultureInfo.InvariantCulture, $"Frame Rate: {frameRate:F2} FPS")
                : "Frame Rate: Probed by FFmpeg on load";

            var formatLabelText = !string.IsNullOrEmpty(videoCodec)
                                  && !string.Equals(detectedFormat, videoCodec, StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(videoCodec, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? $"Format: {detectedFormat} ({videoCodec}, {recommendedExtension}) | {FormatByteSize(byteSize)}"
                : $"Format: {detectedFormat} ({recommendedExtension}) | {FormatByteSize(byteSize)}";

            var audioLabelText = FormatAudioLabelText(
                hasEmbeddedAudio,
                embeddedAudioCodec,
                embeddedAudioChannels,
                embeddedAudioSampleRate,
                hasLinkedAudioClip,
                resolvedLinkedName,
                linkedAudioChannels,
                linkedAudioFrequency);

            var loopStatus = isLooping ? "Loop: enabled (m_Loop = true)" : "Loop: disabled (m_Loop = false)";
            var readyInfoText = $"MovieTexture ({detectedFormat}, {FormatByteSize(byteSize)}) | {loopStatus}. Press Play to start preview.";

            var summaryText = BuildSummaryText(
                resolvedAssetName,
                detectedFormat,
                videoCodec,
                recommendedExtension,
                byteSize,
                width,
                height,
                frameRate,
                theoraVersion,
                nominalBitrateBps,
                isLooping,
                audioLabelText,
                hasMovieData: true);

            return new MovieTextureMetadata
            {
                AssetName = resolvedAssetName,
                DetectedFormat = detectedFormat,
                VideoCodec = videoCodec,
                RecommendedExtension = recommendedExtension,
                Width = width,
                Height = height,
                FrameRate = frameRate,
                FrameRateNumerator = fpsNum,
                FrameRateDenominator = fpsDen,
                NominalBitrateBps = nominalBitrateBps,
                TheoraVersion = theoraVersion,
                ByteSize = byteSize,
                HasMovieData = true,
                HasEmbeddedAudio = hasEmbeddedAudio,
                EmbeddedAudioCodec = embeddedAudioCodec,
                EmbeddedAudioChannels = embeddedAudioChannels,
                EmbeddedAudioSampleRate = embeddedAudioSampleRate,
                HasLinkedAudioClip = hasLinkedAudioClip,
                LinkedAudioClipName = resolvedLinkedName,
                LinkedAudioChannels = linkedAudioChannels,
                LinkedAudioFrequency = linkedAudioFrequency,
                IsLooping = isLooping,
                ResolutionText = resolutionText,
                FrameRateText = frameRateText,
                FormatLabelText = formatLabelText,
                AudioLabelText = audioLabelText,
                ReadyInfoText = readyInfoText,
                SummaryText = summaryText
            };
        }

        public static bool TryParseOggTheora(
            ReadOnlySpan<byte> data,
            out int width,
            out int height,
            out double frameRate,
            out uint fpsNum,
            out uint fpsDen,
            out int nominalBitrateBps,
            out string theoraVersion,
            out bool hasEmbeddedAudio,
            out string embeddedAudioCodec,
            out int embeddedAudioChannels,
            out int embeddedAudioSampleRate)
        {
            width = 0;
            height = 0;
            frameRate = 0.0;
            fpsNum = 0;
            fpsDen = 0;
            nominalBitrateBps = 0;
            theoraVersion = string.Empty;

            TryParseOggAudioHeaders(
                data,
                out hasEmbeddedAudio,
                out embeddedAudioCodec,
                out embeddedAudioChannels,
                out embeddedAudioSampleRate);

            var idx = FindSequence(data, TheoraHeaderMagic);
            if (idx < 0 || idx + 42 > data.Length)
            {
                return false;
            }

            var header = data.Slice(idx, 42);
            var vmaj = header[7];
            var vmin = header[8];
            var vrev = header[9];
            theoraVersion = string.Create(CultureInfo.InvariantCulture, $"{vmaj}.{vmin}.{vrev}");

            // 16-bit big-endian frame width/height in macroblocks (16x16 pixels each)
            int fmbw = (header[10] << 8) | header[11];
            int fmbh = (header[12] << 8) | header[13];

            // 24-bit big-endian picture display width/height in pixels
            int picw = (header[14] << 16) | (header[15] << 8) | header[16];
            int pich = (header[17] << 16) | (header[18] << 8) | header[19];

            width = picw > 0 ? picw : (fmbw > 0 ? fmbw * 16 : 0);
            height = pich > 0 ? pich : (fmbh > 0 ? fmbh * 16 : 0);

            // 32-bit big-endian frame rate numerator and denominator
            fpsNum = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(22, 4));
            fpsDen = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(26, 4));
            if (fpsNum > 0 && fpsDen > 0)
            {
                var computed = (double)fpsNum / fpsDen;
                if (!double.IsNaN(computed) && !double.IsInfinity(computed) && computed > 0.0 && computed <= 1000.0)
                {
                    frameRate = computed;
                }
            }

            // 24-bit big-endian nominal bitrate (bits per second)
            nominalBitrateBps = (header[37] << 16) | (header[38] << 8) | header[39];
            return true;
        }

        private static AudioClip? TryResolveLinkedAudioClipFromAssetsFile(PPtr<AudioClip> audioClipPPtr)
        {
            try
            {
                if (!audioClipPPtr.TryGetAssetsFile(out var sourceFile)
                    || sourceFile == null
                    || sourceFile.reader == null
                    || !sourceFile.ObjectInfoDic.TryGetValue(audioClipPPtr.m_PathID, out var objInfo))
                {
                    return null;
                }

                lock (sourceFile.reader)
                {
                    if (sourceFile.TryGetObject(audioClipPPtr.m_PathID, out var existing) && existing is AudioClip existingClip)
                    {
                        return existingClip;
                    }

                    var objReader = new ObjectReader(sourceFile.reader, sourceFile, objInfo);
                    var created = AssetsManager.CreateObjectFromReader(objReader);
                    if (created is AudioClip createdClip)
                    {
                        sourceFile.AddObject(createdClip);
                        return createdClip;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool IsOggContainer(ReadOnlySpan<byte> data)
        {
            return data.Length >= 4
                && data[0] == (byte)'O'
                && data[1] == (byte)'g'
                && data[2] == (byte)'g'
                && data[3] == (byte)'S';
        }

        private static void TryParseOggAudioHeaders(
            ReadOnlySpan<byte> data,
            out bool hasEmbeddedAudio,
            out string embeddedAudioCodec,
            out int channels,
            out int sampleRate)
        {
            hasEmbeddedAudio = false;
            embeddedAudioCodec = string.Empty;
            channels = 0;
            sampleRate = 0;

            var vorbisIdx = FindSequence(data, VorbisHeaderMagic);
            if (vorbisIdx >= 0 && vorbisIdx + 16 <= data.Length)
            {
                var vh = data.Slice(vorbisIdx, 16);
                uint version = BinaryPrimitives.ReadUInt32LittleEndian(vh.Slice(7, 4));
                int ch = vh[11];
                int sr = (int)BinaryPrimitives.ReadUInt32LittleEndian(vh.Slice(12, 4));
                if (version == 0 && ch > 0 && sr > 0)
                {
                    hasEmbeddedAudio = true;
                    embeddedAudioCodec = "Vorbis";
                    channels = ch;
                    sampleRate = sr;
                    return;
                }
            }

            var opusIdx = FindSequence(data, OpusHeaderMagic);
            if (opusIdx >= 0 && opusIdx + 16 <= data.Length)
            {
                var oh = data.Slice(opusIdx, 16);
                int ch = oh[9];
                int sr = (int)BinaryPrimitives.ReadUInt32LittleEndian(oh.Slice(12, 4));
                if (ch > 0)
                {
                    hasEmbeddedAudio = true;
                    embeddedAudioCodec = "Opus";
                    channels = ch;
                    sampleRate = sr > 0 ? sr : 48000;
                }
            }
        }

        private static bool TryDetectAvi(
            ReadOnlySpan<byte> data,
            out string videoCodec,
            out int width,
            out int height,
            out double frameRate,
            out bool hasEmbeddedAudio,
            out string embeddedAudioCodec,
            out int audioChannels,
            out int audioSampleRate)
        {
            videoCodec = "AVI";
            width = 0;
            height = 0;
            frameRate = 0.0;
            hasEmbeddedAudio = false;
            embeddedAudioCodec = string.Empty;
            audioChannels = 0;
            audioSampleRate = 0;

            if (data.Length < 12
                || data[0] != (byte)'R'
                || data[1] != (byte)'I'
                || data[2] != (byte)'F'
                || data[3] != (byte)'F'
                || data[8] != (byte)'A'
                || data[9] != (byte)'V'
                || data[10] != (byte)'I'
                || (data[11] != (byte)' ' && data[11] != (byte)'X'))
            {
                return false;
            }

            // Look for MainAVIHeader ('avih') chunk
            ReadOnlySpan<byte> avihMagic = new byte[] { (byte)'a', (byte)'v', (byte)'i', (byte)'h' };
            var avihIdx = FindSequence(data, avihMagic);
            if (avihIdx >= 0 && avihIdx + 8 + 40 <= data.Length)
            {
                var avihPayload = data.Slice(avihIdx + 8, 40);
                uint usecPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(avihPayload.Slice(0, 4));
                if (usecPerFrame > 0)
                {
                    var fps = 1_000_000.0 / usecPerFrame;
                    if (fps > 0.0 && fps <= 1000.0)
                    {
                        frameRate = fps;
                    }
                }

                int w = BinaryPrimitives.ReadInt32LittleEndian(avihPayload.Slice(32, 4));
                int h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(avihPayload.Slice(36, 4)));
                if (w > 0 && h > 0)
                {
                    width = w;
                    height = h;
                }
            }

            // Look for stream headers ('strh') for video ('vids') and audio ('auds')
            ReadOnlySpan<byte> vidsMagic = new byte[] { (byte)'v', (byte)'i', (byte)'d', (byte)'s' };
            var vidsIdx = FindSequence(data, vidsMagic);
            if (vidsIdx >= 0 && vidsIdx + 8 <= data.Length)
            {
                var fourCc = ExtractPrintableFourCc(data.Slice(vidsIdx + 4, 4));
                if (!string.IsNullOrWhiteSpace(fourCc))
                {
                    videoCodec = fourCc;
                }

                // Check strh rate/scale at vidsIdx + 20 (dwScale) and vidsIdx + 24 (dwRate)
                if (frameRate <= 0.0 && vidsIdx + 28 <= data.Length)
                {
                    uint dwScale = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(vidsIdx + 20, 4));
                    uint dwRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(vidsIdx + 24, 4));
                    if (dwScale > 0 && dwRate > 0)
                    {
                        var fps = (double)dwRate / dwScale;
                        if (fps > 0.0 && fps <= 1000.0)
                        {
                            frameRate = fps;
                        }
                    }
                }
            }

            ReadOnlySpan<byte> audsMagic = new byte[] { (byte)'a', (byte)'u', (byte)'d', (byte)'s' };
            var audsIdx = FindSequence(data, audsMagic);
            if (audsIdx >= 0)
            {
                hasEmbeddedAudio = true;
                embeddedAudioCodec = "AVI Audio";
            }

            return true;
        }

        private static bool TryDetectMp4OrMov(
            ReadOnlySpan<byte> data,
            out string detectedFormat,
            out string videoCodec,
            out string recommendedExtension,
            out int width,
            out int height)
        {
            detectedFormat = "Unknown";
            videoCodec = "Unknown";
            recommendedExtension = ".ogv";
            width = 0;
            height = 0;

            if (data.Length < 12
                || data[4] != (byte)'f'
                || data[5] != (byte)'t'
                || data[6] != (byte)'y'
                || data[7] != (byte)'p')
            {
                return false;
            }

            var brand = ExtractPrintableFourCc(data.Slice(8, 4));
            if (string.Equals(brand, "qt", StringComparison.OrdinalIgnoreCase))
            {
                detectedFormat = "QuickTime MOV";
                videoCodec = "QuickTime";
                recommendedExtension = ".mov";
            }
            else if (string.Equals(brand, "M4V", StringComparison.OrdinalIgnoreCase))
            {
                detectedFormat = "M4V";
                videoCodec = "H.264/MP4";
                recommendedExtension = ".m4v";
            }
            else
            {
                detectedFormat = "MP4";
                videoCodec = string.IsNullOrWhiteSpace(brand) ? "ISO BMFF" : $"MP4 ({brand})";
                recommendedExtension = ".mp4";
            }

            // Try extracting dimensions from 'tkhd' (TrackHeaderBox) if present in the header scan
            ReadOnlySpan<byte> tkhdMagic = new byte[] { (byte)'t', (byte)'k', (byte)'h', (byte)'d' };
            int searchOffset = 0;
            while (searchOffset + 12 <= data.Length)
            {
                var relIdx = FindSequence(data.Slice(searchOffset), tkhdMagic);
                if (relIdx < 0)
                {
                    break;
                }

                var tkhdIdx = searchOffset + relIdx;
                searchOffset = tkhdIdx + 4;
                if (tkhdIdx + 4 > data.Length)
                {
                    break;
                }

                byte version = data[tkhdIdx + 4];
                int widthOffset = version == 1 ? tkhdIdx + 4 + 92 : tkhdIdx + 4 + 80;
                if (widthOffset + 8 <= data.Length)
                {
                    uint fixedW = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(widthOffset, 4));
                    uint fixedH = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(widthOffset + 4, 4));
                    int w = (int)(fixedW >> 16);
                    int h = (int)(fixedH >> 16);
                    if (w > 0 && h > 0 && w <= 16384 && h <= 16384)
                    {
                        width = w;
                        height = h;
                        break;
                    }
                }
            }

            return true;
        }

        private static bool TryDetectEbml(
            ReadOnlySpan<byte> data,
            out string detectedFormat,
            out string videoCodec,
            out string recommendedExtension)
        {
            detectedFormat = "Unknown";
            videoCodec = "Unknown";
            recommendedExtension = ".ogv";

            if (data.Length < 4
                || data[0] != 0x1A
                || data[1] != 0x45
                || data[2] != 0xDF
                || data[3] != 0xA3)
            {
                return false;
            }

            var headerCheck = data.Length > 128 ? data.Slice(0, 128) : data;
            ReadOnlySpan<byte> webmMagic = new byte[] { (byte)'w', (byte)'e', (byte)'b', (byte)'m' };
            if (FindSequence(headerCheck, webmMagic) >= 0)
            {
                detectedFormat = "WebM";
                videoCodec = "VP8/VP9";
                recommendedExtension = ".webm";
            }
            else
            {
                detectedFormat = "Matroska (MKV)";
                videoCodec = "Matroska";
                recommendedExtension = ".mkv";
            }

            return true;
        }

        private static bool TryDetectMpeg(
            ReadOnlySpan<byte> data,
            out string detectedFormat,
            out string videoCodec,
            out string recommendedExtension)
        {
            detectedFormat = "Unknown";
            videoCodec = "Unknown";
            recommendedExtension = ".ogv";

            if (data.Length >= 4
                && data[0] == 0x00
                && data[1] == 0x00
                && data[2] == 0x01
                && (data[3] == 0xBA || data[3] == 0xB3))
            {
                detectedFormat = "MPEG";
                videoCodec = "MPEG-1/2";
                recommendedExtension = ".mpg";
                return true;
            }

            return false;
        }

        private static string FormatAudioLabelText(
            bool hasEmbeddedAudio,
            string embeddedCodec,
            int embeddedChannels,
            int embeddedSampleRate,
            bool hasLinkedAudioClip,
            string linkedAudioClipName,
            int linkedChannels,
            int linkedFrequency)
        {
            if (!hasEmbeddedAudio && !hasLinkedAudioClip)
            {
                return "Audio: no embedded track or linked AudioClip";
            }

            var sb = new StringBuilder("Audio: ");
            if (hasEmbeddedAudio)
            {
                sb.Append($"Embedded {(!string.IsNullOrWhiteSpace(embeddedCodec) ? embeddedCodec : "track")}");
                if (embeddedChannels > 0)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" ({embeddedChannels}ch"));
                    if (embeddedSampleRate > 0)
                    {
                        sb.Append(string.Create(CultureInfo.InvariantCulture, $" {embeddedSampleRate}Hz"));
                    }
                    sb.Append(')');
                }
            }

            if (hasLinkedAudioClip)
            {
                if (hasEmbeddedAudio)
                {
                    sb.Append(" | ");
                }

                sb.Append($"Linked AudioClip: {linkedAudioClipName}");
                if (linkedChannels > 0)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" ({linkedChannels}ch"));
                    if (linkedFrequency > 0)
                    {
                        sb.Append(string.Create(CultureInfo.InvariantCulture, $" {linkedFrequency}Hz"));
                    }
                    sb.Append(')');
                }
            }

            return sb.ToString();
        }

        private static string BuildSummaryText(
            string assetName,
            string detectedFormat,
            string videoCodec,
            string extension,
            int byteSize,
            int width,
            int height,
            double frameRate,
            string theoraVersion,
            int nominalBitrateBps,
            bool isLooping,
            string audioText,
            bool hasMovieData)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MovieTexture: {assetName}");
            sb.AppendLine($"Format: {detectedFormat} (Codec: {videoCodec}, Extension: {extension})");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Data Size: {byteSize:N0} bytes ({FormatByteSize(byteSize)})"));
            sb.AppendLine($"Loop (m_Loop): {(isLooping ? "True" : "False")}");

            if (width > 0 && height > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Dimensions: {width}x{height}"));
            }

            if (frameRate > 0.0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Frame Rate: {frameRate:F2} FPS"));
            }

            if (!string.IsNullOrEmpty(theoraVersion))
            {
                sb.AppendLine($"Theora Version: {theoraVersion}");
            }

            if (nominalBitrateBps > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Nominal Bitrate: {nominalBitrateBps / 1000.0:F1} kbps"));
            }

            sb.AppendLine(audioText);

            if (!hasMovieData)
            {
                sb.AppendLine();
                sb.AppendLine("No embedded m_MovieData bytes are stored in this MovieTexture asset.");
            }

            return sb.ToString().TrimEnd();
        }

        public static string FormatByteSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            if (bytes < 1024)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
            }

            if (bytes < 1024 * 1024)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB");
            }

            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F2} MB");
        }

        private static int FindSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            if (needle.IsEmpty || haystack.Length < needle.Length)
            {
                return -1;
            }

            return haystack.IndexOf(needle);
        }

        private static string ExtractPrintableFourCc(ReadOnlySpan<byte> span)
        {
            if (span.Length < 4)
            {
                return string.Empty;
            }

            Span<char> chars = stackalloc char[4];
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                byte b = span[i];
                if (b >= 0x20 && b <= 0x7E)
                {
                    chars[count++] = (char)b;
                }
            }

            return count > 0 ? new string(chars.Slice(0, count)).Trim() : string.Empty;
        }
    }
}
