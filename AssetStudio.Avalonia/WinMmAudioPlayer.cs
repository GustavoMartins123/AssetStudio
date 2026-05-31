using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FFmpegVideoPlayer.Core;

namespace AssetStudio.Avalonia
{
    public class WinMmAudioPlayer : IAudioPlayer, IDisposable
    {
        private const int MMSYSERR_NOERROR = 0;
        private const int CALLBACK_NULL = 0;
        private const int WAVE_MAPPER = -1;
        private const int WHDR_DONE = 0x00000001;
        private const int TIME_BYTES = 0x0004;

        #region Structs & Enums
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MmTime
        {
            public uint wType;
            public uint cb;
            public uint reserved1;
            public uint reserved2;
            public uint reserved3;
            public uint reserved4;
        }

        private struct WaveInfo
        {
            public ushort AudioFormat;
            public short Channels;
            public int SampleRate;
            public int ByteRate;
            public short BlockAlign;
            public short BitsPerSample;
            public long DataOffset;
            public int DataSize;
        }

        private readonly record struct PlaybackState(
            int Serial,
            byte[] PcmData,
            int SampleRate,
            short Channels,
            short BitsPerSample,
            short BlockAlign,
            int ByteRate,
            long StartByte);
        #endregion

        #region P/Invokes
        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WaveFormatEx pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutPause(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutRestart(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);

        [DllImport("winmm.dll")]
        private static extern int waveOutGetPosition(IntPtr hwo, ref MmTime pmmt, int cbmmt);
        #endregion

        #region State Fields
        private readonly object _lock = new();
        private readonly object _waveOutLock = new();
        private int _activeBufferCount;
        private bool _isDisposed;

        // Streaming (FFmpeg) mode fields
        private IntPtr _hWaveOut;
        private int _sampleRate;
        private int _channels;
        private volatile float _volume = 1f;
        private double _playbackTimeBaseSec;
        private System.Diagnostics.Stopwatch _playbackClock = new();

        // Static WAV mode fields
        private byte[] _pcmData = Array.Empty<byte>();
        private int _wavSampleRate;
        private short _wavChannels;
        private short _bitsPerSample;
        private short _blockAlign;
        private int _byteRate;
        private long _startByte;
        private IntPtr _waveOut; // waveOut handle for static WAV playback
        private Thread? _playThread;
        private volatile bool _stopRequested;
        private volatile bool _isPlaying;
        private volatile bool _isPaused;
        private volatile bool _isCompleted;
        private int _playbackSerial;
        private int _wavVolume = 80;
        #endregion

        #region Properties for Static WAV Mode
        public long DurationMs => _byteRate > 0 ? (long)(_pcmData.Length * 1000.0 / _byteRate) : 0;
        public bool IsPlaying => _isPlaying && !_isPaused && !_isCompleted;
        public bool IsPaused => _isPaused;
        public bool IsCompleted => _isCompleted;
        #endregion

        #region Constructors
        // Constructor for Streaming (FFmpeg) Mode
        public WinMmAudioPlayer(int sampleRate, int channels)
        {
            _sampleRate = sampleRate;
            _channels = channels;

            WaveFormatEx waveFormat = new WaveFormatEx
            {
                wFormatTag = 1,
                nChannels = (short)channels,
                nSamplesPerSec = sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = (short)(channels * 2),
                nAvgBytesPerSec = sampleRate * channels * 2,
                cbSize = 0
            };

            lock (_waveOutLock)
            {
                var res = waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref waveFormat, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (res != MMSYSERR_NOERROR)
                {
                    _hWaveOut = IntPtr.Zero;
                }
                else
                {
                    SetVolumeInternal(_volume);
                    _playbackClock.Start();
                }
            }
        }

        // Constructor for Static WAV Mode
        public WinMmAudioPlayer()
        {
        }
        #endregion

        #region Streaming (FFmpeg) Mode Implementation
        public void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            SetVolumeInternal(_volume);
        }

        private void SetVolumeInternal(float volume)
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut == IntPtr.Zero || _isDisposed) return;
                ushort val = (ushort)(volume * 0xFFFF);
                uint combined = val | ((uint)val << 16);
                waveOutSetVolume(_hWaveOut, combined);
            }
        }

        public void Resume()
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut == IntPtr.Zero || _isDisposed) return;
                waveOutRestart(_hWaveOut);
            }
            _playbackClock.Start();
        }

        public void Pause()
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut == IntPtr.Zero || _isDisposed) return;
                waveOutPause(_hWaveOut);
            }
            _playbackClock.Stop();
        }

        public void Stop()
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut == IntPtr.Zero || _isDisposed) return;
                waveOutReset(_hWaveOut);
            }
            _playbackClock.Reset();
            _playbackTimeBaseSec = 0;
        }

        public double GetPlaybackTime()
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut != IntPtr.Zero && !_isDisposed)
                {
                    MmTime time = new MmTime { wType = TIME_BYTES };
                    if (waveOutGetPosition(_hWaveOut, ref time, Marshal.SizeOf<MmTime>()) == MMSYSERR_NOERROR)
                    {
                        var bytes = time.cb;
                        var bytesPerSec = _sampleRate * _channels * 2;
                        if (bytesPerSec > 0)
                        {
                            return (double)bytes / bytesPerSec;
                        }
                    }
                }
            }

            return _playbackTimeBaseSec + _playbackClock.Elapsed.TotalSeconds;
        }

        public unsafe void QueueSamplesS16(short* samples, int count)
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut == IntPtr.Zero || _isDisposed) return;
            }

            int byteCount = count * 2;
            byte[] managedBuffer = new byte[byteCount];
            Marshal.Copy((IntPtr)samples, managedBuffer, 0, byteCount);

            ThreadPool.QueueUserWorkItem(_ => PlayBuffer(managedBuffer));
        }

        public void QueueSamples(float[] samples)
        {
            lock (_waveOutLock)
            {
                if (_hWaveOut == IntPtr.Zero || _isDisposed) return;
            }

            short[] shortSamples = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                shortSamples[i] = (short)Math.Clamp(samples[i] * 32767f, -32768f, 32767f);
            }

            int byteCount = shortSamples.Length * 2;
            byte[] managedBuffer = new byte[byteCount];
            Buffer.BlockCopy(shortSamples, 0, managedBuffer, 0, byteCount);

            ThreadPool.QueueUserWorkItem(_ => PlayBuffer(managedBuffer));
        }

        private void PlayBuffer(byte[] buffer)
        {
            lock (_waveOutLock)
            {
                if (_isDisposed || _hWaveOut == IntPtr.Zero) return;
                _activeBufferCount++;
            }

            var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var headerPtr = IntPtr.Zero;
            var headerPrepared = false;
            try
            {
                var header = new WaveHeader
                {
                    lpData = bufferHandle.AddrOfPinnedObject(),
                    dwBufferLength = (uint)buffer.Length
                };
                var headerSize = Marshal.SizeOf<WaveHeader>();
                headerPtr = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(header, headerPtr, false);

                lock (_waveOutLock)
                {
                    if (_isDisposed || _hWaveOut == IntPtr.Zero) return;
                    if (waveOutPrepareHeader(_hWaveOut, headerPtr, headerSize) != MMSYSERR_NOERROR)
                    {
                        return;
                    }
                    headerPrepared = true;

                    if (waveOutWrite(_hWaveOut, headerPtr, headerSize) != MMSYSERR_NOERROR)
                    {
                        return;
                    }
                }

                while (!_isDisposed)
                {
                    var currentHeader = Marshal.PtrToStructure<WaveHeader>(headerPtr);
                    if ((currentHeader.dwFlags & WHDR_DONE) == WHDR_DONE)
                    {
                        break;
                    }
                    Thread.Sleep(5);
                }
            }
            catch {}
            finally
            {
                lock (_waveOutLock)
                {
                    if (headerPrepared && _hWaveOut != IntPtr.Zero)
                    {
                        try
                        {
                            waveOutUnprepareHeader(_hWaveOut, headerPtr, Marshal.SizeOf<WaveHeader>());
                        }
                        catch {}
                    }

                    if (headerPtr != IntPtr.Zero)
                    {
                        try
                        {
                            Marshal.FreeHGlobal(headerPtr);
                        }
                        catch {}
                    }

                    if (bufferHandle.IsAllocated)
                    {
                        try
                        {
                            bufferHandle.Free();
                        }
                        catch {}
                    }

                    _activeBufferCount--;
                    Monitor.PulseAll(_waveOutLock);
                }
            }
        }
        #endregion

        #region Static WAV Mode Implementation
        public static bool IsSupportedWave(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return TryReadWaveInfo(stream, out var info)
                    && info.AudioFormat == 1
                    && (info.BitsPerSample == 8 || info.BitsPerSample == 16)
                    && info.Channels >= 1
                    && info.Channels <= 2
                    && info.DataSize > 0;
            }
            catch
            {
                return false;
            }
        }

        public bool Load(string path)
        {
            StopWav();

            using var stream = File.OpenRead(path);
            if (!TryReadWaveInfo(stream, out var info)
                || info.AudioFormat != 1
                || (info.BitsPerSample != 8 && info.BitsPerSample != 16)
                || info.Channels < 1
                || info.Channels > 2
                || info.DataSize <= 0)
            {
                return false;
            }

            _wavSampleRate = info.SampleRate;
            _wavChannels = info.Channels;
            _bitsPerSample = info.BitsPerSample;
            _blockAlign = info.BlockAlign;
            _byteRate = info.ByteRate;
            _pcmData = new byte[info.DataSize];
            stream.Position = info.DataOffset;
            var read = stream.Read(_pcmData, 0, _pcmData.Length);
            if (read != _pcmData.Length)
            {
                Array.Resize(ref _pcmData, read);
            }

            _startByte = 0;
            _isCompleted = false;
            _isPaused = false;
            _isPlaying = false;
            return _pcmData.Length > 0;
        }

        public void Play(long positionMs = 0)
        {
            lock (_lock)
            {
                StopWavLocked();
                _startByte = AlignBytePosition((long)(positionMs * _byteRate / 1000.0));
                _stopRequested = false;
                _isCompleted = false;
                _isPaused = false;
                var state = new PlaybackState(
                    ++_playbackSerial,
                    _pcmData,
                    _wavSampleRate,
                    _wavChannels,
                    _bitsPerSample,
                    _blockAlign,
                    _byteRate,
                    _startByte);
                _playThread = new Thread(() => PlaybackLoop(state))
                {
                    IsBackground = true,
                    Name = "PCM WAV Preview"
                };
                _playThread.Start();
            }
        }

        public void PauseWav()
        {
            lock (_lock)
            {
                if (_waveOut == IntPtr.Zero)
                {
                    return;
                }

                waveOutPause(_waveOut);
                _isPaused = true;
            }
        }

        public void ResumeWav()
        {
            lock (_lock)
            {
                if (_waveOut == IntPtr.Zero)
                {
                    return;
                }

                waveOutRestart(_waveOut);
                _isPaused = false;
            }
        }

        public void StopWav()
        {
            lock (_lock)
            {
                StopWavLocked();
            }
        }

        public void Seek(long positionMs)
        {
            var shouldPlay = _isPlaying || _isPaused;
            if (shouldPlay)
            {
                Play(positionMs);
            }
            else
            {
                _startByte = AlignBytePosition((long)(positionMs * _byteRate / 1000.0));
                _isCompleted = false;
            }
        }

        public long GetPositionMs()
        {
            var bytes = _startByte;
            lock (_lock)
            {
                var handle = _waveOut;
                if (handle != IntPtr.Zero)
                {
                    var time = new MmTime { wType = TIME_BYTES };
                    if (waveOutGetPosition(handle, ref time, Marshal.SizeOf<MmTime>()) == MMSYSERR_NOERROR)
                    {
                        bytes = _startByte + time.cb;
                    }
                }
            }

            if (_byteRate <= 0)
            {
                return 0;
            }

            bytes = Math.Clamp(bytes, 0, _pcmData.Length);
            return (long)(bytes * 1000.0 / _byteRate);
        }

        public void SetVolume(int volume)
        {
            lock (_lock)
            {
                _wavVolume = Math.Clamp(volume, 0, 100);
                var handle = _waveOut;
                if (handle != IntPtr.Zero)
                {
                    waveOutSetVolume(handle, BuildWinMmVolume(_wavVolume));
                }
            }
        }

        private void StopWavLocked()
        {
            _stopRequested = true;
            _playbackSerial++;
            var handle = _waveOut;
            _waveOut = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                try
                {
                    waveOutReset(handle);
                }
                catch {}
            }

            _playThread = null;
            _isPlaying = false;
            _isPaused = false;
        }

        private void PlaybackLoop(PlaybackState state)
        {
            var waveFormat = new WaveFormatEx
            {
                wFormatTag = 1,
                nChannels = state.Channels,
                nSamplesPerSec = state.SampleRate,
                nAvgBytesPerSec = state.ByteRate,
                nBlockAlign = state.BlockAlign,
                wBitsPerSample = state.BitsPerSample,
                cbSize = 0
            };

            var result = waveOutOpen(out var handle, WAVE_MAPPER, ref waveFormat, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (result != MMSYSERR_NOERROR)
            {
                lock (_lock)
                {
                    if (state.Serial == _playbackSerial)
                    {
                        _isPlaying = false;
                        _isCompleted = true;
                    }
                }
                return;
            }

            lock (_lock)
            {
                if (state.Serial != _playbackSerial)
                {
                    waveOutClose(handle);
                    return;
                }

                _waveOut = handle;
                waveOutSetVolume(handle, BuildWinMmVolume(_wavVolume));
                _isPlaying = true;
            }

            try
            {
                var offset = (int)Math.Min(state.StartByte, state.PcmData.Length);
                var count = AlignBufferLength(state.PcmData.Length - offset, state.BlockAlign);
                if (count <= 0)
                {
                    offset = state.PcmData.Length;
                }
                else if (WriteBufferWav(handle, state.PcmData, offset, count))
                {
                    offset = state.PcmData.Length;
                }

                lock (_lock)
                {
                    if (state.Serial == _playbackSerial)
                    {
                        _isCompleted = !_stopRequested && offset >= state.PcmData.Length;
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    try
                    {
                        waveOutReset(handle);
                        waveOutClose(handle);
                    }
                    catch {}
                    if (state.Serial == _playbackSerial)
                    {
                        _waveOut = IntPtr.Zero;
                        _isPlaying = false;
                        _isPaused = false;
                    }
                }
            }
        }

        private bool WriteBufferWav(IntPtr handle, byte[] pcmData, int offset, int count)
        {
            var bufferHandle = GCHandle.Alloc(pcmData, GCHandleType.Pinned);
            var headerPtr = IntPtr.Zero;
            var headerPrepared = false;
            var headerQueued = false;
            try
            {
                var header = new WaveHeader
                {
                    lpData = IntPtr.Add(bufferHandle.AddrOfPinnedObject(), offset),
                    dwBufferLength = (uint)count
                };
                var headerSize = Marshal.SizeOf<WaveHeader>();
                headerPtr = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(header, headerPtr, false);

                if (waveOutPrepareHeader(handle, headerPtr, headerSize) != MMSYSERR_NOERROR)
                {
                    return false;
                }
                headerPrepared = true;

                if (waveOutWrite(handle, headerPtr, headerSize) != MMSYSERR_NOERROR)
                {
                    return false;
                }
                headerQueued = true;

                while (!_stopRequested)
                {
                    var currentHeader = Marshal.PtrToStructure<WaveHeader>(headerPtr);
                    if ((currentHeader.dwFlags & WHDR_DONE) == WHDR_DONE)
                    {
                        break;
                    }

                    Thread.Sleep(5);
                }

                return !_stopRequested;
            }
            finally
            {
                if (headerPrepared && headerPtr != IntPtr.Zero)
                {
                    if (headerQueued && _stopRequested)
                    {
                        waveOutReset(handle);
                    }

                    waveOutUnprepareHeader(handle, headerPtr, Marshal.SizeOf<WaveHeader>());
                }

                if (headerPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(headerPtr);
                }

                if (bufferHandle.IsAllocated)
                {
                    bufferHandle.Free();
                }
            }
        }

        private long AlignBytePosition(long bytePosition)
        {
            if (_blockAlign <= 0)
            {
                return Math.Max(0, bytePosition);
            }

            return Math.Clamp(bytePosition / _blockAlign * _blockAlign, 0, _pcmData.Length);
        }

        private static int AlignBufferLength(int size, short blockAlign)
        {
            if (blockAlign <= 0)
            {
                return size;
            }

            return size / blockAlign * blockAlign;
        }

        private static uint BuildWinMmVolume(int volume)
        {
            var value = (uint)Math.Clamp(volume * 0xFFFF / 100, 0, 0xFFFF);
            return value | (value << 16);
        }

        private static bool TryReadWaveInfo(Stream stream, out WaveInfo info)
        {
            info = default;
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (stream.Length < 44
                || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            {
                return false;
            }

            reader.ReadUInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            {
                return false;
            }

            var hasFormat = false;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var chunkSize = reader.ReadUInt32();
                var chunkStart = stream.Position;

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    info.AudioFormat = reader.ReadUInt16();
                    info.Channels = reader.ReadInt16();
                    info.SampleRate = reader.ReadInt32();
                    info.ByteRate = reader.ReadInt32();
                    info.BlockAlign = reader.ReadInt16();
                    info.BitsPerSample = reader.ReadInt16();
                    hasFormat = true;
                }
                else if (chunkId == "data" && hasFormat)
                {
                    info.DataOffset = stream.Position;
                    info.DataSize = (int)Math.Min(chunkSize, stream.Length - stream.Position);
                    return true;
                }

                stream.Position = chunkStart + chunkSize + ((chunkSize & 1) == 1 ? 1 : 0);
            }

            return false;
        }
        #endregion

        #region IDisposable Implementation
        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                StopWavLocked();
            }

            // Stop and clean up streaming mode resources under _waveOutLock
            lock (_waveOutLock)
            {
                if (_hWaveOut != IntPtr.Zero)
                {
                    try
                    {
                        waveOutReset(_hWaveOut);
                    }
                    catch {}

                    while (_activeBufferCount > 0)
                    {
                        Monitor.Wait(_waveOutLock, 100);
                    }

                    try
                    {
                        waveOutClose(_hWaveOut);
                    }
                    catch {}
                    _hWaveOut = IntPtr.Zero;
                }
            }
            _playbackClock.Stop();
        }
        #endregion
    }
}
