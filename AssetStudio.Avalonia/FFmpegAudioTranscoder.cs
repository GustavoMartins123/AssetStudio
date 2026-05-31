using FFmpeg.AutoGen;
using FFmpegVideoPlayer.Core;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia;

internal sealed class AudioTranscodeResult
{
    public AudioTranscodeResult(bool success, long durationMs, string? error)
    {
        Success = success;
        DurationMs = durationMs;
        Error = error;
    }

    public bool Success { get; }
    public long DurationMs { get; }
    public string? Error { get; }
}

internal static unsafe class FFmpegAudioTranscoder
{
    public static AudioTranscodeResult TryTranscodeToPcmWav(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        SwrContext* swrContext = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        FileStream? outputStream = null;

        try
        {
            FFmpegInitializer.Initialize();

            var openInputResult = ffmpeg.avformat_open_input(&formatContext, inputPath, null, null);
            if (openInputResult < 0)
            {
                return Fail($"avformat_open_input failed: {GetErrorMessage(openInputResult)}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var streamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (streamInfoResult < 0)
            {
                return Fail($"avformat_find_stream_info failed: {GetErrorMessage(streamInfoResult)}");
            }

            var audioStreamIndex = FindAudioStream(formatContext);
            if (audioStreamIndex < 0)
            {
                return Fail("No audio stream found.");
            }

            var audioStream = formatContext->streams[audioStreamIndex];
            var codecParameters = audioStream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
            {
                return Fail($"No FFmpeg decoder for codec {codecParameters->codec_id}.");
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                return Fail("Could not allocate audio decoder context.");
            }

            var parametersResult = ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters);
            if (parametersResult < 0)
            {
                return Fail($"avcodec_parameters_to_context failed: {GetErrorMessage(parametersResult)}");
            }

            var openResult = ffmpeg.avcodec_open2(codecContext, codec, null);
            if (openResult < 0)
            {
                return Fail($"avcodec_open2 failed: {GetErrorMessage(openResult)}");
            }

            var inputSampleRate = codecContext->sample_rate > 0 ? codecContext->sample_rate : 48000;
            var outputSampleRate = inputSampleRate;
            var inputChannels = codecContext->ch_layout.nb_channels > 0
                ? codecContext->ch_layout.nb_channels
                : Math.Max(1, codecParameters->ch_layout.nb_channels);
            var outputChannels = Math.Clamp(inputChannels, 1, 2);

            swrContext = CreateResampler(codecContext, inputSampleRate, outputSampleRate, outputChannels);
            if (swrContext == null)
            {
                return Fail("Could not initialize FFmpeg audio resampler.");
            }

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (packet == null || frame == null)
            {
                return Fail("Could not allocate FFmpeg audio packet/frame.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppDomain.CurrentDomain.BaseDirectory);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            outputStream = File.Create(outputPath);
            WriteWavHeader(outputStream, outputSampleRate, outputChannels, 0);

            long totalOutputSamples = 0;
            long totalDataBytes = 0;

            while (ffmpeg.av_read_frame(formatContext, packet) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (packet->stream_index != audioStreamIndex)
                    {
                        continue;
                    }

                    var decodeResult = DecodePacket(
                        codecContext,
                        swrContext,
                        packet,
                        frame,
                        outputStream,
                        inputSampleRate,
                        outputSampleRate,
                        outputChannels,
                        ref totalOutputSamples,
                        ref totalDataBytes,
                        cancellationToken);

                    if (decodeResult < 0)
                    {
                        return Fail($"Audio decode failed: {GetErrorMessage(decodeResult)}");
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            var flushResult = DecodePacket(
                codecContext,
                swrContext,
                null,
                frame,
                outputStream,
                inputSampleRate,
                outputSampleRate,
                outputChannels,
                ref totalOutputSamples,
                ref totalDataBytes,
                cancellationToken);

            if (flushResult < 0)
            {
                return Fail($"Audio flush failed: {GetErrorMessage(flushResult)}");
            }

            var resamplerFlushResult = FlushResampler(
                swrContext,
                outputStream,
                outputSampleRate,
                outputChannels,
                ref totalOutputSamples,
                ref totalDataBytes,
                cancellationToken);

            if (resamplerFlushResult < 0)
            {
                return Fail($"Audio resampler flush failed: {GetErrorMessage(resamplerFlushResult)}");
            }

            if (totalDataBytes <= 0)
            {
                return Fail("Decoder produced no PCM samples.");
            }

            outputStream.Position = 0;
            WriteWavHeader(outputStream, outputSampleRate, outputChannels, totalDataBytes);
            outputStream.Flush();

            var durationMs = outputSampleRate > 0
                ? (long)(totalOutputSamples * 1000.0 / outputSampleRate)
                : 0;
            return new AudioTranscodeResult(true, durationMs, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            outputStream?.Dispose();
            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }
            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }
            if (swrContext != null)
            {
                ffmpeg.swr_free(&swrContext);
            }
            if (codecContext != null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }
            if (formatContext != null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    private static int FindAudioStream(AVFormatContext* formatContext)
    {
        for (var i = 0; i < formatContext->nb_streams; i++)
        {
            if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                return i;
            }
        }

        return -1;
    }

    private static SwrContext* CreateResampler(
        AVCodecContext* codecContext,
        int inputSampleRate,
        int outputSampleRate,
        int outputChannels)
    {
        var swrContext = ffmpeg.swr_alloc();
        if (swrContext == null)
        {
            return null;
        }

        var inputLayout = codecContext->ch_layout;
        if (inputLayout.nb_channels <= 0)
        {
            ffmpeg.av_channel_layout_default(&inputLayout, Math.Max(1, outputChannels));
        }

        AVChannelLayout outputLayout = default;
        ffmpeg.av_channel_layout_default(&outputLayout, outputChannels);

        ffmpeg.av_opt_set_chlayout(swrContext, "in_chlayout", &inputLayout, 0);
        ffmpeg.av_opt_set_int(swrContext, "in_sample_rate", inputSampleRate, 0);
        ffmpeg.av_opt_set_sample_fmt(swrContext, "in_sample_fmt", codecContext->sample_fmt, 0);
        ffmpeg.av_opt_set_chlayout(swrContext, "out_chlayout", &outputLayout, 0);
        ffmpeg.av_opt_set_int(swrContext, "out_sample_rate", outputSampleRate, 0);
        ffmpeg.av_opt_set_sample_fmt(swrContext, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);

        if (ffmpeg.swr_init(swrContext) < 0)
        {
            ffmpeg.swr_free(&swrContext);
            return null;
        }

        return swrContext;
    }

    private static int DecodePacket(
        AVCodecContext* codecContext,
        SwrContext* swrContext,
        AVPacket* packet,
        AVFrame* frame,
        Stream outputStream,
        int inputSampleRate,
        int outputSampleRate,
        int outputChannels,
        ref long totalOutputSamples,
        ref long totalDataBytes,
        CancellationToken cancellationToken)
    {
        var sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
        if (sendResult < 0)
        {
            return sendResult;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                return 0;
            }
            if (receiveResult < 0)
            {
                return receiveResult;
            }

            var convertResult = ConvertFrame(
                swrContext,
                frame,
                outputStream,
                inputSampleRate,
                outputSampleRate,
                outputChannels,
                ref totalOutputSamples,
                ref totalDataBytes);

            ffmpeg.av_frame_unref(frame);
            if (convertResult < 0)
            {
                return convertResult;
            }
        }
    }

    private static int ConvertFrame(
        SwrContext* swrContext,
        AVFrame* frame,
        Stream outputStream,
        int inputSampleRate,
        int outputSampleRate,
        int outputChannels,
        ref long totalOutputSamples,
        ref long totalDataBytes)
    {
        var delayedSamples = ffmpeg.swr_get_delay(swrContext, inputSampleRate);
        var outputSampleCount = (int)((delayedSamples + frame->nb_samples) * outputSampleRate / inputSampleRate + 256);
        if (outputSampleCount <= 0)
        {
            return 0;
        }

        byte* outputBuffer = null;
        try
        {
            var lineSize = 0;
            var allocResult = ffmpeg.av_samples_alloc(
                &outputBuffer,
                &lineSize,
                outputChannels,
                outputSampleCount,
                AVSampleFormat.AV_SAMPLE_FMT_S16,
                0);
            if (allocResult < 0)
            {
                return allocResult;
            }

            var outputData = stackalloc byte*[1];
            outputData[0] = outputBuffer;
            var convertedSamples = ffmpeg.swr_convert(
                swrContext,
                outputData,
                outputSampleCount,
                frame->extended_data,
                frame->nb_samples);
            if (convertedSamples < 0)
            {
                return convertedSamples;
            }

            var bufferSize = ffmpeg.av_samples_get_buffer_size(
                &lineSize,
                outputChannels,
                convertedSamples,
                AVSampleFormat.AV_SAMPLE_FMT_S16,
                1);
            if (bufferSize <= 0)
            {
                return bufferSize;
            }

            var buffer = new byte[bufferSize];
            Marshal.Copy((IntPtr)outputBuffer, buffer, 0, bufferSize);
            outputStream.Write(buffer, 0, buffer.Length);
            totalOutputSamples += convertedSamples;
            totalDataBytes += bufferSize;
            return 0;
        }
        finally
        {
            if (outputBuffer != null)
            {
                ffmpeg.av_free(outputBuffer);
            }
        }
    }

    private static int FlushResampler(
        SwrContext* swrContext,
        Stream outputStream,
        int outputSampleRate,
        int outputChannels,
        ref long totalOutputSamples,
        ref long totalDataBytes,
        CancellationToken cancellationToken)
    {
        var outputData = stackalloc byte*[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delayedSamples = ffmpeg.swr_get_delay(swrContext, outputSampleRate);
            if (delayedSamples <= 0)
            {
                return 0;
            }

            var outputSampleCount = (int)Math.Min(delayedSamples + 256, int.MaxValue);
            byte* outputBuffer = null;
            try
            {
                var lineSize = 0;
                var allocResult = ffmpeg.av_samples_alloc(
                    &outputBuffer,
                    &lineSize,
                    outputChannels,
                    outputSampleCount,
                    AVSampleFormat.AV_SAMPLE_FMT_S16,
                    0);
                if (allocResult < 0)
                {
                    return allocResult;
                }

                outputData[0] = outputBuffer;
                var convertedSamples = ffmpeg.swr_convert(
                    swrContext,
                    outputData,
                    outputSampleCount,
                    (byte**)null,
                    0);
                if (convertedSamples < 0)
                {
                    return convertedSamples;
                }
                if (convertedSamples == 0)
                {
                    return 0;
                }

                var bufferSize = ffmpeg.av_samples_get_buffer_size(
                    &lineSize,
                    outputChannels,
                    convertedSamples,
                    AVSampleFormat.AV_SAMPLE_FMT_S16,
                    1);
                if (bufferSize <= 0)
                {
                    return bufferSize;
                }

                var buffer = new byte[bufferSize];
                Marshal.Copy((IntPtr)outputBuffer, buffer, 0, bufferSize);
                outputStream.Write(buffer, 0, buffer.Length);
                totalOutputSamples += convertedSamples;
                totalDataBytes += bufferSize;
            }
            finally
            {
                if (outputBuffer != null)
                {
                    ffmpeg.av_free(outputBuffer);
                }
            }
        }
    }

    private static void WriteWavHeader(Stream stream, int sampleRate, int channels, long dataLength)
    {
        var clampedDataLength = (uint)Math.Min((ulong)uint.MaxValue - 36UL, (ulong)Math.Max(0, dataLength));
        var byteRate = sampleRate * channels * 2;
        var blockAlign = (short)(channels * 2);

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(clampedDataLength + 36));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(clampedDataLength);
    }

    private static AudioTranscodeResult Fail(string error)
    {
        return new AudioTranscodeResult(false, 0, error);
    }

    private static string GetErrorMessage(int error)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? error.ToString();
    }
}
