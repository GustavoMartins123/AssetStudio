using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static AssetStudio.Avalonia.Services.SerializedCameraShaderPreview;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class PostProcessingV2Preview
{
    internal const string Prefix = "Hidden/PostProcessing/";
    internal const string GrainBaker = Prefix + "GrainBaker";
    private const string FinalPass = Prefix + "FinalPass";
    private const string Smaa = Prefix + "SubpixelMorphologicalAntialiasing";
    private const string AutoExposure = "_AutoExposureTex";
    private const string CoC = "_CoCTex";

    internal static SerializedCameraShaderPreview? Find(string name) => name switch
    {
        Prefix + "Copy" or Prefix + "CopyStd" or Prefix + "DiscardAlpha" or Prefix + "Uber" => new(name, [0], true),
        // PostProcessLayer enables NaN removal by default, which uses copy pass 1.
        Prefix + "CopyStdFromTexArray" => new(name, [1], true, StereoLayout: ShaderPreviewStereoLayout.TextureArray),
        Prefix + "CopyStdFromDoubleWide" => new(name, [1], true, StereoLayout: ShaderPreviewStereoLayout.DoubleWide),
        Prefix + "DeferredFog" => new(name, [1], true, SceneKeyword: "FOG_EXP2"),
        Prefix + "Bloom" => new(name, [0, 2, 4, 7], true, ImageEffectPass.PostProcessingBloom),
        Prefix + "MotionBlur" => new(name, [0, 1, 2, 3, 4, 5], true, ImageEffectPass.PostProcessingMotionBlur),
        Prefix + "DepthOfField" => new(name, [0, 2, 4, 7, 8], true),
        Prefix + "TemporalAntialiasing" => new(name, [0], true, ImageEffectPass.TemporalAntialiasing),
        Prefix + "ScalableAO" => new(name, [0, 2, 4, 7], false),
        Prefix + "Debug/Overlays" => new(name, [1], false), // View-space normals.
        PostProcessingV2MonitorPreview.LightMeter => new(name, [0], true, ImageEffectPass.PostProcessingLightMeter, "AUTO_EXPOSURE"),
        PostProcessingV2MonitorPreview.Histogram or PostProcessingV2MonitorPreview.Waveform or
            PostProcessingV2MonitorPreview.Vectorscope => new(name, [0], true),
        PostProcessingV2ReflectionPreview.Name => new(name, [0, 1, 3], true, ImageEffectPass.ScreenSpaceReflection),
        PostProcessingV2MultiScalePreview.Name => new(name, [0, 3], false),
        Prefix + "Lut2DBaker" => new(name, [0], false),
        FinalPass => new(name, [0], true),
        Smaa => new(name, [2, 5, 6], true),
        GrainBaker => new(name, [1], false, ImageEffectPass.PostProcessingGrain),
        Prefix + "Texture2DLerp" => new(name, [1], true),
        _ => null
    };

    internal static ShaderPreviewDraw[] Prepare(string name, ShaderPreviewDraw[] draws,
        IDictionary<string, NumericsVector4> values, IDictionary<string, ShaderPreviewTexture> textures, int size)
    {
        values.TryAdd("_RenderViewportScaleFactor", NumericsVector4.One);
        switch (name)
        {
            case Prefix + "Uber":
                // The preview camera uses the package's default exposure and RGB output.
                textures.Add(AutoExposure, CreateFixedExposure());
                values.TryAdd("_UVTransform", new NumericsVector4(1, 1, 0, 0));
                values.TryAdd("_LumaInAlpha", NumericsVector4.Zero);
                break;
            case Prefix + "DeferredFog":
                // The selected exp-squared pass leaves sky pixels unaffected.
                values.TryAdd("_FogColor", new NumericsVector4(0.50f, 0.52f, 0.56f, 1));
                values.TryAdd("_FogParams", new NumericsVector4(0.12f, 0, 0, 0));
                break;
            case Prefix + "Bloom":
                return Bloom(draws, values, textures, size);
            case Prefix + "MotionBlur":
                return MotionBlur(draws, values, size);
            case Prefix + "DepthOfField":
                return DepthOfField(draws, values, size);
            case Prefix + "TemporalAntialiasing":
                TemporalAntialiasingPreview.SetProperties(values, "_Sharpness");
                break;
            case Prefix + "ScalableAO":
                return AmbientOcclusion(draws, values, size);
            case Prefix + "Lut2DBaker":
                const float lutSize = 32;
                values.TryAdd("_Lut2D_Params", new NumericsVector4(lutSize, 0.5f / (lutSize * lutSize), 0.5f / lutSize,
                    lutSize / (lutSize - 1)));
                values.TryAdd("_HueSatCon", new NumericsVector4(0, 1, 1, 0));
                values.TryAdd("_Lift", NumericsVector4.Zero);
                foreach (string property in new[] { "_ColorBalance", "_ColorFilter", "_Brightness", "_InvGamma", "_Gain" })
                {
                    values.TryAdd(property, NumericsVector4.One);
                }
                SetIdentityChannelMixer(values);
                textures.Add("_Curves", CreateIdentityGradingCurves());
                break;
            case FinalPass:
                values.TryAdd("_UVTransform", new NumericsVector4(1, 1, 0, 0));
                break;
            case Smaa:
                var edges = new ShaderPreviewRenderTarget("SMAA edges", size);
                var weights = new ShaderPreviewRenderTarget("SMAA weights", size);
                return
                [
                    draws[0] with { Output = edges },
                    WithInput(draws[1], edges) with { Output = weights },
                    draws[2] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_BlendTex"] = weights } }
                ];
            case GrainBaker:
                values.TryAdd("_NoiseParameters", new NumericsVector4(12.9898f, 78.233f, 43758.5453f, 0));
                break;
            case Prefix + "Texture2DLerp":
                // Unity's texture-to-color pass.
                values.TryAdd("_Interp", new NumericsVector4(0.5f));
                values.TryAdd("_TargetColor", new NumericsVector4(0.30f, 0.46f, 0.58f, 1));
                break;
        }
        return draws;
    }

    internal static void LoadResources(string name, Shader shader, IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, int size, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
        Func<SerializedFile, PPtr<MonoScript>, MonoScript> resolveScript, Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture,
        CancellationToken token)
    {
        if (name is not (FinalPass or Smaa))
        {
            return;
        }
        var resources = PostProcessingResourcesPreview.Find(shader, resolveAsset, resolveScript, token);
        var file = resources.assetsFile;
        using var stream = file.reader.Clone();
        var reader = new ObjectReader(stream, file, file.ObjectInfoDic[resources.m_PathID]);
        var (noise64, _, area, search) = PostProcessingResourcesPreview.ReadTextures(reader);
        if (name == FinalPass)
        {
            var noise = Read(noise64 ?? throw new InvalidDataException("The blueNoise64 resource array is empty."));
            textures.Add("_DitheringTex", noise);
            values.TryAdd("_Dithering_Coords", new NumericsVector4((float)size / noise.Width, (float)size / noise.Height, 0, 0));
        }
        else
        {
            textures.Add("_AreaTex", Read(area));
            textures.Add("_SearchTex", Read(search));
        }

        ShaderPreviewTexture Read(PPtr<Texture> reference) =>
            ShaderPreviewService.ReadTexture(resolveTexture(file, reference), ShaderPreviewTextureDimension.Texture2D);
    }

    private static ShaderPreviewDraw[] Bloom(ShaderPreviewDraw[] draws, IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, int size)
    {
        const float threshold = 1f, knee = threshold * 0.5f + 0.00001f, diffusion = 7f;
        float logSize = MathF.Log2(size / 2f) + diffusion - 10;
        int levels = Math.Clamp((int)MathF.Floor(logSize), 1, 16);
        values.TryAdd("_Threshold", new NumericsVector4(threshold, threshold - knee, 2 * knee, 0.25f / knee));
        values.TryAdd("_Params", new NumericsVector4(65472, 0, 0, 0));
        values.TryAdd("_SampleScale", new NumericsVector4(0.5f + logSize - MathF.Floor(logSize)));
        values.TryAdd("_ColorIntensity", NumericsVector4.One);
        textures.Add(AutoExposure, CreateFixedExposure());

        var operations = new List<ShaderPreviewDraw>(2 * levels);
        var down = new ShaderPreviewRenderTarget[levels];
        down[0] = new ShaderPreviewRenderTarget("Bloom prefilter", size / 2);
        operations.Add(draws[0] with { Output = down[0] });
        for (int i = 1; i < levels; i++)
        {
            down[i] = new ShaderPreviewRenderTarget($"Bloom downsample {i}", Math.Max(size >> (i + 1), 1));
            operations.Add(WithInput(draws[1], down[i - 1]) with { Output = down[i] });
        }
        var previous = down[^1];
        for (int i = levels - 2; i >= 0; i--)
        {
            var up = new ShaderPreviewRenderTarget($"Bloom upsample {i}", down[i].Size);
            operations.Add(draws[2] with { Output = up, Inputs = new Dictionary<string, ShaderPreviewRenderTarget>
            {
                [ImageEffectPreview.MainTexture] = previous,
                ["_BloomTex"] = down[i]
            } });
            previous = up;
        }
        operations.Add(WithInput(draws[3], previous));
        return operations.ToArray();
    }

    private static ShaderPreviewDraw[] DepthOfField(ShaderPreviewDraw[] draws,
        IDictionary<string, NumericsVector4> values, int size)
    {
        // Same optical model as the earlier package, with the v2 film height scaled by the target height.
        SetDepthOfFieldLens(values, size, 0.024f * size / 1080f);
        var coc = new ShaderPreviewRenderTarget("Circle of confusion", size);
        var filtered = new ShaderPreviewRenderTarget("Depth of field prefilter", size / 2);
        var bokeh = new ShaderPreviewRenderTarget("Depth of field bokeh", size / 2);
        return
        [
            draws[0] with { Output = coc },
            draws[1] with { Output = filtered, Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [CoC] = coc } },
            WithInput(draws[2], filtered) with { Output = bokeh },
            WithInput(draws[3], bokeh) with { Output = filtered },
            draws[4] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget>
            {
                [CoC] = coc,
                ["_DepthOfFieldTex"] = filtered
            } }
        ];
    }

    private static ShaderPreviewDraw[] AmbientOcclusion(ShaderPreviewDraw[] draws,
        IDictionary<string, NumericsVector4> values, int size)
    {
        values.TryAdd("_AOParams", new NumericsVector4(1, 0.3f, 0.5f, 10));
        var occlusion = new ShaderPreviewRenderTarget("Scalable AO mask", size / 2);
        var horizontal = new ShaderPreviewRenderTarget("Scalable AO horizontal blur", size);
        var vertical = new ShaderPreviewRenderTarget("Scalable AO vertical blur", size);
        return
        [
            draws[0] with { Output = occlusion },
            WithInput(draws[1], occlusion) with { Output = horizontal },
            WithInput(draws[2], horizontal) with { Output = vertical },
            draws[3] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_SAOcclusionTexture"] = vertical } }
        ];
    }
}
