using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

// Camera passes for serialized Built-in and Post Processing shaders.
internal sealed record SerializedCameraShaderPreview(string ShaderName, int[] PassIndices, bool UsesCameraColor,
    ImageEffectPass? Effect = null, string? SceneKeyword = null, uint DepthBlitSamples = 0,
    ShaderPreviewStereoLayout? StereoLayout = null)
{
    internal static string? ShadowFilter(string name) => name == "Hidden/Internal-ScreenSpaceShadows" ? "HardShadow" : null;

    internal static SerializedCameraShaderPreview? Find(string name) => name switch
    {
        "Hidden/Internal-DeferredReflections" => new(name, [0], false, SceneKeyword: "UNITY_HDR_ON"),
        "Hidden/Internal-CombineDepthNormals" => new(name, [0], false),
        "Hidden/BlitToDepth" => new(name, [0], false, DepthBlitSamples: 1),
        "Hidden/BlitToDepth_MSAA" => new(name, [0], false, DepthBlitSamples: 4),
        "Hidden/Internal-MotionVectors" => new(name, [1], false, ImageEffectPass.ReprojectionMotion),
        "Hidden/Post FX/FXAA" => new(name, [0], true),
        "Hidden/Post FX/Fog" => new(name, [1], true),
        "Hidden/Post FX/Grain Generator" => new(name, [1], false, ImageEffectPass.PostProcessingGrain),
        "Hidden/Post FX/Builtin Debug Views" => new(name, [1], false),
        "Hidden/Post FX/Ambient Occlusion" => new(name, [0, 3, 5], true, SceneKeyword: "FOG_OFF"),
        "Hidden/Post FX/Bloom" => new(name, [0, 1, 2, 3], true, ImageEffectPass.PostProcessingBloom),
        "Hidden/Post FX/Depth Of Field" => new(name, [0, 2, 4, 7], true),
        "Hidden/Post FX/Motion Blur" => new(name, [0, 1, 2, 3, 4, 5], true, ImageEffectPass.PostProcessingMotionBlur),
        "Hidden/Post FX/Lut Generator" => new(name, [0], false),
        "Hidden/Post FX/RadialBlur" => new(name, [0], true),
        "Hidden/Artisan/Pixelate" => new(name, [0], true),
        "Hidden/Artisan/Bloom" => new(name, [0, 1, 2], true),
        "Hidden/ConvertTexture" => new(name, [0], false),
        "Hidden/Compositing" => new(name, [0], true),
        "Hidden/VideoDecode" => new(name, [0], false, ImageEffectPass.VideoDecode),
        StereoCameraPreview.ReadArraySlice => new(name, [0], true, StereoLayout: ShaderPreviewStereoLayout.TextureArray),
        StereoCameraPreview.WriteArraySlice => new(name, [0], true),
        StereoCameraPreview.VideoComposite => new(name, [0], true, StereoLayout: ShaderPreviewStereoLayout.DoubleWide),
        "Hidden/Post FX/Eye Adaptation" => new(name, [2], true, ImageEffectPass.ExposureHistogram, "AUTO_KEY_VALUE"),
        "Hidden/Post FX/Temporal Anti-aliasing" => new(name, [0], true, ImageEffectPass.TemporalAntialiasing),
        "Hidden/Post FX/Screen Space Reflection" => new(name, [0, 1, 2, 3, 5, 6, 8], true, ImageEffectPass.ScreenSpaceReflection),
        _ => PostProcessingV2Preview.Find(name)
    };

    internal IReadOnlyList<ShaderPreviewOperation> Prepare(IReadOnlyList<ShaderPreviewOperation> passes,
        IDictionary<string, NumericsVector4> values, IDictionary<string, ShaderPreviewTexture> textures, int size)
    {
        var draws = passes.OfType<ShaderPreviewDraw>().ToArray();
        if (draws.Length != PassIndices.Length || draws.Length != passes.Count)
        {
            throw new NotSupportedException($"'{ShaderName}' has an unsupported Built-in camera pass layout.");
        }
        if (DepthBlitSamples != 0 &&
            (draws[0].Pass.State is not { ZWrite: true, ZTest: ShaderPreviewDepth.Always, Stencil: null, AlphaToMask: false } ||
                draws[0].Pass.Fragment.Textures is not [
                    { Name: ImageEffectPreview.MainTexture, Dimension: ShaderPreviewTextureDimension.Texture2D } texture] ||
                (texture.SamplerSlot >= 0) != (DepthBlitSamples == 1)))
        {
            throw new NotSupportedException($"'{ShaderName}' has an unsupported depth blit pass.");
        }

        if (StereoLayout is { } stereo)
        {
            return StereoCameraPreview.PrepareCopy(draws[0], stereo, size);
        }
        if (ShaderName == StereoCameraPreview.WriteArraySlice)
        {
            return StereoCameraPreview.PrepareArrayWrite(draws[0], size);
        }
        if (ShaderName.StartsWith(PostProcessingV2Preview.Prefix, StringComparison.Ordinal))
        {
            return PostProcessingV2Preview.Prepare(ShaderName, draws, values, textures, size);
        }
        switch (ShaderName)
        {
            case "Hidden/Post FX/FXAA":
                // Post Processing Stack v1's Default FXAA quality preset.
                values.TryAdd("_QualitySettings", new NumericsVector4(0.75f, 0.166f, 0.0833f, 0));
                break;
            case "Hidden/Post FX/Fog":
                values.TryAdd("_FogColor", new NumericsVector4(0.50f, 0.52f, 0.56f, 1));
                values.TryAdd("_Density", new NumericsVector4(0.12f));
                break;
            case "Hidden/Post FX/Grain Generator":
            case "Hidden/Post FX/Builtin Debug Views":
                // These fullscreen passes have no source texture but still transform their blit UVs.
                values.TryAdd("_MainTex_ST", new NumericsVector4(1, 1, 0, 0));
                break;
            case "Hidden/Post FX/Ambient Occlusion":
                return AmbientOcclusion(draws, values, size);
            case "Hidden/Post FX/Bloom":
                return Bloom(draws, values, textures, size);
            case "Hidden/Post FX/Depth Of Field":
                return DepthOfField(draws, values, size);
            case "Hidden/Post FX/Motion Blur":
                return MotionBlur(draws, values, size);
            case "Hidden/Post FX/Lut Generator":
                SetColorGradingInputs(values, textures);
                break;
            case "Hidden/Post FX/Eye Adaptation":
                ExposureHistogramPreview.SetProperties(values, size);
                break;
            case "Hidden/Post FX/Temporal Anti-aliasing":
                TemporalAntialiasingPreview.SetProperties(values);
                break;
            case "Hidden/Post FX/Screen Space Reflection":
                return ScreenSpaceReflectionPreview.Prepare(draws, values, size);
            case "Hidden/Post FX/RadialBlur":
                values.TryAdd("_Strength", new NumericsVector4(0.08f));
                values.TryAdd("_Weight", NumericsVector4.One);
                break;
            case "Hidden/Artisan/Pixelate":
                values.TryAdd("center", new NumericsVector4(0.5f, 0.5f, 0, 0));
                break;
            case "Hidden/Artisan/Bloom":
                return ArtisanBloom(draws, size);
            case "Hidden/ConvertTexture":
                values.TryAdd("_MainTex_ST", new NumericsVector4(1, 1, 0, 0));
                values.TryAdd("_faceIndex", NumericsVector4.Zero); // Positive X face in Unity's cubemap order.
                break;
            case "Hidden/Compositing":
                values.TryAdd("_AlphaParam", new NumericsVector4(0.75f));
                values.TryAdd("_SecondAlphaParam", new NumericsVector4(0.25f));
                break;
            case "Hidden/VideoDecode":
                if (!draws[0].Pass.PassName.Equals("YCbCr_To_RGB1", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("The video shader has no planar BT.601 RGB pass in this layout.");
                }
                values.TryAdd("_MainTex_ST", new NumericsVector4(1, 1, 0, 0));
                values.TryAdd("_RightEyeUVOffset", NumericsVector4.Zero);
                values.TryAdd("unity_StereoEyeIndex", NumericsVector4.Zero);
                break;
        }
        return draws;
    }

    internal ShaderPreviewTexture? CreateSceneTexture(string name, ShaderPreviewTextureDimension dimension) => (ShaderName, name, dimension) switch
    {
        ("Hidden/ConvertTexture", ImageEffectPreview.MainTexture, ShaderPreviewTextureDimension.Cube) => CreateConversionCube(),
        ("Hidden/Compositing", "_SecondTex", ShaderPreviewTextureDimension.Texture2D) => ShaderPreviewService.CreateClipMask(),
        _ => null
    };

    private static ShaderPreviewTexture CreateConversionCube()
    {
        const int size = 16, faces = 6;
        var pixels = new byte[size * size * 4 * faces];
        for (int face = 0; face < faces; face++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int offset = ((face * size + y) * size + x) * 4;
                    pixels[offset] = (byte)(24 + x * 13);
                    pixels[offset + 1] = (byte)(24 + y * 13);
                    pixels[offset + 2] = (byte)(36 + face * 35);
                    pixels[offset + 3] = 255;
                }
            }
        }
        return new ShaderPreviewTexture(size, size, 1, 28, 4, false, pixels, 1, 1, 1, 0, faces,
            Dimension: ShaderPreviewTextureDimension.Cube);
    }

    private static void SetColorGradingInputs(IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures)
    {
        values.TryAdd("_MainTex_ST", new NumericsVector4(1, 1, 0, 0));
        values.TryAdd("_LutParams", new NumericsVector4(32, 1f / 2048, 1f / 64, 32f / 31));
        SetIdentityChannelMixer(values);
        foreach (string name in new[] { "_Balance", "_InvGamma", "_Gain", "_Power", "_Slope", "_Saturation", "_Contrast" })
        {
            values.TryAdd(name, NumericsVector4.One);
        }
        foreach (string name in new[] { "_Lift", "_Offset", "_HueShift" })
        {
            values.TryAdd(name, NumericsVector4.Zero);
        }

        textures.Add("_Curves", CreateIdentityGradingCurves());
    }

    internal static void SetIdentityChannelMixer(IDictionary<string, NumericsVector4> values)
    {
        values.TryAdd("_ChannelMixerRed", NumericsVector4.UnitX);
        values.TryAdd("_ChannelMixerGreen", NumericsVector4.UnitY);
        values.TryAdd("_ChannelMixerBlue", NumericsVector4.UnitZ);
    }

    internal static ShaderPreviewTexture CreateIdentityGradingCurves()
    {
        // Flat hue and saturation adjustments in the first row, identity RGB and master in the second.
        const int width = 128;
        var pixels = new byte[width * 2 * 8];
        var channels = MemoryMarshal.Cast<byte, Half>(pixels.AsSpan());
        for (int i = 0; i < width; i++)
        {
            channels.Slice(i * 4, 4).Fill((Half)0.5f);
            channels.Slice((i + width) * 4, 4).Fill((Half)((float)i / width));
        }
        return new ShaderPreviewTexture(width, 2, 1, 10, 8, false, pixels, 1, 1, 1, 0);
    }

    private static ShaderPreviewDraw[] ArtisanBloom(ShaderPreviewDraw[] draws, int size)
    {
        var first = new ShaderPreviewRenderTarget("Artisan bloom first blur", size / 2);
        var second = new ShaderPreviewRenderTarget("Artisan bloom second blur", size / 2);
        return [draws[0] with { Output = first }, WithInput(draws[1], first) with { Output = second },
            WithInput(draws[2], second) with
            {
                TextureSources = new Dictionary<string, string> { ["_SourceTex"] = ImageEffectPreview.MainTexture }
            }];
    }

    private static ShaderPreviewDraw[] DepthOfField(ShaderPreviewDraw[] draws, IDictionary<string, NumericsVector4> values, int size)
    {
        // Full-frame circle of confusion, then the medium bokeh kernel at half resolution.
        // The preview camera has temporal anti-aliasing disabled.
        SetDepthOfFieldLens(values, size, filmHeight: 0.024f);
        var coc = new ShaderPreviewRenderTarget("Circle of confusion", size);
        var prefilter = new ShaderPreviewRenderTarget("Depth of field prefilter", size / 2);
        var bokeh = new ShaderPreviewRenderTarget("Depth of field bokeh", size / 2);
        return [draws[0] with { Output = coc },
            draws[1] with { Output = prefilter, Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_CoCTex"] = coc } },
            WithInput(draws[2], prefilter) with { Output = bokeh }, WithInput(draws[3], bokeh)];
    }

    internal static void SetDepthOfFieldLens(IDictionary<string, NumericsVector4> values, int size, float filmHeight)
    {
        const float focalLength = 0.05f, focusDistance = 4f, aperture = 1.4f;
        float maxCoC = MathF.Min(0.05f, 10f / size); // Medium kernel.
        float lensCoefficient = focalLength * focalLength / (aperture * (focusDistance - focalLength) * filmHeight * 2);
        values.TryAdd("_Distance", new NumericsVector4(focusDistance));
        values.TryAdd("_LensCoeff", new NumericsVector4(lensCoefficient));
        values.TryAdd("_MaxCoC", new NumericsVector4(maxCoC));
        values.TryAdd("_RcpMaxCoC", new NumericsVector4(1 / maxCoC));
        values.TryAdd("_RcpAspect", NumericsVector4.One);
    }

    internal static ShaderPreviewDraw[] MotionBlur(ShaderPreviewDraw[] draws, IDictionary<string, NumericsVector4> values, int size)
    {
        int radius = size * 5 / 100;
        int tileSize = ((radius - 1) / 8 + 1) * 8;
        int tileLoop = tileSize / 8;
        values.TryAdd("_MainTex_ST", new NumericsVector4(1, 1, 0, 0));
        values.TryAdd("_VelocityScale", new NumericsVector4(0.5f)); // 180-degree shutter
        values.TryAdd("_MaxBlurRadius", new NumericsVector4(radius));
        values.TryAdd("_RcpMaxBlurRadius", new NumericsVector4(1f / radius));
        values.TryAdd("_TileMaxOffs", new NumericsVector4(-0.5f * (tileLoop - 1)));
        values.TryAdd("_TileMaxLoop", new NumericsVector4(tileLoop));
        values.TryAdd("_LoopCount", new NumericsVector4(5));
        var velocity = new ShaderPreviewRenderTarget("Motion velocity and depth", size, Filter: 0);
        var tile2 = new ShaderPreviewRenderTarget("Motion tiles 2", size / 2, Filter: 0);
        var tile4 = new ShaderPreviewRenderTarget("Motion tiles 4", size / 4, Filter: 0);
        var tile8 = new ShaderPreviewRenderTarget("Motion tiles 8", size / 8, Filter: 0);
        var tiles = new ShaderPreviewRenderTarget("Motion tile maximums", size / tileSize, Filter: 0);
        var neighbors = new ShaderPreviewRenderTarget("Motion neighbor maximums", size / tileSize, Filter: 0);
        return [draws[0] with { Output = velocity }, WithInput(draws[1], velocity) with { Output = tile2 },
            WithInput(draws[2], tile2) with { Output = tile4 }, WithInput(draws[2], tile4) with { Output = tile8 },
            WithInput(draws[3], tile8) with { Output = tiles }, WithInput(draws[4], tiles) with { Output = neighbors },
            draws[5] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget>
            {
                ["_VelocityTex"] = velocity,
                ["_NeighborMaxTex"] = neighbors
            } }];
    }

    private static ShaderPreviewDraw[] AmbientOcclusion(ShaderPreviewDraw[] draws, IDictionary<string, NumericsVector4> values, int size)
    {
        values.TryAdd("_Intensity", NumericsVector4.One);
        values.TryAdd("_Radius", new NumericsVector4(0.3f));
        values.TryAdd("_SampleCount", new NumericsVector4(10));
        values.TryAdd("_Downsample", NumericsVector4.One);
        var occlusion = new ShaderPreviewRenderTarget("Ambient occlusion", size);
        var horizontal = new ShaderPreviewRenderTarget("Ambient occlusion horizontal blur", size);
        return [draws[0] with { Output = occlusion },
            WithInput(draws[1], occlusion) with { Output = horizontal }, WithInput(draws[2], horizontal)];
    }

    private static ShaderPreviewDraw[] Bloom(ShaderPreviewDraw[] draws, IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, int size)
    {
        // The scene has a fixed exposure, then the package's prefilter and down/up-sample passes accumulate highlights.
        const float threshold = 1.243f, knee = threshold * 0.5f + 0.00001f;
        values.TryAdd("_Threshold", new NumericsVector4(threshold));
        values.TryAdd("_Curve", new NumericsVector4(threshold - knee, 2 * knee, 0.25f / knee, 0));
        values.TryAdd("_PrefilterOffs", NumericsVector4.Zero);
        values.TryAdd("_SampleScale", new NumericsVector4(0.75f));
        textures.Add("_AutoExposure", CreateFixedExposure());
        var prefilter = new ShaderPreviewRenderTarget("Bloom prefilter", size / 2);
        var first = new ShaderPreviewRenderTarget("Bloom first reduction", size / 4);
        var second = new ShaderPreviewRenderTarget("Bloom second reduction", size / 8);
        return [draws[0] with { Output = prefilter }, WithInput(draws[1], prefilter) with { Output = first },
            WithInput(draws[2], first) with { Output = second },
            draws[3] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget>
            {
                [ImageEffectPreview.MainTexture] = second,
                ["_BaseTex"] = first
            } }];
    }

    internal static ShaderPreviewTexture CreateFixedExposure() =>
        new(1, 1, 1, 41 /* R32_FLOAT */, 4, false, BitConverter.GetBytes(1f), 1, 1, 1, 0);

    internal static ShaderPreviewDraw WithInput(ShaderPreviewDraw draw, ShaderPreviewRenderTarget input) =>
        draw with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [ImageEffectPreview.MainTexture] = input } };
}
