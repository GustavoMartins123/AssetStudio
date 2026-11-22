using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static AssetStudio.Avalonia.Services.D3D11PreviewDevice;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class PostProcessingV2MultiScalePreview
{
    internal const string Name = PostProcessingV2Preview.Prefix + "MultiScaleVO";
    private const int LayerCount = 16;
    // Sample offsets and weights for the package's non-exhaustive interleaved kernel.
    private static readonly (int X, int Y, int Count)[] Samples =
    [
        (1, 0, 0), (2, 0, 4), (3, 0, 0), (4, 0, 4),
        (1, 1, 4), (1, 2, 0), (1, 3, 8), (1, 4, 0),
        (2, 2, 4), (2, 3, 0), (2, 4, 8), (3, 3, 4)
    ];

    internal static IReadOnlyList<ShaderPreviewOperation> Prepare(Shader shader, ShaderPreviewDraw[] draws,
        MonoBehaviour resources, int size, Func<SerializedFile, PPtr<AssetStudio.Object>, AssetStudio.Object> resolveCompute)
    {
        var (_, _, references) = PostProcessingResourcesPreview.ReadComputeReferences(resources, shader,
            shaderField: 22, computeFields: [7, 8, 9, 10]);
        var downsample1 = ReadCompute(0).GetKernel("MultiScaleVODownsample1");
        var downsample2 = ReadCompute(1).GetKernel("MultiScaleVODownsample2");
        var render = ReadCompute(2).GetKernel("MultiScaleVORender_interleaved");
        var upsample = ReadCompute(3);
        var blend = upsample.GetKernel("MultiScaleVOUpSample_blendout");
        var finish = upsample.GetKernel("MultiScaleVOUpSample");

        var depthCopy = new ShaderPreviewRenderTarget("Camera depth copy", size, Filter: 0) { Format = R32Float };
        var linearDepth = WritableTexture("Linear depth", size, R16Float);
        var lowDepth = new ShaderPreviewRenderTarget[4];
        var tiledDepth = new ShaderPreviewRenderTarget[lowDepth.Length];
        var occlusion = new ShaderPreviewRenderTarget[lowDepth.Length];
        for (int i = 0; i < lowDepth.Length; i++)
        {
            lowDepth[i] = WritableTexture($"Depth level {i + 1}", MipSize(i + 1), R32Float);
            tiledDepth[i] = WritableTexture($"Tiled depth level {i + 1}", MipSize(i + 3), R16Float) with { ArraySize = LayerCount };
            occlusion[i] = WritableTexture($"Occlusion level {i + 1}", lowDepth[i].Size, R8Unorm);
        }

        uint firstGroups = (uint)MipSize(4), secondGroups = (uint)MipSize(6);
        var operations = new List<ShaderPreviewOperation>
        {
            draws[0] with { Output = depthCopy },
            new ShaderPreviewDispatch(downsample1, firstGroups * downsample1.ThreadsX, firstGroups * downsample1.ThreadsY)
            {
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["Depth"] = depthCopy },
                TextureOutputs = new Dictionary<string, ShaderPreviewRenderTarget>
                {
                    ["LinearZ"] = linearDepth, ["DS2x"] = lowDepth[0], ["DS4x"] = lowDepth[1],
                    ["DS2xAtlas"] = tiledDepth[0], ["DS4xAtlas"] = tiledDepth[1]
                },
                Values = new Dictionary<string, NumericsVector4>
                {
                    ["ZBufferParams"] = BuiltInLightingPreview.DepthParameters(reversedDepth: true)
                }
            },
            new ShaderPreviewDispatch(downsample2, secondGroups * downsample2.ThreadsX, secondGroups * downsample2.ThreadsY)
            {
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["DS4x"] = lowDepth[1] },
                TextureOutputs = new Dictionary<string, ShaderPreviewRenderTarget>
                {
                    ["DS8x"] = lowDepth[2], ["DS16x"] = lowDepth[3],
                    ["DS8xAtlas"] = tiledDepth[2], ["DS16xAtlas"] = tiledDepth[3]
                }
            }
        };

        for (int i = 0; i < tiledDepth.Length; i++)
        {
            operations.Add(RenderLevel(render, tiledDepth[i], occlusion[i]));
        }

        var coarser = occlusion[^1];
        for (int i = lowDepth.Length - 2; i >= 0; i--)
        {
            var combined = WritableTexture($"Combined occlusion level {i + 1}", lowDepth[i].Size, R8Unorm);
            operations.Add(Upsample(blend, lowDepth[i + 1], coarser, lowDepth[i], occlusion[i], combined));
            coarser = combined;
        }
        var result = WritableTexture("Multi-scale occlusion", size, R8Unorm);
        operations.Add(Upsample(finish, lowDepth[0], coarser, linearDepth, null, result));
        operations.Add(draws[1] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_MSVOcclusionTexture"] = result } });
        return operations;

        int MipSize(int level) => (size + (1 << level) - 1) >> level;
        SerializedComputeShaderPreview ReadCompute(int index) =>
            SerializedComputeShaderPreview.Read(resolveCompute(resources.assetsFile, references[index]));
    }

    private static ShaderPreviewDispatch RenderLevel(ShaderPreviewComputeKernel kernel, ShaderPreviewRenderTarget depth,
        ShaderPreviewRenderTarget output)
    {
        const float sampleDiameter = 10f;
        float thicknessMultiplier = 2 * MathF.Tan(ShaderPreviewCamera.FieldOfView / 2) * sampleDiameter / depth.Size;
        Span<float> inverseThickness = stackalloc float[Samples.Length];
        Span<float> weights = stackalloc float[Samples.Length];
        float weightSum = 0;
        for (int i = 0; i < Samples.Length; i++)
        {
            var (x, y, count) = Samples[i];
            float thickness = MathF.Sqrt(1f - (x * x + y * y) / 25f);
            inverseThickness[i] = 1f / (thicknessMultiplier * thickness);
            weights[i] = count * thickness;
            weightSum += weights[i];
        }
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] /= weightSum;
        }
        return new ShaderPreviewDispatch(kernel, (uint)depth.Size, (uint)depth.Size, LayerCount)
        {
            Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["DepthTex"] = depth },
            TextureOutputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["Occlusion"] = output },
            Values = new Dictionary<string, NumericsVector4>
            {
                ["gInvSliceDimension"] = InverseSize(depth),
                ["AdditionalParams"] = new(-1, 1, 0, 0) // Default thickness and an enabled effect at unit intensity.
            },
            VectorArrays = new Dictionary<string, NumericsVector4[]>
            {
                ["gInvThicknessTable"] = MemoryMarshal.Cast<float, NumericsVector4>(inverseThickness).ToArray(),
                ["gSampleWeightTable"] = MemoryMarshal.Cast<float, NumericsVector4>(weights).ToArray()
            }
        };
    }

    private static ShaderPreviewDispatch Upsample(ShaderPreviewComputeKernel kernel, ShaderPreviewRenderTarget lowDepth,
        ShaderPreviewRenderTarget lowOcclusion, ShaderPreviewRenderTarget highDepth, ShaderPreviewRenderTarget? highOcclusion,
        ShaderPreviewRenderTarget output)
    {
        const float upsampleTolerance = 1e-12f;
        float stepSize = 1920f / lowDepth.Size;
        float blurTolerance = 1f - MathF.Pow(10f, -4.6f) * stepSize;
        var inputs = new Dictionary<string, ShaderPreviewRenderTarget>
        {
            ["LoResDB"] = lowDepth, ["LoResAO1"] = lowOcclusion, ["HiResDB"] = highDepth
        };
        if (highOcclusion != null)
        {
            inputs.Add("HiResAO", highOcclusion);
        }
        // Each thread writes a 2x2 block and the kernel reads one extra pixel past the edge.
        uint extent = ((uint)output.Size + 3) / 2;
        return new ShaderPreviewDispatch(kernel, extent, extent)
        {
            Inputs = inputs,
            TextureOutputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["AoResult"] = output },
            Values = new Dictionary<string, NumericsVector4>
            {
                ["InvLowResolution"] = InverseSize(lowDepth),
                ["InvHighResolution"] = InverseSize(highDepth),
                ["AdditionalParams"] = new(1f / (1f + upsampleTolerance), stepSize, blurTolerance * blurTolerance, upsampleTolerance)
            }
        };
    }

    private static ShaderPreviewRenderTarget WritableTexture(string name, int size, int format) =>
        new(name, size, Filter: 0) { Format = format, UnorderedAccess = true };

    private static NumericsVector4 InverseSize(ShaderPreviewRenderTarget texture) =>
        new(1f / texture.Size, 1f / texture.Size, 0, 0);
}
