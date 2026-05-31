using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FFmpegVideoPlayer.Core;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private AudioClip? currentPreviewAudioClip;
    private System.Diagnostics.Process? _linuxAudioProcess;
    private bool _isLinuxAudioPaused;
    private System.Diagnostics.Stopwatch? _linuxAudioStopwatch;

    private FFmpegMediaPlayer? _audioMediaPlayer;
    private WinMmAudioPlayer? _pcmWavePreviewPlayer;
    private string? _currentTempAudioPath;
    private string? _currentTempAudioAssetId;
    private CancellationTokenSource? _audioPreviewLoadCts;
    private bool _usingPcmWavePreview;
    private long _audioLengthMs;
    private DispatcherTimer? _audioTimer;
    private const long PrematureAudioEndToleranceMs = 1500;
    private const long AudioLengthMismatchToleranceMs = 250;
    private readonly System.Diagnostics.Stopwatch _audioPlaybackClock = new();
    private long _audioPlaybackBaseMs;
    private volatile int _targetAudioVolume = 80;
    private bool _isUpdatingAudioProgress = false;
    private bool _isAudioDragging = false;
    private string _audioPreviewSourceDescription = string.Empty;

    private void InitializeAudio()
    {
        AudioProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragStartedEvent, AudioProgressBar_DragStarted);
        AudioProgressBar.AddHandler(global::Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, AudioProgressBar_DragCompleted);
        try
        {
            _audioMediaPlayer = CreateAudioMediaPlayer();
            _audioTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _audioTimer.Tick += AudioTimer_Tick;
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to initialize FFmpeg audio player: {ex.Message}");
        }
    }

    #region Audio Preview
    private FFmpegMediaPlayer CreateAudioMediaPlayer()
    {
        var mediaPlayer = new FFmpegMediaPlayer(action =>
        {
            Dispatcher.UIThread.Post(action);
        }, (sr, ch) =>
        {
            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    return new WinMmAudioPlayer(sr, ch);
                }
                return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
            }
            catch
            {
                try
                {
                    return FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(sr, ch);
                }
                catch
                {
                    return null;
                }
            }
        });

        mediaPlayer.EndReached += AudioMediaPlayer_EndReached;
        return mediaPlayer;
    }

    private void ResetAudioMediaPlayer(bool recreate)
    {
        var oldPlayer = _audioMediaPlayer;
        if (oldPlayer != null)
        {
            oldPlayer.EndReached -= AudioMediaPlayer_EndReached;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { oldPlayer.Stop(); } catch {}
                try { oldPlayer.Close(); } catch {}
                try { oldPlayer.Dispose(); } catch {}
            });
            _audioMediaPlayer = null;
        }

        if (!recreate)
        {
            return;
        }

        try
        {
            _audioMediaPlayer = CreateAudioMediaPlayer();
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to initialize FFmpeg audio player: {ex.Message}");
        }
    }

    private void AudioTimer_Tick(object? sender, EventArgs e)
    {
        if (_audioMediaPlayer == null && !_usingPcmWavePreview)
            return;

        long currentMs = 0;
        long totalMs = 0;

        if (useLinuxAudioFallback)
        {
            if (_linuxAudioStopwatch != null)
            {
                currentMs = GetAudioClockPositionMs();
                totalMs = _audioLengthMs;

                if (_linuxAudioProcess != null && _linuxAudioProcess.HasExited)
                {
                    _linuxAudioStopwatch.Stop();
                    if (AudioloopButton.IsChecked == true)
                    {
                        PlayLinuxAudioFallback(_currentTempAudioPath!);
                    }
                    else
                    {
                        AudioStop();
                    }
                }
            }
        }
        else if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
        {
            currentMs = _pcmWavePreviewPlayer.GetPositionMs();
            totalMs = _audioLengthMs;

            if (!_pcmWavePreviewPlayer.IsPlaying && _pcmWavePreviewPlayer.GetPositionMs() >= _audioLengthMs - PrematureAudioEndToleranceMs)
            {
                if (AudioloopButton.IsChecked == true)
                {
                    _pcmWavePreviewPlayer.Play(0);
                    ResetAudioClock();
                }
                else
                {
                    AudioStop();
                }
            }
        }
        else if (_audioMediaPlayer != null)
        {
            currentMs = GetAudioClockPositionMs();
            totalMs = _audioLengthMs;

            if (!_audioMediaPlayer.IsPlaying)
            {
                if (currentMs + PrematureAudioEndToleranceMs >= _audioLengthMs)
                {
                    if (AudioloopButton.IsChecked == true)
                    {
                        RestartAudioPreviewPlayback();
                    }
                    else
                    {
                        AudioStop();
                    }
                }
            }
        }

        if (AudioProgressBar != null && !_isAudioDragging && totalMs > 0)
        {
            _isUpdatingAudioProgress = true;
            AudioTimerLabel.Text = FormatMediaTime(currentMs, totalMs);
            try
            {
                AudioProgressBar.Value = currentMs * 1000.0 / totalMs;
            }
            finally
            {
                _isUpdatingAudioProgress = false;
            }
        }

        if (AudioStatusLabel != null)
        {
            if (useLinuxAudioFallback)
            {
                AudioStatusLabel.Text = (_linuxAudioStopwatch != null && _linuxAudioStopwatch.IsRunning) ? "Playing" : "Paused";
            }
            else if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                AudioStatusLabel.Text = _pcmWavePreviewPlayer.IsPlaying ? "Playing" : "Paused";
            }
            else if (_audioMediaPlayer != null)
            {
                AudioStatusLabel.Text = _audioPlaybackClock.IsRunning || _audioMediaPlayer.IsPlaying ? "Playing" : "Paused";
            }
        }
    }

    private long GetAudioClockPositionMs()
    {
        if (_audioPlaybackClock.IsRunning)
        {
            return _audioPlaybackBaseMs + _audioPlaybackClock.ElapsedMilliseconds;
        }
        return _audioPlaybackBaseMs;
    }

    private void ResetAudioClock()
    {
        _audioPlaybackClock.Reset();
        _audioPlaybackBaseMs = 0;
    }

    private void StartAudioClock(long baseMs = -1)
    {
        if (baseMs >= 0)
        {
            _audioPlaybackBaseMs = baseMs;
        }
        else
        {
            _audioPlaybackBaseMs = GetAudioClockPositionMs();
        }
        _audioPlaybackClock.Restart();
    }

    private void PauseAudioClock()
    {
        _audioPlaybackBaseMs = GetAudioClockPositionMs();
        _audioPlaybackClock.Reset();
    }

    private void SetAudioClockPosition(long positionMs)
    {
        _audioPlaybackBaseMs = Math.Max(0, positionMs);
        if (_audioPlaybackClock.IsRunning)
        {
            _audioPlaybackClock.Restart();
        }
    }

    private void AudioReset(bool recreateAudioPlayer = true)
    {
        try
        {
            _audioTimer?.Stop();
        }
        catch {}

        ResetAudioMediaPlayer(recreateAudioPlayer);
        try
        {
            _pcmWavePreviewPlayer?.StopWav();
        }
        catch {}

        if (useLinuxAudioFallback)
        {
            StopLinuxAudioFallback();
        }

        if (_audioPreviewLoadCts != null)
        {
            try
            {
                _audioPreviewLoadCts.Cancel();
                _audioPreviewLoadCts.Dispose();
            }
            catch {}
            _audioPreviewLoadCts = null;
        }

        if (!string.IsNullOrEmpty(_currentTempAudioPath) && File.Exists(_currentTempAudioPath))
        {
            try
            {
                File.Delete(_currentTempAudioPath);
            }
            catch {}
            _currentTempAudioPath = null;
        }

        _currentTempAudioAssetId = null;
        _usingPcmWavePreview = false;
        _audioLengthMs = 0;
        _audioPreviewSourceDescription = string.Empty;
        ResetAudioClock();
        
        if (AudioProgressBar != null) AudioProgressBar.Value = 0;
        if (AudioTimerLabel != null) AudioTimerLabel.Text = "0:00.0 / 0:00.0";
        if (AudioStatusLabel != null) AudioStatusLabel.Text = "Stopped";
        if (AudioInfoLabel != null) AudioInfoLabel.Text = "";
        if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
        if (AudioPlayButton != null) AudioPlayButton.IsEnabled = true;
        if (AudioPauseButton != null) AudioPauseButton.IsEnabled = true;
    }

    private async void AudioPlayButton_Click(object? sender, RoutedEventArgs e)
    {
        var audioClip = currentPreviewAudioClip;
        if (audioClip == null)
            return;

        try
        {
            if (!await EnsureAudioPreviewFileAsync(audioClip))
            {
                return;
            }

            if (!ReferenceEquals(currentPreviewAudioClip, audioClip))
            {
                return;
            }

            if (useLinuxAudioFallback)
            {
                _linuxAudioStopwatch ??= new System.Diagnostics.Stopwatch();
                _linuxAudioStopwatch.Restart();
                StartAudioClock(0);
                PlayLinuxAudioFallback(_currentTempAudioPath!);
            }
            else if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                _pcmWavePreviewPlayer.SetVolume(_targetAudioVolume);
                _pcmWavePreviewPlayer.Play(0);
                ResetAudioClock();
            }
            else if (_audioMediaPlayer != null)
            {
                var previewPath = _currentTempAudioPath;
                ResetAudioMediaPlayer(recreate: true);
                if (_audioMediaPlayer == null)
                {
                    AudioStatusLabel.Text = "Unsupported";
                    StatusStripUpdate("Audio preview player is unavailable.");
                    return;
                }

                try
                {
                    _audioMediaPlayer.Volume = _targetAudioVolume;
                }
                catch {}
                if (string.IsNullOrEmpty(previewPath)
                    || !_audioMediaPlayer.Open(previewPath)
                    || _audioMediaPlayer.AudioPlayer == null)
                {
                    AudioStatusLabel.Text = "Unsupported";
                    StatusStripUpdate("Audio preview could not reopen this AudioClip.");
                    return;
                }
                StartAudioClock(0);
                _audioMediaPlayer.Play();
            }

            AudioStatusLabel.Text = "Playing";
            AudioPauseButton.Content = "Pause";
            _audioTimer?.Start();
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to play audio: {ex.Message}");
        }
    }

    private bool useLinuxAudioFallback => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) && !isOpenAlSupportedCached;
    private static bool? isOpenAlSupportedCachedVal;
    private static bool isOpenAlSupportedCached
    {
        get
        {
            if (!isOpenAlSupportedCachedVal.HasValue)
            {
                isOpenAlSupportedCachedVal = IsOpenAlSupported();
            }
            return isOpenAlSupportedCachedVal.Value;
        }
    }

    private static bool IsOpenAlSupported()
    {
        try
        {
            using var test = FFmpegVideoPlayer.Audio.OpenTK.AudioPlayerFactory.Create(44100, 2);
            return test != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetLinuxAudioPlayerCommand()
    {
        string[] candidates = { "/usr/bin/pw-play", "/usr/bin/paplay", "/usr/bin/aplay", "/bin/aplay" };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private bool PlayLinuxAudioFallback(string path)
    {
        try
        {
            StopLinuxAudioFallback();

            var playerCmd = GetLinuxAudioPlayerCommand();
            if (playerCmd == null)
            {
                Logger.Warning("No system audio player (pw-play, paplay, aplay) found on Linux.");
                return false;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = playerCmd,
                Arguments = $"\"{path}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            _linuxAudioProcess = System.Diagnostics.Process.Start(startInfo);
            _isLinuxAudioPaused = false;
            return _linuxAudioProcess != null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Linux fallback audio play failed: {ex.Message}");
            return false;
        }
    }

    private void StopLinuxAudioFallback()
    {
        try
        {
            if (_linuxAudioProcess != null && !_linuxAudioProcess.HasExited)
            {
                _linuxAudioProcess.Kill();
            }
        }
        catch {}
        _linuxAudioProcess = null;
        _isLinuxAudioPaused = false;
    }

    private void PauseLinuxAudioFallback()
    {
        if (_linuxAudioProcess == null || _linuxAudioProcess.HasExited || _isLinuxAudioPaused)
        {
            return;
        }

        try
        {
            // Send SIGSTOP
            var killStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"-STOP {_linuxAudioProcess.Id}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            var p = System.Diagnostics.Process.Start(killStartInfo);
            p?.WaitForExit();
            _isLinuxAudioPaused = true;
        }
        catch {}
    }

    private void ResumeLinuxAudioFallback()
    {
        if (_linuxAudioProcess == null || _linuxAudioProcess.HasExited || !_isLinuxAudioPaused)
        {
            return;
        }

        try
        {
            // Send SIGCONT
            var killStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"-CONT {_linuxAudioProcess.Id}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            var p = System.Diagnostics.Process.Start(killStartInfo);
            p?.WaitForExit();
            _isLinuxAudioPaused = false;
        }
        catch {}
    }

    private void AudioProgressBar_DragStarted(object? sender, EventArgs e)
    {
        _isAudioDragging = true;
    }

    private void AudioProgressBar_DragCompleted(object? sender, EventArgs e)
    {
        _isAudioDragging = false;
        if (_audioLengthMs > 0 && AudioProgressBar != null)
        {
            var newMs = (long)(_audioLengthMs * (AudioProgressBar.Value / 1000.0));
            if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                try
                {
                    _pcmWavePreviewPlayer.Seek(newMs);
                }
                catch {}
            }
            else if (!useLinuxAudioFallback && _audioMediaPlayer != null)
            {
                try
                {
                    SetAudioClockPosition(newMs);
                    _audioMediaPlayer.Seek(newMs / (float)_audioLengthMs);
                }
                catch {}
            }
        }
    }

    private async Task<bool> EnsureAudioPreviewFileAsync(AudioClip audioClip)
    {
        var assetId = GetPreviewAssetId(audioClip);
        if (_currentTempAudioAssetId == assetId
            && !string.IsNullOrEmpty(_currentTempAudioPath)
            && File.Exists(_currentTempAudioPath))
        {
            return true;
        }

        AudioReset();
        currentPreviewAudioClip = audioClip;
        _currentTempAudioAssetId = assetId;

        if (AudioPlayButton != null) AudioPlayButton.IsEnabled = false;
        if (AudioPauseButton != null) AudioPauseButton.IsEnabled = false;
        if (AudioStatusLabel != null) AudioStatusLabel.Text = "Loading";

        _audioPreviewLoadCts = new CancellationTokenSource();
        var token = _audioPreviewLoadCts.Token;

        try
        {
            var audioData = audioClip.m_AudioData.GetData();
            if (audioData == null || audioData.Length == 0)
            {
                if (AudioStatusLabel != null) AudioStatusLabel.Text = "Missing";
                StatusStripUpdate("AudioClip has no binary data.");
                return false;
            }

            var converter = new AudioClipConverter(audioClip);
            var extension = NormalizeAudioExtension(converter.GetExtensionName());
            if (string.IsNullOrEmpty(extension))
            {
                extension = ".wav";
            }

            var analysis = await Task.Run(() => AnalyzeAudioPreviewData(audioClip, audioData, extension), token);
            if (token.IsCancellationRequested)
            {
                return false;
            }

            var sampleData = analysis.RebuiltData ?? audioData;
            var formatExtension = analysis.RebuiltExtension;

            _audioLengthMs = analysis.DurationMs > 0
                ? analysis.DurationMs
                : (long)(audioClip.m_Length * 1000.0);

            _usingPcmWavePreview = false;
            _audioPreviewSourceDescription = formatExtension.ToUpperInvariant().TrimStart('.');

            var preferTranscode = Environment.GetEnvironmentVariable("ASSETSTUDIO_PREFER_TRANSCODE_PREVIEW") == "1";
            var shouldTranscode = ShouldTranscodeAudioForPreview(formatExtension, preferTranscode);

            if (shouldTranscode)
            {
                if (token.IsCancellationRequested) return false;

                var tempInputFile = Path.GetTempFileName();
                var audioInputPath = Path.ChangeExtension(tempInputFile, formatExtension);
                if (File.Exists(tempInputFile))
                {
                    try { File.Delete(tempInputFile); } catch {}
                }
                await File.WriteAllBytesAsync(audioInputPath, sampleData, token);

                var tempOutputFile = Path.GetTempFileName();
                var audioOutputPath = Path.ChangeExtension(tempOutputFile, ".wav");
                if (File.Exists(tempOutputFile))
                {
                    try { File.Delete(tempOutputFile); } catch {}
                }

                try
                {
                    var transcodeResult = await Task.Run(() => FFmpegAudioTranscoder.TryTranscodeToPcmWav(audioInputPath, audioOutputPath, token), token);
                    if (transcodeResult.Success && File.Exists(audioOutputPath))
                    {
                        sampleData = await File.ReadAllBytesAsync(audioOutputPath, token);
                        formatExtension = ".wav";
                        _audioPreviewSourceDescription += " -> WAV (Transcoded)";
                    }
                }
                finally
                {
                    try { File.Delete(audioInputPath); } catch {}
                    try { File.Delete(audioOutputPath); } catch {}
                }
            }

            if (token.IsCancellationRequested) return false;

            if (formatExtension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && !useLinuxAudioFallback)
            {
                var waveDurationMs = TryGetWaveDurationMs(sampleData);
                if (waveDurationMs > 0)
                {
                    _audioLengthMs = waveDurationMs;
                }

                var tempFile = Path.GetTempFileName();
                var audioTempPath = Path.ChangeExtension(tempFile, ".wav");
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch {}
                }

                await File.WriteAllBytesAsync(audioTempPath, sampleData, token);
                _currentTempAudioPath = audioTempPath;

                _usingPcmWavePreview = true;
                _pcmWavePreviewPlayer ??= new WinMmAudioPlayer();
                if (!_pcmWavePreviewPlayer.Load(audioTempPath))
                {
                    _usingPcmWavePreview = false;
                    if (AudioStatusLabel != null) AudioStatusLabel.Text = "Unsupported";
                    StatusStripUpdate("Failed to initialize PCM wave audio preview player.");
                    return false;
                }
                _pcmWavePreviewPlayer.SetVolume(_targetAudioVolume);
            }
            else
            {
                var tempFile = Path.GetTempFileName();
                var audioTempPath = Path.ChangeExtension(tempFile, formatExtension);
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch {}
                }

                await File.WriteAllBytesAsync(audioTempPath, sampleData, token);
                _currentTempAudioPath = audioTempPath;

                if (!useLinuxAudioFallback)
                {
                    if (_audioMediaPlayer == null)
                    {
                        if (AudioStatusLabel != null) AudioStatusLabel.Text = "Unsupported";
                        StatusStripUpdate("FFmpeg audio media player was not initialized.");
                        return false;
                    }

                    if (!_audioMediaPlayer.Open(audioTempPath) || _audioMediaPlayer.AudioPlayer == null)
                    {
                        if (AudioStatusLabel != null) AudioStatusLabel.Text = "Unsupported";
                        StatusStripUpdate("FFmpeg media player could not open the audio format.");
                        return false;
                    }
                }
            }

            if (token.IsCancellationRequested) return false;

            UpdateAudioInfoText();

            if (AudioPlayButton != null) AudioPlayButton.IsEnabled = true;
            if (AudioPauseButton != null) AudioPauseButton.IsEnabled = true;
            if (AudioStatusLabel != null && AudioStatusLabel.Text == "Loading")
            {
                AudioStatusLabel.Text = "Ready";
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (AudioStatusLabel != null) AudioStatusLabel.Text = "Error";
            StatusStripUpdate($"Failed to prepare audio preview: {ex.Message}");
            return false;
        }
        finally
        {
            if (ReferenceEquals(currentPreviewAudioClip, audioClip))
            {
                if (AudioPlayButton != null) AudioPlayButton.IsEnabled = true;
                if (AudioPauseButton != null) AudioPauseButton.IsEnabled = true;
                if (AudioStatusLabel != null && AudioStatusLabel.Text == "Loading")
                {
                    AudioStatusLabel.Text = "Ready";
                }
            }
        }
    }

    private AudioPreviewAnalysis AnalyzeAudioPreviewData(AudioClip audioClip, byte[] audioData, string fallbackExtension)
    {
        byte[]? rebuiltData = null;
        var rebuiltExtension = fallbackExtension;
        long durationMs = audioClip.version[0] >= 5 && audioClip.m_Length > 0
            ? (long)(audioClip.m_Length * 1000.0f)
            : 0;

        try
        {
            var bank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(audioData);
            if (bank.Samples != null && bank.Samples.Count > 0)
            {
                var sample = bank.Samples[0];
                if (durationMs <= 0 && sample.Metadata != null && sample.Metadata.Frequency > 0)
                {
                    durationMs = (long)((double)sample.Metadata.SampleCount / sample.Metadata.Frequency * 1000.0);
                }

                if (bank.Header.AudioType == Fmod5Sharp.FmodTypes.FmodAudioType.MPEG)
                {
                    if (sample.SampleBytes != null && sample.SampleBytes.Length > 4)
                    {
                        var detected = DetectAudioExtension(sample.SampleBytes, ".mp3");
                        if (!detected.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                            && !detected.Equals(".fsb", StringComparison.OrdinalIgnoreCase))
                        {
                            rebuiltData = sample.SampleBytes;
                            rebuiltExtension = detected;
                        }
                    }
                }
                else if (sample.RebuildAsStandardFileFormat(out var dataBytes, out var fileExtension))
                {
                    rebuiltData = TrimWaveContainer(dataBytes);
                    rebuiltExtension = NormalizeAudioExtension(fileExtension ?? fallbackExtension);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Debug, $"Audio preview FSB analysis skipped for {audioClip.m_Name}: {ex.Message}");
        }

        if (rebuiltData != null && rebuiltData.Length > 4)
        {
            rebuiltExtension = DetectAudioExtension(rebuiltData, rebuiltExtension);
        }

        return new AudioPreviewAnalysis(rebuiltData, rebuiltExtension, durationMs);
    }

    private static bool ShouldTranscodeAudioForPreview(string extension, bool preferTranscode)
    {
        if (preferTranscode)
        {
            return true;
        }

        return NormalizeAudioExtension(extension).ToLowerInvariant() switch
        {
            ".wav" => false,
            ".aac" or ".aif" or ".aiff" or ".flac" or ".m4a" or ".mp3" or ".ogg"
                or ".it" or ".mod" or ".s3m" or ".xm" => true,
            _ => false
        };
    }

    private static byte[] TrimWaveContainer(byte[] data)
    {
        return data;
    }

    private static string NormalizeAudioExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return ".wav";
        var norm = ext.Trim().ToLowerInvariant();
        if (!norm.StartsWith(".")) norm = "." + norm;
        return norm switch
        {
            ".aiff" => ".aif",
            ".mpeg" => ".mp3",
            _ => norm
        };
    }

    private static string DetectAudioExtension(byte[] data, string fallback)
    {
        if (data.Length < 4) return fallback;

        // MP3 ID3 header
        if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
        {
            return ".mp3";
        }
        // MP3 frame sync (11 bits set: 0xFF and 0xE0 or 0xF0)
        if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
        {
            return ".mp3";
        }
        // OGG Vorbis
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

    private static long TryGetWaveDurationMs(byte[] data)
    {
        try
        {
            if (data.Length < 44
                || data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F'
                || data[8] != (byte)'W' || data[9] != (byte)'A' || data[10] != (byte)'V' || data[11] != (byte)'E')
            {
                return 0;
            }

            int chunkOffset = 12;
            int channels = 0;
            int sampleRate = 0;
            int byteRate = 0;
            int blockAlign = 0;
            int bitsPerSample = 0;
            int dataSize = 0;

            while (chunkOffset + 8 < data.Length)
            {
                var chunkId = System.Text.Encoding.ASCII.GetString(data, chunkOffset, 4);
                int chunkSize = BitConverter.ToInt32(data, chunkOffset + 4);

                if (chunkId.Equals("fmt ", StringComparison.OrdinalIgnoreCase))
                {
                    if (chunkSize >= 16 && chunkOffset + 8 + 16 <= data.Length)
                    {
                        channels = BitConverter.ToInt16(data, chunkOffset + 8 + 2);
                        sampleRate = BitConverter.ToInt32(data, chunkOffset + 8 + 4);
                        byteRate = BitConverter.ToInt32(data, chunkOffset + 8 + 8);
                        blockAlign = BitConverter.ToInt16(data, chunkOffset + 8 + 12);
                        bitsPerSample = BitConverter.ToInt16(data, chunkOffset + 8 + 14);
                    }
                }
                else if (chunkId.Equals("data", StringComparison.OrdinalIgnoreCase))
                {
                    dataSize = chunkSize;
                    break; // Found data, we can stop
                }

                chunkOffset += 8 + chunkSize;
            }

            if (byteRate > 0 && dataSize > 0)
            {
                return (long)((double)dataSize / byteRate * 1000.0);
            }
        }
        catch {}
        return 0;
    }

    private void UpdateAudioInfoText()
    {
        if (AudioInfoLabel == null || string.IsNullOrEmpty(_audioPreviewSourceDescription))
        {
            return;
        }

        var baseText = AudioInfoLabel.Text ?? string.Empty;
        if (baseText.Contains("| Preview:"))
        {
            var idx = baseText.IndexOf("| Preview:");
            baseText = baseText.Substring(0, idx).Trim();
        }

        AudioInfoLabel.Text = $"{baseText} | Preview: {_audioPreviewSourceDescription}";
    }

    private async void AudioPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        var audioClip = currentPreviewAudioClip;
        if (audioClip == null)
            return;

        try
        {
            if (useLinuxAudioFallback)
            {
                if (_linuxAudioStopwatch != null && _linuxAudioStopwatch.IsRunning)
                {
                    _linuxAudioStopwatch.Stop();
                    PauseAudioClock();
                    PauseLinuxAudioFallback();
                    AudioStatusLabel.Text = "Paused";
                    AudioPauseButton.Content = "Resume";
                }
                else
                {
                    _linuxAudioStopwatch ??= new System.Diagnostics.Stopwatch();
                    _linuxAudioStopwatch.Start();
                    StartAudioClock();
                    ResumeLinuxAudioFallback();
                    AudioStatusLabel.Text = "Playing";
                    AudioPauseButton.Content = "Pause";
                    _audioTimer?.Start();
                }
            }
            else if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                if (_pcmWavePreviewPlayer.IsPlaying)
                {
                    _pcmWavePreviewPlayer.PauseWav();
                    AudioStatusLabel.Text = "Paused";
                    AudioPauseButton.Content = "Resume";
                }
                else
                {
                    _pcmWavePreviewPlayer.SetVolume(_targetAudioVolume);
                    _pcmWavePreviewPlayer.ResumeWav();
                    AudioStatusLabel.Text = "Playing";
                    AudioPauseButton.Content = "Pause";
                    _audioTimer?.Start();
                }
            }
            else if (_audioMediaPlayer != null)
            {
                if (_audioMediaPlayer.IsPlaying)
                {
                    _audioMediaPlayer.Pause();
                    PauseAudioClock();
                    AudioStatusLabel.Text = "Paused";
                    AudioPauseButton.Content = "Resume";
                }
                else
                {
                    try
                    {
                        _audioMediaPlayer.Volume = _targetAudioVolume;
                    }
                    catch {}
                    StartAudioClock();
                    _audioMediaPlayer.Play();
                    AudioStatusLabel.Text = "Playing";
                    AudioPauseButton.Content = "Pause";
                    _audioTimer?.Start();
                }
            }
        }
        catch (Exception ex)
        {
            StatusStripUpdate($"Failed to pause audio: {ex.Message}");
        }
    }

    private void AudioStopButton_Click(object? sender, RoutedEventArgs e)
    {
        AudioStop();
    }

    private void RestartAudioPreviewPlayback()
    {
        if (useLinuxAudioFallback)
        {
            return;
        }

        try
        {
            if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                _pcmWavePreviewPlayer.SetVolume(_targetAudioVolume);
                _pcmWavePreviewPlayer.Play(0);
                ResetAudioClock();
                _audioTimer?.Start();
                if (AudioStatusLabel != null) AudioStatusLabel.Text = "Playing";
                if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
                return;
            }

            if (_audioMediaPlayer == null)
            {
                return;
            }

            _audioMediaPlayer.Stop();
            try
            {
                _audioMediaPlayer.Seek(0f);
            }
            catch {}
            try
            {
                _audioMediaPlayer.Volume = _targetAudioVolume;
            }
            catch {}

            StartAudioClock(0);
            _audioMediaPlayer.Play();
            _audioTimer?.Start();
            if (AudioStatusLabel != null) AudioStatusLabel.Text = "Playing";
            if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Warning, $"Failed to restart audio preview: {ex.Message}");
        }
    }

    private void AudioStop()
    {
        try
        {
            if (useLinuxAudioFallback)
            {
                _linuxAudioStopwatch?.Reset();
                ResetAudioClock();
                StopLinuxAudioFallback();
            }
            else if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                _pcmWavePreviewPlayer.StopWav();
                ResetAudioClock();
            }
            else if (_audioMediaPlayer != null)
            {
                _audioMediaPlayer.Stop();
                ResetAudioClock();
            }
            _audioTimer?.Stop();
            if (AudioProgressBar != null) AudioProgressBar.Value = 0;
            if (AudioTimerLabel != null) AudioTimerLabel.Text = "0:00.0 / 0:00.0";
            if (AudioStatusLabel != null) AudioStatusLabel.Text = "Stopped";
            if (AudioPauseButton != null) AudioPauseButton.Content = "Pause";
        }
        catch {}
    }

    private void AudioLoopButton_Click(object? sender, RoutedEventArgs e)
    {
    }

    private void AudioVolumeBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _targetAudioVolume = (int)(AudioVolumeBar.Value * 10);
        if (_usingPcmWavePreview && _pcmWavePreviewPlayer != null)
        {
            _pcmWavePreviewPlayer.SetVolume(_targetAudioVolume);
            return;
        }

        if (_audioMediaPlayer != null && !useLinuxAudioFallback)
        {
            try
            {
                _audioMediaPlayer.Volume = _targetAudioVolume;
            }
            catch {}
        }
    }

    private void AudioProgressBar_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingAudioProgress)
            return;

        if (_audioLengthMs > 0 && AudioTimerLabel != null)
        {
            var newMs = (long)(_audioLengthMs * (AudioProgressBar.Value / 1000.0));
            AudioTimerLabel.Text = FormatMediaTime(newMs, _audioLengthMs);

            if (!_isAudioDragging && _usingPcmWavePreview && _pcmWavePreviewPlayer != null)
            {
                try
                {
                    _pcmWavePreviewPlayer.Seek(newMs);
                }
                catch {}
            }
            else if (!_isAudioDragging && !useLinuxAudioFallback && _audioMediaPlayer != null)
            {
                try
                {
                    SetAudioClockPosition(newMs);
                    _audioMediaPlayer.Seek(newMs / (float)_audioLengthMs);
                }
                catch {}
            }
        }
    }

    private void AudioMediaPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _audioMediaPlayer)
                || currentPreviewAudioClip == null
                || string.IsNullOrEmpty(_currentTempAudioPath)
                || useLinuxAudioFallback
                || _usingPcmWavePreview
                || _audioMediaPlayer == null)
            {
                return;
            }

            if (_audioLengthMs > 0)
            {
                var currentMs = GetAudioClockPositionMs();
                if (currentMs + PrematureAudioEndToleranceMs < _audioLengthMs)
                {
                    logger.Log(LoggerEvent.Warning, $"Ignored premature audio end event at {currentMs} ms of {_audioLengthMs} ms.");
                    return;
                }
            }

            if (AudioloopButton.IsChecked == true)
            {
                RestartAudioPreviewPlayback();
            }
            else
            {
                AudioStop();
            }
        });
    }

    private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
    {
        AudioReset();
        currentPreviewAudioClip = m_AudioClip;
        
        var infoText = "Compression format: ";
        if (m_AudioClip.version[0] < 5)
        {
            switch (m_AudioClip.m_Type)
            {
                case FMODSoundType.ACC:
                    infoText += "Acc";
                    break;
                case FMODSoundType.AIFF:
                    infoText += "AIFF";
                    break;
                case FMODSoundType.IT:
                    infoText += "Impulse tracker";
                    break;
                case FMODSoundType.MOD:
                    infoText += "Protracker / Fasttracker MOD";
                    break;
                case FMODSoundType.MPEG:
                    infoText += "MP2/MP3 MPEG";
                    break;
                case FMODSoundType.OGGVORBIS:
                    infoText += "Ogg vorbis";
                    break;
                case FMODSoundType.S3M:
                    infoText += "ScreamTracker 3";
                    break;
                case FMODSoundType.WAV:
                    infoText += "Microsoft WAV";
                    break;
                case FMODSoundType.XM:
                    infoText += "FastTracker 2 XM";
                    break;
                case FMODSoundType.XMA:
                    infoText += "Xbox360 XMA";
                    break;
                case FMODSoundType.VAG:
                    infoText += "PlayStation Portable ADPCM";
                    break;
                case FMODSoundType.AUDIOQUEUE:
                    infoText += "iPhone";
                    break;
                default:
                    infoText += "Unknown";
                    break;
            }
        }
        else
        {
            switch (m_AudioClip.m_CompressionFormat)
            {
                case AudioCompressionFormat.PCM:
                    infoText += "PCM";
                    break;
                case AudioCompressionFormat.Vorbis:
                    infoText += "Vorbis";
                    break;
                case AudioCompressionFormat.ADPCM:
                    infoText += "ADPCM";
                    break;
                case AudioCompressionFormat.MP3:
                    infoText += "MP3";
                    break;
                case AudioCompressionFormat.PSMVAG:
                    infoText += "PlayStation Portable ADPCM";
                    break;
                case AudioCompressionFormat.HEVAG:
                    infoText += "PSVita ADPCM";
                    break;
                case AudioCompressionFormat.XMA:
                    infoText += "Xbox360 XMA";
                    break;
                case AudioCompressionFormat.AAC:
                    infoText += "AAC";
                    break;
                case AudioCompressionFormat.GCADPCM:
                    infoText += "Nintendo 3DS/Wii DSP";
                    break;
                case AudioCompressionFormat.ATRAC9:
                    infoText += "PSVita ATRAC9";
                    break;
                default:
                    infoText += "Unknown";
                    break;
            }
        }

        AudioPanel.IsVisible = true;
        AudioInfoLabel.Text = infoText;
        AudioTimerLabel.Text = "0:00.0 / 0:00.0";
        AudioTitleLabel.Text = m_AudioClip.m_Name;
        AudioStatusLabel.Text = "Ready";
        AudioPauseButton.Content = "Pause";
        StatusStripUpdate($"Loaded audio metadata: {m_AudioClip.m_Name}");
    }
    #endregion
}

internal sealed class PreparedAudioPreview
{
    public PreparedAudioPreview(List<PreparedAudioPreviewCandidate> candidates, long lengthMs)
    {
        Candidates = candidates;
        LengthMs = lengthMs;
    }

    public List<PreparedAudioPreviewCandidate> Candidates { get; }
    public long LengthMs { get; }
}

internal sealed class PreparedAudioPreviewCandidate
{
    public PreparedAudioPreviewCandidate(string path, string description, bool originalData, long durationMs)
    {
        Path = path;
        Description = description;
        OriginalData = originalData;
        DurationMs = durationMs;
    }

    public string Path { get; }
    public string Description { get; }
    public bool OriginalData { get; }
    public long DurationMs { get; }
}

internal sealed class AudioPreviewAnalysis
{
    public AudioPreviewAnalysis(byte[]? rebuiltData, string rebuiltExtension, long durationMs)
    {
        RebuiltData = rebuiltData;
        RebuiltExtension = rebuiltExtension;
        DurationMs = durationMs;
    }

    public byte[]? RebuiltData { get; }
    public string RebuiltExtension { get; }
    public long DurationMs { get; }
}
