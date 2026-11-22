using System.Collections.Generic;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class ScreenSpaceReflectionPreview
{
    internal const string CameraReflections = "_CameraReflectionsTexture";

    internal static ShaderPreviewDraw[] Prepare(ShaderPreviewDraw[] passes, IDictionary<string, NumericsVector4> values, int size)
    {
        // The Post Processing Stack v1 default uses five reflection levels at half resolution.
        int resolution = size / 2;
        values.TryAdd("_ReflectionBufferSize", new NumericsVector4(resolution, resolution, 0, 0));
        values.TryAdd("_ScreenSize", new NumericsVector4(size, size, 0, 0));
        values.TryAdd("_RayStepSize", new NumericsVector4(3));
        values.TryAdd("_LayerThickness", new NumericsVector4(0.5f));
        values.TryAdd("_ScreenEdgeFading", new NumericsVector4(0.03f));
        values.TryAdd("_MaxRayTraceDistance", new NumericsVector4(100));
        values.TryAdd("_FadeDistance", new NumericsVector4(100));
        values.TryAdd("_MaxSteps", new NumericsVector4(256));
        foreach (string name in new[] { "_TraceBehindObjects", "_BilateralUpsampling", "_HalfResolution", "_SSRMultiplier", "_FresnelFade", "_FresnelFadePower", "_ReflectionBlur" })
        {
            values.TryAdd(name, NumericsVector4.One);
        }
        foreach (string name in new[] { "_TreatBackfaceHitAsMiss", "_AllowBackwardsRays", "_AdditiveReflection", "_HighlightSuppression" })
        {
            values.TryAdd(name, NumericsVector4.Zero);
        }

        var normalAndRoughness = new ShaderPreviewRenderTarget("Reflection normals and roughness", size, Filter: 0);
        var hit = new ShaderPreviewRenderTarget("Reflection ray hit", resolution);
        var filtered = new ShaderPreviewRenderTarget("Reflection color at hit", resolution, Filter: 0);
        var resolved = new ShaderPreviewRenderTarget("Resolved reflection", resolution, Filter: 0);
        var levels = new ShaderPreviewRenderTarget[5];
        levels[0] = new ShaderPreviewRenderTarget("Reflection level 0", resolution);
        var draws = new List<ShaderPreviewDraw>
        {
            passes[5] with { Output = normalAndRoughness },
            passes[0] with { Output = hit },
            passes[4] with { Output = filtered, Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_HitPointTexture"] = hit } },
            SerializedCameraShaderPreview.WithInput(passes[6], filtered) with { Output = levels[0] }
        };
        for (int level = 1; level < levels.Length; level++)
        {
            int width = resolution >> level;
            var horizontal = new ShaderPreviewRenderTarget($"Reflection horizontal blur {level}", width);
            levels[level] = new ShaderPreviewRenderTarget($"Reflection level {level}", width);
            draws.Add(Blur(levels[level - 1], horizontal, NumericsVector4.UnitX));
            draws.Add(Blur(horizontal, levels[level], NumericsVector4.UnitY));
        }
        var resolveInputs = new Dictionary<string, ShaderPreviewRenderTarget>
        {
            ["_NormalAndRoughnessTexture"] = normalAndRoughness,
            ["_HitPointTexture"] = hit
        };
        for (int level = 0; level < levels.Length; level++)
        {
            resolveInputs.Add("_ReflectionTexture" + level, levels[level]);
        }
        draws.Add(passes[3] with { Output = resolved, Inputs = resolveInputs });
        draws.Add(passes[1] with { Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { ["_FinalReflectionTexture"] = resolved } });
        return draws.ToArray();

        ShaderPreviewDraw Blur(ShaderPreviewRenderTarget source, ShaderPreviewRenderTarget target, NumericsVector4 axis) =>
            passes[2] with
            {
                Output = target,
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget>
                {
                    [ImageEffectPreview.MainTexture] = source,
                    ["_NormalAndRoughnessTexture"] = normalAndRoughness
                },
                Values = new Dictionary<string, NumericsVector4> { ["_Axis"] = axis }
            };
    }

    internal static NumericsMatrix ProjectToPixel(NumericsMatrix projection, int size) =>
        projection * NumericsMatrix.CreateScale(size / 2f, size / 2f, 1) * NumericsMatrix.CreateTranslation(size / 2f, size / 2f, 0);

    internal static NumericsVector4 ProjectionInfo(NumericsMatrix projection, int size) =>
        new(-2 / (size * projection.M11), -2 / (size * projection.M22), 1 / projection.M11, 1 / projection.M22);
}
