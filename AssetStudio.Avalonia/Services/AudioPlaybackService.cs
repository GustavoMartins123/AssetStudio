using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpegVideoPlayer.Core;

namespace AssetStudio.Avalonia.Services
{
    /// <summary>Decoded/prepared audio bytes produced off the UI thread (PCM WAV, transcoded WAV or raw container).</summary>
    public sealed class AudioPreviewSource
    {
        public AudioPreviewSource(byte[] data, string extension, long durationMs, string sourceDescription)
        {
            Data = data ?? Array.Empty<byte>();
            Extension = AudioPreviewMedia.NormalizeExtension(extension);
            DurationMs = Math.Max(0, durationMs);
            SourceDescription = sourceDescription ?? string.Empty;
        }

        public byte[] Data { get; }
        public string Extension { get; }
        public long DurationMs { get; }
        public string SourceDescription { get; }
    }

    /// <summary>A prepared audio preview materialised as a uniquely named temp file owned by <see cref="AudioPlaybackService"/>.</summary>
    public sealed class AudioPreviewMedia
    {
        public AudioPreviewMedia(string filePath, string extension, long durationMs, string sourceDescription)
        {
            FilePath = filePath;
            Extension = NormalizeExtension(extension);
            DurationMs = Math.Max(0, durationMs);
            SourceDescription = sourceDescription ?? string.Empty;
        }

        public string FilePath { get; }
        public string Extension { get; }
        public long DurationMs { get; }
        public string SourceDescription { get; }

        public bool IsWave => Extension == ".wav";

        /// <summary>
        /// Raw FMOD sample bank that neither <c>UnityAudioDecoder</c> nor the FFmpeg transcoder could turn into PCM.
        /// Such data is never handed to a streaming player (raw FSB5 playback in FFmpeg is unreliable).
        /// </summary>
        public bool IsRawFsb => Extension == ".fsb";

        public static string NormalizeExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".wav";
            }

            var normalized = extension.Trim().ToLowerInvariant();
            if (!normalized.StartsWith(".", StringComparison.Ordinal))
            {
                normalized = "." + normalized;
            }

            return normalized switch
            {
                ".aiff" => ".aif",
                ".mpeg" => ".mp3",
                _ => normalized
            };
        }
    }

    /// <summary>One concrete audio output strategy (WinMM PCM, FFmpeg stream, system player process, ...).</summary>
    public interface IAudioPreviewBackend : IDisposable
    {
        string BackendName { get; }

        /// <summary>Message reported when <see cref="PlayFromStartAsync"/> fails.</summary>
        string PlaybackFailureMessage { get; }

        bool CanSeek { get; }

        bool IsPlaying { get; }

        /// <summary>Playback reached (or is within the premature-end tolerance of) the end of the media.</summary>
        bool IsEnded { get; }

        double PositionSeconds { get; }

        double DurationSeconds { get; }

        /// <summary>Raised (possibly on a player thread) when the backend reports end of media.</summary>
        event EventHandler? EndReached;

        /// <summary>Opens the prepared media; heavy work must run off the calling (UI) thread.</summary>
        Task<bool> LoadAsync(AudioPreviewMedia media, CancellationToken cancellationToken);

        /// <summary>Rewinds and starts playback from zero (may reopen the media off the UI thread).</summary>
        Task<bool> PlayFromStartAsync(CancellationToken cancellationToken);

        /// <summary>Starts / resumes from the current position.</summary>
        void Play();

        void Pause();

        /// <summary>Stops and rewinds to zero.</summary>
        void Stop();

        /// <summary>Synchronous rewind-and-play used for looping.</summary>
        void Restart();

        void Seek(double seconds);

        /// <summary>Volume on the 0..100 scale.</summary>
        void SetVolume(int volume);
    }

    /// <summary>Creates backends and decides which media they can handle (strategy selection lives here, not in MainWindow).</summary>
    public interface IAudioPreviewBackendFactory
    {
        string Name { get; }

        /// <summary>Message reported when this backend cannot open the media.</summary>
        string FailureMessage { get; }

        bool CanHandle(AudioPreviewMedia media);

        IAudioPreviewBackend Create();
    }

    public sealed class DelegateAudioPreviewBackendFactory : IAudioPreviewBackendFactory
    {
        private readonly Func<AudioPreviewMedia, bool> _canHandle;
        private readonly Func<IAudioPreviewBackend> _create;

        public DelegateAudioPreviewBackendFactory(
            string name,
            string failureMessage,
            Func<AudioPreviewMedia, bool> canHandle,
            Func<IAudioPreviewBackend> create)
        {
            Name = name;
            FailureMessage = failureMessage;
            _canHandle = canHandle;
            _create = create;
        }

        public string Name { get; }
        public string FailureMessage { get; }

        public bool CanHandle(AudioPreviewMedia media) => _canHandle(media);

        public IAudioPreviewBackend Create() => _create();
    }

    public enum AudioPreviewResultStatus
    {
        Ready,
        Started,
        Missing,
        Unsupported,
        Failed,
        Cancelled
    }

    public readonly record struct AudioPreviewResult(AudioPreviewResultStatus Status, string Message)
    {
        public bool IsSuccess => Status is AudioPreviewResultStatus.Ready or AudioPreviewResultStatus.Started;

        public static AudioPreviewResult Cancelled { get; } = new(AudioPreviewResultStatus.Cancelled, string.Empty);
    }

    public readonly record struct AudioPlaybackSnapshot(bool HasBackend, long PositionMs, long DurationMs, bool IsPlaying, bool IsEnded);

    /// <summary>Result of analysing raw audio bytes (decoding FSB5 when supported, or detecting container format).</summary>
    public readonly record struct AudioPreviewAnalysisResult(byte[]? RebuiltData, string Extension, long DurationMs, bool DecodedFromFsb);

    /// <summary>
    /// Stateless, off-UI-thread audio preview preparation: FSB5 PCM/ADPCM decoding via <see cref="UnityAudioDecoder"/>,
    /// container sniffing, FFmpeg PCM WAV transcoding with unique temp files, and WAV header duration inspection.
    /// </summary>
    public static class AudioPreviewPreparer
    {
        public delegate bool TranscodeToWavDelegate(string inputPath, string outputPath, CancellationToken cancellationToken);

        public static bool IsFsb5Header(byte[]? data)
        {
            return data != null
                && data.Length >= 4
                && data[0] == (byte)'F'
                && data[1] == (byte)'S'
                && data[2] == (byte)'B'
                && data[3] == (byte)'5';
        }

        public static string FormatAudioClipCompressionInfo(AudioClip audioClip)
        {
            var infoText = "Compression format: ";
            if (audioClip.version[0] < 5)
            {
                return infoText + audioClip.m_Type switch
                {
                    FMODSoundType.ACC => "Acc",
                    FMODSoundType.AIFF => "AIFF",
                    FMODSoundType.IT => "Impulse tracker",
                    FMODSoundType.MOD => "Protracker / Fasttracker MOD",
                    FMODSoundType.MPEG => "MP2/MP3 MPEG",
                    FMODSoundType.OGGVORBIS => "Ogg vorbis",
                    FMODSoundType.S3M => "ScreamTracker 3",
                    FMODSoundType.WAV => "Microsoft WAV",
                    FMODSoundType.XM => "FastTracker 2 XM",
                    FMODSoundType.XMA => "Xbox360 XMA",
                    FMODSoundType.VAG => "PlayStation Portable ADPCM",
                    FMODSoundType.AUDIOQUEUE => "iPhone",
                    _ => "Unknown"
                };
            }

            return infoText + audioClip.m_CompressionFormat switch
            {
                AudioCompressionFormat.PCM => "PCM",
                AudioCompressionFormat.Vorbis => "Vorbis",
                AudioCompressionFormat.ADPCM => "ADPCM",
                AudioCompressionFormat.MP3 => "MP3",
                AudioCompressionFormat.PSMVAG => "PlayStation Portable ADPCM",
                AudioCompressionFormat.HEVAG => "PSVita ADPCM",
                AudioCompressionFormat.XMA => "Xbox360 XMA",
                AudioCompressionFormat.AAC => "AAC",
                AudioCompressionFormat.GCADPCM => "Nintendo 3DS/Wii DSP",
                AudioCompressionFormat.ATRAC9 => "PSVita ATRAC9",
                _ => "Unknown"
            };
        }

        public static AudioPreviewSource? PrepareAudioClip(
            AudioClip audioClip,
            bool isLinux,
            bool preferTranscode,
            CancellationToken cancellationToken,
            string? tempDirectory = null,
            TranscodeToWavDelegate? transcoder = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audioData = audioClip.m_AudioData.GetData();
            cancellationToken.ThrowIfCancellationRequested();
            if (audioData == null || audioData.Length == 0)
            {
                return null;
            }

            var converter = new AudioClipConverter(audioClip);
            var fallbackExtension = AudioPreviewMedia.NormalizeExtension(converter.GetExtensionName());
            var metadataDurationMs = audioClip.m_Length > 0 ? (long)(audioClip.m_Length * 1000.0) : 0L;

            return PrepareRawAudioData(
                audioData,
                fallbackExtension,
                metadataDurationMs,
                isLinux,
                preferTranscode,
                cancellationToken,
                tempDirectory,
                transcoder);
        }

        public static AudioPreviewSource? PrepareRawAudioData(
            byte[] audioData,
            string fallbackExtension,
            long metadataDurationMs,
            bool isLinux,
            bool preferTranscode,
            CancellationToken cancellationToken,
            string? tempDirectory = null,
            TranscodeToWavDelegate? transcoder = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (audioData == null || audioData.Length == 0)
            {
                return null;
            }

            var normalizedFallback = AudioPreviewMedia.NormalizeExtension(fallbackExtension);
            var analysis = AnalyzeAudioData(audioData, normalizedFallback, metadataDurationMs);
            cancellationToken.ThrowIfCancellationRequested();

            var sampleData = analysis.RebuiltData ?? audioData;
            var formatExtension = analysis.Extension;
            var durationMs = analysis.DurationMs > 0 ? analysis.DurationMs : Math.Max(0, metadataDurationMs);
            var sourceDescription = analysis.DecodedFromFsb
                ? "FSB5 -> WAV (PCM)"
                : formatExtension.ToUpperInvariant().TrimStart('.');

            if (ShouldTranscodeAudioForPreview(formatExtension, preferTranscode))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workDir = string.IsNullOrEmpty(tempDirectory) ? Path.GetTempPath() : tempDirectory;
                var inputPath = MediaTempFiles.CreateUniquePath(workDir, "assetstudio_audio_in_", formatExtension);
                var outputPath = MediaTempFiles.CreateUniquePath(workDir, "assetstudio_audio_out_", ".wav");
                try
                {
                    File.WriteAllBytes(inputPath, sampleData);
                    var succeeded = transcoder != null
                        ? transcoder(inputPath, outputPath, cancellationToken)
                        : FFmpegAudioTranscoder.TryTranscodeToPcmWav(inputPath, outputPath, cancellationToken).Success;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (succeeded && File.Exists(outputPath))
                    {
                        var transcoded = File.ReadAllBytes(outputPath);
                        if (transcoded.Length > 0)
                        {
                            sampleData = transcoded;
                            formatExtension = ".wav";
                            sourceDescription += " -> WAV (Transcoded)";
                        }
                    }
                }
                finally
                {
                    MediaTempFiles.TryDeleteNow(inputPath);
                    MediaTempFiles.TryDeleteNow(outputPath);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (formatExtension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && !isLinux)
            {
                var waveDurationMs = TryGetWaveDurationMs(sampleData);
                if (waveDurationMs > 0)
                {
                    durationMs = waveDurationMs;
                }
            }

            return new AudioPreviewSource(sampleData, formatExtension, durationMs, sourceDescription);
        }

        public static AudioPreviewAnalysisResult AnalyzeAudioData(byte[] audioData, string fallbackExtension, long metadataDurationMs = 0)
        {
            byte[]? rebuiltData = null;
            var rebuiltExtension = AudioPreviewMedia.NormalizeExtension(fallbackExtension);
            var decodedFromFsb = false;

            if (IsFsb5Header(audioData))
            {
                try
                {
                    rebuiltData = UnityAudioDecoder.ConvertToWav(audioData);
                    if (rebuiltData != null && rebuiltData.Length > 0)
                    {
                        rebuiltExtension = ".wav";
                        decodedFromFsb = true;
                    }
                    else
                    {
                        rebuiltData = null;
                    }
                }
                catch
                {
                    rebuiltData = null;
                }
            }

            if (rebuiltData == null)
            {
                rebuiltExtension = DetectAudioExtension(audioData, rebuiltExtension);
            }

            return new AudioPreviewAnalysisResult(rebuiltData, rebuiltExtension, Math.Max(0, metadataDurationMs), decodedFromFsb);
        }

        public static bool ShouldTranscodeAudioForPreview(string extension, bool preferTranscode)
        {
            if (preferTranscode)
            {
                return true;
            }

            return AudioPreviewMedia.NormalizeExtension(extension) switch
            {
                ".wav" => false,
                ".aac" or ".aif" or ".aiff" or ".flac" or ".fsb" or ".m4a" or ".mp3" or ".ogg"
                    or ".it" or ".mod" or ".s3m" or ".xm" => true,
                _ => false
            };
        }

        public static string DetectAudioExtension(byte[] data, string fallback)
        {
            if (data == null || data.Length < 4)
            {
                return fallback;
            }

            if (IsFsb5Header(data))
            {
                return ".fsb";
            }

            // MP3 ID3 header
            if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
            {
                return ".mp3";
            }

            // MP3 frame sync (11 bits set)
            if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
            {
                return ".mp3";
            }

            // Ogg container
            if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
            {
                return ".ogg";
            }

            // RIFF WAVE
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46)
            {
                return ".wav";
            }

            // FLAC
            if (data[0] == 0x66 && data[1] == 0x4C && data[2] == 0x61 && data[3] == 0x43)
            {
                return ".flac";
            }

            return fallback;
        }

        public static long TryGetWaveDurationMs(byte[] data)
        {
            try
            {
                if (data == null
                    || data.Length < 44
                    || data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F'
                    || data[8] != (byte)'W' || data[9] != (byte)'A' || data[10] != (byte)'V' || data[11] != (byte)'E')
                {
                    return 0;
                }

                var chunkOffset = 12;
                var byteRate = 0;
                var dataSize = 0;

                while (chunkOffset + 8 <= data.Length)
                {
                    var chunkId = Encoding.ASCII.GetString(data, chunkOffset, 4);
                    var chunkSize = BitConverter.ToInt32(data, chunkOffset + 4);
                    if (chunkSize < 0)
                    {
                        return 0;
                    }

                    if (chunkId.Equals("fmt ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (chunkSize >= 16 && chunkOffset + 8 + 16 <= data.Length)
                        {
                            byteRate = BitConverter.ToInt32(data, chunkOffset + 8 + 8);
                        }
                    }
                    else if (chunkId.Equals("data", StringComparison.OrdinalIgnoreCase))
                    {
                        dataSize = chunkSize;
                        break;
                    }

                    var paddedSize = chunkSize + (chunkSize & 1);
                    if (paddedSize > data.Length - chunkOffset - 8)
                    {
                        break;
                    }

                    chunkOffset += 8 + paddedSize;
                }

                if (byteRate > 0 && dataSize > 0)
                {
                    return (long)((double)dataSize / byteRate * 1000.0);
                }
            }
            catch
            {
            }

            return 0;
        }
    }

    /// <summary>
    /// Owns the audio preview session: off-UI-thread decode + unique temp file, ordered backend fallback,
    /// stale-load cancellation on rapid clip switching, end-of-media marshalling and background temp cleanup.
    /// </summary>
    public sealed class AudioPlaybackService : IDisposable
    {
        public const long PrematureEndToleranceMs = 1500;
        public const string TempFilePrefix = "assetstudio_audio_";
        private const int TempDeleteAttempts = 6;
        private const int TempDeleteRetryDelayMs = 40;

        private readonly object _gate = new();
        private readonly IReadOnlyList<IAudioPreviewBackendFactory> _factories;
        private readonly Action<Action> _dispatchToUi;
        private readonly Action<string>? _log;
        private readonly string _tempDirectory;

        private long _sessionId;
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _playCts;
        private IAudioPreviewBackend? _backend;
        private AudioPreviewMedia? _media;
        private string? _tempPath;
        private string? _assetId;
        private int _volume = 80;
        private bool _disposed;

        public AudioPlaybackService(
            IEnumerable<IAudioPreviewBackendFactory> factories,
            Action<Action>? dispatchToUi = null,
            string? tempDirectory = null,
            Action<string>? log = null)
        {
            _factories = (factories ?? throw new ArgumentNullException(nameof(factories))).ToArray();
            _dispatchToUi = dispatchToUi ?? (static action => action());
            _tempDirectory = string.IsNullOrEmpty(tempDirectory) ? Path.GetTempPath() : tempDirectory;
            _log = log;
        }

        /// <summary>
        /// Builds the platform-appropriate ordered backend strategy:
        /// Windows prefers WinMM PCM for WAV with FFmpeg/OpenAL fallback; Linux uses the bundled/system player;
        /// macOS and other OSes use FFmpeg with OpenTK/OpenAL audio output. Raw undecoded FSB5 is never passed to FFmpeg.
        /// </summary>
        public static IReadOnlyList<IAudioPreviewBackendFactory> CreateDefaultFactories(
            Func<WinMmAudioPlayer> getOrCreatePcmPlayer,
            SemaphoreSlim pcmLoadGate,
            Func<FFmpegMediaPlayer?> createFFmpegPlayer,
            Action<string>? log = null,
            bool? isWindowsOverride = null,
            bool? isLinuxOverride = null)
        {
            var isWindows = isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var isLinux = isLinuxOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            var factories = new List<IAudioPreviewBackendFactory>();
            if (isLinux)
            {
                factories.Add(new DelegateAudioPreviewBackendFactory(
                    "System audio player",
                    "Linux audio preview could not start a bundled or system audio player.",
                    static media => !media.IsRawFsb,
                    () => new ProcessAudioPreviewBackend(log)));
                return factories;
            }

            if (isWindows)
            {
                factories.Add(new DelegateAudioPreviewBackendFactory(
                    "WinMM PCM",
                    "Failed to initialize PCM wave audio preview player.",
                    static media => media.IsWave,
                    () => new PcmWaveAudioPreviewBackend(getOrCreatePcmPlayer(), pcmLoadGate)));
            }

            factories.Add(new DelegateAudioPreviewBackendFactory(
                "FFmpeg",
                "FFmpeg media player could not open the audio format.",
                static media => !media.IsRawFsb,
                () => new FFmpegAudioPreviewBackend(createFFmpegPlayer)));

            return factories;
        }

        /// <summary>Raised on the UI dispatcher when the active backend genuinely reached the end of the media.</summary>
        public event EventHandler? PlaybackEnded;

        public long SessionId => Interlocked.Read(ref _sessionId);

        public IAudioPreviewBackend? ActiveBackend
        {
            get { lock (_gate) { return _backend; } }
        }

        public bool HasActiveBackend => ActiveBackend != null;

        public string BackendName => ActiveBackend?.BackendName ?? string.Empty;

        public bool CanSeek => ActiveBackend?.CanSeek == true;

        public bool IsPlaying => ActiveBackend?.IsPlaying == true;

        public AudioPreviewMedia? CurrentMedia
        {
            get { lock (_gate) { return _media; } }
        }

        public string? CurrentAssetId
        {
            get { lock (_gate) { return _assetId; } }
        }

        public string? CurrentTempPath
        {
            get { lock (_gate) { return _tempPath; } }
        }

        public long DurationMs => CurrentMedia?.DurationMs ?? 0;

        public string SourceDescription => CurrentMedia?.SourceDescription ?? string.Empty;

        public int Volume => Volatile.Read(ref _volume);

        /// <summary>The asset is prepared, its temp file still exists and a backend has it open.</summary>
        public bool IsLoaded(string assetId)
        {
            lock (_gate)
            {
                return _backend != null
                    && string.Equals(_assetId, assetId, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(_tempPath)
                    && File.Exists(_tempPath);
            }
        }

        /// <summary>
        /// Cancels any in-flight load/play, disposes the active backend and deletes the temp file in the background.
        /// </summary>
        public void Reset()
        {
            CancellationTokenSource? loadCts;
            CancellationTokenSource? playCts;
            IAudioPreviewBackend? backend;
            string? tempPath;
            lock (_gate)
            {
                Interlocked.Increment(ref _sessionId);
                loadCts = _loadCts;
                playCts = _playCts;
                backend = _backend;
                tempPath = _tempPath;
                _loadCts = null;
                _playCts = null;
                _backend = null;
                _media = null;
                _tempPath = null;
                _assetId = null;
            }

            TryCancel(loadCts);
            TryCancel(playCts);
            DisposeBackend(backend);
            MediaTempFiles.DeleteInBackground(tempPath, TempDeleteAttempts, TempDeleteRetryDelayMs);
        }

        /// <summary>
        /// Prepares <paramref name="assetId"/> via <paramref name="prepareSource"/> on the thread pool, writes a unique temp
        /// file and opens it with the first backend that can handle it, falling back down the ordered factory list.
        /// A newer <see cref="LoadAsync"/> or <see cref="Reset"/> cancels this one; stale results are disposed and deleted.
        /// </summary>
        public async Task<AudioPreviewResult> LoadAsync(string assetId, Func<CancellationToken, AudioPreviewSource?> prepareSource)
        {
            ArgumentNullException.ThrowIfNull(prepareSource);
            ObjectDisposedException.ThrowIf(_disposed, this);

            Reset();

            var loadCts = new CancellationTokenSource();
            long session;
            lock (_gate)
            {
                session = Interlocked.Increment(ref _sessionId);
                _loadCts = loadCts;
                _assetId = assetId;
            }

            var token = loadCts.Token;
            var tempDirectory = _tempDirectory;
            string? orphanTempPath = null;
            IAudioPreviewBackend? candidate = null;
            try
            {
                var media = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var source = prepareSource(token);
                    token.ThrowIfCancellationRequested();
                    if (source == null || source.Data.Length == 0)
                    {
                        return null;
                    }

                    var path = MediaTempFiles.CreateUniquePath(tempDirectory, TempFilePrefix, source.Extension);
                    try
                    {
                        File.WriteAllBytes(path, source.Data);
                    }
                    catch
                    {
                        MediaTempFiles.TryDeleteNow(path);
                        throw;
                    }

                    if (token.IsCancellationRequested)
                    {
                        MediaTempFiles.TryDeleteNow(path);
                        token.ThrowIfCancellationRequested();
                    }

                    return new AudioPreviewMedia(path, source.Extension, source.DurationMs, source.SourceDescription);
                }, token);

                orphanTempPath = media?.FilePath;
                if (!IsCurrent(session, token))
                {
                    return AudioPreviewResult.Cancelled;
                }

                if (media == null)
                {
                    return new AudioPreviewResult(AudioPreviewResultStatus.Missing, "AudioClip has no binary data.");
                }

                lock (_gate)
                {
                    if (session != Interlocked.Read(ref _sessionId))
                    {
                        return AudioPreviewResult.Cancelled;
                    }

                    _media = media;
                    _tempPath = media.FilePath;
                }

                orphanTempPath = null; // ownership transferred to the session

                var failureMessage = media.IsRawFsb
                    ? "Raw FSB audio could not be decoded to PCM WAV for preview."
                    : "No audio preview backend is available for this format.";
                foreach (var factory in _factories)
                {
                    if (!factory.CanHandle(media))
                    {
                        continue;
                    }

                    failureMessage = factory.FailureMessage;
                    try
                    {
                        candidate = factory.Create();
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"Audio preview backend '{factory.Name}' could not be created: {ex.Message}");
                        continue;
                    }

                    bool loaded;
                    try
                    {
                        candidate.SetVolume(Volume);
                        loaded = await candidate.LoadAsync(media, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"Audio preview backend '{factory.Name}' failed to open media: {ex.Message}");
                        loaded = false;
                    }

                    if (!IsCurrent(session, token))
                    {
                        return AudioPreviewResult.Cancelled;
                    }

                    if (!loaded)
                    {
                        DisposeBackend(candidate);
                        candidate = null;
                        continue;
                    }

                    var adopted = candidate;
                    lock (_gate)
                    {
                        if (session != Interlocked.Read(ref _sessionId))
                        {
                            return AudioPreviewResult.Cancelled;
                        }

                        _backend = adopted;
                    }

                    candidate = null;
                    adopted.EndReached += OnBackendEndReached;
                    adopted.SetVolume(Volume);
                    return new AudioPreviewResult(AudioPreviewResultStatus.Ready, string.Empty);
                }

                // No backend could open the file; release the temp file immediately so nothing leaks.
                lock (_gate)
                {
                    if (session == Interlocked.Read(ref _sessionId))
                    {
                        orphanTempPath = _tempPath;
                        _tempPath = null;
                        _media = null;
                    }
                }

                return new AudioPreviewResult(AudioPreviewResultStatus.Unsupported, failureMessage);
            }
            catch (OperationCanceledException)
            {
                return AudioPreviewResult.Cancelled;
            }
            catch (Exception ex)
            {
                return IsCurrent(session, token)
                    ? new AudioPreviewResult(AudioPreviewResultStatus.Failed, $"Failed to prepare audio preview: {ex.Message}")
                    : AudioPreviewResult.Cancelled;
            }
            finally
            {
                DisposeBackend(candidate);
                _ = MediaTempFiles.DeleteInBackground(orphanTempPath, TempDeleteAttempts, TempDeleteRetryDelayMs);
                lock (_gate)
                {
                    if (ReferenceEquals(_loadCts, loadCts))
                    {
                        _loadCts = null;
                    }
                }

                loadCts.Dispose();
            }
        }

        /// <summary>Rewinds and starts the active backend. Superseded calls report <see cref="AudioPreviewResultStatus.Cancelled"/>.</summary>
        public async Task<AudioPreviewResult> PlayFromStartAsync()
        {
            IAudioPreviewBackend? backend;
            long session;
            var playCts = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (_gate)
            {
                backend = _backend;
                session = Interlocked.Read(ref _sessionId);
                previous = _playCts;
                _playCts = backend != null ? playCts : null;
            }

            TryCancel(previous);
            if (backend == null)
            {
                playCts.Dispose();
                return new AudioPreviewResult(AudioPreviewResultStatus.Unsupported, "Audio preview player is unavailable.");
            }

            var token = playCts.Token;
            try
            {
                backend.SetVolume(Volume);
                bool started;
                try
                {
                    started = await backend.PlayFromStartAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return AudioPreviewResult.Cancelled;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Audio preview backend '{backend.BackendName}' failed to start: {ex.Message}");
                    started = false;
                }

                if (token.IsCancellationRequested || !IsCurrentBackend(session, backend))
                {
                    return AudioPreviewResult.Cancelled;
                }

                return started
                    ? new AudioPreviewResult(AudioPreviewResultStatus.Started, string.Empty)
                    : new AudioPreviewResult(AudioPreviewResultStatus.Unsupported, backend.PlaybackFailureMessage);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_playCts, playCts))
                    {
                        _playCts = null;
                    }
                }

                playCts.Dispose();
            }
        }

        public void Pause() => ActiveBackend?.Pause();

        public void Resume()
        {
            var backend = ActiveBackend;
            if (backend == null)
            {
                return;
            }

            backend.SetVolume(Volume);
            backend.Play();
        }

        public void Stop() => ActiveBackend?.Stop();

        public void RestartForLoop()
        {
            var backend = ActiveBackend;
            if (backend == null)
            {
                return;
            }

            backend.SetVolume(Volume);
            backend.Restart();
        }

        /// <summary>Seeks the active backend (clamped to the media duration); ignored for non-seekable backends.</summary>
        public void Seek(long positionMs)
        {
            var backend = ActiveBackend;
            var durationMs = DurationMs;
            if (backend == null || !backend.CanSeek || durationMs <= 0)
            {
                return;
            }

            backend.Seek(Math.Clamp(positionMs, 0, durationMs) / 1000.0);
        }

        /// <summary>Stores the 0..100 volume (applied to future backends too) and forwards it to the active backend.</summary>
        public void SetVolume(int volume)
        {
            var clamped = Math.Clamp(volume, 0, 100);
            Volatile.Write(ref _volume, clamped);
            ActiveBackend?.SetVolume(clamped);
        }

        public AudioPlaybackSnapshot Poll()
        {
            var backend = ActiveBackend;
            if (backend == null)
            {
                return default;
            }

            return new AudioPlaybackSnapshot(
                true,
                (long)Math.Max(0, backend.PositionSeconds * 1000.0),
                DurationMs,
                backend.IsPlaying,
                backend.IsEnded);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Reset();
        }

        private void OnBackendEndReached(object? sender, EventArgs e)
        {
            _dispatchToUi(() =>
            {
                var backend = ActiveBackend;
                if (backend == null || !ReferenceEquals(sender, backend))
                {
                    return;
                }

                var durationMs = DurationMs;
                if (durationMs > 0)
                {
                    var currentMs = (long)Math.Max(0, backend.PositionSeconds * 1000.0);
                    if (currentMs + PrematureEndToleranceMs < durationMs)
                    {
                        _log?.Invoke($"Ignored premature audio end event at {currentMs} ms of {durationMs} ms.");
                        return;
                    }
                }

                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            });
        }

        private bool IsCurrent(long session, CancellationToken token)
        {
            return !token.IsCancellationRequested && session == Interlocked.Read(ref _sessionId);
        }

        private bool IsCurrentBackend(long session, IAudioPreviewBackend backend)
        {
            lock (_gate)
            {
                return session == Interlocked.Read(ref _sessionId) && ReferenceEquals(_backend, backend);
            }
        }

        private void DisposeBackend(IAudioPreviewBackend? backend)
        {
            if (backend == null)
            {
                return;
            }

            backend.EndReached -= OnBackendEndReached;
            try
            {
                backend.Dispose();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Audio preview backend '{backend.BackendName}' failed to dispose: {ex.Message}");
            }
        }

        private static void TryCancel(CancellationTokenSource? cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Unique temp-file naming and leak-free background deletion with retry (players may still hold handles).</summary>
    internal static class MediaTempFiles
    {
        public static string CreateUniquePath(string directory, string prefix, string extension)
        {
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"{prefix}{Guid.NewGuid():N}{extension}");
        }

        public static void TryDeleteNow(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        public static Task DeleteInBackground(string? path, int attempts, int retryDelayMs)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    try
                    {
                        if (!File.Exists(path))
                        {
                            return;
                        }

                        File.Delete(path);
                        return;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch
                    {
                        return;
                    }

                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                }
            });
        }
    }

    /// <summary>Stopwatch-based playback clock for backends whose players do not report a reliable position.</summary>
    internal sealed class AudioPreviewClock
    {
        private readonly Stopwatch _stopwatch = new();
        private long _baseMs;

        public bool IsRunning => _stopwatch.IsRunning;

        public long PositionMs => _stopwatch.IsRunning ? _baseMs + _stopwatch.ElapsedMilliseconds : _baseMs;

        public void Reset()
        {
            _stopwatch.Reset();
            _baseMs = 0;
        }

        public void Start(long baseMs = -1)
        {
            _baseMs = baseMs >= 0 ? baseMs : PositionMs;
            _stopwatch.Restart();
        }

        public void Pause()
        {
            _baseMs = PositionMs;
            _stopwatch.Reset();
        }

        public void SetPosition(long positionMs)
        {
            _baseMs = Math.Max(0, positionMs);
            if (_stopwatch.IsRunning)
            {
                _stopwatch.Restart();
            }
        }
    }

    /// <summary>
    /// Windows WinMM PCM WAV player (UnityAudioDecoder / transcoded WAV output). The underlying player is shared and
    /// reused across clips, so loads are serialised through <c>loadGate</c> to keep a stale load from clobbering a newer one.
    /// </summary>
    internal sealed class PcmWaveAudioPreviewBackend : IAudioPreviewBackend
    {
        private readonly WinMmAudioPlayer _player;
        private readonly SemaphoreSlim _loadGate;
        private long _durationMs;
        private volatile int _volume = 80;
        private bool _hasStarted;
        private bool _startPending;
        private bool _disposed;

        public PcmWaveAudioPreviewBackend(WinMmAudioPlayer player, SemaphoreSlim loadGate)
        {
            _player = player;
            _loadGate = loadGate;
        }

        public string BackendName => "WinMM PCM";
        public string PlaybackFailureMessage => "Failed to start PCM wave audio preview playback.";
        public bool CanSeek => true;

        public bool IsPlaying
        {
            get
            {
                if (_player.IsPlaying)
                {
                    _startPending = false;
                    return true;
                }

                return _startPending && !_player.IsPaused && !_player.IsCompleted;
            }
        }

        public bool IsEnded =>
            _hasStarted
            && !IsPlaying
            && (_player.IsCompleted
                || (_durationMs > 0 && _player.GetPositionMs() >= _durationMs - AudioPlaybackService.PrematureEndToleranceMs));

        public double PositionSeconds => _player.GetPositionMs() / 1000.0;

        public double DurationSeconds => _durationMs / 1000.0;

        public event EventHandler? EndReached
        {
            add { }
            remove { }
        }

        public async Task<bool> LoadAsync(AudioPreviewMedia media, CancellationToken cancellationToken)
        {
            var path = media.FilePath;
            var player = _player;
            var gate = _loadGate;
            var loaded = await Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return player.Load(path);
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken);

            if (!loaded)
            {
                return false;
            }

            _durationMs = media.DurationMs > 0 ? media.DurationMs : _player.DurationMs;
            _hasStarted = false;
            _startPending = false;
            _player.SetVolume(_volume);
            return true;
        }

        public Task<bool> PlayFromStartAsync(CancellationToken cancellationToken)
        {
            Restart();
            return Task.FromResult(true);
        }

        public void Play()
        {
            _hasStarted = true;
            _player.SetVolume(_volume);
            _player.ResumeWav();
        }

        public void Pause()
        {
            _startPending = false;
            _player.PauseWav();
        }

        public void Stop()
        {
            _hasStarted = false;
            _startPending = false;
            _player.StopWav();
        }

        public void Restart()
        {
            _hasStarted = true;
            _player.SetVolume(_volume);
            _player.Play(0);
            _startPending = true;
        }

        public void Seek(double seconds)
        {
            _player.Seek((long)(seconds * 1000.0));
        }

        public void SetVolume(int volume)
        {
            _volume = Math.Clamp(volume, 0, 100);
            _player.SetVolume(_volume);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hasStarted = false;
            _startPending = false;
            try
            {
                _player.StopWav();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// FFmpeg streaming player (WinMM / OpenAL audio sinks). Opens happen on the thread pool; players are torn down on
    /// the thread pool (and deferred until an in-flight <c>Open</c> finishes) so clip switching never blocks or races.
    /// </summary>
    internal sealed class FFmpegAudioPreviewBackend : IAudioPreviewBackend
    {
        private sealed class PlayerHandle
        {
            // 0 = opening, 1 = open, 2 = release requested / disposed
            public int State;
            public readonly FFmpegMediaPlayer Player;

            public PlayerHandle(FFmpegMediaPlayer player)
            {
                Player = player;
            }
        }

        private readonly Func<FFmpegMediaPlayer?> _createPlayer;
        private readonly AudioPreviewClock _clock = new();
        private volatile PlayerHandle? _handle;
        private string? _path;
        private long _durationMs;
        private volatile int _volume = 80;
        private bool _hasStarted;
        private bool _disposed;

        public FFmpegAudioPreviewBackend(Func<FFmpegMediaPlayer?> createPlayer)
        {
            _createPlayer = createPlayer;
        }

        public string BackendName => "FFmpeg";
        public string PlaybackFailureMessage => "Audio preview could not reopen this AudioClip.";
        public bool CanSeek => true;

        public bool IsPlaying
        {
            get
            {
                var player = _handle?.Player;
                return player != null && (_clock.IsRunning || player.IsPlaying);
            }
        }

        public bool IsEnded
        {
            get
            {
                var player = _handle?.Player;
                return _hasStarted
                    && player != null
                    && !player.IsPlaying
                    && _durationMs > 0
                    && _clock.PositionMs + AudioPlaybackService.PrematureEndToleranceMs >= _durationMs;
            }
        }

        public double PositionSeconds => _clock.PositionMs / 1000.0;

        public double DurationSeconds => _durationMs / 1000.0;

        public event EventHandler? EndReached;

        public Task<bool> LoadAsync(AudioPreviewMedia media, CancellationToken cancellationToken)
        {
            _path = media.FilePath;
            _durationMs = media.DurationMs;
            _hasStarted = false;
            return OpenNewPlayerAsync(cancellationToken);
        }

        public async Task<bool> PlayFromStartAsync(CancellationToken cancellationToken)
        {
            if (!await OpenNewPlayerAsync(cancellationToken))
            {
                return false;
            }

            var player = _handle?.Player;
            if (player == null || _disposed || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            _hasStarted = true;
            _clock.Start(0);
            player.Play();
            return true;
        }

        public void Play()
        {
            var player = _handle?.Player;
            if (player == null)
            {
                return;
            }

            _hasStarted = true;
            TrySetVolume(player, _volume);
            _clock.Start();
            player.Play();
        }

        public void Pause()
        {
            var player = _handle?.Player;
            if (player == null)
            {
                return;
            }

            player.Pause();
            _clock.Pause();
        }

        public void Stop()
        {
            _hasStarted = false;
            _handle?.Player.Stop();
            _clock.Reset();
        }

        public void Restart()
        {
            var player = _handle?.Player;
            if (player == null)
            {
                return;
            }

            player.Stop();
            try
            {
                player.Seek(0f);
            }
            catch
            {
            }

            TrySetVolume(player, _volume);
            _hasStarted = true;
            _clock.Start(0);
            player.Play();
        }

        public void Seek(double seconds)
        {
            var player = _handle?.Player;
            if (player == null || _durationMs <= 0)
            {
                return;
            }

            var positionMs = (long)(seconds * 1000.0);
            _clock.SetPosition(positionMs);
            player.Seek(positionMs / (float)_durationMs);
        }

        public void SetVolume(int volume)
        {
            _volume = Math.Clamp(volume, 0, 100);
            var player = _handle?.Player;
            if (player != null)
            {
                TrySetVolume(player, _volume);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hasStarted = false;
            _clock.Reset();
            var handle = Interlocked.Exchange(ref _handle, null);
            ReleaseHandle(handle);
        }

        private async Task<bool> OpenNewPlayerAsync(CancellationToken cancellationToken)
        {
            var path = _path;
            if (_disposed || string.IsNullOrEmpty(path))
            {
                return false;
            }

            var previous = Interlocked.Exchange(ref _handle, null);
            ReleaseHandle(previous);
            _clock.Reset();
            _hasStarted = false;

            var player = _createPlayer();
            if (player == null)
            {
                return false;
            }

            var handle = new PlayerHandle(player);
            player.EndReached += OnPlayerEndReached;
            _handle = handle;
            var volume = _volume;
            var opened = await Task.Run(() =>
            {
                var ok = false;
                try
                {
                    if (Volatile.Read(ref handle.State) == 0 && !cancellationToken.IsCancellationRequested)
                    {
                        TrySetVolume(player, volume);
                        ok = player.Open(path) && player.AudioPlayer != null;
                    }
                }
                catch
                {
                    ok = false;
                }

                // Transition 0 -> 1 if not cancelled/released; if ReleaseHandle already set State=2, dispose now safely.
                if (Interlocked.CompareExchange(ref handle.State, 1, 0) != 0)
                {
                    player.EndReached -= OnPlayerEndReached;
                    DisposePlayerQuietly(player);
                    return false;
                }

                return ok;
            }, CancellationToken.None);

            if (!opened || _disposed || cancellationToken.IsCancellationRequested || !ReferenceEquals(_handle, handle))
            {
                if (ReferenceEquals(_handle, handle))
                {
                    _handle = null;
                }

                ReleaseHandle(handle);
                return false;
            }

            return true;
        }

        private void OnPlayerEndReached(object? sender, EventArgs e)
        {
            if (!_disposed && ReferenceEquals(sender, _handle?.Player))
            {
                EndReached?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ReleaseHandle(PlayerHandle? handle)
        {
            if (handle == null)
            {
                return;
            }

            handle.Player.EndReached -= OnPlayerEndReached;
            var previousState = Interlocked.Exchange(ref handle.State, 2);
            if (previousState == 1)
            {
                ThreadPool.QueueUserWorkItem(static state => DisposePlayerQuietly((FFmpegMediaPlayer)state!), handle.Player);
            }
        }

        internal static void DisposePlayerInBackground(FFmpegMediaPlayer? player)
        {
            if (player == null)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(static state => DisposePlayerQuietly((FFmpegMediaPlayer)state!), player);
        }

        private static void DisposePlayerQuietly(FFmpegMediaPlayer player)
        {
            try { player.Stop(); } catch { }
            try { player.Close(); } catch { }
            try { player.Dispose(); } catch { }
        }

        private static void TrySetVolume(FFmpegMediaPlayer player, int volume)
        {
            try
            {
                player.Volume = volume;
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Linux system player process (bundled ffplay, pw-play, paplay or aplay). Pause/resume use SIGSTOP/SIGCONT;
    /// position comes from a playback clock; seeking is not supported.
    /// </summary>
    internal sealed class ProcessAudioPreviewBackend : IAudioPreviewBackend
    {
        private readonly AudioPreviewClock _clock = new();
        private readonly Action<string>? _log;
        private Process? _process;
        private bool _isPaused;
        private string? _path;
        private long _durationMs;
        private bool _disposed;

        public ProcessAudioPreviewBackend(Action<string>? log = null)
        {
            _log = log;
        }

        public string BackendName => "System audio player";
        public string PlaybackFailureMessage => "Linux audio preview could not start a bundled or system audio player.";
        public bool CanSeek => false;
        public bool IsPlaying => _clock.IsRunning && !_isPaused && _process != null && !HasExited(_process);
        public bool IsEnded => _process != null && HasExited(_process);
        public double PositionSeconds => _clock.PositionMs / 1000.0;
        public double DurationSeconds => _durationMs / 1000.0;

        public event EventHandler? EndReached
        {
            add { }
            remove { }
        }

        public Task<bool> LoadAsync(AudioPreviewMedia media, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _path = media.FilePath;
            _durationMs = media.DurationMs;
            return Task.FromResult(File.Exists(media.FilePath));
        }

        public Task<bool> PlayFromStartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StartProcess())
            {
                return Task.FromResult(false);
            }

            _clock.Start(0);
            return Task.FromResult(true);
        }

        public void Play()
        {
            if (_process == null || HasExited(_process))
            {
                return;
            }

            if (_isPaused && SendSignal(_process, "CONT"))
            {
                _isPaused = false;
            }

            _clock.Start();
        }

        public void Pause()
        {
            if (_process == null || HasExited(_process) || _isPaused)
            {
                return;
            }

            if (SendSignal(_process, "STOP"))
            {
                _isPaused = true;
            }

            _clock.Pause();
        }

        public void Stop()
        {
            KillProcess();
            _clock.Reset();
        }

        public void Restart()
        {
            if (StartProcess())
            {
                _clock.Start(0);
            }
            else
            {
                _clock.Reset();
            }
        }

        public void Seek(double seconds)
        {
        }

        public void SetVolume(int volume)
        {
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            KillProcess();
            _clock.Reset();
        }

        internal static string? FindPlayerCommand()
        {
            var baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "x64", "ffmpeg", "ffplay"),
                Path.Combine(baseDir, "ffmpeg", "ffplay"),
                Path.Combine(baseDir, "ffplay"),
                "/usr/bin/pw-play",
                "/usr/bin/paplay",
                "/usr/bin/aplay",
                "/bin/aplay"
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private bool StartProcess()
        {
            KillProcess();
            var path = _path;
            if (_disposed || string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                var playerCmd = FindPlayerCommand();
                if (playerCmd == null)
                {
                    Logger.Warning("No system audio player (pw-play, paplay, aplay) found on Linux.");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = playerCmd,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                var isFfplay = string.Equals(Path.GetFileName(playerCmd), "ffplay", StringComparison.OrdinalIgnoreCase);
                if (isFfplay)
                {
                    startInfo.ArgumentList.Add("-nodisp");
                    startInfo.ArgumentList.Add("-autoexit");
                    startInfo.ArgumentList.Add("-loglevel");
                    startInfo.ArgumentList.Add("error");
                }

                startInfo.ArgumentList.Add(path);

                if (isFfplay)
                {
                    var ffmpegDir = Path.GetDirectoryName(playerCmd);
                    if (!string.IsNullOrEmpty(ffmpegDir))
                    {
                        var existing = startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : null;
                        startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing)
                            ? ffmpegDir
                            : ffmpegDir + Path.PathSeparator + existing;
                    }
                }

                _process = Process.Start(startInfo);
                _isPaused = false;
                return _process != null;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Linux fallback audio play failed: {ex.Message}");
                Logger.Error($"Linux fallback audio play failed: {ex.Message}");
                return false;
            }
        }

        private void KillProcess()
        {
            var process = _process;
            _process = null;
            _isPaused = false;
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }

            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static bool SendSignal(Process process, string signal)
        {
            try
            {
                using var kill = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-{signal} {process.Id}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                kill?.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
