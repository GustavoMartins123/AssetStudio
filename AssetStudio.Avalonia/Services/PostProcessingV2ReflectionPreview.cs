using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class PostProcessingV2ReflectionPreview
{
    internal const string Name = PostProcessingV2Preview.Prefix + "ScreenSpaceReflections";
    private const string Noise = "_Noise";

    internal static IReadOnlyList<ShaderPreviewOperation> Prepare(Shader shader, ShaderPreviewDraw[] draws, MonoBehaviour resources,
        IDictionary<string, NumericsVector4> values, IDictionary<string, ShaderPreviewTexture> textures, int size,
        Func<SerializedFile, PPtr<AssetStudio.Object>, AssetStudio.Object> resolveCompute,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture)
    {
        var (noise256, _, computeReferences) =
            PostProcessingResourcesPreview.ReadComputeReferences(resources, shader, shaderField: 23, computeFields: [11]);
        var file = resources.assetsFile;
        var noise = ShaderPreviewService.ReadTexture(resolveTexture(file,
            noise256 ?? throw new InvalidDataException("The blueNoise256 resource array is empty.")), ShaderPreviewTextureDimension.Texture2D);
        textures.Add(Noise, noise);
        var gaussian = SerializedComputeShaderPreview.Read(resolveCompute(file, computeReferences[0])).GetKernel("KMain");

        // The package's Medium preset uses a half-resolution power-of-two target. Keep an 8x8 final mip.
        int resolution = 1 << (BitOperations.Log2((uint)size) - 1);
        int lodCount = BitOperations.Log2((uint)resolution) - 3;
        values.TryAdd("_Params", new NumericsVector4(0.5f, 0.5f, 100, lodCount));
        values.TryAdd("_Params2", new NumericsVector4(1, (float)resolution / noise.Width, 16, 32));
        values.TryAdd("_Test_TexelSize", new NumericsVector4(1f / resolution, 1f / resolution, resolution, resolution));

        var hits = new ShaderPreviewRenderTarget("Reflection ray hits", resolution, Filter: 0);
        var baseLevel = new ShaderPreviewRenderTarget("Resolved reflection", resolution);
        var pyramid = new ShaderPreviewRenderTarget("Reflection mip pyramid", resolution, Filter: 2) { MipLevels = lodCount + 1 };
        // Use the scene-view resolve. Each Gaussian dispatch writes a complete mip before compositing.
        var operations = new List<ShaderPreviewOperation>
        {
            draws[0] with { Output = hits },
            draws[1] with { Output = baseLevel, Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_Test"] = hits } },
            new ShaderPreviewCopyMip(baseLevel, pyramid, 0)
        };
        var previous = baseLevel;
        for (int level = 1; level <= lodCount; level++)
        {
            int width = resolution >> level;
            var downsample = new ShaderPreviewRenderTarget($"Reflection mip {level}", width) { UnorderedAccess = true };
            operations.Add(new ShaderPreviewDispatch(gaussian, (uint)width, (uint)width)
            {
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_Source"] = previous },
                TextureOutputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_Result"] = downsample },
                Values = new Dictionary<string, NumericsVector4> { ["_Size"] = new(width, width, 1f / width, 1f / width) }
            });
            operations.Add(new ShaderPreviewCopyMip(downsample, pyramid, level));
            previous = downsample;
        }
        operations.Add(draws[2] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_Resolve"] = pyramid } });
        return operations;
    }
}
