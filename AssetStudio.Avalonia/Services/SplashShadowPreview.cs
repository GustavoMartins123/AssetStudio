using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class SplashShadowPreview
{
    private const string Caster = "Hidden/InternalSplashShadowCaster";
    private const string Blur = "Hidden/InternalSplashShadowBlur";
    private const string Receiver = "Hidden/InternalSplashShadowReceiver";
    private const string SimpleReceiver = "Hidden/InternalSplashShadowReceiverSimple";
    private const string MainTexture = "_MainTex";
    private const string ShadowTexture = "unity_SplashScreenShadowTex";
    private static readonly NumericsVector3 LightPosition = new(-0.35f, 0.45f, 4f);
    private static readonly NumericsMatrix LightProjection = NumericsMatrix.CreateLookAt(LightPosition, NumericsVector3.Zero, NumericsVector3.UnitY) *
        NumericsMatrix.CreatePerspectiveFieldOfView(MathF.PI / 3, 1, 0.1f, 16);

    internal static bool IsShadowEffect(string name) => name is Blur or Receiver or SimpleReceiver;

    internal static IReadOnlyList<ShaderPreviewDraw> Prepare(Shader shader, string name, ShaderPreviewDraw[] selected,
        ShaderPreviewLight light, int size, CancellationToken token)
    {
        ShaderPreviewDraw caster = ReadPasses(shader, Caster, light, token).Single();
        ShaderPreviewDraw[] blur = name == Blur ? selected : ReadPasses(shader, Blur, light, token);
        if (blur.Length != 2 || blur.Any(draw => !draw.Pass.Fragment.Textures.Any(texture => texture.Name == MainTexture)))
        {
            throw new NotSupportedException("The splash shadow blur needs a horizontal and a vertical pass.");
        }

        var draws = new List<ShaderPreviewDraw>();
        if (name == Blur)
        {
            var capture = new ShaderPreviewRenderTarget("Splash caster", size / 2);
            var horizontal = new ShaderPreviewRenderTarget("Horizontal splash blur", size / 2);
            draws.Add(Capture(capture, new NumericsVector3(0, 0, 0.55f), 0.65f, new NumericsVector4(0.45f)));
            draws.Add(Blit(blur[0], capture, horizontal));
            draws.Add(Blit(blur[1], horizontal, null));
            return draws;
        }

        NumericsVector3[] centers = name == Receiver
            ? [new(-0.40f, 0.28f, 0.37f), new(0.12f, -0.37f, 0.82f), new(0.34f, 0.24f, 1.16f)]
            : [new(0, 0, 0.55f)];
        int levels = name == Receiver ? 3 : 2;
        var inputs = new Dictionary<string, ShaderPreviewRenderTarget>(StringComparer.Ordinal);
        var matrices = new Dictionary<string, NumericsMatrix>(StringComparer.Ordinal)
        {
            ["unity_World2Shadow0"] = LightProjection * NumericsMatrix.CreateScale(1, -1, 1)
        };
        for (int group = 0; group < 3; group++)
        {
            // Each set of textures packs four casters into RGBA. The preview scene uses its red channel.
            var distances = new NumericsMatrix();
            if (group < centers.Length)
            {
                var center = centers[group];
                distances.M31 = -5;
                distances.M41 = 5 * center.Z;
                var source = new ShaderPreviewRenderTarget($"Splash shadow {group + 1}", size / 2);
                draws.Add(Capture(source, center, name == Receiver ? 0.24f : 0.48f, new NumericsVector4(0.48f, 0, 0, 0)));
                inputs.Add(ShadowTexture + (group * levels), source);
                for (int level = 1; level < levels; level++)
                {
                    int resolution = size >> (level + 2);
                    var horizontal = new ShaderPreviewRenderTarget($"Splash shadow {group + 1} horizontal {level}", resolution);
                    var blurred = new ShaderPreviewRenderTarget($"Splash shadow {group + 1} blur {level}", resolution);
                    draws.Add(Blit(blur[0], source, horizontal));
                    draws.Add(Blit(blur[1], horizontal, blurred));
                    inputs.Add(ShadowTexture + (group * levels + level), blurred);
                    source = blurred;
                }
            }
            matrices.Add($"unity_World2Shadow{group + 1}", distances);
        }

        draws.Add(selected.Single() with
        {
            Geometry = ShaderPreviewGeometry.Plane,
            Inputs = inputs,
            Matrices = matrices,
            Values = new Dictionary<string, NumericsVector4>(StringComparer.Ordinal)
            {
                ["unity_LightPosition0"] = new(LightPosition, 1),
                ["unity_LightColor0"] = new(0.86f, 0.80f, 0.67f, 1),
                ["unity_LightColor1"] = new(0.15f, 0.17f, 0.22f, 1)
            }
        });
        return draws;

        ShaderPreviewDraw Capture(ShaderPreviewRenderTarget target, NumericsVector3 center, float radius, NumericsVector4 color) =>
            caster with
            {
                Geometry = ShaderPreviewGeometry.Sphere,
                Output = target,
                VertexColor = color,
                Matrices = new Dictionary<string, NumericsMatrix>
                {
                    ["glstate_matrix_mvp"] = NumericsMatrix.CreateScale(radius) * NumericsMatrix.CreateTranslation(center) * LightProjection
                }
            };

        static ShaderPreviewDraw Blit(ShaderPreviewDraw pass, ShaderPreviewRenderTarget source, ShaderPreviewRenderTarget? output) =>
            pass with
            {
                Geometry = ShaderPreviewGeometry.ScreenQuad,
                Inputs = new Dictionary<string, ShaderPreviewRenderTarget> { [MainTexture] = source },
                Output = output
            };
    }

    private static ShaderPreviewDraw[] ReadPasses(Shader selected, string name, ShaderPreviewLight light, CancellationToken token)
    {
        // These engine shaders are sibling assets in the Built-in resource file.
        var source = selected.assetsFile;
        using var file = source.reader.Clone();
        Shader? match = null;
        foreach (var info in source.m_Objects)
        {
            if (info.classID != (int)ClassIDType.Shader)
            {
                continue;
            }
            token.ThrowIfCancellationRequested();
            var candidate = new Shader(new ObjectReader(file, source, info));
            if (!LegacyShaderPreview.ReadName(candidate).Equals(name, StringComparison.Ordinal))
            {
                continue;
            }
            if (match != null)
            {
                throw new InvalidDataException($"The Built-in resource file contains multiple shaders named '{name}'.");
            }
            match = candidate;
        }
        if (match == null)
        {
            throw new InvalidDataException($"The Built-in resource file does not contain '{name}'.");
        }

        var shader = LegacyShaderPreview.Read(match);
        var subShader = shader.SubShaders.FirstOrDefault(sub => sub.Passes.Any(pass => pass.HasDirect3D11))
            ?? throw new NotSupportedException($"'{name}' has no D3D11 splash shadow pass.");
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        return subShader.Passes.Where(pass => pass.HasDirect3D11)
            .Select(pass => new ShaderPreviewDraw(shader.PreparePass(subShader, pass, keywords, shader.Values), light))
            .ToArray();
    }
}
