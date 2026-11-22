using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal enum ImageEffectPass
{
    BrightFilter, LensFlare, BloomComposite, BloomStretch,
    Vignette, ChromaticAberration, SeparableBlur, Antialiasing, GlobalFog, Overlay, RenderTargetDisplay,
    SunShafts, DepthOfField, Bokeh, ReprojectionMotion, ObjectMotion, MotionBlur, Thicken, Noise, SkinDeformation
}

internal sealed class ImageEffectPreview
{
    internal const string MainTexture = "_MainTex";
    internal const string BokehBlurredColor = "_BlurredColor";
    internal const string BokehAperture = "Bokeh aperture";
    private const string CameraColor = "_ColorBuffer";
    private static readonly NumericsVector4[] FlareColors =
    [
        new(0.4f, 0.4f, 0.8f, 0.75f), new(0.4f, 0.8f, 0.8f, 0.75f),
        new(0.8f, 0.4f, 0.8f, 0.75f), new(0.8f, 0.4f, 0, 0.75f)
    ];

    private readonly ImageEffectPass _pass;
    private readonly Dictionary<string, ShaderPreviewTexture> _textures = new(StringComparer.Ordinal);

    internal ImageEffectPreview(ImageEffectPass pass, int frameSize)
    {
        _pass = pass;
        int inputSize = frameSize / 4;
        _textures.Add(MainTexture, CreateTexture(inputSize));
        if (pass == ImageEffectPass.BloomComposite)
        {
            _textures.Add(CameraColor, CreateTexture(frameSize));
        }
    }

    internal IReadOnlyDictionary<string, ShaderPreviewTexture> Textures => _textures;

    internal static (IReadOnlyList<ShaderPreviewDraw>? Draws, ImageEffectPass? Pass) SelectDraws(IEnumerable<ShaderPreviewDraw> draws, int size)
    {
        var passes = draws.ToArray();
        if (passes.Length == 1 && UsesMatrix(passes[0].Pass.Fragment, "_AM_MATRIX_CURR_REPROJ") &&
            UsesTexture(passes[0].Pass.Fragment, BuiltInLightingPreview.CameraDepth))
        {
            return ([passes[0]], ImageEffectPass.ReprojectionMotion);
        }
        if (passes.Length is 4 or 8 && UsesMatrix(passes[2].Pass.Vertex, "_AM_MATRIX_PREV_MVP") &&
            UsesVector(passes[2].Pass.Fragment, "_AM_OBJECT_ID", 1) &&
            UsesTexture(passes[2].Pass.Fragment, BuiltInLightingPreview.CameraDepth))
        {
            // Standard quality, opaque geometry. The animated vertices are provided by the preview mesh.
            return ([passes[2]], ImageEffectPass.ObjectMotion);
        }
        if (passes.Length is 2 or 4 && UsesVector(passes[1].Pass.Fragment, "_AM_BLUR_STEP", 4) &&
            UsesTexture(passes[1].Pass.Fragment, MotionPreview.MotionTexture) &&
            UsesTexture(passes[1].Pass.Fragment, BuiltInLightingPreview.CameraDepth))
        {
            return ([passes[1]], ImageEffectPass.MotionBlur);
        }
        if (passes.Length == 3 && UsesMatrix(passes[0].Pass.Fragment, "_AM_WORLD_TO_LOCAL_MATRIX") &&
            UsesTexture(passes[0].Pass.Fragment, MotionPreview.BoneTexture) && UsesTexture(passes[0].Pass.Fragment, MotionPreview.BoneIndices) &&
            UsesTexture(passes[0].Pass.Fragment, MotionPreview.BaseVertices))
        {
            return ([passes[0]], ImageEffectPass.SkinDeformation);
        }
        if (passes.Length == 5 && passes.All(draw => IsFullscreenBlit(draw.Pass)) &&
            UsesVector(passes[1].Pass.Vertex, "_BlurRadius4", 4) &&
            UsesTexture(passes[2].Pass.Fragment, BuiltInLightingPreview.CameraDepth) &&
            UsesVector(passes[2].Pass.Fragment, "_SunPosition", 4) &&
            UsesVector(passes[0].Pass.Fragment, "_SunColor", 4) && UsesTexture(passes[0].Pass.Fragment, CameraColor))
        {
            return (SunShaftPasses(passes, size), ImageEffectPass.SunShafts);
        }
        if (passes.Length == 23 && UsesTexture(passes[10].Pass.Fragment, BuiltInLightingPreview.CameraDepth) &&
            UsesVector(passes[10].Pass.Fragment, "_CurveParams", 4) && UsesTexture(passes[10].Pass.Fragment, MainTexture) &&
            UsesTexture(passes[6].Pass.Fragment, MainTexture) && UsesVector(passes[17].Pass.Fragment, "_Offsets", 4) &&
            UsesTexture(passes[18].Pass.Fragment, "_LowRez"))
        {
            return (DepthOfFieldPasses(passes, size), ImageEffectPass.DepthOfField);
        }
        if (passes.Length == 3 && IsFullscreenBlit(passes[0].Pass) && UsesVector(passes[0].Pass.Fragment, "_BokehParams", 4) &&
            UsesTexture(passes[0].Pass.Fragment, BokehBlurredColor) && passes[2].Pass.Geometry is { } geometry &&
            UsesVector(geometry, "_Screen", 3) && passes[2].Pass.Vertex.StructuredBuffers.Any(binding => binding.Name == "pointBuffer"))
        {
            return ([passes[0] with { PointPass = ShaderPreviewPointPass.Collect },
                passes[2] with
                {
                    PointPass = ShaderPreviewPointPass.Draw,
                    TextureSources = new Dictionary<string, string> { [MainTexture] = BokehAperture }
                }], ImageEffectPass.Bokeh);
        }
        if (passes.Length == 5 && passes.All(draw => IsFullscreenBlit(draw.Pass) && draw.Pass.State.ColorMask == 7 &&
            draw.Pass.Fragment.Textures.Length == 2 && UsesTexture(draw.Pass.Fragment, "_Overlay")) &&
            UsesVector(passes[0].Pass.Fragment, "_Intensity", 1))
        {
            // The ScreenOverlay component's Overlay mode is pass 3 of the five blend operations.
            return ([passes[3]], ImageEffectPass.Overlay);
        }
        foreach (var draw in passes)
        {
            if (Identify(draw.Pass) is { } effect)
            {
                return ([draw], effect);
            }
        }
        return (null, null);
    }

    private static IReadOnlyList<ShaderPreviewDraw> SunShaftPasses(ShaderPreviewDraw[] passes, int size)
    {
        var mask = new ShaderPreviewRenderTarget("Sun shaft mask", size / 2);
        var blur = new ShaderPreviewRenderTarget("Sun shaft blur", size / 2);
        var draws = new List<ShaderPreviewDraw> { passes[2] with { Output = mask } };
        const float radius = 2.5f;
        // The default effect performs two pairs of radial blits at half resolution.
        for (int i = 0; i < 4; i++)
        {
            float offset = radius * (i == 0 ? 1 : i * 6) / 768f;
            bool fromMask = i % 2 == 0;
            draws.Add(passes[1] with
            {
                Output = fromMask ? blur : mask,
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [MainTexture] = fromMask ? mask : blur },
                Values = new Dictionary<string, NumericsVector4> { ["_BlurRadius4"] = new(offset, offset, 0, 0) }
            });
        }
        draws.Add(passes[0] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [CameraColor] = mask } });
        return draws;
    }

    private static IReadOnlyList<ShaderPreviewDraw> DepthOfFieldPasses(ShaderPreviewDraw[] passes, int size)
    {
        var focus = new ShaderPreviewRenderTarget("Depth of field color and focus", size);
        var downsampled = new ShaderPreviewRenderTarget("Depth of field downsample", size / 2);
        var blur = new ShaderPreviewRenderTarget("Depth of field disc blur", size / 2);
        const float blurWidth = 2f;
        return
        [
            // This pass writes both the original color and the signed circle of confusion to a separate target.
            passes[10] with { Output = focus },
            passes[6] with { Output = downsampled, Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [MainTexture] = focus } },
            passes[17] with
            {
                Output = blur,
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [MainTexture] = downsampled },
                Values = new Dictionary<string, NumericsVector4> { ["_Offsets"] = new(0, blurWidth, 0.1f, blurWidth) }
            },
            passes[18] with
            {
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [MainTexture] = focus, ["_LowRez"] = blur },
                Values = new Dictionary<string, NumericsVector4> { ["_Offsets"] = new(2 * blurWidth) }
            }
        ];
    }

    internal static NumericsVector4 SunPosition(NumericsMatrix view, NumericsMatrix projection, float time)
    {
        float angle = MathF.Tau * time / 4;
        var direction = new NumericsVector4(-0.09f + 0.045f * MathF.Sin(angle), 0.19f, -1, 0);
        var clip = NumericsVector4.Transform(direction, view * projection);
        float distance = MathF.Max(MathF.Abs(clip.W), 0.001f);
        return new NumericsVector4(0.5f + 0.5f * clip.X / distance, 0.5f - 0.5f * clip.Y / distance, clip.W, 0.75f);
    }

    internal static NumericsVector4 DepthOfFieldCurve(NumericsMatrix view) =>
        new(1, 0.05f, 11.5f / 10, -view.M43 / ShaderPreviewCamera.Far);

    internal static ShaderPreviewTexture CreateBokehAperture()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float v = 2f * (y + 0.5f) / size - 1;
            for (int x = 0; x < size; x++)
            {
                float u = 2f * (x + 0.5f) / size - 1;
                byte coverage = (byte)(255 * Math.Clamp((1 - MathF.Sqrt(u * u + v * v)) * size / 2, 0, 1));
                pixels.AsSpan((y * size + x) * 4, 4).Fill(coverage);
            }
        }
        return new ShaderPreviewTexture(size, size, 1, 28, 4, false, pixels, 1, 1, 1, 0);
    }

    private static bool IsFullscreenBlit(LegacyShaderPreviewPass pass) =>
        pass.LightMode.Equals("Always", StringComparison.OrdinalIgnoreCase) && !pass.State.ZWrite &&
        pass.State.ZTest == ShaderPreviewDepth.Always && pass.State.Cull == ShaderPreviewCull.Off &&
        UsesTexture(pass.Fragment, MainTexture);

    private static ImageEffectPass? Identify(LegacyShaderPreviewPass pass)
    {
        if (!IsFullscreenBlit(pass))
        {
            return null;
        }

        if (UsesVector(pass.Fragment, "colorA", 4) && UsesVector(pass.Fragment, "colorB", 4) &&
            UsesVector(pass.Fragment, "colorC", 4) && UsesVector(pass.Fragment, "colorD", 4))
        {
            return ImageEffectPass.LensFlare;
        }
        if (UsesVector(pass.Fragment, "_Threshhold", 4))
        {
            return ImageEffectPass.BrightFilter;
        }
        if (UsesTexture(pass.Fragment, CameraColor) && UsesVector(pass.Fragment, "_Intensity", 1))
        {
            return ImageEffectPass.BloomComposite;
        }
        if (UsesVector(pass.Vertex, "_Offsets", 4) && UsesVector(pass.Vertex, "_StretchWidth", 1))
        {
            return ImageEffectPass.BloomStretch;
        }
        if (UsesVector(pass.Fragment, "_Intensity", 1) && UsesVector(pass.Fragment, "_Blur", 1) &&
            UsesTexture(pass.Fragment, "_VignetteTex"))
        {
            return ImageEffectPass.Vignette;
        }
        if (UsesVector(pass.Fragment, "_ChromaticAberration", 1) && !UsesVector(pass.Fragment, "_AxialAberration", 1))
        {
            return ImageEffectPass.ChromaticAberration;
        }
        if (UsesVector(pass.Vertex, "offsets", 4) && pass.Fragment.Textures.Length == 1)
        {
            return ImageEffectPass.SeparableBlur;
        }
        if (UsesVector(pass.Vertex, "_OffsetScale", 1) && UsesVector(pass.Fragment, "_BlurRadius", 1) &&
            UsesVector(pass.Fragment, "_MainTex_TexelSize", 4))
        {
            return ImageEffectPass.Antialiasing;
        }
        if (UsesTexture(pass.Fragment, BuiltInLightingPreview.CameraDepth) &&
            UsesVector(pass.Fragment, "_HeightParams", 4) && UsesVector(pass.Fragment, "_DistanceParams", 4) &&
            pass.Vertex.Buffers.SelectMany(buffer => buffer.Parameters).Any(parameter =>
                parameter.Name == "_FrustumCornersWS" && parameter.IsMatrix && parameter.Rows == 4 && parameter.Columns == 4))
        {
            return ImageEffectPass.GlobalFog;
        }
        if (pass.Fragment.Textures.Length == 1 && UsesVector(pass.Fragment, "_Channels", 4) && UsesVector(pass.Fragment, "_Levels", 4))
        {
            // The frame debugger chooses its pass by the source render target's dimension. The camera target is 2D.
            return ImageEffectPass.RenderTargetDisplay;
        }
        if (pass.Fragment.Textures.Length == 1 && UsesVector(pass.Fragment, "_MainTex_TexelSize", 4) &&
            UsesVector(pass.Fragment, "RIGHT", 2) && UsesVector(pass.Fragment, "LEFT", 2) &&
            UsesVector(pass.Fragment, "UP", 2) && UsesVector(pass.Fragment, "DOWN", 2))
        {
            return ImageEffectPass.Thicken;
        }
        if (UsesVector(pass.Vertex, "_GrainOffsetScale", 4) && UsesVector(pass.Vertex, "_ScratchOffsetScale", 4) &&
            UsesVector(pass.Fragment, "_Intensity", 4) && UsesTexture(pass.Fragment, NoisePreview.GrainTexture) &&
            UsesTexture(pass.Fragment, NoisePreview.ScratchTexture))
        {
            return ImageEffectPass.Noise;
        }
        return null;
    }

    internal static bool HasAnimatedInput(ImageEffectPass? pass) =>
        pass is ImageEffectPass.BrightFilter or ImageEffectPass.LensFlare or ImageEffectPass.BloomComposite or ImageEffectPass.BloomStretch;

    internal static bool ProvidesTexture(ImageEffectPass? pass, string name) =>
        HasAnimatedInput(pass) && (name == MainTexture || (pass == ImageEffectPass.BloomComposite && name == CameraColor));

    internal static void SetParameters(ImageEffectPass pass, IDictionary<string, NumericsVector4> values, int frameSize)
    {
        // The Unity Standard Assets image effects bind these settings immediately before their blit.
        switch (pass)
        {
            case ImageEffectPass.BrightFilter:
                values.TryAdd("_Threshhold", new NumericsVector4(0.5f));
                break;
            case ImageEffectPass.LensFlare:
                values.TryAdd("colorA", FlareColors[0]);
                values.TryAdd("colorB", FlareColors[1]);
                values.TryAdd("colorC", FlareColors[2]);
                values.TryAdd("colorD", FlareColors[3]);
                break;
            case ImageEffectPass.BloomComposite:
                values.TryAdd("_Intensity", new NumericsVector4(0.5f, 0, 0, 0));
                break;
            case ImageEffectPass.BloomStretch:
                values.TryAdd("_Offsets", new NumericsVector4(1f / (frameSize / 4), 0, 0, 0));
                values.TryAdd("_StretchWidth", new NumericsVector4(2.5f, 0, 0, 0));
                break;
            case ImageEffectPass.Vignette:
                values.TryAdd("_Intensity", new NumericsVector4(0.375f, 0, 0, 0));
                values.TryAdd("_Blur", NumericsVector4.Zero);
                break;
            case ImageEffectPass.ChromaticAberration:
                values.TryAdd("_ChromaticAberration", new NumericsVector4(0.2f, 0, 0, 0));
                break;
            case ImageEffectPass.SeparableBlur:
                values.TryAdd("offsets", new NumericsVector4(0, 0.75f / frameSize, 0, 0));
                break;
            case ImageEffectPass.Antialiasing:
                values.TryAdd("_OffsetScale", new NumericsVector4(0.2f, 0, 0, 0));
                values.TryAdd("_BlurRadius", new NumericsVector4(18f, 0, 0, 0));
                break;
            case ImageEffectPass.GlobalFog:
                const float density = 0.12f; // The preview scene's exponential fog density.
                values.TryAdd("_SceneFogMode", new NumericsVector4(3, 0, 0, 0)); // Exp2, camera depth.
                values.TryAdd("_SceneFogParams", new NumericsVector4(density * 1.2011224f, density * 1.442695f, 0, 0));
                values.TryAdd("_DistanceParams", NumericsVector4.Zero);
                values.TryAdd("unity_FogColor", new NumericsVector4(0.50f, 0.52f, 0.56f, 1));
                break;
            case ImageEffectPass.Overlay:
            case ImageEffectPass.DepthOfField:
            case ImageEffectPass.ReprojectionMotion:
            case ImageEffectPass.ObjectMotion:
            case ImageEffectPass.Noise:
            case ImageEffectPass.SkinDeformation:
                break;
            case ImageEffectPass.SunShafts:
                // The supplied sky color texture stores the sun mask in alpha (useSkyBoxAlpha = 1).
                values.TryAdd("_NoSkyBoxMask", NumericsVector4.Zero);
                break;
            case ImageEffectPass.Bokeh:
                values.TryAdd("_BokehParams", new NumericsVector4(1.2f, 2.5f, 0.5f, 2));
                values.TryAdd("_SpawnHeuristic", new NumericsVector4(0.0875f, 0, 0, 0));
                break;
            case ImageEffectPass.MotionBlur:
                values.TryAdd("_AM_BLUR_STEP", new NumericsVector4(MotionPreview.MaxVelocity / 1000, MotionPreview.MaxVelocity / 1000, 0, 0));
                values.TryAdd("_AM_DEPTH_THRESHOLD", new NumericsVector4(0.001f, 1000, 0, 0));
                break;
            case ImageEffectPass.Thicken:
                // The program applies each direction in units of _MainTex_TexelSize.
                values.TryAdd("RIGHT", NumericsVector4.UnitX);
                values.TryAdd("LEFT", -NumericsVector4.UnitX);
                values.TryAdd("DOWN", NumericsVector4.UnitY);
                values.TryAdd("UP", -NumericsVector4.UnitY);
                break;
            case ImageEffectPass.RenderTargetDisplay:
                values.TryAdd("_Channels", new NumericsVector4(1, 1, 1, 0));
                values.TryAdd("_Levels", new NumericsVector4(0, 1, 0, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pass));
        }
    }

    internal void RenderInputs(ShaderPreviewCamera camera, float time, CancellationToken cancellationToken)
    {
        float phase = time * (MathF.Tau / 4) + camera.Yaw;
        float lightX = 0.27f + 0.08f * MathF.Sin(phase);
        float lightY = 0.37f + 0.1f * MathF.Sin(camera.Pitch * 0.5f + phase);
        bool highlights = _pass != ImageEffectPass.BrightFilter;
        RenderColor(_textures[MainTexture], lightX, lightY, highlights, cancellationToken);
        if (_pass == ImageEffectPass.BloomComposite)
        {
            RenderColor(_textures[CameraColor], lightX, lightY, highlightsOnly: false, cancellationToken);
        }
    }

    private static void RenderColor(ShaderPreviewTexture texture, float lightX, float lightY, bool highlightsOnly, CancellationToken token)
    {
        var pixels = MemoryMarshal.Cast<byte, ushort>(texture.Pixels.AsSpan());
        var lampColor = new NumericsVector3(1, 0.76f, 0.54f);
        var skyColor = new NumericsVector3(0.038f, 0.06f, 0.11f);
        float radius = highlightsOnly ? 0.062f : 0.046f;
        float power = highlightsOnly ? 0.95f : 2.25f;
        for (int y = 0; y < texture.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            float v = (y + 0.5f) / texture.Height;
            float dy = v - lightY;
            for (int x = 0; x < texture.Width; x++)
            {
                float u = (x + 0.5f) / texture.Width;
                float dx = u - lightX;
                float light = power * MathF.Exp(-(dx * dx + dy * dy) / (radius * radius));
                var color = light * lampColor;
                if (!highlightsOnly)
                {
                    color += (0.5f + 0.5f * (1 - v)) * skyColor;
                }
                int offset = (y * texture.Width + x) * 4;
                pixels[offset] = BitConverter.HalfToUInt16Bits((Half)color.X);
                pixels[offset + 1] = BitConverter.HalfToUInt16Bits((Half)color.Y);
                pixels[offset + 2] = BitConverter.HalfToUInt16Bits((Half)color.Z);
                pixels[offset + 3] = BitConverter.HalfToUInt16Bits((Half)1);
            }
        }
    }

    private static ShaderPreviewTexture CreateTexture(int size) =>
        new(size, size, 1, 10 /* R16G16B16A16_FLOAT */, 8, false, new byte[size * size * 8], 1, 1, 1, 0);

    private static bool UsesTexture(ShaderPreviewStage stage, string name) =>
        Array.Exists(stage.Textures, texture => texture.Name == name && texture.Dimension == ShaderPreviewTextureDimension.Texture2D);

    private static bool UsesVector(ShaderPreviewStage stage, string name, int columns) =>
        stage.Buffers.SelectMany(buffer => buffer.Parameters).Any(parameter =>
            parameter.Name == name && parameter.Type == 0 && !parameter.IsMatrix &&
            parameter.Rows == 1 && parameter.Columns == columns && parameter.ArraySize == 1);

    private static bool UsesMatrix(ShaderPreviewStage stage, string name) =>
        stage.Buffers.SelectMany(buffer => buffer.Parameters).Any(parameter =>
            parameter.Name == name && parameter.Type == 0 && parameter.IsMatrix &&
            parameter.Rows == 4 && parameter.Columns == 4 && parameter.ArraySize == 1);
}
