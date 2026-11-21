using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal enum BuiltInLightingPass { None, CollectShadows, DeferredLighting }

internal sealed class BuiltInLightingPreview
{
    internal const string CameraDepth = "_CameraDepthTexture";
    internal const string ShadowMap = "_ShadowMapTexture";
    private const string Diffuse = "_CameraGBufferTexture0";
    private const string Specular = "_CameraGBufferTexture1";
    private const string Normal = "_CameraGBufferTexture2";
    private const float SphereRadius = 0.82f;
    private const float FloorHeight = -0.86f;
    private const float ShadowBias = 0.002f;
    private const float ShadowWidth = 6, ShadowNear = 0.1f, ShadowFar = 16;

    private readonly Dictionary<string, ShaderPreviewTexture> _textures = new(StringComparer.Ordinal);
    private readonly BuiltInLightingPass _pass;
    private readonly NumericsMatrix _shadowProjection;
    private readonly NumericsVector3 _lightDirection;
    private bool _hasShadowMap;

    internal BuiltInLightingPreview(BuiltInLightingPass pass, ShaderPreviewLight light, int size)
    {
        _pass = pass;
        _lightDirection = light.Direction;
        _textures.Add(CameraDepth, NewTexture(size, 41 /* R32_FLOAT */));
        if (pass == BuiltInLightingPass.CollectShadows)
        {
            _textures.Add(ShadowMap, NewTexture(size * 2, 41, comparison: true));
            var lightUp = MathF.Abs(light.Direction.Y) < 0.99f ? NumericsVector3.UnitY : NumericsVector3.UnitZ;
            _shadowProjection = NumericsMatrix.CreateLookAt(light.Direction * 6, NumericsVector3.Zero, lightUp) *
                NumericsMatrix.CreateOrthographic(ShadowWidth, ShadowWidth, ShadowNear, ShadowFar);
            WorldToShadow = _shadowProjection * NumericsMatrix.CreateScale(0.5f, -0.5f, 1) * NumericsMatrix.CreateTranslation(0.5f, 0.5f, 0);
        }
        else
        {
            _textures.Add(Diffuse, NewTexture(size, 28 /* R8G8B8A8_UNORM */));
            _textures.Add(Specular, NewTexture(size, 28));
            _textures.Add(Normal, NewTexture(size, 28));
        }
    }

    internal IReadOnlyDictionary<string, ShaderPreviewTexture> Textures => _textures;
    internal NumericsMatrix WorldToShadow { get; }

    internal static bool ProvidesTexture(BuiltInLightingPass pass, string name) => pass switch
    {
        BuiltInLightingPass.CollectShadows => name is CameraDepth or ShadowMap,
        BuiltInLightingPass.DeferredLighting => name is CameraDepth or Diffuse or Specular or Normal,
        _ => false
    };

    internal void RenderInputs(NumericsMatrix view, NumericsMatrix projection, NumericsVector3 eye, CancellationToken token)
    {
        var depth = _textures[CameraDepth];
        var depths = MemoryMarshal.Cast<byte, float>(depth.Pixels.AsSpan());
        var viewToWorld = NumericsMatrix.Transpose(view); // The upper 3×3 contains only the camera rotation.
        var worldToClip = view * projection;
        bool deferred = _pass == BuiltInLightingPass.DeferredLighting;
        var diffuse = deferred ? _textures[Diffuse].Pixels.AsSpan() : Span<byte>.Empty;
        var specular = deferred ? _textures[Specular].Pixels.AsSpan() : Span<byte>.Empty;
        var normals = deferred ? _textures[Normal].Pixels.AsSpan() : Span<byte>.Empty;

        for (int y = 0; y < depth.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            float rayY = ClipY(y, depth.Height) / projection.M22;
            for (int x = 0; x < depth.Width; x++)
            {
                float rayX = ClipX(x, depth.Width) / projection.M11;
                var ray = NumericsVector3.TransformNormal(new NumericsVector3(rayX, rayY, -1), viewToWorld);
                int index = y * depth.Width + x;
                bool hit = TryHit(eye, ray, out var point, out bool sphere);
                depths[index] = hit ? ProjectDepth(point, worldToClip) : 1;
                if (deferred)
                {
                    int offset = index * 4;
                    if (hit && depths[index] < 1)
                    {
                        byte floor = ((int)MathF.Floor(point.X) + (int)MathF.Floor(point.Z)) % 2 == 0 ? (byte)78 : (byte)104;
                        WriteRgba(diffuse[offset..], sphere ? (byte)182 : floor, sphere ? (byte)96 : (byte)(floor + 7), sphere ? (byte)50 : (byte)(floor + 16), 255);
                        byte reflectance = sphere ? (byte)52 : (byte)9;
                        WriteRgba(specular[offset..], reflectance, reflectance, reflectance, sphere ? (byte)160 : (byte)42);
                    }
                    else
                    {
                        diffuse.Slice(offset, 4).Clear();
                        specular.Slice(offset, 4).Clear();
                    }
                    var normal = hit ? sphere ? NumericsVector3.Normalize(point) : NumericsVector3.UnitY : -_lightDirection;
                    WriteRgba(normals[offset..], EncodeNormal(normal.X), EncodeNormal(normal.Y), EncodeNormal(normal.Z), 255);
                }
            }
        }

        if (_pass == BuiltInLightingPass.CollectShadows && !_hasShadowMap)
        {
            RenderShadowMap(token);
            _hasShadowMap = true;
        }
    }

    private void RenderShadowMap(CancellationToken token)
    {
        var shadow = _textures[ShadowMap];
        var depths = MemoryMarshal.Cast<byte, float>(shadow.Pixels.AsSpan());
        float texelSlope = ShadowWidth / (shadow.Width * (ShadowFar - ShadowNear));
        if (!NumericsMatrix.Invert(_shadowProjection, out var clipToWorld))
        {
            throw new InvalidOperationException("The preview light projection is singular.");
        }
        for (int y = 0; y < shadow.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            float clipY = ClipY(y, shadow.Height);
            for (int x = 0; x < shadow.Width; x++)
            {
                var origin = NumericsVector3.Transform(new NumericsVector3(ClipX(x, shadow.Width), clipY, 0), clipToWorld);
                float depth = 1;
                if (TryHit(origin, -_lightDirection, out var point, out bool sphere))
                {
                    var normal = sphere ? NumericsVector3.Normalize(point) : NumericsVector3.UnitY;
                    float facing = Math.Clamp(NumericsVector3.Dot(normal, _lightDirection), 0.05f, 1);
                    float slope = MathF.Sqrt(1 - facing * facing) / facing;
                    // Bias depth by the change across the comparison sampler's footprint.
                    depth = Math.Min(1, ProjectDepth(point, _shadowProjection) + ShadowBias + Math.Min(0.012f, 2 * texelSlope * slope));
                }
                depths[y * shadow.Width + x] = depth;
            }
        }
    }

    private static bool TryHit(NumericsVector3 origin, NumericsVector3 ray, out NumericsVector3 point, out bool sphere)
    {
        float rayLength = NumericsVector3.Dot(ray, ray);
        float alongRay = NumericsVector3.Dot(origin, ray);
        float radiusTerm = NumericsVector3.Dot(origin, origin) - SphereRadius * SphereRadius;
        float discriminant = alongRay * alongRay - rayLength * radiusTerm;
        float sphereDistance = discriminant >= 0 ? (-alongRay - MathF.Sqrt(discriminant)) / rayLength : float.PositiveInfinity;
        if (sphereDistance <= 0)
        {
            sphereDistance = float.PositiveInfinity;
        }
        float floorDistance = ray.Y < -1e-5f && origin.Y > FloorHeight ? (FloorHeight - origin.Y) / ray.Y : float.PositiveInfinity;
        sphere = sphereDistance < floorDistance;
        float distance = Math.Min(sphereDistance, floorDistance);
        point = float.IsFinite(distance) ? origin + distance * ray : default;
        return float.IsFinite(distance);
    }

    private static ShaderPreviewTexture NewTexture(int size, int format, bool comparison = false) =>
        new(size, size, 1, format, 4, false, new byte[size * size * 4], comparison ? 1 : 0, 1, 1, 0, Comparison: comparison);

    private static float ProjectDepth(NumericsVector3 point, NumericsMatrix worldToClip)
    {
        var clip = NumericsVector4.Transform(new NumericsVector4(point, 1), worldToClip);
        return Math.Clamp(clip.Z / clip.W, 0, 1);
    }

    private static float ClipX(int x, int width) => 2 * (x + 0.5f) / width - 1;
    private static float ClipY(int y, int height) => 1 - 2 * (y + 0.5f) / height;
    private static byte EncodeNormal(float component) => (byte)Math.Clamp(MathF.Round((component * 0.5f + 0.5f) * 255), 0, 255);

    private static void WriteRgba(Span<byte> pixel, byte r, byte g, byte b, byte a)
    {
        pixel[0] = r;
        pixel[1] = g;
        pixel[2] = b;
        pixel[3] = a;
    }
}
