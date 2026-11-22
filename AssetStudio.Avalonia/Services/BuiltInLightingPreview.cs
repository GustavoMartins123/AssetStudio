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
    private const string CameraNormals = "_CameraNormalsTexture";
    private const string Diffuse = "_CameraGBufferTexture0";
    private const string Specular = "_CameraGBufferTexture1";
    private const string Normal = "_CameraGBufferTexture2";
    private const float SphereRadius = 0.82f;
    private const float FloorHeight = -0.86f;
    private const float ShadowBias = 0.002f;
    private const float ShadowWidth = 6, ShadowNear = 0.1f, ShadowFar = 16;

    private readonly Dictionary<string, ShaderPreviewTexture> _textures = new(StringComparer.Ordinal);
    private readonly int _size;
    private readonly ImageEffectPass? _effect;
    private readonly NumericsMatrix _shadowProjection;
    private readonly NumericsVector3 _lightDirection;
    private bool _hasShadowMap;

    internal BuiltInLightingPreview(IReadOnlySet<string> inputs, ShaderPreviewLight light, int size, ImageEffectPass? effect)
    {
        _size = size;
        _effect = effect;
        _lightDirection = light.Direction;
        if (effect == ImageEffectPass.SkinDeformation)
        {
            _textures = MotionPreview.CreateSkinTextures();
            return;
        }
        foreach (string input in inputs)
        {
            _textures.Add(input, input switch
            {
                CameraDepth => NewTexture(size, 41 /* R32_FLOAT */),
                ShadowMap => NewTexture(size * 2, 41, comparison: true),
                CameraNormals or Diffuse or Specular or Normal => NewTexture(size, 28 /* R8G8B8A8_UNORM */),
                MotionPreview.MotionTexture when effect == ImageEffectPass.MotionBlur => NewTexture(size, 28),
                ImageEffectPreview.MainTexture when effect != ImageEffectPass.Bokeh => NewTexture(size, 28) with { Filter = 1 },
                ImageEffectPreview.MainTexture or ImageEffectPreview.BokehBlurredColor when effect == ImageEffectPass.Bokeh =>
                    new ShaderPreviewTexture(size, size, 1, 10, 8, false, new byte[size * size * 8], 1, 1, 1, 0),
                _ => throw new ArgumentException($"Unknown Built-in camera texture '{input}'.", nameof(inputs))
            });
        }
        if (inputs.Contains(ShadowMap))
        {
            var lightUp = MathF.Abs(light.Direction.Y) < 0.99f ? NumericsVector3.UnitY : NumericsVector3.UnitZ;
            _shadowProjection = NumericsMatrix.CreateLookAt(light.Direction * 6, NumericsVector3.Zero, lightUp) *
                NumericsMatrix.CreateOrthographic(ShadowWidth, ShadowWidth, ShadowNear, ShadowFar);
            WorldToShadow = _shadowProjection * NumericsMatrix.CreateScale(0.5f, -0.5f, 1) * NumericsMatrix.CreateTranslation(0.5f, 0.5f, 0);
        }
    }

    internal IReadOnlyDictionary<string, ShaderPreviewTexture> Textures => _textures;
    internal NumericsMatrix WorldToShadow { get; }

    internal static bool IsSurfaceTexture(string name) => name is CameraNormals or Diffuse or Specular or Normal;

    internal void RenderInputs(NumericsMatrix view, NumericsMatrix projection, NumericsVector3 eye, float time,
        MotionPreviewFrame? motion, CancellationToken token)
    {
        if (_effect == ImageEffectPass.SkinDeformation)
        {
            MotionPreview.UpdateBoneTexture(_textures[MotionPreview.BoneTexture], time);
            return;
        }
        var depths = MemoryMarshal.Cast<byte, float>(GetPixels(CameraDepth));
        var viewToWorld = NumericsMatrix.Transpose(view); // The upper 3×3 contains only the camera rotation.
        var worldToClip = view * projection;
        var diffuse = GetPixels(Diffuse);
        var specular = GetPixels(Specular);
        var normals = GetPixels(Normal);
        var cameraNormals = GetPixels(CameraNormals);
        var color = GetPixels(ImageEffectPreview.MainTexture);
        var motionVectors = GetPixels(MotionPreview.MotionTexture);
        bool motionBlur = _effect == ImageEffectPass.MotionBlur;
        if (motionBlur && !motion.HasValue)
        {
            throw new InvalidOperationException("The motion blur preview needs a camera and mesh pose for the current frame.");
        }
        var sphereCenter = motionBlur ? motion!.Value.Model.Translation : NumericsVector3.Zero;
        var sphereScale = motionBlur ? motion!.Value.Deformation : NumericsVector3.One;
        bool needsNormal = !normals.IsEmpty || !cameraNormals.IsEmpty || !color.IsEmpty;
        bool needsAlbedo = !diffuse.IsEmpty || !color.IsEmpty;
        var sun = _effect == ImageEffectPass.SunShafts ? ImageEffectPreview.SunPosition(view, projection, time) : default;
        bool bokeh = _effect == ImageEffectPass.Bokeh;
        var curve = bokeh ? ImageEffectPreview.DepthOfFieldCurve(view) : default;
        var hdr = bokeh ? MemoryMarshal.Cast<byte, Half>(color) : Span<Half>.Empty;

        for (int y = 0; y < _size; y++)
        {
            token.ThrowIfCancellationRequested();
            float rayY = ClipY(y, _size) / projection.M22;
            for (int x = 0; x < _size; x++)
            {
                float rayX = ClipX(x, _size) / projection.M11;
                var ray = NumericsVector3.TransformNormal(new NumericsVector3(rayX, rayY, -1), viewToWorld);
                int index = y * _size + x;
                int offset = index * 4;
                bool hit = TryHit(eye, ray, sphereCenter, sphereScale, out var point, out bool sphere);
                float depth = hit ? ProjectDepth(point, worldToClip) : 1;
                bool visible = depth < 1;
                byte objectId = sphere && visible ? (byte)1 : (byte)0;
                if (!depths.IsEmpty)
                {
                    depths[index] = depth;
                }
                if (!motionVectors.IsEmpty && motion is { } motionFrame)
                {
                    var clip = new NumericsVector4(ClipX(x, _size), -ClipY(y, _size), depth, 1);
                    NumericsVector4 oldClip;
                    if (objectId != 0)
                    {
                        var previousVertex = (point - sphereCenter) / sphereScale * motionFrame.PreviousDeformation;
                        oldClip = NumericsVector4.Transform(new NumericsVector4(previousVertex, 1), motionFrame.PreviousModelViewProjection);
                        oldClip.Y = -oldClip.Y;
                    }
                    else
                    {
                        oldClip = NumericsVector4.Transform(clip, motionFrame.Reprojection);
                    }
                    float dx = (clip.X - oldClip.X / oldClip.W) * motionFrame.Scale;
                    float dy = (clip.Y - oldClip.Y / oldClip.W) * motionFrame.Scale;
                    float speed = MathF.Sqrt(dx * dx + dy * dy);
                    float velocity = Math.Clamp((speed - MotionPreview.MinVelocity) / (MotionPreview.MaxVelocity - MotionPreview.MinVelocity), 0, 1);
                    WriteRgba(motionVectors[offset..], EncodeNormal(speed == 0 ? 0 : dx / speed),
                        EncodeNormal(speed == 0 ? 0 : dy / speed), (byte)MathF.Round(velocity * 255), objectId);
                }
                byte gloss = sphere ? (byte)160 : (byte)42;
                var normal = -_lightDirection;
                if (hit && needsNormal)
                {
                    normal = sphere ? NumericsVector3.Normalize((point - sphereCenter) / (sphereScale * sphereScale)) : NumericsVector3.UnitY;
                }
                byte r = 0, g = 0, b = 0;
                if (visible && needsAlbedo)
                {
                    byte floor = ((int)MathF.Floor(point.X) + (int)MathF.Floor(point.Z)) % 2 == 0 ? (byte)78 : (byte)104;
                    r = sphere ? (byte)182 : floor;
                    g = sphere ? (byte)96 : (byte)(floor + 7);
                    b = sphere ? (byte)50 : (byte)(floor + 16);
                }
                if (!diffuse.IsEmpty)
                {
                    WriteRgba(diffuse[offset..], r, g, b, visible ? (byte)255 : (byte)0);
                }
                if (!specular.IsEmpty)
                {
                    byte reflectance = sphere ? (byte)52 : (byte)9;
                    WriteRgba(specular[offset..], visible ? reflectance : (byte)0, visible ? reflectance : (byte)0,
                        visible ? reflectance : (byte)0, visible ? gloss : (byte)0);
                }
                if (!normals.IsEmpty)
                {
                    WriteRgba(normals[offset..], EncodeNormal(normal.X), EncodeNormal(normal.Y), EncodeNormal(normal.Z), 255);
                }
                if (!cameraNormals.IsEmpty)
                {
                    WriteRgba(cameraNormals[offset..], EncodeNormal(normal.X), EncodeNormal(normal.Y), EncodeNormal(normal.Z),
                        visible ? gloss : (byte)0);
                }
                if (bokeh)
                {
                    var rgb = new NumericsVector3(0.12f, 0.16f, 0.22f);
                    if (visible)
                    {
                        float lambert = 0.35f + 0.65f * Math.Max(0, NumericsVector3.Dot(normal, _lightDirection));
                        rgb = new NumericsVector3(r, g, b) * (lambert / 255);
                        if (!sphere)
                        {
                            rgb += FloorHighlight(point, 1.65f, -4.3f) * new NumericsVector3(4, 3.1f, 2.4f);
                            rgb += FloorHighlight(point, -1.75f, -4.8f) * new NumericsVector3(1.8f, 3.1f, 4.5f);
                        }
                    }
                    const float ratio = ShaderPreviewCamera.Far / ShaderPreviewCamera.Near;
                    float linearDepth = 1 / ((1 - ratio) * depth + ratio);
                    float distance = linearDepth - curve.W;
                    float coc = MathF.CopySign(Math.Clamp(MathF.Abs(distance) * curve.Z / (linearDepth + 0.00001f) - curve.Y, 0, curve.X), distance);
                    hdr[offset] = (Half)rgb.X;
                    hdr[offset + 1] = (Half)rgb.Y;
                    hdr[offset + 2] = (Half)rgb.Z;
                    hdr[offset + 3] = (Half)coc;
                }
                else if (!color.IsEmpty)
                {
                    if (visible)
                    {
                        float lighting = 0.35f + 0.65f * Math.Max(0, NumericsVector3.Dot(normal, _lightDirection));
                        WriteRgba(color[offset..], (byte)(r * lighting), (byte)(g * lighting), (byte)(b * lighting),
                            motionBlur ? objectId : (byte)255);
                    }
                    else
                    {
                        byte sky = (byte)(24 + 32 * (1 - (float)y / _size));
                        if (_effect == ImageEffectPass.SunShafts)
                        {
                            float dx = (x + 0.5f) / _size - sun.X;
                            float dy = (y + 0.5f) / _size - sun.Y;
                            float glow = sun.Z > 0 ? MathF.Exp(-(dx * dx + dy * dy) / (0.045f * 0.045f)) : 0;
                            WriteRgba(color[offset..], (byte)(sky + (255 - sky) * glow),
                                (byte)(sky + 12 + (234 - sky - 12) * glow),
                                (byte)(sky + 28 + (191 - sky - 28) * glow), (byte)(255 * glow));
                        }
                        else
                        {
                            WriteRgba(color[offset..], sky, (byte)(sky + 12), (byte)(sky + 28),
                                motionBlur ? (byte)0 : (byte)255);
                        }
                    }
                }
            }
        }

        if (bokeh)
        {
            FilterBokehSource(hdr, MemoryMarshal.Cast<byte, Half>(GetPixels(ImageEffectPreview.BokehBlurredColor)), token);
        }

        if (_textures.ContainsKey(ShadowMap) && !_hasShadowMap)
        {
            RenderShadowMap(token);
            _hasShadowMap = true;
        }
    }

    private static float FloorHighlight(NumericsVector3 point, float x, float z)
    {
        const float radius = 0.035f;
        float dx = point.X - x, dz = point.Z - z;
        return Math.Clamp(1 - (dx * dx + dz * dz) / (radius * radius), 0, 1);
    }

    private void FilterBokehSource(ReadOnlySpan<Half> source, Span<Half> target, CancellationToken token)
    {
        for (int y = 0; y < _size; y++)
        {
            token.ThrowIfCancellationRequested();
            int up = Math.Max(y - 2, 0) * _size, down = Math.Min(y + 2, _size - 1) * _size;
            for (int x = 0; x < _size; x++)
            {
                int index = y * _size + x;
                int left = y * _size + Math.Max(x - 2, 0), right = y * _size + Math.Min(x + 2, _size - 1);
                for (int component = 0; component < 4; component++)
                {
                    target[index * 4 + component] = (Half)(((float)source[index * 4 + component] +
                        (float)source[left * 4 + component] + (float)source[right * 4 + component] +
                        (float)source[(up + x) * 4 + component] + (float)source[(down + x) * 4 + component]) / 5);
                }
            }
        }
    }

    private Span<byte> GetPixels(string name) => _textures.TryGetValue(name, out var texture) ? texture.Pixels.AsSpan() : Span<byte>.Empty;

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
                if (TryHit(origin, -_lightDirection, NumericsVector3.Zero, NumericsVector3.One, out var point, out bool sphere))
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

    private static bool TryHit(NumericsVector3 origin, NumericsVector3 ray, NumericsVector3 sphereCenter,
        NumericsVector3 sphereScale, out NumericsVector3 point, out bool sphere)
    {
        var localOrigin = (origin - sphereCenter) / sphereScale;
        var localRay = ray / sphereScale;
        float rayLength = NumericsVector3.Dot(localRay, localRay);
        float alongRay = NumericsVector3.Dot(localOrigin, localRay);
        float radiusTerm = NumericsVector3.Dot(localOrigin, localOrigin) - SphereRadius * SphereRadius;
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
