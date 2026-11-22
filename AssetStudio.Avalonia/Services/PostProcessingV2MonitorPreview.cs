using System;
using System.Collections.Generic;
using System.IO;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class PostProcessingV2MonitorPreview
{
    private const string Debug = PostProcessingV2Preview.Prefix + "Debug/";
    internal const string LightMeter = Debug + "LightMeter";
    internal const string Histogram = Debug + "Histogram";
    internal const string Waveform = Debug + "Waveform";
    internal const string Vectorscope = Debug + "Vectorscope";
    private const string HistogramBuffer = "_HistogramBuffer";
    private const string Source = "_Source";

    internal static bool Handles(string name) => name is LightMeter or Histogram or Waveform or Vectorscope;

    internal static IReadOnlyList<ShaderPreviewOperation> Prepare(Shader shader, string name, ShaderPreviewDraw draw,
        MonoBehaviour resources, int size, Func<SerializedFile, PPtr<AssetStudio.Object>, AssetStudio.Object> resolveCompute,
        Func<SerializedFile, PPtr<Shader>, Shader> resolveShader, Func<Shader, ShaderPreviewScene> prepareShader)
    {
        int halfSize = size / 2;
        var monitor = name switch
        {
            LightMeter => new Monitor(15, 1, "KEyeHistogramClear", "KEyeHistogram", HistogramBuffer, 128, 1, sizeof(uint),
                "_ScaleOffsetRes", new(1f / 18, 0.5f, size, size), new(1f / 18, 0.5f, 1f / size, 1f / size)),
            Histogram => new Monitor(16, 4, "KHistogramClear", "KHistogramGather", HistogramBuffer, 256, 1, sizeof(uint),
                "_Params", new(halfSize, halfSize, 0, 3), new(size, size, 0, 0)),
            Waveform => new Monitor(17, 5, "KWaveformClear", "KWaveformGather", "_WaveformBuffer", halfSize, halfSize, 4 * sizeof(uint),
                "_Params", new(halfSize, halfSize, 0, 0), new(halfSize, halfSize, 0.12f, 0)),
            Vectorscope => new Monitor(18, 6, "KVectorscopeClear", "KVectorscopeGather", "_VectorscopeBuffer", 256, 256, sizeof(uint),
                "_Params", new(halfSize, halfSize, 256, 0), new(256, 256, 0.12f, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        var file = resources.assetsFile;
        var (_, copyReference, computeReferences) =
            PostProcessingResourcesPreview.ReadComputeReferences(resources, shader, monitor.ShaderField, monitor.ComputeField);
        var computeAsset = resolveCompute(file, computeReferences[0]);
        var compute = SerializedComputeShaderPreview.Read(computeAsset);

        var buffer = new ShaderPreviewComputeBuffer(monitor.BufferName, monitor.Width * monitor.Height, monitor.Stride);
        var buffers = new Dictionary<string, ShaderPreviewComputeBuffer> { [monitor.BufferName] = buffer };
        var parameters = new Dictionary<string, NumericsVector4> { [monitor.Property] = monitor.ComputeValue };
        var clear = new ShaderPreviewDispatch(compute.GetKernel(monitor.Clear), (uint)monitor.Width, (uint)monitor.Height)
        {
            ComputeBuffers = buffers, Values = parameters
        };
        var gather = new ShaderPreviewDispatch(compute.GetKernel(monitor.Gather), (uint)halfSize, (uint)halfSize)
        {
            ComputeBuffers = buffers, Values = parameters
        };
        var displayValues = new Dictionary<string, NumericsVector4> { [monitor.Property] = monitor.DrawValue };
        var composite = draw with { ComputeBuffers = buffers, Values = displayValues };
        if (name == LightMeter)
        {
            // The v2 Auto Exposure profile starts at the 50th and 95th percentiles, with both EV limits at zero.
            displayValues.Add("_Params", new NumericsVector4(0.5f, 0.95f, 1, 1));
            return [clear, gather with { TextureSources = new Dictionary<string, string> { [Source] = ImageEffectPreview.MainTexture } }, composite];
        }

        var copyShader = resolveShader(file, copyReference);
        if (ShaderPreviewReader.ReadName(copyShader) != PostProcessingV2Preview.Prefix + "Copy" ||
            prepareShader(copyShader).Operations is not [ShaderPreviewDraw copy])
        {
            throw new InvalidDataException("PostProcessResources has an invalid camera copy shader.");
        }
        var halfResolution = new ShaderPreviewRenderTarget("Half-resolution monitor source", halfSize);
        return
        [
            copy with { Output = halfResolution },
            clear,
            gather with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [Source] = halfResolution } },
            composite
        ];
    }

    private sealed record Monitor(int ShaderField, int ComputeField, string Clear, string Gather, string BufferName,
        int Width, int Height, int Stride, string Property, NumericsVector4 ComputeValue, NumericsVector4 DrawValue);
}
