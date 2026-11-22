using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using FFmpegVideoPlayer.Core;
using Xunit;

namespace AssetStudio.Tests
{
    public class VideoPreviewLifecycleTests : IDisposable
    {
        private readonly string _tempDir;

        public VideoPreviewLifecycleTests()
        {
            _tempDir = Path.Combine(
                Path.GetTempPath(),
                "AssetStudio_VideoPreviewTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public async Task RapidSequentialVideoClipSwitch_CancelsPriorToken_And_IgnoresStaleCompletions()
        {
            using var coordinator = new VideoPreviewSessionCoordinator(_tempDir, maxCachedTempFiles: 2);

            var clip1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clip1CancelledObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clip2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clip2CancelledObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 1) Start session 1 (simulating a large 1080p 8142-frame WebM extraction)
            long session1 = coordinator.BeginSession("clip_1");
            var task1 = coordinator.PrepareOrReuseFileAsync("clip_1", session1, ct =>
            {
                clip1Started.TrySetResult();
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        Thread.Sleep(5);
                    }
                    ct.ThrowIfCancellationRequested();
                    return null;
                }
                catch (OperationCanceledException)
                {
                    clip1CancelledObserved.TrySetResult(true);
                    throw;
                }
            });

            await clip1Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // 2) Rapidly switch to session 2 before session 1 finishes
            long session2 = coordinator.BeginSession("clip_2");
            Assert.True(session2 > session1);
            Assert.False(coordinator.IsSessionCurrent(session1));
            Assert.True(coordinator.IsSessionCurrent(session2));

            var task2 = coordinator.PrepareOrReuseFileAsync("clip_2", session2, ct =>
            {
                clip2Started.TrySetResult();
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        Thread.Sleep(5);
                    }
                    ct.ThrowIfCancellationRequested();
                    return null;
                }
                catch (OperationCanceledException)
                {
                    clip2CancelledObserved.TrySetResult(true);
                    throw;
                }
            });

            await clip2Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(await clip1CancelledObserved.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(await task1.WaitAsync(TimeSpan.FromSeconds(3)));

            // 3) Rapidly switch to session 3 and allow session 3 to complete
            long session3 = coordinator.BeginSession("clip_3");
            Assert.True(await clip2CancelledObserved.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(await task2.WaitAsync(TimeSpan.FromSeconds(3)));

            var clip3Bytes = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02, 0x03, 0x04 };
            var result3 = await coordinator.PrepareOrReuseFileAsync("clip_3", session3, ct =>
                VideoPreviewService.PrepareVideoFile(
                    "clip_3",
                    "ClipThree",
                    300L,
                    ".webm",
                    videoClip: null,
                    rawVideoBytes: clip3Bytes,
                    tempDirectory: _tempDir,
                    cancellationToken: ct));

            Assert.NotNull(result3);
            Assert.Equal("clip_3", result3!.AssetIdKey);
            Assert.True(File.Exists(result3.FilePath));
            Assert.Equal(clip3Bytes.Length, result3.ByteSize);
            Assert.True(coordinator.CancelledSessionCount >= 2);

            // 4) Stale completion from session 1 or 2 is rejected even if invoked late
            var staleResult = await coordinator.PrepareOrReuseFileAsync("clip_1", session1, _ =>
                new VideoPreviewPreparedFile("clip_1", Path.Combine(_tempDir, "stale.webm"), 128, isOwnedTempFile: true));
            Assert.Null(staleResult);
        }

        [Fact]
        public async Task ConcurrentPrepareOrReuse_ForSameAssetAndSession_CoalescesOntoSingleExtraction()
        {
            using var coordinator = new VideoPreviewSessionCoordinator(_tempDir, maxCachedTempFiles: 2);
            long sessionId = coordinator.BeginSession("coalesce_clip");

            int workerInvocationCount = 0;
            var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var payload = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x99, 0x88, 0x77, 0x66 };

            Func<CancellationToken, VideoPreviewPreparedFile?> worker = ct =>
            {
                Interlocked.Increment(ref workerInvocationCount);
                workerEntered.TrySetResult();
                releaseWorker.Task.GetAwaiter().GetResult();
                return VideoPreviewService.PrepareVideoFile(
                    "coalesce_clip",
                    "CoalesceClip",
                    42L,
                    ".webm",
                    videoClip: null,
                    rawVideoBytes: payload,
                    tempDirectory: _tempDir,
                    cancellationToken: ct);
            };

            // Simulate thumbnail load and user Play click racing on the same (assetIdKey, sessionId)
            var thumbnailTask = coordinator.PrepareOrReuseFileAsync("coalesce_clip", sessionId, worker);
            await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var playClickTask = coordinator.PrepareOrReuseFileAsync("coalesce_clip", sessionId, worker);
            releaseWorker.TrySetResult();

            var res1 = await thumbnailTask.WaitAsync(TimeSpan.FromSeconds(3));
            var res2 = await playClickTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(1, Volatile.Read(ref workerInvocationCount));
            Assert.NotNull(res1);
            Assert.NotNull(res2);
            Assert.Equal(res1!.FilePath, res2!.FilePath);
            Assert.True(File.Exists(res1.FilePath));

            // Subsequent request in a new session for the same asset reuses the cached file with zero re-extraction
            long nextSession = coordinator.BeginSession("coalesce_clip");
            var cachedRes = await coordinator.PrepareOrReuseFileAsync("coalesce_clip", nextSession, worker);
            Assert.NotNull(cachedRes);
            Assert.True(cachedRes!.FromCache);
            Assert.Equal(1, Volatile.Read(ref workerInvocationCount));
        }

        [Fact]
        public void ExtractStreamWithCancellation_AbortsMidCopy_WhenCancelled()
        {
            const int chunkSize = 64 * 1024;
            const int totalChunks = 32; // 2 MiB total
            using var cts = new CancellationTokenSource();
            using var stream = new CancellingChunkStream(chunkSize * totalChunks, cancelAfterBytes: chunkSize * 3, cts);

            var destPath = Path.Combine(_tempDir, "aborted_extract.webm");
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                VideoPreviewService.ExtractStreamWithCancellation(
                    stream,
                    sourceOffset: 0,
                    byteCount: stream.Length,
                    destinationPath: destPath,
                    cancellationToken: cts.Token,
                    chunkBufferSize: chunkSize);
            });

            // Stream must stop reading immediately after the 3rd chunk rather than copying all 32 chunks
            Assert.True(stream.TotalBytesRead <= chunkSize * 4, $"Expected <= {chunkSize * 4} bytes read, actual {stream.TotalBytesRead}");
        }

        [Fact]
        public async Task VideoPlayerTeardownQueue_ExecutesOffCallingThread_And_SurvivesExceptionsAndTimeouts()
        {
            using var queue = new VideoPlayerTeardownQueue("TestVideoTeardownWorker");
            int callerThreadId = Environment.CurrentManagedThreadId;
            int teardownThreadId = -1;
            int callbackThreadId = -1;

            // 1) Normal teardown runs on background thread and invokes completion callback
            await queue.EnqueueTeardownAsync(
                teardownAction: () => teardownThreadId = Environment.CurrentManagedThreadId,
                onCompleted: () => callbackThreadId = Environment.CurrentManagedThreadId);

            Assert.NotEqual(callerThreadId, teardownThreadId);
            Assert.NotEqual(callerThreadId, callbackThreadId);

            // 2) Throwing teardown action does not fault the queue or skip onCompleted
            bool callbackAfterThrow = false;
            await queue.EnqueueTeardownAsync(
                teardownAction: () => throw new InvalidOperationException("Simulated native teardown error"),
                onCompleted: () => callbackAfterThrow = true);
            Assert.True(callbackAfterThrow);

            // 3) Hung teardown action times out cleanly without deadlocking subsequent items
            var sw = Stopwatch.StartNew();
            bool callbackAfterTimeout = false;
            await queue.EnqueueTeardownAsync(
                teardownAction: () => Thread.Sleep(2000),
                onCompleted: () => callbackAfterTimeout = true,
                timeoutMs: 120);
            sw.Stop();

            Assert.True(callbackAfterTimeout);
            Assert.True(sw.ElapsedMilliseconds < 1200, $"Expected timeout guard to release within 1200ms, actual {sw.ElapsedMilliseconds}ms");

            // 4) Queue remains healthy for subsequent teardowns
            bool subsequentCompleted = false;
            await queue.EnqueueTeardownAsync(
                teardownAction: () => { },
                onCompleted: () => subsequentCompleted = true);
            Assert.True(subsequentCompleted);
        }

        [Fact]
        public void DisablePlayerDiskLogger_And_SignalAndJoinPlaybackThread_PreventLoggerAllocationAndSelfDeadlock()
        {
            using var player = new FFmpegMediaPlayer();
            VideoPreviewService.DisablePlayerDiskLogger(player);

            var loggerField = typeof(FFmpegMediaPlayer).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(loggerField);
            var loggerObj = loggerField!.GetValue(player);
            Assert.NotNull(loggerObj);

            var disposedField = typeof(PlayerLogger).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
            var entriesField = typeof(PlayerLogger).GetField("_logEntries", BindingFlags.Instance | BindingFlags.NonPublic);
            var filePathField = typeof(PlayerLogger).GetField("_logFilePath", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.True((bool)(disposedField?.GetValue(loggerObj) ?? false));

            // Logging events after DisablePlayerDiskLogger must be a complete no-op
            var logEventMethod = typeof(PlayerLogger).GetMethod("LogEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var flushMethod = typeof(PlayerLogger).GetMethod("FlushToDisk", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            logEventMethod?.Invoke(loggerObj, new object?[] { "AudioPacket", "Ignored" });
            flushMethod?.Invoke(loggerObj, Array.Empty<object>());

            var entries = entriesField?.GetValue(loggerObj) as System.Collections.ICollection;
            Assert.NotNull(entries);
            Assert.Empty(entries!);

            var logPath = filePathField?.GetValue(loggerObj) as string;
            if (!string.IsNullOrEmpty(logPath))
            {
                Assert.False(File.Exists(logPath));
            }

            // Simulate an active _playbackThread waiting on _cancellationTokenSource + player._lock (the exact 1000ms self-deadlock pattern)
            var lockField = typeof(FFmpegMediaPlayer).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic);
            var ctsField = typeof(FFmpegMediaPlayer).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic);
            var threadField = typeof(FFmpegMediaPlayer).GetField("_playbackThread", BindingFlags.Instance | BindingFlags.NonPublic);

            var playerLock = lockField!.GetValue(player)!;
            using var simCts = new CancellationTokenSource();
            ctsField!.SetValue(player, simCts);

            var threadStarted = new ManualResetEventSlim(false);
            var simulatedPlaybackThread = new Thread(() =>
            {
                threadStarted.Set();
                while (!simCts.IsCancellationRequested)
                {
                    Thread.Sleep(2);
                }

                // Just like FFmpegMediaPlayer.PlaybackLoop's finally block, acquire player._lock before exiting
                lock (playerLock)
                {
                }
            })
            {
                IsBackground = true
            };
            threadField!.SetValue(player, simulatedPlaybackThread);
            simulatedPlaybackThread.Start();
            threadStarted.Wait(TimeSpan.FromSeconds(2));

            var sw = Stopwatch.StartNew();
            bool joined = VideoPreviewService.SignalAndJoinPlaybackThread(player, joinTimeoutMs: 500);
            sw.Stop();

            Assert.True(joined);
            Assert.False(simulatedPlaybackThread.IsAlive);
            Assert.True(sw.ElapsedMilliseconds < 250, $"Expected fast (<250ms) join outside lock, actual {sw.ElapsedMilliseconds}ms");

            threadField.SetValue(player, null);
            ctsField.SetValue(player, null);
        }

        [Fact]
        public void CodecAndProxyDecisionHelpers_DetectWebmMp4OggAndNeverRequireProxyTranscode()
        {
            // 1) WebM / Matroska EBML magic header (0x1A 0x45 0xDF 0xA3) with "webm" doctype + "V_VP8" codec ID
            var webmHeader = new byte[64];
            webmHeader[0] = 0x1A;
            webmHeader[1] = 0x45;
            webmHeader[2] = 0xDF;
            webmHeader[3] = 0xA3;
            var doctype = System.Text.Encoding.ASCII.GetBytes("webm");
            Array.Copy(doctype, 0, webmHeader, 8, doctype.Length);
            var vp8Tag = System.Text.Encoding.ASCII.GetBytes("V_VP8");
            Array.Copy(vp8Tag, 0, webmHeader, 20, vp8Tag.Length);

            var clip = (VideoClip)RuntimeHelpers.GetUninitializedObject(typeof(VideoClip));
            clip.m_Name = "SampleEndCreditsClip";
            clip.m_OriginalPath = "Assets/Movies/SampleEndCreditsClip.webm";
            SetPrivateProperty(clip, nameof(VideoClip.m_Width), 1920u);
            SetPrivateProperty(clip, nameof(VideoClip.m_Height), 1080u);
            SetPrivateProperty(clip, nameof(VideoClip.m_ProxyWidth), 1920u);
            SetPrivateProperty(clip, nameof(VideoClip.m_ProxyHeight), 1080u);
            SetPrivateProperty(clip, nameof(VideoClip.m_FrameRate), 29.970029830932617);
            SetPrivateProperty(clip, nameof(VideoClip.m_FrameCount), 8142UL);
            SetPrivateProperty(clip, nameof(VideoClip.m_Format), 4);
            SetPrivateProperty(clip, nameof(VideoClip.m_AudioChannelCount), new ushort[] { 2 });
            SetPrivateProperty(clip, nameof(VideoClip.m_AudioSampleRate), new uint[] { 48000 });

            var extRes = (StreamedResource)RuntimeHelpers.GetUninitializedObject(typeof(StreamedResource));
            extRes.m_Source = "archive:/CAB-sample/CAB-sample.resource";
            extRes.m_Offset = 0;
            extRes.m_Size = 552_115_881;
            clip.m_ExternalResources = extRes;

            var info = VideoPreviewService.InspectVideoClip(clip, webmHeader);
            Assert.Equal(".webm", info.RecommendedExtension);
            Assert.Contains("WebM", info.ContainerFormat, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("VP8", info.VideoCodec, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1920, info.Width);
            Assert.Equal(1080, info.Height);
            Assert.Equal(1920, info.ProxyWidth);
            Assert.Equal(1080, info.ProxyHeight);
            Assert.Equal(8142UL, info.FrameCount);
            Assert.InRange(info.EstimatedDurationSeconds, 271.6, 271.75);
            Assert.True(info.HasAudio);
            Assert.True(info.IsNativelySupportedByFFmpeg);
            Assert.False(info.RequiresProxyTranscode);
            Assert.False(VideoPreviewService.ShouldUseProxyTranscode(".webm", webmHeader));

            // 2) MP4 / ISO BMFF ftyp header
            var mp4Header = new byte[]
            {
                0x00, 0x00, 0x00, 0x18,
                (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                (byte)'i', (byte)'s', (byte)'o', (byte)'m',
                0x00, 0x00, 0x02, 0x00
            };
            var mp4Info = VideoPreviewService.InspectBytesAndExtension(mp4Header, ".mp4", 10_485_760);
            Assert.Equal(".mp4", mp4Info.RecommendedExtension);
            Assert.True(mp4Info.IsNativelySupportedByFFmpeg);
            Assert.False(mp4Info.RequiresProxyTranscode);
            Assert.False(VideoPreviewService.ShouldUseProxyTranscode(".mp4", mp4Header));

            // 3) Ogg Theora header
            var ogvInfo = VideoPreviewService.InspectBytesAndExtension(ReadOnlySpan<byte>.Empty, ".ogv", 1_048_576);
            Assert.Equal(".ogv", ogvInfo.RecommendedExtension);
            Assert.True(ogvInfo.IsNativelySupportedByFFmpeg);
            Assert.False(ogvInfo.RequiresProxyTranscode);
        }

        [Fact]
        public void VideoPreviewService_IsPureServiceWithZeroAvaloniaUiImports()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var servicePath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "VideoPreviewService.cs");
            Assert.True(File.Exists(servicePath), $"Expected service file at {servicePath}");

            var lines = File.ReadAllLines(servicePath);
            var avaloniaUsings = lines
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("using Avalonia", StringComparison.Ordinal))
                .ToList();

            Assert.Empty(avaloniaUsings);
        }

        private static void SetPrivateProperty<T>(object target, string propertyName, T value)
        {
            var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(prop);
            prop!.SetValue(target, value);
        }

        private sealed class CancellingChunkStream : Stream
        {
            private readonly long _length;
            private readonly long _cancelAfterBytes;
            private readonly CancellationTokenSource _cts;
            private long _position;

            public CancellingChunkStream(long length, long cancelAfterBytes, CancellationTokenSource cts)
            {
                _length = length;
                _cancelAfterBytes = cancelAfterBytes;
                _cts = cts;
            }

            public long TotalBytesRead { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set => _position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _length)
                {
                    return 0;
                }

                int toCopy = (int)Math.Min(count, _length - _position);
                buffer.AsSpan(offset, toCopy).Fill(0x42);
                _position += toCopy;
                TotalBytesRead += toCopy;

                if (TotalBytesRead >= _cancelAfterBytes && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }

                return toCopy;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => _position = offset;
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
