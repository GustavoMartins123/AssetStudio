using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class ScenePostProcessingPreview
{
    private const string ColorCorrection = "Hidden/BOHColorCorrection";
    private const string Tonemapping = "Hidden/TonemappingColorGrading";
    private const string BloodHaze = "BoH/PostProcess/BloodHaze";
    private const string Histogram = "Hidden/TonemappingColorGradingHistogram";
    private const string TargetHighlight = "BoH/Misc/TargetHighlightPostProcess";
    internal const string HighlightMask = "_MaskTex";
    private const string IdentityLut = "Color correction identity LUT";
    private const string InternalLut = "_InternalLutTex";

    internal static SerializedCameraShaderPreview? Find(string name) => name switch
    {
        ColorCorrection => new(name, [0, 5], true),
        // Both serialized instances use tonemapper zero, with eye adaptation and grading disabled.
        Tonemapping => new(name, [5], true),
        BloodHaze => new(name, [0], true),
        Histogram => new(name, [0], true),
        TargetHighlight => new(name, [2], true, ImageEffectPass.TargetHighlight),
        _ => null
    };

    internal static IReadOnlyList<ShaderPreviewOperation> Prepare(Shader shader, string name,
        IReadOnlyList<ShaderPreviewOperation> operations, IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture, int size, CancellationToken token)
    {
        var draws = operations.OfType<ShaderPreviewDraw>().ToArray();
        switch (name)
        {
            case ColorCorrection:
                return PrepareColorCorrection(draws, values, textures);
            case Tonemapping:
                values.TryAdd("_Exposure", NumericsVector4.One);
                return operations;
            case BloodHaze:
                LoadBloodHaze(shader, values, textures, resolveAsset, resolveTexture, token);
                return operations;
            case Histogram:
                return PrepareHistogram(shader, draws.Single(), resolveAsset, size, token);
            case TargetHighlight:
                // The full-resolution effect composites pass two over the camera's selected object.
                values["_Color"] = new(180f / 255, 0, 0, 1);
                values["_BlinkSpeed"] = NumericsVector4.Zero;
                values["_TimeEditor"] = NumericsVector4.Zero;
                values["_HighLightIntensity"] = new(0.7f);
                values["_OutlineIntensity"] = new(1.2f);
                values["_Distance"] = new(2);
                values["_DistanceSteps"] = NumericsVector4.One;
                return operations;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static IReadOnlyList<ShaderPreviewOperation> PrepareHistogram(Shader shader, ShaderPreviewDraw draw,
        Func<AssetHandle, AssetStudio.Object?> resolveAsset, int size, CancellationToken token)
    {
        const string histogram = "_Histogram";
        var compute = SerializedComputeShaderPreview.Read(EffectPreviewAssets.Require<AssetStudio.Object>(shader,
            ClassIDType.ComputeShader, "HistogramCompute", resolveAsset, token));
        var buffer = new ShaderPreviewComputeBuffer(histogram, 256, 4 * sizeof(uint));
        var buffers = new Dictionary<string, ShaderPreviewComputeBuffer> { [histogram] = buffer };
        var parameters = new Dictionary<string, NumericsVector4>
        {
            ["_Channels"] = NumericsVector4.One,
            ["_IsLinear"] = NumericsVector4.Zero,
            ["_Height"] = new(size, 0, 0, 0)
        };
        return
        [
            new ShaderPreviewDispatch(compute.GetKernel("KHistogramGather"), (uint)size, (uint)size)
            {
                ComputeBuffers = buffers, Values = parameters, ClearOutputs = true,
                TextureSources = new Dictionary<string, string> { ["_Source"] = ImageEffectPreview.MainTexture }
            },
            new ShaderPreviewDispatch(compute.GetKernel("KHistogramScale"), 16, 16)
            {
                ComputeBuffers = buffers, Values = parameters
            },
            draw with
            {
                ComputeBuffers = buffers,
                Values = new Dictionary<string, NumericsVector4>
                {
                    ["_Size"] = new(size, size, 0, 0), ["_Channel"] = new(3, 0, 0, 0),
                    ["_ColorR"] = new(1, 0, 0, 1), ["_ColorG"] = new(0, 1, 0, 1),
                    ["_ColorB"] = new(0, 0, 1, 1), ["_ColorL"] = NumericsVector4.One
                }
            }
        ];
    }

    private static IReadOnlyList<ShaderPreviewOperation> PrepareColorCorrection(ShaderPreviewDraw[] draws,
        IDictionary<string, NumericsVector4> values, IDictionary<string, ShaderPreviewTexture> textures)
    {
        const int height = 32, width = height * height;
        if (draws is not [var composite, var bake] ||
            !composite.Pass.Fragment.Textures.Any(texture => texture.Name == InternalLut) ||
            bake.Pass.Fragment.Textures is not [{ Name: ImageEffectPreview.MainTexture }])
        {
            throw new NotSupportedException("The color correction shader has an unexpected LUT pass layout.");
        }
        // GenerateIdentityLut flattens the blue slices horizontally. UpdateLut blits pass five into ARGB32.
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = Encode(x % height);
                pixels[offset + 1] = Encode(y);
                pixels[offset + 2] = Encode(x / height);
                pixels[offset + 3] = 255;
            }
        }
        textures.Add(IdentityLut, new ShaderPreviewTexture(width, height, 1, D3D11PreviewDevice.Rgba8,
            4, false, pixels, 1, 1, 1, 0));
        var lut = new ShaderPreviewRenderTarget(InternalLut, width) { Height = height, Format = D3D11PreviewDevice.Rgba8 };
        // The preview camera uses tonemapper zero and exposure 2, as serialized on the scene camera.
        values.TryAdd("_Exposure", new NumericsVector4(2, 0, 0, 0));
        values["_InternalLutParams"] = new(1f / width, 1f / height, height - 1, 0);
        return
        [
            bake with { Output = lut, TextureSources = new Dictionary<string, string> { [ImageEffectPreview.MainTexture] = IdentityLut } },
            composite with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [InternalLut] = lut } }
        ];

        static byte Encode(int channel) => (byte)MathF.Round(255f * channel / (height - 1));
    }

    private static void LoadBloodHaze(Shader shader, IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture, CancellationToken token)
    {
        MonoBehaviour? profile = null;
        IDictionary? fields = null;
        // The full-strength volume defines the isolated blood haze preview.
        foreach (var handle in EffectPreviewAssets.Find(shader, ClassIDType.MonoBehaviour, "BloodHaze_PostProcess", token))
        {
            if (resolveAsset(handle) is not MonoBehaviour { version: [2019, 3, ..], m_Enabled: 1 } candidate ||
                candidate.ToType() is not { } data || data["active"] is not byte and not bool ||
                !Convert.ToBoolean(data["active"]) ||
                EffectPreviewAssets.Float(EffectPreviewAssets.Fields(data, "_EffectBlending"), "value") != 1)
            {
                continue;
            }
            if (profile != null)
            {
                throw new InvalidDataException("The project has multiple full-strength blood haze profiles.");
            }
            profile = candidate;
            fields = data;
        }
        if (profile == null || fields == null)
        {
            throw new InvalidDataException("The blood haze preview requires the game's full-strength profile.");
        }
        values["_NoiseTilings"] = Vector(fields, "_NoiseTilings", "x", "y", "z", "w");
        values["_NoisePannings"] = Vector(fields, "_NoisePannings", "x", "y", "z", "w");
        values["_Color"] = Vector(fields, "_Color", "r", "g", "b", "a");
        values["_OverallSpeed"] = new(Read(fields, "_OverallSpeed"), 0, 0, 0);
        values["_FrameParams"] = new(Read(fields, "_SmoothLow2"), Read(fields, "_SmoothHigh2"),
            Read(fields, "_VignetteHardness2"), Read(fields, "_GlobalOpacity2"));
        var noise = EffectPreviewAssets.Fields(EffectPreviewAssets.Fields(fields, "_Noise"), "value");
        if (noise["m_FileID"] is not int fileId || noise["m_PathID"] is not long pathId)
        {
            throw new InvalidDataException("The blood haze profile has no rendered noise texture reference.");
        }
        var file = profile.assetsFile;
        var reference = new PPtr<Texture>(fileId, pathId, file);
        textures.Add("_Noise", ShaderPreviewService.ReadTexture(resolveTexture(file, reference), ShaderPreviewTextureDimension.Texture2D));

        static float Read(IDictionary data, string key) => EffectPreviewAssets.Float(EffectPreviewAssets.Fields(data, key), "value");

        static NumericsVector4 Vector(IDictionary data, string key, string x, string y, string z, string w)
        {
            var vector = EffectPreviewAssets.Fields(EffectPreviewAssets.Fields(data, key), "value");
            return new(EffectPreviewAssets.Float(vector, x), EffectPreviewAssets.Float(vector, y),
                EffectPreviewAssets.Float(vector, z), EffectPreviewAssets.Float(vector, w));
        }
    }
}
