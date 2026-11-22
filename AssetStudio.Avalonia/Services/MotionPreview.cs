using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal readonly record struct MotionPreviewFrame(NumericsMatrix Model, NumericsMatrix PreviousModelViewProjection,
    NumericsMatrix Reprojection, NumericsVector3 Deformation, NumericsVector3 PreviousDeformation, float Scale);

internal static class MotionPreview
{
    internal const string MotionTexture = "_MotionTex";
    internal const string BoneTexture = "_AM_BONE_TEX";
    internal const string BoneIndices = "_AM_BONE_INDEX_TEX";
    internal const string BaseVertices = "_AM_BASE_VERTEX0_TEX";
    internal const float MinVelocity = 1, MaxVelocity = 10;
    internal const float MotionScale = 3;
    internal const float FrameInterval = 1f / ShaderPreviewService.FramesPerSecond;

    internal static bool UsesHistory(ImageEffectPass? effect) =>
        effect is ImageEffectPass.ReprojectionMotion or ImageEffectPass.ObjectMotion or ImageEffectPass.MotionBlur;

    internal static bool IsSkinTexture(string name) => name is BoneTexture or BoneIndices or BaseVertices;

    internal static Dictionary<string, ShaderPreviewTexture> CreateSkinTextures()
    {
        var (vertices, _, _) = MaterialPreviewGeometry.CreateSphere(32, 32);
        int width = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Ceiling(Math.Sqrt(vertices.Length)));
        int height = (int)BitOperations.RoundUpToPowerOf2((uint)((vertices.Length + width - 1) / width));
        var points = FloatTexture(width, height);
        var texels = MemoryMarshal.Cast<byte, NumericsVector4>(points.Pixels.AsSpan());
        for (int i = 0; i < vertices.Length; i++)
        {
            // One bone at index zero with full weight, in the same packing as the Unity component.
            texels[i] = new NumericsVector4(vertices[i] * 0.35f, 1);
        }
        return new Dictionary<string, ShaderPreviewTexture>(StringComparer.Ordinal)
        {
            [BoneTexture] = FloatTexture(1, 4),
            [BoneIndices] = FloatTexture(width, height),
            [BaseVertices] = points
        };

        static ShaderPreviewTexture FloatTexture(int width, int height) =>
            new(width, height, 1, 2 /* R32G32B32A32_FLOAT */, 16, false, new byte[width * height * 16], 0, 1, 1, 0);
    }

    internal static void UpdateBoneTexture(ShaderPreviewTexture texture, float time)
    {
        var model = NumericsMatrix.CreateScale(DeformationAtTime(time + 0.42f)) *
            NumericsMatrix.CreateTranslation(0.5f, 0.5f, 0.5f);
        var rows = NumericsMatrix.Transpose(model);
        MemoryMarshal.Write(texture.Pixels.AsSpan(), in rows);
    }

    internal static ShaderPreviewCamera CameraAtTime(ShaderPreviewCamera camera, float time) =>
        camera with { Yaw = camera.Yaw + 0.21f * MathF.Sin(time * 2.2f), Pitch = camera.Pitch + 0.07f * MathF.Sin(time * 1.7f) };

    internal static MotionPreviewFrame CreateFrame(NumericsMatrix currentView, NumericsMatrix previousView,
        NumericsMatrix projection, float time, float previousTime, float scale, bool objectMotion, bool deformed)
    {
        var viewProjection = currentView * projection;
        if (!NumericsMatrix.Invert(viewProjection, out var inverse))
        {
            throw new InvalidOperationException("The motion preview camera matrix is singular.");
        }
        // Video texture UVs run down the viewport, while the projected camera axis runs up.
        var flipY = NumericsMatrix.CreateScale(1, -1, 1);
        var reprojection = flipY * inverse * previousView * projection * flipY;
        var model = objectMotion ? ModelAtTime(time) : NumericsMatrix.Identity;
        var previousModel = objectMotion ? ModelAtTime(previousTime) : NumericsMatrix.Identity;
        return new MotionPreviewFrame(model, previousModel * previousView * projection, reprojection,
            deformed ? DeformationAtTime(time) : NumericsVector3.One,
            deformed ? DeformationAtTime(previousTime) : NumericsVector3.One, scale);
    }

    private static NumericsMatrix ModelAtTime(float time) =>
        NumericsMatrix.CreateTranslation(0.26f * MathF.Sin(time * 2.7f), 0.08f * MathF.Sin(time * 2), 0);

    private static NumericsVector3 DeformationAtTime(float time)
    {
        float amount = 0.14f * MathF.Sin(time * 3.4f);
        return new NumericsVector3(1 + amount, 1 - amount, 1);
    }
}
