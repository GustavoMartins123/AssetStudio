using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia;

internal sealed class PcmWavePreviewPlayer : IDisposable
{
    private const int MMSYSERR_NOERROR = 0;
    private const int CALLBACK_NULL = 0;
    private const int WAVE_MAPPER = -1;
    private const int WHDR_DONE = 0x00000001;
    private const int TIME_BYTES = 0x0004;

    private readonly object _lock = new();
    private byte[] _pcmData = Array.Empty<byte>();
    private int _sampleRate;
    private short _channels;
    private short _bitsPerSample;
    private short _blockAlign;
    private int _byteRate;
    private long _startByte;
    private IntPtr _waveOut;
    private Thread? _playThread;
    private volatile bool _stopRequested;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;
    private volatile bool _isCompleted;
    private int _playbackSerial;
    private int _volume = 80;

    public long DurationMs => _byteRate > 0 ? (long)(_pcmData.Length * 1000.0 / _byteRate) : 0;
    public bool IsPlaying => _isPlaying && !_isPaused && !_isCompleted;
    public bool IsPaused => _isPaused;
    public bool IsCompleted => _isCompleted;

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
        Stop();

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

        _sampleRate = info.SampleRate;
        _channels = info.Channels;
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
            StopLocked();
            _startByte = AlignBytePosition((long)(positionMs * _byteRate / 1000.0));
            _stopRequested = false;
            _isCompleted = false;
            _isPaused = false;
            var state = new PlaybackState(
                ++_playbackSerial,
                _pcmData,
                _sampleRate,
                _channels,
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

    public void Pause()
    {
        if (_waveOut == IntPtr.Zero)
        {
            return;
        }

        waveOutPause(_waveOut);
        _isPaused = true;
    }

    public void Resume()
    {
        if (_waveOut == IntPtr.Zero)
        {
            return;
        }

        waveOutRestart(_waveOut);
        _isPaused = false;
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
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
        var handle = _waveOut;
        if (handle != IntPtr.Zero)
        {
            var time = new MmTime { wType = TIME_BYTES };
            if (waveOutGetPosition(handle, ref time, Marshal.SizeOf<MmTime>()) == MMSYSERR_NOERROR)
            {
                bytes = _startByte + time.cb;
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
        _volume = Math.Clamp(volume, 0, 100);
        var handle = _waveOut;
        if (handle != IntPtr.Zero)
        {
            waveOutSetVolume(handle, BuildWinMmVolume(_volume));
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void StopLocked()
    {
        _stopRequested = true;
        _playbackSerial++;
        var handle = _waveOut;
        if (handle != IntPtr.Zero)
        {
            waveOutReset(handle);
        }

        if (_playThread != null && _playThread.IsAlive && _playThread != Thread.CurrentThread)
        {
            _playThread.Join(1000);
        }

        _playThread = null;
        _waveOut = IntPtr.Zero;
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
            if (state.Serial == _playbackSerial)
            {
                _isPlaying = false;
                _isCompleted = true;
            }
            return;
        }

        if (state.Serial != _playbackSerial)
        {
            waveOutClose(handle);
            return;
        }

        _waveOut = handle;
        waveOutSetVolume(handle, BuildWinMmVolume(_volume));
        _isPlaying = true;

        try
        {
            var offset = (int)Math.Min(state.StartByte, state.PcmData.Length);
            var count = AlignBufferLength(state.PcmData.Length - offset, state.BlockAlign);
            if (count <= 0)
            {
                offset = state.PcmData.Length;
            }
            else if (WriteBuffer(handle, state.PcmData, offset, count))
            {
                offset = state.PcmData.Length;
            }

            if (state.Serial == _playbackSerial)
            {
                _isCompleted = !_stopRequested && offset >= state.PcmData.Length;
            }
        }
        finally
        {
            waveOutReset(handle);
            waveOutClose(handle);
            if (state.Serial == _playbackSerial)
            {
                _waveOut = IntPtr.Zero;
                _isPlaying = false;
                _isPaused = false;
            }
        }
    }

    private bool WriteBuffer(IntPtr handle, byte[] pcmData, int offset, int count)
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

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WaveFormatEx lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutPause(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutRestart(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(IntPtr hWaveOut, ref MmTime pmmt, int cbmmt);

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
}
