using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class VolumetricCameraPreview
{
    private const string Downscale = "Hidden/HxVolumetricDownscaleDepth";
    private const string Directional = "Hidden/HxVolumetricDirectionalLight";
    private const string Blur = "Hidden/HxVolumetricDepthAwareBlur";
    private const string Apply = "Hidden/HxVolumetricApply";
    private const string ApplyDirect = "Hidden/HxVolumetricApplyDirect";
    private const string ApplyQueue = "Hidden/HxVolumetricApplyRenderQueue";
    private const string Depth = "VolumetricDepth";
    private const string Volume = "VolumetricTexture";
    private const string Source = ImageEffectPreview.MainTexture;

    internal static SerializedCameraShaderPreview? Find(string name) => name switch
    {
        Directional => new(name, [0], false, ImageEffectPass.VolumetricCamera),
        Blur => new(name, [0, 2], false, ImageEffectPass.VolumetricCamera),
        Apply => new(name, [0, 1], true, ImageEffectPass.VolumetricCamera),
        ApplyDirect or ApplyQueue => new(name, [0], false, ImageEffectPass.VolumetricCamera),
        _ => null
    };

    internal static IReadOnlyList<ShaderPreviewOperation> Prepare(Shader shader, string name,
        IReadOnlyList<ShaderPreviewOperation> selected, IDictionary<string, ShaderPreviewTexture> textures,
        int size, CancellationToken token)
    {
        var settings = Find(name) ?? throw new ArgumentOutOfRangeException(nameof(name));
        var assets = ReadShaderGroup(shader, token);
        var programs = new Dictionary<string, ShaderPreviewReader>(StringComparer.Ordinal);
        var light = ((ShaderPreviewDraw)selected[0]).Light;
        // These passes implement a half-resolution camera and one unshadowed directional light.
        var depth = new ShaderPreviewRenderTarget(Depth, size / 2, Filter: 0);
        var rays = new ShaderPreviewRenderTarget("Directional scattering", size / 2);
        var horizontal = new ShaderPreviewRenderTarget("Horizontal volume blur", size / 2);
        var vertical = new ShaderPreviewRenderTarget("Vertical volume blur", size / 2);
        var upsampled = new ShaderPreviewRenderTarget(Volume, size);
        var ops = new List<ShaderPreviewOperation> { Draw(Downscale, 1) with { Output = depth } };
        textures.Add("Tile5x5", CreateDitherTile());
        const int densityVolumeSlots = 10;
        var volumeSettings = new NumericsVector4[densityVolumeSlots];
        Array.Fill(volumeSettings, new NumericsVector4(0, -1, 0, 0));
        var lightColor = new NumericsVector4(light.Color, 0);
        ops.Add(Draw(Directional, 0) with
        {
            Output = name == Directional ? null : rays,
            Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [Depth] = depth },
            VectorArrays = new Dictionary<string, NumericsVector4[]> { ["hxVolumeSettingsOld"] = volumeSettings },
            Values = new Dictionary<string, NumericsVector4>
            {
                // Camera profile values for density, samples, extinction, scattering and sun.
                ["Density"] = new(0.613f, 8, 0, 0.01f),
                ["Phase"] = Phase(0.277f),
                ["Phase2"] = Phase(0.9999f),
                ["SunSize"] = new(0, 1, 0, 0),
                ["MaxRayDistance"] = new(64, 64, 0, 0),
                ["_SpotLightParams"] = new(-light.Direction, 0),
                ["LightColour"] = lightColor,
                ["LightColour2"] = lightColor,
                ["TintPercent"] = NumericsVector4.Zero,
                ["ExtinctionEffect"] = NumericsVector4.Zero,
                ["AmbientStrength"] = new(0.5f),
                ["AmbientSkyColor"] = NumericsVector4.One,
                ["hxRayOffset"] = NumericsVector4.Zero,
                ["HxTileSize"] = new(5),
                ["VolumeScale"] = new(name == Directional ? 1 : 0.5f),
                ["FirstLight"] = NumericsVector4.One,
                ["ShadowBias"] = NumericsVector4.Zero
            }
        });
        if (name == Directional)
        {
            return ops;
        }
        ops.Add(BlurPass(0, rays, horizontal));
        ops.Add(BlurPass(2, horizontal, name == Blur ? null : vertical));
        if (name == Blur)
        {
            return ops;
        }

        ops.Add(Draw(name == ApplyDirect ? ApplyDirect : Apply, 0) with
        {
            Output = name == ApplyDirect ? null : upsampled,
            Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [Depth] = depth, [Source] = vertical },
            Values = new Dictionary<string, NumericsVector4> { ["DepthThreshold"] = new(0.06f) }
        });
        if (name != ApplyDirect)
        {
            ops.Add(Draw(name == Apply ? Apply : ApplyQueue, name == Apply ? 1 : 0) with
            {
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [Volume] = upsampled }
            });
        }
        return ops;

        ShaderPreviewDraw BlurPass(int index, ShaderPreviewRenderTarget input, ShaderPreviewRenderTarget? output) => Draw(Blur, index) with
        {
            Output = output,
            Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [Depth] = depth, [Source] = input },
            Values = new Dictionary<string, NumericsVector4> { ["BlurDepthFalloff"] = new(5) }
        };

        ShaderPreviewDraw Draw(string source, int index)
        {
            if (source == name)
            {
                return (ShaderPreviewDraw)selected[Array.IndexOf(settings.PassIndices, index)];
            }
            if (!programs.TryGetValue(source, out var reader))
            {
                if (!assets.TryGetValue(source, out var asset))
                {
                    throw new InvalidDataException($"The volumetric preview requires '{source}' in '{shader.assetsFile.fileName}'.");
                }
                reader = ShaderPreviewReader.Read(asset);
                programs.Add(source, reader);
            }
            if (reader.SubShaders is not [var subShader] || index >= subShader.Passes.Count)
            {
                throw new NotSupportedException($"The volumetric preview cannot select pass {index} of '{source}'.");
            }
            var keywords = new HashSet<string>(StringComparer.Ordinal) { "SHADOWS_OFF", "UNITY_COLORSPACE_GAMMA" };
            return new ShaderPreviewDraw(reader.PreparePass(subShader, subShader.Passes[index], keywords, reader.Values), light);
        }
    }

    private static Dictionary<string, Shader> ReadShaderGroup(Shader selected, CancellationToken token)
    {
        var file = selected.assetsFile;
        using var stream = file.reader.Clone();
        var shaders = new Dictionary<string, Shader>(StringComparer.Ordinal);
        foreach (var info in file.m_Objects)
        {
            token.ThrowIfCancellationRequested();
            if (info.classID != (int)ClassIDType.Shader)
            {
                continue;
            }
            var shader = info.m_PathID == selected.m_PathID ? selected : new Shader(new ObjectReader(stream, file, info));
            string name = ShaderPreviewReader.ReadName(shader);
            if ((name == Downscale || Find(name) != null) && !shaders.TryAdd(name, shader))
            {
                throw new InvalidDataException($"The volumetric shader '{name}' appears more than once in '{file.fileName}'.");
            }
        }
        return shaders;
    }

    private static NumericsVector4 Phase(float scattering) =>
        new(1 / (4 * MathF.PI), 1 - scattering * scattering, 1 + scattering * scattering, 2 * scattering);

    private static ShaderPreviewTexture CreateDitherTile()
    {
        // Hx CreateTileTexture writes the normalized values of Tile5x5int to R.
        ReadOnlySpan<byte> order = [8, 18, 22, 0, 13, 4, 14, 9, 19, 21, 16, 23, 1, 12, 6, 10, 7, 15, 24, 3, 20, 2, 11, 5, 17];
        var pixels = new byte[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            pixels[i] = (byte)MathF.Round(order[i] * (255f / 25));
        }
        return new ShaderPreviewTexture(5, 5, 1, D3D11PreviewDevice.R8Unorm, 1, false, pixels, 0, 0, 1, 0);
    }
}
